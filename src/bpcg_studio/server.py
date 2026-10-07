"""스튜디오 HTTP 서버: 127.0.0.1 에서만 듣는 JSON API + static/ 화면 파일.

`uv run bpcg studio [--port 8765] [--no-browser]` 로 켭니다. 표준 라이브러리 http.server 의
ThreadingHTTPServer 만 쓰고, 화면(static/)도 바깥 자원(CDN·글꼴) 없이 돕니다.

| 요청 | 하는 일 |
|---|---|
| GET /api/meta | 행성·프로필, 도는 실행, Godot 상태, 히어로 한 변 칸 수 상한·반올림 규칙 |
| GET /api/params?planet=&profile= | 매개변수 표 (params.build_schema) |
| POST /api/validate | 바꾼 값 검사 {planet, profile, overrides} → 키별 오류 (히어로·회랑 포함) |
| GET /api/runs | 실행 목록 (jobs.list_runs) + 이름 때문에 열 수 없는 폴더(skipped) |
| GET /api/run?run= | 실행 개요 (summary.run_summary: diff, diff_missing, config_flat, profile …) |
| POST /api/jobs | 실행 시작 {planet, profile, seed, flat, figures, overrides} (이미 돌면 409) |
| GET /api/job?id=&since= | 실행 상태·진행률·새 기록 줄 |
| POST /api/jobs/cancel | 실행 취소 {id} |
| GET /api/level?run=&level= | 지도 격자·필드 목록·겹칠 것 + version (결과가 바뀌면 바뀌는 글자) |
| GET /api/field?run=&level=&name=&v= | 필드 설명·통계(positive_min 늘 있음)·추천 색 범위 |
| GET /api/field.bin?run=&level=&name=&v= | 지도 격자 float32 (행 우선, 북쪽이 0번 행) |
| GET /api/probe?run=&level=&px=&py= | 한 칸의 모든 필드 값 |
| GET /api/figure?run=&name= | 그림 PNG |
| GET /api/godot | Godot 상태 (찾은 경로, engine/baked 에 있는 실행) |
| POST /api/godot/launch | 회랑 복사 → 가져오기 → 실행 {run, mode: play|editor} |
| /compare, /api/compare/* | 방법 비교 화면 (compare_api.py 의 표) |

v= 는 브라우저 캐시를 가르는 값이라 서버는 읽지 않습니다(늘 지금 파일을 보냄).

다른 웹 페이지가 이 서버로 실행을 시작하지 못하게:
- Host 머리글이 127.0.0.1/localhost 가 아니면 막습니다 (DNS 리바인딩).
- POST 는 Content-Type: application/json 만 받고, Origin 머리글이 있으면 이 서버 주소여야 합니다
  (Sec-Fetch-Site: cross-site 도 막음). 본문 길이(Content-Length)가 없거나 음수·글자이면 400.
- 모든 응답에 X-Frame-Options: DENY 와 CSP frame-ancestors 'none' 을 붙여, 다른 페이지가 화면을
  iframe 으로 덮어 '실행'·'Godot로 보기' 를 누르게 하는 것(클릭재킹)을 막습니다.
- 윈도우에서는 SO_REUSEADDR 대신 SO_EXCLUSIVEADDRUSE 로 열어, 같은 포트에 두 번째 스튜디오가
  조용히 붙지 못하게 합니다.
브라우저가 먼저 연결을 끊은 것(ConnectionResetError 등)은 한 줄만 찍고, 진짜 오류만 traceback 을
찍습니다.
"""

import json
import math
import socket
import sys
import threading
import traceback
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, urlparse

import numpy as np

from bpcg_studio import __version__
from bpcg_studio import godot as gd
from bpcg_studio.compare_api import CompareService
from bpcg_studio.config import load_config
from bpcg_studio.jobs import JobError, JobManager, list_runs, resolve_run, validate_request
from bpcg_studio.params import build_schema, planet_names, profile_names
from bpcg_studio.paths import OUT
from bpcg_studio.summary import figure_path, run_summary
from bpcg_studio.views import ViewError, ViewStore

