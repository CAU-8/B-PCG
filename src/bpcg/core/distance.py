"""가장 가까운 출발 칸까지의 거리 (여러 출발점 다익스트라, docs/pipeline.md 3장).

그래프 경로 길이(8방향 걸음의 합)를 쓰면 등거리선이 팔각형이 되고, 큐브 면 경계에서
격자 방향이 바뀌는 곳에 이음새가 생깁니다(설계도 5장). 그래서 칸마다 '어느 출발 칸에서
왔는지'를 이웃에게 넘겨주고(벡터 전파), 힙의 키는 그 출발 칸까지의 직선 거리로 둡니다.
구면은 대원 거리, 평면은 유클리드 거리이고 등거리선이 원이 됩니다.

구현 메모
- 힙 키는 3D 대표점 사이 현(chord) 길이의 제곱입니다. 구면에서 현 길이는 대원 거리와
  단조 관계라서 순서가 같고, atan2 를 칸마다 부르지 않아도 됩니다. 마지막에 출발 칸이
  정해지면 대원 거리 R·atan2(|a×b|, a·b) 로 정확히 다시 잽니다.
- 키가 경로를 따라 단조롭지 않을 수 있어서, 더 가까운 출발 칸이 나타나면 이미 꺼낸 칸도
  다시 힙에 넣습니다(라벨 수정). 거리가 같으면 출발 칸 번호가 작은 쪽을 고릅니다.
- 출발 칸의 보로노이 영역이 그래프 이웃 경로로 이어지지 않는 드문 경우에는 두 번째로
  가까운 출발 칸이 뽑힐 수 있습니다(덴마크식 벡터 거리 변환과 같은 한계). 오차는 칸 크기의
  일부입니다.
"""

import math

import numpy as np
from numba import njit, prange

from bpcg.core.graph import CellGraph


@njit(cache=True, inline="always")
def _heap_less(hkey: np.ndarray, hcell: np.ndarray, a: int, b: int) -> bool:
    """힙 원소 a 가 b 보다 앞인지 (키, 같으면 셀 번호)."""
    if hkey[a] < hkey[b]:
        return True
    if hkey[a] > hkey[b]:
        return False
    return hcell[a] < hcell[b]


@njit(cache=True, inline="always")
def _heap_swap(hkey: np.ndarray, hcell: np.ndarray, hsrc: np.ndarray, a: int, b: int) -> None:
    tk = hkey[a]
    hkey[a] = hkey[b]
    hkey[b] = tk
    tc = hcell[a]
    hcell[a] = hcell[b]
    hcell[b] = tc
    ts = hsrc[a]
    hsrc[a] = hsrc[b]
    hsrc[b] = ts


@njit(cache=True)
def _sift_up(hkey: np.ndarray, hcell: np.ndarray, hsrc: np.ndarray, k: int) -> None:
    while k > 0:
        parent = (k - 1) >> 1
        if _heap_less(hkey, hcell, k, parent):
            _heap_swap(hkey, hcell, hsrc, k, parent)
            k = parent
        else:
            break


@njit(cache=True)
def _sift_down(hkey: np.ndarray, hcell: np.ndarray, hsrc: np.ndarray, size: int) -> None:
    k = 0
    while True:
        left = 2 * k + 1
        if left >= size:
            break
        best = left
        right = left + 1
        if right < size and _heap_less(hkey, hcell, right, left):
            best = right
        if _heap_less(hkey, hcell, best, k):
            _heap_swap(hkey, hcell, hsrc, k, best)
            k = best
        else:
            break


