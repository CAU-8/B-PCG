"""선상지 후처리와 물길 다시 계산 (docs/pipeline.md 7.2, 설계도 5장 5번).

D8 정상상태 지형에는 부채꼴 선상지가 생기지 않으므로, 산에서 나온 강의 경사가 갑자기 줄어드는 곳
(산지 앞)에 꼭짓점을 두고 원뿔을 얹습니다. 얹은 칸은 정상상태가 아니므로 `not_steady` 를 달고,
그 뒤 물길(채움 → D8 → Q)만 다시 계산합니다. 원뿔 때문에 생긴 호수는 그대로 둡니다.

거리는 평면이면 유클리드, 구면이면 대원 거리입니다(현 길이에서 바꿈).
반경 안의 칸은 scipy cKDTree 로 찾습니다.
"""

import numpy as np
from scipy.spatial import cKDTree

from bpcg.core.graph import CellGraph
from bpcg.hydro.accumulate import accumulate
from bpcg.hydro.depressions import fill_epsilon
from bpcg.hydro.routing import d8_receivers, topo_order
from bpcg.landscape.solver import SolverResult


def _arc_to_chord(graph: CellGraph, s: float) -> float:
    """거리 s [m] 에 맞는 3D 직선(현) 거리. 평면은 그대로입니다."""
    if graph.kind == "sphere" and np.isfinite(s):
        R = float(graph.R)
        return 2.0 * R * np.sin(min(0.5 * s / R, 0.5 * np.pi))
    return s


def _chord_to_arc(graph: CellGraph, chord: np.ndarray) -> np.ndarray:
    """3D 직선 거리 → 거리 [m] (구면은 대원 거리)."""
    if graph.kind == "sphere":
        R = float(graph.R)
        return 2.0 * R * np.arcsin(np.clip(chord / (2.0 * R), 0.0, 1.0))
    return chord


def _receiver_distance(graph: CellGraph, rcv: np.ndarray) -> np.ndarray:
    """칸마다 수신 셀까지 거리 [m] (N,). 출구(자기 자신)는 inf."""
    hit = graph.nbr == rcv[:, None]
    d = np.where(hit, graph.dist, np.inf).min(axis=1)
    return d


def find_fan_apexes(graph: CellGraph, result: SolverResult, cfg) -> np.ndarray:
    """선상지 꼭짓점 칸 번호 (M,) int64, Q 가 큰 것부터 (pipeline.md 7.2 '꼭짓점').

    후보: 출구가 아니고 수신 셀도 출구가 아닌 칸 가운데 S_c / S_r(c) ≥ fans.slope_drop_ratio 이고
    Q 가 [fans.min_discharge, fans.max_discharge] [m³/yr] 안인 칸. 이 Q 범위가 선상지를 만드는
    '강 칸'의 정의입니다. 수신 셀이 radius 밖이면(칸이 radius 보다 큰 L0) 원뿔이 덮을 칸이
    없으므로 뺍니다. 서로 2·radius 안의 후보는 Q 가 큰 것 하나만 남깁니다
    (같은 Q 면 셀 번호가 작은 것).
    """
    fc = cfg.fans
    rcv = result.receiver
    n = rcv.shape[0]
    ids = np.arange(n)
    is_out = rcv == ids
    S = result.slope
    Q = result.discharge
    Sr = S[rcv]
    cand = (
        ~is_out
        & ~is_out[rcv]
        & (Sr > 0.0)
        & (S >= float(fc.slope_drop_ratio) * Sr)
        & (Q >= float(fc.min_discharge_m3_per_yr))
        & (Q <= float(fc.max_discharge_m3_per_yr))
    )
    cells = np.flatnonzero(cand)
    if cells.size:
        hit = graph.nbr[cells] == rcv[cells, None]
        d_r = np.where(hit, graph.dist[cells], np.inf).min(axis=1)
        cells = cells[d_r < float(fc.radius_m)]
    if cells.size == 0:
        return cells.astype(np.int64)
    cells = cells[np.lexsort((cells, -Q[cells]))]  # Q 내림차순, 같으면 번호 오름차순
    tree = cKDTree(graph.pos[cells])
    r2 = _arc_to_chord(graph, 2.0 * float(fc.radius_m))
    suppressed = np.zeros(cells.size, dtype=bool)
    keep = []
    for k in range(cells.size):
        if suppressed[k]:
            continue
        keep.append(cells[k])
        near = tree.query_ball_point(graph.pos[cells[k]], r2)
        suppressed[near] = True
    return np.asarray(keep, dtype=np.int64)


