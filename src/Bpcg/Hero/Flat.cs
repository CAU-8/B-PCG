using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.IO;
using Bpcg.Landscape;
using Bpcg.Numerics;
using Bpcg.Planet;

namespace Bpcg.Hero;

/// <summary>
/// 평면 히어로: L0 없이 가짜 경계조건으로 2~4단계를 돌립니다 (src/bpcg/hero/flat.py, docs/pipeline.md 9장).
/// </summary>
public static class Flat
{
    public const double UBaseMPerYr = 1.0e-4;
    public const double UPeakMPerYr = 2.0e-3;
    public const double UCenterFraction = 0.3;
    public const double UWidthFraction = 0.25;
    public const byte TemplateId = Model.FoldThrust;
    public const double RunoffMPerYr = 0.5;
    public const double TemperatureSeaC = 15.0;
    public const int OutletCells = 5;
    public const double OutletZM = 200.0;
    public const int PresolveCoarsen = 4;
    public const int PresolveMinCells = 16;

    private static double Seconds(long t0) => Stopwatch.GetElapsedTime(t0).TotalSeconds;

    /// <summary>섭입 단면 융기 U(y) [m/yr] (flat_uplift). y: 북쪽 가장자리에서 잰 거리 [m].</summary>
    public static double[] FlatUplift(double[] yFromNorth, double lengthM)
    {
        if (!(lengthM > 0))
        {
            throw new ArgumentException($"length_m 은 0 보다 커야 합니다: {lengthM}");
        }
        double c0 = UCenterFraction * lengthM;
        double w = UWidthFraction * lengthM;
        return Array.ConvertAll(yFromNorth, y =>
        {
            double t = (y - c0) / w;
            return UBaseMPerYr + (UPeakMPerYr * Math.Exp(-(t * t)));
        });
    }

    /// <summary>Budyko 유출이 runoff 가 되는 강수 P [m/yr] (precip_for_runoff, brentq).</summary>
    public static double PrecipForRunoff(double runoff, double pet)
    {
        if (!(runoff > 0 && pet > 0))
        {
            throw new ArgumentException($"runoff, pet 는 0 보다 커야 합니다: {runoff}, {pet}");
        }
        double Gap(double p) => Climate.BudykoRunoff([p], [pet])[0] - runoff;
        double lo = runoff;
        double hi = runoff + pet;
        if (Gap(hi) < 0.0)
        {
            hi = runoff + (2.0 * pet);
        }
        return Interp.Brentq(Gap, lo, hi, 1e-12, 1e-12);
    }

    /// <summary>T_e = (템플릿 1 퇴적층 두께) / U_max [yr] (erosion_period_yr). U_max ≤ 0 이면 0.</summary>
    public static double ErosionPeriodYr(double[] uplift)
    {
        double uMax = uplift.Max();
        if (!(uMax > 0))
        {
            return 0.0;
        }
        double thickness = 0.0;
        foreach ((_, double th) in Model.Templates[TemplateId].Layers)
        {
            thickness += th;
        }
        return thickness / uMax;
    }

    /// <summary>거친 격자로 미리 푼 정상상태 지표를 점 (x 동, y 북) 에서 보간합니다 (presolve_surface).</summary>
    public static double[] PresolveSurface(Config cfg, double[] x, double[] y)
    {
        (int nSide, double dx) = Domain.HeroGridSize(cfg);
        double l = nSide * dx;
        int nC = Math.Max(PresolveMinCells, nSide / PresolveCoarsen);
        double dc = l / nC;
        CellGraph cg = Graph.FlatGraph(nC, nC, dc, 0.0, 0, (-0.5 * l, 0.5 * l));
        double[] yy = new double[cg.NCells];
        for (int c = 0; c < yy.Length; c++)
        {
            yy[c] = (0.5 * l) - cg.Pos[(c * 3) + 1];
        }
        double[] uC = FlatUplift(yy, l);
        bool[] outlet = new bool[cg.NCells];
        outlet[Domain.EdgeCells(nC, "south", nC / 2, 1)[0]] = true;
        double[] runoff = Enumerable.Repeat(RunoffMPerYr, cg.NCells).ToArray();
        SolverResult res = Solver.SolveSteadyState(cg, outlet, [OutletZM], uC, runoff, null, cfg);
        double[] xs = new double[nC];
        double[] ys = new double[nC];
        for (int i = 0; i < nC; i++)
        {
            xs[i] = (-0.5 * l) + ((i + 0.5) * dc);
            ys[i] = (-0.5 * l) + ((i + 0.5) * dc);
        }
        double[] img = new double[nC * nC];
        for (int j = 0; j < nC; j++)
        {
            Array.Copy(res.Z, (nC - 1 - j) * nC, img, j * nC, nC);
        }
        return Interp.RegularGridLinear2D(ys, xs, img, y, x);
    }

    /// <summary>깎인 두께(기둥 꼭대기) = max(z_pre, 0) + max(U, 0)·T_e [m] (flat_exhumation).</summary>
    public static double[] FlatExhumation(double[] uplift, double[] zPre)
    {
        double[] u = Array.ConvertAll(uplift, v => Math.Max(v, 0.0));
        double te = ErosionPeriodYr(u);
        double[] o = new double[u.Length];
        for (int c = 0; c < o.Length; c++)
        {
            o[c] = Math.Max(zPre[c], 0.0) + (u[c] * te);
        }
        return o;
    }

