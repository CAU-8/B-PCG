"""생성 파이프라인: 1단계 재료 → 2~4단계 → 히어로 (docs/pipeline.md 1·9·13장).

- generate_planet: 거친 격자 재료(planet.materials) → L0 로 옮기기 → 지질 기둥 → 2~4단계 → 점수표.
- generate_hero: 행성이 있으면 히어로 후보 찾기 → L0 경계조건 → 2~4단계, 없으면 평면 히어로
  (가짜 경계조건, hero/flat.py).
- run_stages_2_to_4: 구면 L0 와 평면 히어로가 같이 쓰는 2~4단계 한 경로입니다
  (솔버 → 선상지 → 기복 보정(구면만) → 최종 고도 기온 → 물 → 지표 암석 → 흙 → 지하수면 → 동굴).

계산 함수는 파일을 쓰지 않습니다(묶음 쓰기는 bake/). 결과 필드는 계산 정밀도 그대로(고도 float64)
들고 다니고, 저장할 때 FIELDS 의 dtype 으로 바꿉니다.

점수표(metrics)를 부르는 곳은 이 모듈뿐입니다. 의존 방향 규칙(docs/conventions.md 1장)은 과학
모듈이 metrics 에 기대지 않게 하려는 것이고, 이 모듈은 실행 입구(`bpcg planet` 이 점수표를 냄,
pipeline.md 13장)라서 함수 안에서만 가져옵니다.
"""

import time
from collections.abc import Callable
from dataclasses import dataclass, field

import numpy as np

from bpcg.core.config import Config
from bpcg.core.fields import check_fields
from bpcg.core.graph import CellGraph, sphere_graph
from bpcg.geology.model import LayerColumns, generate_geology, surface_rock
from bpcg.hydro.accumulate import accumulate
from bpcg.hydro.depressions import fill_epsilon
from bpcg.hydro.network import river_segments
from bpcg.hydro.routing import d8_receivers, topo_order
from bpcg.landscape.fans import add_fans
from bpcg.landscape.relief import subgrid_relief
from bpcg.landscape.solver import SolverResult, solve_steady_state
from bpcg.planet.climate import latitude_rad, surface_temperature
from bpcg.planet.materials import build_materials, transfer_materials
from bpcg.subsurface.caves import cave_levels
from bpcg.subsurface.groundwater import water_table
from bpcg.subsurface.soil import soil_and_alluvium
from bpcg.subsurface.water import valley_depth, water_bodies

Log = Callable[[str], None] | None

SOLVER_LOG_EVERY = 10  # 솔버 반복 기록은 10회마다 한 줄만 화면에 냅니다 (history 에는 모두 남김)


# ---------------------------------------------------------------- 결과 묶음
@dataclass(eq=False)
class PlanetState:
    """행성(L0) 결과 (pipeline.md 1장 '묶음 out/<run>/planet/').

    graph: L0 구면 그래프(노드 흔들기).
    fields: FIELDS 이름 dict ((N,), strata_* 는 (N, L)·(N, L+1)), 계산 정밀도 그대로.
    columns: 지질 기둥(geology.model.LayerColumns).
    info: 해수면·바다 비율·수심 등 1단계 값(transfer_materials 의 info + 거친 격자 요약 + 판 정보).
    diag: 단계별 시간 seconds, solver(반복·수렴·history), geology, stages, scorecard 등.
    """

    graph: CellGraph
    fields: dict[str, np.ndarray]
    columns: LayerColumns
    info: dict
    diag: dict


@dataclass(eq=False)
class HeroState:
    """히어로(L2) 결과 (pipeline.md 9장 HeroState 표).

    graph: 평면 그래프(국소 (동, 북) [m], 가운데가 원점). fields: FIELDS 이름 dict.
    columns: 지질 기둥. site: hero.finder.HeroSite 또는 None(평면 히어로).
    rivers: 강 구간 목록 (각각 상류 → 하류 칸 번호 int64 배열, hydro.network.river_segments).
    fan_apexes: 선상지 꼭짓점 dict 목록 (landscape.fans.add_fans). diag: 진단값.
    """

    graph: CellGraph
    fields: dict[str, np.ndarray]
    columns: LayerColumns
    site: object | None
    rivers: list[np.ndarray]
    fan_apexes: list[dict]
    diag: dict


