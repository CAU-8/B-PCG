"""프랙탈 디테일 검사: 띠 제한 자기 아핀 잡음, 스펙트럼 측정, 회랑 굽기의 detail 파일."""

import json
import math
import shutil

import numpy as np
import pytest

from bpcg.bake.corridor import DETAIL_STEMS, WATER_NONE_M, _grid, bake_corridor
from bpcg.bake.detail import (
    NOTE,
    add_fractal_detail,
    band_rms,
    base_sinks,
    detail_settings,
    detail_weight,
    fractal_field,
    psd_slope,
    white_noise,
)
from bpcg.bake.heightmap import read_heightmap
from bpcg.core.config import Config, load_config
from bpcg.core.graph import flat_graph
from bpcg.geology import rocks as rk
from bpcg.hydro.depressions import fill_depressions
from bpcg.pipeline import generate_hero
from bpcg.volume.sample import HeroVolume

# tests/test_bake.py 와 같은 조건 (선상지·호수·동굴 입구가 생기는 tiny 히어로). 설명은 그 파일에.
HERO_OVERRIDES = {"landscape.slope_exponent_n": 1.0, "rivers.min_discharge_m3_per_s": 0.02}
META_KEYS = (
    "gain", "hurst", "min_wavelength_m", "max_wavelength_m", "anchor_rms_m", "rms_m",
    "max_abs_m", "beta_before", "beta_after", "n_filled", "note",
)  # fmt: skip


@pytest.fixture(scope="module")
def hero_cfg():
    cfg = load_config("earth", "tiny", overrides=HERO_OVERRIDES)
    return generate_hero(cfg, log=None), cfg


@pytest.fixture(scope="module")
def baked(hero_cfg, tmp_path_factory):
    hero, cfg = hero_cfg
    out = tmp_path_factory.mktemp("corridor_detail")
    eng = tmp_path_factory.mktemp("engine_detail")
    man = bake_corridor(hero, cfg, out, engine_dir=eng, log=None)
    return man, out, eng


def _grid_xy(man, meta):
    """높이맵 표본의 국소 (동, 북) [m]. 굽기와 같은 식이라 지표를 비트까지 같게 다시 잽니다."""
    xs, ys = _grid(man["corridor"]["rect"], man["corridor"]["voxel_m"])
    assert (xs.size, ys.size) == (meta["width"], meta["height"])
    return np.meshgrid(xs, ys)


def _edge_outlets(wet):
    out = wet.copy()
    out[0, :] = out[-1, :] = out[:, 0] = out[:, -1] = True
    return out


# ---------------------------------------------------------------- 띠 제한 잡음
@pytest.mark.parametrize("hurst", [0.3, 0.8])
def test_fractal_field_slope_matches_hurst(hurst):
    f, diag = fractal_field((512, 512), 2.0, 4.0, 100.0, hurst, seed=0)
    # 테이퍼(70–100 m) 밖의 평평한 띠에서 잽니다. P ∝ k^−(2+2H)
    beta = psd_slope(f, 2.0, 8.0, 50.0)
    assert abs(beta - (2.0 + 2.0 * hurst)) < 0.35, beta
    assert abs(f.std() - 1.0) < 0.1  # 기댓값 기준 표준편차 1
    assert abs(f.mean()) < 0.1
    assert diag["min_wavelength_eff_m"] == 4.0 and not diag["taper_top"]


@pytest.mark.parametrize(
    ("spacing", "lam_min", "k_hi"),
    [(2.0, 4.0, 0.25), (1.0, 4.0, 0.25), (8.0, 4.0, 1.0 / 16.0)],
)
def test_fractal_field_zero_power_outside_band(spacing, lam_min, k_hi):
    n = 256
    f, diag = fractal_field((n, n), spacing, lam_min, 100.0, 0.7, seed=3, pad_m=0.0)
    assert diag["k_hi_per_m"] == pytest.approx(k_hi)
    P = np.abs(np.fft.fft2(f)) ** 2
    ky = np.fft.fftfreq(n, d=spacing)
    k = np.hypot(ky[:, None], ky[None, :])
    outside = (k < 1.0 / 100.0) | (k > k_hi)
    assert P[outside].sum() <= 1e-20 * P.sum()
    assert P[~outside].sum() > 0


