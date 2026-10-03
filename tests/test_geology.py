"""지질 검사 (docs/pipeline.md 5장): 암석 표, 템플릿, 층 기둥, 지표 암석 = 그 높이의 층 암석.

C# 이 쓴 행성·히어로 묶음과 회랑 재질 부피의 범례(strata.json)를 봅니다. 변성 문턱·습곡 식처럼
함수 하나를 보는 값은 tests/Bpcg.Tests 와 docs/pipeline.md 5장 표가 기준입니다.
"""

import numpy as np
import pytest
from bundles import read_json, rock_at

from bpcg_studio.labels import ROCK_COLOR_RGB, ROCK_NAMES

N_TEMPLATES = 3  # Geology/Model.cs: carbonate_platform, fold_thrust, basement_arc


def test_rock_table_in_legend_matches_studio_table(tiny_run):
    """회랑 재질 부피의 범례(C# 암석 표)가 스튜디오의 암석 이름·색과 같고, 색이 서로 다릅니다."""
    legend = read_json(tiny_run / "corridor" / "strata.json")["legend"]
    rocks = [e for e in legend if e["id"] < 254]
    assert [e["name"] for e in rocks] == list(ROCK_NAMES)
    assert [tuple(e["rgb"]) for e in rocks] == [tuple(c) for c in ROCK_COLOR_RGB]
    assert len({tuple(e["rgb"]) for e in legend}) == len(legend)
    assert {e["id"]: e["name"] for e in legend if e["id"] >= 254} == {254: "water", 255: "air"}


@pytest.mark.parametrize("level", ["planet", "hero"])
def test_layer_columns(request, level):
    b = request.getfixturevalue(level)
    bottoms = b["strata_bottom_m"]
    rocks = b["strata_rock"]
    n = b.graph.n_cells
    assert bottoms.shape[0] == n and rocks.shape == (n, bottoms.shape[1] + 1)
    assert (np.diff(bottoms, axis=1) <= 0).all()  # 층 바닥은 아래로 갈수록 낮아짐
    assert set(np.unique(rocks)) <= set(range(len(ROCK_NAMES)))
    assert set(np.unique(b["template_id"])) <= set(range(N_TEMPLATES))
    if "fold_phase" in b.fields:
        assert np.isfinite(b["fold_phase"]).all()


@pytest.mark.parametrize("level", ["planet", "hero"])
def test_surface_rock_is_rock_at_surface(request, level):
    """지표 암석 = 그 칸 기둥에서 지표 바로 아래 높이의 암석 (점수표 rock_consistency 의 뜻)."""
    b = request.getfixturevalue(level)
    z = b["z_m"].astype(np.float64)
    assert np.array_equal(rock_at(b, z - 0.01), b["surface_rock"].astype(np.int64))


def test_planet_uses_all_templates(planet):
    assert set(np.unique(planet["template_id"])) == set(range(N_TEMPLATES))


def test_deterministic(planet, tiny_planet_again):
    for k in ("template_id", "strata_bottom_m", "strata_rock", "surface_rock"):
        assert np.array_equal(planet[k], tiny_planet_again[k]), k
