"""실행 하나를 진짜 지구와 비교하는 표 (스튜디오 '진짜 지구와 비교' 탭, GET /api/earth).

지구 값은 `configs/reference/earth.json`(만드는 코드 `earthref.py`)에서 읽고, 생성 값은 실행 폴더의
행성·히어로 묶음에서 같은 함수(`earthstats.py`)와 같은 구간으로 잽니다.

지표마다 종류를 붙입니다(점수표와 같은 말).
- emergent: 입력에 없던 성질이 결과로 나온 값. '지구와 닮았다'는 근거로 쓰는 종류이고,
  닮은 정도 평균은 이 종류만으로 냅니다.
- forced: 입력(설정 값이나 정해 둔 식)이 거의 정하는 값. 입력을 지구에 맞게 골랐는지 보는 데만
  씁니다.
- none: 생성기가 아직 만들지 않는 성질이라 비교하지 못하는 지구 자료.
"""

from __future__ import annotations

import json
import math
import threading
from pathlib import Path
from typing import Any

import numpy as np

from bpcg_studio import earthstats as es
from bpcg_studio.bundle import read_manifest
from bpcg_studio.earthref import REFERENCE, load_existing

EARTH_VERSION = 1  # 계산이 바뀌면 올려 캐시를 버림
SECONDS_PER_YEAR = 3.15576e7  # Bpcg.Core.Constants.SecondsPerYear 와 같은 값
BUILD_HINT = "지구 자료를 받은 뒤 'uv run bpcg earth-ref' 로 기준 파일을 만듭니다 (docs/studio.md)"

KIND_TEXT = {
    "emergent": "결과로 나온 값",
    "forced": "입력이 정한 값",
    "none": "비교할 생성 값 없음",
}

CLIMATE_FORCED = (
    "생성기의 강수는 위도 띠마다 정해 둔 식(climate.p_* 설정)에 무작위 흔들림을 곱한 값입니다. "
    "그래서 지구와 닮았는지가 아니라, 기후 설정을 지구에 맞게 골랐는지를 봅니다."
)


# ---------------------------------------------------------------- 묶음 읽기
class Bundle:
    """실행 폴더 안 묶음 하나 (planet/ 또는 hero/) 의 필드와 그래프."""

    def __init__(self, path: Path):
        self.dir = Path(path)
        self.manifest = read_manifest(self.dir)
        self.files = {e["name"]: e["file"] for e in self.manifest["fields"]}
        self.graph = self.manifest["graph"]
        self._npz: dict[str, np.ndarray] | None = None

    def has(self, *names: str) -> bool:
        return all(n in self.files for n in names)

    def f(self, name: str, dtype=np.float64) -> np.ndarray:
        return np.asarray(np.load(self.dir / self.files[name], allow_pickle=False), dtype=dtype)

    def g(self, key: str) -> np.ndarray:
        if self._npz is None:
            with np.load(self.dir / self.graph["file"], allow_pickle=False) as z:
                self._npz = {k: np.asarray(z[k]) for k in z.files}
        return self._npz[key]

    def latitude_deg(self) -> np.ndarray:
        cfg = self.manifest.get("config") or {}
        axis = np.asarray((cfg.get("planet") or {}).get("axis", [0.0, 0.0, 1.0]), dtype=np.float64)
        axis = axis / np.linalg.norm(axis)
        pos = self.g("pos")
        unit = pos / np.linalg.norm(pos, axis=1, keepdims=True)
        return np.degrees(np.arcsin(np.clip(unit @ axis, -1.0, 1.0)))


# ---------------------------------------------------------------- 표 한 줄
def _row(id_: str, scale: str, source: str, kind: str, title: str, what: str, **kw: Any) -> dict:
    r = {
        "id": id_,
        "scale": scale,
        "source": source,
        "kind": kind,
        "kind_text": KIND_TEXT[kind],
        "title": title,
        "what": what,
        "status": "ok",
        "score": None,
    }
    r.update(kw)
    return r


def _skip(
    id_: str, scale: str, source: str, kind: str, title: str, what: str, status: str, reason: str
) -> dict:
    return _row(id_, scale, source, kind, title, what, status=status, reason=reason)


def _trim(edges: list, series: list[dict]) -> tuple[list, list[dict]]:
    """모든 계열이 0 인 양 끝 구간을 잘라 그림을 줄입니다."""
    arr = np.asarray([[v or 0.0 for v in s["p"]] for s in series])
    nz = np.flatnonzero(arr.sum(axis=0) > 1e-9)
    if nz.size == 0:
        return edges, series
    lo, hi = int(nz[0]), int(nz[-1]) + 1
    return edges[lo : hi + 1], [{**s, "p": s["p"][lo:hi]} for s in series]


def _hist_chart(
    edges, series: list[dict], x_label: str, x_log10: bool = False, x_shift: float = 0.0
) -> dict:
    e = [float(x) + x_shift for x in edges]
    e, ser = _trim(
        e, [{**s, "p": [None if v is None else float(v) for v in s["p"]]} for s in series]
    )
    return {"type": "hist", "edges": e, "series": ser, "x_label": x_label, "x_log10": x_log10}


def _q_text(q: list, unit: str, fmt=lambda v: f"{v:,.0f}") -> str:
    if not q or any(v is None for v in q):
        return "값 없음"
    return f"가운데값 {fmt(q[1])} {unit} (아래 10% {fmt(q[0])}, 위 10% {fmt(q[2])})".replace(
        "  ", " "
    )


def _hist_row(
    id_,
    scale,
    source,
    kind,
    title,
    what,
    edges,
    gen_p,
    earth_p,
    unit,
    x_label,
    fmt,
    how,
    note="",
    extra_series=None,
    transform=None,
    x_log10=False,
    x_shift=0.0,
    **kw,
) -> dict:
    """분포 두 개(생성, 지구)를 겹침 규칙으로 비교하는 줄.

    x_shift 는 그림의 가로 값에만 더합니다(예: log10 m/m → log10 m/km 은 +3).
    """
    tq = transform or (lambda v: v)
    gq = [None if v is None else tq(v) for v in es.hist_quantiles(edges, gen_p)]
    eq = [None if v is None else tq(v) for v in es.hist_quantiles(edges, earth_p)]
    series = [
        {"key": "gen", "label": "생성", "p": list(gen_p)},
        {"key": "earth", "label": "지구", "p": list(earth_p)},
    ]
    series += extra_series or []
    return _row(
        id_,
        scale,
        source,
        kind,
        title,
        what,
        rule="overlap",
        score=es.overlap(gen_p, earth_p),
        gen_text=_q_text(gq, unit, fmt),
        earth_text=_q_text(eq, unit, fmt),
        how=how,
        note=note,
        chart=_hist_chart(edges, series, x_label, x_log10=x_log10, x_shift=x_shift),
        **kw,
    )


