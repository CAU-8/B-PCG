# 출처: 옵시디언 볼트 'VAULT/프로젝트/(캡스톤) B-PCG/figures/make_figures.py' 를 2026-10-02 에 옮긴 사본입니다.
# 예전 코드(legacy)라 ruff 검사에서 뺍니다. 바꾼 것은 그림을 저장하는 폴더(OUT) 하나뿐입니다.
# 실행: uv run python analysis/legacy/make_figures.py  -> docs/guide/figures/ 에 PNG 22장을 씁니다.
"""b-pcg 가이드 문서용 그림 생성 스크립트.

모든 그림은 가이드에 적힌 수식을 그대로 계산해서 그린다.
실행: uv run python docs/figures/make_figures.py
"""
import os
import heapq
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib import font_manager as fm
from matplotlib.colors import LightSource
from matplotlib.patches import Rectangle, Patch, FancyArrowPatch

# 저장소 루트 기준 docs/guide/figures (이 파일은 analysis/legacy/ 에 있음)
OUT = str(Path(__file__).resolve().parents[2] / "docs" / "guide" / "figures")
os.makedirs(OUT, exist_ok=True)

for p in ["/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
          "/System/Library/Fonts/AppleSDGothicNeo.ttc"]:
    if os.path.exists(p):
        fm.fontManager.addfont(p)
_cands = [f.name for f in fm.fontManager.ttflist
          if "CJK" in f.name or "Apple SD Gothic" in f.name or "Nanum" in f.name]
plt.rcParams.update({
    "font.family": (sorted(_cands, key=lambda n: ("Sans" not in n, n))[0] if _cands else "sans-serif"),
    "axes.unicode_minus": False,
    "savefig.dpi": 150, "savefig.bbox": "tight",
    "axes.spines.top": False, "axes.spines.right": False,
    "font.size": 10,
})

C_LAND, C_SED, C_WATER, C_OCEAN = "#C9A77C", "#E8D7B0", "#6FA8DC", "#4A86C5"
FACE_COLORS = ["#E8A0A0", "#A0C4E8", "#A8D8A8", "#F0D090", "#C8B0E0", "#A0D8D0"]


def save(fig, name):
    fig.savefig(os.path.join(OUT, name))
    plt.close(fig)
    print("saved", name)


def smoothstep(x):
    x = np.clip(x, 0.0, 1.0)
    return x * x * (3 - 2 * x)


def wave_noise(shape_fn, n_waves, scale, seed):
    """무작위 사인파의 합으로 만든 매끄러운 결정적 노이즈 (그림용)."""
    r = np.random.default_rng(seed)
    return r, n_waves, scale


def noise1d(x, wavelength, seed, octaves=4):
    r = np.random.default_rng(seed)
    out = np.zeros_like(x, dtype=float)
    amp, tot = 1.0, 0.0
    for o in range(octaves):
        k = 2 * np.pi / (wavelength / 2 ** o)
        out += amp * np.sin(k * x * r.uniform(0.8, 1.2) + r.uniform(0, 2 * np.pi))
        tot += amp
        amp *= 0.5
    return out / tot


def noise2d(x, y, wavelength, seed, n=24):
    r = np.random.default_rng(seed)
    out = np.zeros(np.broadcast(x, y).shape)
    tot = 0.0
    for m in range(n):
        lam = wavelength * 2 ** (-r.uniform(0, 3))
        ang = r.uniform(0, 2 * np.pi)
        a = lam / wavelength
        out += a * np.sin(2 * np.pi * (np.cos(ang) * x + np.sin(ang) * y) / lam + r.uniform(0, 2 * np.pi))
        tot += a
    return out / tot * 1.6


def noise_sphere(P, freq, seed, n=40):
    """구면 위 점 P(...,3)에서 직접 샘플하는 3D 노이즈 → 이음새 없음."""
    r = np.random.default_rng(seed)
    out = np.zeros(P.shape[:-1])
    tot = 0.0
    for m in range(n):
        f = freq * 2 ** r.uniform(0, 2.5)
        k = r.normal(size=3)
        k = k / np.linalg.norm(k) * f
        a = freq / f
        out += a * np.sin(P @ k + r.uniform(0, 2 * np.pi))
        tot += a
    return out / tot * 1.6


# ---------------------------------------------------------------- 1단계
FACES = [  # (u, v, n) : u x v = n
    ((0, 1, 0), (0, 0, 1), (1, 0, 0)),     # 0 +X
    ((0, -1, 0), (0, 0, 1), (-1, 0, 0)),   # 1 -X
    ((0, 0, 1), (1, 0, 0), (0, 1, 0)),     # 2 +Y
    ((0, 0, -1), (1, 0, 0), (0, -1, 0)),   # 3 -Y
    ((1, 0, 0), (0, 1, 0), (0, 0, 1)),     # 4 +Z
    ((-1, 0, 0), (0, 1, 0), (0, 0, -1)),   # 5 -Z
]
FACE_NAMES = ["+X", "-X", "+Y", "-Y", "+Z", "-Z"]


def x_f(f, a, b, equi=True):
    u, v, n = (np.array(t, float) for t in FACES[f])
    X = np.tan(np.pi * a / 4) if equi else a
    Y = np.tan(np.pi * b / 4) if equi else b
    p = n + np.multiply.outer(X, u) + np.multiply.outer(Y, v)
    return p / np.linalg.norm(p, axis=-1, keepdims=True)


def omega(X, Y):
    return np.arctan(X * Y / np.sqrt(1 + X ** 2 + Y ** 2))


def face_areas(n, equi=True):
    t = np.linspace(-1, 1, n + 1)
    E = np.tan(np.pi * t / 4) if equi else t
    X0, X1 = E[:-1][None, :], E[1:][None, :]
    Y0, Y1 = E[:-1][:, None], E[1:][:, None]
    return omega(X1, Y1) - omega(X0, Y1) - omega(X1, Y0) + omega(X0, Y0)


def fig_sphere_grid():
    v = np.array([1.0, 0.75, 0.55]); v /= np.linalg.norm(v)
    e1 = np.cross([0, 0, 1], v); e1 /= np.linalg.norm(e1)
    e2 = np.cross(v, e1)
    fig, axs = plt.subplots(1, 2, figsize=(10, 5))
    for ax, equi, title in [(axs[0], False, "단순 정규화 (gnomonic)"),
                            (axs[1], True, "등각 매핑 (equiangular)")]:
        n = 8
        polys = []
        t = np.linspace(-1, 1, n + 1)
        for f in range(6):
            for j in range(n):
                for i in range(n):
                    s = np.linspace(0, 1, 5)
                    a0, a1, b0, b1 = t[i], t[i + 1], t[j], t[j + 1]
                    ea = np.concatenate([a0 + (a1 - a0) * s, np.full(5, a1), a1 - (a1 - a0) * s, np.full(5, a0)])
                    eb = np.concatenate([np.full(5, b0), b0 + (b1 - b0) * s, np.full(5, b1), b1 - (b1 - b0) * s])
                    P = x_f(f, ea, eb, equi)
                    c = x_f(f, np.array((a0 + a1) / 2), np.array((b0 + b1) / 2), equi)
                    depth = c @ v
                    if depth > 0:
                        polys.append((depth, P @ e1, P @ e2, FACE_COLORS[f]))
        polys.sort(key=lambda q: q[0])
        for _, xs, ys, col in polys:
            ax.fill(xs, ys, color=col, ec="white", lw=0.8)
        ax.add_patch(plt.Circle((0, 0), 1, fill=False, lw=0.8, color="#555"))
        ax.set_aspect("equal"); ax.axis("off"); ax.set_title(title)
    fig.suptitle("큐브의 여섯 면을 구에 투영한 격자 (면마다 8×8칸, 색 = 면)", y=0.98)
    save(fig, "f1_sphere_grid.png")


def fig_area_heatmap():
    n = 64
    fig, axs = plt.subplots(1, 2, figsize=(11.5, 4.2))
    fig.subplots_adjust(wspace=0.5)
    for ax, equi, title in [(axs[0], False, "단순 정규화"), (axs[1], True, "등각 매핑")]:
        A = face_areas(n, equi)
        rel = A / A.mean()
        im = ax.imshow(rel, origin="lower", extent=[-1, 1, -1, 1], cmap="viridis")
        ax.set_title(f"{title}: 최대/최소 면적 비 = {A.max() / A.min():.2f}")
        ax.set_xlabel("면 좌표 a"); ax.set_ylabel("면 좌표 b")
        fig.colorbar(im, ax=ax, label="셀 면적 / 평균")
    save(fig, "f1_area_ratio.png")
    total = 6 * face_areas(n, True).sum()
    print("  area check:", total, 4 * np.pi)


