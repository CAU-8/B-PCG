"""높이맵을 Godot 엔진이 바로 읽는 원시 파일 한 쌍으로 굽고, 다시 읽습니다.

쓰는 파일
- <stem>.bin: float32 리틀 엔디언 표본만 행 우선(row-major)으로 늘어놓은 파일입니다.
  머리글이 없으며 크기는 정확히 width * height * 4 바이트입니다.
- <stem>.json: 형식, 크기, 간격, 원점, 최솟값, 최댓값, 만든 bpcg 버전을 적은 설명 파일입니다.

좌표 약속 (Godot 국소 접평면 좌표, 단위 m)
- Godot 축: X = 동쪽, Y = 위, Z = 남쪽입니다 (오른손 좌표계, Godot 의 앞쪽 -Z 가 북쪽).
- 배열 z[row, col] 에서 열(col)이 늘면 동쪽(+X), 행(row)이 늘면 남쪽(+Z)으로 갑니다.
  0번 행이 북쪽 끝이므로 북쪽이 위인 래스터(GeoTIFF 등)를 뒤집지 않고 그대로 넣습니다.
- 표본 (row, col) 의 Godot 위치 = origin + (col * spacing_m, z[row, col], row * spacing_m).
  origin 은 0번 행, 0번 열(북서쪽 모서리) 표본을 높이 0 으로 놓았을 때의 국소 좌표입니다.

Godot 공식 빌드는 단정밀도(float32)라서 행성 중심 좌표를 그대로 넘기지 않습니다.
행성 위 어느 점을 국소 좌표의 원점으로 삼았는지는 묶음 manifest 에 따로 적습니다.
엔진 쪽 읽기 코드는 engine/scripts/heightmap_loader.gd 입니다.
"""

import json
import math
import os
from collections.abc import Sequence
from pathlib import Path
from typing import Any

import numpy as np

from bpcg import __version__

FORMAT = "float32_le"
LAYOUT = "row_major"
AXES = "x_east_y_up_z_south"

REQUIRED_KEYS = ("format", "width", "height", "spacing_m", "min", "max", "origin")


def stem_paths(path_stem: str | os.PathLike[str]) -> tuple[Path, Path]:
    """확장자를 뺀 경로(stem)에서 .bin 과 .json 경로를 만듭니다.

    'tile.v2' 처럼 점이 든 이름도 그대로 두고 뒤에 확장자를 붙입니다.
    """
    stem = Path(path_stem)
    return stem.with_name(stem.name + ".bin"), stem.with_name(stem.name + ".json")


