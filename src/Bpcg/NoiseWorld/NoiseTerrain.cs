using System;
using Bpcg.Core;
using Bpcg.Geology;

namespace Bpcg.NoiseWorld;

/// <summary>지표 한 점의 노이즈 값: 높이와, 그 높이를 만든 중간 값.</summary>
/// <param name="HeightM">해발 높이 [m] (강 골짜기와 물길을 판 뒤).</param>
/// <param name="Valley">강 골짜기 안쪽 정도 (0 = 골짜기 밖, 1 = 골짜기 바닥).</param>
/// <param name="RiverDistM">강 노이즈 값을 거리로 바꾼 값: 강 가운데 줄까지의 대략 거리 [m].</param>
/// <param name="RiverWaterM">물길 안이면 강 수면 높이 [m], 아니면 NaN.</param>
/// <param name="Mountain">산지 정도 (0 = 평지, 1 = 산지).</param>
/// <param name="Continent">대륙 노이즈 값 (대략 −1~1, 클수록 대륙 안쪽).</param>
public readonly record struct SurfacePoint(
    double HeightM, double Valley, double RiverDistM, double RiverWaterM, double Mountain, double Continent);

/// <summary>한 지표 칸 아래 땅속 암석을 정하는 값 (칸마다 한 번 셈).</summary>
/// <param name="Province">암석 구역 번호 (세포 노이즈의 칸 해시).</param>
/// <param name="Kind">구역의 암석 무리: 0 퇴적암, 1 변성암, 2 화성암.</param>
/// <param name="ShiftM">띠 높이를 옮기는 값 [m]: 띠를 휘게 하는 노이즈 + 구역마다 기운 면.</param>
public readonly record struct RockColumn(long Province, int Kind, double ShiftM);

/// <summary>
/// 노이즈 비교본의 세계 함수. 오늘날 게임이 지형을 만드는 흔한 방법(여러 노이즈를 겹쳐 높이·물·동굴·암석을 따로 정하는 방법)을
/// 행성 위 위치의 함수 하나로 씁니다. 판, 침식, 물의 흐름 같은 물리 계산은 하지 않습니다.
/// </summary>
/// <remarks>
/// <para>
/// 모든 값은 행성 위 3D 위치 [m]의 함수라서, 행성 지도·히어로 유역·회랑이 같은 함수를 다른 간격으로 읽습니다(<see cref="Octaves"/>).
/// 무작위 값은 <see cref="Hashing"/> 의 정수 해시로만 만들어, 같은 시드면 같은 세계가 나옵니다.
/// </para>
/// <para>
/// 높이는 다섯 겹입니다: 대륙 노이즈를 꺾은선으로 바꾼 기본 땅, 산지 노이즈가 큰 곳에만 더하는 ridged 산줄기, 어디나 더하는
/// 언덕, 가까이서 보이는 잔 거칠기, 강 노이즈가 0 에 가까운 띠를 낮춘 강 골짜기. 강 골짜기는 둘레 땅보다 정해 둔 깊이까지만
/// 낮추므로 골짜기 바닥과 수면이 산을 따라 오르내립니다. 물이 늘 낮은 쪽으로 흐른다는 보장이 없는 것이 노이즈 강의 한계이고,
/// 이 비교본이 보여 주려는 차이입니다.
/// </para>
/// </remarks>
public sealed class NoiseTerrain
{
    private const long StreamWarp = 9_101;
    private const long StreamContinent = 9_111;
    private const long StreamErosion = 9_121;
    private const long StreamRidge = 9_131;
    private const long StreamHills = 9_141;
    private const long StreamRough = 9_151;
    private const long StreamRiver = 9_161;
    private const long StreamAquifer = 9_171;
    private const long StreamTemperature = 9_181;
    private const long StreamPrecip = 9_191;
    private const long StreamCaveRegion = 9_201;
    private const long StreamTunnelA = 9_211;
    private const long StreamTunnelB = 9_221;
    private const long StreamTunnelRadius = 9_231;
    private const long StreamChamber = 9_241;
    private const long StreamStrataWarp = 9_251;
    private const long StreamProvince = 9_261;
    private const long StreamBand = 9_271;
    private const long StreamDip = 9_281;
    private const long StreamGradient = 9_291;

