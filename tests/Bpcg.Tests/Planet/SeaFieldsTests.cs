using System;
using System.Collections.Generic;
using System.Linq;
using Bpcg.Core;
using Bpcg.Planet;
using Xunit;

namespace Bpcg.Tests.Planet;

/// <summary>
/// 바다 필드 여섯 개(바다 물의 온도, 바다 얼음, 강물 퍼짐, 파도 받이, 바다 밑 바닥 종류, 해안 종류)의 시험.
/// 작은 격자에 손으로 놓은 바다와 뭍으로 식을 확인하고, 작은 행성 하나로 값의 범위와 '같은 시드면 같은 값'을 확인합니다.
/// </summary>
public sealed class SeaFieldsTests
{
    /// <summary>칸 한 변 (m). 한 줄 격자에서 이웃 칸까지의 거리입니다.</summary>
    private const double Dx = 100.0;

    /// <summary>바다 필드 여섯 개의 이름.</summary>
    private static readonly string[] SeaNames = ["sea_temp_c", "sea_ice", "river_plume", "wave_exposure", "seabed", "coast_kind"];

    /// <summary>칸 n 개가 서쪽에서 동쪽으로 한 줄로 늘어선 평면 격자. 칸마다 이웃은 양옆 두 칸뿐입니다.</summary>
    private static CellGraph Row(int n) => Graph.FlatGraph(1, n, Dx);

    /// <summary>자기 자신에게 물을 보내는(출구인) 물길 표.</summary>
    private static long[] SelfReceivers(int n) => Enumerable.Range(0, n).Select(c => (long)c).ToArray();

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

    /// <summary>시험용 크기의 작은 행성 (시드 0). 시험 둘이 함께 쓰고, 필드를 고치지 않습니다.</summary>
    private static readonly Lazy<PlanetState> Planet = new(() => Pipeline.GeneratePlanet(Small10(), null));

    /// <summary>두 배열의 바이트가 모두 같은지 (NaN 도 비트로 견줍니다).</summary>
    private static bool SameBytes(Array a, Array b)
    {
        if (a.GetType() != b.GetType() || a.Length != b.Length)
        {
            return false;
        }
        byte[] x = new byte[Buffer.ByteLength(a)];
        byte[] y = new byte[Buffer.ByteLength(b)];
        Buffer.BlockCopy(a, 0, x, 0, x.Length);
        Buffer.BlockCopy(b, 0, y, 0, y.Length);
        return x.AsSpan().SequenceEqual(y);
    }

