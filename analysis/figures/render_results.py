"""생성 결과를 그림으로 그립니다 (행성 지도·지구본·지구와 고도 분포 비교·히어로 지도·단면·3D).

실행: uv run python analysis/figures/render_results.py out/earth_v2 [--out out/earth_v2/figures]
입력은 `bpcg all` 이 쓴 폴더(planet/, hero/, corridor/)입니다. ETOPO 비교는 data/pilot 이 있을 때만 그립니다.

아직 돌지 않습니다: 이 스크립트는 지운 Python 생성기(bpcg)를 import 합니다. C# 이 쓴 결과 묶음
(planet/·hero/·corridor/ 의 manifest·.npy)을 직접 읽게 고칠 때까지 남겨 둡니다.
"""

import argparse
import json
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
from bpcg.bake.bundle import load_hero_state, load_planet_state  # noqa: E402
from bpcg.core import cubesphere as cs  # noqa: E402
from bpcg.core.config import load_config  # noqa: E402
from bpcg.core.paths import PILOT  # noqa: E402
from bpcg.geology import rocks  # noqa: E402
from matplotlib import font_manager  # noqa: E402
from matplotlib.colors import LightSource, ListedColormap, TwoSlopeNorm  # noqa: E402

for name in (
    "AppleGothic",
    "Apple SD Gothic Neo",
    "Malgun Gothic",
    "NanumGothic",
    "Noto Sans CJK KR",
):
    if any(f.name == name for f in font_manager.fontManager.ttflist):
        plt.rcParams["font.family"] = name
        break
plt.rcParams["axes.unicode_minus"] = False
plt.rcParams["figure.dpi"] = 110


def terrain_cmap():
    """바다(파랑 계열)와 육지(초록→갈색→흰색)를 0 m 에서 잇는 색표."""
    ocean = plt.get_cmap("Blues_r")(np.linspace(0.05, 0.85, 128))
    land = plt.get_cmap("gist_earth")(np.linspace(0.38, 0.98, 128))
    return ListedColormap(np.vstack([ocean, land]))


def equirect(field: np.ndarray, n: int, width: int = 2400):
    """셀 값 → 위경도 그림 (H, W). 경도 −180~180, 위도 90~−90."""
    h = width // 2
    lon = np.linspace(-np.pi, np.pi, width, endpoint=False) + np.pi / width
    lat = np.linspace(np.pi / 2, -np.pi / 2, h, endpoint=False) - np.pi / (2 * h)
    LON, LAT = np.meshgrid(lon, lat)
    p = np.stack([np.cos(LAT) * np.cos(LON), np.cos(LAT) * np.sin(LON), np.sin(LAT)], -1)
    c = cs.cell_of(p.reshape(-1, 3), n).reshape(h, width)
    return field[c], c // (n * n)


def face_edges(face_img: np.ndarray) -> np.ndarray:
    e = np.zeros(face_img.shape, bool)
    e[:, 1:] |= face_img[:, 1:] != face_img[:, :-1]
    e[1:, :] |= face_img[1:, :] != face_img[:-1, :]
    return e


def shaded(z: np.ndarray, cmap, norm, dx: float, vert: float = 1.0) -> np.ndarray:
    ls = LightSource(azdeg=315, altdeg=40)
    rgb = cmap(norm(z))[..., :3]
    return ls.shade_rgb(rgb, z, vert_exag=vert, dx=dx, dy=dx, blend_mode="soft", fraction=1.0)


