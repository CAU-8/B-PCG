"""지오코딩한 30 m DEM 에 가이드 2장 물길(priority-flood + ε, D8, 유량)과 3장 경사-면적 분석을 적용합니다.

fill_eps, d8, accumulate 는 chi.py, ch4_reference.py, eco.py 와 analysis/veg/ 스크립트가 함께 씁니다.
가져오기만 하면 계산은 돌지 않습니다.

입력: data/derived/afrisar/<이름>_dem_30m.npz (geocode.py 또는 analysis/veg/pilot_lope.py 가 만듦)
출력: data/derived/afrisar/<이름>_hydro.npz
    A [m^2] 상류 면적, S [m/m] 수신 셀까지의 경사, zt [m] ε 채움 고도, fill [m] 채운 깊이,
    cen/med: log10 A 구간 중심과 그 구간의 log10 S 중앙값
실행: uv run python analysis/afrisar/hydro.py lope rabi lope_tomo_dtm
"""

import sys

import numba as nb
import numpy as np

from bpcg_studio.paths import DERIVED

OUT_DIR = DERIVED / "afrisar"
DI = np.array([-1, -1, -1, 0, 0, 1, 1, 1])
DJ = np.array([-1, 0, 1, -1, 1, -1, 0, 1])


@nb.njit(cache=True)
def fill_eps(z, valid, eps):
    """priority-flood + ε. 무효 셀이나 배열 가장자리에 닿는 유효 셀을 출구로 삼습니다."""
    ny, nx = z.shape
    zt = z.copy()
    seen = ~valid
    N = ny * nx
    hk = np.empty(N)
    hi = np.empty(N, np.int64)
    n = 0
    for i in range(ny):
        for j in range(nx):
            if not valid[i, j]:
                continue
            edge = False
            for k in range(8):
                a, b = i + DI[k], j + DJ[k]
                if a < 0 or a >= ny or b < 0 or b >= nx or not valid[a, b]:
                    edge = True
            if edge:
                seen[i, j] = True
                # 힙에 넣기
                p = n
                hk[p] = zt[i, j]
                hi[p] = i * nx + j
                n += 1
                while p > 0:
                    q = (p - 1) // 2
                    if hk[q] <= hk[p]:
                        break
                    hk[p], hk[q] = hk[q], hk[p]
                    hi[p], hi[q] = hi[q], hi[p]
                    p = q
    while n > 0:
        c = hi[0]
        zc = hk[0]
        n -= 1
        hk[0] = hk[n]
        hi[0] = hi[n]
        p = 0
        while True:
            l = 2 * p + 1
            r = l + 1
            m = p
            if l < n and hk[l] < hk[m]:
                m = l
            if r < n and hk[r] < hk[m]:
                m = r
            if m == p:
                break
            hk[p], hk[m] = hk[m], hk[p]
            hi[p], hi[m] = hi[m], hi[p]
            p = m
        i, j = c // nx, c % nx
        for k in range(8):
            a, b = i + DI[k], j + DJ[k]
            if a < 0 or a >= ny or b < 0 or b >= nx or seen[a, b]:
                continue
            seen[a, b] = True
            zt[a, b] = max(zt[a, b], zc + eps)
            p = n
            hk[p] = zt[a, b]
            hi[p] = a * nx + b
            n += 1
            while p > 0:
                q = (p - 1) // 2
                if hk[q] <= hk[p]:
                    break
                hk[p], hk[q] = hk[q], hk[p]
                hi[p], hi[q] = hi[q], hi[p]
                p = q
    return zt


@nb.njit(cache=True)
def d8(zt, valid, dx):
    """가장 가파른 이웃 하나로 흘려보냅니다. rec 는 평평하게 편 셀 번호(-1 = 출구), dist [m]."""
    ny, nx = zt.shape
    rec = -np.ones((ny, nx), np.int64)
    dist = np.zeros((ny, nx))
    for i in range(ny):
        for j in range(nx):
            if not valid[i, j]:
                continue
            best = 0.0
            bk = -1
            for k in range(8):
                a, b = i + DI[k], j + DJ[k]
                if a < 0 or a >= ny or b < 0 or b >= nx or not valid[a, b]:
                    continue
                d = dx * np.sqrt(DI[k] * DI[k] + DJ[k] * DJ[k])
                s = (zt[i, j] - zt[a, b]) / d
                if s > best:
                    best = s
                    bk = k
            if bk >= 0:
                rec[i, j] = (i + DI[bk]) * nx + (j + DJ[bk])
                dist[i, j] = dx * np.sqrt(DI[bk] ** 2 + DJ[bk] ** 2)
    return rec, dist


