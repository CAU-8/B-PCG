"""회랑 지표의 프랙탈 디테일: 히어로 격자가 그리지 못한 짧은 파장의 거칠기를 이어 그립니다.

현상
- 실제 산지 지표는 넓은 크기 범위에서 작게 볼수록 계속 거칩니다(자기 아핀). 고도의 2D 파워
  스펙트럼이 P(k) ∝ k^−β 를 따르고, GLO-90 산지 타일 16개에서 β ≈ 3.0 (2–15 km), β ≈ 4.2
  (0.2–2 km) 입니다. 히어로(25 m) 지표도 같은 값(2.98, 4.26)을 냅니다.
- 회랑 2 m 높이맵은 50 m 보다 짧은 파장에서 매끈합니다(4–20 m 에서 β ≈ 4.74). 히어로 25 m 지도를
  쌍선형 보간하고 40 m fbm(진폭 2 m) 하나만 더하는데, 25 m 격자는 칸 2개(50 m, 나이퀴스트)보다
  짧은 파장을 그리지 못하고 그 근처 파장도 보간으로 약해지기 때문입니다.

규칙 (설정 [detail])
1. 이음 기준: 회랑 지표에서 히어로가 그린 가장 짧은 옥타브(파장 max_wavelength_m ~ 그 두 배)의
   띠 RMS a 를 잽니다(band_rms).
2. 자기 아핀으로 이어 가기: 파장이 반으로 줄 때마다 옥타브 RMS 가 2^−H 배인 스펙트럼
   P ∝ k^−(2+2H) 를 [1/max_wavelength_m, 1/min_wavelength_m] 띠에만 만듭니다(fractal_field).
   띠 첫 옥타브(max_λ/2 ~ max_λ)의 RMS 가 fractal_gain·a·2^−H 이 되게 크기를 맞춥니다(테이퍼
   전 스펙트럼 기준이라, 테이퍼가 끝난 파장부터는 거듭제곱 법칙을 그대로 잇습니다).
   흰 가우스 잡음은 전역 칸 번호의 정수 해시로 만들어(core.hashing) 시드가 같으면 늘 같습니다.
   디테일은 원래 지표에 더해집니다. 원래 지표의 같은 띠 성분(40 m fbm 등)은 그대로 남습니다.
3. 어디에 얼마나: 디테일 계수 m = 경사 항 × 흙 항 × 물 항 × 동굴 항 × 웅덩이 항 × 가장자리 항.
   - 경사: 가파른 비탈일수록 암반이 드러나 거칩니다. m_S = clip(S / slope_ref, 0, 1).
   - 흙·충적층: 흙이 덮인 비탈은 확산이 짧은 파장을 지워 매끈합니다(Perron 2008). soil_factor 배.
   - 물: 물 칸에서 water_margin_m 안은 0, 그 뒤 20 m 에 걸쳐 1 로 오릅니다. 물 칸은 그대로입니다.
   - 동굴 입구: 원래 지표의 동굴 입구 구멍(cave_mouth < 0)에서 2 m 안은 0, 그 뒤 10 m 에 걸쳐 1.
     입구 둘레를 들어 올리면 지표 아래로 잘라 둔 동굴 벽과 지표 사이에 틈이 생기기 때문입니다.
   - 웅덩이: 원래 지표의 닫힌 웅덩이 칸(base_sinks)은 0, 그 뒤 6 m 에 걸쳐 1. 원래 우묵한 곳의
     모양을 그대로 둡니다.
   - 가장자리: 회랑 가장자리에서 edge_fade_m 에 걸쳐 0 → 1. 주변 25 m 지형과의 단차를 늘리지
     않습니다.
4. 웅덩이 채우기: 더한 잡음이 만든 닫힌 웅덩이에 빗물이 고여 보이지 않게, 가장자리·물 칸을
   출구로 priority-flood 로 채웁니다(hydro.depressions.fill_depressions). 원래 웅덩이 칸도
   출구로 두고, 그 칸은 3 의 웅덩이 항으로 높이가 바뀌지 않으므로 원래 모양 그대로입니다.
   그래서 디테일을 켜고 꺼도 달라지는 것은 짧은 파장의 거칠기와 그것이 만든 작은 웅덩이뿐입니다.

측정: 회랑 가운데 정사각 창에서 4–50 m 스펙트럼 기울기 β 를 더하기 전과 뒤에 잽니다(psd_slope).

보기용 디테일입니다. 솔버·물길·지하수·동굴 결과가 아니며 water·strata·caves 는 바꾸지 않습니다.
굽기는 이 지표로 동굴 입구 구멍(cave_mouth_detail)만 다시 잽니다.
"""

