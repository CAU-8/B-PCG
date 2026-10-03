"""스튜디오 검사 (docs/studio.md): 매개변수 표, --set, 진행률, 지도 격자, HTTP API, Godot 파일 복사.

tiny 프로필 실행 하나를 모듈 fixture 로 만들어(몇 초) 진행률·지도·서버 검사에 같이 씁니다.
Godot 는 실행하지 않습니다(찾기는 가짜 후보와 가짜 버전 함수로, 복사는 임시 폴더로).
실행 관리(취소·실패·동시 시작·저장 실패·서버 꺼짐)는 파이프라인 대신 짧은 파이썬 한 줄을 돌립니다.
"""

import contextlib
import http.client
import io
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import tomllib
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler
from pathlib import Path

import numpy as np
import pytest
from bundles import run_cli

import bpcg_studio.server as server_mod
import bpcg_studio.views as views_mod
from bpcg_studio import godot as gd
from bpcg_studio import summary as sm
from bpcg_studio.bundle import read_manifest
from bpcg_studio.config import BareText, checked_overrides, load_config, parse_assignment
from bpcg_studio.jobs import (
    JobError,
    JobManager,
    cost_of,
    list_runs,
    pid_alive,
    resolve_run,
    toml_literal,
    validate_request,
)
from bpcg_studio.names import bare_name, relative_name, run_folder_name
from bpcg_studio.params import (
    CATEGORIES,
    build_schema,
    config_diff,
    config_missing,
    profile_names,
)
from bpcg_studio.paths import CONFIGS
from bpcg_studio.progress import STAGE_INDEX, STAGES, ProgressTracker
from bpcg_studio.server import Studio, StudioHTTPServer, create_server
from bpcg_studio.views import PLANET_H, PLANET_W, ViewStore


def _run_cli(args: list[str]) -> list[str]:
    """C# 콘솔을 돌리고 기록 줄을 돌려줍니다."""
    return run_cli(*args).stdout.splitlines()


def _cli_error(args: list[str]) -> str:
    """실패해야 하는 C# 콘솔 명령. 반환: 오류 글 (stderr)."""
    proc = run_cli(*args, check=False)
    assert proc.returncode != 0, args
    return proc.stderr


@pytest.fixture(scope="module")
def runs(csharp_cli, tmp_path_factory):
    """tiny 실행 둘: 보통(θ 를 --set 으로 바꿈)과 평면 히어로. 반환: (out 폴더, {이름: 기록 줄})."""
    out = tmp_path_factory.mktemp("studio_out")
    logs = {
        "tiny": _run_cli(
            ["all", "--profile", "tiny", "--out", str(out / "tiny"), "--set", "landscape.theta=0.5"]
        ),
        "flat": _run_cli(
            ["all", "--profile", "tiny", "--flat", "--seed", "3", "--out", str(out / "flat")]
        ),
    }
    return out, logs


def _flatten(d: dict, prefix: str = "") -> dict:
    out = {}
    for k, v in d.items():
        out.update(_flatten(v, f"{prefix}{k}.") if isinstance(v, dict) else {f"{prefix}{k}": v})
    return out


# ---------------------------------------------------------------- 매개변수 표
@pytest.mark.parametrize("profile", [p["name"] for p in profile_names()])
def test_schema_covers_every_config_key_with_help(profile):
    with open(CONFIGS / "planets" / "earth.toml", "rb") as fh:
        planet_keys = set(_flatten(tomllib.load(fh)))
    with open(CONFIGS / "profiles" / f"{profile}.toml", "rb") as fh:
        profile_keys = {f"profile.{k}" for k in _flatten(tomllib.load(fh))}
    schema = build_schema("earth", profile)
    by_key = {p["key"]: p for p in schema["params"]}
    missing = (planet_keys | profile_keys) - set(by_key)
    assert not missing, f"매개변수 표에 없는 키: {sorted(missing)}"
    no_help = [k for k in planet_keys | profile_keys if not by_key[k]["help"].strip()]
    assert not no_help, f"설명이 없는 키: {sorted(no_help)}"
    for p in schema["params"]:
        assert p["category"] in CATEGORIES
        assert p["type"] in ("int", "float", "bool", "str", "list")
    assert by_key["landscape.theta"]["category"] == "learn"
    assert by_key["landscape.slope_exponent_n"]["category"] == "hand"
    assert by_key["landscape.slope_exponent_n"]["history"]
    assert by_key["planet.radius_m"]["category"] == "physics"
    assert by_key["planet.radius_m"]["unit"] == "m"
    assert by_key["uplift.craton_erosion_m_per_myr"]["unit"] == "m/Myr"
    assert by_key["profile.hero.size_m"]["category"] == "resolution"
    assert not by_key["planet.seed"]["editable"]
    sections = {s["id"] for s in schema["sections"]}
    assert {"landscape", "caves", "profile.hero"} <= sections


def test_config_diff_lists_changed_keys():
    cfg = load_config("earth", "tiny", {"landscape.theta": 0.5}).as_dict()
    diff = config_diff(cfg)
    assert [d["key"] for d in diff] == ["landscape.theta"]
    assert diff[0]["default"] == 0.45 and diff[0]["value"] == 0.5


# ---------------------------------------------------------------- --set
def test_parse_assignment_reads_toml_values():
    assert parse_assignment("landscape.theta=0.5") == ("landscape.theta", 0.5)
    assert parse_assignment(" fans.enabled = false ") == ("fans.enabled", False)
    assert parse_assignment("plates.speed_m_per_yr=[0.02, 0.07]")[1] == [0.02, 0.07]
    assert parse_assignment('planet.name="mars"')[1] == "mars"
    assert isinstance(parse_assignment("planet.name=mars")[1], BareText)
    with pytest.raises(ValueError):
        parse_assignment("landscape.theta")


def test_checked_overrides_rejects_unknown_keys_and_bad_types():
    cfg = load_config("earth", "tiny")
    out = checked_overrides(cfg, {"uplift.orogen_width_m": 90000, "caves.levels": 2.0})
    assert out == {"uplift.orogen_width_m": 90000.0, "caves.levels": 2}
    assert isinstance(out["uplift.orogen_width_m"], float)
    assert isinstance(out["caves.levels"], int)
    with pytest.raises(ValueError, match="landscape.theta"):  # 비슷한 키를 알려 줌
        checked_overrides(cfg, {"landscape.thetaa": 0.5})
    with pytest.raises(ValueError, match="실수"):
        checked_overrides(cfg, {"landscape.theta": "abc"})
    with pytest.raises(ValueError, match="절"):
        checked_overrides(cfg, {"landscape": 1.0})
    with pytest.raises(ValueError):
        checked_overrides(cfg, {"fans.enabled": 1})
    with pytest.raises(ValueError, match="원소 2개"):
        checked_overrides(cfg, {"plates.speed_m_per_yr": [0.1]})


