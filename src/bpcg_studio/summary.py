"""스튜디오 '개요'·'그림' 탭: 실행 하나의 요약을 manifest·점수표·job.json 에서 모읍니다.

- 바뀐 값: 실행 설정(manifest 의 config)과 기본 설정의 차이 (params.config_diff). 한쪽에만 있는
  키는 diff_missing 으로 따로, 실행 설정 전체는 config_flat 으로 (매개변수 설명 표).
- 걸린 시간: 묶음 manifest 의 diag.seconds, 회랑 manifest 의 seconds(CORRIDOR_SECONDS_LABELS),
  job.json 의 단계 시간.
- 솔버 수렴: 반복 수·수렴 여부·고정 칸·반복마다 기록(history).
- 점수표: planet/scorecard.json, hero/scorecard.json 을 한 표로. 항목 뜻과 지구 값은 SCORE_TEXT.
  계산하지 못한 검사(값 없음, 불합격)도 줄로 남기고 위 경고에 적습니다.
- 그림: figures/*.png 와 FIGURE_TEXT 의 설명 (무엇인가·어떻게 읽나·볼 점).
"""

import json
from pathlib import Path
from typing import Any

from bpcg_studio.jobs import JOB_FILE, LOG_FILE, _read_json, _tail
from bpcg_studio.names import bare_name
from bpcg_studio.params import config_diff_parts, flat_config

EARTH_OCEAN_FRACTION = 0.708  # Scorecard.cs 의 DefaultThresholds["earth_ocean_fraction"] 과 같음

