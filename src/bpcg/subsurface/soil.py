"""흙과 충적층 두께 (docs/pipeline.md 8.2, 설계도 2장·5장 14번, 가이드 6장 퇴적물).

- 흙: 생산 상한 P₀(기온·강수로 늘어남, 상한 있음)와 깎임 E 의 균형 두께 h* = h₀·ln(P₀/E)
  (Heimsath 형 지수 감소 생산). 맨 암반은 주로 '흙이 버티는 경사보다 가파른 비탈'입니다
  (설계도 5장 14번: 융기가 생산보다 빠르면 맨 암반이라는 규칙은 과해서 P₀ 상한을 mm/yr 까지 엶).
- 충적층: 퇴적이 지배하는 완만한 칸에 가이드 6장 모양으로 쌓고, 선상지는 올린 높이를 더합니다.
"""

import numpy as np

from bpcg.subsurface.water import _as_mask, _as_vector, _result_array

# ---------------------------------------------------------------- 명세 상수 (설정에 없는 값)
SOIL_TEMP_COEF_PER_C = 0.07  # 생산률의 기온 의존 e^(0.07(T − 15)) [1/°C]
SOIL_TEMP_REF_C = 15.0  # 기준 기온 [°C]
SOIL_PRECIP_REF_M_PER_YR = 1.0  # 강수 배율 P/1 m/yr 의 기준 [m/yr]
SOIL_PRECIP_FACTOR_MAX = 2.0  # 강수 배율 상한 min(2, P/1 m/yr)
MIN_DENUDATION_M_PER_YR = 1e-7  # 깎임 E = max(U, 1e-7) [m/yr] (융기 0 에서 흙이 무한대가 되지 않게)
ALLUVIUM_MAX_SLOPE = 0.02  # 충적층이 쌓이는 칸의 경사 상한 [m/m]
ALLUVIUM_Q_LOW_M3_PER_YR = 1e6  # 충적층 두께가 0 에서 오르기 시작하는 유량 Q₁ [m³/yr] (가이드 6장)
ALLUVIUM_Q_HIGH_M3_PER_YR = 1e9  # 충적층 두께가 최대가 되는 유량 Q₂ [m³/yr]
ALLUVIUM_SLOPE_SCALE = 0.01  # 두께가 0 이 되는 경사 S_dep [m/m] (1 − sst(S/0.01))


