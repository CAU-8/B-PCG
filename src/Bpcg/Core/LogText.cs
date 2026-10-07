using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bpcg.Core;

/// <summary>
/// 실행 기록 줄을 사람이 읽기 쉽게 쓰는 도우미 (CLAUDE.md 4.4). 줄 머리(`[2단계] 솔버 끝` 등)는
/// 스튜디오(progress.py)·엔진(StageProgress.cs)·시험이 읽으므로 이 도우미는 머리 뒤의 내용에만 씁니다.
/// </summary>
public static class LogText
{
    /// <summary>천 단위 쉼표를 넣은 정수 (1572864 → "1,572,864").</summary>
    public static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>0~1 비율을 백분율로 (0.6225 → "62.2%").</summary>
    public static string Pct(double fraction) => (fraction * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";

    /// <summary>불합격 항목 목록: 없으면 "없음", 있으면 "2개 (a, b)".</summary>
    public static string Failed(IEnumerable<object?> failed)
    {
        var items = failed.Select(f => f?.ToString() ?? "").ToList();
        return items.Count == 0 ? "없음" : $"{items.Count}개 ({string.Join(", ", items)})";
    }

    /// <summary>가장자리 이름 (south → 남쪽). 없으면 "없음".</summary>
    public static string Side(object? side) => side switch
    {
        null => "없음",
        "south" => "남쪽",
        "north" => "북쪽",
        "east" => "동쪽",
        "west" => "서쪽",
        _ => side.ToString() ?? "",
    };

    /// <summary>회랑의 긴 축 이름 (north → 남북, east → 동서).</summary>
    public static string Axis(string axis) => axis switch
    {
        "north" => "남북",
        "east" => "동서",
        _ => axis,
    };

    /// <summary>동굴 층 번호 → 이름 (0 → 아래층, 1 → 위층).</summary>
    public static string CaveLevel(int k) => k switch
    {
        0 => "아래층",
        1 => "위층",
        _ => $"{k}층",
    };
}
