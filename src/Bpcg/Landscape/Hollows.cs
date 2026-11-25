using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Hydro;
using Bpcg.Planet;

namespace Bpcg.Landscape;

/// <summary>[lakes] 설정 값 (LakeSettings).</summary>
/// <param name="MinDepthM">가장 깊은 곳이 이보다 얕은 웅덩이는 메웁니다 [m] (lakes.min_depth_m).</param>
/// <param name="MinAreaM2">넓이가 이보다 작은 웅덩이는 메웁니다 [m²] (lakes.min_area_m2).</param>
/// <param name="HollowDepthM">얼음이 긁은 우묵한 자리의 가장 깊은 곳 [m] (lakes.hollow_depth_m).</param>
/// <param name="HollowWavelengthM">우묵한 자리 하나의 대략의 크기 [m] (lakes.hollow_wavelength_m).</param>
/// <param name="HollowFraction">고를 수 있는 땅 가운데 낮추는 몫 0~1 (lakes.hollow_fraction).</param>
/// <param name="HollowAgeYr">얼음이 물러난 뒤 지난 햇수 [yr] (lakes.hollow_age_yr).</param>
/// <param name="Seed">시드 (planet.seed).</param>
/// <param name="LgmCoolingC">빙하기에 지금보다 낮았던 기온 [°C] (lakes.lgm_cooling_c). null 이면 빙하가 덮였던 땅을 가리지 않고 어디에나 우묵한 자리를 둡니다.</param>
/// <param name="SummerAmplitudeC">극에서 여름이 연평균보다 따뜻한 정도 [°C] (lakes.summer_amplitude_c).</param>
/// <param name="BasinAgeYr">땅이 가라앉아 온 햇수 [yr] (uplift.orogen_duration_myr). 0 이면 가라앉는 땅의 우묵한 자리를 두지 않습니다.</param>
/// <param name="Evaporation">호수 수면의 증발을 따져 넘치지 못하는 호수를 가리면 참 (lakes.evaporation).</param>
/// <param name="RiftSegmentM">판이 갈라지는 곳의 분지 하나와 그 옆 턱을 합친 길이 [m] (lakes.rift_segment_m). 0 이면 마디로 나누지 않습니다.</param>
public sealed record LakeSettings(
    double MinDepthM, double MinAreaM2, double HollowDepthM, double HollowWavelengthM, double HollowFraction, double HollowAgeYr, long Seed,
    double? LgmCoolingC = null, double SummerAmplitudeC = 0.0, double BasinAgeYr = 0.0, bool Evaporation = false,
    double RiftSegmentM = 0.0);

/// <summary>우묵한 자리를 만들 때 원인을 따지는 데 쓰는 칸 값들 (Hollows.AddBasins).</summary>
/// <param name="Receiver">(N,) 물을 보내는 이웃 (솔버 결과).</param>
/// <param name="Order">(N,) 하류가 먼저 오는 순서 (솔버 결과).</param>
/// <param name="Uplift">(N,) 땅이 솟는 속도 [m/yr]. 가라앉는 칸은 음수.</param>
/// <param name="TemperatureC">(N,) 지금의 연평균 기온 [°C].</param>
/// <param name="Precip">(N,) 한 해 강수 [m/yr].</param>
/// <param name="LatRad">(N,) 위도 [rad].</param>
public sealed record BasinCauses(long[] Receiver, long[] Order, double[] Uplift, double[] TemperatureC, double[] Precip, double[] LatRad);

/// <summary>Hollows.AddBasins 의 결과.</summary>
/// <param name="Z">(N,) 새 고도 [m]. 물이 고이는 칸만 낮아져 있습니다.</param>
/// <param name="Hollow">(N,) 낮춘 채로 둔 칸.</param>
/// <param name="Ice">(N,) 얼음이 덮였던 땅: Glaciation.None, IceAge, Now. 가리지 않았으면 모두 None.</param>
/// <param name="NGlacial">얼음이 긁은 자리로 낮춘 칸 수.</param>
/// <param name="NTectonic">가라앉는 땅이라 낮춘 칸 수.</param>
public sealed record BasinResult(double[] Z, bool[] Hollow, byte[] Ice, int NGlacial, int NTectonic);

