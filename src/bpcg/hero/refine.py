"""히어로 정밀화: L0 값과 경계조건으로 25 m 평면에서 2~4단계를 다시 풉니다 (pipeline.md 9장).

1. 표본: 히어로 칸의 구면 점에서 L0 필드를 core.resample.sample_sphere 로 읽습니다.
   매끄러운 값(U, 유출, 강수, 잠재 증발산, 깎인 두께, 습곡 위상)은 linear, 템플릿은 nearest.
   습곡 위상은 감지 않은 값이라 그대로 선형 보간합니다.
2. 지질 기둥: fold_displacement_from_phase(L0 U_max) → build_columns → LayerColumns.
3. 경계조건(설계도 3장 가벼운 버전)
   - 출구: 중심 L0 칸에서 수신 셀 방향으로 그은 반직선이 히어로 가장자리와 만나는 곳의 5칸.
     z_outlet 은 그 점의 L0 고도 z_m(골짜기 바닥)입니다.
   - 나머지 가장자리: 닫힌 일반 칸. 그래서 영역 전체가 한 유역입니다.
   - 들어오는 물: Q_in = Q_L0(중심) − Σ(A·R_eff)_히어로 > 0 이면, 중심 L0 칸의 기여 셀 가운데
     Q 가 가장 큰 칸 방향의 가장자리 칸 하나에 extra_inflow 로 넣습니다. 기여 셀이 없으면(히어로가
     L0 칸보다 작은 프로필) 수신 셀 반대 방향입니다.
4. pipeline.run_stages_2_to_4 (L0 와 같은 함수). 기온은 히어로 중심 위도와 최종 고도로 다시.
"""

import time

import numpy as np

from bpcg.core.resample import sample_sphere
from bpcg.geology.model import (
    LayerColumns,
    build_columns,
    fold_displacement_from_phase,
)
from bpcg.hero.domain import (
    direction_to_local,
    edge_cells,
    edge_hit,
    hero_graph,
    hero_grid_size,
    local_to_unit,
)
from bpcg.landscape.warmstart import coarse_warm_start
from bpcg.pipeline import (
    HeroState,
    Log,
    PlanetState,
    run_stages_2_to_4,
    score,
    scorecard_summary,
    solver_max_iter,
)

OUTLET_CELLS = 5  # 출구 칸 수 (pipeline.md 9장)
LINEAR_FIELDS = (  # L0 → 히어로 선형 보간 (pipeline.md 9장 refine_hero)
    "uplift_m_per_yr",
    "runoff_m_per_yr",
    "runoff_eff_m_per_yr",
    "precip_m_per_yr",
    "pet_m_per_yr",
    "exhumation_m",
    "fold_phase",
)
NEAREST_FIELDS = ("template_id",)  # 범주 값은 가장 가까운 L0 칸


def _say(log: Log, msg: str) -> None:
    if log is not None:
        log(msg)


def sample_l0_fields(planet: PlanetState, unit: np.ndarray) -> dict[str, np.ndarray]:
    """L0 필드를 구면 점 unit (M, 3) 에서 읽습니다. 반환: LINEAR_FIELDS + NEAREST_FIELDS (M,)."""
    n_l0 = int(planet.graph.shape[1])
    f = planet.fields
    missing = [k for k in (*LINEAR_FIELDS, *NEAREST_FIELDS) if k not in f]
    if missing:
        raise ValueError(f"planet.fields 에 히어로 표본에 필요한 필드가 없습니다: {missing}")
    out: dict[str, np.ndarray] = {}
    for name in LINEAR_FIELDS:
        v = sample_sphere(np.asarray(f[name], dtype=np.float64), n_l0, unit, "linear")
        if not np.isfinite(v).all():
            raise ValueError(f"L0 '{name}' 을 히어로로 보간한 값에 NaN 이나 inf 가 있습니다")
        out[name] = v
    for name in NEAREST_FIELDS:
        out[name] = sample_sphere(np.asarray(f[name]), n_l0, unit, "nearest")
    return out


def _u_max(planet: PlanetState) -> float:
    geo = planet.diag.get("geology", {}) if isinstance(planet.diag, dict) else {}
    if "u_max_m_per_yr" in geo:
        return float(geo["u_max_m_per_yr"])
    U = np.asarray(planet.fields["uplift_m_per_yr"], dtype=np.float64)
    return float(np.maximum(U[np.isfinite(U)], 0.0).max())


