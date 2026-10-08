"""파이프라인 기록 줄 → 단계별 진행률 (스튜디오 '진행' 탭).

`bpcg all` 은 단계마다 "[1단계] …", "[2단계] 솔버 끝 …", "[굽기] …" 같은 줄을 찍습니다.
STAGES 표가 단계 순서·무게·끝 줄 무늬를 한 곳에 둡니다. 줄이 들어오면 아직 끝나지 않은
단계부터 앞으로만 찾아, 끝 줄 무늬가 맞는 첫 단계까지를 끝낸 것으로 봅니다. 그래서 같은 줄
("[2단계] 솔버 끝")이 행성과 히어로에서 두 번 나와도 차례대로 맞고, 기록을 남기지 않는 단계
(지각 세기 한계를 끈 경우 등)는 다음 단계 줄이 오면 함께 끝납니다.

무게는 노트북 프로필(out/earth_v2)에서 잰 대략 시간 [s] 입니다. 같은 프로필로 끝난 지난 실행이
있으면 그 실행에서 잰 단계 시간을 무게로 씁니다(진행률과 남은 시간이 더 맞음). 크기(히어로 칸 수
등)가 다르면 jobs 가 비율로 맞춰 넘기고, 지난 실행에 없던 단계는 기본 무게에 (잰 시간 / 기본 무게)
비율을 곱해 씁니다.
단계 안 진행: 솔버는 "솔버 반복 k" 줄로 k / 반복 상한, 기록이 없는 긴 단계는 지난 실행 시간 대비
지난 시간으로 채웁니다(둘 다 0.95 를 넘지 않음). 진행률은 줄어들지 않습니다.
남은 시간: 지난 실행 기준이면 단계마다 (무게 − 지난 시간). 도는 단계가 지난번 시간을 넘기면 반복
비율이 있으면 지금 속도로 남은 반복을, 없으면 '계산 중'(None)으로 둡니다.
"""

import re
import time
from dataclasses import dataclass

ITERATION = re.compile(r"^솔버 반복 (\d+):")
SUB_CAP = 0.95  # 단계 안 진행의 상한 (끝 줄이 와야 1)

# 눈에 띄게 보여 줄 기록 줄: (무늬, 경고 문구). 진행·개요 탭 위에 노란 띠로 나옵니다.
# 한 줄에는 처음 맞는 무늬 하나만 씁니다(더 좁은 무늬를 앞에).
WARNINGS: tuple[tuple[str, str], ...] = (
    (
        r"^\[히어로\] 행성에서 히어로 자리를 찾지 못해.*(spacing_m|칸은 상한)",
        "히어로 영역 설정(profile.hero.size_m·spacing_m)이 맞지 않아 히어로를 만들지 못했습니다. "
        "한 변은 칸 간격의 3배 이상, 한 변 칸 수는 상한 이하여야 합니다. 아래 기록 줄의 값을 "
        "보고 고치세요.",
    ),
    (
        r"^\[히어로\] 행성에서 히어로 자리를 찾지 못해",
        "히어로를 행성에 놓지 못해 평면 히어로(가짜 경계조건)로 바꿨습니다. 이유는 아래 기록 "
        "줄에 있습니다. 바다에서 먼 육지가 모자라서라면 profile.hero.size_m 를 줄이거나 시드를 "
        "바꿔 보세요.",
    ),
    (
        r"^\[2단계\] 솔버 끝: .*수렴 False",
        "솔버가 반복 상한 안에 수렴하지 않았습니다. 반복 상한(landscape.max_flow_iterations, "
        "profile.hero.max_flow_iterations)을 늘리거나 고정 칸 수를 확인하세요.",
    ),
    (
        r"^Traceback \(most recent call last\)",
        "파이썬 오류가 났습니다. 아래 기록의 마지막 줄들을 보세요.",
    ),
)


@dataclass(frozen=True)
class Stage:
    key: str
    label: str
    phase: str  # 준비 | 행성 | 히어로 | 굽기 | 그림
    weight: float  # 노트북 프로필 대략 시간 [s]
    end: str  # 이 무늬가 맞는 줄이 나오면 단계가 끝남 (re.search)
    detail: str  # 무엇을 하는지 한 줄
    iter_key: str | None = None  # 솔버 반복 상한 설정 키 (부분 진행용)
    planet_only: bool = False  # 평면 히어로(--flat)에서는 건너뜀
    figures: bool = False  # 그림 스크립트 단계


