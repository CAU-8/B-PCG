"""파일럿 2: 레인지(입사각) 추세를 빼고 평탄지(경사 < 0.05)만 써서 수관 높이/HV 전력과 HAND 의 관계를 다시 봅니다.

결론(설계도 부록 A-2): 로페에서 사바나 후보(낮은 HV)는 하천 위 6 m 안쪽에서 43~48%, 80 m 위에서 12%.
다만 북부는 비가 적고 불 관리를 해 와서 '물길 때문'이라고 단정하지 않습니다.

입력: data/derived/afrisar/lope_dem_30m.npz, lope_ground_canopy.npz,
     data/derived/veg/lope_veg_pilot.npz (pilot_lope.py), lope_radar_diag.npz (diag_range_artifact.py),
     data/cache/afrisar/data/lope-tomo-capon-hh.h5 (위경도만 읽음, 약 1.4 GB, 없으면 꺼냄)
출력: data/derived/veg/lope_radar_detr.npz (CHd, HVd 레인지 추세 제거값, HAND [m], SL; 레이더 격자)
실행: uv run python analysis/veg/pilot2_detrended.py
"""

import h5py
import numpy as np
from afrisar_link import AFRISAR_DIR, VEG_DIR, extract

R_E = 6378137.0  # [m]


def detrend(a, ok):
    """레인지 열별 중앙값을 뺍니다."""
    med = np.array([np.nanmedian(np.where(ok[:, k], a[:, k], np.nan)) for k in range(a.shape[1])])
    return a - med[None, :]


def sp(a, b):
    """순위 상관 (Spearman)."""
    ra = np.argsort(np.argsort(a))
    rb = np.argsort(np.argsort(b))
    return np.corrcoef(ra, rb)[0, 1]


def main():
    dem = np.load(AFRISAR_DIR / "lope_dem_30m.npz")
    pv = np.load(VEG_DIR / "lope_veg_pilot.npz")
    dg = np.load(VEG_DIR / "lope_radar_diag.npz")
    gc = np.load(AFRISAR_DIR / "lope_ground_canopy.npz")
    CH = gc["ch"]
    G = gc["g"]
    with h5py.File(extract.tomo_file("lope", "capon", "hh"), "r") as h:
        lat = h["Latitude"][:].astype(np.float64)
        lon = h["Longitude"][:].astype(np.float64)
    dx = float(dem["dx"])
    ny, nx = dem["z"].shape
    x = np.deg2rad(lon - float(dem["lon0"])) * R_E * np.cos(np.deg2rad(float(dem["lat0"])))
    y = np.deg2rad(lat - float(dem["lat0"])) * R_E
    J = np.clip(np.rint((x - float(dem["x0"])) / dx).astype(int), 0, nx - 1)
    I = np.clip(np.rint((y - float(dem["y0"])) / dx).astype(int), 0, ny - 1)
    HAND = pv["HAND"][I, J]
    SL = pv["slope"][I, J]
    TWI = pv["twi"][I, J]
    Z = dem["z"][I, J]
    HV = dg["pdb"]
    ok = (
        np.isfinite(CH)
        & (CH > -5)
        & (CH < 80)
        & np.isfinite(HAND)
        & np.isfinite(SL)
        & np.isfinite(HV)
    )
    CHd = detrend(CH, ok)
    HVd = detrend(HV, ok)
    flat = ok & (SL < 0.05)
    print(f"pixels ok {ok.sum():,}, flat(<0.05) {flat.sum():,}")
    edges = [0, 1, 3, 6, 10, 20, 40, 80, 600]
    print(
        "FLAT ONLY  HAND bin : n | CH_detrended median | HV_detrended median dB | "
        "low-HV (HVd<-6 dB) frac | tomo ground offset median"
    )
    for a0, a1 in zip(edges[:-1], edges[1:], strict=True):
        m = flat & (HAND >= a0) & (HAND < a1)
        if m.sum() < 500:
            continue
        print(
            f"  {a0:>3}-{a1:<3}: {m.sum():>8,} | {np.median(CHd[m]):+5.1f} | "
            f"{np.median(HVd[m]):+5.2f} | {np.mean(HVd[m] < -6):.3f} | {np.nanmedian(G[m]):+.1f}"
        )
    print("ALL SLOPES HAND bin : n | CH_detrended | HVd | low-HV frac")
    for a0, a1 in zip(edges[:-1], edges[1:], strict=True):
        m = ok & (HAND >= a0) & (HAND < a1)
        if m.sum() < 500:
            continue
        print(
            f"  {a0:>3}-{a1:<3}: {m.sum():>8,} | {np.median(CHd[m]):+5.1f} | "
            f"{np.median(HVd[m]):+5.2f} | {np.mean(HVd[m] < -6):.3f}"
        )
    # 저-HV 화소(사바나/초지/수면 후보)의 공간 분포: 위도 띠별
    lowhv = ok & (HVd < -6)
    bands = [
        a for a in np.arange(-0.40, 0.10, 0.05) if ((lat >= a) & (lat < a + 0.05) & ok).sum() > 1000
    ]
    print(
        "low-HV fraction by latitude band (S->N):",
        " ".join(
            f"{a:.2f}:{np.mean(lowhv[(lat >= a) & (lat < a + 0.05) & ok]):.3f}" for a in bands
        ),
    )
    print(
        f"low-HV: CH_detrended median {np.median(CHd[lowhv]):+.1f}, "
        f"HAND median {np.median(HAND[lowhv]):.1f} vs all {np.median(HAND[ok]):.1f}, "
        f"slope median {np.median(SL[lowhv]):.3f} vs all {np.median(SL[ok]):.3f}"
    )
    # 평탄지 표본(최대 20만)에서 Spearman
    rng = np.random.default_rng(0)
    idx = np.flatnonzero(flat)
    idx = rng.choice(idx, min(200000, idx.size), replace=False)
    for n, v in dict(HAND=HAND, TWI=TWI, elev=Z).items():
        print(
            f"  flat: Spearman(CH_detr, {n}) {sp(CHd.ravel()[idx], v.ravel()[idx]):+.3f} | "
            f"Spearman(HV_detr, {n}) {sp(HVd.ravel()[idx], v.ravel()[idx]):+.3f}"
        )
    VEG_DIR.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(
        VEG_DIR / "lope_radar_detr.npz",
        CHd=CHd.astype(np.float32),
        HVd=HVd.astype(np.float32),
        HAND=HAND.astype(np.float32),
        SL=SL.astype(np.float32),
    )


if __name__ == "__main__":
    main()
