"""물길 검사 (docs/pipeline.md 6장): C# 이 쓴 수신 셀·집수 넓이·유량·퇴적물·강 구간.

채움·D8·히스테리시스 같은 함수 하나하나는 tests/Bpcg.Tests 의 hydro golden 대조 시험(비트 일치)이
보고, 여기서는 결과 묶음이 물길 불변식을 지키는지 봅니다.
"""

import numpy as np
import pytest
from bundles import accumulate, load_config_dict, outlet_of, topo_order

SECONDS_PER_YEAR = 365.25 * 86_400.0


@pytest.fixture(params=["planet", "hero"])
def bundle(request):
    return request.getfixturevalue(request.param)


def _non_root(b):
    rcv = b["receiver"].astype(np.int64)
    return rcv, rcv != np.arange(rcv.size)


def test_receivers_are_neighbours_and_acyclic(bundle):
    rcv, nr = _non_root(bundle)
    nbr = bundle.graph.nbr
    assert (nbr[nr] == rcv[nr][:, None]).any(axis=1).all()
    order = topo_order(rcv)
    assert np.array_equal(np.sort(order), np.arange(rcv.size))
    roots = np.flatnonzero(~nr)
    assert np.isin(outlet_of(rcv), roots).all()  # 모든 칸이 출구에 닿음


def test_water_flows_downhill_except_lake_floors(bundle):
    rcv, nr = _non_root(bundle)
    z = bundle["z_m"].astype(np.float64)
    downhill = z[rcv] < z
    assert (downhill | ~nr | bundle["is_lake"]).all()


def test_drainage_area_is_accumulated_area(bundle):
    rcv, _ = _non_root(bundle)
    expect = accumulate(rcv, bundle.graph.area)
    assert np.allclose(bundle["drainage_area_m2"], expect, rtol=1e-6)


def test_discharge_is_accumulated_runoff_plus_inflow(bundle):
    rcv, _ = _non_root(bundle)
    src = bundle.graph.area * bundle["runoff_eff_m_per_yr"].astype(np.float64)
    boundary = bundle.diag.get("boundary")
    if boundary and boundary.get("inflow_m3_per_yr", 0) > 0:
        src[boundary["inflow_cell"]] += boundary["inflow_m3_per_yr"]
    assert np.allclose(bundle["discharge_m3_per_yr"], accumulate(rcv, src), rtol=1e-6)


def test_sediment_flux_is_accumulated_uplift(bundle):
    """정상상태에서 퇴적물 플럭스 = 상류 Σ A·U (솔버가 실제로 쓴 융기, 질량 보존)."""
    rcv, nr = _non_root(bundle)
    src = bundle.graph.area * bundle["uplift_m_per_yr"].astype(np.float64)
    qs = accumulate(rcv, src)
    assert np.allclose(bundle["sediment_flux_m3_per_yr"], qs, rtol=1e-6, atol=1.0)
    assert bundle["sediment_flux_m3_per_yr"][~nr].sum() == pytest.approx(src.sum(), rel=1e-6)


def test_river_cells_carry_threshold_discharge(bundle):
    q_min = float(load_config_dict()["rivers"]["min_discharge_m3_per_s"]) * SECONDS_PER_YEAR
    river = bundle["is_river"]
    assert river.any()
    assert (bundle["discharge_m3_per_yr"][river] >= q_min).all()
    assert (bundle["river_width_m"][~river] == 0).all() and (
        bundle["river_width_m"][river] > 0
    ).all()
    assert (bundle["river_depth_m"][~river] == 0).all()


def test_river_segments_partition_river_cells(hero):
    rcv = hero["receiver"].astype(np.int64)
    segs = hero.rivers()
    assert len(segs) == hero.manifest["rivers"]["n_segments"] > 0
    cells = np.concatenate(segs)
    assert len(np.unique(cells)) == len(cells) == hero.manifest["rivers"]["n_cells"]
    assert set(cells.tolist()) == set(np.flatnonzero(hero["is_river"]).tolist())
    for seg in segs:  # 구간 안에서는 한 칸 아래가 수신 셀
        assert (rcv[seg[:-1]] == seg[1:]).all()
