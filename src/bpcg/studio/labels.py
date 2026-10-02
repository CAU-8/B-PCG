"""지도 보기의 이름표: 필드마다 한국어 이름·그룹·읽는 법·색표·범주 이름 (views 가 씀).

- FIELD_TEXT: 묶음 필드 이름 → label, group, read(어떻게 읽나), cmap, scale, kind,
  categories, display(표시 단위, 곱할 수), range_mask("nonzero": 추천 색 범위를 0 이 아닌 칸으로
  잡음). label_planet/label_hero, read_planet/read_hero 는 단계마다 뜻이 다를 때 씁니다.
- DERIVED: 묶음에 없지만 읽기 쉬운 계산값 (바다 수심, 지하수면 깊이, 동굴 층 합친 그림 등).
- OVERLAYS: 지도 위에 겹쳐 그리는 것 (해안선, 판 경계, 강, 호수, 선상지, 동굴 입구).
설명(description)과 원래 단위는 core.fields.FIELDS 에서 읽습니다.
"""

from collections.abc import Callable
from typing import Any

import numpy as np

from bpcg.geology import rocks as rk

GROUPS = ("지형", "판·지각", "해양", "기후", "물(강·호수·지하수)", "지질·암석", "동굴", "흙")
ROCK_KO = (
    "충적층", "사암", "셰일", "석회암", "화강암", "화산암",
    "점판암", "편암", "편마암", "대리암", "규암", "흙",
)  # fmt: skip
LEVEL_LABELS = {"planet": "행성 (L0)", "hero": "히어로 유역 (L2)"}
NONE_COLOR = "#e6e9e6"


def hex_color(rgb) -> str:
    r, g, b = (int(c) for c in rgb)
    return f"#{r:02x}{g:02x}{b:02x}"


def _cats(*items: tuple[int, str, str]) -> list[dict]:
    return [{"value": v, "label": label, "color": color} for v, label, color in items]


def bool_cats(no: str, yes: str, color: str) -> list[dict]:
    return _cats((0, no, NONE_COLOR), (1, yes, color))


ROCK_CATS = [
    {"value": i, "label": f"{ROCK_KO[i]} ({rk.ROCK_NAMES[i]})", "color": hex_color(rk.COLOR_RGB[i])}
    for i in range(rk.N_ROCKS)
]

