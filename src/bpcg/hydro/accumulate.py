"""유량 누적: Q_c = w_c + Σ_{c'∈D(c)} Q_c' (가이드 2장 ④).

순서 σ 를 거꾸로(상류부터) 한 번 훑으면 끝납니다. 가중치가 칸 면적이면 상류 면적 A↑ [m²],
면적 × 유출이면 유량 Q [m³/yr] 입니다.
"""

import numpy as np
from numba import njit

from bpcg.hydro.routing import _check_rcv


@njit(cache=True)
def _accumulate_kernel(rcv: np.ndarray, order: np.ndarray, acc: np.ndarray) -> None:
    """acc 에 가중치를 넣어 두고 부르면 그 자리에서 누적합니다."""
    for q in range(order.shape[0] - 1, -1, -1):
        c = order[q]
        r = rcv[c]
        if r != c:
            acc[r] += acc[c]


@njit(cache=True)
def _donors_count_kernel(rcv: np.ndarray) -> np.ndarray:
    n = rcv.shape[0]
    cnt = np.zeros(n, dtype=np.int32)
    for c in range(n):
        r = rcv[c]
        if r != c:
            cnt[r] += 1
    return cnt


def accumulate(rcv: np.ndarray, order: np.ndarray, weight: np.ndarray | float) -> np.ndarray:
    """상류부터 가중치를 모읍니다: acc_c = weight_c + Σ_{기여 셀} acc (가이드 2장 ④).

    rcv: (N,) 수신 셀. order: (N,) topo_order 결과(하류부터). weight: (N,) 또는 스칼라
    (예: 칸 면적 [m²], 면적 × 유출 [m³/yr]).
    반환: (N,) float64. 질량 보존: Σ_출구 acc = Σ weight (반올림 오차 안).
    """
    rcv = _check_rcv(rcv)
    n = rcv.shape[0]
    order = np.ascontiguousarray(order)
    if order.shape != (n,) or order.dtype.kind not in "iu":
        raise ValueError(f"order 는 (N,) 정수 배열이어야 합니다: {order.shape} {order.dtype}")
    order = order.astype(np.int64, copy=False)
    if n and (order.min() < 0 or order.max() >= n):
        raise ValueError("order 에 범위를 벗어난 셀 번호가 있습니다 (0..N-1)")
    w = np.asarray(weight, dtype=np.float64)
    if w.ndim == 0:
        acc = np.full(n, float(w))
    elif w.shape == (n,):
        acc = w.copy()
    else:
        raise ValueError(f"weight 는 (N,) 또는 스칼라여야 합니다: {w.shape}")
    _accumulate_kernel(rcv, order, acc)
    return acc


def donors_count(rcv: np.ndarray) -> np.ndarray:
    """칸마다 물을 보내 오는 기여 셀 수 (자기 자신 제외).

    rcv: (N,) 수신 셀. 반환: (N,) int32.
    """
    rcv = _check_rcv(rcv)
    return _donors_count_kernel(rcv)