def add_fans(
    graph: CellGraph, z: np.ndarray, result: SolverResult, cfg
) -> tuple[np.ndarray, np.ndarray, np.ndarray, list[dict]]:
    """산지 앞 꼭짓점에 원뿔 선상지를 얹습니다 (pipeline.md 7.2).

    graph: CellGraph. z: (N,) 지형 고도 [m] (보통 result.z). result: 솔버 결과(수신 셀·경사·Q).
    cfg: fans 절 (enabled, slope_drop_ratio, min/max_discharge_m3_per_yr, radius_m, slope).

    꼭짓점 a 에서 거리 ℓ < radius 이고 하류 쪽 반평면((p − p_a)·(p_r(a) − p_a) > 0)에 있는
    출구가 아닌 칸에 z_cone = z_a − slope·ℓ 을 만들고 z = max(z, z_cone) 로 올립니다.

    반환: (z_new (N,) float64 [m], fan_mask (N,) bool 올린 칸, not_steady_mask (N,) bool
    올린 칸(정상상태 아님), apexes list[dict]). apex dict 는 cell, receiver, discharge_m3_per_yr,
    z_m, slope_ratio, n_raised (≥ 1), pos (3,) [m] 입니다. 한 칸도 올리지 못한 꼭짓점(이미 원뿔보다
    높은 지형)은 선상지가 아니므로 목록에서 뺍니다.
    그 뒤 호출하는 쪽이 reroute 로 물길만 다시 계산합니다.
    """
    if not isinstance(graph, CellGraph):
        raise ValueError(f"graph 는 CellGraph 여야 합니다: {type(graph).__name__}")
    if not isinstance(result, SolverResult):
        raise ValueError("result 는 SolverResult 여야 합니다")
    n = graph.n_cells
    z = np.asarray(z, dtype=np.float64)
    if z.shape != (n,) or not np.isfinite(z).all():
        raise ValueError(f"z 는 유한한 ({n},) 배열이어야 합니다: {z.shape}")
    if result.receiver.shape != (n,):
        raise ValueError("result 의 칸 수가 그래프와 다릅니다")
    z_new = z.copy()
    fan = np.zeros(n, dtype=bool)
    if not bool(cfg.fans.enabled):
        return z_new, fan, fan.copy(), []
    radius = float(cfg.fans.radius_m)
    cone_slope = float(cfg.fans.slope)
    if not (radius > 0.0 and cone_slope >= 0.0):
        raise ValueError(f"fans.radius_m > 0, fans.slope ≥ 0 이어야 합니다: {radius}, {cone_slope}")

    apex_cells = find_fan_apexes(graph, result, cfg)
    rcv = result.receiver
    is_out = rcv == np.arange(n)
    apexes: list[dict] = []
    if apex_cells.size == 0:
        return z_new, fan, fan.copy(), apexes
    tree = cKDTree(graph.pos)
    r_chord = _arc_to_chord(graph, radius)
    for a in apex_cells:
        pa = graph.pos[a]
        idx = np.asarray(tree.query_ball_point(pa, r_chord), dtype=np.int64)
        rel = graph.pos[idx] - pa
        ell = _chord_to_arc(graph, np.linalg.norm(rel, axis=1))
        down = rel @ (graph.pos[rcv[a]] - pa) > 0.0
        ok = down & (ell < radius) & ~is_out[idx]
        idx = idx[ok]
        z_cone = z[a] - cone_slope * ell[ok]
        raise_ = z_cone > z_new[idx]
        if not raise_.any():
            continue
        z_new[idx[raise_]] = z_cone[raise_]
        fan[idx[raise_]] = True
        apexes.append(
            {
                "cell": int(a),
                "receiver": int(rcv[a]),
                "discharge_m3_per_yr": float(result.discharge[a]),
                "z_m": float(z[a]),
                "slope_ratio": float(result.slope[a] / result.slope[rcv[a]]),
                "n_raised": int(raise_.sum()),
                "pos": tuple(float(v) for v in pa),
            }
        )
    return z_new, fan, fan.copy(), apexes


def reroute(
    graph: CellGraph,
    z: np.ndarray,
    is_outlet: np.ndarray,
    runoff_eff: np.ndarray,
    cfg,
    extra_inflow: np.ndarray | None = None,
) -> dict[str, np.ndarray]:
    """후처리 뒤 물길만 다시 계산합니다: ε 채움 → D8 → Q (pipeline.md 7.2 '표시').

    graph: CellGraph. z: (N,) 고도 [m]. is_outlet: (N,) bool. runoff_eff: (N,) R_eff [m/yr].
    cfg: landscape.fill_epsilon_m. extra_inflow: (N,) 밖에서 들어오는 물 [m³/yr] 또는 None.
    지형은 고치지 않으므로 원뿔 뒤에 생긴 호수는 그대로 남습니다(호수 판정은 subsurface/water).
    반환: FIELDS 이름 dict {receiver (N,) int64, discharge_m3_per_yr, drainage_area_m2,
    slope (N,) float64}. slope 는 z 에서 잰 (z_c − z_r)/d 를 0 에서 자른 값입니다(호수 안은 0).
    하류부터 순서가 필요하면 hydro.routing.topo_order(receiver) 로 다시 구합니다.
    """
    if not isinstance(graph, CellGraph):
        raise ValueError(f"graph 는 CellGraph 여야 합니다: {type(graph).__name__}")
    n = graph.n_cells
    z = np.asarray(z, dtype=np.float64)
    R = np.asarray(runoff_eff, dtype=np.float64)
    if z.shape != (n,) or R.shape != (n,):
        raise ValueError(f"z, runoff_eff 는 ({n},) 이어야 합니다: {z.shape}, {R.shape}")
    if not np.isfinite(R).all() or (R < 0.0).any():
        raise ValueError("runoff_eff 는 0 이상의 유한한 값이어야 합니다")
    weight = graph.area * R
    if extra_inflow is not None:
        inflow = np.asarray(extra_inflow, dtype=np.float64)
        if inflow.shape != (n,) or not np.isfinite(inflow).all() or (inflow < 0.0).any():
            raise ValueError(f"extra_inflow 는 0 이상의 유한한 ({n},) 배열이어야 합니다")
        weight = weight + inflow
    zt = fill_epsilon(z, graph.nbr, is_outlet, float(cfg.landscape.fill_epsilon_m))
    rcv, _, _ = d8_receivers(zt, graph.nbr, graph.dist, is_outlet)
    order = topo_order(rcv)
    Q = accumulate(rcv, order, weight)
    A_up = accumulate(rcv, order, graph.area)
    d = _receiver_distance(graph, rcv)
    slope = np.where(np.isfinite(d), np.maximum(z - z[rcv], 0.0) / d, 0.0)
    return {
        "receiver": rcv,
        "discharge_m3_per_yr": Q,
        "drainage_area_m2": A_up,
        "slope": slope,
    }
