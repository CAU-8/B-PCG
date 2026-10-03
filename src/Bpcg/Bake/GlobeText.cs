using System.Collections.Generic;
using Bpcg.IO;

namespace Bpcg.Bake;

/// <summary>
/// 지구본 굽기(bake.globe)가 globe.json 에 적는 색표·범주 색·설명 글 (src/bpcg/bake/globe_text.py).
/// 글의 {..} 는 굽는 때의 값으로 채웁니다(PyFormat.Format).
/// </summary>
public static class GlobeText
{
    public static readonly string[] Groups = ["지형", "판·지각", "해양", "기후", "물", "지질", "동굴"];

    public static readonly int[][] Viridis =
        [[68, 1, 84], [71, 45, 123], [59, 82, 139], [44, 114, 142], [33, 145, 140], [40, 174, 128], [94, 201, 98], [173, 220, 48], [253, 231, 37]];

    public static readonly int[][] Magma =
        [[0, 0, 4], [29, 17, 71], [81, 18, 124], [131, 38, 129], [183, 55, 121], [231, 82, 99], [252, 137, 97], [254, 196, 136], [252, 253, 191]];

    public static readonly int[][] YlGnBu =
        [[255, 255, 217], [237, 248, 177], [198, 233, 180], [126, 205, 187], [64, 181, 196], [29, 144, 192], [34, 93, 168], [36, 51, 146], [8, 29, 88]];

    public static readonly int[][] RdYlBu =
        [[165, 0, 38], [222, 64, 46], [249, 142, 82], [254, 212, 133], [254, 255, 192], [209, 236, 244], [142, 194, 220], [79, 129, 186], [49, 54, 149]];

    public static readonly int[][] RdBuR =
        [[5, 48, 97], [42, 113, 178], [107, 172, 209], [194, 221, 236], [247, 246, 246], [251, 204, 180], [228, 128, 102], [186, 40, 50], [103, 0, 31]];

    public static readonly int[][] PuOrR =
        [[45, 0, 75], [95, 58, 145], [153, 144, 191], [207, 207, 229], [247, 247, 246], [254, 213, 159], [238, 155, 57], [189, 97, 9], [127, 59, 8]];

    public static readonly (double T, int[] Rgb)[] OceanStops =
    [
        (-1.0, [8, 29, 72]), (-0.75, [16, 52, 110]), (-0.5, [28, 84, 150]), (-0.25, [55, 125, 190]), (-0.05, [120, 180, 225]),
    ];

    public const double ShoreStopM = -0.5;
    public static readonly int[] OceanShoreRgb = [165, 215, 240];

    public static readonly (double T, int[] Rgb)[] LandStops =
    [
        (0.0, [58, 120, 62]), (0.1, [110, 160, 80]), (0.25, [190, 190, 110]), (0.45, [170, 130, 80]), (0.7, [130, 95, 70]),
        (0.9, [200, 195, 190]), (1.0, [250, 250, 250]),
    ];

    public static readonly string[] RockLabels = ["충적층", "사암", "셰일", "석회암", "화강암", "화산암", "점판암", "편암", "편마암", "대리암", "규암", "흙"];

    public static readonly int[] NoneRgb = [232, 232, 228];

    public static readonly (int Id, string Label, int[] Rgb)[] BoundaryCats =
        [(0, "없음", NoneRgb), (1, "수렴", [215, 48, 39]), (2, "발산", [44, 123, 182]), (3, "변환", [230, 171, 2])];

    public static readonly (int Id, string Label, int[] Rgb)[] CrustCats = [(0, "해양 지각", [62, 84, 130]), (1, "대륙 지각", [196, 164, 112])];

    public static readonly (int Id, string Label, int[] Rgb)[] TemplateCats =
        [(0, "탄산염 탁상지", [222, 206, 150]), (1, "습곡충상대", [190, 90, 60]), (2, "기반암 + 화산호", [110, 60, 120])];

    public static readonly (int Id, string Label, int[] Rgb)[] CaveCats =
        [(0, "없음", NoneRgb), (1, "아래층 동굴", [0, 160, 190]), (2, "위층 동굴", [235, 120, 20]), (3, "두 층 모두", [190, 40, 150])];

    public static readonly (int Id, string Label, int[]? Rgb)[] RiverCats =
    [
        (0, "강 아님", null), (1, "작은 강 (10 m³/s 미만)", [120, 190, 235]), (2, "강 (10~100 m³/s)", [60, 150, 225]),
        (3, "큰 강 (100~1000 m³/s)", [30, 100, 210]), (4, "아주 큰 강 (1000 m³/s 이상)", [10, 50, 170]),
    ];