def write_heightmap(
    path_stem: str | os.PathLike[str],
    z: Any,
    spacing_m: float,
    origin: Sequence[float] = (0.0, 0.0, 0.0),
) -> dict[str, Any]:
    """높이맵을 <stem>.bin 과 <stem>.json 으로 씁니다.

    Args:
        path_stem: 확장자를 뺀 출력 경로. 상위 폴더가 없으면 만듭니다.
        z: 2차원 높이 배열 (m). z[row, col], 행은 북에서 남(+Z), 열은 서에서 동(+X).
        spacing_m: 표본 사이 수평 간격 (m). 동서와 남북이 같습니다.
        origin: 북서쪽 모서리 표본의 Godot 국소 좌표 (x, y, z) (m).

    Returns:
        .json 에 쓴 설명 사전.

    Raises:
        ValueError: 2차원이 아니거나 한 변이 2 표본보다 작을 때, 숫자가 아닐 때,
            NaN 이나 무한대가 있을 때 (float32 로 바꾸며 넘친 값 포함), 간격이나 원점이 잘못됐을 때.
    """
    arr = np.asarray(z)
    if arr.ndim != 2:
        raise ValueError(f"높이맵은 2차원 배열이어야 합니다 (받은 차원: {arr.ndim})")
    rows, cols = arr.shape
    if rows < 2 or cols < 2:
        raise ValueError(f"높이맵은 한 변이 2 표본 이상이어야 합니다 (받은 크기: {arr.shape})")
    if not (np.issubdtype(arr.dtype, np.floating) or np.issubdtype(arr.dtype, np.integer)):
        raise ValueError(f"높이맵은 실수나 정수 배열이어야 합니다 (받은 형식: {arr.dtype})")

    spacing = float(spacing_m)
    if not (math.isfinite(spacing) and spacing > 0.0):
        raise ValueError(f"spacing_m 은 0 보다 큰 유한한 값이어야 합니다 (받은 값: {spacing_m})")

    origin_xyz = [float(v) for v in origin]
    if len(origin_xyz) != 3 or not all(math.isfinite(v) for v in origin_xyz):
        raise ValueError(f"origin 은 유한한 값 3개 (x, y, z) 여야 합니다 (받은 값: {origin})")

    with np.errstate(over="ignore", invalid="ignore"):
        data = np.ascontiguousarray(arr, dtype="<f4")
    bad = int(np.count_nonzero(~np.isfinite(data)))
    if bad:
        raise ValueError(
            f"높이맵에 NaN 이나 무한대 표본이 {bad}개 있습니다 "
            "(float32 범위를 넘는 값도 무한대가 됩니다). 굽기 전에 채우거나 잘라 내야 합니다."
        )

    meta: dict[str, Any] = {
        "format": FORMAT,
        "layout": LAYOUT,
        "axes": AXES,
        "width": int(cols),
        "height": int(rows),
        "spacing_m": spacing,
        "origin": origin_xyz,
        "min": float(data.min()),
        "max": float(data.max()),
        "bpcg_version": __version__,
    }

    bin_path, json_path = stem_paths(path_stem)
    bin_path.parent.mkdir(parents=True, exist_ok=True)
    _write_atomic(bin_path, data.tobytes(order="C"))
    # 엔진은 .json 을 보고 .bin 을 읽으므로 .json 을 나중에 씁니다.
    text = json.dumps(meta, ensure_ascii=False, indent=2) + "\n"
    _write_atomic(json_path, text.encode("utf-8"))
    return meta


def read_heightmap(path_stem: str | os.PathLike[str]) -> tuple[np.ndarray, dict[str, Any]]:
    """write_heightmap 이 쓴 파일 한 쌍을 읽어 (z, meta) 를 돌려줍니다.

    z 는 (height, width) 모양의 float32 배열 (m) 이고, meta 는 .json 내용입니다.

    Raises:
        FileNotFoundError: .bin 이나 .json 이 없을 때.
        ValueError: 형식이 다르거나 필드가 빠졌거나 .bin 크기가 맞지 않을 때.
    """
    bin_path, json_path = stem_paths(path_stem)
    meta = json.loads(json_path.read_text(encoding="utf-8"))
    missing = [k for k in REQUIRED_KEYS if k not in meta]
    if missing:
        raise ValueError(f"{json_path.name} 에 필드가 없습니다: {', '.join(missing)}")
    if meta["format"] != FORMAT:
        raise ValueError(
            f"지원하지 않는 형식입니다: {meta['format']!r} (읽을 수 있는 형식: {FORMAT})"
        )

    width, height = int(meta["width"]), int(meta["height"])
    data = np.fromfile(bin_path, dtype="<f4")
    if data.size != width * height:
        raise ValueError(
            f"{bin_path.name} 의 표본 수 {data.size} 가 "
            f"width * height = {width * height} 와 다릅니다"
        )
    z = data.reshape(height, width).astype(np.float32, copy=False)
    return z, meta


def _write_atomic(path: Path, payload: bytes) -> None:
    """임시 파일(.part)에 쓴 뒤 이름을 바꿔, 중간에 멈춰도 반쯤 쓴 파일이 남지 않게 합니다."""
    tmp = path.with_name(path.name + ".part")
    with open(tmp, "wb") as f:
        f.write(payload)
    os.replace(tmp, path)
