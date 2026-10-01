"""정상상태 솔버: 물길 방향 맞추기 반복 + 경사 법칙 + 층 경계별 적분 (docs/pipeline.md 7.1).

가이드 4장의 '바다에서부터 계단 쌓기'를 설계도 2·5장의 경사 법칙으로 바꾼 것입니다.
한 번의 반복은 다음과 같습니다.

1. 물길: z̃ = fill_epsilon(z), r = d8_receivers(z̃, prev, η), σ = topo_order(r).
2. 모으기: Q = Acc(A·R_eff + 들어오는 물), A↑ = Acc(A), Qs = max(Acc(U·A), 0).
3. 경사 법칙(칸마다, Yuan 2019 정상상태 + 사면 확산):
   E = U + G·R_ref·Qs/Q (E ≤ 0 이면 S = s_min),
   S_r = (E/K)^(1/n)·Q^(−θ), K = K_ref·K 배율, K_ref = U_ref/k_ref^n,
   S_h = E·a/D (a = A↑/w, w = sqrt(칸 면적)),
   S = clip(1/(1/S_r + 1/S_h), s_min, S_crit).
4. 층 경계별 적분: σ 순서(하류부터)로 z_c = z_r + ∫₀^d S(z) dx. 층마다 K 배율·S_crit 가 달라
   경계를 넘을 때 경사를 바꿉니다. 출구는 z = z_outlet.
   ε 바닥(가이드와 다름): 한 칸이 오르는 높이는 fill_epsilon_m(ε) 이상입니다. 칸이 작아
   s_min·d < ε 인 평지(히어로 25 m 에서 s_min·d = 0.25 mm)에서는 ε 채움이 솔버가 쌓은 z 를 다시
   들어 올려 물길이 채움 순서로 정해지고, 반복마다 그 순서가 바뀌며 끝없이 오갑니다. 바닥을 ε 로
   두면 솔버가 쌓은 z 가 ε 채움의 고정점이라 물길이 실제 지형의 가장 가파른 방향으로 정해지고,
   그 뒤 반복에서는 ε 채움을 건너뜁니다. 실제 최소 경사는 max(s_min, ε/d) 입니다.
5. 멈춤: n_changed == 0 그리고 max|z_new − z| < stop_dz_m, 상한 max_flow_iterations.

진동 칸 고정(가이드와 다름). η 히스테리시스만으로는 끝나지 않는 순환이 남습니다. 예를 들어
침강하는 해안 저지대에서 수신 셀이 바뀌면 상류 퇴적물이 들어오거나 끊겨 E 의 부호가 바뀌고,
층 경계 근처에서는 수신 셀에 따라 지표 층이 바뀌어 고도가 수십 m 씩 뜁니다. 융기가 없는 퇴적
평야(G-법칙)에서는 퇴적물을 실은 강이 둑처럼 높아져 옆 칸으로 넘치므로(범람·유로 변경) D8
정상상태가 아예 없습니다. 이런 칸들은 2~3 반복 주기로 같은 방향들을 오갑니다.
그래서 방향이 freeze_after_flips 번(설정 landscape.freeze_after_flips, 없으면 16) 바뀐 칸은,
옛 수신 셀이 아직 z̃ 에서 낮은 이웃이면 그대로 둡니다. 고정된 칸도 z 는 법칙대로 쌓이므로
자기일관성과 '웅덩이 없음'은 그대로이고, 가장 가파른 방향이라는 조건만 그 칸에서 느슨해집니다.
잰 값: 원형 섬 96²~200² 은 고정 칸 0, 구면 L0(면당 512, 157만 칸)은 170회에 고정 171칸(육지의
0.03%), 평면 1280²(100 m)은 574회에 육지의 1.2% 입니다.

n = 1 이면 조화 결합은 정확합니다. 두 과정이 같은 경사 S 로 함께 깎는다고 보면
E = K·Q^m·S + D·S/a 이므로 S = E/(K·Q^m + D/a) = 1/(1/S_r + 1/S_h) 입니다.
수렴은 수신 셀 r 이 그대로일 때 정확한 고정점입니다. Φ 는 z 에 r 을 통해서만 의존하기
때문입니다(가이드 4장).
"""

