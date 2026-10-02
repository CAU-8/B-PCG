"""명령줄 실행 `bpcg` (docs/pipeline.md 13장).

| 명령 | 하는 일 |
|---|---|
| bpcg planet --profile laptop --out out/earth | 1~4단계 → out/earth/planet (묶음·텍스처·점수표) |
| bpcg hero --from out/earth | 히어로 L2 (L0 경계조건) → out/earth/hero |
| bpcg hero --flat --out out/flat | 평면 히어로 (가짜 경계조건) → out/flat/hero |
| bpcg bake --hero out/earth/hero --engine | 회랑 굽기 → out/earth/corridor (+ engine/baked) |
| bpcg all --profile tiny --out out/tiny | planet → hero → bake 를 한 번에 |
| bpcg studio | 매개변수를 바꿔 돌리고 결과를 보는 로컬 웹 도구 (docs/studio.md) |

`bake` 는 옆에 행성 묶음(planet/)이 있으면 지구본도 굽습니다(out/<run>/globe, --no-globe 로 끔).
`hero --from` 과 `bake --hero` 는 묶음에 적힌 설정(manifest 의 config)을 그대로 씁니다. 그래서
같은 실행의 단계들이 늘 같은 설정으로 이어집니다. `--seed` 는 planet.seed 를 덮어씁니다.
`--set 키=값` (여러 번 가능)은 planet, hero --flat, all 에서 설정 값 하나를 덮어씁니다. bake 에서는
굽기에서만 쓰는 키(detail.*, profile.corridor.*)만 받습니다(bake_config). 값은 TOML
문법입니다(`--set landscape.theta=0.5`, `--set fans.enabled=false`). 없는 키나 형식이 다른 값은
바로 오류로 멈춥니다(core.config.checked_overrides).
`all` 은 단계마다 묶음을 쓴 뒤 디스크에서 다시 읽어 이어 가므로, 단계를 따로 돌린 결과와 같습니다.
설치 전에는 `uv run python -m bpcg.cli ...` 로도 돌릴 수 있습니다.
"""

import argparse
import sys
import time
from pathlib import Path

from bpcg.core.config import Config, checked_overrides, load_config, parse_assignment
from bpcg.core.paths import OUT, ROOT

PLANET_DIR = "planet"
HERO_DIR = "hero"
CORRIDOR_DIR = "corridor"
TEXTURE_DIR = "textures"
GLOBE_DIR = "globe"


def _say(msg: str) -> None:
    print(msg, flush=True)


def _set_overrides(items) -> dict:
    """--set 키=값 목록 → {점 경로: 값} (형식 검사 전). 꼴이 틀리면 SystemExit."""
    out = {}
    for item in items or []:
        try:
            key, value = parse_assignment(item)
        except ValueError as e:
            raise SystemExit(f"--set: {e}") from None
        out[key] = value
    return out


def _config(args) -> Config:
    overrides = _set_overrides(getattr(args, "set", None))
    if getattr(args, "seed", None) is not None:
        overrides["planet.seed"] = int(args.seed)
    base = load_config(args.planet, args.profile)
    if not overrides:
        return base
    try:
        checked = checked_overrides(base, overrides)
    except ValueError as e:
        raise SystemExit(f"--set: {e}") from None
    for key, value in checked.items():
        if key != "planet.seed":
            _say(f"[설정] {key} = {value!r} (기본 {base[key]!r})")
    return base.with_overrides(checked)


def _with_seed(cfg: Config, seed) -> Config:
    return cfg if seed is None else cfg.with_overrides({"planet.seed": int(seed)})


def _out_dir(args) -> Path:
    if args.out:
        return Path(args.out)
    return OUT / args.planet


def _planet_bundle_dir(path: Path) -> Path:
    """--from 으로 받은 경로에서 행성 묶음 폴더 (실행 폴더면 그 아래 planet/)."""
    if (path / "manifest.json").exists():
        return path
    if (path / PLANET_DIR / "manifest.json").exists():
        return path / PLANET_DIR
    raise SystemExit(f"행성 묶음을 찾지 못했습니다: {path} (planet/manifest.json 이 없습니다)")


def _hero_bundle_dir(path: Path) -> Path:
    if (path / "manifest.json").exists():
        return path
    if (path / HERO_DIR / "manifest.json").exists():
        return path / HERO_DIR
    raise SystemExit(f"히어로 묶음을 찾지 못했습니다: {path} (manifest.json 이 없습니다)")


