"""큐브스피어 필드 보간: 고스트 줄, 구면 점 샘플, 거친 격자 → L0 옮기기 (가이드 7장).

면 경계 근처에서 쌍선형 보간을 하려면 면 바깥 한 줄(고스트 줄)의 값이 필요합니다.
인접 면의 칸 값을 그대로 복사하면 경계를 따라가는 방향으로 최대 반 칸 가까이 어긋나서
경계 근처 오차가 1차로만 줄어듭니다. 그래서 고스트 칸의 정확한 위치를 인접 면 좌표로 바꾸고,
그 위치가 놓이는 인접 면의 줄(중심선 위에 정확히 놓임)을 따라 1D 선형 보간합니다.
이러면 경계 근처 오차도 면 안쪽처럼 2차로 줄어듭니다.

배열 규칙: 면별 배열은 (6, n, n) 이고 [f, j, i] 순서입니다(j 는 v 방향 b, i 는 u 방향 a).
고스트를 붙인 배열은 (6, n + 2L, n + 2L) 이고, 원래 칸 (j, i) 는 [j + L, i + L] 에 있습니다.
"""

import functools
import math

import numpy as np
from numba import njit, prange

from bpcg.core import cubesphere as cs
from bpcg.core.graph import CellGraph

_EDGE_SIDES = (("i", 1), ("i", -1), ("j", 1), ("j", -1))


def _face_with_normal(e: np.ndarray) -> int:
    """법선이 e 인 면 번호."""
    return int(np.argmax(cs.FACE_N @ e))


