"""tools/download_pilot.py 검사. 네트워크를 쓰지 않습니다 (curl 호출은 가짜로 바꿉니다).

스크립트는 패키지가 아니라서 importlib 로 파일을 직접 불러옵니다.
"""

import hashlib
import importlib.util
import json
import random

import pytest

from bpcg.core.paths import ROOT


def _load_script():
    spec = importlib.util.spec_from_file_location(
        "download_pilot", ROOT / "tools" / "download_pilot.py"
    )
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


dp = _load_script()
PREFIX = "Copernicus_DSM_COG_30_"


def square(lon, lat, d=0.1):
    """(lon, lat) 에서 시작하는 작은 정사각형 Polygon."""
    ring = [[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]
    return {"type": "Polygon", "coordinates": [ring]}


def feature(obsid, region, ebe, area=100.0, gla=0.0, lon=10.2, lat=20.2):
    props = {
        "OBSID1": obsid, "CNTRY": "TST", "REGION_INT": region, "BASIN": f"basin {obsid}",
        "AREA": area, "EBE_MMKYR": ebe, "EBE_ERR": 1.0, "SLP_AVE": 100.0, "GLA_PCNT": gla,
    }  # fmt: skip
    return {"type": "Feature", "properties": props, "geometry": square(lon, lat)}


def sha(data):
    return hashlib.sha256(data).hexdigest()


# --------------------------------------------------------------------------- 타일 이름과 상자


@pytest.mark.parametrize(
    ("lat", "lon", "name"),
    [
        (-18, 145, "S18_00_E145_00"),
        (0, 30, "N00_00_E030_00"),
        (-1, -1, "S01_00_W001_00"),
        (89, -180, "N89_00_W180_00"),
        (45, 7, "N45_00_E007_00"),
    ],
)
def test_tile_name(lat, lon, name):
    assert dp.tile_name(lat, lon) == f"{PREFIX}{name}_DEM"


def test_bbox_polygon():
    geom = {
        "type": "Polygon",
        "coordinates": [[[30.1, 0.6], [30.4, 0.6], [30.4, 0.74], [30.1, 0.6]]],
    }
    assert dp.bbox(geom) == (30.1, 0.6, 30.4, 0.74)


def test_bbox_multipolygon_with_int_coords():
    geom = {
        "type": "MultiPolygon",
        "coordinates": [
            [[[10, 1], [11, 1], [11, 2], [10, 1]]],
            [[[-5.5, -3], [-4, -3], [-4, -2], [-5.5, -3]]],
        ],
    }
    assert dp.bbox(geom) == (-5.5, -3, 11, 2)
    assert dp.bbox_split(geom) == [(-5.5, -3, 11, 2)]


def test_tiles_for_box_lat_then_lon_order():
    # 145.5..146.2, -18.0..-17.6 -> 위도 오름차순, 그 안에서 경도 오름차순
    tiles = dp.tiles_for_boxes([(145.5, -18.0, 146.2, -17.6)])
    assert tiles == [f"{PREFIX}S18_00_E145_00_DEM", f"{PREFIX}S18_00_E146_00_DEM"]


def test_antimeridian_split():
    # 피지 근처: 동쪽 조각은 179.5..179.9, 서쪽 조각은 -179.9..-179.6
    geom = {
        "type": "MultiPolygon",
        "coordinates": [
            [[[179.5, -17.6], [179.9, -17.6], [179.9, -17.2], [179.5, -17.6]]],
            [[[-179.9, -17.5], [-179.6, -17.5], [-179.6, -17.3], [-179.9, -17.5]]],
        ],
    }
    boxes = dp.bbox_split(geom)
    assert len(boxes) == 2
    (e0, s0, e1, n0), (w0, s1, w1, n1) = boxes
    assert (e0, e1) == (179.5, 180.0)
    assert w0 == -180.0 and w1 == pytest.approx(-179.6)
    assert s0 == s1 == -17.6 and n0 == n1 == -17.2
    tiles = dp.tiles_for_boxes(boxes)
    assert tiles == [f"{PREFIX}S18_00_E179_00_DEM", f"{PREFIX}S18_00_W180_00_DEM"]
    # 나누지 않으면 경도 359칸에 걸친 타일 목록이 나옵니다
    assert len(dp.tiles_for_boxes([dp.bbox(geom)])) > 300

    row = dp.basin_row({"properties": {"OBSID1": "X"}, "geometry": geom})
    assert row["lon_min"] > row["lon_max"]  # 날짜 변경선을 넘는다는 표시
    assert row["tiles"].split(";") == tiles


def test_tiles_clamped_at_edges():
    assert dp.tiles_for_boxes([(179.5, 89.5, 180.0, 90.0)]) == [f"{PREFIX}N89_00_E179_00_DEM"]


# --------------------------------------------------------------------------- 유역 고르기


def synthetic_features():
    feats = [
        feature(f"S{i:04d}", f"R{i % 30:02d}", ebe=1.0 + (i * 37 % 200) * 2.5) for i in range(200)
    ]
    excluded = [
        feature("XGLA", "RX1", 50.0, gla=5.0),  # 빙하
        feature("XSMALL", "RX2", 50.0, area=5.0),  # 10 km² 미만
        feature("XLARGE", "RX3", 50.0, area=2000.0),  # 1000 km² 초과
        feature("XZERO", "RX4", 0.0),  # 침식률 0
        feature("XSENT", "RX5", -9999.99),  # 결측값
        feature("XAREA", "RX6", 50.0, area=-9999.99),  # 면적 결측값
    ]
    no_geom = feature("XNOGEOM", "RX7", 50.0)
    no_geom["geometry"] = None
    no_id = feature(None, "RX8", 50.0)
    return feats + excluded + [no_geom, no_id]


def test_select_basins_filters_and_region_unique():
    feats = synthetic_features()
    chosen = dp.select_basins(feats, 20)
    ids = [f["properties"]["OBSID1"] for f in chosen]
    regions = [f["properties"]["REGION_INT"] for f in chosen]
    assert 0 < len(chosen) <= 20
    assert len(set(regions)) == len(regions)
    assert not any(str(i).startswith("X") for i in ids)
    assert None not in ids


def test_select_basins_deterministic_and_order_free():
    feats = synthetic_features()
    first = [f["properties"]["OBSID1"] for f in dp.select_basins(feats, 20)]
    for seed in range(5):
        shuffled = list(feats)
        random.Random(seed).shuffle(shuffled)
        assert [f["properties"]["OBSID1"] for f in dp.select_basins(shuffled, 20)] == first


def test_select_basins_spreads_over_erosion_bins():
    feats = [feature(f"S{i:03d}", f"R{i:03d}", ebe=float(i + 1)) for i in range(100)]
    chosen = dp.select_basins(feats, 10, bins=5)
    assert len(chosen) == 10
    bins = [int(f["properties"]["EBE_MMKYR"] - 1) // 20 for f in chosen]
    assert sorted(bins) == [0, 0, 1, 1, 2, 2, 3, 3, 4, 4]


def test_dedupe_by_id():
    a, b = feature("A", "R1", 1.0), feature("B", "R2", 2.0)
    a2 = feature("A", "R9", 9.0)
    out = dp.dedupe_by_id([a, b, a2])
    assert [f["properties"]["OBSID1"] for f in out] == ["A", "B"]
    assert out[0]["properties"]["REGION_INT"] == "R1"


# --------------------------------------------------------------------------- 경로와 manifest


def test_data_dir_honors_env(monkeypatch, tmp_path):
    monkeypatch.setenv("BPCG_DATA", str(tmp_path))
    assert dp.data_dir() == tmp_path
    monkeypatch.delenv("BPCG_DATA")
    assert dp.data_dir() == ROOT / "data"


def test_committed_manifest_and_basins_are_consistent():
    manifest = dp.load_manifest(dp.MANIFEST)
    paths = [e["path"] for e in manifest["files"]]
    assert len(paths) == len(set(paths))
    for e in manifest["files"]:
        assert not e["path"].startswith("/") and ".." not in e["path"].split("/")
        assert e["bytes"] > 0 and len(e["sha256"]) == 64
        assert e["group"] in ("pilot", "external")
        assert e["redownload"] in ("script", "manual")
        assert e["license"] and e["note"]
        if e["redownload"] == "script":
            assert {k: e[k] for k in dp.describe(e["path"])} == dp.describe(e["path"])
    rows = dp.read_basins(dp.BASINS_CSV)
    assert len(rows) == 40 and list(rows[0]) == dp.BASIN_COLUMNS
    assert len({r["REGION_INT"] for r in rows}) == 40
    for t in dp.tiles_from_rows(rows):
        assert dp.glo90_path(t) in paths or t in manifest.get("glo90_no_tile", [])


def test_load_manifest_missing(tmp_path):
    assert dp.load_manifest(tmp_path / "none.json") == {"version": 1, "files": []}


def make_data(tmp_path):
    """임시 데이터 폴더, manifest, pilot_basins.csv 를 만듭니다."""
    data = tmp_path / "data"
    tile = f"{PREFIX}N20_00_E010_00_DEM"
    blobs = {
        "pilot/etopo/x.nc": b"a" * 100,
        dp.glo90_path(tile): b"t" * 50,
        "pilot/octopus/crn_int_basins.geojson": b'{"features": []}',
    }
    files = []
    for rel, blob in blobs.items():
        (data / rel).parent.mkdir(parents=True, exist_ok=True)
        (data / rel).write_bytes(blob)
        files.append({"path": rel, "bytes": len(blob), "sha256": sha(blob), "redownload": "script"})
    files.append({"path": "external/a.zip", "bytes": 9, "sha256": "0" * 64, "redownload": "manual"})
    manifest = {"version": 1, "files": files}
    csv_path = tmp_path / "pilot_basins.csv"
    row = dp.basin_row(feature("S1", "R1", 10.0))
    assert row["tiles"] == tile
    dp.write_basins(csv_path, [row])
    return data, manifest, csv_path


def test_verify_ok_and_optional_manual(tmp_path):
    data, manifest, csv_path = make_data(tmp_path)
    assert dp.verify(manifest, data, csv_path) == 0
    assert dp.verify(manifest, data, csv_path, check_hash=True) == 0


def test_verify_missing_and_size_mismatch(tmp_path):
    data, manifest, csv_path = make_data(tmp_path)
    (data / "pilot/etopo/x.nc").write_bytes(b"a" * 99)
    assert dp.verify(manifest, data, csv_path) == 1
    (data / "pilot/etopo/x.nc").unlink()
    assert dp.verify(manifest, data, csv_path) == 1


def test_verify_hash_catches_same_size_corruption(tmp_path):
    data, manifest, csv_path = make_data(tmp_path)
    (data / "pilot/etopo/x.nc").write_bytes(b"b" * 100)
    assert dp.verify(manifest, data, csv_path) == 0
    assert dp.verify(manifest, data, csv_path, check_hash=True) == 1


def test_verify_live_wfs_file_only_warns(tmp_path):
    data, manifest, csv_path = make_data(tmp_path)
    (data / "pilot/octopus/crn_int_basins.geojson").write_bytes(b'{"features": [1]}')
    assert dp.verify(manifest, data, csv_path, check_hash=True) == 0


def test_verify_tile_listed_in_csv_but_not_manifest(tmp_path):
    data, manifest, csv_path = make_data(tmp_path)
    rows = dp.read_basins(csv_path)
    rows.append(dp.basin_row(feature("S2", "R2", 20.0, lon=-70.5, lat=-33.5)))
    dp.write_basins(csv_path, rows)
    assert dp.verify(manifest, data, csv_path) == 1
    manifest["glo90_no_tile"] = [f"{PREFIX}S34_00_W071_00_DEM"]
    assert dp.verify(manifest, data, csv_path) == 0


def test_main_verify_only(tmp_path, monkeypatch):
    data, manifest, csv_path = make_data(tmp_path)
    mpath = tmp_path / "manifest.json"
    mpath.write_text(json.dumps(manifest), encoding="utf-8")
    monkeypatch.setattr(dp, "MANIFEST", mpath)
    monkeypatch.setattr(dp, "BASINS_CSV", csv_path)
    assert dp.main(["--verify-only", "--data", str(data)]) == 0
    monkeypatch.setenv("BPCG_DATA", str(data))
    assert dp.main(["--verify-only"]) == 0
    (data / "pilot/etopo/x.nc").unlink()
    assert dp.main(["--verify-only"]) == 1


def test_require_curl_message(monkeypatch):
    monkeypatch.setattr(dp.shutil, "which", lambda name: None)
    with pytest.raises(SystemExit) as e:
        dp.require_curl()
    assert "curl" in str(e.value.code)


# --------------------------------------------------------------------------- 내려받기 (가짜 curl)


class FakeCurl:
    """run_curl 대신 씁니다. url 별 (HTTP 코드, 종료 코드, 내용)을 정해 둡니다."""

    def __init__(self, table):
        self.table = table
        self.calls = []

    def __call__(self, url, out, resume=False, retry_all=True):
        self.calls.append((url, resume, retry_all))
        code, rc, blob = self.table[url]
        if rc == 0:
            if resume and out.exists():
                have = out.stat().st_size
                out.write_bytes(out.read_bytes() + blob[have:])
            else:
                out.write_bytes(blob)
        return code, rc, "" if rc == 0 else "fake error"


def test_ensure_file_skips_when_size_matches(tmp_path, monkeypatch):
    fake = FakeCurl({})
    monkeypatch.setattr(dp, "run_curl", fake)
    dest = tmp_path / "f.bin"
    dest.write_bytes(b"x" * 10)
    entry = {"bytes": 10, "sha256": sha(b"x" * 10)}
    assert dp.ensure_file("u", dest, entry, check_hash=True)[0] == "ok"
    assert fake.calls == []


def test_download_uses_part_then_replace(tmp_path, monkeypatch):
    blob = b"y" * 30
    monkeypatch.setattr(dp, "run_curl", FakeCurl({"u": (200, 0, blob)}))
    dest = tmp_path / "sub" / "f.bin"
    status, _ = dp.ensure_file("u", dest, {"bytes": 30})
    assert status == "got"
    assert dest.read_bytes() == blob
    assert not dp.part_path(dest).exists()


def test_download_404_is_no_tile_only_when_allowed(tmp_path, monkeypatch):
    monkeypatch.setattr(dp, "run_curl", FakeCurl({"u": (404, 22, b"")}))
    dest = tmp_path / "t.tif"
    assert dp.ensure_file("u", dest, allow_404=True)[0] == "no_tile"
    assert dp.ensure_file("u", dest)[0] == "error"
    assert not dest.exists()


@pytest.mark.parametrize(("code", "rc"), [(403, 22), (0, 6), (0, 28), (500, 22)])
def test_download_other_failures_are_errors(tmp_path, monkeypatch, code, rc):
    monkeypatch.setattr(dp, "run_curl", FakeCurl({"u": (code, rc, b"")}))
    status, msg = dp.ensure_file("u", tmp_path / "t.tif", allow_404=True)
    assert status == "error" and str(rc) in msg


def test_download_size_mismatch_keeps_part(tmp_path, monkeypatch):
    monkeypatch.setattr(dp, "run_curl", FakeCurl({"u": (200, 0, b"z" * 7)}))
    dest = tmp_path / "f.bin"
    assert dp.ensure_file("u", dest, {"bytes": 8})[0] == "error"
    assert not dest.exists() and dp.part_path(dest).stat().st_size == 7
    # --write-manifest 일 때는 새 원본을 받아들입니다
    assert dp.ensure_file("u", dest, {"bytes": 8}, accept_new=True)[0] == "got"
    assert dest.stat().st_size == 7


def test_truncated_final_file_is_resumed(tmp_path, monkeypatch):
    blob = b"0123456789"
    fake = FakeCurl({"u": (206, 0, blob)})
    monkeypatch.setattr(dp, "run_curl", fake)
    dest = tmp_path / "f.bin"
    dest.write_bytes(blob[:4])  # 예전 스크립트가 남긴, 끊긴 파일
    assert dp.ensure_file("u", dest, {"bytes": 10})[0] == "got"
    assert dest.read_bytes() == blob
    assert fake.calls == [("u", True, True)]


def test_larger_final_file_is_left_alone(tmp_path, monkeypatch):
    fake = FakeCurl({})
    monkeypatch.setattr(dp, "run_curl", fake)
    dest = tmp_path / "f.bin"
    dest.write_bytes(b"x" * 11)
    assert dp.ensure_file("u", dest, {"bytes": 10})[0] == "error"
    assert dest.stat().st_size == 11 and fake.calls == []


def test_resume_not_supported_restarts(tmp_path, monkeypatch):
    calls = []

    def fake(url, out, resume=False, retry_all=True):
        calls.append(resume)
        if resume:
            return 200, 33, "range not supported"
        out.write_bytes(b"full")
        return 200, 0, ""

    monkeypatch.setattr(dp, "run_curl", fake)
    dest = tmp_path / "f.bin"
    dp.part_path(dest).write_bytes(b"fu")
    assert dp.download("u", dest, 4)[0] == "got"
    assert calls == [True, False] and dest.read_bytes() == b"full"


def write_geojson(path, feats):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"type": "FeatureCollection", "features": feats}), encoding="utf-8")


