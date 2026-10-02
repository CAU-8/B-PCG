"""판: 씨앗, 판 배정, 오일러 극 운동, 경계 판정, 경계까지 거리, 섭입 방향 (docs/pipeline.md 4.1).

사슬 1(설계도 4장)의 첫 고리입니다. 거친 격자(구면 그래프)에서 한 번 계산합니다.

순서
1. 씨앗 M 개: 해시 균등수 → Box–Muller 정규분포 3개 → 정규화 (구면에 고르게).
2. 판 배정: π(c) = argmax_k normalize(p + A_w·ξ⃗(f_w·p))·s_k (가이드 5장).
3. 판 운동: 오일러 극 축(해시 단위 벡터), |ω| = v/R, v(p) = ω × R·p [m/yr].
4. 경계 판정: 다른 판 이웃 쌍마다 γ = −Δv·b̂, τ = |Δv·(p×b̂)| 의 평균, γ > κτ 수렴, γ < −κτ 발산.
5. 해양 면적이 절반을 넘는 판에 발산 경계가 없으면 ω 를 뒤집고 4를 다시 (최대 3회).
   뒤집어도 없으면 가장 큰 이웃 판에서 멀어지는 회전으로 바꿉니다 (ensure_divergent_boundaries).
6. 경계까지 거리: 같은 판 안에서 nearest_source (흐름선 거리 근사) + 경계선까지 반 칸.
   경계 값(γ, 반확장 속도)은 경계를 따라 격자 잡음을 지운 뒤 넘겨받습니다.
7. 섭입 방향: 해양-대륙은 해양 쪽, 해양-해양은 더 늙은 쪽(판 쌍 평균 나이), 대륙-대륙은 충돌.
"""

import math

import numpy as np
from numba import njit, prange

from bpcg.core.distance import nearest_source
from bpcg.core.graph import CellGraph
from bpcg.core.hashing import hash3, hash_unit
from bpcg.core.noise import vector_fbm3

# 해시 용도 번호 (core 의 101·102·201·210·220 과 겹치지 않게 300번대).
STREAM_PLATE_SEED = 301  # 판 씨앗 방향
STREAM_POLE = 302  # 오일러 극 축
STREAM_SPEED = 303  # 판 속도
STREAM_WARP = 304  # 판 경계 흔들기 노이즈

MAX_FLIP_ROUNDS = 3  # docs/pipeline.md 4.1 의 5: 최대 3회
BOUNDARY_SMOOTH_ITERATIONS = 4  # 경계 칸 γ 의 격자 잡음 지우기 (칸 몇 개 범위)

# boundary_type
BOUNDARY_NONE = 0
CONVERGENT = 1
DIVERGENT = 2
TRANSFORM = 3

# convergence_kind
KIND_NONE = 0
KIND_OCEAN_CONTINENT = 1
KIND_OCEAN_OCEAN = 2
KIND_COLLISION = 3

_MYR = 1.0e6  # [yr/Myr]


# ---------------------------------------------------------------- 공통 검사
def check_sphere_graph(graph: CellGraph) -> None:
    """구면 CellGraph 인지 확인합니다 (아니면 ValueError)."""
    if not isinstance(graph, CellGraph):
        raise ValueError("graph 는 bpcg.core.graph.CellGraph 여야 합니다")
    if graph.kind != "sphere" or graph.R is None or not graph.R > 0:
        raise ValueError("판 계산은 반지름이 있는 구면 그래프(kind='sphere')에서만 합니다")


def check_bool_mask(mask: np.ndarray, n_cells: int, name: str) -> np.ndarray:
    """(N,) bool 배열인지 확인하고 연속 배열로 돌려줍니다."""
    arr = np.asarray(mask)
    if arr.dtype != np.bool_:
        raise ValueError(f"{name} 은 bool 배열이어야 합니다: {arr.dtype}")
    if arr.shape != (n_cells,):
        raise ValueError(f"{name} 모양이 칸 수와 다릅니다: {arr.shape}, 기대 ({n_cells},)")
    return np.ascontiguousarray(arr)


def sub_seed(seed: int, stream: int) -> int:
    """행성 시드에서 용도별 시드를 만듭니다 (int64 안의 음 아닌 정수)."""
    return int(hash3(np.int64(seed), np.int64(stream), np.int64(0)) >> np.uint64(1))


