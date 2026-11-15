using System;
using System.Linq;
using Bpcg.Core;
using Bpcg.Hydro;
using Xunit;

namespace Bpcg.Tests.Hydro;

/// <summary>웅덩이를 흙으로 메운 지표 (docs/pipeline.md 6장): 넘치는 곳에서 멀어질수록 조금씩 높아지는 평지.</summary>
public sealed class FillAlluvialTests
{
    private const int N = 21;
    private const double Dx = 25.0;

    /// <summary>서쪽으로 100 m 에 2 m 씩 낮아지는 비탈. 출구는 서쪽 끝 열입니다. 가운데 행의 열 8~12 를 4 m 파서 웅덩이를 만듭니다.</summary>
    private static (CellGraph Graph, double[] Z, bool[] Outlet, int[] Pit) Slope()
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx);
        double[] z = Enumerable.Range(0, N * N).Select(c => 50.0 + (0.02 * g.Pos[c * 3])).ToArray();
        bool[] outlet = Enumerable.Range(0, N * N).Select(c => c % N == 0).ToArray();
        int[] pit = Enumerable.Range(8, 5).Select(col => ((N / 2) * N) + col).ToArray();
        foreach (int c in pit)
        {
            z[c] -= 4.0;
        }
        return (g, z, outlet, pit);
    }

    [Fact]
    public void PitBecomesAGentlyRisingFlatAndOtherCellsStay()
    {
        (CellGraph g, double[] z, bool[] outlet, int[] pit) = Slope();
        const double slope = 0.004;
        (double[] fill, bool[] raised) = Depressions.FillAlluvial(z, g.Nbr, g.Dist, outlet, slope);
        // 웅덩이 밖은 그대로이고, 메운 칸은 원래보다 높습니다.
        for (int c = 0; c < z.Length; c++)
        {
            Assert.Equal(raised[c], fill[c] > z[c]);
            Assert.True(raised[c] || fill[c] == z[c]);
        }
        Assert.Equal(pit, Enumerable.Range(0, z.Length).Where(c => raised[c]));
        // 웅덩이는 서쪽 끝 칸(열 8)에서 넘칩니다. 그 칸은 넘치는 높이보다 한 걸음(이웃까지 25 m 나 대각선 35.4 m × 0.004) 높고,
        // 동쪽으로 한 칸마다 0.004 × 25 m = 0.1 m 씩 높아집니다.
        double spill = Depressions.FillDepressions(z, g.Nbr, outlet)[pit[0]];
        Assert.InRange(fill[pit[0]] - spill, (slope * Dx) - 1e-9, (slope * Dx * Math.Sqrt(2.0)) + 1e-9);
        for (int k = 1; k < pit.Length; k++)
        {
            Assert.Equal(slope * Dx, fill[pit[k]] - fill[pit[k - 1]], 9);
        }
        // 메운 뒤에는 물이 고이는 칸이 없고, 출구가 아닌 칸마다 자기보다 낮은 이웃이 있습니다.
        double[] after = Depressions.FillDepressions(fill, g.Nbr, outlet);
        Assert.Equal(fill, after);
        AssertEveryCellHasALowerNeighbour(g, fill, outlet);
    }

    private static void AssertEveryCellHasALowerNeighbour(CellGraph g, double[] z, bool[] outlet)
    {
        for (int c = 0; c < z.Length; c++)
        {
            if (outlet[c])
            {
                continue;
            }
            bool lower = false;
            for (int s = 0; s < CellGraph.NSlots; s++)
            {
                int k = g.Nbr[(c * CellGraph.NSlots) + s];
                lower |= k >= 0 && z[k] < z[c];
            }
            Assert.True(lower, $"칸 {c} 에 더 낮은 이웃이 없습니다");
        }
    }

    [Fact]
    public void ChannelCellsAreFilledLowerThanTheCellsBesideThem()
    {
        // 세 줄 폭의 웅덩이(행 9~11, 열 8~12). 가운데 줄이 물길이고, 양옆 줄은 기울기를 두 배로 줍니다.
        (CellGraph g, double[] z, bool[] outlet, _) = Slope();
        int mid = N / 2;
        foreach (int row in new[] { mid - 1, mid + 1 })
        {
            for (int col = 8; col <= 12; col++)
            {
                z[(row * N) + col] -= 4.0;
            }
        }
        double[] cellSlope = Enumerable.Range(0, N * N).Select(c => c / N == mid ? 0.004 : 0.008).ToArray();
        (double[] fill, bool[] raised) = Depressions.FillAlluvial(z, g.Nbr, g.Dist, outlet, cellSlope);
        for (int col = 9; col <= 12; col++)
        {
            int c = (mid * N) + col;
            Assert.True(raised[c] && raised[c - N] && raised[c + N]);
            // 물길 칸은 같은 열의 양옆 칸보다 낮고, 서쪽 물길 칸보다 0.004 × 25 m = 0.1 m 높습니다.
            Assert.True(fill[c] < fill[c - N] && fill[c] < fill[c + N]);
            Assert.Equal(0.1, fill[c] - fill[c - 1], 9);
        }
        AssertEveryCellHasALowerNeighbour(g, fill, outlet);
    }

    [Fact]
    public void ZeroSlopeGivesTheLevelFillAndTerrainWithoutPitsIsUntouched()
    {
        (CellGraph g, double[] z, bool[] outlet, _) = Slope();
        (double[] level, _) = Depressions.FillAlluvial(z, g.Nbr, g.Dist, outlet, 0.0);
        Assert.Equal(Depressions.FillDepressions(z, g.Nbr, outlet), level);
        double[] plain = Enumerable.Range(0, N * N).Select(c => 50.0 + (0.02 * g.Pos[c * 3])).ToArray();
        (double[] same, bool[] raised) = Depressions.FillAlluvial(plain, g.Nbr, g.Dist, outlet, 0.004);
        Assert.Equal(plain, same);
        Assert.DoesNotContain(true, raised);
    }

    [Fact]
    public void WrongInputsAreRejectedWithTheReceivedValues()
    {
        (CellGraph g, double[] z, bool[] outlet, _) = Slope();
        ArgumentException e = Assert.Throws<ArgumentException>(() => Depressions.FillAlluvial(z, g.Nbr, g.Dist, outlet, -0.1));
        Assert.Contains("-0.1", e.Message, StringComparison.Ordinal);
        e = Assert.Throws<ArgumentException>(() => Depressions.FillAlluvial(z, g.Nbr, new double[8], outlet, 0.004));
        Assert.Contains("8", e.Message, StringComparison.Ordinal);
        e = Assert.Throws<ArgumentException>(() => Depressions.FillAlluvial(z, g.Nbr, g.Dist, outlet, new double[5]));
        Assert.Contains("5", e.Message, StringComparison.Ordinal);
        double[] negative = Enumerable.Repeat(-0.25, z.Length).ToArray();
        e = Assert.Throws<ArgumentException>(() => Depressions.FillAlluvial(z, g.Nbr, g.Dist, outlet, negative));
        Assert.Contains("-0.25", e.Message, StringComparison.Ordinal);
    }
}