# 점수표 항목 (planet./hero. 를 뗀 이름) → 이름, 뜻, 지구 값이나 합격 기준(있을 때).
# 뜻은 docs/pipeline.md 12장과 src/Bpcg/Metrics/Scorecard.cs 의 계산을 쉬운 말로 옮긴 것입니다.
SCORE_TEXT: dict[str, dict[str, str]] = {
    "ocean_fraction": {
        "label": "바다 비율",
        "meaning": "행성 넓이 가운데 바다의 몫입니다. 바닷물 양을 정해 두고 낮은 곳부터 채우므로, "
        "이 비율은 입력이 아니라 결과로 나옵니다.",
        "earth": "",  # earth_ocean_fraction() 으로 채움 (점수표 기준과 같은 값)
    },
    "shelf_area": {
        "label": "대륙붕 넓이",
        "meaning": "대륙 지각 위에 있으면서 수심이 200 m 보다 얕은 바다(대륙붕)의 넓이입니다.",
        "earth": "약 2,700만 km² (바다의 약 7%)",
    },
    "hypsometry_bimodal": {
        "label": "고도 분포의 두 봉우리 간격",
        "meaning": "높이마다 넓이를 세면 지구는 육지 높이와 바다 밑 높이 두 곳에 몰립니다(봉우리 "
        "둘). 그 두 봉우리 사이의 간격이고, 1 km 넘게 떨어지면 합격입니다. 지각 두께와 지각평형이 "
        "거의 정하는 값이라 '지구답다'는 근거로 세지 않습니다.",
        "earth": "약 4,500~5,000 m (0~500 m 와 −4,000~−5,000 m)",
    },
    "hack_exponent": {
        "label": "Hack 법칙 지수",
        "meaning": "유역이 넓어질 때 본류가 얼마나 길어지는지입니다. 본류 길이 L = c·A^h (A 는 "
        "유역 넓이) 의 h 이고, h = 0.5 면 넓이가 4배일 때 길이가 2배입니다.",
        "earth": "0.49~0.6",
    },
    "gw_surface_fraction": {
        "label": "지하수가 땅 겉에 닿는 육지 비율",
        "meaning": "지하수면이 땅 겉에서 0.5 m 안까지 올라온 육지의 넓이 비율입니다(습지·강가 "
        "같은 땅).",
        "earth": "0.22~0.32, 열 칸 가운데 두세 칸 (Fan 2013)",
    },
    "river_reach_fraction": {
        "label": "강이 바다·호수·출구에 닿는 비율",
        "meaning": "강 칸에서 물길을 따라 내려가면 바다나 호수, 유역 출구에 닿는 칸의 비율입니다. "
        "1 이면 모든 강이 끊기지 않고 이어집니다.",
        "earth": "기준 1 (100%)",
    },
    "river_backflow": {
        "label": "거꾸로 흐르는 강 칸",
        "meaning": "물을 받는 아래 칸의 수면이 보내는 칸보다 높은 강 칸의 수입니다. 물이 비탈을 "
        "거슬러 오르는 셈이라 0 이어야 합니다.",
        "earth": "기준 0",
    },
    "law_consistency": {
        "label": "경사 법칙과 실제 경사의 차이",
        "meaning": "칸마다 실제 경사가 솔버의 경사 법칙(강과 산비탈이 깎는 양으로 정한 경사)과 "
        "얼마나 다른지 잰 상대 오차의 중앙값입니다. 0.001 은 0.1% 차이입니다. 선상지·후처리 칸, "
        "출구, 지층 경계를 넘는 칸은 뺍니다.",
        "earth": "기준 0.001 미만",
    },
    "water_budget": {
        "label": "물 수지 오차",
        "meaning": "물이 새거나 생기지 않았는지 봅니다. 유량 Q 가 칸마다 (넓이 × 침식에 쓰는 "
        "유출)을 상류부터 더한 값과 맞는지 잰 상대 오차입니다(히어로는 행성에서 들어오는 물을 "
        "더함).",
        "earth": "기준 1e-6 (100만분의 1) 이하",
    },
    "sediment_budget": {
        "label": "퇴적물 수지 오차",
        "meaning": "흙이 새거나 생기지 않았는지 봅니다. 퇴적물 유량 Qs 가 상류의 (융기 × 넓이)를 "
        "더한 값과 맞는지 잰 상대 오차입니다.",
        "earth": "기준 1e-6 (100만분의 1) 이하",
    },
    "rock_consistency": {
        "label": "경사를 만든 암석 = 보이는 암석",
        "meaning": "솔버가 경사를 정할 때 쓴 암석과 지표에 보이는 암석이 같은 칸의 "
        "비율입니다(표본으로 셈). 1 이면 모두 같습니다.",
        "earth": "기준 1 (100%)",
    },
    "cave_in_soluble": {
        "label": "동굴이 녹는 암석 안에 있음",
        "meaning": "동굴 층이 있는 자리(칸마다, 층마다) 가운데 그 높이의 암석이 석회암·대리암인 "
        "비율입니다. 1 이면 모든 동굴이 녹는 암석 안에 있습니다.",
        "earth": "기준 1 (100%)",
    },
    "water_rule_violations": {
        "label": "물 규칙 위반 칸",
        "meaning": "물 규칙을 어긴 칸 수의 합입니다: 지하수면이 땅 위로 솟음, 물 칸의 지하수면이 "
        "수면과 다름, 지하수면이 땅 겉에서 최대 깊이(groundwater.max_depth_m, 기본 300 m)보다 "
        "깊음, 값이 비어 있음(NaN).",
        "earth": "기준 0",
    },
    "grid_alignment": {
        "label": "격자 정렬 지수",
        "meaning": "강이 바둑판 칸의 줄(가로·세로·45°)을 얼마나 따르는지입니다. 강 칸마다 6칸 "
        "아래까지의 방향이 이 줄에서 5° 안인 비율을, 아무 방향으로 흐를 때의 비율(약 22%)로 "
        "나눕니다. 1 이면 격자를 편들지 않습니다.",
        "earth": "기준 1.3 미만",
    },
    "grid_alignment_edge": {
        "label": "격자 정렬 지수 (면 경계 띠)",
        "meaning": "행성 격자(정육면체 여섯 면을 부풀린 공)에서 면 경계 근처만 잰 격자 정렬 "
        "지수입니다. 면 경계에서 바둑판 방향이 꺾이므로 따로 봅니다. 1 이면 격자를 편들지 "
        "않습니다.",
        "earth": "기준 1.3 미만",
    },
    "grid_alignment_center": {
        "label": "격자 정렬 지수 (면 가운데)",
        "meaning": "행성 격자에서 면 가운데만 잰 격자 정렬 지수입니다. 1 이면 격자를 편들지 "
        "않습니다.",
        "earth": "기준 1.3 미만",
    },
    "solver_converged": {
        "label": "솔버 수렴",
        "meaning": "솔버가 반복 상한 안에 멈췄는지입니다. 물길 방향이 바뀐 칸이 0 이고 가장 크게 "
        "바뀐 높이가 멈춤 기준(landscape.stop_dz_m, 기본 0.1 m)보다 작아야 멈춥니다.",
        "earth": "기준 수렴",
    },
    "flat_fraction": {
        "label": "평탄지 비율",
        "meaning": "경사가 0.02(100 m 에 2 m)보다 완만한 육지의 넓이 비율입니다(범람원·분지). "
        "히어로에서만 잽니다.",
        "earth": "보고만 함 (합격선 없음)",
    },
}

