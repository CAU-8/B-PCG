using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Hydro;
using Bpcg.IO;
using Bpcg.Numerics;

namespace Bpcg.Metrics;

/// <summary>
/// 점수표: 행성(L0)과 히어로(L2) 결과를 지표 표로 묶습니다 (src/bpcg/metrics/scorecard.py, docs/pipeline.md 12장).
/// 항목마다 {value, unit, kind, pass, note}.
/// </summary>
public static class Scorecard
{
    public static readonly OrderedDictionary<string, double> DefaultThresholds = new()
    {
        ["earth_ocean_fraction"] = 0.708,
        ["shelf_depth_m"] = 200.0,
        ["bimodal_min_separation_m"] = 1000.0,
        ["flat_slope"] = 0.02,
        ["gw_surface_tol_m"] = 0.5,
        ["law_median_max"] = 1.0e-3,
        ["budget_max"] = 1.0e-6,
        ["alignment_max"] = 1.3,
        ["rock_samples"] = 10_000,
        ["hack_min_area_cells"] = 10.0,
        ["water_rule_tol_m"] = 1.0e-3,
    };

    public static readonly (double Lo, double Hi) EarthHackRange = (0.49, 0.6);
    public static readonly (double Lo, double Hi) EarthGwSurfaceRange = (0.22, 0.32);

    /// <summary>합격선 dict. cfg 에 [metrics] 절이 있으면 같은 키를 덮어씁니다.</summary>
    public static OrderedDictionary<string, double> Thresholds(Config? cfg = null)
    {
        var th = new OrderedDictionary<string, double>(DefaultThresholds);
        if (cfg is not null && cfg.Contains("metrics"))
        {
            Section sec = cfg.Sec("metrics");
            foreach (string k in th.Keys.ToList())
            {
                if (sec.Contains(k))
                {
                    th[k] = sec.F(k);
                }
            }
        }
        return th;
    }

    /// <summary>'경사를 만든 암석 = 보이는 암석' 비율 (rock_consistency). 표본이 없으면 NaN.</summary>
    public static double RockConsistency(byte[] sampleRock, byte[] solverRock)
    {
        if (sampleRock.Length != solverRock.Length)
        {
            throw new ArgumentException("sample_rock 과 solver_rock 은 같은 (M,) 모양이어야 합니다");
        }
        if (sampleRock.Length == 0)
        {
            return double.NaN;
        }
        int same = 0;
        for (int i = 0; i < sampleRock.Length; i++)
        {
            same += sampleRock[i] == solverRock[i] ? 1 : 0;
        }
        return (double)same / sampleRock.Length;
    }

