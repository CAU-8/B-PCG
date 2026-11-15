using System;
using System.Collections.Generic;
using Bpcg.Core;
using Bpcg.Hydro;
using Bpcg.Numerics;

namespace Bpcg.Landscape;

/// <summary>[dams] 설정 값 (DamSettings).</summary>
/// <param name="BlockingFraction">골짜기를 막을 만큼 큰 산사태 가운데 실제로 둑이 되어 남는 몫 (0~1).</param>
/// <param name="PersistenceYr">무너지지 않은 둑이 강에 깎여 없어질 때까지 남는 햇수.</param>
/// <param name="MinHeightM">다루는 가장 낮은 둑의 높이 [m].</param>
/// <param name="MaxHeightM">다루는 가장 높은 둑의 높이 [m].</param>
/// <param name="FaceSlope">둑 비탈의 경사 [m/m].</param>
/// <param name="FillSlopeRatio">둑 뒤를 메운 흙 바닥의 경사 ÷ 메우기 전 강바닥의 경사 (0 이상 1 미만).</param>
/// <param name="MinSlopeRatio">옆 비탈이 무너지는 경사(S_crit)의 이 배 이상일 때만 산사태가 난다고 봅니다.</param>
/// <param name="MinDrainageAreaM2">상류 넓이가 이만큼 이상인 칸을 물길로 봅니다 [m²].</param>
/// <param name="Seed">시드 (planet.seed).</param>
public sealed record DamSettings(
    double BlockingFraction, double PersistenceYr, double MinHeightM, double MaxHeightM, double FaceSlope, double FillSlopeRatio,
    double MinSlopeRatio, double MinDrainageAreaM2, long Seed);

/// <summary>산사태 둑을 놓고 그 뒤를 흙으로 메운 결과 (DamResult).</summary>
public sealed class DamResult
{
    /// <summary>둑과 메운 흙을 얹은 고도 [m] (N,).</summary>
    public required double[] Z { get; init; }

    /// <summary>둑(돌무더기 둔덕)이 올린 칸 (N,).</summary>
    public required bool[] Dam { get; init; }

    /// <summary>둑이나 선상지 뒤의 웅덩이를 흙이 메운 칸 (N,).</summary>
    public required bool[] Fill { get; init; }

    /// <summary>놓은 둑의 목록 (칸, 높이, 나이, 부피, 흙이 메운 몫 등).</summary>
    public required List<object?> Sites { get; init; }

    /// <summary>진단: 후보 칸 수, 지금 남아 있을 둑과 호수의 기대 수, 상류 넓이 구간별 기대 수.</summary>
    public required OrderedDictionary<string, object?> Diag { get; init; }
}

/// <summary>
/// 산사태 둑과 그 뒤를 메운 흙 (docs/pipeline.md 7.4).
/// </summary>
/// <remarks>
/// 왜 필요한가: 가파른 산비탈이 무너져 골짜기를 막으면 그 위쪽에 물이 고이고, 강이 실어 온 흙이 그 물을 메워 평평한 골짜기
/// 바닥이 됩니다. 솔버가 만든 지형에는 이 둘이 없습니다.
/// 둑을 아무 데나 정해진 수만큼 놓지 않고, 파이프라인이 이미 아는 값으로 '지금 둑이 있을 기대 수'를 칸마다 계산합니다.
/// 1. 산사태로 나오는 암석의 양: 무너지는 경사에 묶인 산비탈에서는 깎이는 양이 땅이 솟는 양과 같고, 그것이 산사태로 나옵니다
///    (Burbank 외 1996, Larsen &amp; Montgomery 2012). 물길 칸 하나로 곧바로 내려오는 산비탈의 넓이 × 솟는 속도입니다.
/// 2. 산사태의 크기: 넓이가 a 인 산사태의 수는 a^(−2.4) 에 비례합니다 (Malamud 외 2004). 부피는 0.146 × a^1.33 입니다
///    (Larsen 외 2010. 지수는 확인했고 계수 0.146 은 원문에서 확인하지 못했습니다).
/// 3. 골짜기를 높이 h 로 막는 데 드는 부피: 그 자리의 지표에 원뿔 둔덕을 얹어 잽니다. 좁은 골짜기일수록 적게 듭니다.
///    두 높이에서 잰 부피를 거듭제곱 식 v0 × (h ÷ h0)^p 로 이어 씁니다. 옆 비탈 경사가 s 인 곧은 V 자 골짜기라면
///    2h³ ÷ (3 × 둑 비탈 경사 × s) 입니다.
/// 4. 둑이 남을 확률: 강이 클수록, 둑이 작을수록 넘친 물에 깎여 무너집니다. 지표 DBI = log10(상류 넓이 km² × 높이 m ÷
///    부피 백만 m³) 가 1.2 이하이면 남고 5.0 이상이면 무너지며, 그 사이는 곧은 선으로 잇습니다 (Oppikofer 외 2020 의 합친 자료).
/// 5. 지금 남아 있을 기대 수 = 1년에 생기는 수 × 남을 확률 × 둑이 남는 햇수 × BlockingFraction.
/// 기대 수에 맞춰 해시로 둑을 뽑고, 둑마다 나이를 0 ~ 남는 햇수 사이에서 뽑습니다. 그 나이 동안 강이 실어 온 흙(흙 흐름 ×
/// 나이)이 둑 뒤의 웅덩이를 먼 쪽부터 메웁니다. 다 메워지면 평지가 되고, 덜 메워졌으면 둑 가까이에 호수가 남습니다.
/// 둑이 없는 웅덩이(선상지가 막은 자리)는 오래된 것이라 다 메웁니다.
/// </remarks>
public static class Dams
{
    /// <summary>산사태 넓이 분포의 지수: 넓이 a 인 산사태의 수 ∝ a^(−2.4) (Malamud 외 2004).</summary>
    public const double SizeExponent = 2.4;