def test_cli_set_errors_clearly(csharp_cli, tmp_path):
    base = ["planet", "--profile", "tiny", "--out", str(tmp_path), "--set"]
    assert "landscape.thetaa" in _cli_error(base + ["landscape.thetaa=1"])
    assert "--set" in _cli_error(base + ["nonsense"])
    assert "--flat" in _cli_error(["hero", "--from", str(tmp_path), "--set", "landscape.theta=0.5"])


def test_cli_set_reaches_bundle_config(runs):
    out, logs = runs
    man = read_manifest(out / "tiny" / "planet")
    assert man["config"]["landscape"]["theta"] == 0.5
    assert read_manifest(out / "tiny" / "hero")["config"]["landscape"]["theta"] == 0.5
    assert any(line.startswith("[설정] landscape.theta = 0.5") for line in logs["tiny"])


@pytest.mark.parametrize("value", [0.5, 1e-05, 1.335e18, 3, True, 'a"b', [0.02, 0.08], -1e-4])
def test_toml_literal_round_trips(value):
    assert parse_assignment(f"k={toml_literal(value)}")[1] == value


# ---------------------------------------------------------------- 진행률
def _feed(tracker: ProgressTracker, lines: list[str]) -> list[float]:
    tracker.start(0.0)
    seen = []
    for i, line in enumerate(lines):
        tracker.feed(line, float(i + 1))
        seen.append(tracker.fraction(float(i + 1)))
    return seen


def test_progress_reaches_100_monotonically_on_tiny_log(runs):
    _, logs = runs
    lines = logs["tiny"]
    tr = ProgressTracker(
        flat=False,
        figures=False,
        max_iter={"landscape.max_flow_iterations": 200, "profile.hero.max_flow_iterations": 400},
    )
    seen = _feed(tr, lines)
    assert all(b >= a for a, b in zip(seen, seen[1:], strict=False)), "진행률이 줄었습니다"
    assert 0.9 < seen[-1] < 1.0  # 끝(finish) 전에는 100% 가 아님
    # 파이프라인 단계는 기록 줄만으로 모두 끝나야 합니다 (그림은 고르지 않음).
    snap = tr.snapshot(1e3)
    status = {s["key"]: s["status"] for s in snap["stages"]}
    assert status["figures"] == "skipped"
    assert all(v == "done" for k, v in status.items() if k != "figures"), status
    # 같은 "솔버 끝" 줄이 행성과 히어로에 차례로 맞아야 합니다 (단계를 잘못 세면 여기서 틀림).
    ends = [ln for ln in lines if ln.startswith("[2단계] 솔버 끝")]
    result = {s["key"]: s["result"] for s in snap["stages"]}
    assert len(ends) >= 2
    assert result["planet_solver"] == ends[0] and result["hero_solver"] == ends[-1]
    assert result["bake_finish"].startswith("[굽기] 끝")
    assert result["globe"].startswith("[지구본] 끝")
    # 행성 솔버 끝 줄까지만 넣으면 히어로 단계는 아직 시작 전입니다.
    part = ProgressTracker(figures=False)
    _feed(part, lines[: lines.index(ends[0]) + 1])
    st = {s["key"]: s["status"] for s in part.snapshot(1e3)["stages"]}
    assert st["planet_solver"] == "done" and st["planet_fans"] == "running"
    assert st["hero_solver"] == "pending" and st["bake_finish"] == "pending"
    assert part.fraction(1e3) < 0.6
    tr.finish("done", 1e3)
    assert tr.fraction(1e3) == 1.0
    assert tr.snapshot(1e3)["percent"] == 100.0


def test_progress_flat_run_skips_planet_stages(runs):
    _, logs = runs
    tr = ProgressTracker(flat=True, figures=False)
    seen = _feed(tr, logs["flat"])
    assert all(b >= a for a, b in zip(seen, seen[1:], strict=False))
    snap = tr.snapshot(1e3)
    status = {s["key"]: s["status"] for s in snap["stages"]}
    planet_keys = {s.key for s in STAGES if s.planet_only}
    assert all(status[k] == "skipped" for k in planet_keys)
    assert status["hero_solver"] == "done" and status["bake_finish"] == "done"


def test_progress_reports_fallback_warning_and_failure():
    tr = ProgressTracker()
    tr.start(0.0)
    tr.feed("[행성] 시작: 프로필 tiny, 시드 0", 1.0)
    tr.feed("[히어로] 행성에서 히어로 자리를 찾지 못해 평면 히어로로 바꿉니다: 후보 없음", 2.0)
    snap = tr.snapshot(2.0)
    assert len(snap["warnings"]) == 1 and "평면 히어로" in snap["warnings"][0]["message"]
    tr.finish("failed", 3.0, "종료 코드 1")
    st = {s["key"]: s["status"] for s in tr.snapshot(3.0)["stages"]}
    assert "failed" in st.values()


def test_progress_uses_history_weights_for_eta():
    tr = ProgressTracker(weights={s.key: 1.0 for s in STAGES}, figures=True)
    tr.start(0.0)
    tr.feed("[행성] 시작: 프로필 tiny, 시드 0", 1.0)
    eta, basis = tr.eta_seconds(1.0)
    assert basis == "지난 실행 기준" and eta == pytest.approx(len(STAGES) - 1.0)


def test_progress_eta_follows_iterations_when_stage_overruns_history():
    """지난 실행보다 오래 걸리는 단계: 반복 비율로 남은 시간을 세고, 없으면 '계산 중'(None)."""
    w = {s.key: 1.0 for s in STAGES}
    tr = ProgressTracker(weights=w, figures=True, max_iter={"landscape.max_flow_iterations": 100})
    tr.start(0.0)
    for i, line in enumerate(
        [
            "[행성] 시작: x",
            "[1단계] 거친 격자",
            "[1단계] L0 면당",
            "[1단계] 지질 템플릿",
            "[2단계] 지각 세기 한계",
        ]  # fmt: skip
    ):
        tr.feed(line, float(i + 1))
    assert tr.current()["key"] == "planet_solver"  # 5 s 에 시작
    tr.feed("솔버 반복 10: 방향 변화 1", 20.0)  # 15 s 지남 (지난번 1 s), 반복 10 / 100
    eta, basis = tr.eta_seconds(20.0)
    pending = sum(1 for s in tr.stages if s["status"] == "pending")
    assert basis == "지난 실행 기준"
    assert eta == pytest.approx(15.0 * 0.9 / 0.1 + pending)
    row = next(s for s in tr.snapshot(20.0)["stages"] if s["key"] == "planet_solver")
    assert row["sub"] == pytest.approx(0.1)  # 시간 상한 0.9 가 아니라 반복 비율
    # 반복 줄이 없는 단계가 지난번 시간을 넘기면 남은 시간을 모름
    tr2 = ProgressTracker(weights=w, figures=True)
    tr2.start(0.0)
    tr2.feed("[행성] 시작: x", 1.0)
    assert tr2.eta_seconds(10.0)[0] is None