def smoothstep01(x: np.ndarray) -> np.ndarray:
    """[0, 1] 로 자른 smoothstep sst(x) = 3t² − 2t³, t = clip(x, 0, 1). 같은 모양 float64."""
    t = np.clip(np.asarray(x, dtype=np.float64), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def soil_production_max(temperature: np.ndarray, precip: np.ndarray, cfg) -> np.ndarray:
    """흙 생산 상한 P₀ = min(cap, P_max·e^(0.07(T − 15))·min(2, P/1 m/yr)) [m/yr].

    temperature: 연평균 기온 [°C]. precip: 연강수 [m/yr] (음수는 0). 같은 모양으로 브로드캐스트.
    cfg: soil.production_max_m_per_yr, soil.production_max_cap_m_per_yr.
    습하고 따뜻한 활성 산지에서 mm/yr 까지 허용합니다(설계도 5장 14번, Larsen 2014).
    """
    s = cfg.soil
    p_max = float(s.production_max_m_per_yr)
    cap = float(s.production_max_cap_m_per_yr)
    if not (p_max > 0.0 and cap > 0.0):
        raise ValueError(
            f"soil.production_max_m_per_yr, production_max_cap_m_per_yr 는 0 보다 커야 합니다: "
            f"{p_max}, {cap}"
        )
    T = np.asarray(temperature, dtype=np.float64)
    P = np.maximum(np.asarray(precip, dtype=np.float64), 0.0)
    wet = np.minimum(SOIL_PRECIP_FACTOR_MAX, P / SOIL_PRECIP_REF_M_PER_YR)
    return np.minimum(cap, p_max * np.exp(SOIL_TEMP_COEF_PER_C * (T - SOIL_TEMP_REF_C)) * wet)


def soil_and_alluvium(
    z: np.ndarray,
    slope: np.ndarray,
    uplift: np.ndarray,
    temperature: np.ndarray,
    precip: np.ndarray,
    result,
    fan_raise: np.ndarray | None,
    surface_rock: np.ndarray,
    cfg,
    is_ocean: np.ndarray | None = None,
) -> dict[str, np.ndarray]:
    """흙 두께·충적층 두께·맨 암반을 정합니다 (pipeline.md 8.2).

    z: (N,) 고도 [m] (칸 수 확인용). slope: (N,) 경사 [m/m]. uplift: (N,) U [m/yr].
    temperature: (N,) 기온 [°C]. precip: (N,) 강수 [m/yr].
    result: SolverResult(discharge, sediment_flux 속성) 또는 FIELDS 이름 dict
    (discharge_m3_per_yr, sediment_flux_m3_per_yr). fan_raise: (N,) 선상지로 올린 높이 [m]
    (z_new − z, 0 이상) 또는 None. surface_rock: (N,) 지표 암석 번호 (모양 확인만 하고, 지금
    식에는 쓰지 않습니다. 암석별 생산률은 알려진 빈칸). cfg: soil 절, landscape.deposition_g,
    climate.runoff_ref_m_per_yr. is_ocean: (N,) bool 또는 None(모두 육지). 바다는 모두 0 입니다.

    - 흙: E = max(U, 1e-7), h* = h₀·ln(P₀/E) (P₀ > E), 아니면 0, thickness_cap_m 으로 자름.
      경사 > bare_slope 이면 0.
    - 충적층: G·R_ref·Qs/Q > U 이고 S < 0.02 인 칸에
      H = alluvium_max·sst((ln Q − ln 1e6)/(ln 1e9 − ln 1e6))·(1 − sst(S/0.01)),
      선상지는 + fan_raise.
    - 맨 암반: 흙과 충적층이 모두 0 인 육지 칸 (가파른 비탈과 흙 생산보다 빨리 깎이는 칸).

    반환: FIELDS 이름 dict. soil_thickness_m, alluvium_m (N,) float64 [m], bare_rock (N,) bool.
    """
    zz = np.asarray(z)
    if zz.ndim != 1:
        raise ValueError(f"z 는 (N,) 1차원이어야 합니다 (받은 모양: {zz.shape})")
    n = zz.shape[0]
    S = np.maximum(_as_vector(slope, n, "slope"), 0.0)
    U = _as_vector(uplift, n, "uplift")
    T = _as_vector(temperature, n, "temperature")
    P = _as_vector(precip, n, "precip")
    rock = np.asarray(surface_rock)
    if rock.shape != (n,):
        raise ValueError(f"surface_rock 은 ({n},) 배열이어야 합니다 (받은 모양: {rock.shape})")
    Q = np.asarray(_result_array(result, "discharge", "discharge_m3_per_yr", n), dtype=np.float64)
    Qs = np.asarray(
        _result_array(result, "sediment_flux", "sediment_flux_m3_per_yr", n), dtype=np.float64
    )
    if not (np.isfinite(Q).all() and np.isfinite(Qs).all()):
        raise ValueError("result 의 discharge, sediment_flux 에 NaN 이나 inf 가 있습니다")
    raise_m = np.zeros(n) if fan_raise is None else _as_vector(fan_raise, n, "fan_raise")
    if (raise_m < 0.0).any():
        raise ValueError("fan_raise 는 0 이상이어야 합니다 (선상지로 올린 높이)")
    land = np.ones(n, dtype=bool) if is_ocean is None else ~_as_mask(is_ocean, n, "is_ocean")

    s = cfg.soil
    h0 = float(s.decay_depth_m)
    cap = float(s.thickness_cap_m)
    bare_slope = float(s.bare_slope)
    a_max = float(s.alluvium_max_m)
    if not (h0 > 0.0 and cap >= 0.0 and bare_slope > 0.0 and a_max >= 0.0):
        raise ValueError(
            "soil 설정은 decay_depth_m > 0, thickness_cap_m ≥ 0, bare_slope > 0, "
            f"alluvium_max_m ≥ 0 이어야 합니다: {h0}, {cap}, {bare_slope}, {a_max}"
        )
    g_dep = float(cfg.landscape.deposition_g)
    r_ref = float(cfg.climate.runoff_ref_m_per_yr)

    # 흙 (Heimsath 형 지수 감소 생산과 깎임의 균형)
    P0 = soil_production_max(T, P, cfg)
    E = np.maximum(U, MIN_DENUDATION_M_PER_YR)
    with np.errstate(divide="ignore", invalid="ignore"):
        h_star = np.where(P0 > E, h0 * np.log(P0 / E), 0.0)
    soil = np.clip(h_star, 0.0, cap)
    soil[(S > bare_slope) | ~land] = 0.0

    # 충적층 (가이드 6장 모양, 퇴적 지배 칸만)
    pos_q = Q > 0.0
    q_safe = np.where(pos_q, Q, 1.0)
    deposition = pos_q & (g_dep * r_ref * Qs / q_safe > U) & (S < ALLUVIUM_MAX_SLOPE)
    lnq = (np.log(q_safe) - np.log(ALLUVIUM_Q_LOW_M3_PER_YR)) / (
        np.log(ALLUVIUM_Q_HIGH_M3_PER_YR) - np.log(ALLUVIUM_Q_LOW_M3_PER_YR)
    )
    shape = smoothstep01(lnq) * (1.0 - smoothstep01(S / ALLUVIUM_SLOPE_SCALE))
    alluvium = np.where(deposition, a_max * shape, 0.0) + raise_m
    alluvium[~land] = 0.0

    bare = land & (soil <= 0.0) & (alluvium <= 0.0)
    return {"soil_thickness_m": soil, "alluvium_m": alluvium, "bare_rock": bare}
