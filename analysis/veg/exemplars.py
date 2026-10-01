"""로페 Capon HV 수직 프로파일을 토모 지면 기준으로 줄 세우고 k-means(K=5)로 '구조 유형' 예제 프로파일을 만듭니다.

설계도 부록 A-2 의 '복셀 숲 예제: 땅 기준으로 줄 세운 수직 프로파일이 5가지 유형으로 나뉨'의 근거입니다.
중간 레인지(400:1300 열)와 경사 < 0.15 인 화소만, 방위 방향은 4줄마다 하나씩 씁니다.

입력: data/derived/afrisar/lope_ground_canopy.npz, data/derived/veg/lope_radar_detr.npz (pilot2_detrended.py),
     data/cache/afrisar/data/lope-tomo-capon-hv.h5 (약 1.4 GB, 없으면 꺼냄)
출력: data/derived/veg/lope_exemplar_profiles.npz
    Z [m] 토모 지면 기준 높이 격자(-10..70 m, 2 m), C (5, len(Z)) 유형별 평균 프로파일(합 1로 정규화, 낮은→높은 순)
저장소에 올린 값: analysis/veg/results/lope_exemplar_profiles.npz
실행: uv run python analysis/veg/exemplars.py
"""

import h5py
import numpy as np
from afrisar_link import AFRISAR_DIR, VEG_DIR, extract

K = 5  # 유형 수


def main(r0=400, r1=1300):
    gc = np.load(AFRISAR_DIR / "lope_ground_canopy.npz")
    G = gc["g"]
    dd = np.load(VEG_DIR / "lope_radar_detr.npz")
    HAND = dd["HAND"]
    HVd = dd["HVd"]
    SL = dd["SL"]
    with h5py.File(extract.tomo_file("lope", "capon", "hv"), "r") as h:
        H = h["Heights"][:]
        X = np.maximum(h["Tomogram"][:, ::4, r0:r1].astype(np.float32), 0)
    g = G[::4, r0:r1]
    hand = HAND[::4, r0:r1]
    hvd = HVd[::4, r0:r1]
    sl = SL[::4, r0:r1]
    nh = H.size
    P = X.reshape(nh, -1).T
    g = g.ravel()
    hand = hand.ravel()
    hvd = hvd.ravel()
    sl = sl.ravel()
    ok = np.isfinite(g) & (P.max(1) > 0) & np.isfinite(hand) & (sl < 0.15)
    P, g, hand, hvd = P[ok], g[ok], hand[ok], hvd[ok]
    # 지면 기준 높이 격자 (-10..70 m, 2 m)로 재표본
    Z = np.arange(-10, 72, 2.0)
    Q = np.empty((P.shape[0], Z.size), np.float32)
    for i0 in range(0, P.shape[0], 50000):
        sl_ = slice(i0, i0 + 50000)
        for k, zk in enumerate(Z):
            hk = zk + g[sl_]  # SRTM 기준 높이
            f = np.clip((hk - H[0]) / 2.0, 0, nh - 1.001)
            i = f.astype(int)
            w = f - i
            Q[sl_, k] = (1 - w) * P[sl_][np.arange(i.size), i] + w * P[sl_][
                np.arange(i.size), i + 1
            ]
    Q = np.maximum(Q - np.percentile(Q, 10, axis=1, keepdims=True), 0)
    Q /= np.maximum(Q.sum(1, keepdims=True), 1e-12)
    rng = np.random.default_rng(1)
    C = Q[rng.choice(len(Q), K, replace=False)]
    for _ in range(40):
        lab = np.empty(len(Q), int)
        for i0 in range(0, len(Q), 100000):
            lab[i0 : i0 + 100000] = np.argmin(
                ((Q[i0 : i0 + 100000, None, :] - C[None]) ** 2).sum(-1), 1
            )
        C = np.stack([Q[lab == k].mean(0) for k in range(K)])
    order = np.argsort([(C[k] * Z).sum() for k in range(K)])
    C = C[order]
    lab = np.argsort(order)[lab]
    print(
        f"profiles used: {len(Q):,} (Lope, mid-range, slope<0.15); "
        f"height grid rel. TomoSAR ground {Z[0]}..{Z[-1]} m"
    )
    for k in range(K):
        m = lab == k
        c = C[k]
        cc = np.cumsum(c)
        rh25, rh50, rh75, rh95 = (Z[np.argmax(cc >= p)] for p in (0.25, 0.5, 0.75, 0.95))
        print(
            f" class {k}: {m.mean():5.1%} | peak {Z[np.argmax(c)]:+.0f} m | "
            f"RH25 {rh25:+.0f} RH50 {rh50:+.0f} RH75 {rh75:+.0f} RH95 {rh95:+.0f} | "
            f"HAND median {np.median(hand[m]):5.1f} | HVd median {np.median(hvd[m]):+.1f} dB"
        )
        print(
            "    profile (every 6 m):",
            " ".join(f"{z:+.0f}:{v * 100:.1f}" for z, v in zip(Z[::3], c[::3], strict=True)),
        )
    VEG_DIR.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(VEG_DIR / "lope_exemplar_profiles.npz", Z=Z, C=C)


if __name__ == "__main__":
    main()