import math

import numpy as np
from numba import njit, prange
from scipy import ndimage

from bpcg.core import cubesphere as cs
from bpcg.core.hashing import hash3, hash_unit
from bpcg.geology import rocks as rk
from bpcg.hydro.depressions import fill_depressions

_STREAM_FRACTAL = 7301  # 프랙탈 디테일 잡음 시드 갈래 (다른 모듈의 갈래 번호와 겹치지 않게)

TAPER_OCTAVES = 0.5  # 띠 양 끝에서 0 → 1 로 오르는 폭 [옥타브] (log k 에서 올린 코사인)
PAD_WAVELENGTHS = 3.0  # 창 둘레에 더 만드는 폭 [max_wavelength_m 배] (주기 경계를 창 밖으로)
PAD_MAX_CELLS = 512  # 그 폭의 상한 [칸] (큰 max_wavelength_m 에서 FFT 격자가 커지지 않게)
SLOPE_SMOOTH_M = 10.0  # 경사를 잴 때 지표를 고르는 가우스 σ [m] (40 m fbm 의 잔 경사를 덜어 냄)
SOIL_PROBE_DEPTH_M = 0.5  # 이 깊이의 재질이 흙·충적층이면 흙 덮인 곳으로 봄
SOIL_SMOOTH_M = 6.0  # 흙 항 경계를 고르는 가우스 σ [m] (흙·암반 경계에 계단이 생기지 않게)
WATER_RAMP_M = 20.0  # 물 항이 0 에서 1 로 오르는 거리 [m]
CAVE_MARGIN_M = 2.0  # 동굴 입구 구멍에서 이 거리 안은 디테일 0 [m]
CAVE_RAMP_M = 10.0  # 동굴 항이 0 에서 1 로 오르는 거리 [m]
SINK_RAMP_M = 6.0  # 원래 웅덩이 칸에서 웅덩이 항이 0 에서 1 로 오르는 거리 [m]
BETA_WAVELENGTH_M = (4.0, 50.0)  # 전후 β 를 잴 파장 범위 [m]
BINS_PER_OCTAVE = 4  # 방사 평균 스펙트럼의 log k 칸 수 (옥타브당)
NOTE = "보기용 디테일, 솔버 결과 아님"


# ---------------------------------------------------------------- 흰 잡음
def _sub_seed(seed: int, k: int) -> np.int64:
    """planet.seed 에서 이 모듈의 갈래 시드 (0 ≤ 값 < 2^63)."""
    h = hash3(np.int64(int(seed)), np.int64(_STREAM_FRACTAL), np.int64(k))
    return np.int64(int(h >> np.uint64(1)))


@njit(cache=True, parallel=True)
def _white_noise_kernel(seed_a, seed_b, row0, col0, out):
    """전역 칸 (row0 + j, col0 + i) 마다 표준 정규 값 하나 (Box–Muller, 칸마다 독립)."""
    ny, nx = out.shape
    two_pi = 2.0 * math.pi
    for j in prange(ny):
        r = np.int64(row0 + j)
        for i in range(nx):
            c = np.int64(col0 + i)
            u1 = hash_unit(seed_a, r, c)  # [0, 1)
            u2 = hash_unit(seed_b, r, c)
            out[j, i] = math.sqrt(-2.0 * math.log(1.0 - u1)) * math.cos(two_pi * u2)


def white_noise(shape, seed: int, cell_offset=(0, 0)) -> np.ndarray:
    """전역 칸 번호의 정수 해시로 만든 표준 정규 흰 잡음 (ny, nx) float64.

    cell_offset (row0, col0): 0번 행·열의 전역 칸 번호. 같은 전역 칸은 창과 상관없이 같은 값입니다.
    """
    ny, nx = (int(v) for v in shape)
    out = np.empty((ny, nx), dtype=np.float64)
    _white_noise_kernel(
        _sub_seed(seed, 0), _sub_seed(seed, 1), int(cell_offset[0]), int(cell_offset[1]), out
    )
    return out


