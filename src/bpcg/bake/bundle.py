"""생성 결과 묶음(bundle) 쓰기·읽기 (docs/pipeline.md 11장, 설계도 5장 '하나의 묶음').

폴더 하나가 묶음 하나입니다.

    <path>/manifest.json      bpcg 버전, git 커밋, 설정 해시·내용, 시드, 그래프 정보,
                              face_basis(구면), 필드 목록(이름·파일·dtype·단위·그룹·모양),
                              강 구간 파일, meta
    <path>/graph.npz          pos, nbr, dist, area (CellGraph 배열)
    <path>/<그룹>/<이름>.npy   필드 하나 (FIELDS 의 dtype 으로 바꿔 저장)
    <path>/rivers.npz         강 구간 (cells 평평한 int64, offsets int64), 있을 때만

필드 이름·단위·dtype·그룹은 bpcg.core.fields.FIELDS 를 따릅니다. 고도처럼 float64 로 계산한
값도 저장할 때 FIELDS 의 dtype(float32)으로 바꿉니다(pipeline.md 2장 '정밀도').

load_planet_state / load_hero_state 는 묶음에서 pipeline.PlanetState / HeroState 를 다시 만들어
`bpcg hero --from`, `bpcg bake --hero` 가 디스크에서 이어 돌게 합니다.
"""

import json
import math
import os
import subprocess
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np

from bpcg import __version__
from bpcg.core import cubesphere as cs
from bpcg.core.config import Config
from bpcg.core.fields import FIELDS, check_fields
from bpcg.core.graph import CellGraph
from bpcg.core.paths import ROOT

FORMAT = "bpcg-bundle"
FORMAT_VERSION = 1
MANIFEST = "manifest.json"
GRAPH_FILE = "graph.npz"
RIVERS_FILE = "rivers.npz"
JSON_ARRAY_MAX = 1_024  # meta 안의 배열·리스트가 이보다 크면 JSON 에 넣지 않고 요약만 적습니다


@dataclass(eq=False)
class Bundle:
    """read_bundle 결과.

    graph: CellGraph. fields: 이름 → 배열 (FIELDS dtype). manifest: manifest.json 내용 전체.
    meta: manifest['meta'] (쓸 때 준 meta 를 JSON 으로 바꾼 것). rivers: 강 구간 목록 또는 None.
    """

    graph: CellGraph
    fields: dict[str, np.ndarray]
    manifest: dict
    meta: dict = field(default_factory=dict)
    rivers: list[np.ndarray] | None = None


# ---------------------------------------------------------------- 도움 함수
def git_commit() -> str | None:
    """저장소의 현재 git 커밋 해시. git 이 없거나 저장소가 아니면 None."""
    try:
        out = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=10,
            check=False,
        )
    except (OSError, subprocess.SubprocessError):
        return None
    sha = out.stdout.strip()
    return sha if out.returncode == 0 and sha else None


def jsonable(obj: Any, max_array: int | None = JSON_ARRAY_MAX) -> Any:
    """JSON 으로 쓸 수 있는 값으로 바꿉니다.

    numpy 스칼라·배열은 파이썬 값·리스트로, NaN·inf 는 null 로 바꿉니다(JSON 표준이 아님).
    원소가 max_array 보다 많은 배열은 {"__array__": 모양, "dtype"} 요약만 남깁니다
    (리스트는 {"__list__": 길이}). max_array 가 None 이면 줄이지 않습니다.
    dataclass 처럼 __dict__ 가 있는 객체는 그 dict 로, 그 밖은 문자열로 바꿉니다.
    """
    if obj is None or isinstance(obj, bool | str):
        return obj
    if isinstance(obj, int | np.integer):
        return int(obj)
    if isinstance(obj, float | np.floating):
        v = float(obj)
        return v if math.isfinite(v) else None
    if isinstance(obj, np.bool_):
        return bool(obj)
    if isinstance(obj, np.ndarray):
        if max_array is not None and obj.size > max_array:
            return {"__array__": list(obj.shape), "dtype": str(obj.dtype)}
        return jsonable(obj.tolist(), max_array)
    if isinstance(obj, dict):
        return {str(k): jsonable(v, max_array) for k, v in obj.items()}
    if isinstance(obj, list | tuple):
        if max_array is not None and len(obj) > max_array:
            return {"__list__": len(obj)}
        return [jsonable(v, max_array) for v in obj]
    if isinstance(obj, Path):
        return str(obj)
    if hasattr(obj, "__dict__"):
        return jsonable(vars(obj), max_array)
    return str(obj)


