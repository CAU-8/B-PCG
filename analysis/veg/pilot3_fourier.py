"""파일럿 3 (두 사이트 공통, Fourier HV 만 사용): HV 전력·프로파일 상단(RH95 유사)·HAND 의 관계.

Capon 파일(1.4 GB) 없이 Fourier HV(0.38 GB)만으로 로페와 라비를 같은 방법으로 봅니다.

입력: data/derived/afrisar/<site>_dem_30m.npz, <site>_hydro.npz,
     data/cache/afrisar/data/<site>-tomo-fourier-hv.h5 (없으면 꺼냄)
출력: data/derived/veg/<site>_fourier_metrics.npz (HV [dB], HVd, TOP [m, SRTM 기준], HAND [m], SL; 레이더 격자)
실행: uv run python analysis/veg/pilot3_fourier.py lope   (그리고 rabi)
"""

import sys

import h5py
import numba as nb
import numpy as np
from afrisar_link import AFRISAR_DIR, VEG_DIR, extract, hydro

R_E = 6378137.0  # [m]


@nb.njit(cache=True)
def hand_fast(order, ch, r, zf):
    """order 는 하류(낮은 곳)부터. 하천 셀(ch)까지 내려가 그 높이를 base 로 돌려줍니다."""
    base = np.full(zf.size, np.nan)
    for t in range(order.size):
        c = order[t]
        if ch[c]:
            base[c] = zf[c]
        elif r[c] >= 0:
            base[c] = base[r[c]]
    return base


def sp(a, b):
    """순위 상관 (Spearman)."""
    ra = np.argsort(np.argsort(a))
    rb = np.argsort(np.argsort(b))
    return np.corrcoef(ra, rb)[0, 1]


def main(site):
    dem = np.load(AFRISAR_DIR / f"{site}_dem_30m.npz")
    hy = np.load(AFRISAR_DIR / f"{site}_hydro.npz")
    z = dem["z"].astype(np.float64)
    dx = float(dem["dx"])
    valid = np.isfinite(z)
    ny, nx = z.shape
    zt = hy["zt"].astype(np.float64)
    A = hy["A"].astype(np.float64)
    rec, dist = hydro.d8(zt, valid, dx)
    zf = zt.ravel()
    idx = np.flatnonzero(valid.ravel())
    order = idx[np.argsort(zf[idx], kind="stable")]
    HANDg = (zf - hand_fast(order, A.ravel() >= 1e6, rec.ravel(), zf)).reshape(ny, nx)
    gy, gx = np.gradient(np.where(valid, z, np.nan), dx)
    SLg = np.hypot(gx, gy)
    with h5py.File(extract.tomo_file(site, "fourier", "hv"), "r") as h:
        H = h["Heights"][:]
        tm = np.maximum(h["Tomogram"][:], 0)
        lat = h["Latitude"][:].astype(np.float64)
        lon = h["Longitude"][:].astype(np.float64)
    pw = tm.sum(0)
    HV = 10 * np.log10(np.where(pw > 0, pw, np.nan))
    c = np.cumsum(tm, 0) / np.where(pw > 0, pw, 1)[None]
    TOP = H[np.argmax(c >= 0.95, axis=0)].astype(np.float32)  # RH95 유사 (SRTM 기준)
    TOP[~(pw > 0)] = np.nan
    x = np.deg2rad(lon - float(dem["lon0"])) * R_E * np.cos(np.deg2rad(float(dem["lat0"])))
    y = np.deg2rad(lat - float(dem["lat0"])) * R_E
    J = np.clip(np.rint((x - float(dem["x0"])) / dx).astype(int), 0, nx - 1)
    I = np.clip(np.rint((y - float(dem["y0"])) / dx).astype(int), 0, ny - 1)
    HAND = HANDg[I, J]
    SL = SLg[I, J]
    Z = z[I, J]
    ok = np.isfinite(HV) & np.isfinite(HAND) & np.isfinite(SL)

    def detrend(a):
        """레인지 열별 중앙값을 뺍니다 (쓸 화소가 없는 열은 NaN)."""
        med = np.array(
            [
                np.nanmedian(np.where(ok[:, k], a[:, k], np.nan)) if ok[:, k].any() else np.nan
                for k in range(a.shape[1])
            ]
        )
        return a - med[None, :]

    HVd = detrend(HV)
    TOPd = detrend(TOP)
    flat = ok & (SL < 0.05)
    forest = HVd > -3
    edges = [0, 1, 3, 6, 10, 20, 40, 80, 600]
    print(
        f"[{site}] ok {ok.sum():,} px; flat {flat.sum():,}; HAND p50 {np.nanmedian(HAND[ok]):.1f} m"
    )
    print(
        " HAND bin | flat: n, HVd med, low-HV(<-6dB) frac, TOPd med | "
        "flat&forest: n, TOPd med, TOPd p90"
    )
    for a0, a1 in zip(edges[:-1], edges[1:], strict=True):
        m = flat & (HAND >= a0) & (HAND < a1)
        mf = m & forest
        if m.sum() < 500:
            continue
        print(
            f"  {a0:>3}-{a1:<3} | {m.sum():>8,} {np.median(HVd[m]):+5.2f} "
            f"{np.mean(HVd[m] < -6):.3f} {np.nanmedian(TOPd[m]):+5.1f} | "
            f"{mf.sum():>8,} {np.nanmedian(TOPd[mf]):+5.1f} {np.nanpercentile(TOPd[mf], 90):+5.1f}"
        )
    rng = np.random.default_rng(0)
    for name, msk in (("flat", flat), ("flat&forest", flat & forest)):
        ii = np.flatnonzero(msk & np.isfinite(TOPd))
        ii = rng.choice(ii, min(200000, ii.size), replace=False)
        print(
            f"  {name}: Spearman HVd~HAND {sp(HVd.ravel()[ii], HAND.ravel()[ii]):+.3f}, "
            f"TOPd~HAND {sp(TOPd.ravel()[ii], HAND.ravel()[ii]):+.3f}, "
            f"HVd~elev {sp(HVd.ravel()[ii], Z.ravel()[ii]):+.3f}"
        )
    VEG_DIR.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(
        VEG_DIR / f"{site}_fourier_metrics.npz",
        HV=HV.astype(np.float32),
        HVd=HVd.astype(np.float32),
        TOP=TOP,
        HAND=HAND.astype(np.float32),
        SL=SL.astype(np.float32),
    )


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("사용법: pilot3_fourier.py <site>  (site = lope 또는 rabi)")
    main(sys.argv[1])
