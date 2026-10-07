"""스튜디오의 '방법 비교' (bpcg_studio.compare_api, docs/compare.md 5장) 와 bpcg compare 명령 시험.

계산은 C# 콘솔이 하므로 여기서는 콘솔이 쓴 파일을 스튜디오가 읽고, 그림·실행을 콘솔로 부르는지
봅니다.
방법·지표 자체는 tests/Bpcg.Tests/CompareTool 이 봅니다.
"""

import json
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

import pytest
from bundles import run_cli

from bpcg_studio.compare_api import CompareService
from bpcg_studio.jobs import JobError
from bpcg_studio.server import Studio, create_server

PNG = b"\x89PNG\r\n\x1a\n"
CONFIG = """
[grid]
n = 32
dx = 100.0
relief_m = 800.0

[run]
seeds = [0, 1]

[[methods]]
id = "fbm"

[[methods]]
id = "thermal"
params = { iterations = 5 }

[[methods]]
id = "faulting"
params = { faults = 20 }
"""


@pytest.fixture(scope="module")
def compare_out(csharp_cli, tmp_path_factory) -> Path:
    """out 폴더: out/compare/t 에 작은 비교 묶음 (시드 0·1, 방법 셋)."""
    out = tmp_path_factory.mktemp("cmp_out")
    cfg = out / "t.toml"
    cfg.write_text(CONFIG, encoding="utf-8")
    proc = csharp_cli("compare", "run", "--config", cfg, "--out", out / "compare" / "t")
    assert "[비교] 끝" in proc.stdout
    return out


def test_service_reads_sets_groups_and_process(compare_out):
    cs = CompareService(compare_out / "compare")
    sets = cs.sets()
    assert [s["name"] for s in sets["sets"]] == ["t"]
    assert "default" in sets["configs"]
    data = cs.set_data("t")
    assert [g["seed"] for g in data["groups"]] == [0, 1]
    for g in data["groups"]:
        assert [e["id"] for e in g["entries"]] == ["fbm", "thermal", "faulting"]
        assert all(e["metrics"]["relief_m"] > 0 for e in g["entries"])
    assert {m["name"] for m in data["metrics"]} >= {"relief_m", "spectral_beta"}
    assert len(data["elevation_colors"]) >= 2
    pr = cs.process("t", 0, "thermal")
    assert pr["stages"] and pr["series"]
    assert {"label", "kind", "vmin", "vmax", "legend"} <= set(pr["stages"][0])
    methods = {m["name"] for m in cs.methods()["methods"]}
    assert {"fbm", "hydraulic", "quilting", "bpcg"} <= methods


def test_service_renders_one_at_a_time_and_remembers(compare_out, monkeypatch):
    cs = CompareService(compare_out / "compare")
    calls: list[list[str]] = []
    real = cs._console

    def counting(args, timeout=60):
        calls.append(args)
        return real(args, timeout)

    monkeypatch.setattr(cs, "_console", counting)
    assert cs.elevation_png("t", 0, "fbm", "self")[:8] == PNG
    assert cs.elevation_png("t", 0, "fbm", "self")[:8] == PNG
    assert len(calls) == 1  # 두 번째는 기억한 파일
    assert cs.elevation_png("t", 0, "fbm", "group")[:8] == PNG
    assert cs.stage_png("t", 0, "thermal", 0)[:8] == PNG
    assert len(calls) == 3
    for bad in (
        lambda: cs.set_data("../t"),
        lambda: cs.elevation_png("t", 0, "nope", "self"),
        lambda: cs.elevation_png("t", 0, "fbm", "wide"),
        lambda: cs.stage_png("t", 0, "thermal", 99),
        lambda: cs.process("t", 5, "fbm"),
    ):
        with pytest.raises(JobError):
            bad()


def test_cli_list_render_and_errors(compare_out, tmp_path):
    set_dir = compare_out / "compare" / "t"
    out = run_cli("compare", "list", "--set", set_dir).stdout
    assert "== 시드 0" in out and "faulting" in out
    listed = json.loads(run_cli("compare", "list", "--set", set_dir, "--json").stdout)
    assert [g["seed"] for g in listed["groups"]] == [0, 1]
    png = run_cli("compare", "render", "--set", set_dir, "--seed", "1", "--method", "fbm")
    assert Path(png.stdout.strip()).read_bytes()[:8] == PNG
    cfg = tmp_path / "c.toml"
    cfg.write_text(CONFIG, encoding="utf-8")
    bad = run_cli("compare", "run", "--config", cfg, "--param", "fbm.nope=1", check=False)
    assert bad.returncode == 1 and "없는 매개변수" in bad.stderr
    bad = run_cli("compare", "render", "--set", set_dir, "--seed", "0", check=False)
    assert bad.returncode == 2 and "--method" in bad.stderr
    bad = run_cli("compare", "list", "--set", tmp_path / "none", check=False)
    assert bad.returncode == 1 and "비교 결과가 없습니다" in bad.stderr


def _get(base: str, path: str, raw: bool = False):
    with urllib.request.urlopen(base + path, timeout=60) as r:
        body = r.read()
        return body if raw else json.loads(body.decode("utf-8"))


def _post(base: str, path: str, body: dict):
    req = urllib.request.Request(
        base + path,
        data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read().decode("utf-8"))


def test_server_routes_and_run(compare_out, tmp_path):
    app = Studio(out_dir=compare_out, baked_dir=tmp_path / "baked")
    server = create_server(0, app)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    base = f"http://127.0.0.1:{server.server_address[1]}"
    try:
        assert "방법 비교".encode() in _get(base, "/compare", raw=True)
        assert b"renderTable" in _get(base, "/static/compare.js", raw=True)
        assert b'href="/compare"' in _get(base, "/", raw=True)
        assert [s["name"] for s in _get(base, "/api/compare/sets")["sets"]] == ["t"]
        assert _get(base, "/api/compare/set?name=t")["groups"][1]["seed"] == 1
        png = _get(base, "/api/compare/elevation.png?name=t&seed=1&id=thermal&scale=group", True)
        assert png[:8] == PNG
        assert _get(base, "/api/compare/stage.png?name=t&seed=0&id=fbm&i=0", raw=True)[:8] == PNG
        with pytest.raises(urllib.error.HTTPError) as e:
            _get(base, "/api/compare/process?name=t&seed=0&id=nope")
        assert e.value.code == 404
        with pytest.raises(urllib.error.HTTPError) as e:
            _post(base, "/api/compare/cancel", {})
        assert e.value.code == 409
        # 실행: 저장소의 default 설정을 작은 격자·방법 둘로
        body = {"config": "default", "name": "web", "seeds": "0", "only": ["fbm", "faulting"]}
        st = _post(base, "/api/compare/run", {**body, "n": 32})
        assert st["status"] == "running"
        deadline = time.time() + 300
        while st["status"] == "running" and time.time() < deadline:
            time.sleep(0.5)
            st = _get(base, "/api/compare/run?since=0")
        assert st["status"] == "done", st["tail"]
        assert st["progress"] == [2, 2]
        web = _get(base, "/api/compare/set?name=web")
        assert [e["id"] for e in web["groups"][0]["entries"]] == ["fbm", "faulting"]
        assert web["config"]["grid"]["n"] == 32
    finally:
        server.shutdown()
        server.server_close()