@nb.njit(cache=True)
def accumulate(order, rec, w):
    """order 는 상류(높은 곳)부터. w 는 셀마다 더할 값(보통 셀 면적 [m^2])."""
    A = w.copy()
    for t in range(order.size):
        c = order[t]
        r = rec[c]
        if r >= 0:
            A[r] += A[c]
    return A


def analyze(site, dx=30.0):
    """한 DEM 에 물길·경사-면적 분석을 돌리고 결과를 출력·저장합니다."""
    d = np.load(OUT_DIR / f"{site}_dem_{int(dx)}m.npz")
    z = d["z"].astype(np.float64)
    valid = np.isfinite(z)
    zz = np.where(valid, z, 0.0)
    zt = fill_eps(zz, valid, 1e-3)
    rec, dist = d8(zt, valid, dx)
    ny, nx = z.shape
    flat_rec = rec.ravel()
    vflat = valid.ravel()
    idx = np.nonzero(vflat)[0]
    order = idx[np.argsort(-zt.ravel()[idx], kind="stable")]  # 높은 곳부터 = 상류부터
    A = accumulate(order, flat_rec, np.where(vflat, dx * dx, 0.0)).reshape(ny, nx)
    # 경사: 원래 DEM 으로 수신 셀까지 (채워진 셀 제외)
    r = flat_rec.reshape(ny, nx)
    S = np.full((ny, nx), np.nan)
    m = valid & (r >= 0)
    zr = z.ravel()[np.where(m, r, 0).ravel()].reshape(ny, nx)
    S[m] = (z[m] - zr[m]) / dist[m]
    filled = (zt - zz) > 0.01
    depth_fill = np.where(valid, zt - zz, np.nan)
    print(
        f"[{site}] valid {valid.sum():,} cells; filled (pits) {filled[valid].mean():.1%}; "
        f"max fill depth {np.nanmax(depth_fill):.1f} m; "
        f"mean fill depth (filled cells) {np.nanmean(depth_fill[filled]):.2f} m"
    )
    print(
        f"[{site}] max drainage area {A.max() / 1e6:.0f} km2; "
        f"relief {np.nanmax(z) - np.nanmin(z):.0f} m"
    )
    # 경계에서 잘린 유역 영향 제거: 유역 면적이 신뢰 가능한 범위만
    ok = valid & ~filled & np.isfinite(S) & (S > 0)
    la, ls = np.log10(A[ok]), np.log10(S[ok])
    bins = np.arange(3, np.log10(A.max()) + 0.2, 0.2)
    cen, med, cnt = [], [], []
    for b0, b1 in zip(bins[:-1], bins[1:], strict=True):
        sel = (la >= b0) & (la < b1)
        if sel.sum() >= 30:
            cen.append(0.5 * (b0 + b1))
            med.append(np.median(ls[sel]))
            cnt.append(sel.sum())
    cen, med = np.array(cen), np.array(med)
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(
        OUT_DIR / f"{site}_hydro.npz",
        A=A.astype(np.float32),
        S=S.astype(np.float32),
        zt=zt.astype(np.float32),
        fill=depth_fill.astype(np.float32),
        cen=cen,
        med=med,
    )
    for lo, hi in ((5.0, 7.5), (5.5, 8.0), (6.0, 8.5)):
        sel = (cen >= lo) & (cen <= hi)
        if sel.sum() >= 4:
            p = np.polyfit(cen[sel], med[sel], 1)
            res = med[sel] - np.polyval(p, cen[sel])
            r2 = 1 - res.var() / med[sel].var()
            print(
                f"[{site}] slope-area fit logA in [{lo},{hi}]: theta = {-p[0]:.3f}, "
                f"k_sn(theta) = {10 ** p[1]:.3g}, R2 = {r2:.2f}, bins {sel.sum()}"
            )
    # 사면 구간 임계경사 후보
    hs = ok & (A < 1e5)
    print(
        f"[{site}] hillslope (A<1e5 m2) slope p50 {np.nanpercentile(S[hs], 50):.3f} "
        f"p95 {np.nanpercentile(S[hs], 95):.3f} p99 {np.nanpercentile(S[hs], 99):.3f}"
    )
    print(
        f"[{site}] binned median log10S by log10A:",
        " ".join(f"{c:.1f}:{v:.2f}" for c, v in zip(cen, med, strict=True)),
    )


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("사용법: hydro.py <이름> [<이름> ...]  (예: lope rabi lope_tomo_dtm)")
    for s in sys.argv[1:]:
        analyze(s)
