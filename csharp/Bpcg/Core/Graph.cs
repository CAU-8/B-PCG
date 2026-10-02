using System;
using Bpcg.Numerics;

namespace Bpcg.Core;

/// <summary>
/// 점과 이웃 목록으로 된 셀 그래프 (src/bpcg/core/graph.py CellGraph). 물길·솔버·지하수 코드는 이 그래프만
/// 받으므로 구면 L0 와 평면 히어로 L2 가 같은 코드를 씁니다.
/// </summary>
/// <remarks>
/// Pos 는 (N, 3), Nbr·Dist 는 (N, 8) 행 우선입니다. 이웃이 없으면 Nbr = −1, Dist = +∞ 입니다.
/// 평면 셀 번호는 c = j·nx + i 이고 0번 행이 북쪽 끝입니다.
/// </remarks>
public sealed class CellGraph
{
    public CellGraph(
        string kind, long[] shape, double[] pos, int[] nbr, double[] dist, double[] area, double spacing,
        double? r = null, (double East, double North) origin = default)
    {
        if (kind is not ("sphere" or "flat"))
        {
            throw new ArgumentException($"graph.kind 는 'sphere' 또는 'flat' 이어야 합니다: '{kind}'");
        }
        int n = area.Length;
        if (pos.Length != n * 3 || nbr.Length != n * NSlots || dist.Length != n * NSlots)
        {
            throw new ArgumentException("pos·nbr·dist·area 의 칸 수가 서로 다릅니다");
        }
        Kind = kind;
        Shape = shape;
        Pos = pos;
        Nbr = nbr;
        Dist = dist;
        Area = area;
        Spacing = spacing;
        R = r;
        Origin = origin;
    }

    /// <summary>이웃 슬롯 수 (가이드 1장, 늘 8).</summary>
    public const int NSlots = 8;

    /// <summary>"sphere" 또는 "flat".</summary>
    public string Kind { get; }

    /// <summary>구면 (6, n, n), 평면 (ny, nx).</summary>
    public long[] Shape { get; }

    /// <summary>(N, 3) 대표점 [m].</summary>
    public double[] Pos { get; }

    /// <summary>(N, 8) 이웃 셀 번호, 없으면 −1.</summary>
    public int[] Nbr { get; }

    /// <summary>(N, 8) 이웃까지 거리 [m] (구면은 대원 거리), 없으면 +∞.</summary>
    public double[] Dist { get; }

    /// <summary>(N,) 셀 면적 [m²].</summary>
    public double[] Area { get; }

    /// <summary>대표 칸 크기 [m] = sqrt(평균 면적).</summary>
    public double Spacing { get; }

    /// <summary>구면 반지름 [m]. 평면은 null.</summary>
    public double? R { get; }

    /// <summary>평면: 북서쪽 모서리 칸의 바깥 모서리 (동, 북) [m].</summary>
    public (double East, double North) Origin { get; }

    /// <summary>칸 수.</summary>
    public int NCells => Area.Length;

    /// <summary>물이 지나가는 등고선 폭 [m] = sqrt(면적).</summary>
    public double[] Width()
    {
        double[] w = new double[Area.Length];
        for (int c = 0; c < w.Length; c++)
        {
            w[c] = Math.Sqrt(Area[c]);
        }
        return w;
    }

    /// <summary>구면: 대표점의 단위 벡터 (N, 3) (pos / norm(pos, axis=1)).</summary>
    public double[] Unit()
    {
        if (Kind != "sphere")
        {
            throw new InvalidOperationException("unit() 은 구면 그래프에서만 씁니다");
        }
        double[] u = new double[Pos.Length];
        for (int c = 0; c < NCells; c++)
        {
            double norm = LinAlg.Norm3(Pos.AsSpan(c * 3, 3));
            u[c * 3] = Pos[c * 3] / norm;
            u[(c * 3) + 1] = Pos[(c * 3) + 1] / norm;
            u[(c * 3) + 2] = Pos[(c * 3) + 2] / norm;
        }
        return u;
    }

    /// <summary>평면 그래프의 가장자리 칸 (이웃 슬롯 중 하나라도 비어 있음).</summary>
    public bool[] BoundaryMask()
    {
        bool[] m = new bool[NCells];
        for (int c = 0; c < NCells; c++)
        {
            for (int s = 0; s < NSlots; s++)
            {
                if (Nbr[(c * NSlots) + s] < 0)
                {
                    m[c] = true;
                    break;
                }
            }
        }
        return m;
    }
}

/// <summary>구면·평면 셀 그래프 만들기 (src/bpcg/core/graph.py).</summary>
public static class Graph
{
    private const long JitterStreamA = 101;
    private const long JitterStreamB = 102;

    /// <summary>노드 흔들기 비율: ((해시 − 0.5) · jitter) 두 성분 (_jitter_offsets).</summary>
    internal static (double[] Ja, double[] Jb) JitterOffsets(int nCells, double jitter, long seed)
    {
        long[] ids = new long[nCells];
        for (int i = 0; i < nCells; i++)
        {
            ids[i] = i;
        }
        double[] ua = Hashing.HashUniformArray(ids, seed, JitterStreamA);
        double[] ub = Hashing.HashUniformArray(ids, seed, JitterStreamB);
        double[] ja = new double[nCells];
        double[] jb = new double[nCells];
        for (int i = 0; i < nCells; i++)
        {
            ja[i] = (ua[i] - 0.5) * jitter;
            jb[i] = (ub[i] - 0.5) * jitter;
        }
        return (ja, jb);
    }

