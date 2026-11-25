"""명령줄 검사 (docs/pipeline.md 13장): C# 콘솔 bpcg planet·hero·bake·all 과 Python 입구 `bpcg`.

tiny 프로필로 전체를 몇 초 안에 돌립니다.
"""

import json
import shutil
import subprocess

import numpy as np
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
    # 지구본: 같은 실행의 행성 묶음으로 굽고, 히어로 자리는 회랑 manifest 에서 찾음
    gj = read_json(tiny_run / "globe" / "globe.json")
    assert gj["format"] == "bpcg-globe" and gj["config_digest"] == pm["config_digest"]
    assert gj["hero"] is not None and len(gj["fields"]) > 5


def test_bake_regenerates_globe_unless_no_globe(csharp_cli, tiny_run, tmp_path):
    run = tmp_path / "copy"
    shutil.copytree(tiny_run, run)
    (run / "globe" / "globe.json").unlink()
    csharp_cli("bake", "--hero", run / "hero", "--no-globe")
    assert not (run / "globe" / "globe.json").exists()
    csharp_cli("bake", "--hero", run / "hero")
    assert (run / "globe" / "globe.json").exists()


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
        for word in ("all", "planet", "hero", "bake", "compare", "figures", "studio"):
            assert f"\n  {word} " in proc.stdout, word
        for flag in ("--planet", "--profile", "--seed", "--out", "--set", "--from", "--flat",
                     "--hero", "--engine", "--no-globe"):  # fmt: skip
            assert f"\n  {flag} " in proc.stdout, flag


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
        (["planet", "--planet", "venus"], "쓸 수 있는 이름은 earth, mars 입니다"),
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


def test_python_entry_forwards_to_csharp(csharp_cli, tmp_path, capfd):
    """Python 입구 `bpcg` 의 planet·hero·bake·all 은 C# 콘솔로 넘어갑니다."""
    assert bpcg_main(["hero", "--flat", "--profile", "tiny", "--out", str(tmp_path)]) == 0
    assert (tmp_path / "hero" / "manifest.json").exists()
    assert bpcg_main(["nope"]) == 2
    assert "usage: bpcg" in capfd.readouterr().err
