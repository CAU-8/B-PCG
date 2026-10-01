# /// script
# requires-python = ">=3.11"
# dependencies = []
# ///
"""파일럿 데이터를 내려받고 검사합니다 (설계 문서 3장 '실제 강에게 규칙 배우기'에 씁니다).

받는 자료 (모두 공개, 로그인 없음). 출처·라이선스·주의할 점은 data/README.md 에 있습니다.
  - ETOPO 2022 60초 고도 (육지+해저)          NOAA NCEI, 퍼블릭 도메인
  - CHELSA V2.1 연강수 bio12 (1981-2010)      CC0 (스크립트 기준, 확인 안 됨)
  - Global Aridity Index v3.1 (연간)          figshare 는 CC BY 4.0, 동봉 Readme 는 비상업 이용
  - RiverATLAS v1.0 (gdb)                     CC BY 4.0, 일부 열 ODbL
  - OCTOPUS v2 10Be 유역 침식률 (WFS)          CC BY-NC-SA 4.0 으로 취급 (라이선스 미해결)
  - Copernicus GLO-90 타일 (파일럿 유역만)     Copernicus DEM 라이선스 (출처 문구 의무)

원자료는 저장소에 올리지 않습니다. 이 스크립트, configs/pilot_basins.csv, data/manifest.json 만
올립니다. 표준 라이브러리만 쓰므로 패키지를 설치하기 전에도 돌아갑니다. 내려받을 때는 curl 이
필요합니다 (macOS, Windows 10 이상, 대부분의 Linux 에 들어 있습니다).

실행 (저장소 루트에서):
  uv run python tools/download_pilot.py                    없는 파일만 받습니다
  uv run python tools/download_pilot.py --verify-only      네트워크 없이 크기만 검사합니다
  uv run python tools/download_pilot.py --verify-only --verify-hash    sha256 까지 검사합니다
  uv run python tools/download_pilot.py --reselect         유역을 OCTOPUS 에서 다시 고릅니다

데이터 폴더는 기본 <저장소>/data 이고, 환경 변수 BPCG_DATA 나 --data 로 바꿀 수 있습니다.
data/manifest.json 의 path 는 이 데이터 폴더를 기준으로 한 상대 경로입니다.
"""

import argparse
import csv
import datetime
import hashlib
import json
import math
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "data" / "manifest.json"  # 저장소에 올리는 목록 (BPCG_DATA 와 상관없이 여기)
BASINS_CSV = ROOT / "configs" / "pilot_basins.csv"  # 파일럿 유역의 기준 목록 (저장소에 올림)

ETOPO_URL = (
    "https://www.ngdc.noaa.gov/thredds/fileServer/global/ETOPO2022/60s/60s_surface_elev_netcdf/"
    "ETOPO_2022_v1_60s_N90W180_surface.nc"
)
CHELSA_URL = (
    "https://os.unil.cloud.switch.ch/chelsa02/chelsa/global/bioclim/bio12/1981-2010/"
    "CHELSA_bio12_1981-2010_V.2.1.tif"
)
# 데이터 폴더 기준 경로 -> 내려받을 주소
FILES = {
    "pilot/etopo/ETOPO_2022_v1_60s_N90W180_surface.nc": ETOPO_URL,
    "pilot/chelsa/CHELSA_bio12_1981-2010_V.2.1.tif": CHELSA_URL,
    "pilot/aridity/Global-AI_ET0__annual_v3_1.zip": "https://ndownloader.figshare.com/files/56300327",
    "pilot/riveratlas/RiverATLAS_Data_v10.gdb.zip": "https://ndownloader.figshare.com/files/20087321",
}
# OCTOPUS GeoServer 는 https 접속이 실패해서(2026-10-02 확인) http 를 씁니다.
WFS = "http://geoserver.octopusdata.org/geoserver/wfs"
OCTOPUS_LAYERS = ["be10-denude:crn_int_basins", "be10-denude:crn_aus_basins"]
OCTOPUS_DIR = "pilot/octopus"
BASINS_COPY = "pilot/octopus/pilot_basins.csv"  # configs/pilot_basins.csv 의 사본 (예전 위치)
GLO90 = "https://copernicus-dem-90m.s3.amazonaws.com"
GLO90_DIR = "pilot/glo90"

BASIN_COLUMNS = [
    "OBSID1", "CNTRY", "REGION_INT", "BASIN", "AREA_km2", "EBE_mm_per_kyr", "EBE_ERR",
    "SLP_AVE", "lon_min", "lat_min", "lon_max", "lat_max", "tiles",
]  # fmt: skip

