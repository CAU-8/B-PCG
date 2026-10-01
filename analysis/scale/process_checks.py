"""지형 과정 규칙의 빠른 크기 점검 7가지 (설계 결정의 숫자 근거).

1) Q* 에서 나오는 사면 길이, 2) Roering 2007 R*(E*) 사면 기복, 3) Heimsath 정상상태 토양 두께,
4) Ohmura 식 평형선 고도(ELA), 5) 선상지/유역 경사비, 6) 침강 분지가 있는 1D 산지 전면 종단면,
7) 홀로세 삼각주 면적. 단위: m, yr, m/yr, m^3/yr (강수 P = 1 m/yr 이면 Q = A).

입력: 없음
출력: 화면 출력만
실행: uv run python analysis/scale/process_checks.py
"""

import numpy as np


def r_star(E):
    """Roering 2007 의 무차원 기복 R*(E*)."""
    s = np.sqrt(1 + E**2)
    return (s - np.log(0.5 * (1 + s)) - 1) / E


def t_ela(P_mm):
    """Ohmura 식: 연강수 P [mm] 에서 평형선의 여름 기온 [°C]."""
    a, b, c = 9, 296, 645 - P_mm
    return (-b + np.sqrt(b * b - 4 * a * c)) / (2 * a)


def main():
    # 1) 기본 설계의 Q* 가 뜻하는 사면 길이
    theta = 0.45
    Sc = 0.6
    for ks in (60, 250):
        Qs = (ks / Sc) ** (1 / theta)
        print(f"Q*={Qs:.3g} m3/yr -> A*(P=1m/yr)={Qs:.3g} m2 -> L_H~sqrt={np.sqrt(Qs):.0f} m")
    # 2) Roering 2007 R*(E*)
    for U in (1e-5, 1e-4, 1e-3):  # [m/yr]
        D = 0.005
        S_c = 1.25
        LH = 150
        rr = 2.0
        E = 2 * U * LH * rr / (D * S_c)
        print(
            f"U={U:.0e} m/yr E*={E:.2f} R*={r_star(E):.3f} hill relief={r_star(E) * S_c * LH:.1f} m"
        )
    # 3) Heimsath 정상상태 토양 두께
    P0 = 2e-4
    k = 3.0
    for U in (1e-6, 1e-5, 5e-5, 1e-4, 2e-4, 1e-3):
        h = max(0, np.log(P0 / U) / k)
        print(f"U={U:.0e} h*={h:.2f} m")
    # 4) 해수면 여름 기온 단순 분포로 본 Ohmura ELA
    for lat, P, Tsl in ((0, 2000, 26), (25, 150, 30), (45, 1000, 20), (60, 1200, 12), (75, 300, 3)):
        z = (Tsl - t_ela(P)) / 0.0065
        print(
            f"lat {lat} P={P} T_ELA={t_ela(P):.2f} -> ELA={z:.0f} m (glacial-mean ~ -900 m: {z - 900:.0f})"
        )
    # 5) 퇴적 정상상태: n=1 일 때 선상지/유역 경사비
    for G in (0.2, 0.5, 1, 2):
        print(
            f"G={G:.1f}  S_fan/S_catch={G / (1 + G):.2f}  steepness factor (1+G)^(1/n)={1 + G:.2f}"
        )
    # 6) 침강 분지가 있는 1D 산지 전면 종단면
    n = 1
    m = 0.45
    K = 2e-5  # [m^(1-2m)/yr], Q = PA (P = 1)
    L = 200e3
    dx = 500
    x = np.arange(0, L + dx, dx)  # 출구(0)에서 분수령(L)까지
    dist = L - x
    A = 0.5 * dist**1.8 + dx * dx  # Hack 법칙
    xf = 120e3  # 산지 전면: x > xf 가 산지
    U = np.where(x > xf, 1e-3, -1e-4)  # 융기 1 mm/yr, 분지 침강 0.1 mm/yr
    # 퇴적물 플럭스 Qs = 상류 쪽 U*dA 의 합 (칸별 면적 증분 사용)
    dAi = np.empty_like(A)
    dAi[:-1] = A[:-1] - A[1:]
    dAi[-1] = A[-1]
    Qs = np.cumsum((U * dAi)[::-1])[::-1]
    for G in (0.0, 1.0):
        rhs = U + G * Qs / A
        ok = rhs > 0
        S = np.where(ok, (np.clip(rhs, 1e-12, None) / K) ** (1 / n) * A ** (-m / n), np.nan)
        z = np.concatenate([[0], np.cumsum(S[1:] * dx)])
        i = np.searchsorted(x, xf)
        print(
            f"G={G:.1f} starved cells={(~ok).sum()}  z(front)={z[i]:.0f} m z(divide)={z[-1]:.0f} m  "
            f"S just below front={S[i - 1]:.4f} S just above={S[i + 1]:.4f} Qs_out={Qs[0]:.3g}"
        )
    # 7) 홀로세 삼각주 면적
    Qs = 1.5e8  # [m^3/yr]
    T = 7000  # [yr]
    h = 50  # [m]
    print(f"delta area ~ {Qs * T / h / 1e6:.0f} km2")


if __name__ == "__main__":
    main()
