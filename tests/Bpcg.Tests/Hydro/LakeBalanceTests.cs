using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Hydro;
using Xunit;

namespace Bpcg.Tests.Hydro;

/// <summary>
/// 호수 물수지 (docs/pipeline.md 8.1): 웅덩이마다 들어오는 물과 수면의 증발을 견줘, 넘치는 호수와 넘치지 못하는 호수와 마른 분지를
/// 가립니다. 호수마다 들어온 물 = 증발 + 넘친 물 이 맞아야 합니다.
/// </summary>
public sealed class LakeBalanceTests
{
    private const int N = 21;
    private const double Dx = 1000.0;   // 칸 넓이 10⁶ m²
    private const int Centre = (10 * N) + 10;

    /// <summary>
    /// 가운데가 가장 낮은 깔때기 땅 (가운데 10 m, 한 칸 멀어질 때마다 1 m 높음). 출구는 왼쪽 위 모서리 칸 하나(0 m)입니다.
    /// 물은 모서리 옆 대각선 칸(약 22.7 m)을 넘어야 나가므로, 그보다 낮은 칸이 모두 한 분지이고 바닥은 가운데 칸 하나입니다.
    /// 모서리와 맞닿은 세 칸(대각선 칸 포함)은 물이 모서리로 흘러가, 가운데로 모이는 물은 441 − 1 − 3 = 437칸의 유출입니다.
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

    private static double[] Fill(int n, double v) => Enumerable.Repeat(v, n).ToArray();

    /// <summary>호수마다 들어온 물 = 증발 + 넘친 물 이고, 넘친 물을 받은 호수는 그만큼 이상을 받았습니다.</summary>
    private static void AssertConserved(LakeBalanceResult r)
    {
        foreach (LakeBudget b in r.Lakes)
        {
            double miss = Math.Abs(b.InflowM3PerYr - b.EvaporationM3PerYr - b.OverflowM3PerYr);
            Assert.True(miss <= 1e-9 * Math.Max(b.InflowM3PerYr, 1.0), $"바닥 {b.Pit} 의 호수: 들어옴 {b.InflowM3PerYr}, 증발 {b.EvaporationM3PerYr}, 넘침 {b.OverflowM3PerYr}");
            Assert.True(b.EvaporationM3PerYr >= 0.0 && b.OverflowM3PerYr >= 0.0);
            if (b.State == LakeState.Open)
            {
                Assert.True(b.Target >= -1 && b.Target < r.Lakes.Count);
                if (b.Target >= 0)
                {
                    Assert.True(r.Lakes[b.Target].InflowM3PerYr >= b.OverflowM3PerYr * (1.0 - 1e-12));
                }
            }
            else
            {
                Assert.Equal(-2, b.Target);
                Assert.Equal(0.0, b.OverflowM3PerYr);
            }
        }
        Assert.True(LakeBalance.Residual(r) <= 1e-9);
    }

    [Fact]
    public void AWetBasinOverflowsAndNothingChanges()
    {
        (CellGraph g, double[] z, bool[] outlet) = Funnel();
        // 유출 0.5 m/yr, 강수 1.0, 잠재 증발산 0.6: 물에 잠긴 칸은 땅일 때보다 0.6 − (1.0 − 0.5) = 0.1 m/yr 더 증발합니다.
        // 들어오는 물 437 × 5 × 10⁵ ≈ 2.2 × 10⁸ m³/yr 는 분지를 다 잠그는 데 드는 약 4 × 10⁷ 보다 많아 넘칩니다.
        LakeBalanceResult r = LakeBalance.Solve(g, z, outlet, Fill(0.5), Fill(1.0), Fill(0.6), null, 1e-3);
        Assert.Equal(1, r.NOpen);
        Assert.Equal(0, r.NClosed);
        Assert.Equal(0, r.NDry);
        Assert.DoesNotContain(true, r.Closed);
        Assert.DoesNotContain(true, r.Sink);
        Assert.All(r.Level, v => Assert.True(double.IsNaN(v)));
        LakeBudget lake = Assert.Single(r.Lakes);
        Assert.Equal(LakeState.Open, lake.State);
        Assert.Equal(-1, lake.Target);
        Assert.Equal(437 * 5.0e5, lake.InflowM3PerYr, 6);
        // 넘치는 호수는 넘칠 높이(대각선 칸, 10 + √162 m)까지 찹니다. 그보다 낮은 칸만 잠기고, 증발을 뺀 물이 바다로 갑니다.
        double spill = 10.0 + Math.Sqrt(162.0);
        Assert.Equal(spill, lake.Level, 9);
        int below = z.Count(v => v < spill - LakeBalance.PondedM) - 1;  // 출구 칸 빼기
        Assert.Equal(below, lake.Cells);
        Assert.Equal(below * 1.0e5, lake.EvaporationM3PerYr, 6);
        AssertConserved(r);
    }

