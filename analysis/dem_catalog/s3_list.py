"""공개 S3 버킷 목록(메타데이터)만 읽어 DEM 타일 수와 용량을 셉니다. 파일은 받지 않습니다.

ListObjectsV2 를 처음부터 끝까지 한 페이지(1,000개)씩 넘깁니다. 느리지만(GLO-90 약 424쪽) 단순합니다.
빠른 병렬판은 s3_prefix_sum.py 입니다. curl 이 있어야 합니다 (Windows 10 이상은 기본 포함).

결과(2026-10-01): analysis/dem_catalog/results/glo90_summary.txt
실행: uv run python analysis/dem_catalog/s3_list.py copernicus-dem-90m [copernicus-dem-30m]
"""

import subprocess
import sys
import urllib.parse
import xml.etree.ElementTree as ET

NS = {"s": "http://s3.amazonaws.com/doc/2006-03-01/"}


def listing(bucket):
    """버킷 전체를 훑어 *_DEM.tif(AUXFILES 제외)와 나머지 파일의 개수·크기 [B] 를 셉니다."""
    token = None
    pages = 0
    dem = []
    aux = 0
    aux_n = 0
    while True:
        q = {"list-type": "2", "max-keys": "1000"}
        if token:
            q["continuation-token"] = token
        url = f"https://{bucket}.s3.amazonaws.com/?" + urllib.parse.urlencode(q)
        out = subprocess.run(
            ["curl", "-s", "--max-time", "120", "--retry", "3", url],
            capture_output=True,
            check=True,
        ).stdout
        root = ET.fromstring(out)
        for c in root.findall("s:Contents", NS):
            key = c.find("s:Key", NS).text
            size = int(c.find("s:Size", NS).text)
            if key.endswith("_DEM.tif") and "/AUXFILES/" not in key:
                dem.append(size)
            else:
                aux += size
                aux_n += 1
        pages += 1
        if pages % 25 == 0:
            print(f"  {bucket}: {pages} pages, {len(dem):,} DEM tiles so far", flush=True)
        if root.find("s:IsTruncated", NS).text == "true":
            token = root.find("s:NextContinuationToken", NS).text
        else:
            break
    dem.sort()
    print(
        f"{bucket}: DEM tiles {len(dem):,}, DEM total {sum(dem) / 1e9:,.1f} GB "
        f"(median tile {dem[len(dem) // 2] / 1e6:.1f} MB, max {dem[-1] / 1e6:.1f} MB); "
        f"aux files {aux_n:,} = {aux / 1e9:,.1f} GB",
        flush=True,
    )


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("사용법: s3_list.py <버킷> [<버킷> ...]  (예: copernicus-dem-90m)")
    for b in sys.argv[1:]:
        listing(b)