# manifest 항목에 적는 출처·라이선스. data/README.md 의 표와 같은 내용입니다.
DATASETS = {
    "etopo": {
        "license": (
            "퍼블릭 도메인 (NOAA NCEI, 예전 스크립트에는 CC0). 인용: NOAA NCEI 2022, "
            "ETOPO 2022 Global Relief Model, doi:10.25921/fd45-gt74"
        ),
        "note": (
            "60초(약 1.8 km) 지표 고도, 육지+해저, m (EGM2008). 빙상은 윗면 고도. "
            "위도가 남->북 순서 (0번 행 = 남쪽)."
        ),
    },
    "chelsa": {
        "license": (
            "CC0 (예전 스크립트 기준, 온라인 확인 안 됨). 인용: Karger et al. 2017 Sci Data "
            "4:170122; Karger et al. 2021 EnviDat doi:10.16904/envidat.228.v2.1"
        ),
        "note": "1981-2010 연강수. 값이 이미 mm/yr (kg m-2 yr-1). 84°N 까지. nodata 65535.",
    },
    "aridity": {
        "license": (
            "figshare 표기는 CC BY 4.0, 그러나 동봉 Readme 는 '비상업 이용' 이라고 적음 "
            "(두 표기가 다름). 인용: Zomer, Xu, Trabucco 2022 Sci Data 9:409"
        ),
        "note": (
            "Global Aridity Index v3.1 연간 (ai_v31_yr.tif 등, /vsizip/ 으로 바로 읽음). "
            "AI 는 0.0001 을 곱함, 바다 0, 65535 는 매우 습한 칸. 기간 1970-2000."
        ),
    },
    "riveratlas": {
        "license": (
            "CC BY 4.0 (일부 열은 ODbL 1.0, HydroATLAS TechDoc 4.1절). 인용: Linke et al. "
            "2019 Sci Data 6:283"
        ),
        "note": (
            "하천 구간 8,477,883개 (gdb, 풀면 약 7 GB). sgr_dk_rav 는 dm/km, slp_dg_* 는 "
            "도x10. 경사는 EarthEnv-DEM90 에서 계산된 값."
        ),
    },
    "octopus": {
        "license": (
            "미해결: 예전 스크립트는 CC BY-NC-SA 4.0, octopusdata.org 바닥글과 Zenodo 기록은 "
            "CC BY 4.0. 확인 전까지 CC BY-NC-SA 4.0 으로 취급. 인용: Codilean et al. 2022 "
            "ESSD 14:3695-3713, doi:10.5194/essd-14-3695-2022"
        ),
        "note": (
            "10Be 유역 평균 침식률 (EBE_MMKYR, mm/kyr). WFS 에서 500개씩 받아 합친 파일. "
            "결측값 -9999.99 / -99.99, 고쳐야 할 도형이 있음 (make_valid). WFS 내용이 바뀌면 "
            "다시 받았을 때 크기와 해시가 달라질 수 있음."
        ),
    },
    "pilot_basins": {
        "license": "OCTOPUS 에서 골라낸 자료라 OCTOPUS 조건을 따름",
        "note": (
            "configs/pilot_basins.csv 의 사본 (줄바꿈 CRLF). 파일럿 유역 40개와 "
            "GLO-90 타일 목록. 기준은 configs 쪽."
        ),
    },
    "glo90": {
        "license": (
            "Copernicus WorldDEM-90 라이선스 (무료, 출처 문구와 면책 문장 의무, "
            "data/README.md 참고)"
        ),
        "note": (
            "1°x1° 타일, 3초, float32, m (EGM2008). nodata 없음, 바다 = 0, "
            "pixel-is-point (경계가 반 칸 밀림)."
        ),
    },
}


def say(msg=""):
    print(msg, flush=True)


def data_dir():
    """데이터 폴더. 환경 변수 BPCG_DATA 가 있으면 그 폴더, 없으면 <저장소>/data 입니다."""
    env = os.environ.get("BPCG_DATA")
    return Path(env) if env else ROOT / "data"


def part_path(dest):
    """받는 중인 파일의 이름 (<이름>.part). 다 받은 뒤에만 원래 이름으로 바꿉니다."""
    return dest.with_name(dest.name + ".part")


def is_live(rel):
    """WFS 처럼 원본 내용이 바뀔 수 있는 파일인지. 이런 파일은 크기가 달라도 경고만 합니다."""
    return rel.startswith(OCTOPUS_DIR + "/") and rel.endswith(".geojson")