# ---------------------------------------------------------------- 스펙트럼 도움 함수
def _rfft_k(ny: int, nx: int, spacing_m: float) -> np.ndarray:
    """rfft2 반평면의 파수 크기 |k| [1/m] (ny, nx//2 + 1)."""
    ky = np.fft.fftfreq(ny, d=spacing_m)
    kx = np.fft.rfftfreq(nx, d=spacing_m)
    return np.hypot(ky[:, None], kx[None, :])


def _rfft_weights(nx: int) -> np.ndarray:
    """rfft2 반평면 열마다 전체 평면에서 몇 번 나오는지 (0번 열과 짝수 길이의 나이퀴스트 열은 1)."""
    w = np.full(nx // 2 + 1, 2.0)
    w[0] = 1.0
    if nx % 2 == 0:
        w[-1] = 1.0
    return w


def _raised_cosine(t: np.ndarray) -> np.ndarray:
    """t ≤ 0 이면 0, t ≥ 1 이면 1, 그 사이는 ½(1 − cos πt)."""
    t = np.clip(t, 0.0, 1.0)
    return 0.5 * (1.0 - np.cos(np.pi * t))


def band_limits(spacing_m: float, min_wavelength_m: float, max_wavelength_m: float):
    """더할 파수 띠 (k_lo, k_hi) [1/m]. k_hi 는 격자의 나이퀴스트 1/(2·간격) 을 넘지 않습니다."""
    k_lo = 1.0 / float(max_wavelength_m)
    k_hi = min(1.0 / float(min_wavelength_m), 0.5 / float(spacing_m))
    return k_lo, k_hi


def _band_filter(k: np.ndarray, k_lo: float, k_hi: float, hurst: float, taper_top: bool):
    """진폭 필터 A(k) = W(k)·k^−(1+H) 와 테이퍼 없는 k^−(1+H) (띠 밖 0).

    taper_top 이 거짓이면(위쪽 끝이 격자 나이퀴스트) 위쪽은 테이퍼 없이 자릅니다.
    """
    kk = np.where(k > 0, k, 1.0)
    inside = (k >= k_lo) & (k <= k_hi) & (k > 0)
    taper = _raised_cosine(np.log2(kk / k_lo) / TAPER_OCTAVES)
    if taper_top:
        taper = taper * _raised_cosine(np.log2(k_hi / kk) / TAPER_OCTAVES)
    power_law = np.where(inside, kk ** -(1.0 + hurst), 0.0)
    return np.where(inside, taper, 0.0) * power_law, power_law


def fractal_field(
    shape,
    spacing_m: float,
    min_wavelength_m: float,
    max_wavelength_m: float,
    hurst: float,
    seed: int,
    cell_offset=(0, 0),
    pad_m: float | None = None,
) -> tuple[np.ndarray, dict]:
    """띠 제한 자기 아핀 잡음 (ny, nx), 표준편차 1 (기댓값 기준).

    흰 가우스 잡음(white_noise)을 FFT 로 걸러 진폭이 k^−(1+H) (파워 k^−(2+2H)) 이 되게 합니다.
    띠 [1/max_λ, min(1/min_λ, 나이퀴스트)] 밖은 0 이고, 양 끝 TAPER_OCTAVES 옥타브는 log k 에서
    올린 코사인으로 0 → 1 입니다(위쪽 끝이 나이퀴스트면 위쪽은 테이퍼 없이 자름).
    표준편차는 필터의 파워 합으로 나눠 기댓값이 1 이 되게 맞춥니다(창마다 표본 표준편차는 몇 %
    다릅니다). 표본으로 맞추지 않으므로 같은 전역 칸은 창이 달라도 거의 같은 값입니다.

    spacing_m: 격자 간격 [m]. hurst: H (0 < H ≤ 1). seed: planet.seed (갈래는 이 모듈이 나눔).
    cell_offset (row0, col0): 0번 행·열의 전역 칸 번호 (행은 남쪽, 열은 동쪽으로 늚).
    pad_m: 창 둘레에 더 만드는 폭 [m]. 기본 PAD_WAVELENGTHS·max_λ. 0 이면 창 자체를 주기 경계로
    거르므로 띠 밖 파워가 정확히 0 이지만, 창 가장자리가 반대쪽 가장자리와 이어집니다.
    반환 diag: pad_cells, k_lo_per_m, k_hi_per_m, min_wavelength_eff_m, sigma_raw,
    top_octave_rms_per_std (테이퍼 없는 첫 옥타브 [k_lo, 2·k_lo) 의 RMS / 표준편차).
    """
    ny, nx = (int(v) for v in shape)
    dx = float(spacing_m)
    h = float(hurst)
    if ny < 2 or nx < 2:
        raise ValueError(f"shape 는 한 변이 2 이상이어야 합니다: {shape}")
    if not (math.isfinite(dx) and dx > 0):
        raise ValueError(f"spacing_m 은 0 보다 커야 합니다: {spacing_m}")
    if not (0.0 < h <= 1.0):
        raise ValueError(f"hurst 는 0 < H ≤ 1 이어야 합니다: {hurst}")
    if not (0.0 < min_wavelength_m < max_wavelength_m):
        raise ValueError(
            f"0 < min_wavelength_m < max_wavelength_m 이어야 합니다: "
            f"{min_wavelength_m}, {max_wavelength_m}"
        )
    k_lo, k_hi = band_limits(dx, min_wavelength_m, max_wavelength_m)
    if k_hi <= k_lo:
        raise ValueError(
            f"간격 {dx:g} m 격자로는 {max_wavelength_m:g} m 보다 짧은 파장을 그릴 수 없습니다"
        )
    pad_len = PAD_WAVELENGTHS * float(max_wavelength_m) if pad_m is None else float(pad_m)
    p = min(max(int(math.ceil(pad_len / dx)), 0), PAD_MAX_CELLS)
    ny_p, nx_p = ny + 2 * p, nx + 2 * p
    noise = white_noise((ny_p, nx_p), seed, (int(cell_offset[0]) - p, int(cell_offset[1]) - p))
    k = _rfft_k(ny_p, nx_p, dx)
    # 위쪽 끝이 나이퀴스트면 테이퍼를 두지 않습니다. 격자가 더 짧은 파장을 그리지 못하므로
    # 끝을 잘라도 고리 무늬가 칸 크기에만 생기고, 테이퍼는 가장 짧은 파장의 거칠기만 지웁니다.
    taper_top = 1.0 / float(min_wavelength_m) < 0.5 / dx * (1.0 - 1e-9)
    amp, power_law = _band_filter(k, k_lo, k_hi, h, taper_top)
    w = _rfft_weights(nx_p)[None, :]
    n_all = ny_p * nx_p
    # 단위 분산 흰 잡음을 A 로 거른 값의 분산 = (1/N) Σ_전체평면 A²
    var = float((w * amp * amp).sum()) / n_all
    if not var > 0.0:
        raise ValueError(
            f"[{1.0 / k_hi:.3g}, {1.0 / k_lo:.3g}] m 띠에 이 격자({ny_p}×{nx_p}, {dx:g} m)의 "
            "FFT 칸이 없습니다. min_wavelength_m 과 max_wavelength_m 사이를 넓히세요"
        )
    sigma = math.sqrt(var)
    top = (k >= k_lo) & (k < 2.0 * k_lo)
    top_var = float((w * np.where(top, power_law, 0.0) ** 2).sum()) / n_all
    field = np.fft.irfft2(np.fft.rfft2(noise) * amp, s=(ny_p, nx_p))
    field = field[p : p + ny, p : p + nx] / sigma
    diag = {
        "pad_cells": p,
        "k_lo_per_m": k_lo,
        "k_hi_per_m": k_hi,
        "min_wavelength_eff_m": 1.0 / k_hi,
        "sigma_raw": sigma,
        "top_octave_rms_per_std": math.sqrt(top_var) / sigma,
        "taper_top": bool(taper_top),
        "seed_stream": _STREAM_FRACTAL,
    }
    return np.ascontiguousarray(field), diag


def _detrend_plane(z: np.ndarray) -> np.ndarray:
    """가장 잘 맞는 평면 a + b·i + c·j 를 뺍니다 (최소제곱).

    꽉 찬 격자에서는 가운데로 옮긴 i, j 가 서로와 상수에 직교하므로 계수를 따로 구합니다.
    """
    ny, nx = z.shape
    ic = np.arange(nx, dtype=np.float64) - 0.5 * (nx - 1)
    jc = np.arange(ny, dtype=np.float64) - 0.5 * (ny - 1)
    b = float((z @ ic).sum()) / (ny * float(ic @ ic))
    c = float((jc @ z).sum()) / (nx * float(jc @ jc))
    return z - (float(z.mean()) + b * ic[None, :] + c * jc[:, None])


def _periodogram(z, spacing_m: float) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """평면을 빼고 2D Hann 창을 씌운 주기도 (반평면).

    반환 (P, w, k): P (ny, nx//2+1) 는 칸마다 분산 몫 (Σ w·P = 창 보정한 분산 추정),
    w 는 반평면 열 가중치, k 는 |k| [1/m].
    """
    a = np.asarray(z, dtype=np.float64)
    if a.ndim != 2 or min(a.shape) < 4:
        raise ValueError(f"z 는 한 변이 4 이상인 2차원 배열이어야 합니다: {a.shape}")
    if not np.isfinite(a).all():
        raise ValueError("z 에 NaN 이나 inf 가 있습니다")
    ny, nx = a.shape
    win = np.outer(np.hanning(ny), np.hanning(nx))
    F = np.fft.rfft2(_detrend_plane(a) * win)
    P = (F.real**2 + F.imag**2) / (float(a.size) ** 2 * float(np.mean(win * win)))
    w = np.broadcast_to(_rfft_weights(nx)[None, :], P.shape)
    return P, w, _rfft_k(ny, nx, float(spacing_m))


def band_rms(z, spacing_m: float, lam_lo_m: float, lam_hi_m: float) -> float:
    """파장 [lam_lo_m, lam_hi_m] 띠의 RMS [m] (평면 제거·Hann 창 보정한 FFT 띠 통과)."""
    P, w, k = _periodogram(z, spacing_m)
    sel = (k >= 1.0 / float(lam_hi_m)) & (k <= 1.0 / float(lam_lo_m))
    return math.sqrt(float((P * w)[sel].sum()))


def psd_slope(z, spacing_m: float, lam_lo_m: float, lam_hi_m: float) -> float:
    """방사 평균 2D 파워 스펙트럼 P(k) ∝ k^−β 의 β (파장 [lam_lo_m, lam_hi_m]).

    평면을 빼고 Hann 창을 씌운 주기도를 log k 칸(옥타브당 BINS_PER_OCTAVE)으로 평균해 log-log
    최소제곱으로 맞춥니다. 짧은 쪽은 나이퀴스트에서 자릅니다. 칸이 3개보다 적으면 NaN.
    """
    P, _, k = _periodogram(z, spacing_m)
    k_lo = 1.0 / float(lam_hi_m)
    k_hi = min(1.0 / float(lam_lo_m), 0.5 / float(spacing_m))
    if k_hi <= k_lo:
        return math.nan
    n_bins = max(int(math.ceil(math.log2(k_hi / k_lo) * BINS_PER_OCTAVE)), 1)
    edges = k_lo * (k_hi / k_lo) ** (np.arange(n_bins + 1) / n_bins)
    sel = (k >= k_lo) & (k <= k_hi) & (k > 0)
    kk, pp = k[sel], P[sel]
    idx = np.clip(np.searchsorted(edges, kk, side="right") - 1, 0, n_bins - 1)
    cnt = np.bincount(idx, minlength=n_bins)
    ok = (cnt > 0) & (np.bincount(idx, weights=pp, minlength=n_bins) > 0)
    if ok.sum() < 3:
        return math.nan
    logk = np.bincount(idx, weights=np.log10(kk), minlength=n_bins)[ok] / cnt[ok]
    logp = np.log10(np.bincount(idx, weights=pp, minlength=n_bins)[ok] / cnt[ok])
    slope = np.polyfit(logk, logp, 1)[0]
    return float(-slope)


def central_window(z: np.ndarray) -> np.ndarray:
    """가운데 정사각 창 (한 변 = 짧은 변)."""
    ny, nx = z.shape
    s = min(ny, nx)
    r0, c0 = (ny - s) // 2, (nx - s) // 2
    return z[r0 : r0 + s, c0 : c0 + s]


# ---------------------------------------------------------------- 디테일 계수와 더하기
def _grid_neighbors(ny: int, nx: int) -> np.ndarray:
    """격자 8 이웃 표 (N, 8) int32, 없으면 −1.

    flat_graph 와 같은 슬롯 순서입니다. 거리·위치 배열은 만들지 않아 큰 회랑에서도 가볍습니다.
    """
    jj, ii = np.divmod(np.arange(ny * nx, dtype=np.int64), nx)
    nbr = np.full((ny * nx, 8), -1, dtype=np.int32)
    for s, (dj, di) in enumerate(cs.NEIGHBOR_SLOTS):
        j2 = jj + dj
        i2 = ii + di
        ok = (j2 >= 0) & (j2 < ny) & (i2 >= 0) & (i2 < nx)
        nbr[ok, s] = j2[ok] * nx + i2[ok]
    return nbr


def edge_outlets(wet) -> np.ndarray:
    """웅덩이 채우기의 기본 출구 (ny, nx) bool: 물 칸과 회랑 가장자리 칸."""
    out = np.array(wet, dtype=bool)
    out[0, :] = out[-1, :] = out[:, 0] = out[:, -1] = True
    return out


def base_sinks(surf, wet, nbr: np.ndarray | None = None) -> np.ndarray:
    """원래 지표의 닫힌 웅덩이 칸 (ny, nx) bool (가장자리·물 칸을 출구로 채우면 올라가는 칸).

    히어로 지도와 40 m 지표 노이즈가 만든 웅덩이입니다. 디테일은 이 칸을 출구로 두어 채우지
    않으므로, 디테일을 켜고 꺼도 원래 지형의 우묵한 곳은 그대로입니다.
    """
    z = np.asarray(surf, dtype=np.float64)
    ny, nx = z.shape
    if nbr is None:
        nbr = _grid_neighbors(ny, nx)
    filled = fill_depressions(z.ravel(), nbr, edge_outlets(wet).ravel()).reshape(z.shape)
    return filled > z


def detail_settings(cfg) -> dict | None:
    """설정 [detail] 을 읽어 검사합니다. 절이 없거나 fractal_gain ≤ 0 이면 None (끔)."""
    det = cfg.get("detail") if hasattr(cfg, "get") else None
    if det is None or "fractal_gain" not in det:
        return None
    s = {
        "gain": float(det.fractal_gain),
        "hurst": float(det.hurst),
        "min_wavelength_m": float(det.min_wavelength_m),
        "max_wavelength_m": float(det.max_wavelength_m),
        "soil_factor": float(det.soil_factor),
        "slope_ref": float(det.slope_ref),
        "water_margin_m": float(det.water_margin_m),
        "edge_fade_m": float(det.get("edge_fade_m", det.max_wavelength_m)),
    }
    if not all(math.isfinite(v) for v in s.values()):
        raise ValueError(f"detail 설정에 NaN 이나 inf 가 있습니다: {s}")
    if s["gain"] <= 0:
        return None
    if not (0.0 < s["hurst"] <= 1.0):
        raise ValueError(f"detail.hurst 는 0 < H ≤ 1 이어야 합니다: {s['hurst']}")
    if not (0.0 < s["min_wavelength_m"] < s["max_wavelength_m"]):
        raise ValueError(
            "detail 은 0 < min_wavelength_m < max_wavelength_m 이어야 합니다: "
            f"{s['min_wavelength_m']}, {s['max_wavelength_m']}"
        )
    if not (0.0 <= s["soil_factor"] <= 1.0):
        raise ValueError(f"detail.soil_factor 는 0~1 이어야 합니다: {s['soil_factor']}")
    if s["slope_ref"] <= 0 or s["water_margin_m"] < 0 or s["edge_fade_m"] < 0:
        raise ValueError(
            "detail.slope_ref 는 0 보다 크고 water_margin_m, edge_fade_m 은 0 이상이어야 "
            f"합니다: {s}"
        )
    return s


def detail_weight(surf, gx, gy, voxel_m: float, wet, vol, settings: dict):
    """디테일 계수 m (ny, nx) = 경사 항 × 흙 항 × 물 항 (모듈 설명 3) 과 항별 지도."""
    surf = np.asarray(surf, dtype=np.float64)
    wet = np.asarray(wet, dtype=bool)
    dx = float(voxel_m)
    smooth = ndimage.gaussian_filter(surf, SLOPE_SMOOTH_M / dx, mode="nearest")
    gyy, gxx = np.gradient(smooth, dx)
    slope = np.hypot(gxx, gyy)
    m_slope = np.clip(slope / settings["slope_ref"], 0.0, 1.0)
    probe = (surf - SOIL_PROBE_DEPTH_M).reshape(-1, 1)
    mat = vol.evaluate_grid(gx.ravel(), gy.ravel(), probe, keys=("solid_material",))
    mat = mat["solid_material"].reshape(surf.shape)
    soil = (mat == rk.SOIL) | (mat == rk.ALLUVIUM)
    m_soil = ndimage.gaussian_filter(
        np.where(soil, settings["soil_factor"], 1.0), SOIL_SMOOTH_M / dx, mode="nearest"
    )
    if wet.any():
        dist = ndimage.distance_transform_edt(~wet) * dx
        m_water = np.clip((dist - settings["water_margin_m"]) / WATER_RAMP_M, 0.0, 1.0)
        m_water[wet] = 0.0
    else:
        m_water = np.ones_like(surf)
    m = m_slope * m_soil * m_water
    return m, {"slope": slope, "soil": soil, "m_slope": m_slope, "m_water": m_water}


def _ramp_from(mask: np.ndarray, voxel_m: float, margin_m: float, ramp_m: float) -> np.ndarray:
    """mask 칸에서 margin_m 안은 0, 그 뒤 ramp_m 에 걸쳐 1 로 오르는 계수 (mask 칸은 정확히 0)."""
    if not mask.any():
        return np.ones(mask.shape)
    dist = ndimage.distance_transform_edt(~mask) * float(voxel_m)
    out = np.clip((dist - margin_m) / ramp_m, 0.0, 1.0)
    out[mask] = 0.0
    return out


def _edge_ramp(shape, voxel_m: float, fade_m: float) -> np.ndarray:
    """회랑 가장자리에서 fade_m 에 걸쳐 0 → 1 로 오르는 계수 (가장자리 칸은 0)."""
    ny, nx = shape
    if fade_m <= 0.0:
        return np.ones(shape)
    r = np.minimum(np.arange(ny), np.arange(ny)[::-1])[:, None]
    c = np.minimum(np.arange(nx), np.arange(nx)[::-1])[None, :]
    return np.clip(np.minimum(r, c) * float(voxel_m) / fade_m, 0.0, 1.0)


def add_fractal_detail(
    surf, gx, gy, voxel_m: float, wet, vol, cfg, *, mouth=None
) -> tuple[np.ndarray, dict]:
    """회랑 지표 surf (ny, nx) [m] 에 프랙탈 디테일을 더하고 웅덩이를 채웁니다 (모듈 설명).

    gx, gy: 표본의 국소 (동, 북) [m] (ny, nx), 0번 행이 북쪽 끝. voxel_m: 간격 [m].
    wet: (ny, nx) bool 물 칸 (water.bin 의 물 표본). vol: HeroVolume (흙·충적층 판정).
    mouth: (ny, nx) 원래 지표의 동굴 거리 d_cave (cave_mouth) 또는 None. 음수인 입구 구멍 둘레는
    디테일을 0 으로 둡니다.
    cfg: 설정 ([detail], planet.seed). [detail] 이 없거나 fractal_gain ≤ 0 이면 ValueError.
    반환 (surf_detail (ny, nx) float64, meta). 물 칸은 surf 와 비트까지 같습니다.
    meta: gain, hurst, min_wavelength_m, max_wavelength_m, anchor_rms_m, rms_m, max_abs_m (채운 뒤
    surf 와의 차이), beta_before, beta_after, n_filled (채워 올라간 칸 수), note 와 진단값.
    """
    st = detail_settings(cfg)
    if st is None:
        raise ValueError("detail.fractal_gain 이 0 보다 커야 프랙탈 디테일을 더합니다")
    surf = np.asarray(surf, dtype=np.float64)
    wet = np.asarray(wet, dtype=bool)
    gx = np.asarray(gx, dtype=np.float64)
    gy = np.asarray(gy, dtype=np.float64)
    if surf.ndim != 2 or wet.shape != surf.shape or gx.shape != surf.shape:
        raise ValueError(f"surf, wet, gx, gy 는 같은 2차원 모양이어야 합니다: {surf.shape}")
    ny, nx = surf.shape
    dx = float(voxel_m)
    lam_max = st["max_wavelength_m"]
    hurst = st["hurst"]

    # 1. 이음 기준: 히어로가 그린 가장 짧은 옥타브 [max_λ, 2·max_λ] 의 띠 RMS
    anchor = band_rms(surf, dx, lam_max, 2.0 * lam_max)
    # 2. 자기 아핀 잡음. 전역 칸 번호 = 국소 좌표 / 간격 (회랑이 달라도 같은 칸은 같은 잡음)
    offset = (round(-float(gy[0, 0]) / dx), round(float(gx[0, 0]) / dx))
    field, fdiag = fractal_field(
        surf.shape, dx, st["min_wavelength_m"], lam_max, hurst, int(cfg.planet.seed), offset
    )
    amp_m = st["gain"] * anchor * 2.0**-hurst / fdiag["top_octave_rms_per_std"]
    # 3. 어디에 얼마나. 동굴 입구·원래 웅덩이·회랑 가장자리 둘레는 0 (모듈 설명 3)
    m, parts = detail_weight(surf, gx, gy, dx, wet, vol, st)
    nbr = _grid_neighbors(ny, nx)
    sink = base_sinks(surf, wet, nbr)
    cave = np.zeros(surf.shape, dtype=bool) if mouth is None else np.asarray(mouth) < 0.0
    m = (
        m
        * _ramp_from(cave, dx, CAVE_MARGIN_M, CAVE_RAMP_M)
        * _ramp_from(sink, dx, 0.0, SINK_RAMP_M)
        * _edge_ramp(surf.shape, dx, st["edge_fade_m"])
    )
    z = surf + amp_m * m * field
    z[wet] = surf[wet]
    z[sink | cave] = surf[sink | cave]
    # 4. 새로 생긴 닫힌 웅덩이 채우기. 출구 = 가장자리 + 물 칸 + 원래 웅덩이 칸 + 동굴 입구 구멍
    # (물이 동굴로 빠지는 싱크홀이라 채우지 않습니다)
    outlet = edge_outlets(wet) | sink | cave
    filled = fill_depressions(z.ravel(), nbr, outlet.ravel()).reshape(z.shape)
    n_filled = int((filled > z).sum())
    filled[wet] = surf[wet]

    diff = filled - surf
    lam_lo, lam_hi = BETA_WAVELENGTH_M
    meta = {
        "gain": st["gain"],
        "hurst": hurst,
        "min_wavelength_m": st["min_wavelength_m"],
        "max_wavelength_m": lam_max,
        "min_wavelength_eff_m": fdiag["min_wavelength_eff_m"],
        "anchor_rms_m": anchor,
        "amplitude_m": amp_m,
        "rms_m": float(np.sqrt(np.mean(diff * diff))),
        "max_abs_m": float(np.abs(diff).max()),
        "beta_before": psd_slope(central_window(surf), dx, lam_lo, lam_hi),
        "beta_after": psd_slope(central_window(filled), dx, lam_lo, lam_hi),
        "beta_wavelength_m": [lam_lo, lam_hi],
        "n_filled": n_filled,
        "n_base_sink_cells": int(sink.sum()),
        "mean_weight": float(m.mean()),
        "soil_fraction": float(parts["soil"].mean()),
        "soil_factor": st["soil_factor"],
        "slope_ref": st["slope_ref"],
        "water_margin_m": st["water_margin_m"],
        "edge_fade_m": st["edge_fade_m"],
        "n_cave_mouth_cells": int(cave.sum()),
        "cell_offset": [int(offset[0]), int(offset[1])],
        "seed_stream": _STREAM_FRACTAL,
        "note": NOTE,
    }
    return filled, meta
