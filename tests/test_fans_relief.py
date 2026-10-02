"""선상지(landscape.fans)와 아격자 기복(landscape.relief) 검사 (docs/pipeline.md 7.2, 7.3).

- 선상지: 산지 앞(경사가 slope_drop_ratio 배 이상 줄어드는 강 칸)에 꼭짓점이 생기고, 하류 반평면의
  반경 안 칸이 원뿔 높이 이상으로 올라가며(z_new ≥ z), 올린 칸에 fan·not_steady 가 달립니다.
  꼭짓점끼리는 2·radius 이상 떨어집니다. 그 뒤 물길만 다시 계산합니다(물 질량 보존).
- 기복: 바다 0, 육지 양수, k_s 가 클수록 큼. 32점 로그 사다리꼴 적분이 해석해와 맞음.
"""

import numpy as np
import pytest

from bpcg.core.config import load_config
from bpcg.core.graph import flat_graph, sphere_graph
from bpcg.core.noise import fbm3
from bpcg.landscape.fans import add_fans, find_fan_apexes, reroute
from bpcg.landscape.relief import (
    N_QUADRATURE,
    channel_head_area,
    hillslope_relief,
    river_relief,
    subgrid_relief,
)
from bpcg.landscape.solver import solve_steady_state

CFG = load_config("earth", "tiny")
# 산지 앞 경사 차이가 뚜렷하도록 경사 지수 n = 1 로 고정하고(강 경사 ∝ E^(1/n) 이라 n = 2 면
# 융기 30배 차이가 경사 약 5.5배로 줄고, 유량·퇴적 항까지 더해 slope_drop_ratio 4 에 못 미침)
# 퇴적 계수를 줄이며, 작은 시험 영역에서도 강이 여러 개 생기게 선상지 최소 유량을 낮춥니다
# (기본값 1e6 m³/yr 는 L2 히어로 크기 기준).
FAN_CFG = CFG.with_overrides(
    {
        "landscape.slope_exponent_n": 1.0,
        "landscape.deposition_g": 0.2,
        "fans.min_discharge_m3_per_yr": 2.0e5,
    }
)


# ---------------------------------------------------------------- 선상지
@pytest.fixture(scope="module")
def mountain_front():
    """북쪽 40줄은 솟는 산지(U = 3 mm/yr), 남쪽은 느리게 솟는 저지대(0.1 mm/yr).

    남쪽 끝 줄이 출구입니다.
    """
    ny, nx, dx = 100, 240, 50.0
    g = flat_graph(ny, nx, dx, jitter=0.4, seed=2)
    row = np.arange(g.n_cells) // nx
    is_outlet = row == ny - 1
    U = np.where(row < 40, 3e-3, 1e-4)
    R = np.full(g.n_cells, 1.0)
    res = solve_steady_state(g, is_outlet, 0.0, U, R, None, FAN_CFG)
    assert res.converged
    return g, is_outlet, R, res, row


def test_fans_raise_cone_at_mountain_front(mountain_front):
    g, is_outlet, R, res, row = mountain_front
    fc = FAN_CFG.fans
    z_new, fan, not_steady, apexes = add_fans(g, res.z, res, FAN_CFG)
    assert len(apexes) >= 2
    assert fan.sum() > 1000
    # 올린 칸만 바뀌고, 높이는 원래 이상입니다.
    assert np.all(z_new >= res.z)
    assert np.all(z_new[fan] > res.z[fan])
    np.testing.assert_array_equal(z_new[~fan], res.z[~fan])
    np.testing.assert_array_equal(not_steady, fan)
    assert not fan[is_outlet].any()
    for a in apexes:
        c = a["cell"]
        assert row[c] in (38, 39, 40, 41)  # 산지 앞(40번째 줄 근처)
        assert fc.min_discharge_m3_per_yr <= a["discharge_m3_per_yr"] <= fc.max_discharge_m3_per_yr
        assert a["slope_ratio"] >= fc.slope_drop_ratio
        assert a["receiver"] == res.receiver[c]
        assert a["n_raised"] > 0
    # 꼭짓점끼리는 2·radius 이상 떨어져 있습니다(Q 가 큰 것만 남김).
    pos = g.pos[[a["cell"] for a in apexes]]
    gap = np.linalg.norm(pos[:, None] - pos[None], axis=2)
    assert np.all(gap[np.triu_indices(len(apexes), 1)] >= 2 * fc.radius_m)
    qs = [a["discharge_m3_per_yr"] for a in apexes]
    assert qs == sorted(qs, reverse=True)


