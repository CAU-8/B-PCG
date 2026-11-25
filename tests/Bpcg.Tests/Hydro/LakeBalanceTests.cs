using System;
using System.Linq;
using Bpcg.Core;
using Bpcg.Hydro;
using Xunit;

namespace Bpcg.Tests.Hydro;

/// <summary>호수 물수지 (docs/pipeline.md 8.1): 들어오는 물과 수면의 증발을 견줘 넘치는 호수와 넘치지 못하는 호수를 가릅니다.</summary>
public sealed class LakeBalanceTests
{
    private const int N = 21;
    private const double Dx = 1000.0;   // 칸 넓이 10⁶ m²
    private const int Centre = (10 * N) + 10;

    /// <summary>
    /// 가운데가 가장 낮은 깔때기 땅 (가운데 10 m, 한 칸 멀어질 때마다 1 m 높음). 출구는 왼쪽 위 모서리 칸 하나(0 m)입니다.
    /// 물은 모서리 옆 대각선 칸(약 22.7 m)을 넘어야 나가므로, 그보다 낮은 칸이 모두 한 분지입니다.
    /// 모서리와 맞닿은 두 칸(약 23.5 m)만 분지 밖이라, 분지로 모이는 물은 441 − 1 − 2 = 438칸의 유출입니다.
    /// </summary>
    private static (CellGraph Graph, double[] Z, bool[] Outlet) Funnel()
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx);
        double[] z = new double[N * N];
        bool[] outlet = new bool[N * N];
        for (int c = 0; c < z.Length; c++)
        {
            double dr = (c / N) - 10;
            double dc = (c % N) - 10;
            z[c] = 10.0 + Math.Sqrt((dr * dr) + (dc * dc));
        }
        z[0] = 0.0;
        outlet[0] = true;
        return (g, z, outlet);
    }

    private static double[] Fill(double v) => Enumerable.Repeat(v, N * N).ToArray();

    [Fact]
    public void AWetBasinOverflowsAndNothingChanges()
    {
        (CellGraph g, double[] z, bool[] outlet) = Funnel();
        // 유출 0.5 m/yr, 강수 1.0, 잠재 증발산 0.6: 물에 잠긴 칸은 땅일 때보다 0.6 − (1.0 − 0.5) = 0.1 m/yr 더 증발합니다.
        // 들어오는 물 438 × 5 × 10⁵ = 2.19 × 10⁸ m³/yr 는 분지를 다 잠그는 데 드는 약 4.4 × 10⁷ 보다 많아 넘칩니다.
        LakeBalanceResult r = LakeBalance.Solve(g, z, outlet, Fill(0.5), Fill(1.0), Fill(0.6), null, 1e-3);
        Assert.Equal(1, r.NOpen);
        Assert.Equal(0, r.NClosed);
        Assert.Equal(0, r.NDry);
        Assert.DoesNotContain(true, r.Closed);
        Assert.DoesNotContain(true, r.Sink);
        Assert.All(r.Level, v => Assert.True(double.IsNaN(v)));
    }

    [Fact]
    public void ADryClimateLeavesALakeThatStopsWhereTheWaterRunsOut()
    {
        (CellGraph g, double[] z, bool[] outlet) = Funnel();
        // 유출 0.1 m/yr, 강수 0.5, 잠재 증발산 1.5: 잠긴 칸마다 1.5 − (0.5 − 0.1) = 1.1 m/yr, 곧 1.1 × 10⁶ m³/yr 가 더 듭니다.
        // 들어오는 물은 438 × 10⁵ = 4.38 × 10⁷ m³/yr 라 39칸(4.29 × 10⁷)까지 잠그고 40번째 칸에서 모자랍니다.
        LakeBalanceResult r = LakeBalance.Solve(g, z, outlet, Fill(0.1), Fill(0.5), Fill(1.5), null, 1e-3);
        Assert.Equal(1, r.NClosed);
        Assert.Equal(0, r.NDry);
        Assert.Equal(39, r.Sink.Count(b => b));
        // 낮은 칸부터 잠급니다: 가운데에서 거리² 가 10 이하인 37칸과, 거리² 13 인 8칸 가운데 번호가 작은 2칸.
        // 39번째와 40번째 칸의 바닥이 같은 높이(10 + √13)라 수면이 그 높이이고, 그 두 칸은 물 깊이가 0 이라 호수로 세지 않습니다.
        double h = 10.0 + Math.Sqrt(13.0);
        int[] closed = Enumerable.Range(0, N * N).Where(c => r.Closed[c]).ToArray();
        Assert.Equal(37, closed.Length);
        Assert.Contains(Centre, closed);
        Assert.All(closed, c => Assert.True(z[c] < h));
        Assert.All(closed, c => Assert.Equal(h, r.Level[c], 9));
        Assert.All(Enumerable.Range(0, N * N).Where(c => !r.Closed[c]), c => Assert.True(double.IsNaN(r.Level[c])));
        // 물길이 끝나는 칸은 모두 호수 칸이거나 그 물가(수면과 같은 높이)의 칸입니다.
        Assert.All(Enumerable.Range(0, N * N).Where(c => r.Sink[c]), c => Assert.True(z[c] <= h + 1e-12));
        // 같은 입력이면 같은 결과입니다.
        LakeBalanceResult again = LakeBalance.Solve(g, z, outlet, Fill(0.1), Fill(0.5), Fill(1.5), null, 1e-3);
        Assert.Equal(r.Closed, again.Closed);
        Assert.Equal(r.Level, again.Level);
    }

    [Fact]
    public void MoreInflowMakesTheClosedLakeBigger()
    {
        (CellGraph g, double[] z, bool[] outlet) = Funnel();
        LakeBalanceResult small = LakeBalance.Solve(g, z, outlet, Fill(0.1), Fill(0.5), Fill(1.5), null, 1e-3);
        // 바깥에서 가운데 칸으로 한 해 2.2 × 10⁷ m³ 가 더 들어오면 20칸을 더 잠급니다 (39 → 59칸).
        double[] inflow = new double[N * N];
        inflow[Centre] = 2.2e7;
        LakeBalanceResult big = LakeBalance.Solve(g, z, outlet, Fill(0.1), Fill(0.5), Fill(1.5), inflow, 1e-3);
        Assert.Equal(59, big.Sink.Count(b => b));
        Assert.True(big.Closed.Count(b => b) > small.Closed.Count(b => b));
        Assert.True(big.Level[Centre] > small.Level[Centre]);
    }

    [Fact]
    public void ADesertBasinStaysDryAndTheRiverEndsAtItsFloor()
    {
        (CellGraph g, double[] z, bool[] outlet) = Funnel();
        // 유출 0.001 m/yr: 들어오는 물 4.38 × 10⁵ m³/yr 가 가장 낮은 칸 하나를 잠그는 데 드는 1.8 × 10⁶ 에 못 미칩니다.
        LakeBalanceResult r = LakeBalance.Solve(g, z, outlet, Fill(0.001), Fill(0.2), Fill(2.0), null, 1e-3);
        Assert.Equal(0, r.NClosed);
        Assert.Equal(1, r.NDry);
        Assert.DoesNotContain(true, r.Closed);
        Assert.Equal([Centre], Enumerable.Range(0, N * N).Where(c => r.Sink[c]));
    }

    [Fact]
    public void WrongLengthsAndValuesAreRejected()
    {
        (CellGraph g, double[] z, bool[] outlet) = Funnel();
        ArgumentException e = Assert.Throws<ArgumentException>(
            () => LakeBalance.Solve(g, z, outlet, new double[4], Fill(1.0), Fill(1.0), null, 1e-3));
        Assert.Contains("4", e.Message, StringComparison.Ordinal);
        double[] bad = Fill(1.0);
        bad[7] = double.NaN;
        e = Assert.Throws<ArgumentException>(() => LakeBalance.Solve(g, z, outlet, Fill(0.1), Fill(1.0), bad, null, 1e-3));
        Assert.Contains("칸 7", e.Message, StringComparison.Ordinal);
    }
}