    /// <summary>산사태 부피 = VolumeAreaCoefficient × 넓이^VolumeAreaExponent (Larsen 외 2010, 넓이 m², 부피 m³).</summary>
    public const double VolumeAreaExponent = 1.33;

    /// <summary>위 식의 계수. 원문에서 확인하지 못한 값입니다.</summary>
    public const double VolumeAreaCoefficient = 0.146;

    /// <summary>크기 분포를 쓰는 산사태 넓이의 범위 [m²]. 이보다 작은 산사태는 수가 a^(−2.4) 을 따르지 않습니다.</summary>
    public const double LandslideMinAreaM2 = 1.0e3;
    public const double LandslideMaxAreaM2 = 1.0e6;

    /// <summary>둑이 남는 쪽과 무너지는 쪽의 DBI 한계 (Oppikofer 외 2020).</summary>
    public const double DbiLower = 1.2;
    public const double DbiUpper = 5.0;

    /// <summary>물이 넘치는 높이 ÷ 둑 높이의 어림. 물은 둔덕과 골짜기 벽이 만나는 낮은 목으로 넘칩니다.</summary>
    public const double SpillFraction = 0.75;

    /// <summary>둑 뒤에 물이 고이는 길이(넘치는 높이 ÷ 강 경사)가 칸 간격의 이 배 이상인 둑만 다룹니다. 그보다 짧으면 격자가 그리지 못합니다.</summary>
    public const double MinPondCells = 4.0;

    /// <summary>
    /// 메운 평지에서 물길이 아닌 칸의 바닥 기울기 ÷ 물길 칸의 바닥 기울기. 물길 칸이 옆 칸보다 낮게 메워져,
    /// 강이 평지 위에서도 메우기 전의 물길을 따라 흐릅니다.
    /// </summary>
    public const double FloodplainSlopeFactor = 2.0;

    /// <summary>메운 바닥에 두는 가장 작은 경사 [m/m] (1 km 에 10 cm). 강바닥이 평평한 칸에서도 물이 흘러갈 방향이 남게 합니다.</summary>
    public const double MinFillSlope = 1.0e-4;

    /// <summary>둑 높이를 나누는 구간 수 (로그 간격).</summary>
    public const int HeightBins = 24;

    /// <summary>진단에 쓰는 상류 넓이 구간의 경계 [km²].</summary>
    public static readonly double[] CatchmentEdgesKm2 = [0.1, 0.5, 1.5, 5.0, 13.0, 20.0, 50.0, 200.0];

    private const long StreamSite = 7111;
    private const long StreamHeight = 7112;
    private const long StreamAge = 7113;
    private const long StreamOrder = 7114;
    private const double PondedM = 1e-3;

