"""생성 파이프라인 끝까지 검사 (docs/pipeline.md 1·9·13장): C# 이 만든 tiny 행성 → 히어로 묶음.

행성은 FIELDS 의 모든 필드를 만들고, 바다 칸은 출구, 육지는 해수면 위, 점수표의 일관성 검사는
합격해야 합니다. 히어로는 행성 자리에서 출구 가장자리·들어오는 물·물길이 경계조건과 맞아야 합니다.
"""

import math

import numpy as np
import pytest
from bundles import load_config_dict, outlet_of, topo_order

from bpcg_studio.fields import FIELDS
from bpcg_studio.hero import hero_grid_size

# 판정이 있으면 반드시 합격해야 하는 일관성 검사 (pipeline.md 12장)
MUST_PASS = (
    "river_reach_fraction",
    "river_backflow",
    "law_consistency",
    "water_budget",
    "sediment_budget",
    "rock_consistency",
    "cave_in_soluble",
    "water_rule_violations",
)
MAX_LAT_DEG = 55.0  # Hero/Finder.cs 의 MaxLatDeg
OUTLET_CELLS = 5  # Hero/Refine.cs·Flat.cs 의 출구 칸 수
NAN_OK = {"water_level_m", "cave_level_0_m", "cave_level_1_m", "valley_depth_m", "ocean_age_myr"}


def _lat_rad(pos: np.ndarray, axis) -> np.ndarray:
    """칸 위도 = asin(단위 위치 · 자전축) (Planet/Climate.cs 의 LatitudeRad)."""
    a = np.asarray(axis, dtype=np.float64)
    unit = pos / np.linalg.norm(pos, axis=1, keepdims=True)
    return np.arcsin(np.clip(unit @ (a / np.linalg.norm(a)), -1.0, 1.0))


def test_planet_fields(planet):
    g = planet.graph
    n = g.n_cells
    cfg = load_config_dict()
    assert g.kind == "sphere" and n == 6 * int(cfg["profile"]["grid"]["l0_n_per_face"]) ** 2
    f = planet.fields
    assert set(f) == set(FIELDS)  # 행성은 FIELDS 의 모든 필드를 만듭니다
    for k, v in f.items():
        assert v.shape[0] == n, k
    must_finite = (
        "z_m", "z_mean_m", "relief_m", "discharge_m3_per_yr", "slope", "water_table_m",
        "temperature_c", "soil_thickness_m", "uplift_m_per_yr", "strata_bottom_m",
    )  # fmt: skip
    for k in must_finite:
        assert np.isfinite(f[k]).all(), k
    # 바다는 출구(자기 자신)이고 고도는 수심(< 0), 육지는 해수면 위
    ocean = f["is_ocean"]
    rcv = f["receiver"].astype(np.int64)
    assert np.array_equal(rcv == np.arange(n), ocean)
    assert (f["z_m"][ocean] < 0).all() and (f["z_m"][~ocean] > 0).all()
    assert (f["relief_m"][ocean] == 0).all() and (f["relief_m"] >= 0).all()
    assert np.isfinite(f["water_level_m"][ocean]).all()
    assert np.allclose(f["water_table_m"][ocean], 0.0)
    assert planet.meta["info"]["ocean_fraction"] == pytest.approx(
        float(g.area[ocean].sum() / g.area.sum()), rel=1e-9
    )


def test_planet_temperature_uses_final_mean_surface(planet):
    """최종 고도 기온: 평균 지표 z_mean 으로 다시 계산 (pipeline.md 4.5 '두 번 부릅니다').

    T = T_eq − (T_eq − T_pole)·sin²φ − 감률·max(z_mean, 0)
    (Planet/Climate.cs 의 SurfaceTemperature).
    """
    f = planet.fields
    cfg = load_config_dict()
    c = cfg["climate"]
    s = np.sin(_lat_rad(planet.graph.pos, cfg["planet"]["axis"]))
    t_sea = c["t_equator_c"] - (c["t_equator_c"] - c["t_pole_c"]) * s**2
    expect = t_sea - c["lapse_rate_c_per_m"] * np.maximum(f["z_mean_m"].astype(np.float64), 0.0)
    assert np.allclose(f["temperature_c"], expect, atol=1e-4)


