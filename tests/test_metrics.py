"""점수표 지표(bpcg.metrics) 검사 (docs/pipeline.md 12장, 가이드 2장 '격자 정렬 지수').

답을 아는 합성 자료로 확인합니다.
- 고도 분포: 두 가우스 섞음에서 봉우리 두 개와 간격, 한 봉우리·가까운 봉우리는 불합격.
- Hack: 1×N 띠(L ∝ A, h ≈ 1)와 대각선 본류 망(L = √2·k·dx, A = (k+1)²·dx², h ≈ 0.5).
- 격자 정렬: 곧은 D8 하천 4.5(= 1 / (10/45)), 말 걸음 하천 0, 면 경계를 넘는 경로 제외,
  매끈한 원뿔에서 출발한 솔버 결과는 노드 흔들기가 지수를 낮춤.
- 법칙 자기일관성·수지: 솔버 결과에서 중앙값 < 1e-3, 수지 < 1e-6(한 칸을 고치면 잡아냄).
- 점수표: 입력이 없으면 건너뜀, 있는 입력으로 항목을 채움, 실패를 pass False 로 기록.
"""

import time

import numpy as np
import pytest

from bpcg.core.config import load_config
from bpcg.core.graph import flat_graph, sphere_graph
from bpcg.geology import rocks as rk
from bpcg.geology.model import LayerColumns, rock_at
from bpcg.hydro.accumulate import accumulate
from bpcg.hydro.routing import topo_order
from bpcg.landscape.solver import solve_steady_state
from bpcg.metrics.drainage import (
    alignment_counts,
    budget_error,
    grid_alignment,
    hack_fit,
    law_consistency,
    law_cross_mask,
    law_slope_from_fields,
    longest_flow_path,
    main_stem_mask,
    river_backflow_count,
    river_reach_fraction,
)
from bpcg.metrics.hypsometry import (
    bimodality,
    flat_fraction,
    gw_surface_fraction,
    hypsometry,
    hypsometry_distance,
    ocean_fraction,
    shelf_area,
)
from bpcg.metrics.scorecard import (
    DEFAULT_THRESHOLDS,
    cave_soluble_fraction,
    even_sample,
    failed_checks,
    rock_consistency,
    scorecard,
    thresholds,
    water_rule_violations,
)

CFG = load_config("earth", "tiny")
RANDOM_FRACTION = 10.0 / 45.0  # 가이드 2장: 방향이 무작위일 때 ±5° 안에 드는 비율


# ---------------------------------------------------------------- 합성 망
def strip_network(nx: int, dx: float = 1000.0):
    """1×nx 띠: 모든 칸이 동쪽으로 흐르고 마지막 칸이 출구."""
    g = flat_graph(1, nx, dx)
    rcv = np.minimum(np.arange(nx) + 1, nx - 1)
    return g, rcv


def diagonal_network(n: int, dx: float = 1000.0):
    """n×n 격자: 대각선 아래 칸은 동쪽, 위 칸은 남쪽, 대각선 칸은 남동쪽으로 흐름.

    대각선 칸 (k, k) 의 상류는 max(j, i) ≤ k 인 칸 전부라 A = (k+1)²·dx² 이고, 가장 긴 경로는
    (0, 0) 에서 대각선을 따라오는 L = √2·k·dx 입니다. 출구는 (n−1, n−1).
    """
    g = flat_graph(n, n, dx)
    jj, ii = np.divmod(np.arange(n * n), n)
    rcv = np.where(ii < jj, jj * n + ii + 1, np.where(ii > jj, (jj + 1) * n + ii, 0))
    diag = ii == jj
    rcv[diag] = np.minimum(jj[diag] + 1, n - 1) * (n + 1)
    return g, rcv.astype(np.int64)


def island(n: int, dx: float, jitter: float, seed: int = 1):
    g = flat_graph(n, n, dx, jitter=jitter, seed=seed)
    half = 0.5 * n * dx
    r = np.hypot(g.pos[:, 0] - half, g.pos[:, 1] + half)
    return g, r > 0.42 * n * dx, r


