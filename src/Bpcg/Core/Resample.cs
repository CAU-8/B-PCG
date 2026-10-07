using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Bpcg.Core;

/// <summary>
/// 고스트 칸을 채우는 보간 계수 (_ghost_stencil 의 반환). 번호는 모두 덧붙인 배열 (6, P, P) 을 펼친 번호입니다.
/// EdgeSrc·EdgeW 는 (E, 2), CornerSrc·CornerW 는 (C, 4) 행 우선입니다.
/// </summary>
public sealed record GhostStencil(
    long[] EdgeDst, long[] EdgeSrc, double[] EdgeW, long[] CornerDst, long[] CornerSrc, double[] CornerW);

/// <summary>
/// 큐브스피어 필드 보간: 고스트 줄, 구면 점 샘플, 거친 격자 → L0 옮기기 (src/bpcg/core/resample.py, 가이드 7장).
/// 면별 배열은 (6, n, n) [f, j, i] 이고, 고스트를 붙인 배열은 (6, n + 2L, n + 2L) 입니다.
/// </summary>
public static class Resample
{
    private static readonly (char Axis, int Sign)[] EdgeSides = [('i', 1), ('i', -1), ('j', 1), ('j', -1)];

    private static readonly ConcurrentDictionary<(int, int), Lazy<GhostStencil>> StencilCache = new();

    private static int FaceWithNormal(double ex, double ey, double ez)
    {
        // np.argmax(FACE_N @ e): 첫 최댓값
        int best = 0;
        double bestV = (Cubesphere.FaceN[0] * ex) + (Cubesphere.FaceN[1] * ey) + (Cubesphere.FaceN[2] * ez);
        for (int g = 1; g < 6; g++)
        {
            double d = (Cubesphere.FaceN[g * 3] * ex) + (Cubesphere.FaceN[(g * 3) + 1] * ey)
                + (Cubesphere.FaceN[(g * 3) + 2] * ez);
            if (d > bestV)
            {
                bestV = d;
                best = g;
            }
        }
        return best;
    }

    private static double Dot(ReadOnlySpan<double> q, double[] table, int f) =>
        (q[0] * table[f * 3]) + (q[1] * table[(f * 3) + 1]) + (q[2] * table[(f * 3) + 2]);

    /// <summary>
    /// 면 f 의 고스트 위치 q 가 놓이는 인접 면 f2 의 줄을 찾아 1D 보간 계수를 줍니다 (_locate_on_row).
    /// </summary>
    private static (long Idx0, long Idx1, double W1) LocateOnRow(
        ReadOnlySpan<double> q, int f, int f2, int n, int L, long loMin, long loMax)
    {
        long P = n + (2 * L);
        double qn = Dot(q, Cubesphere.FaceN, f2);
        double a2 = 4.0 / Math.PI * Math.Atan(Dot(q, Cubesphere.FaceU, f2) / qn);
        double b2 = 4.0 / Math.PI * Math.Atan(Dot(q, Cubesphere.FaceV, f2) / qn);
        double x2 = ((a2 + 1.0) * n / 2.0) - 0.5 + L; // 덧붙인 배열의 연속 열 번호
        double y2 = ((b2 + 1.0) * n / 2.0) - 0.5 + L; // 덧붙인 배열의 연속 행 번호
        // 원래 면의 법선이 인접 면의 u 축이면 고스트 줄은 고정된 열(i) 위에 놓입니다.
        double nu = (Cubesphere.FaceN[f * 3] * Cubesphere.FaceU[f2 * 3])
            + (Cubesphere.FaceN[(f * 3) + 1] * Cubesphere.FaceU[(f2 * 3) + 1])
            + (Cubesphere.FaceN[(f * 3) + 2] * Cubesphere.FaceU[(f2 * 3) + 2]);
        bool fixedIsI = Math.Abs(nu) > 0.5;
        double fixedV = fixedIsI ? x2 : y2;
        double moving = fixedIsI ? y2 : x2;
        long fixedIdx = (long)Math.Round(fixedV, MidpointRounding.ToEven);
        // 내부 불변식: 줄 중심선 위에 정확히 놓이고, 그 줄은 f2 의 원래 칸 줄입니다.
        if (!(Math.Abs(fixedV - fixedIdx) < 1e-6) || fixedIdx < L || fixedIdx >= L + n)
        {
            throw new InvalidOperationException("고스트 줄이 인접 면의 칸 중심선 위에 놓이지 않습니다");
        }
        long lo = Math.Clamp((long)Math.Floor(moving), loMin, loMax);
        double w1 = moving - lo;
        if (!(w1 > -1e-9 && w1 < 1.0 + 1e-9))
        {
            throw new InvalidOperationException("고스트 보간 가중치가 [0, 1] 밖입니다");
        }
        long idx0;
        long idx1;
        if (fixedIsI)
        {
            idx0 = (f2 * P * P) + (lo * P) + fixedIdx;
            idx1 = idx0 + P;
        }
        else
        {
            idx0 = (f2 * P * P) + (fixedIdx * P) + lo;
            idx1 = idx0 + 1;
        }
        return (idx0, idx1, w1);
    }

