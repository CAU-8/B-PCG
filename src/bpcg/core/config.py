"""설정 읽기: configs/planets/<행성>.toml + configs/profiles/<프로필>.toml.

쓰는 법
    cfg = load_config("earth", "laptop")
    cfg.landscape.theta          # 0.45
    cfg["landscape.theta"]       # 같은 값
    cfg.with_overrides({"landscape.theta": 0.5})

configs/learned/<행성>.toml 이 있으면 같은 키를 덮어씁니다(데이터로 맞춘 값, 설계도 6장).

명령줄 `--set 키=값` 과 스튜디오는 parse_assignment 로 값을 읽고 checked_overrides 로 키와
형식을 검사한 뒤 덮어씁니다. with_overrides 는 없는 키도 새로 만들기 때문에(오타가 조용히
무시됨) 바깥에서 받은 값은 반드시 checked_overrides 를 거칩니다. 실수 자리에 nan·inf 는 받지
않고(묶음 manifest 에 null 로 적혀 다음 단계가 깨짐), 행성·프로필 이름(planet.name,
profile.name)은 --planet·--profile 로만 고릅니다.
"""

import copy
import difflib
import hashlib
import json
import math
import tomllib
from pathlib import Path
from typing import Any

from bpcg.core.paths import CONFIGS


class Section:
    """점(.)으로 읽는 설정 묶음. 값은 바꾸지 않습니다."""

    def __init__(self, data: dict[str, Any]):
        object.__setattr__(self, "_data", data)

    def __getattr__(self, key: str) -> Any:
        try:
            v = self._data[key]
        except KeyError as e:
            raise AttributeError(f"설정에 '{key}' 가 없습니다") from e
        return Section(v) if isinstance(v, dict) else v

    def __setattr__(self, key: str, value: Any) -> None:
        raise AttributeError("설정은 바꿀 수 없습니다. with_overrides() 를 쓰세요")

    def __contains__(self, key: str) -> bool:
        return key in self._data

    def get(self, key: str, default: Any = None) -> Any:
        v = self._data.get(key, default)
        return Section(v) if isinstance(v, dict) else v

    def as_dict(self) -> dict[str, Any]:
        return copy.deepcopy(self._data)

    def __repr__(self) -> str:
        return f"Section({self._data!r})"


class Config(Section):
    """행성 설정 + 프로필. cfg["a.b"] 로 점 경로를 읽을 수 있습니다."""

    def __getitem__(self, dotted: str) -> Any:
        node: Any = self._data
        for part in dotted.split("."):
            node = node[part]
        return Section(node) if isinstance(node, dict) else node

    def with_overrides(self, overrides: dict[str, Any]) -> "Config":
        data = self.as_dict()
        for dotted, value in overrides.items():
            node = data
            parts = dotted.split(".")
            for part in parts[:-1]:
                node = node.setdefault(part, {})
            node[parts[-1]] = value
        return Config(data)

    def digest(self) -> str:
        """설정 내용의 짧은 해시 (결과 manifest 에 기록)."""
        raw = json.dumps(self._data, sort_keys=True, ensure_ascii=False).encode("utf-8")
        return hashlib.sha256(raw).hexdigest()[:12]


def _merge(base: dict[str, Any], extra: dict[str, Any]) -> dict[str, Any]:
    out = copy.deepcopy(base)
    for k, v in extra.items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = _merge(out[k], v)
        else:
            out[k] = copy.deepcopy(v)
    return out


def _read(path: Path) -> dict[str, Any]:
    with open(path, "rb") as fh:
        return tomllib.load(fh)


def load_config(
    planet: str | Path = "earth",
    profile: str | Path = "laptop",
    overrides: dict[str, Any] | None = None,
) -> Config:
    """행성 설정과 프로필을 합칩니다. 이름 대신 파일 경로를 줘도 됩니다."""
    planet_path = (
        Path(planet) if str(planet).endswith(".toml") else CONFIGS / "planets" / f"{planet}.toml"
    )
    profile_path = (
        Path(profile)
        if str(profile).endswith(".toml")
        else CONFIGS / "profiles" / f"{profile}.toml"
    )
    data = _read(planet_path)
    learned = CONFIGS / "learned" / planet_path.name
    if learned.exists():
        data = _merge(data, _read(learned))
    data = _merge(data, {"profile": _read(profile_path)})
    data["profile"]["name"] = profile_path.stem
    cfg = Config(data)
    return cfg.with_overrides(overrides) if overrides else cfg


# ---------------------------------------------------------------- 바깥에서 받은 덮어쓰기
class BareText(str):
    """TOML 값으로 읽히지 않은 글자 (따옴표 없는 글자). 기존 값이 글자일 때만 받습니다."""


def parse_assignment(text: str) -> tuple[str, Any]:
    """'키=값' 하나를 (점 경로, 값) 으로 바꿉니다. 값은 TOML 값 문법으로 읽습니다.

    예: 'landscape.theta=0.5' → ('landscape.theta', 0.5), 'fans.enabled=false' → False,
    'plates.speed_m_per_yr=[0.02, 0.07]' → 리스트, 'k="글자"' → '글자'.
    TOML 로 읽히지 않는 값(따옴표 없는 글자 등)은 BareText 로 돌려주고, checked_overrides 가
    기존 값이 글자일 때만 받습니다. '=' 가 없거나 키가 비면 ValueError.
    """
    key, sep, raw = str(text).partition("=")
    key = key.strip()
    raw = raw.strip()
    if not sep or not key or not raw:
        raise ValueError(f"'키=값' 꼴이어야 합니다: {text!r} (예: landscape.theta=0.5)")
    try:
        value = tomllib.loads(f"v = {raw}")["v"]
    except tomllib.TOMLDecodeError:
        value = BareText(raw)
    return key, value