@pytest.fixture(scope="module")
def island_solution():
    """원형 섬(평면 96², 400 m, 노드 흔들기 0.4) 정상상태 + 합성 물·지하수 필드."""
    g, is_outlet, _ = island(96, 400.0, 0.4)
    U = np.where(is_outlet, 0.0, 1e-3)
    R = np.full(g.n_cells, 0.8)
    res = solve_steady_state(g, is_outlet, 0.0, U, R, None, CFG)
    f = res.fields()
    f["uplift_m_per_yr"] = U
    f["runoff_eff_m_per_yr"] = R
    f["is_ocean"] = is_outlet
    f["is_lake"] = np.zeros(g.n_cells, dtype=bool)
    f["is_river"] = (res.drainage_area >= 20 * g.area) & ~is_outlet
    hw = np.full(g.n_cells, np.nan)
    hw[f["is_river"]] = res.z[f["is_river"]] - 0.1
    hw[is_outlet] = 0.0
    f["water_level_m"] = hw
    # 지하수면: 물 칸은 수면, 육지는 하천 가까이(상류 면적 큰 칸)만 0.3 m 아래, 나머지는 5 m 아래
    zgw = res.z - np.where(res.drainage_area >= 5 * g.area, 0.3, 5.0)
    zgw = np.where(np.isfinite(hw), hw, zgw)
    f["water_table_m"] = zgw
    return g, is_outlet, res, f


# ---------------------------------------------------------------- 고도 분포
def test_hypsometry_is_area_weighted_histogram():
    rng = np.random.default_rng(3)
    z = rng.normal(0.0, 1000.0, 5000)
    a = rng.uniform(1.0, 3.0, 5000)
    edges, frac = hypsometry(z, a, 40)
    ref, _ = np.histogram(z, bins=edges, weights=a)
    assert edges.shape == (41,) and frac.shape == (40,)
    np.testing.assert_allclose(frac, ref / a.sum(), rtol=1e-12)
    assert abs(frac.sum() - 1.0) < 1e-12
    # 범위 일부만 칸으로 주면 범위 밖 면적은 빠집니다
    _, part = hypsometry(z, a, np.array([0.0, 1e9]))
    assert abs(part[0] - a[z >= 0].sum() / a.sum()) < 1e-12


def two_gaussians(rng, mu1, s1, w1, mu2, s2, n=40_000):
    k = int(n * w1)
    return np.concatenate([rng.normal(mu1, s1, k), rng.normal(mu2, s2, n - k)])


def test_bimodality_finds_continent_and_ocean_peaks():
    rng = np.random.default_rng(7)
    z = two_gaussians(rng, 500.0, 300.0, 0.3, -4000.0, 700.0)
    a = rng.uniform(0.5, 1.5, z.shape[0])
    r = bimodality(z, a)
    assert r["ok"] and len(r["peaks"]) == 2
    # 평활 σ 250 m 와 칸 폭 100 m 안에서 봉우리 위치가 맞음
    assert abs(r["peaks"][0] + 4000.0) < 200.0
    assert abs(r["peaks"][1] - 500.0) < 200.0
    assert abs(r["separation_m"] - 4500.0) < 300.0


def test_bimodality_rejects_single_and_close_peaks():
    rng = np.random.default_rng(8)
    one = bimodality(rng.normal(0.0, 800.0, 30_000), np.ones(30_000))
    assert not one["ok"] and one["n_peaks"] == 1 and one["separation_m"] == 0.0
    z = two_gaussians(rng, 0.0, 80.0, 0.5, 700.0, 80.0)
    close = bimodality(z, np.ones(z.shape[0]))
    assert close["n_peaks"] == 2 and not close["ok"]  # 1 km 보다 가까움
    assert abs(close["separation_m"] - 700.0) < 200.0
    # 면적 가중: 한쪽 봉우리 칸의 면적이 0 이면 봉우리 하나만 남음
    z = two_gaussians(rng, 500.0, 300.0, 0.3, -4000.0, 700.0)
    a = np.where(z > -1500.0, 0.0, 1.0)
    assert bimodality(z, a)["n_peaks"] == 1


def test_hypsometry_distance_is_ks():
    z = np.linspace(0.0, 1.0, 2001)
    a = np.ones_like(z)
    assert hypsometry_distance(z, a, z, a) == 0.0
    assert abs(hypsometry_distance(z, a, z + 0.5, a) - 0.5) < 2e-3


