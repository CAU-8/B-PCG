using System;
using Bpcg.Core;

namespace Bpcg.Planet;

/// <summary>
/// 칸마다 바이옴(기후대마다 다른 식물 군락)을 고릅니다. 연평균 기온으로 띠를 나누고, 그 띠 안에서 물이 넉넉한지로 숲·초원·사막을 가릅니다.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 두 값인가.</b> 같은 비가 와도 더운 곳은 물이 빨리 증발해 더 마릅니다. 휘터커 도표(Ricklefs 2008, 그림 5.5)에서 바이옴 경계가
/// 비스듬한 까닭입니다. 그래서 강수만 보지 않고, 강수를 잠재 증발산(그 기온에서 물이 넉넉할 때 증발할 수 있는 양)으로 나눈
/// 건조 지수 AI = P / PET 를 씁니다. AI 가 1 이면 내린 비만큼 증발할 수 있고, 0.5 면 증발할 수 있는 양의 절반만 내립니다.
/// 잠재 증발산은 <see cref="Climate.GenerateClimate"/> 와 같은 식(0 °C 위 기온 1 °C 마다 climate.pet_per_degc_m_per_yr, 바닥값
/// climate.pet_base_m_per_yr)을 최종 기온에 다시 씁니다.
/// </para>
/// <para>
/// <b>경계값.</b> 기온 띠(−10, −5, 5, 20 °C)는 휘터커 도표의 연평균 기온 범위(툰드라 −15~−5, 북방림 −5~5, 온대 5~20, 열대·아열대 20 °C 위)를
/// 따랐고, −10 °C 아래는 눈과 얼음이 1년 내내 남는 빙원으로 따로 둡니다. 건조 지수 경계(0.5, 1.0, 1.2)는 이 프로젝트가 정한 근사입니다.
/// 0.5 는 유엔환경계획(UNEP)의 반건조와 건조 아습윤의 경계(0.5)와 같습니다. 1.0 과 1.2 는 휘터커 도표에서 숲이 시작되는 강수를
/// 그 기온의 잠재 증발산으로 나눈 값에 가깝게 잡았습니다.
/// </para>
/// <para>
/// 계절이 없는 기후(연평균만 있음)라 지중해성 관목림, 몬순, 건기가 뚜렷한 사바나는 기온·강수로만 근사합니다. 바다 칸은 <see cref="Water"/> 입니다.
/// </para>
/// </remarks>
public static class Biomes
{
    /// <summary>바다 (바이옴 없음).</summary>
    public const byte Water = 0;

    /// <summary>빙원: 연평균 기온 −10 °C 아래. 눈과 얼음이 1년 내내 남음.</summary>
    public const byte Ice = 1;

    /// <summary>툰드라: −10 ~ −5 °C. 이끼와 키 작은 풀.</summary>
    public const byte Tundra = 2;

    /// <summary>북방림(타이가): −5 ~ 5 °C 이고 AI ≥ 0.5. 바늘잎 나무 숲.</summary>
    public const byte BorealForest = 3;

    /// <summary>온대 활엽수림: 5 ~ 20 °C 이고 AI ≥ 1.0.</summary>
    public const byte TemperateForest = 4;

    /// <summary>초원: 5 ~ 20 °C 이고 0.5 ≤ AI &lt; 1.0, 또는 −5 ~ 5 °C 이고 AI &lt; 0.5 (찬 초원).</summary>
    public const byte Grassland = 5;

    /// <summary>관목지: 5 ~ 20 °C 이고 AI &lt; 0.5. 키 작은 떨기나무와 메마른 땅.</summary>
    public const byte Shrubland = 6;

    /// <summary>사막: 20 °C 위이고 AI &lt; 0.5.</summary>
    public const byte Desert = 7;

    /// <summary>사바나: 20 °C 위이고 0.5 ≤ AI &lt; 1.2. 풀밭에 나무가 드문드문.</summary>
    public const byte Savanna = 8;

    /// <summary>열대우림: 20 °C 위이고 AI ≥ 1.2.</summary>
    public const byte TropicalRainforest = 9;

    /// <summary>바이옴 번호 수 (바다 포함).</summary>
    public const int Count = 10;

    /// <summary>이보다 추우면 빙원 (°C).</summary>
    public const double IceMaxC = -10.0;

    /// <summary>이보다 추우면 툰드라 (°C).</summary>
    public const double TundraMaxC = -5.0;

