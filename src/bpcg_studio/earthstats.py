"""진짜 지구 자료와 생성 결과를 같은 잣대로 재는 계산 (스튜디오 '진짜 지구와 비교' 탭).

지구 기준값을 만드는 `earthref.py` 와 실행 결과를 재는 `earth.py` 가 이 파일의 함수와 구간을 함께
씁니다. 그래서 "지구 쪽은 이 방법, 생성 쪽은 저 방법"으로 재는 일이 없습니다. 이 파일은 numpy 와
scipy 만 씁니다. 지구 자료를 읽는 rasterio·geopandas 는 `earthref.py` 안에서만 부릅니다.

닮은 정도(0~1)는 세 가지 규칙으로 셉니다. 지표마다 어느 규칙인지 화면에 적습니다.

- 겹침 (overlap, 분포): 두 분포를 같은 구간으로 나눠 넓이를 1로 맞춘 뒤 겹치는 넓이.
  0.8 이면 넓이의 80%가 겹칩니다.
- 배수 (ratio, 숫자 하나·곡선): 2^(−|log2(생성 ÷ 지구)|). 같으면 1, 두 배 어긋나면 0.5,
  네 배면 0.25 입니다.
- 가운데 (center, 표본이 적은 값): 생성 값이 지구 값들 가운데 몇 번째쯤인지(백분위 q)로
  1 − 2·|q − 0.5|. 지구 값의 가운데값이면 1, 맨 끝이면 0 입니다.
"""

from __future__ import annotations

import math
from collections.abc import Sequence

import numpy as np

# ---------------------------------------------------------------- 구간 (지구·생성 공통)
LAT_LIMIT_DEG = 60.0  # 극지방 얼음판(남극·그린란드)을 빼려고 두 쪽 모두 남위 60°~북위 60° 땅만 씀
ELEV_EDGES = np.arange(-11_000.0, 9_001.0, 100.0)  # 해수면 기준 높이 [m]
LOG_SLOPE_EDGES = np.round(np.arange(-6.0, 0.0001, 0.1), 6)  # log10(경사 m/m)
RELIEF_EDGES = np.arange(0.0, 6_001.0, 50.0)  # 칸 안 기복 [m]
LOG_P_EDGES = np.round(np.arange(-2.5, 1.3001, 0.05), 6)  # log10(강수 m/yr)
PET_EDGES = np.round(np.arange(0.0, 3.0001, 0.05), 6)  # 잠재 증발산 [m/yr]
AI_CLASS_EDGES = (0.0, 0.03, 0.2, 0.5, 0.65, math.inf)  # UNEP 건조 지수 구분
AI_CLASS_NAMES = ("극건조", "건조", "반건조", "건조 반습윤", "습윤")
LAT_BAND_EDGES = np.arange(-60.0, 60.0001, 5.0)  # 위도 띠 [°]
LOG_AREA_EDGES = np.round(np.arange(2.0, 7.0001, 0.25), 6)  # log10(상류 넓이 km²)
HERO_SPACING_M = 100.0  # 히어로 크기 지표를 재는 칸 간격 (GLO-90 은 약 90 m)
HERO_SLOPE_EDGES = np.arange(0.0, 71.0, 1.0)  # 경사 [°]
RELIEF_1KM_EDGES = np.arange(0.0, 1_001.0, 10.0)  # 1 km 네모 안 기복 [m]
RELIEF_5KM_EDGES = np.arange(0.0, 3_001.0, 25.0)  # 5 km 네모 안 기복 [m]
LOG_E_EDGES = np.round(np.arange(-0.5, 4.0001, 0.25), 6)  # log10(침식률 mm/kyr)
BASIN_SLOPE_EDGES = np.round(np.arange(0.0, 1.0001, 0.025), 6)  # 유역 평균 경사 [m/m]
SUB_BASIN_KM2 = (10.0, 300.0)  # 히어로 안 작은 유역: 넓이 10~300 km² 가운데 가장 큰 것들
STRAHLER_MIN_CELLS = 20  # 강 갈래를 셀 때 상류 넓이 문턱 = L0 칸 넓이 × 이 수


