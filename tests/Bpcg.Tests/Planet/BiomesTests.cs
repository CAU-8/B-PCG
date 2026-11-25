using Bpcg.Core;
using Bpcg.Planet;
using Xunit;

namespace Bpcg.Tests.Planet;

/// <summary>바이옴 고르기 시험. 기온 띠와 건조 지수(강수 ÷ 잠재 증발산) 경계에서 맞는 바이옴이 나오는지 봅니다.</summary>
public sealed class BiomesTests
{
    [Theory]
    [InlineData(-20.0, 0.5, 0.1, Biomes.Ice)]
    [InlineData(-10.0, 0.5, 0.1, Biomes.Tundra)]
    [InlineData(-5.0, 0.5, 0.1, Biomes.BorealForest)]
    [InlineData(0.0, 0.04, 0.1, Biomes.Grassland)]
    [InlineData(5.0, 0.4, 0.4, Biomes.TemperateForest)]
    [InlineData(10.0, 0.39, 0.4, Biomes.Grassland)]
    [InlineData(10.0, 0.19, 0.4, Biomes.Shrubland)]
    [InlineData(20.0, 1.2, 1.0, Biomes.TropicalRainforest)]
    [InlineData(25.0, 0.6, 1.0, Biomes.Savanna)]
    [InlineData(25.0, 0.49, 1.0, Biomes.Desert)]
    public void Classify_PicksTheBandAndMoisture(double t, double p, double pet, byte expected) =>
        Assert.Equal(expected, Biomes.Classify(t, p, pet, ocean: false));

    [Fact]
    public void Classify_OceanIsWaterWhateverTheClimate() =>
        Assert.Equal(Biomes.Water, Biomes.Classify(25.0, 2.0, 1.0, ocean: true));

    [Fact]
    public void Classify_ArraysUseTheClimatePetFormula()
    {
        Config cfg = Config.LoadConfig("earth", "tiny");
        double petPer = cfg.F("climate.pet_per_degc_m_per_yr");
        double petBase = cfg.F("climate.pet_base_m_per_yr");
        // 25 °C 에서 잠재 증발산은 petPer × 25 + petBase. 강수를 그 0.49 배와 0.51 배로 두면 사막과 사바나 경계 양쪽입니다.
        double pet = (petPer * 25.0) + petBase;
        byte[] b = Biomes.Classify([25.0, 25.0, -30.0], [0.49 * pet, 0.51 * pet, 1.0], [false, false, true], cfg);
        Assert.Equal(new[] { Biomes.Desert, Biomes.Savanna, Biomes.Water }, b);
    }

    [Fact]
    public void Tables_HaveOneEntryPerBiome()
    {
        Assert.Equal(Biomes.Count, Biomes.Names.Length);
        Assert.Equal(Biomes.Count, Biomes.Keys.Length);
        Assert.Equal(Biomes.Count * 3, Biomes.ColorRgb.Length);
    }
}
