"""C# 대조 시험의 golden 자료를 만듭니다 (docs/csharp_port.md 7장).

    uv run python csharp/golden/export_golden.py                 # 모든 사례
    uv run python csharp/golden/export_golden.py --only core/noise

src/bpcg 의 함수를 부르기만 하고 그 입력과 출력을 파일로 씁니다.

- out/golden/<모듈>/<사례>.npz (+ .json): 모든 사례. BPCG_OUT 을 따르고 git 에서 빠집니다.
- csharp/golden/data/<모듈>/<사례>.npz (+ .json): commit=True 인 작은 사례. 각 1 MB 이하이고
  커밋합니다(.gitignore 예외). Python 없이도 핵심 대조가 돌게 합니다.

npz 안의 배열 이름은 입력 'in.<이름>', 출력 'out.<이름>' 입니다. 글자·설정·예외처럼 배열이
아닌 값은 같은 이름의 JSON 에 둡니다. 무작위 입력은 고정 시드의 np.random.Generator 로 만들고
입력도 함께 저장하므로, numpy 난수열이 바뀌어도 대조는 깨지지 않습니다.
"""

from __future__ import annotations

import argparse
import io
import json
import math
import platform
import shutil
import sys
from collections.abc import Callable
from pathlib import Path

import numba
import numpy as np
import scipy

from bpcg import __version__
from bpcg.bake.bundle import git_commit
from bpcg.core import config as cfgmod
from bpcg.core import constants, cubesphere, distance, fields, graph, hashing, noise, resample
from bpcg.core.paths import OUT, ROOT
from bpcg.hydro import accumulate as hacc
from bpcg.hydro import depressions, network, routing
from bpcg.planet import climate, crust, materials, ocean, plates, uplift

COMMIT_ROOT = ROOT / "csharp" / "golden" / "data"
MAX_COMMIT_BYTES = 1_000_000  # docs/conventions.md 8절: 커밋하는 바이너리는 각 1 MB 이하
R_EARTH = 6_371_000.0


class Writer:
    """사례를 out/golden 과 (commit=True 면) csharp/golden/data 에 씁니다."""

    def __init__(self, root: Path, commit_root: Path | None):
        self.root = root
        self.commit_root = commit_root
        self.cases: list[dict] = []

    def case(
        self,
        module: str,
        name: str,
        *,
        inputs: dict | None = None,
        outputs: dict | None = None,
        meta: dict | None = None,
        commit: bool = False,
    ) -> None:
        arrays = {}
        for prefix, group in (("in", inputs or {}), ("out", outputs or {})):
            for key, value in group.items():
                arrays[f"{prefix}.{key}"] = np.asarray(value)
        buf = io.BytesIO()
        np.savez(buf, **arrays)
        data = buf.getvalue()
        doc = {"module": module, "case": name, "meta": meta or {}}
        text = json.dumps(doc, ensure_ascii=False, indent=2) + "\n"
        targets = [self.root]
        if commit and self.commit_root is not None:
            if len(data) > MAX_COMMIT_BYTES:
                raise ValueError(
                    f"커밋하는 golden 이 1 MB 를 넘습니다: {module}/{name} {len(data)}"
                )
            targets.append(self.commit_root)
        for root in targets:
            d = root / module
            d.mkdir(parents=True, exist_ok=True)
            (d / f"{name}.npz").write_bytes(data)
            (d / f"{name}.json").write_text(text, encoding="utf-8", newline="\n")
        self.cases.append(
            {"module": module, "case": name, "bytes": len(data), "commit": bool(commit)}
        )


def rng(seed: int) -> np.random.Generator:
    return np.random.default_rng(seed)


def expect_error(fn: Callable, *args, **kwargs) -> str | None:
    """예외가 나면 그 종류 이름, 아니면 None."""
    try:
        fn(*args, **kwargs)
    except Exception as e:  # golden 은 어떤 예외가 났는지만 적습니다
        return type(e).__name__
    return None


# ---------------------------------------------------------------- core/hashing
EDGE_I64 = np.array(
    [0, 1, -1, 2, -2, 7, 101, 102, 201, 210, 211, 212, 220, 7101, 7301, 9101, 2**31 - 1, -(2**31)]
    + [2**53, 2**62, 2**63 - 1, -(2**63), -(2**63) + 1],
    dtype=np.int64,
)


def export_hashing(w: Writer) -> None:
    m = "core/hashing"
    x = np.concatenate(
        [
            EDGE_I64.view(np.uint64),
            np.array([2**64 - 1, 2**63, 0x9E3779B97F4A7C15], dtype=np.uint64),
            rng(1).integers(0, 2**63, 2000, dtype=np.int64).view(np.uint64),
        ]
    )
    out = np.array([hashing.splitmix64(np.uint64(v)) for v in x], dtype=np.uint64)
    w.case(m, "splitmix64", inputs={"x": x}, outputs={"h": out}, commit=True)

    g = rng(2)
    a = np.concatenate([np.repeat(EDGE_I64, 3), g.integers(-(2**63), 2**63 - 1, 3000)])
    b = np.concatenate([np.tile(EDGE_I64, 3), g.integers(-(2**63), 2**63 - 1, 3000)])
    c = np.concatenate([np.roll(np.repeat(EDGE_I64, 3), 5), g.integers(-1000, 1000, 3000)])
    a, b, c = (v.astype(np.int64) for v in (a, b, c))
    h3 = np.array(
        [hashing.hash3(*map(np.int64, t)) for t in zip(a, b, c, strict=True)], dtype=np.uint64
    )
    hu = np.array([hashing.hash_unit(*map(np.int64, t)) for t in zip(a, b, c, strict=True)])
    w.case(
        m,
        "hash3_hash_unit",
        inputs={"a": a, "b": b, "c": c},
        outputs={"hash3": h3.astype(np.uint64), "hash_unit": hu.astype(np.float64)},
        commit=True,
    )

    ids = np.arange(-5, 1000, dtype=np.int64)
    for seed in (0, -3, 2**63 - 1):
        for stream in (101, 102):
            out = hashing.hash_uniform_array(ids, seed, stream)
            w.case(
                m,
                f"hash_uniform_array_s{seed}_t{stream}".replace("-", "m"),
                inputs={"ids": ids, "seed": np.int64(seed), "stream": np.int64(stream)},
                outputs={"u": out},
                commit=True,
            )


# ---------------------------------------------------------------- core/noise
P_TEST = np.array([[0.6, -0.48, 0.64], [0.0, 0.0, 1.0], [-0.36, 0.48, -0.8]])


