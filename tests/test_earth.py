"""스튜디오 '진짜 지구와 비교' 탭: 같은 잣대 함수, 실행 비교, 기준 파일 만들기.

지구 원자료(data/, 약 4.5 GB)는 CI 에 없으므로, 기준 파일을 만드는 일은 작은 가짜 자료로만 봅니다.
저장소의 기준 파일(configs/reference/earth.json)은 구간이 코드와 같은지만 봅니다.
"""

import json
import math

import numpy as np
import pytest

from bpcg_studio import earth, earthref
from bpcg_studio import earthstats as es

# ---------------------------------------------------------------- 같은 잣대 함수


def test_hist_is_area_share_and_clips_into_end_bins():
    edges = np.array([0.0, 1.0, 2.0, 3.0])
    p = es.hist([-5.0, 0.5, 1.5, 99.0, np.nan], edges, weights=[1, 1, 2, 4, 1])
    assert p.sum() == pytest.approx(1.0)
    assert p.tolist() == pytest.approx([2 / 8, 2 / 8, 4 / 8])
    assert es.hist([], edges).sum() == 0


def test_scores_follow_their_rules():
    assert es.overlap([0.5, 0.5], [0.5, 0.5]) == pytest.approx(1.0)
    assert es.overlap([1, 0], [0, 1]) == pytest.approx(0.0)
    assert es.overlap([0, 0], [1, 0]) is None
    assert es.ratio_score(2.0, 1.0) == pytest.approx(0.5)
    assert es.ratio_score(1.0, 4.0) == pytest.approx(0.25)
    assert es.ratio_score(0.0, 1.0) is None
    assert es.center_score(0.5) == pytest.approx(1.0)
    assert es.center_score(1.0) == pytest.approx(0.0)
    assert es.curve_score([1, 2, None], [2, 4, 3]) == pytest.approx(0.5)
    assert es.curve_score([1, None], [1, 1]) is None


def test_quantiles_and_percentiles_read_the_same_distribution():
    edges = np.arange(0.0, 11.0)
    p = np.ones(10) / 10
    assert es.hist_quantiles(edges, p, (0.5,)) == pytest.approx([5.0])
    assert es.hist_percentile(edges, p, 2.5) == pytest.approx(0.25)
    assert es.percentile_of([1, 2, 3, 4], 2) == pytest.approx(0.375)
    assert es.wquantiles(np.arange(1, 6), None, (0.5,)) == pytest.approx([3.0])


def test_steepest_slopes_on_graph_and_latlon_grid():
    z = np.array([10.0, 0.0, 5.0])
    nbr = np.array([[1, 2], [0, -1], [0, 1]])
    dist = np.array([[100.0, 50.0], [100.0, 0.0], [50.0, 100.0]])
    s = es.graph_steepest_slope(z, nbr, dist)
    assert s.tolist() == pytest.approx([0.1, 0.0, 0.05])
    # 남쪽으로 1 m 씩 낮아지는 위경도 격자: 가장 가파른 쪽은 바로 남쪽 (1 m ÷ 남북 간격)
    lat = np.array([1.5, 0.5, -0.5, -1.5])
    zz = np.repeat(np.array([3.0, 2.0, 1.0, 0.0])[:, None], 8, axis=1)
    ss = es.latlon_steepest_slope(zz, lat, 1.0, 6_371_000.0)
    dy = 6_371_000.0 * math.radians(1.0)
    assert ss[1, 3] == pytest.approx(1.0 / dy)
    assert ss[3, 3] == 0.0


def test_strahler_counts_streams_not_cells():
    # 샘 0·1 이 2 에서 만나 2차, 샘 3 이 4 에서 2차 물줄기에 붙고 (차수 그대로), 5 로 나갑니다.
    rcv = np.array([2, 2, 4, 4, 5, 5])
    area = np.array([1.0, 1.0, 3.0, 1.0, 5.0, 6.0])
    counts = es.strahler_streams(rcv, np.ones(6, dtype=bool), area)
    assert counts == {1: 3, 2: 1}
    assert es.bifurcation_ratio(counts) is None
    assert es.bifurcation_ratio({1: 64, 2: 16, 3: 4, 4: 1}) == pytest.approx(4.0)