    /// <summary>고스트 칸을 채우는 보간 계수 (_ghost_stencil). (n, layers) 마다 한 번 만들어 둡니다.</summary>
    internal static GhostStencil GetGhostStencil(int n, int layers) =>
        StencilCache.GetOrAdd((n, layers), key => new Lazy<GhostStencil>(() => BuildStencil(key.Item1, key.Item2)))
            .Value;

    private static GhostStencil BuildStencil(int n, int layers)
    {
        int L = layers;
        long P = n + (2 * L);
        double[] centers = new double[n];
        for (int i = 0; i < n; i++)
        {
            centers[i] = -1.0 + (((2.0 * i) + 1.0) / n);
        }
        var eDst = new List<long>();
        var eSrc = new List<long>();
        var eW = new List<double>();
        Span<double> q = stackalloc double[3];
        for (int f = 0; f < 6; f++)
        {
            foreach ((char axis, int sign) in EdgeSides)
            {
                double[] tab = axis == 'i' ? Cubesphere.FaceU : Cubesphere.FaceV;
                int f2 = FaceWithNormal(
                    sign * tab[f * 3], sign * tab[(f * 3) + 1], sign * tab[(f * 3) + 2]);
                // meshgrid(arange(1, L+1), arange(n), indexing="ij") 를 펼친 순서: m 바깥, k 안쪽
                for (int m = 1; m <= L; m++)
                {
                    double beyond = 1.0 + (((2.0 * m) - 1.0) / n);
                    for (int k = 0; k < n; k++)
                    {
                        double along = centers[k];
                        long outer = (sign > 0 ? n - 1 + m : -m) + L;
                        double a;
                        double b;
                        long gi;
                        long gj;
                        if (axis == 'i')
                        {
                            a = sign * beyond;
                            b = along;
                            gi = outer;
                            gj = k + L;
                        }
                        else
                        {
                            a = along;
                            b = sign * beyond;
                            gi = k + L;
                            gj = outer;
                        }
                        double X = Math.Tan(Math.PI * a / 4.0);
                        double Y = Math.Tan(Math.PI * b / 4.0);
                        for (int c = 0; c < 3; c++)
                        {
                            q[c] = Cubesphere.FaceN[(f * 3) + c] + (X * Cubesphere.FaceU[(f * 3) + c])
                                + (Y * Cubesphere.FaceV[(f * 3) + c]);
                        }
                        (long idx0, long idx1, double w1) = LocateOnRow(q, f, f2, n, L, L, L + n - 2);
                        eDst.Add((f * P * P) + (gj * P) + gi);
                        eSrc.Add(idx0);
                        eSrc.Add(idx1);
                        eW.Add(1.0 - w1);
                        eW.Add(w1);
                    }
                }
            }
        }

        var cDst = new List<long>();
        var cSrc = new List<long>();
        var cW = new List<double>();
        for (int f = 0; f < 6; f++)
        {
            foreach (int si in (int[])[1, -1])
            {
                foreach (int sj in (int[])[1, -1])
                {
                    for (int mi = 1; mi <= L; mi++)
                    {
                        for (int mj = 1; mj <= L; mj++)
                        {
                            double a = si * (1.0 + (((2.0 * mi) - 1.0) / n));
                            double b = sj * (1.0 + (((2.0 * mj) - 1.0) / n));
                            double X = Math.Tan(Math.PI * a / 4.0);
                            double Y = Math.Tan(Math.PI * b / 4.0);
                            for (int c = 0; c < 3; c++)
                            {
                                q[c] = Cubesphere.FaceN[(f * 3) + c] + (X * Cubesphere.FaceU[(f * 3) + c])
                                    + (Y * Cubesphere.FaceV[(f * 3) + c]);
                            }
                            // 연장한 면 평면의 이 점은 |X|, |Y| 중 큰 쪽 인접 면에 속합니다.
                            // 같으면 두 면의 공유 경계 위이므로 양쪽에서 구해 평균합니다.
                            var cand = new List<int>();
                            if (mi >= mj)
                            {
                                cand.Add(FaceWithNormal(
                                    si * Cubesphere.FaceU[f * 3], si * Cubesphere.FaceU[(f * 3) + 1],
                                    si * Cubesphere.FaceU[(f * 3) + 2]));
                            }
                            if (mj >= mi)
                            {
                                cand.Add(FaceWithNormal(
                                    sj * Cubesphere.FaceV[f * 3], sj * Cubesphere.FaceV[(f * 3) + 1],
                                    sj * Cubesphere.FaceV[(f * 3) + 2]));
                            }
                            var src = new List<long>();
                            var w = new List<double>();
                            foreach (int f2 in cand)
                            {
                                (long idx0, long idx1, double w1) = LocateOnRow(q, f, f2, n, L, L - 1, L + n - 1);
                                src.Add(idx0);
                                src.Add(idx1);
                                w.Add((1.0 - w1) / cand.Count);
                                w.Add(w1 / cand.Count);
                            }
                            if (cand.Count == 1)
                            {
                                src.Add(src[0]);
                                src.Add(src[0]);
                                w.Add(0.0);
                                w.Add(0.0);
                            }
                            long gi = (si > 0 ? n - 1 + mi : -mi) + L;
                            long gj = (sj > 0 ? n - 1 + mj : -mj) + L;
                            cDst.Add((f * P * P) + (gj * P) + gi);
                            cSrc.AddRange(src);
                            cW.AddRange(w);
                        }
                    }
                }
            }
        }
        return new GhostStencil(
            eDst.ToArray(), eSrc.ToArray(), eW.ToArray(), cDst.ToArray(), cSrc.ToArray(), cW.ToArray());
    }