def _locate_on_row(
    q: np.ndarray, f: int, f2: int, n: int, L: int, lo_min: int, lo_max: int
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """면 f 의 고스트 위치 q (K,3) 가 놓이는 인접 면 f2 의 줄을 찾아 1D 보간 계수를 줍니다.

    q 는 f 와 f2 의 공유 경계와 나란한 f2 의 줄 중심선 위에 정확히 놓입니다(가이드 7장).
    lo_min, lo_max: 줄을 따라가는 덧붙인 배열 번호의 하한·상한 (보간 구간의 왼쪽 끝).
    반환: 덧붙인 배열 (6, P, P) 를 펼친 번호 idx0, idx1 (K,) 과 idx1 의 가중치 w1 (K,).
    """
    P = n + 2 * L
    qn = q @ cs.FACE_N[f2]
    a2 = 4.0 / np.pi * np.arctan((q @ cs.FACE_U[f2]) / qn)
    b2 = 4.0 / np.pi * np.arctan((q @ cs.FACE_V[f2]) / qn)
    x2 = (a2 + 1.0) * n / 2.0 - 0.5 + L  # 덧붙인 배열의 연속 열 번호
    y2 = (b2 + 1.0) * n / 2.0 - 0.5 + L  # 덧붙인 배열의 연속 행 번호
    # 원래 면의 법선이 인접 면의 u 축이면 고스트 줄은 고정된 열(i) 위에 놓입니다.
    fixed_is_i = abs(float(cs.FACE_N[f] @ cs.FACE_U[f2])) > 0.5
    fixed, moving = (x2, y2) if fixed_is_i else (y2, x2)
    fixed_idx = np.rint(fixed).astype(np.int64)
    # 내부 불변식: 줄 중심선 위에 정확히 놓이고, 그 줄은 f2 의 원래 칸 줄입니다.
    assert np.abs(fixed - fixed_idx).max() < 1e-6
    assert fixed_idx.min() >= L and fixed_idx.max() < L + n
    lo = np.clip(np.floor(moving).astype(np.int64), lo_min, lo_max)
    w1 = moving - lo
    assert w1.min() > -1e-9 and w1.max() < 1.0 + 1e-9
    if fixed_is_i:
        idx0 = f2 * P * P + lo * P + fixed_idx
        idx1 = idx0 + P
    else:
        idx0 = f2 * P * P + fixed_idx * P + lo
        idx1 = idx0 + 1
    return idx0, idx1, w1


@functools.lru_cache(maxsize=32)
def _ghost_stencil(n: int, layers: int) -> tuple[np.ndarray, ...]:
    """고스트 칸을 채우는 보간 계수를 미리 계산합니다. 번호는 모두 덧붙인 배열을 펼친 번호입니다.

    반환
      edge_dst (E,), edge_src (E, 2), edge_w (E, 2): 모서리 고스트 =
        인접 면 원래 칸 두 개의 1D 보간.
      corner_dst (C,), corner_src (C, 4), corner_w (C, 4): 꼭짓점 고스트 = 인접 면 둘에서
        각각 경계까지 1D 보간한 값의 평균 (모서리 고스트를 읽으므로 모서리 다음에 채웁니다).
    """
    L = layers
    P = n + 2 * L
    centers = -1.0 + (2.0 * np.arange(n) + 1.0) / n
    mm, kk = np.meshgrid(np.arange(1, L + 1), np.arange(n), indexing="ij")
    mm = mm.ravel()
    kk = kk.ravel()
    # m 번째 고스트 줄의 바깥 면 좌표 1 + (2m − 1)/n (면 평면을 넘어 연장한 등각 좌표)
    beyond = 1.0 + (2.0 * mm - 1.0) / n
    along = centers[kk]

    e_dst, e_src, e_w = [], [], []
    for f in range(6):
        for axis, sign in _EDGE_SIDES:
            outer = (n - 1 + mm if sign > 0 else -mm) + L
            if axis == "i":
                a, b, gi, gj = sign * beyond, along, outer, kk + L
                e = sign * cs.FACE_U[f]
            else:
                a, b, gi, gj = along, sign * beyond, kk + L, outer
                e = sign * cs.FACE_V[f]
            X = np.tan(np.pi * a / 4.0)
            Y = np.tan(np.pi * b / 4.0)
            q = cs.FACE_N[f] + X[:, None] * cs.FACE_U[f] + Y[:, None] * cs.FACE_V[f]
            # 모서리 고스트는 인접 면 원래 칸만 읽습니다 (a'_{j,m} 은 늘 칸 중심 범위 안).
            idx0, idx1, w1 = _locate_on_row(q, f, _face_with_normal(e), n, L, L, L + n - 2)
            e_dst.append(f * P * P + gj * P + gi)
            e_src.append(np.stack([idx0, idx1], axis=1))
            e_w.append(np.stack([1.0 - w1, w1], axis=1))

    c_dst, c_src, c_w = [], [], []
    for f in range(6):
        for si in (1, -1):
            for sj in (1, -1):
                for mi in range(1, L + 1):
                    for mj in range(1, L + 1):
                        a = si * (1.0 + (2.0 * mi - 1.0) / n)
                        b = sj * (1.0 + (2.0 * mj - 1.0) / n)
                        X = np.tan(np.pi * a / 4.0)
                        Y = np.tan(np.pi * b / 4.0)
                        q = (cs.FACE_N[f] + X * cs.FACE_U[f] + Y * cs.FACE_V[f])[None, :]
                        # 연장한 면 평면의 이 점은 |X|, |Y| 중 큰 쪽 인접 면에 속합니다.
                        # 같으면 두 면의 공유 경계 위이므로 양쪽에서 구해 평균합니다.
                        cand = []
                        if mi >= mj:
                            cand.append(_face_with_normal(si * cs.FACE_U[f]))
                        if mj >= mi:
                            cand.append(_face_with_normal(sj * cs.FACE_V[f]))
                        src, w = [], []
                        for f2 in cand:
                            idx0, idx1, w1 = _locate_on_row(q, f, f2, n, L, L - 1, L + n - 1)
                            src += [idx0[0], idx1[0]]
                            w += [(1.0 - w1[0]) / len(cand), w1[0] / len(cand)]
                        if len(cand) == 1:
                            src += [src[0], src[0]]
                            w += [0.0, 0.0]
                        gi = (n - 1 + mi if si > 0 else -mi) + L
                        gj = (n - 1 + mj if sj > 0 else -mj) + L
                        c_dst.append(f * P * P + gj * P + gi)
                        c_src.append(src)
                        c_w.append(w)

    result = (
        np.concatenate(e_dst),
        np.concatenate(e_src),
        np.concatenate(e_w),
        np.asarray(c_dst, dtype=np.int64),
        np.asarray(c_src, dtype=np.int64),
        np.asarray(c_w, dtype=np.float64),
    )
    for arr in result:
        arr.setflags(write=False)
    return result


def add_ghost_layers(faces: np.ndarray, layers: int = 1) -> np.ndarray:
    """면별 필드에 인접 면에서 1D 보간한 고스트 줄을 덧붙입니다 (가이드 7장).

    faces: (6, n, n) 실수 필드 [f, j, i]. layers: 덧붙일 줄 수 L (1 ≤ L, 2L ≤ n).
    m 번째 고스트 줄의 칸 j 는 인접 면 m 번째 줄 중심선 위, 경계 방향 좌표
    a'_{j,m} = 4/π·atan(tan(π b_j/4) / tan(π/4·(1 + (2m−1)/n))) 에서 선형 보간한 값입니다.
    꼭짓점 칸(두 축 모두 면 밖)의 위치는 두 인접 면의 공유 경계 위(또는 한쪽 면 안)에 있고,
    그 인접 면 줄을 따라 원래 칸과 고스트 칸 사이를 1D 보간합니다. 공유 경계 위면 두 인접 면에서
    구한 값의 평균입니다(명세의 '인접 고스트 두 개의 평균'을 2차 정확도로 바꾼 것).
    반환: (6, n + 2L, n + 2L) float64. NaN 이 있으면 그 칸을 쓰는 고스트도 NaN 입니다.
    """
    arr = np.asarray(faces)
    if arr.ndim != 3 or arr.shape[0] != 6 or arr.shape[1] != arr.shape[2]:
        raise ValueError(f"faces 는 (6, n, n) 배열이어야 합니다: 받은 모양 {arr.shape}")
    if not (np.issubdtype(arr.dtype, np.floating) or np.issubdtype(arr.dtype, np.integer)):
        raise ValueError(f"faces 는 실수 배열이어야 합니다: {arr.dtype}")
    n = arr.shape[1]
    if isinstance(layers, bool) or not isinstance(layers, int | np.integer) or layers < 1:
        raise ValueError(f"layers 는 1 이상의 정수여야 합니다: {layers!r}")
    layers = int(layers)
    if 2 * layers > n:
        # 고스트 줄 위치 1 + (2L−1)/n 이 2 (면 평면의 무한대)에 닿지 않아야 합니다.
        raise ValueError(f"layers 는 n/2 이하여야 합니다: layers={layers}, n={n}")
    L = layers
    out = np.empty((6, n + 2 * L, n + 2 * L), dtype=np.float64)
    out[:, L : L + n, L : L + n] = arr
    flat = out.reshape(-1)
    edge_dst, edge_src, edge_w, corner_dst, corner_src, corner_w = _ghost_stencil(n, L)
    flat[edge_dst] = flat[edge_src[:, 0]] * edge_w[:, 0] + flat[edge_src[:, 1]] * edge_w[:, 1]
    flat[corner_dst] = (flat[corner_src] * corner_w).sum(axis=1)
    return out


@njit(cache=True, parallel=True)
def _bilinear_kernel(
    padded: np.ndarray,
    unit: np.ndarray,
    n: int,
    layers: int,
    face_u: np.ndarray,
    face_v: np.ndarray,
    face_n: np.ndarray,
    out: np.ndarray,
) -> None:
    """고스트를 붙인 (6, n+2L, n+2L) 배열에서 점 (M,3) 마다 쌍선형 보간 → out (M,)."""
    size = n + 2 * layers
    four_over_pi = 4.0 / math.pi
    for k in prange(unit.shape[0]):
        px = unit[k, 0]
        py = unit[k, 1]
        pz = unit[k, 2]
        # 역사상 f* = argmax_f p·n_f (같으면 번호가 작은 면, np.argmax 와 같은 규칙)
        f = 0
        best = px * face_n[0, 0] + py * face_n[0, 1] + pz * face_n[0, 2]
        for g in range(1, 6):
            d = px * face_n[g, 0] + py * face_n[g, 1] + pz * face_n[g, 2]
            if d > best:
                best = d
                f = g
        pu = px * face_u[f, 0] + py * face_u[f, 1] + pz * face_u[f, 2]
        pv = px * face_v[f, 0] + py * face_v[f, 1] + pz * face_v[f, 2]
        a = four_over_pi * math.atan(pu / best)
        b = four_over_pi * math.atan(pv / best)
        # 칸 중심 a_i = −1 + (2i+1)/n 이므로 연속 번호 x = (a+1)·n/2 − 0.5, 고스트만큼 밀기
        x = (a + 1.0) * 0.5 * n - 0.5 + layers
        y = (b + 1.0) * 0.5 * n - 0.5 + layers
        i0 = int(math.floor(x))
        j0 = int(math.floor(y))
        if i0 < 0:
            i0 = 0
        elif i0 > size - 2:
            i0 = size - 2
        if j0 < 0:
            j0 = 0
        elif j0 > size - 2:
            j0 = size - 2
        tx = x - i0
        ty = y - j0
        v00 = padded[f, j0, i0]
        v01 = padded[f, j0, i0 + 1]
        v10 = padded[f, j0 + 1, i0]
        v11 = padded[f, j0 + 1, i0 + 1]
        top = v00 + tx * (v01 - v00)
        bot = v10 + tx * (v11 - v10)
        out[k] = top + ty * (bot - top)


def _check_unit(unit: np.ndarray) -> np.ndarray:
    pts = np.ascontiguousarray(unit, dtype=np.float64)
    if pts.ndim != 2 or pts.shape[1] != 3:
        raise ValueError(f"unit 은 (M, 3) 배열이어야 합니다: 받은 모양 {pts.shape}")
    if not np.isfinite(pts).all():
        raise ValueError("unit 에 NaN 이나 inf 가 있습니다")
    if pts.shape[0] and not (np.abs(pts).max(axis=1) > 0).all():
        raise ValueError("unit 에 길이 0 인 벡터가 있습니다")
    return pts


def sample_sphere(
    field: np.ndarray, n: int, unit: np.ndarray, method: str = "linear"
) -> np.ndarray:
    """큐브스피어 필드를 구면 위 임의의 점에서 읽습니다.

    field: (6n²,) 셀 값 (셀 번호 c = f·n² + j·n + i). n: 면 한 변의 칸 수.
    unit: (M, 3) 방향 벡터 (길이는 상관없음, 0 은 안 됨).
    method: "linear" 는 역사상 → 면 좌표 → 고스트 줄(가이드 7장)을 붙인 쌍선형 보간 (float64 반환),
            "nearest" 는 field[cell_of(unit)] (field 의 dtype 그대로, 범주 값용).
    반환: (M,) 배열.
    """
    if isinstance(n, bool) or not isinstance(n, int | np.integer) or n < 1:
        raise ValueError(f"n 은 1 이상의 정수여야 합니다: {n!r}")
    n = int(n)
    vals = np.asarray(field)
    if vals.shape != (6 * n * n,):
        raise ValueError(f"field 모양이 (6n²,) = ({6 * n * n},) 이 아닙니다: {vals.shape}")
    pts = _check_unit(unit)
    if method == "nearest":
        return vals[cs.cell_of(pts, n)]
    if method != "linear":
        raise ValueError(f"method 는 'linear' 또는 'nearest' 여야 합니다: {method!r}")
    if not np.issubdtype(vals.dtype, np.floating):
        raise ValueError(
            f"linear 보간은 실수 필드에만 씁니다 (범주 값은 method='nearest'): {vals.dtype}"
        )
    if n < 2:
        raise ValueError("linear 보간에는 n ≥ 2 가 필요합니다")
    padded = add_ghost_layers(vals.reshape(6, n, n), layers=1)
    out = np.empty(pts.shape[0], dtype=np.float64)
    _bilinear_kernel(padded, pts, n, 1, cs.FACE_U, cs.FACE_V, cs.FACE_N, out)
    return out


def resample_sphere(
    field: np.ndarray, n_src: int, graph_dst: CellGraph, method: str = "linear"
) -> np.ndarray:
    """거친 큐브스피어 필드 (6·n_src²,) 를 다른 구면 그래프(L0)의 칸 대표점으로 옮깁니다.

    매끄러운 값(융기, 강수 등)은 method="linear", 범주 값(판 번호 등)은 "nearest" 를 씁니다.
    노드 흔들기를 한 그래프도 그 대표점(graph_dst.unit())에서 읽습니다.
    반환: (graph_dst.n_cells,) 배열 (linear 는 float64, nearest 는 field 의 dtype).
    """
    if not isinstance(graph_dst, CellGraph) or graph_dst.kind != "sphere":
        raise ValueError("graph_dst 는 구면 CellGraph (kind='sphere') 여야 합니다")
    return sample_sphere(field, n_src, graph_dst.unit(), method)