# ---------------------------------------------------------------- 분포
def hist(values: np.ndarray, edges: np.ndarray, weights: np.ndarray | None = None) -> np.ndarray:
    """넓이(또는 개수)로 무게를 준 분포. 합이 1 인 몫 (구간 밖 값은 양 끝 구간에 넣음).

    유한한 값만 셉니다. 셀 값이 없으면 모두 0 입니다.
    """
    v = np.asarray(values, dtype=np.float64).ravel()
    w = np.ones_like(v) if weights is None else np.asarray(weights, dtype=np.float64).ravel()
    ok = np.isfinite(v) & np.isfinite(w) & (w > 0)
    if not ok.any():
        return np.zeros(len(edges) - 1)
    lo, hi = float(edges[0]), float(edges[-1])
    vv = np.clip(v[ok], lo, np.nextafter(hi, lo))
    h, _ = np.histogram(vv, bins=edges, weights=w[ok])
    s = h.sum()
    return h / s if s > 0 else h


def overlap(p: Sequence[float], q: Sequence[float]) -> float | None:
    """두 분포(합이 1)가 겹치는 넓이 0~1. 한쪽이 비었으면 None."""
    a = np.asarray(p, dtype=np.float64)
    b = np.asarray(q, dtype=np.float64)
    if a.sum() <= 0 or b.sum() <= 0:
        return None
    return float(np.minimum(a / a.sum(), b / b.sum()).sum())


def hist_quantiles(edges: Sequence[float], p: Sequence[float], qs=(0.1, 0.5, 0.9)) -> list:
    """분포(합이 1)에서 구간 안을 곧게 이어 읽은 백분위 값. 비었으면 None 목록."""
    e = np.asarray(edges, dtype=np.float64)
    f = np.asarray(p, dtype=np.float64)
    if f.sum() <= 0:
        return [None] * len(qs)
    c = np.concatenate([[0.0], np.cumsum(f / f.sum())])
    return [float(np.interp(q, c, e)) for q in qs]


def hist_percentile(edges: Sequence[float], p: Sequence[float], value: float) -> float | None:
    """value 가 분포에서 몇 번째쯤인지 (0~1). 분포가 비었거나 값이 없으면 None."""
    e = np.asarray(edges, dtype=np.float64)
    f = np.asarray(p, dtype=np.float64)
    if f.sum() <= 0 or value is None or not math.isfinite(value):
        return None
    c = np.concatenate([[0.0], np.cumsum(f / f.sum())])
    return float(np.interp(value, e, c))


def wquantiles(values: np.ndarray, weights: np.ndarray | None, qs=(0.1, 0.5, 0.9)) -> list:
    """무게를 준 백분위 값 (유한한 값만). 비었으면 None 목록."""
    v = np.asarray(values, dtype=np.float64).ravel()
    w = np.ones_like(v) if weights is None else np.asarray(weights, dtype=np.float64).ravel()
    ok = np.isfinite(v) & np.isfinite(w) & (w > 0)
    if not ok.any():
        return [None] * len(qs)
    order = np.argsort(v[ok], kind="stable")
    vs = v[ok][order]
    cw = np.cumsum(w[ok][order])
    cw = (cw - 0.5 * w[ok][order]) / cw[-1]
    return [float(np.interp(q, cw, vs)) for q in qs]


def percentile_of(sample: Sequence[float], value: float) -> float | None:
    """value 가 표본 가운데 몇 번째쯤인지 (0~1, 같은 값은 절반으로 셈). 표본이 없으면 None."""
    s = np.asarray([x for x in sample if x is not None and math.isfinite(x)], dtype=np.float64)
    if s.size == 0 or value is None or not math.isfinite(value):
        return None
    return float(((s < value).sum() + 0.5 * (s == value).sum()) / s.size)


