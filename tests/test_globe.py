"""지구본 굽기 검사 (bpcg.bake.globe).

tiny 행성 하나를 만들어 면당 칸 수가 L0 와 같을 때와 절반일 때 굽고, 엔진이 기대는 약속
(파일 크기, 설명, 면 기저, 등각 사상, 면 이음매, 다시 담기 규칙, Godot 좌표 규칙)을 봅니다.
검사 쪽 계산(사상, 좌표 변환, 이웃 찾기)은 모듈 함수를 쓰지 않고 globe.json 의 글로 된 규칙대로
따로 짭니다.
"""

import json
import math
import re
from types import SimpleNamespace

import numpy as np
import pytest
from scipy.spatial import cKDTree

from bpcg.bake import globe as gb
from bpcg.bake.bundle import save_planet_state
from bpcg.core import cubesphere as cs
from bpcg.core.config import load_config
from bpcg.core.constants import SECONDS_PER_YEAR
from bpcg.core.graph import sphere_graph
from bpcg.pipeline import generate_planet

REQUIRED_FIELDS = {
    "elevation", "plate_id", "boundary_type", "crust_type", "ocean_age_myr", "uplift_mm_per_yr",
    "precip_m_per_yr", "temperature_c", "surface_rock", "relief_m", "caves",
    "discharge_log10_m3_per_s", "geology_template",
}  # fmt: skip
# L0 에서 칸 안 기복과 거의 같은 값이라 싣지 않는 레이어 (bake.globe._build_fields 설명)
DROPPED_FIELDS = {"water_table_depth_m", "valley_depth_m"}
HANGUL = re.compile(r"[가-힣]")
HERO_PLANET_UNIT = np.array([0.3, -0.5, 0.8]) / np.linalg.norm([0.3, -0.5, 0.8])


@pytest.fixture(scope="module")
def cfg():
    return load_config("earth", "tiny")


@pytest.fixture(scope="module")
def planet(cfg):
    return generate_planet(cfg, log=None)


@pytest.fixture(scope="module", params=[1, 2], ids=["res_l0", "res_half"])
def baked(request, planet, cfg, tmp_path_factory):
    n = planet.graph.shape[1]
    res = n // request.param
    out = tmp_path_factory.mktemp(f"globe_{res}")
    hero = {"unit": HERO_PLANET_UNIT.tolist(), "size_m": 6400.0, "seed": int(cfg.planet.seed)}
    meta = gb.bake_globe(planet, cfg, out, face_res=res, hero=hero, log=None)
    return meta, out, res


# ------------------------------------------------ 검사 쪽 규칙 (globe.json 의 글대로)
def _godot(p: np.ndarray) -> np.ndarray:
    """frame.rule: godot = (planet_x, planet_z, -planet_y)."""
    p = np.asarray(p, dtype=np.float64)
    return np.stack([p[..., 0], p[..., 2], -p[..., 1]], axis=-1)


def _bases(meta) -> np.ndarray:
    return np.array([[f["n"], f["u"], f["v"]] for f in meta["faces"]], dtype=np.float64)


def _dirs(bases: np.ndarray, res: int, corners: bool = False) -> np.ndarray:
    """mapping: dir ∝ n + tan(a·π/4)·u + tan(b·π/4)·v, (6, R, R, 3)."""
    k = np.arange(res + 1) if corners else np.arange(res) + 0.5
    t = -1.0 + 2.0 * k / res
    out = np.empty((6, t.size, t.size, 3))
    for f in range(6):
        for r, b in enumerate(t):
            for c, a in enumerate(t):
                d = bases[f, 0] + math.tan(a * math.pi / 4) * bases[f, 1]
                d = d + math.tan(b * math.pi / 4) * bases[f, 2]
                out[f, r, c] = d / np.linalg.norm(d)
    return out


def _inverse(d: np.ndarray, bases: np.ndarray, res: int):
    """mapping_inverse: 방향 → (f, a, b, r, c)."""
    f = np.argmax(d @ bases[:, 0].T, axis=1)
    dn = (d * bases[f, 0]).sum(1)
    a = 4 / math.pi * np.arctan((d * bases[f, 1]).sum(1) / dn)
    b = 4 / math.pi * np.arctan((d * bases[f, 2]).sum(1) / dn)
    c = np.clip(np.floor((a + 1) * res / 2).astype(int), 0, res - 1)
    r = np.clip(np.floor((b + 1) * res / 2).astype(int), 0, res - 1)
    return f, a, b, r, c