STATIC = Path(__file__).with_name("static")
# 화면 파일 종류. mimetypes 는 윈도우 레지스트리에 따라 .js 를 text/plain 으로 줄 수 있어
# (nosniff 와 만나면 스크립트가 막힘) 여기서 정합니다.
STATIC_TYPES = {
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".json": "application/json; charset=utf-8",
    ".png": "image/png",
    ".gif": "image/gif",
    ".svg": "image/svg+xml",
    ".ico": "image/x-icon",
    ".webp": "image/webp",
}
MAX_BODY = 1_000_000
HOST = "127.0.0.1"
SECURITY_HEADERS = {
    "X-Content-Type-Options": "nosniff",
    "X-Frame-Options": "DENY",
    "Content-Security-Policy": "frame-ancestors 'none'",
}


def _reject_constant(name: str) -> Any:
    """json.loads 의 NaN·Infinity 글자를 막습니다 (표준 JSON 이 아니고 설정에 넣을 수 없음)."""
    raise JobError(f"JSON 에 {name} 는 쓸 수 없습니다 (유한한 숫자만 받습니다)")


class Studio:
    """서버가 들고 있는 상태: 실행 관리, 지도 캐시, Godot 단계."""

    def __init__(self, out_dir: Path = OUT, baked_dir: Path = gd.BAKED):
        self.out_dir = Path(out_dir)
        self.jobs = JobManager(self.out_dir)
        self.views = ViewStore(self.out_dir / "studio" / ".cache")
        self.godot = gd.GodotRunner(baked_dir=baked_dir, log_dir=self.out_dir)
        self.compare = CompareService(self.out_dir / "compare")

    def run_dir(self, q: dict) -> Path:
        return resolve_run(_one(q, "run"), self.out_dir)


def clean_json(obj: Any) -> Any:
    """JSON 으로 보낼 수 있게 바꿉니다: numpy 값은 파이썬 값으로, NaN·inf 는 null 로."""
    if isinstance(obj, dict):
        return {str(k): clean_json(v) for k, v in obj.items()}
    if isinstance(obj, list | tuple):
        return [clean_json(v) for v in obj]
    if isinstance(obj, np.ndarray):
        return clean_json(obj.tolist())
    if isinstance(obj, bool | np.bool_):
        return bool(obj)
    if isinstance(obj, int | np.integer):
        return int(obj)
    if isinstance(obj, float | np.floating):
        v = float(obj)
        return v if math.isfinite(v) else None
    if isinstance(obj, Path):
        return str(obj)
    return obj


def _one(q: dict, key: str, default: str | None = None) -> str:
    v = q.get(key)
    if not v:
        if default is not None:
            return default
        raise JobError(f"'{key}' 값이 필요합니다")
    return v[0]


def _int(q: dict, key: str) -> int:
    try:
        return int(_one(q, key))
    except ValueError:
        raise JobError(f"'{key}' 는 정수여야 합니다") from None