    /// <summary>mask 칸에서 고르게 nSamples 개 (even_sample, 난수 없음).</summary>
    public static long[] EvenSample(bool[] mask, int nSamples)
    {
        var cells = new List<long>();
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i])
            {
                cells.Add(i);
            }
        }
        if (cells.Count <= nSamples)
        {
            return cells.ToArray();
        }
        double[] k = NpGrid.Linspace(0, cells.Count - 1, nSamples);
        return k.Select(v => cells[(int)Math.Round(v)]).ToArray();
    }

    /// <summary>표본 칸의 (보이는 암석, 솔버 지표 층 암석) (surface_rock_samples).</summary>
    public static (byte[] Sample, byte[] Solver) SurfaceRockSamples(
        double[] z, byte[] surfaceRock, double[] strataBottom, byte[] strataRock, int nLayers, long[] cells)
    {
        byte[] sample = new byte[cells.Length];
        byte[] solver = new byte[cells.Length];
        for (int k = 0; k < cells.Length; k++)
        {
            int c = (int)cells[k];
            int i = Model.LayerIndexAt(strataBottom, nLayers, c, z[c]);
            solver[k] = strataRock[(c * (nLayers + 1)) + i];
            sample[k] = surfaceRock[c];
        }
        return (sample, solver);
    }

    /// <summary>동굴이 있는 (칸, 층) 가운데 녹는 암석인 비율 (cave_soluble_fraction). 동굴이 없으면 (NaN, 0).</summary>
    public static (double Fraction, long Count) CaveSolubleFraction(
        IEnumerable<double[]> caveLevels, double[] strataBottom, byte[] strataRock, int nLayers)
    {
        long hit = 0;
        long total = 0;
        foreach (double[] lv in caveLevels)
        {
            var cells = new List<long>();
            var zs = new List<double>();
            for (int c = 0; c < lv.Length; c++)
            {
                if (double.IsFinite(lv[c]))
                {
                    cells.Add(c);
                    zs.Add(lv[c]);
                }
            }
            if (cells.Count == 0)
            {
                continue;
            }
            byte[] rock = Model.RockAtPoints(strataBottom, strataRock, nLayers, cells.ToArray(), zs.ToArray());
            foreach (byte r in rock)
            {
                hit += Rocks.Soluble[r] ? 1 : 0;
            }
            total += cells.Count;
        }
        return (total > 0 ? (double)hit / total : double.NaN, total);
    }

    /// <summary>지하수면 규칙 위반 칸 수 (water_rule_violations): above, water, deep, nan, total.</summary>
    public static OrderedDictionary<string, object?> WaterRuleViolations(
        double[] z, double[] zGw, double[] waterLevel, double? maxDepth = null, double tol = 1.0e-3)
    {
        int n = z.Length;
        if (zGw.Length != n || waterLevel.Length != n)
        {
            throw new ArgumentException("z, z_gw, water_level 은 같은 (N,) 모양이어야 합니다");
        }
        long above = 0;
        long water = 0;
        long deep = 0;
        long nan = 0;
        long total = 0;
        for (int c = 0; c < n; c++)
        {
            bool wet = double.IsFinite(waterLevel[c]);
            bool bNan = !double.IsFinite(zGw[c]);
            bool bAbove = !wet && zGw[c] > z[c] + tol;
            bool bWater = wet && Math.Abs(zGw[c] - waterLevel[c]) > tol;
            bool bDeep = maxDepth is double md && zGw[c] < z[c] - md - tol;
            above += bAbove ? 1 : 0;
            water += bWater ? 1 : 0;
            deep += bDeep ? 1 : 0;
            nan += bNan ? 1 : 0;
            total += bNan || bAbove || bWater || bDeep ? 1 : 0;
        }
        return new OrderedDictionary<string, object?>
        {
            ["above"] = above,
            ["water"] = water,
            ["deep"] = deep,
            ["nan"] = nan,
            ["total"] = total,
        };
    }

    private static OrderedDictionary<string, object?> Entry(object? value, string unit, string kind, bool? passed, string note) =>
        new()
        {
            ["value"] = value,
            ["unit"] = unit,
            ["kind"] = kind,
            ["pass"] = passed,
            ["note"] = note,
        };

    private sealed class Level(string name, FieldSet fields, CellGraph? graph, IReadOnlyDictionary<string, object?> diag, Config? cfg, OrderedDictionary<string, double> th)
    {
        private long[]? _order;

        public string Name { get; } = name;

        public FieldSet F { get; } = fields;

        public CellGraph? Graph { get; } = graph;

        public IReadOnlyDictionary<string, object?> Diag { get; } = diag;

        public Config? Cfg { get; } = cfg;

        public OrderedDictionary<string, double> Th { get; } = th;

        public List<string> Missing(IEnumerable<string>? names = null, bool graph = false, bool cfg = false)
        {
            var o = (names ?? []).Where(k => !F.Contains(k)).ToList();
            if (graph && Graph is null)
            {
                o.Add("graph");
            }
            if (cfg && Cfg is null)
            {
                o.Add("cfg");
            }
            return o;
        }

        public double[] D(string name) => FieldSet.ToFloat64(F[name]);

        public bool[] Bool(string name) => (bool[])FieldSet.CastTo(F[name], typeof(bool));

        public long[] Long(string name) => (long[])FieldSet.CastTo(F[name], typeof(long));

        public byte[] U8(string name) => (byte[])FieldSet.CastTo(F[name], typeof(byte));

        public int N => F["receiver"].Length;

        public long[] Rcv() => Long("receiver");

        public long[] Order() => _order ??= Routing.TopoOrder(Rcv());

        public bool[] Root()
        {
            long[] r = Rcv();
            bool[] o = new bool[r.Length];
            for (int i = 0; i < r.Length; i++)
            {
                o[i] = r[i] == i;
            }
            return o;
        }

        public bool[] Flag(string name, int n) => F.Contains(name) ? Bool(name) : new bool[n];

        public bool[] Land(int n) => Array.ConvertAll(Flag("is_ocean", n), b => !b);

        public int Columns(string name) => F.Columns.TryGetValue(name, out int c) ? c : 1;
    }

    private static void Run(
        OrderedDictionary<string, object?> output, string key, string kind, string unit, List<string> missing,
        Func<(object? Value, bool? Passed, string Note)> fn)
    {
        if (missing.Count > 0)
        {
            output[key] = Entry(null, unit, kind, null, "건너뜀: 없는 입력 " + string.Join(", ", missing));
            return;
        }
        try
        {
            (object? value, bool? passed, string note) = fn();
            output[key] = Entry(value, unit, kind, passed, note);
        }
        catch (ArgumentException e)
        {
            output[key] = Entry(null, unit, kind, kind == "check" ? false : null, $"계산 실패: {e.Message}");
        }
    }

    private static object? FiniteOrNull(double x) => double.IsFinite(x) ? x : null;

    private static string G(double x) => PyFormat.General(x);

    private static void AddPlanetOnly(OrderedDictionary<string, object?> output, Level l)
    {
        OrderedDictionary<string, double> th = l.Th;
        string p = l.Name;
        Run(output, $"{p}.ocean_fraction", "emergent", "", l.Missing(["is_ocean"], graph: true), () =>
        {
            double v = Hypsometry.OceanFraction(l.Bool("is_ocean"), l.Graph!.Area);
            return (v, null, $"물 부피 보존 해수면으로 나온 값. 지구 {PyFormat.Repr(th["earth_ocean_fraction"])}");
        });
        Run(output, $"{p}.shelf_area", "emergent", "km2", l.Missing(["z_m", "crust_type", "is_ocean"], graph: true), () =>
        {
            (double a, double frac) = Hypsometry.ShelfArea(
                l.D("z_m"), l.Graph!.Area, l.Long("crust_type"), l.Bool("is_ocean"), th["shelf_depth_m"]);
            string note = $"대륙 지각 위 수심 < {G(th["shelf_depth_m"])} m, 바다의 {PyFormat.Fixed(frac, 4)}. "
                + "지각평형 흉내로 나온 값이고 퇴적물이 대륙붕에 쌓이는 과정은 없음";
            return (a / 1.0e6, null, note);
        });
        string zname = l.F.Contains("z_mean_m") ? "z_mean_m" : "z_m";
        Run(output, $"{p}.hypsometry_bimodal", "forced", "m", l.Missing([zname], graph: true), () =>
        {
            OrderedDictionary<string, object?> r = Hypsometry.Bimodality(
                l.D(zname), l.Graph!.Area, minSeparationM: th["bimodal_min_separation_m"]);
            string peaks = string.Join(", ", ((List<object?>)r["peaks"]!).Select(v => PyFormat.Fixed((double)v!, 0)));
            string note = $"{zname} 면적 가중 분포의 봉우리 [{peaks}] m (돌출 봉우리 {r["n_peaks"]}개), "
                + $"간격 > {G(th["bimodal_min_separation_m"])} m 이면 합격. 지각 비율·지각평형으로 거의 정해지는 반쯤 입력";
            return ((double)r["separation_m"]!, (bool)r["ok"]!, note);
        });
    }

    private static void AddCommon(OrderedDictionary<string, object?> output, Level l)
    {
        OrderedDictionary<string, double> th = l.Th;
        string p = l.Name;

        Run(output, $"{p}.hack_exponent", "emergent", "", l.Missing(["receiver", "drainage_area_m2"], graph: true), () =>
        {
            int n = l.N;
            (double c, double h) = Drainage.HackFit(
                l.Graph!, l.Rcv(), l.Order(), l.D("drainage_area_m2"), l.Land(n), th["hack_min_area_cells"]);
            return (FiniteOrNull(h), null,
                $"본류 L = {PyFormat.General(c, 3)}·A^h (km, km²). 지구 약 {PyFormat.Repr(EarthHackRange.Lo)}~{PyFormat.Repr(EarthHackRange.Hi)}");
        });

        Run(output, $"{p}.gw_surface_fraction", "emergent", "", l.Missing(["z_m", "water_table_m"]), () =>
        {
            double[] z = l.D("z_m");
            double v = Hypsometry.GwSurfaceFraction(z, l.D("water_table_m"), l.Land(z.Length), th["gw_surface_tol_m"], l.Graph?.Area);
            string note = $"z − z_gw ≤ {G(th["gw_surface_tol_m"])} m 인 육지 면적 비율. 지구 "
                + $"{PyFormat.Repr(EarthGwSurfaceRange.Lo)}~{PyFormat.Repr(EarthGwSurfaceRange.Hi)}";
            return (FiniteOrNull(v), null, note);
        });

        Run(output, $"{p}.river_reach_fraction", "check", "", l.Missing(["receiver", "is_river"]), () =>
        {
            int n = l.N;
            bool[] ocean = l.Flag("is_ocean", n);
            bool[] lake = l.Flag("is_lake", n);
            bool[] root = l.Root();
            bool[] sink = new bool[n];
            for (int c = 0; c < n; c++)
            {
                sink[c] = ocean[c] || lake[c] || root[c];
            }
            bool[] riv = l.Bool("is_river");
            double v = Drainage.RiverReachFraction(l.Rcv(), l.Order(), riv, sink);
            if (!double.IsFinite(v))
            {
                return (null, null, "하천 칸 없음");
            }
            return (v, v == 1.0, $"하천 {riv.Count(b => b)}칸, 기준 100%");
        });

        Run(output, $"{p}.river_backflow", "check", "cells", l.Missing(["receiver", "is_river", "water_level_m"]), () =>
        {
            long v = Drainage.RiverBackflowCount(l.Rcv(), l.D("water_level_m"), l.Bool("is_river"));
            return (v, v == 0, "수신 셀 수면이 더 높은 하천 칸 수, 기준 0");
        });

        bool lawGiven = l.Diag.ContainsKey("law_slope");
        var lawNeed = new List<string> { "receiver", "slope", "s_crit" };
        if (!lawGiven)
        {
            lawNeed.AddRange(["discharge_m3_per_yr", "drainage_area_m2", "sediment_flux_m3_per_yr", "uplift_m_per_yr", "k_s"]);
        }
        Run(output, $"{p}.law_consistency", "check", "", l.Missing(lawNeed, graph: !lawGiven, cfg: !lawGiven), () =>
        {
            int n = l.N;
            double[] lawS = lawGiven
                ? FieldSet.ToFloat64((Array)l.Diag["law_slope"]!)
                : Drainage.LawSlopeFromFields(
                    l.Graph!, l.Rcv(), l.D("discharge_m3_per_yr"), l.D("drainage_area_m2"), l.D("sediment_flux_m3_per_yr"),
                    l.D("uplift_m_per_yr"), l.D("k_s"), l.D("s_crit"), l.Cfg!);
            bool[] land = l.Land(n);
            bool[] root = l.Root();
            bool[] ns = l.Flag("not_steady", n);
            bool[] fan = l.Flag("fan", n);
            bool[] mask = new bool[n];
            for (int c = 0; c < n; c++)
            {
                mask[c] = land[c] && !root[c] && !ns[c] && !fan[c];
            }
            string excl = "";
            if (l.F.Contains("strata_bottom_m") && l.F.Contains("z_m"))
            {
                bool[] cross = Drainage.LawCrossMask(l.Rcv(), l.D("z_m"), l.D("strata_bottom_m"), l.Columns("strata_bottom_m"));
                for (int c = 0; c < n; c++)
                {
                    mask[c] &= !cross[c];
                }
                excl = $", 층 경계를 넘는 {cross.Count(b => b)}칸 뺌";
            }
            OrderedDictionary<string, object?> r = Drainage.LawConsistency(l.D("slope"), lawS, l.D("s_crit"), mask);
            if ((long)r["n"]! == 0)
            {
                return (null, null, "볼 칸 없음");
            }
            double median = (double)r["median"]!;
            string note = $"|S − 법칙|/법칙 중앙값 (최댓값 {PyFormat.General((double)r["max"]!, 3)}, {r["n"]}칸{excl}), "
                + $"기준 < {G(th["law_median_max"])}";
            return (median, median < th["law_median_max"], note);
        });

        Run(output, $"{p}.water_budget", "check", "", l.Missing(["receiver", "discharge_m3_per_yr", "runoff_eff_m_per_yr"], graph: true), () =>
        {
            double[] reff = l.D("runoff_eff_m_per_yr");
            double[] src = new double[reff.Length];
            for (int c = 0; c < src.Length; c++)
            {
                src[c] = l.Graph!.Area[c] * reff[c];
            }
            bool hasExtra = l.Diag.TryGetValue("extra_inflow_m3_per_yr", out object? extra) && extra is not null;
            if (hasExtra)
            {
                double[] ex = FieldSet.ToFloat64((Array)extra!);
                for (int c = 0; c < src.Length; c++)
                {
                    src[c] += ex[c];
                }
            }
            double v = Drainage.BudgetError(l.Rcv(), l.D("discharge_m3_per_yr"), src, l.Order());
            string note = $"Q = 누적(A·R_eff{(hasExtra ? " + 들어오는 물" : "")}), 기준 ≤ {G(th["budget_max"])}";
            return (v, v <= th["budget_max"], note);
        });

        Run(output, $"{p}.sediment_budget", "check", "", l.Missing(["receiver", "sediment_flux_m3_per_yr", "uplift_m_per_yr"], graph: true), () =>
        {
            double[] u = l.D("uplift_m_per_yr");
            double[] src = new double[u.Length];
            for (int c = 0; c < src.Length; c++)
            {
                src[c] = l.Graph!.Area[c] * u[c];
            }
            double v = Drainage.BudgetError(l.Rcv(), l.D("sediment_flux_m3_per_yr"), src, l.Order(), true);
            return (v, v <= th["budget_max"], $"Qs = max(누적(A·U), 0), 기준 ≤ {G(th["budget_max"])}");
        });

        bool rockGiven = l.Diag.ContainsKey("rock_samples");
        List<string> rockNeed = rockGiven ? [] : ["z_m", "surface_rock", "strata_bottom_m", "strata_rock"];
        Run(output, $"{p}.rock_consistency", "check", "", l.Missing(rockNeed), () =>
        {
            double v;
            string src;
            int nS;
            if (rockGiven)
            {
                (byte[] sample, byte[] solver) = ((byte[], byte[]))l.Diag["rock_samples"]!;
                v = RockConsistency(sample, solver);
                src = "3D 함수 표본";
                nS = sample.Length;
            }
            else
            {
                double[] z = l.D("z_m");
                int n = z.Length;
                bool[] land = l.Land(n);
                bool[] ns = l.Flag("not_steady", n);
                bool[] fan = l.Flag("fan", n);
                bool[] use = new bool[n];
                for (int c = 0; c < n; c++)
                {
                    use[c] = land[c] && !ns[c] && !fan[c];
                }
                long[] cells = EvenSample(use, (int)th["rock_samples"]);
                int nl = l.Columns("strata_bottom_m");
                (byte[] sample, byte[] solver) = SurfaceRockSamples(
                    z, l.U8("surface_rock"), l.D("strata_bottom_m"), l.U8("strata_rock"), nl, cells);
                bool[] same = new bool[cells.Length];
                for (int k = 0; k < same.Length; k++)
                {
                    same[k] = sample[k] == solver[k];
                }
                if (l.F.Contains("s_crit"))
                {
                    double[] scAll = l.D("s_crit");
                    for (int k = 0; k < same.Length; k++)
                    {
                        double sc = scAll[cells[k]];
                        same[k] &= Math.Abs(sc - Rocks.SCrit[sample[k]]) <= 1e-6 * Math.Max(1.0, sc);
                    }
                }
                v = cells.Length > 0 ? (double)same.Count(b => b) / cells.Length : double.NaN;
                src = "지표 필드 표본(층 기둥·S_crit 대조)";
                nS = cells.Length;
            }
            if (!double.IsFinite(v))
            {
                return (null, null, "표본 없음");
            }
            return (v, v == 1.0, $"{src} {nS}점, 기준 100%");
        });

        List<string> caveNames = new[] { "cave_level_0_m", "cave_level_1_m" }.Where(l.F.Contains).ToList();
        var caveMissing = new List<string>();
        if (caveNames.Count == 0)
        {
            caveMissing.Add("cave_level_0_m");
        }
        caveMissing.AddRange(l.Missing(["strata_bottom_m", "strata_rock"]));
        Run(output, $"{p}.cave_in_soluble", "check", "", caveMissing, () =>
        {
            (double v, long cnt) = CaveSolubleFraction(
                caveNames.Select(l.D), l.D("strata_bottom_m"), l.U8("strata_rock"), l.Columns("strata_bottom_m"));
            if (cnt == 0)
            {
                return (null, null, "동굴 칸 없음 (판정 없음)");
            }
            return (v, v == 1.0, $"동굴 (칸, 층) {cnt}개, 기준 100%");
        });

        Run(output, $"{p}.water_rule_violations", "check", "cells", l.Missing(["z_m", "water_table_m", "water_level_m"]), () =>
        {
            double? maxDepth = null;
            if (l.Cfg is not null && l.Cfg.Contains("groundwater") && l.Cfg.Sec("groundwater").Contains("max_depth_m"))
            {
                maxDepth = l.Cfg.F("groundwater.max_depth_m");
            }
            OrderedDictionary<string, object?> r = WaterRuleViolations(
                l.D("z_m"), l.D("water_table_m"), l.D("water_level_m"), maxDepth, th["water_rule_tol_m"]);
            string note = $"땅 위로 솟음 {r["above"]}, 물 칸 수면 불일치 {r["water"]}, 최대 깊이 넘음 {r["deep"]}, NaN {r["nan"]}. 기준 0";
            long total = (long)r["total"]!;
            return (total, total == 0, note);
        });

        string[] bands = l.Graph is not null && l.Graph.Kind == "sphere" ? ["edge", "center"] : ["all"];
        foreach (string band in bands)
        {
            string key = $"{p}.grid_alignment" + (band == "all" ? "" : $"_{band}");
            Run(output, key, "check", "", l.Missing(["receiver", "is_river"], graph: true), () =>
            {
                double v = Drainage.GridAlignment(l.Graph!, l.Rcv(), l.Bool("is_river"), band);
                if (!double.IsFinite(v))
                {
                    return (null, null, $"띠 '{band}' 에 6칸을 따라갈 하천 칸 없음");
                }
                string note = $"띠 '{band}', 면 로컬 (i, j), 6칸 하류 방향, 무작위 = 1, 목표 < {G(th["alignment_max"])}";
                return (v, v < th["alignment_max"], note);
            });
        }

        Run(output, $"{p}.solver_converged", "check", "", l.Diag.ContainsKey("converged") ? [] : ["diag.converged"], () =>
        {
            bool c = (bool)l.Diag["converged"]!;
            l.Diag.TryGetValue("iterations", out object? it);
            return (c, c, it is not null ? $"반복 {it}회" : "솔버 진단값");
        });
    }

    private static void AddHeroOnly(OrderedDictionary<string, object?> output, Level l)
    {
        string p = l.Name;
        Run(output, $"{p}.flat_fraction", "emergent", "", l.Missing(["slope"]), () =>
        {
            double[] s = l.D("slope");
            double v = Hypsometry.FlatFraction(s, l.Land(s.Length), l.Th["flat_slope"], l.Graph?.Area);
            return (FiniteOrNull(v), null, $"경사 < {G(l.Th["flat_slope"])} 인 육지 면적 비율 (L2)");
        });
    }

    /// <summary>
    /// 있는 입력으로 점수표를 만듭니다 (scorecard). diag: {"planet": {...}, "hero": {...}}.
    /// 반환: {이름: {value, unit, kind, pass, note}}.
    /// </summary>
    public static OrderedDictionary<string, object?> Build(
        FieldSet? planetFields = null, CellGraph? planetGraph = null, FieldSet? heroFields = null, CellGraph? heroGraph = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? diag = null, Config? cfg = null)
    {
        OrderedDictionary<string, double> th = Thresholds(cfg);
        var output = new OrderedDictionary<string, object?>();
        foreach ((string name, FieldSet? fields, CellGraph? graph) in new[] { ("planet", planetFields, planetGraph), ("hero", heroFields, heroGraph) })
        {
            IReadOnlyDictionary<string, object?> levelDiag =
                diag is not null && diag.TryGetValue(name, out IReadOnlyDictionary<string, object?>? d) && d is not null
                    ? d
                    : new Dictionary<string, object?>();
            var l = new Level(name, fields ?? new FieldSet(), graph, levelDiag, cfg, th);
            if (graph is not null && fields is not null && fields.Count > 0)
            {
                foreach (KeyValuePair<string, Array> kv in fields)
                {
                    int rows = kv.Value.Length / l.Columns(kv.Key);
                    if (rows != graph.NCells)
                    {
                        throw new ArgumentException($"{name}_fields['{kv.Key}'] 의 길이가 그래프 칸 수 {graph.NCells} 와 다릅니다");
                    }
                }
            }
            if (name == "planet")
            {
                AddPlanetOnly(output, l);
            }
            AddCommon(output, l);
            if (name == "hero")
            {
                AddHeroOnly(output, l);
            }
        }
        return output;
    }

    /// <summary>점수표에서 pass 가 False 인 항목 이름 (failed_checks).</summary>
    public static List<string> FailedChecks(OrderedDictionary<string, object?> card) =>
        card.Where(kv => ((OrderedDictionary<string, object?>)kv.Value!)["pass"] is false).Select(kv => kv.Key).ToList();
}