def _load(out, entry) -> np.ndarray:
    dt = np.dtype(entry["dtype"]).newbyteorder("<")
    return np.fromfile(out / entry["file"], dtype=dt).reshape(entry["shape"])


def _field(meta, name) -> dict:
    return next(e for e in meta["fields"] if e["name"] == name)


def _knn(planet, meta, res, k):
    """칸 중심마다 가장 가까운 L0 칸 k 개 (Godot 좌표에서 따로 찾음), (6·res², k)."""
    tree = cKDTree(_godot(planet.graph.unit()))
    _, idx = tree.query(_dirs(_bases(meta), res).reshape(-1, 3), k=k)
    return idx.reshape(-1, k)


# ---------------------------------------------------------------- 파일과 설명
def test_files_exist_with_exact_sizes(baked):
    meta, out, res = baked
    text = (out / "globe.json").read_text(encoding="utf-8")

    def no_nan(tok):
        raise AssertionError(f"globe.json 에 {tok} 가 있습니다")

    assert json.loads(text, parse_constant=no_nan) == meta
    assert meta["format"] == "bpcg-globe" and meta["format_version"] == 1
    assert meta["face_res"] == res and meta["radius_m"] == 6_371_000.0
    assert meta["frame"]["rule"].startswith("godot = (planet_x, planet_z, -planet_y)")
    assert meta["sea_level_m"] == 0.0 and meta["mapping"] == gb.MAPPING
    for e in meta["fields"] + meta["overlays"]:
        size = 6 * res * res * np.dtype(e["dtype"]).itemsize
        assert e["shape"] == [6, res, res], e["name"]
        assert (out / e["file"]).stat().st_size == size == meta["file_bytes"][e["file"]]
    c = meta["corners"]
    assert c["shape"] == [6, res + 1, res + 1] and c["unit"] == "m" and c["dtype"] == "float32"
    assert (out / c["file"]).stat().st_size == 6 * (res + 1) ** 2 * 4
    assert not list(out.glob("*.part"))


def test_fields_say_what_they_are(baked):
    meta, out, _ = baked
    names = {e["name"] for e in meta["fields"]}
    assert REQUIRED_FIELDS <= names
    assert {o["name"] for o in meta["overlays"]} == {"rivers", "lakes"}
    assert meta["fields"][0]["name"] == "elevation"
    for e in meta["fields"]:
        assert e["file"] == e["name"] + ".bin" and e["group"] in gb.GROUPS
        for key in ("label", "description", "how_to_read"):
            assert HANGUL.search(e[key]), (e["name"], key)
        arr = _load(out, e)
        if e["kind"] == "continuous":
            assert e["dtype"] == "float32" and e["unit"] and isinstance(e["log"], bool)
            vals = [s[0] for s in e["colormap"]]
            assert vals == sorted(set(vals)), e["name"]  # 값이 늘어남
            assert e["min"] == vals[0] and e["max"] == vals[-1]
            for _, rgb in e["colormap"]:
                assert len(rgb) == 3 and all(isinstance(x, int) and 0 <= x <= 255 for x in rgb)
            if np.isnan(arr).any():
                assert HANGUL.search(e["nan_label"]), e["name"]
        else:
            assert e["kind"] == "categorical" and e["dtype"] == "uint8"
            ids = [c["id"] for c in e["categories"]]
            assert len(ids) == len(set(ids))
            assert all(HANGUL.search(c["label"]) for c in e["categories"])
            assert set(np.unique(arr)) <= set(ids), e["name"]
            # '값 없음' 범주는 넓이 몫이 없고 nan_rgb 로 칠합니다. 나머지 몫의 합은 1 입니다.
            for c in e["categories"]:
                if c.get("no_data"):
                    assert c["id"] == 255 and c["area_fraction"] is None
                    assert c["rgb"] == meta["nan_rgb"]
            fracs = [c["area_fraction"] for c in e["categories"] if not c.get("no_data")]
            assert sum(fracs) == pytest.approx(1.0, abs=1e-4), e["name"]
            assert e["area_fraction_basis"] in ("land", "planet")
            assert HANGUL.search(e["area_fraction_note"])
    for o in meta["overlays"]:
        assert HANGUL.search(o["label"]) and HANGUL.search(o["description"])
        assert set(np.unique(_load(out, o))) <= {c["id"] for c in o["categories"]}