def test_run_reselect_write_manifest_then_idempotent(tmp_path, monkeypatch):
    data = tmp_path / "data"
    configs_csv = tmp_path / "configs" / "pilot_basins.csv"
    mpath = tmp_path / "manifest.json"
    monkeypatch.setattr(dp, "BASINS_CSV", configs_csv)
    monkeypatch.setattr(dp, "MANIFEST", mpath)
    monkeypatch.setattr(dp, "FILES", {"pilot/etopo/x.nc": "https://example.invalid/x.nc"})

    # 지역 6개, 유역마다 다른 1°칸. 하나(S0005)는 바다 칸이라 타일이 없다고(404) 칩니다.
    feats = [
        feature(f"S{i:04d}", f"R{i}", ebe=5.0 * (i + 1), lon=10.2 + i, lat=20.2) for i in range(6)
    ]
    write_geojson(data / dp.octopus_path(dp.OCTOPUS_LAYERS[0]), feats[:4])
    write_geojson(data / dp.octopus_path(dp.OCTOPUS_LAYERS[1]), feats[4:])
    ocean = dp.tile_name(20, 15)
    table = {"https://example.invalid/x.nc": (200, 0, b"n" * 40)}
    for i in range(6):
        t = dp.tile_name(20, 10 + i)
        table[dp.glo90_url(t)] = (404, 22, b"") if t == ocean else (200, 0, t.encode())
    fake = FakeCurl(table)
    monkeypatch.setattr(dp, "run_curl", fake)

    args = dp.parse_args(["--reselect", "--n-basins", "6", "--write-manifest"])
    assert dp.run(args, data, dp.load_manifest(mpath)) == 0
    assert all(not retry_all for url, _, retry_all in fake.calls if "example" not in url)

    rows = dp.read_basins(configs_csv)
    assert len(rows) == 6
    assert b"\r" not in configs_csv.read_bytes()
    copy = data / dp.BASINS_COPY
    assert b"\r\n" in copy.read_bytes() and dp.read_basins(copy) == rows

    manifest = dp.load_manifest(mpath)
    assert manifest["glo90_no_tile"] == [ocean]
    by_path = {e["path"]: e for e in manifest["files"]}
    assert dp.glo90_path(ocean) not in by_path
    assert len([p for p in by_path if p.startswith("pilot/glo90/")]) == 5
    for rel, e in by_path.items():
        assert e["sha256"] == sha((data / rel).read_bytes())
        assert e["bytes"] == (data / rel).stat().st_size
    assert by_path["pilot/etopo/x.nc"]["source_url"] == "https://example.invalid/x.nc"
    assert dp.verify(manifest, data, configs_csv, check_hash=True) == 0

    # 두 번째 실행: 다 있으므로 아무것도 받지 않습니다
    monkeypatch.setattr(dp, "run_curl", FakeCurl({}))
    before = {p: p.stat().st_mtime_ns for p in data.rglob("*") if p.is_file()}
    assert dp.run(dp.parse_args([]), data, dp.load_manifest(mpath)) == 0
    assert before == {p: p.stat().st_mtime_ns for p in data.rglob("*") if p.is_file()}


