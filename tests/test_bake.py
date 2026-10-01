"""굽기 검사: 묶음 쓰기·읽기, 면 텍스처, 동굴 메시, 회랑 굽기 (docs/pipeline.md 11장)."""

import json
from types import SimpleNamespace

import numpy as np
import pytest

from bpcg.bake.bundle import (
    jsonable,
    load_hero_state,
    read_bundle,
    save_hero_state,
    write_bundle,
)
from bpcg.bake.corridor import (
    STRATA_AIR,
    STRATA_WATER,
    WATER_NONE_M,
    bake_corridor,
    best_donor,
    choose_corridor,
    main_stem,
)
from bpcg.bake.heightmap import read_heightmap
from bpcg.bake.mesh import EngineFrame, cave_surface
from bpcg.bake.textures import face_textures
from bpcg.core.config import load_config
from bpcg.core.fields import FIELDS
from bpcg.core.graph import flat_graph, sphere_graph
from bpcg.geology import rocks as rk
from bpcg.pipeline import generate_hero
from bpcg.volume.sample import HeroVolume

HEIGHTMAP_STEMS = ("heightmap", "surround25", "water", "water_table", "strata_top")


# 회랑 굽기 검사는 선상지 꼭짓점·호수와 동굴·입구(동굴 메시)가 있어야 의미가 있으므로,
# 둘이 반드시 생기는 조건으로 고정합니다.
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
def baked(hero_cfg, tmp_path_factory):
    hero, cfg = hero_cfg
    out = tmp_path_factory.mktemp("corridor")
    eng = tmp_path_factory.mktemp("engine_baked")
    man = bake_corridor(hero, cfg, out, engine_dir=eng, log=None)
    return man, out, eng


# ---------------------------------------------------------------- 묶음
def _assert_same(a: np.ndarray, b: np.ndarray) -> None:
    assert a.dtype == b.dtype and a.shape == b.shape
    if a.dtype.kind == "f":
        assert np.array_equal(a, b, equal_nan=True)
    else:
        assert np.array_equal(a, b)


def test_bundle_roundtrip_exact_flat(hero_cfg, tmp_path):
    hero, cfg = hero_cfg
    fields = {k: v for k, v in hero.fields.items() if k in FIELDS}
    man = write_bundle(tmp_path, hero.graph, fields, {"note": "검사"}, cfg=cfg, rivers=hero.rivers)
    b = read_bundle(tmp_path)
    for k in ("pos", "nbr", "dist", "area"):
        assert np.array_equal(getattr(b.graph, k), getattr(hero.graph, k))
    assert b.graph.shape == hero.graph.shape and b.graph.origin == hero.graph.origin
    assert b.graph.spacing == hero.graph.spacing and b.graph.kind == "flat"
    assert set(b.fields) == set(fields)
    for k, v in fields.items():
        want = np.asarray(v).astype(FIELDS[k].dtype)
        _assert_same(b.fields[k], want)
        assert str(b.fields[k].dtype) == FIELDS[k].dtype
    assert len(b.rivers) == len(hero.rivers)
    for x, y in zip(b.rivers, hero.rivers, strict=True):
        assert np.array_equal(x, y)
    assert b.meta == {"note": "검사"}
    assert man["config_digest"] == cfg.digest() and man["seed"] == cfg.planet.seed
    assert man["face_basis"] is None
    e = {f["name"]: f for f in man["fields"]}
    assert e["strata_bottom_m"]["shape"] == list(hero.columns.bottom.shape)
    assert e["z_m"]["group"] == "surface" and e["z_m"]["unit"] == "m"
    # 두 번째로 쓰고 읽어도 같음 (FIELDS dtype 그대로 왕복)
    write_bundle(tmp_path / "again", b.graph, b.fields)
    c = read_bundle(tmp_path / "again")
    for k in b.fields:
        _assert_same(c.fields[k], b.fields[k])