def test_ocean_and_shelf_area():
    z = np.array([-5000.0, -150.0, -150.0, -250.0, 100.0, -100.0])
    area = np.array([1.0, 2.0, 3.0, 4.0, 5.0, 6.0])
    crust = np.array([0, 1, 0, 1, 1, 1])
    ocean = np.array([True, True, True, True, False, False])  # 마지막 칸은 바다와 안 이어진 저지대
    assert ocean_fraction(ocean, area) == pytest.approx(10.0 / 21.0, rel=1e-15)
    s, frac = shelf_area(z, area, crust, ocean, depth=200.0)
    assert s == 2.0  # 대륙 지각 + 바다 + 수심 < 200 m 인 칸은 둘째 칸뿐
    assert frac == pytest.approx(2.0 / 10.0)
    with pytest.raises(ValueError):
        shelf_area(z, area, crust, ocean, depth=-1.0)


def test_flat_and_groundwater_fractions():
    slope = np.array([0.001, 0.05, 0.01, np.nan, 0.5])
    land = np.array([True, True, True, True, False])
    area = np.array([1.0, 1.0, 2.0, 1.0, 1.0])
    assert flat_fraction(slope, land, 0.02) == pytest.approx(2.0 / 3.0)
    assert flat_fraction(slope, land, 0.02, area) == pytest.approx(3.0 / 4.0)
    assert np.isnan(flat_fraction(slope, np.zeros(5, dtype=bool)))
    z = np.array([10.0, 10.0, 10.0, 10.0, 0.0])
    zgw = np.array([9.6, 9.4, 10.0, np.nan, 0.0])
    assert gw_surface_fraction(z, zgw, land, 0.5) == pytest.approx(2.0 / 3.0)
    with pytest.raises(ValueError):
        flat_fraction(slope, land.astype(int), 0.02)


# ---------------------------------------------------------------- Hack 법칙
def test_longest_path_and_main_stem_on_diagonal_network():
    n, dx = 40, 1000.0
    g, rcv = diagonal_network(n, dx)
    order = topo_order(rcv)
    length, main = longest_flow_path(g, rcv, order)
    k = np.arange(n)
    diag_cells = k * (n + 1)
    np.testing.assert_allclose(length[diag_cells], np.sqrt(2.0) * k * dx, rtol=1e-12)
    assert main[0] == -1 and (main[diag_cells[1:]] == diag_cells[:-1]).all()
    stem = main_stem_mask(rcv, order, main)
    # 출구의 기여 셀 셋(대각선, 마지막 행, 마지막 열)이 각각 하구라 본류가 셋입니다.
    jj, ii = np.divmod(np.arange(n * n), n)
    expect = (ii == jj) | (jj == n - 1) | (ii == n - 1)
    assert (stem == expect).all()


def _analytic_fit(A_m2, L_m):
    h, b = np.polyfit(np.log10(A_m2 / 1e6), np.log10(L_m / 1e3), 1)
    return 10.0**b, h


def test_hack_fit_diagonal_network_gives_half():
    n, dx = 200, 1000.0
    g, rcv = diagonal_network(n, dx)
    order = topo_order(rcv)
    area_up = accumulate(rcv, order, g.area)
    jj, ii = np.divmod(np.arange(n * n), n)
    side = (jj == n - 1) | (ii == n - 1)  # 마지막 행·열의 두 띠 본류 (L ∝ A)
    c, h = hack_fit(g, rcv, order, area_up, mask=~side | (ii == jj), min_area_cells=10.0)
    # 점: 대각선 칸 k = 3..n−2 (A ≥ 10 칸, 출구 제외), L = √2·k·dx, A = (k+1)²·dx²
    k = np.arange(3, n - 1)
    A_d, L_d = (k + 1.0) ** 2 * dx * dx, np.sqrt(2.0) * k * dx
    c_ref, h_ref = _analytic_fit(A_d, L_d)
    assert abs(h - h_ref) < 1e-10 and abs(c / c_ref - 1.0) < 1e-10
    # mask 없이: 두 띠 본류의 점 (i = 9..n−2, A = (i+1)·dx², L = i·dx) 이 더해집니다.
    i = np.arange(9, n - 1)
    A_s, L_s = (i + 1.0) * dx * dx, i * dx
    c_all, h_all = hack_fit(g, rcv, order, area_up, min_area_cells=10.0)
    c_ref, h_ref = _analytic_fit(np.concatenate([A_d, A_s, A_s]), np.concatenate([L_d, L_s, L_s]))
    assert abs(h_all - h_ref) < 1e-10 and abs(c_all / c_ref - 1.0) < 1e-10
    assert 0.5 < h < 0.56  # L = √2(√A − 1): 큰 A 에서 지수 1/2 로 다가감
    assert abs(c - np.sqrt(2.0)) < 0.25