@dataclass(eq=False)
class StageResult:
    """run_stages_2_to_4 의 결과.

    fields: 2~4단계가 만든 FIELDS 이름 dict (아래 run_stages_2_to_4 설명).
    rivers: 강 구간 목록. fan_apexes: 선상지 꼭짓점 목록. solver: 솔버 결과(선상지 전).
    diag: seconds(단계별 [s]), solver(반복·수렴·history 등), fans, water, groundwater, caves.
    """

    fields: dict[str, np.ndarray]
    rivers: list[np.ndarray]
    fan_apexes: list[dict]
    solver: SolverResult
    diag: dict = field(default_factory=dict)


# ---------------------------------------------------------------- 도움 함수
def _say(log: Log, msg: str) -> None:
    if log is not None:
        log(msg)


def _every(log: Log, every: int) -> Log:
    """처음 한 줄과 every 번째 줄마다만 log 로 넘기는 함수."""
    if log is None:
        return None
    count = [0]

    def inner(msg: str) -> None:
        count[0] += 1
        if count[0] == 1 or count[0] % every == 0:
            log(msg)

    return inner


def apply_threads(cfg) -> int:
    """설정 profile.compute.numba_threads 를 numba 에 적용합니다 (0 = 모든 코어). 쓴 스레드 수."""
    import numba

    compute = cfg.profile.get("compute")
    want = int(compute.get("numba_threads", 0)) if compute is not None else 0
    if want < 0:
        raise ValueError(f"profile.compute.numba_threads 는 0 이상이어야 합니다: {want}")
    top = int(numba.config.NUMBA_NUM_THREADS)
    k = top if want == 0 else min(want, top)
    numba.set_num_threads(k)
    return k


def _check_cfg(cfg) -> None:
    if not isinstance(cfg, Config):
        raise ValueError(f"cfg 는 bpcg.core.config.Config 여야 합니다: {type(cfg).__name__}")


def _vector(x, n: int, name: str) -> np.ndarray:
    a = np.asarray(x, dtype=np.float64)
    if a.ndim == 0:
        a = np.full(n, float(a))
    if a.shape != (n,):
        raise ValueError(f"{name} 는 ({n},) 배열 또는 스칼라여야 합니다: 모양 {a.shape}")
    return a


def solver_max_iter(cfg, level: str) -> int:
    """솔버 반복 상한. level 'hero' 는 profile.hero.max_flow_iterations 가 있으면 그 값을 씁니다.

    없으면 landscape.max_flow_iterations 입니다(히어로처럼 칸이 많으면 반복이 더 필요할 수 있어
    프로필에서 따로 줄 수 있게 둡니다, 설계도 5장).
    """
    base = int(cfg.landscape.max_flow_iterations)
    if level == "hero":
        hero = cfg.profile.get("hero")
        if hero is not None and "max_flow_iterations" in hero:
            return int(hero.max_flow_iterations)
    return base


def score(
    cfg,
    planet_fields: dict | None = None,
    planet_graph: CellGraph | None = None,
    hero_fields: dict | None = None,
    hero_graph: CellGraph | None = None,
    diag: dict | None = None,
) -> dict:
    """metrics.scorecard.scorecard 를 부릅니다(점수표, pipeline.md 12장). 반환은 그 dict 그대로."""
    from bpcg.metrics.scorecard import scorecard

    return scorecard(
        planet_fields=planet_fields,
        planet_graph=planet_graph,
        hero_fields=hero_fields,
        hero_graph=hero_graph,
        diag=diag,
        cfg=cfg,
    )


