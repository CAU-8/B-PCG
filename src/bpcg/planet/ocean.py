"""해수면과 바다 마스크 (docs/pipeline.md 4.3, 설계도 5장 3번).

해수면 h 는 바다 물 부피 보존 V(h) = Σ A·max(h − z, 0) = W 로 정하고, 바다 비율은 결과로 나옵니다.
바다는 z < h 이면서 가장 큰(면적) 연결 성분에 속하는 칸입니다. 이어지지 않은 낮은 땅은 육지입니다.
이후 모든 고도는 z − h (해수면 0) 로 씁니다.
"""

import math

import numpy as np
from numba import njit

from bpcg.core.graph import CellGraph

SEA_LEVEL_REL_TOL = 1e-9  # 부피 상대 오차 (docs/pipeline.md 4.3)
_MAX_BISECTION = 200


@njit(cache=True)
def _volume_below(z: np.ndarray, area: np.ndarray, h: float) -> float:
    """V(h) = Σ A·max(h − z, 0) [m³]. 직렬 합이라 스레드 수와 상관없이 같은 값입니다."""
    total = 0.0
    for c in range(z.shape[0]):
        d = h - z[c]
        if d > 0.0:
            total += area[c] * d
    return total


def _check_z_area(z_platform: np.ndarray, area: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    z = np.ascontiguousarray(z_platform, dtype=np.float64)
    a = np.ascontiguousarray(area, dtype=np.float64)
    if z.ndim != 1 or z.shape != a.shape or z.size == 0:
        raise ValueError(f"z 와 area 는 같은 길이의 1차원 배열이어야 합니다: {z.shape}, {a.shape}")
    if not np.isfinite(z).all():
        raise ValueError("z_platform 에 NaN 이나 inf 가 있습니다")
    if not (np.isfinite(a).all() and (a > 0).all()):
        raise ValueError("area 는 모두 0 보다 큰 유한한 값이어야 합니다")
    return z, a


def ocean_volume(z_platform: np.ndarray, area: np.ndarray, h: float) -> float:
    """해수면 h [m] 아래 물 부피 V(h) = Σ A·max(h − z, 0) [m³].

    z_platform: (N,) 기준 고도 [m]. area: (N,) 칸 면적 [m²].
    """
    z, a = _check_z_area(z_platform, area)
    return float(_volume_below(z, a, float(h)))


def sea_level(z_platform: np.ndarray, area: np.ndarray, water_volume: float) -> float:
    """물 부피 보존 해수면 h [m]. V(h) = water_volume 을 이분법으로 풉니다 (상대 오차 1e-9).

    z_platform: (N,) 기준 고도 [m], float64. area: (N,) 칸 면적 [m²].
    water_volume: 물 부피 [m³] > 0.
    반환: h [m] (z_platform 과 같은 기준).
    """
    z, a = _check_z_area(z_platform, area)
    w = float(water_volume)
    if not (math.isfinite(w) and w > 0.0):
        raise ValueError(f"water_volume 은 0 보다 큰 유한한 값이어야 합니다: {water_volume}")
    lo = float(z.min())  # V(lo) = 0 < W
    hi = float(z.max()) + w / float(a.sum())  # V(hi) ≥ (hi − max z)·ΣA = W
    tol = SEA_LEVEL_REL_TOL * w
    h = hi
    for _ in range(_MAX_BISECTION):
        h = 0.5 * (lo + hi)
        v = _volume_below(z, a, h)
        if abs(v - w) <= tol:
            return h
        if v < w:
            lo = h
        else:
            hi = h
        if hi - lo <= 4.0 * np.spacing(abs(h) + 1.0):
            break
    # 부피가 h 에 대해 조각별 선형이므로 마지막 구간에서 한 번 선형으로 맞춥니다.
    v_lo = _volume_below(z, a, lo)
    v_hi = _volume_below(z, a, hi)
    if v_hi > v_lo:
        h = lo + (w - v_lo) * (hi - lo) / (v_hi - v_lo)
    return float(h)


@njit(cache=True)
def _label_components(mask: np.ndarray, nbr: np.ndarray) -> tuple[np.ndarray, int]:
    """mask 칸들의 연결 성분 번호 (N,) int64 (밖은 -1) 와 성분 수.

    작은 셀 번호부터 번호를 붙입니다.
    """
    n = mask.shape[0]
    labels = np.full(n, -1, dtype=np.int64)
    stack = np.empty(n, dtype=np.int64)
    n_lab = 0
    for c in range(n):
        if not mask[c] or labels[c] >= 0:
            continue
        labels[c] = n_lab
        top = 0
        stack[top] = c
        top += 1
        while top > 0:
            top -= 1
            u = stack[top]
            for s in range(nbr.shape[1]):
                v = nbr[u, s]
                if v >= 0 and mask[v] and labels[v] < 0:
                    labels[v] = n_lab
                    stack[top] = v
                    top += 1
        n_lab += 1
    return labels, n_lab


def ocean_mask(graph: CellGraph, z_platform: np.ndarray, h: float) -> np.ndarray:
    """바다 칸 (N,) bool: z < h 이고 그런 칸들의 가장 큰(면적) 연결 성분에 속하는 칸.

    graph: CellGraph (nbr 로 연결을 봅니다). z_platform: (N,) [m]. h: 해수면 [m].
    면적이 같은 성분이 둘이면 번호가 작은 칸을 가진 쪽입니다.
    """
    if not isinstance(graph, CellGraph):
        raise ValueError("graph 는 bpcg.core.graph.CellGraph 여야 합니다")
    z, a = _check_z_area(z_platform, graph.area)
    if not math.isfinite(float(h)):
        raise ValueError(f"해수면 h 는 유한한 값이어야 합니다: {h}")
    low = z < float(h)
    labels, n_lab = _label_components(low, np.ascontiguousarray(graph.nbr))
    out = np.zeros(z.shape[0], dtype=bool)
    if n_lab == 0:
        return out
    comp_area = np.bincount(labels[low], weights=a[low], minlength=n_lab)
    out[labels == int(np.argmax(comp_area))] = True
    return out
