"""매개변수 표: 행성 설정과 프로필 TOML 에서 스튜디오 왼쪽 패널의 목록을 만듭니다.

- 값: load_config 로 읽은 실제 기본값 (configs/learned 덮어쓰기 포함).
- 설명: TOML 줄 끝 주석(`키 = 값  # 설명`)과 절 위 띠 주석(`# ---- 사슬 1: 판·지각·해양저`).
  주석이 없는 키는 HELP_FALLBACK 의 설명을 씁니다. 프로필 키는 프로필마다 주석이 달라서
  PROFILE_HELP 를 설명으로, 파일 주석은 덧붙임(note)으로 씁니다.
- 단위: 키 이름 끝(_m, _m_per_yr …)에서 짐작합니다 (UNIT_SUFFIXES, 예외는 UNIT_OVERRIDES).
- 분류: CATEGORY_TABLE (짧은 명시 표). 값을 바꿀 때 얼마나 조심해야 하는지 보여 줍니다.
"""

import re
from pathlib import Path
from typing import Any

from bpcg.core.config import load_config
from bpcg.core.paths import CONFIGS

# ---------------------------------------------------------------- 분류
CATEGORIES: dict[str, dict[str, str]] = {
    "learn": {
        "label": "학습 예정",
        "long": "학습 예정(데이터로 맞출 값)",
        "description": "실제 지형 자료로 맞출 값입니다. 지금 값은 문헌 기본값이고, "
        "configs/learned/ 에 맞춘 값이 생기면 그 값이 덮어씁니다.",
    },
    "hand": {
        "label": "손 보정(임시)",
        "long": "손 보정(임시)",
        "description": "첫 실행에서 생긴 문제(높이 폭주, 격자 결, 물길 진동)를 고치려고 "
        "손으로 바꾼 값입니다. 데이터 학습 전까지만 쓰는 임시 값입니다.",
    },
    "physics": {
        "label": "물리 상수",
        "long": "물리 상수",
        "description": "지구에서 잰 값입니다. 다른 행성을 만들 때가 아니면 바꾸지 않습니다.",
    },
    "resolution": {
        "label": "해상도·성능",
        "long": "해상도·성능 (프로필)",
        "description": "프로필 값입니다. 격자 크기와 걸리는 시간을 정합니다. 해상도가 바뀌면 결과 "
        "모양도 조금 달라집니다.",
    },
    "detail": {
        "label": "보기용 디테일 (굽기)",
        "long": "보기용 디테일 (굽기)",
        "description": "굽기 때 회랑 높이맵에만 더하는 보기용 잔무늬입니다. 솔버 결과(지형 법칙)와 "
        "점수표는 바꾸지 않고, 엔진에서 켜고 끌 수 있습니다.",
    },
    "literature": {
        "label": "문헌 기본값",
        "long": "문헌 기본값",
        "description": "논문·관측 범위에서 고른 기본값입니다. 바꿔 볼 수 있지만 바꾼 이유를 "
        "기록해 둡니다.",
    },
}

# 절 → 분류 (절 전체가 같은 분류일 때). 키 표(CATEGORY_TABLE)가 먼저입니다.
SECTION_CATEGORY: dict[str, str] = {"detail": "detail"}

# 키 → 분류. 여기 없는 행성 키는 절 분류 → literature, profile.* 은 resolution 입니다.
CATEGORY_TABLE: dict[str, str] = {
    # 데이터로 맞출 값 (configs/learned/README.md: θ, k_s, S_crit)
    "landscape.theta": "learn",
    "landscape.k_ref": "learn",
    "landscape.s_crit_default": "learn",
    # 손 보정 (커밋 d7201ed, de12284)
    "landscape.slope_exponent_n": "hand",
    "landscape.jitter": "hand",
    "landscape.deposition_g_l0": "hand",
    "uplift.orogen_factor": "hand",
    "uplift.collision_factor": "hand",
    "uplift.orogen_width_m": "hand",
    "uplift.collision_width_m": "hand",
    "uplift.elevation_limit_m": "hand",
    "uplift.elevation_limit_power": "hand",
    # 물리 상수
    "planet.radius_m": "physics",
    "planet.mean_density_kg_m3": "physics",
    "planet.rotation_period_s": "physics",
    "planet.axis": "physics",
    "crust.rho_crust": "physics",
    "crust.rho_mantle": "physics",
    "crust.rho_water": "physics",
    "ocean.water_volume_m3": "physics",
    "climate.lapse_rate_c_per_m": "physics",
}

