using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Hero;

namespace Bpcg.Compare;

/// <summary>
/// 우리 방법: B-PCG 평면 히어로(docs/pipeline.md 9장)를 비교 격자에서 돌립니다.
/// </summary>
/// <remarks>
/// 설정은 configs/planets/&lt;planet&gt;.toml + profiles/&lt;profile&gt;.toml 이고, 비교 격자에 맞춰 profile.hero.size_m = n·dx,
/// profile.hero.spacing_m = dx, planet.seed = 시드로 덮어씁니다. 'set' 에 'landscape.theta=0.5; fans.enabled=false' 처럼
/// 세미콜론으로 나눈 덮어쓰기를 더 줄 수 있습니다(bpcg --set 과 같은 검사). 융기는 남북 섭입 단면, 출구는 남쪽 가운데 5칸으로
/// 고정이라 시드는 노드 흔들기·지질 노이즈만 바꿉니다.
/// </remarks>
public static class BpcgMethod
{
    public static Config ConfigFor(CompareGrid grid, long seed, ParamValues p)
    {
        Config baseCfg = Config.LoadConfig(p.S("planet"), p.S("profile"));
        var overrides = new OrderedDictionary<string, object?>
        {
            ["planet.seed"] = seed,
            ["profile.hero.size_m"] = grid.LengthM,
            ["profile.hero.spacing_m"] = grid.Dx,
        };
        foreach (string item in p.S("set").Split(';'))
        {
            if (item.Trim().Length > 0)
            {
                (string key, object? value) = Config.ParseAssignment(item.Trim());
                overrides[key] = value;
            }
        }
        return baseCfg.WithOverrides(Config.CheckedOverrides(baseCfg, overrides));
    }

    private static double[] F(FieldSet f, string name) => FieldSet.ToFloat64(f[name]);

    private static double[] Mask(FieldSet f, string name) => ((bool[])f[name]).Select(b => b ? 1.0 : 0.0).ToArray();

    private static double[] Bytes(FieldSet f, string name) => ((byte[])f[name]).Select(b => (double)b).ToArray();

    public static readonly CompareMethod Bpcg = new(
        "bpcg",
        "B-PCG (평면 히어로)",
        "ours",
        "이 저장소 docs/pipeline.md 7·8·9장",
        "정해 둔 융기에서 솟는 만큼 깎이는 다 자란 산과 강을 지층마다 다른 경사로 풀고, 같은 암석과 물로 땅속까지 만듦",
        [
            new Param("planet", "earth", "행성 설정 이름 (configs/planets)", Kind: "str"),
            new Param("profile", "laptop", "프로필 이름 (configs/profiles, 히어로 크기는 격자로 덮어씀)", Kind: "str"),
            new Param("set", "", "더할 설정 덮어쓰기 'key=value; key=value'", Kind: "str"),
        ],
        (grid, seed, p, ctx) =>
        {
            Config cfg = ConfigFor(grid, seed, p);
            var captured = new Dictionary<string, double[]>();
            Action<string, double[]>? capture = ctx.Recording ? (name, z) => captured[name] = (double[])z.Clone() : null;
            // 솔버 반복 줄은 수백 줄이라 빼고 단계 줄만 남깁니다.
            HeroState st = Flat.FlatHero(cfg, msg =>
            {
                if (!msg.Contains("솔버 반복", StringComparison.Ordinal))
                {
                    ctx.Say("    " + msg);
                }
            }, capture);
            FieldSet f = st.Fields;
            double[] z = F(f, "z_m");
            if (ctx.Recording)
            {
                using (ctx.Extra())
                {
                    Record(ctx, st, captured, z);
                }
            }
            var sd = (OrderedDictionary<string, object?>)st.Diag["solver"]!;
            var summary = (OrderedDictionary<string, object?>)st.Diag["scorecard_summary"]!;
            var output = new MethodOutput(z);
            output.Layers["water_table_m"] = F(f, "water_table_m");
            output.Layers["is_river"] = Mask(f, "is_river");
            output.Layers["surface_rock"] = Bytes(f, "surface_rock");
            output.Layers["uplift_m_per_yr"] = F(f, "uplift_m_per_yr");
            output.Info["solver_iterations"] = sd["iterations"];
            output.Info["solver_converged"] = sd["converged"];
            output.Info["scorecard_failed"] = summary["failed"];
            return output;
        });