    [Fact]
    public void ADryClimateLeavesALakeThatStopsWhereTheWaterRunsOut()
    {
        (CellGraph g, double[] z, bool[] outlet) = Funnel();
        // 유출 0.1 m/yr, 강수 0.5, 잠재 증발산 1.5: 잠긴 칸마다 1.5 − (0.5 − 0.1) = 1.1 m/yr, 곧 1.1 × 10⁶ m³/yr 가 더 듭니다.
        // 들어오는 물은 437 × 10⁵ = 4.37 × 10⁷ m³/yr 라 39칸(4.29 × 10⁷)까지 잠그고 40번째 칸에서 모자랍니다.
        LakeBalanceResult r = LakeBalance.Solve(g, z, outlet, Fill(0.1), Fill(0.5), Fill(1.5), null, 1e-3);
        Assert.Equal(1, r.NClosed);
        Assert.Equal(0, r.NDry);
        // 낮은 칸부터 잠급니다: 가운데에서 거리² 가 10 이하인 37칸과, 거리² 13 인 8칸 가운데 번호가 작은 2칸.
        // 39번째와 40번째 칸의 바닥이 같은 높이(10 + √13)라 수면이 그 높이이고, 그 두 칸은 물 깊이가 0 이라 호수가 아닙니다.
        // 그 두 칸은 물가라서 물길도 끝나지 않고 호수로 흘러듭니다.
        double h = 10.0 + Math.Sqrt(13.0);
        int[] closed = Enumerable.Range(0, N * N).Where(c => r.Closed[c]).ToArray();
        Assert.Equal(37, closed.Length);
        Assert.Contains(Centre, closed);
        Assert.Equal(r.Closed, r.Sink);
        Assert.All(closed, c => Assert.True(z[c] < h));
        Assert.All(closed, c => Assert.Equal(h, r.Level[c], 9));
        Assert.All(Enumerable.Range(0, N * N).Where(c => !r.Closed[c]), c => Assert.True(double.IsNaN(r.Level[c])));
        // 들어온 물이 모두 증발합니다: 잠근 39칸 4.29 × 10⁷ 와 40번째 칸을 잠그다 만 8 × 10⁵.
        LakeBudget lake = Assert.Single(r.Lakes);
        Assert.Equal(LakeState.Closed, lake.State);
        Assert.Equal(37, lake.Cells);
        Assert.Equal(4.37e7, lake.InflowM3PerYr, 6);
        Assert.Equal(4.37e7, lake.EvaporationM3PerYr, 6);
        AssertConserved(r);
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
        // 59칸 가운데 거리² 18 인 마지막 두 칸은 다음 칸과 바닥이 같아 물 깊이가 0 이라, 호수 칸은 57칸입니다.
        double[] inflow = new double[N * N];
        inflow[Centre] = 2.2e7;
        LakeBalanceResult big = LakeBalance.Solve(g, z, outlet, Fill(0.1), Fill(0.5), Fill(1.5), inflow, 1e-3);
        Assert.Equal(57, big.Closed.Count(b => b));
        Assert.True(big.Closed.Count(b => b) > small.Closed.Count(b => b));
        Assert.True(big.Level[Centre] > small.Level[Centre]);
        Assert.Equal(4.37e7 + 2.2e7, Assert.Single(big.Lakes).InflowM3PerYr, 6);
        AssertConserved(big);
    }

