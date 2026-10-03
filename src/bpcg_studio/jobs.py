"""스튜디오 실행: C# 콘솔 `all` 을 하위 프로세스로 돌리고, 실행 목록을 만듭니다.

실행 하나 = out/studio/<실행 id>/ 폴더 하나입니다.

    job.json   프로필·시드·덮어쓴 값·상태·단계별 시간·단계 결과 줄 (서버를 다시 켜도 남음)
    job.log    파이프라인과 그림 스크립트가 찍은 줄 전체
    planet/ hero/ corridor/ globe/   C# 콘솔 `all` 의 결과 (figures/ 는 그림 스크립트가 돌 때만)

명령: [dotnet, build, src/Bpcg.Cli, -c, Release …] (C# 콘솔을 바뀐 것만 빌드)
→ [dotnet, Bpcg.Cli.dll, all, --planet, --profile, --seed, --out, (--flat), --set 키=값 …].
결과 그림 스크립트(analysis/figures/render_results.py)는 아직 C# 결과를 읽게 고치지 않아
FIGURES_READY 가 False 이고, 그림 단계는 건너뜁니다.
기록 줄은 스레드에서 한 줄씩 읽어 progress.ProgressTracker 에 넣습니다. 한 번에 한 실행만 돕니다
(같은 서버 안에서는 잠금 하나로 검사와 등록을 함께 하고, 같은 out/ 을 보는 다른 스튜디오 서버가
돌리는 실행은 job.json 의 server_pid 로 알아봅니다).

실행 목록은 out/studio/* 와, out/* 중 planet/ 이나 hero/ 에 manifest.json 이 있는 폴더(읽기 전용,
예: out/earth_v2)를 함께 보여 줍니다. 바깥 폴더 이름은 한글·공백도 받고, 경로 구분자·':'·앞의
'.' 가 있는 이름은 열 수 없어 목록에서 빼고 skipped 로 알려 줍니다(names.run_folder_name).
"""

import collections
import json
import math
import os
import re
import subprocess
import sys
import threading
import time
from datetime import datetime
from pathlib import Path
from typing import Any

from bpcg_studio.bundle import write_json
from bpcg_studio.config import Config, checked_overrides, load_config
from bpcg_studio.csharp import CSharpError, build_command, cli_command
from bpcg_studio.names import run_folder_name
from bpcg_studio.params import planet_names, profile_names
from bpcg_studio.paths import OUT, ROOT
from bpcg_studio.progress import STAGES, ProgressTracker

STUDIO_DIRNAME = "studio"
JOB_FILE = "job.json"
LOG_FILE = "job.log"
FIGURE_SCRIPT = ROOT / "analysis" / "figures" / "render_results.py"
# 그림 스크립트가 C# 결과 묶음을 읽게 고쳐지면 True 로 바꿉니다 (지금은 Python 생성기를 import 함).
FIGURES_READY = False
FIGURES_SKIP_MSG = "결과 그림 스크립트는 아직 C# 결과를 읽게 고치지 않아 건너뜁니다"
COMMAND_NAMES = ("C# 빌드", "파이프라인", "그림 스크립트")
LOG_KEEP = 5_000  # 메모리에 남길 기록 줄 수 (파일에는 모두)
SAVE_EVERY_S = 2.0  # job.json 을 이 간격보다 자주 쓰지 않음 (단계가 바뀔 때는 바로)
CANCEL_WAIT_S = 5.0
SEED_MAX = 2**31 - 1
RUN_KEY = re.compile(r"^(?:studio/)?[A-Za-z0-9][A-Za-z0-9._\-]*$")  # 스튜디오 실행 id
ACTIVE = ("queued", "running")
SAVE_TRIES = 3  # job.json 바꿔 쓰기를 몇 번 해 볼지 (윈도우는 읽는 중인 파일을 못 바꿈)
INTERRUPTED_MSG = "스튜디오 서버가 꺼져서 실행이 멈췄습니다"


class JobError(Exception):
    """사용자에게 보여 줄 실행 오류. status 는 HTTP 상태 번호."""

    def __init__(self, message: str, status: int = 400, details: dict | None = None):
        super().__init__(message)
        self.status = status
        self.details = details or {}


