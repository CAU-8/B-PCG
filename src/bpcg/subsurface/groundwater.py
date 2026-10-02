"""지하수면: 물가 거리 + 띠 대수층 Dupuit 해 (docs/pipeline.md 8.3, 설계도 4장 사슬 3).

Fan 2013·Haitjema 2005 를 줄인 어림식입니다. 발표에서 '물리 모델'이라 부르지 않습니다.

계산 순서
1. 물 칸(바다·호수·강)에서 core.distance.nearest_source 로 물가 거리 δ 와 출발 칸 s 를 구하고,
   기준 수위 h_d = h_w(s) 를 넘겨받습니다.
2. 길이 L 의 정의는 하나입니다(설계도 5장 10번): 같은 물가 s 를 출발점으로 하는 칸들 가운데
   가장 먼 δ, 즉 그 물가 영역의 분수령까지 거리입니다. 그래서 늘 δ ≤ L 이고 H ≥ 0 입니다.
3. 수리전도도 K_h = 10^(log k)·ρ_w·g/μ × SECONDS_PER_YEAR [m/yr] (지표 암석의 Gleeson 2011 투수성),
   대수층 두께 b = α/(1 + β·S) [m] (Fan 2013), 투수량 계수 T = K_h·b [m²/yr].
4. 함양 R_g = recharge_fraction·유출 [m/yr].
5. 띠 대수층(한쪽은 수위 h_d 인 물가, 다른 쪽 L 은 흐름 없는 분수령) Dupuit 해
   H = R_g·δ·(2L − δ)/(2T), z_gw = h_d + H.
6. 규칙: z_gw ≤ max(z, h_w), 물 칸은 z_gw = h_w (make_figures 586행 버그를 고친 규칙),
   z_gw ≥ z − max_depth_m.
"""

import time

import numpy as np
from numba import njit, prange

from bpcg.core.constants import MU_WATER, RHO_WATER, SECONDS_PER_YEAR
from bpcg.core.constants import gravity as planet_gravity
from bpcg.core.distance import nearest_source
from bpcg.core.graph import CellGraph
from bpcg.geology import rocks as rk
from bpcg.subsurface.water import _as_mask, _as_vector, _check_graph

SHALLOW_DEPTH_M = 0.5  # 진단: 지하수가 땅 겉 0.5 m 안에 닿는 육지 비율 (pipeline.md 8.3)


@njit(cache=True)
def _divide_distance_kernel(src: np.ndarray, delta: np.ndarray) -> np.ndarray:
    """출발 칸마다 그 영역의 가장 먼 δ (분수령까지 거리) [m] 를 칸마다 돌려줍니다.

    src: (N,) int64 출발 칸 (없으면 -1). delta: (N,) [m]. 반환: (N,) L [m], 출발 칸이 없으면 NaN.
    """
    n = src.shape[0]
    lmax = np.zeros(n, dtype=np.float64)
    for c in range(n):
        s = src[c]
        if s >= 0 and delta[c] > lmax[s]:
            lmax[s] = delta[c]
    out = np.empty(n, dtype=np.float64)
    for c in range(n):
        s = src[c]
        out[c] = lmax[s] if s >= 0 else np.nan
    return out


@njit(cache=True, parallel=True)
def _water_table_kernel(
    z: np.ndarray,
    h_w: np.ndarray,
    is_water: np.ndarray,
    src: np.ndarray,
    delta: np.ndarray,
    L: np.ndarray,
    recharge: np.ndarray,
    trans: np.ndarray,
    max_depth: float,
    out: np.ndarray,
) -> None:
    """z_gw = clip(h_d + R_g·δ·(2L − δ)/(2T), z − max_depth, z), 물 칸은 h_w. 모두 (N,) [m]."""
    for c in prange(z.shape[0]):
        if is_water[c]:
            out[c] = h_w[c]
            continue
        lo = z[c] - max_depth
        s = src[c]
        if s < 0:
            out[c] = lo
            continue
        d = delta[c]
        H = recharge[c] * d * (2.0 * L[c] - d) / (2.0 * trans[c])
        v = h_w[s] + max(H, 0.0)
        if v > z[c]:
            v = z[c]
        if v < lo:
            v = lo
        out[c] = v


def hydraulic_conductivity(rock: np.ndarray, gravity: float) -> np.ndarray:
    """암석 번호의 수리전도도 K_h = 10^(log10 k)·ρ_w·g/μ × SECONDS_PER_YEAR [m/yr].

    rock: 암석 번호 (아무 모양, geology.rocks). gravity: g [m/s²].
    log10 k 는 geology.rocks.LOG10_PERM (Gleeson 2011, 원문 확인 필요) 입니다.
    반환: rock 과 같은 모양 float64 [m/yr].
    """
    r = np.asarray(rock)
    if r.size and (not np.issubdtype(r.dtype, np.integer) or r.min() < 0 or r.max() >= rk.N_ROCKS):
        raise ValueError(f"암석 번호는 0..{rk.N_ROCKS - 1} 정수여야 합니다")
    g = float(gravity)
    if not (np.isfinite(g) and g > 0.0):
        raise ValueError(f"gravity 는 0 보다 큰 유한한 값이어야 합니다: {g}")
    k = 10.0 ** rk.LOG10_PERM[r.astype(np.intp)]
    return k * RHO_WATER * g / MU_WATER * SECONDS_PER_YEAR


