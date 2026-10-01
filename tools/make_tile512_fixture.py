"""AfriSAR 30 m DEM 에서 빈칸 없는 512 x 512 조각을 잘라 테스트 고정 자료를 만듭니다.

가장 큰 '빈칸(NaN) 없는 정사각형'의 가운데에서 자릅니다.
입력: data/derived/afrisar/<site>_dem_30m.npz (analysis/afrisar/geocode.py 가 만듦;
     키 z (float32, 띠 밖 NaN), x0, y0, dx [m], lat0, lon0 [도])
출력: tests/fixtures/<site>_tile512_30m.npz
     키 z [m], dx [m], x0, y0 [m] (조각 왼쪽 아래 칸), lat0, lon0, src (원본 파일 이름), row0, col0
실행: uv run python tools/make_tile512_fixture.py lope rabi [--size 512] [--out-dir tests/fixtures]
"""

import argparse
from pathlib import Path

import numpy as np

from bpcg.core.paths import DERIVED, ROOT

IN_DIR = DERIVED / "afrisar"
OUT_DIR = ROOT / "tests" / "fixtures"


def largest_valid_square(valid):
    """True 로만 된 가장 큰 정사각형의 (한 변, 맨 위 행, 맨 왼쪽 열). 동적 계획법."""
    ny, nx = valid.shape
    best, bi, bj = 0, 0, 0
    prev = np.zeros(nx + 1, np.int32)
    for i in range(ny):
        cur = np.zeros(nx + 1, np.int32)
        row = valid[i]
        for j in range(nx):
            if row[j]:
                cur[j + 1] = 1 + min(prev[j + 1], cur[j], prev[j])
                if cur[j + 1] > best:
                    best, bi, bj = cur[j + 1], i, j
        prev = cur
    return best, bi - best + 1, bj - best + 1


def make_tile(src: Path, dst: Path, size=512):
    d = np.load(src)
    z = d["z"]
    side, i0, j0 = largest_valid_square(np.isfinite(z))
    if side < size:
        raise SystemExit(
            f"{src.name}: 빈칸 없는 가장 큰 정사각형이 {side} 칸으로 {size} 칸보다 작습니다"
        )
    ci, cj = i0 + side // 2, j0 + side // 2
    a, b = ci - size // 2, cj - size // 2
    t = z[a : a + size, b : b + size].astype(np.float32)
    assert np.isfinite(t).all()
    dx = float(d["dx"])
    dst.parent.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(
        dst,
        z=t,
        dx=dx,
        x0=float(d["x0"]) + b * dx,
        y0=float(d["y0"]) + a * dx,
        lat0=float(d["lat0"]),
        lon0=float(d["lon0"]),
        src=src.name,
        row0=a,
        col0=b,
    )
    print(
        f"{src.name}: largest gap-free square {side} cells ({side * dx / 1e3:.1f} km); "
        f"tile rows {a}:{a + size}, cols {b}:{b + size}; relief {np.ptp(t):.0f} m -> {dst}"
    )


def main():
    ap = argparse.ArgumentParser(description="AfriSAR DEM 에서 테스트용 512 x 512 조각을 자릅니다.")
    ap.add_argument("sites", nargs="*", default=["lope", "rabi"], help="사이트 (기본 lope rabi)")
    ap.add_argument("--size", type=int, default=512, help="조각 한 변의 칸 수")
    ap.add_argument("--out-dir", type=Path, default=OUT_DIR, help="저장 폴더 (기본 tests/fixtures)")
    args = ap.parse_args()
    for site in args.sites:
        src = IN_DIR / f"{site}_dem_30m.npz"
        make_tile(src, args.out_dir / f"{site}_tile{args.size}_30m.npz", args.size)


if __name__ == "__main__":
    main()
