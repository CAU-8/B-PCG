"""subsurface(물·흙·지하수면·동굴) 검사 (docs/pipeline.md 8.1~8.4, 가이드 6장, 설계도 4장 사슬 3).

시험 지형
- 섬: 평면 64², 200 m, 노드 흔들기 0.4 를 정상상태 솔버로 풀고, 지질은 템플릿 0(탄산염 탁상지)
  기둥(깎인 두께 450 m)이라 석회암이 고도 −200~300 m 에 있습니다. 섬이 작아서 강 문턱은
  0.02 m³/s 로 낮춥니다(기본 0.3 이면 유출 0.25 m/yr 에서 강이 없음).
- 합성 골짜기: 평면 격자에 강 한 줄과 V 자·협곡 벽을 손으로 만든 지형(해석해 비교용).
"""

import time

import numpy as np
import pytest
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components
from scipy.spatial import cKDTree
from scipy.stats import spearmanr

from bpcg.core.config import load_config
from bpcg.core.constants import MU_WATER, RHO_WATER, SECONDS_PER_YEAR, gravity
from bpcg.core.distance import nearest_source
from bpcg.core.graph import flat_graph
from bpcg.geology import rocks as rk
from bpcg.geology.model import LayerColumns, build_columns, rock_at, surface_rock
from bpcg.hydro.depressions import fill_depressions
from bpcg.landscape.fans import reroute
from bpcg.landscape.solver import solve_steady_state
from bpcg.subsurface.caves import cave_levels
from bpcg.subsurface.groundwater import SHALLOW_DEPTH_M, hydraulic_conductivity, water_table
from bpcg.subsurface.soil import smoothstep01, soil_and_alluvium, soil_production_max
from bpcg.subsurface.water import valley_depth, water_bodies, window_max

CFG = load_config("earth", "tiny", {"rivers.min_discharge_m3_per_s": 0.02})
EXHUMATION_M = 450.0  # 석회암 (−200, 300] m, 그 위 사암, 아래 셰일


def platform_columns(n: int, exhumation: float, cfg=CFG) -> LayerColumns:
    sb, sr = build_columns(np.zeros(n, np.int64), np.full(n, exhumation), None, cfg)
    return LayerColumns.from_columns(sb, sr)


def lake_components(graph, mask: np.ndarray) -> np.ndarray:
    """mask 칸들의 이웃 연결 성분 번호 (mask 밖은 -1)."""
    rows, slots = np.nonzero(graph.nbr >= 0)
    cols = graph.nbr[rows, slots]
    keep = mask[rows] & mask[cols]
    n = graph.n_cells
    adj = coo_matrix((np.ones(keep.sum()), (rows[keep], cols[keep])), shape=(n, n))
    _, label = connected_components(adj, directed=False)
    return np.where(mask, label, -1)


def assert_rivers_never_rise(rcv, wb):
    """모든 강 칸에서 h_w(c) ≥ h_w(r(c)) (가이드 6장: 강물이 거꾸로 흐르지 않음)."""
    h = wb["water_level_m"]
    river = np.flatnonzero(wb["is_river"])
    r = rcv[river]
    moving = r != river
    assert np.isfinite(h[r[moving]]).all()  # 강의 수신 셀은 늘 물(강·호수·바다)
    assert np.all(h[r[moving]] <= h[river[moving]])


# ---------------------------------------------------------------- 시험 섬
@pytest.fixture(scope="module")
def island():
    n, dx = 64, 200.0
    g = flat_graph(n, n, dx, jitter=0.4, seed=1)
    half = 0.5 * n * dx
    rr = np.hypot(g.pos[:, 0] - half, g.pos[:, 1] + half)
    is_ocean = rr > 0.42 * n * dx
    U = np.where(is_ocean, 0.0, 1e-5 + 1e-4 * np.exp(-((rr / (0.25 * n * dx)) ** 2)))
    R = np.full(g.n_cells, 0.25)
    cols = platform_columns(g.n_cells, EXHUMATION_M)
    res = solve_steady_state(g, is_ocean, 0.0, U, R, cols, CFG)
    assert res.converged
    wb = water_bodies(g, res.z, res, is_ocean, CFG)
    is_water = is_ocean | wb["is_lake"] | wb["is_river"]
    srock = surface_rock(cols, res.z)
    zgw, diag = water_table(
        g, res.z, wb["water_level_m"], is_water, srock, res.slope, R, CFG, is_ocean=is_ocean
    )
    return {
        "g": g,
        "is_ocean": is_ocean,
        "U": U,
        "R": R,
        "cols": cols,
        "res": res,
        "z": res.z,
        "wb": wb,
        "is_water": is_water,
        "srock": srock,
        "zgw": zgw,
        "diag": diag,
    }