def test_elevation_colormap_breaks_at_sea_level(baked):
    # 약속 (engine/GLOBE.md): 색표 값은 엄격히 늘어나고, 해수면의 끊김은 -0.5 m 매듭(가장 얕은
    # 바다 색)과 0 m 매듭(낮은 땅 초록) 사이 0.5 m 와 color_break = 0 으로 나타냅니다.
    e = _field(baked[0], "elevation")
    cm = e["colormap"]
    below = [rgb for v, rgb in cm if v < 0]
    above = [rgb for v, rgb in cm if v >= 0]
    assert cm[0][0] < 0 < cm[-1][0] and below and above
    assert all(b > r and b > g for r, g, b in below)  # 바다는 파랑 계열
    vals = [v for v, _ in cm]
    k = vals.index(0.0)
    assert vals[k - 1] == gb.gt.SHORE_STOP_M == -0.5
    assert cm[k - 1][1] == list(gb.gt.OCEAN_SHORE_RGB) and cm[k][1] == list(gb.gt.LAND_STOPS[0][1])
    assert e["color_break"] == 0.0 == baked[0]["sea_level_m"]
    other = [f for f in baked[0]["fields"] if f["kind"] == "continuous" and f is not e]
    assert all("color_break" not in f for f in other)


# ---------------------------------------------------------------- 면 기저와 사상
def test_face_bases_orthonormal_right_handed_godot_frame(baked):
    meta = baked[0]
    b = _bases(meta)
    for f in range(6):
        n, u, v = b[f]
        assert np.allclose(b[f] @ b[f].T, np.eye(3), atol=1e-12)
        assert np.allclose(np.cross(u, v), n, atol=1e-12)  # handedness: u × v = n
        assert np.allclose(n, _godot(cs.FACE_N[f])) and np.allclose(u, _godot(cs.FACE_U[f]))
        assert np.allclose(v, _godot(cs.FACE_V[f]))
    north = [f for f in range(6) if np.allclose(cs.FACE_N[f], [0, 0, 1])][0]
    assert np.allclose(b[north, 0], [0, 1, 0])  # 행성 자전축 z(북쪽) = Godot +Y


def test_documented_mapping_round_trips(baked):
    meta, _, res = baked
    b = _bases(meta)
    rng = np.random.default_rng(7)
    d = rng.normal(size=(4000, 3))
    d /= np.linalg.norm(d, axis=1, keepdims=True)
    f, a, bb, r, c = _inverse(d, b, res)
    ta, tb = np.tan(a * np.pi / 4)[:, None], np.tan(bb * np.pi / 4)[:, None]
    back = b[f, 0] + ta * b[f, 1] + tb * b[f, 2]
    back /= np.linalg.norm(back, axis=1, keepdims=True)
    assert np.abs(back - d).max() < 1e-12
    centers = _dirs(b, res)
    # 그 방향을 담은 칸의 중심은 가까이 있어야 합니다 (칸 한 변 π/(2N) 안쪽; 반대각선은 약 0.71 배)
    ang = np.arccos(np.clip((centers[f, r, c] * d).sum(1), -1, 1))
    assert ang.max() < math.pi / (2 * res)
    # 모듈의 역사상과 같고, 칸 중심은 자기 칸으로 돌아옵니다
    fm, rm, cm = gb.direction_to_cell(d, b, res)
    assert np.array_equal(fm, f) and np.array_equal(rm, r) and np.array_equal(cm, c)
    ff, _, _, rr, cc = _inverse(centers.reshape(-1, 3), b, res)
    want = np.arange(6 * res * res)
    assert np.array_equal((ff * res + rr) * res + cc, want)
    assert np.allclose(gb.face_directions(b, res), centers, atol=1e-14)


def test_documented_mapping_matches_bundle_cell_convention():
    # 묶음의 셀 번호 c = f·n² + j·n + i 위치와 문서의 칸 (f, r = j, c = i) 중심이 같아야 합니다
    n = 6
    basis = {"n": cs.FACE_N.tolist(), "u": cs.FACE_U.tolist(), "v": cs.FACE_V.tolist()}
    bases = gb.face_bases(basis)  # 묶음 manifest 의 face_basis 와 같은 모양
    grid = cs.cubesphere_grid(n, 1.0)
    assert np.abs(_dirs(bases, n).reshape(-1, 3) - grid.pos).max() < 1e-12
    flat = sphere_graph(n, 6_371_000.0)
    assert gb.check_bundle_mapping(flat, bases, 0.0)["max_error_cells"] < 1e-9
    jit = sphere_graph(n, 6_371_000.0, jitter=1.0, seed=3)
    check = gb.check_bundle_mapping(jit, bases, 1.0)
    assert 0.0 < check["max_error_ratio"] <= 1.0
    with pytest.raises(ValueError, match="맞지 않습니다"):
        gb.check_bundle_mapping(jit, bases, 0.1)  # 흔들기보다 작은 허용치
    swapped = sphere_graph(n, 6_371_000.0)
    swapped.pos = swapped.pos.reshape(6, n, n, 3).transpose(0, 2, 1, 3).reshape(-1, 3).copy()
    with pytest.raises(ValueError, match="맞지 않습니다"):
        gb.check_bundle_mapping(swapped, bases, 1.0)  # 행·열이 바뀐 묶음


