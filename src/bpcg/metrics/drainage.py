"""물길 지표: Hack 법칙, 하천 연결·역류, 법칙 자기일관성, 수지, 격자 정렬 지수.

docs/pipeline.md 12장과 가이드 2장 '격자 정렬 지수'를 따릅니다. 모두 수신 셀 배열(트리)과 그래프
배열만 받고, 칸마다 도는 계산은 numba 커널입니다.

- `longest_flow_path`, `main_stem_mask`, `hack_fit`: 본류를 따라 L(가장 긴 흐름 경로) 대
  A(상류 면적)를 log-log 로 맞춘 Hack 법칙 L = c·A^h (km, km²). 지구 h 약 0.49~0.6.
- `river_reach_fraction`, `river_backflow_count`: 하천이 바다·호수·출구에 닿는 비율(100%)과
  수면이 하류로 올라가는 칸 수(0).
- `law_slope_from_fields`, `law_consistency`: 솔버 결과 필드에서 경사 법칙을 다시 계산해 실제 경사와
  비교합니다(중앙값 < 1e-3).
- `budget_error`: 물·퇴적물 수지 상대 오차(1e-6).
- `grid_alignment`, `alignment_counts`: 하천이 격자 8방향과 나란한 정도(1 이면 결 없음, 목표 < 1.3).
"""

import math

import numpy as np
from numba import njit, prange

from bpcg.core.graph import CellGraph
from bpcg.geology.model import layer_index_at
from bpcg.hydro.accumulate import accumulate
from bpcg.hydro.routing import _check_rcv, topo_order

# 가이드 2장 격자 정렬 지수의 정의값 (설정에 없음)
ALIGNMENT_STEPS = 6  # 하류로 따라가는 칸 수
ALIGNMENT_TOL_DEG = 5.0  # 격자 방향과의 허용 각도 [도]
EDGE_BAND = 0.9  # 가이드 1장 회전 테스트: 면 경계 띠 |a| > 0.9 또는 |b| > 0.9

_BANDS = {"all": 0, "edge": 1, "center": 2}


# ---------------------------------------------------------------- 커널
@njit(cache=True, parallel=True)
def _receiver_distance_kernel(
    rcv: np.ndarray, nbr: np.ndarray, dist: np.ndarray, pos: np.ndarray, out: np.ndarray
) -> None:
    """칸 c 에서 수신 셀까지 거리 [m]. 이웃 슬롯에서 찾고, 이웃이 아니면 대표점 직선 거리."""
    for c in prange(rcv.shape[0]):
        r = rcv[c]
        if r == c:
            out[c] = 0.0
            continue
        d = -1.0
        for s in range(nbr.shape[1]):
            if nbr[c, s] == r:
                d = dist[c, s]
                break
        if d < 0.0:
            dx = pos[c, 0] - pos[r, 0]
            dy = pos[c, 1] - pos[r, 1]
            dz = pos[c, 2] - pos[r, 2]
            d = math.sqrt(dx * dx + dy * dy + dz * dz)
        out[c] = d


@njit(cache=True)
def _longest_path_kernel(
    order: np.ndarray,
    rcv: np.ndarray,
    rdist: np.ndarray,
    area_up: np.ndarray,
    length: np.ndarray,
    main: np.ndarray,
) -> None:
    """상류부터 가장 긴 흐름 경로 길이와 그 경로의 기여 셀(main)을 채웁니다.

    같은 길이면 상류 면적이 큰 기여 셀, 그래도 같으면 번호가 작은 셀을 고릅니다.
    """
    for q in range(order.shape[0] - 1, -1, -1):
        c = order[q]
        r = rcv[c]
        if r == c:
            continue
        cand = length[c] + rdist[c]
        m = main[r]
        if (
            m < 0
            or cand > length[r]
            or (
                cand == length[r]
                and (area_up[c] > area_up[m] or (area_up[c] == area_up[m] and c < m))
            )
        ):
            length[r] = cand
            main[r] = c


