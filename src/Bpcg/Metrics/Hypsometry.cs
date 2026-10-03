using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Numerics;

namespace Bpcg.Metrics;

/// <summary>
/// 고도 분포와 면적 비율 지표 (src/bpcg/metrics/hypsometry.py, docs/pipeline.md 12장).
/// </summary>
public static class Hypsometry
{
    public const double BimodalBinM = 100.0;
    public const double BimodalSmoothM = 250.0;
    public const double BimodalMinSeparationM = 1000.0;
    public const double BimodalMinProminence = 0.05;

    private static double[] Area(double[]? area, int n)
    {
        if (area is null)
        {
            double[] o = new double[n];
            Array.Fill(o, 1.0);
            return o;
        }
        if (area.Length != n)
        {
            throw new ArgumentException($"area 의 길이 {area.Length} 가 {n} 와 다릅니다");
        }
        foreach (double v in area)
        {
            if (!(double.IsFinite(v) && v >= 0.0))
            {
                throw new ArgumentException("area 는 0 이상의 유한한 값이어야 합니다");
            }
        }
        return area;
    }

    private static double WeightedFraction(bool[] hit, bool[] use, double[] area)
    {
        double total = NpStats.SumWhere(area, use);
        if (!(total > 0.0))
        {
            return double.NaN;
        }
        bool[] both = new bool[hit.Length];
        for (int i = 0; i < both.Length; i++)
        {
            both[i] = use[i] && hit[i];
        }
        return NpStats.SumWhere(area, both) / total;
    }

    /// <summary>면적 가중 고도 히스토그램 (hypsometry). edges 가 null 이면 bins 칸(z 최솟값~최댓값).</summary>
    public static (double[] Edges, double[] Frac) HypsometryHist(double[] z, double[]? area, int bins = 100, double[]? edges = null)
    {
        double[] a = Area(area, z.Length);
        bool[] ok = Array.ConvertAll(z, double.IsFinite);
        if (Array.IndexOf(ok, true) < 0)
        {
            throw new ArgumentException("z 에 유한한 값이 하나도 없습니다");
        }
        double total = NpStats.SumWhere(a, ok);
        if (!(total > 0.0))
        {
            throw new ArgumentException("유한한 z 칸의 면적 합이 0 입니다");
        }
        double[] zs = NpStats.Select(z, ok);
        double[] ws = NpStats.Select(a, ok);
        if (edges is null)
        {
            if (bins < 1)
            {
                throw new ArgumentException($"bins 는 1 이상이어야 합니다: {bins}");
            }
            double lo = zs.Min();
            double hi = zs.Max();
            if (hi <= lo)
            {
                hi = lo + 1.0;
            }
            edges = NpGrid.Linspace(lo, hi, bins + 1);
        }
        else
        {
            if (edges.Length < 2)
            {
                throw new ArgumentException("bins 배열은 길이 2 이상의 오름차순 칸 경계여야 합니다");
            }
            for (int i = 1; i < edges.Length; i++)
            {
                if (!(edges[i] - edges[i - 1] > 0))
                {
                    throw new ArgumentException("bins 배열은 길이 2 이상의 오름차순 칸 경계여야 합니다");
                }
            }
        }
        double[] h = SciPy.Histogram(zs, ws, edges);
        for (int i = 0; i < h.Length; i++)
        {
            h[i] /= total;
        }
        return (edges, h);
    }

    /// <summary>
    /// 고도 분포의 두 봉우리 (bimodality). 반환: peaks (낮은 것부터, 최대 2개), separation_m, ok, n_peaks.
    /// </summary>
    public static OrderedDictionary<string, object?> Bimodality(
        double[] z, double[] area, double binM = BimodalBinM, double smoothM = BimodalSmoothM,
        double minSeparationM = BimodalMinSeparationM, double minProminence = BimodalMinProminence)
    {
        foreach ((string name, double v) in new[] { ("bin_m", binM), ("smooth_m", smoothM) })
        {
            if (!(double.IsFinite(v) && v > 0.0))
            {
                throw new ArgumentException($"{name} 는 0 보다 커야 합니다: {v}");
            }
        }
        if (!(minProminence >= 0.0 && minProminence < 1.0))
        {
            throw new ArgumentException($"min_prominence 는 [0, 1) 이어야 합니다: {minProminence}");
        }
        bool[] ok = Array.ConvertAll(z, double.IsFinite);
        if (Array.IndexOf(ok, true) < 0)
        {
            throw new ArgumentException("z 에 유한한 값이 하나도 없습니다");
        }
        double[] zs = NpStats.Select(z, ok);
        double pad = 4.0 * smoothM;
        double lo = Math.Floor((zs.Min() - pad) / binM) * binM;
        double hi = Math.Ceiling((zs.Max() + pad) / binM) * binM;
        int nb = Math.Max((int)Math.Round((hi - lo) / binM), 1);
        double[] edges = new double[nb + 1];
        for (int i = 0; i <= nb; i++)
        {
            edges[i] = lo + (binM * i);
        }
        (_, double[] frac) = HypsometryHist(z, area, edges: edges);
        double[] dens = SciPy.GaussianFilter1DConstant(frac, smoothM / binM);
        double top = dens.Max();
        if (!(top > 0.0))
        {
            return new OrderedDictionary<string, object?>
            {
                ["peaks"] = new List<object?>(),
                ["separation_m"] = 0.0,
                ["ok"] = false,
                ["n_peaks"] = 0L,
            };
        }
        double[] padded = new double[dens.Length + 2];
        Array.Copy(dens, 0, padded, 1, dens.Length);
        (int[] idx, double[] prom) = SciPy.FindPeaks(padded, minProminence * top);
        for (int i = 0; i < idx.Length; i++)
        {
            idx[i] -= 1;
        }
        // np.argsort(-prominences, kind="stable")[:2]
        int[] order = Enumerable.Range(0, idx.Length).OrderBy(k => -prom[k]).ThenBy(k => k).Take(2).ToArray();
        var peaks = order.Select(k => 0.5 * (edges[idx[k]] + edges[idx[k] + 1])).OrderBy(v => v).ToList();
        double sep = peaks.Count == 2 ? peaks[^1] - peaks[0] : 0.0;
        return new OrderedDictionary<string, object?>
        {
            ["peaks"] = peaks.Cast<object?>().ToList(),
            ["separation_m"] = sep,
            ["ok"] = peaks.Count == 2 && sep > minSeparationM,
            ["n_peaks"] = (long)idx.Length,
        };
    }

