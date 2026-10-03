using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Numerics;

namespace Bpcg.Planet;

/// <summary>
/// 융기 속도 U, 깎인 두께, 해구-화산호 거리 (src/bpcg/planet/uplift.py, docs/pipeline.md 4.4).
/// </summary>
public static class Uplift
{
    public const long StreamOrogenNoise = 321;
    public const double CollisionWidthFactor = 1.5;
    public const double MinMountainUpliftMPerYr = 1e-9;
    public const double RiftMaxDistM = 50_000.0;
    public const double RiftWidthM = 30_000.0;
    public const double OrogenNoiseAmplitude = 0.25;
    public const int OrogenNoiseOctaves = 4;
    public const double OrogenNoiseWavelengthFactor = 2.0;

    private static readonly string[] Required =
        ["crust_type", "is_ocean", "convergence_kind", "subduction_side", "convergence_m_per_yr", "dist_convergent_m", "dist_divergent_m"];

    /// <summary>해구-화산호 거리 d_arc = h₁/tan θ₁ + (h_arc − h₁)/tan θ₂ [m] (arc_distance_m).</summary>
    public static double ArcDistanceM(Config cfg)
    {
        double h1 = cfg.F("uplift.slab_shallow_depth_m");
        double hArc = cfg.F("uplift.arc_slab_depth_m");
        double t1 = NpMath.Radians(cfg.F("uplift.slab_shallow_dip_deg"));
        double t2 = NpMath.Radians(cfg.F("uplift.slab_deep_dip_deg"));
        if (!(0.0 < t1 && t1 < Math.PI / 2 && 0.0 < t2 && t2 < Math.PI / 2))
        {
            throw new ArgumentException("섭입판 경사는 0~90° 사이여야 합니다");
        }
        if (!(0.0 <= h1 && h1 <= hArc))
        {
            throw new ArgumentException("0 ≤ slab_shallow_depth_m ≤ arc_slab_depth_m 이어야 합니다");
        }
        return (h1 / Math.Tan(t1)) + ((hArc - h1) / Math.Tan(t2));
    }

    /// <summary>노이즈를 읽을 점 (N, 3) (noise_points): 구면은 단위 벡터, 평면은 pos/R.</summary>
    public static double[] NoisePoints(CellGraph graph, double radiusM)
    {
        if (graph.Kind == "sphere")
        {
            return graph.Unit();
        }
        double[] p = new double[graph.Pos.Length];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = graph.Pos[i] / radiusM;
        }
        return p;
    }

    /// <summary>
    /// 융기 필드를 만듭니다 (generate_uplift): uplift_m_per_yr (바다 0), exhumation_m, dist_arc_m (위판 쪽만 d_arc).
    /// </summary>
    public static FieldSet GenerateUplift(CellGraph graph, FieldSet fields, Config cfg)
    {
        int n = graph.NCells;
        foreach (string name in Required)
        {
            if (!fields.Contains(name) || fields[name].Length != n)
            {
                throw new ArgumentException($"fields 에 (N,) 모양의 '{name}' 이 있어야 합니다");
            }
        }
        byte[] crustType = fields.Get<byte>("crust_type");
        bool[] isOcean = fields.Get<bool>("is_ocean");
        byte[] kind = fields.Get<byte>("convergence_kind");
        sbyte[] side = fields.Get<sbyte>("subduction_side");
        double[] conv = fields.GetFloat64("convergence_m_per_yr");
        double[] dConv = fields.GetFloat64("dist_convergent_m");
        double[] dDiv = fields.GetFloat64("dist_divergent_m");
        double w = cfg.F("uplift.orogen_width_m");
        if (!(w > 0))
        {
            throw new ArgumentException($"orogen_width_m 는 0 보다 커야 합니다: {w}");
        }
        double wCol = cfg.Sec("uplift").Contains("collision_width_m")
            ? cfg.F("uplift.collision_width_m")
            : CollisionWidthFactor * w;
        if (!(wCol > 0))
        {
            throw new ArgumentException($"collision_width_m 는 0 보다 커야 합니다: {wCol}");
        }
        double dArc = ArcDistanceM(cfg);
        double radius = cfg.F("planet.radius_m");
        double craton = cfg.F("uplift.craton_erosion_m_per_myr") * 1e-6;
        double of = cfg.F("uplift.orogen_factor");
        double cf = cfg.F("uplift.collision_factor");

        double[] uplift = new double[n];
        bool[] overriding = new bool[n];
        double[] mountain = new double[n];
        for (int c = 0; c < n; c++)
        {
            bool cont = crustType[c] == 1;
            uplift[c] = cont || !isOcean[c] ? craton : 0.0;
            double gamma = Math.Max(conv[c], 0.0);
            overriding[c] = side[c] == 1 && (kind[c] == Plates.KindOceanContinent || kind[c] == Plates.KindOceanOcean);
            if (overriding[c])
            {
                double t = (dConv[c] - dArc) / w;
                mountain[c] = of * gamma * Math.Exp(-(t * t));
            }
            if (kind[c] == Plates.KindCollision)
            {
                double t = dConv[c] / wCol;
                mountain[c] += cf * gamma * Math.Exp(-(t * t));
            }
        }
        // 가우스 꼬리의 아주 작은 값은 0 으로 둡니다(뒤 단계 거듭제곱에서 NaN 이 되지 않게).
        var active = new List<int>();
        for (int c = 0; c < n; c++)
        {
            if (mountain[c] < MinMountainUpliftMPerYr)
            {
                mountain[c] = 0.0;
            }
            if (mountain[c] > 0.0)
            {
                active.Add(c);
            }
        }
        if (active.Count > 0)
        {
            double[] all = NoisePoints(graph, radius);
            double[] pts = new double[active.Count * 3];
            for (int t = 0; t < active.Count; t++)
            {
                Array.Copy(all, active[t] * 3, pts, t * 3, 3);
            }
            double[] xi = Noise.Fbm3(
                pts, Plates.SubSeed(cfg.I("planet.seed"), StreamOrogenNoise), OrogenNoiseOctaves,
                frequency: radius / (OrogenNoiseWavelengthFactor * w));
            for (int t = 0; t < active.Count; t++)
            {
                mountain[active[t]] *= 1.0 + (OrogenNoiseAmplitude * xi[t]);
            }
        }
        double rs = cfg.F("uplift.rift_subsidence_m_per_yr");
        double dur = cfg.F("uplift.orogen_duration_myr");
        double capEx = cfg.F("uplift.exhumation_cap_m");
        double[] exhumation = new double[n];
        double[] distArc = new double[n];
        for (int c = 0; c < n; c++)
        {
            uplift[c] += mountain[c];
            if (crustType[c] == 1 && dDiv[c] < RiftMaxDistM)
            {
                double q = dDiv[c] / RiftWidthM;
                uplift[c] += rs * Math.Exp(-(q * q));
            }
            if (isOcean[c])
            {
                uplift[c] = 0.0;
            }
            exhumation[c] = NpMath.Clip(Math.Max(uplift[c], 0.0) * dur * 1e6, 0.0, capEx);
            distArc[c] = overriding[c] ? dArc : NpMath.NaN;
        }
        return new FieldSet
        {
            ["uplift_m_per_yr"] = uplift,
            ["exhumation_m"] = exhumation,
            ["dist_arc_m"] = distArc,
        };
    }
}
