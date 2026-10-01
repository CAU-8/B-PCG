"""물: 호수·강·수면과 골짜기 깊이 (docs/pipeline.md 8.1, 가이드 6장, 설계도 4장 사슬 3).

4단계(3D 값)의 첫 계산입니다. 솔버(또는 선상지 뒤 reroute)의 물길을 읽어 물이 있는 칸과 그
수면 높이 h_w 를 정하고, 동굴 층 높이에 쓸 골짜기 깊이를 구합니다. 지형은 바꾸지 않습니다.

수면 규칙(가이드 6장 + 이 모듈의 보충)
- 바다는 0, 호수는 채운 높이 ẑ, 강은 z − 0.2·D 에서 시작합니다.
- 1차(상류부터): 강·호수 칸 c 의 수신 셀 r 이 강이면 h_w(r) ← min(h_w(r), h_w(c)) 입니다
  (명세 그대로, 역류 막기). 호수와 바다의 수면은 넘침 높이·해수면으로 정해진 값이라 낮추지
  않습니다. 낮추면 호수 한 칸만 내려가 '호수가 평평하다'가 깨집니다.
- 2차(하류부터): 강 칸 c 의 수신 셀 r 이 물이면 h_w(c) ← max(h_w(c), h_w(r)) 입니다.
  강어귀가 호수면·해수면보다 낮게 계산된 곳(깊은 강이 바다에 닿는 칸 등)을 받는 쪽 수면까지
  올립니다(배수 효과). 1차 뒤라 강-강 사이에서는 바뀌지 않고, 호수·바다 어귀에서만 상류로
  번집니다. 그래서 모든 강 칸에서 h_w(c) ≥ h_w(r(c)) 가 정확히 성립합니다.
"""

import math
from collections.abc import Mapping

import numba
import numpy as np
from numba import njit, prange

from bpcg.core.constants import SECONDS_PER_YEAR
from bpcg.core.distance import nearest_source_values
from bpcg.core.graph import CellGraph
from bpcg.hydro.depressions import fill_depressions
from bpcg.hydro.routing import topo_order

# ---------------------------------------------------------------- 명세 상수 (설정에 없는 값)
LAKE_MIN_DEPTH_M = 1e-3  # pipeline.md 8.1: ẑ − z > 1e-3 m 인 육지 칸이 호수
RIVER_SURFACE_DEPTH_FRACTION = 0.2  # 강 수면 = z − 0.2·D (가이드 6장)
WIDTH_EXPONENT = 0.5  # W = k_W·Q^0.5 (Leopold–Maddock 하류 수리 기하)
DEPTH_EXPONENT = 0.4  # D = k_D·Q^0.4
SEA_LEVEL_M = 0.0  # 모든 고도는 해수면 0 기준 (pipeline.md 4.3)

# 물 종류 번호 (커널 안에서만 씀)
KIND_NONE = 0
KIND_OCEAN = 1
KIND_LAKE = 2
KIND_RIVER = 3


# ---------------------------------------------------------------- 입력 확인 (subsurface 공용)
def _check_graph(graph: CellGraph) -> int:
    if not isinstance(graph, CellGraph):
        raise ValueError(f"graph 는 CellGraph 여야 합니다: {type(graph).__name__}")
    return graph.n_cells


def _as_vector(x, n: int, name: str, allow_scalar: bool = False) -> np.ndarray:
    """(n,) float64 배열로 바꿉니다. allow_scalar 이면 스칼라를 n 개로 늘립니다. NaN·inf 는 거절."""
    a = np.asarray(x, dtype=np.float64)
    if a.ndim == 0 and allow_scalar:
        a = np.full(n, float(a))
    if a.shape != (n,):
        raise ValueError(f"{name} 는 ({n},) 배열이어야 합니다 (받은 모양: {a.shape})")
    if not np.isfinite(a).all():
        raise ValueError(f"{name} 에 NaN 이나 inf 가 있습니다")
    return np.ascontiguousarray(a)


def _as_mask(x, n: int, name: str) -> np.ndarray:
    m = np.asarray(x)
    if m.dtype != np.bool_ or m.shape != (n,):
        raise ValueError(f"{name} 는 ({n},) bool 배열이어야 합니다 (받은 것: {m.shape} {m.dtype})")
    return np.ascontiguousarray(m)


