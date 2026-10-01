"""파일럿 1: 로페에서 TomoSAR 수관 높이/지면 오프셋을 30 m 격자로 옮기고 HAND·경사·TWI 와의 관계를 봅니다.

덤으로, 토모 지면 오프셋(5 x 5 평활)으로 SRTM DEM 을 보정한 DTM 을 만들어 저장합니다
(그 DTM 으로 hydro.py, chi.py 를 다시 돌리면 θ 가 바뀌는지 볼 수 있음).

입력: data/derived/afrisar/lope_dem_30m.npz, lope_hydro.npz, lope_ground_canopy.npz,
     data/cache/afrisar/data/lope-tomo-capon-hh.h5 (위경도만 읽음, 약 1.4 GB, 없으면 꺼냄)
출력: data/derived/veg/lope_veg_pilot.npz (CH, G, HAND, slope, twi, DCH; 30 m 격자)
     data/derived/afrisar/lope_tomo_dtm_dem_30m.npz (보정 DTM, 예전 이름 lopeTomoDTM_dem_30m.npz)
실행: uv run python analysis/veg/pilot_lope.py
"""

import h5py
import numba as nb
import numpy as np
from afrisar_link import AFRISAR_DIR, VEG_DIR, extract, hydro
from scipy import ndimage

R_E = 6378137.0  # [m]


@nb.njit(cache=True)
def hand_fast(order, ch, r, zf, dl):
    """order 는 하류(낮은 곳)부터. 하천 셀(ch)까지 내려가 그 높이(base)와 흐름 거리(dch [m])를 셉니다."""
    base = np.full(zf.size, np.nan)
    dch = np.full(zf.size, np.nan)
    for t in range(order.size):
        c = order[t]
        if ch[c]:
            base[c] = zf[c]
            dch[c] = 0.0
        elif r[c] >= 0:
            base[c] = base[r[c]]
            dch[c] = dch[r[c]] + dl[c]
    return base, dch


def hand_map(zt, valid, A, Ac, dx):
    """A >= Ac [m^2] 를 하천으로 보고 HAND [m] 와 하천까지의 흐름 거리 [m] 를 구합니다."""
    ny, nx = zt.shape
    rec, dist = hydro.d8(zt, valid, dx)
    r = rec.ravel()
    zf = zt.ravel()
    v = valid.ravel()
    idx = np.nonzero(v)[0]
    order = idx[np.argsort(zf[idx], kind="stable")]
    base, dch = hand_fast(order, A.ravel() >= Ac, r, zf, dist.ravel())
    return (zf - base).reshape(ny, nx), dch.reshape(ny, nx)


def q(a):
    f = a[np.isfinite(a)]
    return " ".join(f"p{p}:{np.percentile(f, p):.1f}" for p in (5, 25, 50, 75, 95))


def spearman(a, b):
    ra = np.argsort(np.argsort(a))
    rb = np.argsort(np.argsort(b))
    return np.corrcoef(ra, rb)[0, 1]


