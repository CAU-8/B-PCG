"""잘리지 않은 유역만 골라 (1) 구간 평활 경사-면적, (2) χ 분석으로 θ 를 추정합니다.

'잘린 유역'은 출구 근처(10칸)를 뺀 셀이 띠 가장자리에 닿는 유역입니다.
(1) 하류로 L = 300 m 떨어진 지점까지의 낙차/거리로 경사를 재고, A >= 1e6 m^2 구간에서 직선 맞춤.
(2) θ = 0.10..0.90 마다 χ = ∫(A0/A)^θ dx 를 계산하고, 20 km^2 이상 유역에서 z-χ 직선성(R^2) 중앙값이
    가장 큰 θ 를 고릅니다.

입력: data/derived/afrisar/<이름>_dem_30m.npz
출력: 화면 출력만 (파일 없음)
실행: uv run python analysis/afrisar/chi.py lope rabi lope_tomo_dtm
"""

import sys

import numba as nb
import numpy as np
from hydro import OUT_DIR, accumulate, d8, fill_eps


@nb.njit(cache=True)
def roots_and_edge(order_up, rec, valid_flat, nx, ny):
    """order_up 은 낮은 곳부터(하류 먼저). 셀마다 출구 셀 번호(root)를 돌려줍니다."""
    N = rec.size
    root = -np.ones(N, np.int64)
    for t in range(order_up.size):
        c = order_up[t]
        r = rec[c]
        root[c] = c if r < 0 else root[r]
    return root


def analyze(site, dx=30.0, A_min=1e6):
    d = np.load(OUT_DIR / f"{site}_dem_{int(dx)}m.npz")
    z = d["z"].astype(np.float64)
    valid = np.isfinite(z)
    zz = np.where(valid, z, 0.0)
    ny, nx = z.shape
    zt = fill_eps(zz, valid, 1e-3)
    rec, dist = d8(zt, valid, dx)
    rf = rec.ravel()
    vf = valid.ravel()
    idx = np.nonzero(vf)[0]
    zt_f = zt.ravel()
    up = idx[np.argsort(-zt_f[idx], kind="stable")]
    A = accumulate(up, rf, np.where(vf, dx * dx, 0.0))
    down = up[::-1]
    root = roots_and_edge(down, rf, vf, nx, ny)
    # 가장자리 셀(무효 셀 이웃) 표시
    pad = np.pad(valid, 1, constant_values=False)
    edge = valid & ~(
        pad[:-2, :-2]
        & pad[:-2, 1:-1]
        & pad[:-2, 2:]
        & pad[1:-1, :-2]
        & pad[1:-1, 2:]
        & pad[2:, :-2]
        & pad[2:, 1:-1]
        & pad[2:, 2:]
    )
    ef = edge.ravel()
    # 유역(root)마다: 출구 근처(10셀)를 제외한 셀이 가장자리에 닿으면 '잘린 유역'
    iy, ix = np.divmod(np.arange(ny * nx), nx)
    ry, rx = np.divmod(np.where(root >= 0, root, 0), nx)
    far = np.hypot(iy - ry, ix - rx) > 10
    bad_roots = np.unique(root[ef & far & (root >= 0)])
    complete = vf & ~np.isin(root, bad_roots)
    big = np.bincount(root[complete], minlength=ny * nx)
    print(
        f"[{site}] complete basins: {np.sum(big * dx * dx > 5e6)} with area > 5 km2; "
        f"largest {big.max() * dx * dx / 1e6:.0f} km2; "
        f"cells in complete basins {complete.sum() / vf.sum():.0%}"
    )
    # (1) 구간 평활 경사: 하류로 L=300 m 떨어진 지점까지의 낙차 / 거리 (원래 z)
    L = 300.0
    S = np.full(ny * nx, np.nan)
    zf = zz.ravel()
    df = dist.ravel()
    ch = complete & (A >= 1e5)
    for c in np.nonzero(ch)[0]:
        s = 0.0
        k = c
        while s < L and rf[k] >= 0:
            s += df[k]
            k = rf[k]
        if s >= L * 0.9:
            S[c] = (zf[c] - zf[k]) / s
    ok = ch & np.isfinite(S) & (S > 0)
    la, ls = np.log10(A[ok]), np.log10(S[ok])
    bins = np.arange(5, la.max() + 0.2, 0.2)
    cen = []
    med = []
    for b0, b1 in zip(bins[:-1], bins[1:], strict=True):
        sel = (la >= b0) & (la < b1)
        if sel.sum() >= 30:
            cen.append(0.5 * (b0 + b1))
            med.append(np.median(ls[sel]))
    cen, med = np.array(cen), np.array(med)
    sel = cen >= np.log10(A_min)
    if sel.sum() >= 4:
        p = np.polyfit(cen[sel], med[sel], 1)
        print(
            f"[{site}] smoothed (300 m) slope-area, complete basins, A>={A_min:.0e}: "
            f"theta = {-p[0]:.3f}, bins {sel.sum()}  medians:",
            " ".join(f"{c:.1f}:{m:.2f}" for c, m in zip(cen, med, strict=True)),
        )
    # (2) χ 분석: 유역마다 χ = ∫(A0/A)^θ dx (출구에서 상류로), 하천 셀에서 z-χ 직선성
    A0 = 1.0
    chan = complete & (A >= A_min)
    best = []
    basins = [r for r in np.unique(root[chan]) if big[r] * dx * dx > 2e7]  # 20 km2 이상 유역
    for theta in np.arange(0.1, 0.95, 0.05):
        chi = np.zeros(ny * nx)
        for c in down:
            r = rf[c]
            if r >= 0 and chan[c]:
                chi[c] = chi[r] + (A0 / A[c]) ** theta * df[c]
        r2s = []
        for b in basins:
            m = chan & (root == b)
            if m.sum() < 50:
                continue
            x, y = chi[m], zf[m]
            p = np.polyfit(x, y, 1)
            res = y - np.polyval(p, x)
            r2s.append(1 - res.var() / y.var())
        best.append((theta, np.median(r2s), len(r2s)))
    best = np.array(best)
    i = np.argmax(best[:, 1])
    print(
        f"[{site}] chi analysis over {int(best[0, 2])} basins (>20 km2, A>={A_min:.0e}): "
        f"best theta = {best[i, 0]:.2f} (median z-chi R2 {best[i, 1]:.3f});",
        "R2 curve:",
        " ".join(f"{t:.2f}:{r:.3f}" for t, r, _ in best[::2]),
    )


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("사용법: chi.py <이름> [<이름> ...]  (예: lope rabi lope_tomo_dtm)")
    for s in sys.argv[1:]:
        analyze(s)
