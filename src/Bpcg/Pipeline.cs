using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Hero;
using Bpcg.Hydro;
using Bpcg.IO;
using Bpcg.Landscape;
using Bpcg.Metrics;
using Bpcg.Planet;
using Bpcg.Subsurface;

namespace Bpcg;

/// <summary>행성(L0) 결과 (PlanetState).</summary>
public sealed class PlanetState(CellGraph graph, FieldSet fields, LayerColumns columns, OrderedDictionary<string, object?> info, OrderedDictionary<string, object?> diag)
{
    public CellGraph Graph { get; } = graph;

    public FieldSet Fields { get; } = fields;

    public LayerColumns Columns { get; } = columns;

    public OrderedDictionary<string, object?> Info { get; } = info;

    public OrderedDictionary<string, object?> Diag { get; } = diag;
}

/// <summary>히어로(L2) 결과 (HeroState).</summary>
public sealed class HeroState(
    CellGraph graph, FieldSet fields, LayerColumns columns, HeroSite? site, List<long[]> rivers, List<object?> fanApexes,
    OrderedDictionary<string, object?> diag)
{
    public CellGraph Graph { get; } = graph;

    public FieldSet Fields { get; } = fields;

    public LayerColumns Columns { get; } = columns;

    public HeroSite? Site { get; } = site;

    public List<long[]> Rivers { get; } = rivers;

    public List<object?> FanApexes { get; } = fanApexes;

    public OrderedDictionary<string, object?> Diag { get; } = diag;
}

/// <summary>run_stages_2_to_4 의 결과 (StageResult).</summary>
public sealed class StageResult(FieldSet fields, List<long[]> rivers, List<object?> fanApexes, SolverResult solver, OrderedDictionary<string, object?> diag)
{
    public FieldSet Fields { get; } = fields;

    public List<long[]> Rivers { get; } = rivers;

    public List<object?> FanApexes { get; } = fanApexes;

    public SolverResult Solver { get; } = solver;

    public OrderedDictionary<string, object?> Diag { get; } = diag;
}

/// <summary>
/// 생성 파이프라인: 1단계 재료 → 2~4단계 → 히어로 (src/bpcg/pipeline.py, docs/pipeline.md 1·9·13장).
/// </summary>
public static class Pipeline
{
    public const int SolverLogEvery = 10;

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    internal static void Say(Action<string>? log, string msg) => log?.Invoke(msg);

    private static Action<string>? Every(Action<string>? log, int every)
    {
        if (log is null)
        {
            return null;
        }
        int count = 0;
        return msg =>
        {
            count++;
            if (count == 1 || count % every == 0)
            {
                log(msg);
            }
        };
    }

    /// <summary>기존 profile.compute.numba_threads 를 C# 병렬 커널의 최대 실행 수로 씁니다 (0 = 자동).</summary>
    /// <remarks>기존 설정 이름을 유지해 Python 설정과 하위 호환합니다.</remarks>
    public static int ApplyThreads(Config cfg)
    {
        Section profile = cfg.Sec("profile");
        long want = 0;
        if (profile.Contains("compute") && profile.Sec("compute").Contains("numba_threads"))
        {
            want = profile.I("compute.numba_threads");
        }
        if (want < 0)
        {
            throw new ArgumentException($"profile.compute.numba_threads 는 0 이상이어야 합니다: {want}");
        }
        int top = Environment.ProcessorCount;
        int threads = want == 0 ? top : (int)Math.Min(want, top);
        Parallelism.Configure(threads);
        return threads;
    }

    private static double[] Vector(double[] x, int n, string name)
    {
        if (x.Length == 1 && n != 1)
        {
            double[] o = new double[n];
            Array.Fill(o, x[0]);
            return o;
        }
        if (x.Length != n)
        {
            throw new ArgumentException($"{name} 는 ({n},) 배열 또는 스칼라여야 합니다: 길이 {x.Length}");
        }
        return x;
    }

    /// <summary>솔버 반복 상한 (solver_max_iter). level "hero" 는 profile.hero.max_flow_iterations 가 있으면 그 값.</summary>
    public static int SolverMaxIter(Config cfg, string level)
    {
        int b = (int)cfg.I("landscape.max_flow_iterations");
        if (level == "hero")
        {
            Section profile = cfg.Sec("profile");
            if (profile.Contains("hero") && profile.Sec("hero").Contains("max_flow_iterations"))
            {
                return (int)profile.I("hero.max_flow_iterations");
            }
        }
        return b;
    }

