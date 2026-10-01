"""3D 지질 모델: 템플릿 3개, 습곡, 층 기둥, 높이별 암석 (docs/pipeline.md 5.2, 설계도 2·4장).

칸마다 위에서 아래로 쌓인 층 기둥 하나를 둡니다. 기둥은 해수면에서 쌓였고 깎인 두께만큼 통째로
솟았다고 보므로, 지층 높이는 최종 지형이 아니라 누적 융기로 정해집니다(설계도 5장). 그래서 솔버
전에 만들 수 있고, 솔버와 3D 함수는 같은 기둥을 읽기만 합니다.

층 기둥 배열
- strata_bottom: (N, L) float64 층 바닥 고도 [m], 위층부터. 아래로 갈수록 작거나 같습니다.
- strata_rock: (N, L+1) uint8 층 암석 번호(geology.rocks). 마지막 열은 맨 아래 바닥이 없는 기반암.
- 층 i 는 고도 (bottom[i], bottom[i-1]] 입니다(층 0 은 위가 열려 있음). 경계 고도와 정확히
  같은 점은 아래층에 속합니다. 솔버가 경계까지 아래층 경사로 올라온 뒤 위층으로 넘어가는 것과
  같은 약속입니다.
- 두께 0 층(짧은 템플릿의 빈 층)은 바로 아래 층의 암석을 씁니다. rock_at 은 이 층을 고르지 않습니다.

기반암 안의 변성 경계(가이드와 다름, pipeline.md 5.2 보충)
- 묻힌 깊이는 원래 기둥 꼭대기(퇴적 당시 해수면)에서 잰 깊이 z_top − z 입니다. 템플릿 퇴적층은
  2.6 km 이하라 기본 지온 경사(30 °C/km)로는 변성되지 않고, 깎인 두께 25~35 km 인 산맥 한가운데에서
  드러나는 것은 기반암입니다. 기반암을 '꼭대기 깊이 온도' 하나로 정하면 편마암이 생기지 않으므로,
  기반암 계열의 문턱 온도가 되는 깊이(화강암 → 편마암 650 °C, 약 21.3 km)에 층 경계를 하나
  더 둡니다.
  그래서 L = (가장 긴 템플릿 층 수 5) + (기반암 문턱 수 1) = 6 입니다. 경계 위아래 암석은 층마다
  하나라서 '경사를 만든 암석 = 보이는 암석'이 그대로 성립합니다.
"""

import math
import time
from dataclasses import dataclass

import numpy as np
from numba import njit, prange

from bpcg.core.hashing import hash3, hash_unit
from bpcg.geology import rocks as rk

# ---------------------------------------------------------------- 템플릿 (pipeline.md 5.2 표)
PLATFORM = 0  # 탄산염 탁상지
FOLD_THRUST = 1  # 습곡충상대 (자그로스·쥐라·캐나다 로키형)
ARC = 2  # 기반암 + 화산호


@dataclass(frozen=True)
class Template:
    """지질 템플릿 하나. layers 는 위층부터 (암석 번호, 두께 [m]), basement 는 기반암 암석 번호."""

    name: str
    layers: tuple[tuple[int, float], ...]
    basement: int


TEMPLATES: tuple[Template, ...] = (
    Template(
        "carbonate_platform",
        (
            (rk.SANDSTONE, 150.0),
            (rk.LIMESTONE, 500.0),
            (rk.SHALE, 250.0),
            (rk.LIMESTONE, 600.0),
            (rk.SANDSTONE, 300.0),
        ),
        rk.GRANITE,
    ),
    Template(
        "fold_thrust",
        (
            (rk.SHALE, 300.0),
            (rk.LIMESTONE, 800.0),
            (rk.SHALE, 400.0),
            (rk.SANDSTONE, 500.0),
            (rk.LIMESTONE, 600.0),
        ),
        rk.GRANITE,
    ),
    Template("basement_arc", ((rk.VOLCANIC, 800.0),), rk.GRANITE),
)
N_TEMPLATES = len(TEMPLATES)
N_TEMPLATE_LAYERS = max(len(t.layers) for t in TEMPLATES)  # 5
N_BASEMENT_SPLITS = max(len(rk.METAMORPHIC_SERIES.get(t.basement, ())) for t in TEMPLATES)  # 1
N_LAYERS = N_TEMPLATE_LAYERS + N_BASEMENT_SPLITS  # L = 6, strata_rock 은 L+1 = 7 열

