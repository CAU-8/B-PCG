"""고스트 줄과 구면 보간 검사 (가이드 7장 '확인하기', bpcg.core.resample)."""

import numpy as np
import pytest

from bpcg.core import cubesphere as cs
from bpcg.core import resample as rs
from bpcg.core.graph import sphere_graph

K = np.array([0.3, -0.8, 0.5])


def F(p):
    """가이드 7장의 매끄러운 시험 함수."""
    return np.sin(3 * (p @ K))


def sample_on_grid(n):
    """셀 중심에서 샘플한 (6, n, n) 면별 필드."""
    return F(cs.cubesphere_grid(n, 1.0).pos).reshape(6, n, n)


def _test_points(n, per_cell=4):
    """면마다 칸당 per_cell 간격의 점 (면 경계·꼭짓점 포함)과 '경계에서 한 칸 이내' 표시."""
    t = np.linspace(-1.0, 1.0, per_cell * n + 1)
    A, B = np.meshgrid(t, t, indexing="xy")
    edge = (np.maximum(np.abs(A), np.abs(B)) > 1.0 - 2.0 / n).ravel()
    pts = np.concatenate([cs.to_sphere(f, A, B).reshape(-1, 3) for f in range(6)])
    return pts, np.tile(edge, 6)


def _interp(padded, n, pts):
    out = np.empty(len(pts))
    rs._bilinear_kernel(padded, pts, n, 1, cs.FACE_U, cs.FACE_V, cs.FACE_N, out)
    return out


def max_interp_error(padded, n, band):
    pts, edge = _test_points(n)
    err = np.abs(_interp(padded, n, pts) - F(pts))
    return err[edge].max() if band == "edge" else err[~edge].max()


@pytest.mark.parametrize("n", [32, 64, 128])
def test_ghost_interpolation(n):
    # 가이드 7장: 경계 근처 보간 오차 / 내부 오차 < 2
    padded = rs.add_ghost_layers(sample_on_grid(n), layers=1)
    e_edge = max_interp_error(padded, n, "edge")
    e_int = max_interp_error(padded, n, "interior")
    assert e_edge / e_int < 2.0


def test_ghost_convergence():
    # 가이드 7장: n 을 두 배로 하면 오차가 약 1/4 (2차 수렴)
    e = {}
    for n in (64, 128):
        padded = rs.add_ghost_layers(sample_on_grid(n), layers=1)
        e[n] = (max_interp_error(padded, n, "edge"), max_interp_error(padded, n, "interior"))
    assert 3.0 < e[64][0] / e[128][0] < 5.0
    assert 3.0 < e[64][1] / e[128][1] < 5.0


def test_naive_copy_is_worse():
    # 인접 칸 값을 그대로 복사하면(이웃 표 그대로) 경계 오차가 크게 나빠집니다 — 고치는 이유.
    n = 64
    faces = sample_on_grid(n)
    good = rs.add_ghost_layers(faces, 1)
    naive = good.copy()
    flat = faces.reshape(-1)
    nbr = cs.neighbor_table(n)
    slot = {(0, -1): 3, (0, 1): 4, (-1, 0): 1, (1, 0): 6}
    for f in range(6):
        for k in range(n):
            base = f * n * n
            naive[f, k + 1, 0] = flat[nbr[base + k * n + 0, slot[(0, -1)]]]
            naive[f, k + 1, n + 1] = flat[nbr[base + k * n + n - 1, slot[(0, 1)]]]
            naive[f, 0, k + 1] = flat[nbr[base + k, slot[(-1, 0)]]]
            naive[f, n + 1, k + 1] = flat[nbr[base + (n - 1) * n + k, slot[(1, 0)]]]
    assert max_interp_error(naive, n, "edge") > 3 * max_interp_error(good, n, "edge")