def toml_literal(v: Any) -> str:
    """값 → `--set 키=값` 의 TOML 값 글자. nan·inf 는 checked_overrides 가 막으므로 ValueError."""
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, int):
        return str(v)
    if isinstance(v, float):
        if not math.isfinite(v):
            raise ValueError(f"nan·inf 는 --set 값으로 넘기지 않습니다: {v!r}")
        return repr(v)
    if isinstance(v, str):
        return json.dumps(v, ensure_ascii=False)
    if isinstance(v, list | tuple):
        return "[" + ", ".join(toml_literal(x) for x in v) + "]"
    raise ValueError(f"TOML 값으로 바꿀 수 없는 형식입니다: {type(v).__name__}")


def validate_request(req: dict) -> dict:
    """실행 요청을 검사해 정리한 dict 를 돌려줍니다. 틀리면 JobError(400, details=키별 오류).

    req: {planet, profile, seed, flat, figures, overrides: {점 경로: 값}}.
    """
    planet = str(req.get("planet") or "earth")
    profile = str(req.get("profile") or "laptop")
    if planet not in planet_names():
        raise JobError(f"행성 설정 '{planet}' 이 없습니다")
    if profile not in [p["name"] for p in profile_names()]:
        raise JobError(f"프로필 '{profile}' 이 없습니다")
    try:
        seed = int(req.get("seed", 0))
    except (TypeError, ValueError):
        raise JobError("시드는 정수여야 합니다", details={"seed": "정수여야 합니다"}) from None
    if not 0 <= seed <= SEED_MAX:
        raise JobError(f"시드는 0..{SEED_MAX} 이어야 합니다", details={"seed": "범위 밖"})
    overrides = req.get("overrides") or {}
    if not isinstance(overrides, dict):
        raise JobError("overrides 는 {키: 값} 이어야 합니다")
    base = load_config(planet, profile)
    errors: dict[str, str] = {}
    checked: dict[str, Any] = {}
    for key, value in overrides.items():
        if key == "planet.seed":
            errors[key] = "시드는 따로 고릅니다 (시드 칸)"
            continue
        try:
            checked.update(checked_overrides(base, {key: value}))
        except ValueError as e:
            errors[key] = str(e)
    if not errors:
        errors = domain_errors(base.with_overrides(checked), set(checked))
    if errors:
        raise JobError("바꾼 값에 문제가 있습니다", details=errors)
    # 기본값과 같은 값은 빼서 명령 줄과 기록을 짧게 둡니다.
    checked = {k: v for k, v in checked.items() if base[k] != v}
    return {
        "planet": planet,
        "profile": profile,
        "seed": seed,
        "flat": bool(req.get("flat", False)),
        "figures": bool(req.get("figures", True)),
        "overrides": checked,
    }


def domain_errors(cfg: Config, changed: set[str] | None = None) -> dict[str, str]:
    """형식은 맞지만 파이프라인이 받지 않는 영역 값 {키: 오류}.

    히어로 한 변·칸 간격(bpcg_studio.hero.hero_grid_size = C# Domain.HeroGridSize: 간격 > 0,
    한 변 ≥ 3·간격, 칸 수 상한)과 회랑 길이·폭·복셀 간격(> 0)을 실행 전에 봅니다.
    그대로 돌리면 행성 단계를 다 돈 뒤에야 히어로에서 멈추고, 그 오류가 '히어로 자리를
    찾지 못함' 으로 잘못 보입니다.
    changed: 사용자가 바꾼 키 (오류를 그 키에 붙임).
    """
    from bpcg_studio.hero import hero_grid_size  # 서버를 켤 때 hero 패키지를 읽지 않게

    changed = changed or set()
    errors: dict[str, str] = {}
    try:
        hero_grid_size(cfg)
    except ValueError as e:
        keys = [k for k in ("profile.hero.size_m", "profile.hero.spacing_m") if k in changed]
        for key in keys or ["profile.hero.size_m"]:
            errors[key] = str(e)
    cor = cfg.profile.get("corridor")
    for name in ("length_m", "width_m", "voxel_m"):
        v = cor.get(name) if cor is not None else None
        if v is None:
            continue
        if not (isinstance(v, int | float) and math.isfinite(float(v)) and float(v) > 0):
            errors[f"profile.corridor.{name}"] = (
                f"profile.corridor.{name} 은 0 보다 커야 합니다: {v!r}"
            )
    return errors


