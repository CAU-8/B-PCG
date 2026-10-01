"""geology: 암석 표, 변성, 템플릿, 습곡, 층 기둥, rock_at (docs/pipeline.md 5장)."""

import math

import numpy as np
import pytest

from bpcg.core.config import load_config
from bpcg.geology import model as gm
from bpcg.geology import rocks as rk


@pytest.fixture(scope="module")
def cfg():
    return load_config("earth", "tiny")


def _depth_of(cfg, t_c):
    """온도 t_c [°C] 가 되는 묻힌 깊이 [m]."""
    g = cfg.geology
    return (t_c - g.surface_temperature_c) / g.geotherm_c_per_m


# ---------------------------------------------------------------- 암석 표
def test_rock_table_matches_spec():
    # pipeline.md 5.1 표 그대로
    assert rk.N_ROCKS == 12 and len(rk.ROCK_NAMES) == rk.N_ROCKS
    assert (rk.ALLUVIUM, rk.LIMESTONE, rk.MARBLE, rk.SOIL) == (0, 3, 9, 11)
    np.testing.assert_array_equal(
        rk.K_MULT, [3.0, 0.5, 2.0, 0.6, 0.3, 0.5, 0.8, 0.6, 0.35, 0.5, 0.25, 4.0]
    )
    np.testing.assert_array_equal(
        rk.S_CRIT, [0.4, 0.9, 0.45, 1.0, 1.0, 0.9, 0.8, 0.8, 1.0, 1.0, 1.1, 0.6]
    )
    assert rk.LOG10_PERM[rk.SHALE] == -16.5 and rk.LOG10_PERM[rk.ALLUVIUM] == -10.9
    assert set(np.flatnonzero(rk.SOLUBLE)) == {rk.LIMESTONE, rk.MARBLE}
    for a in (rk.K_MULT, rk.S_CRIT, rk.LOG10_PERM, rk.SOLUBLE, rk.COLOR_RGB):
        assert a.shape[0] == rk.N_ROCKS and not a.flags.writeable


def test_rock_colors_are_distinct():
    c = rk.COLOR_RGB.astype(float)
    assert rk.COLOR_RGB.dtype == np.uint8 and c.shape == (rk.N_ROCKS, 3)
    d = np.linalg.norm(c[:, None] - c[None], axis=2)
    d[np.diag_indices(rk.N_ROCKS)] = np.inf
    # 단면에서 눈으로 가를 수 있게 RGB 거리 40 이상
    assert d.min() > 40


# ---------------------------------------------------------------- 변성
@pytest.mark.parametrize(
    ("rock", "t", "expected"),
    [
        (rk.SHALE, 249.9, rk.SHALE),
        (rk.SHALE, 250.0, rk.SLATE),
        (rk.SHALE, 399.9, rk.SLATE),
        (rk.SHALE, 400.0, rk.SCHIST),
        (rk.SHALE, 599.9, rk.SCHIST),
        (rk.SHALE, 600.0, rk.GNEISS),
        (rk.SLATE, 450.0, rk.SCHIST),
        (rk.SCHIST, 700.0, rk.GNEISS),
        (rk.SCHIST, 100.0, rk.SCHIST),  # 후퇴 변성 없음
        (rk.LIMESTONE, 349.9, rk.LIMESTONE),
        (rk.LIMESTONE, 350.0, rk.MARBLE),
        (rk.SANDSTONE, 349.9, rk.SANDSTONE),
        (rk.SANDSTONE, 350.0, rk.QUARTZITE),
        (rk.GRANITE, 649.9, rk.GRANITE),
        (rk.GRANITE, 650.0, rk.GNEISS),
        (rk.VOLCANIC, 900.0, rk.VOLCANIC),
        (rk.ALLUVIUM, 900.0, rk.ALLUVIUM),
        (rk.SOIL, 900.0, rk.SOIL),
        (rk.MARBLE, 900.0, rk.MARBLE),
    ],
)
def test_metamorphose_thresholds(rock, t, expected):
    out = rk.metamorphose(np.array([rock]), np.array([t]))
    assert out.dtype == np.uint8 and out[0] == expected


