"""지도 보기의 이름표: 필드마다 한국어 이름·그룹·읽는 법·색표·범주 이름 (views 가 씀).

- FIELD_TEXT: 묶음 필드 이름 → label, group, read(어떻게 읽나), cmap, scale, kind,
  categories, display(표시 단위, 곱할 수), range_mask("nonzero": 추천 색 범위를 0 이 아닌 칸으로
  잡음). label_planet/label_hero, read_planet/read_hero 는 단계마다 뜻이 다를 때 씁니다.
- DERIVED: 묶음에 없지만 읽기 쉬운 계산값 (바다 수심, 지하수면 깊이, 동굴 층 합친 그림 등).
- OVERLAYS: 지도 위에 겹쳐 그리는 것 (해안선, 판 경계, 강, 호수, 선상지, 동굴 입구).
설명(description)과 원래 단위는 core.fields.FIELDS 에서 읽습니다.

읽는 법(read)은 CLAUDE.md 4.3 을 따릅니다: 지도가 무엇인지, 밝은(진한) 곳과 어두운(옅은)
곳이 뜻하는 것, 어디를 보면 이상한지. 숫자는 configs/planets/earth.toml 의 기본값과
docs/pipeline.md 에서 옵니다.
"""

from collections.abc import Callable
from typing import Any

import numpy as np

# 암석 번호 → 영어 이름·색 (생성기는 C# 의 src/Bpcg/Geology/Rocks.cs, 번호와 값이 같아야 함)
ROCK_NAMES = (
    "alluvium", "sandstone", "shale", "limestone", "granite", "volcanic",
    "slate", "schist", "gneiss", "marble", "quartzite", "soil",
)  # fmt: skip
ROCK_COLOR_RGB = (
    (214, 190, 128), (226, 150, 82), (92, 98, 116), (222, 218, 196), (206, 140, 136),
    (60, 32, 36), (52, 62, 84), (128, 140, 92), (156, 108, 146), (248, 248, 252),
    (232, 170, 180), (104, 74, 46),
)  # fmt: skip
N_ROCKS = len(ROCK_NAMES)

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
    {"value": i, "label": f"{ROCK_KO[i]} ({ROCK_NAMES[i]})", "color": hex_color(ROCK_COLOR_RGB[i])}
    for i in range(N_ROCKS)
]