# 손 보정 기록: 무엇을 왜 바꿨나 (커밋 메시지 요약)
HAND_HISTORY: dict[str, str] = {
    "landscape.slope_exponent_n": "1 → 2. n = 1 이면 L0 골짜기 바닥이 수십 km 까지 쌓였습니다",
    "landscape.jitter": "0.4 → 1.0. 격자 정렬 지수가 1.70 에서 1.12 로 내려갔습니다",
    "landscape.deposition_g_l0": "새로 둠 (0 = 끔). L0 칸에서는 퇴적 항이 물길을 진동시켰습니다",
    "uplift.orogen_factor": "0.04 → 0.03. 산맥이 너무 높았습니다",
    "uplift.collision_factor": "0.06 → 0.04. 충돌대가 너무 높았습니다",
    "uplift.orogen_width_m": "150 km → 80 km. 띠가 넓으면 L0 높이가 10 km 를 넘었습니다",
    "uplift.collision_width_m": "새로 둠 (100 km)",
    "uplift.elevation_limit_m": "새로 둠. 한계가 없으면 첫 풀이 평균 지표가 14 km 를 넘었습니다",
    "uplift.elevation_limit_power": "새로 둠",
}

SECTION_LABELS: dict[str, str] = {
    "planet": "행성",
    "plates": "판",
    "crust": "지각",
    "ocean": "해양저",
    "uplift": "융기",
    "climate": "기후",
    "landscape": "지형 솔버",
    "relief": "기복 보정 (L0)",
    "fans": "선상지",
    "geology": "지질",
    "soil": "흙",
    "groundwater": "지하수",
    "caves": "동굴",
    "rivers": "강",
    "detail": "보기용 디테일 (굽기)",
    "profile.grid": "격자 해상도",
    "profile.hero": "히어로 유역",
    "profile.corridor": "걷는 회랑",
    "profile.compute": "계산",
}

# 키 이름 끝 → 단위 (긴 것부터 맞춰 봄)
UNIT_SUFFIXES: tuple[tuple[str, str], ...] = (
    ("_per_degc_m_per_yr", "m/yr/°C"),
    ("_m2_per_yr", "m²/yr"),
    ("_m3_per_yr", "m³/yr"),
    ("_m3_per_s", "m³/s"),
    ("_m_per_myr", "m/Myr"),
    ("_m_per_yr", "m/yr"),
    ("_c_per_m", "°C/m"),
    ("_kg_m3", "kg/m³"),
    ("_n_per_face", "칸"),
    ("_fraction", "비율 0~1"),
    ("_iterations", "회"),
    ("_m3", "m³"),
    ("_km", "km"),
    ("_myr", "Myr"),
    ("_deg", "°"),
    ("_s", "s"),
    ("_c", "°C"),
    ("_m", "m"),
)
UNIT_OVERRIDES: dict[str, str] = {
    "planet.axis": "단위 벡터",
    "planet.seed": "",
    "crust.rho_crust": "kg/m³",
    "crust.rho_mantle": "kg/m³",
    "crust.rho_water": "kg/m³",
    "climate.p_equator": "m/yr",
    "climate.p_midlat": "m/yr",
    "landscape.k_ref": "k_s (Q: m³/yr)",
    "landscape.s_crit_default": "m/m",
    "landscape.s_min": "m/m",
    "landscape.jitter": "칸 크기 대비",
    "fans.slope": "m/m",
    "fans.slope_drop_ratio": "배",
    "soil.bare_slope": "m/m",
    "detail.slope_ref": "m/m",
    "plates.count": "개",
    "plates.warp_amplitude": "단위 구 길이",
    "caves.levels": "층",
    "profile.compute.numba_threads": "개 (0 = 모두)",
}

