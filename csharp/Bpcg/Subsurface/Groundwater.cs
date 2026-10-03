using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Numerics;

namespace Bpcg.Subsurface;

/// <summary>
/// 지하수면: 물가 거리 + 띠 대수층 Dupuit 해 (src/bpcg/subsurface/groundwater.py, docs/pipeline.md 8.3).
/// </summary>
public static class Groundwater
{
    public const double ShallowDepthM = 0.5;

    private static double[] DivideDistanceKernel(long[] src, double[] delta)
    {
        int n = src.Length;
        double[] lmax = new double[n];
        for (int c = 0; c < n; c++)
        {
            long s = src[c];
            if (s >= 0 && delta[c] > lmax[s])
            {
                lmax[s] = delta[c];
            }
        }
        double[] o = new double[n];
        for (int c = 0; c < n; c++)
        {
            long s = src[c];
            o[c] = s >= 0 ? lmax[s] : NpMath.NaN;
        }
        return o;
    }

    /// <summary>수리전도도 K_h = 10^(log10 k)·ρ_w·g/μ × SECONDS_PER_YEAR [m/yr] (hydraulic_conductivity).</summary>
    public static double[] HydraulicConductivity(byte[] rock, double gravity)
    {
        if (!(double.IsFinite(gravity) && gravity > 0.0))
        {
            throw new ArgumentException($"gravity 는 0 보다 큰 유한한 값이어야 합니다: {gravity}");
        }
        double[] o = new double[rock.Length];
        for (int i = 0; i < o.Length; i++)
        {
            if (rock[i] >= Rocks.NRocks)
            {
                throw new ArgumentException($"암석 번호는 0..{Rocks.NRocks - 1} 정수여야 합니다");
            }
            double k = Math.Pow(10.0, Rocks.Log10Perm[rock[i]]);
            o[i] = k * Constants.RhoWater * gravity / Constants.MuWater * Constants.SecondsPerYear;
        }
        return o;
    }