/// <summary>
/// 호수가 생길 우묵한 자리 (docs/pipeline.md 7.5). 솔버가 만든 지형은 모든 칸이 아랫칸보다 높아서 물이 고일 자리가 없습니다.
/// 지구의 큰 호수 자리는 두 가지로 생깁니다. 땅이 가라앉는데 강이 실어 온 흙이 그만큼을 메우지 못한 자리(바이칼호, 탕가니카호),
/// 그리고 빙하기의 얼음이 바닥을 고르지 않게 긁고 물러난 자리(오대호, 캐나다와 핀란드의 호수)입니다.
/// 행성 지도에서 이 두 자리를 낮추고, 물이 실제로 고이는 칸만 낮춘 채로 둡니다. 그리고 가장 깊은 곳이 MinDepthM 에 못 미치거나
/// 넓이가 MinAreaM2 에 못 미치는 작은 웅덩이는 흙으로 메웁니다.
/// 호수 자체는 뒤의 물 단계(LakeBalance, Water.WaterBodies)가 정합니다.
/// </summary>
public static class Hollows
{
    /// <summary>낮출 깊이를 정하는 노이즈의 해시 용도 번호.</summary>
    public const long StreamHollow = 7121;

    /// <summary>판이 갈라지는 곳을 마디로 나누는 노이즈의 해시 용도 번호.</summary>
    public const long StreamRift = 7122;

    /// <summary>
    /// 마디 노이즈 ν 가 이 값 이상이면 온전히 가라앉고(몫 1), −이 값 이하면 가라앉지 않습니다(몫 0, 마디 사이의 턱). 그 사이는 곧게 잇습니다.
    /// 노이즈가 0 둘레에 고르게 놓이므로 길이의 절반쯤이 분지, 나머지가 턱과 그 비탈입니다.
    /// </summary>
    public const double RiftSegmentBand = 0.2;

    /// <summary>노이즈를 겹치는 횟수. 큰 덩어리 위에 절반·4분의 1 크기의 굴곡을 얹습니다.</summary>
    public const int NoiseOctaves = 3;

    /// <summary>낮추는 땅 가운데 노이즈 값이 가장 큰 이 몫(10분의 1)은 HollowDepthM 까지 다 내려갑니다.</summary>
    public const double FullDepthShare = 0.1;

    /// <summary>물이 고였다고 보는 깊이의 아래 한계 [m]. Water.LakeMinDepthM 과 같은 값입니다.</summary>
    public const double PondedM = 1e-3;

    /// <summary>낮춘 바닥이 바다(높이 0 m)보다 이만큼은 높게 남게 합니다 [m]. 바다보다 낮은 땅은 바다에 잠긴 땅이 되기 때문입니다.</summary>
    public const double BedAboveSeaM = 1.0;