def test_metamorphose_vectorized_and_nan():
    rock = np.array([[rk.SHALE], [rk.LIMESTONE]])
    t = np.array([100.0, 300.0, 500.0, 700.0, np.nan])
    out = rk.metamorphose(rock, t)  # 브로드캐스트 (2, 5)
    assert out.shape == (2, 5)
    np.testing.assert_array_equal(out[0], [rk.SHALE, rk.SLATE, rk.SCHIST, rk.GNEISS, rk.SHALE])
    np.testing.assert_array_equal(
        out[1], [rk.LIMESTONE, rk.LIMESTONE, rk.MARBLE, rk.MARBLE, rk.LIMESTONE]
    )
    with pytest.raises(ValueError):
        rk.metamorphose(np.array([rk.N_ROCKS]), np.array([0.0]))


def test_peak_temperature(cfg):
    # 기본 지온 경사 30 °C/km, 지표 10 °C
    assert rk.peak_temperature_c(1000.0, cfg) == pytest.approx(40.0)


# ---------------------------------------------------------------- 층 기둥
@pytest.mark.parametrize("tid", range(gm.N_TEMPLATES))
def test_layer_order_and_thickness(cfg, tid):
    tpl = gm.TEMPLATES[tid]
    k = len(tpl.layers)
    exh = np.array([0.0, 1234.5])
    bottom, rock = gm.build_columns(np.full(2, tid), exh, None, cfg)
    assert bottom.shape == (2, gm.N_LAYERS) and rock.shape == (2, gm.N_LAYERS + 1)
    assert bottom.dtype == np.float64 and rock.dtype == np.uint8
    # 위에서 아래로 템플릿 순서 (기본 지온 경사에서는 퇴적층이 변성되지 않음)
    np.testing.assert_array_equal(rock[:, :k], np.tile([r for r, _ in tpl.layers], (2, 1)))
    # 층 두께 = 템플릿 두께, 합 = 템플릿 전체 두께
    top = exh[:, None]
    thick = -np.diff(np.concatenate([top, bottom], axis=1), axis=1)
    np.testing.assert_allclose(thick[:, :k], np.tile([t for _, t in tpl.layers], (2, 1)))
    np.testing.assert_allclose(exh - bottom[:, k - 1], sum(t for _, t in tpl.layers))
    # 빈 층은 두께 0, 바닥은 아래로 작거나 같음
    assert (thick >= 0).all()
    np.testing.assert_array_equal(thick[:, k : gm.N_TEMPLATE_LAYERS], 0.0)
    # 기반암: 위쪽 화강암, 650 °C 깊이 아래 편마암
    assert (rock[:, -2] == rk.GRANITE).all() and (rock[:, -1] == rk.GNEISS).all()
    np.testing.assert_allclose(exh - bottom[:, -1], _depth_of(cfg, rk.T_GRANITE_GNEISS_C))


def test_fold_displacement_moves_column(cfg):
    tid = np.array([1, 1])
    b0, _ = gm.build_columns(tid, np.array([500.0, 500.0]), None, cfg)
    b1, _ = gm.build_columns(tid, np.array([500.0, 500.0]), np.array([300.0, -200.0]), cfg)
    np.testing.assert_allclose(b1 - b0, [[300.0] * gm.N_LAYERS, [-200.0] * gm.N_LAYERS])


def test_metamorphism_per_layer_mid_depth():
    # 뜨거운 가상 행성: 지표 300 °C, 100 °C/km. 층 가운데 깊이로 층마다 하나씩 정함.
    hot = load_config(
        "earth",
        "tiny",
        overrides={"geology.surface_temperature_c": 300.0, "geology.geotherm_c_per_m": 0.1},
    )
    _, rock = gm.build_columns(np.array([gm.FOLD_THRUST]), np.zeros(1), None, hot)
    # 셰일 0~300 (가운데 315 °C) → 점판암, 석회암 300~1100 (370) → 대리암,
    # 셰일 1100~1500 (430) → 편암, 사암 1500~2000 (475) → 규암, 석회암 2000~2600 (530) → 대리암,
    # 기반암 꼭대기 2600 (560) → 화강암, 3500 m (650 °C) 아래 → 편마암
    expected = [rk.SLATE, rk.MARBLE, rk.SCHIST, rk.QUARTZITE, rk.MARBLE, rk.GRANITE, rk.GNEISS]
    np.testing.assert_array_equal(rock[0], expected)


