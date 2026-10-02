"""1단계 재료 묶음: 거친 격자에서 사슬 1과 기후를 만들고 더 고운 격자(L0)로 옮깁니다.

build_materials: continent_mask → plates → crust → ocean(해수면, 바다) → uplift → climate.
transfer_materials: 거친 격자 → 고운 격자 (docs/pipeline.md 1·4장).

옮기는 규칙 (FIELDS 이름 기준)
- 범주 값, 가장 가까운 칸(nearest): plate_id, boundary_type, convergence_kind, subduction_side,
  crust_type. 단 수렴 경계 근처의 subduction_side 는 부호 거리의 부호로 다시 정합니다.
- 매끄러운 값, 선형 보간(linear): convergence_m_per_yr, spreading_m_per_yr, dist_divergent_m
  (inf 는 잠시 πR 로 두고 보간), crust_thickness_m, ocean_age_myr (대륙 NaN 을 가장 가까운
  해양 값으로 채워 보간한 뒤 대륙 칸만 다시 NaN), z_platform_base (해구 뺀 기준 고도).
- 고운 격자에서 다시 계산: dist_convergent_m (부호 거리 보간으로 경계선을 칸보다 곱게),
  z_platform_m (= 보간한 기준 고도 + 고운 격자 해구), 해수면·is_ocean (물 부피 보존),
  uplift_m_per_yr·exhumation_m·dist_arc_m (닫힌 식이라 고운 격자 값으로 다시),
  기후 5개 (고운 격자 고도로).
"""

import math
import time

import numpy as np

from bpcg.core.cubesphere import cell_of
from bpcg.core.distance import nearest_source_values
from bpcg.core.fields import check_fields
from bpcg.core.graph import CellGraph
from bpcg.core.resample import resample_sphere
from bpcg.planet.climate import generate_climate
from bpcg.planet.crust import continent_mask, generate_crust, trench_offset_m
from bpcg.planet.ocean import ocean_mask, sea_level
from bpcg.planet.plates import (
    KIND_OCEAN_CONTINENT,
    KIND_OCEAN_OCEAN,
    check_sphere_graph,
    generate_plates,
)
from bpcg.planet.uplift import generate_uplift

CATEGORICAL_FIELDS = (
    "plate_id",
    "boundary_type",
    "convergence_kind",
    "subduction_side",
    "crust_type",
)
LINEAR_FIELDS = (
    "convergence_m_per_yr",
    "spreading_m_per_yr",
    "dist_divergent_m",
    "crust_thickness_m",
    "ocean_age_myr",
)
RECOMPUTED_FIELDS = (
    "dist_convergent_m",
    "z_platform_m",
    "is_ocean",
    "uplift_m_per_yr",
    "exhumation_m",
    "dist_arc_m",
    "precip_m_per_yr",
    "temperature_c",
    "pet_m_per_yr",
    "runoff_m_per_yr",
    "runoff_eff_m_per_yr",
)
# 부호 거리 보간을 믿는 범위: 거친 칸 2개 안.
# 그 밖에서는 판 안쪽의 가짜 0 교차를 피하려고 |s| 를 보간합니다.
SIGNED_DIST_RANGE_CELLS = 2.0


def _sea_level_and_mask(graph: CellGraph, z_platform: np.ndarray, cfg) -> tuple[float, np.ndarray]:
    h = sea_level(z_platform, graph.area, float(cfg.ocean.water_volume_m3))
    return h, ocean_mask(graph, z_platform, h)


def _area_fraction(graph: CellGraph, mask: np.ndarray) -> float:
    return float(graph.area[mask].sum() / graph.area.sum())