def test_hack_fit_strip_gives_one_and_respects_mask():
    nx, dx = 500, 1000.0
    g, rcv = strip_network(nx, dx)
    order = topo_order(rcv)
    area_up = accumulate(rcv, order, g.area)
    c, h = hack_fit(g, rcv, order, area_up)
    i = np.arange(9, nx - 1)  # A = (i+1)·dx² ≥ 10 칸, 출구 제외
    c_ref, h_ref = _analytic_fit((i + 1.0) * dx * dx, i * dx)
    assert abs(h - h_ref) < 1e-10 and abs(c / c_ref - 1.0) < 1e-10
    assert abs(h - 1.0) < 0.03
    # mask 로 점을 거의 다 빼면 NaN
    mask = np.zeros(nx, dtype=bool)
    mask[100] = True
    assert np.isnan(hack_fit(g, rcv, order, area_up, mask=mask)[1])


# ---------------------------------------------------------------- 하천 연결·역류
def test_river_reach_and_backflow():
    # 0→1→2→3(출구 바다), 4→5→6(육지 막다른 칸, 자기 자신), 7→2 (7 은 강, 2 로 합류)
    rcv = np.array([1, 2, 3, 3, 5, 6, 6, 2])
    river = np.array([True, True, True, False, True, True, False, True])
    sink = np.array([False, False, False, True, False, False, False, False])
    order = topo_order(rcv)
    # 닿는 강 칸: 0, 1, 2, 7 (4 개), 못 닿는 강 칸: 4, 5 (6 은 강도 sink 도 아님)
    assert river_reach_fraction(rcv, order, river, sink) == pytest.approx(4.0 / 6.0)
    sink2 = sink.copy()
    sink2[6] = True  # 6 을 호수로 보면 모두 닿음
    assert river_reach_fraction(rcv, None, river, sink2) == 1.0
    hw = np.array([5.0, 4.0, 3.0, 0.0, 2.0, 2.5, np.nan, 2.9])
    # 4→5 는 2.0 → 2.5 (역류), 7→2 는 2.9 → 3.0 (역류), 5→6 은 6 이 NaN 이라 안 셈
    assert river_backflow_count(rcv, hw, river) == 2
    assert river_backflow_count(rcv, hw, river, tol=0.6) == 0


# ---------------------------------------------------------------- 법칙·수지
def test_law_consistency_synthetic():
    s = np.array([0.1, 0.2, 0.3, 0.9, 0.05])
    law = s * np.array([1.0 + 1e-4, 1.0 - 2e-4, 1.0, 0.5, 1.0])
    sc = np.array([1.0, 1.0, 1.0, 0.9, 1.0])
    r = law_consistency(s, law, sc)
    # 넷째 칸은 S = S_crit 이라 빠짐
    assert r["n"] == 4
    assert r["median"] == pytest.approx(np.median([1e-4 / 1.0001, 2e-4 / 0.9998, 0.0, 0.0]))
    assert r["max"] == pytest.approx(2e-4 / 0.9998)
    mask = np.array([False, True, False, False, False])
    assert law_consistency(s, law, sc, mask)["n"] == 1
    assert np.isnan(law_consistency(s, law, sc * 0.0)["median"])


def test_law_consistency_on_solver_output(island_solution):
    g, is_outlet, res, f = island_solution
    law = law_slope_from_fields(
        g,
        res.receiver,
        res.discharge,
        res.drainage_area,
        res.sediment_flux,
        f["uplift_m_per_yr"],
        res.k_s,
        res.s_crit_surface,
        CFG,
    )
    assert np.isnan(law[is_outlet]).all()
    r = law_consistency(res.slope, law, res.s_crit_surface, ~is_outlet)
    # pipeline.md 7.1·12장: 균일 암석에서 S < S_crit 인 칸은 상대 차이 < 1e-3
    assert r["n"] > 1000 and r["median"] < 1e-3 and r["max"] < 1e-3


