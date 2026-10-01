"""정상상태 솔버(bpcg.landscape.solver) 검사 (docs/pipeline.md 7.1 '검사', 가이드 4장 '확인하기').

- 수렴: 원형 섬(평면 96², 400 m, 노드 흔들기)에서 max_flow_iterations(200) 안에 멈춤, 기록 남음.
- 웅덩이 없음: 육지에서 fill_depressions(z) == z.
- 법칙 자기일관성: 균일 암석에서 S < S_crit 인 칸은 |S − 법칙|/법칙 < 1e-3.
- 퇴적물 수지: Σ_출구 Qs = Σ U·A (U ≥ 0, 상대 1e-6).
- 2층 해석해: 1×N 띠의 층별 적분이 손으로 푼 조각별 해와 1e-3 m 안.
- G-법칙: 융기 없는 저지대에서 경사 > s_min 이고 하류로 갈수록 줄어듦(충적 경사).
- 멈춤 조건: 마지막 반복만 (n_changed == 0 그리고 max|Δz| < stop_dz) 를 만족.
"""

import numpy as np
import pytest

from bpcg.core.config import load_config
from bpcg.core.graph import flat_graph, sphere_graph
from bpcg.core.noise import fbm3
from bpcg.geology.model import LayerColumns
from bpcg.hydro.depressions import fill_depressions, fill_epsilon
from bpcg.hydro.routing import d8_receivers
from bpcg.landscape.solver import SolverResult, initial_surface, solve_steady_state

CFG = load_config("earth", "tiny")


# ---------------------------------------------------------------- 시험 지형
def island(n: int = 96, dx: float = 400.0, jitter: float = 0.4, seed: int = 1):
    """원형 섬: 반지름 0.42·한 변 바깥이 출구(바다)."""
    g = flat_graph(n, n, dx, jitter=jitter, seed=seed)
    half = 0.5 * n * dx
    r = np.hypot(g.pos[:, 0] - half, g.pos[:, 1] + half)
    is_outlet = r > 0.42 * n * dx
    return g, is_outlet


def law_slope(cfg, U, Q, Qs, A_up, width, k_mult, s_crit):
    """pipeline.md 7.1 경사 법칙을 numpy 로 다시 계산합니다 (솔버 커널과 독립)."""
    ls = cfg.landscape
    n = ls.slope_exponent_n
    E = U + ls.deposition_g * cfg.climate.runoff_ref_m_per_yr * Qs / Q
    K = ls.u_ref_m_per_yr / ls.k_ref**n * k_mult
    with np.errstate(divide="ignore", invalid="ignore"):
        S_r = (E / K) ** (1.0 / n) * Q ** (-ls.theta)
        S_h = E * (A_up / width) / ls.hillslope_diffusivity_m2_per_yr
        S = 1.0 / (1.0 / S_r + 1.0 / S_h)
    S = np.minimum(S, s_crit)
    S = np.maximum(S, ls.s_min)
    return np.where(E > 0, S, ls.s_min)


@pytest.fixture(scope="module")
def island_result():
    g, is_outlet = island()
    U = np.where(is_outlet, 0.0, 1e-3)
    R = np.full(g.n_cells, 1.0)
    res = solve_steady_state(g, is_outlet, 0.0, U, R, None, CFG)
    return g, is_outlet, U, R, res


# ---------------------------------------------------------------- (1) 수렴 + 웅덩이 없음
def test_island_converges_with_history(island_result):
    g, is_outlet, U, R, res = island_result
    assert isinstance(res, SolverResult)
    assert res.converged
    # pipeline.md 7.1: 원형 섬(평면 96², 400 m)에서 200회 안에 멈춤
    assert res.iterations <= CFG.landscape.max_flow_iterations
    assert len(res.history) == res.iterations
    assert [h["iteration"] for h in res.history] == list(range(1, res.iterations + 1))
    assert set(res.history[0]) == {"iteration", "n_changed", "max_dz", "n_frozen"}
    assert res.history[0]["n_changed"] == g.n_cells  # 첫 반복은 prev 가 없음
    assert res.seconds > 0
    assert np.all(res.z[is_outlet] == 0.0)
    assert res.z[~is_outlet].min() > 0.0
    # 산이 실제로 솟았는지(정상상태 경사로 쌓은 기복): 수백 m 이상
    assert res.z.max() > 500.0


