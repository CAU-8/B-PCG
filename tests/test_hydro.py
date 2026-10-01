"""물길(bpcg.hydro) 검사: 채움, ε 채움, D8 + 히스테리시스, 순서, 누적, 유역, 강 구간.

docs/pipeline.md 6장과 가이드 2장 '확인하기' 표를 따릅니다.
- 싱크 없음: 출구가 아닌 모든 칸에서 z̃_r < z̃_c.
- 질량 보존: Σ_출구 Q = Σ A·P (상대 1e-12).
- 채움: ẑ ≥ z, 연결된 호수마다 ẑ 일정.
- 실제 지형(로페 512²): pyflwdir(MIT) 와 채움·누적이 같고, 가장 가파른 D8 기준 도구(landlab, MIT)와
  큰 강 겹침 ≥ 0.95, 흐름 방향 일치 ≥ 99%.
"""

import time
from pathlib import Path

import numpy as np
import pytest
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components

from bpcg.core.config import load_config
from bpcg.core.graph import flat_graph, sphere_graph
from bpcg.hydro.accumulate import accumulate, donors_count
from bpcg.hydro.depressions import fill_depressions, fill_epsilon
from bpcg.hydro.network import outlet_of, river_segments
from bpcg.hydro.routing import d8_receivers, donor_lists, topo_order

FIXTURES = Path(__file__).parent / "fixtures"
CFG = load_config("earth", "tiny")
EPS = CFG.landscape.fill_epsilon_m
ETA = CFG.landscape.hysteresis_eta


# ---------------------------------------------------------------- 시험 지형


def fractal_surface(ny: int, nx: int, seed: int, beta: float = 3.0, amp: float = 1000.0):
    """스펙트럼 합성 프랙탈 지형 (ny, nx) [m]. 웅덩이가 많이 생깁니다."""
    rng = np.random.default_rng(seed)
    ky = np.fft.fftfreq(ny)[:, None]
    kx = np.fft.rfftfreq(nx)[None, :]
    k = np.hypot(kx, ky)
    k[0, 0] = 1.0
    spec = (rng.normal(size=k.shape) + 1j * rng.normal(size=k.shape)) * k ** (-beta / 2)
    spec[0, 0] = 0.0
    z = np.fft.irfft2(spec, s=(ny, nx))
    return (z - z.min()) / (z.max() - z.min()) * amp


def flat_case(jitter: float, seed: int = 0, n: int = 64):
    g = flat_graph(n, n, 100.0, jitter=jitter, seed=seed)
    rng = np.random.default_rng(seed + 100)
    z = fractal_surface(n, n, seed).ravel() + rng.normal(0.0, 5.0, g.n_cells)
    return g, z, g.boundary_mask()