import time
from collections.abc import Callable
from dataclasses import dataclass, field

import numpy as np
from numba import njit, prange

from bpcg.core.distance import nearest_source_values
from bpcg.core.fields import FIELDS
from bpcg.core.graph import CellGraph
from bpcg.core.hashing import hash3
from bpcg.core.noise import fbm3
from bpcg.geology.model import LayerColumns, layer_index_at
from bpcg.hydro.accumulate import accumulate
from bpcg.hydro.depressions import fill_epsilon
from bpcg.hydro.routing import d8_receivers, topo_order

# 초기 지형의 명세 값 (pipeline.md 7.1 '초기 지형', 설정에 없는 값)
INIT_SLOPE = 1.0e-3  # 출구까지 거리에 곱하는 기울기 [m/m]
INIT_NOISE_M = 10.0  # 초기 노이즈 진폭 [m]
# 초기 노이즈의 기본 파장 [칸 크기 배수]. 명세에 없어 우리가 정한 값이고, 물길의 첫 갈래만 정합니다.
INIT_NOISE_WAVELENGTH_CELLS = 16.0
_STREAM_INIT_NOISE = 7101  # 해시 용도 번호 (다른 모듈의 101·102·2xx·5201 과 겹치지 않게)
# 진동 칸 고정 문턱의 기본값 (설정 landscape.freeze_after_flips 가 없을 때). 우리가 정한 값입니다.
FREEZE_AFTER_FLIPS = 16


# ---------------------------------------------------------------- 결과
@dataclass(eq=False)
class SolverResult:
    """정상상태 솔버 결과 (pipeline.md 7.1 표). 배열은 모두 (N,) 입니다.

    z: 고도 [m] float64. receiver: 수신 셀 int64 (출구는 자기 자신). order: 하류부터 순서 int64.
    discharge: Q [m³/yr]. drainage_area: A↑ [m²]. sediment_flux: Qs [m³/yr] (0 이상).
    slope: 수신 셀까지 실제 경사 (z_c − z_r)/d [m/m] (출구 0).
    k_s: 지표 암석의 강 가파름 (E/K)^(1/n) (Q 단위 m³/yr, 출구·E ≤ 0 이면 0).
    s_crit_surface: 지표 암석의 임계 경사 [m/m].
    n_frozen, frozen: 진동 칸 고정에 걸린 칸 수와 (N,) bool 표시(수신 셀이 가장 가파른 이웃이
    아닐 수 있는 칸).
    receiver·Q·Qs 는 z 를 쌓을 때 쓴 값이라 z 와 서로 맞습니다(수렴하면 z 로 다시 계산해도 같음).
    """

    z: np.ndarray
    receiver: np.ndarray
    order: np.ndarray
    discharge: np.ndarray
    drainage_area: np.ndarray
    sediment_flux: np.ndarray
    slope: np.ndarray
    k_s: np.ndarray
    s_crit_surface: np.ndarray
    iterations: int
    history: list[dict] = field(default_factory=list)
    converged: bool = False
    seconds: float = 0.0
    n_frozen: int = 0
    frozen: np.ndarray | None = None

    def fields(self, cast: bool = False) -> dict[str, np.ndarray]:
        """FIELDS 이름으로 된 필드 dict.

        z_m, receiver, drainage_area_m2, discharge_m3_per_yr, sediment_flux_m3_per_yr, slope,
        k_s, s_crit. cast=False 면 계산 정밀도 그대로(고도 float64), True 면 FIELDS 의 dtype
        (저장용, 예: z_m float32, receiver int32) 으로 바꿉니다.
        """
        out = {
            "z_m": self.z,
            "receiver": self.receiver,
            "drainage_area_m2": self.drainage_area,
            "discharge_m3_per_yr": self.discharge,
            "sediment_flux_m3_per_yr": self.sediment_flux,
            "slope": self.slope,
            "k_s": self.k_s,
            "s_crit": self.s_crit_surface,
        }
        if cast:
            out = {k: v.astype(FIELDS[k].dtype) for k, v in out.items()}
        return out

    def diag(self) -> dict:
        """진단값 dict: 반복 수, 수렴 여부, 시간 [s], 마지막 방향 변화·max|Δz| [m], 고정 칸 수."""
        last = self.history[-1] if self.history else {"n_changed": -1, "max_dz": np.nan}
        return {
            "iterations": self.iterations,
            "converged": self.converged,
            "seconds": self.seconds,
            "final_n_changed": int(last["n_changed"]),
            "final_max_dz_m": float(last["max_dz"]),
            "n_frozen": self.n_frozen,
        }