def orthographic(field, n, center, size=900, up=(0, 0, 1)):
    """정사영 지구본 그림: 중심 방향 center 에서 본 반구. 반구 밖은 NaN."""
    c = np.asarray(center, float)
    c /= np.linalg.norm(c)
    u = np.asarray(up, float)
    u = u - (u @ c) * c
    if np.linalg.norm(u) < 1e-6:
        u = np.array([1.0, 0, 0]) - c[0] * c
    u /= np.linalg.norm(u)
    r = np.cross(u, c)
    x = np.linspace(-1, 1, size)
    X, Y = np.meshgrid(x, -x)
    rr = X**2 + Y**2
    inside = rr < 1
    Z = np.sqrt(np.clip(1 - rr, 0, 1))
    p = X[..., None] * r + Y[..., None] * u + Z[..., None] * c
    cell = cs.cell_of(p.reshape(-1, 3), n).reshape(size, size)
    img = field[cell].astype(float)
    img[~inside] = np.nan
    return img, cell // (n * n), inside


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("run", help="bpcg all 출력 폴더")
    ap.add_argument("--out", default=None)
    a = ap.parse_args()
    run = Path(a.run)
    out = Path(a.out) if a.out else run / "figures"
    out.mkdir(parents=True, exist_ok=True)

    P = load_planet_state(run / "planet")
    H = load_hero_state(run / "hero")
    f = P.fields
    n = P.graph.shape[1]
    cmap = terrain_cmap()
    zm = np.asarray(f["z_mean_m"], float)
    norm = TwoSlopeNorm(vmin=-7000, vcenter=0, vmax=6000)
    summary: dict = {}

    # --- 1. 행성 고도 지도 (면 경계 없이 / 겹쳐서)
    img, faces = equirect(zm, n)
    dx_deg = 360 / img.shape[1] * 111e3
    rgb = shaded(img, cmap, norm, dx_deg, vert=8)
    for tag, edges in (("plain", None), ("faces", face_edges(faces))):
        fig, ax = plt.subplots(figsize=(16, 8.4))
        show = rgb.copy()
        if edges is not None:
            show[edges] = (1.0, 0.15, 0.15)
        ax.imshow(show, extent=(-180, 180, -90, 90))
        ax.set_xlabel("경도 (°)")
        ax.set_ylabel("위도 (°)")
        title = "B-PCG 행성 평균 지표 고도 (L0 면당 512칸, 기복 보정 포함)"
        if edges is not None:
            title += " — 빨간 선: 정육면체 면 경계"
        ax.set_title(title)
        sm = plt.cm.ScalarMappable(norm=norm, cmap=cmap)
        fig.colorbar(sm, ax=ax, shrink=0.7, label="고도 (m)")
        fig.tight_layout()
        fig.savefig(out / f"planet_elevation_{tag}.png")
        plt.close(fig)

    # --- 2. 지구본 세 방향 (히어로 위치, 정육면체 꼭짓점, 면 가운데)
    site_unit = np.asarray(H.site.center_unit) if H.site is not None else np.array([1.0, 0, 0])
    views = [
        ("히어로 유역 쪽", site_unit),
        ("정육면체 꼭짓점 쪽 (세 면이 만나는 곳)", np.array([1.0, 1.0, 1.0])),
        ("면 가운데 쪽 (+Z, 북극)", np.array([0.2, 0.0, 1.0])),
    ]
    fig, axes = plt.subplots(2, 3, figsize=(16, 11))
    for k, (label, v) in enumerate(views):
        gimg, gfaces, inside = orthographic(zm, n, v)
        g_rgb = shaded(np.nan_to_num(gimg), cmap, norm, 6371e3 * 2 / gimg.shape[0], vert=10)
        g_rgb[~inside] = 1.0
        axes[0, k].imshow(g_rgb)
        axes[0, k].set_title(label)
        g2 = g_rgb.copy()
        g2[face_edges(gfaces) & inside] = (1.0, 0.15, 0.15)
        axes[1, k].imshow(g2)
        axes[1, k].set_title(label + " + 면 경계")
        if k == 0 and H.site is not None:
            for ax in axes[:, k]:
                ax.plot(gimg.shape[1] / 2, gimg.shape[0] / 2, "o", mfc="none", mec="yellow", ms=14)
        for ax in axes[:, k]:
            ax.axis("off")
    fig.suptitle("같은 행성을 세 방향에서 본 모습. 아래 줄의 빨간 선이 정육면체 면 경계입니다")
    fig.tight_layout()
    fig.savefig(out / "planet_globes.png")
    plt.close(fig)

    # --- 3. 판·해양저 나이·강수·융기
    plate_img, _ = equirect(np.asarray(f["plate_id"]), n)
    btype_img, _ = equirect(np.asarray(f["boundary_type"]), n)
    age_img, _ = equirect(np.asarray(f["ocean_age_myr"], float), n)
    p_img, _ = equirect(np.asarray(f["precip_m_per_yr"], float), n)
    u_img, _ = equirect(np.asarray(f["uplift_m_per_yr"], float) * 1e3, n)
    ocean_img, _ = equirect(np.asarray(f["is_ocean"]), n)
    fig, axes = plt.subplots(2, 2, figsize=(17, 9.5))
    ext = (-180, 180, -90, 90)
    ax = axes[0, 0]
    ax.imshow(plate_img % 20, cmap="tab20", extent=ext, interpolation="nearest")
    for t, col in ((1, "red"), (2, "deepskyblue"), (3, "lightgray")):
        yy, xx = np.nonzero(btype_img == t)
        ax.scatter(
            xx / plate_img.shape[1] * 360 - 180, 90 - yy / plate_img.shape[0] * 180, s=0.3, c=col
        )
    ax.contour(
        ocean_img.astype(float),
        levels=[0.5],
        colors="k",
        linewidths=0.4,
        extent=(-180, 180, 90, -90),
    )
    ax.set_title("판 (색) · 경계: 빨강 수렴, 파랑 발산, 회색 변환 · 검은 선 해안")
    im = axes[0, 1].imshow(age_img, cmap="viridis_r", extent=ext)
    axes[0, 1].set_title("해양저 나이 (Myr) — 해령에서 멀수록 늙음")
    fig.colorbar(im, ax=axes[0, 1], shrink=0.8)
    im = axes[1, 0].imshow(p_img, cmap="YlGnBu", extent=ext, vmax=3)
    axes[1, 0].set_title("연강수량 (m/yr) — 적도와 중위도 띠, 30° 근처 건조")
    fig.colorbar(im, ax=axes[1, 0], shrink=0.8)
    im = axes[1, 1].imshow(
        np.where(ocean_img, np.nan, u_img), cmap="magma", extent=ext, vmin=0, vmax=2
    )
    axes[1, 1].set_title("융기 속도 (mm/yr, 지각 세기 한계 적용 후)")
    fig.colorbar(im, ax=axes[1, 1], shrink=0.8)
    fig.tight_layout()
    fig.savefig(out / "planet_layers.png")
    plt.close(fig)

    # --- 4. 고도 분포: B-PCG vs 지구(ETOPO 2022)
    A = P.graph.area
    bins = np.arange(-11000, 9001, 250)
    h_b, _ = np.histogram(zm, bins=bins, weights=A / A.sum())
    etopo = PILOT / "etopo" / "ETOPO_2022_v1_60s_N90W180_surface.nc"
    fig, ax = plt.subplots(figsize=(10, 5.2))
    ax.step(bins[:-1], h_b * 100, where="post", label="B-PCG (L0 평균 지표)", color="tab:orange")
    if etopo.exists():
        import h5py

        with h5py.File(etopo, "r") as fh:
            z = fh["z"][::10, ::10].astype(float)  # 약 18 km 간격으로 표본
            lat = fh["lat"][::10]
        w = np.cos(np.radians(lat))[:, None] * np.ones_like(z)
        h_e, _ = np.histogram(z, bins=bins, weights=w / w.sum())
        ax.step(bins[:-1], h_e * 100, where="post", label="지구 (ETOPO 2022)", color="tab:blue")
        summary["earth_ocean_fraction"] = float((w * (z < 0)).sum() / w.sum())
    ax.set_xlabel("고도 (m)")
    ax.set_ylabel("면적 비율 (%, 250 m 구간)")
    ax.set_title("고도 분포: 대륙과 해양 두 봉우리 (설계도 7장의 '반쯤 입력' 검사)")
    ax.legend()
    ax.grid(alpha=0.3)
    fig.tight_layout()
    fig.savefig(out / "hypsometry_vs_earth.png")
    plt.close(fig)
    summary["bpcg_ocean_fraction"] = float((A * np.asarray(f["is_ocean"])).sum() / A.sum())

    # --- 5. 히어로 지도
    hf = H.fields
    ny, nx = H.graph.shape
    dx = float(H.graph.spacing)
    z = np.asarray(hf["z_m"], float).reshape(ny, nx)
    ext_h = (-nx * dx / 2000, nx * dx / 2000, -ny * dx / 2000, ny * dx / 2000)
    hnorm = plt.Normalize(vmin=float(z.min()), vmax=float(z.max()))
    hcm = plt.get_cmap("gist_earth")
    hrgb = shaded(z, hcm, hnorm, dx, vert=1.5)
    river = np.asarray(hf["is_river"]).reshape(ny, nx)
    lake = np.asarray(hf["is_lake"]).reshape(ny, nx)
    fan = np.asarray(hf["fan"]).reshape(ny, nx)
    ent = np.asarray(hf["cave_entrance"]).reshape(ny, nx) > 0
    show = hrgb.copy()
    show[fan] = 0.6 * show[fan] + 0.4 * np.array([1.0, 0.85, 0.3])
    show[river] = (0.1, 0.35, 0.9)
    show[lake] = (0.25, 0.6, 1.0)
    fig, ax = plt.subplots(figsize=(11, 10))
    ax.imshow(show, extent=ext_h)
    yy, xx = np.nonzero(ent)
    ax.scatter(
        (xx + 0.5) * dx / 1000 + ext_h[0],
        ext_h[3] - (yy + 0.5) * dx / 1000,
        s=1,
        c="magenta",
        label="동굴 입구",
    )
    man = json.loads((run / "corridor" / "manifest.json").read_text(encoding="utf-8"))
    r = man["corridor"]["rect_local_m"]
    ax.add_patch(
        plt.Rectangle((r["x_min"] / 1000, r["y_min"] / 1000), (r["x_max"] - r["x_min"]) / 1000,
                      (r["y_max"] - r["y_min"]) / 1000, fill=False, ec="red", lw=2, label="걷는 회랑 (1 × 6 km)")
    )  # fmt: skip
    ax.set_xlabel("동 (km)")
    ax.set_ylabel("북 (km)")
    ax.set_title(
        f"히어로 유역 25 m ({nx}×{ny}칸) — 고도 {z.min():.0f}~{z.max():.0f} m · "
        "파랑 강·호수, 노랑 선상지, 분홍 동굴 입구"
    )
    sm = plt.cm.ScalarMappable(norm=hnorm, cmap=hcm)
    fig.colorbar(sm, ax=ax, shrink=0.7, label="고도 (m)")
    ax.legend(loc="lower right")
    fig.tight_layout()
    fig.savefig(out / "hero_map.png")
    plt.close(fig)

    # --- 6. 히어로 땅속 지도
    rock = np.asarray(hf["surface_rock"]).reshape(ny, nx)
    rock_rgb = rocks.COLOR_RGB[rock] / 255.0
    soil = np.asarray(hf["soil_thickness_m"], float).reshape(ny, nx)
    wtd = z - np.asarray(hf["water_table_m"], float).reshape(ny, nx)
    c0 = np.isfinite(np.asarray(hf["cave_level_0_m"], float)).reshape(ny, nx)
    c1 = np.isfinite(np.asarray(hf["cave_level_1_m"], float)).reshape(ny, nx)
    fig, axes = plt.subplots(2, 2, figsize=(14, 13))
    axes[0, 0].imshow(
        LightSource(315, 40).shade_rgb(rock_rgb, z, vert_exag=1.5, dx=dx, dy=dx, blend_mode="soft"),
        extent=ext_h,
    )
    present = np.unique(rock)
    handles = [plt.Rectangle((0, 0), 1, 1, color=rocks.COLOR_RGB[k] / 255) for k in present]
    axes[0, 0].legend(
        handles, [rocks.ROCK_NAMES[k] for k in present], loc="lower right", fontsize=8
    )
    axes[0, 0].set_title("지표에 드러난 암석 (솔버가 경사를 정한 바로 그 암석)")
    im = axes[0, 1].imshow(soil, cmap="copper_r", extent=ext_h)
    axes[0, 1].set_title("흙 두께 (m) — 가파른 비탈은 0 (맨 암반)")
    fig.colorbar(im, ax=axes[0, 1], shrink=0.8)
    im = axes[1, 0].imshow(np.clip(wtd, 0, 300), cmap="Blues_r", extent=ext_h)
    axes[1, 0].set_title("지하수면 깊이 (m) — 강가에서 0, 능선 아래에서 깊음")
    fig.colorbar(im, ax=axes[1, 0], shrink=0.8)
    cave_img = np.ones((ny, nx, 3)) * 0.92
    soluble = rocks.SOLUBLE[rock]
    cave_img[soluble] = (0.85, 0.85, 0.75)
    cave_img[c0] = (0.15, 0.35, 0.85)
    cave_img[c1] = (0.95, 0.55, 0.1)
    cave_img[ent] = (0.9, 0.0, 0.6)
    axes[1, 1].imshow(cave_img, extent=ext_h)
    axes[1, 1].set_title(
        "동굴 층: 파랑 아래층(잠김), 주황 위층(마름), 분홍 입구, 연한 노랑 녹는 암석"
    )
    for ax in axes.ravel():
        ax.set_xlabel("동 (km)")
        ax.set_ylabel("북 (km)")
    fig.tight_layout()
    fig.savefig(out / "hero_subsurface.png")
    plt.close(fig)

    # --- 7. 단면 (회랑을 가로지르는 동서 단면, 동굴 입구가 많은 줄)
    from bpcg.volume.sample import HeroVolume
    from bpcg.volume.slices import vertical_slice

    cfg = load_config("earth", man.get("profile", "laptop"))
    vol = HeroVolume(H, cfg)
    cx = 0.5 * (r["x_min"] + r["x_max"])
    rows = np.nonzero(ent.any(axis=1))[0]
    ys = ext_h[3] * 1000 - (rows + 0.5) * dx
    inside = (ys > r["y_min"]) & (ys < r["y_max"])
    y_cut = (
        float(ys[inside][len(ys[inside]) // 2]) if inside.any() else 0.5 * (r["y_min"] + r["y_max"])
    )
    half = 1500.0
    zz = vol.surface_height(np.linspace(cx - half, cx + half, 200), np.full(200, y_cut))
    z_lo = float(np.nanmin(zz)) - 120
    z_hi = float(np.nanmax(zz)) + 40
    vertical_slice(
        vol,
        (cx - half, y_cut),
        (cx + half, y_cut),
        z_lo,
        z_hi,
        1.5,
        save_path=out / "cross_section.png",
    )
    summary["cross_section"] = {"y_m": y_cut, "x_m": [cx - half, cx + half], "z": [z_lo, z_hi]}

    # --- 8. 히어로 3D 조감
    step = 4
    zs = z[::step, ::step]
    X, Y = np.meshgrid(
        np.arange(zs.shape[1]) * dx * step / 1000, -np.arange(zs.shape[0]) * dx * step / 1000
    )
    face_rgb = shaded(zs, hcm, hnorm, dx * step, vert=1.0)
    face_rgb[river[::step, ::step]] = (0.1, 0.35, 0.9)
    fig = plt.figure(figsize=(14, 9))
    ax = fig.add_subplot(projection="3d")
    ax.plot_surface(
        X,
        Y,
        zs / 1000,
        facecolors=face_rgb,
        rstride=1,
        cstride=1,
        linewidth=0,
        antialiased=False,
        shade=False,
    )
    ax.set_box_aspect((1, 1, 0.25))
    ax.view_init(elev=35, azim=-60)
    ax.set_axis_off()
    ax.set_title("히어로 유역 조감 (고도 과장 없음, 4칸마다 표본)")
    fig.tight_layout()
    fig.savefig(out / "hero_3d.png")
    plt.close(fig)

    summary.update(
        {
            "planet_iterations": P.diag.get("solver", {}).get("iterations"),
            "hero_iterations": H.diag.get("solver", {}).get("iterations"),
            "hero_z_range_m": [float(z.min()), float(z.max())],
            "hero_site": {
                "lat": getattr(H.site, "lat_deg", None),
                "lon": getattr(H.site, "lon_deg", None),
            },
        }
    )
    (out / "summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8"
    )
    print("그림을 썼습니다:", out)


if __name__ == "__main__":
    main()
