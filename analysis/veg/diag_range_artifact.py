"""진단: 수관 높이 추정이 입사각(레인지)·지형 경사(레이더 쪽/반대쪽)에 얼마나 오염됐는지,
HV 전력으로 숲/사바나를 나눌 수 있는지 봅니다.

결론(설계도 부록 A-2): 수관 높이와 하천 위 높이의 관계는 절반가량이 촬영 방식 때문에 생긴 가짜입니다
(비행 경로에서 가까운 쪽 약 44 m, 먼 쪽 26~30 m).

입력: data/derived/afrisar/lope_ground_canopy.npz, data/cache/afrisar/data/lope-tomo-fourier-hv.h5
출력: data/derived/veg/lope_radar_diag.npz (pdb HV 총전력 [dB], sr 레인지 방향 지형 경사; 레이더 격자)
실행: uv run python analysis/veg/diag_range_artifact.py
"""

import h5py
import numpy as np
from afrisar_link import AFRISAR_DIR, VEG_DIR, extract

R_E = 6378137.0  # [m]


def main():
    gc = np.load(AFRISAR_DIR / "lope_ground_canopy.npz")
    CH = gc["ch"]
    T = gc["T"].astype(np.float64)
    with h5py.File(extract.tomo_file("lope", "fourier", "hv"), "r") as h:
        Rg = h["Ranges"][:]
        Hh = h["Heights"][:]
        tm = h["Tomogram"][:]
        lat = h["Latitude"][:].astype(np.float64)
        lon = h["Longitude"][:].astype(np.float64)
    print("Fourier heights:", Hh)
    pw = np.maximum(tm, 0).sum(0)
    pdb = 10 * np.log10(np.where(pw > 0, pw, np.nan))
    # 지상 거리 간격(레인지 방향) — 위경도로부터
    x = np.deg2rad(lon) * R_E * np.cos(np.deg2rad(lat.mean()))
    y = np.deg2rad(lat) * R_E
    dgr = np.hypot(np.diff(x, axis=1), np.diff(y, axis=1))
    dgr = np.c_[dgr, dgr[:, -1:]]
    # 레인지 방향 지형 경사 (+: 먼 쪽이 높음 = 레이더를 향한 사면)
    sr = np.gradient(T, axis=1) / np.maximum(dgr, 1)
    ok = np.isfinite(CH) & (CH > -5) & (CH < 80) & np.isfinite(pdb)
    print(
        f"slant range {Rg[0]:.0f}..{Rg[-1]:.0f} m; ground range spacing "
        f"near {np.median(dgr[:, :20]):.1f} far {np.median(dgr[:, -20:]):.1f} m"
    )
    deciles = list(zip(range(0, 1640, 164), range(164, 1641, 164), strict=True))
    print(
        "CH median by range decile (near->far):",
        " ".join(f"{np.median(CH[:, a:b][ok[:, a:b]]):.1f}" for a, b in deciles),
    )
    print(
        "HV total power dB by range decile:",
        " ".join(f"{np.nanmedian(pdb[:, a:b]):.1f}" for a, b in deciles),
    )
    print(
        "range-slope bin : n | CH median   "
        "(+ = slope facing radar/foreslope? sign depends on look side)"
    )
    for a0, a1 in (
        (-1, -0.3),
        (-0.3, -0.15),
        (-0.15, -0.05),
        (-0.05, 0.05),
        (0.05, 0.15),
        (0.15, 0.3),
        (0.3, 1),
    ):
        m = ok & (sr >= a0) & (sr < a1)
        print(f"  {a0:+.2f}..{a1:+.2f}: {m.sum():>8,} | {np.median(CH[m]):.1f}")
    # HV 전력 분포 (숲/비숲 이봉성?)
    h_, e_ = np.histogram(pdb[ok], bins=40)
    print(
        "HV power dB histogram:",
        " ".join(f"{(e_[i] + e_[i + 1]) / 2:.0f}:{h_[i] // 1000}k" for i in range(0, 40, 2)),
    )
    lo = np.nanpercentile(pdb[ok], 5)
    print(
        f"HV dB p1 {np.nanpercentile(pdb[ok], 1):.1f} p5 {lo:.1f} "
        f"p50 {np.nanpercentile(pdb[ok], 50):.1f}"
    )
    m_low = ok & (pdb < np.nanpercentile(pdb[ok], 3))
    print(
        f"CH median in lowest-3% HV power pixels: {np.median(CH[m_low]):.1f} "
        f"(vs all {np.median(CH[ok]):.1f})"
    )
    VEG_DIR.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(
        VEG_DIR / "lope_radar_diag.npz", pdb=pdb.astype(np.float32), sr=sr.astype(np.float32)
    )


if __name__ == "__main__":
    main()
