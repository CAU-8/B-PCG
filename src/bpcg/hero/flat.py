"""평면 히어로: L0 없이 가짜 경계조건으로 2~4단계를 돌립니다 (pipeline.md 9장, 설계도 8장).

'평면 히어로 먼저'를 위한 경로입니다. 경계조건은 다음과 같습니다(명세 값은 모듈 상수).

- 남북 방향 섭입 단면 융기 U(y) = 0.1 mm/yr + 2 mm/yr·exp(−((y − 0.3L)/0.25L)²).
  y 는 북쪽 가장자리에서 남쪽으로 잰 거리 [m] 입니다(행 번호 j 방향). 그래서 북쪽에 산맥이 있고,
  남쪽 출구로 가며 융기가 줄어 산지 앞(선상지)이 생깁니다.
- 템플릿 1(습곡충상대). 습곡 위상은 수렴 경계가 북쪽 가장자리를 따라 있다고 보고
  δ_conv = y 로 geology.model.fold_displacement 를 씁니다(노이즈 점은 pos/R).
- 깎인 두께(기둥 꼭대기 z_top): 누적 융기 = 지금 지표 높이 + 그동안 깎인 두께로 봅니다.
  z_top = z_pre + U·T_e 이고, T_e = (템플릿 1 퇴적층 두께 2.6 km) / U_max 는 산마루가 퇴적층을 막
  다 깎아 낸 때입니다. z_pre 는 4배 거친 격자(기준 암석 하나)로 미리 푼 정상상태 지표를 고운 격자로
  보간한 값입니다. 그래서 산지 앞은 맨 위 층, 산허리는 석회암·사암, 산마루는 기반암 꼭대기가
  드러납니다(z_top ≥ 지표, 지표가 기둥 꼭대기 위로 뜨지 않음). 행성 식(U·30 Myr, 상한 30 km)을
  그대로 쓰면 0.1 mm/yr 인 산지 앞에서도 3 km 가 깎여 모든 지표가 기반암이 되고, U·T_e 만 쓰면
  지표(최대 수 km)가 기둥 꼭대기보다 높아 맨 위 셰일만 보입니다.
- 유출 0.5 m/yr (R_eff 도 같음, 바닥값보다 큼). 해수면 기온 15 °C (최종 고도로 기온 감률 적용).
- 강수는 Budyko 식을 거꾸로 풀어 유출 0.5 m/yr 가 되는 값입니다(PET = a·15 + b). 그래서 기후 세
  값이 서로 맞습니다.
- 출구는 남쪽 가장자리 가운데 5칸, z = 200 m. 나머지 가장자리는 닫힌 칸. 들어오는 물 없음.
"""

import time

import numpy as np
from scipy.interpolate import RegularGridInterpolator
from scipy.optimize import brentq

from bpcg.core.graph import flat_graph
from bpcg.geology.model import (
    FOLD_THRUST,
    TEMPLATES,
    LayerColumns,
    build_columns,
    fold_displacement,
)
from bpcg.hero.domain import edge_cells, hero_flat_graph, hero_grid_size
from bpcg.landscape.solver import solve_steady_state
from bpcg.pipeline import (
    HeroState,
    Log,
    run_stages_2_to_4,
    score,
    scorecard_summary,
    solver_max_iter,
)
from bpcg.planet.climate import budyko_runoff

# pipeline.md 9장 hero/flat.py 의 가짜 경계조건
U_BASE_M_PER_YR = 1.0e-4  # 0.1 mm/yr
U_PEAK_M_PER_YR = 2.0e-3  # 2 mm/yr
U_CENTER_FRACTION = 0.3  # 봉우리 위치 0.3·L (북쪽 가장자리에서)
U_WIDTH_FRACTION = 0.25  # 폭 0.25·L
TEMPLATE_ID = FOLD_THRUST  # 템플릿 1
RUNOFF_M_PER_YR = 0.5  # 유출
TEMPERATURE_SEA_C = 15.0  # 해수면 기온
OUTLET_CELLS = 5  # 남쪽 가장자리 가운데 5칸
OUTLET_Z_M = 200.0  # 출구 고도
PRESOLVE_COARSEN = 4  # 깎인 두께용 미리 풀기 격자는 4배 거칠게 (우리가 정한 값)
PRESOLVE_MIN_CELLS = 16  # 미리 풀기 격자의 한 변 최소 칸 수


def flat_uplift(y_from_north: np.ndarray, length_m: float) -> np.ndarray:
    """섭입 단면 융기 U(y) [m/yr] (N,). y_from_north: 북쪽 가장자리에서 잰 거리 [m]."""
    y = np.asarray(y_from_north, dtype=np.float64)
    L = float(length_m)
    if not L > 0:
        raise ValueError(f"length_m 은 0 보다 커야 합니다: {L}")
    return U_BASE_M_PER_YR + U_PEAK_M_PER_YR * np.exp(
        -(((y - U_CENTER_FRACTION * L) / (U_WIDTH_FRACTION * L)) ** 2)
    )