# assign_template 의 기하 상수 (pipeline.md 5.2, 설정에 없는 명세 값)
ARC_BELT_FRACTION = 0.4  # 화산호 띠: |δ_conv − d_arc| < 0.4·arc_belt_width_m
ARC_FOLD_GAP_M = 50_000.0  # 위판 습곡대는 화산호에서 50 km 바깥부터
FOLD_NOISE_WEIGHT = 0.5  # φ_fold 의 노이즈 계수 0.5 (pipeline.md 5.2)
_FOLD_NOISE_STREAM = 5201  # 습곡 노이즈 시드 갈래 (다른 노이즈와 겹치지 않게)

_REQUIRED_TEMPLATE_FIELDS = (
    "subduction_side",
    "convergence_kind",
    "dist_convergent_m",
    "dist_arc_m",
)


# ---------------------------------------------------------------- 입력 확인
def _field(fields: dict, name: str, n: int | None = None) -> np.ndarray:
    if name not in fields:
        raise ValueError(f"필드 '{name}' 가 없습니다")
    a = np.asarray(fields[name])
    if a.ndim != 1:
        raise ValueError(f"필드 '{name}' 는 (N,) 1차원이어야 합니다 (받은 모양: {a.shape})")
    if n is not None and a.shape[0] != n:
        raise ValueError(f"필드 '{name}' 의 길이 {a.shape[0]} 가 다른 필드 길이 {n} 과 다릅니다")
    return a


def _check_template_id(template_id: np.ndarray) -> np.ndarray:
    t = np.asarray(template_id)
    if t.ndim != 1:
        raise ValueError(f"template_id 는 (N,) 1차원이어야 합니다 (받은 모양: {t.shape})")
    if t.size and not np.issubdtype(t.dtype, np.integer):
        raise ValueError(f"template_id 는 정수 배열이어야 합니다 (받은 dtype: {t.dtype})")
    if t.size and (t.min() < 0 or t.max() >= N_TEMPLATES):
        raise ValueError(f"template_id 는 0..{N_TEMPLATES - 1} 이어야 합니다")
    return t.astype(np.intp, copy=False)


def _check_columns(strata_bottom: np.ndarray, strata_rock: np.ndarray):
    b = np.ascontiguousarray(strata_bottom, dtype=np.float64)
    r = np.ascontiguousarray(strata_rock, dtype=np.uint8)
    if b.ndim != 2 or r.ndim != 2:
        raise ValueError("strata_bottom 은 (N, L), strata_rock 은 (N, L+1) 2차원 배열이어야 합니다")
    if r.shape != (b.shape[0], b.shape[1] + 1):
        raise ValueError(
            f"strata_rock 모양 {r.shape} 이 strata_bottom 모양 {b.shape} 과 맞지 않습니다 "
            "((N, L+1) 이어야 함)"
        )
    return b, r


def _as_2d_z(z: np.ndarray, n_cells: int) -> tuple[np.ndarray, tuple[int, ...]]:
    z = np.asarray(z, dtype=np.float64)
    if z.ndim not in (1, 2) or z.shape[0] != n_cells:
        raise ValueError(
            f"z 는 ({n_cells},) 또는 ({n_cells}, M) 모양이어야 합니다 (받은 모양: {z.shape})"
        )
    z2 = z.reshape(n_cells, -1) if z.ndim == 1 else z
    return np.ascontiguousarray(z2), z.shape


# ---------------------------------------------------------------- 템플릿 배정
def assign_template(fields: dict, cfg) -> np.ndarray:
    """칸마다 지질 템플릿 번호를 정합니다 (pipeline.md 5.2 assign_template).

    fields: subduction_side (N,) int8 (+1 위판, −1 섭입판, 0), convergence_kind (N,) uint8
    (3 = 대륙 충돌), dist_convergent_m (N,) 수렴 경계까지 거리 [m] (없으면 inf),
    dist_arc_m (N,) 해구-화산호 거리 d_arc [m] (위판 쪽만, 나머지 NaN).
    반환: (N,) uint8. 2 = 위판이고 |δ_conv − d_arc| < 0.4·arc_belt_width_m,
    1 = 충돌이고 δ_conv < fold_belt_width_m 이거나,
    위판이고 d_arc + 50 km < δ_conv < fold_belt_width_m,
    0 = 나머지. 2 와 1 이 겹치면 2 입니다(명세의 순서).
    """
    side = _field(fields, "subduction_side")
    n = side.shape[0]
    kind = _field(fields, "convergence_kind", n)
    dist = _field(fields, "dist_convergent_m", n).astype(np.float64)
    d_arc = _field(fields, "dist_arc_m", n).astype(np.float64)
    g = cfg.geology
    fold_w = float(g.fold_belt_width_m)
    arc_half = ARC_BELT_FRACTION * float(g.arc_belt_width_m)

    upper = side == 1
    with np.errstate(invalid="ignore"):
        is_arc = upper & (np.abs(dist - d_arc) < arc_half)
        is_fold = ((kind == 3) & (dist < fold_w)) | (
            upper & (dist > d_arc + ARC_FOLD_GAP_M) & (dist < fold_w)
        )
    out = np.zeros(n, dtype=np.uint8)
    out[is_fold] = FOLD_THRUST
    out[is_arc] = ARC
    return out


