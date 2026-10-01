"""아격자 기복 보정 (docs/pipeline.md 7.3, 설계도 3장 '골짜기 바닥 높이 + 기복 보정').

L0 칸(약 19.5 km)의 솔버 고도는 칸 안 큰 강의 골짜기 바닥입니다. 칸 안에 숨은 작은 강과 사면이
만드는 기복을 같은 경사 법칙으로 계산해 더합니다. L0 에서만 씁니다.

- 하천 시작점: Q* = (k_s/S_crit)^(1/θ), A* = Q*/R_eff. 여기서 하천 경사 k_s·Q^(−θ) 가 S_crit 와
  같습니다(가이드 4장). 알려진 빈칸: Kargère 2025 식으로 바꿀 예정입니다(설계도 5장 9번).
- 칸 안 하천 기복: Hack 법칙 L(A) = c_H·A^h (km, km²) 을 거꾸로 A(x) = (x/c_H)^(1/h) 로 쓰고,
  x* = L(A*) 부터 x_c = L(칸 면적) 까지 ∫ k_s·(R_eff·A(x))^(−θ) dx 를 로그 간격 32점 사다리꼴로
  적분합니다.
- 사면 기복: L_h = 0.5·sqrt(A*), 기복 = L_h·min(S_crit, U·L_h/D)/2.
- relief = 하천 + 사면 (바다 0), z_mean = z + mean_fraction·relief.
"""

import numpy as np

from bpcg.core.graph import CellGraph

N_QUADRATURE = 32  # 로그 간격 사다리꼴 점 수 (pipeline.md 7.3)
_KM = 1.0e3  # [m/km]
_KM2 = 1.0e6  # [m²/km²]


def _vec(x, n: int, name: str) -> np.ndarray:
    a = np.asarray(x, dtype=np.float64)
    if a.shape != (n,):
        raise ValueError(f"{name} 은 ({n},) 이어야 합니다: 모양 {a.shape}")
    return a


def channel_head_area(
    k_s: np.ndarray, s_crit: np.ndarray, runoff_eff: np.ndarray, cfg
) -> np.ndarray:
    """하천 시작 면적 A* = (k_s/S_crit)^(1/θ) / R_eff [m²] (pipeline.md 7.3).

    k_s: (N,) 강 가파름 (Q 단위 m³/yr). s_crit: (N,) [m/m]. runoff_eff: (N,) [m/yr].
    k_s, S_crit, R_eff 중 하나라도 0 이하인 칸은 0 입니다(하천이 생기지 않음).
    """
    theta = float(cfg.landscape.theta)
    if not theta > 0.0:
        raise ValueError(f"landscape.theta 는 0 보다 커야 합니다: {theta}")
    ok = (k_s > 0.0) & (s_crit > 0.0) & (runoff_eff > 0.0)
    out = np.zeros(k_s.shape, dtype=np.float64)
    q_star = (k_s[ok] / s_crit[ok]) ** (1.0 / theta)  # [m³/yr]
    out[ok] = q_star / runoff_eff[ok]
    return out


def river_relief(
    k_s: np.ndarray, runoff_eff: np.ndarray, a_star: np.ndarray, cell_area: np.ndarray, cfg
) -> np.ndarray:
    """칸 안 하천 기복 ∫_{x*}^{x_c} k_s·(R_eff·A(x))^(−θ) dx [m] (로그 간격 32점 사다리꼴).

    k_s: (N,) [Q 단위 m³/yr]. runoff_eff: (N,) [m/yr]. a_star: (N,) 하천 시작 면적 [m²].
    cell_area: (N,) 칸 면적 [m²]. A(x) = (x/c_H)^(1/h) 는 km·km² 단위 Hack 법칙의 역입니다.
    0 < A* < 칸 면적인 칸만 계산하고 나머지는 0 입니다.
    """
    theta = float(cfg.landscape.theta)
    c_h = float(cfg.relief.hack_coefficient_km)
    h = float(cfg.relief.hack_exponent)
    if not (c_h > 0.0 and h > 0.0):
        raise ValueError(
            f"relief.hack_coefficient_km, hack_exponent 는 0 보다 커야 합니다: {c_h}, {h}"
        )
    out = np.zeros(k_s.shape, dtype=np.float64)
    ok = (a_star > 0.0) & (a_star < cell_area) & (k_s > 0.0) & (runoff_eff > 0.0)
    if not ok.any():
        return out
    ks = k_s[ok]
    R = runoff_eff[ok]
    x_star = c_h * (a_star[ok] / _KM2) ** h  # [km]
    x_c = c_h * (cell_area[ok] / _KM2) ** h  # [km]
    # 로그 간격 점에서는 u = ln x 로 바꿔 ∫ f(x)·x du 를 사다리꼴로 적분합니다.
    # 피적분 함수가 거듭제곱 꼴이라 x 에서 바로 적분할 때보다 훨씬 정확합니다.
    du = np.log(x_c / x_star) / (N_QUADRATURE - 1)
    total = np.zeros(ks.shape, dtype=np.float64)
    g_prev = None
    for k in range(N_QUADRATURE):
        x = x_star * np.exp(du * k)  # [km]
        area_m2 = (x / c_h) ** (1.0 / h) * _KM2
        g_k = ks * (R * area_m2) ** (-theta) * (x * _KM)  # 하천 경사 [m/m] × x [m]
        if k > 0:
            total += 0.5 * (g_k + g_prev) * du
        g_prev = g_k
    out[ok] = total
    return out


