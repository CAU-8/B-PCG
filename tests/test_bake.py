"""굽기 검사 (docs/pipeline.md 11장, engine/README.md 넘김 형식): C# 이 쓴 회랑 파일과 면 텍스처.

회랑 manifest 의 files·file_meta 와 각 .json·.bin·.u8·.glb 가 서로 맞는지, 높이맵이 float32
리틀 엔디언 행 우선인지, 동굴 glb 가 엔진이 읽는 꼴(법선·COLOR_0)인지 봅니다.
"""

import json
import struct

import numpy as np
import pytest
from bundles import read_json
from PIL import Image

HEIGHTMAP_STEMS = (
    "heightmap", "surround25", "water", "water_table", "strata_top", "cave_mouth",
    "heightmap_detail", "cave_mouth_detail",
)  # fmt: skip
WATER_NONE_M = -10_000.0


@pytest.fixture(scope="module")
def corridor(tiny_run):
    return tiny_run / "corridor"


@pytest.fixture(scope="module")
def man(corridor):
    return read_json(corridor / "manifest.json")


def _heightmap(corridor, stem):
    meta = read_json(corridor / f"{stem}.json")
    data = np.fromfile(corridor / f"{stem}.bin", dtype="<f4")
    return meta, data.reshape(meta["height"], meta["width"])


def test_manifest_lists_existing_files(corridor, man):
    assert man["format"] == "bpcg-corridor"
    for name in man["files"]:
        assert (corridor / name).is_file(), name
    names = {p.name for p in corridor.iterdir()}
    assert set(man["files"]) | {"manifest.json"} == names


@pytest.mark.parametrize("stem", HEIGHTMAP_STEMS)
def test_heightmaps_match_their_json(corridor, man, stem):
    meta, h = _heightmap(corridor, stem)
    fm = man["file_meta"][stem]  # manifest 쪽에는 n_wet·n_open·detail 같은 요약이 더 있음
    assert {k: fm[k] for k in meta} == meta
    assert meta["format"] == "float32_le" and meta["layout"] == "row_major"
    assert meta["axes"] == "x_east_y_up_z_south"
    assert (corridor / f"{stem}.bin").stat().st_size == meta["width"] * meta["height"] * 4
    assert np.isfinite(h).all()
    assert float(h.min()) == meta["min"] and float(h.max()) == meta["max"]


def test_corridor_grids_share_frame(corridor, man):
    hm, _ = _heightmap(corridor, "heightmap")
    rect = man["corridor"]["rect_engine_m"]
    x0, _y0, z0 = hm["origin"]
    assert x0 == rect["x_min"] and z0 == rect["z_min"]
    assert x0 + (hm["width"] - 1) * hm["spacing_m"] == pytest.approx(rect["x_max"])
    assert z0 + (hm["height"] - 1) * hm["spacing_m"] == pytest.approx(rect["z_max"])
    assert hm["origin"][1] == -man["frame"]["y_offset_m"]
    for stem in ("water", "water_table", "cave_mouth", "heightmap_detail", "cave_mouth_detail"):
        other, _ = _heightmap(corridor, stem)
        for k in ("width", "height", "spacing_m", "origin"):
            assert other[k] == hm[k], (stem, k)
    # 재질 부피 윗면은 회랑 지표를 복셀 간격으로 나눈 격자이고 겹치는 점은 같은 값
    top, t = _heightmap(corridor, "strata_top")
    _, h = _heightmap(corridor, "heightmap")
    step = round(hm["spacing_m"] / top["spacing_m"])
    assert top["origin"] == hm["origin"] and np.array_equal(t[::step, ::step], h)


def test_water_marks_dry_cells_and_counts_wet(corridor, man):
    _, w = _heightmap(corridor, "water")
    wet = w > WATER_NONE_M + 1
    assert (w[~wet] == WATER_NONE_M).all()
    assert man["file_meta"]["water"]["n_wet"] == int(wet.sum()) > 0


def test_strata_volume_matches_json(corridor, man):
    st = read_json(corridor / "strata.json")
    assert st == man["file_meta"]["strata"]
    rows, layers, cols = st["shape"]
    vol = np.fromfile(corridor / "strata.u8", dtype=np.uint8)
    assert vol.size == rows * layers * cols
    counts = {str(k): int(v) for k, v in zip(*np.unique(vol, return_counts=True), strict=True)}
    assert counts == st["counts"]
    assert set(map(int, counts)) <= {e["id"] for e in st["legend"]}
    assert st["depth_m"] == pytest.approx(layers * st["spacing_m"]["y"])
    top = read_json(corridor / "strata_top.json")
    assert (top["width"], top["height"]) == (cols, rows)


def _glb(path):
    raw = path.read_bytes()
    magic, version, length = struct.unpack_from("<4sII", raw, 0)
    assert magic == b"glTF" and version == 2 and length == len(raw)
    jlen, jtype = struct.unpack_from("<II", raw, 12)
    assert jtype == 0x4E4F534A  # 'JSON'
    gltf = json.loads(raw[20 : 20 + jlen])
    blen, btype = struct.unpack_from("<II", raw, 20 + jlen)
    assert btype == 0x004E4942  # 'BIN\0'
    return gltf, raw


def test_cave_mesh_glb(corridor, man):
    info = man["file_meta"]["caves"]
    path = corridor / info["file"]
    assert path.stat().st_size == info["bytes"]
    gltf, _ = _glb(path)
    acc = gltf["accessors"]
    faces = vertices = 0
    for mesh in gltf["meshes"]:
        for prim in mesh["primitives"]:
            attrs = prim["attributes"]
            assert {"POSITION", "NORMAL", "COLOR_0"} <= set(attrs)  # 엔진이 읽는 꼴
            vertices += acc[attrs["POSITION"]]["count"]
            faces += acc[prim["indices"]]["count"] // 3
    assert (faces, vertices) == (info["faces"], info["vertices"])
    assert faces > 0


def test_entrances_match_manifest(corridor, man):
    ent = read_json(corridor / man["caves"]["entrances_file"])
    n = man["caves"]["n_entrances"]
    assert len(ent["inside"]) == len(ent["outside"]) == n > 0
    for p in ent["inside"] + ent["outside"]:
        assert len(p) == 3 and all(np.isfinite(p))


def test_face_textures(tiny_run, planet):
    tex_dir = tiny_run / "planet" / "textures"
    index = read_json(tex_dir / "textures.json")
    n = index["n_per_face"]
    assert n == planet.graph.shape[1]
    assert index["face_basis"] == planet.manifest["face_basis"]
    assert index["layers"]
    for name, layer in index["layers"].items():
        assert len(layer["files"]) == 6, name
        for f in layer["files"]:
            with Image.open(tex_dir / f) as im:
                assert im.size == (n, n), (name, f)