def test_bad_face_basis_is_rejected():
    left = {"n": cs.FACE_N.tolist(), "u": cs.FACE_V.tolist(), "v": cs.FACE_U.tolist()}
    with pytest.raises(ValueError, match="오른손"):
        gb.face_bases(left)
    with pytest.raises(ValueError):
        gb.face_bases({"n": cs.FACE_N.tolist()})


def test_corner_grids_are_seamless(baked):
    meta, out, res = baked
    corners = np.fromfile(out / meta["corners"]["file"], dtype="<f4").reshape(6, res + 1, res + 1)
    assert np.isfinite(corners).all()
    d = _dirs(_bases(meta), res, corners=True).reshape(-1, 3)
    pairs = cKDTree(d).query_pairs(r=1e-9, output_type="ndarray")
    # 같은 점은 다른 면에서만 나오고, 서로 다른 점의 수는 오일러 공식 6N² + 2 입니다
    face = np.repeat(np.arange(6), (res + 1) ** 2)
    assert (face[pairs[:, 0]] != face[pairs[:, 1]]).all()
    rep = np.arange(d.shape[0])
    np.minimum.at(rep, pairs[:, 1], pairs[:, 0])
    assert np.unique(rep).size == 6 * res * res + 2
    z = corners.ravel()
    assert np.array_equal(z[pairs[:, 0]], z[pairs[:, 1]])  # 이웃 면이 같은 높이를 씀
    assert np.abs(d[pairs[:, 0]] - d[pairs[:, 1]]).max() < 1e-12


# ---------------------------------------------------------------- 다시 담기
def test_categorical_values_equal_nearest_l0_cell(baked, planet):
    meta, out, res = baked
    nn = _knn(planet, meta, res, 1)[:, 0]
    f = planet.fields
    ocean = np.asarray(f["is_ocean"], dtype=bool)[nn]
    assert ocean.any() and (~ocean).any()
    for name, src, ocean_id in (
        ("plate_id", "plate_id", None),
        ("crust_type", "crust_type", None),
        ("surface_rock", "surface_rock", 255),
        ("geology_template", "template_id", 255),
    ):
        e = _field(meta, name)
        assert e["sampling"] == "nearest"
        want = np.asarray(f[src])[nn].astype(np.uint8)
        if ocean_id is not None:  # 지질은 바다에서 계산하지 않음 → '값 없음' 범주
            want[ocean] = ocean_id
            assert e["area_fraction_basis"] == "land"
            nd = [c for c in e["categories"] if c.get("no_data")]
            assert [c["id"] for c in nd] == [255] and "바다" in nd[0]["label"]
            assert "바다" in e["description"] and "육지" in e["how_to_read"]
        assert np.array_equal(_load(out, e).ravel(), want), name
    bt = _load(out, _field(meta, "boundary_type")).ravel()
    want = np.asarray(f["boundary_type"])[nn]
    keep = want != 0
    assert np.array_equal(bt[keep], want[keep])  # 경계 칸은 가장 가까운 칸 그대로
    assert set(np.unique(bt)) <= {0, 1, 2, 3}


def test_geology_area_fraction_counts_land_only(baked, planet):
    meta = baked[0]
    f = planet.fields
    land = ~np.asarray(f["is_ocean"], dtype=bool)
    area = np.asarray(planet.graph.area, dtype=np.float64)
    for name, src in (("surface_rock", "surface_rock"), ("geology_template", "template_id")):
        ids = np.asarray(f[src]).astype(int)[land]
        w = np.bincount(ids, weights=area[land], minlength=256) / area[land].sum()
        for c in _field(meta, name)["categories"]:
            if not c.get("no_data"):
                assert c["area_fraction"] == pytest.approx(w[c["id"]], abs=1e-5), (name, c)
    # 판·지각처럼 바다에서도 뜻이 있는 범주는 행성 전체 넓이 기준입니다
    assert _field(meta, "crust_type")["area_fraction_basis"] == "planet"