def scorecard_summary(card: dict) -> dict:
    """점수표 요약: check·forced 합격/불합격 수와 불합격 이름 목록."""
    judged = {k: v for k, v in card.items() if v["pass"] is not None}
    failed = sorted(k for k, v in judged.items() if v["pass"] is False)
    return {
        "n_items": len(card),
        "n_judged": len(judged),
        "n_failed": len(failed),
        "failed": failed,
    }


# ---------------------------------------------------------------- 2~4단계 (L0·히어로 공통)
def reroute_after_fans(
    graph: CellGraph,
    z: np.ndarray,
    is_outlet: np.ndarray,
    receiver: np.ndarray,
    fan: np.ndarray,
    runoff_eff: np.ndarray,
    uplift: np.ndarray,
    cfg,
    extra_inflow: np.ndarray | None = None,
) -> dict[str, np.ndarray]:
    """선상지를 얹은 뒤 물길만 다시 계산합니다: ε 채움 → D8 → Q, A↑, Qs (pipeline.md 7.2 '표시').

    graph: CellGraph. z: (N,) 선상지를 얹은 고도 [m]. is_outlet: (N,) bool.
    receiver: (N,) 솔버 수신 셀. fan: (N,) bool 올린 칸. runoff_eff: (N,) [m/yr].
    uplift: (N,) U [m/yr]. cfg: landscape.fill_epsilon_m. extra_inflow: (N,) [m³/yr] 또는 None.

    landscape.fans.reroute 와 달리 선상지 밖에서는 솔버의 수신 셀이 z̃ 에서 아직 확실히 낮은
    이웃이면 그대로 둡니다(η = ∞ 히스테리시스). 솔버가 진동 칸을 고정했거나 반복 상한에서 멈추면
    수신 셀이 가장 가파른 이웃이 아닌 칸이 남는데, 전부 다시 고르면 선상지와 상관없는 먼 칸들의
    물길까지 바뀌어 z 와 물길이 어긋나기 때문입니다. 올린 칸과 옛 수신 셀이 더는 낮지 않은 칸
    (원뿔에 막힌 칸, ε 채움으로 올라간 호수)만 가장 가파른 이웃으로 바꿉니다. 두 경우 모두 z̃ 가
    엄격히 줄어드는 쪽이라 순환이 생기지 않습니다.

    반환: {receiver (N,) int64, order (N,) int64, discharge_m3_per_yr, drainage_area_m2,
    sediment_flux_m3_per_yr (= max(누적(U·A), 0)), slope (= max(z − z_r, 0)/d)} (N,) float64.
    """
    if not isinstance(graph, CellGraph):
        raise ValueError(f"graph 는 CellGraph 여야 합니다: {type(graph).__name__}")
    n = graph.n_cells
    z = _vector(z, n, "z")
    runoff_eff = _vector(runoff_eff, n, "runoff_eff")
    uplift = _vector(uplift, n, "uplift")
    outlet = np.asarray(is_outlet)
    fan = np.asarray(fan)
    old = np.asarray(receiver)
    if outlet.shape != (n,) or outlet.dtype != np.bool_ or fan.shape != (n,):
        raise ValueError(f"is_outlet, fan 은 ({n},) bool 배열이어야 합니다")
    if (
        old.shape != (n,)
        or old.dtype.kind not in "iu"
        or (n and not 0 <= old.min() <= old.max() < n)
    ):
        raise ValueError(f"receiver 는 0..{n - 1} 범위의 ({n},) 정수 배열이어야 합니다")
    old = old.astype(np.int64)
    zt = fill_epsilon(z, graph.nbr, outlet, float(cfg.landscape.fill_epsilon_m))
    steep, _, _ = d8_receivers(zt, graph.nbr, graph.dist, outlet)
    ids = np.arange(n)
    keep = ~fan.astype(bool) & ~outlet & (old != ids) & (zt[old] < zt)
    rcv = np.where(keep, old, steep)
    order = topo_order(rcv)
    weight = (
        graph.area * runoff_eff if extra_inflow is None else graph.area * runoff_eff + extra_inflow
    )
    Q = accumulate(rcv, order, weight)
    A_up = accumulate(rcv, order, graph.area)
    Qs = np.maximum(accumulate(rcv, order, uplift * graph.area), 0.0)
    hit = graph.nbr == rcv[:, None]
    d = np.where(hit, graph.dist, np.inf).min(axis=1)
    slope = np.where(np.isfinite(d), np.maximum(z - z[rcv], 0.0) / d, 0.0)
    return {
        "receiver": rcv,
        "order": order,
        "discharge_m3_per_yr": Q,
        "drainage_area_m2": A_up,
        "sediment_flux_m3_per_yr": Qs,
        "slope": slope,
    }


