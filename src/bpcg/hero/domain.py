"""히어로 영역: 접평면 평면 격자와 구면 점 사이 변환 (docs/pipeline.md 9장 hero/domain.py).

히어로는 한 변 profile.hero.size_m, 간격 profile.hero.spacing_m 인 정사각형 평면 격자입니다.
국소 좌표 (x, y) = (동, 북) [m] 이고 영역 가운데가 원점입니다. 평면 셀 번호는 c = j·nx + i 이고
0번 행이 북쪽 끝입니다(core.graph.flat_graph).

국소 점의 구면 위치는 unit = normalize(center·R + east·x + north·y) 입니다(접평면에서 중심으로
되돌린 점, gnomonic). 32 km 영역에서 거리 왜곡은 (16 km / R)² ≈ 6e-6 이라 무시합니다.
"""

import math
from dataclasses import dataclass

import numpy as np

from bpcg.core.graph import CellGraph, flat_graph
from bpcg.core.hashing import hash3

_STREAM_HERO_JITTER = 9101  # 히어로 노드 흔들기 시드 갈래 (다른 모듈의 갈래 번호와 겹치지 않게)

EDGES = ("north", "south", "east", "west")


MAX_HERO_CELLS = 5_000 * 5_000  # 히어로 칸 수 상한 (노트북 기준 laptop 기본 1280² 의 약 15 배)


@dataclass(frozen=True)
class EdgeHit:
    """영역 가운데에서 한 방향으로 그은 반직선이 가장자리와 만나는 곳.

    edge: 'north' | 'south' | 'east' | 'west'. index: 그 가장자리를 따라 센 칸 번호
    (북·남 가장자리는 열 i, 동·서 가장자리는 행 j). x, y: 만나는 점의 국소 좌표 [m].
    """

    edge: str
    index: int
    x: float
    y: float