def _result_array(result, attr: str, key: str, n: int, required: bool = True):
    """솔버 결과(SolverResult 같은 속성 묶음) 또는 FIELDS 이름 dict 에서 배열 하나를 꺼냅니다."""
    if isinstance(result, Mapping):
        a = result.get(key, result.get(attr))
    else:
        a = getattr(result, attr, None)
    if a is None:
        if required:
            raise ValueError(f"result 에 '{attr}' (필드 '{key}') 가 없습니다")
        return None
    a = np.asarray(a)
    if a.shape != (n,):
        raise ValueError(f"result 의 '{attr}' 모양 {a.shape} 이 칸 수 ({n},) 와 다릅니다")
    return a


def routing_from_result(result, n: int) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """물길 결과에서 (receiver, order, discharge) 를 꺼냅니다.

    result: landscape.solver.SolverResult(receiver, order, discharge 속성) 또는 FIELDS 이름 dict
    (receiver, discharge_m3_per_yr, 있으면 order). 선상지 뒤 landscape.fans.reroute 결과 dict 를
    그대로 줄 수 있습니다. order 가 없으면 hydro.routing.topo_order 로 다시 구합니다.
    반환: receiver (n,) int64, order (n,) int64 하류부터, discharge Q (n,) float64 [m³/yr].
    """
    rcv = _result_array(result, "receiver", "receiver", n)
    if rcv.dtype.kind not in "iu":
        raise ValueError(f"result 의 receiver 는 정수 배열이어야 합니다: {rcv.dtype}")
    rcv = np.ascontiguousarray(rcv, dtype=np.int64)
    if n and (rcv.min() < 0 or rcv.max() >= n):
        raise ValueError("result 의 receiver 에 범위를 벗어난 셀 번호가 있습니다")
    order = _result_array(result, "order", "order", n, required=False)
    order = topo_order(rcv) if order is None else np.ascontiguousarray(order, dtype=np.int64)
    Q = _result_array(result, "discharge", "discharge_m3_per_yr", n)
    Q = np.ascontiguousarray(Q, dtype=np.float64)
    if not np.isfinite(Q).all() or (Q < 0.0).any():
        raise ValueError("result 의 discharge 는 0 이상의 유한한 값이어야 합니다")
    return rcv, order, Q


# ---------------------------------------------------------------- 커널
@njit(cache=True)
def _water_level_kernel(order: np.ndarray, rcv: np.ndarray, kind: np.ndarray, h: np.ndarray):
    """수면 h (N,) [m] 를 그 자리에서 고칩니다. kind: (N,) uint8 물 종류. order: 하류부터 순서.

    1차(상류부터): 강·호수 → 강이면 h(r) = min(h(r), h(c)).
    2차(하류부터): 강 → 물이면 h(c) = max(h(c), h(r)) (호수·바다 어귀의 배수 효과).
    """
    n = order.shape[0]
    for k in range(n - 1, -1, -1):
        c = order[k]
        r = rcv[c]
        if r == c:
            continue
        if (kind[c] == KIND_RIVER or kind[c] == KIND_LAKE) and kind[r] == KIND_RIVER:
            if h[c] < h[r]:
                h[r] = h[c]
    for k in range(n):
        c = order[k]
        r = rcv[c]
        if r == c or kind[c] != KIND_RIVER or kind[r] == KIND_NONE:
            continue
        if h[r] > h[c]:
            h[c] = h[r]


@njit(cache=True)
def _window_max_chunk(
    pos: np.ndarray,
    nbr: np.ndarray,
    z: np.ndarray,
    sources: np.ndarray,
    r2: float,
    lo: int,
    hi: int,
    out: np.ndarray,
) -> None:
    """출발 칸 sources[lo:hi] 마다 반경 안 칸들의 최고 z 를 out 에 씁니다 (한 스레드 몫).

    그래프 이웃을 따라 너비 우선으로 퍼뜨리되, 출발 칸까지 직선(현) 거리가 반경 안인 칸만
    넘어갑니다. 그래서 퍼지는 모양이 팔각형이 아니라 원입니다(설계도 5장 거리 규칙).
    방문 표시는 출발 칸 번호 k 로 하므로 출발 칸마다 지우지 않아도 됩니다.
    """
    n = pos.shape[0]
    n_slots = nbr.shape[1]
    stamp = np.full(n, -1, dtype=np.int64)
    cap = 1024
    queue = np.empty(cap, dtype=np.int64)
    for k in range(lo, hi):
        c0 = sources[k]
        x0 = pos[c0, 0]
        y0 = pos[c0, 1]
        w0 = pos[c0, 2]
        stamp[c0] = k
        queue[0] = c0
        head = 0
        tail = 1
        best = z[c0]
        while head < tail:
            u = queue[head]
            head += 1
            for s in range(n_slots):
                v = nbr[u, s]
                if v < 0 or stamp[v] == k:
                    continue
                stamp[v] = k  # 반경 밖이어도 표시합니다(같은 출발 칸에서는 결과가 같음).
                dx = pos[v, 0] - x0
                dy = pos[v, 1] - y0
                dw = pos[v, 2] - w0
                if dx * dx + dy * dy + dw * dw > r2:
                    continue
                if z[v] > best:
                    best = z[v]
                if tail == cap:
                    cap *= 2
                    grown = np.empty(cap, dtype=np.int64)
                    grown[:tail] = queue[:tail]
                    queue = grown
                queue[tail] = v
                tail += 1
        out[k] = best