    /// <summary>모든 물결을 쓰라는 표본 간격 (3D 동굴 노이즈처럼 간격과 상관없이 읽는 값).</summary>
    public const double AllOctaves = 1e-9;

    // 대륙 노이즈 값 → 기본 땅 높이 [m] 꺾은선. 0 근처가 해안이고, 해수면은 나중에 바다 몫으로 다시 맞춥니다.
    private static readonly double[] BaseC = [-1.0, -0.45, -0.2, -0.08, -0.03, 0.0, 0.08, 0.25, 0.5, 1.0];
    private static readonly double[] BaseH = [-5_800.0, -5_000.0, -3_800.0, -900.0, -150.0, 5.0, 90.0, 300.0, 650.0, 1_400.0];

    // 암석 무리마다 띠에 고를 암석 목록.
    private static readonly byte[][] Palettes =
    [
        [Rocks.Sandstone, Rocks.Shale, Rocks.Limestone, Rocks.Sandstone, Rocks.Limestone, Rocks.Shale],
        [Rocks.Slate, Rocks.Schist, Rocks.Gneiss, Rocks.Marble, Rocks.Quartzite, Rocks.Schist],
        [Rocks.Granite, Rocks.Granite, Rocks.Volcanic, Rocks.Granite],
    ];

    private readonly long _seed;
    private readonly Octaves _warpX;
    private readonly Octaves _warpY;
    private readonly Octaves _warpZ;
    private readonly Octaves _continent;
    private readonly Octaves _erosion;
    private readonly Octaves _ridge;
    private readonly Octaves _hills;
    private readonly Octaves _rough;
    private readonly Octaves _river;
    private readonly Octaves _aquifer;
    private readonly Octaves _temperature;
    private readonly Octaves _precip;
    private readonly Octaves _caveRegion;
    private readonly Octaves _tunnelA;
    private readonly Octaves _tunnelB;
    private readonly Octaves _tunnelRadius;
    private readonly Octaves _chamber;
    private readonly Octaves _strataWarp;

    // 노이즈 값 1 이 몇 m 에 해당하는지 (첫 파장 ÷ 기울기 크기).
    private readonly double _riverM;
    private readonly double _regionM;
    private readonly double _tunnelAM;
    private readonly double _tunnelBM;
    private readonly double _chamberM;

    private readonly double _warpM;
    private readonly double _mountainM;
    private readonly double _hillsM;
    private readonly double _roughM;
    private readonly double _valleyHalfWidthM;
    private readonly double _valleyDepthM;
    private readonly double _channelHalfWidthM;
    private readonly double _channelDepthM;
    private readonly double _aquiferMinM;
    private readonly double _aquiferRangeM;
    private readonly double _regionThreshold;
    private readonly double _tunnelRadiusM;
    private readonly double _tunnelRangeM;
    private readonly double _chamberThreshold;
    private readonly double _chamberMinDepthM;
    private readonly double _verticalSquash;
    private readonly double _provinceM;
    private readonly double _bandM;
    private readonly double _strataWarpM;
    private readonly double _maxDipTan;
    private readonly double _soilMaxM;
    private readonly double _alluviumM;
    private readonly double _tEquatorC;
    private readonly double _tPoleC;
    private readonly double _lapseCPerM;
    private readonly double _tNoiseC;
    private readonly double _precipMax;