def hillslope_relief(
    s_crit: np.ndarray, uplift: np.ndarray, a_star: np.ndarray, cell_area: np.ndarray, cfg
) -> np.ndarray:
    """사면 기복 L_h·min(S_crit, U·L_h/D)/2 [m], L_h = 0.5·sqrt(min(A*, 칸 면적)) [m].

    s_crit: (N,) [m/m]. uplift: (N,) U [m/yr], 0 이하이면 기복 0. a_star: (N,) [m²].
    cell_area: (N,) [m²]. D = landscape.hillslope_diffusivity_m2_per_yr (0 이면 S_crit 로 잘림).
    """
    diff = float(cfg.landscape.hillslope_diffusivity_m2_per_yr)
    if not diff >= 0.0:
        raise ValueError(
            f"landscape.hillslope_diffusivity_m2_per_yr 는 0 이상이어야 합니다: {diff}"
        )
    l_h = 0.5 * np.sqrt(np.minimum(a_star, cell_area))
    u = np.maximum(uplift, 0.0)
    with np.errstate(divide="ignore", invalid="ignore"):
        s_diff = np.where(diff > 0.0, u * l_h / diff, np.inf)
    s = np.minimum(np.maximum(s_crit, 0.0), s_diff)
    s = np.where(u > 0.0, s, 0.0)
    return l_h * s / 2.0


def subgrid_relief(
    graph: CellGraph,
    z: np.ndarray,
    k_s: np.ndarray,
    s_crit: np.ndarray,
    runoff_eff: np.ndarray,
    uplift: np.ndarray,
    is_ocean: np.ndarray,
    cfg,
) -> tuple[np.ndarray, np.ndarray]:
    """L0 칸 안의 아격자 기복과 평균 지표를 계산합니다 (pipeline.md 7.3).

    graph: CellGraph (칸 면적 [m²]). z: (N,) 골짜기 바닥 고도 [m] (솔버 결과).
    k_s: (N,) 강 가파름 (Q 단위 m³/yr). s_crit: (N,) 지표 암석 임계 경사 [m/m].
    runoff_eff: (N,) [m/yr]. uplift: (N,) U [m/yr]. is_ocean: (N,) bool.
    cfg: landscape.theta, landscape.hillslope_diffusivity_m2_per_yr, relief 절.
    반환: (relief_m (N,) float64 [m], 바다 0, z_mean_m (N,) float64 [m] = z + mean_fraction·relief).
    """
    if not isinstance(graph, CellGraph):
        raise ValueError(f"graph 는 CellGraph 여야 합니다: {type(graph).__name__}")
    n = graph.n_cells
    z = _vec(z, n, "z")
    k_s = _vec(k_s, n, "k_s")
    s_crit = _vec(s_crit, n, "s_crit")
    R = _vec(runoff_eff, n, "runoff_eff")
    U = _vec(uplift, n, "uplift")
    ocean = np.asarray(is_ocean)
    if ocean.shape != (n,) or ocean.dtype != np.bool_:
        raise ValueError(f"is_ocean 은 ({n},) bool 배열이어야 합니다: {ocean.shape} {ocean.dtype}")
    for name, a in (("z", z), ("k_s", k_s), ("s_crit", s_crit), ("runoff_eff", R), ("uplift", U)):
        if not np.isfinite(a[~ocean]).all():
            raise ValueError(f"육지 칸의 {name} 에 NaN 이나 inf 가 있습니다")
    frac = float(cfg.relief.mean_fraction)
    land = ~ocean
    area = graph.area
    a_star = np.zeros(n, dtype=np.float64)
    a_star[land] = channel_head_area(k_s[land], s_crit[land], R[land], cfg)
    relief = np.zeros(n, dtype=np.float64)
    relief[land] = river_relief(k_s[land], R[land], a_star[land], area[land], cfg)
    relief[land] += hillslope_relief(s_crit[land], U[land], a_star[land], area[land], cfg)
    z_mean = z + frac * relief
    return relief, z_mean
