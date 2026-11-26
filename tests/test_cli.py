"""명령줄 검사 (docs/pipeline.md 13장): C# 콘솔 bpcg planet·hero·bake·all 과 Python 입구 `bpcg`.

tiny 프로필로 전체를 몇 초 안에 돌립니다.
"""

import json
import shutil
import subprocess

import numpy as np
import pytest
from bundles import CONFIGS, ROOT, cli_env, load_bundle, read_json, run_cli

from bpcg_studio.cli import main as bpcg_main
from bpcg_studio.csharp import cli_command

CORRIDOR_FILES = (
    "heightmap.bin",
    "heightmap.json",
    "surround25.bin",
    "water.bin",
    "water_table.bin",
    "water_table_lower.bin",
    "strata.u8",
    "strata.json",
    "manifest.json",
)


def test_all_matches_independent_commands(csharp_cli, tiny_run, tmp_path):
    csharp_cli("planet", "--profile", "tiny", "--seed", "0", "--out", tmp_path)
    csharp_cli(
        "hero", "--profile", "tiny", "--seed", "0", "--from", tmp_path / "planet", "--out", tmp_path
    )
    csharp_cli("bake", "--hero", tmp_path / "hero")
    for expected in tiny_run.rglob("*"):
        if expected.suffix == ".npy":
            actual = tmp_path / expected.relative_to(tiny_run)
            np.testing.assert_array_equal(np.load(expected), np.load(actual))
        elif expected.suffix in {".bin", ".u8", ".png"}:
            actual = tmp_path / expected.relative_to(tiny_run)
            assert expected.read_bytes() == actual.read_bytes(), expected.relative_to(tiny_run)


@pytest.mark.slow  # 따로 C# 실행을 돌려 몇 초 걸림 (CI 는 돌림)
def test_single_and_auto_thread_results_match(csharp_cli, tmp_path):
    outputs = []
    modes = (
        ("single", "1"),
        ("threads2", "2"),
        ("threads4", "4"),
        ("auto", "0"),
        ("auto_repeat", "0"),
    )
    for mode, threads in modes:
        run = tmp_path / mode
        proc = csharp_cli(
            "all",
            "--profile",
            "tiny",
            "--seed",
            "1738104521",
            "--out",
            run,
            "--set",
            f"profile.compute.numba_threads={threads}",
        )
        outputs.append(run)
        assert "[전체] 끝" in proc.stdout
    baseline, *others = outputs
    # 스레드 수는 결과를 바꾸지 않으므로 설정 해시(config_digest)도 같아야 합니다.
    # 목차의 설정(config)에는 스레드 수가 그대로 남습니다.
    indexes = ("planet/manifest.json", "hero/manifest.json", "corridor/manifest.json")
    digests = {read_json(run / i)["config_digest"] for run in outputs for i in indexes}
    digests |= {read_json(run / "globe" / "globe.json")["config_digest"] for run in outputs}
    assert len(digests) == 1, digests
    configs = [read_json(run / "planet" / "manifest.json")["config"] for run in outputs]
    assert {c["profile"]["compute"]["numba_threads"] for c in configs} == {0, 1, 2, 4}
    for expected in baseline.rglob("*"):
        if expected.suffix in {".npy", ".bin", ".u8", ".png"}:
            for output in others:
                actual = output / expected.relative_to(baseline)
                if expected.suffix == ".npy":
                    got = np.load(actual)
                    want = np.load(expected)
                    np.testing.assert_array_equal(got, want)
                    if expected.name in {
                        "z_m.npy",
                        "uplift_m_per_yr.npy",
                        "temperature_c.npy",
                        "precip_m_per_yr.npy",
                        "discharge_m3_per_yr.npy",
                        "water_table_m.npy",
                    }:
                        assert np.isfinite(got).all(), expected.relative_to(baseline)
                else:
                    assert expected.read_bytes() == actual.read_bytes(), expected.relative_to(
                        baseline
                    )