# ---------------------------------------------------------------- 1·3. 씨앗과 운동
def hashed_unit_vectors(seed: int, stream: int, count: int) -> np.ndarray:
    """해시 균등수 4개 → Box–Muller 정규분포 3개 → 정규화. 구면에 고르게 퍼진 (count, 3)."""
    out = np.empty((count, 3), dtype=np.float64)
    for k in range(count):
        u = [hash_unit(np.int64(seed), np.int64(stream), np.int64(4 * k + i)) for i in range(4)]
        r1 = math.sqrt(-2.0 * math.log(1.0 - u[0]))  # 1 − u ∈ (0, 1] 이라 log 가 유한
        r2 = math.sqrt(-2.0 * math.log(1.0 - u[2]))
        g = np.array(
            [
                r1 * math.cos(2.0 * math.pi * u[1]),
                r1 * math.sin(2.0 * math.pi * u[1]),
                r2 * math.cos(2.0 * math.pi * u[3]),
            ]
        )
        norm = float(np.linalg.norm(g))
        out[k] = g / norm if norm > 0.0 else np.array([0.0, 0.0, 1.0])
    return out


def plate_seeds(cfg) -> np.ndarray:
    """판 씨앗 s_k (M, 3) 단위 벡터. M = cfg.plates.count."""
    count = int(cfg.plates.count)
    if count < 2:
        raise ValueError(f"판 개수는 2 이상이어야 합니다: {count}")
    return hashed_unit_vectors(sub_seed(int(cfg.planet.seed), STREAM_PLATE_SEED), 0, count)


def plate_omegas(cfg, radius_m: float | None = None) -> np.ndarray:
    """판 각속도 ω_k (M, 3) [rad/yr]. 축은 해시 단위 벡터, |ω| = v/R, v 는 speed_m_per_yr 범위.

    radius_m: 행성 반지름 R [m] (None 이면 cfg.planet.radius_m).
    """
    count = int(cfg.plates.count)
    seed = sub_seed(int(cfg.planet.seed), STREAM_POLE)
    axes = hashed_unit_vectors(seed, 0, count)
    v_lo, v_hi = (float(x) for x in cfg.plates.speed_m_per_yr)
    if not (0.0 <= v_lo <= v_hi):
        raise ValueError(f"speed_m_per_yr 는 0 ≤ 하한 ≤ 상한이어야 합니다: {(v_lo, v_hi)}")
    radius = float(cfg.planet.radius_m if radius_m is None else radius_m)
    s_seed = sub_seed(int(cfg.planet.seed), STREAM_SPEED)
    speeds = np.array(
        [
            v_lo + (v_hi - v_lo) * hash_unit(np.int64(s_seed), np.int64(k), np.int64(0))
            for k in range(count)
        ]
    )
    return axes * (speeds / radius)[:, None]


# ---------------------------------------------------------------- 2. 판 배정
@njit(cache=True, parallel=True)
def _argmax_dot_kernel(q: np.ndarray, seeds: np.ndarray, out: np.ndarray) -> None:
    """q (N,3), seeds (M,3) → out (N,) int32 = argmax_k q·s_k (같으면 번호가 작은 판)."""
    for c in prange(q.shape[0]):
        best = -np.inf
        arg = 0
        for k in range(seeds.shape[0]):
            d = q[c, 0] * seeds[k, 0] + q[c, 1] * seeds[k, 1] + q[c, 2] * seeds[k, 2]
            if d > best:
                best = d
                arg = k
        out[c] = arg


def assign_plates(unit: np.ndarray, seeds: np.ndarray, cfg) -> np.ndarray:
    """칸마다 판 번호 π(c) (N,) int32 (가이드 5장, 경계를 벡터 노이즈로 흔든 구면 보로노이).

    unit: (N, 3) 단위 벡터. seeds: (M, 3) 판 씨앗.
    """
    pc = cfg.plates
    warp = vector_fbm3(
        unit,
        sub_seed(int(cfg.planet.seed), STREAM_WARP),
        frequency=float(pc.warp_frequency),
    )
    q = unit + float(pc.warp_amplitude) * warp
    q /= np.linalg.norm(q, axis=1, keepdims=True)
    out = np.empty(unit.shape[0], dtype=np.int32)
    _argmax_dot_kernel(np.ascontiguousarray(q), np.ascontiguousarray(seeds), out)
    return out