def test_progress_scales_missing_history_stages():
    """지난 실행에 없던 단계는 기본 무게 × (잰 시간 / 기본 무게)."""
    d = {s.key: s.weight for s in STAGES}
    tr = ProgressTracker(weights={"planet_solver": 2 * d["planet_solver"], "hero_solver": 2 * d[
        "hero_solver"]})  # fmt: skip
    assert tr.stages[STAGE_INDEX["figures"]]["weight"] == pytest.approx(2 * d["figures"])
    assert tr.stages[STAGE_INDEX["hero_solver"]]["weight"] == pytest.approx(2 * d["hero_solver"])


def test_progress_config_error_gets_its_own_warning():
    tr = ProgressTracker()
    tr.start(0.0)
    tr.feed(
        "[히어로] 행성에서 히어로 자리를 찾지 못해 평면 히어로로 바꿉니다: profile.hero 는 "
        "spacing_m > 0, size_m ≥ 3·spacing_m 이어야 합니다: 50.0, 25.0",
        1.0,
    )
    (w,) = tr.snapshot(1.0)["warnings"]
    assert "영역 설정" in w["message"] and "줄이거나" not in w["message"]


# ---------------------------------------------------------------- 지도 격자
def test_views_planet_and_hero_grids(runs, tmp_path):
    out, _ = runs
    store = ViewStore(tmp_path / "cache")
    run = out / "tiny"
    planet = store.level(run, "planet")
    meta = planet.level_meta()
    assert (meta["width"], meta["height"]) == (PLANET_W, PLANET_H)
    names = {f["name"] for g in meta["groups"] for f in g["fields"]}
    assert {"z_mean_m", "is_ocean", "ocean_age_myr", "ocean_depth_m", "cave_entrance"} <= names
    groups = {g["name"] for g in meta["groups"]}
    assert {"지형", "판·지각", "해양", "기후", "동굴"} <= groups
    assert meta["markers"] and meta["markers"][0]["label"] == "히어로 유역"
    grid = planet.grid("z_mean_m")
    assert grid.shape == (PLANET_H, PLANET_W) and grid.dtype == np.dtype("<f4")
    assert len(store.grid_bytes(run, "planet", "uplift_m_per_yr")) == PLANET_W * PLANET_H * 4
    assert list((tmp_path / "cache").glob("equirect-*.npy")), "칸 번호 지도를 캐시하지 않았습니다"
    up = store.field_meta(run, "planet", "uplift_m_per_yr")
    assert up["unit"] == "mm/yr" and up["factor"] == 1e3 and up["label"] == "융기 속도 U"
    assert up["read"] and up["description"]
    plates = store.field_meta(run, "planet", "plate_id")
    assert plates["kind"] == "categorical" and plates["categories"]
    rock = store.field_meta(run, "planet", "surface_rock")
    assert any(c["label"].startswith("석회암") for c in rock["categories"])

    hero = store.level(run, "hero")
    hm = hero.level_meta()
    ny, nx = read_manifest(run / "hero")["graph"]["shape"]
    assert (hm["height"], hm["width"]) == (ny, nx)  # tiny 는 640 보다 작아 묶지 않음
    assert hm["rects"] and hm["rects"][0]["x1"] > hm["rects"][0]["x0"]
    assert hero.grid("water_table_depth_m").shape == (ny, nx)
    assert not hero.has("ocean_depth_m")  # 바다 수심은 행성에만
    probe = hero.probe(3, 4)
    labels = {it["label"] for g in probe["groups"] for it in g["items"]}
    assert "지표 고도" in labels and "지표 암석" in labels
    assert probe["strata"] and probe["strata"][-1]["bedrock"]
    pp = planet.probe(10, 10)
    assert -90 <= pp["where"]["lat"] <= 90 and "칸" in pp["where"]["label"]


# ---------------------------------------------------------------- 서버
def _get(base: str, path: str, raw: bool = False):
    with urllib.request.urlopen(base + path, timeout=30) as r:
        body = r.read()
        return body if raw else json.loads(body.decode("utf-8"))


def _post(base: str, path: str, body: dict):
    req = urllib.request.Request(
        base + path,
        data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read().decode("utf-8"))


def test_server_api_smoke(runs, tmp_path):
    out, _ = runs
    app = Studio(out_dir=out, baked_dir=tmp_path / "baked")
    server = create_server(0, app)
    port = server.server_address[1]
    assert server.server_address[0] == "127.0.0.1"
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    base = f"http://127.0.0.1:{port}"
    try:
        assert b"B-PCG" in _get(base, "/", raw=True)
        assert b"MapView" in _get(base, "/static/app.js", raw=True)
        params = _get(base, "/api/params?planet=earth&profile=tiny")
        assert any(p["key"] == "landscape.theta" for p in params["params"])
        keys = {r["key"] for r in _get(base, "/api/runs")["runs"]}
        assert {"tiny", "flat"} <= keys
        summary = _get(base, "/api/run?run=tiny")
        assert [d["key"] for d in summary["diff"]] == ["landscape.theta"]
        assert summary["scorecard"] and summary["solver"]["hero"]["iterations"]
        level = _get(base, "/api/level?run=tiny&level=hero")
        meta = _get(base, "/api/field?run=tiny&level=hero&name=z_m")
        assert meta["unit"] == "m" and meta["range"][1] > meta["range"][0]
        data = _get(base, "/api/field.bin?run=tiny&level=hero&name=z_m", raw=True)
        assert len(data) == level["width"] * level["height"] * 4
        assert np.isfinite(np.frombuffer(data, dtype="<f4")).all()
        probe = _get(base, "/api/probe?run=tiny&level=hero&px=1&py=1")
        assert probe["groups"]
        ok = _post(
            base, "/api/validate", {"profile": "tiny", "overrides": {"landscape.theta": 0.5}}
        )
        assert ok["ok"]
        bad = _post(base, "/api/validate", {"profile": "tiny", "overrides": {"nope.key": 1}})
        assert not bad["ok"] and "nope.key" in bad["errors"]
        assert _get(base, "/api/godot")["baked"] is None
        with pytest.raises(urllib.error.HTTPError) as e:
            _get(base, "/api/run?run=../etc")
        assert e.value.code == 400
        with pytest.raises(urllib.error.HTTPError) as e:
            _get(base, "/api/field?run=tiny&level=hero&name=no_such_field")
        assert e.value.code == 404
        req = urllib.request.Request(base + "/api/meta", headers={"Host": "evil.example"})
        with pytest.raises(urllib.error.HTTPError) as e:
            urllib.request.urlopen(req, timeout=10)
        assert e.value.code == 403
        req = urllib.request.Request(base + "/api/jobs", data=b"x", method="POST")
        with pytest.raises(urllib.error.HTTPError) as e:
            urllib.request.urlopen(req, timeout=10)
        assert e.value.code == 415
    finally:
        server.shutdown()
        server.server_close()


