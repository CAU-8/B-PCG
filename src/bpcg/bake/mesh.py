"""동굴 메시: 3D 샘플 함수의 동굴 SDF 를 조각별 marching cubes 로 glb 로 굽습니다.

docs/pipeline.md 11장 caves.glb. 엔진 형식은 engine/README.md 'glb 타일' 을 따릅니다.

- 엔진 축: X = 동, Y = 위, Z = 남 [m]. 히어로 국소 (동 e, 북 n, 위 u) 는
  (X, Y, Z) = (e − c_x, u − y_offset, −(n − c_y)) 입니다(EngineFrame). 이 변환은 회전(행렬식 +1)이라
  삼각형 감김 방향이 그대로 유지됩니다.
- 동굴 벽은 d_cave = 0 등위면입니다(땅 위로 나온 삼각형은 버림). 법선과 삼각형 앞면(glTF 의
  반시계 방향)은 동굴 빈 곳 쪽(d_cave 가 줄어드는 쪽)을 봅니다. 동굴 안에서 보는 면이기 때문입니다.
- COLOR_0 은 벽 바로 뒤 암석의 색(geology.rocks.COLOR_RGB)이고, 알파 채널에 재질 번호를 넣습니다
  (A = 재질 번호, 0..11. 정규화 uint8 이라 셰이더에서는 A·255 로 읽음).
- 조각(타일)은 수평 tile_voxels 칸이고 이웃 조각과 한 칸 겹칩니다. 높이 방향은 조각 안 동굴 층과
  입구 캡슐 높이 ± (통로 반지름 + 2 칸) 만 계산하며, 높이 격자는 전역 voxel 배수에 맞춰 이웃
  조각의 꼭짓점이 정확히 겹치게 합니다(나중에 merge_vertices 로 붙임).
"""

import math
import os
import time
import warnings
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from bpcg.geology import rocks as rk

TILE_VOXELS = 64  # 조각 한 변 복셀 수 (우리가 정한 값)
BAND_PAD_VOXELS = 2  # 동굴 띠 위아래 여유 [복셀]
SDF_CLIP_VOXELS = 4.0  # marching cubes 에 넘기는 SDF 를 ±이 값·복셀로 자름 (inf 없애기)
COLOR_PROBE_VOXELS = 0.5  # 꼭짓점 색: 법선 반대(암석) 쪽으로 이만큼 들어간 점의 재질


@dataclass(frozen=True)
class EngineFrame:
    """히어로 국소 좌표 → 엔진 좌표 변환 (pipeline.md 11장 끝, engine/README.md '좌표').

    cx, cy: 엔진 원점이 되는 히어로 국소 (동, 북) [m] (회랑 가운데). y_offset: 엔진 Y = 고도 −
    y_offset [m] (회랑 최저 지표를 내림한 값, float32 정밀도와 플레이어 낙하 한계를 위해).
    """

    cx: float
    cy: float
    y_offset: float

    def to_engine(self, pts: np.ndarray) -> np.ndarray:
        """(M, 3) 국소 (동, 북, 위) → (M, 3) 엔진 (X, Y, Z)."""
        p = np.asarray(pts, dtype=np.float64)
        return np.stack([p[:, 0] - self.cx, p[:, 2] - self.y_offset, -(p[:, 1] - self.cy)], axis=1)

    def to_local(self, pts: np.ndarray) -> np.ndarray:
        """(M, 3) 엔진 (X, Y, Z) → (M, 3) 국소 (동, 북, 위)."""
        p = np.asarray(pts, dtype=np.float64)
        return np.stack([p[:, 0] + self.cx, self.cy - p[:, 2], p[:, 1] + self.y_offset], axis=1)

    def as_dict(self) -> dict:
        return {
            "axes": "x_east_y_up_z_south",
            "units": "m",
            "local_origin_east_north_m": [self.cx, self.cy],
            "y_offset_m": self.y_offset,
            "rule": "X = east - cx, Y = elevation - y_offset, Z = -(north - cy)",
        }


def _merge_intervals(iv: list[tuple[float, float]]) -> list[tuple[float, float]]:
    iv = sorted(iv)
    out: list[list[float]] = []
    for lo, hi in iv:
        if out and lo <= out[-1][1]:
            out[-1][1] = max(out[-1][1], hi)
        else:
            out.append([lo, hi])
    return [(a, b) for a, b in out]