def export_noise(w: Writer) -> None:
    m = "core/noise"
    pts = [
        (0.0, 0.0, 0.0),
        (-0.0, -0.0, -0.0),
        (-22.0, -0.0, -35.0),
        (1.0, 2.0, 3.0),
        (1.0 + 1e-9, 2.0 - 1e-9, 3.0),
        (1e-20, -1e-20, 0.5),
        (1e9, -1e9, 7.25),
        (2.0**52 + 0.5, -(2.0**52) - 0.5, 0.125),
        (1e19, -1e19, 1e300),
        (0.3, 0.7, 1.2),
        (-5.25, 3.5, 0.125),
        (100.1, -200.2, 300.3),
    ]
    seeds = [0, 1, -7, 42, 2**63 - 1, -(2**63)]
    xs, ys, zs, ss = [], [], [], []
    for p in pts:
        for s in seeds:
            xs.append(p[0])
            ys.append(p[1])
            zs.append(p[2])
            ss.append(s)
    g = rng(3)
    rp = g.uniform(-60.0, 60.0, (5000, 3))
    rs = g.integers(-(2**31), 2**31, 5000)
    x = np.concatenate([np.array(xs), rp[:, 0]])
    y = np.concatenate([np.array(ys), rp[:, 1]])
    z = np.concatenate([np.array(zs), rp[:, 2]])
    s = np.concatenate([np.array(ss, dtype=np.int64), rs.astype(np.int64)])
    v = np.array(
        [
            noise.gradient_noise3(float(a), float(b), float(c), int(d))
            for a, b, c, d in zip(x, y, z, s, strict=True)
        ]
    )
    w.case(
        m,
        "gradient_noise3",
        inputs={"x": x, "y": y, "z": z, "seed": s},
        outputs={"v": v},
        commit=True,
    )

    # 옥타브 표 (내부 함수지만 fbm 의 시드·이동·주파수를 따로 대조하려고 부름)
    params = [
        (0, 1, 5, 0.5, 2.0, 1.0),
        (123, 1, 4, 0.5, 2.0, 1.2),
        (-5, 3, 6, 0.55, 2.1, 2.5),
        (2**62, 3, 1, 0.9, 1.7, 0.3),
    ]
    for i, (seed, n_comp, octaves, gain, lac, freq) in enumerate(params):
        seeds_t, offsets, freqs, amps, inv_norm = noise._octave_tables(
            seed, n_comp, octaves, gain, lac, freq
        )
        w.case(
            m,
            f"octave_tables_{i}",
            inputs={
                "seed": np.int64(seed),
                "n_comp": np.int64(n_comp),
                "octaves": np.int64(octaves),
                "gain": np.float64(gain),
                "lacunarity": np.float64(lac),
                "frequency": np.float64(freq),
            },
            outputs={
                "seeds": seeds_t,
                "offsets": offsets,
                "freqs": freqs,
                "amps": amps,
                "inv_norm": np.float64(inv_norm),
            },
            commit=True,
        )

    rp2 = rng(4).uniform(-3.0, 3.0, (2000, 3))
    cases = [
        ("fbm3_test_p", P_TEST, 0, {}),
        ("fbm3_test_p_123", P_TEST, 123, {"octaves": 4, "frequency": 1.2}),
        ("fbm3_random", rp2, 7, {}),
        (
            "fbm3_random_params",
            rp2,
            -11,
            {"octaves": 3, "gain": 0.6, "lacunarity": 2.3, "frequency": 4.0},
        ),
    ]
    for name, p, seed, kw in cases:
        out = noise.fbm3(p, seed, **kw)
        w.case(
            m,
            name,
            inputs={"points": p, "seed": np.int64(seed)},
            outputs={"v": out},
            meta={"kwargs": kw},
            commit=True,
        )
    vcases = [
        ("vector_fbm3_test_p", P_TEST, 0, {"frequency": 2.5}),
        ("vector_fbm3_random", rp2, 9, {"octaves": 4}),
    ]
    for name, p, seed, kw in vcases:
        out = noise.vector_fbm3(p, seed, **kw)
        w.case(
            m,
            name,
            inputs={"points": p, "seed": np.int64(seed)},
            outputs={"v": out},
            meta={"kwargs": kw},
            commit=True,
        )
    errors = {
        "octaves0": expect_error(noise.fbm3, P_TEST, 0, octaves=0),
        "gain0": expect_error(noise.fbm3, P_TEST, 0, gain=0.0),
        "freq_neg": expect_error(noise.fbm3, P_TEST, 0, frequency=-1.0),
        "nan_point": expect_error(noise.fbm3, np.array([[np.nan, 0.0, 0.0]]), 0),
        "bad_shape": expect_error(noise.fbm3, np.zeros((4, 2)), 0),
    }
    empty = noise.fbm3(np.zeros((0, 3)), 0)
    w.case(m, "fbm3_errors", outputs={"empty": empty}, meta={"errors": errors}, commit=True)


# ---------------------------------------------------------------- core/constants, fields
def export_constants_fields(w: Writer) -> None:
    rs = np.array([6.371e6, 1.0, 3.3895e6, R_EARTH])
    ds = np.array([5514.0, 1.0, 3933.5, 5514.0])
    g = np.array([constants.gravity(r, d) for r, d in zip(rs, ds, strict=True)])
    errors = {
        "zero_radius": expect_error(constants.gravity, 0.0, 5514.0),
        "neg_density": expect_error(constants.gravity, 6.371e6, -1.0),
        "nan_radius": expect_error(constants.gravity, math.nan, 5514.0),
        "inf_density": expect_error(constants.gravity, 6.371e6, math.inf),
    }
    w.case(
        "core/constants",
        "gravity",
        inputs={"radius_m": rs, "density_kg_m3": ds},
        outputs={"g": g},
        meta={
            "errors": errors,
            "G_GRAV": constants.G_GRAV,
            "SECONDS_PER_YEAR": constants.SECONDS_PER_YEAR,
            "RHO_WATER": constants.RHO_WATER,
            "MU_WATER": constants.MU_WATER,
        },
        commit=True,
    )
    table = [
        [name, info.group, info.unit, info.dtype, info.description]
        for name, info in fields.FIELDS.items()
    ]
    try:
        fields.check_fields({"z_m": 0, "zz": 0, "Aa": 0, "a": 0})
        msg = None
    except ValueError as e:
        msg = str(e)
    w.case(
        "core/fields",
        "fields_table",
        meta={"fields": table, "groups": list(fields.GROUPS), "check_error": msg},
        commit=True,
    )


# ---------------------------------------------------------------- core/config
def typed(v):
    """설정 값을 형 이름과 함께 (C# 이 int·float·bool·글자·리스트를 구분해 비교하게)."""
    if isinstance(v, bool):
        return ["bool", v]
    if isinstance(v, int):
        return ["int", str(v)]
    if isinstance(v, float):
        return ["float", repr(v)]
    if isinstance(v, cfgmod.BareText):
        return ["bare", str(v)]
    if isinstance(v, str):
        return ["str", v]
    if isinstance(v, list | tuple):
        return ["list", [typed(x) for x in v]]
    if isinstance(v, dict):
        return ["dict", {k: typed(x) for k, x in v.items()}]
    return [type(v).__name__, repr(v)]


