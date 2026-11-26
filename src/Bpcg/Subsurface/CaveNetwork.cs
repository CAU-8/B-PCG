using System;
using System.Collections.Generic;
using Bpcg.Core;

namespace Bpcg.Subsurface;

/// <summary>동굴 통로 그물을 만들 때 쓰는 값들.</summary>
/// <param name="RadiusMaxM">그리는 통로 반지름의 위 한계 [m] (caves.passage_radius_m).</param>
/// <param name="SinkSpacingM">싱크홀 사이의 최소 거리 [m] (caves.sink_spacing_m).</param>
/// <param name="SinkMinAreaM2">빗물이 이 넓이 이상 모이는 칸만 싱크홀 후보 [m²] (caves.sink_min_area_m2).</param>
/// <param name="PathWavelengthM">물이 잘 통하는 자리와 덜 통하는 자리가 번갈아 놓이는 간격 [m] (caves.noise_wavelength_m).</param>
/// <param name="Seed">시드 (planet.seed).</param>
/// <param name="WallRetreatMPerYr">물이 찬 통로의 벽이 한 해에 녹아 물러나는 거리 [m/yr] (caves.wall_retreat_m_per_yr). 0 이면 시간으로는 굵기를 막지 않습니다.</param>
/// <param name="ActiveZoneM">통로가 물에 잠겨 자라는 높이 범위 [m] (caves.active_zone_m).</param>
/// <param name="HydraulicGradient">통로 안 물 높이의 기울기 [m/m] (groundwater.karst_gradient).</param>
/// <param name="GravityMS2">중력 가속도 [m/s²].</param>
public sealed record CaveNetworkParams(
    double RadiusMaxM, double SinkSpacingM, double SinkMinAreaM2, double PathWavelengthM, long Seed,
    double WallRetreatMPerYr = 0.0, double ActiveZoneM = 0.0, double HydraulicGradient = 0.002, double GravityMS2 = 9.81);

/// <summary>통로 그물에 더 줄 수 있는 칸 값들. 주지 않은 값은 괄호 안처럼 봅니다.</summary>
/// <param name="DischargeM3PerYr">(N,) 지표에서 그 칸을 지나는 물 [m³/yr] (없으면 상류 넓이 × 1 m/yr).</param>
/// <param name="Receiver">(N,) 지표의 물이 흘러가는 이웃. 같은 골짜기의 싱크홀이 위쪽 싱크홀의 물을 두 번 세지 않게 합니다 (없으면 그대로 더함).</param>
/// <param name="UpliftMPerYr">(N,) 땅이 솟는 속도 = 강이 파 내려가는 속도 [m/yr] (없으면 통로 굵기를 시간으로 막지 않음).</param>
/// <param name="BedZ">(N,) 아래층이 놓인 지층의 층리면(지층이 쌓인 면) 높이 [m] (없으면 통로가 물 높이를 그대로 따라감).</param>
/// <param name="DipSin">(N,) 그 층리면이 기운 각의 sin (없으면 0).</param>
/// <param name="StreamLossM3PerYr">
/// (N,) 지표의 개울이 그 칸에서 땅속으로 사라지는 물 [m³/yr]. 0 보다 큰 칸은 솎지 않고 싱크홀로 둡니다.
/// 이 칸의 아래층은 '층이 있고 샘까지 길이 있는지(넘침 높이가 있는지, SpillHead)'만 봅니다 (히어로 단계의
/// KarstStreams.DrainsToSpring 과 같은 판단).
/// </param>
/// <param name="GroundAtNodes">칸 번호 목록 → 그 칸 노드(graph.Pos) 자리의 지표 높이 [m]. 수직굴 꼭대기를 정합니다 (없으면 칸 높이 z).</param>
/// <param name="StreamRouteLevel0">
/// (N,) 개울이 사라지는 칸에서 샘까지 길을 찾을 때 쓰는 아래층 높이 [m] (Caves.RouteLevel0). 그린 아래층에, 지표에서 300 m 보다 깊어
/// 굽지 않는 석회암의 아래 지하수면 높이를 더한 값입니다. 빗물이 모이는 싱크홀은 쓰지 않습니다 (없으면 levels 의 아래층).
/// </param>
/// <param name="SolubleFloorAt">
/// (x, y, z) 자리마다 높이 z 아래로 이어지는 녹는 암석의 바닥 높이 [m]. 아래층 통로가 층리면을 따라 물 높이 아래로 내려가도 통로
/// 바닥(가운데 선 − 반지름)이 이 높이 아래로 가지 않습니다. 통로의 꺾임을 다듬은 뒤의 자리에서 묻습니다. NaN 을 돌려준 자리와
/// 주지 않은 경우는 막지 않습니다.
/// </param>
public sealed record CaveNetworkFields(
    double[]? DischargeM3PerYr = null, long[]? Receiver = null, double[]? UpliftMPerYr = null, double[]? BedZ = null,
    double[]? DipSin = null, double[]? StreamLossM3PerYr = null, Func<int[], double[]>? GroundAtNodes = null,
    double[]? StreamRouteLevel0 = null, Func<double[], double[], double[], double[]>? SolubleFloorAt = null);

/// <summary>
/// 통로 그물의 모양을 잰 값. 실제 동굴 지도 34개에서 잰 범위 (Collon 외 2017, 정정 2021): 마디에 이어진 가지 수의 평균 1.75~2.57
/// (고리가 없는 나뭇가지 꼴은 2 미만), 가지 길이의 흩어진 정도 0.90~2.56, 굽은 정도 1.12~1.54. 지도는 사람이 들어간 통로만
/// 담으므로 좁거나 물에 잠긴 통로는 빠져 있습니다.
/// </summary>
/// <param name="NCaves">동굴 수: 샘 하나로 모이는 통로들을 동굴 하나로 셉니다.</param>
/// <param name="NSinglePassage">그 가운데 통로가 한 번도 합쳐지지 않는 동굴 수 (싱크홀에서 샘까지 외길).</param>
/// <param name="NBranches">가지 수: 마디(통로의 끝, 통로가 합쳐지는 자리)와 마디 사이의 통로.</param>
/// <param name="MeanDegree">마디마다 이어진 가지 수의 평균. 통로가 한 번이라도 합쳐지는 동굴에서만 냅니다 (그런 동굴이 없으면 0).</param>
/// <param name="BranchLengthCv">가지 길이의 표준편차 ÷ 평균.</param>
/// <param name="Tortuosity">가지 길이 ÷ 양 끝의 직선 거리의 평균.</param>
public sealed record CaveShape(
    long NCaves, long NSinglePassage, long NBranches, double MeanDegree, double BranchLengthCv, double Tortuosity);

/// <summary>
/// 동굴 통로 그물: 싱크홀(지표의 물이 땅속으로 들어가는 자리)에서 샘(땅속의 물이 지표로 나오는 자리)까지 물이 흐르는 길.
/// </summary>
/// <remarks>
/// 왜 필요한가: 지구의 석회암 동굴은 빗물이 땅속으로 들어가 샘으로 나오는 물길을 따라 자랍니다. 물길은 하류로 가며 합쳐지므로
/// 통로가 나뭇가지 꼴이 됩니다. 무작위 무늬로 통로를 놓으면 물이 들어가는 곳도 나오는 곳도 없는 닫힌 고리가 됩니다.
/// 만드는 순서 (docs/pipeline.md 10장 4번):
/// 1. 물이 많이 들어가는 싱크홀부터 차례로, 샘이나 '이미 뚫린 통로'까지 가장 힘이 덜 드는 길을 찾습니다. 암석의 틈으로 물이
///    새다가 한 길이 먼저 뚫리면 그 길의 물 높이가 뚝 떨어져 둘레의 물이 그리로 끌려듭니다 (Dreybrodt 의 돌파 시간 이론;
///    Perne, Covington &amp; Gabrovšek 2014). 그래서 뒤에 뚫리는 길은 샘까지 따로 가지 않고 먼저 뚫린 통로에 붙습니다
///    (같은 방식의 생성기: Borghi, Renard &amp; Jenni 2012). 힘은 거리 × 그 자리의 저항이고, 저항은 암석의 틈이 많고 적은
///    정도를 흉내 낸 잡음입니다. 아래층(지금 물이 흐르는 층)에서는 물 높이가 낮아지는 쪽으로만 갑니다.
/// 2. 통로마다 지나는 물의 양을 더합니다. 같은 골짜기의 아래쪽 싱크홀은 위쪽 싱크홀이 이미 삼킨 물을 빼고 셉니다.
/// 3. 통로 굵기는 두 한계 가운데 작은 쪽입니다 (Radius). 큰비 때의 물을 가득 채워 흘릴 수 있는 굵기, 그리고 통로가 물에 잠겨
///    있는 동안 벽이 녹아 물러난 거리.
/// 4. 아래층 통로는 지층이 쌓인 면(층리면)을 따라갑니다. 층리면이 기울어 있으면 통로가 물 높이 아래로 내려갔다가 틈을 타고
///    다시 올라오기를 되풀이해, 물에 잠긴 구간과 동굴 호수가 생깁니다 (LoopDepth 가 폭, SawtoothDepth 가 깊이). 녹는 암석의
///    바닥 아래로는 내려가지 않습니다 (CaveNetworkFields.SolubleFloorAt).
/// 길을 고르는 비교에는 더하기, 곱하기만 씁니다. 지수·로그 함수는 운영체제마다 끝자리가 달라 길이 달라질 수 있기 때문입니다.
/// 굵기와 깊이(길을 고른 뒤의 모양)에만 거듭제곱을 씁니다.
/// </remarks>
public sealed class CaveNetwork
{
    /// <summary>
    /// 수직굴 꼭대기를 노드 자리 지표보다 더 올리는 높이 [m]. 굽기의 프랙탈 디테일(보기용 잔무늬)이 지표를 올린 곳에서도
    /// 구멍이 열리게 둡니다. laptop 프로필 시드 0 회랑에서 디테일이 더한 높이는 가장 큰 곳이 7.2 m 였습니다.
    /// </summary>
    public const double ShaftAboveGroundM = 10.0;

