"""땅속 검사 (docs/pipeline.md 8.1~8.4, 설계도 4장 사슬 3): 물·흙·지하수면·동굴 필드.

C# 이 쓴 행성·히어로 묶음과, 동굴이 꼭 생기도록 강 문턱을 낮춘 평면 히어로(cave_hero)를 봅니다.
"""

import numpy as np
import pytest
from bundles import load_config_dict, rock_at

SOLUBLE = {3, 9}  # 석회암, 대리암 (Geology/Rocks.cs 의 녹는 암석)


@pytest.fixture(scope="module")
def cfg():
    return load_config_dict()


@pytest.fixture(params=["planet", "hero", "cave_hero"])
def bundle(request):
    return request.getfixturevalue(request.param)


def _land(b) -> np.ndarray:
    return ~b["is_ocean"] if "is_ocean" in b.fields else np.ones(b.graph.n_cells, dtype=bool)


def test_water_levels(bundle):
    f = bundle.fields
    z = f["z_m"].astype(np.float64)
    wl = f["water_level_m"].astype(np.float64)
    ocean = ~_land(bundle)
    wet = f["is_lake"] | f["is_river"] | ocean
    assert np.array_equal(np.isfinite(wl), wet)  # 물이 없으면 NaN
    assert (wl[ocean] == 0).all()
    lake = f["is_lake"]
    assert (wl[lake] >= z[lake] - 1e-3).all()  # 호수 수면은 바닥 위
    river = f["is_river"] & ~lake
    depth = f["river_depth_m"].astype(np.float64)
    # 강 수면은 칸 평균 지표보다 낮은 물길 안 (바닥 = 수면 − 깊이)
    assert (wl[river] <= z[river] + 1e-3).all() and (
        wl[river] >= z[river] - depth[river] - 1e-3
    ).all()


def test_river_levels_never_rise_downstream(bundle):
    f = bundle.fields
    rcv = f["receiver"].astype(np.int64)
    n = rcv.size
    both = f["is_river"] & f["is_river"][rcv] & (rcv != np.arange(n))
    wl = f["water_level_m"].astype(np.float64)
    assert (wl[rcv[both]] <= wl[both] + 1e-3).all()


def test_water_table(bundle, cfg):
    f = bundle.fields
    z = f["z_m"].astype(np.float64)
    wt = f["water_table_m"].astype(np.float64)
    land = _land(bundle)
    lake = f["is_lake"]
    dry = land & ~lake
    assert (wt[dry] <= z[dry] + 1e-3).all()  # 지표에서 자름
    assert np.isfinite(wt).all()  # 깊이로는 자르지 않지만 값은 늘 있음
    assert (wt[~land] == 0).all()
    assert np.allclose(wt[lake], f["water_level_m"][lake], atol=1e-3)  # 호수에서는 수면
    # 아래 지하수면(막는 층 아래 녹는 암석의 물 높이)은 맨 위 지하수면보다 높지 않고,
    # 물 칸에서는 같음
    lower = f["water_table_lower_m"].astype(np.float64)
    assert np.isfinite(lower).all() and (lower <= wt + 1e-3).all()
    wet = lake | f["is_river"] | ~land
    assert np.allclose(lower[wet], wt[wet], atol=1e-3)


def test_soil(bundle, cfg):
    f = bundle.fields
    s = cfg["soil"]
    soil = f["soil_thickness_m"]
    assert (soil >= 0).all() and (soil <= float(s["thickness_cap_m"]) + 1e-6).all()
    land = _land(bundle)
    alluvium = f["alluvium_m"]
    off_fan = ~f["fan"]  # 선상지 칸의 충적층은 선상지 퇴적 두께라 상한이 없음
    assert (alluvium >= 0).all() and (alluvium[off_fan] <= float(s["alluvium_max_m"]) + 1e-6).all()
    # 맨 암반 = 흙도 충적층도 없는 육지 칸
    uncovered = (soil == 0) & (alluvium == 0)
    assert np.array_equal(uncovered[land], f["bare_rock"][land])