def block(a, k=3):
    """k x k 블록 평균 (공간 자기상관 완화용, 30 m x 3 = 90 m)."""
    ny, nx = a.shape
    yy, xx = (ny // k) * k, (nx // k) * k
    return np.nanmean(a[:yy, :xx].reshape(ny // k, k, nx // k, k), axis=(1, 3))


def main():
    dem = np.load(AFRISAR_DIR / "lope_dem_30m.npz")
    hy = np.load(AFRISAR_DIR / "lope_hydro.npz")
    z = dem["z"].astype(np.float64)
    dx = float(dem["dx"])
    valid = np.isfinite(z)
    zt = hy["zt"].astype(np.float64)
    A = hy["A"].astype(np.float64)
    ny, nx = z.shape

    # --- 1. TomoSAR 산출물(레이더 좌표)을 같은 30 m 격자로 binning
    gc = np.load(AFRISAR_DIR / "lope_ground_canopy.npz")
    with h5py.File(extract.tomo_file("lope", "capon", "hh"), "r") as h:
        lat = h["Latitude"][:].astype(np.float64)
        lon = h["Longitude"][:].astype(np.float64)
    lat0, lon0 = float(dem["lat0"]), float(dem["lon0"])
    x = np.deg2rad(lon - lon0) * R_E * np.cos(np.deg2rad(lat0))
    y = np.deg2rad(lat - lat0) * R_E
    J = np.rint((x - float(dem["x0"])) / dx).astype(np.int64)
    I = np.rint((y - float(dem["y0"])) / dx).astype(np.int64)

    def to_grid(a):
        m = np.isfinite(a) & (I >= 0) & (I < ny) & (J >= 0) & (J < nx)
        k = I[m] * nx + J[m]
        s = np.bincount(k, a[m], ny * nx)
        n = np.bincount(k, None, ny * nx)
        with np.errstate(invalid="ignore"):
            return (s / n).reshape(ny, nx)

    CH = to_grid(gc["ch"])
    G = to_grid(gc["g"])
    # 점검: 지오코딩 정합 — 레이더 TerrainHeight 를 같은 방식으로 옮겨 DEM 과 비교
    Tg = to_grid(gc["T"])
    d = Tg - z
    print(
        f"geocode check |T_binned - DEM30| median {np.nanmedian(np.abs(d)):.2f} m, "
        f"p95 {np.nanpercentile(np.abs(d), 95):.2f} m"
    )

    # --- 2. 지형·수문 변수
    gy, gx = np.gradient(np.where(valid, z, np.nan), dx)
    slope = np.hypot(gx, gy)
    twi = np.log((A / dx) / np.maximum(slope, 1e-3))
    res = {}
    for Ac in (2.5e5, 1e6):
        res[Ac] = hand_map(zt, valid, A, Ac, dx)
    HAND, DCH = res[1e6]
    ok = valid & np.isfinite(CH) & np.isfinite(HAND) & np.isfinite(slope) & (CH > -5) & (CH < 80)
    print(f"cells with canopy height & HAND: {ok.sum():,} ({ok.sum() * dx * dx / 1e6:.0f} km2)")
    print("canopy height (tomo top - tomo ground), 30 m cells:", q(CH[ok]))
    print("HAND (Ac=1 km2):", q(HAND[ok]), "| HAND (Ac=0.25 km2):", q(res[2.5e5][0][ok]))

    # 공간자기상관 완화: 90 m 블록(3x3) 평균에서 상관 계산
    with np.errstate(invalid="ignore"):
        B = {
            n: block(np.where(ok, v, np.nan))
            for n, v in dict(
                CH=CH,
                HAND=HAND,
                HAND025=res[2.5e5][0],
                slope=slope,
                twi=twi,
                elev=z,
                dch=DCH,
                G=G,
                logA=np.log10(A),
            ).items()
        }
    bok = np.all([np.isfinite(v) for v in B.values()], axis=0)
    print(f"90 m blocks used: {bok.sum():,}")
    for n in ("HAND", "HAND025", "slope", "twi", "elev", "dch", "logA"):
        print(f"  Spearman(CH, {n}) = {spearman(B['CH'][bok], B[n][bok]):+.3f}")
    # 다중회귀 CH ~ log1p(HAND) + slope + twi + elev
    Xm = np.c_[
        np.ones(bok.sum()),
        np.log1p(np.maximum(B["HAND"][bok], 0)),
        B["slope"][bok],
        B["twi"][bok],
        B["elev"][bok],
    ]
    coef, *_ = np.linalg.lstsq(Xm, B["CH"][bok], rcond=None)
    pred = Xm @ coef
    r2 = 1 - np.var(B["CH"][bok] - pred) / np.var(B["CH"][bok])
    print(f"OLS CH ~ 1 + log1p(HAND) + slope + TWI + elev: coef {np.round(coef, 3)}, R2 = {r2:.3f}")
    # HAND 구간별 수관고/사바나 비율
    edges = [0, 2, 5, 10, 20, 40, 80, 200, 600]
    print(
        "HAND bin [m] : n | CH median | CH p90 | savanna-like(CH<8) frac | ground offset g median"
    )
    for a0, a1 in zip(edges[:-1], edges[1:], strict=True):
        m = ok & (HAND >= a0) & (HAND < a1)
        if m.sum() < 200:
            continue
        print(
            f"  {a0:>4}-{a1:<4}: {m.sum():>7,} | {np.median(CH[m]):5.1f} | "
            f"{np.percentile(CH[m], 90):5.1f} | {np.mean(CH[m] < 8):.2f} | {np.nanmedian(G[m]):+.1f}"
        )
    print("slope bin : n | CH median | savanna-like frac")
    for a0, a1 in ((0, 0.05), (0.05, 0.1), (0.1, 0.2), (0.2, 0.3), (0.3, 0.5), (0.5, 2)):
        m = ok & (slope >= a0) & (slope < a1)
        if m.sum() < 200:
            continue
        print(
            f"  {a0:.2f}-{a1:.2f}: {m.sum():>7,} | {np.median(CH[m]):5.1f} | {np.mean(CH[m] < 8):.2f}"
        )
    # SRTM 편향(지면오프셋) vs 수관고
    m = ok & np.isfinite(G)
    print(
        f"Spearman(G ground offset, CH) = {spearman(G[m], CH[m]):+.3f}; "
        f"G median in CH>30: {np.median(G[m & (CH > 30)]):+.1f} m, CH<8: {np.median(G[m & (CH < 8)]):+.1f} m"
    )
    VEG_DIR.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(
        VEG_DIR / "lope_veg_pilot.npz",
        CH=CH.astype(np.float32),
        G=G.astype(np.float32),
        HAND=HAND.astype(np.float32),
        slope=slope.astype(np.float32),
        twi=twi.astype(np.float32),
        DCH=DCH.astype(np.float32),
    )

    # --- 3. 덤: 토모 지면으로 보정한 DTM 에서 θ 다시 맞추기 (hydro.py, chi.py 에 lope_tomo_dtm 으로 넣음)
    Gf = np.where(np.isfinite(G), G, 0.0)
    w = ndimage.uniform_filter(np.isfinite(G).astype(float), 5)
    Gsm = ndimage.uniform_filter(Gf, 5) / np.maximum(w, 1e-6)
    Gsm = np.where(w > 0.3, Gsm, 0.0)
    zc = np.where(valid, z + Gsm, np.nan)
    np.savez_compressed(
        AFRISAR_DIR / "lope_tomo_dtm_dem_30m.npz",
        z=zc.astype(np.float32),
        x0=dem["x0"],
        y0=dem["y0"],
        dx=dem["dx"],
        lat0=dem["lat0"],
        lon0=dem["lon0"],
    )
    print("corrected DTM saved; ground correction applied:", q(Gsm[valid & (w > 0.3)]))


if __name__ == "__main__":
    main()