def _ratio_row(
    id_, scale, source, kind, title, what, gen, earth, gen_text, earth_text, how, note="", **kw
) -> dict:
    kw.setdefault("chart", {"type": "bars", "labels": ["생성", "지구"], "values": [gen, earth]})
    return _row(
        id_,
        scale,
        source,
        kind,
        title,
        what,
        rule="ratio",
        score=es.ratio_score(gen, earth),
        gen_value=gen,
        earth_value=earth,
        gen_text=gen_text,
        earth_text=earth_text,
        how=how,
        note=note,
        **kw,
    )


# ---------------------------------------------------------------- 행성 크기
def _scale_key(ref: dict, n: int) -> tuple[str | None, str]:
    scales = ref.get("scales") or {}
    if not scales:
        return None, ""
    if str(n) in scales:
        return str(n), ""
    key = min(scales, key=lambda k: abs(math.log(int(k) / n)))
    sp = scales[key]["ref_spacing_m"] / 1000
    return (
        key,
        f"이 실행의 L0 면당 칸 수 {n} 에 맞춘 지구 값이 없어, 가장 가까운 면당 {key}칸(약 "
        f"{sp:.0f} km)의 지구 값을 씁니다.",
    )


def planet_rows(b: Bundle, ref: dict) -> list[dict]:
    rows: list[dict] = []
    d = ref.get("data") or {}
    miss = ref.get("missing") or {}
    n = int(b.graph["shape"][1])
    radius = float(b.graph.get("radius_m") or 6_371_000.0)
    key, scale_note = _scale_key(ref, n)
    area = b.g("area")
    lat = b.latitude_deg()
    ocean = b.f("is_ocean", bool)
    land = ~ocean
    band = np.abs(lat) <= es.LAT_LIMIT_DEG
    lb = land & band
    # 묶음의 높이는 해수면이 0 m 입니다 (바다 칸의 water_level_m = 0). meta.info.sea_level_m 은
    # 높이를 옮기기 전의 값입니다.
    zname = "z_mean_m" if b.has("z_mean_m") else "z_m"
    z = b.f(zname)
    km = es.l0_spacing_m(n, radius) / 1000
    sp_text = f"L0 칸(약 {km:.1f} km)" if km < 100 else f"L0 칸(약 {km:.0f} km)"
    ref_km = (ref["scales"][key]["ref_spacing_m"] / 1000) if key else None

    def why_missing(src: str) -> str:
        if d.get(src) and key:
            return (
                f"기준 파일의 {src} 값에 L0 면당 {key}칸 값이 없습니다. "
                "'uv run bpcg earth-ref' 로 기준 파일을 다시 만듭니다."
            )
        return miss.get(src) or f"기준 파일에 {src} 값이 없습니다. {BUILD_HINT}"

    def at_scale(src: str) -> dict | None:
        return ((d.get(src) or {}).get("scales") or {}).get(key) if key else None

    # 1. 바다 비율 (forced)
    et = d.get("etopo")
    ets = at_scale("etopo")
    title, what = "바다 비율", "행성 겉넓이 가운데 바다의 몫"
    if et:
        g = float(area[ocean].sum() / area.sum())
        rows.append(
            _ratio_row(
                "planet.ocean_fraction",
                "planet",
                "etopo",
                "forced",
                title,
                what,
                g,
                et["ocean_fraction"],
                f"{g:.3f} (열 칸 가운데 {g * 10:.1f}칸)",
                f"{et['ocean_fraction']:.3f} (높이 0 m 아래인 땅)",
                how="두 막대가 같을수록 닮았습니다.",
                note=(
                    "생성기는 대륙 지각 비율(설정)로 바다 비율을 거의 정합니다. 지구 값에는 "
                    "카스피해처럼 해수면보다 낮은 육지 호수도 바다로 들어갑니다."
                ),
            )
        )
    else:
        rows.append(
            _skip(
                "planet.ocean_fraction",
                "planet",
                "etopo",
                "forced",
                title,
                what,
                "no_reference",
                why_missing("etopo"),
            )
        )

    # 2·3. 높이 분포
    w = area
    for id_, title, what, kind, mask, eprop, how, note in (
        (
            "planet.hyps_land",
            "육지 높이 분포",
            "해수면 위 땅이 어느 높이에 얼마나 넓게 있는지",
            "emergent",
            lb,
            "hyps_land",
            "가로가 해수면 기준 높이, 세로가 그 높이 땅의 넓이 몫입니다. 지구는 낮은 땅(0~500 "
            "m)이 넓고 높은 산지는 좁습니다.",
            "극지방 얼음판의 윗면을 빼려고 두 쪽 모두 남위 60°~북위 60° 땅만 씁니다. 높이는 칸 "
            "평균입니다.",
        ),
        (
            "planet.hyps_ocean",
            "바다 깊이 분포",
            "바다 바닥이 어느 깊이에 얼마나 넓게 있는지",
            "forced",
            ocean,
            "hyps_ocean",
            "지구 바다는 4~5 km 깊이의 넓은 해양저와 얕은 대륙붕으로 나뉩니다.",
            "생성기의 해양저 깊이는 해양 지각의 나이로 정하는 식(판이 식으며 가라앉음)을 "
            "따릅니다. 점수표도 높이의 두 봉우리를 forced 로 봅니다.",
        ),
    ):
        if ets:
            gp = es.hist(z[mask], es.ELEV_EDGES, w[mask])
            rows.append(
                _hist_row(
                    id_,
                    "planet",
                    "etopo",
                    kind,
                    title,
                    what,
                    es.ELEV_EDGES,
                    gp,
                    ets[eprop],
                    "m",
                    "높이 [m]",
                    lambda v: f"{v:,.0f}",
                    how,
                    note=(note + " " + scale_note).strip(),
                )
            )
        else:
            rows.append(
                _skip(
                    id_, "planet", "etopo", kind, title, what, "no_reference", why_missing("etopo")
                )
            )

    # 4. 대륙붕 몫 (emergent)
    title, what = "대륙붕 넓이 몫", "바다 가운데 200 m 보다 얕은 곳(대륙붕)의 몫"
    if et:
        g = float(area[ocean & (z >= -200.0)].sum() / max(area[ocean].sum(), 1e-300))
        rows.append(
            _ratio_row(
                "planet.shelf_fraction",
                "planet",
                "etopo",
                "emergent",
                title,
                what,
                g,
                et["shelf_fraction_of_ocean"],
                f"{g:.3f}",
                f"{et['shelf_fraction_of_ocean']:.3f}",
                how="두 막대가 같을수록 닮았습니다. 지구는 바다 넓이의 약 7%가 대륙붕입니다.",
                note=(
                    "대륙 가장자리가 얼마나 천천히 깊어지는지를 봅니다. 점수표 planet.shelf_area "
                    "와 같은 성질입니다."
                ),
            )
        )
    else:
        rows.append(
            _skip(
                "planet.shelf_fraction",
                "planet",
                "etopo",
                "emergent",
                title,
                what,
                "no_reference",
                why_missing("etopo"),
            )
        )

    # 5. 경사 (emergent)
    title, what = f"{sp_text} 사이 경사", "이웃 칸 가운데 가장 가파르게 내려가는 쪽의 경사"
    if ets:
        s = es.graph_steepest_slope(z, b.g("nbr"), b.g("dist"))
        gp = es.hist(np.log10(np.maximum(s[lb], 1e-6)), es.LOG_SLOPE_EDGES, w[lb])
        rows.append(
            _hist_row(
                "planet.slope",
                "planet",
                "etopo",
                "forced",
                title,
                what,
                es.LOG_SLOPE_EDGES,
                gp,
                ets["slope_land"],
                "m/km",
                "경사 [m/km, 로그 눈금]",
                lambda v: f"{v:,.2g}",
                "가로는 1 km 를 갈 때 몇 m 내려가는지입니다(로그 눈금). 같은 칸 크기에서 잰 "
                "값끼리만 비교합니다.",
                note=(
                    "생성기의 칸 사이 경사는 강 경사 식(k_s × (유출 × 상류 넓이)^(−θ), 암석마다 "
                    "정해 둔 최대 경사 s_crit 에서 잘림)으로 정해집니다. 육지 칸의 대부분이 강 "
                    "칸이라 "
                    "분포는 이 입력 식과 상류 넓이의 퍼짐이 함께 정하므로, 입력이 정한 값으로 "
                    "둡니다. "
                    f"지구는 60″ 칸을 평균해 약 {ref_km:.0f} km 칸으로 만든 뒤 같은 방법으로 "
                    f"잽니다. 위경도 칸이라 동서 간격이 위도 60°에서 "
                    "절반으로 줄어, 지구 쪽 경사가 조금 크게 나올 수 있습니다. " + scale_note
                ).strip(),
                transform=lambda v: 1000 * 10**v,
                x_log10=True,
                x_shift=3.0,
            )
        )
    else:
        rows.append(
            _skip(
                "planet.slope",
                "planet",
                "etopo",
                "forced",
                title,
                what,
                "no_reference",
                why_missing("etopo"),
            )
        )

    # 6. 칸 안 기복 (emergent)
    title, what = "칸 안 기복", f"{sp_text} 하나 안의 가장 높은 곳과 낮은 곳의 차이"
    if ets and b.has("relief_m"):
        rel = b.f("relief_m")
        gp = es.hist(rel[lb], es.RELIEF_EDGES, w[lb])
        rows.append(
            _hist_row(
                "planet.relief",
                "planet",
                "etopo",
                "forced",
                title,
                what,
                es.RELIEF_EDGES,
                gp,
                ets["relief_land"],
                "m",
                "칸 안 기복 [m]",
                lambda v: f"{v:,.0f}",
                "지구는 대부분의 칸이 수백 m 안쪽이고, 큰 산맥만 2 km 를 넘습니다.",
                note=(
                    "생성기의 칸 안 기복(relief_m)은 칸마다 강 경사 식과 비탈 최대 경사(s_crit)를 "
                    "칸 안에서 더해 낸 식의 값이라(Relief.SubgridRelief), 입력이 정한 값입니다. "
                    "지구 "
                    "값은 칸 안 60″ 점들의 최고 − 최저입니다. 60″ 점은 이미 약 1.85 km 넓이를 "
                    "평균한 높이라 그보다 짧은 골짜기·봉우리가 빠져, 지구 값이 실제 기복보다 작게 "
                    "나옵니다. " + scale_note
                ).strip(),
            )
        )
    elif not b.has("relief_m"):
        rows.append(
            _skip(
                "planet.relief",
                "planet",
                "etopo",
                "forced",
                title,
                what,
                "no_run_data",
                "이 실행의 행성 묶음에 relief_m 필드가 없습니다.",
            )
        )
    else:
        rows.append(
            _skip(
                "planet.relief",
                "planet",
                "etopo",
                "forced",
                title,
                what,
                "no_reference",
                why_missing("etopo"),
            )
        )

    # 7·8. 강수 (forced)
    chs = at_scale("chelsa")
    if b.has("precip_m_per_yr"):
        p = b.f("precip_m_per_yr")
    else:
        p = None
    title, what = "육지 강수 분포", "1년 동안 내리는 비·눈의 양이 땅마다 어떻게 퍼져 있는지"
    if chs and p is not None:
        gp = es.hist(np.log10(np.maximum(p[lb], 10 ** es.LOG_P_EDGES[0])), es.LOG_P_EDGES, w[lb])
        rows.append(
            _hist_row(
                "planet.precip",
                "planet",
                "chelsa",
                "forced",
                title,
                what,
                es.LOG_P_EDGES,
                gp,
                chs["precip_land"],
                "m/yr",
                "연강수 [m/yr, 로그 눈금]",
                lambda v: f"{v:,.2f}",
                "서울의 연강수는 약 1.4 m 입니다. 지구 육지는 사막(0.1 m 아래)부터 열대 우림(2 m "
                "넘음)까지 넓게 퍼져 있습니다.",
                note=CLIMATE_FORCED,
                transform=lambda v: 10**v,
                x_log10=True,
            )
        )
    else:
        rows.append(
            _skip(
                "planet.precip",
                "planet",
                "chelsa",
                "forced",
                title,
                what,
                "no_run_data" if p is None else "no_reference",
                "이 실행에 precip_m_per_yr 필드가 없습니다."
                if p is None
                else why_missing("chelsa"),
            )
        )
    title, what = "위도 띠별 강수", "위도 5°마다 육지 평균 강수"
    if chs and p is not None:
        gz = []
        for a, c in zip(es.LAT_BAND_EDGES[:-1], es.LAT_BAND_EDGES[1:], strict=True):
            m = lb & (lat >= a) & (lat < c)
            gz.append(float((p[m] * w[m]).sum() / w[m].sum()) if m.sum() >= 3 else None)
        ez = chs["precip_zonal"]
        centers = [float(x) for x in (es.LAT_BAND_EDGES[:-1] + 2.5)]
        rows.append(
            _row(
                "planet.precip_zonal",
                "planet",
                "chelsa",
                "forced",
                title,
                what,
                rule="curve",
                score=es.curve_score(gz, ez),
                gen_text=(
                    f"적도 띠(±5°) {_fmt2(_mean2(gz, 11, 12))} m/yr, 남북 25° 근처 "
                    f"{_fmt2(_mean2(gz, 6, 17))} m/yr"
                ),
                earth_text=(
                    f"적도 띠(±5°) {_fmt2(_mean2(ez, 11, 12))} m/yr, 남북 25° 근처 "
                    f"{_fmt2(_mean2(ez, 6, 17))} m/yr"
                ),
                how=(
                    "가로가 위도, 세로가 그 위도 띠 육지의 평균 강수입니다. 지구는 적도에서 가장 "
                    "많고 남북 20~30°(사막 띠)에서 적습니다."
                ),
                note=CLIMATE_FORCED
                + " 점수는 같은 위도에서 두 값이 몇 배 어긋나는지의 평균으로 냅니다.",
                chart={
                    "type": "curve",
                    "x": centers,
                    "x_label": "위도 [°]",
                    "y_label": "강수 [m/yr]",
                    "series": [
                        {"key": "gen", "label": "생성", "y": gz},
                        {"key": "earth", "label": "지구", "y": ez},
                    ],
                },
            )
        )
    else:
        rows.append(
            _skip(
                "planet.precip_zonal",
                "planet",
                "chelsa",
                "forced",
                title,
                what,
                "no_run_data" if p is None else "no_reference",
                "이 실행에 precip_m_per_yr 필드가 없습니다."
                if p is None
                else why_missing("chelsa"),
            )
        )

    # 9·10. 건조 지수, 잠재 증발산 (forced)
    ar = d.get("aridity")
    ars = at_scale("aridity")
    pet = b.f("pet_m_per_yr") if b.has("pet_m_per_yr") else None
    title, what = (
        "건조한 땅의 몫",
        "강수 ÷ 잠재 증발산(땅이 젖어 있으면 증발할 물의 양)으로 나눈 다섯 구분의 넓이",
    )
    if ars and p is not None and pet is not None:
        with np.errstate(divide="ignore", invalid="ignore"):
            ai = np.where(pet > 0, p / pet, np.inf)
        gc = es.class_fractions(np.minimum(ai[lb], 1e9), es.AI_CLASS_EDGES, w[lb])
        ec = ars["ai_classes"]
        rows.append(
            _row(
                "planet.aridity",
                "planet",
                "aridity",
                "forced",
                title,
                what,
                rule="overlap",
                score=es.overlap(gc, ec),
                gen_text="건조(반습윤까지) " + f"{sum(gc[:4]):.2f}, 습윤 {gc[4]:.2f}",
                earth_text="건조(반습윤까지) " + f"{sum(ec[:4]):.2f}, 습윤 {ec[4]:.2f}",
                how=(
                    "UNEP 구분입니다: 극건조 0.03 미만, 건조 0.2 미만, 반건조 0.5 미만, 건조 "
                    "반습윤 0.65 미만, 그 위는 습윤."
                ),
                note=CLIMATE_FORCED
                + " 지구 값은 1970–2000 평균이라 강수(1981–2010)와 기간이 다릅니다.",
                chart={
                    "type": "classes",
                    "labels": list(es.AI_CLASS_NAMES),
                    "series": [
                        {"key": "gen", "label": "생성", "p": gc},
                        {"key": "earth", "label": "지구", "p": ec},
                    ],
                },
            )
        )
        gp = es.hist(pet[lb], es.PET_EDGES, w[lb])
        rows.append(
            _hist_row(
                "planet.pet",
                "planet",
                "aridity",
                "forced",
                "잠재 증발산 분포",
                "땅이 늘 젖어 있다면 1년에 증발할 물의 양",
                es.PET_EDGES,
                gp,
                ars["pet_land"],
                "m/yr",
                "잠재 증발산 [m/yr]",
                lambda v: f"{v:,.2f}",
                "더운 곳일수록 큽니다. 지구 육지는 대부분 0.5~2.5 m 입니다.",
                note="생성기의 잠재 증발산은 기온(위도와 높이로 정함)으로 정하는 식입니다.",
            )
        )
    else:
        reason = (
            why_missing("aridity")
            if not ar
            else "이 실행에 precip_m_per_yr·pet_m_per_yr 필드가 없습니다."
        )
        st = "no_reference" if not ar else "no_run_data"
        rows.append(_skip("planet.aridity", "planet", "aridity", "forced", title, what, st, reason))
        rows.append(
            _skip(
                "planet.pet",
                "planet",
                "aridity",
                "forced",
                "잠재 증발산 분포",
                "땅이 늘 젖어 있다면 1년에 증발할 물의 양",
                st,
                reason,
            )
        )

    rows += _river_rows(b, d, miss, key, land, area, n, radius)
    return rows