# ---------------------------------------------------------------- 실행 관리
def test_validate_request_reports_bad_keys():
    with pytest.raises(JobError) as e:
        validate_request({"profile": "tiny", "overrides": {"landscape.thetaa": 1.0}})
    assert "landscape.thetaa" in e.value.details
    with pytest.raises(JobError):
        validate_request({"profile": "no_such_profile"})
    spec = validate_request({"profile": "tiny", "seed": 2, "overrides": {"landscape.theta": 0.45}})
    assert spec["overrides"] == {}  # 기본값과 같은 값은 뺌


def test_job_manager_runs_tiny_pipeline(csharp_cli, tmp_path):
    jm = JobManager(out_dir=tmp_path)
    job = jm.start(
        {"profile": "tiny", "seed": 2, "figures": False, "overrides": {"fans.enabled": False}}
    )
    with pytest.raises(JobError) as e:  # 한 번에 하나만
        jm.start({"profile": "tiny"})
    assert e.value.status == 409
    deadline = time.time() + 300
    while job.status in ("queued", "running") and time.time() < deadline:
        time.sleep(0.2)
    state = jm.job_state(job.id)
    assert state["status"] == "done", state["error"]
    assert state["progress"]["percent"] == 100.0
    # 단계 결과 줄은 기록 줄을 읽어야만 채워집니다 (finish 가 채우지 않음).
    result = {s["key"]: s["result"] for s in state["progress"]["stages"]}
    assert result["hero_solver"].startswith("[2단계] 솔버 끝"), result
    assert result["planet_solver"].startswith("[2단계] 솔버 끝"), result
    assert result["bake_finish"].startswith("[굽기] 끝"), result
    assert result["globe"].startswith("[지구본] 끝"), result
    saved = json.loads((job.run_dir / "job.json").read_text(encoding="utf-8"))
    assert saved["status"] == "done" and saved["stage_seconds"]["hero_solver"] >= 0
    assert saved["server_pid"] == os.getpid() and saved["cost"]["hero_cells"] == 64 * 64
    assert (job.run_dir / "job.log").read_text(encoding="utf-8").count("[굽기]") >= 3
    man = read_manifest(job.run_dir / "hero")
    assert man["config"]["fans"]["enabled"] is False and man["seed"] == 2
    listed = {r["key"]: r for r in list_runs(tmp_path)}
    assert listed[job.key]["source"] == "studio" and listed[job.key]["has"]["corridor"]
    assert resolve_run(job.key, tmp_path) == job.run_dir
    # 서버를 다시 켜면 남은 기록으로 상태를 읽습니다.
    again = JobManager(out_dir=tmp_path).job_state(job.id)
    assert again["status"] == "done" and again["log"]["lines"]


# ---------------------------------------------------------------- Godot (실행하지 않음)
def test_find_godot_uses_candidates_in_order(tmp_path, monkeypatch):
    fake = tmp_path / "Godot"
    fake.write_text("가짜", encoding="utf-8")
    other = tmp_path / "godot-old"
    other.write_text("가짜", encoding="utf-8")
    found = gd.find_godot([tmp_path / "없음", fake], check_version=False)
    assert found["ok"] and found["path"] == str(fake)
    versions = {str(other): "4.7.2.stable.official", str(fake): "4.7.2.stable.mono.official"}
    found = gd.find_godot([other, fake], check_version=True, version_fn=lambda p: versions[str(p)])
    assert found["ok"] and found["path"] == str(fake) and found["version"].startswith("4.7.2")
    assert found["seen"][0]["version"] == "4.7.2.stable.official"  # 표준판은 건너뜀
    none = gd.find_godot([other], check_version=True, version_fn=lambda p: "4.6.1")
    assert not none["ok"] and "4.7.2" in none["message"]
    assert not gd.find_godot([tmp_path / "없음"])["ok"]
    monkeypatch.setenv("GODOT_NET", str(fake))
    assert gd.candidates()[0] == fake


def test_isolated_env_points_home_to_temp(tmp_path):
    env = gd.isolated_env(tmp_path)
    assert Path(env["HOME"]).is_relative_to(tmp_path)
    names = ("APPDATA", "LOCALAPPDATA") if sys.platform == "win32" else ("XDG_CONFIG_HOME",)
    for name in names:
        assert Path(env[name]).is_relative_to(tmp_path)


def test_install_corridor_copies_manifest_files(runs, tmp_path):
    out, _ = runs
    corridor = out / "tiny" / "corridor"
    baked = tmp_path / "baked"
    baked.mkdir()
    # 이전 회랑: 이번에 없는 파일과 그 .import 는 지우고, 모르는 파일은 둡니다.
    (baked / "manifest.json").write_text(
        json.dumps({"format": "bpcg-corridor", "files": ["old_only.bin"]}), encoding="utf-8"
    )
    (baked / "old_only.bin").write_bytes(b"old")
    (baked / "old_only.bin.import").write_text("x")
    (baked / "keep_me.txt").write_text("사용자 파일", encoding="utf-8")
    marker = gd.install_corridor(corridor, baked, run_key="tiny")
    names = gd.corridor_files(corridor)
    assert "manifest.json" in names and "heightmap.bin" in names
    for n in names:
        assert (baked / n).read_bytes() == (corridor / n).read_bytes(), n
    assert not (baked / "old_only.bin").exists() and not (baked / "old_only.bin.import").exists()
    assert (baked / "keep_me.txt").exists()
    assert marker["run"] == "tiny" and set(marker["removed"]) == {
        "old_only.bin",
        "old_only.bin.import",
    }
    assert gd.read_marker(baked)["run"] == "tiny"
    assert not list(baked.glob("*.part"))


def test_install_corridor_brings_globe_and_drops_stale_one(runs, tmp_path):
    """행성이 있는 실행은 지구본(globe/)도 엔진으로 옮깁니다.

    평면 히어로 실행은 지난 지구본을 지웁니다.
    """
    out, logs = runs
    baked = tmp_path / "baked"
    marker = gd.install_corridor(out / "tiny" / "corridor", baked, run_key="tiny")
    src = out / "tiny" / "globe"
    assert marker["globe"] is True
    assert sorted(p.name for p in (baked / "globe").iterdir()) == sorted(
        p.name for p in src.iterdir()
    )
    assert (baked / "globe" / "globe.json").read_bytes() == (src / "globe.json").read_bytes()
    assert gd.baked_info(baked)["has_globe"]
    marker = gd.install_corridor(out / "flat" / "corridor", baked, run_key="flat")
    assert marker["globe"] is False and not (baked / "globe").exists()
    assert not list(baked.glob("*.part"))
    # 진행표: 지구본 단계는 행성 실행에서만 있고, 그 기록 줄에서 끝납니다.
    assert any(line.startswith("[지구본] 끝") for line in logs["tiny"])
    assert not any(line.startswith("[지구본]") for line in logs["flat"])