def test_basement_isograd_shallower_than_stack():
    # 문턱 깊이가 퇴적층보다 얕으면 기반암은 바로 편마암이고 화강암 층 두께는 0
    hot = load_config(
        "earth",
        "tiny",
        overrides={"geology.surface_temperature_c": 600.0, "geology.geotherm_c_per_m": 0.1},
    )
    bottom, rock = gm.build_columns(np.array([gm.ARC]), np.zeros(1), None, hot)
    assert bottom[0, -1] == bottom[0, -2] == -800.0
    assert (rock[0, 1:] == rk.GNEISS).all()
    assert gm.rock_at(bottom, rock, np.array([-800.5]))[0] == rk.GNEISS


def test_build_columns_validation(cfg):
    with pytest.raises(ValueError):
        gm.build_columns(np.array([3]), np.zeros(1), None, cfg)
    with pytest.raises(ValueError):
        gm.build_columns(np.array([0, 1]), np.zeros(3), None, cfg)
    with pytest.raises(ValueError):
        gm.build_columns(np.array([0]), np.array([np.nan]), None, cfg)
    with pytest.raises(ValueError):
        gm.build_columns(np.array([0.5]), np.zeros(1), None, cfg)


# ---------------------------------------------------------------- 드러나는 암석
@pytest.mark.parametrize("tid", range(gm.N_TEMPLATES))
def test_zero_exhumation_exposes_top_layer(cfg, tid):
    bottom, rock = gm.build_columns(np.array([tid]), np.zeros(1), None, cfg)
    top_rock = gm.TEMPLATES[tid].layers[0][0]
    for z in (-1e-6, 0.0, 250.0):  # 지표 바로 아래, 꼭대기, 꼭대기보다 높은 곳
        assert gm.rock_at(bottom, rock, np.array([z]))[0] == top_rock


def test_large_exhumation_exposes_basement_and_metamorphics(cfg):
    tid = np.array([0, 1, 2, 1])
    exh = np.array([10_000.0, 10_000.0, 10_000.0, 30_000.0])
    bottom, rock = gm.build_columns(tid, exh, None, cfg)
    z_surface = np.full(4, 1_000.0)
    s = gm.rock_at(bottom, rock, z_surface)
    # 깊이 9 km (280 °C): 화강암 기반암. 깊이 29 km (880 °C): 편마암
    np.testing.assert_array_equal(s, [rk.GRANITE, rk.GRANITE, rk.GRANITE, rk.GNEISS])


def test_rock_at_boundaries(cfg):
    # 경계와 같은 고도는 아래층 (pipeline.md 5.2: 'z 보다 낮은 바닥을 가진 첫 층')
    bottom, rock = gm.build_columns(np.array([gm.FOLD_THRUST]), np.array([2000.0]), None, cfg)
    # 바닥: 1700, 900, 500, 0, -600, 2000 - 21333.3
    expected_lower = [rk.LIMESTONE, rk.SHALE, rk.SANDSTONE, rk.LIMESTONE, rk.GRANITE, rk.GNEISS]
    for i in range(gm.N_LAYERS):
        b = bottom[0, i]
        assert gm.rock_at(bottom, rock, np.array([b]))[0] == expected_lower[i]
        above = np.nextafter(b, np.inf)
        assert gm.rock_at(bottom, rock, np.array([above]))[0] == rock[0, i]
    # z = 0 은 사암 바닥 → 아래 석회암
    assert gm.rock_at(bottom, rock, np.array([0.0]))[0] == rk.LIMESTONE
    # 모든 바닥보다 낮으면 기반암(마지막 열)
    assert gm.rock_at(bottom, rock, np.array([-1e6]))[0] == rk.GNEISS


def test_rock_at_shapes_and_points(cfg):
    rng = np.random.default_rng(1)
    n = 50
    tid = rng.integers(0, gm.N_TEMPLATES, n)
    exh = rng.uniform(0, 30_000, n)
    bottom, rock = gm.build_columns(tid, exh, None, cfg)
    z = rng.uniform(-30_000, 5_000, (n, 7))
    r2 = gm.rock_at(bottom, rock, z)
    assert r2.shape == (n, 7) and r2.dtype == np.uint8
    for m in range(7):
        np.testing.assert_array_equal(r2[:, m], gm.rock_at(bottom, rock, z[:, m]))
    cells = np.repeat(np.arange(n), 7)
    rp = gm.rock_at_points(bottom, rock, cells, z.ravel())
    np.testing.assert_array_equal(rp, r2.ravel())
    with pytest.raises(ValueError):
        gm.rock_at(bottom, rock, np.zeros(n + 1))
    with pytest.raises(ValueError):
        gm.rock_at(bottom, rock[:, :-1], np.zeros(n))


