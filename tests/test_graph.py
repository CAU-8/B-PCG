"""격자 검사 (가이드 1장 '확인하기' 표, docs/pipeline.md 3장): C# 이 쓴 graph.npz 와 거리 필드.

큐브스피어 면 기저·셀 번호·이웃 표·넓이·이웃 거리(구면 대원 거리, 평면 직선 거리), 가장 가까운
출발 칸 거리(dist_*_m)의 성질을 결과 파일에서 봅니다. 노이즈·고스트 보간 같은 함수 단위 값은
tests/Bpcg.Tests 의 golden 대조 시험이 봅니다.
"""

import numpy as np
import pytest
from bundles import load_config_dict

# 가이드 1장 표의 면 기저 (u, v, n)
REF = [
    ((0, 1, 0), (0, 0, 1), (1, 0, 0)),
    ((0, -1, 0), (0, 0, 1), (-1, 0, 0)),
    ((0, 0, 1), (1, 0, 0), (0, 1, 0)),
    ((0, 0, -1), (1, 0, 0), (0, -1, 0)),
    ((1, 0, 0), (0, 1, 0), (0, 0, 1)),
    ((-1, 0, 0), (0, 1, 0), (0, 0, -1)),
]


def _valid_pairs(nbr: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    i, k = np.nonzero(nbr >= 0)
    return i, nbr[i, k]


def _cell_of(p: np.ndarray, n: int) -> np.ndarray:
    """역사상의 독립 구현 (가이드 1장): 방향 → 면·열·행 → 셀 번호."""
    N = np.array([r[2] for r in REF], float)
    U = np.array([r[0] for r in REF], float)
    V = np.array([r[1] for r in REF], float)
    p = p / np.linalg.norm(p, axis=-1, keepdims=True)
    f = np.argmax(p @ N.T, axis=-1)
    pn = np.sum(p * N[f], -1)
    a = 4 / np.pi * np.arctan(np.sum(p * U[f], -1) / pn)
    b = 4 / np.pi * np.arctan(np.sum(p * V[f], -1) / pn)
    i = np.minimum(np.floor(n * (a + 1) / 2).astype(int), n - 1)
    j = np.minimum(np.floor(n * (b + 1) / 2).astype(int), n - 1)
    return f * n * n + j * n + i


@pytest.mark.parametrize("f", range(6))
def test_face_basis_matches_guide_and_is_right_handed(planet, f):
    fb = planet.manifest["face_basis"]
    u, v, n = REF[f]
    assert np.allclose(fb["u"][f], u) and np.allclose(fb["v"][f], v) and np.allclose(fb["n"][f], n)
    assert np.allclose(np.cross(fb["u"][f], fb["v"][f]), fb["n"][f])


def test_sphere_radius_area_and_cell_order(planet):
    g = planet.graph
    cfg = load_config_dict()
    n = int(cfg["profile"]["grid"]["l0_n_per_face"])
    assert g.shape == (6, n, n) and g.n_cells == 6 * n * n
    r = np.linalg.norm(g.pos, axis=1)
    assert np.allclose(r, g.radius_m, rtol=1e-12)
    assert g.area.sum() == pytest.approx(4 * np.pi * g.radius_m**2, rel=1e-9)
    assert (g.area > 0).all() and g.area.max() / g.area.min() < 2.0
    # 흔들린(jitter) 칸 중심은 제 칸이나 이웃 칸 안에 있음 (역사상이 자기 또는 이웃 번호)
    c = _cell_of(g.pos, n)
    assert ((c == np.arange(g.n_cells)) | (g.nbr == c[:, None]).any(axis=1)).all()
    assert (c == np.arange(g.n_cells)).mean() > 0.9


def test_neighbor_table_symmetric_with_24_corner_cells(planet):
    g = planet.graph
    i, j = _valid_pairs(g.nbr)
    pairs = set(zip(i.tolist(), j.tolist(), strict=True))
    assert all((b, a) in pairs for a, b in pairs)
    assert (i != j).all()
    counts = (g.nbr >= 0).sum(axis=1)
    # 큐브 꼭짓점 8 개 둘레의 세 칸은 이웃이 7 개, 나머지는 8 개
    assert np.bincount(counts).tolist()[7:] == [24, g.n_cells - 24]


def test_sphere_neighbor_distance_is_great_circle(planet):
    g = planet.graph
    i, j = _valid_pairs(g.nbr)
    u = g.pos / np.linalg.norm(g.pos, axis=1, keepdims=True)
    chord = np.linalg.norm(u[i] - u[j], axis=1)
    great = 2.0 * g.radius_m * np.arcsin(np.clip(chord / 2.0, 0.0, 1.0))
    assert np.allclose(g.dist[g.nbr >= 0], great, rtol=1e-9)
    assert np.isinf(g.dist[g.nbr < 0]).all()  # 없는 이웃 자리는 inf


def test_flat_neighbor_distance_is_euclid(hero):
    g = hero.graph
    assert g.kind == "flat" and g.radius_m is None
    i, j = _valid_pairs(g.nbr)
    euclid = np.linalg.norm(g.pos[i, :2] - g.pos[j, :2], axis=1)
    assert np.allclose(g.dist[g.nbr >= 0], euclid, rtol=1e-12)
    rows, cols = g.shape
    assert g.area.sum() == pytest.approx(rows * cols * g.spacing_m**2, rel=1e-9)
    # 이웃 수: 모서리 4 칸은 3, 가장자리는 5, 안쪽은 8
    counts = np.bincount((g.nbr >= 0).sum(axis=1), minlength=9)
    assert counts[[3, 5, 8]].tolist() == [4, 2 * (rows + cols) - 8, (rows - 2) * (cols - 2)]


@pytest.mark.parametrize(("name", "kind"), [("dist_convergent_m", 1), ("dist_divergent_m", 2)])
def test_boundary_distances(planet, name, kind):
    """경계까지 거리: 유한, 0 이상, 반 바퀴 이하이고 그 종류의 경계 칸에서 작음.

    거리는 거친 격자에서 판 안 최단 경로로 재고 L0 로 옮기므로 L0 이웃 사이의 립시츠 성질은
    성립하지 않습니다 (함수 자체는 golden 대조 시험이 봄).
    """
    g = planet.graph
    d = planet[name].astype(np.float64)
    assert np.isfinite(d).all() and (d >= 0).all()
    assert d.max() <= np.pi * g.radius_m
    at = planet["boundary_type"] == kind
    assert at.any() and np.median(d[at]) < np.median(d[~at]) / 2