    public NoiseTerrain(Config cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _seed = cfg.I("planet.seed");
        RadiusM = cfg.F("planet.radius_m");
        double[] axis = cfg.FArray("planet.axis");
        double na = Math.Sqrt((axis[0] * axis[0]) + (axis[1] * axis[1]) + (axis[2] * axis[2]));
        Axis = [axis[0] / na, axis[1] / na, axis[2] / na];
        Section s = cfg.Sec("noise");

        double warpLambda = Positive(s, "continents.warp_wavelength_m");
        _warpX = new Octaves(_seed, StreamWarp, warpLambda, 3, 0.5);
        _warpY = new Octaves(_seed, StreamWarp + 1, warpLambda, 3, 0.5);
        _warpZ = new Octaves(_seed, StreamWarp + 2, warpLambda, 3, 0.5);
        _warpM = NonNegative(s, "continents.warp_m");
        _continent = new Octaves(_seed, StreamContinent, Positive(s, "continents.wavelength_m"), (int)s.I("continents.octaves"), 0.5);
        _erosion = new Octaves(_seed, StreamErosion, Positive(s, "mountains.erosion_wavelength_m"), 4, 0.5);
        // 물결 수는 회랑 2 m 표본까지 닿도록 넉넉히 둡니다. 쓰지 않는 짧은 물결은 Band 가 0 이라 계산하지 않습니다.
        _ridge = new Octaves(_seed, StreamRidge, Positive(s, "mountains.wavelength_m"), 20, s.F("mountains.gain"));
        _mountainM = NonNegative(s, "mountains.height_m");
        _hills = new Octaves(_seed, StreamHills, Positive(s, "hills.wavelength_m"), 16, s.F("hills.gain"));
        _hillsM = NonNegative(s, "hills.height_m");
        _rough = new Octaves(_seed, StreamRough, Positive(s, "hills.rough_wavelength_m"), 6, 0.6);
        _roughM = NonNegative(s, "hills.rough_height_m");
        // 강 노이즈는 gain 을 작게 두어 짧은 물결이 강 줄기를 잘게 흩뜨리지 않게 합니다.
        _river = new Octaves(_seed, StreamRiver, Positive(s, "rivers.wavelength_m"), 6, 0.35);
        _valleyHalfWidthM = Positive(s, "rivers.valley_half_width_m");
        _valleyDepthM = NonNegative(s, "rivers.valley_depth_m");
        _channelHalfWidthM = Positive(s, "rivers.channel_half_width_m");
        _channelDepthM = NonNegative(s, "rivers.channel_depth_m");
        _aquifer = new Octaves(_seed, StreamAquifer, Positive(s, "aquifer.depth_wavelength_m"), 3, 0.5);
        _aquiferMinM = NonNegative(s, "aquifer.depth_min_m");
        _aquiferRangeM = NonNegative(s, "aquifer.depth_range_m");
        _temperature = new Octaves(_seed, StreamTemperature, Positive(s, "climate.temperature_wavelength_m"), 3, 0.5);
        _precip = new Octaves(_seed, StreamPrecip, Positive(s, "climate.precip_wavelength_m"), 4, 0.5);
        _tNoiseC = NonNegative(s, "climate.temperature_noise_c");
        _precipMax = Positive(s, "climate.precip_max_m_per_yr");
        _tEquatorC = cfg.F("climate.t_equator_c");
        _tPoleC = cfg.F("climate.t_pole_c");
        _lapseCPerM = cfg.F("climate.lapse_rate_c_per_m");

        _caveRegion = new Octaves(_seed, StreamCaveRegion, Positive(s, "caves.region_wavelength_m"), 2, 0.5);
        _regionThreshold = s.F("caves.region_threshold");
        double tunnel = Positive(s, "caves.tunnel_wavelength_m");
        _tunnelA = new Octaves(_seed, StreamTunnelA, tunnel, 2, 0.4);
        _tunnelB = new Octaves(_seed, StreamTunnelB, tunnel, 2, 0.4);
        _tunnelRadius = new Octaves(_seed, StreamTunnelRadius, 60.0, 2, 0.5);
        _tunnelRadiusM = Positive(s, "caves.tunnel_radius_m");
        _tunnelRangeM = NonNegative(s, "caves.tunnel_radius_range_m");
        _chamber = new Octaves(_seed, StreamChamber, Positive(s, "caves.chamber_wavelength_m"), 3, 0.5);
        _chamberThreshold = s.F("caves.chamber_threshold");
        _chamberMinDepthM = NonNegative(s, "caves.chamber_min_depth_m");
        _verticalSquash = Positive(s, "caves.vertical_squash");
        CaveMaxDepthM = Positive(s, "caves.max_depth_m");

        _provinceM = Positive(s, "strata.province_wavelength_m");
        _bandM = Positive(s, "strata.band_thickness_m");
        _strataWarp = new Octaves(_seed, StreamStrataWarp, Positive(s, "strata.warp_wavelength_m"), 3, 0.5);
        _strataWarpM = NonNegative(s, "strata.warp_m");
        _maxDipTan = Math.Tan(Math.Clamp(s.F("strata.max_dip_deg"), 0.0, 60.0) * Math.PI / 180.0);
        _soilMaxM = NonNegative(s, "strata.soil_max_m");
        _alluviumM = NonNegative(s, "strata.alluvium_m");

        _riverM = _river.Lambda[0] / NoiseMath.GradientScale(_river, RadiusM, _seed, StreamGradient);
        _regionM = _caveRegion.Lambda[0] / NoiseMath.GradientScale(_caveRegion, RadiusM, _seed, StreamGradient + 2);
        _tunnelAM = _tunnelA.Lambda[0] / NoiseMath.GradientScale(_tunnelA, RadiusM, _seed, StreamGradient + 4);
        _tunnelBM = _tunnelB.Lambda[0] / NoiseMath.GradientScale(_tunnelB, RadiusM, _seed, StreamGradient + 6);
        _chamberM = _chamber.Lambda[0] / NoiseMath.GradientScale(_chamber, RadiusM, _seed, StreamGradient + 8);
    }