    /// <summary>
    /// 통로 반지름의 아래 한계 [m]. 동굴 메시는 2 m 칸으로 만듭니다. 반지름이 2 m 보다 작은 관은 칸 사이로 빠져
    /// 메시가 토막 나므로 이보다 가늘게 두지 않습니다. 지구의 작은 통로는 이보다 가는 것이 많습니다.
    /// </summary>
    public const double RadiusMinM = 2.0;

    /// <summary>싱크홀을 둘 수 있는 가장 얕은 깊이 [m]: 지표에서 동굴 층까지 이만큼 이상일 때만 둡니다.</summary>
    public const double SinkMinDepthM = 10.0;

    /// <summary>
    /// 아래층 통로를 물 높이 위로 올리는 정도 (반지름의 배수). 0.4 면 관의 바닥이 물 높이보다 반지름의 0.6 배 아래라,
    /// 통로 바닥에 얕은 물이 흐릅니다 (반지름 3 m 면 물 깊이 1.8 m).
    /// </summary>
    public const double StreamLift = 0.4;

    /// <summary>저항의 범위: 물이 가장 잘 통하는 자리와 가장 덜 통하는 자리 (거리 1 m 를 가는 데 드는 힘).</summary>
    public const double ResistanceMin = 0.25;
    public const double ResistanceMax = 4.0;

    /// <summary>
    /// 큰비 때 통로를 지나는 물 ÷ 한 해 평균 (가정). 통로는 물이 관을 가득 채우는 큰비 때 자라므로, 굵기는 평균이 아니라
    /// 큰비 때의 물로 정합니다. 30 은 작은 유역의 큰비 유량이 평균의 수십 배라는 데서 고른 값이고, 잰 값이 아닙니다.
    /// </summary>
    public const double FloodRatio = 30.0;

    /// <summary>통로 벽의 마찰 계수 f (Darcy–Weisbach). 동굴 통로는 0.1 에서 수백까지 재어집니다. 매끈한 쪽 값입니다.</summary>
    public const double FrictionFactor = 0.1;

    /// <summary>Worthington 2004 식 10 의 계수와 지수: 통로가 물 높이 아래로 내려가는 평균 깊이 D = 0.18 × (L × sin θ)^0.81 [m].</summary>
    public const double LoopCoefficient = 0.18;
    public const double LoopExponent = 0.81;

    /// <summary>통로가 물 높이 아래로 오르내리는 폭이 이보다 작으면 [m] 물 높이를 그대로 따라간다고 봅니다.</summary>
    public const double LoopMinM = 1.0;

    /// <summary>오르내리는 폭의 위 한계 [m]. Audra &amp; Palmer 2011 은 가장 큰 폭이 200 m 를 넘기도 한다고 적었습니다.</summary>
    public const double LoopMaxM = 200.0;

    /// <summary>샘으로 올라오는 통로의 가장 가파른 기울기 (가정). 0.5 면 샘에서 100 m 떨어진 곳의 통로는 물 높이 아래 50 m 보다 깊지 않습니다.</summary>
    public const double SpringRiseSlope = 0.5;

    /// <summary>동굴 호수로 세는 물의 가장 얕은 깊이 [m] 와, 물 위에 남아야 하는 빈 높이 [m].</summary>
    public const double LakeMinWaterM = 2.5;
    public const double LakeMinAirM = 0.5;

    private const int PathOctaves = 3;
    private const int SmoothPasses = 2;
    private const double LevelEps = 1.0e-6;

    // 해시·잡음의 용도 번호.
    private const long StreamSink = 7261;
    private const long StreamPath = 7262;

    private CaveNetwork()
    {
    }

    /// <summary>동굴 층 수 K.</summary>
    public int NLevels { get; private init; }

    /// <summary>통로 조각의 한쪽 끝 (E, 3) [m]. 층 통로는 상류 쪽 끝, 수직굴은 지표 쪽 끝.</summary>
    public double[] SegA { get; private init; } = [];

    /// <summary>통로 조각의 다른 쪽 끝 (E, 3) [m].</summary>
    public double[] SegB { get; private init; } = [];

    /// <summary>통로 조각의 반지름 (E,) [m].</summary>
    public double[] SegRadius { get; private init; } = [];

    /// <summary>조각이 수직굴이면 참 (E,). 수직굴만 지표로 열립니다.</summary>
    public bool[] SegIsShaft { get; private init; } = [];

    /// <summary>동굴 호수의 가운데 칸 번호 (오름차순). 아래층 통로가 물에 깊이 잠기되 천장 아래에 빈 곳이 남은 구간마다 하나.</summary>
    public int[] Lakes { get; private init; } = [];

    /// <summary>호수마다 가운데의 x, y 와 물 높이 (L, 3) [m]. Lakes 와 같은 순서.</summary>
    public double[] LakeXyz { get; private init; } = [];

    /// <summary>싱크홀 칸 번호 (오름차순).</summary>
    public int[] Sinks { get; private init; } = [];

    /// <summary>싱크홀마다 그 구멍으로 들어가는 물 [m³/yr] (위쪽 싱크홀이 삼킨 물은 뺌). Sinks 와 같은 순서.</summary>
    public double[] SinkDischarge { get; private init; } = [];

    /// <summary>싱크홀이 지표의 개울이 사라지는 자리이면 참. Sinks 와 같은 순서.</summary>
    public bool[] SinkIsStream { get; private init; } = [];

    /// <summary>
    /// 개울이 땅속으로 사라지는 칸(StreamLossM3PerYr &gt; 0) 가운데, 아래에 샘까지 이어지는 동굴 층이 없어 싱크홀을 두지 못한 칸 수.
    /// 이런 칸은 지표에서는 개울이 사라지지만 3D 회랑에는 구멍도 통로도 없습니다. 히어로 단계가 같은 판단(KarstStreams.DrainsToSpring)으로
    /// 그런 자리에서는 개울을 사라지게 하지 않으므로 0 이어야 합니다. 0 이 아니면 히어로 단계와 굽기가 다른 동굴 층이나 샘을 본 것입니다.
    /// </summary>
    public int NStreamSinksWithoutCave { get; private init; }

    /// <summary>(K·N) 층 k 의 칸 c 를 지나는 물 [m³/yr] (index k·N + c). 통로가 없으면 0.</summary>
    public double[] Flow { get; private init; } = [];

    /// <summary>(K·N) 층 k 에서 칸 c 의 물이 흘러가는 다음 칸. 샘 칸과 통로가 없는 칸은 −1.</summary>
    public int[] Parent { get; private init; } = [];

    /// <summary>(K·N) 통로의 반지름 [m]. 통로가 없으면 0.</summary>
    public double[] CellRadius { get; private init; } = [];

    /// <summary>(K·N) 통로 가운데 선의 높이 [m]. 통로가 없으면 NaN.</summary>
    public double[] AxisZ { get; private init; } = [];

    /// <summary>(N,) 칸마다 그 칸에서 하류로 나가는 층 통로의 길이 합 [m].</summary>
    public double[] PassageM { get; private init; } = [];

    /// <summary>아래층 통로 가운데 천장까지 물에 잠긴 길이 [m].</summary>
    public double FloodedM { get; private init; }

    /// <summary>아래층 통로의 길이 [m].</summary>
    public double LowerLevelM { get; private init; }

    /// <summary>아래층 통로 칸에서 물 높이 아래로 내려간 깊이의 평균 [m] (통로 가운데 선 기준).</summary>
    public double MeanLoopDepthM { get; private init; }

    /// <summary>물이 나오는 샘 수 (통로가 닿은 샘 칸, 층마다 따로 셈).</summary>
    public int NSprings { get; private init; }

    /// <summary>그물의 모양을 잰 값 (Shape). 실제 동굴 지도에서 잰 범위와 비교합니다.</summary>
    public CaveShape Shape { get; private init; } = new(0, 0, 0, 0.0, 0.0, 0.0);

    public int NSegments => SegRadius.Length;

    /// <summary>
    /// 물을 가득 채워 흘릴 때 필요한 관의 반지름 [m] (full_pipe_radius). 관에 물이 가득 차 흐를 때 기울기 i 와 유량 Q 사이에는
    /// i = f·Q² ÷ (4π²·g·R⁵) 가 맞습니다 (Darcy–Weisbach 식, White 2007 의 식 4). R 로 풀면 R = (f·Q² ÷ (4π²·g·i))^(1/5) 입니다.
    /// 물이 10배 늘면 반지름은 10^0.4 ≈ 2.5배가 됩니다.
    /// 예: f = 0.1, i = 0.002, g = 9.81 m/s² 에서 Q = 10 m³/s 면 약 1.7 m, 60 m³/s 면 약 3.4 m.
    /// qM3PerS: 관을 지나는 물 [m³/s].
    /// </summary>
    public static double FullPipeRadius(double qM3PerS, double gradient, double gravity)
    {
        if (!(qM3PerS > 0.0) || !(gradient > 0.0) || !(gravity > 0.0))
        {
            return 0.0;
        }
        return Math.Pow(FrictionFactor * qM3PerS * qM3PerS / (4.0 * Math.PI * Math.PI * gravity * gradient), 0.2);
    }

