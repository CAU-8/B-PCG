"""큐브스피어 격자(가이드 1장) 검사. 2026-10-01 main.py 버그 수정 때 쓴 검사를 pytest로 옮긴 것."""

import numpy as np
import pytest

from bpcg.core import cubesphere as cs

# 가이드 1장 표의 면 기저 (u, v, n)
REF = [
    ((0, 1, 0), (0, 0, 1), (1, 0, 0)),
    ((0, -1, 0), (0, 0, 1), (-1, 0, 0)),
    ((0, 0, 1), (1, 0, 0), (0, 1, 0)),
    ((0, 0, -1), (1, 0, 0), (0, -1, 0)),
    ((1, 0, 0), (0, 1, 0), (0, 0, 1)),
    ((-1, 0, 0), (0, 1, 0), (0, 0, -1)),
]

U = np.array([cs.FACE_BASIS[f]["uf"] for f in range(6)])
V = np.array([cs.FACE_BASIS[f]["vf"] for f in range(6)])
N = np.array([cs.FACE_BASIS[f]["nf"] for f in range(6)])


def cell_of(p, n):
    """역사상의 독립 구현(검사용). 본 구현은 아직 없음."""
    p = p / np.linalg.norm(p, axis=-1, keepdims=True)
    f = np.argmax(p @ N.T, axis=-1)
    pn = np.sum(p * N[f], -1)
    a = 4 / np.pi * np.arctan(np.sum(p * U[f], -1) / pn)
    b = 4 / np.pi * np.arctan(np.sum(p * V[f], -1) / pn)
    i = np.minimum(np.floor(n * (a + 1) / 2).astype(int), n - 1)
    j = np.minimum(np.floor(n * (b + 1) / 2).astype(int), n - 1)
    return f * n * n + j * n + i


@pytest.mark.parametrize("f", range(6))
def test_face_basis_matches_guide_and_is_right_handed(f):
    u, v, n = REF[f]
    B = cs.FACE_BASIS[f]
    assert np.allclose(B["uf"], u) and np.allclose(B["vf"], v) and np.allclose(B["nf"], n)
    assert np.allclose(np.cross(B["uf"], B["vf"]), B["nf"])


@pytest.mark.parametrize("n", [1, 2, 5, 16, 64])
def test_grid_order_radius_and_area(n):
    R = 15.0
    g = cs.cubesphere_grid(n, R)
    total = 6 * n * n
    assert g.pos.shape == (total, 3) and g.n_cells == total
    # 셀 번호 c = f·n² + j·n + i, 셀 중심
    assert np.array_equal(cell_of(g.pos, n), np.arange(total))
    assert len(np.unique(np.round(g.pos / R, 12), axis=0)) == total
    assert np.allclose(np.linalg.norm(g.pos, axis=1), R)
    # 면적 합 = 4πR²
    assert g.area.shape == (n * n,) and g.area_full().shape == (total,)
    assert abs(g.area_full().sum() - 4 * np.pi * R * R) / (4 * np.pi * R * R) < 1e-12


def test_area_ratio_tends_to_sqrt2():
    A = cs.face_cell_areas(512, 1.0)
    assert abs(A.max() / A.min() - np.sqrt(2)) < 0.02


def test_cells_meet_across_face_edge():
    n = 32
    g = cs.cubesphere_grid(n, 1.0)
    edge = g.pos[5 * n + (n - 1)]
    d_in = cs.neighbor_distance(g.pos[5 * n + (n - 2)], edge, 1.0)
    d_cross = cs.neighbor_distance(edge[None, :], g.pos[n * n :], 1.0).min()
    assert abs(d_cross / d_in - 1) < 0.1


def test_neighbor_distance_vectorized_matches_arccos():
    g = cs.cubesphere_grid(32, 1.0)
    P1, P2 = g.pos[:100], g.pos[100:200]
    vec = cs.neighbor_distance(P1, P2, 2.0)
    loop = np.array([cs.neighbor_distance(a, b, 2.0) for a, b in zip(P1, P2, strict=True)])
    assert vec.shape == (100,) and np.allclose(vec, loop)
    assert np.allclose(vec, 2.0 * np.arccos(np.clip(np.sum(P1 * P2, 1), -1, 1)))


def test_neighbor_distance_tiny_separation_at_earth_radius():
    R = 6.371e6
    p = np.array([1.0, 0.0, 0.0])
    q = np.array([np.cos(1e-7), np.sin(1e-7), 0.0])  # 약 0.64 m
    assert abs(cs.neighbor_distance(p, q, R) - R * 1e-7) < 1e-6
