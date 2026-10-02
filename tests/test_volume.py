"""3D 샘플 함수 HeroVolume 과 단면 그림 검사 (docs/pipeline.md 10장, 가이드 7장 '확인하기')."""

import numpy as np
import pytest

from bpcg.core.config import load_config
from bpcg.geology import rocks as rk
from bpcg.pipeline import generate_hero
from bpcg.volume.sample import (
    ALLUVIUM_CLIP_BLEND_M,
    MATERIAL_EMPTY,
    RIVER_DENSIFY_M,
    HeroVolume,
    catmull_rom_centripetal,
)
from bpcg.volume.slices import vertical_slice

# 이 파일의 검사는 호수(물 규칙)와 동굴·입구가 있어야 의미가 있으므로, 둘이 반드시 생기는
# 조건으로 고정합니다.
# 평면 히어로의 호수는 선상지 원뿔이 강을 막아서만 생기는데,
# 기본 경사 지수 n = 2 에서는 산지 앞 경사 차이(∝ 융기 차이^(1/n))가 fans.slope_drop_ratio 4 에
# 못 미쳐 선상지가 없으므로 n = 1 로 둡니다. tiny 영역(6.4 km, 41 km²)은 기본 강 문턱 0.3 m³/s
# (상류 약 19 km²)보다 작아 강이 출구 옆뿐이고, 그러면 지하수면이 거의 모든 칸에서 지표로 잘려
# 덮인 동굴이 드물어지므로 강 문턱을 0.02 m³/s 로 낮춥니다.
HERO_OVERRIDES = {"landscape.slope_exponent_n": 1.0, "rivers.min_discharge_m3_per_s": 0.02}


@pytest.fixture(scope="module")
def hero_cfg():
    cfg = load_config("earth", "tiny", overrides=HERO_OVERRIDES)
    return generate_hero(cfg, log=None), cfg


@pytest.fixture(scope="module")
def vol(hero_cfg):
    hero, cfg = hero_cfg
    return HeroVolume(hero, cfg)


@pytest.fixture(scope="module")
def vol_flat(hero_cfg):
    """지표 노이즈를 끈 볼륨 (칸 중심 값을 정확히 비교할 때)."""
    hero, cfg = hero_cfg
    return HeroVolume(hero, cfg, detail_amplitude_m=0.0)


def _random_points(vol, hero, n, seed, below=300.0, above=30.0):
    rng = np.random.default_rng(seed)
    xc, yc = vol.cell_centers()
    z = np.asarray(hero.fields["z_m"])
    c = rng.integers(0, xc.size, n)
    h = 0.5 * vol.dx
    return np.stack(
        [
            xc[c] + rng.uniform(-h, h, n),
            yc[c] + rng.uniform(-h, h, n),
            z[c] + rng.uniform(-below, above, n),
        ],
        axis=1,
    )


def _cave_points(vol, hero, n_per_cell=40, seed=3):
    """동굴 층 높이 근처에 몰아 뽑은 점 (동굴 점을 충분히 얻으려고)."""
    rng = np.random.default_rng(seed)
    xc, yc = vol.cell_centers()
    pts = []
    for k in range(vol.n_levels):
        zk = vol.cave_levels[k].ravel()
        cells = np.flatnonzero(np.isfinite(zk))
        if cells.size == 0:
            continue
        c = np.repeat(cells, n_per_cell)
        h = 0.5 * vol.dx
        pts.append(
            np.stack(
                [
                    xc[c] + rng.uniform(-h, h, c.size),
                    yc[c] + rng.uniform(-h, h, c.size),
                    zk[c] + rng.uniform(-2 * vol.r_pass, 2 * vol.r_pass, c.size),
                ],
                axis=1,
            )
        )
    a, b = vol.cap_a, vol.cap_b
    if a.shape[0]:
        t = rng.uniform(0, 1, (a.shape[0], 20))
        p = a[:, None, :] + t[..., None] * (b - a)[:, None, :]
        p = p.reshape(-1, 3) + rng.uniform(-vol.r_pass, vol.r_pass, (p.shape[0] * p.shape[1], 3))
        pts.append(p)
    return np.vstack(pts)


def test_sample_shapes_and_dtypes(vol, hero_cfg):
    hero, _ = hero_cfg
    pts = _random_points(vol, hero, 5000, 0)
    d, mat, water = vol.sample(pts)
    assert d.shape == mat.shape == water.shape == (5000,)
    assert d.dtype == np.float32 and mat.dtype == np.uint8 and water.dtype == np.bool_
    assert np.isfinite(d).all()
    solid = d <= 0
    assert (mat[solid] < rk.N_ROCKS).all()
    assert (mat[~solid] == MATERIAL_EMPTY).all()
    assert not water[solid].any()  # 물은 빈 곳에만


def test_sample_rejects_bad_points(vol):
    with pytest.raises(ValueError):
        vol.sample(np.zeros((4, 2)))
    with pytest.raises(ValueError):
        vol.sample(np.array([[0.0, 0.0, np.nan]]))
    d, m, w = vol.sample(np.zeros((0, 3)))
    assert d.shape == (0,)