    /// <summary>
    /// 면별 필드 (6, n, n) 에 인접 면에서 1D 보간한 고스트 줄을 덧붙입니다 (add_ghost_layers).
    /// 반환: (6, n + 2L, n + 2L) float64 행 우선. NaN 이 있으면 그 칸을 쓰는 고스트도 NaN 입니다.
    /// </summary>
    public static double[] AddGhostLayers(double[] faces, int n, int layers = 1)
    {
        if (faces.Length != 6 * n * n)
        {
            throw new ArgumentException($"faces 는 (6, n, n) 배열이어야 합니다: 원소 수 {faces.Length}, n = {n}");
        }
        if (layers < 1)
        {
            throw new ArgumentException($"layers 는 1 이상의 정수여야 합니다: {layers}");
        }
        if (2 * layers > n)
        {
            throw new ArgumentException($"layers 는 n/2 이하여야 합니다: layers={layers}, n={n}");
        }
        int L = layers;
        int P = n + (2 * L);
        double[] flat = new double[6 * P * P];
        for (int f = 0; f < 6; f++)
        {
            for (int j = 0; j < n; j++)
            {
                Array.Copy(faces, (f * n * n) + (j * n), flat, (f * P * P) + ((j + L) * P) + L, n);
            }
        }
        GhostStencil st = GetGhostStencil(n, L);
        for (int e = 0; e < st.EdgeDst.Length; e++)
        {
            flat[st.EdgeDst[e]] = (flat[st.EdgeSrc[e * 2]] * st.EdgeW[e * 2])
                + (flat[st.EdgeSrc[(e * 2) + 1]] * st.EdgeW[(e * 2) + 1]);
        }
        for (int c = 0; c < st.CornerDst.Length; c++)
        {
            // (C, 4) 축 합: +0.0 에서 시작하는 순차 합 (모두 −0.0 이어도 +0.0)
            double acc = 0.0;
            for (int t = 0; t < 4; t++)
            {
                acc += flat[st.CornerSrc[(c * 4) + t]] * st.CornerW[(c * 4) + t];
            }
            flat[st.CornerDst[c]] = acc;
        }
        return flat;
    }