def export_config(w: Writer) -> None:
    m = "core/config"
    for profile in ("tiny", "laptop", "lab"):
        cfg = cfgmod.load_config("earth", profile)
        data = cfg.as_dict()
        w.case(
            m,
            f"load_earth_{profile}",
            meta={
                "planet": "earth",
                "profile": profile,
                "digest": cfg.digest(),
                "sorted_json": json.dumps(data, sort_keys=True, ensure_ascii=False),
                "manifest_json": json.dumps(data, ensure_ascii=False, indent=2) + "\n",
                "typed": typed(data),
            },
            commit=True,
        )

    base = cfgmod.load_config("earth", "tiny")
    overrides = [
        {"landscape.theta": 0.5},
        {"planet.seed": 3},
        {"caves.levels": 2.0},
        {"landscape.theta": 1},
        {"fans.enabled": False},
        {"plates.speed_m_per_yr": [0.02, 0.07]},
        {"profile.hero.size_m": 40000.0},
        {"detail.fractal_gain": 1},
    ]
    results = []
    for ov in overrides:
        checked = cfgmod.checked_overrides(base, ov)
        new = base.with_overrides(checked)
        results.append(
            {
                "overrides": typed(ov),
                "checked": typed(checked),
                "digest": new.digest(),
                "sorted_json": json.dumps(new.as_dict(), sort_keys=True, ensure_ascii=False),
            }
        )
    # 검사를 거치지 않은 with_overrides (새 키 생성, int 그대로)
    raw = base.with_overrides({"landscape.k_ref": 150, "zz.new.key": 1.5})
    results.append(
        {
            "overrides": typed({"landscape.k_ref": 150, "zz.new.key": 1.5}),
            "checked": None,
            "digest": raw.digest(),
            "sorted_json": json.dumps(raw.as_dict(), sort_keys=True, ensure_ascii=False),
            "manifest_json": json.dumps(raw.as_dict(), ensure_ascii=False, indent=2) + "\n",
        }
    )
    w.case(m, "overrides", meta={"base": "earth/tiny", "cases": results}, commit=True)

    bad = [
        {"landscape.thetaa": 0.5},
        {"plates.speed": [0.1, 0.2]},
        {"caves.levels": 2.5},
        {"caves.levels": True},
        {"landscape.theta": math.nan},
        {"landscape.theta": math.inf},
        {"landscape": 1.0},
        {"planet.name": "mars"},
        {"profile.name": "x"},
        {"plates.speed_m_per_yr": [0.1]},
        {"plates.speed_m_per_yr": [0.1, "a"]},
        {"landscape.theta": cfgmod.BareText("abc")},
        {"planet.axis.0": 1.0},
        {"planet..seed": 1},
    ]
    errs = []
    for ov in bad:
        try:
            cfgmod.checked_overrides(base, ov)
            errs.append({"overrides": typed(ov), "error": None})
        except ValueError as e:
            errs.append({"overrides": typed(ov), "error": str(e)})
    w.case(m, "overrides_errors", meta={"base": "earth/tiny", "cases": errs}, commit=True)

    texts = [
        "landscape.theta=0.5",
        "fans.enabled=false",
        "plates.speed_m_per_yr=[0.02, 0.07]",
        'k="글자"',
        "planet.name=mars",
        "x=inf",
        "x=-nan",
        "x=-0.0",
        "x=0x10",
        "x=1_000",
        "x=1979-05-27",
        "x=07:32:00",
        "x=07:32",
        "x=1.",
        "x=.5",
        "x=01",
        "x=0.5 # 주석",
        "x={a=1}",
        "x=1e400",
        " a.b = 2 ",
        "k==1",
        "=1",
        "k=",
        "noequals",
    ]
    out = []
    for t in texts:
        try:
            key, value = cfgmod.parse_assignment(t)
            out.append({"text": t, "key": key, "value": typed(value), "error": None})
        except ValueError as e:
            out.append({"text": t, "key": None, "value": None, "error": str(e)})
    w.case(m, "parse_assignment", meta={"cases": out}, commit=True)


# ---------------------------------------------------------------- io: json·npy·npz 시험 벡터
def export_io(w: Writer) -> None:
    g = rng(5)
    bits = g.integers(0, 2**64, 20000, dtype=np.uint64)
    vals = bits.view(np.float64)
    vals = vals[np.isfinite(vals)]
    edge = np.array(
        [
            0.0,
            -0.0,
            5e-324,
            2.2250738585072014e-308,
            1.7976931348623157e308,
            1e16,
            1e15,
            9999999999999998.0,
            1e-4,
            1e-5,
            0.1 + 0.2,
            2.0**53,
            2.0**63,
            0.5,
            1.5,
            -1.0,
            100.0,
            6371000.0,
            3.15576e7,
            1e-3,
            0.0001,
            123456789012345680.0,
            1.335e18,
            0.1,
            1e22,
            1e21,
            1e-7,
            2.5e-5,
        ]
    )
    allv = np.concatenate([edge, vals, g.uniform(-1e6, 1e6, 3000), g.uniform(0, 1, 3000)])
    reprs = [json.dumps(float(v)) for v in allv]
    f32 = g.standard_normal(2000).astype(np.float32) * np.float32(1000)
    reprs32 = [json.dumps(float(v)) for v in f32]
    w.case(
        "io/pyjson",
        "float_repr",
        inputs={"values": allv, "values_f32": f32},
        meta={"reprs": reprs, "reprs_f32": reprs32},
        commit=True,
    )
    special = {
        "nan": json.dumps(math.nan),
        "inf": json.dumps(math.inf),
        "-inf": json.dumps(-math.inf),
    }
    sample = {
        "b": 1,
        "a": [1, 2.5, None, True, False, '글자 "따옴표" \\ \n\t\x01\x1f\x7f /'],
        "empty_list": [],
        "empty_dict": {},
        "nested": {"z": [[], [{}]], "y": {"x": -0.0, "w": 1e-05, "v": 12, "u": 12.0}},
        "emoji": "😀Ａ",
        "big": 2**63 - 1,
        "neg": -(2**63),
    }
    w.case(
        "io/pyjson",
        "layout",
        meta={
            "special": special,
            "sample": sample,
            "indent2": json.dumps(sample, ensure_ascii=False, indent=2),
            "sorted": json.dumps(sample, ensure_ascii=False, sort_keys=True),
            "compact": json.dumps(sample, ensure_ascii=False),
            "ascii": json.dumps(sample),
            "allow_nan_false_error": expect_error(json.dumps, math.nan, allow_nan=False),
        },
        commit=True,
    )

    arrays = {
        "f4_1d": np.arange(7, dtype=np.float32) * np.float32(0.5),
        "f8_2d": rng(6).standard_normal((5, 3)),
        "i4_1d": np.array([-1, 0, 1, 2**31 - 1], dtype=np.int32),
        "i8_1d": np.array([-(2**63), 0, 2**63 - 1], dtype=np.int64),
        "u1_3d": np.arange(24, dtype=np.uint8).reshape(2, 3, 4),
        "i1_1d": np.array([-128, -1, 0, 1, 127], dtype=np.int8),
        "b1_1d": np.array([True, False, True]),
        "f8_0d": np.array(3.25),
        "f4_empty": np.zeros((0,), dtype=np.float32),
        "i4_empty2": np.zeros((3, 0), dtype=np.int32),
        "f4_nan": np.array([np.nan, 1.0, np.inf, -np.inf], dtype=np.float32),
        "f8_long": np.arange(6144, dtype=np.float64),
        "u8_1d": np.array([0, 2**64 - 1], dtype=np.uint64),
    }
    raw = {}
    for name, arr in arrays.items():
        buf = io.BytesIO()
        np.save(buf, arr, allow_pickle=False)
        raw[name] = np.frombuffer(buf.getvalue(), dtype=np.uint8)
    w.case(
        "io/npy",
        "save_bytes",
        inputs=arrays,
        outputs=raw,
        meta={"names": list(arrays)},
        commit=True,
    )
    buf = io.BytesIO()
    pos = rng(7).standard_normal((10, 3))
    nbr = np.arange(80, dtype=np.int32).reshape(10, 8)
    np.savez(buf, pos=pos, nbr=nbr)
    w.case(
        "io/npz",
        "savez_bytes",
        inputs={"pos": pos, "nbr": nbr},
        outputs={"bytes": np.frombuffer(buf.getvalue(), dtype=np.uint8)},
        commit=True,
    )


