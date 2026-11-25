using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Hydro;
using Bpcg.Landscape;
using Bpcg.Planet;
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
    public void SubsidingLandKeepsTheDepthThatSedimentCouldNotFill()
    {
        // 물길: 칸 0 → 1 → 2 → 3(출구). 칸마다 넓이 3.2 × 10⁸ m².
        // 칸 0 은 한 해 6.25 × 10⁻⁶ m 솟아 흙 2,000 m³/yr 를 냅니다. 칸 1 과 2 는 한 해 3 × 10⁻⁵ m 가라앉아 빈자리가 9,600 m³/yr 씩 생깁니다.
        long[] rcv = [1, 2, 3, 3];
        long[] order = [3, 2, 1, 0];
        double[] area = [3.2e8, 3.2e8, 3.2e8, 3.2e8];
        double[] uplift = [6.25e-6, -3.0e-5, -3.0e-5, 0.0];
        bool[] outlet = [false, false, false, true];
        double[] d = Hollows.StarvedDepth(rcv, order, uplift, area, outlet, ageYr: 3.0e7);
        // 칸 1: 흙 2,000 이 들어와 7,600 m³/yr 가 남음 → 7,600 × 3 × 10⁷ ÷ (3.2 × 10⁸) = 712.5 m. 흙은 다 쌓여 칸 2 로 넘어가지 않음.
        // 칸 2: 흙이 없어 9,600 m³/yr 가 다 남음 → 900 m.
        Assert.Equal([0.0, 712.5, 900.0, 0.0], d.Select(v => Math.Round(v, 9)));
        // 흙이 빈자리보다 많이 들어오면 다 메워지고 남은 흙이 아래로 넘어갑니다: 칸 0 이 20,000 m³/yr 를 내면 칸 1 은 0 m,
        // 칸 2 는 넘어온 10,400 으로 다 메워져 0 m.
        uplift[0] = 6.25e-5;
        Assert.Equal([0.0, 0.0, 0.0, 0.0], Hollows.StarvedDepth(rcv, order, uplift, area, outlet, 3.0e7));
        // 가라앉는 몫: 칸 1 이 분지와 분지 사이의 턱(몫 0)이면 빈자리가 생기지 않아 0 m 이고, 흙 2,000 은 칸 2 로 넘어가
        // 칸 2 에 7,600 m³/yr 가 남습니다 (712.5 m). 몫 0.5 인 칸은 빈자리가 절반(4,800 m³/yr)입니다.
        uplift[0] = 6.25e-6;
        Assert.Equal(
            [0.0, 0.0, 712.5, 0.0],
            Hollows.StarvedDepth(rcv, order, uplift, area, outlet, 3.0e7, share: [1.0, 0.0, 1.0, 1.0]).Select(v => Math.Round(v, 9)));
        Assert.Equal(
            (4800.0 - 2000.0) * 3.0e7 / 3.2e8,
            Hollows.StarvedDepth(rcv, order, uplift, area, outlet, 3.0e7, share: [1.0, 0.5, 1.0, 1.0])[1], 9);
        // 나이가 0 이면 깊이도 0 입니다.
        uplift[0] = 0.0;
        Assert.All(Hollows.StarvedDepth(rcv, order, uplift, area, outlet, 0.0), v => Assert.Equal(0.0, v));
        ArgumentException e = Assert.Throws<ArgumentException>(() => Hollows.StarvedDepth(rcv, order, uplift, area, outlet, -1.0));
        Assert.Contains("-1", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoweredBedStaysAboveTheSeaAndKeepsDeeperBasinsLower()
    {
        // 높이 401 m 인 땅: 100 m 낮추면 1 + 400² ÷ 500 = 321 m, 700 m 낮추면 1 + 400² ÷ 1,100 ≈ 146.45 m.
        Assert.Equal(321.0, Hollows.LoweredBed(401.0, 100.0), 9);
        Assert.Equal(146.4545, Hollows.LoweredBed(401.0, 700.0), 4);
        Assert.Equal(401.0, Hollows.LoweredBed(401.0, 0.0));
        // 아무리 깊이 낮춰도 바다 위 1 m 보다 낮아지지 않고, 더 깊이 낮춘 쪽이 늘 더 낮습니다.
        Assert.True(Hollows.LoweredBed(401.0, 1.0e6) > Hollows.BedAboveSeaM);
        Assert.True(Hollows.LoweredBed(401.0, 900.0) < Hollows.LoweredBed(401.0, 700.0));
        // 바다 높이 가까이의 땅은 그대로 둡니다.
        Assert.Equal(0.5, Hollows.LoweredBed(0.5, 100.0));
    }

    /// <summary>
    /// 칸 7개가 한 줄로 놓인 땅 (칸 간격 1 km): 칸 0 이 출구(0 m), 칸 1 이 턱(rim m), 칸 2~4 가 웅덩이, 칸 5·6 이 높은 땅입니다.
    /// 웅덩이 세 칸은 높이 9 m 를 depth 만큼 낮춘 땅이고, 들어오는 zNew 는 LoweredBed 로 낮춘 바닥입니다.
    /// </summary>
    private static (CellGraph Graph, double[] Z, double[] Depth, bool[] Outlet, double[] ZNew) Bowl(double rim, double[] depth3)
    {
        CellGraph g = Graph.FlatGraph(1, 7, 1000.0);
        double[] z = [0.0, rim, 9.0, 9.0, 9.0, 20.0, 30.0];
        double[] depth = [0.0, 0.0, depth3[0], depth3[1], depth3[2], 0.0, 0.0];
        bool[] outlet = [true, false, false, false, false, false, false];
        double[] zNew = z.Select((v, c) => Hollows.LoweredBed(v, depth[c])).ToArray();
        return (g, z, depth, outlet, zNew);
    }

    [Fact]
    public void ShapeBedsKeepsTheBowlShapeAndTheSpill()
    {
        // 넘칠 높이 6 m 인 웅덩이. 그냥 빼면 바닥이 −291, −391, −291 m 라 바다보다 낮으므로, 가장 낮은 칸이 1 m 에 닿게
        // k = (6 − 1) ÷ (6 + 391) = 5 ÷ 397 로 누릅니다: 가운데 1 m, 양옆 6 − 297 × 5 ÷ 397 ≈ 2.26 m.
        // LoweredBed 로는 1.157 m 와 1.208 m 라 바닥이 거의 평평했습니다.
        (CellGraph g, double[] z, double[] depth, bool[] outlet, double[] zNew) = Bowl(6.0, [300.0, 400.0, 300.0]);
        double[] before = (double[])zNew.Clone();
        int squeezed = Hollows.ShapeBeds(g, z, depth, outlet, zNew);
        Assert.Equal(1, squeezed);
        Assert.Equal(Hollows.BedAboveSeaM, zNew[3], 12);
        Assert.Equal(6.0 - (297.0 * 5.0 / 397.0), zNew[2], 12);
        Assert.Equal(zNew[2], zNew[4]);
        Assert.True(zNew[2] - zNew[3] > 1.0 && before[2] - before[3] < 0.06);
        // 웅덩이 밖은 그대로이고, 넘칠 높이도 그대로입니다.
        Assert.Equal(before.Where((_, c) => c < 2 || c > 4), zNew.Where((_, c) => c < 2 || c > 4));
        Assert.Equal(
            Depressions.FillDepressions(before, g.Nbr, outlet)[3], Depressions.FillDepressions(zNew, g.Nbr, outlet)[3]);
        // 그냥 빼도 바다 1 m 위에 남는 웅덩이(턱 12 m, 깊이 3·5·3 m)는 누르지 않고 뺀 높이 6, 4, 6 m 를 그대로 씁니다.
        (g, z, depth, outlet, zNew) = Bowl(12.0, [3.0, 5.0, 3.0]);
        Assert.Equal(0, Hollows.ShapeBeds(g, z, depth, outlet, zNew));
        Assert.Equal([6.0, 4.0, 6.0], zNew[2..5]);
        Assert.Throws<ArgumentException>(() => Hollows.ShapeBeds(g, z, new double[3], outlet, zNew));
    }

    [Fact]
    public void BasinsAppearOnlyWhereIceScouredOrTheLandSinks()
    {
        (CellGraph g, double[] z, bool[] ocean) = TiltedPlanet();
        int n = g.NCells;
        double[] unit = g.Unit();
        double[] lat = Enumerable.Range(0, n).Select(c => Math.Asin(unit[(c * 3) + 2])).ToArray();
        // 기온: 북위 30° 위쪽은 여름 기온이 +2 °C 가 되게 둡니다 (연평균 = 2 − 16 × sin 위도). 강수 0.5 m 로는 지금 눈이 남지 않지만
        // (1.27 m 가 필요), 빙하기(−6 °C)에는 여름이 −4 °C 라 남습니다. 그 아래는 따뜻해서(+20 °C) 얼음이 덮인 적이 없습니다.
        double[] temp = lat.Select(v => v > Math.PI / 6.0 ? 2.0 - (16.0 * Math.Sin(v)) : 20.0).ToArray();
        double[] precip = Enumerable.Repeat(0.5, n).ToArray();
        long[] rcv = Routing.D8Receivers(z, g.Nbr, g.Dist, ocean).Rcv;
        long[] order = Routing.TopoOrder(rcv);
        double[] uplift = Enumerable.Repeat(1.0e-6, n).ToArray();
        Config cfg = Cfg(
            ("lakes.hollow_depth_m", 3000.0), ("lakes.hollow_wavelength_m", 6.0e6), ("lakes.hollow_fraction", 0.5), ("lakes.hollow_age_yr", 0.0));
        var causes = new BasinCauses(rcv, order, uplift, temp, precip, lat);
        BasinResult r = Hollows.AddBasins(g, z, ocean, new double[n], cfg, causes);
        // 얼음이 덮였던 땅은 북위 30° 위쪽의 육지입니다.
        for (int c = 0; c < n; c++)
        {
            byte expect = !ocean[c] && lat[c] > Math.PI / 6.0 ? Glaciation.IceAge : Glaciation.None;
            Assert.Equal(expect, r.Ice[c]);
        }
        Assert.Contains(true, r.Hollow);
        Assert.True(r.NGlacial > 0);
        Assert.Equal(0, r.NTectonic);
        // 낮춘 칸은 모두 얼음이 덮였던 땅 안에 있습니다. 그 밖의 땅은 솔버 높이 그대로입니다.
        for (int c = 0; c < n; c++)
        {
            Assert.True(!r.Hollow[c] || r.Ice[c] == Glaciation.IceAge, $"칸 {c} 는 얼음이 덮인 적 없는 땅인데 낮췄습니다");
            Assert.True(r.Hollow[c] ? r.Z[c] < z[c] : r.Z[c] == z[c]);
            Assert.True(ocean[c] || r.Z[c] >= Hollows.BedAboveSeaM);
        }
        // 따뜻한 땅 한 칸이 흙 없이 한 해 3 × 10⁻⁵ m 씩 가라앉으면, 얼음이 없어도 그 칸이 낮아집니다.
        // 분지를 마디로 나누지 않으면(rift_segment_m = 0) 3천만 년 동안 900 m 가 다 남습니다.
        int warm = Enumerable.Range(0, n).First(c => !ocean[c] && lat[c] < 0.0 && lat[c] > -0.3 && z[c] > 200.0);
        uplift = new double[n];
        uplift[warm] = -3.0e-5;
        var sinking = new BasinCauses(rcv, order, uplift, temp, precip, lat);
        Config whole = cfg.WithOverrides([new("lakes.rift_segment_m", 0.0)]);
        BasinResult t = Hollows.AddBasins(g, z, ocean, new double[n], whole, sinking);
        Assert.True(t.Hollow[warm]);
        Assert.Equal(1, t.NTectonic);
        // 900 m 를 그냥 빼면 바다보다 낮아지므로(높이 200~510 m), 웅덩이 바닥을 바다 1 m 위로 눌러 담습니다. 칸 하나뿐인 웅덩이라
        // 그 칸이 가장 깊은 칸이고, 바닥은 정확히 1 m 입니다. (얼음이 긁은 웅덩이도 3,000 m 를 빼면 바다보다 낮아 함께 눌립니다.)
        Assert.True(t.NSqueezed >= 1);
        Assert.Equal(Hollows.BedAboveSeaM, t.Z[warm], 9);
        // 마디로 나누면(지구 설정 200 km) 그 칸의 가라앉는 몫만큼만 낮아져, 위보다 깊어지지 않습니다.
        BasinResult parted = Hollows.AddBasins(g, z, ocean, new double[n], cfg, sinking);
        Assert.True(parted.Z[warm] >= t.Z[warm] && parted.Z[warm] <= z[warm]);
        // 설정에 lakes.lgm_cooling_c 가 없으면(예전 설정) 원인을 따지지 않고 예전처럼 어디에나 둡니다.
        BasinResult old = Hollows.AddBasins(g, z, ocean, new double[n], cfg, causes: null);
        Assert.All(old.Ice, v => Assert.Equal(Glaciation.None, v));
        Assert.Contains(Enumerable.Range(0, n), c => old.Hollow[c] && lat[c] < Math.PI / 6.0);
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
        // 한 칸마다 1 mm 씩 높게 메우면(stepM), 메운 칸이 넘칠 높이보다 조금 높아 그 물이 흘러갈 더 낮은 이웃이 생깁니다.
        (double[] zStep, bool[] filledStep, _, _) = Hollows.FillShallow(g, z, outlet, minDepthM: 2.0, stepM: 1e-3);
        Assert.Equal(filled, filledStep);
        Assert.True(zStep[shallow] > spill[shallow] && zStep[shallow] < spill[shallow] + 0.01);
        Assert.Contains(
            Enumerable.Range(0, CellGraph.NSlots).Select(s => g.Nbr[(shallow * CellGraph.NSlots) + s]),
            v => v >= 0 && zStep[v] < zStep[shallow]);
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
