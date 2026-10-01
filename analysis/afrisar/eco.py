"""로페 수관 높이(TomoSAR)를 30 m 격자로 옮겨 HAND·경사·고도와의 관계를 봅니다.

HAND(가장 가까운 하천 위 높이)는 A >= 1e6 m^2 를 하천으로 보고 D8 흐름을 따라 내려가 잽니다.

입력: data/derived/afrisar/lope_ground_canopy.npz (ground_canopy.py), lope_dem_30m.npz (geocode.py),
     data/cache/afrisar/data/lope-tomo-fourier-hv.h5 (위경도만 읽음)
출력: data/derived/afrisar/lope_eco_30m.npz (CH 수관 높이 [m], HAND [m], A 상류 면적 [m^2])
실행: uv run python analysis/afrisar/eco.py
"""

import h5py
import numpy as np
from extract import tomo_file
from hydro import OUT_DIR, accumulate, d8, fill_eps
from scipy.interpolate import LinearNDInterpolator
from scipy.spatial import Delaunay
from scipy.stats import spearmanr

R_E = 6378137.0  # [m]
DX = 30.0  # [m]


def binned(c, v, edges, name):
    """v 구간별 수관 높이 c 의 중앙값과 키 큰 숲/낮은 식생 비율."""
    out = []
    for a, b in zip(edges[:-1], edges[1:], strict=True):
        s = (v >= a) & (v < b)
        if s.sum() > 500:
            out.append(
                f"{a:g}-{b:g}: med {np.median(c[s]):.1f} m, tall(>40m) {np.mean(c[s] > 40):.0%}, "
                f"short(<15m) {np.mean(c[s] < 15):.0%} (n={s.sum()})"
            )
    print(f"canopy height by {name}:")
    for o in out:
        print("   ", o)


def main():
    dx = DX
    gc = np.load(OUT_DIR / "lope_ground_canopy.npz")
    ch = gc["ch"][::2, ::2].astype(np.float64)
    with h5py.File(tomo_file("lope", "fourier", "hv"), "r") as h:
        lat = h["Latitude"][::2, ::2].astype(np.float64)
        lon = h["Longitude"][::2, ::2].astype(np.float64)
    dem = np.load(OUT_DIR / "lope_dem_30m.npz")
    z = dem["z"].astype(np.float64)
    x = np.deg2rad(lon - float(dem["lon0"])) * R_E * np.cos(np.deg2rad(float(dem["lat0"])))
    y = np.deg2rad(lat - float(dem["lat0"])) * R_E
    ny, nx = z.shape
    xs = float(dem["x0"]) + dx * np.arange(nx)
    ys = float(dem["y0"]) + dx * np.arange(ny)
    X, Y = np.meshgrid(xs, ys)
    m = np.isfinite(ch)
    CH = LinearNDInterpolator(Delaunay(np.c_[x[m], y[m]]), ch[m])(X, Y)
    valid = np.isfinite(z) & np.isfinite(CH)
    zz = np.where(np.isfinite(z), z, 0.0)
    zt = fill_eps(zz, np.isfinite(z), 1e-3)
    rec, dist = d8(zt, np.isfinite(z), dx)
    rf = rec.ravel()
    vf = np.isfinite(z).ravel()
    idx = np.nonzero(vf)[0]
    up = idx[np.argsort(-zt.ravel()[idx], kind="stable")]
    A = accumulate(up, rf, np.where(vf, dx * dx, 0.0))
    chan = A >= 1e6
    base = np.full(ny * nx, np.nan)
    zf = zz.ravel()
    for c in up[::-1]:  # 하류부터
        r = rf[c]
        base[c] = zf[c] if (chan[c] or r < 0) else base[r]
    HAND = (zf - base).reshape(ny, nx)
    gy, gx = np.gradient(np.where(np.isfinite(z), z, np.nan), dx)
    SL = np.hypot(gx, gy)
    ok = valid & np.isfinite(HAND) & np.isfinite(SL)
    c = CH[ok]
    hnd = HAND[ok]
    sl = SL[ok]
    el = z[ok]
    print(f"cells {ok.sum():,}; canopy height median {np.median(c):.1f} m")
    binned(c, hnd, [0, 2, 5, 10, 20, 40, 80, 200], "HAND [m]")
    binned(c, sl, [0, 0.05, 0.1, 0.2, 0.3, 0.5, 1.0], "slope")
    binned(c, el, [100, 150, 200, 250, 300, 400, 700], "elevation [m]")
    for name, v in (("HAND", hnd), ("slope", sl), ("elevation", el)):
        rho = spearmanr(v[::50], c[::50]).statistic
        print(f"Spearman rho(canopy, {name}) = {rho:+.3f}")
    np.savez_compressed(
        OUT_DIR / "lope_eco_30m.npz",
        CH=CH.astype(np.float32),
        HAND=HAND.astype(np.float32),
        A=A.reshape(ny, nx).astype(np.float32),
    )


if __name__ == "__main__":
    main()