def test_dropped_layers_are_explained_in_relief(baked, planet):
    meta = baked[0]
    names = {e["name"] for e in meta["fields"]}
    assert not names & DROPPED_FIELDS
    f = planet.fields
    land = ~np.asarray(f["is_ocean"], dtype=bool)
    rel = np.asarray(f["relief_m"], dtype=np.float64)[land]
    # 싣지 않는 까닭이 자료에서도 맞는지: 지하수면 깊이 ≈ mean_fraction × 기복, 골짜기 깊이 ≈ 기복
    gap = np.median(np.maximum(np.asarray(f["z_m"]) - np.asarray(f["water_table_m"]), 0)[land])
    assert gap < 0.1 * np.median(rel)
    vd = np.asarray(f["valley_depth_m"], dtype=np.float64)[land]
    ok = rel > 0
    assert np.median(vd[ok] / rel[ok]) == pytest.approx(1.0, abs=0.2)
    desc = _field(meta, "relief_m")["description"]
    assert "지하수면" in desc and "valley_depth_m" in desc and "따로 싣지 않고" in desc


def test_thin_and_rare_features_are_kept(planet, cfg, tmp_path):
    # 동굴·호수·판 경계처럼 드문 칸이 절반 해상도에서도 사라지지 않는지 봅니다
    n_cells = planet.graph.n_cells
    rng = np.random.default_rng(11)
    pick = rng.choice(n_cells, size=12, replace=False)
    lvl0 = np.full(n_cells, np.nan)
    lvl1 = np.full(n_cells, np.nan)
    lvl0[pick[:8]] = 100.0
    lvl1[pick[4:]] = 200.0
    lake = np.zeros(n_cells, dtype=bool)
    lake[pick[:3]] = True
    fields = {**planet.fields, "cave_level_0_m": lvl0, "cave_level_1_m": lvl1, "is_lake": lake}
    state = SimpleNamespace(graph=planet.graph, fields=fields)
    res = planet.graph.shape[1] // 2
    meta = gb.bake_globe(state, cfg, tmp_path, face_res=res, log=None)
    caves = _load(tmp_path, _field(meta, "caves")).ravel()
    lakes = _load(tmp_path, next(o for o in meta["overlays"] if o["name"] == "lakes")).ravel()
    ff, _, _, rc, cc = _inverse(_godot(planet.graph.unit()[pick]), _bases(meta), res)
    cell = (ff * res + rc) * res + cc
    bits = np.where(np.isfinite(lvl0[pick]), 1, 0) | np.where(np.isfinite(lvl1[pick]), 2, 0)
    assert np.all(caves[cell] & bits == bits)
    assert np.all(lakes[cell[:3]] == 1)
    e = _field(meta, "caves")
    cats = {c["id"]: c["area_fraction"] for c in e["categories"]}
    assert cats[3] > 0 and cats[1] > 0 and cats[2] > 0
    # 드문 칸은 엔진이 점으로도 찍습니다 (point_markers), 칸 수가 설명에 나옵니다
    assert e["point_markers"] is True and e["n_nonzero_cells"] == int(np.count_nonzero(caves))
    how = e["how_to_read"]
    assert f"지구본 칸으로는 {e['n_nonzero_cells']}개" in how and "점" in how


def test_continuous_values_finite_where_source_is(baked, planet):
    meta, out, res = baked
    nn = _knn(planet, meta, res, 1)[:, 0]
    f = planet.fields
    for name in ("elevation", "temperature_c", "precip_m_per_yr", "uplift_mm_per_yr"):
        assert np.isfinite(_load(out, _field(meta, name))).all(), name
    age = _load(out, _field(meta, "ocean_age_myr")).ravel()
    assert np.array_equal(np.isfinite(age), np.isfinite(np.asarray(f["ocean_age_myr"])[nn]))
    land = ~np.asarray(f["is_ocean"], dtype=bool)[nn]
    for name in ("relief_m", "discharge_log10_m3_per_s"):
        v = _load(out, _field(meta, name)).ravel()
        assert np.array_equal(np.isfinite(v), land), name
        assert _field(meta, name)["nan_label"]


