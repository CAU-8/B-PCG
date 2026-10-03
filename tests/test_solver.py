"""정상상태 솔버 검사 (docs/pipeline.md 7.1, 가이드 4장 '확인하기'): C# 결과의 진단값과 필드.

수렴·반복 기록·고정 칸·경사·법칙 일관성·퇴적물 수지와 결정성을 봅니다. 손으로 만든 섬·띠 입력의
해석해 비교는 포팅 때 golden 으로 맞춘 C# 코드가 기준입니다.
"""

import numpy as np
import pytest


@pytest.fixture(params=["planet", "hero", "flat_hero"])
def bundle(request):
    return request.getfixturevalue(request.param)


def test_solver_converged_with_history(bundle):
    s = bundle.diag["solver"]
    assert s["converged"] is True
    assert len(s["history"]) == s["iterations"] > 0
    assert s["final_n_changed"] == 0
    assert 0 <= s["n_frozen"] < bundle.graph.n_cells * 0.01  # 고정 칸(드나드는 칸)은 드묾


def test_steady_fields(bundle):
    assert (bundle["slope"] >= 0).all()
    for k in ("k_s", "s_crit", "slope", "z_m"):
        assert np.isfinite(bundle[k]).all(), k
    assert (bundle["k_s"] >= 0).all() and (bundle["s_crit"] > 0).all()  # 강이 아닌 칸의 k_s 는 0
    assert bundle["not_steady"].mean() < 0.01


@pytest.mark.parametrize("level", ["planet", "hero"])
def test_law_and_budgets_pass(request, level):
    b = request.getfixturevalue(level)
    card = b.diag["scorecard"]
    for name in ("law_consistency", "water_budget", "sediment_budget", "solver_converged"):
        e = card[f"{level}.{name}"]
        assert e["pass"] in (True, None), (name, e)


def test_flat_hero_deterministic(csharp_cli, flat_hero, tmp_path):
    csharp_cli("hero", "--flat", "--profile", "tiny", "--seed", "3", "--out", tmp_path)
    from bundles import load_bundle

    again = load_bundle(tmp_path / "hero")
    for k in ("z_m", "receiver", "discharge_m3_per_yr", "water_table_m"):
        assert np.array_equal(again[k], flat_hero[k], equal_nan=True), k
    assert again.diag["solver"]["iterations"] == flat_hero.diag["solver"]["iterations"]
