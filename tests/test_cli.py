"""명령줄 bpcg 검사 (docs/pipeline.md 13장). tiny 프로필로 전체를 몇 초 안에 돌립니다."""

import json

import pytest

from bpcg.bake.bundle import read_manifest
from bpcg.cli import build_parser, main

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


def test_cli_all_tiny_end_to_end(tmp_path, capsys):
    run = tmp_path / "tiny"
    assert main(["all", "--profile", "tiny", "--out", str(run)]) == 0
    printed = capsys.readouterr().out
    assert "[전체] 끝" in printed
    pm = read_manifest(run / "planet")
    assert pm["kind"] == "planet" and pm["face_basis"] is not None
    assert (run / "planet" / "textures" / "textures.json").exists()
    assert (run / "planet" / "scorecard.json").exists()
    hm = read_manifest(run / "hero")
    assert hm["kind"] == "hero" and hm["config_digest"] == pm["config_digest"]
    for name in CORRIDOR_FILES:
        assert (run / "corridor" / name).exists(), name
    cm = json.loads((run / "corridor" / "manifest.json").read_text())
    assert cm["config_digest"] == pm["config_digest"]
    assert cm["seed"] == 0
    # 지구본: 같은 실행의 행성 묶음으로 굽고, 히어로 자리는 회랑 manifest 에서 찾음
    gj = json.loads((run / "globe" / "globe.json").read_text())
    assert gj["format"] == "bpcg-globe" and gj["config_digest"] == pm["config_digest"]
    assert gj["hero"] is not None and len(gj["fields"]) > 5
    # bake 를 다시 돌려도 지구본을 다시 굽고, --no-globe 면 건드리지 않음
    (run / "globe" / "globe.json").unlink()
    assert main(["bake", "--hero", str(run / "hero"), "--no-globe"]) == 0
    assert not (run / "globe" / "globe.json").exists()
    assert main(["bake", "--hero", str(run / "hero")]) == 0
    assert (run / "globe" / "globe.json").exists()


def test_cli_hero_flat_then_bake(tmp_path):
    run = tmp_path / "flat"
    assert main(["hero", "--flat", "--profile", "tiny", "--out", str(run), "--seed", "3"]) == 0
    hm = read_manifest(run / "hero")
    assert hm["seed"] == 3 and hm["meta"]["site"] is None
    assert main(["bake", "--hero", str(run / "hero")]) == 0
    cm = json.loads((run / "corridor" / "manifest.json").read_text())
    assert cm["site"]["kind"] == "flat_hero" and cm["seed"] == 3
    assert not (run / "globe").exists()  # 행성이 없는 실행은 지구본도 없음


def test_cli_rejects_missing_inputs(tmp_path):
    with pytest.raises(SystemExit):
        main(["hero", "--profile", "tiny", "--out", str(tmp_path)])
    with pytest.raises(SystemExit):
        main(["hero", "--from", str(tmp_path / "nothing")])
    with pytest.raises(SystemExit):
        main(["bake", "--hero", str(tmp_path / "nothing")])
    with pytest.raises(SystemExit):
        build_parser().parse_args([])
