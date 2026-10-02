"""가벼운 기후: 위도 띠 강수, 기온, 잠재 증발산, Budyko 유출 (docs/pipeline.md 4.5).

바람으로 수분을 옮기지 않습니다(지형성 강수는 여유 작업). 솔버 전에 기준 고도로 한 번 부르고,
솔버 뒤에는 최종 고도로 기온만 다시 계산합니다(surface_temperature).
"""

import math

import numpy as np

from bpcg.core.graph import CellGraph
from bpcg.core.noise import fbm3
from bpcg.planet.plates import sub_seed
from bpcg.planet.uplift import noise_points

STREAM_PRECIP_NOISE = 331
PRECIP_NOISE_FREQUENCY = 3.0  # 단위 구 기준 (기본 파장 약 2000 km)
PRECIP_NOISE_OCTAVES = 5


def latitude_rad(graph: CellGraph, cfg, lat_deg: float | None = None) -> np.ndarray:
    """칸마다 위도 φ = asin(p̂·axis) [rad] (N,).

    구면 그래프는 cfg.planet.axis 로 계산합니다. 평면 그래프는 위도가 하나라서 lat_deg [°] 를
    꼭 줘야 합니다.
    """
    if not isinstance(graph, CellGraph):
        raise ValueError("graph 는 bpcg.core.graph.CellGraph 여야 합니다")
    if graph.kind == "sphere":
        if lat_deg is not None:
            raise ValueError("구면 그래프에서는 lat_deg 를 주지 않습니다 (자전축으로 계산)")
        axis = np.asarray(cfg.planet.axis, dtype=np.float64)
        norm = float(np.linalg.norm(axis))
        if axis.shape != (3,) or not norm > 0:
            raise ValueError(f"planet.axis 는 길이가 0 이 아닌 3차원 벡터여야 합니다: {axis}")
        return np.arcsin(np.clip(graph.unit() @ (axis / norm), -1.0, 1.0))
    if lat_deg is None:
        raise ValueError("평면 그래프에는 위도 lat_deg [°] 를 줘야 합니다")
    lat = float(lat_deg)
    if not -90.0 <= lat <= 90.0:
        raise ValueError(f"lat_deg 는 -90~90 이어야 합니다: {lat}")
    return np.full(graph.n_cells, math.radians(lat))


def surface_temperature(latitude: np.ndarray, z: np.ndarray | None, cfg) -> np.ndarray:
    """기온 T = T_eq − (T_eq − T_pole)·sin²φ − lapse·max(z, 0) [°C] (N,).

    latitude: (N,) [rad]. z: (N,) 해수면 기준 고도 [m] 또는 None (0 으로 봄).
    """
    c = cfg.climate
    phi = np.asarray(latitude, dtype=np.float64)
    t_eq = float(c.t_equator_c)
    t = t_eq - (t_eq - float(c.t_pole_c)) * np.sin(phi) ** 2
    if z is not None:
        t = t - float(c.lapse_rate_c_per_m) * np.maximum(np.asarray(z, dtype=np.float64), 0.0)
    return t


def budyko_runoff(precip: np.ndarray, pet: np.ndarray) -> np.ndarray:
    """Budyko(1974) 유출 R = P − ET, ET = P·sqrt((PET/P)·tanh(P/PET)·(1 − e^(−PET/P))) [m/yr].

    precip, pet: (N,) [m/yr], 모두 0 보다 커야 합니다. 결과는 0 ≤ R ≤ P.
    """
    p = np.asarray(precip, dtype=np.float64)
    e = np.asarray(pet, dtype=np.float64)
    phi = e / p
    et = p * np.sqrt(phi * np.tanh(1.0 / phi) * (1.0 - np.exp(-phi)))
    return np.clip(p - et, 0.0, p)


def generate_climate(
    graph: CellGraph,
    cfg,
    z: np.ndarray | None = None,
    seed: int | None = None,
    lat_deg: float | None = None,
) -> dict[str, np.ndarray]:
    """가벼운 기후 필드를 만듭니다 (docs/pipeline.md 4.5).

    graph: 구면 그래프 또는 평면 그래프(이때 lat_deg [°] 필요). z: (N,) 해수면 기준 고도 [m]
    (None 이면 0). seed: 강수 노이즈 시드 (None 이면 cfg.planet.seed).
    반환 (모두 (N,) float64): precip_m_per_yr [m/yr], temperature_c [°C], pet_m_per_yr [m/yr],
    runoff_m_per_yr (Budyko) [m/yr], runoff_eff_m_per_yr = max(R, 비율·P, 바닥값) [m/yr].
    """
    phi = latitude_rad(graph, cfg, lat_deg)
    n = graph.n_cells
    if z is not None and np.shape(z) != (n,):
        raise ValueError(f"z 모양이 칸 수와 다릅니다: {np.shape(z)}, 기대 ({n},)")
    c = cfg.climate
    seed = int(cfg.planet.seed) if seed is None else int(seed)

    # 강수 (가이드 5장): 적도 봉우리 + 중위도 봉우리, 그 사이(약 30°)와 극은 저절로 건조.
    s1 = math.radians(float(c.p_equator_sigma_deg))
    phi_m = math.radians(float(c.p_midlat_center_deg))
    s2 = math.radians(float(c.p_midlat_sigma_deg))
    band = (
        float(c.p_base_m_per_yr)
        + float(c.p_equator) * np.exp(-((phi / s1) ** 2))
        + float(c.p_midlat) * np.exp(-(((np.abs(phi) - phi_m) / s2) ** 2))
    )
    eta = float(c.p_noise)
    if eta != 0.0:
        xi = fbm3(
            noise_points(graph, float(cfg.planet.radius_m)),
            sub_seed(seed, STREAM_PRECIP_NOISE),
            octaves=PRECIP_NOISE_OCTAVES,
            frequency=PRECIP_NOISE_FREQUENCY,
        )
        precip = band * np.exp(eta * xi - 0.5 * eta * eta)
    else:
        precip = band.copy()
    if not (precip > 0).all():
        raise ValueError("강수가 0 이하인 칸이 있습니다 (p_base_m_per_yr 를 확인하세요)")

    temp = surface_temperature(phi, z, cfg)
    pet = float(c.pet_per_degc_m_per_yr) * np.maximum(temp, 0.0) + float(c.pet_base_m_per_yr)
    if not (pet > 0).all():
        raise ValueError("잠재 증발산이 0 이하입니다 (pet_base_m_per_yr > 0 이어야 함)")
    runoff = budyko_runoff(precip, pet)
    runoff_eff = np.maximum(
        np.maximum(runoff, float(c.runoff_floor_fraction) * precip),
        float(c.runoff_floor_m_per_yr),
    )
    return {
        "precip_m_per_yr": precip,
        "temperature_c": temp,
        "pet_m_per_yr": pet,
        "runoff_m_per_yr": runoff,
        "runoff_eff_m_per_yr": runoff_eff,
    }
