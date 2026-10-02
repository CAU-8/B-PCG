"""1단계 재료(사슬 1과 기후) 검사 (docs/pipeline.md 4장, bpcg.planet)."""

import math

import numpy as np
import pytest

from bpcg.core.config import load_config
from bpcg.core.cubesphere import cell_of
from bpcg.core.fields import FIELDS
from bpcg.core.graph import flat_graph, sphere_graph
from bpcg.core.hashing import hash_uniform_array
from bpcg.planet.climate import budyko_runoff, generate_climate, latitude_rad
from bpcg.planet.crust import (
    MARGIN_EDGE_FRACTION,
    airy_elevation_m,
    continent_mask,
    generate_crust,
    ocean_depth_m,
    trench_offset_m,
)
from bpcg.planet.materials import (
    CATEGORICAL_FIELDS,
    build_materials,
    transfer_materials,
)
from bpcg.planet.ocean import ocean_mask, ocean_volume, sea_level
from bpcg.planet.plates import (
    CONVERGENT,
    DIVERGENT,
    TRANSFORM,
    classify_boundaries,
    ensure_divergent_boundaries,
    generate_plates,
)
from bpcg.planet.uplift import arc_distance_m, generate_uplift


@pytest.fixture(scope="module")
def cfg():
    return load_config("earth", "tiny")


@pytest.fixture(scope="module")
def planet24(cfg):
    g = sphere_graph(24, cfg.planet.radius_m)
    fields, info = build_materials(g, cfg)
    return g, fields, info


# ---------------------------------------------------------------- 판
def test_plate_ids_cover_all_plates(cfg, planet24):
    g, f, _ = planet24
    pid = f["plate_id"]
    assert pid.dtype == np.int32 and pid.shape == (g.n_cells,)
    assert np.array_equal(np.unique(pid), np.arange(int(cfg.plates.count)))


def test_two_plate_boundary_classification(cfg):
    # 북·남 반구 두 판이 x 축 둘레로 반대로 돌면 +y 쪽은 벌어지고(발산) −y 쪽은 모입니다(수렴).
    R = float(cfg.planet.radius_m)
    g = sphere_graph(24, R)
    u = g.unit()
    plate = (u[:, 2] < 0).astype(np.int32)
    w = 0.05 / R  # 5 cm/yr
    omega = np.array([[w, 0.0, 0.0], [-w, 0.0, 0.0]])
    cls = classify_boundaries(g, plate, omega, float(cfg.plates.boundary_kappa))
    bt = cls["boundary_type"]
    on = bt > 0
    # 경계 칸은 적도에 붙은 두 줄뿐
    assert np.abs(u[on, 2]).max() < 2.0 * g.spacing / R
    assert (bt[~on] == 0).all()
    assert (bt[on & (u[:, 1] > 0.5)] == DIVERGENT).all()
    assert (bt[on & (u[:, 1] < -0.5)] == CONVERGENT).all()
    # 상대 회전축(±x̂) 근처로 갈수록 수렴·발산 속도가 줄어듭니다 (|γ| ∝ |y|).
    g_abs = np.abs(cls["gamma"][on])
    assert np.corrcoef(g_abs, np.abs(u[on, 1]))[0, 1] > 0.95
    # p = ±ŷ 에서 상대 속도는 2wR = 0.1 m/yr. 대각 이웃 평균이라 0.7~1 배
    g_conv = cls["gamma"][on & (u[:, 1] < -0.9)]
    assert (g_conv > 0.07).all() and (g_conv <= 0.1 + 1e-12).all()
    assert (cls["gamma"][on & (u[:, 1] > 0.9)] < -0.07).all()
    assert (cls["other"][on] == 1 - plate[on]).all()