# ---------------------------------------------------------------- 4. 경계 판정
@njit(cache=True, parallel=True)
def _classify_kernel(
    unit: np.ndarray,
    nbr: np.ndarray,
    dist: np.ndarray,
    plate: np.ndarray,
    omega: np.ndarray,
    radius: float,
    kappa: float,
    btype: np.ndarray,
    gamma: np.ndarray,
    tau: np.ndarray,
    other: np.ndarray,
    half_gap: np.ndarray,
) -> None:
    """칸마다 다른 판 이웃들과의 γ, τ 평균 [m/yr] 과 경계 종류, 가장 많이 맞닿은 다른 판,
    가장 가까운 다른 판 이웃까지 거리의 절반 [m] (경계선까지 거리 근사)."""
    n_slots = nbr.shape[1]
    for c in prange(nbr.shape[0]):
        k = plate[c]
        px = unit[c, 0]
        py = unit[c, 1]
        pz = unit[c, 2]
        sg = 0.0
        st = 0.0
        cnt = 0
        hg = np.inf
        best_plate = -1
        best_count = 0
        for s in range(n_slots):
            v = nbr[c, s]
            if v < 0:
                continue
            kv = plate[v]
            if kv == k:
                continue
            # b̂: 이웃 쪽을 가리키는 접선 방향 (가이드 5장)
            qx = unit[v, 0]
            qy = unit[v, 1]
            qz = unit[v, 2]
            d = qx * px + qy * py + qz * pz
            bx = qx - d * px
            by = qy - d * py
            bz = qz - d * pz
            bn = math.sqrt(bx * bx + by * by + bz * bz)
            if bn == 0.0:
                continue
            bx /= bn
            by /= bn
            bz /= bn
            # Δv = v_{π(c')}(p) − v_{π(c)}(p) = (ω' − ω) × R·p
            wx = omega[kv, 0] - omega[k, 0]
            wy = omega[kv, 1] - omega[k, 1]
            wz = omega[kv, 2] - omega[k, 2]
            dvx = (wy * pz - wz * py) * radius
            dvy = (wz * px - wx * pz) * radius
            dvz = (wx * py - wy * px) * radius
            sg += -(dvx * bx + dvy * by + dvz * bz)
            cx = py * bz - pz * by
            cy = pz * bx - px * bz
            cz = px * by - py * bx
            st += abs(dvx * cx + dvy * cy + dvz * cz)
            cnt += 1
            if dist[c, s] < hg:
                hg = dist[c, s]
            m = 0
            for s2 in range(n_slots):
                v2 = nbr[c, s2]
                if v2 >= 0 and plate[v2] == kv:
                    m += 1
            if m > best_count or (m == best_count and kv < best_plate):
                best_count = m
                best_plate = kv
        if cnt == 0:
            btype[c] = 0
            gamma[c] = 0.0
            tau[c] = 0.0
            other[c] = -1
            half_gap[c] = 0.0
            continue
        gm = sg / cnt
        tm = st / cnt
        if gm > kappa * tm:
            btype[c] = 1
        elif gm < -kappa * tm:
            btype[c] = 2
        else:
            btype[c] = 3
        gamma[c] = gm
        tau[c] = tm
        other[c] = best_plate
        half_gap[c] = 0.5 * hg