def test_bundle_roundtrip_sphere_has_face_basis(tmp_path):
    g = sphere_graph(6, 6_371_000.0, jitter=0.3, seed=1)
    rng = np.random.default_rng(0)
    n = g.n_cells
    fields = {
        "z_m": rng.normal(size=n) * 1000,
        "plate_id": rng.integers(0, 12, n),
        "is_ocean": rng.random(n) < 0.5,
        "ocean_age_myr": np.where(rng.random(n) < 0.5, np.nan, rng.random(n) * 100),
    }
    man = write_bundle(tmp_path, g, fields)
    b = read_bundle(tmp_path)
    assert man["face_basis"] is not None and len(man["face_basis"]["u"]) == 6
    assert b.graph.R == g.R and b.graph.kind == "sphere"
    assert np.array_equal(b.graph.pos, g.pos)
    for k, v in fields.items():
        _assert_same(b.fields[k], np.asarray(v).astype(FIELDS[k].dtype))
    assert b.rivers is None


def test_bundle_rejects_bad_fields(tmp_path):
    g = flat_graph(4, 4, 10.0)
    with pytest.raises(ValueError):
        write_bundle(tmp_path, g, {"no_such_field": np.zeros(16)})
    with pytest.raises(ValueError):
        write_bundle(tmp_path, g, {"z_m": np.zeros(15)})
    with pytest.raises(ValueError):
        write_bundle(tmp_path, g, {"plate_id": np.full(16, 0.5)})
    with pytest.raises(FileNotFoundError):
        read_bundle(tmp_path / "missing")


def test_hero_state_save_load(hero_cfg, tmp_path):
    hero, cfg = hero_cfg
    save_hero_state(tmp_path, hero, cfg)
    h2 = load_hero_state(tmp_path)
    assert h2.site is None
    assert h2.graph.shape == hero.graph.shape
    assert np.allclose(h2.columns.bottom, hero.columns.bottom, rtol=1e-6)
    assert np.array_equal(h2.columns.rock, hero.columns.rock)
    assert len(h2.fan_apexes) == len(hero.fan_apexes)
    assert h2.fields["z_m"].dtype == np.float64
    assert np.allclose(h2.fields["z_m"], hero.fields["z_m"], rtol=1e-6)
    # 다시 읽은 히어로로도 3D 함수가 만들어짐
    HeroVolume(h2, cfg)


def test_jsonable_handles_numpy_and_nan():
    obj = {"a": np.float32(1.5), "b": np.nan, "c": np.arange(3), "d": np.zeros(5000), "e": (1, 2)}
    out = jsonable(obj)
    assert out["a"] == 1.5 and out["b"] is None and out["c"] == [0, 1, 2]
    assert out["d"]["__array__"] == [5000] and out["e"] == [1, 2]
    json.dumps(out, allow_nan=False)


# ---------------------------------------------------------------- 면 텍스처
def test_face_textures(tmp_path):
    g = sphere_graph(8, 6_371_000.0)
    n = g.n_cells
    rng = np.random.default_rng(1)
    z = rng.normal(size=n) * 3000
    fields = {
        "z_mean_m": z,
        "plate_id": rng.integers(0, 12, n),
        "ocean_age_myr": np.where(z < 0, rng.random(n) * 150, np.nan),
        "precip_m_per_yr": rng.random(n) * 2,
    }
    meta = face_textures(SimpleNamespace(graph=g, fields=fields), tmp_path)
    for layer in ("elevation", "plate", "ocean_age", "precip"):
        assert len(meta["layers"][layer]["files"]) == 6
        for f in meta["layers"][layer]["files"]:
            assert (tmp_path / f).stat().st_size > 0
    from matplotlib import image as mpimg

    img = mpimg.imread(tmp_path / "elevation_0.png")
    assert img.shape[:2] == (8, 8)
    assert json.loads((tmp_path / "textures.json").read_text())["n_per_face"] == 8