def test_flip_makes_divergent_boundary_on_twisting_plates(cfg):
    # 두 반구가 z 축 둘레로만 비틀면 경계가 모두 변환 → 뒤집기(−ω) 로도 안 됨 → 멀어지는 회전.
    R = float(cfg.planet.radius_m)
    g = sphere_graph(16, R)
    plate = (g.unit()[:, 2] < 0).astype(np.int32)
    w = 0.05 / R
    omega = np.array([[0.0, 0.0, w], [0.0, 0.0, 0.0]])
    cont = np.zeros(g.n_cells, dtype=bool)
    before = classify_boundaries(g, plate, omega, 0.3)["boundary_type"]
    assert (before[before > 0] == TRANSFORM).all()
    om, flips, cls, remaining = ensure_divergent_boundaries(g, plate, omega, cont, 0.3, w)
    assert remaining == []
    assert [d["method"] for d in flips] == ["negate", "away_from_neighbor"]
    for k in (0, 1):
        assert ((cls["boundary_type"] == DIVERGENT) & (plate == k)).any()
    assert np.array_equal(omega[1], [0.0, 0.0, 0.0])  # 입력은 바꾸지 않음


def test_mostly_oceanic_plates_have_divergent_boundary(cfg):
    # 설계도 5장 17번: 해양판마다 벌어지는 경계를 하나 이상. 기본 설정과 뒤집기가 일어나는 설정.
    cases = [
        cfg,
        cfg.with_overrides(
            {"plates.count": 4, "planet.seed": 27, "plates.continental_area_fraction": 0.2}
        ),
    ]
    g = sphere_graph(24, cfg.planet.radius_m)
    n_flips = 0
    for c in cases:
        cont = continent_mask(g, c)
        f, info = generate_plates(g, cont, c)
        n_flips += len(info["flips"])
        assert info["plates_without_divergent"] == []
        for k in np.flatnonzero(info["ocean_fraction"] > 0.5):
            assert ((f["boundary_type"] == DIVERGENT) & (f["plate_id"] == k)).any()
    assert n_flips >= 1


def test_plate_distances_and_polarity(planet24):
    g, f, _ = planet24
    side = f["subduction_side"]
    kind = f["convergence_kind"]
    assert set(np.unique(side)) <= {-1, 0, 1}
    assert set(np.unique(kind)) <= {0, 1, 2, 3}
    # 섭입(kind 1·2)은 위판(+1)·섭입판(−1), 충돌·없음은 0
    sub = (kind == 1) | (kind == 2)
    assert (side[sub] != 0).all() and (side[~sub] == 0).all()
    for name in ("dist_convergent_m", "dist_divergent_m"):
        d = f[name]
        assert (d[np.isfinite(d)] >= 0).all()
        # 경계 칸의 거리 = 경계선까지 반 칸 (칸 크기보다 작음)
    assert f["dist_convergent_m"][f["boundary_type"] == CONVERGENT].max() < g.spacing
    assert (f["convergence_m_per_yr"][kind > 0] > 0).all()
    assert (f["spreading_m_per_yr"] >= 0).all()


# ---------------------------------------------------------------- 지각
def test_continental_area_fraction(cfg):
    g = sphere_graph(32, cfg.planet.radius_m)
    for frac in (0.4, 0.25):
        c = cfg.with_overrides({"plates.continental_area_fraction": frac})
        mask = continent_mask(g, c)
        got = g.area[mask].sum() / g.area.sum()
        assert abs(got - frac) < 0.02  # 과제 기준: 목표와 2% 안


def test_parsons_sclater_ridge_depth():
    # 해령 꼭대기 −2.5 km 는 넣은 상수 그대로라 회귀 테스트로만 씁니다 (설계도 4장 사슬 1).
    d = ocean_depth_m(np.array([0.0, 25.0, 100.0]), 2500.0)
    assert d[0] == 2500.0
    assert d[1] == pytest.approx(2500.0 + 350.0 * 5.0, abs=1e-9)
    assert d[2] == pytest.approx(6400.0 - 3200.0 * math.exp(-100.0 / 62.8), abs=1e-9)


