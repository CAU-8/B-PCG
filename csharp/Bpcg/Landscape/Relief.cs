using System;
using Bpcg.Core;
using Bpcg.Numerics;

namespace Bpcg.Landscape;

/// <summary>
/// 아격자 기복 보정 (src/bpcg/landscape/relief.py, docs/pipeline.md 7.3). 계산은 칸마다 독립이라 칸 단위로 씁니다.
/// </summary>
public static class Relief
{
    public const int NQuadrature = 32;
    private const double Km = 1.0e3;
    private const double Km2 = 1.0e6;

    /// <summary>하천 시작 면적 A* = (k_s/S_crit)^(1/θ) / R_eff [m²] (channel_head_area). 하나라도 0 이하이면 0.</summary>
    public static double[] ChannelHeadArea(double[] kS, double[] sCrit, double[] runoffEff, Config cfg)
    {
        double theta = cfg.F("landscape.theta");
        if (!(theta > 0.0))
        {
            throw new ArgumentException($"landscape.theta 는 0 보다 커야 합니다: {theta}");
        }
        double inv = 1.0 / theta;
        double[] o = new double[kS.Length];
        for (int c = 0; c < o.Length; c++)
        {
            if (kS[c] > 0.0 && sCrit[c] > 0.0 && runoffEff[c] > 0.0)
            {
                o[c] = NpMath.Power(kS[c] / sCrit[c], inv) / runoffEff[c];
            }
        }
        return o;
    }

    /// <summary>칸 안 하천 기복 ∫ k_s·(R_eff·A(x))^(−θ) dx [m] (river_relief, 로그 간격 32점 사다리꼴).</summary>
    public static double[] RiverRelief(double[] kS, double[] runoffEff, double[] aStar, double[] cellArea, Config cfg)
    {
        double theta = cfg.F("landscape.theta");
        double cH = cfg.F("relief.hack_coefficient_km");
        double h = cfg.F("relief.hack_exponent");
        if (!(cH > 0.0 && h > 0.0))
        {
            throw new ArgumentException($"relief.hack_coefficient_km, hack_exponent 는 0 보다 커야 합니다: {cH}, {h}");
        }
        double invH = 1.0 / h;
        double[] o = new double[kS.Length];
        for (int c = 0; c < o.Length; c++)
        {
            bool ok = aStar[c] > 1.0 && aStar[c] < cellArea[c] && kS[c] > 0.0 && runoffEff[c] > 0.0
                && double.IsFinite(aStar[c]) && double.IsFinite(kS[c]);
            if (!ok)
            {
                continue;
            }
            double ks = kS[c];
            double r = runoffEff[c];
            double xStar = cH * NpMath.Power(aStar[c] / Km2, h);
            double xC = cH * NpMath.Power(cellArea[c] / Km2, h);
            double du = Math.Log(xC / xStar) / (NQuadrature - 1);
            double total = 0.0;
            double gPrev = 0.0;
            for (int k = 0; k < NQuadrature; k++)
            {
                double x = xStar * Math.Exp(du * k);
                double areaM2 = NpMath.Power(x / cH, invH) * Km2;
                double gK = ks * NpMath.Power(r * areaM2, -theta) * (x * Km);
                if (k > 0)
                {
                    total += 0.5 * (gK + gPrev) * du;
                }
                gPrev = gK;
            }
            o[c] = total;
        }
        return o;
    }

    /// <summary>사면 기복 L_h·min(S_crit, U·L_h/D)/2 [m], L_h = 0.5·sqrt(min(A*, 칸 면적)) (hillslope_relief).</summary>
    public static double[] HillslopeRelief(double[] sCrit, double[] uplift, double[] aStar, double[] cellArea, Config cfg)
    {
        double diff = cfg.F("landscape.hillslope_diffusivity_m2_per_yr");
        if (!(diff >= 0.0))
        {
            throw new ArgumentException($"landscape.hillslope_diffusivity_m2_per_yr 는 0 이상이어야 합니다: {diff}");
        }
        double[] o = new double[sCrit.Length];
        for (int c = 0; c < o.Length; c++)
        {
            double lH = 0.5 * Math.Sqrt(Math.Min(aStar[c], cellArea[c]));
            double u = Math.Max(uplift[c], 0.0);
            double sDiff = diff > 0.0 ? u * lH / diff : double.PositiveInfinity;
            double s = Math.Min(Math.Max(sCrit[c], 0.0), sDiff);
            s = u > 0.0 ? s : 0.0;
            o[c] = lH * s / 2.0;
        }
        return o;
    }

    /// <summary>L0 칸 안의 아격자 기복과 평균 지표 (subgrid_relief): (relief_m 바다 0, z_mean_m = z + mean_fraction·relief).</summary>
    public static (double[] Relief, double[] ZMean) SubgridRelief(
        CellGraph graph, double[] z, double[] kS, double[] sCrit, double[] runoffEff, double[] uplift, bool[] isOcean, Config cfg)
    {
        int n = graph.NCells;
        foreach ((string name, double[] a) in new[] { ("z", z), ("k_s", kS), ("s_crit", sCrit), ("runoff_eff", runoffEff), ("uplift", uplift) })
        {
            if (a.Length != n)
            {
                throw new ArgumentException($"{name} 은 ({n},) 이어야 합니다: 길이 {a.Length}");
            }
        }
        if (isOcean.Length != n)
        {
            throw new ArgumentException($"is_ocean 은 ({n},) bool 배열이어야 합니다");
        }
        foreach ((string name, double[] a) in new[] { ("z", z), ("k_s", kS), ("s_crit", sCrit), ("runoff_eff", runoffEff), ("uplift", uplift) })
        {
            for (int c = 0; c < n; c++)
            {
                if (!isOcean[c] && !double.IsFinite(a[c]))
                {
                    throw new ArgumentException($"육지 칸의 {name} 에 NaN 이나 inf 가 있습니다");
                }
            }
        }
        double frac = cfg.F("relief.mean_fraction");
        double[] aStar = ChannelHeadArea(kS, sCrit, runoffEff, cfg);
        double[] river = RiverRelief(kS, runoffEff, aStar, graph.Area, cfg);
        double[] hill = HillslopeRelief(sCrit, uplift, aStar, graph.Area, cfg);
        double[] relief = new double[n];
        double[] zMean = new double[n];
        for (int c = 0; c < n; c++)
        {
            if (!isOcean[c])
            {
                relief[c] = river[c] + hill[c];
            }
            zMean[c] = z[c] + (frac * relief[c]);
        }
        return (relief, zMean);
    }
}