# ---------------------------------------------------------------- 습곡
def _sub_seed(seed: int, stream: int) -> int:
    """행성 시드에서 용도별 시드를 만듭니다 (31비트 양수)."""
    return int(hash3(np.int64(seed), np.int64(stream), np.int64(0)) >> np.uint64(33))


def _fallback_fbm3(points: np.ndarray, seed: int, octaves: int = 5) -> np.ndarray:
    """core/noise 가 없을 때 쓰는 결정적 대체 노이즈: 해시로 고른 방향의 사인파 합, 값 [-1, 1].

    옥타브마다 구 위 균등 방향 3개를 골라 진동수 2^o 의 사인파를 더합니다(fbm3 과 같은 가중 g^o).
    """
    pts = np.asarray(points, dtype=np.float64)
    total = np.zeros(pts.shape[0], dtype=np.float64)
    norm = 0.0
    for o in range(octaves):
        amp = 0.5**o
        freq = 2.0**o
        for d in range(3):
            u = [hash_unit(np.int64(seed), np.int64(o), np.int64(3 * d + k)) for k in range(3)]
            cz = 2.0 * u[0] - 1.0
            ang = 2.0 * math.pi * u[1]
            rr = math.sqrt(max(0.0, 1.0 - cz * cz))
            direction = np.array([rr * math.cos(ang), rr * math.sin(ang), cz])
            total += amp * np.sin(2.0 * math.pi * (freq * (pts @ direction) + u[2]))
            norm += amp
    return total / norm


def _fold_noise(points: np.ndarray, seed: int) -> tuple[np.ndarray, str]:
    """fbm3(points) 와 쓴 노이즈 이름. core/noise 가 아직 없으면 대체 노이즈를 씁니다."""
    try:
        from bpcg.core.noise import fbm3
    except ImportError:
        return _fallback_fbm3(points, seed), "fallback"
    return np.asarray(fbm3(np.ascontiguousarray(points, dtype=np.float64), seed)), "fbm3"


def _fold_phase(
    dist_convergent: np.ndarray, cfg, unit_points: np.ndarray | None
) -> tuple[np.ndarray, str]:
    lam = float(cfg.geology.fold_wavelength_m)
    if not lam > 0:
        raise ValueError(f"fold_wavelength_m 은 0 보다 커야 합니다: {lam}")
    dist = np.asarray(dist_convergent, dtype=np.float64)
    finite = np.isfinite(dist)
    phase = np.where(finite, 2.0 * math.pi * np.where(finite, dist, 0.0) / lam, 0.0)
    if unit_points is None:
        return phase, "none"
    p = np.asarray(unit_points, dtype=np.float64)
    if p.shape != (dist.shape[0], 3):
        raise ValueError(
            f"unit_points 는 ({dist.shape[0]}, 3) 모양이어야 합니다 (받은 모양: {p.shape})"
        )
    radius = float(cfg.planet.radius_m)
    seed = _sub_seed(int(cfg.planet.seed), _FOLD_NOISE_STREAM)
    noise, backend = _fold_noise(p * (radius / lam), seed)
    phase = phase + FOLD_NOISE_WEIGHT * np.where(finite, noise, 0.0)
    return phase, backend