def octopus_path(layer):
    return f"{OCTOPUS_DIR}/{layer.split(':')[1]}.geojson"


def octopus_url(layer, count=None, start=None):
    url = (
        f"{WFS}?service=WFS&version=2.0.0&request=GetFeature&typeNames={layer}"
        f"&outputFormat=application/json&srsName=EPSG:4326&sortBy=OBSID1"
    )
    if count is not None:
        url += f"&count={count}&startIndex={start}"
    return url


def glo90_path(tile):
    return f"{GLO90_DIR}/{tile}.tif"


def glo90_url(tile):
    return f"{GLO90}/{tile}/{tile}.tif"


# --------------------------------------------------------------------------- manifest


def load_manifest(path):
    """data/manifest.json 을 읽습니다. 없으면 빈 목록을 돌려줍니다."""
    if not path.exists():
        say(f"경고: {path.name} 이 없습니다. 크기 검사 없이 진행합니다.")
        return {"version": 1, "files": []}
    with open(path, encoding="utf-8") as fh:
        manifest = json.load(fh)
    manifest.setdefault("files", [])
    return manifest


def save_manifest(path, manifest):
    text = json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
    part = part_path(path)
    part.write_text(text, encoding="utf-8", newline="\n")
    os.replace(part, path)


def manifest_index(manifest):
    return {e["path"]: e for e in manifest["files"]}


def sha256_file(path):
    with open(path, "rb") as fh:
        return hashlib.file_digest(fh, "sha256").hexdigest()


def describe(rel):
    """스크립트가 받는 파일 rel 의 manifest 정보 (group, source_url, license, redownload, note)."""
    parts = rel.split("/")
    url = None
    if rel in FILES:
        meta, url = DATASETS[parts[1]], FILES[rel]
    elif rel == BASINS_COPY:
        meta = DATASETS["pilot_basins"]
    elif is_live(rel):
        layer = next(x for x in OCTOPUS_LAYERS if octopus_path(x) == rel)
        meta, url = DATASETS["octopus"], octopus_url(layer)
    elif rel.startswith(GLO90_DIR + "/") and rel.endswith(".tif"):
        meta, url = DATASETS["glo90"], glo90_url(parts[-1][: -len(".tif")])
    else:
        raise KeyError(f"스크립트가 관리하지 않는 파일입니다: {rel}")
    info = {"group": "pilot"}
    if url:
        info["source_url"] = url
    info |= {"license": meta["license"], "redownload": "script", "note": meta["note"]}
    return info


def manifest_entry(rel, size, sha256):
    return {"path": rel, "bytes": size, "sha256": sha256} | describe(rel)


def update_manifest(manifest, data, rels, fresh, no_tile):
    """rels 중 디스크에 있는 파일의 항목을 새로 씁니다. 손으로 받는 항목은 건드리지 않습니다.

    크기가 manifest 와 같고 이번에 받지 않은 파일은 예전 sha256 을 그대로 씁니다.
    """
    index = manifest_index(manifest)
    for rel in sorted(set(rels)):
        f = data / rel
        if not f.exists():
            continue
        size = f.stat().st_size
        old = index.get(rel)
        if old and old.get("bytes") == size and old.get("sha256") and rel not in fresh:
            digest = old["sha256"]
        else:
            say(f"  sha256 계산: {rel}")
            digest = sha256_file(f)
        index[rel] = manifest_entry(rel, size, digest)
    manifest["version"] = 1
    manifest["generated"] = datetime.date.today().isoformat()
    manifest["files"] = sorted(index.values(), key=lambda e: e["path"])
    manifest["glo90_no_tile"] = sorted(set(manifest.get("glo90_no_tile", [])) | set(no_tile))


# --------------------------------------------------------------------------- 검사


