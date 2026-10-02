"""3D 샘플 함수: 위치 하나를 받아 (거리, 재료, 물)을 돌려줍니다. 지층·동굴·물 규칙 (가이드 7장).

미시 스케일(L3 1 m 복셀). 파이썬에만 있고 엔진으로 옮기지 않습니다.

- sample.HeroVolume(hero_state, cfg).sample(points) -> (d float32, material uint8, water bool)
- slices.vertical_slice(volume, p0, p1, z_min, z_max, res_m) -> RGB 그림
"""

from bpcg.volume.sample import MATERIAL_EMPTY, HeroVolume
from bpcg.volume.slices import vertical_slice

__all__ = ["MATERIAL_EMPTY", "HeroVolume", "vertical_slice"]
