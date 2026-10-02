"""유역과 강 구간 (docs/pipeline.md 6장).

- `outlet_of`: 칸마다 물이 결국 빠져나가는 출구 칸 (유역 번호로 씁니다).
- `river_segments`: 강 칸을 하류 방향으로 이은 구간들. 합류점과 출구에서 끊습니다.
"""

import numpy as np
from numba import njit

from bpcg.hydro.routing import _check_rcv


@njit(cache=True)
def _outlet_kernel(rcv: np.ndarray, order: np.ndarray) -> np.ndarray:
    n = rcv.shape[0]
    out = np.empty(n, dtype=np.int64)
    for q in range(order.shape[0]):
        c = order[q]
        r = rcv[c]
        out[c] = c if r == c else out[r]
    return out


@njit(cache=True)
def _segments_kernel(
    rcv: np.ndarray, order: np.ndarray, is_river: np.ndarray
) -> tuple[np.ndarray, np.ndarray]:
    """강 구간을 평평한 배열(cells)과 구간 시작 위치(offsets, 길이 구간 수 + 1)로 돌려줍니다."""
    n = rcv.shape[0]
    n_rdon = np.zeros(n, dtype=np.int64)  # 강 기여 셀 수
    n_river = 0
    for c in range(n):
        if is_river[c]:
            n_river += 1
            r = rcv[c]
            if r != c:
                n_rdon[r] += 1
    cells = np.empty(n_river, dtype=np.int64)
    offsets = np.empty(n_river + 1, dtype=np.int64)
    m = 0
    n_seg = 0
    # 구간 머리 = 강 기여 셀 수가 1 이 아닌 강 칸 (0 이면 발원점, 2 이상이면 합류점).
    # 머리는 하류부터(order 순서) 적습니다.
    for q in range(order.shape[0]):
        h = order[q]
        if not is_river[h] or n_rdon[h] == 1:
            continue
        offsets[n_seg] = m
        n_seg += 1
        c = h
        while True:
            cells[m] = c
            m += 1
            r = rcv[c]
            if r == c or not is_river[r] or n_rdon[r] >= 2:
                break
            c = r
    offsets[n_seg] = m
    return cells[:m], offsets[: n_seg + 1]


def outlet_of(rcv: np.ndarray, order: np.ndarray) -> np.ndarray:
    """칸마다 흘러 나가는 출구 칸 번호 (유역 번호).

    rcv: (N,) 수신 셀. order: (N,) topo_order 결과(하류부터).
    반환: (N,) int64. 출구 칸은 자기 자신입니다.
    """
    rcv = _check_rcv(rcv)
    order = _check_order(order, rcv.shape[0])
    return _outlet_kernel(rcv, order)


def river_segments(rcv: np.ndarray, order: np.ndarray, is_river: np.ndarray) -> list[np.ndarray]:
    """강 칸을 하류 방향으로 이은 구간 목록.

    rcv: (N,) 수신 셀. order: (N,) topo_order 결과. is_river: (N,) bool 강 칸.
    반환: int64 배열의 list. 각 배열은 상류 → 하류 칸 번호이고 이웃한 두 칸은 rcv 로 이어집니다
    (seg[i+1] == rcv[seg[i]]).

    - 구간은 발원점(강 기여 셀 0개) 또는 합류점(강 기여 셀 2개 이상)에서 시작합니다.
      합류점 칸은 하류 구간의 첫 칸이고, 위 구간들에는 들어가지 않습니다.
    - 출구(자기 자신을 가리킴), 강이 아닌 수신 셀, 합류점 바로 앞에서 끝납니다.
    - 모든 강 칸은 정확히 한 구간에 한 번 들어갑니다.
    - 구간 순서는 머리 칸이 order 에 나오는 순서(하류 쪽 구간 먼저)입니다.
    """
    rcv = _check_rcv(rcv)
    n = rcv.shape[0]
    order = _check_order(order, n)
    is_river = np.ascontiguousarray(is_river, dtype=np.bool_)
    if is_river.shape != (n,):
        raise ValueError(f"is_river 는 (N,) 이어야 합니다: {is_river.shape}")
    cells, offsets = _segments_kernel(rcv, order, is_river)
    if cells.shape[0] != int(is_river.sum()):
        raise ValueError(
            "강 칸 일부가 구간에 들어가지 않았습니다 (rcv 에 순환이 있는지 확인하세요)"
        )
    if offsets.shape[0] == 1:
        return []
    return np.split(cells, offsets[1:-1])


def _check_order(order: np.ndarray, n: int) -> np.ndarray:
    order = np.ascontiguousarray(order)
    if order.shape != (n,) or order.dtype.kind not in "iu":
        raise ValueError(f"order 는 (N,) 정수 배열이어야 합니다: {order.shape} {order.dtype}")
    order = order.astype(np.int64, copy=False)
    if n and (order.min() < 0 or order.max() >= n):
        raise ValueError("order 에 범위를 벗어난 셀 번호가 있습니다 (0..N-1)")
    return order