    [Fact]
    public void ADesertBasinStaysDryAndTheRiverEndsAtItsFloor()
    {
        (CellGraph g, double[] z, bool[] outlet) = Funnel();
        // 유출 0.001 m/yr: 들어오는 물 4.37 × 10⁵ m³/yr 가 가장 낮은 칸 하나를 잠그는 데 드는 1.8 × 10⁶ 에 못 미칩니다.
        LakeBalanceResult r = LakeBalance.Solve(g, z, outlet, Fill(0.001), Fill(0.2), Fill(2.0), null, 1e-3);
        Assert.Equal(0, r.NClosed);
        Assert.Equal(1, r.NDry);
        Assert.Equal(0, r.NShallow);
        Assert.DoesNotContain(true, r.Closed);
        Assert.Equal([Centre], Enumerable.Range(0, N * N).Where(c => r.Sink[c]));
        LakeBudget lake = Assert.Single(r.Lakes);
        Assert.Equal(LakeState.Dry, lake.State);
        Assert.Equal(4.37e5, lake.EvaporationM3PerYr, 6);
        AssertConserved(r);
    }

    /// <summary>
    /// 칸 13개가 한 줄로 놓인 땅 (칸 넓이 10⁶ m²). 왼쪽 끝 칸 0 이 출구(0 m)이고, 칸 1 (50 m)이 분지의 턱입니다.
    /// 분지 안에는 바닥이 둘 있습니다: 큰 바닥 칸 5 (10 m)와 작은 바닥 칸 7 (15 m). 둘 사이에 칸 6 (25 m)이 턱입니다.
    /// 칸 2~6 의 물은 칸 5 로, 칸 7~12 의 물은 칸 7 로 모입니다. 유출은 0 이고, 물은 inflow 로 칸 3 과 칸 9 에 넣습니다.
    /// 강수 0.5, 잠재 증발산 1.5 m/yr 라 잠긴 칸마다 한 해 10⁶ m³ 가 더 듭니다.
    /// </summary>
    private static LakeBalanceResult TwoBowls(double intoMain, double intoSide, double minDepthM = 0.0)
    {
        double[] z = [0, 50, 40, 30, 20, 10, 25, 15, 18, 35, 45, 55, 60];
        int n = z.Length;
        CellGraph g = Graph.FlatGraph(1, n, Dx);
        bool[] outlet = new bool[n];
        outlet[0] = true;
        double[] inflow = new double[n];
        inflow[3] = intoMain;
        inflow[9] = intoSide;
        return LakeBalance.Solve(g, z, outlet, Fill(n, 0.0), Fill(n, 0.5), Fill(n, 1.5), inflow, 1e-3, minDepthM);
    }

    [Fact]
    public void ADrySideBowlKeepsItsOwnWaterAndTheMainLakeUsesOnlyItsOwn()
    {
        // 작은 바닥에는 한 해 9 × 10⁵ m³ 가 들어와 바닥 칸 하나(10⁶)도 잠그지 못하므로 마른 분지입니다. 그 물은 큰 바닥으로 가지 않습니다.
        // 큰 바닥은 자기 물 1.5 × 10⁶ 으로 칸 5 를 잠그고, 남은 5 × 10⁵ 로 다음 칸(20 m)까지의 절반을 올려 수면이 15 m 입니다.
        // (분지 전체를 한 수면으로 잠그면 두 바닥의 물 2.4 × 10⁶ 으로 칸 5 와 칸 7 을 잠가, 물이 없는 작은 바닥에 호수가 생깁니다.)
        LakeBalanceResult r = TwoBowls(1.5e6, 0.9e6);
        Assert.Equal(1, r.NClosed);
        Assert.Equal(1, r.NDry);
        Assert.Equal(0, r.NOpen);
        Assert.Equal([5], Enumerable.Range(0, 13).Where(c => r.Closed[c]));
        Assert.Equal(15.0, r.Level[5], 9);
        Assert.Equal([5, 7], Enumerable.Range(0, 13).Where(c => r.Sink[c]));
        Assert.Equal(2, r.Lakes.Count);
        Assert.Equal((5, LakeState.Closed), (r.Lakes[0].Pit, r.Lakes[0].State));
        Assert.Equal(1.5e6, r.Lakes[0].EvaporationM3PerYr, 6);
        Assert.Equal((7, LakeState.Dry), (r.Lakes[1].Pit, r.Lakes[1].State));
        Assert.Equal(0.9e6, r.Lakes[1].EvaporationM3PerYr, 6);
        AssertConserved(r);
        // 물 깊이 문턱 6 m 를 주면, 가장 깊은 곳이 5 m 인 큰 바닥의 호수도 마른 분지로 셉니다. 물길은 그 바닥에서 끝납니다.
        LakeBalanceResult strict = TwoBowls(1.5e6, 0.9e6, minDepthM: 6.0);
        Assert.Equal(0, strict.NClosed);
        Assert.Equal(2, strict.NDry);
        Assert.Equal(1, strict.NShallow);
        Assert.DoesNotContain(true, strict.Closed);
        Assert.Equal([5, 7], Enumerable.Range(0, 13).Where(c => strict.Sink[c]));
        AssertConserved(strict);
    }

