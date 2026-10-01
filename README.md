# B-PCG

B-PCG is a procedural generator for an Earth-sized planet. It solves plate-driven uplift, rainfall and river incision as a steady state on a cube-sphere. It then derives the subsurface (strata, groundwater, caves) from the same rock and water. One river basin is baked into a walkable 3D corridor for Godot. The same metrics are run on real Earth data (ETOPO, RiverATLAS, OCTOPUS) to score how Earth-like the result is. This is a capstone project at Chung-Ang University (Fall 2026). Documentation is in Korean.

판·비·강이 산을 만들고, 그 산을 만든 바로 그 암석과 물이 땅속이 되는 행성 생성기입니다. 같은 코드에 진짜 지구를 넣어 얼마나 지구다운지 점수를 매깁니다.

## 빠른 시작

맥·리눅스:

```bash
./scripts/setup.sh
```

윈도우(PowerShell):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
```

스크립트는 운영체제를 확인하고 uv, 파이썬 3.13, 패키지를 설치한 뒤 환경을 점검합니다. 옵션은 다음과 같습니다(윈도우는 `-Godot`처럼 씁니다).

| 옵션 | 하는 일 |
|---|---|
| `--godot` | Godot 4.7.2를 `.tools/godot/`에 받습니다(엔진 담당) |
| `--data` | 파일럿 자료 약 4.5 GB를 받습니다 |
| `--analysis` / `--gpl` / `--notebook` | 비교·분석 도구를 더합니다 |
| `--check` | 설치하지 않고 점검만 합니다 |

설치한 뒤 자주 쓰는 명령:

```bash
uv run pytest -m "not slow"       # 테스트
uv run ruff format . && uv run ruff check .
uv run python tools/doctor.py     # 환경 점검
```

## 지원 환경

| 운영체제 | 지원 | 비고 |
|---|---|---|
| macOS 15 이상, Apple Silicon | 됨 | 개발 맥에서 시험함 |
| Windows 10/11 x64 | 됨 | CI에 넣음(GitHub에 올린 뒤 첫 실행), 팀원 컴퓨터에서 첫 시험 필요 |
| Linux x86_64(glibc 2.28 이상) | 됨 | CI에 넣음(GitHub에 올린 뒤 첫 실행) |
| Windows ARM64 | x64 파이썬을 에뮬레이션으로 씀 | 시험하지 않음 |
| Linux ARM64 | 기본 묶음만 | `gpl` 묶음(fastscapelib, TopoToolbox)은 설치 불가 |
| Intel Mac, macOS 14 이하 | 안 됨 | numba·rasterio 바이너리가 없음 |

파이썬은 3.13으로 고정합니다. 이유는 [docs/conventions.md](docs/conventions.md) 11절에 있습니다.

## 폴더

| 폴더 | 내용 |
|---|---|
| `src/bpcg/` | 생성기 본체 |
| `tests/` | 테스트 |
| `analysis/` | 연구용 스크립트(GPL 도구 허용) |
| `tools/` | 자료 받기, 환경 점검 |
| `scripts/` | 설치 스크립트 |
| `configs/` | 행성 값, 해상도 프로필, 맞춘 값 |
| `engine/` | Godot 4.7.2 프로젝트 |
| `docs/` | 규칙, 개발 단계, 설계도 사본, 가이드 |
| `data/` | 자료(git 제외). [data/README.md](data/README.md) 참고 |

## 문서

- [개발 규칙](docs/conventions.md)
- [함께 일하는 방법](CONTRIBUTING.md)
- [개발 단계](docs/roadmap.md)
- [설계도 사본](docs/design/blueprint.md)
- [초기 설계 가이드](docs/guide/planet_guide.md)
- [분석 스크립트](analysis/README.md)
- [엔진](engine/README.md)

## 라이선스

코드 라이선스는 아직 정하지 않았습니다. 데이터의 출처와 의무 문구는 [data/README.md](data/README.md)에 있습니다.