def run_stages_2_to_4(
    graph: CellGraph,
    is_outlet: np.ndarray,
    z_outlet: float | np.ndarray,
    uplift: np.ndarray,
    runoff_eff: np.ndarray,
    precip: np.ndarray,
    columns: LayerColumns,
    cfg,
    extra_inflow: np.ndarray | None = None,
    lat_deg: float | None = None,
    log: Log = None,
    *,
    runoff: np.ndarray | float | None = None,
    is_ocean: np.ndarray | None = None,
    z_seafloor: np.ndarray | None = None,
    temperature_sea_c: float | np.ndarray | None = None,
    max_iter: int | None = None,
) -> StageResult:
    """2~4단계를 한 경로로 돌립니다 (pipeline.md 1장, L0 와 히어로가 같은 코드).

    graph: CellGraph (구면 L0 또는 평면 히어로). is_outlet: (N,) bool 바다·출구.
    z_outlet: 스칼라 또는 (N,) 출구 고도 [m]. uplift: (N,) U [m/yr]. runoff_eff: (N,) R_eff [m/yr].
    precip: (N,) 강수 [m/yr]. columns: LayerColumns (같은 칸 수). cfg: 설정.
    extra_inflow: (N,) 밖에서 들어오는 물 [m³/yr] 또는 None (히어로 경계).
    lat_deg: 평면 그래프의 위도 [°] (최종 고도 기온용, temperature_sea_c 가 없을 때 필요).
    log: 진행 상황을 받을 함수 또는 None.
    runoff: 지하수 함양에 쓰는 Budyko 유출 [m/yr] ((N,) 또는 스칼라), None 이면 runoff_eff.
    is_ocean: (N,) bool 바다. None 이면 구면은 is_outlet, 평면은 모두 육지.
    z_seafloor: (N,) 바다 칸 고도(해수면 0 기준 수심, 음수) [m]. 솔버는 바다를 출구 높이 z_outlet
      으로 두므로, 주면 솔버 뒤 바다 칸 z 를 이 값으로 바꿉니다(고도 분포·대륙붕 지표용).
    temperature_sea_c: 해수면 기온 [°C] (스칼라 또는 (N,)). 주면 T = 이 값 − lapse·max(z, 0),
      없으면 planet.climate.surface_temperature(위도, z) 입니다.
    max_iter: 솔버 반복 상한 (None 이면 landscape.max_flow_iterations).

    순서: 솔버 → 선상지(꼭짓점이 있으면 reroute_after_fans 로 물길만 다시, Qs 도 새 물길로)
    → 바다 칸 수심 → 기복 보정(구면만) → 최종 고도 기온 → 물 → 지표 암석 → 흙 → 지하수면 → 동굴.

    반환 StageResult.fields (모두 (N,), 고도 float64):
      z_m (최종 지표, 선상지 포함, 바다는 z_seafloor), receiver (int64), drainage_area_m2,
      discharge_m3_per_yr, sediment_flux_m3_per_yr, slope, k_s, s_crit, not_steady, fan,
      z_mean_m·relief_m (구면만), temperature_c, water_level_m, is_lake, is_river, river_width_m,
      river_depth_m, valley_depth_m, surface_rock (uint8), soil_thickness_m, alluvium_m, bare_rock,
      water_table_m, cave_level_<k>_m, cave_entrance (uint8), strata_bottom_m (N, L),
      strata_rock (N, L+1).
    """
    _check_cfg(cfg)
    if not isinstance(graph, CellGraph):
        raise ValueError(f"graph 는 CellGraph 여야 합니다: {type(graph).__name__}")
    if not isinstance(columns, LayerColumns):
        raise ValueError(f"columns 는 LayerColumns 여야 합니다: {type(columns).__name__}")
    n = graph.n_cells
    if columns.n_cells != n:
        raise ValueError(f"columns 의 칸 수 {columns.n_cells} 가 그래프 칸 수 {n} 와 다릅니다")
    outlet = np.asarray(is_outlet)
    if outlet.shape != (n,) or outlet.dtype != np.bool_:
        raise ValueError(f"is_outlet 은 ({n},) bool 배열이어야 합니다: {outlet.shape}")
    sphere = graph.kind == "sphere"
    if is_ocean is None:
        ocean = outlet.copy() if sphere else np.zeros(n, dtype=bool)
    else:
        ocean = np.asarray(is_ocean)
        if ocean.shape != (n,) or ocean.dtype != np.bool_:
            raise ValueError(f"is_ocean 은 ({n},) bool 배열이어야 합니다: {ocean.shape}")
        if (ocean & ~outlet).any():
            raise ValueError("바다 칸은 모두 출구(is_outlet)여야 합니다")
    U = _vector(uplift, n, "uplift")
    R_eff = _vector(runoff_eff, n, "runoff_eff")
    P = _vector(precip, n, "precip")
    if not (np.isfinite(P).all() and (P > 0).all()):
        raise ValueError("precip 는 0 보다 큰 유한한 값이어야 합니다")
    R_gw = R_eff if runoff is None else _vector(runoff, n, "runoff")
    inflow = None if extra_inflow is None else _vector(extra_inflow, n, "extra_inflow")
    if not sphere and temperature_sea_c is None and lat_deg is None:
        raise ValueError("평면 그래프에는 lat_deg 나 temperature_sea_c 를 줘야 합니다 (기온)")

    sec: dict[str, float] = {}
    t_all = time.perf_counter()

    # --- 2단계: 정상상태 솔버
    t = time.perf_counter()
    _say(log, f"[2단계] 솔버 시작: 칸 {n}, 출구 {int(outlet.sum())}")
    res = solve_steady_state(
        graph,
        outlet,
        z_outlet,
        U,
        R_eff,
        columns,
        cfg,
        extra_inflow=inflow,
        max_iter=max_iter,
        log=_every(log, SOLVER_LOG_EVERY),
    )
    sec["solver"] = time.perf_counter() - t
    sd = res.diag()
    _say(
        log,
        f"[2단계] 솔버 끝: 반복 {sd['iterations']}, 수렴 {sd['converged']}, "
        f"고정 칸 {sd['n_frozen']}, {sec['solver']:.2f} s",
    )

    # --- 3단계: 선상지 → 물길만 다시
    t = time.perf_counter()
    z, fan, not_steady, apexes = add_fans(graph, res.z, res, cfg)
    fan_raise = np.maximum(z - res.z, 0.0)
    n_rerouted = 0
    if apexes:
        rr = reroute_after_fans(graph, z, outlet, res.receiver, fan, R_eff, U, cfg, inflow)
        rcv, order = rr["receiver"], rr["order"]
        Q, A_up = rr["discharge_m3_per_yr"], rr["drainage_area_m2"]
        Qs, slope = rr["sediment_flux_m3_per_yr"], rr["slope"]
        # 물길이 바뀐 칸도 정상상태가 아닙니다(그 칸의 경사는 옛 수신 셀로 쌓은 값).
        changed = rcv != res.receiver
        n_rerouted = int(changed.sum())
        not_steady = not_steady | changed
    else:
        rcv, order = res.receiver, res.order
        Q, A_up, Qs, slope = res.discharge, res.drainage_area, res.sediment_flux, res.slope
    sec["fans"] = time.perf_counter() - t
    _say(
        log,
        f"[3단계] 선상지 {len(apexes)}개, 올린 칸 {int(fan.sum())}, 물길 바뀐 칸 {n_rerouted}",
    )

    if z_seafloor is not None:
        zs = _vector(z_seafloor, n, "z_seafloor")
        if not np.isfinite(zs[ocean]).all():
            raise ValueError("바다 칸의 z_seafloor 에 NaN 이나 inf 가 있습니다")
        z = z.copy()
        z[ocean] = zs[ocean]

    # --- 3단계: 기복 보정 (L0 만)
    out: dict[str, np.ndarray] = {}
    z_air = z
    if sphere:
        t = time.perf_counter()
        relief, z_mean = subgrid_relief(graph, z, res.k_s, res.s_crit_surface, R_eff, U, ocean, cfg)
        out["relief_m"] = relief
        out["z_mean_m"] = z_mean
        z_air = z_mean
        sec["relief"] = time.perf_counter() - t

    # --- 최종 고도로 기온만 다시 (pipeline.md 4.5 '두 번 부릅니다')
    t = time.perf_counter()
    if temperature_sea_c is not None:
        t_sea = _vector(temperature_sea_c, n, "temperature_sea_c")
        temp = t_sea - float(cfg.climate.lapse_rate_c_per_m) * np.maximum(z_air, 0.0)
    else:
        lat = latitude_rad(graph, cfg, None if sphere else lat_deg)
        temp = surface_temperature(lat, z_air, cfg)
    sec["climate_final"] = time.perf_counter() - t

    # --- 4단계: 물
    t = time.perf_counter()
    routing = {"receiver": rcv, "order": order, "discharge_m3_per_yr": Q}
    wb = water_bodies(graph, z, routing, ocean, cfg)
    if sphere:
        # L0 칸(약 20 km)은 골짜기 창(1.5 km)보다 커서 창 최고점이 칸 자신뿐입니다. 칸 안의 능선
        # 높이 z + 기복을 '골짜기 가장자리'로 써서 골짜기 깊이를 정합니다.
        wb["valley_depth_m"] = valley_depth(
            graph, z + out["relief_m"], wb["water_level_m"], wb["is_river"],
            float(cfg.caves.valley_window_m),
        )  # fmt: skip
    sec["water"] = time.perf_counter() - t

    # --- 지표 암석 (흙·지하수가 씀)
    t = time.perf_counter()
    rock = surface_rock(columns, z)
    sec["surface_rock"] = time.perf_counter() - t

    # --- 흙과 충적층
    t = time.perf_counter()
    soil = soil_and_alluvium(
        z,
        slope,
        U,
        temp,
        P,
        {"discharge_m3_per_yr": Q, "sediment_flux_m3_per_yr": Qs},
        fan_raise,
        rock,
        cfg,
        is_ocean=ocean,
    )
    sec["soil"] = time.perf_counter() - t

    # --- 지하수면
    t = time.perf_counter()
    is_water = ocean | wb["is_lake"] | wb["is_river"]
    z_gw, gw_diag = water_table(
        graph, z, wb["water_level_m"], is_water, rock, slope, R_gw, cfg, is_ocean=ocean
    )
    sec["groundwater"] = time.perf_counter() - t

    # --- 동굴 층
    t = time.perf_counter()
    caves = cave_levels(graph, z, z_gw, wb["valley_depth_m"], columns, cfg)
    sec["caves"] = time.perf_counter() - t

    t = time.perf_counter()
    rivers = river_segments(rcv, order, wb["is_river"])
    sec["rivers"] = time.perf_counter() - t
    sec["total"] = time.perf_counter() - t_all

    out.update(
        {
            "z_m": z,
            "receiver": rcv,
            "drainage_area_m2": A_up,
            "discharge_m3_per_yr": Q,
            "sediment_flux_m3_per_yr": Qs,
            "slope": slope,
            "k_s": res.k_s,
            "s_crit": res.s_crit_surface,
            "not_steady": not_steady,
            "fan": fan,
            "temperature_c": temp,
            **wb,
            "surface_rock": rock,
            **soil,
            "water_table_m": z_gw,
            **caves,
            "strata_bottom_m": columns.bottom,
            "strata_rock": columns.rock,
        }
    )
    check_fields(out)
    land = ~ocean
    cave_cells = {k: int(np.isfinite(v).sum()) for k, v in caves.items() if k.startswith("cave_l")}
    diag = {
        "seconds": sec,
        "solver": {**sd, "history": res.history},
        "fans": {
            "n_apexes": len(apexes),
            "n_raised": int(fan.sum()),
            "n_rerouted": n_rerouted,
        },
        "water": {
            "n_river_cells": int(wb["is_river"].sum()),
            "n_lake_cells": int(wb["is_lake"].sum()),
            "n_river_segments": len(rivers),
        },
        "groundwater": gw_diag,
        "caves": {**cave_cells, "n_entrance_cells": int((caves["cave_entrance"] > 0).sum())},
        "z_land_max_m": float(z[land].max()) if land.any() else float("nan"),
    }
    _say(
        log,
        f"[4단계] 강 칸 {diag['water']['n_river_cells']} (구간 {len(rivers)}), "
        f"호수 칸 {diag['water']['n_lake_cells']}, 동굴 {cave_cells}, "
        f"2~4단계 {sec['total']:.2f} s",
    )
    return StageResult(fields=out, rivers=rivers, fan_apexes=apexes, solver=res, diag=diag)