    /// <summary>이보다 추우면 북방림 띠 (°C).</summary>
    public const double BorealMaxC = 5.0;

    /// <summary>이보다 추우면 온대 띠, 이상이면 열대·아열대 띠 (°C).</summary>
    public const double TemperateMaxC = 20.0;

    /// <summary>건조 지수가 이보다 작으면 마른 땅(찬 초원·관목지·사막).</summary>
    public const double DryAridity = 0.5;

    /// <summary>온대 띠에서 건조 지수가 이 이상이면 숲.</summary>
    public const double TemperateForestAridity = 1.0;

    /// <summary>열대·아열대 띠에서 건조 지수가 이 이상이면 열대우림.</summary>
    public const double RainforestAridity = 1.2;

    /// <summary>번호 순서의 한국어 이름.</summary>
    public static readonly string[] Names =
        ["바다", "빙원", "툰드라", "북방림", "온대 활엽수림", "초원", "관목지", "사막", "사바나", "열대우림"];

    /// <summary>번호 순서의 영어 키 (스튜디오·엔진이 씀).</summary>
    public static readonly string[] Keys =
        ["water", "ice", "tundra", "boreal_forest", "temperate_forest", "grassland", "shrubland", "desert", "savanna", "tropical_rainforest"];

    /// <summary>번호 순서의 색 (r, g, b, 0~255). 지구본·스튜디오·게임이 같은 색을 씁니다.</summary>
    public static readonly byte[] ColorRgb =
    [
        44, 93, 138,
        238, 243, 247,
        169, 179, 154,
        47, 94, 69,
        63, 143, 58,
        185, 196, 106,
        168, 154, 104,
        227, 201, 138,
        200, 180, 74,
        22, 99, 43,
    ];

    /// <summary>칸 하나의 바이옴.</summary>
    /// <param name="temperatureC">연평균 기온 (°C, 고도 반영).</param>
    /// <param name="precipMPerYr">연강수량 (m/yr).</param>
    /// <param name="petMPerYr">잠재 증발산 (m/yr, 0 보다 큼).</param>
    /// <param name="ocean">바다 칸인지.</param>
    public static byte Classify(double temperatureC, double precipMPerYr, double petMPerYr, bool ocean)
    {
        if (ocean)
        {
            return Water;
        }
        if (temperatureC < IceMaxC)
        {
            return Ice;
        }
        if (temperatureC < TundraMaxC)
        {
            return Tundra;
        }
        double ai = precipMPerYr / petMPerYr;
        if (temperatureC < BorealMaxC)
        {
            return ai >= DryAridity ? BorealForest : Grassland;
        }
        if (temperatureC < TemperateMaxC)
        {
            return ai >= TemperateForestAridity ? TemperateForest : ai >= DryAridity ? Grassland : Shrubland;
        }
        return ai >= RainforestAridity ? TropicalRainforest : ai >= DryAridity ? Savanna : Desert;
    }

    /// <summary>
    /// 칸마다 바이옴을 고릅니다. 잠재 증발산은 최종 기온으로 <see cref="Climate.GenerateClimate"/> 와 같은 식을 다시 씁니다.
    /// 칸마다 따로 셈하므로 나눠 돌려도 결과가 같습니다.
    /// </summary>
    public static byte[] Classify(double[] temperatureC, double[] precipMPerYr, bool[] ocean, Config cfg)
    {
        ArgumentNullException.ThrowIfNull(temperatureC);
        ArgumentNullException.ThrowIfNull(precipMPerYr);
        ArgumentNullException.ThrowIfNull(ocean);
        ArgumentNullException.ThrowIfNull(cfg);
        int n = temperatureC.Length;
        if (precipMPerYr.Length != n || ocean.Length != n)
        {
            throw new ArgumentException(
                $"바이옴 입력의 칸 수가 다릅니다: 기온 {n}, 강수 {precipMPerYr.Length}, 바다 {ocean.Length}. 같은 격자의 필드를 주세요.");
        }
        double petPer = cfg.F("climate.pet_per_degc_m_per_yr");
        double petBase = cfg.F("climate.pet_base_m_per_yr");
        byte[] biome = new byte[n];
        Parallelism.For(0, n, c =>
        {
            double pet = (petPer * Math.Max(temperatureC[c], 0.0)) + petBase;
            biome[c] = Classify(temperatureC[c], precipMPerYr[c], pet, ocean[c]);
        });
        return biome;
    }
}