def test_run_reports_failure_exit_code(tmp_path, monkeypatch):
    data = tmp_path / "data"
    configs_csv = tmp_path / "pilot_basins.csv"
    monkeypatch.setattr(dp, "BASINS_CSV", configs_csv)
    monkeypatch.setattr(dp, "FILES", {})
    for layer in dp.OCTOPUS_LAYERS:
        write_geojson(data / dp.octopus_path(layer), [])
    dp.write_basins(configs_csv, [dp.basin_row(feature("S1", "R1", 10.0))])
    tile = dp.tile_name(20, 10)
    monkeypatch.setattr(dp, "run_curl", FakeCurl({dp.glo90_url(tile): (403, 22, b"")}))
    assert dp.run(dp.parse_args([]), data, {"version": 1, "files": []}) == 1
    assert not (data / dp.glo90_path(tile)).exists()


def test_fetch_octopus_pages_sorted_and_deduped(tmp_path, monkeypatch):
    feats = [feature(f"S{i:03d}", f"R{i}", 1.0 + i) for i in range(5)]
    pages = {0: feats[:2], 2: feats[1:3], 4: [feats[4]]}  # S001 이 두 쪽에 겹칩니다
    urls = []

    def fake_fetch(url):
        urls.append(url)
        start = int(url.rsplit("startIndex=", 1)[1])
        return json.dumps({"features": pages[start]}).encode()

    monkeypatch.setattr(dp, "fetch_text", fake_fetch)
    dest = tmp_path / "o.geojson"
    got = dp.fetch_octopus(dp.OCTOPUS_LAYERS[0], dest, page=2)
    assert [f["properties"]["OBSID1"] for f in got] == ["S000", "S001", "S002", "S004"]
    assert all("sortBy=OBSID1" in u for u in urls)
    assert dp.load_octopus(dest) == got and not dp.part_path(dest).exists()


def test_fetch_octopus_rejects_non_json(tmp_path, monkeypatch):
    monkeypatch.setattr(dp, "fetch_text", lambda url: b"<ows:ExceptionReport/>")
    dest = tmp_path / "o.geojson"
    with pytest.raises(RuntimeError, match="GeoJSON"):
        dp.fetch_octopus(dp.OCTOPUS_LAYERS[0], dest)
    assert not dest.exists()


def test_reselect_aborts_when_wfs_fails(tmp_path, monkeypatch):
    configs_csv = tmp_path / "configs" / "pilot_basins.csv"
    monkeypatch.setattr(dp, "BASINS_CSV", configs_csv)
    monkeypatch.setattr(dp, "FILES", {})

    def broken(url):
        raise RuntimeError("WFS 요청 실패 (가짜)")

    monkeypatch.setattr(dp, "fetch_text", broken)
    args = dp.parse_args(["--reselect"])
    assert dp.run(args, tmp_path / "data", {"version": 1, "files": []}) == 1
    assert not configs_csv.exists()