def classify_boundaries(
    graph: CellGraph, plate_id: np.ndarray, omega: np.ndarray, kappa: float
) -> dict[str, np.ndarray]:
    """판 경계 칸을 수렴(1)·발산(2)·변환(3)으로 판정합니다 (가이드 5장, pipeline 4.1 의 4).

    graph: 구면 그래프. plate_id: (N,) 판 번호 0..M-1. omega: (M, 3) 각속도 [rad/yr].
    kappa: 수렴·발산과 변환을 가르는 비율 κ.
    반환 dict: boundary_type (N,) uint8 (경계 아니면 0), gamma (N,) 평균 수렴 속도 γ [m/yr]
    (양수 수렴, 음수 발산), tau (N,) 평균 전단 속도 τ [m/yr], other (N,) int32 가장 많이 맞닿은
    다른 판 (없으면 -1), half_gap (N,) 가장 가까운 다른 판 이웃까지 거리의 절반 [m].
    """
    check_sphere_graph(graph)
    n = graph.n_cells
    plate = np.ascontiguousarray(plate_id, dtype=np.int32)
    if plate.shape != (n,):
        raise ValueError(f"plate_id 모양이 칸 수와 다릅니다: {plate.shape}")
    om = np.ascontiguousarray(omega, dtype=np.float64)
    if om.ndim != 2 or om.shape[1] != 3 or plate.min() < 0 or plate.max() >= om.shape[0]:
        raise ValueError("omega 는 (M, 3) 이고 plate_id 는 0..M-1 이어야 합니다")
    if not (math.isfinite(kappa) and kappa >= 0):
        raise ValueError(f"boundary_kappa 는 0 이상이어야 합니다: {kappa}")
    out = {
        "boundary_type": np.empty(n, dtype=np.uint8),
        "gamma": np.empty(n, dtype=np.float64),
        "tau": np.empty(n, dtype=np.float64),
        "other": np.empty(n, dtype=np.int32),
        "half_gap": np.empty(n, dtype=np.float64),
    }
    _classify_kernel(
        np.ascontiguousarray(graph.unit()),
        np.ascontiguousarray(graph.nbr),
        np.ascontiguousarray(graph.dist),
        plate,
        om,
        float(graph.R),
        float(kappa),
        out["boundary_type"],
        out["gamma"],
        out["tau"],
        out["other"],
        out["half_gap"],
    )
    return out


# ---------------------------------------------------------------- 6. 거리
@njit(cache=True)
def _smooth_on_cells_kernel(
    values: np.ndarray,
    cells: np.ndarray,
    member: np.ndarray,
    plate: np.ndarray,
    nbr: np.ndarray,
    iterations: int,
) -> np.ndarray:
    """member 칸끼리(같은 판)만 이웃 평균을 iterations 번 (야코비). cells 는 member 칸 목록."""
    cur = values.copy()
    nxt = values.copy()
    for _ in range(iterations):
        for t in range(cells.shape[0]):
            c = cells[t]
            s = cur[c]
            cnt = 1
            for k in range(nbr.shape[1]):
                v = nbr[c, k]
                if v >= 0 and member[v] and plate[v] == plate[c]:
                    s += cur[v]
                    cnt += 1
            nxt[c] = s / cnt
        for t in range(cells.shape[0]):
            cur[cells[t]] = nxt[cells[t]]
    return cur


def smooth_along_boundary(
    values: np.ndarray, member: np.ndarray, plate: np.ndarray, nbr: np.ndarray
) -> np.ndarray:
    """같은 종류·같은 판 경계 칸끼리 값을 BOUNDARY_SMOOTH_ITERATIONS 번 이웃 평균합니다.

    경계 칸의 b̂ 는 계단 모양 격자의 직교·대각 방향이라 칸마다 γ 가 들쭉날쭉합니다. 강체 회전의
    상대 속도는 경계를 따라 매끄럽게 변하므로, 이 격자 잡음을 지운 뒤 경계 값을 넘겨줍니다.
    그러지 않으면 나이·융기에 경계 칸 보로노이 모양의 쐐기 줄무늬가 생깁니다.
    """
    cells = np.flatnonzero(member).astype(np.int64)
    if cells.size == 0:
        return np.asarray(values, dtype=np.float64).copy()
    return _smooth_on_cells_kernel(
        np.ascontiguousarray(values, dtype=np.float64),
        cells,
        np.ascontiguousarray(member),
        np.ascontiguousarray(plate),
        np.ascontiguousarray(nbr),
        BOUNDARY_SMOOTH_ITERATIONS,
    )


def same_plate_graph(graph: CellGraph, plate_id: np.ndarray) -> CellGraph:
    """같은 판 이웃만 남긴 그래프. 거리가 판 안에서만 퍼지게 합니다(해령→해구 흐름선 근사)."""
    nbr = graph.nbr
    ok = nbr >= 0
    nb_plate = np.where(ok, plate_id[np.where(ok, nbr, 0)], -1)
    nbr_same = np.where(ok & (nb_plate == plate_id[:, None]), nbr, -1).astype(nbr.dtype)
    return CellGraph(
        kind=graph.kind,
        shape=graph.shape,
        pos=graph.pos,
        nbr=nbr_same,
        dist=graph.dist,
        area=graph.area,
        spacing=graph.spacing,
        R=graph.R,
    )