    /// <summary>설정 [lakes] 를 읽어 검사합니다. 절이 없으면(이 절이 생기기 전의 설정) null.</summary>
    public static LakeSettings? Settings(Config cfg)
    {
        if (!cfg.Contains("lakes"))
        {
            return null;
        }
        double minDepth = cfg.F("lakes.min_depth_m");
        double minArea = cfg.F("lakes.min_area_m2");
        double depth = cfg.F("lakes.hollow_depth_m");
        double wavelength = cfg.F("lakes.hollow_wavelength_m");
        double fraction = cfg.F("lakes.hollow_fraction");
        double age = cfg.F("lakes.hollow_age_yr");
        if (!(minDepth >= 0.0 && minArea >= 0.0 && depth >= 0.0 && wavelength > 0.0 && fraction >= 0.0 && fraction <= 1.0 && age >= 0.0))
        {
            throw new ArgumentException(
                "lakes 는 min_depth_m ≥ 0, min_area_m2 ≥ 0, hollow_depth_m ≥ 0, hollow_wavelength_m > 0, 0 ≤ hollow_fraction ≤ 1, "
                + $"hollow_age_yr ≥ 0 이어야 합니다. 받은 값: min_depth_m {minDepth}, min_area_m2 {minArea}, hollow_depth_m {depth}, "
                + $"hollow_wavelength_m {wavelength}, hollow_fraction {fraction}, hollow_age_yr {age}");
        }
        // 아래 세 키는 뒤에 생겼습니다. 없는 설정(예전 행성 설정)은 예전처럼 어디에나 우묵한 자리를 두고, 호수를 넘칠 때까지 채웁니다.
        Section lakes = cfg.Sec("lakes");
        double? cooling = lakes.Contains("lgm_cooling_c") ? cfg.F("lakes.lgm_cooling_c") : null;
        double summer = lakes.Contains("summer_amplitude_c") ? cfg.F("lakes.summer_amplitude_c") : 0.0;
        bool evaporation = lakes.Contains("evaporation") && cfg.B("lakes.evaporation");
        if (cooling is double dc && !(double.IsFinite(dc) && dc >= 0.0 && double.IsFinite(summer) && summer >= 0.0))
        {
            throw new ArgumentException(
                $"lakes 는 lgm_cooling_c ≥ 0, summer_amplitude_c ≥ 0 이어야 합니다. 받은 값: lgm_cooling_c {dc}, summer_amplitude_c {summer}");
        }
        double basinAge = cooling is null ? 0.0 : cfg.F("uplift.orogen_duration_myr") * 1.0e6;
        double segment = lakes.Contains("rift_segment_m") ? cfg.F("lakes.rift_segment_m") : 0.0;
        if (!(double.IsFinite(segment) && segment >= 0.0))
        {
            throw new ArgumentException($"lakes.rift_segment_m 은 0 이상이어야 합니다. 받은 값: {segment}");
        }
        return new LakeSettings(
            minDepth, minArea, depth, wavelength, fraction, age, cfg.I("planet.seed"), cooling, summer, basinAge, evaporation, segment);
    }

    /// <summary>
    /// 칸마다 낮출 깊이 [m] (출구·바다 칸은 0). 노이즈 값이 큰 쪽부터 땅의 HollowFraction 만큼을 고르고, 그 안에서
    /// 노이즈가 클수록 깊게(가장 깊은 곳 HollowDepthM) 낮춥니다. 그다음 강이 실어 온 흙이 HollowAgeYr 동안 쌓인 두께
    /// (흙 흐름 × 햇수 ÷ 칸 넓이)를 뺍니다. 흙을 많이 나르는 큰 강 위의 자리는 다 메워져 0 이 됩니다.
    /// noise 는 칸마다의 노이즈 값 (N,) 입니다.
    /// </summary>
    public static double[] Depth(double[] noise, bool[] isOutlet, double[] sedimentFlux, double[] cellArea, LakeSettings st) =>
        Depth(noise, isOutlet, sedimentFlux, cellArea, st, eligible: null);