# 필드 이름 → 보기 설명. label·group·read(어떻게 읽나)·cmap·scale·kind·categories·display.
# label_planet/label_hero, read_planet/read_hero 는 단계마다 뜻이 다를 때 씁니다.
FIELD_TEXT: dict[str, dict[str, Any]] = {
    # --- 지형
    "z_mean_m": {
        "label": "평균 지표 고도",
        "group": "지형",
        "cmap": "terrain",
        "read": "골짜기 바닥 고도(z_m)에 칸 안 능선 기복의 일부를 더한 값입니다. 산맥과 대륙 "
        "높이를 볼 때 쓰는 대표 고도입니다. 0 m 에서 바다(파랑)와 육지(초록→갈색→흰색)로 "
        "갈립니다. 지각 세기 한계 때문에 고원은 약 5.5 km 위로 거의 오르지 않습니다.",
    },
    "z_m": {
        "label": "고도",
        "label_planet": "골짜기 바닥 고도",
        "label_hero": "지표 고도",
        "group": "지형",
        "cmap": "terrain",
        "read_planet": "L0 칸(약 20 km) 안에서 강이 흐르는 골짜기 바닥의 높이입니다. 바다 칸은 "
        "해저 고도(음수)입니다. 산맥의 평균 높이는 '평균 지표 고도'를 보세요.",
        "read_hero": "히어로 칸의 지표 높이입니다. 솔버가 강 법칙과 산비탈 임계 경사로 만든 "
        "정상상태 지형에 선상지를 얹은 값입니다. 음영을 켜면 골짜기와 능선이 잘 보입니다.",
    },
    "relief_m": {
        "label": "칸 안 기복",
        "group": "지형",
        "cmap": "magma",
        "read": "L0 칸 하나 안에 있을 능선과 골짜기의 높이 차를 공식(Hack 법칙 + 임계 경사)으로 "
        "추정한 값입니다. 산맥에서 크고 평원과 바다에서 0 에 가깝습니다.",
    },
    "slope": {
        "label": "경사",
        "group": "지형",
        "cmap": "magma",
        "read": "물을 보내는 이웃 칸까지의 경사(높이 차 / 거리)입니다. 0.6~0.8 이면 약 30~40° 로 "
        "산비탈이 버티는 임계 경사에 가깝고, 0.02 아래는 평탄지입니다.",
    },
    "k_s": {
        "label": "강 가파름 k_s",
        "group": "지형",
        "cmap": "viridis",
        "scale": "log",
        "read": "강 경사를 유량으로 보정한 가파름(S·Q^θ)입니다. 융기가 빠르거나 암석이 단단할수록 "
        "큽니다. 데이터 학습 단계에서 진짜 지형의 k_s 와 견줄 값입니다.",
    },
    "s_crit": {
        "label": "임계 경사",
        "group": "지형",
        "cmap": "viridis",
        "read": "지표 암석이 버티는 가장 가파른 산비탈 경사입니다(암석 표 geology.rocks.S_CRIT).",
    },
    "sediment_flux_m3_per_yr": {
        "label": "퇴적물 흐름 Qs",
        "group": "지형",
        "cmap": "ylorbr",
        "scale": "log",
        "read": "1년 동안 이 칸을 지나는 퇴적물 부피입니다(상류의 융기 × 면적 합). 로그 눈금이라 "
        "큰 강줄기를 따라 밝게 이어집니다.",
    },
    "not_steady": {
        "label": "정상상태 아님",
        "group": "지형",
        "kind": "categorical",
        "categories": bool_cats("정상상태", "후처리로 고친 칸", "#d1495b"),
        "read": "선상지를 얹거나 물길을 다시 이어서 고친 칸입니다. 이 칸의 경사는 솔버 법칙을 "
        "정확히 따르지 않습니다(물려받은 지형).",
    },
    "fan": {
        "label": "선상지",
        "group": "지형",
        "kind": "categorical",
        "categories": bool_cats("아님", "선상지", "#e0a526"),
        "read": "강이 산지를 빠져나오며 경사가 갑자기 줄어드는 곳에 쌓은 부채꼴 퇴적 지형입니다.",
    },
    # --- 판·지각
    "plate_id": {
        "label": "판 번호",
        "group": "판·지각",
        "kind": "categorical",
        "read": "같은 색이 한 판입니다. 색은 번호를 가르기만 하고 뜻은 없습니다. 판이 만나는 곳의 "
        "경계 종류는 '판 경계 종류'를 보세요.",
    },
    "boundary_type": {
        "label": "판 경계 종류",
        "group": "판·지각",
        "kind": "categorical",
        "categories": _cats(
            (0, "경계 아님", NONE_COLOR),
            (1, "수렴 (산맥·해구)", "#d1495b"),
            (2, "발산 (해령·열곡)", "#2e86de"),
            (3, "변환 단층", "#8d99a6"),
        ),
        "read": "경계 칸에만 값이 있습니다. 두 판이 다가오면 수렴, 멀어지면 발산, 비껴 지나가면 "
        "변환입니다(plates.boundary_kappa 로 가름).",
    },
    "convergence_m_per_yr": {
        "label": "수렴 속도",
        "group": "판·지각",
        "cmap": "magma",
        "display": ("cm/yr", 100.0),
        "read": "가장 가까운 수렴 경계에서 두 판이 다가오는 속도입니다. 산맥 융기 = 계수 × 수렴 "
        "속도로 산맥 높이를 정합니다.",
    },
    "convergence_kind": {
        "label": "수렴 경계 종류",
        "group": "판·지각",
        "kind": "categorical",
        "categories": _cats(
            (0, "없음", NONE_COLOR),
            (1, "해양-대륙 섭입", "#d1495b"),
            (2, "해양-해양 섭입", "#7b4fd6"),
            (3, "대륙 충돌", "#e08a1e"),
        ),
        "read": "가장 가까운 수렴 경계가 어떤 판끼리 만나는지입니다. 대륙 충돌은 넓은 고원을, "
        "섭입은 좁은 산맥과 화산호를 만듭니다.",
    },
    "subduction_side": {
        "label": "섭입 쪽",
        "group": "판·지각",
        "kind": "categorical",
        "categories": _cats(
            (-1, "섭입판 (아래로)", "#2e86de"),
            (0, "해당 없음·충돌", NONE_COLOR),
            (1, "위판 (위에 얹힘)", "#d1495b"),
        ),
        "read": "섭입 경계에서 이 칸이 위에 얹힌 판인지 아래로 들어가는 판인지입니다. 산맥과 "
        "화산호는 위판 쪽에 생깁니다.",
    },
    "dist_convergent_m": {
        "label": "수렴 경계까지 거리",
        "group": "판·지각",
        "cmap": "viridis",
        "display": ("km", 1e-3),
        "read": "가장 가까운 수렴 경계까지의 대원 거리입니다. 융기 띠의 폭(가우스 σ)과 비교합니다.",
    },
    "dist_divergent_m": {
        "label": "발산 경계까지 거리",
        "group": "판·지각",
        "cmap": "viridis",
        "display": ("km", 1e-3),
        "read": "가장 가까운 발산 경계(해령)까지의 대원 거리입니다.",
    },
    "spreading_m_per_yr": {
        "label": "반확장 속도",
        "group": "판·지각",
        "cmap": "magma",
        "display": ("cm/yr", 100.0),
        "read": "가장 가까운 해령에서 한쪽 판이 멀어지는 속도입니다. 빠를수록 같은 거리에서 "
        "해양저가 젊습니다.",
    },
    "crust_thickness_m": {
        "label": "지각 두께",
        "group": "판·지각",
        "cmap": "viridis",
        "display": ("km", 1e-3),
        "read": "대륙 약 35 km, 해양 약 7 km, 충돌대는 더 두껍습니다. 지각평형으로 기준 고도를 "
        "정합니다(두꺼울수록 높이 뜸).",
    },
    "z_platform_m": {
        "label": "기준 고도 (해수면 정하기 전)",
        "group": "판·지각",
        # 0 m 가 해수면이 아니므로 바다·육지 두 갈래 색(terrain)을 쓰지 않습니다.
        "cmap": "cividis",
        "read": "지각평형과 해양저 나이-수심으로 정한 높이입니다. 0 m 는 해수면이 아닙니다. 이 "
        "값에서 물 부피를 보존하는 해수면(개요의 '해수면 (기준 고도 대비)')을 빼면 바다와 육지가 "
        "갈립니다. 실제 해안은 검은 해안선으로 봅니다.",
    },
    "uplift_m_per_yr": {
        "label": "융기 속도 U",
        "group": "판·지각",
        "cmap": "magma",
        "display": ("mm/yr", 1e3),
        "range_mask": "nonzero",  # 바다(0)가 대부분이라 전체 백분위로는 산맥이 한 색이 됨
        "read": "땅이 솟는 속도입니다(지각 세기 한계 적용 후). 수렴 경계 대륙 쪽 좁은 띠에서 "
        "크고, 나머지는 크라톤 기본 깎임 정도로 작습니다. 음수는 침강입니다. 기본 색 범위는 0 이 "
        "아닌 칸의 2~98% 라 가장 빠른 산맥 몇 %는 맨 위 색입니다(색 범위·로그 눈금으로 바꿈).",
    },
    "exhumation_m": {
        "label": "깎인 두께",
        "group": "판·지각",
        "cmap": "magma",
        "display": ("km", 1e-3),
        "scale": "log",  # 크라톤 0.15 km 와 산맥 수십 km 를 한 지도에서 보려고 로그
        "read": "융기 × 기간으로 정한, 지표 위에서 깎여 나간 암석 두께입니다. 깊이 묻혔던 변성암이 "
        "얼마나 드러나는지를 정합니다. 로그 눈금이고, 0 (바다)은 맨 아래 색입니다.",
    },
    "dist_arc_m": {
        "label": "해구-화산호 거리",
        "group": "판·지각",
        "cmap": "viridis",
        "display": ("km", 1e-3),
        "read": "섭입 위판 쪽에서 해구부터 화산호까지의 거리입니다(섭입판 각도로 정함).",
    },
    # --- 해양
    "is_ocean": {
        "label": "바다",
        "group": "해양",
        "kind": "categorical",
        "categories": bool_cats("육지", "바다", "#2e6fb3"),
        "read": "물 부피를 보존하는 해수면 아래이고 큰 바다와 이어진 칸입니다. 해수면보다 낮아도 "
        "바다와 이어지지 않은 땅은 육지입니다.",
    },
    "crust_type": {
        "label": "지각 종류",
        "group": "해양",
        "kind": "categorical",
        "categories": _cats((0, "해양 지각", "#3b6ea8"), (1, "대륙 지각", "#c49a5a")),
        "read": "얇고 무거운 해양 지각은 낮게 떠 바다 밑이 되고, 두껍고 가벼운 대륙 지각은 높게 "
        "떠 육지와 대륙붕이 됩니다.",
    },
    "ocean_age_myr": {
        "label": "해양저 나이",
        "group": "해양",
        "cmap": "viridis",
        "read": "해령에서 만들어진 뒤 지난 시간입니다(대륙은 빈칸). 오래될수록 식어서 깊어집니다"
        "(나이-수심 관계, Parsons & Sclater 1977).",
    },
    # --- 기후
    "precip_m_per_yr": {
        "label": "연강수량",
        "group": "기후",
        "cmap": "ylgnbu",
        "read": "적도와 중위도에 비가 많은 띠가 있고 위도 30° 근처와 극은 건조합니다. 노이즈로 "
        "띠를 흔듭니다.",
    },
    "temperature_c": {
        "label": "연평균 기온",
        "group": "기후",
        "cmap": "diverging",
        "center": 0.0,
        "read": "위도와 고도로 정합니다(1 km 오를 때 6.5 °C 내려감). 0 °C 에서 파랑과 빨강으로 "
        "갈립니다.",
    },
    "pet_m_per_yr": {
        "label": "잠재 증발산",
        "group": "기후",
        "cmap": "ylorbr",
        "read": "물이 충분할 때 증발할 수 있는 양입니다. 따뜻할수록 큽니다.",
    },
    "runoff_m_per_yr": {
        "label": "유출 (Budyko)",
        "group": "기후",
        "cmap": "ylgnbu",
        "read": "강수 중 증발하지 않고 흘러 나가는 양입니다(Budyko 곡선). 지하수 함양에 씁니다.",
    },
    "runoff_eff_m_per_yr": {
        "label": "침식에 쓰는 유출",
        "group": "기후",
        "cmap": "ylgnbu",
        "read": "유출에 바닥값(강수의 일정 비율, 최솟값)을 적용한 값입니다. 사막에서도 강이 "
        "조금은 깎게 합니다. 유량 Q 는 이 값을 상류로 누적한 것입니다.",
    },
    # --- 물
    "discharge_m3_per_yr": {
        "label": "유량 Q",
        "group": "물(강·호수·지하수)",
        "cmap": "blues",
        "scale": "log",
        "read": "1년 동안 이 칸을 지나는 물의 양입니다(유효 유출 × 상류 면적의 누적). 로그 "
        "눈금이라 큰 강이 진하게 이어집니다. 1 m³/s ≈ 3.15×10⁷ m³/yr 입니다.",
    },
    "drainage_area_m2": {
        "label": "상류 면적",
        "group": "물(강·호수·지하수)",
        "cmap": "blues",
        "scale": "log",
        "display": ("km²", 1e-6),
        "read": "이 칸으로 물이 모이는 상류 땅의 넓이입니다. 로그 눈금으로 보면 물길망이 나무 "
        "모양으로 보입니다.",
    },
    "is_river": {
        "label": "강",
        "group": "물(강·호수·지하수)",
        "kind": "categorical",
        "categories": bool_cats("강 아님", "강", "#1f5fd1"),
        "read": "유량이 rivers.min_discharge_m3_per_s 보다 많은 칸입니다. L0 칸은 커서 강 칸이 "
        "많습니다.",
    },
    "is_lake": {
        "label": "호수",
        "group": "물(강·호수·지하수)",
        "kind": "categorical",
        "categories": bool_cats("호수 아님", "호수", "#4aa3ff"),
        "read": "웅덩이에 물이 고여 수면이 지표보다 높은 칸입니다.",
    },
    "water_level_m": {
        "label": "수면 높이",
        "group": "물(강·호수·지하수)",
        "cmap": "terrain",
        "read": "강·호수·바다의 수면 고도입니다(물이 없으면 빈칸).",
    },
    "river_width_m": {
        "label": "강 폭",
        "group": "물(강·호수·지하수)",
        "cmap": "blues",
        "read": "W = k_W·Q^0.5 (Q 는 m³/s) 로 정한 강 폭입니다(강이 아니면 0).",
    },
    "river_depth_m": {
        "label": "강 깊이",
        "group": "물(강·호수·지하수)",
        "cmap": "blues",
        "read": "D = k_D·Q^0.4 로 정한 강 깊이입니다(강이 아니면 0).",
    },
    "water_table_m": {
        "label": "지하수면 고도",
        "group": "물(강·호수·지하수)",
        "cmap": "terrain",
        "read": "땅속 물이 차 있는 높이입니다. 지표와 비교하려면 '지하수면 깊이'를 보세요.",
    },
    # --- 지질
    "template_id": {
        "label": "지질 템플릿",
        "group": "지질·암석",
        "kind": "categorical",
        "categories": _cats(
            (0, "탄산염 탁상지", "#e9e2c4"),
            (1, "습곡충상대", "#b07c55"),
            (2, "기반암 + 화산호", "#7a4a5a"),
        ),
        "read": "칸마다 쌓인 지층의 틀입니다. 탁상지는 석회암이 평평하게, 습곡충상대는 휘어 "
        "기울게 쌓입니다. 화산호는 섭입대 위판 쪽입니다.",
    },
    "fold_phase": {
        "label": "습곡 위상",
        "group": "지질·암석",
        "cmap": "viridis",
        "read": "습곡 파형의 위상(라디안)입니다. 줄무늬 방향이 습곡 축 방향입니다.",
    },
    "surface_rock": {
        "label": "지표 암석",
        "group": "지질·암석",
        "kind": "categorical",
        "categories": ROCK_CATS,
        "read": "지금 지표에 드러난 암석입니다. 솔버가 산비탈 경사(S_crit)와 깎임(K)을 정할 때 쓴 "
        "바로 그 암석입니다. 석회암·대리암에만 동굴이 생깁니다.",
    },
    # --- 동굴
    "cave_level_0_m": {
        "label": "동굴 아래층 고도 (잠김)",
        "group": "동굴",
        "cmap": "terrain",
        "read": "지금 지하수면 높이에 생긴, 물에 잠긴 동굴 층입니다. 녹는 암석(석회암·대리암) "
        "안이고 지표보다 충분히 깊은 칸과 입구 칸에만 값이 있습니다(그 밖은 빈칸).",
    },
    "cave_level_1_m": {
        "label": "동굴 위층 고도 (마름)",
        "group": "동굴",
        "cmap": "terrain",
        "read": "강이 더 깊이 파기 전 옛 지하수면 높이에 생긴 마른 동굴 층입니다. 높이 = "
        "지하수면 + 하각 비율 × 골짜기 깊이입니다.",
    },
    "cave_entrance": {
        "label": "동굴 입구",
        "group": "동굴",
        "kind": "categorical",
        "categories": _cats(
            (0, "입구 아님", NONE_COLOR),
            (1, "아래층 입구", "#2e86de"),
            (2, "위층 입구", "#e08a1e"),
            (3, "두 층 입구", "#e0218a"),
        ),
        "read": "동굴 층이 땅 밖과 이어지는 칸입니다. 층이 비탈에서 땅 위로 나오거나, 가파른 "
        "골짜기 벽과 만나는 곳입니다.",
    },
    "valley_depth_m": {
        "label": "골짜기 깊이",
        "group": "동굴",
        "cmap": "magma",
        "read": "가장 가까운 강의 골짜기 깊이입니다. 동굴 위층 높이를 정합니다(지하수면 + 하각 "
        "비율 × 골짜기 깊이).",
    },
    # --- 흙
    "soil_thickness_m": {
        "label": "흙 두께",
        "group": "흙",
        "cmap": "ylorbr",
        "read": "가파른 비탈은 0(맨 암반)이고, 완만하고 따뜻하고 습할수록 두껍습니다(상한 "
        "soil.thickness_cap_m).",
    },
    "alluvium_m": {
        "label": "충적층 두께",
        "group": "흙",
        "cmap": "ylorbr",
        "read": "강이 실어 와 쌓은 퇴적층 두께입니다. 큰 강가와 선상지에서 두껍습니다.",
    },
    "bare_rock": {
        "label": "맨 암반",
        "group": "흙",
        "kind": "categorical",
        "categories": bool_cats("흙 덮임", "맨 암반", "#7d7c78"),
        "read": "흙이 없어 암석이 바로 드러난 칸입니다(경사가 soil.bare_slope 보다 가파름).",
    },
    # --- 지도에 그리지 않고 칸 정보에만 보이는 필드
    "receiver": {
        "label": "물을 보내는 이웃 (칸 번호)",
        "group": "물(강·호수·지하수)",
        "map": False,
    },
    "strata_bottom_m": {"label": "층 바닥 고도", "group": "지질·암석", "map": False},
    "strata_rock": {"label": "층 암석", "group": "지질·암석", "map": False},
}


