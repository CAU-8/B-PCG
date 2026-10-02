"""회랑 굽기: 히어로에서 걸어 다닐 회랑을 골라 엔진 파일로 씁니다 (docs/pipeline.md 11장).

회랑 고르기
- 출발 칸: 선상지 꼭짓점 가운데 유량이 가장 큰 칸, 없으면 유량이 가장 큰 강 칸(출구 쪽 큰 강).
- 본류: 출발 칸에서 하류로 출구까지, 상류로는 유량이 가장 큰 기여 셀을 따라 끝까지 이은 길.
- 후보 시작점: 본류 위의 강 칸들. 시작점에서 본류를 따라 length_m 만큼 거슬러 오른 점 쪽이
  회랑 방향입니다. 엔진 축(X = 동, Z = 남)에 맞춘 높이맵을 쓰므로 방향은 동서·남북 중 가까운
  쪽으로 맞춥니다(명세와 다름: 기울어진 직사각형 대신 축에 맞춘 직사각형).
- 직사각형: 시작점에서 그 방향으로 length_m, 가로로 width_m (시작점이 가로 가운데). 히어로 칸
  중심 범위 밖으로 나가면 안으로 밉니다.
- 고르는 기준: 직사각형 안 동굴 입구 칸 수가 가장 많은 후보, 같으면 출발 칸에 가까운 후보.

쓰는 파일 (out_dir, engine_dir 를 주면 같은 이름으로 복사)
| 파일 | 내용 |
|---|---|
| heightmap.bin/.json | 회랑 지표 (간격 voxel_m, 강바닥 깎기와 지표 노이즈 포함, 동굴 제외) |
| surround25.bin/.json | 히어로 전체 지표 z_m (히어로 간격, 보통 25 m) |
| water.bin/.json | 지상 수면 (호수·바다·하도). 물이 없으면 −10000 |
| water_table.bin/.json | 지하수면 z_gw |
| caves.glb | 동굴 벽 메시 (bake.mesh). 회랑에 동굴이 없으면 쓰지 않고 manifest 에 적음 |
| strata.u8/.json | 재질 부피 (수평 4 m, 수직 2 m, uint8). 위는 strata_top 을 따라감 |
| strata_top.bin/.json | 재질 부피의 윗면 (지표, 수평 4 m) — 명세에 더한 파일 |
| entrances.json | 동굴 입구 캡슐 양 끝 (엔진 좌표, 입구로 옮겨 갈 때) — 명세에 더한 파일 |
| cave_mouth.bin/.json | 지표 점의 동굴 거리 d_cave (높이맵 격자). 음수면 입구 구멍 |
| heightmap_detail.bin/.json | heightmap + 프랙탈 디테일 (bake.detail). 보기용, 솔버 결과 아님 |
| cave_mouth_detail.bin/.json | cave_mouth 와 같되 heightmap_detail 지표의 d_cave |
| manifest.json | 국소 좌표 원점, 엔진 변환, 회랑 사각형, 파일 목록, 시드, 설정 해시, git 커밋 |

*_detail 두 파일은 detail.fractal_gain > 0 일 때만 씁니다. 0 이거나 [detail] 이 없으면 쓰지 않고
지난 굽기가 남긴 것도 지웁니다(엔진은 heightmap_detail 이 있을 때만 '프랙탈 디테일' 층을 둡니다).

모든 높이맵은 bake.heightmap.write_heightmap 형식입니다. 값은 해수면 기준 고도 [m] 그대로이고
origin 의 y 가 −y_offset 이라 엔진 Y = 값 − y_offset 입니다(origin + (col·간격, 값, row·간격)).
엔진 원점(0, 0, 0)은 회랑 가운데 (높이는 회랑 최저 지표를 내림한 값) 입니다.
"""

import math
import os
import shutil
import time
from pathlib import Path

import numpy as np