    /// <summary>
    /// 위와 같되, eligible 이 참인 칸에서만 고릅니다 (얼음이 긁은 땅). 몫 HollowFraction 도 그 칸들 가운데서 셉니다.
    /// eligible 이 null 이면 출구가 아닌 모든 칸에서 고릅니다.
    /// </summary>
    public static double[] Depth(
        double[] noise, bool[] isOutlet, double[] sedimentFlux, double[] cellArea, LakeSettings st, bool[]? eligible)
    {
        int n = noise.Length;
        if (isOutlet.Length != n || sedimentFlux.Length != n || cellArea.Length != n || (eligible is not null && eligible.Length != n))
        {
            throw new ArgumentException(
                $"noise, isOutlet, sedimentFlux, cellArea 는 같은 길이여야 합니다. 받은 길이: {n}, {isOutlet.Length}, {sedimentFlux.Length}, {cellArea.Length}");
        }
        double[] depth = new double[n];
        var land = new List<double>();
        for (int c = 0; c < n; c++)
        {
            if (!isOutlet[c] && (eligible is null || eligible[c]))
            {
                land.Add(noise[c]);
            }
        }
        int nLand = land.Count;
        int nLow = (int)Math.Floor(st.HollowFraction * nLand);
        if (nLand == 0 || nLow == 0 || st.HollowDepthM <= 0.0)
        {
            return depth;
        }
        land.Sort();
        // 낮추기 시작하는 노이즈 값(edge)과 최대 깊이에 닿는 노이즈 값(full).
        double edge = land[nLand - nLow];
        int nFull = Math.Max((int)Math.Floor(FullDepthShare * nLow), 1);
        double full = land[nLand - nFull];
        double span = full - edge;
        for (int c = 0; c < n; c++)
        {
            if (isOutlet[c] || noise[c] < edge || (eligible is not null && !eligible[c]))
            {
                continue;
            }
            double t = span > 0.0 ? Math.Min((noise[c] - edge) / span, 1.0) : 1.0;
            double infill = st.HollowAgeYr * Math.Max(sedimentFlux[c], 0.0) / cellArea[c];
            depth[c] = Math.Max((st.HollowDepthM * t) - infill, 0.0);
        }
        return depth;
    }

    /// <summary>
    /// 가라앉는 땅에서 흙이 메우지 못하고 남는 깊이 [m] (starved_depth). 땅이 한 해에 −U 만큼 가라앉으면 그 칸에는 −U × 칸 넓이
    /// [m³/yr] 의 빈자리가 생깁니다. 강이 실어 온 흙이 그 빈자리를 채우고, 남은 흙만 아래 칸으로 넘어갑니다. 흙이 모자라 채우지
    /// 못한 빈자리가 ageYr 동안 쌓인 깊이가 호수 바닥이 됩니다 (받는 자리가 흙보다 많은 분지에 호수가 남는다는 규칙,
    /// Carroll &amp; Bohacs 1999).
    /// 예: 한 해 0.03 mm 가라앉는 넓이 3.2 × 10⁸ m² 칸의 빈자리는 9,600 m³/yr 입니다. 흙이 한 해 2,000 m³ 들어오면
    /// 7,600 m³/yr 가 남고, 3천만 년이면 7,600 × 3 × 10⁷ ÷ (3.2 × 10⁸) ≈ 713 m 입니다. 흙이 9,600 m³ 넘게 들어오면 0 입니다.
    /// receiver·order 는 물길(하류가 먼저 오는 순서), uplift [m/yr], cellArea [m²]. 출구 칸은 0 입니다.
    /// share: (N,) 칸마다 가라앉는 몫 0~1 (없으면 모두 1). 판이 갈라지는 곳은 길이 약 100 km 의 분지로 나뉘고 분지 사이에는
    /// 덜 가라앉는 턱이 있습니다. 턱의 칸은 몫이 0 에 가까워 빈자리가 거의 생기지 않습니다.
    /// </summary>
    public static double[] StarvedDepth(
        long[] receiver, long[] order, double[] uplift, double[] cellArea, bool[] isOutlet, double ageYr, double[]? share = null)
    {
        int n = receiver.Length;
        if (order.Length != n || uplift.Length != n || cellArea.Length != n || isOutlet.Length != n || (share is not null && share.Length != n))
        {
            throw new ArgumentException(
                $"receiver, order, uplift, cellArea, isOutlet 은 같은 길이여야 합니다. 받은 길이: {n}, {order.Length}, {uplift.Length}, "
                + $"{cellArea.Length}, {isOutlet.Length}");
        }
        if (!(double.IsFinite(ageYr) && ageYr >= 0.0))
        {
            throw new ArgumentException($"ageYr 는 0 이상의 유한한 값이어야 합니다. 받은 값: {ageYr}");
        }
        double[] depth = new double[n];
        double[] sedimentIn = new double[n];
        // order 는 하류가 먼저라 뒤에서부터 돌면 상류가 먼저입니다.
        for (int k = n - 1; k >= 0; k--)
        {
            int c = (int)order[k];
            if (isOutlet[c])
            {
                continue;
            }
            double passOn;
            if (uplift[c] >= 0.0)
            {
                passOn = sedimentIn[c] + (uplift[c] * cellArea[c]);
            }
            else
            {
                double room = -uplift[c] * cellArea[c] * (share is null ? 1.0 : Math.Min(Math.Max(share[c], 0.0), 1.0));
                double settled = Math.Min(sedimentIn[c], room);
                depth[c] = (room - settled) / cellArea[c] * ageYr;
                passOn = sedimentIn[c] - settled;
            }
            int r = (int)receiver[c];
            if (r != c)
            {
                sedimentIn[r] += passOn;
            }
        }
        return depth;
    }