def test_install_corridor_refuses_incomplete_corridor(runs, tmp_path):
    out, _ = runs
    broken = tmp_path / "corridor"
    broken.mkdir()
    man = json.loads((out / "tiny" / "corridor" / "manifest.json").read_text(encoding="utf-8"))
    (broken / "manifest.json").write_text(json.dumps(man), encoding="utf-8")
    baked = tmp_path / "baked"
    with pytest.raises(gd.GodotError, match="없습니다"):
        gd.install_corridor(broken, baked, run_key="x")
    assert not baked.exists() or not any(baked.iterdir())
    man["files"] = ["../escape.bin"]
    (broken / "manifest.json").write_text(json.dumps(man), encoding="utf-8")
    with pytest.raises(gd.GodotError, match="올바르지"):
        gd.install_corridor(broken, baked)


# ---------------------------------------------------------------- 리뷰에서 나온 회귀 검사
def test_checked_overrides_rejects_non_finite_and_name_keys():
    cfg = load_config("earth", "tiny")
    for bad in (float("inf"), float("nan"), -float("inf")):
        with pytest.raises(ValueError, match="nan·inf"):
            checked_overrides(cfg, {"landscape.theta": bad})
    with pytest.raises(ValueError, match="nan·inf"):
        checked_overrides(cfg, {"plates.speed_m_per_yr": [0.02, float("nan")]})
    with pytest.raises(ValueError, match="nan·inf"):
        checked_overrides(cfg, {"landscape.theta": parse_assignment("k=inf")[1]})
    for key in ("planet.name", "profile.name"):
        with pytest.raises(ValueError, match="덮어쓸 수 없습니다"):
            checked_overrides(cfg, {key: "x"})
    assert checked_overrides(cfg, {"planet.seed": 3}) == {"planet.seed": 3}  # --seed 가 쓰는 길
    with pytest.raises(ValueError):
        toml_literal(float("nan"))


def test_cli_set_rejects_inf_and_name_keys(csharp_cli, tmp_path):
    base = ["planet", "--profile", "tiny", "--out", str(tmp_path), "--set"]
    assert "nan·inf" in _cli_error(base + ["landscape.stop_dz_m=inf"])
    assert "--planet" in _cli_error(base + ['planet.name="earth_dry"'])
    assert "--profile" in _cli_error(base + ['profile.name="laptop"'])
    assert not any(tmp_path.iterdir()), "검사에서 멈춰야 하는데 결과를 썼습니다"


def test_validate_request_checks_hero_and_corridor_area():
    def details(overrides):
        with pytest.raises(JobError) as e:
            validate_request({"profile": "tiny", "overrides": overrides})
        return e.value.details

    d = details({"profile.hero.spacing_m": 5000.0})  # 6400 < 3·5000
    assert "3·spacing_m" in d["profile.hero.spacing_m"]
    assert "3·spacing_m" in details({"profile.hero.size_m": 200.0})["profile.hero.size_m"]
    assert "상한" in details({"profile.hero.size_m": 600_000.0})["profile.hero.size_m"]
    assert "profile.corridor.voxel_m" in details({"profile.corridor.voxel_m": 0.0})
    assert "덮어쓸 수 없습니다" in details({"planet.name": "x"})["planet.name"]
    ok = validate_request({"profile": "tiny", "overrides": {"profile.hero.size_m": 6450.0}})
    assert ok["overrides"] == {"profile.hero.size_m": 6450.0}


def test_config_diff_keeps_keys_missing_from_run_apart():
    cfg = load_config("earth", "tiny", {"landscape.theta": 0.5}).as_dict()
    del cfg["landscape"]["k_ref"]  # 실행 뒤에 생긴 키처럼
    cfg["landscape"]["old_key"] = 1.0  # 지금 기본에는 없는 키처럼
    assert [d["key"] for d in config_diff(cfg)] == ["landscape.theta"]
    missing = {d["key"]: d for d in config_missing(cfg)}
    assert missing["landscape.k_ref"]["status"] == "added_since_run"
    assert missing["landscape.k_ref"]["value"] is None and missing["landscape.k_ref"]["default"]
    assert missing["landscape.old_key"]["status"] == "removed"


@pytest.mark.parametrize(
    "name, ok",
    [
        ("planet_globes.png", True),
        ("지형 그림.png", True),
        ("D:secret.png", False),  # 윈도우 드라이브 상대 경로
        ("x.png:stream", False),  # NTFS 대체 스트림
        ("../x.png", False),
        ("a/b.png", False),
        ("a\\b.png", False),
        (".hidden.png", False),
        ("", False),
        ("x\0.png", False),
    ],
)
def test_bare_name(name, ok):
    assert bare_name(name) is ok


def test_names_for_paths_and_run_folders(tmp_path):
    assert relative_name("surface/z_m.npy") and relative_name("z_m.npy")
    for bad in ("../x.npy", "/abs.npy", "C:/x.npy", "a//b.npy", "a/./b.npy", "a\\b.npy"):
        assert not relative_name(bad), bad
    assert run_folder_name("지형 테스트") and run_folder_name("earth_v2 copy")
    assert not run_folder_name(" lead") and not run_folder_name("D:x")
    with pytest.raises(FileNotFoundError):
        sm.figure_path(tmp_path, "D:x.png")
    cor = tmp_path / "corridor"
    cor.mkdir()
    (cor / "manifest.json").write_text(
        json.dumps({"format": "bpcg-corridor", "files": ["D:heightmap.bin"]}), encoding="utf-8"
    )
    with pytest.raises(gd.GodotError, match="올바르지"):
        gd.corridor_files(cor)


def test_external_run_names_with_spaces_and_korean_open(tmp_path):
    for name in ("지형 테스트", "earth_v2 copy", " lead"):
        (tmp_path / name / "hero").mkdir(parents=True)
        (tmp_path / name / "hero" / "manifest.json").write_text(
            json.dumps({"kind": "hero", "profile": "tiny", "seed": 0}), encoding="utf-8"
        )
    skipped: list = []
    keys = {r["key"] for r in list_runs(tmp_path, skipped)}
    assert {"지형 테스트", "earth_v2 copy"} <= keys and " lead" not in keys
    assert [s["name"] for s in skipped] == [" lead"] and skipped[0]["reason"]
    for key in ("지형 테스트", "earth_v2 copy"):
        assert resolve_run(key, tmp_path) == tmp_path / key
    for bad in ("../x", "a/b", "D:x", ".hidden", "studio", " lead", "studio/../x", "studio/a b"):
        with pytest.raises(JobError) as e:
            resolve_run(bad, tmp_path)
        assert e.value.status == 400, bad


