"""동굴 층과 입구 (docs/pipeline.md 8.4, 설계도 4장 사슬 3·5장 '동굴 층 높이').

강이 파 내려갈 때마다 그때의 지하수면 높이에 머문 물이 녹는 암석을 녹여 층층이 동굴을
남긴다고 봅니다. 정상상태 지형에는 하각의 역사가 없으므로, 층 k 의 높이를
z_k = z_gw + k·f_k·(골짜기 깊이) 로 둡니다(Audra & Palmer 2011 관찰, f_k 는 우리가 정한 값).
0층은 지금 지하수면이라 잠기고, 위층은 옛 지하수면이라 마릅니다. 동굴 층과 하천 단구는
'정상상태 아님(물려받은 지형)'입니다.

칸마다·층마다 판정 (r = passage_radius_m, τ = entrance_tolerance_m, 깊이 d = z − z_k)
- 녹음: z_k 에서 geology.model.rock_at 이 녹는 암석(석회암·대리암)이어야 합니다.
- 덮개(동굴 안): 녹음 그리고 d > 2r.
- 지표 띠: 녹음 그리고 |d| ≤ τ + r. 층이 땅 겉과 만나는 칸입니다.
- 입구 비트(층 k 는 1 << k): 동굴이 땅 밖과 이어지는 칸에 반드시 켭니다.
    1. 지표 띠 칸이 덮개 칸(자기 자신 또는 이웃)과 맞닿음 → 층이 비탈에서 서서히 땅 위로 나옴.
    2. 덮개 칸의 이웃 n 이 동굴도 지표 띠도 아니고 z_n ≤ z_k + τ + r → 층이 가파른 골짜기 벽과
       칸 사이에서 만남(25 m 칸에서 벽 경사 1.0 이면 이웃 사이 높이차가 띠 폭보다 큼).
- 층 값: 덮개 칸과 입구 칸에만 z_k, 그 밖은 NaN. 그래서 값이 있는 칸은 모두 녹는 암석이고,
  입구 칸이 아니면 땅 겉보다 2r 넘게 아래입니다. 땅 겉과 전혀 만나지 않는 닫힌 동굴 덩어리에는
  입구를 억지로 두지 않습니다.
"""

import numpy as np
from numba import njit, prange

from bpcg.core.fields import FIELDS
from bpcg.core.graph import CellGraph
from bpcg.geology import rocks as rk
from bpcg.geology.model import LayerColumns, rock_at
from bpcg.subsurface.water import _as_vector, _check_graph

MAX_LEVELS = 8  # cave_entrance 는 uint8 비트라 층은 8 개까지


def level_field_name(k: int) -> str:
    """층 k 의 FIELDS 이름 (cave_level_0_m, cave_level_1_m, ...)."""
    return f"cave_level_{k}_m"


@njit(cache=True, parallel=True)
def _entrance_kernel(
    nbr: np.ndarray,
    z: np.ndarray,
    zk: np.ndarray,
    soluble: np.ndarray,
    two_r: float,
    band: float,
    level_out: np.ndarray,
    entrance: np.ndarray,
) -> None:
    """층 값과 입구 비트를 채웁니다.

    nbr: (N, K). z: (N,) 지표 [m]. zk: (N, M) 층 높이 [m]. soluble: (N, M) bool, z_k 의 암석이 녹음.
    two_r: 2·passage_radius [m]. band: entrance_tolerance + passage_radius [m].
    level_out: (N, M) float64 (덮개·입구 칸만 z_k, 나머지 NaN). entrance: (N,) uint8 비트.
    """
    n = z.shape[0]
    m = zk.shape[1]
    n_slots = nbr.shape[1]
    for c in prange(n):
        bits = 0
        for k in range(m):
            level_out[c, k] = np.nan
            if not soluble[c, k]:
                continue
            d = z[c] - zk[c, k]
            covered = d > two_r
            in_band = abs(d) <= band
            is_entrance = False
            if in_band:
                if covered:
                    is_entrance = True
                else:
                    for s in range(n_slots):
                        v = nbr[c, s]
                        if v >= 0 and soluble[v, k] and z[v] - zk[v, k] > two_r:
                            is_entrance = True
                            break
            elif covered:
                lim = zk[c, k] + band
                for s in range(n_slots):
                    v = nbr[c, s]
                    if v < 0:
                        continue
                    dv = z[v] - zk[v, k]
                    in_cave = soluble[v, k] and (dv > two_r or abs(dv) <= band)
                    if not in_cave and z[v] <= lim:
                        is_entrance = True
                        break
            if is_entrance:
                bits |= 1 << k
            if covered or is_entrance:
                level_out[c, k] = zk[c, k]
        entrance[c] = bits


