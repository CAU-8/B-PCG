"""선상지와 아격자 기복 검사 (docs/pipeline.md 7.2, 7.3): C# 결과의 fan 필드·선상지 꼭짓점·기복.

선상지 꼭짓점이 있으면 그 둘레 칸이 fan 이고 물길이 다시 이어지며, 기복은 0 이상·바다에서 0·
평균 지표 z_mean = z + 기복/2 입니다.
"""

import numpy as np
import pytest
from bundles import outlet_of


def test_fans_follow_apexes(cave_hero):
    apexes = cave_hero.meta.get("fan_apexes") or []
    fan = cave_hero["fan"]
    assert len(apexes) > 0, "강 문턱을 낮춘 평면 히어로에는 선상지가 생겨야 합니다"
    assert fan.any()
    # 선상지 칸도 출구로 물이 빠짐 (선상지를 얹은 뒤 물길을 다시 이음)
    rcv = cave_hero["receiver"].astype(np.int64)
    roots = np.flatnonzero(rcv == np.arange(rcv.size))
    assert np.isin(outlet_of(rcv)[fan], roots).all()


@pytest.mark.parametrize("level", ["hero", "flat_hero"])
def test_fan_cells_only_with_apexes(request, level):
    b = request.getfixturevalue(level)
    apexes = b.meta.get("fan_apexes") or []
    assert b["fan"].any() == (len(apexes) > 0)


def test_relief_on_planet(planet):
    relief = planet["relief_m"]
    assert (relief >= 0).all() and (relief[planet["is_ocean"]] == 0).all()
    assert relief[~planet["is_ocean"]].max() > 0
    z_mean = planet["z_m"].astype(np.float64) + 0.5 * relief
    assert np.allclose(planet["z_mean_m"], z_mean, atol=1e-2)