def hero_boundary(planet: PlanetState, site, graph, R_eff: np.ndarray, cfg) -> dict:
    """히어로 경계조건 (모듈 설명 3).

    planet: PlanetState (receiver, discharge_m3_per_yr, z_m). site: HeroSite. graph: 히어로 평면
    그래프. R_eff: (N,) 히어로 칸 침식용 유출 [m/yr]. cfg: 설정.
    반환 dict: is_outlet (N,) bool, outlet_cells (5,) int64, outlet_edge str, z_outlet_m float,
    extra_inflow (N,) float64 [m³/yr], inflow_cell int (없으면 −1), inflow_edge str | None,
    inflow_m3_per_yr float, l0_discharge_m3_per_yr float, l0_receiver int, l0_donor int (없으면 −1),
    inflow_at_outlet bool (들어오는 칸이 출구 칸과 겹치면 True).
    """
    g0 = planet.graph
    f0 = planet.fields
    n_side, dx = hero_grid_size(cfg)
    n = graph.n_cells
    R = float(cfg.planet.radius_m)
    c0 = int(site.l0_cell)
    rcv0 = np.asarray(f0["receiver"])
    r0 = int(rcv0[c0])
    if r0 == c0:
        raise ValueError(f"히어로 중심 L0 칸 {c0} 이 출구(바다)입니다")

    hit = edge_hit(direction_to_local(site, g0.pos[r0] - g0.pos[c0]), n_side, dx)
    outlet_cells = edge_cells(n_side, hit.edge, hit.index, OUTLET_CELLS)
    is_outlet = np.zeros(n, dtype=bool)
    is_outlet[outlet_cells] = True
    p_out = local_to_unit(site, np.array([hit.x]), np.array([hit.y]), R)
    z_out = float(
        sample_sphere(np.asarray(f0["z_m"], dtype=np.float64), int(g0.shape[1]), p_out)[0]
    )
    if not np.isfinite(z_out):
        raise ValueError("출구 위치의 L0 고도가 NaN 입니다")

    q0 = float(np.asarray(f0["discharge_m3_per_yr"], dtype=np.float64)[c0])
    q_in = q0 - float(np.sum(graph.area * R_eff))
    extra = np.zeros(n, dtype=np.float64)
    inflow_cell, inflow_edge, donor = -1, None, -1
    if q_in > 0.0:
        donors = np.flatnonzero(rcv0 == c0)
        donors = donors[donors != c0]
        if donors.size:
            q_d = np.asarray(f0["discharge_m3_per_yr"], dtype=np.float64)[donors]
            donor = int(donors[int(np.argmax(q_d))])
            direction = g0.pos[donor] - g0.pos[c0]
        else:
            # 기여 셀이 없는 L0 칸(히어로가 L0 칸보다 작을 때)은 그 칸 안의 상류, 곧 수신 셀
            # 반대쪽에서 물이 들어온다고 봅니다.
            direction = g0.pos[c0] - g0.pos[r0]
        hin = edge_hit(direction_to_local(site, direction), n_side, dx)
        inflow_cell = int(edge_cells(n_side, hin.edge, hin.index, 1)[0])
        inflow_edge = hin.edge
        extra[inflow_cell] = q_in
    return {
        "is_outlet": is_outlet,
        "outlet_cells": outlet_cells,
        "outlet_edge": hit.edge,
        "z_outlet_m": z_out,
        "extra_inflow": extra,
        "inflow_cell": inflow_cell,
        "inflow_edge": inflow_edge,
        "inflow_m3_per_yr": float(extra.sum()),
        "l0_discharge_m3_per_yr": q0,
        "l0_receiver": r0,
        "l0_donor": donor,
        "inflow_at_outlet": bool(inflow_cell >= 0 and is_outlet[inflow_cell]),
    }