def test_sample_is_deterministic(hero_cfg, vol):
    hero, cfg = hero_cfg
    pts = _random_points(vol, hero, 3000, 1)
    a = vol.sample(pts)
    b = HeroVolume(hero, cfg).sample(pts)
    for x, y in zip(a, b, strict=True):
        assert np.array_equal(x, y)


def test_water_rule_no_air_below_water_outside_caves(vol, hero_cfg):
    # 가이드 7장 물 규칙: 지상 빈 곳은 수면 아래면 물, 동굴은 지하수면 아래면 물
    hero, _ = hero_cfg
    pts = np.vstack(
        [_random_points(vol, hero, 40000, 2, below=50, above=20), _cave_points(vol, hero)]
    )
    ev = vol.evaluate(pts)
    empty = ev["d"] > 0
    above = empty & ~ev["under"]
    cave = empty & ev["under"]
    up = pts[:, 2]
    assert not (above & (up < ev["water_level"]) & ~ev["water"]).any()
    assert not (above & (up >= ev["water_level"]) & ev["water"]).any()
    assert not (cave & (up < ev["z_gw"]) & ~ev["water"]).any()
    assert not (cave & (up >= ev["z_gw"]) & ev["water"]).any()
    assert not (ev["water"] & ~empty).any()
    assert cave.any() and (cave & ev["water"]).any() and (cave & ~ev["water"]).any()


def test_lake_cells_hold_water(vol, hero_cfg):
    hero, _ = hero_cfg
    f = hero.fields
    level = np.asarray(f["water_level_m"])
    z = np.asarray(f["z_m"])
    lake = np.flatnonzero(np.asarray(f["is_lake"]) & (level - z > 1.0))
    assert lake.size > 0
    xc, yc = vol.cell_centers()
    pts = np.stack([xc[lake], yc[lake], 0.5 * (z[lake] + level[lake])], axis=1)
    d, _, water = vol.sample(pts)
    assert (d > 0).all() and water.all()
    # 수면 위는 마른 공기
    pts[:, 2] = level[lake] + 0.5
    d, _, water = vol.sample(pts)
    assert (d > 0).all() and not water.any()


def test_caves_only_in_soluble_rock(vol, hero_cfg):
    # 가이드 7장 '동굴 위치': 모든 동굴 점(땅 속 빈 곳)은 녹는 암석 안
    hero, _ = hero_cfg
    pts = np.vstack([_cave_points(vol, hero), _random_points(vol, hero, 40000, 4)])
    ev = vol.evaluate(pts)
    cave = (ev["d"] > 0) & ev["under"]
    assert cave.sum() > 100
    assert rk.SOLUBLE[ev["rock"][cave]].all()
    # 충적층 안 동굴은 충적층이 ALLUVIUM_CLIP_BLEND_M 보다 얇아 자르는 면을 올린 곳뿐
    in_alluv = cave & (ev["solid_material"] == rk.ALLUVIUM)
    thick = vol.columns(pts[in_alluv, 0], pts[in_alluv, 1])["alluvium"]
    assert (thick < ALLUVIUM_CLIP_BLEND_M).all()


def test_cave_flooded_below_water_table(vol, hero_cfg):
    hero, _ = hero_cfg
    ev = vol.evaluate(_cave_points(vol, hero))
    cave = (ev["d"] > 0) & ev["under"]
    pts_up = _cave_points(vol, hero)[:, 2]
    below = cave & (pts_up < ev["z_gw"])
    assert below.any()
    assert ev["water"][below].all()


def test_continuity_of_sdf(vol, hero_cfg):
    # 1 cm 떨어진 두 점의 |Δd| 는 기울기 상한 × 1 cm 를 넘지 않습니다 (강 중심선 근처 제외:
    # 가장 가까운 강 점이 바뀌는 곳은 연속이 아님).
    hero, _ = hero_cfg
    rng = np.random.default_rng(5)
    a = np.vstack(
        [_random_points(vol, hero, 30000, 6, below=60, above=20), _cave_points(vol, hero)]
    )
    u = rng.normal(size=a.shape)
    u /= np.linalg.norm(u, axis=1, keepdims=True)
    b = a + 0.01 * u
    ea, eb = vol.evaluate(a), vol.evaluate(b)
    z = vol.z
    g = max(np.abs(np.diff(z, axis=0)).max(), np.abs(np.diff(z, axis=1)).max()) / vol.dx
    bound = 0.01 * (np.sqrt(1.0 + 2.0 * g * g) + 1.0) + 1e-6  # 지표 기울기 + 노이즈·동굴 여유
    cols = vol.columns(a[:, 0], a[:, 1])
    far = cols["river_dist"] > cols["river_half_width"] + 2.0
    dd = np.abs(ea["d"] - eb["d"])
    assert dd[far].max() <= bound
    assert np.quantile(dd, 0.999) <= bound