@njit(cache=True)
def _main_stem_kernel(order: np.ndarray, rcv: np.ndarray, main: np.ndarray, out: np.ndarray):
    """하구에서 main 을 따라 거슬러 오른 칸(본류)을 표시합니다 (하류부터 한 번 훑기).

    출구와, 출구로 바로 흘러드는 칸(하구)은 모두 본류의 시작입니다. L0 에서는 바다 칸 하나가
    여러 강을 받으므로, 하구마다 따로 본류를 세워야 유역마다 본류가 하나씩 생깁니다.
    """
    for q in range(order.shape[0]):
        c = order[q]
        r = rcv[c]
        out[c] = (r == c) or (rcv[r] == r) or (out[r] and main[r] == c)


@njit(cache=True)
def _reach_kernel(
    order: np.ndarray, rcv: np.ndarray, is_river: np.ndarray, is_sink: np.ndarray, ok: np.ndarray
) -> None:
    """ok[c]: c 에서 하류로 강 칸만 지나 바다·호수·출구(sink)에 닿으면 True (하류부터)."""
    for q in range(order.shape[0]):
        c = order[q]
        r = rcv[c]
        if is_sink[c]:
            ok[c] = True
        elif r == c or not is_river[c]:
            ok[c] = False
        else:
            ok[c] = ok[r]


@njit(cache=True, parallel=True)
def _layer_cross_kernel(
    rcv: np.ndarray, z: np.ndarray, bottom: np.ndarray, out: np.ndarray
) -> None:
    """칸 c 와 수신 셀 사이 구간이 층 경계를 넘으면 True.

    솔버 적분의 시작 층(z_r 바로 위)과 지표 층(layer_index_at(z_c))이 다르면 넘은 것입니다.
    """
    n_layers = bottom.shape[1]
    for c in prange(rcv.shape[0]):
        r = rcv[c]
        if r == c:
            out[c] = False
            continue
        lo = n_layers
        for i in range(n_layers):
            if bottom[c, i] <= z[r]:
                lo = i
                break
        out[c] = lo != layer_index_at(bottom, c, z[c])


@njit(cache=True, parallel=True)
def _alignment_kernel(
    rcv: np.ndarray,
    is_river: np.ndarray,
    face_cells: int,
    ny: int,
    nx: int,
    band: int,
    edge: float,
    steps: int,
    tol_deg: float,
    out: np.ndarray,
) -> None:
    """칸마다 격자 정렬 판정: 1 정렬, 0 비정렬, −1 안 씀(강 아님·띠 밖·출구·면 넘음).

    셀 번호 c = f·face_cells + j·nx + i 에서 (i, j) 를 읽어, 하류로 steps 칸 간 변위
    (Δi, Δj) 의 방향이 8방향(45° 간격) 중 하나와 tol_deg 안이면 정렬입니다.
    """
    for c0 in prange(rcv.shape[0]):
        out[c0] = -1
        if not is_river[c0]:
            continue
        c = np.int64(c0)
        f = c // face_cells
        rem = c - f * face_cells
        j0 = rem // nx
        i0 = rem - j0 * nx
        if band != 0:
            a = -1.0 + (2.0 * i0 + 1.0) / nx
            b = -1.0 + (2.0 * j0 + 1.0) / ny
            is_edge = abs(a) > edge or abs(b) > edge
            if (band == 1) != is_edge:
                continue
        cur = c
        good = True
        for _ in range(steps):
            r = rcv[cur]
            if r == cur or r // face_cells != f:
                good = False
                break
            cur = r
        if not good:
            continue
        rem = cur - f * face_cells
        j1 = rem // nx
        i1 = rem - j1 * nx
        di = abs(i1 - i0)
        dj = abs(j1 - j0)
        if di == 0 and dj == 0:
            continue
        ang = math.degrees(math.atan2(dj, di))  # 0~90°
        dev = ang % 45.0
        if 45.0 - dev < dev:
            dev = 45.0 - dev
        out[c0] = 1 if dev <= tol_deg else 0


# ---------------------------------------------------------------- 입력 검사
def _check_graph(graph: CellGraph) -> int:
    if not isinstance(graph, CellGraph):
        raise ValueError(f"graph 는 CellGraph 여야 합니다: {type(graph).__name__}")
    return graph.n_cells


