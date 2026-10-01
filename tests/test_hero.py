"""히어로 유역 검사 (docs/pipeline.md 9장): 평면 히어로, 영역 변환, 후보 찾기."""

import math

import numpy as np
import pytest

from bpcg.core.config import load_config
from bpcg.core.fields import FIELDS
from bpcg.core.graph import flat_graph, sphere_graph
from bpcg.geology import rocks as rk
from bpcg.geology.model import (
    FOLD_THRUST,
    PLATFORM,
    LayerColumns,
    build_columns,
    rock_at,
)
from bpcg.hero.domain import (
    edge_cells,
    edge_hit,
    hero_flat_graph,
    hero_graph,
    hero_grid_size,
    local_to_unit,
    tangent_frame,
)
from bpcg.hero.finder import (
    SCORE_WEIGHTS,
    HeroSite,
    carbonate_fraction,
    dry_fraction,
    find_hero,
    score_cells,
    site_from_cell,
    uplift_gradient,
)
from bpcg.hero.flat import (
    OUTLET_CELLS,
    OUTLET_Z_M,
    RUNOFF_M_PER_YR,
    U_BASE_M_PER_YR,
    U_PEAK_M_PER_YR,
    flat_hero,
    flat_uplift,
    precip_for_runoff,
    presolve_surface,
)
from bpcg.hydro.network import outlet_of
from bpcg.hydro.routing import topo_order
from bpcg.pipeline import HeroState, PlanetState, run_stages_2_to_4
from bpcg.planet.climate import budyko_runoff

R_EARTH = 6_371_000.0

# 2~4단계가 늘 만드는 필드 (pipeline.run_stages_2_to_4, 평면이라 relief_m·z_mean_m 없음).
# uplift_m_per_yr 는 솔버가 실제로 쓴 융기(SolverResult.uplift_effective)를 그대로 돌려줍니다.
STAGE_FIELDS = {
    "uplift_m_per_yr",
    "z_m",
    "receiver",
    "drainage_area_m2",
    "discharge_m3_per_yr",
    "sediment_flux_m3_per_yr",
    "slope",
    "k_s",
    "s_crit",
    "not_steady",
    "fan",
    "temperature_c",
    "water_level_m",
    "is_lake",
    "is_river",
    "river_width_m",
    "river_depth_m",
    "valley_depth_m",
    "surface_rock",
    "soil_thickness_m",
    "alluvium_m",
    "bare_rock",
    "water_table_m",
    "cave_level_0_m",
    "cave_level_1_m",
    "cave_entrance",
    "strata_bottom_m",
    "strata_rock",
}
FLAT_INPUT_FIELDS = {
    "uplift_m_per_yr",
    "exhumation_m",
    "precip_m_per_yr",
    "pet_m_per_yr",
    "runoff_m_per_yr",
    "runoff_eff_m_per_yr",
    "template_id",
    "fold_phase",
    "dist_convergent_m",
    "is_ocean",
}
# 물이 없으면 NaN 인 필드 (FIELDS 설명), 나머지 실수 필드는 모두 유한해야 함
NAN_ALLOWED = {"water_level_m", "cave_level_0_m", "cave_level_1_m"}


@pytest.fixture(scope="module")
def cfg():
    return load_config("earth", "tiny")


@pytest.fixture(scope="module")
def flat_tiny(cfg):
    return flat_hero(cfg)


@pytest.fixture(scope="module")
def flat_tiny_rivers():
    """강 문턱을 작은 영역에 맞춘 평면 히어로 (동굴 검사용).

    tiny 영역(6.4 km, 41 km²)은 기본 강 문턱 0.3 m³/s(유출 0.5 m/yr 에서 상류 약 19 km²)보다 작아
    강이 출구 옆 몇 칸뿐입니다. 기본값(n = 2)에서는 산지 앞 경사 차이가 fans.slope_drop_ratio 에
    못 미쳐 선상지 호수도 없습니다. 그러면 물가가 멀어 띠 대수층 수위 H 가 기복을 넘고 지하수면이
    모든 칸에서 지표로 잘려(z_gw = z) 덮인 동굴이 생기지 않습니다. 문턱을 0.02 m³/s(약 1.3 km²)로
    낮추면 석회암이 드러난 산허리까지 강이 닿아 지하수면이 땅속으로 내려갑니다.
    """
    cfg = load_config("earth", "tiny", overrides={"rivers.min_discharge_m3_per_s": 0.02})
    return cfg, flat_hero(cfg)


