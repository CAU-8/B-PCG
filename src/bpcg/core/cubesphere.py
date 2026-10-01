"""등각 큐브스피어 격자 (가이드 1장).

규칙(docs/conventions.md 의 '격자'):
- n 은 면 한 변의 칸 수, 전체 칸 수는 n_cells = 6·n².
- 셀 번호 c = f·n² + j·n + i (i 는 u_f 방향, j 는 v_f 방향).
- 셀 중심 a_i = -1 + (2i+1)/n, 면 좌표 (a, b) ∈ [-1, 1].
- 면 기저는 오른손 좌표계 u_f × v_f = n_f.
"""

from dataclasses import dataclass

import numpy as np

# 면 6개의 기저 (u, v, n). 가이드 1장 표와 같습니다.
FACE_NAMES = ("+X", "-X", "+Y", "-Y", "+Z", "-Z")
FACE_U = np.array(
    [[0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1], [1, 0, 0], [-1, 0, 0]], dtype=float
)
FACE_V = np.array([[0, 0, 1], [0, 0, 1], [1, 0, 0], [1, 0, 0], [0, 1, 0], [0, 1, 0]], dtype=float)
FACE_N = np.array(
    [[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1]], dtype=float
)

# 읽기 쉬운 사전 형태 (예전 main.py 와 같은 모양)
FACE_BASIS = {
    f: {"name": FACE_NAMES[f], "uf": FACE_U[f], "vf": FACE_V[f], "nf": FACE_N[f]} for f in range(6)
}


@dataclass(eq=False, frozen=True)
class Grid:
    n: int  # 면 한 변의 칸 수 (면 하나가 n x n 칸)
    R: float  # 반지름 [m]
    pos: np.ndarray  # (6n², 3) 셀 중심 위치 [m], 셀 번호 c = f·n² + j·n + i 순서
    area: np.ndarray  # (n²,) 면 하나분의 셀 면적 [m²]. 셀 c 의 면적은 area[c % n²]

    @property
    def n_cells(self) -> int:
        """전체 칸 수 6·n²."""
        return 6 * self.n * self.n

    def area_full(self) -> np.ndarray:
        """전체 셀(6n²)의 면적. 여섯 면은 기하학적으로 같으므로 면 하나분을 반복합니다."""
        return np.tile(self.area, 6)


def to_sphere(f: int, a: np.ndarray, b: np.ndarray) -> np.ndarray:
    """면 좌표 (a, b) ∈ [-1, 1] 를 등각 사상으로 구면 위 3차원 단위 벡터로 바꿉니다."""
    f = int(f)
    X = np.tan(np.pi * np.asarray(a, dtype=float) / 4.0)
    Y = np.tan(np.pi * np.asarray(b, dtype=float) / 4.0)
    num = FACE_N[f] + X[..., np.newaxis] * FACE_U[f] + Y[..., np.newaxis] * FACE_V[f]
    den = np.sqrt(1.0 + X**2 + Y**2)
    return num / den[..., np.newaxis]


def omega(X: np.ndarray, Y: np.ndarray) -> np.ndarray:
    """셀 면적 계산을 위한 입체각 함수."""
    return np.arctan2(X * Y, np.sqrt(1.0 + X**2 + Y**2))


def face_cell_areas(n: int, R: float) -> np.ndarray:
    """면 하나를 n x n 격자로 나눴을 때 각 셀의 정확한 면적 [m²].

    대원으로 둘러싸인 사각형의 입체각 차이를 씁니다.
    반환 배열은 [j, i] 순서라서 reshape(-1)하면 셀 번호 j·n + i 순서가 됩니다.
    """
    axis = np.linspace(-1.0, 1.0, n + 1)
    # 메시그리드 (행 = j(b), 열 = i(a))
    A_low, B_low = np.meshgrid(axis[:-1], axis[:-1], indexing="xy")
    A_high, B_high = np.meshgrid(axis[1:], axis[1:], indexing="xy")

    X_minus = np.tan(np.pi * A_low / 4.0)
    X_plus = np.tan(np.pi * A_high / 4.0)
    Y_minus = np.tan(np.pi * B_low / 4.0)
    Y_plus = np.tan(np.pi * B_high / 4.0)

    # 포함-배제로 입체각 계산
    om = (
        omega(X_plus, Y_plus)
        - omega(X_minus, Y_plus)
        - omega(X_plus, Y_minus)
        + omega(X_minus, Y_minus)
    )
    return (R**2) * om


def neighbor_distance(p1: np.ndarray, p2: np.ndarray, R: float) -> np.ndarray:
    """아주 가까운 두 점 사이의 대원 거리 [m].

    정밀도 저하를 막기 위해 arccos 대신 atan2 를 씁니다.
    p1, p2 는 (..., 3) 배열도 받으며, 마지막 축 기준으로 쌍마다 계산합니다.
    """
    cross_norm = np.linalg.norm(np.cross(p1, p2), axis=-1)
    dot_prod = np.sum(p1 * p2, axis=-1)
    return R * np.arctan2(cross_norm, dot_prod)


def cubesphere_grid(n: int, R: float = 6_371_000.0) -> Grid:
    """반지름 R [m] 인 전체 큐브스피어 격자를 만듭니다."""
    # 셀 중심 a_i = -1 + (2i+1)/n (면 경계 위의 점이 아님)
    centers = -1.0 + (2.0 * np.arange(n) + 1.0) / n
    # 행 = j(b), 열 = i(a) → reshape(-1)하면 j·n + i 순서
    a, b = np.meshgrid(centers, centers, indexing="xy")

    pos = np.empty((6 * n * n, 3))
    for f in range(6):
        pos[f * n * n : (f + 1) * n * n] = (to_sphere(f, a, b) * R).reshape(-1, 3)

    area = face_cell_areas(n, R).reshape(-1)
    return Grid(n=n, R=R, pos=pos, area=area)
