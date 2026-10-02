"""이웃 표와 셀 그래프 검사 (가이드 1장 '확인하기' 표, bpcg.core.cubesphere·graph)."""

import numpy as np
import pytest

from bpcg.core import cubesphere as cs
from bpcg.core.graph import flat_graph, sphere_graph

R_EARTH = 6_371_000.0


def _reverse_slot(nbr: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """이웃 쌍 (c, s) 마다 반대쪽에서 c 를 가리키는 슬롯 s2 (없으면 -1)."""
    c, s = np.nonzero(nbr >= 0)
    c2 = nbr[c, s]
    back = nbr[c2] == c[:, None]  # (K, 8)
    s2 = np.where(back.any(axis=1), back.argmax(axis=1), -1)
    return c, s, s2


@pytest.mark.parametrize("n", [2, 5, 16])
def test_neighbor_symmetry(n):
    # 가이드 1장: c' ∈ N(c) ⇔ c ∈ N(c')
    nbr = cs.neighbor_table(n)
    _, _, s2 = _reverse_slot(nbr)
    assert (s2 >= 0).all()
    # 자기 자신이나 같은 이웃이 두 번 나오지 않습니다.
    c = np.arange(nbr.shape[0])
    assert not (nbr == c[:, None]).any()
    for row in nbr[nbr.min(axis=1) >= 0][:500]:
        assert len(set(row.tolist())) == 8


@pytest.mark.parametrize("n", [2, 4, 16, 33])
def test_exactly_24_seven_neighbour_cells(n):
    # 가이드 1장: 꼭짓점 8개 × 3면 = 24칸만 이웃 7개, 나머지는 8개
    k = (cs.neighbor_table(n) >= 0).sum(axis=1)
    assert (k == 7).sum() == 24
    assert np.all(k[k != 7] == 8)


@pytest.mark.parametrize("n", [4, 16, 64])
@pytest.mark.parametrize("R", [1.0, R_EARTH])
def test_roundtrip_cell_of_pos(n, R):
    # 가이드 1장: 모든 c 에서 cell(p_c) = c (반지름을 곱한 위치에서도)
    g = cs.cubesphere_grid(n, R)
    assert np.array_equal(cs.cell_of(g.pos, n), np.arange(6 * n * n))


def test_seam_area_pairs_equal():
    # 가이드 1장: 면 경계를 사이에 둔 두 칸의 면적 상대 차이 < 1e-10 (거울 대칭 평균으로 1e-12 까지)
    n = 256
    g = cs.cubesphere_grid(n, 1.0)
    pairs = cs.cross_face_pairs(n)
    assert len(pairs) == 12 * n  # 큐브 모서리 12개 × n 쌍
    A = g.area_full()
    rel = np.abs(A[pairs[:, 0]] / A[pairs[:, 1]] - 1.0)
    assert rel.max() < 1e-12


def test_area_ratio_tends_to_sqrt2():
    # 가이드 1장: n 이 커질수록 max A / min A → √2
    gaps = []
    for n in (16, 64, 256, 1024):
        A = cs.face_cell_areas(n, 1.0)
        gaps.append(abs(A.max() / A.min() - np.sqrt(2.0)))
    assert all(g2 < g1 for g1, g2 in zip(gaps, gaps[1:], strict=False))
    assert gaps[-1] < 0.01


def test_area_sum_equals_sphere():
    # 가이드 1장: 면적 합 상대 오차 < 1e-6
    g = cs.cubesphere_grid(64, R_EARTH)
    total = g.area_full().sum()
    assert abs(total - 4 * np.pi * R_EARTH**2) / (4 * np.pi * R_EARTH**2) < 1e-6


def _check_graph_distances(g):
    nbr, dist = g.nbr, g.dist
    # 이웃이 없으면 inf, 있으면 양수 유한값
    assert np.all(np.isinf(dist[nbr < 0]))
    ok = nbr >= 0
    assert np.all(np.isfinite(dist[ok])) and np.all(dist[ok] > 0)
    # 두 칸 사이 거리는 어느 쪽에서 재도 같습니다.
    c, s, s2 = _reverse_slot(nbr)
    assert (s2 >= 0).all()
    np.testing.assert_allclose(dist[c, s], dist[nbr[c, s], s2], rtol=1e-12, atol=0)


@pytest.mark.parametrize("jitter", [0.0, 0.4])
def test_sphere_graph_distances(jitter):
    g = sphere_graph(16, R_EARTH, jitter=jitter, seed=5)
    assert g.kind == "sphere" and g.shape == (6, 16, 16) and g.n_cells == 6 * 16 * 16
    _check_graph_distances(g)
    np.testing.assert_allclose(np.linalg.norm(g.pos, axis=1), R_EARTH, rtol=1e-12)
    # 이웃 거리는 칸 크기 정도 (가장 먼 대각선도 칸 크기의 2배 안)
    d = g.dist[g.nbr >= 0]
    assert d.max() < 2.5 * g.spacing and d.min() > 0.2 * g.spacing


@pytest.mark.parametrize("jitter", [0.0, 0.4])
def test_flat_graph_distances(jitter):
    g = flat_graph(20, 30, 25.0, jitter=jitter, seed=2, origin=(1000.0, 500.0))
    assert g.kind == "flat" and g.shape == (20, 30) and g.n_cells == 600
    _check_graph_distances(g)
    # 가장자리 칸만 빈 슬롯이 있습니다.
    edge = np.zeros((20, 30), dtype=bool)
    edge[0, :] = edge[-1, :] = edge[:, 0] = edge[:, -1] = True
    assert np.array_equal(g.boundary_mask(), edge.ravel())
    if jitter == 0.0:
        # 0번 행이 북쪽 끝, c = j·nx + i
        assert g.pos[0, 0] == pytest.approx(1012.5) and g.pos[0, 1] == pytest.approx(487.5)
        assert g.pos[31, 0] == pytest.approx(1037.5) and g.pos[31, 1] == pytest.approx(462.5)
        np.testing.assert_allclose(g.dist[31, [1, 3, 4, 6]], 25.0)
        np.testing.assert_allclose(g.dist[31, [0, 2, 5, 7]], 25.0 * np.sqrt(2.0))


def test_jitter_is_deterministic():
    a = sphere_graph(16, R_EARTH, jitter=0.4, seed=5)
    b = sphere_graph(16, R_EARTH, jitter=0.4, seed=5)
    c = sphere_graph(16, R_EARTH, jitter=0.4, seed=6)
    z = sphere_graph(16, R_EARTH, jitter=0.0, seed=5)
    assert np.array_equal(a.pos, b.pos) and np.array_equal(a.dist, b.dist)
    assert not np.allclose(a.pos, c.pos)
    np.testing.assert_allclose(z.pos, cs.cubesphere_grid(16, R_EARTH).pos, rtol=0, atol=1e-6)
    # 흔들어도 면적과 이웃 표는 그대로
    assert np.array_equal(a.area, z.area) and np.array_equal(a.nbr, z.nbr)
    # 흔든 대표점은 여전히 제 칸 안에 있습니다 (jitter < 1 이면 칸 반 폭 안).
    assert np.array_equal(cs.cell_of(a.pos, 16), np.arange(a.n_cells))

    fa = flat_graph(10, 12, 50.0, jitter=0.4, seed=9)
    fb = flat_graph(10, 12, 50.0, jitter=0.4, seed=9)
    fc = flat_graph(10, 12, 50.0, jitter=0.4, seed=10)
    f0 = flat_graph(10, 12, 50.0)
    assert np.array_equal(fa.pos, fb.pos)
    assert not np.allclose(fa.pos, fc.pos)
    shift = np.abs(fa.pos - f0.pos)[:, :2]
    assert shift.max() <= 0.2 * 50.0 + 1e-9 and shift.max() > 0