def fold_displacement_from_phase(
    fold_phase: np.ndarray,
    uplift: np.ndarray,
    template_id: np.ndarray,
    cfg,
    u_max: float | None = None,
) -> np.ndarray:
    """습곡 위상에서 층 변위 Δ = A·(U/U_max)·sin(φ_fold) 를 구합니다 (템플릿 1 만, 나머지 0).

    fold_phase: (N,) [rad]. uplift: (N,) 융기 U [m/yr]. template_id: (N,) 정수.
    u_max: U_max [m/yr]. None 이면 받은 칸들의 max(U, 0). 히어로는 L0 값을 넘겨 같은 크기를 씁니다.
    U/U_max 는 [0, 1] 로 자릅니다(침강 칸은 0). 반환: (N,) float64 변위 [m], |Δ| ≤ fold_amplitude_m.
    """
    tid = _check_template_id(template_id)
    n = tid.shape[0]
    phase = np.asarray(fold_phase, dtype=np.float64)
    U = np.asarray(uplift, dtype=np.float64)
    if phase.shape != (n,) or U.shape != (n,):
        raise ValueError(f"fold_phase, uplift 는 template_id 와 같은 ({n},) 모양이어야 합니다")
    u_pos = np.where(np.isfinite(U), np.maximum(U, 0.0), 0.0)
    if u_max is None:
        u_max = float(u_pos.max()) if n else 0.0
    u_max = float(u_max)
    if not math.isfinite(u_max) or u_max < 0:
        raise ValueError(f"u_max 는 0 이상의 유한한 값이어야 합니다: {u_max}")
    amp = float(cfg.geology.fold_amplitude_m)
    if u_max == 0.0:
        return np.zeros(n, dtype=np.float64)
    ratio = np.minimum(u_pos / u_max, 1.0)
    disp = amp * ratio * np.sin(np.where(np.isfinite(phase), phase, 0.0))
    return np.where(tid == FOLD_THRUST, disp, 0.0)


def fold_displacement(
    fields: dict,
    template_id: np.ndarray,
    cfg,
    unit_points: np.ndarray | None = None,
    u_max: float | None = None,
) -> tuple[np.ndarray, np.ndarray]:
    """습곡 위상과 층 변위를 구합니다 (pipeline.md 5.2 '습곡').

    fields: dist_convergent_m (N,) [m] (없으면 inf), uplift_m_per_yr (N,) [m/yr].
    template_id: (N,) 정수. unit_points: (N, 3) 칸 대표점의 단위 벡터. 주면 위상에
    0.5·fbm3(p·R/λ) 를 더하고, None 이면 노이즈 없이 2π·δ_conv/λ 만 씁니다.
    u_max: fold_displacement_from_phase 참고.
    반환: (fold_phase (N,) float64 [rad], disp_m (N,) float64 [m]). 위상은 모든 칸에서 계산하고
    (히어로가 L0 에서 보간), 수렴 경계 거리가 inf/NaN 인 칸은 0 입니다.
    변위는 템플릿 1 에서만 0 이 아닙니다.
    """
    tid = _check_template_id(template_id)
    n = tid.shape[0]
    dist = _field(fields, "dist_convergent_m", n)
    U = _field(fields, "uplift_m_per_yr", n)
    phase, _ = _fold_phase(dist, cfg, unit_points)
    disp = fold_displacement_from_phase(phase, U, tid, cfg, u_max)
    return phase, disp


