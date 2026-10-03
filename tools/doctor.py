"""개발 환경 점검. macOS, Linux, Windows 에서 똑같이 돕니다.

실행: uv run python tools/doctor.py [--hash]
  --hash   data/manifest.json 의 SHA-256 까지 대조합니다 (데이터 전체를 읽어 몇 분 걸립니다).

필수 항목이 하나라도 실패하면 종료 코드 1 로 끝납니다. '주의'와 '정보' 항목은 알려 주기만 합니다.
패키지가 하나도 없어도 돌도록 표준 라이브러리만 먼저 가져오고, 패키지는 점검할 때 하나씩 가져옵니다.
시스템 python3(3.9 이상)으로 돌려도 문법 오류가 나지 않게 씁니다.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib
import importlib.metadata
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import sysconfig
import tempfile
import time
import unicodedata
import warnings
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = Path(os.environ.get("BPCG_DATA") or ROOT / "data")
GODOT_VERSION = "4.7.2"
MIN_UV = (0, 12, 17)
MIN_MACOS_MAJOR = 15  # 잠근 rasterio 1.5.2 의 Apple Silicon wheel 이 macosx_15_0 용
MIN_GLIBC = (2, 28)  # numba, rasterio 등의 Linux wheel 이 manylinux_2_28 용
MIN_FREE_GB = 20

IS_WIN = sys.platform == "win32"
IS_MAC = sys.platform == "darwin"
IS_LINUX = sys.platform.startswith("linux")

# (가져올 모듈 이름, 배포 이름, 묶음)
REQUIRED_MODULES = [
    ("numpy", "numpy", "기본"),
    ("scipy", "scipy", "기본"),
    ("numba", "numba", "기본"),
    ("pytest", "pytest", "dev"),
    ("ruff", "ruff", "dev"),
    ("hypothesis", "hypothesis", "dev"),
    ("matplotlib", "matplotlib", "dev"),
    ("h5py", "h5py", "data"),
    ("h5netcdf", "h5netcdf", "data"),
    ("xarray", "xarray", "data"),
    ("rasterio", "rasterio", "data"),
    ("pyogrio", "pyogrio", "data"),
    ("geopandas", "geopandas", "data"),
    ("pyarrow", "pyarrow", "data"),
    ("skimage", "scikit-image", "mesh"),
    ("trimesh", "trimesh", "mesh"),
]
OPTIONAL_MODULES = [
    ("pyflwdir", "pyflwdir", "analysis"),
    ("landlab", "landlab", "analysis"),
    ("fastscapelib", "fastscapelib", "gpl"),
    ("topotoolbox", "topotoolbox", "gpl"),
    ("jupyterlab", "jupyterlab", "notebook"),
    ("ipykernel", "ipykernel", "notebook"),
]

OK, FAIL, WARN, INFO = "[ OK ]", "[실패]", "[주의]", "[정보]"
rows = []  # (표시, 항목, 내용)


def report(mark, name, detail):
    rows.append((mark, name, detail))
    return mark != FAIL


def check(name, ok, detail, required=True):
    return report(OK if ok else (FAIL if required else WARN), name, detail)


def setup_cmd(option=""):
    """이 운영체제에서 설치 스크립트를 부르는 명령."""
    if IS_WIN:
        flag = f" -{option.capitalize()}" if option else ""
        return f"powershell -ExecutionPolicy Bypass -File scripts\\setup.ps1{flag}"
    return f"./scripts/setup.sh --{option}" if option else "./scripts/setup.sh"


def version_tuple(text):
    return tuple(int(x) for x in re.findall(r"\d+", text)[:3])


def native_arch():
    """운영체제의 실제 CPU 종류. 에뮬레이션 중인 파이썬이 보는 값과 다를 수 있습니다."""
    if IS_WIN:
        arch = os.environ.get("PROCESSOR_ARCHITEW6432") or os.environ.get(
            "PROCESSOR_ARCHITECTURE", ""
        )
        try:
            import winreg

            key = r"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, key) as k:
                arch = winreg.QueryValueEx(k, "PROCESSOR_ARCHITECTURE")[0]
        except OSError:
            pass
        return arch.upper()
    if IS_MAC:
        r = subprocess.run(
            ["sysctl", "-n", "sysctl.proc_translated"], capture_output=True, text=True
        )
        if r.stdout.strip() == "1":
            return "ARM64"  # Rosetta 로 x86_64 파이썬이 도는 Apple Silicon
    return platform.machine().upper()


# ---------------------------------------------------------------- 운영체제
def check_os():
    machine = platform.machine()
    arch = native_arch()
    if IS_MAC:
        ver = platform.mac_ver()[0]
        if not ver or ver.startswith("10.16"):
            r = subprocess.run(["sw_vers", "-productVersion"], capture_output=True, text=True)
            ver = r.stdout.strip() or ver
        major = version_tuple(ver or "0")[0]
        name = f"macOS {ver} ({machine})"
        if arch == "ARM64" and machine == "x86_64":
            return report(
                FAIL,
                "운영체제",
                f"{name}: Rosetta 로 x86_64 파이썬이 돌고 있습니다. "
                ".venv 를 지우고 arm64 터미널에서 ./scripts/setup.sh 를 다시 실행하세요.",
            )
        if machine == "x86_64":
            return report(
                FAIL,
                "운영체제",
                f"{name}: Intel Mac 은 지원하지 않습니다 "
                "(numba 0.68 / llvmlite 0.50 의 macOS x86_64 wheel 이 없음).",
            )
        if major < MIN_MACOS_MAJOR:
            return report(
                FAIL,
                "운영체제",
                f"{name}: macOS {MIN_MACOS_MAJOR} 이상이 필요합니다 "
                "(rasterio 1.5.2 의 Apple Silicon wheel 이 macosx_15_0 용).",
            )
        return report(OK, "운영체제", name)

    if IS_LINUX:
        libc = ""
        try:
            libc = os.confstr("CS_GNU_LIBC_VERSION") or ""
        except (AttributeError, ValueError, OSError):
            pass
        name = f"Linux {platform.release()} ({machine})"
        if not libc.startswith("glibc"):
            lib, ver = platform.libc_ver()
            libc = f"{lib} {ver}".strip()
        if not libc.startswith("glibc"):
            return report(
                FAIL,
                "운영체제",
                f"{name}, libc '{libc or '알 수 없음'}': glibc {'.'.join(map(str, MIN_GLIBC))} "
                "이상인 배포판이 필요합니다 (musl/Alpine 미지원).",
            )
        glibc = version_tuple(libc)
        name += f", {libc}"
        if glibc < MIN_GLIBC:
            return report(
                FAIL,
                "운영체제",
                f"{name}: glibc {'.'.join(map(str, MIN_GLIBC))} 이상이 필요합니다 "
                "(Linux wheel 이 manylinux_2_28 용).",
            )
        if machine in ("aarch64", "arm64"):
            return report(
                INFO,
                "운영체제",
                f"{name}: ARM64 에서는 gpl 묶음(fastscapelib 등)을 쓸 수 없습니다.",
            )
        return report(OK, "운영체제", name)

    if IS_WIN:
        name = f"Windows {platform.release()} {platform.version()} ({machine})"
        if arch == "ARM64" and machine.upper() == "ARM64":
            return report(
                WARN,
                "운영체제",
                f"{name}: ARM64 용 pyogrio, shapely, pyarrow wheel 이 없습니다. "
                "설치 스크립트는 x64 파이썬(에뮬레이션)을 씁니다. "
                ".venv 를 지우고 설치 스크립트를 다시 실행하세요.",
            )
        if arch == "ARM64":
            return report(
                INFO, "운영체제", f"{name}: ARM64 PC 에서 x64 파이썬(에뮬레이션)으로 돕니다."
            )
        return report(OK, "운영체제", name)

    return report(FAIL, "운영체제", f"{platform.system()}: 지원하지 않는 운영체제입니다.")


def check_long_paths():
    """Windows 의 260자 경로 제한. 깊은 패키지 폴더(jupyterlab 등)에서 문제가 됩니다."""
    if not IS_WIN:
        return
    enabled = 0
    try:
        import winreg

        key = r"SYSTEM\CurrentControlSet\Control\FileSystem"
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, key) as k:
            enabled = winreg.QueryValueEx(k, "LongPathsEnabled")[0]
    except OSError:
        pass
    if enabled == 1:
        report(OK, "긴 경로", "LongPathsEnabled = 1")
    else:
        report(
            WARN,
            "긴 경로",
            "260자 넘는 경로를 못 씁니다. 저장소를 C:\\src\\b-pcg 처럼 짧은 곳에 두거나 "
            "관리자 권한으로 LongPathsEnabled 를 켜세요 (scripts/setup.ps1 안내 참고).",
        )


# ---------------------------------------------------------------- 파이썬과 가상환경
def check_python():
    want_text = (ROOT / ".python-version").read_text(encoding="utf-8").strip()
    want = version_tuple(want_text)
    have = sys.version_info[: len(want)]
    detail = f"{platform.python_version()} (요구 {want_text}, {sys.executable})"
    check("파이썬", tuple(have) == want, detail)


def site_packages():
    return Path(sysconfig.get_paths()["purelib"])


def is_under(path, parent):
    try:
        Path(path).resolve().relative_to(Path(parent).resolve())
        return True
    except (ValueError, OSError):
        return False


def launcher_python(script):
    """실행 스크립트(bin/pytest 등)가 부르는 파이썬 경로.

    경로가 짧으면 첫 줄이 '#!<경로>' 이고, 길면 첫 줄 '#!/bin/sh' 다음 줄에 exec '<경로>' 가 옵니다.
    """
    try:
        head = script.read_text(encoding="utf-8", errors="replace").splitlines()[:3]
    except OSError:
        return ""
    if head and head[0].startswith("#!") and head[0].strip() != "#!/bin/sh":
        return head[0][2:].strip()
    for line in head[1:]:
        m = re.match(r"^'''exec' '([^']+)'", line)
        if m:
            return m.group(1)
    return ""


def check_venv():
    venv = ROOT / ".venv"
    if sys.prefix == sys.base_prefix:
        if not venv.exists():
            hint = f"저장소에 .venv 가 없습니다. {setup_cmd()} 로 설치하세요."
        else:
            hint = "uv run python tools/doctor.py 로 실행하세요."
        return report(WARN, "가상환경", f"가상환경 밖에서 돌고 있습니다 ({sys.executable}). {hint}")
    if not is_under(sys.prefix, venv):
        report(INFO, "가상환경", f"{sys.prefix} (저장소의 .venv 가 아님, UV_PROJECT_ENVIRONMENT?)")
    # 저장소를 옮기거나 이름을 바꾸면 .pth 와 실행 파일 첫 줄이 옛 경로를 가리킵니다.
    stale = []
    for pth in sorted(site_packages().glob("*.pth")):
        try:
            lines = pth.read_text(encoding="utf-8").splitlines()
        except OSError:
            continue
        for line in lines:
            line = line.strip()
            if not line or line.startswith(("#", "import")) or not os.path.isabs(line):
                continue
            if not Path(line).exists() or not is_under(line, ROOT):
                stale.append(f"{pth.name} -> {line}")
    if not IS_WIN:
        exe = launcher_python(Path(sys.prefix) / "bin" / "pytest")
        if exe.startswith("/") and not Path(exe).exists():
            stale.append(f"bin/pytest -> {exe}")
    if stale:
        return report(
            FAIL,
            "가상환경",
            f"옛 경로를 가리킵니다 ({'; '.join(stale[:3])}). 저장소를 옮긴 뒤라면 .venv 를 지우고 "
            f"{setup_cmd()} 를 다시 실행하세요.",
        )
    return report(OK, "가상환경", str(sys.prefix))


# ---------------------------------------------------------------- 패키지
def dist_version(dist, module):
    try:
        return importlib.metadata.version(dist)
    except importlib.metadata.PackageNotFoundError:
        return str(getattr(module, "__version__", "?"))


def import_quietly(name):
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        return importlib.import_module(name)


def check_project_package():
    """Python 패키지 bpcg_studio (스튜디오, `bpcg` 명령, 시험 도우미). 생성기는 C# 입니다."""
    name = "패키지 bpcg_studio"
    try:
        pkg = import_quietly("bpcg_studio")
    except Exception as e:
        return report(FAIL, name, f"{type(e).__name__}: {e} — {setup_cmd()} 를 다시 실행하세요.")
    where = Path(pkg.__file__).resolve().parent
    if not is_under(where, ROOT / "src"):
        return report(
            FAIL,
            name,
            f"다른 위치에서 가져옵니다: {where}. .venv 를 지우고 {setup_cmd()} 를 다시 실행하세요.",
        )
    return report(OK, name, f"{dist_version('b-pcg', pkg)} [이 저장소, 편집 설치]")


