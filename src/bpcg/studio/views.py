"""지도 보기: 묶음 필드를 브라우저 지도용 격자로 바꾸고, 이름·단위·읽는 법을 붙입니다.

- 행성(L0 큐브스피어): 위경도 격자(PLANET_W × PLANET_H)로 다시 표본합니다. 픽셀 가운데 방향에서
  가장 가까운 칸(단위 벡터 cKDTree)을 고르고, 그 칸 번호 지도는 실행마다 디스크에 캐시합니다.
  위경도는 hero.finder 와 같은 규칙(planet.axis 기준, 경도 0 은 축에 가장 수직인 좌표축)입니다.
- 히어로(평면): 가로·세로가 HERO_MAX_PX 이하가 되게 f 칸씩 묶습니다. 연속값은 평균, 범주는 가운데
  칸, 참·거짓 같은 '있음' 값은 묶음 안에 하나라도 있으면 있음(강이 끊기지 않게), 동굴 입구·동굴
  층처럼 비트로 층을 적는 값은 묶음 안 비트를 모두 합칩니다(1 아래층 + 2 위층 = 3 두 층).
- 값은 float32 리틀 엔디언, 행 우선, 0번 행이 북쪽입니다. 표시 단위가 다른 필드(융기 mm/yr 등)는
  표시 단위로 바꿔 보냅니다(meta 의 unit, factor).
- 캐시: 단계 묶음은 그 manifest 의 mtime 으로, 지도에 겹칠 위치(히어로 자리, 회랑 사각형)는 hero·
  corridor manifest 의 mtime 으로 새로 읽습니다. run_version 은 세 manifest 의 mtime 을 이은 글자로,
  /api/level 이 돌려줘 브라우저가 캐시 열쇠와 ?v= 에 씁니다(같은 폴더를 다시 만들면 바뀜).

이름·그룹·읽는 법·색표·범주 이름은 studio.labels 의 FIELD_TEXT·DERIVED·OVERLAYS 에 있습니다.
"""

import colorsys
import hashlib
import math
import threading
from collections import OrderedDict
from pathlib import Path
from typing import Any

import numpy as np

from bpcg.bake.bundle import read_manifest
from bpcg.core.fields import FIELDS
from bpcg.studio.labels import (
    ANY_FIELDS,
    BITMASK_FIELDS,
    DERIVED,
    FIELD_TEXT,
    GROUPS,
    LEVEL_LABELS,
    OVERLAYS,
    ROCK_CATS,
    ROCK_KO,
    bool_cats,
    hex_color,
)
from bpcg.studio.names import relative_name

PLANET_W, PLANET_H = 1024, 512
HERO_MAX_PX = 640


class ViewError(Exception):
    """보기 요청 오류 (status 는 HTTP 상태 번호)."""

    def __init__(self, message: str, status: int = 400):
        super().__init__(message)
        self.status = status


def _text(name: str, level: str) -> dict[str, Any]:
    if name in DERIVED:
        return dict(DERIVED[name]["text"])
    t = dict(FIELD_TEXT.get(name, {}))
    t["label"] = t.get(f"label_{level}", t.get("label", name))
    t["read"] = t.get(f"read_{level}", t.get("read", ""))
    if "group" not in t:
        t["group"] = "지형"
    return t


def _plate_colors(values: np.ndarray) -> list[dict]:
    out = []
    for v in values:
        h = (int(v) * 0.618033988749895) % 1.0
        r, g, b = colorsys.hls_to_rgb(h, 0.62, 0.55)
        out.append(
            {
                "value": int(v),
                "label": f"판 {int(v)}",
                "color": hex_color((r * 255, g * 255, b * 255)),
            }
        )
    return out


def _fmt(x: float) -> str:
    if x is None or not math.isfinite(x):
        return "없음"
    ax = abs(x)
    if ax != 0 and (ax >= 1e6 or ax < 1e-3):
        return f"{x:.3g}"
    if ax >= 100:
        return f"{x:,.0f}"
    if ax >= 10:
        return f"{x:.1f}"
    return f"{x:.3g}"


def _finite_float(x: Any) -> float | None:
    try:
        v = float(x)
    except (TypeError, ValueError):
        return None
    return v if math.isfinite(v) else None