def _rcv_n(receiver: np.ndarray, n: int | None = None) -> np.ndarray:
    rcv = _check_rcv(receiver)
    if n is not None and rcv.shape[0] != n:
        raise ValueError(f"receiver 길이 {rcv.shape[0]} 가 칸 수 {n} 와 다릅니다")
    return rcv


def _order(order, rcv: np.ndarray) -> np.ndarray:
    if order is None:
        return topo_order(rcv)
    o = np.ascontiguousarray(order)
    n = rcv.shape[0]
    if o.shape != (n,) or o.dtype.kind not in "iu":
        raise ValueError(f"order 는 (N,) 정수 배열이어야 합니다: {o.shape} {o.dtype}")
    o = o.astype(np.int64, copy=False)
    if n and (o.min() < 0 or o.max() >= n):
        raise ValueError("order 에 범위를 벗어난 셀 번호가 있습니다 (0..N-1)")
    return o


def _vec(x, name: str, n: int) -> np.ndarray:
    a = np.ascontiguousarray(x, dtype=np.float64)
    if a.ndim == 0:
        return np.full(n, float(a))
    if a.shape != (n,):
        raise ValueError(f"{name} 는 ({n},) 이어야 합니다: 모양 {a.shape}")
    return a


def _mask(x, name: str, n: int) -> np.ndarray:
    m = np.asarray(x)
    if m.shape != (n,) or m.dtype != np.bool_:
        raise ValueError(f"{name} 는 ({n},) bool 배열이어야 합니다: {m.shape} {m.dtype}")
    return np.ascontiguousarray(m)


def receiver_distance(graph: CellGraph, receiver: np.ndarray) -> np.ndarray:
    """칸마다 수신 셀까지 거리 [m] (출구 0).

    graph: CellGraph. receiver: (N,) 수신 셀. 이웃 슬롯의 dist(구면은 대원 거리)를 쓰고, 이웃이
    아닌 수신 셀(있으면 안 되지만)은 대표점 사이 직선 거리입니다. 반환: (N,) float64.
    """
    n = _check_graph(graph)
    rcv = _rcv_n(receiver, n)
    out = np.empty(n, dtype=np.float64)
    _receiver_distance_kernel(rcv, graph.nbr, graph.dist, graph.pos, out)
    return out


# ---------------------------------------------------------------- Hack 법칙
def longest_flow_path(
    graph: CellGraph,
    receiver: np.ndarray,
    order: np.ndarray | None = None,
    drainage_area: np.ndarray | None = None,
) -> tuple[np.ndarray, np.ndarray]:
    """칸마다 가장 먼 분수령(발원 칸)에서 그 칸까지의 흐름 경로 길이와 본류 기여 셀.

    graph: CellGraph. receiver: (N,) 수신 셀. order: (N,) topo_order 결과 또는 None(계산함).
    drainage_area: (N,) 상류 면적 [m²] (같은 길이일 때 큰 쪽을 고름) 또는 None(누적해 계산).
    반환: (length (N,) float64 [m], main_donor (N,) int64). 발원 칸의 길이는 0 이고 main_donor 는
    −1 입니다. main_donor[c] 는 c 로 이어지는 가장 긴 경로의 바로 위 칸입니다.
    """
    n = _check_graph(graph)
    rcv = _rcv_n(receiver, n)
    o = _order(order, rcv)
    a_up = (
        accumulate(rcv, o, graph.area)
        if drainage_area is None
        else _vec(drainage_area, "drainage_area", n)
    )
    rdist = np.empty(n, dtype=np.float64)
    _receiver_distance_kernel(rcv, graph.nbr, graph.dist, graph.pos, rdist)
    length = np.zeros(n, dtype=np.float64)
    main = np.full(n, -1, dtype=np.int64)
    _longest_path_kernel(o, rcv, rdist, a_up, length, main)
    return length, main


