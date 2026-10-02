"""거친 격자 먼저 풀기 (평면 히어로용 시작 지형, pipeline.md 7.1 추가).

25 m 히어로(1280², 160만 칸)를 노이즈 지형에서 바로 풀면 물길망이 자리를 잡기 전에 큰 유역끼리
서로를 빼앗으며 진동합니다(실험: 800회에도 높이 변화 1.3 km, 진동 고정 칸 36%).
그래서 factor 배 거친 격자에서 같은 경계조건으로 먼저 풀고,
그 지형을 쌍선형 보간해 시작 지형으로 씁니다.
거친 격자도 크면 같은 방법을 한 번 더 씁니다(멀티그리드의 위로 올라가는 절반만).
결과(정상상태 지형)는 시작 지형과 상관없이 같은 법칙을 만족하고, 바뀌는 것은 반복 수뿐입니다.
"""

import time

import numpy as np
from scipy.interpolate import RegularGridInterpolator

from bpcg.core.graph import CellGraph, flat_graph
from bpcg.geology.model import LayerColumns
from bpcg.landscape.solver import solve_steady_state

MIN_COARSE_CELLS = 64  # 거친 격자 한 변이 이보다 작아지면 더 거칠게 하지 않습니다
MIN_WARM_START_CELLS = 16  # 거친 격자 한 변이 이보다 작으면 먼저 풀기를 건너뜁니다 (작은 히어로)


def _block_index(graph: CellGraph, factor: int) -> tuple[np.ndarray, int, int]:
    ny, nx = graph.shape
    nyc, nxc = max(ny // factor, 1), max(nx // factor, 1)
    c = np.arange(graph.n_cells)
    j = np.minimum((c // nx) // factor, nyc - 1)
    i = np.minimum((c % nx) // factor, nxc - 1)
    return j * nxc + i, nyc, nxc


def coarse_warm_start(
    graph: CellGraph,
    is_outlet: np.ndarray,
    z_outlet: float | np.ndarray,
    uplift: np.ndarray,
    runoff_eff: np.ndarray,
    columns: LayerColumns | None,
    cfg,
    *,
    extra_inflow: np.ndarray | None = None,
    factor: int = 4,
    max_iter: int | None = None,
    log=None,
) -> tuple[np.ndarray | None, dict]:
    """평면 그래프의 시작 지형 [m] (N,) 을 거친 격자 풀이로 만듭니다.

    graph: 평면 CellGraph. is_outlet: (N,) bool. z_outlet: 스칼라 또는 (N,) [m].
    uplift, runoff_eff: (N,) [m/yr]. columns: 지질 기둥 또는 None.
    extra_inflow: (N,) [m³/yr] 또는 None.
    거친 칸 값: 융기·유출은 칸 평균, 들어오는 물은 합, 출구는 고운 출구가 하나라도 있으면 출구
    (높이는 그 중 최솟값), 지질 기둥은 거친 칸 가운데 고운 칸의 기둥.
    반환: (시작 지형, 진단 dict: 단계별 칸 수·반복 수·수렴·시간). 거친 격자 한 변이
    MIN_WARM_START_CELLS 보다 작으면 (작은 히어로) 먼저 풀지 않고 (None, {"skipped": True, …}).
    """
    if graph.kind != "flat":
        raise ValueError("coarse_warm_start 는 평면 그래프에서만 씁니다")
    if min(graph.shape) // factor < MIN_WARM_START_CELLS:
        return None, {"levels": [], "seconds": 0.0, "skipped": True}
    t0 = time.perf_counter()
    n = graph.n_cells
    outlet = np.asarray(is_outlet, dtype=bool)
    zo = np.broadcast_to(np.asarray(z_outlet, dtype=np.float64), (n,))
    blk, nyc, nxc = _block_index(graph, factor)
    nc = nyc * nxc
    counts = np.bincount(blk, minlength=nc).astype(np.float64)
    U_c = np.bincount(blk, weights=np.asarray(uplift, dtype=np.float64), minlength=nc) / counts
    R_c = np.bincount(blk, weights=np.asarray(runoff_eff, dtype=np.float64), minlength=nc) / counts
    out_c = np.zeros(nc, dtype=bool)
    out_c[blk[outlet]] = True
    zo_c = np.full(nc, np.inf)
    np.minimum.at(zo_c, blk[outlet], zo[outlet])
    zo_c[~out_c] = 0.0
    inflow_c = None
    if extra_inflow is not None:
        inflow_c = np.bincount(
            blk, weights=np.asarray(extra_inflow, dtype=np.float64), minlength=nc
        )
    dxc = graph.spacing * factor
    coarse = flat_graph(
        nyc, nxc, dxc,
        jitter=float(cfg.landscape.jitter), seed=int(cfg.planet.seed) + 7919,
        origin=graph.origin,
    )  # fmt: skip
    cols_c = None
    if columns is not None:
        ny, nx = graph.shape
        jc, ic = np.divmod(np.arange(nc), nxc)
        jf = np.minimum(jc * factor + factor // 2, ny - 1)
        if_ = np.minimum(ic * factor + factor // 2, nx - 1)
        pick = jf * nx + if_
        cols_c = LayerColumns(
            columns.bottom[pick], columns.rock[pick], columns.k_mult[pick], columns.s_crit[pick]
        )
    levels = []
    z_init_c = None
    if min(nyc, nxc) // factor >= MIN_COARSE_CELLS:
        z_init_c, sub = coarse_warm_start(
            coarse, out_c, zo_c, U_c, R_c, cols_c, cfg,
            extra_inflow=inflow_c, factor=factor, max_iter=max_iter,
        )  # fmt: skip
        levels.extend(sub["levels"])
    res = solve_steady_state(
        coarse, out_c, zo_c, U_c, R_c, cols_c, cfg,
        z_init=z_init_c, extra_inflow=inflow_c, max_iter=max_iter,
    )  # fmt: skip
    levels.append(
        {"shape": [nyc, nxc], "spacing_m": dxc, "iterations": res.iterations,
         "converged": res.converged, "n_frozen": res.n_frozen}
    )  # fmt: skip
    # 거친 칸 중심(흔들기 전) 격자에서 쌍선형 보간. 가장자리 반 칸은 선형 외삽합니다.
    ox, oy = graph.origin
    xs = ox + (np.arange(nxc) + 0.5) * dxc
    ys = oy - (np.arange(nyc) + 0.5) * dxc  # 북 → 남 (내림차순)
    img = res.z.reshape(nyc, nxc)[::-1, :]  # 행을 남 → 북 (오름차순)으로
    interp = RegularGridInterpolator((ys[::-1], xs), img, bounds_error=False, fill_value=None)
    z_init = interp(np.stack([graph.pos[:, 1], graph.pos[:, 0]], axis=1))
    z_init[outlet] = zo[outlet]
    diag = {"levels": levels, "seconds": time.perf_counter() - t0}
    if log is not None:
        log(
            "[2단계] 거친 격자 먼저: "
            + ", ".join(f"{lv['shape'][0]}² 반복 {lv['iterations']}" for lv in levels)
            + f", {diag['seconds']:.1f} s"
        )
    return z_init, diag
