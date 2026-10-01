"""고도 분포와 면적 비율 지표 (docs/pipeline.md 12장, 설계도 7장 '평가 체계').

- `hypsometry`: 면적 가중 고도 히스토그램(고도 분포 곡선).
- `bimodality`: 고도 분포의 두 봉우리(대륙·해양)와 그 간격. '반쯤 입력'(forced) 지표입니다.
- `hypsometry_distance`: 두 행성의 고도 누적 분포 차이(KS). 가이드 1장 회전 테스트용입니다.
- `ocean_fraction`, `shelf_area`: 물 부피 보존으로 나온 바다 비율과 대륙붕 넓이(창발 지표).
- `flat_fraction`, `gw_surface_fraction`: 평탄지 비율, 지하수가 땅 겉에 닿는 육지 비율(창발 지표).

모든 함수는 셀마다 하나인 (N,) 배열과 칸 면적 [m²] 을 받고, 파일을 읽거나 쓰지 않습니다.
비율은 면적 가중입니다. 칸 면적이 고르지 않은 큐브스피어에서도 '땅 넓이의 몇 %' 로 읽힙니다.
"""

import numpy as np
from scipy.ndimage import gaussian_filter1d
from scipy.signal import find_peaks

# 고도 분포 봉우리 찾기의 기본값 (우리가 정한 값, 설정에 없음).
BIMODAL_BIN_M = 100.0  # 히스토그램 칸 폭 [m]
BIMODAL_SMOOTH_M = 250.0  # 가우스 평활 σ [m]. 칸 몇 개짜리 잡음 봉우리를 지웁니다
BIMODAL_MIN_SEPARATION_M = 1000.0  # pipeline.md 12장: 봉우리 두 개가 1 km 넘게 떨어짐
BIMODAL_MIN_PROMINENCE = 0.05  # 봉우리로 셀 돌출도 (가장 높은 밀도 대비)


# ---------------------------------------------------------------- 입력 검사
def _vec(x, name: str, n: int | None = None, dtype=np.float64) -> np.ndarray:
    a = np.asarray(x)
    if a.ndim != 1:
        raise ValueError(f"{name} 은 (N,) 1차원 배열이어야 합니다: 모양 {a.shape}")
    if n is not None and a.shape[0] != n:
        raise ValueError(f"{name} 의 길이 {a.shape[0]} 가 {n} 와 다릅니다")
    if dtype is np.bool_:
        if a.dtype != np.bool_:
            raise ValueError(f"{name} 은 bool 배열이어야 합니다: {a.dtype}")
        return a
    return np.asarray(a, dtype=dtype)


def _area(area, n: int) -> np.ndarray:
    if area is None:
        return np.ones(n)
    a = _vec(area, "area", n)
    if not (np.isfinite(a).all() and (a >= 0.0).all()):
        raise ValueError("area 는 0 이상의 유한한 값이어야 합니다")
    return a


def _weighted_fraction(hit: np.ndarray, use: np.ndarray, area: np.ndarray) -> float:
    w = area[use]
    total = w.sum()
    if not total > 0.0:
        return float("nan")
    return float(w[hit[use]].sum() / total)


# ---------------------------------------------------------------- 고도 분포
def hypsometry(
    z: np.ndarray, area: np.ndarray, bins: int | np.ndarray = 100
) -> tuple[np.ndarray, np.ndarray]:
    """면적 가중 고도 히스토그램 (고도 분포 곡선).

    z: (N,) 고도 [m] (NaN 은 뺌). area: (N,) 칸 면적 [m²]. bins: 칸 수(int, 범위는 z 의 최솟값~
    최댓값) 또는 (B+1,) 칸 경계 [m] (오름차순).
    반환: (edges (B+1,) [m], frac (B,) 면적 비율). frac 의 합은 범위 안 칸들의 면적 비율로, 정수
    bins 이면 1 입니다.
    """
    z = _vec(z, "z")
    a = _area(area, z.shape[0])
    ok = np.isfinite(z)
    if not ok.any():
        raise ValueError("z 에 유한한 값이 하나도 없습니다")
    total = a[ok].sum()
    if not total > 0.0:
        raise ValueError("유한한 z 칸의 면적 합이 0 입니다")
    if np.ndim(bins) == 0:
        nb = int(bins)
        if nb < 1:
            raise ValueError(f"bins 는 1 이상이어야 합니다: {bins}")
        lo, hi = float(z[ok].min()), float(z[ok].max())
        if hi <= lo:
            hi = lo + 1.0
        edges = np.linspace(lo, hi, nb + 1)
    else:
        edges = np.asarray(bins, dtype=np.float64)
        if edges.ndim != 1 or edges.shape[0] < 2 or not (np.diff(edges) > 0).all():
            raise ValueError("bins 배열은 길이 2 이상의 오름차순 칸 경계여야 합니다")
    h, edges = np.histogram(z[ok], bins=edges, weights=a[ok])
    return edges, h / total