    /// <summary>점수표를 부릅니다 (score).</summary>
    public static OrderedDictionary<string, object?> Score(
        Config cfg, FieldSet? planetFields = null, CellGraph? planetGraph = null, FieldSet? heroFields = null,
        CellGraph? heroGraph = null, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? diag = null) =>
        Scorecard.Build(planetFields, planetGraph, heroFields, heroGraph, diag, cfg);

    /// <summary>점수표 요약: n_items, n_judged, n_failed, failed (scorecard_summary).</summary>
    public static OrderedDictionary<string, object?> ScorecardSummary(OrderedDictionary<string, object?> card)
    {
        var judged = card.Where(kv => ((OrderedDictionary<string, object?>)kv.Value!)["pass"] is not null).ToList();
        var failed = judged.Where(kv => ((OrderedDictionary<string, object?>)kv.Value!)["pass"] is false)
            .Select(kv => kv.Key).OrderBy(k => k, PyJson.CodePointComparer.Instance).Cast<object?>().ToList();
        return new OrderedDictionary<string, object?>
        {
            ["n_items"] = (long)card.Count,
            ["n_judged"] = (long)judged.Count,
            ["n_failed"] = (long)failed.Count,
            ["failed"] = failed,
        };
    }

    /// <summary>
    /// 선상지를 얹은 뒤 물길만 다시 계산합니다 (reroute_after_fans). 반환: receiver, order, discharge_m3_per_yr,
    /// drainage_area_m2, sediment_flux_m3_per_yr, slope.
    /// </summary>
    public static FieldSet RerouteAfterFans(
        CellGraph graph, double[] z, bool[] isOutlet, long[] receiver, bool[] fan, double[] runoffEff, double[] uplift,
        Config cfg, double[]? extraInflow = null)
    {
        int n = graph.NCells;
        z = Vector(z, n, "z");
        runoffEff = Vector(runoffEff, n, "runoff_eff");
        uplift = Vector(uplift, n, "uplift");
        if (isOutlet.Length != n || fan.Length != n)
        {
            throw new ArgumentException($"is_outlet, fan 은 ({n},) bool 배열이어야 합니다");
        }
        if (receiver.Length != n || receiver.Any(r => r < 0 || r >= n))
        {
            throw new ArgumentException($"receiver 는 0..{n - 1} 범위의 ({n},) 정수 배열이어야 합니다");
        }
        double[] zt = Depressions.FillEpsilon(z, graph.Nbr, isOutlet, cfg.F("landscape.fill_epsilon_m"));
        (long[] steep, _, _) = Routing.D8Receivers(zt, graph.Nbr, graph.Dist, isOutlet);
        long[] rcv = new long[n];
        for (int c = 0; c < n; c++)
        {
            long old = receiver[c];
            bool keep = !fan[c] && !isOutlet[c] && old != c && zt[old] < zt[c];
            rcv[c] = keep ? old : steep[c];
        }
        long[] order = Routing.TopoOrder(rcv);
        double[] weight = new double[n];
        double[] sedW = new double[n];
        for (int c = 0; c < n; c++)
        {
            weight[c] = extraInflow is null ? graph.Area[c] * runoffEff[c] : (graph.Area[c] * runoffEff[c]) + extraInflow[c];
            sedW[c] = uplift[c] * graph.Area[c];
        }
        double[] q = AccumulateModule.Accumulate(rcv, order, weight);
        double[] aUp = AccumulateModule.Accumulate(rcv, order, graph.Area);
        double[] qs = AccumulateModule.Accumulate(rcv, order, sedW);
        double[] slope = new double[n];
        for (int c = 0; c < n; c++)
        {
            qs[c] = Math.Max(qs[c], 0.0);
            double d = double.PositiveInfinity;
            for (int s = 0; s < CellGraph.NSlots; s++)
            {
                if (graph.Nbr[(c * CellGraph.NSlots) + s] == rcv[c])
                {
                    d = Math.Min(d, graph.Dist[(c * CellGraph.NSlots) + s]);
                }
            }
            slope[c] = double.IsFinite(d) ? Math.Max(z[c] - z[rcv[c]], 0.0) / d : 0.0;
        }
        return new FieldSet
        {
            ["receiver"] = rcv,
            ["order"] = order,
            ["discharge_m3_per_yr"] = q,
            ["drainage_area_m2"] = aUp,
            ["sediment_flux_m3_per_yr"] = qs,
            ["slope"] = slope,
        };
    }