def _kind(v: Any) -> str:
    if isinstance(v, bool):
        return "참·거짓(true/false)"
    if isinstance(v, int):
        return "정수"
    if isinstance(v, float):
        return "실수"
    if isinstance(v, str):
        return "글자"
    if isinstance(v, list):
        return "리스트"
    return type(v).__name__


def _finite(key: str, v: float) -> float:
    if not math.isfinite(v):
        raise ValueError(f"'{key}' 는 유한한 실수여야 합니다 (nan·inf 는 받지 않습니다): {v!r}")
    return v


def _coerce(key: str, old: Any, new: Any) -> Any:
    """new 를 old 와 같은 형식으로 맞춥니다. 맞출 수 없으면 ValueError (nan·inf 실수 포함)."""
    if isinstance(new, BareText) and not isinstance(old, str):
        raise ValueError(
            f"'{key}' 값 {str(new)!r} 를 읽지 못했습니다. {_kind(old)} 값을 TOML 문법으로 주세요 "
            f"(지금 값: {old!r})"
        )
    if isinstance(old, bool):
        if isinstance(new, bool):
            return new
    elif isinstance(old, int):
        if isinstance(new, int) and not isinstance(new, bool):
            return new
        if isinstance(new, float) and math.isfinite(new) and new == round(new):
            return int(new)
    elif isinstance(old, float):
        if isinstance(new, int | float) and not isinstance(new, bool):
            return _finite(key, float(new))
    elif isinstance(old, str):
        if isinstance(new, str):
            return str(new)
    elif isinstance(old, list):
        if isinstance(new, list):
            if old and len(new) != len(old):
                raise ValueError(f"'{key}' 는 원소 {len(old)}개 리스트여야 합니다: {new!r}")
            if not old:
                for i, v in enumerate(new):
                    if isinstance(v, float):
                        _finite(f"{key}[{i}]", v)
                return list(new)
            pairs = enumerate(zip(old, new, strict=True))
            return [_coerce(f"{key}[{i}]", o, v) for i, (o, v) in pairs]
    raise ValueError(f"'{key}' 는 {_kind(old)} 값이어야 합니다: {new!r} (지금 값: {old!r})")


def _all_keys(data: dict, prefix: str = "") -> list[str]:
    out = []
    for k, v in data.items():
        dotted = f"{prefix}{k}"
        out += _all_keys(v, dotted + ".") if isinstance(v, dict) else [dotted]
    return out


# 덮어쓸 수 없는 키 → 대신 고르는 곳. 이름을 바꾸면 manifest 가 없는 설정 파일을 가리켜
# 기본값 비교(스튜디오 '바꾼 설정')가 깨집니다. planet.seed 는 --seed 가 이 경로로 넣으므로
# 받습니다.
FIXED_KEYS: dict[str, str] = {
    "planet.name": "행성은 --planet (스튜디오는 '행성 설정' 칸)으로 고르세요",
    "profile.name": "프로필은 --profile (스튜디오는 '프로필' 칸)으로 고르세요",
}


def checked_overrides(cfg: Config, overrides: dict[str, Any]) -> dict[str, Any]:
    """바깥에서 받은 덮어쓰기를 설정의 기존 키·형식과 맞춰 봅니다. 반환: 형식을 맞춘 새 dict.

    - 키는 설정에 이미 있는 값이어야 합니다(오타로 새 키가 생기지 않게). 절(dict)은 못 바꿉니다.
    - planet.name, profile.name 은 바꿀 수 없습니다 (FIXED_KEYS).
    - 실수 자리의 정수는 실수로, 정수 자리의 2.0 같은 값은 정수로 바꿉니다.
    - 실수 자리에 nan·inf 는 받지 않습니다 (리스트 원소도).
    - 참·거짓, 글자, 리스트(길이·원소 형식)도 기존 값과 같아야 합니다.
    틀리면 ValueError 이고, 메시지에 키 이름·기대 형식·비슷한 키를 적습니다.
    """
    data = cfg.as_dict()
    out: dict[str, Any] = {}
    for dotted, value in overrides.items():
        if dotted in FIXED_KEYS:
            raise ValueError(f"'{dotted}' 는 덮어쓸 수 없습니다. {FIXED_KEYS[dotted]}")
        node: Any = data
        for part in dotted.split("."):
            if not isinstance(node, dict) or part not in node:
                close = difflib.get_close_matches(dotted, _all_keys(data), n=3, cutoff=0.6)
                hint = f" 비슷한 키: {', '.join(close)}" if close else ""
                raise ValueError(f"설정에 '{dotted}' 키가 없습니다.{hint}")
            node = node[part]
        if isinstance(node, dict):
            raise ValueError(f"'{dotted}' 는 절(section)이라 통째로 바꿀 수 없습니다")
        out[dotted] = _coerce(dotted, node, value)
    return out