def test_fractal_field_deterministic_and_seeded():
    a, _ = fractal_field((128, 96), 2.0, 4.0, 100.0, 0.7, seed=0, cell_offset=(10, -20))
    b, _ = fractal_field((128, 96), 2.0, 4.0, 100.0, 0.7, seed=0, cell_offset=(10, -20))
    c, _ = fractal_field((128, 96), 2.0, 4.0, 100.0, 0.7, seed=1, cell_offset=(10, -20))
    assert np.array_equal(a, b)
    assert abs(np.corrcoef(a.ravel(), c.ravel())[0, 1]) < 0.2


def test_fractal_field_translation_consistent():
    # 흰 잡음은 전역 칸 번호의 해시라 창을 옮겨도 같은 칸은 정확히 같습니다.
    wa = white_noise((64, 64), 0, (1000, -500))
    wb = white_noise((64, 64), 0, (1040, -470))
    assert np.array_equal(wa[40:, 30:], wb[:24, :34])
    # 거른 값은 창 둘레(PAD_WAVELENGTHS·max_λ)까지 만들어 거르므로 거의 같습니다.
    a, _ = fractal_field((256, 256), 2.0, 4.0, 100.0, 0.7, 0, (1000, -500))
    b, _ = fractal_field((256, 256), 2.0, 4.0, 100.0, 0.7, 0, (1040, -470))
    diff = a[40:, 30:] - b[:216, :226]
    assert np.sqrt(np.mean(diff * diff)) < 0.02
    assert np.abs(diff).max() < 0.1


def test_fractal_field_rejects_empty_band():
    with pytest.raises(ValueError):
        fractal_field((64, 64), 50.0, 4.0, 100.0, 0.7, seed=0)  # 나이퀴스트 100 m 이상만 그림
    with pytest.raises(ValueError):
        fractal_field((64, 64), 2.0, 100.0, 4.0, 0.7, seed=0)


def test_band_rms_and_psd_slope_on_known_signals():
    n, dx = 512, 2.0
    x = np.arange(n) * dx
    z = 3.0 * np.sin(2 * np.pi * x / 40.0)[None, :] + np.zeros((n, 1)) + 0.01 * x[:, None]
    assert band_rms(z, dx, 30.0, 55.0) == pytest.approx(3.0 / math.sqrt(2.0), rel=0.05)
    assert band_rms(z, dx, 100.0, 200.0) < 0.05  # 평면은 빼고, 다른 띠로 새지 않음
    assert math.isnan(psd_slope(z, dx, 4.0, 4.5))  # 칸이 3개보다 적음


# ---------------------------------------------------------------- 크기 맞추기 (가짜 HeroVolume)
class _StubVolume:
    """지표 아래 재질이 모두 같은 가짜 3D 함수 (흙 항만 검사)."""

    def __init__(self, material: int):
        self.material = np.uint8(material)

    def evaluate_grid(self, x, y, up, keys=("solid_material",)):
        return {"solid_material": np.full(np.shape(up), self.material, dtype=np.uint8)}


def _synthetic_surface(n=384, dx=2.0, lam=50.0):
    """히어로가 그린 것처럼 lam 보다 긴 파장만 있는 가파른(경사 2) 지표."""
    base, _ = fractal_field((n, n), dx, lam, 1000.0, 0.7, seed=5, pad_m=0.0)
    x = np.arange(n) * dx
    gx, gy = np.meshgrid(x, -x)
    return 2.0 * gx + 20.0 * base, gx, gy