# ---------------------------------------------------------------- 회랑
def test_engine_frame_roundtrip():
    fr = EngineFrame(cx=100.0, cy=-50.0, y_offset=1200.0)
    p = np.array([[100.0, -50.0, 1200.0], [110.0, -40.0, 1250.0]])
    e = fr.to_engine(p)
    assert np.allclose(e[0], 0.0)
    assert np.allclose(e[1], [10.0, 50.0, -10.0])  # 북쪽(+10) 은 엔진 −Z
    assert np.allclose(fr.to_local(e), p)


def test_main_stem_and_best_donor():
    # 0 ← 1 ← 2, 3 → 1 (작은 지류), 4 → 4 (출구)
    rcv = np.array([0, 0, 1, 1, 4])
    q = np.array([10.0, 8.0, 5.0, 2.0, 1.0])
    assert list(best_donor(rcv, q)) == [1, 2, -1, -1, -1]
    path, i = main_stem(rcv, q, 1)
    assert list(path) == [0, 1, 2] and i == 1


def test_choose_corridor_inside_domain(hero_cfg):
    hero, cfg = hero_cfg
    cor = choose_corridor(hero, cfg)
    x_min, x_max, y_min, y_max = cor["rect"]
    g = hero.graph
    ny, nx = g.shape
    dx = g.spacing
    assert x_min >= g.origin[0] + 0.5 * dx - 1e-6 and x_max <= g.origin[0] + (nx - 0.5) * dx + 1e-6
    assert y_max <= g.origin[1] - 0.5 * dx + 1e-6 and y_min >= g.origin[1] - (ny - 0.5) * dx - 1e-6
    c = cfg.profile.corridor
    along = (x_max - x_min) if cor["axis"] == "east" else (y_max - y_min)
    across = (y_max - y_min) if cor["axis"] == "east" else (x_max - x_min)
    assert np.isclose(along, min(c.length_m, ny * dx - dx))
    assert np.isclose(across, c.width_m)
    assert cor["apex_kind"] in ("fan", "river", "discharge")


def test_bake_corridor_writes_consistent_files(baked, hero_cfg):
    man, out, eng = baked
    hero, cfg = hero_cfg
    for name in man["files"] + ["manifest.json"]:
        assert (out / name).exists(), name
        assert (eng / name).exists(), name
    assert json.loads((out / "manifest.json").read_text()) == json.loads(
        (eng / "manifest.json").read_text()
    )
    voxel = cfg.profile.corridor.voxel_m
    z, meta = read_heightmap(out / "heightmap")
    assert meta["spacing_m"] == voxel
    # 엔진 원점은 회랑 가운데: 높이맵 가로·세로 가운데가 (0, 0)
    x0, _, z0 = meta["origin"]
    assert np.isclose(x0 + 0.5 * (meta["width"] - 1) * voxel, 0.0, atol=voxel)
    assert np.isclose(z0 + 0.5 * (meta["height"] - 1) * voxel, 0.0, atol=voxel)
    assert meta["origin"][1] == -man["frame"]["y_offset_m"]
    assert meta["min"] + meta["origin"][1] >= 0.0  # 엔진 Y ≥ 0 (최저 지표를 내림한 기준)
    for stem in ("water", "water_table"):
        w, wm = read_heightmap(out / stem)
        assert w.shape == z.shape and wm["origin"] == meta["origin"]
    w, _ = read_heightmap(out / "water")
    wet = w > WATER_NONE_M
    assert np.all(w[wet] >= z[wet] - 0.011)
    # 지표 높이 = 3D 함수의 지표 (회랑 격자의 국소 좌표로 다시 계산)
    vol = HeroVolume(hero, cfg)
    fr = man["frame"]
    cx, cy = fr["local_origin_east_north_m"]
    cols = np.arange(meta["width"]) * voxel + x0 + cx
    rows = cy - (np.arange(meta["height"]) * voxel + z0)
    gx, gy = np.meshgrid(cols, rows)
    h = vol.surface_height(gx.ravel(), gy.ravel()).reshape(gx.shape)
    assert np.allclose(z, h.astype(np.float32), atol=1e-3)
    # surround25 = 히어로 z_m
    s, sm = read_heightmap(out / "surround25")
    assert s.shape == hero.graph.shape
    assert np.allclose(s.ravel(), np.asarray(hero.fields["z_m"], dtype=np.float32))
    # 재질 부피
    st = json.loads((out / "strata.json").read_text())
    rows_n, layers_n, cols_n = st["shape"]
    raw = np.fromfile(out / "strata.u8", dtype=np.uint8)
    assert raw.size == rows_n * layers_n * cols_n
    top, tm = read_heightmap(out / "strata_top")
    assert top.shape == (rows_n, cols_n)
    assert tm["origin"][0] == st["origin_xz"][0] and tm["origin"][2] == st["origin_xz"][1]
    ok = set(range(rk.N_ROCKS)) | {STRATA_WATER, STRATA_AIR}
    assert set(np.unique(raw).tolist()) <= ok
    vol3 = raw.reshape(rows_n, layers_n, cols_n)
    assert (vol3[:, -1, :] < rk.N_ROCKS).mean() > 0.95  # 맨 아래 층은 거의 다 암석
    assert man["corridor"]["n_entrance_cells"] >= 0


