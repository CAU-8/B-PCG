# B-PCG

B-PCG is a procedural generator for an Earth-sized planet, written in C# (.NET 10). It solves plate-driven uplift, rainfall and river incision as a steady state on a cube-sphere. It then derives the subsurface (strata, groundwater, caves) from the same rock and water. One river basin is baked into a walkable 3D corridor for Godot 4.7.2 .NET, which can also generate planets itself. The same metrics are run on real Earth data (ETOPO, RiverATLAS, OCTOPUS) to score how Earth-like the result is. This is a capstone project at Chung-Ang University (Fall 2026). Documentation is in Korean.

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

스크립트는 운영체제를 확인하고 uv, 파이썬 3.13, 패키지를 설치한 뒤 환경을 점검합니다. 생성기와 엔진은 C# 이라 **.NET 10 SDK** 가 따로 있어야 합니다. 스크립트는 SDK 를 확인만 하고, 없으면 설치 명령을 알려 줍니다(맥 `brew install --cask dotnet-sdk`, 윈도우 `winget install --id Microsoft.DotNet.SDK.10 -e`). 파이썬은 스튜디오·시험·분석 스크립트에 씁니다. 옵션은 다음과 같습니다(윈도우는 `-Godot`처럼 씁니다).

| 옵션 | 하는 일 |
|---|---|
| `--godot` | Godot 4.7.2 .NET 판을 `.tools/godot-net/`에 받습니다(엔진 담당) |
| `--data` | 파일럿 자료 약 4.5 GB를 받습니다 |
| `--analysis` / `--gpl` / `--notebook` | 비교·분석 도구를 더합니다 |
| `--check` | 설치하지 않고 점검만 합니다 |

설치한 뒤 자주 쓰는 명령:

```bash
uv run bpcg all --profile tiny    # 행성 → 히어로 → 회랑 → 지구본 (C# 콘솔로 넘김, out/earth 에 씀)
uv run bpcg studio                # 스튜디오 (매개변수 바꾸기, 지도, Godot 로 보기)
dotnet build Bpcg.slnx            # C# 빌드 (생성기, 콘솔, 엔진, 대조 시험)
dotnet test --solution Bpcg.slnx  # C# 대조 시험 (golden 자료와 비교)
uv run pytest -m "not slow"       # 시험 (C# 콘솔 결과 검사, 스튜디오)
uv run ruff format . && uv run ruff check .
uv run python tools/doctor.py     # 환경 점검
.tools/godot-net/Godot_mono.app/Contents/MacOS/Godot --path engine -e   # 엔진 편집기 (맥)
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
| `src/Bpcg/` | 생성기 본체 (C# 라이브러리, net10.0). `Core/`·`Planet/`·`Hydro/` … 단계별 묶음, `Runs.cs` 실행 단위 |
| `src/Bpcg.Cli/` | 콘솔 `bpcg planet·hero·bake·all·compare` (C#) |
| `src/bpcg_studio/` | 파이썬 스튜디오와 `bpcg` 명령(studio, 나머지는 C# 콘솔로 넘김), 저장소 경로 |
| `engine/` | Godot 4.7.2 .NET 프로젝트 (C#). 처음 화면에서 행성을 만들고 회랑·지구본을 봄. [engine/README.md](engine/README.md) |
| `tests/` | 파이썬 시험(C# 콘솔이 쓴 결과 파일과 엔진·스튜디오 검사), `tests/Bpcg.Tests/` C# 대조 시험, `tests/golden/` golden 자료 |
| `analysis/` | 연구용 스크립트(GPL 도구 허용) |
| `tools/` | 자료 받기, 환경 점검 |
| `scripts/` | 설치 스크립트 |
| `configs/` | 행성 값, 해상도 프로필, 맞춘 값 (C# 과 스튜디오가 같은 파일을 읽음) |
| `docs/` | 규칙, 개발 단계, 설계도 사본, 가이드, C# 포팅 기록 |
| `data/` | 자료(git 제외). [data/README.md](data/README.md) 참고 |

맨 위의 `Bpcg.slnx`(C# 솔루션), `Directory.Build.props`(C# 공통 설정), `global.json`(.NET SDK 판)은 C# 프로젝트 넷이 같이 씁니다.

## 문서

- [개발 규칙](docs/conventions.md)
- [함께 일하는 방법](CONTRIBUTING.md)
- [개발 단계](docs/roadmap.md)
- [설계도 사본](docs/design/blueprint.md)
- [초기 설계 가이드](docs/guide/planet_guide.md)
- [분석 스크립트](analysis/README.md)
- [엔진](engine/README.md)
- [C# 포팅 기록](docs/csharp_port.md) (Python 생성기를 C# 으로 옮긴 과정과 대조 결과)
- [스튜디오: 매개변수를 바꿔 돌리고 지도·그림·Godot 로 보기](docs/studio.md) (`uv run bpcg studio`)
- [방법 비교: 기존 지형 생성 방법론과 같은 시드로 비교하고 연산 과정 보기](docs/compare.md) (`uv run bpcg compare run`, 스튜디오 '방법 비교')

## 라이선스

코드 라이선스는 아직 정하지 않았습니다. 데이터의 출처와 의무 문구는 [data/README.md](data/README.md)에 있습니다.
