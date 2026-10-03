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
    "runoff_m_per_yr": _f("climate", "m/yr", "float32", "Budyko 유출"),
    "runoff_eff_m_per_yr": _f("climate", "m/yr", "float32", "침식에 쓰는 유출 (바닥값 적용)"),
    # --- 사슬 2: 지질 (geology/)
    "template_id": _f("geology", "", "uint8", "0 탄산염 탁상지, 1 습곡충상대, 2 기반암 + 화산호"),
    "fold_phase": _f("geology", "rad", "float32", "습곡 위상"),
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
    # --- 물 (subsurface/water.py)
    "water_level_m": _f("water", "m", "float32", "수면 h_w (물 없으면 NaN)"),
    "is_lake": _f("water", "", "bool", "호수"),
    "is_river": _f("water", "", "bool", "강"),
    "river_width_m": _f("water", "m", "float32", "강 폭 (강 아니면 0)"),
    "river_depth_m": _f("water", "m", "float32", "강 깊이 (강 아니면 0)"),
    # --- 사슬 3: 땅속 (subsurface/)
    "soil_thickness_m": _f("subsurface", "m", "float32", "흙 두께"),
    "alluvium_m": _f("subsurface", "m", "float32", "충적층(퇴적층) 두께"),
    "bare_rock": _f("subsurface", "", "bool", "맨 암반"),
    "water_table_m": _f("subsurface", "m", "float32", "지하수면 z_gw"),
    "valley_depth_m": _f("subsurface", "m", "float32", "가장 가까운 강의 골짜기 깊이"),
    "cave_level_0_m": _f(
        "subsurface", "m", "float32", "동굴 아래층 (지금 지하수면, 잠김). 없으면 NaN"
    ),
    "cave_level_1_m": _f("subsurface", "m", "float32", "동굴 위층 (옛 지하수면, 마름). 없으면 NaN"),
    "cave_entrance": _f("subsurface", "", "uint8", "동굴 입구 비트 (1 아래층, 2 위층)"),
}

GROUPS = ("plates", "geology", "climate", "surface", "water", "subsurface")


def check_fields(fields: dict) -> None:
    """목록에 없는 이름이 있으면 ValueError."""
    unknown = sorted(set(fields) - set(FIELDS))
    if unknown:
        raise ValueError(
            f"FIELDS 에 없는 필드 이름: {unknown} (Fields.cs 와 이 표에 먼저 적으세요)"
        )