def test_cave_mesh_faces_into_void(baked, hero_cfg):
    import trimesh

    man, out, _ = baked
    hero, cfg = hero_cfg
    # HERO_OVERRIDES 로 회랑에 동굴이 생기는 조건을 고정했으므로 건너뛰지 않습니다.
    assert "caves.glb" in man["files"], "회랑에 동굴이 없습니다 (HERO_OVERRIDES 설명)"
    scene = trimesh.load(out / "caves.glb")
    mesh = next(iter(scene.geometry.values()))
    assert len(mesh.faces) == man["file_meta"]["caves"]["faces"]
    rgba = np.asarray(mesh.visual.vertex_colors)
    assert rgba.shape == (len(mesh.vertices), 4)
    assert (rgba[:, 3] < rk.N_ROCKS).all()
    assert np.array_equal(rgba[:, :3], np.asarray(rk.COLOR_RGB)[rgba[:, 3]])


def test_cave_surface_orientation_fine_voxels(hero_cfg):
    # 고운 복셀(2 m)로 입구 둘레를 굽고, 법선이 d_cave 가 줄어드는 쪽(빈 곳)을 보는지 확인
    hero, cfg = hero_cfg
    vol = HeroVolume(hero, cfg)
    assert vol.cap_a.shape[0] > 0, "동굴 입구가 없습니다 (HERO_OVERRIDES 설명)"
    c = 0.5 * (vol.cap_a[0] + vol.cap_b[0])
    rect = (c[0] - 60.0, c[0] + 60.0, c[1] - 60.0, c[1] + 60.0)
    verts, faces, diag = cave_surface(vol, rect, 2.0)
    assert faces.shape[0] > 100
    v0, v1, v2 = (verts[faces[:, k]] for k in range(3))
    nrm = np.cross(v1 - v0, v2 - v0)
    area = np.linalg.norm(nrm, axis=1)
    keep = area > 1e-9
    n = nrm[keep] / area[keep, None]
    cen = ((v0 + v1 + v2) / 3)[keep]
    dp = vol.evaluate(cen + 0.3 * n)["d_cave"]
    dm = vol.evaluate(cen - 0.3 * n)["d_cave"]
    ok = np.isfinite(dp) & np.isfinite(dm)
    assert np.average(dp[ok] < dm[ok], weights=area[keep][ok]) > 0.9
    # 남긴 삼각형은 모두 땅 속
    assert (vol.evaluate(cen)["d1"] < 0).all()
    assert diag["faces_kept"] == faces.shape[0]
