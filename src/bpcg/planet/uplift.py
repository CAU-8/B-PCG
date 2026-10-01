"""융기 속도 U, 깎인 두께, 해구-화산호 거리 (docs/pipeline.md 4.4).

U 는 실제 단위 [m/yr] 입니다. 바다 칸은 0 입니다.
- 크라톤: 모든 대륙 칸에 U₀ = craton_erosion_m_per_myr·1e-6 (설계도 5장 2번).
- 섭입 위판(kind 1·2, side +1): U += orogen_factor·γ·exp(−((δ − d_arc)/w)²).
  d_arc 는 섭입판을 얕은·깊은 두 구간으로 나눠 정합니다 (설계도 5장 16번).
- 충돌(kind 3): U += collision_factor·γ·exp(−(δ/(1.5w))²).
- 대륙 열곡(δ_div < 50 km): U += rift_subsidence·exp(−(δ_div/30 km)²).
- 산맥 성분에 (1 + 0.25·fbm3) 을 곱합니다.
- 깎인 두께 = clip(max(U, 0)·기간, 0, 상한) (설계도 5장 8번).
"""

import math

import numpy as np

from bpcg.core.graph import CellGraph
from bpcg.core.noise import fbm3
from bpcg.planet.plates import (
    KIND_COLLISION,
    KIND_OCEAN_CONTINENT,
    KIND_OCEAN_OCEAN,
    sub_seed,
)

STREAM_OROGEN_NOISE = 321

COLLISION_WIDTH_FACTOR = 1.5  # 충돌 융기 폭 = 1.5·w
RIFT_MAX_DIST_M = 50_000.0  # 대륙 열곡: δ_div < 50 km
RIFT_WIDTH_M = 30_000.0  # exp(−(δ_div/30 km)²)
OROGEN_NOISE_AMPLITUDE = 0.25  # 산맥 성분 × (1 + 0.25·fbm3)
OROGEN_NOISE_OCTAVES = 4
# 산맥 노이즈의 기본 파장 = 2·orogen_width_m (산맥 폭 정도로 세기가 바뀌게).
OROGEN_NOISE_WAVELENGTH_FACTOR = 2.0

_REQUIRED = (
    "crust_type",
    "is_ocean",
    "convergence_kind",
    "subduction_side",
    "convergence_m_per_yr",
    "dist_convergent_m",
    "dist_divergent_m",
)


def arc_distance_m(cfg) -> float:
    """해구-화산호 거리 d_arc = h₁/tan θ₁ + (h_arc − h₁)/tan θ₂ [m] (설계도 5장 16번).

    h₁ = slab_shallow_depth_m, θ₁ = slab_shallow_dip_deg, h_arc = arc_slab_depth_m,
    θ₂ = slab_deep_dip_deg. 지구 기본값에서 약 200 km 입니다.
    """
    u = cfg.uplift
    h1 = float(u.slab_shallow_depth_m)
    h_arc = float(u.arc_slab_depth_m)
    t1 = math.radians(float(u.slab_shallow_dip_deg))
    t2 = math.radians(float(u.slab_deep_dip_deg))
    if not (0.0 < t1 < math.pi / 2 and 0.0 < t2 < math.pi / 2):
        raise ValueError("섭입판 경사는 0~90° 사이여야 합니다")
    if not (0.0 <= h1 <= h_arc):
        raise ValueError("0 ≤ slab_shallow_depth_m ≤ arc_slab_depth_m 이어야 합니다")
    return h1 / math.tan(t1) + (h_arc - h1) / math.tan(t2)


def noise_points(graph: CellGraph, radius_m: float) -> np.ndarray:
    """노이즈를 읽을 점 (N, 3): 구면은 단위 벡터, 평면은 pos/R (같은 물리 길이로 변하게)."""
    if graph.kind == "sphere":
        return graph.unit()
    return np.ascontiguousarray(graph.pos / float(radius_m))


def generate_uplift(graph: CellGraph, fields: dict[str, np.ndarray], cfg) -> dict[str, np.ndarray]:
    """융기 필드를 만듭니다 (docs/pipeline.md 4.4).

    graph: CellGraph (구면 또는 평면). fields: crust_type, is_ocean, convergence_kind,
    subduction_side, convergence_m_per_yr [m/yr], dist_convergent_m [m], dist_divergent_m [m]
    (모두 (N,), 거리는 없으면 inf).
    반환: uplift_m_per_yr (N,) float64 [m/yr] (바다 0, 침강 음수), exhumation_m (N,) float64 [m],
    dist_arc_m (N,) float64 [m] (섭입 위판 쪽만 d_arc, 나머지 NaN).
    """
    if not isinstance(graph, CellGraph):
        raise ValueError("graph 는 bpcg.core.graph.CellGraph 여야 합니다")
    n = graph.n_cells
    for name in _REQUIRED:
        if name not in fields or np.shape(fields[name]) != (n,):
            raise ValueError(f"fields 에 (N,) 모양의 '{name}' 이 있어야 합니다")
    u = cfg.uplift
    cont = np.asarray(fields["crust_type"]) == 1
    is_ocean = np.asarray(fields["is_ocean"]).astype(bool)
    kind = np.asarray(fields["convergence_kind"])
    side = np.asarray(fields["subduction_side"])
    gamma = np.maximum(np.asarray(fields["convergence_m_per_yr"], dtype=np.float64), 0.0)
    d_conv = np.asarray(fields["dist_convergent_m"], dtype=np.float64)
    d_div = np.asarray(fields["dist_divergent_m"], dtype=np.float64)
    w = float(u.orogen_width_m)
    if not w > 0:
        raise ValueError(f"orogen_width_m 는 0 보다 커야 합니다: {w}")
    d_arc = arc_distance_m(cfg)
    radius = float(cfg.planet.radius_m)

    uplift = np.where(cont, float(u.craton_erosion_m_per_myr) * 1e-6, 0.0)

    overriding = (side == 1) & ((kind == KIND_OCEAN_CONTINENT) | (kind == KIND_OCEAN_OCEAN))
    collision = kind == KIND_COLLISION
    mountain = np.zeros(n)
    mountain[overriding] = (
        float(u.orogen_factor)
        * gamma[overriding]
        * np.exp(-(((d_conv[overriding] - d_arc) / w) ** 2))
    )
    mountain[collision] += (
        float(u.collision_factor)
        * gamma[collision]
        * np.exp(-((d_conv[collision] / (COLLISION_WIDTH_FACTOR * w)) ** 2))
    )
    active = mountain > 0.0
    if active.any():
        pts = noise_points(graph, radius)[active]
        xi = fbm3(
            pts,
            sub_seed(int(cfg.planet.seed), STREAM_OROGEN_NOISE),
            octaves=OROGEN_NOISE_OCTAVES,
            frequency=radius / (OROGEN_NOISE_WAVELENGTH_FACTOR * w),
        )
        mountain[active] *= 1.0 + OROGEN_NOISE_AMPLITUDE * xi
    uplift += mountain

    rift = cont & (d_div < RIFT_MAX_DIST_M)
    uplift[rift] += float(u.rift_subsidence_m_per_yr) * np.exp(-((d_div[rift] / RIFT_WIDTH_M) ** 2))
    uplift[is_ocean] = 0.0

    exhumation = np.clip(
        np.maximum(uplift, 0.0) * float(u.orogen_duration_myr) * 1e6,
        0.0,
        float(u.exhumation_cap_m),
    )
    dist_arc = np.where(overriding, d_arc, np.nan)
    return {"uplift_m_per_yr": uplift, "exhumation_m": exhumation, "dist_arc_m": dist_arc}