    /// <summary>평면 히어로의 계산 순서대로 중간 지도와 솔버 기록을 남깁니다 (pipeline.md 7~9장).</summary>
    private static void Record(CompareContext ctx, HeroState st, Dictionary<string, double[]> captured, double[] z)
    {
        FieldSet f = st.Fields;
        ctx.Snapshot("1. 융기 U", Array.ConvertAll(F(f, "uplift_m_per_yr"), v => v * 1e3), kind: "field", unit: "mm/yr",
            note: "북쪽에 산맥 띠가 있는 섭입 단면 융기. 남쪽 가운데 5칸이 출구입니다.");
        if (captured.TryGetValue("z_pre", out double[]? zPre))
        {
            ctx.Snapshot("2. 미리 푼 거친 지표", zPre, unit: "m", note: "4배 거친 격자에서 암석 하나로 푼 정상상태. 깎인 두께를 정하는 데만 씁니다.");
        }
        ctx.Snapshot("3. 깎인 두께", F(f, "exhumation_m"), kind: "field", unit: "m",
            note: "미리 푼 지표 + 융기 × 침식 기간. 깊이 깎인 곳일수록 아래 지층이 드러납니다.");
        if (captured.TryGetValue("z_init", out double[]? zInit))
        {
            ctx.Snapshot("4. 거친 격자 먼저 (시작 지형)", zInit, unit: "m", scale: "final",
                note: "거친 격자부터 풀어 보간한 지형. 솔버가 여기서 시작합니다.");
        }
        ctx.Snapshot("5. 솔버 결과", z, unit: "m", note: "물길 방향이 더 바뀌지 않을 때까지 되풀이한 정상상태 지표(선상지 포함).");
        ctx.Snapshot("6. 유량", Array.ConvertAll(F(f, "discharge_m3_per_yr"), v => Math.Log10(Math.Max(v, 1.0))), kind: "field",
            unit: "log₁₀ m³/yr", note: "정상상태 물길로 모은 연 유량입니다.");
        ctx.Snapshot("7. 드러난 암석", Bytes(f, "surface_rock"), kind: "category",
            note: "지표에 드러난 지층 번호. 경사를 만든 암석과 같습니다(점수표 검사).");
        ctx.Snapshot("8. 선상지", Mask(f, "fan"), kind: "mask", note: "산지 앞 부채꼴 퇴적(후처리, 정상상태 아님) 칸입니다.");
        double[] river = Mask(f, "is_river");
        double[] lake = Mask(f, "is_lake");
        ctx.Snapshot("9. 강과 호수", river.Zip(lake, (r, l) => r + (2.0 * l)).ToArray(), kind: "mask",
            note: "강(1, 유량 문턱 이상)과 호수(2) 칸입니다.");
        ctx.Snapshot("10. 흙 두께", F(f, "soil_thickness_m"), kind: "field", unit: "m", note: "경사·융기·기온·강수로 정한 흙 두께입니다.");
        double[] wt = F(f, "water_table_m");
        ctx.Snapshot("11. 지하수면 깊이", z.Zip(wt, (a, b) => a - b).ToArray(), kind: "field", unit: "m",
            note: "지표 − 지하수면. 0 이면 지하수가 지표에 닿습니다(알려진 결함: 너무 넓음).");
        double[] c0 = F(f, "cave_level_0_m");
        double[] c1 = F(f, "cave_level_1_m");
        double[] caves = new double[z.Length];
        for (int k = 0; k < z.Length; k++)
        {
            double v = (double.IsFinite(c0[k]) ? 1.0 : 0.0) + (double.IsFinite(c1[k]) ? 2.0 : 0.0);
            caves[k] = v > 0 ? v : double.NaN;
        }
        ctx.Snapshot("12. 동굴 층", caves, kind: "category", note: "1 = 아래층(지하수면, 잠김), 2 = 위층(옛 지하수면, 마름), 3 = 둘 다. 회색 = 없음.");

        var sd = (OrderedDictionary<string, object?>)st.Diag["solver"]!;
        if (sd.TryGetValue("history", out object? h) && h is List<object?> hist && hist.Count > 0)
        {
            var rows = hist.Cast<OrderedDictionary<string, object?>>().ToList();
            double[] it = rows.Select(r => Convert.ToDouble(r["iteration"], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            double Get(OrderedDictionary<string, object?> r, string k) => Convert.ToDouble(r[k], System.Globalization.CultureInfo.InvariantCulture);
            ctx.Trace("n_changed", it, rows.Select(r => Get(r, "n_changed")), label: "물길 방향이 바뀐 칸", unit: "칸", xlabel: "솔버 반복");
            ctx.Trace("max_dz", it, rows.Select(r => Get(r, "max_dz")), label: "최대 고도 변화", unit: "m", xlabel: "솔버 반복");
            ctx.Trace("n_frozen", it, rows.Select(r => Get(r, "n_frozen")), label: "진동으로 고정한 칸", unit: "칸", xlabel: "솔버 반복");
        }
        var sec = (OrderedDictionary<string, object?>)st.Diag["seconds"]!;
        var items = new List<(string, double)>
        {
            ("미리 풀기", Convert.ToDouble(sec["presolve"], System.Globalization.CultureInfo.InvariantCulture)),
            ("지질 기둥", Convert.ToDouble(sec["geology"], System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (sec.TryGetValue("stages_detail", out object? det) && det is OrderedDictionary<string, object?> detail)
        {
            foreach ((string k, object? v) in detail)
            {
                if (k != "total" && v is double dv)
                {
                    items.Add((k, dv));
                }
            }
        }
        items.Add(("점수표", Convert.ToDouble(sec["scorecard"], System.Globalization.CultureInfo.InvariantCulture)));
        ctx.Bar("seconds", items, label: "단계별 시간", unit: "s");
    }
}