from bpcg import __version__
from bpcg.bake.bundle import git_commit, write_json
from bpcg.bake.detail import add_fractal_detail, band_limits, detail_settings
from bpcg.bake.heightmap import write_heightmap
from bpcg.bake.mesh import EngineFrame, build_mesh, cave_surface, export_glb
from bpcg.geology import rocks as rk
from bpcg.volume.sample import MATERIAL_EMPTY, HeroVolume

STRATA_DX_M = 4.0  # 재질 부피 수평 간격 (pipeline.md 11장)
STRATA_DZ_M = 2.0  # 재질 부피 수직 간격
WATER_NONE_M = -10_000.0  # 물이 없는 칸의 수면 값 (pipeline.md 11장)
STRATA_WATER = 254  # 재질 부피의 물
STRATA_AIR = MATERIAL_EMPTY  # 재질 부피의 공기 (255)
MAX_CANDIDATES = 256  # 회랑 시작점 후보 수 상한
STRATA_CHUNK_POINTS = 4_000_000  # 재질 부피를 이만큼씩 나눠 계산 (메모리 상한)
WATER_SURFACE_TOL_M = 0.01  # 수면이 지표보다 이만큼 이상 낮으면 물 없음으로 봄
CAVE_MOUTH_CLIP_M = 20.0  # cave_mouth 값(지표 점의 d_cave)을 ±이 값으로 자름 (inf 없애기)
DETAIL_STEMS = ("heightmap_detail", "cave_mouth_detail")  # 프랙탈 디테일을 켰을 때만 쓰는 높이맵
DETAIL_MIN_SAMPLES = 8  # 높이맵 한 변이 이보다 짧으면 스펙트럼을 잴 수 없어 디테일을 건너뜀


def _log(log, msg: str) -> None:
    if log is not None:
        log(msg)


def _remove_stems(folder: Path, stems) -> None:
    """높이맵 파일 한 쌍(<stem>.bin/.json)이 남아 있으면 지웁니다 (끈 층의 지난 결과)."""
    for stem in stems:
        for ext in (".bin", ".json"):
            p = folder / f"{stem}{ext}"
            if p.exists():
                p.unlink()


def _centers(graph) -> tuple[np.ndarray, np.ndarray]:
    ny, nx = graph.shape
    dx = float(graph.spacing)
    jj, ii = np.divmod(np.arange(ny * nx), nx)
    return graph.origin[0] + (ii + 0.5) * dx, graph.origin[1] - (jj + 0.5) * dx


def best_donor(receiver: np.ndarray, discharge: np.ndarray) -> np.ndarray:
    """칸마다 유량이 가장 큰 기여 셀 (없으면 −1) (N,) int64. 같은 유량이면 칸 번호가 작은 쪽."""
    rcv = np.asarray(receiver, dtype=np.int64)
    n = rcv.size
    q = np.asarray(discharge, dtype=np.float64)
    ids = np.arange(n)
    donors = ids[rcv != ids]
    out = np.full(n, -1, dtype=np.int64)
    if donors.size == 0:
        return out
    # (받는 칸, 유량, −칸 번호) 순으로 정렬해 받는 칸마다 마지막 것이 최대
    order = np.lexsort((-donors, q[donors], rcv[donors]))
    d_sorted = donors[order]
    r_sorted = rcv[d_sorted]
    last = np.r_[r_sorted[1:] != r_sorted[:-1], True]
    out[r_sorted[last]] = d_sorted[last]
    return out


def main_stem(receiver: np.ndarray, discharge: np.ndarray, start: int) -> tuple[np.ndarray, int]:
    """start 를 지나는 본류 (하류 끝 출구 → 상류 끝) 칸 번호와 그 안의 start 위치."""
    rcv = np.asarray(receiver, dtype=np.int64)
    n = rcv.size
    down = [int(start)]
    seen = {int(start)}
    while rcv[down[-1]] != down[-1] and len(down) <= n:
        nxt = int(rcv[down[-1]])
        if nxt in seen:
            raise ValueError("receiver 에 순환이 있습니다")
        seen.add(nxt)
        down.append(nxt)
    donor = best_donor(rcv, discharge)
    up = []
    c = int(start)
    while donor[c] >= 0 and len(up) <= n:
        c = int(donor[c])
        up.append(c)
    path = np.array(down[::-1] + up, dtype=np.int64)
    return path, len(down) - 1