    /// <summary>두 고도 분포의 면적 가중 누적 분포 최대 차이 (hypsometry_distance, KS 거리).</summary>
    public static double HypsometryDistance(double[] zA, double[] areaA, double[] zB, double[] areaB)
    {
        static (double[] Z, double[] C) CdfParts(double[] z, double[] area)
        {
            double[] a = Area(area, z.Length);
            bool[] ok = Array.ConvertAll(z, double.IsFinite);
            double s = NpStats.SumWhere(a, ok);
            if (Array.IndexOf(ok, true) < 0 || !(s > 0.0))
            {
                throw new ArgumentException("유한한 고도 칸이 없거나 면적 합이 0 입니다");
            }
            double[] zs = NpStats.Select(z, ok);
            double[] ws = NpStats.Select(a, ok);
            int[] k = Enumerable.Range(0, zs.Length).OrderBy(i => zs[i]).ThenBy(i => i).ToArray();
            double[] zz = new double[k.Length];
            double[] c = new double[k.Length];
            double acc = 0.0;
            for (int i = 0; i < k.Length; i++)
            {
                zz[i] = zs[k[i]];
                acc += ws[k[i]];
                c[i] = acc / s;
            }
            return (zz, c);
        }
        (double[] za, double[] ca) = CdfParts(zA, areaA);
        (double[] zb, double[] cb) = CdfParts(zB, areaB);
        double[] grid = za.Concat(zb).Distinct().OrderBy(v => v).ToArray();
        double best = 0.0;
        foreach (double g in grid)
        {
            int ia = SciPy.SearchSorted(za, g, true);
            int ib = SciPy.SearchSorted(zb, g, true);
            double fa = ia == 0 ? 0.0 : ca[ia - 1];
            double fb = ib == 0 ? 0.0 : cb[ib - 1];
            best = Math.Max(best, Math.Abs(fa - fb));
        }
        return best;
    }

    /// <summary>바다 칸의 면적 비율 (ocean_fraction).</summary>
    public static double OceanFraction(bool[] isOcean, double[] area)
    {
        double[] a = Area(area, isOcean.Length);
        bool[] all = new bool[isOcean.Length];
        Array.Fill(all, true);
        return WeightedFraction(isOcean, all, a);
    }

    /// <summary>대륙붕 넓이 (shelf_area): (넓이 [m²], 바다 넓이 대비 비율).</summary>
    public static (double Area, double Fraction) ShelfArea(double[] z, double[] area, long[] crustType, bool[] isOcean, double depth = 200.0)
    {
        if (!(double.IsFinite(depth) && depth > 0.0))
        {
            throw new ArgumentException($"depth 는 0 보다 커야 합니다: {depth}");
        }
        int n = z.Length;
        double[] a = Area(area, n);
        bool[] shelf = new bool[n];
        for (int c = 0; c < n; c++)
        {
            shelf[c] = isOcean[c] && crustType[c] == 1 && z[c] > -depth;
        }
        double s = NpStats.SumWhere(a, shelf);
        double ocean = NpStats.SumWhere(a, isOcean);
        return (s, ocean > 0.0 ? s / ocean : double.NaN);
    }

    /// <summary>평탄지 비율 (flat_fraction): 육지 가운데 |경사| &lt; threshold 인 넓이 비율.</summary>
    public static double FlatFraction(double[] slope, bool[] landMask, double threshold = 0.02, double[]? area = null)
    {
        int n = slope.Length;
        double[] a = Area(area, n);
        bool[] use = new bool[n];
        bool[] hit = new bool[n];
        for (int c = 0; c < n; c++)
        {
            use[c] = landMask[c] && double.IsFinite(slope[c]);
            hit[c] = Math.Abs(slope[c]) < threshold;
        }
        return WeightedFraction(hit, use, a);
    }

    /// <summary>지하수가 땅 겉 tol 안에 닿는 육지 비율 (gw_surface_fraction).</summary>
    public static double GwSurfaceFraction(double[] z, double[] zGw, bool[] landMask, double tol = 0.5, double[]? area = null)
    {
        if (!(double.IsFinite(tol) && tol >= 0.0))
        {
            throw new ArgumentException($"tol 은 0 이상이어야 합니다: {tol}");
        }
        int n = z.Length;
        double[] a = Area(area, n);
        bool[] use = new bool[n];
        bool[] hit = new bool[n];
        for (int c = 0; c < n; c++)
        {
            use[c] = landMask[c] && double.IsFinite(z[c]) && double.IsFinite(zGw[c]);
            hit[c] = z[c] - zGw[c] <= tol;
        }
        return WeightedFraction(hit, use, a);
    }
}