    /// <summary>행성 반지름 [m].</summary>
    public double RadiusM { get; }

    /// <summary>자전축 (단위 벡터). 위도를 재는 기준.</summary>
    public double[] Axis { get; }

    /// <summary>동굴이 생기는 가장 깊은 곳 (지표 아래 m).</summary>
    public double CaveMaxDepthM { get; }

    /// <summary>
    /// 대륙 노이즈에서 빼는 값. 행성 지도 칸에서 대륙 노이즈가 이 값보다 작은 넓이가 바다 몫(noise.sea.ocean_fraction)이 되도록
    /// <see cref="NoisePlanet"/> 이 정합니다. 해안·대륙붕·대륙사면 모양을 그대로 두고 해안선 자리만 옮깁니다.
    /// </summary>
    public double ContinentShift { get; set; }

    /// <summary>
    /// 해수면을 맞추려고 모든 높이에서 빼는 값 [m]. 대륙 노이즈를 옮긴 뒤 남은 작은 차이를 맞춥니다 (<see cref="NoisePlanet"/>).
    /// </summary>
    public double SeaShiftM { get; set; }

    /// <summary>대륙 노이즈 값 (옮기기 전). 행성 지도에서 해안 자리를 정할 때 씁니다.</summary>
    public double ContinentValue(double ux, double uy, double uz, double spacingM)
    {
        double x = ux * RadiusM;
        double y = uy * RadiusM;
        double z = uz * RadiusM;
        double wx = _warpM * _warpX.Fbm(x, y, z, spacingM);
        double wy = _warpM * _warpY.Fbm(x, y, z, spacingM);
        double wz = _warpM * _warpZ.Fbm(x, y, z, spacingM);
        return _continent.Fbm(x + wx, y + wy, z + wz, spacingM);
    }

    private static double Positive(Section s, string key)
    {
        double v = s.F(key);
        return double.IsFinite(v) && v > 0
            ? v
            : throw new ArgumentException($"노이즈 설정 noise.{key} 는 0 보다 커야 합니다 (받은 값 {v}). configs/noise/default.toml 의 값을 보세요");
    }

    private static double NonNegative(Section s, string key)
    {
        double v = s.F(key);
        return double.IsFinite(v) && v >= 0
            ? v
            : throw new ArgumentException($"노이즈 설정 noise.{key} 는 0 이상이어야 합니다 (받은 값 {v}). configs/noise/default.toml 의 값을 보세요");
    }