def test_caves_only_in_soluble_rock_at_water_table(cave_hero):
    f = cave_hero.fields
    z = f["z_m"].astype(np.float64)
    c0 = f["cave_level_0_m"].astype(np.float64)
    has0 = np.isfinite(c0)
    assert has0.any(), "강 문턱을 낮춘 평면 히어로에는 동굴이 있어야 합니다"
    rock = rock_at(cave_hero, np.where(has0, c0, z) - 0.01)
    assert set(np.unique(rock[has0])) <= SOLUBLE
    # 아래층 동굴 = 녹는 암석 안의 지금 물 높이 (아래 지하수면, 잠긴 층)
    assert np.allclose(c0[has0], f["water_table_lower_m"][has0], atol=1e-3)
    c1 = f["cave_level_1_m"].astype(np.float64)
    has1 = np.isfinite(c1)
    rock1 = rock_at(cave_hero, np.where(has1, c1, z) - 0.01)
    assert set(np.unique(rock1[has1])) <= SOLUBLE
    assert (c1[has1 & has0] > c0[has1 & has0]).all()  # 위층(옛 지하수면)은 아래층보다 높음


def _neighbor_gradients(b, values: np.ndarray) -> np.ndarray:
    """값이 있는 이웃 칸 쌍마다 |Δ값| / 거리 [m/m]."""
    nbr, dist = b.graph.nbr, b.graph.dist
    has = np.isfinite(values)
    out = []
    for s in range(nbr.shape[1]):
        k = nbr[:, s]
        ok = (k >= 0) & has
        ok &= has[np.where(k >= 0, k, 0)]
        out.append(np.abs(values[ok] - values[k[ok]]) / dist[ok, s])
    return np.concatenate(out)


def test_lower_caves_lie_flat_in_platform_karst(hero, cfg):
    # 탄산염 탁상지(층이 수평)에서는 석회암 지하수면이 강 높이에 거의 평평하고,
    # 아래층 동굴도 그렇습니다.
    # 지구 카르스트의 지하수면 기울기는 0.001~0.005 입니다(설정 groundwater.karst_gradient).
    c0 = hero["cave_level_0_m"].astype(np.float64)
    assert np.isfinite(c0).any(), "tiny 히어로에는 아래층 동굴이 있어야 합니다"
    g = _neighbor_gradients(hero, c0)
    grad = float(cfg["groundwater"]["karst_gradient"])
    assert np.percentile(g, 90) <= 5 * grad, np.percentile(g, [50, 90, 99])


@pytest.mark.parametrize("level", ["hero", "cave_hero"])
def test_cave_entrances_are_spaced(request, level, cfg):
    b = request.getfixturevalue(level)
    pos = b.graph.pos
    ent = b["cave_entrance"].astype(np.int64)
    spacing = float(cfg["caves"]["entrance_spacing_m"])
    for k in (0, 1):
        idx = np.flatnonzero((ent >> k) & 1)
        if idx.size < 2:
            continue
        d = np.sqrt(((pos[idx, None, :] - pos[None, idx, :]) ** 2).sum(axis=2))
        np.fill_diagonal(d, np.inf)
        assert d.min() > spacing - 1e-6, (k, d.min())


@pytest.mark.parametrize("level", ["hero", "cave_hero"])
def test_cave_entrances(request, level):
    f = request.getfixturevalue(level).fields
    ent = f["cave_entrance"]
    assert set(np.unique(ent)) <= {0, 1, 2, 3}
    assert np.isfinite(f["cave_level_0_m"][(ent & 1) > 0]).all()
    assert np.isfinite(f["cave_level_1_m"][(ent & 2) > 0]).all()


def test_planet_scorecard_cave_and_water_rules(planet, hero):
    for b, lvl in ((planet, "planet"), (hero, "hero")):
        card = b.diag["scorecard"]
        for name in ("cave_in_soluble", "water_rule_violations", "gw_surface_fraction"):
            e = card[f"{lvl}.{name}"]
            assert e["pass"] in (True, None), (lvl, name, e)


def test_valley_depth_where_rivers(bundle):
    vd = bundle["valley_depth_m"]
    if bundle["is_river"].any():
        finite = np.isfinite(vd)
        assert finite.any() and (vd[finite] >= 0).all()