def check_modules():
    for mod, dist, group in REQUIRED_MODULES:
        try:
            m = import_quietly(mod)
            report(OK, f"패키지 {mod}", f"{dist_version(dist, m)} [{group}]")
        except Exception as e:
            report(FAIL, f"패키지 {mod}", f"[{group}] {type(e).__name__}: {e} — {setup_cmd()}")
    is_arm_linux = IS_LINUX and platform.machine() in ("aarch64", "arm64")
    for mod, dist, group in OPTIONAL_MODULES:
        try:
            m = import_quietly(mod)
            report(OK, f"패키지 {mod}", f"{dist_version(dist, m)} [{group}, 선택]")
        except Exception as e:
            if group == "gpl" and is_arm_linux:
                hint = "Linux ARM64 용 wheel 이 없어 설치할 수 없습니다"
            else:
                hint = f"필요하면 {setup_cmd(group)}"
            report(INFO, f"패키지 {mod}", f"없음 [{group}, 선택] ({type(e).__name__}) — {hint}")


def check_numba_jit():
    try:
        import numba
        import numpy as np
    except Exception:
        return report(FAIL, "numba JIT", "numba 나 numpy 를 가져오지 못해 건너뜁니다.")
    try:

        @numba.njit(parallel=True)
        def total(a):
            s = 0.0
            for i in numba.prange(a.size):
                s += a[i]
            return s

        a = np.arange(1_000_000, dtype=np.float64)
        t0 = time.perf_counter()
        got = total(a)
        dt = time.perf_counter() - t0
        want = float(a.sum())
        ok = abs(got - want) <= 1e-9 * want
        detail = (
            f"병렬 컴파일+실행 {dt:.1f}초, 스레드 {numba.get_num_threads()}개, "
            f"스레딩 계층 {numba.threading_layer()}"
        )
        return check("numba JIT", ok, detail)
    except Exception as e:
        return report(FAIL, "numba JIT", f"{type(e).__name__}: {e}")


