"""가이드 4장 지형 비교에 쓸 실측 기준값: 경사 분포, 고도곡선(HI), 완전 유역 단위 HI.

경사는 중앙차분 |grad z| [m/m], HI(hypsometric integral) = (평균 - 최소) / (최대 - 최소).
'완전 유역'은 5 km^2 이상이고 띠 가장자리에 닿는 셀이 max(10, 1%) 이하인 유역입니다.

입력: data/derived/afrisar/<site>_dem_30m.npz (geocode.py)
출력: <out-dir>/<site>_ch4_reference.npz (기본 data/derived/afrisar/)
    slope_hist: 경사 0..1 을 100 구간으로 나눈 개수, hyps: 고도 0..100 백분위 [m],
    basin_HI: 완전 유역별 HI, basin_area_km2: 그 유역 면적 [km^2]
저장소에 올린 값: analysis/afrisar/results/<site>_ch4_reference.npz (같은 입력이면 비트 단위로 같아야 함)
실행: uv run python analysis/afrisar/ch4_reference.py lope rabi [--out-dir 폴더]
"""

import argparse
from pathlib import Path

import numba as nb
import numpy as np
from hydro import OUT_DIR, d8, fill_eps


@nb.njit(cache=True)
def label_outlets(order, rec):
    """order 는 높은 곳부터. 뒤에서부터(하류 먼저) 돌며 셀마다 출구 셀 번호를 붙입니다."""
    lab = -np.ones(rec.size, np.int64)
    for t in range(order.size - 1, -1, -1):  # 하류(낮은 곳)부터
        c = order[t]
        r = rec[c]
        lab[c] = c if r < 0 else lab[r]
    return lab


def reference(site, in_dir=OUT_DIR, out_dir=OUT_DIR):
    d = np.load(in_dir / f"{site}_dem_30m.npz")
    z = d["z"].astype(np.float64)
    dx = float(d["dx"])
    valid = np.isfinite(z)
    ny, nx = z.shape
    gy, gx = np.gradient(np.where(valid, z, np.nan), dx)
    s = np.hypot(gx, gy)
    s = s[np.isfinite(s)]
    zv = z[valid]
    HI = (zv.mean() - zv.min()) / (zv.max() - zv.min())
    print(
        f"[{site}] grid {ny}x{nx} @ {dx:.0f} m; swath HI {HI:.3f}; "
        f"slope(|grad|, central diff) mean {s.mean():.3f} p50 {np.percentile(s, 50):.3f} "
        f"p90 {np.percentile(s, 90):.3f} p95 {np.percentile(s, 95):.3f} p99 {np.percentile(s, 99):.3f}"
    )
    zz = np.where(valid, z, 0.0)
    zt = fill_eps(zz, valid, 1e-3)
    rec, dist = d8(zt, valid, dx)
    fr = rec.ravel()
    idx = np.nonzero(valid.ravel())[0]
    order = idx[np.argsort(-zt.ravel()[idx], kind="stable")]
    lab = label_outlets(order, fr).reshape(ny, nx)
    # 가장자리 셀(무효/배열 경계 인접, 4-이웃)
    pad = np.pad(valid, 1, constant_values=False)
    edge = valid & ~(pad[:-2, 1:-1] & pad[2:, 1:-1] & pad[1:-1, :-2] & pad[1:-1, 2:])
    labs, cnt = np.unique(lab[valid], return_counts=True)
    ecount = np.bincount(np.searchsorted(labs, lab[edge]), minlength=labs.size)
    his, areas = [], []
    for L, n, e in zip(labs, cnt, ecount, strict=True):
        A = n * dx * dx
        if A < 5e6 or e > max(10, 0.01 * n):  # 5 km2 이상, 경계 접촉 셀이 적은(≈출구 부근만) 유역
            continue
        zb = z[lab == L]
        his.append((zb.mean() - zb.min()) / (zb.max() - zb.min()))
        areas.append(A / 1e6)
    his = np.array(his)
    areas = np.array(areas)
    if his.size:
        print(
            f"[{site}] complete basins >=5 km2: n={his.size}, area median {np.median(areas):.1f} km2 "
            f"(max {areas.max():.0f}); HI median {np.median(his):.3f} "
            f"IQR {np.percentile(his, 25):.3f}-{np.percentile(his, 75):.3f}"
        )
    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / f"{site}_ch4_reference.npz"
    np.savez_compressed(
        out,
        slope_hist=np.histogram(s, bins=np.linspace(0, 1, 101))[0],
        hyps=np.percentile(zv, np.arange(0, 101)),
        basin_HI=his,
        basin_area_km2=areas,
    )
    return out


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="4장 비교용 실측 기준값을 만듭니다.")
    ap.add_argument("sites", nargs="+", help="lope, rabi 등 (<site>_dem_30m.npz 가 있어야 함)")
    ap.add_argument(
        "--out-dir", type=Path, default=OUT_DIR, help="저장 폴더 (기본 data/derived/afrisar)"
    )
    args = ap.parse_args()
    for site in args.sites:
        print("saved", reference(site, out_dir=args.out_dir))
