"""행성 전체를 지구본처럼 돌려 보기 위한 굽기 (globe bake).

L0 행성 묶음의 필드를 큐브스피어 면 6개 × 면당 N×N 칸(기본 N = 256, 2..1024)으로 다시 담아,
Godot 이 구 하나에 색을 칠하고 고도로 부풀려 보여 줄 수 있게 합니다. 엔진은 계산하지 않고 이
파일만 읽습니다. 칸마다 무엇인지(이름·단위·범례·설명)를 globe.json 에 함께 적습니다. 색표와 설명
글은 bake.globe_text 에 있습니다.

쓰는 파일 (<out>/, 보통 out/<run>/globe/ 와 engine/baked/globe/)

    globe.json                 형식 설명 (아래)
    corners_elevation_m.bin    꼭짓점 고도 float32 (6, N+1, N+1) [m], 구 메시의 정점 높이
    <필드 이름>.bin             필드 하나 float32 또는 uint8 (6, N, N)
    rivers.bin, lakes.bin      덧그림(overlay) uint8 (6, N, N)

.bin 은 머리글 없는 리틀 엔디언 배열을 [면][행][열] 순서(C 순서)로 늘어놓은 것입니다. 값이 없는
칸은 연속 필드면 float32 NaN(대륙의 해양저 나이, 바다의 칸 안 기복 등)이고, 범주 필드면 범주
번호 255(categories 에 no_data: true, 색은 nan_rgb; 바다의 지표 암석·지질 틀)입니다.

globe.json 의 주요 키
- format "bpcg-globe", format_version 1, bpcg_version, git_commit, config_digest, seed.
- frame: 좌표 규칙. godot = (q_x, q_z, -q_y), q = axis_rotation · planet. axis_rotation 은 설정의
  자전축(planet.axis)을 z(북쪽)로, 경도 0 의 기준(hero.finder 와 같은 규칙)을 x 로 돌리는
  회전이고, 자전축이 z 면 단위 행렬이라 godot = (planet_x, planet_z, -planet_y) 입니다. 그래서
  자전축이 늘 Godot +Y 이고 위도·경도가 기후·히어로의 것과 같습니다. 둘 다 회전(행렬식 +1)이라
  면 기저의 오른손 규칙 u × v = n 이 그대로 지켜집니다.
- radius_m, face_res(N), sea_level_m(0).
- faces: 면 6개의 {index, n, u, v} (Godot 좌표). 묶음 manifest 의 face_basis 를 바꾼 것입니다.
- mapping: 등각 사상. dir ∝ n + tan(a·π/4)·u + tan(b·π/4)·v.
  칸 (행 r, 열 c) 의 중심 a = -1 + 2(c+0.5)/N, b = -1 + 2(r+0.5)/N.
  꼭짓점 (r, c) 는 a = -1 + 2c/N, b = -1 + 2r/N. 역사상은 mapping_inverse 에 있습니다.
  묶음의 셀 번호 c = f·n² + j·n + i (i 가 u, j 가 v)와 같은 규칙이라 행 r = j, 열 c = i 입니다.
- corners: 꼭짓점 고도 파일 설명. fields: 필드 목록, overlays: 덧그림 목록, hero: 히어로 위치.
- 연속 필드: colormap [[값, [r, g, b]], …] 은 값이 엄격히 늘어납니다(같은 값의 매듭 없음).
  color_break(있으면): 색이 이 값에서 끊긴다는 표시입니다. 엔진은 이 값의 양쪽 칸끼리, 그리고
  값이 없는 칸과 있는 칸끼리는 텍셀 보간으로 섞지 않습니다(범례에 없는 색이 생기지 않게).
- 범주 필드: categories [{id, label, rgb, area_fraction, no_data?}], area_fraction_basis
  ("land" 또는 "planet")와 area_fraction_note(글). point_markers 가 true 면 0 이 아닌 칸이 드물어
  엔진이 그 칸마다 범주 색 점을 화면 크기 그대로 덧찍습니다. n_marked_cells 는 그 칸 수입니다.

다시 담는 규칙 (L0 단위 벡터에 cKDTree)
- 연속 값: 칸 중심에서 가장 가까운 L0 칸 4개의 평균(값 없는 칸은 빼고). 값 없음 여부는 가장
  가까운 칸을 따릅니다.
- 범주 값: 가장 가까운 L0 칸의 값. 판 경계·동굴·강·호수처럼 가늘거나 드문 것은 칸 안에 든
  L0 칸 가운데 하나라도 있으면 남깁니다(필드마다 sampling 키, 뜻은 globe.json 의 sampling 표).
- 꼭짓점 고도: 꼭짓점에서 가장 가까운 L0 칸 4개의 평균. 이웃 면이 같이 쓰는 꼭짓점은 같은 값을
  씁니다(구 메시에 틈이 생기지 않게).

칸 크기는 어디서나 '면 가운데 칸 한 변' = 2πR / (4·면당 칸 수) 로 잽니다(L0 와 지구본 모두).

실행: `python -m bpcg.bake.globe --planet out/earth_v2/planet [--out DIR] [--engine]
[--face-res 256]`. 기본 출력은 <planet>/../globe, --engine 은 engine/baked/globe 에도 씁니다.
"""

import argparse
import colorsys
import json
import math
import os
import shutil
import sys
import time
from pathlib import Path

import numpy as np
from scipy.spatial import cKDTree

from bpcg import __version__
from bpcg.bake import globe_text as gt
from bpcg.bake.bundle import git_commit, write_json
from bpcg.bake.globe_text import GROUPS, TEXT
from bpcg.core import cubesphere as cs
from bpcg.core.constants import SECONDS_PER_YEAR
from bpcg.core.paths import ROOT
from bpcg.geology import rocks as rk

FORMAT = "bpcg-globe"
FORMAT_VERSION = 1
GLOBE_JSON = "globe.json"
CORNERS_FILE = "corners_elevation_m.bin"
DEFAULT_FACE_RES = 256
# 면당 칸 수의 상한. 넘으면 꼭짓점 방향·이웃 표만 수 GB 가 되고(노트북 메모리 부족), 엔진이
# 필드 텍스처 한 장을 만드는 데도 몇 분이 걸립니다. 512 는 파이썬 약 1 s, 엔진 검사 약 7 s 입니다.
MAX_FACE_RES = 1024
K_NEAREST = 4  # 연속 값·꼭짓점: 가장 가까운 L0 칸 4개의 평균
SEA_LEVEL_M = 0.0
NAN_RGB = (128, 128, 128)  # 엔진이 '값 없음' 칸에 칠하는 회색 (제안)
# 범주 0 이 아닌 지구본 칸이 이 몫보다 적으면 엔진이 점으로 덧찍습니다 (point_markers).
MARKER_MAX_FRACTION = 0.01
assert len(gt.ROCK_LABELS) == rk.N_ROCKS