def test_terminal_and_window_relief_and_hypsometry():
    rcv = np.array([1, 2, 2, 2, 3])
    assert es.terminal_of(rcv).tolist() == [2, 2, 2, 2, 2]
    stop = np.array([False, True, False, False, False])
    assert es.terminal_of(rcv, stop).tolist() == [1, 1, 2, 2, 2]
    z = np.arange(25, dtype=float).reshape(5, 5)
    r = es.window_relief(z, (3, 3))
    assert r[2, 2] == pytest.approx(12.0)
    assert np.isnan(r[0, 0])  # 네모가 격자 밖으로 나감
    z[2, 2] = np.nan
    assert np.isnan(es.window_relief(z, (3, 3))[2, 2])
    assert es.hypsometric_integral([0.0, 0.0, 0.0, 4.0]) == pytest.approx(0.25)
    assert es.hypsometric_integral([1.0, 1.0]) is None


def test_block_mean_and_divisor():
    a = np.arange(16, dtype=float).reshape(4, 4)
    assert es.block_mean(a, 2).tolist() == [[2.5, 4.5], [10.5, 12.5]]
    assert es.nearest_divisor(9.7) == 10
    assert es.nearest_divisor(167.5) == 180


# ---------------------------------------------------------------- 저장소의 기준 파일


def test_committed_reference_matches_code_bins():
    """기준 파일의 구간이 코드와 다르면 지구 분포와 생성 분포를 다른 구간으로 비교하게 됩니다."""
    ref = earthref.load_existing()
    if ref is None:
        pytest.skip("configs/reference/earth.json 이 없습니다 (uv run bpcg earth-ref)")
    e = ref["edges"]
    for key, arr in (
        ("elev_m", es.ELEV_EDGES),
        ("log10_slope", es.LOG_SLOPE_EDGES),
        ("relief_m", es.RELIEF_EDGES),
        ("log10_precip_m_per_yr", es.LOG_P_EDGES),
        ("pet_m_per_yr", es.PET_EDGES),
        ("log10_area_km2", es.LOG_AREA_EDGES),
        ("hero_slope_deg", es.HERO_SLOPE_EDGES),
        ("relief_1km_m", es.RELIEF_1KM_EDGES),
        ("relief_5km_m", es.RELIEF_5KM_EDGES),
        ("log10_e_mm_per_kyr", es.LOG_E_EDGES),
        ("basin_slope_m_per_m", es.BASIN_SLOPE_EDGES),
    ):
        assert np.allclose(e[key], arr), (
            f"{key} 구간이 코드와 다릅니다. uv run bpcg earth-ref 로 다시 만드세요"
        )
    assert set(ref["scales"]) == set(earthref.profile_scales())
    assert (earthref.REFERENCE.stat().st_size) < 1_000_000  # 저장소 규칙: 커밋 파일 1 MB 이하
    for key, src in ref["sources"].items():
        assert src["license"] and src["citation"], key


# ---------------------------------------------------------------- 기준 파일 만들기 (가짜 자료)