def hero_grid_size(cfg) -> tuple[int, float]:
    """(한 변 칸 수 n, 간격 [m]). n = round(size_m / spacing_m).

    히어로 영역은 profile.hero.size_m (한 변) 과 spacing_m (칸 간격) 으로 늘리거나 줄입니다.
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


def hero_grid_info(cfg) -> dict:
    """히어로 격자 진단값: 한 변 칸 수, 간격, 실제 한 변, 설정한 한 변, 칸 수 [m]."""
    n, dx = hero_grid_size(cfg)
    return {
        "n_side": n,
        "spacing_m": dx,
        "size_m": n * dx,
        "requested_size_m": float(cfg.profile.hero.size_m),
        "n_cells": n * n,
    }


def hero_jitter_seed(cfg) -> int:
    """히어로 노드 흔들기 시드 = hash3(planet.seed, 9101, 0) 의 위 31비트."""
    h = hash3(np.int64(int(cfg.planet.seed)), np.int64(_STREAM_HERO_JITTER), np.int64(0))
    return int(h >> np.uint64(33))


def hero_flat_graph(cfg) -> CellGraph:
    """히어로 평면 그래프: n×n, 간격 spacing_m, 노드 흔들기 landscape.jitter, 가운데가 원점.

    origin(북서쪽 모서리 칸의 바깥 모서리) = (−n·dx/2, +n·dx/2) [m] 입니다.
    """
    n, dx = hero_grid_size(cfg)
    half = 0.5 * n * dx
    return flat_graph(
        n,
        n,
        dx,
        jitter=float(cfg.landscape.jitter),
        seed=hero_jitter_seed(cfg),
        origin=(-half, half),
    )


def tangent_frame(center: np.ndarray, axis: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """중심 단위 벡터에서 동·북 단위 벡터 (east (3,), north (3,)).

    east = normalize(axis × center), north = center × east. 중심이 자전축과 거의 나란하면(극)
    동쪽이 정해지지 않으므로 축과 가장 수직인 좌표축을 대신 씁니다.
    """
    c = np.asarray(center, dtype=np.float64)
    a = np.asarray(axis, dtype=np.float64)
    if c.shape != (3,) or a.shape != (3,):
        raise ValueError("center 와 axis 는 (3,) 벡터여야 합니다")
    nc = float(np.linalg.norm(c))
    na = float(np.linalg.norm(a))
    if not (nc > 0 and na > 0):
        raise ValueError("center 와 axis 는 길이가 0 이 아니어야 합니다")
    c = c / nc
    a = a / na
    e = np.cross(a, c)
    if float(np.linalg.norm(e)) < 1e-9:
        alt = np.eye(3)[int(np.argmin(np.abs(c)))]
        e = np.cross(alt, c)
    e = e / np.linalg.norm(e)
    north = np.cross(c, e)
    return e, north / np.linalg.norm(north)


def local_to_unit(site, x: np.ndarray, y: np.ndarray, radius_m: float) -> np.ndarray:
    """국소 (동 x, 북 y) [m] (M,) → 구면 단위 벡터 (M, 3).

    unit = normalize(center·R + east·x + north·y).
    """
    xx = np.atleast_1d(np.asarray(x, dtype=np.float64))
    yy = np.atleast_1d(np.asarray(y, dtype=np.float64))
    if xx.shape != yy.shape or xx.ndim != 1:
        raise ValueError(f"x, y 는 같은 (M,) 모양이어야 합니다: {xx.shape} {yy.shape}")
    p = (
        float(radius_m) * np.asarray(site.center_unit)[None, :]
        + xx[:, None] * np.asarray(site.east)[None, :]
        + yy[:, None] * np.asarray(site.north)[None, :]
    )
    return p / np.linalg.norm(p, axis=1, keepdims=True)


def direction_to_local(site, vector: np.ndarray) -> tuple[float, float]:
    """3D 방향 벡터를 접평면 국소 방향 (동, 북) 으로 (정규화하지 않음)."""
    v = np.asarray(vector, dtype=np.float64)
    return float(v @ np.asarray(site.east)), float(v @ np.asarray(site.north))


def hero_graph(site, cfg) -> tuple[CellGraph, np.ndarray]:
    """히어로 평면 그래프와 칸마다 L0 값을 읽을 구면 단위 벡터 (pipeline.md 9장).

    site: hero.finder.HeroSite (center_unit, east, north). cfg: profile.hero, landscape.jitter,
    planet.radius_m, planet.seed.
    반환: (graph flat (n×n, 노드 흔들기), unit_points (N, 3) float64). unit_points 는 흔든 대표점
    (graph.pos 의 동·북)을 local_to_unit 으로 바꾼 것입니다.
    """
    graph = hero_flat_graph(cfg)
    unit = local_to_unit(site, graph.pos[:, 0], graph.pos[:, 1], float(cfg.planet.radius_m))
    return graph, np.ascontiguousarray(unit)


def edge_hit(direction: tuple[float, float], n: int, spacing: float) -> EdgeHit:
    """가운데 (0, 0) 에서 국소 방향 (dx, dy) 로 그은 반직선이 영역 가장자리와 만나는 곳.

    direction: (동, 북) 방향 (길이 상관없음, 0 이면 안 됨). n: 한 변 칸 수. spacing: 간격 [m].
    |dx| ≥ |dy| 이면 동·서 가장자리, 아니면 북·남 가장자리입니다(모서리는 동·서).
    """
    dx, dy = float(direction[0]), float(direction[1])
    m = max(abs(dx), abs(dy))
    if not (math.isfinite(m) and m > 0):
        raise ValueError(f"방향 벡터가 0 이거나 유한하지 않습니다: {direction}")
    half = 0.5 * n * spacing
    t = half / m
    x, y = dx * t, dy * t
    if abs(dx) >= abs(dy):
        edge = "east" if dx > 0 else "west"
        index = int(np.clip(math.floor((half - y) / spacing), 0, n - 1))  # 행 j (북쪽이 0)
    else:
        edge = "north" if dy > 0 else "south"
        index = int(np.clip(math.floor((x + half) / spacing), 0, n - 1))  # 열 i (서쪽이 0)
    return EdgeHit(edge=edge, index=index, x=x, y=y)


def edge_cells(n: int, edge: str, index: int, count: int = 1) -> np.ndarray:
    """가장자리 edge 를 따라 index 를 가운데로 한 연속 count 칸의 셀 번호 (count,) int64.

    영역 밖으로 나가면 안쪽으로 밀어 count 칸을 모두 가장자리 위에 둡니다.
    """
    if edge not in EDGES:
        raise ValueError(f"edge 는 {EDGES} 중 하나여야 합니다: {edge!r}")
    if not (1 <= count <= n):
        raise ValueError(f"count 는 1..{n} 이어야 합니다: {count}")
    start = int(np.clip(int(index) - count // 2, 0, n - count))
    k = np.arange(start, start + count, dtype=np.int64)
    if edge == "north":
        return k
    if edge == "south":
        return (n - 1) * n + k
    if edge == "west":
        return k * n
    return k * n + (n - 1)
