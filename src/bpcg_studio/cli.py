"""명령줄 입구 `bpcg`.

| 명령 | 하는 일 |
|---|---|
| bpcg studio | 매개변수를 바꿔 돌리고 결과를 보는 로컬 웹 도구 (docs/studio.md) |
| bpcg planet·hero·bake·all … | C# 콘솔(src/Bpcg.Cli)에 그대로 넘깁니다 (빌드는 자동) |
"""

import argparse
import subprocess
import sys

from bpcg_studio.csharp import CSharpError, cli_command, ensure_built


def _studio(argv: list[str]) -> int:
    p = argparse.ArgumentParser(prog="bpcg studio", description="B-PCG 스튜디오")
    p.add_argument("--port", type=int, default=8765, help="주소 http://127.0.0.1:<port>/")
    p.add_argument("--no-browser", action="store_true", help="브라우저를 열지 않음")
    args = p.parse_args(argv)
    from bpcg_studio.server import serve

    serve(port=args.port, open_browser=not args.no_browser)
    return 0


def main(argv: list[str] | None = None) -> int:
    """반환: 종료 코드."""
    argv = sys.argv[1:] if argv is None else list(argv)
    if argv and argv[0] == "studio":
        return _studio(argv[1:])
    try:
        ensure_built()
        return subprocess.call(cli_command(argv))
    except CSharpError as e:
        print(e, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