# ---------------------------------------------------------------- 경사 법칙 (numba)
@njit(cache=True, inline="always")
def _upper_layer(bottom: np.ndarray, c: int, z: float) -> int:
    """고도 z 에서 위로 올라갈 때 지나는 층 번호: bottom ≤ z 인 첫 층, 없으면 L(기반암).

    rock_at 의 약속(경계 위의 점은 아래층)과 달리 z 바로 위 (z, z+δ) 가 속한 층입니다.
    그래서 그 층의 위 경계 bottom[i−1] 은 늘 z 보다 높고, 두께 0 층은 저절로 건너뜁니다.
    """
    n_layers = bottom.shape[1]
    for i in range(n_layers):
        if bottom[c, i] <= z:
            return i
    return n_layers


@njit(cache=True, inline="always")
def _law_slope(
    ks_ref: float, kmult: float, inv_n: float, qt: float, hill: float, scrit: float, s_min: float
) -> float:
    """한 층의 경사 S = clip(1/(1/S_r + 1/S_h), s_min, S_crit) [m/m].

    ks_ref: K 배율 1 에서의 k_s = k_ref·(E/U_ref)^(1/n). qt: Q^θ. hill: 1/S_h = D/(E·a).
    """
    ks = ks_ref * kmult ** (-inv_n)  # (E/K)^(1/n), K = K_ref·K 배율
    s = 1.0 / (qt / ks + hill)  # 1/S_r = Q^θ / k_s
    if s > scrit:
        s = scrit
    if s < s_min:
        s = s_min
    return s


@njit(cache=True)
def _receiver_slot_distance(c: int, r: int, nbr: np.ndarray, dist: np.ndarray) -> float:
    """칸 c 에서 수신 셀 r 까지 거리 [m] (이웃이 아니면 inf)."""
    for s in range(nbr.shape[1]):
        if nbr[c, s] == r:
            return dist[c, s]
    return np.inf


