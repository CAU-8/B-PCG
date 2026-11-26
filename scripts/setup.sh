#!/usr/bin/env bash
# B-PCG 개발 환경 설치 스크립트 (macOS, Linux). Windows 는 scripts/setup.ps1 을 씁니다.
#
# 저장소 폴더에서 ./scripts/setup.sh --help 로 옵션을 볼 수 있습니다.
# 여러 번 실행해도 됩니다. 이미 있는 것은 건너뛰고, 이미 설치한 선택 묶음은 지우지 않습니다.
# macOS 기본 bash(3.2)에서도 돌도록 썼습니다 (연관 배열, ${x,,} 같은 bash 4 문법을 쓰지 않음).
set -euo pipefail

UV_VERSION="0.12.17"
UV_URL_BASE="https://github.com/astral-sh/uv/releases/download/${UV_VERSION}"
# uv 가 없으면 위 주소의 압축 파일을 받아, 아래 SHA-256 과 같을 때만 풉니다 (받은 설치 스크립트를 바로 실행하지
# 않음). 값은 GitHub 릴리스 정보(gh api repos/astral-sh/uv/releases/tags/0.12.17 의 digest)에서 가져왔습니다.
# UV_VERSION 을 바꾸면 함께 바꿉니다.
UV_SHA_MAC_ARM64="85f00cbdc6dd3e97eba4c31b4d014375a9fdfe8f570023b84e5102fc3456896b"
UV_SHA_MAC_X64="8dcf05a8c809bb3c471d2b614788ba27a6e41298fc8c31ac84b5f4339fd468e5"
UV_SHA_LINUX_X64="fa82fd8dde8e8eefdecada6aa0889666556cfceb690d06e0c3bca49eb3070a63"
UV_SHA_LINUX_ARM64="d636d1b678e9e7f367ecb22b46bd1cabbed234d6bc3b4d96365d2b507f72f86c"
GODOT_VERSION="4.7.2"
GODOT_TAG="${GODOT_VERSION}-stable"
GODOT_URL_BASE="https://github.com/godotengine/godot/releases/download/${GODOT_TAG}"
# 엔진(engine/)은 C# 이라 Godot .NET(mono) 판을 받습니다.
# 아래 값은 위 주소의 SHA512-SUMS.txt 에서 가져왔습니다. Godot 버전을 바꾸면 함께 바꿉니다.
SHA_MAC="0862c53d7158c7a67f745e2e46f90b68cf5343cbe8b95d6d4333c469e42ca104af9c121d1746a50e5d221a99d09d82ef7016495f8e0d09255842884ed0502795"
SHA_LINUX_X64="1855960b27ee3ef5e66e5e228cced69d55637b24334a7411162687dcd077d8f9f645348cdb8eae984bec8135d49ed855a1e3a16476786b8bce60774fd8402d13"
SHA_LINUX_ARM64="4b8b700ea21bea16b1a2ed8bc3a266431604f0d6452c819e022bfadea33eb431269bd680b11cd9dab93fc1f487ae4a60d15ee511e5691a82c22a2753b428d263"
MIN_GLIBC="2.28"       # 여러 패키지의 Linux wheel 이 manylinux_2_27, manylinux_2_28 (glibc 2.28 이상) 용입니다.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
TOOLS_DIR="$ROOT/.tools"
GODOT_DIR="$TOOLS_DIR/godot-net"
cd "$ROOT"

usage() {
  cat <<'EOF'
B-PCG 개발 환경 설치 (macOS, Linux)

쓰는 법: ./scripts/setup.sh [옵션...]

  (옵션 없음)   uv, 파이썬 3.13, 기본 패키지 묶음(dev: 스튜디오와 시험에 쓰는 패키지)을 설치합니다.
                생성기·엔진은 C# 이라 .NET 10 SDK 가 따로 있어야 합니다 (없으면 설치 명령을 알려 줌).
  --analysis    비교·분석 도구(pyflwdir, landlab, numba, h5py)도 설치합니다. Intel Mac 에서는 안 됩니다.
  --gpl         GPL 비교 도구(fastscapelib, TopoToolbox)도 설치합니다. Linux ARM64 에서는 안 됩니다.
  --notebook    JupyterLab 도 설치합니다. Intel Mac 에서는 안 됩니다.
  --godot       Godot 4.7.2 .NET 판을 .tools/godot-net/ 에 받습니다 (macOS 약 200 MB, Linux 약 110 MB).
  --data        파일럿 데이터를 받습니다 (약 4.5 GB, 오래 걸립니다. 끊겨도 다시 실행하면 이어 받습니다).
  --all         --analysis --gpl --notebook --godot 와 data 묶음(자료를 읽는 패키지)을 한꺼번에
                (파일럿 데이터 받기는 빼고).
  --check       아무것도 설치하지 않고 환경 점검(tools/doctor.py)만 합니다.
  -h, --help    이 도움말을 봅니다.

여러 번 실행해도 됩니다. 이미 있는 것은 건너뛰고, 이미 설치한 선택 묶음은 지우지 않습니다.
EOF
}