    /// <summary>
    /// 땅 높이 z [m] 를 depth [m] 만큼 낮춘 바닥 높이 [m]. 우리 지도는 바다(0 m)보다 낮은 땅을 그리지 못하므로, 바닥이
    /// 바다 높이에 가까워질수록 덜 낮춥니다: 바닥 = BedAboveSeaM + h² ÷ (h + depth), h = z − BedAboveSeaM.
    /// 깊이가 땅 높이보다 훨씬 작으면 거의 그대로 낮추고, 훨씬 크면 바다 바로 위에서 멈춥니다. 깊이를 그냥 자르면 깊이 가라앉은
    /// 분지의 바닥이 모두 같은 높이가 되어, 물이 어디부터 차는지 정할 수 없습니다.
    /// 예: 높이 401 m 인 땅을 100 m 낮추면 1 + 400² ÷ 500 = 321 m (그냥 빼면 301 m), 700 m 낮추면 1 + 400² ÷ 1,100 ≈ 146 m.
    /// 지구의 깊은 호수 바닥은 바다보다 낮기도 합니다 (바이칼호 −1,186 m). 그런 바닥은 만들지 못합니다.
    /// </summary>
    public static double LoweredBed(double z, double depth)
    {
        double h = z - BedAboveSeaM;
        if (!(depth > 0.0) || !(h > 0.0))
        {
            return z;
        }
        return BedAboveSeaM + ((h * h) / (h + depth));
    }

    /// <summary>
    /// 행성 지도에 우묵한 자리를 만듭니다 (add_hollows). 원인을 따지지 않는 예전 방식입니다: 노이즈 값이 큰 땅을 어디든 낮춥니다.
    /// 설정에 lakes.lgm_cooling_c 가 없을 때와 시험에서 씁니다. 반환: (새 고도, 낮춘 채로 둔 칸).
    /// </summary>
    public static (double[] ZNew, bool[] Hollow) AddHollows(
        CellGraph graph, double[] z, bool[] isOutlet, double[] sedimentFlux, Config cfg)
    {
        BasinResult r = AddBasins(graph, z, isOutlet, sedimentFlux, cfg, causes: null);
        return (r.Z, r.Hollow);
    }