def test_ghost_matches_guide_formula():
    # 가이드 7장: +Z 면에서 +X 면 쪽 m 번째 고스트 줄의 칸 j 는
    # +X 면 (n−m) 번째 행을 따라 a'_{j,m} 에서 1D 보간한 값입니다.
    n, L = 16, 2
    centers = -1.0 + (2.0 * np.arange(n) + 1.0) / n
    faces = np.zeros((6, n, n))
    faces[0] = centers[None, :]  # +X 면: 값 = 칸 중심의 a 좌표 (행마다 같음)
    padded = rs.add_ghost_layers(faces, layers=L)
    for m in (1, 2):
        expect = (
            4.0
            / np.pi
            * np.arctan(
                np.tan(np.pi * centers / 4.0) / np.tan(np.pi / 4.0 * (1.0 + (2 * m - 1) / n))
            )
        )
        got = padded[4, L : L + n, L + n - 1 + m]
        np.testing.assert_allclose(got, expect, rtol=0, atol=1e-13)


def test_ghost_nearest_cell_matches_neighbor_table():
    # 첫 고스트 줄에서 가중치가 큰 쪽 칸은 이웃 표가 가리키는 면 너머 이웃과 같습니다.
    n, L = 12, 1
    P = n + 2 * L
    edge_dst, edge_src, edge_w, *_ = rs._ghost_stencil(n, L)
    nbr = cs.neighbor_table(n)
    pick = np.where(edge_w[:, 1] > edge_w[:, 0], edge_src[:, 1], edge_src[:, 0])

    def to_cell(flat_idx):
        f, rem = np.divmod(flat_idx, P * P)
        jj, ii = np.divmod(rem, P)
        return f, jj - L, ii - L

    fs, js, is_ = to_cell(pick)
    assert np.all((js >= 0) & (js < n) & (is_ >= 0) & (is_ < n))
    picked_cell = fs * n * n + js * n + is_
    gf, gj, gi = to_cell(edge_dst)
    # 고스트 (j, i) 가 면 밖 한 칸이면, 안쪽 칸과 슬롯 (dj, di) 로 이웃 표를 읽습니다.
    dj = np.where(gj < 0, -1, np.where(gj >= n, 1, 0))
    di = np.where(gi < 0, -1, np.where(gi >= n, 1, 0))
    inner = gf * n * n + np.clip(gj, 0, n - 1) * n + np.clip(gi, 0, n - 1)
    slots = {d: s for s, d in enumerate(cs.NEIGHBOR_SLOTS)}
    slot = np.array([slots[(a, b)] for a, b in zip(dj, di, strict=True)])
    assert np.array_equal(nbr[inner, slot], picked_cell)


def test_multilayer_ghosts_are_accurate():
    # 두·세 번째 고스트 줄과 꼭짓점 블록도 정확한 위치의 값에 가깝습니다.
    n = 32
    for L in (1, 2, 3):
        padded = rs.add_ghost_layers(sample_on_grid(n), layers=L)
        centers = -1.0 + (2.0 * np.arange(-L, n + L) + 1.0) / n
        A, B = np.meshgrid(centers, centers, indexing="xy")
        X, Y = np.tan(np.pi * A / 4.0), np.tan(np.pi * B / 4.0)
        for f in range(6):
            q = cs.FACE_N[f] + X[..., None] * cs.FACE_U[f] + Y[..., None] * cs.FACE_V[f]
            err = np.abs(padded[f] - F(q / np.linalg.norm(q, axis=-1, keepdims=True)))
            assert err.max() < 3e-3  # 1D 선형 보간 오차 ~ h²/8·|F''| 수준
            assert np.array_equal(padded[f, L : L + n, L : L + n], sample_on_grid(n)[f])


def test_constant_field_stays_constant():
    padded = rs.add_ghost_layers(np.full((6, 8, 8), 3.25), layers=2)
    np.testing.assert_allclose(padded, 3.25, rtol=1e-15)


