using System;
using Bpcg.Core;
using Bpcg.Numerics;

namespace Bpcg.Subsurface;

/// <summary>
/// 흙과 충적층 두께 (src/bpcg/subsurface/soil.py, docs/pipeline.md 8.2).
/// </summary>
public static class Soil
{
    public const double SoilTempCoefPerC = 0.07;
    public const double SoilTempRefC = 15.0;
    public const double SoilPrecipRefMPerYr = 1.0;
    public const double SoilPrecipFactorMax = 2.0;
    public const double MinDenudationMPerYr = 1e-7;
    public const double AlluviumMaxSlope = 0.02;
    public const double AlluviumQLowM3PerYr = 1e6;
    public const double AlluviumQHighM3PerYr = 1e9;
    public const double AlluviumSlopeScale = 0.01;

    /// <summary>[0, 1] 로 자른 smoothstep 3t² − 2t³.</summary>
    public static double Smoothstep01(double x)
    {
        double t = NpMath.Clip(x, 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }

    /// <summary>흙 생산 상한 P₀ = min(cap, P_max·e^(0.07(T − 15))·min(2, P/1 m/yr)) [m/yr] (soil_production_max).</summary>
    public static double[] SoilProductionMax(double[] temperature, double[] precip, Config cfg)
    {
        double pMax = cfg.F("soil.production_max_m_per_yr");
        double cap = cfg.F("soil.production_max_cap_m_per_yr");
        if (!(pMax > 0.0 && cap > 0.0))
        {
            throw new ArgumentException(
                $"soil.production_max_m_per_yr, production_max_cap_m_per_yr 는 0 보다 커야 합니다: {pMax}, {cap}");
        }
        double[] o = new double[temperature.Length];
        for (int c = 0; c < o.Length; c++)
        {
            double p = Math.Max(precip[c], 0.0);
            double wet = Math.Min(SoilPrecipFactorMax, p / SoilPrecipRefMPerYr);
            o[c] = Math.Min(cap, pMax * Math.Exp(SoilTempCoefPerC * (temperature[c] - SoilTempRefC)) * wet);
        }
        return o;
    }

    /// <summary>
    /// 흙 두께·충적층 두께·맨 암반 (soil_and_alluvium). discharge·sedimentFlux 는 솔버 결과 Q·Qs.
    /// sCrit: (N,) 그 칸의 지표 암석이 버티는 가장 가파른 경사 [m/m] (솔버가 경사를 자른 값, SolverResult.SCritSurface).
    /// 반환: soil_thickness_m, alluvium_m, bare_rock.
    /// </summary>
    /// <remarks>
    /// 가파른 비탈의 맨 암반은 '경사 ≥ bare_slope_ratio × sCrit' 로 정합니다. 솔버가 경사를 sCrit 에서 자르므로,
    /// 고정된 문턱(예전 bare_slope = 0.8)과 견주면 문턱에 딱 걸린 칸들이 반올림 끝자리에 따라 갈려 깨알 무늬가 생깁니다.
    /// 비율로 견주면 문턱(0.9 × sCrit)이 칸들이 몰려 있는 값(sCrit)에서 떨어져 있어 그런 일이 없습니다.
    /// </remarks>
    public static FieldSet SoilAndAlluvium(
        double[] z, double[] slope, double[] sCrit, double[] uplift, double[] temperature, double[] precip,
        double[] discharge, double[] sedimentFlux, double[]? fanRaise, Array surfaceRock, Config cfg, bool[]? isOcean = null)
    {
        int n = z.Length;
        double[] s = Array.ConvertAll(Water.AsVector(slope, n, "slope"), v => Math.Max(v, 0.0));
        double[] u = Water.AsVector(uplift, n, "uplift");
        double[] t = Water.AsVector(temperature, n, "temperature");
        double[] p = Water.AsVector(precip, n, "precip");
        if (surfaceRock.Length != n)
        {
            throw new ArgumentException($"surface_rock 은 ({n},) 배열이어야 합니다");
        }
        double[] sc = Water.AsVector(sCrit, n, "s_crit");
        foreach (double v in sc)
        {
            if (!(v > 0.0))
            {
                throw new ArgumentException($"s_crit 은 0 보다 커야 합니다 (받은 값 {v}). 솔버 결과의 SCritSurface 를 그대로 넘기세요.");
            }
        }
        if (discharge.Length != n || sedimentFlux.Length != n)
        {
            throw new ArgumentException($"result 의 discharge·sediment_flux 모양이 칸 수 ({n},) 와 다릅니다");
        }
        for (int c = 0; c < n; c++)
        {
            if (!double.IsFinite(discharge[c]) || !double.IsFinite(sedimentFlux[c]))
            {
                throw new ArgumentException("result 의 discharge, sediment_flux 에 NaN 이나 inf 가 있습니다");
            }
        }
        double[] raise = fanRaise is null ? new double[n] : Water.AsVector(fanRaise, n, "fan_raise");
        foreach (double v in raise)
        {
            if (v < 0.0)
            {
                throw new ArgumentException("fan_raise 는 0 이상이어야 합니다 (선상지로 올린 높이)");
            }
        }
        bool[] land = new bool[n];
        for (int c = 0; c < n; c++)
        {
            land[c] = isOcean is null || !Water.AsMask(isOcean, n, "is_ocean")[c];
        }
        double h0 = cfg.F("soil.decay_depth_m");
        double cap = cfg.F("soil.thickness_cap_m");
        double bareRatio = cfg.F("soil.bare_slope_ratio");
        double aMax = cfg.F("soil.alluvium_max_m");
        if (!(h0 > 0.0 && cap >= 0.0 && bareRatio > 0.0 && bareRatio <= 1.0 && aMax >= 0.0))
        {
            throw new ArgumentException(
                "soil 설정은 decay_depth_m > 0, thickness_cap_m ≥ 0, 0 < bare_slope_ratio ≤ 1, alluvium_max_m ≥ 0 이어야 합니다. "
                + $"받은 값: {h0}, {cap}, {bareRatio}, {aMax}");
        }
        double gDep = cfg.F("landscape.deposition_g");
        double rRef = cfg.F("climate.runoff_ref_m_per_yr");
        double[] p0 = SoilProductionMax(t, p, cfg);
        double lnLow = Math.Log(AlluviumQLowM3PerYr);
        double lnSpan = Math.Log(AlluviumQHighM3PerYr) - lnLow;
        double[] soil = new double[n];
        double[] alluvium = new double[n];
        bool[] bare = new bool[n];
        for (int c = 0; c < n; c++)
        {
            double e = Math.Max(u[c], MinDenudationMPerYr);
            double hStar = p0[c] > e ? h0 * Math.Log(p0[c] / e) : 0.0;
            soil[c] = NpMath.Clip(hStar, 0.0, cap);
            if (s[c] >= bareRatio * sc[c] || !land[c])
            {
                soil[c] = 0.0;
            }
            bool posQ = discharge[c] > 0.0;
            double qSafe = posQ ? discharge[c] : 1.0;
            bool deposition = posQ && gDep * rRef * sedimentFlux[c] / qSafe > u[c] && s[c] < AlluviumMaxSlope;
            double lnq = (Math.Log(qSafe) - lnLow) / lnSpan;
            double shape = Smoothstep01(lnq) * (1.0 - Smoothstep01(s[c] / AlluviumSlopeScale));
            alluvium[c] = (deposition ? aMax * shape : 0.0) + raise[c];
            if (!land[c])
            {
                alluvium[c] = 0.0;
            }
            bare[c] = land[c] && soil[c] <= 0.0 && alluvium[c] <= 0.0;
        }
        return new FieldSet
        {
            ["soil_thickness_m"] = soil,
            ["alluvium_m"] = alluvium,
            ["bare_rock"] = bare,
        };
    }
}
