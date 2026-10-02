"""점수표: 행성(L0)과 히어로(L2) 결과를 지표 표로 묶습니다 (docs/pipeline.md 12장, 설계도 7장).

항목마다 {"value", "unit", "kind", "pass", "note"} 를 둡니다.

- kind: "forced"(입력으로 거의 정해짐, 회귀 테스트로만), "emergent"(결과로 나옴, 지구다움의 근거),
  "check"(일관성 불변식, 합격선이 있음).
- pass: check 와 forced 는 합격선 판정(bool), emergent 는 보고만 하므로 None 입니다.
- 입력이 없으면 그 항목은 value None, pass None, note '건너뜀: …' 입니다.
  계산 중 ValueError 가 나면 value None, check 이면 pass False, note '계산 실패: …' 입니다.

항목 이름은 '<단계>.<지표>' 입니다(예: planet.ocean_fraction, hero.grid_alignment).
합격선 기본값은 pipeline.md 12장의 값이고, 설정에 [metrics] 절이 있으면 같은 키로 덮어씁니다.
"""

import numpy as np

from bpcg.core.graph import CellGraph
from bpcg.geology import rocks as rk
from bpcg.geology.model import layer_index, rock_at_points
from bpcg.hydro.routing import topo_order
from bpcg.metrics.drainage import (
    budget_error,
    grid_alignment,
    hack_fit,
    law_consistency,
    law_cross_mask,
    law_slope_from_fields,
    river_backflow_count,
    river_reach_fraction,
)
from bpcg.metrics.hypsometry import (
    bimodality,
    flat_fraction,
    gw_surface_fraction,
    ocean_fraction,
    shelf_area,
)

# pipeline.md 12장 합격선·기준값 (설정 [metrics] 절이 있으면 같은 키로 덮어씀)
DEFAULT_THRESHOLDS: dict[str, float] = {
    "earth_ocean_fraction": 0.708,  # 지구 바다 비율 (보고용 기준)
    "shelf_depth_m": 200.0,  # 대륙붕 수심 문턱 [m]
    "bimodal_min_separation_m": 1000.0,  # 고도 분포 두 봉우리 최소 간격 [m]
    "flat_slope": 0.02,  # 평탄지 경사 문턱 [m/m]
    "gw_surface_tol_m": 0.5,  # 지하수가 '땅 겉에 닿음' 깊이 [m]
    "law_median_max": 1.0e-3,  # 법칙 자기일관성 중앙값 합격선
    "budget_max": 1.0e-6,  # 물·퇴적물 수지 합격선
    "alignment_max": 1.3,  # 격자 정렬 지수 목표
    "rock_samples": 10_000,  # '경사를 만든 암석 = 보이는 암석' 표본 수
    "hack_min_area_cells": 10.0,  # Hack 맞추기에서 뺄 작은 상류 면적 (평균 칸 면적 배수)
    "water_rule_tol_m": 1.0e-3,  # 물 규칙 비교 허용 [m] (float32 저장 반올림보다 큼)
}

EARTH_HACK_RANGE = (0.49, 0.6)  # 지구 Hack 지수 범위 (보고용)
EARTH_GW_SURFACE_RANGE = (0.22, 0.32)  # 지구 지하수가 땅 겉에 닿는 육지 비율 (Fan 2013)


def thresholds(cfg=None) -> dict[str, float]:
    """합격선 dict. cfg 에 [metrics] 절이 있으면 같은 키를 덮어씁니다."""
    th = dict(DEFAULT_THRESHOLDS)
    sec = cfg.get("metrics") if cfg is not None else None
    if sec is not None:
        for k in th:
            if k in sec:
                th[k] = float(getattr(sec, k))
    return th


