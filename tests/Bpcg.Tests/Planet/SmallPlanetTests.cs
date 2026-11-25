using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests.Planet;

/// <summary>
/// 반지름 10 km 작은 행성 설정(configs/planets/small10.toml)과, 지구 크기에 맞춰 코드에 두었던 길이를 설정으로 읽는 키의 시험.
/// </summary>
public sealed class SmallPlanetTests
{
    /// <summary>작은 행성을 시험용 크기(면마다 128칸, 칸 약 123 m)로 줄인 설정.</summary>
    private static Config Small10(params (string Key, object? Value)[] extra)
    {
        var overrides = new List<KeyValuePair<string, object?>>
        {
            new("profile.grid.coarse_n_per_face", 32L),
            new("profile.grid.l0_n_per_face", 128L),
        };
        overrides.AddRange(extra.Select(e => new KeyValuePair<string, object?>(e.Key, e.Value)));
        return Config.LoadConfig("small10", "tiny").WithOverrides(overrides);
    }

    private static double LandFraction(PlanetState planet)
    {
        bool[] ocean = planet.Fields.Get<bool>("is_ocean");
        double[] area = planet.Graph.Area;
        double land = 0.0;
        double all = 0.0;
        for (int c = 0; c < ocean.Length; c++)
        {
            all += area[c];
            land += ocean[c] ? 0.0 : area[c];
        }
        return land / all;
    }

    [Fact]
    public void 길이_키가_없는_설정은_지구_크기_기본값을_쓴다()
    {
        Config earth = Config.LoadConfig("earth", "tiny");
        Assert.Equal(Bpcg.Planet.Uplift.RiftMaxDistM, earth.FOr("uplift.rift_max_dist_m", Bpcg.Planet.Uplift.RiftMaxDistM));
        Assert.Equal(50_000.0, earth.FOr("crust.transition_half_width_m", Bpcg.Planet.Crust.TransitionHalfWidthM));
        Assert.Equal(123.0, earth.FOr("없는.키", 123.0));
        // 있는 키는 기본값이 아니라 설정 값을 돌려줍니다.
        Assert.Equal(80_000.0, earth.FOr("uplift.orogen_width_m", 1.0));
    }

    [Fact]
    public void 작은_행성_설정은_다섯_길이를_행성_크기에_맞게_적어_둔다()
    {
        Config cfg = Config.LoadConfig("small10", "tiny");
        Assert.Equal(10_000.0, cfg.F("planet.radius_m"));
        double circumference = 2.0 * Math.PI * cfg.F("planet.radius_m");
        foreach (string key in new[]
        {
            "uplift.rift_max_dist_m", "uplift.rift_width_m", "crust.transition_half_width_m",
            "geology.arc_fold_gap_m", "hero.dry_radius_m",
        })
        {
            double v = cfg.F(key);
            Assert.True(v > 0.0 && v < circumference / 20.0, $"{key} = {v} m 는 0 보다 크고 행성 둘레의 20분의 1({circumference / 20.0:F0} m)보다 작아야 합니다.");
        }
    }

    [Fact]
    public void 길이_키의_값이_숫자가_아니면_기본값으로_넘어가지_않고_오류를_낸다()
    {
        Config cfg = Config.LoadConfig("small10", "tiny").WithOverrides(
            [new KeyValuePair<string, object?>("uplift.rift_width_m", "넓게")]);
        Assert.ThrowsAny<Exception>(() => cfg.FOr("uplift.rift_width_m", 30_000.0));
    }