def cave_bands(volume, bbox: tuple[float, float, float, float], pad_m: float) -> list:
    """수평 상자 bbox = (x_min, x_max, y_min, y_max) [m] 안 동굴이 있을 수 있는 높이 구간들.

    상자에 닿는 히어로 칸(한 칸 여유)의 동굴 층 높이와, 상자에 닿는 입구 캡슐의 높이에
    ± (통로 반지름 + pad_m) 를 붙여 겹치는 구간을 합칩니다. 반환: [(z_lo, z_hi), ...] [m].
    """
    x_min, x_max, y_min, y_max = bbox
    dx, r = volume.dx, volume.r_pass
    i0 = max(int(math.floor((x_min - volume.x0) / dx - 0.5)) - 1, 0)
    i1 = min(int(math.ceil((x_max - volume.x0) / dx - 0.5)) + 1, volume.nx - 1)
    j0 = max(int(math.floor((volume.y0 - y_max) / dx - 0.5)) - 1, 0)
    j1 = min(int(math.ceil((volume.y0 - y_min) / dx - 0.5)) + 1, volume.ny - 1)
    iv: list[tuple[float, float]] = []
    if i0 <= i1 and j0 <= j1:
        lv = volume.cave_levels[:, j0 : j1 + 1, i0 : i1 + 1]
        for k in range(lv.shape[0]):
            v = lv[k][np.isfinite(lv[k])]
            if v.size:
                iv.append((float(v.min()) - r - pad_m, float(v.max()) + r + pad_m))
    a, b = volume.cap_a, volume.cap_b
    if a.shape[0]:
        lo_x = np.minimum(a[:, 0], b[:, 0]) - r
        hi_x = np.maximum(a[:, 0], b[:, 0]) + r
        lo_y = np.minimum(a[:, 1], b[:, 1]) - r
        hi_y = np.maximum(a[:, 1], b[:, 1]) + r
        hit = (hi_x >= x_min) & (lo_x <= x_max) & (hi_y >= y_min) & (lo_y <= y_max)
        for e in np.flatnonzero(hit):
            zlo = min(a[e, 2], b[e, 2]) - r - pad_m
            zhi = max(a[e, 2], b[e, 2]) + r + pad_m
            iv.append((float(zlo), float(zhi)))
    return _merge_intervals(iv)


