"""가장 가까운 출발 칸 거리 검사 (docs/pipeline.md 3장, bpcg.core.distance)."""

import numpy as np
import pytest
from scipy.sparse import csr_matrix
from scipy.sparse.csgraph import dijkstra
from scipy.spatial import cKDTree

from bpcg.core.distance import nearest_source, nearest_source_values
from bpcg.core.graph import CellGraph, flat_graph, sphere_graph
from bpcg.core.hashing import hash_uniform_array

R_EARTH = 6_371_000.0


def _great_circle(p, q, R):
    p = p / np.linalg.norm(p, axis=1, keepdims=True)
    q = q / np.linalg.norm(q, axis=1, keepdims=True)
    return R * np.arctan2(np.linalg.norm(np.cross(p, q), axis=1), np.sum(p * q, axis=1))


def _graph_path_length(g, source):
    ok = g.nbr >= 0
    rows = np.repeat(np.arange(g.n_cells), ok.sum(axis=1))
    mat = csr_matrix((g.dist[ok], (rows, g.nbr[ok])), shape=(g.n_cells, g.n_cells))
    return dijkstra(mat, indices=source)


def _random_sources(n_cells, fraction, seed):
    return hash_uniform_array(np.arange(n_cells, dtype=np.int64), seed, 1) < fraction


def test_flat_single_source_matches_euclid():
    # docs/pipeline.md 3장: 평면 한 점 출발이면 유클리드 거리와 상대 오차 1% 이내
    g = flat_graph(81, 101, 100.0, jitter=0.4, seed=1)
    s = 40 * 101 + 50
    is_src = np.zeros(g.n_cells, dtype=bool)
    is_src[s] = True
    dist, src = nearest_source(g, is_src)
    assert dist.dtype == np.float64 and src.dtype == np.int64
    assert np.all(src == s) and dist[s] == 0.0
    true = np.linalg.norm(g.pos - g.pos[s], axis=1)
    away = true > 2 * g.spacing
    assert np.max(np.abs(dist - true)[away] / true[away]) < 0.01
    # 그래프 경로 길이였다면 등거리선이 팔각형이 되어 몇 % 틀립니다(이 방법을 쓰는 이유).
    path = _graph_path_length(g, s)
    assert np.max(np.abs(path - true)[away] / true[away]) > 0.03


@pytest.mark.parametrize("jitter", [0.0, 0.4])
def test_sphere_single_source_matches_great_circle(jitter):
    # docs/pipeline.md 3장: 구면 한 점 출발이면 대원 거리와 1% 이내 (면 경계·꼭짓점 너머 포함)
    n = 32
    g = sphere_graph(n, R_EARTH, jitter=jitter, seed=3)
    s = 4 * n * n + 3 * n + 29  # +Z 면의 꼭짓점 근처
    is_src = np.zeros(g.n_cells, dtype=bool)
    is_src[s] = True
    dist, src = nearest_source(g, is_src)
    assert np.all(src == s)
    true = _great_circle(g.pos, g.pos[[s]], R_EARTH)
    away = true > 2 * g.spacing
    assert np.max(np.abs(dist - true)[away] / true[away]) < 0.01
    assert dist.max() == pytest.approx(true.max(), rel=1e-12)  # 맞은편까지 닿음


@pytest.mark.parametrize("kind", ["flat", "sphere"])
def test_multiple_sources_pick_nearest(kind):
    if kind == "flat":
        g = flat_graph(120, 140, 50.0, jitter=0.4, seed=4)
    else:
        g = sphere_graph(40, R_EARTH, jitter=0.4, seed=4)
    is_src = _random_sources(g.n_cells, 0.01, seed=11)
    dist, src = nearest_source(g, is_src)
    ids = np.nonzero(is_src)[0]
    _, k = cKDTree(g.pos[ids]).query(g.pos)  # 구면에서도 현 길이 순서 = 대원 거리 순서
    best = ids[k]
    if kind == "flat":
        true = np.linalg.norm(g.pos - g.pos[best], axis=1)
    else:
        true = _great_circle(g.pos, g.pos[best], R_EARTH)
    assert np.all(src >= 0) and np.all(is_src[src])
    # 벡터 전파의 알려진 한계로 드물게 두 번째로 가까운 출발 칸이 뽑힐 수 있습니다
    # (칸 크기의 일부 오차).
    assert np.mean(src != best) < 1e-3
    assert np.all(dist >= true - 1e-6)
    assert np.max(dist - true) < 0.25 * g.spacing
    away = true > 2 * g.spacing
    assert np.max((dist - true)[away] / true[away]) < 0.01
    assert np.all(dist[is_src] == 0) and np.array_equal(src[is_src], ids)


