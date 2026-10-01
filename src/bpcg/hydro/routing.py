"""수신 셀(D8 + 히스테리시스)과 하류부터의 순서 (가이드 2장 ②③, 4장 히스테리시스).

- `d8_receivers`: 각 칸이 물을 보내는 이웃 r(c) = argmax (z̃_c − z̃_c')/d_cc'.
  노드 흔들기를 한 그래프에서는 dist 가 흔든 점 사이 거리라서 방향이 칸마다 제각각이 됩니다.
- `topo_order`: rank(r(c)) < rank(c) 인 순서 (출구부터 깊이 우선).
- `donor_lists`: 기여 셀 목록(CSR). 순서·강 구간 계산이 함께 씁니다.
"""

import numpy as np
from numba import njit

# ---------------------------------------------------------------- 커널


@njit(cache=True)
def _d8_kernel(
    zt: np.ndarray,
    nbr: np.ndarray,
    dist: np.ndarray,
    is_outlet: np.ndarray,
    prev: np.ndarray,
    has_prev: bool,
    eta: float,
    rcv: np.ndarray,
    slope: np.ndarray,
) -> int:
    """D8 수신 셀과 경사를 채우고, prev 와 다른 칸 수를 돌려줍니다."""
    n = zt.shape[0]
    n_slot = nbr.shape[1]
    n_changed = 0
    for c in range(n):
        if is_outlet[c]:
            rcv[c] = c
            slope[c] = 0.0
        else:
            zc = zt[c]
            best = np.int64(-1)
            best_s = 0.0
            for s in range(n_slot):
                k = nbr[c, s]
                if k < 0:
                    continue
                zk = zt[k]
                if zk < zc:
                    sl = (zc - zk) / dist[c, s]
                    # 같은 경사면 셀 번호가 작은 이웃 (결정성)
                    if best < 0 or sl > best_s or (sl == best_s and k < best):
                        best = k
                        best_s = sl
            if best < 0:
                # 낮은 이웃이 없는 칸(채우지 않은 고도의 웅덩이 바닥)은 자기 자신을 가리킵니다.
                rcv[c] = c
                slope[c] = 0.0
            else:
                if has_prev:
                    p = prev[c]
                    if p != best and p != c:
                        # 가이드 4장 히스테리시스: 옛 수신 셀이 아직 확실히 낮은 이웃이면,
                        # 새 경사가 (1+η)배보다 클 때만 바꿉니다.
                        for s in range(n_slot):
                            if nbr[c, s] == p:
                                zp = zt[p]
                                if zp < zc:
                                    sp = (zc - zp) / dist[c, s]
                                    if not (best_s > (1.0 + eta) * sp):
                                        best = p
                                        best_s = sp
                                break
                rcv[c] = best
                slope[c] = best_s
        if has_prev and rcv[c] != prev[c]:
            n_changed += 1
    if not has_prev:
        n_changed = n
    return n_changed