    /// <summary>
    /// 지하수면 z_gw 를 닫힌 어림식으로 구합니다 (water_table). runoff 는 (N,) 또는 길이 1(스칼라).
    /// 반환: (z_gw, diag).
    /// </summary>
    public static (double[] ZGw, OrderedDictionary<string, object?> Diag) WaterTable(
        CellGraph graph, double[] z, double[] waterLevel, bool[] isWater, byte[] surfaceRock, double[] slope,
        double[] runoff, Config cfg, double? gravity = null, bool[]? isOcean = null)
    {
        long t0 = Stopwatch.GetTimestamp();
        int n = graph.NCells;
        double[] zz = Water.AsVector(z, n, "z");
        bool[] water = Water.AsMask(isWater, n, "is_water");
        if (waterLevel.Length != n)
        {
            throw new ArgumentException($"water_level 은 ({n},) 배열이어야 합니다");
        }
        for (int c = 0; c < n; c++)
        {
            if (water[c] && !double.IsFinite(waterLevel[c]))
            {
                throw new ArgumentException("물 칸의 water_level 에 NaN 이나 inf 가 있습니다");
            }
        }
        if (surfaceRock.Length != n)
        {
            throw new ArgumentException($"surface_rock 은 ({n},) 배열이어야 합니다");
        }
        double[] s = Array.ConvertAll(Water.AsVector(slope, n, "slope"), v => Math.Max(v, 0.0));
        double[] ro = Water.AsVector(runoff, n, "runoff", allowScalar: true);
        foreach (double v in ro)
        {
            if (v < 0.0)
            {
                throw new ArgumentException("runoff 는 0 이상이어야 합니다");
            }
        }
        double fR = cfg.F("groundwater.recharge_fraction");
        double alpha = cfg.F("groundwater.fan_alpha_m");
        double beta = cfg.F("groundwater.fan_beta");
        double maxDepth = cfg.F("groundwater.max_depth_m");
        if (!(fR >= 0.0 && fR <= 1.0 && alpha > 0.0 && beta >= 0.0 && maxDepth >= 0.0))
        {
            throw new ArgumentException(
                $"groundwater 설정은 0 ≤ recharge_fraction ≤ 1, fan_alpha_m > 0, fan_beta ≥ 0, max_depth_m ≥ 0 이어야 합니다: {fR}, {alpha}, {beta}, {maxDepth}");
        }
        double g = gravity ?? Constants.Gravity(cfg.F("planet.radius_m"), cfg.F("planet.mean_density_kg_m3"));
        double[] kH = HydraulicConductivity(surfaceRock, g);
        double[] trans = new double[n];
        double[] recharge = new double[n];
        for (int c = 0; c < n; c++)
        {
            double thickness = alpha / (1.0 + (beta * s[c]));
            trans[c] = kH[c] * thickness;
            recharge[c] = fR * ro[c];
        }
        (double[] delta, long[] src) = Distance.NearestSource(graph, water);
        double[] l = DivideDistanceKernel(src, delta);
        double[] zGw = new double[n];
        Parallel.For(0, n, c =>
        {
            if (water[c])
            {
                zGw[c] = waterLevel[c];
                return;
            }
            double lo = zz[c] - maxDepth;
            long sc = src[c];
            if (sc < 0)
            {
                zGw[c] = lo;
                return;
            }
            double d = delta[c];
            double hh = recharge[c] * d * ((2.0 * l[c]) - d) / (2.0 * trans[c]);
            double v = waterLevel[sc] + NpMath.PyMax(hh, 0.0);
            if (v > zz[c])
            {
                v = zz[c];
            }
            if (v < lo)
            {
                v = lo;
            }
            zGw[c] = v;
        });

        double[] depth = new double[n];
        bool[] dry = new bool[n];
        bool[] shallow = new bool[n];
        bool[] dryShallow = new bool[n];
        int nDry = 0;
        int nClipSurf = 0;
        int nClipDeep = 0;
        int nWater = 0;
        for (int c = 0; c < n; c++)
        {
            depth[c] = zz[c] - zGw[c];
            dry[c] = !water[c];
            shallow[c] = depth[c] <= ShallowDepthM;
            dryShallow[c] = dry[c] && shallow[c];
            if (dry[c])
            {
                nDry++;
                nClipSurf += depth[c] <= 0.0 ? 1 : 0;
                nClipDeep += depth[c] >= maxDepth ? 1 : 0;
            }
            else
            {
                nWater++;
            }
        }
        double[] area = graph.Area;
        double aDry = NpStats.SumWhere(area, dry);
        var diag = new OrderedDictionary<string, object?>
        {
            ["shallow_fraction"] = aDry > 0 ? NpStats.SumWhere(area, dryShallow) / aDry : double.NaN,
            ["shallow_fraction_with_water"] = null,
            ["median_depth_m"] = nDry > 0 ? NpStats.Median(NpStats.Select(depth, dry)) : double.NaN,
            ["clipped_surface_fraction"] = nDry > 0 ? (double)nClipSurf / nDry : 0.0,
            ["clipped_max_depth_fraction"] = nDry > 0 ? (double)nClipDeep / nDry : 0.0,
            ["n_water"] = (long)nWater,
            ["gravity_m_s2"] = g,
        };
        if (isOcean is not null)
        {
            bool[] ocean = Water.AsMask(isOcean, n, "is_ocean");
            bool[] land = Array.ConvertAll(ocean, o => !o);
            double aLand = NpStats.SumWhere(area, land);
            bool[] landWet = new bool[n];
            for (int c = 0; c < n; c++)
            {
                landWet[c] = land[c] && (shallow[c] || water[c]);
            }
            diag["shallow_fraction_with_water"] = aLand > 0 ? NpStats.SumWhere(area, landWet) / aLand : double.NaN;
        }
        diag["seconds"] = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        return (zGw, diag);
    }
}
