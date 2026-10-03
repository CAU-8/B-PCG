"""프랙탈 디테일 검사 (engine/README.md '프랙탈 디테일').

굽기가 쓴 heightmap_detail·cave_mouth_detail 을 봅니다.

디테일은 기본 회랑 지표에 50 m 아래 거칠기를 더한 보기용 지표입니다. 같은 격자에 쓰고, 물가·회랑
가장자리에는 넣지 않으며, detail.fractal_gain = 0 이면 파일을 쓰지 않습니다. 잡음 스펙트럼 같은
함수 단위 성질은 C# Bake/Detail.cs 가 기준입니다.
"""

import numpy as np
import pytest
from bundles import read_json

WATER_NONE_M = -10_000.0


def _grid(corridor, stem):
    meta = read_json(corridor / f"{stem}.json")
    return meta, np.fromfile(corridor / f"{stem}.bin", dtype="<f4").reshape(
        meta["height"], meta["width"]
    )


@pytest.fixture(scope="module")
def corridor(cave_run):
    return cave_run / "corridor"


def test_detail_files_on_same_grid(corridor):
    base_meta, base = _grid(corridor, "heightmap")
    det_meta, det = _grid(corridor, "heightmap_detail")
    for k in ("width", "height", "spacing_m", "origin"):
        assert det_meta[k] == base_meta[k], k
    man = read_json(corridor / "manifest.json")
    assert "detail" in man["file_meta"]["heightmap_detail"]  # 디테일 설정·통계
    diff = det.astype(np.float64) - base
    assert np.abs(diff).max() > 0.5  # 실제로 거칠기를 더함
    mouth_meta, _ = _grid(corridor, "cave_mouth_detail")
    assert mouth_meta["width"] == base_meta["width"]


def test_detail_spares_water_and_edges(corridor):
    _, base = _grid(corridor, "heightmap")
    _, det = _grid(corridor, "heightmap_detail")
    _, water = _grid(corridor, "water")
    wet = water > WATER_NONE_M + 1
    assert wet.any()
    assert np.array_equal(det[wet], base[wet])  # 물 칸은 그대로
    edge = np.zeros_like(wet)
    edge[0, :] = edge[-1, :] = edge[:, 0] = edge[:, -1] = True
    assert np.array_equal(det[edge], base[edge])  # 회랑 가장자리는 그대로


def test_without_detail_writes_no_detail_files(no_detail_run):
    man = read_json(no_detail_run / "manifest.json")
    for stem in ("heightmap_detail", "cave_mouth_detail"):
        assert f"{stem}.bin" not in man["files"]
        assert stem not in man["file_meta"]
        assert not (no_detail_run / f"{stem}.bin").exists()
    assert "heightmap.bin" in man["files"]