    [Fact]
    public void AFullSideBowlPassesOnOnlyTheWaterItDoesNotEvaporate()
    {
        // 작은 바닥에 3.5 × 10⁶ 이 들어와 칸 7·8 을 잠그고(2 × 10⁶) 턱(칸 6, 25 m)까지 찹니다. 턱 칸은 물 깊이가 0 이라 호수가
        // 아니므로, 남은 1.5 × 10⁶ 이 턱을 넘어 큰 바닥으로 갑니다 (증발한 2 × 10⁶ 은 빼고).
        // 큰 바닥은 자기 물 5 × 10⁵ 와 넘어온 1.5 × 10⁶ 으로 칸 5 와 칸 4 를 잠그고 수면 20 m 에서 멈춥니다.
        LakeBalanceResult r = TwoBowls(0.5e6, 3.5e6);
        Assert.Equal(1, r.NOpen);
        Assert.Equal(1, r.NClosed);
        Assert.Equal(1, r.NSpills);
        Assert.Equal([5], Enumerable.Range(0, 13).Where(c => r.Closed[c]));
        Assert.Equal(20.0, r.Level[5], 9);
        // 넘치는 작은 호수의 칸은 물길이 끝나는 칸이 아닙니다. 물은 턱을 넘어 큰 호수로 흘러갑니다.
        Assert.Equal([5], Enumerable.Range(0, 13).Where(c => r.Sink[c]));
        LakeBudget main = r.Lakes[0];
        LakeBudget side = r.Lakes[1];
        Assert.Equal((5, LakeState.Closed), (main.Pit, main.State));
        Assert.Equal((7, LakeState.Open, 0), (side.Pit, side.State, side.Target));
        Assert.Equal(2, side.Cells);
        Assert.Equal(25.0, side.Level, 9);
        Assert.Equal(2.0e6, side.EvaporationM3PerYr, 6);
        Assert.Equal(1.5e6, side.OverflowM3PerYr, 6);
        Assert.Equal(2.0e6, main.InflowM3PerYr, 6);
        AssertConserved(r);
    }

    [Fact]
    public void TwoBowlsThatMeetAtTheSillBecomeOneLake()
    {
        // 큰 바닥에 1.7 × 10⁶, 작은 바닥에 3.5 × 10⁶. 작은 호수가 턱(25 m)에서 넘친 1.5 × 10⁶ 을 받은 큰 호수가 턱까지 차오르면
        // 두 수면이 만나 한 호수가 됩니다. 남은 2 × 10⁵ 가 다음 칸(30 m)을 5분의 1 만큼 올려 수면은 26 m 입니다.
        LakeBalanceResult r = TwoBowls(1.7e6, 3.5e6);
        Assert.Equal(1, r.NMerges);
        Assert.Equal(1, r.NClosed);
        Assert.Equal(0, r.NOpen);
        Assert.Equal([4, 5, 6, 7, 8], Enumerable.Range(0, 13).Where(c => r.Closed[c]));
        Assert.All(new[] { 4, 5, 6, 7, 8 }, c => Assert.Equal(26.0, r.Level[c], 9));
        LakeBudget lake = Assert.Single(r.Lakes);
        Assert.Equal(5.2e6, lake.InflowM3PerYr, 6);
        Assert.Equal(5.2e6, lake.EvaporationM3PerYr, 6);
        AssertConserved(r);
    }

