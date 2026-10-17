"""스튜디오의 '방법 비교' 화면을 위한 서버 쪽 (docs/compare.md 5장).

계산·그림은 C# 콘솔(`bpcg compare …`, src/Bpcg/Compare)이 하고, 여기서는 결과 파일을 읽고 콘솔을
부릅니다.

| 요청 | 하는 일 |
|---|---|
| GET /compare | 화면 (static/compare.html) |
| GET /api/compare/sets | 비교 묶음 목록 + 설정 파일 목록 + 도는 실행 |
| GET /api/compare/set?name= | 묶음을 시드끼리 묶어: 설정, 그룹(시드 → 방법들의 지표), 지표 설명 |
| GET /api/compare/methods | 등록된 방법과 매개변수 (`bpcg compare methods --json`) |
| GET /api/compare/process?name=&seed=&id= | 연산 과정: 단계 목록(범례 포함), 곡선, 막대 |
| GET /api/compare/elevation.png?name=&seed=&id=&scale= | 고도 그림 한 장 (색 범위 self·group) |
| GET /api/compare/stage.png?name=&seed=&id=&i= | 과정 단계 그림 한 장 |
| GET /api/compare/run?since= | 비교 실행 상태와 새 기록 줄 |
| POST /api/compare/run | 비교 실행 시작 {config, name, seeds, only, force, n, dx} (이미 돌면 409) |
| POST /api/compare/cancel | 비교 실행 멈춤 |

그림은 잠금으로 한 번에 한 장씩만 그립니다(`bpcg compare render` 를 차례로 부름). 한 번 받은 그림
경로는 기억해 두고, 파일이 남아 있고 고도·단계 파일보다 새것이면 콘솔을 다시 부르지 않습니다.
실행은 `bpcg compare run` 을 하위 프로세스로 돌리고 한 번에 하나만 받습니다.
"""

import json
import re
import subprocess
import sys
import threading
import time
from collections import deque
from pathlib import Path
from typing import Any

from bpcg_studio.csharp import CLI_DLL, CSharpError, build_command, cli_command, ensure_built
from bpcg_studio.jobs import JobError
from bpcg_studio.paths import CONFIGS, ROOT

NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_\-]{0,63}$")
ID_RE = re.compile(r"^[a-z0-9][a-z0-9_\-]{0,47}$")  # CompareRunner.IsValidId 와 같음
CONFIG_DIR = CONFIGS / "compare"
MAX_LINES = 4000
PROGRESS_RE = re.compile(r"\[진행\] (\d+)/(\d+)")
RENDER_TIMEOUT_S = 120


def _read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


# ---------------------------------------------------------------- 결과 파일 읽기
def list_sets(root: Path) -> list[dict]:
    """비교 묶음 목록 (compare.json 이 있는 폴더)."""
    out: list[dict] = []
    if not root.is_dir():
        return out
    for d in sorted(root.iterdir()):
        rec = _read_json(d / "compare.json") if d.is_dir() else None
        if not isinstance(rec, dict):
            continue
        out.append(
            {
                "name": d.name,
                "grid": rec.get("grid"),
                "seeds": rec.get("seeds_run", []),
                "methods": [m["id"] for m in rec.get("methods", [])],
                "updated": rec.get("updated"),
            }
        )
    return out


def load_set(set_dir: Path) -> dict:
    """묶음을 시드별로 묶어 읽습니다: {config, groups: [{seed, entries}], metrics}.

    CompareRunner.LoadSet 과 같은 규칙입니다: z.npy 와 meta.json 이 있는 결과만, 설정의 방법 순서로.
    """
    rec = _read_json(set_dir / "compare.json")
    if not isinstance(rec, dict):
        raise JobError(f"비교 묶음이 아닙니다 (compare.json 없음): {set_dir.name}", status=404)
    order = [m["id"] for m in rec.get("methods", [])]
    groups = []
    for sd in sorted(set_dir.glob("seed_*")):
        try:
            seed = int(sd.name.split("_", 1)[1])
        except ValueError:
            continue
        entries = []
        for d in sd.iterdir() if sd.is_dir() else []:
            meta = _read_json(d / "meta.json")
            if not isinstance(meta, dict) or not (d / "z.npy").is_file():
                continue
            metrics = _read_json(d / "metrics.json")
            meta["metrics"] = metrics.get("values", {}) if isinstance(metrics, dict) else {}
            entries.append(meta)
        entries.sort(
            key=lambda e: (order.index(e["id"]) if e["id"] in order else len(order), e["id"])
        )
        if entries:
            groups.append({"seed": seed, "entries": entries})
    return {"config": rec, "groups": groups, "metrics": rec.get("metric_defs", [])}


def load_process(entry: Path) -> dict:
    """연산 과정: {stages (범례 포함), series, bars}. 기록이 없으면 빈 목록."""
    stages = _read_json(entry / "stages" / "stages.json")
    trace = _read_json(entry / "trace.json")
    trace = trace if isinstance(trace, dict) else {}
    return {
        "stages": stages if isinstance(stages, list) else [],
        "series": trace.get("series", []),
        "bars": trace.get("bars", []),
    }