def main_stem_mask(receiver: np.ndarray, order: np.ndarray, main_donor: np.ndarray) -> np.ndarray:
    """본류 칸: 출구와, 하구(출구로 바로 흘러드는 칸)마다 main_donor 를 따라 거슬러 오른 칸들.

    receiver, order, main_donor: (N,) (longest_flow_path 결과). 반환: (N,) bool.
    """
    rcv = _rcv_n(receiver)
    n = rcv.shape[0]
    o = _order(order, rcv)
    m = np.ascontiguousarray(main_donor, dtype=np.int64)
    if m.shape != (n,):
        raise ValueError(f"main_donor 는 ({n},) 이어야 합니다: {m.shape}")
    out = np.zeros(n, dtype=np.bool_)
    _main_stem_kernel(o, rcv, m, out)
    return out


def hack_fit(
    graph: CellGraph,
    receiver: np.ndarray,
    order: np.ndarray | None,
    drainage_area: np.ndarray,
    mask: np.ndarray | None = None,
    min_area_cells: float = 10.0,
) -> tuple[float, float]:
    """본류를 따라 Hack 법칙 L = c·A^h 를 맞춥니다 (L [km], A [km²]).

    graph: CellGraph. receiver: (N,) 수신 셀. order: (N,) topo_order 결과 또는 None.
    drainage_area: (N,) 상류 면적 A↑ [m²]. mask: (N,) bool 쓸 칸(예: 육지) 또는 None(전부).
    min_area_cells: 평균 칸 면적의 이 배수보다 상류 면적이 작은 칸은 뺍니다(발원 근처는 L 이 칸
    크기로 끊겨 법칙이 안 맞음).

    점: 본류 칸(main_stem_mask) 가운데 출구가 아니고, mask 안이고, A ≥ 문턱, L > 0 인 칸마다
    (A↑, 가장 긴 흐름 경로 길이). log10 L = log10 c + h·log10 A 를 최소제곱으로 맞춥니다.
    반환: (coefficient_km, exponent). 점이 모자라면 (NaN, NaN).
    """
    n = _check_graph(graph)
    rcv = _rcv_n(receiver, n)
    o = _order(order, rcv)
    a_up = _vec(drainage_area, "drainage_area", n)
    if not (np.isfinite(min_area_cells) and min_area_cells > 0.0):
        raise ValueError(f"min_area_cells 는 0 보다 커야 합니다: {min_area_cells}")
    use = np.ones(n, dtype=np.bool_) if mask is None else _mask(mask, "mask", n)
    length, main = longest_flow_path(graph, rcv, o, a_up)
    stem = np.zeros(n, dtype=np.bool_)
    _main_stem_kernel(o, rcv, main, stem)
    a_min = min_area_cells * float(graph.area.mean())
    pick = stem & use & (rcv != np.arange(n)) & (a_up >= a_min) & (length > 0.0)
    if pick.sum() < 3:
        return float("nan"), float("nan")
    x = np.log10(a_up[pick] / 1.0e6)
    y = np.log10(length[pick] / 1.0e3)
    if np.ptp(x) <= 0.0:
        return float("nan"), float("nan")
    h, b = np.polyfit(x, y, 1)
    return float(10.0**b), float(h)


# ---------------------------------------------------------------- 하천 연결·역류
def river_reach_fraction(
    receiver: np.ndarray, order: np.ndarray | None, is_river: np.ndarray, is_sink: np.ndarray
) -> float:
    """하천 칸 가운데 하류로 강 칸만 지나 바다·호수·출구에 닿는 비율 (pipeline.md 12장, 100%).

    receiver: (N,) 수신 셀. order: (N,) topo_order 결과 또는 None. is_river: (N,) bool.
    is_sink: (N,) bool 바다·호수·출구 칸. 강이 아닌 육지 칸이나 출구 아닌 막다른 칸(자기 자신을
    가리키는 육지)에서 끊기면 닿지 못한 것입니다. 하천이 없으면 NaN.
    """
    rcv = _rcv_n(receiver)
    n = rcv.shape[0]
    o = _order(order, rcv)
    riv = _mask(is_river, "is_river", n)
    sink = _mask(is_sink, "is_sink", n)
    if not riv.any():
        return float("nan")
    ok = np.zeros(n, dtype=np.bool_)
    _reach_kernel(o, rcv, riv, sink, ok)
    return float(ok[riv].mean())