def _write_text_atomic(path: Path, text: str) -> None:
    tmp = path.with_name(path.name + ".part")
    tmp.write_text(text, encoding="utf-8")
    os.replace(tmp, path)


def write_json(path: str | os.PathLike, obj: Any, max_array: int | None = JSON_ARRAY_MAX) -> None:
    """obj 를 jsonable 로 바꿔 들여쓰기 2 의 UTF-8 JSON 으로 씁니다(임시 파일 뒤 이름 바꾸기).

    max_array: jsonable 과 같음. 목록 자체가 내용인 파일(동굴 입구 등)은 None 으로 줄이지 않습니다.
    """
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    data = jsonable(obj, max_array)
    text = json.dumps(data, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
    _write_text_atomic(p, text)


def _graph_info(graph: CellGraph) -> dict:
    return {
        "kind": graph.kind,
        "shape": [int(v) for v in graph.shape],
        "n_cells": int(graph.n_cells),
        "radius_m": None if graph.R is None else float(graph.R),
        "spacing_m": float(graph.spacing),
        "origin": [float(v) for v in graph.origin],
        "file": GRAPH_FILE,
    }


def _cast_field(name: str, value, n: int) -> np.ndarray:
    info = FIELDS[name]
    a = np.asarray(value)
    if a.shape[:1] != (n,):
        raise ValueError(f"필드 '{name}' 의 첫 차원 {a.shape[:1]} 이 칸 수 {n} 와 다릅니다")
    dt = np.dtype(info.dtype)
    if dt.kind in "iu" and a.dtype.kind == "f":
        if not np.isfinite(a).all() or np.any(a != np.round(a)):
            raise ValueError(f"필드 '{name}' 은 정수여야 하는데 정수가 아닌 값이 있습니다")
    if dt.kind in "iu" and a.size:
        lo, hi = np.iinfo(dt).min, np.iinfo(dt).max
        if a.min() < lo or a.max() > hi:
            raise ValueError(f"필드 '{name}' 값이 {dt} 범위를 벗어납니다")
    return np.ascontiguousarray(a.astype(dt, copy=False))


# ---------------------------------------------------------------- 쓰기·읽기
def write_bundle(
    path: str | os.PathLike,
    graph: CellGraph,
    fields: dict,
    meta: dict | None = None,
    *,
    cfg: Config | None = None,
    rivers: list[np.ndarray] | None = None,
    kind: str | None = None,
) -> dict:
    """묶음 하나를 path 폴더에 씁니다 (pipeline.md 11장 bundle.py).

    graph: CellGraph. fields: FIELDS 이름 → (N,) 또는 (N, ...) 배열. meta: manifest['meta'] 에 넣을
    dict (jsonable 로 바꿈). cfg: 주면 설정 해시·내용·시드를 기록합니다. rivers: 강 구간 목록
    (각각 int 칸 번호 배열)을 rivers.npz 로. kind: 'planet' | 'hero' 등 묶음 종류 표시.
    반환: 쓴 manifest dict. 이름이 FIELDS 에 없거나 모양·범위가 맞지 않으면 ValueError.
    """
    if not isinstance(graph, CellGraph):
        raise ValueError(f"graph 는 CellGraph 여야 합니다: {type(graph).__name__}")
    if not isinstance(fields, dict):
        raise ValueError("fields 는 이름 → 배열 dict 여야 합니다")
    check_fields(fields)
    root = Path(path)
    root.mkdir(parents=True, exist_ok=True)
    n = graph.n_cells
    entries = []
    for name in sorted(fields):
        arr = _cast_field(name, fields[name], n)
        info = FIELDS[name]
        rel = f"{info.group}/{name}.npy"
        (root / info.group).mkdir(exist_ok=True)
        np.save(root / rel, arr, allow_pickle=False)
        entries.append(
            {
                "name": name,
                "file": rel,
                "dtype": str(arr.dtype),
                "unit": info.unit,
                "group": info.group,
                "shape": [int(v) for v in arr.shape],
                "description": info.description,
            }
        )
    np.savez(
        root / GRAPH_FILE,
        pos=np.ascontiguousarray(graph.pos, dtype=np.float64),
        nbr=np.ascontiguousarray(graph.nbr, dtype=np.int32),
        dist=np.ascontiguousarray(graph.dist, dtype=np.float64),
        area=np.ascontiguousarray(graph.area, dtype=np.float64),
    )
    rivers_info = None
    if rivers is not None:
        segs = [np.asarray(s, dtype=np.int64).ravel() for s in rivers]
        cells = np.concatenate(segs) if segs else np.zeros(0, dtype=np.int64)
        if cells.size and (cells.min() < 0 or cells.max() >= n):
            raise ValueError("rivers 에 범위를 벗어난 칸 번호가 있습니다")
        offsets = np.concatenate([[0], np.cumsum([s.size for s in segs])]).astype(np.int64)
        np.savez(root / RIVERS_FILE, cells=cells, offsets=offsets)
        rivers_info = {"file": RIVERS_FILE, "n_segments": len(segs), "n_cells": int(cells.size)}
    manifest = {
        "format": FORMAT,
        "format_version": FORMAT_VERSION,
        "kind": kind,
        "bpcg_version": __version__,
        "git_commit": git_commit(),
        "config_digest": None if cfg is None else cfg.digest(),
        "seed": None if cfg is None else int(cfg.planet.seed),
        "profile": None if cfg is None else cfg.profile.get("name"),
        "graph": _graph_info(graph),
        "face_basis": (
            {"u": cs.FACE_U.tolist(), "v": cs.FACE_V.tolist(), "n": cs.FACE_N.tolist()}
            if graph.kind == "sphere"
            else None
        ),
        "fields": entries,
        "rivers": rivers_info,
        "config": None if cfg is None else cfg.as_dict(),
        "meta": jsonable(meta or {}),
    }
    write_json(root / MANIFEST, manifest)
    return manifest


def read_manifest(path: str | os.PathLike) -> dict:
    """묶음 폴더의 manifest.json 을 읽습니다. 형식이 다르면 ValueError."""
    p = Path(path) / MANIFEST
    if not p.exists():
        raise FileNotFoundError(f"묶음 manifest 가 없습니다: {p}")
    man = json.loads(p.read_text(encoding="utf-8"))
    if man.get("format") != FORMAT:
        raise ValueError(f"{p} 는 bpcg 묶음이 아닙니다 (format = {man.get('format')!r})")
    if int(man.get("format_version", -1)) > FORMAT_VERSION:
        raise ValueError(f"{p} 의 형식 버전 {man.get('format_version')} 을 읽을 수 없습니다")
    return man


def read_bundle(path: str | os.PathLike) -> Bundle:
    """write_bundle 로 쓴 묶음을 읽습니다 (배열은 쓴 그대로, 비트 단위로 같음)."""
    root = Path(path)
    man = read_manifest(root)
    g = man["graph"]
    with np.load(root / g["file"], allow_pickle=False) as npz:
        pos, nbr, dist, area = (np.array(npz[k]) for k in ("pos", "nbr", "dist", "area"))
    graph = CellGraph(
        kind=g["kind"],
        shape=tuple(int(v) for v in g["shape"]),
        pos=pos,
        nbr=nbr,
        dist=dist,
        area=area,
        spacing=float(g["spacing_m"]),
        R=None if g["radius_m"] is None else float(g["radius_m"]),
        origin=tuple(float(v) for v in g["origin"]),
    )
    fields: dict[str, np.ndarray] = {}
    for e in man["fields"]:
        arr = np.load(root / e["file"], allow_pickle=False)
        if list(arr.shape) != list(e["shape"]) or str(arr.dtype) != e["dtype"]:
            raise ValueError(f"묶음 필드 '{e['name']}' 의 모양·dtype 이 manifest 와 다릅니다")
        fields[e["name"]] = arr
    rivers = None
    if man.get("rivers"):
        with np.load(root / man["rivers"]["file"], allow_pickle=False) as npz:
            cells, offsets = np.array(npz["cells"]), np.array(npz["offsets"])
        rivers = [cells[offsets[i] : offsets[i + 1]].copy() for i in range(offsets.size - 1)]
    return Bundle(
        graph=graph, fields=fields, manifest=man, meta=man.get("meta") or {}, rivers=rivers
    )


def config_from_manifest(manifest: dict) -> Config:
    """묶음에 기록한 설정 내용으로 Config 를 다시 만듭니다 (없으면 ValueError)."""
    data = manifest.get("config")
    if not isinstance(data, dict):
        raise ValueError("묶음 manifest 에 설정(config)이 없습니다")
    return Config(data)


# ---------------------------------------------------------------- 상태 저장·복원
def _float_fields(fields: dict) -> dict:
    """복원한 필드를 계산용 dtype 으로: 실수는 float64, 나머지는 그대로."""
    out = {}
    for k, v in fields.items():
        out[k] = v.astype(np.float64) if v.dtype.kind == "f" else v
    return out


def save_planet_state(path: str | os.PathLike, planet, cfg: Config) -> dict:
    """PlanetState 를 묶음으로 씁니다. info·diag 는 meta 에 (큰 배열은 요약만)."""
    fields = {k: v for k, v in planet.fields.items() if k in FIELDS}
    meta = {"info": planet.info, "diag": planet.diag}
    return write_bundle(path, planet.graph, fields, meta, cfg=cfg, kind="planet")


def load_planet_state(path: str | os.PathLike):
    """묶음에서 pipeline.PlanetState 를 다시 만듭니다 (실수 필드는 float64 로)."""
    from bpcg.geology.model import LayerColumns
    from bpcg.pipeline import PlanetState

    b = read_bundle(path)
    if b.graph.kind != "sphere":
        raise ValueError(f"{path} 는 행성(구면) 묶음이 아닙니다")
    fields = _float_fields(b.fields)
    for k in ("strata_bottom_m", "strata_rock"):
        if k not in fields:
            raise ValueError(f"행성 묶음에 '{k}' 가 없습니다")
    columns = LayerColumns.from_columns(fields["strata_bottom_m"], fields["strata_rock"])
    return PlanetState(
        graph=b.graph,
        fields=fields,
        columns=columns,
        info=b.meta.get("info") or {},
        diag=b.meta.get("diag") or {},
    )


def _site_meta(site) -> dict | None:
    if site is None:
        return None
    return {
        "center_unit": np.asarray(site.center_unit, dtype=np.float64).tolist(),
        "east": np.asarray(site.east, dtype=np.float64).tolist(),
        "north": np.asarray(site.north, dtype=np.float64).tolist(),
        "l0_cell": int(site.l0_cell),
        "score": float(site.score),
        "parts": {k: float(v) for k, v in (site.parts or {}).items()},
        "lat_deg": float(site.lat_deg),
        "lon_deg": float(site.lon_deg),
    }


def save_hero_state(path: str | os.PathLike, hero, cfg: Config) -> dict:
    """HeroState 를 묶음으로 씁니다 (강 구간은 rivers.npz, 자리·선상지·진단은 meta)."""
    fields = {k: v for k, v in hero.fields.items() if k in FIELDS}
    meta = {
        "site": _site_meta(hero.site),
        "fan_apexes": hero.fan_apexes,
        "diag": hero.diag,
    }
    return write_bundle(
        path, hero.graph, fields, meta, cfg=cfg, rivers=list(hero.rivers), kind="hero"
    )


def load_hero_state(path: str | os.PathLike):
    """묶음에서 pipeline.HeroState 를 다시 만듭니다 (실수 필드는 float64 로)."""
    from bpcg.geology.model import LayerColumns
    from bpcg.hero.finder import HeroSite
    from bpcg.pipeline import HeroState

    b = read_bundle(path)
    if b.graph.kind != "flat":
        raise ValueError(f"{path} 는 히어로(평면) 묶음이 아닙니다")
    fields = _float_fields(b.fields)
    for k in ("strata_bottom_m", "strata_rock"):
        if k not in fields:
            raise ValueError(f"히어로 묶음에 '{k}' 가 없습니다")
    columns = LayerColumns.from_columns(fields["strata_bottom_m"], fields["strata_rock"])
    s = b.meta.get("site")
    site = None
    if s:
        site = HeroSite(
            center_unit=np.asarray(s["center_unit"], dtype=np.float64),
            east=np.asarray(s["east"], dtype=np.float64),
            north=np.asarray(s["north"], dtype=np.float64),
            l0_cell=int(s["l0_cell"]),
            score=float(s["score"]),
            parts=dict(s.get("parts") or {}),
            lat_deg=float("nan") if s.get("lat_deg") is None else float(s["lat_deg"]),
            lon_deg=float("nan") if s.get("lon_deg") is None else float(s["lon_deg"]),
        )
    return HeroState(
        graph=b.graph,
        fields=fields,
        columns=columns,
        site=site,
        rivers=b.rivers or [],
        fan_apexes=list(b.meta.get("fan_apexes") or []),
        diag=b.meta.get("diag") or {},
    )