# ---------------------------------------------------------------- 층 기둥
def template_columns(cfg) -> tuple[np.ndarray, np.ndarray]:
    """템플릿별 층 바닥 깊이와 변성을 적용한 암석 (모든 칸이 공유하는 표).

    반환: (depth_bottom (N_TEMPLATES, L) float64 [m] 기둥 꼭대기에서 잰 층 바닥 깊이(늘 0 이상,
    아래로 커지거나 같음), rock (N_TEMPLATES, L+1) uint8).
    층 순서: 템플릿 층(위부터) → 두께 0 빈 층 → 기반암 변성 경계별 층 → 마지막 열 기반암.
    퇴적층의 변성은 층 가운데 깊이의 T_peak, 기반암 층은 그 층 꼭대기 깊이의 T_peak 로 정합니다.
    """
    g = cfg.geology
    t_surf = float(g.surface_temperature_c)
    grad = float(g.geotherm_c_per_m)
    if not (math.isfinite(grad) and grad > 0):
        raise ValueError(f"geotherm_c_per_m 은 0 보다 커야 합니다: {grad}")
    depth = np.zeros((N_TEMPLATES, N_LAYERS), dtype=np.float64)
    rock = np.zeros((N_TEMPLATES, N_LAYERS + 1), dtype=np.uint8)
    for t, tpl in enumerate(TEMPLATES):
        bottoms: list[float] = []
        rocks: list[int] = []
        top = 0.0
        for r, thickness in tpl.layers:
            mid = top + 0.5 * thickness
            rocks.append(int(rk.metamorphose(r, rk.peak_temperature_c(mid, cfg))))
            top += thickness
            bottoms.append(top)
        while len(bottoms) < N_TEMPLATE_LAYERS:  # 빈 층: 바닥 = 앞층 바닥
            bottoms.append(top)
            rocks.append(-1)
        # 기반암: 꼭대기 온도의 암석부터, 계열 문턱 온도가 되는 깊이마다 층을 나눕니다.
        b = tpl.basement
        t_top = t_surf + grad * top
        rocks.append(int(rk.metamorphose(b, t_top)))
        seg_top = top
        series = rk.METAMORPHIC_SERIES.get(b, ())
        for t_min, _ in series:
            seg_top = max(seg_top, (t_min - t_surf) / grad)
            bottoms.append(seg_top)
            # 경계 깊이의 온도는 정확히 문턱이므로 반올림 오차 없이 문턱 값으로 변성합니다.
            rocks.append(int(rk.metamorphose(b, max(t_min, t_top))))
        while len(bottoms) < N_LAYERS:
            bottoms.append(seg_top)
            rocks.insert(len(rocks) - 1, -1)
        # 두께 0 층은 바로 아래 층의 암석을 씁니다(아래에서 위로 채움).
        thick = np.diff(np.concatenate([[0.0], bottoms]))
        for i in range(N_LAYERS - 1, -1, -1):
            if thick[i] <= 0.0 or rocks[i] < 0:
                rocks[i] = rocks[i + 1]
        depth[t] = bottoms
        rock[t] = rocks
    return depth, rock


def build_columns(
    template_id: np.ndarray,
    exhumation_m: np.ndarray,
    fold_disp_m: np.ndarray | None,
    cfg,
) -> tuple[np.ndarray, np.ndarray]:
    """칸마다 층 기둥을 만듭니다 (pipeline.md 5.2 build_columns).

    template_id: (N,) 정수 0..2. exhumation_m: (N,) 깎인 두께 [m]. fold_disp_m: (N,) 습곡 변위 [m]
    또는 None(0). 기둥 꼭대기 z_top = exhumation + Δ, 층 i 바닥 = z_top − Σ_{j≤i} t_j 입니다.
    반환: (strata_bottom (N, L) float64 [m] 위층부터 아래로 작거나 같음,
    strata_rock (N, L+1) uint8).
    """
    tid = _check_template_id(template_id)
    n = tid.shape[0]
    exh = np.asarray(exhumation_m, dtype=np.float64)
    if exh.shape != (n,):
        raise ValueError(f"exhumation_m 은 template_id 와 같은 ({n},) 모양이어야 합니다")
    if not np.isfinite(exh).all():
        raise ValueError("exhumation_m 에 NaN 이나 inf 가 있습니다")
    z_top = exh.copy()
    if fold_disp_m is not None:
        disp = np.asarray(fold_disp_m, dtype=np.float64)
        if disp.shape != (n,):
            raise ValueError(f"fold_disp_m 은 template_id 와 같은 ({n},) 모양이어야 합니다")
        if not np.isfinite(disp).all():
            raise ValueError("fold_disp_m 에 NaN 이나 inf 가 있습니다")
        z_top += disp
    depth, rock = template_columns(cfg)
    strata_bottom = z_top[:, None] - depth[tid]
    strata_rock = rock[tid]
    return strata_bottom, strata_rock


# ---------------------------------------------------------------- 높이별 암석 (numba)
@njit(cache=True, inline="always")
def layer_index_at(bottom: np.ndarray, c: int, z: float) -> int:
    """칸 c 에서 고도 z 가 속한 층 번호 (0..L). z 보다 낮은 바닥을 가진 첫 층, 없으면 L(기반암).

    bottom: (N, L) float64 [m]. 다른 numba 커널(솔버 층별 적분, 3D 함수) 안에서 그대로 부릅니다.
    경계와 같은 z 는 아래층, 기둥 꼭대기보다 높은 z 는 층 0, NaN 은 L 입니다.
    """
    n_layers = bottom.shape[1]
    for i in range(n_layers):
        if bottom[c, i] < z:
            return i
    return n_layers


