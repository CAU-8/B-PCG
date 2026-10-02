"""생성 파이프라인 끝까지 검사 (docs/pipeline.md 1·9·13장): tiny 프로필 행성 → 히어로."""

import math

import numpy as np
import pytest

from bpcg.core.config import load_config
from bpcg.core.fields import FIELDS
from bpcg.hero.domain import direction_to_local, hero_grid_size
from bpcg.hero.finder import MAX_LAT_DEG, HeroSite
from bpcg.hero.refine import OUTLET_CELLS
from bpcg.hydro.network import outlet_of
from bpcg.hydro.routing import topo_order
from bpcg.pipeline import HeroState, PlanetState, generate_hero, generate_planet
from bpcg.planet.climate import latitude_rad, surface_temperature

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


@pytest.fixture(scope="module")
def cfg():
    return load_config("earth", "tiny")


@pytest.fixture(scope="module")
def planet(cfg):
    return generate_planet(cfg, log=None)


@pytest.fixture(scope="module")
def hero(cfg, planet):
    return generate_hero(cfg, planet, log=None)


def test_generate_planet_fields(cfg, planet):
    assert isinstance(planet, PlanetState)
    g = planet.graph
    n = g.n_cells
    assert g.kind == "sphere" and n == 6 * int(cfg.profile.grid.l0_n_per_face) ** 2
    f = planet.fields
    assert set(f) == set(FIELDS)  # 행성은 FIELDS 의 모든 필드를 만듭니다
    for k, v in f.items():
        assert np.asarray(v).shape[0] == n, k
    ocean = np.asarray(f["is_ocean"])
    must_finite = (
        "z_m",
        "z_mean_m",
        "relief_m",
        "discharge_m3_per_yr",
        "slope",
        "water_table_m",
        "temperature_c",
        "soil_thickness_m",
        "uplift_m_per_yr",
        "strata_bottom_m",
    )
    for k in must_finite:
        assert np.isfinite(f[k]).all(), k
    # 바다는 출구(자기 자신)이고 고도는 수심(< 0), 육지는 해수면 위
    rcv = np.asarray(f["receiver"])
    roots = rcv == np.arange(n)
    assert np.array_equal(roots, ocean)
    assert (f["z_m"][ocean] < 0).all() and (f["z_m"][~ocean] > 0).all()
    assert np.allclose(f["z_m"][ocean], planet.info["bathymetry_m"][ocean])
    assert (f["relief_m"][ocean] == 0).all() and (f["relief_m"] >= 0).all()
    # 최종 고도 기온: 평균 지표 z_mean 으로 다시 계산 (pipeline.md 4.5 '두 번 부릅니다')
    t_sea = surface_temperature(latitude_rad(g, cfg), None, cfg)
    expect = t_sea - cfg.climate.lapse_rate_c_per_m * np.maximum(f["z_mean_m"], 0.0)
    assert np.allclose(f["temperature_c"], expect, atol=1e-9)
    assert np.isfinite(f["water_level_m"][ocean]).all()
    assert np.allclose(f["water_table_m"][ocean], 0.0)


def test_generate_planet_diag_and_scorecard(planet):
    d = planet.diag
    for k in ("materials_coarse", "transfer", "geology", "stages", "scorecard", "total"):
        assert d["seconds"][k] >= 0.0
    for k in ("solver", "fans", "relief", "water", "soil", "groundwater", "caves"):
        assert k in d["seconds"]["stages_detail"]
    assert len(d["solver"]["history"]) == d["solver"]["iterations"]
    card = d["scorecard"]
    assert card["planet.ocean_fraction"]["value"] == pytest.approx(planet.info["ocean_fraction"])
    for name in MUST_PASS:
        e = card[f"planet.{name}"]
        assert e["pass"] in (True, None), (name, e)
    assert d["n_river_segments"] > 0


def test_generate_planet_deterministic(cfg, planet):
    again = generate_planet(cfg, log=None)
    for k in ("z_m", "receiver", "water_table_m", "surface_rock"):
        assert np.array_equal(again.fields[k], planet.fields[k]), k


def test_generate_hero_site_and_boundary(cfg, planet, hero):
    assert isinstance(hero, HeroState) and isinstance(hero.site, HeroSite)
    site = hero.site
    f0 = planet.fields
    assert not f0["is_ocean"][site.l0_cell]
    assert abs(site.lat_deg) <= MAX_LAT_DEG
    assert np.allclose(site.center_unit, planet.graph.unit()[site.l0_cell])

    g = hero.graph
    n_side, dx = hero_grid_size(cfg)
    assert g.kind == "flat" and g.shape == (n_side, n_side)
    b = hero.diag["boundary"]
    cells = np.asarray(b["outlet_cells"])
    assert len(cells) == OUTLET_CELLS
    assert g.boundary_mask()[cells].all()
    # 출구 가장자리는 L0 수신 셀 방향
    d = planet.graph.pos[b["l0_receiver"]] - planet.graph.pos[site.l0_cell]
    de, dn = direction_to_local(site, d)
    expect = (
        ("east" if de > 0 else "west") if abs(de) >= abs(dn) else ("north" if dn > 0 else "south")
    )
    assert b["outlet_edge"] == expect
    assert np.allclose(hero.fields["z_m"][cells], b["z_outlet_m"])
    # 들어오는 물: Q_L0(중심) − Σ A·R_eff 가 양수일 때만, 가장자리 한 칸
    q_in = b["l0_discharge_m3_per_yr"] - float(np.sum(g.area * hero.fields["runoff_eff_m_per_yr"]))
    assert b["inflow_m3_per_yr"] == pytest.approx(max(q_in, 0.0), rel=1e-12)
    if b["inflow_m3_per_yr"] > 0:
        assert g.boundary_mask()[b["inflow_cell"]]


