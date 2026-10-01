"""스케일 사다리 계산: 행성 반지름별 중력·탈출속도·칸 크기·float32 정밀도, 칸 안 기복, 메모리.

설계도 3장(세 스케일, 지구 반지름 6,371 km 를 고른 이유)과 docs/figures/scale_ladder.png 의 숫자 근거입니다.
가정: 지구 밀도 5,514 kg/m^3, 큐브스피어 면당 n 칸(칸 크기 d0 = (π/2)R/n), 강수 P = 1 m/yr,
θ = 0.45, S_crit = 0.6, Montgomery-Dietrich 사면 길이 L = 1.78 A^0.49.

입력: 없음
출력: 화면 출력만 (표)
실행: uv run python analysis/scale/scale_ladder_calcs.py
"""

import math

G = 6.674e-11  # [m^3 kg^-1 s^-2]
RHO_E = 5514.0  # [kg/m^3] 지구 평균 밀도
R_E = 6.371e6  # [m]


def props(R, rho=RHO_E, n=1024):
    """반지름 R [m] 행성의 (g [m/s^2], v_esc [m/s], 칸 크기 [m], float32 ULP [m],
    해들리 폭을 지구와 같게 하는 하루 길이 [h], 폭 배율, g=9.8 이 되는 밀도 [kg/m^3])."""
    g = 4 / 3 * math.pi * G * rho * R
    vesc = math.sqrt(2 * g * R)
    d0 = (math.pi / 2) * R / n
    ulp = 2 ** (math.floor(math.log2(R)) - 23)
    # Held-Hou: 폭 ∝ Rt^(1/2), Rt ∝ ρ/(Ω^2 a) -> 같은 폭을 유지하려면 Ω ∝ a^-1/2
    day = 24 * math.sqrt(R / R_E)
    phi_scale = math.sqrt(R_E / R)
    rho_needed_for_9_8 = 9.8 / (4 / 3 * math.pi * G * R)
    return g, vesc, d0, ulp, day, phi_scale, rho_needed_for_9_8


def subgrid(ks, A, P=1.0, theta=0.45, Scrit=0.6, c=1.78, h=0.49):
    """칸(면적 A [m^2]) 안의 해석적 기복 [m]: (사면, 하도, 사면 길이 [m])."""
    Qs = (ks / Scrit) ** (1 / theta)
    astar = Qs / P
    hill = Scrit * c * astar**h
    k = ks * P ** (-theta) * c * h / (h - theta)
    chan = k * (A ** (h - theta) - astar ** (h - theta)) if A > astar else 0
    return hill, chan, c * astar**h


def main():
    print(
        "R_km g vesc_kms d0_km(n=1024) d0_km(n=512) ulp_m day_h_sameHadley widthScale rho_for_9.8"
    )
    for Rkm in [6371, 3390, 2575, 1737, 1000, 500, 100, 20]:
        R = Rkm * 1e3
        g, v, d0, ulp, day, ps, rn = props(R)
        d05 = props(R, n=512)[2]
        print(
            f"{Rkm:6d} {g:6.2f} {v / 1e3:6.2f} {d0 / 1e3:8.3f} {d05 / 1e3:8.3f} {ulp:8.4f} "
            f"{day:6.1f} {ps:5.2f} {rn:9.0f}"
        )
    # Q* 와 칸 강수
    P = 1.0
    theta = 0.45
    Scrit = 0.6
    for ks in [60, 250]:
        Qs = (ks / Scrit) ** (1 / theta)
        print("ks", ks, f"Q*={Qs:.3g}", f"delta where cell rain = Q*: {math.sqrt(Qs / P):.0f} m")
    # 거친 칸 하나의 계단 높이 vs 해석적 칸 안 기복 (Montgomery-Dietrich L=1.78 A^0.49)
    for d in [9770, 19540, 1530, 610, 38]:
        A = d * d
        for ks in [60, 250]:
            step = d * min(ks * (P * A) ** -theta, 0.6)
            hill, chan, L = subgrid(ks, A)
            print(
                f"d={d}m ks={ks}: coarse headwater step={step:7.1f} m ; "
                f"analytic subgrid relief hill={hill:6.0f} chan={chan:6.0f} total={hill + chan:6.0f} m ; "
                f"hillslope length={L:6.0f} m"
            )
    # 메모리
    for n in [512, 1024]:
        N = 6 * n * n
        print("n", n, "cells", N, "MB per f32 field", N * 4 / 1e6)
    for side_km, dx in [(40, 38), (30, 20), (625, 610), (50, 10)]:
        m = int(side_km * 1000 / dx)
        print(
            f"tile {side_km}km @ {dx}m: {m}^2={m * m / 1e6:.2f}M cells, {m * m * 4 / 1e6:.0f} MB/field"
        )
    n_l2 = (math.pi / 2) * R_E / 38
    print("n at L2 for 38m on R=6371:", n_l2, " 6n^2=", 6 * n_l2**2, "> 2^31?", 6 * n_l2**2 > 2**31)


if __name__ == "__main__":
    main()