def cave_surface(
    volume, rect: tuple[float, float, float, float], voxel_m: float, log=None
) -> tuple[np.ndarray, np.ndarray, dict]:
    """회랑 rect = (x_min, x_max, y_min, y_max) [m] 안 동굴 벽 삼각형 (국소 좌표).

    반환: (verts (V, 3) 국소 (동, 북, 위) [m] float64, faces (F, 3) int64, diag). 삼각형 앞면과
    법선은 동굴 빈 곳 쪽입니다. 땅 위(d₁ ≥ 0)로 나온 삼각형은 버립니다. 동굴이 없으면 V = F = 0.
    diag: tiles, tiles_with_caves, voxels, faces_raw, faces_kept, seconds.
    """
    from skimage.measure import marching_cubes

    t_all = time.perf_counter()
    v = float(voxel_m)
    if not (math.isfinite(v) and v > 0):
        raise ValueError(f"voxel_m 은 0 보다 커야 합니다: {voxel_m}")
    x_min, x_max, y_min, y_max = rect
    nxv = int(math.floor((x_max - x_min) / v + 1e-9)) + 1
    nyv = int(math.floor((y_max - y_min) / v + 1e-9)) + 1
    xs = x_min + np.arange(nxv) * v
    ys = y_min + np.arange(nyv) * v
    pad = BAND_PAD_VOXELS * v
    clip = SDF_CLIP_VOXELS * v
    verts_l, faces_l = [], []
    n_vert = 0
    diag = {"tiles": 0, "tiles_with_caves": 0, "voxels": 0}
    for ia in range(0, max(nxv - 1, 1), TILE_VOXELS):
        ib = min(ia + TILE_VOXELS, nxv - 1)
        for ja in range(0, max(nyv - 1, 1), TILE_VOXELS):
            jb = min(ja + TILE_VOXELS, nyv - 1)
            diag["tiles"] += 1
            bands = cave_bands(volume, (xs[ia], xs[ib], ys[ja], ys[jb]), pad)
            if not bands:
                continue
            tx = xs[ia : ib + 1]
            ty = ys[ja : jb + 1]
            gx, gy = np.meshgrid(tx, ty, indexing="ij")
            cx, cy = gx.ravel(), gy.ravel()
            for lo, hi in bands:
                k0 = int(math.floor(lo / v))
                k1 = int(math.ceil(hi / v))
                if k1 - k0 < 2:
                    k1 = k0 + 2
                zz = np.arange(k0, k1 + 1) * v
                up = np.broadcast_to(zz[None, :], (cx.size, zz.size))
                ev = volume.evaluate_grid(cx, cy, up, keys=("d_cave",))
                f = np.clip(ev["d_cave"], -clip, clip).reshape(tx.size, ty.size, zz.size)
                diag["voxels"] += f.size
                if f.min() >= 0.0 or f.max() <= 0.0:
                    continue
                with warnings.catch_warnings():
                    # scikit-image 0.26 이 NumPy 2.5 에서 내는 shape 대입 경고 (결과와 무관)
                    warnings.simplefilter("ignore", DeprecationWarning)
                    vt, fc, _, _ = marching_cubes(
                        f, level=0.0, spacing=(v, v, v), allow_degenerate=False
                    )
                if fc.shape[0] == 0:
                    continue
                vt = vt + np.array([tx[0], ty[0], zz[0]])
                verts_l.append(vt)
                # skimage 의 감김은 값이 커지는 쪽(동굴 밖)을 앞면으로 둡니다 → 뒤집어 빈 곳 쪽으로.
                faces_l.append(fc[:, [0, 2, 1]].astype(np.int64) + n_vert)
                n_vert += vt.shape[0]
            diag["tiles_with_caves"] += 1
        if log is not None:
            log(f"[동굴 메시] 열 {ib}/{nxv - 1} 조각 끝, 꼭짓점 {n_vert}")
    if not verts_l:
        diag.update(faces_raw=0, faces_kept=0, seconds=time.perf_counter() - t_all)
        return np.zeros((0, 3)), np.zeros((0, 3), dtype=np.int64), diag
    verts = np.vstack(verts_l)
    faces = np.vstack(faces_l)
    diag["faces_raw"] = int(faces.shape[0])
    # 땅 위로 나온 삼각형 버리기 (무게중심의 d₁, 지표 노이즈 포함)
    cen = verts[faces].mean(axis=1)
    d1 = volume.evaluate(cen)["d1"]
    faces = faces[d1 < 0.0]
    diag["faces_kept"] = int(faces.shape[0])
    used = np.unique(faces)
    remap = np.full(verts.shape[0], -1, dtype=np.int64)
    remap[used] = np.arange(used.size)
    verts = verts[used]
    faces = remap[faces]
    diag["seconds"] = time.perf_counter() - t_all
    return verts, faces, diag


def build_mesh(verts_local: np.ndarray, faces: np.ndarray, volume, frame: EngineFrame, voxel_m):
    """국소 꼭짓점·삼각형 → 엔진 좌표 trimesh.Trimesh (꼭짓점 법선, COLOR_0 = 암석 색 + 재질 번호).

    겹친 조각의 같은 꼭짓점을 붙이고(merge_vertices), 법선은 삼각형 감김(동굴 빈 곳 쪽)에서
    면적 가중으로 구합니다. 색은 법선 반대(암석) 쪽 COLOR_PROBE_VOXELS 칸 점의 재질입니다.
    """
    import trimesh

    mesh = trimesh.Trimesh(
        vertices=frame.to_engine(verts_local), faces=faces, process=False, validate=False
    )
    mesh.merge_vertices()
    mesh.remove_unreferenced_vertices()
    normals = np.asarray(mesh.vertex_normals, dtype=np.float64)
    local = frame.to_local(np.asarray(mesh.vertices) - COLOR_PROBE_VOXELS * voxel_m * normals)
    mat = volume.evaluate(local)["solid_material"]
    rgba = np.empty((mat.size, 4), dtype=np.uint8)
    rgba[:, :3] = np.asarray(rk.COLOR_RGB)[np.minimum(mat, rk.N_ROCKS - 1)]
    rgba[:, 3] = mat
    mesh.visual.vertex_colors = rgba
    return mesh


def export_glb(mesh, path: str | os.PathLike) -> int:
    """trimesh 메시를 glb 로 씁니다 (NORMAL, COLOR_0 포함). 반환: 파일 크기 [바이트]."""
    import trimesh
    from trimesh.exchange.gltf import export_glb as _export

    scene = trimesh.Scene()
    scene.add_geometry(mesh, node_name="caves", geom_name="caves")
    data = _export(scene, include_normals=True)
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    tmp = p.with_name(p.name + ".part")
    tmp.write_bytes(data)
    os.replace(tmp, p)
    return len(data)
