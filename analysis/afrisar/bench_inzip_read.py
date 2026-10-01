"""AfriSAR zip 을 풀지 않고 h5 를 바로 읽을 때 얼마나 걸리는지 잽니다 (zip 안에서 h5py 로 열기).

결과는 '작은 배열(TerrainHeight)은 zip 안에서 읽어도 되지만 Tomogram 블록은 풀어서 읽는 게 낫다'는
판단(extract.py 로 캐시에 꺼내 쓰기)의 근거입니다.

입력: data/external/afrisar/Polarimetric_height_profile_1577.zip (파일을 꺼내지 않음)
출력: 화면 출력만
실행: uv run python analysis/afrisar/bench_inzip_read.py [data/lope-tomo-capon-hh.h5 ...]
"""

import sys
import time

import h5py
import numpy as np
from extract import NEEDED_MEMBERS, ZIP_TOP, normalize, open_zip


def bench(zf, name):
    member = ZIP_TOP + normalize(name)
    t = time.time()
    with zf.open(member) as f, h5py.File(f, "r") as h:
        T = h["TerrainHeight"][:]
        print(f"{member.split('/')[-1]} TerrainHeight {T.shape} in-zip {time.time() - t:.1f} s")
        t = time.time()
        H = h["Heights"][:]
        i0 = int(np.argmin(abs(H)))
        S = h["Tomogram"][i0]
        print(f"  Tomogram slice h={H[i0]:g} in-zip {S.shape} {time.time() - t:.1f} s")
        t = time.time()
        S2 = h["Tomogram"][:, 1000:1128, :]
        print(f"  Tomogram azimuth block (all heights) in-zip {S2.shape} {time.time() - t:.1f} s")


if __name__ == "__main__":
    names = sys.argv[1:] or [NEEDED_MEMBERS[2]]  # 기본: data/lope-tomo-capon-hh.h5
    with open_zip() as zf:
        for n in names:
            bench(zf, n)