def test_planet_diag_and_scorecard(planet):
    d = planet.diag
    for k in ("materials_coarse", "transfer", "geology", "stages", "scorecard", "total"):
        assert d["seconds"][k] >= 0.0
    for k in ("solver", "fans", "relief", "water", "soil", "groundwater", "caves"):
        assert k in d["seconds"]["stages_detail"]
    assert len(d["solver"]["history"]) == d["solver"]["iterations"]
    card = d["scorecard"]
    assert card["planet.ocean_fraction"]["value"] == pytest.approx(
        planet.meta["info"]["ocean_fraction"]
    )
    for name in MUST_PASS:
        e = card[f"planet.{name}"]
        assert e["pass"] in (True, None), (name, e)
    assert d["n_river_segments"] > 0


def test_planet_deterministic(planet, tiny_planet_again):
    for k in ("z_m", "receiver", "water_table_m", "surface_rock", "strata_rock"):
        assert np.array_equal(tiny_planet_again[k], planet[k], equal_nan=k != "receiver"), k
    assert tiny_planet_again.manifest["config_digest"] == planet.manifest["config_digest"]


def test_hero_site_and_boundary(planet, hero):
    site = hero.meta["site"]
    assert site is not None
    assert not planet["is_ocean"][site["l0_cell"]]
    assert abs(site["lat_deg"]) <= MAX_LAT_DEG
    unit = planet.graph.pos[site["l0_cell"]] / np.linalg.norm(planet.graph.pos[site["l0_cell"]])
    assert np.allclose(site["center_unit"], unit)

    g = hero.graph
    n_side, _dx = hero_grid_size(_Cfg(load_config_dict()))
    assert g.kind == "flat" and g.shape == (n_side, n_side)
    b = hero.diag["boundary"]
    cells = np.asarray(b["outlet_cells"])
    assert len(cells) == OUTLET_CELLS
    assert g.boundary_mask()[cells].all()
    # 출구 가장자리는 L0 수신 셀 방향
    d = planet.graph.pos[b["l0_receiver"]] - planet.graph.pos[site["l0_cell"]]
    de, dn = float(np.dot(d, site["east"])), float(np.dot(d, site["north"]))
    expect = (
        ("east" if de > 0 else "west") if abs(de) >= abs(dn) else ("north" if dn > 0 else "south")
    )
    assert b["outlet_edge"] == expect
    assert np.allclose(hero["z_m"][cells], b["z_outlet_m"])
    # 들어오는 물: Q_L0(중심) − Σ A·R_eff 가 양수일 때만, 가장자리 한 칸
    q_in = b["l0_discharge_m3_per_yr"] - float(np.sum(g.area * hero["runoff_eff_m_per_yr"]))
    assert b["inflow_m3_per_yr"] == pytest.approx(max(q_in, 0.0), rel=1e-9)
    if b["inflow_m3_per_yr"] > 0:
        assert g.boundary_mask()[b["inflow_cell"]]


def test_hero_fields_and_drainage(hero):
    f = hero.fields
    n = hero.graph.n_cells
    for k, v in f.items():
        assert k in FIELDS, k
        assert v.shape[0] == n
        if v.dtype.kind == "f" and k not in NAN_OK:
            assert np.isfinite(v).all(), k
    cells = np.asarray(hero.diag["boundary"]["outlet_cells"])
    rcv = f["receiver"].astype(np.int64)
    assert set(np.flatnonzero(rcv == np.arange(n)).tolist()) == set(cells.tolist())
    assert np.isin(outlet_of(rcv), cells).all()
    topo_order(rcv)  # 순환 없음
    # 기온은 히어로 위도와 최종 고도로 다시 계산
    lat = math.radians(hero.meta["site"]["lat_deg"])
    c = load_config_dict()["climate"]
    t_sea = c["t_equator_c"] - (c["t_equator_c"] - c["t_pole_c"]) * math.sin(lat) ** 2
    expect = t_sea - c["lapse_rate_c_per_m"] * np.maximum(f["z_m"].astype(np.float64), 0.0)
    assert np.allclose(f["temperature_c"], expect, atol=1e-4)
    card = hero.diag["scorecard"]
    for name in MUST_PASS:
        e = card[f"hero.{name}"]
        assert e["pass"] in (True, None), (name, e)


def test_flat_hero_outlet_is_south(flat_hero):
    assert flat_hero.meta["site"] is None
    assert flat_hero.diag["boundary"]["outlet_edge"] == "south"


class _Cfg:
    """dict 설정을 hero_grid_size 가 읽는 cfg.profile.hero.* 꼴로 감쌉니다."""

    def __init__(self, data: dict):
        self._d = data

    def __getattr__(self, key):
        v = self._d[key]
        return _Cfg(v) if isinstance(v, dict) else v