    public const int NoDataId = 255;
    public const string OceanNoGeologyLabel = "바다 (지질 계산 안 함)";
    public const string AreaNoteLand = "넓이 몫(%)은 육지 넓이에 대한 몫입니다 (바다 칸은 빼고 셈).";
    public const string AreaNotePlanet = "넓이 몫(%)은 행성 표면 전체에 대한 몫입니다.";

    public const string OceanGeologyNote =
        " 바다 칸에는 파이프라인이 지질을 계산하지 않고 자리 채움 값만 두므로, 지구본에서는 "
        + "'바다 (지질 계산 안 함)' 범주(회색)로 칠합니다.";

    public const string LandAreaNote = " 범례의 비율은 육지 넓이에 대한 몫이고, 회색은 지질을 계산하지 않은 바다입니다.";
    public const string PlanetAreaNote = " 범례의 비율은 행성 표면 전체 넓이에 대한 몫입니다.";

    /// <summary>필드 이름 → (표시 이름, 그룹, 설명, 읽는 법).</summary>
    public static readonly Dictionary<string, (string Label, string Group, string Desc, string How)> Text = new()
    {
        ["elevation"] = ("고도", "지형",
            "해수면(0 m)을 기준으로 잰 땅의 높이입니다. {z_land} 바다는 바닥까지의 깊이를 음수로 적은 "
            + "해저 지형입니다. 원래 계산은 L0 칸(면 가운데 칸 한 변 약 {l0_km:.1f} km)에서 했고, "
            + "지구본 칸 하나는 가장 가까운 L0 칸 4개의 평균입니다.",
            "파란색은 바다이고 진할수록 깊습니다(가장 진한 파랑 ≈ {dmax:.0f} m 깊이). 0 m 에서 색이 "
            + "끊기고, 육지는 초록(낮은 땅) → 누런 갈색 → 갈색 → 흰색(≈ {hmax:.0f} m 이상) 순으로 "
            + "높아집니다."),
        ["relief_m"] = ("칸 안 기복", "지형",
            "L0 칸 하나(면 가운데 칸 한 변 약 {l0_km:.1f} km) 안에 숨어 있는 골짜기 바닥과 능선의 "
            + "높이 차입니다. 칸이 커서 격자로는 보이지 않는 산과 골짜기가 얼마나 거친지를 Hack 법칙으로 "
            + "추정한 값입니다. 바다 칸은 계산하지 않아 값이 없습니다.{hydro_note}",
            "검정·보라는 평평한 땅이고, 빨강·주황을 지나 밝은 노랑으로 갈수록 칸 안 기복이 큽니다"
            + "(밝은 노랑 ≈ {hi:.0f} m 이상). 회색은 바다(값 없음)입니다."),
        ["plate_id"] = ("판", "판·지각",
            "행성 표면을 나눈 판의 번호입니다. 판마다 다른 방향으로 움직이고, 판끼리 만나는 곳에서 "
            + "산맥·해구·해령이 생깁니다. 이 행성에는 판이 {n_plates}개 있습니다.",
            "판마다 다른 색을 칠했습니다. 색에는 크기나 순서의 뜻이 없고, 같은 색이면 같은 판입니다. "
            + "범례의 비율은 그 판이 덮은 행성 표면 넓이의 몫입니다."),
        ["boundary_type"] = ("판 경계", "판·지각",
            "판과 판이 만나는 경계 칸의 종류입니다. 수렴은 두 판이 부딪치는 곳(산맥·해구), 발산은 "
            + "벌어지는 곳(해령·열곡), 변환은 옆으로 엇갈려 미끄러지는 곳입니다.",
            "빨간 선은 수렴, 파란 선은 발산, 노란 선은 변환 경계이고, 옅은 회백색은 판 안쪽입니다. "
            + "가는 선이 끊기지 않도록 지구본 칸 안에 경계 칸이 하나라도 있으면 경계로 칠했습니다."),
        ["crust_type"] = ("지각 종류", "판·지각",
            "땅 껍질(지각)의 종류입니다. 대륙 지각은 두껍고 가벼워 높이 뜨고, 해양 지각은 얇고 "
            + "무거워 바다 밑에 깔립니다. 대륙 지각의 가장자리(대륙붕)는 물에 잠겨 있기도 합니다.",
            "짙은 청회색은 해양 지각, 황갈색은 대륙 지각입니다. 해안선이 아니라 지각의 경계라서 "
            + "고도 지도의 해안선과 조금 다릅니다."),
        ["uplift_mm_per_yr"] = ("융기 속도", "판·지각",
            "판 운동이 땅을 밀어 올리는 빠르기 U 입니다(가라앉으면 음수). 묶음의 m/yr 값에 1000 을 "
            + "곱해 mm/yr 로 적었습니다. 침식과 맞서 산의 높이를 정하는 입력값입니다.",
            "{how}"),
        ["ocean_age_myr"] = ("해양저 나이", "해양",
            "해령에서 해양 지각이 만들어진 뒤 지난 시간입니다(Myr = 백만 년). 오래된 해양저는 식어서 "
            + "무거워지고 더 깊이 가라앉습니다. 대륙 지각에는 값이 없습니다.",
            "빨강은 막 생긴 젊은 해양저(해령 근처)이고, 노랑·하늘색을 지나 짙은 파랑으로 갈수록 "
            + "오래되었습니다(짙은 파랑 ≈ {hi:.0f} Myr). 회색은 대륙 지각(값 없음)입니다."),
        ["temperature_c"] = ("연평균 기온", "기후",
            "1년 평균 지표 기온입니다. 적도에서 극으로 갈수록, 땅이 높을수록(고도 1 km 에 약 "
            + "{lapse:.1f} °C) 낮아집니다. 바다 위는 해수면 높이의 기온입니다.",
            "{how}"),
        ["precip_m_per_yr"] = ("연강수량", "기후",
            "1년 동안 내리는 비와 눈을 물 높이로 적은 양입니다(1 m/yr = 1000 mm/yr). 적도 비구름 띠와 "
            + "중위도 편서풍 띠에 노이즈를 더해 정했습니다. 강물과 침식의 원천입니다.",
            "옅은 노랑은 메마른 곳이고, 초록·청록을 지나 짙은 남색으로 갈수록 비가 많습니다"
            + "(옅은 노랑 ≈ {lo:.2f} m/yr 이하, 짙은 남색 ≈ {hi:.2f} m/yr 이상)."),
        ["discharge_log10_m3_per_s"] = ("강 유량", "물",
            "칸을 지나 하류로 흘러가는 물의 양(유량 Q)입니다. 상류 전체의 유출을 모은 값이라 큰 "
            + "강일수록 큽니다. 값은 초당 세제곱미터(m³/s)의 상용로그입니다: 0 은 1 m³/s, 3 은 "
            + "1000 m³/s 입니다. 바다 칸은 값이 없습니다.",
            "짙은 보라는 작은 물줄기이고, 청록·초록을 지나 노랑으로 갈수록 큰 강입니다(노랑 ≈ "
            + "10^{hi:.1f} m³/s 이상). 로그 눈금이라 1 차이가 유량 10 배입니다. 회색은 바다(값 "
            + "없음)입니다."),
        ["surface_rock"] = ("지표 암석", "지질",
            "지표에 드러난 암석의 종류입니다. 암석에 따라 침식되는 빠르기, 비탈이 버티는 가파름, "
            + "물이 스미는 정도가 다르고, 석회암·대리암처럼 물에 녹는 암석에만 동굴이 생깁니다."
            + "{ocean_note}",
            "암석마다 정해진 색을 칠했습니다(엔진의 지층 단면과 같은 색). 석회암은 밝은 크림색, "
            + "대리암은 흰색입니다.{area_note}"),
        ["geology_template"] = ("지질 틀", "지질",
            "칸마다 고른 땅속 지층 구조의 틀입니다. 탄산염 탁상지는 사암·석회암·셰일이 평평하게 "
            + "쌓인 안정한 땅, 습곡충상대는 판이 부딪쳐 지층이 접히고 밀려 올라간 산맥, 기반암 + "
            + "화산호는 화강암 위를 섭입대 화산암이 덮은 곳입니다.{ocean_note}",
            "세 가지 색이 세 틀입니다. 습곡충상대와 화산호는 수렴 경계를 따라 띠로 나타나고, "
            + "{land}나머지 대부분은 탄산염 탁상지입니다.{area_note}"),
        ["caves"] = ("동굴", "동굴",
            "칸 안에 동굴 층이 있는지 보여 줍니다. 아래층 동굴은 지금 지하수면 높이에서 물에 잠긴 채 "
            + "자라는 통로이고, 위층 동굴은 강이 골짜기를 더 파 지하수면이 내려가기 전, 옛 지하수면 "
            + "높이에 남은 마른 통로입니다. 물에 녹는 암석(석회암·대리암)에만 생깁니다.",
            "옅은 회백색은 동굴 없음, 청록은 아래층만, 주황은 위층만, 자주는 두 층 모두 있는 칸입니다. "
            + "동굴 칸은 드물어서 놓치지 않도록, 지구본 칸 안의 L0 칸 가운데 하나라도 동굴이 있으면 "
            + "표시했습니다. 동굴이 있는 L0 칸은 {n_cave_cells}개, 지구본 칸으로는 {n_globe_cells}개"
            + "입니다.{marker_note}"),
    };

