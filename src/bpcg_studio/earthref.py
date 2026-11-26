"""진짜 지구 기준값 만들기: data/ 의 지구 자료 → configs/reference/earth.json (작은 통계 파일).

스튜디오의 '진짜 지구와 비교' 탭은 생성 결과를 이 파일의 지구 값과 나란히 놓습니다.
원자료(약 4.5 GB)는 git 에 올리지 않습니다. 그래서 원자료를 한 번 읽어 분포·곡선·표만 남긴
이 파일을 저장소에 둡니다. 자료를 받지 않은 팀원도 비교 탭을 쓸 수 있습니다.

    uv run bpcg earth-ref                 # 있는 자료를 모두 읽어 기준 파일을 새로 씀
    uv run bpcg earth-ref --only etopo    # 한 자료만 다시 읽고 나머지는 지난 파일 값을 둠

읽는 자료와 재는 값 (자세한 뜻은 docs/studio.md '진짜 지구와 비교'):

| 자료 | 크기 | 재는 값 |
|---|---|---|
| ETOPO 2022 (고도) | 행성 | 바다 비율, 대륙붕 몫, 육지·바다 높이 분포, 경사, 칸 안 기복 |
| CHELSA bio12 (연강수) | 행성 | 육지 강수 분포, 위도 띠별 강수 |
| Global Aridity Index v3.1 | 행성 | 건조 지수 구분별 넓이, 잠재 증발산 분포 |
| RiverATLAS v1.0 (하천) | 행성 | 강 갈래 비율, 바다로 못 나가는 땅, 유량·강 경사와 상류 넓이 |
| OCTOPUS v2 (10Be 침식률) | 유역 | 같은 침식률에서 유역 경사 |
| Copernicus GLO-90 (90 m 고도) | 유역 | 파일럿 유역 40곳의 경사·기복·유역 모양 |
| AfriSAR (가봉 레이더) | 유역 | 숲 저지대 두 곳의 경사·기복·유역 모양, 숲 높이와 물길 위 높이 |

rasterio·xarray·geopandas(`data` 패키지 묶음)가 필요합니다: `uv sync --inexact --group data`.
자료 파일이 없으면 그 자료만 건너뛰고, 기준 파일의 `missing` 에 이유를 적습니다.
"""

from __future__ import annotations

import argparse
import csv
import datetime as dt
import json
import math
import subprocess
import sys
import time
from collections.abc import Callable
from pathlib import Path
from typing import Any

import numpy as np

from bpcg_studio import earthstats as es
from bpcg_studio.paths import CONFIGS, DATA, DERIVED, PILOT, ROOT

FORMAT = "bpcg-earth-reference"
FORMAT_VERSION = 1
REFERENCE = CONFIGS / "reference" / "earth.json"
R_EARTH_M = 6_371_000.0
ETOPO_PX_M = 2.0 * math.pi * R_EARTH_M / (360 * 60)  # 60″ 한 칸의 남북 길이 [m]
STEPS = ("etopo", "chelsa", "aridity", "riveratlas", "glo90", "octopus", "afrisar")

ETOPO = PILOT / "etopo" / "ETOPO_2022_v1_60s_N90W180_surface.nc"
CHELSA = PILOT / "chelsa" / "CHELSA_bio12_1981-2010_V.2.1.tif"
ARIDITY = PILOT / "aridity" / "Global-AI_ET0__annual_v3_1.zip"
ARIDITY_AI = "Global-AI_ET0__annual_v3_1/ai_v31_yr.tif"
ARIDITY_ET0 = "Global-AI_ET0__annual_v3_1/et0_v31_yr.tif"
RIVERATLAS = PILOT / "riveratlas" / "RiverATLAS_Data_v10.gdb.zip"
OCTOPUS = (
    PILOT / "octopus" / "crn_int_basins.geojson",
    PILOT / "octopus" / "crn_aus_basins.geojson",
)
PILOT_BASINS = CONFIGS / "pilot_basins.csv"
GLO90 = PILOT / "glo90"
AFRISAR = DERIVED / "afrisar"
AFRISAR_VEG = DERIVED / "veg" / "lope_veg_pilot.npz"