@njit(cache=True)
def _propagate_sources(
    pos: np.ndarray, nbr: np.ndarray, is_source: np.ndarray, max_key: float
) -> tuple[np.ndarray, np.ndarray]:
    """벡터 전파 다익스트라. pos (N,3) [m], nbr (N,K) int, is_source (N,) bool.

    반환: key (N,) 출발 칸까지 현 길이 제곱 [m²] (닿지 않으면 inf), src (N,) int64 (없으면 -1).
    max_key 보다 먼 칸에는 값을 주지 않고 거기서 전파를 멈춥니다.
    """
    n_cells = pos.shape[0]
    n_slots = nbr.shape[1]
    key = np.full(n_cells, np.inf)
    src = np.full(n_cells, -1, dtype=np.int64)

    cap = max(16, n_cells + n_cells // 2)
    hkey = np.empty(cap, dtype=np.float64)
    hcell = np.empty(cap, dtype=np.int64)
    hsrc = np.empty(cap, dtype=np.int64)
    size = 0
    # 출발 칸은 키 0, 셀 번호 순으로 넣으므로 그대로 힙 순서입니다.
    for c in range(n_cells):
        if is_source[c]:
            key[c] = 0.0
            src[c] = c
            hkey[size] = 0.0
            hcell[size] = c
            hsrc[size] = c
            size += 1

    while size > 0:
        k0 = hkey[0]
        c = hcell[0]
        s = hsrc[0]
        size -= 1
        if size > 0:
            hkey[0] = hkey[size]
            hcell[0] = hcell[size]
            hsrc[0] = hsrc[size]
            _sift_down(hkey, hcell, hsrc, size)
        # 그 사이 더 가까운 출발 칸으로 바뀐 칸이면 낡은 원소이므로 건너뜁니다.
        if k0 != key[c] or s != src[c]:
            continue
        sx = pos[s, 0]
        sy = pos[s, 1]
        sz = pos[s, 2]
        for slot in range(n_slots):
            v = nbr[c, slot]
            if v < 0:
                continue
            dx = pos[v, 0] - sx
            dy = pos[v, 1] - sy
            dz = pos[v, 2] - sz
            d2 = dx * dx + dy * dy + dz * dz
            if d2 > max_key:
                continue
            if d2 < key[v] or (d2 == key[v] and s < src[v]):
                key[v] = d2
                src[v] = s
                if size == cap:
                    cap *= 2
                    nk = np.empty(cap, dtype=np.float64)
                    nc = np.empty(cap, dtype=np.int64)
                    ns = np.empty(cap, dtype=np.int64)
                    nk[:size] = hkey[:size]
                    nc[:size] = hcell[:size]
                    ns[:size] = hsrc[:size]
                    hkey = nk
                    hcell = nc
                    hsrc = ns
                hkey[size] = d2
                hcell[size] = v
                hsrc[size] = s
                _sift_up(hkey, hcell, hsrc, size)
                size += 1
    return key, src


@njit(cache=True, parallel=True)
def _final_distance(
    pos: np.ndarray, src: np.ndarray, radius: float, is_sphere: bool, max_dist: float
) -> np.ndarray:
    """정해진 출발 칸까지 정확한 거리 [m] (구면 대원, 평면 유클리드). max_dist 초과는 inf."""
    n_cells = pos.shape[0]
    out = np.empty(n_cells, dtype=np.float64)
    for c in prange(n_cells):
        s = src[c]
        if s < 0:
            out[c] = np.inf
            continue
        ax = pos[c, 0]
        ay = pos[c, 1]
        az = pos[c, 2]
        bx = pos[s, 0]
        by = pos[s, 1]
        bz = pos[s, 2]
        if is_sphere:
            cx = ay * bz - az * by
            cy = az * bx - ax * bz
            cz = ax * by - ay * bx
            cross = math.sqrt(cx * cx + cy * cy + cz * cz)
            dot = ax * bx + ay * by + az * bz
            d = radius * math.atan2(cross, dot)
        else:
            dx = ax - bx
            dy = ay - by
            dz = az - bz
            d = math.sqrt(dx * dx + dy * dy + dz * dz)
        out[c] = d if d <= max_dist else np.inf
    return out


def _check_inputs(graph: CellGraph, is_source: np.ndarray, max_dist: float) -> np.ndarray:
    if not isinstance(graph, CellGraph):
        raise ValueError("graph 는 bpcg.core.graph.CellGraph 여야 합니다")
    if graph.kind not in ("sphere", "flat"):
        raise ValueError(f"graph.kind 는 'sphere' 또는 'flat' 이어야 합니다: {graph.kind!r}")
    if graph.kind == "sphere" and not (graph.R is not None and graph.R > 0):
        raise ValueError("구면 그래프에는 반지름 R 이 있어야 합니다")
    mask = np.asarray(is_source)
    if mask.dtype != np.bool_:
        raise ValueError(f"is_source 는 bool 배열이어야 합니다 (셀 번호 목록이 아님): {mask.dtype}")
    if mask.shape != (graph.n_cells,):
        raise ValueError(
            f"is_source 모양이 그래프와 다릅니다: {mask.shape}, 기대 ({graph.n_cells},)"
        )
    if math.isnan(max_dist) or max_dist < 0:
        raise ValueError(f"max_dist 는 0 이상이어야 합니다: {max_dist}")
    return np.ascontiguousarray(mask)


def nearest_source(
    graph: CellGraph, is_source: np.ndarray, max_dist: float = np.inf
) -> tuple[np.ndarray, np.ndarray]:
    """칸마다 가장 가까운 출발 칸과 그 거리 (구면 대원 거리, 평면 유클리드 거리).

    graph: CellGraph (pos [m], nbr). is_source: (N,) bool, 출발 칸 표시.
    max_dist: 이 거리 [m] 를 넘는 칸은 찾지 않습니다(전파도 거기서 멈춤).
    반환: dist (N,) float64 [m], src (N,) int64 출발 칸 번호.
    출발 칸이 없거나 닿지 않거나 max_dist 를 넘으면 dist = inf, src = -1.
    거리가 같으면 번호가 작은 출발 칸을 고릅니다.
    """
    max_dist = float(max_dist)
    mask = _check_inputs(graph, is_source, max_dist)
    pos = np.ascontiguousarray(graph.pos, dtype=np.float64)
    nbr = np.ascontiguousarray(graph.nbr)
    is_sphere = graph.kind == "sphere"
    radius = float(graph.R) if is_sphere else 0.0

    # 거리 상한을 현 길이 제곱으로 바꿉니다. 반올림으로 경계 칸을 잃지 않게 조금 넉넉히 두고,
    # 마지막에 정확한 거리로 다시 자릅니다.
    if not math.isfinite(max_dist):
        max_key = np.inf
    elif is_sphere:
        if max_dist >= math.pi * radius:
            max_key = np.inf
        else:
            chord = 2.0 * radius * math.sin(0.5 * max_dist / radius)
            max_key = chord * chord * (1.0 + 1e-9)
    else:
        max_key = max_dist * max_dist * (1.0 + 1e-9)

    _, src = _propagate_sources(pos, nbr, mask, max_key)
    dist = _final_distance(pos, src, radius, is_sphere, max_dist)
    src = np.where(np.isfinite(dist), src, -1).astype(np.int64)
    return dist, src


def nearest_source_values(
    graph: CellGraph,
    is_source: np.ndarray,
    values: np.ndarray,
    max_dist: float = np.inf,
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """nearest_source 에 더해 가장 가까운 출발 칸의 값을 넘겨받습니다.

    values: (N, ...) 칸마다의 값 (출발 칸의 값만 읽음).
    반환: (dist (N,) [m], src (N,) int64, values[src] (N, ...)).
    출발 칸이 없는 칸의 값은 실수면 NaN, 부호 있는 정수면 -1, 그 밖(bool, 부호 없는 정수)은 0.
    """
    vals = np.asarray(values)
    if vals.ndim < 1 or vals.shape[0] != graph.n_cells:
        raise ValueError(
            f"values 의 첫 축 길이가 칸 수와 같아야 합니다: {vals.shape}, 칸 수 {graph.n_cells}"
        )
    dist, src = nearest_source(graph, is_source, max_dist)
    ok = src >= 0
    out = vals[np.where(ok, src, 0)]
    if not ok.all():
        if np.issubdtype(out.dtype, np.floating) or np.issubdtype(out.dtype, np.complexfloating):
            fill = np.nan
        elif np.issubdtype(out.dtype, np.signedinteger):
            fill = -1
        else:
            fill = 0
        out[~ok] = fill
    return dist, src, out