def test_airy_continuous_at_sea_level(cfg):
    # 물이 채워지는 z < 0 쪽 기울기는 (ρm−ρc)/(ρm−ρw) 이고, z = 0 에서 끊기지 않습니다.
    c = cfg.crust
    a_land = (c.rho_mantle - c.rho_crust) / c.rho_mantle
    h0 = c.continental_thickness_m - c.continental_platform_m / a_land  # z_air = 0 인 두께
    z = airy_elevation_m(np.array([h0 - 1.0, h0, h0 + 1.0, c.continental_thickness_m]), cfg)
    assert abs(z[0]) < 1.0 and abs(z[1]) < 1e-9 and abs(z[2]) < 1.0
    assert z[3] == pytest.approx(c.continental_platform_m)
    slope_sea = (c.rho_mantle - c.rho_crust) / (c.rho_mantle - c.rho_water)
    z2 = airy_elevation_m(np.array([h0 - 2000.0, h0 - 1000.0]), cfg)
    assert z2[1] - z2[0] == pytest.approx(1000.0 * slope_sea, rel=1e-12)


def test_crust_fields(cfg, planet24):
    g, f, info = planet24
    cont = f["crust_type"] == 1
    age = f["ocean_age_myr"]
    cap = float(cfg.ocean.age_cap_myr)
    assert np.isnan(age[cont]).all() and np.isfinite(age[~cont]).all()
    assert (age[~cont] >= 0).all() and (age[~cont] <= cap).all()
    th = f["crust_thickness_m"]
    assert (th[~cont] == cfg.crust.oceanic_thickness_m).all()
    lo = MARGIN_EDGE_FRACTION * cfg.crust.continental_thickness_m
    hi = cfg.crust.continental_thickness_m + cfg.crust.collision_thickening_m
    assert (th[cont] >= lo - 1e-6).all() and (th[cont] <= hi + 1e-6).all()
    # 해구는 따로 더한 변위
    assert np.array_equal(f["z_platform_m"], info["z_platform_base_m"] + info["trench_m"])
    assert (info["trench_m"] <= 0).all()
    on = info["trench_m"] < 0
    assert (f["subduction_side"][on] == -1).all()


def test_trench_offset_profile(cfg):
    d = np.array([0.0, 40_000.0, 1e9, 0.0, 0.0])
    side = np.array([-1, -1, -1, 1, -1])
    kind = np.array([1, 2, 1, 1, 3])
    t = trench_offset_m(d, side, kind, cfg)
    D = cfg.ocean.trench_depth_m
    assert t[0] == -D and t[1] == pytest.approx(-D * math.exp(-1.0)) and t[2] == 0.0
    assert t[3] == 0.0 and t[4] == 0.0


def test_ocean_ages_decrease_with_age(cfg):
    # 설계도 5장 17번: 상한에 면적이 몰리지 않고, 늙은 바닥일수록 면적이 줄어듭니다.
    g = sphere_graph(48, cfg.planet.radius_m)
    f, _ = build_materials(g, cfg)
    age = f["ocean_age_myr"]
    ok = np.isfinite(age)
    h, _ = np.histogram(
        age[ok], bins=10, range=(0.0, cfg.ocean.age_cap_myr + 1e-9), weights=g.area[ok]
    )
    h = h / h.sum()
    assert h[-1] < 0.05
    assert h[5:].sum() < h[:5].sum()
    assert np.all(np.diff(h[3:]) < 0.01)