def test_layer_columns_consistency(cfg):
    # 솔버가 쓰는 K·S_crit 의 암석 = rock_at 이 보여 주는 암석
    # (설계도 7장 "경사를 만든 암석 = 보이는 암석")
    rng = np.random.default_rng(7)
    n = 400
    tid = rng.integers(0, gm.N_TEMPLATES, n)
    exh = rng.uniform(0, 30_000, n)
    disp = np.where(tid == 1, rng.uniform(-1500, 1500, n), 0.0)
    cols = gm.LayerColumns.from_columns(*gm.build_columns(tid, exh, disp, cfg))
    assert cols.n_cells == n and cols.n_layers == gm.N_LAYERS
    # 경계 고도 그대로 + 무작위 고도
    z = np.concatenate([cols.bottom, rng.uniform(-30_000, 35_000, (n, 20))], axis=1)
    li = cols.layer_index(z)
    r = gm.rock_at(cols.bottom, cols.rock, z)
    rows = np.arange(n)[:, None]
    np.testing.assert_array_equal(cols.rock[rows, li], r)
    np.testing.assert_array_equal(cols.k_mult[rows, li], rk.K_MULT[r])
    np.testing.assert_array_equal(cols.s_crit[rows, li], rk.S_CRIT[r])
    # 층 번호의 정의: bottom[i] < z ≤ bottom[i-1]
    upper = np.concatenate([np.full((n, 1), np.inf), cols.bottom], axis=1)
    lower = np.concatenate([cols.bottom, np.full((n, 1), -np.inf)], axis=1)
    assert (lower[rows, li] < z).all() and (z <= upper[rows, li]).all()
    # 두께 0 층은 고르지 않음
    thick = np.concatenate([np.full((n, 1), np.inf), -np.diff(cols.bottom, axis=1)], axis=1)
    thick = np.concatenate([thick, np.full((n, 1), np.inf)], axis=1)
    assert (thick[rows, li] > 0).all()
    zs = z[:, 7]
    np.testing.assert_array_equal(gm.surface_rock(cols, zs), r[:, 7])


# ---------------------------------------------------------------- 템플릿 배정
def test_assign_template(cfg):
    g = cfg.geology
    d_arc = 200_000.0
    arc_half = 0.4 * g.arc_belt_width_m  # 100 km
    side = np.array([1, 1, 1, 1, 0, 0, -1, 1, 0], dtype=np.int8)
    kind = np.array([1, 1, 1, 2, 3, 3, 1, 1, 0], dtype=np.uint8)
    dist = np.array(
        [
            d_arc,  # 화산호 → 2
            d_arc + arc_half + 10_000,  # 위판 습곡대 → 1
            d_arc + 70_000,  # 화산호 띠와 습곡대가 겹침 → 2
            g.fold_belt_width_m + 1_000,  # 습곡대 바깥 → 0
            100_000,  # 충돌 근처 → 1
            g.fold_belt_width_m + 1_000,  # 충돌에서 멂 → 0
            d_arc,  # 섭입판 → 0
            d_arc - arc_half - 1_000,  # 위판, 해구 쪽 → 0
            np.inf,  # 수렴 경계 없음 → 0
        ]
    )
    arc = np.where(side == 1, d_arc, np.nan)
    fields = {
        "subduction_side": side,
        "convergence_kind": kind,
        "dist_convergent_m": dist.astype(np.float32),
        "dist_arc_m": arc.astype(np.float32),
    }
    t = gm.assign_template(fields, cfg)
    assert t.dtype == np.uint8
    np.testing.assert_array_equal(t, [2, 1, 2, 0, 1, 0, 0, 0, 0])
    with pytest.raises(ValueError):
        gm.assign_template({k: v for k, v in fields.items() if k != "dist_arc_m"}, cfg)


# ---------------------------------------------------------------- 습곡
def test_fold_displacement_formula(cfg):
    g = cfg.geology
    n = 64
    dist = np.linspace(0, 100_000, n)
    U = np.linspace(0, 2e-3, n)
    tid = np.where(np.arange(n) % 2 == 0, 1, 0)
    phase, disp = gm.fold_displacement({"dist_convergent_m": dist, "uplift_m_per_yr": U}, tid, cfg)
    np.testing.assert_allclose(phase, 2 * math.pi * dist / g.fold_wavelength_m)
    expected = g.fold_amplitude_m * (U / U.max()) * np.sin(phase)
    np.testing.assert_allclose(disp, np.where(tid == 1, expected, 0.0), atol=1e-9)
    assert np.abs(disp).max() <= g.fold_amplitude_m
    # 히어로처럼 U_max 를 밖에서 주면 그 값으로 나눔
    _, disp2 = gm.fold_displacement(
        {"dist_convergent_m": dist, "uplift_m_per_yr": U}, tid, cfg, u_max=4e-3
    )
    np.testing.assert_allclose(disp2, 0.5 * disp, atol=1e-9)