# WITH_DATA 는 파일럿 데이터 받기, WITH_DATA_GROUP 은 자료를 읽는 패키지 묶음(data) 설치입니다.
WITH_ANALYSIS=0; WITH_GPL=0; WITH_NOTEBOOK=0; WITH_GODOT=0; WITH_DATA=0; WITH_DATA_GROUP=0; CHECK_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --analysis) WITH_ANALYSIS=1 ;;
    --gpl) WITH_GPL=1 ;;
    --notebook) WITH_NOTEBOOK=1 ;;
    --godot) WITH_GODOT=1 ;;
    --data) WITH_DATA=1 ;;
    --all) WITH_ANALYSIS=1; WITH_GPL=1; WITH_NOTEBOOK=1; WITH_GODOT=1; WITH_DATA_GROUP=1 ;;
    --check) CHECK_ONLY=1 ;;
    -h|--help) usage; exit 0 ;;
    *) printf '모르는 옵션입니다: %s\n\n' "$arg" >&2; usage >&2; exit 2 ;;
  esac
done

if [ -t 1 ]; then
  C_BLUE=$'\033[1;34m'; C_YELLOW=$'\033[1;33m'; C_RED=$'\033[1;31m'; C_OFF=$'\033[0m'
else
  C_BLUE=""; C_YELLOW=""; C_RED=""; C_OFF=""
fi
say()  { printf '%s==>%s %s\n' "$C_BLUE" "$C_OFF" "$*"; }
warn() { printf '%s[주의]%s %s\n' "$C_YELLOW" "$C_OFF" "$*" >&2; }
die()  { printf '%s[실패]%s %s\n' "$C_RED" "$C_OFF" "$*" >&2; exit 1; }

# $1 >= $2 이면 참. 점으로 나눈 숫자 버전을 비교합니다 (예: 2.28, 0.12.17).
version_ge() {
  local -a a b
  IFS=. read -r -a a <<< "$1"
  IFS=. read -r -a b <<< "$2"
  local i=0 x y n=${#a[@]}
  [ "${#b[@]}" -gt "$n" ] && n=${#b[@]}
  while [ "$i" -lt "$n" ]; do
    x="${a[$i]:-0}"; y="${b[$i]:-0}"
    x="${x%%[!0-9]*}"; y="${y%%[!0-9]*}"
    x=$((10#${x:-0})); y=$((10#${y:-0}))
    [ "$x" -gt "$y" ] && return 0
    [ "$x" -lt "$y" ] && return 1
    i=$((i + 1))
  done
  return 0
}

STARTED=$SECONDS
step_time() { printf '    (%d초 걸림)\n' "$((SECONDS - $1))"; }

# ---------------------------------------------------------------- 1. 운영체제와 CPU
OS="$(uname -s)"; ARCH="$(uname -m)"
case "$OS" in
  Darwin) PLATFORM="mac" ;;
  Linux) PLATFORM="linux" ;;
  MINGW*|MSYS*|CYGWIN*)
    die "Windows 의 Git Bash 에서는 이 스크립트를 쓰지 않습니다. PowerShell 에서 실행하세요:
      powershell -ExecutionPolicy Bypass -File scripts\\setup.ps1" ;;
  *) die "지원하지 않는 운영체제입니다: $OS (macOS, Linux, Windows 만 지원)" ;;
esac
case "$ARCH" in
  arm64|aarch64) ARCH="arm64" ;;
  x86_64|amd64) ARCH="x86_64" ;;
  *) die "지원하지 않는 CPU 입니다: $ARCH (x86_64, arm64 만 지원)" ;;
esac

