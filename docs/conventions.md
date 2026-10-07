# B-PCG 개발 규칙

세 사람이 같은 저장소에서 일하기 위한 규칙입니다. 처음 정한 날은 2026-10-02이고, 바꾸려면 PR로 이 파일을 고칩니다. 2026-10-03에 생성기와 엔진을 C#으로 옮기면서 폴더 구조와 C#·엔진 규칙을 고쳤습니다(옮긴 기록은 [csharp_port.md](csharp_port.md)). 일하는 순서(브랜치, PR, 리뷰)는 [CONTRIBUTING.md](../CONTRIBUTING.md)에 있습니다.

## 한눈에 보기

| 항목 | 규칙 |
|---|---|
| 이름 | 폴더·GitHub 저장소·배포 이름은 `b-pcg`. C# 네임스페이스는 `Bpcg`, 파이썬 import 이름은 `bpcg_studio`(스튜디오), 명령 이름은 `bpcg` |
| C# | .NET SDK 10(`global.json`), net10.0. 생성기 `src/Bpcg`, 콘솔 `src/Bpcg.Cli`, 엔진 `engine/`. NuGet 잠금 파일(`packages.lock.json`)을 커밋합니다 |
| 파이썬 | 스튜디오·검사·분석·도구용. 3.13 고정(`.python-version`). uv로만 설치하고 `uv.lock`을 커밋합니다 |
| 언어 | 변수·함수·파일 이름은 영어. 주석·docstring·문서·커밋·PR은 한국어(합니다체). README 첫 문단만 영어 요약 |
| 스타일 | C#은 `dotnet format`(`.editorconfig`, 경고는 오류). 파이썬은 `ruff format` + `ruff check`(한 줄 100자). CI가 통과해야 합칩니다 |
| 단위 | SI에 시간만 연(yr): m, yr, m/yr, m³/yr, kg/m³. 각도는 안에서 라디안, 입출력에서만 도 |
| 격자 | 가이드 1장 큐브스피어 규칙. `n`은 면 한 변의 칸 수, `n_cells = 6·n²`, 셀 번호 `c = f·n² + j·n + i` |
| 경로 | 코드에 절대 경로를 쓰지 않습니다. C#은 `Bpcg.Core.Paths`, 파이썬은 `bpcg_studio.paths`로만 찾습니다 |
| 데이터 | `data/`는 올리지 않습니다(README와 manifest만). 커밋하는 바이너리는 1 MB 이하 |
| 라이선스 격리 | GPL 도구(fastscapelib, TopoToolbox, pysheds, richdem)는 `analysis/`에서만 import합니다. ruff가 막습니다 |
| 재현성 | 생성기의 난수와 노이즈는 시드를 받은 정수 해시(`Bpcg.Core.Hashing`)로만. `System.Random`은 쓰지 않음 |
| git | main 직접 커밋 금지. 기능 브랜치 → PR → 리뷰 1명 → squash 합치기 |
| 커밋 메시지 | `종류(범위): 한국어 요약`. 예: `feat(hydro): 우선순위 홍수 채우기 추가` |

## 1. 폴더 구조