def test_island_has_no_pits(island_result):
    g, is_outlet, U, R, res = island_result
    zh = fill_depressions(res.z, g.nbr, is_outlet)
    land = ~is_outlet
    np.testing.assert_array_equal(zh[land], res.z[land])
    # 출구가 아닌 모든 칸은 수신 셀보다 엄밀히 높습니다.
    assert np.all(res.z[land] > res.z[res.receiver[land]])


def test_solver_surface_is_epsilon_fill_fixed_point(island_result):
    """솔버가 쌓은 z 는 ε 채움의 고정점입니다(그래서 다음 반복의 채움을 건너뛸 수 있음)."""
    g, is_outlet, U, R, res = island_result
    eps = CFG.landscape.fill_epsilon_m
    land = ~is_outlet
    assert np.all(res.z[land] >= res.z[res.receiver[land]] + eps)
    np.testing.assert_array_equal(fill_epsilon(res.z, g.nbr, is_outlet, eps), res.z)


def test_epsilon_floor_on_fine_grid():
    """s_min·d < ε 인 작은 칸(25 m)의 평지에서도 한 칸 오르는 높이는 ε 이상이고 수렴합니다."""
    eps = CFG.landscape.fill_epsilon_m
    N, dx = 200, 25.0
    assert CFG.landscape.s_min * dx < eps
    g, is_outlet = strip(N, dx)
    U = np.zeros(N)  # E = 0 → 법칙 경사는 s_min
    res = solve_steady_state(g, is_outlet, 0.0, U, np.full(N, 0.5), None, CFG)
    assert res.converged
    np.testing.assert_allclose(np.diff(res.z), eps, rtol=1e-9)
    np.testing.assert_array_equal(fill_epsilon(res.z, g.nbr, is_outlet, eps), res.z)


def test_avulsing_plain_converges_with_frozen_cells():
    """융기 없는 2D 퇴적 평야(G = 1)는 D8 정상상태가 없습니다(강이 높은 둑 위로 흘러 옆으로 넘침).

    진동 칸 고정으로 멈추고, 고정된 칸도 수신 셀보다 높아 웅덩이가 없습니다.
    """
    ny, nx, dx = 60, 60, 50.0
    g = flat_graph(ny, nx, dx, jitter=0.4, seed=2)
    row = np.arange(g.n_cells) // nx
    is_outlet = row == ny - 1
    U = np.where(row < 20, 3e-3, 0.0)
    res = solve_steady_state(g, is_outlet, 0.0, U, np.full(g.n_cells, 1.0), None, CFG)
    assert res.converged and res.n_frozen > 0
    assert res.frozen.sum() == res.n_frozen
    # 고정은 주로 평야에서 일어납니다.
    assert res.frozen[row < 20].mean() < 0.5 * res.frozen[row >= 20].mean()
    land = ~is_outlet
    assert np.all(res.z[land] > res.z[res.receiver[land]])
    zh = fill_depressions(res.z, g.nbr, is_outlet)
    np.testing.assert_array_equal(zh, res.z)


def test_converged_receivers_are_fixed_point(island_result):
    """수렴한 z 로 물길을 다시 계산해도 수신 셀이 그대로입니다(가이드 4장 고정점)."""
    g, is_outlet, U, R, res = island_result
    assert res.n_frozen == 0  # 보통 지형에서는 진동 칸 고정이 쓰이지 않습니다.
    zt = fill_epsilon(res.z, g.nbr, is_outlet, CFG.landscape.fill_epsilon_m)
    rcv, _, n_changed = d8_receivers(
        zt, g.nbr, g.dist, is_outlet, prev=res.receiver, eta=CFG.landscape.hysteresis_eta
    )
    assert n_changed == 0
    np.testing.assert_array_equal(rcv, res.receiver)