    /// <summary>
    /// 설정 [dams] 를 읽어 검사합니다. 절이 없거나, enabled 가 false 이거나, blocking_fraction 이나 fill_slope_ratio 키가
    /// 없으면(이 키들이 생기기 전의 설정으로 만든 묶음) null.
    /// </summary>
    public static DamSettings? Settings(Config cfg)
    {
        if (!cfg.Contains("dams") || !cfg.B("dams.enabled") || !cfg.Sec("dams").Contains("blocking_fraction")
            || !cfg.Sec("dams").Contains("fill_slope_ratio"))
        {
            return null;
        }
        double beta = cfg.F("dams.blocking_fraction");
        double persistence = cfg.F("dams.persistence_yr");
        double hMin = cfg.F("dams.min_height_m");
        double hMax = cfg.F("dams.max_height_m");
        double face = cfg.F("dams.slope");
        double fill = cfg.F("dams.fill_slope_ratio");
        double ratio = cfg.F("dams.min_slope_ratio");
        double areaMin = cfg.F("dams.min_drainage_area_m2");
        if (!(beta >= 0.0 && beta <= 1.0 && persistence >= 0.0 && hMin > 0.0 && hMax > hMin && face > 0.0 && fill >= 0.0 && fill < 1.0))
        {
            throw new ArgumentException(
                "dams 는 0 ≤ blocking_fraction ≤ 1, persistence_yr ≥ 0, 0 < min_height_m < max_height_m, slope > 0, 0 ≤ fill_slope_ratio < 1 "
                + $"이어야 합니다. 받은 값: blocking_fraction {beta}, persistence_yr {persistence}, min_height_m {hMin}, "
                + $"max_height_m {hMax}, slope {face}, fill_slope_ratio {fill}");
        }
        if (!(ratio >= 0.0 && areaMin > 0.0))
        {
            throw new ArgumentException(
                $"dams 는 min_slope_ratio ≥ 0, min_drainage_area_m2 > 0 이어야 합니다. 받은 값: min_slope_ratio {ratio}, min_drainage_area_m2 {areaMin}");
        }
        return new DamSettings(beta, persistence, hMin, hMax, face, fill, ratio, areaMin, cfg.I("planet.seed"));
    }

    /// <summary>
    /// 넓이가 areaM2 이상인 산사태의 수를 산사태로 나간 암석의 부피로 나눈 값 [1/m³]. 넓이 분포가 a^(−SizeExponent) 이고
    /// 넓이가 LandslideMinAreaM2 ~ maxAreaM2 인 산사태가 암석을 모두 실어 나른다고 볼 때의 값입니다.
    /// areaM2 가 maxAreaM2 이상이면 0 (그 비탈에서는 그만큼 큰 산사태가 나지 않습니다).
    /// </summary>
    public static double NumberPerVolume(double areaM2, double maxAreaM2)
    {
        if (!(maxAreaM2 > LandslideMinAreaM2) || areaM2 >= maxAreaM2)
        {
            return 0.0;
        }
        double a = Math.Max(areaM2, LandslideMinAreaM2);
        double p = 1.0 - SizeExponent;
        double number = (NpMath.Power(a, p) - NpMath.Power(maxAreaM2, p)) / (SizeExponent - 1.0);
        double e = VolumeAreaExponent - SizeExponent + 1.0;
        double volume = VolumeAreaCoefficient * (NpMath.Power(maxAreaM2, e) - NpMath.Power(LandslideMinAreaM2, e)) / e;
        return number / volume;
    }

    /// <summary>V 자 골짜기를 높이 h [m] 로 막는 원뿔 둔덕의 부피 [m³] = 2h³ ÷ (3 × 둑 비탈 경사 × 골짜기 옆 비탈 경사).</summary>
    public static double DamVolume(double heightM, double faceSlope, double sideSlope) =>
        2.0 * heightM * heightM * heightM / (3.0 * faceSlope * sideSlope);

    /// <summary>부피 [m³] 가 volumeM3 인 산사태의 넓이 [m²] (부피 = 0.146 × 넓이^1.33 을 거꾸로 풉니다).</summary>
    public static double LandslideArea(double volumeM3) =>
        NpMath.Power(Math.Max(volumeM3, 0.0) / VolumeAreaCoefficient, 1.0 / VolumeAreaExponent);

    /// <summary>강에 비해 둑이 얼마나 작은지의 지표 DBI = log10(상류 넓이 km² × 둑 높이 m ÷ 둑 부피 백만 m³).</summary>
    public static double Dbi(double catchmentM2, double heightM, double volumeM3) =>
        Math.Log10(Math.Max(catchmentM2, 1.0) * heightM / Math.Max(volumeM3, 1.0));