def _mean2(v: list, i: int, j: int) -> float | None:
    xs = [x for x in (v[i], v[j]) if x is not None]
    return float(np.mean(xs)) if xs else None


def _fmt2(v: float | None) -> str:
    return "없음" if v is None else f"{v:.2f}"


def _river_rows(
    b: Bundle, d: dict, miss: dict, key: str | None, land, area, n: int, radius: float
) -> list[dict]:
    rows: list[dict] = []
    ra = d.get("riveratlas")
    srcs = "riveratlas"
    why = miss.get(srcs) or f"기준 파일에 RiverATLAS 값이 없습니다. {BUILD_HINT}"
    need = ("receiver", "drainage_area_m2")
    titles = {
        "planet.bifurcation": (
            "강 갈래 비율",
            "강 차수가 하나 오를 때 물줄기 수가 몇 분의 1로 줄어드는지",
        ),
        "planet.endorheic": (
            "바다로 못 나가는 땅의 몫",
            "빗물이 바다에 닿지 못하고 안쪽 호수·사막에서 끝나는 땅의 넓이 몫",
        ),
        "planet.discharge_area": (
            "상류 넓이와 유량",
            "같은 상류 넓이에서 강을 지나는 물의 양(가운데값)",
        ),
        "planet.channel_slope": (
            "상류 넓이와 강 경사",
            "같은 상류 넓이에서 강바닥의 경사(가운데값)",
        ),
    }
    kinds = {
        "planet.bifurcation": "emergent",
        "planet.endorheic": "emergent",
        "planet.discharge_area": "forced",
        "planet.channel_slope": "forced",
    }
    if not ra or not b.has(*need):
        st = "no_reference" if not ra else "no_run_data"
        reason = why if not ra else "이 실행에 receiver·drainage_area_m2 필드가 없습니다."
        return [_skip(k, "planet", srcs, kinds[k], *titles[k], st, reason) for k in titles]
    rcv = b.f("receiver", np.int64)
    akm = b.f("drainage_area_m2") / 1e6
    # 강 갈래 비율
    sc = (ra.get("scales") or {}).get(key or "")
    if sc:
        a_min = sc["strahler_min_area_km2"]
        counts = es.strahler_streams(rcv, land & (akm >= a_min), akm)
        rb = es.bifurcation_ratio(counts)
        ecounts = {int(k): v for k, v in sc["stream_counts"].items()}
        rows.append(
            _ratio_row(
                "planet.bifurcation",
                "planet",
                srcs,
                "emergent",
                *titles["planet.bifurcation"],
                rb,
                sc["bifurcation_ratio"],
                _rb_text(rb, counts),
                _rb_text(sc["bifurcation_ratio"], ecounts),
                how=(
                    "샘에서 시작한 물줄기를 1차, 1차 둘이 만나면 2차처럼 세는 방법(Strahler "
                    "차수)입니다. 지구의 큰 강 그물은 보통 3~5 입니다."
                ),
                note=(
                    f"두 쪽 모두 상류 넓이 {a_min:,.0f} km²(L0 칸 {es.STRAHLER_MIN_CELLS}개) "
                    f"이상인 물길만 그물로 봅니다. "
                    "지구 쪽은 RiverATLAS 의 구간 연결(NEXT_DOWN)로 차수를 다시 셉니다. 무작위 "
                    "그물도 4 안팎이 나오는 것으로 알려져 있어, 이 값만으로는 구분력이 약합니다."
                ),
                chart={
                    "type": "orders",
                    "series": [
                        {
                            "key": "gen",
                            "label": "생성",
                            "counts": {str(k): v for k, v in counts.items()},
                        },
                        {"key": "earth", "label": "지구", "counts": sc["stream_counts"]},
                    ],
                },
            )
        )
    else:
        rows.append(
            _skip(
                "planet.bifurcation",
                "planet",
                srcs,
                "emergent",
                *titles["planet.bifurcation"],
                "no_reference",
                why,
            )
        )
    # 바다로 못 나가는 땅
    ocean = ~land
    term = es.terminal_of(rcv)
    endo = land & ~ocean[term]
    g = float(area[endo].sum() / max(area[land].sum(), 1e-300))
    e = ra["endorheic_fraction"]
    rows.append(
        _ratio_row(
            "planet.endorheic",
            "planet",
            srcs,
            "emergent",
            *titles["planet.endorheic"],
            g if g > 0 else None,
            e,
            f"{g:.3f}",
            f"{e:.3f}",
            how=(
                "지구는 육지의 약 5분의 1에서 물이 바다로 나가지 못합니다(카스피해, 사하라, "
                "오스트레일리아 안쪽 등)."
            ),
            note=(
                "생성 쪽은 물길을 따라 내려가 끝나는 칸이 바다가 아닌 땅을 셉니다. 지구 쪽은 "
                "RiverATLAS 끝 구간의 ENDORHEIC 표시와 상류 넓이로 셉니다."
            )
            + (" 생성 값이 0 이라 배수 점수를 낼 수 없습니다." if g <= 0 else ""),
        )
    )
    # 유량·강 경사와 상류 넓이 (forced)
    centers = [float(x) for x in (es.LOG_AREA_EDGES[:-1] + 0.125)]
    river = land & (b.f("is_river", bool) if b.has("is_river") else np.ones_like(land))
    loga = np.log10(np.maximum(akm, 1e-9))
    if b.has("discharge_m3_per_yr"):
        q = b.f("discharge_m3_per_yr") / SECONDS_PER_YEAR
        m = river & (q > 0)
        gq = [
            None if v is None else 10**v
            for v in es.binned_median(loga[m], np.log10(q[m]), es.LOG_AREA_EDGES)
        ]
        eq = ra["discharge_by_area"]
        rows.append(
            _row(
                "planet.discharge_area",
                "planet",
                srcs,
                "forced",
                *titles["planet.discharge_area"],
                rule="curve",
                score=es.curve_score(gq, eq),
                gen_text=_at_area(gq, centers, 4.0, "m³/s"),
                earth_text=_at_area(eq, centers, 4.0, "m³/s"),
                how=(
                    "가로가 상류 넓이(로그), 세로가 유량(로그)입니다. 넓이가 10배면 유량도 거의 "
                    "10배 늘어납니다."
                ),
                note=(
                    "생성기의 유량은 기후로 정한 유출을 상류 넓이만큼 더한 값이라, 이 곡선은 기후 "
                    "입력이 거의 정합니다."
                ),
                chart={
                    "type": "curve",
                    "x": centers,
                    "x_label": "상류 넓이 [log10 km²]",
                    "y_label": "유량 [m³/s]",
                    "y_log": True,
                    "series": [
                        {"key": "gen", "label": "생성", "y": gq},
                        {"key": "earth", "label": "지구", "y": eq},
                    ],
                },
            )
        )
    else:
        rows.append(
            _skip(
                "planet.discharge_area",
                "planet",
                srcs,
                "forced",
                *titles["planet.discharge_area"],
                "no_run_data",
                "이 실행에 discharge_m3_per_yr 필드가 없습니다.",
            )
        )
    if b.has("slope"):
        s = b.f("slope")
        m = river & (s > 0)
        gs = [
            None if v is None else 10**v
            for v in es.binned_median(loga[m], np.log10(s[m]), es.LOG_AREA_EDGES)
        ]
        esl = ra["slope_by_area"]
        rows.append(
            _row(
                "planet.channel_slope",
                "planet",
                srcs,
                "forced",
                *titles["planet.channel_slope"],
                rule="curve",
                score=es.curve_score(gs, esl),
                gen_text=_at_area(gs, centers, 4.0, "m/m", "{:.2g}"),
                earth_text=_at_area(esl, centers, 4.0, "m/m", "{:.2g}"),
                how="강은 하류로 갈수록(상류 넓이가 클수록) 완만해집니다.",
                note=(
                    "생성기는 강 경사를 '경사 = k_s × (유출 × 상류 넓이)^(−θ)' 식으로 풉니다. "
                    "그래서 곡선의 기울기(θ)는 입력 그대로이고, "
                    "높낮이는 강 깎는 세기 k_s 와 융기 속도가 정합니다. 지구 쪽 경사는 RiverATLAS "
                    "구간의 평균 경사(sgr_dk_rav)이고, "
                    "생성 쪽은 L0 칸(약 20 km) 사이 경사라 재는 길이가 다릅니다. 지구 경사는 dm/km "
                    "정수라 0(0.1 m/km 보다 완만)은 그 절반인 0.05 m/km 로 둡니다."
                ),
                chart={
                    "type": "curve",
                    "x": centers,
                    "x_label": "상류 넓이 [log10 km²]",
                    "y_label": "강 경사 [m/m]",
                    "y_log": True,
                    "series": [
                        {"key": "gen", "label": "생성", "y": gs},
                        {"key": "earth", "label": "지구", "y": esl},
                    ],
                },
            )
        )
    else:
        rows.append(
            _skip(
                "planet.channel_slope",
                "planet",
                srcs,
                "forced",
                *titles["planet.channel_slope"],
                "no_run_data",
                "이 실행에 slope 필드가 없습니다.",
            )
        )
    return rows