def precip_for_runoff(runoff: float, pet: float) -> float:
    """Budyko 유출이 runoff 가 되는 강수 P [m/yr] (planet.climate.budyko_runoff 를 거꾸로).

    R(P) 는 P 에 대해 늘고 P − PET ≤ R ≤ P 이므로 [runoff, runoff + PET] 에서 brentq 로 풉니다.
    """
    r = float(runoff)
    e = float(pet)
    if not (r > 0 and e > 0):
        raise ValueError(f"runoff, pet 는 0 보다 커야 합니다: {r}, {e}")

    def gap(p: float) -> float:
        return float(budyko_runoff(np.array([p]), np.array([e]))[0]) - r

    lo, hi = r, r + e
    if gap(hi) < 0.0:  # 반올림으로 끝점이 모자라면 조금 넓힘
        hi = r + 2.0 * e
    return float(brentq(gap, lo, hi, xtol=1e-12, rtol=1e-12))


def erosion_period_yr(uplift: np.ndarray) -> float:
    """T_e = (템플릿 1 퇴적층 두께) / U_max [yr] (모듈 설명). U_max ≤ 0 이면 0."""
    u_max = float(np.max(np.asarray(uplift, dtype=np.float64)))
    if not u_max > 0:
        return 0.0
    thickness = float(sum(t for _, t in TEMPLATES[TEMPLATE_ID].layers))
    return thickness / u_max


