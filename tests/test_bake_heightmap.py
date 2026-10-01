"""높이맵 굽기(bpcg.bake.heightmap) 검사: 왕복, 설명 필드, 바이트 순서, 잘못된 입력 거부."""

import json
import struct

import numpy as np
import pytest

import bpcg
from bpcg.bake.heightmap import AXES, FORMAT, read_heightmap, stem_paths, write_heightmap


def _sample(rows: int = 5, cols: int = 7) -> np.ndarray:
    rng = np.random.default_rng(20261002)
    return rng.normal(100.0, 50.0, size=(rows, cols))


def test_roundtrip_keeps_values_and_shape(tmp_path):
    z = _sample()
    meta = write_heightmap(tmp_path / "hm", z, spacing_m=25.0, origin=(-100.0, 0.0, 50.0))
    z2, meta2 = read_heightmap(tmp_path / "hm")
    assert z2.shape == z.shape
    assert z2.dtype == np.float32
    np.testing.assert_array_equal(z2, z.astype(np.float32))
    assert meta2 == meta


def test_json_fields(tmp_path):
    z = _sample(rows=4, cols=6)
    write_heightmap(tmp_path / "hm", z, spacing_m=10.0, origin=(1.0, 2.0, 3.0))
    meta = json.loads((tmp_path / "hm.json").read_text(encoding="utf-8"))
    assert meta["format"] == FORMAT == "float32_le"
    assert meta["axes"] == AXES
    assert meta["width"] == 6 and isinstance(meta["width"], int)
    assert meta["height"] == 4 and isinstance(meta["height"], int)
    assert meta["spacing_m"] == 10.0
    assert meta["origin"] == [1.0, 2.0, 3.0]
    z32 = z.astype(np.float32)
    assert meta["min"] == float(z32.min())
    assert meta["max"] == float(z32.max())
    assert meta["bpcg_version"] == bpcg.__version__


def test_bin_is_float32_little_endian_row_major(tmp_path):
    z = np.zeros((3, 4))
    z[0, 1] = 1.5  # 0번 행(북쪽), 1번 열: 두 번째 표본
    z[1, 0] = -2.25  # 1번 행 첫 표본: width 개 뒤
    write_heightmap(tmp_path / "hm", z, spacing_m=1.0)
    raw = (tmp_path / "hm.bin").read_bytes()
    assert len(raw) == 3 * 4 * 4
    assert raw == np.asarray(z, dtype="<f4").tobytes()
    assert raw[4:8] == struct.pack("<f", 1.5)
    assert raw[4 * 4 : 4 * 5] == struct.pack("<f", -2.25)


@pytest.mark.parametrize("bad", [np.nan, np.inf, -np.inf, 1e39])
def test_rejects_non_finite(tmp_path, bad):
    z = _sample()
    z[2, 3] = bad
    with pytest.raises(ValueError, match="NaN"):
        write_heightmap(tmp_path / "hm", z, spacing_m=10.0)
    assert not any(tmp_path.iterdir())


@pytest.mark.parametrize(
    ("z", "spacing", "origin"),
    [
        (np.zeros(5), 10.0, (0, 0, 0)),  # 1차원
        (np.zeros((1, 5)), 10.0, (0, 0, 0)),  # 한 변이 1
        (np.zeros((3, 3), dtype=bool), 10.0, (0, 0, 0)),  # 숫자가 아님
        (np.zeros((3, 3)), 0.0, (0, 0, 0)),  # 간격 0
        (np.zeros((3, 3)), float("nan"), (0, 0, 0)),
        (np.zeros((3, 3)), 10.0, (0, 0)),  # 원점 성분 2개
        (np.zeros((3, 3)), 10.0, (0, float("inf"), 0)),
    ],
)
def test_rejects_bad_input(tmp_path, z, spacing, origin):
    with pytest.raises(ValueError):
        write_heightmap(tmp_path / "hm", z, spacing_m=spacing, origin=origin)


def test_stem_with_dot_and_missing_parent(tmp_path):
    stem = tmp_path / "baked" / "tile.v2"
    write_heightmap(stem, np.ones((2, 2)), spacing_m=1.0)
    assert stem_paths(stem) == (
        tmp_path / "baked" / "tile.v2.bin",
        tmp_path / "baked" / "tile.v2.json",
    )
    assert (tmp_path / "baked" / "tile.v2.bin").exists()
    assert not list((tmp_path / "baked").glob("*.part"))


def test_read_rejects_size_mismatch(tmp_path):
    write_heightmap(tmp_path / "hm", np.ones((3, 3)), spacing_m=1.0)
    (tmp_path / "hm.bin").write_bytes(b"\x00" * 8)
    with pytest.raises(ValueError, match="표본 수"):
        read_heightmap(tmp_path / "hm")
