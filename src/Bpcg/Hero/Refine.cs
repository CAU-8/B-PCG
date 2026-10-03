using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Landscape;
using Bpcg.Numerics;

namespace Bpcg.Hero;

/// <summary>
/// 히어로 정밀화: L0 값과 경계조건으로 25 m 평면에서 2~4단계를 다시 풉니다 (src/bpcg/hero/refine.py).
/// </summary>
public static class Refine
{
    public const int OutletCells = 5;

    public static readonly string[] LinearFields =
        ["uplift_m_per_yr", "runoff_m_per_yr", "runoff_eff_m_per_yr", "precip_m_per_yr", "pet_m_per_yr", "exhumation_m", "fold_phase"];

    public static readonly string[] NearestFields = ["template_id"];

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    /// <summary>L0 필드를 구면 점 unit (M, 3) 에서 읽습니다 (sample_l0_fields).</summary>
    public static FieldSet SampleL0Fields(PlanetState planet, double[] unit)
    {
        int nL0 = (int)planet.Graph.Shape[1];
        FieldSet f = planet.Fields;
        var missing = LinearFields.Concat(NearestFields).Where(k => !f.Contains(k)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"planet.fields 에 히어로 표본에 필요한 필드가 없습니다: {Pipeline.PyListRepr(missing)}");
        }
        var output = new FieldSet();
        foreach (string name in LinearFields)
        {
            double[] v = Resample.SampleSphere(f.GetFloat64(name), nL0, unit);
            if (v.Any(x => !double.IsFinite(x)))
            {
                throw new ArgumentException($"L0 '{name}' 을 히어로로 보간한 값에 NaN 이나 inf 가 있습니다");
            }
            output[name] = v;
        }
        foreach (string name in NearestFields)
        {
            output[name] = Resample.SampleSphereNearest((byte[])FieldSet.CastTo(f[name], typeof(byte)), nL0, unit);
        }
        return output;
    }

    private static double UMax(PlanetState planet)
    {
        if (planet.Diag.TryGetValue("geology", out object? geo) && geo is OrderedDictionary<string, object?> g
            && g.TryGetValue("u_max_m_per_yr", out object? um))
        {
            return Convert.ToDouble(um, System.Globalization.CultureInfo.InvariantCulture);
        }
        double mx = double.NegativeInfinity;
        foreach (double v in planet.Fields.GetFloat64("uplift_m_per_yr"))
        {
            if (double.IsFinite(v))
            {
                mx = Math.Max(mx, Math.Max(v, 0.0));
            }
        }
        return mx;
    }

    /// <summary>히어로 경계조건 (hero_boundary).</summary>
    public static OrderedDictionary<string, object?> HeroBoundary(PlanetState planet, HeroSite site, CellGraph graph, double[] rEff, Config cfg)
    {
        CellGraph g0 = planet.Graph;
        FieldSet f0 = planet.Fields;
        (int nSide, double dx) = Domain.HeroGridSize(cfg);
        int n = graph.NCells;
        double r = cfg.F("planet.radius_m");
        long c0 = site.L0Cell;
        long[] rcv0 = (long[])FieldSet.CastTo(f0["receiver"], typeof(long));
        long r0 = rcv0[c0];
        if (r0 == c0)
        {
            throw new ArgumentException($"히어로 중심 L0 칸 {c0} 이 출구(바다)입니다");
        }
        double[] Dir(long a, long b) =>
            [g0.Pos[a * 3] - g0.Pos[b * 3], g0.Pos[(a * 3) + 1] - g0.Pos[(b * 3) + 1], g0.Pos[(a * 3) + 2] - g0.Pos[(b * 3) + 2]];
        EdgeHit hit = Domain.EdgeHitOf(Domain.DirectionToLocal(site, Dir(r0, c0)), nSide, dx);
        long[] outletCells = Domain.EdgeCells(nSide, hit.Edge, hit.Index, OutletCells);
        bool[] isOutlet = new bool[n];
        foreach (long c in outletCells)
        {
            isOutlet[c] = true;
        }
        double[] pOut = Domain.LocalToUnit(site, [hit.X], [hit.Y], r);
        double zOut = Resample.SampleSphere(f0.GetFloat64("z_m"), (int)g0.Shape[1], pOut)[0];
        if (!double.IsFinite(zOut))
        {
            throw new ArgumentException("출구 위치의 L0 고도가 NaN 입니다");
        }
        double[] q0All = f0.GetFloat64("discharge_m3_per_yr");
        double q0 = q0All[c0];
        double[] prod = new double[n];
        for (int c = 0; c < n; c++)
        {
            prod[c] = graph.Area[c] * rEff[c];
        }
        double qIn = q0 - NpReduce.Sum(prod);
        double[] extra = new double[n];
        long inflowCell = -1;
        string? inflowEdge = null;
        long donor = -1;
        if (qIn > 0.0)
        {
            var donors = new List<long>();
            for (int c = 0; c < rcv0.Length; c++)
            {
                if (rcv0[c] == c0 && c != c0)
                {
                    donors.Add(c);
                }
            }
            double[] direction;
            if (donors.Count > 0)
            {
                long bestD = donors[0];
                foreach (long dd in donors)
                {
                    if (q0All[dd] > q0All[bestD])
                    {
                        bestD = dd;
                    }
                }
                donor = bestD;
                direction = Dir(donor, c0);
            }
            else
            {
                direction = Dir(c0, r0);
            }
            EdgeHit hin = Domain.EdgeHitOf(Domain.DirectionToLocal(site, direction), nSide, dx);
            inflowCell = Domain.EdgeCells(nSide, hin.Edge, hin.Index, 1)[0];
            inflowEdge = hin.Edge;
            extra[inflowCell] = qIn;
        }
        return new OrderedDictionary<string, object?>
        {
            ["is_outlet"] = isOutlet,
            ["outlet_cells"] = outletCells,
            ["outlet_edge"] = hit.Edge,
            ["z_outlet_m"] = zOut,
            ["extra_inflow"] = extra,
            ["inflow_cell"] = inflowCell,
            ["inflow_edge"] = inflowEdge,
            ["inflow_m3_per_yr"] = NpReduce.Sum(extra),
            ["l0_discharge_m3_per_yr"] = q0,
            ["l0_receiver"] = r0,
            ["l0_donor"] = donor,
            ["inflow_at_outlet"] = inflowCell >= 0 && isOutlet[inflowCell],
        };
    }

    /// <summary>L0 경계조건으로 히어로 2~4단계를 풉니다 (refine_hero).</summary>
    public static HeroState RefineHero(PlanetState planet, HeroSite site, Config cfg, Action<string>? log = null)
    {
        long tAll = Stopwatch.GetTimestamp();
        var sec = new OrderedDictionary<string, object?>();

        long t = Stopwatch.GetTimestamp();
        (CellGraph graph, double[] unit) = Domain.HeroGraph(site, cfg);
        FieldSet sampled = SampleL0Fields(planet, unit);
        sec["sample"] = Seconds(t);
        int n = graph.NCells;
        Pipeline.Say(log, $"[히어로] 평면 {graph.Shape[0]}×{graph.Shape[1]} ({n}칸), L0 값 표본 끝");

        t = Stopwatch.GetTimestamp();
        byte[] tid = sampled.Get<byte>("template_id");
        double[] disp = Model.FoldDisplacementFromPhase(
            sampled.Get<double>("fold_phase"), sampled.Get<double>("uplift_m_per_yr"), tid, cfg, UMax(planet));
        (double[] sb, byte[] sr) = Model.BuildColumns(tid, sampled.Get<double>("exhumation_m"), disp, cfg);
        LayerColumns columns = LayerColumns.FromColumns(sb, sr, Model.NLayers);
        sec["geology"] = Seconds(t);

        t = Stopwatch.GetTimestamp();
        OrderedDictionary<string, object?> bc = HeroBoundary(planet, site, graph, sampled.Get<double>("runoff_eff_m_per_yr"), cfg);
        sec["boundary"] = Seconds(t);
        Pipeline.Say(log, $"[히어로] 출구 {bc["outlet_edge"]} 가장자리 {OutletCells}칸, z = {(double)bc["z_outlet_m"]!:F1} m, "
            + $"들어오는 물 {IO.PyFormat.General((double)bc["inflow_m3_per_yr"]!, 3)} m³/yr ({bc["inflow_edge"] ?? "None"})");

        t = Stopwatch.GetTimestamp();
        double[]? inflow = (double)bc["inflow_m3_per_yr"]! > 0 ? (double[])bc["extra_inflow"]! : null;
        bool[] isOutlet = (bool[])bc["is_outlet"]!;
        double[] zOutlet = [(double)bc["z_outlet_m"]!];
        int maxIter = Pipeline.SolverMaxIter(cfg, "hero");
        (double[]? zInit, OrderedDictionary<string, object?> warm) = Warmstart.CoarseWarmStart(
            graph, isOutlet, zOutlet, sampled.Get<double>("uplift_m_per_yr"), sampled.Get<double>("runoff_eff_m_per_yr"), columns, cfg,
            inflow, maxIter: maxIter, log: log);
        StageResult st = Pipeline.RunStages2To4(
            graph, isOutlet, zOutlet, sampled.Get<double>("uplift_m_per_yr"), sampled.Get<double>("runoff_eff_m_per_yr"),
            sampled.Get<double>("precip_m_per_yr"), columns, cfg, extraInflow: inflow, latDeg: site.LatDeg, log: log,
            runoff: sampled.Get<double>("runoff_m_per_yr"), maxIter: maxIter, zInit: zInit);
        st.Diag["warm_start"] = warm;
        sec["stages"] = Seconds(t);

        var fields = new FieldSet();
        foreach (KeyValuePair<string, Array> kv in sampled)
        {
            if (kv.Key != "template_id")
            {
                fields[kv.Key] = kv.Value;
            }
        }
        fields["template_id"] = tid;
        fields["is_ocean"] = new bool[n];
        fields.Update(st.Fields);

        t = Stopwatch.GetTimestamp();
        var sd = (OrderedDictionary<string, object?>)st.Diag["solver"]!;
        OrderedDictionary<string, object?> card = Pipeline.Score(
            cfg, heroFields: fields, heroGraph: graph,
            diag: new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["hero"] = new Dictionary<string, object?>
                {
                    ["converged"] = sd["converged"],
                    ["iterations"] = sd["iterations"],
                    ["extra_inflow_m3_per_yr"] = bc["extra_inflow"],
                },
            });
        sec["scorecard"] = Seconds(t);
        sec["total"] = Seconds(tAll);
        OrderedDictionary<string, object?> summary = Pipeline.ScorecardSummary(card);
        Pipeline.Say(log, $"[히어로] 끝: {(double)sec["total"]!:F2} s, 점수표 불합격 {Pipeline.PyListRepr((List<object?>)summary["failed"]!)}");
        var boundary = new OrderedDictionary<string, object?>();
        foreach (KeyValuePair<string, object?> kv in bc)
        {
            if (kv.Key is not "is_outlet" and not "extra_inflow")
            {
                boundary[kv.Key] = kv.Value;
            }
        }
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
            ["seconds"] = new OrderedDictionary<string, object?>(sec) { ["stages_detail"] = st.Diag["seconds"] },
            ["boundary"] = boundary,
            ["grid"] = Domain.HeroGridInfo(cfg),
            ["solver"] = sd,
            ["stages"] = stages,
            ["scorecard"] = card,
            ["scorecard_summary"] = summary,
            ["extra_inflow_m3_per_yr"] = bc["extra_inflow"],
        };
        return new HeroState(graph, fields, columns, site, st.Rivers, st.FanApexes, diag);
    }
}
