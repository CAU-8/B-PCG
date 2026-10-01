"""암석 표와 변성 정도 (docs/pipeline.md 5.1, 설계도 2장 '변성 정도').

암석 번호는 모든 단계가 같이 씁니다. 솔버는 K 배율과 S_crit, 지하수는 투수성, 동굴은 녹음 여부,
단면 셰이더는 색을 이 표에서 읽습니다. 배열은 읽기 전용입니다.
"""

import numpy as np

# ---------------------------------------------------------------- 암석 번호
ALLUVIUM = 0  # 충적층
SANDSTONE = 1  # 사암
SHALE = 2  # 셰일
LIMESTONE = 3  # 석회암
GRANITE = 4  # 화강암
VOLCANIC = 5  # 화산암
SLATE = 6  # 점판암
SCHIST = 7  # 편암
GNEISS = 8  # 편마암
MARBLE = 9  # 대리암
QUARTZITE = 10  # 규암
SOIL = 11  # 흙
N_ROCKS = 12


def _frozen(values, dtype) -> np.ndarray:
    a = np.array(values, dtype=dtype)
    a.setflags(write=False)
    return a


ROCK_NAMES: tuple[str, ...] = (
    "alluvium",
    "sandstone",
    "shale",
    "limestone",
    "granite",
    "volcanic",
    "slate",
    "schist",
    "gneiss",
    "marble",
    "quartzite",
    "soil",
)

# 침식 계수 배율. K = K_ref · K_MULT[암석] (pipeline.md 5.1 표). (N_ROCKS,) float64
K_MULT = _frozen([3.0, 0.5, 2.0, 0.6, 0.3, 0.5, 0.8, 0.6, 0.35, 0.5, 0.25, 4.0], np.float64)

# 산비탈이 버티는 가장 가파른 경사 [m/m] (pipeline.md 5.1 표). (N_ROCKS,) float64
S_CRIT = _frozen([0.4, 0.9, 0.45, 1.0, 1.0, 0.9, 0.8, 0.8, 1.0, 1.0, 1.1, 0.6], np.float64)

# 투수성 log10 k [m²] (Gleeson 2011, 원문 확인 필요). (N_ROCKS,) float64
LOG10_PERM = _frozen(
    [-10.9, -12.5, -16.5, -11.8, -14.1, -12.5, -14.1, -14.1, -14.1, -11.8, -14.1, -11.0],
    np.float64,
)

# 물에 녹는 암석 (동굴이 생길 수 있음): 석회암, 대리암. (N_ROCKS,) bool
SOLUBLE = _frozen([r in (LIMESTONE, MARBLE) for r in range(N_ROCKS)], np.bool_)

# 단면 셰이더용 색 (sRGB), (N_ROCKS, 3) uint8.
# 이웃 층이 잘 갈리도록 밝기와 색상을 서로 떨어뜨렸습니다.
COLOR_RGB = _frozen(
    [
        (214, 190, 128),  # alluvium 모래색
        (226, 150, 82),  # sandstone 주황 황갈색
        (92, 98, 116),  # shale 어두운 청회색
        (222, 218, 196),  # limestone 밝은 크림색
        (206, 140, 136),  # granite 분홍빛
        (60, 32, 36),  # volcanic 검붉은 갈색
        (52, 62, 84),  # slate 짙은 남회색
        (128, 140, 92),  # schist 은빛 녹색
        (156, 108, 146),  # gneiss 자줏빛
        (248, 248, 252),  # marble 흰색
        (232, 170, 180),  # quartzite 분홍
        (104, 74, 46),  # soil 흙 갈색
    ],
    np.uint8,
)

# ---------------------------------------------------------------- 변성 (pipeline.md 5.1)
# 문턱 온도 [°C]. 설계도 2장 표 조회(닫힌 식)이고 값은 pipeline.md 5.1 그대로입니다.
T_SLATE_C = 250.0  # 셰일 → 점판암
T_SCHIST_C = 400.0  # 셰일·점판암 → 편암
T_GNEISS_C = 600.0  # 셰일·점판암·편암 → 편마암
T_MARBLE_C = 350.0  # 석회암 → 대리암
T_QUARTZITE_C = 350.0  # 사암 → 규암
T_GRANITE_GNEISS_C = 650.0  # 화강암 → 편마암