def fig_cube_net():
    fig, ax = plt.subplots(figsize=(12, 6.4))
    pos = {2: (1, 0), 1: (0, 1), 4: (1, 1), 0: (2, 1), 5: (3, 1), 3: (1, 2)}
    S = 1.0
    hi = {21: "#F0997B", 10: "#F0997B"}
    for f, (cx, cy) in pos.items():
        ox, oy = cx * 3 * S, -cy * 3 * S
        for j in range(3):
            for i in range(3):
                cid = f * 9 + j * 3 + i
                ax.add_patch(Rectangle((ox + i * S, oy - (j + 1) * S), S, S,
                                       fc=hi.get(cid, FACE_COLORS[f]), ec="white", lw=1.5))
                ax.text(ox + i * S + 0.5, oy - j * S - 0.5, str(cid), ha="center", va="center", fontsize=9)
        ax.add_patch(Rectangle((ox, oy - 3 * S), 3 * S, 3 * S, fill=False, ec="#444", lw=1.2))
    ax.add_patch(FancyArrowPatch((3.0, -1.5), (1.5, -3.0), connectionstyle="arc3,rad=0.45",
                                 arrowstyle="-|>", mutation_scale=14, color="#C0502A", lw=1.8, ls="--"))
    ax.text(0.0, -1.2, "접으면 붙는 이웃" "\n" "21번의 왼쪽 = 10번", color="#C0502A", fontsize=9)
    y0 = -10.6
    ax.text(0, y0 + 1.1, "색 = 면. 메모리에서는 이렇게 한 줄로: c = f·n² + j·n + i", fontsize=10)
    for f in range(6):
        ax.add_patch(Rectangle((f * 2.0, y0), 1.9, 0.8, fc=FACE_COLORS[f], ec="white"))
        ax.text(f * 2.0 + 0.95, y0 + 0.4, f"면{f} {FACE_NAMES[f]}: {f * 9}~{f * 9 + 8}", ha="center", va="center", fontsize=8.5)
    ox, oy = 13.6, 0.0
    for j in range(3):
        for i in range(3):
            ax.add_patch(Rectangle((ox + i, oy - (j + 1)), 1, 1, fc="#DDD", ec="white", lw=1.5))
            ax.text(ox + i + 0.5, oy - j - 0.5, str(j * 3 + i), ha="center", va="center", fontsize=9)
    ax.text(ox + 1.5, oy + 0.3, "평면 격자 (면 1개)", ha="center", fontsize=9)
    ax.text(ox - 0.2, oy - 4.3, "번호와 이웃 표만 있으면" "\n" "같은 코드로 처리된다", fontsize=9)
    ax.set_xlim(-0.3, 17.2); ax.set_ylim(-11.0, 0.9); ax.set_aspect("equal"); ax.axis("off")
    save(fig, "f1_cube_net.png")


# ---------------------------------------------------------------- 2단계 (평면 격자 수계)
NB = [(-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)]


def fill(z, ocean, eps):
    ny, nx = z.shape
    zf = z.copy()
    done = ocean.copy()
    h = [(z[j, i], j, i) for j, i in zip(*np.nonzero(ocean))]
    heapq.heapify(h)
    while h:
        zc, j, i = heapq.heappop(h)
        for dj, di in NB:
            jj, ii = j + dj, i + di
            if 0 <= jj < ny and 0 <= ii < nx and not done[jj, ii]:
                done[jj, ii] = True
                zf[jj, ii] = max(z[jj, ii], zc + eps)
                heapq.heappush(h, (zf[jj, ii], jj, ii))
    return zf


def receivers(zf, ocean, dx, prev=None, eta=0.02, pos=None):
    """D8 최급경사 수신 셀. prev가 있으면 새 후보가 (1+eta)배 이상 가파를 때만 바꾼다 (히스테리시스).
    pos(ny, nx, 2)가 주어지면 흔든(jitter) 노드 위치로 거리를 잰다."""
    ny, nx = zf.shape
    rec = np.arange(ny * nx)
    slope = np.zeros(ny * nx)
    def dist(j, i, jj, ii):
        if pos is None:
            return dx * np.hypot(jj - j, ii - i)
        return np.hypot(*(pos[jj, ii] - pos[j, i]))
    for j in range(ny):
        for i in range(nx):
            c = j * nx + i
            if ocean[j, i]:
                continue
            best, bs = c, 0.0
            for dj, di in NB:
                jj, ii = j + dj, i + di
                if 0 <= jj < ny and 0 <= ii < nx:
                    s = (zf[j, i] - zf[jj, ii]) / dist(j, i, jj, ii)
                    if s > bs:
                        bs, best = s, jj * nx + ii
            if prev is not None and prev[c] != c:
                pj, pi = divmod(prev[c], nx)
                sp = (zf[j, i] - zf[pj, pi]) / dist(j, i, pj, pi)
                if sp > 0 and sp * (1 + eta) >= bs:
                    best, bs = prev[c], sp
            rec[c], slope[c] = best, bs
    return rec, slope


def order_and_acc(rec, w):
    N = len(rec)
    donors = [[] for _ in range(N)]
    for c in range(N):
        if rec[c] != c:
            donors[rec[c]].append(c)
    order = []
    for c in range(N):
        if rec[c] == c:
            st = [c]
            while st:
                k = st.pop(); order.append(k); st.extend(donors[k])
    Q = w.astype(float).copy()
    for k in reversed(order):
        if rec[k] != k:
            Q[rec[k]] += Q[k]
    return np.array(order), Q


def fig_fill_profile():
    x = np.linspace(0, 100, 600)
    cp = [(0, -20), (10, -5), (14, 2), (25, 20), (35, 38), (42, 45), (48, 34), (54, 40),
          (60, 55), (70, 75), (78, 60), (88, 30), (95, 5), (100, -15)]
    z = np.interp(x, *zip(*cp))
    k = np.exp(-0.5 * (np.arange(-20, 21) / 7) ** 2); k /= k.sum()
    z = np.convolve(np.pad(z, 20, mode="edge"), k, mode="valid")
    zh = np.minimum(np.maximum.accumulate(z), np.maximum.accumulate(z[::-1])[::-1])
    zh = np.maximum(zh, 0)
    fig, ax = plt.subplots(figsize=(10, 3.8))
    ax.fill_between(x, -30, z, color=C_LAND, lw=0)
    ax.fill_between(x, z, 0, where=z < 0, color=C_OCEAN, alpha=0.8, lw=0)
    lake = (zh > z + 1e-6) & (z > 0)
    ax.fill_between(x, z, zh, where=lake, color=C_WATER, lw=0)
    ax.plot(x, zh, "--", color="#1B4F8C", lw=1.2, label=r"채움 고도 $\hat{z}$ (넘침 고도)")
    ax.plot(x, z, color="#6B4E2E", lw=1.5, label="원래 고도 z")
    xs = x[np.argmax(np.where(x < 48, z, -1e9))]
    ax.annotate("넘침 지점: 물이 여기로\n넘어서 바다로 간다", xy=(xs, z.max() * 0 + zh[lake].max()), xytext=(22, 62),
                arrowprops=dict(arrowstyle="->", color="#333"), fontsize=9)
    ax.text(49, 49, r"호수 ($\hat{z} > z$ 인 곳)", ha="center", fontsize=9, color="#0B3D6E")
    ax.text(3, -12, "바다 O", color="white", fontsize=9)
    ax.set_ylim(-30, 85); ax.set_xlabel("거리"); ax.set_ylabel("고도")
    ax.legend(loc="upper right", frameon=False)
    ax.set_title("채움(priority-flood): 웅덩이는 넘침 지점 높이까지 물이 찬다")
    save(fig, "f2_fill_profile.png")


def hillshade(z, dx):
    return LightSource(azdeg=315, altdeg=40).hillshade(z, vert_exag=1.5, dx=dx, dy=dx)


# ---------------------------------------------------------------- 4단계 (정상상태 솔버, 평면)
def steady_state_demo(jitter=0.0):
    n, dx = 96, 400.0
    jj, ii = np.mgrid[0:n, 0:n]
    rj = np.random.default_rng(99)
    pos = (np.stack([jj, ii], -1) + rj.uniform(-jitter, jitter, (n, n, 2))) * dx if jitter > 0 else None
    X, Y = (ii - n / 2) / (n / 2), (jj - n / 2) / (n / 2)
    dome = np.clip(1 - (X ** 2 + Y ** 2) * 0.9, 0, 1)
    nz = noise2d(X, Y, 0.9, seed=3)
    z0 = 700 * dome + 180 * nz
    ocean = z0 < np.quantile(z0, 0.28)
    ocean[0, :] = ocean[-1, :] = ocean[:, 0] = ocean[:, -1] = True
    Ut = np.clip(dome + 0.25 * noise2d(X, Y, 0.6, seed=11), 0, 1)
    ks = np.exp((1 - Ut) * np.log(60) + Ut * np.log(250)).ravel()
    theta, scrit = 0.45, 0.6
    w = np.full(n * n, dx * dx)  # P = 1 m/yr
    z = np.where(ocean, 0.0, z0)
    changes, prev = [], None
    coords = np.stack([jj.ravel(), ii.ravel()], 1)
    snapshots = {}
    for it in range(60):
        zf = fill(z, ocean, 1e-3)
        rec, _ = receivers(zf, ocean, dx, prev, pos=pos)
        order, Q = order_and_acc(rec, w)
        if prev is not None:
            ch = int(np.sum(rec != prev)); changes.append(ch)
            if ch == 0:
                break
        prev = rec.copy()
        zn = np.zeros(n * n)
        for c in order:
            r = rec[c]
            if r == c:
                continue
            d = dx * np.hypot(*(coords[c] - coords[r])) if pos is None else np.hypot(*(pos.reshape(-1, 2)[c] - pos.reshape(-1, 2)[r]))
            zn[c] = zn[r] + d * min(ks[c] * Q[c] ** -theta, scrit)
        z = zn.reshape(n, n)
        if it == 0:
            snapshots["first"] = z.copy()
    zf = fill(z, ocean, 1e-3)
    rec, slope = receivers(zf, ocean, dx, prev, pos=pos)
    order, Q = order_and_acc(rec, w)
    return dict(n=n, dx=dx, z0=np.where(ocean, 0, z0), z=z, ocean=ocean, rec=rec, Q=Q, pos=pos, zf=zf,
                slope=slope, changes=changes, ks=ks, theta=theta, scrit=scrit, coords=coords)