def test_sample_sphere_exact_at_centers_and_continuous_across_edges():
    n = 32
    g = cs.cubesphere_grid(n, 1.0)
    field = F(g.pos)
    np.testing.assert_allclose(rs.sample_sphere(field, n, g.pos), field, rtol=0, atol=1e-12)
    # 길이가 1 이 아닌 벡터도 방향만 봅니다.
    np.testing.assert_allclose(rs.sample_sphere(field, n, 7.0 * g.pos), field, rtol=0, atol=1e-12)
    # 면 경계 양쪽 아주 가까운 두 점의 차이는 보간 오차 수준이고 2차로 줄어듭니다.
    jumps = {}
    for nn in (32, 64):
        fld = F(cs.cubesphere_grid(nn, 1.0).pos)
        t = np.linspace(-0.999, 0.999, 1001)
        js = []
        for f in range(6):
            for s in (1.0, -1.0):
                for on_a in (True, False):
                    e_in, e_out = s * (1 - 1e-9), s * (1 + 1e-9)
                    if on_a:
                        p_in, p_out = (
                            cs.to_sphere(f, e_in + 0 * t, t),
                            cs.to_sphere(f, e_out + 0 * t, t),
                        )
                    else:
                        p_in, p_out = (
                            cs.to_sphere(f, t, e_in + 0 * t),
                            cs.to_sphere(f, t, e_out + 0 * t),
                        )
                    js.append(
                        np.abs(rs.sample_sphere(fld, nn, p_in) - rs.sample_sphere(fld, nn, p_out))
                    )
        jumps[nn] = np.concatenate(js).max()
        padded = rs.add_ghost_layers(fld.reshape(6, nn, nn), 1)
        assert jumps[nn] < max_interp_error(padded, nn, "interior")
    assert jumps[32] / jumps[64] > 3.0


def test_sample_sphere_nearest_is_cell_of():
    n = 16
    field = np.arange(6 * n * n, dtype=np.int32) * 7
    pts = np.random.default_rng(0).normal(size=(5000, 3))
    got = rs.sample_sphere(field, n, pts, method="nearest")
    assert got.dtype == np.int32
    assert np.array_equal(got, field[cs.cell_of(pts, n)])


def test_resample_coarse_to_fine():
    # 거친 격자 → L0: 매끄러운 값은 쌍선형 (2차 수렴), 범주 값은 가장 가까운 칸
    errs = {}
    for n_src in (16, 32):
        field = F(cs.cubesphere_grid(n_src, 1.0).pos)
        dst = sphere_graph(4 * n_src, 6_371_000.0, jitter=0.4, seed=1)
        got = rs.resample_sphere(field, n_src, dst)
        assert got.shape == (dst.n_cells,) and got.dtype == np.float64
        errs[n_src] = np.abs(got - F(dst.unit())).max()
        near = rs.resample_sphere(field, n_src, dst, method="nearest")
        assert np.array_equal(near, field[cs.cell_of(dst.unit(), n_src)])
        assert errs[n_src] < 0.25 * np.abs(near - F(dst.unit())).max()
    assert errs[32] < 5e-3
    assert 3.0 < errs[16] / errs[32] < 5.0


def test_invalid_inputs():
    with pytest.raises(ValueError):
        rs.add_ghost_layers(np.zeros((5, 4, 4)))
    with pytest.raises(ValueError):
        rs.add_ghost_layers(np.zeros((6, 4, 4)), layers=3)  # 2L > n
    with pytest.raises(ValueError):
        rs.add_ghost_layers(np.zeros((6, 4, 4)), layers=0)
    with pytest.raises(ValueError):
        rs.sample_sphere(np.zeros(50), 4, np.ones((2, 3)))  # 길이가 6n² 이 아님
    with pytest.raises(ValueError):
        rs.sample_sphere(np.zeros(6 * 16), 4, np.ones((2, 2)))
    with pytest.raises(ValueError):
        rs.sample_sphere(np.zeros(6 * 16), 4, np.zeros((2, 3)))  # 길이 0 벡터
    with pytest.raises(ValueError):
        rs.sample_sphere(np.zeros(6 * 16), 4, np.ones((2, 3)), method="cubic")
    with pytest.raises(ValueError):
        rs.sample_sphere(np.zeros(6 * 16, dtype=np.int32), 4, np.ones((2, 3)))  # 범주 값 linear
    with pytest.raises(ValueError):
        from bpcg.core.graph import flat_graph

        rs.resample_sphere(np.zeros(6 * 16), 4, flat_graph(3, 3, 1.0))