# ---------------------------------------------------------------- 해수면
def test_sea_level_volume_conservation(cfg, planet24):
    ids = np.arange(5000, dtype=np.int64)
    z = -6000.0 + 9000.0 * hash_uniform_array(ids, 1, 7)
    area = 1e9 * (0.5 + hash_uniform_array(ids, 1, 8))
    W = 0.4 * float(np.sum(area * np.maximum(-z, 0.0)))
    h = sea_level(z, area, W)
    assert abs(ocean_volume(z, area, h) - W) <= 1e-9 * W  # pipeline 4.3: 상대 오차 1e-9
    # 생성한 행성: 설정의 물 부피를 그대로 담고, 바다 비율은 결과로 나옴
    g, f, info = planet24
    W = float(cfg.ocean.water_volume_m3)
    V = ocean_volume(f["z_platform_m"], g.area, info["sea_level_m"])
    assert abs(V - W) <= 1e-9 * W
    assert 0.4 < info["ocean_fraction"] < 0.9
    assert np.allclose(info["bathymetry_m"], f["z_platform_m"] - info["sea_level_m"])
    assert (info["bathymetry_m"][f["is_ocean"]] < 0).all()


def test_ocean_mask_keeps_largest_connected_basin():
    g = flat_graph(10, 30, 1000.0)
    z = np.full(g.n_cells, 100.0)
    img = z.reshape(10, 30)
    img[:, :12] = -100.0  # 큰 분지
    img[:, 20:25] = -100.0  # 작은 분지 (육지로 남아야 함)
    z = img.ravel()
    mask = ocean_mask(g, z, 0.0)
    m = mask.reshape(10, 30)
    assert m[:, :12].all() and not m[:, 12:].any()


def test_sea_level_validation():
    with pytest.raises(ValueError):
        sea_level(np.zeros(3), np.ones(3), 0.0)
    with pytest.raises(ValueError):
        sea_level(np.array([0.0, np.nan]), np.ones(2), 1.0)
    with pytest.raises(ValueError):
        sea_level(np.zeros(3), np.ones(2), 1.0)


# ---------------------------------------------------------------- 융기
def test_arc_distance_default(cfg):
    # 설계도 5장 16번: 두 구간 섭입판, 기본값으로 약 200 km
    assert 190_000.0 <= arc_distance_m(cfg) <= 210_000.0


def test_uplift_rules(cfg, planet24):
    g, f, _ = planet24
    U = f["uplift_m_per_yr"]
    u0 = cfg.uplift.craton_erosion_m_per_myr * 1e-6
    land_cont = (f["crust_type"] == 1) & ~f["is_ocean"]
    no_rift = f["dist_divergent_m"] >= 50_000.0
    assert (U[land_cont & no_rift] >= u0 - 1e-15).all()
    assert (U[f["is_ocean"]] == 0.0).all()
    exh = f["exhumation_m"]
    cap = cfg.uplift.exhumation_cap_m
    assert (exh >= 0).all() and (exh <= cap).all()
    expect = np.clip(np.maximum(U, 0) * cfg.uplift.orogen_duration_myr * 1e6, 0, cap)
    assert np.allclose(exh, expect)
    arc = np.isfinite(f["dist_arc_m"])
    upper = (f["subduction_side"] == 1) & np.isin(f["convergence_kind"], [1, 2])
    assert np.array_equal(arc, upper)
    assert np.allclose(f["dist_arc_m"][arc], arc_distance_m(cfg))


def test_uplift_arc_profile_on_flat_section(cfg):
    # 평면 단면: 해구에서 거리 δ 만 바뀌는 위판. 최대 융기는 δ ≈ d_arc, 크기는 계수·γ·(1 ± 0.25·ξ).
    nx = 400
    g = flat_graph(1, nx, 2000.0)
    d = g.pos[:, 0]
    gamma = 0.05
    fields = {
        "crust_type": np.ones(nx, dtype=np.uint8),
        "is_ocean": np.zeros(nx, dtype=bool),
        "convergence_kind": np.ones(nx, dtype=np.uint8),
        "subduction_side": np.ones(nx, dtype=np.int8),
        "convergence_m_per_yr": np.full(nx, gamma),
        "dist_convergent_m": d,
        "dist_divergent_m": np.full(nx, np.inf),
    }
    U = generate_uplift(g, fields, cfg)["uplift_m_per_yr"]
    peak = cfg.uplift.orogen_factor * gamma
    assert abs(d[np.argmax(U)] - arc_distance_m(cfg)) < 0.5 * cfg.uplift.orogen_width_m
    assert 0.75 * peak < U.max() < 1.3 * peak
    assert U.min() >= cfg.uplift.craton_erosion_m_per_myr * 1e-6 - 1e-15