def _derive_ocean_depth(get: Callable[[str], np.ndarray]) -> np.ndarray:
    return np.where(get("is_ocean").astype(bool), -get("z_m"), np.nan)


def _derive_wt_depth(get: Callable[[str], np.ndarray]) -> np.ndarray:
    d = np.maximum(get("z_m") - get("water_table_m"), 0.0)
    try:
        d = np.where(get("is_ocean").astype(bool), np.nan, d)
    except KeyError:
        pass
    return d


def _derive_cave_levels(get: Callable[[str], np.ndarray]) -> np.ndarray:
    return np.isfinite(get("cave_level_0_m")) * 1.0 + np.isfinite(get("cave_level_1_m")) * 2.0


def _derive_cave_cover(get: Callable[[str], np.ndarray]) -> np.ndarray:
    return get("z_m") - get("cave_level_0_m")


# 계산값: 이름 → (필요한 필드, 함수, 보기 설명, 단위, 식)
DERIVED: dict[str, dict[str, Any]] = {
    "ocean_depth_m": {
        "levels": ("planet",),
        "needs": ("z_m", "is_ocean"),
        "fn": _derive_ocean_depth,
        "unit": "m",
        "formula": "−z_m (바다 칸만)",
        "text": {
            "label": "바다 수심",
            "group": "해양",
            "cmap": "blues",
            "read": "바다 칸의 해저 깊이입니다. 해령에서 얕고(약 2.5 km), 나이 든 해양저와 "
            "해구에서 깊습니다. 대륙 지각 위 얕은 바다는 대륙붕입니다.",
        },
    },
    "water_table_depth_m": {
        "needs": ("z_m", "water_table_m"),
        "fn": _derive_wt_depth,
        "unit": "m",
        "formula": "z_m − water_table_m (육지)",
        "text": {
            "label": "지하수면 깊이 (지표에서)",
            "group": "물(강·호수·지하수)",
            "cmap": "blues",
            "read": "지표에서 지하수면까지의 깊이입니다. 0 이면 지하수가 땅 겉에 닿습니다(습지·"
            "강가). 지구에서 이런 땅은 육지의 22~32% 입니다(Fan 2013).",
        },
    },
    "cave_levels": {
        "needs": ("cave_level_0_m", "cave_level_1_m"),
        "fn": _derive_cave_levels,
        "unit": "",
        "formula": "아래층 있음 + 2 × 위층 있음",
        "text": {
            "label": "동굴 층 (합친 그림)",
            "group": "동굴",
            "kind": "categorical",
            "categories": _cats(
                (0, "동굴 없음", NONE_COLOR),
                (1, "아래층만 (잠김)", "#2e86de"),
                (2, "위층만 (마름)", "#e08a1e"),
                (3, "두 층 모두", "#7b4fd6"),
            ),
            "read": "칸마다 동굴 층이 있는지 한눈에 봅니다. 동굴은 녹는 암석(석회암·대리암) 안에만 "
            "생깁니다. 입구는 '동굴 입구'를 보세요.",
        },
    },
    "cave_cover_0_m": {
        "needs": ("z_m", "cave_level_0_m"),
        "fn": _derive_cave_cover,
        "unit": "m",
        "formula": "z_m − cave_level_0_m",
        "text": {
            "label": "동굴 아래층 위 덮개 두께",
            "group": "동굴",
            "cmap": "viridis",
            "read": "지표에서 아래층 동굴까지 깊이입니다. 얇으면 천장이 무너지기 쉬운 곳이고, 0 "
            "근처는 입구입니다.",
        },
    },
}