# ---------------------------------------------------------------- 일관성 지표
def rock_consistency(sample_rock: np.ndarray, solver_rock: np.ndarray) -> float:
    """'경사를 만든 암석 = 보이는 암석' 비율 (pipeline.md 12장, 100%).

    sample_rock: (M,) 보이는 암석 번호(3D 함수·지표 필드). solver_rock: (M,) 솔버가 경사에 쓴 암석.
    반환: 같은 표본의 비율 (0~1). 표본이 없으면 NaN.
    """
    a = np.asarray(sample_rock)
    b = np.asarray(solver_rock)
    if a.ndim != 1 or a.shape != b.shape:
        raise ValueError(
            f"sample_rock 과 solver_rock 은 같은 (M,) 모양이어야 합니다: {a.shape} {b.shape}"
        )
    if a.shape[0] == 0:
        return float("nan")
    return float(np.mean(a.astype(np.int64) == b.astype(np.int64)))


def even_sample(mask: np.ndarray, n_samples: int) -> np.ndarray:
    """mask 가 True 인 칸에서 고르게 n_samples 개를 고릅니다 (난수 없이 같은 간격, 결정적).

    mask: (N,) bool. 반환: (M,) int64 칸 번호, M = min(n_samples, mask 칸 수).
    """
    cells = np.flatnonzero(np.asarray(mask, dtype=np.bool_))
    if cells.shape[0] <= n_samples:
        return cells.astype(np.int64)
    k = np.linspace(0, cells.shape[0] - 1, int(n_samples)).round().astype(np.int64)
    return cells[k].astype(np.int64)


def surface_rock_samples(
    z: np.ndarray,
    surface_rock: np.ndarray,
    strata_bottom: np.ndarray,
    strata_rock: np.ndarray,
    cells: np.ndarray,
) -> tuple[np.ndarray, np.ndarray]:
    """표본 칸의 (보이는 암석, 솔버 지표 층 암석).

    z: (N,) 고도 [m]. surface_rock: (N,) 지표 암석 필드. strata_bottom: (N, L) [m].
    strata_rock: (N, L+1). cells: (M,) 표본 칸. 솔버 지표 층은 geology.model.layer_index (경계 위의
    점은 아래층) 로 고르며 솔버의 s_crit·k_s 와 같은 약속입니다.
    반환: ((M,) uint8, (M,) uint8).
    """
    cells = np.asarray(cells, dtype=np.int64)
    zz = np.asarray(z, dtype=np.float64)[cells]
    b = np.asarray(strata_bottom, dtype=np.float64)[cells]
    i = layer_index(b, zz)
    solver = np.asarray(strata_rock)[cells, i].astype(np.uint8)
    return np.asarray(surface_rock)[cells].astype(np.uint8), solver


def cave_soluble_fraction(
    cave_levels: list[np.ndarray], strata_bottom: np.ndarray, strata_rock: np.ndarray
) -> tuple[float, int]:
    """동굴이 있는 (칸, 층) 가운데 그 높이의 암석이 녹는 암석인 비율 (pipeline.md 12장, 100%).

    cave_levels: (N,) 동굴 층 높이 [m] 배열의 목록 (없으면 NaN). strata_bottom: (N, L) [m].
    strata_rock: (N, L+1). 반환: (비율, 동굴 칸 수). 동굴이 없으면 (NaN, 0).
    """
    hit = 0
    total = 0
    for lv in cave_levels:
        lv = np.asarray(lv, dtype=np.float64)
        cells = np.flatnonzero(np.isfinite(lv))
        if cells.shape[0] == 0:
            continue
        rock = rock_at_points(strata_bottom, strata_rock, cells, lv[cells])
        hit += int(rk.SOLUBLE[rock].sum())
        total += int(cells.shape[0])
    return (hit / total if total else float("nan")), total