# 주석이 없는 행성 키의 설명
HELP_FALLBACK: dict[str, str] = {
    "planet.name": "행성 설정 이름 (결과 manifest 에 기록)",
    "planet.radius_m": "행성 반지름. 격자 칸 크기와 중력(g = 4/3·π·G·ρ·R)이 이 값으로 정해집니다",
    "planet.mean_density_kg_m3": "행성 평균 밀도. 반지름과 함께 중력을 정하고, 중력은 지하수 "
    "계산에 들어갑니다",
    "crust.continental_thickness_m": "보통 대륙 지각 두께. 지각평형으로 대륙이 뜨는 높이의 기준",
    "crust.oceanic_thickness_m": "해양 지각 두께",
    "crust.rho_crust": "지각 밀도 (지각평형 계산)",
    "crust.rho_mantle": "맨틀 밀도 (지각평형 계산)",
    "crust.rho_water": "바닷물 밀도 (물에 잠긴 지각의 지각평형)",
    "ocean.trench_width_m": "해구 단면의 폭 (trench_depth_m 만큼 깊어지는 띠)",
    "climate.p_equator": "적도 강수 띠의 크기 p₁ (위 강수 식)",
    "climate.p_equator_sigma_deg": "적도 강수 띠의 폭 (위도, 가우스 σ)",
    "climate.p_midlat": "중위도 강수 띠의 크기 p₂",
    "climate.p_midlat_center_deg": "중위도 강수 띠의 가운데 위도",
    "climate.p_midlat_sigma_deg": "중위도 강수 띠의 폭 (위도, 가우스 σ)",
    "climate.t_equator_c": "적도 해수면 연평균 기온",
    "climate.t_pole_c": "극 해수면 연평균 기온 (그 사이는 sin²(위도)로 이음)",
    "climate.lapse_rate_c_per_m": "기온 감률: 1 m 오를 때 내려가는 기온 (0.0065 = 6.5 °C/km)",
    "climate.pet_base_m_per_yr": "잠재 증발산의 바닥값 b (잠재 증발산 = a·max(T, 0) + b)",
    "climate.runoff_floor_m_per_yr": "침식에 쓰는 유출의 가장 작은 값 "
    "(사막에서도 강이 조금은 깎게)",
    "relief.hack_exponent": "Hack 법칙 지수 h (본류 길이 L = c·A^h)",
    "groundwater.fan_beta": "대수층 깊이 식의 경사 계수 β (f = α / (1 + β·S))",
    "landscape.hillslope_diffusivity_m2_per_yr": "산비탈 흙이 기어 내려가는 확산 계수 (둥근 능선)",
    "fans.enabled": "선상지 후처리를 켭니다 (false 면 선상지를 만들지 않음)",
    "fans.min_discharge_m3_per_yr": "선상지를 만들 강의 가장 작은 유량",
    "fans.max_discharge_m3_per_yr": "선상지를 만들 강의 가장 큰 유량 "
    "(큰 강은 선상지를 만들지 않음)",
    "fans.radius_m": "선상지 부채꼴의 반지름",
    "fans.slope": "선상지 표면 경사",
    "geology.surface_temperature_c": "변성 정도를 정할 때 쓰는 지표 온도 (깊은 곳 온도 = 이 값 + "
    "지온 경사 × 깊이)",
    "geology.fold_amplitude_m": "습곡 진폭 (지층이 위아래로 휘는 높이)",
    "geology.arc_belt_width_m": "화산호 띠의 폭 (섭입대 위판 쪽 화산암 지대)",
    "soil.decay_depth_m": "흙이 두꺼울수록 흙 생산이 줄어드는 깊이 h₀ (Heimsath 지수 감소)",
    "soil.thickness_cap_m": "흙 두께의 상한",
    "soil.alluvium_max_m": "충적층(강이 쌓은 퇴적층) 두께의 상한",
    "groundwater.max_depth_m": "지하수면이 지표 아래로 내려갈 수 있는 가장 깊은 값",
    "caves.levels": "동굴 층 수 (0층 = 지금 지하수면, 1층 = 옛 지하수면)",
    "caves.entrance_tolerance_m": "동굴 층이 지표와 이만큼 가까우면 입구로 봄 (τ)",
    "caves.passage_radius_m": "동굴 통로 반지름 (r)",
    "caves.noise_wavelength_m": "동굴 통로를 구불구불하게 하는 노이즈의 파장",
}

