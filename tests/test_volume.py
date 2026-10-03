"""3D 샘플(HeroVolume)을 구운 재질 부피 검사 (docs/pipeline.md 10장): 회랑 strata.u8.

재질 부피는 HeroVolume 을 회랑 복셀마다 표본한 결과입니다. 동굴 빈 곳은 지하수면 아래면 물(254),
위면 공기(255)이고, 녹는 암석 둘레에만 있으며, 윗면은 회랑 지표를 따릅니다.
"""

import numpy as np
import pytest
from bundles import read_json

ROCK_IDS = set(range(12))
WATER_ID = 254
AIR_ID = 255
SOLUBLE = (3, 9)  # 석회암, 대리암


@pytest.fixture(params=["tiny_run", "cave_run"])
def volume(request):
    corridor = request.getfixturevalue(request.param) / "corridor"
    meta = read_json(corridor / "strata.json")
    rows, layers, cols = meta["shape"]
    vol = np.fromfile(corridor / "strata.u8", dtype=np.uint8).reshape(rows, layers, cols)
    top_meta = read_json(corridor / "strata_top.json")
    top = np.fromfile(corridor / "strata_top.bin", dtype="<f4").reshape(rows, cols)
    wt_meta = read_json(corridor / "water_table.json")
    wt = np.fromfile(corridor / "water_table.bin", dtype="<f4").reshape(
        wt_meta["height"], wt_meta["width"]
    )
    return meta, vol, top_meta, top.astype(np.float64), wt_meta, wt.astype(np.float64)


def _bilinear_to(grid: np.ndarray, step: int, rows: int, cols: int) -> np.ndarray:
    """간격이 step 배 큰 격자를 같은 원점의 고운 격자 (rows, cols) 로 쌍선형 보간."""
    rr = np.arange(rows) / step
    cc = np.arange(cols) / step
    r0 = np.minimum(np.floor(rr).astype(int), grid.shape[0] - 2)
    c0 = np.minimum(np.floor(cc).astype(int), grid.shape[1] - 2)
    tr = (rr - r0)[:, None]
    tc = cc - c0
    a = grid[np.ix_(r0, c0)] * (1 - tc) + grid[np.ix_(r0, c0 + 1)] * tc
    b = grid[np.ix_(r0 + 1, c0)] * (1 - tc) + grid[np.ix_(r0 + 1, c0 + 1)] * tc
    return a * (1 - tr) + b * tr


def test_values_are_rocks_water_or_air(volume):
    _, vol, *_ = volume
    assert set(np.unique(vol).tolist()) <= ROCK_IDS | {WATER_ID, AIR_ID}


def test_cave_void_is_flooded_below_water_table(volume):
    """빈 곳은 지하수면 아래 물, 위 공기.

    재질 부피와 회랑 지하수면은 각자 히어로 격자(수십~100 m)에서 따로 표본하므로 경계에서 조금
    어긋납니다. 모든 복셀이 세 층(3·dy) 안, 98 % 이상이 한 층(dy) 안이어야 합니다.
    """
    meta, vol, top_meta, top, wt_meta, wt = volume
    rows, layers, cols = vol.shape
    dy = meta["spacing_m"]["y"]
    oy = top_meta["origin"][1]
    step = round(wt_meta["spacing_m"] / top_meta["spacing_m"])
    wt_fine = _bilinear_to(wt, step, rows, cols)
    yc = (top + oy)[:, None, :] - (np.arange(layers)[None, :, None] + 0.5) * dy
    wt_y = np.broadcast_to((wt_fine + oy)[:, None, :], vol.shape)
    over = (yc - wt_y)[vol == WATER_ID]  # 물인데 지하수면보다 높은 만큼
    under = (wt_y - yc)[vol == AIR_ID]  # 공기인데 지하수면보다 낮은 만큼
    for gap in (over, under):
        if gap.size:
            assert (gap <= 3 * dy).all() and (gap <= dy).mean() >= 0.98


def test_cave_void_touches_soluble_rock(volume):
    _, vol, *_ = volume
    cave = (vol == WATER_ID) | (vol == AIR_ID)
    if not cave.any():
        pytest.skip("이 회랑에는 동굴 복셀이 없습니다")
    pad = np.pad(vol, 1, constant_values=0)
    r, layer_count, c = vol.shape
    ok = np.zeros_like(cave)
    for d in ((1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)):
        nb = pad[
            1 + d[0] : r + 1 + d[0], 1 + d[1] : layer_count + 1 + d[1], 1 + d[2] : c + 1 + d[2]
        ]
        ok |= np.isin(nb, (*SOLUBLE, WATER_ID, AIR_ID))
    assert ok[cave].all()


def test_cave_run_has_both_flooded_and_dry_voids(cave_run):
    meta = read_json(cave_run / "corridor" / "strata.json")
    assert int(meta["counts"].get(str(WATER_ID), 0)) > 0
    assert int(meta["counts"].get(str(AIR_ID), 0)) > 0
    assert meta["vertical"] == "surface_following"
