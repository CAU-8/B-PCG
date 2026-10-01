"""3D 샘플 함수 HeroVolume: 점 하나에 '땅인가, 무슨 돌인가, 물인가'를 답합니다.

docs/pipeline.md 10장과 가이드 7장의 구현입니다. 히어로(L2, 평면) 지도들을 감싸 국소 좌표
(동, 북, 위) [m] 의 점마다 부호 거리 d (음수 = 땅 속), 재질, 물 여부를 돌려줍니다.

계산 순서 (점 x 마다)
1. 지표: d₀ = 위 − (z_s + m·A_d·ξ(x)). A_d = 2 m, ξ = fbm3(x / 40 m) (3D 라서 높이마다 다름),
   m 은 물(강 둑·호수)에서 30 m 안이면 0 이고 60 m 에서 1 이 되는 smoothstep 입니다.
2. 강바닥: 가장 가까운 강 점까지 수평 거리 ℓ, 반폭 W/2, 깊이 D, 둑 높이 z_q 로
   d_riv = min(W/2, D)·(sqrt((ℓ/(W/2))² + (v/D)²) − 1), v = 위 − z_q.
   d₁ = smax_k(d₀, −d_riv), u = d₁ < 0 ('동굴을 파기 전에 땅 속').
3. 재질: 지표 아래 깊이 < 흙 두께 → 흙(11), < 흙 + 충적층 → 충적층(0), 그 밖은 지층 기둥의 암석.
4. 동굴: 층 k 마다 d_k = max(δ_ν − r, |위 − z_k| − r, 영역 경계), δ_ν = |ν_k|/|∇ν_k| 는 수평
   2D fbm ν_k 의 0 등고선(통로 중심선)까지 거리입니다. 입구 칸에는 골짜기 쪽 수평 캡슐을 합칩니다.
   동굴 전체를 '녹는 지층 판'의 부호 거리 d_sol 및 충적층 바닥과 교차해(max) 동굴이 늘 녹는
   기반암 안에만 생기게 합니다. d = max(d₁, −d_cave).
5. 물: w = (d > 0) ∧ (위 < (u 이면 z_gw, 아니면 h_w)) (가이드 7장 물 규칙).

지도 보간: 히어로 칸 값은 흔들지 않은 칸 중심 (x₀ + (i + ½)·dx, y₀ − (j + ½)·dx) 에 있다고 보고
쌍선형 보간합니다(노드 흔들기 위치는 무시, 최대 0.2 칸). 강 폴리라인도 같은 중심을 지나게 해
골짜기 바닥과 하도가 어긋나지 않게 합니다.

명세와 다르게 정한 것(모듈 상수로 둠)
- 강 둘레 지표 맞춤: 25 m 지도에서는 대각선으로 꺾이는 강 사이의 쌍선형 지표가 둑보다 높아
  하도가 땅 속 굴이 됩니다. 그래서 강 둘레 (W/2 + 0.5·dx) 안에서는 지표를 둑 높이 쪽으로만
  내립니다(z_s' = z_s − max(z_s − z_q, 0)·(1 − sst((ℓ − W/2)/(0.5·dx)))). 하도 안(ℓ ≤ W/2)은
  둑보다 높지 않아 물이 늘 하늘로 열립니다.
- 동굴 ⊂ 녹는 암석: 명세의 '녹는 암석이 아니면 d_k = +∞' 를 연속인 부호 거리(녹는 층 판까지의
  수직 거리)로 바꿨습니다. 동굴 지붕·바닥이 층 경계에서 평평하게 잘립니다.
- 통로 폭: 명세의 |ν|·λ_c (λ_c = λ/2π) 대신 |ν|/|∇ν| 를 씁니다(cave_distance). 진폭 1 사인파에서는
  같은 값이지만 fbm 은 진폭이 작아 |ν|·λ_c 로는 층 전체가 빈 방이 됩니다.
- 충적층(선상지 등) 안에는 동굴을 두지 않습니다. 자르는 면은 충적층 바닥이고, 충적층이 1 m 보다
  얇아지면 땅 위로 서서히 올려 연속으로 둡니다.
- 동굴 층의 값이 있는 칸 밖은 영역 부호 거리 (0.5 − w_valid)·dx 로 연속적으로 닫습니다.
- 재질 표현: 빈 곳(공기·물)의 재질은 MATERIAL_EMPTY = 255 입니다. 물인지는 water 로 봅니다.
"""

import math

import numpy as np
from numba import njit, prange
from scipy import ndimage
from scipy.spatial import cKDTree

from bpcg.core.hashing import hash3
from bpcg.core.noise import fbm3
from bpcg.geology import rocks as rk
from bpcg.subsurface.water import RIVER_SURFACE_DEPTH_FRACTION

# ---------------------------------------------------------------- 명세 상수 (설정에 없는 값)
DETAIL_AMPLITUDE_M = 2.0  # A_d (pipeline.md 10장)
DETAIL_WAVELENGTH_M = 40.0  # ξ = fbm3(x / 40 m)
WATER_CALM_M = 30.0  # 물에서 30 m 안이면 m = 0 (그 뒤 30 m 동안 1 로 오름)
RIVER_DENSIFY_M = 1.0  # 강 폴리라인 점 간격 상한 [m]
# ---------------------------------------------------------------- 우리가 정한 값
RIVER_SMOOTH_K_M = 0.5  # smax_k 의 k [m] (둑 모서리를 둥글게)
CONFORM_CELLS = 0.5  # 강 둘레 지표 맞춤 폭 [칸 크기 배수]
CAVE_OCTAVES = 3  # 동굴 노이즈 ν 옥타브 수 (통로가 너무 잘게 갈라지지 않게)
CAVE_CORE_FRACTION = 0.5  # 입구 캡슐 안쪽 끝을 찾을 때 '통로 한가운데' 문턱 (δ_ν < 0.5·r)
CAVE_GRAD_STEP_M = 0.25  # ν 기울기 중앙 차분 간격 [m] (가장 짧은 옥타브 파장 λ/4 보다 훨씬 작게)
CAVE_GRAD_FLOOR = 0.05  # |∇ν| 하한 [2π/λ 배수] (안장점 근처에서 거리가 터지지 않게)
CAVE_DIST_CAP_RADII = 2.0  # δ_ν 상한 [통로 반지름 배수] (통로에서 먼 곳의 기울기 폭주를 막음)
ALLUVIUM_CLIP_BLEND_M = (
    1.0  # 동굴을 충적층 아래로 자를 때 충적층이 이보다 얇으면 자르는 면을 서서히 올림
)
LAKE_WEIGHT_MIN = 0.5  # 호수 수면 보간: 물 칸 가중치 합이 이 값 이상일 때만 물
ENTRANCE_SEARCH_WAVELENGTHS = 2.0  # 입구 캡슐 안쪽 탐색 길이 [노이즈 파장 배수]
ENTRANCE_OUT_CELLS = 4.0  # 입구 캡슐 바깥쪽(골짜기 쪽) 탐색 길이 [칸 크기 배수]
SURFACE_FIXED_POINT_ITERS = 10  # 지표 높이(d₀ = 0) 고정점 반복 수
SURFACE_BISECT_ITERS = 48  # 강 둘레 지표 높이 이분법 반복 수
CHUNK_POINTS = 1_000_000  # 한 번에 계산하는 점 수 (메모리 상한)
FAR_M = 1.0e12  # '아주 멂' (inf·0 = NaN 을 피하려고 유한값을 씀)

MATERIAL_EMPTY = 255  # 빈 곳(공기·물)의 재질 번호

_STREAM_DETAIL = 7201  # 지표 노이즈 시드 갈래
_STREAM_CAVE = 7202  # 동굴 노이즈 시드 갈래 (층 k 마다 다른 시드)

