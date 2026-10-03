"""히어로 유역 검사 (docs/pipeline.md 9장): 평면 히어로, 영역 크기, 행성 위 히어로 자리.

C# 콘솔의 hero --flat 결과와 all 의 히어로 묶음을 봅니다. 후보 점수·접평면 변환 같은 함수 하나는
C# Hero/*.cs 코드가 기준입니다.
"""

import numpy as np
import pytest
from bundles import load_bundle, outlet_of

from bpcg_studio.fields import FIELDS

# 2~4단계가 늘 만드는 필드 (평면이라 relief_m·z_mean_m 없음)
STAGE_FIELDS = {
    "uplift_m_per_yr", "z_m", "receiver", "drainage_area_m2", "discharge_m3_per_yr",
    "sediment_flux_m3_per_yr", "slope", "k_s", "s_crit", "not_steady", "fan", "temperature_c",
    "water_level_m", "is_lake", "is_river", "river_width_m", "river_depth_m", "valley_depth_m",
    "surface_rock", "soil_thickness_m", "alluvium_m", "bare_rock", "water_table_m",
    "cave_level_0_m", "cave_level_1_m", "cave_entrance", "strata_bottom_m", "strata_rock",
}  # fmt: skip
FLAT_INPUT_FIELDS = {
    "uplift_m_per_yr", "exhumation_m", "precip_m_per_yr", "pet_m_per_yr", "runoff_m_per_yr",
    "runoff_eff_m_per_yr", "template_id", "fold_phase", "dist_convergent_m", "is_ocean",
}  # fmt: skip
OUTLET_CELLS = 5  # Hero/Flat.cs
OUTLET_Z_M = 200.0


def test_flat_hero_fields_present(flat_hero):
    names = set(flat_hero.fields)
    assert STAGE_FIELDS | FLAT_INPUT_FIELDS <= names <= set(FIELDS)
    assert "relief_m" not in names and "z_mean_m" not in names


def test_flat_hero_boundary(flat_hero):
    g = flat_hero.graph
    rows, cols = g.shape
    b = flat_hero.diag["boundary"]
    cells = np.asarray(b["outlet_cells"])
    assert b["outlet_edge"] == "south" and len(cells) == OUTLET_CELLS
    # 남쪽 가장자리 가운데 연속한 칸 (마지막 행)
    assert (cells // cols == rows - 1).all() and np.all(np.diff(np.sort(cells % cols)) == 1)
    assert np.allclose(flat_hero["z_m"][cells], OUTLET_Z_M)
    rcv = flat_hero["receiver"].astype(np.int64)
    assert set(np.flatnonzero(rcv == np.arange(rcv.size)).tolist()) == set(cells.tolist())
    assert np.isin(outlet_of(rcv), cells).all()
    assert not flat_hero["is_ocean"].any()


def test_flat_hero_with_low_river_threshold_has_rivers_and_caves(cave_hero):
    assert cave_hero["is_river"].sum() > 10
    assert np.isfinite(cave_hero["cave_level_0_m"]).any()
    assert (cave_hero["cave_entrance"] > 0).any()
    assert cave_hero["is_lake"].any()  # 선상지가 막은 호수


def test_flat_hero_domain_size_is_adjustable(csharp_cli, tmp_path):
    """한 변·간격을 바꾸면 격자가 따라옴: 12.8 km, 50 m → 256²."""
    csharp_cli(
        "hero", "--flat", "--profile", "tiny", "--out", tmp_path,
        "--set", "profile.hero.size_m=12800.0", "--set", "profile.hero.spacing_m=50.0",
    )  # fmt: skip
    h = load_bundle(tmp_path / "hero")
    assert h.graph.shape == (256, 256) and h.graph.spacing_m == 50.0
    assert h.diag["solver"]["converged"] is True


def test_hero_size_rounds_to_spacing(csharp_cli, tmp_path):
    """한 변이 간격의 정수배가 아니면 가장 가까운 정수배 (6449 m / 100 m → 64 칸)."""
    csharp_cli(
        "hero", "--flat", "--profile", "tiny", "--out", tmp_path,
        "--set", "profile.hero.size_m=6449.0",
    )  # fmt: skip
    assert load_bundle(tmp_path / "hero").graph.shape == (64, 64)


def test_hero_size_is_capped(csharp_cli, tmp_path):
    proc = csharp_cli(
        "hero", "--flat", "--profile", "tiny", "--out", tmp_path,
        "--set", "profile.hero.spacing_m=1.0", check=False,
    )  # fmt: skip
    assert proc.returncode != 0
    assert "상한" in proc.stdout + proc.stderr


def test_planet_hero_site_and_grid(planet, hero):
    site = hero.meta["site"]
    assert -55.0 <= site["lat_deg"] <= 55.0
    assert not planet["is_ocean"][site["l0_cell"]]
    e, n, c = (np.asarray(site[k]) for k in ("east", "north", "center_unit"))
    assert np.allclose([e @ e, n @ n, c @ c], 1.0) and abs(e @ n) < 1e-9 and abs(e @ c) < 1e-9
    assert set(site["parts"]) == {"uplift_gradient", "carbonate", "relief", "dry_fraction"}
    assert 0.0 <= site["score"] <= 1.0
    assert hero.graph.kind == "flat"


@pytest.mark.parametrize("level", ["hero", "flat_hero"])
def test_hero_grid_diag(request, level):
    b = request.getfixturevalue(level)
    grid = b.diag["grid"]
    rows, cols = b.graph.shape
    assert grid["n_side"] == rows == cols
    assert grid["spacing_m"] == pytest.approx(b.graph.spacing_m)