def bimodality(
    z: np.ndarray,
    area: np.ndarray,
    bin_m: float = BIMODAL_BIN_M,
    smooth_m: float = BIMODAL_SMOOTH_M,
    min_separation_m: float = BIMODAL_MIN_SEPARATION_M,
    min_prominence: float = BIMODAL_MIN_PROMINENCE,
) -> dict:
    """고도 분포의 두 봉우리를 찾습니다 (pipeline.md 12장 '고도 분포 봉우리 2개').

    z: (N,) 고도 [m]. area: (N,) 칸 면적 [m²]. bin_m: 히스토그램 칸 폭 [m]. smooth_m: 가우스 평활
    σ [m]. min_separation_m: 합격 간격 [m]. min_prominence: 봉우리 돌출도 문턱(최고 밀도 대비).

    면적 가중 히스토그램을 평활한 뒤 돌출도가 큰 봉우리 둘을 고릅니다.
    반환 dict:
    - peaks: 봉우리 고도 [m] 목록 (낮은 것부터, 최대 2개)
    - separation_m: 두 봉우리 간격 [m] (봉우리가 하나 이하면 0)
    - ok: 봉우리가 2개이고 간격 > min_separation_m
    - n_peaks: 문턱을 넘은 봉우리 수
    """
    for name, v in (("bin_m", bin_m), ("smooth_m", smooth_m)):
        if not (np.isfinite(v) and v > 0.0):
            raise ValueError(f"{name} 는 0 보다 커야 합니다: {v}")
    if not (0.0 <= min_prominence < 1.0):
        raise ValueError(f"min_prominence 는 [0, 1) 이어야 합니다: {min_prominence}")
    z = _vec(z, "z")
    ok = np.isfinite(z)
    if not ok.any():
        raise ValueError("z 에 유한한 값이 하나도 없습니다")
    pad = 4.0 * smooth_m
    lo = np.floor((z[ok].min() - pad) / bin_m) * bin_m
    hi = np.ceil((z[ok].max() + pad) / bin_m) * bin_m
    nb = max(int(round((hi - lo) / bin_m)), 1)
    edges = lo + bin_m * np.arange(nb + 1)
    _, frac = hypsometry(z, area, edges)
    dens = gaussian_filter1d(frac, smooth_m / bin_m, mode="constant")
    top = dens.max()
    if not top > 0.0:
        return {"peaks": [], "separation_m": 0.0, "ok": False, "n_peaks": 0}
    # 가장자리 봉우리도 잡도록 양 끝에 0 을 붙입니다.
    padded = np.concatenate([[0.0], dens, [0.0]])
    idx, props = find_peaks(padded, prominence=min_prominence * top)
    idx = idx - 1
    centers = 0.5 * (edges[:-1] + edges[1:])
    keep = np.argsort(-props["prominences"], kind="stable")[:2]
    peaks = sorted(float(centers[idx[k]]) for k in keep)
    sep = peaks[-1] - peaks[0] if len(peaks) == 2 else 0.0
    return {
        "peaks": peaks,
        "separation_m": float(sep),
        "ok": bool(len(peaks) == 2 and sep > min_separation_m),
        "n_peaks": int(idx.shape[0]),
    }


def hypsometry_distance(
    z_a: np.ndarray, area_a: np.ndarray, z_b: np.ndarray, area_b: np.ndarray
) -> float:
    """두 고도 분포의 면적 가중 누적 분포 최대 차이 (KS 거리, 0~1).

    z_a, area_a: (N_a,) 고도 [m]·칸 면적 [m²]. z_b, area_b: (N_b,). 가이드 1장 회전 테스트에서
    두 행성의 고도 분포 곡선 차이로 씁니다. NaN 고도는 뺍니다.
    """

    def cdf_parts(z, area):
        z = _vec(z, "z")
        a = _area(area, z.shape[0])
        ok = np.isfinite(z)
        if not ok.any() or not a[ok].sum() > 0.0:
            raise ValueError("유한한 고도 칸이 없거나 면적 합이 0 입니다")
        k = np.argsort(z[ok], kind="stable")
        return z[ok][k], np.cumsum(a[ok][k]) / a[ok].sum()

    za, ca = cdf_parts(z_a, area_a)
    zb, cb = cdf_parts(z_b, area_b)
    grid = np.union1d(za, zb)
    fa = np.concatenate([[0.0], ca])[np.searchsorted(za, grid, side="right")]
    fb = np.concatenate([[0.0], cb])[np.searchsorted(zb, grid, side="right")]
    return float(np.abs(fa - fb).max())