    public const string MarkerNote =
        " 칸이 작아 화면에서 놓치기 쉬우므로, 동굴이 있는 지구본 칸마다 같은 색 점을 화면 크기 그대로 "
        + "덧찍었습니다(점 크기는 실제 넓이와 상관없습니다).";

    /// <summary>고도 설명의 육지 문장 (elevation_land_text).</summary>
    public static string ElevationLandText(string zName, double? meanFraction)
    {
        if (zName == "z_mean_m")
        {
            string frac = meanFraction is double mf ? $"{PyFormat.General(mf)} 배" : "일부";
            return $"육지는 칸 평균 지표 고도(z_mean_m = 골짜기 바닥 z_m + 칸 안 기복의 {frac})입니다.";
        }
        return $"육지는 L0 칸의 골짜기 바닥 고도({zName})입니다. 이 묶음에는 칸 평균 지표(z_mean_m)가 "
            + "없어 칸 안 기복을 더하지 않았으므로, 실제 평균 지표보다 조금 낮습니다.";
    }

    /// <summary>칸 안 기복 설명에 덧붙이는 물 이야기 (relief_hydro_note).</summary>
    public static string ReliefHydroNote(double? meanFraction, double? gapM, double? valleyRatio)
    {
        var parts = new List<string>();
        if (gapM is double gap)
        {
            string frac = meanFraction is double mf ? PyFormat.General(mf) : "mean_fraction";
            parts.Add($" L0 에서는 지하수면이 골짜기 바닥에 붙어 있어(둘의 차 가운데값 {PyFormat.General(gap, 2)} m), "
                + $"지표에서 지하수면까지의 칸 평균 깊이는 거의 {frac} × 칸 안 기복입니다.");
        }
        if (valleyRatio is double vr)
        {
            parts.Add(" 동굴 위층의 높이를 정하는 골짜기 깊이(valley_depth_m)도 L0 에서는 칸 안 기복과 거의 "
                + $"같습니다(가운데값 비 {PyFormat.Fixed(vr, 2)}).");
        }
        if (parts.Count > 0)
        {
            parts.Add(" 그래서 두 값은 따로 싣지 않고 이 레이어로 읽습니다.");
        }
        return string.Concat(parts);
    }