# ---------------------------------------------------------------- 도구
def check_tools():
    git = shutil.which("git")
    check("도구 git", bool(git), git or "없음 — git 을 설치하세요")

    # uv run 은 자식 프로세스에 UV(uv 실행 파일 경로)를 넘겨 줍니다. PATH 에 없을 때 씁니다.
    uv = shutil.which("uv")
    on_path = bool(uv)
    uv = uv or os.environ.get("UV")
    if not uv:
        return report(FAIL, "도구 uv", f"없음 — {setup_cmd()} 로 설치하세요")
    try:
        out = subprocess.run([uv, "--version"], capture_output=True, text=True, timeout=30)
        ver = out.stdout.strip() or "버전을 알 수 없음"
    except (OSError, subprocess.SubprocessError) as e:
        ver = f"버전 확인 실패: {e}"
    need = ".".join(map(str, MIN_UV))
    notes = []
    if re.search(r"\d", ver) and version_tuple(ver) < MIN_UV:
        notes.append(f"{need} 이상 필요 (uv self update)")
    if not on_path:
        notes.append("PATH 에 없음 (새 터미널에서 쓰려면 uv tool update-shell)")
    detail = f"{ver} ({uv})" + (" — " + ", ".join(notes) if notes else "")
    report(WARN if notes else OK, "도구 uv", detail)

    curl = shutil.which("curl")
    check("도구 curl", bool(curl), curl or "없음 — 파일럿 데이터 받기에 필요", required=False)