@njit(cache=True)
def _donor_csr_kernel(rcv: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """기여 셀 CSR: donors[start[c]:start[c+1]] 가 c 로 물을 보내는 칸(번호 오름차순)."""
    n = rcv.shape[0]
    start = np.zeros(n + 1, dtype=np.int64)
    for c in range(n):
        r = rcv[c]
        if r != c:
            start[r + 1] += 1
    for c in range(n):
        start[c + 1] += start[c]
    donors = np.empty(start[n], dtype=np.int64)
    fill = start[:n].copy()
    for c in range(n):
        r = rcv[c]
        if r != c:
            donors[fill[r]] = c
            fill[r] += 1
    return start, donors


@njit(cache=True)
def _topo_kernel(rcv: np.ndarray, start: np.ndarray, donors: np.ndarray, order: np.ndarray) -> int:
    """출구(자기 자신을 가리키는 칸)를 먼저 모두 적고, 출구마다 기여 셀을 깊이 우선으로 적습니다.

    채운 칸 수를 돌려줍니다. N 보다 작으면 수신 셀에 순환이 있다는 뜻입니다.
    """
    n = rcv.shape[0]
    m = 0
    for c in range(n):
        if rcv[c] == c:
            order[m] = c
            m += 1
    n_root = m
    stack = np.empty(n, dtype=np.int64)
    for q in range(n_root):
        root = order[q]
        sp = 0
        for t in range(start[root + 1] - 1, start[root] - 1, -1):
            stack[sp] = donors[t]
            sp += 1
        while sp > 0:
            sp -= 1
            c = stack[sp]
            order[m] = c
            m += 1
            # 번호가 작은 기여 셀을 먼저 꺼내도록 거꾸로 넣습니다.
            for t in range(start[c + 1] - 1, start[c] - 1, -1):
                stack[sp] = donors[t]
                sp += 1
    return m


# ---------------------------------------------------------------- 공개 함수


def _check_rcv(rcv: np.ndarray) -> np.ndarray:
    rcv = np.ascontiguousarray(rcv)
    if rcv.ndim != 1:
        raise ValueError(f"rcv 는 (N,) 1차원 배열이어야 합니다: 모양 {rcv.shape}")
    if rcv.dtype.kind not in "iu":
        raise ValueError(f"rcv 는 정수 배열이어야 합니다: {rcv.dtype}")
    rcv = rcv.astype(np.int64, copy=False)
    if rcv.size and (rcv.min() < 0 or rcv.max() >= rcv.shape[0]):
        raise ValueError("rcv 에 범위를 벗어난 셀 번호가 있습니다 (0..N-1)")
    return rcv


def d8_receivers(
    zt: np.ndarray,
    nbr: np.ndarray,
    dist: np.ndarray,
    is_outlet: np.ndarray,
    prev: np.ndarray | None = None,
    eta: float = 0.0,
) -> tuple[np.ndarray, np.ndarray, int]:
    """가장 가파른 낮은 이웃을 수신 셀로 고릅니다 (D8, 가이드 2장 ②, 히스테리시스 4장).

    zt: (N,) ε 채움 고도 z̃ [m]. nbr: (N, K) 이웃 셀 번호(없으면 -1). dist: (N, K) 이웃 거리 [m]
    (없으면 inf). is_outlet: (N,) bool. prev: (N,) 지난 반복의 수신 셀 또는 None.
    eta: 히스테리시스 η (설정 landscape.hysteresis_eta).

    - 출구는 자기 자신을 가리키고 경사 0 입니다.
    - 나머지는 z̃_k < z̃_c 인 이웃 가운데 (z̃_c − z̃_k)/d_ck 가 가장 큰 이웃입니다.
      같은 경사면 셀 번호가 작은 이웃을 고릅니다.
    - 낮은 이웃이 없는 칸(채우지 않은 고도를 넣었을 때의 웅덩이 바닥)은 자기 자신, 경사 0 입니다.
    - prev 가 있고 prev[c] 가 아직 확실히 낮은 이웃이면, 새 경사 > (1+η)·옛 경사일 때만 바꿉니다.

    반환: (rcv (N,) int64, slope (N,) float64 [m/m], n_changed int). n_changed 는 rcv != prev 인
    칸 수이고, prev 가 None 이면 N 입니다.
    """
    zt = np.ascontiguousarray(zt, dtype=np.float64)
    if zt.ndim != 1:
        raise ValueError(f"zt 는 (N,) 1차원 배열이어야 합니다: 모양 {zt.shape}")
    n = zt.shape[0]
    nbr = np.ascontiguousarray(nbr)
    if nbr.ndim != 2 or nbr.shape[0] != n or nbr.dtype.kind != "i":
        raise ValueError(f"nbr 는 (N, K) 정수 배열이어야 합니다: {nbr.shape} {nbr.dtype}")
    if nbr.size and (nbr.min() < -1 or nbr.max() >= n):
        raise ValueError("nbr 에 범위를 벗어난 셀 번호가 있습니다 (-1 또는 0..N-1)")
    dist = np.ascontiguousarray(dist, dtype=np.float64)
    if dist.shape != nbr.shape:
        raise ValueError(f"dist 는 nbr 와 모양이 같아야 합니다: {dist.shape} != {nbr.shape}")
    is_outlet = np.ascontiguousarray(is_outlet, dtype=np.bool_)
    if is_outlet.shape != (n,):
        raise ValueError(f"is_outlet 은 (N,) 이어야 합니다: {is_outlet.shape}")
    if not np.isfinite(zt).all():
        raise ValueError("zt 에 NaN 이나 무한대가 있습니다")
    eta = float(eta)
    if not (np.isfinite(eta) and eta >= 0.0):
        raise ValueError(f"eta 는 0 이상이어야 합니다: {eta}")
    if prev is None:
        prev_arr = np.empty(0, dtype=np.int64)
        has_prev = False
    else:
        prev_arr = _check_rcv(prev)
        if prev_arr.shape != (n,):
            raise ValueError(f"prev 는 (N,) 이어야 합니다: {prev_arr.shape}")
        has_prev = True
    rcv = np.empty(n, dtype=np.int64)
    slope = np.empty(n, dtype=np.float64)
    n_changed = _d8_kernel(zt, nbr, dist, is_outlet, prev_arr, has_prev, eta, rcv, slope)
    return rcv, slope, int(n_changed)


def donor_lists(rcv: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """기여 셀 목록 (CSR).

    rcv: (N,) 수신 셀. 반환: (start (N+1,) int64, donors (M,) int64).
    칸 c 의 기여 셀은 donors[start[c]:start[c+1]] 이고 번호 오름차순입니다(자기 자신 제외).
    """
    rcv = _check_rcv(rcv)
    return _donor_csr_kernel(rcv)


def topo_order(rcv: np.ndarray) -> np.ndarray:
    """하류가 먼저 오는 순서 σ: 모든 칸에서 rank(r(c)) < rank(c) (가이드 2장 ③).

    rcv: (N,) 수신 셀 (출구는 자기 자신).
    반환: (N,) int64 셀 번호의 순열. 앞쪽 n_root 개는 자기 자신을 가리키는 칸(출구) 전부를 번호
    오름차순으로 담고, 그 뒤는 출구마다 기여 셀을 깊이 우선(번호 작은 기여 셀 먼저)으로 담습니다.
    거꾸로 돌면 상류부터 처리합니다. 수신 셀에 순환이 있으면 ValueError.
    """
    rcv = _check_rcv(rcv)
    start, donors = _donor_csr_kernel(rcv)
    order = np.empty(rcv.shape[0], dtype=np.int64)
    m = _topo_kernel(rcv, start, donors, order)
    if m != rcv.shape[0]:
        raise ValueError(
            f"수신 셀에 순환이 있어 출구에 닿지 않는 칸이 {rcv.shape[0] - m}개 있습니다"
        )
    return order