def cost_of(cfg: Config) -> dict[str, float]:
    """실행 시간을 정하는 크기: 행성 L0 칸 수, 히어로 칸 수, 회랑 표본 수(길이·폭/복셀³).

    지난 실행의 단계 시간을 무게로 쓸 때 이 값이 같은지 보고, 다르면 비율로 늘이거나 줄입니다.
    """
    from bpcg_studio.hero import hero_grid_size

    prof = cfg.profile
    grid = prof.get("grid")
    l0 = float(grid.get("l0_n_per_face", 0)) if grid is not None else 0.0
    try:
        n_hero = float(hero_grid_size(cfg)[0])
    except (ValueError, AttributeError):
        n_hero = 0.0
    cor = prof.get("corridor")
    samples = 0.0
    if cor is not None:
        try:
            v = float(cor.voxel_m)
            samples = float(cor.length_m) * float(cor.width_m) / v**3 if v > 0 else 0.0
        except (AttributeError, TypeError, ValueError):
            samples = 0.0
    return {"l0_cells": 6.0 * l0 * l0, "hero_cells": n_hero * n_hero, "corridor": samples}


# 단계 묶음 → 그 시간을 정하는 cost_of 의 크기 (다른 크기로 돈 지난 실행의 무게를 맞출 때)
_COST_OF_PHASE = {"행성": "l0_cells", "히어로": "hero_cells", "굽기": "corridor"}


def _cost_from_spec(spec: dict) -> dict | None:
    """예전 job.json (크기 기록 없음) 의 spec 에서 cost_of 를 다시 셉니다. 못 세면 None."""
    try:
        cfg = load_config(str(spec["planet"]), str(spec["profile"]))
        cfg = cfg.with_overrides(checked_overrides(cfg, dict(spec.get("overrides") or {})))
        return cost_of(cfg)
    except (KeyError, OSError, ValueError, TypeError):
        return None


def scale_weights(weights: dict[str, float], old: dict, new: dict) -> dict[str, float]:
    """지난 실행 단계 시간을 이번 크기에 맞춥니다 (단계 시간 ∝ 그 단계의 칸·표본 수)."""
    phase = {s.key: s.phase for s in STAGES}
    out = {}
    for key, w in weights.items():
        what = _COST_OF_PHASE.get(phase.get(key, ""))
        if key == "hero_find":
            what = "l0_cells"  # 자리 찾기는 행성 칸을 훑습니다
        a = float(old.get(what) or 0.0) if what else 0.0
        b = float(new.get(what) or 0.0) if what else 0.0
        out[key] = w * (b / a) if a > 0 and b > 0 else w
    return out


def pid_alive(pid: Any) -> bool:
    """프로세스 pid 가 살아 있으면 True. 윈도우는 OpenProcess 로 봅니다(os.kill 은 끝내 버림)."""
    if not isinstance(pid, int) or isinstance(pid, bool) or pid <= 0:
        return False
    if pid == os.getpid():
        return True
    if sys.platform == "win32":
        import ctypes

        kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
        handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return False
        try:
            code = ctypes.c_ulong()
            if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
                return False
            return code.value == 259  # STILL_ACTIVE
        finally:
            kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)  # 신호 0: 보내지 않고 있는지만 봄
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return False
    return True


def _now_iso(t: float | None = None) -> str:
    return datetime.fromtimestamp(time.time() if t is None else t).isoformat(timespec="seconds")


