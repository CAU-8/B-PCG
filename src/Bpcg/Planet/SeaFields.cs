using System;
using System.Collections.Generic;
using Bpcg.Core;

namespace Bpcg.Planet;

/// <summary>
/// 바다 필드 여섯 개: 바다 물의 온도, 바다 얼음, 강물 퍼짐, 파도 받이, 바다 밑 바닥 종류, 해안 종류.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 필요한가.</b> 바다 생물은 아무 바다에나 살지 않습니다. 산호는 따뜻하고 맑은 얕은 바다의 바위에, 다시마는 찬 바다의 바위에,
/// 잘피(바다 밑 모래에 뿌리를 내리는 꽃식물)는 잔잔한 모래 바닥에 삽니다. 그런데 바다 칸에는 지표 암석 번호와 기온밖에 없었습니다.
/// 그래서 생물을 놓는 쪽(게임)이 읽을 값 여섯 개를 칸마다 셈합니다. 생물을 직접 놓지는 않습니다.
/// </para>
/// <para>
/// <b>바다 물의 온도 (<c>sea_temp_c</c>).</b> 바다 칸은 해수면 높이의 연평균 기온을 쓰되, 바닷물이 어는 온도(<see cref="FreezeC"/>, −1.8 °C)
/// 아래로는 내려가지 않게 자릅니다. 물은 얼면서 더 차가워지지 않기 때문입니다. 뭍 칸은 NaN 입니다.
/// 예: 기온이 12 °C 인 바다 칸은 12 °C, −20 °C 인 바다 칸은 −1.8 °C 입니다.
/// </para>
/// <para>
/// <b>바다 얼음 (<c>sea_ice</c>).</b> 바다 물의 온도가 <see cref="IceMaxC"/>(−1.5 °C) 아래인 바다 칸입니다.
/// 묶음에 적는 정밀도(float32)로 반올림한 온도로 견주므로, 묶음에서 읽은 <c>sea_temp_c</c> 로 다시 셈해도 같은 칸이 나옵니다.
/// </para>
/// <para>
/// <b>강물 퍼짐 (<c>river_plume</c>).</b> 강어귀 앞바다는 강이 실어 온 흙으로 흐립니다. 큰 강일수록 멀리까지 흐립니다.
/// 강어귀(강 칸이 물을 보내는 바다 칸)마다 세기 s = 강의 유량 ÷ <c>sea.plume_full_discharge_m3_per_s</c> 를 정하고,
/// 그 바다 칸에서 물길로 d [m] 떨어진 바다 칸에 s × 2^(−d ÷ L) 을 더합니다. L 은 <c>sea.plume_half_distance_m</c> 입니다.
/// 여러 강어귀의 값을 더한 뒤 1 에서 자릅니다. 예: L = 200 m, s = 0.8 이면 강어귀 칸은 0.8, 200 m 밖은 0.4, 400 m 밖은 0.2 입니다.
/// 거리는 직선이 아니라 바다 칸만 밟아 가는 가장 짧은 길로 잽니다. 그래야 강물이 곶 너머 반대쪽 바다로 건너뛰지 않습니다.
/// 값이 <see cref="PlumeCutoff"/>(0.01)보다 작아지는 곳에서 퍼뜨리기를 멈춥니다. 뭍 칸은 0 입니다.
/// </para>
/// <para>
/// <b>파도 받이 (<c>wave_exposure</c>).</b> 탁 트인 바다를 마주한 곶은 파도가 세고, 뭍으로 둘러싸인 만 안은 잔잔합니다.
/// 바다 칸마다 열여섯 방향(22.5° 간격)으로 곧게 나아가며 뭍을 만날 때까지의 거리를 잽니다. 이 거리를 <c>sea.fetch_cap_m</c> 에서 자르고,
/// 열여섯 값의 평균을 <c>sea.fetch_cap_m</c> 으로 나눕니다. 예: 재는 거리가 1 km 일 때 여덟 방향은 1 km 넘게 트이고 여덟 방향은 바로 뭍이면 0.5 입니다.
/// 둘레 1 km 에 뭍이 없으면 1, 곧은 해안 바로 앞은 약 0.5, 좁은 만 안은 0.5 보다 작습니다. 뭍 칸은 0 입니다.
/// </para>
/// <para>
/// <b>바다 밑 바닥 종류 (<c>seabed</c>).</b> 위에서부터 먼저 맞는 규칙을 씁니다.
/// 바다 밑 비탈이 <c>sea.rock_slope_deg</c>(25°)를 넘으면 흙이 쌓이지 못해 바위,
/// 강물 퍼짐이 <see cref="MudPlume"/>(0.3)를 넘으면 강이 실어 온 펄,
/// 깊이가 <see cref="MudDepthM"/>(60 m)를 넘으면 파도가 닿지 않아 가는 흙이 가라앉은 펄,
/// 바위 해안에 붙은 칸은 바위, 자갈 해변에 붙은 칸은 자갈,
/// 물 온도가 <see cref="CarbonateMinTempC"/>(20 °C)를 넘고 깊이가 <see cref="CarbonateMaxDepthM"/>(30 m) 안이며 강물 퍼짐이
/// <see cref="CarbonateMaxPlume"/>(0.2) 아래면 부서진 산호와 조개껍데기가 쌓인 석회 모래, 나머지는 모래입니다. 뭍 칸은 0 입니다.
/// 바다 밑 비탈은 이웃 바다 칸까지의 높이 차 ÷ 거리 가운데 가장 큰 값을, 그 칸과 둘레 바다 칸에서 평균한 값입니다.
/// 칸 하나의 값만 보면 바위와 모래가 칸마다 번갈아 나오기 때문입니다(맨 암반 <c>bare_rock</c> 도 같은 까닭으로 둘레 칸과 평균합니다).
/// </para>
/// <para>
/// <b>해안 종류 (<c>coast_kind</c>).</b> 바다 칸과 닿은(둘레 여덟 칸 가운데 바다가 있는) 뭍 칸에만 적습니다. 위에서부터 먼저 맞는 규칙을 씁니다.
/// 비탈이 <see cref="RockCoastSlopeDeg"/>(30°)를 넘거나 맨 암반이면 바위 해안,
/// 앞바다의 강물 퍼짐이 0.3 을 넘고 비탈이 <see cref="MudflatMaxSlopeDeg"/>(3°) 아래면 갯벌,
/// 비탈이 <see cref="GravelCoastSlopeDeg"/>(10°)를 넘으면 자갈 해변(가파른 해변일수록 알갱이가 굵습니다),
/// 붙은 바다 칸 가운데 석회 모래 바닥이 있으면 흰 모래 해변, 나머지는 모래 해변입니다. 그 밖의 뭍 칸과 바다 칸은 0 입니다.
/// </para>
/// <para>
/// 길이와 유량(<c>sea.plume_half_distance_m</c>, <c>sea.fetch_cap_m</c>, <c>sea.plume_full_discharge_m3_per_s</c>)은 행성 크기를 따라야 하므로 설정에서 읽고,
/// 설정에 없으면 지구 크기 기본값을 씁니다. 바위가 되는 바다 밑 비탈(<c>sea.rock_slope_deg</c>)도 설정에서 읽습니다. 높이만 덜 줄인 작은 행성은
/// 바다 밑이 지구보다 훨씬 가팔라서, 행성마다 맞는 값이 다르기 때문입니다. 그 밖의 각도와 깊이, 온도의 경계는 코드 상수입니다.
/// 모두 근거 논문 없이 정한 값이고, 바람과 해류, 계절은 아직 없습니다.
/// </para>
/// </remarks>
public static class SeaFields
{
    /// <summary>바닥 종류 0: 뭍 (바다가 아님).</summary>
    public const byte SeabedLand = 0;