def draw_rivers(ax, R, qmin):
    n, rec, Q, coords = R["n"], R["rec"], R["Q"], R["coords"]
    lq = np.log10(Q)
    for c in range(n * n):
        if Q[c] >= qmin and rec[c] != c:
            (j0, i0), (j1, i1) = coords[c], coords[rec[c]]
            ax.plot([i0, i1], [j0, j1], color="#1F5FA8", lw=0.4 + 1.6 * (lq[c] - np.log10(qmin)) / (lq.max() - np.log10(qmin)),
                    solid_capstyle="round")


def terrain_img(ax, z, ocean, dx):
    hs = hillshade(z, dx)
    zn = np.clip(z / max(z.max(), 1), 0, 1)
    base = plt.cm.gist_earth(0.35 + 0.6 * zn)[..., :3]
    rgb = base * (0.45 + 0.55 * hs[..., None])
    rgb[ocean] = np.array([0.36, 0.56, 0.8])
    ax.imshow(rgb, origin="lower")
    ax.set_xticks([]); ax.set_yticks([]); ax.set_frame_on(False)


def fig_d8(R):
    n, dx = R["n"], R["dx"]
    z0, ocean = R["z0"], R["ocean"]
    zf = fill(z0, ocean, 1e-3)
    rec, _ = receivers(zf, ocean, dx)
    order, Q = order_and_acc(rec, np.full(n * n, dx * dx))
    fig, axs = plt.subplots(1, 2, figsize=(11, 5.2))
    # 확대: 수신 셀 화살표
    j0, i0, m = 40, 52, 12
    sub = zf[j0:j0 + m, i0:i0 + m]
    axs[0].imshow(sub, origin="lower", cmap="gist_earth", extent=[-0.5, m - 0.5, -0.5, m - 0.5])
    for j in range(m):
        for i in range(m):
            c = (j0 + j) * n + (i0 + i)
            r = rec[c]
            if r != c:
                rj, ri = divmod(r, n)
                axs[0].annotate("", xy=(ri - i0, rj - j0), xytext=(i, j),
                                arrowprops=dict(arrowstyle="->", lw=0.4 + 0.5 * np.log10(Q[c] / (dx * dx) + 1), color="#123"))
    axs[0].set_title("확대: 각 칸의 화살표 = 수신 셀 r(c)\n(가장 가파르게 내려가는 이웃, 굵기 = 유량)")
    axs[0].set_xticks([]); axs[0].set_yticks([])
    axs[1].set_frame_on(False)
    lq = np.log10(Q.reshape(n, n) / (dx * dx))
    lq[ocean] = np.nan
    im = axs[1].imshow(lq, origin="lower", cmap="Blues")
    axs[1].add_patch(Rectangle((i0 - 0.5, j0 - 0.5), m, m, fill=False, ec="red", lw=1.2))
    axs[1].set_title("유량 Q (log, 셀 개수 단위): 물이 모이는 곳이 강")
    axs[1].set_xticks([]); axs[1].set_yticks([])
    fig.colorbar(im, ax=axs[1], label=r"$\log_{10}$(상류 셀 개수)")
    save(fig, "f2_d8_accumulation.png")


def fig_steady(R):
    fig, axs = plt.subplots(1, 3, figsize=(15, 5), gridspec_kw=dict(width_ratios=[1, 1, 0.8]))
    terrain_img(axs[0], R["z0"], R["ocean"], R["dx"])
    axs[0].set_title(r"시작: 융기 + 노이즈로 만든 $z^{(0)}$")
    terrain_img(axs[1], R["z"], R["ocean"], R["dx"])
    draw_rivers(axs[1], R, qmin=R["dx"] ** 2 * 30)
    axs[1].set_title(f"결과: 정상상태 지형 (최고 {R['z'].max():.0f} m)\n파란 선 = 강 (굵기 = 유량)")
    ch = R["changes"]
    axs[2].plot(range(1, len(ch) + 1), np.maximum(ch, 0.8), "o-", color="#534AB7")
    axs[2].annotate("0 → 수렴", xy=(len(ch), 0.8), xytext=(len(ch) - 9, 3), fontsize=9, arrowprops=dict(arrowstyle="->"))
    axs[2].set_yscale("log"); axs[2].set_xlabel("반복 횟수 k"); axs[2].set_ylabel("바뀐 수신 셀 수")
    axs[2].set_title("수렴: 수신 셀이 더 안 바뀌면 끝")
    save(fig, "f4_steady_state.png")


def fig_slope_area(R):
    ocean = R["ocean"].ravel()
    A, S = R["Q"][~ocean], R["slope"][~ocean]
    ok = S > 0
    A, S = A[ok], S[ok]
    fig, ax = plt.subplots(figsize=(8, 5))
    ax.scatter(A, S, s=3, alpha=0.25, color="#888", label="셀")
    bins = np.logspace(np.log10(A.min()), np.log10(A.max()), 22)
    idx = np.digitize(A, bins)
    bx, by = [], []
    for b in range(1, len(bins)):
        sel = idx == b
        if sel.sum() >= 5:
            bx.append(np.sqrt(bins[b - 1] * bins[b])); by.append(np.median(S[sel]))
    bx, by = np.array(bx), np.array(by)
    ax.plot(bx, by, "o", color="#D85A30", label="구간 중앙값")
    Acrit = R["dx"] ** 2 * 8
    sel = (bx >= Acrit) & (by < R["scrit"] * 0.95)
    th, lk = np.polyfit(np.log10(bx[sel]), np.log10(by[sel]), 1)
    xx = np.logspace(np.log10(Acrit), np.log10(A.max()), 50)
    ax.plot(xx, 10 ** lk * xx ** th, "-", color="#534AB7", lw=2, label=f"적합: θ = {-th:.2f} (입력 θ = {R['theta']})")
    ax.axhline(R["scrit"], ls="--", color="#555", lw=1)
    ax.text(A.min() * 1.3, R["scrit"] * 1.12, r"$S_{crit}$ (사면 한계 경사)", fontsize=9)
    ax.axvline(Acrit, ls=":", color="#999")
    ax.text(Acrit * 1.1, S.min() * 2, r"$A_{crit}$" "\n" "(하천 시작)", fontsize=9, color="#666")
    ax.set_xscale("log"); ax.set_yscale("log")
    ax.set_xlabel("상류 유역 면적 A [m²]"); ax.set_ylabel("경사 S")
    ax.set_title("경사–면적 그래프: 로그 눈금에서 직선 → 기울기가 −θ")
    ax.legend(frameon=False, loc="lower left")
    save(fig, "f3_slope_area.png")


def fig_theta_profiles():
    L = np.linspace(0, 20000, 800)  # 분수령부터의 거리 [m]
    A = 1.4e6 * np.maximum(L / 1000, 0.05) ** 1.6
    fig, ax = plt.subplots(figsize=(8, 4.2))
    for th, col in [(0.3, "#1D9E75"), (0.45, "#534AB7"), (0.6, "#D85A30")]:
        S = A ** -th
        z = np.cumsum((S * np.gradient(L))[::-1])[::-1]
        z = z - z[-1]
        ax.plot(L / 1000, z / z.max(), color=col, lw=2, label=f"θ = {th}")
    ax.set_xlabel("분수령(상류 끝)으로부터의 거리 [km]"); ax.set_ylabel("정규화한 고도")
    ax.set_title("θ가 클수록 강의 종단면이 더 오목하다 (상류는 급하고 하류는 완만)")
    ax.legend(frameon=False)
    save(fig, "f3_theta_profiles.png")