# 점수표 종류. 이름(check·emergent·forced)은 CLAUDE.md 2.6 과 docs/glossary.md 7장의 것입니다.
KIND_TEXT = {
    "check": "검사 (check): 꼭 지켜야 하는 규칙",
    "emergent": "결과 (emergent): 입력에 없던 성질, 지구와 견줌",
    "forced": "반쯤 입력 (forced): 입력이 거의 정함, 근거로 쓰지 않음",
}

SECONDS_LABELS: dict[str, str] = {
    "materials_coarse": "거친 격자 재료 (판·지각·해수면·융기·기후)",
    "transfer": "거친 격자에서 행성 지도(L0)로 옮기기",
    "geology": "지질",
    "strength_limit": "지각 세기 한계 (첫 풀이)",
    "stages": "2~4단계 (솔버·선상지·물·흙·지하수·동굴)",
    "scorecard": "점수표",
    "total": "합계",
    "solver": "솔버 (산과 강의 모양 풀기)",
    "fans": "선상지",
    "relief": "기복 보정",
    "climate_final": "기온 다시 계산 (최종 고도)",
    "water": "물 (강·호수)",
    "surface_rock": "지표 암석",
    "soil": "흙",
    "groundwater": "지하수면",
    "caves": "동굴 층",
    "rivers": "강 구간 나누기",
    "sample": "히어로 격자 만들고 행성 값 옮기기",
    "boundary": "히어로 가장자리 조건 (출구·들어오는 물)",
    "find": "히어로 자리 찾기",
    "volume": "3D 샘플 함수 준비",
    "choose": "회랑 고르기",
    "heightmap": "높이맵",
    "strata": "재질 부피",
}

# 회랑(굽기) manifest 의 seconds 는 같은 키라도 뜻이 달라 따로 이름을 붙입니다.
CORRIDOR_SECONDS_LABELS: dict[str, str] = {
    **SECONDS_LABELS,
    "volume": "3D 샘플 함수 준비",
    "choose": "회랑 고르기",
    "heightmap": "지표 높이맵",
    "water": "수면·지하수면 높이맵",
    "cave_mouth": "동굴 입구 구멍 (cave_mouth)",
    "detail": "프랙탈 디테일 높이맵 (보기용 잔무늬)",
    "caves": "동굴 메시 (caves.glb)",
    "strata": "재질 부피 (strata.u8)",
}

