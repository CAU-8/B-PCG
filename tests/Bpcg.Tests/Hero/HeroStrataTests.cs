using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Geology;
using Bpcg.Hero;
using Xunit;

namespace Bpcg.Tests.Hero;

/// <summary>
/// 히어로 유역이 행성과 같은 규칙으로 지층을 다시 만드는지 봅니다 (Refine.HeroColumns, docs/pipeline.md 5.2·9장).
/// 습곡충상대의 지층은 물결처럼 오르내리고, 그 높이(습곡 변위)는 융기가 클수록 큽니다. 행성은 지각 세기 한계로 융기를
/// 줄이기 전에 지층을 만들므로, 히어로도 줄이기 전 융기(uplift_tectonic_m_per_yr)와 행성의 U_max 를 써야 같은 지층이 나옵니다.
/// </summary>
public sealed class HeroStrataTests
{
    private static readonly Lazy<PlanetState> Planet = new(() => Pipeline.GeneratePlanet(Tiny(), null));

    private static Config Tiny() => Config.LoadConfig("earth", "tiny");

    private static FieldSet GeologyInputs(FieldSet f, string upliftField) => new()
    {
        ["template_id"] = f["template_id"],
        ["fold_phase"] = f["fold_phase"],
        ["uplift_tectonic_m_per_yr"] = f[upliftField],
        ["exhumation_m"] = f["exhumation_m"],
    };

    private static double MaxAbsDiff(double[] a, double[] b)
    {
        Assert.Equal(a.Length, b.Length);
        double mx = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            mx = Math.Max(mx, Math.Abs(a[i] - b[i]));
        }
        return mx;
    }

    /// <summary>
    /// 같은 자리에서는 같은 지층: 행성 칸의 값을 보간 없이 히어로 규칙에 넣으면, 행성 자신의 기둥이 비트까지 같게 나와야 합니다.
    /// 층 바닥 = 깎인 두께 + 습곡 변위 − 층 깊이이므로, 층 바닥이 같으면 두 지층의 습곡 변위도 같습니다.
    /// </summary>
    [Fact]
    public void HeroRuleAtPlanetCellsRebuildsPlanetStrata()
    {
        PlanetState p = Planet.Value;
        Config cfg = Tiny();
        LayerColumns rebuilt = Refine.HeroColumns(p, GeologyInputs(p.Fields, "uplift_tectonic_m_per_yr"), cfg);
        Assert.Equal(p.Columns.Bottom, rebuilt.Bottom);
        Assert.Equal(p.Columns.Rock, rebuilt.Rock);

        // 빈 비교가 아님을 확인합니다. 습곡충상대 육지에 지각 세기 한계로 융기를 줄인 칸이 있고, 줄인 융기로 다시 만들면
        // 행성 지층과 달라집니다 (tiny 시드 0 에서 습곡대 육지 152칸 가운데 147칸, 층 바닥 차이 최대 약 265 m).
        byte[] tid = p.Fields.Get<byte>("template_id");
        bool[] ocean = p.Fields.Get<bool>("is_ocean");
        double[] uLimited = p.Fields.GetFloat64("uplift_m_per_yr");
        double[] uTectonic = p.Fields.GetFloat64("uplift_tectonic_m_per_yr");
        int reduced = 0;
        for (int c = 0; c < tid.Length; c++)
        {
            reduced += tid[c] == Model.FoldThrust && !ocean[c] && uLimited[c] < uTectonic[c] ? 1 : 0;
        }
        Assert.True(reduced > 0, "tiny 행성의 습곡충상대 육지에 융기를 줄인 칸이 없어, 이 시험이 아무것도 비교하지 않습니다");
        LayerColumns old = Refine.HeroColumns(p, GeologyInputs(p.Fields, "uplift_m_per_yr"), cfg);
        Assert.True(MaxAbsDiff(p.Columns.Bottom, old.Bottom) > 1.0, "줄인 융기로 만든 지층이 행성 지층과 같아, 이 시험이 고친 결함을 잡지 못합니다");
    }

    /// <summary>
    /// 히어로 유역을 끝까지 만든 뒤에도, 히어로 칸의 지층은 그 자리에서 보간한 판이 준 융기와 행성의 U_max 로 만든 습곡 변위를 씁니다.
    /// </summary>
    [Fact]
    public void HeroFoldsWithTectonicUpliftAndPlanetUMax()
    {
        PlanetState p = Planet.Value;
        Config cfg = Tiny();
        HeroState hero = Pipeline.GenerateHero(cfg, p, null);
        FieldSet h = hero.Fields;
        byte[] tid = h.Get<byte>("template_id");
        double uMax = Convert.ToDouble(
            ((OrderedDictionary<string, object?>)p.Diag["geology"]!)["u_max_m_per_yr"], System.Globalization.CultureInfo.InvariantCulture);
        double[] phase = h.GetFloat64("fold_phase");
        double[] exh = h.GetFloat64("exhumation_m");
        double[] disp = Model.FoldDisplacementFromPhase(phase, h.GetFloat64("uplift_tectonic_m_per_yr"), tid, cfg, uMax);
        (double[] bottom, byte[] rock) = Model.BuildColumns(tid, exh, disp, cfg);
        Assert.Equal(bottom, hero.Columns.Bottom);
        Assert.Equal(rock, hero.Columns.Rock);

        // tiny 시드 0 의 히어로 유역은 모두 습곡충상대이고 모든 칸의 융기가 줄었습니다. 줄인 융기를 쓰던 옛 규칙과는 변위가 최대 약 6 m 다릅니다.
        int fold = Array.FindAll(tid, t => t == Model.FoldThrust).Length;
        Assert.True(fold > 0, "tiny 히어로 유역에 습곡충상대가 없어, 이 시험이 습곡 변위를 비교하지 않습니다");
        double[] oldDisp = Model.FoldDisplacementFromPhase(phase, h.GetFloat64("uplift_m_per_yr"), tid, cfg, uMax);
        Assert.True(MaxAbsDiff(disp, oldDisp) > 1.0, "줄인 융기로 구한 변위가 새 변위와 같아, 이 시험이 고친 결함을 잡지 못합니다");
    }
}