def _boundary_distance(
    graph: CellGraph, is_source: np.ndarray, half_gap: np.ndarray
) -> tuple[np.ndarray, np.ndarray]:
    """가장 가까운 출발 칸까지 대원 거리 + 그 칸에서 경계선까지 반 칸 [m]. 닿지 않으면 inf, -1."""
    dist, src = nearest_source(graph, is_source)
    ok = src >= 0
    out = np.full(graph.n_cells, np.inf)
    out[ok] = dist[ok] + half_gap[src[ok]]
    return out, src


def _age_consistent_rate_floor(
    dist_div: np.ndarray, plate: np.ndarray, continental: np.ndarray, m: int, cap_myr: float
) -> np.ndarray:
    """판마다 반확장 속도의 하한 [m/yr] = 판 해양 칸의 가장 먼 해령 거리 / 나이 상한.

    판 모양은 고정이고 속도는 해시로 따로 뽑으므로, 느린 해령에서 먼 바다는 나이가 상한을 넘어
    상한에 면적이 몰립니다(설계도 5장 17번이 경고한 분포). 상한보다 늙은 해양저는 이미 섭입해
    남지 않으므로, 판의 해령은 평균적으로 적어도 이만큼 빨리 벌어졌어야 합니다. 이렇게 두면 판에서
    가장 먼 해양 칸만 상한 나이에 닿고, 나이 분포는 흐름선 거리(판 모양)로 정해집니다.
    """
    oc = ~continental & np.isfinite(dist_div)
    d_max = np.zeros(m)
    if oc.any():
        np.maximum.at(d_max, plate[oc], dist_div[oc])
    return d_max / (float(cap_myr) * _MYR)


def seafloor_age_myr(
    dist_divergent_m: np.ndarray, spreading_m_per_yr: np.ndarray, cap_myr: float
) -> np.ndarray:
    """해양저 나이 = 해령 거리 / 반확장 속도 [Myr] (설계도 5장 17번). cap_myr 는 안전장치.

    dist_divergent_m: (N,) [m] (없으면 inf). spreading_m_per_yr: (N,) [m/yr].
    반환: (N,) float64 [Myr], 0..cap_myr. 속도가 0 이하이거나 거리가 무한이면 cap_myr.
    """
    d = np.asarray(dist_divergent_m, dtype=np.float64)
    v = np.asarray(spreading_m_per_yr, dtype=np.float64)
    ok = np.isfinite(d) & np.isfinite(v) & (v > 0.0)
    age = np.full(d.shape, float(cap_myr))
    age[ok] = d[ok] / v[ok] / _MYR
    return np.clip(age, 0.0, float(cap_myr))


# ---------------------------------------------------------------- 7. 섭입 방향
@njit(cache=True, parallel=True)
def _other_side_kernel(
    nbr: np.ndarray,
    plate: np.ndarray,
    continental: np.ndarray,
    age: np.ndarray,
    cells: np.ndarray,
    cont_frac: np.ndarray,
    other_age: np.ndarray,
) -> None:
    """경계 칸 cells 마다 다른 판 이웃 중 대륙 비율과 해양 이웃 평균 나이 [Myr] (없으면 NaN)."""
    for t in prange(cells.shape[0]):
        c = cells[t]
        k = plate[c]
        n_other = 0
        n_cont = 0
        s_age = 0.0
        n_age = 0
        for s in range(nbr.shape[1]):
            v = nbr[c, s]
            if v < 0 or plate[v] == k:
                continue
            n_other += 1
            if continental[v]:
                n_cont += 1
            else:
                s_age += age[v]
                n_age += 1
        cont_frac[t] = n_cont / n_other if n_other > 0 else 0.0
        other_age[t] = s_age / n_age if n_age > 0 else np.nan