@njit(cache=True)
def _integrate_kernel(
    order: np.ndarray,
    rcv: np.ndarray,
    nbr: np.ndarray,
    dist: np.ndarray,
    z_outlet: np.ndarray,
    Q: np.ndarray,
    A_up: np.ndarray,
    Qs: np.ndarray,
    U: np.ndarray,
    width: np.ndarray,
    bottom: np.ndarray,
    k_mult: np.ndarray,
    s_crit: np.ndarray,
    k_ref: float,
    u_ref: float,
    inv_n: float,
    theta: float,
    g_dep: float,
    r_ref: float,
    diff: float,
    s_min: float,
    rise_min: float,
    z_new: np.ndarray,
) -> int:
    """경사 법칙 + 층 경계별 적분을 σ 순서(하류부터)로 한 번에 계산해 z_new 를 채웁니다.

    order: (N,) 하류부터 순서. rcv: (N,) 수신 셀(출구는 자기 자신). nbr, dist: (N, K).
    z_outlet: (N,) 출구 고도 [m]. Q [m³/yr], A_up [m²], Qs [m³/yr], U [m/yr], width [m]: (N,).
    bottom: (N, L) 층 바닥 [m]. k_mult, s_crit: (N, L+1). rise_min: 한 칸이 수신 셀보다
    적어도 높아야 하는 양 [m] (= fill_epsilon_m). 나머지는 설정 상수.
    반환: 수신 셀이 이웃이 아닌 칸 수 (0 이어야 정상).
    """
    n = order.shape[0]
    n_bad = 0
    for q in range(n):
        c = order[q]
        r = rcv[c]
        if r == c:
            z_new[c] = z_outlet[c]
            continue
        d = _receiver_slot_distance(c, r, nbr, dist)
        if not np.isfinite(d):
            n_bad += 1
            d = 0.0
        z = z_new[r]
        z_floor = z + rise_min
        # 필요한 깎임 E = U + G·R_ref·Qs/Q (Yuan 2019 정상상태) [m/yr]
        e = U[c] + g_dep * r_ref * Qs[c] / Q[c]
        if e <= 0.0:
            z = z + s_min * d
            z_new[c] = z if z >= z_floor else z_floor
            continue
        qt = np.exp(theta * np.log(Q[c]))
        a = A_up[c] / width[c]  # 비유역 면적 [m]
        hill = diff / (e * a)  # 1/S_h
        ks_ref = k_ref * (e / u_ref) ** inv_n
        rem = d
        # 층 경계별 적분: 지금 층 경사로 올라가다 위 경계에 먼저 닿으면 그 층으로 바꿉니다.
        while True:
            i = _upper_layer(bottom, c, z)
            s = _law_slope(ks_ref, k_mult[c, i], inv_n, qt, hill, s_crit[c, i], s_min)
            if i == 0:
                z += rem * s
                break
            up = bottom[c, i - 1]
            need = (up - z) / s  # 위 경계까지 남은 수평 거리 [m]
            if need >= rem:
                z += rem * s
                break
            z = up
            rem -= need
        z_new[c] = z if z >= z_floor else z_floor
    return n_bad


@njit(cache=True, parallel=True)
def _surface_law_kernel(
    rcv: np.ndarray,
    nbr: np.ndarray,
    dist: np.ndarray,
    z: np.ndarray,
    Q: np.ndarray,
    Qs: np.ndarray,
    U: np.ndarray,
    bottom: np.ndarray,
    k_mult: np.ndarray,
    s_crit: np.ndarray,
    k_ref: float,
    u_ref: float,
    inv_n: float,
    g_dep: float,
    r_ref: float,
    slope: np.ndarray,
    k_s: np.ndarray,
    s_crit_out: np.ndarray,
) -> None:
    """실제 경사, 지표 암석의 k_s 와 S_crit 를 칸마다 채웁니다 (경쟁 없음, prange).

    지표 암석은 rock_at 과 같은 약속(layer_index_at, 경계 위의 점은 아래층)으로 고릅니다.
    적분의 마지막 구간이 쓴 층과 같습니다.
    """
    n = rcv.shape[0]
    for c in prange(n):
        i = layer_index_at(bottom, c, z[c])
        s_crit_out[c] = s_crit[c, i]
        r = rcv[c]
        if r == c:
            slope[c] = 0.0
            k_s[c] = 0.0
            continue
        d = _receiver_slot_distance(c, r, nbr, dist)
        slope[c] = (z[c] - z[r]) / d
        e = U[c] + g_dep * r_ref * Qs[c] / Q[c]
        if e > 0.0:
            k_s[c] = k_ref * (e / (u_ref * k_mult[c, i])) ** inv_n
        else:
            k_s[c] = 0.0


