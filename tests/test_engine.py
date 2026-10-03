"""Godot 엔진(engine/) 연기 검사: Godot 4.7.2 .NET 판을 화면 없이 돌립니다.

엔진 안에서 노드를 들여다보는 검사 장면(engine/Tests/smoke.tscn, globe_smoke.tscn)은 Godot
프로세스 안에서 돌아야 해서 C# 입니다. 이 파일이 빌드, 가져오기(import), 엔진 안 생성, 검사 장면
실행을 맡고, 출력의 BPCG_* 표시와 굽기 결과 파일을 맞대어 판정합니다.

Godot .NET 판을 아래 순서로 찾고, 없거나 dotnet 이 없으면 건너뜁니다.
환경 변수 GODOT_NET → .tools/godot-net/ 아래 (macOS 앱, 리눅스, Windows 콘솔판)
→ 환경 변수 GODOT (버전에 .mono 가 있을 때만).
Godot 는 사용자 폴더에 설정과 user:// 를 쓰므로 HOME (리눅스 XDG_*, 윈도우 APPDATA·LOCALAPPDATA)
을 임시 폴더로 바꿉니다. 엔진 안 생성 결과도 그 임시 user:// 에 쌓입니다.

검사
- test_engine_imports_cleanly: 가져오기가 프로젝트 폴더(.godot/ 밖)에 .uid·.import 만 만듦.
- test_engine_without_bake: 빈 굽기 폴더로 회랑(표본 지형만)과 지구본('자료가 없습니다').
- test_engine_generates_and_loads: 엔진 안에서 tiny 를 만들고(처음 화면의 --generate),
  그 실행으로 회랑·지구본 검사를 돌려 레이어·동굴 삼각형·입구 수·지구본 필드 수가 굽기 결과와
  같은지 봄.
- test_engine_matches_cli: 엔진 안 생성 결과가 C# 콘솔(Bpcg.Cli all)과 파일까지 같은지 봄
  (manifest 의 걸린 시간 'seconds' 만 뺌).
- test_engine_loads_console_bake: 콘솔로 구운 동굴 회랑과 지구본 약속 대체 자료도 읽는지 봄.
- test_engine_globe_malformed: .bin 크기가 틀린 지구본에 '읽지 못했습니다' 알림이 뜨는지 봄.
- test_fixture_globe_follows_contract: 대체 자료 자체가 약속을 지키는지 (Godot 없이).
"""

import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np
import pytest
from globe_fixture import write_fixture_globe

from bpcg_studio.paths import ROOT

pytestmark = pytest.mark.godot

PROJECT = ROOT / "engine"
GODOT_VERSION = "4.7.2"
BUILD_TIMEOUT_S = 900
IMPORT_TIMEOUT_S = 600
GENERATE_TIMEOUT_S = 600
SCENE_TIMEOUT_S = 300
SMOKE_SCENE = "res://Tests/smoke.tscn"
GLOBE_SCENE = "res://Tests/globe_smoke.tscn"
GENERATE_OK = re.compile(r"^BPCG_GENERATE_OK (.+)$", re.MULTILINE)
# 출력에 있으면 실패로 보는 글 (C# 예외는 Godot 가 'ERROR:' 줄로 찍음)
ERROR_MARKS = ("SCRIPT ERROR", "SHADER ERROR", "ERROR:")


def _isolated_env(tmp: Path) -> dict[str, str]:
    """Godot 가 사용자 설정 폴더 대신 임시 폴더를 쓰게 하는 환경 변수."""
    env = os.environ.copy()
    home = tmp / "home"
    home.mkdir(parents=True, exist_ok=True)
    env["HOME"] = str(home)
    if sys.platform == "win32":
        for name in ("APPDATA", "LOCALAPPDATA"):
            d = tmp / name.lower()
            d.mkdir(exist_ok=True)
            env[name] = str(d)
    else:
        # 리눅스는 XDG_* 가 있으면 HOME 보다 먼저 봅니다 (macOS 는 무시합니다).
        for name in ("XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME"):
            d = tmp / name.lower()
            d.mkdir(exist_ok=True)
            env[name] = str(d)
    return env