def _subduction_polarity(
    graph: CellGraph,
    plate: np.ndarray,
    other: np.ndarray,
    is_conv: np.ndarray,
    continental: np.ndarray,
    age_myr: np.ndarray,
    n_plates: int,
) -> tuple[np.ndarray, np.ndarray]:
    """수렴 경계 칸마다 kind (uint8) 와 side (int8, +1 위판, −1 섭입판, 0 충돌)."""
    n = graph.n_cells
    kind = np.zeros(n, dtype=np.uint8)
    side = np.zeros(n, dtype=np.int8)
    cells = np.flatnonzero(is_conv).astype(np.int64)
    if cells.size == 0:
        return kind, side
    cont_frac = np.empty(cells.size)
    other_age = np.empty(cells.size)
    age_ocean = np.where(continental, 0.0, age_myr)
    _other_side_kernel(
        np.ascontiguousarray(graph.nbr), plate, continental, age_ocean, cells, cont_frac, other_age
    )
    own_cont = continental[cells]
    oth_cont = cont_frac >= 0.5
    a = plate[cells].astype(np.int64)
    b = other[cells].astype(np.int64)
    k_cells = np.where(
        own_cont & oth_cont,
        KIND_COLLISION,
        np.where(own_cont ^ oth_cont, KIND_OCEAN_CONTINENT, KIND_OCEAN_OCEAN),
    ).astype(np.uint8)
    sub = np.full(cells.size, -1, dtype=np.int64)
    # 해양-대륙: 해양 쪽이 섭입
    oc = k_cells == KIND_OCEAN_CONTINENT
    sub[oc] = np.where(own_cont[oc], b[oc], a[oc])
    # 해양-해양: 판 쌍마다 평균 나이를 비교해 더 늙은 쪽이 섭입 (같으면 번호가 작은 판).
    # 칸마다 비교하면 경계를 따라 방향이 칸 단위로 뒤집히므로 판 쌍 단위로 정합니다.
    oo = np.flatnonzero(k_cells == KIND_OCEAN_OCEAN)
    if oo.size:
        sums = np.zeros((n_plates, n_plates))
        cnts = np.zeros((n_plates, n_plates))
        own_age = age_ocean[cells[oo]]
        np.add.at(sums, (a[oo], b[oo]), own_age)
        np.add.at(cnts, (a[oo], b[oo]), 1.0)
        ok = np.isfinite(other_age[oo])
        np.add.at(sums, (b[oo][ok], a[oo][ok]), other_age[oo][ok])
        np.add.at(cnts, (b[oo][ok], a[oo][ok]), 1.0)
        with np.errstate(invalid="ignore", divide="ignore"):
            mean = sums / cnts
        age_a = mean[a[oo], b[oo]]
        age_b = mean[b[oo], a[oo]]
        sub[oo] = np.where(
            age_a > age_b, a[oo], np.where(age_b > age_a, b[oo], np.minimum(a[oo], b[oo]))
        )
    s_cells = np.where(sub < 0, 0, np.where(sub == a, -1, 1)).astype(np.int8)
    kind[cells] = k_cells
    side[cells] = s_cells
    return kind, side


# ---------------------------------------------------------------- 5. 뒤집기
def _plate_centroids(unit: np.ndarray, area: np.ndarray, plate: np.ndarray, m: int) -> np.ndarray:
    cen = np.zeros((m, 3))
    for ax in range(3):
        cen[:, ax] = np.bincount(plate, weights=unit[:, ax] * area, minlength=m)
    norm = np.linalg.norm(cen, axis=1, keepdims=True)
    return np.where(norm > 0, cen / np.where(norm > 0, norm, 1.0), 0.0)


def _away_axis(c_j: np.ndarray, c_k: np.ndarray) -> np.ndarray:
    """c_j 에서 c_k 쪽으로 미는 회전축 normalize(c_j × c_k). 두 점이 겹치거나 반대편이면
    c_k 에 수직인 축 하나 (어느 쪽이든 경계의 절반은 벌어짐)."""
    axis = np.cross(c_j, c_k)
    norm = float(np.linalg.norm(axis))
    if norm < 1e-9:
        e = np.zeros(3)
        e[int(np.argmin(np.abs(c_k)))] = 1.0
        axis = np.cross(c_k, e)
        norm = float(np.linalg.norm(axis))
    return axis / norm


