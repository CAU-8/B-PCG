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

    /// <summary>
    /// 선상지·둑·호수·땅속으로 사라지는 개울 때문에 지나는 물이나 흙의 양이 솔버가 쓴 값보다 이 몫 넘게 바뀐 칸에 '정상상태 아님'을
    /// 답니다 (0.01 = 100 가운데 1).
    /// </summary>
    public const double LostFlowTolerance = 0.01;

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    // 지나는 양이 솔버가 쓴 값에서 LostFlowTolerance 넘게 바뀌었는지 (둘 다 0 이면 바뀌지 않음).
    private static bool FlowChanged(double now, double solver) => Math.Abs(now - solver) > LostFlowTolerance * Math.Abs(solver);

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

    /// <summary>
    /// 점수표 요약 (scorecard_summary): n_items, n_judged, n_failed, failed, n_errors, errors. errors 는 계산이 예외로 끝난
    /// 항목(종류와 상관없이)이고, 그 가운데 check·self 는 failed 에도 들어 있습니다.
    /// </summary>
    public static OrderedDictionary<string, object?> ScorecardSummary(OrderedDictionary<string, object?> card)
    {
        var judged = card.Where(kv => ((OrderedDictionary<string, object?>)kv.Value!)["pass"] is not null).ToList();
        var failed = judged.Where(kv => ((OrderedDictionary<string, object?>)kv.Value!)["pass"] is false)
            .Select(kv => kv.Key).OrderBy(k => k, PyJson.CodePointComparer.Instance).Cast<object?>().ToList();
        var errors = Scorecard.ComputeErrors(card).OrderBy(k => k, PyJson.CodePointComparer.Instance).Cast<object?>().ToList();
        return new OrderedDictionary<string, object?>
        {
            ["n_items"] = (long)card.Count,
            ["n_judged"] = (long)judged.Count,
            ["n_failed"] = (long)failed.Count,
            ["failed"] = failed,
            ["n_errors"] = (long)errors.Count,
            ["errors"] = errors,
        };
    }

    /// <summary>
    /// 기록 줄 끝에 붙이는 점수표 결과: "불합격 없음, 계산 실패 없음". 스튜디오 경고(progress.py WARNINGS)가 '계산 실패 N개'를
    /// 찾습니다.
    /// </summary>
    public static string ScorecardLogText(OrderedDictionary<string, object?> summary) =>
        $"불합격 {LogText.Failed((List<object?>)summary["failed"]!)}, 계산 실패 {LogText.Failed((List<object?>)summary["errors"]!)}";

    /// <summary>
    /// 지표를 고친 뒤의 물길 (receiver, 하류가 먼저 오는 순서). 고친 칸(fan), 출구, 옛 수신 셀이 더는 낮지 않은 칸만
    /// 채운 지표에서 가장 가파른 이웃으로 바꾸고, 나머지는 솔버가 정한 수신 셀을 그대로 둡니다.
    /// </summary>
    public static (long[] Receiver, long[] Order) RerouteReceivers(
        CellGraph graph, double[] z, bool[] isOutlet, long[] receiver, bool[] fan, Config cfg)
    {
        int n = graph.NCells;
        double[] zt = Depressions.FillEpsilon(z, graph.Nbr, isOutlet, cfg.F("landscape.fill_epsilon_m"));
        (long[] steep, _, _) = Routing.D8Receivers(zt, graph.Nbr, graph.Dist, isOutlet);
        long[] rcv = new long[n];
        for (int c = 0; c < n; c++)
        {
            long old = receiver[c];
            bool keep = !fan[c] && !isOutlet[c] && old != c && zt[old] < zt[c];
            rcv[c] = keep ? old : steep[c];
        }
        return (rcv, Routing.TopoOrder(rcv));
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
        (long[] rcv, long[] order) = RerouteReceivers(graph, z, isOutlet, receiver, fan, cfg);
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
        int? maxIter = null, double[]? zInit = null, double[]? pet = null)
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
        // 산사태 둑과 메운 흙 (히어로 유역만): 둑이 있을 기대 수를 솟는 속도와 골짜기 모양에서 계산해 뽑고, 둑과 선상지 뒤의
        // 웅덩이를 강이 실어 온 흙으로 메웁니다. 덜 메워진 둑 뒤에만 호수가 남고, 호수는 4단계에서 정합니다.
        DamResult damResult = Dams.Apply(graph, z, res, u, rEff, isOutlet, cfg, fan);
        z = damResult.Z;
        bool[] dam = damResult.Dam;
        bool[] valleyFill = damResult.Fill;
        List<object?> dams = damResult.Sites;
        // 우묵한 자리: 행성 지도에서 땅이 가라앉는 자리와 빙하기의 얼음이 긁은 자리를 낮춰 물이 고일 자리를 만듭니다
        // (물이 고이는 칸만 낮춘 채로 둠). 얼음이 덮였던 땅은 솔버 지형의 칸 평균 높이에서 잰 기온으로 가립니다.
        LakeSettings? lakeSettings = Hollows.Settings(cfg);
        BasinCauses? causes = null;
        if (sphere && lakeSettings?.LgmCoolingC is not null)
        {
            (_, double[] zMean0) = Relief.SubgridRelief(graph, z, res.KS, res.SCritSurface, rEff, u, ocean, cfg);
            double[] lat0 = Climate.LatitudeRad(graph, cfg);
            causes = new BasinCauses(res.Receiver, res.Order, u, Climate.SurfaceTemperature(lat0, zMean0, cfg), p, lat0);
        }
        BasinResult basins = Hollows.AddBasins(graph, z, isOutlet, res.SedimentFlux, cfg, causes);
        z = basins.Z;
        bool[] hollow = basins.Hollow;
        // 작은 웅덩이 메우기: 가장 깊은 곳이 lakes.min_depth_m 에 못 미치거나 넓이가 lakes.min_area_m2 에 못 미치는 웅덩이는
        // 호수로 두지 않고 흙으로 메웁니다.
        bool[] pitFilled = new bool[n];
        int nPitsFilled = 0;
        if (lakeSettings is not null && (lakeSettings.MinDepthM > 0.0 || lakeSettings.MinAreaM2 > 0.0))
        {
            (z, pitFilled, nPitsFilled, _) = Hollows.FillShallow(
                graph, z, isOutlet, lakeSettings.MinDepthM, lakeSettings.MinAreaM2, cfg.F("landscape.fill_epsilon_m"));
        }
        // 호수 물수지: 들어오는 물보다 수면에서 증발하는 물이 많은 분지는 넘치지 못합니다. 그런 호수의 칸과 마른 분지의 바닥은
        // 물길이 끝나는 칸이 됩니다 (그 아래 옛 물길은 물이 줄어 '정상상태 아님').
        LakeBalanceResult? balance = null;
        bool[] routeOutlet = isOutlet;
        if (lakeSettings is not null && lakeSettings.Evaporation && pet is not null)
        {
            // 물길은 뒤에서 쓸 것과 같은 규칙으로 정합니다 (고친 칸만 다시, 나머지는 솔버의 수신 셀).
            bool[] touched = new bool[n];
            for (int c = 0; c < n; c++)
            {
                touched[c] = fan[c] || dam[c] || valleyFill[c] || hollow[c] || pitFilled[c];
            }
            double[] zBed = z;
            balance = LakeBalance.Solve(
                graph, z, isOutlet, rGw, p, Vector(pet, n, "pet"), inflow,
                outlet => RerouteReceivers(graph, zBed, outlet, res.Receiver, touched, cfg));
            if (Array.IndexOf(balance.Sink, true) >= 0)
            {
                routeOutlet = new bool[n];
                for (int c = 0; c < n; c++)
                {
                    routeOutlet[c] = isOutlet[c] || balance.Sink[c];
                }
            }
        }
        // 선상지·둑·메운 흙이 쌓아 올린 두께. 흙 단계가 이 두께를 쌓인 흙(충적층)으로 봅니다.
        double[] fanRaise = new double[n];
        bool[] raised = new bool[n];
        bool anyMoved = apexes.Count > 0 || dams.Count > 0 || !ReferenceEquals(routeOutlet, isOutlet);
        for (int c = 0; c < n; c++)
        {
            fanRaise[c] = Math.Max(z[c] - res.Z[c], 0.0);
            raised[c] = fan[c] || dam[c] || valleyFill[c] || hollow[c] || pitFilled[c];
            notSteady[c] |= dam[c] || valleyFill[c] || hollow[c] || pitFilled[c];
            anyMoved |= valleyFill[c] || hollow[c] || pitFilled[c];
        }
        long nRerouted = 0;
        long[] rcv;
        long[] order;
        double[] q;
        double[] aUp;
        double[] qs;
        double[] slope;
        if (anyMoved)
        {
            FieldSet rr = RerouteAfterFans(graph, z, routeOutlet, res.Receiver, raised, rEff, u, cfg, inflow);
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
                // 솔버가 경사를 정할 때 쓴 값이 뒤에서 바뀐 칸은 솔버의 균형(정상상태)에서 벗어납니다. 물을 보낼 칸이 바뀐 칸,
                // 물을 받는 칸의 높이가 바뀐 칸(낮춘 호수 바닥이나 흙으로 메운 칸으로 흘러드는 칸), 지나는 물이나 흙의 양이
                // 1% 넘게 바뀐 칸(닫힌 호수가 물을 가둔 아래쪽 옛 물길, 선상지·둑이 물길을 돌려 물이 불어난 칸)입니다.
                notSteady[c] |= changed
                    || z[rcv[c]] != res.Z[rcv[c]]
                    || FlowChanged(q[c], res.Discharge[c])
                    || FlowChanged(qs[c], res.SedimentFlux[c]);
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
        string basinText = causes is null
            ? ""
            : $"(가라앉는 땅 {LogText.N(basins.NTectonic)}개, 얼음이 긁은 땅 {LogText.N(basins.NGlacial)}개)";
        string balanceText = balance is null
            ? ""
            : $", 넘치지 못하는 호수 {LogText.N(balance.NClosed)}개, 마른 분지 {LogText.N(balance.NDry)}개";
        Say(log, $"[3단계] 선상지 {apexes.Count}개, 흙을 쌓아 올린 칸 {LogText.N(fan.Count(b => b))}개, 산사태 둑 {dams.Count}개"
            + $"(이 가운데 흙이 다 메우지 못한 둑 {damResult.Diag["n_lakes"]}개), 흙이 메운 칸 {LogText.N(valleyFill.Count(b => b))}개, "
            + $"낮춘 칸 {LogText.N(hollow.Count(b => b))}개{basinText}, 메운 작은 웅덩이 {LogText.N(nPitsFilled)}개{balanceText}, "
            + $"물길이 바뀐 칸 {LogText.N(nRerouted)}개");

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
        double[]? zr = null;
        if (sphere)
        {
            // L0 칸은 골짜기 창보다 커서 칸 안의 능선 높이 z + 기복을 '골짜기 가장자리'로 씁니다.
            zr = new double[n];
            for (int c = 0; c < n; c++)
            {
                zr[c] = z[c] + reliefArr![c];
            }
        }
        FieldSet wb = Water.WaterBodies(graph, z, rcv, order, q, ocean, cfg, valleyZ: zr, closedLevel: balance?.Level);
        sec["water"] = Seconds(t);

        // --- 지표 암석
        t = Stopwatch.GetTimestamp();
        byte[] rock = Model.SurfaceRock(columns, z);
        sec["surface_rock"] = Seconds(t);

        // --- 석회암 땅에서 땅속으로 사라지는 개울 (히어로 유역만): 지하수면보다 높이 떠 있는 작은 개울은 물을 잃고, 그 물은
        //     큰 강가에서 다시 솟습니다. 유량이 바뀌므로 강·호수를 다시 정합니다. 지형은 그대로라 물이 준 골짜기는 마른 골짜기로 남습니다.
        t = Stopwatch.GetTimestamp();
        KarstStreamResult? karst = null;
        if (!sphere)
        {
            double[] made = new double[n];
            for (int c = 0; c < n; c++)
            {
                made[c] = (graph.Area[c] * rEff[c]) + (inflow is null ? 0.0 : inflow[c]);
            }
            karst = KarstStreams.Apply(
                graph, z, rcv, order, made, q, wb.Get<bool>("is_river"), wb.Get<bool>("is_lake"), ocean,
                wb.Get<double>("water_level_m"), rock, u, columns, cfg);
            if (karst is not null)
            {
                for (int c = 0; c < n; c++)
                {
                    // 솔버가 쓴 물의 양에서 벗어난 칸 (사라진 아래쪽의 마른 골짜기, 솟은 아래쪽의 불어난 강).
                    notSteady[c] |= FlowChanged(karst.Discharge[c], q[c]);
                }
                q = karst.Discharge;
                wb = Water.WaterBodies(graph, z, rcv, order, q, ocean, cfg, valleyZ: zr, closedLevel: balance?.Level);
            }
        }
        sec["karst_streams"] = Seconds(t);

        // --- 흙과 충적층
        t = Stopwatch.GetTimestamp();
        FieldSet soil = Soil.SoilAndAlluvium(z, slope, res.SCritSurface, u, temp, p, q, qs, fanRaise, rock, cfg, ocean, graph);
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
        (double[] zGw, double[] zGwLower, double[] baseLevel, OrderedDictionary<string, object?> gwDiag) = Groundwater.WaterTable(
            graph, z, wb.Get<double>("water_level_m"), isWater, rock, slope, rGw, aUp, columns, cfg, isOcean: ocean,
            karstBase: karst?.Big);
        sec["groundwater"] = Seconds(t);

        // --- 동굴 층 (녹는 암석 안의 물 높이인 아래 지하수면에 맞춤)
        t = Stopwatch.GetTimestamp();
        // 위층 높이 = 기준 물의 수면 + k·f_k·골짜기 깊이. 히어로에서는 수면(baseLevel)을 가장 가까운 큰 물(karst.Big)에서
        // 가져오므로, 골짜기 깊이도 같은 큰 물의 값을 씁니다. 필드 valley_depth_m 은 가장 가까운 강(크기와 상관없음)의
        // 값이라, 그대로 쓰면 수면과 골짜기 깊이가 서로 다른 강에서 와서 '마른 옛 강 높이'가 가까운 개울보다 낮아집니다.
        double[] valleyDepth = wb.Get<double>("valley_depth_m");
        if (karst?.Big is bool[] bigWater && Array.IndexOf(bigWater, true) >= 0)
        {
            (_, _, valleyDepth) = Distance.NearestSourceValues(graph, bigWater, valleyDepth);
        }
        FieldSet caves = Caves.CaveLevels(graph, z, zGwLower, baseLevel, valleyDepth, columns, cfg);
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
        output["dam"] = dam;
        output["temperature_c"] = temp;
        if (causes is not null)
        {
            output["glaciated"] = basins.Ice;
        }
        output.Update(wb);
        if (balance is not null)
        {
            output["lake_closed"] = balance.Closed;
        }
        if (karst is not null)
        {
            output["karst_exchange_m3_per_yr"] = karst.Exchange;
        }
        output["surface_rock"] = rock;
        output.Update(soil);
        output["water_table_m"] = zGw;
        output["water_table_lower_m"] = zGwLower;
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
            ["dams"] = new OrderedDictionary<string, object?>(damResult.Diag)
            {
                ["n_raised"] = (long)dam.Count(b => b),
                ["sites"] = dams,
            },
            ["hollows"] = new OrderedDictionary<string, object?>
            {
                ["n_lowered"] = (long)hollow.Count(b => b),
                ["n_lowered_subsiding"] = (long)basins.NTectonic,
                ["n_lowered_ice_scoured"] = (long)basins.NGlacial,
                ["n_shallow_pits_filled"] = (long)nPitsFilled,
                ["n_shallow_cells_filled"] = (long)pitFilled.Count(b => b),
                ["n_open_lakes"] = balance is null ? null : (long)balance.NOpen,
                ["n_closed_lakes"] = balance is null ? null : (long)balance.NClosed,
                ["n_dry_basins"] = balance is null ? null : (long)balance.NDry,
                ["n_closed_lake_cells"] = balance is null ? null : (long)balance.Closed.Count(b => b),
            },
            ["water"] = new OrderedDictionary<string, object?>
            {
                ["n_river_cells"] = (long)isRiver.Count(b => b),
                ["n_lake_cells"] = (long)isLake.Count(b => b),
                ["n_river_segments"] = (long)rivers.Count,
            },
            ["groundwater"] = gwDiag,
            ["karst_streams"] = new OrderedDictionary<string, object?>
            {
                ["n_sinks"] = karst is null ? 0L : (long)karst.NSinks,
                ["n_springs"] = karst is null ? 0L : (long)karst.NSprings,
                ["lost_m3_per_yr"] = karst is null ? 0.0 : karst.LostM3PerYr,
                ["passes"] = karst is null ? 0L : (long)karst.Passes,
                ["settled"] = karst is null || karst.Settled,
            },
            ["caves"] = cavesDiag,
            ["z_land_max_m"] = anyLand ? zLandMax : double.NaN,
        };
        string caveText = string.Join("·", caveCells.Select((kv, k) => $"{LogText.CaveLevel(k)} {LogText.N((long)kv.Value!)}칸"));
        string karstText = karst is null
            ? ""
            : $"땅속으로 사라지는 개울 {LogText.N(karst.NSinks)}곳(모두 {karst.LostM3PerYr / Constants.SecondsPerYear:F1} m³/s, 다시 솟는 샘 {LogText.N(karst.NSprings)}곳"
                + (karst.Settled ? "" : $", 물을 {karst.Passes}번 다시 모아도 자리를 못 잡아 사라진 물과 솟은 물의 합이 다를 수 있음") + "), ";
        Say(log, $"[4단계] 강 칸 {LogText.N(isRiver.Count(b => b))}개(강 구간 {LogText.N(rivers.Count)}개), 호수 칸 {LogText.N(isLake.Count(b => b))}개, "
            + $"{karstText}동굴 {caveText}, 2~4단계 {(double)sec["total"]!:F2} s");
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
            ["first_pass_solver"] = res.Diag(),
            ["first_pass_history"] = res.History.Cast<object?>().ToList(),
            ["first_pass_z_mean_max_m"] = anyLand ? zMax : 0.0,
            ["reduced_cells"] = reduced,
            ["seconds"] = Seconds(t),
        };
        // 첫 풀이가 반복 상한에 닿아 멈추면, 융기를 줄일 칸을 덜 풀렸을 수 있는 지형으로 고른 셈입니다. 최종 풀이의 수렴과 따로 알립니다
        // (스튜디오 progress.py 의 WARNINGS 가 '첫 풀이 … 수렴 못 함' 을 읽습니다).
        string firstPass = res.Converged
            ? $"첫 풀이 반복 {res.Iterations}번, 수렴함"
            : $"첫 풀이 반복 {res.Iterations}번, 반복 상한까지 수렴 못 함";
        Say(log, $"[2단계] 지각 세기 한계 {zLim:F0} m: {firstPass}, 첫 풀이의 평균 지표 최고 {(double)diag["first_pass_z_mean_max_m"]!:F0} m, "
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
            zSeafloor: (double[])info["bathymetry_m"]!, maxIter: SolverMaxIter(cfg, "planet"),
            pet: fields.GetFloat64("pet_m_per_yr"));
        fields.Update(st.Fields);
        st.Diag["strength_limit"] = limitDiag;
        sec["stages"] = Seconds(t);
        Fields.CheckFields(fields.Names);

        t = Stopwatch.GetTimestamp();
        var sd = (OrderedDictionary<string, object?>)st.Diag["solver"]!;
        var planetDiag = new Dictionary<string, object?> { ["converged"] = sd["converged"], ["iterations"] = sd["iterations"] };
        if (limitDiag.TryGetValue("first_pass_converged", out object? fpc))
        {
            planetDiag["first_pass_converged"] = fpc;
            planetDiag["first_pass_iterations"] = limitDiag["first_pass_iterations"];
        }
        // 점수표의 법칙 검사(law_consistency)는 솔버가 푼 설정으로 다시 계산해야 맞습니다. L0 은 cfgL0
        // (landscape.deposition_g 를 deposition_g_l0 으로 바꾼 설정)로 풀었으므로 같은 설정을 넘깁니다.
        OrderedDictionary<string, object?> card = Score(
            cfgL0, planetFields: fields, planetGraph: l0,
            diag: new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["planet"] = planetDiag });
        sec["scorecard"] = Seconds(t);
        sec["total"] = Seconds(tAll);
        OrderedDictionary<string, object?> summary = ScorecardSummary(card);
        Say(log, $"[점수표] 항목 {summary["n_items"]}개 가운데 합격·불합격을 가리는 것 {summary["n_judged"]}개, {ScorecardLogText(summary)}");
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
        string how = Finder.RequestedSite(cfg) is (double lat, double lon)
            ? $", 설정 hero.site_lat_lon_deg [{lat:F2}, {lon:F2}] 에서 가장 가까운 칸"
            : "";
        Say(log, $"[히어로] 후보: L0 칸 {site.L0Cell}, 위도 {site.LatDeg:F2}°, 경도 {site.LonDeg:F2}°, 점수 {site.Score:F3} ({parts}){how}, {tFind:F2} s");
        HeroState hero = Refine.RefineHero(planet, site, cfg, log);
        if (!hero.Diag.TryGetValue("seconds", out object? s) || s is not OrderedDictionary<string, object?>)
        {
            hero.Diag["seconds"] = new OrderedDictionary<string, object?>();
        }
        ((OrderedDictionary<string, object?>)hero.Diag["seconds"]!)["find"] = tFind;
        return hero;
    }
}