    /// <summary>바닥 종류 1: 펄. 강어귀 앞과 깊은 바다.</summary>
    public const byte SeabedMud = 1;

    /// <summary>바닥 종류 2: 모래.</summary>
    public const byte SeabedSand = 2;

    /// <summary>바닥 종류 3: 자갈. 자갈 해변 앞.</summary>
    public const byte SeabedGravel = 3;

    /// <summary>바닥 종류 4: 바위. 가파른 바다 밑과 바위 해안 앞.</summary>
    public const byte SeabedRock = 4;

    /// <summary>바닥 종류 5: 석회 모래. 따뜻하고 맑은 얕은 바다에 쌓인 부서진 산호와 조개껍데기.</summary>
    public const byte SeabedCarbonateSand = 5;

    /// <summary>바닥 종류 번호 수 (뭍 포함).</summary>
    public const int SeabedCount = 6;

    /// <summary>해안 종류 0: 해안이 아님 (바다 칸, 바다와 닿지 않은 뭍 칸).</summary>
    public const byte CoastNone = 0;

    /// <summary>해안 종류 1: 모래 해변.</summary>
    public const byte CoastSandBeach = 1;

    /// <summary>해안 종류 2: 자갈 해변.</summary>
    public const byte CoastGravelBeach = 2;

    /// <summary>해안 종류 3: 바위 해안.</summary>
    public const byte CoastRocky = 3;

    /// <summary>해안 종류 4: 갯벌.</summary>
    public const byte CoastMudflat = 4;

    /// <summary>해안 종류 5: 흰 모래 해변. 앞바다가 석회 모래.</summary>
    public const byte CoastWhiteSandBeach = 5;

    /// <summary>해안 종류 번호 수 (해안 아님 포함).</summary>
    public const int CoastCount = 6;

    /// <summary>바닥 종류 번호 순서의 한국어 이름.</summary>
    public static readonly string[] SeabedNames = ["뭍", "펄", "모래", "자갈", "바위", "석회 모래"];

    /// <summary>바닥 종류 번호 순서의 영어 키 (스튜디오·게임이 씀).</summary>
    public static readonly string[] SeabedKeys = ["land", "mud", "sand", "gravel", "rock", "carbonate_sand"];

    /// <summary>바닥 종류 번호 순서의 색 (r, g, b, 0~255). 스튜디오와 게임이 같은 색을 씁니다.</summary>
    public static readonly byte[] SeabedColorRgb =
    [
        230, 233, 230,
        112, 98, 84,
        222, 200, 150,
        150, 150, 150,
        70, 74, 82,
        250, 244, 225,
    ];