def presolve_surface(cfg, x: np.ndarray, y: np.ndarray) -> np.ndarray:
    """거친 격자로 미리 푼 정상상태 지표를 점 (x 동, y 북) [m] (N,) 에서 보간합니다 [m] (N,).

    거친 격자: 한 변 max(PRESOLVE_MIN_CELLS, n/PRESOLVE_COARSEN) 칸, 흔들기 없음, 같은 영역.
    융기는 같은 단면, 유출 RUNOFF_M_PER_YR, 기준 암석 하나(layers=None), 출구는 남쪽 가운데 한 칸
    (z = OUTLET_Z_M). 보간은 칸 중심 격자의 쌍선형(가장자리 반 칸은 선형 외삽)입니다.
    """
    n_side, dx = hero_grid_size(cfg)
    L = n_side * dx
    n_c = max(PRESOLVE_MIN_CELLS, n_side // PRESOLVE_COARSEN)
    dc = L / n_c
    cg = flat_graph(n_c, n_c, dc, origin=(-0.5 * L, 0.5 * L))
    U_c = flat_uplift(0.5 * L - cg.pos[:, 1], L)
    outlet = np.zeros(cg.n_cells, dtype=bool)
    outlet[edge_cells(n_c, "south", n_c // 2, 1)] = True
    res = solve_steady_state(
        cg, outlet, OUTLET_Z_M, U_c, np.full(cg.n_cells, RUNOFF_M_PER_YR), None, cfg
    )
    xs = -0.5 * L + (np.arange(n_c) + 0.5) * dc  # 서 → 동
    ys = -0.5 * L + (np.arange(n_c) + 0.5) * dc  # 남 → 북 (오름차순)
    img = res.z.reshape(n_c, n_c)[::-1, :]  # 행을 남 → 북으로
    interp = RegularGridInterpolator((ys, xs), img, bounds_error=False, fill_value=None)
    return interp(np.stack([np.asarray(y, dtype=np.float64), np.asarray(x, dtype=np.float64)], 1))


def flat_exhumation(uplift: np.ndarray, z_pre: np.ndarray) -> np.ndarray:
    """깎인 두께(기둥 꼭대기) = max(z_pre, 0) + max(U, 0)·T_e [m] (N,) (모듈 설명)."""
    U = np.maximum(np.asarray(uplift, dtype=np.float64), 0.0)
    zp = np.maximum(np.asarray(z_pre, dtype=np.float64), 0.0)
    return zp + U * erosion_period_yr(U)


def flat_hero(cfg, log: Log = None) -> HeroState:
    """가짜 경계조건으로 평면 히어로를 만듭니다 (pipeline.md 9장 hero/flat.py).

    cfg: 설정 (profile.hero 크기·간격, landscape, climate, geology, ...). log: 진행 기록 함수.
    반환: HeroState (site None). fields: uplift_m_per_yr, exhumation_m, precip_m_per_yr,
    pet_m_per_yr, runoff_m_per_yr, runoff_eff_m_per_yr, template_id, fold_phase, dist_convergent_m,
    is_ocean(모두 False) + 2~4단계 필드. diag: seconds, boundary, solver, stages, scorecard,
    scorecard_summary.
    """
    t_all = time.perf_counter()
    sec: dict[str, float] = {}
    n_side, dx = hero_grid_size(cfg)
    L = n_side * dx
    graph = hero_flat_graph(cfg)
    n = graph.n_cells
    y = 0.5 * L - graph.pos[:, 1]  # 북쪽 가장자리에서 남쪽으로 잰 거리 [m]
    U = flat_uplift(y, L)

    c = cfg.climate
    pet_value = float(c.pet_per_degc_m_per_yr) * max(TEMPERATURE_SEA_C, 0.0) + float(
        c.pet_base_m_per_yr
    )
    p_value = precip_for_runoff(RUNOFF_M_PER_YR, pet_value)
    runoff = np.full(n, RUNOFF_M_PER_YR)
    runoff_eff = np.maximum(
        np.maximum(runoff, float(c.runoff_floor_fraction) * p_value),
        float(c.runoff_floor_m_per_yr),
    )
    precip = np.full(n, p_value)
    pet = np.full(n, pet_value)

    t = time.perf_counter()
    z_pre = presolve_surface(cfg, graph.pos[:, 0], graph.pos[:, 1])
    sec["presolve"] = time.perf_counter() - t

    t = time.perf_counter()
    tid = np.full(n, TEMPLATE_ID, dtype=np.uint8)
    exh = flat_exhumation(U, z_pre)
    d_conv = np.ascontiguousarray(y)
    phase, disp = fold_displacement(
        {"dist_convergent_m": d_conv, "uplift_m_per_yr": U},
        tid,
        cfg,
        unit_points=np.ascontiguousarray(graph.pos / float(cfg.planet.radius_m)),
    )
    sb, sr = build_columns(tid, exh, disp, cfg)
    columns = LayerColumns.from_columns(sb, sr)
    sec["geology"] = time.perf_counter() - t

    outlet_cells = edge_cells(n_side, "south", n_side // 2, OUTLET_CELLS)
    is_outlet = np.zeros(n, dtype=bool)
    is_outlet[outlet_cells] = True
    if log is not None:
        log(
            f"[평면 히어로] {n_side}×{n_side} ({n}칸, {dx:g} m), U {U.min() * 1e3:.2f}~"
            f"{U.max() * 1e3:.2f} mm/yr, 강수 {p_value:.3f} m/yr, 출구 남쪽 {OUTLET_CELLS}칸"
        )

    t = time.perf_counter()
    st = run_stages_2_to_4(
        graph,
        is_outlet,
        OUTLET_Z_M,
        U,
        runoff_eff,
        precip,
        columns,
        cfg,
        log=log,
        runoff=runoff,
        temperature_sea_c=TEMPERATURE_SEA_C,
        max_iter=solver_max_iter(cfg, "hero"),
    )
    sec["stages"] = time.perf_counter() - t

    fields = {
        "uplift_m_per_yr": U,
        "exhumation_m": exh,
        "precip_m_per_yr": precip,
        "pet_m_per_yr": pet,
        "runoff_m_per_yr": runoff,
        "runoff_eff_m_per_yr": runoff_eff,
        "template_id": tid,
        "fold_phase": phase,
        "dist_convergent_m": d_conv,
        "is_ocean": np.zeros(n, dtype=bool),
        **st.fields,
    }

    t = time.perf_counter()
    sd = st.diag["solver"]
    card = score(
        cfg,
        hero_fields=fields,
        hero_graph=graph,
        diag={"hero": {"converged": sd["converged"], "iterations": sd["iterations"]}},
    )
    sec["scorecard"] = time.perf_counter() - t
    sec["total"] = time.perf_counter() - t_all
    summary = scorecard_summary(card)
    if log is not None:
        log(f"[평면 히어로] 끝: {sec['total']:.2f} s, 점수표 불합격 {summary['failed']}")
    diag = {
        "seconds": {**sec, "stages_detail": st.diag["seconds"]},
        "boundary": {
            "outlet_cells": outlet_cells,
            "outlet_edge": "south",
            "z_outlet_m": OUTLET_Z_M,
            "inflow_m3_per_yr": 0.0,
            "precip_m_per_yr": p_value,
            "pet_m_per_yr": pet_value,
            "length_m": L,
            "erosion_period_yr": erosion_period_yr(U),
            "presolve_z_max_m": float(z_pre.max()),
        },
        "solver": sd,
        "stages": {k: v for k, v in st.diag.items() if k not in ("seconds", "solver")},
        "scorecard": card,
        "scorecard_summary": summary,
    }
    return HeroState(
        graph=graph,
        fields=fields,
        columns=columns,
        site=None,
        rivers=st.rivers,
        fan_apexes=st.fan_apexes,
        diag=diag,
    )