    /// <summary>융기 속도 읽는 법 (uplift_how).</summary>
    public static string UpliftHow(double loEnd, double hiEnd, bool hasNeg, bool hasPos)
    {
        if (!hasNeg && !hasPos)
        {
            return "모든 칸의 융기 속도가 0 이라 흰색 한 가지입니다.";
        }
        string o = "흰색은 거의 0(안정한 땅)입니다.";
        o += hasPos
            ? $" 주황 → 짙은 갈색으로 갈수록 빨리 솟습니다(짙은 갈색 ≈ {PyFormat.General(hiEnd, 2)} mm/yr 이상)."
            : " 솟는 곳이 없어 주황·갈색은 쓰지 않습니다.";
        o += hasNeg
            ? $" 보라는 가라앉는 곳입니다(진한 보라 ≈ {PyFormat.General(loEnd, 2)} mm/yr 이하)."
            : " 가라앉는 곳이 없어 보라색은 쓰지 않습니다.";
        if (hasNeg && hasPos)
        {
            o += " 솟는 쪽과 가라앉는 쪽은 눈금 크기가 달라 0 을 기준으로 따로 읽습니다.";
        }
        return o;
    }

    /// <summary>기온 읽는 법 (temperature_how).</summary>
    public static string TemperatureHow(double loEnd, double hiEnd, double tMin, double tMax)
    {
        string o = "흰색이 0 °C(어는점)입니다.";
        o += loEnd < 0.0
            ? $" 파랑이 진할수록 춥습니다(가장 진한 파랑 ≈ {PyFormat.Fixed(loEnd, 0)} °C 이하)."
            : $" 영하인 곳이 없어 파랑은 쓰지 않습니다(가장 추운 곳 ≈ {PyFormat.Fixed(tMin, 0)} °C).";
        o += hiEnd > 0.0
            ? $" 빨강이 진할수록 덥습니다(가장 진한 빨강 ≈ {PyFormat.Fixed(hiEnd, 0)} °C 이상)."
            : $" 영상인 곳이 없어 빨강은 쓰지 않습니다(가장 더운 곳 ≈ {PyFormat.Fixed(tMax, 0)} °C).";
        return o;
    }
}