    /// <summary>add_ghost_layers (float32 입력은 float64 로 넓혀 계산).</summary>
    public static double[] AddGhostLayers(float[] faces, int n, int layers = 1) =>
        AddGhostLayers(Array.ConvertAll(faces, v => (double)v), n, layers);

    /// <summary>add_ghost_layers (정수 입력은 float64 로 바꿔 계산).</summary>
    public static double[] AddGhostLayers(int[] faces, int n, int layers = 1) =>
        AddGhostLayers(Array.ConvertAll(faces, v => (double)v), n, layers);

    /// <summary>
    /// 고스트를 붙인 (6, n+2L, n+2L) 배열에서 점 (M,3) 마다 쌍선형 보간 (_bilinear_kernel). 점마다 나눕니다.
    /// </summary>
    internal static void BilinearKernel(double[] padded, double[] unit, int n, int layers, double[] output)
    {
        int size = n + (2 * layers);
        double fourOverPi = 4.0 / Math.PI;
        double[] fu = Cubesphere.FaceU;
        double[] fv = Cubesphere.FaceV;
        double[] fn = Cubesphere.FaceN;
        int m = unit.Length / 3;
        Parallelism.For(0, m, k =>
        {
            double px = unit[k * 3];
            double py = unit[(k * 3) + 1];
            double pz = unit[(k * 3) + 2];
            // 역사상 f* = argmax_f p·n_f (같으면 번호가 작은 면)
            int f = 0;
            double best = (px * fn[0]) + (py * fn[1]) + (pz * fn[2]);
            for (int g = 1; g < 6; g++)
            {
                double d = (px * fn[g * 3]) + (py * fn[(g * 3) + 1]) + (pz * fn[(g * 3) + 2]);
                if (d > best)
                {
                    best = d;
                    f = g;
                }
            }
            double pu = (px * fu[f * 3]) + (py * fu[(f * 3) + 1]) + (pz * fu[(f * 3) + 2]);
            double pv = (px * fv[f * 3]) + (py * fv[(f * 3) + 1]) + (pz * fv[(f * 3) + 2]);
            double a = fourOverPi * Math.Atan(pu / best);
            double b = fourOverPi * Math.Atan(pv / best);
            // 칸 중심 a_i = −1 + (2i+1)/n 이므로 연속 번호 x = (a+1)·n/2 − 0.5, 고스트만큼 밀기
            double x = ((a + 1.0) * 0.5 * n) - 0.5 + layers;
            double y = ((b + 1.0) * 0.5 * n) - 0.5 + layers;
            long i0 = (long)Math.Floor(x);
            long j0 = (long)Math.Floor(y);
            i0 = Math.Clamp(i0, 0, size - 2);
            j0 = Math.Clamp(j0, 0, size - 2);
            double tx = x - i0;
            double ty = y - j0;
            long baseIdx = (long)f * size * size;
            double v00 = padded[baseIdx + (j0 * size) + i0];
            double v01 = padded[baseIdx + (j0 * size) + i0 + 1];
            double v10 = padded[baseIdx + ((j0 + 1) * size) + i0];
            double v11 = padded[baseIdx + ((j0 + 1) * size) + i0 + 1];
            double top = v00 + (tx * (v01 - v00));
            double bot = v10 + (tx * (v11 - v10));
            output[k] = top + (ty * (bot - top));
        });
    }