    /// <summary>
    /// 행성 지도에 우묵한 자리를 만듭니다 (add_basins). 낮춘 땅에서 물이 고이는 칸(넘칠 높이가 땅보다 높은 칸)만 낮춘 채로 두고,
    /// 나머지는 원래 높이로 되돌립니다. 그래서 호수가 되지 않는 땅은 솔버 결과 그대로입니다.
    /// causes 가 있고 설정에 lakes.lgm_cooling_c 가 있으면 두 원인으로 낮춥니다:
    /// 1. 얼음이 긁은 자리: 얼음이 덮인 적이 있는 땅(Glaciation.IceAge 와 Now)에서 노이즈 값이 큰 쪽부터 HollowFraction 만큼.
    /// 2. 가라앉는 땅: 흙이 메우지 못하고 남는 깊이 (StarvedDepth).
    /// 그렇지 않으면 예전처럼 땅 어디에서나 노이즈로 고릅니다.
    /// 평면 그래프(걷는 유역)에는 만들지 않습니다: 파장이 유역보다 커서 유역 전체가 함께 내려갈 뿐입니다.
    /// </summary>
    public static BasinResult AddBasins(
        CellGraph graph, double[] z, bool[] isOutlet, double[] sedimentFlux, Config cfg, BasinCauses? causes)
    {
        int n = graph.NCells;
        if (z.Length != n || isOutlet.Length != n || sedimentFlux.Length != n)
        {
            throw new ArgumentException(
                $"z, isOutlet, sedimentFlux 는 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: {z.Length}, {isOutlet.Length}, {sedimentFlux.Length}");
        }
        double[] zNew = (double[])z.Clone();
        bool[] hollow = new bool[n];
        byte[] ice = new byte[n];
        LakeSettings? st = Settings(cfg);
        if (st is null || graph.Kind != "sphere")
        {
            return new BasinResult(zNew, hollow, ice, 0, 0);
        }
        bool byCause = causes is not null && st.LgmCoolingC is not null;
        if (byCause && (causes!.Receiver.Length != n || causes.Order.Length != n || causes.Uplift.Length != n
            || causes.TemperatureC.Length != n || causes.Precip.Length != n || causes.LatRad.Length != n))
        {
            throw new ArgumentException($"causes 의 배열은 모두 칸 수 ({n},) 와 같은 길이여야 합니다");
        }
        bool[]? scoured = null;
        if (byCause)
        {
            ice = Glaciation.IceCover(
                causes!.TemperatureC, causes.Precip, causes.LatRad, isOutlet, st.LgmCoolingC!.Value, st.SummerAmplitudeC);
            // 얼음이 덮인 적이 있는 땅 (빙하기에만 덮였던 땅과 지금도 눈이 남는 땅). 지금도 눈이 남는 땅은 실제라면 얼음 아래라
            // 호수가 드러나지 않습니다. 그런데 이 생성기는 지금의 얼음을 만들지 않고 그 땅에도 강과 흙을 그대로 둡니다.
            // 호수만 빼면 앞뒤가 맞지 않으므로 두 땅 모두에 둡니다.
            scoured = new bool[n];
            for (int c = 0; c < n; c++)
            {
                scoured[c] = ice[c] != Glaciation.None;
            }
        }
        double[] glacial = new double[n];
        if (st.HollowDepthM > 0.0 && st.HollowFraction > 0.0)
        {
            long seed = (long)(Hashing.Hash3(st.Seed, StreamHollow, 0) >> 1);
            double[] noise = Noise.Fbm3(graph.Unit(), seed, NoiseOctaves, frequency: graph.R!.Value / st.HollowWavelengthM);
            glacial = Depth(noise, isOutlet, sedimentFlux, graph.Area, st, scoured);
        }
        double[] tectonic = new double[n];
        if (byCause && st.BasinAgeYr > 0.0)
        {
            // 판이 갈라지는 곳은 끊기지 않는 한 줄의 골이 아니라, 길이 약 100 km 의 분지가 턱을 사이에 두고 늘어선 것입니다.
            // 노이즈로 분지와 턱을 번갈아 둡니다. 턱이 없으면 호수가 판 경계를 따라 수천 km 이어집니다.
            double[]? share = null;
            if (st.RiftSegmentM > 0.0)
            {
                long riftSeed = (long)(Hashing.Hash3(st.Seed, StreamRift, 0) >> 1);
                double[] nu = Noise.Fbm3(graph.Unit(), riftSeed, 1, frequency: graph.R!.Value / st.RiftSegmentM);
                share = new double[n];
                for (int c = 0; c < n; c++)
                {
                    share[c] = Math.Min(Math.Max(0.5 + (nu[c] / (2.0 * RiftSegmentBand)), 0.0), 1.0);
                }
            }
            tectonic = StarvedDepth(causes!.Receiver, causes.Order, causes.Uplift, graph.Area, isOutlet, st.BasinAgeYr, share);
        }
        double[] zLow = new double[n];
        for (int c = 0; c < n; c++)
        {
            zLow[c] = byCause
                ? LoweredBed(z[c], glacial[c] + tectonic[c])
                // 예전 방식: 바닥이 바다 높이 아래로 내려가지 않게 깊이를 자릅니다 (바닷가의 낮은 땅).
                : z[c] - Math.Min(glacial[c], Math.Max(z[c] - BedAboveSeaM, 0.0));
        }
        double[] filled = Depressions.FillDepressions(zLow, graph.Nbr, isOutlet);
        int nGlacial = 0;
        int nTectonic = 0;
        for (int c = 0; c < n; c++)
        {
            if (!isOutlet[c] && filled[c] - zLow[c] > PondedM)
            {
                hollow[c] = true;
                zNew[c] = zLow[c];
                if (zLow[c] < z[c])
                {
                    nTectonic += tectonic[c] > glacial[c] ? 1 : 0;
                    nGlacial += tectonic[c] > glacial[c] ? 0 : 1;
                }
            }
        }
        return new BasinResult(zNew, hollow, ice, nGlacial, nTectonic);
    }

