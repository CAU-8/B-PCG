"""결과 그림 검사: C# 콘솔 `bpcg figures results`·`bpcg figures cubesphere` (src/Bpcg.Figures).

그림은 matplotlib 판과 눈으로 같으면 되므로 픽셀은 보지 않고,
파일·크기(인치 × dpi)·summary.json 값을 봅니다.
"""

import json
import struct

import numpy as np
from bundles import load_bundle, run_cli

# 파일 → (가로, 세로) 픽셀 = figsize × dpi (render_results.py 와 같음, dpi 110)
FIGURE_SIZES = {
    "planet_elevation_plain.png": (1760, 924),
    "planet_elevation_faces.png": (1760, 924),
    "planet_globes.png": (1760, 1210),
    "planet_layers.png": (1870, 1045),
    "hypsometry_vs_earth.png": (1100, 572),
    "hero_map.png": (1210, 1100),
    "hero_subsurface.png": (1540, 1430),
    "hero_3d.png": (1540, 990),
}


def png_size(path) -> tuple[int, int]:
    head = path.read_bytes()[:24]
    assert head[:8] == b"\x89PNG\r\n\x1a\n", path
    return struct.unpack(">II", head[16:24])


def test_figures_results_writes_every_figure(csharp_cli, tiny_run, tmp_path):
    out = tmp_path / "figures"
    proc = csharp_cli("figures", "results", tiny_run, "--out", out)
    assert proc.stdout.strip().splitlines()[-1] == f"그림을 썼습니다: {out}"
    for name, size in FIGURE_SIZES.items():
        assert png_size(out / name) == size, name
    w, h = png_size(out / "cross_section.png")
    assert w == 1500 and 420 <= h <= 1620  # 10 인치 × 150 dpi, 높이는 단면 비율로 2.8~10.8 인치

    summary = json.loads((out / "summary.json").read_text(encoding="utf-8"))
    planet = load_bundle(tiny_run / "planet")
    hero = load_bundle(tiny_run / "hero")
    area = planet.graph.area
    ocean = np.asarray(planet.fields["is_ocean"], bool)
    assert summary["bpcg_ocean_fraction"] == np.float64((area * ocean).sum() / area.sum())
    z = np.asarray(hero.fields["z_m"], float)
    assert summary["hero_z_range_m"] == [float(z.min()), float(z.max())]
    cut = summary["cross_section"]
    assert cut["x_m"][1] - cut["x_m"][0] == 3000.0 and cut["z"][0] < cut["z"][1]
    assert summary["planet_iterations"] >= 1 and summary["hero_iterations"] >= 1
    if "earth_ocean_fraction" in summary:  # data/pilot 에 ETOPO 가 있을 때만
        assert 0.6 < summary["earth_ocean_fraction"] < 0.8


def test_figures_results_without_etopo(csharp_cli, tiny_run, tmp_path):
    out = tmp_path / "figures"
    csharp_cli("figures", "results", tiny_run, "--out", out, "--etopo", tmp_path / "none.nc")
    summary = json.loads((out / "summary.json").read_text(encoding="utf-8"))
    assert "earth_ocean_fraction" not in summary
    assert png_size(out / "hypsometry_vs_earth.png") == FIGURE_SIZES["hypsometry_vs_earth.png"]


def test_figures_results_needs_planet_bundle(csharp_cli, flat_run, tmp_path):
    proc = run_cli("figures", "results", flat_run, "--out", tmp_path / "f", check=False)
    assert proc.returncode == 1
    assert "결과 그림에 필요한 파일이 없습니다" in proc.stderr
    proc = run_cli("figures", "results", check=False)
    assert proc.returncode == 2 and "required: run" in proc.stderr


def test_figures_cubesphere(csharp_cli, tmp_path):
    path = tmp_path / "cube.png"
    proc = csharp_cli("figures", "cubesphere", "--n", "16", "--out", path)
    lines = proc.stdout.strip().splitlines()
    assert lines[0] == "생성된 총 셀 개수: 1536"
    assert lines[1] == "전체 면적 합: 2827.433388 (이론값 4πR²: 2827.433388)"
    assert png_size(path) == (960, 960)  # 8 인치 × 120 dpi