# ---------------------------------------------------------------- 단계
def run_planet(cfg: Config, run_dir: Path) -> Path:
    """행성을 만들어 run_dir/planet 에 묶음·면 텍스처를 씁니다. 반환: 묶음 폴더."""
    from bpcg.bake.bundle import save_planet_state, write_json
    from bpcg.bake.textures import face_textures
    from bpcg.pipeline import generate_planet

    t = time.perf_counter()
    _say(f"[행성] 시작: 프로필 {cfg.profile.get('name')}, 시드 {cfg.planet.seed}")
    planet = generate_planet(cfg, log=_say)
    out = run_dir / PLANET_DIR
    save_planet_state(out, planet, cfg)
    face_textures(planet, out / TEXTURE_DIR)
    write_json(out / "scorecard.json", planet.diag.get("scorecard", {}))
    _say(f"[행성] 묶음을 썼습니다: {out} ({time.perf_counter() - t:.1f} s)")
    return out


def run_hero(cfg: Config, run_dir: Path, planet_dir: Path | None, flat: bool) -> Path:
    """히어로를 만들어 run_dir/hero 에 묶음을 씁니다. planet_dir 이 없거나 flat 이면 평면 히어로."""
    from bpcg.bake.bundle import load_planet_state, save_hero_state, write_json
    from bpcg.pipeline import generate_hero

    t = time.perf_counter()
    planet = None
    if not flat:
        if planet_dir is None:
            raise SystemExit("히어로를 만들려면 --from <행성 묶음> 이나 --flat 이 필요합니다")
        _say(f"[히어로] 행성 묶음을 읽습니다: {planet_dir}")
        planet = load_planet_state(planet_dir)
    hero = generate_hero(cfg, planet=planet, log=_say)
    out = run_dir / HERO_DIR
    save_hero_state(out, hero, cfg)
    write_json(out / "scorecard.json", hero.diag.get("scorecard", {}))
    _say(f"[히어로] 묶음을 썼습니다: {out} ({time.perf_counter() - t:.1f} s)")
    return out


BAKE_SECTIONS = ("detail",)  # 굽기에서만 쓰는 절. 묶음 설정에 없으면 지금 설정 파일의 값을 씀
BAKE_SET_PREFIXES = ("detail.", "profile.corridor.")  # bake --set 으로 바꿀 수 있는 키


def bake_config(hero_dir: Path, seed=None, sets=None) -> Config:
    """회랑 굽기 설정 = 히어로 묶음 설정 + (묶음에 없으면) 지금 설정 파일의 굽기 절 + bake --set.

    굽기 절([detail])이 생기기 전에 만든 히어로 묶음도 지금 configs/planets/<행성>.toml 의 값으로
    굽습니다. --set 은 굽기에서만 쓰는 키(BAKE_SET_PREFIXES)만 받습니다. 히어로를 다시 풀어야 하는
    키는 bpcg all/hero 에서 바꿉니다.
    """
    from bpcg.bake.bundle import config_from_manifest, read_manifest

    cfg = _with_seed(config_from_manifest(read_manifest(hero_dir)), seed)
    missing = [s for s in BAKE_SECTIONS if s not in cfg]
    if missing:
        planet = str(cfg.planet.get("name", "earth"))
        current = load_config(planet, str(cfg.profile.get("name") or "laptop"))
        add = {s: current.get(s).as_dict() for s in missing if s in current}
        if add:
            cfg = cfg.with_overrides(add)
            _say(
                f"[굽기] 묶음 설정에 [{', '.join(add)}] 가 없어 지금 설정 파일({planet}.toml)의 "
                "값을 씁니다"
            )
    overrides = _set_overrides(sets)
    if overrides:
        bad = [k for k in overrides if not k.startswith(BAKE_SET_PREFIXES)]
        if bad:
            raise SystemExit(
                f"--set: bake 에서는 {', '.join(BAKE_SET_PREFIXES)} 로 시작하는 키만 바꿀 수 "
                f"있습니다: {', '.join(bad)} (지형을 바꾸려면 bpcg all 에서 --set)"
            )
        try:
            checked = checked_overrides(cfg, overrides)
        except ValueError as e:
            raise SystemExit(f"--set: {e}") from None
        for key, value in checked.items():
            _say(f"[설정] {key} = {value!r} (묶음 {cfg[key]!r})")
        cfg = cfg.with_overrides(checked)
    return cfg