    /// <summary>
    /// 2~4단계를 한 경로로 돌립니다 (run_stages_2_to_4). zOutlet·temperatureSeaC·runoff 는 길이 1(스칼라) 또는 (N,).
    /// </summary>
    public static StageResult RunStages2To4(
        CellGraph graph, bool[] isOutlet, double[] zOutlet, double[] uplift, double[] runoffEff, double[] precip,
        LayerColumns columns, Config cfg, double[]? extraInflow = null, double? latDeg = null, Action<string>? log = null,
        double[]? runoff = null, bool[]? isOcean = null, double[]? zSeafloor = null, double[]? temperatureSeaC = null,
        int? maxIter = null, double[]? zInit = null)
    {
        int n = graph.NCells;
        if (columns.NCells != n)
        {
            throw new ArgumentException($"columns 의 칸 수 {columns.NCells} 가 그래프 칸 수 {n} 와 다릅니다");
        }
        if (isOutlet.Length != n)
        {
            throw new ArgumentException($"is_outlet 은 ({n},) bool 배열이어야 합니다");
        }
        bool sphere = graph.Kind == "sphere";
        bool[] ocean;
        if (isOcean is null)
        {
            ocean = sphere ? (bool[])isOutlet.Clone() : new bool[n];
        }
        else
        {
            if (isOcean.Length != n)
            {
                throw new ArgumentException($"is_ocean 은 ({n},) bool 배열이어야 합니다");
            }
            for (int c = 0; c < n; c++)
            {
                if (isOcean[c] && !isOutlet[c])
                {
                    throw new ArgumentException("바다 칸은 모두 출구(is_outlet)여야 합니다");
                }
            }
            ocean = isOcean;
        }
        double[] u = Vector(uplift, n, "uplift");
        double[] rEff = Vector(runoffEff, n, "runoff_eff");
        double[] p = Vector(precip, n, "precip");
        foreach (double v in p)
        {
            if (!(double.IsFinite(v) && v > 0))
            {
                throw new ArgumentException("precip 는 0 보다 큰 유한한 값이어야 합니다");
            }
        }
        double[] rGw = runoff is null ? rEff : Vector(runoff, n, "runoff");
        double[]? inflow = extraInflow is null ? null : Vector(extraInflow, n, "extra_inflow");
        if (!sphere && temperatureSeaC is null && latDeg is null)
        {
            throw new ArgumentException("평면 그래프에는 lat_deg 나 temperature_sea_c 를 줘야 합니다 (기온)");
        }

        var sec = new OrderedDictionary<string, object?>();
        long tAll = Stopwatch.GetTimestamp();

        // --- 2단계: 정상상태 솔버
        long t = Stopwatch.GetTimestamp();
        Say(log, $"[2단계] 솔버 시작: 칸 {LogText.N(n)}개, 물이 빠지는 출구 칸 {LogText.N(isOutlet.Count(b => b))}개");
        SolverResult res = Solver.SolveSteadyState(
            graph, isOutlet, zOutlet, u, rEff, columns, cfg, zInit, inflow, maxIter, Every(log, SolverLogEvery));
        sec["solver"] = Seconds(t);
        if (res.UpliftEffective is not null)
        {
            u = res.UpliftEffective;
        }
        OrderedDictionary<string, object?> sd = res.Diag();
        // '수렴 못 함' 은 스튜디오 경고(progress.py WARNINGS)가 찾는 글자입니다.
        Say(log, $"[2단계] 솔버 끝: 반복 {sd["iterations"]}번, {((bool)sd["converged"]! ? "수렴함" : "반복 상한까지 수렴 못 함")}, "
            + $"방향을 묶은 칸 {LogText.N((long)sd["n_frozen"]!)}개, {(double)sec["solver"]!:F2} s");

        // --- 3단계: 선상지 → 물길만 다시
        t = Stopwatch.GetTimestamp();
        (double[] z, bool[] fan, bool[] notSteady, List<object?> apexes) = Fans.AddFans(graph, res.Z, res, cfg);
        double[] fanRaise = new double[n];
        for (int c = 0; c < n; c++)
        {
            fanRaise[c] = Math.Max(z[c] - res.Z[c], 0.0);
        }
        long nRerouted = 0;
        long[] rcv;
        long[] order;
        double[] q;
        double[] aUp;
        double[] qs;
        double[] slope;
        if (apexes.Count > 0)
        {
            FieldSet rr = RerouteAfterFans(graph, z, isOutlet, res.Receiver, fan, rEff, u, cfg, inflow);
            rcv = rr.Get<long>("receiver");
            order = rr.Get<long>("order");
            q = rr.Get<double>("discharge_m3_per_yr");
            aUp = rr.Get<double>("drainage_area_m2");
            qs = rr.Get<double>("sediment_flux_m3_per_yr");
            slope = rr.Get<double>("slope");
            for (int c = 0; c < n; c++)
            {
                bool changed = rcv[c] != res.Receiver[c];
                nRerouted += changed ? 1 : 0;
                notSteady[c] |= changed;
            }
        }
        else
        {
            rcv = res.Receiver;
            order = res.Order;
            q = res.Discharge;
            aUp = res.DrainageArea;
            qs = res.SedimentFlux;
            slope = res.Slope;
        }
        sec["fans"] = Seconds(t);
        Say(log, $"[3단계] 선상지 {apexes.Count}개, 흙을 쌓아 올린 칸 {LogText.N(fan.Count(b => b))}개, 물길이 바뀐 칸 {LogText.N(nRerouted)}개");

        if (zSeafloor is not null)
        {
            double[] zs = Vector(zSeafloor, n, "z_seafloor");
            z = (double[])z.Clone();
            for (int c = 0; c < n; c++)
            {
                if (ocean[c])
                {
                    if (!double.IsFinite(zs[c]))
                    {
                        throw new ArgumentException("바다 칸의 z_seafloor 에 NaN 이나 inf 가 있습니다");
                    }
                    z[c] = zs[c];
                }
            }
        }

        // --- 3단계: 기복 보정 (L0 만)
        var output = new FieldSet { ["uplift_m_per_yr"] = u };
        double[] zAir = z;
        double[]? reliefArr = null;
        if (sphere)
        {
            t = Stopwatch.GetTimestamp();
            (double[] relief, double[] zMean) = Relief.SubgridRelief(graph, z, res.KS, res.SCritSurface, rEff, u, ocean, cfg);
            output["relief_m"] = relief;
            output["z_mean_m"] = zMean;
            reliefArr = relief;
            zAir = zMean;
            sec["relief"] = Seconds(t);
        }

        // --- 최종 고도로 기온만 다시
        t = Stopwatch.GetTimestamp();
        double[] temp;
        if (temperatureSeaC is not null)
        {
            double[] tSea = Vector(temperatureSeaC, n, "temperature_sea_c");
            double lapse = cfg.F("climate.lapse_rate_c_per_m");
            temp = new double[n];
            for (int c = 0; c < n; c++)
            {
                temp[c] = tSea[c] - (lapse * Math.Max(zAir[c], 0.0));
            }
        }
        else
        {
            double[] lat = Climate.LatitudeRad(graph, cfg, sphere ? null : latDeg);
            temp = Climate.SurfaceTemperature(lat, zAir, cfg);
        }
        sec["climate_final"] = Seconds(t);

        // --- 4단계: 물
        t = Stopwatch.GetTimestamp();
        FieldSet wb = Water.WaterBodies(graph, z, rcv, order, q, ocean, cfg);
        if (sphere)
        {
            // L0 칸은 골짜기 창보다 커서 칸 안의 능선 높이 z + 기복을 '골짜기 가장자리'로 씁니다.
            double[] zr = new double[n];
            for (int c = 0; c < n; c++)
            {
                zr[c] = z[c] + reliefArr![c];
            }
            wb["valley_depth_m"] = Water.ValleyDepth(
                graph, zr, wb.Get<double>("water_level_m"), wb.Get<bool>("is_river"), cfg.F("caves.valley_window_m"));
        }
        sec["water"] = Seconds(t);

        // --- 지표 암석
        t = Stopwatch.GetTimestamp();
        byte[] rock = Model.SurfaceRock(columns, z);
        sec["surface_rock"] = Seconds(t);

        // --- 흙과 충적층
        t = Stopwatch.GetTimestamp();
        FieldSet soil = Soil.SoilAndAlluvium(z, slope, u, temp, p, q, qs, fanRaise, rock, cfg, ocean);
        sec["soil"] = Seconds(t);

        // --- 지하수면
        t = Stopwatch.GetTimestamp();
        bool[] isLake = wb.Get<bool>("is_lake");
        bool[] isRiver = wb.Get<bool>("is_river");
        bool[] isWater = new bool[n];
        for (int c = 0; c < n; c++)
        {
            isWater[c] = ocean[c] || isLake[c] || isRiver[c];
        }
        (double[] zGw, OrderedDictionary<string, object?> gwDiag) = Groundwater.WaterTable(
            graph, z, wb.Get<double>("water_level_m"), isWater, rock, slope, rGw, cfg, isOcean: ocean);
        sec["groundwater"] = Seconds(t);

        // --- 동굴 층
        t = Stopwatch.GetTimestamp();
        FieldSet caves = Caves.CaveLevels(graph, z, zGw, wb.Get<double>("valley_depth_m"), columns, cfg);
        sec["caves"] = Seconds(t);

        t = Stopwatch.GetTimestamp();
        List<long[]> rivers = Network.RiverSegments(rcv, order, isRiver);
        sec["rivers"] = Seconds(t);
        sec["total"] = Seconds(tAll);

        output["z_m"] = z;
        output["receiver"] = rcv;
        output["drainage_area_m2"] = aUp;
        output["discharge_m3_per_yr"] = q;
        output["sediment_flux_m3_per_yr"] = qs;
        output["slope"] = slope;
        output["k_s"] = res.KS;
        output["s_crit"] = res.SCritSurface;
        output["not_steady"] = notSteady;
        output["fan"] = fan;
        output["temperature_c"] = temp;
        output.Update(wb);
        output["surface_rock"] = rock;
        output.Update(soil);
        output["water_table_m"] = zGw;
        output.Update(caves);
        output.Set2D("strata_bottom_m", columns.Bottom, columns.NLayers);
        output.Set2D("strata_rock", columns.Rock, columns.NLayers + 1);
        Fields.CheckFields(output.Names);

        var caveCells = new OrderedDictionary<string, object?>();
        foreach (KeyValuePair<string, Array> kv in caves)
        {
            if (kv.Key.StartsWith("cave_l", StringComparison.Ordinal))
            {
                caveCells[kv.Key] = (long)((double[])kv.Value).Count(double.IsFinite);
            }
        }
        double zLandMax = double.NegativeInfinity;
        bool anyLand = false;
        for (int c = 0; c < n; c++)
        {
            if (!ocean[c])
            {
                anyLand = true;
                zLandMax = Math.Max(zLandMax, z[c]);
            }
        }
        var solverDiag = new OrderedDictionary<string, object?>(sd) { ["history"] = res.History.Cast<object?>().ToList() };
        var cavesDiag = new OrderedDictionary<string, object?>(caveCells)
        {
            ["n_entrance_cells"] = (long)caves.Get<byte>("cave_entrance").Count(b => b > 0),
        };
        var diag = new OrderedDictionary<string, object?>
        {
            ["seconds"] = sec,
            ["solver"] = solverDiag,
            ["fans"] = new OrderedDictionary<string, object?>
            {
                ["n_apexes"] = (long)apexes.Count,
                ["n_raised"] = (long)fan.Count(b => b),
                ["n_rerouted"] = nRerouted,
            },
            ["water"] = new OrderedDictionary<string, object?>
            {
                ["n_river_cells"] = (long)isRiver.Count(b => b),
                ["n_lake_cells"] = (long)isLake.Count(b => b),
                ["n_river_segments"] = (long)rivers.Count,
            },
            ["groundwater"] = gwDiag,
            ["caves"] = cavesDiag,
            ["z_land_max_m"] = anyLand ? zLandMax : double.NaN,
        };
        string caveText = string.Join("·", caveCells.Select((kv, k) => $"{LogText.CaveLevel(k)} {LogText.N((long)kv.Value!)}칸"));
        Say(log, $"[4단계] 강 칸 {LogText.N(isRiver.Count(b => b))}개(강 구간 {LogText.N(rivers.Count)}개), 호수 칸 {LogText.N(isLake.Count(b => b))}개, "
            + $"동굴 {caveText}, 2~4단계 {(double)sec["total"]!:F2} s");
        return new StageResult(output, rivers, apexes, res, diag);
    }

