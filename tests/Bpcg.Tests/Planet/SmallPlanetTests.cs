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