class LevelData:
    """실행 하나의 한 단계(planet | hero) 묶음. 필드는 쓸 때 읽고(mmap) 격자는 캐시합니다.

    bundle_dir: 묶음 폴더. level: 'planet' | 'hero'. cache_dir: 행성 칸 번호 지도 캐시 폴더.
    """

    def __init__(self, bundle_dir: Path, level: str, cache_dir: Path, extra: dict | None = None):
        self.dir = Path(bundle_dir)
        self.level = level
        self.cache_dir = Path(cache_dir)
        self.manifest = read_manifest(self.dir)
        self.entries = {e["name"]: e for e in self.manifest["fields"]}
        self.extra_key: tuple = ()  # ViewStore 가 extra 를 다시 읽을지 보는 열쇠
        self.version = 0  # manifest mtime_ns (ViewStore 가 채움, 격자·설명 캐시 열쇠)
        self.graph = self.manifest["graph"]
        self.extra = extra or {}
        self.lock = threading.Lock()
        self._pix: np.ndarray | None = None
        self._unit: np.ndarray | None = None
        self._values: OrderedDict[str, np.ndarray] = OrderedDict()
        if level == "planet" and self.graph["kind"] != "sphere":
            raise ViewError(f"{bundle_dir} 는 행성(구면) 묶음이 아닙니다")
        if level == "hero" and self.graph["kind"] != "flat":
            raise ViewError(f"{bundle_dir} 는 히어로(평면) 묶음이 아닙니다")

    # ------------------------------------------------------------ 필드 값
    def raw(self, name: str) -> np.ndarray:
        e = self.entries.get(name)
        if e is None:
            raise KeyError(name)
        if not relative_name(e.get("file")):
            raise ViewError(
                f"manifest 의 '{name}' 파일 이름이 올바르지 않습니다: {e.get('file')!r}"
            )
        return np.load(self.dir / e["file"], mmap_mode="r", allow_pickle=False)

    def has(self, name: str) -> bool:
        if name in DERIVED:
            d = DERIVED[name]
            if self.level not in d.get("levels", ("planet", "hero")):
                return False
            return all(n in self.entries for n in d["needs"])
        return name in self.entries

    def values(self, name: str) -> np.ndarray:
        """(N,) float64 값 (계산값 포함). 최근 몇 개는 메모리에 둡니다."""
        with self.lock:
            hit = self._values.get(name)
            if hit is not None:
                self._values.move_to_end(name)
                return hit
        if name in DERIVED:
            if not self.has(name):
                raise KeyError(name)
            with np.errstate(invalid="ignore"):
                v = DERIVED[name]["fn"](lambda n: np.asarray(self.raw(n), dtype=np.float64))
        else:
            a = self.raw(name)
            if a.ndim != 1:
                raise ViewError(f"'{name}' 는 지도로 그릴 수 없는 {a.ndim}차원 필드입니다")
            v = np.asarray(a, dtype=np.float64)
        with self.lock:
            self._values[name] = v
            while len(self._values) > 6:
                self._values.popitem(last=False)
        return v

    def field_names(self) -> list[str]:
        names = [n for n in self.entries if FIELD_TEXT.get(n, {}).get("map", True)]
        names = [n for n in names if len(self.entries[n]["shape"]) == 1]
        names += [n for n in DERIVED if self.has(n)]
        order = list(FIELD_TEXT) + list(DERIVED)
        return sorted(names, key=lambda n: (order.index(n) if n in order else 999, n))

    # ------------------------------------------------------------ 격자
    def _cache_file(self) -> Path:
        man = self.dir / "manifest.json"
        st = man.stat()
        key = f"{man.resolve()}|{st.st_mtime_ns}|{st.st_size}|{PLANET_W}x{PLANET_H}"
        digest = hashlib.sha1(key.encode("utf-8")).hexdigest()[:16]
        return self.cache_dir / f"equirect-{digest}.npy"

    def axis_frame(self) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
        """(축 a, 경도 0 방향 ref, 경도 90° 방향 b). hero.finder.site_from_cell 과 같은 규칙."""
        cfg = self.manifest.get("config") or {}
        axis = np.asarray((cfg.get("planet") or {}).get("axis", [0.0, 0.0, 1.0]), dtype=np.float64)
        a = axis / np.linalg.norm(axis)
        ref = np.eye(3)[int(np.argmin(np.abs(a)))]
        ref = ref - (ref @ a) * a
        ref = ref / np.linalg.norm(ref)
        return a, ref, np.cross(a, ref)

    def unit_positions(self) -> np.ndarray:
        """행성 칸 중심의 단위 벡터 (N, 3) (처음 한 번 읽어 둠)."""
        if self._unit is None:
            if not relative_name(self.graph.get("file")):
                raise ViewError(
                    f"manifest 의 그래프 파일 이름이 올바르지 않습니다: {self.graph.get('file')!r}"
                )
            with np.load(self.dir / self.graph["file"], allow_pickle=False) as npz:
                pos = np.array(npz["pos"], dtype=np.float64)
            self._unit = pos / np.linalg.norm(pos, axis=1, keepdims=True)
        return self._unit

    def pixel_cells(self) -> np.ndarray:
        """행성 위경도 격자 (H, W) 의 픽셀마다 가장 가까운 칸 번호 (디스크 캐시)."""
        with self.lock:
            if self._pix is not None:
                return self._pix
            path = self._cache_file()
            if path.exists():
                try:
                    pix = np.load(path, allow_pickle=False)
                    if pix.shape == (PLANET_H, PLANET_W):
                        self._pix = pix
                        return pix
                except (OSError, ValueError):
                    pass
            from scipy.spatial import cKDTree

            unit = self.unit_positions()
            a, ref, b = self.axis_frame()
            lon = np.linspace(-np.pi, np.pi, PLANET_W, endpoint=False) + np.pi / PLANET_W
            lat = np.linspace(np.pi / 2, -np.pi / 2, PLANET_H, endpoint=False) - np.pi / (
                2 * PLANET_H
            )
            LON, LAT = np.meshgrid(lon, lat)
            p = (
                (np.cos(LAT) * np.cos(LON))[..., None] * ref
                + (np.cos(LAT) * np.sin(LON))[..., None] * b
                + np.sin(LAT)[..., None] * a
            )
            _, idx = cKDTree(unit).query(p.reshape(-1, 3), workers=-1)
            pix = idx.reshape(PLANET_H, PLANET_W).astype(np.int32)
            self.cache_dir.mkdir(parents=True, exist_ok=True)
            tmp = path.with_name(path.name + ".part.npy")
            np.save(tmp, pix, allow_pickle=False)
            tmp.replace(path)
            self._pix = pix
            return pix

    def hero_shape(self) -> tuple[int, int, int]:
        """(행 H, 열 W, 묶는 칸 수 f)."""
        ny, nx = (int(v) for v in self.graph["shape"])
        f = max(1, math.ceil(max(ny, nx) / HERO_MAX_PX))
        return ny // f, nx // f, f

    def grid_shape(self) -> tuple[int, int]:
        if self.level == "planet":
            return PLANET_H, PLANET_W
        h, w, _ = self.hero_shape()
        return h, w

    def grid(self, name: str) -> np.ndarray:
        """지도 격자 (H, W) float32 (표시 단위)."""
        if not self.has(name):
            raise ViewError(
                f"이 실행의 {LEVEL_LABELS[self.level]} 에 '{name}' 필드가 없습니다", 404
            )
        v = self.values(name)
        factor = self.meta_light(name).get("factor", 1.0)
        if self.level == "planet":
            out = v[self.pixel_cells()]
        else:
            ny, nx = (int(x) for x in self.graph["shape"])
            h, w, f = self.hero_shape()
            a = v.reshape(ny, nx)[: h * f, : w * f]
            if f == 1:
                out = a
            elif name in BITMASK_FIELDS:
                blk = np.nan_to_num(a, nan=0.0).astype(np.int64).reshape(h, f, w, f)
                out = np.bitwise_or.reduce(np.bitwise_or.reduce(blk, axis=3), axis=1)
            elif name in ANY_FIELDS or self.entries.get(name, {}).get("dtype") == "bool":
                out = a.reshape(h, f, w, f).max(axis=(1, 3))
            elif self.meta_light(name)["kind"] == "categorical":
                out = a[f // 2 :: f, f // 2 :: f][:h, :w]
            else:
                blk = a.reshape(h, f, w, f)
                ok = np.isfinite(blk)
                cnt = ok.sum(axis=(1, 3))
                tot = np.where(ok, blk, 0.0).sum(axis=(1, 3))
                with np.errstate(invalid="ignore", divide="ignore"):
                    out = np.where(cnt > 0, tot / np.maximum(cnt, 1), np.nan)
        out = np.asarray(out, dtype=np.float64)
        if factor != 1.0:
            out = out * factor
        return np.ascontiguousarray(out, dtype="<f4")

    # ------------------------------------------------------------ 설명
    def meta_light(self, name: str) -> dict[str, Any]:
        """통계 없는 필드 설명."""
        t = _text(name, self.level)
        if name in DERIVED:
            unit_raw = DERIVED[name]["unit"]
            desc = f"계산값: {DERIVED[name]['formula']}"
            dtype = "float64"
        else:
            info = FIELDS.get(name)
            unit_raw = info.unit if info else self.entries.get(name, {}).get("unit", "")
            desc = info.description if info else self.entries.get(name, {}).get("description", "")
            dtype = self.entries.get(name, {}).get("dtype", "")
        kind = t.get("kind")
        if kind is None:
            kind = "categorical" if dtype in ("bool",) else "continuous"
        unit, factor = unit_raw, 1.0
        if "display" in t:
            unit, factor = t["display"]
        cats = t.get("categories")
        if dtype == "bool" and not cats:
            cats = bool_cats("아니오", "예", "#2e6fb3")
        return {
            "name": name,
            "label": t.get("label", name),
            "group": t.get("group", "지형"),
            "description": desc,
            "read": t.get("read", ""),
            "unit": "m³/yr" if unit == "m3/yr" else ("m²" if unit == "m2" else unit),
            "unit_raw": unit_raw,
            "factor": factor,
            "kind": kind,
            "cmap": t.get("cmap", "categorical" if kind == "categorical" else "viridis"),
            "scale": t.get("scale", "linear"),
            "center": t.get("center"),
            "range_mask": t.get("range_mask"),
            "categories": cats,
            "derived": name in DERIVED,
            "dtype": dtype,
        }

    def field_meta(self, name: str) -> dict[str, Any]:
        """필드 설명 + 통계 (최소·최대·백분위, 범주별 칸 수)와 추천 색 범위."""
        if not self.has(name):
            raise ViewError(
                f"이 실행의 {LEVEL_LABELS[self.level]} 에 '{name}' 필드가 없습니다", 404
            )
        meta = self.meta_light(name)
        v = self.values(name) * meta["factor"]
        fin = v[np.isfinite(v)]
        pos = fin[fin > 0]
        stats: dict[str, Any] = {
            "n": int(v.size),
            "finite_fraction": float(fin.size / v.size) if v.size else 0.0,
            # 로그 눈금의 아래 끝 후보 (양수가 없으면 None). 모든 필드에 넣어 로그 단추에 씀
            "positive_min": float(pos.min()) if pos.size else None,
        }
        if meta["kind"] == "categorical":
            vals, counts = np.unique(fin.astype(np.int64), return_counts=True)
            stats["counts"] = {str(int(a)): int(c) for a, c in zip(vals, counts, strict=True)}
            if meta["categories"] is None and name == "plate_id":
                meta["categories"] = _plate_colors(vals)
            elif meta["categories"] is None:
                meta["categories"] = _plate_colors(vals)
                for c in meta["categories"]:
                    c["label"] = str(c["value"])
            meta["range"] = None
        else:
            if fin.size:
                q = np.percentile(fin, [0.5, 2, 50, 98, 99.5])
                stats.update(
                    {
                        "min": float(fin.min()),
                        "max": float(fin.max()),
                        "mean": float(fin.mean()),
                        "p02": float(q[1]),
                        "median": float(q[2]),
                        "p98": float(q[3]),
                    }
                )
                lo, hi = float(q[1]), float(q[3])
                if meta["cmap"] == "terrain":
                    lo, hi = float(q[0]), float(q[4])
                nz = fin[fin != 0]
                if meta["range_mask"] == "nonzero" and nz.size:
                    # 대부분이 0 인 필드(바다의 융기 0 등): 0 이 아닌 칸으로 범위를 잡아 육지·
                    # 산맥의 차이가 한 색으로 뭉개지지 않게 합니다. 0 은 아래 끝 색입니다.
                    qz = np.percentile(nz, [2, 98])
                    lo, hi = min(float(qz[0]), 0.0), float(qz[1])
                    stats["nonzero_fraction"] = float(nz.size / fin.size)
                if meta["scale"] == "log":
                    if pos.size:
                        lo = float(np.percentile(pos, 2))
                        hi = float(pos.max())
                    else:
                        meta["scale"] = "linear"
                if meta["center"] is not None:
                    m = max(abs(lo - meta["center"]), abs(hi - meta["center"]))
                    lo, hi = meta["center"] - m, meta["center"] + m
                if hi <= lo:
                    hi = lo + (abs(lo) * 1e-3 or 1.0)
                meta["range"] = [lo, hi]
            else:
                meta["range"] = None
        meta["stats"] = stats
        return meta

    # ------------------------------------------------------------ 단계 설명
    def level_meta(self) -> dict[str, Any]:
        h, w = self.grid_shape()
        names = self.field_names()
        groups = []
        for g in GROUPS:
            items = [self.meta_light(n) for n in names if self.meta_light(n)["group"] == g]
            if items:
                groups.append({"name": g, "fields": items})
        overlays = [o for o in OVERLAYS[self.level] if self.has(o["field"])]
        out: dict[str, Any] = {
            "level": self.level,
            "label": LEVEL_LABELS[self.level],
            "width": w,
            "height": h,
            "n_cells": int(self.graph["n_cells"]),
            "groups": groups,
            "overlays": overlays,
            "markers": [],
            "rects": [],
        }
        if self.level == "planet":
            out.update(
                {
                    "projection": "equirect",
                    "extent": {"lon": [-180.0, 180.0], "lat": [-90.0, 90.0]},
                    "elevation": "z_mean_m" if self.has("z_mean_m") else "z_m",
                    "default_field": "z_mean_m" if self.has("z_mean_m") else "z_m",
                    "n_per_face": int(self.graph["shape"][1]),
                    "spacing_km": float(self.graph["spacing_m"]) / 1000.0,
                }
            )
            site = self.extra.get("hero_site")
            if site:
                out["markers"].append(
                    {"label": "히어로 유역", "lat": site["lat_deg"], "lon": site["lon_deg"]}
                )
        else:
            ny, nx = (int(v) for v in self.graph["shape"])
            hh, ww, f = self.hero_shape()
            dx = float(self.graph["spacing_m"])
            ox, oy = (float(v) for v in self.graph["origin"])
            out.update(
                {
                    "projection": "local",
                    "extent": {
                        "x_km": [ox / 1000.0, (ox + ww * f * dx) / 1000.0],
                        "y_km": [(oy - hh * f * dx) / 1000.0, oy / 1000.0],
                    },
                    "elevation": "z_m",
                    "default_field": "z_m",
                    "spacing_m": dx,
                    "factor": f,
                    "shape": [ny, nx],
                    "pixel_m": dx * f,
                }
            )
            site = self.extra.get("hero_site")
            if site:
                out["site"] = {"lat": site["lat_deg"], "lon": site["lon_deg"]}
            rect = self.extra.get("corridor_rect")
            if rect:
                out["rects"].append(
                    {
                        "label": "걷는 회랑 (엔진으로 구운 곳)",
                        "x0": rect["x_min"] / 1000.0,
                        "x1": rect["x_max"] / 1000.0,
                        "y0": rect["y_min"] / 1000.0,
                        "y1": rect["y_max"] / 1000.0,
                        "color": "#e03131",
                    }
                )
        return out

    # ------------------------------------------------------------ 칸 정보
    def cell_at(self, px: int, py: int) -> int:
        h, w = self.grid_shape()
        if not (0 <= px < w and 0 <= py < h):
            raise ViewError(f"픽셀 ({px}, {py}) 가 지도 밖입니다 ({w}×{h})")
        if self.level == "planet":
            return int(self.pixel_cells()[py, px])
        _, nx = (int(v) for v in self.graph["shape"])
        _, _, f = self.hero_shape()
        return int((py * f + f // 2) * nx + (px * f + f // 2))

    def probe(self, px: int, py: int) -> dict[str, Any]:
        """픽셀 하나의 칸에 있는 모든 필드 값 (그룹별, 이름·단위·범주 이름 포함)."""
        cell = self.cell_at(px, py)
        where: dict[str, Any] = {"cell": cell, "px": px, "py": py}
        if self.level == "planet":
            with self.lock:
                p = self.unit_positions()[cell]
            a, ref, b = self.axis_frame()
            where["lat"] = math.degrees(math.asin(float(np.clip(p @ a, -1.0, 1.0))))
            where["lon"] = math.degrees(math.atan2(float(p @ b), float(p @ ref)))
            where["label"] = f"칸 {cell:,} · 위도 {where['lat']:.2f}°, 경도 {where['lon']:.2f}°"
        else:
            ny, nx = (int(v) for v in self.graph["shape"])
            dx = float(self.graph["spacing_m"])
            ox, oy = (float(v) for v in self.graph["origin"])
            j, i = divmod(cell, nx)
            where["x_km"] = (ox + (i + 0.5) * dx) / 1000.0
            where["y_km"] = (oy - (j + 0.5) * dx) / 1000.0
            where["row"], where["col"] = j, i
            where["label"] = f"칸 ({j}, {i}) · 동 {where['x_km']:.2f} km, 북 {where['y_km']:.2f} km"
        groups: dict[str, list[dict]] = {g: [] for g in GROUPS}
        z_here = None
        for name in self.field_names() + [n for n in ("receiver",) if n in self.entries]:
            meta = self.meta_light(name)
            try:
                raw = float(self.values(name)[cell])
            except (KeyError, ViewError):
                continue
            if name == "z_m":
                z_here = raw
            shown = raw * meta["factor"]
            item = {
                "name": name,
                "label": meta["label"],
                "unit": meta["unit"],
                "value": _finite_float(shown),
                "display": _fmt(shown),
                "derived": meta["derived"],
            }
            if meta["kind"] == "categorical" and math.isfinite(raw):
                cats = {c["value"]: c["label"] for c in (meta["categories"] or [])}
                item["display"] = cats.get(int(raw), f"{int(raw)}")
                if name == "plate_id":
                    item["display"] = f"판 {int(raw)}"
            elif name == "receiver":
                item["display"] = (
                    "자기 자신 (바다·출구)" if int(raw) == cell else f"칸 {int(raw):,}"
                )
                item["unit"] = ""
            groups.setdefault(meta["group"], []).append(item)
        strata = self._strata(cell, z_here)
        return {
            "level": self.level,
            "where": where,
            "groups": [{"name": g, "items": items} for g, items in groups.items() if items],
            "strata": strata,
        }

    def _strata(self, cell: int, z: float | None) -> list[dict] | None:
        if "strata_bottom_m" not in self.entries or "strata_rock" not in self.entries:
            return None
        bottom = np.asarray(self.raw("strata_bottom_m")[cell], dtype=np.float64)
        rock = np.asarray(self.raw("strata_rock")[cell], dtype=np.int64)
        rows = []
        for k in range(rock.size):
            top = None if k == 0 else float(bottom[k - 1])
            bot = float(bottom[k]) if k < bottom.size else None
            r = int(rock[k])
            if z is None:
                state = ""
            elif bot is not None and bot >= z:
                state = "깎여 없어짐"
            elif top is None or top >= z:
                state = "지표에 드러남"
            else:
                state = "땅속"
            rows.append(
                {
                    "index": k,
                    "rock": ROCK_KO[r] if 0 <= r < len(ROCK_KO) else str(r),
                    "rock_id": r,
                    "color": ROCK_CATS[r]["color"] if 0 <= r < len(ROCK_CATS) else "#999999",
                    "top_m": top,
                    "bottom_m": bot,
                    "bedrock": k == rock.size - 1,
                    "state": state,
                }
            )
        return rows


class ViewStore:
    """실행·단계별 LevelData 와 격자 바이트를 캐시합니다 (서버가 하나 가짐)."""

    def __init__(self, cache_dir: Path, max_levels: int = 6, max_grids: int = 48):
        self.cache_dir = Path(cache_dir)
        self.max_levels = max_levels
        self.max_grids = max_grids
        self.lock = threading.Lock()
        self.levels: OrderedDict[tuple, LevelData] = OrderedDict()
        self.grids: OrderedDict[tuple, bytes] = OrderedDict()
        self.metas: OrderedDict[tuple, dict] = OrderedDict()

    def level(self, run_dir: Path, level: str) -> LevelData:
        """실행 한 단계의 LevelData (그 manifest 가 바뀌면 새로 읽음, 겹칠 위치는 따로 새로 봄)."""
        if level not in ("planet", "hero"):
            raise ViewError(f"level 은 planet 또는 hero 입니다: {level!r}")
        run_dir = Path(run_dir)
        bundle = run_dir / level
        man = bundle / "manifest.json"
        try:
            mtime = man.stat().st_mtime_ns
        except OSError:
            raise ViewError(f"이 실행에는 {LEVEL_LABELS[level]} 결과가 없습니다", 404) from None
        key = (str(bundle.resolve()), mtime)
        with self.lock:
            data = self.levels.get(key)
            if data is not None:
                self.levels.move_to_end(key)
        if data is None:
            data = LevelData(bundle, level, self.cache_dir)
            data.version = mtime
            with self.lock:
                self.levels[key] = data
                while len(self.levels) > self.max_levels:
                    self.levels.popitem(last=False)
        # 히어로 자리·회랑 사각형은 다른 단계 manifest 에 있어, 지도를 실행 도중에 열었으면
        # 나중에 생깁니다. 그 manifest 의 mtime 이 바뀌면 다시 읽습니다(작은 JSON 두 개).
        extra_key = (_mtime_ns(run_dir / "hero" / "manifest.json"),
                     _mtime_ns(run_dir / "corridor" / "manifest.json"))  # fmt: skip
        if data.extra_key != extra_key:
            extra = _extra(run_dir)
            with data.lock:
                data.extra = extra
                data.extra_key = extra_key
        return data

    def level_meta(self, run_dir: Path, level: str) -> dict[str, Any]:
        """/api/level: 단계 설명 + version (세 manifest 의 mtime, 브라우저 캐시 열쇠)."""
        out = self.level(run_dir, level).level_meta()
        out["version"] = run_version(Path(run_dir))
        return out

    def grid_bytes(self, run_dir: Path, level: str, name: str) -> bytes:
        data = self.level(run_dir, level)
        # id(data) 는 LevelData 가 캐시에서 밀려난 뒤 다시 쓰일 수 있어 manifest mtime 을 씁니다.
        key = (str(data.dir), data.version, name)
        with self.lock:
            hit = self.grids.get(key)
            if hit is not None:
                self.grids.move_to_end(key)
                return hit
        out = data.grid(name).tobytes(order="C")
        with self.lock:
            self.grids[key] = out
            while len(self.grids) > self.max_grids:
                self.grids.popitem(last=False)
        return out

    def field_meta(self, run_dir: Path, level: str, name: str) -> dict:
        data = self.level(run_dir, level)
        key = (str(data.dir), data.version, name)
        with self.lock:
            hit = self.metas.get(key)
            if hit is not None:
                return hit
        meta = data.field_meta(name)
        h, w = data.grid_shape()
        meta["width"], meta["height"] = w, h
        with self.lock:
            self.metas[key] = meta
            while len(self.metas) > 200:
                self.metas.popitem(last=False)
        return meta


def _mtime_ns(path: Path) -> int:
    try:
        return path.stat().st_mtime_ns
    except OSError:
        return 0


def run_version(run_dir: Path) -> str:
    """실행 결과의 판 표시: planet·hero·corridor manifest 의 mtime_ns 를 이은 글자.

    `bpcg all --out out/<이름>` 으로 같은 폴더를 다시 만들거나, 실행 도중 다음 단계가 생기면
    바뀝니다.
    """
    run_dir = Path(run_dir)
    parts = (_mtime_ns(run_dir / lv / "manifest.json") for lv in ("planet", "hero", "corridor"))
    return "-".join(str(p) for p in parts)


def _extra(run_dir: Path) -> dict:
    """지도에 겹칠 위치: 히어로 자리(위경도), 회랑 사각형(히어로 국소 m)."""
    import json

    out: dict[str, Any] = {}
    try:
        hm = json.loads((run_dir / "hero" / "manifest.json").read_text(encoding="utf-8"))
        site = (hm.get("meta") or {}).get("site")
        if site and site.get("lat_deg") is not None:
            out["hero_site"] = {"lat_deg": site["lat_deg"], "lon_deg": site["lon_deg"]}
    except (OSError, ValueError):
        pass
    try:
        cm = json.loads((run_dir / "corridor" / "manifest.json").read_text(encoding="utf-8"))
        rect = (cm.get("corridor") or {}).get("rect_local_m")
        if rect:
            out["corridor_rect"] = rect
    except (OSError, ValueError):
        pass
    return out
