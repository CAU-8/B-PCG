"""지구본 굽기 검사 (engine/GLOBE.md 파일 형식): C# 이 쓴 globe/ 와 같은 실행의 행성 묶음.

파일 크기·필드 설명·면 기저(Godot 좌표, 오른손)·등각 사상의 왕복·꼭짓점 이음매, 그리고 표본 규칙
(nearest = 가장 가까운 L0 칸, mean_k4 = 가장 가까운 4 칸 평균)을 행성 묶음에서 다시 계산해 봅니다.
"""

import numpy as np
import pytest
from bundles import read_json
from scipy.spatial import cKDTree

ITEM = {"float32": 4, "uint8": 1}


@pytest.fixture(scope="module")
def globe_dir(tiny_run):
    return tiny_run / "globe"


@pytest.fixture(scope="module")
def meta(globe_dir):
    return read_json(globe_dir / "globe.json")


def _load(globe_dir, entry) -> np.ndarray:
    dt = {"float32": "<f4", "uint8": "u1"}[entry["dtype"]]
    return np.fromfile(globe_dir / entry["file"], dtype=dt).reshape(entry["shape"])


def _faces(meta):
    n = np.array([f["n"] for f in meta["faces"]], float)
    u = np.array([f["u"] for f in meta["faces"]], float)
    v = np.array([f["v"] for f in meta["faces"]], float)
    return n, u, v


def _cell_dirs(meta, corners: bool) -> np.ndarray:
    """(6, R, R, 3) 칸 중심(또는 꼭짓점)의 단위 방향 (globe.json 의 mapping)."""
    N = meta["face_res"]
    n, u, v = _faces(meta)
    t = -1.0 + 2.0 * (np.arange(N + 1) if corners else np.arange(N) + 0.5) / N
    a, b = np.meshgrid(t, t, indexing="xy")  # 열 = a(u), 행 = b(v)
    ta = np.where(np.abs(a) >= 1.0, np.sign(a), np.tan(a * np.pi / 4.0))
    tb = np.where(np.abs(b) >= 1.0, np.sign(b), np.tan(b * np.pi / 4.0))
    d = (
        n[:, None, None]
        + ta[None, ..., None] * u[:, None, None]
        + tb[None, ..., None] * v[:, None, None]
    )
    return d / np.linalg.norm(d, axis=-1, keepdims=True)


def _dir_to_cell(meta, d: np.ndarray) -> np.ndarray:
    """방향 → (면, 행, 열) 번호 (mapping_inverse)."""
    N = meta["face_res"]
    n, u, v = _faces(meta)
    f = np.argmax(d @ n.T, axis=-1)
    dn = np.sum(d * n[f], -1)
    a = 4 / np.pi * np.arctan(np.sum(d * u[f], -1) / dn)
    b = 4 / np.pi * np.arctan(np.sum(d * v[f], -1) / dn)
    col = np.clip(np.floor((a + 1) * N / 2).astype(int), 0, N - 1)
    row = np.clip(np.floor((b + 1) * N / 2).astype(int), 0, N - 1)
    return (f * N + row) * N + col


def _l0_godot(meta, planet) -> np.ndarray:
    m = np.asarray(meta["frame"]["matrix_planet_to_godot"], float)
    unit = planet.graph.pos / np.linalg.norm(planet.graph.pos, axis=1, keepdims=True)
    return unit @ m.T


def test_files_exist_with_exact_sizes(globe_dir, meta):
    entries = meta["fields"] + meta["overlays"] + [meta["corners"]]
    for e in entries:
        size = int(np.prod(e["shape"])) * ITEM[e["dtype"]]
        assert (globe_dir / e["file"]).stat().st_size == size == meta["file_bytes"][e["file"]], e[
            "file"
        ]
    N = meta["face_res"]
    assert meta["corners"]["shape"] == [6, N + 1, N + 1]
    assert all(e["shape"] == [6, N, N] for e in meta["fields"] + meta["overlays"])