def river_backflow_count(
    receiver: np.ndarray, water_level: np.ndarray, is_river: np.ndarray, tol: float = 0.0
) -> int:
    """하천 칸 가운데 수신 셀의 수면이 더 높은(물이 거꾸로 오르는) 칸 수 (pipeline.md 12장, 0).

    receiver: (N,) 수신 셀. water_level: (N,) 수면 h_w [m] (물 없으면 NaN). is_river: (N,) bool.
    tol: 허용 [m]. 두 칸 수면이 모두 있을 때 h_w(r) > h_w(c) + tol 이면 셉니다.
    """
    rcv = _rcv_n(receiver)
    n = rcv.shape[0]
    hw = _vec(water_level, "water_level", n)
    riv = _mask(is_river, "is_river", n)
    c = np.nonzero(riv & (rcv != np.arange(n)))[0]
    h0 = hw[c]
    h1 = hw[rcv[c]]
    bad = np.isfinite(h0) & np.isfinite(h1) & (h1 > h0 + tol)
    return int(bad.sum())


# ---------------------------------------------------------------- 법칙 자기일관성
def law_slope_from_fields(
    graph: CellGraph,
    receiver: np.ndarray,
    discharge: np.ndarray,
    drainage_area: np.ndarray,
    sediment_flux: np.ndarray,
    uplift: np.ndarray,
    k_s: np.ndarray,
    s_crit: np.ndarray,
    cfg,
) -> np.ndarray:
    """솔버 결과 필드로 경사 법칙을 다시 계산합니다 (pipeline.md 7.1 3단계, 지표 암석 하나).

    graph: CellGraph. receiver: (N,) 수신 셀. discharge: Q [m³/yr]. drainage_area: A↑ [m²].
    sediment_flux: Qs [m³/yr]. uplift: U [m/yr]. k_s: 지표 암석의 (E/K)^(1/n) (솔버 필드).
    s_crit: 지표 암석의 S_crit [m/m]. 모두 (N,). cfg: 설정(landscape.theta, s_min,
    hillslope_diffusivity_m2_per_yr, deposition_g, fill_epsilon_m, climate.runoff_ref_m_per_yr).

    E = U + G·R_ref·Qs/Q, S = clip(1/(Q^θ/k_s + D/(E·a)), s_min, S_crit) (a = A↑/sqrt(칸 면적)),
    E ≤ 0 이면 s_min. 솔버는 한 칸을 수신 셀보다 ε(fill_epsilon_m) 이상 올리므로 max(S, ε/d) 입니다.
    반환: (N,) float64 [m/m], 출구는 NaN.
    """
    n = _check_graph(graph)
    rcv = _rcv_n(receiver, n)
    Q = _vec(discharge, "discharge", n)
    A_up = _vec(drainage_area, "drainage_area", n)
    Qs = _vec(sediment_flux, "sediment_flux", n)
    U = _vec(uplift, "uplift", n)
    ks = _vec(k_s, "k_s", n)
    sc = _vec(s_crit, "s_crit", n)
    ls = cfg.landscape
    theta = float(ls.theta)
    s_min = float(ls.s_min)
    diff = float(ls.hillslope_diffusivity_m2_per_yr)
    g_dep = float(ls.deposition_g)
    eps = float(ls.fill_epsilon_m)
    r_ref = float(cfg.climate.runoff_ref_m_per_yr)
    d = receiver_distance(graph, rcv)
    width = np.sqrt(graph.area)
    with np.errstate(divide="ignore", invalid="ignore", over="ignore"):
        E = U + g_dep * r_ref * Qs / Q
        good = (E > 0.0) & (ks > 0.0) & (Q > 0.0)
        inv = Q**theta / ks + diff / (E * (A_up / width))
        S = np.where(good, 1.0 / inv, s_min)
        S = np.maximum(np.minimum(S, sc), s_min)
        S = np.maximum(S, eps / d)
    S[rcv == np.arange(n)] = np.nan
    return S


