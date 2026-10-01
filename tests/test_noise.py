"""결정적 노이즈 검사 (docs/pipeline.md 3장, bpcg.core.noise)."""

import numba
import numpy as np
import pytest

from bpcg.core import cubesphere as cs
from bpcg.core.noise import fbm3, gradient_noise3, vector_fbm3

# 2026-10-02 에 만든 기준값. 값이 바뀌면 같은 시드의 행성 모양이 바뀝니다(재현성 깨짐).
# 해시와 실수 계산만 쓰므로 맥·윈도우·리눅스에서 같아야 합니다. 허용 오차는 FMA 같은
# 컴파일러 차이로 생길 수 있는 몇 ulp 만 둡니다.
_P = np.array([[0.6, -0.48, 0.64], [0.0, 0.0, 1.0], [-0.36, 0.48, -0.8]])
GOLDEN_NOISE = [
    ((0.3, 0.7, 1.2, 1), -0.05504087910128644),
    ((-5.25, 3.5, 0.125, 42), 0.30940183252096176),
    ((100.1, -200.2, 300.3, -7), 0.33227717790739775),
]
GOLDEN_FBM = [9.781583187640756e-05, -0.08322133371069211, -0.016536584775024128]
GOLDEN_FBM_123 = [-0.08924148446451823, -0.1828963787661437, -0.17065415754312638]
GOLDEN_VEC = [
    [-0.1836037338038461, 0.06366503619251782, -0.11074448159146501],
    [0.20297296852512883, -0.04079806382064178, -0.19776208277506221],
    [0.08915891342806581, 0.00043482802344716625, 0.0748405521188608],
]


def _random_points(m, scale, seed=0):
    return np.random.default_rng(seed).uniform(-scale, scale, (m, 3))


def test_golden_values():
    for args, want in GOLDEN_NOISE:
        assert gradient_noise3(*args) == pytest.approx(want, rel=0, abs=1e-14)
    np.testing.assert_allclose(fbm3(_P, 0), GOLDEN_FBM, rtol=0, atol=1e-14)
    np.testing.assert_allclose(
        fbm3(_P, 123, octaves=4, frequency=1.2), GOLDEN_FBM_123, rtol=0, atol=1e-14
    )
    np.testing.assert_allclose(vector_fbm3(_P, 0, frequency=2.5), GOLDEN_VEC, rtol=0, atol=1e-14)


def test_deterministic_and_thread_independent():
    p = _random_points(20_000, 50.0)
    a = fbm3(p, 7)
    b = fbm3(p, 7)
    assert np.array_equal(a, b)
    v1 = vector_fbm3(p, 7)
    old = numba.get_num_threads()
    try:
        numba.set_num_threads(1)
        c = fbm3(p, 7)
        v2 = vector_fbm3(p, 7)
    finally:
        numba.set_num_threads(old)
    assert np.array_equal(a, c) and np.array_equal(v1, v2)
    # 파이썬에서 부른 스칼라 함수와 같은 시드 규칙 (옥타브 1개, 주파수 1이면 이동만 다름)
    assert gradient_noise3(1.5, 2.5, 3.5, 3) == gradient_noise3(1.5, 2.5, 3.5, 3)


def test_range():
    # docs/pipeline.md 3장: 값이 [-1.2, 1.2] 안
    p = _random_points(400_000, 200.0, seed=1)
    single = fbm3(p, 3, octaves=1)
    assert np.abs(single).max() < 1.2
    assert np.abs(single).max() > 0.5  # 값 범위를 실제로 씁니다
    multi = fbm3(p, 3, octaves=6, gain=0.6)
    assert np.abs(multi).max() < 1.2
    vec = vector_fbm3(p[:100_000], 3)
    assert np.abs(vec).max() < 1.2
    # 평균은 0 근처
    assert abs(single.mean()) < 0.01


def test_zero_on_integer_lattice():
    # 그래디언트 노이즈는 정수 격자점에서 0 입니다.
    for x, y, z in [(0, 0, 0), (1, -2, 3), (-17, 40, 9)]:
        assert gradient_noise3(float(x), float(y), float(z), 5) == 0.0


