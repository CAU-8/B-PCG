using Bpcg.Volume;
using Xunit;

namespace Bpcg.Tests.Volume;

/// <summary>싱크홀 둘레의 그릇 모양으로 꺼진 땅 (docs/pipeline.md 10장): 크기를 정하는 규칙과 단면.</summary>
public sealed class DolineTests
{
    private const double FullArea = 1.0e6;
    private const double RadiusMax = 60.0;
    private const double Ratio = 0.25;
    private const double Spacing = 300.0;

    [Fact]
    public void RadiusGrowsWithTheAreaThatDrainsIntoTheSinkhole()
    {
        // 넓이 1 km² 이상이면 가장 큰 반지름 60 m, 깊이는 0.25 × 지름 = 30 m.
        Assert.Equal((60.0, 30.0), HeroVolume.DolineSize(FullArea, FullArea, RadiusMax, Ratio, Spacing, depthToCaveM: 200.0));
        Assert.Equal((60.0, 30.0), HeroVolume.DolineSize(4.0e6, FullArea, RadiusMax, Ratio, Spacing, depthToCaveM: 200.0));
        // 넓이 0.25 km² 이면 60 × √0.25 = 30 m, 깊이 15 m.
        Assert.Equal((30.0, 15.0), HeroVolume.DolineSize(2.5e5, FullArea, RadiusMax, Ratio, Spacing, depthToCaveM: 200.0));
        // 넓이 2만 m² 이면 60 × √0.02 ≈ 8.5 m 라 아래 한계 15 m, 깊이 7.5 m.
        Assert.Equal((HeroVolume.DolineRadiusMinM, 7.5), HeroVolume.DolineSize(2.0e4, FullArea, RadiusMax, Ratio, Spacing, depthToCaveM: 200.0));
    }

    [Fact]
    public void DepthStaysAboveTheCaveAndBowlsDoNotOverlap()
    {
        // 지표에서 동굴 층까지 10 m 면 깊이는 그 절반인 5 m 를 넘지 않습니다 (규칙대로면 7.5 m).
        Assert.Equal((15.0, 5.0), HeroVolume.DolineSize(2.0e4, FullArea, RadiusMax, Ratio, Spacing, depthToCaveM: 10.0));
        // 싱크홀 사이 최소 거리가 100 m 면 반지름은 그 0.45 배인 45 m 를 넘지 않습니다.
        (double radius, double depth) = HeroVolume.DolineSize(FullArea, FullArea, RadiusMax, Ratio, sinkSpacingM: 100.0, depthToCaveM: 200.0);
        Assert.Equal(45.0, radius);
        Assert.Equal(22.5, depth);
        // 반지름이나 깊이 비가 0 이거나, 싱크홀을 솎지 않거나(거리 0), 동굴 층이 지표에 닿아 있으면 꺼뜨리지 않습니다.
        Assert.Equal((0.0, 0.0), HeroVolume.DolineSize(FullArea, FullArea, 0.0, Ratio, Spacing, 200.0));
        Assert.Equal((0.0, 0.0), HeroVolume.DolineSize(FullArea, FullArea, RadiusMax, 0.0, Spacing, 200.0));
        Assert.Equal((0.0, 0.0), HeroVolume.DolineSize(FullArea, FullArea, RadiusMax, Ratio, 0.0, 200.0));
        Assert.Equal((0.0, 0.0), HeroVolume.DolineSize(FullArea, FullArea, RadiusMax, Ratio, Spacing, 0.0));
    }

    [Fact]
    public void ProfileIsDeepestAtTheCentreAndFlatAtTheRim()
    {
        const double depth = 8.0;
        const double radius = 20.0;
        Assert.Equal(depth, HeroVolume.DolineProfile(depth, radius, 0.0));
        // 반지름의 절반에서는 8 × (1 − 0.25)² = 4.5 m.
        Assert.Equal(4.5, HeroVolume.DolineProfile(depth, radius, 10.0), 12);
        Assert.Equal(0.0, HeroVolume.DolineProfile(depth, radius, radius));
        Assert.Equal(0.0, HeroVolume.DolineProfile(depth, radius, 35.0));
        Assert.Equal(0.0, HeroVolume.DolineProfile(depth, 0.0, 0.0));
        // 가운데에서 멀어질수록 얕아지고, 가장자리 바로 안쪽은 거의 0 이라 둘레 지표와 매끄럽게 이어집니다.
        double prev = depth;
        for (double d = 1.0; d < radius; d += 1.0)
        {
            double now = HeroVolume.DolineProfile(depth, radius, d);
            Assert.True(now < prev, $"거리 {d} m 에서 깊이가 줄지 않았습니다");
            prev = now;
        }
        Assert.True(HeroVolume.DolineProfile(depth, radius, 19.0) < 0.1);
    }
}