# 그림 파일 → 설명 (analysis/figures/render_results.py 가 그리는 그림)
FIGURE_TEXT: dict[str, dict[str, str]] = {
    "planet_elevation_plain.png": {
        "title": "행성 평균 지표 고도 (위경도 지도)",
        "what": "행성 전체의 평균 지표 고도(z_mean_m)를 경도 −180~180°, 위도 −90~90° 지도로 "
        "편 그림입니다. 골짜기 바닥 고도에 칸 안 기복의 절반(기본값)을 더한 값이고, 음영으로 "
        "산맥을 도드라지게 했습니다.",
        "how": "파랑은 바다(아래로 갈수록 깊음), 초록→갈색→흰색은 육지 높이입니다. 오른쪽 "
        "색 막대가 고도 [m] 입니다. 극 근처는 위경도 지도라 옆으로 늘어나 보입니다.",
        "look": "산맥이 판 수렴 경계를 따라 좁은 띠로 서는지, 바다가 해령에서 멀어질수록 "
        "깊어지는지 봅니다. 그림 제목의 '면당 512칸' 은 노트북 프로필 기준 문구입니다.",
    },
    "planet_elevation_faces.png": {
        "title": "같은 지도 + 정육면체 면 경계",
        "what": "위 지도에 정육면체 여섯 면의 경계를 빨간 선으로 겹친 그림입니다.",
        "how": "빨간 선 양쪽에서 산맥·해안선·색이 끊기지 않고 이어지면 면 이음새 문제가 "
        "없는 것입니다.",
        "look": "선을 따라 지형이 계단처럼 어긋나거나, 강·산맥이 선과 나란히 몰리는지 봅니다.",
    },
    "planet_globes.png": {
        "title": "지구본 세 방향",
        "what": "같은 행성을 히어로 유역 쪽, 정육면체 꼭짓점 쪽(세 면이 만나는 곳), 북극 쪽에서 "
        "본 정사영(아주 멀리서 평행하게 내려다본 그림)입니다. 아래 줄은 같은 그림에 면 경계를 "
        "겹쳤습니다.",
        "how": "왼쪽 위 노란 원이 히어로 유역입니다. 가운데 열은 이웃이 7개뿐인 꼭짓점을 정면에서 "
        "봅니다.",
        "look": "꼭짓점과 면 경계에서 지형에 이음새나 별 모양 무늬가 생기지 않았는지 봅니다.",
    },
    "planet_layers.png": {
        "title": "재료 네 장: 판·해양저 나이·강수·융기",
        "what": "왼쪽 위: 판(색)과 경계(빨강 수렴, 파랑 발산, 회색 변환), 검은 선 해안. "
        "오른쪽 위: 해양저 나이 [Myr]. 왼쪽 아래: 연강수량 [m/yr]. 오른쪽 아래: 육지 융기 속도 "
        "[mm/yr] (지각 세기 한계 적용 후).",
        "how": "각 판의 색은 번호일 뿐 뜻이 없습니다. 해양저 나이는 해령에서 0 이고 멀수록 "
        "늙습니다. 강수는 적도와 중위도 띠가 젖고 30° 근처가 마릅니다.",
        "look": "융기가 수렴 경계의 대륙 쪽 좁은 띠에만 생기는지, 해양저 나이에 곧은 쐐기 "
        "무늬(알려진 문제)가 있는지 봅니다.",
    },
    "hypsometry_vs_earth.png": {
        "title": "고도 분포: B-PCG 와 지구 (ETOPO 2022)",
        "what": "고도 250 m 구간마다 행성 넓이 중 몇 % 가 그 고도인지 그린 분포입니다. 주황이 "
        "우리 행성(L0 평균 지표), 파랑이 지구입니다(data/pilot 에 ETOPO 가 있을 때만).",
        "how": "대륙 봉우리(0~1,000 m)와 해양 봉우리(−4,000~−5,000 m) 두 개가 서야 지구답습니다.",
        "look": "봉우리 위치는 지각 두께로 거의 정해지는 '반쯤 입력' 이라 지구다움의 근거로 "
        "세지 않습니다. 육지 봉우리가 너무 뾰족하면 육지가 평평하다는 뜻입니다.",
    },
    "hero_map.png": {
        "title": "히어로 유역 지도",
        "what": "가까이 보여 주려고 고른 유역 하나를 히어로 해상도(노트북 25 m)로 푼 지표입니다. "
        "가로·세로 축은 유역 가운데에서 동·북으로 잰 거리 [km] 입니다.",
        "how": "색은 고도 [m] 입니다. 파란 선은 강과 호수, 노란 칸은 선상지, 분홍 점은 동굴 "
        "입구, 빨간 사각형은 엔진으로 구운 걷는 회랑입니다.",
        "look": "강이 나무 모양으로 모이는지, 산지에서 빠져나오는 곳에 선상지가 있는지, "
        "회랑이 강을 따라 동굴 입구가 많은 곳에 놓였는지 봅니다.",
    },
    "hero_subsurface.png": {
        "title": "히어로 땅속 네 장",
        "what": "왼쪽 위: 지표 암석(범례), 오른쪽 위: 흙 두께 [m], 왼쪽 아래: 지하수면 깊이 [m], "
        "오른쪽 아래: 동굴 층(파랑 잠긴 아래층, 주황 마른 위층, 분홍 입구, 연노랑 녹는 암석).",
        "how": "지표 암석은 솔버가 경사를 정할 때 쓴 바로 그 암석입니다. 흙은 가파른 비탈에서 "
        "0(맨 암반)입니다. 지하수면 깊이는 강가에서 0, 능선 아래에서 깊습니다.",
        "look": "동굴이 석회암·대리암 안에만 있는지, 지하수면이 거의 모든 땅에서 지표에 닿는지"
        "(알려진 문제) 봅니다.",
    },
    "cross_section.png": {
        "title": "땅속 단면 (회랑을 가로지르는 동서 단면)",
        "what": "3D 샘플 함수로 회랑을 동서로 자른 단면입니다. 가로는 동서 거리, "
        "세로는 고도입니다.",
        "how": "색은 암석입니다(베이지 석회암, 회색 셰일, 주황 사암 등). 하늘색은 지상 공기, "
        "검은 곳은 마른 동굴(위층), 밝은 파랑은 물(지하수면 아래 잠긴 동굴·강·호수), 진한 파란 "
        "선은 지하수면입니다.",
        "look": "층이 기울어 드러나는 모습, 아래층 동굴(밝은 파랑 띠)이 지하수면 선 높이나 바로 "
        "아래에 놓였는지, 지표 근처에 큰 빈 곳(알려진 문제)이 없는지 봅니다.",
    },
    "hero_3d.png": {
        "title": "히어로 유역 조감 (3D)",
        "what": "히어로 지표를 고도 과장 없이 비스듬히 내려다본 그림입니다(4칸마다 표본).",
        "how": "파란 칸이 강입니다. 색은 히어로 지도와 같은 고도 색입니다.",
        "look": "산지 사면이 임계 경사(약 35~40°)로 잘려 있는지, 골짜기가 이어지는지 봅니다.",
    },
}
FIGURE_ORDER = list(FIGURE_TEXT)