def test_detail_continues_spectrum_from_anchor():
    # 가장자리 항은 끕니다 (창 전체에서 띠 RMS 를 잼). 가장자리 항은 따로 검사합니다.
    # 세기 1 (이어 가기) 에서 재고, 2 배를 따로 봅니다 (설정 기본값은 2).
    cfg = load_config(
        "earth", "tiny", overrides={"detail.edge_fade_m": 0.0, "detail.fractal_gain": 1.0}
    )
    lam = cfg.detail.max_wavelength_m
    surf, gx, gy = _synthetic_surface(lam=lam)
    dx = 2.0
    wet = np.zeros(surf.shape, dtype=bool)
    zd, meta = add_fractal_detail(surf, gx, gy, dx, wet, _StubVolume(rk.GRANITE), cfg)
    h = cfg.detail.hurst
    anchor = band_rms(surf, dx, lam, 2.0 * lam)
    assert meta["anchor_rms_m"] == pytest.approx(anchor)
    # 테이퍼 밖 [λ/4, λ/2] 옥타브는 이음 기준에서 두 옥타브 아래: a·2^−2H (경사 ≥ slope_ref, 암반)
    got = band_rms(zd - surf, dx, lam / 4.0, lam / 2.0)
    assert got == pytest.approx(cfg.detail.fractal_gain * anchor * 2.0 ** (-2.0 * h), rel=0.25)
    assert meta["beta_after"] < meta["beta_before"]
    # 흙이 덮이면 soil_factor 배
    zs, _ = add_fractal_detail(surf, gx, gy, dx, wet, _StubVolume(rk.SOIL), cfg)
    ratio = band_rms(zs - surf, dx, lam / 4.0, lam / 2.0) / got
    assert ratio == pytest.approx(cfg.detail.soil_factor, rel=0.1)
    # gain 2 는 두 배
    z2, _ = add_fractal_detail(
        surf, gx, gy, dx, wet, _StubVolume(rk.GRANITE),
        cfg.with_overrides({"detail.fractal_gain": 2.0}),
    )  # fmt: skip
    assert band_rms(z2 - surf, dx, lam / 4.0, lam / 2.0) / got == pytest.approx(2.0, rel=0.1)