@njit(cache=True)
def _freeze_kernel(
    zt: np.ndarray, prev: np.ndarray, rcv: np.ndarray, flips: np.ndarray, max_flips: int
) -> tuple[int, int]:
    """방향이 max_flips 번 바뀐 칸은 옛 수신 셀이 아직 낮으면 되돌리고, 바뀐 칸 수를 셉니다.

    zt: (N,) ε 채움 고도 [m]. prev: (N,) 지난 수신 셀. rcv: (N,) 새 수신 셀 (그 자리에서 고침).
    flips: (N,) int32 칸마다 방향이 바뀐 횟수 (그 자리에서 늘림).
    반환: (n_changed, n_frozen) = (rcv != prev 인 칸 수, flips ≥ max_flips 인 칸 수).
    """
    n = rcv.shape[0]
    n_changed = 0
    n_frozen = 0
    for c in range(n):
        p = prev[c]
        if rcv[c] != p:
            if flips[c] >= max_flips and p != c and zt[p] < zt[c]:
                rcv[c] = p
            else:
                flips[c] += 1
                n_changed += 1
        if flips[c] >= max_flips:
            n_frozen += 1
    return n_changed, n_frozen


@njit(cache=True, parallel=True)
def _eps_drop_count(z: np.ndarray, rcv: np.ndarray, is_outlet: np.ndarray, eps: float) -> int:
    """z_c ≥ z_r(c) + ε 를 만족하지 않는 비출구 칸 수 (fill_epsilon 커널과 같은 비교)."""
    n_bad = 0
    for c in prange(z.shape[0]):
        if not is_outlet[c] and not (z[c] >= z[rcv[c]] + eps):
            n_bad += 1
    return n_bad


def _has_eps_drop(z: np.ndarray, rcv: np.ndarray, is_outlet: np.ndarray, eps: float) -> bool:
    """모든 비출구 칸이 수신 셀보다 ε 이상 높으면 True (그러면 fill_epsilon(z) == z)."""
    return _eps_drop_count(z, rcv, is_outlet, eps) == 0


# ---------------------------------------------------------------- 입력 검사
def _vector(x, n: int, name: str, allow_none: bool = False) -> np.ndarray | None:
    if x is None:
        if allow_none:
            return None
        raise ValueError(f"{name} 가 없습니다")
    a = np.asarray(x, dtype=np.float64)
    if a.ndim == 0:
        a = np.full(n, float(a))
    if a.shape != (n,):
        raise ValueError(f"{name} 은 ({n},) 이어야 합니다: 모양 {a.shape}")
    return np.ascontiguousarray(a)


def _law_params(cfg) -> dict:
    """경사 법칙 상수를 설정에서 읽습니다 (landscape 절 + climate.runoff_ref_m_per_yr)."""
    ls = cfg.landscape
    p = {
        "theta": float(ls.theta),
        "n": float(ls.slope_exponent_n),
        "k_ref": float(ls.k_ref),
        "u_ref": float(ls.u_ref_m_per_yr),
        "diff": float(ls.hillslope_diffusivity_m2_per_yr),
        "g_dep": float(ls.deposition_g),
        "r_ref": float(cfg.climate.runoff_ref_m_per_yr),
        "s_min": float(ls.s_min),
        "s_crit_default": float(ls.s_crit_default),
    }
    for key in ("n", "k_ref", "u_ref", "r_ref", "s_min", "s_crit_default"):
        if not (np.isfinite(p[key]) and p[key] > 0.0):
            raise ValueError(f"설정 값 {key} 는 0 보다 커야 합니다: {p[key]}")
    for key in ("theta", "diff", "g_dep"):
        if not (np.isfinite(p[key]) and p[key] >= 0.0):
            raise ValueError(f"설정 값 {key} 는 0 이상이어야 합니다: {p[key]}")
    return p