def build_materials(graph: CellGraph, cfg) -> tuple[dict[str, np.ndarray], dict]:
    """한 구면 그래프(보통 거친 격자)에서 1단계 재료(사슬 1 + 기후)를 만듭니다.

    반환 fields (FIELDS 이름, (N,)): 판 8개, 지각 4개, is_ocean, 융기 3개, 기후 5개.
    z_platform_m 은 해수면 전 기준 고도입니다(바다 칸 수심은 z_platform_m − sea_level_m).
    반환 info: sea_level_m [m], ocean_fraction, continental_fraction (면적 비율),
      bathymetry_m (N,) = z_platform_m − sea_level_m [m] (해수면 0 기준 고도),
      z_platform_base_m (N,), trench_m (N,) [m], plates (generate_plates 의 info),
      coast_signed_m (N,) [m], seconds (단계별 시간 [s]).
    """
    check_sphere_graph(graph)
    sec: dict[str, float] = {}
    t0 = time.perf_counter()
    continental = continent_mask(graph, cfg)
    sec["continent_mask"] = time.perf_counter() - t0

    t = time.perf_counter()
    plate_fields, plate_info = generate_plates(graph, continental, cfg)
    sec["plates"] = time.perf_counter() - t

    t = time.perf_counter()
    crust_fields, crust_info = generate_crust(graph, plate_fields, continental, cfg)
    sec["crust"] = time.perf_counter() - t

    t = time.perf_counter()
    z_platform = crust_fields["z_platform_m"]
    h, is_ocean = _sea_level_and_mask(graph, z_platform, cfg)
    bathymetry = z_platform - h
    sec["ocean"] = time.perf_counter() - t

    t = time.perf_counter()
    fields: dict[str, np.ndarray] = {**plate_fields, **crust_fields, "is_ocean": is_ocean}
    fields.update(generate_uplift(graph, fields, cfg))
    sec["uplift"] = time.perf_counter() - t

    t = time.perf_counter()
    fields.update(generate_climate(graph, cfg, z=np.maximum(bathymetry, 0.0)))
    sec["climate"] = time.perf_counter() - t
    sec["total"] = time.perf_counter() - t0
    check_fields(fields)

    info = {
        "sea_level_m": h,
        "ocean_fraction": _area_fraction(graph, is_ocean),
        "continental_fraction": _area_fraction(graph, continental),
        "bathymetry_m": bathymetry,
        "z_platform_base_m": crust_info["z_platform_base_m"],
        "trench_m": crust_info["trench_m"],
        "coast_signed_m": crust_info["coast_signed_m"],
        "plates": plate_info,
        "seconds": sec,
    }
    return fields, info


def _finite_resample(
    values: np.ndarray, n_src: int, fine: CellGraph, big: float, nearest_idx: np.ndarray
) -> np.ndarray:
    """inf 를 big 으로 잠시 바꿔 선형 보간하고, 가장 가까운 거친 칸이 inf 인 칸은 inf 로 되돌림."""
    v = np.asarray(values, dtype=np.float64)
    finite = np.isfinite(v)
    out = resample_sphere(np.where(finite, v, big), n_src, fine, "linear")
    out[~finite[nearest_idx]] = np.inf
    return out