# 설치 전에 이 컴퓨터에서 패키지가 깔릴 수 있는지 먼저 봅니다. (--check 에서는 doctor 가 같은 점검을 합니다.)
# 맥은 묶음마다 설치 파일(wheel)이 나온 가장 낮은 macOS 판이 다릅니다. 아래 판은 uv.lock 의 wheel 이름에서
# 읽었고, 패키지 판을 올리면 다시 봅니다. 확인하는 명령 (아무것도 설치하지 않음):
#   MACOSX_DEPLOYMENT_TARGET=14.0 uv sync --locked --no-build --dry-run \
#     --python-platform aarch64-apple-darwin --group analysis
platform_check() {
  if [ "$PLATFORM" = mac ]; then
    local ver need why
    ver="$(sw_vers -productVersion 2>/dev/null || echo 0)"
    say "운영체제: macOS $ver ($ARCH)"
    if [ "$ARCH" = x86_64 ]; then
      if [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = 1 ]; then
        die "터미널이 Rosetta(x86_64)로 돌고 있습니다. Apple Silicon 그대로 실행하세요:
      arch -arm64 /bin/bash ./scripts/setup.sh $*"
      fi
      if [ "$WITH_ANALYSIS" = 1 ] || [ "$WITH_NOTEBOOK" = 1 ]; then
        die "Intel Mac 에서는 --analysis 와 --notebook 을 쓸 수 없습니다 (--all 에도 들어 있습니다).
      잠근 numba 0.68(analysis 묶음)과 argon2-cffi-bindings 26.1(notebook 묶음)이 macOS x86_64 용
      wheel 을 내지 않아, uv 가 소스에서 빌드해야 합니다. 두 옵션을 빼고 다시 실행하세요.
      기본 설치와 --gpl, --godot, --data 는 Intel Mac 에서도 됩니다."
      fi
      need="10.15"; why="잠근 scipy 1.18.1 의 Intel Mac 용 wheel 이 macosx_10_15 용입니다"
    else
      need="12.0"; why="잠근 scipy 1.18.1 의 Apple Silicon 용 wheel 이 macosx_12_0 용입니다"
      if [ "$WITH_ANALYSIS" = 1 ]; then
        need="14.0"; why="analysis 묶음의 netcdf4 1.7.4 가 Apple Silicon 용 wheel 을 macosx_14_0 용으로만 냅니다"
      fi
    fi
    if [ "$WITH_GPL" = 1 ] || [ "$WITH_DATA_GROUP" = 1 ]; then
      need="15.0"; why="gpl·data 묶음의 rasterio 1.5.2 가 macOS 용 wheel 을 macosx_15_0 용으로만 냅니다"
    fi
    version_ge "$ver" "$need" || die "macOS $need 이상이 필요합니다. 지금은 $ver 입니다.
      까닭: $why.
      더 낮은 판에서는 uv 가 그 패키지를 소스에서 빌드하려다 실패합니다.
      시스템 설정 > 일반 > 소프트웨어 업데이트에서 올리거나, 그 묶음을 설치하는 옵션을 빼고 다시 실행하세요."
  else
    local glibc=""
    glibc="$(getconf GNU_LIBC_VERSION 2>/dev/null | awk '{print $2}')" || true
    if [ -z "$glibc" ]; then
      glibc="$({ ldd --version 2>&1 || true; } | head -n 1 | grep -oE '[0-9]+\.[0-9]+$' || true)"
    fi
    if [ -z "$glibc" ]; then
      if { ldd --version 2>&1 || true; } | grep -qi musl; then
        die "musl libc 배포판(Alpine 등)은 지원하지 않습니다. 패키지 wheel 이 glibc 용(manylinux)입니다.
      Ubuntu 20.04+, Debian 10+, Fedora, Arch 처럼 glibc $MIN_GLIBC 이상인 배포판을 쓰세요."
      fi
      warn "glibc 버전을 알아내지 못했습니다. glibc $MIN_GLIBC 이상이어야 합니다. 계속 진행합니다."
      say "운영체제: Linux ($ARCH)"
    else
      say "운영체제: Linux ($ARCH), glibc $glibc"
      version_ge "$glibc" "$MIN_GLIBC" || die "glibc $MIN_GLIBC 이상이 필요합니다. 지금은 $glibc 입니다.
      까닭: numpy, scipy, pillow 와 선택 묶음 패키지의 Linux wheel 이 manylinux_2_27, manylinux_2_28 용이라
      더 낮은 glibc 에서는 설치되지 않습니다.
      Ubuntu 20.04+, Debian 10+, RHEL/Rocky 8+ 처럼 더 새 배포판을 쓰세요."
    fi
    if [ "$ARCH" = arm64 ]; then
      warn "Linux ARM64 에는 GPL 묶음(fastscapelib, TopoToolbox)의 wheel 이 없어 --gpl 을 쓸 수 없습니다."
    fi
    if grep -qi microsoft /proc/version 2>/dev/null; then
      case "$ROOT" in
        /mnt/[a-zA-Z]/*) warn "WSL 에서 Windows 드라이브($ROOT)에 저장소를 두면 매우 느립니다. ~/ 아래로 옮기기를 권합니다." ;;
      esac
    fi
  fi
}

# ---------------------------------------------------------------- uv 찾기
# uv 를 둘 폴더는 공식 설치 스크립트와 같은 순서로 정합니다 (그 스크립트로 이미 깐 uv 도 찾도록):
# UV_INSTALL_DIR, XDG_BIN_HOME, XDG_DATA_HOME/../bin, ~/.local/bin
if [ -n "${UV_INSTALL_DIR:-}" ]; then UV_BIN_DIR="$UV_INSTALL_DIR"
elif [ -n "${XDG_BIN_HOME:-}" ]; then UV_BIN_DIR="$XDG_BIN_HOME"
elif [ -n "${XDG_DATA_HOME:-}" ]; then UV_BIN_DIR="$XDG_DATA_HOME/../bin"
else UV_BIN_DIR="$HOME/.local/bin"
fi
UV_ON_USER_PATH=1
find_uv() {
  if command -v uv >/dev/null 2>&1; then return 0; fi
  if [ -x "$UV_BIN_DIR/uv" ]; then
    export PATH="$UV_BIN_DIR:$PATH"   # 이번 실행 동안만 PATH 에 더합니다.
    UV_ON_USER_PATH=0
    return 0
  fi
  return 1
}

# ---------------------------------------------------------------- --check: 점검만
if [ "$CHECK_ONLY" = 1 ]; then
  say "환경 점검만 합니다 (아무것도 설치하지 않음)."
  if find_uv; then
    if [ -d "$ROOT/.venv" ]; then
      exec uv run --no-sync python tools/doctor.py
    fi
    # .venv 가 없을 때 uv run 은 빈 .venv 를 만들어 버리므로, 프로젝트 밖 파이썬으로 점검합니다.
    warn ".venv 가 아직 없습니다. 설치하려면: ./scripts/setup.sh"
    exec uv run --no-project python tools/doctor.py
  fi
  # uv 가 없어도 doctor 는 표준 라이브러리만으로 돌아 무엇이 빠졌는지 알려 줍니다.
  # macOS 에서 개발 도구(CLT)가 없으면 /usr/bin/python3 는 설치 창만 띄우므로 건너뜁니다.
  if command -v python3 >/dev/null 2>&1 && { [ "$PLATFORM" != mac ] || xcode-select -p >/dev/null 2>&1; }; then
    warn "uv 가 없어 시스템 python3 로 점검합니다. 설치하려면: ./scripts/setup.sh"
    exec python3 tools/doctor.py
  fi
  die "uv 가 없습니다. 먼저 설치하세요: ./scripts/setup.sh"
fi

platform_check "$@"
if [ "$WITH_GPL" = 1 ] && [ "$PLATFORM" = linux ] && [ "$ARCH" = arm64 ]; then
  warn "--gpl 은 Linux ARM64 에서 빼고 진행합니다."
  WITH_GPL=0
fi
if [ "$(id -u)" = 0 ]; then
  warn "root(sudo)로 실행 중입니다. 컨테이너가 아니라면 일반 사용자로 실행하세요 (.venv 와 uv 가 root 소유가 됩니다)."
fi

# ---------------------------------------------------------------- 2. 시스템 도구 (설치는 안내만)
install_hint() {
  if [ "$PLATFORM" = mac ]; then
    case "$1" in
      git) echo "xcode-select --install   (또는 brew install git)" ;;
      *) echo "brew install $1" ;;
    esac
  elif command -v apt-get >/dev/null 2>&1; then echo "sudo apt-get update && sudo apt-get install -y $1"
  elif command -v dnf >/dev/null 2>&1; then echo "sudo dnf install -y $1"
  elif command -v pacman >/dev/null 2>&1; then echo "sudo pacman -S --needed $1"
  elif command -v zypper >/dev/null 2>&1; then echo "sudo zypper install -y $1"
  else echo "배포판의 패키지 관리자로 $1 을 설치하세요"
  fi
}
for tool in git curl; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool 이 없습니다. 먼저 설치하세요: $(install_hint "$tool")"
done
if ! command -v unzip >/dev/null 2>&1; then
  if [ "$WITH_GODOT" = 1 ] && [ "$PLATFORM" = linux ]; then
    die "unzip 이 없습니다 (Godot 압축 풀기에 필요). 먼저 설치하세요: $(install_hint unzip)"
  fi
  warn "unzip 이 없습니다. --godot 을 쓰려면 설치하세요: $(install_hint unzip)"
fi
say "$(git --version), curl 확인"

# .NET 10 SDK: 생성기(src/Bpcg)·콘솔·엔진(engine/)이 C# 입니다. 시스템에 설치하도록 안내만 합니다
# (Godot 편집기가 Finder·탐색기에서 열려도 SDK 를 찾게 하려는 것, docs/csharp_port.md 1장).
dotnet_hint() {
  if [ "$PLATFORM" = mac ]; then echo "brew install --cask dotnet-sdk   (또는 https://dotnet.microsoft.com/download)"
  else echo "https://learn.microsoft.com/dotnet/core/install/linux 의 배포판 안내대로 dotnet-sdk-10.0 을 설치"
  fi
}
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -qE '^10\.'; then
  say ".NET SDK $(dotnet --list-sdks | grep -E '^10\.' | tail -n 1 | awk '{print $1}') 확인"
else
  warn ".NET 10 SDK 가 없습니다. 생성기와 시험에 필요합니다: $(dotnet_hint)"
fi

# ---------------------------------------------------------------- 3. uv (파이썬과 패키지 관리자)
sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  else shasum -a 256 "$1" | awk '{print $1}'; fi
}
# GitHub 릴리스의 uv 압축 파일을 받아 SHA-256 이 UV_SHA_* 와 같은지 본 뒤, uv 와 uvx 를 $UV_BIN_DIR 에 둡니다.
# 값이 다르면 받은 파일을 지우고 아무것도 두지 않은 채 멈춥니다.
install_uv() {
  local target sha url tmp got dir exe
  case "$PLATFORM-$ARCH" in
    mac-arm64) target="aarch64-apple-darwin"; sha="$UV_SHA_MAC_ARM64" ;;
    mac-x86_64) target="x86_64-apple-darwin"; sha="$UV_SHA_MAC_X64" ;;
    linux-x86_64) target="x86_64-unknown-linux-gnu"; sha="$UV_SHA_LINUX_X64" ;;
    linux-arm64) target="aarch64-unknown-linux-gnu"; sha="$UV_SHA_LINUX_ARM64" ;;
    *) die "이 운영체제($PLATFORM-$ARCH)용 uv 내려받기는 준비돼 있지 않습니다." ;;
  esac
  url="$UV_URL_BASE/uv-$target.tar.gz"
  tmp="$(mktemp -d "${TMPDIR:-/tmp}/bpcg-uv.XXXXXX")"
  say "uv $UV_VERSION 받기: $url"
  if ! curl -fL --retry 3 --retry-delay 2 --progress-bar -o "$tmp/uv.tar.gz" "$url"; then
    rm -rf "$tmp"
    die "uv 내려받기에 실패했습니다. 인터넷 연결을 확인하고 다시 실행하세요."
  fi
  got="$(sha256_of "$tmp/uv.tar.gz")"
  if [ "$got" != "$sha" ]; then
    rm -rf "$tmp"
    die "받은 uv 파일의 SHA-256 이 스크립트에 적은 값과 다릅니다. 받은 파일은 지웠습니다.
      받은 파일: $got
      적은 값:   $sha
      다시 실행해도 다르면 팀에 알려 주세요."
  fi
  if ! tar -xzf "$tmp/uv.tar.gz" -C "$tmp"; then
    rm -rf "$tmp"
    die "uv 압축 파일을 풀지 못했습니다: $url"
  fi
  dir="$tmp/uv-$target"
  mkdir -p "$UV_BIN_DIR"
  for exe in uv uvx; do
    if [ ! -f "$dir/$exe" ]; then
      rm -rf "$tmp"
      die "uv 압축 파일 안에 uv-$target/$exe 가 없습니다: $url"
    fi
    # 임시 이름으로 복사한 뒤 바꿔, 중간에 멈춰도 반쯤 쓴 실행 파일이 남지 않게 합니다.
    cp "$dir/$exe" "$UV_BIN_DIR/.$exe.bpcg-part"
    chmod 755 "$UV_BIN_DIR/.$exe.bpcg-part"
    mv -f "$UV_BIN_DIR/.$exe.bpcg-part" "$UV_BIN_DIR/$exe"
  done
  rm -rf "$tmp"
}

UV_INSTALLED_NOW=0
if ! find_uv; then
  t=$SECONDS
  say "uv $UV_VERSION 설치 ($UV_BIN_DIR). 셸 설정 파일은 건드리지 않습니다."
  install_uv
  hash -r
  find_uv || die "uv 를 설치했지만 $UV_BIN_DIR 에서 찾지 못했습니다. 새 터미널을 열고 다시 실행하세요."
  UV_INSTALLED_NOW=1
  step_time "$t"
fi
UV_HAVE="$(uv --version | awk '{print $2}')"
version_ge "$UV_HAVE" "$UV_VERSION" || die "uv $UV_HAVE 는 너무 오래됐습니다. $UV_VERSION 이상이 필요합니다.
      올리기: uv self update   (Homebrew 로 깔았다면 brew upgrade uv)
      이 스크립트가 $UV_BIN_DIR 에 깐 uv 는 uv self update 가 되지 않습니다.
      그 폴더의 uv, uvx 를 지운 뒤 이 스크립트를 다시 실행하세요."
say "uv $UV_HAVE ($(command -v uv))"

# ---------------------------------------------------------------- 4. 파이썬 + 패키지
# 저장소 폴더를 옮기거나 이름을 바꾸면 .venv 안의 경로(편집 설치 .pth, 실행 파일 첫 줄)가 옛 위치를 가리킵니다.
# 그런 .venv 는 다시 만드는 편이 확실합니다. .venv 는 uv 가 언제든 다시 만들 수 있습니다.
venv_is_stale() {
  [ -d "$ROOT/.venv" ] || return 1
  local pth line real
  for pth in "$ROOT"/.venv/lib/python3*/site-packages/*.pth; do
    [ -f "$pth" ] || continue
    while IFS= read -r line || [ -n "$line" ]; do
      case "$line" in
        /*)
          real="$(cd "$line" 2>/dev/null && pwd -P)" || return 0
          case "$real/" in "$ROOT"/*) ;; *) return 0 ;; esac ;;
      esac
    done < "$pth"
  done
  return 1
}
if venv_is_stale; then
  warn "저장소 폴더가 옮겨져 .venv 가 옛 경로를 가리킵니다. .venv 를 지우고 새로 만듭니다."
  rm -rf "$ROOT/.venv"
fi

SYNC_ARGS=(--locked --inexact)
GROUP_NOTE="기본(dev)"
if [ "$WITH_ANALYSIS" = 1 ]; then SYNC_ARGS+=(--group analysis); GROUP_NOTE="$GROUP_NOTE + analysis"; fi
if [ "$WITH_GPL" = 1 ]; then SYNC_ARGS+=(--group gpl); GROUP_NOTE="$GROUP_NOTE + gpl"; fi
if [ "$WITH_NOTEBOOK" = 1 ]; then SYNC_ARGS+=(--group notebook); GROUP_NOTE="$GROUP_NOTE + notebook"; fi
if [ "$WITH_DATA_GROUP" = 1 ]; then SYNC_ARGS+=(--group data); GROUP_NOTE="$GROUP_NOTE + data"; fi

# 파이썬 버전은 .python-version, 패키지 버전은 uv.lock 이 정합니다. 파이썬도 uv 가 직접 받습니다.
t=$SECONDS
say "파이썬 $(tr -d '[:space:]' < .python-version)과 패키지 설치: $GROUP_NOTE"
say "  uv sync ${SYNC_ARGS[*]}"
uv sync "${SYNC_ARGS[@]}" || die "uv sync 가 실패했습니다. 위 오류를 확인하세요.
      'lockfile needs to be updated' 라면 pyproject.toml 을 바꾼 사람이 uv lock 으로 uv.lock 도 갱신해 올려야 합니다."
step_time "$t"

# ---------------------------------------------------------------- 5. Godot (선택)
# Godot 은 언제나 임시 HOME 으로 실행합니다. 그래야 사용자의 Godot 편집기 설정을 건드리지 않습니다.
godot_version_of() {
  local exe="$1" tmp out
  [ -n "$exe" ] && [ -f "$exe" ] && [ -x "$exe" ] || return 1
  tmp="$(mktemp -d "${TMPDIR:-/tmp}/bpcg-godot-home.XXXXXX")"
  out="$(HOME="$tmp" XDG_CONFIG_HOME="$tmp/config" XDG_DATA_HOME="$tmp/data" XDG_CACHE_HOME="$tmp/cache" \
    "$exe" --headless --version 2>/dev/null </dev/null)" || true
  rm -rf "$tmp"
  out="$(printf '%s\n' "$out" | grep -E '^[0-9]+\.[0-9]+' | tail -n 1)" || true
  [ -n "$out" ] || return 1
  printf '%s\n' "$out"
}
find_godot() {
  local c v mono="Godot_v${GODOT_TAG}_mono"
  for c in "${GODOT_NET:-}" "${GODOT:-}" \
    "$GODOT_DIR/Godot_mono.app/Contents/MacOS/Godot" \
    "$GODOT_DIR/${mono}_linux_x86_64/${mono}_linux.x86_64" \
    "$GODOT_DIR/${mono}_linux_arm64/${mono}_linux.arm64"; do
    [ -n "$c" ] || continue
    v="$(godot_version_of "$c")" || continue
    case "$v" in
      "${GODOT_VERSION}.stable.mono"*) printf '%s\n' "$c"; return 0 ;;
      *) warn "Godot $v 이 있지만 $GODOT_VERSION .NET 판이 아니라 쓰지 않습니다: $c" ;;
    esac
  done
  return 1
}
sha512_of() {
  if command -v sha512sum >/dev/null 2>&1; then sha512sum "$1" | awk '{print $1}'
  else shasum -a 512 "$1" | awk '{print $1}'; fi
}
install_godot() {
  local zip sha url part extract item
  case "$PLATFORM-$ARCH" in
    mac-*) zip="Godot_v${GODOT_TAG}_mono_macos.universal.zip"; sha="$SHA_MAC" ;;  # Apple Silicon, Intel 겸용
    linux-x86_64) zip="Godot_v${GODOT_TAG}_mono_linux_x86_64.zip"; sha="$SHA_LINUX_X64" ;;
    linux-arm64) zip="Godot_v${GODOT_TAG}_mono_linux_arm64.zip"; sha="$SHA_LINUX_ARM64" ;;
    *) die "이 운영체제($PLATFORM-$ARCH)용 Godot 내려받기는 준비돼 있지 않습니다." ;;
  esac
  url="$GODOT_URL_BASE/$zip"
  part="$TOOLS_DIR/$zip.part"
  mkdir -p "$GODOT_DIR"
  if [ -f "$part" ] && [ "$(sha512_of "$part")" = "$sha" ]; then
    say "이미 받아 둔 $zip 을 씁니다."
  else
    say "Godot $GODOT_VERSION .NET 판 받기: $url"
    # 끊긴 파일이 있으면 이어 받고, 이어받기가 안 되면 처음부터 받습니다.
    if ! curl -fL --retry 3 --retry-delay 2 --progress-bar -C - -o "$part" "$url"; then
      rm -f "$part"
      curl -fL --retry 3 --retry-delay 2 --progress-bar -o "$part" "$url" \
        || { rm -f "$part"; die "Godot 내려받기에 실패했습니다. 인터넷 연결을 확인하고 다시 실행하세요."; }
    fi
  fi
  if [ "$(sha512_of "$part")" != "$sha" ]; then
    rm -f "$part"
    die "받은 Godot 파일의 SHA-512 가 공식 값과 다릅니다. 지웠으니 다시 실행하세요. 계속 다르면 팀에 알려 주세요."
  fi
  say "SHA-512 확인 완료. 압축을 풉니다: $GODOT_DIR"
  # 임시 폴더에 푼 뒤 옮깁니다. 중간에 멈춰도 반쯤 풀린 Godot 이 남지 않습니다.
  extract="$TOOLS_DIR/godot.extract"
  rm -rf "$extract"
  mkdir -p "$extract"
  if [ "$PLATFORM" = mac ]; then
    ditto -x -k "$part" "$extract"   # macOS 기본 도구. 앱 묶음의 서명·링크를 그대로 풉니다.
  else
    unzip -q "$part" -d "$extract"
  fi
  for item in "$extract"/* "$extract"/.[!.]*; do
    [ -e "$item" ] || continue
    rm -rf "$GODOT_DIR/$(basename "$item")"
    mv "$item" "$GODOT_DIR/"
  done
  rm -rf "$extract" "$part"
  if [ "$PLATFORM" = linux ]; then
    # Linux zip 은 실행 파일과 GodotSharp/ 를 한 폴더에 담습니다. Godot 은 GodotSharp/ 를 실행 파일
    # 옆에서 찾으므로 폴더째 둡니다 (tests/test_engine.py 가 찾는 곳).
    for item in "$GODOT_DIR"/Godot_v"${GODOT_TAG}"_mono_linux_*/Godot_v"${GODOT_TAG}"_mono_linux.*; do
      [ -f "$item" ] && chmod +x "$item"
    done
  fi
}

