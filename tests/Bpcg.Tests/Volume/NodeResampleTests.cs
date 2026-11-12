using System;
using Bpcg.Core;
using Bpcg.Volume;
using Xunit;

namespace Bpcg.Tests.Volume;

/// <summary>흔든 격자점의 높이를 칸 중심 높이로 옮기기 (NodeResample).</summary>
public sealed class NodeResampleTests
{
    private const int N = 12;
    private const double Dx = 25.0;

    private static double Plane(double x, double y) => 100.0 + (0.8 * x) - (0.3 * y);

    private static (double X, double Y) Center(int c) => (((c % N) + 0.5) * Dx, -((c / N) + 0.5) * Dx);

    [Fact]
    public void PlaneSampledAtJitteredNodesComesBackExactAtCellCenters()
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx, jitter: 1.0, seed: 7);
        double[] z = new double[N * N];
        double worstBefore = 0.0;
        for (int c = 0; c < z.Length; c++)
        {
            z[c] = Plane(g.Pos[c * 3], g.Pos[(c * 3) + 1]);
            (double x, double y) = Center(c);
            worstBefore = Math.Max(worstBefore, Math.Abs(z[c] - Plane(x, y)));
        }
        double[] zc = NodeResample.ToCellCenters(g, z);
        for (int c = 0; c < z.Length; c++)
        {
            (double x, double y) = Center(c);
            // 평면이면 최소제곱 기울기가 정확하므로 반올림 오차만 남습니다.
            Assert.True(Math.Abs(zc[c] - Plane(x, y)) < 1e-9, $"칸 {c}: {zc[c]} vs {Plane(x, y)}");
        }
        // 고치기 전의 어긋남은 m 단위입니다 (경사 0.85, 옮긴 거리 최대 반 칸).
        Assert.True(worstBefore > 5.0);
    }

    [Fact]
    public void WithoutJitterNothingChanges()
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx);
        double[] z = new double[N * N];
        for (int c = 0; c < z.Length; c++)
        {
            z[c] = Math.Sin(c * 0.37) * 50.0;
        }
        Assert.Equal(z, NodeResample.ToCellCenters(g, z));
    }

    [Fact]
    public void KeptCellsStayAsTheyAre()
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx, jitter: 1.0, seed: 7);
        double[] z = new double[N * N];
        bool[] keep = new bool[N * N];
        for (int c = 0; c < z.Length; c++)
        {
            z[c] = Plane(g.Pos[c * 3], g.Pos[(c * 3) + 1]);
            keep[c] = c % 5 == 0;
        }
        double[] zc = NodeResample.ToCellCenters(g, z, keep);
        for (int c = 0; c < z.Length; c += 5)
        {
            Assert.Equal(z[c], zc[c]);
        }
    }

    [Fact]
    public void SmoothingHalvesCellScaleBumpsAndLeavesKeptCellsAlone()
    {
        // 매끈한 비탈(동쪽으로 경사 0.4) 위에 칸마다 ±5 m 의 울퉁불퉁함을 얹습니다.
        const int n = 40;
        double[] z = new double[n * n];
        double[] smooth = new double[n * n];
        bool[] keep = new bool[n * n];
        for (int c = 0; c < z.Length; c++)
        {
            smooth[c] = 0.4 * 25.0 * (c % n);
            z[c] = smooth[c] + (10.0 * (Hashing.HashUnit(7, c, 0) - 0.5));
            keep[c] = c % 17 == 0;
        }
        double[] once = NodeResample.SmoothHillslopes(n, n, z, keep, 1);
        double before = 0.0;
        double after = 0.0;
        int count = 0;
        for (int j = 2; j < n - 2; j++)
        {
            for (int i = 2; i < n - 2; i++)
            {
                int c = (j * n) + i;
                if (keep[c])
                {
                    Assert.Equal(z[c], once[c]);
                    continue;
                }
                before += (z[c] - smooth[c]) * (z[c] - smooth[c]);
                after += (once[c] - smooth[c]) * (once[c] - smooth[c]);
                count++;
            }
        }
        // 한 번 섞으면 매끈한 비탈에서 벗어난 크기(RMS)가 절반 아래로 줄어듭니다.
        Assert.True(Math.Sqrt(after / count) < 0.5 * Math.Sqrt(before / count), $"{Math.Sqrt(before / count)} → {Math.Sqrt(after / count)}");
    }

    [Fact]
    public void SmoothingNeverGoesBelowTheLowestNeighbour()
    {
        const int n = 30;
        double[] z = new double[n * n];
        bool[] keep = new bool[n * n];
        for (int c = 0; c < z.Length; c++)
        {
            z[c] = 100.0 * Hashing.HashUnit(11, c, 0);
        }
        double[] twice = NodeResample.SmoothHillslopes(n, n, z, keep, 1);
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double lowest = double.PositiveInfinity;
                for (int dj = -1; dj <= 1; dj++)
                {
                    for (int di = -1; di <= 1; di++)
                    {
                        int jj = j + dj;
                        int ii = i + di;
                        if (jj >= 0 && jj < n && ii >= 0 && ii < n)
                        {
                            lowest = Math.Min(lowest, z[(jj * n) + ii]);
                        }
                    }
                }
                Assert.True(twice[(j * n) + i] >= lowest);
            }
        }
    }

    [Fact]
    public void ZeroPassesReturnTheSameHeights()
    {
        double[] z = [1.0, 5.0, 2.0, 8.0, 3.0, 9.0, 4.0, 7.0, 6.0];
        Assert.Equal(z, NodeResample.SmoothHillslopes(3, 3, z, new bool[9], 0));
        Assert.Throws<ArgumentException>(() => NodeResample.SmoothHillslopes(3, 3, z, new bool[9], -1));
        Assert.Throws<ArgumentException>(() => NodeResample.SmoothHillslopes(3, 3, z, new bool[8], 1));
    }

    [Fact]
    public void SphereGraphIsRejected()
    {
        CellGraph g = Graph.SphereGraph(4, 1000.0);
        Assert.Throws<ArgumentException>(() => NodeResample.ToCellCenters(g, new double[6 * 16]));
    }
}
