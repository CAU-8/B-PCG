"""결정적 3D 그래디언트 노이즈와 fbm (가이드 5장 ξ, docs/pipeline.md 3장).

- 격자 꼭짓점의 기울기는 bpcg.core.hashing 의 splitmix64 정수 해시로 고릅니다.
  numpy 난수열을 쓰지 않으므로 맥·윈도우·리눅스에서 같은 시드로 같은 값이 나옵니다.
- 기울기는 Perlin(2002) 개선판의 12방향 (±1, ±1, 0) 류이고, 보간은 퀸틱 6t⁵ − 15t⁴ + 10t³ 입니다.
  값은 대략 [-1, 1] (이론상 최대 약 1.04) 이고, 정수 격자점에서는 0 입니다.
- 노이즈는 3D 좌표에서 직접 읽으므로 큐브 면 경계라는 개념이 없습니다(가이드 1장).
- 점마다 독립이라 prange 로 나눠도 결과가 스레드 수와 상관없이 같습니다.
"""

import numpy as np
from numba import njit, prange

from bpcg.core.hashing import hash3, hash_unit, splitmix64

# Perlin 2002 의 12개 기울기 (정육면체 모서리 중점 방향).
_GRAD3 = np.array(
    [
        [1.0, 1.0, 0.0],
        [-1.0, 1.0, 0.0],
        [1.0, -1.0, 0.0],
        [-1.0, -1.0, 0.0],
        [1.0, 0.0, 1.0],
        [-1.0, 0.0, 1.0],
        [1.0, 0.0, -1.0],
        [-1.0, 0.0, -1.0],
        [0.0, 1.0, 1.0],
        [0.0, -1.0, 1.0],
        [0.0, 1.0, -1.0],
        [0.0, -1.0, -1.0],
    ]
)
_S32 = np.uint64(32)
_N_GRAD = np.uint64(12)

# 해시 용도 번호 (graph.py 의 101, 102 와 겹치지 않게).
_STREAM_OCTAVE = 201  # 옥타브별 시드
_STREAM_OFFSET = 210  # 옥타브별 좌표 이동 (축마다 +0, +1, +2)
_STREAM_VECTOR = 220  # vector_fbm3 의 성분별 시드

# 옥타브마다 좌표를 [0, 64)³ 안에서 옮깁니다. 정수 격자점(노이즈 0)이 옥타브끼리 겹치지 않게 합니다.
_OFFSET_SPAN = 64.0


@njit(cache=True, inline="always")
def _fade(t: float) -> float:
    """퀸틱 보간 곡선 6t⁵ − 15t⁴ + 10t³ (1·2차 도함수가 격자 경계에서 0)."""
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0)


@njit(cache=True, inline="always")
def _grad_dot(h: np.uint64, dx: float, dy: float, dz: float) -> float:
    """해시 h 로 고른 기울기와 (dx, dy, dz) 의 내적."""
    # 위쪽 32비트에 12를 곱하고 다시 32비트 내리면 0..11 이 거의 고르게 나옵니다.
    k = ((h >> _S32) * _N_GRAD) >> _S32
    return _GRAD3[k, 0] * dx + _GRAD3[k, 1] * dy + _GRAD3[k, 2] * dz