GODOT_FOUND=""
if [ "$WITH_GODOT" = 1 ]; then
  t=$SECONDS
  if GODOT_FOUND="$(find_godot)"; then
    say "Godot $GODOT_VERSION .NET 판이 이미 있습니다: $GODOT_FOUND"
  else
    install_godot
    GODOT_FOUND="$(find_godot)" || die "Godot 을 풀었지만 실행되지 않습니다. $GODOT_DIR 을 지우고 다시 실행하세요."
    say "Godot .NET 판 설치 완료: $GODOT_FOUND"
  fi
  step_time "$t"
fi

# ---------------------------------------------------------------- 6. 파일럿 데이터 (선택)
if [ "$WITH_DATA" = 1 ]; then
  t=$SECONDS
  say "파일럿 데이터 받기 (약 4.5 GB). 중간에 끊겨도 다시 실행하면 이어 받습니다."
  uv run --no-sync python tools/download_pilot.py || die "파일럿 데이터 받기에 실패했습니다. 다시 실행하면 이어 받습니다."
  step_time "$t"
fi

# ---------------------------------------------------------------- 7. 점검
say "환경 점검 (tools/doctor.py)"
DOCTOR_RC=0
uv run --no-sync python tools/doctor.py || DOCTOR_RC=$?

echo
if [ "$DOCTOR_RC" != 0 ]; then
  die "점검에서 필수 항목이 실패했습니다. 위 [실패] 줄을 확인하고 고친 뒤 다시 실행하세요. (전체 $((SECONDS - STARTED))초)"