# 지도 위에 겹쳐 그리는 것 (JS 가 필드를 받아 그림)
OVERLAYS: dict[str, list[dict[str, Any]]] = {
    "planet": [
        {"id": "coast", "label": "해안선", "field": "is_ocean", "mode": "edge",
         "color": "#14181b", "default": True},
        {"id": "boundary", "label": "판 경계 (빨강 수렴·파랑 발산·회색 변환)",
         "field": "boundary_type", "mode": "category", "default": False},
    ],
    "hero": [
        {"id": "rivers", "label": "강", "field": "is_river", "mode": "mask",
         "color": "#1f5fd1", "default": True},
        {"id": "lakes", "label": "호수", "field": "is_lake", "mode": "mask",
         "color": "#4aa3ff", "default": True},
        {"id": "fans", "label": "선상지", "field": "fan", "mode": "mask",
         "color": "#e0a526", "default": False},
        {"id": "entrances", "label": "동굴 입구", "field": "cave_entrance", "mode": "mask",
         "color": "#e0218a", "default": False},
    ],
}  # fmt: skip

# 비트로 층을 적는 범주 (1 아래층, 2 위층, 3 두 층): 히어로 지도에서 묶을 때 비트를 합칩니다.
BITMASK_FIELDS = {"cave_entrance", "cave_levels"}

# 묶음 안에 하나라도 있으면 있음으로 묶는 값 (강이 끊기지 않게). BITMASK_FIELDS 가 먼저입니다.
ANY_FIELDS = {
    "cave_entrance",
    "is_river",
    "is_lake",
    "fan",
    "bare_rock",
    "not_steady",
    "cave_levels",
}