# ---------------------------------------------------------------- 기후
def test_climate_sphere(cfg):
    g = sphere_graph(48, cfg.planet.radius_m)
    cl = generate_climate(g, cfg)
    P = cl["precip_m_per_yr"]
    c = cfg.climate
    assert (P > 0).all()
    R, Re = cl["runoff_m_per_yr"], cl["runoff_eff_m_per_yr"]
    assert (R >= 0).all() and (R <= P + 1e-12).all()
    assert (Re >= R).all() and (Re >= c.runoff_floor_fraction * P - 1e-15).all()
    assert (Re >= c.runoff_floor_m_per_yr).all()
    # 건조한 아열대: 띠 평균 P(30°) < P(0°), P(50°)
    lat = np.degrees(np.abs(latitude_rad(g, cfg)))

    def band(lo, hi):
        m = (lat >= lo) & (lat < hi)
        return np.average(P[m], weights=g.area[m])

    p_eq, p_sub, p_mid = band(0, 4), band(27, 33), band(47, 53)
    assert p_sub < p_eq and p_sub < p_mid
    # 기온은 극으로 갈수록 내려감 (z 없음 → 위도만)
    T = cl["temperature_c"]
    assert T[lat > 80].max() < T[lat < 10].min()


def test_climate_flat_altitude_and_noise_free_bands(cfg):
    g = flat_graph(4, 5, 1000.0)
    quiet = cfg.with_overrides({"climate.p_noise": 0.0})
    with pytest.raises(ValueError):
        generate_climate(g, quiet)
    p = {lat: generate_climate(g, quiet, lat_deg=lat)["precip_m_per_yr"][0] for lat in (0, 30, 50)}
    assert p[30] < p[0] and p[30] < p[50]
    z = np.linspace(0.0, 3000.0, g.n_cells)
    T = generate_climate(g, quiet, z=z, lat_deg=45.0)["temperature_c"]
    assert np.all(np.diff(T) < 0)
    assert T[0] - T[-1] == pytest.approx(3000.0 * cfg.climate.lapse_rate_c_per_m)
    # 해수면 아래(z < 0)는 기온을 올리지 않음
    T2 = generate_climate(g, quiet, z=-z, lat_deg=45.0)["temperature_c"]
    assert np.allclose(T2, T2[0])


def test_budyko_limits():
    P = np.array([1.0, 1.0, 1.0])
    PET = np.array([1e-3, 1.0, 1e3])
    R = budyko_runoff(P, PET)
    assert R[0] == pytest.approx(1.0, abs=2e-3)  # 증발 수요가 없으면 거의 모두 흐름
    assert R[2] == pytest.approx(0.0, abs=2e-3)  # 아주 건조하면 거의 안 흐름
    assert 0.0 < R[1] < 1.0


# ---------------------------------------------------------------- 묶음과 옮기기
def test_build_materials_fields_and_determinism(cfg):
    g = sphere_graph(16, cfg.planet.radius_m)
    f1, i1 = build_materials(g, cfg)
    f2, _ = build_materials(g, cfg)
    for name, arr in f1.items():
        assert name in FIELDS
        assert arr.shape == (g.n_cells,)
        np.testing.assert_array_equal(arr, f2[name])
    for name in CATEGORICAL_FIELDS + ("is_ocean",):
        assert f1[name].dtype == np.dtype(FIELDS[name].dtype)
    assert 0.38 < i1["continental_fraction"] < 0.42