    /// <summary>둑이 무너지지 않고 남을 확률: DBI 가 DbiLower 이하이면 1, DbiUpper 이상이면 0, 그 사이는 곧은 선.</summary>
    public static double Survival(double catchmentM2, double heightM, double volumeM3) =>
        1.0 - NpMath.Clip((Dbi(catchmentM2, heightM, volumeM3) - DbiLower) / (DbiUpper - DbiLower), 0.0, 1.0);

    /// <summary>둑 높이 구간의 경계 [m] (HeightBins + 1 개, 로그 간격).</summary>
    public static double[] HeightEdges(DamSettings st)
    {
        double[] edges = new double[HeightBins + 1];
        double ratio = st.MaxHeightM / st.MinHeightM;
        for (int b = 0; b <= HeightBins; b++)
        {
            edges[b] = st.MinHeightM * NpMath.Power(ratio, (double)b / HeightBins);
        }
        return edges;
    }

    /// <summary>
    /// 물길 칸 하나에서, 높이 구간마다 '지금 남아 있을 둑의 기대 수'를 구합니다 (weights 에 적음). 반환: 그 합.
    /// rockM3PerYr: 이 칸으로 곧바로 내려오는 산비탈에서 1년에 산사태로 나오는 암석 [m³/yr].
    /// maxLandslideM2: 그 산비탈에서 날 수 있는 가장 큰 산사태의 넓이.
    /// 둑 높이 h 에 드는 부피는 volume0M3 × (h ÷ height0M)^volumePower 입니다 (그 자리의 지표에서 잰 값).
    /// channelSlope: 강의 경사. catchmentM2: 상류 넓이. spacingM: 칸 간격.
    /// </summary>
    public static double ExpectedDams(
        double rockM3PerYr, double maxLandslideM2, double volume0M3, double height0M, double volumePower, double channelSlope,
        double catchmentM2, double spacingM, DamSettings st, double[] edges, double[] weights)
    {
        double total = 0.0;
        double scale = st.BlockingFraction * st.PersistenceYr * rockM3PerYr;
        double Volume(double h) => volume0M3 * NpMath.Power(h / height0M, volumePower);
        double prev = NumberPerVolume(LandslideArea(Volume(edges[0])), maxLandslideM2);
        for (int b = 0; b < HeightBins; b++)
        {
            double next = NumberPerVolume(LandslideArea(Volume(edges[b + 1])), maxLandslideM2);
            double mid = Math.Sqrt(edges[b] * edges[b + 1]);
            double w = 0.0;
            // 둑 뒤에 물이 고이는 길이가 칸 몇 개는 되어야 격자가 그립니다.
            if (prev > next && SpillFraction * mid >= MinPondCells * spacingM * Math.Max(channelSlope, 0.0))
            {
                w = scale * (prev - next) * Survival(catchmentM2, mid, Volume(mid));
            }
            weights[b] = w;
            total += w;
            prev = next;
        }
        return total;
    }

    /// <summary>
    /// 칸 a 의 강바닥에서 높이 heightM 만큼 솟고 사방으로 faceSlope 로 낮아지는 원뿔 둔덕이 지표 z 위로 솟는 부피 [m³].
    /// cells 를 주면 둔덕이 지표보다 높은 칸과 그 칸의 둔덕 높이를 담습니다. 출구 칸은 올리지 않습니다.
    /// </summary>
    public static double MoundVolume(
        CellGraph graph, double[] z, long[] rcv, int a, double heightM, double faceSlope, List<(int Cell, double Top)>? cells = null)
    {
        int ny = (int)graph.Shape[0];
        int nx = (int)graph.Shape[1];
        double dx = graph.Spacing;
        double[] pos = graph.Pos;
        int reach = (int)Math.Ceiling(heightM / faceSlope / dx) + 1;
        int aj = a / nx;
        int ai = a % nx;
        double volume = 0.0;
        for (int jj = Math.Max(aj - reach, 0); jj <= Math.Min(aj + reach, ny - 1); jj++)
        {
            for (int ii = Math.Max(ai - reach, 0); ii <= Math.Min(ai + reach, nx - 1); ii++)
            {
                int i = (jj * nx) + ii;
                if (rcv[i] == i)
                {
                    continue;
                }
                double ex = pos[i * 3] - pos[a * 3];
                double ey = pos[(i * 3) + 1] - pos[(a * 3) + 1];
                double top = z[a] + heightM - (faceSlope * Math.Sqrt((ex * ex) + (ey * ey)));
                if (top > z[i])
                {
                    volume += (top - z[i]) * graph.Area[i];
                    cells?.Add((i, top));
                }
            }
        }
        return volume;
    }