def _rb_text(rb: float | None, counts: dict) -> str:
    if rb is None:
        return f"차수가 셋보다 적어 셀 수 없음 (차수별 물줄기 {counts})"
    top = max(counts) if counts else 0
    return f"{rb:.2f} (가장 큰 차수 {top})"


def _at_area(vals: list, centers: list, at: float, unit: str, fmt: str = "{:,.0f}") -> str:
    """상류 넓이 log10 값 at 이 든 구간(왼쪽 끝 포함)의 값을 글로."""
    i = int(np.searchsorted(es.LOG_AREA_EDGES, at, side="right")) - 1
    i = min(max(i, 0), len(vals) - 1)
    lo, hi = 10 ** float(es.LOG_AREA_EDGES[i]), 10 ** float(es.LOG_AREA_EDGES[i + 1])
    where = f"상류 {lo:,.0f}~{hi:,.0f} km² 구간"
    return (
        f"{where}에서 {fmt.format(vals[i])} {unit}" if vals[i] is not None else f"{where} 값 없음"
    )


# ---------------------------------------------------------------- 히어로 크기
def hero_rows(b: Bundle, ref: dict) -> list[dict]:
    rows: list[dict] = []
    d = ref.get("data") or {}
    miss = ref.get("missing") or {}
    ny, nx = (int(v) for v in b.graph["shape"])
    sp0 = float(b.graph["spacing_m"])
    z = b.f("z_m").reshape(ny, nx)
    wet = np.zeros((ny, nx), dtype=bool)
    for name in ("is_ocean", "is_lake"):
        if b.has(name):
            wet |= b.f(name, bool).reshape(ny, nx)
    zl = np.where(wet, np.nan, z)
    f = max(1, round(es.HERO_SPACING_M / sp0))
    sp = sp0 * f
    zb = es.block_mean(zl, f)
    slope = es.gradient_slope(zb, sp, sp)
    gl = d.get("glo90")
    af = (d.get("afrisar") or {}).get("sites") or {}

    def why(src: str) -> str:
        return miss.get(src) or f"기준 파일에 {src} 값이 없습니다. {BUILD_HINT}"

    def af_series(prop: str) -> list[dict]:
        return [
            {"key": f"afrisar_{k}", "label": f"가봉 {v['label']} (AfriSAR)", "p": v[prop]}
            for k, v in af.items()
            if v.get(prop)
        ]

    res_note = (
        f"생성 쪽은 {sp0:g} m 칸을 {f} × {f} 로 평균한 {sp:g} m 칸, 지구 쪽은 GLO-90 의 약 90 m "
        f"칸에서 잽니다."
        if f > 1
        else f"생성 쪽은 {sp:g} m 칸, 지구 쪽은 GLO-90 의 약 90 m 칸에서 잽니다."
    )
    src_note = " 지구 쪽 분포는 파일럿 유역 40곳마다 낸 분포를 같은 무게로 평균했습니다."

    # 1. 경사 분포
    title, what = "산비탈 경사 분포", "약 100 m 칸에서 잰 땅의 기울기"
    gp = es.hist(np.degrees(np.arctan(slope)), es.HERO_SLOPE_EDGES)
    if gl:
        rows.append(
            _hist_row(
                "hero.slope",
                "hero",
                "glo90",
                "forced",
                title,
                what,
                es.HERO_SLOPE_EDGES,
                gp,
                gl["slope_hist"],
                "°",
                "경사 [°]",
                lambda v: f"{v:.1f}",
                "산지 유역은 20~35° 비탈이 넓고, 그보다 가파른 비탈은 빨리 줄어듭니다(비탈이 "
                "무너져 버틸 수 있는 경사에 한계가 있음).",
                note=(
                    res_note + src_note + " 생성기의 비탈은 암석마다 정해 둔 최대 경사"
                    "(s_crit, 흔한 값 0.8 = 38.7°)에서 잘립니다. "
                    "laptop 시드 0 히어로 유역에서는 육지 칸의 약 절반이 이 상한에 닿아 있어, "
                    "분포의 "
                    "가파른 쪽 끝은 입력이 정합니다. 그래서 입력이 정한 값으로 둡니다."
                ),
                extra_series=af_series("slope_hist"),
            )
        )
    else:
        rows.append(
            _skip(
                "hero.slope", "hero", "glo90", "forced", title, what, "no_reference", why("glo90")
            )
        )

    # 2·3. 기복
    for id_, title, size_m, edges, prop in (
        ("hero.relief_1km", "1 km 네모 안 기복", 1000, es.RELIEF_1KM_EDGES, "relief_1km_hist"),
        ("hero.relief_5km", "5 km 네모 안 기복", 5000, es.RELIEF_5KM_EDGES, "relief_5km_hist"),
    ):
        what = (
            f"땅 위 어디서나 한 변 {size_m // 1000} km 네모를 놓았을 때 가장 높은 곳 − 가장 낮은 곳"
        )
        nwin = max(1, round(size_m / sp))
        if nwin * 2 > min(zb.shape):
            rows.append(
                _skip(
                    id_,
                    "hero",
                    "glo90",
                    "emergent",
                    title,
                    what,
                    "no_run_data",
                    f"히어로 유역(한 변 {ny * sp0 / 1000:g} km)이 {size_m // 1000} km 네모 둘이 "
                    f"들어가기에 작습니다.",
                )
            )
            continue
        rel = es.window_relief(zb, (nwin, nwin))
        if gl and gl.get(prop):
            rows.append(
                _hist_row(
                    id_,
                    "hero",
                    "glo90",
                    "emergent",
                    title,
                    what,
                    edges,
                    es.hist(rel, edges),
                    gl[prop],
                    "m",
                    "기복 [m]",
                    lambda v: f"{v:,.0f}",
                    "골짜기가 얼마나 깊게 파였는지를 봅니다. 곡선이 오른쪽에 있을수록 깊은 "
                    "골짜기가 많습니다.",
                    note=res_note + " 네모 안에 바다·호수·유역 밖이 끼면 세지 않습니다." + src_note,
                    extra_series=af_series(prop),
                )
            )
        else:
            rows.append(
                _skip(id_, "hero", "glo90", "emergent", title, what, "no_reference", why("glo90"))
            )

    # 4·5. 작은 유역
    basins = _sub_basins(b, z, slope, f, ny, nx, sp0)
    title, what = "유역 모양 (고도 적분)", "작은 유역마다 (평균 높이 − 최저) ÷ (최고 − 최저)"
    ghi = [x["hi"] for x in basins if x["hi"] is not None]
    if gl and len(ghi) >= 1:
        ehi = [x["hi"] for x in gl["basins"] if x.get("hi") is not None]
        med = float(np.median(ghi))
        q = es.percentile_of(ehi, med)
        pts = [{"key": "earth", "label": "지구 파일럿 유역 (GLO-90)", "values": ehi}]
        for k, v in af.items():
            if v.get("basin_hi"):
                pts.append(
                    {
                        "key": f"afrisar_{k}",
                        "label": f"가봉 {v['label']} (AfriSAR)",
                        "values": [x for x in v["basin_hi"] if x is not None],
                    }
                )
        pts.append({"key": "gen", "label": "생성 (히어로 안 유역)", "values": ghi})
        rows.append(
            _row(
                "hero.hypsometric",
                "hero",
                "glo90",
                "emergent",
                title,
                what,
                rule="center",
                score=es.center_score(q),
                gen_text=f"가운데값 {med:.2f} (유역 {len(ghi)}곳)",
                earth_text=(
                    f"가운데값 {float(np.median(ehi)):.2f} (유역 {len(ehi)}곳, 가운데 절반 "
                    f"{np.percentile(ehi, 25):.2f}~{np.percentile(ehi, 75):.2f})"
                ),
                how=(
                    "0.5 면 평균 높이가 한가운데입니다. 낮으면 넓은 골짜기 바닥 위로 봉우리만 "
                    "솟은 유역, 높으면 높은 땅이 넓고 골짜기만 파인 유역입니다."
                ),
                note=(
                    f"생성 쪽 유역은 히어로 안에서 상류 넓이 "
                    f"{es.SUB_BASIN_KM2[0]:g}~{es.SUB_BASIN_KM2[1]:g} km² 인 가장 큰 물줄기 "
                    "유역입니다. 히어로 전체 유역은 넓이가 1,000 km² 이하일 때만 넣습니다(지구 "
                    "파일럿 유역을 고른 넓이 범위 10~1,000 km² 와 같음). "
                    "점수는 생성 가운데값이 지구 유역들 가운데 몇 번째쯤인지로 냅니다"
                    "(가운데면 100)."
                ),
                chart={
                    "type": "strip",
                    "x_label": "고도 적분",
                    "lo": 0.0,
                    "hi": 1.0,
                    "series": pts,
                },
                percentile=q,
            )
        )
    else:
        rows.append(
            _skip(
                "hero.hypsometric",
                "hero",
                "glo90",
                "emergent",
                title,
                what,
                "no_reference" if not gl else "no_run_data",
                why("glo90") if not gl else "히어로 안에 넓이 10 km² 넘는 유역이 없습니다.",
            )
        )

    oc = d.get("octopus")
    title, what = "같은 침식률에서 유역 경사", "깎이는 속도가 비슷한 유역끼리 평균 경사를 견줌"
    gb = [x for x in basins if x["e_mm_kyr"] and x["e_mm_kyr"] > 0 and x["mean_slope"] is not None]
    if oc and gb:
        counts = np.asarray(oc["e_slope_counts"], dtype=np.float64)
        scores, qs = [], []
        for x in gb:
            i = int(
                np.clip(
                    np.searchsorted(es.LOG_E_EDGES, math.log10(x["e_mm_kyr"]), side="right") - 1,
                    0,
                    counts.shape[0] - 1,
                )
            )
            lo, hi = i, i + 1
            while counts[lo:hi].sum() < 20 and (lo > 0 or hi < counts.shape[0]):
                lo, hi = max(0, lo - 1), min(counts.shape[0], hi + 1)
            qv = es.hist_percentile(
                es.BASIN_SLOPE_EDGES, counts[lo:hi].sum(axis=0), x["mean_slope"]
            )
            x["earth_percentile"] = qv
            qs.append(qv)
            scores.append(es.center_score(qv))
        ok = [s for s in scores if s is not None]
        band = _e_slope_band(counts)
        e_lo = min(x["e_mm_kyr"] for x in gb)
        e_hi = max(x["e_mm_kyr"] for x in gb)
        q_med = (
            float(np.median([q for q in qs if q is not None]))
            if any(q is not None for q in qs)
            else math.nan
        )
        rows.append(
            _row(
                "hero.erosion_slope",
                "hero",
                "octopus",
                "forced",
                title,
                what,
                rule="center",
                score=float(np.mean(ok)) if ok else None,
                gen_text=(
                    f"유역 {len(gb)}곳, 침식률 {e_lo:,.0f}~{e_hi:,.0f} mm/kyr, "
                    f"지구 같은 침식률 유역 가운데 경사 백분위 가운데값 {q_med:.2f}"
                ),
                earth_text=f"OCTOPUS 유역 {oc['n_basins']:,}곳 (빙하 있는 유역 뺌)",
                how=(
                    "가로는 1,000년에 깎이는 mm(로그), 세로는 유역 평균 경사입니다. 띠는 지구 "
                    "유역의 가운데 절반, 선은 가운데값입니다. "
                    "지구에서는 빨리 깎이는 유역일수록 가파르지만, 30° 근처에서 더 가팔라지지 "
                    "않습니다."
                ),
                note=(
                    "생성기는 다 자란 지형(정상상태)이라 침식률 = 융기 속도입니다. 그 속도로 "
                    "깎이려면 얼마나 가팔라야 하는지는 강 경사 식(k_s, θ)과 비탈 최대 "
                    "경사(s_crit)가 "
                    "정하므로, 이 줄은 그 입력 값들이 지구에 맞는지를 봅니다. 빨리 솟는 유역은 "
                    "평균 "
                    "경사가 s_crit 근처에서 멈춥니다. 점수는 유역마다 같은 침식률의 지구 유역 "
                    "가운데 몇 번째 경사인지(가운데면 100)의 평균입니다. "
                    + oc.get("slope_unit_note", "")
                ),
                chart={
                    "type": "band",
                    "x": band["x"],
                    "x_label": "침식률 [mm/kyr, 로그 눈금]",
                    "y_label": "유역 평균 경사 [°]",
                    "earth": band,
                    "points": [
                        {
                            "x": math.log10(x["e_mm_kyr"]),
                            "y": math.degrees(math.atan(x["mean_slope"])),
                        }
                        for x in gb
                    ],
                },
            )
        )
    else:
        rows.append(
            _skip(
                "hero.erosion_slope",
                "hero",
                "octopus",
                "forced",
                title,
                what,
                "no_reference" if not oc else "no_run_data",
                why("octopus") if not oc else "히어로 안에 융기 속도가 0 보다 큰 유역이 없습니다.",
            )
        )

    # 6. 숲 높이 (비교할 생성 값 없음)
    av = d.get("afrisar") or {}
    title, what = "숲 높이와 물길 위 높이", "가장 가까운 물길보다 높은 땅일수록 나무가 큰지"
    if "canopy_hand_rho" in av:
        rows.append(
            _row(
                "hero.canopy_hand",
                "hero",
                "afrisar",
                "none",
                title,
                what,
                status="no_model",
                earth_text=(
                    f"순위 상관 {av['canopy_hand_rho']:+.2f} (가봉 로페, 30 m 칸 "
                    f"{av['canopy_n']:,}개)"
                ),
                gen_text="생성기는 숲 높이를 만들지 않습니다",
                reason=(
                    "생성기에 숲 높이가 없어 비교하지 않습니다. "
                    "식생을 만들면 이 지구 값과 비교할 수 있습니다."
                ),
                how=(
                    "순위 상관은 −1~+1 입니다. +0.3 이면 물길에서 높은 곳일수록 나무가 조금 큰 "
                    "경향이 있다는 뜻입니다."
                ),
                note=(
                    "레이더 촬영 방향 때문에 생긴 몫이 섞여 있습니다(analysis/README.md 의 veg 표)."
                ),
            )
        )
    else:
        rows.append(
            _skip(
                "hero.canopy_hand",
                "hero",
                "afrisar",
                "none",
                title,
                what,
                "no_reference",
                why("afrisar"),
            )
        )
    return rows


