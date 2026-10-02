"""지각: 대륙 마스크, 해양저 나이, 지각 두께, 기준 고도 (docs/pipeline.md 4.2).

기준 고도 z_platform 은 해수면을 정하기 전 값입니다.
- 대륙: Airy 지각평형. 대륙-해양 전이(가장자리 얇아짐)에만 쓰고, 육지 고도는 솔버가 정합니다
  (설계도 5장 2번).
- 해양: Parsons & Sclater 1977 나이-수심.
- 해구: 섭입판 쪽에 −D·exp(−(δ/W)²) 를 더합니다. 변위(m)라 속도와 섞지 않고(설계도 5장 7번),
  좁아서 L0 에서 다시 계산할 수 있게 따로 돌려줍니다(trench_offset_m).
"""

import numpy as np
from numba import njit, prange

from bpcg.core.distance import nearest_source
from bpcg.core.graph import CellGraph
from bpcg.core.hashing import hash_unit
from bpcg.core.noise import fbm3
from bpcg.planet.plates import (
    KIND_COLLISION,
    KIND_OCEAN_CONTINENT,
    KIND_OCEAN_OCEAN,
    check_bool_mask,
    check_sphere_graph,
    plate_seeds,
    seafloor_age_myr,
    sub_seed,
)

STREAM_CONTINENT_NOISE = 311
STREAM_CONTINENT_BIAS = 312

# docs/pipeline.md 4.2 의 대륙 마스크 상수
CONTINENT_NOISE_FREQUENCY = 1.2
CONTINENT_NOISE_OCTAVES = 4
CONTINENT_PLATE_BIAS = 0.3  # 판별 치우침 ±0.3
# 판별 치우침을 판 씨앗 소프트맥스로 부드럽게 섞는 날카로움. 경계에서 계단이 생기지 않게 해
# '대륙은 판 경계와 상관없이 놓인다'를 지킵니다 (전이 폭 약 ±0.08 rad ≈ ±500 km).
CONTINENT_BIAS_SHARPNESS = 12.0

MARGIN_EDGE_FRACTION = 0.55  # 대륙 가장자리 두께 = 0.55 × 대륙 두께
TRANSITION_HALF_WIDTH_M = 50_000.0  # 대륙-해양 전이 섞기: 경계 양쪽 50 km

# Parsons & Sclater 1977 나이-수심 (ridge_depth_m 은 설정에서)
PS_SQRT_COEF_M = 350.0  # d = d_ridge + 350·√t [m], t < 70 Myr
PS_BREAK_MYR = 70.0
PS_DEEP_M = 6400.0  # d = 6400 − 3200·e^(−t/62.8) [m]
PS_DEEP_AMP_M = 3200.0
PS_TAU_MYR = 62.8

CONTINENTAL = 1
OCEANIC = 0


