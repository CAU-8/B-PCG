"""행성 면 텍스처 PNG (docs/pipeline.md 11장 textures.py, 궤도 장면용).

L0 필드를 큐브 면 6장으로 나눠 층마다 PNG 를 씁니다.

| 층 | 필드 | 색 |
|---|---|---|
| elevation | z_mean_m (없으면 z_m) | 바다는 파랑(깊을수록 어둡게), 육지는 초록 → 갈색 → 흰색 |
| plate | plate_id | 판마다 다른 색 (tab20) |
| ocean_age | ocean_age_myr | viridis, 대륙(NaN)은 회색 |
| precip | precip_m_per_yr | YlGnBu |

그림 방향: 열 i 가 면의 u 방향(오른쪽), 행 j 가 v 방향(아래에서 위)입니다. 곧 PNG 맨 아래 줄이
j = 0 입니다(텍스처 UV 의 (u, v) 와 같은 방향). 면 기저(FACE_U, FACE_V, FACE_N)는 textures.json 에
적습니다. matplotlib 의 색표와 PNG 쓰기만 쓰고 pyplot 전역 상태는 쓰지 않습니다(Agg 와 같음).
"""

import os
from pathlib import Path

import numpy as np
from matplotlib import colormaps
from matplotlib import image as mpimg
from matplotlib.colors import LinearSegmentedColormap

from bpcg.bake.bundle import write_json
from bpcg.core import cubesphere as cs

LAYERS = ("elevation", "plate", "ocean_age", "precip")
NAN_RGBA = (0.55, 0.55, 0.55, 1.0)  # 값이 없는 칸(대륙의 해양저 나이 등)
ELEVATION_PERCENTILE = 99.0  # 고도 색 범위: 바다 깊이·육지 높이의 이 백분위까지 (튀는 값 무시)

_OCEAN = LinearSegmentedColormap.from_list(
    "bpcg_ocean", [(0.02, 0.06, 0.22), (0.10, 0.30, 0.60), (0.55, 0.78, 0.92)]
)
_LAND = LinearSegmentedColormap.from_list(
    "bpcg_land",
    [(0.20, 0.45, 0.20), (0.55, 0.62, 0.30), (0.55, 0.42, 0.28), (0.95, 0.95, 0.95)],
)


def elevation_rgba(z: np.ndarray, depth_max: float, height_max: float) -> np.ndarray:
    """고도 [m] → RGBA (…, 4) float. z < 0 은 바다 색표, z ≥ 0 은 육지 색표."""
    z = np.asarray(z, dtype=np.float64)
    out = np.empty(z.shape + (4,))
    sea = z < 0
    out[sea] = _OCEAN(np.clip(1.0 + z[sea] / max(depth_max, 1.0), 0.0, 1.0))
    out[~sea] = _LAND(np.clip(z[~sea] / max(height_max, 1.0), 0.0, 1.0))
    out[~np.isfinite(z)] = NAN_RGBA
    return out


def _scalar_rgba(v: np.ndarray, cmap: str, lo: float, hi: float) -> np.ndarray:
    v = np.asarray(v, dtype=np.float64)
    t = np.clip((v - lo) / (hi - lo if hi > lo else 1.0), 0.0, 1.0)
    out = colormaps[cmap](np.nan_to_num(t))
    out[~np.isfinite(v)] = NAN_RGBA
    return out


def face_textures(planet_state, out_dir: str | os.PathLike) -> dict:
    """면 6개 × 층 4개 PNG 와 textures.json 을 out_dir 에 씁니다 (pipeline.md 11장).

    planet_state: pipeline.PlanetState (구면 그래프 (6, n, n), fields). out_dir: 출력 폴더.
    반환: textures.json 내용 dict (층별 파일 목록, 값 범위, 그림 방향, 면 기저).
    파일 이름은 '<층>_<면 번호>.png' 입니다. 필드가 없는 층은 건너뛰고 skipped 에 적습니다.
    """
    graph = getattr(planet_state, "graph", None)
    fields = getattr(planet_state, "fields", None)
    if graph is None or graph.kind != "sphere" or not isinstance(fields, dict):
        raise ValueError("planet_state 는 구면 그래프와 fields 를 가진 PlanetState 여야 합니다")
    _, n, _ = graph.shape
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)

    def faces(name: str) -> np.ndarray:
        return np.asarray(fields[name], dtype=np.float64).reshape(6, n, n)

    layers: dict[str, dict] = {}
    skipped: list[str] = []
    rgba_by_layer: dict[str, np.ndarray] = {}
    z_name = "z_mean_m" if "z_mean_m" in fields else ("z_m" if "z_m" in fields else None)
    if z_name is not None:
        z = faces(z_name)
        sea, land = z[z < 0], z[z >= 0]
        depth_max = float(np.percentile(-sea, ELEVATION_PERCENTILE)) if sea.size else 1.0
        height_max = float(np.percentile(land, ELEVATION_PERCENTILE)) if land.size else 1.0
        rgba_by_layer["elevation"] = elevation_rgba(z, depth_max, height_max)
        layers["elevation"] = {
            "field": z_name,
            "depth_max_m": depth_max,
            "height_max_m": height_max,
        }
    else:
        skipped.append("elevation")
    if "plate_id" in fields:
        pid = faces("plate_id")
        rgba = colormaps["tab20"]((np.nan_to_num(pid).astype(np.int64) % 20) / 19.0)
        rgba_by_layer["plate"] = rgba
        layers["plate"] = {"field": "plate_id", "n_plates": int(np.nanmax(pid)) + 1}
    else:
        skipped.append("plate")
    if "ocean_age_myr" in fields:
        age = faces("ocean_age_myr")
        hi = float(np.nanmax(age)) if np.isfinite(age).any() else 1.0
        rgba_by_layer["ocean_age"] = _scalar_rgba(age, "viridis", 0.0, hi)
        layers["ocean_age"] = {"field": "ocean_age_myr", "min": 0.0, "max": hi}
    else:
        skipped.append("ocean_age")
    if "precip_m_per_yr" in fields:
        p = faces("precip_m_per_yr")
        hi = float(np.nanpercentile(p, ELEVATION_PERCENTILE))
        rgba_by_layer["precip"] = _scalar_rgba(p, "YlGnBu", 0.0, hi)
        layers["precip"] = {"field": "precip_m_per_yr", "min": 0.0, "max": hi}
    else:
        skipped.append("precip")

    for name, rgba in rgba_by_layer.items():
        files = []
        for f in range(6):
            fname = f"{name}_{f}.png"
            # origin='lower': 배열 [j, i] 의 j = 0 을 그림 맨 아래 줄로 (UV 의 v 가 위로)
            mpimg.imsave(out / fname, rgba[f], origin="lower")
            files.append(fname)
        layers[name]["files"] = files
    meta = {
        "layers": layers,
        "skipped": skipped,
        "n_per_face": int(n),
        "orientation": "column i = face u (right), row j = face v (bottom to top, origin lower)",
        "face_basis": {"u": cs.FACE_U.tolist(), "v": cs.FACE_V.tolist(), "n": cs.FACE_N.tolist()},
    }
    write_json(out / "textures.json", meta)
    return meta