def ensure_divergent_boundaries(
    graph: CellGraph,
    plate_id: np.ndarray,
    omega: np.ndarray,
    continental: np.ndarray,
    kappa: float,
    min_rate: float,
    max_rounds: int = MAX_FLIP_ROUNDS,
) -> tuple[np.ndarray, list[dict], dict[str, np.ndarray], list[int]]:
    """해양 면적이 절반을 넘는 판마다 발산 경계 칸이 생기도록 ω 를 고칩니다 (pipeline 4.1 의 5).

    한 번째는 명세대로 ω_k ← −ω_k 입니다. 뒤집은 판이 다음 판정에서도 발산 경계가 없으면, 가장 큰
    이웃 판 j 에서 멀어지는 회전 ω_k ← ω_j + max(|ω_k|, min_rate)·normalize(c_j × c_k) 로 바꿉니다
    (c 는 판 무게중심). 이 상대 회전은 두 판 경계에서 k 를 j 로부터 밀어내므로 발산 경계가 생깁니다.
    한 회차에 서로 이웃한 두 판을 같이 바꾸면 효과가 지워질 수 있어서, 이웃이 이미 바뀐 판은 다음
    회차로 미룹니다. 판정은 최대 max_rounds 번 다시 합니다.

    plate_id: (N,) int32. omega: (M, 3) [rad/yr] (복사해서 고침). continental: (N,) bool.
    min_rate: 멀어지는 회전의 최소 각속도 [rad/yr].
    반환: (고친 ω (M,3), 바꾼 기록 list[dict(round, plate, neighbor, method)],
          마지막 classify_boundaries 결과, 아직 발산 경계가 없는 해양판 번호 list).
    """
    check_sphere_graph(graph)
    continental = check_bool_mask(continental, graph.n_cells, "continental")
    om = np.array(omega, dtype=np.float64, copy=True)
    m = om.shape[0]
    plate = np.ascontiguousarray(plate_id, dtype=np.int32)
    area = graph.area
    plate_area = np.bincount(plate, weights=area, minlength=m)
    ocean_area = np.bincount(plate, weights=area * (~continental), minlength=m)
    with np.errstate(invalid="ignore", divide="ignore"):
        mostly_ocean = np.nan_to_num(ocean_area / plate_area) > 0.5
    centroids = _plate_centroids(graph.unit(), area, plate, m)
    flipped = np.zeros(m, dtype=bool)
    log: list[dict] = []
    for rnd in range(max_rounds + 1):
        cls = classify_boundaries(graph, plate, om, kappa)
        has_div = np.zeros(m, dtype=bool)
        has_div[np.unique(plate[cls["boundary_type"] == DIVERGENT])] = True
        failing = np.flatnonzero(mostly_ocean & ~has_div & (plate_area > 0))
        if failing.size == 0 or rnd == max_rounds:
            break
        other = cls["other"]
        changed = np.zeros(m, dtype=bool)
        for k in failing:
            nb = np.unique(other[(plate == k) & (other >= 0)])
            if nb.size == 0 or changed[nb].any():
                continue
            j = int(nb[np.argmax(plate_area[nb])])  # 같으면 번호가 작은 판 (argmax 첫 값)
            if not flipped[k]:
                om[k] = -om[k]
                method = "negate"
            else:
                rate = max(float(np.linalg.norm(om[k])), float(min_rate))
                om[k] = om[j] + rate * _away_axis(centroids[j], centroids[k])
                method = "away_from_neighbor"
            flipped[k] = True
            changed[k] = True
            log.append({"round": rnd + 1, "plate": int(k), "neighbor": j, "method": method})
    return om, log, cls, [int(k) for k in failing]