@pytest.fixture(scope="module")
def flat_256():
    # 조금 큰 평면 히어로: 12.8 km, 50 m → 256² (65,536칸).
    cfg = load_config(
        "earth", "tiny", overrides={"profile.hero.size_m": 12_800.0, "profile.hero.spacing_m": 50.0}
    )
    return cfg, flat_hero(cfg)


# ---------------------------------------------------------------- 공통 불변식
def _check_hero_invariants(hero: HeroState, cfg, outlet_cells: np.ndarray) -> None:
    f = hero.fields
    g = hero.graph
    n = g.n_cells
    for k, v in f.items():
        assert k in FIELDS, k
        v = np.asarray(v)
        assert v.shape[0] == n, k
        if v.dtype.kind == "f" and k not in NAN_ALLOWED:
            assert np.isfinite(v).all(), k

    # 모든 칸이 출구 칸으로 흘러 나감: 뿌리(수신 셀이 자기 자신)는 정확히 출구 칸들
    rcv = np.asarray(f["receiver"], dtype=np.int64)
    roots = np.flatnonzero(rcv == np.arange(n))
    assert set(roots.tolist()) == set(np.asarray(outlet_cells).tolist())
    order = topo_order(rcv)
    assert np.isin(outlet_of(rcv, order), outlet_cells).all()
    assert g.boundary_mask()[outlet_cells].all()

    # 물 수지 (들어오는 물 포함): 출구 Q 합 = Σ A·R_eff + 들어오는 물, 상대 1e-12
    inflow = hero.diag.get("extra_inflow_m3_per_yr")
    src = g.area * np.asarray(f["runoff_eff_m_per_yr"]) + (0.0 if inflow is None else inflow)
    q_out = np.asarray(f["discharge_m3_per_yr"])[outlet_cells].sum()
    assert abs(q_out - src.sum()) <= 1e-12 * src.sum()

    # 수면은 물 칸에만 있음 (바다 없음)
    wet = np.asarray(f["is_lake"]) | np.asarray(f["is_river"])
    assert np.array_equal(np.isfinite(f["water_level_m"]), wet)

    # 지하수면 규칙 (pipeline.md 8.3): z_gw ≤ max(z, h_w), 물 칸은 z_gw = h_w, z_gw ≥ z − max_depth
    z = np.asarray(f["z_m"])
    zg = np.asarray(f["water_table_m"])
    hw = np.asarray(f["water_level_m"])
    top = np.where(wet, np.maximum(z, np.nan_to_num(hw, nan=-np.inf)), z)
    assert (zg <= top + 1e-9).all()
    assert np.allclose(zg[wet], hw[wet], rtol=0, atol=1e-9)
    assert (zg >= z - float(cfg.groundwater.max_depth_m) - 1e-9).all()

    # 동굴은 녹는 암석에만 (pipeline.md 8.4)
    for k in ("cave_level_0_m", "cave_level_1_m"):
        lv = np.asarray(f[k])
        cells = np.flatnonzero(np.isfinite(lv))
        if cells.size:
            rock = rock_at(hero.columns.bottom[cells], hero.columns.rock[cells], lv[cells])
            assert rk.SOLUBLE[rock].all(), k

    # 강 구간은 수신 셀로 이어짐
    for seg in hero.rivers:
        assert np.array_equal(rcv[seg[:-1]], seg[1:])

    # 점수표: 판정이 있는 check 는 모두 합격. 격자 정렬 지수는 빼고 봄 — 평면 히어로의 남북
    # 단면 융기가 강을 남쪽으로 줄 세워 6칸 직선 사슬이 많아지고, 하천 칸이 적은 작은 영역에서는
    # 표본이 적어 값이 크게 흔들립니다(합격선 < 1.3 은 구면 L0 기준).
    card = hero.diag["scorecard"]
    failed = [k for k, v in card.items() if k.startswith("hero.") and v["pass"] is False]
    assert set(failed) <= {"hero.grid_alignment"}, failed