# 암석마다 (문턱 온도, 바뀐 암석) 목록. 온도가 문턱 이상이면 뒤의 것이 앞의 것을 덮어씁니다.
# 이미 변성된 암석도 같은 계열을 따라 올라가고(점판암 → 편암), 거꾸로(후퇴 변성)는 가지 않습니다.
# 충적층·흙·화산암·편마암·대리암·규암은 바뀌지 않습니다.
METAMORPHIC_SERIES: dict[int, tuple[tuple[float, int], ...]] = {
    SHALE: ((T_SLATE_C, SLATE), (T_SCHIST_C, SCHIST), (T_GNEISS_C, GNEISS)),
    SLATE: ((T_SCHIST_C, SCHIST), (T_GNEISS_C, GNEISS)),
    SCHIST: ((T_GNEISS_C, GNEISS),),
    LIMESTONE: ((T_MARBLE_C, MARBLE),),
    SANDSTONE: ((T_QUARTZITE_C, QUARTZITE),),
    GRANITE: ((T_GRANITE_GNEISS_C, GNEISS),),
}
_MAX_STEPS = max(len(s) for s in METAMORPHIC_SERIES.values())


def _series_tables() -> tuple[np.ndarray, np.ndarray]:
    t = np.full((N_ROCKS, _MAX_STEPS), np.inf, dtype=np.float64)
    to = np.tile(np.arange(N_ROCKS, dtype=np.uint8)[:, None], (1, _MAX_STEPS))
    for r, steps in METAMORPHIC_SERIES.items():
        for k, (t_min, product) in enumerate(steps):
            t[r, k] = t_min
            to[r, k] = product
    t.setflags(write=False)
    to.setflags(write=False)
    return t, to


# (N_ROCKS, _MAX_STEPS): 단계 k 의 문턱 [°C](없으면 inf)와 그때의 암석
_META_T, _META_TO = _series_tables()


def peak_temperature_c(depth_m: np.ndarray | float, cfg) -> np.ndarray:
    """묻힌 깊이에서의 최고 온도 T_peak = surface_temperature_c + geotherm_c_per_m · 깊이.

    depth_m: 묻힌 깊이 [m] (아무 모양). 반환: 같은 모양의 온도 [°C], float64.
    """
    g = cfg.geology
    return float(g.surface_temperature_c) + float(g.geotherm_c_per_m) * np.asarray(
        depth_m, dtype=np.float64
    )


def metamorphose(rock: np.ndarray, t_peak_c: np.ndarray) -> np.ndarray:
    """최고 온도로 원래 암석을 변성암으로 바꿉니다 (pipeline.md 5.1 문턱, 벡터 계산).

    rock: 암석 번호 (아무 모양, 정수). t_peak_c: 최고 온도 [°C], rock 과 브로드캐스트되는 모양.
    반환: 브로드캐스트한 모양의 암석 번호, uint8. 문턱과 같은 온도는 변성된 쪽입니다.
    온도가 NaN 이면 바꾸지 않습니다.
    """
    r = np.asarray(rock)
    if r.size and not np.issubdtype(r.dtype, np.integer):
        raise ValueError(f"암석 번호는 정수 배열이어야 합니다 (받은 dtype: {r.dtype})")
    if r.size and (r.min() < 0 or r.max() >= N_ROCKS):
        raise ValueError(f"암석 번호는 0..{N_ROCKS - 1} 이어야 합니다")
    t = np.asarray(t_peak_c, dtype=np.float64)
    r, t = np.broadcast_arrays(r.astype(np.intp, copy=False), t)
    out = r.astype(np.uint8)
    for k in range(_MAX_STEPS):
        hot = t >= _META_T[r, k]
        out = np.where(hot, _META_TO[r, k], out)
    return out.astype(np.uint8, copy=False)