# ---------------------------------------------------------------- 닮은 정도
def ratio_score(gen: float | None, earth: float | None) -> float | None:
    """2^(−|log2(생성 ÷ 지구)|). 같으면 1, 두 배 어긋나면 0.5. 0 이하나 값이 없으면 None."""
    if gen is None or earth is None or not (gen > 0 and earth > 0):
        return None
    return float(2.0 ** (-abs(math.log2(gen / earth))))


def center_score(q: float | None) -> float | None:
    """백분위 q(0~1) → 1 − 2·|q − 0.5|. 가운데값이면 1, 맨 끝이면 0."""
    if q is None or not math.isfinite(q):
        return None
    return float(max(0.0, 1.0 - 2.0 * abs(q - 0.5)))


def curve_score(gen: Sequence[float | None], earth: Sequence[float | None]) -> float | None:
    """두 곡선이 같은 x 에서 몇 배 어긋나는지의 평균으로 낸 배수 규칙 점수.

    두 값이 모두 양수인 구간만 씁니다. 그런 구간이 둘보다 적으면 None.
    """
    d = [
        abs(math.log2(g / e))
        for g, e in zip(gen, earth, strict=True)
        if g is not None and e is not None and g > 0 and e > 0
    ]
    if len(d) < 2:
        return None
    return float(2.0 ** (-float(np.mean(d))))


# ---------------------------------------------------------------- 경사·기복
def graph_steepest_slope(z: np.ndarray, nbr: np.ndarray, dist: np.ndarray) -> np.ndarray:
    """칸마다 이웃 가운데 가장 가파르게 내려가는 쪽의 경사 [m/m] (내려가는 이웃이 없으면 0).

    nbr 는 (N, K) 이웃 번호(-1 = 없음), dist 는 (N, K) 거리 [m] 입니다.
    """
    z = np.asarray(z, dtype=np.float64)
    ok = nbr >= 0
    zn = np.where(ok, z[np.where(ok, nbr, 0)], np.inf)
    with np.errstate(invalid="ignore", divide="ignore"):
        drop = np.where(ok & (dist > 0), (z[:, None] - zn) / dist, 0.0)
    return np.maximum(np.nanmax(drop, axis=1), 0.0)


def latlon_steepest_slope(
    z: np.ndarray, lat_deg: np.ndarray, step_deg: float, radius_m: float
) -> np.ndarray:
    """위경도 격자에서 칸마다 가장 가파르게 내려가는 이웃 쪽 경사 [m/m].

    격자는 북쪽이 0번 행이고 동서로 이어집니다.

    남북 간격은 radius·step, 동서 간격은 거기에 그 행 위도의 cos 를 곱한 값입니다.
    내려가는 이웃이 없으면 0 입니다.
    """
    z = np.asarray(z, dtype=np.float64)
    ny, nx = z.shape
    dy = radius_m * math.radians(step_deg)
    dx = dy * np.cos(np.radians(lat_deg))[:, None] * np.ones((1, nx))
    best = np.zeros_like(z)
    for di in (-1, 0, 1):
        for dj in (-1, 0, 1):
            if di == 0 and dj == 0:
                continue
            zn = np.full_like(z, np.inf)
            src = np.roll(z, -dj, axis=1)
            if di == -1:
                zn[1:] = src[:-1]
            elif di == 1:
                zn[:-1] = src[1:]
            else:
                zn = src
            d = np.sqrt((dy * di) ** 2 + (dx * dj) ** 2)
            with np.errstate(invalid="ignore"):
                s = (z - zn) / d
            best = np.fmax(best, np.where(np.isfinite(s), s, 0.0))
    return best


def gradient_slope(z: np.ndarray, dx: float, dy: float) -> np.ndarray:
    """격자 높이의 기울기 크기 [m/m] (가운데 차분, 이웃이 NaN 이면 NaN)."""
    gy, gx = np.gradient(np.asarray(z, dtype=np.float64), dy, dx)
    return np.hypot(gx, gy)


