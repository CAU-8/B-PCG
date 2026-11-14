using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Hydro;
using Bpcg.Landscape;
using Xunit;

namespace Bpcg.Tests.Landscape;

/// <summary>호수가 생길 우묵한 자리 (docs/pipeline.md 7.5): 행성 지도의 낮춘 땅과 얕은 웅덩이 메우기.</summary>
public sealed class HollowsTests
{
    private static Config Cfg(params (string Key, object Value)[] overrides) =>
        Config.LoadConfig("earth", "tiny").WithOverrides(overrides.Select(o => new KeyValuePair<string, object?>(o.Key, o.Value)));

    [Fact]
    public void DepthLowersTheChosenShareAndSedimentFillsTheFloor()
    {
        // 칸 100개의 노이즈 값이 0, 1, …, 99. 땅의 0.2 를 낮추면 값이 큰 20칸(80~99)이 뽑힙니다.
        // 그 가운데 가장 큰 10분의 1(2칸, 98·99)은 최대 깊이 200 m 까지 내려갑니다.
        const int n = 100;
        double[] noise = Enumerable.Range(0, n).Select(c => (double)c).ToArray();
        bool[] outlet = new bool[n];
        double[] area = Enumerable.Repeat(1.0e8, n).ToArray();
        double[] qs = new double[n];
        var st = new LakeSettings(MinDepthM: 2.0, MinAreaM2: 0.0, HollowDepthM: 200.0, HollowWavelengthM: 1.0, HollowFraction: 0.2, HollowAgeYr: 1000.0, Seed: 0);
        double[] d = Hollows.Depth(noise, outlet, qs, area, st);
        Assert.All(Enumerable.Range(0, 81), c => Assert.Equal(0.0, d[c]));
        Assert.Equal(100.0, d[89], 9);  // (89 − 80) ÷ (98 − 80) = 0.5 → 200 m 의 절반
        Assert.Equal(200.0, d[98]);
        Assert.Equal(200.0, d[99]);
        // 흙 흐름 5 × 10⁶ m³/yr 가 1,000 년 동안 넓이 10⁸ m² 칸에 쌓이면 두께 50 m. 2 × 10⁷ 이면 200 m 라 다 메워집니다.
        qs[98] = 5.0e6;
        qs[99] = 2.0e7;
        d = Hollows.Depth(noise, outlet, qs, area, st);
        Assert.Equal(150.0, d[98], 9);
        Assert.Equal(0.0, d[99]);
        // 출구(바다) 칸은 낮추지 않고, 몫을 셀 때도 뺍니다: 땅 50칸의 0.2 는 10칸(값 40~49).
        bool[] halfOcean = Enumerable.Range(0, n).Select(c => c >= 50).ToArray();
        d = Hollows.Depth(noise, halfOcean, new double[n], area, st);
        Assert.Equal(10, d.Count(v => v > 0.0) + 1);  // 가장자리 칸(값 40)은 깊이 0
        Assert.All(Enumerable.Range(50, 50), c => Assert.Equal(0.0, d[c]));
    }

    /// <summary>남쪽 4분의 1 이 바다이고 북쪽으로 갈수록 높아지는 행성. 모든 땅 칸에 더 낮은 이웃이 있어 물이 고이지 않습니다.</summary>
    private static (CellGraph Graph, double[] Z, bool[] Ocean) TiltedPlanet()
    {
        CellGraph g = Graph.SphereGraph(12, 6.371e6);
        double[] unit = g.Unit();
        int n = g.NCells;
        double[] z = new double[n];
        bool[] ocean = new bool[n];
        for (int c = 0; c < n; c++)
        {
            double up = unit[(c * 3) + 2];
            ocean[c] = up < -0.5;
            z[c] = ocean[c] ? 0.0 : 10.0 + (1000.0 * (up + 0.5));
        }
        return (g, z, ocean);
    }

    [Fact]
    public void OnlyCellsThatHoldWaterStayLowered()
    {
        (CellGraph g, double[] z, bool[] ocean) = TiltedPlanet();
        Assert.Equal(z, Depressions.FillDepressions(z, g.Nbr, ocean));
        Config cfg = Cfg(
            ("lakes.hollow_depth_m", 3000.0), ("lakes.hollow_wavelength_m", 6.0e6), ("lakes.hollow_fraction", 0.3), ("lakes.hollow_age_yr", 0.0));
        (double[] zNew, bool[] hollow) = Hollows.AddHollows(g, z, ocean, new double[g.NCells], cfg);
        Assert.Contains(true, hollow);
        double[] filled = Depressions.FillDepressions(zNew, g.Nbr, ocean);
        for (int c = 0; c < z.Length; c++)
        {
            bool ponded = filled[c] - zNew[c] > Hollows.PondedM;
            // 물이 고이는 칸과 낮춘 채로 둔 칸은 같은 칸입니다. 다른 칸은 높이가 그대로이고, 물은 원래 땅 높이를 넘지 않습니다.
            Assert.Equal(hollow[c], ponded);
            Assert.True(hollow[c] ? zNew[c] < z[c] : zNew[c] == z[c]);
            Assert.True(filled[c] <= z[c] + 1e-9, $"칸 {c} 의 수면 {filled[c]} 이 원래 땅 {z[c]} 보다 높습니다");
            Assert.False(hollow[c] && ocean[c]);
            // 낮춘 바닥도 바다(0 m)보다 1 m 이상 높습니다. 바닷가의 땅(가장 낮은 곳 약 10 m)을 3,000 m 낮추라고 해도 그렇습니다.
            Assert.True(ocean[c] || zNew[c] >= Hollows.BedAboveSeaM, $"칸 {c} 의 바닥 {zNew[c]} m 가 바다보다 낮습니다");
        }
        // 같은 입력이면 결과가 같습니다.
        (double[] zAgain, _) = Hollows.AddHollows(g, z, ocean, new double[g.NCells], cfg);
        Assert.Equal(zNew, zAgain);
    }