def test_fields_helper_uses_field_names(island_result):
    g, is_outlet, U, R, res = island_result
    f = res.fields()
    assert set(f) == {
        "z_m",
        "receiver",
        "drainage_area_m2",
        "discharge_m3_per_yr",
        "sediment_flux_m3_per_yr",
        "slope",
        "k_s",
        "s_crit",
    }
    assert f["z_m"].dtype == np.float64
    fc = res.fields(cast=True)
    assert fc["z_m"].dtype == np.float32 and fc["receiver"].dtype == np.int32
    d = res.diag()
    assert d["converged"] and d["final_n_changed"] == 0


# ---------------------------------------------------------------- (2) 법칙 자기일관성
@pytest.mark.parametrize("n_exp", [1.0, 1.5])
def test_law_self_consistency_uniform_rock(n_exp):
    cfg = CFG.with_overrides({"landscape.slope_exponent_n": n_exp})
    g, is_outlet = island(seed=3)
    # 공간적으로 변하는 U(침강 포함)로 G 항과 E ≤ 0 분기까지 시험합니다.
    x = g.pos[:, 0] / (96 * 400.0)
    U = np.where(is_outlet, 0.0, 2e-3 * x - 2e-4)
    R = np.full(g.n_cells, 0.5)
    res = solve_steady_state(g, is_outlet, 0.0, U, R, None, cfg)
    # 침강 해안에서는 E 부호가 수신 셀에 따라 바뀌어 η 만으로는 순환이 남습니다.
    # 그래서 진동 칸 고정이 쓰입니다.
    assert res.converged and res.n_frozen > 0
    assert res.n_frozen < 0.01 * g.n_cells
    land = ~is_outlet
    # 고정된 칸도 수신 셀보다 엄밀히 높습니다(웅덩이 없음).
    assert np.all(res.z[land] > res.z[res.receiver[land]])
    law = law_slope(
        cfg,
        U,
        res.discharge,
        res.sediment_flux,
        res.drainage_area,
        g.width,
        1.0,
        cfg.landscape.s_crit_default,
    )
    S = res.slope
    below = land & (S < cfg.landscape.s_crit_default)
    assert below.sum() > 100
    rel = np.abs(S[below] - law[below]) / law[below]
    # pipeline.md 7.1: 균일 암석에서 S < S_crit 인 칸은 |S − 법칙|/법칙 < 1e-3
    assert rel.max() < 1e-3
    # S_crit 로 잘린 칸은 정확히 S_crit (반올림 안)
    at_crit = land & ~below
    np.testing.assert_allclose(S[at_crit], cfg.landscape.s_crit_default, rtol=1e-9)
    # 침강으로 E ≤ 0 인 칸이 실제로 있고, 그 칸은 s_min
    E = U + cfg.landscape.deposition_g * cfg.climate.runoff_ref_m_per_yr * (
        res.sediment_flux / res.discharge
    )
    neg = land & (E <= 0)
    assert neg.any()
    np.testing.assert_allclose(S[neg], cfg.landscape.s_min, rtol=1e-9)
    assert np.all(res.k_s[neg] == 0.0)
    # k_s = (E/K)^(1/n) (기준 암석)
    pos = land & (E > 0)
    ks = cfg.landscape.k_ref * (E[pos] / cfg.landscape.u_ref_m_per_yr) ** (1.0 / n_exp)
    np.testing.assert_allclose(res.k_s[pos], ks, rtol=1e-12)


# ---------------------------------------------------------------- (3) 퇴적물 수지
def test_sediment_budget(island_result):
    g, is_outlet, U, R, res = island_result
    outlets = res.receiver == np.arange(g.n_cells)
    total = np.sum(U * g.area)
    # pipeline.md 7.1: Σ_출구 Qs = Σ U·A (침강 없을 때) 상대 1e-6
    assert abs(res.sediment_flux[outlets].sum() - total) / total < 1e-6
    # 물도 같은 방식으로 보존됩니다.
    q_total = np.sum(R * g.area)
    assert abs(res.discharge[outlets].sum() - q_total) / q_total < 1e-12