def law_cross_mask(receiver: np.ndarray, z: np.ndarray, strata_bottom: np.ndarray) -> np.ndarray:
    """칸과 수신 셀 사이에서 층 경계를 넘는 칸 (법칙 비교에서 뺄 칸).

    receiver: (N,) 수신 셀. z: (N,) 고도 [m]. strata_bottom: (N, L) 층 바닥 고도 [m].
    이런 칸의 경사는 두 층 경사의 평균이라 지표 암석 하나의 법칙과 다릅니다. 반환: (N,) bool.
    """
    rcv = _rcv_n(receiver)
    n = rcv.shape[0]
    zz = _vec(z, "z", n)
    b = np.ascontiguousarray(strata_bottom, dtype=np.float64)
    if b.ndim != 2 or b.shape[0] != n:
        raise ValueError(f"strata_bottom 은 ({n}, L) 이어야 합니다: {b.shape}")
    out = np.zeros(n, dtype=np.bool_)
    _layer_cross_kernel(rcv, zz, b, out)
    return out


def law_consistency(
    slope: np.ndarray, law_slope: np.ndarray, s_crit: np.ndarray, mask: np.ndarray | None = None
) -> dict:
    """실제 경사와 법칙 경사의 상대 차이 |S − 법칙|/법칙 (pipeline.md 7.1·12장, 중앙값 < 1e-3).

    slope: (N,) 실제 경사 [m/m]. law_slope: (N,) 법칙 경사 [m/m] (NaN 은 뺌). s_crit: (N,) 임계
    경사 [m/m] (S < S_crit 인 칸만 봄). mask: (N,) bool 또는 None(전부).
    반환: {"median", "max", "n"}. 볼 칸이 없으면 median·max 는 NaN.
    """
    s = np.asarray(slope, dtype=np.float64)
    if s.ndim != 1:
        raise ValueError(f"slope 는 (N,) 1차원 배열이어야 합니다: {s.shape}")
    n = s.shape[0]
    law = _vec(law_slope, "law_slope", n)
    sc = _vec(s_crit, "s_crit", n)
    use = np.ones(n, dtype=np.bool_) if mask is None else _mask(mask, "mask", n)
    use = use & np.isfinite(s) & np.isfinite(law) & (law > 0.0) & (s < sc)
    if not use.any():
        return {"median": float("nan"), "max": float("nan"), "n": 0}
    rel = np.abs(s[use] - law[use]) / law[use]
    return {"median": float(np.median(rel)), "max": float(rel.max()), "n": int(use.sum())}


# ---------------------------------------------------------------- 수지
def budget_error(
    receiver: np.ndarray,
    flux: np.ndarray,
    source: np.ndarray,
    order: np.ndarray | None = None,
    clip_negative: bool = False,
) -> float:
    """흐름 수지의 상대 오차 (pipeline.md 12장 '퇴적물·물 수지', 기준 1e-6).

    receiver: (N,) 수신 셀. flux: (N,) 검사할 누적 흐름(예: Q [m³/yr], Qs [m³/yr]).
    source: (N,) 칸마다 들어오는 양 [m³/yr] (예: 면적·R_eff + 들어오는 물, 면적·U).
    order: (N,) topo_order 결과 또는 None. clip_negative: Qs 처럼 누적 뒤 0 에서 자른 흐름이면 True.

    기대값 F = accumulate(source) (clip_negative 면 max(F, 0)) 와 비교해 아래 두 값 중 큰 것을
    돌려줍니다.
    - 출구 수지: |Σ_출구 flux − Σ_출구 F| / Σ_출구 |F|. 자르지 않으면 Σ_출구 F = Σ source 입니다.
    - 칸 수지: max_c |flux_c − F_c| / Σ_출구 |F| (칸 하나의 어긋남을 전체 흐름 대비로).
    """
    rcv = _rcv_n(receiver)
    n = rcv.shape[0]
    f = _vec(flux, "flux", n)
    src = _vec(source, "source", n)
    if not (np.isfinite(f).all() and np.isfinite(src).all()):
        raise ValueError("flux 와 source 는 유한한 값이어야 합니다")
    o = _order(order, rcv)
    expect = accumulate(rcv, o, src)
    if clip_negative:
        expect = np.maximum(expect, 0.0)
    root = rcv == np.arange(n)
    if clip_negative:
        total_in = expect[root].sum()
    else:
        total_in = src.sum()
    scale = np.abs(expect[root]).sum()
    if not scale > 0.0:
        return 0.0 if np.abs(f).max(initial=0.0) == 0.0 else float("inf")
    outlet = abs(f[root].sum() - total_in) / scale
    cell = np.abs(f - expect).max() / scale
    return float(max(outlet, cell))