    /// <summary>해수면을 맞추기 전의 높이 [m] (강 골짜기 없이). 행성 지도에서 해수면을 정할 때 씁니다.</summary>
    public double RawHeight(double ux, double uy, double uz, double spacingM) =>
        Raw(ux * RadiusM, uy * RadiusM, uz * RadiusM, spacingM, out _, out _);

    private double Raw(double x, double y, double z, double spacingM, out double mountain, out double continent)
    {
        double wx = _warpM * _warpX.Fbm(x, y, z, spacingM);
        double wy = _warpM * _warpY.Fbm(x, y, z, spacingM);
        double wz = _warpM * _warpZ.Fbm(x, y, z, spacingM);
        double c = _continent.Fbm(x + wx, y + wy, z + wz, spacingM) - ContinentShift;
        double e = _erosion.Fbm(x, y, z, spacingM);
        double land = NoiseMath.Smoothstep(-0.02, 0.15, c);
        double mount = land * NoiseMath.Smoothstep(0.12, -0.28, e);
        double h = NoiseMath.Piecewise(BaseC, BaseH, c);
        if (mount > 0.0)
        {
            double ridge = _ridge.Ridged(x + (0.1 * wx), y + (0.1 * wy), z + (0.1 * wz), spacingM);
            h += _mountainM * mount * Math.Pow(Math.Max(ridge, 0.0), 1.5);
        }
        double hillsAmp = _hillsM * (0.3 + (0.7 * NoiseMath.Smoothstep(0.35, -0.35, e))) * (0.35 + (0.65 * land));
        h += hillsAmp * _hills.Fbm(x, y, z, spacingM);
        if (_roughM > 0.0 && Octaves.Band(_rough.Lambda[0], spacingM) > 0.0)
        {
            h += _roughM * (0.3 + (0.7 * mount)) * _rough.Fbm(x, y, z, spacingM);
        }
        mountain = mount;
        continent = c;
        return h;
    }

    /// <summary>
    /// 단위 벡터 (ux, uy, uz) 자리 지표의 노이즈 값. spacingM 은 표본 간격 [m]: 그보다 짧은 물결은 빼고, 물길은 간격이 물길 반폭보다
    /// 좁을 때만 팝니다(25 m 히어로 지도에는 폭 20 m 물길이 그려지지 않음).
    /// </summary>
    public SurfacePoint Surface(double ux, double uy, double uz, double spacingM)
    {
        double x = ux * RadiusM;
        double y = uy * RadiusM;
        double z = uz * RadiusM;
        double h = Raw(x, y, z, spacingM, out double mountain, out double continent) - SeaShiftM;
        double r = _river.Fbm(x, y, z, spacingM);
        double dist = Math.Abs(r) * _riverM;
        double valley = 0.0;
        double water = double.NaN;
        if (h > 1.0)
        {
            valley = 1.0 - NoiseMath.Smoothstep(0.0, _valleyHalfWidthM, dist);
            h -= valley * Math.Min(_valleyDepthM, h - 1.0);
            if (spacingM <= _channelHalfWidthM && dist < _channelHalfWidthM)
            {
                double t = dist / _channelHalfWidthM;
                water = h - (0.25 * _channelDepthM);
                h -= _channelDepthM * (1.0 - (t * t));
            }
        }
        return new SurfacePoint(h, valley, dist, water, mountain, continent);
    }

    /// <summary>
    /// 지하수면 높이 [m]: 지표 surfaceM 에서 노이즈로 정한 깊이만큼 아래 (게임에서 흔한 '지표를 따라가는 물 높이'). 해안의 낮은 땅에서는
    /// 해수면(0 m)보다 낮아지지 않고, 바다 밑에서는 해수면입니다. 지표보다 늘 낮으므로 지하수가 고인 호수는 생기지 않습니다.
    /// </summary>
    public double WaterTable(double ux, double uy, double uz, double surfaceM, double spacingM)
    {
        if (surfaceM < 0.0)
        {
            return 0.0;
        }
        double depth = _aquiferMinM + (_aquiferRangeM * (0.5 + (0.5 * _aquifer.Fbm(ux * RadiusM, uy * RadiusM, uz * RadiusM, spacingM))));
        return Math.Max(surfaceM - depth, 0.0);
    }

