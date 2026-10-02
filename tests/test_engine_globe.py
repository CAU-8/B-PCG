"""Godot 지구본 장면 연기 검사: engine/tests/globe_smoke.gd 를 화면 없이 돌립니다.

Godot 찾기와 임시 HOME 처리는 test_engine_smoke 의 것을 그대로 씁니다 (Godot 4.7.2 가 없으면
건너뜀). GDScript·셰이더 오류가 나도 Godot 는 0 으로 끝나므로 출력의 BPCG_GLOBE_OK 표시를 봅니다.

검사
- test_engine_globe: tiny 행성을 만들어 bpcg.bake.globe.bake_globe 로 <임시>/globe 에 굽고
  `--baked-dir=<임시>` 로 넘깁니다. 면 메시·틈·필드 고르기·사상 왕복·값 읽기·히어로 위경도·
  고도 과장·겹쳐 보기·키 입력·카메라·고정 상자를 globe_smoke.gd 가 봅니다.
  bake_globe 를 가져올 수 없으면 아래 대체 자료로 돌립니다.
- test_engine_globe_contract_fixture: 굽기 코드와 상관없이 globe.json 약속(engine/GLOBE.md)만으로
  쓴 작은 해석적 자료(NaN 칸, 로그 필드, 범주 14 개, color_break, 점으로 찍는 드문 범주와 그 안의
  rgb 가 null 인 범주·'값 없음' 범주 255, 문턱 아래 강 등급)를 엔진이 읽는지 봅니다.
- test_engine_globe_no_data: 빈 굽기 폴더로 열어 '자료가 없습니다' 알림이 뜨고 멈추지 않는지 봅니다.
- test_engine_globe_malformed: globe.json 은 있는데 .bin 크기가 틀린 자료로 열어 '읽지 못했습니다'
  알림(없다는 알림과 다름)이 뜨는지 봅니다.
- test_fixture_globe_follows_contract: 대체 자료 자체가 약속을 지키는지 (Godot 없이).
"""

import json
import math
import time
from pathlib import Path

import numpy as np
import pytest
from test_engine_smoke import (  # noqa: F401  (godot_env 는 pytest 고정물로 씀)
    IMPORT_TIMEOUT_S,
    SMOKE_TIMEOUT_S,
    _run,
    _tail,
    godot_env,
)

GLOBE_SMOKE = "res://tests/globe_smoke.gd"
# 다른 작업이 같은 프로젝트를 동시에 가져오기(import)하면 캐시가 잠겨 실패할 수 있어 다시 해 봅니다.
IMPORT_ATTEMPTS = 3
IMPORT_RETRY_WAIT_S = 30.0
# 히어로 자리로 쓸 tiny 행성 L0 칸 번호 (아무 칸이나 됨)
HERO_CELL = 4000
HERO_SIZE_M = 6_400.0


@pytest.fixture(scope="module")
def imported(godot_env):  # noqa: F811
    """스크립트를 더한 뒤 한 번 가져오기를 돌립니다 (class_name 등록, .uid 생성)."""
    godot, env = godot_env
    last = None
    for attempt in range(IMPORT_ATTEMPTS):
        last = _run(godot, env, ["--import"], IMPORT_TIMEOUT_S)
        if last.returncode == 0:
            return godot, env
        if attempt + 1 < IMPORT_ATTEMPTS:
            time.sleep(IMPORT_RETRY_WAIT_S)
    pytest.fail(f"--import 실패:\n{_tail(last)}")


def _globe_smoke(godot: Path, env: dict[str, str], baked_dir: Path, *extra: str) -> str:
    """globe_smoke.gd 를 돌리고 통과 표시와 오류 없음을 확인합니다. 반환: 표준 출력."""
    proc = _run(
        godot,
        env,
        ["--script", GLOBE_SMOKE, "--", f"--baked-dir={baked_dir.as_posix()}", *extra],
        SMOKE_TIMEOUT_S,
    )
    output = proc.stdout + proc.stderr
    assert "BPCG_GLOBE_OK" in proc.stdout, f"지구본 연기 검사 실패:\n{_tail(proc)}"
    assert proc.returncode == 0, f"종료 코드 {proc.returncode}:\n{_tail(proc)}"
    assert "SCRIPT ERROR" not in output, f"스크립트 오류:\n{_tail(proc)}"
    assert "SHADER ERROR" not in output, f"셰이더 오류:\n{_tail(proc)}"
    return proc.stdout


