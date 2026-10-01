"""합성 벤치마크: GLO-30 타일 하나 크기(3600 x 3600, 30 m) DEM 에 채움 → D8 → 정렬 → 유량 → χ → 경사-면적.

전 지구 GLO-30 처리 시간 추정(타일당 약 3.3 s → 26,450 타일 약 24시간,
docs/design/research/global_dem_catalog.json)의 근거입니다. 첫 번째 반복은 Numba 컴파일 시간을 포함합니다.

입력: 없음 (멱법칙 스펙트럼 잡음 + 돔으로 합성)
출력: 화면 출력만
실행: uv run python analysis/dem_catalog/bench_flow.py [N=3600]
"""

import heapq
import sys
import time

import numba as nb
import numpy as np

try:
    import resource  # Unix 전용
except ImportError:  # Windows
    resource = None


def synth_dem(N, seed=0):
    """k^-1.6 스펙트럼 잡음(0..2000 m)에 가장자리가 출구가 되도록 돔을 더한 N x N DEM [m]."""
    rng = np.random.default_rng(seed)
    kx = np.fft.fftfreq(N)[:, None]
    ky = np.fft.rfftfreq(N)[None, :]
    k = np.sqrt(kx**2 + ky**2)
    k[0, 0] = 1
    spec = (rng.standard_normal(k.shape) + 1j * rng.standard_normal(k.shape)) * k ** (-1.6)
    z = np.fft.irfft2(spec, s=(N, N)).astype(np.float64)
    z = (z - z.min()) / (z.max() - z.min()) * 2000.0
    yy, xx = np.mgrid[0:N, 0:N]
    z += 0.02 * np.minimum(np.minimum(xx, yy), np.minimum(N - 1 - xx, N - 1 - yy)) * 30  # 돔
    return z.astype(np.float32)


@nb.njit(cache=True)
def priority_flood(dem):
    ny, nx = dem.shape
    n = ny * nx
    out = dem.astype(np.float64).ravel().copy()
    closed = np.zeros(n, np.bool_)
    h = [(0.0, np.int64(0))]
    h.pop()
    for i in range(ny):
        for j in range(nx):
            if i == 0 or j == 0 or i == ny - 1 or j == nx - 1:
                idx = i * nx + j
                closed[idx] = True
                heapq.heappush(h, (out[idx], np.int64(idx)))
    di = np.array([-1, -1, -1, 0, 0, 1, 1, 1])
    dj = np.array([-1, 0, 1, -1, 1, -1, 0, 1])
    while len(h) > 0:
        zc, c = heapq.heappop(h)
        ci = c // nx
        cj = c % nx
        for k in range(8):
            ni = ci + di[k]
            nj = cj + dj[k]
            if ni < 0 or nj < 0 or ni >= ny or nj >= nx:
                continue
            nn = ni * nx + nj
            if closed[nn]:
                continue
            closed[nn] = True
            zn = out[nn]
            if zn <= zc:
                zn = np.nextafter(zc, np.inf) + 1e-6
            out[nn] = zn
            heapq.heappush(h, (zn, np.int64(nn)))
    return out


@nb.njit(cache=True, parallel=True)
def d8(f, ny, nx, dx):
    rec = np.empty(ny * nx, np.int64)
    slope = np.zeros(ny * nx, np.float32)
    di = np.array([-1, -1, -1, 0, 0, 1, 1, 1])
    dj = np.array([-1, 0, 1, -1, 1, -1, 0, 1])
    dist = np.array([1.4142135, 1, 1.4142135, 1, 1, 1.4142135, 1, 1.4142135]) * dx
    for c in nb.prange(ny * nx):
        ci = c // nx
        cj = c % nx
        best = 0.0
        r = np.int64(-1)
        for k in range(8):
            ni = ci + di[k]
            nj = cj + dj[k]
            if ni < 0 or nj < 0 or ni >= ny or nj >= nx:
                continue
            nn = ni * nx + nj
            s = (f[c] - f[nn]) / dist[k]
            if s > best:
                best = s
                r = nn
        rec[c] = r if r >= 0 else c
        slope[c] = best
    return rec, slope


@nb.njit(cache=True)
def accumulate(order, rec, cell_area):
    n = rec.size
    A = np.full(n, cell_area, np.float64)
    for t in range(n):  # order: 채운 고도가 높은 곳부터 (상류 먼저)
        c = order[t]
        r = rec[c]
        if r != c:
            A[r] += A[c]
    return A


@nb.njit(cache=True)
def chi_int(order, rec, A, theta, A0, dx_arr):
    n = rec.size
    chi = np.zeros(n, np.float64)
    for t in range(n - 1, -1, -1):  # 낮은 곳부터 (하류 먼저)
        c = order[t]
        r = rec[c]
        if r != c:
            chi[c] = chi[r] + (A0 / A[c]) ** theta * dx_arr[c]
    return chi


def peak_rss_gb():
    """이 프로세스의 최대 상주 메모리 [GB]. ru_maxrss 단위는 macOS 바이트, Linux KiB 입니다."""
    if resource is not None:
        r = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
        return r / 1e9 if sys.platform == "darwin" else r * 1024 / 1e9
    try:
        import psutil  # Windows 에서는 psutil 이 있을 때만
    except ImportError:
        return None
    mi = psutil.Process().memory_info()
    return getattr(mi, "peak_wset", mi.rss) / 1e9


def main(N=3600, dx=30.0):
    t0 = time.time()
    dem = synth_dem(N)
    print(f"N={N} cells={N * N / 1e6:.1f}M synth {time.time() - t0:.1f}s")
    for rep in range(2):  # rep 0 은 JIT 컴파일 시간 포함
        T = {}
        t = time.time()
        f = priority_flood(dem)
        T["fill"] = time.time() - t
        t = time.time()
        rec, slope = d8(f, N, N, dx)
        T["d8"] = time.time() - t
        t = time.time()
        order = np.argsort(-f, kind="stable")
        T["sort"] = time.time() - t
        t = time.time()
        A = accumulate(order, rec, dx * dx)
        T["acc"] = time.time() - t
        t = time.time()
        dxa = np.where(np.abs(rec - np.arange(rec.size)) % N == 0, dx, dx * 1.4142135).astype(
            np.float64
        )
        chi_int(order, rec, A, 0.45, 1.0, dxa)
        T["chi"] = time.time() - t
        t = time.time()
        m = (A > 1e6) & (slope > 0)
        lb = np.log10(A[m])
        ls = np.log10(slope[m])
        bins = np.linspace(lb.min(), lb.max(), 30)
        idx = np.digitize(lb, bins)
        med = [
            (np.median(lb[idx == b]), np.median(ls[idx == b]))
            for b in np.unique(idx)
            if (idx == b).sum() > 50
        ]
        p = np.polyfit([a for a, _ in med], [s for _, s in med], 1)
        T["slope_area"] = time.time() - t
        tot = sum(T.values())
        print(
            ("compile+run" if rep == 0 else "warm run   "),
            " ".join(f"{k}={v:.2f}s" for k, v in T.items()),
            f"TOTAL={tot:.1f}s  theta_fit={-p[0]:.2f}",
        )
    rss = peak_rss_gb()
    print("peak RSS GB:", "알 수 없음" if rss is None else f"{rss:.2f}")


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 else 3600)