_REQUIRED = (
    "z_m",
    "soil_thickness_m",
    "alluvium_m",
    "water_table_m",
    "water_level_m",
    "is_lake",
    "is_river",
    "river_width_m",
    "river_depth_m",
    "receiver",
    "cave_entrance",
)


def _sub_seed(seed: int, stream: int, k: int = 0) -> int:
    """planet.seed 에서 용도별 노이즈 시드 (0 ≤ 값 < 2^63)."""
    h = hash3(np.int64(int(seed)), np.int64(stream), np.int64(k))
    return int(h >> np.uint64(1))


def smoothstep01(t: np.ndarray) -> np.ndarray:
    """[0, 1] 로 자른 smoothstep 3t² − 2t³."""
    t = np.clip(t, 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


# ---------------------------------------------------------------- numba 도움 함수
@njit(cache=True, inline="always")
def _smax(a: float, b: float, k: float) -> float:
    """smax_k(a, b) = −smin_k(−a, −b) = max(a, b) + h²k/4, h = max(k − |a − b|, 0)/k (가이드 7장)"""
    h = max(k - abs(a - b), 0.0) / k
    return max(a, b) + h * h * k * 0.25


@njit(cache=True, inline="always")
def _cell_frac(x: float, y: float, x0: float, y0: float, dx: float, nx: int, ny: int):
    """국소 (x, y) [m] → 칸 중심 격자의 왼쪽 위 칸 (i0, j0) 과 비율 (tx, ty). 밖은 가장자리 값."""
    fi = (x - x0) / dx - 0.5
    fj = (y0 - y) / dx - 0.5
    fi = min(max(fi, 0.0), nx - 1.0)
    fj = min(max(fj, 0.0), ny - 1.0)
    i0 = min(int(math.floor(fi)), nx - 2)
    j0 = min(int(math.floor(fj)), ny - 2)
    return i0, j0, fi - i0, fj - j0


@njit(cache=True, parallel=True)
def _columns_kernel(
    cx, cy, x0, y0, dx,
    z, soil, alluv, zgw, lake, lake_dist, caves, sb, sr,
    o_z, o_soil, o_alluv, o_zgw, o_lake, o_lake_dist, o_cave_z, o_cave_w, o_sb, o_sr,
):  # fmt: skip
    """기둥(수평 위치)마다 지도 값을 쌍선형 보간합니다.

    cx, cy: (C,) 국소 좌표 [m]. z, soil, alluv, zgw, lake_dist: (ny, nx). lake: (ny, nx) 수면
    (없으면 NaN). caves: (K, ny, nx) 동굴 층 높이 (없으면 NaN). sb: (ny, nx, L), sr: (ny, nx, L+1).
    출력 o_lake 는 물 칸 가중치 합 < LAKE_WEIGHT_MIN 이면 NaN, o_cave_w 는 값이 있는 칸 가중치 합.
    지층은 네 모서리의 암석 열이 모두 같으면 바닥을 보간하고, 다르면 가장 가까운 칸 기둥을 씁니다.
    """
    ny, nx = z.shape
    n_lv = caves.shape[0]
    n_l = sb.shape[2]
    for c in prange(cx.shape[0]):
        i0, j0, tx, ty = _cell_frac(cx[c], cy[c], x0, y0, dx, nx, ny)
        w00 = (1.0 - tx) * (1.0 - ty)
        w10 = tx * (1.0 - ty)
        w01 = (1.0 - tx) * ty
        w11 = tx * ty
        i1 = i0 + 1
        j1 = j0 + 1
        o_z[c] = w00 * z[j0, i0] + w10 * z[j0, i1] + w01 * z[j1, i0] + w11 * z[j1, i1]
        o_soil[c] = (
            w00 * soil[j0, i0] + w10 * soil[j0, i1] + w01 * soil[j1, i0] + w11 * soil[j1, i1]
        )
        o_alluv[c] = (
            w00 * alluv[j0, i0] + w10 * alluv[j0, i1] + w01 * alluv[j1, i0] + w11 * alluv[j1, i1]
        )
        o_zgw[c] = w00 * zgw[j0, i0] + w10 * zgw[j0, i1] + w01 * zgw[j1, i0] + w11 * zgw[j1, i1]
        o_lake_dist[c] = (
            w00 * lake_dist[j0, i0]
            + w10 * lake_dist[j0, i1]
            + w01 * lake_dist[j1, i0]
            + w11 * lake_dist[j1, i1]
        )
        # 호수·바다 수면: 값이 있는 모서리만 가중 평균
        ws = 0.0
        acc = 0.0
        for q in range(4):
            jj = j0 + (q >> 1)
            ii = i0 + (q & 1)
            w = w00 if q == 0 else (w10 if q == 1 else (w01 if q == 2 else w11))
            v = lake[jj, ii]
            if w > 0.0 and not math.isnan(v):
                ws += w
                acc += w * v
        o_lake[c] = acc / ws if ws >= LAKE_WEIGHT_MIN else np.nan
        # 동굴 층
        for k in range(n_lv):
            ws = 0.0
            acc = 0.0
            for q in range(4):
                jj = j0 + (q >> 1)
                ii = i0 + (q & 1)
                w = w00 if q == 0 else (w10 if q == 1 else (w01 if q == 2 else w11))
                v = caves[k, jj, ii]
                if w > 0.0 and not math.isnan(v):
                    ws += w
                    acc += w * v
            o_cave_w[c, k] = ws
            o_cave_z[c, k] = acc / ws if ws > 0.0 else np.nan
        # 지층 기둥
        same = True
        for q in range(1, 4):
            jj = j0 + (q >> 1)
            ii = i0 + (q & 1)
            for li in range(n_l + 1):
                if sr[jj, ii, li] != sr[j0, i0, li]:
                    same = False
                    break
            if not same:
                break
        if same:
            for li in range(n_l):
                o_sb[c, li] = (
                    w00 * sb[j0, i0, li]
                    + w10 * sb[j0, i1, li]
                    + w01 * sb[j1, i0, li]
                    + w11 * sb[j1, i1, li]
                )
            for li in range(n_l + 1):
                o_sr[c, li] = sr[j0, i0, li]
        else:
            jn = j0 + (1 if ty >= 0.5 else 0)
            i_n = i0 + (1 if tx >= 0.5 else 0)
            for li in range(n_l):
                o_sb[c, li] = sb[jn, i_n, li]
            for li in range(n_l + 1):
                o_sr[c, li] = sr[jn, i_n, li]


@njit(cache=True, parallel=True)
def _river_project_kernel(qx, qy, idx, pts, conn, hw, dep, zq, lev, o_l, o_hw, o_d, o_zq, o_lev):
    """KD-tree 로 찾은 가장 가까운 강 점 idx 의 앞뒤 선분에 투영해 정확한 수평 거리와 값을 구합니다.

    qx, qy: (C,) [m]. idx: (C,) 가장 가까운 점 번호 (없으면 −1). pts: (P, 2). conn: (P,) bool
    (점 i 와 i+1 이 같은 폴리라인). hw, dep, zq, lev: (P,) 반폭·깊이·둑 높이·수면 [m].
    없으면 o_l = FAR_M, 나머지는 0 입니다.
    """
    n_pts = pts.shape[0]
    for c in prange(qx.shape[0]):
        i = idx[c]
        if i < 0:
            o_l[c] = FAR_M
            o_hw[c] = 0.0
            o_d[c] = 0.0
            o_zq[c] = 0.0
            o_lev[c] = 0.0
            continue
        x = qx[c]
        y = qy[c]
        best = math.hypot(x - pts[i, 0], y - pts[i, 1])
        a = i
        t_best = 0.0
        for s in range(2):
            j = i - 1 if s == 0 else i
            if j < 0 or j + 1 >= n_pts or not conn[j]:
                continue
            ax = pts[j, 0]
            ay = pts[j, 1]
            bx = pts[j + 1, 0] - ax
            by = pts[j + 1, 1] - ay
            l2 = bx * bx + by * by
            t = 0.0 if l2 <= 0.0 else ((x - ax) * bx + (y - ay) * by) / l2
            t = min(max(t, 0.0), 1.0)
            d = math.hypot(x - ax - t * bx, y - ay - t * by)
            if d < best:
                best = d
                a = j
                t_best = t
        b = min(a + 1, n_pts - 1)
        o_l[c] = best
        o_hw[c] = hw[a] + t_best * (hw[b] - hw[a])
        o_d[c] = dep[a] + t_best * (dep[b] - dep[a])
        o_zq[c] = zq[a] + t_best * (zq[b] - zq[a])
        o_lev[c] = lev[a] + t_best * (lev[b] - lev[a])


@njit(cache=True, inline="always")
def _capsule_dist(px, py, pz, cap_a, cap_b, e):
    """점에서 캡슐 축 선분 e 까지 거리 [m] (반지름은 빼지 않음)."""
    ax = cap_a[e, 0]
    ay = cap_a[e, 1]
    az = cap_a[e, 2]
    bx = cap_b[e, 0] - ax
    by = cap_b[e, 1] - ay
    bz = cap_b[e, 2] - az
    qx = px - ax
    qy = py - ay
    qz = pz - az
    l2 = bx * bx + by * by + bz * bz
    t = 0.0 if l2 <= 0.0 else (qx * bx + qy * by + qz * bz) / l2
    t = min(max(t, 0.0), 1.0)
    return math.sqrt((qx - t * bx) ** 2 + (qy - t * by) ** 2 + (qz - t * bz) ** 2)


@njit(cache=True, parallel=True)
def _point_kernel(
    col, px, py, up, xi,
    zs, m, soil, alluv, zgw, lake_lev,
    riv_l, riv_hw, riv_d, riv_zq, riv_lev,
    sb, sr, soluble,
    cave_z, cave_w, nu_dist,
    amp, k_smooth, r_pass, dx,
    cap_a, cap_b, bx0, by0, bsize, nbx, nby, b_start, b_items,
    o_d0, o_d1, o_dcave, o_d, o_mat, o_solid, o_rock, o_water, o_hw,
):  # fmt: skip
    """점마다 SDF·재질·물 (모듈 설명 1~5). col: (M,) 기둥 번호, 기둥 값은 (C, ...) 배열.

    xi: (M,) 지표 노이즈 ξ (계산하지 않은 점은 0). nu_dist: (C, K) ν_k = 0 등고선까지 수평 거리 [m].
    cap_a, cap_b: (E, 3) 입구 캡슐 축. b_start/b_items: 캡슐 버킷 (CSR, 버킷 크기 bsize [m]).
    """
    n_l = sb.shape[1]
    n_lv = cave_z.shape[1]
    for p in prange(col.shape[0]):
        c = col[p]
        z = up[p]
        surf = zs[c] + m[c] * amp * xi[p]
        d0 = z - surf
        # 강바닥 (반타원 하도)
        hw = riv_hw[c]
        dd = riv_d[c]
        d1 = d0
        if hw > 0.0 and dd > 0.0 and riv_l[c] < FAR_M:
            a = riv_l[c] / hw
            v = (z - riv_zq[c]) / dd
            d_riv = min(hw, dd) * (math.sqrt(a * a + v * v) - 1.0)
            d1 = _smax(d0, -d_riv, k_smooth)
        # 재질 (빈 곳을 무시한 땅의 재질). rock 은 흙·충적층을 걷어 낸 지층 암석입니다.
        li = n_l
        for i in range(n_l):
            if sb[c, i] < z:
                li = i
                break
        rock = sr[c, li]
        depth = max(surf - z, 0.0)
        if depth < soil[c]:
            solid = np.uint8(rk.SOIL)
        elif depth < soil[c] + alluv[c]:
            solid = np.uint8(rk.ALLUVIUM)
        else:
            solid = rock
        # 동굴
        dmin = math.inf
        for k in range(n_lv):
            w = cave_w[c, k]
            if w > 0.0:
                t_k = max(
                    nu_dist[c, k] - r_pass,
                    abs(z - cave_z[c, k]) - r_pass,
                    (0.5 - w) * dx,
                )
                dmin = min(dmin, t_k)
        if b_items.shape[0] > 0:
            ib = min(max(int(math.floor((px[p] - bx0) / bsize)), 0), nbx - 1)
            jb = min(max(int(math.floor((py[p] - by0) / bsize)), 0), nby - 1)
            bb = jb * nbx + ib
            for q in range(b_start[bb], b_start[bb + 1]):
                e = b_items[q]
                dmin = min(dmin, _capsule_dist(px[p], py[p], z, cap_a, cap_b, e) - r_pass)
        dcave = math.inf
        if dmin < math.inf:
            # 녹는 층 판들까지의 수직 부호 거리 (안쪽 음수)
            d_sol = math.inf
            for i in range(n_l + 1):
                if soluble[sr[c, i]]:
                    top = math.inf if i == 0 else sb[c, i - 1]
                    bot = -math.inf if i == n_l else sb[c, i]
                    d_sol = min(d_sol, max(z - top, bot - z))
            # 충적층 아래(기반암 꼭대기)로 자름. 충적층이 얇아지면 자르는 면을 땅 위로 올려 연속.
            blend = min(alluv[c] / ALLUVIUM_CLIP_BLEND_M, 1.0)
            plane = surf - alluv[c] - soil[c] + (soil[c] + 2.0 * r_pass) * (1.0 - blend)
            dcave = max(dmin, d_sol, z - plane)
        d = max(d1, -dcave)
        # 물 규칙
        h_w = lake_lev[c]
        if math.isnan(h_w):
            h_w = -math.inf
        if hw > 0.0 and riv_l[c] <= hw:
            h_w = max(h_w, riv_lev[c])
        level = zgw[c] if d1 < 0.0 else h_w
        o_d0[p] = d0
        o_d1[p] = d1
        o_dcave[p] = dcave
        o_d[p] = d
        o_solid[p] = solid
        o_rock[p] = rock
        o_mat[p] = solid if d <= 0.0 else MATERIAL_EMPTY
        o_water[p] = d > 0.0 and z < level
        o_hw[p] = h_w


@njit(cache=True, parallel=True)
def _bisect_surface_kernel(zs, riv_l, riv_hw, riv_d, riv_zq, k_smooth, todo, n_iter, out):
    """강 둘레(노이즈 없음) 기둥의 지표 높이: d₁ = smax_k(위 − z_s', −d_riv) = 0 의 근 [m]."""
    for q in prange(todo.shape[0]):
        c = todo[q]
        hw = riv_hw[c]
        dd = riv_d[c]
        a = riv_l[c] / hw
        lo = min(zs[c], riv_zq[c] - dd) - 4.0 * (k_smooth + dd + 1.0)
        hi = zs[c] + k_smooth + 1.0
        for _ in range(n_iter):
            mid = 0.5 * (lo + hi)
            v = (mid - riv_zq[c]) / dd
            d_riv = min(hw, dd) * (math.sqrt(a * a + v * v) - 1.0)
            f = _smax(mid - zs[c], -d_riv, k_smooth)
            if f > 0.0:
                hi = mid
            else:
                lo = mid
        out[c] = 0.5 * (lo + hi)


# ---------------------------------------------------------------- 강 폴리라인
def catmull_rom_centripetal(ctrl: np.ndarray, step_m: float) -> tuple[np.ndarray, np.ndarray]:
    """centripetal Catmull-Rom (α = 0.5) 으로 제어점을 지나는 곡선을 step_m 이하 간격으로 촘촘히.

    ctrl: (n, 2) 제어점 [m] (n ≥ 1). 양 끝은 대칭으로 늘린 가상 점(2P₀ − P₁)을 씁니다.
    반환: (points (P, 2), span (P,) float64). span 은 제어점 번호 + 구간 안 비율(0~1)이라 제어점
    값을 선형 보간할 때 씁니다(마지막 점은 n − 1).
    """
    ctrl = np.asarray(ctrl, dtype=np.float64)
    n = ctrl.shape[0]
    if ctrl.ndim != 2 or ctrl.shape[1] != 2 or n < 1:
        raise ValueError(f"ctrl 은 (n ≥ 1, 2) 배열이어야 합니다: {ctrl.shape}")
    if not step_m > 0:
        raise ValueError(f"step_m 은 0 보다 커야 합니다: {step_m}")
    if n == 1:
        return ctrl.copy(), np.zeros(1)
    ext = np.vstack([2.0 * ctrl[0] - ctrl[1], ctrl, 2.0 * ctrl[-1] - ctrl[-2]])
    p0, p1, p2, p3 = ext[:-3], ext[1:-2], ext[2:-1], ext[3:]
    seg_len = np.linalg.norm(p2 - p1, axis=1)
    counts = np.maximum(np.ceil(seg_len / step_m).astype(np.int64), 1)
    span_id = np.repeat(np.arange(n - 1), counts)
    local = np.concatenate([np.arange(k) / k for k in counts])
    eps = 1e-9

    def knot(a, b):
        return np.maximum(np.linalg.norm(b - a, axis=1) ** 0.5, eps)

    t0 = np.zeros(n - 1)
    t1 = t0 + knot(p0, p1)
    t2 = t1 + knot(p1, p2)
    t3 = t2 + knot(p2, p3)
    s = span_id
    tt = (t1[s] + local * (t2[s] - t1[s]))[:, None]
    T0, T1, T2, T3 = t0[s, None], t1[s, None], t2[s, None], t3[s, None]
    P0, P1, P2, P3 = p0[s], p1[s], p2[s], p3[s]
    A1 = (T1 - tt) / (T1 - T0) * P0 + (tt - T0) / (T1 - T0) * P1
    A2 = (T2 - tt) / (T2 - T1) * P1 + (tt - T1) / (T2 - T1) * P2
    A3 = (T3 - tt) / (T3 - T2) * P2 + (tt - T2) / (T3 - T2) * P3
    B1 = (T2 - tt) / (T2 - T0) * A1 + (tt - T0) / (T2 - T0) * A2
    B2 = (T3 - tt) / (T3 - T1) * A2 + (tt - T1) / (T3 - T1) * A3
    C = (T2 - tt) / (T2 - T1) * B1 + (tt - T1) / (T2 - T1) * B2
    pts = np.vstack([C, ctrl[-1:]])
    span = np.concatenate([span_id + local, [n - 1.0]])
    return pts, span


def _interp_ctrl(values: np.ndarray, span: np.ndarray) -> np.ndarray:
    """제어점 값 (n,) 을 span (P,) 위치에서 선형 보간."""
    n = values.shape[0]
    i = np.minimum(np.floor(span).astype(np.int64), n - 1)
    j = np.minimum(i + 1, n - 1)
    t = span - i
    return values[i] + t * (values[j] - values[i])


class HeroVolume:
    """히어로 지도로 만든 3D 샘플 함수 (pipeline.md 10장).

    hero_state: pipeline.HeroState (평면 그래프, fields, columns, rivers). cfg: 설정
    (planet.seed, caves.*). detail_amplitude_m: 지표 노이즈 진폭 A_d [m] (검사용으로 0 가능).

    좌표는 히어로 국소 (동, 북, 위) [m] 입니다. 히어로 밖의 점은 가장자리 칸 값을 씁니다.
    """

    def __init__(self, hero_state, cfg, *, detail_amplitude_m: float = DETAIL_AMPLITUDE_M):
        graph = getattr(hero_state, "graph", None)
        fields = getattr(hero_state, "fields", None)
        columns = getattr(hero_state, "columns", None)
        if graph is None or not isinstance(fields, dict) or columns is None:
            raise ValueError("hero_state 는 graph, fields, columns 를 가진 HeroState 여야 합니다")
        if graph.kind != "flat":
            raise ValueError("HeroVolume 은 평면 히어로 그래프에서만 만듭니다")
        missing = [k for k in _REQUIRED if k not in fields]
        if missing:
            raise ValueError(f"hero_state.fields 에 필요한 필드가 없습니다: {missing}")
        if not (math.isfinite(detail_amplitude_m) and detail_amplitude_m >= 0):
            raise ValueError(f"detail_amplitude_m 은 0 이상이어야 합니다: {detail_amplitude_m}")
        ny, nx = graph.shape
        n = ny * nx
        self.ny, self.nx = int(ny), int(nx)
        self.dx = float(graph.spacing)
        self.x0, self.y0 = float(graph.origin[0]), float(graph.origin[1])
        self.seed = int(cfg.planet.seed)
        self.amp = float(detail_amplitude_m)
        cv = cfg.caves
        self.r_pass = float(cv.passage_radius_m)
        self.wavelength = float(cv.noise_wavelength_m)
        self.n_levels = int(cv.levels)
        if self.n_levels < 1:
            raise ValueError(f"caves.levels 는 1 이상이어야 합니다: {self.n_levels}")

        def img(name, dtype=np.float64):
            a = np.asarray(fields[name])
            if a.shape[:1] != (n,):
                raise ValueError(f"fields['{name}'] 의 길이가 칸 수 {n} 와 다릅니다")
            return np.ascontiguousarray(a.astype(dtype, copy=False).reshape(ny, nx))

        self.z = img("z_m")
        if not np.isfinite(self.z).all():
            raise ValueError("z_m 에 NaN 이나 inf 가 있습니다")
        self.soil = np.nan_to_num(img("soil_thickness_m"))
        self.alluvium = np.nan_to_num(img("alluvium_m"))
        self.z_gw = img("water_table_m")
        self.z_gw = np.where(np.isfinite(self.z_gw), self.z_gw, self.z)
        level = img("water_level_m")
        is_lake = img("is_lake", bool)
        is_ocean = img("is_ocean", bool) if "is_ocean" in fields else np.zeros_like(is_lake)
        self._build_lakes(level, is_lake | is_ocean)
        lv = []
        for k in range(self.n_levels):
            name = f"cave_level_{k}_m"
            lv.append(img(name) if name in fields else np.full((ny, nx), np.nan))
        self.cave_levels = np.ascontiguousarray(np.stack(lv))
        L = int(columns.n_layers)
        if columns.n_cells != n:
            raise ValueError(f"columns 의 칸 수 {columns.n_cells} 가 {n} 와 다릅니다")
        self.n_layers = L
        self.strata_bottom = np.ascontiguousarray(
            np.asarray(columns.bottom, dtype=np.float64).reshape(ny, nx, L)
        )
        self.strata_rock = np.ascontiguousarray(
            np.asarray(columns.rock, dtype=np.uint8).reshape(ny, nx, L + 1)
        )
        self.soluble = np.ascontiguousarray(rk.SOLUBLE, dtype=np.bool_)
        self._detail_seed = _sub_seed(self.seed, _STREAM_DETAIL)
        self._cave_seeds = [_sub_seed(self.seed, _STREAM_CAVE, k) for k in range(self.n_levels)]
        self._build_rivers(hero_state.rivers, fields, img)
        self._build_capsules(fields, img)

    # ------------------------------------------------------------ 만들기
    @property
    def extent(self) -> tuple[float, float, float, float]:
        """히어로 영역 (x_min, x_max, y_min, y_max) [m]."""
        return (self.x0, self.x0 + self.nx * self.dx, self.y0 - self.ny * self.dx, self.y0)

    def cell_centers(self) -> tuple[np.ndarray, np.ndarray]:
        """칸 중심 (흔들지 않은) x, y (N,) [m], 칸 번호 c = j·nx + i 순서."""
        jj, ii = np.divmod(np.arange(self.ny * self.nx), self.nx)
        return self.x0 + (ii + 0.5) * self.dx, self.y0 - (jj + 0.5) * self.dx

    def _build_lakes(self, level: np.ndarray, is_body: np.ndarray) -> None:
        """호수·바다 수면 지도(물 칸 + 한 칸 둘레로 넓힘)와 물가까지 거리 지도를 만듭니다.

        둘레 칸은 이웃 물 칸 수면의 최댓값을 받습니다. 둘레 칸의 지표는 수면 이상이므로 보간한
        수면이 땅과 만나는 곳에서 물이 자연스럽게 끝납니다.
        """
        lake = np.where(is_body & np.isfinite(level), level, np.nan)
        dil = lake.copy()
        pad = np.pad(lake, 1, constant_values=np.nan)
        ny, nx = lake.shape
        with np.errstate(invalid="ignore"):
            for dj in (-1, 0, 1):
                for di in (-1, 0, 1):
                    if dj == 0 and di == 0:
                        continue
                    nb = pad[1 + dj : 1 + dj + ny, 1 + di : 1 + di + nx]
                    dil = np.where(np.isnan(lake), np.fmax(dil, nb), dil)
        self.lake_level = np.ascontiguousarray(dil)
        if is_body.any():
            dist = ndimage.distance_transform_edt(~is_body) * self.dx - 0.5 * self.dx
            self.lake_dist = np.ascontiguousarray(np.maximum(dist, 0.0))
        else:
            self.lake_dist = np.full(lake.shape, FAR_M)

    def _build_rivers(self, rivers, fields, img) -> None:
        """강 구간을 칸 중심 제어점 → centripetal Catmull-Rom → 1 m 이하 간격 점 + KD-tree 로."""
        n = self.ny * self.nx
        xc, yc = self.cell_centers()
        z = img("z_m").ravel()
        W = np.nan_to_num(img("river_width_m").ravel())
        D = np.nan_to_num(img("river_depth_m").ravel())
        lev = img("water_level_m").ravel()
        is_river = img("is_river", bool).ravel()
        rcv = np.asarray(fields["receiver"]).astype(np.int64).ravel()
        if rcv.shape != (n,) or (n and (rcv.min() < 0 or rcv.max() >= n)):
            raise ValueError("fields['receiver'] 는 0..N-1 범위의 (N,) 배열이어야 합니다")
        pts_l, hw_l, d_l, zq_l, lev_l, conn_l = [], [], [], [], [], []
        for seg in rivers or []:
            cells = np.asarray(seg, dtype=np.int64).ravel()
            if cells.size == 0:
                continue
            if cells.min() < 0 or cells.max() >= n:
                raise ValueError("hero_state.rivers 에 범위를 벗어난 칸 번호가 있습니다")
            last = int(cells[-1])
            f_s = RIVER_SURFACE_DEPTH_FRACTION
            h = np.where(np.isfinite(lev[cells]), lev[cells], z[cells] - f_s * D[cells])
            c_hw, c_d, c_zq, c_lev = 0.5 * W[cells], D[cells], z[cells], h
            ctrl = cells
            r = int(rcv[last])
            if r != last:
                # 아래 구간의 합류점 칸 또는 강이 흘러드는 호수 칸까지 이어 붙입니다.
                ctrl = np.append(cells, r)
                if is_river[r]:
                    r_lev = lev[r] if np.isfinite(lev[r]) else z[r] - f_s * D[r]
                    add = (0.5 * W[r], D[r], z[r], r_lev)
                else:
                    r_lev = lev[r] if np.isfinite(lev[r]) else h[-1]
                    r_lev = min(r_lev, h[-1])
                    add = (c_hw[-1], c_d[-1], min(z[last], r_lev + f_s * c_d[-1]), r_lev)
                c_hw = np.append(c_hw, add[0])
                c_d = np.append(c_d, add[1])
                c_zq = np.append(c_zq, add[2])
                c_lev = np.append(c_lev, add[3])
            pts, span = catmull_rom_centripetal(
                np.stack([xc[ctrl], yc[ctrl]], axis=1), RIVER_DENSIFY_M
            )
            pts_l.append(pts)
            hw_l.append(_interp_ctrl(c_hw, span))
            d_l.append(_interp_ctrl(c_d, span))
            zq_l.append(_interp_ctrl(c_zq, span))
            lev_l.append(_interp_ctrl(c_lev, span))
            conn = np.ones(pts.shape[0], dtype=bool)
            conn[-1] = False
            conn_l.append(conn)
        if pts_l:
            self.river_pts = np.ascontiguousarray(np.vstack(pts_l))
            self.river_hw = np.ascontiguousarray(np.concatenate(hw_l))
            self.river_depth = np.ascontiguousarray(np.concatenate(d_l))
            self.river_zq = np.ascontiguousarray(np.concatenate(zq_l))
            self.river_level = np.ascontiguousarray(np.concatenate(lev_l))
            self.river_conn = np.ascontiguousarray(np.concatenate(conn_l))
            self._tree = cKDTree(self.river_pts)
            hw_max = float(self.river_hw.max())
        else:
            self.river_pts = np.zeros((0, 2))
            self.river_hw = self.river_depth = self.river_zq = self.river_level = np.zeros(0)
            self.river_conn = np.zeros(0, dtype=bool)
            self._tree = None
            hw_max = 0.0
        # 강 영향 반경: 둑 + 지표 맞춤 폭, 노이즈 감쇠(2·30 m), smax 폭 중 가장 큰 것
        self._river_reach = (
            hw_max + max(CONFORM_CELLS * self.dx, 2.0 * WATER_CALM_M, RIVER_SMOOTH_K_M) + 1.0
        )

    def _bilinear(self, image: np.ndarray, x: np.ndarray, y: np.ndarray) -> np.ndarray:
        """(ny, nx) 지도를 국소 (x, y) [m] 에서 쌍선형 보간 (밖은 가장자리 값)."""
        fi = np.clip((x - self.x0) / self.dx - 0.5, 0.0, self.nx - 1.0)
        fj = np.clip((self.y0 - y) / self.dx - 0.5, 0.0, self.ny - 1.0)
        i0 = np.minimum(np.floor(fi).astype(np.int64), self.nx - 2)
        j0 = np.minimum(np.floor(fj).astype(np.int64), self.ny - 2)
        tx, ty = fi - i0, fj - j0
        a = image
        return (
            a[j0, i0] * (1 - tx) * (1 - ty)
            + a[j0, i0 + 1] * tx * (1 - ty)
            + a[j0 + 1, i0] * (1 - tx) * ty
            + a[j0 + 1, i0 + 1] * tx * ty
        )

    def cave_noise(self, x: np.ndarray, y: np.ndarray, k: int) -> np.ndarray:
        """동굴 층 k 의 수평 2D 노이즈 ν_k(x, y) (M,) (파장 caves.noise_wavelength_m)."""
        x = np.asarray(x, dtype=np.float64)
        pts = np.stack([x, np.asarray(y, dtype=np.float64), np.zeros_like(x)], axis=1)
        if pts.shape[0] == 0:
            return np.zeros(0)
        return fbm3(pts, self._cave_seeds[k], octaves=CAVE_OCTAVES, frequency=1.0 / self.wavelength)

    def cave_distance(self, x: np.ndarray, y: np.ndarray, k: int) -> np.ndarray:
        """층 k 통로 중심선(ν_k = 0 등고선)까지 수평 거리 근사 δ_ν = |ν|/|∇ν| (M,) [m].

        명세의 |ν|·λ_c (λ_c = λ/2π) 는 진폭 1 인 사인파에서의 같은 값입니다. fbm 은 진폭과 기울기가
        그보다 작아 |ν|·λ_c 를 쓰면 통로가 몇 배 넓어져 층 전체가 빈 방이 되므로, 중앙 차분으로 잰
        실제 기울기로 나눕니다(1차 SDF 근사). |∇ν| 는 CAVE_GRAD_FLOOR·2π/λ 아래로 내리지 않고,
        δ_ν 는 2·r 에서 자릅니다(통로 밖 먼 곳에서 |ν|/|∇ν| 의 기울기가 커지는 것을 막음.
        통로 모양에는 영향이 없고 거리값만 보수적으로 작아집니다).
        """
        x = np.asarray(x, dtype=np.float64)
        y = np.asarray(y, dtype=np.float64)
        h = CAVE_GRAD_STEP_M
        m = x.size
        xs = np.concatenate([x, x + h, x - h, x, x])
        ys = np.concatenate([y, y, y, y + h, y - h])
        v = self.cave_noise(xs, ys, k)
        nu = v[:m]
        gx = (v[m : 2 * m] - v[2 * m : 3 * m]) / (2 * h)
        gy = (v[3 * m : 4 * m] - v[4 * m :]) / (2 * h)
        floor = CAVE_GRAD_FLOOR * 2.0 * math.pi / self.wavelength
        dist = np.abs(nu) / np.maximum(np.hypot(gx, gy), floor)
        return np.minimum(dist, CAVE_DIST_CAP_RADII * self.r_pass)

    def _build_capsules(self, fields, img) -> None:
        """입구 칸마다 골짜기 쪽 수평 캡슐 (pipeline.md 10장 4 '입구가 반드시 열리게').

        축은 입구 칸 중심을 지나는 높이 z_k 의 수평 선분입니다. 바깥쪽은 가장 가파르게 내려가는
        이웃 방향으로, 그 방향 지표가 z_k 아래로 내려갈 때까지(최대 ENTRANCE_OUT_CELLS 칸) 늘립니다.
        안쪽은 반대 방향으로 통로 한가운데(δ_ν < 0.5·r)를 만날 때까지(최대 2 파장) 늘려
        동굴 미로와 잇습니다. 못 찾으면 반 파장입니다.
        """
        ent = np.asarray(fields["cave_entrance"]).astype(np.int64).ravel()
        xc, yc = self.cell_centers()
        rcv = np.asarray(fields["receiver"]).astype(np.int64).ravel()
        z = self.z.ravel()
        ny, nx, dx, r = self.ny, self.nx, self.dx, self.r_pass
        a_l, b_l = [], []
        self.n_unopened = 0
        for k in range(self.n_levels):
            zk_all = self.cave_levels[k].ravel()
            cells = np.flatnonzero(((ent >> k) & 1).astype(bool) & np.isfinite(zk_all))
            if cells.size == 0:
                continue
            jj, ii = np.divmod(cells, nx)
            best = np.zeros(cells.size)
            dirx = np.zeros(cells.size)
            diry = np.zeros(cells.size)
            for dj in (-1, 0, 1):
                for di in (-1, 0, 1):
                    if dj == 0 and di == 0:
                        continue
                    j2, i2 = jj + dj, ii + di
                    ok = (j2 >= 0) & (j2 < ny) & (i2 >= 0) & (i2 < nx)
                    nb = np.where(ok, j2 * nx + i2, cells)
                    dist = dx * math.hypot(dj, di)
                    drop = np.where(ok, (z[cells] - z[nb]) / dist, -np.inf)
                    better = drop > best
                    best = np.where(better, drop, best)
                    norm = math.hypot(di, dj)
                    dirx = np.where(better, di / norm, dirx)
                    diry = np.where(better, -dj / norm, diry)  # 행 j 가 늘면 남쪽(−y)
            none = best <= 0
            if none.any():
                rr = rcv[cells[none]]
                vx = xc[rr] - xc[cells[none]]
                vy = yc[rr] - yc[cells[none]]
                nv = np.hypot(vx, vy)
                dirx[none] = np.where(nv > 0, vx / np.where(nv > 0, nv, 1), 0.0)
                diry[none] = np.where(nv > 0, vy / np.where(nv > 0, nv, 1), 0.0)
            keep = np.hypot(dirx, diry) > 0
            cells, dirx, diry = cells[keep], dirx[keep], diry[keep]
            if cells.size == 0:
                continue
            zk = zk_all[cells]
            px, py = xc[cells], yc[cells]
            step = 0.5 * r
            # 바깥쪽: 지표가 z_k 아래로 내려가는 첫 거리
            t_out_max = ENTRANCE_OUT_CELLS * dx
            ts = np.arange(0.0, t_out_max + 1e-9, step)
            sx = px[:, None] + dirx[:, None] * ts[None, :]
            sy = py[:, None] + diry[:, None] * ts[None, :]
            zs = self._bilinear(self.z, sx.ravel(), sy.ravel()).reshape(sx.shape)
            below = zs < zk[:, None]
            found = below.any(axis=1)
            t_out = np.where(found, ts[np.argmax(below, axis=1)] + r, t_out_max)
            self.n_unopened += int((~found).sum())
            # 안쪽: 통로 한가운데를 만나는 첫 거리
            t_in_max = ENTRANCE_SEARCH_WAVELENGTHS * self.wavelength
            ts = np.arange(step, t_in_max + 1e-9, step)
            sx = px[:, None] - dirx[:, None] * ts[None, :]
            sy = py[:, None] - diry[:, None] * ts[None, :]
            nu = self.cave_distance(sx.ravel(), sy.ravel(), k).reshape(sx.shape)
            valid = np.isfinite(self.cave_levels[k])
            wv = self._bilinear(valid.astype(np.float64), sx.ravel(), sy.ravel()).reshape(sx.shape)
            core = (nu < CAVE_CORE_FRACTION * r) & (wv > 0.5)
            found = core.any(axis=1)
            t_in = np.where(found, ts[np.argmax(core, axis=1)] + r, 0.5 * self.wavelength)
            a_l.append(np.stack([px - dirx * t_in, py - diry * t_in, zk], axis=1))
            b_l.append(np.stack([px + dirx * t_out, py + diry * t_out, zk], axis=1))
        if a_l:
            self.cap_a = np.ascontiguousarray(np.vstack(a_l))
            self.cap_b = np.ascontiguousarray(np.vstack(b_l))
        else:
            self.cap_a = np.zeros((0, 3))
            self.cap_b = np.zeros((0, 3))
        self._build_buckets()

    def _build_buckets(self) -> None:
        """캡슐을 수평 버킷(크기 2·dx)에 나눠 담습니다 (CSR: b_start, b_items)."""
        x_min, x_max, y_min, y_max = self.extent
        size = 2.0 * self.dx
        nbx = max(int(math.ceil((x_max - x_min) / size)), 1)
        nby = max(int(math.ceil((y_max - y_min) / size)), 1)
        self._bucket = (x_min, y_min, size, nbx, nby)
        lists: list[list[int]] = [[] for _ in range(nbx * nby)]
        r = self.r_pass
        for e in range(self.cap_a.shape[0]):
            lo_x = min(self.cap_a[e, 0], self.cap_b[e, 0]) - r
            hi_x = max(self.cap_a[e, 0], self.cap_b[e, 0]) + r
            lo_y = min(self.cap_a[e, 1], self.cap_b[e, 1]) - r
            hi_y = max(self.cap_a[e, 1], self.cap_b[e, 1]) + r
            i0 = min(max(int(math.floor((lo_x - x_min) / size)), 0), nbx - 1)
            i1 = min(max(int(math.floor((hi_x - x_min) / size)), 0), nbx - 1)
            j0 = min(max(int(math.floor((lo_y - y_min) / size)), 0), nby - 1)
            j1 = min(max(int(math.floor((hi_y - y_min) / size)), 0), nby - 1)
            for jb in range(j0, j1 + 1):
                for ib in range(i0, i1 + 1):
                    lists[jb * nbx + ib].append(e)
        counts = np.array([len(v) for v in lists], dtype=np.int64)
        self._b_start = np.concatenate([[0], np.cumsum(counts)]).astype(np.int64)
        self._b_items = np.array([e for v in lists for e in v], dtype=np.int64)

    # ------------------------------------------------------------ 기둥 값
    def _river_query(self, x: np.ndarray, y: np.ndarray) -> tuple[np.ndarray, ...]:
        """기둥마다 가장 가까운 강 (ℓ, W/2, D, z_q, h_w). 영향 반경 밖이면 ℓ = FAR_M."""
        c = x.shape[0]
        out = [np.empty(c) for _ in range(5)]
        if self._tree is None or c == 0:
            out[0][:] = FAR_M
            for a in out[1:]:
                a[:] = 0.0
            return tuple(out)
        q = np.stack([x, y], axis=1)
        _, idx = self._tree.query(q, k=1, distance_upper_bound=self._river_reach, workers=-1)
        idx = np.where(idx >= self.river_pts.shape[0], -1, idx).astype(np.int64)
        _river_project_kernel(
            x, y, idx, self.river_pts, self.river_conn, self.river_hw, self.river_depth,
            self.river_zq, self.river_level, *out,
        )  # fmt: skip
        return tuple(out)

    def columns(self, x: np.ndarray, y: np.ndarray) -> dict[str, np.ndarray]:
        """수평 위치 (x, y) (C,) [m] 마다 높이와 상관없는 값들.

        반환 dict (C,) float64: z_surface (지표 맞춤 뒤 z_s'), z_map (보간만 한 z), m (노이즈 계수),
        soil, alluvium, z_gw, lake_level (없으면 NaN), river_dist (없으면 FAR_M), river_half_width,
        river_depth, river_bank, river_level, water_dist (물가까지 [m]);
        (C, K): cave_z, cave_w, nu_dist (ν_k = 0 까지 거리 [m]);
        (C, L) strata_bottom, (C, L+1) strata_rock (uint8).
        """
        x = np.ascontiguousarray(x, dtype=np.float64)
        y = np.ascontiguousarray(y, dtype=np.float64)
        if x.shape != y.shape or x.ndim != 1:
            raise ValueError(f"x, y 는 같은 (C,) 모양이어야 합니다: {x.shape} {y.shape}")
        if not (np.isfinite(x).all() and np.isfinite(y).all()):
            raise ValueError("x, y 에 NaN 이나 inf 가 있습니다")
        C, K, L = x.shape[0], self.n_levels, self.n_layers
        o = {k: np.empty(C) for k in ("z_map", "soil", "alluvium", "z_gw", "lake_level")}
        o["lake_dist"] = np.empty(C)
        o["cave_z"] = np.empty((C, K))
        o["cave_w"] = np.empty((C, K))
        o["strata_bottom"] = np.empty((C, L))
        o["strata_rock"] = np.empty((C, L + 1), dtype=np.uint8)
        _columns_kernel(
            x, y, self.x0, self.y0, self.dx,
            self.z, self.soil, self.alluvium, self.z_gw, self.lake_level, self.lake_dist,
            self.cave_levels, self.strata_bottom, self.strata_rock,
            o["z_map"], o["soil"], o["alluvium"], o["z_gw"], o["lake_level"], o["lake_dist"],
            o["cave_z"], o["cave_w"], o["strata_bottom"], o["strata_rock"],
        )  # fmt: skip
        l, hw, dep, zq, lev = self._river_query(x, y)
        o.update(river_dist=l, river_half_width=hw, river_depth=dep, river_bank=zq, river_level=lev)
        has = (hw > 0) & (l < FAR_M)
        # 강 둘레 지표 맞춤: 둑보다 높은 지표만 둑 쪽으로 내림
        wc = CONFORM_CELLS * self.dx
        t = smoothstep01(np.maximum(l - hw, 0.0) / wc)
        excess = np.maximum(o["z_map"] - zq, 0.0)
        o["z_surface"] = np.where(has, o["z_map"] - excess * (1.0 - t), o["z_map"])
        river_gap = np.where(has, np.maximum(l - hw, 0.0), FAR_M)
        o["water_dist"] = np.minimum(o["lake_dist"], river_gap)
        o["m"] = smoothstep01((o["water_dist"] - WATER_CALM_M) / WATER_CALM_M)
        nu = np.full((C, K), FAR_M)
        for k in range(K):
            sel = np.flatnonzero(o["cave_w"][:, k] > 0)
            if sel.size:
                nu[sel, k] = self.cave_distance(x[sel], y[sel], k)
        o["nu_dist"] = nu
        return o

    # ------------------------------------------------------------ 점 계산
    def _detail_noise(self, px, py, pz) -> np.ndarray:
        pts = np.stack([px, py, pz], axis=1)
        return fbm3(pts, self._detail_seed, frequency=1.0 / DETAIL_WAVELENGTH_M)

    def _evaluate(self, cols: dict, col, px, py, up, noise_band_m: float) -> dict:
        M = col.shape[0]
        xi = np.zeros(M)
        if self.amp > 0:
            mm = cols["m"][col]
            need = mm > 0
            if math.isfinite(noise_band_m):
                need &= np.abs(up - cols["z_surface"][col]) <= noise_band_m
            sel = np.flatnonzero(need)
            if sel.size:
                xi[sel] = self._detail_noise(px[sel], py[sel], up[sel])
        out = {k: np.empty(M) for k in ("d0", "d1", "d_cave", "d", "water_level")}
        out["material"] = np.empty(M, dtype=np.uint8)
        out["solid_material"] = np.empty(M, dtype=np.uint8)
        out["rock"] = np.empty(M, dtype=np.uint8)
        out["water"] = np.empty(M, dtype=np.bool_)
        bx0, by0, bsize, nbx, nby = self._bucket
        _point_kernel(
            col, px, py, up, xi,
            cols["z_surface"], cols["m"], cols["soil"], cols["alluvium"], cols["z_gw"],
            cols["lake_level"],
            cols["river_dist"], cols["river_half_width"], cols["river_depth"], cols["river_bank"],
            cols["river_level"],
            cols["strata_bottom"], cols["strata_rock"], self.soluble,
            cols["cave_z"], cols["cave_w"], cols["nu_dist"],
            self.amp, RIVER_SMOOTH_K_M, self.r_pass, self.dx,
            self.cap_a, self.cap_b, bx0, by0, bsize, nbx, nby, self._b_start, self._b_items,
            out["d0"], out["d1"], out["d_cave"], out["d"], out["material"],
            out["solid_material"], out["rock"], out["water"], out["water_level"],
        )  # fmt: skip
        out["surface"] = cols["z_surface"][col] + cols["m"][col] * self.amp * xi
        out["under"] = out["d1"] < 0
        out["z_gw"] = cols["z_gw"][col]
        return out

    def evaluate(self, points: np.ndarray, noise_band_m: float = math.inf) -> dict:
        """점마다 계산의 모든 중간값 (검사·굽기용).

        points: (M, 3) 국소 (동, 북, 위) [m]. noise_band_m: 지표 노이즈를 계산할 높이 띠
        (|위 − z_s'| ≤ 값인 점만, 기본은 모든 점). 반환 dict (M,): d0, d1, d_cave, d (float64),
        material, solid_material, rock (흙·충적층을 뺀 지층 암석) (uint8), water, under (bool),
        water_level (물 규칙의 h_w, 없으면 −inf), surface (노이즈를 더한 지표), z_gw.
        """
        pts = self._check_points(points)
        if pts.shape[0] == 0:
            z0 = np.zeros(0)
            return self._evaluate(self.columns(z0, z0), np.zeros(0, np.int64), z0, z0, z0, 0.0)
        parts = []
        for s in range(0, pts.shape[0], CHUNK_POINTS):
            p = pts[s : s + CHUNK_POINTS]
            px, py, pz = (np.ascontiguousarray(p[:, a]) for a in range(3))
            cols = self.columns(px, py)
            col = np.arange(p.shape[0], dtype=np.int64)
            parts.append(self._evaluate(cols, col, px, py, pz, noise_band_m))
        return {k: np.concatenate([q[k] for q in parts]) for k in parts[0]}

    def sample(self, points: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
        """3D 샘플 함수 (pipeline.md 10장).

        points: (M, 3) 국소 (동, 북, 위) [m].
        반환: (d (M,) float32 부호 거리 [m] (음수 = 땅 속), material (M,) uint8 (빈 곳은 255),
        water (M,) bool).
        """
        ev = self.evaluate(points)
        return ev["d"].astype(np.float32), ev["material"], ev["water"]

    def evaluate_grid(
        self,
        x: np.ndarray,
        y: np.ndarray,
        up: np.ndarray,
        noise_band_m: float = math.inf,
        keys: tuple[str, ...] = ("d", "material", "water"),
    ) -> dict:
        """수평 기둥 (x, y) (C,) 와 기둥마다 높이 up (C, Z) 의 격자를 한꺼번에 계산합니다.

        기둥마다 같은 값(지도 보간, 강 찾기, 동굴 노이즈)은 한 번만 구해 빠릅니다.
        반환: keys 의 값들 (C, Z). keys 는 evaluate 의 키 가운데 고릅니다.
        """
        x = np.ascontiguousarray(x, dtype=np.float64)
        y = np.ascontiguousarray(y, dtype=np.float64)
        up = np.asarray(up, dtype=np.float64)
        if up.ndim != 2 or up.shape[0] != x.shape[0]:
            raise ValueError(f"up 은 (C, Z) 모양이어야 합니다: {up.shape}, C = {x.shape[0]}")
        if not np.isfinite(up).all():
            raise ValueError("up 에 NaN 이나 inf 가 있습니다")
        C, Z = up.shape
        out = {}
        step = max(CHUNK_POINTS // max(Z, 1), 1)
        for s in range(0, C, step):
            e = min(s + step, C)
            cols = self.columns(x[s:e], y[s:e])
            col = np.repeat(np.arange(e - s, dtype=np.int64), Z)
            px = np.repeat(x[s:e], Z)
            py = np.repeat(y[s:e], Z)
            ev = self._evaluate(cols, col, px, py, up[s:e].ravel().copy(), noise_band_m)
            for k in keys:
                if k not in out:
                    out[k] = np.empty((C, Z), dtype=ev[k].dtype)
                out[k][s:e] = ev[k].reshape(e - s, Z)
        return out

    def _by_chunks(self, fn, x, y) -> np.ndarray:
        """수평 위치 (C,) 를 CHUNK_POINTS 씩 나눠 fn(x, y) -> (C,) 를 부르고 잇습니다."""
        x = np.ascontiguousarray(x, dtype=np.float64)
        y = np.ascontiguousarray(y, dtype=np.float64)
        if x.shape != y.shape or x.ndim != 1:
            raise ValueError(f"x, y 는 같은 (C,) 모양이어야 합니다: {x.shape} {y.shape}")
        if x.size <= CHUNK_POINTS:
            return fn(x, y)
        return np.concatenate(
            [
                fn(x[s : s + CHUNK_POINTS], y[s : s + CHUNK_POINTS])
                for s in range(0, x.size, CHUNK_POINTS)
            ]
        )

    def surface_height(self, x: np.ndarray, y: np.ndarray) -> np.ndarray:
        """동굴을 빼고 강바닥을 깎은 지표 높이 (d₁ = 0 의 근) (C,) [m]. 높이맵 굽기용.

        물가 30 m 밖(노이즈 있음)은 h = z_s' + m·A_d·ξ(x, y, h) 를 고정점 반복으로, 강 둘레(노이즈
        0)는 smax 식을 이분법으로 풉니다. m·A_d·|∂ξ/∂z| < 1 이라 근은 하나입니다.
        """
        return self._by_chunks(self._surface_height, x, y)

    def _surface_height(self, x: np.ndarray, y: np.ndarray) -> np.ndarray:
        cols = self.columns(x, y)
        h = cols["z_surface"].copy()
        noisy = np.flatnonzero(cols["m"] > 0)
        if self.amp > 0 and noisy.size:
            xs, ys = x[noisy], y[noisy]
            base = cols["z_surface"][noisy]
            amp = cols["m"][noisy] * self.amp
            hh = base.copy()
            for _ in range(SURFACE_FIXED_POINT_ITERS):
                hh = base + amp * self._detail_noise(xs, ys, hh)
            h[noisy] = hh
        near = (cols["river_half_width"] > 0) & (
            cols["river_dist"] < cols["river_half_width"] + RIVER_SMOOTH_K_M
        )
        todo = np.flatnonzero(near & (cols["m"] <= 0)).astype(np.int64)
        if todo.size:
            _bisect_surface_kernel(
                cols["z_surface"], cols["river_dist"], cols["river_half_width"],
                cols["river_depth"], cols["river_bank"], RIVER_SMOOTH_K_M, todo,
                SURFACE_BISECT_ITERS, h,
            )  # fmt: skip
        return h

    def water_surface(self, x: np.ndarray, y: np.ndarray) -> np.ndarray:
        """물 규칙에 쓰는 지상 수면 h_w (C,) [m] (호수·바다·하도). 없으면 NaN."""

        def one(xx, yy):
            cols = self.columns(xx, yy)
            hw, dist = cols["river_half_width"], cols["river_dist"]
            inside = (hw > 0) & (dist <= hw)
            return np.where(
                inside, np.fmax(cols["lake_level"], cols["river_level"]), cols["lake_level"]
            )

        return self._by_chunks(one, x, y)

    def water_table(self, x: np.ndarray, y: np.ndarray) -> np.ndarray:
        """지하수면 z_gw (C,) [m] (쌍선형 보간)."""
        return self._by_chunks(lambda xx, yy: self.columns(xx, yy)["z_gw"], x, y)

    def _check_points(self, points) -> np.ndarray:
        pts = np.asarray(points, dtype=np.float64)
        if pts.ndim != 2 or pts.shape[1] != 3:
            raise ValueError(f"points 는 (M, 3) 배열이어야 합니다: 받은 모양 {pts.shape}")
        if not np.isfinite(pts).all():
            raise ValueError("points 에 NaN 이나 inf 가 있습니다")
        return pts