@njit(cache=True, parallel=True)
def _layer_index_kernel(bottom, z, out):
    for c in prange(z.shape[0]):
        for m in range(z.shape[1]):
            out[c, m] = layer_index_at(bottom, c, z[c, m])


@njit(cache=True, parallel=True)
def _rock_at_kernel(bottom, rock, z, out):
    for c in prange(z.shape[0]):
        for m in range(z.shape[1]):
            out[c, m] = rock[c, layer_index_at(bottom, c, z[c, m])]


@njit(cache=True, parallel=True)
def _rock_at_points_kernel(bottom, rock, cells, z, out):
    for k in prange(z.shape[0]):
        c = cells[k]
        out[k] = rock[c, layer_index_at(bottom, c, z[k])]


def layer_index(strata_bottom: np.ndarray, z: np.ndarray) -> np.ndarray:
    """고도 z 가 속한 층 번호 (0..L, L 은 기반암). strata_rock·k_mult·s_crit 의 열 번호입니다.

    strata_bottom: (N, L) [m]. z: (N,) 또는 (N, M) 고도 [m]. 반환: z 와 같은 모양, int32.
    """
    b = np.ascontiguousarray(strata_bottom, dtype=np.float64)
    if b.ndim != 2:
        raise ValueError(f"strata_bottom 은 (N, L) 2차원이어야 합니다 (받은 모양: {b.shape})")
    z2, shape = _as_2d_z(z, b.shape[0])
    out = np.empty(z2.shape, dtype=np.int32)
    _layer_index_kernel(b, z2, out)
    return out.reshape(shape)


def rock_at(strata_bottom: np.ndarray, strata_rock: np.ndarray, z: np.ndarray) -> np.ndarray:
    """칸마다 고도 z 의 암석 번호 (pipeline.md 5.2 rock_at, numba).

    strata_bottom: (N, L) [m]. strata_rock: (N, L+1) uint8. z: (N,) 또는 (N, M) 고도 [m].
    z 보다 낮은 바닥을 가진 첫 층의 암석이고, 모든 바닥보다 낮으면 기반암입니다. 경계와 같은 z 는
    아래층, 기둥 꼭대기보다 높은 z 는 맨 위층 암석입니다. 반환: z 와 같은 모양, uint8.
    """
    b, r = _check_columns(strata_bottom, strata_rock)
    z2, shape = _as_2d_z(z, b.shape[0])
    out = np.empty(z2.shape, dtype=np.uint8)
    _rock_at_kernel(b, r, z2, out)
    return out.reshape(shape)


def rock_at_points(
    strata_bottom: np.ndarray, strata_rock: np.ndarray, cells: np.ndarray, z: np.ndarray
) -> np.ndarray:
    """임의의 점들의 암석 번호. 점 k 는 칸 cells[k] 의 기둥에서 고도 z[k] 로 찾습니다.

    cells: (M,) 정수 칸 번호. z: (M,) 고도 [m]. 반환: (M,) uint8. 3D 함수(volume)용입니다.
    """
    b, r = _check_columns(strata_bottom, strata_rock)
    cells = np.ascontiguousarray(cells, dtype=np.int64)
    zz = np.ascontiguousarray(z, dtype=np.float64)
    if cells.ndim != 1 or zz.shape != cells.shape:
        raise ValueError("cells 와 z 는 같은 (M,) 모양이어야 합니다")
    if cells.size and (cells.min() < 0 or cells.max() >= b.shape[0]):
        raise ValueError(f"cells 는 0..{b.shape[0] - 1} 이어야 합니다")
    out = np.empty(zz.shape[0], dtype=np.uint8)
    _rock_at_points_kernel(b, r, cells, zz, out)
    return out