# ---------------------------------------------------------------- core/cubesphere
def export_cubesphere(w: Writer) -> None:
    m = "core/cubesphere"
    for n, R, commit in (
        (1, R_EARTH, True),
        (2, R_EARTH, True),
        (3, 15.0, True),
        (5, 1.0, True),
        (16, R_EARTH, True),
        (16, 9964050.17398628, False),
        (32, R_EARTH, False),
        (33, R_EARTH, False),
    ):
        grid = cubesphere.cubesphere_grid(n, R)
        w.case(
            m,
            f"grid_n{n}_r{int(R)}",
            inputs={"n": np.int64(n), "R": np.float64(R)},
            outputs={"pos": grid.pos, "area": grid.area},
            commit=commit,
        )
    ab = np.array([-1.0 - 3.0 / 16, -1.0, -0.37, 0.0, 0.5, 1.0, 1.0 + 1.0 / 16, 1.0 + 3.0 / 16])
    aa, bb = np.meshgrid(ab, ab, indexing="ij")
    outs = {f"f{f}": cubesphere.to_sphere(f, aa.ravel(), bb.ravel()) for f in range(6)}
    xy = rng(8).uniform(-3.0, 3.0, (500, 2))
    w.case(
        m,
        "to_sphere_omega",
        inputs={"a": aa.ravel(), "b": bb.ravel(), "X": xy[:, 0], "Y": xy[:, 1]},
        outputs={**outs, "omega": cubesphere.omega(xy[:, 0], xy[:, 1])},
        commit=True,
    )
    g32 = cubesphere.cubesphere_grid(32, R_EARTH)
    idx = rng(9).integers(0, g32.pos.shape[0], (100, 2))
    p1 = g32.pos[idx[:, 0]] / R_EARTH
    p2 = g32.pos[idx[:, 1]] / R_EARTH
    tiny = p1[:5] + 1e-7 * rng(10).standard_normal((5, 3))
    p1 = np.concatenate([p1, p1[:5], p1[:3], p1[:3]])
    p2 = np.concatenate([p2, tiny, p1[:3], -p1[:3]])
    w.case(
        m,
        "neighbor_distance",
        inputs={"p1": p1, "p2": p2, "R": np.float64(R_EARTH)},
        outputs={"d": cubesphere.neighbor_distance(p1, p2, R_EARTH)},
        commit=True,
    )
    edge_pts = np.array(
        [
            [1.0, 1.0, 0.0],
            [1.0, 1.0, 1.0],
            [-1.0, -1.0, -1.0],
            [0.0, 0.0, 0.0],
            [-0.0, 0.0, -0.0],
            [1.0, -0.0, 0.0],
            [0.0, 0.0, -1.0],
            [2.0, 1.0, 2.0],
            [0.5, 0.5, 0.5 + 1e-15],
        ]
    )
    pts = np.concatenate([edge_pts, rng(11).standard_normal((5000, 3)), g32.pos[::7]])
    face, a, b = cubesphere.to_face(pts)
    outs = {"f": face.astype(np.int64), "a": a, "b": b}
    for n in (1, 3, 16, 32):
        outs[f"cell_n{n}"] = cubesphere.cell_of(pts, n)
    w.case(m, "to_face_cell_of", inputs={"p": pts}, outputs=outs, commit=True)
    for n in (1, 2, 3, 4, 5, 16, 32, 33):
        w.case(
            m,
            f"neighbor_table_n{n}",
            inputs={"n": np.int64(n)},
            outputs={"nbr": cubesphere.neighbor_table(n)},
            commit=n <= 16,
        )
    for n in (1, 2, 4, 16, 64):
        w.case(
            m,
            f"cross_face_pairs_n{n}",
            inputs={"n": np.int64(n)},
            outputs={"pairs": cubesphere.cross_face_pairs(n)},
            commit=n <= 16,
        )


# ---------------------------------------------------------------- core/graph
def export_graph(w: Writer) -> None:
    m = "core/graph"
    for n, R, jitter, seed, commit in (
        (16, R_EARTH, 0.0, 0, True),
        (32, R_EARTH, 1.0, 0, False),
        (7, R_EARTH, 1.0, 123456789, True),
        (16, R_EARTH, 0.4, 5, False),
    ):
        gr = graph.sphere_graph(n, R, jitter, seed)
        w.case(
            m,
            f"sphere_n{n}_j{jitter}_s{seed}",
            inputs={
                "n": np.int64(n),
                "R": np.float64(R),
                "jitter": np.float64(jitter),
                "seed": np.int64(seed),
            },
            outputs={
                "pos": gr.pos,
                "nbr": gr.nbr,
                "dist": gr.dist,
                "area": gr.area,
                "spacing": np.float64(gr.spacing),
                "unit": gr.unit(),
            },
            commit=commit,
        )
    for ny, nx, dx, jitter, seed, origin, commit in (
        (64, 64, 100.0, 1.0, 1738104521, (-3200.0, 3200.0), False),
        (16, 16, 400.0, 1.0, 7919, (-3200.0, 3200.0), True),
        (1, 5, 10.0, 0.0, 0, (0.0, 0.0), True),
        (20, 30, 25.0, 0.4, 2, (1000.0, 500.0), True),
        (12, 9, 50.0, 0.0, 0, (-225.0, 300.0), True),
    ):
        gr = graph.flat_graph(ny, nx, dx, jitter, seed, origin)
        w.case(
            m,
            f"flat_{ny}x{nx}_j{jitter}_s{seed}",
            inputs={
                "ny": np.int64(ny),
                "nx": np.int64(nx),
                "dx": np.float64(dx),
                "jitter": np.float64(jitter),
                "seed": np.int64(seed),
                "origin": np.array(origin, dtype=np.float64),
            },
            outputs={
                "pos": gr.pos,
                "nbr": gr.nbr,
                "dist": gr.dist,
                "area": gr.area,
                "spacing": np.float64(gr.spacing),
                "boundary": gr.boundary_mask(),
            },
            commit=commit,
        )