def earth_ocean_fraction() -> float:
    """점수표가 쓰는 지구 바다 비율 (C# 의 Metrics/Scorecard.cs 기준값 earth_ocean_fraction)."""
    return EARTH_OCEAN_FRACTION


def _seconds_rows(seconds: dict | None, labels: dict[str, str] = SECONDS_LABELS) -> list[dict]:
    rows = []
    for k, v in (seconds or {}).items():
        if isinstance(v, int | float):
            rows.append({"key": k, "label": labels.get(k, k), "seconds": float(v)})
    return rows


def _solver(diag: dict | None, max_iter: int | None) -> dict | None:
    if not diag:
        return None
    s = diag.get("solver") or {}
    hist = s.get("history")
    return {
        "iterations": s.get("iterations"),
        "converged": s.get("converged"),
        "n_frozen": s.get("n_frozen"),
        "seconds": s.get("seconds"),
        "max_iter": max_iter,
        "history": hist if isinstance(hist, list) else [],
        "history_note": "" if isinstance(hist, list) else "기록이 길어 manifest 에 요약만 있습니다",
    }


def _scorecard_rows(planet: dict | None, hero: dict | None) -> list[dict]:
    names: list[str] = []
    for card in (planet or {}, hero or {}):
        for k in card:
            base = k.split(".", 1)[-1]
            if base not in names:
                names.append(base)
    rows = []
    for base in names:
        p = _shown((planet or {}).get(f"planet.{base}"))
        h = _shown((hero or {}).get(f"hero.{base}"))
        if p is None and h is None:
            continue
        ref = p or h
        text = SCORE_TEXT.get(base, {})
        earth = text.get("earth", "")
        if base == "ocean_fraction":
            earth = f"{earth_ocean_fraction():g}"
        rows.append(
            {
                "key": base,
                "label": text.get("label", base),
                "meaning": text.get("meaning", ""),
                "earth": earth,
                "kind": ref.get("kind"),
                "kind_label": KIND_TEXT.get(ref.get("kind"), ref.get("kind") or ""),
                "unit": ref.get("unit", ""),
                "planet": p,
                "hero": h,
            }
        )
    return rows