    [Fact]
    public void AShoreCellLeftBySpillingLakeJoinsTheTwoLakesInsteadOfLoopingWater()
    {
        // 칸 10개가 한 줄로 놓인 땅: 출구, 벽, A 바닥(1 m), A 비탈(5 m), A 물가(9.9996 m), 턱(10 m), B 비탈(5 m), B 바닥(1 m),
        // B 비탈(20 m), 벽. 잠긴 칸마다 한 해 10⁶ m³ 가 듭니다.
        // A 는 자기 물 4.5 × 10⁶ 으로 턱까지 차서 넘칩니다. 물 깊이 0.4 mm 인 A 물가 칸과 턱 칸은 호수가 아니라서 그 물을 돌려받아
        // 2.5 × 10⁶ 을 B 로 넘깁니다. B 는 자기 물 10⁶ 과 합쳐 턱까지 차오르고, 턱 너머 A 물가 칸에 닿습니다.
        // 그 칸의 물은 A 로 흐르지만 A 의 수면(10 m)이 B 의 수면과 같으므로, B 가 A 로 넘치는 것이 아니라 두 호수가 하나가 됩니다.
        double[] z = [0, 100, 1, 5, 9.9996, 10, 5, 1, 20, 100];
        int n = z.Length;
        CellGraph g = Graph.FlatGraph(1, n, Dx);
        bool[] outlet = new bool[n];
        outlet[0] = true;
        double[] inflow = new double[n];
        inflow[3] = 4.5e6;
        inflow[8] = 1.0e6;
        LakeBalanceResult r = LakeBalance.Solve(g, z, outlet, Fill(n, 0.0), Fill(n, 0.5), Fill(n, 1.5), inflow, 1e-3);
        Assert.Equal(1, r.NSpills);
        Assert.Equal(1, r.NMerges);
        Assert.Equal(1, r.NClosed);
        Assert.Equal([2, 3, 6, 7], Enumerable.Range(0, n).Where(c => r.Closed[c]));
        Assert.All(new[] { 2, 3, 6, 7 }, c => Assert.Equal(10.0, r.Level[c], 9));
        LakeBudget lake = Assert.Single(r.Lakes);
        Assert.Equal(5.5e6, lake.InflowM3PerYr, 6);
        AssertConserved(r);
    }

    /// <summary>
    /// 웅덩이가 여럿인 땅 (40 × 40 칸, 칸 넓이 10⁶ m²). 서쪽 끝 열이 출구이고, 서쪽으로 내려가는 비탈에 물결 모양 굴곡이 있어
    /// 웅덩이가 여럿 생깁니다. 남쪽은 메마르고(유출 0.02 m/yr) 북쪽은 축축합니다(0.3 m/yr).
    /// </summary>
    private static (LakeBalanceResult R, double[] Z, double[] Source) ManyBasins()
    {
        const int n = 40;
        CellGraph g = Graph.FlatGraph(n, n, Dx);
        double[] z = new double[n * n];
        bool[] outlet = new bool[n * n];
        double[] runoff = new double[n * n];
        double[] source = new double[n * n];
        for (int c = 0; c < z.Length; c++)
        {
            int j = c / n;
            int i = c % n;
            outlet[c] = i == 0;
            z[c] = i == 0 ? 0.0 : 20.0 + (2.0 * i) + (25.0 * Math.Sin(0.55 * i) * Math.Sin(0.45 * j)) + (8.0 * Math.Cos(0.9 * (i + j)));
            runoff[c] = 0.02 + (0.28 * j / (n - 1.0));
            source[c] = outlet[c] ? 0.0 : runoff[c] * Dx * Dx;
        }
        LakeBalanceResult r = LakeBalance.Solve(g, z, outlet, runoff, Fill(n * n, 0.6), Fill(n * n, 1.4), null, 1e-3);
        return (r, z, source);
    }