# ---------------------------------------------------------------- core/distance
def _graph_inputs(gr: graph.CellGraph) -> dict:
    return {
        "kind_sphere": np.bool_(gr.kind == "sphere"),
        "shape": np.array(gr.shape, dtype=np.int64),
        "pos": gr.pos,
        "nbr": gr.nbr,
        "dist": gr.dist,
        "area": gr.area,
        "spacing": np.float64(gr.spacing),
        "R": np.float64(gr.R if gr.R is not None else 0.0),
        "origin": np.array(gr.origin, dtype=np.float64),
    }


def export_distance(w: Writer) -> None:
    m = "core/distance"
    sph = graph.sphere_graph(32, R_EARTH, 1.0, 0)
    flat = graph.flat_graph(64, 64, 100.0, 1.0, 1738104521, (-3200.0, 3200.0))
    sph7 = graph.sphere_graph(7, R_EARTH, 1.0, 123456789)
    cases = []
    u = hashing.hash_uniform_array(np.arange(sph.n_cells, dtype=np.int64), 11, 9001)
    cases.append(("sphere32_inf", sph, u < 0.03, np.inf, False))
    cases.append(("sphere32_3sp", sph, u < 0.03, 3.0 * sph.spacing, False))
    cases.append(("sphere32_zero", sph, u < 0.03, 0.0, False))
    u7 = hashing.hash_uniform_array(np.arange(sph7.n_cells, dtype=np.int64), 3, 9001)
    cases.append(("sphere7_inf", sph7, u7 < 0.1, np.inf, True))
    cases.append(("sphere7_3sp", sph7, u7 < 0.1, 3.0 * sph7.spacing, True))
    uf = hashing.hash_uniform_array(np.arange(flat.n_cells, dtype=np.int64), 5, 9001)
    cases.append(("flat64_inf", flat, uf < 0.01, np.inf, False))
    cases.append(("flat64_1000", flat, uf < 0.01, 1000.0, False))
    f15 = graph.flat_graph(1, 5, 1.0)
    cases.append(("flat1x5_tie", f15, np.array([True, False, False, False, True]), np.inf, True))
    f55 = graph.flat_graph(5, 5, 1.0)
    src55 = np.zeros(25, dtype=bool)
    src55[[2, 22]] = True
    cases.append(("flat5x5_tie", f55, src55, np.inf, True))
    cases.append(("flat5x5_none", f55, np.zeros(25, dtype=bool), np.inf, True))
    f2 = graph.flat_graph(20, 30, 25.0, 1.0, 2, (1000.0, 500.0))
    u2 = hashing.hash_uniform_array(np.arange(f2.n_cells, dtype=np.int64), 2, 9001)
    cases.append(("flat20x30_j1", f2, u2 < 0.02, 200.0, True))
    for name, gr, mask, max_dist, commit in cases:
        dist, src = distance.nearest_source(gr, mask, max_dist)
        max_key = _max_key(gr, max_dist)
        key, psrc = distance._propagate_sources(
            np.ascontiguousarray(gr.pos), np.ascontiguousarray(gr.nbr), mask, max_key
        )
        w.case(
            m,
            name,
            inputs={**_graph_inputs(gr), "is_source": mask, "max_dist": np.float64(max_dist)},
            outputs={
                "dist": dist,
                "src": src,
                "max_key": np.float64(max_key),
                "prop_key": key,
                "prop_src": psrc,
            },
            commit=commit,
        )
    # nearest_source_values: 여러 dtype 과 (N, 2) 값
    vals = {
        "f8": rng(12).standard_normal(f2.n_cells),
        "i4": np.arange(f2.n_cells, dtype=np.int32) - 7,
        "u1": (np.arange(f2.n_cells) % 251).astype(np.uint8),
        "b1": (np.arange(f2.n_cells) % 3) == 0,
        "f4": rng(13).standard_normal(f2.n_cells).astype(np.float32),
        "f8x2": rng(14).standard_normal((f2.n_cells, 2)),
    }
    outs = {}
    for k, v in vals.items():
        d, s, o = distance.nearest_source_values(f2, u2 < 0.02, v, 150.0)
        outs[f"{k}.dist"] = d
        outs[f"{k}.src"] = s
        outs[f"{k}.values"] = o
    w.case(
        m,
        "values_flat20x30",
        inputs={
            **_graph_inputs(f2),
            "is_source": u2 < 0.02,
            "max_dist": np.float64(150.0),
            **{f"values.{k}": v for k, v in vals.items()},
        },
        outputs=outs,
        commit=True,
    )


def _max_key(gr: graph.CellGraph, max_dist: float) -> float:
    """distance.nearest_source 안의 max_key 계산을 그대로 다시 합니다(전파 커널을 따로 부르려고)."""
    max_dist = float(max_dist)
    if not math.isfinite(max_dist):
        return math.inf
    if gr.kind == "sphere":
        radius = float(gr.R)
        if max_dist >= math.pi * radius:
            return math.inf
        chord = 2.0 * radius * math.sin(0.5 * max_dist / radius)
        return chord * chord * (1.0 + 1e-9)
    return max_dist * max_dist * (1.0 + 1e-9)


# ---------------------------------------------------------------- core/resample
def export_resample(w: Writer) -> None:
    m = "core/resample"
    for n, L in ((2, 1), (3, 1), (4, 2), (7, 1), (16, 1), (16, 2), (16, 8), (32, 1), (33, 3)):
        st = resample._ghost_stencil(n, L)
        names = ("edge_dst", "edge_src", "edge_w", "corner_dst", "corner_src", "corner_w")
        w.case(
            m,
            f"ghost_stencil_n{n}_l{L}",
            inputs={"n": np.int64(n), "layers": np.int64(L)},
            outputs=dict(zip(names, st, strict=True)),
            commit=n <= 16,
        )
    g = rng(15)
    for n, L, kind in (
        (4, 1, "f8"),
        (7, 2, "f8"),
        (16, 1, "f8"),
        (16, 3, "f4"),
        (5, 1, "i4"),
        (6, 1, "nan"),
        (8, 2, "negzero"),
        (10, 2, "const"),
    ):
        if kind == "f8":
            faces = g.standard_normal((6, n, n))
        elif kind == "f4":
            faces = g.standard_normal((6, n, n)).astype(np.float32)
        elif kind == "i4":
            faces = g.integers(-50, 50, (6, n, n)).astype(np.int32)
        elif kind == "nan":
            faces = g.standard_normal((6, n, n))
            faces[2, 0, 3] = np.nan
            faces[4, 5, 5] = -0.0
        elif kind == "negzero":
            faces = np.full((6, n, n), -0.0)
        else:
            faces = np.full((6, n, n), 3.25)
        w.case(
            m,
            f"add_ghost_n{n}_l{L}_{kind}",
            inputs={"faces": faces, "layers": np.int64(L)},
            outputs={"padded": resample.add_ghost_layers(faces, L)},
            commit=True,
        )
    # sample_sphere: 면 경계 근처 점과 무작위 점, 선형과 nearest
    for n, commit in ((4, True), (16, False)):
        field = g.standard_normal(6 * n * n)
        cat = (np.arange(6 * n * n) % 13).astype(np.uint8)
        unit = g.standard_normal((3000, 3))
        eps = np.array([1.0 + 1e-9, 1.0 - 1e-9, 1.0, -1.0, -1.0 - 1e-9])
        edge = np.stack([np.ones(5), eps, 0.3 * np.ones(5)], axis=1)
        unit = np.concatenate([unit, edge, -edge, np.array([[1.0, 1.0, 1.0], [7.0, -7.0, 7.0]])])
        w.case(
            m,
            f"sample_sphere_n{n}",
            inputs={"field": field, "cat": cat, "n": np.int64(n), "unit": unit},
            outputs={
                "linear": resample.sample_sphere(field, n, unit, "linear"),
                "nearest": resample.sample_sphere(cat, n, unit, "nearest"),
            },
            commit=commit,
        )
    g32 = graph.sphere_graph(32, R_EARTH, 1.0, 0)
    f16 = g.standard_normal(6 * 16 * 16)
    c16 = (np.arange(6 * 16 * 16) % 7).astype(np.int32)
    w.case(
        m,
        "resample_16_to_32",
        inputs={"field": f16, "cat": c16, "n_src": np.int64(16), "unit": g32.unit()},
        outputs={
            "linear": resample.resample_sphere(f16, 16, g32, "linear"),
            "nearest": resample.resample_sphere(c16, 16, g32, "nearest"),
        },
        commit=False,
    )