    /// <summary>L0 에서 쓰는 설정 (l0_config): landscape.deposition_g_l0 가 있으면 deposition_g 를 바꿉니다.</summary>
    public static Config L0Config(Config cfg)
    {
        if (!cfg.Sec("landscape").Contains("deposition_g_l0"))
        {
            return cfg;
        }
        object? g = cfg.Sec("landscape").Get("deposition_g_l0");
        if (g is null)
        {
            return cfg;
        }
        return cfg.WithOverrides([new("landscape.deposition_g", cfg.F("landscape.deposition_g_l0"))]);
    }

    /// <summary>지각 세기 한계로 줄인 융기 (strength_limited_uplift). 반환: (U', 첫 풀이 지형 또는 null, 진단).</summary>
    public static (double[] Uplift, double[]? ZStart, OrderedDictionary<string, object?> Diag) StrengthLimitedUplift(
        CellGraph graph, bool[] ocean, FieldSet fields, LayerColumns columns, Config cfg, Action<string>? log = null)
    {
        double[] u = fields.GetFloat64("uplift_m_per_yr");
        Section up = cfg.Sec("uplift");
        if (!up.Contains("elevation_limit_m") || up.Get("elevation_limit_m") is null)
        {
            return (u, null, new OrderedDictionary<string, object?> { ["applied"] = false });
        }
        double zLim = up.F("elevation_limit_m");
        double power = up.Contains("elevation_limit_power") ? up.F("elevation_limit_power") : 4.0;
        long t = Stopwatch.GetTimestamp();
        double[] reff = fields.GetFloat64("runoff_eff_m_per_yr");
        SolverResult res = Solver.SolveSteadyState(
            graph, ocean, [0.0], u, reff, columns, cfg, maxIter: SolverMaxIter(cfg, "planet"));
        (_, double[] zMean) = Relief.SubgridRelief(graph, res.Z, res.KS, res.SCritSurface, reff, u, ocean, cfg);
        double b = cfg.F("uplift.craton_erosion_m_per_myr") * 1e-6;
        int n = u.Length;
        double[] u2 = new double[n];
        long reduced = 0;
        double zMax = double.NegativeInfinity;
        bool anyLand = false;
        for (int c = 0; c < n; c++)
        {
            double taper = Numerics.NpMath.Clip(1.0 - Numerics.NpMath.Power(Math.Max(zMean[c], 0.0) / zLim, power), 0.0, 1.0);
            u2[c] = u[c] > 0.0 ? Math.Max(u[c] * taper, Math.Min(u[c], b)) : u[c];
            if (!ocean[c])
            {
                anyLand = true;
                zMax = Math.Max(zMax, zMean[c]);
                reduced += u2[c] < u[c] ? 1 : 0;
            }
        }
        var diag = new OrderedDictionary<string, object?>
        {
            ["applied"] = true,
            ["elevation_limit_m"] = zLim,
            ["first_pass_iterations"] = (long)res.Iterations,
            ["first_pass_converged"] = res.Converged,
            ["first_pass_z_mean_max_m"] = anyLand ? zMax : 0.0,
            ["reduced_cells"] = reduced,
            ["seconds"] = Seconds(t),
        };
        Say(log, $"[2단계] 지각 세기 한계 {zLim:F0} m: 첫 풀이의 평균 지표 최고 {(double)diag["first_pass_z_mean_max_m"]!:F0} m, "
            + $"융기를 줄인 칸 {LogText.N(reduced)}개, {(double)diag["seconds"]!:F1} s");
        return (u2, res.Z, diag);
    }