def choose_corridor(hero_state, cfg) -> dict:
    """회랑 직사각형을 고릅니다 (모듈 설명 '회랑 고르기').

    반환 dict: rect (x_min, x_max, y_min, y_max) [m] 국소, axis ('east'|'north'), sign (+1|−1,
    상류 쪽), start_cell, start_xy, apex_cell, apex_kind ('fan'|'river'|'discharge'),
    n_entrance_cells (회랑 안 입구 칸 수), n_stem_cells, length_m, width_m, n_candidates.
    """
    graph = hero_state.graph
    f = hero_state.fields
    if graph.kind != "flat":
        raise ValueError("회랑은 평면 히어로에서만 고릅니다")
    cor = cfg.profile.corridor
    length = float(cor.length_m)
    width = float(cor.width_m)
    if not (length > 0 and width > 0):
        raise ValueError(f"profile.corridor 의 length_m, width_m 은 0 보다 커야 합니다: {cor}")
    ny, nx = graph.shape
    dx = float(graph.spacing)
    xc, yc = _centers(graph)
    lo_x, hi_x = graph.origin[0] + 0.5 * dx, graph.origin[0] + (nx - 0.5) * dx
    lo_y, hi_y = graph.origin[1] - (ny - 0.5) * dx, graph.origin[1] - 0.5 * dx
    Q = np.asarray(f["discharge_m3_per_yr"], dtype=np.float64)
    rcv = np.asarray(f["receiver"], dtype=np.int64)
    is_river = np.asarray(f["is_river"], dtype=bool)
    ent = np.asarray(f["cave_entrance"]).astype(np.int64) > 0

    apexes = [a for a in (hero_state.fan_apexes or []) if "cell" in a]
    if apexes:
        apex = max(apexes, key=lambda a: (float(a.get("discharge_m3_per_yr", 0.0)), -a["cell"]))
        start, kind = int(apex["cell"]), "fan"
    elif is_river.any():
        riv = np.flatnonzero(is_river)
        start, kind = int(riv[np.argmax(Q[riv])]), "river"
    else:
        start, kind = int(np.argmax(Q)), "discharge"
    path, p0 = main_stem(rcv, Q, start)
    P = np.stack([xc[path], yc[path]], axis=1)
    s = np.concatenate([[0.0], np.cumsum(np.linalg.norm(np.diff(P, axis=0), axis=1))])
    cand = np.flatnonzero(is_river[path])
    if cand.size == 0:
        cand = np.arange(path.size)
    if cand.size > MAX_CANDIDATES:
        cand = cand[np.linspace(0, cand.size - 1, MAX_CANDIDATES).round().astype(np.int64)]
    # 축 길이가 영역보다 길면 영역에 맞춰 줄입니다.
    best = None
    for a in cand:
        b = int(np.searchsorted(s, s[a] + length))
        b = min(b, path.size - 1)
        vec = P[b] - P[a]
        if not np.any(vec):
            vec = P[-1] - P[a] if np.any(P[-1] - P[a]) else np.array([0.0, 1.0])
        axis = "east" if abs(vec[0]) >= abs(vec[1]) else "north"
        sign = 1.0 if (vec[0] if axis == "east" else vec[1]) >= 0 else -1.0
        ax, ay = P[a]
        if axis == "east":
            L = min(length, hi_x - lo_x)
            Wd = min(width, hi_y - lo_y)
            x_a, x_b = sorted((ax, ax + sign * L))
            y_a, y_b = ay - 0.5 * Wd, ay + 0.5 * Wd
        else:
            L = min(length, hi_y - lo_y)
            Wd = min(width, hi_x - lo_x)
            y_a, y_b = sorted((ay, ay + sign * L))
            x_a, x_b = ax - 0.5 * Wd, ax + 0.5 * Wd
        sx = max(lo_x - x_a, 0.0) - max(x_b - hi_x, 0.0)
        sy = max(lo_y - y_a, 0.0) - max(y_b - hi_y, 0.0)
        rect = (x_a + sx, x_b + sx, y_a + sy, y_b + sy)
        inside = (xc >= rect[0]) & (xc <= rect[1]) & (yc >= rect[2]) & (yc <= rect[3])
        n_ent = int((inside & ent).sum())
        n_stem = int(inside[path].sum())
        key = (n_ent, -abs(s[a] - s[p0]), n_stem)
        if best is None or key > best[0]:
            best = (key, a, axis, sign, rect, n_ent, n_stem, L, Wd)
    _, a, axis, sign, rect, n_ent, n_stem, L, Wd = best
    return {
        "rect": tuple(float(v) for v in rect),
        "axis": axis,
        "sign": int(sign),
        "start_cell": int(path[a]),
        "start_xy": (float(P[a, 0]), float(P[a, 1])),
        "apex_cell": start,
        "apex_kind": kind,
        "n_entrance_cells": n_ent,
        "n_stem_cells": n_stem,
        "length_m": float(L),
        "width_m": float(Wd),
        "n_candidates": int(cand.size),
    }


