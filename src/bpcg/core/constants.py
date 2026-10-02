"""물리 상수와 행성 상수 계산 (docs/pipeline.md 3장).

행성마다 바뀌는 값(반지름, 밀도 등)은 configs/planets/<행성>.toml 에 있고,
여기에는 행성과 무관한 자연 상수와 단위 환산만 둡니다.
"""

import math

G_GRAV = 6.674e-11  # 만유인력 상수 [m³/(kg·s²)]
SECONDS_PER_YEAR = 3.15576e7  # 율리우스년 1년 [s/yr]
RHO_WATER = 1000.0  # 민물 밀도 [kg/m³] (수리전도도 K = k·ρ_w·g/μ 에 씀)
MU_WATER = 1e-3  # 물의 점성 [Pa·s] (20 °C 근처)


def gravity(radius_m: float, density_kg_m3: float) -> float:
    """균질한 구의 표면 중력 g = 4/3·π·G·ρ·R [m/s²].

    radius_m: 행성 반지름 [m]. density_kg_m3: 평균 밀도 [kg/m³].
    지구 설정(R = 6.371e6 m, ρ = 5514 kg/m³)에서 약 9.82 m/s² 입니다.
    """
    radius_m = float(radius_m)
    density_kg_m3 = float(density_kg_m3)
    if not (math.isfinite(radius_m) and radius_m > 0.0):
        raise ValueError(f"반지름은 0 보다 큰 유한한 값이어야 합니다: {radius_m}")
    if not (math.isfinite(density_kg_m3) and density_kg_m3 > 0.0):
        raise ValueError(f"밀도는 0 보다 큰 유한한 값이어야 합니다: {density_kg_m3}")
    return 4.0 / 3.0 * math.pi * G_GRAV * density_kg_m3 * radius_m