    /// <summary>해안 종류 번호 순서의 한국어 이름.</summary>
    public static readonly string[] CoastNames = ["해안 아님", "모래 해변", "자갈 해변", "바위 해안", "갯벌", "흰 모래 해변"];

    /// <summary>해안 종류 번호 순서의 영어 키 (스튜디오·게임이 씀).</summary>
    public static readonly string[] CoastKeys = ["none", "sand_beach", "gravel_beach", "rocky_coast", "mudflat", "white_sand_beach"];

    /// <summary>해안 종류 번호 순서의 색 (r, g, b, 0~255).</summary>
    public static readonly byte[] CoastColorRgb =
    [
        230, 233, 230,
        226, 201, 138,
        150, 150, 150,
        84, 78, 74,
        122, 104, 84,
        250, 244, 225,
    ];

    /// <summary>바닷물이 어는 온도 (°C). 바다 물의 온도는 이 아래로 내려가지 않습니다.</summary>
    public const double FreezeC = -1.8;

    /// <summary>바다 물의 온도가 이보다 낮으면 바다 얼음 (°C).</summary>
    public const double IceMaxC = -1.5;

    /// <summary>강물 퍼짐이 절반이 되는 거리 (m). 설정에 <c>sea.plume_half_distance_m</c> 이 없을 때 씁니다 (지구 크기).</summary>
    public const double PlumeHalfDistanceM = 30_000.0;

    /// <summary>강어귀 칸의 강물 퍼짐이 1 이 되는 유량 (m³/s). 설정에 <c>sea.plume_full_discharge_m3_per_s</c> 가 없을 때 씁니다 (지구 크기).</summary>
    public const double PlumeFullDischargeM3PerS = 10_000.0;

    /// <summary>강어귀 하나가 보태는 값이 이보다 작아지는 곳에서 퍼뜨리기를 멈춥니다.</summary>
    public const double PlumeCutoff = 0.01;

    /// <summary>파도 받이를 잴 때 방향마다 나아가는 가장 먼 거리 (m). 설정에 <c>sea.fetch_cap_m</c> 이 없을 때 씁니다 (지구 크기).</summary>
    public const double FetchCapM = 160_000.0;

    /// <summary>파도 받이를 재는 방향 수.</summary>
    public const int FetchDirections = 16;

    /// <summary>바다 밑 비탈이 이보다 가파르면 바위 (°). 설정에 <c>sea.rock_slope_deg</c> 가 없을 때 씁니다.</summary>
    public const double RockSeabedSlopeDeg = 25.0;

    /// <summary>강물 퍼짐이 이보다 크면 펄 바닥이고, 완만한 해안은 갯벌.</summary>
    public const double MudPlume = 0.3;

    /// <summary>이보다 깊으면 펄 (m).</summary>
    public const double MudDepthM = 60.0;

    /// <summary>바다 물의 온도가 이보다 높아야 석회 모래 (°C).</summary>
    public const double CarbonateMinTempC = 20.0;

    /// <summary>깊이가 이 안이어야 석회 모래 (m).</summary>
    public const double CarbonateMaxDepthM = 30.0;

    /// <summary>강물 퍼짐이 이보다 작아야 석회 모래. 산호는 흐린 물에서 자라지 못합니다.</summary>
    public const double CarbonateMaxPlume = 0.2;

    /// <summary>해안 뭍 칸의 비탈이 이보다 가파르면 바위 해안 (°).</summary>
    public const double RockCoastSlopeDeg = 30.0;

    /// <summary>해안 뭍 칸의 비탈이 이보다 가파르면 자갈 해변 (°).</summary>
    public const double GravelCoastSlopeDeg = 10.0;

    /// <summary>해안 뭍 칸의 비탈이 이보다 완만해야 갯벌 (°).</summary>
    public const double MudflatMaxSlopeDeg = 3.0;