@njit(cache=True, parallel=True)
def _window_max_kernel(
    pos: np.ndarray,
    nbr: np.ndarray,
    z: np.ndarray,
    sources: np.ndarray,
    r2: float,
    n_chunks: int,
    out: np.ndarray,
) -> None:
    """출발 칸마다 반경 안 칸들의 최고 z (M,) [m] 를 out 에 씁니다.

    pos: (N, 3) [m]. nbr: (N, K). z: (N,) [m]. sources: (M,) 출발 칸. r2: 반경의 현 길이 제곱 [m²].
    출발 칸을 n_chunks 묶음으로 나눠 병렬로 돌고, 묶음마다 방문 표시 배열을 따로 둡니다.
    최댓값이라 처리 순서와 상관없이 결과가 같습니다.
    """
    m = sources.shape[0]
    for ch in prange(n_chunks):
        _window_max_chunk(
            pos, nbr, z, sources, r2, ch * m // n_chunks, (ch + 1) * m // n_chunks, out
        )


def _window_chord_sq(graph: CellGraph, window_m: float) -> float:
    """반경 [m] (구면은 대원 거리) 을 대표점 사이 현 길이 제곱 [m²] 으로 바꿉니다."""
    if graph.kind == "sphere":
        R = float(graph.R)
        if window_m >= math.pi * R:
            return math.inf
        chord = 2.0 * R * math.sin(0.5 * window_m / R)
        return chord * chord
    return window_m * window_m


def window_max(graph: CellGraph, z: np.ndarray, cells: np.ndarray, window_m: float) -> np.ndarray:
    """칸 cells 마다 반경 window_m 안 칸들의 최고 고도 (그래프 이웃을 따라 퍼뜨린 최댓값).

    graph: CellGraph. z: (N,) 고도 [m]. cells: (M,) 출발 칸 번호. window_m: 반경 [m]
    (구면 대원 거리, 평면 유클리드 거리). 출발 칸 자신은 늘 포함합니다.
    반환: (M,) float64 [m].
    """
    n = _check_graph(graph)
    zz = _as_vector(z, n, "z")
    window_m = float(window_m)
    if not (math.isfinite(window_m) and window_m >= 0.0):
        raise ValueError(f"window_m 은 0 이상의 유한한 값이어야 합니다: {window_m}")
    cells = np.ascontiguousarray(cells, dtype=np.int64)
    if cells.ndim != 1:
        raise ValueError(f"cells 는 (M,) 1차원이어야 합니다: {cells.shape}")
    if cells.size and (cells.min() < 0 or cells.max() >= n):
        raise ValueError(f"cells 는 0..{n - 1} 이어야 합니다")
    out = np.empty(cells.shape[0], dtype=np.float64)
    if cells.size == 0:
        return out
    n_chunks = max(1, min(int(numba.get_num_threads()), cells.shape[0]))
    _window_max_kernel(
        np.ascontiguousarray(graph.pos, dtype=np.float64),
        np.ascontiguousarray(graph.nbr),
        zz,
        cells,
        _window_chord_sq(graph, window_m),
        n_chunks,
        out,
    )
    return out


def valley_depth(
    graph: CellGraph,
    z: np.ndarray,
    water_level: np.ndarray,
    is_river: np.ndarray,
    window_m: float,
) -> np.ndarray:
    """골짜기 깊이 = (강 칸 반경 window_m 안 최고 z) − h_w, 다른 칸은 가장 가까운 강의 값.

    graph: CellGraph. z: (N,) 고도 [m]. water_level: (N,) 수면 h_w [m] (강 칸 값만 읽음).
    is_river: (N,) bool. window_m: 골짜기 가장자리를 찾는 반경 [m] (caves.valley_window_m).
    반환: (N,) float64 [m], 0 이상. 강이 하나도 없으면 모두 NaN 입니다.
    가장 가까운 강은 core.distance.nearest_source(직선·대원 거리)로 찾습니다.
    """
    n = _check_graph(graph)
    zz = _as_vector(z, n, "z")
    river = _as_mask(is_river, n, "is_river")
    h = np.asarray(water_level, dtype=np.float64)
    if h.shape != (n,):
        raise ValueError(f"water_level 은 ({n},) 배열이어야 합니다 (받은 모양: {h.shape})")
    cells = np.flatnonzero(river)
    if cells.size and not np.isfinite(h[cells]).all():
        raise ValueError("강 칸의 water_level 에 NaN 이나 inf 가 있습니다")
    vd = np.full(n, np.nan)
    if cells.size == 0:
        return vd
    rim = window_max(graph, zz, cells, window_m)
    vd[cells] = np.maximum(rim - h[cells], 0.0)
    _, _, out = nearest_source_values(graph, river, vd)
    return out


