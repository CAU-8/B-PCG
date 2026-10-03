"""지구본 약속 대체 자료: globe.json 약속(engine/GLOBE.md)만으로 쓴 작은 해석적 행성.

굽기 코드와 상관없이 엔진이 약속을 읽는지 볼 때 씁니다 (tests/test_engine.py).
고도 = 위경도의 사인 무늬, 판 = 경도 띠 14 개, 해양저 나이는 바다만, NaN 칸, 로그 필드, 범주 14 개,
color_break, 점으로 찍는 드문 범주와 그 안의 rgb 가 null 인 범주·'값 없음' 범주 255, 문턱 아래
강 등급을 담습니다.
"""

import json
import math
from pathlib import Path

import numpy as np

HERO_SIZE_M = 6_400.0


# 행성 좌표의 면 기저 (cubesphere.FACE_N/U/V 와 같음)
_FACE_N = np.array([[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1]], float)
_FACE_U = np.array([[0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1], [1, 0, 0], [-1, 0, 0]], float)
_FACE_V = np.array([[0, 0, 1], [0, 0, 1], [1, 0, 0], [1, 0, 0], [0, 1, 0], [0, 1, 0]], float)


def _to_godot(v: np.ndarray) -> np.ndarray:
    v = np.asarray(v, dtype=np.float64)
    return np.stack([v[..., 0], v[..., 2], -v[..., 1]], axis=-1) + 0.0


def _directions(n: int, corners: bool) -> np.ndarray:
    """(6, R, R, 3) 행성 좌표 단위 방향. 열 = a(u), 행 = b(v)."""
    t = -1.0 + 2.0 * (np.arange(n + 1) if corners else np.arange(n) + 0.5) / n
    a, b = np.meshgrid(t, t, indexing="xy")
    x = np.tan(a * np.pi / 4.0)[None, :, :, None]
    y = np.tan(b * np.pi / 4.0)[None, :, :, None]
    d = _FACE_N[:, None, None] + x * _FACE_U[:, None, None] + y * _FACE_V[:, None, None]
    return d / np.linalg.norm(d, axis=-1, keepdims=True)


def _elevation_m(d: np.ndarray) -> np.ndarray:
    lat = np.arcsin(np.clip(d[..., 2], -1.0, 1.0))
    lon = np.arctan2(d[..., 1], d[..., 0])
    return 4000.0 * np.sin(3.0 * lon) * np.cos(2.0 * lat) - 1000.0


