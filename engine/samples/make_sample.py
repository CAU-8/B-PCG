"""엔진 검사용 작은 표본 높이맵(sample.bin, sample.json)을 만듭니다.

129 x 129 표본, 간격 10 m (한 변 1280 m) 의 가우스 언덕 몇 개와 얕은 골짜기입니다.
원점을 (-640, 0, -640) 으로 두어 지형 가운데가 Godot 좌표 원점에 옵니다.
난수를 쓰지 않으므로 다시 만들어도 같은 파일이 나옵니다.

실행 (저장소 맨 위에서): uv run --no-sync python engine/samples/make_sample.py
"""

from pathlib import Path

import numpy as np

from bpcg.bake.heightmap import write_heightmap

SIZE = 129
SPACING_M = 10.0
HALF_M = (SIZE - 1) * SPACING_M / 2.0

# (동쪽 x m, 남쪽 z m, 높이 m, 폭 m). 좌표는 지형 가운데 기준입니다.
HILLS = [
    (-250.0, -300.0, 120.0, 180.0),
    (300.0, -150.0, 80.0, 140.0),
    (150.0, 350.0, 60.0, 220.0),
    (-350.0, 300.0, 40.0, 120.0),
]


def make_heights() -> np.ndarray:
    """(행 = 북→남, 열 = 서→동) 순서의 높이 배열 (m) 을 만듭니다."""
    axis = np.arange(SIZE) * SPACING_M - HALF_M
    x = axis[np.newaxis, :]  # 열 → 동쪽 (+X)
    z = axis[:, np.newaxis]  # 행 → 남쪽 (+Z)
    h = np.full((SIZE, SIZE), 20.0)
    for cx, cz, amp, width in HILLS:
        h += amp * np.exp(-((x - cx) ** 2 + (z - cz) ** 2) / (2.0 * width**2))
    # 북동에서 남서로 지나는 얕은 골짜기
    h -= 15.0 * np.exp(-(((x + z) / np.sqrt(2.0)) ** 2) / (2.0 * 60.0**2))
    return h


def main() -> None:
    stem = Path(__file__).resolve().parent / "sample"
    meta = write_heightmap(stem, make_heights(), SPACING_M, origin=(-HALF_M, 0.0, -HALF_M))
    print(
        f"{stem.name}.bin / {stem.name}.json 을 썼습니다: "
        f"{meta['width']} x {meta['height']}, 간격 {meta['spacing_m']} m, "
        f"높이 {meta['min']:.2f} ~ {meta['max']:.2f} m"
    )


if __name__ == "__main__":
    main()