def test_law_cross_mask_on_layered_solver_output():
    """층 경계(고도 150 m)를 넘는 칸을 빼면 지표 암석 하나의 법칙과 맞습니다."""
    g, is_outlet, _ = island(64, 400.0, 0.4)
    n = g.n_cells
    bottom = np.full((n, 1), 150.0)
    rock = np.tile(np.array([rk.SHALE, rk.GRANITE], dtype=np.uint8), (n, 1))
    layers = LayerColumns.from_columns(bottom, rock)
    U = np.where(is_outlet, 0.0, 2e-3)
    res = solve_steady_state(g, is_outlet, 0.0, U, 0.8, layers, CFG)
    law = law_slope_from_fields(
        g, res.receiver, res.discharge, res.drainage_area, res.sediment_flux,
        U, res.k_s, res.s_crit_surface, CFG,
    )  # fmt: skip
    cross = law_cross_mask(res.receiver, res.z, bottom)
    assert 0 < cross.sum() < 0.2 * n
    keep = law_consistency(res.slope, law, res.s_crit_surface, ~is_outlet & ~cross)
    assert keep["median"] < 1e-3 and keep["max"] < 1e-3
    every = law_consistency(res.slope, law, res.s_crit_surface, ~is_outlet)
    assert every["max"] > 1e-2  # 넘는 칸은 두 층 경사의 평균이라 크게 어긋남


def test_budget_error_detects_errors():
    n, dx = 30, 1000.0
    g, rcv = diagonal_network(n, dx)
    order = topo_order(rcv)
    src = g.area * np.linspace(0.1, 1.0, g.n_cells)
    q = accumulate(rcv, order, src)
    assert budget_error(rcv, q, src, order) < 1e-14
    bad = q.copy()
    bad[5] += 1e-4 * src.sum()
    assert budget_error(rcv, bad, src) == pytest.approx(1e-4, rel=1e-6)
    # 침강이 있는 퇴적물: 누적 뒤 0 에서 자름
    u = np.where(np.arange(g.n_cells) % 7 == 0, -3.0, 1.0) * 1e-3 * g.area
    qs = np.maximum(accumulate(rcv, order, u), 0.0)
    assert (qs == 0.0).any()
    assert budget_error(rcv, qs, u, order, clip_negative=True) < 1e-14
    assert budget_error(rcv, qs, u, order) > 1e-3  # 자르지 않은 수지와는 다름


# ---------------------------------------------------------------- 격자 정렬 지수
def test_alignment_straight_rivers_and_knight_moves():
    n = 40
    g = flat_graph(n, n, 100.0, jitter=0.4, seed=2)  # 흔든 위치는 쓰지 않음
    jj, ii = np.divmod(np.arange(n * n), n)
    allr = np.ones(n * n, dtype=bool)
    east = np.where(ii < n - 1, jj * n + ii + 1, jj * n + ii)
    assert grid_alignment(g, east, allr) == pytest.approx(1.0 / RANDOM_FRACTION)
    hit, used = alignment_counts(g, east, allr)
    assert hit == used == n * (n - 6)  # 6칸 가기 전에 출구에 닿는 칸은 뺌
    se = np.where((ii < n - 1) & (jj < n - 1), (jj + 1) * n + ii + 1, jj * n + ii)
    assert grid_alignment(g, se, allr) == pytest.approx(4.5)
    # 말 걸음: 짝수 열은 동쪽, 홀수 열은 남동쪽 → 6칸 뒤 (Δi, Δj) = (6, 3), 26.6°
    knight = np.where(
        (ii < n - 1) & (jj < n - 1), np.where(ii % 2 == 0, jj * n + ii + 1, (jj + 1) * n + ii + 1),
        jj * n + ii,
    )  # fmt: skip
    hit, used = alignment_counts(g, knight, allr)
    assert used > 0 and hit == 0
    assert np.isnan(grid_alignment(g, east, np.zeros(n * n, dtype=bool)))
    with pytest.raises(ValueError):
        grid_alignment(g, east, allr, band="middle")