def _e_slope_band(counts: np.ndarray) -> dict:
    x = [float(v) for v in (es.LOG_E_EDGES[:-1] + 0.125)]
    q25, q50, q75, n = [], [], [], []
    for row in counts:
        n.append(int(row.sum()))
        if row.sum() < 10:
            q25.append(None)
            q50.append(None)
            q75.append(None)
            continue
        qs = es.hist_quantiles(es.BASIN_SLOPE_EDGES, row, (0.25, 0.5, 0.75))
        q25.append(math.degrees(math.atan(qs[0])))
        q50.append(math.degrees(math.atan(qs[1])))
        q75.append(math.degrees(math.atan(qs[2])))
    return {"x": x, "q25": q25, "q50": q50, "q75": q75, "n": n}


def _sub_basins(
    b: Bundle, z: np.ndarray, slope: np.ndarray, f: int, ny: int, nx: int, sp0: float
) -> list[dict]:
    """히어로 안 작은 유역들.

    상류 넓이 10~300 km² 인 칸 가운데, 물이 그보다 큰 유역으로 넘어가는 칸을 출구로 삼은 유역입니다.
    히어로 전체 유역도 1,000 km² 이하이면 하나로 넣습니다.
    """
    if not b.has("receiver", "drainage_area_m2"):
        return []
    rcv = b.f("receiver", np.int64)
    akm = b.f("drainage_area_m2") / 1e6
    ncell = ny * nx
    lo, hi = es.SUB_BASIN_KM2
    rc = np.where((rcv >= 0) & (rcv < ncell), rcv, np.arange(ncell))
    out_mask = (akm >= lo) & (akm <= hi) & ((akm[rc] > hi) | (rc == np.arange(ncell)))
    term = es.terminal_of(rcv, stop=out_mask)
    zf = z.ravel()
    uplift = b.f("uplift_m_per_yr") if b.has("uplift_m_per_yr") else np.zeros(ncell)
    ii = np.arange(ncell) // nx // f
    jj = np.arange(ncell) % nx // f
    ok_blk = (ii < slope.shape[0]) & (jj < slope.shape[1])
    sl = np.full(ncell, np.nan)
    sl[ok_blk] = slope[ii[ok_blk], jj[ok_blk]]
    basins = []

    def one(cells: np.ndarray, label: str) -> dict:
        s = sl[cells]
        u = uplift[cells]
        return {
            "label": label,
            "area_km2": float(cells.size * sp0 * sp0 / 1e6),
            "hi": es.hypsometric_integral(zf[cells]),
            "relief_m": float(np.nanmax(zf[cells]) - np.nanmin(zf[cells])),
            "mean_slope": float(np.nanmean(s)) if np.isfinite(s).any() else None,
            "e_mm_kyr": float(np.mean(u)) * 1e6,
        }

    order = np.argsort(term, kind="stable")
    ts = term[order]
    starts = np.flatnonzero(np.r_[True, ts[1:] != ts[:-1]])
    ends = np.r_[starts[1:], ts.size]
    for s0, e0 in zip(starts, ends, strict=True):
        t = int(ts[s0])
        if out_mask[t]:
            basins.append(one(order[s0:e0], "작은 유역"))
    main = int(np.argmax(akm))
    if lo <= akm[main] <= 1000.0:
        full = es.terminal_of(rcv)
        cells = np.flatnonzero(full == full[main])
        basins.append(one(cells, "히어로 전체 유역"))
    return basins