def test_detail_spares_edges_cave_mouths_and_base_pits():
    """가장자리·동굴 입구 구멍·원래 웅덩이 칸은 원래 지표와 비트까지 같습니다.

    둘레는 서서히 오릅니다.
    """
    cfg = load_config("earth", "tiny")
    surf, gx, gy = _synthetic_surface()
    dx = 2.0
    n = surf.shape[0]
    surf[200:206, 150:156] -= 30.0  # 원래 지표의 닫힌 웅덩이 (비탈이라 일부 칸만 웅덩이)
    wet = np.zeros(surf.shape, dtype=bool)
    sink = base_sinks(surf, wet)
    assert sink[200:206, 150:156].sum() >= 10
    mouth = np.full(surf.shape, 20.0)
    mouth[100:104, 250:256] = -1.0  # 동굴 입구 구멍
    zd, meta = add_fractal_detail(surf, gx, gy, dx, wet, _StubVolume(rk.GRANITE), cfg, mouth=mouth)
    assert meta["n_cave_mouth_cells"] == 24 and meta["edge_fade_m"] == cfg.detail.edge_fade_m
    for sl in (np.s_[0, :], np.s_[-1, :], np.s_[:, 0], np.s_[:, -1]):
        assert np.array_equal(zd[sl], surf[sl])  # 가장자리
    assert np.array_equal(zd[100:104, 250:256], surf[100:104, 250:256])  # 입구 구멍
    assert np.array_equal(zd[sink], surf[sink])  # 원래 웅덩이
    # 가장자리에서 edge_fade_m 안은 안쪽보다 디테일이 작음
    fade = int(cfg.detail.edge_fade_m / dx)
    d = np.abs(zd - surf)
    inner = d[fade + 10 : n - fade - 10, fade + 10 : n - fade - 10]
    assert d[1 : fade // 4, fade:-fade].mean() < 0.5 * inner.mean()


def test_fractal_field_empty_band_raises():
    # 띠가 FFT 칸 하나보다 좁으면 크기를 맞출 수 없어 ValueError (굽기는 이것을 잡아 건너뜀)
    with pytest.raises(ValueError, match="FFT 칸이 없습니다"):
        fractal_field((8, 8), 49.0, 98.5, 99.0, 0.7, seed=0, pad_m=0.0)


def test_detail_weight_water_ramp():
    cfg = load_config("earth", "tiny")
    st = detail_settings(cfg)
    n, dx = 64, 2.0
    x = np.arange(n) * dx
    gx, gy = np.meshgrid(x, -x)
    surf = 3.0 * gx  # 경사 3 ≥ slope_ref
    wet = np.zeros((n, n), dtype=bool)
    wet[:, :4] = True  # 서쪽 끝 띠가 물
    m, parts = detail_weight(surf, gx, gy, dx, wet, _StubVolume(rk.GRANITE), st)
    dist = (np.arange(n) - 3) * dx  # 마지막 물 열(3)에서의 거리
    want = np.clip((dist - st["water_margin_m"]) / 20.0, 0.0, 1.0)
    want[:4] = 0.0
    np.testing.assert_allclose(parts["m_water"][n // 2], want, atol=1e-12)
    assert np.all(m[wet] == 0.0)
    np.testing.assert_allclose(m[:, 20:-8], 1.0, atol=1e-9)


# ---------------------------------------------------------------- 회랑 굽기
def test_bake_writes_detail_files_on_same_grid(baked, hero_cfg):
    man, out, eng = baked
    hero, cfg = hero_cfg
    for stem in DETAIL_STEMS:
        for ext in (".bin", ".json"):
            assert f"{stem}{ext}" in man["files"]
            assert (out / f"{stem}{ext}").exists() and (eng / f"{stem}{ext}").exists()
    z, meta = read_heightmap(out / "heightmap")
    zd, dmeta = read_heightmap(out / "heightmap_detail")
    md, mmeta = read_heightmap(out / "cave_mouth_detail")
    for a, m in ((zd, dmeta), (md, mmeta)):
        assert a.shape == z.shape
        for key in ("origin", "spacing_m", "width", "height", "format", "layout", "axes"):
            assert m[key] == meta[key], key
    # manifest 의 설명
    fm = man["file_meta"]["heightmap_detail"]
    assert fm["width"] == meta["width"] and fm["origin"] == meta["origin"]
    det = fm["detail"]
    for key in META_KEYS:
        assert key in det, key
    assert det["note"] == NOTE
    assert det["gain"] == cfg.detail.fractal_gain and det["hurst"] == cfg.detail.hurst
    assert det["anchor_rms_m"] > 0 and det["rms_m"] > 0 and det["n_filled"] >= 0
    assert det["max_abs_m"] == pytest.approx(
        float(np.abs(zd - z.astype(np.float64)).max()), abs=1e-3
    )
    assert det["beta_after"] < det["beta_before"]
    assert man["file_meta"]["cave_mouth_detail"]["n_open"] == int((md < 0).sum())
    assert "detail" in man["seconds"]


def test_bake_detail_keeps_water_and_has_no_new_pits(baked, hero_cfg):
    man, out, _ = baked
    hero, cfg = hero_cfg
    z, meta = read_heightmap(out / "heightmap")
    zd, _ = read_heightmap(out / "heightmap_detail")
    w, _ = read_heightmap(out / "water")
    wet = w > WATER_NONE_M
    assert wet.any()
    assert np.array_equal(zd[wet], z[wet])  # 물 칸은 비트까지 같음
    # 가파른 암반(물에서 먼 곳)에는 디테일이 붙음
    vol = HeroVolume(hero, cfg)
    gx, gy = _grid_xy(man, meta)
    h = vol.surface_height(gx.ravel(), gy.ravel()).reshape(gx.shape)  # 굽기와 같은 float64 지표
    voxel = meta["spacing_m"]
    m, parts = detail_weight(h, gx, gy, voxel, wet, vol, detail_settings(cfg))
    rock = ~parts["soil"] & (parts["slope"] >= cfg.detail.slope_ref) & (parts["m_water"] >= 1.0)
    assert rock.sum() > 100
    assert (np.abs(zd - z)[rock] > 0.01).mean() > 0.9
    # 새 웅덩이 없음. 출구 = 가장자리 + 물 칸 + 원래 지표의 웅덩이 칸 (flat_graph 이웃으로 계산)
    ny, nx = z.shape
    nbr = flat_graph(ny, nx, voxel).nbr
    edge = _edge_outlets(wet)
    sink = fill_depressions(h.ravel(), nbr, edge.ravel()).reshape(h.shape) > h
    mouth, _ = read_heightmap(out / "cave_mouth")
    cave = mouth < 0.0
    outlet = edge | sink | cave
    # 원래 웅덩이 칸과 동굴 입구 구멍은 원래 지표와 비트까지 같음 (모양을 바꾸지 않음)
    assert np.array_equal(zd[sink], z[sink]) and np.array_equal(zd[cave], z[cave])
    zd64 = zd.astype(np.float64)
    refill = fill_depressions(zd64.ravel(), nbr, outlet.ravel()).reshape(zd.shape)
    assert np.array_equal(refill[~outlet], zd64[~outlet])
    assert man["file_meta"]["heightmap_detail"]["detail"]["n_base_sink_cells"] == int(sink.sum())


def test_bake_cave_mouth_detail_matches_volume(baked, hero_cfg):
    man, out, _ = baked
    hero, cfg = hero_cfg
    z, meta = read_heightmap(out / "heightmap")
    zd, _ = read_heightmap(out / "heightmap_detail")
    md, _ = read_heightmap(out / "cave_mouth_detail")
    assert np.all(np.abs(md) <= 20.0)
    vol = HeroVolume(hero, cfg)
    gx, gy = _grid_xy(man, meta)
    flat_md = md.ravel()
    inner = np.flatnonzero(np.abs(flat_md) < 19.0)
    pick = np.concatenate([np.flatnonzero(flat_md < 0)[:40], inner[:: max(inner.size // 60, 1)]])
    assert pick.size > 20
    pts = np.stack(
        [gx.ravel()[pick], gy.ravel()[pick], zd.ravel()[pick].astype(np.float64)], axis=1
    )
    np.testing.assert_allclose(flat_md[pick], vol.evaluate(pts)["d_cave"], atol=1e-3)


@pytest.mark.parametrize("how", ["gain_zero", "no_section"])
def test_bake_without_detail(baked, hero_cfg, tmp_path, how):
    man, out, eng = baked
    hero, cfg = hero_cfg
    if how == "gain_zero":
        cfg0 = cfg.with_overrides({"detail.fractal_gain": 0.0})
    else:
        data = cfg.as_dict()
        data.pop("detail")
        cfg0 = Config(data)
    assert detail_settings(cfg0) is None
    # 지난 굽기(디테일 있음) 폴더에 다시 구우면 디테일 파일이 지워져야 엔진이 끈 층으로 봅니다.
    out0 = tmp_path / "out"
    eng0 = tmp_path / "eng"
    shutil.copytree(out, out0)
    shutil.copytree(eng, eng0)
    man0 = bake_corridor(hero, cfg0, out0, engine_dir=eng0, log=None)
    for stem in DETAIL_STEMS:
        for ext in (".bin", ".json"):
            assert not (out0 / f"{stem}{ext}").exists()
            assert not (eng0 / f"{stem}{ext}").exists()
            assert f"{stem}{ext}" not in man0["files"]
        assert stem not in man0["file_meta"]
    assert "detail" not in man0["seconds"]
    # 나머지 파일과 manifest 는 디테일을 켠 굽기와 같음
    for name in man0["files"]:
        if name.endswith(".bin") or name.endswith(".u8"):
            assert (out0 / name).read_bytes() == (out / name).read_bytes(), name

    def strip(folder):
        m = json.loads((folder / "manifest.json").read_text())
        for k in ("seconds", "config_digest"):
            m.pop(k)
        m["files"] = [f for f in m["files"] if not f.startswith(DETAIL_STEMS)]
        for stem in DETAIL_STEMS:
            m["file_meta"].pop(stem, None)
        return m

    assert strip(out0) == strip(out)


# ---------------------------------------------------------------- bpcg bake 설정
def test_bake_config_fills_missing_detail_and_limits_set(hero_cfg, tmp_path):
    """[detail] 이 생기기 전 히어로 묶음도 지금 설정 파일의 [detail] 로 굽습니다.

    bake --set 은 굽기에서만 쓰는 키만 받습니다.
    """
    from bpcg.bake.bundle import save_hero_state
    from bpcg.cli import bake_config

    hero, cfg = hero_cfg
    data = cfg.as_dict()
    data.pop("detail")
    save_hero_state(tmp_path / "hero", hero, Config(data))
    got = bake_config(tmp_path / "hero")
    assert got["detail"].as_dict() == load_config("earth", "tiny")["detail"].as_dict()
    assert got["landscape.slope_exponent_n"] == 1.0  # 나머지는 묶음 설정 그대로
    two = bake_config(
        tmp_path / "hero", sets=["detail.fractal_gain=2.0", "profile.corridor.voxel_m=4"]
    )
    assert two["detail.fractal_gain"] == 2.0 and two["profile.corridor.voxel_m"] == 4.0
    with pytest.raises(SystemExit, match="bake 에서는"):
        bake_config(tmp_path / "hero", sets=["landscape.theta=0.5"])
    with pytest.raises(SystemExit, match="detail.fractal_gai"):
        bake_config(tmp_path / "hero", sets=["detail.fractal_gai=2.0"])