# ---------------------------------------------------------------- .NET
def check_dotnet():
    """생성기(src/Bpcg)와 엔진(engine/)은 C# 이라 .NET 10 SDK 가 필요합니다 (global.json)."""
    dotnet = shutil.which("dotnet")
    if not dotnet:
        return report(
            FAIL,
            ".NET SDK",
            "없음 — .NET 10 SDK 를 설치하세요 (macOS: brew install --cask dotnet-sdk)",
        )
    try:
        out = subprocess.run(
            [dotnet, "--list-sdks"], capture_output=True, text=True, timeout=60, cwd=ROOT
        )
        sdks = [ln.split()[0] for ln in out.stdout.splitlines() if ln.strip()]
    except (OSError, subprocess.SubprocessError) as e:
        return report(FAIL, ".NET SDK", f"dotnet --list-sdks 실패: {e}")
    ok = any(v.split(".")[0] == "10" for v in sdks)
    detail = f"{', '.join(sdks) or 'SDK 없음'} ({dotnet})"
    return check(".NET SDK", ok, detail if ok else detail + " — 10.x 가 필요합니다")


# ---------------------------------------------------------------- Godot
def godot_candidates():
    """Godot 4.7.2 .NET 판 후보 (engine/ 은 C# 프로젝트라 표준판으로는 열리지 않음)."""
    tag = f"Godot_v{GODOT_VERSION}-stable_mono"
    tools = ROOT / ".tools" / "godot-net"
    for env in ("GODOT_NET", "GODOT"):
        if os.environ.get(env):
            yield Path(os.environ[env])
    yield tools / "Godot_mono.app" / "Contents" / "MacOS" / "Godot"
    yield tools / f"{tag}_linux_x86_64" / f"{tag}_linux.x86_64"
    yield tools / f"{tag}_linux_arm64" / f"{tag}_linux.arm64"
    yield tools / f"{tag}_win64" / f"{tag}_win64_console.exe"
    yield tools / f"{tag}_windows_arm64" / f"{tag}_windows_arm64_console.exe"


