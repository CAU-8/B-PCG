"""S3 버킷을 위도 접두어(N00..N90, S00..S90)로 나눠 32개 스레드로 동시에 목록을 읽고 DEM 용량을 합칩니다.

설계도 6장 표의 'GLO-90 타일 26,475개, 71.1 GB', 'GLO-30 589.1 GB' 같은 숫자를 이 방식으로 셌습니다.
파일은 받지 않습니다.

실행 예:
    uv run python analysis/dem_catalog/s3_prefix_sum.py copernicus-dem-90m.s3.amazonaws.com Copernicus_DSM_COG_30_
    uv run python analysis/dem_catalog/s3_prefix_sum.py copernicus-dem-30m.s3.amazonaws.com Copernicus_DSM_COG_10_
"""

import concurrent.futures as cf
import re
import statistics
import sys
import urllib.parse

from net import get


def lst(host, p):
    """접두어 p 로 시작하는 객체의 (키, 크기 [B]) 목록."""
    out = []
    tok = None
    while True:
        q = {"list-type": "2", "prefix": p, "max-keys": "1000"}
        if tok:
            q["continuation-token"] = tok
        x = get(f"https://{host}/?" + urllib.parse.urlencode(q), timeout=60).read().decode()
        for k, s in re.findall(r"<Key>(.*?)</Key>.*?<Size>(\d+)</Size>", x):
            out.append((k, int(s)))
        m = re.search(r"<NextContinuationToken>(.*?)</NextContinuationToken>", x)
        if not m:
            break
        tok = m.group(1)
    return out


def main(host, base):
    prefixes = [base + f"{h}{i:02d}" for h in "NS" for i in range(0, 91)]
    allo = []
    with cf.ThreadPoolExecutor(32) as ex:
        for r in ex.map(lambda p: lst(host, p), prefixes):
            allo += r
    dem = [s for k, s in allo if k.endswith("_DEM.tif") and "/AUXFILES/" not in k]
    print(host, "objects", len(allo), "total GB", round(sum(s for k, s in allo) / 1e9, 1))
    print(
        "DEM.tif",
        len(dem),
        "GB",
        round(sum(dem) / 1e9, 1),
        "GiB",
        round(sum(dem) / 2**30, 1),
        "median MB",
        round(statistics.median(dem) / 1e6, 2),
        "max MB",
        round(max(dem) / 1e6, 2),
    )


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(
            "사용법: s3_prefix_sum.py <호스트> <키 접두어>  (예: copernicus-dem-90m.s3.amazonaws.com Copernicus_DSM_COG_30_)"
        )
    main(sys.argv[1], sys.argv[2])