# ---------------------------------------------------------------- 평면 히어로
def test_flat_hero_fields_present(flat_tiny):
    f = flat_tiny.fields
    missing = (STAGE_FIELDS | FLAT_INPUT_FIELDS) - set(f)
    assert not missing
    assert flat_tiny.site is None
    assert isinstance(flat_tiny.columns, LayerColumns)
    assert flat_tiny.graph.kind == "flat"
    assert (np.asarray(f["template_id"]) == FOLD_THRUST).all()


def test_flat_hero_invariants(cfg, flat_tiny):
    cells = flat_tiny.diag["boundary"]["outlet_cells"]
    n, _ = hero_grid_size(cfg)
    # 출구: 남쪽 가장자리(마지막 행) 가운데 5칸, z = 200 m
    assert len(cells) == OUTLET_CELLS
    assert (cells // n == n - 1).all()
    assert abs(np.mean(cells % n) - (n // 2)) <= 1
    assert np.allclose(flat_tiny.fields["z_m"][cells], OUTLET_Z_M)
    _check_hero_invariants(flat_tiny, cfg, cells)


def test_flat_hero_has_river_and_caves(flat_tiny, flat_tiny_rivers):
    f = flat_tiny.fields
    assert len(flat_tiny.rivers) >= 1
    assert np.asarray(f["is_river"]).sum() >= 1
    # 깎인 두께를 지표 + U·T_e 로 두어 템플릿 1 의 여러 층이 드러납니다.
    rocks = set(np.unique(f["surface_rock"]).tolist())
    assert rk.LIMESTONE in rocks and len(rocks) >= 3
    # 그 석회암 산허리까지 강이 닿으면 석회암 동굴과 입구가 생깁니다 (flat_tiny_rivers 설명).
    cfg_r, hero_r = flat_tiny_rivers
    fr = hero_r.fields
    assert rk.LIMESTONE in set(np.unique(fr["surface_rock"]).tolist())
    assert np.isfinite(fr["cave_level_0_m"]).sum() >= 1
    assert np.asarray(fr["cave_entrance"]).any()
    _check_hero_invariants(hero_r, cfg_r, hero_r.diag["boundary"]["outlet_cells"])


def test_flat_hero_solver_converged_and_deterministic(cfg, flat_tiny):
    assert flat_tiny.diag["solver"]["converged"]
    again = flat_hero(cfg)
    assert np.array_equal(again.fields["z_m"], flat_tiny.fields["z_m"])
    assert np.array_equal(again.fields["receiver"], flat_tiny.fields["receiver"])


def test_flat_hero_larger_domain(flat_256):
    cfg, hero = flat_256
    n, dx = hero_grid_size(cfg)
    assert (n, dx) == (256, 50.0)
    assert hero.graph.n_cells == 256 * 256
    _check_hero_invariants(hero, cfg, hero.diag["boundary"]["outlet_cells"])
    assert len(hero.rivers) >= 1


def test_flat_uplift_profile():
    L = 10_000.0
    y = np.array([0.0, 0.3 * L, L])
    U = flat_uplift(y, L)
    assert U[1] == pytest.approx(U_BASE_M_PER_YR + U_PEAK_M_PER_YR, rel=1e-12)
    assert U[2] == pytest.approx(
        U_BASE_M_PER_YR + U_PEAK_M_PER_YR * math.exp(-((0.7 / 0.25) ** 2)), rel=1e-12
    )
    assert U[0] > U[2]  # 봉우리가 북쪽 가장자리에 더 가까움


def test_precip_for_runoff_inverts_budyko():
    for pet in (0.3, 0.925, 2.0):
        P = precip_for_runoff(RUNOFF_M_PER_YR, pet)
        R = budyko_runoff(np.array([P]), np.array([pet]))[0]
        assert R == pytest.approx(RUNOFF_M_PER_YR, rel=1e-9)
    with pytest.raises(ValueError):
        precip_for_runoff(0.0, 1.0)


def test_flat_exhumation_keeps_column_top_above_presolve(cfg, flat_tiny):
    g = flat_tiny.graph
    z_pre = presolve_surface(cfg, g.pos[:, 0], g.pos[:, 1])
    assert np.isfinite(z_pre).all()
    assert (flat_tiny.fields["exhumation_m"] >= np.maximum(z_pre, 0.0) - 1e-9).all()


# ---------------------------------------------------------------- 영역
def _site(center, cfg) -> HeroSite:
    c = np.asarray(center, dtype=np.float64)
    c = c / np.linalg.norm(c)
    e, nn = tangent_frame(c, np.asarray(cfg.planet.axis, dtype=np.float64))
    return HeroSite(center_unit=c, east=e, north=nn, l0_cell=0, score=0.0)


def test_tangent_frame_orthonormal_and_pole_fallback():
    axis = np.array([0.0, 0.0, 1.0])
    for c in ([1.0, 0.0, 0.0], [0.3, -0.5, 0.6], [0.0, 0.0, 1.0]):
        c = np.asarray(c) / np.linalg.norm(c)
        e, nn = tangent_frame(c, axis)
        M = np.stack([e, nn, c])
        assert np.allclose(M @ M.T, np.eye(3), atol=1e-12)
        assert np.allclose(np.cross(e, nn), c, atol=1e-12)  # 동 × 북 = 위
    e, nn = tangent_frame(np.array([1.0, 0.0, 0.0]), axis)
    assert np.allclose(e, [0.0, 1.0, 0.0]) and np.allclose(nn, [0.0, 0.0, 1.0])


def test_hero_graph_maps_local_to_sphere(cfg):
    site = _site([0.5, 0.2, 0.4], cfg)
    graph, unit = hero_graph(site, cfg)
    n, dx = hero_grid_size(cfg)
    assert graph.shape == (n, n) and unit.shape == (n * n, 3)
    assert np.allclose(np.linalg.norm(unit, axis=1), 1.0, atol=1e-12)
    # 원점이 가운데이고 0번 행이 북쪽
    assert abs(graph.pos[:, 0].mean()) < dx and abs(graph.pos[:, 1].mean()) < dx
    assert graph.pos[0, 1] > graph.pos[-1, 1]
    # 대원 거리 = 국소 거리 (32 km 영역에서 상대 1e-5 안, gnomonic 왜곡 (L/R)² 정도)
    s = R_EARTH * np.arccos(np.clip(unit @ site.center_unit, -1, 1))
    d = np.hypot(graph.pos[:, 0], graph.pos[:, 1])
    far = d > 10 * dx
    assert np.allclose(s[far], d[far], rtol=1e-5)
    # 동쪽 점은 east 방향, 북쪽 점은 north 방향
    p = local_to_unit(site, np.array([1000.0, 0.0]), np.array([0.0, 1000.0]), R_EARTH)
    assert (p[0] - site.center_unit) @ site.east > 0
    assert (p[1] - site.center_unit) @ site.north > 0


def test_hero_flat_graph_jitter_and_size(cfg):
    g = hero_flat_graph(cfg)
    n, dx = hero_grid_size(cfg)
    assert g.n_cells == n * n and g.spacing == dx
    ii = np.arange(n * n) % n
    jj = np.arange(n * n) // n
    x0 = -0.5 * n * dx + (ii + 0.5) * dx
    y0 = 0.5 * n * dx - (jj + 0.5) * dx
    off = np.hypot(g.pos[:, 0] - x0, g.pos[:, 1] - y0)
    assert off.max() > 0  # 노드 흔들기
    assert off.max() <= 0.5 * float(cfg.landscape.jitter) * dx * math.sqrt(2) + 1e-9


def test_edge_hit_and_edge_cells():
    n, dx = 64, 100.0
    assert edge_hit((1.0, 0.0), n, dx).edge == "east"
    assert edge_hit((0.0, -1.0), n, dx).edge == "south"
    assert edge_hit((0.0, 2.0), n, dx).edge == "north"
    h = edge_hit((-1.0, 0.5), n, dx)
    assert h.edge == "west"
    assert h.x == pytest.approx(-3200.0) and h.y == pytest.approx(1600.0)
    assert h.index == 16  # 북쪽에서 (3200 − 1600)/100 = 16 번째 행
    with pytest.raises(ValueError):
        edge_hit((0.0, 0.0), n, dx)

    g = flat_graph(n, n, dx)
    border = g.boundary_mask()
    for edge in ("north", "south", "east", "west"):
        for idx in (0, 31, 63):
            cells = edge_cells(n, edge, idx, 5)
            assert len(cells) == 5 and border[cells].all()
            assert len(set(cells.tolist())) == 5
    assert np.array_equal(edge_cells(n, "south", 32, 5), (n - 1) * n + np.arange(30, 35))
    assert np.array_equal(edge_cells(n, "west", 0, 3), np.array([0, n, 2 * n]))
    with pytest.raises(ValueError):
        edge_cells(n, "up", 0, 1)


# ---------------------------------------------------------------- 후보 찾기
def test_uplift_gradient_linear_field():
    g = flat_graph(32, 32, 100.0)
    a, b = 2e-6, -1e-6  # [1/yr]
    U = a * g.pos[:, 0] + b * g.pos[:, 1]
    grad = uplift_gradient(g, U)
    inner = ~g.boundary_mask()
    # 대각 이웃 거리가 √2 배라 8 방향 평균은 정확히 |∇U|²/2 가 아님: 축과 대각 방향 기여가
    # 같아 sqrt(2·mean) = |∇U| (정사각 격자에서 정확)
    assert np.allclose(grad[inner], math.hypot(a, b), rtol=1e-12)


def test_carbonate_fraction_and_dry_fraction(cfg):
    tid = np.array([PLATFORM, PLATFORM])
    sb, sr = build_columns(tid, np.array([0.0, 0.0]), None, cfg)
    # 탁상지(꼭대기 0 m): 사암 0~150 m, 석회암 150~650 m 아래
    z = np.array([-400.0, -250.0, -100.0])
    frac = carbonate_fraction(sb[[0, 0, 0]], sr[[0, 0, 0]], z, np.array([0.0, 200.0, 50.0]))
    assert frac[0] == 1.0  # 석회암 한가운데
    assert frac[1] == pytest.approx(0.5)  # −250 ~ −50 m: 석회암 100 m + 사암 100 m
    assert frac[2] == 0.0  # 사암만
    g = flat_graph(10, 10, 1000.0)
    dry = np.zeros(100, dtype=bool)
    dry[:50] = True  # 북쪽 절반
    f = dry_fraction(g, dry, np.array([0, 99]), 1500.0)
    assert f[0] == 1.0 and f[1] == 0.0


def _synthetic_planet(cfg, hot_cell=None, ocean_all=False) -> PlanetState:
    g = sphere_graph(8, R_EARTH)
    n = g.n_cells
    unit = g.unit()
    ocean = np.ones(n, dtype=bool) if ocean_all else unit[:, 2] < -0.8
    U = np.zeros(n)
    relief = np.zeros(n)
    if hot_cell is not None:
        U[hot_cell] = 2e-3
        relief[hot_cell] = 1000.0
    tid = np.zeros(n, dtype=np.uint8)
    sb, sr = build_columns(tid, np.zeros(n), None, cfg)
    cols = LayerColumns.from_columns(sb, sr)
    fields = {
        "is_ocean": ocean,
        "uplift_m_per_yr": U,
        "z_m": np.where(ocean, -3000.0, 10.0),
        "relief_m": relief,
        "precip_m_per_yr": np.full(n, 2.0),
        "pet_m_per_yr": np.full(n, 1.0),
        "strata_bottom_m": sb,
        "strata_rock": sr,
    }
    return PlanetState(graph=g, fields=fields, columns=cols, info={}, diag={})


def test_find_hero_picks_highest_score(cfg):
    g = sphere_graph(8, R_EARTH)
    lat = np.degrees(np.arcsin(g.unit()[:, 2]))
    hot = int(np.flatnonzero(np.abs(lat - 10.0) < 8.0)[0])
    planet = _synthetic_planet(cfg, hot_cell=hot)
    site = find_hero(planet, cfg)
    assert site.l0_cell == hot
    assert 0.0 <= site.score <= 1.0
    assert set(site.parts) == {"uplift_gradient", "carbonate", "relief", "dry_fraction"}
    assert all(0.0 <= v <= 1.0 for v in site.parts.values())
    assert site.parts["relief"] == 1.0 and site.parts["dry_fraction"] == 0.0
    # pipeline.md 9장: 점수 = 0.3·융기 기울기 + 0.4·탄산염 + 0.15·기복 + 0.15·건조 칸 비율
    weights = {"uplift_gradient": 0.3, "carbonate": 0.4, "relief": 0.15, "dry_fraction": 0.15}
    assert SCORE_WEIGHTS == weights
    assert site.score == pytest.approx(
        sum(w * site.parts[k] for k, w in weights.items()), rel=1e-12
    )
    assert site.lat_deg == pytest.approx(lat[hot], abs=1e-9)
    M = np.stack([site.east, site.north, site.center_unit])
    assert np.allclose(M @ M.T, np.eye(3), atol=1e-12)


def test_find_hero_excludes_ocean_and_high_latitude(cfg):
    g = sphere_graph(8, R_EARTH)
    lat = np.degrees(np.arcsin(g.unit()[:, 2]))
    polar = int(np.flatnonzero(lat > 60.0)[0])
    planet = _synthetic_planet(cfg, hot_cell=polar)
    score, parts, cand = score_cells(planet, cfg)
    assert not cand[polar] and score[polar] == 0.0
    assert not cand[planet.fields["is_ocean"]].any()
    assert (np.abs(lat[cand]) <= 55.0).all()
    site = find_hero(planet, cfg)
    assert site.l0_cell != polar and abs(site.lat_deg) <= 55.0
    for v in parts.values():
        assert v.min() >= 0.0 and v.max() <= 1.0


def test_find_hero_raises_without_candidates(cfg):
    with pytest.raises(ValueError):
        find_hero(_synthetic_planet(cfg, ocean_all=True), cfg)
    with pytest.raises(ValueError):
        site_from_cell(_synthetic_planet(cfg), 10**9, cfg)


def test_run_stages_validation(cfg):
    g = flat_graph(8, 8, 100.0)
    n = g.n_cells
    out = np.zeros(n, dtype=bool)
    out[0] = True
    cols = LayerColumns.from_columns(*build_columns(np.zeros(n, np.uint8), np.zeros(n), None, cfg))
    args = (g, out, 0.0, np.full(n, 1e-4), np.full(n, 0.5), np.full(n, 1.0), cols, cfg)
    with pytest.raises(ValueError):  # 평면인데 위도·기온이 없음
        run_stages_2_to_4(*args)
    ocean = np.zeros(n, dtype=bool)
    ocean[5] = True
    with pytest.raises(ValueError):  # 바다가 출구가 아님
        run_stages_2_to_4(*args, lat_deg=30.0, is_ocean=ocean)
    st = run_stages_2_to_4(*args, lat_deg=30.0)
    assert "relief_m" not in st.fields  # 기복 보정은 구면만
    assert set(st.fields) == STAGE_FIELDS
    # 솔버는 융기를 바꾸지 않으므로 돌려준 융기는 넣은 값과 같습니다.
    np.testing.assert_array_equal(st.fields["uplift_m_per_yr"], args[3])