def test_all_tiny_end_to_end(tiny_run, planet, hero):
    pm = planet.manifest
    assert pm["kind"] == "planet" and pm["face_basis"] is not None
    assert (tiny_run / "planet" / "textures" / "textures.json").exists()
    assert (tiny_run / "planet" / "scorecard.json").exists()
    hm = hero.manifest
    assert hm["kind"] == "hero" and hm["config_digest"] == pm["config_digest"]
    for name in CORRIDOR_FILES:
        assert (tiny_run / "corridor" / name).exists(), name
    cm = read_json(tiny_run / "corridor" / "manifest.json")
    assert cm["config_digest"] == pm["config_digest"]
    assert cm["seed"] == 0
    # 지구본: 같은 실행의 행성 묶음으로 굽고, 히어로 자리는 히어로 묶음에서 찾음
    gj = read_json(tiny_run / "globe" / "globe.json")
    assert gj["format"] == "bpcg-globe" and gj["config_digest"] == pm["config_digest"]
    assert gj["hero"] is not None and len(gj["fields"]) > 5
    # 묶음마다 만들 때 읽은 위 단계 묶음의 표지(parent)를 적습니다.
    # 행성 → 히어로 → 회랑, 행성·히어로 → 지구본.
    planet_id = {"kind": "planet", "config_digest": pm["config_digest"], "seed": 0}
    hero_id = {"kind": "hero", "config_digest": hm["config_digest"], "seed": 0, "parent": planet_id}
    assert pm["parent"] is None and hm["parent"] == planet_id
    assert cm["parent"] == hero_id
    assert gj["parent"] == planet_id and gj["hero_bundle"] == hero_id


def test_bake_regenerates_globe_unless_no_globe(csharp_cli, tiny_run, tmp_path):
    run = tmp_path / "copy"
    shutil.copytree(tiny_run, run)
    (run / "globe" / "globe.json").unlink()
    csharp_cli("bake", "--hero", run / "hero", "--no-globe")
    assert not (run / "globe" / "globe.json").exists()
    csharp_cli("bake", "--hero", run / "hero")
    assert (run / "globe" / "globe.json").exists()


def test_bake_stops_when_the_planet_beside_the_hero_is_from_another_run(
    csharp_cli, tiny_run, tmp_path
):
    """히어로 옆의 행성 묶음이 그 히어로를 만든 행성이 아니면 굽기 전에 멈춥니다.

    멈출 때 받은 값과 고치는 법을 알립니다.

    --no-globe 로 구우면 지구본을 굽지 않으므로 회랑만 굽습니다.
    """
    run = tmp_path / "mixed"
    csharp_cli("planet", "--profile", "tiny", "--seed", "1", "--out", run)
    shutil.copytree(tiny_run / "hero", run / "hero")  # 시드 0 행성에서 만든 히어로
    proc = csharp_cli("bake", "--hero", run / "hero", check=False)
    assert proc.returncode == 1, proc.stderr
    assert "지구본을 굽지 못합니다" in proc.stderr
    assert "다른 행성(시드 0," in proc.stderr and "(시드 1," in proc.stderr
    assert "--no-globe" in proc.stderr and "bpcg hero --from" in proc.stderr
    assert not (run / "corridor").exists() and not (run / "globe").exists()
    csharp_cli("bake", "--hero", run / "hero", "--no-globe")
    assert (run / "corridor" / "manifest.json").is_file() and not (run / "globe").exists()


def test_rewriting_a_run_folder_leaves_no_stage_from_the_old_run(csharp_cli, tiny_run, tmp_path):
    """같은 실행 폴더를 다시 쓰면, 이번 실행과 이어지지 않는 지난 단계 폴더가 남지 않습니다.

    히어로를 다시 만들면 회랑·지구본을 지우고, 평면 히어로는 다른 행성이 있는 폴더에 쓰지 않습니다.
    """
    run = tmp_path / "copy"
    shutil.copytree(tiny_run, run)
    proc = csharp_cli(
        "hero", "--from", run / "planet", "--set", "hero.site_lat_lon_deg=[8.39, -0.92]"
    )
    assert "[실행 폴더] 묵은 단계 폴더 2개를 지웠습니다: corridor, globe" in proc.stdout
    assert not (run / "corridor").exists() and not (run / "globe").exists()
    proc = csharp_cli("hero", "--flat", "--profile", "tiny", "--out", run, check=False)
    assert proc.returncode == 1
    assert "행성 묶음이 있습니다(시드 0," in proc.stderr and "다른 --out 폴더" in proc.stderr
    assert read_json(run / "hero" / "manifest.json")["parent"]["kind"] == "planet"


