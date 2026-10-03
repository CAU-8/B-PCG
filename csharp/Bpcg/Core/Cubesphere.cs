using System;
using System.Collections.Generic;
using Bpcg.Numerics;

namespace Bpcg.Core;

/// <summary>
/// 큐브스피어 격자 하나: 면 한 변 칸 수 n, 반지름 R, 셀 중심 위치, 면 하나분의 셀 면적
/// (src/bpcg/core/cubesphere.py Grid).
/// </summary>
public sealed class Grid
{
    public Grid(int n, double r, double[] pos, double[] area)
    {
        N = n;
        R = r;
        Pos = pos;
        Area = area;
    }

    /// <summary>면 한 변의 칸 수.</summary>
    public int N { get; }

    /// <summary>반지름 [m].</summary>
    public double R { get; }

    /// <summary>(6n², 3) 셀 중심 위치 [m], 행 우선, 셀 번호 c = f·n² + j·n + i 순서.</summary>
    public double[] Pos { get; }

    /// <summary>(n²,) 면 하나분의 셀 면적 [m²]. 셀 c 의 면적은 Area[c % n²].</summary>
    public double[] Area { get; }

    /// <summary>전체 칸 수 6·n².</summary>
    public int NCells => 6 * N * N;

    /// <summary>전체 셀(6n²)의 면적: 면 하나분을 여섯 번 반복합니다(np.tile).</summary>
    public double[] AreaFull()
    {
        int nn = N * N;
        double[] full = new double[6 * nn];
        for (int f = 0; f < 6; f++)
        {
            Array.Copy(Area, 0, full, f * nn, nn);
        }
        return full;
    }
}

/// <summary>
/// 등각 큐브스피어 격자 (src/bpcg/core/cubesphere.py, 가이드 1장).
/// 셀 번호 c = f·n² + j·n + i, 셀 중심 a_i = −1 + (2i+1)/n, 면 기저는 오른손 좌표계 u × v = n.
/// </summary>
/// <remarks>
/// 계산 순서는 numpy 와 같게 두었습니다. tan·atan·atan2·pow 는 플랫폼 libm 을 부르므로 macOS 에서는
/// numpy 와 비트 단위로 같고, 다른 OS 에서는 몇 ulp 다를 수 있습니다(docs/csharp_port.md 6장 core ②).
/// </remarks>
public static class Cubesphere
{
    /// <summary>FACE_NAMES.</summary>
    public static readonly string[] FaceNames = ["+X", "-X", "+Y", "-Y", "+Z", "-Z"];

    /// <summary>FACE_U (6, 3) 행 우선.</summary>
    public static readonly double[] FaceU =
        [0, 1, 0, 0, -1, 0, 0, 0, 1, 0, 0, -1, 1, 0, 0, -1, 0, 0];

    /// <summary>FACE_V (6, 3) 행 우선.</summary>
    public static readonly double[] FaceV =
        [0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 0];

    /// <summary>FACE_N (6, 3) 행 우선.</summary>
    public static readonly double[] FaceN =
        [1, 0, 0, -1, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 1, 0, 0, -1];

    /// <summary>이웃 슬롯 순서 (dj, di). 가이드 1장과 같습니다.</summary>
    public static readonly (int Dj, int Di)[] NeighborSlots =
        [(-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)];

    /// <summary>가로·세로 이웃 슬롯.</summary>
    public static readonly int[] CardinalSlots = [1, 3, 4, 6];

    /// <summary>
    /// 면 좌표 (a, b) 하나 → 구면 위 단위 벡터 (to_sphere). X = tan(π·a/4), num = (N + X·U) + Y·V,
    /// den = sqrt((1 + X²) + Y²).
    /// </summary>
    public static void ToSphere(int f, double a, double b, Span<double> p)
    {
        double X = Math.Tan(Math.PI * a / 4.0);
        double Y = Math.Tan(Math.PI * b / 4.0);
        double den = Math.Sqrt(1.0 + (X * X) + (Y * Y));
        for (int k = 0; k < 3; k++)
        {
            double num = FaceN[(f * 3) + k] + (X * FaceU[(f * 3) + k]) + (Y * FaceV[(f * 3) + k]);
            p[k] = num / den;
        }
    }