# 프로필 키의 일반 설명 (파일 주석은 프로필마다 달라서 note 로 붙임)
PROFILE_HELP: dict[str, str] = {
    "profile.grid.coarse_n_per_face": "거친 격자 해상도: 정육면체 면 한 변의 칸 수. 판·지각·기후를 "
    "이 격자에서 만듭니다",
    "profile.grid.l0_n_per_face": "행성 L0 격자 해상도: 면 한 변의 칸 수. 칸 수 6·n², 칸 크기 "
    "≈ 40,000 km / (4·n)",
    "profile.hero.max_flow_iterations": "히어로 솔버 반복 상한 "
    "(L0 는 landscape.max_flow_iterations)",
    "profile.hero.spacing_m": "히어로 유역 칸 간격 (L2). 줄이면 칸 수가 제곱으로 늘어납니다",
    "profile.hero.size_m": "히어로 유역 한 변 길이. spacing_m 의 배수로 맞춥니다. 클수록 바다에서 "
    "먼 육지 자리가 필요하고, 행성에 그런 자리가 없으면 평면 히어로로 바뀝니다",
    "profile.corridor.voxel_m": "회랑 높이맵·동굴 메시 간격 (L3, 작을수록 굽기가 오래 걸림)",
    "profile.corridor.width_m": "걷는 회랑의 폭 (히어로 한 변보다 크면 히어로에 맞춰 줄어듦)",
    "profile.corridor.length_m": "걷는 회랑의 길이 (강을 따라, 히어로 한 변보다 크면 줄어듦)",
    "profile.compute.numba_threads": "계산에 쓸 CPU 스레드 수 (0 = 모든 코어)",
}

# 왼쪽 패널에서 바로 고치지 않는 키 (시드는 따로 입력 칸이 있음)
READ_ONLY: dict[str, str] = {
    "planet.name": "행성 이름은 설정 파일 이름으로 고릅니다",
    "planet.seed": "위의 '시드' 칸에서 바꿉니다",
    "profile.name": "프로필은 위의 '프로필' 칸에서 고릅니다",
}

PROFILE_HINTS: dict[str, str] = {
    "tiny": "몇 초. 파이프라인이 도는지만 보는 시험용이라 모양은 보지 않습니다",
    "laptop": "수 분. 데모와 평소 실험용 (노트북 16 GB)",
    "lab": "오래 걸림. 보고용 고해상도 (연구실 PC)",
}

_SECTION = re.compile(r"^\s*\[\s*([A-Za-z0-9_.\-]+)\s*\]\s*(?:#(.*))?$")
_KEY = re.compile(r"^\s*([A-Za-z0-9_\-]+)\s*=(.*)$")
_BANNER = re.compile(r"^\s*#\s*-{4,}\s*(.*)$")


def _split_comment(rest: str) -> tuple[str, str]:
    """'값  # 주석' → (값, 주석). 따옴표 안의 # 는 주석으로 보지 않습니다."""
    quote = ""
    for i, ch in enumerate(rest):
        if quote:
            if ch == quote:
                quote = ""
        elif ch in "\"'":
            quote = ch
        elif ch == "#":
            return rest[:i].strip(), rest[i + 1 :].strip()
    return rest.strip(), ""


def read_comments(path: Path, prefix: str = "") -> dict[str, Any]:
    """TOML 파일의 주석을 읽습니다 (값은 tomllib 이 읽음).

    반환: {"header": 파일 맨 위 주석 줄들, "keys": {점 경로: 줄 끝 주석 (없으면 바로 위 주석)},
    "sections": {절 경로: {"banner": 가장 가까운 위 띠 주석, "comment": 절 줄의 주석}}}.
    prefix 는 점 경로 앞에 붙일 글자 (프로필은 "profile.").
    """
    header: list[str] = []
    keys: dict[str, str] = {}
    sections: dict[str, dict[str, str]] = {}
    section = ""
    banner = ""
    above: list[str] = []
    seen_body = False
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.rstrip()
        if not line.strip():
            if not seen_body and header:
                seen_body = True
            above = []
            continue
        if m := _BANNER.match(line):
            banner = m.group(1).strip()
            above = []
            seen_body = True
            continue
        if line.lstrip().startswith("#"):
            text = line.lstrip()[1:].strip()
            if not seen_body:
                header.append(text)
            else:
                above.append(text)
            continue
        seen_body = True
        if m := _SECTION.match(line):
            section = prefix + m.group(1)
            sections[section] = {"banner": banner, "comment": (m.group(2) or "").strip()}
            above = []
            continue
        if m := _KEY.match(line):
            _, comment = _split_comment(m.group(2))
            dotted = f"{section}.{m.group(1)}" if section else prefix + m.group(1)
            keys[dotted] = comment or " ".join(above)
            above = []
    return {"header": header, "keys": keys, "sections": sections}