def test_hero_flat_then_bake(flat_run, flat_hero):
    hm = flat_hero.manifest
    assert hm["seed"] == 3 and hm["meta"]["site"] is None
    cm = read_json(flat_run / "corridor" / "manifest.json")
    assert cm["site"]["kind"] == "flat_hero" and cm["seed"] == 3
    assert not (flat_run / "globe").exists()  # 행성이 없는 실행은 지구본도 없음


def test_hero_from_planet_bundle(csharp_cli, tiny_run, hero, tmp_path):
    """hero --from <행성 묶음> 은 all 과 같은 히어로를 만듭니다."""
    csharp_cli("hero", "--from", tiny_run / "planet", "--out", tmp_path)
    again = load_bundle(tmp_path / "hero")
    for name in ("z_m", "receiver", "surface_rock", "water_table_m"):
        assert (again[name] == hero[name]).all(), name


def test_hero_from_moves_site_without_new_planet(csharp_cli, tiny_run, hero, tmp_path):
    """hero --from 의 --set 은 hero. 키만 받습니다: 행성은 그대로 두고 히어로 자리만 옮깁니다."""
    auto = hero.manifest["meta"]["site"]
    proc = csharp_cli(
        "hero", "--from", tiny_run / "planet", "--out", tmp_path,
        "--set", "hero.site_lat_lon_deg=[8.39, -0.92]",
    )  # fmt: skip
    assert "설정 hero.site_lat_lon_deg [8.39, -0.92] 에서 가장 가까운 칸" in proc.stdout
    moved = read_json(tmp_path / "hero" / "manifest.json")["meta"]["site"]
    assert moved["l0_cell"] != auto["l0_cell"]
    assert abs(moved["lat_deg"] - 8.39) < 0.01 and abs(moved["lon_deg"] + 0.92) < 0.01
    assert not (tmp_path / "planet").exists()

    proc = csharp_cli(
        "hero", "--from", tiny_run / "planet", "--out", tmp_path,
        "--set", "landscape.theta=0.5", check=False,
    )  # fmt: skip
    assert proc.returncode != 0
    assert "hero --from 에서는 hero. 로 시작하는 키만" in proc.stderr


def test_help_lists_commands_and_options(csharp_cli):
    """bpcg --help 는 오류 없이 명령과 옵션을 모두 보여 줍니다."""
    for args in (["--help"], ["all", "-h"], ["help"]):
        proc = csharp_cli(*args)
        assert proc.stdout.startswith("usage: bpcg"), args
        for word in ("all", "planet", "hero", "bake", "noise", "compare", "figures", "studio"):
            assert f"\n  {word} " in proc.stdout, word
        for flag in ("--planet", "--profile", "--seed", "--out", "--set", "--from", "--flat",
                     "--hero", "--engine", "--no-globe"):  # fmt: skip
            assert f"\n  {flag} " in proc.stdout, flag


def test_noise_world_writes_the_same_engine_files(csharp_cli, tmp_path):
    """bpcg noise: 같은 프로필·시드로 노이즈 비교본을 만들어, B-PCG 와 같은 파일 꼴로 씁니다."""
    run = tmp_path / "noise"
    proc = csharp_cli(
        "noise", "--profile", "tiny", "--seed", "0", "--out", run,
        "--set", "noise.caves.tunnel_radius_m=3.0",
    )  # fmt: skip
    lines = proc.stdout.strip().splitlines()
    assert lines[-1].startswith("[노이즈] 끝: "), lines[-1]
    assert "[설정] noise.caves.tunnel_radius_m = 3.0 (기본 2.6)" in proc.stdout
    for name in CORRIDOR_FILES:
        assert (run / "corridor" / name).is_file(), name
    assert (run / "globe" / "globe.json").is_file()
    assert not (run / "planet").exists() and not (run / "hero").exists()
    man = read_json(run / "corridor" / "manifest.json")
    assert man["generator"] == "noise" and man["seed"] == 0 and man["profile"] == "tiny"
    noise = read_json(run / "noise" / "manifest.json")
    assert noise["config"]["noise"]["caves"]["tunnel_radius_m"] == 3.0
    assert abs(noise["planet"]["ocean_fraction"] - 0.71) < 0.005


