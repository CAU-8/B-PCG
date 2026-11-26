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
        Assert.Throws<ArgumentException>(() => NodeResample.SurfaceToCellCenters(g, new double[6 * 16]));
    }

    // --- 땅속의 면 (SurfaceToCellCenters)

    private static bool Interior(int c) => c % N > 0 && c % N < N - 1 && c / N > 0 && c / N < N - 1;

    [Fact]
    public void BuriedPlaneComesBackExactAtInteriorCellCenters()
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx, jitter: 1.0, seed: 7);
        double[] v = new double[N * N];
        double worstBefore = 0.0;
        for (int c = 0; c < v.Length; c++)
        {
            v[c] = Plane(g.Pos[c * 3], g.Pos[(c * 3) + 1]);
            (double x, double y) = Center(c);
            worstBefore = Math.Max(worstBefore, Math.Abs(v[c] - Plane(x, y)));
        }
        double[] moved = NodeResample.SurfaceToCellCenters(g, v);
        for (int c = 0; c < v.Length; c++)
        {
            if (Interior(c))
            {
                (double x, double y) = Center(c);
                Assert.True(Math.Abs(moved[c] - Plane(x, y)) < 1e-9, $"칸 {c}: {moved[c]} vs {Plane(x, y)}");
            }
        }
        Assert.True(worstBefore > 5.0);
    }

    [Fact]
    public void StepNeighboursDoNotPullACellAcrossTheStep()
    {
        // 거의 평평한 면(동쪽으로 0.002)이 i = 6 에서 200 m 솟습니다 (분수령 밑 석회암 지하수면의 계단).
        // 계단 양쪽 칸은 계단 너머 이웃을 빼고 제 쪽 기울기로만 옮겨, 옮기는 양이 0.002 × 반 칸 정도에 그칩니다.
        CellGraph g = Graph.FlatGraph(N, N, Dx, jitter: 1.0, seed: 3);
        double[] v = new double[N * N];
        for (int c = 0; c < v.Length; c++)
        {
            v[c] = 100.0 + (0.002 * g.Pos[c * 3]) + (c % N >= 6 ? 200.0 : 0.0);
        }
        double[] moved = NodeResample.SurfaceToCellCenters(g, v, maxSlope: 1.5);
        for (int c = 0; c < v.Length; c++)
        {
            Assert.True(Math.Abs(moved[c] - v[c]) < 0.1, $"칸 {c} (i {c % N}): {v[c]} → {moved[c]}");
        }
    }

    [Fact]
    public void NarrowStripBetweenStepsStaysWithTheMaxSlopeGuard()
    {
        // 한 칸 폭의 띠(i = 6)만 200 m 높습니다. 띠의 칸은 이웃 여섯이 건너편이라 계단 거르기만으로는 못 거르고,
        // 구한 기울기가 maxSlope 보다 가팔라 그대로 남습니다.
        CellGraph g = Graph.FlatGraph(N, N, Dx, jitter: 1.0, seed: 5);
        double[] v = new double[N * N];
        for (int c = 0; c < v.Length; c++)
        {
            v[c] = 100.0 + (c % N == 6 ? 200.0 : 0.0);
        }
        double[] moved = NodeResample.SurfaceToCellCenters(g, v, maxSlope: 1.5);
        for (int c = 0; c < v.Length; c++)
        {
            Assert.True(Math.Abs(moved[c] - v[c]) < 1e-9, $"칸 {c} (i {c % N}): {v[c]} → {moved[c]}");
        }
    }

    [Fact]
    public void MissingValuesStayMissingAndAreSkippedAsNeighbours()
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx, jitter: 1.0, seed: 7);
        double[] v = new double[N * N];
        for (int c = 0; c < v.Length; c++)
        {
            v[c] = c % 7 == 3 ? double.NaN : Plane(g.Pos[c * 3], g.Pos[(c * 3) + 1]);
        }
        double[] moved = NodeResample.SurfaceToCellCenters(g, v);
        for (int c = 0; c < v.Length; c++)
        {
            if (double.IsNaN(v[c]))
            {
                Assert.True(double.IsNaN(moved[c]));
            }
            else if (Interior(c))
            {
                (double x, double y) = Center(c);
                Assert.True(Math.Abs(moved[c] - Plane(x, y)) < 1e-9, $"칸 {c}: {moved[c]} vs {Plane(x, y)}");
            }
        }
    }

    [Fact]
    public void KeptCellsAndArgumentsAreChecked()
    {
        CellGraph g = Graph.FlatGraph(N, N, Dx, jitter: 1.0, seed: 7);
        double[] v = new double[N * N];
        bool[] keep = new bool[N * N];
        for (int c = 0; c < v.Length; c++)
        {
            v[c] = Plane(g.Pos[c * 3], g.Pos[(c * 3) + 1]);
            keep[c] = c % 5 == 0;
        }
        double[] moved = NodeResample.SurfaceToCellCenters(g, v, keep);
        for (int c = 0; c < v.Length; c += 5)
        {
            Assert.Equal(v[c], moved[c]);
        }
        Assert.Throws<ArgumentException>(() => NodeResample.SurfaceToCellCenters(g, new double[(N * N) - 1]));
        Assert.Throws<ArgumentException>(() => NodeResample.SurfaceToCellCenters(g, v, new bool[3]));
        Assert.Throws<ArgumentException>(() => NodeResample.SurfaceToCellCenters(g, v, maxSlope: 0.0));
    }

    [Fact]
    public void StrataMoveAsOneBlockAndDoNotMixTemplates()
    {
        // 서쪽 절반과 동쪽 절반이 다른 지질 템플릿입니다 (암석 열도, 층 두께도 다름). 두 쪽 모두 층이 동쪽으로 0.5,
        // 남쪽으로 0.3 기울었고, 동쪽 템플릿은 300 m 높이 놓였습니다. 층 바닥은 다시 읽은 묶음처럼 float32 로 반올림합니다.
        const int nL = 3;
        CellGraph g = Graph.FlatGraph(N, N, Dx, jitter: 1.0, seed: 11);
        double[] depthW = [100.0, 250.0, 600.0];
        double[] depthE = [50.0, 50.0, 400.0];
        double[] bottom = new double[N * N * nL];
        byte[] rock = new byte[N * N * (nL + 1)];
        double Top(double x, double y, bool east) => 1000.0 + (0.5 * x) - (0.3 * y) + (east ? 300.0 : 0.0);
        for (int c = 0; c < N * N; c++)
        {
            bool east = c % N >= N / 2;
            double top = Top(g.Pos[c * 3], g.Pos[(c * 3) + 1], east);
            for (int i = 0; i < nL; i++)
            {
                bottom[(c * nL) + i] = (float)(top - (east ? depthE[i] : depthW[i]));
            }
            for (int i = 0; i <= nL; i++)
            {
                rock[(c * (nL + 1)) + i] = (byte)(east ? 5 - i : i);
            }
        }
        double[] centered = HeroVolume.StrataAtCenters(g, bottom, rock, nL);
        for (int c = 0; c < N * N; c++)
        {
            bool east = c % N >= N / 2;
            // 층 두께는 그대로입니다 (모든 층에 같은 양을 더함).
            for (int i = 1; i < nL; i++)
            {
                double before = bottom[(c * nL) + i] - bottom[c * nL];
                double after = centered[(c * nL) + i] - centered[c * nL];
                Assert.True(Math.Abs(after - before) < 1e-9, $"칸 {c} 층 {i}: 두께 {before} → {after}");
            }
            if (!Interior(c))
            {
                continue;
            }
            // 템플릿 경계 바로 옆 칸도 제 템플릿 이웃만으로 기울기를 구해, 칸 중심의 층 바닥이 맞습니다 (float32 반올림만큼 틀림).
            (double x, double y) = Center(c);
            double want = Top(x, y, east) - (east ? depthE[0] : depthW[0]);
            Assert.True(Math.Abs(centered[c * nL] - want) < 0.01, $"칸 {c} (i {c % N}): {centered[c * nL]} vs {want}");
        }
    }
}
