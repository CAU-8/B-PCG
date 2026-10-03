using System;
using Bpcg.Core;
using Bpcg.Numerics;

namespace Bpcg.Planet;

/// <summary>
/// 해수면과 바다 마스크 (src/bpcg/planet/ocean.py, docs/pipeline.md 4.3, 설계도 5장 3번).
/// </summary>
/// <remarks>
/// 이분법의 상한 hi 에 들어가는 area 합은 numpy pairwise 합이라 중점 수열 전체를 정합니다(NpReduce.Sum 필수).
/// </remarks>
public static class Ocean
{
    public const double SeaLevelRelTol = 1e-9;
    private const int MaxBisection = 200;

    /// <summary>V(h) = Σ A·max(h − z, 0) [m³], 셀 번호 순 순차 합 (_volume_below).</summary>
    internal static double VolumeBelow(double[] z, double[] area, double h)
    {
        double total = 0.0;
        for (int c = 0; c < z.Length; c++)
        {
            double d = h - z[c];
            if (d > 0.0)
            {
                total += area[c] * d;
            }
        }
        return total;
    }

    private static void CheckZArea(double[] z, double[] a)
    {
        if (z.Length != a.Length || z.Length == 0)
        {
            throw new ArgumentException($"z 와 area 는 같은 길이의 1차원 배열이어야 합니다: ({z.Length},), ({a.Length},)");
        }
        foreach (double v in z)
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentException("z_platform 에 NaN 이나 inf 가 있습니다");
            }
        }
        foreach (double v in a)
        {
            if (!(double.IsFinite(v) && v > 0))
            {
                throw new ArgumentException("area 는 모두 0 보다 큰 유한한 값이어야 합니다");
            }
        }
    }

    /// <summary>해수면 h 아래 물 부피 V(h) [m³] (ocean_volume).</summary>
    public static double OceanVolume(double[] zPlatform, double[] area, double h)
    {
        CheckZArea(zPlatform, area);
        return VolumeBelow(zPlatform, area, h);
    }

    /// <summary>물 부피 보존 해수면 h [m] (sea_level): V(h) = water_volume 을 이분법으로 풉니다 (상대 오차 1e-9).</summary>
    public static double SeaLevel(double[] zPlatform, double[] area, double waterVolume)
    {
        CheckZArea(zPlatform, area);
        double w = waterVolume;
        if (!(double.IsFinite(w) && w > 0.0))
        {
            throw new ArgumentException($"water_volume 은 0 보다 큰 유한한 값이어야 합니다: {waterVolume}");
        }
        double zmin = double.PositiveInfinity;
        double zmax = double.NegativeInfinity;
        foreach (double v in zPlatform)
        {
            zmin = Math.Min(zmin, v);
            zmax = Math.Max(zmax, v);
        }
        double lo = zmin;
        double hi = zmax + (w / NpReduce.Sum(area));
        double tol = SeaLevelRelTol * w;
        double h = hi;
        for (int it = 0; it < MaxBisection; it++)
        {
            h = 0.5 * (lo + hi);
            double v = VolumeBelow(zPlatform, area, h);
            if (Math.Abs(v - w) <= tol)
            {
                return h;
            }
            if (v < w)
            {
                lo = h;
            }
            else
            {
                hi = h;
            }
            if (hi - lo <= 4.0 * NpMath.Spacing(Math.Abs(h) + 1.0))
            {
                break;
            }
        }
        // 부피가 h 에 대해 조각별 선형이므로 마지막 구간에서 한 번 선형으로 맞춥니다.
        double vLo = VolumeBelow(zPlatform, area, lo);
        double vHi = VolumeBelow(zPlatform, area, hi);
        if (vHi > vLo)
        {
            h = lo + ((w - vLo) * (hi - lo) / (vHi - vLo));
        }
        return h;
    }

    /// <summary>mask 칸들의 연결 성분 번호 (밖은 −1) 와 성분 수 (_label_components). 작은 셀 번호부터 번호를 붙입니다.</summary>
    internal static (long[] Labels, int NLab) LabelComponents(bool[] mask, int[] nbr)
    {
        int n = mask.Length;
        int nSlots = n == 0 ? 0 : nbr.Length / n;
        long[] labels = new long[n];
        Array.Fill(labels, -1L);
        int[] stack = new int[n];
        int nLab = 0;
        for (int c = 0; c < n; c++)
        {
            if (!mask[c] || labels[c] >= 0)
            {
                continue;
            }
            labels[c] = nLab;
            int top = 0;
            stack[top] = c;
            top++;
            while (top > 0)
            {
                top--;
                int u = stack[top];
                for (int s = 0; s < nSlots; s++)
                {
                    int v = nbr[(u * nSlots) + s];
                    if (v >= 0 && mask[v] && labels[v] < 0)
                    {
                        labels[v] = nLab;
                        stack[top] = v;
                        top++;
                    }
                }
            }
            nLab++;
        }
        return (labels, nLab);
    }

    /// <summary>
    /// 바다 칸 (ocean_mask): z &lt; h 이고 그런 칸들의 가장 큰(면적) 연결 성분에 속하는 칸.
    /// 면적이 같은 성분이 둘이면 번호가 작은 칸을 가진 쪽입니다.
    /// </summary>
    public static bool[] OceanMask(CellGraph graph, double[] zPlatform, double h)
    {
        CheckZArea(zPlatform, graph.Area);
        if (!double.IsFinite(h))
        {
            throw new ArgumentException($"해수면 h 는 유한한 값이어야 합니다: {h}");
        }
        int n = zPlatform.Length;
        bool[] low = new bool[n];
        for (int c = 0; c < n; c++)
        {
            low[c] = zPlatform[c] < h;
        }
        (long[] labels, int nLab) = LabelComponents(low, graph.Nbr);
        bool[] output = new bool[n];
        if (nLab == 0)
        {
            return output;
        }
        double[] compArea = new double[nLab];
        for (int c = 0; c < n; c++)
        {
            if (low[c])
            {
                compArea[labels[c]] += graph.Area[c];
            }
        }
        int best = 0;
        for (int k = 1; k < nLab; k++)
        {
            if (compArea[k] > compArea[best])
            {
                best = k;
            }
        }
        for (int c = 0; c < n; c++)
        {
            output[c] = labels[c] == best;
        }
        return output;
    }
}