def transfer_materials(
    coarse_graph: CellGraph,
    coarse_fields: dict[str, np.ndarray],
    coarse_info: dict | None,
    fine_graph: CellGraph,
    cfg,
) -> tuple[dict[str, np.ndarray], dict]:
    """거친 격자 재료를 고운 구면 그래프(L0)로 옮기고, 좁은 것과 해수면·기후는 다시 계산합니다.

    coarse_graph: 거친 구면 그래프 (노드 흔들기 없음, 면당 n_c 칸).
    coarse_fields: build_materials 의 fields.
    coarse_info: build_materials 의 info (z_platform_base_m 을 읽음, None 이면 z_platform_m − 해구로
    다시 만듦). fine_graph: 고운 구면 그래프 (흔들기 있어도 됨).
    반환 fields: build_materials 와 같은 이름 (고운 격자 (N_f,)). 규칙은 모듈 docstring.
    반환 info: sea_level_m, ocean_fraction, continental_fraction, bathymetry_m, z_platform_base_m,
      trench_m, seconds.
    """
    check_sphere_graph(coarse_graph)
    check_sphere_graph(fine_graph)
    n_src = int(coarse_graph.shape[1])
    if coarse_graph.n_cells != 6 * n_src * n_src:
        raise ValueError("coarse_graph 는 큐브스피어 구면 그래프여야 합니다")
    need = CATEGORICAL_FIELDS + LINEAR_FIELDS + ("dist_convergent_m", "z_platform_m")
    for name in need:
        if name not in coarse_fields or np.shape(coarse_fields[name]) != (coarse_graph.n_cells,):
            raise ValueError(f"coarse_fields 에 거친 격자 모양의 '{name}' 이 있어야 합니다")
    t0 = time.perf_counter()
    big = math.pi * float(coarse_graph.R)
    h_coarse = float(coarse_graph.spacing)
    out: dict[str, np.ndarray] = {}
    # 범주 값은 가장 가까운 거친 칸 (resample_sphere(..., "nearest") 와 같고, 칸 찾기를 한 번만 함).
    idx = cell_of(fine_graph.unit(), n_src)

    for name in CATEGORICAL_FIELDS:
        out[name] = np.asarray(coarse_fields[name])[idx]
    for name in ("convergence_m_per_yr", "spreading_m_per_yr", "crust_thickness_m"):
        out[name] = resample_sphere(
            np.asarray(coarse_fields[name], dtype=np.float64), n_src, fine_graph, "linear"
        )
    out["dist_divergent_m"] = _finite_resample(
        coarse_fields["dist_divergent_m"], n_src, fine_graph, big, idx
    )

    # 해양저 나이: 대륙 NaN 을 가장 가까운 해양 값으로 채워 보간 → 고운 격자 대륙 칸은 NaN.
    age_c = np.asarray(coarse_fields["ocean_age_myr"], dtype=np.float64)
    is_oc = np.asarray(coarse_fields["crust_type"]) == 0
    if is_oc.any():
        _, _, filled = nearest_source_values(coarse_graph, is_oc, np.nan_to_num(age_c))
        filled = np.where(np.isfinite(filled), filled, 0.0)
        age_f = resample_sphere(filled, n_src, fine_graph, "linear")
        age_f = np.clip(age_f, 0.0, float(cfg.ocean.age_cap_myr))
    else:
        age_f = np.full(fine_graph.n_cells, np.nan)
    age_f[out["crust_type"] == 1] = np.nan
    out["ocean_age_myr"] = age_f

    # 수렴 경계 거리: 섭입판 쪽을 음수로 둔 부호 거리를 보간하면
    # 경계선(0)이 거친 칸보다 곱게 잡힙니다.
    side_c = np.asarray(coarse_fields["subduction_side"])
    d_conv_c = np.asarray(coarse_fields["dist_convergent_m"], dtype=np.float64)
    finite_c = np.isfinite(d_conv_c)
    d_abs_c = np.where(finite_c, d_conv_c, big)
    s_c = np.where(side_c == -1, -d_abs_c, d_abs_c)
    s_f = resample_sphere(s_c, n_src, fine_graph, "linear")
    abs_f = resample_sphere(d_abs_c, n_src, fine_graph, "linear")
    near = abs_f < SIGNED_DIST_RANGE_CELLS * h_coarse
    d_conv_f = np.where(near, np.abs(s_f), abs_f)
    d_conv_f[~finite_c[idx]] = np.inf
    kind_f = out["convergence_kind"]
    subduct = (kind_f == KIND_OCEAN_CONTINENT) | (kind_f == KIND_OCEAN_OCEAN)
    fix = near & subduct & (s_f != 0.0)
    side_f = out["subduction_side"].copy()
    side_f[fix] = np.where(s_f[fix] < 0.0, -1, 1).astype(side_f.dtype)
    out["subduction_side"] = side_f
    out["dist_convergent_m"] = d_conv_f

    # 기준 고도 = 보간한 해구 뺀 값 + 고운 격자에서 다시 계산한 해구.
    if coarse_info is not None and "z_platform_base_m" in coarse_info:
        base_c = np.asarray(coarse_info["z_platform_base_m"], dtype=np.float64)
    else:
        base_c = np.asarray(coarse_fields["z_platform_m"], dtype=np.float64) - trench_offset_m(
            d_conv_c, side_c, coarse_fields["convergence_kind"], cfg
        )
    base_f = resample_sphere(base_c, n_src, fine_graph, "linear")
    trench_f = trench_offset_m(d_conv_f, side_f, kind_f, cfg)
    z_platform_f = base_f + trench_f
    out["z_platform_m"] = z_platform_f
    t_resample = time.perf_counter() - t0

    t = time.perf_counter()
    h, is_ocean = _sea_level_and_mask(fine_graph, z_platform_f, cfg)
    out["is_ocean"] = is_ocean
    bathymetry = z_platform_f - h
    t_ocean = time.perf_counter() - t

    t = time.perf_counter()
    out.update(generate_uplift(fine_graph, out, cfg))
    out.update(generate_climate(fine_graph, cfg, z=np.maximum(bathymetry, 0.0)))
    t_rest = time.perf_counter() - t
    check_fields(out)

    info = {
        "sea_level_m": h,
        "ocean_fraction": _area_fraction(fine_graph, is_ocean),
        "continental_fraction": _area_fraction(fine_graph, out["crust_type"] == 1),
        "bathymetry_m": bathymetry,
        "z_platform_base_m": base_f,
        "trench_m": trench_f,
        "seconds": {
            "resample": t_resample,
            "ocean": t_ocean,
            "uplift_climate": t_rest,
            "total": time.perf_counter() - t0,
        },
    }
    return out, info