def block_mean(z: np.ndarray, f: int) -> np.ndarray:
    """f × f 칸을 평균한 격자 (남는 줄은 버림). 묶음 안에 NaN 이 하나라도 있으면 NaN."""
    z = np.asarray(z, dtype=np.float64)
    if f <= 1:
        return z.copy()
    ny, nx = (z.shape[0] // f) * f, (z.shape[1] // f) * f
    return z[:ny, :nx].reshape(ny // f, f, nx // f, f).mean(axis=(1, 3))


def window_relief(z: np.ndarray, size: tuple[int, int]) -> np.ndarray:
    """칸마다 둘레 size(행, 열) 네모 안 가장 높은 곳 − 가장 낮은 곳 [m].

    네모 안에 NaN(유역 밖·바다)이 하나라도 있으면 NaN 입니다.
    그래서 경계에 걸친 네모는 세지 않습니다.
    """
    from scipy import ndimage

    z = np.asarray(z, dtype=np.float64)
    valid = np.isfinite(z)
    hi = ndimage.maximum_filter(
        np.where(valid, z, -np.inf), size=size, mode="constant", cval=-np.inf
    )
    lo = ndimage.minimum_filter(np.where(valid, z, np.inf), size=size, mode="constant", cval=np.inf)
    full = ndimage.minimum_filter(valid.astype(np.uint8), size=size, mode="constant", cval=0) > 0
    return np.where(full, hi - lo, np.nan)


def hypsometric_integral(z: np.ndarray) -> float | None:
    """(평균 − 최저) ÷ (최고 − 최저). 0.5 면 평균 높이가 한가운데. 기복이 0 이면 None."""
    v = np.asarray(z, dtype=np.float64)
    v = v[np.isfinite(v)]
    if v.size < 2:
        return None
    lo, hi = float(v.min()), float(v.max())
    if hi - lo <= 0:
        return None
    return float((v.mean() - lo) / (hi - lo))


# ---------------------------------------------------------------- 물길
def terminal_of(receiver: np.ndarray, stop: np.ndarray | None = None) -> np.ndarray:
    """칸마다 물을 따라 내려가 닿는 마지막 칸 (receiver 가 자기 자신이거나 음수인 칸, 또는 stop 칸).

    포인터를 두 배씩 건너뛰어 log2(N) 번 안에 끝냅니다.
    """
    n = receiver.shape[0]
    idx = np.arange(n)
    nxt = np.asarray(receiver, dtype=np.int64).copy()
    bad = (nxt < 0) | (nxt >= n)
    nxt[bad] = idx[bad]
    if stop is not None:
        nxt[stop] = idx[stop]
    for _ in range(64):
        new = nxt[nxt]
        if np.array_equal(new, nxt):
            break
        nxt = new
    return nxt


def strahler_streams(receiver: np.ndarray, keep: np.ndarray, area: np.ndarray) -> dict[int, int]:
    """물길 그물의 차수(Strahler)별 물줄기 수.

    keep 인 칸(또는 하천 구간)만 그물로 봅니다. 상류 넓이(area)가 작은 것부터 차례로 돌며, 가장 큰
    윗물 차수가 둘 이상 만나면 차수를 하나 올립니다. 물줄기 수는 그 차수가 새로 시작하는 곳(샘, 같은
    차수 두 개가 만나는 곳)의 수입니다.
    """
    n = receiver.shape[0]
    rcv = np.asarray(receiver, dtype=np.int64)
    ids = np.flatnonzero(keep)
    order = ids[np.argsort(np.asarray(area)[ids], kind="stable")]
    top = np.zeros(n, dtype=np.int32)  # 들어온 윗물 가운데 가장 큰 차수
    ntop = np.zeros(n, dtype=np.int32)  # 그 차수로 들어온 윗물 수
    counts: dict[int, int] = {}
    for c in order.tolist():
        o = 1 if top[c] == 0 else int(top[c]) + (1 if ntop[c] >= 2 else 0)
        if o != top[c]:
            counts[o] = counts.get(o, 0) + 1
        p = int(rcv[c])
        if 0 <= p < n and p != c and keep[p]:
            if o > top[p]:
                top[p] = o
                ntop[p] = 1
            elif o == top[p]:
                ntop[p] += 1
    return dict(sorted(counts.items()))


def bifurcation_ratio(counts: dict[int, int]) -> float | None:
    """차수별 물줄기 수 N_k 가 차수마다 몇 분의 1로 줄어드는지 (ln N_k 를 차수에 곧게 맞춘 기울기).

    차수가 셋보다 적으면 None 입니다. 지구의 큰 강 그물은 보통 3~5 입니다.
    """
    ks = [k for k, v in counts.items() if v > 0]
    if len(ks) < 3:
        return None
    x = np.asarray(ks, dtype=np.float64)
    y = np.log(np.asarray([counts[k] for k in ks], dtype=np.float64))
    slope = np.polyfit(x, y, 1)[0]
    return float(math.exp(-slope))


def binned_median(x: np.ndarray, y: np.ndarray, edges: np.ndarray, min_count: int = 5) -> list:
    """x 구간마다 y 의 가운데값 (표본이 min_count 보다 적으면 None)."""
    x = np.asarray(x, dtype=np.float64)
    y = np.asarray(y, dtype=np.float64)
    ok = np.isfinite(x) & np.isfinite(y)
    x, y = x[ok], y[ok]
    out: list = []
    for a, b in zip(edges[:-1], edges[1:], strict=True):
        m = (x >= a) & (x < b)
        out.append(float(np.median(y[m])) if m.sum() >= min_count else None)
    return out


def class_fractions(
    values: np.ndarray, edges: Sequence[float], weights: np.ndarray | None = None
) -> list:
    """값을 edges 구간으로 나눈 넓이 몫 (합 1, 유한한 값만)."""
    v = np.asarray(values, dtype=np.float64).ravel()
    w = np.ones_like(v) if weights is None else np.asarray(weights, dtype=np.float64).ravel()
    ok = np.isfinite(v) & (w > 0)
    tot = w[ok].sum()
    if tot <= 0:
        return [0.0] * (len(edges) - 1)
    out = []
    for a, b in zip(edges[:-1], edges[1:], strict=True):
        m = ok & (v >= a) & (v < b)
        out.append(float(w[m].sum() / tot))
    return out


def hist2d_counts(
    x: np.ndarray, y: np.ndarray, x_edges: np.ndarray, y_edges: np.ndarray
) -> np.ndarray:
    """(x 구간 × y 구간) 개수 표. 구간 밖 값은 양 끝 구간에 넣습니다."""
    x = np.asarray(x, dtype=np.float64)
    y = np.asarray(y, dtype=np.float64)
    ok = np.isfinite(x) & np.isfinite(y)
    xx = np.clip(x[ok], x_edges[0], np.nextafter(x_edges[-1], x_edges[0]))
    yy = np.clip(y[ok], y_edges[0], np.nextafter(y_edges[-1], y_edges[0]))
    h, _, _ = np.histogram2d(xx, yy, bins=[x_edges, y_edges])
    return h


def l0_spacing_m(n_per_face: int, radius_m: float) -> float:
    """정육면체 구 격자(L0) 한 칸의 평균 크기 [m] = 2πR / (4n)."""
    return 2.0 * math.pi * radius_m / (4.0 * n_per_face)


def l0_cell_area_m2(n_per_face: int, radius_m: float) -> float:
    """L0 칸 하나의 평균 넓이 [m²] = 구 겉넓이 ÷ (6 n²)."""
    return 4.0 * math.pi * radius_m**2 / (6.0 * n_per_face**2)


def nearest_divisor(target: float, n: int = 10_800) -> int:
    """n 을 나누어떨어지게 하는 수 가운데 target 에 가장 가까운 것 (1 이상)."""
    divs = [d for d in range(1, n + 1) if n % d == 0 and 21_600 % d == 0]
    return min(divs, key=lambda d: (abs(d - target), d))