def test_extra_inflow_flows_downstream():
    g, is_outlet = island(n=48, seed=5)
    U = np.where(is_outlet, 0.0, 1e-3)
    R = np.full(g.n_cells, 1.0)
    inflow = np.zeros(g.n_cells)
    src = int(np.flatnonzero(~is_outlet)[len(np.flatnonzero(~is_outlet)) // 2])
    inflow[src] = 5e8
    res = solve_steady_state(g, is_outlet, 0.0, U, R, None, CFG, extra_inflow=inflow)
    assert res.converged
    outlets = res.receiver == np.arange(g.n_cells)
    total = np.sum(R * g.area) + inflow.sum()
    assert abs(res.discharge[outlets].sum() - total) / total < 1e-12
    # 들어온 물은 그 칸에서 하류로 끝까지 함께 흐릅니다.
    c = src
    while res.receiver[c] != c:
        assert res.discharge[c] >= 5e8
        c = res.receiver[c]


# ---------------------------------------------------------------- (4) 2층 해석해
def strip(N: int, dx: float):
    g = flat_graph(1, N, dx)
    is_outlet = np.zeros(N, dtype=bool)
    is_outlet[0] = True
    return g, is_outlet


def hand_profile(cfg, N, dx, U, R, z_out, zb, km_soft, km_hard, sc_soft, sc_hard):
    """1×N 띠(출구는 0번 칸)의 조각별 정확한 해를 칸마다 손으로 적분합니다."""
    ls = cfg.landscape
    n = ls.slope_exponent_n

    def slope(Q, A, E, km, sc):
        ks = ls.k_ref * (E / (ls.u_ref_m_per_yr * km)) ** (1.0 / n)
        S_r = ks * Q ** (-ls.theta)
        S_h = E * (A / dx) / ls.hillslope_diffusivity_m2_per_yr
        S = 1.0 / (1.0 / S_r + 1.0 / S_h)
        return max(min(S, sc), ls.s_min)

    z = np.zeros(N)
    z[0] = z_out
    for i in range(1, N):
        A = dx * dx * (N - i)  # 상류 면적이 선형으로 늘어남
        Q = R * A
        E = U + ls.deposition_g * cfg.climate.runoff_ref_m_per_yr * (U * A) / Q
        z0 = z[i - 1]
        if z0 < zb:
            s1 = slope(Q, A, E, km_hard, sc_hard)
            if z0 + s1 * dx <= zb:
                z[i] = z0 + s1 * dx
            else:
                xb = (zb - z0) / s1
                z[i] = zb + slope(Q, A, E, km_soft, sc_soft) * (dx - xb)
        else:
            z[i] = z0 + slope(Q, A, E, km_soft, sc_soft) * dx
    return z


@pytest.mark.parametrize("padding", [False, True])
def test_two_layer_strip_matches_piecewise_solution(padding):
    # 사면 확산 D 를 키워 S_h 도 경사에 들어오게 합니다(조화 결합 시험).
    cfg = CFG.with_overrides({"landscape.hillslope_diffusivity_m2_per_yr": 50.0})
    N, dx, U, R = 200, 1000.0, 1e-3, 1.0
    z_out, zb = 5.0, 600.0
    km_soft, km_hard = 3.0, 0.3  # 위층 무름(셰일 류), 아래층 단단함
    sc_soft, sc_hard = 0.6, 1.0
    g, is_outlet = strip(N, dx)
    if padding:
        # 두께 0 층(짧은 템플릿의 빈 층)은 고르지 않아야 합니다: 가운데 층 값은 엉뚱하게 둡니다.
        bottom = np.column_stack([np.full(N, zb), np.full(N, zb)])
        k_mult = np.tile([km_soft, 99.0, km_hard], (N, 1))
        s_crit = np.tile([sc_soft, 0.01, sc_hard], (N, 1))
    else:
        bottom = np.full((N, 1), zb)
        k_mult = np.tile([km_soft, km_hard], (N, 1))
        s_crit = np.tile([sc_soft, sc_hard], (N, 1))
    rock = np.zeros(k_mult.shape, dtype=np.uint8)
    layers = LayerColumns(bottom=bottom, rock=rock, k_mult=k_mult, s_crit=s_crit)
    res = solve_steady_state(g, is_outlet, z_out, np.full(N, U), np.full(N, R), layers, cfg)
    assert res.converged
    np.testing.assert_array_equal(res.receiver[1:], np.arange(N - 1))
    ref = hand_profile(cfg, N, dx, U, R, z_out, zb, km_soft, km_hard, sc_soft, sc_hard)
    # 경계를 실제로 가로지르는지 확인합니다.
    assert ref.min() < zb < ref.max()
    # pipeline.md 7.1: 손으로 푼 조각별 해와 1e-3 m 안
    assert np.max(np.abs(res.z - ref)) < 1e-3
    # 지표 암석의 S_crit·k_s 는 칸 고도가 속한 층(rock_at 약속) 값입니다.
    upper = res.z > zb
    assert np.all(res.s_crit_surface[upper] == sc_soft)
    assert np.all(res.s_crit_surface[~upper] == sc_hard)


# ---------------------------------------------------------------- (5) G-법칙 충적 경사
def test_g_law_alluvial_slope_downstream_of_uplift():
    N, dx = 160, 500.0
    g, is_outlet = strip(N, dx)
    front = N // 2
    U = np.where(np.arange(N) > front, 2e-3, 0.0)  # 상류 절반만 솟음, 저지대 U = 0
    R = np.full(N, 0.4)
    res = solve_steady_state(g, is_outlet, 0.0, U, R, None, CFG)
    assert res.converged
    low = np.arange(1, front + 1)  # 저지대 칸 (출구 제외), 번호가 작을수록 하류
    S = res.slope[low]
    s_min = CFG.landscape.s_min
    assert np.all(S > 10 * s_min)
    # 하류(출구 쪽)로 갈수록 경사가 줄어듭니다.
    # Qs 는 그대로인데 Q 가 늘어 E = G·R_ref·Qs/Q 가 줄어들기 때문입니다.
    assert np.all(np.diff(S) > 0)
    # 산지보다 훨씬 완만합니다.
    assert S.max() < res.slope[front + 1 :].min()
    # G = 0 이면 저지대 경사는 정확히 s_min 입니다.
    cfg0 = CFG.with_overrides({"landscape.deposition_g": 0.0})
    res0 = solve_steady_state(g, is_outlet, 0.0, U, R, None, cfg0)
    np.testing.assert_allclose(res0.slope[low], s_min, rtol=1e-9)


# ---------------------------------------------------------------- (6) 멈춤 조건
def test_stop_criterion_respected(island_result):
    _, _, _, _, res = island_result
    stop_dz = CFG.landscape.stop_dz_m
    ok = [h["n_changed"] == 0 and h["max_dz"] < stop_dz for h in res.history]
    assert ok[-1] and not any(ok[:-1])


def test_max_iter_caps_iterations():
    g, is_outlet = island(n=64, seed=2)
    U = np.where(is_outlet, 0.0, 1e-3)
    R = np.full(g.n_cells, 1.0)
    res = solve_steady_state(g, is_outlet, 0.0, U, R, None, CFG, max_iter=3)
    assert not res.converged
    assert res.iterations == 3 and len(res.history) == 3
    # 다 수렴하지 않아도 z 는 반환된 수신 셀과 법칙에 맞게 쌓여 있고 웅덩이가 없습니다.
    land = ~is_outlet
    assert np.all(res.z[land] > res.z[res.receiver[land]])


def test_log_callback_and_determinism():
    g, is_outlet = island(n=40, seed=4)
    U = np.where(is_outlet, 0.0, 1e-3)
    R = np.full(g.n_cells, 1.0)
    lines: list[str] = []
    a = solve_steady_state(g, is_outlet, 0.0, U, R, None, CFG, log=lines.append)
    b = solve_steady_state(g, is_outlet, 0.0, U, R, None, CFG)
    assert len(lines) == a.iterations
    np.testing.assert_array_equal(a.z, b.z)
    np.testing.assert_array_equal(a.receiver, b.receiver)


# ---------------------------------------------------------------- 층 기둥과 구면
def test_layered_columns_surface_rock_consistency():
    """솔버의 S_crit 는 그 칸 고도에서 rock_at 이 고르는 암석의 값입니다."""
    g, is_outlet = island(n=64, seed=6)
    n = g.n_cells
    # 칸마다 조금씩 다른 층 경계(100~150 m 간격, 위층부터)와 서로 다른 K·S_crit.
    top = 2000.0 + 50.0 * fbm3(g.pos / 5000.0, 9)
    thick = np.array([150.0, 0.0, 300.0, 200.0, 400.0])
    bottom = top[:, None] - np.cumsum(thick)[None, :]
    k_tab = np.array([2.0, 1.0, 0.5, 3.0, 0.6, 0.3])
    s_tab = np.array([0.45, 0.9, 0.9, 0.4, 1.0, 1.0])
    rock = np.tile(np.arange(6, dtype=np.uint8), (n, 1))
    layers = LayerColumns(
        bottom=bottom, rock=rock, k_mult=np.tile(k_tab, (n, 1)), s_crit=np.tile(s_tab, (n, 1))
    )
    U = np.where(is_outlet, 0.0, 1.5e-3)
    res = solve_steady_state(g, is_outlet, 0.0, U, np.full(n, 0.8), layers, CFG)
    assert res.converged
    rid = layers.rock_at(res.z)
    np.testing.assert_array_equal(res.s_crit_surface, s_tab[rid])
    land = ~is_outlet
    # 여러 층이 지표에 드러납니다.
    assert len(np.unique(rid[land])) >= 3
    # 경사는 어느 칸에서도 그 칸이 지나는 층들의 S_crit 최댓값을 넘지 않습니다.
    assert np.all(res.slope[land] <= s_tab.max() + 1e-12)


@pytest.mark.parametrize("n_exp", [1.0, 2.0])
def test_geology_columns_drive_slopes(n_exp):
    """geology.build_columns 의 실제 템플릿(습곡충상대)으로 풉니다.

    지표 암석이 여러 층에 걸치고, 솔버의 S_crit·k_s 가 rock_at 이 고른 암석의 표 값과 같습니다
    ('경사를 만든 암석 = 보이는 암석'). k_s = k_ref·(E/(U_ref·K 배율))^(1/n) 이 경사 지수 n 에
    맞는지 보려고 n 을 1 과 2 로 고정해 둘 다 풉니다.
    """
    from bpcg.geology import rocks as rk
    from bpcg.geology.model import FOLD_THRUST, build_columns

    cfg = CFG.with_overrides({"landscape.slope_exponent_n": n_exp})
    g, is_outlet = island(n=64, seed=8)
    n = g.n_cells
    template = np.full(n, FOLD_THRUST, dtype=np.uint8)
    exhumation = np.full(n, 1500.0)  # 기둥 꼭대기 1500 m: 셰일 300, 석회암 800, 셰일 400 ...
    sb, sr = build_columns(template, exhumation, None, cfg)
    layers = LayerColumns.from_columns(sb, sr)
    U = np.where(is_outlet, 0.0, 1.5e-3)
    R = np.full(n, 0.8)
    res = solve_steady_state(g, is_outlet, 0.0, U, R, layers, cfg)
    assert res.converged
    land = ~is_outlet
    rid = layers.rock_at(res.z)
    assert set(np.unique(rid[land])) >= {rk.SHALE, rk.LIMESTONE}
    np.testing.assert_array_equal(res.s_crit_surface, rk.S_CRIT[rid])
    ls = cfg.landscape
    E = U + ls.deposition_g * cfg.climate.runoff_ref_m_per_yr * (res.sediment_flux / res.discharge)
    pos = land & (E > 0)
    ks = ls.k_ref * (E[pos] / (ls.u_ref_m_per_yr * rk.K_MULT[rid[pos]])) ** (1.0 / n_exp)
    np.testing.assert_allclose(res.k_s[pos], ks, rtol=1e-12)
    zh = fill_depressions(res.z, g.nbr, is_outlet)
    np.testing.assert_array_equal(zh[land], res.z[land])


def test_sphere_graph_converges():
    R_planet = CFG.planet.radius_m
    g = sphere_graph(16, R_planet, jitter=0.4, seed=0)
    noise = fbm3(g.unit() * 2.0, 3)
    is_outlet = noise < np.quantile(noise, 0.6)
    U = np.where(is_outlet, 0.0, 1e-3)
    R = np.full(g.n_cells, 0.5)
    res = solve_steady_state(g, is_outlet, 0.0, U, R, None, CFG)
    assert res.converged
    zh = fill_depressions(res.z, g.nbr, is_outlet)
    np.testing.assert_array_equal(zh, res.z)
    outlets = res.receiver == np.arange(g.n_cells)
    total = np.sum(U * g.area)
    assert abs(res.sediment_flux[outlets].sum() - total) / total < 1e-6


def test_initial_surface_points_to_outlets():
    g, is_outlet = island(n=48)
    z0 = initial_surface(g, is_outlet, 0.0, CFG)
    assert np.all(z0[is_outlet] == 0.0)
    # 출구에서 멀수록 높아지는 경향(거리 × 1e-3 + 10 m 노이즈)
    far = z0[~is_outlet]
    assert far.max() > 5.0 and np.isfinite(z0).all()
    np.testing.assert_array_equal(z0, initial_surface(g, is_outlet, 0.0, CFG))


# ---------------------------------------------------------------- 입력 검사
def test_invalid_inputs_raise():
    g, is_outlet = island(n=16)
    n = g.n_cells
    U = np.full(n, 1e-3)
    R = np.full(n, 1.0)
    with pytest.raises(ValueError):
        solve_steady_state(g, np.zeros(n, dtype=bool), 0.0, U, R, None, CFG)
    with pytest.raises(ValueError):
        solve_steady_state(g, is_outlet, 0.0, U[:-1], R, None, CFG)
    with pytest.raises(ValueError):
        solve_steady_state(g, is_outlet, 0.0, U, np.zeros(n), None, CFG)
    with pytest.raises(ValueError):
        solve_steady_state(g, is_outlet, 0.0, np.full(n, np.nan), R, None, CFG)
    with pytest.raises(ValueError):
        solve_steady_state(g, is_outlet, 0.0, U, R, None, CFG, extra_inflow=-np.ones(n))
    with pytest.raises(ValueError):
        solve_steady_state(g, is_outlet, 0.0, U, R, None, CFG, max_iter=0)
    with pytest.raises(ValueError):
        solve_steady_state(g, is_outlet.astype(int), 0.0, U, R, None, CFG)
    bad = LayerColumns(
        bottom=np.zeros((n - 1, 1)),
        rock=np.zeros((n - 1, 2), dtype=np.uint8),
        k_mult=np.ones((n - 1, 2)),
        s_crit=np.ones((n - 1, 2)),
    )
    with pytest.raises(ValueError):
        solve_steady_state(g, is_outlet, 0.0, U, R, bad, CFG)


@pytest.mark.slow
def test_sphere_l0_laptop_converges_and_timing():
    """L0 노트북 프로필 크기(면당 512, 157만 칸)에서 max_flow_iterations 안에 수렴합니다."""
    import time

    cfg = load_config("earth", "laptop")
    n_face = cfg.profile.grid.l0_n_per_face
    g = sphere_graph(n_face, cfg.planet.radius_m, jitter=cfg.landscape.jitter, seed=0)
    u = g.unit()
    noise = fbm3(u * 1.5, 3)
    is_outlet = noise < np.quantile(noise, 0.65)
    U = np.where(is_outlet, 0.0, np.maximum(1e-4, 2e-3 * (fbm3(u * 3.0, 4) + 0.2)))
    R = np.full(g.n_cells, 0.3)
    t0 = time.perf_counter()
    res = solve_steady_state(g, is_outlet, 0.0, U, R, None, cfg)
    seconds = time.perf_counter() - t0
    print(f"L0 {g.n_cells} 칸: {res.iterations}회, {seconds:.1f} s, 고정 칸 {res.n_frozen}")
    assert res.converged
    assert res.n_frozen < 0.01 * (~is_outlet).sum()
    zh = fill_depressions(res.z, g.nbr, is_outlet)
    np.testing.assert_array_equal(zh, res.z)