# 단계 표: 순서, 이름, 단계 묶음, 무게 [s], 끝 줄 무늬, 설명. 기록 줄 형식이 바뀌면 여기만 고칩니다.
_PLANET = {"planet_only": True}  # 평면 히어로에서 건너뛰는 단계
STAGES: tuple[Stage, ...] = (
    Stage("prepare", "준비 (모듈·설정 읽기)", "준비", 3.0, r"^\[행성\] 시작",
          "파이썬 모듈과 numba 함수를 읽고 설정을 합칩니다"),
    Stage("planet_materials", "판·지각·해수면·융기·기후 (거친 격자)", "행성", 0.5,
          r"^\[1단계\] 거친 격자",
          "거친 격자에서 판, 지각, 해양저 깊이, 해수면, 융기, 강수를 정합니다", **_PLANET),
    Stage("planet_transfer", "L0 격자로 옮기기", "행성", 1.6, r"^\[1단계\] L0 면당",
          "재료를 흔들린 L0 격자(약 20 km)로 옮기고 해수면을 다시 맞춥니다", **_PLANET),
    Stage("planet_geology", "지질 (층 기둥·습곡)", "행성", 0.2, r"^\[1단계\] 지질 템플릿",
          "칸마다 지질 템플릿과 층 기둥을 만듭니다", **_PLANET),
    Stage("planet_strength", "지각 세기 한계 (첫 풀이)", "행성", 15.4,
          r"^\[2단계\] 지각 세기 한계",
          "한 번 풀어 평균 지표를 구하고, 너무 오른 곳의 융기를 줄입니다", **_PLANET),
    Stage("planet_solver", "L0 솔버 (정상상태)", "행성", 3.6, r"^\[2단계\] 솔버 끝",
          "강 법칙과 임계 경사로 정상상태 지형을 풉니다",
          iter_key="landscape.max_flow_iterations", **_PLANET),
    Stage("planet_fans", "선상지", "행성", 0.6, r"^\[3단계\] 선상지",
          "산지 앞 경사가 꺾이는 곳에 선상지를 얹고 물길을 다시 잇습니다", **_PLANET),
    Stage("planet_water", "기복·물·흙·지하수·동굴", "행성", 1.4, r"^\[4단계\]",
          "칸 안 기복, 강·호수, 지표 암석, 흙, 지하수면, 동굴 층을 정합니다", **_PLANET),
    Stage("planet_score", "점수표", "행성", 0.6, r"^\[점수표\]",
          "검사 지표와 지구 비교 지표를 잽니다", **_PLANET),
    Stage("planet_save", "행성 묶음·텍스처 쓰기", "행성", 4.0, r"^\[행성\] 묶음을 썼습니다",
          "planet/ 에 필드, 그래프, 면 텍스처, 점수표를 씁니다", **_PLANET),
    Stage("hero_find", "히어로 자리 찾기", "히어로", 1.5,
          r"^\[히어로\] 후보:|^\[히어로\] 행성에서 히어로 자리를 찾지 못해",
          "융기 기울기·탄산염·기복·건조 점수로 가까이 볼 유역을 고릅니다", **_PLANET),
    Stage("hero_setup", "히어로 격자·지질·경계조건", "히어로", 1.0,
          r"^\[히어로\] 출구|^\[평면 히어로\] \d",
          "평면 격자에 L0 값을 표본하고 출구 높이와 들어오는 물을 정합니다"),
    Stage("hero_warm", "히어로 거친 격자 먼저 풀기", "히어로", 1.4,
          r"^\[2단계\] 거친 격자 먼저",
          "4배 거친 격자에서 먼저 풀어 물길망을 잡습니다"),
    Stage("hero_solver", "히어로 솔버 (L2)", "히어로", 28.8, r"^\[2단계\] 솔버 끝",
          "같은 법칙으로 히어로 유역을 정상상태까지 풉니다",
          iter_key="profile.hero.max_flow_iterations"),
    Stage("hero_fans", "히어로 선상지", "히어로", 0.6, r"^\[3단계\] 선상지",
          "선상지를 얹고 물길을 다시 잇습니다"),
    Stage("hero_water", "히어로 물·흙·지하수·동굴", "히어로", 1.2, r"^\[4단계\]",
          "강·호수, 지표 암석, 흙, 지하수면, 동굴 층과 입구를 정합니다"),
    Stage("hero_save", "히어로 점수표·묶음 쓰기", "히어로", 2.5,
          r"^\[히어로\] 묶음을 썼습니다",
          "hero/ 에 필드, 강 구간, 점수표를 씁니다"),
    Stage("bake_choose", "굽기: 3D 샘플·회랑 고르기", "굽기", 3.0, r"^\[굽기\] 회랑",
          "3D 샘플 함수를 만들고 동굴 입구가 많은 강가 회랑을 고릅니다"),
    Stage("bake_heightmap", "굽기: 높이맵", "굽기", 0.7, r"^\[굽기\] 높이맵",
          "회랑 지표 높이맵(강바닥 깎기·노이즈 포함)을 씁니다"),
    Stage("bake_detail", "굽기: 프랙탈 디테일 (보기용)", "굽기", 1.5, r"^\[굽기\] 프랙탈 디테일",
          "히어로 격자가 못 그린 50 m 아래 거칠기를 이어 붙인 지표를 씁니다 ([detail])"),
    Stage("bake_caves", "굽기: 물·동굴 메시", "굽기", 11.5, r"^\[굽기\] 동굴 메시",
          "수면·지하수면 높이맵과 동굴 벽 메시(caves.glb)를 씁니다"),
    Stage("bake_strata", "굽기: 재질 부피", "굽기", 2.5, r"^\[굽기\] 재질 부피",
          "땅속 재질 부피(strata.u8: 암석·물·공기)를 씁니다"),
    Stage("bake_finish", "굽기: 입구·manifest", "굽기", 0.5, r"^\[굽기\] 끝",
          "동굴 입구 위치와 엔진 좌표 manifest 를 씁니다"),
    Stage("globe", "지구본 굽기", "굽기", 1.5, r"^\[(지구본\] 끝|전체\] 끝)",
          "행성 전체를 엔진 지구본 장면용 면 격자로 다시 담습니다 (globe/)", **_PLANET),
    Stage("figures", "그림 그리기", "그림", 25.0, r"^그림을 썼습니다",
          "C# 콘솔 figures results 로 결과 그림(PNG)을 그립니다", figures=True),
)  # fmt: skip

