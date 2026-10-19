"""설정 golden 사례(core/config 의 load_earth_*, overrides)를 지금 configs/ 로 다시 씁니다.

포팅 때 Python 생성기가 만든 설정 사례는 '설정 파일을 읽는 규칙'을 C# 과 견주는 자료입니다.
earth.toml 의 값이나 키가 바뀌면 읽는 규칙은 그대로여도 사례의 정답 글자가 달라집니다.
그때만 이 스크립트로 정답 글자(sorted_json, manifest_json, digest, typed)를 다시 씁니다.
덮어쓰기 입력·형 검사(overrides 의 checked)와 오류 사례는 설정 내용과 상관없어 그대로 둡니다.

    uv run python tests/golden/refresh_config.py          # 다시 쓰기
    uv run python tests/golden/refresh_config.py --check  # 지금 자료와 같은지 보기만 함

규칙은 Python 판 load_config 와 같습니다. 행성 TOML (+ configs/learned) 에 프로필 TOML 을
'profile' 절로 겹치고 profile.name 을 붙입니다.
digest 는 키를 정렬한 JSON 의 SHA-256 앞 12자리입니다.
"""

import copy
import hashlib
import json
import sys
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CASES = ROOT / "tests" / "golden" / "data" / "core" / "config"


def merge(base: dict, extra: dict) -> dict:
    out = copy.deepcopy(base)
    for k, v in extra.items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = merge(out[k], v)
        else:
            out[k] = copy.deepcopy(v)
    return out


def load_config(planet: str, profile: str) -> dict:
    data = tomllib.loads(
        (ROOT / "configs" / "planets" / f"{planet}.toml").read_text(encoding="utf-8")
    )
    learned = ROOT / "configs" / "learned" / f"{planet}.toml"
    if learned.exists():
        data = merge(data, tomllib.loads(learned.read_text(encoding="utf-8")))
    prof = tomllib.loads(
        (ROOT / "configs" / "profiles" / f"{profile}.toml").read_text(encoding="utf-8")
    )
    data = merge(data, {"profile": prof})
    data["profile"]["name"] = profile
    return data


def typed(v):
    if isinstance(v, bool):
        return ["bool", v]
    if isinstance(v, int):
        return ["int", str(v)]
    if isinstance(v, float):
        return ["float", repr(v)]
    if isinstance(v, str):
        return ["str", v]
    if isinstance(v, list):
        return ["list", [typed(x) for x in v]]
    if isinstance(v, dict):
        return ["dict", {k: typed(x) for k, x in v.items()}]
    raise TypeError(f"설정 값의 형을 모릅니다: {type(v).__name__} ({v!r})")


def untyped(t):
    kind, v = t
    if kind == "bool":
        return bool(v)
    if kind == "int":
        return int(v)
    if kind == "float":
        return float(v)
    if kind == "str":
        return v
    if kind == "list":
        return [untyped(x) for x in v]
    if kind == "dict":
        return {k: untyped(x) for k, x in v.items()}
    raise ValueError(f"golden 형 표기를 모릅니다: {kind}")


def with_overrides(cfg: dict, overrides: dict) -> dict:
    out = copy.deepcopy(cfg)
    for key, value in overrides.items():
        node = out
        parts = key.split(".")
        for p in parts[:-1]:
            node = node.setdefault(p, {})
        node[parts[-1]] = copy.deepcopy(value)
    return out


def sorted_json(cfg: dict) -> str:
    return json.dumps(cfg, sort_keys=True)


def digest(cfg: dict) -> str:
    return hashlib.sha256(sorted_json(cfg).encode("utf-8")).hexdigest()[:12]


def refresh() -> dict[str, dict]:
    out: dict[str, dict] = {}
    for profile in ("tiny", "laptop", "lab"):
        name = f"load_earth_{profile}.json"
        doc = json.loads((CASES / name).read_text(encoding="utf-8"))
        cfg = load_config(doc["meta"]["planet"], doc["meta"]["profile"])
        doc["meta"]["digest"] = digest(cfg)
        doc["meta"]["sorted_json"] = sorted_json(cfg)
        doc["meta"]["manifest_json"] = json.dumps(cfg, indent=2) + "\n"
        doc["meta"]["typed"] = typed(cfg)
        out[name] = doc
    doc = json.loads((CASES / "overrides.json").read_text(encoding="utf-8"))
    planet, profile = doc["meta"]["base"].split("/")
    base = load_config(planet, profile)
    for case in doc["meta"]["cases"]:
        applied = untyped(case["checked"] if case["checked"] is not None else case["overrides"])
        cfg = with_overrides(base, applied)
        case["digest"] = digest(cfg)
        case["sorted_json"] = sorted_json(cfg)
        if "manifest_json" in case:
            case["manifest_json"] = json.dumps(cfg, indent=2) + "\n"
    out["overrides.json"] = doc
    return out


def main() -> int:
    check = "--check" in sys.argv[1:]
    changed = []
    for name, doc in refresh().items():
        path = CASES / name
        text = json.dumps(doc, indent=2) + "\n"
        old = json.loads(path.read_text(encoding="utf-8"))
        if old != doc:
            changed.append(name)
            if not check:
                path.write_text(text, encoding="utf-8")
    verb = "다른" if check else "다시 쓴"
    names = ", ".join(changed) if changed else "없음"
    print(f"[golden] 설정 사례 {verb} 파일 {len(changed)}개: {names}")
    return 1 if check and changed else 0


if __name__ == "__main__":
    raise SystemExit(main())
