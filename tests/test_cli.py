"""명령줄 검사 (docs/pipeline.md 13장): C# 콘솔 bpcg planet·hero·bake·all 과 Python 입구 `bpcg`.

tiny 프로필로 전체를 몇 초 안에 돌립니다.
"""

import shutil

import numpy as np

from bundles import load_bundle, read_json, run_cli

from bpcg_studio.cli import main as bpcg_main

CORRIDOR_FILES = (
    "heightmap.bin",
    "heightmap.json",
    "surround25.bin",
    "water.bin",
    "water_table.bin",
    "strata.u8",
    "strata.json",
    "manifest.json",
)


def test_all_matches_independent_commands(csharp_cli, tiny_run, tmp_path):
    csharp_cli("planet", "--profile", "tiny", "--seed", "0", "--out", tmp_path)
    csharp_cli("hero", "--profile", "tiny", "--seed", "0", "--from",
               tmp_path / "planet", "--out", tmp_path)
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
    modes = (("single", "1"), ("threads2", "2"), ("threads4", "4"), ("auto", "0"), ("auto_repeat", "0"))
    for mode, threads in modes:
        run = tmp_path / mode
        proc = csharp_cli(
            "all", "--profile", "tiny", "--seed", "1738104521", "--out", run,
            "--set", f"profile.compute.numba_threads={threads}",
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
                        "z_m.npy", "uplift_m_per_yr.npy", "temperature_c.npy", "precip_m_per_yr.npy",
                        "discharge_m3_per_yr.npy", "water_table_m.npy",
                    }:
                        assert np.isfinite(got).all(), expected.relative_to(baseline)
                else:
                    assert expected.read_bytes() == actual.read_bytes(), expected.relative_to(baseline)


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


def test_python_entry_forwards_to_csharp(csharp_cli, tmp_path, capfd):
    """Python 입구 `bpcg` 의 planet·hero·bake·all 은 C# 콘솔로 넘어갑니다."""
    assert bpcg_main(["hero", "--flat", "--profile", "tiny", "--out", str(tmp_path)]) == 0
    assert (tmp_path / "hero" / "manifest.json").exists()
    assert bpcg_main(["nope"]) == 2
    assert "usage: bpcg" in capfd.readouterr().err
