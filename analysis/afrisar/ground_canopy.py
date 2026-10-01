"""로페 Capon HH/HV 프로파일에서 지면(SRTM 대비)과 수관 꼭대기를 추정합니다 (잡음 바닥 제거).

지면: HH 에서 상대 진폭 0.5 이상인 국소 최대 중 가장 낮은 것.
수관 꼭대기: HV 누적 에너지 95% 높이 (RH95 와 비슷한 값).
높이는 모두 SRTM 기준 [m] 이고, -40..70 m 창만 씁니다.

입력: data/cache/afrisar/data/lope-tomo-capon-hh.h5, lope-tomo-capon-hv.h5 (각 약 1.4 GB, 없으면 꺼냄)
출력: data/derived/afrisar/lope_ground_canopy.npz
    g 지면, top 수관 꼭대기, ch = top - g 수관 높이, hvpk HV 최대 높이, T TerrainHeight [m] (레이더 격자)
실행: uv run python analysis/afrisar/ground_canopy.py
"""

import h5py
import numpy as np
from extract import tomo_file

from bpcg.core.paths import DERIVED

OUT_DIR = DERIVED / "afrisar"


def load_norm(path, a0, a1, w):
    """방위 a0:a1 줄의 프로파일을 읽어 10% 백분위(잡음 바닥)를 빼고 픽셀별 최댓값으로 나눕니다."""
    with h5py.File(path, "r") as h:
        H = h["Heights"][:]
        X = h["Tomogram"][:, a0:a1, :].astype(np.float32)[w]
    X = np.maximum(X, 0)
    floor = np.percentile(X, 10, axis=0)
    X = np.maximum(X - floor, 0)
    mx = X.max(0)
    X = X / np.where(mx > 0, mx, 1)
    return H[w], X, mx > 0


def q(a):
    """유한값의 백분위와 유효 비율."""
    f = a[np.isfinite(a)]
    return (
        " ".join(f"p{p}:{np.percentile(f, p):.1f}" for p in (5, 25, 50, 75, 95))
        + f" (valid {np.isfinite(a).mean():.0%})"
    )


def main(block=192):
    hh_path = tomo_file("lope", "capon", "hh")
    hv_path = tomo_file("lope", "capon", "hv")
    with h5py.File(hh_path, "r") as h:
        H = h["Heights"][:]
        ny, nx = h["TerrainHeight"].shape
        T = h["TerrainHeight"][:]
    w = (H >= -40) & (H <= 70)
    g = np.full((ny, nx), np.nan, np.float32)
    top = g.copy()
    hvpk = g.copy()
    for a in range(0, ny, block):
        Hw, hh, ok1 = load_norm(hh_path, a, a + block, w)
        _, hv, ok2 = load_norm(hv_path, a, a + block, w)
        # 지면: HH 에서 상대 진폭 0.5 이상인 국소 최대 중 가장 낮은 것
        loc = (hh[1:-1] >= hh[:-2]) & (hh[1:-1] >= hh[2:]) & (hh[1:-1] >= 0.5)
        has = loc.any(0)
        first = np.argmax(loc, axis=0) + 1
        g[a : a + block] = np.where(ok1 & has, Hw[first], np.nan)
        # 수관 꼭대기: HV 누적 에너지 95% 높이 (RH95 유사)
        c = np.cumsum(hv, axis=0)
        c = c / np.where(c[-1] > 0, c[-1], 1)
        top[a : a + block] = np.where(ok2, Hw[np.argmax(c >= 0.95, axis=0)], np.nan)
        hvpk[a : a + block] = np.where(ok2, Hw[np.argmax(hv, axis=0)], np.nan)
    ch = top - g
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(OUT_DIR / "lope_ground_canopy.npz", g=g, top=top, ch=ch, hvpk=hvpk, T=T)
    print("ground (HH lowest strong peak) rel. SRTM [m]:", q(g))
    print("HV volume peak rel. SRTM [m]:", q(hvpk))
    print("canopy top RH95-like rel. SRTM [m]:", q(top))
    print("canopy height = top - ground [m]:", q(ch))
    # 수관 높이로 숲/사바나 나누기
    forest = ch > 20
    sav = ch < 8
    print(
        f"forest-like (ch>20 m) {np.nanmean(forest):.0%}, savanna-like (ch<8 m) {np.nanmean(sav):.0%}"
    )
    print("ground offset in forest-like:", q(np.where(forest, g, np.nan)))
    print("ground offset in savanna-like:", q(np.where(sav, g, np.nan)))


if __name__ == "__main__":
    main()