def _layer_arrays(layers: LayerColumns | None, n: int, s_crit_default: float):
    """솔버 커널에 넘길 (bottom (N, L), k_mult (N, L+1), s_crit (N, L+1)) 배열."""
    if layers is None:
        return (
            np.empty((n, 0), dtype=np.float64),
            np.ones((n, 1), dtype=np.float64),
            np.full((n, 1), s_crit_default, dtype=np.float64),
        )
    if not isinstance(layers, LayerColumns):
        raise ValueError(
            f"layers 는 geology.model.LayerColumns 또는 None 이어야 합니다: {layers!r}"
        )
    if layers.n_cells != n:
        raise ValueError(f"layers 의 칸 수 {layers.n_cells} 가 그래프 칸 수 {n} 와 다릅니다")
    if not (np.isfinite(layers.bottom).all() and np.isfinite(layers.k_mult).all()):
        raise ValueError("layers 의 bottom·k_mult 에 NaN 이나 inf 가 있습니다")
    if not (layers.k_mult > 0.0).all() or not (layers.s_crit > 0.0).all():
        raise ValueError("layers 의 k_mult 와 s_crit 는 모두 0 보다 커야 합니다")
    return layers.bottom, layers.k_mult, layers.s_crit


def _check_graph(graph: CellGraph) -> int:
    if not isinstance(graph, CellGraph):
        raise ValueError(f"graph 는 CellGraph 여야 합니다: {type(graph).__name__}")
    return graph.n_cells


def _check_outlets(is_outlet, n: int) -> np.ndarray:
    m = np.asarray(is_outlet)
    if m.shape != (n,) or m.dtype != np.bool_:
        raise ValueError(f"is_outlet 은 ({n},) bool 배열이어야 합니다: {m.shape} {m.dtype}")
    if not m.any():
        raise ValueError("출구 칸이 하나도 없습니다")
    return np.ascontiguousarray(m)


# ---------------------------------------------------------------- 초기 지형
def initial_surface(
    graph: CellGraph, is_outlet: np.ndarray, z_outlet: float | np.ndarray, cfg
) -> np.ndarray:
    """솔버의 초기 지형 z = z_outlet + 1e-3·(출구까지 거리) + 10 m·fbm (pipeline.md 7.1).

    graph: CellGraph. is_outlet: (N,) bool. z_outlet: 스칼라 또는 (N,) 출구 고도 [m]
    (가장 가까운 출구의 값을 씁니다). 거리는 core.distance.nearest_source(구면 대원, 평면 유클리드).
    fbm 은 파장 16칸, 시드 hash3(planet.seed, 7101, 0) 입니다.
    반환: (N,) float64 [m], 출구는 z_outlet.
    물길이 처음부터 출구 쪽을 향하게 하는 것이 목적입니다.
    """
    n = _check_graph(graph)
    mask = _check_outlets(is_outlet, n)
    zo = _vector(z_outlet, n, "z_outlet")
    if not np.isfinite(zo[mask]).all():
        raise ValueError("출구 칸의 z_outlet 에 NaN 이나 inf 가 있습니다")
    dist, _, z_src = nearest_source_values(graph, mask, zo)
    if not np.isfinite(dist).all():
        raise ValueError(f"출구에 닿지 않는 칸이 {int((~np.isfinite(dist)).sum())}개 있습니다")
    seed = int(
        hash3(np.int64(cfg.planet.seed), np.int64(_STREAM_INIT_NOISE), np.int64(0)) >> np.uint64(33)
    )
    pts = graph.pos / (INIT_NOISE_WAVELENGTH_CELLS * graph.spacing)
    z = z_src + INIT_SLOPE * dist + INIT_NOISE_M * fbm3(pts, seed)
    z[mask] = zo[mask]
    return z