# ---------------------------------------------------------------- 행성
def generate_planet(cfg, log: Log = print) -> PlanetState:
    """행성 하나를 1~4단계로 만듭니다 (pipeline.md 1·13장, `bpcg planet`).

    1. 거친 격자(면당 profile.grid.coarse_n_per_face, 흔들기 없음)에서 build_materials.
    2. L0(면당 profile.grid.l0_n_per_face, 노드 흔들기 landscape.jitter, 시드 planet.seed)로
       transfer_materials.
    3. 지질: generate_geology (습곡 노이즈는 L0 단위 벡터).
    4. run_stages_2_to_4: 출구 = is_ocean, z_outlet = 0, 바다 칸 고도는 수심(z_platform − 해수면).
    5. 점수표(metrics.scorecard) → diag['scorecard'].

    반환: PlanetState. diag 키: seconds(materials_coarse, transfer, geology, stages, scorecard,
    total 과 stages 세부), solver(반복·수렴·history), geology, stages, scorecard,
    scorecard_summary, threads.
    """
    _check_cfg(cfg)
    t_all = time.perf_counter()
    threads = apply_threads(cfg)
    R = float(cfg.planet.radius_m)
    grid = cfg.profile.grid
    n_c = int(grid.coarse_n_per_face)
    n_l0 = int(grid.l0_n_per_face)
    sec: dict[str, float] = {}

    t = time.perf_counter()
    coarse = sphere_graph(n_c, R)
    coarse_fields, coarse_info = build_materials(coarse, cfg)
    sec["materials_coarse"] = time.perf_counter() - t
    _say(
        log,
        f"[1단계] 거친 격자 면당 {n_c}: 바다 {coarse_info['ocean_fraction']:.3f}, "
        f"{sec['materials_coarse']:.2f} s",
    )

    t = time.perf_counter()
    l0 = sphere_graph(n_l0, R, jitter=float(cfg.landscape.jitter), seed=int(cfg.planet.seed))
    fields, info = transfer_materials(coarse, coarse_fields, coarse_info, l0, cfg)
    sec["transfer"] = time.perf_counter() - t
    _say(
        log,
        f"[1단계] L0 면당 {n_l0} ({l0.n_cells}칸): 바다 {info['ocean_fraction']:.3f}, "
        f"해수면 {info['sea_level_m']:.0f} m, {sec['transfer']:.2f} s",
    )

    t = time.perf_counter()
    geo_fields, columns, geo_diag = generate_geology(fields, cfg, unit_points=l0.unit())
    fields.update(geo_fields)
    sec["geology"] = time.perf_counter() - t
    _say(log, f"[1단계] 지질 템플릿 칸 수 {geo_diag['template_counts']}, {sec['geology']:.2f} s")

    ocean = np.asarray(fields["is_ocean"], dtype=bool)
    t = time.perf_counter()
    st = run_stages_2_to_4(
        l0,
        ocean,
        0.0,
        fields["uplift_m_per_yr"],
        fields["runoff_eff_m_per_yr"],
        fields["precip_m_per_yr"],
        columns,
        cfg,
        log=log,
        runoff=fields["runoff_m_per_yr"],
        is_ocean=ocean,
        z_seafloor=info["bathymetry_m"],
        max_iter=solver_max_iter(cfg, "planet"),
    )
    fields.update(st.fields)
    sec["stages"] = time.perf_counter() - t
    check_fields(fields)

    t = time.perf_counter()
    sd = st.diag["solver"]
    card = score(
        cfg,
        planet_fields=fields,
        planet_graph=l0,
        diag={"planet": {"converged": sd["converged"], "iterations": sd["iterations"]}},
    )
    sec["scorecard"] = time.perf_counter() - t
    sec["total"] = time.perf_counter() - t_all
    summary = scorecard_summary(card)
    _say(
        log,
        f"[점수표] 항목 {summary['n_items']}, 판정 {summary['n_judged']}, "
        f"불합격 {summary['failed']}",
    )
    _say(log, f"[행성] 끝: {sec['total']:.2f} s")

    plate_info = coarse_info.get("plates", {})
    out_info = {
        **info,
        "coarse": {
            "n_per_face": n_c,
            "sea_level_m": coarse_info["sea_level_m"],
            "ocean_fraction": coarse_info["ocean_fraction"],
            "continental_fraction": coarse_info["continental_fraction"],
        },
        "plates": plate_info,
        "n_per_face": n_l0,
    }
    diag = {
        "seconds": {**sec, "stages_detail": st.diag["seconds"]},
        "solver": sd,
        "geology": geo_diag,
        "stages": {k: v for k, v in st.diag.items() if k not in ("seconds", "solver")},
        "scorecard": card,
        "scorecard_summary": summary,
        "threads": threads,
        "n_river_segments": len(st.rivers),
    }
    return PlanetState(graph=l0, fields=fields, columns=columns, info=out_info, diag=diag)