# ---------------------------------------------------------------- 격자 정렬 지수
def _face_layout(graph: CellGraph) -> tuple[int, int, int]:
    """(면 하나의 칸 수, ny, nx). 구면 (6, n, n), 평면 (ny, nx)."""
    if graph.kind == "sphere":
        _, n, _ = graph.shape
        return n * n, n, n
    if graph.kind == "flat":
        ny, nx = graph.shape
        return ny * nx, ny, nx
    raise ValueError(f"그래프 종류는 sphere 또는 flat 이어야 합니다: {graph.kind}")


def alignment_counts(
    graph: CellGraph,
    receiver: np.ndarray,
    is_river: np.ndarray,
    band: str = "all",
    steps: int = ALIGNMENT_STEPS,
    tol_deg: float = ALIGNMENT_TOL_DEG,
) -> tuple[int, int]:
    """격자 정렬 지수의 (정렬된 칸 수, 쓴 칸 수).

    graph: CellGraph (구면·평면). receiver: (N,) 수신 셀. is_river: (N,) bool.
    band: 'all' | 'edge' | 'center'. edge 는 시작 칸의 면 좌표 |a| > 0.9 또는 |b| > 0.9
    (평면은 영역 전체를 [-1, 1]² 로 본 좌표). steps: 하류로 따라갈 칸 수. tol_deg: 허용 각도 [도].

    방향은 면마다 로컬 격자 좌표 (i, j) 로 잽니다. 노드 흔들기 위치는 쓰지 않습니다.
    steps 칸을 가기 전에 출구에 닿거나 면 경계를 넘는 경로는 뺍니다.
    """
    n = _check_graph(graph)
    rcv = _rcv_n(receiver, n)
    riv = _mask(is_river, "is_river", n)
    if band not in _BANDS:
        raise ValueError(f"band 는 'all', 'edge', 'center' 가운데 하나여야 합니다: {band!r}")
    steps = int(steps)
    if steps < 1:
        raise ValueError(f"steps 는 1 이상이어야 합니다: {steps}")
    if not (0.0 < tol_deg < 22.5):
        raise ValueError(f"tol_deg 는 (0, 22.5) 도여야 합니다: {tol_deg}")
    face_cells, ny, nx = _face_layout(graph)
    flag = np.empty(n, dtype=np.int8)
    _alignment_kernel(rcv, riv, face_cells, ny, nx, _BANDS[band], EDGE_BAND, steps, tol_deg, flag)
    return int((flag == 1).sum()), int((flag >= 0).sum())


def grid_alignment(
    graph: CellGraph,
    receiver: np.ndarray,
    is_river: np.ndarray,
    band: str = "all",
    steps: int = ALIGNMENT_STEPS,
    tol_deg: float = ALIGNMENT_TOL_DEG,
) -> float:
    """격자 정렬 지수 (가이드 2장): 정렬 비율 / 무작위 기대 비율 (2·tol/45 = 10/45).

    인자는 alignment_counts 와 같습니다. 1 이면 결이 없고, 클수록 하천이 격자 방향으로 흐릅니다
    (가이드: 원형 섬 D8 2.92, 목표 < 1.3). 쓴 칸이 없으면 NaN.
    """
    hit, used = alignment_counts(graph, receiver, is_river, band, steps, tol_deg)
    if used == 0:
        return float("nan")
    return (hit / used) / (2.0 * tol_deg / 45.0)