    /// <summary>
    /// 반지름 radiusM [m] 인 관이 가득 차서 흘릴 수 있는 물 [m³/s]: Q = sqrt(4π²·g·i·R⁵ ÷ f). FullPipeRadius 의 역입니다.
    /// </summary>
    public static double FullPipeDischarge(double radiusM, double gradient, double gravity)
    {
        if (!(radiusM > 0.0) || !(gradient > 0.0) || !(gravity > 0.0))
        {
            return 0.0;
        }
        return Math.Sqrt(4.0 * Math.PI * Math.PI * gravity * gradient * Math.Pow(radiusM, 5.0) / FrictionFactor);
    }

    /// <summary>
    /// 통로가 물에 잠겨 있는 동안 벽이 녹아 물러나는 거리 [m] (time_limited_radius) = 벽이 녹는 속도 × 잠겨 있는 햇수.
    /// 통로는 물이 가득 찬 동안에만 사방으로 자랍니다. 강이 한 해에 U 만큼 파 내려가면 물 높이도 그만큼 내려가므로,
    /// 높이 범위 activeZoneM 을 지나는 데 activeZoneM ÷ U 년이 걸립니다. 강이 빨리 파 내려가는 산에서는 통로가 굵어질 틈이 없습니다
    /// (Audra &amp; Palmer 2011: 빨리 파이는 곳의 통로는 큰 굵기에 닿을 만큼 오래 쓰이지 못함). 벽이 녹는 속도는 한 해
    /// 0.1~1 mm 가 위 한계입니다 (Palmer 1991).
    /// 예: 한 해 0.1 mm 씩 녹고 범위가 10 m 일 때, 강이 한 해 1 mm 파 내려가면 10,000년 동안 1 m, 0.2 mm 파 내려가면 5 m.
    /// 솟는 속도가 0 이하이거나 설정이 0 이면 +∞ (시간으로는 막지 않음).
    /// </summary>
    public static double TimeLimitedRadius(double upliftMPerYr, double wallRetreatMPerYr, double activeZoneM)
    {
        if (!(wallRetreatMPerYr > 0.0) || !(activeZoneM > 0.0) || !(upliftMPerYr > 0.0))
        {
            return double.PositiveInfinity;
        }
        return wallRetreatMPerYr * activeZoneM / upliftMPerYr;
    }

    /// <summary>
    /// 통로 반지름 [m]: 큰비 때의 물(FloodRatio × 평균)을 가득 채워 흘리는 굵기와, 잠겨 있는 동안 벽이 물러난 거리 가운데
    /// 작은 쪽. 그리는 한계 RadiusMinM 과 RadiusMaxM 사이로 자릅니다. qM3PerYr: 통로를 지나는 물의 한 해 평균 [m³/yr].
    /// </summary>
    public static double Radius(double qM3PerYr, double upliftMPerYr, CaveNetworkParams p)
    {
        double flood = FloodRatio * Math.Max(qM3PerYr, 0.0) / Constants.SecondsPerYear;
        double r = Math.Min(
            FullPipeRadius(flood, p.HydraulicGradient, p.GravityMS2),
            TimeLimitedRadius(upliftMPerYr, p.WallRetreatMPerYr, p.ActiveZoneM));
        return Math.Min(Math.Max(r, Math.Min(RadiusMinM, p.RadiusMaxM)), p.RadiusMaxM);
    }

    /// <summary>
    /// 통로가 물 높이 아래로 오르내리는 폭 [m] (loop_depth). 물은 지층이 쌓인 면(층리면)을 따라 흐르기 쉽습니다. 층리면이 기울어
    /// 있으면 통로는 면을 따라 물 높이 아래로 내려갔다가 세로 틈을 타고 올라오기를 되풀이합니다. 물길이 길고 지층이 가파를수록
    /// 깊이 내려갑니다: 평균 깊이 D = 0.18 × (L × sin θ)^0.81 [m] (Worthington 2004 식 10, 싱크홀에서 샘까지 20개 물길을 맞춘 식,
    /// 상관계수 0.79). L 은 물길 길이 [m], θ 는 지층이 기운 각입니다. 톱니 꼴로 0 에서 폭까지 오르내리면 평균이 폭의 절반이므로
    /// 폭 = 2D 입니다.
    /// 예: 길이 2,200 m, 기운 각 0.6° 면 D ≈ 0.18 × 23^0.81 ≈ 2.3 m (매머드 동굴에서 잰 값 2 m). 길이 800 m, 10° 면 약 10 m.
    /// 폭이 LoopMinM 보다 작으면 0, LoopMaxM 보다 크면 LoopMaxM.
    /// </summary>
    public static double LoopDepth(double pathLengthM, double dipSin)
    {
        double x = Math.Max(pathLengthM, 0.0) * Math.Max(dipSin, 0.0);
        if (!(x > 0.0))
        {
            return 0.0;
        }
        double span = 2.0 * LoopCoefficient * Math.Pow(x, LoopExponent);
        return span < LoopMinM ? 0.0 : Math.Min(span, LoopMaxM);
    }

    /// <summary>
    /// 통로가 층리면을 따라 물 높이 아래로 내려간 깊이 [m] (sawtooth_depth, 샘 쪽을 자르기 전). 층리면은 폭 span [m] 간격으로
    /// 겹겹이 있다고 봅니다. 통로는 한 면을 따라가다가, 그 면이 물 높이보다 폭만큼 깊어지면 세로 틈을 타고 위의 면으로 옮겨 탑니다.
    /// 면이 물 높이 위로 올라가면 아래의 면으로 옮겨 탑니다. 그래서 깊이는 물길을 따라 0 에서 폭까지 톱니 꼴로 오르내립니다.
    /// 칸마다 따로 (물 높이 − 층리면 높이) 를 폭으로 나눈 나머지를 쓰면, 그 값이 폭의 수십 배라 폭이 조금만 달라도 나머지가 크게
    /// 바뀝니다. 그래서 하류 칸(샘 쪽 이웃)의 깊이 downstreamDepth 에 두 칸 사이에서 (물 높이 − 층리면 높이) 가 바뀐 만큼만 더하고,
    /// 0 이상 폭 미만이 되도록 폭을 더하거나 뺍니다. 두 칸이 같은 면 위에 있으면 통로의 높이 차는 층리면의 높이 차와 같습니다.
    /// aboveBed, downstreamAboveBed: 이 칸과 하류 칸에서 물 높이 − 층리면 높이 [m]. 하류 칸이 없거나(샘) 그 칸의 층리면을 모르면
    /// (NaN) aboveBed 를 폭으로 나눈 나머지, 곧 물 높이 바로 아래의 면에서 시작합니다. 폭이 0 이거나 층리면을 모르면 0 (물 높이를 따라감).
    /// 예: 폭 10 m. 샘 칸에서 물 높이가 층리면보다 123 m 위면 깊이는 123 − 120 = 3 m 입니다. 상류 칸에서 그 차가 127 m 면 7 m 이고,
    /// 그다음 칸에서 135 m 면 7 + 8 = 15 m 로 폭을 넘으므로 위의 면으로 옮겨 타 5 m 입니다.
    /// </summary>
    public static double SawtoothDepth(double span, double aboveBed, double downstreamAboveBed, double downstreamDepth)
    {
        if (!(span > 0.0) || !double.IsFinite(aboveBed))
        {
            return 0.0;
        }
        double x = double.IsFinite(downstreamDepth) && double.IsFinite(downstreamAboveBed)
            ? downstreamDepth + (aboveBed - downstreamAboveBed)
            : aboveBed;
        double depth = x - (span * Math.Floor(x / span));
        // 반올림으로 span 과 같아진 값은 위의 면(깊이 0)으로 봅니다.
        return depth < span ? depth : 0.0;
    }

    /// <summary>잡음 값(대략 −1~1)을 저항으로 바꿉니다. 0 이하의 잡음은 잘 통하는 자리, 큰 잡음은 덜 통하는 자리입니다.</summary>
    public static double Resistance(double noise)
    {
        double t = Math.Min(Math.Max(0.5 + noise, 0.0), 1.0);
        return ResistanceMin + ((ResistanceMax - ResistanceMin) * t * t);
    }