# ---------------------------------------------------------------- 지도 (묶기·겹칠 것·판)
def test_views_hero_binning_paths(runs, tmp_path, monkeypatch):
    """f > 1 로 묶을 때: 연속값 평균, 참·거짓 최대, 범주 가운데 칸, 비트 범주 합침, 칸 정보 위치."""
    out, _ = runs
    monkeypatch.setattr(views_mod, "HERO_MAX_PX", 32)
    hero = ViewStore(tmp_path / "cache").level(out / "tiny", "hero")
    ny, nx = (int(v) for v in hero.graph["shape"])
    h, w, f = hero.hero_shape()
    assert f == 2 and (h, w) == (ny // 2, nx // 2)

    def blocks(name):
        return np.asarray(hero.values(name)).reshape(ny, nx)[: h * f, : w * f].reshape(h, f, w, f)

    z = blocks("z_m")
    assert np.allclose(hero.grid("z_m"), np.nanmean(z, axis=(1, 3)), rtol=1e-5, equal_nan=True)
    assert np.array_equal(hero.grid("is_river"), blocks("is_river").max(axis=(1, 3)))
    rock = np.asarray(hero.values("surface_rock")).reshape(ny, nx)
    assert np.array_equal(hero.grid("surface_rock"), rock[1::2, 1::2][:h, :w])
    # 묶음 안에 아래층(1)과 위층(2) 입구가 섞이면 '두 층'(3) 이어야 합니다 (최대값이면 2).
    synth = np.zeros(ny * nx)
    synth[0], synth[nx] = 1, 2  # 첫 2×2 묶음
    synth[2] = 1  # 둘째 묶음: 아래층만
    real_values = hero.values
    monkeypatch.setattr(hero, "values", lambda n: synth if n == "cave_entrance" else real_values(n))
    g = hero.grid("cave_entrance")
    assert g[0, 0] == 3 and g[0, 1] == 1 and g[1, 1] == 0
    # 칸 정보는 그 픽셀의 가운데 칸을 봅니다.
    probe = hero.probe(3, 4)
    assert (probe["where"]["row"], probe["where"]["col"]) == (4 * f + 1, 3 * f + 1)


def test_planet_probe_is_near_pixel_lat_lon(runs, tmp_path):
    out, _ = runs
    planet = ViewStore(tmp_path / "cache").level(out / "tiny", "planet")
    n = int(planet.graph["shape"][1])
    tol = 1.5 * 90.0 / n  # 큐브 면 한 칸의 각 크기 정도
    for px, py in ((10, PLANET_H // 2), (PLANET_W // 3, PLANET_H // 3), (900, 300)):
        lat = 90.0 - (py + 0.5) * 180.0 / PLANET_H
        lon = -180.0 + (px + 0.5) * 360.0 / PLANET_W
        where = planet.probe(px, py)["where"]
        dlon = (where["lon"] - lon + 180.0) % 360.0 - 180.0
        assert abs(where["lat"] - lat) < tol and abs(dlon) * np.cos(np.radians(lat)) < tol


def test_view_overlays_and_version_follow_later_stages(runs, tmp_path):
    """실행 도중 행성 지도를 열어도, 나중에 생긴 히어로 자리·회랑 사각형이 지도에 나옵니다."""
    out, _ = runs
    src, run = out / "tiny", tmp_path / "run"
    run.mkdir()
    shutil.copytree(src / "planet", run / "planet")
    store = ViewStore(tmp_path / "cache")
    first = store.level_meta(run, "planet")
    assert first["markers"] == [] and first["version"]
    shutil.copytree(src / "hero", run / "hero")
    shutil.copytree(src / "corridor", run / "corridor")
    again = store.level_meta(run, "planet")
    assert again["markers"] and again["markers"][0]["label"] == "히어로 유역"
    assert again["version"] != first["version"]
    assert store.level_meta(run, "hero")["rects"]
    # 같은 단계 묶음은 다시 읽지 않습니다 (격자 캐시 열쇠는 manifest mtime).
    assert store.level(run, "planet") is store.level(run, "planet")


def test_field_meta_always_has_positive_min_and_sensible_uplift_range(runs, tmp_path):
    out, _ = runs
    store = ViewStore(tmp_path / "cache")
    for name in ("z_m", "is_ocean", "uplift_m_per_yr", "exhumation_m"):
        meta = store.field_meta(out / "tiny", "planet", name)
        assert "positive_min" in meta["stats"], name
    up = store.field_meta(out / "tiny", "planet", "uplift_m_per_yr")
    v = store.level(out / "tiny", "planet").values("uplift_m_per_yr") * up["factor"]
    nz = v[np.isfinite(v) & (v != 0)]
    assert up["range"][1] == pytest.approx(np.percentile(nz, 98))  # 바다 0 을 빼고 잡음
    assert store.field_meta(out / "tiny", "planet", "exhumation_m")["scale"] == "log"
    assert store.field_meta(out / "tiny", "planet", "z_platform_m")["cmap"] != "terrain"


# ---------------------------------------------------------------- 개요 (점수표·경고·시간)
def test_scorecard_keeps_checks_that_failed_to_compute():
    planet = {
        "planet.law_consistency": {
            "value": None, "pass": False, "kind": "check", "note": "계산 실패: 빈 배열",
        },
        "planet.water_budget": {"value": 1e-9, "pass": True, "kind": "check"},
        "planet.hack_exponent": {"value": None, "pass": None, "kind": "emergent", "note": "건너뜀"},
        "planet.ocean_fraction": {"value": 0.7, "pass": None, "kind": "emergent"},
    }  # fmt: skip
    rows = {r["key"]: r for r in sm._scorecard_rows(planet, None)}
    assert "law_consistency" in rows and rows["law_consistency"]["planet"]["pass"] is False
    assert "hack_exponent" not in rows
    assert rows["ocean_fraction"]["earth"] == f"{sm.EARTH_OCEAN_FRACTION:g}"  # Scorecard.cs 기준값
    failed = sm.failed_checks(planet, None)
    assert [f["key"] for f in failed] == ["planet.law_consistency"] and failed[0]["compute_failed"]
    warns = sm._warnings(None, None, None, None, {}, {}, failed)
    assert any("불합격" in w["message"] and "계산 실패" in w["message"] for w in warns)


def test_summary_warns_on_unconverged_strength_limit_and_labels_corridor_times():
    pdiag = {
        "solver": {"converged": True, "iterations": 50},
        "stages": {
            "strength_limit": {
                "applied": True, "first_pass_converged": False, "first_pass_iterations": 200,
                "first_pass_z_mean_max_m": 14559.0, "reduced_cells": 53008,
            }
        },
    }  # fmt: skip
    warns = sm._warnings(None, None, None, None, pdiag, {})
    assert any("지각 세기 한계" in w["message"] and "200" in w["message"] for w in warns)
    secs = {"caves": 10.0, "water": 0.6, "cave_mouth": 0.3, "detail": 1.4, "volume": 1.2}
    rows = sm._seconds_rows(secs, sm.CORRIDOR_SECONDS_LABELS)
    labels = {r["key"]: r["label"] for r in rows}
    assert labels["caves"].startswith("동굴 메시") and labels["water"].startswith("수면")
    assert all(r["label"] != r["key"] for r in rows), labels
    assert "검은 곳은 마른 동굴" in sm.FIGURE_TEXT["cross_section.png"]["how"]


# ---------------------------------------------------------------- 실행 관리 (가짜 하위 프로세스)
def _stub(jm: JobManager, code: str) -> None:
    jm.commands_for = lambda spec, run_dir: [[sys.executable, "-u", "-c", code]]


def _wait(job, timeout: float = 60.0) -> None:
    deadline = time.time() + timeout
    while job.status in ("queued", "running") and time.time() < deadline:
        time.sleep(0.05)


def test_job_manager_start_is_atomic_and_cancel_stops_child(tmp_path):
    jm = JobManager(out_dir=tmp_path)
    _stub(jm, "import time; print('[행성] 시작: x', flush=True); time.sleep(30)")
    # 준비를 느리게 해서 두 요청이 모두 첫 검사를 지나게 합니다 (예전 코드면 둘 다 시작).
    jm._history_weights = lambda spec, cost=None: time.sleep(0.3)
    barrier = threading.Barrier(2)
    results: list = []

    def go():
        barrier.wait()
        try:
            results.append(jm.start({"profile": "tiny", "figures": False}))
        except JobError as e:
            results.append(e)

    threads = [threading.Thread(target=go) for _ in range(2)]
    for t in threads:
        t.start()
    for t in threads:
        t.join(30)
    started = [r for r in results if not isinstance(r, JobError)]
    refused = [r for r in results if isinstance(r, JobError)]
    assert len(started) == 1 and len(refused) == 1 and refused[0].status == 409
    job = started[0]
    deadline = time.time() + 30
    while job.proc is None and time.time() < deadline:
        time.sleep(0.05)
    pid = job.proc.pid
    jm.cancel(job.id)
    _wait(job, 30)
    assert job.status == "cancelled" and jm.active() is None
    assert not pid_alive(pid)


def test_job_manager_reports_failed_subprocess(tmp_path):
    jm = JobManager(out_dir=tmp_path)
    _stub(jm, "import sys; print('[행성] 시작: x', flush=True); sys.exit(3)")
    job = jm.start({"profile": "tiny", "figures": False})
    _wait(job)
    st = jm.job_state(job.id)
    assert st["status"] == "failed" and st["returncode"] == 3
    assert "종료 코드 3" in st["error"] and st["progress"]["state"] == "failed"
    assert jm.active() is None


def test_job_survives_job_json_write_errors(tmp_path, monkeypatch):
    """job.json 을 못 써도(윈도우 파일 잠금) 실행이 '도는 중' 으로 굳지 않고 끝납니다."""

    def locked(path, obj):
        raise PermissionError(13, "다른 프로그램이 파일을 쓰는 중", str(path))

    monkeypatch.setattr("bpcg_studio.jobs.write_json", locked)
    jm = JobManager(out_dir=tmp_path)
    _stub(jm, "print('[행성] 시작: x', flush=True); print('끝', flush=True)")
    job = jm.start({"profile": "tiny", "figures": False})
    _wait(job)
    assert job.status == "done" and jm.active() is None
    assert "PermissionError" in job.save_error


def test_job_error_while_reading_output_stops_child(tmp_path, monkeypatch):
    pid_file = tmp_path / "child.pid"
    code = (
        f"import os, time; open({str(pid_file)!r}, 'w').write(str(os.getpid())); "
        "print('boom', flush=True); time.sleep(30)"
    )
    orig = ProgressTracker.feed

    def feed(self, line, t=None):
        if line == "boom":
            raise RuntimeError("읽기 오류")
        return orig(self, line, t)

    monkeypatch.setattr(ProgressTracker, "feed", feed)
    jm = JobManager(out_dir=tmp_path)
    _stub(jm, code)
    job = jm.start({"profile": "tiny", "figures": False})
    _wait(job, 30)
    assert job.status == "failed" and "RuntimeError" in job.error
    assert jm.active() is None and not pid_alive(int(pid_file.read_text()))


def _write_job(dir_: Path, info: dict) -> None:
    dir_.mkdir(parents=True)
    (dir_ / "job.json").write_text(json.dumps(info), encoding="utf-8")


def test_restart_marks_dead_server_jobs_interrupted(tmp_path):
    tr = ProgressTracker(weights={s.key: 10.0 for s in STAGES})
    tr.start(0.0)
    tr.feed("[행성] 시작: 프로필 tiny, 시드 0", 1.0)
    snap = tr.snapshot(2.0)
    assert snap["eta_s"] is not None and snap["current"] is not None
    dead = subprocess.Popen([sys.executable, "-c", "pass"])
    dead.wait()
    base = {"status": "running", "spec": {"profile": "tiny", "flat": False}, "progress": snap}
    _write_job(tmp_path / "studio" / "a", {**base, "id": "a", "server_pid": dead.pid})
    _write_job(tmp_path / "studio" / "old", {**base, "id": "old"})  # 예전 형식 (pid 없음)
    _write_job(tmp_path / "studio" / "b", {**base, "id": "b", "server_pid": os.getppid()})
    jm = JobManager(out_dir=tmp_path)
    for key in ("a", "old"):
        st = jm.job_state(key)
        pr = st["progress"]
        assert st["status"] == "interrupted" and st["finished"], key
        assert pr["state"] == "interrupted" and pr["eta_s"] is None and pr["current"] is None
        assert not [s for s in pr["stages"] if s["status"] == "running"]
        assert any(s["status"] == "failed" and "서버" in s["note"] for s in pr["stages"])
    # 살아 있는 다른 서버(pid)의 실행은 건드리지 않고, 그동안 새 실행을 막습니다.
    assert jm.job_state("b")["status"] == "running"
    with pytest.raises(JobError) as e:
        jm.start({"profile": "tiny"})
    assert e.value.status == 409 and "다른 스튜디오 서버" in str(e.value)


def test_history_weights_scale_with_run_size(tmp_path):
    jm = JobManager(out_dir=tmp_path)
    cost = cost_of(load_config("earth", "tiny"))
    secs = {"hero_solver": 10.0, "planet_solver": 2.0, "bake_caves": 3.0}
    spec = {"planet": "earth", "profile": "tiny", "flat": False, "overrides": {}}
    _write_job(tmp_path / "studio" / "old", {"status": "done", "spec": spec, "stage_seconds": secs})
    want = {"profile": "tiny", "flat": False}
    assert jm._history_weights(want, cost) == secs  # 크기 기록이 없으면 설정에서 다시 셈
    big = {**cost, "hero_cells": 4 * cost["hero_cells"]}
    scaled = jm._history_weights(want, big)
    assert scaled["hero_solver"] == pytest.approx(40.0)
    assert scaled["planet_solver"] == pytest.approx(2.0) and scaled["bake_caves"] == 3.0
    assert jm._history_weights({"profile": "laptop", "flat": False}, cost) is None


# ---------------------------------------------------------------- 서버 (보안·약속한 응답)
@contextlib.contextmanager
def _serving(app: Studio):
    server = create_server(0, app)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield server, f"http://127.0.0.1:{server.server_address[1]}"
    finally:
        server.shutdown()
        server.server_close()


def _raw_post(port: int, path: str, body: bytes, headers: dict) -> tuple[int, dict]:
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
    try:
        conn.putrequest("POST", path, skip_accept_encoding=True)
        for k, v in {"Content-Type": "application/json", **headers}.items():
            conn.putheader(k, v)
        conn.endheaders()
        conn.send(body)
        conn.sock.shutdown(1)  # 쓰기 끝 (Content-Length 가 음수여도 서버가 끝없이 기다리지 않게)
        r = conn.getresponse()
        return r.status, json.loads(r.read().decode("utf-8"))
    finally:
        conn.close()


def test_server_security_headers_and_contract(runs, tmp_path):
    out, _ = runs
    with _serving(Studio(out_dir=out, baked_dir=tmp_path / "baked")) as (server, base):
        port = server.server_address[1]
        with urllib.request.urlopen(base + "/", timeout=10) as r:
            assert r.headers["X-Frame-Options"] == "DENY"
            assert "frame-ancestors 'none'" in r.headers["Content-Security-Policy"]
        meta = _get(base, "/api/meta")
        assert meta["max_hero_side"] == 5000 and meta["hero_side_rule"] == "round_half_even"
        assert "skipped" in _get(base, "/api/runs")
        level = _get(base, "/api/level?run=tiny&level=hero")
        assert isinstance(level["version"], str) and level["version"]
        v = level["version"]
        fm = _get(base, f"/api/field?run=tiny&level=hero&name=z_m&v={v}")
        assert "positive_min" in fm["stats"]
        data = _get(base, f"/api/field.bin?run=tiny&level=hero&name=z_m&v={v}", raw=True)
        assert len(data) == level["width"] * level["height"] * 4
        summary = _get(base, "/api/run?run=tiny")
        assert summary["profile"] == "tiny" and summary["config_flat"]["landscape.theta"] == 0.5
        assert "profile.name" not in summary["config_flat"]
        assert summary["config_flat"]["profile.hero.size_m"] == 6400.0
        assert summary["diff_missing"] == []
        bad = _post(
            base, "/api/validate", {"profile": "tiny", "overrides": {"profile.hero.spacing_m": 5e3}}
        )
        assert not bad["ok"] and "profile.hero.spacing_m" in bad["errors"]
        # 같은 화면에서 온 POST 는 받고, 다른 사이트에서 온 POST 는 막습니다.
        ok_body = json.dumps({"profile": "tiny"}).encode()
        n = str(len(ok_body))
        same = {"Origin": base, "Content-Length": n}
        assert _raw_post(port, "/api/validate", ok_body, same)[0] == 200
        evil = {"Origin": "http://evil.example", "Content-Length": n}
        assert _raw_post(port, "/api/validate", ok_body, evil)[0] == 403
        cross = {"Sec-Fetch-Site": "cross-site", "Content-Length": n}
        assert _raw_post(port, "/api/validate", ok_body, cross)[0] == 403
        # 본문 길이가 음수·글자이면 400 (예전에는 음수면 1 MB 상한 없이 연결 끝까지 읽고 200)
        status, body = _raw_post(port, "/api/validate", ok_body, {"Content-Length": "-1"})
        assert status == 400 and "Content-Length" in body["error"]
        assert _raw_post(port, "/api/validate", ok_body, {"Content-Length": "abc"})[0] == 400
        # JSON 의 NaN·Infinity 는 받지 않습니다.
        nan_body = b'{"profile": "tiny", "overrides": {"landscape.theta": NaN}}'
        status, body = _raw_post(port, "/api/validate", nan_body, {"Content-Length": "58"})
        assert status == 400 and "NaN" in body["error"]


def test_server_logs_client_disconnect_quietly():
    server = StudioHTTPServer(("127.0.0.1", 0), BaseHTTPRequestHandler)
    try:
        quiet = io.StringIO()
        with contextlib.redirect_stderr(quiet):
            try:
                raise ConnectionResetError(54, "Connection reset by peer")
            except ConnectionResetError:
                server.handle_error(None, ("127.0.0.1", 50000))
        assert "Traceback" not in quiet.getvalue()
        assert quiet.getvalue().count("\n") == 1 and "끊었습니다" in quiet.getvalue()
        loud = io.StringIO()
        with contextlib.redirect_stderr(loud):
            try:
                raise ValueError("진짜 오류")
            except ValueError:
                server.handle_error(None, ("127.0.0.1", 50000))
        assert "Traceback" in loud.getvalue() and "진짜 오류" in loud.getvalue()
    finally:
        server.server_close()
    assert StudioHTTPServer.allow_reuse_address == (sys.platform != "win32")


def test_second_server_on_busy_port_fails_before_touching_jobs(tmp_path, monkeypatch):
    first = create_server(0, Studio(out_dir=tmp_path, baked_dir=tmp_path / "baked"))
    made: list = []
    monkeypatch.setattr(server_mod, "Studio", lambda *a, **k: made.append(1))
    try:
        with pytest.raises(OSError):
            create_server(first.server_address[1])
        assert made == [], "포트를 열기 전에 Studio(=job.json 고치기)를 만들었습니다"
    finally:
        first.server_close()


# ---------------------------------------------------------------- engine/baked 상태
def test_baked_info_reports_cli_bake_without_marker(runs, tmp_path):
    out, _ = runs
    assert gd.baked_info(tmp_path / "없음") is None
    baked = tmp_path / "baked"
    baked.mkdir()
    man = json.loads((out / "tiny" / "corridor" / "manifest.json").read_text(encoding="utf-8"))
    (baked / "manifest.json").write_text(json.dumps(man), encoding="utf-8")
    info = gd.baked_info(baked)
    assert info["origin"] == "cli" and info["run"].startswith("명령으로 구움")
    assert info["profile"] == "tiny" and info["config_digest"] == man["config_digest"]
    assert info["git_commit"] == man.get("git_commit")
    assert not info["has_globe"] and "지구본 없음" in info["summary"]
    (baked / "heightmap_detail.bin").write_bytes(b"x")
    (baked / "globe").mkdir()
    (baked / "globe" / "globe.json").write_text("{}", encoding="utf-8")
    info = gd.baked_info(baked)
    assert info["has_detail"] and info["has_globe"] and "지구본 있음" in info["summary"]
    # 스튜디오가 복사하면 표시 파일로, 그 뒤 명령으로 다시 구우면(설정 해시가 다름) 다시 명령으로.
    gd.install_corridor(out / "tiny" / "corridor", baked, run_key="tiny")
    info = gd.baked_info(baked)
    assert info["origin"] == "studio" and info["run"] == "tiny"
    man["config_digest"] = "다시구움"
    (baked / "manifest.json").write_text(json.dumps(man), encoding="utf-8")
    assert gd.baked_info(baked)["origin"] == "cli"