# ---------------------------------------------------------------- 5단계 (행성 입력)
def planet_fields():
    nlon, nlat = 360, 180
    lon = np.linspace(-np.pi, np.pi, nlon, endpoint=False) + np.pi / nlon
    lat = np.linspace(-np.pi / 2, np.pi / 2, nlat, endpoint=False) + np.pi / (2 * nlat)
    LON, LAT = np.meshgrid(lon, lat)
    P = np.stack([np.cos(LAT) * np.cos(LON), np.cos(LAT) * np.sin(LON), np.sin(LAT)], -1)
    r = np.random.default_rng(21)
    M = 9
    seeds = r.normal(size=(M, 3)); seeds /= np.linalg.norm(seeds, axis=1, keepdims=True)
    warp = np.stack([noise_sphere(P, 3, s) for s in (1, 2, 3)], -1)
    Pw = P + 0.25 * warp
    Pw /= np.linalg.norm(Pw, axis=-1, keepdims=True)
    plate = np.argmax(Pw @ seeds.T, axis=-1)
    omega_k = r.normal(size=(M, 3))
    cont = r.random(M) < 0.45
    vel = np.cross(omega_k[plate], P)
    btype = np.zeros(plate.shape, int)
    gam = np.zeros(plate.shape)
    for dj, di in [(0, 1), (1, 0)]:
        pn = np.roll(plate, -di, axis=1) if di else np.roll(plate, -dj, axis=0)
        Pn = np.roll(P, -di, axis=1) if di else np.roll(P, -dj, axis=0)
        vn = np.cross(omega_k[pn], P)
        diff = pn != plate
        if dj:
            diff[-1, :] = False
        b = Pn - (np.sum(Pn * P, -1, keepdims=True)) * P
        b /= np.linalg.norm(b, axis=-1, keepdims=True) + 1e-12
        dv = vn - vel
        g = -np.sum(dv * b, -1)
        tau = np.abs(np.sum(dv * np.cross(P, b), -1))
        t = np.where(g > 0.3 * tau, 1, np.where(g < -0.3 * tau, 2, 3))
        btype = np.where(diff & (btype == 0), t, btype)
        gam = np.where(diff & (gam == 0), g, gam)
    conv = np.argwhere(btype == 1)
    if len(conv) > 2500:
        conv = conv[r.choice(len(conv), 2500, replace=False)]
    Pc = P[conv[:, 0], conv[:, 1]]
    gc = gam[conv[:, 0], conv[:, 1]]
    flat = P.reshape(-1, 3)
    delta = np.empty(len(flat)); gstar = np.empty(len(flat))
    for s in range(0, len(flat), 4000):
        dots = np.clip(flat[s:s + 4000] @ Pc.T, -1, 1)
        k = np.argmax(dots, 1)
        delta[s:s + 4000] = np.arccos(dots[np.arange(len(k)), k]); gstar[s:s + 4000] = gc[k]
    delta, gstar = delta.reshape(LAT.shape), gstar.reshape(LAT.shape)
    ubase = np.where(cont[plate], 0.35, 0.0)
    U = np.clip(ubase + 0.75 * gstar / gc.max() * np.exp(-delta ** 2 / (2 * 0.07 ** 2))
                + 0.12 * noise_sphere(P, 4, 9), 0, 1)
    z0 = U + 0.15 * noise_sphere(P, 5, 17)
    wgt = np.cos(LAT).ravel()
    srt = np.argsort(z0.ravel())
    cw = np.cumsum(wgt[srt]) / wgt.sum()
    h = z0.ravel()[srt][np.searchsorted(cw, 0.6)]
    return dict(LON=LON, LAT=LAT, P=P, plate=plate, btype=btype, vel=vel, U=U, z0=z0, h=h, cont=cont)


def fig_plates(F):
    LON, LAT = np.degrees(F["LON"]), np.degrees(F["LAT"])
    fig, axs = plt.subplots(1, 2, figsize=(14, 4.3))
    cmap = plt.cm.Pastel1
    axs[0].imshow(F["plate"] % 9, origin="lower", extent=[-180, 180, -90, 90], cmap=cmap, interpolation="nearest")
    for t, col, lab in [(1, "#C0302A", "수렴 (산맥)"), (2, "#1F5FA8", "발산 (열곡, 해령)"), (3, "#555", "변환 (어긋남)")]:
        m = F["btype"] == t
        axs[0].scatter(LON[m], LAT[m], s=1.2, color=col, label=lab)
    s = (slice(6, None, 12), slice(6, None, 12))
    lon, lat = F["LON"][s], F["LAT"][s]
    v = F["vel"][s]
    east = np.stack([-np.sin(lon), np.cos(lon), 0 * lon], -1)
    north = np.stack([-np.sin(lat) * np.cos(lon), -np.sin(lat) * np.sin(lon), np.cos(lat)], -1)
    axs[0].quiver(np.degrees(lon), np.degrees(lat), np.sum(v * east, -1), np.sum(v * north, -1),
                  color="#333", width=0.0018, scale=40)
    axs[0].set_title("판과 경계 유형 (화살표 = 판의 이동 속도 ω×p)")
    axs[0].legend(loc="lower left", fontsize=8, markerscale=6, frameon=True)
    im = axs[1].imshow(F["U"], origin="lower", extent=[-180, 180, -90, 90], cmap="YlOrBr")
    axs[1].contour(LON, LAT, F["z0"], levels=[F["h"]], colors="#1F5FA8", linewidths=1)
    axs[1].set_title("융기 Ũ (파란 선 = 해안선, 바다 비율 60%)")
    fig.colorbar(im, ax=axs[1], fraction=0.025)
    for ax in axs:
        ax.set_xlabel("경도 [°]"); ax.set_ylabel("위도 [°]")
    save(fig, "f5_plates_uplift.png")


def fig_precip():
    phi = np.linspace(-90, 90, 721)
    P = 0.15 + 1.0 * np.exp(-(phi / 12) ** 2) + 0.6 * np.exp(-((np.abs(phi) - 50) / 12) ** 2)
    fig, ax = plt.subplots(figsize=(8, 3.8))
    ax.plot(phi, P, color="#1F5FA8", lw=2)
    ax.fill_between(phi, 0, P, color="#1F5FA8", alpha=0.12)
    for x, t in [(0, "적도 수렴대\n(비 많음)"), (30, "아열대 고압대\n(사막)"), (50, "중위도 편서풍\n(비 다시 많음)"), (80, "극지방\n(건조)")]:
        y = np.interp(x, phi, P)
        ax.annotate(t, xy=(x, y), xytext=(x, y + 0.35), ha="center", fontsize=8,
                    arrowprops=dict(arrowstyle="-", color="#999"))
    ax.set_xlabel("위도 φ [°]"); ax.set_ylabel(r"상대 강수량 $P/P_0$")
    ax.set_ylim(0, 1.75)
    ax.set_title("위도별 강수: 봉우리 두 개만 넣으면 사막대와 극지방 건조대는 저절로 생긴다")
    save(fig, "f5_precip.png")