    private static void CheckUnit(double[] unit)
    {
        if (unit.Length % 3 != 0)
        {
            throw new ArgumentException($"unit 은 (M, 3) 배열이어야 합니다: 원소 수 {unit.Length}");
        }
        for (int k = 0; k < unit.Length; k++)
        {
            if (!double.IsFinite(unit[k]))
            {
                throw new ArgumentException("unit 에 NaN 이나 inf 가 있습니다");
            }
        }
        for (int k = 0; k < unit.Length / 3; k++)
        {
            if (unit[k * 3] == 0.0 && unit[(k * 3) + 1] == 0.0 && unit[(k * 3) + 2] == 0.0)
            {
                throw new ArgumentException("unit 에 길이 0 인 벡터가 있습니다");
            }
        }
    }

    private static void CheckField(int length, int n)
    {
        if (n < 1)
        {
            throw new ArgumentException($"n 은 1 이상의 정수여야 합니다: {n}");
        }
        if (length != 6 * n * n)
        {
            throw new ArgumentException($"field 모양이 (6n²,) = ({6 * n * n},) 이 아닙니다: ({length},)");
        }
    }

    /// <summary>
    /// 큐브스피어 필드를 구면 위 임의의 점에서 읽습니다 (sample_sphere, method="linear"):
    /// 역사상 → 면 좌표 → 고스트 줄을 붙인 쌍선형 보간. unit: (M, 3) 방향 벡터. 반환: (M,) float64.
    /// </summary>
    public static double[] SampleSphere(double[] field, int n, double[] unit)
    {
        CheckField(field.Length, n);
        CheckUnit(unit);
        if (n < 2)
        {
            throw new ArgumentException("linear 보간에는 n ≥ 2 가 필요합니다");
        }
        double[] padded = AddGhostLayers(field, n, 1);
        double[] output = new double[unit.Length / 3];
        BilinearKernel(padded, unit, n, 1, output);
        return output;
    }

    /// <summary>sample_sphere linear (float32 필드).</summary>
    public static double[] SampleSphere(float[] field, int n, double[] unit) =>
        SampleSphere(Array.ConvertAll(field, v => (double)v), n, unit);

    /// <summary>sample_sphere method="nearest": field[cell_of(unit)] (범주 값용, 원래 형 그대로).</summary>
    public static T[] SampleSphereNearest<T>(T[] field, int n, double[] unit)
    {
        CheckField(field.Length, n);
        CheckUnit(unit);
        long[] cells = Cubesphere.CellOf(unit, n);
        T[] output = new T[cells.Length];
        for (int k = 0; k < cells.Length; k++)
        {
            output[k] = field[cells[k]];
        }
        return output;
    }

    /// <summary>거친 큐브스피어 필드 (6·nSrc²,) 를 구면 그래프의 칸 대표점으로 옮깁니다 (resample_sphere, linear).</summary>
    public static double[] ResampleSphere(double[] field, int nSrc, CellGraph dst)
    {
        CheckSphere(dst);
        return SampleSphere(field, nSrc, dst.Unit());
    }

    /// <summary>resample_sphere linear (float32 필드).</summary>
    public static double[] ResampleSphere(float[] field, int nSrc, CellGraph dst)
    {
        CheckSphere(dst);
        return SampleSphere(field, nSrc, dst.Unit());
    }

    /// <summary>resample_sphere method="nearest".</summary>
    public static T[] ResampleSphereNearest<T>(T[] field, int nSrc, CellGraph dst)
    {
        CheckSphere(dst);
        return SampleSphereNearest(field, nSrc, dst.Unit());
    }

    private static void CheckSphere(CellGraph dst)
    {
        if (dst.Kind != "sphere")
        {
            throw new ArgumentException("graph_dst 는 구면 CellGraph (kind='sphere') 여야 합니다");
        }
    }
}