class Job:
    """실행 하나의 상태. 스레드에서 바뀌므로 읽고 쓸 때 lock 을 잡습니다."""

    def __init__(
        self,
        job_id: str,
        run_dir: Path,
        spec: dict,
        tracker: ProgressTracker,
        cost: dict | None = None,
    ):
        self.id = job_id
        self.run_dir = run_dir
        self.spec = spec
        self.cost = dict(cost or {})
        self.tracker = tracker
        self.status = "queued"
        self.created = time.time()
        self.started: float | None = None
        self.finished: float | None = None
        self.returncode: int | None = None
        self.error = ""
        self.commands: list[list[str]] = []
        self.log: collections.deque[str] = collections.deque(maxlen=LOG_KEEP)
        self.n_lines = 0
        self.proc: subprocess.Popen | None = None
        self.cancel_requested = False
        self.lock = threading.RLock()
        self._saved = 0.0
        self.save_error = ""

    @property
    def key(self) -> str:
        return f"{STUDIO_DIRNAME}/{self.id}"

    def add_line(self, line: str) -> bool:
        with self.lock:
            self.log.append(line)
            self.n_lines += 1
            return self.tracker.feed(line)

    def state(self, since: int | None = None) -> dict:
        """API 와 job.json 에 쓰는 상태 dict. since 를 주면 그 줄 번호 뒤의 기록만 넣습니다."""
        with self.lock:
            snap = self.tracker.snapshot()
            first = self.n_lines - len(self.log)
            if since is None:
                lines = list(self.log)[-200:]
                start = self.n_lines - len(lines)
            else:
                start = max(int(since), first)
                lines = list(self.log)[start - first :]
            return {
                "id": self.id,
                "run": self.key,
                "status": self.status,
                "spec": self.spec,
                "created": _now_iso(self.created),
                "started": None if self.started is None else _now_iso(self.started),
                "finished": None if self.finished is None else _now_iso(self.finished),
                "returncode": self.returncode,
                "error": self.error,
                "commands": [" ".join(_quote(a) for a in c) for c in self.commands],
                "progress": snap,
                "stage_seconds": self.tracker.stage_seconds(),
                "log": {"start": start, "lines": lines, "total": self.n_lines},
            }

    def save(self, force: bool = False) -> bool:
        """job.json 을 씁니다. 실패해도 예외를 내지 않고 False (다음 호출에서 다시 씀).

        윈도우는 다른 스레드(실행 목록 읽기)나 백신이 job.json 을 열고 있으면 바꿔 쓰지 못합니다.
        그 오류로 실행 스레드가 죽으면 하위 프로세스가 남거나 실행이 '도는 중' 으로 굳으므로,
        force 저장은 잠깐 쉬며 몇 번 더 해 보고, 그래도 안 되면 save_error 에 적고 넘어갑니다.
        """
        now = time.time()
        if not force and now - self._saved < SAVE_EVERY_S:
            return True
        st = self.state()
        st.pop("log", None)
        st["cost"] = self.cost
        st["server_pid"] = os.getpid()
        tries = SAVE_TRIES if force else 1
        for k in range(tries):
            try:
                write_json(self.run_dir / JOB_FILE, st)
            except OSError as e:
                self.save_error = f"{type(e).__name__}: {e}"
                if k + 1 < tries:
                    time.sleep(0.05 * (k + 1))
                continue
            self._saved = now
            self.save_error = ""
            return True
        return False


def _quote(a: str) -> str:
    return a if re.fullmatch(r"[A-Za-z0-9_./=:+\-]+", a) else json.dumps(a, ensure_ascii=False)


