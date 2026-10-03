"""결과 묶음 manifest 읽기와 JSON 쓰기 (생성기는 C# 의 src/Bpcg/Bake/Bundle.cs).

스튜디오는 묶음 폴더(planet/, hero/)의 manifest.json 을 읽어 지도 탭을 만들고, job.json 같은
자기 파일을 씁니다. 형식은 docs/pipeline.md 의 묶음 절과 같습니다.
"""

import json
import math
import os
from pathlib import Path
from typing import Any

import numpy as np

FORMAT = "bpcg-bundle"
FORMAT_VERSION = 1
MANIFEST = "manifest.json"
JSON_ARRAY_MAX = 1_024  # 배열·리스트가 이보다 크면 JSON 에 넣지 않고 요약만 적습니다


def read_manifest(path: str | os.PathLike) -> dict:
    """묶음 폴더의 manifest.json 을 읽습니다. 형식이 다르면 ValueError."""
    p = Path(path) / MANIFEST
    if not p.exists():
        raise FileNotFoundError(f"묶음 manifest 가 없습니다: {p}")
    man = json.loads(p.read_text(encoding="utf-8"))
    if man.get("format") != FORMAT:
        raise ValueError(f"{p} 는 bpcg 묶음이 아닙니다 (format = {man.get('format')!r})")
    if int(man.get("format_version", -1)) > FORMAT_VERSION:
        raise ValueError(f"{p} 의 형식 버전 {man.get('format_version')} 을 읽을 수 없습니다")
    return man


def jsonable(obj: Any, max_array: int | None = JSON_ARRAY_MAX) -> Any:
    """JSON 으로 쓸 수 있는 값으로 바꿉니다.

    numpy 스칼라·배열은 파이썬 값·리스트로, NaN·inf 는 null 로 바꿉니다(JSON 표준이 아님).
    원소가 max_array 보다 많은 배열은 {"__array__": 모양, "dtype"} 요약만 남깁니다
    (리스트는 {"__list__": 길이}). max_array 가 None 이면 줄이지 않습니다.
    dataclass 처럼 __dict__ 가 있는 객체는 그 dict 로, 그 밖은 문자열로 바꿉니다.
    """
    if obj is None or isinstance(obj, bool | str):
        return obj
    if isinstance(obj, int | np.integer):
        return int(obj)
    if isinstance(obj, float | np.floating):
        v = float(obj)
        return v if math.isfinite(v) else None
    if isinstance(obj, np.bool_):
        return bool(obj)
    if isinstance(obj, np.ndarray):
        if max_array is not None and obj.size > max_array:
            return {"__array__": list(obj.shape), "dtype": str(obj.dtype)}
        return jsonable(obj.tolist(), max_array)
    if isinstance(obj, dict):
        return {str(k): jsonable(v, max_array) for k, v in obj.items()}
    if isinstance(obj, list | tuple):
        if max_array is not None and len(obj) > max_array:
            return {"__list__": len(obj)}
        return [jsonable(v, max_array) for v in obj]
    if isinstance(obj, Path):
        return str(obj)
    if hasattr(obj, "__dict__"):
        return jsonable(vars(obj), max_array)
    return str(obj)


def write_json(path: str | os.PathLike, obj: Any, max_array: int | None = JSON_ARRAY_MAX) -> None:
    """obj 를 jsonable 로 바꿔 들여쓰기 2 의 UTF-8 JSON 으로 씁니다(임시 파일 뒤 이름 바꾸기)."""
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(jsonable(obj, max_array), ensure_ascii=False, indent=2, allow_nan=False)
    tmp = p.with_name(p.name + ".part")
    tmp.write_text(text + "\n", encoding="utf-8")
    os.replace(tmp, p)