def test_generate_hero_fields_and_drainage(cfg, hero):
    f = hero.fields
    g = hero.graph
    n = g.n_cells
    nan_ok = {"water_level_m", "cave_level_0_m", "cave_level_1_m", "valley_depth_m"}
    for k, v in f.items():
        assert k in FIELDS, k
        v = np.asarray(v)
        assert v.shape[0] == n
        if v.dtype.kind == "f" and k not in nan_ok:
            assert np.isfinite(v).all(), k
    cells = np.asarray(hero.diag["boundary"]["outlet_cells"])
    rcv = np.asarray(f["receiver"], dtype=np.int64)
    assert set(np.flatnonzero(rcv == np.arange(n)).tolist()) == set(cells.tolist())
    assert np.isin(outlet_of(rcv, topo_order(rcv)), cells).all()
    # 기온은 히어로 위도와 최종 고도로 다시 계산
    lat = math.radians(hero.site.lat_deg)
    c = cfg.climate
    t_sea = c.t_equator_c - (c.t_equator_c - c.t_pole_c) * math.sin(lat) ** 2
    expect = t_sea - c.lapse_rate_c_per_m * np.maximum(f["z_m"], 0.0)
    assert np.allclose(f["temperature_c"], expect, atol=1e-9)
    card = hero.diag["scorecard"]
    for name in MUST_PASS:
        e = card[f"hero.{name}"]
        assert e["pass"] in (True, None), (name, e)


def test_generate_hero_without_planet_is_flat(cfg):
    h = generate_hero(cfg, None, log=None)
    assert h.site is None and h.diag["boundary"]["outlet_edge"] == "south"


def test_generate_planet_rejects_bad_cfg():
    with pytest.raises(ValueError):
        generate_planet({"planet": {}}, log=None)
    with pytest.raises(ValueError):
        generate_hero(load_config("earth", "tiny"), planet=object(), log=None)


def test_reroute_after_fans_keeps_valid_receivers(cfg):
    from bpcg.core.graph import flat_graph
    from bpcg.hydro.depressions import fill_epsilon
    from bpcg.landscape.solver import solve_steady_state
    from bpcg.pipeline import reroute_after_fans

    g = flat_graph(40, 40, 100.0, jitter=0.4, seed=3)
    n = g.n_cells
    outlet = g.boundary_mask()
    U = np.full(n, 5e-4)
    R = np.full(n, 0.5)
    res = solve_steady_state(g, outlet, 0.0, U, R, None, cfg)
    # 가운데 칸 묶음을 원뿔처럼 올려 물길을 막습니다.
    x, y = g.pos[:, 0] - 2000.0, g.pos[:, 1] + 2000.0
    r = np.hypot(x, y)
    fan = (r < 500.0) & ~outlet
    z = res.z.copy()
    z[fan] = np.maximum(z[fan], res.z[fan].max() + 30.0 - 0.03 * r[fan])
    rr = reroute_after_fans(g, z, outlet, res.receiver, fan, R, U, cfg)
    rcv = rr["receiver"]
    order = rr["order"]
    assert np.array_equal(np.sort(order), np.arange(n))  # 순환 없음 (topo_order 성공)
    zt = fill_epsilon(z, g.nbr, outlet, float(cfg.landscape.fill_epsilon_m))
    old = res.receiver
    valid = ~fan & ~outlet & (zt[old] < zt)
    assert np.array_equal(rcv[valid], old[valid])  # 선상지 밖 유효한 수신 셀은 그대로
    assert (zt[rcv[~outlet]] < zt[~outlet]).all()  # 모든 비출구 칸은 z̃ 가 낮은 쪽으로
    assert (rcv != old).sum() < fan.sum() + (~valid & ~outlet).sum() + 1
    q_out = rr["discharge_m3_per_yr"][outlet].sum()
    assert q_out == pytest.approx(float(np.sum(g.area * R)), rel=1e-12)
    qs_out = rr["sediment_flux_m3_per_yr"][outlet].sum()
    assert qs_out == pytest.approx(float(np.sum(g.area * U)), rel=1e-12)
    assert (rr["slope"] >= 0).all()