def write_fixture_globe(out: Path, n: int = 32) -> dict:
    """globe.json 약속대로 작은 지구본 자료를 씁니다. 반환: globe.json 내용."""
    out.mkdir(parents=True, exist_ok=True)
    cells = _directions(n, corners=False)
    z = _elevation_m(cells)
    lon = np.degrees(np.arctan2(cells[..., 1], cells[..., 0]))
    plates = np.floor((lon + 180.0) / (360.0 / 14.0)).clip(0, 13).astype(np.uint8)
    age = np.where(z < 0.0, (lon + 180.0) / 2.0, np.nan)
    discharge = np.where(z >= 0.0, np.log10(1.0 + np.maximum(z, 0.0)), np.nan)
    rivers = np.where((z > 0.0) & (z < 300.0), 3, 0).astype(np.uint8)
    lakes = ((z > 2500.0) & (z < 2600.0)).astype(np.uint8)
    # 드문 범주 (점으로 찍음): 육지 칸 몇 개만 1, 하나는 rgb 가 null 인 범주 2, 바다는 '값 없음' 255
    rare = np.where(z < 0.0, 255, 0).astype(np.uint8)
    land_cells = np.flatnonzero(z.ravel() >= 0.0)
    rare.ravel()[land_cells[:: max(land_cells.size // 6, 1)][:6]] = 1
    rare.ravel()[land_cells[-1]] = 2
    corners = _elevation_m(_directions(n, corners=True))

    def write(name: str, arr: np.ndarray, dtype: str) -> str:
        (out / f"{name}.bin").write_bytes(np.ascontiguousarray(arr, dtype="<" + dtype).tobytes())
        return f"{name}.bin"

    shape = [6, n, n]
    land = [[0.0, [58, 120, 62]], [3000.0, [250, 250, 250]]]
    fields = [
        {
            "name": "elevation", "label": "고도", "group": "지형", "unit": "m",
            "file": write("elevation", z, "f4"), "dtype": "float32", "shape": shape,
            "kind": "continuous", "min": -5000.0, "max": 3000.0, "log": False,
            "colormap": [[-5000.0, [8, 29, 72]], [-0.5, [165, 215, 240]], *land],
            "color_break": 0.0,
            "description": "대체 자료의 고도입니다.", "how_to_read": "파랑은 바다입니다.",
        },
        {
            "name": "plate_id", "label": "판", "group": "판·지각", "unit": "",
            "file": write("plate_id", plates, "u1"), "dtype": "uint8", "shape": shape,
            "kind": "categorical",
            "categories": [
                {"id": k, "label": f"판 {k}", "rgb": [(37 * k) % 256, (91 * k) % 256, 160]}
                for k in range(14)
            ],
            "description": "경도 띠로 나눈 판입니다.", "how_to_read": "색마다 다른 판입니다.",
        },
        {
            "name": "caves", "label": "동굴", "group": "동굴", "unit": "",
            "file": write("caves", rare, "u1"), "dtype": "uint8", "shape": shape,
            "kind": "categorical", "point_markers": True,
            "n_nonzero_cells": int(np.count_nonzero((rare != 0) & (rare != 255))),
            "area_fraction_basis": "land",
            "area_fraction_note": "넓이 몫(%)은 육지 넓이에 대한 몫입니다 (바다 칸은 빼고 셈).",
            "categories": [
                {"id": 0, "label": "없음", "rgb": [232, 232, 228], "area_fraction": 0.9},
                {"id": 1, "label": "동굴", "rgb": [0, 160, 190], "area_fraction": 0.09},
                {"id": 2, "label": "색 없는 범주", "rgb": None, "area_fraction": 0.01},
                {"id": 255, "label": "바다 (계산 안 함)", "rgb": [128, 128, 128],
                 "area_fraction": None, "no_data": True},
            ],
            "description": "드문 칸을 점으로도 찍는 범주입니다.",
            "how_to_read": "청록 점이 동굴입니다.",
        },
        {
            "name": "ocean_age_myr", "label": "해양저 나이", "group": "해양", "unit": "Myr",
            "file": write("ocean_age_myr", age, "f4"), "dtype": "float32", "shape": shape,
            "kind": "continuous", "min": 0.0, "max": 180.0, "log": False,
            "colormap": [[0.0, [165, 0, 38]], [180.0, [49, 54, 149]]], "nan_label": "대륙",
            "description": "바다에만 있는 값입니다.", "how_to_read": "회색은 대륙입니다.",
        },
        {
            "name": "discharge_log10_m3_per_s", "label": "강 유량", "group": "물",
            "unit": "log10(m³/s)", "file": write("discharge_log10_m3_per_s", discharge, "f4"),
            "dtype": "float32", "shape": shape, "kind": "continuous", "min": 0.0, "max": 4.0,
            "log": True, "colormap": [[0.0, [68, 1, 84]], [4.0, [253, 231, 37]]],
            "nan_label": "바다", "description": "유량의 상용로그입니다.",
            "how_to_read": "노랑일수록 큰 강입니다.",
        },
    ]  # fmt: skip
    overlays = [
        {
            "name": "rivers", "label": "강", "file": write("rivers", rivers, "u1"),
            "dtype": "uint8", "shape": shape, "description": "강 칸입니다.",
            "how_to_read": "파랗게 덧칠합니다.", "rgb": [10, 50, 170],
            "categories": [{"id": 0, "label": "강 아님", "rgb": None},
                           {"id": 3, "label": "큰 강", "rgb": [30, 100, 210]},
                           {"id": 4, "label": "아주 큰 강", "rgb": [10, 50, 170]}],
        },
        {
            "name": "lakes", "label": "호수", "file": write("lakes", lakes, "u1"),
            "dtype": "uint8", "shape": shape, "description": "호수 칸입니다.",
            "how_to_read": "하늘색으로 덧칠합니다.", "rgb": [90, 200, 250],
        },
    ]  # fmt: skip
    hero_planet = np.array([0.6, 0.5, 0.62])
    hero_planet /= np.linalg.norm(hero_planet)
    meta = {
        "format": "bpcg-globe",
        "format_version": 1,
        "bpcg_version": None,
        "git_commit": None,
        "config_digest": None,
        "seed": None,
        "frame": {
            "rule": "godot = (planet_x, planet_z, -planet_y); planet axis z = north = Godot +Y",
            "units": "unit sphere directions",
        },
        "radius_m": 6_371_000.0,
        "face_res": n,
        "faces": [
            {
                "index": f,
                "n": _to_godot(_FACE_N[f]).tolist(),
                "u": _to_godot(_FACE_U[f]).tolist(),
                "v": _to_godot(_FACE_V[f]).tolist(),
            }
            for f in range(6)
        ],
        "mapping": "equiangular (engine/GLOBE.md)",
        "corners": {
            "file": write("corners_elevation_m", corners, "f4"),
            "dtype": "float32",
            "shape": [6, n + 1, n + 1],
            "unit": "m",
            "description": "꼭짓점 고도입니다.",
        },
        "sea_level_m": 0.0,
        "fields": fields,
        "overlays": overlays,
        "hero": {
            "unit": _to_godot(hero_planet).tolist(),
            "lat_deg": math.degrees(math.asin(hero_planet[2])),
            "lon_deg": math.degrees(math.atan2(hero_planet[1], hero_planet[0])),
            "size_m": HERO_SIZE_M,
            "label": "히어로 유역",
        },
    }
    (out / "globe.json").write_text(json.dumps(meta, ensure_ascii=False), encoding="utf-8")
    return meta