SOURCES: dict[str, dict[str, Any]] = {
    "etopo": {
        "label": "ETOPO 2022 (60″, 육지+해저 고도)",
        "files": [ETOPO],
        "license": "퍼블릭 도메인",
        "citation": "NOAA NCEI (2022), ETOPO 2022 Global Relief Model, doi:10.25921/fd45-gt74",
    },
    "chelsa": {
        "label": "CHELSA V2.1 bio12 (1981–2010 연강수, 30″)",
        "files": [CHELSA],
        "license": "CC0 (확인 안 됨)",
        "citation": "Karger et al. (2017) Scientific Data 4:170122; "
        "Karger et al. (2021) EnviDat, doi:10.16904/envidat.228.v2.1",
    },
    "aridity": {
        "label": "Global Aridity Index·ET0 v3.1 (1970–2000, 30″)",
        "files": [ARIDITY],
        "license": "CC BY 4.0 / Readme 에는 비상업 이용 (data/README.md)",
        "citation": "Zomer, Xu, Trabucco (2022) Scientific Data 9:409",
    },
    "riveratlas": {
        "label": "HydroSHEDS RiverATLAS v1.0 (하천 구간 약 850만 개)",
        "files": [RIVERATLAS],
        "license": "CC BY 4.0, 일부 열 ODbL 1.0",
        "citation": "Linke et al. (2019) Scientific Data 6:283",
    },
    "octopus": {
        "label": "OCTOPUS v2 (10Be 유역 평균 침식률)",
        "files": list(OCTOPUS),
        "license": "CC BY-NC-SA 4.0 으로 다룸 (data/README.md '아직 정해지지 않은 라이선스')",
        "citation": "Codilean et al. (2022) Earth System Science Data 14:3695-3713, "
        "doi:10.5194/essd-14-3695-2022",
    },
    "glo90": {
        "label": "Copernicus DEM GLO-90 (90 m 고도, 파일럿 유역 40곳)",
        "files": [GLO90],
        "license": "Copernicus DEM 라이선스 (출처 문구 의무)",
        "citation": "produced using Copernicus WorldDEM™-90 © DLR e.V. 2010-2014 and "
        "© Airbus Defence and Space "
        "GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights "
        "reserved",
    },
    "afrisar": {
        "label": "AfriSAR TomoSAR (가봉 로페·라비, 30 m 지형과 숲 높이, analysis/ 결과)",
        "files": [AFRISAR, AFRISAR_VEG],
        "license": "NASA 공개 자료, 인용 요청",
        "citation": "Hawkins, Pinto, Lavalle, Hensley (2018), ORNL DAAC, doi:10.3334/ORNLDAAC/1577",
    },
}


class MissingData(Exception):
    """자료 파일이 없거나 읽을 도구가 없어 이 자료를 건너뜁니다."""


def _need(*paths: Path) -> None:
    gone = [p for p in paths if not p.exists()]
    if gone:
        rel = ", ".join(
            str(p.relative_to(DATA)) if p.is_relative_to(DATA) else str(p) for p in gone
        )
        raise MissingData(
            f"파일이 없습니다: {rel}. 'uv run python tools/download_pilot.py' 로 받습니다"
        )


def _imports(*names: str) -> None:
    import importlib

    for n in names:
        try:
            importlib.import_module(n)
        except ImportError:
            raise MissingData(
                f"파이썬 패키지 {n} 가 없습니다. 'uv sync --inexact --group data' 로 설치합니다"
            ) from None


def _lst(a: Any) -> Any:
    """JSON 에 넣을 값: 배열은 소수 6자리로 줄인 목록."""
    if isinstance(a, np.ndarray):
        return [None if not np.isfinite(x) else round(float(x), 6) for x in a.ravel()]
    if isinstance(a, list | tuple):
        return [_lst(x) for x in a]
    if isinstance(a, float | np.floating):
        return None if not math.isfinite(float(a)) else round(float(a), 6)
    if isinstance(a, np.integer):
        return int(a)
    return a


# ---------------------------------------------------------------- 격자 크기
def profile_scales() -> dict[str, dict[str, Any]]:
    """configs/profiles 의 L0 면당 칸 수마다 지구 자료를 모을 칸 크기.

    지구 자료는 60″(약 1.85 km) 칸 k 개를 한 칸으로 평균합니다. k 는 L0 칸 크기에 가장 가까운,
    지구 둘레를 나누어떨어지게 하는 수입니다.
    """
    import tomllib

    out: dict[str, dict[str, Any]] = {}
    for p in sorted((CONFIGS / "profiles").glob("*.toml")):
        cfg = tomllib.loads(p.read_text(encoding="utf-8"))
        n = int(cfg["grid"]["l0_n_per_face"])
        spacing = es.l0_spacing_m(n, R_EARTH_M)
        k = es.nearest_divisor(spacing / ETOPO_PX_M)
        entry = out.setdefault(str(n), {"profiles": [], "l0_spacing_m": spacing, "block_px": k})
        entry["profiles"].append(p.stem)
        entry["ref_spacing_m"] = k * ETOPO_PX_M
    return out


def _lat_centers(k: int) -> np.ndarray:
    """북쪽이 0번 행인 k×60″ 칸 격자의 행 가운데 위도 [°]."""
    h = 10_800 // k
    return 90.0 - (np.arange(h) + 0.5) * k / 60.0


def _block_nanmean(a: np.ndarray, f: int) -> np.ndarray:
    if f == 1:
        return a
    h, w = a.shape[0] // f, a.shape[1] // f
    with np.errstate(invalid="ignore"), _quiet():
        return np.nanmean(a[: h * f, : w * f].reshape(h, f, w, f), axis=(1, 3))