@njit(cache=True)
def _noise3_mixed(x: float, y: float, z: float, s: np.uint64) -> float:
    """이미 섞은 시드 s 로 3D 그래디언트 노이즈 한 값."""
    fx = np.floor(x)
    fy = np.floor(y)
    fz = np.floor(z)
    ix = np.int64(fx)
    iy = np.int64(fy)
    iz = np.int64(fz)
    tx = x - fx
    ty = y - fy
    tz = z - fz

    # 꼭짓점 해시를 x → y → z 순으로 섞어 splitmix 횟수를 줄입니다 (8꼭짓점에 14번).
    hx0 = splitmix64(s ^ np.uint64(ix))
    hx1 = splitmix64(s ^ np.uint64(ix + 1))
    hx0y0 = splitmix64(hx0 ^ np.uint64(iy))
    hx0y1 = splitmix64(hx0 ^ np.uint64(iy + 1))
    hx1y0 = splitmix64(hx1 ^ np.uint64(iy))
    hx1y1 = splitmix64(hx1 ^ np.uint64(iy + 1))
    z0 = np.uint64(iz)
    z1 = np.uint64(iz + 1)

    n000 = _grad_dot(splitmix64(hx0y0 ^ z0), tx, ty, tz)
    n100 = _grad_dot(splitmix64(hx1y0 ^ z0), tx - 1.0, ty, tz)
    n010 = _grad_dot(splitmix64(hx0y1 ^ z0), tx, ty - 1.0, tz)
    n110 = _grad_dot(splitmix64(hx1y1 ^ z0), tx - 1.0, ty - 1.0, tz)
    n001 = _grad_dot(splitmix64(hx0y0 ^ z1), tx, ty, tz - 1.0)
    n101 = _grad_dot(splitmix64(hx1y0 ^ z1), tx - 1.0, ty, tz - 1.0)
    n011 = _grad_dot(splitmix64(hx0y1 ^ z1), tx, ty - 1.0, tz - 1.0)
    n111 = _grad_dot(splitmix64(hx1y1 ^ z1), tx - 1.0, ty - 1.0, tz - 1.0)

    u = _fade(tx)
    v = _fade(ty)
    w = _fade(tz)
    nx00 = n000 + u * (n100 - n000)
    nx10 = n010 + u * (n110 - n010)
    nx01 = n001 + u * (n101 - n001)
    nx11 = n011 + u * (n111 - n011)
    nxy0 = nx00 + v * (nx10 - nx00)
    nxy1 = nx01 + v * (nx11 - nx01)
    return nxy0 + w * (nxy1 - nxy0)


@njit(cache=True)
def gradient_noise3(x: float, y: float, z: float, seed: int) -> float:
    """정수 격자 해시 3D 그래디언트 노이즈 ν(x, y, z) 한 값. 값은 대략 [-1, 1].

    x, y, z: 좌표 (단위 없음, 정수 간격 1 이 노이즈 한 칸). seed: 정수 시드 (int64 범위).
    numba 함수라서 다른 커널 안에서도 부를 수 있습니다.
    """
    return _noise3_mixed(x, y, z, splitmix64(np.uint64(seed)))


@njit(cache=True, parallel=True)
def _fbm_kernel(
    points: np.ndarray,
    seeds: np.ndarray,
    offsets: np.ndarray,
    freqs: np.ndarray,
    amps: np.ndarray,
    inv_norm: float,
    out: np.ndarray,
) -> None:
    """points (M,3), seeds (C,O) uint64, offsets (C,O,3), freqs (O,), amps (O,) → out (M,C)."""
    n_points = points.shape[0]
    n_comp = seeds.shape[0]
    n_oct = seeds.shape[1]
    for k in prange(n_points):
        px = points[k, 0]
        py = points[k, 1]
        pz = points[k, 2]
        for c in range(n_comp):
            acc = 0.0
            for o in range(n_oct):
                f = freqs[o]
                acc += amps[o] * _noise3_mixed(
                    px * f + offsets[c, o, 0],
                    py * f + offsets[c, o, 1],
                    pz * f + offsets[c, o, 2],
                    seeds[c, o],
                )
            out[k, c] = acc * inv_norm


def _check_seed(seed: int) -> int:
    if isinstance(seed, bool) or not isinstance(seed, int | np.integer):
        raise ValueError(f"시드는 정수여야 합니다: {seed!r}")
    seed = int(seed)
    if not (-(2**63) <= seed < 2**63):
        raise ValueError(f"시드는 int64 범위여야 합니다: {seed}")
    return seed


def _check_points(points: np.ndarray) -> np.ndarray:
    pts = np.ascontiguousarray(points, dtype=np.float64)
    if pts.ndim != 2 or pts.shape[1] != 3:
        raise ValueError(f"points 는 (M, 3) 배열이어야 합니다: 받은 모양 {pts.shape}")
    if not np.isfinite(pts).all():
        raise ValueError("points 에 NaN 이나 inf 가 있습니다")
    return pts