    /// <summary>to_sphere 배열판: a, b (K,) → (K, 3) 행 우선.</summary>
    public static double[] ToSphere(int f, double[] a, double[] b)
    {
        if (f < 0 || f >= 6)
        {
            throw new ArgumentOutOfRangeException(nameof(f), $"면 번호는 0..5 여야 합니다: {f}");
        }
        double[] output = new double[a.Length * 3];
        for (int k = 0; k < a.Length; k++)
        {
            ToSphere(f, a[k], b[k], output.AsSpan(k * 3, 3));
        }
        return output;
    }

    /// <summary>셀 면적 계산을 위한 입체각 함수 ω(X, Y) = atan2(X·Y, sqrt((1 + X²) + Y²)).</summary>
    public static double Omega(double X, double Y) => Math.Atan2(X * Y, Math.Sqrt(1.0 + (X * X) + (Y * Y)));

    /// <summary>
    /// 면 하나를 n×n 으로 나눈 각 셀의 정확한 면적 [m²], [j·n + i] 순서 (face_cell_areas).
    /// 포함-배제 뒤 거울 평균과 전치 평균을 Python 과 같은 순서로 합니다.
    /// </summary>
    public static double[] FaceCellAreas(int n, double R)
    {
        double[] axis = NpGrid.Linspace(-1.0, 1.0, n + 1);
        double[] tLo = new double[n];
        double[] tHi = new double[n];
        for (int i = 0; i < n; i++)
        {
            tLo[i] = Math.Tan(Math.PI * axis[i] / 4.0);
            tHi[i] = Math.Tan(Math.PI * axis[i + 1] / 4.0);
        }
        double[] om = new double[n * n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                // ((ω(X+, Y+) − ω(X−, Y+)) − ω(X+, Y−)) + ω(X−, Y−); X 는 열 i, Y 는 행 j
                om[(j * n) + i] = Omega(tHi[i], tHi[j]) - Omega(tLo[i], tHi[j]) - Omega(tHi[i], tLo[j])
                    + Omega(tLo[i], tLo[j]);
            }
        }
        double[] mirror = new double[n * n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double s = om[(j * n) + i] + om[(j * n) + (n - 1 - i)];
                s += om[((n - 1 - j) * n) + i];
                s += om[((n - 1 - j) * n) + (n - 1 - i)];
                mirror[(j * n) + i] = 0.25 * s;
            }
        }
        double r2 = Math.Pow(R, 2.0); // Python R**2 는 libm pow (R*R 와 1ulp 다를 수 있음)
        double[] area = new double[n * n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double t = 0.5 * (mirror[(j * n) + i] + mirror[(i * n) + j]);
                area[(j * n) + i] = r2 * t;
            }
        }
        return area;
    }

    /// <summary>
    /// 아주 가까운 두 점 사이의 대원 거리 [m] (neighbor_distance): R·atan2(|p1×p2|, p1·p2).
    /// </summary>
    public static double NeighborDistance(ReadOnlySpan<double> p1, ReadOnlySpan<double> p2, double R)
    {
        Span<double> c = stackalloc double[3];
        LinAlg.Cross3(p1, p2, c);
        double crossNorm = LinAlg.Norm3(c);
        double dot = LinAlg.Dot3Np(p1, p2);
        return R * Math.Atan2(crossNorm, dot);
    }

    /// <summary>neighbor_distance 배열판: p1, p2 (M, 3) 행 우선 → (M,).</summary>
    public static double[] NeighborDistance(double[] p1, double[] p2, double R)
    {
        int m = p1.Length / 3;
        double[] output = new double[m];
        for (int k = 0; k < m; k++)
        {
            output[k] = NeighborDistance(p1.AsSpan(k * 3, 3), p2.AsSpan(k * 3, 3), R);
        }
        return output;
    }

    /// <summary>
    /// 구면 위 점 (M, 3) → (면 f, a, b) (to_face). 면은 p·n_f 가 가장 큰 면(같으면 번호가 작은 면)이고,
    /// a = (4/π)·atan((p·u)/(p·n)). 0 벡터는 0/0 = NaN 입니다.
    /// </summary>
    public static (long[] F, double[] A, double[] B) ToFace(double[] p)
    {
        int m = p.Length / 3;
        long[] fs = new long[m];
        double[] a = new double[m];
        double[] b = new double[m];
        double fourOverPi = 4.0 / Math.PI;
        for (int k = 0; k < m; k++)
        {
            ReadOnlySpan<double> q = p.AsSpan(k * 3, 3);
            int f = ArgMaxFace(q);
            double pn = LinAlg.Dot3Np(q, FaceN.AsSpan(f * 3, 3));
            double pu = LinAlg.Dot3Np(q, FaceU.AsSpan(f * 3, 3));
            double pv = LinAlg.Dot3Np(q, FaceV.AsSpan(f * 3, 3));
            fs[k] = f;
            a[k] = fourOverPi * Math.Atan(pu / pn);
            b[k] = fourOverPi * Math.Atan(pv / pn);
        }
        return (fs, a, b);
    }

    // np.argmax(p @ FACE_N.T): 첫 최댓값, NaN 이 있으면 첫 NaN.
    private static int ArgMaxFace(ReadOnlySpan<double> q)
    {
        int best = 0;
        double bestV = LinAlg.Dot3Numba(q, FaceN.AsSpan(0, 3));
        if (double.IsNaN(bestV))
        {
            return 0;
        }
        for (int g = 1; g < 6; g++)
        {
            double d = LinAlg.Dot3Numba(q, FaceN.AsSpan(g * 3, 3));
            if (double.IsNaN(d))
            {
                return g;
            }
            if (d > bestV)
            {
                bestV = d;
                best = g;
            }
        }
        return best;
    }

    /// <summary>
    /// 구면 위 점 (M, 3) → 그 점을 담은 셀 번호 (cell_of). i = clip(floor(n·(a + 1)/2), 0, n − 1),
    /// NaN 은 0 (numpy arm64 와 .NET 9+ 모두 포화 변환).
    /// </summary>
    public static long[] CellOf(double[] p, int n)
    {
        (long[] f, double[] a, double[] b) = ToFace(p);
        long[] cells = new long[f.Length];
        for (int k = 0; k < f.Length; k++)
        {
            long i = Math.Clamp((long)Math.Floor(n * (a[k] + 1.0) / 2.0), 0, n - 1);
            long j = Math.Clamp((long)Math.Floor(n * (b[k] + 1.0) / 2.0), 0, n - 1);
            cells[k] = (f[k] * n * n) + (j * n) + i;
        }
        return cells;
    }

    private static int[] IntRow(double[] table, int f) =>
        [(int)table[f * 3], (int)table[(f * 3) + 1], (int)table[(f * 3) + 2]];

    private static int Dot(int[] x, int[] y) => (x[0] * y[0]) + (x[1] * y[1]) + (x[2] * y[2]);

    private static int FaceWithNormal(int[] e)
    {
        for (int f = 0; f < 6; f++)
        {
            int[] nf = IntRow(FaceN, f);
            if (nf[0] == e[0] && nf[1] == e[1] && nf[2] == e[2])
            {
                return f;
            }
        }
        throw new InvalidOperationException("법선이 맞는 면이 없습니다");
    }

    // 가이드 1장 '이웃 규칙': 면 f 에서 방향 e 로 넘어갈 때 축 t 의 인덱스 k 가 닿는 (f2, i3, j3).
    private static (int F2, int I3, int J3) CrossEdge(int f, int n, int[] e, int[] t, int k)
    {
        int f2 = FaceWithNormal(e);
        int[] nfI = IntRow(FaceN, f);
        int[] uf2 = IntRow(FaceU, f2);
        int[] vf2 = IntRow(FaceV, f2);
        if (Math.Abs(Dot(nfI, uf2)) == 1)
        {
            int s1 = Dot(nfI, uf2);
            int s2 = Dot(t, vf2);
            int i3 = s1 > 0 ? n - 1 : 0;
            int j3 = s2 > 0 ? k : n - 1 - k;
            return (f2, i3, j3);
        }
        else
        {
            int s1 = Dot(nfI, vf2);
            int s2 = Dot(t, uf2);
            int j3 = s1 > 0 ? n - 1 : 0;
            int i3 = s2 > 0 ? k : n - 1 - k;
            return (f2, i3, j3);
        }
    }

    /// <summary>
    /// 전체 이웃 표 (6n², 8) 행 우선 int (neighbor_table). 큐브 꼭짓점 너머 대각선은 −1.
    /// </summary>
    public static int[] NeighborTable(int n)
    {
        if (n > 18918)
        {
            throw new ArgumentOutOfRangeException(nameof(n), "이웃 표가 int32 를 넘습니다 (n ≤ 18918)");
        }
        int nn = n * n;
        int[] nbr = new int[6 * nn * 8];
        Array.Fill(nbr, -1);
        for (int f = 0; f < 6; f++)
        {
            int[] uf = IntRow(FaceU, f);
            int[] vf = IntRow(FaceV, f);
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int c = (f * nn) + (j * n) + i;
                    for (int s = 0; s < 8; s++)
                    {
                        (int dj, int di) = NeighborSlots[s];
                        int i2 = i + di;
                        int j2 = j + dj;
                        bool inI = i2 >= 0 && i2 < n;
                        bool inJ = j2 >= 0 && j2 < n;
                        if (inI && inJ)
                        {
                            nbr[(c * 8) + s] = (f * nn) + (j2 * n) + i2;
                        }
                        else if (!inI && inJ)
                        {
                            int side = i2 == n ? 1 : -1;
                            int[] e = [side * uf[0], side * uf[1], side * uf[2]];
                            (int f2, int i3, int j3) = CrossEdge(f, n, e, vf, j2);
                            nbr[(c * 8) + s] = (f2 * nn) + (j3 * n) + i3;
                        }
                        else if (inI && !inJ)
                        {
                            int side = j2 == n ? 1 : -1;
                            int[] e = [side * vf[0], side * vf[1], side * vf[2]];
                            (int f2, int i3, int j3) = CrossEdge(f, n, e, uf, i2);
                            nbr[(c * 8) + s] = (f2 * nn) + (j3 * n) + i3;
                        }
                    }
                }
            }
        }
        return nbr;
    }

    /// <summary>
    /// 면 경계를 사이에 둔 가로·세로 이웃 쌍 (k, 2) 행 우선 long (cross_face_pairs). 칸 → 슬롯 1·3·4·6 순서,
    /// 각 쌍은 한 번씩만 (c &lt; c2).
    /// </summary>
    public static long[] CrossFacePairs(int n, int[]? nbr = null)
    {
        nbr ??= NeighborTable(n);
        long nn = (long)n * n;
        int nCells = nbr.Length / 8;
        var pairs = new List<long>();
        for (int c = 0; c < nCells; c++)
        {
            foreach (int s in CardinalSlots)
            {
                int c2 = nbr[(c * 8) + s];
                if (c2 >= 0 && c / nn != c2 / nn && c < c2)
                {
                    pairs.Add(c);
                    pairs.Add(c2);
                }
            }
        }
        return pairs.ToArray();
    }

    /// <summary>반지름 R [m] 인 전체 큐브스피어 격자 (cubesphere_grid).</summary>
    public static Grid CubesphereGrid(int n, double R = 6_371_000.0)
    {
        double[] centers = new double[n];
        for (int i = 0; i < n; i++)
        {
            centers[i] = -1.0 + (((2.0 * i) + 1.0) / n);
        }
        int nn = n * n;
        double[] pos = new double[6 * nn * 3];
        Span<double> p = stackalloc double[3];
        for (int f = 0; f < 6; f++)
        {
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    ToSphere(f, centers[i], centers[j], p);
                    int c = (f * nn) + (j * n) + i;
                    pos[c * 3] = p[0] * R;
                    pos[(c * 3) + 1] = p[1] * R;
                    pos[(c * 3) + 2] = p[2] * R;
                }
            }
        }
        return new Grid(n, R, pos, FaceCellAreas(n, R));
    }
}