# ---------------------------------------------------------------- 단계 함수
def water_bodies(
    graph: CellGraph, z: np.ndarray, result, is_ocean: np.ndarray, cfg
) -> dict[str, np.ndarray]:
    """호수·강·수면·골짜기 깊이를 정합니다 (pipeline.md 8.1).

    graph: CellGraph. z: (N,) 최종 지형 고도 [m] (선상지를 얹었으면 그 고도).
    result: 물길 결과. landscape.solver.SolverResult 또는 FIELDS 이름 dict(receiver,
    discharge_m3_per_yr, 있으면 order; landscape.fans.reroute 의 결과). is_ocean: (N,) bool 바다.
    cfg: rivers 절(min_discharge_m3_per_s, width_coefficient, depth_coefficient),
    caves.valley_window_m.

    - 호수: 바다·출구(수신 셀이 자기 자신)에서 채운 ẑ = fill_depressions(z) 가 z 보다 1e-3 m 넘게
      높은 육지 칸. 수면은 ẑ 라서 연결된 호수마다 같습니다.
    - 강: Q/SECONDS_PER_YEAR ≥ min_discharge_m3_per_s 인 육지 칸(호수 칸은 뺌).
      폭 W = k_W·Q_s^0.5, 깊이 D = k_D·Q_s^0.4 [m] (Q_s 는 m³/s).
    - 수면 h_w: 바다 0, 호수 ẑ, 강 z − 0.2·D 에서 시작해 모듈 설명의 두 번 훑기로 역류를 없앱니다.
    - 골짜기 깊이: valley_depth 참고.

    반환: FIELDS 이름 dict. water_level_m (N,) float64 (물 없으면 NaN), is_lake, is_river (N,) bool,
    river_width_m, river_depth_m (N,) float64 (강 아니면 0), valley_depth_m (N,) float64
    (강이 없으면 NaN).
    """
    n = _check_graph(graph)
    zz = _as_vector(z, n, "z")
    ocean = _as_mask(is_ocean, n, "is_ocean")
    rcv, order, Q = routing_from_result(result, n)
    rv = cfg.rivers
    q_min = float(rv.min_discharge_m3_per_s)
    k_w = float(rv.width_coefficient)
    k_d = float(rv.depth_coefficient)
    if not (q_min >= 0.0 and k_w > 0.0 and k_d > 0.0):
        raise ValueError(
            "rivers.min_discharge_m3_per_s ≥ 0, width_coefficient > 0, depth_coefficient > 0 "
            f"이어야 합니다: {q_min}, {k_w}, {k_d}"
        )
    window = float(cfg.caves.valley_window_m)

    is_outlet = ocean | (rcv == np.arange(n))
    z_hat = fill_depressions(zz, graph.nbr, is_outlet)
    land = ~ocean
    is_lake = land & (z_hat - zz > LAKE_MIN_DEPTH_M)
    q_s = Q / SECONDS_PER_YEAR
    is_river = land & ~is_lake & (q_s >= q_min)
    width = np.where(is_river, k_w * q_s**WIDTH_EXPONENT, 0.0)
    depth = np.where(is_river, k_d * q_s**DEPTH_EXPONENT, 0.0)

    kind = np.full(n, KIND_NONE, dtype=np.uint8)
    kind[ocean] = KIND_OCEAN
    kind[is_lake] = KIND_LAKE
    kind[is_river] = KIND_RIVER
    h = np.full(n, np.nan)
    h[ocean] = SEA_LEVEL_M
    h[is_lake] = z_hat[is_lake]
    h[is_river] = zz[is_river] - RIVER_SURFACE_DEPTH_FRACTION * depth[is_river]
    _water_level_kernel(order, rcv, kind, h)

    vd = valley_depth(graph, zz, h, is_river, window)
    return {
        "water_level_m": h,
        "is_lake": is_lake,
        "is_river": is_river,
        "river_width_m": width,
        "river_depth_m": depth,
        "valley_depth_m": vd,
    }