def water_rule_violations(
    z: np.ndarray,
    z_gw: np.ndarray,
    water_level: np.ndarray,
    max_depth: float | None = None,
    tol: float = 1.0e-3,
) -> dict:
    """지하수면 규칙 위반 칸 수 (pipeline.md 8.3 '규칙', 12장 '물 규칙 위반 0').

    z: (N,) 지표 [m]. z_gw: (N,) 지하수면 [m]. water_level: (N,) 수면 h_w [m] (물 칸만 유한).
    max_depth: 지하수면이 지표 아래로 내려갈 수 있는 최대 깊이 [m] (groundwater.max_depth_m) 또는
    None(검사 안 함). tol: 허용 [m].
    반환 dict: above(물 아닌 칸 z_gw > z + tol), water(물 칸 |z_gw − h_w| > tol),
    deep(z_gw < z − max_depth − tol), nan(z_gw 가 NaN), total(위반 칸 수, 한 칸은 한 번).
    """
    z = np.asarray(z, dtype=np.float64)
    g = np.asarray(z_gw, dtype=np.float64)
    hw = np.asarray(water_level, dtype=np.float64)
    if z.ndim != 1 or g.shape != z.shape or hw.shape != z.shape:
        raise ValueError("z, z_gw, water_level 은 같은 (N,) 모양이어야 합니다")
    wet = np.isfinite(hw)
    bad_nan = ~np.isfinite(g)
    with np.errstate(invalid="ignore"):
        bad_above = ~wet & (g > z + tol)
        bad_water = wet & (np.abs(g - hw) > tol)
        bad_deep = (
            (g < z - max_depth - tol) if max_depth is not None else np.zeros(z.shape, np.bool_)
        )
    total = bad_nan | bad_above | bad_water | bad_deep
    return {
        "above": int(bad_above.sum()),
        "water": int(bad_water.sum()),
        "deep": int(bad_deep.sum()),
        "nan": int(bad_nan.sum()),
        "total": int(total.sum()),
    }


# ---------------------------------------------------------------- 점수표 조립
def _entry(value, unit: str, kind: str, passed, note: str) -> dict:
    if isinstance(value, np.generic):
        value = value.item()
    return {"value": value, "unit": unit, "kind": kind, "pass": passed, "note": note}


class _Level:
    """한 단계(행성 또는 히어로)의 입력과 자주 쓰는 값(수신 셀 순서, 육지)을 들고 다닙니다."""

    def __init__(self, name: str, fields: dict, graph: CellGraph | None, diag: dict, cfg, th):
        self.name = name
        self.f = fields
        self.graph = graph
        self.diag = diag
        self.cfg = cfg
        self.th = th
        self._order = None

    def missing(self, names=(), graph=False, cfg=False) -> list[str]:
        out = [k for k in names if k not in self.f]
        if graph and self.graph is None:
            out.append("graph")
        if cfg and self.cfg is None:
            out.append("cfg")
        return out

    def arr(self, name: str, dtype=None) -> np.ndarray:
        a = np.asarray(self.f[name])
        return a if dtype is None else a.astype(dtype, copy=False)

    @property
    def n(self) -> int:
        return int(np.asarray(self.f["receiver"]).shape[0])

    def rcv(self) -> np.ndarray:
        return self.arr("receiver", np.int64)

    def order(self) -> np.ndarray:
        if self._order is None:
            self._order = topo_order(self.rcv())
        return self._order

    def root(self) -> np.ndarray:
        r = self.rcv()
        return r == np.arange(r.shape[0])

    def flag(self, name: str, n: int) -> np.ndarray:
        """bool 필드, 없으면 모두 False."""
        if name in self.f:
            return self.arr(name).astype(np.bool_)
        return np.zeros(n, dtype=np.bool_)

    def land(self, n: int) -> np.ndarray:
        return ~self.flag("is_ocean", n)


def _run(out: dict, key: str, kind: str, unit: str, missing: list[str], fn) -> None:
    if missing:
        out[key] = _entry(None, unit, kind, None, "건너뜀: 없는 입력 " + ", ".join(missing))
        return
    try:
        value, passed, note = fn()
    except ValueError as e:
        out[key] = _entry(None, unit, kind, False if kind == "check" else None, f"계산 실패: {e}")
        return
    out[key] = _entry(value, unit, kind, passed, note)