# ---------------------------------------------------------------- 묶기
def compare_run(run_dir: Path, ref: dict | None = None, ref_path: Path = REFERENCE) -> dict:
    """실행 하나의 지구 비교 표: {summary, rows, sources, reference, levels}."""
    run_dir = Path(run_dir)
    if ref is None:
        ref = load_existing(ref_path)
    out: dict[str, Any] = {"version": EARTH_VERSION, "levels": {}, "rows": []}
    if ref is None:
        out["reference"] = None
        out["reference_error"] = f"지구 기준 파일({ref_path.name})이 없습니다. {BUILD_HINT}"
        out["summary"] = _summary([])
        return out
    out["reference"] = {
        "built": ref.get("built"),
        "git_commit": ref.get("git_commit"),
        "have": sorted((ref.get("data") or {}).keys()),
        "missing": ref.get("missing") or {},
    }
    out["sources"] = ref.get("sources") or {}
    for level, fn in (("planet", planet_rows), ("hero", hero_rows)):
        p = run_dir / level
        if not (p / "manifest.json").exists():
            out["levels"][level] = None
            continue
        b = Bundle(p)
        out["levels"][level] = {
            "l0_km": (
                es.l0_spacing_m(int(b.graph["shape"][1]), float(b.graph.get("radius_m") or 6.371e6))
                / 1000
                if b.graph["kind"] == "sphere"
                else None
            ),
            "kind": b.graph["kind"],
            "shape": b.graph["shape"],
            "spacing_m": b.graph.get("spacing_m"),
        }
        if level == "planet" and b.graph["kind"] != "sphere":
            continue
        out["rows"] += fn(b, ref)
    out["summary"] = _summary(out["rows"])
    return out