class _quiet:
    def __enter__(self):
        import warnings

        self._w = warnings.catch_warnings()
        self._w.__enter__()
        warnings.simplefilter("ignore", RuntimeWarning)

    def __exit__(self, *a):
        self._w.__exit__(*a)


# ---------------------------------------------------------------- ETOPO
def step_etopo(scales: dict, ctx: dict, log: Callable[[str], None]) -> dict:
    _need(ETOPO)
    _imports("xarray", "netCDF4")
    import xarray as xr

    t0 = time.perf_counter()
    with xr.open_dataset(ETOPO) as ds:
        z = np.asarray(ds["z"].values, dtype=np.float32)[::-1]  # 0번 행이 북쪽
    lat_px = 90.0 - (np.arange(z.shape[0]) + 0.5) / 60.0
    w_px = np.cos(np.radians(lat_px))
    ocean_w = (np.where(z < 0, 1.0, 0.0) * w_px[:, None]).sum()
    shelf_w = (np.where((z < 0) & (z >= -200.0), 1.0, 0.0) * w_px[:, None]).sum()
    total_w = w_px.sum() * z.shape[1]
    out: dict[str, Any] = {
        "ocean_fraction": float(ocean_w / total_w),
        "shelf_fraction_of_ocean": float(shelf_w / ocean_w),
        "scales": {},
    }
    log(
        f"[지구 기준] ETOPO 읽음: 바다 {out['ocean_fraction']:.3f}, 대륙붕(200 m 보다 얕은 바다) "
        f"몫 "
        f"{out['shelf_fraction_of_ocean']:.3f}, {time.perf_counter() - t0:.1f} s"
    )
    ctx["etopo"] = {}
    for key, sc in scales.items():
        k = sc["block_px"]
        h, w = z.shape[0] // k, z.shape[1] // k
        zb = np.empty((h, w))
        rb = np.empty((h, w))
        for i in range(h):
            strip = z[i * k : (i + 1) * k].reshape(k, w, k)
            zb[i] = strip.mean(axis=(0, 2), dtype=np.float64)
            rb[i] = strip.max(axis=(0, 2)) - strip.min(axis=(0, 2))
        lat = _lat_centers(k)
        wb = np.cos(np.radians(lat))[:, None] * np.ones((1, w))
        land = zb > 0
        band = np.abs(lat)[:, None] <= es.LAT_LIMIT_DEG
        lb = land & band
        slope = es.latlon_steepest_slope(zb, lat, k / 60.0, R_EARTH_M)
        logs = np.log10(np.maximum(slope, 1e-6))
        out["scales"][key] = {
            "hyps_land": _lst(es.hist(zb[lb], es.ELEV_EDGES, wb[lb])),
            "hyps_ocean": _lst(es.hist(zb[~land], es.ELEV_EDGES, wb[~land])),
            "slope_land": _lst(es.hist(logs[lb], es.LOG_SLOPE_EDGES, wb[lb])),
            "relief_land": _lst(es.hist(rb[lb], es.RELIEF_EDGES, wb[lb])),
            "n_land_blocks": int(lb.sum()),
        }
        ctx["etopo"][key] = {"land_band": lb, "lat": lat, "w": wb, "k": k}
        log(
            f"[지구 기준] ETOPO {k}×60″ 칸 ({sc['ref_spacing_m'] / 1000:.1f} km): 남위 60°~북위 "
            f"60° 육지 칸 "
            f"{int(lb.sum()):,}개"
        )
    return out


# ---------------------------------------------------------------- 래스터 → 칸 평균
def _warp_mean(path: str, k: int, src_nodata: float, scale: float) -> np.ndarray:
    """래스터를 북쪽이 0번 행인 k×60″ 전 지구 격자로 평균해 읽습니다 (값 없음은 NaN)."""
    import rasterio
    from rasterio.enums import Resampling
    from rasterio.transform import Affine
    from rasterio.vrt import WarpedVRT

    res = k / 60.0
    h, w = 10_800 // k, 21_600 // k
    with (
        rasterio.open(path) as src,
        WarpedVRT(
            src,
            crs="EPSG:4326",
            transform=Affine(res, 0.0, -180.0, 0.0, -res, 90.0),
            width=w,
            height=h,
            resampling=Resampling.average,
            src_nodata=src_nodata,
            nodata=np.nan,
            dtype="float32",
        ) as vrt,
    ):
        a = vrt.read(1).astype(np.float64)
    return a * scale


def _base_k(scales: dict) -> int:
    return min(sc["block_px"] for sc in scales.values())