    /// <summary>
    /// 동굴 빈 곳까지의 대략 거리 [m] (음수면 동굴 안). (px, py, pz) 는 행성 중심 기준 3D 위치 [m], depthM 은 그 점이 지표에서
    /// 얼마나 아래인지 [m]. 지표 위(음수 깊이)에서도 값을 이어서 돌려줘, 메시를 지표에서 자를 때 뚜껑이 생기지 않게 합니다.
    /// </summary>
    /// <remarks>
    /// 굴은 서로 다른 노이즈 둘이 모두 0 에 가까운 줄입니다(두 노이즈의 0 면이 만나는 선). 큰 방은 세 번째 노이즈가 문턱을 넘는
    /// 덩어리입니다. 둘 다 동굴 구역 노이즈가 문턱보다 큰 곳에만 둡니다. 굴과 방은 위아래로 눌러 수평에 가깝게 눕힙니다.
    /// 노이즈 값을 거리로 바꾸는 배수는 기울기 크기의 가운데값이라,
    /// 실제 거리와 다를 수 있습니다. earlyM 보다 멀다고 판단되면 계산을 줄이고 그 근사를 돌려줍니다(메시가 값을 earlyM 에서 자르므로
    /// 결과는 같음).
    /// </remarks>
    public double CaveDistance(double px, double py, double pz, double depthM, double earlyM = double.PositiveInfinity)
    {
        double below = depthM - CaveMaxDepthM;
        if (below >= earlyM)
        {
            return below;
        }
        double region = (_regionThreshold - _caveRegion.Fbm(px, py, pz, AllOctaves)) * _regionM;
        if (region >= earlyM)
        {
            return region;
        }
        // 굴과 방 노이즈는 위아래로 눌러 읽습니다: 높이 차를 vertical_squash 배로 늘려 넣으면 무늬가 그만큼 납작해져,
        // 굴이 수평에 가깝게 눕고 굴의 높이가 폭의 1/vertical_squash 이 됩니다.
        double qx = px;
        double qy = py;
        double qz = pz;
        if (_verticalSquash != 1.0)
        {
            double r = Math.Sqrt((px * px) + (py * py) + (pz * pz));
            double s = (RadiusM + (_verticalSquash * (r - RadiusM))) / r;
            qx *= s;
            qy *= s;
            qz *= s;
        }
        double a = _tunnelA.Fbm(qx, qy, qz, AllOctaves) * _tunnelAM;
        double b = _tunnelB.Fbm(qx, qy, qz, AllOctaves) * _tunnelBM;
        double radius = _tunnelRadiusM + (0.5 * _tunnelRangeM * _tunnelRadius.Fbm(px, py, pz, AllOctaves));
        double d = Math.Sqrt((a * a) + (b * b)) - radius;
        if (depthM > _chamberMinDepthM - earlyM)
        {
            double chamber = (_chamberThreshold - _chamber.Fbm(qx, qy, qz, AllOctaves)) * _chamberM;
            d = Math.Min(d, Math.Max(chamber, _chamberMinDepthM - depthM));
        }
        return Math.Max(Math.Max(d, region), below);
    }

