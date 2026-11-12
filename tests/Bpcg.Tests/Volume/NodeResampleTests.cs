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
    public void SphereGraphIsRejected()
    {
        CellGraph g = Graph.SphereGraph(4, 1000.0);
        Assert.Throws<ArgumentException>(() => NodeResample.ToCellCenters(g, new double[6 * 16]));
    }
}
