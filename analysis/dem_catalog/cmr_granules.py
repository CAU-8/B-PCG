"""NASA CMR 검색 API 로 NASADEM_HGT, SRTMGL1, ASTGTM 의 그래뉼 수와 용량 합 [MB] 을 셉니다.

로그인 없이 메타데이터만 읽습니다 (실제 내려받기는 Earthdata 계정이 필요). 파일은 받지 않습니다.

실행: uv run python analysis/dem_catalog/cmr_granules.py
"""

import json

from net import get

PRODUCTS = [("NASADEM_HGT", "001"), ("SRTMGL1", "003"), ("ASTGTM", "003")]


def count(short_name, version, page_size=2000):
    """한 제품의 (CMR-Hits, 실제로 센 개수, granule_size 합 [MB])."""
    tot = 0.0
    n = 0
    sa = None
    hits = None
    while True:
        url = (
            "https://cmr.earthdata.nasa.gov/search/granules.json"
            f"?short_name={short_name}&version={version}&page_size={page_size}"
        )
        r = get(url, timeout=120, headers={"CMR-Search-After": sa} if sa else None)
        hits = r.headers.get("CMR-Hits")
        sa = r.headers.get("CMR-Search-After")
        e = json.load(r)["feed"]["entry"]
        if not e:
            break
        n += len(e)
        tot += sum(float(x.get("granule_size", 0) or 0) for x in e)
        if len(e) < page_size:
            break
    return hits, n, tot


if __name__ == "__main__":
    for sn, v in PRODUCTS:
        hits, n, tot = count(sn, v)
        print(sn, v, "hits", hits, "listed", n, "sum granule_size MB", round(tot, 1))
