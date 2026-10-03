"""AfriSAR zip(ORNL DAAC 1577)에서 필요한 HDF5 파일만 꺼내 캐시 폴더에 둡니다.

원본 zip(약 15 GB)은 data/external/afrisar/Polarimetric_height_profile_1577.zip 에 둡니다.
NASA Earthdata 계정으로 ORNL DAAC 에서 받은 파일이며 저장소에는 넣지 않습니다.
꺼낸 파일은 data/cache/afrisar/data/<이름>.h5 에 생기고, 지워도 다시 꺼낼 수 있습니다.

다른 분석 스크립트는 ensure_member() 를 불러 쓰므로 미리 꺼내 둘 필요는 없습니다.

실행 예:
    uv run python analysis/afrisar/extract.py --list          # zip 안의 파일과 캐시 상태 보기
    uv run python analysis/afrisar/extract.py                 # 분석에 쓰는 4개 모두 꺼내기 (약 3.55 GB)
    uv run python analysis/afrisar/extract.py --fourier-only  # Fourier HV 2개만 (약 0.76 GB)
    uv run python analysis/afrisar/extract.py data/rabi-tomo-capon-hh.h5
"""

import argparse
import shutil
import sys
import zipfile
from pathlib import Path

from bpcg_studio.paths import CACHE, EXTERNAL

ZIP_PATH = EXTERNAL / "afrisar" / "Polarimetric_height_profile_1577.zip"
ZIP_TOP = "Polarimetric_height_profile_1577/"  # zip 안의 맨 위 폴더
CACHE_DIR = CACHE / "afrisar"

# 분석 스크립트가 쓰는 파일 (zip 맨 위 폴더 기준 경로)
FOURIER_MEMBERS = (
    "data/lope-tomo-fourier-hv.h5",
    "data/rabi-tomo-fourier-hv.h5",
)
CAPON_MEMBERS = (
    "data/lope-tomo-capon-hh.h5",
    "data/lope-tomo-capon-hv.h5",
)
NEEDED_MEMBERS = FOURIER_MEMBERS + CAPON_MEMBERS


def normalize(name: str) -> str:
    """'lope-tomo-fourier-hv.h5', 'data/…', 'Polarimetric_height_profile_1577/data/…' 를 모두
    'data/lope-tomo-fourier-hv.h5' 꼴로 바꿉니다."""
    name = name.replace("\\", "/").lstrip("/")
    if name.startswith(ZIP_TOP):
        name = name[len(ZIP_TOP) :]
    if "/" not in name:
        name = "data/" + name
    return name


def open_zip() -> zipfile.ZipFile:
    """원본 zip 을 엽니다. 없으면 어디에 두어야 하는지 알려 주고 멈춥니다."""
    if not ZIP_PATH.is_file():
        raise SystemExit(
            f"AfriSAR zip 이 없습니다: {ZIP_PATH}\n"
            "ORNL DAAC 1577 'AfriSAR: Polarimetric Height Profiles by TomoSAR' 를 "
            "NASA Earthdata 계정으로 받아 위 위치에 두세요 (data/README.md 참고)."
        )
    return zipfile.ZipFile(ZIP_PATH)


def member_path(name: str) -> Path:
    """캐시에 꺼낸 파일이 놓일 경로 (꺼냈는지와 상관없이)."""
    return CACHE_DIR / normalize(name)


def ensure_member(name: str) -> Path:
    """zip 안의 파일 하나를 캐시에 꺼내고 그 경로를 돌려줍니다. 이미 있으면 크기만 확인합니다."""
    name = normalize(name)
    dst = member_path(name)
    if dst.is_file() and not ZIP_PATH.is_file():
        return dst  # zip 은 없지만 예전에 꺼내 둔 파일이 있음
    with open_zip() as zf:
        try:
            info = zf.getinfo(ZIP_TOP + name)
        except KeyError:
            raise SystemExit(
                f"zip 안에 {ZIP_TOP + name} 이 없습니다. --list 로 이름을 확인하세요."
            ) from None
        if dst.is_file() and dst.stat().st_size == info.file_size:
            return dst
        dst.parent.mkdir(parents=True, exist_ok=True)
        tmp = dst.with_name(dst.name + ".part")
        print(f"꺼내는 중: {name} ({info.file_size / 1e9:.2f} GB) -> {dst}", flush=True)
        with zf.open(info) as src, open(tmp, "wb") as out:
            shutil.copyfileobj(src, out, length=16 * 1024 * 1024)  # zipfile 이 CRC 도 검사함
    if tmp.stat().st_size != info.file_size:
        raise SystemExit(f"꺼낸 크기가 다릅니다: {tmp} ({tmp.stat().st_size} != {info.file_size})")
    tmp.replace(dst)
    return dst


def tomo_file(site: str, method: str = "fourier", pol: str = "hv") -> Path:
    """'lope', 'capon', 'hh' -> data/lope-tomo-capon-hh.h5 를 꺼내 경로를 돌려줍니다."""
    return ensure_member(f"data/{site}-tomo-{method}-{pol}.h5")


def list_members() -> None:
    """zip 안의 h5 파일, 크기, 캐시에 꺼냈는지를 출력합니다."""
    with open_zip() as zf:
        infos = [i for i in zf.infolist() if not i.is_dir()]
    print(f"zip: {ZIP_PATH}")
    print(f"캐시: {CACHE_DIR}")
    print(f"{'파일':42s} {'크기 GB':>8s}  상태")
    for i in sorted(infos, key=lambda i: i.filename):
        name = normalize(i.filename)
        p = member_path(name)
        if p.is_file() and p.stat().st_size == i.file_size:
            state = "꺼냄"
        elif p.is_file():
            state = "크기 다름"
        else:
            state = "-"
        mark = " *" if name in NEEDED_MEMBERS else ""
        print(f"{name:42s} {i.file_size / 1e9:8.2f}  {state}{mark}")
    print("* = 분석 스크립트가 쓰는 파일")


def main() -> None:
    ap = argparse.ArgumentParser(description="AfriSAR zip 에서 HDF5 파일을 캐시로 꺼냅니다.")
    ap.add_argument(
        "names", nargs="*", help="꺼낼 파일 (예: data/lope-tomo-fourier-hv.h5). 비우면 분석용 4개"
    )
    ap.add_argument("--list", action="store_true", help="zip 안의 파일과 캐시 상태만 보여 줍니다")
    ap.add_argument(
        "--fourier-only", action="store_true", help="Fourier HV 2개(약 0.76 GB)만 꺼냅니다"
    )
    args = ap.parse_args()
    if args.list:
        list_members()
        return
    names = args.names or (FOURIER_MEMBERS if args.fourier_only else NEEDED_MEMBERS)
    for n in names:
        print(ensure_member(n))


if __name__ == "__main__":
    sys.exit(main())
