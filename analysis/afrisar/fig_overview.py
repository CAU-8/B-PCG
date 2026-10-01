"""AfriSAR 로페·라비 분석을 한 장(2 x 3)으로 모은 그림을 그립니다 (docs/figures/afrisar_overview.png).

(a) DEM + 하천, (b) 수관 높이, (c) 숲/낮은 식생의 평균 수직 프로파일, (d) 경사-면적,
(e) HAND 별 수관 높이, (f) SRTM 대비 지면 오프셋 분포.

입력: data/derived/afrisar/ 의 lope_dem_30m.npz, lope_eco_30m.npz, lope_hydro.npz, rabi_hydro.npz,
     lope_ground_canopy.npz 와 data/cache/afrisar/data/lope-tomo-capon-hv.h5 (약 1.4 GB, 없으면 꺼냄)
출력: data/derived/afrisar/afrisar_overview.png (--out 으로 바꿈). 문서 그림을 바꾸려면 docs/figures/ 에 복사합니다.
실행: uv run python analysis/afrisar/fig_overview.py [--out 경로]
"""

import argparse
from pathlib import Path

import h5py
import matplotlib
import matplotlib.pyplot as plt
import numpy as np
from extract import tomo_file
from hydro import OUT_DIR
from matplotlib import font_manager
from matplotlib.ticker import FuncFormatter

matplotlib.use("Agg")  # 창 없이 파일로만 그림

# 운영체제마다 있는 한글 글꼴 (macOS, Windows, Linux 순)
KOREAN_FONTS = (
    "AppleGothic",
    "Apple SD Gothic Neo",
    "Malgun Gothic",
    "NanumGothic",
    "Noto Sans CJK KR",
)


def use_korean_font():
    """설치된 한글 글꼴을 찾아 쓰고, 빠진 글자는 DejaVu Sans 로 채웁니다."""
    installed = {f.name for f in font_manager.fontManager.ttflist}
    found = [n for n in KOREAN_FONTS if n in installed]
    if not found:
        print(
            "경고: 한글 글꼴을 찾지 못해 한글이 네모로 나올 수 있습니다:", ", ".join(KOREAN_FONTS)
        )
    plt.rcParams["font.family"] = [*found, "DejaVu Sans"]
    plt.rcParams["axes.unicode_minus"] = False  # 마이너스 기호를 ASCII '-' 로
    plt.rcParams["mathtext.fontset"] = "dejavusans"
    plt.rcParams["axes.formatter.use_mathtext"] = False