# ---------------------------------------------------------------- 6단계 (파생 맵)
def fig_derived_profile():
    x = np.linspace(0, 20, 2001)
    cp = [(0, -120), (1.5, -40), (2.5, 0), (4, 300), (5.5, 520), (7, 380), (8.3, 150), (9, 120), (9.7, 150),
          (11, 420), (12.5, 610), (13.6, 470), (14.4, 395), (15.2, 370), (16.0, 395), (16.6, 440), (18, 600), (20, 650)]
    z = np.interp(x, *zip(*cp))
    k = np.exp(-0.5 * (np.arange(-40, 41) / 14) ** 2); k /= k.sum()
    z = np.convolve(np.pad(z, 40, mode="edge"), k, mode="valid")
    ocean = x < 2.45
    lake_level = 405.0
    lake = (x > 13.9) & (x < 16.6) & (z < lake_level)
    river = np.abs(x - 9.0) < 0.12
    water = ocean | lake | river
    hw = np.where(ocean, 0.0, np.where(lake, lake_level, np.where(river, z - 2, np.nan)))
    widx = np.nonzero(water)[0]
    dist = np.abs(x[:, None] - x[widx][None, :]).min(1) * 1000
    zgw = z - np.minimum(140, 0.045 * dist)
    for _ in range(300):
        zgw[1:-1] = 0.5 * zgw[1:-1] + 0.25 * (zgw[:-2] + zgw[2:])
        zgw = np.minimum(zgw, z)
        zgw[water] = hw[water]
    rad = 150
    zp = np.pad(z, rad, mode="edge")
    zmax = np.array([zp[i:i + 2 * rad + 1].max() for i in range(len(z))])
    kb = np.exp(-0.5 * (np.arange(-120, 121) / 60) ** 2); kb /= kb.sum()
    datum = np.maximum(z, np.convolve(np.pad(zmax, 120, mode="edge"), kb, mode="valid"))
    slope = np.abs(np.gradient(z, x * 1000))
    near_river = np.exp(-((x - 9.0) / 0.45) ** 2)
    delta = np.exp(-((x - 2.9) / 0.35) ** 2)
    Hs = 45 * np.maximum(near_river, delta) * (1 - smoothstep(slope / 0.6))
    fig, ax = plt.subplots(figsize=(12, 4.6))
    ax.fill_between(x, -150, z, color=C_LAND, lw=0)
    ax.fill_between(x, z - Hs, z, where=Hs > 1, color=C_SED, lw=0)
    ax.fill_between(x, z, 0, where=ocean, color=C_OCEAN, alpha=0.85, lw=0)
    ax.fill_between(x, z, lake_level, where=lake, color=C_WATER, lw=0)
    ax.plot(x, z, color="#6B4E2E", lw=1.3, label=r"최종 고도 $z'$")
    ax.plot(np.where(ocean, np.nan, x), np.where(ocean, np.nan, datum), "--", color="#333", lw=1.2, label=r"퇴적 기준면 $z_{datum}$ (능선 포락면)")
    ax.plot(np.where(water, np.nan, x), np.where(water, np.nan, zgw), "--", color="#1F5FA8", lw=1.3, label=r"지하수면 $z_{gw}$")
    ax.annotate("", xy=(9.0, 125), xytext=(9.0, datum[900]), arrowprops=dict(arrowstyle="<->", color="#333"))
    ax.text(9.15, 300, "침식 깊이" "\n" r"$= z_{datum} - z'$", fontsize=9)
    ax.text(15.2, 425, r"호수: 수면 = $\hat{z}$", ha="center", fontsize=8, color="#0B3D6E")
    ax.text(9.0, 60, "강\n(수면 = 둑 − 0.2D)", ha="center", fontsize=8, color="#0B3D6E")
    ax.text(0.4, -90, "바다: 수면 0", color="white", fontsize=8)
    ax.annotate("삼각주 퇴적", xy=(2.9, 20), xytext=(3.4, -80), fontsize=8, arrowprops=dict(arrowstyle="->", color="#333"))
    ax.annotate("능선 아래에서\n지하수면이 깊어짐", xy=(5.5, 368), xytext=(2.6, 430), fontsize=8, color="#1F5FA8", arrowprops=dict(arrowstyle="->", color="#1F5FA8"))
    ax.set_xlim(0, 20); ax.set_ylim(-150, 760)
    ax.set_xlabel("거리 [km]"); ax.set_ylabel("고도 [m]")
    ax.legend(loc="upper left", frameon=False, fontsize=9)
    ax.set_title("파생 맵 단면: 수면, 지하수면, 기준면, 퇴적물")
    save(fig, "f6_derived_profile.png")


def fig_hydraulic():
    Q = np.logspace(-1, 4, 100)
    fig, ax = plt.subplots(figsize=(7, 3.8))
    ax.plot(Q, 4 * Q ** 0.5, color="#1F5FA8", lw=2, label=r"폭 $W = 4\,\bar{Q}^{0.5}$")
    ax.plot(Q, 0.35 * Q ** 0.4, color="#D85A30", lw=2, label=r"깊이 $D = 0.35\,\bar{Q}^{0.4}$")
    ax.set_xscale("log"); ax.set_yscale("log")
    ax.set_xlabel(r"유량 $\bar{Q}$ [m³/s]"); ax.set_ylabel("[m]")
    ax.set_title("하천 수리 기하: 유량이 100배 → 폭 10배, 깊이 약 6배")
    ax.legend(frameon=False)
    save(fig, "f6_hydraulic.png")


# ---------------------------------------------------------------- 7단계 (3D 샘플 함수의 2D 단면)
def smin(a, b, k):
    h = np.maximum(k - np.abs(a - b), 0) / k
    return np.minimum(a, b) - h * h * k / 4


def smax(a, b, k):
    return -smin(-a, -b, k)


def fig_smin():
    x, y = np.meshgrid(np.linspace(-2, 2, 500), np.linspace(-1.4, 1.4, 350))
    a = np.hypot(x + 0.6, y) - 0.8
    b = np.hypot(x - 0.7, y) - 0.6
    fig, axs = plt.subplots(1, 2, figsize=(11, 3.8))
    for ax, hard, soft, t in [(axs[0], np.minimum(a, b), smin(a, b, 0.5), "합치기: min(a,b) vs smin"),
                              (axs[1], np.maximum(a, -b), smax(a, -b, 0.5), "깎아내기: max(a,−b) vs smax")]:
        ax.contourf(x, y, soft, levels=[-10, 0], colors=[C_LAND])
        ax.contour(x, y, hard, levels=[0], colors="#333", linestyles="--", linewidths=1)
        ax.contour(x, y, soft, levels=[0], colors="#6B4E2E", linewidths=2)
        ax.set_aspect("equal"); ax.set_xticks([]); ax.set_yticks([]); ax.set_frame_on(False); ax.set_title(t)
    axs[0].text(-1.9, -1.3, "점선 = 딱딱한 버전, 실선 = 부드러운 버전 (k = 0.5)", fontsize=8)
    save(fig, "f7_smin.png")


def fig_slice():
    nx, ny = 900, 520
    xs = np.linspace(0, 3000, nx)
    ys = np.linspace(150, 800, ny)
    X, Y = np.meshgrid(xs, ys)
    dxv = np.abs(X - 1500)
    zp = 300 + 0.40 * dxv * (1 - smoothstep((dxv - 700) / 900) * 0.55)
    zp = np.minimum(zp, 670) + 25 * noise1d(X, 900, 5)
    m = smoothstep((dxv - 60) / 200)
    rho = zp + m * 14 * noise2d(X / 1000, Y / 1000, 0.25, seed=8)
    d0 = Y - rho
    W, D = 80.0, 12.0
    bank = 300 + 25 * noise1d(np.array([1500.0]), 900, 5)[0]
    driv = min(W / 2, D) * (np.sqrt((dxv / (W / 2)) ** 2 + ((Y - bank) / D) ** 2) - 1)
    d1 = smax(d0, -driv, 4.0)
    under = d1 < 0
    datum = 700 + 10 * noise1d(X, 2500, 2)
    s = datum - Y + 14 * np.sin(2 * np.pi * X / 1800) + np.where(X > 2380, 55, 0)
    T = [110, 260, 360]
    lay = np.digitize(s, T)  # 0 사암, 1 석회암, 2 셰일, 3 화강암
    Hsed = 1.5 + 11 * np.exp(-(dxv / 110) ** 2)
    sed = (rho - Y) < Hsed
    zgw = 297 + 0.19 * np.minimum(dxv, 1100) + 6 * noise1d(X, 700, 4)
    ycave = X / 1000, 2.6 * s / 1000
    nu = noise2d(ycave[0], ycave[1], 0.28, seed=31)
    dcave = 60 * (np.abs(nu) - 0.2)
    M = (lay == 1) * np.exp(-((Y - zgw) ** 2) / (2 * 45 ** 2))
    dcave_t = dcave + 200 * (1 - M)
    d = np.maximum(d1, -dcave_t)
    hw = np.where(dxv < W / 2, bank - 2, -np.inf)
    water = (d > 0) & (Y < np.where(under, zgw, hw))
    cols = {"air": "#F4F7FA", "water": "#5B9BD5", "sed": "#E8D7B0",
            0: "#E6C68A", 1: "#D8D5CA", 2: "#9A8E80", 3: "#C49A8E"}
    img = np.zeros((ny, nx, 3))
    def rgb(h):
        return np.array([int(h[i:i + 2], 16) for i in (1, 3, 5)]) / 255
    for L in range(4):
        img[lay == L] = rgb(cols[L])
    img[sed & (d <= 0)] = rgb(cols["sed"])
    img[d > 0] = rgb(cols["air"])
    img[water] = rgb(cols["water"])
    fig, ax = plt.subplots(figsize=(12, 6.2))
    ax.imshow(img, origin="lower", extent=[0, 3000, 150, 800], aspect="auto")
    ax.plot(xs, datum[0], "--", color="#333", lw=1)
    zg_line = np.where(under[np.clip(np.searchsorted(ys, zgw[0]), 0, ny - 1), np.arange(nx)], zgw[0], np.nan)
    ax.plot(xs, zg_line, "--", color="#1F5FA8", lw=1)
    ax.text(40, 712, r"퇴적 기준면 $z_{datum}$ (지층은 여기서부터 잰다)", fontsize=9)
    ax.text(40, zgw[0, 10] + 30, r"지하수면 $z_{gw}$", fontsize=9, color="#1F5FA8")
    ax.annotate("강 (하도 SDF로 깎음)", xy=(1500, bank - 3), xytext=(1580, 230), fontsize=9,
                arrowprops=dict(arrowstyle="->", color="#333"))
    ax.annotate("단층: 지층이\n55 m 어긋남", xy=(2385, 400), xytext=(2480, 250), fontsize=9,
                arrowprops=dict(arrowstyle="->", color="#333"))
    ax.text(1130, 560, "하곡 벽에\n지층이 드러남", fontsize=9, ha="center")
    handles = [Patch(color=cols[0], label="사암"), Patch(color=cols[1], label="석회암 (동굴 가능)"),
               Patch(color=cols[2], label="셰일"), Patch(color=cols[3], label="화강암"),
               Patch(color=cols["sed"], label="퇴적층·토양"), Patch(color=cols["water"], label="물"),
               Patch(color=cols["air"], label="빈 공간", ec="#999")]
    ax.legend(handles=handles, loc="lower left", fontsize=8, frameon=True, ncol=4)
    ax.set_xlabel("수평 거리 [m]"); ax.set_ylabel("고도 [m]")
    ax.set_title("sample(x)로 계산한 수직 단면: 동굴은 석회암 안, 지하수면 근처에만 생기고 그 아래는 잠긴다")
    save(fig, "f7_slice.png")