    /// <summary>
    /// 평면 격자 그래프 (flat_graph). 칸 크기 dx [m], 노드 흔들기 jitter (칸 크기 대비, 0~1).
    /// 대표점 x = o_e + (i + 0.5)·dx, y = o_n − (j + 0.5)·dx (흔들기는 jitter &gt; 0 일 때만).
    /// </summary>
    public static CellGraph FlatGraph(
        int ny, int nx, double dx, double jitter = 0.0, long seed = 0, (double East, double North) origin = default)
    {
        int nCells = ny * nx;
        double[] pos = new double[nCells * 3];
        double[]? ja = null;
        double[]? jb = null;
        if (jitter > 0)
        {
            (ja, jb) = JitterOffsets(nCells, jitter, seed);
        }
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int c = (j * nx) + i;
                double x = origin.East + ((i + 0.5) * dx);
                double y = origin.North - ((j + 0.5) * dx);
                if (ja is not null && jb is not null)
                {
                    x += ja[c] * dx;
                    y += jb[c] * dx;
                }
                pos[c * 3] = x;
                pos[(c * 3) + 1] = y;
                pos[(c * 3) + 2] = 0.0;
            }
        }
        int[] nbr = new int[nCells * CellGraph.NSlots];
        Array.Fill(nbr, -1);
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int c = (j * nx) + i;
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    (int dj, int di) = Cubesphere.NeighborSlots[s];
                    int j2 = j + dj;
                    int i2 = i + di;
                    if (j2 >= 0 && j2 < ny && i2 >= 0 && i2 < nx)
                    {
                        nbr[(c * CellGraph.NSlots) + s] = (j2 * nx) + i2;
                    }
                }
            }
        }
        double[] dist = Distances(pos, nbr, null);
        double[] area = new double[nCells];
        Array.Fill(area, dx * dx);
        return new CellGraph("flat", [ny, nx], pos, nbr, dist, area, dx, null, origin);
    }

    /// <summary>
    /// 구면 큐브스피어 그래프 (sphere_graph, 면당 n 칸, 반지름 R [m]). 흔들기는 면의 u, v 방향으로
    /// 칸 크기 비율만큼 옮긴 뒤 구면으로 되돌립니다. 흔들기가 0 이어도 pos = (grid.pos / R)·R 입니다.
    /// </summary>
    public static CellGraph SphereGraph(int n, double R, double jitter = 0.0, long seed = 0)
    {
        Grid grid = Cubesphere.CubesphereGrid(n, R);
        int nCells = grid.NCells;
        double[] area = grid.AreaFull();
        double[] unit = new double[nCells * 3];
        for (int k = 0; k < unit.Length; k++)
        {
            unit[k] = grid.Pos[k] / R;
        }
        if (jitter > 0)
        {
            (double[] ja, double[] jb) = JitterOffsets(nCells, jitter, seed);
            int nn = n * n;
            for (int c = 0; c < nCells; c++)
            {
                int f = c / nn;
                double h = Math.Sqrt(area[c]) / R;
                double ah = ja[c] * h;
                double bh = jb[c] * h;
                for (int k = 0; k < 3; k++)
                {
                    unit[(c * 3) + k] = unit[(c * 3) + k] + (ah * Cubesphere.FaceU[(f * 3) + k])
                        + (bh * Cubesphere.FaceV[(f * 3) + k]);
                }
            }
            for (int c = 0; c < nCells; c++)
            {
                double norm = LinAlg.Norm3(unit.AsSpan(c * 3, 3));
                unit[c * 3] /= norm;
                unit[(c * 3) + 1] /= norm;
                unit[(c * 3) + 2] /= norm;
            }
        }
        double[] pos = new double[nCells * 3];
        for (int k = 0; k < pos.Length; k++)
        {
            pos[k] = unit[k] * R;
        }
        int[] nbr = Cubesphere.NeighborTable(n);
        double[] dist = Distances(pos, nbr, R);
        double spacing = Math.Sqrt(NpReduce.Mean(area));
        return new CellGraph("sphere", [6, n, n], pos, nbr, dist, area, spacing, R);
    }

    // 이웃 거리: 평면은 유클리드 거리, 구면은 neighbor_distance(a/R, b/R, R). 없으면 +∞.
    private static double[] Distances(double[] pos, int[] nbr, double? R)
    {
        int nCells = pos.Length / 3;
        double[] dist = new double[nbr.Length];
        Array.Fill(dist, double.PositiveInfinity);
        Span<double> a = stackalloc double[3];
        Span<double> b = stackalloc double[3];
        for (int c = 0; c < nCells; c++)
        {
            for (int s = 0; s < CellGraph.NSlots; s++)
            {
                int v = nbr[(c * CellGraph.NSlots) + s];
                if (v < 0)
                {
                    continue;
                }
                if (R is double r)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        a[k] = pos[(c * 3) + k] / r;
                        b[k] = pos[(v * 3) + k] / r;
                    }
                    dist[(c * CellGraph.NSlots) + s] = Cubesphere.NeighborDistance(a, b, r);
                }
                else
                {
                    for (int k = 0; k < 3; k++)
                    {
                        a[k] = pos[(c * 3) + k] - pos[(v * 3) + k];
                    }
                    dist[(c * CellGraph.NSlots) + s] = LinAlg.Norm3(a);
                }
            }
        }
        return dist;
    }
}