    /// <summary>
    /// 행성 단계의 필드로 바다 필드 여섯 개를 셈합니다.
    /// 돌려주는 필드: <c>sea_temp_c</c>, <c>sea_ice</c>, <c>river_plume</c>, <c>wave_exposure</c>, <c>seabed</c>, <c>coast_kind</c>.
    /// </summary>
    /// <param name="graph">행성 격자 (구면).</param>
    /// <param name="fields">1~4단계가 끝난 필드. is_ocean, z_m, temperature_c, receiver, is_river, discharge_m3_per_yr, slope, bare_rock 을 읽습니다.</param>
    /// <param name="cfg">설정. planet.axis 와 sea.* (없으면 지구 크기 기본값)를 읽습니다.</param>
    public static FieldSet Compute(CellGraph graph, FieldSet fields, Config cfg)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(cfg);
        bool[] ocean = fields.Get<bool>("is_ocean");
        double[] seaTemp = SeaTemperature(fields.GetFloat64("temperature_c"), ocean);
        double[] plume = RiverPlume(
            graph, ocean, fields.Get<long>("receiver"), fields.Get<bool>("is_river"), fields.GetFloat64("discharge_m3_per_yr"),
            cfg.FOr("sea.plume_half_distance_m", PlumeHalfDistanceM),
            cfg.FOr("sea.plume_full_discharge_m3_per_s", PlumeFullDischargeM3PerS) * Constants.SecondsPerYear);
        double[] exposure = WaveExposure(graph, ocean, cfg.FOr("sea.fetch_cap_m", FetchCapM), cfg.FArray("planet.axis"));
        (byte[] seabed, byte[] coast) = Classify(
            graph, ocean, fields.GetFloat64("z_m"), fields.GetFloat64("slope"), fields.Get<bool>("bare_rock"), seaTemp, plume,
            cfg.FOr("sea.rock_slope_deg", RockSeabedSlopeDeg));
        return new FieldSet
        {
            ["sea_temp_c"] = seaTemp,
            ["sea_ice"] = SeaIce(seaTemp),
            ["river_plume"] = plume,
            ["wave_exposure"] = exposure,
            ["seabed"] = seabed,
            ["coast_kind"] = coast,
        };
    }

    /// <summary>바다 물의 온도 (°C). 바다 칸은 max(기온, <see cref="FreezeC"/>), 뭍 칸은 NaN 입니다.</summary>
    /// <param name="temperatureC">칸마다 연평균 기온 (°C). 바다 칸은 해수면 높이의 기온입니다.</param>
    /// <param name="ocean">칸마다 바다인지.</param>
    public static double[] SeaTemperature(double[] temperatureC, bool[] ocean)
    {
        ArgumentNullException.ThrowIfNull(temperatureC);
        ArgumentNullException.ThrowIfNull(ocean);
        if (temperatureC.Length != ocean.Length)
        {
            throw new ArgumentException(
                $"바다 물의 온도 입력의 칸 수가 다릅니다: 기온 {temperatureC.Length}, 바다 {ocean.Length}. 같은 격자의 필드를 주세요.");
        }
        double[] t = new double[ocean.Length];
        // 칸마다 제 자리만 쓰므로 나눠 돌려도 결과가 같습니다.
        Parallelism.For(0, t.Length, c => t[c] = ocean[c] ? Math.Max(temperatureC[c], FreezeC) : double.NaN);
        return t;
    }

    /// <summary>
    /// 바다 얼음. 바다 물의 온도를 묶음에 적는 정밀도(float32)로 반올림한 값이 <see cref="IceMaxC"/> 보다 낮은 칸입니다. NaN(뭍)은 거짓입니다.
    /// </summary>
    /// <param name="seaTempC">칸마다 바다 물의 온도 (°C, 뭍은 NaN).</param>
    public static bool[] SeaIce(double[] seaTempC)
    {
        ArgumentNullException.ThrowIfNull(seaTempC);
        bool[] ice = new bool[seaTempC.Length];
        Parallelism.For(0, ice.Length, c => ice[c] = (float)seaTempC[c] < (float)IceMaxC);
        return ice;
    }

    /// <summary>
    /// 강물 퍼짐 (0~1). 강어귀마다 세기 s = 유량 ÷ <paramref name="fullDischargeM3PerYr"/> 를 정하고, 바다 칸만 밟아 가는 가장 짧은 길로
    /// d [m] 떨어진 바다 칸에 s × 2^(−d ÷ <paramref name="halfDistanceM"/>) 을 더한 뒤 1 에서 자릅니다. 뭍 칸은 0 입니다.
    /// </summary>
    /// <remarks>
    /// 강어귀는 강 칸이 물을 보내는 바다 칸입니다. 강 칸 여럿이 같은 바다 칸으로 물을 보내면 유량을 더합니다.
    /// 강어귀는 칸 번호 순서로 하나씩 퍼뜨리고 그 순서로 더하므로, 몇 개의 실로 돌려도 결과가 같습니다.
    /// 보태는 값이 <see cref="PlumeCutoff"/> 보다 작아지는 거리에서 멈추므로, 세기가 그보다 작은 강어귀는 아무 값도 보태지 않습니다.
    /// </remarks>
    /// <param name="graph">격자. 이웃(nbr)과 이웃까지 거리(dist)를 씁니다.</param>
    /// <param name="ocean">칸마다 바다인지.</param>
    /// <param name="rcv">칸마다 물을 보내는 이웃 (바다와 출구는 자기 자신).</param>
    /// <param name="isRiver">칸마다 강인지.</param>
    /// <param name="dischargeM3PerYr">칸마다 유량 (m³/yr).</param>
    /// <param name="halfDistanceM">값이 절반이 되는 거리 (m).</param>
    /// <param name="fullDischargeM3PerYr">강어귀 칸의 값이 1 이 되는 유량 (m³/yr).</param>
    public static double[] RiverPlume(
        CellGraph graph, bool[] ocean, long[] rcv, bool[] isRiver, double[] dischargeM3PerYr,
        double halfDistanceM, double fullDischargeM3PerYr)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(ocean);
        ArgumentNullException.ThrowIfNull(rcv);
        ArgumentNullException.ThrowIfNull(isRiver);
        ArgumentNullException.ThrowIfNull(dischargeM3PerYr);
        if (!(halfDistanceM > 0.0) || !double.IsFinite(halfDistanceM) || !(fullDischargeM3PerYr > 0.0) || !double.IsFinite(fullDischargeM3PerYr))
        {
            throw new ArgumentException(
                $"sea.plume_half_distance_m({halfDistanceM} m)과 sea.plume_full_discharge_m3_per_s({fullDischargeM3PerYr / Constants.SecondsPerYear} m³/s)는 "
                + "0 보다 큰 유한한 값이어야 합니다. 예: 반지름 10 km 행성은 200 m 와 0.05 m³/s.");
        }
        int n = graph.NCells;
        if (ocean.Length != n || rcv.Length != n || isRiver.Length != n || dischargeM3PerYr.Length != n)
        {
            throw new ArgumentException(
                $"강물 퍼짐 입력의 칸 수가 격자({n}칸)와 다릅니다: 바다 {ocean.Length}, 물길 {rcv.Length}, 강 {isRiver.Length}, 유량 {dischargeM3PerYr.Length}.");
        }
        double[] mouth = new double[n];
        for (int c = 0; c < n; c++)
        {
            long r = rcv[c];
            if (!ocean[c] && isRiver[c] && r != c && ocean[r])
            {
                mouth[r] += dischargeM3PerYr[c];
            }
        }

        double[] plume = new double[n];
        double[] best = new double[n];
        Array.Fill(best, double.PositiveInfinity);
        var touched = new List<int>();
        // 힙의 순서는 (거리, 칸 번호)입니다. 거리가 같은 칸의 차례까지 정해 두어 실행마다 같은 순서로 꺼냅니다.
        var heap = new PriorityQueue<int, (double Dist, int Cell)>();
        int[] nbr = graph.Nbr;
        double[] dist = graph.Dist;
        for (int m = 0; m < n; m++)
        {
            double strength = mouth[m] / fullDischargeM3PerYr;
            if (!(strength >= PlumeCutoff))
            {
                continue;
            }
            double reach = halfDistanceM * Math.Log2(strength / PlumeCutoff);
            best[m] = 0.0;
            touched.Add(m);
            heap.Enqueue(m, (0.0, m));
            while (heap.TryDequeue(out int c, out (double Dist, int Cell) key))
            {
                if (key.Dist > best[c])
                {
                    // 그 사이 더 짧은 길이 생긴 칸의 낡은 원소입니다.
                    continue;
                }
                plume[c] += strength * Math.Pow(2.0, -key.Dist / halfDistanceM);
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = nbr[(c * CellGraph.NSlots) + s];
                    if (v < 0 || !ocean[v])
                    {
                        continue;
                    }
                    double d = key.Dist + dist[(c * CellGraph.NSlots) + s];
                    if (d > reach || d >= best[v])
                    {
                        continue;
                    }
                    if (double.IsPositiveInfinity(best[v]))
                    {
                        touched.Add(v);
                    }
                    best[v] = d;
                    heap.Enqueue(v, (d, v));
                }
            }
            foreach (int c in touched)
            {
                best[c] = double.PositiveInfinity;
            }
            touched.Clear();
        }
        for (int c = 0; c < n; c++)
        {
            plume[c] = Math.Min(plume[c], 1.0);
        }
        return plume;
    }

    /// <summary>
    /// 파도 받이 (0~1). 바다 칸마다 열여섯 방향으로 곧게(대원을 따라) 나아가며 뭍 칸을 만날 때까지의 거리를 재고,
    /// <paramref name="fetchCapM"/> 에서 자른 열여섯 값의 평균을 <paramref name="fetchCapM"/> 으로 나눕니다. 뭍 칸은 0 입니다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 방향은 동쪽(자전축 × 칸의 자리)에서 북쪽으로 22.5° 씩 돌립니다. 극점처럼 동쪽을 정할 수 없는 칸은 자전축 대신 다른 좌표축을 씁니다.
    /// 방향마다 대표 칸 크기의 절반보다 길지 않은 걸음으로 나아가며, 걸음마다 그 점이 든 칸(<see cref="Cubesphere.CellOf(double, double, double, int)"/>)이 뭍인지 봅니다.
    /// 처음 뭍에 든 걸음의 바로 앞까지를 트인 거리로 셉니다.
    /// </para>
    /// <para>
    /// 가장 가까운 뭍 칸이 '재는 거리 + 대표 칸 크기의 3배'보다 멀면 어느 방향으로도 뭍에 닿지 않으므로, 나아가 보지 않고 1 을 줍니다.
    /// 칸마다 제 자리만 쓰므로 나눠 돌려도 결과가 같습니다.
    /// </para>
    /// </remarks>
    /// <param name="graph">행성 격자 (구면).</param>
    /// <param name="ocean">칸마다 바다인지.</param>
    /// <param name="fetchCapM">방향마다 나아가는 가장 먼 거리 (m). 0 보다 크고 행성 둘레의 4분의 1 이하.</param>
    /// <param name="axis">자전축 (길이가 0 이 아닌 3차원 벡터).</param>
    public static double[] WaveExposure(CellGraph graph, bool[] ocean, double fetchCapM, double[] axis)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(ocean);
        ArgumentNullException.ThrowIfNull(axis);
        if (graph.Kind != "sphere" || graph.R is not double radius)
        {
            throw new ArgumentException($"파도 받이는 구면 격자에서만 셈합니다: 받은 격자는 '{graph.Kind}' 입니다.");
        }
        int n = graph.NCells;
        if (ocean.Length != n)
        {
            throw new ArgumentException($"파도 받이 입력의 칸 수가 격자({n}칸)와 다릅니다: 바다 {ocean.Length}.");
        }
        double quarter = 0.5 * Math.PI * radius;
        if (!(fetchCapM > 0.0) || !(fetchCapM <= quarter))
        {
            throw new ArgumentException(
                $"sea.fetch_cap_m({fetchCapM} m)은 0 보다 크고 행성 둘레의 4분의 1({quarter:F0} m) 이하여야 합니다. "
                + "반지름 10 km 행성은 1,000 m 를 씁니다.");
        }
        double axisNorm = axis.Length == 3 ? Math.Sqrt((axis[0] * axis[0]) + (axis[1] * axis[1]) + (axis[2] * axis[2])) : 0.0;
        if (!(axisNorm > 0.0))
        {
            throw new ArgumentException("planet.axis 는 길이가 0 이 아닌 3차원 벡터여야 합니다");
        }
        double a0 = axis[0] / axisNorm;
        double a1 = axis[1] / axisNorm;
        double a2 = axis[2] / axisNorm;
        // 동쪽을 정할 수 없는 칸(극점)에서 자전축 대신 쓰는 축: 자전축과 가장 덜 나란한 좌표축.
        double b0 = 0.0;
        double b1 = 0.0;
        double b2 = 0.0;
        if (Math.Abs(a0) <= Math.Abs(a1) && Math.Abs(a0) <= Math.Abs(a2))
        {
            b0 = 1.0;
        }
        else if (Math.Abs(a1) <= Math.Abs(a2))
        {
            b1 = 1.0;
        }
        else
        {
            b2 = 1.0;
        }

        bool[] land = new bool[n];
        for (int c = 0; c < n; c++)
        {
            land[c] = !ocean[c];
        }
        (double[] distLand, _) = Distance.NearestSource(graph, land, fetchCapM + (3.0 * graph.Spacing));

        int nFace = (int)graph.Shape[1];
        int steps = Math.Max(1, (int)Math.Ceiling(fetchCapM / (0.5 * graph.Spacing)));
        double[] cosStep = new double[steps];
        double[] sinStep = new double[steps];
        for (int j = 0; j < steps; j++)
        {
            double angle = fetchCapM * (j + 1) / steps / radius;
            cosStep[j] = Math.Cos(angle);
            sinStep[j] = Math.Sin(angle);
        }
        double[] cosDir = new double[FetchDirections];
        double[] sinDir = new double[FetchDirections];
        for (int k = 0; k < FetchDirections; k++)
        {
            double angle = 2.0 * Math.PI * k / FetchDirections;
            cosDir[k] = Math.Cos(angle);
            sinDir[k] = Math.Sin(angle);
        }

        double[] pos = graph.Pos;
        double[] exposure = new double[n];
        Parallelism.For(0, n, c =>
        {
            if (!ocean[c])
            {
                return;
            }
            if (!double.IsFinite(distLand[c]))
            {
                exposure[c] = 1.0;
                return;
            }
            double px = pos[c * 3];
            double py = pos[(c * 3) + 1];
            double pz = pos[(c * 3) + 2];
            double pn = Math.Sqrt((px * px) + (py * py) + (pz * pz));
            px /= pn;
            py /= pn;
            pz /= pn;
            // 동쪽 = 자전축 × 칸의 자리, 북쪽 = 칸의 자리 × 동쪽.
            double ex = (a1 * pz) - (a2 * py);
            double ey = (a2 * px) - (a0 * pz);
            double ez = (a0 * py) - (a1 * px);
            double en = Math.Sqrt((ex * ex) + (ey * ey) + (ez * ez));
            if (en < 1e-9)
            {
                ex = (b1 * pz) - (b2 * py);
                ey = (b2 * px) - (b0 * pz);
                ez = (b0 * py) - (b1 * px);
                en = Math.Sqrt((ex * ex) + (ey * ey) + (ez * ez));
            }
            ex /= en;
            ey /= en;
            ez /= en;
            double nx = (py * ez) - (pz * ey);
            double ny = (pz * ex) - (px * ez);
            double nz = (px * ey) - (py * ex);

            int open = 0;
            for (int k = 0; k < FetchDirections; k++)
            {
                double dx = (cosDir[k] * ex) + (sinDir[k] * nx);
                double dy = (cosDir[k] * ey) + (sinDir[k] * ny);
                double dz = (cosDir[k] * ez) + (sinDir[k] * nz);
                int reached = steps;
                for (int j = 0; j < steps; j++)
                {
                    double qx = (cosStep[j] * px) + (sinStep[j] * dx);
                    double qy = (cosStep[j] * py) + (sinStep[j] * dy);
                    double qz = (cosStep[j] * pz) + (sinStep[j] * dz);
                    if (!ocean[Cubesphere.CellOf(qx, qy, qz, nFace)])
                    {
                        reached = j;
                        break;
                    }
                }
                open += reached;
            }
            exposure[c] = open / (double)(FetchDirections * steps);
        });
        return exposure;
    }

    /// <summary>
    /// 바다 밑 비탈 (m/m). 바다 칸에서 이웃 바다 칸까지의 높이 차 ÷ 거리 가운데 가장 큰 값입니다. 뭍 칸과 이웃 바다 칸이 없는 칸은 0 입니다.
    /// </summary>
    /// <param name="graph">격자.</param>
    /// <param name="ocean">칸마다 바다인지.</param>
    /// <param name="z">칸마다 높이 (m, 해수면 0). 바다 칸은 바다 밑의 높이입니다.</param>
    public static double[] SeabedSlope(CellGraph graph, bool[] ocean, double[] z)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(ocean);
        ArgumentNullException.ThrowIfNull(z);
        int n = graph.NCells;
        if (ocean.Length != n || z.Length != n)
        {
            throw new ArgumentException($"바다 밑 비탈 입력의 칸 수가 격자({n}칸)와 다릅니다: 바다 {ocean.Length}, 높이 {z.Length}.");
        }
        int[] nbr = graph.Nbr;
        double[] dist = graph.Dist;
        double[] slope = new double[n];
        // 칸마다 제 자리만 쓰므로 나눠 돌려도 결과가 같습니다.
        Parallelism.For(0, n, c =>
        {
            if (!ocean[c])
            {
                return;
            }
            double steepest = 0.0;
            for (int s = 0; s < CellGraph.NSlots; s++)
            {
                int v = nbr[(c * CellGraph.NSlots) + s];
                if (v >= 0 && ocean[v])
                {
                    steepest = Math.Max(steepest, Math.Abs(z[c] - z[v]) / dist[(c * CellGraph.NSlots) + s]);
                }
            }
            slope[c] = steepest;
        });
        return slope;
    }

    /// <summary>
    /// 바다 칸의 값을 그 칸과 둘레 바다 칸(이웃 여덟 칸 가운데 바다인 칸)에서 평균합니다. 뭍 칸은 0 입니다.
    /// </summary>
    /// <param name="graph">격자.</param>
    /// <param name="ocean">칸마다 바다인지.</param>
    /// <param name="values">칸마다의 값. 바다 칸의 값만 씁니다.</param>
    public static double[] MeanOverSeaNeighbors(CellGraph graph, bool[] ocean, double[] values)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(ocean);
        ArgumentNullException.ThrowIfNull(values);
        int n = graph.NCells;
        if (ocean.Length != n || values.Length != n)
        {
            throw new ArgumentException($"평균할 값의 칸 수가 격자({n}칸)와 다릅니다: 바다 {ocean.Length}, 값 {values.Length}.");
        }
        int[] nbr = graph.Nbr;
        double[] mean = new double[n];
        // 칸마다 제 자리만 쓰고 이웃은 자리 순서대로 더하므로, 나눠 돌려도 결과가 같습니다.
        Parallelism.For(0, n, c =>
        {
            if (!ocean[c])
            {
                return;
            }
            double sum = values[c];
            int count = 1;
            for (int s = 0; s < CellGraph.NSlots; s++)
            {
                int v = nbr[(c * CellGraph.NSlots) + s];
                if (v >= 0 && ocean[v])
                {
                    sum += values[v];
                    count++;
                }
            }
            mean[c] = sum / count;
        });
        return mean;
    }

    /// <summary>
    /// 바다 밑 바닥 종류와 해안 종류를 고릅니다. 규칙은 이 형의 설명에 있습니다.
    /// </summary>
    /// <remarks>
    /// 세 걸음으로 셈합니다. 먼저 뭍의 값만으로 바위 해안과 자갈 해변을 정합니다. 다음에 바다 밑을 정합니다(바위 해안과 자갈 해변에 붙은 칸을 봅니다).
    /// 마지막으로 남은 해안 칸을 갯벌, 흰 모래 해변, 모래 해변으로 나눕니다(붙은 바다 칸의 강물 퍼짐과 바닥 종류를 봅니다).
    /// 걸음마다 칸은 제 자리만 쓰고 앞 걸음의 결과만 읽으므로, 나눠 돌려도 결과가 같습니다.
    /// </remarks>
    /// <param name="graph">격자.</param>
    /// <param name="ocean">칸마다 바다인지.</param>
    /// <param name="z">칸마다 높이 (m, 해수면 0).</param>
    /// <param name="slope">칸마다 물을 보내는 이웃까지의 경사 (m/m). 뭍 칸의 값만 씁니다.</param>
    /// <param name="bareRock">칸마다 맨 암반인지.</param>
    /// <param name="seaTempC">칸마다 바다 물의 온도 (°C, 뭍은 NaN).</param>
    /// <param name="plume">칸마다 강물 퍼짐 (0~1).</param>
    /// <param name="rockSeabedSlopeDeg">바다 밑 비탈(그 칸과 둘레 바다 칸의 평균)이 이보다 가파르면 바위 (°). 0 보다 크고 90 보다 작음.</param>
    public static (byte[] Seabed, byte[] CoastKind) Classify(
        CellGraph graph, bool[] ocean, double[] z, double[] slope, bool[] bareRock, double[] seaTempC, double[] plume,
        double rockSeabedSlopeDeg = RockSeabedSlopeDeg)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(ocean);
        ArgumentNullException.ThrowIfNull(z);
        ArgumentNullException.ThrowIfNull(slope);
        ArgumentNullException.ThrowIfNull(bareRock);
        ArgumentNullException.ThrowIfNull(seaTempC);
        ArgumentNullException.ThrowIfNull(plume);
        int n = graph.NCells;
        if (ocean.Length != n || z.Length != n || slope.Length != n || bareRock.Length != n || seaTempC.Length != n || plume.Length != n)
        {
            throw new ArgumentException(
                $"바닥 종류 입력의 칸 수가 격자({n}칸)와 다릅니다: 바다 {ocean.Length}, 높이 {z.Length}, 경사 {slope.Length}, "
                + $"맨 암반 {bareRock.Length}, 물 온도 {seaTempC.Length}, 강물 퍼짐 {plume.Length}.");
        }
        if (!(rockSeabedSlopeDeg > 0.0 && rockSeabedSlopeDeg < 90.0))
        {
            throw new ArgumentException(
                $"sea.rock_slope_deg({rockSeabedSlopeDeg}°)는 0 보다 크고 90 보다 작아야 합니다. 기본값은 {RockSeabedSlopeDeg}° 입니다.");
        }
        double rockCoast = Math.Tan(RockCoastSlopeDeg * Math.PI / 180.0);
        double gravelCoast = Math.Tan(GravelCoastSlopeDeg * Math.PI / 180.0);
        double mudflat = Math.Tan(MudflatMaxSlopeDeg * Math.PI / 180.0);
        double rockSeabed = Math.Tan(rockSeabedSlopeDeg * Math.PI / 180.0);
        int[] nbr = graph.Nbr;

        // 1. 해안 칸(바다와 닿은 뭍 칸)을 찾고, 뭍의 값만으로 바위 해안과 자갈 해변을 정합니다. 나머지 해안 칸은 우선 모래 해변으로 둡니다.
        byte[] coast = new byte[n];
        Parallelism.For(0, n, c =>
        {
            if (ocean[c])
            {
                return;
            }
            bool touchesSea = false;
            for (int s = 0; s < CellGraph.NSlots; s++)
            {
                int v = nbr[(c * CellGraph.NSlots) + s];
                if (v >= 0 && ocean[v])
                {
                    touchesSea = true;
                    break;
                }
            }
            if (!touchesSea)
            {
                return;
            }
            coast[c] = slope[c] > rockCoast || bareRock[c] ? CoastRocky : slope[c] > gravelCoast ? CoastGravelBeach : CoastSandBeach;
        });

        // 2. 바다 밑.
        double[] seabedSlope = MeanOverSeaNeighbors(graph, ocean, SeabedSlope(graph, ocean, z));
        byte[] seabed = new byte[n];
        Parallelism.For(0, n, c =>
        {
            if (!ocean[c])
            {
                return;
            }
            bool nearRock = false;
            bool nearGravel = false;
            for (int s = 0; s < CellGraph.NSlots; s++)
            {
                int v = nbr[(c * CellGraph.NSlots) + s];
                if (v >= 0)
                {
                    nearRock |= coast[v] == CoastRocky;
                    nearGravel |= coast[v] == CoastGravelBeach;
                }
            }
            double depth = -z[c];
            if (seabedSlope[c] > rockSeabed)
            {
                seabed[c] = SeabedRock;
            }
            else if (plume[c] > MudPlume || depth > MudDepthM)
            {
                seabed[c] = SeabedMud;
            }
            else if (nearRock)
            {
                seabed[c] = SeabedRock;
            }
            else if (nearGravel)
            {
                seabed[c] = SeabedGravel;
            }
            else if (seaTempC[c] > CarbonateMinTempC && depth <= CarbonateMaxDepthM && plume[c] < CarbonateMaxPlume)
            {
                seabed[c] = SeabedCarbonateSand;
            }
            else
            {
                seabed[c] = SeabedSand;
            }
        });

        // 3. 바위도 자갈도 아닌 해안 칸을 갯벌, 흰 모래 해변, 모래 해변으로 나눕니다.
        Parallelism.For(0, n, c =>
        {
            if (coast[c] != CoastSandBeach)
            {
                return;
            }
            double plumeMax = 0.0;
            bool carbonate = false;
            for (int s = 0; s < CellGraph.NSlots; s++)
            {
                int v = nbr[(c * CellGraph.NSlots) + s];
                if (v >= 0 && ocean[v])
                {
                    plumeMax = Math.Max(plumeMax, plume[v]);
                    carbonate |= seabed[v] == SeabedCarbonateSand;
                }
            }
            if (plumeMax > MudPlume && slope[c] < mudflat)
            {
                coast[c] = CoastMudflat;
            }
            else if (carbonate)
            {
                coast[c] = CoastWhiteSandBeach;
            }
        });
        return (seabed, coast);
    }
}