# ---------------------------------------------------------------- 8단계 (엔진)
def fig_ulp():
    x = np.logspace(0, 7.2, 1000)
    ulp = 2.0 ** (np.floor(np.log2(x)) - 23)
    fig, ax = plt.subplots(figsize=(8, 4))
    ax.step(x, ulp, where="post", color="#534AB7", lw=1.8)
    ax.set_xscale("log"); ax.set_yscale("log")
    ax.axhline(1e-3, ls="--", color="#999")
    ax.text(1.5, 1.3e-3, "1 mm", color="#666", fontsize=9)
    for xv, t, off in [(8192, "8 km → 약 1 mm", (1 / 30, 4)), (1e5, "100 km → 약 8 mm", (1 / 30, 4)),
                       (6.4e6, "6400 km → 0.5 m", (1 / 400, 0.15))]:
        yv = 2.0 ** (np.floor(np.log2(xv)) - 23)
        ax.plot(xv, yv, "o", color="#D85A30")
        ax.annotate(t, xy=(xv, yv), xytext=(xv * off[0], yv * off[1]), fontsize=9, arrowprops=dict(arrowstyle="->", color="#999"))
    ax.set_xlabel("원점에서의 거리 [m]"); ax.set_ylabel("float32로 표현 가능한 최소 간격 [m]")
    ax.set_title("멀어질수록 float32 좌표가 거칠어진다 → 카메라를 원점으로 두고 그린다")
    save(fig, "f8_ulp.png")


def fig_transport():
    """극을 지나는 대원 경로를 옆에서 본 그림 (경로가 x-z 평면에 있으므로 2D로 충분)."""
    t = np.radians(np.linspace(20, 160, 9))
    arc = np.radians(np.linspace(0, 180, 200))
    fig, axs = plt.subplots(1, 2, figsize=(11, 4.4))
    for k, ax in enumerate(axs):
        ax.plot(np.cos(np.linspace(0, 2 * np.pi, 300)), np.sin(np.linspace(0, 2 * np.pi, 300)), color="#CCC", lw=1)
        ax.plot(np.cos(arc), np.sin(arc), color="#333", lw=2)
        ax.plot(0, 1, "o", color="#333"); ax.text(0, 1.1, "북극", ha="center", fontsize=9)
        for tt in t:
            p = np.array([np.cos(tt), np.sin(tt)])
            fwd = np.array([-np.sin(tt), np.cos(tt)])
            if k == 0:
                q, col = (fwd, "#1F5FA8") if tt < np.pi / 2 else (-fwd, "#C0302A")
            else:
                q, col = fwd, "#1D9E75"
            ax.annotate("", xy=p + 0.3 * q, xytext=p, arrowprops=dict(arrowstyle="-|>", color=col, lw=2))
        ax.annotate("", xy=(-1.25, 0.2), xytext=(1.25, 0.2), arrowprops=dict(arrowstyle="-|>", color="#999", lw=1, ls="--"))
        ax.text(0, 0.05, "플레이어가 이동하는 방향", ha="center", fontsize=8, color="#777")
        ax.set_aspect("equal"); ax.axis("off"); ax.set_xlim(-1.5, 1.5); ax.set_ylim(-0.3, 1.45)
    axs[0].set_title("위경도로 구한 '북쪽' 기준 방향\n극을 지나는 순간 180° 뒤집힌다 (파랑 → 빨강)", fontsize=10)
    axs[1].set_title("평행 이동으로 넘겨받은 방향\n극에서도 끊김 없이 이어진다", fontsize=10)
    save(fig, "f8_transport.png")


# ---------------------------------------------------------------- 2·4장: 적응형 MFD 솔버 (채택안)
def mfd_route(zf, ocean, dx, w, cellA, pmin=1.1, pmax=8.0, n1=10, n2=300):
    """적응형 지수 MFD. 높은 셀부터 처리하면서 그 셀의 유량으로 지수 p를 정하고, 아래 이웃들에 나눠 보낸다."""
    ny, nx = zf.shape
    N = ny * nx
    order = np.argsort(-zf.ravel(), kind="stable")       # 높은 곳 → 낮은 곳
    Q = w.astype(float).copy()
    W = [None] * N
    for c in order:
        j, i = divmod(c, nx)
        if ocean[j, i]:
            continue
        L = []
        for dj, di in NB:
            jj, ii = j + dj, i + di
            if 0 <= jj < ny and 0 <= ii < nx:
                d = dx * np.hypot(dj, di)
                sl = (zf[j, i] - zf[jj, ii]) / d
                if sl > 0:
                    L.append((jj * nx + ii, sl, d, 0.5 if (dj == 0 or di == 0) else 0.354))
        x = (np.log10(Q[c] / cellA) - np.log10(n1)) / (np.log10(n2) - np.log10(n1))
        p = pmin + (pmax - pmin) * smoothstep(x)
        sl = np.array([t[1] for t in L]); sl = sl / sl.max()
        wt = np.array([t[3] for t in L]) * sl ** p
        wt /= wt.sum()
        W[c] = [(t[0], wi, t[2]) for t, wi in zip(L, wt)]
        for k, wi, _ in W[c]:
            Q[k] += wi * Q[c]
    return W, Q, order


def mfd_integrate(W, order, Q, ks, theta, scrit, shape):
    z = np.zeros(len(Q))
    for c in order[::-1]:                                  # 낮은 곳 → 높은 곳
        if W[c] is None:
            continue
        S = min(ks[c] * Q[c] ** -theta, scrit)
        z[c] = sum(wi * (z[k] + d * S) for k, wi, d in W[c])
    return z.reshape(shape)


def solve_mfd_two_phase(z0, ocean, dx, ks, theta, scrit, K1=30, lam=0.3):
    n = z0.shape[0]
    w = np.full(n * n, dx * dx)
    z = np.where(ocean, 0.0, z0)
    for _ in range(K1):                                    # 1단계: 감쇠 반복으로 수계 조직
        W, Q, order = mfd_route(fill(z, ocean, 1e-3), ocean, dx, w, dx * dx)
        z = z + lam * (mfd_integrate(W, order, Q, ks, theta, scrit, z.shape) - z)
    W, Q, order = mfd_route(fill(z, ocean, 1e-3), ocean, dx, w, dx * dx)
    z = mfd_integrate(W, order, Q, ks, theta, scrit, z.shape)   # 2단계: 가중치 고정, 한 번 적분
    dom = np.arange(n * n)
    for c in range(n * n):
        if W[c]:
            dom[c] = max(W[c], key=lambda t: t[1])[0]      # 주 수신 셀 (하천 추출용)
    return z, Q, dom


def solve_d8(z0, ocean, dx, ks, theta, scrit, maxit=60):
    n = z0.shape[0]
    w = np.full(n * n, dx * dx)
    coords = np.stack(np.divmod(np.arange(n * n), n), 1)
    z, prev = np.where(ocean, 0.0, z0), None
    for _ in range(maxit):
        rec, _ = receivers(fill(z, ocean, 1e-3), ocean, dx, prev)
        order, Q = order_and_acc(rec, w)
        if prev is not None and np.all(rec == prev):
            break
        prev = rec.copy()
        zn = np.zeros(n * n)
        for c in order:
            r = rec[c]
            if r != c:
                zn[c] = zn[r] + dx * np.hypot(*(coords[c] - coords[r])) * min(ks[c] * Q[c] ** -theta, scrit)
        z = zn.reshape(n, n)
    return z, Q, rec


def grid_alignment(z, ocean, dx, steps=6, tol=5.0):
    """하천이 격자 8방향과 얼마나 나란한가. 1이면 무작위(격자 흔적 없음), 클수록 격자에 정렬."""
    n = z.shape[0]
    rec, _ = receivers(fill(z, ocean, 1e-3), ocean, dx)
    _, Q = order_and_acc(rec, np.full(n * n, dx * dx))
    devs = []
    for c in np.nonzero((Q > dx * dx * 20) & (~ocean.ravel()))[0]:
        k = c
        for _ in range(steps):
            k = rec[k]
        (j0, i0), (j1, i1) = divmod(c, n), divmod(k, n)
        if (j1 - j0) ** 2 + (i1 - i0) ** 2 < 9:
            continue
        a = np.degrees(np.arctan2(j1 - j0, i1 - i0)) % 45
        devs.append(min(a, 45 - a))
    return np.mean(np.array(devs) < tol) / (2 * tol / 45)


