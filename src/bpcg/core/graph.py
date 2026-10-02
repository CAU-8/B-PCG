"""점과 이웃 목록으로 된 셀 그래프 (설계도 5장 '일반 그래프').

물길·솔버·지하수 코드는 이 그래프만 받습니다.
그래서 구면 L0 와 평면 히어로 L2 가 같은 코드를 씁니다.

규칙
- nbr[c, s]: 셀 c 의 s 번째 이웃 (슬롯 순서 (dj, di) 는 가이드 1장, 없으면 -1).
- dist[c, s]: 두 셀 대표점 사이 거리 [m]. 이웃이 없으면 inf. 구면은 대원 거리.
- area[c]: 셀 면적 [m²]. 노드 흔들기를 해도 면적은 그대로입니다.
- pos[c]: 대표점 [m]. 구면은 반지름 R 위의 점(행성 중심 기준), 평면은 (동, 북, 0) 국소 좌표.
- 평면 셀 번호 c = j·nx + i. 0번 행이 북쪽 끝이고 j 가 늘면 남쪽(엔진 높이맵과 같은 순서).
"""

from dataclasses import dataclass

import numpy as np

from bpcg.core import cubesphere as cs
from bpcg.core.hashing import hash_uniform_array

_JITTER_STREAM_A = 101
_JITTER_STREAM_B = 102


@dataclass(eq=False)
class CellGraph:
    kind: str  # "sphere" 또는 "flat"
    shape: tuple[int, ...]  # 구면 (6, n, n), 평면 (ny, nx)
    pos: np.ndarray  # (N, 3) float64 [m]
    nbr: np.ndarray  # (N, 8) int32
    dist: np.ndarray  # (N, 8) float64 [m]
    area: np.ndarray  # (N,) float64 [m²]
    spacing: float  # 대표 칸 크기 [m] = sqrt(평균 면적)
    R: float | None = None  # 구면 반지름 [m]
    origin: tuple[float, float] = (0.0, 0.0)  # 평면: 북서쪽 모서리 칸의 바깥 모서리 (동, 북) [m]

    @property
    def n_cells(self) -> int:
        return self.nbr.shape[0]

    @property
    def width(self) -> np.ndarray:
        """물이 지나가는 등고선 폭 [m] (칸 크기 근사 sqrt(면적))."""
        return np.sqrt(self.area)

    def unit(self) -> np.ndarray:
        """구면: 대표점의 단위 벡터. 평면에는 쓰지 않습니다."""
        if self.kind != "sphere":
            raise ValueError("unit() 은 구면 그래프에서만 씁니다")
        return self.pos / np.linalg.norm(self.pos, axis=1, keepdims=True)

    def boundary_mask(self) -> np.ndarray:
        """평면 그래프의 가장자리 칸 (이웃 슬롯 중 하나라도 비어 있음)."""
        return (self.nbr < 0).any(axis=1)

    def as_image(self, field: np.ndarray) -> np.ndarray:
        """셀 값 배열을 격자 모양으로 (평면 (ny, nx), 구면 (6, n, n))."""
        return np.asarray(field).reshape(self.shape)


def _jitter_offsets(n_cells: int, jitter: float, seed: int) -> tuple[np.ndarray, np.ndarray]:
    ids = np.arange(n_cells, dtype=np.int64)
    ja = (hash_uniform_array(ids, seed, _JITTER_STREAM_A) - 0.5) * jitter
    jb = (hash_uniform_array(ids, seed, _JITTER_STREAM_B) - 0.5) * jitter
    return ja, jb


def flat_graph(
    ny: int,
    nx: int,
    dx: float,
    jitter: float = 0.0,
    seed: int = 0,
    origin: tuple[float, float] = (0.0, 0.0),
) -> CellGraph:
    """평면 격자 그래프. 칸 크기 dx [m], 노드 흔들기 jitter (칸 크기 대비, 0~1)."""
    jj, ii = np.meshgrid(np.arange(ny), np.arange(nx), indexing="ij")
    jj = jj.ravel()
    ii = ii.ravel()
    n_cells = ny * nx
    x = origin[0] + (ii + 0.5) * dx
    y = origin[1] - (jj + 0.5) * dx
    if jitter > 0:
        ja, jb = _jitter_offsets(n_cells, jitter, seed)
        x = x + ja * dx
        y = y + jb * dx
    pos = np.stack([x, y, np.zeros(n_cells)], axis=1)
    nbr = np.full((n_cells, 8), -1, dtype=np.int32)
    for s, (dj, di) in enumerate(cs.NEIGHBOR_SLOTS):
        j2 = jj + dj
        i2 = ii + di
        ok = (j2 >= 0) & (j2 < ny) & (i2 >= 0) & (i2 < nx)
        nbr[ok, s] = j2[ok] * nx + i2[ok]
    dist = _distances(pos, nbr, None)
    area = np.full(n_cells, dx * dx)
    return CellGraph(
        kind="flat",
        shape=(ny, nx),
        pos=pos,
        nbr=nbr,
        dist=dist,
        area=area,
        spacing=float(dx),
        origin=(float(origin[0]), float(origin[1])),
    )


def sphere_graph(n: int, R: float, jitter: float = 0.0, seed: int = 0) -> CellGraph:
    """구면 큐브스피어 그래프 (면당 n 칸, 반지름 R [m])."""
    grid = cs.cubesphere_grid(n, R)
    unit = grid.pos / R
    n_cells = grid.n_cells
    area = grid.area_full()
    if jitter > 0:
        # 면의 u, v 방향으로 칸 크기 비율만큼 옮긴 뒤 구면으로 되돌립니다.
        ja, jb = _jitter_offsets(n_cells, jitter, seed)
        f = np.arange(n_cells) // (n * n)
        h = np.sqrt(area) / R
        unit = unit + (ja * h)[:, None] * cs.FACE_U[f] + (jb * h)[:, None] * cs.FACE_V[f]
        unit /= np.linalg.norm(unit, axis=1, keepdims=True)
    pos = unit * R
    nbr = cs.neighbor_table(n)
    dist = _distances(pos, nbr, R)
    return CellGraph(
        kind="sphere",
        shape=(6, n, n),
        pos=pos,
        nbr=nbr,
        dist=dist,
        area=area,
        spacing=float(np.sqrt(area.mean())),
        R=float(R),
    )


def _distances(pos: np.ndarray, nbr: np.ndarray, R: float | None) -> np.ndarray:
    dist = np.full(nbr.shape, np.inf)
    for s in range(nbr.shape[1]):
        ok = nbr[:, s] >= 0
        a = pos[ok]
        b = pos[nbr[ok, s]]
        if R is None:
            dist[ok, s] = np.linalg.norm(a - b, axis=1)
        else:
            dist[ok, s] = cs.neighbor_distance(a / R, b / R, R)
    return dist