def _finite_or_none(x):
    return float(x) if x is not None and np.isfinite(x) else None


def _add_planet_only(out: dict, L: _Level) -> None:
    th = L.th
    p = L.name

    def ocean():
        v = ocean_fraction(L.arr("is_ocean", np.bool_), L.graph.area)
        return v, None, f"물 부피 보존 해수면으로 나온 값. 지구 {th['earth_ocean_fraction']}"

    _run(out, f"{p}.ocean_fraction", "emergent", "", L.missing(["is_ocean"], graph=True), ocean)

    def shelf():
        a, frac = shelf_area(
            L.arr("z_m", np.float64),
            L.graph.area,
            L.arr("crust_type", np.int64),
            L.arr("is_ocean", np.bool_),
            th["shelf_depth_m"],
        )
        note = (
            f"대륙 지각 위 수심 < {th['shelf_depth_m']:g} m, 바다의 {frac:.4f}. "
            "지각평형 흉내로 나온 값이고 퇴적물이 대륙붕에 쌓이는 과정은 없음"
        )
        return a / 1.0e6, None, note

    _run(
        out,
        f"{p}.shelf_area",
        "emergent",
        "km2",
        L.missing(["z_m", "crust_type", "is_ocean"], graph=True),
        shelf,
    )

    zname = "z_mean_m" if "z_mean_m" in L.f else "z_m"

    def bimodal():
        r = bimodality(
            L.arr(zname, np.float64), L.graph.area, min_separation_m=th["bimodal_min_separation_m"]
        )
        peaks = ", ".join(f"{v:.0f}" for v in r["peaks"])
        note = (
            f"{zname} 면적 가중 분포의 봉우리 [{peaks}] m (돌출 봉우리 {r['n_peaks']}개), "
            f"간격 > {th['bimodal_min_separation_m']:g} m 이면 합격. 지각 비율·지각평형으로 거의 "
            "정해지는 반쯤 입력"
        )
        return r["separation_m"], r["ok"], note

    _run(out, f"{p}.hypsometry_bimodal", "forced", "m", L.missing([zname], graph=True), bimodal)