def main(out):
    use_korean_font()
    dem = np.load(OUT_DIR / "lope_dem_30m.npz")
    z = dem["z"]
    eco = np.load(OUT_DIR / "lope_eco_30m.npz")
    hy = np.load(OUT_DIR / "lope_hydro.npz")
    hr = np.load(OUT_DIR / "rabi_hydro.npz")
    gc = np.load(OUT_DIR / "lope_ground_canopy.npz")
    fig, ax = plt.subplots(2, 3, figsize=(17, 10.5))
    num_fmt = FuncFormatter(lambda v, _: f"{v:g}")

    # (a) DEM + 하천
    gy, gx = np.gradient(np.nan_to_num(z, nan=np.nanmean(z)), 30.0)
    hs = np.clip(0.5 + 0.5 * (-gx * 0.7 + gy * 0.7) / np.hypot(1, np.hypot(gx, gy)), 0, 1)
    a = ax[0, 0]
    a.imshow(np.where(np.isfinite(z), hs, np.nan), cmap="gray", origin="lower", alpha=1)
    im = a.imshow(z, cmap="terrain", origin="lower", alpha=0.55)
    A = eco["A"]
    riv = np.where(A >= 2e6, np.log10(A), np.nan)
    a.imshow(riv, cmap="Blues", origin="lower", vmin=5.5, vmax=9)
    a.set_title("(a) Lope TerrainHeight(SRTM) 30 m + 하천(A≥2 km²)")
    plt.colorbar(im, ax=a, fraction=0.04, label="m")
    a.set_xticks([])
    a.set_yticks([])

    # (b) 수관 높이
    a = ax[0, 1]
    im = a.imshow(eco["CH"], cmap="YlGn", origin="lower", vmin=5, vmax=60)
    a.set_title("(b) TomoSAR 수관 높이 (HV RH95 - HH 지면)")
    plt.colorbar(im, ax=a, fraction=0.04, label="m")
    a.set_xticks([])
    a.set_yticks([])

    # (c) 평균 수직 프로파일 (Capon HV), 방위 1200:1300, 레인지 600:900 창
    a = ax[0, 2]
    with h5py.File(tomo_file("lope", "capon", "hv"), "r") as h:
        H = h["Heights"][:]
        X = h["Tomogram"][:, 1200:1300, 600:900]
    P = np.maximum(X - np.percentile(X, 10, axis=0), 0)
    P = P / P.max(0)
    chs = gc["ch"][1200:1300, 600:900]
    for lab, m, c in (
        ("키 큰 숲 (수관 ≥ 40 m)", chs >= 40, "darkgreen"),
        ("낮은 식생 (수관 < 20 m)", chs < 20, "goldenrod"),
    ):
        if m.sum() > 50:
            a.plot(P[:, m].mean(1), H, color=c, lw=2, label=f"{lab}, n={m.sum()}")
    a.axhline(0, color="k", lw=0.8, ls=":")
    a.set_ylim(-40, 80)
    a.set_xlabel("정규화 후방산란 (HV, Capon)")
    a.set_ylabel("SRTM 기준 높이 [m]")
    a.set_title("(c) 실제 숲의 수직 밀도 프로파일")
    a.legend(fontsize=9)

    # (d) 경사-면적 (원시)
    a = ax[1, 0]
    for h_, lab, c in ((hy, "Lope", "C0"), (hr, "Rabi", "C1")):
        a.plot(10 ** h_["cen"], 10 ** h_["med"], "o-", color=c, label=f"{lab} (구간 중앙값)")
    Aref = np.logspace(5, 8.5)
    a.plot(Aref, 0.04 * (Aref / 1e6) ** -0.45, "k--", lw=1, label="θ = 0.45 기준선")
    a.set_xscale("log")
    a.set_yscale("log")
    a.xaxis.set_major_formatter(num_fmt)
    a.yaxis.set_major_formatter(num_fmt)
    a.set_xlabel("상류 면적 A [m²]")
    a.set_ylabel("경사 S")
    a.set_title("(d) 경사-면적: θ≈0.07–0.13 (잡음·잘린 유역)")
    a.legend(fontsize=9)

    # (e) 수관 vs HAND
    a = ax[1, 1]
    hnd = eco["HAND"].ravel()
    chh = eco["CH"].ravel()
    ok = np.isfinite(hnd) & np.isfinite(chh) & (hnd >= 0)
    edges = np.array([0, 2, 5, 10, 20, 40, 80, 200])
    cen = []
    q = []
    for lo, hi in zip(edges[:-1], edges[1:], strict=True):
        s = ok & (hnd >= lo) & (hnd < hi)
        cen.append(np.sqrt(max(lo, 1) * hi))
        q.append(np.percentile(chh[s], [25, 50, 75]))
    q = np.array(q)
    a.fill_between(cen, q[:, 0], q[:, 2], alpha=0.3, color="green")
    a.plot(cen, q[:, 1], "o-", color="darkgreen")
    a.set_xscale("log")
    a.xaxis.set_major_formatter(num_fmt)
    a.set_xlabel("HAND: 가장 가까운 하천 위 높이 [m]")
    a.set_ylabel("수관 높이 [m]")
    a.set_title("(e) 하천에서 멀수록 숲이 높다 (ρ=+0.30)")

    # (f) 지면 오프셋 분포
    a = ax[1, 2]
    g = gc["g"].ravel()
    g = g[np.isfinite(g)]
    a.hist(g, bins=np.arange(-40, 42, 2), color="sienna")
    a.axvline(np.median(g), color="k", ls="--", label=f"중앙값 {np.median(g):.0f} m")
    a.set_xlabel("HH 지면 반사 높이 - SRTM [m]")
    a.set_ylabel("픽셀 수")
    a.set_title("(f) SRTM 대비 지면 위치: 평균은 맞지만 ±15 m 흔들림")
    a.legend()

    fig.suptitle(
        "AfriSAR TomoSAR (ORNL DAAC 1577) — Lope 국립공원, 가봉: 무엇을 쓸 수 있나", fontsize=15
    )
    fig.tight_layout()
    out.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(out, dpi=110)
    print("saved", out)


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="AfriSAR 개요 그림을 그립니다.")
    ap.add_argument(
        "--out", type=Path, default=OUT_DIR / "afrisar_overview.png", help="저장할 PNG 경로"
    )
    main(ap.parse_args().out)