def water_table(
    graph: CellGraph,
    z: np.ndarray,
    water_level: np.ndarray,
    is_water: np.ndarray,
    surface_rock: np.ndarray,
    slope: np.ndarray,
    runoff: np.ndarray | float,
    cfg,
    gravity: float | None = None,
    is_ocean: np.ndarray | None = None,
) -> tuple[np.ndarray, dict]:
    """지하수면 z_gw 를 닫힌 어림식으로 구합니다 (pipeline.md 8.3).

    graph: CellGraph. z: (N,) 지표 고도 [m]. water_level: (N,) 수면 h_w [m] (물 칸 값만 읽음,
    보통 water_bodies 의 water_level_m). is_water: (N,) bool 바다·호수·강 칸.
    surface_rock: (N,) 지표 암석 번호. slope: (N,) 경사 [m/m] (음수는 0 으로 봄).
    runoff: (N,) 또는 스칼라 유출 [m/yr].
    cfg: groundwater 절(recharge_fraction, fan_alpha_m, fan_beta, max_depth_m), planet 절(중력).
    gravity: g [m/s²], None 이면 core.constants.gravity(planet.radius_m, planet.mean_density_kg_m3).
    is_ocean: (N,) bool 또는 None. 진단의 '물을 포함한 육지 비율'에만 씁니다.

    반환: (z_gw (N,) float64 [m], diag dict).
    diag: shallow_fraction(물이 아닌 육지 중 z − z_gw ≤ 0.5 m 인 면적 비율, 창발 지표, 지구 22~32%),
    shallow_fraction_with_water(is_ocean 이 있으면 호수·강을 포함한 육지 기준, 없으면 None),
    median_depth_m, clipped_surface_fraction, clipped_max_depth_fraction, n_water, gravity_m_s2,
    seconds. 물 칸이 하나도 없으면 모든 칸이 z − max_depth_m 입니다.
    """
    t0 = time.perf_counter()
    n = _check_graph(graph)
    zz = _as_vector(z, n, "z")
    water = _as_mask(is_water, n, "is_water")
    h = np.ascontiguousarray(water_level, dtype=np.float64)
    if h.shape != (n,):
        raise ValueError(f"water_level 은 ({n},) 배열이어야 합니다 (받은 모양: {h.shape})")
    if not np.isfinite(h[water]).all():
        raise ValueError("물 칸의 water_level 에 NaN 이나 inf 가 있습니다")
    rock = np.asarray(surface_rock)
    if rock.shape != (n,):
        raise ValueError(f"surface_rock 은 ({n},) 배열이어야 합니다 (받은 모양: {rock.shape})")
    S = np.maximum(_as_vector(slope, n, "slope"), 0.0)
    runoff = _as_vector(runoff, n, "runoff", allow_scalar=True)
    if (runoff < 0.0).any():
        raise ValueError("runoff 는 0 이상이어야 합니다")
    gw = cfg.groundwater
    f_r = float(gw.recharge_fraction)
    alpha = float(gw.fan_alpha_m)
    beta = float(gw.fan_beta)
    max_depth = float(gw.max_depth_m)
    if not (0.0 <= f_r <= 1.0 and alpha > 0.0 and beta >= 0.0 and max_depth >= 0.0):
        raise ValueError(
            "groundwater 설정은 0 ≤ recharge_fraction ≤ 1, fan_alpha_m > 0, fan_beta ≥ 0, "
            f"max_depth_m ≥ 0 이어야 합니다: {f_r}, {alpha}, {beta}, {max_depth}"
        )
    if gravity is None:
        gravity = planet_gravity(cfg.planet.radius_m, cfg.planet.mean_density_kg_m3)
    k_h = hydraulic_conductivity(rock, gravity)
    thickness = alpha / (1.0 + beta * S)
    trans = np.ascontiguousarray(k_h * thickness)
    recharge = np.ascontiguousarray(f_r * runoff)

    delta, src = nearest_source(graph, water)
    L = _divide_distance_kernel(src, delta)
    z_gw = np.empty(n, dtype=np.float64)
    _water_table_kernel(zz, h, water, src, delta, L, recharge, trans, max_depth, z_gw)

    depth = zz - z_gw
    dry = ~water
    area = graph.area
    a_dry = float(area[dry].sum())
    shallow = depth <= SHALLOW_DEPTH_M
    diag: dict = {
        "shallow_fraction": float(area[dry & shallow].sum() / a_dry) if a_dry > 0 else float("nan"),
        "shallow_fraction_with_water": None,
        "median_depth_m": float(np.median(depth[dry])) if dry.any() else float("nan"),
        "clipped_surface_fraction": float(np.mean(depth[dry] <= 0.0)) if dry.any() else 0.0,
        "clipped_max_depth_fraction": (
            float(np.mean(depth[dry] >= max_depth)) if dry.any() else 0.0
        ),
        "n_water": int(water.sum()),
        "gravity_m_s2": float(gravity),
    }
    if is_ocean is not None:
        land = ~_as_mask(is_ocean, n, "is_ocean")
        a_land = float(area[land].sum())
        # 호수·강 칸은 지하수면이 수면이라 땅 겉에 닿은 것으로 셉니다.
        wet = shallow | water
        diag["shallow_fraction_with_water"] = (
            float(area[land & wet].sum() / a_land) if a_land > 0 else float("nan")
        )
    diag["seconds"] = time.perf_counter() - t0
    return z_gw, diag