    [Fact]
    public void 바다_물의_온도는_어는_온도_아래로_내려가지_않고_뭍은_NaN이다()
    {
        double[] t = SeaFields.SeaTemperature([12.0, -20.0, -1.8, 27.0, 30.0, -30.0], [true, true, true, true, false, false]);
        Assert.Equal(12.0, t[0]);
        Assert.Equal(SeaFields.FreezeC, t[1]);
        Assert.Equal(SeaFields.FreezeC, t[2]);
        Assert.Equal(27.0, t[3]);
        Assert.True(double.IsNaN(t[4]));
        Assert.True(double.IsNaN(t[5]));

        ArgumentException e = Assert.Throws<ArgumentException>(() => SeaFields.SeaTemperature([1.0, 2.0], [true]));
        Assert.Contains("기온 2, 바다 1", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 바다_얼음은_물_온도가_영하_1점5도_아래인_바다_칸이다()
    {
        // 마지막 값은 double 로는 −1.5 보다 낮지만 묶음에 적는 float32 로 반올림하면 −1.5 라서 얼음이 아닙니다.
        // 묶음에서 읽은 sea_temp_c 로 다시 셈해도 같은 답이 나오게 하려는 규칙입니다.
        bool[] ice = SeaFields.SeaIce([SeaFields.FreezeC, -1.6, -1.5, -1.4, 10.0, double.NaN, -1.50000001]);
        Assert.Equal(new[] { true, true, false, false, false, false, false }, ice);
    }

    [Fact]
    public void 강물_퍼짐은_200m마다_절반이_되고_뭍_너머로_건너뛰지_않는다()
    {
        // 칸: 0 강(뭍) → 1 바다, 2~5 바다, 6 뭍, 7~9 바다. 강어귀는 칸 1 이고 칸 6 이 바다를 둘로 가릅니다.
        CellGraph g = Row(10);
        bool[] ocean = [false, true, true, true, true, true, false, true, true, true];
        long[] rcv = SelfReceivers(10);
        rcv[0] = 1;
        bool[] river = new bool[10];
        river[0] = true;
        double full = 1_000.0;
        double[] q = new double[10];
        q[0] = 0.8 * full;

        double[] plume = SeaFields.RiverPlume(g, ocean, rcv, river, q, halfDistanceM: 200.0, fullDischargeM3PerYr: full);
        Assert.Equal(0.8, plume[1], 12);
        Assert.Equal(0.8 * Math.Pow(2.0, -0.5), plume[2], 12);
        Assert.Equal(0.4, plume[3], 12);
        Assert.Equal(0.2, plume[5], 12);
        // 뭍 칸은 0 이고, 뭍(칸 6) 너머의 바다는 물길이 이어지지 않아 0 입니다.
        Assert.Equal(0.0, plume[0]);
        Assert.Equal(0.0, plume[6]);
        Assert.All(new[] { 7, 8, 9 }, c => Assert.Equal(0.0, plume[c]));

        // 강어귀가 둘이면 값을 더합니다: 칸 6 도 강이 되어 칸 5 로 유량 0.4 × full 을 보냅니다.
        rcv[6] = 5;
        river[6] = true;
        q[6] = 0.4 * full;
        plume = SeaFields.RiverPlume(g, ocean, rcv, river, q, 200.0, full);
        Assert.Equal(0.8 + (0.4 * 0.25), plume[1], 12);
        Assert.Equal(0.4 + 0.2, plume[3], 12);
        Assert.Equal((0.8 * 0.25) + 0.4, plume[5], 12);

        // 큰 강은 1 에서 자릅니다: 세기 3 이면 강어귀와 200 m 밖(1.5)은 1, 400 m 밖은 0.75 입니다.
        rcv[6] = 6;
        river[6] = false;
        q[0] = 3.0 * full;
        plume = SeaFields.RiverPlume(g, ocean, rcv, river, q, 200.0, full);
        Assert.Equal(1.0, plume[1]);
        Assert.Equal(1.0, plume[3]);
        Assert.Equal(0.75, plume[5], 12);
        Assert.All(plume, v => Assert.InRange(v, 0.0, 1.0));
    }

    [Fact]
    public void 강물_퍼짐은_값이_0점01보다_작아지는_곳에서_멈춘다()
    {
        CellGraph g = Row(8);
        bool[] ocean = [false, true, true, true, true, true, true, true];
        long[] rcv = SelfReceivers(8);
        rcv[0] = 1;
        bool[] river = new bool[8];
        river[0] = true;
        double[] q = new double[8];

        // 세기 0.03 은 200 × log2(0.03 ÷ 0.01) ≈ 317 m 까지 퍼집니다: 300 m 밖(칸 4)은 0.03 × 2^(−1.5) ≈ 0.0106, 400 m 밖(칸 5)은 0 입니다.
        q[0] = 0.03;
        double[] plume = SeaFields.RiverPlume(g, ocean, rcv, river, q, 200.0, 1.0);
        Assert.Equal(0.03 * Math.Pow(2.0, -1.5), plume[4], 12);
        Assert.Equal(0.0, plume[5]);
        Assert.All(plume.Where(v => v > 0.0), v => Assert.True(v >= SeaFields.PlumeCutoff));

        // 세기가 0.01 보다 작은 강어귀는 아무 값도 보태지 않습니다.
        q[0] = 0.005;
        Assert.All(SeaFields.RiverPlume(g, ocean, rcv, river, q, 200.0, 1.0), v => Assert.Equal(0.0, v));

        // 강이 아닌 뭍 칸이 바다로 보내는 물은 세지 않습니다.
        q[0] = 5.0;
        river[0] = false;
        Assert.All(SeaFields.RiverPlume(g, ocean, rcv, river, q, 200.0, 1.0), v => Assert.Equal(0.0, v));

        ArgumentException e = Assert.Throws<ArgumentException>(() => SeaFields.RiverPlume(g, ocean, rcv, river, q, 0.0, 1.0));
        Assert.Contains("sea.plume_half_distance_m(0 m)", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SeaFields.RiverPlume(g, ocean, rcv, river, q, 200.0, 0.0));
        Assert.Throws<ArgumentException>(() => SeaFields.RiverPlume(g, ocean, rcv, river, new double[3], 200.0, 1.0));
    }

    [Fact]
    public void 강물_퍼짐의_거리는_뭍을_돌아가는_물길로_잰다()
    {
        // 세 줄 × 다섯 칸. 가운데 줄의 가운데 세 칸이 뭍이고, 그 한가운데 칸(7)이 북쪽 바다 칸(2)으로 물을 보내는 강입니다.
        //   0  1  2  3  4      바 바 바 바 바
        //   5  6  7  8  9  =   바 뭍 강 뭍 바
        //  10 11 12 13 14      바 바 바 바 바
        CellGraph g = Graph.FlatGraph(3, 5, Dx);
        bool[] ocean = Enumerable.Repeat(true, 15).ToArray();
        ocean[6] = ocean[7] = ocean[8] = false;
        long[] rcv = SelfReceivers(15);
        rcv[7] = 2;
        bool[] river = new bool[15];
        river[7] = true;
        double[] q = new double[15];
        q[7] = 1.0;

        double[] plume = SeaFields.RiverPlume(g, ocean, rcv, river, q, 200.0, 1.0);
        Assert.Equal(1.0, plume[2]);
        // 칸 12 는 강어귀(칸 2)에서 직선으로 200 m 지만 뭍에 막혀 있습니다.
        // 가장 짧은 물길은 2 → 1 → 5 → 11 → 12 로, 100 + 100√2 + 100√2 + 100 ≈ 483 m 입니다.
        double around = (2.0 * Dx) + (2.0 * Dx * Math.Sqrt(2.0));
        Assert.Equal(Math.Pow(2.0, -around / 200.0), plume[12], 12);
        Assert.True(plume[12] < 0.5 * plume[2]);
    }

    [Fact]
    public void 파도_받이는_트인_바다에서_1이고_곧은_해안_앞에서_절반쯤이다()
    {
        // 반지름 1 km 의 구. 북반구가 뭍, 남반구가 바다라 해안선이 적도를 따라 곧게 놓입니다. 칸 한 변은 약 45 m 이고 300 m 까지 잽니다.
        CellGraph g = Graph.SphereGraph(32, 1_000.0);
        int n = g.NCells;
        double[] axis = [0.0, 0.0, 1.0];
        bool[] ocean = Enumerable.Range(0, n).Select(c => g.Pos[(3 * c) + 2] < 0.0).ToArray();
        double[] exposure = SeaFields.WaveExposure(g, ocean, fetchCapM: 300.0, axis);

        var atCoast = new List<double>();
        var midway = new List<double>();
        for (int c = 0; c < n; c++)
        {
            double south = -Math.Asin(g.Pos[(3 * c) + 2] / 1_000.0) * 1_000.0;
            if (!ocean[c])
            {
                Assert.Equal(0.0, exposure[c]);
                continue;
            }
            Assert.InRange(exposure[c], 0.0, 1.0);
            if (south > 500.0)
            {
                // 해안에서 500 m 넘게 떨어진 칸은 어느 방향으로도 300 m 안에 뭍이 없습니다.
                Assert.Equal(1.0, exposure[c]);
            }
            bool touchesLand = Enumerable.Range(0, CellGraph.NSlots).Select(s => g.Nbr[(c * CellGraph.NSlots) + s]).Any(v => v >= 0 && !ocean[v]);
            if (touchesLand)
            {
                atCoast.Add(exposure[c]);
            }
            else if (south is > 120.0 and < 200.0)
            {
                midway.Add(exposure[c]);
            }
        }
        // 곧은 해안 바로 앞은 열여섯 방향 가운데 절반쯤이 바로 뭍입니다.
        Assert.All(atCoast, v => Assert.InRange(v, 0.3, 0.7));
        Assert.InRange(atCoast.Average(), 0.4, 0.6);
        // 해안에서 멀어질수록 뭍 쪽으로도 더 멀리 트이므로 값이 커지지만, 300 m 안에 뭍이 있으면 1 은 아닙니다.
        Assert.True(midway.Count > 50);
        Assert.True(midway.Average() > atCoast.Average() + 0.1);
        Assert.All(midway, v => Assert.True(v < 1.0));

        // 뭍이 하나도 없으면 모든 칸이 1 입니다.
        Assert.All(SeaFields.WaveExposure(g, Enumerable.Repeat(true, n).ToArray(), 300.0, axis), v => Assert.Equal(1.0, v));
    }

    [Fact]
    public void 파도_받이는_뭍으로_둘러싸인_작은_바다에서_낮다()
    {
        // 남극점 둘레 반지름 100 m 만 바다이고 나머지는 모두 뭍입니다. 면마다 31칸이라 칸 하나의 중심이 정확히 극점에 놓여,
        // 동쪽을 정할 수 없는 칸에서 자전축 대신 다른 축을 쓰는 길도 함께 확인합니다.
        CellGraph g = Graph.SphereGraph(31, 1_000.0);
        int n = g.NCells;
        bool[] ocean = Enumerable.Range(0, n).Select(c => g.Pos[(3 * c) + 2] < -1_000.0 * Math.Cos(0.1)).ToArray();
        int pole = Enumerable.Range(0, n).MinBy(c => g.Pos[(3 * c) + 2]);
        Assert.Equal(-1_000.0, g.Pos[(3 * pole) + 2], 9);
        Assert.True(ocean.Count(b => b) >= 9);

        double[] exposure = SeaFields.WaveExposure(g, ocean, 300.0, [0.0, 0.0, 1.0]);
        // 어느 방향으로도 200 m 안에 뭍이 있으므로 값은 200 ÷ 300 을 넘지 못하고, 한가운데는 100 ÷ 300 쯤입니다.
        Assert.All(Enumerable.Range(0, n).Where(c => ocean[c]), c => Assert.InRange(exposure[c], 0.0, 0.67));
        Assert.InRange(exposure[pole], 0.15, 0.45);
    }

    [Fact]
    public void 파도_받이는_재는_거리가_맞지_않으면_받은_값과_고치는_법을_알려_준다()
    {
        CellGraph g = Graph.SphereGraph(8, 10_000.0);
        bool[] ocean = Enumerable.Repeat(true, g.NCells).ToArray();
        // 지구 크기 기본값(160 km)은 반지름 10 km 행성 둘레의 4분의 1(약 15.7 km)보다 깁니다.
        ArgumentException e = Assert.Throws<ArgumentException>(() => SeaFields.WaveExposure(g, ocean, SeaFields.FetchCapM, [0.0, 0.0, 1.0]));
        Assert.Contains("sea.fetch_cap_m(160000 m)", e.Message, StringComparison.Ordinal);
        Assert.Contains("15708 m", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SeaFields.WaveExposure(g, ocean, 0.0, [0.0, 0.0, 1.0]));
        Assert.Throws<ArgumentException>(() => SeaFields.WaveExposure(g, ocean, 1_000.0, [0.0, 0.0, 0.0]));
        Assert.Throws<ArgumentException>(() => SeaFields.WaveExposure(Row(4), new bool[4], 100.0, [0.0, 0.0, 1.0]));
    }

    [Fact]
    public void 바다_밑_비탈은_이웃_바다_칸까지_가장_가파른_경사이고_둘레_바다_칸과_평균한다()
    {
        // 칸: 0 뭍(5 m), 1~3 바다(−10, −30, −40 m). 칸 사이는 100 m 입니다.
        CellGraph g = Row(4);
        bool[] ocean = [false, true, true, true];
        double[] slope = SeaFields.SeabedSlope(g, ocean, [5.0, -10.0, -30.0, -40.0]);
        // 뭍 칸과의 높이 차(15 m)는 세지 않습니다.
        Assert.Equal(new[] { 0.0, 0.2, 0.2, 0.1 }, slope.Select(v => Math.Round(v, 12)));

        double[] mean = SeaFields.MeanOverSeaNeighbors(g, ocean, slope);
        Assert.Equal(0.0, mean[0]);
        Assert.Equal(0.2, mean[1], 12);
        Assert.Equal(0.5 / 3.0, mean[2], 12);
        Assert.Equal(0.15, mean[3], 12);
    }

    /// <summary>
    /// 한 줄 격자로 바닥 종류와 해안 종류를 고릅니다. 칸 0 은 바다와 닿지 않은 뭍, 칸 1 은 해안 뭍, 그 뒤는 바다입니다.
    /// </summary>
    /// <param name="depths">바다 칸의 깊이 (m), 해안에서 먼 쪽으로.</param>
    /// <param name="coastSlope">해안 뭍 칸의 경사 (m/m).</param>
    /// <param name="bare">해안 뭍 칸이 맨 암반인지.</param>
    /// <param name="tempC">바다 물의 온도 (°C).</param>
    /// <param name="plume">바다 칸의 강물 퍼짐.</param>
    /// <param name="rockSlopeDeg">바위가 되는 바다 밑 비탈 (°).</param>
    private static (byte[] Seabed, byte[] Coast) ClassifyRow(
        double[] depths, double coastSlope, bool bare, double tempC, double plume, double rockSlopeDeg = SeaFields.RockSeabedSlopeDeg)
    {
        int n = depths.Length + 2;
        bool[] ocean = Enumerable.Range(0, n).Select(c => c >= 2).ToArray();
        double[] z = new double[n];
        z[0] = 8.0;
        z[1] = 2.0;
        double[] slope = new double[n];
        slope[0] = coastSlope;
        slope[1] = coastSlope;
        bool[] bareRock = new bool[n];
        bareRock[1] = bare;
        double[] temp = new double[n];
        double[] plumes = new double[n];
        temp[0] = temp[1] = double.NaN;
        for (int c = 2; c < n; c++)
        {
            z[c] = -depths[c - 2];
            temp[c] = tempC;
            plumes[c] = plume;
        }
        return SeaFields.Classify(Row(n), ocean, z, slope, bareRock, temp, plumes, rockSlopeDeg);
    }

    /// <summary>완만한 바다 밑: 깊이 5~80 m, 칸 사이 경사가 가장 가팔라도 0.25(약 14°)입니다.</summary>
    private static readonly double[] GentleDepths = [5.0, 10.0, 15.0, 20.0, 35.0, 45.0, 70.0, 80.0];

    private const byte Mud = SeaFields.SeabedMud;
    private const byte Sand = SeaFields.SeabedSand;
    private const byte Gravel = SeaFields.SeabedGravel;
    private const byte Rock = SeaFields.SeabedRock;
    private const byte Carbonate = SeaFields.SeabedCarbonateSand;

    [Fact]
    public void 따뜻하고_맑은_얕은_바다는_석회_모래이고_그_앞_해변은_흰_모래_해변이다()
    {
        (byte[] seabed, byte[] coast) = ClassifyRow(GentleDepths, coastSlope: 0.02, bare: false, tempC: 25.0, plume: 0.0);
        // 깊이 30 m 안은 석회 모래, 30~60 m 는 모래, 60 m 넘으면 펄입니다. 뭍 칸의 바닥 종류는 0 입니다.
        Assert.Equal(new byte[] { 0, 0, Carbonate, Carbonate, Carbonate, Carbonate, Sand, Sand, Mud, Mud }, seabed);
        // 바다와 닿은 뭍 칸(1)만 해안 종류가 있고, 바다와 닿지 않은 뭍 칸(0)과 바다 칸은 0 입니다.
        Assert.Equal(SeaFields.CoastWhiteSandBeach, coast[1]);
        Assert.Equal(SeaFields.CoastNone, coast[0]);
        Assert.All(coast.Skip(2), k => Assert.Equal(SeaFields.CoastNone, k));

        // 찬 바다(8 °C)는 석회 모래가 없어 모래 바닥과 모래 해변입니다.
        (seabed, coast) = ClassifyRow(GentleDepths, 0.02, false, tempC: 8.0, plume: 0.0);
        Assert.Equal(new byte[] { 0, 0, Sand, Sand, Sand, Sand, Sand, Sand, Mud, Mud }, seabed);
        Assert.Equal(SeaFields.CoastSandBeach, coast[1]);

        // 정확히 20 °C 는 '20 °C 를 넘음'이 아니라서 모래입니다.
        (seabed, _) = ClassifyRow(GentleDepths, 0.02, false, tempC: SeaFields.CarbonateMinTempC, plume: 0.0);
        Assert.Equal(Sand, seabed[2]);

        // 따뜻해도 강물이 조금 퍼진 곳(0.25)은 산호가 자라지 못해 모래이고, 0.3 을 넘지 않아 펄도 아닙니다.
        (seabed, coast) = ClassifyRow(GentleDepths, 0.02, false, tempC: 25.0, plume: 0.25);
        Assert.Equal(new byte[] { 0, 0, Sand, Sand, Sand, Sand, Sand, Sand, Mud, Mud }, seabed);
        Assert.Equal(SeaFields.CoastSandBeach, coast[1]);
    }

    [Fact]
    public void 강물이_퍼진_바다는_펄이고_완만한_해안은_갯벌이다()
    {
        (byte[] seabed, byte[] coast) = ClassifyRow(GentleDepths, coastSlope: 0.02, bare: false, tempC: 25.0, plume: 0.5);
        Assert.All(seabed.Skip(2), b => Assert.Equal(Mud, b));
        Assert.Equal(SeaFields.CoastMudflat, coast[1]);

        // 비탈이 3°(경사 약 0.052)보다 가파른 해안은 갯벌이 아니라 모래 해변입니다.
        (_, coast) = ClassifyRow(GentleDepths, coastSlope: 0.1, bare: false, tempC: 25.0, plume: 0.5);
        Assert.Equal(SeaFields.CoastSandBeach, coast[1]);
    }

    [Fact]
    public void 바위_해안과_자갈_해변은_뭍의_비탈로_정하고_붙은_바다_칸의_바닥이_따라간다()
    {
        // 비탈 30°(경사 약 0.577)를 넘으면 바위 해안이고, 붙은 바다 칸은 바위입니다. 그다음 칸부터는 물 온도를 따릅니다.
        (byte[] seabed, byte[] coast) = ClassifyRow(GentleDepths, coastSlope: 0.7, bare: false, tempC: 25.0, plume: 0.0);
        Assert.Equal(SeaFields.CoastRocky, coast[1]);
        Assert.Equal(new byte[] { 0, 0, Rock, Carbonate, Carbonate, Carbonate, Sand, Sand, Mud, Mud }, seabed);

        // 완만해도 맨 암반이면 바위 해안입니다.
        (seabed, coast) = ClassifyRow(GentleDepths, coastSlope: 0.02, bare: true, tempC: 8.0, plume: 0.0);
        Assert.Equal(SeaFields.CoastRocky, coast[1]);
        Assert.Equal(Rock, seabed[2]);
        Assert.Equal(Sand, seabed[3]);

        // 바위 해안은 강물이 퍼져도 갯벌이 되지 않습니다. 붙은 바다 칸은 펄이 먼저입니다.
        (seabed, coast) = ClassifyRow(GentleDepths, coastSlope: 0.02, bare: true, tempC: 8.0, plume: 0.5);
        Assert.Equal(SeaFields.CoastRocky, coast[1]);
        Assert.Equal(Mud, seabed[2]);

        // 비탈 10°(경사 약 0.176)와 30° 사이는 자갈 해변이고, 붙은 바다 칸은 자갈입니다.
        (seabed, coast) = ClassifyRow(GentleDepths, coastSlope: 0.3, bare: false, tempC: 25.0, plume: 0.0);
        Assert.Equal(SeaFields.CoastGravelBeach, coast[1]);
        Assert.Equal(new byte[] { 0, 0, Gravel, Carbonate, Carbonate, Carbonate, Sand, Sand, Mud, Mud }, seabed);
    }

    [Fact]
    public void 가파른_바다_밑은_깊어도_바위이고_바위가_되는_비탈은_설정으로_바꾼다()
    {
        // 칸마다 150 m 씩 깊어지는 바다 밑(경사 1.5, 약 56°)은 60 m 보다 깊어도 펄이 아니라 바위입니다.
        (byte[] seabed, _) = ClassifyRow([5.0, 150.0, 300.0, 450.0, 600.0], coastSlope: 0.02, bare: false, tempC: 25.0, plume: 0.0);
        Assert.All(seabed.Skip(2), b => Assert.Equal(Rock, b));

        // 완만한 바다 밑도 문턱을 10°(경사 약 0.176)로 낮추면 경사 0.15~0.25 가 이어지는 곳(칸 6~8, 평균 0.18~0.22)이 바위가 됩니다.
        (seabed, _) = ClassifyRow(GentleDepths, 0.02, false, 25.0, 0.0, rockSlopeDeg: 10.0);
        Assert.All(new[] { 6, 7, 8 }, c => Assert.Equal(Rock, seabed[c]));
        Assert.All(new[] { 2, 3, 4 }, c => Assert.Equal(Carbonate, seabed[c]));

        ArgumentException e = Assert.Throws<ArgumentException>(() => ClassifyRow(GentleDepths, 0.02, false, 25.0, 0.0, rockSlopeDeg: 90.0));
        Assert.Contains("sea.rock_slope_deg(90°)", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ClassifyRow(GentleDepths, 0.02, false, 25.0, 0.0, rockSlopeDeg: 0.0));
    }

    [Fact]
    public void 번호_표는_종류마다_이름과_키와_색이_하나씩_있다()
    {
        Assert.Equal(SeaFields.SeabedCount, SeaFields.SeabedNames.Length);
        Assert.Equal(SeaFields.SeabedCount, SeaFields.SeabedKeys.Length);
        Assert.Equal(SeaFields.SeabedCount * 3, SeaFields.SeabedColorRgb.Length);
        Assert.Equal(SeaFields.CoastCount, SeaFields.CoastNames.Length);
        Assert.Equal(SeaFields.CoastCount, SeaFields.CoastKeys.Length);
        Assert.Equal(SeaFields.CoastCount * 3, SeaFields.CoastColorRgb.Length);
    }

    [Fact]
    public void 바다_설정_키가_없으면_지구_크기_기본값을_쓰고_작은_행성은_행성_크기에_맞춘_값을_적어_둔다()
    {
        Config earth = Config.LoadConfig("earth", "tiny");
        Assert.Equal(30_000.0, earth.FOr("sea.plume_half_distance_m", SeaFields.PlumeHalfDistanceM));
        Assert.Equal(160_000.0, earth.FOr("sea.fetch_cap_m", SeaFields.FetchCapM));
        Assert.Equal(10_000.0, earth.FOr("sea.plume_full_discharge_m3_per_s", SeaFields.PlumeFullDischargeM3PerS));
        Assert.Equal(25.0, earth.FOr("sea.rock_slope_deg", SeaFields.RockSeabedSlopeDeg));

        Config small = Config.LoadConfig("small10", "tiny");
        double circumference = 2.0 * Math.PI * small.F("planet.radius_m");
        foreach (string key in new[] { "sea.plume_half_distance_m", "sea.fetch_cap_m" })
        {
            double v = small.F(key);
            Assert.True(v > 0.0 && v < circumference / 20.0, $"{key} = {v} m 는 0 보다 크고 행성 둘레의 20분의 1({circumference / 20.0:F0} m)보다 작아야 합니다.");
        }
        // 이 행성에서 강으로 치는 가장 작은 유량보다는 크고, 그 100배보다는 작아야 강어귀마다 값이 고르게 갈립니다.
        double riverMin = small.F("rivers.min_discharge_m3_per_s");
        Assert.InRange(small.F("sea.plume_full_discharge_m3_per_s"), riverMin, 100.0 * riverMin);
        Assert.InRange(small.F("sea.rock_slope_deg"), 1.0, 89.0);
    }

    [Fact]
    public void 작은_행성의_바다_필드는_범위를_지키고_같은_시드면_실_수가_달라도_같다()
    {
        Config cfg = Small10();
        PlanetState planet = Planet.Value;
        FieldSet f = planet.Fields;
        CellGraph g = planet.Graph;
        int n = g.NCells;
        bool[] ocean = f.Get<bool>("is_ocean");
        double[] air = f.GetFloat64("temperature_c");
        double[] seaTemp = f.GetFloat64("sea_temp_c");
        bool[] ice = f.Get<bool>("sea_ice");
        double[] plume = f.GetFloat64("river_plume");
        double[] exposure = f.GetFloat64("wave_exposure");
        byte[] seabed = f.Get<byte>("seabed");
        byte[] coast = f.Get<byte>("coast_kind");
        double warmest = cfg.F("climate.t_equator_c");

        for (int c = 0; c < n; c++)
        {
            bool touchesSea = false;
            for (int s = 0; s < CellGraph.NSlots; s++)
            {
                int v = g.Nbr[(c * CellGraph.NSlots) + s];
                touchesSea |= v >= 0 && ocean[v];
            }
            if (ocean[c])
            {
                Assert.InRange(seaTemp[c], SeaFields.FreezeC, warmest);
                Assert.Equal(Math.Max(air[c], SeaFields.FreezeC), seaTemp[c]);
                Assert.InRange(seabed[c], SeaFields.SeabedMud, SeaFields.SeabedCarbonateSand);
                Assert.Equal(SeaFields.CoastNone, coast[c]);
                Assert.InRange(plume[c], 0.0, 1.0);
                Assert.InRange(exposure[c], 0.0, 1.0);
            }
            else
            {
                Assert.True(double.IsNaN(seaTemp[c]), $"뭍 칸 {c} 의 sea_temp_c 가 NaN 이 아니라 {seaTemp[c]} 입니다.");
                Assert.Equal(SeaFields.SeabedLand, seabed[c]);
                Assert.Equal(0.0, plume[c]);
                Assert.Equal(0.0, exposure[c]);
                // 해안 종류는 바다와 닿은 뭍 칸에만 있습니다.
                Assert.True((coast[c] != SeaFields.CoastNone) == touchesSea, $"뭍 칸 {c}: 바다와 닿음 {touchesSea}, coast_kind {coast[c]}");
                Assert.InRange(coast[c], SeaFields.CoastNone, SeaFields.CoastWhiteSandBeach);
            }
            Assert.Equal((float)seaTemp[c] < (float)SeaFields.IceMaxC, ice[c]);
        }

        // 이 행성에는 얼음 바다, 강어귀, 트인 바다와 가려진 바다, 여러 바닥과 해안이 모두 있습니다.
        Assert.Contains(true, ice);
        Assert.Contains(plume, v => v > SeaFields.MudPlume);
        Assert.Contains(exposure, v => v == 1.0);
        Assert.Contains(Enumerable.Range(0, n), c => ocean[c] && exposure[c] < 0.4);
        foreach (byte kind in new[] { Mud, Sand, Gravel, Rock, Carbonate })
        {
            Assert.Contains(kind, seabed);
        }
        foreach (byte kind in new[]
        {
            SeaFields.CoastSandBeach, SeaFields.CoastGravelBeach, SeaFields.CoastRocky, SeaFields.CoastMudflat, SeaFields.CoastWhiteSandBeach,
        })
        {
            Assert.Contains(kind, coast);
        }

        // 실 하나로 다시 만들어도 모든 필드의 바이트가 같습니다(나눠 돌려도 결과가 같음).
        PlanetState single = Pipeline.GeneratePlanet(Small10(("profile.compute.numba_threads", 1L)), null);
        foreach (string name in f.Names)
        {
            Assert.True(SameBytes(f[name], single.Fields[name]), $"필드 '{name}' 이 실 수에 따라 달라졌습니다.");
        }
    }

    [Fact]
    public void 바다_설정을_바꿔도_바다_필드가_아닌_필드는_그대로다()
    {
        PlanetState a = Planet.Value;
        PlanetState b = Pipeline.GeneratePlanet(
            Small10(
                ("sea.plume_half_distance_m", 400.0), ("sea.plume_full_discharge_m3_per_s", 0.01), ("sea.fetch_cap_m", 500.0),
                ("sea.rock_slope_deg", 15.0)),
            null);
        foreach (string name in a.Fields.Names)
        {
            bool same = SameBytes(a.Fields[name], b.Fields[name]);
            if (name is "river_plume" or "wave_exposure" or "seabed" or "coast_kind")
            {
                Assert.False(same, $"설정을 바꿨는데 '{name}' 이 그대로입니다.");
            }
            else
            {
                Assert.True(same, $"바다 설정만 바꿨는데 '{name}' 이 달라졌습니다.");
            }
        }
        Assert.All(SeaNames, name => Assert.Contains(name, a.Fields.Names));
        // 퍼지는 거리를 늘리고 기준 유량을 줄였으므로 강물이 퍼진 바다 칸이 늘어납니다.
        int before = a.Fields.GetFloat64("river_plume").Count(v => v > 0.0);
        int after = b.Fields.GetFloat64("river_plume").Count(v => v > 0.0);
        Assert.True(after > before, $"강물이 퍼진 칸이 {before}개에서 {after}개로 늘지 않았습니다.");
    }
}