def run_bake(hero_dir: Path, out_dir: Path, engine: bool, seed=None, sets=None) -> dict:
    """히어로 묶음에서 회랑을 굽습니다. engine 이면 engine/baked 에도 씁니다 (bake_config)."""
    from bpcg.bake.bundle import load_hero_state
    from bpcg.bake.corridor import bake_corridor

    cfg = bake_config(hero_dir, seed, sets)
    _say(f"[굽기] 히어로 묶음을 읽습니다: {hero_dir}")
    hero = load_hero_state(hero_dir)
    engine_dir = ROOT / "engine" / "baked" if engine else None
    return bake_corridor(hero, cfg, out_dir, engine_dir=engine_dir, log=_say)


def run_globe(planet_dir: Path, run_dir: Path, engine: bool) -> Path:
    """행성 묶음에서 지구본 파일을 굽습니다 (bake.globe). engine 이면 engine/baked/globe 에도.

    히어로 자리는 같은 실행의 corridor/hero 묶음에서 찾습니다(globe.hero_from_run).
    """
    from bpcg.bake.bundle import config_from_manifest, load_planet_state, read_manifest
    from bpcg.bake.globe import bake_globe, hero_from_run

    man = read_manifest(planet_dir)
    out = run_dir / GLOBE_DIR
    bake_globe(
        load_planet_state(planet_dir),
        config_from_manifest(man),
        out,
        hero=hero_from_run(run_dir),
        log=_say,
        face_basis=man.get("face_basis"),
        engine_dir=ROOT / "engine" / "baked" / GLOBE_DIR if engine else None,
    )
    return out


def _drop_engine_globe() -> None:
    """행성이 없는 실행(평면 히어로)을 엔진에 넣을 때, 지난 실행의 지구본을 지웁니다.

    남겨 두면 엔진의 지구본이 지금 회랑과 상관없는 행성을 보여 줍니다.
    """
    import shutil

    old = ROOT / "engine" / "baked" / GLOBE_DIR
    if (old / "globe.json").exists():
        shutil.rmtree(old)
        _say(f"[지구본] 행성이 없는 실행이라 엔진의 지난 지구본을 지웠습니다: {old}")


# ---------------------------------------------------------------- 명령
def cmd_planet(args) -> int:
    from bpcg.pipeline import apply_threads

    cfg = _config(args)
    apply_threads(cfg)
    run_planet(cfg, _out_dir(args))
    return 0


def cmd_hero(args) -> int:
    from bpcg.bake.bundle import config_from_manifest, read_manifest
    from bpcg.pipeline import apply_threads

    if args.flat:
        cfg = _config(args)
        run_dir = _out_dir(args)
        apply_threads(cfg)
        run_hero(cfg, run_dir, None, flat=True)
        return 0
    if not args.from_dir:
        raise SystemExit("hero 는 --from <행성 묶음 폴더> 나 --flat 중 하나가 필요합니다")
    if args.set:
        raise SystemExit("--set 은 hero --flat 에서만 씁니다 (--from 은 묶음 설정을 그대로 씀)")
    planet_dir = _planet_bundle_dir(Path(args.from_dir))
    cfg = _with_seed(config_from_manifest(read_manifest(planet_dir)), args.seed)
    run_dir = Path(args.out) if args.out else planet_dir.parent
    apply_threads(cfg)
    run_hero(cfg, run_dir, planet_dir, flat=False)
    return 0


def cmd_bake(args) -> int:
    hero_dir = _hero_bundle_dir(Path(args.hero))
    out_dir = Path(args.out) if args.out else hero_dir.parent / CORRIDOR_DIR
    run_bake(hero_dir, out_dir, args.engine, seed=args.seed, sets=args.set)
    planet_dir = hero_dir.parent / PLANET_DIR
    if args.no_globe:
        pass
    elif (planet_dir / "manifest.json").exists():
        run_globe(planet_dir, hero_dir.parent, args.engine)
    elif args.engine:
        _drop_engine_globe()
    return 0


