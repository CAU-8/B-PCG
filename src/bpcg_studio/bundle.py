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


# ---------------------------------------------------------------- 같은 실행인지 (C# RunFolder)
def bundle_id(man: dict | None) -> dict | None:
    """목차에서 그 묶음의 표지 {kind, config_digest, seed} 를 만듭니다 (히어로는 parent 도).

    히어로 목차에 parent 가 없으면(이 키를 적기 전에 만든 묶음) 어느 행성에서 만들었는지
    몰라 None 입니다. C# 의 RunFolder.BundleId 와 같은 규칙입니다.
    """
    if not man:
        return None
    kind = man.get("kind")
    out = {"kind": kind, "config_digest": man.get("config_digest"), "seed": man.get("seed")}
    if kind == "hero":
        if "parent" not in man:
            return None
        out["parent"] = man["parent"]
    return out


def describe_bundle(bid: Any) -> str:
    """표지를 글로: '시드 0, 설정 해시 1a2b3c4d5e6f' (히어로면 어느 행성에서 만들었는지도)."""
    if not isinstance(bid, dict):
        return "기록 없음"
    text = f"시드 {bid.get('seed')}, 설정 해시 {bid.get('config_digest')}"
    if "parent" not in bid:
        return text
    if bid["parent"] is None:
        return f"{text}, 행성 없이 만든 평면 히어로"
    return f"{text}, 행성 [{describe_bundle(bid['parent'])}]에서 만듦"


def run_mismatches(
    pm: dict | None,
    hm: dict | None,
    cm: dict | None,
    gm: dict | None,
    run_dir: str = "<실행 폴더>",
) -> list[str]:
    """실행 폴더 안의 묶음끼리 이어지지 않는 곳마다 글 하나 (무엇이 틀렸는지, 받은 값, 고치는 법).

    pm·hm·cm·gm 은 planet/·hero/·corridor/ 의 manifest.json 과 globe/globe.json 내용
    (없으면 None). 아래 묶음이 적은 parent(만들 때 읽은 위 묶음의 표지)를 같은 폴더의 위
    묶음과 비교합니다. parent 를 적기 전에 만든 묶음은 시드만 비교합니다.
    """
    out: list[str] = []
    pid = bundle_id(pm)
    hid = bundle_id(hm)
    redo_hero = (
        f"bpcg hero --from {run_dir}/planet 으로 히어로를 다시 만들거나 "
        "bpcg all 로 실행 전체를 다시 돌리세요."
    )
    redo_bake = f"bpcg bake --hero {run_dir}/hero 로 다시 구우세요."
    if pm and hm:
        if "parent" in hm:
            if hm["parent"] is None:
                out.append(
                    "히어로(hero/)는 행성 없이 만든 평면 히어로인데, 같은 폴더에 다른 실행의 "
                    f"행성(planet/, {describe_bundle(pid)})이 있습니다. "
                    "두 묶음을 함께 보면 안 됩니다. " + redo_hero
                )
            elif hm["parent"] != pid:
                out.append(
                    f"히어로(hero/)는 다른 행성 [{describe_bundle(hm['parent'])}]에서 "
                    f"만들었고, 같은 폴더의 행성(planet/)은 [{describe_bundle(pid)}]입니다. "
                    + redo_hero
                )
        elif hm.get("seed") != pm.get("seed"):
            out.append(
                f"히어로(hero/)의 시드 {hm.get('seed')} 가 같은 폴더의 행성(planet/) "
                f"시드 {pm.get('seed')} 와 다릅니다. 다른 실행의 묶음입니다. " + redo_hero
            )
    if hm and cm and "parent" in cm and cm["parent"] != hid:
        out.append(
            f"회랑(corridor/)은 다른 히어로 [{describe_bundle(cm['parent'])}]로 구웠고, "
            f"같은 폴더의 히어로(hero/)는 [{describe_bundle(hid)}]입니다. " + redo_bake
        )
    if pm and gm:
        if "parent" in gm:
            if gm["parent"] != pid:
                out.append(
                    f"지구본(globe/)은 다른 행성 [{describe_bundle(gm['parent'])}]으로 "
                    f"구웠고, 같은 폴더의 행성(planet/)은 [{describe_bundle(pid)}]입니다. "
                    + redo_bake
                )
        elif (gm.get("seed"), gm.get("config_digest")) != (pm.get("seed"), pm.get("config_digest")):
            out.append(
                f"지구본(globe/)의 시드·설정 해시({gm.get('seed')}, {gm.get('config_digest')})이 "
                f"같은 폴더의 행성(planet/)과 다릅니다({pm.get('seed')}, "
                f"{pm.get('config_digest')}). " + redo_bake
            )
    if hm and gm and "hero_bundle" in gm and gm["hero_bundle"] != hid:
        out.append(
            f"지구본(globe/)은 다른 히어로 [{describe_bundle(gm['hero_bundle'])}]와 함께 "
            "구워, 지구본에 찍힌 히어로 자리가 같은 폴더의 히어로"
            f"(hero/, [{describe_bundle(hid)}])와 다릅니다. " + redo_bake
        )
    return out