def verify(manifest, data, basins_csv, check_hash=False):
    """manifest 와 디스크를 비교합니다 (네트워크 없음). 문제가 있으면 1, 없으면 0 을 돌려줍니다.

    손으로 받는 자료(redownload = manual)가 없는 것은 문제로 치지 않습니다.
    WFS 에서 받은 OCTOPUS 파일은 원본이 바뀔 수 있어 크기·해시가 달라도 경고만 합니다.
    """
    say(f"데이터 폴더: {data}")
    ok, missing, optional, bad, warn = [], [], [], [], []
    for e in manifest["files"]:
        rel, f = e["path"], data / e["path"]
        if not f.exists():
            (optional if e.get("redownload") == "manual" else missing).append(rel)
            continue
        size = f.stat().st_size
        if size != e.get("bytes"):
            msg = f"{rel}: 크기 {size:,} B, manifest {e.get('bytes')} B"
            (warn if is_live(rel) else bad).append(msg)
            continue
        if check_hash and e.get("sha256"):
            if size >= 100_000_000:
                say(f"  sha256 계산: {rel} ({size / 1e6:,.0f} MB)")
            if sha256_file(f) != e["sha256"]:
                (warn if is_live(rel) else bad).append(f"{rel}: sha256 이 manifest 와 다릅니다")
                continue
        ok.append(rel)

    known = set(manifest_index(manifest)) | {
        glo90_path(t) for t in manifest.get("glo90_no_tile", [])
    }
    if basins_csv.exists():
        for t in tiles_from_rows(read_basins(basins_csv)):
            if glo90_path(t) not in known:
                bad.append(f"{glo90_path(t)}: {basins_csv.name} 에 있지만 manifest 에 없습니다")

    pilot = data / "pilot"
    listed = set(manifest_index(manifest))
    extra, parts = [], []
    if pilot.is_dir():
        for f in sorted(pilot.rglob("*")):
            if not f.is_file() or f.name == ".DS_Store":
                continue
            rel = f.relative_to(data).as_posix()
            if f.name.endswith(".part"):
                parts.append(rel)
            elif rel not in listed:
                extra.append(rel)

    what = "크기와 sha256" if check_hash else "크기"
    say(f"맞음 ({what}): {len(ok)}개")
    for title, items in [
        ("없음 (다시 받으세요)", missing),
        ("다름", bad),
        ("경고 (WFS 원본이 바뀌었을 수 있음)", warn),
        ("없음 (손으로 받는 선택 자료, 문제 아님)", optional),
        ("받다 만 파일 (.part, 다시 실행하면 이어받음)", parts),
        ("manifest 에 없는 파일 (참고)", extra),
    ]:
        if items:
            say(f"{title}: {len(items)}개")
            for x in items:
                say(f"  {x}")
    if missing or bad:
        say("검사 실패. 'uv run python tools/download_pilot.py' 로 없는 파일을 받을 수 있습니다.")
        return 1
    say("검사 통과.")
    return 0


# --------------------------------------------------------------------------- 내려받기


def require_curl():
    if shutil.which("curl") is None:
        sys.exit(
            "curl 을 찾을 수 없습니다. macOS 와 Windows 10 이상에는 기본으로 들어 있습니다. "
            "Linux 에서는 패키지 관리자로 curl 을 설치한 뒤 다시 실행하세요."
        )


def run_curl(url, out, resume=False, retry_all=True):
    """curl 로 url 을 out 에 받습니다. (HTTP 코드, curl 종료 코드, 오류 메시지)를 돌려줍니다.

    30초 안에 연결되지 않거나 120초 동안 10 kB/s 보다 느리면 끊고 다시 시도합니다.
    retry_all 이면 모든 오류를 다시 시도합니다. 단 그러면 HTTP 404 도 다섯 번 다시 묻게 되므로,
    바다라서 타일이 없을 수 있는 GLO-90 에는 쓰지 않습니다.
    """
    tty = sys.stderr.isatty()
    cmd = [
        "curl", "--location", "--fail", "--connect-timeout", "30",
        "--speed-limit", "10000", "--speed-time", "120", "--retry", "5",
        "--output", str(out), "--write-out", "%{http_code}",
    ]  # fmt: skip
    if retry_all:
        cmd.append("--retry-all-errors")
    if resume:
        cmd += ["--continue-at", "-"]
    cmd += ["--progress-bar"] if tty else ["--silent", "--show-error"]
    cmd.append(url)
    r = subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=None if tty else subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    code = (r.stdout or "").strip()[-3:]
    return (int(code) if code.isdigit() else 0), r.returncode, (r.stderr or "").strip()