def test_fold_noise_deterministic_and_bounded(cfg):
    g = cfg.geology
    rng = np.random.default_rng(3)
    n = 2000
    p = rng.normal(size=(n, 3))
    p /= np.linalg.norm(p, axis=1, keepdims=True)
    dist = rng.uniform(0, 300_000, n)
    dist[:5] = np.inf
    fields = {"dist_convergent_m": dist, "uplift_m_per_yr": np.full(n, 1e-3)}
    tid = np.ones(n, dtype=np.uint8)
    ph1, d1 = gm.fold_displacement(fields, tid, cfg, unit_points=p)
    ph2, d2 = gm.fold_displacement(fields, tid, cfg, unit_points=p)
    np.testing.assert_array_equal(ph1, ph2)
    np.testing.assert_array_equal(d1, d2)
    base = np.where(np.isfinite(dist), 2 * math.pi * np.where(np.isfinite(dist), dist, 0) / 15e3, 0)
    dev = ph1 - base
    # 0.5·fbm3, fbm3 값은 [-1.2, 1.2] 안 (pipeline.md 3장 검사)
    assert np.abs(dev).max() <= 0.5 * 1.2
    assert np.abs(dev).std() > 1e-3  # 노이즈가 실제로 들어감
    assert np.all(ph1[:5] == 0.0)
    assert np.isfinite(d1).all() and np.abs(d1).max() <= g.fold_amplitude_m
    # 시드가 바뀌면 위상도 바뀜
    cfg2 = cfg.with_overrides({"planet.seed": 1})
    ph3, _ = gm.fold_displacement(fields, tid, cfg2, unit_points=p)
    assert not np.allclose(ph1, ph3)


def test_fallback_noise_range():
    # core/noise 가 없을 때의 대체 노이즈: 결정적이고 [-1, 1] 안
    pts = np.random.default_rng(5).uniform(-500, 500, (5000, 3))
    v = gm._fallback_fbm3(pts, seed=11)
    assert np.abs(v).max() <= 1.0 and v.std() > 0.1
    np.testing.assert_array_equal(v, gm._fallback_fbm3(pts, seed=11))
    assert not np.allclose(v, gm._fallback_fbm3(pts, seed=12))


# ---------------------------------------------------------------- 단계 함수
def test_generate_geology_deterministic(cfg):
    rng = np.random.default_rng(11)
    n = 300
    side = rng.choice(np.array([-1, 0, 1], dtype=np.int8), n)
    kind = np.where(side == 0, rng.choice([0, 3], n), 1).astype(np.uint8)
    fields = {
        "subduction_side": side,
        "convergence_kind": kind,
        "dist_convergent_m": rng.uniform(0, 600_000, n).astype(np.float32),
        "dist_arc_m": np.where(side == 1, 200_000.0, np.nan).astype(np.float32),
        "uplift_m_per_yr": rng.uniform(-1e-4, 3e-3, n).astype(np.float32),
        "exhumation_m": rng.uniform(0, 30_000, n).astype(np.float32),
    }
    p = rng.normal(size=(n, 3))
    p /= np.linalg.norm(p, axis=1, keepdims=True)
    out1, cols1, diag1 = gm.generate_geology(fields, cfg, unit_points=p)
    out2, cols2, _ = gm.generate_geology(fields, cfg, unit_points=p)
    for k in out1:
        np.testing.assert_array_equal(out1[k], out2[k])
    assert set(out1) == {"template_id", "fold_phase", "strata_bottom_m", "strata_rock"}
    assert sum(diag1["template_counts"]) == n
    assert out1["strata_bottom_m"].shape == (n, gm.N_LAYERS)
    np.testing.assert_array_equal(cols1.k_mult, rk.K_MULT[cols1.rock])
    # 바닥은 위층부터 아래로 작거나 같음
    assert (np.diff(out1["strata_bottom_m"], axis=1) <= 0).all()