def fig_d8_vs_mfd():
    n, dx = 96, 400.0
    jj, ii = np.mgrid[0:n, 0:n]
    X, Y = (ii - n / 2 + 0.5) / (n / 2), (jj - n / 2 + 0.5) / (n / 2)
    ocean = np.hypot(X, Y) > 0.85
    z0 = 300 * (1 - np.hypot(X, Y)) + 5 * noise2d(X, Y, 0.3, seed=1)
    ks = np.full(n * n, 150.0)
    zd, Qd, rd = solve_d8(z0, ocean, dx, ks, 0.45, 0.6)
    zm, Qm, rm = solve_mfd_two_phase(z0, ocean, dx, ks, 0.45, 0.6)
    ga_d, ga_m = grid_alignment(zd, ocean, dx), grid_alignment(zm, ocean, dx)
    coords = np.stack(np.divmod(np.arange(n * n), n), 1)
    fig, axs = plt.subplots(1, 2, figsize=(12, 6))
    for ax, z, Q, rec, t in [(axs[0], zd, Qd, rd, f"D8: 격자 정렬 지수 {ga_d:.2f}"),
                             (axs[1], zm, Qm, rm, f"MFD (적응형 지수): 격자 정렬 지수 {ga_m:.2f}")]:
        terrain_img(ax, z, ocean, dx)
        R = dict(n=n, rec=rec, Q=Q, coords=coords)
        draw_rivers(ax, R, qmin=dx * dx * 25)
        ax.set_title(t)
    fig.suptitle("같은 원형 섬, 같은 파라미터. D8은 강이 가로·세로·대각선으로 곧게 뻗는 결이 생긴다 (지수 1 = 결 없음)", y=0.97)
    save(fig, "f2_d8_vs_mfd.png")
    print("  grid alignment D8/MFD:", round(ga_d, 2), round(ga_m, 2))