def download(url, dest, expected=None, allow_404=False, retry_all=True, accept_new=False):
    """url 을 <dest>.part 로 받고, 다 받으면 dest 로 이름을 바꿉니다. (상태, 설명)을 돌려줍니다.

    상태는 "got"(받음), "no_tile"(서버에 없음, allow_404 일 때 HTTP 404), "error" 중 하나입니다.
    .part 가 이미 있으면 이어받습니다.
    """
    dest.parent.mkdir(parents=True, exist_ok=True)
    part = part_path(dest)
    resume = part.exists()
    code, rc, err = run_curl(url, part, resume=resume, retry_all=retry_all)
    if rc == 33 and resume:  # 서버가 이어받기를 지원하지 않으면 처음부터 받습니다
        part.unlink()
        code, rc, err = run_curl(url, part, resume=False, retry_all=retry_all)
    if rc != 0:
        if allow_404 and code == 404:
            part.unlink(missing_ok=True)
            return "no_tile", "서버에 타일이 없습니다 (HTTP 404, 바다만 있는 칸)"
        http = f"HTTP {code}" if code else "HTTP 응답 없음"
        return "error", f"내려받기 실패 (curl 종료 코드 {rc}, {http}) {err}".strip()
    size = part.stat().st_size
    if expected is not None and size != expected and not accept_new:
        return "error", (
            f"받은 크기 {size:,} B 가 manifest 의 {expected:,} B 와 다릅니다. "
            f"{part.name} 로 남겨 둡니다. 원본이 바뀐 것이 맞으면 --write-manifest 로 받아들이세요."
        )
    os.replace(part, dest)
    return "got", f"{size / 1e6:,.1f} MB 받음"


def ensure_file(
    url, dest, entry=None, check_hash=False, allow_404=False, retry_all=True, accept_new=False
):
    """dest 가 manifest 와 맞으면 건너뛰고, 아니면 받습니다. (상태, 설명)을 돌려줍니다.

    상태는 "ok"(이미 있음)와 download() 의 상태 중 하나입니다.
    """
    expected = entry.get("bytes") if entry else None
    if dest.exists():
        size = dest.stat().st_size
        if expected is None:
            return "ok", "이미 있음 (manifest 에 크기가 없어 검사하지 않음)"
        if size == expected:
            if check_hash and entry.get("sha256") and sha256_file(dest) != entry["sha256"]:
                return "error", "크기는 맞지만 sha256 이 다릅니다. 파일을 지우고 다시 받으세요."
            return "ok", "이미 있음"
        if size > expected or part_path(dest).exists():
            return "error", (
                f"크기 {size:,} B 가 manifest 의 {expected:,} B 와 다릅니다. 원본이 바뀌었는지 "
                "확인하고, 다시 받으려면 이 파일을 지운 뒤 실행하세요."
            )
        # 예전 스크립트는 최종 이름으로 바로 받았습니다. 작으면 끊긴 파일로 보고 이어받습니다.
        os.replace(dest, part_path(dest))
    return download(url, dest, expected, allow_404, retry_all, accept_new)


def fetch_text(url):
    """작은 응답(WFS 한 쪽)을 받아 bytes 로 돌려줍니다."""
    r = subprocess.run(
        ["curl", "--silent", "--show-error", "--fail", "--location", "--connect-timeout", "30",
         "--max-time", "600", "--retry", "5", url],
        capture_output=True,
    )  # fmt: skip
    if r.returncode != 0:
        err = r.stderr.decode("utf-8", "replace").strip()
        raise RuntimeError(f"WFS 요청 실패 (curl 종료 코드 {r.returncode}) {err}")
    return r.stdout


def dedupe_by_id(feats):
    """OBSID1 이 같은 유역은 처음 것만 남깁니다 (WFS 를 쪽으로 나눠 받을 때 겹칠 수 있음)."""
    seen, out = set(), []
    for f in feats:
        key = (f.get("properties") or {}).get("OBSID1")
        if key is not None:
            if key in seen:
                continue
            seen.add(key)
        out.append(f)
    return out


def fetch_octopus(layer, dest, page=500):
    """WFS 를 page 개씩 OBSID1 순서로 나눠 받아 GeoJSON 하나로 합칩니다. 유역 목록을 돌려줍니다."""
    feats, start = [], 0
    while True:
        body = fetch_text(octopus_url(layer, page, start))
        try:
            got = json.loads(body)["features"]
        except (ValueError, KeyError) as e:
            head = body[:200].decode("utf-8", "replace")
            raise RuntimeError(f"WFS 가 GeoJSON 이 아닌 응답을 보냈습니다: {head}") from e
        feats += got
        say(f"  {layer}: {len(feats)}")
        if len(got) < page:
            break
        start += page
    feats = dedupe_by_id(feats)
    dest.parent.mkdir(parents=True, exist_ok=True)
    part = part_path(dest)
    part.write_text(json.dumps({"type": "FeatureCollection", "features": feats}), encoding="utf-8")
    os.replace(part, dest)
    return feats


def load_octopus(dest):
    with open(dest, encoding="utf-8") as fh:
        return json.load(fh)["features"]