def test_build_skips_missing_data_and_writes_what_it_has(tmp_path, monkeypatch):
    der = tmp_path / "derived"
    (der / "afrisar").mkdir(parents=True)
    yy, xx = np.mgrid[0:60, 0:60]
    z = (5.0 * xx + 2.0 * yy).astype(np.float32)
    np.savez(
        der / "afrisar" / "lope_dem_30m.npz",
        z=z,
        dx=np.float64(30.0),
        x0=0.0,
        y0=0.0,
        lat0=0.0,
        lon0=0.0,
    )
    np.savez(
        der / "afrisar" / "lope_ch4_reference.npz",
        basin_HI=np.array([0.3, 0.5]),
        basin_area_km2=np.array([20.0, 30.0]),
    )
    monkeypatch.setattr(earthref, "AFRISAR", der / "afrisar")
    monkeypatch.setattr(earthref, "AFRISAR_VEG", der / "veg" / "none.npz")
    monkeypatch.setattr(earthref, "ETOPO", tmp_path / "none.nc")
    out = tmp_path / "earth.json"
    lines: list[str] = []
    ref = earthref.build(out, only=["etopo", "afrisar"], log=lines.append)
    assert set(ref["data"]) == {"afrisar"}
    assert "etopo" in ref["missing"] and "download_pilot" in ref["missing"]["etopo"]
    lope = ref["data"]["afrisar"]["sites"]["lope"]
    assert lope["spacing_m"] == 90.0 and sum(lope["slope_hist"]) == pytest.approx(1.0, abs=1e-4)
    assert lope["basin_hi"] == [0.3, 0.5]
    assert json.loads(out.read_text(encoding="utf-8"))["format"] == earthref.FORMAT
    assert any("[지구 기준] 끝" in line for line in lines)
    with pytest.raises(ValueError, match="모르는 자료"):
        earthref.build(out, only=["nope"])


# ---------------------------------------------------------------- 실행 하나와 비교하기


def test_compare_tiny_run_has_every_row_with_a_kind_and_bounded_score(tiny_run):
    ref = earthref.load_existing()
    if ref is None:
        pytest.skip("configs/reference/earth.json 이 없습니다 (uv run bpcg earth-ref)")
    res = earth.compare_run(tiny_run, ref)
    ids = [r["id"] for r in res["rows"]]
    assert len(ids) == len(set(ids))
    assert {"planet.hyps_land", "planet.endorheic", "hero.slope", "hero.erosion_slope"} <= set(ids)
    for r in res["rows"]:
        assert r["kind"] in earth.KIND_TEXT
        assert r["scale"] in ("planet", "hero")
        assert r["score"] is None or 0.0 <= r["score"] <= 1.0 + 1e-9, r["id"]
        if r["status"] != "ok":
            assert r["reason"], r["id"]
    em = [
        r["score"]
        for r in res["rows"]
        if r["kind"] == "emergent" and r["status"] == "ok" and r["score"] is not None
    ]
    assert res["summary"]["n_emergent"] == len(em)
    assert res["summary"]["emergent_mean"] == pytest.approx(float(np.mean(em)))
    json.dumps(res, allow_nan=False)  # 서버가 보낼 수 있는 값만
    ocean = next(r for r in res["rows"] if r["id"] == "planet.ocean_fraction")
    assert ocean["kind"] == "forced" and 0.0 < ocean["gen_value"] < 1.0


def test_compare_without_reference_says_how_to_build(tiny_run, tmp_path):
    res = earth.compare_run(tiny_run, ref_path=tmp_path / "none.json")
    assert res["reference"] is None and res["rows"] == []
    assert "earth-ref" in res["reference_error"]


def test_flat_run_has_only_hero_rows(flat_run):
    res = earth.compare_run(flat_run, {"data": {}, "missing": {}, "scales": {}})
    assert res["levels"]["planet"] is None
    assert res["rows"] and all(r["scale"] == "hero" for r in res["rows"])
    assert {r["status"] for r in res["rows"]} <= {"no_reference", "no_run_data"}


def test_service_caches_until_the_run_changes(tiny_run, tmp_path):
    svc = earth.EarthService(ref_path=tmp_path / "none.json")
    a = svc.get(tiny_run)
    assert svc.get(tiny_run) is a
    (tmp_path / "none.json").write_text(
        json.dumps({"format": earthref.FORMAT, "data": {}, "missing": {}, "scales": {}})
    )
    b = svc.get(tiny_run)
    assert b is not a and b["reference"] is not None