def test_alignment_sphere_bands_and_face_crossing():
    n = 20
    g = sphere_graph(n, 1.0e6, jitter=0.4, seed=1)
    rcv = g.nbr[:, 4].astype(np.int64)  # 슬롯 4 = (dj, di) = (0, +1): 면 안에서 +i 쪽
    allr = np.ones(g.n_cells, dtype=bool)
    hit, used = alignment_counts(g, rcv, allr, "all")
    # i ≤ n−7 인 칸만 6칸 동안 같은 면에 머묾. 면을 넘는 경로는 뺌
    assert hit == used == 6 * n * (n - 6)
    he, ue = alignment_counts(g, rcv, allr, "edge")
    hc, uc = alignment_counts(g, rcv, allr, "center")
    # 면 좌표 |a| > 0.9: i ∈ {0, n−1}, |b| > 0.9: j ∈ {0, n−1}. 쓰는 칸은 i ≤ n−7
    edge_per_face = 2 * (n - 6) + (n - 2)
    assert ue == 6 * edge_per_face and uc == used - ue
    assert he == ue and hc == uc
    assert grid_alignment(g, rcv, allr, "edge") == pytest.approx(4.5)
    assert grid_alignment(g, rcv, allr, "center") == pytest.approx(4.5)


def test_alignment_jitter_lowers_index_on_solver_output():
    """매끈한 원뿔에서 출발한 원형 섬: 흔들지 않은 D8 은 격자에 정렬, 흔들면 낮아짐(가이드 2장)."""
    out = {}
    for jit in (0.0, 0.4):
        n, dx = 64, 400.0
        g, is_outlet, r = island(n, dx, jit)
        U = np.where(is_outlet, 0.0, 1e-3)
        z0 = np.maximum(0.42 * n * dx - r, 0.0) * 1e-2
        res = solve_steady_state(g, is_outlet, 0.0, U, 1.0, None, CFG, z_init=z0)
        river = (res.drainage_area >= 5 * g.area) & ~is_outlet
        out[jit] = grid_alignment(g, res.receiver, river)
    assert out[0.0] > 2.0  # 가이드 2장: 원형 섬 D8 약 2.9
    assert out[0.4] < 0.8 * out[0.0]


# ---------------------------------------------------------------- 일관성 지표
def test_rock_consistency_and_caves():
    assert rock_consistency(np.array([1, 2, 3]), np.array([1, 2, 3])) == 1.0
    assert rock_consistency(np.array([1, 2, 3, 4]), np.array([1, 2, 0, 4])) == 0.75
    assert np.isnan(rock_consistency(np.array([]), np.array([])))
    with pytest.raises(ValueError):
        rock_consistency(np.array([1, 2]), np.array([1]))
    bottom = np.array([[100.0, -200.0], [100.0, -200.0], [50.0, 0.0]])
    rock = np.array([[1, 3, 4], [1, 3, 4], [9, 2, 4]], dtype=np.uint8)
    lv0 = np.array([0.0, np.nan, 60.0])  # 석회암, 없음, 대리암
    lv1 = np.array([-300.0, np.nan, np.nan])  # 화강암(녹지 않음)
    v, cnt = cave_soluble_fraction([lv0], bottom, rock)
    assert v == 1.0 and cnt == 2
    v, cnt = cave_soluble_fraction([lv0, lv1], bottom, rock)
    assert v == pytest.approx(2.0 / 3.0) and cnt == 3
    v, cnt = cave_soluble_fraction([np.full(3, np.nan)], bottom, rock)
    assert np.isnan(v) and cnt == 0
    assert list(even_sample(np.array([True, False, True, True]), 10)) == [0, 2, 3]
    assert even_sample(np.ones(1000, dtype=bool), 10).shape == (10,)


def test_water_rule_violations():
    z = np.array([10.0, 10.0, 0.0, 5.0, 20.0, 8.0])
    hw = np.array([np.nan, np.nan, 0.0, 4.0, np.nan, np.nan])
    zgw = np.array([9.0, 10.5, 0.0, 4.5, -400.0, np.nan])
    r = water_rule_violations(z, zgw, hw, max_depth=300.0)
    assert r == {"above": 1, "water": 1, "deep": 1, "nan": 1, "total": 4}
    assert water_rule_violations(z, zgw, hw)["deep"] == 0


# ---------------------------------------------------------------- 점수표
def test_scorecard_without_inputs_skips_everything():
    card = scorecard()
    assert {k.split(".")[0] for k in card} == {"planet", "hero"}
    for k in (
        "planet.ocean_fraction", "planet.shelf_area", "planet.hypsometry_bimodal",
        "planet.hack_exponent", "planet.law_consistency", "planet.grid_alignment",
        "hero.flat_fraction", "hero.grid_alignment", "hero.water_budget",
    ):  # fmt: skip
        assert k in card
    for v in card.values():
        assert set(v) == {"value", "unit", "kind", "pass", "note"}
        assert v["kind"] in ("forced", "emergent", "check")
        assert v["value"] is None and v["pass"] is None and v["note"].startswith("건너뜀")
    assert failed_checks(card) == []