```text
b-pcg/
├── Bpcg.slnx            C# 솔루션 (src/Bpcg, src/Bpcg.Cli, engine, tests/Bpcg.Tests)
├── Directory.Build.props, global.json, .editorconfig   C# 공통 설정
├── src/
│   ├── Bpcg/            생성기 본체 (C# 라이브러리, Godot에 의존하지 않음)
│   │   ├── Core/        격자·이웃·거리·행성 상수·노이즈·설정·경로 (가이드 1장, 공통 뼈대)
│   │   ├── IO/          npy·npz·JSON·TOML 읽기·쓰기
│   │   ├── Numerics/    scipy 대신 직접 짠 수치 함수 (KD-tree, ndimage 등)
│   │   ├── Planet/      사슬 1 뼈대: 판·지각·해양저·해수면·융기, 가벼운 기후
│   │   ├── Geology/     사슬 2 암석: 3D 지질 템플릿, 변성 정도 표
│   │   ├── Hydro/       물길: 웅덩이 채우기, D8 + 노드 흔들기, 유량 누적 (가이드 2장)
│   │   ├── Landscape/   정상상태 솔버, G-법칙 퇴적, 선상지 후처리 (가이드 4장)
│   │   ├── Subsurface/  사슬 3 내부: 흙, 지하수면, 2층 동굴
│   │   ├── Hero/        히어로 유역 격자
│   │   ├── Volume/      3D 샘플 함수 (가이드 7장)
│   │   ├── Bake/        묶음·회랑 메시·높이맵·지구본을 엔진용 파일로 쓰기
│   │   ├── Metrics/     점수표 지표
│   │   ├── Compare/     기존 지형 생성 방법론과 같은 시드로 비교 (docs/compare.md)
│   │   └── Pipeline.cs, Runs.cs   생성 순서, 실행 단위 (콘솔과 엔진이 같이 씀)
│   ├── Bpcg.Cli/        콘솔 bpcg planet·hero·bake·all·compare
│   └── bpcg_studio/     웹 스튜디오 (파이썬). 계산은 C# 콘솔을 불러 시킴
├── engine/              Godot 4.7.2 .NET 프로젝트. 처음 화면에서 생성하고, 구운 파일을 보여 줌
├── tests/               pytest: C# 콘솔·엔진 결과 검사. Bpcg.Tests/: C# 단위·golden 대조 시험
├── analysis/            한 번 돌리는 연구 스크립트(파이썬). GPL 도구 허용
├── tools/               설치 전에도 도는 스크립트 (자료 받기, 환경 점검)
├── scripts/             설치 스크립트 (setup.sh, setup.ps1)
├── configs/             planets/(행성 값), profiles/(해상도), learned/(맞춘 값), compare/(방법 비교 설정)
├── docs/                규칙, 개발 단계, 설계도 사본, 가이드, 그림, 라이선스 원문
├── data/                (git 제외) pilot/ external/ derived/ cache/ logs/
├── out/                 (git 제외) 생성 결과
└── .tools/              (git 제외) Godot .NET 실행 파일 등
```

**의존 방향.** 아래쪽이 위쪽을 쓸 수 있고 거꾸로는 안 됩니다. `Metrics`는 무엇이든 쓸 수 있지만, 생성 단계 안에서 `Metrics`를 부르는 곳은 점수표를 만드는 `Pipeline`뿐입니다.

```text
Core, IO, Numerics → Planet, Geology, Hydro → Landscape → Subsurface → Hero, Volume → Bake → Runs, Compare
```

`Compare`는 비교 도구라 생성 단계 어디서도 부르지 않습니다. 평면 히어로(`Hero`)와 물길(`Hydro`), 색표(`Bake`)를 씁니다.

`src/Bpcg.Cli`와 `engine/`은 `src/Bpcg`를 참조하고, `src/Bpcg`는 둘을 모릅니다. 파이썬(`bpcg_studio`, `analysis/`, `tools/`, `tests/`)은 C#을 콘솔 명령으로만 부르고 C# 결과 파일을 읽습니다. `src/bpcg_studio`는 `analysis/`와 `tools/`를 import하지 않습니다.

**무엇을 어디에 두나.**

- 계산 함수(과학)는 파일을 읽거나 쓰지 않습니다. 읽기는 `IO`·`Bake/Bundle`, 쓰기는 `Bake`에서만 합니다.
- 계산은 `src/Bpcg`에 둡니다. 엔진(`engine/Scripts`)은 Godot 자료형으로 바꾸어 보여 주는 코드만 둡니다.
- 파일 하나가 500줄을 넘으면 나누는 것을 검토합니다.
- 실험 코드는 `analysis/`에서 시작하고, 시험을 붙여 `src/Bpcg`로 옮깁니다.

## 2. 이름 짓기