# ---------------------------------------------------------------- 8.1 물
def test_water_masks_and_hydraulic_geometry(island):
    g, wb, ocean, res = island["g"], island["wb"], island["is_ocean"], island["res"]
    assert set(wb) == {
        "water_level_m",
        "is_lake",
        "is_river",
        "river_width_m",
        "river_depth_m",
        "valley_depth_m",
    }
    river, lake, h = wb["is_river"], wb["is_lake"], wb["water_level_m"]
    assert river.sum() > 50  # 강이 실제로 있어야 이 검사가 의미 있음
    assert not (river & ocean).any() and not (lake & ocean).any() and not (river & lake).any()
    # 솔버 결과에는 웅덩이가 없으므로 호수도 없습니다(호수는 선상지·댐에서만 생김).
    assert not lake.any()
    q_s = res.discharge / SECONDS_PER_YEAR
    np.testing.assert_array_equal(river, ~ocean & (q_s >= CFG.rivers.min_discharge_m3_per_s))
    # Leopold–Maddock: W = k_W·Q^0.5, D = k_D·Q^0.4 (Q 는 m³/s), 강 밖은 0
    np.testing.assert_allclose(wb["river_width_m"][river], 4.0 * q_s[river] ** 0.5, rtol=1e-12)
    np.testing.assert_allclose(wb["river_depth_m"][river], 0.35 * q_s[river] ** 0.4, rtol=1e-12)
    assert np.all(wb["river_width_m"][~river] == 0) and np.all(wb["river_depth_m"][~river] == 0)
    # 수면: 물 칸만 값, 바다 0, 강은 z − 0.2·D 이하(배수로 올라간 어귀만 받는 쪽 수면)
    water = ocean | lake | river
    assert np.isfinite(h[water]).all() and np.isnan(h[~water]).all()
    assert np.all(h[ocean] == 0.0)
    raw = res.z - 0.2 * wb["river_depth_m"]
    assert np.all(h[river] <= res.z[river])  # 강 수면은 둑(칸 고도) 아래
    assert np.mean(h[river] == raw[river]) > 0.9  # 대부분은 z − 0.2·D 그대로
    assert g.n_cells == h.shape[0]


def test_river_levels_never_rise_downstream(island):
    assert_rivers_never_rise(island["res"].receiver, island["wb"])


def strip_water(z: np.ndarray, q_m3_per_s: float = 1000.0):
    """서→동 1×N 띠, 마지막 칸이 바다. 모든 칸이 같은 유량이라 강 깊이 D 가 같습니다."""
    n = z.shape[0]
    g = flat_graph(1, n, 100.0)
    ocean = np.zeros(n, bool)
    ocean[-1] = True
    rcv = np.minimum(np.arange(n) + 1, n - 1)
    result = {"receiver": rcv, "discharge_m3_per_yr": np.full(n, q_m3_per_s * SECONDS_PER_YEAR)}
    wb = water_bodies(g, z, result, ocean, CFG)
    return wb, 0.2 * 0.35 * q_m3_per_s**0.4


def test_water_level_rules_on_strip():
    # 호수(칸 2, 3: 넘침 높이 8.5 m)와 바다 어귀. 강 수면 z − 0.2·D 는 D ≈ 5.5 m 라 1.1 m 아래.
    z = np.array([10.0, 9.0, 8.0, 5.0, 8.5, 0.5, 0.3, -10.0])
    wb, drop = strip_water(z)
    h = wb["water_level_m"]
    np.testing.assert_array_equal(wb["is_lake"], [0, 0, 1, 1, 0, 0, 0, 0])
    np.testing.assert_array_equal(wb["is_river"], [1, 1, 0, 0, 1, 1, 1, 0])
    assert h[2] == h[3] == 8.5  # 호수는 넘침 높이로 평평
    assert h[0] == pytest.approx(10.0 - drop)
    assert h[1] == 8.5  # 호수로 들어가는 어귀는 호수면까지 올림(배수 효과, 호수는 낮추지 않음)
    assert h[4] == pytest.approx(8.5 - drop)  # 호수에서 나가는 강은 그대로
    assert h[5] == h[6] == 0.0  # 해수면보다 낮게 계산된 강어귀는 해수면까지
    assert h[7] == 0.0
    assert np.all(np.diff(h) <= 0.0)  # 하류로 갈수록 높아지지 않음

    # 명세 규칙(상류부터 min): 하류 칸 z 가 0.5 mm 더 높아도(호수 문턱 1 mm 미만) 역류 없음
    z = np.array([10.0, 6.0, 6.0005, 3.0, -10.0])
    wb, drop = strip_water(z)
    h = wb["water_level_m"]
    assert not wb["is_lake"].any()
    assert h[2] == h[1] == pytest.approx(6.0 - drop)
    assert np.all(np.diff(h) <= 0.0)