def test_noise_rejects_unknown_noise_key(csharp_cli, tmp_path):
    """노이즈 설정에 없는 키는 비슷한 키를 알려 주고 멈춥니다 (스택 추적 없이)."""
    proc = csharp_cli(
        "noise", "--profile", "tiny", "--out", tmp_path / "x",
        "--set", "noise.caves.tunnel_radius=3",
        check=False,
    )  # fmt: skip
    assert proc.returncode == 1
    assert "noise.caves.tunnel_radius" in proc.stderr and "비슷한 키" in proc.stderr
    assert "at Bpcg" not in proc.stderr


def test_rejects_missing_inputs(csharp_cli, tmp_path):
    cases = [
        (["hero", "--profile", "tiny", "--out", tmp_path], "--from"),
        (["hero", "--from", tmp_path / "nothing"], "행성 묶음을 찾지 못했습니다"),
        (["bake", "--hero", tmp_path / "nothing"], "히어로 묶음을 찾지 못했습니다"),
        ([], "usage: bpcg"),
        (
            ["all", "--profile", "tiny", "--set", "landscape.thetaa=0.5"],
            "비슷한 키: landscape.theta",
        ),
        (["all", "--profile", "tiny", "--set", "planet.name=mars"], "덮어쓸 수 없습니다"),
        (["all", "--profile", "preview"], "쓸 수 있는 이름은 lab, laptop, tiny 입니다"),
        (["planet", "--planet", "venus"], "쓸 수 있는 이름은 earth, mars, small10 입니다"),
    ]
    for args, message in cases:
        proc = csharp_cli(*args, check=False)
        assert proc.returncode != 0, args
        assert message in proc.stderr, (args, proc.stderr)


def test_bake_set_only_bake_keys(csharp_cli, cave_run, tmp_path):
    proc = run_cli(
        "bake", "--hero", cave_run / "hero", "--out", tmp_path, "--no-globe",
        "--set", "landscape.theta=0.5", check=False,
    )  # fmt: skip
    assert proc.returncode != 0
    assert "bake 에서는 detail., profile.corridor. 로 시작하는 키만" in proc.stderr


def test_bake_of_a_bundle_without_newer_keys_says_how_to_fix(csharp_cli, cave_run, tmp_path):
    """굽기가 읽는 설정 키가 없는 히어로 묶음은 고치는 법을 알려 주고 멈춥니다.

    그 키가 생기기 전의 코드로 만든 묶음이 여기에 들어갑니다.
    """
    old_hero = tmp_path / "hero"
    shutil.copytree(cave_run / "hero", old_hero)
    manifest = json.loads((old_hero / "manifest.json").read_text(encoding="utf-8"))
    del manifest["config"]["caves"]["sink_spacing_m"]
    (old_hero / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False), encoding="utf-8"
    )
    proc = run_cli("bake", "--hero", old_hero, "--out", tmp_path / "out", "--no-globe", check=False)
    assert proc.returncode == 1
    assert "caves.sink_spacing_m" in proc.stderr
    assert "다시 돌려" in proc.stderr and "Unhandled exception" not in proc.stderr


def test_subcommand_help_lists_commands_and_options(csharp_cli):
    """bpcg compare --help 와 bpcg figures --help 도 오류 없이 명령과 옵션을 모두 보여 줍니다."""
    cases = (
        (["compare", "--help"], ("methods", "run", "list", "render", "--json", "--config", "--out",
                                 "--seeds", "--only", "--param", "--force", "--set", "--seed",
                                 "--method", "--scale", "--stage")),
        (["compare", "run", "-h"], ("--param", "--force")),
        (["compare", "help"], ("render",)),
        (["figures", "--help"], ("results", "cubesphere", "--out", "--etopo", "--n", "--R")),
        (["figures", "results", "-h"], ("--etopo",)),
    )  # fmt: skip
    for args, words in cases:
        proc = csharp_cli(*args)
        assert proc.stdout.startswith(f"usage: bpcg {args[0]}"), args
        for word in words:
            assert f"\n  {word} " in proc.stdout, (args, word)