def _grid(rect, step: float) -> tuple[np.ndarray, np.ndarray]:
    """rect 의 북서쪽 모서리에서 시작하는 표본 열 x (동쪽으로), 행 y (남쪽으로)."""
    x_min, x_max, y_min, y_max = rect
    ncol = int(math.floor((x_max - x_min) / step + 1e-9)) + 1
    nrow = int(math.floor((y_max - y_min) / step + 1e-9)) + 1
    return x_min + np.arange(ncol) * step, y_max - np.arange(nrow) * step


def _site_frame(hero_state, cfg, cx: float, cy: float) -> dict:
    """회랑 가운데의 행성 위 위치 (site 가 있을 때). 위경도는 hero.finder 와 같은 규칙."""
    site = hero_state.site
    if site is None:
        return {"kind": "flat_hero", "note": "행성 없이 만든 평면 히어로라 행성 위 위치가 없습니다"}
    from bpcg.hero.domain import local_to_unit

    R = float(cfg.planet.radius_m)
    p = local_to_unit(site, np.array([cx]), np.array([cy]), R)[0]
    axis = np.asarray(cfg.planet.axis, dtype=np.float64)
    axis = axis / np.linalg.norm(axis)
    lat = math.degrees(math.asin(float(np.clip(p @ axis, -1.0, 1.0))))
    ref = np.eye(3)[int(np.argmin(np.abs(axis)))]
    ref = ref - (ref @ axis) * axis
    ref = ref / np.linalg.norm(ref)
    lon = math.degrees(math.atan2(float(np.cross(ref, p) @ axis), float(p @ ref)))
    return {
        "kind": "planet",
        "origin_unit": p.tolist(),
        "east": np.asarray(site.east, dtype=np.float64).tolist(),
        "north": np.asarray(site.north, dtype=np.float64).tolist(),
        "lat_deg": lat,
        "lon_deg": lon,
        "hero_center_unit": np.asarray(site.center_unit, dtype=np.float64).tolist(),
        "hero_lat_deg": float(site.lat_deg),
        "hero_lon_deg": float(site.lon_deg),
        "radius_m": R,
    }