def _per_scale(base: np.ndarray, base_k: int, scales: dict):
    for key, sc in scales.items():
        k = sc["block_px"]
        if k % base_k:
            raise ValueError(f"칸 크기 {k} 가 가장 작은 칸 크기 {base_k} 의 배수가 아닙니다")
        yield key, _block_nanmean(base, k // base_k)


def step_chelsa(scales: dict, ctx: dict, log: Callable[[str], None]) -> dict:
    _need(CHELSA)
    _imports("rasterio")
    if "etopo" not in ctx:
        raise MissingData(
            "육지를 구분하는 ETOPO 를 먼저 읽어야 합니다 (--only 에 etopo 도 넣으세요)"
        )
    t0 = time.perf_counter()
    bk = _base_k(scales)
    p = _warp_mean(str(CHELSA), bk, 65535.0, 1e-3)  # mm/yr → m/yr
    out: dict[str, Any] = {"scales": {}}
    for key, pk in _per_scale(p, bk, scales):
        e = ctx["etopo"][key]
        m = e["land_band"] & np.isfinite(pk)
        logp = np.log10(np.maximum(pk, 10 ** es.LOG_P_EDGES[0]))
        zonal = []
        lat2 = e["lat"][:, None] * np.ones((1, pk.shape[1]))
        for a, b in zip(es.LAT_BAND_EDGES[:-1], es.LAT_BAND_EDGES[1:], strict=True):
            mm = m & (lat2 >= a) & (lat2 < b)
            zonal.append(
                float((pk[mm] * e["w"][mm]).sum() / e["w"][mm].sum()) if mm.any() else None
            )
        out["scales"][key] = {
            "precip_land": _lst(es.hist(logp[m], es.LOG_P_EDGES, e["w"][m])),
            "precip_zonal": _lst(zonal),
        }
    log(f"[지구 기준] CHELSA 강수: {time.perf_counter() - t0:.1f} s")
    return out


def step_aridity(scales: dict, ctx: dict, log: Callable[[str], None]) -> dict:
    _need(ARIDITY)
    _imports("rasterio")
    if "etopo" not in ctx:
        raise MissingData(
            "육지를 구분하는 ETOPO 를 먼저 읽어야 합니다 (--only 에 etopo 도 넣으세요)"
        )
    t0 = time.perf_counter()
    bk = _base_k(scales)
    ai = _warp_mean(f"zip://{ARIDITY}!{ARIDITY_AI}", bk, 0.0, 1e-4)  # 저장 값 × 0.0001
    # AI 는 바다를 0 으로, ET0 는 바다를 값 없음(65535)으로 둡니다.
    et0 = _warp_mean(f"zip://{ARIDITY}!{ARIDITY_ET0}", bk, 65535.0, 1e-3)  # mm/yr → m/yr
    out: dict[str, Any] = {"scales": {}}
    for (key, aik), (_, etk) in zip(
        _per_scale(ai, bk, scales), _per_scale(et0, bk, scales), strict=True
    ):
        e = ctx["etopo"][key]
        m = e["land_band"] & np.isfinite(aik)
        n = e["land_band"] & np.isfinite(etk)
        out["scales"][key] = {
            "ai_classes": _lst(es.class_fractions(aik[m], es.AI_CLASS_EDGES, e["w"][m])),
            "pet_land": _lst(es.hist(etk[n], es.PET_EDGES, e["w"][n])),
        }
    log(f"[지구 기준] 건조 지수·잠재 증발산: {time.perf_counter() - t0:.1f} s")
    return out


# ---------------------------------------------------------------- RiverATLAS
RIVER_COLUMNS = ["HYRIV_ID", "NEXT_DOWN", "UPLAND_SKM", "ENDORHEIC", "dis_m3_pyr", "sgr_dk_rav"]


def step_riveratlas(scales: dict, ctx: dict, log: Callable[[str], None]) -> dict:
    _need(RIVERATLAS)
    _imports("pyogrio")
    import pyogrio

    t0 = time.perf_counter()
    src = f"/vsizip/{RIVERATLAS}/RiverATLAS_v10.gdb"
    layer = pyogrio.list_layers(src)[0][0]
    df = pyogrio.read_dataframe(src, layer=layer, columns=RIVER_COLUMNS, read_geometry=False)
    log(f"[지구 기준] RiverATLAS 하천 구간 {len(df):,}개를 읽음: {time.perf_counter() - t0:.1f} s")
    ids = df["HYRIV_ID"].to_numpy(np.int64)
    nxt = df["NEXT_DOWN"].to_numpy(np.int64)
    area = df["UPLAND_SKM"].to_numpy(np.float64)
    endo = df["ENDORHEIC"].to_numpy(np.int64) == 1
    q = df["dis_m3_pyr"].to_numpy(np.float64)
    # 강 경사는 dm/km 정수라 0 은 '0.1 m/km(1e-4 m/m)보다 완만함'입니다. 버리면 큰 강의 가운데값이
    # 커지므로 반 칸(0.5e-4 m/m)으로 둡니다. 음수(약 14만 구간)는 잰 값의 오류로 보고 뺍니다.
    s_raw = df["sgr_dk_rav"].to_numpy(np.float64)
    s = np.where(s_raw == 0, 0.5, s_raw) * 1e-4  # dm/km → m/m
    pos = np.searchsorted(ids, nxt) if np.all(np.diff(ids) > 0) else None
    if pos is None:
        order = np.argsort(ids)
        ids_sorted = ids[order]
        p = np.searchsorted(ids_sorted, nxt)
        p = np.clip(p, 0, len(ids) - 1)
        rcv = np.where(ids_sorted[p] == nxt, order[p], -1)
    else:
        pos = np.clip(pos, 0, len(ids) - 1)
        rcv = np.where(ids[pos] == nxt, pos, -1)
    terminal = nxt == 0
    out: dict[str, Any] = {
        "endorheic_fraction": float(area[terminal & endo].sum() / area[terminal].sum()),
        "n_reaches": int(len(df)),
        "scales": {},
    }
    loga = np.log10(np.maximum(area, 1e-9))
    big = area >= 10 ** es.LOG_AREA_EDGES[0]
    qmed = es.binned_median(loga[big & (q > 0)], np.log10(q[big & (q > 0)]), es.LOG_AREA_EDGES)
    smed = es.binned_median(loga[big & (s > 0)], np.log10(s[big & (s > 0)]), es.LOG_AREA_EDGES)
    out["discharge_by_area"] = _lst([None if v is None else 10**v for v in qmed])
    out["slope_by_area"] = _lst([None if v is None else 10**v for v in smed])
    for key in scales:
        a_min = es.STRAHLER_MIN_CELLS * es.l0_cell_area_m2(int(key), R_EARTH_M) / 1e6
        keep = area >= a_min
        counts = es.strahler_streams(rcv, keep, area)
        out["scales"][key] = {
            "strahler_min_area_km2": a_min,
            "stream_counts": {str(k): v for k, v in counts.items()},
            "bifurcation_ratio": es.bifurcation_ratio(counts),
        }
        log(
            f"[지구 기준] RiverATLAS 강 갈래 (상류 {a_min:,.0f} km² 이상): 차수별 물줄기 수 "
            f"{counts}, "
            f"갈래 비율 {out['scales'][key]['bifurcation_ratio']}"
        )
    log(
        f"[지구 기준] RiverATLAS: 바다로 못 나가는 땅의 몫 {out['endorheic_fraction']:.3f}, "
        f"{time.perf_counter() - t0:.1f} s"
    )
    return out


# ---------------------------------------------------------------- OCTOPUS·GLO-90
def _octopus_frame(log: Callable[[str], None]):
    """OCTOPUS 국제·호주 유역 (침식률·경사·도형). 결측값(-9999.99)은 NaN."""
    _imports("geopandas", "pyogrio")
    import geopandas as gpd
    import pandas as pd

    have = [p for p in OCTOPUS if p.exists()]
    if not have:
        _need(*OCTOPUS)
    frames = []
    for p in have:
        g = gpd.read_file(p, engine="pyogrio")
        g["layer"] = p.stem
        frames.append(g)
    g = pd.concat(frames, ignore_index=True)
    for c in ("EBE_MMKYR", "AREA", "SLP_AVE", "ELEV_AVE"):
        if c in g:
            g[c] = g[c].where(g[c] > -9999.0)
    if "GLA_PCNT" in g:
        g["GLA_PCNT"] = g["GLA_PCNT"].where(g["GLA_PCNT"] > -99.0)
    missing = [p.name for p in OCTOPUS if not p.exists()]
    if missing:
        log(f"[지구 기준] OCTOPUS: {', '.join(missing)} 가 없어 있는 층만 씁니다")
    return g, missing


def _pilot_rows() -> list[dict]:
    with PILOT_BASINS.open(encoding="utf-8") as f:
        return list(csv.DictReader(f))


def step_glo90(scales: dict, ctx: dict, log: Callable[[str], None]) -> dict:
    _need(GLO90, PILOT_BASINS)
    _imports("rasterio", "geopandas")
    import rasterio
    from rasterio.features import geometry_mask
    from rasterio.merge import merge

    t0 = time.perf_counter()
    g, missing = _octopus_frame(log)
    by_id = {str(r["OBSID1"]): r for _, r in g.iterrows()}
    rows = []
    slope_h = []
    r1_h = []
    r5_h = []
    for prow in _pilot_rows():
        oid = prow["OBSID1"]
        poly = by_id.get(oid)
        tiles = [GLO90 / f"{t}.tif" for t in prow["tiles"].split(";") if t]
        if poly is None or not all(t.exists() for t in tiles):
            continue
        geom = poly.geometry
        if not geom.is_valid:
            from shapely import make_valid

            geom = make_valid(geom)
        srcs = [rasterio.open(t) for t in tiles]
        try:
            minx, miny, maxx, maxy = geom.bounds
            pad = 0.02
            arr, tr = merge(
                srcs,
                bounds=(minx - pad, miny - pad, maxx + pad, maxy + pad),
                nodata=np.nan,
                dtype="float32",
            )
        finally:
            for s in srcs:
                s.close()
        z = arr[0].astype(np.float64)
        inside = ~geometry_mask([geom.__geo_interface__], out_shape=z.shape, transform=tr)
        z = np.where(inside & (z > 0), z, np.nan)
        lat_c = 0.5 * (miny + maxy)
        dy = abs(tr.e) * math.pi / 180.0 * R_EARTH_M
        dx = abs(tr.a) * math.pi / 180.0 * R_EARTH_M * math.cos(math.radians(lat_c))
        slope = es.gradient_slope(z, dx, dy)
        n_ok = int(np.isfinite(z).sum())
        if n_ok < 100:
            continue
        slope_h.append(es.hist(np.degrees(np.arctan(slope)), es.HERO_SLOPE_EDGES))
        rel1 = es.window_relief(z, (max(1, round(1000 / dy)), max(1, round(1000 / dx))))
        rel5 = es.window_relief(z, (max(1, round(5000 / dy)), max(1, round(5000 / dx))))
        if np.isfinite(rel1).any():
            r1_h.append(es.hist(rel1, es.RELIEF_1KM_EDGES))
        if np.isfinite(rel5).any():
            r5_h.append(es.hist(rel5, es.RELIEF_5KM_EDGES))
        rows.append(
            {
                "id": oid,
                "country": prow["CNTRY"],
                "region": prow["REGION_INT"],
                "area_km2": n_ok * dx * dy / 1e6,
                "hi": es.hypsometric_integral(z),
                "relief_m": float(np.nanmax(z) - np.nanmin(z)),
                "mean_slope": float(np.nanmean(slope)),
                "slp_ave_octopus": float(poly["SLP_AVE"])
                if poly["SLP_AVE"] == poly["SLP_AVE"]
                else None,
                "e_mm_kyr": float(prow["EBE_mm_per_kyr"]),
            }
        )
    if not rows:
        raise MissingData("GLO-90 타일과 OCTOPUS 유역 도형이 맞는 파일럿 유역이 하나도 없습니다")
    pairs = [
        (r["slp_ave_octopus"], r["mean_slope"])
        for r in rows
        if r["slp_ave_octopus"] and r["mean_slope"] > 0
    ]
    ratio = float(np.median([a / b for a, b in pairs])) if pairs else None
    ctx["slp_ave_per_mm"] = ratio
    out = {
        "basins": _lst(rows),
        "n_basins": len(rows),
        "slope_hist": _lst(np.mean(slope_h, axis=0)),
        "relief_1km_hist": _lst(np.mean(r1_h, axis=0)) if r1_h else None,
        "relief_5km_hist": _lst(np.mean(r5_h, axis=0)) if r5_h else None,
        "slp_ave_ratio": ratio,
        "octopus_missing": missing,
    }
    log(
        f"[지구 기준] GLO-90 파일럿 유역 {len(rows)}곳: OCTOPUS SLP_AVE ÷ GLO-90 평균 경사(m/m) "
        f"가운데값 "
        f"{ratio if ratio is None else round(ratio, 1)}, {time.perf_counter() - t0:.1f} s"
    )
    return out


def step_octopus(scales: dict, ctx: dict, log: Callable[[str], None]) -> dict:
    t0 = time.perf_counter()
    g, missing = _octopus_frame(log)
    ratio = ctx.get("slp_ave_per_mm")
    # SLP_AVE 단위: GLO-90 으로 잰 파일럿 유역 평균 경사(m/m)와 비교해 정합니다. 1000 근처면 m/km.
    if ratio is None:
        per = 1000.0
        unit_note = "GLO-90 비교를 하지 못해 SLP_AVE 를 m/km 로 보았습니다 (확인 안 됨)"
    elif 500.0 <= ratio <= 2000.0:
        # 1000 근처라 SLP_AVE 는 m/km 입니다. 다만 이 비교의 잣대(약 90 m 칸 기울기)로 잰 값과
        # 맞추려고 1000 대신
        # 파일럿 유역에서 잰 배수로 나눕니다. 생성 쪽 경사도 같은 잣대(100 m 칸 기울기)로 잽니다.
        per = ratio
        unit_note = (
            f"OCTOPUS 의 평균 경사(SLP_AVE)는 m/km 로 보입니다. 파일럿 유역 40곳에서 SLP_AVE 가 "
            f"GLO-90 90 m 칸 기울기(m/m)의 {ratio:.0f}배라, "
            f"같은 잣대로 맞추려고 {ratio:.0f} 으로 나눕니다."
        )
    else:
        per = ratio
        unit_note = (
            f"파일럿 유역에서 SLP_AVE ÷ GLO-90 평균 경사(m/m) 가운데값 {ratio:.1f} 로 나눠 "
            "m/m 로 바꿉니다"
        )
    e = g["EBE_MMKYR"].to_numpy(np.float64)
    s = g["SLP_AVE"].to_numpy(np.float64) / per
    ok = np.isfinite(e) & (e > 0) & np.isfinite(s) & (s > 0)
    if "GLA_PCNT" in g:
        gla = g["GLA_PCNT"].to_numpy(np.float64)
        ok &= ~(np.isfinite(gla) & (gla > 0))
    counts = es.hist2d_counts(np.log10(e[ok]), s[ok], es.LOG_E_EDGES, es.BASIN_SLOPE_EDGES)
    out = {
        "n_basins": int(ok.sum()),
        "e_slope_counts": [[int(v) for v in row] for row in counts],
        "e_hist": _lst(es.hist(np.log10(e[ok]), es.LOG_E_EDGES)),
        "slope_unit_note": unit_note,
        "missing_layers": missing,
    }
    log(
        f"[지구 기준] OCTOPUS 유역 {int(ok.sum()):,}곳 (빙하 있는 유역·결측 뺌): {unit_note}, "
        f"{time.perf_counter() - t0:.1f} s"
    )
    return out


# ---------------------------------------------------------------- AfriSAR
def step_afrisar(scales: dict, ctx: dict, log: Callable[[str], None]) -> dict:
    t0 = time.perf_counter()
    sites = {}
    for site, label in (("lope", "로페"), ("rabi", "라비")):
        dem = AFRISAR / f"{site}_dem_30m.npz"
        ref = AFRISAR / f"{site}_ch4_reference.npz"
        if not dem.exists():
            continue
        with np.load(dem) as d:
            z = np.where(d["z"] > -1000, d["z"], np.nan).astype(np.float64)
            dx = float(d["dx"])
        f = max(1, round(90.0 / dx))
        zb = es.block_mean(z, f)
        sp = dx * f
        slope = es.gradient_slope(zb, sp, sp)
        rel1 = es.window_relief(zb, (round(1000 / sp),) * 2)
        rel5 = es.window_relief(zb, (round(5000 / sp),) * 2)
        entry = {
            "label": label,
            "spacing_m": sp,
            "slope_hist": _lst(es.hist(np.degrees(np.arctan(slope)), es.HERO_SLOPE_EDGES)),
            "relief_1km_hist": _lst(es.hist(rel1, es.RELIEF_1KM_EDGES)),
            "relief_5km_hist": _lst(es.hist(rel5, es.RELIEF_5KM_EDGES)),
        }
        if ref.exists():
            with np.load(ref) as r:
                entry["basin_hi"] = _lst(np.asarray(r["basin_HI"], dtype=np.float64))
                entry["basin_area_km2"] = _lst(np.asarray(r["basin_area_km2"], dtype=np.float64))
        sites[site] = entry
    if not sites:
        _need(AFRISAR / "lope_dem_30m.npz")
    out: dict[str, Any] = {"sites": sites}
    if AFRISAR_VEG.exists():
        from scipy.stats import spearmanr

        with np.load(AFRISAR_VEG) as v:
            ch = np.asarray(v["CH"], dtype=np.float64).ravel()
            hand = np.asarray(v["HAND"], dtype=np.float64).ravel()
        ok = np.isfinite(ch) & np.isfinite(hand)
        rho = spearmanr(ch[ok][::7], hand[ok][::7]).statistic
        out["canopy_hand_rho"] = round(float(rho), 4)
        out["canopy_n"] = int(ok.sum())
    log(
        f"[지구 기준] AfriSAR: 자리 {len(sites)}곳, 숲 높이–물길 위 높이 순위 상관 "
        f"{out.get('canopy_hand_rho')}, "
        f"{time.perf_counter() - t0:.1f} s"
    )
    return out


STEP_FUNCS = {
    "etopo": step_etopo,
    "chelsa": step_chelsa,
    "aridity": step_aridity,
    "riveratlas": step_riveratlas,
    "glo90": step_glo90,
    "octopus": step_octopus,
    "afrisar": step_afrisar,
}


# ---------------------------------------------------------------- 묶기
def _manifest_hashes() -> dict[str, dict]:
    p = DATA / "manifest.json"
    if not p.exists():
        p = ROOT / "data" / "manifest.json"
    try:
        man = json.loads(p.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    return {
        f["path"]: {"bytes": f.get("bytes"), "sha256": f.get("sha256")}
        for f in man.get("files", [])
    }


def _git_commit() -> str | None:
    try:
        return subprocess.run(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True
        ).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return None


def load_existing(path: Path = REFERENCE) -> dict | None:
    try:
        ref = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    return ref if ref.get("format") == FORMAT else None


def build(
    out_path: Path = REFERENCE, only: list[str] | None = None, log: Callable[[str], None] = print
) -> dict:
    """지구 기준 파일을 만듭니다.

    only 를 주면 그 자료만 다시 읽고, 나머지는 지난 파일의 값을 그대로 둡니다.
    """
    steps = list(STEPS) if not only else [s for s in STEPS if s in only]
    bad = [s for s in (only or []) if s not in STEPS]
    if bad:
        raise ValueError(
            f"모르는 자료 이름입니다: {', '.join(bad)}. 쓸 수 있는 이름: {', '.join(STEPS)}"
        )
    # 강수·건조 지수는 ETOPO 의 육지 칸을 쓰므로 ETOPO 를 함께 읽습니다.
    if any(s in steps for s in ("chelsa", "aridity")) and "etopo" not in steps:
        steps.insert(0, "etopo")
    # OCTOPUS 경사 단위는 GLO-90 으로 확인하므로 GLO-90 을 먼저 읽습니다.
    if "octopus" in steps and "glo90" not in steps:
        steps.insert(steps.index("octopus"), "glo90")
    old = load_existing(out_path) if only else None
    scales = profile_scales()
    if old is not None and set(old.get("scales") or {}) != set(scales):
        add = [s for s in ("etopo", "chelsa", "aridity", "riveratlas") if s not in steps]
        if add:
            log(
                f"[지구 기준] 프로필의 L0 면당 칸 수가 지난 파일과 달라, 칸 크기를 쓰는 자료 "
                f"{', '.join(add)} 도 다시 읽습니다"
            )
            steps = [s for s in STEPS if s in steps or s in add]
    hashes = _manifest_hashes()
    ref: dict[str, Any] = {
        "format": FORMAT,
        "format_version": FORMAT_VERSION,
        "built": dt.date.today().isoformat(),
        "git_commit": _git_commit(),
        "radius_m": R_EARTH_M,
        "scales": {
            k: _lst(v) if not isinstance(v, dict) else {kk: _lst(vv) for kk, vv in v.items()}
            for k, v in scales.items()
        },
        "edges": {
            "elev_m": _lst(es.ELEV_EDGES),
            "log10_slope": _lst(es.LOG_SLOPE_EDGES),
            "relief_m": _lst(es.RELIEF_EDGES),
            "log10_precip_m_per_yr": _lst(es.LOG_P_EDGES),
            "pet_m_per_yr": _lst(es.PET_EDGES),
            "ai_classes": [0.0, 0.03, 0.2, 0.5, 0.65, None],
            "lat_bands_deg": _lst(es.LAT_BAND_EDGES),
            "log10_area_km2": _lst(es.LOG_AREA_EDGES),
            "hero_slope_deg": _lst(es.HERO_SLOPE_EDGES),
            "relief_1km_m": _lst(es.RELIEF_1KM_EDGES),
            "relief_5km_m": _lst(es.RELIEF_5KM_EDGES),
            "log10_e_mm_per_kyr": _lst(es.LOG_E_EDGES),
            "basin_slope_m_per_m": _lst(es.BASIN_SLOPE_EDGES),
        },
        "sources": {},
        "data": dict((old or {}).get("data", {})),
        "missing": dict((old or {}).get("missing", {})),
    }
    for key in STEPS:
        src = SOURCES[key]
        files = []
        for f in src["files"]:
            rel = str(f.relative_to(DATA)) if f.is_relative_to(DATA) else str(f)
            files.append({"path": rel, **hashes.get(rel, {})})
        ref["sources"][key] = {
            "label": src["label"],
            "files": files,
            "license": src["license"],
            "citation": src["citation"],
        }
    ctx: dict[str, Any] = {}
    t_all = time.perf_counter()
    for key in steps:
        try:
            ref["data"][key] = STEP_FUNCS[key](scales, ctx, log)
            ref["missing"].pop(key, None)
        except MissingData as e:
            ref["data"].pop(key, None)
            ref["missing"][key] = str(e)
            log(f"[지구 기준] {SOURCES[key]['label']} 건너뜀: {e}")
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp = out_path.with_name(out_path.name + ".part")
    tmp.write_text(
        json.dumps(ref, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n",
        encoding="utf-8",
    )
    tmp.replace(out_path)
    have = [k for k in STEPS if k in ref["data"]]
    log(
        f"[지구 기준] 끝: 자료 {len(have)}개 ({', '.join(have) or '없음'}), 빠진 자료 "
        f"{', '.join(ref['missing']) or '없음'}, {out_path.stat().st_size / 1e3:,.0f} kB, "
        f"{time.perf_counter() - t_all:.1f} s → {out_path}"
    )
    return ref


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(
        prog="bpcg earth-ref",
        description=(
            "data/ 의 진짜 지구 자료를 읽어 스튜디오 '진짜 지구와 비교' 탭의 기준 파일을 만듭니다"
        ),
    )
    p.add_argument("--only", default="", help=f"다시 읽을 자료 (쉼표로): {', '.join(STEPS)}")
    p.add_argument(
        "--out", type=Path, default=REFERENCE, help=f"쓸 파일 (기본 {REFERENCE.relative_to(ROOT)})"
    )
    args = p.parse_args(argv)
    only = [s.strip() for s in args.only.split(",") if s.strip()] or None
    try:
        build(args.out, only)
    except ValueError as e:
        print(e, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