    [Fact]
    public void 열곡_길이가_0_이하면_받은_값과_고치는_법을_알려_준다()
    {
        Config cfg = Small10(("uplift.rift_width_m", 0.0));
        ArgumentException e = Assert.Throws<ArgumentException>(() => Pipeline.GeneratePlanet(cfg, null));
        Assert.Contains("uplift.rift_width_m(0 m)", e.Message, StringComparison.Ordinal);
        Assert.Contains("0 보다 커야", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 바다가_덮는_비율을_주면_이어진_바다의_넓이가_그_비율이_된다()
    {
        // 남쪽이 낮은 구: 고도가 z 좌표에 비례합니다. 북극 둘레에는 바다와 이어지지 않은 깊은 구덩이를 둡니다.
        CellGraph graph = Graph.SphereGraph(16, 1_000.0);
        int n = graph.NCells;
        double[] z = new double[n];
        double pit = 0.0;
        double total = 0.0;
        for (int c = 0; c < n; c++)
        {
            double up = graph.Pos[(3 * c) + 2] / 1_000.0;
            bool inPit = up > 0.97;
            z[c] = inPit ? -5_000.0 : 1_000.0 * up;
            total += graph.Area[c];
            pit += inPit ? graph.Area[c] : 0.0;
        }
        Assert.InRange(pit / total, 0.005, 0.05);

        foreach (double want in new[] { 0.3, 0.5, 0.66 })
        {
            double h = Bpcg.Planet.Ocean.SeaLevelForFraction(graph, z, want);
            bool[] ocean = Bpcg.Planet.Ocean.OceanMask(graph, z, h);
            double covered = Enumerable.Range(0, n).Where(c => ocean[c]).Sum(c => graph.Area[c]) / total;
            // 바다는 바라는 비율 이상이고, 칸 한 줄(전체의 2% 안쪽)보다 더 넘치지 않습니다.
            Assert.InRange(covered, want - 1.0e-9, want + 0.02);
            // 북극의 구덩이는 해수면보다 낮지만 바다와 이어지지 않아 바다가 아닙니다.
            Assert.DoesNotContain(Enumerable.Range(0, n), c => ocean[c] && z[c] == -5_000.0);
        }
        ArgumentException e = Assert.Throws<ArgumentException>(() => Bpcg.Planet.Ocean.SeaLevelForFraction(graph, z, 1.0));
        Assert.Contains("ocean.target_fraction", e.Message, StringComparison.Ordinal);
        Assert.Contains("1", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Bpcg.Planet.Ocean.SeaLevelForFraction(graph, z, 0.0));
    }

    [Fact]
    public void 작은_행성은_바다_비율로_해수면을_정하고_지구는_물의_부피로_정한다()
    {
        Assert.True(Bpcg.Planet.Materials.UsesOceanFraction(Config.LoadConfig("small10", "tiny")));
        Assert.False(Bpcg.Planet.Materials.UsesOceanFraction(Config.LoadConfig("earth", "tiny")));
        Assert.False(Bpcg.Planet.Materials.UsesOceanFraction(Config.LoadConfig("mars", "tiny")));

        PlanetState small = Pipeline.GeneratePlanet(Small10(), null);
        Assert.InRange(1.0 - LandFraction(small), 0.66, 0.665);
        // 해수면이 오르다가 낮은 분지 하나가 바다와 이어지면 바다가 한꺼번에 넓어집니다. 그래서 바라는 비율보다 조금 넘칠 수 있습니다.
        PlanetState half = Pipeline.GeneratePlanet(Small10(("ocean.target_fraction", 0.5)), null);
        Assert.InRange(1.0 - LandFraction(half), 0.5, 0.53);
    }

    [Fact]
    public void 작은_행성의_점수표는_길이_합격선을_설정에서_읽고_경사_법칙을_행성_단계_설정으로_셈한다()
    {
        Config cfg = Small10();
        OrderedDictionary<string, double> th = Bpcg.Metrics.Scorecard.Thresholds(cfg);
        Assert.Equal(10.0, th["bimodal_bin_m"]);
        Assert.Equal(25.0, th["bimodal_smooth_m"]);
        Assert.Equal(100.0, th["bimodal_min_separation_m"]);
        // 지구 설정에는 [metrics] 절이 없어 기본값을 씁니다.
        OrderedDictionary<string, double> earth = Bpcg.Metrics.Scorecard.Thresholds(Config.LoadConfig("earth", "tiny"));
        Assert.Equal(100.0, earth["bimodal_bin_m"]);
        Assert.Equal(250.0, earth["bimodal_smooth_m"]);
        Assert.Equal(1_000.0, earth["bimodal_min_separation_m"]);

        PlanetState small = Pipeline.GeneratePlanet(cfg, null);
        OrderedDictionary<string, object?> card = Pipeline.Score(cfg, planetFields: small.Fields, planetGraph: small.Graph);
        var law = (OrderedDictionary<string, object?>)card["planet.law_consistency"]!;
        // 행성 단계는 퇴적 항을 끄고 풀었습니다. 같은 설정으로 다시 셈하면 경사가 법칙과 소수 여섯째 자리까지 맞습니다.
        // 히어로 단계의 값(퇴적 항 켬)으로 셈하면 칸이 작은 이 행성에서는 수십 % 어긋나게 나옵니다.
        Assert.True((double)law["value"]! < 1.0e-6, $"행성 단계 경사 법칙의 어긋남 가운데값이 {law["value"]} 입니다. 1e-6 보다 작아야 합니다.");
        Assert.Equal(true, law["pass"]);
        var sea = (OrderedDictionary<string, object?>)card["planet.ocean_fraction"]!;
        Assert.Equal("forced", sea["kind"]);
    }

    /// <summary>육지 칸 가운데 융기가 음수(가라앉는 땅)인 칸의 비율.</summary>
    private static double SinkingLandFraction(PlanetState planet)
    {
        bool[] ocean = planet.Fields.Get<bool>("is_ocean");
        double[] uplift = planet.Fields.GetFloat64("uplift_m_per_yr");
        int land = 0;
        int sinking = 0;
        for (int c = 0; c < ocean.Length; c++)
        {
            if (!ocean[c])
            {
                land++;
                sinking += uplift[c] < 0.0 ? 1 : 0;
            }
        }
        return sinking / (double)land;
    }

    [Fact]
    public void 작은_행성에는_육지와_바다가_함께_있고_가라앉는_땅은_열곡_둘레뿐이다()
    {
        PlanetState small = Pipeline.GeneratePlanet(Small10(), null);
        Assert.InRange(LandFraction(small), 0.2, 0.6);
        double sinking = SinkingLandFraction(small);
        Assert.True(sinking < 0.2, $"작은 행성 설정에서 가라앉는 육지 칸이 {sinking:P1} 입니다. 열곡 둘레만 가라앉아 20% 아래여야 합니다.");

        // 열곡 길이를 지구 크기(50 km, 30 km)로 두면 행성(둘레 63 km)보다 커서 대륙 전체가 가라앉습니다.
        PlanetState earthSized = Pipeline.GeneratePlanet(
            Small10(("uplift.rift_max_dist_m", Bpcg.Planet.Uplift.RiftMaxDistM), ("uplift.rift_width_m", Bpcg.Planet.Uplift.RiftWidthM)), null);
        double sinkingEarth = SinkingLandFraction(earthSized);
        Assert.True(sinkingEarth > 0.8, $"지구 크기 열곡 길이에서 가라앉는 육지 칸이 {sinkingEarth:P1} 입니다. 대륙 전체가 가라앉아 80% 위여야 합니다.");
    }
}
