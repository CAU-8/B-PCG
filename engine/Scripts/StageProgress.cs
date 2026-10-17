using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bpcg.Engine;

/// <summary>
/// 생성 기록 줄 → 단계별 진행률 (src/bpcg/studio/progress.py 의 STAGES 표를 줄여 옮김).
/// </summary>
/// <remarks>
/// 줄이 들어오면 아직 끝나지 않은 단계부터 앞으로만 찾아, 끝 줄 무늬가 맞는 첫 단계까지를 끝낸 것으로 봅니다.
/// 그래서 같은 줄("[2단계] 솔버 끝")이 행성과 히어로에서 두 번 나와도 차례대로 맞습니다.
/// 무게는 노트북 프로필에서 잰 대략 시간 [s] 이고, 진행률은 끝난 단계 무게의 몫입니다 (줄어들지 않음).
/// </remarks>
public sealed class StageProgress
{
    /// <summary>단계: 이름, 묶음, 무게 [s], 끝 줄 무늬, 평면 히어로에서 건너뛰는지.</summary>
    public sealed record Stage(string Label, string Phase, double Weight, Regex End, bool PlanetOnly);

    private static Stage S(string label, string phase, double weight, string end, bool planetOnly = false) =>
        new(label, phase, weight, new Regex(end, RegexOptions.CultureInvariant), planetOnly);

    public static readonly Stage[] Stages =
    [
        S("판·지각·해수면·융기·기후 정하기 (큰 칸에서 먼저)", "행성", 0.5, @"^\[1단계\] 거친 격자", true),
        S("행성 지도(L0) 칸으로 옮기기", "행성", 1.6, @"^\[1단계\] L0 면당", true),
        S("칸마다 땅속 지층 정하기 (지질)", "행성", 0.2, @"^\[1단계\] 지질 템플릿", true),
        S("산이 너무 높지 않게 융기 줄이기 (지각 세기 한계)", "행성", 15.4, @"^\[2단계\] 지각 세기 한계", true),
        S("산과 강 모양 풀기 (솔버)", "행성", 3.6, @"^\[2단계\] 솔버 끝", true),
        S("산 앞에 흙 부채 쌓기 (선상지)", "행성", 0.6, @"^\[3단계\] 선상지", true),
        S("칸 안 기복·물·흙·지하수·동굴 계산", "행성", 1.4, @"^\[4단계\]", true),
        S("점수표 매기기", "행성", 0.6, @"^\[점수표\]", true),
        S("행성 결과 파일 쓰기", "행성", 4.0, @"^\[행성\] 묶음을 썼습니다", true),
        S("걸을 강 유역 고르기", "히어로 유역", 1.5, @"^\[히어로\] 후보:|^\[히어로\] 행성에서 히어로 자리를 찾지 못해", true),
        S("칸·지층·출구 준비", "히어로 유역", 1.0, @"^\[히어로\] 출구|^\[평면 히어로\] \d"),
        S("큰 칸에서 먼저 풀어 시작 지형 만들기", "히어로 유역", 1.4, @"^\[2단계\] 거친 격자 먼저"),
        S("산과 강 모양 풀기 (솔버)", "히어로 유역", 28.8, @"^\[2단계\] 솔버 끝"),
        S("산 앞에 흙 부채 쌓기 (선상지)", "히어로 유역", 0.6, @"^\[3단계\] 선상지"),
        S("물·흙·지하수·동굴 계산", "히어로 유역", 1.2, @"^\[4단계\]"),
        S("점수표 매기고 결과 파일 쓰기", "히어로 유역", 2.5, @"^\[히어로\] 묶음을 썼습니다"),
        S("3D 샘플 함수 만들기·걸을 띠(회랑) 고르기", "엔진 파일 굽기", 3.0, @"^\[굽기\] 회랑"),
        S("지표 높이맵 쓰기", "엔진 파일 굽기", 0.7, @"^\[굽기\] 높이맵"),
        S("잔 굴곡 더하기 (프랙탈 디테일, 보기용)", "엔진 파일 굽기", 1.5, @"^\[굽기\] 프랙탈 디테일"),
        S("주변 지형·동굴 벽 3D 모양 만들기", "엔진 파일 굽기", 11.5, @"^\[굽기\] 동굴 메시"),
        S("땅속 암석 상자 쓰기 (재질 부피)", "엔진 파일 굽기", 2.5, @"^\[굽기\] 재질 부피"),
        S("동굴 입구·목록 파일 쓰기", "엔진 파일 굽기", 0.5, @"^\[굽기\] 끝"),
        S("지구본 자료 쓰기", "엔진 파일 굽기", 1.5, @"^\[(지구본\] 끝|전체\] 끝)", true),
    ];

    private readonly Stage[] _stages;
    private readonly double _total;
    private int _done;

    public StageProgress(bool flat)
    {
        _stages = flat ? Stages.Where(s => !s.PlanetOnly).ToArray() : Stages;
        _total = _stages.Sum(s => s.Weight);
    }

    /// <summary>끝난 단계 무게의 몫 (0~1).</summary>
    public double Fraction => _done >= _stages.Length ? 1.0 : _stages.Take(_done).Sum(s => s.Weight) / _total;

    /// <summary>지금 도는 단계 이름과 몇 번째인지 (다 끝났으면 "끝").</summary>
    public string Current =>
        _done >= _stages.Length ? "끝" : $"{_stages[_done].Phase} · {_stages[_done].Label} ({_done + 1}/{_stages.Length})";

    /// <summary>기록 줄 하나를 읽습니다. 단계가 끝났으면 true.</summary>
    public bool Feed(string line)
    {
        for (int i = _done; i < _stages.Length; i++)
        {
            if (_stages[i].End.IsMatch(line))
            {
                _done = i + 1;
                return true;
            }
        }
        return false;
    }

    /// <summary>모든 단계를 끝낸 것으로 둡니다 (실행이 끝났을 때).</summary>
    public void Finish() => _done = _stages.Length;

    /// <summary>"12.3 s" 꼴의 시간 글.</summary>
    public static string FormatSeconds(double s) =>
        s < 60.0 ? $"{s:F1} s" : $"{Math.Floor(s / 60.0):F0} 분 {s % 60.0:F0} 초";
}