def test_continuous_values_are_mean_of_four_nearest(baked, planet):
    meta, out, res = baked
    idx = _knn(planet, meta, res, 4)
    f = planet.fields
    e = _field(meta, "uplift_mm_per_yr")
    assert e["unit"] == "mm/yr" and e["sampling"] == "mean_k4"
    want = 1000.0 * np.asarray(f["uplift_m_per_yr"], dtype=np.float64)[idx].mean(axis=1)
    got = _load(out, e).ravel().astype(np.float64)
    assert np.allclose(got, want, rtol=1e-6, atol=1e-9)  # float32 정밀도
    assert np.abs(got).max() > 0
    z = np.asarray(f["z_mean_m"], dtype=np.float64)[idx].mean(axis=1)
    assert np.allclose(_load(out, _field(meta, "elevation")).ravel(), z, rtol=1e-6, atol=1e-3)
    q = _field(meta, "discharge_log10_m3_per_s")
    assert q["log"] is True and q["unit"] == "log10(m³/s)"
    lq = np.log10(np.asarray(f["discharge_m3_per_yr"], np.float64) / SECONDS_PER_YEAR)
    lq[np.asarray(f["is_ocean"], dtype=bool)] = np.nan
    v = lq[idx]
    ok = np.isfinite(v[:, 0])
    want_q = np.nanmean(v[ok], axis=1)
    assert np.allclose(_load(out, q).ravel()[ok], want_q, rtol=1e-6, atol=1e-6)


# ---------------------------------------------------------------- 히어로
def test_hero_planet_unit_becomes_godot_frame(baked):
    h = baked[0]["hero"]
    assert h["label"] == "히어로 유역" and h["size_m"] == 6400.0
    p = HERO_PLANET_UNIT
    assert np.allclose(h["unit"], [p[0], p[2], -p[1]], atol=1e-15)
    assert h["lat_deg"] == pytest.approx(math.degrees(math.asin(p[2])))
    assert h["lon_deg"] == pytest.approx(math.degrees(math.atan2(p[1], p[0])))
    north = gb.hero_entry({"unit": [0, 0, 2], "size_m": 1.0})
    assert np.allclose(north["unit"], [0, 1, 0]) and north["lat_deg"] == pytest.approx(90.0)
    east = gb.hero_entry({"unit": [0, 1, 0], "lat_deg": 0.0, "lon_deg": 90.0})
    assert np.allclose(east["unit"], [0, 0, -1])  # 행성 +y(경도 90°) = Godot -Z
    # 위도·경도는 늘 unit 에서 다시 계산합니다 (엔진이 핀 이름표에 쓰는 값과 같게)
    odd = gb.hero_entry({"unit": [0, 1, 0], "lat_deg": 12.0, "lon_deg": -40.0})
    assert odd["lat_deg"] == pytest.approx(0.0) and odd["lon_deg"] == pytest.approx(90.0)
    assert "경도 90.00°" in odd["description"]


def _engine_lat_lon(g) -> tuple[float, float]:
    """엔진(GlobeData.lat_lon)의 규칙: 위도 = asin(gy), 경도 = atan2(-gz, gx)."""
    return math.degrees(math.asin(g[1])), math.degrees(math.atan2(-g[2], g[0]))


def test_tilted_axis_rotates_frame_so_north_is_up(planet, cfg, tmp_path):
    axis = np.array([1.0, 0.0, 0.0])
    tilted = cfg.with_overrides({"planet.axis": axis.tolist()})
    hero_p = HERO_PLANET_UNIT
    hero = {"unit": hero_p.tolist(), "lat_deg": 0.0, "lon_deg": 0.0, "seed": int(cfg.planet.seed)}
    logs: list[str] = []
    meta = gb.bake_globe(planet, tilted, tmp_path, face_res=4, hero=hero, log=logs.append)
    rot = np.asarray(meta["frame"]["axis_rotation"])
    assert np.allclose(rot @ rot.T, np.eye(3)) and np.linalg.det(rot) == pytest.approx(1.0)
    assert np.allclose(rot @ axis, [0, 0, 1]) and any("돌립니다" in m for m in logs)
    b = _bases(meta)
    for f in range(6):
        assert np.allclose(b[f] @ b[f].T, np.eye(3), atol=1e-12)
        assert np.allclose(np.cross(b[f, 1], b[f, 2]), b[f, 0], atol=1e-12)
    # 행성 자전축 방향의 면이 Godot +Y (북쪽)
    up = [f for f in range(6) if np.allclose(cs.FACE_N[f], axis)][0]
    assert np.allclose(b[up, 0], [0, 1, 0])
    # 히어로: 엔진이 Godot 좌표에서 계산하는 위도·경도 = 설정 자전축 기준 위도(기후) = 글
    h = meta["hero"]
    lat, lon = _engine_lat_lon(h["unit"])
    assert lat == pytest.approx(math.degrees(math.asin(hero_p @ axis)), abs=1e-9)
    assert (h["lat_deg"], h["lon_deg"]) == pytest.approx((lat, lon), abs=1e-9)
    # finder.site_from_cell 의 경도 규칙 (기준 = 자전축 성분이 가장 작은 좌표축을 깎은 방향)
    ref = np.eye(3)[int(np.argmin(np.abs(axis)))]
    ref = ref - (ref @ axis) * axis
    want_lon = math.degrees(math.atan2(np.cross(ref, hero_p) @ axis, hero_p @ ref))
    assert lon == pytest.approx(want_lon, abs=1e-9)
    # 자전축이 z 면 단위 행렬이고 규칙 글도 그대로입니다
    assert np.array_equal(gb.axis_rotation([0, 0, 1]), np.eye(3))
    with pytest.raises(ValueError):
        gb.axis_rotation([0, 0, 0])