# ---------------------------------------------------------------- 공개 함수
def generate_plates(
    graph: CellGraph, continental: np.ndarray, cfg
) -> tuple[dict[str, np.ndarray], dict]:
    """판 필드를 만듭니다 (docs/pipeline.md 4.1).

    graph: 구면 그래프 (보통 거친 격자).
    continental: (N,) bool 대륙 지각 마스크 (crust.continent_mask).
    반환 fields:
      plate_id (N,) int32, boundary_type (N,) uint8 (0 없음, 1 수렴, 2 발산, 3 변환),
      convergence_m_per_yr (N,) float64 가장 가까운 같은 판 수렴 경계의 γ [m/yr] (없으면 0),
      convergence_kind (N,) uint8, subduction_side (N,) int8,
      dist_convergent_m, dist_divergent_m (N,) float64 같은 판 안의 가장 가까운 경계선까지
      대원 거리 [m] (판에 그 경계가 없으면 inf; 발산은 다른 판의 해령으로 대신),
      spreading_m_per_yr (N,) float64 그 해령의 반확장 속도 |Δv·b̂|/2 [m/yr] 에 판별 하한
      (_age_consistent_rate_floor, 해양저가 age_cap_myr 보다 늙지 않게) 을 적용한 값.
    반환 info: seeds (M,3), omegas (M,3) [rad/yr] 최종, omegas_initial, flips (list),
      ocean_fraction (M,), plate_area_m2 (M,), plates_without_divergent (list), n_boundary (dict),
      age_provisional_myr (N,) 섭입 방향에 쓴 임시 나이, spreading_kinematic_m_per_yr (N,)
      하한 적용 전 속도, spreading_floor_m_per_yr (M,) 판별 하한.
    """
    check_sphere_graph(graph)
    n = graph.n_cells
    cont = check_bool_mask(continental, n, "continental")
    pc = cfg.plates
    m = int(pc.count)
    kappa = float(pc.boundary_kappa)
    cap = float(cfg.ocean.age_cap_myr)
    unit = graph.unit()
    area = graph.area

    seeds = plate_seeds(cfg)
    plate = assign_plates(unit, seeds, cfg)
    omega_initial = plate_omegas(cfg, float(graph.R))
    plate_area = np.bincount(plate, weights=area, minlength=m)
    ocean_area = np.bincount(plate, weights=area * (~cont), minlength=m)
    with np.errstate(invalid="ignore", divide="ignore"):
        ocean_frac = np.where(plate_area > 0, ocean_area / plate_area, np.nan)
    min_rate = float(pc.speed_m_per_yr[0]) / float(graph.R)
    omega, flips, cls, remaining = ensure_divergent_boundaries(
        graph, plate, omega_initial, cont, kappa, min_rate
    )

    btype = cls["boundary_type"]
    half_gap = cls["half_gap"]
    is_conv = btype == CONVERGENT
    is_div = btype == DIVERGENT
    # 경계 칸 γ 의 격자 잡음을 같은 종류·같은 판 경계를 따라 지웁니다 (판정은 그대로).
    gamma = cls["gamma"].copy()
    gamma[is_div] = smooth_along_boundary(cls["gamma"], is_div, plate, graph.nbr)[is_div]
    gamma[is_conv] = smooth_along_boundary(cls["gamma"], is_conv, plate, graph.nbr)[is_conv]
    g_same = same_plate_graph(graph, plate)

    # 발산: 같은 판 해령까지 (흐름선 근사). 판에 해령이 없으면 가장 가까운 아무 해령.
    dist_div, src_div = _boundary_distance(g_same, is_div, half_gap)
    missing = src_div < 0
    if missing.any() and is_div.any():
        d_g, s_g = _boundary_distance(graph, is_div, half_gap)
        dist_div[missing] = d_g[missing]
        src_div[missing] = s_g[missing]
    spreading_kin = np.where(src_div >= 0, -0.5 * gamma[np.maximum(src_div, 0)], 0.0)
    spreading_kin = np.maximum(spreading_kin, 0.0)
    rate_floor = _age_consistent_rate_floor(dist_div, plate, cont, m, cap)
    spreading = np.maximum(spreading_kin, rate_floor[plate])
    age_prov = seafloor_age_myr(dist_div, spreading, cap)

    kind_cell, side_cell = _subduction_polarity(
        graph, plate, cls["other"], is_conv, cont, age_prov, m
    )
    dist_conv, src_conv = _boundary_distance(g_same, is_conv, half_gap)
    has_conv = src_conv >= 0
    sc = np.maximum(src_conv, 0)
    convergence = np.where(has_conv, gamma[sc], 0.0)
    kind = np.where(has_conv, kind_cell[sc], KIND_NONE).astype(np.uint8)
    side = np.where(has_conv, side_cell[sc], 0).astype(np.int8)

    fields = {
        "plate_id": plate,
        "boundary_type": btype,
        "convergence_m_per_yr": convergence,
        "convergence_kind": kind,
        "subduction_side": side,
        "dist_convergent_m": dist_conv,
        "dist_divergent_m": dist_div,
        "spreading_m_per_yr": spreading,
    }
    info = {
        "seeds": seeds,
        "omegas": omega,
        "omegas_initial": omega_initial,
        "flips": flips,
        "ocean_fraction": ocean_frac,
        "plate_area_m2": plate_area,
        "plates_without_divergent": remaining,
        "n_boundary": {
            "convergent": int(is_conv.sum()),
            "divergent": int(is_div.sum()),
            "transform": int((btype == TRANSFORM).sum()),
        },
        "age_provisional_myr": age_prov,
        "spreading_kinematic_m_per_yr": spreading_kin,
        "spreading_floor_m_per_yr": rate_floor,
    }
    return fields, info