def test_fan_cells_lie_on_cone_downstream(mountain_front):
    g, is_outlet, R, res, row = mountain_front
    fc = FAN_CFG.fans
    z_new, fan, _, apexes = add_fans(g, res.z, res, FAN_CFG)
    cone = np.full(g.n_cells, -np.inf)
    for a in apexes:
        pa = g.pos[a["cell"]]
        rel = g.pos - pa
        ell = np.linalg.norm(rel, axis=1)
        down = rel @ (g.pos[a["receiver"]] - pa) > 0
        ok = down & (ell < fc.radius_m)
        cone[ok] = np.maximum(cone[ok], a["z_m"] - fc.slope * ell[ok])
    # 올린 칸은 모두 어느 원뿔의 하류 반평면 반경 안이고, 높이는 그 원뿔 높이입니다.
    assert np.all(np.isfinite(cone[fan]))
    np.testing.assert_allclose(z_new[fan], cone[fan], rtol=0, atol=1e-9)
    # 원뿔이 지형보다 높은데 올리지 않은 칸은 출구뿐입니다.
    missed = (cone > res.z) & ~fan
    assert np.all(is_outlet[missed])


def test_reroute_after_fans_conserves_water(mountain_front):
    g, is_outlet, R, res, row = mountain_front
    z_new, fan, _, _ = add_fans(g, res.z, res, FAN_CFG)
    rt = reroute(g, z_new, is_outlet, R, FAN_CFG)
    assert set(rt) == {"receiver", "discharge_m3_per_yr", "drainage_area_m2", "slope"}
    rcv = rt["receiver"]
    outlets = rcv == np.arange(g.n_cells)
    np.testing.assert_array_equal(outlets, is_outlet)
    total = np.sum(g.area * R)
    assert abs(rt["discharge_m3_per_yr"][outlets].sum() - total) / total < 1e-12
    assert abs(rt["drainage_area_m2"][outlets].sum() - g.area.sum()) / g.area.sum() < 1e-12
    assert np.all(rt["slope"] >= 0) and np.all(np.isfinite(rt["slope"]))
    # 선상지 위에서는 물길이 바뀝니다.
    assert np.any(rcv[fan] != res.receiver[fan])
    # 바뀌지 않은 지형이면 솔버 수신 셀과 거의 같습니다(히스테리시스·진동 칸 고정 차이만).
    rt0 = reroute(g, res.z, is_outlet, R, FAN_CFG)
    assert np.mean(rt0["receiver"] == res.receiver) > 0.9
    same = rt0["receiver"] == res.receiver
    assert np.mean(same[~res.frozen]) > np.mean(same)


def test_fans_disabled_or_no_apex(mountain_front):
    g, is_outlet, R, res, row = mountain_front
    cfg_off = FAN_CFG.with_overrides({"fans.enabled": False})
    z_new, fan, ns, apexes = add_fans(g, res.z, res, cfg_off)
    np.testing.assert_array_equal(z_new, res.z)
    assert not fan.any() and not ns.any() and apexes == []
    # 경사가 크게 줄어드는 곳이 없으면 꼭짓점도 없습니다.
    cfg_hi = FAN_CFG.with_overrides({"fans.slope_drop_ratio": 1e6})
    assert find_fan_apexes(g, res, cfg_hi).size == 0
    z_new, fan, _, apexes = add_fans(g, res.z, res, cfg_hi)
    assert not fan.any() and apexes == []


def test_fans_invalid_inputs(mountain_front):
    g, is_outlet, R, res, row = mountain_front
    with pytest.raises(ValueError):
        add_fans(g, res.z[:-1], res, FAN_CFG)
    with pytest.raises(ValueError):
        add_fans(g, res.z, {"receiver": res.receiver}, FAN_CFG)
    with pytest.raises(ValueError):
        reroute(g, res.z, is_outlet, -R, FAN_CFG)


# ---------------------------------------------------------------- 기복
def l0_strip(n: int = 40, dx: float = 19_500.0):
    """L0 크기(칸 19.5 km)의 평면 칸 n 개."""
    return flat_graph(1, n, dx)


def analytic_river_relief(k_s, R, a_star, area, cfg):
    """∫ k_s (R·A(x))^(−θ) dx 의 해석해 (A(x) = (x/c_H)^(1/h), km·km²)."""
    theta = cfg.landscape.theta
    c_h = cfg.relief.hack_coefficient_km
    h = cfg.relief.hack_exponent
    p = theta / h
    x0 = c_h * (a_star / 1e6) ** h
    x1 = c_h * (area / 1e6) ** h
    coef = k_s * R ** (-theta) * 1e6 ** (-theta) * c_h**p * 1e3
    return coef * (x1 ** (1 - p) - x0 ** (1 - p)) / (1 - p)


def test_channel_head_matches_s_crit():
    k_s = np.array([50.0, 150.0, 400.0])
    sc = np.array([0.6, 0.6, 1.0])
    R = np.array([0.3, 1.0, 0.05])
    a_star = channel_head_area(k_s, sc, R, CFG)
    # 하천 시작점에서 하천 경사 k_s·Q*^(−θ) 가 S_crit 와 같습니다(가이드 4장).
    np.testing.assert_allclose(k_s * (R * a_star) ** (-CFG.landscape.theta), sc, rtol=1e-12)
    zero = channel_head_area(np.array([0.0]), np.array([0.6]), np.array([0.3]), CFG)
    assert zero[0] == 0.0