def test_water_bodies_is_deterministic(island):
    g, res, ocean = island["g"], island["res"], island["is_ocean"]
    again = water_bodies(g, res.z, res, ocean, CFG)
    for k, v in island["wb"].items():
        np.testing.assert_array_equal(again[k], v)


def test_dam_lake_is_flat_and_rivers_monotone(island):
    """강을 막는 둑을 세우면 호수가 생기고, 호수는 평평하며 강은 거꾸로 흐르지 않습니다."""
    g, res, ocean, R = island["g"], island["res"], island["is_ocean"], island["R"]
    wb0 = island["wb"]
    z0 = res.z
    rcv = res.receiver
    tree = cKDTree(g.pos)
    cand = np.flatnonzero(wb0["is_river"] & ~ocean[rcv] & ~ocean[rcv[rcv]])
    cand = cand[np.argsort(-res.discharge[cand], kind="stable")]
    found = None
    for c in cand:
        r = rcv[c]
        dam = np.asarray(tree.query_ball_point(g.pos[r], 1.5 * g.spacing), dtype=np.int64)
        dam = dam[(dam != c) & ~ocean[dam]]
        # 둑 높이는 강을 칸 5개 거슬러 오를 만큼(강 경사 × 5칸, 최소 20 m)입니다. 강 경사는 경사
        # 지수 n 에 따라 달라서(n = 2 면 이 섬의 강 경사 약 0.1) 20 m 고정이면 호수가 1~2칸뿐입니다.
        dam_h = max(20.0, 5.0 * res.slope[c] * g.spacing)
        z = z0.copy()
        z[dam] = np.maximum(z[dam], z0[c] + dam_h)
        rr = reroute(g, z, ocean, R, CFG)
        wb = water_bodies(g, z, rr, ocean, CFG)
        if wb["is_lake"].sum() >= 3:
            found = (z, rr, wb)
            break
    assert found is not None
    z, rr, wb = found
    lake, h = wb["is_lake"], wb["water_level_m"]
    # 호수 수면 = priority-flood 넘침 높이, 연결된 호수마다 일정(가이드 6장 '호수가 평평하다')
    is_outlet = ocean | (rr["receiver"] == np.arange(g.n_cells))
    z_hat = fill_depressions(z, g.nbr, is_outlet)
    np.testing.assert_array_equal(h[lake], z_hat[lake])
    assert np.all(z_hat[lake] - z[lake] > 1e-3)
    label = lake_components(g, lake)
    for lab in np.unique(label[lake]):
        assert np.ptp(h[label == lab]) == 0.0
    assert_rivers_never_rise(rr["receiver"], wb)
    # 호수로 들어가는 강 어귀는 호수면 이상입니다(배수 효과).
    river = np.flatnonzero(wb["is_river"])
    into_lake = river[lake[rr["receiver"][river]]]
    assert np.all(h[into_lake] >= h[rr["receiver"][into_lake]])


def test_valley_depth_matches_window_maximum(island):
    g, z, wb = island["g"], island["z"], island["wb"]
    river = np.flatnonzero(wb["is_river"])
    h = wb["water_level_m"]
    window = CFG.caves.valley_window_m
    tree = cKDTree(g.pos)
    brute = np.array([z[tree.query_ball_point(g.pos[c], window)].max() for c in river])
    rim = window_max(g, z, river, window)
    # 그래프로 퍼뜨린 원판은 직선 원판의 부분집합이라 최댓값이 넘지 않습니다.
    assert np.all(rim <= brute)
    assert np.mean(rim == brute) >= 0.99
    vd = wb["valley_depth_m"]
    np.testing.assert_allclose(vd[river], np.maximum(rim - h[river], 0.0), rtol=0, atol=0)
    assert np.all(vd >= 0.0)
    # 다른 칸은 가장 가까운 강의 값을 넘겨받습니다.
    _, src = nearest_source(g, wb["is_river"])
    np.testing.assert_array_equal(vd, vd[src])