def test_scorecard_hero_from_solver(island_solution):
    g, is_outlet, res, f = island_solution
    diag = {"hero": res.diag()}
    card = scorecard(hero_fields=f, hero_graph=g, diag=diag, cfg=CFG)
    assert failed_checks(card) == []
    for k in (
        "hero.law_consistency", "hero.water_budget", "hero.sediment_budget",
        "hero.river_reach_fraction", "hero.river_backflow", "hero.water_rule_violations",
        "hero.grid_alignment", "hero.solver_converged",
    ):  # fmt: skip
        assert card[k]["kind"] == "check" and card[k]["pass"] is True, (k, card[k])
    assert card["hero.law_consistency"]["value"] < 1e-3
    assert card["hero.water_budget"]["value"] <= 1e-6
    assert card["hero.river_backflow"]["value"] == 0
    for k in ("hero.hack_exponent", "hero.flat_fraction", "hero.gw_surface_fraction"):
        assert card[k]["kind"] == "emergent" and card[k]["pass"] is None
        assert 0.0 <= card[k]["value"] <= 1.0
    land = ~is_outlet
    expect = (f["z_m"] - f["water_table_m"] <= 0.5)[land].mean()
    assert card["hero.gw_surface_fraction"]["value"] == pytest.approx(expect)
    assert card["planet.ocean_fraction"]["value"] is None  # 행성 입력 없음

    # 물 수지를 한 칸 어긋나게 하면 잡아냄
    f2 = dict(f)
    q = f["discharge_m3_per_yr"].copy()
    q[np.argmax(q)] *= 1.001
    f2["discharge_m3_per_yr"] = q
    card2 = scorecard(hero_fields=f2, hero_graph=g, diag=diag, cfg=CFG)
    assert card2["hero.water_budget"]["pass"] is False
    assert "hero.water_budget" in failed_checks(card2)

    # 잘못된 입력은 '계산 실패' 로 남고 check 는 불합격
    card3 = scorecard(
        hero_fields=f, hero_graph=g, diag={"hero": {"law_slope": np.ones(3)}}, cfg=CFG
    )
    assert card3["hero.law_consistency"]["pass"] is False
    assert card3["hero.law_consistency"]["note"].startswith("계산 실패")


def test_scorecard_planet_on_sphere():
    n = 16
    g = sphere_graph(n, 6.371e6, jitter=0.4, seed=0)
    N = g.n_cells
    p = g.unit()
    rng = np.random.default_rng(0)
    continental = p[:, 2] > 0.3  # 북쪽 모자 = 대륙
    z = np.where(
        continental,
        400.0 + 200.0 * rng.standard_normal(N),
        -4000.0 + 500.0 * rng.standard_normal(N),
    )
    z[continental & (p[:, 2] < 0.36)] = -100.0  # 대륙 가장자리의 얕은 바다 = 대륙붕
    is_ocean = z < 0.0
    rcv = g.nbr[:, 4].astype(np.int64)  # +i 쪽으로 곧게 흐르는 가짜 하천
    fields = {
        "z_m": z,
        "is_ocean": is_ocean,
        "crust_type": continental.astype(np.uint8),
        "receiver": rcv,
        "is_river": ~is_ocean,
    }
    card = scorecard(planet_fields=fields, planet_graph=g, cfg=CFG)
    assert card["planet.ocean_fraction"]["value"] == pytest.approx(
        g.area[is_ocean].sum() / g.area.sum()
    )
    shelf = is_ocean & continental & (z > -200.0)
    assert card["planet.shelf_area"]["value"] == pytest.approx(g.area[shelf].sum() / 1e6)
    assert card["planet.hypsometry_bimodal"]["pass"] is True
    assert card["planet.hypsometry_bimodal"]["kind"] == "forced"
    # 곧은 하천은 격자 정렬 4.5 로 목표 1.3 을 넘어 불합격, 두 띠 모두 보고
    for band in ("edge", "center"):
        e = card[f"planet.grid_alignment_{band}"]
        assert e["value"] == pytest.approx(4.5) and e["pass"] is False
    assert "planet.grid_alignment" not in card
    assert card["planet.law_consistency"]["note"].startswith("건너뜀")
    with pytest.raises(ValueError):
        scorecard(planet_fields={"z_m": z[:10]}, planet_graph=g)


