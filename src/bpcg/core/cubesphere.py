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
    # 포함-배제의 반올림 오차가 n² 에 비례해 쌓여 8겹 대칭이 깨집니다(n=4096 에서 2e-9).
    # 면 경계를 사이에 둔 두 칸의 면적이 정확히 같도록 거울 대칭으로 평균합니다. 합은 그대로입니다.
    om = 0.25 * (om + om[:, ::-1] + om[::-1, :] + om[::-1, ::-1])
    om = 0.5 * (om + om.T)
    return (R**2) * om


def neighbor_distance(p1: np.ndarray, p2: np.ndarray, R: float) -> np.ndarray:
    """아주 가까운 두 점 사이의 대원 거리 [m].

    정밀도 저하를 막기 위해 arccos 대신 atan2 를 씁니다.
    p1, p2 는 (..., 3) 배열도 받으며, 마지막 축 기준으로 쌍마다 계산합니다.
    """
    cross_norm = np.linalg.norm(np.cross(p1, p2), axis=-1)
    dot_prod = np.sum(p1 * p2, axis=-1)
    return R * np.arctan2(cross_norm, dot_prod)


def to_face(p: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """구면 위 점(단위 벡터가 아니어도 됨) → (면 f, a, b). 가이드 1장 역사상."""
    p = np.asarray(p, dtype=float)
    f = np.argmax(p @ FACE_N.T, axis=-1)
    pn = np.einsum("...k,...k->...", p, FACE_N[f])
    a = 4.0 / np.pi * np.arctan(np.einsum("...k,...k->...", p, FACE_U[f]) / pn)
    b = 4.0 / np.pi * np.arctan(np.einsum("...k,...k->...", p, FACE_V[f]) / pn)
    return f, a, b


def cell_of(p: np.ndarray, n: int) -> np.ndarray:
    """구면 위 점 → 그 점을 담은 셀 번호 c = f·n² + j·n + i."""
    f, a, b = to_face(p)
    i = np.clip(np.floor(n * (a + 1.0) / 2.0).astype(np.int64), 0, n - 1)
    j = np.clip(np.floor(n * (b + 1.0) / 2.0).astype(np.int64), 0, n - 1)
    return f * n * n + j * n + i


# 이웃 슬롯 순서 (dj, di). 가이드 1장과 같습니다.
NEIGHBOR_SLOTS = ((-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1))
CARDINAL_SLOTS = (1, 3, 4, 6)

_U_INT = FACE_U.astype(int)
_V_INT = FACE_V.astype(int)
_N_INT = FACE_N.astype(int)


def _face_with_normal(e: np.ndarray) -> int:
    return int(np.nonzero((_N_INT == e).all(axis=1))[0][0])


def _cross_edge(f: int, n: int, e: np.ndarray, t: np.ndarray, k: np.ndarray):
    """면 f 에서 방향 e 로 넘어갈 때, 남은 인덱스 k(축 t)가 닿는 이웃 면의 (f2, i3, j3).

    가이드 1장 '이웃 규칙' 1~5단계. k 는 배열이어도 됩니다.
    """
    f2 = _face_with_normal(e)
    if abs(int(_N_INT[f] @ _U_INT[f2])) == 1:
        s1 = int(_N_INT[f] @ _U_INT[f2])
        s2 = int(t @ _V_INT[f2])
        i3 = np.full_like(k, n - 1 if s1 > 0 else 0)
        j3 = k if s2 > 0 else n - 1 - k
    else:
        s1 = int(_N_INT[f] @ _V_INT[f2])
        s2 = int(t @ _U_INT[f2])
        j3 = np.full_like(k, n - 1 if s1 > 0 else 0)
        i3 = k if s2 > 0 else n - 1 - k
    return f2, i3, j3


def neighbor_table(n: int) -> np.ndarray:
    """전체 이웃 표 (6n², 8) int32. 큐브 꼭짓점 너머 대각선은 -1.

    슬롯 순서는 NEIGHBOR_SLOTS.
    면 경계를 넘는 이웃은 가이드 1장의 인덱스 교환·반전 규칙으로 정합니다.
    """
    nn = n * n
    nbr = np.full((6 * nn, 8), -1, dtype=np.int32)
    jj, ii = np.meshgrid(np.arange(n), np.arange(n), indexing="ij")
    jj = jj.ravel()
    ii = ii.ravel()
    for f in range(6):
        c = f * nn + jj * n + ii
        for s, (dj, di) in enumerate(NEIGHBOR_SLOTS):
            i2 = ii + di
            j2 = jj + dj
            in_i = (i2 >= 0) & (i2 < n)
            in_j = (j2 >= 0) & (j2 < n)
            m = in_i & in_j
            nbr[c[m], s] = f * nn + j2[m] * n + i2[m]
            for side in (-1, 1):
                # i 쪽으로만 넘어감
                m = (~in_i) & in_j & (i2 == (n if side > 0 else -1))
                if m.any():
                    f2, i3, j3 = _cross_edge(f, n, side * _U_INT[f], _V_INT[f], j2[m])
                    nbr[c[m], s] = f2 * nn + j3 * n + i3
                # j 쪽으로만 넘어감
                m = in_i & (~in_j) & (j2 == (n if side > 0 else -1))
                if m.any():
                    f2, i3, j3 = _cross_edge(f, n, side * _V_INT[f], _U_INT[f], i2[m])
                    nbr[c[m], s] = f2 * nn + j3 * n + i3
    return nbr


def cross_face_pairs(n: int, nbr: np.ndarray | None = None) -> np.ndarray:
    """면 경계를 사이에 둔 가로·세로 이웃 쌍 (k, 2). 각 쌍은 한 번씩만 나옵니다."""
    if nbr is None:
        nbr = neighbor_table(n)
    c = np.repeat(np.arange(nbr.shape[0]), len(CARDINAL_SLOTS))
    c2 = nbr[:, list(CARDINAL_SLOTS)].ravel()
    ok = (c2 >= 0) & (c // (n * n) != c2 // (n * n)) & (c < c2)
    return np.stack([c[ok], c2[ok]], axis=1)


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