def smoothstep(x: np.ndarray) -> np.ndarray:
    """3t² − 2t³, t = clip(x, 0, 1)."""
    t = np.clip(x, 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


# ---------------------------------------------------------------- 대륙 마스크
@njit(cache=True, parallel=True)
def _soft_plate_bias_kernel(
    unit: np.ndarray, seeds: np.ndarray, bias: np.ndarray, sharp: float, out: np.ndarray
) -> None:
    """out[c] = Σ_k b_k·w_k / Σ_k w_k, w_k = exp(β(p·s_k − max_j p·s_j))."""
    m = seeds.shape[0]
    for c in prange(unit.shape[0]):
        best = -np.inf
        for k in range(m):
            d = unit[c, 0] * seeds[k, 0] + unit[c, 1] * seeds[k, 1] + unit[c, 2] * seeds[k, 2]
            if d > best:
                best = d
        num = 0.0
        den = 0.0
        for k in range(m):
            d = unit[c, 0] * seeds[k, 0] + unit[c, 1] * seeds[k, 1] + unit[c, 2] * seeds[k, 2]
            w = np.exp(sharp * (d - best))
            num += w * bias[k]
            den += w
        out[c] = num / den


def continent_score(graph: CellGraph, cfg) -> np.ndarray:
    """대륙 점수 (N,) = 낮은 주파수 fbm + 판별 치우침(부드럽게 섞음). 클수록 먼저 대륙이 됩니다."""
    check_sphere_graph(graph)
    seed = int(cfg.planet.seed)
    unit = np.ascontiguousarray(graph.unit())
    noise = fbm3(
        unit,
        sub_seed(seed, STREAM_CONTINENT_NOISE),
        octaves=CONTINENT_NOISE_OCTAVES,
        frequency=CONTINENT_NOISE_FREQUENCY,
    )
    seeds = plate_seeds(cfg)
    b_seed = sub_seed(seed, STREAM_CONTINENT_BIAS)
    bias = np.array(
        [
            CONTINENT_PLATE_BIAS
            * (2.0 * hash_unit(np.int64(b_seed), np.int64(k), np.int64(0)) - 1.0)
            for k in range(seeds.shape[0])
        ]
    )
    out = np.empty(graph.n_cells)
    _soft_plate_bias_kernel(unit, seeds, bias, CONTINENT_BIAS_SHARPNESS, out)
    return noise + out


def continent_mask(graph: CellGraph, cfg) -> np.ndarray:
    """대륙 지각 마스크 (N,) bool (docs/pipeline.md 4.2).

    점수가 큰 칸부터(같으면 번호순) 면적을 더해 continental_area_fraction 에 가장 가깝게 자릅니다.
    """
    frac = float(cfg.plates.continental_area_fraction)
    if not 0.0 <= frac <= 1.0:
        raise ValueError(f"continental_area_fraction 은 0~1 이어야 합니다: {frac}")
    score = continent_score(graph, cfg)
    n = graph.n_cells
    order = np.lexsort((np.arange(n), -score))
    csum = np.cumsum(graph.area[order])
    target = frac * csum[-1]
    k = int(np.searchsorted(csum, target))  # csum[k] ≥ target 인 첫 번호
    if k >= n:
        n_take = n
    elif k == 0:
        n_take = 1 if abs(csum[0] - target) < target else 0
    else:
        n_take = k + 1 if abs(csum[k] - target) <= abs(csum[k - 1] - target) else k
    mask = np.zeros(n, dtype=bool)
    mask[order[:n_take]] = True
    return mask


# ---------------------------------------------------------------- 깊이와 고도
def ocean_depth_m(age_myr: np.ndarray, ridge_depth_m: float) -> np.ndarray:
    """Parsons & Sclater 1977 해양저 깊이 d [m] (양수, 아래로).

    t < 70 Myr: d = d_ridge + 350·√t, 그 밖: d = 6400 − 3200·e^(−t/62.8).
    age_myr: 나이 [Myr] (음수는 0). ridge_depth_m: 해령 꼭대기 깊이 (기본 2500).
    """
    t = np.maximum(np.asarray(age_myr, dtype=np.float64), 0.0)
    young = float(ridge_depth_m) + PS_SQRT_COEF_M * np.sqrt(t)
    old = PS_DEEP_M - PS_DEEP_AMP_M * np.exp(-t / PS_TAU_MYR)
    return np.where(t < PS_BREAK_MYR, young, old)


def airy_elevation_m(thickness_m: np.ndarray, cfg) -> np.ndarray:
    """대륙 지각 두께 H [m] → Airy 기준 고도 z [m] (docs/pipeline.md 4.2).

    z_air = continental_platform_m + (H − H_c)·(ρm − ρc)/ρm. z_air < 0 이면 물이 채워진 기둥이라
    z = z_air·ρm/(ρm − ρw) 입니다. 이 값은 z < 0 구간의 기울기가 (ρm − ρc)/(ρm − ρw) 로 명세와 같고,
    z = 0 에서 끊기지 않습니다.
    """
    c = cfg.crust
    rho_m = float(c.rho_mantle)
    rho_c = float(c.rho_crust)
    rho_w = float(c.rho_water)
    if not rho_m > max(rho_c, rho_w):
        raise ValueError("맨틀 밀도는 지각·물 밀도보다 커야 합니다")
    z_air = float(c.continental_platform_m) + (
        np.asarray(thickness_m, dtype=np.float64) - float(c.continental_thickness_m)
    ) * ((rho_m - rho_c) / rho_m)
    return np.where(z_air >= 0.0, z_air, z_air * (rho_m / (rho_m - rho_w)))


def trench_offset_m(
    dist_convergent_m: np.ndarray, subduction_side: np.ndarray, convergence_kind: np.ndarray, cfg
) -> np.ndarray:
    """해구 변위 [m] (≤ 0). 섭입판 쪽(side −1, kind 1·2) 에서 −D·exp(−(δ/W)²).

    D = ocean.trench_depth_m, W = ocean.trench_width_m. 섭입판 쪽 경계 근처는 만든 방식상 해양
    지각이므로 지각 종류는 따로 보지 않습니다(L0 에서 지각 종류가 계단처럼 옮겨져도 해구가
    잘리지 않게).
    """
    o = cfg.ocean
    d = np.asarray(dist_convergent_m, dtype=np.float64)
    side = np.asarray(subduction_side)
    kind = np.asarray(convergence_kind)
    on = (side == -1) & ((kind == KIND_OCEAN_CONTINENT) | (kind == KIND_OCEAN_OCEAN))
    out = np.zeros(d.shape)
    w = float(o.trench_width_m)
    out[on] = -float(o.trench_depth_m) * np.exp(-((d[on] / w) ** 2))
    return out


def _edge_cells(nbr: np.ndarray, region: np.ndarray, dist: np.ndarray):
    """region 칸 중 바깥 이웃이 있는 칸과, 그 바깥 이웃까지 거리의 절반 [m]."""
    ok = nbr >= 0
    nb_in = np.where(ok, region[np.where(ok, nbr, 0)], True)
    cross = ok & (nb_in != region[:, None])
    edge = cross.any(axis=1)
    half = 0.5 * np.where(cross, dist, np.inf).min(axis=1)
    half[~edge] = 0.0
    return edge, half


# ---------------------------------------------------------------- 공개 함수
def generate_crust(
    graph: CellGraph, plate_fields: dict[str, np.ndarray], continental: np.ndarray, cfg
) -> tuple[dict[str, np.ndarray], dict]:
    """지각 필드를 만듭니다 (docs/pipeline.md 4.2).

    graph: 구면 그래프. plate_fields: generate_plates 의 fields. continental: (N,) bool.
    반환 fields: crust_type (N,) uint8 (0 해양, 1 대륙), crust_thickness_m (N,) [m],
      ocean_age_myr (N,) [Myr] (대륙 NaN), z_platform_m (N,) [m] 해구 포함 기준 고도 (해수면 전).
    반환 info: z_platform_base_m (N,) 해구를 뺀 기준 고도 [m], trench_m (N,) 해구 변위 [m],
      coast_signed_m (N,) 대륙-해양 경계선까지 부호 거리 [m] (대륙 +, 해양 −, 멀면 ±inf).
    z_platform_m = z_platform_base_m + trench_m 이라서, 더 고운 격자에서 해구만 다시 계산할 수
    있습니다.
    """
    check_sphere_graph(graph)
    n = graph.n_cells
    cont = check_bool_mask(continental, n, "continental")
    for name in (
        "dist_divergent_m",
        "spreading_m_per_yr",
        "dist_convergent_m",
        "convergence_kind",
        "subduction_side",
    ):
        if name not in plate_fields or np.shape(plate_fields[name]) != (n,):
            raise ValueError(f"plate_fields 에 (N,) 모양의 '{name}' 이 있어야 합니다")
    c = cfg.crust
    h_cont = float(c.continental_thickness_m)
    h_ocean = float(c.oceanic_thickness_m)
    margin = float(c.margin_width_m)
    cap = float(cfg.ocean.age_cap_myr)
    d_conv = np.asarray(plate_fields["dist_convergent_m"], dtype=np.float64)
    kind = np.asarray(plate_fields["convergence_kind"])
    side = np.asarray(plate_fields["subduction_side"])

    # 해양저 나이 (설계도 5장 17번): 해령 거리 / 반확장 속도, 상한은 안전장치.
    age = seafloor_age_myr(
        plate_fields["dist_divergent_m"], plate_fields["spreading_m_per_yr"], cap
    )
    age[cont] = np.nan

    # 대륙-해양 경계선까지 거리: 같은 쪽 가장자리 칸까지 대원 거리 + 그 칸에서 경계선까지 반 칸.
    reach = max(margin, TRANSITION_HALF_WIDTH_M) + 2.0 * graph.spacing
    signed = np.where(cont, np.inf, -np.inf)
    z_from_cont = np.full(n, np.nan)  # 해양 칸에서 본 가장 가까운 대륙 가장자리 칸
    z_from_ocean = np.full(n, np.nan)
    src_c = src_o = None
    if cont.any() and (~cont).any():
        edge_c, half_c = _edge_cells(graph.nbr, cont, graph.dist)
        edge_o, half_o = _edge_cells(graph.nbr, ~cont, graph.dist)
        d_c, src_c = nearest_source(graph, edge_c, reach)
        d_o, src_o = nearest_source(graph, edge_o, reach)
        in_c = cont & (src_c >= 0)
        in_o = ~cont & (src_o >= 0)
        signed[in_c] = d_c[in_c] + half_c[src_c[in_c]]
        signed[in_o] = -(d_o[in_o] + half_o[src_o[in_o]])

    # 지각 두께
    thick = np.where(cont, h_cont, h_ocean)
    coll = cont & (kind == KIND_COLLISION)
    thick[coll] += float(c.collision_thickening_m) * np.exp(
        -((d_conv[coll] / float(c.thickening_width_m)) ** 2)
    )
    near = cont & (signed < margin)
    h_edge = MARGIN_EDGE_FRACTION * h_cont
    thick[near] = h_edge + (thick[near] - h_edge) * smoothstep(signed[near] / margin)

    # 기준 고도: 대륙 Airy, 해양 Parsons & Sclater
    z_cont = airy_elevation_m(thick, cfg)
    z_ocean = -ocean_depth_m(np.where(cont, 0.0, age), float(cfg.ocean.ridge_depth_m))
    z = np.where(cont, z_cont, z_ocean)
    # 전이: 경계 양쪽 50 km 에서 smoothstep 으로 섞습니다. 반대쪽 값은 가장 가까운 가장자리 칸 값.
    if src_c is not None:
        band = np.abs(signed) < TRANSITION_HALF_WIDTH_M
        bo = band & ~cont & (src_c >= 0)
        bc = band & cont & (src_o >= 0)
        z_from_cont[bo] = z_cont[src_c[bo]]
        z_from_ocean[bc] = z_ocean[src_o[bc]]
        w = smoothstep((signed + TRANSITION_HALF_WIDTH_M) / (2.0 * TRANSITION_HALF_WIDTH_M))
        z[bo] = w[bo] * z_from_cont[bo] + (1.0 - w[bo]) * z_ocean[bo]
        z[bc] = w[bc] * z_cont[bc] + (1.0 - w[bc]) * z_from_ocean[bc]

    trench = trench_offset_m(d_conv, side, kind, cfg)
    fields = {
        "crust_type": cont.astype(np.uint8),
        "crust_thickness_m": thick,
        "ocean_age_myr": age,
        "z_platform_m": z + trench,
    }
    info = {"z_platform_base_m": z, "trench_m": trench, "coast_signed_m": signed}
    return fields, info