class CompareService:
    def __init__(self, root: Path):
        self.root = Path(root)
        self._lock = threading.Lock()
        self._render_lock = threading.Lock()  # 그림은 한 번에 한 장
        self._pngs: dict[tuple, Path] = {}
        self._methods: tuple[float, dict] | None = None
        self._proc: subprocess.Popen | None = None
        self._lines: deque[str] = deque(maxlen=MAX_LINES)
        self._first = 0  # deque 맨 앞 줄의 번호
        self._state: dict = {"status": "idle"}

    # ------------------------------------------------------------ 읽기
    def set_dir(self, name: str) -> Path:
        if not NAME_RE.match(name or ""):
            raise JobError(f"비교 묶음 이름이 올바르지 않습니다: {name!r}")
        d = self.root / name
        if not (d / "compare.json").is_file():
            raise JobError(f"비교 묶음이 없습니다: {name}", status=404)
        return d

    def sets(self) -> dict:
        configs = sorted(p.stem for p in CONFIG_DIR.glob("*.toml")) if CONFIG_DIR.is_dir() else []
        return {"sets": list_sets(self.root), "configs": configs, "run": self.run_state(None)}

    def set_data(self, name: str) -> dict:
        data = load_set(self.set_dir(name))
        data["name"] = name
        data["elevation_colors"] = data["config"].get("elevation_colors", [])
        return data

    def _console(self, args: list[str], timeout: float = RENDER_TIMEOUT_S) -> str:
        """C# 콘솔을 불러 표준 출력을 돌려줍니다. 실패하면 JobError."""
        try:
            if not CLI_DLL.is_file():
                ensure_built()
            cmd = cli_command(args)
        except CSharpError as e:
            raise JobError(str(e), status=500) from None
        proc = subprocess.run(
            cmd,
            cwd=ROOT,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
        )
        if proc.returncode != 0:
            msg = (proc.stderr or proc.stdout).strip().splitlines()
            status = 404 if proc.returncode == 1 else 400
            raise JobError(msg[-1] if msg else f"bpcg {args[0]} 실패", status=status)
        return proc.stdout

    def methods(self) -> dict:
        stamp = CLI_DLL.stat().st_mtime if CLI_DLL.is_file() else 0.0
        if self._methods is None or self._methods[0] != stamp:
            data = json.loads(self._console(["compare", "methods", "--json"]))
            self._methods = (CLI_DLL.stat().st_mtime, data)
        return self._methods[1]

    def _entry(self, name: str, seed: int, inst: str) -> Path:
        d = self.set_dir(name) / f"seed_{seed:04d}" / inst
        if not ID_RE.match(inst or "") or not (d / "z.npy").is_file():
            raise JobError(f"결과가 없습니다: 시드 {seed}, {inst}", status=404)
        return d

    def process(self, name: str, seed: int, inst: str) -> dict:
        return load_process(self._entry(name, seed, inst))

    def _png(self, key: tuple, source: Path, args: list[str]) -> bytes:
        """그림 한 장: 기억한 파일이 소스보다 새것이면 그대로, 아니면 콘솔로 그림 (잠금 안에서)."""
        with self._render_lock:
            p = self._pngs.get(key)
            fresh = p is not None and p.is_file() and p.stat().st_mtime >= source.stat().st_mtime
            if not fresh:
                p = Path(self._console(args).strip().splitlines()[-1])
                self._pngs[key] = p
            assert p is not None
            return p.read_bytes()

    def elevation_png(self, name: str, seed: int, inst: str, scale: str) -> bytes:
        if scale not in ("self", "group"):
            raise JobError(f"scale 은 self 또는 group 이어야 합니다: {scale!r}")
        d = self._entry(name, seed, inst)
        args = ["compare", "render", "--set", str(self.set_dir(name)), "--seed", str(seed)]
        args += ["--method", inst, "--scale", scale]
        if scale == "group":  # 같은 시드의 다른 결과가 바뀌면 색 범위도 바뀜
            groups = load_set(self.set_dir(name))["groups"]
            keys = [e["z_key"] for g in groups if g["seed"] == seed for e in g["entries"]]
            key: tuple = ("elev", str(d), scale, tuple(keys))
        else:
            key = ("elev", str(d), scale)
        return self._png(key, d / "z.npy", args)

    def stage_png(self, name: str, seed: int, inst: str, i: int) -> bytes:
        d = self._entry(name, seed, inst)
        src = d / "stages" / f"{i:02d}.npy"
        if i < 0 or not src.is_file():
            raise JobError(f"단계 그림을 만들 수 없습니다: {i} 번 단계가 없습니다", status=404)
        args = ["compare", "render", "--set", str(self.set_dir(name)), "--seed", str(seed)]
        args += ["--method", inst, "--stage", str(i)]
        return self._png(("stage", str(d), i), src, args)

    # ------------------------------------------------------------ 실행
    def start(self, body: dict) -> dict:
        config = str(body.get("config") or "default")
        if not NAME_RE.match(config) or not (CONFIG_DIR / f"{config}.toml").is_file():
            have = ", ".join(sorted(p.stem for p in CONFIG_DIR.glob("*.toml")))
            raise JobError(
                f"비교 설정이 없습니다: configs/compare/{config}.toml (있는 설정: {have})"
            )
        name = str(body.get("name") or config)
        if not NAME_RE.match(name):
            raise JobError(f"묶음 이름이 올바르지 않습니다: {name!r}")
        args = ["compare", "run", "--config", str(CONFIG_DIR / f"{config}.toml")]
        args += ["--out", str(self.root / name)]
        seeds = str(body.get("seeds") or "").replace(" ", "")
        if seeds:
            if not re.fullmatch(r"\d+(,\d+)*", seeds):
                raise JobError(f"시드는 쉼표로 나눈 정수여야 합니다: {seeds!r}")
            args += ["--seeds", seeds]
        only = body.get("only") or []
        if only:
            if not isinstance(only, list) or not all(
                isinstance(x, str) and ID_RE.match(x) for x in only
            ):
                raise JobError('only 는 방법 id 목록이어야 합니다 (예: ["fbm", "bpcg"])')
            args += ["--only", ",".join(only)]
        for key in ("n", "dx"):
            v = body.get(key)
            if v not in (None, ""):
                try:
                    num = float(v)
                except (TypeError, ValueError):
                    raise JobError(f"grid.{key} 는 숫자여야 합니다: {v!r}") from None
                args += ["--param", f"grid.{key}={int(num) if key == 'n' else num}"]
        if body.get("force"):
            args.append("--force")
        try:
            # 실행 전에 늘 빌드합니다(바뀐 것이 없으면 몇 초). 옛 dll 로 만든 결과가 섞이지 않게.
            cmds = [build_command(), cli_command(args)]
        except CSharpError as e:
            raise JobError(str(e), status=500) from None
        with self._lock:
            if self._proc is not None and self._proc.poll() is None:
                raise JobError(
                    "비교 실행이 이미 돌고 있습니다. 끝나거나 멈춘 뒤 다시 누르세요", status=409
                )
            if self._state.get("status") == "running":
                raise JobError(
                    "비교 실행이 이미 돌고 있습니다. 끝나거나 멈춘 뒤 다시 누르세요", status=409
                )
            self._lines.clear()
            self._first = 0
            self._state = {
                "status": "running",
                "name": name,
                "config": config,
                "started": time.time(),
                "progress": None,
            }
        threading.Thread(target=self._work, args=(cmds,), daemon=True).start()
        return self.run_state(None)

    def _add(self, line: str) -> None:
        with self._lock:
            if len(self._lines) == self._lines.maxlen:
                self._first += 1
            self._lines.append(line)
            m = PROGRESS_RE.search(line)
            if m:
                self._state["progress"] = [int(m.group(1)), int(m.group(2))]

    def _work(self, cmds: list[list[str]]) -> None:
        code = 0
        try:
            for k, cmd in enumerate(cmds):
                if k == 0:
                    self._add("[비교] C# 콘솔 빌드")
                with self._lock:
                    if self._state.get("status") != "running":
                        return
                    kwargs: dict[str, Any] = {}
                    if sys.platform == "win32":
                        kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
                    self._proc = subprocess.Popen(
                        cmd,
                        cwd=ROOT,
                        stdin=subprocess.DEVNULL,
                        stdout=subprocess.PIPE,
                        stderr=subprocess.STDOUT,
                        text=True,
                        encoding="utf-8",
                        errors="replace",
                        bufsize=1,
                        **kwargs,
                    )
                    proc = self._proc
                assert proc.stdout is not None
                for line in proc.stdout:
                    self._add(line.rstrip("\r\n"))
                code = proc.wait()
                if code != 0:
                    break
        except OSError as e:
            self._add(f"[비교] 실행하지 못했습니다: {e}")
            code = -1
        finally:
            with self._lock:
                if self._state.get("status") == "running":
                    self._state["status"] = "done" if code == 0 else "failed"
                self._state["returncode"] = code
                self._state["finished"] = time.time()

    def cancel(self) -> dict:
        with self._lock:
            if self._state.get("status") != "running":
                raise JobError("돌고 있는 비교 실행이 없습니다", status=409)
            self._state["status"] = "cancelled"
            if self._proc is not None and self._proc.poll() is None:
                self._proc.terminate()
        return self.run_state(None)

    def run_state(self, since: int | None) -> dict:
        with self._lock:
            st = dict(self._state)
            end = self._first + len(self._lines)
            start = self._first if since is None else max(int(since), self._first)
            lines = list(self._lines)[start - self._first :] if since is not None else []
            st["tail"] = list(self._lines)[-12:]
            st["lines"] = lines
            st["cursor"] = end
        return st