def cave_levels(
    graph: CellGraph,
    z: np.ndarray,
    z_gw: np.ndarray,
    valley_depth: np.ndarray,
    columns: LayerColumns,
    cfg,
) -> dict[str, np.ndarray]:
    """동굴 층 높이와 입구 비트를 정합니다 (pipeline.md 8.4).

    graph: CellGraph (이웃). z: (N,) 지표 고도 [m]. z_gw: (N,) 지하수면 [m] (water_table).
    valley_depth: (N,) 골짜기 깊이 [m] (water_bodies 의 valley_depth_m, NaN 이면 그 칸은 0층만).
    columns: geology.model.LayerColumns (같은 칸 수). cfg: caves 절(incision_fraction, levels,
    entrance_tolerance_m, passage_radius_m).

    층 k = 0..levels−1 의 높이는 z_k = z_gw + k·f_k·valley_depth 입니다. 판정은 모듈 설명을 보세요.
    반환: FIELDS 이름 dict. cave_level_<k>_m (N,) float64 [m] (동굴 없으면 NaN), cave_entrance (N,)
    uint8 (비트 1 << k 가 층 k 의 입구, 1 아래층, 2 위층).
    """
    n = _check_graph(graph)
    zz = _as_vector(z, n, "z")
    gw = _as_vector(z_gw, n, "z_gw")
    vd = np.asarray(valley_depth, dtype=np.float64)
    if vd.shape != (n,):
        raise ValueError(f"valley_depth 는 ({n},) 배열이어야 합니다 (받은 모양: {vd.shape})")
    if np.isinf(vd).any() or (vd < 0.0).any():
        raise ValueError("valley_depth 는 0 이상이어야 합니다 (강이 없는 칸만 NaN)")
    if not isinstance(columns, LayerColumns):
        raise ValueError(f"columns 는 LayerColumns 여야 합니다: {type(columns).__name__}")
    if columns.n_cells != n:
        raise ValueError(f"columns 의 칸 수 {columns.n_cells} 가 그래프 칸 수 {n} 과 다릅니다")
    cv = cfg.caves
    levels = int(cv.levels)
    f_k = float(cv.incision_fraction)
    r = float(cv.passage_radius_m)
    tol = float(cv.entrance_tolerance_m)
    if not (1 <= levels <= MAX_LEVELS):
        raise ValueError(f"caves.levels 는 1..{MAX_LEVELS} 이어야 합니다: {levels}")
    if not (f_k >= 0.0 and r > 0.0 and tol >= 0.0):
        raise ValueError(
            "caves 설정은 incision_fraction ≥ 0, passage_radius_m > 0, entrance_tolerance_m ≥ 0 "
            f"이어야 합니다: {f_k}, {r}, {tol}"
        )
    names = [level_field_name(k) for k in range(levels)]
    missing = [nm for nm in names if nm not in FIELDS]
    if missing:
        raise ValueError(
            f"FIELDS 에 없는 동굴 층 이름: {missing} (bpcg/core/fields.py 에 먼저 적으세요)"
        )

    zk = np.empty((n, levels), dtype=np.float64)
    zk[:, 0] = gw  # 0층은 지금 지하수면 (골짜기 깊이가 없어도 정해짐)
    for k in range(1, levels):
        zk[:, k] = gw + k * f_k * vd
    rock = rock_at(columns.bottom, columns.rock, zk)
    soluble = np.ascontiguousarray(rk.SOLUBLE[rock] & np.isfinite(zk))

    level_out = np.empty((n, levels), dtype=np.float64)
    entrance = np.empty(n, dtype=np.uint8)
    _entrance_kernel(
        np.ascontiguousarray(graph.nbr), zz, zk, soluble, 2.0 * r, tol + r, level_out, entrance
    )
    out: dict[str, np.ndarray] = {nm: level_out[:, k].copy() for k, nm in enumerate(names)}
    out["cave_entrance"] = entrance
    return out
