"""히어로 후보 찾기: L0 육지 칸에 점수를 매겨 가장 높은 칸을 고릅니다 (docs/pipeline.md 9장).

점수 = 0.3·융기 기울기 + 0.4·지표 탄산염 + 0.15·기복 + 0.15·200 km 안 건조 칸 비율.
동굴이 보이려면 탄산염이 있어야 하므로 탄산염을 가장 무겁게 둡니다(pipeline.md 9장에서 바꿈).
항목은 모두 [0, 1] 입니다(설계도 8장 '히어로 유역은 도구로 고른다').

- 융기 기울기 |∇U| [1/yr]: 이웃 방향 미분 g_s = (U_n − U_c)/d 로 |∇U| ≈ sqrt(2·평균 g_s²)
  (8 방향이 고르게 퍼진 선형 장에서 평균 (∇U·ê)² = |∇U|²/2). 산지 앞쪽처럼 융기가 바뀌는 곳.
- 지표 탄산염: 칸의 골짜기 바닥 z 부터 능선 z + relief 까지 높이 범위에서 녹는 암석(석회암·
  대리암)이 차지하는 비율. 히어로 지형이 드러낼 암석이라 동굴이 생길 수 있는 곳입니다.
- 기복 relief_m [m]: L0 아격자 기복(landscape.relief).
- 건조 칸 비율: 200 km 안 칸 가운데 강수 < 잠재 증발산(P/PET < 1, Budyko 의 물 제한 쪽)인
  육지 칸의 비율(칸 수 기준, 바다 칸은 분모에만).
- 정규화: 융기 기울기와 기복은 후보 칸 값의 99 백분위로 나누고 [0, 1] 로 자릅니다(한두 칸의
  튀는 값이 전체를 누르지 않게).

후보 칸: 육지이고 |위도| ≤ 55° 이며, 바다까지 거리가 히어로 반대각선 + L0 칸 2개보다 먼 칸.
그래서 히어로 영역과 그 둘레 L0 보간 범위가 모두 육지입니다(경계조건이 L0 강 하나로 정해지게).
"""

import math
from dataclasses import dataclass, field

import numpy as np
from scipy.spatial import cKDTree

from bpcg.core.distance import nearest_source
from bpcg.geology import rocks as rk
from bpcg.geology.model import rock_at
from bpcg.hero.domain import hero_grid_size, tangent_frame
from bpcg.planet.climate import latitude_rad

SCORE_WEIGHTS = {  # pipeline.md 9장 점수 가중치
    "uplift_gradient": 0.3,
    "carbonate": 0.4,
    "relief": 0.15,
    "dry_fraction": 0.15,
}
MAX_LAT_DEG = 55.0  # 위도 55° 넘으면 0점 (pipeline.md 9장)
DRY_RADIUS_M = 200_000.0  # 건조 칸 비율을 세는 반경 (pipeline.md 9장)
DRY_ARIDITY_INDEX = 1.0  # P/PET < 1 이면 건조 칸 (Budyko 물 제한 쪽, 우리가 정한 문턱)
NORMALIZE_PERCENTILE = 99.0  # 정규화 기준 백분위 (우리가 정한 값)
CARBONATE_SAMPLES = 16  # 탄산염 비율을 셀 높이 점 수 (z ~ z + relief)
OCEAN_MARGIN_CELLS = 2.0  # 바다까지 거리 여유 [L0 칸 크기 배수] (쌍선형 보간 범위)

_REQUIRED = (
    "is_ocean",
    "uplift_m_per_yr",
    "z_m",
    "relief_m",
    "precip_m_per_yr",
    "pet_m_per_yr",
    "strata_bottom_m",
    "strata_rock",
)


@dataclass(frozen=True, eq=False)
class HeroSite:
    """고른 히어로 자리 (pipeline.md 9장 HeroSite).

    center_unit: (3,) 중심 단위 벡터. east, north: (3,) 접평면 동·북 단위 벡터.
    l0_cell: 중심 L0 칸 번호. score: 점수 [0, 1]. parts: 항목별 점수 dict (각 [0, 1],
    키는 SCORE_WEIGHTS 와 같음). lat_deg, lon_deg: 중심 위도·경도 [°] (자전축 기준).
    """

    center_unit: np.ndarray
    east: np.ndarray
    north: np.ndarray
    l0_cell: int
    score: float
    parts: dict = field(default_factory=dict)
    lat_deg: float = float("nan")
    lon_deg: float = float("nan")