def bake_strata(volume: HeroVolume, rect, frame: EngineFrame, cfg, out: Path) -> dict:
    """재질 부피 strata.u8/.json 과 윗면 strata_top.bin/.json 을 씁니다.

    배열 vol[행 k (북→남, +Z), 층 j (윗면에서 아래로), 열 i (서→동, +X)] uint8, C 순서.
    칸 가운데 엔진 위치 = (origin_x + i·dx, top_Y[k, i] − (j + ½)·dz, origin_z + k·dx), top_Y 는
    strata_top 높이맵(엔진 Y). 값: 0..11 재질(geology.rocks), 254 물, 255 공기.
    깊이 = groundwater.max_depth_m + 2·caves.passage_radius_m (가장 깊은 동굴 층까지).
    """
    xs, ys = _grid(rect, STRATA_DX_M)
    gx, gy = np.meshgrid(xs, ys)  # (nrow, ncol)
    cx, cy = gx.ravel(), gy.ravel()
    top = volume.surface_height(cx, cy)
    depth = float(cfg.groundwater.max_depth_m) + 2.0 * float(cfg.caves.passage_radius_m)
    nz = int(math.ceil(depth / STRATA_DZ_M))
    offs = (np.arange(nz) + 0.5) * STRATA_DZ_M
    mat = np.empty((cx.size, nz), dtype=np.uint8)
    step = max(STRATA_CHUNK_POINTS // nz, 1)
    for s in range(0, cx.size, step):
        e = min(s + step, cx.size)
        up = top[s:e, None] - offs[None, :]
        ev = volume.evaluate_grid(cx[s:e], cy[s:e], up, keys=("material", "water"))
        m = ev["material"]
        m[ev["water"]] = STRATA_WATER
        mat[s:e] = m
    vol = np.ascontiguousarray(mat.reshape(ys.size, xs.size, nz).transpose(0, 2, 1))
    origin_top = [float(xs[0] - frame.cx), -frame.y_offset, float(-(ys[0] - frame.cy))]
    top_meta = write_heightmap(out / "strata_top", top.reshape(ys.size, xs.size), STRATA_DX_M,
                               origin_top)  # fmt: skip
    tmp = out / "strata.u8.part"
    tmp.write_bytes(vol.tobytes(order="C"))
    os.replace(tmp, out / "strata.u8")
    counts = np.bincount(vol.ravel(), minlength=256)
    legend = [
        {"id": r, "name": rk.ROCK_NAMES[r], "rgb": [int(c) for c in rk.COLOR_RGB[r]]}
        for r in range(rk.N_ROCKS)
    ]
    legend += [
        {"id": STRATA_WATER, "name": "water", "rgb": [40, 110, 200]},
        {"id": STRATA_AIR, "name": "air", "rgb": [0, 0, 0]},
    ]
    meta = {
        "format": "uint8",
        "layout": "row_layer_col",
        "axes": "x_east_y_up_z_south",
        "shape": [int(ys.size), int(nz), int(xs.size)],
        "shape_names": ["rows (+Z, north to south)", "layers (down from top)", "cols (+X)"],
        "spacing_m": {"x": STRATA_DX_M, "y": STRATA_DZ_M, "z": STRATA_DX_M},
        "origin_xz": [origin_top[0], origin_top[2]],
        "vertical": "surface_following",
        "top_stem": "strata_top",
        "layer_center_rule": "Y = (strata_top value + origin.y) - (layer + 0.5) * spacing_m.y",
        "depth_m": nz * STRATA_DZ_M,
        "legend": legend,
        "counts": {str(i): int(c) for i, c in enumerate(counts) if c},
        "bpcg_version": __version__,
    }
    write_json(out / "strata.json", meta)
    return {"strata": meta, "strata_top": top_meta}


def bake_corridor(hero_state, cfg, out_dir, engine_dir=None, log=print) -> dict:
    """회랑을 골라 엔진 파일을 굽습니다 (pipeline.md 11장, 모듈 설명).

    hero_state: pipeline.HeroState (평면). cfg: 설정 (profile.corridor, caves, groundwater, planet).
    out_dir: 출력 폴더. engine_dir: 주면 같은 파일을 그 폴더(보통 engine/baked)에도 복사합니다.
    log: 진행 기록 함수 또는 None. 반환: manifest dict (manifest.json 과 같음).
    """
    t_all = time.perf_counter()
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    sec: dict[str, float] = {}
    voxel = float(cfg.profile.corridor.voxel_m)
    if not (math.isfinite(voxel) and voxel > 0):
        raise ValueError(f"profile.corridor.voxel_m 은 0 보다 커야 합니다: {voxel}")

    t = time.perf_counter()
    vol = HeroVolume(hero_state, cfg)
    sec["volume"] = time.perf_counter() - t
    t = time.perf_counter()
    cor = choose_corridor(hero_state, cfg)
    rect = cor["rect"]
    sec["choose"] = time.perf_counter() - t
    _log(
        log,
        f"[굽기] 회랑 {cor['axis']} 방향 {cor['length_m']:.0f}×{cor['width_m']:.0f} m, "
        f"시작 칸 {cor['start_cell']} ({cor['apex_kind']} 기준), 입구 칸 {cor['n_entrance_cells']}",
    )

    # --- 높이맵 (회랑 지표)
    t = time.perf_counter()
    xs, ys = _grid(rect, voxel)
    gx, gy = np.meshgrid(xs, ys)
    surf = vol.surface_height(gx.ravel(), gy.ravel()).reshape(gx.shape)
    cx = 0.5 * (rect[0] + rect[1])
    cy = 0.5 * (rect[2] + rect[3])
    frame = EngineFrame(cx=float(cx), cy=float(cy), y_offset=float(math.floor(surf.min())))
    origin = [float(xs[0] - cx), -frame.y_offset, float(-(ys[0] - cy))]
    files: dict[str, dict] = {}
    files["heightmap"] = write_heightmap(out / "heightmap", surf, voxel, origin)
    sec["heightmap"] = time.perf_counter() - t
    _log(log, f"[굽기] 높이맵 {xs.size}×{ys.size} (간격 {voxel:g} m), {sec['heightmap']:.2f} s")

    # --- 수면·지하수면 (같은 격자)
    t = time.perf_counter()
    hw = vol.water_surface(gx.ravel(), gy.ravel()).reshape(gx.shape)
    wet = np.isfinite(hw) & (hw >= surf - WATER_SURFACE_TOL_M)
    files["water"] = write_heightmap(out / "water", np.where(wet, hw, WATER_NONE_M), voxel, origin)
    files["water"]["n_wet"] = int(wet.sum())
    zgw = vol.water_table(gx.ravel(), gy.ravel()).reshape(gx.shape)
    files["water_table"] = write_heightmap(out / "water_table", zgw, voxel, origin)
    sec["water"] = time.perf_counter() - t

    # --- 동굴 입구 구멍: 지표 점에서의 동굴 거리. 음수면 지표가 동굴 빈 곳 안이라 엔진이 지형을
    # 뚫습니다. SDF 라 쌍선형 보간해도 구멍 가장자리가 매끄럽습니다.
    t = time.perf_counter()
    d_mouth = vol.evaluate_grid(gx.ravel(), gy.ravel(), surf.reshape(-1, 1), keys=("d_cave",))
    mouth = np.clip(d_mouth["d_cave"].reshape(gx.shape), -CAVE_MOUTH_CLIP_M, CAVE_MOUTH_CLIP_M)
    files["cave_mouth"] = write_heightmap(out / "cave_mouth", mouth, voxel, origin)
    files["cave_mouth"]["n_open"] = int((mouth < 0.0).sum())
    sec["cave_mouth"] = time.perf_counter() - t

    # --- 프랙탈 디테일 (보기용): 히어로 격자가 못 그린 짧은 파장을 이어 그린 지표와 그 지표의
    # 동굴 입구 구멍. 끄면(fractal_gain 0) 쓰지 않고 지난 굽기의 파일도 지웁니다. 엔진은
    # heightmap_detail 이 있을 때만 '프랙탈 디테일' 층을 둡니다.
    det = detail_settings(cfg)
    if det is not None:
        k_lo, k_hi = band_limits(voxel, det["min_wavelength_m"], det["max_wavelength_m"])
        if k_hi <= k_lo or min(surf.shape) < DETAIL_MIN_SAMPLES:
            _log(log, f"[굽기] 프랙탈 디테일: 간격 {voxel:g} m 회랑에는 더할 파장이 없습니다")
            det = None
    if det is not None:
        t = time.perf_counter()
        try:
            surf_d, dmeta = add_fractal_detail(surf, gx, gy, voxel, wet, vol, cfg, mouth=mouth)
        except ValueError as e:
            _log(log, f"[굽기] 프랙탈 디테일을 건너뜁니다: {e}")
            det = None
    if det is not None:
        files["heightmap_detail"] = write_heightmap(out / "heightmap_detail", surf_d, voxel, origin)
        files["heightmap_detail"]["detail"] = dmeta
        ev = vol.evaluate_grid(gx.ravel(), gy.ravel(), surf_d.reshape(-1, 1), keys=("d_cave",))
        mouth_d = np.clip(ev["d_cave"].reshape(gx.shape), -CAVE_MOUTH_CLIP_M, CAVE_MOUTH_CLIP_M)
        files["cave_mouth_detail"] = write_heightmap(out / "cave_mouth_detail", mouth_d, voxel,
                                                     origin)  # fmt: skip
        files["cave_mouth_detail"]["n_open"] = int((mouth_d < 0.0).sum())
        sec["detail"] = time.perf_counter() - t
        _log(
            log,
            f"[굽기] 프랙탈 디테일: RMS {dmeta['rms_m']:.2f} m (최대 {dmeta['max_abs_m']:.1f} m), "
            f"β {dmeta['beta_before']:.2f} → {dmeta['beta_after']:.2f} "
            f"({dmeta['beta_wavelength_m'][0]:g}–{dmeta['beta_wavelength_m'][1]:g} m), "
            f"웅덩이 채움 {dmeta['n_filled']} 칸, {sec['detail']:.2f} s",
        )
    else:
        _remove_stems(out, DETAIL_STEMS)

    # --- 히어로 전체 25 m 지표
    g = hero_state.graph
    ny, nx = g.shape
    dxh = float(g.spacing)
    z_hero = np.asarray(hero_state.fields["z_m"], dtype=np.float64).reshape(ny, nx)
    o_s = [
        float(g.origin[0] + 0.5 * dxh - cx),
        -frame.y_offset,
        float(-((g.origin[1] - 0.5 * dxh) - cy)),
    ]
    files["surround25"] = write_heightmap(out / "surround25", z_hero, dxh, o_s)

    # --- 동굴 메시
    t = time.perf_counter()
    verts, faces, cdiag = cave_surface(vol, rect, voxel, log=None)
    caves_note = None
    if faces.shape[0]:
        mesh = build_mesh(verts, faces, vol, frame, voxel)
        size = export_glb(mesh, out / "caves.glb")
        files["caves"] = {
            "file": "caves.glb",
            "bytes": size,
            "vertices": int(len(mesh.vertices)),
            "faces": int(len(mesh.faces)),
            "color_0": "rgb = rock color, alpha = material id (0..11) / 255",
            "normals": "point into the cave void (front faces seen from inside)",
        }
    else:
        caves_note = "회랑 안에 동굴이 없어 caves.glb 를 쓰지 않았습니다"
        stale = out / "caves.glb"
        if stale.exists():
            stale.unlink()
    sec["caves"] = time.perf_counter() - t
    _log(
        log,
        f"[굽기] 동굴 메시: 삼각형 {cdiag.get('faces_kept', 0)} "
        f"(조각 {cdiag['tiles_with_caves']}/{cdiag['tiles']}), {sec['caves']:.2f} s",
    )

    # --- 재질 부피
    t = time.perf_counter()
    st = bake_strata(vol, rect, frame, cfg, out)
    files.update(st)
    sec["strata"] = time.perf_counter() - t
    shp = st["strata"]["shape"]
    _log(log, f"[굽기] 재질 부피 {shp[2]}×{shp[0]}×{shp[1]} (열×행×층), {sec['strata']:.2f} s")

    # --- 입구 위치 (엔진 좌표). 캡슐 inside 끝은 통로 한가운데, outside 끝은 산비탈 밖 공중입니다.
    ent_in: list = []
    ent_out: list = []
    if vol.cap_a.shape[0]:
        mid = 0.5 * (vol.cap_a + vol.cap_b)
        ins = (
            (mid[:, 0] >= rect[0])
            & (mid[:, 0] <= rect[1])
            & (mid[:, 1] >= rect[2])
            & (mid[:, 1] <= rect[3])
        )
        ent_in = frame.to_engine(vol.cap_a[ins]).round(3).tolist()
        ent_out = frame.to_engine(vol.cap_b[ins]).round(3).tolist()
    write_json(
        out / "entrances.json",
        {
            "format": "bpcg-entrances",
            "axes": "x_east_y_up_z_south",
            "count": len(ent_in),
            "inside": ent_in,
            "outside": ent_out,
            "rule": "inside = 통로 한가운데 끝, outside = 산비탈 밖 공중 끝 (같은 동굴 층 높이)",
        },
        max_array=None,
    )

    file_names = [
        "heightmap.bin", "heightmap.json", "surround25.bin", "surround25.json",
        "water.bin", "water.json", "water_table.bin", "water_table.json",
        "strata.u8", "strata.json", "strata_top.bin", "strata_top.json", "entrances.json",
        "cave_mouth.bin", "cave_mouth.json",
    ]  # fmt: skip
    if "caves" in files:
        file_names.append("caves.glb")
    for stem in DETAIL_STEMS:
        if stem in files:
            file_names += [f"{stem}.bin", f"{stem}.json"]
    sec["total"] = time.perf_counter() - t_all
    manifest = {
        "format": "bpcg-corridor",
        "bpcg_version": __version__,
        "git_commit": git_commit(),
        "config_digest": cfg.digest(),
        "seed": int(cfg.planet.seed),
        "profile": cfg.profile.get("name"),
        "frame": frame.as_dict(),
        "site": _site_frame(hero_state, cfg, cx, cy),
        "corridor": {
            **cor,
            "rect_local_m": {
                "x_min": rect[0],
                "x_max": rect[1],
                "y_min": rect[2],
                "y_max": rect[3],
            },
            "rect_engine_m": {
                "x_min": rect[0] - cx,
                "x_max": rect[1] - cx,
                "z_min": -(rect[3] - cy),
                "z_max": -(rect[2] - cy),
            },
            "voxel_m": voxel,
        },  # fmt: skip
        "hero": {
            "shape": [int(ny), int(nx)],
            "spacing_m": dxh,
            "origin_local_m": [float(g.origin[0]), float(g.origin[1])],
        },
        "files": file_names,
        "file_meta": files,
        "caves": {
            **{k: v for k, v in cdiag.items() if k != "seconds"},
            "note": caves_note,
            "n_capsules": int(vol.cap_a.shape[0]),
            "n_unopened_entrances": int(vol.n_unopened),
            "n_entrances": len(ent_in),
            "entrances_file": "entrances.json",
        },
        "seconds": sec,
    }
    write_json(out / "manifest.json", manifest)
    file_names.append("manifest.json")
    if engine_dir is not None:
        eng = Path(engine_dir)
        eng.mkdir(parents=True, exist_ok=True)
        for name in file_names:
            shutil.copy2(out / name, eng / name)
        if "caves" not in files and (eng / "caves.glb").exists():
            (eng / "caves.glb").unlink()
        _remove_stems(eng, [s for s in DETAIL_STEMS if s not in files])
        _log(log, f"[굽기] 엔진 폴더에도 복사했습니다: {eng}")
    _log(log, f"[굽기] 끝: {sec['total']:.2f} s → {out}")
    return manifest
