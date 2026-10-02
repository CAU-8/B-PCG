"""웅덩이 채우기: priority-flood (가이드 2장 ①, docs/pipeline.md 6장).

바다·출구 칸에서 홍수를 시작해, 가장 낮은 칸부터 꺼내며 이웃의 높이를 정합니다.

- `fill_depressions`: ẑ_c = min_γ max_{c''∈γ} z_c''. 호수 수면 높이(완전히 평평한 호수)입니다.
- `fill_epsilon`: z̃_c' = max(z_c', z̃_c + ε). 라우팅용으로, 출구가 아닌 모든 칸에
  확실히(ε 이상) 낮은 이웃이 생깁니다.

힙의 키는 (높이, 셀 번호)라서 같은 높이일 때도 꺼내는 순서가 늘 같습니다.
두 결과 모두 '이웃 가운데 가장 낮은 값'으로 정해지는 고정점이라 같은 높이의 처리 순서와 무관하게
하나로 정해지지만, 순서까지 고정해 두면 디버깅할 때 과정도 같아집니다.
"""

import numpy as np
from numba import njit

# ---------------------------------------------------------------- 이진 힙 (키 = (높이, 셀 번호))


@njit(cache=True, inline="always")
def _key_less(za: float, ia: np.int64, zb: float, ib: np.int64) -> bool:
    """(za, ia) < (zb, ib) 사전식 비교."""
    return za < zb or (za == zb and ia < ib)


@njit(cache=True, inline="always")
def _heap_push(hz: np.ndarray, hi: np.ndarray, size: int, z: float, i: np.int64) -> int:
    """힙에 (z, i)를 넣고 새 크기를 돌려줍니다."""
    k = size
    while k > 0:
        p = (k - 1) >> 1
        if _key_less(z, i, hz[p], hi[p]):
            hz[k] = hz[p]
            hi[k] = hi[p]
            k = p
        else:
            break
    hz[k] = z
    hi[k] = i
    return size + 1


@njit(cache=True, inline="always")
def _heap_pop(hz: np.ndarray, hi: np.ndarray, size: int) -> int:
    """맨 위 원소를 지우고 새 크기를 돌려줍니다. 맨 위 값은 부르기 전에 읽어 둡니다."""
    size -= 1
    z = hz[size]
    i = hi[size]
    k = 0
    while True:
        a = 2 * k + 1
        if a >= size:
            break
        m = a
        b = a + 1
        if b < size and _key_less(hz[b], hi[b], hz[a], hi[a]):
            m = b
        if _key_less(hz[m], hi[m], z, i):
            hz[k] = hz[m]
            hi[k] = hi[m]
            k = m
        else:
            break
    hz[k] = z
    hi[k] = i
    return size


# ---------------------------------------------------------------- 커널


@njit(cache=True)
def _fill_kernel(z: np.ndarray, nbr: np.ndarray, is_outlet: np.ndarray, out: np.ndarray) -> int:
    """priority-flood + 웅덩이 큐 (Barnes 2014). 방문한 칸 수를 돌려줍니다.

    채워지는 칸(z ≤ 지금 넘침 높이)은 키가 지금 꺼낸 칸과 같으므로
    힙 대신 FIFO 큐로 먼저 처리합니다.
    키가 줄지 않는 순서는 그대로라 결과는 순수 힙과 같습니다.
    """
    n = z.shape[0]
    n_slot = nbr.shape[1]
    visited = np.zeros(n, dtype=np.bool_)
    hz = np.empty(n, dtype=np.float64)
    hi = np.empty(n, dtype=np.int64)
    pit = np.empty(n, dtype=np.int64)
    size = 0
    n_visited = 0
    for c in range(n):
        if is_outlet[c]:
            out[c] = z[c]
            visited[c] = True
            n_visited += 1
            size = _heap_push(hz, hi, size, z[c], np.int64(c))
    head = 0
    tail = 0
    while size > 0 or head < tail:
        if head < tail:
            c = pit[head]
            head += 1
            if head == tail:
                head = 0
                tail = 0
        else:
            c = hi[0]
            size = _heap_pop(hz, hi, size)
        zc = out[c]
        for s in range(n_slot):
            k = nbr[c, s]
            if k < 0 or visited[k]:
                continue
            visited[k] = True
            n_visited += 1
            if z[k] <= zc:
                out[k] = zc
                pit[tail] = k
                tail += 1
            else:
                out[k] = z[k]
                size = _heap_push(hz, hi, size, z[k], np.int64(k))
    return n_visited


