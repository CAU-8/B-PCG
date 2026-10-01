"""큐브스피어 셀 중심을 면별 색으로 찍어 보는 데모 (예전 main.py 의 __main__ 부분).

실행: uv run python analysis/demos/plot_cubesphere.py [--n 16]
"""

import argparse

import matplotlib.pyplot as plt

from bpcg.core.cubesphere import FACE_NAMES, cubesphere_grid


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--n", type=int, default=16, help="면 한 변의 칸 수")
    ap.add_argument("--R", type=float, default=15.0, help="반지름 (그림용)")
    ap.add_argument("--save", default=None, help="창 대신 이 파일로 저장")
    args = ap.parse_args()

    g = cubesphere_grid(n=args.n, R=args.R)
    print(f"생성된 총 셀 개수: {g.n_cells}")
    print(
        f"전체 면적 합: {g.area_full().sum():.6f} (이론값 4πR²: {4 * 3.141592653589793 * args.R**2:.6f})"
    )

    fig = plt.figure(figsize=(8, 8))
    ax = fig.add_subplot(projection="3d")
    ax.set_box_aspect([1, 1, 1])
    colors = ["r", "g", "b", "c", "m", "y"]
    m = args.n * args.n
    for f in range(6):
        p = g.pos[f * m : (f + 1) * m]
        ax.scatter(
            p[:, 0],
            p[:, 1],
            p[:, 2],
            s=20,
            c=colors[f],
            alpha=0.8,
            label=f"Face {f} ({FACE_NAMES[f]})",
        )
    ax.set_title(f"Cubesphere grid (n={args.n})")
    ax.legend(loc="upper right")
    if args.save:
        fig.savefig(args.save, dpi=120)
    else:
        plt.show()


if __name__ == "__main__":
    main()