    /// <summary>가짜 경계조건으로 평면 히어로를 만듭니다 (flat_hero).</summary>
    public static HeroState FlatHero(Config cfg, Action<string>? log = null)
    {
        long tAll = Stopwatch.GetTimestamp();
        var sec = new OrderedDictionary<string, object?>();
        (int nSide, double dx) = Domain.HeroGridSize(cfg);
        double l = nSide * dx;
        CellGraph graph = Domain.HeroFlatGraph(cfg);
        int n = graph.NCells;
        double[] y = new double[n];
        double[] px = new double[n];
        double[] py = new double[n];
        for (int c = 0; c < n; c++)
        {
            px[c] = graph.Pos[c * 3];
            py[c] = graph.Pos[(c * 3) + 1];
            y[c] = (0.5 * l) - py[c];
        }
        double[] u = FlatUplift(y, l);

        double petValue = (cfg.F("climate.pet_per_degc_m_per_yr") * NpMath.PyMax(TemperatureSeaC, 0.0)) + cfg.F("climate.pet_base_m_per_yr");
        double pValue = PrecipForRunoff(RunoffMPerYr, petValue);
        double[] runoff = Enumerable.Repeat(RunoffMPerYr, n).ToArray();
        double frac = cfg.F("climate.runoff_floor_fraction");
        double floor = cfg.F("climate.runoff_floor_m_per_yr");
        double[] runoffEff = Array.ConvertAll(runoff, v => Math.Max(Math.Max(v, frac * pValue), floor));
        double[] precip = Enumerable.Repeat(pValue, n).ToArray();
        double[] pet = Enumerable.Repeat(petValue, n).ToArray();

        long t = Stopwatch.GetTimestamp();
        double[] zPre = PresolveSurface(cfg, px, py);
        sec["presolve"] = Seconds(t);

        t = Stopwatch.GetTimestamp();
        byte[] tid = Enumerable.Repeat(TemplateId, n).ToArray();
        double[] exh = FlatExhumation(u, zPre);
        double[] dConv = (double[])y.Clone();
        double radius = cfg.F("planet.radius_m");
        double[] unitPts = Array.ConvertAll(graph.Pos, v => v / radius);
        (double[] phase, double[] disp) = Model.FoldDisplacement(
            new FieldSet { ["dist_convergent_m"] = dConv, ["uplift_m_per_yr"] = u }, tid, cfg, unitPts);
        (double[] sb, byte[] sr) = Model.BuildColumns(tid, exh, disp, cfg);
        LayerColumns columns = LayerColumns.FromColumns(sb, sr, Model.NLayers);
        sec["geology"] = Seconds(t);

        long[] outletCells = Domain.EdgeCells(nSide, "south", nSide / 2, OutletCells);
        bool[] isOutlet = new bool[n];
        foreach (long c in outletCells)
        {
            isOutlet[c] = true;
        }
        Pipeline.Say(log, $"[평면 히어로] {nSide}×{nSide} ({n}칸, {PyFormat.General(dx)} m), U {u.Min() * 1e3:F2}~"
            + $"{u.Max() * 1e3:F2} mm/yr, 강수 {pValue:F3} m/yr, 출구 남쪽 {OutletCells}칸");

        t = Stopwatch.GetTimestamp();
        int maxIter = Pipeline.SolverMaxIter(cfg, "hero");
        (double[]? zInit, OrderedDictionary<string, object?> warm) = Warmstart.CoarseWarmStart(
            graph, isOutlet, [OutletZM], u, runoffEff, columns, cfg, maxIter: maxIter, log: log);
        StageResult st = Pipeline.RunStages2To4(
            graph, isOutlet, [OutletZM], u, runoffEff, precip, columns, cfg, log: log, runoff: runoff,
            temperatureSeaC: [TemperatureSeaC], maxIter: maxIter, zInit: zInit);
        st.Diag["warm_start"] = warm;
        sec["stages"] = Seconds(t);

        var fields = new FieldSet
        {
            ["uplift_m_per_yr"] = u,
            ["exhumation_m"] = exh,
            ["precip_m_per_yr"] = precip,
            ["pet_m_per_yr"] = pet,
            ["runoff_m_per_yr"] = runoff,
            ["runoff_eff_m_per_yr"] = runoffEff,
            ["template_id"] = tid,
            ["fold_phase"] = phase,
            ["dist_convergent_m"] = dConv,
            ["is_ocean"] = new bool[n],
        };
        fields.Update(st.Fields);

        t = Stopwatch.GetTimestamp();
        var sd = (OrderedDictionary<string, object?>)st.Diag["solver"]!;
        OrderedDictionary<string, object?> card = Pipeline.Score(
            cfg, heroFields: fields, heroGraph: graph,
            diag: new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["hero"] = new Dictionary<string, object?> { ["converged"] = sd["converged"], ["iterations"] = sd["iterations"] },
            });
        sec["scorecard"] = Seconds(t);
        sec["total"] = Seconds(tAll);
        OrderedDictionary<string, object?> summary = Pipeline.ScorecardSummary(card);
        Pipeline.Say(log, $"[평면 히어로] 끝: {(double)sec["total"]!:F2} s, 점수표 불합격 {Pipeline.PyListRepr((List<object?>)summary["failed"]!)}");
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
            ["boundary"] = new OrderedDictionary<string, object?>
            {
                ["outlet_cells"] = outletCells,
                ["outlet_edge"] = "south",
                ["z_outlet_m"] = OutletZM,
                ["inflow_m3_per_yr"] = 0.0,
                ["precip_m_per_yr"] = pValue,
                ["pet_m_per_yr"] = petValue,
                ["length_m"] = l,
                ["erosion_period_yr"] = ErosionPeriodYr(u),
                ["presolve_z_max_m"] = zPre.Max(),
            },
            ["grid"] = Domain.HeroGridInfo(cfg),
            ["solver"] = sd,
            ["stages"] = stages,
            ["scorecard"] = card,
            ["scorecard_summary"] = summary,
        };
        return new HeroState(graph, fields, columns, null, st.Rivers, st.FanApexes, diag);
    }
}