def make_handler(app: Studio, port: int) -> type[BaseHTTPRequestHandler]:
    allowed_hosts = {f"{HOST}:{port}", f"localhost:{port}", HOST, "localhost"}
    allowed_origins = {f"http://{HOST}:{port}", f"http://localhost:{port}"}

    class Handler(BaseHTTPRequestHandler):
        server_version = f"bpcg-studio/{__version__}"
        protocol_version = "HTTP/1.1"

        def log_message(self, format: str, *args: Any) -> None:
            if args and str(args[1] if len(args) > 1 else "").startswith(("4", "5")):
                sys.stderr.write("[스튜디오] " + (format % args) + "\n")

        # ------------------------------------------------------------ 보내기
        def _send(self, status: int, body: bytes, ctype: str, extra: dict | None = None) -> None:
            self.send_response(status)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            for k, v in SECURITY_HEADERS.items():
                self.send_header(k, v)
            for k, v in (extra or {}).items():
                self.send_header(k, v)
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(body)

        def _json(self, obj: Any, status: int = 200) -> None:
            body = json.dumps(clean_json(obj), ensure_ascii=False, allow_nan=False)
            self._send(status, body.encode("utf-8"), "application/json; charset=utf-8")

        def _error(self, status: int, message: str, details: dict | None = None) -> None:
            self._json({"error": message, "details": details or {}}, status)

        def _host_ok(self) -> bool:
            host = (self.headers.get("Host") or "").strip().lower()
            if host not in allowed_hosts:
                self._error(403, "이 서버는 127.0.0.1 주소로만 씁니다")
                return False
            return True

        def _origin_ok(self) -> bool:
            """POST 가 이 화면에서 왔는지: Origin 이 있으면 이 서버 주소, cross-site 가 아님."""
            origin = (self.headers.get("Origin") or "").strip().lower()
            site = (self.headers.get("Sec-Fetch-Site") or "").strip().lower()
            if (origin and origin not in allowed_origins) or site == "cross-site":
                self._error(403, "다른 웹 페이지에서 온 요청은 받지 않습니다")
                return False
            return True

        def _body(self) -> dict:
            ctype = (self.headers.get("Content-Type") or "").split(";")[0].strip().lower()
            if ctype != "application/json":
                raise JobError("Content-Type: application/json 만 받습니다", status=415)
            if self.headers.get("Transfer-Encoding"):  # 길이 없이 나눠 보낸 본문은 읽지 않음
                raise JobError("Content-Length 가 있는 본문만 받습니다", status=411)
            text = (self.headers.get("Content-Length") or "").strip()
            try:
                n = int(text) if text else 0
            except ValueError:
                n = -1
            if n < 0:  # 음수면 rfile.read(-1) 이 연결이 끝날 때까지 끝없이 읽음
                raise JobError(f"Content-Length 가 올바르지 않습니다: {text!r}")
            if n > MAX_BODY:
                raise JobError("요청이 너무 큽니다", status=413)
            raw = self.rfile.read(n) if n else b"{}"
            try:
                data = json.loads(raw.decode("utf-8"), parse_constant=_reject_constant)
            except ValueError:
                raise JobError("JSON 을 읽지 못했습니다") from None
            if not isinstance(data, dict):
                raise JobError("JSON 객체를 보내야 합니다")
            return data

        def _static(self, name: str) -> None:
            files = {p.name: p for p in STATIC.iterdir() if p.is_file()}
            p = files.get(name)
            if p is None:
                self._error(404, f"파일이 없습니다: {name}")
                return
            ctype = STATIC_TYPES.get(p.suffix.lower())
            if ctype is None:
                self._error(404, f"보내지 않는 파일 종류입니다: {name}")
                return
            self._send(200, p.read_bytes(), ctype)

        # ------------------------------------------------------------ 받기
        def do_HEAD(self) -> None:
            self.do_GET()

        def do_GET(self) -> None:
            if not self._host_ok():
                return
            url = urlparse(self.path)
            q = parse_qs(url.query)
            try:
                self._route_get(url.path, q)
            except (JobError, ViewError) as e:
                self._error(e.status, str(e), getattr(e, "details", None))
            except FileNotFoundError as e:
                self._error(404, f"파일이 없습니다: {e}")
            except ConnectionError:
                self.close_connection = True  # 브라우저가 먼저 끊음 (탭 닫기, 새로 고침)
            except Exception as e:  # 화면에 보여 주고 서버는 계속 돕니다
                traceback.print_exc()
                self._error(500, f"서버 오류: {type(e).__name__}: {e}")

        def do_POST(self) -> None:
            if not self._host_ok() or not self._origin_ok():
                self.close_connection = True  # 읽지 않은 본문이 다음 요청으로 읽히지 않게
                return
            url = urlparse(self.path)
            try:
                self._route_post(url.path, self._body())
            except (JobError, ViewError, gd.GodotError) as e:
                self.close_connection = True
                self._error(getattr(e, "status", 400), str(e), getattr(e, "details", None))
            except ConnectionError:
                self.close_connection = True
            except Exception as e:
                self.close_connection = True  # 본문을 다 읽었는지 모르므로 연결을 닫음
                traceback.print_exc()
                self._error(500, f"서버 오류: {type(e).__name__}: {e}")

        def _route_get(self, path: str, q: dict) -> None:
            if path in ("/", "/index.html"):
                self._static("index.html")
            elif path.startswith("/static/"):
                self._static(path.removeprefix("/static/"))
            elif path == "/favicon.ico":
                self._send(204, b"", "image/x-icon")
            elif path == "/api/meta":
                from bpcg_studio.hero import MAX_HERO_CELLS

                active = app.jobs.active()
                self._json(
                    {
                        "version": __version__,
                        "planets": planet_names(),
                        "profiles": profile_names(),
                        "out_dir": str(app.out_dir),
                        "active_job": None if active is None else active.id,
                        "godot": app.godot.status(),
                        "laptop_hero": _laptop_hero(),
                        # 히어로 한 변 칸 수 n = round(size_m / spacing_m) (파이썬 round, 반은
                        # 짝수 쪽), size_m ≥ 3·spacing_m, n ≤ max_hero_side (Hero/Domain.cs)
                        "max_hero_side": math.isqrt(MAX_HERO_CELLS),
                        "hero_side_rule": "round_half_even",
                    }
                )
            elif path == "/api/params":
                planet = _one(q, "planet", "earth")
                profile = _one(q, "profile", "laptop")
                try:
                    self._json(build_schema(planet, profile))
                except ValueError as e:
                    raise JobError(str(e)) from None
            elif path == "/api/runs":
                skipped: list[dict] = []
                runs = list_runs(app.out_dir, skipped)
                self._json({"runs": runs, "skipped": skipped})
            elif path == "/api/run":
                key = _one(q, "run")
                self._json(run_summary(app.run_dir(q), key))
            elif path == "/api/job":
                since = _int(q, "since") if q.get("since") else None
                self._json(app.jobs.job_state(_one(q, "id"), since))
            elif path == "/api/level":
                level = _one(q, "level")
                self._json(app.views.level_meta(app.run_dir(q), level))
            elif path == "/api/field":
                meta = app.views.field_meta(app.run_dir(q), _one(q, "level"), _one(q, "name"))
                self._json(meta)
            elif path == "/api/field.bin":
                body = app.views.grid_bytes(app.run_dir(q), _one(q, "level"), _one(q, "name"))
                self._send(200, body, "application/octet-stream")
            elif path == "/api/probe":
                data = app.views.level(app.run_dir(q), _one(q, "level"))
                self._json(data.probe(_int(q, "px"), _int(q, "py")))
            elif path == "/api/figure":
                p = figure_path(app.run_dir(q), _one(q, "name"))
                self._send(200, p.read_bytes(), "image/png")
            elif path == "/api/godot":
                self._json(app.godot.status())
            elif path in ("/compare", "/compare.html"):
                self._static("compare.html")
            elif path.startswith("/api/compare/"):
                self._route_compare(path.removeprefix("/api/compare/"), q)
            else:
                self._error(404, f"없는 주소입니다: {path}")

        def _route_compare(self, sub: str, q: dict) -> None:
            cs = app.compare
            if sub == "sets":
                self._json(cs.sets())
            elif sub == "set":
                self._json(cs.set_data(_one(q, "name")))
            elif sub == "methods":
                self._json(cs.methods())
            elif sub == "process":
                self._json(cs.process(_one(q, "name"), _int(q, "seed"), _one(q, "id")))
            elif sub == "elevation.png":
                body = cs.elevation_png(
                    _one(q, "name"), _int(q, "seed"), _one(q, "id"), _one(q, "scale", "self")
                )
                self._send(200, body, "image/png")
            elif sub == "stage.png":
                body = cs.stage_png(_one(q, "name"), _int(q, "seed"), _one(q, "id"), _int(q, "i"))
                self._send(200, body, "image/png")
            elif sub == "run":
                since = _int(q, "since") if q.get("since") else None
                self._json(cs.run_state(since))
            else:
                self._error(404, f"없는 주소입니다: /api/compare/{sub}")

        def _route_post(self, path: str, body: dict) -> None:
            if path == "/api/jobs":
                job = app.jobs.start(body)
                self._json(job.state(), 201)
            elif path == "/api/jobs/cancel":
                job = app.jobs.cancel(str(body.get("id") or ""))
                self._json({"id": job.id, "status": job.status, "cancel_requested": True})
            elif path == "/api/validate":
                try:
                    spec = validate_request({**body, "seed": body.get("seed", 0)})
                    self._json({"ok": True, "overrides": spec["overrides"], "errors": {}})
                except JobError as e:
                    self._json({"ok": False, "message": str(e), "errors": e.details})
            elif path == "/api/compare/run":
                self._json(app.compare.start(body), 201)
            elif path == "/api/compare/cancel":
                self._json(app.compare.cancel())
            elif path == "/api/godot/launch":
                mode = str(body.get("mode") or "play")
                key = str(body.get("run") or "")
                corridor = None
                if key:
                    run_dir = resolve_run(key, app.out_dir)
                    corridor = run_dir / "corridor"
                    if not (corridor / "manifest.json").exists():
                        raise JobError(
                            "이 실행에는 회랑(corridor/) 결과가 없습니다. "
                            "굽기까지 끝난 실행을 고르세요",
                            status=409,
                        )
                app.godot.start(key, corridor, mode)
                self._json(app.godot.status(), 202)
            else:
                self._error(404, f"없는 주소입니다: {path}")

    return Handler