def cmd_all(args) -> int:
    from bpcg.bake.bundle import config_from_manifest, read_manifest
    from bpcg.pipeline import apply_threads

    t = time.perf_counter()
    cfg = _config(args)
    apply_threads(cfg)
    run_dir = _out_dir(args)
    if args.flat:
        hero_dir = run_hero(cfg, run_dir, None, flat=True)
    else:
        planet_dir = run_planet(cfg, run_dir)
        cfg = config_from_manifest(read_manifest(planet_dir))
        try:
            hero_dir = run_hero(cfg, run_dir, planet_dir, flat=False)
        except ValueError as e:
            _say(f"[히어로] 행성에서 히어로 자리를 찾지 못해 평면 히어로로 바꿉니다: {e}")
            hero_dir = run_hero(cfg, run_dir, None, flat=True)
    run_bake(hero_dir, run_dir / CORRIDOR_DIR, args.engine)
    if not args.flat:
        run_globe(planet_dir, run_dir, args.engine)
    elif args.engine:
        _drop_engine_globe()
    _say(f"[전체] 끝: {time.perf_counter() - t:.1f} s → {run_dir}")
    return 0


def cmd_studio(args) -> int:
    from bpcg.studio.server import serve

    serve(port=args.port, open_browser=not args.no_browser)
    return 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="bpcg", description="B-PCG 행성·히어로·회랑 생성 (docs/pipeline.md 13장)"
    )
    sub = p.add_subparsers(dest="command", required=True)

    def common(sp, out_help: str) -> None:
        sp.add_argument("--planet", default="earth", help="행성 설정 이름 (configs/planets)")
        sp.add_argument("--profile", default="laptop", help="프로필 이름 (configs/profiles)")
        sp.add_argument("--out", default=None, help=out_help)
        sp.add_argument("--seed", type=int, default=None, help="planet.seed 덮어쓰기")
        sp.add_argument(
            "--set",
            action="append",
            default=[],
            metavar="KEY=VALUE",
            help="설정 값 하나 덮어쓰기, 여러 번 가능 (예: --set landscape.theta=0.5, 값은 TOML)",
        )

    sp = sub.add_parser("planet", help="행성 1~4단계, L0 묶음, 면 텍스처, 점수표")
    common(sp, "실행 폴더 (기본 out/<행성>), 묶음은 그 아래 planet/")
    sp.set_defaults(func=cmd_planet)

    sp = sub.add_parser("hero", help="히어로 L2 (--from 행성 묶음 또는 --flat)")
    common(sp, "실행 폴더 (기본: --from 의 실행 폴더), 묶음은 그 아래 hero/")
    sp.add_argument("--from", dest="from_dir", default=None, help="행성 실행 폴더 또는 묶음 폴더")
    sp.add_argument("--flat", action="store_true", help="행성 없이 평면 히어로 (가짜 경계조건)")
    sp.set_defaults(func=cmd_hero)

    sp = sub.add_parser("bake", help="히어로 묶음에서 회랑 굽기")
    sp.add_argument("--hero", required=True, help="히어로 묶음 폴더 (또는 그 실행 폴더)")
    sp.add_argument("--out", default=None, help="출력 폴더 (기본: 히어로 옆 corridor/)")
    sp.add_argument("--engine", action="store_true", help="engine/baked/ 에도 씀")
    sp.add_argument("--seed", type=int, default=None, help="planet.seed 덮어쓰기 (노이즈용)")
    sp.add_argument(
        "--set",
        action="append",
        default=None,
        metavar="KEY=VALUE",
        help="굽기 설정 덮어쓰기, detail.* 과 profile.corridor.* 만 (예: detail.fractal_gain=2.0)",
    )
    sp.add_argument("--no-globe", action="store_true", help="옆의 행성 묶음으로 지구본을 굽지 않음")
    sp.set_defaults(func=cmd_bake)

    sp = sub.add_parser("all", help="planet → hero → bake 를 한 번에")
    common(sp, "실행 폴더 (기본 out/<행성>)")
    sp.add_argument("--flat", action="store_true", help="행성 대신 평면 히어로")
    sp.add_argument("--engine", action="store_true", help="engine/baked/ 에도 씀")
    sp.set_defaults(func=cmd_all)

    sp = sub.add_parser("studio", help="매개변수를 바꿔 돌리고 결과를 보는 로컬 웹 도구")
    sp.add_argument("--port", type=int, default=8765, help="주소 http://127.0.0.1:<port>/")
    sp.add_argument("--no-browser", action="store_true", help="브라우저를 열지 않음")
    sp.set_defaults(func=cmd_studio)
    return p


def main(argv: list[str] | None = None) -> int:
    """명령줄 입구. 반환: 종료 코드."""
    args = build_parser().parse_args(argv)
    return int(args.func(args) or 0)


if __name__ == "__main__":
    sys.exit(main())
