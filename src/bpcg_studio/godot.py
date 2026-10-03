"""스튜디오에서 Godot 로 보기: Godot .NET 판 찾기, 회랑 파일 복사, C# 빌드·가져오기(import), 실행.

엔진(engine/)은 Godot 4.7.2 .NET(C#) 프로젝트입니다. 표준판으로는 열리지 않습니다.

순서 (사용자가 'Godot로 보기' 나 '편집기로 열기' 를 눌렀을 때만)
1. find_godot: tests/test_engine.py 와 같은 후보에서 Godot 4.7.2 .NET 판을 찾습니다.
   버전은 `--headless --version` 을 임시 HOME 으로 돌려 확인합니다 (`.mono.` 가 있어야 함).
2. install_corridor: 실행의 corridor/manifest.json 이 적은 파일 + manifest.json 을 engine/baked/ 로
   복사합니다. 이전 실행에만 있던 회랑 파일(예: 동굴이 없어 빠진 caves.glb)은 지우고,
   engine/baked/studio_run.json 에 어느 실행인지 적습니다. 실행의 지구본(globe/)은
   engine/baked/globe 로 통째로 바꾸고, 지구본이 없는 실행이면 지난 지구본을 지웁니다
   (install_globe).
3. build_engine: `dotnet build engine/Bpcg.Engine.csproj` 로 게임 어셈블리를 빌드합니다
   (편집기 없이 띄우는 '보기' 는 빌드해 둔 어셈블리를 읽음).
   import_project: `Godot --headless --path engine --import` 를 임시 HOME 으로 돌립니다.
4. launch: `Godot --path engine res://scenes/main.tscn` (보기, 처음 화면을 건너뛰고 회랑 장면)
   또는 `Godot --path engine -e` (편집기) 를 사용자 환경 그대로 따로 띄웁니다. 편집기는 사용자
   설정을 써야 하므로 이 단계만 HOME 을 바꾸지 않습니다.

주의: Godot 는 편집기 설정을 사용자 폴더에 씁니다. 1·3 단계처럼 사용자가 보지 않는 실행은 반드시
--headless 와 임시 HOME (리눅스 XDG_*, 윈도우 APPDATA·LOCALAPPDATA) 으로 돌립니다.

engine/baked 에 무엇이 있는지(baked_info)는 스튜디오가 복사했으면 studio_run.json 으로, 명령
(`bpcg bake --engine`, `bpcg all --engine`)으로 구웠으면 engine/baked/manifest.json 으로
알려 줍니다.
표시 파일보다 manifest 가 새롭거나 설정 해시가 다르면 그 뒤에 명령으로 다시 구운 것입니다.
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from datetime import datetime
from pathlib import Path

from bpcg_studio.csharp import CSharpError, dotnet
from bpcg_studio.names import bare_name
from bpcg_studio.paths import OUT, ROOT

GODOT_VERSION = "4.7.2"
ENGINE = ROOT / "engine"
ENGINE_PROJECT = ENGINE / "Bpcg.Engine.csproj"
CORRIDOR_SCENE = "res://scenes/main.tscn"  # '보기' 는 처음 화면(start.tscn) 대신 이 장면을 엶
BUILD_TIMEOUT_S = 900
BAKED = ENGINE / "baked"
MARKER = "studio_run.json"
GLOBE_DIR = "globe"  # 실행 폴더와 engine/baked 안의 지구본 폴더 이름
CORRIDOR_MAYBE = ("caves.glb", "entrances.json")  # 실행에 따라 없을 수 있는 회랑 파일
IMPORT_TIMEOUT_S = 600
VERSION_TIMEOUT_S = 60


class GodotError(Exception):
    """사용자에게 보여 줄 Godot 단계 오류."""


def candidates() -> list[Path]:
    """Godot .NET 판 실행 파일 후보 (tests/test_engine.py 와 같은 순서)."""
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


def is_net_version(version: str) -> bool:
    """Godot 4.7.2 .NET 판의 버전 글인지 (예: 4.7.2.stable.mono.official.ed1daf0bf)."""
    return version.startswith(GODOT_VERSION) and ".mono." in version


def isolated_env(tmp: Path) -> dict[str, str]:
    """Godot 가 사용자 설정 폴더 대신 임시 폴더를 쓰게 하는 환경 변수."""
    env = os.environ.copy()
    home = Path(tmp) / "home"
    home.mkdir(parents=True, exist_ok=True)
    env["HOME"] = str(home)
    if sys.platform == "win32":
        for name in ("APPDATA", "LOCALAPPDATA"):
            d = Path(tmp) / name.lower()
            d.mkdir(exist_ok=True)
            env[name] = str(d)
    else:
        # 리눅스는 XDG_* 가 있으면 HOME 보다 먼저 봅니다 (macOS 는 무시합니다).
        for name in ("XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME"):
            d = Path(tmp) / name.lower()
            d.mkdir(exist_ok=True)
            env[name] = str(d)
    return env


def godot_version(path: Path) -> str:
    """`--headless --version` 출력의 마지막 줄 (임시 HOME). 실패하면 ''."""
    with tempfile.TemporaryDirectory(prefix="bpcg-godot-") as tmp:
        try:
            out = subprocess.run(
                [str(path), "--headless", "--version"],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=isolated_env(Path(tmp)),
                timeout=VERSION_TIMEOUT_S,
                stdin=subprocess.DEVNULL,
            )
        except (OSError, subprocess.SubprocessError):
            return ""
    text = out.stdout.strip()
    return text.splitlines()[-1].strip() if text else ""


def find_godot(paths: list[Path] | None = None, check_version: bool = False, version_fn=None):
    """Godot 를 찾습니다.

    paths: 후보 (기본 candidates()). check_version: 버전이 GODOT_VERSION 으로 시작하는지 확인
    (Godot 를 headless·임시 HOME 으로 한 번 돌림). version_fn: 시험용 버전 함수.
    반환 dict: ok, path, version, message, seen (확인한 후보와 버전).
    """
    version_fn = version_fn or godot_version
    seen = []
    for p in paths if paths is not None else candidates():
        p = Path(p)
        if not p.is_file():
            continue
        if not check_version:
            return {"ok": True, "path": str(p), "version": None, "message": "", "seen": seen}
        v = version_fn(p)
        if is_net_version(v):
            return {"ok": True, "path": str(p), "version": v, "message": "", "seen": seen}
        seen.append({"path": str(p), "version": v or "버전 확인 실패"})
    if seen:
        msg = f"Godot {GODOT_VERSION} .NET 판이 아닙니다: " + ", ".join(
            f"{s['path']} ({s['version']})" for s in seen
        )
    else:
        msg = (
            f"Godot {GODOT_VERSION} .NET 판을 찾지 못했습니다. engine/README.md 의 '준비' 대로 "
            ".tools/godot-net/ 에 받거나 환경 변수 GODOT_NET 에 경로를 넣으세요."
        )
    return {"ok": False, "path": None, "version": None, "message": msg, "seen": seen}


# ---------------------------------------------------------------- 회랑 파일 복사
def corridor_files(corridor_dir: Path) -> list[str]:
    """corridor/manifest.json 의 files + manifest.json. 경로가 섞인 이름은 GodotError."""
    man_path = Path(corridor_dir) / "manifest.json"
    try:
        man = json.loads(man_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        raise GodotError(f"회랑 manifest 를 읽지 못했습니다: {man_path} ({e})") from None
    if man.get("format") != "bpcg-corridor":
        raise GodotError(f"{man_path} 는 회랑 manifest 가 아닙니다")
    names = [str(n) for n in man.get("files") or []]
    for n in names:
        if not bare_name(n):  # 경로·드라이브(D:x)·앞의 '.' 가 섞인 이름은 baked 밖을 가리킴
            raise GodotError(f"회랑 파일 이름이 올바르지 않습니다: {n!r}")
    if "manifest.json" not in names:
        names.append("manifest.json")
    return names


def read_marker(baked_dir: Path = BAKED) -> dict | None:
    """engine/baked 에 마지막으로 복사한 실행 정보 (없으면 None)."""
    try:
        return json.loads((Path(baked_dir) / MARKER).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def _read_json(path: Path) -> dict | None:
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    return data if isinstance(data, dict) else None


def baked_info(baked_dir: Path = BAKED) -> dict | None:
    """engine/baked 에 들어 있는 회랑 (없으면 None).

    반환 dict: origin ('studio' 스튜디오가 복사 | 'cli' 명령으로 구움), origin_label, run (위 막대에
    보일 이름), profile, seed, config_digest, git_commit, files, has_detail (디테일 높이맵),
    has_globe (globe/globe.json), summary (한 줄 설명). 스튜디오 복사면 표시 파일의 run·source·
    copied_at·removed 도 들어 있습니다.
    """
    d = Path(baked_dir)
    marker = read_marker(d)
    man = _read_json(d / "manifest.json")
    if man is not None and man.get("format") != "bpcg-corridor":
        man = None
    if marker is None and man is None:
        return None
    flags = {
        "has_detail": (d / "heightmap_detail.bin").is_file(),
        "has_globe": (d / "globe" / "globe.json").is_file(),
    }
    if marker is not None and (man is None or not _rebaked_after(d, marker, man)):
        info = {**marker, **flags, "origin": "studio", "origin_label": "스튜디오에서 복사"}
        info["git_commit"] = (man or {}).get("git_commit")
        info.setdefault("run", "")
    else:
        assert man is not None
        commit = str(man.get("git_commit") or "")[:7]
        info = {
            **flags,
            "origin": "cli",
            "origin_label": "명령으로 구움",
            "run": f"명령으로 구움 ({man.get('profile') or '프로필 모름'}"
            + (f", 커밋 {commit})" if commit else ")"),
            "source": "명령으로 구움",
            "profile": man.get("profile"),
            "seed": man.get("seed"),
            "config_digest": man.get("config_digest"),
            "git_commit": man.get("git_commit"),
            "files": [str(n) for n in man.get("files") or []],
            "baked_at": _mtime_iso(d / "manifest.json"),
        }
    parts = [info["origin_label"]]
    if info["origin"] == "studio" and info.get("run"):
        parts.append(f"실행 {info['run']}")
    if info.get("profile"):
        parts.append(f"프로필 {info['profile']}")
    if info.get("seed") is not None:
        parts.append(f"시드 {info['seed']}")
    if info.get("git_commit"):
        parts.append(f"커밋 {str(info['git_commit'])[:7]}")
    if info.get("config_digest"):
        parts.append(f"설정 {info['config_digest']}")
    parts.append("디테일 높이맵 " + ("있음" if flags["has_detail"] else "없음"))
    parts.append("지구본 " + ("있음" if flags["has_globe"] else "없음"))
    info["summary"] = " · ".join(parts)
    return info


def _rebaked_after(d: Path, marker: dict, man: dict) -> bool:
    """표시 파일을 쓴 뒤 명령으로 다시 구웠는지: 설정 해시가 다르거나 manifest 가 더 새로움.

    스튜디오 복사는 manifest.json 을 원본 시각 그대로 복사하므로(copy2) 표시 파일보다 새롭지
    않습니다.
    """
    if marker.get("config_digest") != man.get("config_digest"):
        return True
    try:
        return (d / "manifest.json").stat().st_mtime > (d / MARKER).stat().st_mtime + 1.0
    except OSError:
        return False


def _mtime_iso(path: Path) -> str | None:
    try:
        return datetime.fromtimestamp(path.stat().st_mtime).isoformat(timespec="seconds")
    except OSError:
        return None


def install_corridor(corridor_dir: Path, baked_dir: Path = BAKED, run_key: str = "") -> dict:
    """회랑 파일을 baked_dir 로 복사하고 표시 파일(studio_run.json)을 씁니다. 반환: 표시 내용.

    빠진 파일이 있으면 아무것도 복사하지 않고 GodotError. 이전 회랑에만 있던 파일과 그 .import 는
    지웁니다(이전 manifest 의 files 와 CORRIDOR_MAYBE 중 이번에 없는 것). 다른 파일은 둡니다.
    """
    src = Path(corridor_dir)
    dst = Path(baked_dir)
    names = corridor_files(src)
    missing = [n for n in names if not (src / n).is_file()]
    if missing:
        raise GodotError(f"회랑 파일이 없습니다: {', '.join(missing)} ({src})")
    old: set[str] = set(CORRIDOR_MAYBE)
    try:
        old |= set(corridor_files(dst))
    except GodotError:
        pass
    dst.mkdir(parents=True, exist_ok=True)
    for n in names:
        tmp = dst / (n + ".part")
        shutil.copy2(src / n, tmp)
        os.replace(tmp, dst / n)
    removed = []
    for n in sorted(old - set(names)):
        for p in (dst / n, dst / (n + ".import")):
            if p.is_file():
                p.unlink()
                removed.append(p.name)
    man = json.loads((src / "manifest.json").read_text(encoding="utf-8"))
    marker = {
        "run": run_key,
        "source": str(src),
        "copied_at": datetime.now().isoformat(timespec="seconds"),
        "files": names,
        "removed": removed,
        "config_digest": man.get("config_digest"),
        "seed": man.get("seed"),
        "profile": man.get("profile"),
    }
    marker["globe"] = install_globe(src.parent / GLOBE_DIR, dst / GLOBE_DIR)
    tmp = dst / (MARKER + ".part")
    tmp.write_text(json.dumps(marker, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(tmp, dst / MARKER)
    return marker


def install_globe(globe_dir: Path, dst: Path) -> bool:
    """실행의 지구본 폴더(globe/)를 engine/baked/globe 로 통째로 바꿉니다. 반환: 복사했는지.

    실행에 지구본이 없으면(평면 히어로 등) 엔진의 지난 지구본을 지웁니다. 남겨 두면 지구본
    장면이 지금 회랑과 상관없는 행성을 보여 주기 때문입니다.
    """
    src = Path(globe_dir)
    dst = Path(dst)
    if dst.exists() and not (dst / "globe.json").is_file() and any(dst.iterdir()):
        raise GodotError(f"지구본 자리에 알 수 없는 파일이 있습니다: {dst}")
    if not (src / "globe.json").is_file():
        if dst.exists():
            shutil.rmtree(dst)
        return False
    tmp = dst.with_name(dst.name + ".part")
    if tmp.exists():
        shutil.rmtree(tmp)
    shutil.copytree(src, tmp)
    if dst.exists():
        shutil.rmtree(dst)
    os.replace(tmp, dst)
    return True


# ---------------------------------------------------------------- Godot 실행
def build_engine(project: Path = ENGINE_PROJECT, timeout: int = BUILD_TIMEOUT_S) -> dict:
    """`dotnet build engine/Bpcg.Engine.csproj -c Debug`. 반환: {returncode, tail, seconds}."""
    t = time.perf_counter()
    try:
        proc = subprocess.run(
            [dotnet(), "build", str(project), "-c", "Debug", "-nologo", "-v", "quiet"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            cwd=ROOT,
            stdin=subprocess.DEVNULL,
        )
    except CSharpError as e:
        raise GodotError(str(e)) from None
    except subprocess.TimeoutExpired:
        raise GodotError(f"엔진 C# 빌드가 {timeout} s 안에 끝나지 않았습니다") from None
    lines = (proc.stdout + "\n" + proc.stderr).strip().splitlines()
    return {"returncode": proc.returncode, "tail": lines[-30:], "seconds": time.perf_counter() - t}


def import_project(godot: Path, engine_dir: Path = ENGINE, timeout: int = IMPORT_TIMEOUT_S) -> dict:
    """`--headless --path engine --import` (임시 HOME). 반환: {returncode, tail, seconds}."""
    t = time.perf_counter()
    with tempfile.TemporaryDirectory(prefix="bpcg-godot-") as tmp:
        try:
            proc = subprocess.run(
                [str(godot), "--headless", "--path", str(engine_dir), "--import"],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=isolated_env(Path(tmp)),
                timeout=timeout,
                cwd=ROOT,
                stdin=subprocess.DEVNULL,
            )
        except subprocess.TimeoutExpired:
            raise GodotError(f"Godot 가져오기가 {timeout} s 안에 끝나지 않았습니다") from None
        except OSError as e:
            raise GodotError(f"Godot 를 실행하지 못했습니다: {e}") from None
    lines = (proc.stdout + "\n" + proc.stderr).strip().splitlines()
    return {
        "returncode": proc.returncode,
        "tail": lines[-30:],
        "seconds": time.perf_counter() - t,
    }


def launch(godot: Path, mode: str, engine_dir: Path = ENGINE, log_path: Path | None = None) -> int:
    """Godot 창을 따로 띄웁니다 (사용자 환경 그대로). mode: 'play' | 'editor'. 반환: pid."""
    if mode not in ("play", "editor"):
        raise GodotError(f"mode 는 play 또는 editor 입니다: {mode!r}")
    cmd = [str(godot), "--path", str(engine_dir)]
    cmd += ["-e"] if mode == "editor" else [CORRIDOR_SCENE]
    kwargs: dict = {}
    if sys.platform == "win32":
        kwargs["creationflags"] = subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        kwargs["start_new_session"] = True
    out = subprocess.DEVNULL
    fh = None
    if log_path is not None:
        log_path.parent.mkdir(parents=True, exist_ok=True)
        fh = open(log_path, "a", encoding="utf-8")  # 자식 프로세스가 이 파일에 씁니다
        fh.write(f"\n[스튜디오] {datetime.now().isoformat(timespec='seconds')} {' '.join(cmd)}\n")
        fh.flush()
        out = fh
    try:
        proc = subprocess.Popen(
            cmd, cwd=ROOT, env=os.environ.copy(), stdin=subprocess.DEVNULL, stdout=out,
            stderr=subprocess.STDOUT, **kwargs,
        )  # fmt: skip
    except OSError as e:
        raise GodotError(f"Godot 를 띄우지 못했습니다: {e}") from None
    finally:
        if fh is not None:
            fh.close()
    return proc.pid


class GodotRunner:
    """'Godot로 보기' 단계를 백그라운드에서 돌리고 상태를 알려 줍니다 (서버가 하나 가짐)."""

    def __init__(self, baked_dir: Path = BAKED, engine_dir: Path = ENGINE, log_dir: Path = OUT):
        self.baked_dir = Path(baked_dir)
        self.engine_dir = Path(engine_dir)
        self.log_path = Path(log_dir) / "studio" / "godot.log"
        self.lock = threading.Lock()
        # idle | finding | copying | building | importing | launching | launched | error
        self.state = "idle"
        self.message = ""
        self.detail: list[str] = []
        self.run = ""
        self.mode = ""
        self.updated = time.time()
        self.pid: int | None = None

    def _set(self, state: str, message: str, detail: list[str] | None = None) -> None:
        with self.lock:
            self.state = state
            self.message = message
            if detail is not None:
                self.detail = detail
            self.updated = time.time()

    def status(self) -> dict:
        found = find_godot(check_version=False)
        with self.lock:
            return {
                "state": self.state,
                "busy": self.state in ("finding", "copying", "building", "importing", "launching"),
                "message": self.message,
                "detail": list(self.detail),
                "run": self.run,
                "mode": self.mode,
                "pid": self.pid,
                "updated": datetime.fromtimestamp(self.updated).isoformat(timespec="seconds"),
                "godot": found.get("path"),
                "godot_message": found.get("message"),
                "baked": baked_info(self.baked_dir),
                "baked_dir": str(self.baked_dir),
            }

    def start(self, run_key: str, corridor_dir: Path | None, mode: str) -> None:
        """사용자가 누른 버튼 하나. 이미 진행 중이면 GodotError."""
        if mode not in ("play", "editor"):
            raise GodotError(f"mode 는 play 또는 editor 입니다: {mode!r}")
        with self.lock:
            if self.state in ("finding", "copying", "building", "importing", "launching"):
                raise GodotError("Godot 준비가 이미 진행 중입니다")
            self.state = "finding"
            self.message = "Godot 를 찾는 중입니다"
            self.detail = []
            self.run = run_key
            self.mode = mode
            self.pid = None
        threading.Thread(
            target=self._work, args=(run_key, corridor_dir, mode), name="godot", daemon=True
        ).start()

    def _work(self, run_key: str, corridor_dir: Path | None, mode: str) -> None:
        try:
            found = find_godot(check_version=True)
            if not found["ok"]:
                raise GodotError(found["message"])
            godot = Path(found["path"])
            if corridor_dir is not None:
                self._set("copying", f"{run_key} 의 회랑 파일을 engine/baked 로 복사합니다")
                marker = install_corridor(corridor_dir, self.baked_dir, run_key)
                self._set("copying", f"파일 {len(marker['files'])}개를 복사했습니다")
            self._set("building", "엔진 C# 을 빌드합니다 (dotnet build)")
            built = build_engine(self.engine_dir / ENGINE_PROJECT.name)
            if built["returncode"] != 0:
                raise GodotError(
                    f"엔진 C# 빌드 실패, 종료 코드 {built['returncode']}", built["tail"]
                )
            self._set("importing", "Godot 가 새 파일을 가져오는 중입니다 (화면 없이, 임시 HOME)")
            imp = import_project(godot, self.engine_dir)
            if imp["returncode"] != 0:
                raise GodotError(
                    f"가져오기(--import) 실패, 종료 코드 {imp['returncode']}", imp["tail"]
                )
            self._set("launching", "Godot 창을 띄웁니다", imp["tail"][-8:])
            pid = launch(godot, mode, self.engine_dir, self.log_path)
            with self.lock:
                self.pid = pid
            what = "편집기" if mode == "editor" else "보기"
            self._set("launched", f"Godot {what} 창을 띄웠습니다 (pid {pid}, {found['version']})")
        except GodotError as e:
            detail = e.args[1] if len(e.args) > 1 else None
            self._set("error", str(e.args[0]), detail)
        except Exception as e:  # 상태로 알려 줍니다
            self._set("error", f"예상하지 못한 오류: {e}")
