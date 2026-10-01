"""Godot 엔진 연기 검사(smoke test): engine/tests/smoke.gd 를 화면 없이 돌립니다.

Godot 4.7.2 실행 파일을 아래 순서로 찾고, 없으면 건너뜁니다.
환경 변수 GODOT → .tools/godot/ 아래 (macOS 앱, 리눅스, Windows 콘솔판)
→ /Applications/Godot.app → PATH 의 godot.

Godot 는 편집기 설정을 사용자 폴더에 씁니다. 실제 설정을 덮어쓰지 않도록
HOME (Windows 는 APPDATA, LOCALAPPDATA 도) 을 임시 폴더로 바꿔 실행합니다.
GDScript 나 셰이더 오류가 나도 Godot 는 0 으로 끝나므로 출력의 BPCG_SMOKE_OK 표시를 봅니다.

검사는 둘입니다.
- test_engine_smoke: 빈 굽기 폴더를 줘서 표본 지형만으로 돕니다 (engine/baked 에 무엇이 있든 같음).
- test_engine_loads_baked_corridor: tiny 히어로를 임시 폴더에 구워 `--baked-dir` 로 넘기고,
  레이어(주변 지형, 수면, 지하수면, 동굴, 단면) 불러오기와 보이기·숨기기를 검사합니다.
  동굴 메시는 편집기 가져오기 없이 실행 중 glTF 로 읽는 경로를 탑니다.
"""

import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

from bpcg.core.paths import ROOT

pytestmark = pytest.mark.godot

ENGINE = ROOT / "engine"
GODOT_VERSION = "4.7.2"
IMPORT_TIMEOUT_S = 600
SMOKE_TIMEOUT_S = 180


def _candidates() -> list[Path]:
    paths = []
    if env := os.environ.get("GODOT"):
        paths.append(Path(env))
    tools = ROOT / ".tools" / "godot"
    paths += [
        tools / "Godot.app" / "Contents" / "MacOS" / "Godot",
        tools / "godot",
        tools / f"Godot_v{GODOT_VERSION}-stable_win64_console.exe",
        tools / f"Godot_v{GODOT_VERSION}-stable_windows_arm64_console.exe",
        Path("/Applications/Godot.app/Contents/MacOS/Godot"),
    ]
    if found := shutil.which("godot"):
        paths.append(Path(found))
    return paths


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


@pytest.fixture(scope="module")
def godot_env(tmp_path_factory) -> tuple[Path, dict[str, str]]:
    env = _isolated_env(tmp_path_factory.mktemp("godot_user"))
    seen = []
    for path in _candidates():
        if not path.is_file():
            continue
        version = _version(path, env)
        if version.startswith(GODOT_VERSION):
            return path, env
        seen.append(f"{path} ({version or '버전 확인 실패'})")
    if seen:
        pytest.skip(f"Godot {GODOT_VERSION} 가 아닙니다: {', '.join(seen)}")
    pytest.skip(f"Godot {GODOT_VERSION} 실행 파일이 없습니다 (환경 변수 GODOT 로 지정)")


def _run(godot: Path, env: dict[str, str], args: list[str], timeout: int):
    return subprocess.run(
        [str(godot), "--headless", "--path", str(ENGINE), *args],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
        timeout=timeout,
        cwd=ROOT,
    )


def _tail(proc: subprocess.CompletedProcess, n: int = 40) -> str:
    lines = (proc.stdout + "\n" + proc.stderr).splitlines()
    return "\n".join(lines[-n:])


def _engine_files() -> set[Path]:
    """engine/ 아래 파일 목록 (.godot/ 과 baked/ 는 빼고)."""
    skip = {ENGINE / ".godot", ENGINE / "baked"}
    files = set()
    for dirpath, dirnames, filenames in os.walk(ENGINE):
        d = Path(dirpath)
        dirnames[:] = [n for n in dirnames if d / n not in skip]
        files.update(d / f for f in filenames)
    return files


def _smoke(godot: Path, env: dict[str, str], user_args: list[str]) -> str:
    """smoke.gd 를 돌리고 통과 표시와 오류 없음을 확인합니다. 반환: 표준 출력."""
    smoke = _run(
        godot, env, ["--script", "res://tests/smoke.gd", "--", *user_args], SMOKE_TIMEOUT_S
    )
    output = smoke.stdout + smoke.stderr
    assert "BPCG_SMOKE_OK" in smoke.stdout, f"연기 검사 실패:\n{_tail(smoke)}"
    assert smoke.returncode == 0, f"종료 코드 {smoke.returncode}:\n{_tail(smoke)}"
    assert "SCRIPT ERROR" not in output, f"스크립트 오류:\n{_tail(smoke)}"
    assert "SHADER ERROR" not in output, f"셰이더 오류:\n{_tail(smoke)}"
    return smoke.stdout


def test_engine_smoke(godot_env, tmp_path):
    godot, env = godot_env
    before = _engine_files()

    imported = _run(godot, env, ["--import"], IMPORT_TIMEOUT_S)
    assert imported.returncode == 0, f"--import 실패:\n{_tail(imported)}"

    empty = tmp_path / "no_bake"
    empty.mkdir()
    out = _smoke(godot, env, [f"--baked-dir={empty.as_posix()}"])
    assert "표본 지형만 검사했습니다" in out

    # 가져오기(import)는 .godot/ 밖에 .uid, .import 만 만들어야 합니다 (둘 다 커밋 대상).
    new = sorted(p.relative_to(ROOT).as_posix() for p in _engine_files() - before)
    junk = [p for p in new if not p.endswith((".uid", ".import"))]
    assert not junk, f"Godot 가 engine/ 에 예상하지 못한 파일을 만들었습니다: {junk}"


def test_engine_loads_baked_corridor(godot_env, tmp_path):
    """tiny 히어로를 구워 엔진이 모든 레이어를 불러오고 켜고 끄는지 봅니다."""
    from bpcg.bake.corridor import bake_corridor
    from bpcg.core.config import load_config
    from bpcg.pipeline import generate_hero

    godot, env = godot_env
    # test_bake.HERO_OVERRIDES 와 같은 까닭으로 선상지 호수와 동굴이 생기는 조건을 고정합니다.
    overrides = {"landscape.slope_exponent_n": 1.0, "rivers.min_discharge_m3_per_s": 0.02}
    cfg = load_config("earth", "tiny", overrides=overrides)
    man = bake_corridor(generate_hero(cfg, log=None), cfg, tmp_path / "baked", log=None)
    assert "caves.glb" in man["files"] and man["caves"]["n_entrances"] > 0

    imported = _run(godot, env, ["--import"], IMPORT_TIMEOUT_S)
    assert imported.returncode == 0, f"--import 실패:\n{_tail(imported)}"
    out = _smoke(godot, env, [f"--baked-dir={(tmp_path / 'baked').as_posix()}", "--expect-baked"])
    assert "레이어: terrain, surround, water, water_table, caves, section" in out
    assert f"동굴 삼각형 {man['file_meta']['caves']['faces']} (gltf_runtime)" in out
    assert f"입구 {man['caves']['n_entrances']}," in out