# ---------------------------------------------------------------- 면 경계 (1장·7장 추가분)
def fig_edge_continuity():
    n = 32
    A = face_areas(n)
    row = np.tile(A[n // 2] / A.max(), 4)
    lon = -45 + (np.arange(4 * n) + 0.5) * 90 / n
    u4, v4, n4 = (np.array(t, float) for t in FACES[4])
    u0, v0, n0 = (np.array(t, float) for t in FACES[0])
    bs = np.linspace(0, 0.999, 200)
    kink = []
    h = 1e-6
    for b in bs:
        p = x_f(4, np.array(1.0), np.array(b))
        d1 = p - x_f(4, np.array(1.0 - h), np.array(b)); d1 /= np.linalg.norm(d1)
        ap = np.arctan(p @ u0 / (p @ n0)) * 4 / np.pi
        bp = np.arctan(p @ v0 / (p @ n0)) * 4 / np.pi
        d2 = x_f(0, np.array(ap), np.array(bp)) - x_f(0, np.array(ap), np.array(bp + h)); d2 /= np.linalg.norm(d2)
        kink.append(np.degrees(np.arccos(np.clip(d1 @ d2, -1, 1))))
    fig, axs = plt.subplots(1, 2, figsize=(12, 3.9))
    axs[0].plot(lon, row, color="#534AB7", lw=1.8)
    for e in [45, 135, 225]:
        axs[0].axvline(e, ls=":", color="#999")
    axs[0].text(47, 0.64, "면 경계", fontsize=8, color="#666")
    axs[0].set_ylim(0.6, 1.04)
    axs[0].set_xlabel("경도 [°]"); axs[0].set_ylabel("셀 면적 / 최댓값")
    axs[0].set_title("적도를 따라 네 면을 지날 때: 경계에서 끊기지 않는다")
    axs[1].plot(bs, kink, color="#D85A30", lw=2)
    axs[1].set_xlabel("경계를 따라 잰 위치 b (0 = 면 중앙선, 1 = 꼭짓점)")
    axs[1].set_ylabel("격자선이 꺾이는 각도 [°]")
    axs[1].set_title("격자선 꺾임: 중앙선 0°, 꼭짓점 근처 약 60°")
    save(fig, "f1_edge_continuity.png")


def ghost_errors(n, F, samples=40):
    """+Z 면의 a=+1 경계 띠와 면 내부 띠에서 쌍선형 보간 최대 오차 (고스트: 복사 / 1D 재보간)."""
    cent = -1 + (2 * np.arange(n) + 1) / n
    V = F(x_f(4, cent[None, :] + 0 * cent[:, None], cent[:, None] + 0 * cent[None, :]))   # V[j, i]
    row = F(x_f(0, cent, np.full(n, 1 - 1 / n)))                                           # +X 면 경계 줄
    ap = 4 / np.pi * np.arctan(np.tan(np.pi * cent / 4) / np.tan(np.pi / 4 * (1 + 1 / n)))
    ghosts = {"copy": row, "resample": np.interp(ap, cent, row)}
    h = 2 / n
    bq = np.linspace(cent[0], cent[-1], samples * 4)
    jb = np.clip(np.searchsorted(cent, bq) - 1, 0, n - 2)
    tb = (bq - cent[jb]) / h

    def band_err(col0, col1_vals, a0):
        aq = np.linspace(a0, a0 + h, samples)
        ta = (aq - a0) / h
        err = 0.0
        for t, a in zip(ta, aq):
            c0 = col0[jb] * (1 - tb) + col0[jb + 1] * tb
            c1 = col1_vals[jb] * (1 - tb) + col1_vals[jb + 1] * tb
            est = c0 * (1 - t) + c1 * t
            true = F(x_f(4, np.full_like(bq, a), bq))
            ok = a <= 1.0
            if ok:
                err = max(err, np.max(np.abs(est - true)))
        return err
    e_int = band_err(V[:, n // 2], V[:, n // 2 + 1], cent[n // 2])
    e = {k: band_err(V[:, -1], g, cent[-1]) for k, g in ghosts.items()}
    return e_int, e["copy"], e["resample"]


def fig_ghost():
    kvec = 3 * np.array([0.3, -0.8, 0.5])
    F = lambda P: np.sin(P @ kvec)
    fig, axs = plt.subplots(1, 2, figsize=(12, 3.9), gridspec_kw=dict(width_ratios=[1.25, 1]))
    n = 8
    c = -1 + (2 * np.arange(n) + 1) / n
    ap = 4 / np.pi * np.arctan(np.tan(np.pi * c / 4) / np.tan(np.pi * (1 + 1 / n) / 4))
    ax = axs[0]
    for e in np.linspace(-1, 1, n + 1):
        ax.axvline(e, color="#DDD", lw=0.8)
    ax.plot(c, np.zeros(n), "o", color="#534AB7", ms=9, label="인접 면 첫 줄의 셀 중심")
    ax.plot(ap, np.zeros(n), "x", color="#D85A30", ms=10, mew=2, label="고스트 셀이 실제로 가리키는 위치")
    for a, b in zip(c, ap):
        ax.annotate("", xy=(b, 0.12), xytext=(a, 0.12), arrowprops=dict(arrowstyle="->", color="#999"))
    ax.set_ylim(-0.4, 0.5); ax.set_yticks([]); ax.set_xlabel("경계를 따라가는 좌표 a′ (면당 8칸, ±1 = 꼭짓점)")
    ax.set_title("복사하면 틀리는 이유: 고스트 위치가 가운데로 쏠린다 (꼭짓점 쪽은 거의 반 칸)", fontsize=10)
    ax.legend(loc="lower center", ncol=2, frameon=False, fontsize=9)
    ax.spines["left"].set_visible(False)
    ns = np.array([32, 64, 128, 256])
    res = np.array([ghost_errors(k, F) for k in ns])
    GHOST_RESULTS.update({int(k): tuple(r) for k, r in zip(ns, res)})
    ax = axs[1]
    ax.loglog(ns, res[:, 1], "o-", color="#C0302A", label="경계, 값 복사")
    ax.loglog(ns, res[:, 2], "s-", color="#1D9E75", label="경계, 1D 재보간")
    ax.loglog(ns, res[:, 0], "--", color="#555", label="면 내부 (기준)")
    ax.set_xticks(ns); ax.set_xticklabels([str(v) for v in ns]); ax.minorticks_off()
    ax.set_xlabel("면당 칸 수 n"); ax.set_ylabel("최대 쌍선형 보간 오차")
    ax.set_title("재보간하면 경계도 내부와 같은 정확도", fontsize=10)
    ax.legend(frameon=False, fontsize=8)
    save(fig, "f7_ghost.png")


GHOST_RESULTS = {}


# ---------------------------------------------------------------- 2·4장: 비교 실험 (유량만 MFD, 트리 루프 안 하이브리드)
def mfd_accumulate(zf, ocean, dx, w, p=1.1, rec=None, qc=None):
    """다중 흐름 방향 유량 누적. qc가 주어지면 Q >= qc 인 셀(하천)은 rec[c] 하나로만 보낸다 (하이브리드)."""
    ny, nx = zf.shape
    Z = zf.ravel()
    order = np.argsort(-Z, kind="stable")
    Q = w.astype(float).copy()
    oc = ocean.ravel()
    for c in order:
        if oc[c]:
            continue
        if qc is not None and Q[c] >= qc:
            Q[rec[c]] += Q[c]
            continue
        j, i = divmod(c, nx)
        tgt, wt = [], []
        for dj, di in NB:
            jj, ii = j + dj, i + di
            if 0 <= jj < ny and 0 <= ii < nx:
                s = (Z[c] - Z[jj * nx + ii]) / (dx * np.hypot(dj, di))
                if s > 0:
                    tgt.append(jj * nx + ii); wt.append(s ** p)
        wt = np.array(wt) / np.sum(wt)
        for t, f in zip(tgt, wt):
            Q[t] += Q[c] * f
    return Q


def steady_state_hybrid(qc_cells=30, p=1.1):
    """4장 솔버의 하이브리드 판: 유량은 MFD(사면)+D8(하천), 고도 적분은 최급경사 수신 셀."""
    n, dx = 96, 400.0
    jj, ii = np.mgrid[0:n, 0:n]
    X, Y = (ii - n / 2) / (n / 2), (jj - n / 2) / (n / 2)
    dome = np.clip(1 - (X ** 2 + Y ** 2) * 0.9, 0, 1)
    z0 = 700 * dome + 180 * noise2d(X, Y, 0.9, seed=3)
    ocean = z0 < np.quantile(z0, 0.28)
    ocean[0, :] = ocean[-1, :] = ocean[:, 0] = ocean[:, -1] = True
    Ut = np.clip(dome + 0.25 * noise2d(X, Y, 0.6, seed=11), 0, 1)
    ks = np.exp((1 - Ut) * np.log(60) + Ut * np.log(250)).ravel()
    theta, scrit = 0.45, 0.6
    w = np.full(n * n, dx * dx)
    qc = qc_cells * dx * dx
    coords = np.stack([jj.ravel(), ii.ravel()], 1)
    z = np.where(ocean, 0.0, z0)
    prev, hist = None, []
    for it in range(60):
        zf = fill(z, ocean, 1e-3)
        rec, _ = receivers(zf, ocean, dx, prev)
        order, _ = order_and_acc(rec, w)
        Q = mfd_accumulate(zf, ocean, dx, w, p, rec, qc)
        zn = np.zeros(n * n)
        for c in order:
            r = rec[c]
            if r != c:
                zn[c] = zn[r] + dx * np.hypot(*(coords[c] - coords[r])) * min(ks[c] * Q[c] ** -theta, scrit)
        zn = zn.reshape(n, n)
        ch = None if prev is None else int(np.sum(rec != prev))
        dz = float(np.max(np.abs(zn - z)))
        hist.append((ch, dz))
        prev, z = rec.copy(), zn
        if ch == 0 and dz < 0.01:
            break
    zf = fill(z, ocean, 1e-3)
    rec, slope = receivers(zf, ocean, dx, prev)
    Qd8 = order_and_acc(rec, w)[1]
    return dict(n=n, dx=dx, z=z, ocean=ocean, hist=hist, zf=zf, rec=rec, w=w, qc=qc, p=p, Qd8=Qd8)


def fig_mfd(R, p=1.1):
    """D8 솔버가 만든 지형 위에서 유량 누적 방식만 바꿔 비교한다 (지형은 그대로)."""
    n, dx, zf, ocean = R["n"], R["dx"], R["zf"], R["ocean"]
    w = np.full(n * n, dx * dx)
    Qd8 = R["Q"]
    Qmfd = mfd_accumulate(zf, ocean, dx, w, p)
    _, Qada, _ = mfd_route(zf, ocean, dx, w, dx * dx)
    tot = w.sum()
    for Q in (Qd8, Qmfd, Qada):
        assert abs(Q[ocean.ravel()].sum() - tot) / tot < 1e-9
    fig, axs = plt.subplots(1, 3, figsize=(15, 5.2))
    j0, i0, m = 30, 30, 40
    for ax, Q, t in [(axs[0], Qd8, "D8: 한 방향으로만 보냄\n사면에 격자 방향의 평행선"),
                     (axs[1], Qmfd, f"MFD (p = {p}): 경사 비율로 나눔\n사면은 자연스럽지만 강도 넓게 번짐"),
                     (axs[2], Qada, "적응형 MFD: 사면은 퍼지고 강은 모임\n(그래도 강 자체는 여전히 곧다)")]:
        L = np.log10(Q.reshape(n, n) / (dx * dx))
        L[ocean] = np.nan
        ax.imshow(L[j0:j0 + m, i0:i0 + m], origin="lower", cmap="Blues", vmin=0, vmax=2.5)
        ax.set_title(t, fontsize=10); ax.set_xticks([]); ax.set_yticks([]); ax.set_frame_on(False)
    save(fig, "f2_mfd_compare.png")


def river_segments(R, qmin):
    n, rec, Q = R["n"], R["rec"], R["Q"]
    P = R["pos"].reshape(-1, 2) / R["dx"] if R.get("pos") is not None else R["coords"].astype(float)
    segs = [(P[c], P[rec[c]], Q[c]) for c in range(n * n) if Q[c] >= qmin and rec[c] != c]
    return segs


def fig_jitter(R0, R1):
    qmin = R0["dx"] ** 2 * 30
    fig = plt.figure(figsize=(15, 5))
    gs = fig.add_gridspec(1, 3, width_ratios=[1, 1, 0.9])
    for k, (R, t) in enumerate([(R0, "정규 격자: 강이 0°, 45°, 90° 방향으로 곧게 뻗는다"),
                                (R1, "노드를 흔든 격자: 강이 자연스럽게 굽는다")]):
        ax = fig.add_subplot(gs[k])
        ax.imshow(np.where(R["ocean"], 1.0, np.nan), origin="lower", cmap="Blues", vmin=0, vmax=1.6)
        segs = river_segments(R, qmin)
        lq = np.log10([s[2] for s in segs])
        for (a, b, q), l in zip(segs, lq):
            ax.plot([a[1], b[1]], [a[0], b[0]], color="#1F5FA8", lw=0.3 + 1.8 * (l - lq.min()) / (lq.max() - lq.min()))
        ax.set_title(t, fontsize=10); ax.set_xticks([]); ax.set_yticks([]); ax.set_frame_on(False); ax.set_aspect("equal")
    ax = fig.add_subplot(gs[2])
    for R, col, lab in [(R0, "#C0302A", "정규 격자"), (R1, "#1D9E75", "흔든 격자")]:
        segs = river_segments(R, qmin)
        ang = np.degrees(np.arctan2([b[0] - a[0] for a, b, _ in segs], [b[1] - a[1] for a, b, _ in segs])) % 180
        ax.hist(ang, bins=36, range=(0, 180), histtype="step", lw=2, color=col, label=lab, density=True)
    ax.set_xticks([0, 45, 90, 135, 180]); ax.set_xlabel("강 구간의 방향 [°]"); ax.set_ylabel("비율")
    ax.set_title("정규 격자는 45° 간격에만 몰린다", fontsize=10); ax.legend(frameon=False)
    save(fig, "f4_jitter.png")


def fig_mfd_convergence(R0, H):
    fig, ax = plt.subplots(figsize=(7.5, 3.8))
    ax.plot(range(1, len(R0["changes"]) + 1), np.maximum(R0["changes"], 0.8), "o-", color="#534AB7", ms=4, label="D8 + 히스테리시스 (수렴)")
    ch = [c for c, _ in H["hist"] if c is not None]
    ax.plot(range(1, len(ch) + 1), ch, "s-", color="#D85A30", ms=3, label="루프 안에서 하이브리드 MFD (수렴 안 함)")
    ax.set_yscale("log"); ax.set_xlabel("반복 횟수"); ax.set_ylabel("바뀐 수신 셀 수")
    ax.set_title("MFD를 솔버 반복 안에 넣으면 흐름이 계속 흔들린다", fontsize=10)
    ax.legend(frameon=False)
    save(fig, "f4_mfd_convergence.png")


if __name__ == "__main__":
    fig_sphere_grid(); fig_area_heatmap(); fig_cube_net()
    fig_fill_profile()
    R = steady_state_demo()
    print("  steady iterations:", R["changes"])
    fig_d8(R); fig_steady(R); fig_slope_area(R); fig_theta_profiles(); fig_d8_vs_mfd()
    F = planet_fields(); fig_plates(F); fig_precip()
    fig_derived_profile(); fig_hydraulic()
    fig_smin(); fig_slice()
    fig_ulp(); fig_transport()
    fig_edge_continuity(); fig_ghost()
    print("  ghost (interior, copy, resample):", GHOST_RESULTS)
    RJ = steady_state_demo(jitter=0.35)
    print("  jittered iterations:", RJ["changes"])
    fig_jitter(R, RJ)
    fig_mfd(R)
    H = steady_state_hybrid()
    fig_mfd_convergence(R, H)
