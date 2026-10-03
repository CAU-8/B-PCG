using System;
using Bpcg.Core;
using Bpcg.Numerics;

namespace Bpcg.Planet;

/// <summary>
/// 가벼운 기후: 위도 띠 강수, 기온, 잠재 증발산, Budyko 유출 (src/bpcg/planet/climate.py, docs/pipeline.md 4.5).
/// </summary>
/// <remarks>Budyko 의 tanh 는 numpy 자체 구현(<see cref="NpUfunc.Tanh"/>)을 씁니다(libm 과 1 ulp 다름).</remarks>
public static class Climate
{
    public const long StreamPrecipNoise = 331;
    public const double PrecipNoiseFrequency = 3.0;
    public const int PrecipNoiseOctaves = 5;

    /// <summary>칸마다 위도 φ = asin(p̂·axis) [rad] (latitude_rad). 평면 그래프는 latDeg [°] 가 필요합니다.</summary>
    public static double[] LatitudeRad(CellGraph graph, Config cfg, double? latDeg = null)
    {
        if (graph.Kind == "sphere")
        {
            if (latDeg is not null)
            {
                throw new ArgumentException("구면 그래프에서는 lat_deg 를 주지 않습니다 (자전축으로 계산)");
            }
            double[] axis = cfg.FArray("planet.axis");
            double norm = axis.Length == 3 ? LinAlg.Norm3(axis) : 0.0;
            if (axis.Length != 3 || !(norm > 0))
            {
                throw new ArgumentException("planet.axis 는 길이가 0 이 아닌 3차원 벡터여야 합니다");
            }
            double a0 = axis[0] / norm;
            double a1 = axis[1] / norm;
            double a2 = axis[2] / norm;
            double[] unit = graph.Unit();
            double[] phi = new double[graph.NCells];
            for (int c = 0; c < phi.Length; c++)
            {
                double d = (unit[c * 3] * a0) + (unit[(c * 3) + 1] * a1) + (unit[(c * 3) + 2] * a2);
                phi[c] = Math.Asin(NpMath.Clip(d, -1.0, 1.0));
            }
            return phi;
        }
        if (latDeg is not double lat)
        {
            throw new ArgumentException("평면 그래프에는 위도 lat_deg [°] 를 줘야 합니다");
        }
        if (!(-90.0 <= lat && lat <= 90.0))
        {
            throw new ArgumentException($"lat_deg 는 -90~90 이어야 합니다: {lat}");
        }
        double[] o = new double[graph.NCells];
        Array.Fill(o, NpMath.Radians(lat));
        return o;
    }

    /// <summary>기온 T = T_eq − (T_eq − T_pole)·sin²φ − lapse·max(z, 0) [°C] (surface_temperature). z 가 null 이면 0.</summary>
    public static double[] SurfaceTemperature(double[] latitude, double[]? z, Config cfg)
    {
        double tEq = cfg.F("climate.t_equator_c");
        double dT = tEq - cfg.F("climate.t_pole_c");
        double lapse = cfg.F("climate.lapse_rate_c_per_m");
        double[] t = new double[latitude.Length];
        for (int c = 0; c < t.Length; c++)
        {
            double s = Math.Sin(latitude[c]);
            t[c] = tEq - (dT * (s * s));
            if (z is not null)
            {
                t[c] -= lapse * Math.Max(z[c], 0.0);
            }
        }
        return t;
    }

    /// <summary>Budyko(1974) 유출 R = P − ET, ET = P·sqrt((PET/P)·tanh(P/PET)·(1 − e^(−PET/P))) [m/yr] (budyko_runoff).</summary>
    public static double[] BudykoRunoff(double[] precip, double[] pet)
    {
        double[] r = new double[precip.Length];
        for (int c = 0; c < r.Length; c++)
        {
            double p = precip[c];
            double phi = pet[c] / p;
            double et = p * Math.Sqrt(phi * NpUfunc.Tanh(1.0 / phi) * (1.0 - Math.Exp(-phi)));
            r[c] = NpMath.Clip(p - et, 0.0, p);
        }
        return r;
    }