# --------------------------------------------------------------------------- 유역과 타일


def _lon_lat(geom):
    """도형의 모든 꼭짓점 (경도, 위도)."""
    out = []

    def walk(c):
        if isinstance(c[0], (int, float)):
            out.append((c[0], c[1]))
        else:
            for k in c:
                walk(k)

    walk(geom["coordinates"])
    return out


def bbox(geom):
    """도형(Polygon, MultiPolygon 등)을 감싸는 (경도 최소, 위도 최소, 경도 최대, 위도 최대)."""
    pts = _lon_lat(geom)
    xs, ys = [p[0] for p in pts], [p[1] for p in pts]
    return min(xs), min(ys), max(xs), max(ys)


def bbox_split(geom):
    """도형을 감싸는 경위도 상자 목록. 날짜 변경선(경도 ±180°)을 넘으면 두 상자로 나눕니다.

    경도 폭이 180° 를 넘으면 날짜 변경선을 넘은 것으로 봅니다. 유역(1000 km² 이하)은 실제로
    그렇게 넓을 수 없기 때문입니다.
    """
    x0, y0, x1, y1 = bbox(geom)
    if x1 - x0 <= 180:
        return [(x0, y0, x1, y1)]
    xs = [x + 360 if x < 0 else x for x, _ in _lon_lat(geom)]
    e0, e1 = min(xs), max(xs)
    if e1 <= 180:
        return [(e0, y0, e1, y1)]
    return [(e0, y0, 180.0, y1), (-180.0, y0, e1 - 360, y1)]


def tile_name(lat, lon):
    """남서쪽 모서리가 (lat, lon) 인 GLO-90 타일 이름 (정수 도)."""
    ns = f"N{lat:02d}" if lat >= 0 else f"S{-lat:02d}"
    ew = f"E{lon:03d}" if lon >= 0 else f"W{-lon:03d}"
    return f"Copernicus_DSM_COG_30_{ns}_00_{ew}_00_DEM"


def tiles_for_boxes(boxes):
    """상자들이 걸치는 GLO-90 타일 이름 (위도, 경도 오름차순, 중복 없음)."""
    names = []
    for x0, y0, x1, y1 in boxes:
        for la in range(max(math.floor(y0), -90), min(math.floor(y1), 89) + 1):
            for lo in range(max(math.floor(x0), -180), min(math.floor(x1), 179) + 1):
                t = tile_name(la, lo)
                if t not in names:
                    names.append(t)
    return names