class JobManager:
    """실행을 시작·취소하고 목록을 만듭니다. 한 번에 한 실행만 돕니다.

    out_dir: 결과 폴더(기본 out/). python: 하위 프로세스 파이썬 (기본 지금 파이썬).
    """

    def __init__(self, out_dir: Path = OUT, python: str = sys.executable):
        self.out_dir = Path(out_dir)
        self.studio_dir = self.out_dir / STUDIO_DIRNAME
        self.python = python
        self.jobs: dict[str, Job] = {}
        self.lock = threading.Lock()
        self._mark_interrupted()

    # ------------------------------------------------------------ 시작·취소
    def active(self) -> Job | None:
        with self.lock:
            for job in self.jobs.values():
                if job.status in ACTIVE:
                    return job
        return None

    def _new_id(self, spec: dict) -> str:
        stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        base = f"{stamp}-{spec['profile']}-s{spec['seed']}" + ("-flat" if spec["flat"] else "")
        job_id, k = base, 2
        while (self.studio_dir / job_id).exists() or job_id in self.jobs:
            job_id, k = f"{base}-{k}", k + 1
        return job_id

    def _history_weights(self, spec: dict, cost: dict | None = None) -> dict[str, float] | None:
        """같은 프로필·평면 여부로 끝난 지난 실행의 단계 시간.

        크기(cost_of: L0·히어로 칸 수, 회랑 표본 수)까지 같은 실행이 있으면 가장 최근 것을 그대로,
        없으면 가장 최근 실행의 시간을 크기 비율로 늘이거나 줄여 씁니다. 예전 job.json 처럼
        크기 기록이 없으면 그 실행의 설정에서 다시 셉니다.
        """
        if not self.studio_dir.is_dir():
            return None
        found: list[tuple[float, dict, Path]] = []
        for d in self.studio_dir.iterdir():
            info = _read_json(d / JOB_FILE)
            if not info or info.get("status") != "done":
                continue
            s = info.get("spec") or {}
            if s.get("profile") != spec["profile"] or bool(s.get("flat")) != spec["flat"]:
                continue
            secs = info.get("stage_seconds") or {}
            if not secs:
                continue
            try:
                t = (d / JOB_FILE).stat().st_mtime
            except OSError:
                continue
            found.append((t, info, d))
        if not found:
            return None
        found.sort(key=lambda x: x[0], reverse=True)
        latest: tuple[dict, dict] | None = None
        for _, info, _d in found:
            secs = {k: float(v) for k, v in info["stage_seconds"].items()}
            if cost is None:
                return secs
            old = info.get("cost") or _cost_from_spec(info.get("spec") or {})
            if old is None:
                continue
            if all(float(old.get(k) or 0.0) == float(v) for k, v in cost.items()):
                return secs
            if latest is None:
                latest = (secs, old)
        if latest is None:
            return None
        return scale_weights(latest[0], latest[1], cost or {})

    def _foreign_active(self) -> dict | None:
        """같은 out/ 을 보는 다른 스튜디오 서버(살아 있는 pid)가 돌리는 실행의 job.json."""
        if not self.studio_dir.is_dir():
            return None
        me = os.getpid()
        for d in self.studio_dir.iterdir():
            if d.name in self.jobs:
                continue
            info = _read_json(d / JOB_FILE)
            if not info or info.get("status") not in ACTIVE:
                continue
            pid = info.get("server_pid")
            if pid != me and pid_alive(pid):
                return info
        return None

    def commands_for(self, spec: dict, run_dir: Path) -> list[list[str]]:
        try:
            build = build_command()
            cmd = cli_command(["all", "--planet", spec["planet"]])
        except CSharpError as e:
            raise JobError(str(e), status=500) from None
        cmd += ["--profile", spec["profile"], "--seed", str(spec["seed"]), "--out", str(run_dir)]
        if spec["flat"]:
            cmd.append("--flat")
        for key, value in spec["overrides"].items():
            cmd += ["--set", f"{key}={toml_literal(value)}"]
        out = [build, cmd]
        if spec["figures"] and not spec["flat"] and FIGURES_READY:
            out.append([self.python, "-u", str(FIGURE_SCRIPT), str(run_dir)])
        return out

    def start(self, request: dict) -> Job:
        """요청을 검사하고 실행을 시작합니다. 이미 도는 실행이 있으면 JobError(409).

        도는 실행 검사와 등록은 같은 잠금 안에서 해서, 거의 같은 때 온 두 요청이 둘 다 시작하지
        못하게 합니다. 준비(설정 읽기, 지난 실행 시간)는 잠금 밖에서 먼저 합니다.
        """
        spec = validate_request(request)
        if (cur := self.active()) is not None:
            raise JobError(f"이미 도는 실행이 있습니다: {cur.id}", status=409)
        if (other := self._foreign_active()) is not None:
            raise JobError(
                f"같은 out/ 을 쓰는 다른 스튜디오 서버(pid {other.get('server_pid')})가 실행 "
                f"{other.get('id')} 을 돌리고 있습니다. 그 서버 화면에서 보거나 끝난 뒤 다시 "
                "누르세요",
                status=409,
            )
        cfg = load_config(spec["planet"], spec["profile"]).with_overrides(spec["overrides"])
        cost = cost_of(cfg)
        hero = cfg.profile.get("hero")
        max_iter = {
            "landscape.max_flow_iterations": int(cfg.landscape.max_flow_iterations),
            "profile.hero.max_flow_iterations": int(
                hero.get("max_flow_iterations", cfg.landscape.max_flow_iterations)
                if hero is not None
                else cfg.landscape.max_flow_iterations
            ),
        }
        tracker = ProgressTracker(
            flat=spec["flat"],
            figures=spec["figures"],
            max_iter=max_iter,
            weights=self._history_weights(spec, cost),
        )
        if spec["flat"] and spec["figures"]:
            tracker.skip("figures", "평면 히어로는 행성 묶음이 없어 그림 스크립트를 건너뜁니다")
        elif spec["figures"] and not FIGURES_READY:
            tracker.skip("figures", FIGURES_SKIP_MSG)
        with self.lock:
            for other in self.jobs.values():  # active() 는 같은 잠금을 다시 잡으므로 여기서 직접 봄
                if other.status in ACTIVE:
                    raise JobError(f"이미 도는 실행이 있습니다: {other.id}", status=409)
            job_id = self._new_id(spec)
            run_dir = self.studio_dir / job_id
            run_dir.mkdir(parents=True, exist_ok=False)
            job = Job(job_id, run_dir, spec, tracker, cost=cost)
            job.commands = self.commands_for(spec, run_dir)
            self.jobs[job_id] = job
        job.save(force=True)  # 실패해도 실행 스레드가 다시 씁니다
        try:
            threading.Thread(
                target=self._run, args=(job,), name=f"job-{job_id}", daemon=True
            ).start()
        except RuntimeError as e:
            with self.lock:
                self.jobs.pop(job_id, None)
            raise JobError(f"실행 스레드를 시작하지 못했습니다: {e}", status=500) from None
        return job

    def cancel(self, job_id: str) -> Job:
        job = self.get(job_id)
        if job is None:
            raise JobError(f"실행 '{job_id}' 이 없습니다", status=404)
        with job.lock:
            if job.status not in ACTIVE:
                raise JobError("이미 끝난 실행입니다", status=409)
            job.cancel_requested = True
            proc = job.proc
        if proc is not None and proc.poll() is None:
            proc.terminate()
        return job

    def get(self, job_id: str) -> Job | None:
        with self.lock:
            return self.jobs.get(job_id)

    # ------------------------------------------------------------ 실행 스레드
    def _run(self, job: Job) -> None:
        """실행 스레드. 어떤 오류가 나도 마지막에 상태(끝남·실패·취소)를 적습니다."""
        env = os.environ.copy()
        env["PYTHONUNBUFFERED"] = "1"
        env["PYTHONIOENCODING"] = "utf-8"
        env["DOTNET_NOLOGO"] = "1"  # 처음 실행 안내문이 기록에 섞이지 않게
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        log_path = job.run_dir / LOG_FILE
        state, error = "done", ""
        try:
            with job.lock:
                job.status = "running"
                job.started = time.time()
                job.tracker.start(job.started)
            job.save(force=True)
            with open(log_path, "a", encoding="utf-8") as log_fh:
                for i, cmd in enumerate(job.commands):
                    head = "[스튜디오] 실행: " + " ".join(_quote(a) for a in cmd)
                    log_fh.write(head + "\n")
                    job.add_line(head)
                    rc = self._run_one(job, cmd, env, log_fh)
                    with job.lock:
                        job.returncode = rc
                        cancelled = job.cancel_requested
                    if cancelled:
                        state, error = "cancelled", "사용자가 취소했습니다"
                        break
                    if rc != 0:
                        what = COMMAND_NAMES[min(i, len(COMMAND_NAMES) - 1)]
                        tail = " / ".join(list(job.log)[-3:])
                        state, error = "failed", f"{what}이 종료 코드 {rc} 로 멈췄습니다: {tail}"
                        break
        except Exception as e:  # 실행 스레드가 죽으면 실행이 '도는 중' 으로 굳으므로 모두 받음
            state, error = "failed", f"실행하지 못했습니다: {type(e).__name__}: {e}"
        finally:
            with job.lock:
                job.finished = time.time()
                job.status = state
                job.error = error
                job.proc = None
                job.tracker.finish(state, job.finished, error)
            job.save(force=True)

    def _run_one(self, job: Job, cmd: list[str], env: dict, log_fh) -> int:
        kwargs: dict[str, Any] = {}
        if sys.platform == "win32":
            kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
        proc = subprocess.Popen(
            cmd,
            cwd=ROOT,
            env=env,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
            **kwargs,
        )
        try:
            with job.lock:
                job.proc = proc
                if job.cancel_requested:
                    proc.terminate()
            assert proc.stdout is not None
            for raw in proc.stdout:
                line = raw.rstrip("\r\n")
                log_fh.write(line + "\n")
                log_fh.flush()
                changed = job.add_line(line)
                job.save(force=changed)
            try:
                return proc.wait(timeout=CANCEL_WAIT_S if job.cancel_requested else None)
            except subprocess.TimeoutExpired:
                proc.kill()
                return proc.wait()
        finally:
            _stop(proc)

    # ------------------------------------------------------------ 다시 켤 때
    def _mark_interrupted(self) -> None:
        """서버가 꺼질 때 돌던 실행은 '멈춤(서버 꺼짐)' 으로 적어 둡니다.

        job.json 의 server_pid 가 살아 있는 다른 프로세스이면 다른 스튜디오 서버가 아직 돌리는
        실행이므로 건드리지 않습니다. 저장된 진행 상태(도는 단계, 남은 시간)도 함께 멈춤으로
        고칩니다.
        """
        if not self.studio_dir.is_dir():
            return
        me = os.getpid()
        for d in self.studio_dir.iterdir():
            path = d / JOB_FILE
            info = _read_json(path)
            if not info or info.get("status") not in ACTIVE:
                continue
            pid = info.get("server_pid")
            if pid != me and pid_alive(pid):
                continue
            try:
                finished = path.stat().st_mtime
            except OSError:
                finished = time.time()
            try:
                write_json(path, interrupted_info(info, finished))
            except OSError:
                pass  # 다음에 켤 때 다시 고칩니다

    # ------------------------------------------------------------ 기록 읽기
    def job_state(self, job_id: str, since: int | None = None) -> dict:
        """도는 실행이면 메모리 상태, 아니면 job.json + job.log 꼬리."""
        job = self.get(job_id)
        if job is not None:
            return job.state(since)
        run_dir = self.studio_dir / job_id
        if not RUN_KEY.match(job_id) or not (run_dir / JOB_FILE).exists():
            raise JobError(f"실행 '{job_id}' 이 없습니다", status=404)
        info = _read_json(run_dir / JOB_FILE) or {}
        lines = _tail(run_dir / LOG_FILE, 400)
        info["log"] = {"start": 0, "lines": lines, "total": len(lines), "tail_only": True}
        return info


