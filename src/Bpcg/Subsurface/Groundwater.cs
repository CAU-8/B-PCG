using System;
using System.Collections.Generic;
using System.Diagnostics;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Numerics;

namespace Bpcg.Subsurface;

/// <summary>
/// 지하수면: 물 빠지는 곳까지 거리 + 띠 대수층 Dupuit 해 (docs/pipeline.md 8.3).
/// </summary>
/// <remarks>
/// 물이 흐르는 층은 둘입니다. 흙 가까운 층은 투수가 깊이에 따라 f 길이로 줄고(Fan 2013 보충자료),
/// 그 아래 기반암 층은 지표 암석의 투수(Gleeson 2011)로 rock_aquifer_m 두께를 흐릅니다.
/// 녹는 암석 안은 통로가 물을 빼서 지하수면이 가까운 강 높이에 거의 평평합니다.
/// </remarks>
public static class Groundwater
{
    public const double ShallowDepthM = 0.5;

    // 지하수 언덕 높이를 이분법으로 풀 때의 반복 수. 60번이면 구간이 2^60 분의 1로 줄어 float64 끝자리까지 갑니다.
    private const int MoundBisectionSteps = 60;

    // 경사 등급별 함양 계수 (WaterGAP, Döll & Fiedler 2008 표 A1): 경사 [m/m] 가 경계 이상이면 다음 계수.
    private static readonly double[] RechargeSlopeEdges = [0.02, 0.05, 0.08, 0.16, 0.30, 0.45];
    private static readonly double[] RechargeSlopeFactors = [1.0, 0.95, 0.90, 0.75, 0.60, 0.30, 0.15];

    /// <summary>경사 s [m/m] 의 함양 계수 (Döll &amp; Fiedler 2008 표 A1). 평지 1, 경사 0.45 이상 0.15.</summary>
    public static double RechargeSlopeFactor(double s)
    {
        int i = 0;
        while (i < RechargeSlopeEdges.Length && s >= RechargeSlopeEdges[i])
        {
            i++;
        }
        return RechargeSlopeFactors[i];
    }

    /// <summary>
    /// 물 빠지는 곳의 수면 hd 위로 지하수가 솟는 높이 H 를 풉니다: 2·H·T(H) = R·X, H ∈ [0, top].
    /// T(H) = kRock·(rockM + H) + kSurf·f·exp(−(top − H)/f) 는 H 가 클수록 커져 해가 하나입니다.
    /// top 에서도 왼쪽이 오른쪽보다 작으면 지하수가 지표로 스며 나오는 칸이라 top 을 돌려줍니다.
    /// </summary>
    internal static double MoundHeight(double rx, double top, double kRock, double rockM, double kSurf, double f)
    {
        if (!(rx > 0.0) || !(top > 0.0))
        {
            return 0.0;
        }
        double G(double h) => (2.0 * h * ((kRock * (rockM + h)) + (kSurf * f * Math.Exp(-(top - h) / f)))) - rx;
        if (G(top) <= 0.0)
        {
            return top;
        }
        double lo = 0.0;
        double hi = top;
        for (int i = 0; i < MoundBisectionSteps; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (G(mid) > 0.0)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }
        return 0.5 * (lo + hi);
    }

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

    // 얹힌 물이 녹는 층 바닥(아래 암석과 만나는 면) 위에 고이는 두께 [m]. 지하수면이 녹는 층 안에 오게 합니다.
    private const double ContactLiftM = 1.0;