# 필드 이름 → 보기 설명. label·group·read(어떻게 읽나)·cmap·scale·kind·categories·display.
# label_planet/label_hero, read_planet/read_hero 는 단계마다 뜻이 다를 때 씁니다.
FIELD_TEXT: dict[str, dict[str, Any]] = {
    # --- 지형
    "z_mean_m": {
        "label": "평균 지표 고도",
        "group": "지형",
        "cmap": "terrain",
        "read": "행성 칸(노트북 기본 한 변 약 20 km) 하나의 평균 땅 높이입니다. 골짜기 바닥 "
        "높이(z_m)에 '칸 안 기복'의 절반(기본값)을 더했습니다. 파랑은 바다로 진할수록 깊고, "
        "육지는 초록 → 갈색 → 흰색으로 갈수록 높습니다. 지각 세기 한계 때문에 5,500 m(백두산의 약 "
        "2배)를 거의 넘지 않으므로, 크게 넘는 곳이 있으면 개요 탭의 '지각 세기 한계 첫 풀이' 줄을 "
        "봅니다.",
    },
    "z_m": {
        "label": "고도",
        "label_planet": "골짜기 바닥 고도",
        "label_hero": "지표 고도",
        "group": "지형",
        "cmap": "terrain",
        "read_planet": "행성 칸(노트북 기본 한 변 약 20 km) 안에서 강이 흐르는 가장 낮은 곳, 곧 "
        "골짜기 바닥의 높이입니다. 바다 칸은 해저 높이라 음수이고, 색은 '평균 지표 고도'와 "
        "같습니다. 능선 높이가 빠져 있어 산맥이 '평균 지표 고도'보다 낮게 보이는 것이 맞습니다.",
        "read_hero": "히어로 칸(노트북 기본 25 m) 하나의 땅 높이입니다. 솔버가 강과 산비탈의 경사 "
        "규칙으로 쌓은 지형에 선상지를 얹었습니다. 초록이 낮고 갈색 → 흰색으로 갈수록 높으며, "
        "음영(기복 그림자)이 골짜기와 능선을 보여 줍니다. 강이 나뭇가지처럼 모여 가장자리 출구 한 "
        "곳으로 빠지지 않거나, 가로·세로·45° 줄을 따라 곧게 뻗으면 이상합니다.",
    },
    "relief_m": {
        "label": "칸 안 기복",
        "group": "지형",
        "cmap": "magma",
        "read": "행성 칸(노트북 기본 한 변 약 20 km) 하나 안에 숨은 능선과 골짜기의 높이 차를 "
        "공식(Hack 법칙과 임계 경사)으로 어림한 값입니다. 밝을수록 기복이 커서 산맥이 밝고, "
        "평원과 바다(0)는 검습니다. 땅이 거의 솟지 않는 평원이 밝으면 이상합니다.",
    },
    "slope": {
        "label": "경사 (물이 흘러가는 쪽)",
        "group": "지형",
        "cmap": "magma",
        "read": "물이 흘러가는 이웃 칸까지의 경사(높이 차 ÷ 거리)입니다. 밝을수록 가파르고, 0.1 "
        "은 10 m 갈 때 1 m 내려가는 비탈(약 6°), 1.0 은 45° 입니다. 산맥이 밝고 강 바닥과 평야가 "
        "어둡습니다. 산비탈은 암석이 버티는 가장 가파른 경사(임계 경사, 0.4~0.85, 약 22~40°)에서 "
        "멈추므로, 이보다 가파른 칸이 넓게 있으면 이상합니다.",
    },
    "k_s": {
        "label": "강 가파름 k_s",
        "group": "지형",
        "cmap": "viridis",
        "scale": "log",
        "read": "물의 양 차이를 덜어 낸 강의 가파름입니다. 강 경사 = k_s × 유량^(−θ) 의 k_s 이고, "
        "θ 는 기본 0.45, 유량 단위는 m³/yr 입니다. 밝을수록 가파르고, 땅이 빨리 솟거나 암석이 "
        "단단한 곳이 밝습니다. 로그 눈금이라 색 막대 눈금 하나가 10배이며, 융기가 큰 산맥 띠가 "
        "밝지 않으면 이상합니다.",
    },
    "s_crit": {
        "label": "임계 경사 (산비탈이 버티는 최대 경사)",
        "group": "지형",
        "cmap": "viridis",
        "read": "지표 암석이 무너지지 않고 버티는 가장 가파른 산비탈 경사입니다. 암석마다 정한 "
        "값(충적층 0.4 ~ 규암 0.85)이라 무늬가 '지표 암석' 지도와 같아야 합니다. 밝을수록 "
        "가파르게 버티고, 0.6 은 10 m 갈 때 6 m 오르는 비탈(약 31°)입니다.",
    },
    "sediment_flux_m3_per_yr": {
        "label": "퇴적물 유량 Qs",
        "group": "지형",
        "cmap": "ylorbr",
        "scale": "log",
        "read": "1년 동안 이 칸을 지나 내려가는 흙과 모래의 부피입니다. 다 자란 "
        "지형(정상상태)에서는 상류에서 솟은 만큼(융기 × 넓이의 합)이 그대로 내려옵니다. "
        "진할수록(갈색) 많고, 로그 눈금이라 큰 강줄기를 따라 진하게 이어집니다. 강줄기가 중간에 "
        "끊기면 이상합니다.",
    },
    "not_steady": {
        "label": "정상상태 아님 (후처리로 고친 칸)",
        "group": "지형",
        "kind": "categorical",
        "categories": bool_cats("솔버 결과 그대로", "후처리로 고친 칸", "#d1495b"),
        "read": "솔버가 만든 다 자란 지형(정상상태)에 나중에 손댄 칸입니다. 선상지를 얹은 칸과, "
        "그 뒤 물길을 다시 계산해 흐르는 방향이 바뀐 칸이 빨강입니다. 이 칸의 경사는 솔버 규칙을 "
        "따르지 않으므로 점수표의 경사 법칙 검사에서 뺍니다.",
    },
    "fan": {
        "label": "선상지",
        "group": "지형",
        "kind": "categorical",
        "categories": bool_cats("아님", "선상지", "#e0a526"),
        "read": "산골짜기에서 평야로 나오며 강 경사가 4분의 1 아래로 줄어드는 곳에, 강이 흙을 "
        "부채꼴로 쌓은 땅(선상지)입니다. 솔버 뒤에 반지름 3 km 원뿔로 얹은 것이라 정상상태가 "
        "아닙니다. 평면 히어로에는 생기지 않는 것이 지금 알려진 한계입니다.",
    },
    # --- 판·지각
    "plate_id": {
        "label": "판 번호",
        "group": "판·지각",
        "kind": "categorical",
        "read": "지구 겉을 나눈 큰 조각(판)마다 다른 색입니다. 색은 번호를 가를 뿐 뜻은 없습니다. "
        "판끼리 만나는 곳의 종류는 '판 경계 종류'에서 봅니다.",
    },
    "boundary_type": {
        "label": "판 경계 종류",
        "group": "판·지각",
        "kind": "categorical",
        "categories": _cats(
            (0, "경계 아님", NONE_COLOR),
            (1, "수렴 (산맥·해구)", "#d1495b"),
            (2, "발산 (바다 밑 산맥·열곡)", "#2e86de"),
            (3, "변환 (엇갈림)", "#8d99a6"),
        ),
        "read": "두 판이 만나는 칸에만 색이 있습니다. 서로 다가오면 수렴(빨강), 멀어지면 "
        "발산(파랑), 옆으로 엇갈려 미끄러지면 변환(회색)입니다. 다가오거나 멀어지는 빠르기가 "
        "엇갈리는 빠르기의 0.3배(plates.boundary_kappa)를 넘어야 수렴이나 발산으로 칩니다.",
    },
    "convergence_m_per_yr": {
        "label": "수렴 속도",
        "group": "판·지각",
        "cmap": "magma",
        "display": ("cm/yr", 100.0),
        "read": "가장 가까운 수렴 경계에서 두 판이 서로 다가오는 빠르기입니다. 밝을수록 빠르고, "
        "판마다 1년에 2~8 cm(plates.speed_m_per_yr)로 움직이게 정해 두었습니다. 산맥은 이 값에 "
        "비례해 솟습니다. 섭입 산맥은 0.03배라 5 cm/yr 면 1.5 mm/yr 입니다.",
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
        "read": "가장 가까운 수렴 경계에서 어떤 판끼리 만나는지입니다. 바다 판이 다른 판 아래로 "
        "파고드는 섭입은 좁은 산맥과 화산 줄을, 대륙끼리 부딪히는 충돌은 지각을 두껍게 해 넓은 "
        "고원을 만듭니다. 바다 판끼리 만나면 더 오래된 쪽이 파고듭니다.",
    },
    "subduction_side": {
        "label": "섭입 쪽",
        "group": "판·지각",
        "kind": "categorical",
        "categories": _cats(
            (-1, "섭입판 (아래로 파고듦)", "#2e86de"),
            (0, "해당 없음·충돌", NONE_COLOR),
            (1, "위판 (위에 얹힘)", "#d1495b"),
        ),
        "read": "섭입 경계에서 이 칸이 위에 얹힌 판(빨강)인지 아래로 파고드는 판(파랑)인지입니다. "
        "산맥과 화산 줄은 위판 쪽에, 깊은 해구는 파고드는 판 쪽 바다에 생깁니다.",
    },
    "dist_convergent_m": {
        "label": "수렴 경계까지 거리",
        "group": "판·지각",
        "cmap": "viridis",
        "display": ("km", 1e-3),
        "read": "가장 가까운 수렴 경계까지 공 표면을 따라 잰 거리(대원 거리)입니다. 밝을수록 "
        "멉니다. 섭입 산맥은 이 거리가 약 200 km(해구-화산호 거리)인 띠에, 충돌 산맥은 경계 바로 "
        "옆에 섭니다.",
    },
    "dist_divergent_m": {
        "label": "발산 경계까지 거리",
        "group": "판·지각",
        "cmap": "viridis",
        "display": ("km", 1e-3),
        "read": "가장 가까운 발산 경계(바다에서는 바다 밑 산맥, 곧 해령)까지 공 표면을 따라 잰 "
        "거리입니다. 밝을수록 멀고, 바다 밑 나이는 이 거리 ÷ 판이 벌어지는 속도로 정합니다.",
    },
    "spreading_m_per_yr": {
        "label": "벌어지는 속도 (반확장)",
        "group": "판·지각",
        "cmap": "magma",
        "display": ("cm/yr", 100.0),
        "read": "가장 가까운 발산 경계에서 한쪽 판이 경계로부터 멀어지는 빠르기로, 두 판이 "
        "벌어지는 속도의 절반입니다(반확장). 밝을수록 빠르고, 빠를수록 같은 거리의 바다 밑이 "
        "젊습니다.",
    },
    "crust_thickness_m": {
        "label": "지각 두께",
        "group": "판·지각",
        "cmap": "viridis",
        "display": ("km", 1e-3),
        "read": "판 윗부분 암석층(지각)의 두께입니다. 대륙 35 km, 바다 7 km 이고, 대륙끼리 "
        "부딪히는 곳은 최대 25 km 더 두꺼워집니다. 밝을수록 두껍고, 두꺼운 대륙 지각일수록 높이 "
        "떠서 높은 땅이 됩니다(지각평형).",
    },
    "z_platform_m": {
        "label": "기준 고도 (해수면 정하기 전)",
        "group": "판·지각",
        # 0 m 가 해수면이 아니므로 바다·육지 두 갈래 색(terrain)을 쓰지 않습니다.
        "cmap": "cividis",
        "read": "지각 두께와 바다 밑 나이로만 정한, 해수면을 정하기 전의 높이입니다. 그래서 0 m "
        "는 바닷가가 아닙니다. 이 값이 개요 탭의 '해수면 (기준 고도 대비)'보다 낮고 큰 바다와 "
        "이어진 곳이 바다가 되며, 실제 해안은 겹쳐 그린 검은 해안선입니다. 밝을수록 높습니다.",
    },
    "uplift_m_per_yr": {
        "label": "융기 속도 U",
        "group": "판·지각",
        "cmap": "magma",
        "display": ("mm/yr", 1e3),
        "range_mask": "nonzero",  # 바다(0)가 대부분이라 전체 백분위로는 산맥이 한 색이 됨
        "read": "땅이 솟는 속도입니다(지각 세기 한계로 줄인 뒤 값). 1 mm/yr 는 1,000년에 1 m, "
        "100만 년에 1 km 입니다. 수렴 경계 근처 좁은 띠가 밝고, 나머지 육지는 아주 느리게(0.005 "
        "mm/yr) 솟으며, 바다는 0, 내려앉는 열곡은 음수입니다. 기본 색 범위는 0 이 아닌 칸에서 "
        "아래·위 2% 를 뺀 범위라, 가장 빠른 산맥 몇 % 는 맨 위 색입니다.",
    },
    "exhumation_m": {
        "label": "깎인 두께",
        "group": "판·지각",
        "cmap": "magma",
        "display": ("km", 1e-3),
        "scale": "log",  # 크라톤 0.15 km 와 산맥 수십 km 를 한 지도에서 보려고 로그
        "read": "산이 자라는 동안 깎여 사라진 암석의 두께입니다(융기 × 3,000만 년, 최대 30 km). "
        "밝을수록 두껍고, 두꺼울수록 깊이 묻혔다가 열을 받아 바뀐 암석(편암·편마암·대리암 등)이 "
        "지표에 드러납니다. 로그 눈금이라 평원(0.15 km)과 산맥(최대 30 km)을 한 지도에서 보고, "
        "바다(0)는 맨 아래 색입니다.",
    },
    "dist_arc_m": {
        "label": "해구-화산호 거리",
        "group": "판·지각",
        "cmap": "viridis",
        "display": ("km", 1e-3),
        "read": "섭입 경계에서 바다 밑 깊은 골짜기(해구)부터 화산이 줄지어 선 곳(화산호)까지의 "
        "거리입니다. 파고드는 판의 기울기와 깊이 설정만으로 정해, 값이 있는 칸은 모두 "
        "같습니다(기본 약 200 km). 위에 얹힌 판 쪽 칸에만 값이 있습니다.",
    },
    # --- 해양
    "is_ocean": {
        "label": "바다",
        "group": "해양",
        "kind": "categorical",
        "categories": bool_cats("육지", "바다", "#2e6fb3"),
        "read": "정해 둔 바닷물 양(ocean.water_volume_m3)을 낮은 곳부터 채워 정한 해수면보다 "
        "낮고, 큰 바다와 이어진 칸입니다. 해수면보다 낮아도 큰 바다와 이어지지 않은 땅은 육지로 "
        "둡니다. 그래서 바다 비율은 입력이 아니라 결과이고, 개요 탭에서 지구(0.708)와 견줍니다.",
    },
    "crust_type": {
        "label": "지각 종류",
        "group": "해양",
        "kind": "categorical",
        "categories": _cats((0, "해양 지각", "#3b6ea8"), (1, "대륙 지각", "#c49a5a")),
        "read": "대륙 지각(두께 35 km)은 높이 떠서 육지와 얕은 바다(대륙붕)가 되고, 해양 지각(7 "
        "km)은 나이에 따라 약 2.5~6.4 km 깊이의 바다 밑이 됩니다. 대륙은 판 경계와 상관없이 "
        "놓이므로, 대륙 한가운데로 판 경계가 지나가도 정상입니다.",
    },
    "ocean_age_myr": {
        "label": "해양저 나이",
        "group": "해양",
        "cmap": "viridis",
        "read": "바다 밑 땅이 발산 경계(해령)에서 생긴 뒤 지난 시간입니다(1 Myr = 100만 년, "
        "대륙은 빈칸). 밝을수록 오래됐고, 오래될수록 식어서 깊어집니다. 막 생긴 곳은 약 2,500 m "
        "이고, 오래될수록 약 6,400 m 쪽으로 깊어집니다(Parsons & Sclater 1977). 해령에서 "
        "멀어질수록 고르게 밝아져야 하며, 곧은 쐐기 무늬는 알려진 문제입니다.",
    },
    # --- 기후
    "precip_m_per_yr": {
        "label": "연강수량",
        "group": "기후",
        "cmap": "ylgnbu",
        "read": "1년 동안 내린 비와 눈을 물 높이로 잰 양입니다(1 m/yr 는 1년에 1,000 mm). 기본 "
        "설정에서 적도는 약 2 m/yr, 위도 50° 근처는 약 1.2 m/yr 로 젖고, 위도 30° 근처와 극은 약 "
        "0.3 m/yr 로 마르며, 이 띠를 노이즈로 흔듭니다. 진할수록 많습니다. 산이 비를 막는 효과는 "
        "아직 없습니다.",
    },
    "temperature_c": {
        "label": "연평균 기온",
        "group": "기후",
        "cmap": "diverging",
        "center": 0.0,
        "read": "위도와 높이로 정한 1년 평균 기온입니다. 기본 설정에서 적도 바닷가는 27 °C, 극은 "
        "−25 °C 이고, 1 km 오를 때마다 6.5 °C 내려갑니다. 0 °C 에서 파랑(영하)과 빨강(영상)이 "
        "갈리므로, 산맥이 둘레보다 파랗게 보여야 맞습니다.",
    },
    "pet_m_per_yr": {
        "label": "잠재 증발산",
        "group": "기후",
        "cmap": "ylorbr",
        "read": "물이 넉넉할 때 땅과 식물에서 날아갈 수 있는 물의 양(잠재 증발산)입니다. "
        "기온만으로 정해(0.055 × 기온 + 0.1 m/yr, 영하는 0.1) '연평균 기온'과 같은 무늬이고, 27 "
        "°C 면 약 1.6 m/yr 입니다. 진할수록 많습니다.",
    },
    "runoff_m_per_yr": {
        "label": "유출 (Budyko)",
        "group": "기후",
        "cmap": "ylgnbu",
        "read": "비 가운데 증발하지 않고 땅 위로 흘러 나가는 물의 양입니다(유출, Budyko 곡선으로 "
        "셈). 비가 많고 서늘할수록 많고, 진할수록(파랑) 많습니다. 이 값의 "
        "절반(groundwater.recharge_fraction)이 땅속으로 스며 지하수가 됩니다.",
    },
    "runoff_eff_m_per_yr": {
        "label": "침식에 쓰는 유출",
        "group": "기후",
        "cmap": "ylgnbu",
        "read": "강이 땅을 깎는 힘을 셀 때 쓰는 유출입니다. 사막에서도 강이 조금은 깎도록 강수의 "
        "10% 나 0.01 m/yr 보다 작게 두지 않습니다(바닥값). 유량 Q 는 이 값에 칸 넓이를 곱해 "
        "상류부터 더한 것입니다. 진할수록 많습니다.",
    },
    # --- 물
    "discharge_m3_per_yr": {
        "label": "유량 Q",
        "group": "물(강·호수·지하수)",
        "cmap": "blues",
        "scale": "log",
        "read": "1년 동안 이 칸을 지나는 물의 부피입니다(침식에 쓰는 유출 × 칸 넓이를 상류부터 "
        "더함). 진할수록 많고, 로그 눈금이라 큰 강이 진한 줄로 이어집니다. 하류로 갈수록 늘기만 "
        "하므로 강줄기가 끊기거나 옅어지면 이상합니다. 1 m³/s 는 약 3.16×10⁷ m³/yr 입니다.",
    },
    "drainage_area_m2": {
        "label": "상류 면적",
        "group": "물(강·호수·지하수)",
        "cmap": "blues",
        "scale": "log",
        "display": ("km²", 1e-6),
        "read": "이 칸까지 빗물이 모여 오는 상류 땅의 넓이입니다. 진할수록 넓고, 로그 눈금으로 "
        "보면 물길이 나뭇가지처럼 모이는 모양이 드러납니다. 하류로 갈수록 늘기만 하므로 줄기가 "
        "끊기면 이상합니다.",
    },
    "is_river": {
        "label": "강",
        "group": "물(강·호수·지하수)",
        "kind": "categorical",
        "categories": bool_cats("강 아님", "강", "#1f5fd1"),
        "read": "1초에 0.3 m³(rivers.min_discharge_m3_per_s) 이상 흐르는 육지 칸입니다. 행성 칸은 "
        "넓어서(노트북 기본 약 20 km) 육지 칸 대부분이 강입니다. 강이 끊겨 바다·호수·출구에 닿지 "
        "않으면 이상합니다(점수표 '강이 바다·호수·출구에 닿는 비율').",
    },
    "is_lake": {
        "label": "호수",
        "group": "물(강·호수·지하수)",
        "kind": "categorical",
        "categories": bool_cats("호수 아님", "호수", "#4aa3ff"),
        "read": "물이 갇히는 웅덩이를 메운 육지 칸입니다(메운 높이가 땅보다 1 mm 넘게 높음). "
        "솔버가 만든 지형에는 웅덩이가 없으므로, 호수는 주로 선상지가 물길을 막은 곳에 생깁니다.",
    },
    "water_level_m": {
        "label": "수면 높이",
        "group": "물(강·호수·지하수)",
        "cmap": "terrain",
        "read": "바다·호수·강의 물 높이입니다(해수면 0 m 기준, 물이 없는 칸은 회색 빈칸). 강을 "
        "따라 하류로 갈수록 낮아져야 하고, 거꾸로 높아지는 곳은 점수표 '거꾸로 흐르는 강 칸'에 "
        "잡힙니다.",
    },
    "river_width_m": {
        "label": "강 폭",
        "group": "물(강·호수·지하수)",
        "cmap": "blues",
        "read": "물의 양으로 정한 강 폭입니다(폭 = 4 × 유량^0.5, 유량은 m³/s, 계수는 "
        "rivers.width_coefficient). 1초에 100 m³ 흐르는 강은 40 m 이고, 진할수록 넓습니다. 강이 "
        "아닌 칸은 0 입니다.",
    },
    "river_depth_m": {
        "label": "강 깊이",
        "group": "물(강·호수·지하수)",
        "cmap": "blues",
        "read": "물의 양으로 정한 강 깊이입니다(깊이 = 0.35 × 유량^0.4, 유량은 m³/s). 1초에 100 "
        "m³ 흐르는 강은 약 2.2 m 이고, 진할수록 깊습니다. 강이 아닌 칸은 0 입니다.",
    },
    "water_table_m": {
        "label": "지하수면 고도",
        "group": "물(강·호수·지하수)",
        "cmap": "terrain",
        "read": "땅속에서 물이 차 있는 높이(해수면 기준)입니다. 물가에서는 수면과 같고, 물가에서 "
        "멀어질수록 높아지는 어림식(Dupuit)으로 정했습니다. 땅 높이와 견주려면 '지하수면 깊이 "
        "(지표에서)'를 봅니다.",
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
        "read": "칸마다 지층이 쌓인 순서(지질 템플릿)입니다. 탄산염 탁상지는 사암·석회암·셰일이 "
        "평평하게 쌓였고, 습곡충상대는 셰일·석회암·사암 층이 물결처럼 휘었으며, 기반암 + 화산호는 "
        "화강암 위를 화산암 800 m 가 덮었습니다. 화산호는 섭입 경계에서 약 200 km 떨어진 띠, "
        "습곡충상대는 수렴 경계에서 400 km 안에만 있어야 합니다.",
    },
    "fold_phase": {
        "label": "습곡 위상",
        "group": "지질·암석",
        "cmap": "viridis",
        "read": "수렴 경계에서 멀어질수록 커지는 각도로, 15 km(geology.fold_wavelength_m)마다 "
        "2π(한 바퀴)씩 늘고 노이즈로 조금 흔듭니다. 밝을수록 경계에서 멀고, 같은 색이 이어지는 "
        "방향이 습곡(지층이 물결처럼 휜 것) 축의 방향입니다. 지층을 실제로 휘는 것은 습곡충상대 "
        "템플릿뿐입니다.",
    },
    "surface_rock": {
        "label": "지표 암석",
        "group": "지질·암석",
        "kind": "categorical",
        "categories": ROCK_CATS,
        "read": "지금 땅 겉에 드러난 암석입니다. 솔버가 산비탈이 버티는 경사와 깎이는 빠르기를 "
        "정할 때 쓴 바로 그 암석입니다. 많이 깎인 산맥에는 깊이 묻혔다가 열로 바뀐 "
        "암석(편암·편마암·대리암 등)이 드러나고, 동굴은 석회암·대리암에만 생깁니다.",
    },
    # --- 동굴
    "cave_level_0_m": {
        "label": "동굴 아래층 고도 (잠김)",
        "group": "동굴",
        "cmap": "terrain",
        "read": "지금 지하수면 높이에 뚫린, 물에 잠긴 동굴 통로의 높이입니다. 녹는 "
        "암석(석회암·대리암) 안이면서 땅 겉에서 6 m 넘게 묻힌 칸과 입구 칸에만 값이 있고, "
        "나머지는 회색 빈칸입니다. 지하수면이 땅 겉에 닿는 강가에서 이 층이 지표를 뚫어 입구가 "
        "너무 많아지는 것은 알려진 문제입니다.",
    },
    "cave_level_1_m": {
        "label": "동굴 위층 고도 (마름)",
        "group": "동굴",
        "cmap": "terrain",
        "read": "강이 지금보다 덜 파였을 때의 옛 지하수면 높이에 남은, 마른 동굴 통로입니다. 높이 "
        "= 지하수면 + 0.35(caves.incision_fraction) × 골짜기 깊이라서, 골짜기가 100 m 깊으면 "
        "아래층보다 35 m 위입니다. 녹는 암석 안에만 값이 있습니다.",
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
        "read": "동굴 층이 땅 밖과 만나는 칸입니다. 층 높이가 땅 겉에서 6 m 안(입구 허용 3 m + "
        "통로 반지름 3 m)인 녹는 암석 칸으로, 주로 골짜기 벽과 비탈입니다. 히어로 지도는 여러 "
        "칸을 한 픽셀로 묶을 때 두 층의 입구를 합쳐 '두 층 입구'로 보일 수 있습니다.",
    },
    "valley_depth_m": {
        "label": "골짜기 깊이",
        "group": "동굴",
        "cmap": "magma",
        "read": "가장 가까운 강에서, 둘레 1.5 km(caves.valley_window_m) 안 가장 높은 땅과 강 "
        "수면의 높이 차입니다. 밝을수록 깊은 골짜기로, 산맥의 강가가 밝고 평야가 어둡습니다. 동굴 "
        "위층 높이를 정합니다(지하수면 + 0.35 × 골짜기 깊이).",
    },
    # --- 흙
    "soil_thickness_m": {
        "label": "흙 두께",
        "group": "흙",
        "cmap": "ylorbr",
        "read": "암석 위를 덮은 흙의 두께입니다(최대 3 m, soil.thickness_cap_m). 따뜻하고 비가 "
        "많을수록 흙이 빨리 생겨 두껍고, 땅이 빨리 솟아 깎이는 곳은 얇습니다. 경사가 0.8(약 "
        "39°)보다 가파르면 0 입니다(맨 암반). 진할수록 두껍습니다.",
    },
    "alluvium_m": {
        "label": "충적층 두께",
        "group": "흙",
        "cmap": "ylorbr",
        "read": "강이 실어 온 모래·자갈 층(충적층)의 두께입니다. 경사가 0.02(100 m 에 2 m)보다 "
        "완만하고 흙이 쌓이는 쪽이 이기는 큰 강가에 생기며, 선상지에는 올린 높이를 더합니다. "
        "진할수록 두껍습니다.",
    },
    "bare_rock": {
        "label": "맨 암반",
        "group": "흙",
        "kind": "categorical",
        "categories": bool_cats("흙 덮임", "맨 암반", "#7d7c78"),
        "read": "흙도 충적층도 없어 암석이 바로 드러난 육지 칸입니다. 경사가 0.8(약 39°, "
        "soil.bare_slope)보다 가파르거나, 땅이 깎이는 빠르기가 흙이 생기는 빠르기보다 빠른 "
        "곳입니다. 0.8 은 규암을 뺀 모든 암석이 버티는 경사 이상이라, 가파름 때문에 맨 암반이 "
        "되는 칸은 거의 없습니다(알려진 한계).",
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
            "read": "바다 칸에서 해수면부터 바다 밑까지의 깊이입니다. 진할수록 깊습니다. 해령은 "
            "약 2.5 km 로 얕고, 오래된 바다 밑일수록 약 6.4 km 쪽으로 깊어지며, 해구에서는 최대 3 "
            "km 가 더해집니다. 대륙 지각 위의 200 m 보다 얕은 바다가 대륙붕입니다.",
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
            "read": "땅 겉에서 지하수면까지 몇 m 내려가야 하는지입니다(바다는 빈칸). 0 이면 "
            "지하수가 땅 겉까지 차 있는 땅(습지·강가)이고, 진할수록 깊습니다. 지구에서 이런 땅은 "
            "육지의 22~32% 인데(Fan 2013), 지금 노트북 결과는 91~96% 라 대부분 하얗게 보이는 것이 "
            "알려진 문제입니다.",
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
            "read": "칸마다 어느 동굴 층이 있는지 한 장에 모았습니다(파랑 잠긴 아래층, 주황 마른 "
            "위층, 보라 두 층). 동굴은 녹는 암석(석회암·대리암) 안에만 생기므로, 석회암이 없는 "
            "'기반암 + 화산호' 지질에 있으면 이상합니다. 입구는 '동굴 입구'에서 봅니다.",
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
            "read": "땅 겉에서 아래층 동굴까지의 두께입니다. 밝을수록 두껍게 덮였고, 6 m "
            "이하(음수 포함)는 입구 칸뿐입니다.",
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