# ---------------------------------------------------------------- hydro
def _terrain(gr: graph.CellGraph, seed: int, scale: float, base: float) -> np.ndarray:
    """그래프 위 노이즈 지형 [m] (golden 입력용)."""
    p = gr.unit() if gr.kind == "sphere" else gr.pos / 1000.0
    return noise.fbm3(p * 2.0, seed) * scale + base


def _hydro_case(
    w: Writer, name: str, gr: graph.CellGraph, z: np.ndarray, outlet: np.ndarray, commit: bool
) -> None:
    """채움 → D8(두 번, 히스테리시스) → 순서 → 누적 → 유역·강 구간을 한 사례로 씁니다."""
    m = "hydro"
    eps = 1e-3
    zhat = depressions.fill_depressions(z, gr.nbr, outlet)
    zt = depressions.fill_epsilon(z, gr.nbr, outlet, eps)
    rcv, slope, n1 = routing.d8_receivers(zt, gr.nbr, gr.dist, outlet)
    # 지형을 조금 흔든 뒤 지난 수신 셀로 히스테리시스를 겁니다(η = 0.02, 설정 기본값과 같음)
    z2 = z + noise.fbm3((gr.unit() if gr.kind == "sphere" else gr.pos / 1000.0) * 7.0, 99) * 5.0
    zt2 = depressions.fill_epsilon(z2, gr.nbr, outlet, eps)
    rcv2, slope2, n2 = routing.d8_receivers(zt2, gr.nbr, gr.dist, outlet, prev=rcv, eta=0.02)
    start, donors = routing.donor_lists(rcv2)
    order = routing.topo_order(rcv2)
    area_acc = hacc.accumulate(rcv2, order, gr.area)
    q = hacc.accumulate(rcv2, order, gr.area * 0.37)
    one = hacc.accumulate(rcv2, order, 1.0)
    cnt = hacc.donors_count(rcv2)
    outl = network.outlet_of(rcv2, order)
    is_river = (q > np.quantile(q, 0.8)) & ~outlet
    segs = network.river_segments(rcv2, order, is_river)
    cells = np.concatenate(segs) if segs else np.zeros(0, dtype=np.int64)
    offs = np.cumsum([0] + [len(x) for x in segs]).astype(np.int64)
    w.case(
        m,
        name,
        inputs={
            "z": z,
            "z2": z2,
            "nbr": gr.nbr,
            "dist": gr.dist,
            "area": gr.area,
            "is_outlet": outlet,
            "eps": np.float64(eps),
            "eta": np.float64(0.02),
            "is_river": is_river,
        },
        outputs={
            "zhat": zhat,
            "zt": zt,
            "rcv": rcv,
            "slope": slope,
            "n_changed": np.int64(n1),
            "zt2": zt2,
            "rcv2": rcv2,
            "slope2": slope2,
            "n_changed2": np.int64(n2),
            "start": start,
            "donors": donors,
            "order": order,
            "area_acc": area_acc,
            "q": q,
            "one": one,
            "donors_count": cnt,
            "outlet_of": outl,
            "seg_cells": cells,
            "seg_offsets": offs,
        },
        commit=commit,
    )


def export_hydro(w: Writer) -> None:
    sph = graph.sphere_graph(32, R_EARTH, 1.0, 0)
    z = _terrain(sph, 4, 3000.0, 200.0)
    _hydro_case(w, "sphere32", sph, z, z < 0.0, commit=False)
    sph7 = graph.sphere_graph(7, R_EARTH, 1.0, 123456789)
    z7 = _terrain(sph7, 5, 2000.0, 100.0)
    _hydro_case(w, "sphere7", sph7, z7, z7 < 0.0, commit=True)
    fl = graph.flat_graph(64, 64, 100.0, 1.0, 1738104521, (-3200.0, 3200.0))
    zf = _terrain(fl, 6, 400.0, 600.0)
    out_f = (
        fl.boundary_mask() & (np.arange(fl.n_cells) % 64 < 5) & (np.arange(fl.n_cells) >= 63 * 64)
    )
    _hydro_case(w, "flat64", fl, zf, out_f, commit=False)
    # 정수 고도 + 흔들기 없는 평면: 경사 동률과 넓은 평지(채움 FIFO)가 많이 생깁니다
    fl0 = graph.flat_graph(20, 30, 25.0)
    zi = np.floor(_terrain(fl0, 7, 6.0, 3.0))
    out0 = np.zeros(fl0.n_cells, dtype=bool)
    out0[[0, 29, 599]] = True
    _hydro_case(w, "flat20x30_ties", fl0, zi, out0, commit=True)
    # 부호 있는 0: 출구 −0.0 옆의 +0.0 칸 (분석 절의 손 사례)
    nbr = np.array([[3, -1], [2, -1], [1, 3], [0, 2]], dtype=np.int32)
    zz = np.array([-0.0, 0.0, 0.0, 0.0])
    oo = np.array([True, True, False, False])
    w.case(
        "hydro",
        "signed_zero",
        inputs={"z": zz, "nbr": nbr, "is_outlet": oo},
        outputs={"zhat": depressions.fill_depressions(zz, nbr, oo)},
        commit=True,
    )


# ---------------------------------------------------------------- numerics/tanh
def _tanh_inputs(r: np.random.Generator, n_random: int) -> np.ndarray:
    """np.tanh 대조용 입력: 구간 경계(2^k) 근처, 넓은 크기 범위, 특수값."""
    edges = []
    for k in range(-30, 6):
        b = 2.0**k
        for d in range(-3, 4):
            edges.append(np.nextafter(b, np.inf) if d > 0 else b)
            v = b
            for _ in range(abs(d)):
                v = np.nextafter(v, np.inf if d > 0 else -np.inf)
            edges.append(v)
    special = [0.0, -0.0, np.inf, -np.inf, np.nan, 5e-324, -5e-324, 2.2250738585072014e-308]
    special += [0.125, 0.25, 0.5, 1.0, 2.0, 4.0, 8.0, 16.0, 18.5, 19.0, 19.0625, 20.0, 22.0, 1e300]
    mags = 10.0 ** r.uniform(-30.0, 3.0, n_random)
    sign = np.where(r.random(n_random) < 0.5, -1.0, 1.0)
    uni = r.uniform(-25.0, 25.0, n_random)
    x = np.concatenate([np.array(edges), -np.array(edges), np.array(special), mags * sign, uni])
    return x.astype(np.float64)