def unit_of(key: str) -> str:
    """점 경로 key 의 단위 (UNIT_OVERRIDES → 이름 끝 UNIT_SUFFIXES → 없으면 '')."""
    if key in UNIT_OVERRIDES:
        return UNIT_OVERRIDES[key]
    name = key.rsplit(".", 1)[-1]
    for suffix, unit in UNIT_SUFFIXES:
        if name.endswith(suffix):
            return unit
    return ""


def category_of(key: str) -> str:
    if key in CATEGORY_TABLE:
        return CATEGORY_TABLE[key]
    if key.startswith("profile."):
        return "resolution"
    section = key.rsplit(".", 1)[0] if "." in key else ""
    return SECTION_CATEGORY.get(section, "literature")


def _type_name(v: Any) -> str:
    if isinstance(v, bool):
        return "bool"
    if isinstance(v, int):
        return "int"
    if isinstance(v, float):
        return "float"
    if isinstance(v, list):
        return "list"
    return "str"


def _flatten(data: dict, prefix: str = "") -> list[tuple[str, Any]]:
    out = []
    for k, v in data.items():
        dotted = f"{prefix}{k}"
        if isinstance(v, dict):
            out += _flatten(v, dotted + ".")
        else:
            out.append((dotted, v))
    return out


def planet_names() -> list[str]:
    return sorted(p.stem for p in (CONFIGS / "planets").glob("*.toml"))


def profile_names() -> list[dict[str, str]]:
    """프로필 목록 [{name, description (파일 첫 주석), hint (걸리는 시간)}]."""
    out = []
    for p in sorted((CONFIGS / "profiles").glob("*.toml")):
        header = read_comments(p)["header"]
        out.append(
            {
                "name": p.stem,
                "description": " ".join(header),
                "hint": PROFILE_HINTS.get(p.stem, ""),
            }
        )
    order = {"tiny": 0, "laptop": 1, "lab": 2}
    return sorted(out, key=lambda d: (order.get(d["name"], 9), d["name"]))


def _resolve(name: str, kind: str) -> Path:
    if not re.fullmatch(r"[A-Za-z0-9_\-]+", name or ""):
        raise ValueError(f"{kind} 이름이 올바르지 않습니다: {name!r}")
    path = CONFIGS / kind / f"{name}.toml"
    if not path.exists():
        raise ValueError(f"{path} 가 없습니다")
    return path


def build_schema(planet: str = "earth", profile: str = "laptop") -> dict[str, Any]:
    """매개변수 표를 만듭니다.

    반환 dict: planet, profile, header (행성 파일 머리 주석), profile_description, categories,
    sections [{id, label, banner, params: [키…]}], params [{key, section, name, default, type,
    unit, help, note, category, history, editable, read_only_reason, learned, source}].
    """
    planet_path = _resolve(planet, "planets")
    profile_path = _resolve(profile, "profiles")
    cfg = load_config(planet_path, profile_path).as_dict()
    pc = read_comments(planet_path)
    fc = read_comments(profile_path, prefix="profile.")
    learned_path = CONFIGS / "learned" / planet_path.name
    learned_keys = set()
    if learned_path.exists():
        learned_keys = set(read_comments(learned_path)["keys"])

    params: list[dict[str, Any]] = []
    sections: dict[str, dict[str, Any]] = {}
    for key, value in _flatten(cfg):
        is_profile = key.startswith("profile.")
        section = key.rsplit(".", 1)[0] if "." in key else ""
        if key == "profile.name":
            continue
        comments = fc if is_profile else pc
        comment = comments["keys"].get(key, "")
        if is_profile:
            help_text = PROFILE_HELP.get(key) or comment
            note = comment if key in PROFILE_HELP else ""
            source = f"configs/profiles/{profile_path.name}"
        else:
            help_text = comment or HELP_FALLBACK.get(key, "")
            note = ""
            source = f"configs/planets/{planet_path.name}"
        if key in learned_keys:
            source = f"configs/learned/{learned_path.name}"
        sec_info = comments["sections"].get(section, {})
        if section not in sections:
            sections[section] = {
                "id": section,
                "label": SECTION_LABELS.get(section, section),
                "banner": "프로필 (해상도·성능)" if is_profile else sec_info.get("banner", ""),
                "comment": sec_info.get("comment", ""),
                "params": [],
            }
        sections[section]["params"].append(key)
        params.append(
            {
                "key": key,
                "section": section,
                "name": key.rsplit(".", 1)[-1],
                "default": value,
                "type": _type_name(value),
                "unit": unit_of(key),
                "help": help_text,
                "note": note,
                "category": category_of(key),
                "history": HAND_HISTORY.get(key, ""),
                "editable": key not in READ_ONLY,
                "read_only_reason": READ_ONLY.get(key, ""),
                "learned": key in learned_keys,
                "source": source,
            }
        )
    return {
        "planet": planet_path.stem,
        "profile": profile_path.stem,
        "header": pc["header"],
        "profile_description": " ".join(fc["header"]),
        "categories": [{"id": k, **v} for k, v in CATEGORIES.items()],
        "sections": list(sections.values()),
        "params": params,
    }