def _shown(entry: dict | None) -> dict | None:
    """점수표 한 칸을 보일지: 값이 있거나, 값 없이 불합격(계산 실패)이면 보입니다."""
    if not entry:
        return None
    if entry.get("value") is not None or entry.get("pass") is False:
        return entry
    return None


def failed_checks(planet: dict | None, hero: dict | None) -> list[dict]:
    """불합격 검사 [{key, level, label, note, compute_failed}] (솔버 수렴은 따로 경고하므로 뺌)."""
    out = []
    for level, card in (("행성", planet or {}), ("히어로", hero or {})):
        for k, e in card.items():
            if not isinstance(e, dict) or e.get("pass") is not False:
                continue
            base = k.split(".", 1)[-1]
            if base == "solver_converged":
                continue
            note = str(e.get("note") or "")
            out.append(
                {
                    "key": k,
                    "level": level,
                    "label": SCORE_TEXT.get(base, {}).get("label", base),
                    "note": note,
                    "compute_failed": e.get("value") is None or note.startswith("계산 실패"),
                }
            )
    return out


def _manifest(path: Path) -> dict | None:
    return _read_json(path)


def run_summary(run_dir: Path, key: str) -> dict[str, Any]:
    """실행 하나의 개요 (API /api/run)."""
    job = _read_json(run_dir / JOB_FILE)
    pm = _manifest(run_dir / "planet" / "manifest.json")
    hm = _manifest(run_dir / "hero" / "manifest.json")
    cm = _manifest(run_dir / "corridor" / "manifest.json")
    config = (pm or hm or {}).get("config") or {}
    pdiag = ((pm or {}).get("meta") or {}).get("diag") or {}
    hdiag = ((hm or {}).get("meta") or {}).get("diag") or {}
    pinfo = ((pm or {}).get("meta") or {}).get("info") or {}
    site = ((hm or {}).get("meta") or {}).get("site")

    landscape = config.get("landscape") or {}
    profile = config.get("profile") or {}
    l0_iter = landscape.get("max_flow_iterations")
    hero_iter = (profile.get("hero") or {}).get("max_flow_iterations", l0_iter)

    facts: list[dict[str, str]] = []
    prof_name = (pm or hm or {}).get("profile") or profile.get("name")
    facts.append({"label": "프로필", "value": str(prof_name)})
    facts.append({"label": "시드", "value": str((pm or hm or {}).get("seed"))})
    if pm:
        g = pm["graph"]
        n = int(g["shape"][1])
        facts.append(
            {
                "label": "행성 격자 (L0)",
                "value": f"면당 {n}칸 · {g['n_cells']:,}칸 · 약 {g['spacing_m'] / 1000:.1f} km",
            }
        )
        if pinfo.get("ocean_fraction") is not None:
            earth = earth_ocean_fraction()
            facts.append(
                {"label": "바다 비율", "value": f"{pinfo['ocean_fraction']:.3f} (지구 {earth:g})"}
            )
        if pinfo.get("sea_level_m") is not None:
            facts.append(
                {"label": "해수면 (기준 고도 대비)", "value": f"{pinfo['sea_level_m']:.0f} m"}
            )
    if hm:
        g = hm["graph"]
        ny, nx = g["shape"]
        size = nx * g["spacing_m"] / 1000
        facts.append(
            {
                "label": "히어로 유역",
                "value": f"{size:g} × {ny * g['spacing_m'] / 1000:g} km · {g['spacing_m']:g} m · "
                f"{nx}×{ny}칸",
            }
        )
        if site:
            facts.append(
                {
                    "label": "히어로 위치",
                    "value": f"위도 {site['lat_deg']:.2f}°, 경도 {site['lon_deg']:.2f}°",
                }
            )
        else:
            facts.append({"label": "히어로 위치", "value": "평면 히어로 (행성 없음)"})
    if cm:
        c = cm.get("corridor") or {}
        n_ent = (cm.get("caves") or {}).get("n_entrances", "—")
        facts.append(
            {
                "label": "걷는 회랑",
                "value": f"{c.get('length_m', 0):g} × {c.get('width_m', 0):g} m · "
                f"간격 {c.get('voxel_m', 0):g} m · 동굴 입구 {n_ent}",
            }
        )

    figures = []
    fig_dir = run_dir / "figures"
    if fig_dir.is_dir():
        present = {p.name for p in fig_dir.glob("*.png")}
        for name in FIGURE_ORDER + sorted(present - set(FIGURE_ORDER)):
            if name in present:
                figures.append({"file": name, **FIGURE_TEXT.get(name, {"title": name})})
    fig_summary = _read_json(fig_dir / "summary.json") if fig_dir.is_dir() else None

    stages = {}
    for name, diag in (("planet", pdiag), ("hero", hdiag)):
        st = diag.get("stages") or {}
        stages[name] = {
            "water": st.get("water"),
            "caves": st.get("caves"),
            "fans": st.get("fans"),
            "groundwater": {
                k: v
                for k, v in (st.get("groundwater") or {}).items()
                if k in ("shallow_fraction", "median_depth_m")
            },
            "strength_limit": st.get("strength_limit"),
            "warm_start": st.get("warm_start"),
        }

    log_tail = _tail(run_dir / LOG_FILE, 60) if (run_dir / LOG_FILE).exists() else []
    pcard = _read_json(run_dir / "planet" / "scorecard.json")
    hcard = _read_json(run_dir / "hero" / "scorecard.json")
    warnings = _warnings(job, pm, hm, site, pdiag, hdiag, failed_checks(pcard, hcard))
    diff, diff_missing = config_diff_parts(config) if config else ([], [])
    return {
        "key": key,
        "name": run_dir.name,
        "path": str(run_dir),
        "job": job,
        "facts": facts,
        "profile": prof_name,
        "config_digest": (pm or hm or {}).get("config_digest"),
        "git_commit": (pm or hm or {}).get("git_commit"),
        "config_flat": flat_config(config) if config else {},
        "diff": diff,
        "diff_missing": diff_missing,
        "timings": {
            "planet": _seconds_rows(pdiag.get("seconds") or {}),
            "planet_detail": _seconds_rows((pdiag.get("seconds") or {}).get("stages_detail")),
            "hero": _seconds_rows(hdiag.get("seconds") or {}),
            "hero_detail": _seconds_rows((hdiag.get("seconds") or {}).get("stages_detail")),
            "corridor": _seconds_rows((cm or {}).get("seconds"), CORRIDOR_SECONDS_LABELS),
            "job": (job or {}).get("stage_seconds") or {},
        },
        "solver": {
            "planet": _solver(pdiag, l0_iter),
            "hero": _solver(hdiag, hero_iter),
        },
        "stages": stages,
        "scorecard": _scorecard_rows(pcard, hcard),
        "corridor": None
        if cm is None
        else {
            "files": cm.get("files"),
            "rect_local_m": (cm.get("corridor") or {}).get("rect_local_m"),
            "axis": (cm.get("corridor") or {}).get("axis"),
            "apex_kind": (cm.get("corridor") or {}).get("apex_kind"),
            "caves": {
                k: v
                for k, v in (cm.get("caves") or {}).items()
                if isinstance(v, int | float | str) or v is None
            },
            "frame": cm.get("frame"),
        },
        "figures": figures,
        "figure_summary": fig_summary,
        "log_tail": log_tail,
        "levels": {"planet": pm is not None, "hero": hm is not None},
        "warnings": warnings,
    }