    /// <summary>
    /// 층마다 샘에서 거꾸로 닿는 칸 (reach): 그 칸의 물이 동굴 층을 타고 샘까지 갈 수 있으면 참입니다. 샘 칸에서 시작해 층이 있는
    /// 이웃 칸으로 넓힙니다. 층이 없는 칸(NaN)은 지나지 못합니다.
    /// 아래층(k = 0, 지금 물이 흐르는 층)은 물 높이가 샘 쪽으로 낮아지는 길만 셉니다. 물은 높은 데서 낮은 데로만 흐르기 때문입니다.
    /// 위층은 옛 물길이라 따지지 않습니다.
    /// 빗물이 모이는 싱크홀을 둘지 정할 때 씁니다(Build). 개울이 사라지는 싱크홀은 넘침 높이(SpillHead)로 봅니다.
    /// levels·outlet: (K·N) 층 k 의 높이 [m] 와 샘 칸 (index k·N + c). 반환: (K·N).
    /// 예: 샘 칸의 아래층이 100 m, 그 이웃이 100.05 m, 그다음 칸이 99 m 면 앞의 두 칸만 닿습니다. 99 m 칸의 물은 100.05 m 로 오르지 못합니다.
    /// </summary>
    public static bool[] Reach(CellGraph graph, double[] levels, bool[] outlet)
    {
        int n = graph.NCells;
        if (n == 0 || levels.Length == 0 || levels.Length % n != 0 || outlet.Length != levels.Length)
        {
            throw new ArgumentException(
                $"levels 와 outlet 은 (층 수 × {n},) 길이여야 합니다. 받은 길이: levels {levels.Length}, outlet {outlet.Length}");
        }
        int kN = levels.Length / n;
        int[] nbr = graph.Nbr;
        const int nSlots = CellGraph.NSlots;
        bool[] reach = new bool[kN * n];
        var queue = new Queue<int>();
        for (int k = 0; k < kN; k++)
        {
            int off = k * n;
            for (int c = 0; c < n; c++)
            {
                if (outlet[off + c] && double.IsFinite(levels[off + c]))
                {
                    reach[off + c] = true;
                    queue.Enqueue(c);
                }
            }
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                double lv = levels[off + v];
                for (int s = 0; s < nSlots; s++)
                {
                    int u = nbr[(v * nSlots) + s];
                    if (u < 0 || reach[off + u])
                    {
                        continue;
                    }
                    double lu = levels[off + u];
                    if (!double.IsFinite(lu) || (k == 0 && lu < lv - LevelEps))
                    {
                        continue;
                    }
                    reach[off + u] = true;
                    queue.Enqueue(u);
                }
            }
        }
        return reach;
    }

    /// <summary>
    /// 넘침 높이 (spill_head) [m]: 칸의 물이 샘까지 빠지려면 차올라야 하는 가장 낮은 물 높이입니다.
    /// 샘에서 시작해 이웃으로 넓히며, 이웃 칸으로 갈 때의 높이는 max(지금 칸의 넘침 높이, 이웃 칸의 층 높이)입니다(가장 높은 자리가
    /// 가장 낮은 길을 찾는 다익스트라). 층 높이가 샘 쪽으로 낮아지는 길이 있는 칸은 넘침 높이가 그 칸의 층 높이와 같습니다.
    /// 사방이 더 높은 층으로 막힌 곳은 가장 낮은 넘는 자리까지 물이 차오른다고 봅니다. 물에 잠긴 통로는 이 높이 아래에서
    /// 오르내릴 수 있습니다(물 높이 아래로 내려갔다 올라오는 통로, LoopDepth 와 같은 까닭).
    /// 히어로 단계(KarstStreams.DrainsToSpring)와 굽기(개울이 사라지는 싱크홀의 길, Build)가 이 함수로 같은 판단을 합니다.
    /// level: (N,) 층 높이 [m] (없으면 NaN, 지나지 못함). outlet: (N,) 샘 칸. 반환: (N,) [m]. 샘까지 이어지지 않는 칸은 +∞.
    /// 예: 층 높이가 350.4 m 인 칸에서 샘까지 가는 모든 길이 351.15 m 인 칸을 지나야 하면, 넘침 높이는 351.15 m 입니다.
    /// </summary>
    public static double[] SpillHead(CellGraph graph, double[] level, bool[] outlet)
    {
        int n = graph.NCells;
        if (level.Length != n || outlet.Length != n)
        {
            throw new ArgumentException($"level 과 outlet 은 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: {level.Length}, {outlet.Length}");
        }
        int[] nbr = graph.Nbr;
        const int nSlots = CellGraph.NSlots;
        double[] head = new double[n];
        Array.Fill(head, double.PositiveInfinity);
        var pq = new PriorityQueue<int, (double Head, int Cell)>();
        for (int c = 0; c < n; c++)
        {
            if (outlet[c] && double.IsFinite(level[c]))
            {
                head[c] = level[c];
                pq.Enqueue(c, (level[c], c));
            }
        }
        while (pq.TryDequeue(out int v, out (double Head, int Cell) pr))
        {
            if (pr.Head > head[v])
            {
                continue;
            }
            for (int s = 0; s < nSlots; s++)
            {
                int u = nbr[(v * nSlots) + s];
                if (u < 0 || !double.IsFinite(level[u]))
                {
                    continue;
                }
                double h = Math.Max(pr.Head, level[u]);
                if (h < head[u])
                {
                    head[u] = h;
                    pq.Enqueue(u, (h, u));
                }
            }
        }
        return head;
    }

    /// <summary>
    /// 강가 샘 (bank_springs): 아래층(지금 물이 흐르는 층)의 물은 그 물 높이를 정한 큰 물(강·호수·바다)로 빠집니다.
    /// 아래층이 있는 칸이 수면이 그 층 높이 이하인 물 칸이거나, 둘레 여덟 칸에 그런 물 칸이 있으면 샘입니다. 통로는 물 높이에서
    /// 저항이 가장 작은 끝으로 빠지고, 샘의 자리가 지하수면을 정한다는 관찰을 따랐습니다(Audra &amp; Palmer 2011).
    /// 수면이 그 층보다 높은 물 칸(지하수면보다 높이 떠 있는 개울)은 그 자리가 샘이 아닙니다. 동굴의 물이 위로 솟지 못하기 때문입니다.
    /// 물가의 샘은 수면이 가장 낮은 이웃 물 칸으로 잇습니다(같으면 번호가 작은 칸).
    /// 히어로 단계(KarstStreams.DrainsToSpring)와 굽기(HeroVolume)가 이 함수로 같은 샘을 씁니다.
    /// level0: (N,) 아래층 높이 [m] (없으면 NaN). isWater: (N,) 수면이 있는 물 칸. waterLevel: (N,) 수면 [m].
    /// skip: (N,) 다른 까닭으로 이미 샘인 칸(산비탈 밖으로 열리는 입구). 이 칸은 다시 보지 않습니다. 없으면 null.
    /// 반환: Spring (N,) 샘 칸, BankTo (N,) 물가의 샘이 잇는 물 칸 (물 칸 자신이 샘이거나 샘이 아니면 −1).
    /// 예: 아래층이 120.3 m 인 칸 옆에 수면 120.0 m 인 강 칸과 118.5 m 인 강 칸이 있으면, 그 칸은 샘이고 118.5 m 칸으로 잇습니다.
    /// 아래층이 350 m 인 칸에 수면 520 m 인 개울이 흐르면 그 칸은 샘이 아닙니다.
    /// </summary>
    public static (bool[] Spring, int[] BankTo) BankSprings(
        CellGraph graph, double[] level0, bool[] isWater, double[] waterLevel, bool[]? skip = null)
    {
        int n = graph.NCells;
        if (level0.Length != n || isWater.Length != n || waterLevel.Length != n || (skip is not null && skip.Length != n))
        {
            throw new ArgumentException(
                $"level0, isWater, waterLevel, skip 은 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: {level0.Length}, "
                + $"{isWater.Length}, {waterLevel.Length}, {skip?.Length.ToString() ?? "없음"}");
        }
        int[] nbr = graph.Nbr;
        const int nSlots = CellGraph.NSlots;
        bool[] spring = new bool[n];
        int[] bankTo = new int[n];
        Array.Fill(bankTo, -1);
        for (int c = 0; c < n; c++)
        {
            double lv = level0[c];
            if ((skip is not null && skip[c]) || !double.IsFinite(lv))
            {
                continue;
            }
            // 물 칸은 수면이 그 층 높이 이하일 때 그 자리가 샘입니다. 수면이 더 높은 물 칸(지하수면보다 높이 떠 있는 개울)으로는
            // 동굴의 물이 솟지 못하므로, 다른 칸처럼 둘레의 물 칸을 봅니다.
            if (isWater[c] && waterLevel[c] <= lv + LevelEps)
            {
                spring[c] = true;
                continue;
            }
            int best = -1;
            for (int s = 0; s < nSlots; s++)
            {
                int nb = nbr[(c * nSlots) + s];
                if (nb < 0 || !isWater[nb] || !(waterLevel[nb] <= lv + LevelEps))
                {
                    continue;
                }
                if (best < 0 || waterLevel[nb] < waterLevel[best] || (waterLevel[nb] == waterLevel[best] && nb < best))
                {
                    best = nb;
                }
            }
            if (best >= 0)
            {
                spring[c] = true;
                bankTo[c] = best;
            }
        }
        return (spring, bankTo);
    }

    /// <summary>
    /// 통로 그물을 만듭니다.
    /// z: (N,) 지표 높이 [m]. levels: (K·N) 층 k 의 높이 [m] (index k·N + c), 동굴 층이 없는 칸은 NaN.
    /// outlet: (K·N) 층 k 의 샘 칸. sinkAllowed: (N,) 싱크홀을 둘 수 있는 칸 (지표 암석이 물에 녹고, 물에 잠기지 않은 육지 칸).
    /// drainageAreaM2: (N,) 그 칸까지 빗물이 모이는 넓이 [m²]. more: 더 줄 수 있는 칸 값들.
    /// </summary>
    public static CaveNetwork Build(
        CellGraph graph, double[] z, double[] levels, bool[] outlet, bool[] sinkAllowed, double[] drainageAreaM2,
        CaveNetworkParams p, CaveNetworkFields? more = null)
    {
        if (graph.Kind != "flat")
        {
            throw new ArgumentException($"동굴 통로 그물은 평면 그래프에서만 만듭니다. 받은 그래프: '{graph.Kind}'");
        }
        int n = graph.NCells;
        if (z.Length != n || sinkAllowed.Length != n || drainageAreaM2.Length != n)
        {
            throw new ArgumentException(
                $"z, sinkAllowed, drainageAreaM2 는 칸 수 ({n},) 와 같은 길이여야 합니다. "
                + $"받은 길이: {z.Length}, {sinkAllowed.Length}, {drainageAreaM2.Length}");
        }
        if (levels.Length == 0 || levels.Length % n != 0 || outlet.Length != levels.Length)
        {
            throw new ArgumentException(
                $"levels 와 outlet 은 (층 수 × {n},) 길이여야 합니다. 받은 길이: levels {levels.Length}, outlet {outlet.Length}");
        }
        if (!(p.RadiusMaxM > 0.0 && p.SinkSpacingM >= 0.0 && p.SinkMinAreaM2 >= 0.0 && p.PathWavelengthM > 0.0
            && p.WallRetreatMPerYr >= 0.0 && p.ActiveZoneM >= 0.0 && p.HydraulicGradient > 0.0 && p.GravityMS2 > 0.0))
        {
            throw new ArgumentException(
                "동굴 통로 설정은 passage_radius_m > 0, sink_spacing_m ≥ 0, sink_min_area_m2 ≥ 0, noise_wavelength_m > 0, "
                + "wall_retreat_m_per_yr ≥ 0, active_zone_m ≥ 0, karst_gradient > 0 이어야 합니다. 받은 값: "
                + $"{p.RadiusMaxM}, {p.SinkSpacingM}, {p.SinkMinAreaM2}, {p.PathWavelengthM}, {p.WallRetreatMPerYr}, {p.ActiveZoneM}, "
                + $"{p.HydraulicGradient}");
        }
        more ??= new CaveNetworkFields();
        foreach ((string name, int? len) in new (string, int?)[]
        {
            ("DischargeM3PerYr", more.DischargeM3PerYr?.Length), ("Receiver", more.Receiver?.Length),
            ("UpliftMPerYr", more.UpliftMPerYr?.Length), ("BedZ", more.BedZ?.Length), ("DipSin", more.DipSin?.Length),
            ("StreamLossM3PerYr", more.StreamLossM3PerYr?.Length), ("StreamRouteLevel0", more.StreamRouteLevel0?.Length),
        })
        {
            if (len is int l && l != n)
            {
                throw new ArgumentException($"more.{name} 은 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: {l}");
            }
        }
        int kN = levels.Length / n;
        int nx = (int)graph.Shape[1];
        double dx = graph.Spacing;
        double[] pos = graph.Pos;
        int[] nbr = graph.Nbr;
        double[] dist = graph.Dist;
        const int nSlots = CellGraph.NSlots;
        double[] discharge = more.DischargeM3PerYr ?? drainageAreaM2;
        double[]? streamLoss = more.StreamLossM3PerYr;

        // --- 1. 층마다 샘에서 거꾸로 닿는 칸. 아래층은 물 높이가 샘 쪽으로 낮아지는 길만 셉니다 (위층은 옛 물길이라 따지지 않음).
        bool[] reach = Reach(graph, levels, outlet);
        // 개울이 사라지는 칸의 길: 아래층을 StreamRouteLevel0 으로 바꾼 높이(routeLevels)와, 그 높이로 잰 넘침 높이(routeHead, SpillHead).
        // 그린 아래층이 있는 칸에서는 두 높이가 같으므로, 빗물 싱크홀의 길과 통로의 높이는 바뀌지 않습니다.
        double[] routeLevels = levels;
        if (more.StreamRouteLevel0 is double[] route0 && streamLoss is not null)
        {
            routeLevels = (double[])levels.Clone();
            for (int c = 0; c < n; c++)
            {
                if (double.IsFinite(levels[c]))
                {
                    // Caves.RouteLevel0 은 묶음과 같은 정밀도(32비트 실수)로 반올림한 아래층을 돌려줍니다. 메모리에서 바로 만든
                    // 히어로(묶음 파일을 거치지 않음)의 아래층은 64비트라 끝자리가 다를 수 있으므로, 32비트로 맞춰 비교하고
                    // 그린 아래층의 값은 그대로 둡니다.
                    if ((float)levels[c] != (float)route0[c])
                    {
                        throw new ArgumentException(
                            $"more.StreamRouteLevel0 은 아래층이 있는 칸에서 아래층 높이와 같아야 합니다. 칸 {c}: 아래층 {levels[c]}, 받은 값 {route0[c]}");
                    }
                    continue;
                }
                routeLevels[c] = route0[c];
            }
        }
        double[]? routeHead = streamLoss is null ? null : SpillHead(graph, routeLevels[..n], outlet[..n]);

        // --- 2. 싱크홀: 석회암 지표에서 빗물이 모이고, 아래에 샘까지 길이 닿는 동굴 층이 있는 칸.
        //        개울이 사라지는 칸은 솎지 않고 모두 둡니다. 그 둘레 sink_spacing_m 안의 다른 후보는 뺍니다.
        //        개울이 사라지는 칸은 히어로 단계가 같은 판단으로 골랐으므로, 샘까지 닿지 않는 칸(streamWithoutCave)은 없어야 합니다.
        var forced = new List<int>();
        var candidates = new List<int>();
        int streamWithoutCave = 0;
        for (int c = 0; c < n; c++)
        {
            if (streamLoss is not null && streamLoss[c] > 0.0)
            {
                if (FeedsAnyLevel(c))
                {
                    forced.Add(c);
                }
                else
                {
                    streamWithoutCave++;
                }
            }
            else if (sinkAllowed[c] && drainageAreaM2[c] >= p.SinkMinAreaM2 && FeedsAnyLevel(c))
            {
                candidates.Add(c);
            }
        }
        if (forced.Count > 0 && p.SinkSpacingM > 0.0)
        {
            candidates = FarFrom(candidates, forced, pos, p.SinkSpacingM);
        }
        var sinkList = new List<int>(forced);
        sinkList.AddRange(Thin(candidates, pos, p.SinkSpacingM, p.Seed, StreamSink));
        sinkList.Sort();
        int[] sinks = sinkList.ToArray();
        bool[] isSink = new bool[n];
        int[] sinkIndex = new int[n];
        Array.Fill(sinkIndex, -1);
        for (int i = 0; i < sinks.Length; i++)
        {
            isSink[sinks[i]] = true;
            sinkIndex[sinks[i]] = i;
        }

        // --- 3. 싱크홀마다 들어가는 물. 지표 물길을 따라 내려가 처음 만나는 아래쪽 싱크홀에서, 위쪽이 이미 삼킨 물을 뺍니다.
        double[] sinkQ = new double[sinks.Length];
        bool[] sinkStream = new bool[sinks.Length];
        for (int i = 0; i < sinks.Length; i++)
        {
            int s = sinks[i];
            sinkStream[i] = streamLoss is not null && streamLoss[s] > 0.0;
            sinkQ[i] = sinkStream[i] ? streamLoss![s] : Math.Max(discharge[s], 0.0);
        }
        if (more.Receiver is long[] surfaceRcv)
        {
            double[] taken = new double[sinks.Length];
            for (int i = 0; i < sinks.Length; i++)
            {
                if (sinkStream[i])
                {
                    continue;
                }
                int c = sinks[i];
                for (int step = 0; step < n; step++)
                {
                    int r = (int)surfaceRcv[c];
                    if (r == c)
                    {
                        break;
                    }
                    c = r;
                    if (isSink[c])
                    {
                        if (!sinkStream[sinkIndex[c]])
                        {
                            taken[sinkIndex[c]] += Math.Max(discharge[sinks[i]], 0.0);
                        }
                        break;
                    }
                }
            }
            for (int i = 0; i < sinks.Length; i++)
            {
                if (!sinkStream[i])
                {
                    sinkQ[i] = Math.Max(sinkQ[i] - taken[i], 0.0);
                }
            }
        }

        // --- 4. 물이 많은 싱크홀부터, 샘이나 이미 뚫린 통로까지 가장 힘이 덜 드는 길.
        int[] bySize = new int[sinks.Length];
        for (int i = 0; i < bySize.Length; i++)
        {
            bySize[i] = i;
        }
        Array.Sort(bySize, (a, b) => sinkQ[a] != sinkQ[b] ? sinkQ[b].CompareTo(sinkQ[a]) : sinks[a].CompareTo(sinks[b]));
        double[] pts = new double[n * 3];
        for (int c = 0; c < n; c++)
        {
            pts[c * 3] = pos[c * 3];
            pts[(c * 3) + 1] = pos[(c * 3) + 1];
        }
        int[] parent = new int[kN * n];
        Array.Fill(parent, -1);
        bool[] conduit = new bool[kN * n];
        double[] flow = new double[kN * n];
        double[] cost = new double[n];
        int[] stamp = new int[n];
        int[] pred = new int[n];
        int search = 0;
        var pq = new PriorityQueue<int, (double Cost, int Cell)>();
        for (int k = 0; k < kN; k++)
        {
            int off = k * n;
            long pathSeed = (long)(Hashing.Hash3(p.Seed, StreamPath, k) >> 1);
            double[] noise = Noise.Fbm3(pts, pathSeed, PathOctaves, frequency: 1.0 / p.PathWavelengthM);
            double[] resist = new double[n];
            for (int c = 0; c < n; c++)
            {
                resist[c] = Resistance(noise[c]);
            }
            bool downhillOnly = k == 0;
            foreach (int i in bySize)
            {
                int s = sinks[i];
                if (!Feeds(s, k))
                {
                    continue;
                }
                // 싱크홀의 길은 그린 층에서 물 높이가 낮아지는 쪽으로만 갑니다. 개울이 사라지는 싱크홀에 그런 길이 없으면(아래층에서
                // reach 가 거짓), 굽지 않는 깊은 석회암도 지나고(StreamRouteLevel0) 물 높이가 그 싱크홀의 넘침 높이를 넘지 않는 칸이면
                // 오르내리는 길을 찾습니다(SpillHead). 그린 층 안의 길을 먼저 써야 회랑에 보이는 통로가 많습니다.
                bool streamPath = k == 0 && sinkStream[i] && !reach[off + s];
                double[] lvl = streamPath ? routeLevels : levels;
                double headLimit = streamPath ? routeHead![s] + LevelEps : double.PositiveInfinity;
                search++;
                pq.Clear();
                cost[s] = 0.0;
                stamp[s] = search;
                pred[s] = -1;
                pq.Enqueue(s, (0.0, s));
                int found = -1;
                while (pq.TryDequeue(out int v, out (double Cost, int Cell) pr))
                {
                    if (pr.Cost > cost[v])
                    {
                        continue;
                    }
                    if (outlet[off + v] || conduit[off + v])
                    {
                        found = v;
                        break;
                    }
                    double lv = lvl[off + v];
                    for (int slot = 0; slot < nSlots; slot++)
                    {
                        int u = nbr[(v * nSlots) + slot];
                        if (streamPath)
                        {
                            if (u < 0 || !(lvl[off + u] <= headLimit))
                            {
                                continue;
                            }
                        }
                        else
                        {
                            // 싱크홀에서 샘 쪽으로 가는 길이라, 아래층에서는 물 높이가 높아지는 칸으로 가지 않습니다.
                            if (u < 0 || !reach[off + u] || (downhillOnly && levels[off + u] > lv + LevelEps))
                            {
                                continue;
                            }
                        }
                        double next = pr.Cost + (dist[(v * nSlots) + slot] * 0.5 * (resist[v] + resist[u]));
                        if (stamp[u] != search || next < cost[u] || (next == cost[u] && v < pred[u]))
                        {
                            stamp[u] = search;
                            cost[u] = next;
                            pred[u] = v;
                            pq.Enqueue(u, (next, u));
                        }
                    }
                }
                if (found < 0)
                {
                    continue;
                }
                // 찾은 칸에서 싱크홀까지 되짚으며 물이 흘러갈 다음 칸을 적습니다.
                int down = found;
                int up = pred[found];
                while (up >= 0)
                {
                    parent[off + up] = down;
                    conduit[off + up] = true;
                    down = up;
                    up = pred[up];
                }
                conduit[off + s] = true;
                // 이 싱크홀의 물을 샘까지 더합니다.
                int cWalk = s;
                for (int step = 0; cWalk >= 0 && step <= n; step++)
                {
                    flow[off + cWalk] += sinkQ[i];
                    cWalk = parent[off + cWalk];
                }
            }
        }

        // --- 5. 통로가 지나는 자리: 칸의 대표점(흔들어 둔 점)을 잇고, 꺾임을 두 번 다듬습니다. 샘 칸은 칸 한가운데에 둡니다.
        double[] xy = new double[kN * n * 2];
        for (int k = 0; k < kN; k++)
        {
            int off = k * n;
            for (int c = 0; c < n; c++)
            {
                bool atOutlet = outlet[off + c];
                xy[(off + c) * 2] = atOutlet ? graph.Origin.East + (((c % nx) + 0.5) * dx) : pos[c * 3];
                xy[((off + c) * 2) + 1] = atOutlet ? graph.Origin.North - (((c / nx) + 0.5) * dx) : pos[(c * 3) + 1];
            }
            for (int pass = 0; pass < SmoothPasses; pass++)
            {
                double[] sumX = new double[n];
                double[] sumY = new double[n];
                int[] count = new int[n];
                for (int c = 0; c < n; c++)
                {
                    int q = parent[off + c];
                    if (flow[off + c] > 0.0 && q >= 0)
                    {
                        sumX[q] += xy[(off + c) * 2];
                        sumY[q] += xy[((off + c) * 2) + 1];
                        count[q]++;
                    }
                }
                double[] next = new double[n * 2];
                for (int c = 0; c < n; c++)
                {
                    double x = xy[(off + c) * 2];
                    double y = xy[((off + c) * 2) + 1];
                    int q = parent[off + c];
                    // 샘 칸, 싱크홀 칸, 상류 끝은 그대로 둡니다 (입구 굴과 수직굴이 그 자리에 이어집니다).
                    if (flow[off + c] > 0.0 && q >= 0 && count[c] > 0 && !isSink[c])
                    {
                        x = ((2.0 * x) + xy[(off + q) * 2] + (sumX[c] / count[c])) / 4.0;
                        y = ((2.0 * y) + xy[((off + q) * 2) + 1] + (sumY[c] / count[c])) / 4.0;
                    }
                    next[c * 2] = x;
                    next[(c * 2) + 1] = y;
                }
                Array.Copy(next, 0, xy, off * 2, n * 2);
            }
        }

        // --- 6. 통로 굵기와 가운데 선의 높이. 높이는 routeLevels 로 읽습니다: 그린 아래층이 있는 칸은 아래층과 같고, 개울 싱크홀의
        //        길이 지나는 깊은 칸(아래층이 없음)은 그 아래 지하수면입니다.
        double[] radius = new double[kN * n];
        double[] axis = new double[kN * n];
        Array.Fill(axis, double.NaN);
        int nSprings = 0;
        for (int k = 0; k < kN; k++)
        {
            int off = k * n;
            for (int c = 0; c < n; c++)
            {
                if (!(flow[off + c] > 0.0))
                {
                    continue;
                }
                radius[off + c] = Radius(flow[off + c], more.UpliftMPerYr is null ? 0.0 : more.UpliftMPerYr[c], p);
                axis[off + c] = routeLevels[off + c] + (k == 0 ? StreamLift * radius[off + c] : 0.0);
                nSprings += parent[off + c] < 0 ? 1 : 0;
            }
        }
        // 아래층: 층리면을 따라 물 높이 아래로 오르내립니다. 물길 길이 L = 그 칸을 지나는 가장 긴 물길 (가장 먼 싱크홀에서 샘까지).
        double meanLoop = 0.0;
        int nLower = 0;
        if (more.BedZ is not null)
        {
            double[] toSpring = new double[n];
            double[] fromSink = new double[n];
            var cells = new List<int>();
            for (int c = 0; c < n; c++)
            {
                if (flow[c] > 0.0)
                {
                    cells.Add(c);
                }
            }
            // 샘까지의 길이: 샘 칸은 0. 하류 칸의 값이 먼저 있어야 하므로 필요한 칸만 따라 내려가며 채웁니다.
            bool[] done = new bool[n];
            var chain = new List<int>();
            foreach (int c0 in cells)
            {
                chain.Clear();
                int c = c0;
                while (c >= 0 && !done[c])
                {
                    chain.Add(c);
                    c = parent[c];
                }
                for (int t = chain.Count - 1; t >= 0; t--)
                {
                    int a = chain[t];
                    int q = parent[a];
                    toSpring[a] = q < 0 ? 0.0 : toSpring[q] + Step(a, q);
                    done[a] = true;
                }
            }
            // 가장 먼 싱크홀까지의 길이: 샘에서 먼 칸부터 하류로 넘깁니다.
            cells.Sort((a, b) => toSpring[a] != toSpring[b] ? toSpring[b].CompareTo(toSpring[a]) : a.CompareTo(b));
            foreach (int c in cells)
            {
                int q = parent[c];
                if (q >= 0)
                {
                    fromSink[q] = Math.Max(fromSink[q], fromSink[c] + Step(c, q));
                }
            }
            // 층리면을 따라 물 높이 아래로 내려간 깊이 (샘 쪽을 자르기 전). 하류 칸의 깊이에 이어 붙이므로(SawtoothDepth), 샘까지의
            // 길이처럼 하류 칸부터 채웁니다. 폭은 칸마다 다를 수 있지만, 같은 면 위의 두 칸 사이에서는 통로가 면을 그대로 따라갑니다.
            double[] span = new double[n];
            double[] saw = new double[n];
            foreach (int c in cells)
            {
                span[c] = LoopDepth(toSpring[c] + fromSink[c], more.DipSin is null ? 0.0 : more.DipSin[c]);
            }
            Array.Clear(done);
            foreach (int c0 in cells)
            {
                chain.Clear();
                int c = c0;
                while (c >= 0 && !done[c])
                {
                    chain.Add(c);
                    c = parent[c];
                }
                for (int t = chain.Count - 1; t >= 0; t--)
                {
                    int a = chain[t];
                    int q = parent[a];
                    // 개울 싱크홀의 길이 지나는 깊은 칸(아래층을 그리지 않은 칸)은 층리면이 없어(BedZ 가 NaN) 깊이가 0 입니다.
                    double above = routeLevels[a] - more.BedZ[a];
                    double aboveDown = q >= 0 ? routeLevels[q] - more.BedZ[q] : double.NaN;
                    saw[a] = SawtoothDepth(span[a], above, aboveDown, q >= 0 ? saw[q] : double.NaN);
                    done[a] = true;
                }
            }
            // 층리면은 녹는 암석 아래에도 이어지지만 동굴은 녹는 암석 안에만 생깁니다. 그래서 통로 바닥(가운데 선 − 반지름)이 녹는
            // 암석의 바닥 아래로 가지 않게 깊이를 줄입니다. 바닥 높이는 꺾임을 다듬은 뒤 통로가 실제로 지나는 자리(xy)에서 묻습니다.
            double[]? floor = more.SolubleFloorAt?.Invoke(
                cells.ConvertAll(c => xy[c * 2]).ToArray(), cells.ConvertAll(c => xy[(c * 2) + 1]).ToArray(),
                cells.ConvertAll(c => routeLevels[c]).ToArray());
            for (int t = 0; t < cells.Count; t++)
            {
                int c = cells[t];
                // 샘에서는 물이 지표로 나와야 하므로, 샘에 가까울수록 얕게 둡니다 (샘까지 거리 × SpringRiseSlope 보다 깊지 않게).
                double drop = Math.Min(saw[c], SpringRiseSlope * toSpring[c]);
                if (floor is not null && !double.IsNaN(floor[t]))
                {
                    drop = Math.Min(drop, Math.Max(axis[c] - radius[c] - floor[t], 0.0));
                }
                axis[c] -= drop;
                meanLoop += drop;
                nLower++;
            }

            double Step(int a, int q)
            {
                double ex = xy[q * 2] - xy[a * 2];
                double ey = xy[(q * 2) + 1] - xy[(a * 2) + 1];
                return Math.Sqrt((ex * ex) + (ey * ey));
            }
        }

        // --- 7. 통로 조각과 수직굴
        var segA = new List<double>();
        var segB = new List<double>();
        var segR = new List<double>();
        var segShaft = new List<bool>();
        double[] passageM = new double[n];
        double floodedM = 0.0;
        double lowerM = 0.0;
        for (int k = 0; k < kN; k++)
        {
            int off = k * n;
            for (int c = 0; c < n; c++)
            {
                int q = parent[off + c];
                if (!(flow[off + c] > 0.0) || q < 0)
                {
                    continue;
                }
                double r = radius[off + c];
                double ax = xy[(off + c) * 2];
                double ay = xy[((off + c) * 2) + 1];
                double az = axis[off + c];
                double bx = xy[(off + q) * 2];
                double by = xy[((off + q) * 2) + 1];
                double bz = axis[off + q];
                segA.AddRange([ax, ay, az]);
                segB.AddRange([bx, by, bz]);
                segR.Add(r);
                segShaft.Add(false);
                double len = Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)) + ((bz - az) * (bz - az)));
                passageM[c] += len;
                if (k == 0)
                {
                    lowerM += len;
                    // 천장(가운데 선 + 반지름)이 양 끝 모두 물 높이 아래면 그 조각은 물에 다 잠겼습니다.
                    if (az + r <= routeLevels[c] && bz + r <= routeLevels[q])
                    {
                        floodedM += len;
                    }
                }
            }
        }
        // 수직굴은 칸의 노드(흔든 자리, pos)에 섭니다. 그 자리의 지표는 칸 중심 높이 z 와 다를 수 있어서(노드는 칸 중심에서
        // 반 칸까지 떨어짐), 꼭대기를 z 로 잡으면 지표에 닿지 못하고 땅속에서 끝나는 수직굴이 생깁니다. 그래서 노드 자리의
        // 지표 높이(GroundAtNodes)와 z 가운데 높은 쪽에서 시작합니다. 굽기의 프랙탈 디테일이 지표를 더 올릴 수 있어
        // ShaftAboveGroundM 만큼 더 올립니다. 지표 위로 나온 부분은 바위가 없어 아무것도 그리지 않습니다.
        var shaftSinks = new List<int>();
        var shaftBottom = new List<double>();
        for (int i = 0; i < sinks.Length; i++)
        {
            int s = sinks[i];
            double bottom = double.PositiveInfinity;
            for (int k = 0; k < kN; k++)
            {
                if (flow[(k * n) + s] > 0.0)
                {
                    bottom = Math.Min(bottom, axis[(k * n) + s]);
                }
            }
            if (double.IsFinite(bottom))
            {
                shaftSinks.Add(i);
                shaftBottom.Add(bottom);
            }
        }
        double[]? ground = more.GroundAtNodes?.Invoke(shaftSinks.ConvertAll(i => sinks[i]).ToArray());
        for (int q = 0; q < shaftSinks.Count; q++)
        {
            int i = shaftSinks[q];
            int s = sinks[i];
            double r = Radius(sinkQ[i], more.UpliftMPerYr is null ? 0.0 : more.UpliftMPerYr[s], p);
            double top = ground is null || !double.IsFinite(ground[q]) ? z[s] : Math.Max(z[s], ground[q]) + ShaftAboveGroundM;
            segA.AddRange([pos[s * 3], pos[(s * 3) + 1], top + r]);
            segB.AddRange([pos[s * 3], pos[(s * 3) + 1], shaftBottom[q]]);
            segR.Add(r);
            segShaft.Add(true);
        }

        // --- 8. 동굴 호수 (아래층만): 물이 LakeMinWaterM 넘게 깊고 천장 아래 빈 높이가 LakeMinAirM 넘게 남은 통로 칸을 물길을 따라
        //        이어 묶습니다. 묶음마다 물이 가장 깊은 칸을 그 호수의 자리로 적습니다.
        var lakeCells = new List<int>();
        var lakeXyz = new List<double>();
        bool[] lakeLike = new bool[n];
        for (int c = 0; c < n; c++)
        {
            if (!(flow[c] > 0.0) || outlet[c])
            {
                continue;
            }
            double water = routeLevels[c] - (axis[c] - radius[c]);
            double air = (axis[c] + radius[c]) - routeLevels[c];
            lakeLike[c] = water >= LakeMinWaterM && air >= LakeMinAirM;
        }
        bool[] counted = new bool[n];
        for (int c0 = 0; c0 < n; c0++)
        {
            // 묶음의 맨 아래(하류) 칸에서 시작해 상류로 넓힙니다. 하류 칸이 호수 칸이면 그쪽에서 셉니다.
            if (!lakeLike[c0] || counted[c0] || (parent[c0] >= 0 && lakeLike[parent[c0]]))
            {
                continue;
            }
            int best = c0;
            double bestWater = double.NegativeInfinity;
            var stackL = new Stack<int>();
            stackL.Push(c0);
            counted[c0] = true;
            while (stackL.Count > 0)
            {
                int c = stackL.Pop();
                double water = routeLevels[c] - (axis[c] - radius[c]);
                if (water > bestWater || (water == bestWater && c < best))
                {
                    bestWater = water;
                    best = c;
                }
                for (int s = 0; s < nSlots; s++)
                {
                    int u = nbr[(c * nSlots) + s];
                    if (u >= 0 && lakeLike[u] && !counted[u] && parent[u] == c)
                    {
                        counted[u] = true;
                        stackL.Push(u);
                    }
                }
            }
            lakeCells.Add(best);
        }
        lakeCells.Sort();
        foreach (int c in lakeCells)
        {
            lakeXyz.AddRange([xy[c * 2], xy[(c * 2) + 1], routeLevels[c]]);
        }
        CaveShape shape = MeasureShape(n, kN, parent, flow, xy);
        return new CaveNetwork
        {
            NLevels = kN,
            Shape = shape,
            SegA = segA.ToArray(),
            SegB = segB.ToArray(),
            SegRadius = segR.ToArray(),
            SegIsShaft = segShaft.ToArray(),
            Sinks = sinks,
            SinkDischarge = sinkQ,
            SinkIsStream = sinkStream,
            NStreamSinksWithoutCave = streamWithoutCave,
            Lakes = lakeCells.ToArray(),
            LakeXyz = lakeXyz.ToArray(),
            Flow = flow,
            Parent = parent,
            CellRadius = radius,
            AxisZ = axis,
            PassageM = passageM,
            FloodedM = floodedM,
            LowerLevelM = lowerM,
            MeanLoopDepthM = nLower > 0 ? meanLoop / nLower : 0.0,
            NSprings = nSprings,
        };

        // 칸 c 의 물이 층 k 로 들어갈 수 있는지: 그 층이 있고, 샘까지 길이 닿고, 샘 칸이 아니고, 지표에서 충분히 깊을 때.
        // 개울이 사라지는 칸의 아래층은 '그 층이 있고 샘까지 길이 있는지(넘침 높이가 있는지)'만 봅니다. 히어로 단계가 같은 판단(KarstStreams.DrainsToSpring)으로
        // 개울을 사라지게 했고, 깊이(SinkMinDepthM)는 히어로 칸 높이로 이미 재었기 때문입니다. 굽기의 지표(칸 중심 높이)로 다시 재면
        // 판단이 갈릴 수 있습니다. 그 칸이 샘이면 물은 수직굴로 내려가 그 자리에서 나옵니다.
        bool Feeds(int c, int k)
        {
            int i = (k * n) + c;
            if (k == 0 && streamLoss is not null && streamLoss[c] > 0.0)
            {
                return double.IsFinite(levels[i]) && double.IsFinite(routeHead![c]);
            }
            return double.IsFinite(levels[i]) && reach[i] && !outlet[i] && z[c] - levels[i] >= SinkMinDepthM;
        }

        bool FeedsAnyLevel(int c)
        {
            for (int k = 0; k < kN; k++)
            {
                if (Feeds(c, k))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 그물의 모양을 잽니다 (measure_shape). 통로의 끝(싱크홀 쪽 끝과 샘)과 통로가 합쳐지는 자리를 '마디', 마디와 마디 사이를
    /// '가지'로 봅니다 (그냥 지나가는 칸은 마디로 세지 않음. Collon 외 2017 이 동굴 지도 34개를 잰 방식).
    /// 샘 하나로 모이는 통로들을 동굴 하나로 봅니다. 마디마다 이어진 가지 수의 평균은 통로가 한 번이라도 합쳐지는 동굴에서만
    /// 냅니다: 싱크홀 하나에서 샘까지 외길인 동굴은 마디 둘에 가지 하나라 늘 1 이기 때문입니다. 가지 길이의 흩어진 정도
    /// (표준편차 ÷ 평균)와 가지가 굽은 정도(길이 ÷ 양 끝의 직선 거리)는 모든 가지로 냅니다. 층마다 따로 세어 합칩니다.
    /// </summary>
    private static CaveShape MeasureShape(int n, int kN, int[] parent, double[] flow, double[] xy)
    {
        long nNodes = 0;
        long degreeSum = 0;
        long nCaves = 0;
        long nSingle = 0;
        var lengths = new List<double>();
        double tortuositySum = 0.0;
        int nTortuosity = 0;
        int[] children = new int[n];
        int[] root = new int[n];
        long[] rootNodes = new long[n];
        long[] rootDegree = new long[n];
        bool[] rootJoins = new bool[n];
        var chain = new List<int>();
        for (int k = 0; k < kN; k++)
        {
            int off = k * n;
            Array.Clear(children);
            Array.Fill(root, -1);
            Array.Clear(rootNodes);
            Array.Clear(rootDegree);
            Array.Clear(rootJoins);
            for (int c = 0; c < n; c++)
            {
                int q = parent[off + c];
                if (flow[off + c] > 0.0 && q >= 0)
                {
                    children[q]++;
                }
            }
            for (int c0 = 0; c0 < n; c0++)
            {
                if (!(flow[off + c0] > 0.0) || root[c0] >= 0)
                {
                    continue;
                }
                // 샘(통로가 끝나는 칸)까지 내려가 그 칸 번호를 동굴의 이름으로 적습니다.
                chain.Clear();
                int c = c0;
                while (root[c] < 0 && parent[off + c] >= 0)
                {
                    chain.Add(c);
                    c = parent[off + c];
                }
                int name = root[c] >= 0 ? root[c] : c;
                root[c] = name;
                foreach (int a in chain)
                {
                    root[a] = name;
                }
            }
            for (int c = 0; c < n; c++)
            {
                if (!(flow[off + c] > 0.0))
                {
                    continue;
                }
                int q = parent[off + c];
                bool node = children[c] != 1 || q < 0;
                if (!node)
                {
                    continue;
                }
                rootNodes[root[c]]++;
                rootDegree[root[c]] += children[c] + (q >= 0 ? 1 : 0);
                // 합쳐지는 자리: 두 통로가 만나 한 통로로 이어지는 칸 (가지 셋 이상). 샘 칸에서 두 통로가 만나기만 하면
                // 하류로 이어지는 통로가 없어 합쳐진 것으로 세지 않습니다.
                rootJoins[root[c]] |= children[c] + (q >= 0 ? 1 : 0) >= 3;
                if (q < 0)
                {
                    continue;
                }
                // 이 마디에서 하류의 다음 마디까지가 가지 하나입니다.
                double length = 0.0;
                int a = c;
                int b = q;
                while (true)
                {
                    double ex = xy[(off + b) * 2] - xy[(off + a) * 2];
                    double ey = xy[((off + b) * 2) + 1] - xy[((off + a) * 2) + 1];
                    length += Math.Sqrt((ex * ex) + (ey * ey));
                    if (children[b] != 1 || parent[off + b] < 0)
                    {
                        break;
                    }
                    a = b;
                    b = parent[off + b];
                }
                lengths.Add(length);
                double sx = xy[(off + b) * 2] - xy[(off + c) * 2];
                double sy = xy[((off + b) * 2) + 1] - xy[((off + c) * 2) + 1];
                double straight = Math.Sqrt((sx * sx) + (sy * sy));
                if (straight > 0.0)
                {
                    tortuositySum += length / straight;
                    nTortuosity++;
                }
            }
            for (int c = 0; c < n; c++)
            {
                if (rootNodes[c] == 0)
                {
                    continue;
                }
                nCaves++;
                if (rootJoins[c])
                {
                    nNodes += rootNodes[c];
                    degreeSum += rootDegree[c];
                }
                else
                {
                    nSingle++;
                }
            }
        }
        double mean = 0.0;
        foreach (double v in lengths)
        {
            mean += v;
        }
        mean = lengths.Count > 0 ? mean / lengths.Count : 0.0;
        double var = 0.0;
        foreach (double v in lengths)
        {
            var += (v - mean) * (v - mean);
        }
        double cv = lengths.Count > 1 && mean > 0.0 ? Math.Sqrt(var / lengths.Count) / mean : 0.0;
        return new CaveShape(
            nCaves, nSingle, lengths.Count, nNodes > 0 ? (double)degreeSum / nNodes : 0.0, cv,
            nTortuosity > 0 ? tortuositySum / nTortuosity : 0.0);
    }

    /// <summary>
    /// 싱크홀 후보를 솎습니다: spacing [m] 안에 우선순위가 더 높은 후보가 있으면 뺍니다. 우선순위는 시드와 칸 번호의 해시입니다.
    /// 남은 칸끼리는 spacing 넘게 떨어집니다. 반환: 남은 칸 번호 (오름차순).
    /// </summary>
    internal static int[] ThinSinks(List<int> candidates, double[] pos, double spacing, long seed) =>
        Thin(candidates, pos, spacing, seed, StreamSink);

    /// <summary>candidates 가운데 anchors 의 어느 칸에서도 spacing [m] 넘게 떨어진 칸만 남깁니다.</summary>
    internal static List<int> FarFrom(List<int> candidates, List<int> anchors, double[] pos, double spacing)
    {
        var bucket = new Dictionary<(long, long), List<int>>();
        (long, long) Key(int c) => ((long)Math.Floor(pos[c * 3] / spacing), (long)Math.Floor(pos[(c * 3) + 1] / spacing));
        foreach (int a in anchors)
        {
            (long, long) key = Key(a);
            if (!bucket.TryGetValue(key, out List<int>? list))
            {
                list = [];
                bucket[key] = list;
            }
            list.Add(a);
        }
        double s2 = spacing * spacing;
        var keep = new List<int>();
        foreach (int c in candidates)
        {
            (long bx, long by) = Key(c);
            bool near = false;
            for (long ix = -1; ix <= 1 && !near; ix++)
            {
                for (long iy = -1; iy <= 1 && !near; iy++)
                {
                    if (!bucket.TryGetValue((bx + ix, by + iy), out List<int>? list))
                    {
                        continue;
                    }
                    foreach (int a in list)
                    {
                        double ex = pos[a * 3] - pos[c * 3];
                        double ey = pos[(a * 3) + 1] - pos[(c * 3) + 1];
                        if ((ex * ex) + (ey * ey) <= s2)
                        {
                            near = true;
                            break;
                        }
                    }
                }
            }
            if (!near)
            {
                keep.Add(c);
            }
        }
        return keep;
    }

    /// <summary>
    /// 후보 칸을 솎습니다. stream 은 우선순위 해시의 용도 번호라, 용도가 다르면 다른 칸이 남습니다.
    /// </summary>
    internal static int[] Thin(List<int> candidates, double[] pos, double spacing, long seed, long stream)
    {
        if (spacing <= 0.0 || candidates.Count == 0)
        {
            return candidates.ToArray();
        }
        var bucket = new Dictionary<(long, long), List<int>>();
        (long, long) Key(int c) => ((long)Math.Floor(pos[c * 3] / spacing), (long)Math.Floor(pos[(c * 3) + 1] / spacing));
        foreach (int c in candidates)
        {
            (long, long) key = Key(c);
            if (!bucket.TryGetValue(key, out List<int>? list))
            {
                list = [];
                bucket[key] = list;
            }
            list.Add(c);
        }
        ulong Priority(int c) => Hashing.Hash3(seed, stream, c);
        double s2 = spacing * spacing;
        var keep = new List<int>();
        foreach (int c in candidates)
        {
            ulong pc = Priority(c);
            (long bx, long by) = Key(c);
            bool dropped = false;
            for (long ix = -1; ix <= 1 && !dropped; ix++)
            {
                for (long iy = -1; iy <= 1 && !dropped; iy++)
                {
                    if (!bucket.TryGetValue((bx + ix, by + iy), out List<int>? list))
                    {
                        continue;
                    }
                    foreach (int v in list)
                    {
                        if (v == c)
                        {
                            continue;
                        }
                        double ex = pos[v * 3] - pos[c * 3];
                        double ey = pos[(v * 3) + 1] - pos[(c * 3) + 1];
                        if ((ex * ex) + (ey * ey) > s2)
                        {
                            continue;
                        }
                        ulong pv = Priority(v);
                        if (pv > pc || (pv == pc && v < c))
                        {
                            dropped = true;
                            break;
                        }
                    }
                }
            }
            if (!dropped)
            {
                keep.Add(c);
            }
        }
        return keep.ToArray();
    }
}