def test_window_max_regular_grid_is_exact_disk():
    g = flat_graph(40, 40, 50.0)
    z = np.sin(g.pos[:, 0] / 130.0) * np.cos(g.pos[:, 1] / 170.0) + g.pos[:, 0] * 1e-4
    cells = np.arange(0, g.n_cells, 7)
    rim = window_max(g, z, cells, 333.0)
    d = np.linalg.norm(g.pos[cells][:, None, :] - g.pos[None, :, :], axis=2)
    brute = np.where(d <= 333.0, z[None, :], -np.inf).max(axis=1)
    np.testing.assert_array_equal(rim, brute)
    # 반경 0 이면 자기 칸만
    np.testing.assert_array_equal(window_max(g, z, cells, 0.0), z[cells])


def test_valley_depth_without_rivers_is_nan():
    g = flat_graph(8, 8, 100.0)
    vd = valley_depth(g, np.zeros(64), np.full(64, np.nan), np.zeros(64, bool), 500.0)
    assert np.isnan(vd).all()


# ---------------------------------------------------------------- 8.3 지하수면
def test_water_table_rules_on_island(island):
    g, z, zgw, wb = island["g"], island["z"], island["zgw"], island["wb"]
    water, h = island["is_water"], wb["water_level_m"]
    # 가이드 6장 + pipeline.md 8.3: z_gw ≤ max(z, h_w), 물 칸은 z_gw = h_w, z_gw ≥ z − max_depth
    assert np.all(zgw <= np.fmax(z, h))
    np.testing.assert_array_equal(zgw[water], h[water])
    assert np.all(zgw >= z - CFG.groundwater.max_depth_m)
    d = island["diag"]
    assert 0.0 <= d["shallow_fraction"] <= 1.0
    assert d["shallow_fraction"] <= d["shallow_fraction_with_water"] <= 1.0
    assert d["gravity_m_s2"] == pytest.approx(
        gravity(CFG.planet.radius_m, CFG.planet.mean_density_kg_m3), rel=1e-15
    )
    assert d["n_water"] == int(water.sum())
    dry = ~water
    shallow = dry & (z - zgw <= SHALLOW_DEPTH_M)
    assert d["shallow_fraction"] == pytest.approx(g.area[shallow].sum() / g.area[dry].sum())


def test_groundwater_head_rises_away_from_water(island):
    """물가에서 멀수록 수두(z_gw − 물가 수위)가 높아집니다(띠 대수층 해의 성질)."""
    g, zgw, wb, water = island["g"], island["zgw"], island["wb"], island["is_water"]
    delta, src = nearest_source(g, water)
    head = zgw - wb["water_level_m"][src]
    dry = ~water
    below = dry & (zgw < island["z"])  # 지표로 자르지 않은 칸은 물가 수위 이상
    assert np.all(head[below] >= 0.0)
    rho = spearmanr(delta[dry], head[dry]).statistic
    assert rho > 0.5


