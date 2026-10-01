"""확인용 수직 단면 그림 (docs/pipeline.md 10장 slices.py, 가이드 7장 그림).

HeroVolume 을 두 점 p0 → p1 을 잇는 수직면에서 픽셀마다 계산해 재질 색 + 물 + 지하수면 선으로
칠합니다. 눈으로 구조(지층이 골짜기 벽에 드러남, 동굴이 녹는 암석 안 지하수면 근처, 잠긴 동굴)를
확인하는 용도입니다.
"""

import math
import os

import numpy as np

from bpcg.geology import rocks as rk
from bpcg.volume.sample import MATERIAL_EMPTY

AIR_RGB = (206, 226, 240)  # 하늘색 공기
WATER_RGB = (40, 110, 200)  # 물
CAVE_AIR_RGB = (24, 24, 28)  # 땅 속 빈 곳(마른 동굴)
WATER_TABLE_RGB = (20, 40, 160)  # 지하수면 선


def vertical_slice(
    volume,
    p0: tuple[float, float],
    p1: tuple[float, float],
    z_min: float,
    z_max: float,
    res_m: float,
    save_path: str | os.PathLike | None = None,
) -> np.ndarray:
    """p0 → p1 수직 단면의 RGB 그림 (H, W, 3) uint8.

    volume: volume.sample.HeroVolume. p0, p1: 국소 (동, 북) [m]. z_min, z_max: 높이 범위 [m].
    res_m: 픽셀 크기 [m] (수평·수직 같음). 열은 p0 에서 p1 쪽, 행 0 이 z_max(위)입니다.
    색: 땅은 재질 색(geology.rocks.COLOR_RGB), 지상 공기는 하늘색, 땅 속 빈 곳은 검정, 물은 파랑,
    지하수면 z_gw 는 진한 파란 선입니다.
    save_path 를 주면 matplotlib(Agg 캔버스)로 축과 눈금을 붙인 PNG 를 씁니다.
    """
    a = np.asarray(p0, dtype=np.float64)
    b = np.asarray(p1, dtype=np.float64)
    if a.shape != (2,) or b.shape != (2,) or not (np.isfinite(a).all() and np.isfinite(b).all()):
        raise ValueError("p0, p1 은 유한한 (동, 북) 두 값이어야 합니다")
    length = float(np.linalg.norm(b - a))
    if not length > 0:
        raise ValueError("p0 과 p1 이 같은 점입니다")
    if not (math.isfinite(res_m) and res_m > 0):
        raise ValueError(f"res_m 은 0 보다 커야 합니다: {res_m}")
    if not (math.isfinite(z_min) and math.isfinite(z_max) and z_max > z_min):
        raise ValueError(f"z_max 는 z_min 보다 커야 합니다: {z_min}, {z_max}")
    n_w = max(int(math.ceil(length / res_m)) + 1, 2)
    n_h = max(int(math.ceil((z_max - z_min) / res_m)) + 1, 2)
    if n_w * n_h > 50_000_000:
        raise ValueError(f"단면이 너무 큽니다 ({n_w}×{n_h} 픽셀). res_m 을 키우세요")
    s = np.linspace(0.0, 1.0, n_w)
    x = a[0] + s * (b[0] - a[0])
    y = a[1] + s * (b[1] - a[1])
    zz = np.linspace(z_max, z_min, n_h)
    up = np.broadcast_to(zz[None, :], (n_w, n_h))
    ev = volume.evaluate_grid(x, y, up, keys=("d", "material", "water", "d1"))
    mat = ev["material"].T  # (H, W)
    water = ev["water"].T
    under = (ev["d1"] < 0).T
    empty = mat == MATERIAL_EMPTY
    rgb = np.empty((n_h, n_w, 3), dtype=np.uint8)
    colors = np.asarray(rk.COLOR_RGB, dtype=np.uint8)
    rgb[~empty] = colors[np.minimum(mat[~empty], rk.N_ROCKS - 1)]
    rgb[empty & ~under] = AIR_RGB
    rgb[empty & under] = CAVE_AIR_RGB
    rgb[water] = WATER_RGB
    z_gw = volume.water_table(x, y)
    row = np.round((z_max - z_gw) / (z_max - z_min) * (n_h - 1)).astype(np.int64)
    ok = (row >= 0) & (row < n_h)
    cols = np.arange(n_w)[ok]
    rgb[row[ok], cols] = WATER_TABLE_RGB
    if save_path is not None:
        _save_png(rgb, length, z_min, z_max, save_path)
    return rgb


def _save_png(rgb: np.ndarray, length: float, z_min: float, z_max: float, path) -> None:
    """축(거리, 고도)을 붙여 PNG 로 저장합니다. 전역 pyplot 상태를 쓰지 않습니다."""
    from matplotlib.backends.backend_agg import FigureCanvasAgg
    from matplotlib.figure import Figure

    h, w = rgb.shape[:2]
    aspect = h / max(w, 1)
    fig = Figure(figsize=(10.0, max(2.0, min(10.0, 10.0 * aspect)) + 0.8), dpi=150)
    FigureCanvasAgg(fig)
    ax = fig.add_subplot(1, 1, 1)
    ax.imshow(rgb, extent=(0.0, length, z_min, z_max), aspect="auto", interpolation="nearest")
    ax.set_xlabel("distance [m]")  # 한글 글꼴이 없는 기기에서도 깨지지 않게
    ax.set_ylabel("elevation [m]")
    fig.tight_layout()
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    fig.savefig(path)