def test_bad_setting_values_stop_with_a_message_not_a_stack_trace(csharp_cli, tmp_path):
    """범위를 벗어난 설정 값은 스택 추적 대신 메시지와 종료 코드 1 로 멈춥니다.

    마지막 두 사례는 칸마다 나눠 도는 반복 안에서 나는 오류입니다. 스레드 수가 달라도 같은 메시지가
    한 번만 나와야 합니다 (.NET 의 Parallel 은 그대로 두면 오류를 스레드 수만큼 모아서 던짐).
    """
    cases = [
        (["landscape.theta=-3"], "theta 는 0 이상이어야 합니다: -3"),
        (
            ["plates.continental_area_fraction=1.5"],
            "continental_area_fraction 은 0~1 이어야 합니다",
        ),
        (["profile.compute.numba_threads=-1"], "numba_threads 는 0 이상이어야 합니다: -1"),
        (["climate.pet_base_m_per_yr=0"], "잠재 증발산이 0 이하입니다"),
        (
            ["climate.pet_base_m_per_yr=0", "profile.compute.numba_threads=1"],
            "잠재 증발산이 0 이하입니다",
        ),
    ]
    for sets, message in cases:
        args = [x for item in sets for x in ("--set", item)]
        proc = csharp_cli("all", "--profile", "tiny", "--out", tmp_path / "bad", *args, check=False)
        assert proc.returncode == 1, (sets, proc.stderr)
        assert proc.stderr.count(message) == 1, (sets, proc.stderr)
        assert "오류가 난 곳: Bpcg." in proc.stderr, (sets, proc.stderr)
        assert "Unhandled exception" not in proc.stderr, sets
        assert "AggregateException" not in proc.stderr, sets


def test_hero_setting_error_is_reported_not_replaced_by_flat_hero(csharp_cli, tmp_path):
    """히어로 단계의 설정 오류를 '자리를 못 찾음'으로 보고 평면 히어로로 바꾸지 않습니다."""
    run = tmp_path / "run"
    proc = csharp_cli(
        "all", "--profile", "tiny", "--out", run, "--set", "profile.hero.spacing_m=0", check=False
    )
    assert proc.returncode == 1
    assert "profile.hero 는 spacing_m > 0" in proc.stderr
    assert "평면 히어로" not in proc.stdout
    assert (run / "planet" / "manifest.json").is_file()
    assert not (run / "hero").exists()


def test_flag_given_a_value_is_rejected(csharp_cli, tmp_path):
    """--flat=false 를 --flat 으로 읽어 끄려던 옵션을 켜지 않습니다."""
    run = tmp_path / "run"
    for flag in ("--flat=false", "--engine=0"):
        proc = csharp_cli("all", "--profile", "tiny", "--out", run, flag, check=False)
        assert proc.returncode == 2, flag
        assert "값을 받지 않는 옵션입니다" in proc.stderr and flag.split("=")[0] in proc.stderr
        assert not run.exists(), flag
    proc = csharp_cli("bake", "--hero", tmp_path, "--no-globe=false", check=False)
    assert proc.returncode == 2 and "--no-globe" in proc.stderr


def test_planet_toml_path_runs_into_a_folder_named_after_the_file(csharp_cli, tmp_path):
    """--planet 에 TOML 경로를 주고 --out 을 빼면, 실행 폴더는 out/<파일 이름> 입니다."""
    toml = tmp_path / "myworld.toml"
    shutil.copy(CONFIGS / "planets" / "earth.toml", toml)
    out = tmp_path / "out"
    proc = subprocess.run(
        cli_command(["planet", "--planet", str(toml), "--profile", "tiny"]),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=cli_env() | {"BPCG_OUT": str(out)},
        timeout=600,
        cwd=ROOT,
    )
    assert proc.returncode == 0, proc.stderr
    assert (out / "myworld" / "planet" / "manifest.json").is_file()
    assert toml.is_file()


@pytest.mark.slow  # 따로 C# 실행을 돌려 몇 초 걸림 (CI 는 돌림)
def test_python_entry_forwards_to_csharp(csharp_cli, tmp_path, capfd):
    """Python 입구 `bpcg` 의 planet·hero·bake·all 은 C# 콘솔로 넘어갑니다."""
    assert bpcg_main(["hero", "--flat", "--profile", "tiny", "--out", str(tmp_path)]) == 0
    assert (tmp_path / "hero" / "manifest.json").exists()
    assert bpcg_main(["nope"]) == 2
    assert "usage: bpcg" in capfd.readouterr().err
