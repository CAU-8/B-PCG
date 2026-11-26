using System;
using System.Collections.Generic;
using Bpcg.Bake;
using Bpcg.Core;
using Bpcg.Geology;
using Xunit;

namespace Bpcg.Tests.Bake;

/// <summary>회랑 크기와 표본 격자, 회랑 점수의 '고루 섞인 정도', 층리를 그리는 암석, 막힌 동굴 조각 지우기.</summary>
public sealed class CorridorAndMeshTests
{
    [Fact]
    public void CorridorSidesRoundDownToWholeSampleSteps()
    {
        // 이미 정수배인 값은 그대로 둡니다. 기본 설정(laptop 6,000 m ÷ 2 m, tiny 1,200 m ÷ 8 m)의 회랑이 바뀌지 않습니다.
        Assert.Equal(6000.0, Corridor.FloorToMultiple(6000.0, 2.0));
        Assert.Equal(1200.0, Corridor.FloorToMultiple(1200.0, 8.0));
        // 정수배가 아니면 그 아래 정수배로 줄입니다.
        Assert.Equal(6000.0, Corridor.FloorToMultiple(6001.0, 2.0));
        Assert.Equal(1200.0, Corridor.FloorToMultiple(1207.9, 8.0));
        Assert.Equal(399.0, Corridor.FloorToMultiple(401.0, 3.0));
        // 나눈 값의 반올림 오차는 정수배로 봅니다: 0.3 ÷ 0.1 = 2.9999999999999996 이지만 0.3 을 그대로 둡니다.
        Assert.Equal(0.3, Corridor.FloorToMultiple(0.3, 0.1));
    }

    [Fact]
    public void GridReachesTheFarEdgesOfTheRectangle()
    {
        // 동서 6,000 m × 남북 1,000 m 사각형을 2 m 간격으로 덮으면 첫 표본은 북서 귀퉁이, 마지막 표본은 동쪽·남쪽 변 위에 놓입니다.
        (double[] xs, double[] ys) = Corridor.Grid((1234.5, 7234.5, -500.25, 499.75), 2.0);
        Assert.Equal(3001, xs.Length);
        Assert.Equal(501, ys.Length);
        Assert.Equal(1234.5, xs[0]);
        Assert.Equal(7234.5, xs[^1], 9);
        Assert.Equal(499.75, ys[0]);
        Assert.Equal(-500.25, ys[^1], 9);
        // 재질 부피의 4 m 격자는 4 m 의 정수배가 아닌 변(6,002 m)을 덮도록 마지막 열을 변 너머 6,004 m 에 둡니다.
        (double[] sx, double[] sy) = Corridor.Grid((0.0, 6002.0, 0.0, 4.0), 4.0);
        Assert.Equal(1502, sx.Length);
        Assert.Equal(6004.0, sx[^1]);
        Assert.Equal([4.0, 0.0], sy);
    }

    [Fact]
    public void SizeSettingsRejectsSidesShorterThanOneSample()
    {
        Assert.Equal((1200.0, 400.0, 8.0), Corridor.SizeSettings(Config.LoadConfig("earth", "tiny")));
        Config narrow = Config.LoadConfig("earth", "tiny").WithOverrides(
            [new KeyValuePair<string, object?>("profile.corridor.width_m", 4.0)]);
        ArgumentException e = Assert.Throws<ArgumentException>(() => Corridor.SizeSettings(narrow));
        Assert.Contains("width_m 4 m", e.Message, StringComparison.Ordinal);
        Assert.Contains("voxel_m 8 m", e.Message, StringComparison.Ordinal);
        Assert.Contains("8 m 이상으로 늘리거나", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EvennessIsZeroForOneKindAndOneForFourEqualKinds()
    {
        Assert.Equal(0.0, Corridor.Evenness([100, 0, 0, 0]));
        Assert.Equal(1.0, Corridor.Evenness([25, 25, 25, 25]), 12);
        Assert.Equal(0.5, Corridor.Evenness([50, 50, 0, 0]), 12);
        Assert.Equal(0.0, Corridor.Evenness([0, 0, 0, 0]));
        // 다섯 종류가 고르면 1 을 넘지 않게 자릅니다.
        Assert.Equal(1.0, Corridor.Evenness([20, 20, 20, 20, 20]));
    }

    [Fact]
    public void BedsAreDrawnOnlyInLayeredRocks()
    {
        Assert.True(Corridor.HasBeds(Rocks.Limestone));
        Assert.True(Corridor.HasBeds(Rocks.Shale));
        Assert.True(Corridor.HasBeds(Rocks.Gneiss));
        Assert.False(Corridor.HasBeds(Rocks.Granite));
        Assert.False(Corridor.HasBeds(Rocks.Soil));
        Assert.False(Corridor.HasBeds(Rocks.Alluvium));
        Assert.False(Corridor.HasBeds(Corridor.StrataWater));
    }

    [Fact]
    public void KeepReachableDropsPiecesWithoutAnAnchor()
    {
        // 조각 A: 꼭짓점 0-1-2-3 (삼각형 둘), 조각 B: 4-5-6 (삼각형 하나), 조각 C: 7-8-9 (삼각형 하나).
        int[] faces = [0, 1, 2, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        int[] map = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        bool[] anchor = new bool[10];
        anchor[3] = true; // A 가 바깥과 닿음
        anchor[8] = true; // C 가 바깥과 닿음
        int[] kept = Mesh.KeepReachable(faces, map, anchor, 10);
        Assert.Equal([0, 1, 2, 1, 2, 3, 7, 8, 9], kept);
    }

    [Fact]
    public void KeepReachableFollowsMergedVertices()
    {
        // 원래 꼭짓점 0..5 가 합친 꼭짓점 0..3 으로 모입니다 (2 와 3, 4 와 0 이 같은 자리).
        // 삼각형 (0,1,2) 와 (2,3,0) 은 합친 번호로 이어지므로, 원래 꼭짓점 5 의 표시가 둘 다 살립니다.
        int[] faces = [0, 1, 2, 2, 3, 0];
        int[] map = [0, 1, 2, 2, 0, 3];
        bool[] anchor = [false, false, false, false, false, true];
        Assert.Equal(faces, Mesh.KeepReachable(faces, map, anchor, 4));
        Assert.Empty(Mesh.KeepReachable(faces, map, new bool[6], 4));
    }
}