# godot = PLANET_TO_GODOT @ q, q = axis_rotation @ planet. (x, y, z) → (x, z, -y), 행렬식 +1.
PLANET_TO_GODOT = np.array([[1.0, 0.0, 0.0], [0.0, 0.0, 1.0], [0.0, -1.0, 0.0]])
FRAME_RULE = "godot = (planet_x, planet_z, -planet_y); planet axis z = north = Godot +Y"
FRAME_RULE_ROTATED = (
    "godot = (q_x, q_z, -q_y), q = axis_rotation · planet; planet.axis = north = Godot +Y"
)
MAPPING = (
    "equiangular: dir ∝ n + tan(a·π/4)·u + tan(b·π/4)·v; cell (row r, col c) center "
    "a = -1 + 2(c+0.5)/N, b = -1 + 2(r+0.5)/N; corner (r, c) a = -1 + 2c/N, b = -1 + 2r/N"
)
MAPPING_INVERSE = (
    "f = argmax_f dir·n_f; a = (4/π)·atan((dir·u)/(dir·n)), b = (4/π)·atan((dir·v)/(dir·n)); "
    "c = clamp(floor((a+1)·N/2), 0, N-1), r = clamp(floor((b+1)·N/2), 0, N-1)"
)
LAYOUT = "little-endian, C order [face][row][col]; row r follows v (b), col c follows u (a)"
SAMPLING = {
    "mean_k4": "칸 중심에서 가장 가까운 L0 칸 4개의 평균 (값 없는 칸은 빼고, 값 없음 여부는 "
    "가장 가까운 칸을 따름)",
    "nearest": "칸 중심에서 가장 가까운 L0 칸의 값",
    "nearest_fill_in_cell": "가장 가까운 L0 칸의 값. 그 값이 0(없음)이면 칸 안에 든 L0 칸 값 "
    "가운데 가장 큰 것 (가는 선이 끊기지 않게)",
    "max_in_cell": "가장 가까운 L0 칸과 칸 안에 든 L0 칸 값 가운데 가장 큰 것 (강은 가장 큰 등급)",
    "bits_or_in_cell": "가장 가까운 L0 칸과 칸 안에 든 L0 칸 값의 비트 OR (드문 칸을 남김)",
}
# 묶음 칸 위치와 문서의 사상 사이 허용 오차의 여유 (부동소수 반올림만큼)
MAPPING_TOL_SLACK = 1e-6
RIVER_CLASS_EDGES_M3_PER_S = (10.0, 100.0, 1000.0)


def _say(log, msg: str) -> None:
    if log is not None:
        log(msg)


def _num(x: float) -> float:
    """JSON 에 쓰기 좋게 유효숫자 6자리로 줄입니다 (-0.0 은 0.0)."""
    return float(f"{float(x):.6g}") + 0.0


def _rgb(c) -> list[int]:
    return [int(v) for v in c]


# ---------------------------------------------------------------- 좌표와 사상
def to_godot(v: np.ndarray, rotation: np.ndarray | None = None) -> np.ndarray:
    """행성 좌표 (…, 3) → Godot 좌표 (…, 3): q = rotation · v, (q_x, q_y, q_z) → (q_x, q_z, -q_y).

    rotation: axis_rotation 의 결과 (None 이면 단위 행렬, 자전축이 z 인 행성).
    """
    m = PLANET_TO_GODOT if rotation is None else PLANET_TO_GODOT @ rotation
    return np.asarray(v, dtype=np.float64) @ m.T + 0.0


def axis_rotation(axis) -> np.ndarray:
    """자전축을 z 로, 경도 0 의 기준을 x 로 돌리는 회전 (3, 3) (행렬식 +1).

    경도 0 의 기준은 hero.finder.site_from_cell 과 같은 규칙입니다: 자전축 성분의 절댓값이 가장
    작은 좌표축을 자전축에 수직으로 깎은 방향. 자전축이 z 면 기준이 x 라 단위 행렬입니다.
    행: [기준, 자전축 × 기준, 자전축]. 그래서 회전한 좌표의 asin(q_z) 와 atan2(q_y, q_x) 가
    climate.latitude_rad 와 finder 의 위도·경도와 같습니다.
    """
    a = np.asarray(axis, dtype=np.float64)
    norm = float(np.linalg.norm(a)) if a.shape == (3,) else 0.0
    if not norm > 0 or not np.isfinite(a).all():
        raise ValueError(f"planet.axis 는 길이가 0 이 아닌 3차원 벡터여야 합니다: {axis}")
    a = a / norm
    ref = np.eye(3)[int(np.argmin(np.abs(a)))]
    ref = ref - (ref @ a) * a
    ref = ref / np.linalg.norm(ref)
    rot = np.stack([ref, np.cross(a, ref), a])
    return np.where(np.abs(rot) < 1e-15, 0.0, rot) + 0.0


def frame_entry(rotation: np.ndarray) -> dict:
    """globe.json 의 frame (좌표 규칙)."""
    identity = bool(np.allclose(rotation, np.eye(3), atol=1e-12))
    return {
        "rule": FRAME_RULE if identity else FRAME_RULE_ROTATED,
        "units": "unit sphere directions",
        "axis_rotation": rotation.tolist(),
        "matrix_planet_to_godot": (PLANET_TO_GODOT @ rotation).tolist(),
    }


def face_bases(face_basis: dict | None = None) -> np.ndarray:
    """면 기저 (6, 3, 3) [면, (n, u, v), xyz] 를 만들고 검사합니다 (행성 좌표).

    face_basis: 묶음 manifest 의 {"n", "u", "v"} (각각 6×3). None 이면 write_bundle 이 적는 표
    (cubesphere.FACE_N/U/V)를 씁니다. 정규직교가 아니거나 u × v ≠ n 이면 ValueError.
    """
    fb = face_basis or {"n": cs.FACE_N, "u": cs.FACE_U, "v": cs.FACE_V}
    try:
        b = np.stack([np.asarray(fb[k], dtype=np.float64) for k in ("n", "u", "v")], axis=1)
    except (KeyError, TypeError, ValueError) as e:
        raise ValueError(f"face_basis 는 n, u, v 를 6×3 으로 가져야 합니다: {e}") from e
    if b.shape != (6, 3, 3):
        raise ValueError(f"face_basis 모양이 (6, 3, 3) 이 아닙니다: {b.shape}")
    check_bases(b)
    return b


def check_bases(bases: np.ndarray, tol: float = 1e-9) -> None:
    """면 기저가 정규직교이고 오른손 규칙 u × v = n 이며 n 이 서로 다른지 검사합니다."""
    for f in range(bases.shape[0]):
        n, u, v = bases[f]
        if np.abs(bases[f] @ bases[f].T - np.eye(3)).max() > tol:
            raise ValueError(f"면 {f} 의 기저 n, u, v 가 정규직교가 아닙니다")
        if np.abs(np.cross(u, v) - n).max() > tol:
            raise ValueError(f"면 {f} 의 기저가 오른손 규칙 u × v = n 을 따르지 않습니다")
    # 큐브의 여섯 면: 법선마다 반대쪽 면이 하나(n·n' = -1), 옆면이 넷(n·n' = 0)
    dots = bases[:, 0] @ bases[:, 0].T
    for f in range(bases.shape[0]):
        others = np.delete(dots[f], f)
        if np.sum(np.abs(others + 1.0) < tol) != 1 or np.sum(np.abs(others) < tol) != 4:
            raise ValueError(f"면 {f} 의 법선 n 이 큐브의 여섯 방향을 이루지 않습니다")


def face_directions(bases: np.ndarray, res: int, *, corners: bool = False) -> np.ndarray:
    """문서의 등각 사상으로 칸 중심(또는 꼭짓점) 방향 (6, R, R, 3) 단위 벡터를 만듭니다.

    bases: (6, 3, 3) [n, u, v]. res: 면당 칸 수 N. corners: True 면 R = N+1 꼭짓점, 아니면 R = N.
    배열 [f, r, c] 의 열 c 가 a(u 방향), 행 r 이 b(v 방향)입니다.
    """
    if corners:
        t = -1.0 + 2.0 * np.arange(res + 1) / res
    else:
        t = -1.0 + 2.0 * (np.arange(res) + 0.5) / res
    a, b = np.meshgrid(t, t, indexing="xy")  # a[r, c] = t[c], b[r, c] = t[r]
    X = np.tan(a * np.pi / 4.0)[None, :, :, None]
    Y = np.tan(b * np.pi / 4.0)[None, :, :, None]
    n, u, v = (bases[:, k][:, None, None, :] for k in range(3))
    d = n + X * u + Y * v
    return d / np.linalg.norm(d, axis=-1, keepdims=True)