def default_values(planet: str = "earth", profile: str = "laptop") -> dict[str, Any]:
    """{점 경로: 기본값} (profile.name 제외)."""
    cfg = load_config(_resolve(planet, "planets"), _resolve(profile, "profiles")).as_dict()
    return {k: v for k, v in _flatten(cfg) if k != "profile.name"}


def flat_config(config: dict) -> dict[str, Any]:
    """실행 설정(manifest 의 config) → {점 경로: 값} (profile.name 제외, 매개변수 설명 표의 값)."""
    return {k: v for k, v in _flatten(config) if k != "profile.name"}


_MISSING = object()


def config_diff_parts(
    config: dict, planet: str | None = None
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """(config_diff, config_missing) 를 기본 설정을 한 번만 읽어 함께 만듭니다."""
    profile = str((config.get("profile") or {}).get("name") or "laptop")
    planet = planet or str((config.get("planet") or {}).get("name") or "earth")
    try:
        base = default_values(planet, profile)
    except ValueError:
        return [], []
    now = flat_config(config)
    changed: list[dict[str, Any]] = []
    missing: list[dict[str, Any]] = []
    for key in sorted(set(base) | set(now)):
        a, b = base.get(key, _MISSING), now.get(key, _MISSING)
        if a is _MISSING or b is _MISSING:
            missing.append(
                {
                    "key": key,
                    "default": None if a is _MISSING else a,
                    "value": None if b is _MISSING else b,
                    "status": "added_since_run" if b is _MISSING else "removed",
                    "note": "이 실행 뒤에 생긴 키 (이 실행에는 없음)"
                    if b is _MISSING
                    else "지금 기본 설정에는 없는 키",
                    "unit": unit_of(key),
                    "category": category_of(key),
                }
            )
        elif a != b:
            changed.append(
                {
                    "key": key,
                    "default": a,
                    "value": b,
                    "status": "changed",
                    "unit": unit_of(key),
                    "category": category_of(key),
                }
            )
    return changed, missing


def config_diff(config: dict, planet: str | None = None) -> list[dict[str, Any]]:
    """실행 설정(manifest 의 config)과 기본 설정에서 값이 다른 키 목록.

    반환: [{key, default, value, unit, category, status='changed'}]. 프로필 이름은
    config['profile']['name'] 에서 읽습니다. 기본 설정 파일이 없으면 빈 목록.
    한쪽에만 있는 키(실행 뒤에 생기거나 없어진 키)는 바꾼 값이 아니므로 넣지 않습니다
    (config_missing).
    """
    return config_diff_parts(config, planet)[0]


def config_missing(config: dict, planet: str | None = None) -> list[dict[str, Any]]:
    """실행 설정과 지금 기본 설정 중 한쪽에만 있는 키 [{key, default, value, status, note, …}].

    status: 'added_since_run' (실행 뒤에 configs/ 에 생긴 키) | 'removed' (지금 기본에 없는 키).
    """
    return config_diff_parts(config, planet)[1]