def test_ties_go_to_smaller_source_id():
    g = flat_graph(1, 5, 10.0)
    is_src = np.zeros(5, dtype=bool)
    is_src[[0, 4]] = True
    dist, src = nearest_source(g, is_src)
    np.testing.assert_allclose(dist, [0, 10, 20, 10, 0])
    assert src.tolist() == [0, 0, 0, 4, 4]
    # 2차원에서도: 정사각형 격자 가운데 줄은 번호가 작은 위쪽 출발 칸
    g2 = flat_graph(5, 5, 1.0)
    is2 = np.zeros(25, dtype=bool)
    is2[[2, 22]] = True
    _, src2 = nearest_source(g2, is2)
    assert np.all(src2.reshape(5, 5)[2] == 2)


def test_max_dist():
    g = sphere_graph(24, R_EARTH, jitter=0.4, seed=8)
    is_src = _random_sources(g.n_cells, 0.002, seed=5)
    full_d, full_s = nearest_source(g, is_src)
    limit = 3.0 * g.spacing
    dist, src = nearest_source(g, is_src, max_dist=limit)
    inside = full_d <= limit
    assert np.array_equal(src[inside], full_s[inside])
    np.testing.assert_array_equal(dist[inside], full_d[inside])
    assert np.all(np.isinf(dist[~inside])) and np.all(src[~inside] == -1)
    assert inside.sum() < g.n_cells  # 실제로 잘렸습니다
    # 평면도 같은 규칙
    f = flat_graph(30, 30, 10.0)
    fs = np.zeros(900, dtype=bool)
    fs[0] = True
    d2, s2 = nearest_source(f, fs, max_dist=55.0)
    true = np.linalg.norm(f.pos - f.pos[0], axis=1)
    assert np.array_equal(np.isfinite(d2), true <= 55.0)
    assert np.all(s2[true > 55.0] == -1)


def test_no_sources_and_unreachable():
    g = flat_graph(4, 4, 1.0)
    dist, src = nearest_source(g, np.zeros(16, dtype=bool))
    assert np.all(np.isinf(dist)) and np.all(src == -1)
    # 이웃이 하나도 없는 두 칸짜리 그래프: 출발 칸이 아닌 칸에는 닿지 않습니다.
    lone = CellGraph(
        kind="flat",
        shape=(1, 2),
        pos=np.array([[0.0, 0.0, 0.0], [1.0, 0.0, 0.0]]),
        nbr=np.full((2, 8), -1, dtype=np.int32),
        dist=np.full((2, 8), np.inf),
        area=np.ones(2),
        spacing=1.0,
    )
    d, s = nearest_source(lone, np.array([True, False]))
    assert d.tolist() == [0.0, np.inf] and s.tolist() == [0, -1]


def test_nearest_source_values():
    g = flat_graph(1, 6, 1.0)
    is_src = np.array([True, False, False, False, False, True])
    vals = np.array([10.0, 0, 0, 0, 0, 20.0])
    dist, src, got = nearest_source_values(g, is_src, vals)
    assert got.tolist() == [10, 10, 10, 20, 20, 20]
    _, _, got_i = nearest_source_values(g, is_src, np.arange(6, dtype=np.int32) * 3, max_dist=1.5)
    assert got_i.tolist() == [0, 0, -1, -1, 15, 15] and got_i.dtype == np.int32
    _, _, got_f = nearest_source_values(g, is_src, vals, max_dist=1.5)
    assert np.isnan(got_f[2]) and np.isnan(got_f[3]) and got_f[1] == 10.0
    # (N, k) 값도 받습니다.
    _, _, got2 = nearest_source_values(g, is_src, np.stack([vals, -vals], axis=1))
    assert got2.shape == (6, 2) and got2[2].tolist() == [10.0, -10.0]


def test_invalid_inputs():
    g = flat_graph(3, 3, 1.0)
    with pytest.raises(ValueError):
        nearest_source(g, np.zeros(9, dtype=np.int64))  # bool 이 아님
    with pytest.raises(ValueError):
        nearest_source(g, np.zeros(8, dtype=bool))  # 길이가 다름
    with pytest.raises(ValueError):
        nearest_source(g, np.zeros(9, dtype=bool), max_dist=-1.0)
    with pytest.raises(ValueError):
        nearest_source_values(g, np.zeros(9, dtype=bool), np.zeros(4))
