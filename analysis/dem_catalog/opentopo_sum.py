"""OpenTopography S3(opentopography.s3.sdsc.edu/raster) 접두어별 GeoTIFF 개수와 용량을 셉니다.

AWS판 GLO-30 에 빠진 아르메니아·아제르바이잔 쪽 타일(results/glo30_aws_missing_vs_glo90_tiles.txt)이
OpenTopography판에는 있는지(N40 E045 예시 키)도 함께 보여 줍니다. 파일은 받지 않습니다.

실행: uv run python analysis/dem_catalog/opentopo_sum.py COP30 COP90
"""

import re
import sys
import urllib.parse

from net import get

BASE = "https://opentopography.s3.sdsc.edu/raster?"


def lst(p):
    """접두어 p 로 시작하는 객체의 (키, 크기 [B]) 목록."""
    out = []
    tok = None
    while True:
        q = {"list-type": "2", "prefix": p, "max-keys": "1000"}
        if tok:
            q["continuation-token"] = tok
        x = get(BASE + urllib.parse.urlencode(q), timeout=90).read().decode()
        out += [(k, int(s)) for k, s in re.findall(r"<Key>(.*?)</Key>.*?<Size>(\d+)</Size>", x)]
        m = re.search(r"<NextContinuationToken>(.*?)</NextContinuationToken>", x)
        if not m:
            break
        tok = m.group(1)
    return out


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("사용법: opentopo_sum.py <접두어> [<접두어> ...]  (예: COP30)")
    for p in sys.argv[1:]:
        o = lst(p)
        t = [s for k, s in o if k.lower().endswith(".tif")]
        ex = [k for k, s in o if "N40" in k and "E045" in k][:3]
        print(
            p,
            "objs",
            len(o),
            "tif",
            len(t),
            "tifGB",
            round(sum(t) / 1e9, 1),
            "allGB",
            round(sum(s for k, s in o) / 1e9, 1),
            ex,
        )
