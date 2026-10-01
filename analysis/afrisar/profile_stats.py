"""TomoSAR 수직 프로파일에서 지면 위치(SRTM 대비)와 수관 높이를 대강 추정합니다.

픽셀마다 프로파일을 최댓값으로 나눈 뒤, 문턱값(기본 0.2) 이상인 가장 낮은 높이(지면 대용)와
가장 높은 높이(수관 꼭대기 대용), 최댓값 높이를 구합니다. 높이는 모두 SRTM 기준 [m] 입니다.

입력: zip 안의 h5 하나 (예: lope-tomo-fourier-hv.h5, lope-tomo-capon-hv.h5). 없으면 캐시에 꺼냄
출력: data/derived/afrisar/<h5 이름, - 는 _ 로>_profile_stats.npz (bot, top, pk, H, mean_prof)
    예: lope_tomo_fourier_hv_profile_stats.npz. 2026-10-01 에 남긴 결과는 문턱값 0.3 으로 만든 것입니다.
실행: uv run python analysis/afrisar/profile_stats.py lope-tomo-fourier-hv.h5 [0.2]
"""

import sys
from pathlib import PurePosixPath

import h5py
import numpy as np
from extract import ensure_member

from bpcg.core.paths import DERIVED

OUT_DIR = DERIVED / "afrisar"


def q(a):
    """유한값의 5·25·50·75·95 백분위를 한 줄로."""
    f = a[np.isfinite(a)]
    return " ".join(f"p{p}:{np.percentile(f, p):.1f}" for p in (5, 25, 50, 75, 95))


def profile_stats(fn, thr=0.2, block=256):
    """방위 방향으로 block 줄씩 읽으며 픽셀별 bot/top/pk 를 구합니다."""
    with h5py.File(ensure_member(fn), "r") as h:
        H = h["Heights"][:]
        tm = h["Tomogram"]
        nh, ny, nx = tm.shape
        bot = np.full((ny, nx), np.nan, np.float32)
        top = bot.copy()
        pk = bot.copy()
        mean_prof = np.zeros(nh)
        for a in range(0, ny, block):
            P = tm[:, a : a + block, :].astype(np.float32)  # (nh, c, nx)
            P = np.maximum(P, 0)
            mx = P.max(0)
            good = mx > 0
            Pn = P / np.where(good, mx, 1)
            above = Pn >= thr
            first = np.argmax(above, axis=0)
            last = nh - 1 - np.argmax(above[::-1], axis=0)
            bot[a : a + block] = np.where(good, H[first], np.nan)
            top[a : a + block] = np.where(good, H[last], np.nan)
            pk[a : a + block] = np.where(good, H[np.argmax(P, axis=0)], np.nan)
            mean_prof += (Pn * good).sum((1, 2))
        mean_prof /= mean_prof.max()
    stem = PurePosixPath(fn).stem.replace("-", "_")  # 파일 이름은 snake_case
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(
        OUT_DIR / f"{stem}_profile_stats.npz", bot=bot, top=top, pk=pk, H=H, mean_prof=mean_prof
    )
    print(f"[{fn}] heights {H[0]}..{H[-1]} step {H[1] - H[0]}; threshold {thr} of per-pixel max")
    print("  lowest significant return (ground proxy, rel. SRTM):", q(bot))
    print("  peak return height:", q(pk))
    print("  highest significant return (canopy top proxy):", q(top))
    print("  apparent profile extent top-bot:", q(top - bot))
    print(
        "  mean normalized profile:",
        " ".join(f"{h:.0f}:{v:.2f}" for h, v in zip(H, mean_prof, strict=True) if v > 0.05),
    )


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("사용법: profile_stats.py <h5 이름> [문턱값=0.2]")
    profile_stats(sys.argv[1], float(sys.argv[2]) if len(sys.argv) > 2 else 0.2)
