using Bpcg.Bake;
using Bpcg.Geology;
using Xunit;

namespace Bpcg.Tests.Bake;

/// <summary>회랑 점수의 '고루 섞인 정도', 층리를 그리는 암석, 막힌 동굴 조각 지우기.</summary>
public sealed class CorridorAndMeshTests
{
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