    [Fact]
    public void NothingIsLoweredOnAFlatGraphOrWhenTurnedOff()
    {
        CellGraph flat = Graph.FlatGraph(16, 16, 25.0);
        double[] zFlat = Enumerable.Range(0, 256).Select(c => 1.0 + (c % 16)).ToArray();
        bool[] outletFlat = Enumerable.Range(0, 256).Select(c => c % 16 == 0).ToArray();
        (double[] zA, bool[] hA) = Hollows.AddHollows(flat, zFlat, outletFlat, new double[256], Cfg(("lakes.hollow_depth_m", 3000.0)));
        Assert.Equal(zFlat, zA);
        Assert.DoesNotContain(true, hA);
        (CellGraph g, double[] z, bool[] ocean) = TiltedPlanet();
        (double[] zB, bool[] hB) = Hollows.AddHollows(g, z, ocean, new double[g.NCells], Cfg(("lakes.hollow_depth_m", 0.0)));
        Assert.Equal(z, zB);
        Assert.DoesNotContain(true, hB);
    }

    [Fact]
    public void ShallowPitsAreFilledAndDeepPitsAreKept()
    {
        // 서쪽으로 100 m 에 1 m 씩 낮아지는 비탈. 출구는 서쪽 끝 열입니다.
        const int n = 20;
        CellGraph g = Graph.FlatGraph(n, n, 25.0);
        double[] z = Enumerable.Range(0, n * n).Select(c => 50.0 + (0.01 * g.Pos[c * 3])).ToArray();
        bool[] outlet = Enumerable.Range(0, n * n).Select(c => c % n == 0).ToArray();
        int shallow = (5 * n) + 8;
        int deepA = (14 * n) + 10;
        int deepB = deepA + 1;
        z[shallow] -= 1.0;
        z[deepA] -= 6.0;
        z[deepB] -= 3.0;
        (double[] zNew, bool[] filled, int nFilled, int nKept) = Hollows.FillShallow(g, z, outlet, minDepthM: 2.0);
        Assert.Equal(1, nFilled);
        Assert.Equal(1, nKept);
        // 얕은 웅덩이(깊이 약 1 m)는 넘칠 높이까지 올라가고, 깊은 웅덩이와 다른 칸은 그대로입니다.
        double[] spill = Depressions.FillDepressions(z, g.Nbr, outlet);
        Assert.Equal(spill[shallow], zNew[shallow]);
        Assert.True(zNew[shallow] > z[shallow] + 0.5);
        for (int c = 0; c < z.Length; c++)
        {
            Assert.Equal(c == shallow, filled[c]);
            if (c != shallow)
            {
                Assert.Equal(z[c], zNew[c]);
            }
        }
        // 메운 뒤에 물이 고이는 칸은 깊은 웅덩이의 두 칸뿐입니다.
        double[] after = Depressions.FillDepressions(zNew, g.Nbr, outlet);
        int[] ponded = Enumerable.Range(0, after.Length).Where(c => after[c] - zNew[c] > Hollows.PondedM).ToArray();
        Assert.Equal([deepA, deepB], ponded);
        // 넓이 기준도 주면 (두 칸 = 1,250 m² 인데 기준 2,000 m²) 깊은 웅덩이도 메워 물이 고이는 칸이 없어집니다.
        (double[] zAll, _, int nFilledAll, int nKeptAll) = Hollows.FillShallow(g, z, outlet, minDepthM: 2.0, minAreaM2: 2000.0);
        Assert.Equal(2, nFilledAll);
        Assert.Equal(0, nKeptAll);
        double[] afterAll = Depressions.FillDepressions(zAll, g.Nbr, outlet);
        Assert.All(Enumerable.Range(0, afterAll.Length), c => Assert.True(afterAll[c] - zAll[c] <= Hollows.PondedM));
    }

    [Fact]
    public void WrongSettingsAndLengthsAreRejectedWithTheReceivedValues()
    {
        (CellGraph g, double[] z, bool[] ocean) = TiltedPlanet();
        ArgumentException e = Assert.Throws<ArgumentException>(() => Hollows.AddHollows(g, new double[7], ocean, new double[g.NCells], Cfg()));
        Assert.Contains("7", e.Message, StringComparison.Ordinal);
        e = Assert.Throws<ArgumentException>(
            () => Hollows.AddHollows(g, z, ocean, new double[g.NCells], Cfg(("lakes.hollow_fraction", 1.5))));
        Assert.Contains("hollow_fraction 1.5", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Hollows.FillShallow(g, new double[3], ocean, 2.0));
    }
}
