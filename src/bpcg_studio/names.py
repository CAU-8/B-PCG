"""파일·폴더 이름 검사: 바깥(요청, manifest)에서 받은 이름이 폴더 밖을 가리키지 않게 합니다.

- bare_name: 경로가 섞이지 않은 파일 이름 하나만 받습니다. '/', '\\', ':' (윈도우 드라이브 상대
  경로 'D:x', NTFS 대체 스트림), 앞의 '.', NUL 을 막고, 포직스·윈도우 규칙 모두에서 이름 그대로인지
  봅니다. 그림 이름(summary.figure_path), 회랑 파일(godot.corridor_files)에 씁니다.
- relative_name: '/' 로 나눈 조각마다 bare_name 인 상대 경로 (묶음 manifest 의 필드 파일
  'surface/z_m.npy', views.LevelData.raw).
- run_folder_name: out/ 아래 바깥 실행 폴더 이름. 한글·공백은 받고(예: '지형 테스트',
  'earth_v2 copy'), 경로 구분자·'..'·앞의 '.'·제어 문자는 막습니다.
"""

from pathlib import PurePosixPath, PureWindowsPath

_BAD_CHARS = ("/", "\\", ":", "\0")


def bare_name(name: object) -> bool:
    """name 이 경로 없는 파일 이름 하나이면 True."""
    if not isinstance(name, str) or not name or name.startswith("."):
        return False
    if any(c in name for c in _BAD_CHARS) or any(ord(c) < 32 for c in name):
        return False
    return PurePosixPath(name).name == name and PureWindowsPath(name).name == name


def relative_name(name: object) -> bool:
    """name 이 폴더 안을 가리키는 상대 경로('a/b.npy')이면 True ('..', 절대 경로, 드라이브 막음)."""
    if not isinstance(name, str) or not name:
        return False
    return all(bare_name(part) for part in name.split("/"))


def run_folder_name(name: object) -> bool:
    """out/ 바로 아래 실행 폴더 이름으로 받을 수 있으면 True.

    bare_name 규칙에 더해 앞뒤 공백이 있는 이름(윈도우에서 다루기 어려움)을 막습니다.
    """
    return bare_name(name) and str(name).strip() == name