def test_transfer_coarse_to_fine(cfg):
    R = float(cfg.planet.radius_m)
    gc = sphere_graph(16, R)
    fc, ic = build_materials(gc, cfg)
    gf = sphere_graph(32, R, jitter=0.4, seed=1)
    ff, info = transfer_materials(gc, fc, ic, gf, cfg)
    assert set(ff) == set(fc)
    M = int(cfg.plates.count)
    # 범주 값은 거친 격자에 있던 값만, dtype 그대로
    valid = {
        "plate_id": set(range(M)),
        "boundary_type": {0, 1, 2, 3},
        "convergence_kind": {0, 1, 2, 3},
        "subduction_side": {-1, 0, 1},
        "crust_type": {0, 1},
    }
    idx = cell_of(gf.unit(), 16)
    for name in CATEGORICAL_FIELDS:
        assert ff[name].dtype == fc[name].dtype
        assert set(np.unique(ff[name])) <= valid[name]
        if name != "subduction_side":
            assert np.array_equal(ff[name], fc[name][idx])
    # 매끄러운 값은 면적 평균이 거의 같음
    for name in ("spreading_m_per_yr", "crust_thickness_m", "precip_m_per_yr"):
        mc = np.average(fc[name], weights=gc.area)
        mf = np.average(ff[name], weights=gf.area)
        assert abs(mf - mc) <= 0.05 * abs(mc), name
    # 기온은 고운 격자 고도로 다시 계산하므로 산지 모양만큼 다를 수 있음
    mc = np.average(fc["temperature_c"], weights=gc.area)
    assert abs(np.average(ff["temperature_c"], weights=gf.area) - mc) < 2.0
    # 거친 칸(≈ 625 km)이 대륙 가장자리 경사(150 km)보다 훨씬 커서 보간한 사면이 넓어집니다.
    # n = 64 → 128 에서는 차이가 0.001 이하입니다(보고서).
    assert abs(info["ocean_fraction"] - ic["ocean_fraction"]) < 0.1
    # 고운 격자에서 해수면을 물 부피로 다시 정함
    W = float(cfg.ocean.water_volume_m3)
    V = ocean_volume(ff["z_platform_m"], gf.area, info["sea_level_m"])
    assert abs(V - W) <= 1e-9 * W
    age = ff["ocean_age_myr"]
    assert np.array_equal(np.isnan(age), ff["crust_type"] == 1)
    # 좁은 해구를 고운 격자에서 다시 계산: 거친 격자(칸 ≈ 625 km)에선 안 보이던 해구가 생김
    D = cfg.ocean.trench_depth_m
    assert ic["trench_m"].min() > -0.1 * D
    assert info["trench_m"].min() < -0.5 * D
    assert np.array_equal(ff["z_platform_m"], info["z_platform_base_m"] + info["trench_m"])
    assert (ff["uplift_m_per_yr"][ff["is_ocean"]] == 0).all()


def test_input_validation(cfg):
    g = sphere_graph(8, cfg.planet.radius_m)
    flat = flat_graph(4, 4, 100.0)
    with pytest.raises(ValueError):
        generate_plates(g, np.zeros(g.n_cells, dtype=np.uint8), cfg)
    with pytest.raises(ValueError):
        generate_plates(flat, np.zeros(flat.n_cells, dtype=bool), cfg)
    with pytest.raises(ValueError):
        continent_mask(g, cfg.with_overrides({"plates.continental_area_fraction": 1.5}))
    with pytest.raises(ValueError):
        generate_crust(g, {}, np.zeros(g.n_cells, dtype=bool), cfg)
    with pytest.raises(ValueError):
        generate_uplift(g, {}, cfg)
    with pytest.raises(ValueError):
        generate_climate(g, cfg, lat_deg=10.0)
    with pytest.raises(ValueError):
        transfer_materials(flat, {}, None, g, cfg)
