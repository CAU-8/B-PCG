"""PyPI 에 우리 파이썬 버전용 휠(미리 빌드된 설치 파일)이 있는지 패키지별로 확인합니다.

표준 라이브러리만 씁니다. PyPI JSON API(https://pypi.org/pypi/<이름>/json)의 최신 버전
파일 목록만 읽고, 아무것도 설치하지 않습니다. 운영체제별(macOS arm64, Windows x64,
Linux x86_64) cpXY 휠 개수, 순수 파이썬 휠, abi3 휠, 소스 배포 여부와 라이선스를
한 줄씩 보여 줍니다. 파이썬 버전은 기본으로 저장소의 .python-version 을 따릅니다.

실행 예:
    uv run python tools/check_wheels.py                    # 기본 목록, .python-version 기준
    uv run python tools/check_wheels.py --py 3.14 richdem pysheds
"""

import argparse
import json
import ssl
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

DEFAULT_PACKAGES = [
    "pysheds", "richdem", "whitebox", "whitebox-workflows", "topotoolbox", "lsdtopytools",
    "rasterio", "pyproj", "xarray", "rioxarray", "pystac-client", "planetary-computer", "numba",
    "geopandas", "shapely", "pyogrio", "fiona", "dask", "earthengine-api", "gdal", "landlab",
    "odc-stac", "stackstac", "xdem", "pyflwdir", "fastscapelib", "h5py", "scikit-image", "trimesh",
]  # fmt: skip


def ssl_context() -> ssl.SSLContext:
    """운영체제 기본 인증서. 비어 있으면 certifi(있을 때만)를 씁니다."""
    ctx = ssl.create_default_context()
    if ctx.cert_store_stats().get("x509_ca", 0) == 0:
        try:
            import certifi
        except ImportError:
            return ctx
        ctx = ssl.create_default_context(cafile=certifi.where())
    return ctx


def default_py() -> str:
    """저장소 .python-version 의 '3.13' 같은 값. 없으면 지금 파이썬 버전."""
    try:
        v = (ROOT / ".python-version").read_text(encoding="utf-8").strip()
        major, minor = v.split(".")[:2]
        return f"{major}.{minor}"
    except (OSError, ValueError):
        return f"{sys.version_info.major}.{sys.version_info.minor}"


def fetch(name: str, ctx: ssl.SSLContext) -> dict | None:
    """PyPI JSON. 없는 패키지면 None."""
    url = f"https://pypi.org/pypi/{name}/json"
    try:
        with urllib.request.urlopen(url, timeout=30, context=ctx) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        raise


def tag(filename: str) -> str:
    """휠 파일 이름의 마지막 세 부분 (파이썬-ABI-플랫폼 태그)."""
    return "-".join(filename[:-4].split("-")[-3:])


def summarize(name: str, d: dict, cp: str) -> str:
    info = d["info"]
    files = d["urls"]
    rel = files[0]["upload_time"][:10] if files else "?"
    wheels = sorted({f["filename"] for f in files if f["packagetype"] == "bdist_wheel"})
    cpw = [w for w in wheels if f"-{cp}-" in w]
    mac = [w for w in cpw if "macosx" in w and ("arm64" in w or "universal2" in w)]
    win = [w for w in cpw if "win_amd64" in w]
    lin = [w for w in cpw if "manylinux" in w and "x86_64" in w]
    pure = any("none-any" in w for w in wheels)
    abi3 = [tag(w) for w in wheels if "abi3" in w][:3]
    sdist = any(f["packagetype"] == "sdist" for f in files)
    lic = info.get("license_expression") or (info.get("license") or "")[:40]
    line = (
        f"{name:20s} v{info['version']:12s} {rel} req={info.get('requires_python')} "
        f"{cp}: mac={len(mac)} win={len(win)} lin={len(lin)} pure={pure} abi3={abi3} "
        f"sdist={sdist} lic={lic!r}"
    )
    if not cpw and not pure and not abi3 and wheels:
        line += "\n     최신 휠 태그: " + ", ".join(sorted({tag(w) for w in wheels})[:10])
    return line


def main() -> None:
    ap = argparse.ArgumentParser(description="PyPI 휠 지원 여부를 확인합니다.")
    ap.add_argument("packages", nargs="*", help="확인할 패키지 (비우면 기본 목록)")
    ap.add_argument(
        "--py", default=default_py(), help="파이썬 버전 (예: 3.13). 기본 .python-version"
    )
    args = ap.parse_args()
    cp = "cp" + args.py.replace(".", "")
    ctx = ssl_context()
    print(f"기준: {cp} (Python {args.py}); 최신 버전의 파일만 봅니다")
    for name in args.packages or DEFAULT_PACKAGES:
        try:
            d = fetch(name, ctx)
        except Exception as e:  # 네트워크 오류 등은 그 패키지만 건너뜀
            print(f"{name:20s} 오류: {e}")
            continue
        if d is None:
            print(f"{name:20s} PyPI 에 없음")
            continue
        print(summarize(name, d, cp))


if __name__ == "__main__":
    main()