def console_exe(path):
    """Windows 의 창 모드 Godot 은 콘솔에 아무것도 찍지 않으므로 옆의 _console.exe 를 씁니다."""
    if path.suffix.lower() == ".exe" and not path.stem.lower().endswith("_console"):
        sibling = path.with_name(f"{path.stem}_console.exe")
        if sibling.exists():
            return sibling
    return path


def godot_version(exe):
    """임시 HOME(Windows 는 APPDATA 도)으로 --version 만 실행합니다.

    사용자의 Godot 편집기 설정을 건드리지 않게 하려는 것입니다.
    """
    home = tempfile.mkdtemp(prefix="bpcg-godot-home-")
    env = dict(os.environ)
    env["HOME"] = home
    env["XDG_CONFIG_HOME"] = os.path.join(home, "config")
    env["XDG_DATA_HOME"] = os.path.join(home, "data")
    env["XDG_CACHE_HOME"] = os.path.join(home, "cache")
    if IS_WIN:
        env["APPDATA"] = os.path.join(home, "AppData", "Roaming")
        env["LOCALAPPDATA"] = os.path.join(home, "AppData", "Local")
    try:
        r = subprocess.run(
            [str(exe), "--headless", "--version"],
            env=env,
            stdin=subprocess.DEVNULL,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=60,
        )
        lines = [ln.strip() for ln in r.stdout.splitlines() if re.match(r"^\d+\.\d+", ln.strip())]
        return lines[-1] if lines else ""
    except (OSError, subprocess.SubprocessError):
        return ""
    finally:
        shutil.rmtree(home, ignore_errors=True)


def check_godot():
    seen = set()
    for cand in godot_candidates():
        exe = console_exe(cand)
        key = str(exe)
        if key in seen or not exe.is_file():
            continue
        seen.add(key)
        ver = godot_version(exe)
        if ver.startswith(f"{GODOT_VERSION}.stable.mono"):
            return report(OK, "Godot .NET", f"{ver} ({exe})")
        report(
            WARN,
            "Godot .NET",
            f"{exe} 은 {ver or '버전을 알 수 없음'} — {GODOT_VERSION} .NET 판이 필요합니다",
        )
    return report(
        WARN,
        "Godot .NET",
        f"{GODOT_VERSION} .NET 판 없음 — engine/README.md 의 '준비' 대로 .tools/godot-net/ 에 "
        "받으세요 (엔진 담당은 필수)",
    )


# ---------------------------------------------------------------- 데이터와 디스크
def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(1 << 22):
            h.update(chunk)
    return h.hexdigest()


def data_rel(rel):
    """manifest 의 경로를 데이터 폴더 기준 상대 경로로 맞춥니다. 'data/' 로 시작해도 됩니다."""
    rel = rel.replace("\\", "/")
    return rel[len("data/") :] if rel.startswith("data/") else rel