    /// <summary>
    /// 칸마다 메운 바닥의 경사 [m/m] 를 구합니다. 물길 칸은 메우기 전 강바닥 경사 × FillSlopeRatio 입니다. 저수지에 쌓인
    /// 흙의 윗면은 원래 강바닥 경사의 절반쯤으로 눕습니다 (Strand &amp; Pemberton 1982). 물길이 아닌 칸은 그 칸의 물이
    /// 흘러드는 물길 칸의 값 × FloodplainSlopeFactor 입니다. 어느 칸도 MinFillSlope 보다 작지 않습니다.
    /// </summary>
    public static double[] FillSlopes(long[] receiver, double[] areaM2, double[] slope, DamSettings st)
    {
        int n = receiver.Length;
        if (areaM2.Length != n || slope.Length != n)
        {
            throw new ArgumentException(
                $"receiver, areaM2, slope 는 같은 길이여야 합니다. 받은 길이: receiver {n}, areaM2 {areaM2.Length}, slope {slope.Length}");
        }
        double[] cellSlope = new double[n];
        for (int c = 0; c < n; c++)
        {
            // 물길 칸이 나올 때까지 아랫칸으로 내려갑니다. 산비탈은 칸 몇 개 길이라 금방 닿습니다.
            long ch = c;
            while (areaM2[ch] < st.MinDrainageAreaM2 && receiver[ch] != ch)
            {
                ch = receiver[ch];
            }
            double bed = Math.Max(st.FillSlopeRatio * Math.Max(slope[ch], 0.0), MinFillSlope);
            cellSlope[c] = ch == c ? bed : FloodplainSlopeFactor * bed;
        }
        return cellSlope;
    }