def _add_common(out: dict, L: _Level) -> None:
    th = L.th
    p = L.name

    # --- Hack 법칙
    def hack():
        n = L.n
        c, h = hack_fit(
            L.graph,
            L.rcv(),
            L.order(),
            L.arr("drainage_area_m2", np.float64),
            mask=L.land(n),
            min_area_cells=th["hack_min_area_cells"],
        )
        lo, hi = EARTH_HACK_RANGE
        return _finite_or_none(h), None, f"본류 L = {c:.3g}·A^h (km, km²). 지구 약 {lo}~{hi}"

    _run(
        out,
        f"{p}.hack_exponent",
        "emergent",
        "",
        L.missing(["receiver", "drainage_area_m2"], graph=True),
        hack,
    )

    # --- 지하수가 땅 겉에 닿는 비율
    def gw():
        z = L.arr("z_m", np.float64)
        area = L.graph.area if L.graph is not None else None
        v = gw_surface_fraction(
            z, L.arr("water_table_m", np.float64), L.land(z.shape[0]), th["gw_surface_tol_m"], area
        )
        lo, hi = EARTH_GW_SURFACE_RANGE
        note = f"z − z_gw ≤ {th['gw_surface_tol_m']:g} m 인 육지 면적 비율. 지구 {lo}~{hi}"
        return _finite_or_none(v), None, note

    _run(out, f"{p}.gw_surface_fraction", "emergent", "", L.missing(["z_m", "water_table_m"]), gw)

    # --- 하천이 바다·호수·출구에 닿는 비율
    def reach():
        n = L.n
        sink = L.flag("is_ocean", n) | L.flag("is_lake", n) | L.root()
        riv = L.arr("is_river", np.bool_)
        v = river_reach_fraction(L.rcv(), L.order(), riv, sink)
        if not np.isfinite(v):
            return None, None, "하천 칸 없음"
        return v, bool(v == 1.0), f"하천 {int(riv.sum())}칸, 기준 100%"

    _run(out, f"{p}.river_reach_fraction", "check", "", L.missing(["receiver", "is_river"]), reach)

    # --- 역류 구간
    def backflow():
        v = river_backflow_count(
            L.rcv(), L.arr("water_level_m", np.float64), L.arr("is_river", np.bool_)
        )
        return v, bool(v == 0), "수신 셀 수면이 더 높은 하천 칸 수, 기준 0"

    _run(
        out,
        f"{p}.river_backflow",
        "check",
        "cells",
        L.missing(["receiver", "is_river", "water_level_m"]),
        backflow,
    )

    # --- 법칙 자기일관성
    law_given = "law_slope" in L.diag
    law_need = ["receiver", "slope", "s_crit"]
    if not law_given:
        law_need += [
            "discharge_m3_per_yr",
            "drainage_area_m2",
            "sediment_flux_m3_per_yr",
            "uplift_m_per_yr",
            "k_s",
        ]

    def law():
        n = L.n
        if law_given:
            law_s = np.asarray(L.diag["law_slope"], dtype=np.float64)
        else:
            law_s = law_slope_from_fields(
                L.graph,
                L.rcv(),
                L.arr("discharge_m3_per_yr"),
                L.arr("drainage_area_m2"),
                L.arr("sediment_flux_m3_per_yr"),
                L.arr("uplift_m_per_yr"),
                L.arr("k_s"),
                L.arr("s_crit"),
                L.cfg,
            )
        mask = L.land(n) & ~L.root() & ~L.flag("not_steady", n) & ~L.flag("fan", n)
        excl = ""
        if "strata_bottom_m" in L.f and "z_m" in L.f:
            cross = law_cross_mask(L.rcv(), L.arr("z_m", np.float64), L.arr("strata_bottom_m"))
            mask &= ~cross
            excl = f", 층 경계를 넘는 {int(cross.sum())}칸 뺌"
        r = law_consistency(L.arr("slope", np.float64), law_s, L.arr("s_crit", np.float64), mask)
        if r["n"] == 0:
            return None, None, "볼 칸 없음"
        note = (
            f"|S − 법칙|/법칙 중앙값 (최댓값 {r['max']:.3g}, {r['n']}칸{excl}), "
            f"기준 < {th['law_median_max']:g}"
        )
        return r["median"], bool(r["median"] < th["law_median_max"]), note

    _run(
        out,
        f"{p}.law_consistency",
        "check",
        "",
        L.missing(law_need, graph=not law_given, cfg=not law_given),
        law,
    )

    # --- 물·퇴적물 수지
    def water():
        src = L.graph.area * L.arr("runoff_eff_m_per_yr", np.float64)
        extra = L.diag.get("extra_inflow_m3_per_yr")
        if extra is not None:
            src = src + np.asarray(extra, dtype=np.float64)
        v = budget_error(L.rcv(), L.arr("discharge_m3_per_yr", np.float64), src, L.order())
        note = f"Q = 누적(A·R_eff{' + 들어오는 물' if extra is not None else ''}), 기준 ≤ "
        return v, bool(v <= th["budget_max"]), note + f"{th['budget_max']:g}"

    _run(
        out,
        f"{p}.water_budget",
        "check",
        "",
        L.missing(["receiver", "discharge_m3_per_yr", "runoff_eff_m_per_yr"], graph=True),
        water,
    )

    def sediment():
        src = L.graph.area * L.arr("uplift_m_per_yr", np.float64)
        v = budget_error(
            L.rcv(), L.arr("sediment_flux_m3_per_yr", np.float64), src, L.order(), True
        )
        note = f"Qs = max(누적(A·U), 0), 기준 ≤ {th['budget_max']:g}"
        return v, bool(v <= th["budget_max"]), note

    _run(
        out,
        f"{p}.sediment_budget",
        "check",
        "",
        L.missing(["receiver", "sediment_flux_m3_per_yr", "uplift_m_per_yr"], graph=True),
        sediment,
    )

    # --- 경사를 만든 암석 = 보이는 암석
    rock_given = "rock_samples" in L.diag
    rock_need = [] if rock_given else ["z_m", "surface_rock", "strata_bottom_m", "strata_rock"]

    def rock():
        if rock_given:
            sample, solver = L.diag["rock_samples"]
            v = rock_consistency(sample, solver)
            src = "3D 함수 표본"
            n_s = len(sample)
        else:
            z = L.arr("z_m", np.float64)
            n = z.shape[0]
            use = L.land(n) & ~L.flag("not_steady", n) & ~L.flag("fan", n)
            cells = even_sample(use, int(th["rock_samples"]))
            sample, solver = surface_rock_samples(
                z, L.arr("surface_rock"), L.arr("strata_bottom_m"), L.arr("strata_rock"), cells
            )
            same = sample == solver
            if "s_crit" in L.f:
                # 솔버가 쓴 S_crit 이 보이는 암석의 표 값과 같은지도 봅니다 (float32 저장 허용).
                sc = L.arr("s_crit", np.float64)[cells]
                same &= np.abs(sc - rk.S_CRIT[sample]) <= 1e-6 * np.maximum(1.0, sc)
            v = float(same.mean()) if cells.shape[0] else float("nan")
            src = "지표 필드 표본(층 기둥·S_crit 대조)"
            n_s = int(cells.shape[0])
        if not np.isfinite(v):
            return None, None, "표본 없음"
        return v, bool(v == 1.0), f"{src} {n_s}점, 기준 100%"

    _run(out, f"{p}.rock_consistency", "check", "", L.missing(rock_need), rock)

    # --- 동굴 ⊂ 녹는 암석
    cave_names = [k for k in ("cave_level_0_m", "cave_level_1_m") if k in L.f]

    def caves():
        v, cnt = cave_soluble_fraction(
            [L.arr(k) for k in cave_names], L.arr("strata_bottom_m"), L.arr("strata_rock")
        )
        if cnt == 0:
            return None, None, "동굴 칸 없음 (판정 없음)"
        return v, bool(v == 1.0), f"동굴 (칸, 층) {cnt}개, 기준 100%"

    _run(
        out,
        f"{p}.cave_in_soluble",
        "check",
        "",
        (["cave_level_0_m"] if not cave_names else [])
        + L.missing(["strata_bottom_m", "strata_rock"]),
        caves,
    )

    # --- 물 규칙 위반
    def water_rule():
        max_depth = None
        if L.cfg is not None and "groundwater" in L.cfg and "max_depth_m" in L.cfg.groundwater:
            max_depth = float(L.cfg.groundwater.max_depth_m)
        r = water_rule_violations(
            L.arr("z_m", np.float64),
            L.arr("water_table_m", np.float64),
            L.arr("water_level_m", np.float64),
            max_depth,
            th["water_rule_tol_m"],
        )
        note = (
            f"땅 위로 솟음 {r['above']}, 물 칸 수면 불일치 {r['water']}, 최대 깊이 넘음 "
            f"{r['deep']}, NaN {r['nan']}. 기준 0"
        )
        return r["total"], bool(r["total"] == 0), note

    _run(
        out,
        f"{p}.water_rule_violations",
        "check",
        "cells",
        L.missing(["z_m", "water_table_m", "water_level_m"]),
        water_rule,
    )

    # --- 격자 정렬 지수
    bands = ("edge", "center") if (L.graph is not None and L.graph.kind == "sphere") else ("all",)
    for band in bands:
        key = f"{p}.grid_alignment" + ("" if band == "all" else f"_{band}")

        def align(band=band):
            v = grid_alignment(L.graph, L.rcv(), L.arr("is_river", np.bool_), band=band)
            if not np.isfinite(v):
                return None, None, f"띠 '{band}' 에 6칸을 따라갈 하천 칸 없음"
            note = f"띠 '{band}', 면 로컬 (i, j), 6칸 하류 방향, 무작위 = 1, 목표 < "
            return v, bool(v < th["alignment_max"]), note + f"{th['alignment_max']:g}"

        _run(out, key, "check", "", L.missing(["receiver", "is_river"], graph=True), align)

    # --- 솔버 멈춤 조건
    def converged():
        c = bool(L.diag["converged"])
        it = L.diag.get("iterations")
        return c, c, f"반복 {it}회" if it is not None else "솔버 진단값"

    _run(
        out,
        f"{p}.solver_converged",
        "check",
        "",
        [] if "converged" in L.diag else ["diag.converged"],
        converged,
    )