def _laptop_hero() -> dict:
    """비용 비교 기준: 노트북 프로필의 히어로 칸 수."""
    try:
        h = load_config("earth", "laptop").profile.hero
        n = round(float(h.size_m) / float(h.spacing_m))
        return {"size_m": float(h.size_m), "spacing_m": float(h.spacing_m), "cells": n * n}
    except (OSError, ValueError, KeyError, AttributeError):
        return {"size_m": 32000.0, "spacing_m": 25.0, "cells": 1280 * 1280}


class StudioHTTPServer(ThreadingHTTPServer):
    """스튜디오 HTTP 서버.

    - 윈도우에서 SO_REUSEADDR 는 이미 듣고 있는 포트에 두 번째 소켓이 붙게 해서, 스튜디오를 두 번
      켜도 '포트를 열지 못함' 이 나오지 않고 요청이 두 서버로 갈립니다. 윈도우는 SO_REUSEADDR 를
      끄고 SO_EXCLUSIVEADDRUSE 를 켭니다 (asyncio 도 윈도우에서 reuse_address 를 쓰지 않음).
    - 브라우저가 먼저 끊은 연결은 traceback 없이 한 줄만 찍습니다.
    """

    allow_reuse_address = sys.platform != "win32"
    daemon_threads = True

    def server_bind(self) -> None:
        if sys.platform == "win32" and hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()

    def handle_error(self, request: Any, client_address: Any) -> None:
        exc = sys.exc_info()[1]
        if isinstance(exc, ConnectionError):
            host, port = (tuple(client_address) + ("?", "?"))[:2]
            sys.stderr.write(
                f"[스튜디오] 브라우저가 연결을 먼저 끊었습니다 ({host}:{port}, "
                f"{type(exc).__name__})\n"
            )
            return
        super().handle_error(request, client_address)