def check_data(do_hash):
    """manifest 의 파일이 데이터 폴더에 있고 크기(--hash 면 SHA-256)가 맞는지 봅니다."""
    manifest = ROOT / "data" / "manifest.json"
    if not manifest.exists() and (DATA / "manifest.json").exists():
        manifest = DATA / "manifest.json"
    if not manifest.exists():
        return report(INFO, "데이터", "data/manifest.json 이 아직 없어 데이터 점검을 건너뜁니다.")
    try:
        entries = json.loads(manifest.read_text(encoding="utf-8"))["files"]
    except (OSError, ValueError, KeyError, TypeError) as e:
        return report(WARN, "데이터", f"data/manifest.json 을 읽지 못했습니다: {e}")
    if do_hash:
        print("데이터 SHA-256 계산 중 (몇 분 걸릴 수 있습니다)...", flush=True)

    # 묶음(pilot, external 등)마다 한 줄로 알립니다.
    groups = {}
    for e in entries:
        rel = data_rel(str(e.get("path", "")))
        name = str(e.get("group") or rel.split("/")[0])
        g = groups.setdefault(name, {"total": 0, "script": [], "manual": [], "wrong": []})
        g["total"] += 1
        p = DATA / rel
        if not p.is_file():
            g["manual" if e.get("redownload") == "manual" else "script"].append(rel)
            continue
        size = e.get("bytes")
        if size is not None and p.stat().st_size != int(size):
            g["wrong"].append(f"{rel} (크기)")
        elif do_hash and e.get("sha256") and sha256_of(p) != e["sha256"]:
            g["wrong"].append(f"{rel} (SHA-256)")

    for name, g in sorted(groups.items()):
        missing = g["script"] + g["manual"]
        detail = f"{g['total'] - len(missing)}/{g['total']}개 있음"
        if g["script"]:
            top = sorted({"/".join(m.split("/")[:2]) for m in g["script"]})
            detail += f", 없음 {len(g['script'])}개 [{', '.join(top[:6])}] — {setup_cmd('data')}"
        if g["manual"]:
            detail += f", 손으로 받는 자료 {len(g['manual'])}개 없음 (data/README.md 참고)"
        if g["wrong"]:
            detail += f", 다름 {len(g['wrong'])}개: {', '.join(g['wrong'][:5])}"
        if not missing and not g["wrong"]:
            detail += ", SHA-256 일치" if do_hash else ", 크기 일치 (내용 대조는 --hash)"
        if g["script"] or g["wrong"]:
            mark = WARN
        elif g["manual"]:
            mark = INFO
        else:
            mark = OK
        report(mark, f"데이터 {name}", detail)
    report(INFO, "데이터 폴더", f"{DATA} (바꾸려면 환경 변수 BPCG_DATA)")


def check_disk():
    target = DATA if DATA.exists() else ROOT
    free = shutil.disk_usage(target).free / 1e9
    check(
        "디스크 여유",
        free >= MIN_FREE_GB,
        f"{free:.0f} GB ({MIN_FREE_GB} GB 이상 권장: 데이터, 캐시, 결과용)",
        required=False,
    )


# ---------------------------------------------------------------- 출력
def display_width(text):
    return sum(2 if unicodedata.east_asian_width(c) in ("W", "F") else 1 for c in text)


def print_rows():
    width = max(display_width(r[1]) for r in rows)
    for mark, name, detail in rows:
        pad = " " * (width - display_width(name))
        print(f"{mark} {name}{pad}  {detail}")


def main():
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # Windows 콘솔, 파이프 대비
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser(description="B-PCG 개발 환경을 점검합니다.")
    ap.add_argument("--hash", action="store_true", help="데이터 파일의 SHA-256 까지 대조합니다.")
    args = ap.parse_args()

    print(f"B-PCG 환경 점검 — {ROOT}")
    t0 = time.perf_counter()
    check_os()
    check_long_paths()
    check_python()
    check_venv()
    check_project_package()
    check_modules()
    check_numba_jit()
    check_tools()
    check_dotnet()
    check_godot()
    check_data(args.hash)
    check_disk()
    print_rows()

    fails = sum(1 for r in rows if r[0] == FAIL)
    warns = sum(1 for r in rows if r[0] == WARN)
    print(f"\n필수 실패 {fails}개, 주의 {warns}개 ({time.perf_counter() - t0:.1f}초)")
    if fails:
        print("[실패] 줄을 고친 뒤 다시 점검하세요. 모르겠으면 이 출력 전체를 팀에 공유하세요.")
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