def synthetic_valley(ny=20, nx=81, dx=25.0, wall=0.3):
    """가운데 열이 강인 V 자 골짜기. 반환: graph, z, is_river, h_w, 강까지 거리 |x − x_r|."""
    g = flat_graph(ny, nx, dx)
    i = np.arange(g.n_cells) % nx
    x_r = (nx // 2 + 0.5) * dx
    dist = np.abs(g.pos[:, 0] - x_r)
    z = 100.0 + wall * dist
    river = i == nx // 2
    h = np.where(river, z - 0.1, np.nan)
    return g, z, river, h, dist


def test_water_table_strip_matches_dupuit():
    """V 자 골짜기(암석·경사 균일)에서 H = R_g·δ·(2L − δ)/(2T) 와 1e-9 안, 강에서 멀수록 오름."""
    nx, dx = 81, 25.0
    g, z, river, h, dist = synthetic_valley(nx=nx, dx=dx)
    rock = np.full(g.n_cells, rk.LIMESTONE, dtype=np.uint8)
    slope = np.full(g.n_cells, 0.3)
    runoff = 0.2
    cfg = CFG.with_overrides({"groundwater.recharge_fraction": 0.5})
    zgw, diag = water_table(g, z, h, river, rock, slope, runoff, cfg, gravity=9.81)
    K = 10.0**-11.8 * RHO_WATER * 9.81 / MU_WATER * SECONDS_PER_YEAR
    T = K * cfg.groundwater.fan_alpha_m / (1.0 + cfg.groundwater.fan_beta * 0.3)
    L = (nx // 2) * dx  # 같은 줄의 강 칸이 출발점, 분수령은 격자 끝
    H = 0.5 * runoff * dist * (2.0 * L - dist) / (2.0 * T)
    # 줄마다 강 수면이 같으므로 h_d 는 그 줄 강 칸의 수면 = 99.9 m
    expect = np.where(river, h, 99.9 + H)
    assert np.all(expect < z) and np.all(
        expect > z - cfg.groundwater.max_depth_m
    )  # 자르지 않는 경우
    np.testing.assert_allclose(zgw, expect, rtol=1e-12, atol=1e-9)
    img = g.as_image(zgw)
    c = nx // 2
    assert np.all(np.diff(img[:, c:], axis=1) > 0)  # 동쪽으로 멀어질수록 오름
    assert np.all(np.diff(img[:, : c + 1], axis=1) < 0)  # 서쪽도 대칭
    assert diag["shallow_fraction"] == 0.0


def test_water_table_clips_to_surface_and_max_depth():
    g, z, river, h, dist = synthetic_valley(wall=0.3)
    rock = np.full(g.n_cells, rk.SHALE, dtype=np.uint8)  # 투수성 낮음 → 지표까지 차오름
    zgw, diag = water_table(g, z, h, river, rock, np.full(g.n_cells, 0.3), 1.0, CFG)
    dry = ~river
    assert np.all(zgw[dry] == z[dry])
    assert diag["shallow_fraction"] == 1.0
    # 물이 하나도 없으면 z − max_depth
    zgw2, d2 = water_table(g, z, h, np.zeros(g.n_cells, bool), rock, np.zeros(g.n_cells), 1.0, CFG)
    np.testing.assert_allclose(zgw2, z - CFG.groundwater.max_depth_m)
    assert d2["n_water"] == 0


def test_hydraulic_conductivity_values():
    g0 = gravity(CFG.planet.radius_m, CFG.planet.mean_density_kg_m3)
    K = hydraulic_conductivity(np.array([rk.LIMESTONE, rk.SHALE]), g0)
    expect = 10.0 ** np.array([-11.8, -16.5]) * 1000.0 * g0 / 1e-3 * 3.15576e7
    np.testing.assert_allclose(K, expect, rtol=1e-14)
    assert 400.0 < K[0] < 600.0  # 석회암 약 490 m/yr
    with pytest.raises(ValueError, match="암석 번호"):
        hydraulic_conductivity(np.array([12]), g0)


# ---------------------------------------------------------------- 8.4 동굴
def check_cave_invariants(g, z, zgw, cols, caves, cfg):
    r = cfg.caves.passage_radius_m
    band = cfg.caves.entrance_tolerance_m + r
    ent = caves["cave_entrance"]
    levels = [caves[f"cave_level_{k}_m"] for k in range(cfg.caves.levels)]
    n_cave = 0
    for k, lv in enumerate(levels):
        fin = np.isfinite(lv)
        n_cave += int(fin.sum())
        is_ent = ((ent >> k) & 1).astype(bool)
        assert not (is_ent & ~fin).any()  # 입구 비트는 그 층 값이 있는 칸에만
        if fin.any():
            # 동굴 ⊂ 녹는 암석 100% (설계도 7장 점수표)
            rock = rock_at(cols.bottom, cols.rock, np.where(fin, lv, 0.0))[fin]
            assert np.all(rk.SOLUBLE[rock])
            # 입구가 아니면 땅 겉보다 2r 넘게 아래
            inner = fin & ~is_ent
            assert np.all(lv[inner] < z[inner] - 2.0 * r)
            # 입구는 층이 땅 겉 띠 안이거나, 이웃 지표가 층 + 띠 아래(골짜기 벽)
            e = np.flatnonzero(is_ent)
            nb = g.nbr[e]
            z_nb = np.where(nb >= 0, z[np.maximum(nb, 0)], np.inf).min(axis=1)
            ok = (np.abs(z[e] - lv[e]) <= band) | (z_nb <= lv[e] + band)
            assert ok.all()
    if levels:
        fin0 = np.isfinite(levels[0])
        np.testing.assert_array_equal(levels[0][fin0], zgw[fin0])  # 0층 = 지금 지하수면
    for k in range(1, len(levels)):
        both = np.isfinite(levels[k - 1]) & np.isfinite(levels[k])
        assert np.all(levels[k - 1][both] <= levels[k][both])
    if n_cave:
        assert ent.any()  # 동굴이 있으면 입구가 적어도 하나
    return n_cave


@pytest.mark.parametrize("overrides", [{}, {"groundwater.recharge_fraction": 0.05}])
def test_cave_invariants_on_island(island, overrides):
    cfg = CFG.with_overrides(overrides)
    g, z, cols, wb = island["g"], island["z"], island["cols"], island["wb"]
    if overrides:
        zgw, _ = water_table(
            g, z, wb["water_level_m"], island["is_water"], island["srock"],
            island["res"].slope, island["R"], cfg,
        )  # fmt: skip
    else:
        zgw = island["zgw"]
    caves = cave_levels(g, z, zgw, wb["valley_depth_m"], cols, cfg)
    assert set(caves) == {"cave_level_0_m", "cave_level_1_m", "cave_entrance"}
    assert caves["cave_entrance"].dtype == np.uint8
    n_cave = check_cave_invariants(g, z, zgw, cols, caves, cfg)
    assert n_cave > 100
    for k in range(2):
        assert np.isfinite(caves[f"cave_level_{k}_m"]).any()  # 두 층 모두 생김
        assert ((caves["cave_entrance"] >> k) & 1).any()


def canyon(exhumation: float):
    """석회암 탁상지를 강이 200 m 깊이로 판 협곡(벽 경사 1, 위는 거의 평평한 사암 고원)."""
    ny, nx, dx = 12, 61, 25.0
    g = flat_graph(ny, nx, dx)
    i = np.arange(g.n_cells) % nx
    dist = np.abs(g.pos[:, 0] - (nx // 2 + 0.5) * dx)
    z = 100.0 + np.minimum(dist, 200.0) + 0.01 * np.maximum(dist - 200.0, 0.0)
    river = i == nx // 2
    h = np.where(river, z - 0.2, np.nan)
    slope = np.where(dist <= 200.0, 1.0, 0.01)
    cols = platform_columns(g.n_cells, exhumation)
    srock = surface_rock(cols, z)
    zgw, _ = water_table(g, z, h, river, srock, slope, 0.3, CFG)
    vd = valley_depth(g, z, h, river, CFG.caves.valley_window_m)
    return g, z, zgw, vd, cols, dist


def test_canyon_caves_open_on_valley_walls():
    g, z, zgw, vd, cols, dist = canyon(EXHUMATION_M)
    caves = cave_levels(g, z, zgw, vd, cols, CFG)
    check_cave_invariants(g, z, zgw, cols, caves, CFG)
    ent = caves["cave_entrance"]
    up = caves["cave_level_1_m"]
    # 위층(옛 지하수면, 마름)은 강 위 f_k·골짜기 깊이 근처에서 협곡 벽과 만나 입구가 생깁니다.
    e1 = ((ent >> 1) & 1).astype(bool)
    assert e1.any() and np.isfinite(up).sum() > e1.sum()
    assert np.all((dist[e1] > 0) & (dist[e1] <= 200.0))  # 입구는 벽 위
    lift = CFG.caves.incision_fraction * vd[e1]
    assert np.all(np.abs(z[e1] - 100.0 - lift) < 60.0)
    # 아래층(지금 지하수면, 잠김)은 강가에서 땅 겉과 만납니다.
    e0 = (ent & 1).astype(bool)
    assert e0.any() and np.all(dist[e0] <= 200.0)
    # 고원 아래는 두 층 모두 덮인 동굴
    plateau = dist > 300.0
    assert np.isfinite(caves["cave_level_0_m"][plateau]).all()
    assert np.isfinite(up[plateau]).all()


def test_no_caves_outside_soluble_rock():
    # 깎인 두께 150 m: 지하수면 근처(100~180 m)가 사암이라 녹는 암석이 없음
    g, z, zgw, vd, cols, dist = canyon(150.0)
    caves = cave_levels(g, z, zgw, vd, cols, CFG)
    assert np.isnan(caves["cave_level_0_m"]).all() and np.isnan(caves["cave_level_1_m"]).all()
    assert not caves["cave_entrance"].any()


def test_cave_levels_without_valley_depth_keep_level_zero_only():
    g, z, zgw, vd, cols, dist = canyon(EXHUMATION_M)
    caves = cave_levels(g, z, zgw, np.full(g.n_cells, np.nan), cols, CFG)
    assert np.isfinite(caves["cave_level_0_m"]).any()
    assert np.isnan(caves["cave_level_1_m"]).all()
    assert not (caves["cave_entrance"] & 2).any()


# ---------------------------------------------------------------- 8.2 흙
def test_soil_formula_hand_values():
    T = np.array([15.0, 15.0, 15.0, 15.0, 40.0, 15.0, 15.0, 15.0, 15.0])
    P = np.array([1.0, 1.0, 1.0, 1.0, 3.0, 1.0, 1.0, 1.0, 1.0])
    U = np.array([1e-4, 1e-3, 1e-4, 0.0, 1e-4, 0.0, 0.0, 0.0, 1e-4])
    S = np.array([0.1, 0.1, 0.9, 0.1, 0.1, 0.001, 0.03, 0.001, 0.03])
    Q = np.array([1e7, 1e7, 1e7, 1e7, 1e7, 1e9, 1e9, 1e5, 1e7])
    Qs = np.array([0.0, 0.0, 0.0, 0.0, 0.0, 1e5, 1e5, 1e2, 0.0])
    fan = np.array([0.0, 0, 0, 0, 0, 0, 0, 0, 2.5])
    n = T.shape[0]
    result = {"discharge_m3_per_yr": Q, "sediment_flux_m3_per_yr": Qs}
    rock = np.full(n, rk.LIMESTONE, np.uint8)
    out = soil_and_alluvium(np.zeros(n), S, U, T, P, result, fan, rock, CFG)
    s = CFG.soil
    P0 = soil_production_max(T, P, CFG)
    assert P0[0] == pytest.approx(s.production_max_m_per_yr)  # T = 15 °C, P = 1 m/yr → 배율 1
    assert P0[4] == s.production_max_cap_m_per_yr  # 덥고 습하면 상한 2.5 mm/yr
    h = out["soil_thickness_m"]
    assert h[0] == pytest.approx(s.decay_depth_m * np.log(3e-4 / 1e-4), rel=1e-12)
    assert h[1] == 0.0  # 깎임 > 생산 → 맨 암반
    assert h[2] == 0.0  # 경사 > bare_slope → 맨 암반
    assert h[3] == s.thickness_cap_m  # E = 1e-7 → h₀·ln(3000) ≈ 4 m 를 3 m 로 자름
    assert h[4] == pytest.approx(s.decay_depth_m * np.log(2.5e-3 / 1e-4), rel=1e-12)
    np.testing.assert_array_equal(out["bare_rock"], h + out["alluvium_m"] == 0)
    a = out["alluvium_m"]
    # Q = 1e9, S = 0.001, 퇴적 지배 → alluvium_max·1·(1 − sst(0.1))
    assert a[5] == pytest.approx(s.alluvium_max_m * (1.0 - smoothstep01(0.1)), rel=1e-12)
    assert a[6] == 0.0  # S ≥ 0.02
    assert a[7] == 0.0  # Q < 1e6
    assert a[8] == 2.5  # 선상지: 올린 높이
    assert np.all(a[:5] == 0.0)
    ocean = np.zeros(n, bool)
    ocean[0] = True
    out2 = soil_and_alluvium(np.zeros(n), S, U, T, P, result, fan, rock, CFG, is_ocean=ocean)
    assert out2["soil_thickness_m"][0] == 0.0 and not out2["bare_rock"][0]


def test_soil_invariants_on_island(island):
    g, z, res, ocean = island["g"], island["z"], island["res"], island["is_ocean"]
    n = g.n_cells
    T = np.full(n, 12.0)
    P = np.full(n, 0.8)
    fan = np.zeros(n)
    fan[np.flatnonzero(~ocean)[:5]] = 1.5
    # 경사는 지표 암석의 S_crit(사암 0.75, 석회암 0.8) 를 넘지 않으므로 기본 bare_slope 0.8 로는
    # '가파르면 맨 암반' 규칙이 이 섬에서 한 칸도 걸리지 않습니다. 규칙을 실제로 보려고 0.6 으로
    # 둡니다.
    cfg = CFG.with_overrides({"soil.bare_slope": 0.6})
    out = soil_and_alluvium(
        z, res.slope, island["U"], T, P, res, fan, island["srock"], cfg, is_ocean=ocean
    )
    h, a, bare = out["soil_thickness_m"], out["alluvium_m"], out["bare_rock"]
    s = cfg.soil
    assert np.all(h[res.slope > s.bare_slope] == 0.0)
    assert np.all((h >= 0.0) & (h <= s.thickness_cap_m))
    assert h[~ocean].max() > 0.5 and (res.slope[~ocean] > s.bare_slope).any()
    # 충적층은 완만한 퇴적 지배 칸이거나 선상지에만
    dep = (
        CFG.landscape.deposition_g * CFG.climate.runoff_ref_m_per_yr * res.sediment_flux
        / res.discharge > island["U"]
    ) & (res.slope < 0.02)  # fmt: skip
    assert np.all((a == 0.0) | dep | (fan > 0.0))
    assert np.all(a <= s.alluvium_max_m + fan)
    assert np.all(h[ocean] == 0.0) and np.all(a[ocean] == 0.0) and not bare[ocean].any()
    np.testing.assert_array_equal(bare, ~ocean & (h == 0.0) & (a == 0.0))


# ---------------------------------------------------------------- 입력 확인
def test_invalid_inputs_raise_korean_errors(island):
    g, z, res, ocean, wb = island["g"], island["z"], island["res"], island["is_ocean"], island["wb"]
    with pytest.raises(ValueError, match="bool"):
        water_bodies(g, z, res, ocean.astype(np.int8), CFG)
    with pytest.raises(ValueError, match="receiver"):
        water_bodies(g, z, {"discharge_m3_per_yr": res.discharge}, ocean, CFG)
    bad = wb["water_level_m"].copy()
    bad[np.flatnonzero(island["is_water"])[0]] = np.nan
    with pytest.raises(ValueError, match="water_level"):
        water_table(g, z, bad, island["is_water"], island["srock"], res.slope, 0.2, CFG)
    with pytest.raises(ValueError, match="칸 수"):
        cave_levels(g, z, island["zgw"], wb["valley_depth_m"], platform_columns(5, 0.0), CFG)
    with pytest.raises(ValueError, match="fan_raise"):
        n = g.n_cells
        soil_and_alluvium(
            z, res.slope, island["U"], np.zeros(n), np.ones(n), res, -np.ones(n),
            island["srock"], CFG,
        )  # fmt: skip
    with pytest.raises(ValueError, match="FIELDS"):
        cave_levels(
            g, z, island["zgw"], wb["valley_depth_m"], island["cols"],
            CFG.with_overrides({"caves.levels": 3}),
        )  # fmt: skip


# ---------------------------------------------------------------- 속도 (약 1.6M 칸)
@pytest.mark.slow
def test_benchmark_1280_squared():
    from bpcg.core.noise import fbm3
    from bpcg.hydro.accumulate import accumulate
    from bpcg.hydro.depressions import fill_epsilon
    from bpcg.hydro.routing import d8_receivers, topo_order

    cfg = load_config("earth", "laptop")
    n = 1280
    g = flat_graph(n, n, 25.0, jitter=0.4, seed=3)
    z0 = 600.0 * (fbm3(g.pos / 8000.0, 7) + 0.5) + 2e-2 * (g.pos[:, 1] - g.pos[:, 1].min())
    cell = np.arange(g.n_cells)
    is_outlet = (cell // n == n - 1) & (np.abs(cell % n - n // 2) <= 2)
    ocean = np.zeros(g.n_cells, bool)
    z = fill_epsilon(z0, g.nbr, is_outlet, 1e-3)
    rcv, slope, _ = d8_receivers(z, g.nbr, g.dist, is_outlet)
    order = topo_order(rcv)
    R = np.full(g.n_cells, 0.5)
    U = np.full(g.n_cells, 2e-4)
    res = {
        "receiver": rcv,
        "order": order,
        "discharge_m3_per_yr": accumulate(rcv, order, g.area * R),
        "sediment_flux_m3_per_yr": accumulate(rcv, order, g.area * U),
    }
    cols = platform_columns(g.n_cells, 500.0, cfg)
    srock = surface_rock(cols, z)
    T = np.full(g.n_cells, 15.0)
    P = np.ones(g.n_cells)
    times = {}
    for _ in range(2):  # 두 번째가 컴파일 뒤 시간
        t = time.perf_counter()
        wb = water_bodies(g, z, res, ocean, cfg)
        times["water"] = time.perf_counter() - t
        water = wb["is_lake"] | wb["is_river"]
        t = time.perf_counter()
        zgw, _ = water_table(g, z, wb["water_level_m"], water, srock, slope, R, cfg)
        times["groundwater"] = time.perf_counter() - t
        t = time.perf_counter()
        soil_and_alluvium(z, slope, U, T, P, res, None, srock, cfg)
        times["soil"] = time.perf_counter() - t
        t = time.perf_counter()
        cave_levels(g, z, zgw, wb["valley_depth_m"], cols, cfg)
        times["caves"] = time.perf_counter() - t
    print("subsurface 1280² 시간 [s]:", {k: round(v, 3) for k, v in times.items()})
    assert wb["is_river"].sum() > 1000
    assert sum(times.values()) < 10.0
