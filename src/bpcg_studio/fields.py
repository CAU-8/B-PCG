"""생성 결과 필드의 이름·단위·그룹 목록 (설계도 5장 '하나의 묶음'). 스튜디오 지도 탭이 씁니다.

생성기의 표는 C# 의 src/Bpcg/Core/Fields.cs 에 있습니다. 필드를 더하거나 바꾸면 여기도 같이
고칩니다(tests/test_fields_table.py 가 묶음 manifest 와 맞는지 봅니다).
"""

from dataclasses import dataclass


@dataclass(frozen=True)
class FieldInfo:
    group: str  # plates | geology | climate | surface | water | subsurface
    unit: str
    dtype: str  # numpy dtype 문자열
    description: str


def _f(group: str, unit: str, dtype: str, description: str) -> FieldInfo:
    return FieldInfo(group, unit, dtype, description)


FIELDS: dict[str, FieldInfo] = {
    # --- 사슬 1: 판·지각·해양저 (planet/)
    "plate_id": _f("plates", "", "int32", "판 번호"),
    "boundary_type": _f("plates", "", "uint8", "0 없음, 1 수렴, 2 발산, 3 변환 (경계 칸만)"),
    "convergence_m_per_yr": _f("plates", "m/yr", "float32", "가장 가까운 수렴 경계의 수렴 속도"),
    "convergence_kind": _f(
        "plates",
        "",
        "uint8",
        "가장 가까운 수렴 경계: 0 없음, 1 해양-대륙 섭입, 2 해양-해양 섭입, 3 대륙 충돌",
    ),
    "subduction_side": _f("plates", "", "int8", "+1 위판(overriding), -1 섭입판, 0 충돌·해당 없음"),
    "dist_convergent_m": _f("plates", "m", "float32", "가장 가까운 수렴 경계까지 대원 거리"),
    "dist_divergent_m": _f("plates", "m", "float32", "가장 가까운 발산 경계까지 대원 거리"),
    "spreading_m_per_yr": _f("plates", "m/yr", "float32", "가장 가까운 발산 경계의 반확장 속도"),
    "crust_type": _f("plates", "", "uint8", "0 해양 지각, 1 대륙 지각"),
    "crust_thickness_m": _f("plates", "m", "float32", "지각 두께"),
    "ocean_age_myr": _f("plates", "Myr", "float32", "해양저 나이 (대륙은 NaN)"),
    "z_platform_m": _f(
        "plates", "m", "float32", "지각평형·나이-수심으로 정한 기준 고도 (해수면 0 이전)"
    ),
    "is_ocean": _f("plates", "", "bool", "물 부피 보존 해수면 아래이고 큰 바다와 이어진 칸"),
    "uplift_m_per_yr": _f("plates", "m/yr", "float32", "융기 속도 U (침강은 음수)"),
    "exhumation_m": _f("plates", "m", "float32", "깎인 두께 = 누적 융기 (상한 있음)"),
    "dist_arc_m": _f("plates", "m", "float32", "해구에서 화산호까지 거리 (섭입 위판 쪽만)"),
    # --- 가벼운 기후 (planet/climate.py)
    "precip_m_per_yr": _f("climate", "m/yr", "float32", "연강수량"),
    "temperature_c": _f("climate", "°C", "float32", "연평균 기온 (최종 고도 반영)"),
    "pet_m_per_yr": _f("climate", "m/yr", "float32", "잠재 증발산"),
    "biome": _f("climate", "", "uint8", "바이옴 번호 (Biomes: 0 바다, 1 빙원 … 9 열대우림)"),
    "runoff_m_per_yr": _f("climate", "m/yr", "float32", "Budyko 유출"),
    "runoff_eff_m_per_yr": _f("climate", "m/yr", "float32", "침식에 쓰는 유출 (바닥값 적용)"),
    "glaciated": _f(
        "climate", "", "uint8", "얼음이 덮였던 땅: 0 없음, 1 빙하기에만, 2 지금도 (L0)"
    ),
    # --- 사슬 2: 지질 (geology/)
    "template_id": _f("geology", "", "uint8", "0 탄산염 탁상지, 1 습곡충상대, 2 기반암 + 화산호"),
    "fold_phase": _f("geology", "rad", "float32", "습곡 위상"),
    "prospect_lode": _f(
        "geology",
        "",
        "float32",
        "원래 광맥(구리·금) 가능성 0~1: 섭입대 위쪽 판의 화산호 띠에서 높음",
    ),
    "prospect_placer": _f(
        "geology",
        "",
        "float32",
        "사금 가능성 0~1: 상류에 원래 광맥이 있고 강물이 느려지는 강 칸에서 높음",
    ),
    "surface_rock": _f("geology", "", "uint8", "지표에 드러난 암석 번호 (geology.rocks)"),
    "strata_bottom_m": _f("geology", "m", "float32", "(N, L) 층 바닥 고도, 위층부터"),
    "strata_rock": _f("geology", "", "uint8", "(N, L+1) 층 암석 번호, 마지막은 기반암"),
    # --- 지표 (landscape/)
    "z_m": _f("surface", "m", "float32", "고도. L0 에서는 골짜기 바닥, L2 에서는 지표"),
    "z_mean_m": _f("surface", "m", "float32", "기복 보정을 더한 평균 지표 (L0)"),
    "relief_m": _f("surface", "m", "float32", "칸 안의 아격자 기복 (L0)"),
    "receiver": _f("surface", "", "int32", "물을 보내는 이웃 (바다·출구는 자기 자신)"),
    "drainage_area_m2": _f("surface", "m2", "float64", "상류 면적"),
    "discharge_m3_per_yr": _f("surface", "m3/yr", "float64", "유량 Q (유효 유출 기준)"),
    "sediment_flux_m3_per_yr": _f("surface", "m3/yr", "float64", "퇴적물 흐름 Qs"),
    "slope": _f("surface", "m/m", "float32", "수신 셀까지 경사"),
    "k_s": _f("surface", "", "float32", "강 가파름 (Q 단위 m³/yr)"),
    "s_crit": _f("surface", "m/m", "float32", "지표 암석의 임계 경사"),
    "not_steady": _f("surface", "", "bool", "후처리로 고친 칸 (정상상태 아님)"),
    "fan": _f("surface", "", "bool", "선상지"),
    "dam": _f("surface", "", "bool", "산사태 둑 (골짜기를 막은 돌무더기)"),
    # --- 물 (subsurface/water.py)
    "water_level_m": _f("water", "m", "float32", "수면 h_w (물 없으면 NaN)"),
    "is_lake": _f("water", "", "bool", "호수"),
    "lake_closed": _f(
        "water", "", "bool", "넘치지 못하는 호수 (들어오는 물보다 증발이 많아 물길이 여기서 끝남)"
    ),
    "is_river": _f("water", "", "bool", "강"),
    "river_width_m": _f("water", "m", "float32", "강 폭 (강 아니면 0)"),
    "river_depth_m": _f("water", "m", "float32", "강 깊이 (강 아니면 0)"),
    "karst_exchange_m3_per_yr": _f(
        "water",
        "m3/yr",
        "float64",
        "지표의 물이 땅속과 주고받는 양: 음수는 개울이 사라지는 칸, 양수는 샘으로 솟는 칸 "
        "(L2, 그런 칸이 있을 때만)",
    ),
    # --- 바다 (Planet/SeaFields.cs, 행성 단계에만 있음)
    "sea_temp_c": _f(
        "water",
        "°C",
        "float32",
        "바다 물의 연평균 온도: 해수면 높이의 기온, −1.8 °C 아래로 내려가지 않음 (뭍은 NaN)",
    ),
    "sea_ice": _f("water", "", "bool", "바다 얼음: 바다 물의 온도가 −1.5 °C 아래인 바다 칸"),
    "river_plume": _f(
        "water", "", "float32", "강물 퍼짐 0~1: 강어귀에서 나온 강물이 바다로 퍼진 정도 (뭍은 0)"
    ),
    "wave_exposure": _f(
        "water",
        "",
        "float32",
        "파도 받이 0~1: 열여섯 방향으로 바다가 트인 거리의 평균 ÷ 재는 거리 (뭍은 0)",
    ),
    "seabed": _f(
        "water",
        "",
        "uint8",
        "바다 밑 바닥 종류 (SeaFields: 0 뭍, 1 펄, 2 모래, 3 자갈, 4 바위, 5 석회 모래)",
    ),
    "coast_kind": _f(
        "water",
        "",
        "uint8",
        "해안 종류, 바다와 닿은 뭍 칸만 (SeaFields: 0 해안 아님, 1 모래 해변, 2 자갈 해변, "
        "3 바위 해안, 4 갯벌, 5 흰 모래 해변)",
    ),
    # --- 사슬 3: 땅속 (subsurface/)
    "soil_thickness_m": _f("subsurface", "m", "float32", "흙 두께"),
    "alluvium_m": _f("subsurface", "m", "float32", "충적층(퇴적층) 두께"),
    "bare_rock": _f("subsurface", "", "bool", "맨 암반"),
    "water_table_m": _f("subsurface", "m", "float32", "지하수면 z_gw"),
    "water_table_lower_m": _f(
        "subsurface",
        "m",
        "float32",
        "아래 지하수면 (막는 층 아래 녹는 암석의 물 높이, 없으면 지하수면과 같음)",
    ),
    "valley_depth_m": _f("subsurface", "m", "float32", "가장 가까운 강의 골짜기 깊이"),
    "cave_level_0_m": _f(
        "subsurface", "m", "float32", "동굴 아래층 (지금 지하수면, 잠김). 없으면 NaN"
    ),
    "cave_level_1_m": _f("subsurface", "m", "float32", "동굴 위층 (옛 지하수면, 마름). 없으면 NaN"),
    "cave_entrance": _f("subsurface", "", "uint8", "동굴 입구 비트 (1 아래층, 2 위층)"),
}

GROUPS = ("plates", "geology", "climate", "surface", "water", "subsurface")

# 히어로 유역(L2)에서만 만드는 필드. 행성 지도(L0) 묶음에는 없습니다.
HERO_ONLY = frozenset({"karst_exchange_m3_per_yr"})


def check_fields(fields: dict) -> None:
    """목록에 없는 이름이 있으면 ValueError."""
    unknown = sorted(set(fields) - set(FIELDS))
    if unknown:
        raise ValueError(
            f"FIELDS 에 없는 필드 이름: {unknown} (Fields.cs 와 이 표에 먼저 적으세요)"
        )