    /// <summary>행성 하나를 1~4단계로 만듭니다 (generate_planet). 진행 기록은 표준 출력.</summary>
    public static PlanetState GeneratePlanet(Config cfg) => GeneratePlanet(cfg, Console.WriteLine);

    /// <summary>행성 하나를 1~4단계로 만듭니다 (generate_planet, `bpcg planet`).</summary>
    public static PlanetState GeneratePlanet(Config cfg, Action<string>? log)
    {
        long tAll = Stopwatch.GetTimestamp();
        int threads = ApplyThreads(cfg);
        double r = cfg.F("planet.radius_m");
        int nC = (int)cfg.I("profile.grid.coarse_n_per_face");
        int nL0 = (int)cfg.I("profile.grid.l0_n_per_face");
        var sec = new OrderedDictionary<string, object?>();

        long t = Stopwatch.GetTimestamp();
        CellGraph coarse = Graph.SphereGraph(nC, r);
        (FieldSet coarseFields, OrderedDictionary<string, object?> coarseInfo) = Materials.BuildMaterials(coarse, cfg);
        sec["materials_coarse"] = Seconds(t);
        Say(log, $"[1단계] 거친 격자 면당 {nC} × {nC}칸: 바다 {LogText.Pct((double)coarseInfo["ocean_fraction"]!)}, {(double)sec["materials_coarse"]!:F2} s");

        t = Stopwatch.GetTimestamp();
        CellGraph l0 = Graph.SphereGraph(nL0, r, cfg.F("landscape.jitter"), cfg.I("planet.seed"));
        (FieldSet fields, OrderedDictionary<string, object?> info) = Materials.TransferMaterials(coarse, coarseFields, coarseInfo, l0, cfg);
        sec["transfer"] = Seconds(t);
        Say(log, $"[1단계] L0 면당 {nL0} × {nL0}칸 (모두 {LogText.N(l0.NCells)}칸): 바다 {LogText.Pct((double)info["ocean_fraction"]!)}, "
            + $"해수면 {(double)info["sea_level_m"]!:F0} m, {(double)sec["transfer"]!:F2} s");

        t = Stopwatch.GetTimestamp();
        (FieldSet geoFields, LayerColumns columns, OrderedDictionary<string, object?> geoDiag) =
            Model.GenerateGeology(fields, cfg, l0.Unit());
        fields.Update(geoFields);
        sec["geology"] = Seconds(t);
        string[] templateNames = ["탄산염 탁상지", "습곡충상대", "기반암 + 화산호"];
        string counts = string.Join(", ", ((List<object?>)geoDiag["template_counts"]!).Select((v, k) =>
            $"{(k < templateNames.Length ? templateNames[k] : $"{k}번")} {LogText.N(Convert.ToInt64(v, CultureInfo.InvariantCulture))}"));
        Say(log, $"[1단계] 지질 템플릿 칸 수: {counts}, {(double)sec["geology"]!:F2} s");

        bool[] ocean = fields.Get<bool>("is_ocean");
        Config cfgL0 = L0Config(cfg);
        t = Stopwatch.GetTimestamp();
        (double[] uplift, double[]? zStart, OrderedDictionary<string, object?> limitDiag) =
            StrengthLimitedUplift(l0, ocean, fields, columns, cfgL0, log);
        sec["strength_limit"] = Seconds(t);
        t = Stopwatch.GetTimestamp();
        StageResult st = RunStages2To4(
            l0, ocean, [0.0], uplift, fields.GetFloat64("runoff_eff_m_per_yr"), fields.GetFloat64("precip_m_per_yr"),
            columns, cfgL0, zInit: zStart, log: log, runoff: fields.GetFloat64("runoff_m_per_yr"), isOcean: ocean,
            zSeafloor: (double[])info["bathymetry_m"]!, maxIter: SolverMaxIter(cfg, "planet"));
        fields.Update(st.Fields);
        st.Diag["strength_limit"] = limitDiag;
        sec["stages"] = Seconds(t);
        Fields.CheckFields(fields.Names);

        t = Stopwatch.GetTimestamp();
        var sd = (OrderedDictionary<string, object?>)st.Diag["solver"]!;
        OrderedDictionary<string, object?> card = Score(
            cfg, planetFields: fields, planetGraph: l0,
            diag: new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["planet"] = new Dictionary<string, object?> { ["converged"] = sd["converged"], ["iterations"] = sd["iterations"] },
            });
        sec["scorecard"] = Seconds(t);
        sec["total"] = Seconds(tAll);
        OrderedDictionary<string, object?> summary = ScorecardSummary(card);
        Say(log, $"[점수표] 항목 {summary["n_items"]}개 가운데 합격·불합격을 가리는 것 {summary["n_judged"]}개, 불합격 {LogText.Failed((List<object?>)summary["failed"]!)}");
        Say(log, $"[행성] 끝: {(double)sec["total"]!:F2} s");