def _stop(proc: subprocess.Popen) -> None:
    """아직 돌면 끄고(terminate → 기다림 → kill) 파이프를 닫습니다. 오류로 읽기가 멈췄을 때 씀."""
    if proc.poll() is None:
        proc.terminate()
        try:
            proc.wait(timeout=CANCEL_WAIT_S)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()
    if proc.stdout is not None:
        proc.stdout.close()


def interrupted_info(info: dict, finished: float) -> dict:
    """서버가 꺼져 멈춘 실행의 job.json: 상태·오류·끝난 때와 저장된 진행 상태를 멈춤으로 고침.

    진행 상태에 '도는 중' 단계, 지금 단계, 남은 시간이 남아 있으면 진행 탭이 끝난 실행을 아직
    도는 것처럼 보여 주므로, 도는 단계는 실패(서버 꺼짐)로, 지금 단계·남은 시간은 비웁니다.
    """
    out = dict(info)
    out["status"] = "interrupted"
    out["error"] = INTERRUPTED_MSG
    if not out.get("finished"):
        out["finished"] = _now_iso(finished)
    pr = dict(out.get("progress") or {})
    if pr:
        pr["state"] = "interrupted"
        pr["current"] = None
        pr["eta_s"] = None
        pr["eta_basis"] = ""
        stages = []
        for st in pr.get("stages") or []:
            st = dict(st)
            if st.get("status") == "running":
                st["status"] = "failed"
                st["note"] = INTERRUPTED_MSG
            stages.append(st)
        pr["stages"] = stages
        out["progress"] = pr
    return out