# ---------------------------------------------------------------- 바다·대륙붕
def ocean_fraction(is_ocean: np.ndarray, area: np.ndarray) -> float:
    """바다 칸의 면적 비율 (pipeline.md 12장, 지구 0.708).

    is_ocean: (N,) bool. area: (N,) 칸 면적 [m²]. 반환: 0~1.
    """
    m = _vec(is_ocean, "is_ocean", dtype=np.bool_)
    a = _area(area, m.shape[0])
    return _weighted_fraction(m, np.ones(m.shape[0], dtype=np.bool_), a)


def shelf_area(
    z: np.ndarray,
    area: np.ndarray,
    crust_type: np.ndarray,
    is_ocean: np.ndarray,
    depth: float = 200.0,
) -> tuple[float, float]:
    """대륙붕 넓이: 대륙 지각(crust_type 1) 위의 바다 칸 가운데 수심 < depth 인 칸.

    z: (N,) 고도 [m] (해수면 0, 바다는 음수). area: (N,) [m²]. crust_type: (N,) 0 해양·1 대륙.
    is_ocean: (N,) bool. depth: 수심 문턱 [m] (pipeline.md 12장 200 m).
    반환: (넓이 [m²], 바다 넓이 대비 비율). 바다가 없으면 비율은 NaN.
    지각평형 흉내로 나온 값이고, 바다로 나간 퇴적물이 쌓이는 과정이 없다는 단서가 붙습니다.
    """
    if not (np.isfinite(depth) and depth > 0.0):
        raise ValueError(f"depth 는 0 보다 커야 합니다: {depth}")
    z = _vec(z, "z")
    n = z.shape[0]
    a = _area(area, n)
    ct = _vec(crust_type, "crust_type", n, dtype=np.int64)
    m = _vec(is_ocean, "is_ocean", n, dtype=np.bool_)
    shelf = m & (ct == 1) & (z > -depth)
    s = float(a[shelf].sum())
    ocean = float(a[m].sum())
    return s, (s / ocean if ocean > 0.0 else float("nan"))


# ---------------------------------------------------------------- 육지 비율
def flat_fraction(
    slope: np.ndarray,
    land_mask: np.ndarray,
    threshold: float = 0.02,
    area: np.ndarray | None = None,
) -> float:
    """평탄지 비율: 육지 가운데 경사 < threshold 인 넓이 비율 (pipeline.md 12장, L2 경사 < 0.02).

    slope: (N,) 경사 [m/m]. land_mask: (N,) bool. threshold: 문턱 [m/m]. area: (N,) [m²] 또는
    None(칸 수 비율). NaN 경사 칸은 뺍니다. 육지가 없으면 NaN.
    """
    s = _vec(slope, "slope")
    n = s.shape[0]
    m = _vec(land_mask, "land_mask", n, dtype=np.bool_)
    a = _area(area, n)
    use = m & np.isfinite(s)
    return _weighted_fraction(np.abs(s) < threshold, use, a)


def gw_surface_fraction(
    z: np.ndarray,
    z_gw: np.ndarray,
    land_mask: np.ndarray,
    tol: float = 0.5,
    area: np.ndarray | None = None,
) -> float:
    """지하수가 땅 겉 tol 안에 닿는 육지 비율 (pipeline.md 8.3 진단, 12장. 지구 22~32%).

    z: (N,) 지표 고도 [m]. z_gw: (N,) 지하수면 [m]. land_mask: (N,) bool. tol: 깊이 문턱 [m].
    area: (N,) [m²] 또는 None. z − z_gw ≤ tol 인 칸의 비율이고, NaN 칸은 뺍니다. 육지가 없으면 NaN.
    """
    if not (np.isfinite(tol) and tol >= 0.0):
        raise ValueError(f"tol 은 0 이상이어야 합니다: {tol}")
    z = _vec(z, "z")
    n = z.shape[0]
    g = _vec(z_gw, "z_gw", n)
    m = _vec(land_mask, "land_mask", n, dtype=np.bool_)
    a = _area(area, n)
    use = m & np.isfinite(z) & np.isfinite(g)
    return _weighted_fraction((z - g) <= tol, use, a)