def _summary(rows: list[dict]) -> dict:
    def avg(kind: str) -> tuple[float | None, int]:
        s = [
            r["score"]
            for r in rows
            if r["kind"] == kind and r["status"] == "ok" and r["score"] is not None
        ]
        return (float(np.mean(s)) if s else None), len(s)

    em, ne = avg("emergent")
    fo, nf = avg("forced")
    return {
        "emergent_mean": em,
        "n_emergent": ne,
        "forced_mean": fo,
        "n_forced": nf,
        "n_rows": len(rows),
        "n_skipped": sum(1 for r in rows if r["status"] != "ok"),
    }


class EarthService:
    """실행마다 지구 비교 표를 한 번 셈해 두는 곳 (실행 결과나 기준 파일이 바뀌면 다시 셈)."""

    def __init__(self, ref_path: Path = REFERENCE):
        self.ref_path = Path(ref_path)
        self._lock = threading.Lock()
        self._cache: dict[str, tuple[str, dict]] = {}

    def _version(self, run_dir: Path) -> str:
        parts = [str(EARTH_VERSION)]
        for p in (
            run_dir / "planet" / "manifest.json",
            run_dir / "hero" / "manifest.json",
            self.ref_path,
        ):
            try:
                st = p.stat()
                parts.append(f"{st.st_mtime_ns}:{st.st_size}")
            except OSError:
                parts.append("-")
        return "|".join(parts)

    def get(self, run_dir: Path) -> dict:
        run_dir = Path(run_dir)
        ver = self._version(run_dir)
        key = str(run_dir.resolve())
        with self._lock:
            hit = self._cache.get(key)
            if hit and hit[0] == ver:
                return hit[1]
        result = compare_run(run_dir, ref_path=self.ref_path)
        with self._lock:
            self._cache[key] = (ver, result)
            while len(self._cache) > 8:
                self._cache.pop(next(iter(self._cache)))
        return result


def main(
    argv: list[str] | None = None,
) -> int:  # 손으로 돌려 보기: python -m bpcg_studio.earth <실행 폴더>
    import sys

    args = sys.argv[1:] if argv is None else argv
    res = compare_run(Path(args[0]))
    print(json.dumps(res["summary"], ensure_ascii=False))
    for r in res["rows"]:
        sc = "-" if r["score"] is None else f"{r['score']:.2f}"
        print(
            f"{r['id']:<24} {r['kind']:<9} {sc:>5}  {r.get('gen_text', r.get('reason', ''))} | "
            f"{r.get('earth_text', '')}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