# ---------------------------------------------------------------- 실행 목록
def _read_json(path: Path) -> dict | None:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def _tail(path: Path, n: int) -> list[str]:
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            return list(collections.deque((ln.rstrip("\n") for ln in fh), maxlen=n))
    except OSError:
        return []


_MANIFEST_CACHE: dict[str, tuple[float, dict]] = {}


def manifest_head(path: Path) -> dict | None:
    """manifest.json 의 요약(kind, profile, seed, config_digest, graph, git_commit), mtime 캐시."""
    try:
        mtime = path.stat().st_mtime
    except OSError:
        return None
    key = str(path)
    hit = _MANIFEST_CACHE.get(key)
    if hit and hit[0] == mtime:
        return hit[1]
    man = _read_json(path)
    if man is None:
        return None
    keep = ("kind", "profile", "seed", "config_digest", "graph", "git_commit", "format")
    head = {k: man.get(k) for k in keep}
    _MANIFEST_CACHE[key] = (mtime, head)
    return head


def run_info(run_dir: Path, key: str, source: str) -> dict:
    """실행 폴더 하나의 요약 (목록 한 줄)."""
    job = _read_json(run_dir / JOB_FILE) if source == "studio" else None
    pm = manifest_head(run_dir / "planet" / "manifest.json")
    hm = manifest_head(run_dir / "hero" / "manifest.json")
    head = pm or hm or {}
    spec = (job or {}).get("spec") or {}
    figures = run_dir / "figures"
    try:
        created = run_dir.stat().st_mtime
    except OSError:
        created = 0.0
    return {
        "key": key,
        "name": run_dir.name,
        "path": str(run_dir),
        "source": source,
        "read_only": source != "studio",
        "status": (job or {}).get("status", "done" if (pm or hm) else "unknown"),
        "profile": spec.get("profile") or head.get("profile"),
        "seed": spec.get("seed", head.get("seed")),
        "flat": bool(spec.get("flat", pm is None and hm is not None)),
        "overrides": spec.get("overrides") or {},
        "created": _now_iso(created),
        "created_ts": created,
        "has": {
            "planet": pm is not None,
            "hero": hm is not None,
            "corridor": (run_dir / "corridor" / "manifest.json").exists(),
            "figures": figures.is_dir() and any(figures.glob("*.png")),
        },
    }