    /// <summary>지표 칸 하나 아래 땅속 암석 값 (단위 벡터 자리).</summary>
    public RockColumn Column(double ux, double uy, double uz)
    {
        double x = ux * RadiusM;
        double y = uy * RadiusM;
        double z = uz * RadiusM;
        long province = NoiseMath.NearestCell(x, y, z, _provinceM, unchecked((long)Hashing.Hash3(_seed, StreamProvince, 0)));
        double k = Hashing.HashUnit(province, _seed, StreamProvince);
        int kind = k < 0.55 ? 0 : k < 0.8 ? 1 : 2;
        // 구역마다 기운 방향(해시로 고른 3D 방향)과 기울기. 지표 위 위치에 따라 띠 높이가 곧게 바뀌어 띠가 기웁니다.
        double dx = (2.0 * Hashing.HashUnit(province, 1, StreamDip)) - 1.0;
        double dy = (2.0 * Hashing.HashUnit(province, 2, StreamDip)) - 1.0;
        double dz = (2.0 * Hashing.HashUnit(province, 3, StreamDip)) - 1.0;
        double dn = Math.Max(Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)), 1e-9);
        double tilt = _maxDipTan * Hashing.HashUnit(province, 4, StreamDip) * ((x * dx) + (y * dy) + (z * dz)) / dn;
        double shift = (_strataWarpM * _strataWarp.Fbm(x, y, z, AllOctaves)) + tilt;
        return new RockColumn(province, kind, shift);
    }

    /// <summary>기반암 띠 번호와 그 띠의 암석 (높이 zM [m] 에서).</summary>
    public (long Band, byte Rock) Bedrock(RockColumn col, double zM)
    {
        long band = (long)Math.Floor((zM + col.ShiftM) / _bandM);
        byte[] palette = Palettes[col.Kind];
        int i = (int)(Hashing.Hash3(col.Province, band, StreamBand) % (ulong)palette.Length);
        return (band, palette[i]);
    }

    /// <summary>
    /// 어두운 층리 띠인지 (재질 부피 보기용, B-PCG 굽기와 같은 15 m 띠·40% 비율). 같은 암석이어도 어두운 띠와 밝은 띠가 번갈아 보입니다.
    /// </summary>
    public bool DarkBed(RockColumn col, double zM, double thicknessM, double fraction) =>
        Hashing.HashUnit(col.Province, (long)Math.Floor((zM + col.ShiftM) / thicknessM), StreamBand + 1) < fraction;

    /// <summary>
    /// 지표 근처 재질: 강 골짜기 바닥과 바닷가는 모래·자갈(충적층), 평평한 땅은 흙, 그 밖은 기반암. slope 는 지표 경사 (높이/거리).
    /// </summary>
    public byte Material(RockColumn col, double zM, double surfaceM, double slope, double valley)
    {
        double depth = surfaceM - zM;
        bool alluvial = valley > 0.85 || (surfaceM < 2.5 && surfaceM > -30.0);
        if (alluvial && depth < _alluviumM)
        {
            return Rocks.Alluvium;
        }
        double soil = surfaceM > 0.0 ? _soilMaxM * (1.0 - NoiseMath.Smoothstep(0.35, 0.9, slope)) : 0.0;
        if (!alluvial && depth < soil)
        {
            return Rocks.Soil;
        }
        return Bedrock(col, zM).Rock;
    }

    /// <summary>위도 [°] (자전축 기준).</summary>
    public double LatitudeDeg(double ux, double uy, double uz) =>
        Math.Asin(Math.Clamp((ux * Axis[0]) + (uy * Axis[1]) + (uz * Axis[2]), -1.0, 1.0)) * 180.0 / Math.PI;

    /// <summary>
    /// 연평균 기온 [°C]과 연강수량 [m/yr]. 기온은 위도와 높이로 정하고 노이즈를 더합니다(B-PCG 행성 설정의 적도·극 기온과 기온
    /// 감률을 씀). 강수는 위도와 상관없는 노이즈로만 정합니다.
    /// </summary>
    public (double TemperatureC, double PrecipMPerYr) Climate(double ux, double uy, double uz, double heightM)
    {
        double x = ux * RadiusM;
        double y = uy * RadiusM;
        double z = uz * RadiusM;
        double sinLat = Math.Abs((ux * Axis[0]) + (uy * Axis[1]) + (uz * Axis[2]));
        double t = _tEquatorC + ((_tPoleC - _tEquatorC) * Math.Pow(sinLat, 1.25)) - (_lapseCPerM * Math.Max(heightM, 0.0))
            + (_tNoiseC * _temperature.Fbm(x, y, z, AllOctaves));
        double wet = 0.5 + (0.5 * Math.Clamp(1.6 * _precip.Fbm(x, y, z, AllOctaves), -1.0, 1.0));
        return (t, _precipMax * Math.Pow(wet, 1.6));
    }
}