    /// <summary>
    /// 얹힌 카르스트의 지하수면: 칸 c 기둥에서 바닥이 zKarst 보다 높고 zTable 보다 낮은 녹는 층 가운데 가장 낮은 층의
    /// 바닥 + 1 m (그 층이 1 m 보다 얇으면 층 가운데). 그런 층이 없으면 +∞.
    /// </summary>
    internal static double PerchedContact(LayerColumns columns, int c, double zKarst, double zTable)
    {
        int nl = columns.NLayers;
        double best = double.PositiveInfinity;
        for (int i = 0; i < nl; i++)
        {
            double bottom = columns.Bottom[(c * nl) + i];
            double top = i == 0 ? double.PositiveInfinity : columns.Bottom[(c * nl) + i - 1];
            if (top > bottom && bottom > zKarst && bottom < zTable && Rocks.Soluble[columns.Rock[(c * (nl + 1)) + i]])
            {
                best = Math.Min(best, bottom + Math.Min(ContactLiftM, 0.5 * (top - bottom)));
            }
        }
        return best;
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
    /// isWater 는 바다·호수·강 칸, drainageArea 는 상류 면적 [m²] 입니다. 상류 면적이 groundwater.drain_area_m2
    /// 이상인 물길 칸도 지하수가 빠지는 곳(수면 = 지표)으로 봅니다.
    /// 반환: (z_gw, 기준 수면 = 가장 가까운 바다·호수·강 칸의 수면 (없으면 NaN), diag).
    /// </summary>
    public static (double[] ZGw, double[] BaseLevel, OrderedDictionary<string, object?> Diag) WaterTable(
        CellGraph graph, double[] z, double[] waterLevel, bool[] isWater, byte[] surfaceRock, double[] slope,
        double[] runoff, double[] drainageArea, LayerColumns columns, Config cfg, double? gravity = null,
        bool[]? isOcean = null)
    {
        long t0 = Stopwatch.GetTimestamp();
        int n = graph.NCells;
        double[] zz = Water.AsVector(z, n, "z");
        bool[] water = Water.AsMask(isWater, n, "is_water");
        if (waterLevel.Length != n)
        {
            throw new ArgumentException($"water_level 은 ({n},) 배열이어야 합니다 (받은 길이: {waterLevel.Length})");
        }
        for (int c = 0; c < n; c++)
        {
            if (water[c] && !double.IsFinite(waterLevel[c]))
            {
                throw new ArgumentException($"물 칸 {c} 의 water_level 이 {waterLevel[c]} 입니다. 물 칸에는 유한한 수면이 있어야 합니다");
            }
        }
        if (surfaceRock.Length != n)
        {
            throw new ArgumentException($"surface_rock 은 ({n},) 배열이어야 합니다 (받은 길이: {surfaceRock.Length})");
        }
        if (columns.NCells != n)
        {
            throw new ArgumentException($"columns 의 칸 수 {columns.NCells} 가 그래프 칸 수 {n} 과 다릅니다");
        }
        double[] s = Array.ConvertAll(Water.AsVector(slope, n, "slope"), v => Math.Max(v, 0.0));
        double[] ro = Water.AsVector(runoff, n, "runoff", allowScalar: true);
        double[] aUp = Water.AsVector(drainageArea, n, "drainage_area");
        foreach (double v in ro)
        {
            if (v < 0.0)
            {
                throw new ArgumentException($"runoff 는 0 이상이어야 합니다 (받은 값: {v})");
            }
        }
        double fR = cfg.F("groundwater.recharge_fraction");
        double alpha = cfg.F("groundwater.fan_alpha_m");
        double beta = cfg.F("groundwater.fan_beta");
        double fMin = cfg.F("groundwater.fan_min_m");
        double logKSurf = cfg.F("groundwater.surface_log10_perm_m2");
        double rockM = cfg.F("groundwater.rock_aquifer_m");
        double drainArea = cfg.F("groundwater.drain_area_m2");
        double karstGrad = cfg.F("groundwater.karst_gradient");
        double maxDepth = cfg.F("groundwater.max_depth_m");
        if (!(fR >= 0.0 && fR <= 1.0 && alpha > 0.0 && beta >= 0.0 && fMin >= 0.0 && rockM >= 0.0
            && drainArea >= 0.0 && karstGrad >= 0.0 && maxDepth >= 0.0 && double.IsFinite(logKSurf)))
        {
            throw new ArgumentException(
                "groundwater 설정은 0 ≤ recharge_fraction ≤ 1, fan_alpha_m > 0, 나머지 길이·면적·기울기는 0 이상, "
                + "surface_log10_perm_m2 는 유한해야 합니다. 받은 값: "
                + $"recharge_fraction {fR}, fan_alpha_m {alpha}, fan_beta {beta}, fan_min_m {fMin}, rock_aquifer_m {rockM}, "
                + $"drain_area_m2 {drainArea}, karst_gradient {karstGrad}, max_depth_m {maxDepth}, surface_log10_perm_m2 {logKSurf}");
        }
        double g = gravity ?? Constants.Gravity(cfg.F("planet.radius_m"), cfg.F("planet.mean_density_kg_m3"));
        double[] kRock = HydraulicConductivity(surfaceRock, g);
        double kSurf = Math.Pow(10.0, logKSurf) * Constants.RhoWater * g / Constants.MuWater * Constants.SecondsPerYear;

        // 녹는 암석: 통로가 물을 빼서 지하수면이 큰 물(바다·호수·강) 높이에 거의 평평합니다.
        // 가까운 큰 물 수면 + karst_gradient × 거리 높이가 녹는 암석 안이고 지표보다 낮은 칸을 카르스트 칸으로 둡니다.
        // 기준은 가장 가까운 큰 물입니다. 동굴 위층도 같은 큰 물 수면에서 재므로 두 층이 같은 강에 짝지어집니다.
        (double[] deltaW, _, double[] baseLevel) = Distance.NearestSourceValues(graph, water, waterLevel);
        double[] zKarst = new double[n];
        for (int c = 0; c < n; c++)
        {
            zKarst[c] = baseLevel[c] + (karstGrad * deltaW[c]);
        }
        byte[] karstRock = Model.RockAt(columns.Bottom, columns.Rock, columns.NLayers, zKarst);
        bool[] karst = new bool[n];
        for (int c = 0; c < n; c++)
        {
            karst[c] = !water[c] && double.IsFinite(zKarst[c]) && Rocks.Soluble[karstRock[c]] && zKarst[c] < zz[c];
        }

        // 물 빠지는 곳: 바다·호수·강, 그리고 상류 면적이 문턱 이상인 물길 칸(수면 = 지표).
        // 물길 바닥 아래가 물이 빠지는 녹는 층(카르스트, 또는 바닥이 물길보다 낮은 얹힌 녹는 층)이면 그 물길은
        // 물을 땅속으로 잃는 쪽이라 빼냅니다.
        bool[] drain = new bool[n];
        double[] drainLevel = new double[n];
        Parallelism.For(0, n, c =>
        {
            drain[c] = water[c]
                || (aUp[c] >= drainArea && !karst[c] && !(PerchedContact(columns, c, zKarst[c], zz[c]) < zz[c]));
            drainLevel[c] = water[c] ? waterLevel[c] : zz[c];
        });
        (double[] delta, long[] src) = Distance.NearestSource(graph, drain);
        double[] l = DivideDistanceKernel(src, delta);

        double[] zGw = new double[n];
        bool[] seep = new bool[n];
        Parallelism.For(0, n, c =>
        {
            if (drain[c])
            {
                zGw[c] = drainLevel[c];
                return;
            }
            if (karst[c])
            {
                zGw[c] = Math.Max(zKarst[c], zz[c] - maxDepth);
                return;
            }
            double lo = zz[c] - maxDepth;
            long sc = src[c];
            if (sc < 0)
            {
                zGw[c] = lo;
                return;
            }
            double hd = drainLevel[sc];
            double top = Math.Max(zz[c] - hd, 0.0);
            double d = delta[c];
            double rx = fR * RechargeSlopeFactor(s[c]) * ro[c] * d * ((2.0 * l[c]) - d);
            double f = Math.Max(alpha / (1.0 + (beta * s[c])), fMin);
            double h = MoundHeight(rx, top, kRock[c], rockM, kSurf, f);
            seep[c] = h >= top && top > 0.0;
            double v = Math.Min(hd + h, zz[c]);
            // 얹힌 카르스트: 녹는 층이 큰 물 높이까지 내려가지 않고 다른 암석 위에 얹혀 있으면, 그 층의 물은 통로를 따라
            // 층 바닥(아래 암석과 만나는 면)까지 빠집니다. 지하수면이 그 녹는 층 안이나 위에 있으면 층 바닥으로 둡니다.
            double contact = PerchedContact(columns, c, zKarst[c], v);
            if (contact < v)
            {
                v = contact;
                seep[c] = false;
            }
            zGw[c] = v < lo ? lo : v;
        });

        double[] depth = new double[n];
        bool[] dry = new bool[n];
        bool[] shallow = new bool[n];
        bool[] dryShallow = new bool[n];
        int nDry = 0;
        int nClipSurf = 0;
        int nClipDeep = 0;
        int nWater = 0;
        int nDrain = 0;
        int nKarst = 0;
        int nSeep = 0;
        for (int c = 0; c < n; c++)
        {
            depth[c] = zz[c] - zGw[c];
            dry[c] = !water[c];
            shallow[c] = depth[c] <= ShallowDepthM;
            dryShallow[c] = dry[c] && shallow[c];
            nDrain += drain[c] ? 1 : 0;
            nKarst += karst[c] ? 1 : 0;
            nSeep += seep[c] ? 1 : 0;
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
            ["n_drain_cells"] = (long)nDrain,
            ["karst_fraction"] = (double)nKarst / Math.Max(n, 1),
            ["seepage_fraction"] = (double)nSeep / Math.Max(n, 1),
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
        return (zGw, baseLevel, diag);
    }
}
