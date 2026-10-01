"""설정 읽기: configs/planets/<행성>.toml + configs/profiles/<프로필>.toml.

쓰는 법
    cfg = load_config("earth", "laptop")
    cfg.landscape.theta          # 0.45
    cfg["landscape.theta"]       # 같은 값
    cfg.with_overrides({"landscape.theta": 0.5})

configs/learned/<행성>.toml 이 있으면 같은 키를 덮어씁니다(데이터로 맞춘 값, 설계도 6장).
"""

import copy
import hashlib
import json
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
