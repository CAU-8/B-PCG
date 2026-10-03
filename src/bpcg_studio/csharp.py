"""C# 콘솔(src/Bpcg.Cli)을 부르는 명령을 만듭니다.

생성기는 C# 입니다. 스튜디오와 `bpcg` 명령은 `dotnet build` 로 콘솔을 (바뀐 것만) 빌드한 뒤
`dotnet <Bpcg.Cli.dll> all …` 로 부릅니다. 기록 줄과 결과 파일은 예전 Python cli 와 같습니다.
"""

import shutil
import subprocess
from pathlib import Path

from bpcg_studio.paths import ROOT

CLI_PROJECT = ROOT / "src" / "Bpcg.Cli" / "Bpcg.Cli.csproj"
CONFIGURATION = "Release"
TARGET_FRAMEWORK = "net10.0"  # Directory.Build.props 와 같음
CLI_DLL = CLI_PROJECT.parent / "bin" / CONFIGURATION / TARGET_FRAMEWORK / "Bpcg.Cli.dll"


class CSharpError(Exception):
    """dotnet 이 없거나 C# 빌드가 실패했을 때."""


def dotnet() -> str:
    """dotnet 실행 파일 경로. 없으면 CSharpError."""
    path = shutil.which("dotnet")
    if path is None:
        raise CSharpError(
            "dotnet 이 없습니다. .NET 10 SDK 를 설치하세요 (macOS: brew install --cask dotnet-sdk)"
        )
    return path


def build_command() -> list[str]:
    """C# 콘솔을 빌드하는 명령 (바뀐 것이 없으면 몇 초 안에 끝남)."""
    return [
        dotnet(), "build", str(CLI_PROJECT), "-c", CONFIGURATION,
        "-nologo", "-v", "quiet", "-clp:NoSummary",
    ]  # fmt: skip


def cli_command(args: list[str]) -> list[str]:
    """빌드해 둔 C# 콘솔에 args 를 넘기는 명령."""
    return [dotnet(), str(CLI_DLL), *args]


def ensure_built() -> Path:
    """C# 콘솔을 빌드합니다. 실패하면 CSharpError (빌드 출력 끝부분을 담음). 반환: dll 경로."""
    proc = subprocess.run(
        build_command(), capture_output=True, text=True, encoding="utf-8", errors="replace"
    )
    if proc.returncode != 0 or not CLI_DLL.is_file():
        tail = "\n".join((proc.stdout + proc.stderr).strip().splitlines()[-20:])
        raise CSharpError(f"C# 콘솔 빌드에 실패했습니다 ({CLI_PROJECT}):\n{tail}")
    return CLI_DLL
