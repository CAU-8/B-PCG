"""명령줄 실행 `bpcg` (docs/pipeline.md 13장).

| 명령 | 하는 일 |
|---|---|
| bpcg planet --profile laptop --out out/earth | 1~4단계 → out/earth/planet (묶음·텍스처·점수표) |
| bpcg hero --from out/earth | 히어로 L2 (L0 경계조건) → out/earth/hero |
| bpcg hero --flat --out out/flat | 평면 히어로 (가짜 경계조건) → out/flat/hero |
| bpcg bake --hero out/earth/hero --engine | 회랑 굽기 → out/earth/corridor (+ engine/baked) |
| bpcg all --profile tiny --out out/tiny | planet → hero → bake 를 한 번에 |

`hero --from` 과 `bake --hero` 는 묶음에 적힌 설정(manifest 의 config)을 그대로 씁니다. 그래서
같은 실행의 단계들이 늘 같은 설정으로 이어집니다. `--seed` 는 planet.seed 를 덮어씁니다.
`all` 은 단계마다 묶음을 쓴 뒤 디스크에서 다시 읽어 이어 가므로, 단계를 따로 돌린 결과와 같습니다.
설치 전에는 `uv run python -m bpcg.cli ...` 로도 돌릴 수 있습니다.
"""

import argparse
import sys
import time
from pathlib import Path

from bpcg.core.config import Config, load_config
from bpcg.core.paths import OUT, ROOT

PLANET_DIR = "planet"
HERO_DIR = "hero"
CORRIDOR_DIR = "corridor"
TEXTURE_DIR = "textures"


def _say(msg: str) -> None:
    print(msg, flush=True)


def _config(args) -> Config:
    overrides = {}
    if getattr(args, "seed", None) is not None:
        overrides["planet.seed"] = int(args.seed)
    return load_config(args.planet, args.profile, overrides or None)


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


def run_bake(hero_dir: Path, out_dir: Path, engine: bool, seed=None) -> dict:
    """히어로 묶음에서 회랑을 굽습니다. engine 이면 engine/baked 에도 씁니다."""
    from bpcg.bake.bundle import config_from_manifest, load_hero_state, read_manifest
    from bpcg.bake.corridor import bake_corridor

    cfg = _with_seed(config_from_manifest(read_manifest(hero_dir)), seed)
    _say(f"[굽기] 히어로 묶음을 읽습니다: {hero_dir}")
    hero = load_hero_state(hero_dir)
    engine_dir = ROOT / "engine" / "baked" if engine else None
    return bake_corridor(hero, cfg, out_dir, engine_dir=engine_dir, log=_say)


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
    planet_dir = _planet_bundle_dir(Path(args.from_dir))
    cfg = _with_seed(config_from_manifest(read_manifest(planet_dir)), args.seed)
    run_dir = Path(args.out) if args.out else planet_dir.parent
    apply_threads(cfg)
    run_hero(cfg, run_dir, planet_dir, flat=False)
    return 0


def cmd_bake(args) -> int:
    hero_dir = _hero_bundle_dir(Path(args.hero))
    out_dir = Path(args.out) if args.out else hero_dir.parent / CORRIDOR_DIR
    run_bake(hero_dir, out_dir, args.engine, seed=args.seed)
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
    _say(f"[전체] 끝: {time.perf_counter() - t:.1f} s → {run_dir}")
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
    sp.set_defaults(func=cmd_bake)

    sp = sub.add_parser("all", help="planet → hero → bake 를 한 번에")
    common(sp, "실행 폴더 (기본 out/<행성>)")
    sp.add_argument("--flat", action="store_true", help="행성 대신 평면 히어로")
    sp.add_argument("--engine", action="store_true", help="engine/baked/ 에도 씀")
    sp.set_defaults(func=cmd_all)
    return p


def main(argv: list[str] | None = None) -> int:
    """명령줄 입구. 반환: 종료 코드."""
    args = build_parser().parse_args(argv)
    return int(args.func(args) or 0)


if __name__ == "__main__":
    sys.exit(main())