def _tail(proc: subprocess.CompletedProcess, n: int = 40) -> str:
    lines = (proc.stdout + "\n" + proc.stderr).splitlines()
    return "\n".join(lines[-n:])


def _candidates() -> list[Path]:
    paths = []
    if env := os.environ.get("GODOT_NET"):
        paths.append(Path(env))
    tools = ROOT / ".tools" / "godot-net"
    tag = f"Godot_v{GODOT_VERSION}-stable_mono"
    paths += [
        tools / "Godot_mono.app" / "Contents" / "MacOS" / "Godot",
        tools / f"{tag}_linux_x86_64" / f"{tag}_linux.x86_64",
        tools / f"{tag}_linux_arm64" / f"{tag}_linux.arm64",
        tools / f"{tag}_win64" / f"{tag}_win64_console.exe",
        tools / f"{tag}_windows_arm64" / f"{tag}_windows_arm64_console.exe",
    ]
    if env := os.environ.get("GODOT"):
        paths.append(Path(env))
    return paths


def _version(godot: Path, env: dict[str, str]) -> str:
    try:
        out = subprocess.run(
            [str(godot), "--headless", "--version"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=env,
            timeout=60,
        )
    except (OSError, subprocess.TimeoutExpired):
        return ""
    return out.stdout.strip().splitlines()[-1] if out.stdout.strip() else ""


def _proc(args: list[str], env: dict[str, str] | None, timeout: int, cwd: Path = ROOT):
    return subprocess.run(
        args,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
        timeout=timeout,
        cwd=cwd,
    )


def _godot(godot: Path, env: dict[str, str], args: list[str], timeout: int):
    return _proc([str(godot), "--headless", "--path", str(PROJECT), *args], env, timeout)


def _project_files() -> set[Path]:
    """engine/ 아래 파일 목록 (.godot/ 과 git 에서 빠진 baked/ 는 빼고)."""
    files = set()
    skip = {PROJECT / ".godot", PROJECT / "baked"}
    for dirpath, dirnames, filenames in os.walk(PROJECT):
        d = Path(dirpath)
        dirnames[:] = [n for n in dirnames if d / n not in skip]
        files.update(d / f for f in filenames)
    return files


def _check_clean(proc: subprocess.CompletedProcess, what: str) -> str:
    output = proc.stdout + proc.stderr
    for mark in ERROR_MARKS:
        assert mark not in output, f"{what}: 출력에 '{mark}' 가 있습니다:\n{_tail(proc)}"
    return proc.stdout


@pytest.fixture(scope="module")
def engine(tmp_path_factory):
    """Godot .NET 판을 찾고, C# 을 빌드하고, 가져오기를 한 번 돌립니다.

    반환: (godot, 임시 HOME 환경, 가져오기가 프로젝트 폴더에 새로 만든 파일).
    """
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        pytest.skip("dotnet 이 없습니다 (.NET 10 SDK 필요)")
    env = _isolated_env(tmp_path_factory.mktemp("godot_net_user"))
    godot = None
    seen = []
    for path in _candidates():
        if not path.is_file():
            continue
        version = _version(path, env)
        if version.startswith(GODOT_VERSION) and ".mono." in version:
            godot = path
            break
        seen.append(f"{path} ({version or '버전 확인 실패'})")
    if godot is None:
        if seen:
            pytest.skip(f"Godot {GODOT_VERSION} .NET 판이 아닙니다: {', '.join(seen)}")
        pytest.skip(f"Godot {GODOT_VERSION} .NET 판이 없습니다 (환경 변수 GODOT_NET 로 지정)")

    # 빌드는 평소 환경으로 합니다 (NuGet 캐시가 HOME 아래에 있음). 경고도 오류로 다룹니다.
    built = _proc(
        [dotnet, "build", str(PROJECT / "Bpcg.Engine.csproj"), "-c", "Debug", "-nologo"],
        None,
        BUILD_TIMEOUT_S,
    )
    assert built.returncode == 0, f"dotnet build 실패:\n{_tail(built, 60)}"
    before = _project_files()
    imported = _godot(godot, env, ["--import"], IMPORT_TIMEOUT_S)
    assert imported.returncode == 0, f"--import 실패:\n{_tail(imported)}"
    after = _project_files()
    return godot, env, sorted(p.relative_to(ROOT).as_posix() for p in after - before)


@pytest.fixture(scope="module")
def generated_run(engine) -> Path:
    """엔진 안에서 tiny 를 만듭니다 (처음 화면을 명령줄로 몸). 반환: 실행 폴더."""
    godot, env, _ = engine
    proc = _godot(
        godot,
        env,
        ["--", "--generate", "--profile=tiny", "--seed=0", "--quit-when-done"],
        GENERATE_TIMEOUT_S,
    )
    out = _check_clean(proc, "엔진 안 생성")
    assert proc.returncode == 0, f"종료 코드 {proc.returncode}:\n{_tail(proc)}"
    m = GENERATE_OK.search(out)
    assert m, f"BPCG_GENERATE_OK 가 없습니다:\n{_tail(proc)}"
    assert "[전체] 끝" in out
    return Path(m.group(1).strip())


def _scene(engine, scene: str, *args: str, ok: str) -> str:
    """검사 장면을 돌리고 통과 표시와 오류 없음을 확인합니다. 반환: 표준 출력."""
    godot, env, _ = engine
    proc = _godot(godot, env, [scene, "--", *args], SCENE_TIMEOUT_S)
    out = _check_clean(proc, scene)
    assert ok in out, f"{scene} 실패:\n{_tail(proc)}"
    assert proc.returncode == 0, f"종료 코드 {proc.returncode}:\n{_tail(proc)}"
    return out


def _baked(path: Path) -> str:
    return f"--baked-dir={path.as_posix()}"


def _drop_seconds(value):
    """JSON 값에서 걸린 시간 키('seconds')를 모두 뺍니다."""
    if isinstance(value, dict):
        return {k: _drop_seconds(v) for k, v in value.items() if k != "seconds"}
    if isinstance(value, list):
        return [_drop_seconds(v) for v in value]
    return value


def _files(root: Path) -> set[str]:
    return {p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file()}


def test_engine_imports_cleanly(engine):
    _, _, new = engine
    junk = [p for p in new if not p.endswith((".uid", ".import"))]
    assert not junk, f"Godot 가 프로젝트 폴더에 예상하지 못한 파일을 만들었습니다: {junk}"


def test_engine_without_bake(engine, tmp_path):
    empty = tmp_path / "no_bake"
    empty.mkdir()
    out = _scene(engine, SMOKE_SCENE, _baked(empty), ok="BPCG_SMOKE_OK")
    assert "표본 지형만 검사했습니다" in out
    out = _scene(engine, GLOBE_SCENE, _baked(empty), ok="BPCG_GLOBE_OK (no data)")
    assert "자료 없음 알림: 지구본 자료가 없습니다" in out


def test_engine_generates_and_loads(engine, generated_run):
    corridor = generated_run / "corridor"
    for sub in ("planet", "hero", "corridor", "globe"):
        assert (generated_run / sub).is_dir(), f"실행 폴더에 {sub}/ 가 없습니다"
    man = json.loads((corridor / "manifest.json").read_text(encoding="utf-8"))
    globe = json.loads((generated_run / "globe" / "globe.json").read_text(encoding="utf-8"))

    out = _scene(engine, SMOKE_SCENE, _baked(corridor), "--expect-baked", ok="BPCG_SMOKE_OK")
    assert "레이어: terrain, surround, water, water_table, caves, section" in out
    assert f"동굴 삼각형 {man['file_meta']['caves']['faces']} (gltf_runtime)" in out
    assert f"입구 {man['caves']['n_entrances']}," in out
    assert "프랙탈 디테일: 켜짐 (처음)" in out

    # 굽기 폴더가 실행의 corridor/ 이면 지구본은 옆의 globe/ 에서 찾습니다.
    out = _scene(engine, GLOBE_SCENE, _baked(corridor), "--expect-globe", ok="BPCG_GLOBE_OK")
    assert "BPCG_GLOBE_OK (no data)" not in out
    n = globe["face_res"]
    assert f"면 메시: 6 × ({n}+1)² 꼭짓점, 앞면 바깥" in out
    assert f"필드 {len(globe['fields'])} 개 모두 고름" in out
    assert (
        f"고정 상자: 필드 {len(globe['fields'])} 개와 겹쳐 보기 {len(globe['overlays'])} 개" in out
    )
    if globe.get("hero"):
        assert "이름표 '히어로 유역 (" in out


def test_engine_matches_cli(engine, generated_run, csharp_cli, tmp_path):
    cli = tmp_path / "cli"
    csharp_cli("all", "--profile", "tiny", "--seed", "0", "--out", cli)

    names = _files(generated_run)
    assert names == _files(cli), "엔진과 콘솔이 쓴 파일 목록이 다릅니다"
    different = []
    for name in sorted(names):
        a, b = generated_run / name, cli / name
        if name.endswith(".json"):
            ja = _drop_seconds(json.loads(a.read_text(encoding="utf-8")))
            jb = _drop_seconds(json.loads(b.read_text(encoding="utf-8")))
            same = ja == jb
        else:
            same = a.read_bytes() == b.read_bytes()
        if not same:
            different.append(name)
    assert not different, f"엔진 안 생성과 콘솔 결과가 다른 파일: {different}"


def test_engine_loads_console_bake(engine, cave_run, tmp_path):
    """C# 콘솔로 구운 동굴 회랑과 지구본 약속 대체 자료(굽기 코드와 상관없음)를 엔진이 읽습니다."""
    baked = tmp_path / "baked"
    shutil.copytree(cave_run / "corridor", baked)
    man = json.loads((baked / "manifest.json").read_text(encoding="utf-8"))
    assert "caves.glb" in man["files"] and man["caves"]["n_entrances"] > 0
    meta = write_fixture_globe(baked / "globe")

    out = _scene(engine, SMOKE_SCENE, _baked(baked), "--expect-baked", ok="BPCG_SMOKE_OK")
    assert f"동굴 삼각형 {man['file_meta']['caves']['faces']} (gltf_runtime)" in out
    assert f"입구 {man['caves']['n_entrances']}," in out
    out = _scene(engine, GLOBE_SCENE, _baked(baked), "--expect-globe", ok="BPCG_GLOBE_OK")
    assert f"필드 {len(meta['fields'])} 개 모두 고름" in out
    assert "이름표 '히어로 유역 (" in out


def test_engine_globe_malformed(engine, tmp_path):
    baked = tmp_path / "baked"
    meta = write_fixture_globe(baked / "globe", n=8)
    bad = baked / "globe" / meta["fields"][0]["file"]
    bad.write_bytes(bad.read_bytes()[:-4])  # 칸 하나 모자람
    out = _scene(engine, GLOBE_SCENE, _baked(baked), ok="BPCG_GLOBE_OK (no data)")
    assert "자료 없음 알림: 지구본 자료를 읽지 못했습니다" in out
    assert "지구본 자료가 없습니다" not in out


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
        if e.get("colormap"):  # 색표 값은 엄격히 늘어남
            vals = [k[0] for k in e["colormap"]]
            assert all(b > a for a, b in zip(vals, vals[1:], strict=False)), e["name"]
    caves = next(e for e in meta["fields"] if e["name"] == "caves")
    assert caves["point_markers"] and caves["n_nonzero_cells"] > 0
    assert (tmp_path / meta["corners"]["file"]).stat().st_size == 6 * (n + 1) ** 2 * 4