# ---------------------------------------------------------------- 히어로
def generate_hero(cfg, planet: PlanetState | None = None, log: Log = print) -> HeroState:
    """히어로 유역(L2)을 만듭니다 (pipeline.md 9장, `bpcg hero`).

    planet 이 있으면 hero.finder.find_hero → hero.refine.refine_hero (L0 경계조건),
    None 이면 hero.flat.flat_hero (가짜 경계조건, '평면 히어로 먼저').
    반환: HeroState (diag['scorecard'] 에 히어로 점수표).
    """
    _check_cfg(cfg)
    apply_threads(cfg)
    from bpcg.hero import find_hero, flat_hero, refine_hero

    if planet is None:
        return flat_hero(cfg, log=log)
    if not isinstance(planet, PlanetState):
        raise ValueError(f"planet 은 PlanetState 또는 None 이어야 합니다: {type(planet).__name__}")
    t = time.perf_counter()
    site = find_hero(planet, cfg)
    t_find = time.perf_counter() - t
    _say(
        log,
        f"[히어로] 후보: L0 칸 {site.l0_cell}, 위도 {site.lat_deg:.2f}°, 경도 {site.lon_deg:.2f}°, "
        f"점수 {site.score:.3f} {site.parts}, {t_find:.2f} s",
    )
    hero = refine_hero(planet, site, cfg, log=log)
    hero.diag.setdefault("seconds", {})["find"] = t_find
    return hero
