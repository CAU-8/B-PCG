"""결정적 정수 해시. 맥·윈도우·리눅스에서 같은 시드로 같은 값이 나옵니다.

numpy 난수열은 버전마다 바뀔 수 있어서(NEP 19), 지형 모양에 영향을 주는 값(노드 흔들기,
노이즈 격자의 기울기)은 모두 여기 해시로 만듭니다. numba 커널 안에서도 그대로 부를 수 있습니다.
"""

import numpy as np
from numba import njit

_M1 = np.uint64(0xBF58476D1CE4E5B9)
_M2 = np.uint64(0x94D049BB133111EB)
_GOLD = np.uint64(0x9E3779B97F4A7C15)
_S30 = np.uint64(30)
_S27 = np.uint64(27)
_S31 = np.uint64(31)
_S11 = np.uint64(11)
_INV53 = 1.0 / 9007199254740992.0  # 2^-53


@njit(cache=True, inline="always")
def splitmix64(x: np.uint64) -> np.uint64:
    """splitmix64 섞기 한 번."""
    z = x + _GOLD
    z = (z ^ (z >> _S30)) * _M1
    z = (z ^ (z >> _S27)) * _M2
    return z ^ (z >> _S31)


@njit(cache=True, inline="always")
def hash3(a: np.int64, b: np.int64, c: np.int64) -> np.uint64:
    """정수 세 개를 64비트 해시 하나로 섞습니다."""
    h = splitmix64(np.uint64(a))
    h = splitmix64(h ^ np.uint64(b))
    return splitmix64(h ^ np.uint64(c))


@njit(cache=True, inline="always")
def hash_unit(a: np.int64, b: np.int64, c: np.int64) -> float:
    """정수 세 개 → [0, 1) 균등 실수."""
    return float(hash3(a, b, c) >> _S11) * _INV53


@njit(cache=True)
def hash_uniform_array(ids: np.ndarray, seed: int, stream: int) -> np.ndarray:
    """셀 번호 배열마다 [0, 1) 균등 실수 하나. stream 으로 용도를 나눕니다."""
    out = np.empty(ids.shape[0], dtype=np.float64)
    for k in range(ids.shape[0]):
        out[k] = hash_unit(np.int64(ids[k]), np.int64(seed), np.int64(stream))
    return out