def refine_hero(planet: PlanetState, site, cfg, log: Log = None) -> HeroState:
    """L0 경계조건으로 히어로 2~4단계를 풉니다 (pipeline.md 9장 refine_hero).

    planet: PlanetState (L0 결과). site: hero.finder.HeroSite. cfg: 설정. log: 진행 기록 함수.
    반환: HeroState. fields 는 표본 필드(LINEAR_FIELDS, template_id) + is_ocean(모두 False)
    + 2~4단계 필드(run_stages_2_to_4). diag: seconds, boundary, solver, stages, scorecard,
    scorecard_summary.
    """
    if not isinstance(planet, PlanetState):
        raise ValueError(f"planet 은 PlanetState 여야 합니다: {type(planet).__name__}")
    t_all = time.perf_counter()
    sec: dict[str, float] = {}

    t = time.perf_counter()
    graph, unit = hero_graph(site, cfg)
    sampled = sample_l0_fields(planet, unit)
    sec["sample"] = time.perf_counter() - t
    n = graph.n_cells
    _say(log, f"[히어로] 평면 {graph.shape[0]}×{graph.shape[1]} ({n}칸), L0 값 표본 끝")

    t = time.perf_counter()
    tid = sampled["template_id"].astype(np.uint8)
    disp = fold_displacement_from_phase(
        sampled["fold_phase"], sampled["uplift_m_per_yr"], tid, cfg, u_max=_u_max(planet)
    )
    sb, sr = build_columns(tid, sampled["exhumation_m"], disp, cfg)
    columns = LayerColumns.from_columns(sb, sr)
    sec["geology"] = time.perf_counter() - t

    t = time.perf_counter()
    bc = hero_boundary(planet, site, graph, sampled["runoff_eff_m_per_yr"], cfg)
    sec["boundary"] = time.perf_counter() - t
    _say(
        log,
        f"[히어로] 출구 {bc['outlet_edge']} 가장자리 {OUTLET_CELLS}칸, "
        f"z = {bc['z_outlet_m']:.1f} m, "
        f"들어오는 물 {bc['inflow_m3_per_yr']:.3g} m³/yr ({bc['inflow_edge']})",
    )

    t = time.perf_counter()
    inflow = bc["extra_inflow"] if bc["inflow_m3_per_yr"] > 0 else None
    z_init, warm = coarse_warm_start(
        graph, bc["is_outlet"], bc["z_outlet_m"], sampled["uplift_m_per_yr"],
        sampled["runoff_eff_m_per_yr"], columns, cfg,
        extra_inflow=inflow, max_iter=solver_max_iter(cfg, "hero"), log=log,
    )  # fmt: skip
    st = run_stages_2_to_4(
        graph,
        bc["is_outlet"],
        bc["z_outlet_m"],
        sampled["uplift_m_per_yr"],
        sampled["runoff_eff_m_per_yr"],
        sampled["precip_m_per_yr"],
        columns,
        cfg,
        extra_inflow=inflow,
        lat_deg=float(site.lat_deg),
        log=log,
        runoff=sampled["runoff_m_per_yr"],
        max_iter=solver_max_iter(cfg, "hero"),
        z_init=z_init,
    )
    st.diag["warm_start"] = warm
    sec["stages"] = time.perf_counter() - t

    fields = {
        **{k: v for k, v in sampled.items() if k != "template_id"},
        "template_id": tid,
        "is_ocean": np.zeros(n, dtype=bool),
        **st.fields,
    }

    t = time.perf_counter()
    sd = st.diag["solver"]
    card = score(
        cfg,
        hero_fields=fields,
        hero_graph=graph,
        diag={
            "hero": {
                "converged": sd["converged"],
                "iterations": sd["iterations"],
                "extra_inflow_m3_per_yr": bc["extra_inflow"],
            }
        },
    )
    sec["scorecard"] = time.perf_counter() - t
    sec["total"] = time.perf_counter() - t_all
    summary = scorecard_summary(card)
    _say(log, f"[히어로] 끝: {sec['total']:.2f} s, 점수표 불합격 {summary['failed']}")
    boundary = {k: v for k, v in bc.items() if k not in ("is_outlet", "extra_inflow")}
    diag = {
        "seconds": {**sec, "stages_detail": st.diag["seconds"]},
        "boundary": boundary,
        "solver": sd,
        "stages": {k: v for k, v in st.diag.items() if k not in ("seconds", "solver")},
        "scorecard": card,
        "scorecard_summary": summary,
        "extra_inflow_m3_per_yr": bc["extra_inflow"],
    }
    return HeroState(
        graph=graph,
        fields=fields,
        columns=columns,
        site=site,
        rivers=st.rivers,
        fan_apexes=st.fan_apexes,
        diag=diag,
    )
