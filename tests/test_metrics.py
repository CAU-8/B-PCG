"""점수표 검사 (docs/pipeline.md 12장): C# 이 쓴 scorecard.json 과 묶음 진단값.

항목 모양, 종류(kind), 요약 수, 그리고 결과 필드로 다시 셀 수 있는 지표(바다·대륙붕 넓이, 지하수면이
지표에 닿는 넓이, 평평한 넓이)가 점수표 값과 같은지 봅니다. Hack 지수·격자 정렬 지수 같은 계산은
C# 지표 코드(Metrics/*.cs)와 그 시험(tests/Bpcg.Tests/Metrics)이 기준입니다.
"""

import numpy as np
import pytest
from bundles import read_json

SHELF_DEPTH_M = 200.0  # Scorecard.cs DefaultThresholds["shelf_depth_m"]
GW_SURFACE_TOL_M = 0.5  # ["gw_surface_tol_m"]
FLAT_SLOPE = 0.02  # ["flat_slope"]
KINDS = {"forced", "emergent", "check", "self"}
ERROR_PREFIX = "계산 실패: "  # Scorecard.cs ErrorPrefix
# 만드는 코드가 늘 맞게 짠 규칙(코드 자기 검사, kind self).
# 나머지 판정 항목은 결과를 다시 재는 check 입니다.
SELF_CHECKS = {
    "river_reach_fraction",
    "river_backflow",
    "water_budget",
    "sediment_budget",
    "rock_consistency",
    "cave_in_soluble",
    "water_rule_violations",
}


@pytest.mark.parametrize("level", ["planet", "hero"])
def test_scorecard_file_matches_bundle_and_summary(request, tiny_run, level):
    b = request.getfixturevalue(level)
    card = read_json(tiny_run / level / "scorecard.json")
    assert card == b.diag["scorecard"]
    for name, e in card.items():
        assert set(e) == {"value", "unit", "kind", "pass", "note"}, name
        assert e["kind"] in KINDS and e["pass"] in (True, False, None), name
        assert isinstance(e["note"], str) and e["note"], name
    s = b.diag["scorecard_summary"]
    assert s["n_items"] == len(card)
    assert s["n_judged"] == sum(e["pass"] is not None for e in card.values())
    assert s["n_failed"] == len(s["failed"]) == sum(e["pass"] is False for e in card.values())
    assert s["n_failed"] == 0
    errors = sorted(k for k, e in card.items() if e["note"].startswith(ERROR_PREFIX))
    assert s["n_errors"] == len(s["errors"]) == 0 and s["errors"] == errors


def test_scorecard_kinds(planet, hero):
    """자기 검사·검사·입력이 정하는 값의 종류 (pipeline.md 12장 표)."""
    for b in (planet, hero):
        card = b.diag["scorecard"]
        for name, e in card.items():
            base = name.split(".", 1)[1]
            if base in SELF_CHECKS:
                assert e["kind"] == "self", name
            elif base in ("law_consistency", "solver_converged") or base.startswith("grid_"):
                assert e["kind"] == "check", name
    assert planet.diag["scorecard"]["planet.ocean_fraction"]["kind"] == "forced"


def test_planet_area_metrics_recount(planet):
    card = planet.diag["scorecard"]
    a = planet.graph.area
    f = planet.fields
    ocean = f["is_ocean"]
    assert card["planet.ocean_fraction"]["value"] == pytest.approx(a[ocean].sum() / a.sum())
    shelf = (f["crust_type"] == 1) & ocean & (f["z_m"] > -SHELF_DEPTH_M)
    assert card["planet.shelf_area"]["value"] == pytest.approx(a[shelf].sum() / 1e6, rel=1e-9)
    land = ~ocean
    near = land & (f["z_m"].astype(np.float64) - f["water_table_m"] <= GW_SURFACE_TOL_M)
    assert card["planet.gw_surface_fraction"]["value"] == pytest.approx(
        a[near].sum() / a[land].sum()
    )


def test_hero_area_metrics_recount(hero):
    card = hero.diag["scorecard"]
    a = hero.graph.area
    f = hero.fields
    flat = f["slope"] < FLAT_SLOPE
    assert card["hero.flat_fraction"]["value"] == pytest.approx(a[flat].sum() / a.sum())
    near = f["z_m"].astype(np.float64) - f["water_table_m"] <= GW_SURFACE_TOL_M
    assert card["hero.gw_surface_fraction"]["value"] == pytest.approx(a[near].sum() / a.sum())


def test_bimodal_hypsometry_and_hack_are_reported(planet, hero):
    pc = planet.diag["scorecard"]
    e = pc["planet.hypsometry_bimodal"]
    assert e["pass"] is True and e["value"] > 1000.0  # 대륙·바다 두 봉우리, 간격 > 1000 m
    h = hero.diag["scorecard"]["hero.hack_exponent"]
    assert h["value"] is not None and 0.3 < h["value"] < 0.9
    # 행성 단계의 점수표에서는 히어로 항목이 입력이 없어 건너뜀
    assert pc["hero.flat_fraction"]["value"] is None