def select_basins(feats, n, bins=5):
    """빙하 없는 10~1000 km² 유역을 침식률 구간 x 지역으로 고르게 n 개 뽑습니다 (결정적).

    침식률 순서로 bins 개 구간으로 나누고, 구간마다 OBSID1 의 해시 순서로 훑으며 아직 안 나온
    지역(REGION_INT)만 고릅니다. 지역 이름의 알파벳 순서로 고르면 A·B 로 시작하는 지역만
    뽑히므로 해시로 섞습니다. 한 지역은 전체에서 한 번만 나옵니다.
    """
    ok = []
    for f in feats:
        p = f.get("properties") or {}
        e, a, g = p.get("EBE_MMKYR"), p.get("AREA"), p.get("GLA_PCNT")
        if e is None or a is None or p.get("OBSID1") is None or f.get("geometry") is None:
            continue
        if (g or 0) > 0 or not (10 <= a <= 1000) or e <= 0:
            continue
        ok.append(f)
    ok.sort(key=lambda f: (f["properties"]["EBE_MMKYR"], f["properties"]["OBSID1"]))

    def shuffle(f):
        return hashlib.md5(f["properties"]["OBSID1"].encode("utf-8")).hexdigest()

    per_bin = math.ceil(n / bins)
    chosen, seen = [], set()
    for b in range(bins):
        part = sorted(ok[len(ok) * b // bins : len(ok) * (b + 1) // bins], key=shuffle)
        got = 0
        for f in part:
            reg = f["properties"].get("REGION_INT")
            if reg in seen:
                continue
            seen.add(reg)
            chosen.append(f)
            got += 1
            if got >= per_bin:
                break
    return chosen[:n]


def basin_row(f):
    """유역 하나를 pilot_basins.csv 의 한 줄(dict)로 만듭니다.

    날짜 변경선을 넘는 유역은 lon_min > lon_max 로 적습니다 (GeoJSON 과 같은 규칙).
    """
    p = f["properties"]
    boxes = bbox_split(f["geometry"])
    x0, y0, x1, y1 = boxes[0][0], boxes[0][1], boxes[-1][2], boxes[-1][3]
    values = [
        p["OBSID1"], p.get("CNTRY"), p.get("REGION_INT"), p.get("BASIN"), p.get("AREA"),
        p.get("EBE_MMKYR"), p.get("EBE_ERR"), p.get("SLP_AVE"),
        round(x0, 4), round(y0, 4), round(x1, 4), round(y1, 4),
        ";".join(tiles_for_boxes(boxes)),
    ]  # fmt: skip
    return dict(zip(BASIN_COLUMNS, values, strict=True))


def read_basins(path):
    """pilot_basins.csv 를 dict 목록으로 읽습니다 (값은 모두 문자열)."""
    with open(path, encoding="utf-8", newline="") as fh:
        return list(csv.DictReader(fh))


def write_basins(path, rows, lineterminator="\n"):
    """pilot_basins.csv 를 씁니다. configs 쪽은 LF, data 쪽 사본은 예전과 같은 CRLF 입니다."""
    path.parent.mkdir(parents=True, exist_ok=True)
    part = part_path(path)
    with open(part, "w", encoding="utf-8", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=BASIN_COLUMNS, lineterminator=lineterminator)
        w.writeheader()
        w.writerows(rows)
    os.replace(part, path)


def tiles_from_rows(rows):
    """pilot_basins.csv 의 tiles 열에 나온 타일 이름 (정렬, 중복 없음)."""
    return sorted({t for r in rows for t in (r.get("tiles") or "").split(";") if t})


def sync_basins_copy(rows, dest):
    """data 쪽 사본이 없거나 내용이 다르면 configs 의 목록으로 다시 씁니다. 썼으면 True."""
    if dest.exists() and read_basins(dest) == rows:
        return False
    write_basins(dest, rows, lineterminator="\r\n")
    return True


# --------------------------------------------------------------------------- 실행


def run(args, data, manifest):
    """내려받기 모드. 문제가 있으면 1 을 돌려줍니다."""
    index = manifest_index(manifest)
    problems, fresh, no_tile = [], set(), set()
    say(f"데이터 폴더: {data}")

    def handle(rel, status, msg):
        if status == "got":
            fresh.add(rel)
        elif status == "no_tile":
            no_tile.add(rel.rsplit("/", 1)[-1][: -len(".tif")])
        elif status == "error":
            problems.append(f"{rel}: {msg}")
        say(f"  {msg}")

    for rel, url in FILES.items():
        say(f"[파일] {rel}")
        status, msg = ensure_file(
            url, data / rel, index.get(rel), args.verify_hash, accept_new=args.write_manifest
        )
        handle(rel, status, msg)

    say("[OCTOPUS] 유역")
    feats, octopus_failed = [], False
    for layer in OCTOPUS_LAYERS:
        rel = octopus_path(layer)
        dest, entry = data / rel, index.get(rel)
        try:
            if dest.exists():
                say(f"  {rel}: 이미 있음")
                if entry and dest.stat().st_size != entry.get("bytes"):
                    say("  경고: 크기가 manifest 와 다릅니다 (WFS 원본이 바뀌었을 수 있음)")
                if args.reselect:
                    feats += load_octopus(dest)
            else:
                feats += fetch_octopus(layer, dest)
                fresh.add(rel)
        except (RuntimeError, OSError, ValueError) as e:
            octopus_failed = True
            problems.append(f"{rel}: {e}")
            say(f"  {e}")

    if args.reselect:
        if octopus_failed:
            say("OCTOPUS 유역을 모두 읽지 못해 다시 고를 수 없습니다.")
            return finish(problems, no_tile)
        feats = dedupe_by_id(feats)
        chosen = select_basins(feats, args.n_basins)
        rows = [basin_row(f) for f in chosen]
        write_basins(BASINS_CSV, rows)
        rows = read_basins(BASINS_CSV)
        say(f"  유역 {len(feats)}개 중 {len(rows)}개를 골라 configs/{BASINS_CSV.name} 에 썼습니다")
        say("  저장소에 올리기 전에 --write-manifest 로 manifest 도 새로 쓰세요.")
    else:
        rows = read_basins(BASINS_CSV)
        say(f"  파일럿 유역 {len(rows)}개 (configs/{BASINS_CSV.name})")
    if sync_basins_copy(rows, data / BASINS_COPY):
        fresh.add(BASINS_COPY)
        say(f"  {BASINS_COPY} 를 configs 의 목록으로 새로 썼습니다")

    tiles = tiles_from_rows(rows)
    known_absent = set(manifest.get("glo90_no_tile", []))
    say(f"[GLO-90] 유역 {len(rows)}개 -> 타일 {len(tiles)}개")
    counts = {"ok": 0, "got": 0, "no_tile": 0, "error": 0}
    for t in tiles:
        rel = glo90_path(t)
        if t in known_absent:
            counts["no_tile"] += 1
            continue
        status, msg = ensure_file(
            glo90_url(t),
            data / rel,
            index.get(rel),
            args.verify_hash,
            allow_404=True,
            retry_all=False,
            accept_new=args.write_manifest,
        )
        counts[status] += 1
        if status != "ok":
            say(f"  {t}")
            handle(rel, status, msg)
    present = [data / glo90_path(t) for t in tiles if (data / glo90_path(t)).exists()]
    total = sum(f.stat().st_size for f in present)
    say(
        f"  이미 있음 {counts['ok']}, 새로 받음 {counts['got']}, 바다라 없음 {counts['no_tile']}, "
        f"실패 {counts['error']} (합계 {total / 1e6:,.1f} MB)"
    )

    if args.write_manifest:
        say("[manifest] 새로 씁니다")
        rels = list(FILES) + [octopus_path(x) for x in OCTOPUS_LAYERS] + [BASINS_COPY]
        rels += [glo90_path(t) for t in tiles]
        rels += [e["path"] for e in manifest["files"] if e.get("redownload") == "script"]
        update_manifest(manifest, data, rels, fresh, no_tile)
        save_manifest(MANIFEST, manifest)
        shown = MANIFEST.relative_to(ROOT).as_posix() if MANIFEST.is_relative_to(ROOT) else MANIFEST
        say(f"  {shown} ({len(manifest['files'])}개 항목)")
    return finish(problems, no_tile, written=args.write_manifest)


def finish(problems, no_tile, written=False):
    if no_tile:
        say(f"바다만 있어 타일이 없는 칸 {len(no_tile)}개: {', '.join(sorted(no_tile))}")
        if not written:
            say("  manifest 에 기록하려면 --write-manifest 를 붙여 다시 실행하세요.")
    if problems:
        say(f"실패 {len(problems)}건:")
        for p in problems:
            say(f"  {p}")
        say("문제를 고친 뒤 다시 실행하면 받은 파일은 건너뛰고 나머지만 받습니다.")
        return 1
    say("완료.")
    return 0


def parse_args(argv=None):
    ap = argparse.ArgumentParser(
        description="파일럿 데이터를 내려받거나 data/manifest.json 과 비교해 검사합니다.",
    )
    ap.add_argument(
        "--data",
        metavar="DIR",
        help="데이터 폴더 (기본: 환경 변수 BPCG_DATA, 없으면 <저장소>/data)",
    )
    ap.add_argument(
        "--verify-only",
        action="store_true",
        help="내려받지 않고 manifest 와 크기만 비교합니다 (네트워크 없음). 문제가 있으면 1 로 "
        "끝납니다.",
    )
    ap.add_argument(
        "--verify-hash",
        action="store_true",
        help="크기와 함께 sha256 도 비교합니다 (디스크에 따라 몇 초에서 몇 분 걸립니다).",
    )
    ap.add_argument(
        "--reselect",
        action="store_true",
        help="configs/pilot_basins.csv 를 쓰지 않고 OCTOPUS 유역에서 다시 고릅니다.",
    )
    ap.add_argument(
        "--n-basins",
        type=int,
        default=40,
        metavar="N",
        help="--reselect 로 고를 유역 수 (기본 40)",
    )
    ap.add_argument(
        "--write-manifest",
        action="store_true",
        help="받은 파일의 크기와 sha256 으로 data/manifest.json 을 새로 씁니다 (관리자용).",
    )
    args = ap.parse_args(argv)
    if args.verify_only and (args.reselect or args.write_manifest):
        ap.error("--verify-only 는 --reselect, --write-manifest 와 함께 쓸 수 없습니다.")
    return args


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    args = parse_args(argv)
    data = Path(args.data) if args.data else data_dir()
    manifest = load_manifest(MANIFEST)
    if args.verify_only:
        return verify(manifest, data, BASINS_CSV, check_hash=args.verify_hash)
    require_curl()
    try:
        return run(args, data, manifest)
    except KeyboardInterrupt:
        say("\n중단했습니다. 다시 실행하면 받던 파일(.part)을 이어받습니다.")
        return 130


if __name__ == "__main__":
    sys.exit(main())
