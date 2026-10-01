"""analysis/veg 스크립트가 analysis/afrisar 의 hydro.py, extract.py 를 쓰게 이어 줍니다.

analysis/ 는 패키지가 아니라 파일로 실행하는 스크립트 모음이라, 옆 폴더 모듈을 가져오려면
그 폴더를 sys.path 에 더해야 합니다. 그 일을 이 파일 한 곳에서만 합니다 (경로는 이 파일 기준).

쓰는 법: from afrisar_link import AFRISAR_DIR, VEG_DIR, extract, hydro
"""

import sys
from pathlib import Path

from bpcg.core.paths import DERIVED

_AFRISAR_SRC = Path(__file__).resolve().parents[1] / "afrisar"
if str(_AFRISAR_SRC) not in sys.path:
    sys.path.insert(0, str(_AFRISAR_SRC))

import extract  # noqa: E402
import hydro  # noqa: E402

AFRISAR_DIR = DERIVED / "afrisar"  # geocode.py, hydro.py, ground_canopy.py 결과
VEG_DIR = DERIVED / "veg"  # 이 폴더 스크립트의 결과

__all__ = ["AFRISAR_DIR", "VEG_DIR", "extract", "hydro"]