def _warnings(
    job, pm, hm, site, pdiag: dict, hdiag: dict, failed: list[dict] | None = None
) -> list[dict]:
    """개요 탭 위에 보여 줄 경고: 실행 기록의 경고 + 묶음에서 다시 확인한 것.

    묶음에서 보는 것: 평면 히어로로 바뀜, 솔버 미수렴, 지각 세기 한계 첫 풀이 미수렴,
    점수표 불합격·계산 실패 (failed_checks).
    """
    out = list(((job or {}).get("progress") or {}).get("warnings") or [])
    seen = {w.get("message") for w in out}

    def add(msg: str, line: str = "") -> None:
        if msg not in seen:
            out.append({"message": msg, "line": line, "stage": ""})
            seen.add(msg)

    if pm and hm and not site and not any("평면 히어로" in (m or "") for m in seen):
        add(
            "행성은 만들었지만 히어로 유역을 행성 위에 놓지 못해, 정해 둔 융기와 출구로 만드는 "
            "평면 히어로로 바꿨습니다. 까닭은 실행 기록의 '[히어로]' 줄에 있습니다. 바다에서 먼 "
            "육지가 모자라서라면 profile.hero.size_m 을 줄이거나 시드를 바꿉니다."
        )
    for name, diag in (("행성", pdiag), ("히어로", hdiag)):
        s = (diag or {}).get("solver") or {}
        if s and s.get("converged") is False:
            add(
                f"{name} 솔버가 반복 상한({s.get('iterations')}회) 안에 수렴하지 않았습니다. "
                "마지막 반복에서도 물길 방향이 바뀌거나 높이가 멈춤 기준(기본 0.1 m)보다 크게 "
                "바뀌었다는 뜻입니다. 반복 상한(행성은 landscape.max_flow_iterations, 히어로는 "
                "profile.hero.max_flow_iterations)을 늘리거나, 아래 '솔버 수렴' 표에서 방향을 "
                "묶은 칸 수를 봅니다."
            )
        sl = ((diag or {}).get("stages") or {}).get("strength_limit") or {}
        if sl.get("applied") and sl.get("first_pass_converged") is False:
            z = sl.get("first_pass_z_mean_max_m")
            n = sl.get("reduced_cells")
            ztxt = f"(그때 가장 높은 평균 지표 {z:,.0f} m)" if isinstance(z, int | float) else ""
            ntxt = f"({n:,}칸)" if isinstance(n, int) else ""
            add(
                f"{name} 지각 세기 한계의 첫 풀이가 반복 상한({sl.get('first_pass_iterations')}회) "
                f"안에 수렴하지 않았습니다{ztxt}. 그래서 덜 풀린 지형을 보고 융기를 줄일 칸을 "
                f"골랐습니다{ntxt}. 산맥 높이가 이상하면 landscape.max_flow_iterations 를 늘려 "
                "다시 돌립니다. '솔버 수렴' 표의 행성 반복 수는 두 번째 풀이의 것입니다."
            )
    if failed:
        names = ", ".join(
            f"{f['level']} {f['label']}" + (" (계산 실패)" if f["compute_failed"] else "")
            for f in failed
        )
        notes = " / ".join(f"{f['key']}: {f['note']}" for f in failed if f["note"])
        add(
            f"점수표 검사 {len(failed)}개가 불합격입니다: {names}. 아래 점수표의 회색 메모 "
            "줄에 받은 값과 기준이 있습니다.",
            notes,
        )
    return out


def figure_path(run_dir: Path, name: str) -> Path:
    """그림 파일 경로 (figures/ 안의 .png 이름만 받음). 없으면 FileNotFoundError.

    이름은 경로 없는 파일 이름 하나여야 합니다(names.bare_name: '/', '\\', ':', 앞의 '.' 막음 —
    윈도우의 'D:x.png' 같은 드라이브 상대 경로도 막습니다).
    """
    if not bare_name(name) or not name.endswith(".png"):
        raise FileNotFoundError(name)
    p = run_dir / "figures" / name
    if not p.is_file():
        raise FileNotFoundError(name)
    return p


def dumps(obj: Any) -> str:
    return json.dumps(obj, ensure_ascii=False)