STAGE_INDEX = {s.key: i for i, s in enumerate(STAGES)}


def _measured_ratio(weights: dict[str, float]) -> float:
    """지난 실행에서 잰 시간 / 기본 무게 (잰 단계들의 합 비). 지난 실행에 없던 단계에 곱합니다."""
    default = {s.key: s.weight for s in STAGES}
    keys = [k for k in weights if k in default and k != "prepare"]
    base = sum(default[k] for k in keys)
    got = sum(max(float(weights[k]), 0.0) for k in keys)
    if base <= 0 or got <= 0:
        return 1.0
    return got / base


class ProgressTracker:
    """기록 줄을 받아 단계 상태와 전체 진행률을 셉니다.

    flat: 평면 히어로 실행이면 행성 단계를 건너뜀. figures: 그림 단계를 할지.
    max_iter: {설정 키: 반복 상한} (솔버 부분 진행). weights: {단계 키: [s]} 지난 실행에서 잰
    시간 (없으면 STAGES 의 기본 무게). clock: 시간 함수 (시험에서 바꿈).
    """

    def __init__(
        self,
        flat: bool = False,
        figures: bool = True,
        max_iter: dict[str, int] | None = None,
        weights: dict[str, float] | None = None,
        clock=time.time,
    ):
        self.clock = clock
        self.max_iter = dict(max_iter or {})
        self.measured = bool(weights)
        ratio = _measured_ratio(weights or {})
        self.stages = []
        for s in STAGES:
            w = float((weights or {}).get(s.key, s.weight * ratio))
            skip = (flat and s.planet_only) or (s.figures and not figures)
            reason = ""
            if skip and s.planet_only:
                reason = "평면 히어로라 행성이 없습니다"
            elif skip:
                reason = "그림을 그리지 않게 골랐습니다"
            self.stages.append(
                {
                    "key": s.key,
                    "label": s.label,
                    "phase": s.phase,
                    "detail": s.detail,
                    "weight": max(w, 0.05),
                    "status": "skipped" if skip else "pending",
                    "started": None,
                    "finished": None,
                    "seconds": None,
                    "sub": 0.0,
                    "note": reason,
                    "result": "",
                }
            )
        self._end = [re.compile(s.end) for s in STAGES]
        self._warn = [(re.compile(p), msg) for p, msg in WARNINGS]
        self.warnings: list[dict] = []
        self.cur = 0
        self._iter_seen: set[str] = set()  # "솔버 반복" 줄이 온 단계 (시간 대신 반복 비율을 씀)
        self.t_start: float | None = None
        self.t_end: float | None = None
        self.state = "pending"  # pending | running | done | failed | cancelled
        self._best = 0.0
        self._advance_skipped()

    # ------------------------------------------------------------ 상태 바꾸기
    def _advance_skipped(self) -> None:
        while self.cur < len(self.stages) and self.stages[self.cur]["status"] == "skipped":
            self.cur += 1

    def _begin_current(self, t: float) -> None:
        self._advance_skipped()
        if self.cur < len(self.stages) and self.stages[self.cur]["status"] == "pending":
            st = self.stages[self.cur]
            st["status"] = "running"
            st["started"] = t

    def start(self, t: float | None = None) -> None:
        t = self.clock() if t is None else t
        self.t_start = t
        self.state = "running"
        self._begin_current(t)

    def skip(self, key: str, reason: str) -> None:
        """단계 하나를 건너뜀으로 둡니다 (예: 행성이 없어 그림을 못 그림)."""
        st = self.stages[STAGE_INDEX[key]]
        if st["status"] in ("pending", "running"):
            st["status"] = "skipped"
            st["note"] = reason
            st["sub"] = 0.0
        self._advance_skipped()

    def _complete_through(self, i: int, t: float, line: str) -> None:
        for k in range(self.cur, i + 1):
            st = self.stages[k]
            if st["status"] == "skipped":
                continue
            if st["started"] is None:
                st["started"] = t
            st["status"] = "done"
            st["finished"] = t
            st["seconds"] = max(t - st["started"], 0.0)
            st["sub"] = 1.0
            if st["note"].startswith("반복 "):
                st["note"] = ""  # 결과 줄(result)에 마지막 반복 수가 있음
            if k == i:
                st["result"] = line.strip()
        self.cur = i + 1
        self._begin_current(t)

    def feed(self, line: str, t: float | None = None) -> bool:
        """기록 한 줄. 단계가 바뀌었으면 True."""
        t = self.clock() if t is None else t
        text = line.strip()
        changed = self._check_warning(text)
        if not text or self.cur >= len(self.stages):
            return changed
        if m := ITERATION.match(text):
            st = self.stages[self.cur]
            key = STAGES[self.cur].iter_key
            top = self.max_iter.get(key or "", 0)
            if key and top > 0 and st["status"] == "running":
                it = int(m.group(1))
                st["sub"] = max(st["sub"], min(it / top, SUB_CAP))
                st["note"] = f"반복 {it} / 상한 {top}"
                self._iter_seen.add(st["key"])
            return changed
        for i in range(self.cur, len(self.stages)):
            if self.stages[i]["status"] == "skipped":
                continue
            if self._end[i].search(text):
                self._complete_through(i, t, text)
                return True
        return changed

    def _check_warning(self, text: str) -> bool:
        for pat, msg in self._warn:
            if pat.search(text) and all(w["message"] != msg for w in self.warnings):
                stage = self.stages[min(self.cur, len(self.stages) - 1)]["label"]
                self.warnings.append({"message": msg, "line": text, "stage": stage})
                return True
        return False

    def finish(self, state: str, t: float | None = None, error: str = "") -> None:
        """끝냄. state: done (남은 단계 모두 끝) | failed | cancelled (지금 단계에 표시)."""
        t = self.clock() if t is None else t
        self.t_end = t
        self.state = state
        if state == "done":
            last = max(
                (i for i, s in enumerate(self.stages) if s["status"] != "skipped"), default=-1
            )
            if last >= self.cur:
                self._complete_through(last, t, "")
            return
        if self.cur < len(self.stages):
            st = self.stages[self.cur]
            st["status"] = "failed" if state == "failed" else "cancelled"
            st["finished"] = t
            if st["started"] is not None:
                st["seconds"] = max(t - st["started"], 0.0)
            if error:
                st["note"] = error

    # ------------------------------------------------------------ 읽기
    def _sub_now(self, st: dict, t: float) -> float:
        """단계 안 진행 0..1. 반복 줄이 오는 솔버는 반복 비율, 아니면 지난 실행 시간 대비."""
        sub = st["sub"]
        timed = st["key"] not in self._iter_seen
        if self.measured and timed and st["status"] == "running" and st["started"] is not None:
            sub = max(sub, min((t - st["started"]) / st["weight"], 0.9))
        return min(sub, SUB_CAP) if st["status"] == "running" else sub

    def fraction(self, t: float | None = None) -> float:
        """전체 진행률 0..1 (줄어들지 않음)."""
        t = self.clock() if t is None else t
        if self.state == "done":
            return 1.0
        total = sum(s["weight"] for s in self.stages if s["status"] != "skipped")
        if total <= 0:
            return 0.0
        got = 0.0
        for s in self.stages:
            if s["status"] == "done":
                got += s["weight"]
            elif s["status"] == "running":
                got += s["weight"] * self._sub_now(s, t)
        self._best = max(self._best, min(got / total, 0.999))
        return self._best

    def eta_seconds(self, t: float | None = None) -> tuple[float | None, str]:
        """남은 시간 [s] 과 근거 ('지난 실행 기준' | '지금까지 속도 기준'). 모르면 None."""
        t = self.clock() if t is None else t
        if self.state != "running" or self.t_start is None:
            return None, ""
        if self.measured:
            left = 0.0
            for s in self.stages:
                if s["status"] == "pending":
                    left += s["weight"]
                elif s["status"] == "running":
                    spent = max(t - (s["started"] or t), 0.0)
                    if spent <= s["weight"]:
                        left += s["weight"] - spent
                    elif s["key"] in self._iter_seen and s["sub"] > 0.02:
                        left += spent * (1.0 - s["sub"]) / s["sub"]  # 지금 반복 속도로 남은 반복
                    else:
                        return None, "이 단계가 지난 실행보다 오래 걸리는 중"
            return left, "지난 실행 기준"
        f = self.fraction(t)
        if f < 0.03:
            return None, ""
        return (t - self.t_start) * (1.0 - f) / f, "지금까지 속도 기준"

    def stage_seconds(self) -> dict[str, float]:
        """끝난 단계의 걸린 시간 {키: [s]} (다음 실행의 무게)."""
        return {s["key"]: s["seconds"] for s in self.stages if s["status"] == "done"}

    def current(self) -> dict | None:
        for s in self.stages:
            if s["status"] == "running":
                return s
        return None

    def snapshot(self, t: float | None = None) -> dict:
        t = self.clock() if t is None else t
        eta, basis = self.eta_seconds(t)
        cur = self.current()
        end = self.t_end if self.t_end is not None else t
        stages = []
        for s in self.stages:
            row = {k: v for k, v in s.items() if k != "weight"}
            if s["status"] == "running" and s["started"] is not None:
                row["seconds"] = max(t - s["started"], 0.0)
            row["sub"] = self._sub_now(s, t)
            stages.append(row)
        return {
            "state": self.state,
            "percent": round(100.0 * self.fraction(t), 1),
            "current": None if cur is None else {"key": cur["key"], "label": cur["label"]},
            "elapsed_s": None if self.t_start is None else max(end - self.t_start, 0.0),
            "eta_s": eta,
            "eta_basis": basis,
            "weights_from": "지난 실행" if self.measured else "기본 무게(노트북 기준)",
            "stages": stages,
            "warnings": list(self.warnings),
        }

    def restore(self, snap: dict) -> None:
        """job.json 의 snapshot 에서 단계 상태를 되살립니다 (끝난 실행 보기용)."""
        by_key = {s["key"]: s for s in snap.get("stages", [])}
        for st in self.stages:
            old = by_key.get(st["key"])
            if old:
                for k in ("status", "started", "finished", "seconds", "sub", "note", "result"):
                    if k in old:
                        st[k] = old[k]
        self.warnings = list(snap.get("warnings") or [])
        self.state = snap.get("state", self.state)
        if self.state == "done":
            self._best = 1.0