def _bake_tiny_globe(out: Path) -> dict:
    """tiny 행성을 만들어 out 에 지구본을 굽습니다. 반환: globe.json 내용."""
    try:
        from bpcg.bake.globe import bake_globe
    except ImportError:
        return write_fixture_globe(out)
    from bpcg.core.config import load_config
    from bpcg.pipeline import generate_planet

    cfg = load_config("earth", "tiny")
    planet = generate_planet(cfg, log=None)
    p = np.asarray(planet.graph.pos[HERO_CELL], dtype=np.float64)
    hero = {"unit": (p / np.linalg.norm(p)).tolist(), "size_m": HERO_SIZE_M}
    return bake_globe(planet, cfg, out, hero=hero, log=None)


@pytest.mark.godot
def test_engine_globe(imported, tmp_path):
    godot, env = imported
    baked = tmp_path / "baked"
    meta = _bake_tiny_globe(baked / "globe")
    assert (baked / "globe" / "globe.json").is_file()

    out = _globe_smoke(godot, env, baked, "--expect-globe")
    assert "BPCG_GLOBE_OK (no data)" not in out
    n = meta["face_res"]
    assert f"면 메시: 6 × ({n}+1)² 꼭짓점, 앞면 바깥" in out
    assert f"필드 {len(meta['fields'])} 개 모두 고름" in out
    assert f"고정 상자: 필드 {len(meta['fields'])} 개와 겹쳐 보기 {len(meta['overlays'])} 개" in out
    if meta.get("hero"):
        assert "히어로: globe.json 에 없음" not in out
        assert "이름표 '히어로 유역 (" in out


@pytest.mark.godot
def test_engine_globe_contract_fixture(imported, tmp_path):
    godot, env = imported
    baked = tmp_path / "baked"
    meta = write_fixture_globe(baked / "globe")
    out = _globe_smoke(godot, env, baked, "--expect-globe")
    assert f"필드 {len(meta['fields'])} 개 모두 고름" in out
    assert "이름표 '히어로 유역 (" in out


@pytest.mark.godot
def test_engine_globe_no_data(imported, tmp_path):
    godot, env = imported
    empty = tmp_path / "no_bake"
    empty.mkdir()
    out = _globe_smoke(godot, env, empty)
    assert "BPCG_GLOBE_OK (no data)" in out
    assert "지구본 자료가 없습니다" in out


@pytest.mark.godot
def test_engine_globe_malformed(imported, tmp_path):
    godot, env = imported
    baked = tmp_path / "baked"
    meta = write_fixture_globe(baked / "globe", n=8)
    bad = baked / "globe" / meta["fields"][0]["file"]
    bad.write_bytes(bad.read_bytes()[:-4])  # 칸 하나 모자람
    out = _globe_smoke(godot, env, baked)
    assert "BPCG_GLOBE_OK (no data)" in out
    assert "자료 없음 알림: 지구본 자료를 읽지 못했습니다" in out
    assert "지구본 자료가 없습니다" not in out


# ---------------------------------------------------------------- 대체 자료
# bpcg.bake.globe 를 가져올 수 없을 때만 씁니다. globe.json 약속(engine/GLOBE.md)을 따르는
# 작은 해석적 행성입니다: 고도 = 위경도의 사인 무늬, 판 = 경도 띠 14 개, 해양저 나이는 바다만.

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


def test_fixture_globe_follows_contract(tmp_path):
    """대체 자료가 약속(크기·면 기저 오른손 규칙)을 지키는지 봅니다 (Godot 없이)."""
    meta = write_fixture_globe(tmp_path, n=8)
    n = meta["face_res"]
    for f in meta["faces"]:
        nv, u, v = (np.asarray(f[k]) for k in ("n", "u", "v"))
        assert np.allclose(np.cross(u, v), nv)
    for e in meta["fields"] + meta["overlays"]:
        item = 4 if e["dtype"] == "float32" else 1
        assert (tmp_path / e["file"]).stat().st_size == 6 * n * n * item
        if e.get("colormap"):  # 색표 값은 엄격히 늘어남 (해수면의 끊김은 -0.5 m 매듭과 color_break)
            vals = [k[0] for k in e["colormap"]]
            assert all(b > a for a, b in zip(vals, vals[1:], strict=False)), e["name"]
    caves = next(e for e in meta["fields"] if e["name"] == "caves")
    assert caves["point_markers"] and caves["n_nonzero_cells"] > 0
    assert (tmp_path / meta["corners"]["file"]).stat().st_size == 6 * (n + 1) ** 2 * 4
