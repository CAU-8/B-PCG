"""시험 도우미: C# 콘솔(src/Bpcg.Cli)을 돌리고 그 결과 묶음을 numpy 로 읽습니다.

생성기는 C# 입니다. Python 시험은 C# 콘솔이 쓴 결과 파일(묶음 manifest·필드 .npy·graph.npz·
회랑 .bin·지구본)을 읽어 불변식을 봅니다. 함수 하나하나를 손으로 만든 입력으로 보는 대조 시험은
tests/Bpcg.Tests (C#, golden 자료와 비교) 에 있습니다.
"""

import json
import os
import subprocess
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from bpcg_studio.csharp import cli_command, ensure_built
from bpcg_studio.paths import CONFIGS, ROOT

CLI_TIMEOUT_S = 900
R_EARTH_M = 6_371_000.0


def cli_env() -> dict[str, str]:
    env = os.environ.copy()
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    return env


def run_cli(*args: str, check: bool = True) -> subprocess.CompletedProcess:
    """C# 콘솔을 돌립니다 (빌드는 conftest 의 csharp_cli 가 한 번 함).

    check 면 종료 코드 0 을 봅니다.
    """
    proc = subprocess.run(
        cli_command([str(a) for a in args]),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=cli_env(),
        timeout=CLI_TIMEOUT_S,
        cwd=ROOT,
    )
    if check:
        tail = "\n".join((proc.stdout + proc.stderr).splitlines()[-30:])
        assert proc.returncode == 0, f"bpcg {' '.join(map(str, args))} 실패:\n{tail}"
    return proc


def build_cli() -> None:
    ensure_built()


# ---------------------------------------------------------------- 묶음 읽기
@dataclass
class Graph:
    """graph.npz: pos (N,3) [m], nbr (N,K) int32 (-1 = 없음), dist (N,K) [m], area (N,) [m²]."""

    kind: str
    shape: tuple[int, ...]
    radius_m: float | None
    spacing_m: float
    pos: np.ndarray
    nbr: np.ndarray
    dist: np.ndarray
    area: np.ndarray

    @property
    def n_cells(self) -> int:
        return int(self.area.shape[0])

    def boundary_mask(self) -> np.ndarray:
        """평면 격자의 가장자리 칸 (구면은 모두 False)."""
        if self.kind != "flat":
            return np.zeros(self.n_cells, dtype=bool)
        rows, cols = self.shape
        m = np.zeros((rows, cols), dtype=bool)
        m[0, :] = m[-1, :] = m[:, 0] = m[:, -1] = True
        return m.ravel()


@dataclass
class Bundle:
    """묶음 폴더 (planet/ 또는 hero/) 하나."""

    path: Path
    manifest: dict
    graph: Graph
    fields: dict[str, np.ndarray] = field(default_factory=dict)

    @property
    def meta(self) -> dict:
        return self.manifest["meta"]

    @property
    def diag(self) -> dict:
        return self.manifest["meta"]["diag"]

    @property
    def config(self) -> dict:
        return self.manifest["config"]

    def __getitem__(self, name: str) -> np.ndarray:
        return self.fields[name]

    def rivers(self) -> list[np.ndarray]:
        info = self.manifest.get("rivers")
        if not info:
            return []
        with np.load(self.path / info["file"], allow_pickle=False) as npz:
            cells, offsets = np.array(npz["cells"]), np.array(npz["offsets"])
        return [cells[offsets[i] : offsets[i + 1]] for i in range(len(offsets) - 1)]


def read_json(path: Path) -> dict:
    return json.loads(Path(path).read_text(encoding="utf-8"))


def load_bundle(path: Path) -> Bundle:
    path = Path(path)
    man = read_json(path / "manifest.json")
    assert man["format"] == "bpcg-bundle", man.get("format")
    g = man["graph"]
    with np.load(path / g["file"], allow_pickle=False) as npz:
        pos, nbr, dist, area = (np.array(npz[k]) for k in ("pos", "nbr", "dist", "area"))
    graph = Graph(
        kind=g["kind"],
        shape=tuple(int(v) for v in g["shape"]),
        radius_m=g["radius_m"],
        spacing_m=float(g["spacing_m"]),
        pos=pos,
        nbr=nbr,
        dist=dist,
        area=area,
    )
    fields = {}
    for entry in man["fields"]:
        arr = np.load(path / entry["file"], allow_pickle=False)
        assert str(arr.dtype) == entry["dtype"], (entry["name"], arr.dtype, entry["dtype"])
        assert list(arr.shape) == list(entry["shape"]), (entry["name"], arr.shape)
        fields[entry["name"]] = arr
    return Bundle(path, man, graph, fields)


# ---------------------------------------------------------------- 물길 도우미
def topo_order(receiver: np.ndarray) -> np.ndarray:
    """수신 셀이 먼저 오는 순서 (뿌리 → 잎). 순환이 있으면 AssertionError."""
    rcv = np.asarray(receiver, dtype=np.int64)
    n = rcv.size
    donors: list[list[int]] = [[] for _ in range(n)]
    for i in range(n):
        if rcv[i] != i:
            donors[rcv[i]].append(i)
    order = [i for i in range(n) if rcv[i] == i]
    k = 0
    while k < len(order):
        order.extend(donors[order[k]])
        k += 1
    assert len(order) == n, f"수신 셀에 순환이 있습니다 ({n - len(order)} 칸이 출구에 닿지 않음)"
    return np.asarray(order, dtype=np.int64)


def outlet_of(receiver: np.ndarray) -> np.ndarray:
    """칸마다 물이 결국 닿는 출구 칸."""
    rcv = np.asarray(receiver, dtype=np.int64)
    out = np.arange(rcv.size)
    for i in topo_order(rcv):
        out[i] = i if rcv[i] == i else out[rcv[i]]
    return out


def accumulate(receiver: np.ndarray, values: np.ndarray) -> np.ndarray:
    """하류로 더한 값 (잎 → 뿌리 순서로 수신 셀에 더함)."""
    rcv = np.asarray(receiver, dtype=np.int64)
    acc = np.asarray(values, dtype=np.float64).copy()
    for i in topo_order(rcv)[::-1]:
        if rcv[i] != i:
            acc[rcv[i]] += acc[i]
    return acc


# ---------------------------------------------------------------- 지층 기둥
def rock_at(bundle: Bundle, z: np.ndarray) -> np.ndarray:
    """칸마다 고도 z [m] 의 암석 번호 (C# Geology/Model.cs 의 RockAt 과 같은 규칙).

    strata_bottom_m (N, L) 은 층 바닥 고도(위에서 아래로 낮아짐), strata_rock (N, L+1) 은 층
    암석이고 마지막 하나가 기반암입니다. 층 번호 = 바닥이 처음으로 z 보다 낮은 층
    (없으면 L = 기반암).
    """
    bottoms = np.asarray(bundle["strata_bottom_m"], dtype=np.float64)
    rocks = np.asarray(bundle["strata_rock"])
    z = np.asarray(z, dtype=np.float64)
    layer = (bottoms >= z[:, None]).sum(axis=1)
    return rocks[np.arange(z.size), layer].astype(np.int64)


def load_config_dict(planet: str = "earth", profile: str = "tiny") -> dict:
    """configs 의 행성·프로필 TOML 을 합친 dict (스튜디오 설정 읽기와 같은 규칙)."""
    from bpcg_studio.config import load_config

    return load_config(planet, profile).as_dict()


__all__ = ["CONFIGS", "ROOT"]