def create_server(port: int = 8765, app: Studio | None = None) -> StudioHTTPServer:
    """127.0.0.1:port 에 서버를 만듭니다 (port 0 이면 빈 포트). 아직 돌지는 않습니다.

    포트를 먼저 열고 Studio 를 만듭니다. Studio 는 만들 때 '서버가 꺼져서 멈춘 실행' 을 job.json 에
    적으므로, 포트가 이미 쓰이는 두 번째 스튜디오가 첫 서버의 실행을 건드리기 전에 멈추게 합니다.
    """
    server = StudioHTTPServer((HOST, port), BaseHTTPRequestHandler)
    try:
        app = app or Studio()
    except BaseException:
        server.server_close()
        raise
    server.RequestHandlerClass = make_handler(app, server.server_address[1])
    server.studio = app  # type: ignore[attr-defined]
    return server


def serve(port: int = 8765, open_browser: bool = True) -> None:
    """서버를 켜고(브라우저도 열고) Ctrl+C 까지 돕니다."""
    try:
        server = create_server(port)
    except OSError as e:
        raise SystemExit(
            f"포트 {port} 를 열지 못했습니다 ({e}). 이미 스튜디오가 켜져 있으면 "
            f"http://{HOST}:{port}/ 를 여세요. 다른 포트: bpcg studio --port {port + 1}"
        ) from None
    real = server.server_address[1]
    url = f"http://{HOST}:{real}/"
    print(f"[스튜디오] {url} 에서 듣습니다 (끄려면 Ctrl+C)", flush=True)
    if open_browser:
        threading.Timer(0.6, lambda: webbrowser.open(url)).start()
    try:
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        print("\n[스튜디오] 끕니다", flush=True)
    finally:
        server.server_close()