def _fields(planet) -> dict:
    f = getattr(planet, "fields", None)
    g = getattr(planet, "graph", None)
    if not isinstance(f, dict) or g is None:
        raise ValueError("planet 은 graph 와 fields 를 가진 PlanetState 여야 합니다")
    if g.kind != "sphere":
        raise ValueError("planet.graph 는 구면 그래프여야 합니다")
    n = g.n_cells
    missing = [k for k in _REQUIRED if k not in f]
    if missing:
        raise ValueError(f"planet.fields 에 히어로 점수에 필요한 필드가 없습니다: {missing}")
    for k in _REQUIRED:
        if np.shape(f[k])[:1] != (n,):
            raise ValueError(f"planet.fields['{k}'] 의 길이가 칸 수 {n} 와 다릅니다")
    return f


def uplift_gradient(graph, uplift: np.ndarray) -> np.ndarray:
    """융기 기울기 |∇U| ≈ sqrt(2·평균_s ((U_n − U_c)/d)²) [1/yr] (N,). 이웃이 없는 슬롯은 뺍니다."""
    U = np.asarray(uplift, dtype=np.float64)
    nbr = graph.nbr
    ok = nbr >= 0
    un = U[np.where(ok, nbr, 0)]
    g = np.where(ok, (un - U[:, None]) / np.where(ok, graph.dist, 1.0), 0.0)
    cnt = np.maximum(ok.sum(axis=1), 1)
    return np.sqrt(2.0 * (g * g).sum(axis=1) / cnt)


def carbonate_fraction(
    strata_bottom: np.ndarray, strata_rock: np.ndarray, z: np.ndarray, relief: np.ndarray
) -> np.ndarray:
    """z ~ z + relief 높이 범위에서 녹는 암석이 차지하는 비율 (N,) [0, 1].

    범위를 CARBONATE_SAMPLES 개 점(칸 가운데 규칙)으로 나눠 geology.model.rock_at 으로 셉니다.
    relief 가 0 이면 지표 암석 하나로 정합니다.
    """
    z = np.asarray(z, dtype=np.float64)
    r = np.maximum(np.nan_to_num(np.asarray(relief, dtype=np.float64)), 0.0)
    k = (np.arange(CARBONATE_SAMPLES) + 0.5) / CARBONATE_SAMPLES
    zz = z[:, None] + r[:, None] * k[None, :]
    rock = rock_at(strata_bottom, strata_rock, zz)
    return rk.SOLUBLE[rock].mean(axis=1)


def dry_fraction(graph, is_dry: np.ndarray, cells: np.ndarray, radius_m: float) -> np.ndarray:
    """칸 cells (M,) 마다 반경 radius_m (구면 대원 거리) 안 칸 가운데 is_dry 인 칸의 비율 (M,).

    scipy cKDTree 로 3D 직선(현) 거리 2R·sin(s/2R) 안의 칸을 셉니다(자기 자신 포함).
    """
    pos = graph.pos
    if graph.kind == "sphere":
        R = float(graph.R)
        chord = 2.0 * R * math.sin(min(0.5 * radius_m / R, 0.5 * math.pi))
    else:
        chord = float(radius_m)
    q = pos[np.asarray(cells, dtype=np.int64)]
    if q.shape[0] == 0:
        return np.zeros(0)
    total = cKDTree(pos).query_ball_point(q, chord, return_length=True, workers=-1)
    dry_pos = pos[np.asarray(is_dry, dtype=bool)]
    if dry_pos.shape[0] == 0:
        return np.zeros(q.shape[0])
    dry = cKDTree(dry_pos).query_ball_point(q, chord, return_length=True, workers=-1)
    return np.asarray(dry, dtype=np.float64) / np.maximum(np.asarray(total, dtype=np.float64), 1.0)


def _normalize(x: np.ndarray, mask: np.ndarray) -> np.ndarray:
    """mask 칸 값의 NORMALIZE_PERCENTILE 백분위로 나눠 [0, 1] 로 자릅니다.

    백분위가 0 이면(0 이 아닌 칸이 1% 미만) 최댓값으로, 그것도 0 이면 모두 0 입니다.
    """
    if not mask.any():
        return np.zeros_like(x)
    ref = float(np.percentile(x[mask], NORMALIZE_PERCENTILE))
    if not ref > 0.0:
        ref = float(np.max(x[mask]))
    if not ref > 0.0:
        return np.zeros_like(x)
    return np.clip(x / ref, 0.0, 1.0)


def candidate_mask(planet, cfg) -> np.ndarray:
    """히어로 중심이 될 수 있는 L0 칸 (N,) bool (모듈 설명의 후보 조건)."""
    f = _fields(planet)
    g = planet.graph
    ocean = np.asarray(f["is_ocean"], dtype=bool)
    lat = latitude_rad(g, cfg)
    n_side, dx = hero_grid_size(cfg)
    half_diag = 0.5 * math.sqrt(2.0) * n_side * dx
    margin = half_diag + OCEAN_MARGIN_CELLS * float(g.spacing)
    if ocean.any():
        d_ocean, _ = nearest_source(g, ocean)
    else:
        d_ocean = np.full(g.n_cells, np.inf)
    return ~ocean & (np.abs(lat) <= math.radians(MAX_LAT_DEG)) & (d_ocean > margin)