def direction_to_cell(
    dirs: np.ndarray, bases: np.ndarray, res: int
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """방향 (…, 3) → 그 방향을 담은 칸 (면 f, 행 r, 열 c). 문서의 역사상(mapping_inverse)."""
    d = np.asarray(dirs, dtype=np.float64)
    f = np.argmax(d @ bases[:, 0].T, axis=-1)
    dn = np.einsum("...k,...k->...", d, bases[f, 0])
    a = 4.0 / np.pi * np.arctan(np.einsum("...k,...k->...", d, bases[f, 1]) / dn)
    b = 4.0 / np.pi * np.arctan(np.einsum("...k,...k->...", d, bases[f, 2]) / dn)
    c = np.clip(np.floor((a + 1.0) * res / 2.0).astype(np.int64), 0, res - 1)
    r = np.clip(np.floor((b + 1.0) * res / 2.0).astype(np.int64), 0, res - 1)
    return f, r, c


def check_bundle_mapping(graph, bases: np.ndarray, jitter: float) -> dict:
    """문서의 사상으로 만든 칸 중심이 묶음 칸 위치(graph.pos)와 맞는지 봅니다.

    면당 칸 수가 묶음과 같을 때(N = n) 칸 (f, r, c) 는 묶음 칸 f·n² + r·n + c 와 같은 곳이어야
    합니다. 노드 흔들기(graph.sphere_graph)는 칸을 u, v 로 각각 ±jitter/2 · h 옮기므로
    (h = sqrt(면적)/R) 어긋남 |d| ≤ m = jitter·h/√2 이고, 각도로는 atan(m / (1 − m)) 이하입니다.
    반환: {n_per_face, jitter, max_error_cells, max_error_ratio}. 한 칸이라도 넘으면 ValueError.
    """
    _, n, _ = graph.shape
    unit = np.asarray(graph.pos, dtype=np.float64)
    unit = unit / np.linalg.norm(unit, axis=1, keepdims=True)
    centers = face_directions(bases, n).reshape(-1, 3)
    ang = np.arctan2(np.linalg.norm(np.cross(unit, centers), axis=1), (unit * centers).sum(1))
    h = np.sqrt(np.asarray(graph.area, dtype=np.float64)) / float(graph.R)
    m = max(float(jitter), 0.0) * h / math.sqrt(2.0)
    tol = np.arctan(m / np.maximum(1.0 - m, 1e-3)) + MAPPING_TOL_SLACK * h
    ratio = float((ang / tol).max())
    err = float((ang / h).max())
    if not ratio <= 1.0:
        raise ValueError(
            f"묶음 칸 위치가 지구본 사상(행 = v, 열 = u)과 맞지 않습니다: 최대 {err:.3f} 칸 "
            f"어긋남 (노드 흔들기 {jitter:g} 의 허용치의 {ratio:.2f} 배). 묶음의 face_basis 나 "
            "셀 번호 규칙을 확인하세요"
        )
    return {
        "n_per_face": int(n),
        "jitter": float(jitter),
        "max_error_cells": _num(err),
        "max_error_ratio": _num(ratio),
    }


def _face_name(n_planet: np.ndarray) -> str:
    """행성 좌표 법선의 이름 (+X 등, 행성 좌표 기준)."""
    for f in range(6):
        if np.allclose(n_planet, cs.FACE_N[f]):
            return f"planet {cs.FACE_NAMES[f]}"
    return "planet ?"


def lat_lon_deg(unit, rotation: np.ndarray | None = None) -> tuple[float, float]:
    """행성 좌표 단위 벡터 → (위도, 경도) [°]. q = rotation · unit 의 자전축 z, 경도 0 은 +x 쪽.

    rotation: axis_rotation 의 결과 (None 이면 단위 행렬). 엔진이 Godot 좌표에서 계산하는
    위도 asin(gy), 경도 atan2(-gz, gx) 와 같은 값입니다.
    """
    p = np.asarray(unit, dtype=np.float64)
    p = p / np.linalg.norm(p)
    if rotation is not None:
        p = rotation @ p
    lat = math.degrees(math.asin(float(np.clip(p[2], -1.0, 1.0))))
    return lat, math.degrees(math.atan2(float(p[1]), float(p[0])))


# ---------------------------------------------------------------- 히어로 위치
def hero_entry(hero: dict | None, rotation: np.ndarray | None = None) -> dict | None:
    """히어로 dict (행성 좌표 unit, size_m) → globe.json 의 hero (Godot 좌표).

    위도·경도는 늘 unit 에서 다시 계산합니다(hero 의 lat_deg, lon_deg 는 쓰지 않음). 그래야
    설명 글과 엔진이 Godot 좌표에서 계산해 핀 이름표에 쓰는 값이 같습니다.
    rotation: axis_rotation 의 결과 (None 이면 자전축 z).
    """
    if not hero:
        return None
    p = np.asarray(hero.get("unit", ()), dtype=np.float64)
    if p.shape != (3,) or not np.isfinite(p).all() or np.linalg.norm(p) == 0:
        raise ValueError(f"히어로 unit 은 유한한 3차원 벡터여야 합니다: {hero.get('unit')}")
    p = p / np.linalg.norm(p)
    lat, lon = lat_lon_deg(p, rotation)
    size = hero.get("size_m")
    size = None if size is None else float(size)
    size_txt = "" if size is None else f"(한 변 {size / 1000:.0f} km 정사각형)"
    return {
        "unit": [float(v) for v in to_godot(p, rotation)],
        "unit_planet": [float(v) for v in p],
        "lat_deg": lat,
        "lon_deg": lon,
        "size_m": size,
        "label": "히어로 유역",
        "description": f"자세히 만든 히어로 영역{size_txt}의 가운데입니다. 엔진의 지형 장면"
        f"(회랑)이 이 안에 있습니다. 위도 {lat:.2f}°, 경도 {lon:.2f}° 입니다.",
    }


def hero_from_run(run_dir) -> dict | None:
    """실행 폴더의 corridor/manifest.json 또는 hero/manifest.json 에서 히어로 자리를 찾습니다.

    반환: {unit (행성 좌표), lat_deg, lon_deg, size_m, seed, config_digest, source} 또는 None.
    """
    root = Path(run_dir)
    cor = root / "corridor" / "manifest.json"
    if cor.exists():
        m = json.loads(cor.read_text(encoding="utf-8"))
        site = m.get("site") or {}
        if site.get("hero_center_unit") is not None:
            h = m.get("hero") or {}
            size = None
            if h.get("shape") and h.get("spacing_m"):
                size = max(int(v) for v in h["shape"]) * float(h["spacing_m"])
            return {
                "unit": site["hero_center_unit"],
                "lat_deg": site.get("hero_lat_deg"),
                "lon_deg": site.get("hero_lon_deg"),
                "size_m": size,
                "seed": m.get("seed"),
                "config_digest": m.get("config_digest"),
                "source": str(cor),
            }
    hero = root / "hero" / "manifest.json"
    if hero.exists():
        m = json.loads(hero.read_text(encoding="utf-8"))
        site = (m.get("meta") or {}).get("site") or {}
        if site.get("center_unit") is not None:
            g = m.get("graph") or {}
            size = None
            if g.get("shape") and g.get("spacing_m"):
                size = max(int(v) for v in g["shape"]) * float(g["spacing_m"])
            return {
                "unit": site["center_unit"],
                "lat_deg": site.get("lat_deg"),
                "lon_deg": site.get("lon_deg"),
                "size_m": size,
                "seed": m.get("seed"),
                "config_digest": m.get("config_digest"),
                "source": str(hero),
            }
    return None


# ---------------------------------------------------------------- 다시 담기
class _Resampler:
    """L0 칸 값 → 지구본 칸 값 (모듈 설명의 '다시 담는 규칙')."""

    def __init__(self, unit: np.ndarray, bases: np.ndarray, res: int):
        self.res = res
        self.tree = cKDTree(unit)
        centers = face_directions(bases, res).reshape(-1, 3)
        k = min(K_NEAREST, unit.shape[0])
        _, idx = self.tree.query(centers, k=k, workers=-1)
        self.knn = idx.reshape(centers.shape[0], k)
        self.nearest = self.knn[:, 0]
        f, r, c = direction_to_cell(unit, bases, res)
        self.cell_of_l0 = (f * res + r) * res + c

    def mean(self, values: np.ndarray) -> np.ndarray:
        v = np.asarray(values, dtype=np.float64)[self.knn]
        ok = np.isfinite(v)
        cnt = ok.sum(axis=1)
        out = np.where(ok, v, 0.0).sum(axis=1) / np.maximum(cnt, 1)
        out[~ok[:, 0]] = np.nan  # 값 없음 여부는 가장 가까운 칸을 따름
        return out

    def pick(self, values: np.ndarray) -> np.ndarray:
        return np.asarray(values)[self.nearest]

    def in_cell(self, values: np.ndarray, mode: str) -> np.ndarray:
        """가장 가까운 칸 값과 칸 안 L0 칸 값을 합칩니다 (mode 는 SAMPLING 의 *_in_cell 키)."""
        v = np.asarray(values).astype(np.uint8)
        near = v[self.nearest]
        acc = np.zeros_like(near)
        if mode == "bits_or_in_cell":
            np.bitwise_or.at(acc, self.cell_of_l0, v)
            return near | acc
        np.maximum.at(acc, self.cell_of_l0, v)
        if mode == "max_in_cell":
            return np.maximum(near, acc)
        if mode == "nearest_fill_in_cell":
            return np.where(near != 0, near, acc)
        raise ValueError(f"모르는 sampling: {mode}")

    def corners(self, z: np.ndarray, bases: np.ndarray) -> np.ndarray:
        """꼭짓점 고도 (6, N+1, N+1). 여러 면이 같이 쓰는 꼭짓점은 한 번만 계산해 같은 값."""
        d = face_directions(bases, self.res, corners=True).reshape(-1, 3)
        rep = np.arange(d.shape[0])
        pairs = cKDTree(d).query_pairs(r=1e-9, output_type="ndarray")
        if pairs.size:  # 같은 점끼리는 모두 짝으로 나오므로(i < j) 가장 작은 번호가 대표
            np.minimum.at(rep, pairs[:, 1], pairs[:, 0])
        uniq, inv = np.unique(rep, return_inverse=True)
        k = min(K_NEAREST, z.shape[0])
        _, idx = self.tree.query(d[uniq], k=k, workers=-1)
        val = np.asarray(z, dtype=np.float64)[idx.reshape(uniq.size, k)].mean(axis=1)
        return val[inv].reshape(6, self.res + 1, self.res + 1)


# ---------------------------------------------------------------- 색표
def _strict(stops: list) -> list:
    for a, b in zip(stops, stops[1:], strict=False):
        if not b[0] > a[0]:
            raise ValueError(f"색표 값이 늘어나지 않습니다: {a[0]} → {b[0]}")
    return stops


def _sequential(lo: float, hi: float, colors) -> list:
    lo = float(lo)
    hi = float(hi) if float(hi) > float(lo) else float(lo) + 1.0
    m = len(colors) - 1
    return _strict([[_num(lo + (hi - lo) * k / m), _rgb(c)] for k, c in enumerate(colors)])


def _diverging(lo: float, hi: float, colors) -> list:
    """가운데 색이 0 인 양쪽 색표. 음수 쪽과 양수 쪽 눈금 크기가 다를 수 있습니다."""
    half = len(colors) // 2
    neg, pos = colors[: half + 1], colors[half:]
    stops = []
    if lo < 0:
        stops += [[_num(lo * (1 - k / half)), _rgb(neg[k])] for k in range(half)]
    stops.append([0.0, _rgb(colors[half])])
    if hi > 0:
        stops += [[_num(hi * k / half), _rgb(pos[k])] for k in range(1, half + 1)]
    if len(stops) == 1:
        stops.append([1.0, _rgb(pos[-1])])
    return _strict(stops)


def _elevation_colormap(dmax: float, hmax: float) -> list:
    """고도 색표. 바다 매듭은 SHORE_STOP_M(-0.5 m)에서 끝나고 육지 매듭은 0 m 에서 시작합니다.

    값은 엄격히 늘어납니다(같은 값의 매듭 없음). 해수면에서 색이 끊긴다는 것은 필드의
    color_break(0 m)로 따로 적습니다.
    """
    stops = [[_num(t * dmax), _rgb(c)] for t, c in gt.OCEAN_STOPS]
    stops.append([gt.SHORE_STOP_M, _rgb(gt.OCEAN_SHORE_RGB)])
    stops += [[_num(t * hmax), _rgb(c)] for t, c in gt.LAND_STOPS]
    return _strict(stops)


def _pct(values: np.ndarray, q: float, default: float) -> float:
    v = np.asarray(values, dtype=np.float64)
    v = v[np.isfinite(v)]
    return float(np.percentile(v, q)) if v.size else float(default)


def _plate_rgb(k: int) -> tuple[int, int, int]:
    """판 k 의 색: 황금각(137.5°)으로 색상을 돌려 이웃 번호끼리도 잘 갈리게 합니다."""
    h = (k * 137.50776405) % 360.0 / 360.0
    s, v = (0.55, 0.85) if k % 2 == 0 else (0.70, 0.70)
    return tuple(int(round(x * 255)) for x in colorsys.hsv_to_rgb(h, s, v))


def _cfg_float(cfg, section: str, key: str) -> float | None:
    """설정 값 하나 (cfg 가 None 이거나 값이 없으면 None)."""
    try:
        return float(getattr(getattr(cfg, section), key))
    except (AttributeError, TypeError, ValueError):
        return None


def cell_km(radius_m: float, n_per_face: int) -> float:
    """면 가운데 칸 한 변 [km] = 2πR / (4·면당 칸 수). L0 와 지구본 칸 모두 이 잣대로 잽니다."""
    return 2.0 * math.pi * float(radius_m) / (4.0 * int(n_per_face)) / 1000.0


# ---------------------------------------------------------------- 필드 만들기
def _entry(name: str, kind: str, unit: str, source: str, sampling: str, fmt: dict) -> dict:
    label, group, desc, how = TEXT[name]
    return {
        "name": name,
        "label": label,
        "group": group,
        "unit": unit,
        "file": f"{name}.bin",
        "dtype": "float32" if kind == "continuous" else "uint8",
        "kind": kind,
        "source": source,
        "sampling": sampling,
        "description": desc.format(**fmt),
        "how_to_read": how.format(**fmt),
    }


def _continuous(
    name, values, unit, source, colormap, fmt, *, log=False, nan_label=None, color_break=None
):
    e = _entry(name, "continuous", unit, source, "mean_k4", fmt)
    v = np.asarray(values, dtype=np.float64)
    fin = v[np.isfinite(v)]
    e.update(
        {
            "min": colormap[0][0],
            "max": colormap[-1][0],
            "colormap": colormap,
            "log": bool(log),
            "data_min": _num(fin.min()) if fin.size else None,
            "data_max": _num(fin.max()) if fin.size else None,
            "nan_label": nan_label,
        }
    )
    if color_break is not None:
        e["color_break"] = _num(color_break)
    return e, v.astype(np.float32)


def _categorical(
    name, values, source, cats, area_frac, fmt, *, sampling="nearest", basis="planet",
    no_data_label=None,
):  # fmt: skip
    """범주 필드. basis 는 area_fraction 의 잣대("land" 또는 "planet").

    no_data_label 을 주면 '값 없음' 범주(번호 NO_DATA_ID, 색 NAN_RGB, area_fraction null,
    no_data true)를 덧붙입니다. values 의 그 칸은 부르는 쪽이 NO_DATA_ID 로 채웁니다.
    """
    e = _entry(name, "categorical", "", source, sampling, fmt)
    e["categories"] = [
        {
            "id": int(i),
            "label": lab,
            "rgb": _rgb(c),
            "area_fraction": _num(area_frac[i]) if i < area_frac.size else 0.0,
        }
        for i, lab, c in cats
    ]
    if no_data_label is not None:
        if any(int(i) == gt.NO_DATA_ID for i, _, _ in cats):
            raise ValueError(f"{name}: 범주 번호 {gt.NO_DATA_ID} 는 '값 없음' 자리입니다")
        e["categories"].append(
            {
                "id": gt.NO_DATA_ID,
                "label": no_data_label,
                "rgb": _rgb(NAN_RGB),
                "area_fraction": None,
                "no_data": True,
            }
        )
    e["area_fraction_basis"] = basis
    e["area_fraction_note"] = gt.AREA_NOTE_LAND if basis == "land" else gt.AREA_NOTE_PLANET
    return e, np.asarray(values).astype(np.uint8)


def _area_fraction(ids: np.ndarray, area: np.ndarray, n: int) -> np.ndarray:
    w = np.bincount(np.asarray(ids, dtype=np.int64), weights=area, minlength=n)
    return w / max(float(area.sum()), 1e-300)


# 칸 안 기복 설명에 '지하수면 깊이 ≈ mean_fraction × 기복' 을 적는 조건: 육지의 (골짜기 바닥 −
# 지하수면) 가운데값이 기복 가운데값의 이 몫보다 작을 때. 골짜기 깊이 ≈ 기복은 비가 이 범위일 때.
WATER_TABLE_GAP_MAX_FRACTION = 0.1
VALLEY_RATIO_RANGE = (0.8, 1.25)


def _hydro_note(fields: dict, land: np.ndarray, mean_fraction: float | None) -> str:
    """칸 안 기복 설명에 붙일 물 이야기 (자료가 그렇다고 말할 때만)."""
    if "relief_m" not in fields or not land.any():
        return ""
    rel = np.asarray(fields["relief_m"], dtype=np.float64)[land]
    rel_med = _pct(rel, 50.0, 0.0)
    gap = None
    if "water_table_m" in fields and "z_m" in fields:
        d = np.asarray(fields["z_m"], np.float64) - np.asarray(fields["water_table_m"], np.float64)
        d = d[land]
        d = d[np.isfinite(d)]
        if d.size and rel_med > 0:
            g = float(np.median(np.maximum(d, 0.0)))
            gap = g if g < WATER_TABLE_GAP_MAX_FRACTION * rel_med else None
    ratio = None
    if "valley_depth_m" in fields:
        vd = np.asarray(fields["valley_depth_m"], dtype=np.float64)[land]
        ok = np.isfinite(vd) & np.isfinite(rel) & (rel > 0)
        if ok.any():
            q = float(np.median(vd[ok] / rel[ok]))
            ratio = q if VALLEY_RATIO_RANGE[0] <= q <= VALLEY_RATIO_RANGE[1] else None
    return gt.relief_hydro_note(mean_fraction, gap, ratio)


def _build_fields(fields: dict, rs: _Resampler, graph, cfg, log=None) -> tuple[list, list]:
    """필드 목록 (entry dict, 배열) 두 줄. 묶음에 없는 필드는 건너뜁니다.

    지하수면 깊이(z − water_table_m)와 골짜기 깊이(valley_depth_m)는 싣지 않습니다. L0 에서는
    지하수면이 골짜기 바닥에 붙어 있어 앞의 것은 mean_fraction × 칸 안 기복, 뒤의 것은 칸 안
    기복과 거의 같은 값이라, 따로 칠하면 물 이야기처럼 보이는 기복 지도가 됩니다. 대신 칸 안
    기복의 설명에 그 관계를 (자료가 그렇다고 말할 때만) 적습니다.
    """
    area = np.asarray(graph.area, dtype=np.float64)
    ocean = np.asarray(fields["is_ocean"], dtype=bool) if "is_ocean" in fields else None
    ocean_g = None if ocean is None else np.asarray(rs.pick(ocean), dtype=bool)
    land = np.ones(area.shape, dtype=bool) if ocean is None else ~ocean
    _, n_l0, _ = graph.shape
    base = {"l0_km": cell_km(graph.R, n_l0)}
    mean_fraction = _cfg_float(cfg, "relief", "mean_fraction")
    out: list[tuple[dict, np.ndarray]] = []

    def land_only(x):
        x = np.asarray(x, dtype=np.float64).copy()
        if ocean is not None:
            x[ocean] = np.nan
        return x

    def land_categorical(name: str, ids, source: str, cats, n_ids: int, fmt: dict):
        """지질 범주: 바다 칸은 NO_DATA_ID('바다 (지질 계산 안 함)'), 넓이 몫은 육지 기준."""
        ids = np.asarray(ids).astype(np.int64)
        values = np.asarray(rs.pick(ids)).astype(np.uint8)
        if ocean is None:
            text = {"ocean_note": "", "area_note": gt.PLANET_AREA_NOTE, "land": ""}
            return _categorical(name, values, source, cats, _area_fraction(ids, area, n_ids),
                                {**fmt, **text})  # fmt: skip
        values[ocean_g] = gt.NO_DATA_ID
        text = {
            "ocean_note": gt.OCEAN_GEOLOGY_NOTE,
            "area_note": gt.LAND_AREA_NOTE,
            "land": "육지의 ",
        }
        return _categorical(name, values, source, cats,
                            _area_fraction(ids[land], area[land], n_ids), {**fmt, **text},
                            basis="land", no_data_label=gt.OCEAN_NO_GEOLOGY_LABEL)  # fmt: skip

    z_name = "z_mean_m" if "z_mean_m" in fields else ("z_m" if "z_m" in fields else None)
    if z_name is not None:
        z = np.asarray(fields[z_name], dtype=np.float64)
        dmax = max(_pct(-z[z < 0], 99.0, 100.0), 100.0)
        hmax = max(_pct(z[z >= 0], 99.0, 10.0), 10.0)
        fmt = {**base, "dmax": dmax, "hmax": hmax,
               "z_land": gt.elevation_land_text(z_name, mean_fraction)}  # fmt: skip
        out.append(
            _continuous("elevation", rs.mean(z), "m", z_name, _elevation_colormap(dmax, hmax), fmt,
                        color_break=SEA_LEVEL_M)
        )  # fmt: skip
    if "relief_m" in fields:
        src = land_only(fields["relief_m"])
        cm = _sequential(0.0, max(_pct(src, 99.0, 1.0), 1.0), gt.MAGMA)
        fmt = {**base, "hi": cm[-1][0], "hydro_note": _hydro_note(fields, land, mean_fraction)}
        out.append(
            _continuous("relief_m", rs.mean(src), "m", "relief_m", cm, fmt,
                        nan_label="바다 (계산하지 않음)")
        )  # fmt: skip
    if "plate_id" in fields:
        pid = np.asarray(fields["plate_id"]).astype(np.int64)
        n_plates = int(pid.max()) + 1 if pid.size else 0
        if pid.size and (int(pid.min()) < 0 or n_plates > 256):
            _say(log, f"[지구본] 경고: 판 번호가 {int(pid.min())}..{int(pid.max())} 라 uint8 "
                      "(0..255)에 담을 수 없어 판 레이어만 건너뜁니다")  # fmt: skip
        else:
            cats = [(k, f"판 {k}", _plate_rgb(k)) for k in range(n_plates)]
            out.append(
                _categorical("plate_id", rs.pick(pid), "plate_id", cats,
                             _area_fraction(pid, area, n_plates), {**base, "n_plates": n_plates})
            )  # fmt: skip
    if "boundary_type" in fields:
        bt = np.asarray(fields["boundary_type"]).astype(np.uint8)
        out.append(
            _categorical("boundary_type", rs.in_cell(bt, "nearest_fill_in_cell"), "boundary_type",
                         gt.BOUNDARY_CATS, _area_fraction(bt, area, 4), base,
                         sampling="nearest_fill_in_cell")
        )  # fmt: skip
    if "crust_type" in fields:
        ct = np.asarray(fields["crust_type"]).astype(np.uint8)
        out.append(
            _categorical("crust_type", rs.pick(ct), "crust_type", gt.CRUST_CATS,
                         _area_fraction(ct, area, 2), base)
        )  # fmt: skip
    if "uplift_m_per_yr" in fields:
        u = np.asarray(fields["uplift_m_per_yr"], dtype=np.float64) * 1000.0
        lo = min(_pct(u[u < 0], 1.0, 0.0), 0.0)
        hi = max(_pct(u[u > 0], 99.5, 0.0), 0.0)
        cm = _diverging(lo, hi, gt.PUOR_R)
        how = gt.uplift_how(cm[0][0], cm[-1][0], lo < 0, hi > 0)
        out.append(
            _continuous("uplift_mm_per_yr", rs.mean(u), "mm/yr", "uplift_m_per_yr × 1000", cm,
                        {**base, "how": how})
        )  # fmt: skip
    if "ocean_age_myr" in fields:
        age = np.asarray(fields["ocean_age_myr"], dtype=np.float64)
        cm = _sequential(0.0, max(_pct(age, 100.0, 1.0), 1.0), gt.RDYLBU)
        out.append(
            _continuous("ocean_age_myr", rs.mean(age), "Myr", "ocean_age_myr", cm,
                        {**base, "hi": cm[-1][0]}, nan_label="대륙 지각 (해양저가 아님)")
        )  # fmt: skip
    if "temperature_c" in fields:
        t = np.asarray(fields["temperature_c"], dtype=np.float64)
        lo, hi = _pct(t, 1.0, -1.0), _pct(t, 99.0, 1.0)
        cm = _diverging(min(lo, 0.0), max(hi, 0.0), gt.RDBU_R)
        how = gt.temperature_how(cm[0][0], cm[-1][0], _pct(t, 0.0, lo), _pct(t, 100.0, hi))
        lapse = _cfg_float(cfg, "climate", "lapse_rate_c_per_m")
        lapse = 6.5 if lapse is None else 1000.0 * lapse
        out.append(
            _continuous("temperature_c", rs.mean(t), "°C", "temperature_c", cm,
                        {**base, "how": how, "lapse": lapse})
        )  # fmt: skip
    if "precip_m_per_yr" in fields:
        p = np.asarray(fields["precip_m_per_yr"], dtype=np.float64)
        cm = _sequential(_pct(p, 1.0, 0.0), _pct(p, 99.0, 1.0), gt.YLGNBU)
        out.append(
            _continuous("precip_m_per_yr", rs.mean(p), "m/yr", "precip_m_per_yr", cm,
                        {**base, "lo": cm[0][0], "hi": cm[-1][0]})
        )  # fmt: skip
    if "discharge_m3_per_yr" in fields:
        q = np.asarray(fields["discharge_m3_per_yr"], dtype=np.float64) / SECONDS_PER_YEAR
        lq = np.full(q.shape, np.nan)
        lq[q > 0] = np.log10(q[q > 0])
        lq = land_only(lq)
        cm = _sequential(_pct(lq, 1.0, -1.0), _pct(lq, 99.5, 3.0), gt.VIRIDIS)
        e, arr = _continuous(
            "discharge_log10_m3_per_s", rs.mean(lq), "log10(m³/s)",
            "log10(discharge_m3_per_yr / SECONDS_PER_YEAR)", cm,
            {**base, "lo": cm[0][0], "hi": cm[-1][0]}, log=True, nan_label="바다",
        )  # fmt: skip
        e["log_note"] = "값이 이미 log10(m³/s) 입니다. 실제 유량은 10^값 m³/s, 색표 값도 log10"
        out.append((e, arr))
    if "surface_rock" in fields:
        cats = [(k, gt.ROCK_LABELS[k], rk.COLOR_RGB[k]) for k in range(rk.N_ROCKS)]
        e, arr = land_categorical("surface_rock", fields["surface_rock"], "surface_rock", cats,
                                  rk.N_ROCKS, base)  # fmt: skip
        for c in e["categories"]:
            if c["id"] < rk.N_ROCKS:
                c["key"] = rk.ROCK_NAMES[c["id"]]
                c["soluble"] = bool(rk.SOLUBLE[c["id"]])
        out.append((e, arr))
    if "template_id" in fields:
        out.append(
            land_categorical("geology_template", fields["template_id"], "template_id",
                             gt.TEMPLATE_CATS, len(gt.TEMPLATE_CATS), base)
        )  # fmt: skip
    if "cave_level_0_m" in fields or "cave_level_1_m" in fields:
        n = graph.n_cells
        cave = np.zeros(n, dtype=np.uint8)
        for bit, key in ((1, "cave_level_0_m"), (2, "cave_level_1_m")):
            if key in fields:
                cave |= np.where(np.isfinite(np.asarray(fields[key], np.float64)), bit, 0).astype(
                    np.uint8
                )
        cave_g = rs.in_cell(cave, "bits_or_in_cell")
        n_globe = int(np.count_nonzero(cave_g))
        markers = 0 < n_globe <= MARKER_MAX_FRACTION * cave_g.size
        fmt = {**base, "n_cave_cells": int((cave > 0).sum()), "n_globe_cells": n_globe,
               "marker_note": gt.MARKER_NOTE if markers else ""}  # fmt: skip
        e, arr = _categorical("caves", cave_g, "cave_level_0_m, cave_level_1_m", gt.CAVE_CATS,
                              _area_fraction(cave, area, 4), fmt,
                              sampling="bits_or_in_cell")  # fmt: skip
        e["point_markers"] = bool(markers)
        e["n_nonzero_cells"] = n_globe
        out.append((e, arr))
    return [e for e, _ in out], [a for _, a in out]


def _build_overlays(fields: dict, rs: _Resampler, cfg) -> tuple[list, list]:
    entries, arrays = [], []
    if "is_river" in fields:
        river = np.asarray(fields["is_river"], dtype=bool)
        cls = np.zeros(river.shape, dtype=np.uint8)
        if "discharge_m3_per_yr" in fields:
            q = np.asarray(fields["discharge_m3_per_yr"], np.float64) / SECONDS_PER_YEAR
            cls[river] = 1 + np.searchsorted(RIVER_CLASS_EDGES_M3_PER_S, q[river], side="right")
        else:
            cls[river] = 1
        thr = float(cfg.rivers.min_discharge_m3_per_s) if cfg is not None else float("nan")
        entries.append(
            {
                "name": "rivers",
                "label": "강",
                "file": "rivers.bin",
                "dtype": "uint8",
                "source": "is_river, discharge_m3_per_yr",
                "sampling": "max_in_cell",
                "description": f"유량이 강 문턱(초당 {thr:g} m³) 이상인 육지 칸입니다. 값 1~4 는 "
                "유량 등급이고 0 은 강이 아닙니다. L0 칸이 커서 육지 대부분이 문턱을 넘으므로, "
                "큰 강만 보려면 등급 3 이상만 그리면 됩니다.",
                "how_to_read": "0 이 아닌 칸에 파란색을 덧칠합니다. 진한 파랑일수록 큰 강입니다.",
                "rgb": _rgb(gt.RIVER_CATS[-1][2]),
                "categories": [
                    {"id": i, "label": lab, "rgb": None if c is None else _rgb(c)}
                    for i, lab, c in gt.RIVER_CATS
                ],
            }
        )
        arrays.append(rs.in_cell(cls, "max_in_cell"))
    if "is_lake" in fields:
        lake = np.asarray(fields["is_lake"], dtype=bool).astype(np.uint8)
        entries.append(
            {
                "name": "lakes",
                "label": "호수",
                "file": "lakes.bin",
                "dtype": "uint8",
                "source": "is_lake",
                "sampling": "max_in_cell",
                "description": "물이 고여 수면이 지표보다 높은 육지 칸(호수)입니다. 1 이면 호수, "
                f"0 이면 아닙니다. 이 행성의 호수 L0 칸은 {int(lake.sum())}개입니다.",
                "how_to_read": "1 인 칸에 하늘색을 덧칠합니다.",
                "rgb": [90, 200, 250],
                "categories": [
                    {"id": 0, "label": "호수 아님", "rgb": None},
                    {"id": 1, "label": "호수", "rgb": [90, 200, 250]},
                ],
            }
        )
        arrays.append(rs.in_cell(lake, "max_in_cell"))
    return entries, arrays


# ---------------------------------------------------------------- 쓰기
def _write_bin_atomic(path: Path, arr: np.ndarray, dtype: str) -> int:
    data = np.ascontiguousarray(arr, dtype=np.dtype(dtype).newbyteorder("<")).tobytes()
    tmp = path.with_name(path.name + ".part")
    with open(tmp, "wb") as f:
        f.write(data)
    os.replace(tmp, path)
    return len(data)


def _old_files(folder: Path) -> set[str]:
    """이전에 이 폴더에 쓴 globe.json 이 가리키던 파일 이름들 (없으면 빈 집합)."""
    p = folder / GLOBE_JSON
    try:
        m = json.loads(p.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return set()
    if m.get("format") != FORMAT:
        return set()
    names = {e.get("file") for e in (m.get("fields") or []) + (m.get("overlays") or [])}
    names.add((m.get("corners") or {}).get("file"))
    return {n for n in names if isinstance(n, str) and "/" not in n and n.endswith(".bin")}


def copy_globe(src_dir, dst_dir, files: list[str]) -> None:
    """굽은 파일을 다른 폴더(보통 engine/baked/globe)에 복사합니다. globe.json 은 맨 끝에."""
    src, dst = Path(src_dir), Path(dst_dir)
    dst.mkdir(parents=True, exist_ok=True)
    stale = _old_files(dst) - set(files)
    for name in [*files, GLOBE_JSON]:
        tmp = dst / (name + ".part")
        shutil.copy2(src / name, tmp)
        os.replace(tmp, dst / name)
    for name in stale:
        (dst / name).unlink(missing_ok=True)


def face_res_error(face_res) -> str:
    """면당 칸 수가 범위 밖일 때의 알림 글."""
    return (
        f"face_res(면당 칸 수)는 2..{MAX_FACE_RES} 이어야 합니다: {face_res}. {MAX_FACE_RES} 를 "
        "넘으면 꼭짓점 방향·이웃 표만 수 GB 라 노트북 메모리가 모자라고, 엔진이 레이어 하나의 "
        "텍스처를 만드는 데도 몇 분이 걸립니다. L0 만큼 곱게 보려면 L0 면당 칸 수(laptop 512)면 "
        "충분합니다."
    )


def bake_globe(
    planet_state,
    cfg,
    out_dir,
    *,
    face_res: int = DEFAULT_FACE_RES,
    hero: dict | None = None,
    log=None,
    face_basis: dict | None = None,
    engine_dir=None,
) -> dict:
    """행성 상태를 지구본 파일로 굽습니다 (모듈 설명의 형식). 반환: globe.json 내용 dict.

    planet_state: graph(구면 CellGraph)와 fields 를 가진 PlanetState. cfg: 설정(시드·해시·노드
    흔들기·기후·강 문턱) 또는 None. out_dir: 출력 폴더. face_res: 면당 칸 수 N.
    hero: 행성 좌표 {unit, lat_deg, lon_deg, size_m} (hero_from_run 결과) 또는 None.
    face_basis: 묶음 manifest 의 face_basis (None 이면 write_bundle 이 적는 표).
    engine_dir: 주면 같은 파일을 그 폴더에도 씁니다. log: 진행 기록 함수 또는 None.
    """
    t_all = time.perf_counter()
    graph = getattr(planet_state, "graph", None)
    fields = getattr(planet_state, "fields", None)
    if graph is None or graph.kind != "sphere" or not isinstance(fields, dict):
        raise ValueError("planet_state 는 구면 그래프와 fields 를 가진 PlanetState 여야 합니다")
    z_name = "z_mean_m" if "z_mean_m" in fields else ("z_m" if "z_m" in fields else None)
    if z_name is None:
        raise ValueError("행성 필드에 고도(z_mean_m 또는 z_m)가 없습니다")
    res = int(face_res)
    if not 2 <= res <= MAX_FACE_RES:
        raise ValueError(face_res_error(face_res))
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    sec: dict[str, float] = {}

    t = time.perf_counter()
    bases = face_bases(face_basis)
    jitter = float(cfg.landscape.jitter) if cfg is not None else 1.0
    rotation = np.eye(3) if cfg is None else axis_rotation(cfg.planet.axis)
    if not np.allclose(rotation, np.eye(3), atol=1e-12):
        _say(log, "[지구본] 자전축이 z 가 아니라, 자전축이 Godot +Y(북쪽)가 되도록 지구본 좌표를 "
                  "돌립니다 (frame.axis_rotation)")  # fmt: skip
    check = check_bundle_mapping(graph, bases, jitter)
    unit = np.asarray(graph.pos, dtype=np.float64)
    unit = unit / np.linalg.norm(unit, axis=1, keepdims=True)
    rs = _Resampler(unit, bases, res)
    sec["index"] = time.perf_counter() - t
    _say(
        log,
        f"[지구본] 면당 {res}칸 ({6 * res * res}칸)으로 다시 담습니다. L0 면당 "
        f"{check['n_per_face']}칸, 사상 검사 최대 {check['max_error_cells']:.3f} 칸 어긋남 "
        f"(허용치의 {check['max_error_ratio']:.3f} 배), {sec['index']:.2f} s",
    )

    t = time.perf_counter()
    entries, arrays = _build_fields(fields, rs, graph, cfg, log)
    ov_entries, ov_arrays = _build_overlays(fields, rs, cfg)
    corners = rs.corners(np.asarray(fields[z_name], dtype=np.float64), bases)
    sec["resample"] = time.perf_counter() - t
    _say(
        log,
        f"[지구본] 필드 {len(entries)}개, 덧그림 {len(ov_entries)}개, 꼭짓점 고도를 만들었습니다: "
        f"{sec['resample']:.2f} s",
    )

    shape = [6, res, res]
    for e in entries + ov_entries:
        e["shape"] = shape
    if hero is not None and cfg is not None and hero.get("seed") is not None:
        if int(hero["seed"]) != int(cfg.planet.seed):
            _say(log, f"[지구본] 히어로 시드 {hero['seed']} 가 행성 시드와 달라 표시하지 않습니다")
            hero = None
        elif hero.get("config_digest") not in (None, cfg.digest()):
            _say(log, "[지구본] 경고: 히어로 설정 해시가 행성과 다릅니다 (위치는 그대로 씁니다)")
    hero_e = hero_entry(hero, rotation)

    t = time.perf_counter()
    sizes: dict[str, int] = {}
    for e, arr in zip(entries + ov_entries, arrays + ov_arrays, strict=True):
        sizes[e["file"]] = _write_bin_atomic(out / e["file"], arr.reshape(shape), e["dtype"])
    sizes[CORNERS_FILE] = _write_bin_atomic(out / CORNERS_FILE, corners, "float32")
    stale = _old_files(out) - set(sizes)
    sec["write"] = time.perf_counter() - t

    _, n_l0, _ = graph.shape
    l0_km = cell_km(graph.R, n_l0)
    globe_km = cell_km(graph.R, res)
    meta = {
        "format": FORMAT,
        "format_version": FORMAT_VERSION,
        "bpcg_version": __version__,
        "git_commit": git_commit(),
        "config_digest": None if cfg is None else cfg.digest(),
        "seed": None if cfg is None else int(cfg.planet.seed),
        "profile": None if cfg is None else cfg.profile.get("name"),
        "title": "행성 지구본",
        "description": (
            "생성한 행성 전체를 지구본처럼 돌려 보기 위한 자료입니다. 큐브스피어 면 6개를 "
            f"면마다 {res}×{res} 칸으로 나눠 칸마다 지형·판·기후·물·지질·동굴 값을 담았습니다. "
            f"원래 계산은 면당 {n_l0}칸(L0)에서 했습니다. 칸 크기를 같은 잣대(면 가운데 칸 "
            f"한 변)로 재면 L0 칸은 약 {l0_km:.1f} km, 지구본 칸은 약 {globe_km:.1f} km 입니다. "
            "구의 높낮이는 꼭짓점 고도로 만들고, 색은 고른 필드로 칠합니다."
        ),
        "frame": frame_entry(rotation),
        "radius_m": float(graph.R),
        "face_res": res,
        "faces": [
            {
                "index": f,
                "name": _face_name(bases[f, 0]),
                "n": [float(x) for x in to_godot(bases[f, 0], rotation)],
                "u": [float(x) for x in to_godot(bases[f, 1], rotation)],
                "v": [float(x) for x in to_godot(bases[f, 2], rotation)],
            }
            for f in range(6)
        ],
        "handedness": "u × v = n (n 은 구 바깥쪽)",
        "mapping": MAPPING,
        "mapping_inverse": MAPPING_INVERSE,
        "layout": LAYOUT,
        "sampling": SAMPLING,
        "corners": {
            "file": CORNERS_FILE,
            "dtype": "float32",
            "shape": [6, res + 1, res + 1],
            "unit": "m",
            "source": z_name,
            "min": _num(corners.min()),
            "max": _num(corners.max()),
            "description": f"칸 꼭짓점의 고도({z_name})입니다(해수면 0 m 기준, 바다는 음수). "
            "꼭짓점에서 가장 가까운 L0 칸 4개의 평균이고, 이웃 면이 같이 쓰는 꼭짓점은 같은 "
            "값이라 구 메시에 틈이 생기지 않습니다. 실제 높이는 반지름에 비해 아주 작으므로"
            "(0.1% 안팎) 보이게 하려면 과장 배율을 곱합니다.",
        },
        "sea_level_m": SEA_LEVEL_M,
        "nan_rgb": _rgb(NAN_RGB),
        "nan_label": "값 없음",
        "groups": list(GROUPS),
        "fields": entries,
        "overlays": ov_entries,
        "hero": hero_e,
        "source": {
            "n_per_face": int(n_l0),
            "spacing_m": float(graph.spacing),
            "cell_km_face_center": _num(l0_km),
            "elevation_field": z_name,
            "mapping_check": check,
        },
        "file_bytes": sizes,
        "seconds": {k: _num(v) for k, v in sec.items()},
    }
    sec["total"] = time.perf_counter() - t_all
    meta["seconds"]["total"] = _num(sec["total"])
    write_json(out / GLOBE_JSON, meta, max_array=None)
    for name in stale:
        (out / name).unlink(missing_ok=True)
    total_mb = sum(sizes.values()) / 1e6
    _say(log, f"[지구본] 파일 {len(sizes) + 1}개 ({total_mb:.1f} MB)를 썼습니다: {out}")
    if engine_dir is not None:
        copy_globe(out, engine_dir, list(sizes))
        _say(log, f"[지구본] 엔진 폴더에도 썼습니다: {engine_dir}")
    _say(log, f"[지구본] 끝: {sec['total']:.2f} s")
    return meta


# ---------------------------------------------------------------- 명령줄
def main(argv: list[str] | None = None) -> int:
    """`python -m bpcg.bake.globe --planet <행성 묶음> [--out DIR] [--engine] [--face-res N]`."""
    ap = argparse.ArgumentParser(
        prog="python -m bpcg.bake.globe", description="행성 묶음에서 지구본 파일을 굽습니다"
    )
    ap.add_argument("--planet", type=Path, required=True, help="행성 묶음 폴더 (out/<run>/planet)")
    ap.add_argument("--out", type=Path, default=None, help="출력 폴더 (기본 <planet>/../globe)")
    ap.add_argument("--engine", action="store_true", help="engine/baked/globe 에도 씀")
    ap.add_argument(
        "--face-res",
        type=int,
        default=DEFAULT_FACE_RES,
        help=f"면당 칸 수 N (2..{MAX_FACE_RES}, 기본 {DEFAULT_FACE_RES}; L0 만큼 곱게는 512)",
    )
    ap.add_argument("--no-hero", action="store_true", help="히어로 자리를 찾지 않음")
    args = ap.parse_args(argv)
    if not 2 <= args.face_res <= MAX_FACE_RES:
        ap.error(face_res_error(args.face_res))

    from bpcg.bake.bundle import config_from_manifest, load_planet_state, read_manifest

    def say(msg: str) -> None:
        print(msg, flush=True)

    t = time.perf_counter()
    planet_dir = args.planet
    man = read_manifest(planet_dir)
    cfg = config_from_manifest(man)
    planet = load_planet_state(planet_dir)
    say(f"[지구본] 행성 묶음을 읽었습니다: {planet_dir} ({time.perf_counter() - t:.2f} s)")
    run_dir = planet_dir.resolve().parent
    hero = None if args.no_hero else hero_from_run(run_dir)
    if hero is not None:
        say(f"[지구본] 히어로 자리: {hero['source']}")
    out = args.out if args.out is not None else run_dir / "globe"
    engine_dir = ROOT / "engine" / "baked" / "globe" if args.engine else None
    bake_globe(
        planet,
        cfg,
        out,
        face_res=args.face_res,
        hero=hero,
        log=say,
        face_basis=man.get("face_basis"),
        engine_dir=engine_dir,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