def test_surface_material_matches_hero_maps(vol_flat, hero_cfg):
    # 지표 바로 아래(1 cm) 재질 = 흙 / 충적층 / 지표 암석 (노이즈 0, 강 둘레 밖, 동굴 아닌 칸 중심)
    hero, _ = hero_cfg
    f = hero.fields
    vol = vol_flat
    xc, yc = vol.cell_centers()
    z = np.asarray(f["z_m"])
    pts = np.stack([xc, yc, z - 0.01], axis=1)
    ev = vol.evaluate(pts)
    cols = vol.columns(xc, yc)
    soil = np.asarray(f["soil_thickness_m"])
    alluv = np.asarray(f["alluvium_m"])
    near_river = cols["river_dist"] < cols["river_half_width"] + vol.dx
    sb = hero.columns.bottom
    near_boundary = (np.abs(sb - z[:, None]) < 0.02).any(axis=1)
    ok = ~near_river & ~near_boundary & (ev["d"] <= 0)
    assert ok.mean() > 0.9
    want = np.where(
        soil > 0.01, rk.SOIL, np.where(alluv > 0, rk.ALLUVIUM, np.asarray(f["surface_rock"]))
    )
    assert np.array_equal(ev["material"][ok], want[ok].astype(np.uint8))


def test_surface_height_is_root_of_sdf(vol, hero_cfg):
    hero, _ = hero_cfg
    rng = np.random.default_rng(7)
    x0, x1, y0, y1 = vol.extent
    x = rng.uniform(x0, x1, 3000)
    y = rng.uniform(y0, y1, 3000)
    # 강 위 점과 둑 바로 밖(강 점에서 4 m 안) 점도 섞음. 작은 강은 D < W/2 라 smax 가 지표를 바꾸는
    # 띠가 ℓ < W/2 + k 보다 넓습니다(ℓ < W/2·(1 + k/D) 까지).
    assert vol.river_pts.shape[0] > 0
    k = rng.integers(0, vol.river_pts.shape[0], 500)
    x = np.concatenate([x, vol.river_pts[k, 0]])
    y = np.concatenate([y, vol.river_pts[k, 1]])
    k = rng.integers(0, vol.river_pts.shape[0], 1000)
    r = rng.uniform(0.0, 4.0, k.size)
    th = rng.uniform(0.0, 2.0 * np.pi, k.size)
    x = np.concatenate([x, vol.river_pts[k, 0] + r * np.cos(th)])
    y = np.concatenate([y, vol.river_pts[k, 1] + r * np.sin(th)])
    h = vol.surface_height(x, y)
    d1 = lambda dz: vol.evaluate(np.stack([x, y, h + dz], axis=1))["d1"]  # noqa: E731
    assert np.abs(d1(0.0)).max() < 1e-3
    assert (d1(0.05) > 0).all() and (d1(-0.05) < 0).all()


def test_river_channel_is_carved_and_wet(vol, hero_cfg):
    assert vol.river_pts.shape[0] > 0
    sel = vol.river_depth > 0
    x, y = vol.river_pts[sel, 0], vol.river_pts[sel, 1]
    h = vol.surface_height(x, y)
    # 하도 중심의 바닥은 둑 높이보다 대략 D 아래 (smax 로 k/4 까지 둥글어짐)
    assert np.all(h <= vol.river_zq[sel] - 0.5 * vol.river_depth[sel])
    hw = vol.water_surface(x, y)
    assert np.all(hw > h)
    pts = np.stack([x, y, 0.5 * (h + hw)], axis=1)
    d, _, water = vol.sample(pts)
    assert (d > 0).all() and water.all()


def test_catmull_rom_passes_through_and_is_dense():
    ctrl = np.array([[0.0, 0.0], [25.0, 0.0], [50.0, 25.0], [50.0, 75.0], [0.0, 100.0]])
    pts, span = catmull_rom_centripetal(ctrl, RIVER_DENSIFY_M)
    step = np.linalg.norm(np.diff(pts, axis=0), axis=1)
    assert step.max() <= RIVER_DENSIFY_M * 1.6  # 곡선 길이는 현보다 조금 김
    for i in range(ctrl.shape[0]):
        k = np.flatnonzero(np.isclose(span, i))
        assert k.size == 1 and np.allclose(pts[k[0]], ctrl[i])
    one, s1 = catmull_rom_centripetal(ctrl[:1], 1.0)
    assert one.shape == (1, 2) and s1.shape == (1,)
    with pytest.raises(ValueError):
        catmull_rom_centripetal(np.zeros((3, 3)), 1.0)


def test_vertical_slice_image(vol, hero_cfg, tmp_path):
    hero, _ = hero_cfg
    z = np.asarray(hero.fields["z_m"])
    x0, x1, y0, y1 = vol.extent
    p0, p1 = (x0 + 100.0, 0.0), (x1 - 100.0, 0.0)
    png = tmp_path / "slice.png"
    img = vertical_slice(vol, p0, p1, float(z.min()) - 100, float(z.max()) + 50, 20.0, png)
    assert img.dtype == np.uint8 and img.ndim == 3 and img.shape[2] == 3
    assert png.exists() and png.stat().st_size > 1000
    with pytest.raises(ValueError):
        vertical_slice(vol, p0, p0, 0.0, 1.0, 1.0)