    [Fact]
    public void WaterIsConservedInEveryLakeOfManyBasins()
    {
        (LakeBalanceResult r, double[] z, double[] source) = ManyBasins();
        // 넘치는 호수, 넘치지 못하는 호수, 마른 분지가 모두 있어야 이 시험이 뜻이 있습니다.
        Assert.True(r.NOpen > 0 && r.NClosed > 0 && r.NDry > 0, $"넘치는 {r.NOpen}, 넘치지 못하는 {r.NClosed}, 마른 {r.NDry}");
        Assert.True(r.NSpills > 0);
        Assert.Equal(r.NOpen + r.NClosed + r.NDry, r.Lakes.Count(b => b.State != LakeState.Open || b.Cells > 0));
        AssertConserved(r);
        // 땅에서 호수로 곧장 들어온 물 = 모든 호수가 받은 물 − 다른 호수가 넘겨 준 물. 이 물은 땅 전체의 유출보다 많을 수 없습니다.
        double direct = r.Lakes.Sum(b => b.InflowM3PerYr) - r.Lakes.Where(b => b.Target >= 0).Sum(b => b.OverflowM3PerYr);
        Assert.True(direct <= source.Sum() * (1.0 + 1e-12) && direct > 0.0);
        // 넘치지 못하는 호수 칸은 수면 아래에 있고, 모두 물길이 끝나는 칸입니다. 마른 분지마다 물길이 끝나는 칸이 하나 있습니다.
        for (int c = 0; c < z.Length; c++)
        {
            Assert.True(!r.Closed[c] || (r.Sink[c] && r.Level[c] - z[c] > LakeBalance.PondedM));
        }
        Assert.Equal(r.NDry, Enumerable.Range(0, z.Length).Count(c => r.Sink[c] && !r.Closed[c]));
        // 같은 입력이면 같은 결과입니다.
        (LakeBalanceResult again, _, _) = ManyBasins();
        Assert.Equal(r.Level, again.Level);
        Assert.Equal(r.Lakes, again.Lakes);
    }

    [Fact]
    public void ManyTerrainsWithNearlyFlatShoresKeepEveryLakeBalanced()
    {
        // 물결 모양 땅 64가지. 높이를 0.4 mm 단위로 반올림해, 넘칠 높이와 1 mm 안쪽으로 차이 나는 물가 칸이 많이 생기게 합니다
        // (작은 웅덩이를 1 mm 씩 높여 메운 행성 지도에서처럼). 넘친 호수의 물가 칸에 이웃 호수가 닿아도 물이 두 호수 사이를
        // 돌지 않고, 호수마다 물수지가 맞아야 합니다.
        const int n = 24;
        CellGraph g = Graph.FlatGraph(n, n, Dx);
        int[] states = new int[3];
        for (int variant = 0; variant < 64; variant++)
        {
            double a = 0.3 + (0.05 * (variant % 8));
            double b = 0.25 + (0.04 * (variant / 8));
            double[] z = new double[n * n];
            bool[] outlet = new bool[n * n];
            double[] runoff = new double[n * n];
            for (int c = 0; c < z.Length; c++)
            {
                int j = c / n;
                int i = c % n;
                outlet[c] = i == 0 || j == 0;
                double raw = 10.0 + (0.5 * i) + (6.0 * Math.Sin(a * i) * Math.Cos(b * j)) + (3.0 * Math.Sin((b * i) + (a * j)));
                z[c] = outlet[c] ? 0.0 : Math.Round(raw / 4e-4) * 4e-4;
                runoff[c] = 0.01 + (0.15 * ((i + j + variant) % 7) / 6.0);
            }
            LakeBalanceResult r = LakeBalance.Solve(g, z, outlet, runoff, Fill(n * n, 0.6), Fill(n * n, 1.5), null, 1e-3, minDepthM: 0.5);
            AssertConserved(r);
            Assert.All(Enumerable.Range(0, z.Length), c => Assert.True(!r.Closed[c] || r.Level[c] - z[c] > LakeBalance.PondedM));
            states[0] += r.NOpen;
            states[1] += r.NClosed;
            states[2] += r.NDry;
        }
        Assert.All(states, v => Assert.True(v > 0, $"넘치는 {states[0]}, 넘치지 못하는 {states[1]}, 마른 {states[2]}"));
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
        e = Assert.Throws<ArgumentException>(
            () => LakeBalance.Solve(g, z, outlet, Fill(0.1), Fill(1.0), Fill(1.0), null, 1e-3, minDepthM: -2.0));
        Assert.Contains("-2", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResidualIsZeroWithoutLakesAndMeasuresTheWorstLake()
    {
        var empty = new LakeBalanceResult([], [], [], 0, 0, 0, 0, 0, 0, 0, new List<LakeBudget>());
        Assert.Equal(0.0, LakeBalance.Residual(empty));
        var off = new LakeBalanceResult([], [], [], 0, 1, 0, 0, 1, 0, 0, [new LakeBudget(3, 1, 10.0, 100.0, 90.0, 0.0, -2, LakeState.Closed)]);
        Assert.Equal(0.1, LakeBalance.Residual(off), 12);
    }
}