# ---------------------------------------------------------------- 솔버에 넘기는 묶음
@dataclass(eq=False)
class LayerColumns:
    """솔버와 3D 함수가 읽는 층 기둥 묶음 (pipeline.md 5.2 LayerColumns).

    bottom: (N, L) float64 층 바닥 고도 [m]. rock: (N, L+1) uint8 암석 번호.
    k_mult: (N, L+1) float64 침식 계수 배율. s_crit: (N, L+1) float64 임계 경사 [m/m].
    고도 z 의 층 번호 i = layer_index(z) 에 대해 rock[c, i], k_mult[c, i], s_crit[c, i] 가 늘 같은
    암석의 값입니다('경사를 만든 암석 = 보이는 암석').
    """

    bottom: np.ndarray
    rock: np.ndarray
    k_mult: np.ndarray
    s_crit: np.ndarray

    def __post_init__(self) -> None:
        self.bottom, self.rock = _check_columns(self.bottom, self.rock)
        self.k_mult = np.ascontiguousarray(self.k_mult, dtype=np.float64)
        self.s_crit = np.ascontiguousarray(self.s_crit, dtype=np.float64)
        if self.k_mult.shape != self.rock.shape or self.s_crit.shape != self.rock.shape:
            raise ValueError("k_mult, s_crit 는 rock 과 같은 (N, L+1) 모양이어야 합니다")

    @classmethod
    def from_columns(cls, strata_bottom: np.ndarray, strata_rock: np.ndarray) -> "LayerColumns":
        """층 바닥과 암석에서 암석 표(K 배율, S_crit)를 찾아 묶습니다."""
        b, r = _check_columns(strata_bottom, strata_rock)
        if r.size and r.max() >= rk.N_ROCKS:
            raise ValueError(f"strata_rock 의 암석 번호는 0..{rk.N_ROCKS - 1} 이어야 합니다")
        return cls(bottom=b, rock=r, k_mult=rk.K_MULT[r], s_crit=rk.S_CRIT[r])

    @property
    def n_cells(self) -> int:
        return self.bottom.shape[0]

    @property
    def n_layers(self) -> int:
        """층 경계 수 L (암석 열은 L+1)."""
        return self.bottom.shape[1]

    def layer_index(self, z: np.ndarray) -> np.ndarray:
        """고도 z [m] ((N,) 또는 (N, M)) 가 속한 층 번호, int32."""
        return layer_index(self.bottom, z)

    def rock_at(self, z: np.ndarray) -> np.ndarray:
        """고도 z [m] ((N,) 또는 (N, M)) 의 암석 번호, uint8."""
        return rock_at(self.bottom, self.rock, z)


def surface_rock(columns: LayerColumns, z: np.ndarray) -> np.ndarray:
    """지표 고도 z (N,) [m] 에 드러난 암석 번호 (N,) uint8. 필드 surface_rock 입니다."""
    return rock_at(columns.bottom, columns.rock, z)


# ---------------------------------------------------------------- 단계 함수
def generate_geology(
    fields: dict,
    cfg,
    unit_points: np.ndarray | None = None,
    u_max: float | None = None,
) -> tuple[dict, LayerColumns, dict]:
    """1단계 지질: 템플릿 → 습곡 → 층 기둥을 한 번에 만듭니다.

    fields: assign_template 의 필드 4개 + uplift_m_per_yr, exhumation_m (모두 (N,)).
    unit_points: (N, 3) 단위 벡터(습곡 노이즈용, 없으면 노이즈 없음). u_max: 습곡 U_max [m/yr].
    반환: (fields {template_id (N,) uint8, fold_phase (N,) float64,
    strata_bottom_m (N, L) float64, strata_rock (N, L+1) uint8}, LayerColumns,
    diag {template_counts, u_max_m_per_yr, noise, seconds}).
    """
    t0 = time.perf_counter()
    for name in (*_REQUIRED_TEMPLATE_FIELDS, "uplift_m_per_yr", "exhumation_m"):
        _field(fields, name)
    template_id = assign_template(fields, cfg)
    n = template_id.shape[0]
    U = _field(fields, "uplift_m_per_yr", n).astype(np.float64)
    phase, backend = _fold_phase(_field(fields, "dist_convergent_m", n), cfg, unit_points)
    if u_max is None:
        finite = np.isfinite(U)
        u_max = float(np.maximum(U[finite], 0.0).max()) if finite.any() else 0.0
    disp = fold_displacement_from_phase(phase, U, template_id, cfg, u_max)
    exh = _field(fields, "exhumation_m", n)
    strata_bottom, strata_rock = build_columns(template_id, exh, disp, cfg)
    columns = LayerColumns.from_columns(strata_bottom, strata_rock)
    out = {
        "template_id": template_id,
        "fold_phase": phase,
        "strata_bottom_m": columns.bottom,
        "strata_rock": columns.rock,
    }
    diag = {
        "template_counts": np.bincount(template_id, minlength=N_TEMPLATES).tolist(),
        "u_max_m_per_yr": float(u_max),
        "noise": backend,
        "seconds": time.perf_counter() - t0,
    }
    return out, columns, diag