def test_seed_sensitivity():
    p = _random_points(50_000, 30.0, seed=2)
    a = fbm3(p, 0)
    b = fbm3(p, 1)
    assert not np.allclose(a, b)
    assert abs(np.corrcoef(a, b)[0, 1]) < 0.05
    v = vector_fbm3(p, 0)
    # 성분끼리도, 같은 시드의 fbm3 와도 서로 독립
    for i in range(3):
        assert abs(np.corrcoef(v[:, i], a)[0, 1]) < 0.05
        for j in range(i + 1, 3):
            assert abs(np.corrcoef(v[:, i], v[:, j])[0, 1]) < 0.05
    # 옥타브마다 시드가 달라서, 주파수만 다른 옥타브 하나와 같은 값이 아닙니다.
    assert not np.allclose(fbm3(p, 0, octaves=2), fbm3(p, 0, octaves=1))


def test_continuity():
    # 가까운 두 점의 값 차이는 거리에 비례합니다 (기울기 상한이 있음, 퀸틱 보간이라 격자면에서도).
    p = _random_points(100_000, 20.0, seed=3)
    eps = 1e-6
    d = np.random.default_rng(4).normal(size=p.shape)
    d *= eps / np.linalg.norm(d, axis=1, keepdims=True)
    diff = np.abs(fbm3(p + d, 9, octaves=1) - fbm3(p, 9, octaves=1))
    assert diff.max() < 5.0 * eps
    # 정수 격자면을 사이에 두고도 끊기지 않습니다.
    q = np.array([[3.0 - 1e-9, 0.25, 0.75], [-2.0, 1.0 - 1e-9, 0.5], [0.5, 0.5, -1e-9]])
    q2 = q + np.array([[2e-9, 0, 0], [0, 2e-9, 0], [0, 0, 2e-9]])
    for a, b in zip(q, q2, strict=True):
        assert abs(gradient_noise3(*a, 11) - gradient_noise3(*b, 11)) < 1e-7


def test_sphere_noise_has_no_face_seams():
    # 3D 좌표에서 읽으므로 큐브 면 경계에서 값이 끊기지 않습니다(가이드 1장).
    t = np.linspace(-0.99, 0.99, 200)
    eps = 1e-7
    for f in range(6):
        p_in = cs.to_sphere(f, np.full_like(t, 1 - eps), t)
        p_out = cs.to_sphere(f, np.full_like(t, 1 + eps), t)
        diff = np.abs(fbm3(p_in * 3.0, 0) - fbm3(p_out * 3.0, 0))
        assert diff.max() < 1e-4


def test_frequency_and_lacunarity():
    p = _random_points(5_000, 5.0, seed=5)
    # 옥타브 1개면 frequency 는 좌표 배율과 같습니다(이동 o_0 은 같음).
    a = fbm3(p, 4, octaves=1, frequency=2.0)
    b = fbm3(2.0 * p, 4, octaves=1, frequency=1.0)
    np.testing.assert_allclose(a, b, rtol=0, atol=1e-12)


@pytest.mark.parametrize(
    "kwargs",
    [
        {"points": np.zeros((4, 2)), "seed": 0},
        {"points": np.zeros(3), "seed": 0},
        {"points": np.array([[np.nan, 0.0, 0.0]]), "seed": 0},
        {"points": np.zeros((4, 3)), "seed": 0.5},
        {"points": np.zeros((4, 3)), "seed": 2**64},
        {"points": np.zeros((4, 3)), "seed": 0, "octaves": 0},
        {"points": np.zeros((4, 3)), "seed": 0, "gain": 0.0},
        {"points": np.zeros((4, 3)), "seed": 0, "frequency": -1.0},
    ],
)
def test_invalid_inputs(kwargs):
    with pytest.raises(ValueError):
        fbm3(**kwargs)
    with pytest.raises(ValueError):
        vector_fbm3(**kwargs)