def test_scorecard_rock_consistency_from_columns():
    n = 32
    g = flat_graph(n, n, 100.0)
    N = g.n_cells
    z = np.linspace(0.0, 400.0, N)
    bottom = np.tile(np.array([300.0, 100.0]), (N, 1))
    rock = np.tile(np.array([rk.SANDSTONE, rk.LIMESTONE, rk.GRANITE], dtype=np.uint8), (N, 1))
    surf = rock_at(bottom, rock, z)
    fields = {
        "z_m": z,
        "surface_rock": surf,
        "strata_bottom_m": bottom,
        "strata_rock": rock,
        "s_crit": rk.S_CRIT[surf].astype(np.float32),
        "cave_level_0_m": np.where(np.arange(N) % 5 == 0, 200.0, np.nan),
    }
    card = scorecard(hero_fields=fields, hero_graph=g)
    assert card["hero.rock_consistency"]["value"] == 1.0
    assert card["hero.rock_consistency"]["pass"] is True
    assert card["hero.cave_in_soluble"]["value"] == 1.0
    bad = dict(fields)
    sr = surf.copy()
    sr[:10] = rk.SHALE
    bad["surface_rock"] = sr
    bad["cave_level_0_m"] = np.where(np.arange(N) % 5 == 0, 50.0, np.nan)  # 화강암
    card = scorecard(hero_fields=bad, hero_graph=g)
    assert card["hero.rock_consistency"]["pass"] is False
    assert card["hero.rock_consistency"]["value"] == pytest.approx(1.0 - 10.0 / N)
    assert card["hero.cave_in_soluble"]["value"] == 0.0
    # 3D 함수 표본을 diag 로 주면 그것을 씀
    card = scorecard(diag={"hero": {"rock_samples": (np.array([1, 2]), np.array([1, 2]))}})
    assert card["hero.rock_consistency"]["value"] == 1.0


def test_thresholds_follow_config():
    assert thresholds(None) == DEFAULT_THRESHOLDS
    cfg = CFG.with_overrides({"metrics.alignment_max": 5.0})
    th = thresholds(cfg)
    assert th["alignment_max"] == 5.0 and th["budget_max"] == DEFAULT_THRESHOLDS["budget_max"]


# ---------------------------------------------------------------- 속도 (L0 크기)
@pytest.mark.slow
def test_benchmark_l0_size():
    """구면 면당 512 칸(157만 칸)에서 무거운 지표 시간을 잽니다."""
    n = 512
    g = sphere_graph(n, 6.371e6, jitter=0.4, seed=0)
    N = g.n_cells
    z = g.unit()[:, 2] * 3000.0 + 10.0 * np.sin(np.arange(N) * 0.37)
    rcv = g.nbr[:, 4].astype(np.int64)
    rcv[rcv < 0] = np.arange(N)[rcv < 0]
    # 면 경계를 넘는 고리를 끊어 트리로 만듦: 면의 마지막 열은 출구
    rcv[(np.arange(N) % n) == n - 1] = np.arange(N)[(np.arange(N) % n) == n - 1]
    order = topo_order(rcv)
    area_up = accumulate(rcv, order, g.area)
    river = area_up >= 10 * g.area
    grid_alignment(g, rcv, river, "edge")
    hack_fit(g, rcv, order, area_up)
    t = time.perf_counter()
    grid_alignment(g, rcv, river, "edge")
    t_align = time.perf_counter() - t
    t = time.perf_counter()
    hack_fit(g, rcv, order, area_up)
    t_hack = time.perf_counter() - t
    t = time.perf_counter()
    budget_error(rcv, area_up, g.area, order)
    t_budget = time.perf_counter() - t
    t = time.perf_counter()
    card = scorecard(
        planet_fields={"z_m": z, "is_ocean": z < 0, "receiver": rcv, "is_river": river,
                       "drainage_area_m2": area_up, "crust_type": (z > -500).astype(np.uint8)},
        planet_graph=g,
    )  # fmt: skip
    t_card = time.perf_counter() - t
    print(
        f"\nN={N}: align {t_align:.3f}s hack {t_hack:.3f}s "
        f"budget {t_budget:.3f}s card {t_card:.3f}s"
    )
    assert card["planet.hack_exponent"]["value"] is not None
    assert t_align < 2.0 and t_hack < 5.0 and t_card < 30.0