@njit(cache=True)
def _fill_epsilon_kernel(
    z: np.ndarray, nbr: np.ndarray, is_outlet: np.ndarray, eps: float, out: np.ndarray
) -> int:
    """ε 기울기를 준 priority-flood (순수 힙). 방문한 칸 수를 돌려줍니다.

    넣는 키 z̃_c' ≥ z̃_c + ε 가 늘 꺼낸 키보다 커서 꺼내는 순서는 줄지 않습니다.
    그래서 칸 c' 를 처음 찾은 이웃은 c' 의 이웃 가운데 z̃ 가 가장 낮은 칸이고,
    z̃_c' = max(z_c', min_k z̃_k + ε) 를 만족합니다.
    """
    n = z.shape[0]
    n_slot = nbr.shape[1]
    visited = np.zeros(n, dtype=np.bool_)
    hz = np.empty(n, dtype=np.float64)
    hi = np.empty(n, dtype=np.int64)
    size = 0
    n_visited = 0
    for c in range(n):
        if is_outlet[c]:
            out[c] = z[c]
            visited[c] = True
            n_visited += 1
            size = _heap_push(hz, hi, size, z[c], np.int64(c))
    while size > 0:
        c = hi[0]
        size = _heap_pop(hz, hi, size)
        zmin = out[c] + eps
        for s in range(n_slot):
            k = nbr[c, s]
            if k < 0 or visited[k]:
                continue
            visited[k] = True
            n_visited += 1
            zk = z[k] if z[k] > zmin else zmin
            out[k] = zk
            size = _heap_push(hz, hi, size, zk, np.int64(k))
    return n_visited


# ---------------------------------------------------------------- 공개 함수


def _check_inputs(z: np.ndarray, nbr: np.ndarray, is_outlet: np.ndarray):
    z = np.ascontiguousarray(z, dtype=np.float64)
    if z.ndim != 1:
        raise ValueError(f"z 는 (N,) 1차원 배열이어야 합니다: 모양 {z.shape}")
    n = z.shape[0]
    nbr = np.ascontiguousarray(nbr)
    if nbr.ndim != 2 or nbr.shape[0] != n:
        raise ValueError(f"nbr 는 (N, K) 이어야 합니다: z {z.shape}, nbr {nbr.shape}")
    if nbr.dtype.kind != "i":
        raise ValueError(f"nbr 는 정수 배열이어야 합니다: {nbr.dtype}")
    if nbr.size and (nbr.min() < -1 or nbr.max() >= n):
        raise ValueError("nbr 에 범위를 벗어난 셀 번호가 있습니다 (-1 또는 0..N-1)")
    is_outlet = np.ascontiguousarray(is_outlet, dtype=np.bool_)
    if is_outlet.shape != (n,):
        raise ValueError(f"is_outlet 은 (N,) 이어야 합니다: {is_outlet.shape}")
    if not np.isfinite(z).all():
        raise ValueError("z 에 NaN 이나 무한대가 있습니다")
    if not is_outlet.any():
        raise ValueError("출구(바다) 칸이 하나도 없습니다. 물이 빠져나갈 곳이 필요합니다")
    return z, nbr, is_outlet


def fill_depressions(z: np.ndarray, nbr: np.ndarray, is_outlet: np.ndarray) -> np.ndarray:
    """웅덩이를 넘침 높이까지 채웁니다 (priority-flood, 가이드 2장 ①).

    z: (N,) 고도 [m]. nbr: (N, K) 이웃 셀 번호, 없으면 -1. is_outlet: (N,) bool 바다·출구 칸.
    반환: (N,) float64 채운 고도 ẑ [m]. ẑ ≥ z, 출구는 ẑ = z, 연결된 호수(ẑ > z)마다 ẑ 가 같습니다.
    출구와 이어지지 않은 칸이 있으면 ValueError.
    """
    z, nbr, is_outlet = _check_inputs(z, nbr, is_outlet)
    out = np.empty_like(z)
    n_visited = _fill_kernel(z, nbr, is_outlet, out)
    if n_visited != z.shape[0]:
        raise ValueError(f"출구와 이어지지 않은 칸이 {z.shape[0] - n_visited}개 있습니다")
    return out


def fill_epsilon(z: np.ndarray, nbr: np.ndarray, is_outlet: np.ndarray, eps: float) -> np.ndarray:
    """라우팅용 ε 채움: z̃_c' = max(z_c', z̃_c + ε) (가이드 2장 ①).

    z: (N,) 고도 [m]. nbr: (N, K) 이웃 셀 번호, 없으면 -1. is_outlet: (N,) bool 바다·출구 칸.
    eps: 채움 기울기 [m] (설정 landscape.fill_epsilon_m), 0 보다 커야 합니다.
    반환: (N,) float64 z̃ [m]. z̃ ≥ ẑ ≥ z 이고,
    출구가 아닌 모든 칸에 z̃ 가 ε 이상 낮은 이웃이 있습니다.
    z̃ 에 다시 부르면 그대로 돌려줍니다(멱등).
    """
    z, nbr, is_outlet = _check_inputs(z, nbr, is_outlet)
    eps = float(eps)
    if not (np.isfinite(eps) and eps > 0.0):
        raise ValueError(f"eps 는 0 보다 큰 유한한 값이어야 합니다: {eps}")
    out = np.empty_like(z)
    n_visited = _fill_epsilon_kernel(z, nbr, is_outlet, eps, out)
    if n_visited != z.shape[0]:
        raise ValueError(f"출구와 이어지지 않은 칸이 {z.shape[0] - n_visited}개 있습니다")
    return out