        object? plateInfo = coarseInfo.TryGetValue("plates", out object? pi) ? pi : new OrderedDictionary<string, object?>();
        var outInfo = new OrderedDictionary<string, object?>(info)
        {
            ["coarse"] = new OrderedDictionary<string, object?>
            {
                ["n_per_face"] = (long)nC,
                ["sea_level_m"] = coarseInfo["sea_level_m"],
                ["ocean_fraction"] = coarseInfo["ocean_fraction"],
                ["continental_fraction"] = coarseInfo["continental_fraction"],
            },
            ["plates"] = plateInfo,
            ["n_per_face"] = (long)nL0,
        };
        var secAll = new OrderedDictionary<string, object?>(sec) { ["stages_detail"] = st.Diag["seconds"] };
        var stages = new OrderedDictionary<string, object?>();
        foreach (KeyValuePair<string, object?> kv in st.Diag)
        {
            if (kv.Key is not "seconds" and not "solver")
            {
                stages[kv.Key] = kv.Value;
            }
        }
        var diag = new OrderedDictionary<string, object?>
        {
            ["seconds"] = secAll,
            ["solver"] = sd,
            ["geology"] = geoDiag,
            ["stages"] = stages,
            ["scorecard"] = card,
            ["scorecard_summary"] = summary,
            ["threads"] = (long)threads,
            ["n_river_segments"] = (long)st.Rivers.Count,
        };
        return new PlanetState(l0, fields, columns, outInfo, diag);
    }

    /// <summary>Python 의 문자열 목록 repr ['a', 'b'].</summary>
    internal static string PyListRepr(IEnumerable<object?> items) =>
        "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    /// <summary>히어로 유역(L2)을 만듭니다 (generate_hero). 진행 기록은 표준 출력.</summary>
    public static HeroState GenerateHero(Config cfg, PlanetState? planet = null) => GenerateHero(cfg, planet, Console.WriteLine);

    /// <summary>
    /// 히어로 유역(L2)을 만듭니다 (generate_hero, `bpcg hero`). planet 이 있으면 find_hero → refine_hero, null 이면 flat_hero.
    /// </summary>
    public static HeroState GenerateHero(Config cfg, PlanetState? planet, Action<string>? log)
    {
        ApplyThreads(cfg);
        if (planet is null)
        {
            return Flat.FlatHero(cfg, log);
        }
        long t = Stopwatch.GetTimestamp();
        HeroSite site = Finder.FindHero(planet, cfg);
        double tFind = Seconds(t);
        var partNames = new Dictionary<string, string>
        {
            ["uplift_gradient"] = "융기 기울기",
            ["carbonate"] = "지표 탄산염",
            ["relief"] = "기복",
            ["dry_fraction"] = "건조 칸 비율",
        };
        string parts = string.Join(", ", site.Parts.Select(kv => $"{partNames.GetValueOrDefault(kv.Key, kv.Key)} {kv.Value:F2}"));
        Say(log, $"[히어로] 후보: L0 칸 {site.L0Cell}, 위도 {site.LatDeg:F2}°, 경도 {site.LonDeg:F2}°, 점수 {site.Score:F3} ({parts}), {tFind:F2} s");
        HeroState hero = Refine.RefineHero(planet, site, cfg, log);
        if (!hero.Diag.TryGetValue("seconds", out object? s) || s is not OrderedDictionary<string, object?>)
        {
            hero.Diag["seconds"] = new OrderedDictionary<string, object?>();
        }
        ((OrderedDictionary<string, object?>)hero.Diag["seconds"]!)["find"] = tFind;
        return hero;
    }
}