def _add_hero_only(out: dict, L: _Level) -> None:
    th = L.th
    p = L.name

    def flat():
        s = L.arr("slope", np.float64)
        area = L.graph.area if L.graph is not None else None
        v = flat_fraction(s, L.land(s.shape[0]), th["flat_slope"], area)
        return _finite_or_none(v), None, f"경사 < {th['flat_slope']:g} 인 육지 면적 비율 (L2)"

    _run(out, f"{p}.flat_fraction", "emergent", "", L.missing(["slope"]), flat)


def scorecard(
    planet_fields: dict | None = None,
    planet_graph: CellGraph | None = None,
    hero_fields: dict | None = None,
    hero_graph: CellGraph | None = None,
    diag: dict | None = None,
    cfg=None,
) -> dict[str, dict]:
    """있는 입력으로 점수표를 만듭니다 (pipeline.md 12장).

    planet_fields / hero_fields: FIELDS 이름의 (N,) 필드 dict (strata_* 는 (N, L)). 계산 정밀도
    그대로든 저장 dtype 이든 됩니다. planet_graph / hero_graph: 각 단계의 CellGraph.
    diag: {"planet": {...}, "hero": {...}}. 단계마다 넣을 수 있는 값:
      converged, iterations (솔버 diag), extra_inflow_m3_per_yr (N,) 히어로 경계로 들어오는 물,
      law_slope (N,) 법칙 경사(없으면 필드에서 다시 계산), rock_samples (보이는 암석 (M,),
      솔버 암석 (M,)) 3D 함수 표본.
    cfg: 설정. 법칙 자기일관성(경사 법칙 상수), 지하수 최대 깊이, [metrics] 합격선에 씁니다.

    반환: {이름: {"value", "unit", "kind", "pass", "note"}}. 이름은 'planet.*', 'hero.*' 입니다.
    """
    th = thresholds(cfg)
    diag = diag or {}
    out: dict[str, dict] = {}
    for name, fields, graph in (
        ("planet", planet_fields, planet_graph),
        ("hero", hero_fields, hero_graph),
    ):
        if fields is not None and not isinstance(fields, dict):
            raise ValueError(f"{name}_fields 는 dict 여야 합니다: {type(fields).__name__}")
        if graph is not None and not isinstance(graph, CellGraph):
            raise ValueError(f"{name}_graph 는 CellGraph 여야 합니다: {type(graph).__name__}")
        level_diag = diag.get(name) or {}
        L = _Level(name, fields or {}, graph, level_diag, cfg, th)
        if graph is not None and fields:
            for k, v in L.f.items():
                if np.asarray(v).shape[:1] != (graph.n_cells,):
                    raise ValueError(
                        f"{name}_fields['{k}'] 의 길이가 그래프 칸 수 {graph.n_cells} 와 다릅니다"
                    )
        if name == "planet":
            _add_planet_only(out, L)
        _add_common(out, L)
        if name == "hero":
            _add_hero_only(out, L)
    return out


def failed_checks(card: dict[str, dict]) -> list[str]:
    """점수표에서 pass 가 False 인 항목 이름 (check·forced)."""
    return [k for k, v in card.items() if v["pass"] is False]