def export_tanh(w: Writer) -> None:
    m = "numerics/tanh"
    x = _tanh_inputs(rng(7101), 12_000)
    w.case(m, "vectors", inputs={"x": x}, outputs={"tanh": np.tanh(x)}, commit=True)
    xl = _tanh_inputs(rng(7102), 500_000)
    w.case(m, "large", inputs={"x": xl}, outputs={"tanh": np.tanh(xl)}, commit=False)


# ---------------------------------------------------------------- planet
PLANET_CFG = ("earth", "tiny")


def _prefixed(prefix: str, d: dict) -> dict:
    """dict 의 배열 값만 prefix 를 붙여 꺼냅니다(스칼라·목록은 meta 로)."""
    return {f"{prefix}{k}": v for k, v in d.items() if isinstance(v, np.ndarray)}


def _scalars(d: dict) -> dict:
    return {k: v for k, v in d.items() if isinstance(v, int | float) and not isinstance(v, bool)}


def _materials_case(w: Writer, name: str, n_c: int, commit: bool, overrides: dict | None = None):
    """거친 격자의 사슬 1 단계별 출력과 build_materials 전체를 씁니다."""
    m = "planet"
    cfg = cfgmod.load_config(*PLANET_CFG, overrides=overrides)
    R = float(cfg.planet.radius_m)
    gr = graph.sphere_graph(n_c, R)
    score = crust.continent_score(gr, cfg)
    cont = crust.continent_mask(gr, cfg)
    seeds = plates.plate_seeds(cfg)
    omega0 = plates.plate_omegas(cfg, R)
    kappa = float(cfg.plates.boundary_kappa)
    cls = plates.classify_boundaries(gr, plates.assign_plates(gr.unit(), seeds, cfg), omega0, kappa)
    fields, info = materials.build_materials(gr, cfg)
    pinfo = info["plates"]
    outputs = {
        "continent_score": score,
        "continental": cont,
        **_prefixed("cls.", cls),
        **_prefixed("f.", fields),
        **_prefixed("info.", info),
        **_prefixed("plates_info.", pinfo),
        "sea_level_m": np.float64(info["sea_level_m"]),
        "ocean_fraction": np.float64(info["ocean_fraction"]),
        "continental_fraction": np.float64(info["continental_fraction"]),
    }
    meta = {
        "config": list(PLANET_CFG),
        "overrides": overrides or {},
        "n_per_face": n_c,
        "field_order": list(fields),
        "info_order": list(info),
        "plates_info_order": list(pinfo),
        "flips": pinfo["flips"],
        "plates_without_divergent": pinfo["plates_without_divergent"],
        "n_boundary": pinfo["n_boundary"],
    }
    w.case(m, name, outputs=outputs, meta=meta, commit=commit)
    return gr, fields, info, cfg


def _transfer_case(w: Writer, name: str, coarse: tuple, n_f: int, with_info: bool, commit: bool):
    """거친 격자 재료(_materials_case 의 반환)를 L0 (흔들기 있음) 로 옮깁니다."""
    gr, cf, ci, cfg = coarse
    n_c = int(gr.shape[1])
    fine = graph.sphere_graph(
        n_f,
        float(cfg.planet.radius_m),
        jitter=float(cfg.landscape.jitter),
        seed=int(cfg.planet.seed),
    )
    fields, info = materials.transfer_materials(gr, cf, ci if with_info else None, fine, cfg)
    inputs = {**_prefixed("cf.", cf), "coarse_z_platform_base_m": ci["z_platform_base_m"]}
    outputs = {
        **_prefixed("f.", fields),
        **_prefixed("info.", info),
        "sea_level_m": np.float64(info["sea_level_m"]),
        "ocean_fraction": np.float64(info["ocean_fraction"]),
        "continental_fraction": np.float64(info["continental_fraction"]),
    }
    meta = {
        "config": list(PLANET_CFG),
        "n_coarse": n_c,
        "n_fine": n_f,
        "jitter": float(cfg.landscape.jitter),
        "seed": int(cfg.planet.seed),
        "with_info": with_info,
        "field_order": list(fields),
        "info_order": list(info),
    }
    w.case("planet", name, inputs=inputs, outputs=outputs, meta=meta, commit=commit)