def test_hero_with_other_seed_is_dropped(planet, cfg, tmp_path):
    logs: list[str] = []
    hero = {"unit": [1, 0, 0], "seed": int(cfg.planet.seed) + 1}
    meta = gb.bake_globe(planet, cfg, tmp_path, face_res=4, hero=hero, log=logs.append)
    assert meta["hero"] is None and any("시드" in m for m in logs)


def test_hero_from_run_reads_corridor_then_hero(tmp_path):
    assert gb.hero_from_run(tmp_path) is None
    (tmp_path / "hero").mkdir()
    (tmp_path / "hero" / "manifest.json").write_text(
        json.dumps(
            {
                "seed": 0,
                "graph": {"shape": [100, 100], "spacing_m": 25.0},
                "meta": {"site": {"center_unit": [0, 0, 1], "lat_deg": 90.0, "lon_deg": 0.0}},
            }
        ),
        encoding="utf-8",
    )
    h = gb.hero_from_run(tmp_path)
    assert h["unit"] == [0, 0, 1] and h["size_m"] == 2500.0 and h["seed"] == 0
    (tmp_path / "corridor").mkdir()
    (tmp_path / "corridor" / "manifest.json").write_text(
        json.dumps(
            {
                "seed": 0,
                "config_digest": "abc",
                "site": {"hero_center_unit": [1, 0, 0], "hero_lat_deg": 0.0, "hero_lon_deg": 0.0},
                "hero": {"shape": [1280, 1280], "spacing_m": 25.0},
            }
        ),
        encoding="utf-8",
    )
    h = gb.hero_from_run(tmp_path)
    assert h["unit"] == [1, 0, 0] and h["size_m"] == 32000.0 and h["config_digest"] == "abc"


# ---------------------------------------------------------------- 엔진 복사와 명령줄
def test_engine_copy_and_stale_files(planet, cfg, tmp_path):
    out, eng = tmp_path / "out", tmp_path / "engine"
    gb.bake_globe(planet, cfg, out, face_res=8, engine_dir=eng, log=None)
    for p in out.iterdir():
        assert (eng / p.name).read_bytes() == p.read_bytes()
    assert (eng / "caves.bin").exists()
    fields = {k: v for k, v in planet.fields.items() if not k.startswith("cave_level")}
    meta = gb.bake_globe(SimpleNamespace(graph=planet.graph, fields=fields), cfg, out,
                         face_res=8, engine_dir=eng, log=None)  # fmt: skip
    assert "caves" not in {e["name"] for e in meta["fields"]}
    assert not (out / "caves.bin").exists() and not (eng / "caves.bin").exists()


def test_plate_count_guard_skips_only_that_layer(planet, cfg, tmp_path):
    f = planet.fields
    n = planet.graph.n_cells
    ok = {**f, "plate_id": np.arange(n) % 256}  # 판 256 개 (0..255) 는 uint8 에 담김
    meta = gb.bake_globe(SimpleNamespace(graph=planet.graph, fields=ok), cfg, tmp_path / "a",
                         face_res=4, log=None)  # fmt: skip
    assert len(_field(meta, "plate_id")["categories"]) == 256
    logs: list[str] = []
    big = {**f, "plate_id": np.arange(n) % 300}
    meta = gb.bake_globe(SimpleNamespace(graph=planet.graph, fields=big), cfg, tmp_path / "b",
                         face_res=4, log=logs.append)  # fmt: skip
    names = {e["name"] for e in meta["fields"]}
    assert "plate_id" not in names and "elevation" in names and "boundary_type" in names
    assert any("판 레이어만 건너뜁니다" in m for m in logs)