| 대상 | 규칙 | 예 |
|---|---|---|
| C# 형·메서드·속성 | PascalCase, 메서드는 동사로 시작 | `Depressions.FillDepressions`, `CellGraph` |
| C# private 필드 | `_camelCase` | `_baseStem` |
| C# 지역 변수·인자 | 규칙을 강제하지 않음. 수식 기호와 옮기기 전 이름을 그대로 써도 됨(`.editorconfig`) | `z`, `Q`, `k_s`, `nCells` |
| C# 상수·static readonly | PascalCase | `FaceU`, `EarthRadiusM` |
| 파이썬 모듈·함수·변수 | snake_case, 함수는 동사로 시작 | `hero_grid_size`, `load_bundle` |
| 필드·설정 키·파일 이름 | snake_case (묶음·TOML·JSON 형식 그대로) | `z_m`, `is_ocean`, `rivers.min_discharge_m3_per_s` |
| 참·거짓 | `Is`·`Has`(C#), `is_`·`has_`(파이썬, 필드)로 시작 | `IsOcean`, `has_cave` |
| 테스트 | C# `tests/Bpcg.Tests/<묶음>/<모듈>Tests.cs`, pytest `tests/test_<묶음>.py`, `test_<무엇이_어떻다>` | `test_area_sum_equals_sphere` |
| 브랜치 | `종류/짧은-영어-설명` | `feat/priority-flood`, `fix/seam-areas` |

**수식 기호.** 설계도 2장 표의 기호를 ASCII로 그대로 씁니다(C# 지역 변수도 같음). 아래 기본 단위와 다를 때만 이름 끝에 단위를 붙입니다(`_km`, `_mm_per_yr`, `_myr`).

| 기호 | 코드 이름 | 뜻 | 기본 단위 |
|---|---|---|---|
| z | `z` | 고도(해수면 기준) | m |
| Q | `Q` | 유량 | m³/yr |
| A | `A` | 셀 면적 또는 상류 면적 | m² |
| S | `S` | 경사(높이/거리) | m/m |
| k_s | `k_s` | 강의 가파름 | Q의 단위에 맞춤 |
| θ | `theta` | 하류로 갈수록 완만해지는 정도 | 없음 |
| S_crit | `s_crit` | 산비탈이 버티는 가장 가파른 경사 | m/m |
| U | `U` | 융기 속도 | m/yr |
| P | `P` | 강수 | m/yr |
| d | `d` | 이웃 사이 거리 | m |

한 글자 이름은 수식 기호와 격자 첨자(`f`, `i`, `j`, `c`, `n`)에만 씁니다.

## 3. 단위와 정밀도

- 안쪽 계산은 m, yr, m/yr, m³/yr, kg/m³, 라디안입니다. mm/yr, 도, km는 입력을 읽을 때와 그림·보고서에서만 바꿉니다.
- 함수가 받는 배열의 단위는 설명 주석(C# `///`, 파이썬 docstring)에 `[m]`처럼 적습니다.
- 솔버 고도와 웅덩이 채우기 높이는 float64입니다. 채우기에 쓰는 기울기 ε = 1e-3 m는 float32에서 사라지기 때문입니다.
- 엔진으로 넘기는 파일은 float32로 저장합니다(높이맵, 메시).
- 굽기 결과와 엔진의 값 비교는 float32 정밀도에 맞춥니다. 0.1 mm 일치 시험은 하지 않습니다.

## 4. 격자와 좌표

- **면 기저.** `Bpcg.Core.Cubesphere`의 `FaceU`, `FaceV`, `FaceN`이 기준입니다(가이드 1장 표, 오른손 좌표계 u×v = n). 묶음 manifest에도 이 표를 그대로 씁니다.
- **셀 번호.** `c = f·n² + j·n + i`입니다. i는 u 방향, j는 v 방향이고, 셀 중심은 `a_i = -1 + (2i+1)/n`입니다.
- **배열 모양.** 셀마다 하나인 값은 `(n_cells,)` 평평한 배열로 저장합니다. 면별로 볼 때만 `reshape(6, n, n)`으로 `[f, j, i]`를 씁니다.
- **경위도.** 입출력 경계(설정의 히어로 위치, 지구본·스튜디오 표시)에서만 씁니다(도 단위). 안쪽은 단위 벡터입니다.
- **엔진 좌표.** 굽기(`Bpcg.Bake`)가 회랑 기준점에서 지역 좌표로 바꿔 굽습니다. Godot 좌표는 X = 동, Y = 위, Z = 남입니다. 기준점은 manifest에 적습니다. 자세한 형식은 [engine/README.md](../engine/README.md)에 있습니다.

## 5. 코드 스타일

- **C# 포맷과 검사.** 저장소 맨 위 `.editorconfig`와 `Directory.Build.props`가 기준입니다. 경고는 오류이고 빌드 중에 코드 스타일을 검사합니다(`int`·`double` 같은 기본형과 배열에는 `var`를 쓰지 않음, 중괄호 늘 씀, nullable 켬, private 필드는 `_camelCase`). PR 전에 `dotnet format Bpcg.slnx --verify-no-changes`를 통과시킵니다.
- **파이썬 포맷과 린트.** 저장할 때 `ruff format`을 돌리고, `ruff check`를 통과시킵니다(설정은 `pyproject.toml`).
- **설명 주석.** 다른 곳에서 부르는 형·메서드에는 `/// <summary>`를 붙입니다(파이썬은 타입 힌트와 docstring). 첫 줄에 한 문장 요약을 쓰고, 단위와 배열 모양을 적습니다.

    ```csharp
    /// <summary>웅덩이를 채워 모든 셀이 바다로 흐르게 합니다.
    /// z: (n_cells,) 고도 [m]. nbr: (n_cells, 8) 이웃 셀 번호, 없으면 -1. 반환: 채운 고도 [m], 늘 z 이상.</summary>
    public static double[] FillDepressions(double[] z, int[] nbr)
    ```

- **주석.** '무엇'보다 '왜'를 씁니다. 근거가 논문이면 `// Yuan 2019 식 (7)`처럼 출처를 남깁니다.
- **병렬.**
    - `Parallel.For`는 칸마다 독립이고 경쟁 조건이 없을 때만 씁니다(예전 numba `prange` 자리).
    - 합처럼 순서에 따라 값이 달라지는 계산은 병렬로 나누지 않거나, 나눈 순서를 고정해 결과가 늘 같게 합니다.
    - 같은 높이일 때의 순서는 셀 번호로 정해 결과가 늘 같게 합니다.
- **예외.** 입력이 잘못되면 한국어 메시지로 예외를 냅니다(C# `ArgumentException`·`InvalidDataException`, 실행 단위는 `RunError`, 파이썬 `ValueError`). `Debug.Assert`·`assert`는 시험과 내부 불변식에만 씁니다.

## 6. 재현성과 설정

- **설정 파일.** 값은 `configs/`의 TOML에서 읽습니다. 행성 값은 `planets/`, 해상도는 `profiles/`, 맞춘 값은 `learned/`에 둡니다. 코드 안에 숫자를 흩어 두지 않습니다.
- **난수와 노이즈.** 지형 모양을 바꾸는 노이즈와 노드 흔들기는 시드를 받은 정수 해시(`Bpcg.Core.Hashing`의 SplitMix64)로 만듭니다. `System.Random`은 .NET 판마다 수열이 달라질 수 있어서 쓰지 않습니다. 맥과 연구실 PC가 같은 행성을 만들려면 해시가 필요합니다.
- **기록.** 생성 결과의 manifest에는 git 커밋, 설정 파일 내용, 시드, bpcg 버전을 적습니다.

## 7. 테스트

- **도구와 위치.** 두 겹입니다.
    - C# 단위 시험: `tests/Bpcg.Tests/`(xunit v3). 함수 하나를 golden 자료(`tests/golden/data/`, 큰 사례는 `out/golden/`)와 비트 단위로 맞대어 봅니다. `dotnet test --solution Bpcg.slnx`로 돕니다. 큰 사례가 없으면 그 시험만 건너뜁니다.
    - 결과 검사: `tests/test_<묶음>.py`(pytest). C# 콘솔(`bpcg all` 등)이 쓴 묶음·굽기 파일을 읽어 불변식(물은 아래로, 넓이 합, 형식 약속)을 봅니다. 엔진은 `tests/test_engine.py`가 Godot을 띄워 봅니다.
- **표시(marker).**

    | 표시 | 뜻 |
    |---|---|
    | `slow` | 오래 걸림 |
    | `data` | `data/pilot`이 필요함, 없으면 건너뜀 |
    | `godot` | Godot .NET 판과 dotnet이 필요함, 없으면 건너뜀 |

    CI는 `-m "not slow"`로 돕니다. C# 시험은 CI의 csharp 작업(macOS)이 돌립니다. golden 비트 일치는 macOS arm64에서 확인했습니다.
- **허용 오차.** 오차 기준에는 근거를 주석으로 남깁니다. 예: `# 가이드 1장: 면적 합 상대 오차 < 1e-12`.
- **시험 자료.** `tests/fixtures/`에 1 MB 이하로 두고, 출처를 `tests/fixtures/README.md`에 적습니다.
- **GPL 도구와 비교.** `analysis/`에서 미리 계산해 결과만 `tests/fixtures/`에 저장합니다. 테스트는 GPL 도구를 import하지 않고 저장된 결과와 비교만 합니다.
- **완료의 뜻.** 과학 모듈은 테스트가 있고, 데모 빌드에 들어가야 '완료'입니다(설계도 8장).

## 8. 데이터와 파일

- **폴더 역할.**

    | 폴더 | 내용 |
    |---|---|
    | `data/pilot/` | 스크립트로 다시 받을 수 있는 자료 |
    | `data/external/` | 계정이 필요하거나 손으로 받은 자료(AfriSAR) |
    | `data/derived/` | 분석 중간 결과 |
    | `data/cache/` | 지워도 되는 파일 |

    목록과 크기, 해시는 `data/manifest.json`에 있습니다. 자세한 설명은 [data/README.md](../data/README.md)에 있습니다.
- **커밋하는 것.** 원자료, zip, GeoTIFF, GeoJSON, 큰 npz는 커밋하지 않습니다. 커밋하는 바이너리는 시험 자료와 문서 근거용 작은 결과(각 1 MB 이하)뿐입니다. 그보다 큰 파일은 먼저 팀에 묻습니다(Git LFS는 아직 쓰지 않음).
- **다른 디스크에 둘 때.** 큰 자료는 `BPCG_DATA=/다른/디스크/data`로 옮겨 쓸 수 있습니다.
- **결과 파일.** 콘솔·스튜디오의 생성 결과는 `out/`에, `--engine`으로 엔진에 넘기는 결과는 `engine/baked/`에, 엔진 처음 화면의 결과는 Godot `user://runs/`에 씁니다. 모두 git에서 빠집니다.

## 9. 라이선스

- **코드 라이선스.** 아직 정하지 않았습니다(개발 단계 0단계의 남은 일). 정하면 `LICENSE`에 적고, 데이터 라이선스와 분리합니다.
- **GPL 도구.** `analysis/`에서만 씁니다. 핵심 코드(`src/Bpcg`)는 직접 짠 C# 코드로 만들고, NuGet 패키지는 MIT·BSD·Apache-2.0만 씁니다(지금은 하나도 쓰지 않음).
- **연구 코드.** Tzathas 2024 코드는 비상업 연구용이라 가져다 쓰지 않습니다. 논문을 보고 다시 구현합니다.
- **데이터.** 출처와 의무 문구는 `data/README.md`와 `docs/licenses/`에 있습니다. OCTOPUS로 맞춘 표에는 `CC BY-NC-SA, OCTOPUS 유래`를 적습니다.

## 10. git과 협업 요약

- **브랜치.**
    - `main`은 보호합니다. 직접 커밋하지 않고, PR + 리뷰 1명 승인 + CI 통과 후 squash로 합칩니다.
    - 브랜치 종류는 `feat/`, `fix/`, `docs/`, `test/`, `refactor/`, `perf/`, `chore/`, `data/`, `exp/`입니다.
- **커밋 메시지.**
    - 형식은 `종류(범위): 한국어 요약`입니다. 범위는 묶음 이름입니다(`core`, `hydro`, `bake`, `cli`, `engine`, `studio` 등).
    - 본문에는 왜 바꿨는지를 씁니다. 이슈가 있으면 `#12`처럼 적습니다.
- **PR 크기와 리뷰.** PR은 작게 유지합니다(바뀐 줄 400줄 안팎). 리뷰는 하루 안에 합니다.
- **금요일 통합일.** 그 주의 PR을 합치고, 데모 빌드가 도는지 확인합니다.
- **태그.**

    | 태그 | 날짜 | 시점 |
    |---|---|---|
    | `v0.1-pitch` | 10/23 | 중간 피치 |
    | `v0.2-freeze` | 11/11 | 꼭 할 일 기능 동결 |
    | `v0.3-demo-freeze` | 11/18 | 데모 동결 |
    | `v1.0` | 12/4 | 최종 데모 |

## 11. 환경과 의존성

- **설치.**
    - 설치는 `scripts/setup.sh`(맥·리눅스) 또는 `scripts/setup.ps1`(윈도우)로 합니다. .NET 10 SDK는 따로 설치하고(스크립트가 없으면 알려 줌), `tools/doctor.py`로 점검합니다.
    - 파이썬은 uv만 씁니다.
    - `pip install`은 쓰지 않습니다. `uv.lock`과 실제 설치가 어긋납니다.
- **의존성 묶음.**

    | 묶음 | 내용 | 설치 |
    |---|---|---|
    | 기본 | numpy, scipy, numba | 늘 |
    | `dev` | pytest, ruff, hypothesis, matplotlib | 늘(`uv sync`) |
    | `data` | h5py, xarray, rasterio, pyogrio, geopandas 등 | 늘(`uv sync`) |
    | `mesh` | scikit-image, trimesh | 늘(`uv sync`) |
    | `analysis` | pyflwdir, landlab | `--group analysis` |
    | `gpl` | fastscapelib, topotoolbox | `--group gpl`, analysis 전용 |
    | `notebook` | jupyterlab | `--group notebook` |

- **패키지 추가.** 파이썬은 `uv add <패키지> --group <묶음>`, C#은 `dotnet add <프로젝트> package <패키지>`로 더하고, PR 설명에 이유를 씁니다. `uv.lock`·`packages.lock.json`도 같이 커밋합니다.
- **지원 환경.**
    - macOS 15 이상(Apple Silicon): 잠근 rasterio 바이너리가 macOS 15용입니다.
    - Windows 10/11 x64.
    - Linux x86_64(glibc 2.28 이상).
    - Linux ARM에서는 `gpl` 묶음을 설치할 수 없습니다.
    - Windows ARM64는 x64 파이썬을 에뮬레이션으로 씁니다(시험 안 함).
    - Intel Mac은 지원하지 않습니다(numba 0.68에 macOS x86_64 바이너리가 없음).
- **파이썬 3.13인 이유.** fastscapelib과 TopoToolbox가 3.14용 바이너리를 내지 않아서, 세 운영체제 모두에서 바이너리로 설치되는 가장 새 버전이 3.13입니다(2026-10-02 확인). 올릴 때는 `tools/check_wheels.py`로 먼저 확인합니다.
- **Godot.** 4.7.2 **.NET 판**(`4.7.2.stable.mono`)을 모두 같은 버전으로 씁니다. 표준판으로는 C# 엔진이 열리지 않고, 버전이 섞이면 `project.godot`와 `.import` 파일이 계속 바뀝니다. `scripts/setup.sh --godot`가 SHA-512를 확인하고 `.tools/godot-net/`에 받습니다. 다른 곳에 둔 것은 `GODOT_NET` 환경 변수로 알려 줍니다.

## 12. 엔진(C#)

- **스타일.** 5절의 C# 규칙을 따릅니다. Godot 노드 클래스는 `partial`이고, 파일 이름과 클래스 이름이 같아야 합니다. 신호는 `[Signal]` 대리자, 편집기에 보이는 값은 `[Export]` 속성으로 씁니다.
- **엔진의 역할.** 처음 화면에서 `src/Bpcg`의 `Runs`를 불러 행성을 만들고 굽습니다(2026-10-03, 이전 규칙 '엔진은 계산하지 않음'을 바꿈). 계산 코드는 여전히 `src/Bpcg`에만 두고, `engine/Scripts`는 구운 파일을 Godot 자료형으로 바꾸어 보여 줍니다. 엔진 안 생성 결과는 같은 설정의 콘솔 결과와 바이트까지 같아야 합니다.
- **커밋하는 것.** `*.cs`, `*.tscn`, `*.gdshader`, `*.import`, `*.uid`, `Bpcg.Engine.csproj`·`.sln`·`packages.lock.json`은 커밋하고, `.godot/`(C# 빌드 출력 포함)와 `baked/`는 커밋하지 않습니다.
- **스크립트로 실행할 때.** `--headless`로 돌리고, HOME을 임시 폴더로 바꿉니다. 그러지 않으면 내 컴퓨터의 Godot 편집기 설정과 `user://`가 덮어써집니다. 실행 전에 `dotnet build engine/Bpcg.Engine.csproj`로 C#을 빌드합니다.

## 13. 문서

- **문서 위치.** 문서는 `docs/`에 한국어로 씁니다. 설계도의 원본은 Claude 문서이고, `docs/design/`은 그 사본입니다. 두 곳이 다르면 원본이 맞습니다.
- **설계 문서 작성 원칙.**
    1. 확정본을 앞에 두고 기록은 부록으로 보냅니다.
    2. 용어는 화면에 보이는 현상으로 먼저 정의합니다.
    3. 내용을 거시 → 중간 → 미시 스케일로 나눕니다.
    4. 기능은 인과 사슬 3개로 설명합니다.
    5. 차별점은 '빈틈 → 해결 → 검증 기준' 표로 씁니다.
- **문서도 함께 고칩니다.** 코드가 바뀌어 문서가 틀리게 되면 같은 PR에서 문서도 고칩니다.