def _octave_tables(
    seed: int, n_comp: int, octaves: int, gain: float, lacunarity: float, frequency: float
) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray, float]:
    """옥타브별 시드·좌표 이동·주파수·진폭과 정규화 계수 1/Σg^o."""
    if isinstance(octaves, bool) or not isinstance(octaves, int | np.integer) or octaves < 1:
        raise ValueError(f"octaves 는 1 이상의 정수여야 합니다: {octaves!r}")
    for name, val in (("gain", gain), ("lacunarity", lacunarity), ("frequency", frequency)):
        if not (np.isfinite(val) and val > 0.0):
            raise ValueError(f"{name} 은 0 보다 큰 유한한 값이어야 합니다: {val}")
    seeds = np.empty((n_comp, octaves), dtype=np.uint64)
    offsets = np.empty((n_comp, octaves, 3), dtype=np.float64)
    for c in range(n_comp):
        # 성분마다 다른 시드 (fbm3 는 성분 하나, 원래 시드 그대로).
        if n_comp == 1:
            comp_seed = np.int64(seed)
        else:
            comp_seed = np.uint64(hash3(np.int64(seed), np.int64(c), np.int64(_STREAM_VECTOR)))
            comp_seed = comp_seed.view(np.int64)
        for o in range(octaves):
            seeds[c, o] = hash3(comp_seed, np.int64(o), np.int64(_STREAM_OCTAVE))
            for ax in range(3):
                u = hash_unit(comp_seed, np.int64(o), np.int64(_STREAM_OFFSET + ax))
                offsets[c, o, ax] = u * _OFFSET_SPAN
    # 거듭제곱 대신 곱셈을 되풀이해 플랫폼마다 같은 값이 나오게 합니다
    # (pow 는 플랫폼마다 ulp 단위로 다를 수 있음).
    freqs = np.empty(octaves, dtype=np.float64)
    amps = np.empty(octaves, dtype=np.float64)
    f = float(frequency)
    a = 1.0
    total = 0.0
    for o in range(octaves):
        freqs[o] = f
        amps[o] = a
        total += a
        f *= float(lacunarity)
        a *= float(gain)
    return seeds, offsets, freqs, amps, 1.0 / total


def fbm3(
    points: np.ndarray,
    seed: int,
    octaves: int = 5,
    gain: float = 0.5,
    lacunarity: float = 2.0,
    frequency: float = 1.0,
) -> np.ndarray:
    """구면·공간 점에서 읽는 fbm ξ(p) = Σ g^o ν(λ·L^o·p + o_o) / Σ g^o (가이드 5장).

    points: (M, 3) 좌표 (보통 단위 구 위의 점, 단위 없음). seed: 정수 시드.
    octaves: 옥타브 수 L_o. gain: 옥타브마다 진폭 비율 g. lacunarity: 주파수 비율 L (가이드 2).
    frequency: 기본 주파수 λ. 옥타브마다 시드와 좌표 이동 o_o 를 해시로 바꿉니다.
    반환: (M,) float64, 값은 대략 [-1, 1].
    """
    seed = _check_seed(seed)
    pts = _check_points(points)
    seeds, offsets, freqs, amps, inv_norm = _octave_tables(
        seed, 1, octaves, gain, lacunarity, frequency
    )
    out = np.empty((pts.shape[0], 1), dtype=np.float64)
    _fbm_kernel(pts, seeds, offsets, freqs, amps, inv_norm, out)
    return out[:, 0]


def vector_fbm3(
    points: np.ndarray,
    seed: int,
    octaves: int = 5,
    gain: float = 0.5,
    lacunarity: float = 2.0,
    frequency: float = 1.0,
) -> np.ndarray:
    """서로 다른 시드의 fbm 세 개를 묶은 벡터 노이즈 ξ⃗(p) (판 경계 흔들기 등).

    인자는 fbm3 와 같습니다. 성분 k 의 시드는 hash3(seed, k, 용도 번호) 로 정합니다.
    반환: (M, 3) float64, 성분마다 값은 대략 [-1, 1].
    """
    seed = _check_seed(seed)
    pts = _check_points(points)
    seeds, offsets, freqs, amps, inv_norm = _octave_tables(
        seed, 3, octaves, gain, lacunarity, frequency
    )
    out = np.empty((pts.shape[0], 3), dtype=np.float64)
    _fbm_kernel(pts, seeds, offsets, freqs, amps, inv_norm, out)
    return out