    /// <summary>
    /// 작은 웅덩이를 흙으로 메웁니다 (fill_shallow). 웅덩이 하나(서로 이웃한 고인 칸의 묶음)에서 가장 깊은 곳이 minDepthM 에
    /// 못 미치거나 넓이가 minAreaM2 에 못 미치면 그 칸들을 넘칠 높이까지 올립니다. 깊고 넓은 웅덩이는 그대로 두어 호수가 됩니다.
    /// stepM 이 0 보다 크면 메운 칸을 넘치는 곳에서 한 칸 멀어질 때마다 stepM [m] 씩 높게 둡니다. 넘칠 높이로 평평하게만
    /// 메우면 메운 칸과 그 물이 흘러갈 아랫칸의 높이가 같아져, 물이 흐를 방향이 없는 칸이 생기기 때문입니다.
    /// 반환: (새 고도, 메운 칸, 메운 웅덩이 수, 남긴 웅덩이 수).
    /// </summary>
    public static (double[] ZNew, bool[] Filled, int NFilled, int NKept) FillShallow(
        CellGraph graph, double[] z, bool[] isOutlet, double minDepthM, double minAreaM2 = 0.0, double stepM = 0.0)
    {
        int n = graph.NCells;
        if (z.Length != n || isOutlet.Length != n)
        {
            throw new ArgumentException($"z, isOutlet 은 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: {z.Length}, {isOutlet.Length}");
        }
        double[] zNew = (double[])z.Clone();
        bool[] filledMask = new bool[n];
        double[] zHat = Depressions.FillDepressions(z, graph.Nbr, isOutlet);
        bool[] ponded = new bool[n];
        for (int c = 0; c < n; c++)
        {
            ponded[c] = !isOutlet[c] && zHat[c] - z[c] > PondedM;
        }
        bool[] seen = new bool[n];
        var stack = new Stack<int>();
        var group = new List<int>();
        int nFilled = 0;
        int nKept = 0;
        int[] nbr = graph.Nbr;
        for (int c0 = 0; c0 < n; c0++)
        {
            if (!ponded[c0] || seen[c0])
            {
                continue;
            }
            group.Clear();
            double deepest = 0.0;
            double area = 0.0;
            seen[c0] = true;
            stack.Push(c0);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                group.Add(c);
                deepest = Math.Max(deepest, zHat[c] - z[c]);
                area += graph.Area[c];
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = nbr[(c * CellGraph.NSlots) + s];
                    if (v >= 0 && ponded[v] && !seen[v])
                    {
                        seen[v] = true;
                        stack.Push(v);
                    }
                }
            }
            if (deepest >= minDepthM && area >= minAreaM2)
            {
                nKept++;
                continue;
            }
            nFilled++;
            foreach (int c in group)
            {
                zNew[c] = zHat[c];
                filledMask[c] = true;
            }
        }
        if (nFilled > 0 && stepM > 0.0)
        {
            double[] zStep = Depressions.FillEpsilon(z, graph.Nbr, isOutlet, stepM);
            for (int c = 0; c < n; c++)
            {
                if (filledMask[c])
                {
                    zNew[c] = zStep[c];
                }
            }
        }
        return (zNew, filledMask, nFilled, nKept);
    }
}