def score_cells(planet, cfg) -> tuple[np.ndarray, dict[str, np.ndarray], np.ndarray]:
    """L0 칸마다 히어로 점수 (pipeline.md 9장).

    planet: PlanetState (fields 에 _REQUIRED 이름). cfg: planet.axis, profile.hero.
    반환: (score (N,) float64 [0, 1], 후보가 아니면 0, parts {이름: (N,) [0, 1]},
    candidate (N,) bool).
    """
    f = _fields(planet)
    g = planet.graph
    n = g.n_cells
    cand = candidate_mask(planet, cfg)
    ocean = np.asarray(f["is_ocean"], dtype=bool)

    grad = uplift_gradient(g, f["uplift_m_per_yr"])
    relief = np.nan_to_num(np.asarray(f["relief_m"], dtype=np.float64))
    parts = {
        "uplift_gradient": _normalize(grad, cand),
        "carbonate": np.zeros(n),
        "relief": _normalize(relief, cand),
        "dry_fraction": np.zeros(n),
    }
    idx = np.flatnonzero(cand)
    if idx.size:
        parts["carbonate"][idx] = carbonate_fraction(
            np.asarray(f["strata_bottom_m"])[idx],
            np.asarray(f["strata_rock"])[idx],
            np.asarray(f["z_m"], dtype=np.float64)[idx],
            relief[idx],
        )
        P = np.asarray(f["precip_m_per_yr"], dtype=np.float64)
        pet = np.asarray(f["pet_m_per_yr"], dtype=np.float64)
        is_dry = ~ocean & (P < DRY_ARIDITY_INDEX * pet)
        parts["dry_fraction"][idx] = dry_fraction(g, is_dry, idx, DRY_RADIUS_M)
    total = np.zeros(n)
    for k, w in SCORE_WEIGHTS.items():
        total += w * parts[k]
    total[~cand] = 0.0
    return total, parts, cand


def site_from_cell(planet, cell: int, cfg, score: float = float("nan"), parts=None) -> HeroSite:
    """L0 칸 하나를 중심으로 하는 HeroSite (동·북은 planet.axis 기준 접평면)."""
    g = planet.graph
    c = int(cell)
    if not 0 <= c < g.n_cells:
        raise ValueError(f"cell 은 0..{g.n_cells - 1} 이어야 합니다: {cell}")
    center = g.pos[c] / np.linalg.norm(g.pos[c])
    axis = np.asarray(cfg.planet.axis, dtype=np.float64)
    axis = axis / np.linalg.norm(axis)
    east, north = tangent_frame(center, axis)
    lat = math.degrees(math.asin(float(np.clip(center @ axis, -1.0, 1.0))))
    # 경도: 축에 수직인 기준 방향(축과 가장 수직인 좌표축을 축에 수직으로 만든 것)에서 잰 각.
    ref = np.eye(3)[int(np.argmin(np.abs(axis)))]
    ref = ref - (ref @ axis) * axis
    ref = ref / np.linalg.norm(ref)
    lon = math.degrees(math.atan2(float(np.cross(ref, center) @ axis), float(center @ ref)))
    return HeroSite(
        center_unit=center,
        east=east,
        north=north,
        l0_cell=c,
        score=float(score),
        parts=dict(parts or {}),
        lat_deg=lat,
        lon_deg=lon,
    )


def find_hero(planet, cfg) -> HeroSite:
    """점수가 가장 높은 L0 칸을 히어로 중심으로 고릅니다 (같으면 칸 번호가 작은 쪽).

    planet: PlanetState. cfg: 설정. 후보 칸이 없으면 ValueError 입니다.
    반환: HeroSite (parts 는 그 칸의 항목별 점수 float).
    """
    total, parts, cand = score_cells(planet, cfg)
    if not cand.any():
        raise ValueError(
            "히어로 후보 칸이 없습니다 (육지, |위도| ≤ 55°, 바다에서 히어로 반대각선 + L0 칸 2개 "
            "넘게 떨어진 칸이 하나도 없음)"
        )
    score_c = np.where(cand, total, -1.0)
    best = int(np.argmax(score_c))
    best_parts = {k: float(v[best]) for k, v in parts.items()}
    return site_from_cell(planet, best, cfg, score=float(total[best]), parts=best_parts)
