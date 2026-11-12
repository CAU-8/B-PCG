using System;
using Bpcg.Volume;
using Xunit;

namespace Bpcg.Tests.Volume;

/// <summary>강 선의 조절점 부드럽게 하기 (HeroVolume.SmoothLine).</summary>
public sealed class RiverLineTests
{
    [Fact]
    public void EndPointsStayAndCornerMovesInward()
    {
        // 동쪽으로 25 m, 남쪽으로 25 m 꺾이는 모서리.
        double[] xy = [0.0, 0.0, 25.0, 0.0, 25.0, -25.0];
        HeroVolume.SmoothLine(xy, 1);
        Assert.Equal(0.0, xy[0]);
        Assert.Equal(0.0, xy[1]);
        Assert.Equal(25.0, xy[4]);
        Assert.Equal(-25.0, xy[5]);
        // (앞 + 2·자기 + 뒤)/4 = (18.75, −6.25): 모서리 안쪽으로 약 8.8 m.
        Assert.Equal(18.75, xy[2]);
        Assert.Equal(-6.25, xy[3]);
        Assert.Equal(8.84, Math.Sqrt((6.25 * 6.25) + (6.25 * 6.25)), 2);
    }

    [Fact]
    public void StraightEvenlySpacedLineDoesNotMove()
    {
        double[] xy = [0.0, 0.0, 10.0, 5.0, 20.0, 10.0, 30.0, 15.0, 40.0, 20.0];
        double[] before = (double[])xy.Clone();
        HeroVolume.SmoothLine(xy, 2);
        for (int i = 0; i < xy.Length; i++)
        {
            Assert.Equal(before[i], xy[i], 12);
        }
    }

    [Fact]
    public void TwoPointLineAndZeroPassesAreLeftAlone()
    {
        double[] two = [1.0, 2.0, 3.0, 4.0];
        HeroVolume.SmoothLine(two, 3);
        Assert.Equal([1.0, 2.0, 3.0, 4.0], two);
        double[] three = [0.0, 0.0, 5.0, 9.0, 10.0, 0.0];
        HeroVolume.SmoothLine(three, 0);
        Assert.Equal([0.0, 0.0, 5.0, 9.0, 10.0, 0.0], three);
    }
}