def test_face_res_is_capped(planet, cfg, tmp_path, capsys):
    assert gb.MAX_FACE_RES == 1024
    with pytest.raises(ValueError, match="2..1024"):
        gb.bake_globe(planet, cfg, tmp_path, face_res=1025, log=None)
    with pytest.raises(ValueError, match="2..1024"):
        gb.bake_globe(planet, cfg, tmp_path, face_res=1, log=None)
    with pytest.raises(SystemExit) as e:
        gb.main(["--planet", str(tmp_path), "--face-res", "4096"])
    assert e.value.code == 2 and "2..1024" in capsys.readouterr().err


def test_texts_follow_the_data(planet, cfg, tmp_path):
    f = planet.fields
    # 고도 필드가 z_m 뿐이면 설명도 z_m 을 말합니다
    no_mean = {k: v for k, v in f.items() if k != "z_mean_m"}
    meta = gb.bake_globe(SimpleNamespace(graph=planet.graph, fields=no_mean), cfg,
                         tmp_path / "z", face_res=4, log=None)  # fmt: skip
    e = _field(meta, "elevation")
    assert e["source"] == "z_m" and meta["corners"]["source"] == "z_m"
    assert "골짜기 바닥 고도(z_m)" in e["description"]
    assert "칸 안 기복을 더하지 않" in e["description"]
    assert "(z_m)" in meta["corners"]["description"]
    # 가라앉는 곳·영하인 곳이 없으면 그 색을 말하지 않고, 실제 색표 끝을 인용합니다
    warm = {**f, "uplift_m_per_yr": np.abs(np.asarray(f["uplift_m_per_yr"])) + 1e-6,
            "temperature_c": np.abs(np.asarray(f["temperature_c"])) + 1.0}  # fmt: skip
    meta = gb.bake_globe(SimpleNamespace(graph=planet.graph, fields=warm), cfg,
                         tmp_path / "w", face_res=4, log=None)  # fmt: skip
    u = _field(meta, "uplift_mm_per_yr")
    assert u["colormap"][0][0] == 0.0 and "가라앉는 곳이 없어" in u["how_to_read"]
    assert "진한 보라" not in u["how_to_read"]
    assert f"{u['colormap'][-1][0]:.2g} mm/yr 이상" in u["how_to_read"]
    t = _field(meta, "temperature_c")
    assert t["colormap"][0][0] == 0.0 and "영하인 곳이 없어" in t["how_to_read"]
    assert f"{t['colormap'][-1][0]:.0f} °C 이상" in t["how_to_read"]
    cold = {**f, "uplift_m_per_yr": -np.abs(np.asarray(f["uplift_m_per_yr"])) - 1e-6}
    meta = gb.bake_globe(SimpleNamespace(graph=planet.graph, fields=cold), cfg,
                         tmp_path / "c", face_res=4, log=None)  # fmt: skip
    u = _field(meta, "uplift_mm_per_yr")
    assert "솟는 곳이 없어" in u["how_to_read"] and "짙은 갈색 ≈" not in u["how_to_read"]
    assert f"{u['colormap'][0][0]:.2g} mm/yr 이하" in u["how_to_read"]


def test_cell_sizes_use_one_measure(baked, planet):
    meta, _, res = baked
    n = planet.graph.shape[1]
    r = planet.graph.R
    l0_km = 2 * math.pi * r / (4 * n) / 1000
    globe_km = 2 * math.pi * r / (4 * res) / 1000
    assert f"L0 칸은 약 {l0_km:.1f} km, 지구본 칸은 약 {globe_km:.1f} km" in meta["description"]
    assert f"면 가운데 칸 한 변 약 {l0_km:.1f} km" in _field(meta, "elevation")["description"]
    assert meta["source"]["cell_km_face_center"] == pytest.approx(l0_km, rel=1e-5)


def test_main_bakes_from_bundle(planet, cfg, tmp_path, capsys):
    run = tmp_path / "run"
    save_planet_state(run / "planet", planet, cfg)
    assert gb.main(["--planet", str(run / "planet"), "--face-res", "8"]) == 0
    meta = json.loads((run / "globe" / "globe.json").read_text(encoding="utf-8"))
    assert meta["face_res"] == 8 and meta["config_digest"] == cfg.digest()
    assert meta["hero"] is None
    assert "[지구본] 끝" in capsys.readouterr().out