    /// <summary>
    /// 가벼운 기후 필드 (generate_climate): precip_m_per_yr, temperature_c, pet_m_per_yr, runoff_m_per_yr, runoff_eff_m_per_yr.
    /// z: 해수면 기준 고도 [m] 또는 null(0), seed: 강수 노이즈 시드(null 이면 cfg.planet.seed).
    /// </summary>
    public static FieldSet GenerateClimate(CellGraph graph, Config cfg, double[]? z = null, long? seed = null, double? latDeg = null)
    {
        double[] phi = LatitudeRad(graph, cfg, latDeg);
        int n = graph.NCells;
        if (z is not null && z.Length != n)
        {
            throw new ArgumentException($"z 모양이 칸 수와 다릅니다: ({z.Length},), 기대 ({n},)");
        }
        long s = seed ?? cfg.I("planet.seed");
        double s1 = NpMath.Radians(cfg.F("climate.p_equator_sigma_deg"));
        double phiM = NpMath.Radians(cfg.F("climate.p_midlat_center_deg"));
        double s2 = NpMath.Radians(cfg.F("climate.p_midlat_sigma_deg"));
        double pBase = cfg.F("climate.p_base_m_per_yr");
        double pEq = cfg.F("climate.p_equator");
        double pMid = cfg.F("climate.p_midlat");
        double[] band = new double[n];
        for (int c = 0; c < n; c++)
        {
            double a = phi[c] / s1;
            double b = (Math.Abs(phi[c]) - phiM) / s2;
            band[c] = pBase + (pEq * Math.Exp(-(a * a))) + (pMid * Math.Exp(-(b * b)));
        }
        double eta = cfg.F("climate.p_noise");
        double[] precip;
        if (eta != 0.0)
        {
            double[] xi = Noise.Fbm3(
                Uplift.NoisePoints(graph, cfg.F("planet.radius_m")), Plates.SubSeed(s, StreamPrecipNoise),
                PrecipNoiseOctaves, frequency: PrecipNoiseFrequency);
            precip = new double[n];
            for (int c = 0; c < n; c++)
            {
                precip[c] = band[c] * Math.Exp((eta * xi[c]) - (0.5 * eta * eta));
            }
        }
        else
        {
            precip = (double[])band.Clone();
        }
        foreach (double v in precip)
        {
            if (!(v > 0))
            {
                throw new ArgumentException("강수가 0 이하인 칸이 있습니다 (p_base_m_per_yr 를 확인하세요)");
            }
        }
        double[] temp = SurfaceTemperature(phi, z, cfg);
        double petPer = cfg.F("climate.pet_per_degc_m_per_yr");
        double petBase = cfg.F("climate.pet_base_m_per_yr");
        double[] pet = new double[n];
        for (int c = 0; c < n; c++)
        {
            pet[c] = (petPer * Math.Max(temp[c], 0.0)) + petBase;
            if (!(pet[c] > 0))
            {
                throw new ArgumentException("잠재 증발산이 0 이하입니다 (pet_base_m_per_yr > 0 이어야 함)");
            }
        }
        double[] runoff = BudykoRunoff(precip, pet);
        double frac = cfg.F("climate.runoff_floor_fraction");
        double floor = cfg.F("climate.runoff_floor_m_per_yr");
        double[] runoffEff = new double[n];
        for (int c = 0; c < n; c++)
        {
            runoffEff[c] = Math.Max(Math.Max(runoff[c], frac * precip[c]), floor);
        }
        return new FieldSet
        {
            ["precip_m_per_yr"] = precip,
            ["temperature_c"] = temp,
            ["pet_m_per_yr"] = pet,
            ["runoff_m_per_yr"] = runoff,
            ["runoff_eff_m_per_yr"] = runoffEff,
        };
    }
}