def test_fields_say_what_they_are(meta):
    for f in meta["fields"]:
        for k in ("label", "group", "description", "how_to_read", "kind", "source", "sampling"):
            assert f.get(k) not in (None, ""), (f["name"], k)
        assert f["sampling"] in meta["sampling"], f["name"]
        if f["kind"] == "continuous":
            vals = [k[0] for k in f["colormap"]]
            assert all(b > a for a, b in zip(vals, vals[1:], strict=False)), f["name"]
        else:
            ids = [c["id"] for c in f["categories"]]
            assert len(ids) == len(set(ids)), f["name"]
    elev = next(f for f in meta["fields"] if f["name"] == "elevation")
    assert elev["color_break"] == 0.0 and elev["source"] == "z_mean_m"


def test_face_bases_are_godot_frame_of_planet_bases(meta, planet):
    n, u, v = _faces(meta)
    assert np.allclose(np.cross(u, v), n)  # u × v = n (바깥)
    m = np.asarray(meta["frame"]["matrix_planet_to_godot"], float)
    fb = planet.manifest["face_basis"]
    for key, got in (("n", n), ("u", u), ("v", v)):
        assert np.allclose(np.asarray(fb[key], float) @ m.T, got), key


def test_mapping_round_trips(meta):
    d = _cell_dirs(meta, corners=False).reshape(-1, 3)
    assert np.array_equal(_dir_to_cell(meta, d), np.arange(d.shape[0]))


def test_corner_grids_are_seamless(globe_dir, meta):
    corners = _load(globe_dir, meta["corners"])
    d = _cell_dirs(meta, corners=True)
    N = meta["face_res"]
    edge = np.zeros((N + 1, N + 1), dtype=bool)
    edge[0, :] = edge[-1, :] = edge[:, 0] = edge[:, -1] = True
    seen: dict[tuple, float] = {}
    shared = 0
    for f in range(6):
        for r, c in zip(*np.nonzero(edge), strict=True):
            key = tuple(np.round(d[f, r, c], 9))
            z = float(corners[f, r, c])
            if key in seen:
                assert z == seen[key], (f, r, c)
                shared += 1
            else:
                seen[key] = z
    assert shared > 0


def test_nearest_fields_equal_nearest_l0_cell(globe_dir, meta, planet):
    tree = cKDTree(_l0_godot(meta, planet))
    d = _cell_dirs(meta, corners=False).reshape(-1, 3)
    _, nearest = tree.query(d, k=1)
    checked = 0
    for f in meta["fields"]:
        if f["sampling"] != "nearest" or f["source"] not in planet.fields:
            continue
        got = _load(globe_dir, f).reshape(-1)
        want = planet[f["source"]][nearest].astype(got.dtype)
        no_data = {c["id"] for c in f.get("categories", []) if c.get("no_data")}
        if no_data:  # 바다의 지질처럼 '값 없음' 범주인 칸은 가장 가까운 L0 칸이 바다
            blank = np.isin(got, list(no_data))
            assert np.array_equal(blank, planet["is_ocean"][nearest]), f["name"]
            got, want = got[~blank], want[~blank]
        assert np.array_equal(got, want), f["name"]
        checked += 1
    assert checked >= 3


def test_elevation_is_mean_of_four_nearest(globe_dir, meta, planet):
    tree = cKDTree(_l0_godot(meta, planet))
    d = _cell_dirs(meta, corners=False).reshape(-1, 3)
    _, idx = tree.query(d, k=4)
    elev = next(f for f in meta["fields"] if f["name"] == "elevation")
    got = _load(globe_dir, elev).reshape(-1).astype(np.float64)
    want = planet["z_mean_m"].astype(np.float64)[idx].mean(axis=1)
    assert np.allclose(got, want, rtol=0, atol=1e-2)


def test_overlays_use_documented_values(globe_dir, meta):
    for o in meta["overlays"]:
        vals = set(np.unique(_load(globe_dir, o)).tolist())
        if o.get("categories"):
            assert vals <= {c["id"] for c in o["categories"]}, o["name"]
        else:
            assert vals <= {0, 1}, o["name"]


def test_hero_is_godot_frame_of_hero_site(meta, hero):
    h = meta["hero"]
    m = np.asarray(meta["frame"]["matrix_planet_to_godot"], float)
    site = hero.meta["site"]
    assert np.allclose(h["unit_planet"], site["center_unit"])
    assert np.allclose(h["unit"], m @ np.asarray(site["center_unit"]))
    assert h["lat_deg"] == pytest.approx(site["lat_deg"]) and h["lon_deg"] == pytest.approx(
        site["lon_deg"]
    )