def sphere_case(seed: int = 0, n: int = 16):
    R = CFG.planet.radius_m
    g = sphere_graph(n, R, jitter=0.4, seed=seed)
    rng = np.random.default_rng(seed + 200)
    p = g.unit()
    z = np.zeros(g.n_cells)
    for _ in range(12):
        w = rng.normal(size=3)
        w *= rng.uniform(1.0, 8.0) / np.linalg.norm(w)
        z += rng.uniform(100.0, 800.0) * np.cos(p @ w + rng.uniform(0, 2 * np.pi))
    z += rng.normal(0.0, 400.0, g.n_cells)
    is_outlet = np.zeros(g.n_cells, dtype=bool)
    is_outlet[np.argsort(z, kind="stable")[: g.n_cells // 5]] = True  # 가장 낮은 20%
    return g, z, is_outlet


CASES = {
    "flat": lambda: flat_case(0.0),
    "flat_jitter": lambda: flat_case(0.4, seed=1),
    "sphere": lambda: sphere_case(),
}


def route(g, z, is_outlet):
    zh = fill_depressions(z, g.nbr, is_outlet)
    zt = fill_epsilon(z, g.nbr, is_outlet, EPS)
    rcv, slope, n_changed = d8_receivers(zt, g.nbr, g.dist, is_outlet)
    order = topo_order(rcv)
    return zh, zt, rcv, slope, n_changed, order


def lake_components(nbr: np.ndarray, is_lake: np.ndarray):
    """호수 칸(ẑ > z)끼리 이웃으로 이은 연결 성분 번호 (호수 아니면 -1)."""
    n = nbr.shape[0]
    src = np.repeat(np.arange(n), nbr.shape[1])
    dst = nbr.ravel()
    ok = (dst >= 0) & is_lake[src] & is_lake[np.maximum(dst, 0)]
    adj = coo_matrix((np.ones(ok.sum()), (src[ok], dst[ok])), shape=(n, n))
    _, lab = connected_components(adj, directed=False)
    return np.where(is_lake, lab, -1)


# ---------------------------------------------------------------- 채움


@pytest.mark.parametrize("case", CASES)
def test_fill_is_above_z_and_flat_per_lake(case):
    g, z, is_outlet = CASES[case]()
    zh = fill_depressions(z, g.nbr, is_outlet)
    assert zh.dtype == np.float64 and zh.shape == z.shape
    assert np.all(zh >= z)
    np.testing.assert_array_equal(zh[is_outlet], z[is_outlet])
    is_lake = zh > z
    assert is_lake.sum() > 10  # 시험 지형에 호수가 실제로 있어야 의미가 있음
    lab = lake_components(g.nbr, is_lake)
    for k in np.unique(lab[is_lake]):
        level = zh[lab == k]
        # 가이드 2장: 연결된 호수마다 ẑ 일정 (같은 값을 복사하므로 정확히 같음)
        assert level.max() == level.min()


@pytest.mark.parametrize("case", CASES)
def test_fill_matches_bellman_ford_fixed_point(case):
    """ẑ_c = max(z_c, min_k ẑ_k), z̃_c = max(z_c, min_k z̃_k + ε) 의 최소 고정점과 같음."""
    g, z, is_outlet = CASES[case]()
    nbr = g.nbr
    valid = nbr >= 0
    safe = np.maximum(nbr, 0)
    for eps in (0.0, EPS):
        ref = np.where(is_outlet, z, np.inf)
        for _ in range(g.n_cells):
            low = np.where(valid, ref[safe], np.inf).min(axis=1)
            new = np.where(is_outlet, z, np.maximum(z, low + eps))
            if np.array_equal(new, ref):
                break
            ref = new
        got = (
            fill_depressions(z, nbr, is_outlet)
            if eps == 0.0
            else fill_epsilon(z, nbr, is_outlet, eps)
        )
        np.testing.assert_allclose(got, ref, rtol=0, atol=1e-9)


@pytest.mark.parametrize("case", CASES)
def test_fill_epsilon_gives_strictly_lower_neighbour_and_is_idempotent(case):
    g, z, is_outlet = CASES[case]()
    zh = fill_depressions(z, g.nbr, is_outlet)
    zt = fill_epsilon(z, g.nbr, is_outlet, EPS)
    assert np.all(zt >= zh)
    np.testing.assert_array_equal(zt[is_outlet], z[is_outlet])
    low = np.where(g.nbr >= 0, zt[np.maximum(g.nbr, 0)], np.inf).min(axis=1)
    # 출구가 아닌 모든 칸에 ε 이상 낮은 이웃 (부동소수 반올림 1e-9 m 여유)
    assert np.all(low[~is_outlet] <= zt[~is_outlet] - EPS + 1e-9)
    np.testing.assert_array_equal(fill_epsilon(zt, g.nbr, is_outlet, EPS), zt)


def test_fill_rejects_bad_input():
    g = flat_graph(4, 4, 1.0)
    z = np.zeros(16)
    with pytest.raises(ValueError):
        fill_depressions(z, g.nbr, np.zeros(16, bool))  # 출구 없음
    with pytest.raises(ValueError):
        fill_epsilon(z, g.nbr, g.boundary_mask(), 0.0)  # ε ≤ 0
    zn = z.copy()
    zn[5] = np.nan
    with pytest.raises(ValueError):
        fill_depressions(zn, g.nbr, g.boundary_mask())
    nbr = g.nbr.copy()
    nbr[5] = -1  # 5 번 칸을 떼어 냄
    nbr[nbr == 5] = -1
    with pytest.raises(ValueError):
        fill_depressions(z, nbr, g.boundary_mask())  # 출구와 이어지지 않은 칸


# ---------------------------------------------------------------- D8, 순서, 누적


@pytest.mark.parametrize("case", CASES)
def test_no_sinks_every_cell_reaches_outlet_and_mass_is_conserved(case):
    g, z, is_outlet = CASES[case]()
    zh, zt, rcv, slope, n_changed, order = route(g, z, is_outlet)
    n = g.n_cells
    assert rcv.dtype == np.int64 and slope.dtype == np.float64 and n_changed == n
    assert np.array_equal(rcv[is_outlet], np.flatnonzero(is_outlet))
    assert np.all(slope[is_outlet] == 0.0)
    inner = ~is_outlet
    # 가이드 2장: 싱크 없음
    assert np.all(zt[rcv[inner]] < zt[inner])
    assert np.all(slope[inner] > 0)
    # 수신 셀은 이웃이고 경사는 그 이웃까지의 경사
    slot = np.argmax(g.nbr[inner] == rcv[inner][:, None], axis=1)
    assert np.all(g.nbr[inner, slot] == rcv[inner])
    np.testing.assert_allclose(
        slope[inner], (zt[inner] - zt[rcv[inner]]) / g.dist[inner, slot], rtol=1e-15
    )
    # 모든 칸이 출구에 닿음
    basin = outlet_of(rcv, order)
    assert basin.dtype == np.int64 and np.all(is_outlet[basin])
    # 질량 보존: Σ_출구 Q = Σ A·P (상대 1e-12)
    rng = np.random.default_rng(7)
    P = rng.uniform(0.1, 2.0, n)
    Q = accumulate(rcv, order, g.area * P)
    total = np.sum(g.area * P)
    assert abs(Q[is_outlet].sum() - total) / total < 1e-12
    # 유역별로도 보존
    per_basin = np.bincount(basin, weights=g.area * P, minlength=n)
    np.testing.assert_allclose(Q[is_outlet], per_basin[is_outlet], rtol=1e-12)


@pytest.mark.parametrize("case", CASES)
def test_d8_is_steepest_descent(case):
    """numpy 로 따로 계산한 가장 가파른 낮은 이웃(같으면 번호 작은 이웃)과 같음."""
    g, z, is_outlet = CASES[case]()
    zt = fill_epsilon(z, g.nbr, is_outlet, EPS)
    rcv, _, _ = d8_receivers(zt, g.nbr, g.dist, is_outlet)
    nbr = g.nbr
    znb = zt[np.maximum(nbr, 0)]
    s = np.where((nbr >= 0) & (znb < zt[:, None]), (zt[:, None] - znb) / g.dist, -np.inf)
    smax = s.max(axis=1, keepdims=True)
    cand = np.where(s == smax, nbr, np.iinfo(np.int32).max)
    ref = cand.min(axis=1)
    inner = ~is_outlet
    np.testing.assert_array_equal(rcv[inner], ref[inner])


@pytest.mark.parametrize("case", CASES)
def test_topo_order_puts_receivers_first(case):
    g, z, is_outlet = CASES[case]()
    _, _, rcv, _, _, order = route(g, z, is_outlet)
    n = g.n_cells
    assert order.dtype == np.int64
    assert np.array_equal(np.sort(order), np.arange(n))
    rank = np.empty(n, dtype=np.int64)
    rank[order] = np.arange(n)
    inner = rcv != np.arange(n)
    assert np.all(rank[rcv[inner]] < rank[inner])
    # 출구(자기 자신을 가리키는 칸)가 맨 앞에 번호 순서로
    n_root = int((~inner).sum())
    assert np.array_equal(order[:n_root], np.flatnonzero(~inner))


def test_topo_order_rejects_cycle():
    rcv = np.array([0, 2, 1, 2])  # 1 ↔ 2 순환
    with pytest.raises(ValueError):
        topo_order(rcv)


def test_donors_and_accumulate_small_tree():
    #    0 ← 1 ← 2,   1 ← 3,   4 (출구) ← 5
    rcv = np.array([0, 0, 1, 1, 4, 4])
    order = topo_order(rcv)
    assert list(order) == [0, 4, 1, 2, 3, 5]
    np.testing.assert_array_equal(donors_count(rcv), [1, 2, 0, 0, 1, 0])
    assert donors_count(rcv).dtype == np.int32
    start, donors = donor_lists(rcv)
    assert list(donors[start[1] : start[2]]) == [2, 3]
    np.testing.assert_array_equal(accumulate(rcv, order, 1.0), [4, 3, 1, 1, 2, 1])
    np.testing.assert_array_equal(outlet_of(rcv, order), [0, 0, 0, 0, 4, 4])
    is_river = np.array([True, True, True, True, False, False])
    segs = river_segments(rcv, order, is_river)  # 1 은 합류점(강 기여 2) → 하류 구간의 첫 칸
    assert [list(x) for x in segs] == [[1, 0], [2], [3]]
    assert river_segments(rcv, order, np.zeros(6, bool)) == []


def test_d8_tie_breaks_on_lowest_cell_id():
    g = flat_graph(3, 3, 1.0)
    z = np.zeros(9)
    z[4] = 1.0  # 가운데만 높고 가로·세로 이웃 넷이 같은 경사
    is_outlet = g.boundary_mask()
    rcv, slope, _ = d8_receivers(z, g.nbr, g.dist, is_outlet)
    assert rcv[4] == 1 and slope[4] == 1.0  # 북쪽(번호 1)이 가로·세로 이웃 가운데 번호가 가장 작음


def test_hysteresis_rule():
    g = flat_graph(3, 3, 1.0)
    is_outlet = g.boundary_mask()
    z = np.zeros(9)
    z[4] = 1.0
    z[1] = 0.01  # 북쪽 경사 0.99, 서쪽(3)·동쪽(5)·남쪽(7) 경사 1.0
    prev = np.arange(9)
    prev[4] = 1
    # 새 후보(3) 경사 1.0 < (1+0.02)·0.99 → 옛 수신 셀 유지
    rcv, slope, n_changed = d8_receivers(z, g.nbr, g.dist, is_outlet, prev=prev, eta=0.02)
    assert rcv[4] == 1 and slope[4] == pytest.approx(0.99) and n_changed == 0
    # η = 0 이면 더 가파른 쪽으로 바뀜
    rcv, _, n_changed = d8_receivers(z, g.nbr, g.dist, is_outlet, prev=prev, eta=0.0)
    assert rcv[4] == 3 and n_changed == 1
    # 옛 수신 셀이 더 이상 낮지 않으면 η 와 상관없이 바뀜
    z[1] = 2.0
    rcv, _, n_changed = d8_receivers(z, g.nbr, g.dist, is_outlet, prev=prev, eta=10.0)
    assert rcv[4] == 3 and n_changed == 1


def test_hysteresis_reduces_changes_on_nearly_flat_surface():
    """가이드 4장: 경사가 거의 같은 이웃 사이에서 수신 셀이 번갈아 바뀌는 것을 η 가 막음."""
    g = flat_graph(64, 64, 100.0, jitter=0.4, seed=3)
    rng = np.random.default_rng(3)
    y = g.pos[:, 1]
    z0 = 1e-3 * (y - y.min()) + rng.normal(0.0, 0.01, g.n_cells)  # 거의 평평한 비탈
    is_outlet = g.boundary_mask()
    zt0 = fill_epsilon(z0, g.nbr, is_outlet, EPS)
    rcv0, _, _ = d8_receivers(zt0, g.nbr, g.dist, is_outlet)
    z1 = z0 + rng.normal(0.0, 0.002, g.n_cells)  # 작은 흔들림
    zt1 = fill_epsilon(z1, g.nbr, is_outlet, EPS)
    rcv_a, _, n_plain = d8_receivers(zt1, g.nbr, g.dist, is_outlet, prev=rcv0, eta=0.0)
    rcv_b, slope_b, n_hyst = d8_receivers(zt1, g.nbr, g.dist, is_outlet, prev=rcv0, eta=ETA)
    assert n_plain == int(np.sum(rcv_a != rcv0)) and n_hyst == int(np.sum(rcv_b != rcv0))
    assert 0 < n_hyst < n_plain
    # 히스테리시스를 써도 싱크는 없음
    inner = ~is_outlet
    assert np.all(zt1[rcv_b[inner]] < zt1[inner]) and np.all(slope_b[inner] > 0)


# ---------------------------------------------------------------- 강 구간


@pytest.mark.parametrize("case", CASES)
def test_river_segments_partition_river_cells(case):
    g, z, is_outlet = CASES[case]()
    _, _, rcv, _, _, order = route(g, z, is_outlet)
    A = accumulate(rcv, order, g.area)
    is_river = (A >= 20 * np.median(g.area)) & ~is_outlet
    is_river[is_outlet & (donors_count(rcv) > 0)] = True  # 출구 칸도 강이면 거기서 끝나야 함
    segs = river_segments(rcv, order, is_river)
    assert len(segs) > 3 and all(s.dtype == np.int64 and s.size > 0 for s in segs)
    flat = np.concatenate(segs)
    # 모든 강 칸이 정확히 한 번
    assert flat.size == np.unique(flat).size == is_river.sum()
    assert np.all(is_river[flat])
    river_donors = np.bincount(rcv[is_river & (rcv != np.arange(g.n_cells))], minlength=g.n_cells)
    for s in segs:
        # 상류 → 하류로 이어짐
        assert np.array_equal(rcv[s[:-1]], s[1:])
        # 시작은 발원점(강 기여 0) 또는 합류점(2 이상)
        assert river_donors[s[0]] != 1
        # 중간 칸은 합류점이 아님
        assert np.all(river_donors[s[1:]] == 1)
        # 끝은 출구, 강이 아닌 수신 셀, 또는 합류점 바로 앞
        end = s[-1]
        nxt = rcv[end]
        assert nxt == end or not is_river[nxt] or river_donors[nxt] >= 2


# ---------------------------------------------------------------- 실제 지형 (로페 512²)


@pytest.fixture(scope="module")
def lope():
    """로페 512² 30 m 조각을 북쪽이 0번 행이 되게 뒤집어 평면 그래프에서 물길을 계산합니다."""
    d = np.load(FIXTURES / "lope_tile512_30m.npz")
    img = np.asarray(d["z"], dtype=np.float64)[::-1]  # 파일은 남→북, 그래프는 0번 행이 북쪽
    dx = float(d["dx"])
    ny, nx = img.shape
    g = flat_graph(ny, nx, dx)
    z = img.ravel()
    is_outlet = g.boundary_mask()
    zh, zt, rcv, slope, _, order = route(g, z, is_outlet)
    A = accumulate(rcv, order, g.area)
    return dict(
        img=img, dx=dx, g=g, z=z, is_outlet=is_outlet, zh=zh, zt=zt, rcv=rcv, order=order, A=A
    )


def test_lope_routing_invariants(lope):
    g, z, out, zh, zt, rcv, A = (lope[k] for k in ("g", "z", "is_outlet", "zh", "zt", "rcv", "A"))
    assert np.all(zt[rcv[~out]] < zt[~out])
    assert abs(A[out].sum() - g.area.sum()) / g.area.sum() < 1e-12
    assert np.all(zh >= z)


def test_lope_against_pyflwdir(lope):
    """pyflwdir(MIT) 와 채움 높이, 같은 방향에서의 상류 면적, 규칙이 같은 칸의 방향을 비교합니다.

    pyflwdir.from_dem 의 D8 은 '가장 가파른 이웃'이 아니라 Wang & Liu (2006) 홍수에서 처음 닿은
    (채운 높이가 가장 낮은) 이웃입니다. 그래서 강 칸 겹침·방향 일치의 0.95·99% 기준은 가장 가파른
    D8 인 landlab 과 비교하는 아래 시험에서 봅니다.
    """
    pyflwdir = pytest.importorskip("pyflwdir")
    from pyflwdir import core_d8
    from pyflwdir.dem import fill_depressions as pf_fill

    img, dx, g = lope["img"], lope["dx"], lope["g"]
    z, out, zh, rcv, A = (lope[k] for k in ("z", "is_outlet", "zh", "rcv", "A"))
    ny, nx = img.shape
    transform = (dx, 0.0, 0.0, 0.0, -dx, 0.0)

    # 1) 채움 높이가 같음 (같은 priority-flood 정의)
    zf, d8 = pf_fill(img, outlets="edge")
    np.testing.assert_allclose(zf.ravel(), zh, rtol=0, atol=1e-9)

    # 2) 같은 수신 셀에서 pyflwdir 누적과 상류 면적이 같음 (상대 1e-12)
    flw_ours = pyflwdir.from_array(
        core_d8.to_array(rcv, (ny, nx)), ftype="d8", transform=transform, latlon=False
    )
    upa_ours = flw_ours.accuflux(np.full((ny, nx), dx * dx)).ravel()
    np.testing.assert_allclose(upa_ours, A, rtol=1e-12)

    # 3) pyflwdir.from_dem(outlets='edge') 와 방향 비교
    flw = pyflwdir.from_dem(img, outlets="edge", transform=transform, latlon=False)
    ids = flw.idxs_ds.astype(np.int64)
    upa = flw.upstream_area(unit="m2").ravel()
    flat = zh > z
    nbr = g.nbr
    zn = np.where(nbr >= 0, zh[np.maximum(nbr, 0)], np.inf)
    kmin = np.argmin(zn, axis=1)
    zmin = zn[np.arange(z.size), kmin]
    unique_low = (zn == zmin[:, None]).sum(axis=1) == 1
    lowest = nbr[np.arange(z.size), kmin]
    # 두 규칙(가장 낮은 이웃, 가장 가파른 이웃)이 같은 이웃을 가리키는 칸
    same_rule = ~flat & ~out & unique_low & (lowest == rcv) & (zmin < zh)
    agree = np.mean(ids[same_rule] == rcv[same_rule])
    big_ours, big_theirs = A >= 1e6, upa >= 1e6
    iou_raw = (big_ours & big_theirs & ~out).sum() / ((big_ours | big_theirs) & ~out).sum()
    m = big_ours & big_theirs & ~flat & ~out
    agree_raw = np.mean(ids[m] == rcv[m])
    print(
        f"\nlope vs pyflwdir: 규칙 같은 칸 {same_rule.sum()} 개 방향 일치 {agree:.4f}; "
        f"규칙 차이 포함 강(≥1 km²) IoU {iou_raw:.3f}, 방향 일치 {agree_raw:.3f}"
    )
    assert same_rule.sum() > 0.3 * (~flat & ~out).sum()
    assert agree >= 0.99


def test_lope_against_landlab_d8(lope):
    """가장 가파른 D8(landlab FlowDirectorD8, MIT)과 같은 z̃ 에서 비교합니다.

    docs/pipeline.md 6장: 큰 강(상류 1 km² 이상) 칸 겹침 ≥ 0.95, 흐름 방향 일치 ≥ 99%
    (ε 채움 평지 ẑ > z 와 출구 칸은 판정에서 뺌).
    남는 차이는 경사가 정확히 같은 칸에서 고르는 순서 규칙입니다.
    """
    pytest.importorskip("landlab")
    from landlab import RasterModelGrid
    from landlab.components import FlowAccumulator

    img, dx = lope["img"], lope["dx"]
    z, out, zh, zt, rcv, A = (lope[k] for k in ("z", "is_outlet", "zh", "zt", "rcv", "A"))
    ny, nx = img.shape
    mg = RasterModelGrid((ny, nx), xy_spacing=dx)  # landlab 0번 행은 남쪽
    mg.add_field("topographic__elevation", zt.reshape(ny, nx)[::-1].ravel().copy(), at="node")
    FlowAccumulator(mg, flow_director="D8").run_one_step()

    def to_ours(a: np.ndarray) -> np.ndarray:
        """landlab 노드 순서(0번 행이 남쪽) → 그래프 셀 순서(0번 행이 북쪽)."""
        return a.reshape(ny, nx)[::-1].ravel()

    rl = mg.at_node["flow__receiver_node"]
    rl = to_ours((ny - 1 - rl // nx) * nx + rl % nx)
    upa = to_ours(mg.at_node["drainage_area"])
    big_ours, big_theirs = A >= 1e6, upa >= 1e6
    iou = (big_ours & big_theirs & ~out).sum() / ((big_ours | big_theirs) & ~out).sum()
    flat = zh > z
    m = ~flat & ~out
    agree = np.mean(rl[m] == rcv[m])
    print(f"\nlope vs landlab D8: 강(≥1 km²) IoU {iou:.4f}, 방향 일치 {agree:.4f}")
    assert iou >= 0.95
    assert agree >= 0.99


# ---------------------------------------------------------------- 성능 (157만 칸 규모)


@pytest.mark.slow
def test_benchmark_1280_squared():
    n = 1280
    g = flat_graph(n, n, 25.0, jitter=CFG.landscape.jitter, seed=1)
    z = fractal_surface(n, n, seed=1).ravel()
    is_outlet = g.boundary_mask()
    route(*flat_case(0.0, n=16))  # 컴파일 먼저
    t = {}
    t0 = time.perf_counter()
    zt = fill_epsilon(z, g.nbr, is_outlet, EPS)
    t["fill_epsilon"] = time.perf_counter() - t0
    t0 = time.perf_counter()
    fill_depressions(z, g.nbr, is_outlet)
    t["fill_depressions"] = time.perf_counter() - t0
    t0 = time.perf_counter()
    rcv, _, _ = d8_receivers(zt, g.nbr, g.dist, is_outlet)
    t["d8_receivers"] = time.perf_counter() - t0
    t0 = time.perf_counter()
    order = topo_order(rcv)
    t["topo_order"] = time.perf_counter() - t0
    t0 = time.perf_counter()
    accumulate(rcv, order, g.area)
    t["accumulate"] = time.perf_counter() - t0
    print("\n" + ", ".join(f"{k} {v:.3f} s" for k, v in t.items()))
    assert t["fill_epsilon"] < 3.0 and t["fill_depressions"] < 3.0
    assert t["d8_receivers"] < 1.0 and t["topo_order"] < 1.0 and t["accumulate"] < 1.0