fi
say "설치가 끝났습니다. (전체 $((SECONDS - STARTED))초)"
cat <<EOF

다음 단계
  - 가상환경을 켤(activate) 필요가 없습니다. 명령 앞에 uv run 을 붙이면 저장소의 .venv 로 돌아갑니다.
  - 빠른 테스트:      uv run pytest -m "not slow"   (C# 콘솔을 빌드해 결과를 봄, .NET 10 SDK 필요)
  - C# 빌드·시험:     dotnet build Bpcg.slnx   그리고   dotnet test --solution Bpcg.slnx
  - 행성 만들기:      uv run bpcg all --profile tiny   (C# 콘솔로 넘김)   ·   스튜디오: uv run bpcg studio
  - 린트와 서식:      uv run ruff check .   그리고   uv run ruff format .
  - 점검만 다시:      ./scripts/setup.sh --check
EOF
if [ -n "$GODOT_FOUND" ]; then
  echo "  - Godot .NET:       $GODOT_FOUND --path engine -e"
  echo "                      (다른 위치의 Godot .NET 판을 쓰려면 환경 변수 GODOT_NET 에 경로를 넣습니다)"
else
  echo "  - Godot 4.7.2 .NET 판 받기: ./scripts/setup.sh --godot   (엔진 담당은 필수)"
fi
if [ "$WITH_DATA" != 1 ]; then
  echo "  - 파일럿 데이터:    ./scripts/setup.sh --data   (약 4.5 GB)"
fi
if [ "$UV_INSTALLED_NOW" = 1 ] || [ "$UV_ON_USER_PATH" = 0 ]; then
  cat <<EOF

[중요] uv 가 있는 $UV_BIN_DIR 이 아직 PATH 에 없습니다. 이번 실행에서만 임시로 더했습니다.
  새 터미널에서도 uv 를 쓰려면 한 번만 실행하세요 (셸 설정 파일에 PATH 를 더합니다):
      "$UV_BIN_DIR/uv" tool update-shell
  또는 셸 설정 파일(~/.zshrc, ~/.bashrc 등)에 직접 한 줄을 더합니다:
      export PATH="$UV_BIN_DIR:\$PATH"
EOF
fi
