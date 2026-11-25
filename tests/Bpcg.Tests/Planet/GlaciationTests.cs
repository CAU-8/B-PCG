using System;
using Bpcg.Planet;
using Xunit;

namespace Bpcg.Tests.Planet;

/// <summary>빙하가 덮였던 땅 (docs/pipeline.md 7.5): 여름 기온과 강수로 얼음이 남는 땅을 가립니다.</summary>
public sealed class GlaciationTests
{
    [Fact]
    public void RequiredPrecipFollowsTheOhmuraRelation()
    {
        // P [mm] = 645 + 296·T + 9·T² (Ohmura 외 1992). 여름 기온 1.2 °C 면 약 1,013 mm, 4.1 °C 면 약 2,010 mm.
        Assert.Equal(1.01316, Glaciation.RequiredPrecip(1.2), 5);
        Assert.Equal(2.00989, Glaciation.RequiredPrecip(4.1), 5);
        Assert.Equal(0.645, Glaciation.RequiredPrecip(0.0), 12);
        // 여름 기온 약 −2.35 °C 아래에서는 식의 값이 0 이하라, 강수가 얼마든 눈이 남습니다.
        Assert.Equal(-2.346, Glaciation.AlwaysIceSummerC, 3);
        Assert.Equal(0.0, Glaciation.RequiredPrecip(-2.5));
        Assert.Equal(0.0, Glaciation.RequiredPrecip(-40.0));
        Assert.True(Glaciation.RequiredPrecip(-2.3) > 0.0);
    }

    [Fact]
    public void SummerIsWarmerThanTheYearOnlyAwayFromTheEquator()
    {
        Assert.Equal(5.0, Glaciation.SummerTemperature(5.0, 0.0, 16.0), 12);
        Assert.Equal(5.0 + 8.0, Glaciation.SummerTemperature(5.0, Math.PI / 6.0, 16.0), 12);   // 위도 30°: 16 × 0.5
        Assert.Equal(5.0 + 8.0, Glaciation.SummerTemperature(5.0, -Math.PI / 6.0, 16.0), 12);  // 남반구도 같음
    }

    [Fact]
    public void IceCoverSeparatesNowIceAgeOnlyAndNever()
    {
        double lat60 = Math.PI / 3.0;  // 여름은 연평균 + 16 × sin 60° ≈ +13.86 °C
        // 칸 0: 연평균 −20 °C → 여름 −6.1 °C, 눈이 조금만 와도 남음 → 지금도 얼음.
        // 칸 1: 연평균 −12 °C → 여름 +1.86 °C 에는 1.23 m 가 필요한데 강수 1.0 m → 지금은 아님.
        //        빙하기(−6 °C)에는 여름 −4.1 °C → 얼음. 빙하기에만.
        // 칸 2: 칸 1 과 같은 기온에 강수 1.5 m → 지금도 얼음 (눈이 많으면 따뜻해도 남음).
        // 칸 3: 연평균 +5 °C → 빙하기 여름도 +12.9 °C → 얼음 없음.
        // 칸 4: 칸 0 과 같지만 바다 → 없음.
        // 칸 5: 적도, 연평균 −1 °C (높은 산). 여름이 따로 없어 −1 °C: 0.36 m 가 필요, 강수 0.3 m → 지금은 아님. 빙하기에는 −7 °C → 얼음.
        double[] t = [-20.0, -12.0, -12.0, 5.0, -20.0, -1.0];
        double[] p = [0.2, 1.0, 1.5, 1.0, 0.2, 0.3];
        double[] lat = [lat60, lat60, lat60, lat60, lat60, 0.0];
        bool[] ocean = [false, false, false, false, true, false];
        byte[] ice = Glaciation.IceCover(t, p, lat, ocean, coolingC: 6.0, summerAmplitudeC: 16.0);
        Assert.Equal(
            [Glaciation.Now, Glaciation.IceAge, Glaciation.Now, Glaciation.None, Glaciation.None, Glaciation.IceAge], ice);
        // 빙하기가 지금과 같은 기온이면(0 °C) 빙하기에만 덮였던 땅은 없습니다.
        byte[] same = Glaciation.IceCover(t, p, lat, ocean, coolingC: 0.0, summerAmplitudeC: 16.0);
        Assert.DoesNotContain(Glaciation.IceAge, same);
        ArgumentException e = Assert.Throws<ArgumentException>(() => Glaciation.IceCover(t, p, lat, ocean, -1.0, 16.0));
        Assert.Contains("lgm_cooling_c -1", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Glaciation.IceCover(t, new double[2], lat, ocean, 6.0, 16.0));
    }
}
