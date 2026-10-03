"""공통 고정물: C# 콘솔을 한 번 빌드하고, 시험이 같이 쓰는 tiny 실행을 세션마다 한 번 돌립니다.

| 고정물 | 명령 | 쓰는 곳 |
|---|---|---|
| tiny_run | all --profile tiny --seed 0 | 행성·히어로·회랑·지구본 대부분 |
| tiny_planet_again | planet --profile tiny --seed 0 (다른 폴더) | 결정성 |
| flat_run | hero --flat --profile tiny --seed 3 → bake | 평면 히어로 |
| cave_run | hero --flat (강 문턱·n 을 낮춤) → bake | 동굴·선상지 호수가 꼭 생기는 회랑 |
| no_detail_run | bake --set detail.fractal_gain=0 | 프랙탈 디테일을 끈 굽기 |

dotnet 이 없으면 C# 결과를 쓰는 시험은 모두 건너뜁니다 (.NET 10 SDK 필요).
"""

import shutil
from pathlib import Path

import pytest
from bundles import Bundle, build_cli, load_bundle, run_cli

# tests/test_engine.py 와 같은 까닭: 선상지 호수와 동굴이 생기는 조건을 고정합니다 (강 문턱을 작은
# 영역에 맞추고 n = 1 로 산지 앞 경사 차이를 키움).
CAVE_OVERRIDES = ("landscape.slope_exponent_n=1.0", "rivers.min_discharge_m3_per_s=0.02")


@pytest.fixture(scope="session")
def csharp_cli():
    """C# 콘솔을 (바뀐 것만) 빌드합니다."""
    if shutil.which("dotnet") is None:
        pytest.skip("dotnet 이 없습니다 (.NET 10 SDK 필요)")
    build_cli()
    return run_cli


@pytest.fixture(scope="session")
def tiny_run(csharp_cli, tmp_path_factory) -> Path:
    run = tmp_path_factory.mktemp("tiny") / "run"
    proc = csharp_cli("all", "--profile", "tiny", "--seed", "0", "--out", run)
    assert "[전체] 끝" in proc.stdout
    return run


@pytest.fixture(scope="session")
def planet(tiny_run) -> Bundle:
    return load_bundle(tiny_run / "planet")


@pytest.fixture(scope="session")
def hero(tiny_run) -> Bundle:
    return load_bundle(tiny_run / "hero")


@pytest.fixture(scope="session")
def tiny_planet_again(csharp_cli, tmp_path_factory) -> Bundle:
    run = tmp_path_factory.mktemp("tiny_again")
    csharp_cli("planet", "--profile", "tiny", "--seed", "0", "--out", run)
    return load_bundle(run / "planet")


@pytest.fixture(scope="session")
def flat_run(csharp_cli, tmp_path_factory) -> Path:
    run = tmp_path_factory.mktemp("flat") / "run"
    csharp_cli("hero", "--flat", "--profile", "tiny", "--seed", "3", "--out", run)
    csharp_cli("bake", "--hero", run / "hero")
    return run


@pytest.fixture(scope="session")
def flat_hero(flat_run) -> Bundle:
    return load_bundle(flat_run / "hero")


@pytest.fixture(scope="session")
def cave_run(csharp_cli, tmp_path_factory) -> Path:
    run = tmp_path_factory.mktemp("caves") / "run"
    sets = [a for kv in CAVE_OVERRIDES for a in ("--set", kv)]
    csharp_cli("hero", "--flat", "--profile", "tiny", "--out", run, *sets)
    csharp_cli("bake", "--hero", run / "hero")
    return run


@pytest.fixture(scope="session")
def cave_hero(cave_run) -> Bundle:
    return load_bundle(cave_run / "hero")


@pytest.fixture(scope="session")
def no_detail_run(csharp_cli, cave_run, tmp_path_factory) -> Path:
    out = tmp_path_factory.mktemp("no_detail") / "corridor"
    csharp_cli(
        "bake", "--hero", cave_run / "hero", "--out", out, "--no-globe",
        "--set", "detail.fractal_gain=0",
    )  # fmt: skip
    return out
