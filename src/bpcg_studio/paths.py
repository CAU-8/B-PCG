"""저장소 안의 경로. 코드에 절대 경로를 쓰지 않고 여기서만 찾습니다 (스튜디오·tools·analysis 공용).

저장소 맨 위는 Bpcg.slnx 와 configs/ 가 함께 있는 첫 부모 폴더입니다 (C# 의 Paths.cs 와 같은 기준).
환경 변수로 바꿀 수 있습니다.
- BPCG_DATA: 데이터 폴더(기본 <저장소>/data). 큰 자료를 외장 디스크에 둘 때 씁니다.
- BPCG_OUT: 생성 결과 폴더(기본 <저장소>/out).
"""

import os
from pathlib import Path


def _find_root() -> Path:
    here = Path(__file__).resolve()
    for p in here.parents:
        if (p / "Bpcg.slnx").exists() and (p / "configs").is_dir():
            return p
    return Path.cwd()


ROOT = _find_root()
DATA = Path(os.environ.get("BPCG_DATA", ROOT / "data"))
OUT = Path(os.environ.get("BPCG_OUT", ROOT / "out"))
CONFIGS = ROOT / "configs"

PILOT = DATA / "pilot"  # tools/download_pilot.py 로 다시 받을 수 있는 자료
EXTERNAL = DATA / "external"  # 계정이 필요하거나 손으로 받은 자료 (AfriSAR 등)
DERIVED = DATA / "derived"  # 분석 스크립트가 만든 중간 결과
CACHE = DATA / "cache"  # 지워도 되는 임시 파일 (zip 에서 꺼낸 파일 등)