# ---------------------------------------------------------------- 솔버
def solve_steady_state(
    graph: CellGraph,
    is_outlet: np.ndarray,
    z_outlet: float | np.ndarray,
    uplift: np.ndarray,
    runoff_eff: np.ndarray,
    layers: LayerColumns | None,
    cfg,
    z_init: np.ndarray | None = None,
    extra_inflow: np.ndarray | None = None,
    max_iter: int | None = None,
    log: Callable[[str], None] | None = None,
) -> SolverResult:
    """정상상태 지형을 물길 방향 맞추기 반복으로 풉니다 (pipeline.md 7.1, 가이드 4장).

    graph: CellGraph (구면 L0 또는 평면 히어로). is_outlet: (N,) bool 바다·출구 칸.
    z_outlet: 스칼라 또는 (N,) 출구 고도 [m] (출구 칸 값만 씀). uplift: (N,) U [m/yr]
    (침강은 음수). runoff_eff: (N,) 침식용 유출 R_eff [m/yr], 출구가 아닌 칸은 0 보다 커야 합니다.
    layers: geology.model.LayerColumns 또는 None
    (None 이면 기준 암석 하나: K 배율 1, S_crit = s_crit_default).
    cfg: 설정 (landscape 절, climate.runoff_ref_m_per_yr, planet.seed).
    z_init: (N,) 초기 고도 [m] 또는 None(initial_surface). extra_inflow: (N,) 밖에서 들어오는 물
    [m³/yr] 또는 None. 그 칸에서 하류로 함께 모입니다(히어로 경계).
    max_iter: 반복 상한, None 이면 landscape.max_flow_iterations.
    log: 반복마다 부를 함수(str 한 줄) 또는 None.

    반환: SolverResult. history 는 반복마다 {iteration, n_changed, max_dz, n_frozen} 입니다.
    멈춤 조건은 n_changed == 0 그리고 max|Δz| < stop_dz_m 이고, 만족하면 converged = True 입니다.
    """
    t0 = time.perf_counter()
    n = _check_graph(graph)
    mask = _check_outlets(is_outlet, n)
    p = _law_params(cfg)
    zo = _vector(z_outlet, n, "z_outlet")
    if not np.isfinite(zo[mask]).all():
        raise ValueError("출구 칸의 z_outlet 에 NaN 이나 inf 가 있습니다")
    U = _vector(uplift, n, "uplift")
    if not np.isfinite(U).all():
        raise ValueError("uplift 에 NaN 이나 inf 가 있습니다")
    R = _vector(runoff_eff, n, "runoff_eff")
    if not np.isfinite(R).all() or (R < 0.0).any():
        raise ValueError("runoff_eff 는 0 이상의 유한한 값이어야 합니다")
    if (R[~mask] <= 0.0).any():
        raise ValueError(
            "출구가 아닌 칸의 runoff_eff 는 0 보다 커야 합니다 (유출 바닥값을 적용하세요)"
        )
    inflow = _vector(extra_inflow, n, "extra_inflow", allow_none=True)
    if inflow is not None and (not np.isfinite(inflow).all() or (inflow < 0.0).any()):
        raise ValueError("extra_inflow 는 0 이상의 유한한 값이어야 합니다")
    bottom, k_mult, s_crit = _layer_arrays(layers, n, p["s_crit_default"])
    if max_iter is None:
        max_iter = int(cfg.landscape.max_flow_iterations)
    if int(max_iter) < 1:
        raise ValueError(f"max_iter 는 1 이상이어야 합니다: {max_iter}")
    max_iter = int(max_iter)
    eps = float(cfg.landscape.fill_epsilon_m)
    eta = float(cfg.landscape.hysteresis_eta)
    stop_dz = float(cfg.landscape.stop_dz_m)
    max_flips = int(cfg.landscape.get("freeze_after_flips", FREEZE_AFTER_FLIPS))
    if max_flips < 1:
        raise ValueError(f"landscape.freeze_after_flips 는 1 이상이어야 합니다: {max_flips}")

    if z_init is None:
        z = initial_surface(graph, mask, zo, cfg)
    else:
        z = _vector(z_init, n, "z_init").copy()
        if not np.isfinite(z).all():
            raise ValueError("z_init 에 NaN 이나 inf 가 있습니다")
    z[mask] = zo[mask]

    nbr = np.ascontiguousarray(graph.nbr)
    dist = np.ascontiguousarray(graph.dist, dtype=np.float64)
    area = np.ascontiguousarray(graph.area, dtype=np.float64)
    width = np.sqrt(area)
    w_water = area * R if inflow is None else area * R + inflow
    w_sed = U * area
    inv_n = 1.0 / p["n"]

    rcv = None
    flips = np.zeros(n, dtype=np.int32)
    n_frozen = 0
    history: list[dict] = []
    converged = False
    it = 0
    z_new = np.empty(n, dtype=np.float64)
    while it < max_iter:
        it += 1
        if rcv is not None and _has_eps_drop(z, rcv, mask, eps):
            # z 를 지난 수신 셀로 쌓았고 모든 칸이 수신 셀보다 ε 이상 높으면 ε 채움은 z 를 그대로
            # 돌려줍니다(고정점 z̃_c = max(z_c, min 이웃 z̃ + ε) 를 z 가 이미 만족). 결과가 같으므로
            # 우선순위 큐(반복 시간의 약 2/3)를 건너뜁니다. L0(칸 19.5 km)에서는 늘 이 경우입니다.
            zt = z
        else:
            zt = fill_epsilon(z, nbr, mask, eps)
        if rcv is None:
            # 첫 반복은 히스테리시스 없이(η = 0) 고릅니다.
            rcv, _, n_changed = d8_receivers(zt, nbr, dist, mask)
        else:
            prev = rcv
            rcv, _, _ = d8_receivers(zt, nbr, dist, mask, prev=prev, eta=eta)
            n_changed, n_frozen = _freeze_kernel(zt, prev, rcv, flips, max_flips)
        order = topo_order(rcv)
        Q = accumulate(rcv, order, w_water)
        A_up = accumulate(rcv, order, area)
        Qs = np.maximum(accumulate(rcv, order, w_sed), 0.0)
        n_bad = _integrate_kernel(
            order, rcv, nbr, dist, zo, Q, A_up, Qs, U, width, bottom, k_mult, s_crit,
            p["k_ref"], p["u_ref"], inv_n, p["theta"], p["g_dep"], p["r_ref"], p["diff"],
            p["s_min"], eps, z_new,
        )  # fmt: skip
        if n_bad:
            raise ValueError(f"수신 셀이 이웃이 아닌 칸이 {n_bad}개 있습니다 (내부 오류)")
        max_dz = float(np.max(np.abs(z_new - z)))
        z, z_new = z_new, z
        history.append(
            {"iteration": it, "n_changed": int(n_changed), "max_dz": max_dz, "n_frozen": n_frozen}
        )
        if log is not None:
            log(
                f"솔버 반복 {it}: 방향 변화 {n_changed}, 최대 고도 변화 {max_dz:.4g} m, "
                f"고정 칸 {n_frozen}"
            )
        if n_changed == 0 and max_dz < stop_dz:
            converged = True
            break

    slope = np.empty(n, dtype=np.float64)
    k_s = np.empty(n, dtype=np.float64)
    s_crit_surface = np.empty(n, dtype=np.float64)
    _surface_law_kernel(
        rcv, nbr, dist, z, Q, Qs, U, bottom, k_mult, s_crit,
        p["k_ref"], p["u_ref"], inv_n, p["g_dep"], p["r_ref"], slope, k_s, s_crit_surface,
    )  # fmt: skip
    return SolverResult(
        z=z,
        receiver=rcv,
        order=order,
        discharge=Q,
        drainage_area=A_up,
        sediment_flux=Qs,
        slope=slope,
        k_s=k_s,
        s_crit_surface=s_crit_surface,
        iterations=it,
        history=history,
        converged=converged,
        seconds=time.perf_counter() - t0,
        n_frozen=int(n_frozen),
        frozen=flips >= max_flips,
    )