def export_planet(w: Writer) -> None:
    m = "planet"
    c16 = _materials_case(w, "materials_c16", 16, commit=True)
    _transfer_case(w, "transfer_c16_f32", c16, 32, True, commit=False)
    c8 = _materials_case(w, "materials_c8", 8, commit=True)
    _transfer_case(w, "transfer_c8_f12", c8, 12, True, commit=True)
    _transfer_case(w, "transfer_c8_f12_noinfo", c8, 12, False, commit=True)
    # 판 뒤집기(negate·away_from_neighbor)와 끝까지 발산 경계가 없는 판이 남는 경우
    _materials_case(w, "materials_c8_s72", 8, commit=True, overrides={"planet.seed": 72})
    _materials_case(
        w, "materials_c8_p20s43", 8, commit=True, overrides={"plates.count": 20, "planet.seed": 43}
    )

    # 작은 함수: 손으로 고른 경계값 + 무작위 값
    cfg = cfgmod.load_config(*PLANET_CFG)
    r = rng(7301)
    ages = np.concatenate(
        [
            np.array([0.0, -0.0, -5.0, 1e-12, 69.99999999, 70.0, 70.00000001, 200.0, 1e6, np.nan]),
            r.uniform(0.0, 250.0, 300),
        ]
    )
    thick = np.concatenate([np.array([0.0, 35_000.0, 40_000.0, 1e5]), r.uniform(5e3, 8e4, 300)])
    sx = np.concatenate(
        [np.array([-0.5, -0.0, 0.0, 0.5, 1.0, 1.5, np.nan]), r.uniform(-0.2, 1.2, 200)]
    )
    nt = 400
    d_conv = np.where(r.random(nt) < 0.1, np.inf, r.uniform(0.0, 4e5, nt))
    side = r.integers(-1, 2, nt).astype(np.int8)
    kind = r.integers(0, 4, nt).astype(np.uint8)
    p = 10.0 ** r.uniform(-3.0, 1.0, 500)
    pet = 10.0 ** r.uniform(-3.0, 1.0, 500)
    lat = r.uniform(-np.pi / 2, np.pi / 2, 300)
    zt = r.uniform(-3000.0, 6000.0, 300)
    w.case(
        m,
        "functions",
        inputs={
            "ages": ages,
            "thick": thick,
            "sx": sx,
            "d_conv": d_conv,
            "side": side,
            "kind": kind,
            "p": p,
            "pet": pet,
            "lat": lat,
            "z": zt,
        },
        outputs={
            "ocean_depth": crust.ocean_depth_m(ages, 2500.0),
            "airy": crust.airy_elevation_m(thick, cfg),
            "smoothstep": crust.smoothstep(sx),
            "trench": crust.trench_offset_m(d_conv, side, kind, cfg),
            "arc_distance_m": np.float64(uplift.arc_distance_m(cfg)),
            "runoff": climate.budyko_runoff(p, pet),
            "temperature": climate.surface_temperature(lat, zt, cfg),
            "temperature_noz": climate.surface_temperature(lat, None, cfg),
        },
        meta={"config": list(PLANET_CFG), "ridge_depth_m": 2500.0},
        commit=True,
    )

    # 해수면·바다 마스크: 노이즈 지형 + 기울인 자전축의 위도·기후
    cfg_t = cfg.with_overrides({"planet.axis": [0.3, -0.2, 0.9]})
    s8 = graph.sphere_graph(8, R_EARTH, 1.0, 99)
    z8 = _terrain(s8, 11, 4000.0, -500.0)
    wv = float(np.sum(s8.area) * 1500.0)
    h8 = ocean.sea_level(z8, s8.area, wv)
    w.case(
        m,
        "ocean_sphere8",
        inputs={"z": z8, "water_volume": np.float64(wv), "h_probe": np.float64(250.0)},
        outputs={
            "sea_level": np.float64(h8),
            "volume_at_h": np.float64(ocean.ocean_volume(z8, s8.area, h8)),
            "volume_probe": np.float64(ocean.ocean_volume(z8, s8.area, 250.0)),
            "ocean_mask": ocean.ocean_mask(s8, z8, h8),
            "ocean_mask_probe": ocean.ocean_mask(s8, z8, 250.0),
            "latitude": climate.latitude_rad(s8, cfg_t),
            **_prefixed("climate.", climate.generate_climate(s8, cfg_t, z=np.maximum(z8, 0.0))),
            **_prefixed("climate_seed.", climate.generate_climate(s8, cfg_t, seed=777)),
        },
        meta={
            "graph": {"n": 8, "R": R_EARTH, "jitter": 1.0, "seed": 99},
            "axis": [0.3, -0.2, 0.9],
            "climate_seed": 777,
        },
        commit=True,
    )

    # 평면 그래프: 융기·기후 (lat_deg)
    fl = graph.flat_graph(24, 24, 1000.0, 1.0, 7302, (-12_000.0, 12_000.0))
    n = fl.n_cells
    r = rng(7303)
    ff = {
        "crust_type": (r.random(n) < 0.7).astype(np.uint8),
        "is_ocean": r.random(n) < 0.2,
        "convergence_kind": r.integers(0, 4, n).astype(np.uint8),
        "subduction_side": r.integers(-1, 2, n).astype(np.int8),
        "convergence_m_per_yr": r.uniform(-0.05, 0.08, n),
        "dist_convergent_m": np.where(r.random(n) < 0.1, np.inf, r.uniform(0.0, 4e5, n)),
        "dist_divergent_m": np.where(r.random(n) < 0.1, np.inf, r.uniform(0.0, 1e5, n)),
    }
    zf = r.uniform(-200.0, 3000.0, n)
    w.case(
        m,
        "flat24",
        inputs={f"ff.{k}": v for k, v in ff.items()} | {"z": zf},
        outputs={
            **_prefixed("uplift.", uplift.generate_uplift(fl, ff, cfg)),
            **_prefixed("climate.", climate.generate_climate(fl, cfg, z=zf, lat_deg=37.5)),
            "latitude": climate.latitude_rad(fl, cfg, 37.5),
        },
        meta={
            "graph": {"nx": 24, "ny": 24, "dx": 1000.0, "jitter": 1.0, "seed": 7302},
            "origin": [-12_000.0, 12_000.0],
            "lat_deg": 37.5,
            "field_order": list(ff),
        },
        commit=True,
    )


EXPORTS: dict[str, Callable[[Writer], None]] = {
    "core/hashing": export_hashing,
    "core/noise": export_noise,
    "core/constants": export_constants_fields,
    "core/config": export_config,
    "io": export_io,
    "core/cubesphere": export_cubesphere,
    "core/graph": export_graph,
    "core/distance": export_distance,
    "core/resample": export_resample,
    "hydro": export_hydro,
    "numerics/tanh": export_tanh,
    "planet": export_planet,
}


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--out", type=Path, default=OUT / "golden", help="모든 사례를 쓸 폴더")
    ap.add_argument("--no-commit-data", action="store_true", help="csharp/golden/data 에 쓰지 않음")
    ap.add_argument("--only", action="append", help="이 이름으로 시작하는 묶음만 (여러 번 가능)")
    args = ap.parse_args(argv)
    commit_root = None if args.no_commit_data else COMMIT_ROOT
    names = [k for k in EXPORTS if not args.only or any(k.startswith(o) for o in args.only)]
    if not args.only:
        # 전부 다시 만들 때는 지난 사례를 지웁니다(커밋 폴더의 README.md 는 남김).
        if args.out.exists():
            shutil.rmtree(args.out)
        if commit_root is not None and commit_root.exists():
            for child in commit_root.iterdir():
                if child.is_dir():
                    shutil.rmtree(child)
                elif child.name == "manifest.json":
                    child.unlink()
    w = Writer(args.out, commit_root)
    for name in names:
        print(f"[golden] {name}", flush=True)
        EXPORTS[name](w)
    cases = w.cases
    if args.only:
        # 일부만 다시 만들 때는 지난 manifest 의 다른 묶음 사례를 그대로 둡니다.
        old = args.out / "manifest.json"
        if old.exists():
            prev = json.loads(old.read_text(encoding="utf-8")).get("cases", [])
            done = {(c["module"], c["case"]) for c in w.cases}
            kept = [c for c in prev if (c["module"], c["case"]) not in done]
            cases = kept + w.cases
    manifest = {
        "created_by": "csharp/golden/export_golden.py",
        "bpcg_version": __version__,
        "git_commit": git_commit(),
        "python": sys.version.split()[0],
        "numpy": np.__version__,
        "numba": numba.__version__,
        "scipy": scipy.__version__,
        "platform": platform.platform(),
        "machine": platform.machine(),
        "cases": cases,
    }
    text = json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "manifest.json").write_text(text, encoding="utf-8", newline="\n")
    if commit_root is not None:
        committed = dict(manifest, cases=[c for c in cases if c["commit"]])
        committed.pop("git_commit")
        commit_root.mkdir(parents=True, exist_ok=True)
        (commit_root / "manifest.json").write_text(
            json.dumps(committed, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
            newline="\n",
        )
    total = sum(c["bytes"] for c in w.cases)
    kept = sum(c["bytes"] for c in w.cases if c["commit"])
    print(f"[golden] 사례 {len(w.cases)}개, {total / 1e6:.1f} MB → {args.out}", flush=True)
    print(f"[golden] 커밋하는 사례 {kept / 1e6:.2f} MB → {commit_root}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
