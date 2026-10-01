"""AfriSAR TerrainHeight(레이더 좌표, SRTM 에서 옴)를 정규 미터 격자로 지오코딩합니다.

입력: data/cache/afrisar/data/<site>-tomo-fourier-hv.h5 (없으면 extract.py 로 zip 에서 꺼냄)
출력: data/derived/afrisar/<site>_dem_30m.npz
    z [m] (float32, 띠 밖은 NaN), x0, y0 [m] 격자 원점(지역 등거리 좌표), dx [m], lat0, lon0 [도]
실행: uv run python analysis/afrisar/geocode.py lope rabi
"""

import sys

import h5py
import numpy as np
from extract import tomo_file
from scipy.interpolate import LinearNDInterpolator
from scipy.spatial import Delaunay

from bpcg.core.paths import DERIVED

OUT_DIR = DERIVED / "afrisar"
R_E = 6378137.0  # [m] WGS84 적도 반지름


def geocode(site, dx=30.0, stride=2):
    """레이더 격자를 stride 로 솎은 뒤 Delaunay 선형 보간으로 dx [m] 격자에 옮깁니다."""
    with h5py.File(tomo_file(site, "fourier", "hv"), "r") as h:
        T = h["TerrainHeight"][::stride, ::stride].astype(np.float64)
        lat = h["Latitude"][::stride, ::stride].astype(np.float64)
        lon = h["Longitude"][::stride, ::stride].astype(np.float64)
    lat0, lon0 = lat.mean(), lon.mean()
    x = np.deg2rad(lon - lon0) * R_E * np.cos(np.deg2rad(lat0))
    y = np.deg2rad(lat - lat0) * R_E
    # 원래 격자 간격 확인
    dxa = np.hypot(np.diff(x, axis=0), np.diff(y, axis=0))
    dxr = np.hypot(np.diff(x, axis=1), np.diff(y, axis=1))
    print(
        f"[{site}] radar-grid ground spacing (stride {stride}): azimuth {np.median(dxa):.1f} m, "
        f"range {np.median(dxr):.1f} m "
        f"(near {np.median(dxr[:, :20]):.1f} / far {np.median(dxr[:, -20:]):.1f})"
    )
    pts = np.c_[x.ravel(), y.ravel()]
    tri = Delaunay(pts)
    f = LinearNDInterpolator(tri, T.ravel())
    xs = np.arange(x.min(), x.max(), dx)
    ys = np.arange(y.min(), y.max(), dx)
    X, Y = np.meshgrid(xs, ys)
    Z = f(X, Y)
    print(
        f"[{site}] geocoded grid {Z.shape} @ {dx} m, valid {np.isfinite(Z).mean():.1%}, "
        f"extent {(xs[-1] - xs[0]) / 1e3:.1f} x {(ys[-1] - ys[0]) / 1e3:.1f} km, "
        f"swath area {np.isfinite(Z).sum() * dx * dx / 1e6:.0f} km2"
    )
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(
        OUT_DIR / f"{site}_dem_{int(dx)}m.npz",
        z=Z.astype(np.float32),
        x0=xs[0],
        y0=ys[0],
        dx=dx,
        lat0=lat0,
        lon0=lon0,
    )
    return Z


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("사용법: geocode.py <site> [<site> ...]  (site = lope 또는 rabi)")
    for s in sys.argv[1:]:
        geocode(s)