    /// <summary>
    /// 산사태 둑을 놓고, 둑과 선상지 뒤의 웅덩이를 강이 실어 온 흙으로 메웁니다.
    /// uplift: (N,) 땅이 솟는 속도 [m/yr]. runoffEff: (N,) 유효 유출 [m/yr]. exclude: 둑을 놓지 않을 칸 (선상지).
    /// 설정이 없거나 구면 그래프(행성 지도)이면 아무것도 바꾸지 않습니다. 행성 지도의 칸은 둑보다 훨씬 큽니다.
    /// </summary>
    public static DamResult Apply(
        CellGraph graph, double[] z, SolverResult result, double[] uplift, double[] runoffEff, bool[] isOutlet, Config cfg,
        bool[]? exclude = null)
    {
        int n = graph.NCells;
        if (z.Length != n || result.Receiver.Length != n || uplift.Length != n || runoffEff.Length != n || isOutlet.Length != n)
        {
            throw new ArgumentException(
                $"z, result, uplift, runoffEff, isOutlet 는 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이: z {z.Length}, "
                + $"receiver {result.Receiver.Length}, uplift {uplift.Length}, runoffEff {runoffEff.Length}, isOutlet {isOutlet.Length}");
        }
        if (exclude is not null && exclude.Length != n)
        {
            throw new ArgumentException($"exclude 는 칸 수 ({n},) 와 같은 길이여야 합니다. 받은 길이 {exclude.Length}");
        }
        double[] zOut = (double[])z.Clone();
        bool[] dam = new bool[n];
        bool[] fill = new bool[n];
        var sites = new List<object?>();
        var diag = new OrderedDictionary<string, object?>
        {
            ["applied"] = false,
            ["n_candidates"] = 0L,
            ["expected_dams"] = 0.0,
            ["expected_lakes"] = 0.0,
            ["n_dams"] = 0L,
            ["n_lakes"] = 0L,
            ["n_fill_cells"] = 0L,
        };
        DamSettings? st = Settings(cfg);
        if (st is null || graph.Kind != "flat")
        {
            return new DamResult { Z = zOut, Dam = dam, Fill = fill, Sites = sites, Diag = diag };
        }
        diag["applied"] = true;
        long[] rcv = result.Receiver;
        double[] area = result.DrainageArea;
        double[] q = result.Discharge;
        double[] qs = result.SedimentFlux;
        double[] slope = result.Slope;
        double[] sCrit = result.SCritSurface;
        int[] nbr = graph.Nbr;
        double[] pos = graph.Pos;
        double dx = graph.Spacing;

        // --- 1. 물길 칸마다, 곧바로 내려오는 산비탈의 넓이 = 상류 넓이 − 물길인 위쪽 칸들의 상류 넓이
        double[] local = (double[])area.Clone();
        for (int c = 0; c < n; c++)
        {
            long r = rcv[c];
            if (r != c && area[c] >= st.MinDrainageAreaM2)
            {
                local[r] -= area[c];
            }
        }
        // 유량으로 되짚은 상류 넓이: 유역 밖에서 들어온 물이 있는 강은 유역 안의 넓이보다 큽니다.
        double runoffSum = 0.0;
        double areaSum = 0.0;
        for (int c = 0; c < n; c++)
        {
            runoffSum += runoffEff[c] * graph.Area[c];
            areaSum += graph.Area[c];
        }
        double meanRunoff = runoffSum / areaSum;

        // --- 2. 칸마다 기대 수를 구하고, 해시로 둑이 있는 칸·높이·나이를 뽑습니다
        double[] edges = HeightEdges(st);
        double[] weights = new double[HeightBins];
        double[] binExpected = new double[CatchmentEdgesKm2.Length];
        long nCandidates = 0;
        double expectedDams = 0.0;
        double expectedLakes = 0.0;
        var picked = new List<(ulong Priority, int Cell, double Height, double Age)>();
        for (int c = 0; c < n; c++)
        {
            long r = rcv[c];
            if (r == c || rcv[r] == r || area[c] < st.MinDrainageAreaM2 || (exclude is not null && exclude[c]))
            {
                continue;
            }
            bool steepSide = false;
            for (int s = 0; s < CellGraph.NSlots && !steepSide; s++)
            {
                int v = nbr[(c * CellGraph.NSlots) + s];
                steepSide = v >= 0 && v != r && z[v] > z[c] && sCrit[v] > 0.0 && slope[v] >= st.MinSlopeRatio * sCrit[v];
            }
            if (!steepSide || !(sCrit[c] > 0.0))
            {
                continue;
            }
            nCandidates++;
            double hillslope = Math.Max(local[c], graph.Area[c]);
            double rock = Math.Max(uplift[c], 0.0) * hillslope;
            // 비탈 길이 = 산비탈 넓이 ÷ (칸 양옆 2 × 칸 간격). 가장 큰 산사태는 한 변이 비탈 길이인 네모쯤입니다.
            double hillLength = hillslope / (2.0 * dx);
            double maxLandslide = NpMath.Clip(hillLength * hillLength, 4.0 * LandslideMinAreaM2, LandslideMaxAreaM2);
            double catchment = meanRunoff > 0.0 ? Math.Max(area[c], q[c] / meanRunoff) : area[c];
            // 둑 높이에 드는 부피를 이 자리의 지표에서 잽니다: 가장 낮은 둑과, 가장 큰 산사태가 곧은 V 자 골짜기에 만들 높이.
            double h0 = st.MinHeightM;
            double largest = VolumeAreaCoefficient * NpMath.Power(maxLandslide, VolumeAreaExponent);
            double hTop = Math.Cbrt(1.5 * st.FaceSlope * sCrit[c] * largest);
            double h1 = NpMath.Clip(hTop, 2.0 * h0, st.MaxHeightM);
            double v0 = MoundVolume(graph, z, rcv, c, h0, st.FaceSlope);
            double v1 = MoundVolume(graph, z, rcv, c, h1, st.FaceSlope);
            if (!(v0 > 0.0 && v1 > v0))
            {
                continue;
            }
            double power = Math.Log(v1 / v0) / Math.Log(h1 / h0);
            double m = ExpectedDams(rock, maxLandslide, v0, h0, power, slope[c], catchment, dx, st, edges, weights);
            if (!(m > 0.0))
            {
                continue;
            }
            expectedDams += m;
            // 호수가 남아 있을 기대 수: 물 부피 ≈ (넘치는 높이)³ ÷ (3 × 옆 비탈 경사 × 강 경사) 를 흙 흐름으로 나눈 햇수 동안만 호수.
            double sediment = Math.Max(qs[c], 0.0) * (catchment / Math.Max(area[c], 1.0));
            for (int b = 0; b < HeightBins; b++)
            {
                if (weights[b] > 0.0)
                {
                    double hs = SpillFraction * Math.Sqrt(edges[b] * edges[b + 1]);
                    double water = hs * hs * hs / (3.0 * sCrit[c] * Math.Max(slope[c], 1e-6));
                    // 메운 바닥이 강바닥 경사의 FillSlopeRatio 배로 상류까지 이어지므로, 다 메우는 데 드는 흙은 물 부피 ÷ (1 − 비)입니다.
                    double fillYr = sediment > 0.0 ? water / (1.0 - st.FillSlopeRatio) / sediment : double.PositiveInfinity;
                    expectedLakes += weights[b] * (st.PersistenceYr > 0.0 ? Math.Min(fillYr / st.PersistenceYr, 1.0) : 0.0);
                }
            }
            int bin = CatchmentEdgesKm2.Length - 1;
            while (bin > 0 && catchment / 1.0e6 < CatchmentEdgesKm2[bin])
            {
                bin--;
            }
            binExpected[bin] += m;
            if (Hashing.HashUnit(st.Seed, StreamSite, c) >= Math.Min(m, 1.0))
            {
                continue;
            }
            // 높이: 구간을 기대 수의 몫대로 고르고, 구간 안에서는 로그 간격으로 잇습니다.
            double target = Hashing.HashUnit(st.Seed, StreamHeight, c) * m;
            double height = edges[HeightBins];
            double acc = 0.0;
            for (int b = 0; b < HeightBins; b++)
            {
                if (weights[b] > 0.0 && target < acc + weights[b])
                {
                    double t = (target - acc) / weights[b];
                    height = edges[b] * NpMath.Power(edges[b + 1] / edges[b], t);
                    break;
                }
                acc += weights[b];
            }
            double age = Hashing.HashUnit(st.Seed, StreamAge, c) * st.PersistenceYr;
            picked.Add((Hashing.Hash3(st.Seed, StreamOrder, c), c, height, age));
        }
        picked.Sort((a, b) =>
        {
            int cmp = a.Priority.CompareTo(b.Priority);
            return cmp != 0 ? cmp : a.Cell.CompareTo(b.Cell);
        });

        // --- 3. 둔덕을 얹습니다 (먼저 뽑힌 둑이나 선상지와 겹치면 놓지 않습니다)
        var built = new List<(int Cell, double Height, double Age, double Volume, long NRaised, List<int> Cells)>();
        var mound = new List<(int Cell, double Top)>();
        foreach ((_, int a, double h, double age) in picked)
        {
            mound.Clear();
            double volume = MoundVolume(graph, z, rcv, a, h, st.FaceSlope, mound);
            bool overlap = mound.Count == 0;
            foreach ((int i, _) in mound)
            {
                overlap |= dam[i] || (exclude is not null && exclude[i]);
            }
            if (overlap)
            {
                continue;
            }
            var cells = new List<int>(mound.Count);
            foreach ((int i, double top) in mound)
            {
                zOut[i] = top;
                dam[i] = true;
                cells.Add(i);
            }
            built.Add((a, h, age, volume, cells.Count, cells));
        }

        // --- 4. 웅덩이를 흙으로 메운 지표와, 메운 칸의 묶음
        (double[] zFill, bool[] raised) = Depressions.FillAlluvial(
            zOut, nbr, graph.Dist, isOutlet, FillSlopes(rcv, area, slope, st));
        int[] group = new int[n];
        Array.Fill(group, -1);
        var capacity = new List<double>();
        var members = new List<List<int>>();
        var stack = new Stack<int>();
        for (int c0 = 0; c0 < n; c0++)
        {
            if (!raised[c0] || group[c0] >= 0 || !(zFill[c0] - zOut[c0] > PondedM))
            {
                continue;
            }
            int id = capacity.Count;
            double cap = 0.0;
            var list = new List<int>();
            group[c0] = id;
            stack.Push(c0);
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                list.Add(c);
                cap += (zFill[c] - zOut[c]) * graph.Area[c];
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = nbr[(c * CellGraph.NSlots) + s];
                    if (v >= 0 && raised[v] && group[v] < 0 && zFill[v] - zOut[v] > PondedM)
                    {
                        group[v] = id;
                        stack.Push(v);
                    }
                }
            }
            capacity.Add(cap);
            members.Add(list);
        }

        // --- 5. 둑마다 그 뒤의 웅덩이를 찾아, 나이 동안 들어온 흙만큼 먼 쪽부터 메웁니다
        bool[] keepLake = new bool[n];
        bool[] used = new bool[capacity.Count];
        long nLakes = 0;
        foreach ((int a, double h, double age, double volume, long nRaised, List<int> cells) in built)
        {
            int best = -1;
            foreach (int i in cells)
            {
                for (int s = 0; s < CellGraph.NSlots; s++)
                {
                    int v = nbr[(i * CellGraph.NSlots) + s];
                    if (v >= 0 && group[v] >= 0 && !used[group[v]] && (best < 0 || capacity[group[v]] > capacity[best]
                        || (capacity[group[v]] == capacity[best] && group[v] < best)))
                    {
                        best = group[v];
                    }
                }
            }
            double catchment = meanRunoff > 0.0 ? Math.Max(area[a], q[a] / meanRunoff) : area[a];
            double sediment = Math.Max(qs[a], 0.0) * (catchment / Math.Max(area[a], 1.0));
            double delivered = sediment * age;
            double cap = best >= 0 ? capacity[best] : 0.0;
            double filled = cap > 0.0 ? Math.Min(delivered / cap, 1.0) : 1.0;
            if (best >= 0)
            {
                used[best] = true;
                if (delivered < cap)
                {
                    // 둑에서 먼 칸부터 메웁니다. 들어온 흙이 다 떨어지면 나머지 칸은 물이 고인 채로 둡니다.
                    List<int> list = members[best];
                    double[] far = new double[list.Count];
                    for (int k = 0; k < far.Length; k++)
                    {
                        double ex = pos[list[k] * 3] - pos[a * 3];
                        double ey = pos[(list[k] * 3) + 1] - pos[(a * 3) + 1];
                        far[k] = (ex * ex) + (ey * ey);
                    }
                    int[] order = new int[list.Count];
                    for (int k = 0; k < order.Length; k++)
                    {
                        order[k] = k;
                    }
                    Array.Sort(order, (x, y) =>
                    {
                        int cmp = far[y].CompareTo(far[x]);
                        return cmp != 0 ? cmp : list[x].CompareTo(list[y]);
                    });
                    double acc = 0.0;
                    bool lake = false;
                    foreach (int k in order)
                    {
                        int c = list[k];
                        double v = (zFill[c] - zOut[c]) * graph.Area[c];
                        // 흙이 한 번 모자라면 그보다 둑에 가까운 칸은 모두 호수로 둡니다 (메운 땅과 호수가 뒤섞이지 않게).
                        if (lake || acc + v > delivered)
                        {
                            keepLake[c] = true;
                            lake = true;
                        }
                        else
                        {
                            acc += v;
                        }
                    }
                    nLakes += lake ? 1 : 0;
                }
            }
            sites.Add(new OrderedDictionary<string, object?>
            {
                ["cell"] = (long)a,
                ["height_m"] = h,
                ["age_yr"] = age,
                ["volume_m3"] = volume,
                ["n_raised"] = nRaised,
                ["catchment_km2"] = catchment / 1.0e6,
                ["dbi"] = Dbi(catchment, h, volume),
                ["sediment_m3_per_yr"] = sediment,
                ["capacity_m3"] = cap,
                ["filled_fraction"] = filled,
                ["discharge_m3_per_yr"] = q[a],
                ["z_m"] = z[a],
                ["pos"] = new List<object?> { pos[a * 3], pos[(a * 3) + 1], pos[(a * 3) + 2] },
            });
        }
        long nFill = 0;
        for (int c = 0; c < n; c++)
        {
            if (raised[c] && !keepLake[c] && zFill[c] > zOut[c])
            {
                zOut[c] = zFill[c];
                fill[c] = true;
                nFill++;
            }
        }
        var byCatchment = new List<object?>();
        foreach (double v in binExpected)
        {
            byCatchment.Add(v);
        }
        var edgeList = new List<object?>();
        foreach (double v in CatchmentEdgesKm2)
        {
            edgeList.Add(v);
        }
        diag["n_candidates"] = nCandidates;
        diag["expected_dams"] = expectedDams;
        diag["expected_lakes"] = expectedLakes;
        diag["n_dams"] = (long)built.Count;
        diag["n_lakes"] = nLakes;
        diag["n_fill_cells"] = nFill;
        diag["catchment_edges_km2"] = edgeList;
        diag["expected_by_catchment"] = byCatchment;
        return new DamResult { Z = zOut, Dam = dam, Fill = fill, Sites = sites, Diag = diag };
    }
}