def test_river_relief_quadrature_matches_analytic():
    g = l0_strip(4)
    k_s = np.array([60.0, 150.0, 300.0, 600.0])
    R = np.array([0.3, 0.5, 1.0, 0.1])
    sc = np.full(4, 0.6)
    a_star = channel_head_area(k_s, sc, R, CFG)
    assert np.all(a_star < g.area)
    got = river_relief(k_s, R, a_star, g.area, CFG)
    ref = analytic_river_relief(k_s, R, a_star, g.area, CFG)
    # 로그 간격 32점 사다리꼴(u = ln x): 피적분 함수가 e^((1−θ/h)u) 꼴이라 상대 오차는
    # ((1−θ/h)·Δu)²/12 ≈ 1e-5 이하입니다. 여유를 두어 1e-4 로 봅니다.
    assert N_QUADRATURE == 32
    np.testing.assert_allclose(got, ref, rtol=1e-4)
    # 하천 시작 면적이 칸보다 크면 칸 안에 하천이 없습니다.
    none = river_relief(k_s, R, np.full(4, 1e12), g.area, CFG)
    assert np.all(none == 0.0)


def test_hillslope_relief_formula():
    sc = np.array([0.6, 0.6, 1.0, 0.6])
    U = np.array([1e-3, 1e-6, 2e-3, -1e-4])
    a_star = np.array([7e5, 7e5, 2e6, 7e5])
    area = np.full(4, 19_500.0**2)
    got = hillslope_relief(sc, U, a_star, area, CFG)
    D = CFG.landscape.hillslope_diffusivity_m2_per_yr
    L = 0.5 * np.sqrt(a_star)
    ref = L * np.minimum(sc, np.maximum(U, 0) * L / D) / 2
    np.testing.assert_allclose(got, ref, rtol=1e-12)
    assert got[3] == 0.0  # 침강이면 사면 기복 없음
    assert got[0] == pytest.approx(L[0] * 0.6 / 2)  # 문턱 경사로 잘림


def test_relief_zero_on_ocean_and_increasing_with_ks():
    n = 40
    g = l0_strip(n)
    k_s = np.linspace(20.0, 600.0, n)
    is_ocean = np.zeros(n, dtype=bool)
    is_ocean[:3] = True
    z = np.linspace(0.0, 3000.0, n)
    relief, z_mean = subgrid_relief(
        g, z, k_s, np.full(n, 0.6), np.full(n, 0.3), np.full(n, 1e-3), is_ocean, CFG
    )
    assert np.all(relief[is_ocean] == 0.0)
    land = ~is_ocean
    assert np.all(relief[land] > 0.0)
    assert np.all(np.diff(relief[land]) > 0.0)  # k_s 가 클수록 기복이 큼
    np.testing.assert_allclose(z_mean, z + CFG.relief.mean_fraction * relief, rtol=1e-15)
    # 설계도 3장: L0 칸 안 능선 기복은 1~3 km 규모(k_s 150, R 0.3 근처)
    mid = np.argmin(np.abs(k_s - 150.0))
    assert 300.0 < relief[mid] < 5000.0


def test_relief_on_solved_sphere():
    R_planet = CFG.planet.radius_m
    g = sphere_graph(12, R_planet, jitter=0.4, seed=0)
    noise = fbm3(g.unit() * 2.0, 3)
    is_ocean = noise < np.quantile(noise, 0.6)
    U = np.where(is_ocean, 0.0, 1e-3)
    R = np.full(g.n_cells, 0.3)
    res = solve_steady_state(g, is_ocean, 0.0, U, R, None, CFG)
    relief, z_mean = subgrid_relief(g, res.z, res.k_s, res.s_crit_surface, R, U, is_ocean, CFG)
    assert np.all(relief[is_ocean] == 0.0)
    assert np.all(relief[~is_ocean] > 0.0) and np.all(np.isfinite(relief))
    assert np.all(z_mean >= res.z)
    # L0 칸(수백 km)은 선상지 반경(3 km)보다 커서 선상지가 생기지 않습니다.
    z_new, fan, _, apexes = add_fans(g, res.z, res, CFG)
    assert apexes == [] and not fan.any()
    np.testing.assert_array_equal(z_new, res.z)


def test_relief_invalid_inputs():
    g = l0_strip(5)
    ok = np.ones(5)
    ocean = np.zeros(5, dtype=bool)
    with pytest.raises(ValueError):
        subgrid_relief(g, ok[:-1], ok, ok, ok, ok, ocean, CFG)
    with pytest.raises(ValueError):
        subgrid_relief(g, ok, ok, ok, ok, ok, ocean.astype(int), CFG)
    with pytest.raises(ValueError):
        subgrid_relief(g, ok, np.full(5, np.nan), ok, ok, ok, ocean, CFG)
