"""히어로 격자 크기 검사 (생성기는 C# 의 src/Bpcg/Hero/Domain.cs 의 HeroGridSize).

스튜디오는 실행 전에 profile.hero.size_m·spacing_m 을 검사하고 단계 시간을 어림하려고 씁니다.
"""

import math

MAX_HERO_CELLS = 5_000 * 5_000  # 히어로 칸 수 상한 (Domain.cs 의 MaxHeroCells 와 같음)


def hero_grid_size(cfg) -> tuple[int, float]:
    """(한 변 칸 수 n, 간격 [m]). n = round(size_m / spacing_m).

    size_m 이 spacing_m 의 정수배가 아니면 가장 가까운 정수배로 맞춥니다 (실제 한 변 = n·간격).
    n² 가 MAX_HERO_CELLS 를 넘으면 메모리·시간이 감당되지 않으므로 ValueError 입니다.
    """
    hero = cfg.profile.hero
    size = float(hero.size_m)
    dx = float(hero.spacing_m)
    if not (math.isfinite(size) and math.isfinite(dx) and dx > 0 and size >= 3 * dx):
        raise ValueError(
            f"profile.hero 는 spacing_m > 0, size_m ≥ 3·spacing_m 이어야 합니다: {size}, {dx}"
        )
    n = int(round(size / dx))
    if n * n > MAX_HERO_CELLS:
        side = math.isqrt(MAX_HERO_CELLS)
        raise ValueError(
            f"히어로 {n}² = {n * n:,} 칸은 상한 {side}² 를 넘습니다. profile.hero.size_m 을 "
            f"{side * dx:.0f} m 이하로 줄이거나 spacing_m 을 {size / side:.1f} m 이상으로 늘리세요"
        )
    return n, dx