def _has_bundle(d: Path) -> bool:
    return (d / "planet" / "manifest.json").exists() or (d / "hero" / "manifest.json").exists()


def list_runs(out_dir: Path = OUT, skipped: list | None = None) -> list[dict]:
    """스튜디오 실행(out/studio/*)과 다른 실행 폴더(out/* 중 묶음이 있는 것, 읽기 전용).

    skipped 에 리스트를 주면, 묶음은 있지만 이름 때문에 열 수 없는 바깥 폴더를
    {name, reason} 으로 채웁니다 (resolve_run 이 받지 않는 이름).
    """
    out_dir = Path(out_dir)
    runs = []
    studio = out_dir / STUDIO_DIRNAME
    if studio.is_dir():
        for d in studio.iterdir():
            if not d.is_dir() or not RUN_KEY.match(d.name) or not (d / JOB_FILE).exists():
                continue
            runs.append(run_info(d, f"{STUDIO_DIRNAME}/{d.name}", "studio"))
    if out_dir.is_dir():
        for d in out_dir.iterdir():
            if not d.is_dir() or d.name == STUDIO_DIRNAME or d.name.startswith("."):
                continue
            if not _has_bundle(d):
                continue
            if not run_folder_name(d.name):
                if skipped is not None:
                    skipped.append({"name": d.name, "reason": BAD_FOLDER_MSG})
                continue
            runs.append(run_info(d, d.name, "external"))
    return sorted(runs, key=lambda r: r["created_ts"], reverse=True)


BAD_FOLDER_MSG = (
    "폴더 이름에 경로 문자('/', '\\', ':')나 앞뒤 공백이 있어 열 수 없습니다. 이름을 바꾸세요"
)


def resolve_run(key: str, out_dir: Path = OUT) -> Path:
    """실행 키 → 폴더. 바깥 경로나 없는 실행은 JobError.

    키는 'studio/<실행 id>' (영문·숫자·._-) 이거나 out/ 바로 아래 폴더 이름 하나입니다.
    바깥 폴더 이름은 한글·공백도 받고, 경로 구분자·':'·앞의 '.' 는 막습니다(names.run_folder_name).
    """
    if not isinstance(key, str):
        raise JobError(f"실행 이름이 올바르지 않습니다: {key!r}", status=400)
    if key.startswith(f"{STUDIO_DIRNAME}/"):
        ok = bool(RUN_KEY.match(key)) and ".." not in key
    else:
        ok = key != STUDIO_DIRNAME and run_folder_name(key)
    if not ok:
        raise JobError(f"실행 이름이 올바르지 않습니다: {key!r}", status=400)
    d = Path(out_dir) / key
    ok = d.is_dir() and ((d / JOB_FILE).exists() or _has_bundle(d))
    if not ok:
        raise JobError(f"실행 '{key}' 를 찾지 못했습니다", status=404)
    return d
