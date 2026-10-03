"""1단계 재료(사슬 1과 기후) 검사 (docs/pipeline.md 4장): C# 이 만든 tiny 행성 묶음.

판 번호·경계 종류, 대륙 넓이 몫, 해양저 나이, 해수면(바다 한 덩어리), 융기·깎인 두께, 위도 띠
강수와 Budyko 유출의 성질을 결과 필드에서 봅니다.
"""

import numpy as np
import pytest
from bundles import load_config_dict


@pytest.fixture(scope="module")
def cfg():
    return load_config_dict()


def test_plate_ids_cover_all_plates(planet, cfg):
    ids = planet["plate_id"]
    assert np.array_equal(np.unique(ids), np.arange(int(cfg["plates"]["count"])))


def test_categorical_plate_fields_use_documented_codes(planet):
    assert set(np.unique(planet["boundary_type"])) <= {0, 1, 2, 3}  # 없음, 수렴, 발산, 변환
    assert set(np.unique(planet["convergence_kind"])) <= {0, 1, 2, 3}
    assert set(np.unique(planet["subduction_side"])) <= {-1, 0, 1}
    assert set(np.unique(planet["crust_type"])) == {0, 1}
    # 경계 칸은 세 종류가 모두 있음 (판 12 개면 수렴·발산·변환이 다 생김)
    assert set(np.unique(planet["boundary_type"])) == {0, 1, 2, 3}


def test_continental_area_fraction(planet, cfg):
    a = planet.graph.area
    frac = float(a[planet["crust_type"] == 1].sum() / a.sum())
    assert frac == pytest.approx(planet.meta["info"]["continental_fraction"], rel=1e-9)
    assert frac == pytest.approx(float(cfg["plates"]["continental_area_fraction"]), abs=0.02)


def test_ocean_ages(planet, cfg):
    age = planet["ocean_age_myr"]
    cont = planet["crust_type"] == 1
    assert np.isnan(age[cont]).all()  # 대륙은 NaN
    assert np.isfinite(age[~cont]).all()
    assert (age[~cont] >= 0).all() and (age[~cont] <= float(cfg["ocean"]["age_cap_myr"])).all()
    # 해양저는 나이가 들수록 깊어짐 (Parsons & Sclater): 나이와 기준 고도가 뚜렷이 반대로 감
    z = planet["z_platform_m"][~cont]
    assert np.corrcoef(age[~cont], z)[0, 1] < -0.5


def test_sea_level_keeps_one_connected_ocean(planet):
    """해수면 아래 칸 가운데 큰 바다와 이어진 칸만 바다입니다 (호수처럼 갇힌 칸은 빠짐)."""
    ocean = planet["is_ocean"]
    nbr = planet.graph.nbr
    seen = np.zeros(ocean.size, dtype=bool)
    start = int(np.flatnonzero(ocean)[0])
    seen[start] = True
    stack = [start]
    while stack:
        c = stack.pop()
        for v in nbr[c]:
            if v >= 0 and ocean[v] and not seen[v]:
                seen[v] = True
                stack.append(v)
    assert seen[ocean].all()
    assert planet.meta["info"]["sea_level_m"] < 0  # 물 부피 보존 해수면 (기준 고도에서 낮아짐)


def test_uplift_and_exhumation_ranges(planet, cfg):
    u = cfg["uplift"]
    assert (planet["uplift_m_per_yr"] >= float(u["rift_subsidence_m_per_yr"])).all()
    exh = planet["exhumation_m"]
    assert (exh >= 0).all() and (exh <= float(u["exhumation_cap_m"]) + 1e-3).all()
    vmax = max(cfg["plates"]["speed_m_per_yr"])
    assert (planet["convergence_m_per_yr"] >= 0).all()
    assert (planet["convergence_m_per_yr"] <= 2 * vmax + 1e-9).all()  # 두 판의 상대 속도
    assert (planet["spreading_m_per_yr"] >= 0).all()
    arc = planet["dist_arc_m"]
    assert np.isnan(arc).any() and (arc[np.isfinite(arc)] >= 0).all()  # 섭입 위판 쪽만 값


def test_climate_and_budyko(planet, cfg):
    c = cfg["climate"]
    p = planet["precip_m_per_yr"]
    pet = planet["pet_m_per_yr"]
    r = planet["runoff_m_per_yr"]
    r_eff = planet["runoff_eff_m_per_yr"]
    assert (p > 0).all() and (pet >= float(c["pet_base_m_per_yr"]) - 1e-6).all()
    assert (r >= 0).all() and (r <= p + 1e-6).all()  # 증발산은 0 이상, 강수 이하
    assert (r_eff >= r - 1e-6).all() and (r_eff >= float(c["runoff_floor_m_per_yr"]) - 1e-6).all()
    # 적도 띠가 극지보다 비가 많음 (위도 띠 강수)
    axis = np.asarray(cfg["planet"]["axis"], float)
    unit = planet.graph.pos / np.linalg.norm(planet.graph.pos, axis=1, keepdims=True)
    lat = np.degrees(np.arcsin(np.clip(unit @ (axis / np.linalg.norm(axis)), -1, 1)))
    assert np.median(p[np.abs(lat) < 10]) > np.median(p[np.abs(lat) > 70])
