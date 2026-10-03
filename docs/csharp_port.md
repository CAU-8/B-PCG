# C# 포팅 진행 기록

지시 원문은 [docs/csharp_port_prompt.md](csharp_port_prompt.md)에 있습니다. 이 파일은 `src/bpcg`를 C#(`csharp/`)으로 옮기는 작업의 현재 상태를 담고, 모듈 하나를 마칠 때마다 고칩니다. 처음 읽는 사람은 0장부터 읽습니다. 1장부터는 단계 0에서 조사한 근거 자료이고, 6장의 모듈별 절은 그 모듈을 옮길 때 다시 읽습니다.

**폴더를 옮겼습니다(2026-10-03, 단계 5).** 아래 1장부터의 기록은 옮기기 전 경로로 적혀 있습니다. 읽을 때 `csharp/Bpcg` → `src/Bpcg`, `csharp/Bpcg.Cli` → `src/Bpcg.Cli`, `csharp/Bpcg.Tests` → `tests/Bpcg.Tests`, `csharp/Bpcg.Engine` → `engine/`, `csharp/golden/data` → `tests/golden/data`, `csharp/Bpcg.slnx` → `Bpcg.slnx`, `src/bpcg/studio` → `src/bpcg_studio` 로 바꿔 읽습니다. `src/bpcg/*.py`(Python 생성기)와 `engine/scripts/*.gd`(GDScript)는 지웠으므로 git 기록(커밋 3b5bbf6 까지)에서 봅니다. C# 코드 주석의 `(src/bpcg/….py)` 도 그 옮기기 전 원본을 가리킵니다.

## 0. 지금 상태와 다음 할 일

### 단계 5: 폴더 구조 바꾸기 (2026-10-03)

- 사용자가 정한 방향: 포팅이 끝났다고 보고, `csharp/` 밖의 Python 가운데 같은 일을 하는 C# 이 있는 것은 지우고, `csharp/` 안의 폴더를 저장소 맨 위로 옮깁니다. 이름은 원래 자리에 맞춥니다.
- 지금 구성: `Bpcg.slnx`·`Directory.Build.props`(맨 위), `src/Bpcg`(생성기), `src/Bpcg.Cli`(콘솔), `engine/`(Godot .NET 프로젝트, 예전 GDScript 판을 대체), `tests/Bpcg.Tests`(C# 단위·golden 시험), `tests/golden/data`(커밋한 golden).
- 지운 것: `src/bpcg/` 의 생성기 묶음 전부(core·planet·geology·hydro·landscape·subsurface·metrics·hero·volume·bake·pipeline·cli), `engine/scripts/*.gd`·`engine/tests/*.gd`, `csharp/golden/export_golden.py`(golden 은 다시 만들 수 없음. 큰 사례 `out/golden` 이 없으면 그 시험만 건너뜀), Python 생성기를 직접 부르던 pytest.
- 남긴 Python 과 바꾼 점:
  - 스튜디오 → `src/bpcg_studio/`. `bpcg` 명령의 입구(`bpcg_studio.cli:main`)이고, `studio` 밖의 명령(`planet`·`hero`·`bake`·`all`)은 C# 콘솔로 넘깁니다. 실행은 C# 콘솔을 빌드해 부르고, 'Godot로 보기' 는 Godot .NET 판을 찾아 엔진 C# 을 빌드한 뒤 띄웁니다. 스튜디오가 쓰던 설정 읽기·필드 표·히어로 격자 크기·묶음 읽기는 작은 사본으로 `bpcg_studio/` 안에 두었습니다.
  - pytest → C# 콘솔 결과를 검사하게 다시 씀(`tests/bundles.py`, `conftest.py` 의 tiny·평면·동굴 실행 고정물, 묶음별 `test_*.py`). 엔진 검사는 `tests/test_engine.py`.
  - `analysis/` 6개 파일은 경로만 `bpcg_studio.paths` 로 고쳤고, `analysis/figures/render_results.py`·`analysis/demos/plot_cubesphere.py` 는 Python 생성기를 import 해서 지금은 돌지 않습니다(머리에 적어 둠). 스튜디오의 그림 단계는 그래서 건너뜁니다.
  - `tools/doctor.py`·`scripts/setup.sh`·`setup.ps1`·CI 는 .NET 10 SDK 와 Godot 4.7.2 .NET 판(`.tools/godot-net/`)을 보게 고쳤습니다. CI 에 `csharp` 작업(macOS, build·test·format)을 더했습니다.
- 문서: `README.md`, `CONTRIBUTING.md`, `docs/conventions.md`(폴더 구조·C# 규칙·엔진 규칙, 0장 쟁점 9 의 '엔진은 계산하지 않음' 을 바꿈), `docs/studio.md`, `docs/pipeline.md`(이름 읽는 법), `engine/README.md`, `engine/GLOBE.md`.
- 남은 일: 그림 스크립트를 C# 결과에 맞추기, C# 콘솔에 지구본 `--face-res` 선택 더하기, 내보낸 게임의 configs(`engine/Scripts/EnginePaths.cs` 의 TODO), golden 대조를 macOS 밖에서 돌려 보기, 윈도우에서 스튜디오 시험의 글자 인코딩 실패(옮기기 전부터 있던 것).

### 단계 4: Godot .NET 전환 (2026-10-03, 끝남, 단계 5 에서 정리)

- 사용자가 정한 방향: 최종 목표는 Python 없이 C# 으로 진행하는 것이고, `csharp/` 안에서 돌아가면 전체를 대체합니다. GDScript 도 모두 C# 으로 옮기고(0장 쟁점 1 은 '(2) Godot 안에서 계산'), 생성은 Godot 안 처음 화면에서 시작합니다.
- 만든 것: `csharp/Bpcg.Engine/`(Godot 4.7.2 .NET, net10.0, `Bpcg.slnx` 에 포함). `engine/scripts/*.gd` 13개와 `engine/tests/*.gd` 3개를 C# 으로 옮겼고, 처음 화면(`StartMenu`)·진행률(`StageProgress`)·설정 경로(`EnginePaths`)를 새로 만들었습니다. 쓰는 법과 대응표는 [csharp/Bpcg.Engine/README.md](../csharp/Bpcg.Engine/README.md).
- 라이브러리: `cli.py` 의 실행 단위를 `csharp/Bpcg/Runs.cs`(+ `RunError`)로 옮겨 콘솔과 엔진이 같이 씁니다. 콘솔 출력은 그대로입니다(tiny 결과 바이트 일치, 걸린 시간만 다름).
- Godot .NET 판은 `.tools/godot-net/Godot_mono.app` 에 손으로 받았습니다(SHA-512 일치, 1장 절차의 (나)). net10.0 게임 어셈블리가 편집기 빌드(`--build-solutions`)와 실행 모두에서 돕니다.
- 확인: 엔진 안 tiny 생성 = 콘솔 결과, C# 연기 검사(회랑·지구본)가 C#·Python 굽기 모두에서 통과, 화면 13장이 GDScript 판과 픽셀 일치.
- 남은 일이던 설치 스크립트·doctor·CI·pytest 의 Godot 검사·스튜디오 'Godot로 보기' 의 .NET 전환과 `engine/` 대체는 단계 5 에서 했습니다. 내보낸 게임의 configs 는 남았습니다.

### 지금 상태 (2026-10-03)

- **단계 2(묶음별 포팅)를 진행 중입니다.** 사용자가 core 검토를 따로 하지 않고 순서대로 계속하라고 했습니다(10장 D13). 끝낸 묶음: core, hydro.
- **대조 결과: C# 시험 119개가 모두 통과했고, 실수 비교는 모두 비트 단위 일치입니다**(macOS 26.6 arm64, .NET SDK 10.0.401·런타임 10.0.12, numpy 2.5.3, numba 0.68.0). 다른 OS 에서는 아직 돌려 보지 않았습니다.
- hydro(2026-10-03): `Depressions`, `Routing`, `AccumulateModule`(파일 `Accumulate.cs`, 0장 쟁점 6 규칙), `Network`. golden 은 구면 n=32·7, 평면 64², 정수 고도 평면(경사 동률·넓은 평지), ±0 부호 손 사례입니다. 채움·D8(히스테리시스 포함)·순서·누적·유역·강 구간이 모두 비트 단위로 같습니다.
- 만든 것:
  - 저장소 설정: `.gitignore`(C# 빌드 출력, golden 예외), `.gitattributes`, `.editorconfig`(`[*.cs]`), `global.json`(SDK 10.0, 시험 실행기 Microsoft.Testing.Platform, 10장 D11).
  - 프로젝트: `csharp/Directory.Build.props`, `Bpcg`·`Bpcg.Cli`(뼈대만)·`Bpcg.Tests`, `Bpcg.slnx`, 잠금 파일 `packages.lock.json`, `csharp/README.md`.
  - `Bpcg.IO`: `Npy`·`Npz`(numpy 와 바이트까지 같음)·`Crc32`, `PyJson`(Python `json.dumps` 와 같은 글자), `Toml`(Python `tomllib` 이식, 10장 D9).
  - `Bpcg.Numerics`: `NpReduce`(numpy pairwise 합, 10장 D10), `LinAlg`(3성분), `NpGrid.Linspace`.
  - core: `Package`, `Constants`, `Fields`(Python 표에서 생성), `Paths`, `Config`(+`DiffLib`), `Hashing`, `Noise`, `Cubesphere`, `Graph`, `Distance`, `Resample`.
  - golden: `csharp/golden/export_golden.py` 가 core·IO 사례 97개(10.3 MB)를 `out/golden/` 에, 작은 사례(파일마다 1 MB 이하, 합계 약 4 MB)를 `csharp/golden/data/` 에 씁니다. 커밋한 사례만 있을 때는 큰 사례를 쓰는 시험 18개가 '사례 없음'으로 실패합니다(건너뛰지 않는 규칙, 7장).
  - 시험: `csharp/Bpcg.Tests/` 의 `Golden`·`Compare` 와 모듈별 시험 클래스(`IO/IoTests`, `Core/<모듈>Tests`).
- **4~6장은 대부분 독립 비평을 거치지 않은 초안입니다**(10장 D4). 5장(numpy·numba 공통 규칙)과 4장 끝(FFT·polyfit)은 비어 있거나 일부만 채웠습니다(10장 D7).

### 다음 할 일

1. 단계 2 를 순서대로 잇습니다: planet → geology → landscape → subsurface → metrics → pipeline·hero → volume → bake → cli. 묶음마다 golden 사례 추가(6장 해당 절 '(f)') → `csharp/Bpcg/<Pkg>/*.cs` → 대조 시험 → 3장·이 장 갱신 → 커밋.
2. 다음 묶음은 planet 입니다(6장 planet ①·② 절).
3. 확인 명령: `uv run python csharp/golden/export_golden.py` → `dotnet build csharp/Bpcg.slnx` → `dotnet test --solution csharp/Bpcg.slnx` → `dotnet format csharp/Bpcg.slnx --verify-no-changes` → `uv run pytest -m "not slow"`. macOS 에서 SDK 가 PATH 에 없으면 `/usr/local/share/dotnet` 을 앞에 둡니다.

### 결정이 필요한 쟁점

단계 1은 '기본값' 열의 선택으로 진행하고, 사용자가 바꾸면 따릅니다.

| 번호 | 쟁점 | 선택지 | 기본값 (추천) |
|---|---|---|---|
| 1 | 단계 4의 목표 (사용자에게 물었고 답을 기다림) | (1) 스튜디오가 계산 엔진으로 C# 콘솔을 고름, (2) Godot 안에서 C# 노드가 계산해 `user://`에 씀, (3) 웹 ↔ 떠 있는 Godot 실시간 연결 | 단계 3 뒤 (1), 단계 4에서 (2), (3)은 필요할 때만 (2장 끝) |
| 2 | C# 콘솔의 진행 로그 줄 | Python `cli`와 똑같이 / 자유롭게 | 똑같이. 스튜디오(`studio/progress.py`)가 그 줄을 읽으므로, 실행 명령만 바꿔 C# 엔진을 쓸 수 있습니다 |
| 3 | TOML 읽기 | Tomlyn + 정수 원문 범위 검사 / tomllib(810줄) 이식 | tomllib 이식으로 정함(10장 D9) |
| 4 | FIELDS의 bool·int8 원소형 (지시의 다섯 형 목록에 없음) | `bool[]`·`sbyte[]` / `byte[]`로 통일 | `bool[]`·`sbyte[]` (npy `\|b1`·`\|i1`와 1:1) |
| 5 | 셀 번호 배열의 원소형 | numpy dtype 그대로(이웃 표 int32, src·order int64) / 모두 int | numpy dtype 그대로 |
| 6 | 같은 이름 충돌(CS0542) | 모듈 정적 클래스와 같은 이름의 Python 클래스·함수가 있을 때의 규칙 | 클래스가 겹치면 모듈 함수를 그 클래스의 static으로(`Config.LoadConfig`), 함수가 겹치면 정적 클래스에 `Module` 접미사(`AccumulateModule.Accumulate`) |
| 7 | golden 입력 얻기 | src 함수를 직접 부름 / 실행 중 함수를 감싸 호출 지점 입력을 기록 | 단계 1은 직접 부름. 감싸기는 '부르기만 한다' 규칙과 맞는지 확인 뒤 단계 2에서 |
| 8 | CI·설치 스크립트를 고칠 시점 | 지시대로 단계 4 / 단계 1로 앞당김(9장 제안) | 지시대로 단계 4. 단계 1~3은 로컬에서 build·test를 확인 |
| 9 | 엔진은 계산하지 않는다는 규칙(conventions 12절)과 단계 4 | 단계 4에서 규칙을 고침 | 단계 4 시작 전 확인 때 함께 정함 |
| 10 | 커밋하는 golden의 위치 | `csharp/golden/data/` / 다른 곳 | `csharp/golden/data/` (`.gitignore` 예외를 이미 더함) |

나머지 질문은 13장(부록)에 분석 항목별로 모았습니다.

## 1. 환경

### 환경: SDK·Godot .NET·NuGet 패키지

조사일은 2026-10-02입니다. 아래 사실은 근거 링크나 이 컴퓨터에서 읽기만 한 명령으로 확인했고, 확인하지 못한 것은 '(확인 안 함)'으로 적습니다.

- 이 컴퓨터에는 .NET SDK가 없습니다. 지시 원문대로 설치 방법만 보고하고 C# 코드는 쓰지 않습니다.
- .NET 10의 최신판은 런타임 10.0.12, SDK 10.0.401(2026-09-08)이고 LTS로 2028-11-14까지 지원됩니다. .NET 8과 .NET 9는 2026-11-10에 지원이 끝납니다.
- Godot 4.7.2 .NET 판은 나와 있고, 그 C# 묶음(GodotSharp 4.7.2)의 최소 대상은 net8.0입니다. 편집기 런타임은 설치된 가장 높은 정식 런타임으로 올라가므로 net10.0 프로젝트가 돌 것으로 봅니다. 로컬 확인은 '설치 뒤 확인 절차'로 합니다.
- 'Godot 4.8은 net10.0이 최소'는 맞습니다. 다만 4.8은 아직 dev 판입니다(4.8.0-dev.7).

#### dotnet --info 결과

| 항목 | 결과 | 확인 방법 |
|---|---|---|
| `dotnet --info` | `zsh: command not found: dotnet` (종료 코드 127) | 셸에서 실행 |
| 설치 흔적 | `/usr/local/share/dotnet`, `~/.dotnet`, `/opt/homebrew/bin/dotnet`, `/usr/local/bin/dotnet` 모두 없음. `DOTNET_ROOT` 없음. `/etc/paths.d`에 dotnet 없음 | `ls`, `env` |
| 운영체제 | macOS 26.6.2 (25G83), Apple M4 Pro, arm64 | `sw_vers`, `uname -a` |
| Homebrew | 7.0.7, `/opt/homebrew/bin/brew` (cask API 캐시 2026-10-02 05:33) | `brew --version` |
| Godot | `.tools/godot/Godot.app` = `4.7.2.stable.official.ed1daf0bf`, universal(x86_64+arm64), `Contents/Resources/GodotSharp` 없음 → 표준판 | 임시 HOME으로 `--headless --version`, `file`, `ls` |

#### .NET 판과 지원 기간

| 채널 | 최신 런타임 | 최신 SDK | 발표일 | 종류 | 지원 끝 |
|---|---|---|---|---|---|
| 10.0 | 10.0.12 | 10.0.401 (1xx 띠는 10.0.112) | 2026-09-08 | LTS | 2028-11-14 |
| 9.0 | 9.0.20 | 9.0.318 | 2026-09-08 | STS | 2026-11-10 |
| 8.0 | 8.0.31 | 8.0.425 | 2026-09-08 | LTS | 2026-11-10 |
| 11.0 | 11.0.0-rc.1 | 11.0.100-rc.1.26425.128 | 2026-09-08 | STS(go-live) | - |

- 근거: <https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json>, <https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json> (10.0.12의 C# 판은 14.0), <https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core> (STS는 24개월).
- 다음 정기 패치는 둘째 화요일인 2026-10-13로 예상합니다(확인 안 함). 그래서 SDK 판은 global.json에서 범위만 고정합니다.

#### 설치 방법 (운영체제별 표와 추천)

| 운영체제 | 방법 | 명령 또는 파일 | 설치 위치 | 관리자 권한 | Godot이 찾는가 |
|---|---|---|---|---|---|
| macOS arm64 | 공식 .pkg | `dotnet-sdk-10.0.401-osx-arm64.pkg` | `/usr/local/share/dotnet`, `/etc/paths.d/dotnet` | 필요 | 찾음(기본 위치) |
| macOS arm64 | Homebrew cask | `brew install --cask dotnet-sdk` (같은 .pkg, 10.0.401) | 위와 같음 | 필요(sudo) | 찾음 |
| macOS arm64 | Homebrew formula | `brew install dotnet` (소스 빌드 10.0.401) | `/opt/homebrew/opt/dotnet/libexec` | 불필요 | 터미널에서 연 Godot만 (PATH 필요, godot#75668) |
| macOS, Linux | dotnet-install.sh | `./dotnet-install.sh --channel 10.0 --install-dir DIR --no-path` | 기본 `~/.dotnet` | 불필요 | PATH·DOTNET_ROOT를 줄 때만 |
| Windows x64 | winget | `winget install --id Microsoft.DotNet.SDK.10 -e --source winget` | `C:\Program Files\dotnet`, 레지스트리 등록 | 필요(machine 범위) | 찾음 |
| Windows x64 | dotnet-install.ps1 | `.\dotnet-install.ps1 -Channel 10.0 -InstallDir DIR -NoPath` | 기본 `%LocalAppData%\Microsoft\dotnet` | 불필요 | PATH·DOTNET_ROOT를 줄 때만 |
| Ubuntu 24.04 이상 | 배포판 패키지 | `sudo apt-get install -y dotnet-sdk-10.0` (Canonical 빌드, 1xx 띠) | `/usr/lib/dotnet` (확인 안 함) | 필요 | PATH의 `dotnet`으로 찾음 |
| Ubuntu 22.04 | backports PPA | `sudo add-apt-repository ppa:dotnet/backports` 뒤 위와 같음 | 같음 | 필요 | 같음 |

- Homebrew 버전 cask는 `dotnet-sdk@8`(8.0.425), `dotnet-sdk@9`(9.0.318), `dotnet-sdk@preview`(11.0.100-rc.1)가 있고 `@10`은 없습니다. 기본 `dotnet-sdk`가 10입니다. `@8`·`@9`는 `dotnet-sdk`에 기대고, `@preview`와는 함께 깔 수 없습니다. 이는 `brew info --cask`(읽기만)와 <https://github.com/Homebrew/homebrew-cask/blob/HEAD/Casks/d/dotnet-sdk.rb>에서 확인했습니다. formula `dotnet`은 Microsoft의 비공개 구성 요소가 빠진 소스 빌드이고 DOTNET_ROOT 안내가 붙습니다.
- dotnet-install 스크립트는 Microsoft가 CI와 관리자 권한이 없는 설치용이라고 밝히고, 개발 PC에는 설치 프로그램을 권합니다. 스크립트는 PATH를 그 셸에만 더하고 DOTNET_ROOT는 정하지 않으며, ICU 같은 의존 라이브러리도 설치하지 않습니다(<https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script>). `--dry-run --channel 10.0`으로 돌리면 `dotnet-sdk-10.0.401-osx-arm64.tar.gz`를 고릅니다(이 컴퓨터에서 확인, 설치하지 않음).
- 지금 설치 스크립트는 uv를 공식 스크립트로 `~/.local/bin`에, Godot을 SHA-512 확인 뒤 `.tools/godot/`에 받고, git·curl 같은 시스템 도구는 설치하지 않고 명령만 안내합니다(`scripts/setup.sh:206-240`, `:318-368`, `scripts/setup.ps1:292-334`).

| 안 | 장점 | 단점 |
|---|---|---|
| (가) 시스템 설치를 요구하고 스크립트는 안내만 (추천) | Godot이 환경 변수 없이 찾음, Finder에서 연 편집기와 IDE도 찾음, 보안 패치를 설치 프로그램으로 받음 | 관리자 권한 필요, 저장소마다 판을 고정하지 못함(global.json으로 범위만 고정) |
| (나) `--dotnet`이 `.tools/dotnet`에 설치 | 권한 불필요, 판 고정, `.tools/` 관례와 같음 | 모든 셸·IDE·Godot 실행에 PATH와 DOTNET_ROOT가 필요함. macOS의 Godot은 `/usr/local/share/dotnet/dotnet`이 있으면 빌드에 그것을 먼저 씀(godot#97501). 저장소를 옮기면 경로가 깨짐 |
| (다) `--dotnet`이 `~/.dotnet`에 설치 | 권한 불필요, 저장소 위치와 무관, uv(`~/.local/bin`)와 같은 사용자 범위 | (나)와 같은 환경 변수 문제 |

추천은 (가)입니다. setup.sh·setup.ps1은 `dotnet --list-sdks`에 10.0.x가 없으면 위 표의 명령을 보여 주고, doctor.py가 같은 점검을 합니다. 관리자 권한이 없는 Linux를 위해 (다)를 `--dotnet` 선택 옵션으로 둘지는 단계 4에서 정합니다. (나)는 권하지 않습니다.

저장소 최상위에 둘 global.json(단계 1 제안)은 아래와 같습니다. 판 10.0.100 이상의 10.x 가운데 가장 높은 정식 SDK를 쓰고, .NET 10 SDK의 `dotnet test`를 MTP 모드로 돌립니다.

```json
{
  "sdk": { "version": "10.0.100", "rollForward": "latestFeature", "allowPrerelease": false },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

- `dotnet` 명령은 현재 폴더부터, MSBuild SDK 해석기는 프로젝트 폴더부터 위로 global.json을 찾습니다(<https://learn.microsoft.com/en-us/dotnet/core/tools/global-json>). Godot은 `--path engine`이 작업 폴더를 `engine/`으로 바꾸므로(main.cpp `set_cwd`) 같은 파일을 봅니다. `allowPrerelease`는 적지 않으면 true라서 적습니다. .NET 10의 `paths` 항목도 저장소 안 SDK를 가리킬 수 있지만, 10보다 낮은 `dotnet`이 먼저 잡히면 이 항목을 무시하므로 쓰지 않습니다.

#### CI 설정

- actions/setup-dotnet의 최신판은 v6.0.0(2026-07-16)이고 `@v6` 태그가 있습니다. 입력은 `dotnet-version`(`10.0.x` 꼴), `dotnet-quality`, `dotnet-channel`, `global-json-file`, `source-url`, `owner`, `config-file`, `cache`, `cache-dependency-path`, `workloads`, `architecture`입니다. global.json의 `latest*` rollForward를 지원하고, Linux는 `/usr/share/dotnet`, macOS는 `~/.dotnet`, Windows는 `C:\Program Files\dotnet`에 설치하며 PATH와 DOTNET_ROOT를 내보냅니다(<https://github.com/actions/setup-dotnet/blob/v6.0.0/action.yml>, `src/installer.ts`).
- GitHub 호스트 러너에는 SDK가 이미 있습니다. ubuntu-24.04에는 10.0.401, macos-15-arm64에는 10.0.400, windows-2025에는 10.0.401이 있습니다(<https://github.com/actions/runner-images>). 그래도 판은 global.json으로 고정합니다.
- chickensoft-games/setup-godot의 최신판은 v2.4.3(2026-10-01)이고 `@v2`가 v2.4.3을 가리킵니다. `use-dotnet: true`(기본값)는 `_mono` 판 zip을 받아 `GodotSharp.dll`이 있는지 보고 GodotSharp 폴더를 bin에 링크할 뿐이며, .NET SDK는 설치하지 않습니다. README 예시도 setup-dotnet을 따로 둡니다(<https://github.com/chickensoft-games/setup-godot/blob/v2.4.3/src/main.ts>). 지금 `ci.yml:89-93`은 `use-dotnet: false`입니다.
- 지시 원문의 'CI의 use-dotnet 설정'은 단계 4에서 engine 작업의 `use-dotnet`을 `true`로 바꾸고, 그 앞에 `actions/setup-dotnet@v6`(`global-json-file: global.json`) 단계를 더하는 일입니다. engine 작업은 지금처럼 ubuntu-latest에 둡니다. macOS 러너는 SDK를 `~/.dotnet`에 두고 `/usr/local/bin/dotnet`으로 링크하므로(runner-images `install-dotnet.sh`), 그곳의 Godot은 PATH와 DOTNET_ROOT로 SDK를 찾습니다.
- C# 빌드·시험 작업은 ubuntu-latest, windows-latest, macos-15에서 `setup-dotnet` → `dotnet restore --locked-mode` → `dotnet build` → `dotnet test` → `dotnet format --verify-no-changes` 순서로 도는 꼴을 제안합니다. 이 작업에는 Godot이 필요 없습니다.

#### Godot 4.7.2 .NET 판과 net10.0 (로컬 확인 전, 근거 링크)

| 대상 | 파일 (4.7.2-stable 릴리스) | 크기 | 풀었을 때 실행 파일 |
|---|---|---|---|
| macOS universal | `Godot_v4.7.2-stable_mono_macos.universal.zip` | 200,756,622 B | `Godot_mono.app/Contents/MacOS/Godot` (GodotSharp는 `Contents/Resources/`) |
| Windows x64 | `Godot_v4.7.2-stable_mono_win64.zip` | 116,582,061 B | `Godot_v4.7.2-stable_mono_win64/…_mono_win64_console.exe` 옆에 `GodotSharp/` |
| Linux x86_64 | `Godot_v4.7.2-stable_mono_linux_x86_64.zip` | 107,698,034 B | `Godot_v4.7.2-stable_mono_linux_x86_64/…_mono_linux.x86_64` 옆에 `GodotSharp/` |
| 내보내기 템플릿 | `Godot_v4.7.2-stable_mono_export_templates.tpz` | 1,202,598,411 B | 설치 위치 `export_templates/4.7.2.stable.mono/` |

zip 안 목록은 HTTP Range로 중앙 디렉터리만 읽어 확인했습니다. SHA-512 값은 <https://github.com/godotengine/godot/releases/download/4.7.2-stable/SHA512-SUMS.txt>에서 가져왔고, 단계 4의 설치 스크립트에 넣을 값입니다.

```text
0862c53d7158c7a67f745e2e46f90b68cf5343cbe8b95d6d4333c469e42ca104af9c121d1746a50e5d221a99d09d82ef7016495f8e0d09255842884ed0502795  …_mono_macos.universal.zip
79229fd112b0c9cbeab82363a4ef7be18ea70f1caf86bf912789335b136fbe7e01db0053a33461438c5da1c680c17bbb10040bd09bedc51221cd4423d0367757  …_mono_win64.zip
b458d0d9fd1b1f36081980dd07e402d71e667f042ab93c337d074ff03fbf3b2eccb3016f964155b7010051f67b928caefb9378fdaac00559e57167b9cf0466d1  …_mono_windows_arm64.zip
1855960b27ee3ef5e66e5e228cced69d55637b24334a7411162687dcd077d8f9f645348cdb8eae984bec8135d49ed855a1e3a16476786b8bce60774fd8402d13  …_mono_linux_x86_64.zip
4b8b700ea21bea16b1a2ed8bc3a266431604f0d6452c819e022bfadea33eb431269bd680b11cd9dab93fc1f487ae4a60d15ee511e5691a82c22a2753b428d263  …_mono_linux_arm64.zip
bb5c41d72370ed743660361f6228006f808ab04ca33abdc545d740b044f3fe057f32ae8cb7873a1bc86ddcd82ae683b9f6dfdfe4179852f2c0f1acde2ff6bd5a  …_mono_export_templates.tpz
```

net10.0이 될 것으로 보는 근거는 다음과 같습니다.

1. Godot.NET.Sdk 4.7.2(MIT, 2026-08-18)가 NuGet에 있습니다. 같이 쓰는 GodotSharp 4.7.2와 GodotSharpEditor 4.7.2는 `lib/net8.0`뿐입니다(nupkg를 받아 열어 봄). nuspec의 커밋 `ed1daf0bf`는 로컬 Godot과 같은 커밋입니다.
2. 4.7.2 편집기가 새 csproj에 쓰는 값은 `TargetFramework` = `net8.0`, android 조건부 `net9.0`, `EnableDynamicLoading` = `true`입니다(`GodotTools.ProjectEditor/ProjectGenerator.cs`, 태그 4.7.2-stable). `ProjectUtils.EnsureTargetFrameworkMatchesMinimumRequirement`는 net8.0보다 낮은 값만 올리므로 net10.0은 그대로 둡니다. `EnsureGodotSdkIsUpToDate`는 Sdk 속성을 `Godot.NET.Sdk/4.7.2`로 다시 씁니다.
3. 편집기 런타임 설정 `GodotSharp/Api/Debug/GodotPlugins.runtimeconfig.json`은 `tfm net8.0`, `rollForward LatestMajor`입니다(배포 zip 안 파일을 직접 읽음). 그래서 설치된 가장 높은 정식 런타임(10.0.x)에서 돌고 net10.0 게임 어셈블리도 같은 런타임에 올립니다. 정식판이 아닌 런타임은 고르지 않아, .NET 10 RC에서는 실패했고 GA SDK 10에서 풀렸습니다(godot#111246).
4. 빌드는 `dotnet build <csproj> -c Debug`이고, 데스크톱 내보내기는 `dotnet publish -c ExportRelease -r <rid> --self-contained true -p:GodotTargetPlatform=<os>`입니다(`BuildSystem.cs`, `BuildManager.cs`). 런타임을 함께 넣으므로 TFM에 기대지 않습니다.
5. Godot 블로그는 "the version targeted by Godot's C# packages is only the minimum version that your project can target"라고 썼습니다(<https://godotengine.org/article/godotsharp-packages-net8/>). 포럼에도 4.7.2와 4.8.0-dev.6에서 net10.0 디버그가 된다는 보고가 있습니다(<https://forum.godotengine.org/t/use-dotnet-11-rc-sdk/144192/5>, 확인 안 함).
6. 4.8에 관한 근거입니다. godot#123738 "Upgrade packages and minimum TFM required to `net10.0`"이 2026-09-26에 합쳐졌고 이정표는 4.8입니다. master의 `GodotMinimumRequiredTfm`은 `net10.0`이고, GodotSharp 4.8.0-dev.7(2026-09-29)은 `lib/net10.0`입니다(dev.6까지는 net8.0). godot-docs master에는 "Godot 4.8 requires .NET 10 or later."라고 적혀 있습니다. Android 내보내기는 4.7 템플릿이 net10.0을 받지 않지만(godot#118989) 우리 대상이 아닙니다.

Godot이 SDK를 찾는 순서(4.7.2 소스)는 다음과 같습니다.

- 런타임(C++ `hostfxr_resolver.cpp`, `gd_mono.cpp`)은 `DOTNET_ROOT_ARM64` → `DOTNET_ROOT` → `/etc/dotnet/install_location_arm64` → `/etc/dotnet/install_location` → `/usr/local/share/dotnet` 순서로 찾습니다. 모두 실패하면 PATH의 `dotnet --list-sdks`로 찾습니다.
- 빌드용 `dotnet`(C# `DotNetFinder.cs`)은 macOS에서 `/usr/local/share/dotnet/dotnet`이 있으면 그것을 쓰고, 없으면 PATH에서 찾습니다. DOTNET_ROOT는 보지 않습니다. 두 탐색이 어긋나는 문제는 godot#97501에 열려 있고, 고치는 PR godot#98073도 열려 있습니다. Homebrew formula를 쓰려면 `/usr/local/share/dotnet`으로 링크해야 했다는 보고가 있습니다(godot#75668). Finder에서 연 앱은 셸 설정의 PATH와 DOTNET_ROOT를 받지 못합니다.
- .NET 판 편집기는 C#이 없는 프로젝트를 열어도 늘 .NET을 초기화하고(`GDMono::should_initialize`), hostfxr가 없으면 경고 창을 띄웁니다. 엔진을 .NET 판으로 바꾸면 GDScript만 만지는 사람에게도 SDK가 필요합니다.

engine/ csproj가 밖의 라이브러리를 ProjectReference로 참조할 때는 다음을 지킵니다.

- Godot.NET.Sdk는 기본 Compile glob을 끄지 않으므로 `engine/` 아래 모든 .cs가 게임 어셈블리에 들어갑니다. 그래서 계산 라이브러리는 engine/ 밖에 둡니다(지시 원문의 배치와 같음).
- 라이브러리는 `Microsoft.NET.Sdk`로 만들고 GodotSharp를 참조하지 않습니다. GodotSharp는 Godot.NET.Sdk가 게임 프로젝트에만 넣습니다.
- Godot은 `-c Debug`, `-c ExportDebug`, `-c ExportRelease`로 빌드하고, 이 값은 참조한 프로젝트까지 넘어갑니다. Microsoft.NET.Sdk는 `Release`일 때만 `Optimize=true`로 두므로, 내보낸 게임의 Bpcg가 최적화 없이 컴파일됩니다(godot#96435에서 Godot 관리자가 확인, dotnet/sdk v10.0.401 `Microsoft.NET.Sdk.props:47-53`). 그래서 `csharp/Directory.Build.props`에 ExportRelease일 때 Optimize를 켜는 규칙을 넣습니다.
- 내보내기는 sln이나 slnx가 있어야 'dotnet' 특징을 붙이고 C#을 넣습니다(`ExportPlugin.ProjectContainsDotNet`). 4.7.2는 `.slnx`도 찾습니다.
- 프로젝트 폴더에 csproj가 생기면 편집기가 `project.godot`의 `config/features`에 `"C#"`을 넣습니다(`core/config/project_settings.cpp`). 그러면 표준판에서 경고가 납니다. 그래서 단계 1~3에서는 engine/ 아래에 .cs나 .csproj를 두지 않습니다.
- `dotnet/project/assembly_name`이 없으면 어셈블리 이름은 `config/name`에서 온 `B-PCG`가 됩니다(`modules/mono/utils/path_utils.cpp`). 단계 4에서 `BpcgEngine` 같은 이름을 적어 둡니다.
- `FrameworkReference`(ASP.NET Core 등)의 어셈블리를 Godot이 찾지 못하는 문제가 열려 있습니다(godot#120843). Bpcg는 기본 프레임워크만 씁니다.
- Godot 노드 클래스에 C# 14 문법을 쓰면 편집기 빌드가 깨진다는 4.6 포럼 보고가 있습니다(<https://forum.godotengine.org/t/net10-and-c-14/133412>, 확인 안 함). 아래 확인 절차의 (사)에서 봅니다.

판단: macOS·Windows·Linux 데스크톱에서는 net10.0이 될 것으로 봅니다. 로컬 확인이 실패하면 대안은 두 가지입니다. engine csproj만 net8.0으로 두고 Bpcg를 `net8.0;net10.0` 다중 대상으로 만들 수 있지만, .NET 8은 2026-11-10에 지원이 끝납니다. 아니면 Godot 4.8 정식판을 기다립니다.

#### 설치 뒤 확인 절차

SDK와 Godot .NET 판을 깐 뒤 macOS에서 한 번 돌리고, 결과를 진행 기록에 적습니다. 모든 작업은 저장소 밖 임시 폴더에서 하므로 engine/은 건드리지 않습니다.

```bash
# (가) SDK: 'Version: 10.0.4xx', Host 10.0.x arm64, Base Path /usr/local/share/dotnet/sdk/... 이어야 함
dotnet --info && dotnet --list-sdks && dotnet --list-runtimes
# (나) Godot .NET 판 (.tools/ 는 git 제외). shasum 결과는 위 0862c53d… 와 같아야 함
REPO=/Users/jungjuwon/Desktop/3-2/Capstone/B-PCG; mkdir -p "$REPO/.tools/godot-net"; cd "$REPO/.tools/godot-net"
curl -fLO https://github.com/godotengine/godot/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_macos.universal.zip
shasum -a 512 Godot_v4.7.2-stable_mono_macos.universal.zip && ditto -x -k Godot_v4.7.2-stable_mono_macos.universal.zip .
GODOT_NET="$PWD/Godot_mono.app/Contents/MacOS/Godot"
# (다) 버리는 작업 폴더. HOME 은 conventions 12절대로 임시 폴더, NuGet 캐시는 한곳에 고정
W="$(mktemp -d "${TMPDIR:-/tmp}/bpcg-net10.XXXXXX")"; mkdir -p "$W/home" "$W/lib/ProbeLib" "$W/game"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 NUGET_PACKAGES="$W/nuget"
G() { HOME="$W/home" "$GODOT_NET" --headless --path "$W/game" "$@"; }
cat > "$W/lib/ProbeLib/ProbeLib.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)' == 'ExportRelease'">
    <Optimize Condition="'$(Optimize)' == ''">true</Optimize>
  </PropertyGroup>
</Project>
EOF
cat > "$W/lib/ProbeLib/Probe.cs" <<'EOF'
namespace ProbeLib;
public static class Probe
{
    public static ulong SplitMix64(ulong x) { unchecked {
        ulong z = x + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31); } }
    public static ulong Hash3(long a, long b, long c) { unchecked {
        ulong h = SplitMix64((ulong)a); h = SplitMix64(h ^ (ulong)b); return SplitMix64(h ^ (ulong)c); } }
}
EOF
cat > "$W/game/project.godot" <<'EOF'
config_version=5

[application]

config/name="Net10Probe"
run/main_scene="res://main.tscn"

[dotnet]

project/assembly_name="Net10Probe"
EOF
cat > "$W/game/Net10Probe.csproj" <<'EOF'
<Project Sdk="Godot.NET.Sdk/4.7.2">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="../lib/ProbeLib/ProbeLib.csproj" /></ItemGroup>
</Project>
EOF
cat > "$W/game/Main.cs" <<'EOF'
using System.Reflection;
using System.Runtime.Versioning;
using Godot;
public partial class Main : Node
{
    public override void _Ready()
    {
        string tfm = typeof(Main).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "?";
        GD.Print($"BPCG_NET10_OK runtime={System.Environment.Version} tfm={tfm} " +
                 $"sm0=0x{ProbeLib.Probe.SplitMix64(0):X16} h3=0x{ProbeLib.Probe.Hash3(1, 2, 3):X16}");
        GetTree().Quit();
    }
}
EOF
printf '[gd_scene format=3]\n\n[ext_resource type="Script" path="res://Main.cs" id="1"]\n\n[node name="Main" type="Node"]\nscript = ExtResource("1")\n' > "$W/game/main.tscn"
# (라) 명령줄 빌드와 Godot 빌드·실행 (실행은 편집기의 '프로젝트 실행'과 같은 경로)
cd "$W/game" && dotnet new sln --format sln -n Net10Probe && dotnet sln Net10Probe.sln add Net10Probe.csproj
shasum -a 256 Net10Probe.csproj > "$W/csproj.sha"
dotnet build Net10Probe.csproj -c Debug && ls .godot/mono/temp/bin/Debug/
G --version
G --import > "$W/import.log" 2>&1; echo "import rc=$?"
G --build-solutions --quit > "$W/build.log" 2>&1; echo "build rc=$?"
G --verbose --quit-after 600 > "$W/run.log" 2>&1; echo "run rc=$?"
grep -E "BPCG_NET10_OK|Found hostfxr|Failed to load|Unable to load" "$W/run.log"; shasum -a 256 -c "$W/csproj.sha"
# (마) 내보내기에서 하는 C# 단계만 대신 돌림 (내보내기 템플릿 불필요)
dotnet msbuild "$W/lib/ProbeLib/ProbeLib.csproj" -getProperty:Optimize -p:Configuration=ExportRelease
dotnet publish Net10Probe.csproj -c ExportRelease -r osx-arm64 --self-contained true -p:GodotTargetPlatform=macos -o "$W/publish"
ls "$W/publish" | grep -E '^(Net10Probe|ProbeLib)\.dll$|^libhostfxr\.dylib$|^System\.Private\.CoreLib\.dll$'
# (바) 끝나면 지움: rm -rf "$W"
```

| 단계 | 성공 기준 |
|---|---|
| (가) | SDK 10.0.4xx, Host 10.0.x, RID `osx-arm64`, `Microsoft.NETCore.App 10.0.x [/usr/local/share/dotnet/shared/...]` |
| (나) | SHA-512 일치, `G --version` = `4.7.2.stable.mono.official.ed1daf0bf` (mono 표기는 확인 안 함) |
| (라) | `dotnet build` 오류 0, `.godot/mono/temp/bin/Debug/`에 `Net10Probe.dll`과 `ProbeLib.dll`, 세 Godot 명령의 rc 0 |
| (라) 실행 | `BPCG_NET10_OK runtime=10.0.x tfm=.NETCoreApp,Version=v10.0 sm0=0xE220A8397B1DCDAF h3=0xD0734750FDE362B3`, `Found hostfxr: /usr/local/share/dotnet/host/fxr/10.0.x/libhostfxr.dylib`, `Failed to load project assembly` 없음, csproj 해시 같음 |
| (마) | `Optimize` 값이 `true`(조건을 지우면 빈 값으로 godot#96435 재현), publish 폴더에 네 파일, `Net10Probe.runtimeconfig.json`의 `includedFrameworks`가 10.0.x |

- (사) 선택: `extension(int n) { public int Twice => n * 2; }`가 든 static 클래스 파일을 game/에 더하고 (라)의 빌드를 다시 돌려, C# 14 문법이 Godot 소스 생성기를 깨는지 봅니다.
- (아) 선택: 진짜 내보내기는 mono 내보내기 템플릿(1.2 GB)을 `$W/home/Library/Application Support/Godot/export_templates/4.7.2.stable.mono/`에 풀고, macOS 프리셋을 담은 `export_presets.cfg`를 만든 뒤 `G --export-release "macOS" "$W/export/Net10Probe.zip"`로 합니다. 서명 설정 때문에 단계 4로 미룹니다.
- Windows에서는 `HOME`, `APPDATA`, `LOCALAPPDATA`를 임시 폴더로 바꾸고 `_console.exe`와 `-r win-x64`를 씁니다. Linux에서는 `XDG_*`도 임시 폴더로 바꾸고 `-r linux-x64`를 씁니다.

#### NuGet 패키지 후보와 라이선스

| 패키지 | 판(발표일) | 라이선스 | 쓸 곳 | 판단 |
|---|---|---|---|---|
| Tomlyn | 2.10.1 (2026-07-02) | BSD-2-Clause | Bpcg (`Bpcg.IO.Toml`이 감쌈) | 추천. net10.0용 의존 패키지 없음 |
| Samboy063.Tomlet | 6.2.0 (2025-12-24) | MIT | - | 예비 |
| Tommy | 3.1.2 (2022-01-11) | MIT(파일) | - | 제외. 2022-01 뒤로 커밋이 없음 |
| xunit.v3 | 4.0.1 (2026-09-12) | Apache-2.0 | Bpcg.Tests | 추천. xunit.v3.mtp-v2를 거쳐 Microsoft.Testing.Platform 2.4(MIT)와 원격 측정용 ApplicationInsights(MIT)가 따라옴 |
| xunit (v2) | 2.9.3 (2025-01-08) | Apache-2.0 | - | 제외. NuGet에 Legacy 폐기 표시가 있고 보안 수정만 받음 |
| xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | 4.0.0 (2026-08-15), 18.10.1 (2026-09-15) | Apache-2.0, MIT | (선택) | VSTest만 아는 IDE용. MTP 모드의 `dotnet test`에는 필요 없음 |
| System.IO.Hashing | 10.0.12 (2026-09-08) | MIT | - | 제외. 필요한 것은 PNG CRC32 하나뿐이라 표 방식 약 20줄로 직접 구현 |
| System.CommandLine | 2.0.12 (2026-09-08) | MIT | - | 제외. 명령 4개와 옵션 몇 개라 직접 구현 |

TOML 라이브러리 비교:

| 항목 | Python 3.13 tomllib (기준) | Tomlyn 2.10.1 | Tomlet 6.2.0 |
|---|---|---|---|
| TOML 판 | 1.0.0 (1.1 문법을 거부, 실험) | 1.1.0 전용 (1.0의 상위 집합) | 1.0.0 |
| 키 순서 | dict가 문서 순서 (실험) | `TomlTable`이 삽입 순서 보존 (List, 문서화됨) | `Dictionary` (순서 보장 문서 없음) |
| 정수·실수 구분 | int, float | long, double | TomlLong, TomlDouble |
| int64 밖 정수 | 받음 (`9223372036854775808`도 int) | 2^63~2^64-1은 오류 없이 음수로 감김 (소스 읽기), 그 위는 오류 | `Convert.ToInt64` 예외 → 오류 |
| `inf`·`nan`, `6_400.0`, `1e400` | 받음, 6400.0, inf | 받음, 밑줄 처리, `double.TryParse` | 받음, 밑줄 처리 |

- manifest.json의 `config`는 키를 정렬하지 않고 설정 dict 순서대로 씁니다(`bake/bundle.py:125,234`). 그래서 순서를 문서로 보장하는 Tomlyn을 고릅니다. 감싸는 코드에서는 int64 범위 밖 정수를 오류로 막고, TOML 1.1 전용 문법(`--set a={x=1,}` 등)을 Python과 달리 받는 점을 진행 기록에 적습니다.
- 최소 묶음은 Bpcg에 Tomlyn 하나, Bpcg.Tests에 xunit.v3 하나이고 Bpcg.Cli는 없습니다. System.IO.Compression(zip, zlib), SHA256, System.Text.Json은 .NET에 들어 있어 패키지가 아닙니다. MathNet.Numerics 같은 수치 라이브러리는 numpy·scipy와 비트 단위 일치를 보장하지 않으므로 쓰지 않습니다.
- 원격 측정은 CI와 시험 하네스에서 `DOTNET_CLI_TELEMETRY_OPTOUT=1`(MTP는 `TESTINGPLATFORM_TELEMETRY_OPTOUT=1`도 받음)로 끕니다.

#### 고칠 곳 (환경)

- 단계 1에서는 저장소 최상위에 `global.json`을 둡니다. `csharp/Directory.Build.props`에는 net10.0, Nullable, ExportDebug·ExportRelease 구성 대응, `RestorePackagesWithLockFile`을 넣습니다. `.gitignore`에 `bin/`, `obj/`, `*.user`, `.vs/`, `TestResults/`, `*.binlog`를 더하고, `.editorconfig`에 `[*.cs]` 규칙을 더합니다.
- 단계 4에서는 다음을 고칩니다.
  - `scripts/setup.sh`·`scripts/setup.ps1`: mono zip의 이름, SHA-512, 풀린 폴더 구조(`Godot_mono.app`, Linux·Windows의 하위 폴더와 그 옆 `GodotSharp/`), SDK 점검과 안내.
  - `tools/doctor.py:418-431`과 `tests/test_engine_smoke.py:38-72`: 후보 경로, `_isolated_env`에 NUGET_PACKAGES 고정, C# 빌드 단계.
  - `ci.yml`: engine 작업에 setup-dotnet과 `use-dotnet: true`를 더함.
  - `docs/conventions.md:222`(표준판 규칙)와 `:229`(임시 HOME), `engine/README.md:11,21,35,88,109`, `README.md:25`.
  - `engine/project.godot`: `dotnet/project/assembly_name`.

## 2. 폴더와 프로젝트 구성 (계획)

```text
global.json                  SDK 10.0 고정 (rollForward latestFeature). 저장소 맨 위에 두어 단계 4의 Godot 빌드도 같은 SDK 를 씀
csharp/
├── Directory.Build.props    net10.0, Nullable, TreatWarningsAsErrors, EnforceCodeStyleInBuild, 잠금 파일
├── Bpcg.slnx
├── Bpcg/                    계산 라이브러리 (Godot 에 의존하지 않음)
│   ├── Package.cs           bpcg.__version__
│   ├── Pipeline.cs          pipeline.py
│   ├── Core/ Planet/ Geology/ Hydro/ Landscape/ Subsurface/ Metrics/ Hero/ Volume/ Bake/
│   ├── Numerics/            numpy·scipy·scikit-image 를 대신하는 코드 (Python 모듈과 1:1 이 아님)
│   └── IO/                  .npy·.npz·JSON·TOML·PNG·glb 형식 (경로는 정하지 않고 바이트 ↔ 값만)
├── Bpcg.Cli/                콘솔 프로그램 (planet, hero, bake, all)
├── Bpcg.Tests/              xUnit. Python 모듈 하나에 시험 클래스 하나 (<Pkg>/<Mod>Tests.cs)
└── golden/
    ├── export_golden.py     golden 자료를 만드는 Python 스크립트 (7장)
    └── data/                커밋하는 작은 golden (각 1 MB 이하)
```

- **대응 규칙.** `src/bpcg/<pkg>/<mod>.py` → `csharp/Bpcg/<Pkg>/<Mod>.cs`, namespace `Bpcg.<Pkg>`, 모듈 수준 함수는 `public static class <Mod>`의 메서드(`bpcg.hydro.routing.topo_order` → `Bpcg.Hydro.Routing.TopoOrder`), dataclass는 같은 파일의 `sealed class`나 `record`입니다. 이름이 겹칠 때의 규칙은 0장 쟁점 6입니다.
- **배열.** 셀마다 하나인 필드는 길이 N의 1차원 배열, (N, L) 필드는 행 우선 1차원 배열과 열 수입니다. 원소형은 FIELDS의 dtype을 따릅니다: float32 `float`, float64 `double`, uint8 `byte`, int32 `int`, int64 `long`, bool `bool`, int8 `sbyte`(0장 쟁점 4).
- **의존 방향.** `Numerics`·`IO`는 아무것도 참조하지 않고, 그 위에 docs/conventions.md 1절의 `Core → Planet·Geology·Hydro → Landscape → Subsurface → Volume → Bake` 순서를 그대로 지킵니다. `Pipeline`과 `Hero`는 서로를 부르므로 한 단위로 봅니다.
- **수치 규칙.**
  - 계산 코드에서 Godot 자료형(`Vector3` 등)을 쓰지 않습니다.
  - 해시는 `unchecked` 안에서 `ulong`으로 계산합니다.
  - `Math.FusedMultiplyAdd`와 식 재배열을 쓰지 않고, Python과 같은 결합 순서로 씁니다.
  - 정렬은 안정 정렬로 하고, 같은 값의 순서는 Python 규칙(대부분 셀 번호)을 따릅니다.
  - prange 반복만 `Parallel.For`로 옮기고, 그 안에서는 칸·점마다 쓰기만 합니다.
  - 숫자 ↔ 글자 변환은 모두 `CultureInfo.InvariantCulture`로 합니다.

### 현재 스튜디오·Godot 연결 구조와 단계 4 선택지

지금 구조는 다음과 같습니다(`src/bpcg/studio/`, `engine/scripts/`를 읽어 확인).

- 브라우저는 화면만 그리고, 스튜디오 서버(Python `http.server`, 127.0.0.1:8765)와 JSON으로 주고받습니다.
- '실행'을 누르면 서버가 하위 프로세스로 `python -m bpcg.cli all --profile … --set 키=값 …`을 돌립니다. 계산은 이 Python 프로세스가 numpy·numba로 합니다.
- 서버는 그 프로세스의 기록 줄(`[1단계] …`, `[2단계] 솔버 끝 …`, `솔버 반복 k:`, `[굽기] …`)을 `studio/progress.py`로 읽어 진행률을 계산합니다.
- 결과는 `out/studio/<실행>/`의 파일이고, 지도·점수표 탭은 그 파일을 읽습니다.
- 'Godot로 보기'를 누르면 회랑·지구본 파일을 `engine/baked/`로 복사하고, `Godot --headless --import`(임시 HOME) 뒤 `Godot --path engine`을 따로 띄웁니다.
- GDScript는 `BakedPaths`(`res://baked` 또는 `-- --baked-dir=<폴더>`)로 파일을 읽어 그리기만 합니다. `res://` 밖 폴더의 `caves.glb`는 이미 `GLTFDocument`로 실행 중에 읽습니다(`baked_layers.gd:480-492`).
- 스튜디오와 Godot 사이에 실시간 통로는 없습니다(파일 + 프로세스 실행).

단계 4 선택지(0장 쟁점 1)는 다음과 같습니다.

1. **계산 엔진만 바꿔 끼우기.** 스튜디오가 `python -m bpcg.cli` 대신 C# 콘솔을 고를 수 있게 합니다. 인자·기록 줄·결과 파일이 같으면 스튜디오와 Godot는 거의 그대로 둡니다. 단계 3 직후에 할 수 있고 일이 적습니다.
2. **Godot 안에서 계산.** C# 노드가 같은 `Bpcg` 라이브러리를 주 스레드 밖에서 불러 `user://`에 쓰고, `BakedPaths`가 그 폴더를 읽습니다(지시의 단계 4). 웹 없이 Godot 하나로 도는 데모·내보낸 게임용입니다.
3. **웹 ↔ 떠 있는 Godot 실시간 연결.** WebSocket 등으로 값을 넘겨 같은 창에서 다시 계산합니다. 지시가 범위 밖으로 둔 '메모리로 넘기는 연결'과 가까워 별도 작업입니다.

- 1·2는 C# 포팅이 끝나 끝에서 끝 대조(단계 3)를 통과한 뒤에 합니다.
- 팀 전원이 .NET SDK와 Godot .NET 판을 써야 합니다.
- C# Godot 프로젝트의 웹(HTML5) 내보내기 지원 여부는 확인이 필요합니다.
- 일정은 데모 동결 11/18, 최종 12/4입니다.

## 3. 대응표

Python 모듈 60개(`src/bpcg` 에서 studio/ 와 빈 earth/ 를 뺀 것)를 단계 2의 순서로 적었습니다. 함수별 대응은 6장 각 절의 '(a) 대응표'에 있습니다. 상태는 시작 전 → 포팅 중 → 대조 통과(exact) / 대조 통과(허용 오차) / 갈림 기록 으로 바꿉니다.

| Python 모듈 | 줄 | C# 파일 | C# 형 | 위험 | 상태 | 대조 결과 |
|---|---|---|---|---|---|---|
| `__init__.py` | 7 | csharp/Bpcg/Package.cs | public static class Package (namespace Bpcg) | 낮음 | 옮김 | 판 문자열 시험 |
| `core/__init__.py` | 4 | 없음 (namespace Bpcg.Core로만 대응) | 없음 | 낮음 | 해당 없음 | - |
| `core/config.py` | 246 | csharp/Bpcg/Core/Config.cs | public class Section; public sealed class Config : Section (모듈 함수는 정적 클래스 이름과 겹쳐 Config의… | 높음 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `core/constants.py` | 27 | csharp/Bpcg/Core/Constants.cs | public static class Constants | 낮음 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `core/fields.py` | 101 | csharp/Bpcg/Core/Fields.cs | public sealed record FieldInfo(string Group, string Unit, string Dtype, string Descriptio… | 낮음 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `core/paths.py` | 28 | csharp/Bpcg/Core/Paths.cs | public static class Paths | 중간 | 옮김 (golden 없음) | Config 시험이 설정 폴더 찾기를 씀 |
| `core/hashing.py` | 49 | csharp/Bpcg/Core/Hashing.cs | public static class Hashing | 낮음 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `core/noise.py` | 246 | csharp/Bpcg/Core/Noise.cs | public static class Noise | 낮음 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `core/cubesphere.py` | 209 | csharp/Bpcg/Core/Cubesphere.cs | public static class Cubesphere; public sealed class Grid | 중간 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `core/graph.py` | 146 | csharp/Bpcg/Core/Graph.cs | public static class Graph; public sealed class CellGraph | 중간 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `core/distance.py` | 274 | csharp/Bpcg/Core/Distance.cs | public static class Distance | 중간 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `core/resample.py` | 291 | csharp/Bpcg/Core/Resample.cs | public static class Resample; public sealed record GhostStencil | 중간 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `hydro/__init__.py` | 4 | (없음) | 없음 | 낮음 | 해당 없음 | - |
| `hydro/accumulate.py` | 66 | csharp/Bpcg/Hydro/Accumulate.cs | public static class AccumulateModule (namespace Bpcg.Hydro). 정적 클래스 Accumulate 안에 메서드 Acc… | 낮음 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `hydro/depressions.py` | 219 | csharp/Bpcg/Hydro/Depressions.cs | public static class Depressions (namespace Bpcg.Hydro) | 중간 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `hydro/network.py` | 109 | csharp/Bpcg/Hydro/Network.cs | public static class Network (namespace Bpcg.Hydro) | 낮음 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `hydro/routing.py` | 229 | csharp/Bpcg/Hydro/Routing.cs | public static class Routing (namespace Bpcg.Hydro). 반환 튜플은 값 튜플 (long[] Rcv, double[] Slo… | 중간 | 대조 통과(exact) | macOS arm64 비트 일치 |
| `planet/__init__.py` | 5 | (없음) | (없음) | 낮음 | 시작 전 | - |
| `planet/plates.py` | 691 | csharp/Bpcg/Planet/Plates.cs | public static class Plates (namespace Bpcg.Planet); sealed record BoundaryClassification;… | 중간 | 시작 전 | - |
| `planet/crust.py` | 286 | csharp/Bpcg/Planet/Crust.cs | public static class Crust (namespace Bpcg.Planet); sealed record CrustInfo | 중간 | 시작 전 | - |
| `planet/climate.py` | 130 | csharp/Bpcg/Planet/Climate.cs | public static class Bpcg.Planet.Climate (새 형 없음) | 중간 | 시작 전 | - |
| `planet/materials.py` | 261 | csharp/Bpcg/Planet/Materials.cs | public static class Bpcg.Planet.Materials; public sealed class MaterialsInfo (build_mater… | 중간 | 시작 전 | - |
| `planet/ocean.py` | 133 | csharp/Bpcg/Planet/Ocean.cs | public static class Bpcg.Planet.Ocean (새 형 없음) | 중간 | 시작 전 | - |
| `planet/uplift.py` | 145 | csharp/Bpcg/Planet/Uplift.cs | public static class Bpcg.Planet.Uplift (새 형 없음) | 낮음 | 시작 전 | - |
| `geology/__init__.py` | 4 | (없음 — C# 파일 불필요) | 없음 (namespace Bpcg.Geology는 Model.cs와 Rocks.cs가 정의) | 낮음 | 시작 전 | - |
| `geology/model.py` | 556 | csharp/Bpcg/Geology/Model.cs | public static class Model; public sealed record Template; public sealed class LayerColumn… | 중간 | 시작 전 | - |
| `geology/rocks.py` | 151 | csharp/Bpcg/Geology/Rocks.cs | public static class Rocks | 낮음 | 시작 전 | - |
| `landscape/__init__.py` | 4 | 없음 (C# 파일 불필요) | 없음 (namespace Bpcg.Landscape 만 쓰임) | 낮음 | 시작 전 | - |
| `landscape/solver.py` | 588 | csharp/Bpcg/Landscape/Solver.cs | public static class Solver; public sealed class SolverResult; public sealed record Solver… | 높음 | 시작 전 | - |
| `landscape/fans.py` | 206 | csharp/Bpcg/Landscape/Fans.cs | public static class Fans; public sealed record FanApex(long Cell, long Receiver, double D… | 중간 | 시작 전 | - |
| `landscape/relief.py` | 154 | csharp/Bpcg/Landscape/Relief.cs | public static class Relief | 낮음 | 시작 전 | - |
| `landscape/warmstart.py` | 127 | csharp/Bpcg/Landscape/Warmstart.cs | public static class Warmstart; public sealed record WarmStartLevel(int[] Shape, double Sp… | 중간 | 시작 전 | - |
| `subsurface/__init__.py` | 4 | 없음 (C# 파일 불필요) | 없음 | 낮음 | 시작 전 | - |
| `subsurface/water.py` | 356 | csharp/Bpcg/Subsurface/Water.cs | public static class Water; public sealed record RoutingInput(int[] Receiver, int[]? Order… | 중간 | 시작 전 | - |
| `subsurface/groundwater.py` | 194 | csharp/Bpcg/Subsurface/Groundwater.cs | public static class Groundwater; public sealed record WaterTableDiag(double ShallowFracti… | 중간 | 시작 전 | - |
| `subsurface/soil.py` | 138 | csharp/Bpcg/Subsurface/Soil.cs | public static class Soil; public sealed record SoilResult(double[] SoilThicknessM, double… | 낮음 | 시작 전 | - |
| `subsurface/caves.py` | 160 | csharp/Bpcg/Subsurface/Caves.cs | public static class Caves; public sealed record CaveLevelsResult(double[][] LevelM, byte[… | 낮음 | 시작 전 | - |
| `metrics/__init__.py` | 4 | (만들지 않음) 네임스페이스 Bpcg.Metrics 는 아래 세 파일이 만듭니다 | 없음 | 낮음 | 시작 전 | - |
| `metrics/drainage.py` | 576 | csharp/Bpcg/Metrics/Drainage.cs | namespace Bpcg.Metrics; public static class Drainage; public sealed record LawConsistency… | 중간 | 시작 전 | - |
| `metrics/hypsometry.py` | 249 | csharp/Bpcg/Metrics/Hypsometry.cs | namespace Bpcg.Metrics; public static class Hypsometry; public sealed record BimodalityRe… | 중간 | 시작 전 | - |
| `metrics/scorecard.py` | 631 | csharp/Bpcg/Metrics/Scorecard.cs | namespace Bpcg.Metrics; public static class Scorecard; public sealed record ScoreEntry(Sc… | 높음 | 시작 전 | - |
| `pipeline.py` | 735 | csharp/Bpcg/Pipeline.cs | namespace Bpcg; public static class Pipeline; public sealed class PlanetState; public sea… | 중간 | 시작 전 | - |
| `hero/__init__.py` | 16 | 없음 (네임스페이스 Bpcg.Hero 가 대신함) | 없음 | 낮음 | 시작 전 | - |
| `hero/domain.py` | 200 | csharp/Bpcg/Hero/Domain.cs | public static class Domain; public readonly record struct EdgeHit(string Edge, int Index,… | 낮음 | 시작 전 | - |
| `hero/finder.py` | 257 | csharp/Bpcg/Hero/Finder.cs | public static class Finder; public sealed class HeroSite | 중간 | 시작 전 | - |
| `hero/flat.py` | 270 | csharp/Bpcg/Hero/Flat.cs | public static class Flat | 중간 | 시작 전 | - |
| `hero/refine.py` | 267 | csharp/Bpcg/Hero/Refine.cs | public static class Refine; public sealed record HeroBoundaryResult (Python dict 키 순서의 속성… | 중간 | 시작 전 | - |
| `volume/__init__.py` | 12 | 없음 (파일 불필요) | 없음 (namespace Bpcg.Volume 자체) | 낮음 | 시작 전 | - |
| `volume/sample.py` | 1044 | csharp/Bpcg/Volume/Sample.cs | public static class Sample (상수·커널·곡선 함수); public sealed class HeroVolume; public sealed c… | 높음 | 시작 전 | - |
| `volume/slices.py` | 95 | csharp/Bpcg/Volume/Slices.cs | public static class Slices; public sealed record RgbImage(int Height, int Width, byte[] D… | 낮음 | 시작 전 | - |
| `bake/__init__.py` | 4 | 없음 (namespace Bpcg.Bake 만 씀) | 없음 | 낮음 | 시작 전 | - |
| `bake/bundle.py` | 397 | csharp/Bpcg/Bake/Bundle.cs | public static class Bundles (모듈 함수; class Bundle 과 이름이 겹쳐 복수형 제안), public sealed class Bu… | 중간 | 시작 전 | - |
| `bake/corridor.py` | 551 | csharp/Bpcg/Bake/Corridor.cs | public static class Corridor, public sealed record CorridorChoice (choose_corridor 반환 dic… | 중간 | 시작 전 | - |
| `bake/heightmap.py` | 151 | csharp/Bpcg/Bake/Heightmap.cs | public static class Heightmap | 낮음 | 시작 전 | - |
| `bake/mesh.py` | 231 | csharp/Bpcg/Bake/Mesh.cs | public static class Mesh, public sealed record EngineFrame, public readonly record struct… | 높음 | 시작 전 | - |
| `bake/textures.py` | 133 | csharp/Bpcg/Bake/Textures.cs (+ 생성 파일 csharp/Bpcg/Bake/Colo… | public static class Textures, public sealed class Colormap (matplotlib Colormap.__call__… | 중간 | 시작 전 | - |
| `bake/detail.py` | 504 | csharp/Bpcg/Bake/Detail.cs | public static class Detail (namespace Bpcg.Bake) + 중첩 sealed record Settings, FractalDiag… | 높음 | 시작 전 | - |
| `bake/globe.py` | 1089 | csharp/Bpcg/Bake/Globe.cs | public static class Globe (namespace Bpcg.Bake) + 중첩 internal sealed class Resampler; sea… | 중간 | 시작 전 | - |
| `bake/globe_text.py` | 213 | csharp/Bpcg/Bake/GlobeText.cs | public static class GlobeText (namespace Bpcg.Bake) + sealed record Category(int Id, stri… | 낮음 | 시작 전 | - |
| `cli.py` | 370 | csharp/Bpcg.Cli/Cli.cs (+ csharp/Bpcg.Cli/Program.cs) | namespace Bpcg.Cli; public static class Commands (cli.py 함수들); static class Program (Main… | 중간 | 시작 전 | - |

합계 60개 모듈, 14,547줄입니다.

Python 모듈과 1:1이 아닌 새 파일입니다.

| C# 파일 | 하는 일 | 상태 |
|---|---|---|
| `csharp/Bpcg/IO/Npy.cs`, `Npz.cs` | .npy·.npz 읽기·쓰기 (Python studio·분석 스크립트가 그대로 읽게), CRC-32 | 대조 통과(np.save·np.savez 와 바이트 일치) |
| `csharp/Bpcg/IO/PyJson.cs` | Python `json.dumps`와 같은 글자 (실수 repr, `sort_keys`, `indent=2`, `ensure_ascii=False`) | 대조 통과(실수 표기 약 2.6만 개, 배치 4가지) |
| `csharp/Bpcg/IO/Toml.cs` | Python `tomllib` 이식 (문서 순서, int64 범위, BOM) | 대조 통과(설정 3벌, `--set` 원문 24개) |
| `csharp/Bpcg/IO/Png.cs`, `Glb.cs` | PNG(zlib·CRC32), glb 쓰기 | 시작 전 |
| `csharp/Bpcg/Numerics/*.cs` | pairwise 합(`NpReduce`), 3성분 선형대수(`LinAlg`), `NpGrid.Linspace` 는 완료. 백분위수, 안정 정렬, KD-tree, ndimage, find_peaks, 보간, brentq, marching cubes, FFT, polyfit 은 쓰는 묶음과 함께 | 일부 완료 (core 대조로 확인) |
| `csharp/Bpcg.Tests/Golden.cs`, `Compare.cs` | golden 사례 읽기, exact·ulp 비교 | 완료 |
| `csharp/golden/export_golden.py` | golden 자료 만들기 (Python) | core·IO 사례 97개 |

## 4. 외부 라이브러리 대체 방식

| Python 기능 | 쓰는 곳 | C# 대체 | 재현 수준 | 근거 |
|---|---|---|---|---|
| `scipy.spatial.cKDTree` (`query`, `query_ball_point`, `query_pairs`) | landscape/fans, hero/finder, bake/globe, volume/sample | `Bpcg.Numerics.KdTree` | 결과 집합·개수는 exact, 순서가 결과에 쓰이는 곳은 정렬로 맞춤 | 4장 KD-tree 절 |
| `scipy.ndimage.gaussian_filter`, `gaussian_filter1d`, `distance_transform_edt` | bake/detail, metrics/hypsometry, volume/sample | `Bpcg.Numerics.NdImage` | 연산 순서를 옮겨 비트 일치를 목표로 하고, 가중치의 exp 때문에 다른 OS는 허용 오차 | 4장 ndimage 절 |
| `scipy.signal.find_peaks`, `RegularGridInterpolator`, `scipy.optimize.brentq` | metrics/hypsometry, landscape/warmstart, hero/flat | `Bpcg.Numerics.Signal`, `RegularGridInterpolator`, `Brent` | 연산 순서를 옮겨 비트 일치를 목표로 함 | 4장 find_peaks 절 |
| `skimage.measure.marching_cubes`, `trimesh` glb 쓰기 | bake/mesh | `Bpcg.Numerics.MarchingCubes`, `Bpcg.IO.Glb` | 꼭짓점·삼각형 배열을 float32로 비교 | 4장 marching cubes 절 |
| `np.fft`, `np.polyfit`, `np.linalg.norm`·`np.cross`·`np.einsum`·`@` | bake/detail, metrics/drainage, 곳곳 | `Bpcg.Numerics.Fft`·`Polyfit`·`LinAlg` | 3성분 연산은 exact(6장 core ②), FFT·polyfit은 단계 2에서 조사 | 4장 끝(비어 있음) |
| `.npy`·`.npz`, `json.dumps`, `tomllib`, matplotlib PNG, `.bin` | bake/*, core/config | `Bpcg.IO` (`Npy`, `Npz`, `PyJson`, `Toml`, `Png`) | 배열·글자 exact, PNG는 풀어 낸 픽셀 exact | 4장 파일 형식 절 |
| matplotlib 색표 | bake/textures | 같은 RGBA 값을 담은 C# 표 | exact | 6장 bake ① |

### KD-tree (scipy.spatial.cKDTree 대체)

#### 결론

- `Bpcg.Numerics.KdTree`는 scipy 1.18.1 `cKDTree`의 만들기와 세 질의(`query`, `query_ball_point`, `query_pairs`)를 소스 그대로 옮긴 복제로 만듭니다. 네 모듈의 호출 위치가 모두 이 하나를 씁니다. 옮기는 범위는 실제로 쓰는 설정뿐입니다. leafsize 16, balanced_tree, compact_nodes, p = 2, eps = 0, boxsize 없음, 차원 m ≤ 3입니다.
- 복제가 필요한 이유는 셋입니다.
    1. k-NN 동점의 승자는 셀 번호가 아니라 scipy의 순회 순서로 정해집니다. tiny 실행의 강 질의(`volume/sample.py:803`)에서 동점이 46행 나왔고, 동점 후보를 바꾸면 강 투영 커널 출력이 46행 모두 달라집니다. '작은 번호가 이김' 규칙은 11행에서 scipy와 다릅니다.
    2. 이 Mac의 scipy 바이너리는 거리 제곱을 FMA(`fmadd`)로 누적합니다. FMA 없이 계산하면 돌려주는 거리의 8~11 %가 마지막 비트에서 다르고, 근접 동점의 순위와 경계 판정이 갈릴 수 있습니다.
    3. 공 질의는 점 거리만으로 판정하지 않고, 상자 거리(FMA 없음)로 먼저 가지를 치거나 통째로 담습니다. 그래서 점을 하나씩 검사하는 단순 구현은 ulp 경계에서 scipy와 다릅니다.
- 이 설계를 순수 Python 복제본(스크래치 `stage0/lib-kdtree/kdreplica.py`)으로 미리 만들어 scipy와 맞대었습니다. 합성 자료와 tiny 실행에서 기록한 실제 호출 25건 모두에서 다음이 완전히 같았습니다. `tree.indices`와 마디 배열, k-NN의 번호와 거리 비트, 공 질의 목록의 순서와 개수, 쌍 목록의 순서입니다.

#### 호출 위치와 하류 의존성

모든 위치가 거리 값을 버리고(`_, idx = ...` 또는 받지 않음), 인자는 모두 기본값입니다.

| 위치 | 트리 자료 | 질의 | 하류가 쓰는 것 | 의존하는 것 | C# |
|---|---|---|---|---|---|
| `landscape/fans.py:77, 85` | 꼭짓점 후보 `pos[cells]` (M×3) | 점 하나 `query_ball_point(p, r2)`, 정렬 안 함 | `suppressed[near] = True` | 집합(경계는 scipy 산술) | `QueryBallPoint` |
| `landscape/fans.py:132, 136` | `graph.pos` (N×3) | 점 하나 `query_ball_point(pa, r_chord)` | 반평면·`ell < radius` 거르기, 서로 다른 번호에 대입, `n_raised` 개수 | 집합 | `QueryBallPoint` |
| `hero/finder.py:135, 139` | `pos`, `pos[is_dry]` (N×3) | `return_length=True, workers=-1` | 개수 → 건조 비율 | 개수 | `QueryBallPointCount` |
| `bake/globe.py:375, 378` | L0 단위 벡터 (N×3) | `query(centers, k=min(4, N), workers=-1)` | `knn[:, 0]`의 범주 값, 순위 순서로 더한 4개 평균 | 순위 순서, 동점, 근접 동점이면 거리 비트 | `Query` |
| `bake/globe.py:414` | 꼭짓점 방향 (6(N+1)²×3) | `query_pairs(r=1e-9, output_type="ndarray")` | `np.minimum.at`로 대표 번호 | i < j인 쌍의 집합 | `QueryPairs` |
| `bake/globe.py:419` | 375행의 트리 | `query(d[uniq], k)` | 4개 평균 | 순위 순서 | `Query` |
| `volume/sample.py:624, 803` | 강 점 (P×2) | `query(q, k=1, distance_upper_bound=reach, workers=-1)` | 가장 가까운 점(없으면 n → −1)의 앞뒤 선분에 투영 | 동점 승자 | `Query` |

- tiny에서 선상지 트리는 히어로 평면에서만 만들어졌습니다. L0는 칸이 반경 3 km보다 커서 후보가 모두 빠지기 때문입니다.
- KD-tree가 Python과 같은 답을 내려면 입력도 비트 단위로 같아야 합니다. finder의 `math.sin`, fans의 구면 `np.sin`, 지구본 칸 중심의 `np.tan`, 강 점을 만드는 Catmull-Rom의 `** 0.5`는 각 모듈 절과 공통 장의 libm 규칙을 따릅니다.

#### scipy 1.18.1의 동작 (소스에서 확인)

- **만들기.**
    - 루트 상자는 `np.amin`, `np.amax(data, 0)`입니다.
    - 점이 16개 이하이면 잎입니다. 그보다 많으면 자기 점들로 상자를 다시 재고, 폭이 가장 큰 차원으로 나눕니다(`>` 비교라 폭이 같으면 앞 차원). 그 차원의 폭이 0이면 16개를 넘어도 잎입니다.
    - `std::nth_element(idx, idx + n/2, …)`로 `split = x[idx[n/2]]`를 정합니다. 그다음 앞 절반만 `std::partition(!(x >= split))`으로 나눕니다. 결과는 왼쪽 {x < split}, 오른쪽 {x ≥ split}입니다.
    - 앞 절반에 split보다 작은 값이 없으면 `split = nextafter(min, +∞)`로 전체를 다시 나눕니다.
    - 마디는 전위 순서로 쌓입니다. 마디 구조와 잎의 점 집합은 값만으로 정해지지만, 잎 안의 순서(`tree.indices`)는 표준 라이브러리 구현에 따라 달라집니다.
- **k-NN(`query_single_point`).**
    - 마디 큐와 이웃 힙은 scipy가 직접 만든 이진 힙입니다. `push`는 `<`로 올리고, `remove`는 `>`로 내리며 두 자식이 같으면 왼쪽 자식을 고릅니다.
    - 안쪽 마디에서 `x[d] < split`이면 less가 가까운 쪽입니다. 먼 쪽은 `min_distance += side² − 옛 side`로 고친 뒤, 그 값이 ub 이하일 때만 큐에 넣습니다.
    - 안쪽 마디의 `min_distance > ub`이면 검색 전체를 멈춥니다. 큐에서 꺼낸 잎은 이 검사 없이 훑습니다.
    - 잎에서는 `d < ub`(엄격)일 때만 넣습니다. 이웃이 k개 차면 ub를 k번째 거리로 줄이므로, 같은 거리면 먼저 만난 점이 이깁니다.
    - 끝에 힙에서 꺼내 거리 오름차순으로 채웁니다. 못 찾은 순위는 번호 n과 거리 +∞입니다. 거리는 `sqrt(d²)`이고, 상한은 제곱해서 씁니다.
- **query_ball_point.**
    - 추적기가 점과 트리 상자 사이의 최소·최대 거리 제곱을 들고 다닙니다(`ub = r·r`, `epsfac = 1`).
    - 순회 규칙은 차례로 넷입니다. `min > ub`이면 버리고, `max < ub`이면 아래 점을 검사 없이 모두 담고, 잎이면 `d ≤ ub`인 점을 담고, 그 밖에는 less 다음 greater로 내려갑니다.
    - push할 때 거리가 처음 최대 거리보다 작으면 `rect_rect_p`로 다시 재므로, 사실상 매번 다시 잽니다.
    - 점 하나를 주면 순회 순서 그대로의 list를 돌려줍니다. 여러 점을 주면 `std::sort`로 정렬한 list를, `return_length`를 주면 intp 개수를 돌려줍니다.
- **query_pairs.**
    - 같은 트리끼리 이중 순회합니다. 같은 마디 쌍에서는 (less, greater)를 한 번만 돌고, 같은 잎에서는 j > i만 봅니다.
    - `d ≤ ub`이면 늘 (작은 번호, 큰 번호)로 바꿔 담습니다. ndarray는 순회 순서의 (P, 2) intp이고, 결과가 없으면 shape (0, 2)입니다.
- **workers=-1.** 질의 행을 스레드마다 나눌 뿐이므로 결과는 스레드 수와 무관합니다.

#### 이 Mac 바이너리의 실수 연산

- 바퀴는 Apple clang 15.0.0(aarch64, SDK 14.5)으로 만들었고 `/usr/lib/libc++.1.dylib`에 링크됩니다. `nth_element`와 `partition`은 헤더 템플릿이라 그 libc++ 코드가 바이너리 안에 있습니다. LLVM release/16.x와 17.x의 `__nth_element`는 서로 같습니다.
- 점 거리는 `fsub` 다음 `fmadd`로 컴파일되었습니다(`query_knn` 0x41944, 공 질의 잎 0x49a2c·0x49a44·0x49a5c). 식으로 쓰면 다음과 같습니다(m ≤ 3).

    ```text
    d_c = data_c − x_c
    s   = fma(d₂, d₂, fma(d₁, d₁, d₀·d₀))
    ```

- 상자 거리(`rect_rect_p`, `interval_interval_p`)와 k-NN의 `min_distance` 갱신은 `fmul`·`fadd`·`fmaxnm`만 씁니다(FMA 없음).
- m ≥ 4에서는 누산기 4개, 그리고 곱과 합이 따로인 벡터 경로가 섞여 위 식이 맞지 않습니다. 우리 호출은 m = 2, 3뿐입니다.
- 리눅스·윈도 x86-64 바퀴는 FMA 명령이 없고 nth_element 구현도 다르므로, 같은 입력에서도 거리 비트와 동점 승자가 다를 수 있습니다(추정, 확인 안 함). 그래서 KD-tree golden은 macOS arm64에서 만든 것만 기준으로 삼습니다.

#### 실험으로 확인한 것

스크립트와 기록은 스크래치 `stage0/lib-kdtree/`에 있습니다(`check_replica.py`, `instrument.py`, `analyze_real.py`).

| 실험 | 결과 |
|---|---|
| k-NN 거리가 sqrt(FMA 누적)인가 (m = 2, 3, 각 20000 질의) | 40000/40000 일치. FMA 없는 식은 18424, 17760만 일치 |
| 상한과 거리가 정확히 같을 때 | 995/995 제외(`<`) |
| 거리가 정확히 r일 때 (3-4-5 정수 점) | 공 질의와 쌍 질의 모두 포함(`≤`) |
| 공 질의 ulp 경계 | FMA로는 안쪽인데 상자 최소 거리(FMA 없음)로 잘린 경우가 212건 중 28건 |
| 격자 중복 자료의 동점 | k = 1 승자가 가장 작은 번호인 경우 77건 중 2건. k = 4가 모두 같은 거리인 행이 번호 오름차순인 경우 200행 중 6행 |
| 점 하나 공 질의의 순서 | 200건 중 2건만 정렬됨. 여러 점과 `return_sorted=True`는 정렬됨 |
| query_pairs ndarray | 모두 i < j. 사전식 정렬이 아니고, set 결과와 같은 집합 |
| workers 1 대 −1 | k-NN과 개수가 완전히 같음 |
| 복제본 대 scipy (합성 자료) | indices, 마디, k-NN 번호와 거리 비트, 상한, 목록 순서, 개수, 쌍 순서 모두 일치 |
| 대조군 | FMA를 빼면 300행 중 184행만 거리 비트가 같음. nth_element를 바꾸면 indices가 다르고 격자 동점 200행이 모두 다름. 마디 배열과 잎 집합은 30/30 같음 |
| tiny 기록 (트리 7개, 질의 25건) | indices 7/7, 공 목록과 개수, 볼륨 k-NN 123457행, 지구본 표본 40000행씩, 꼭짓점 쌍 3084개 순서까지 일치 |
| tiny 동점 | 강 k = 1에서 46행(예: x = −2350 축에 거울 대칭인 강 점 둘, 서로 다른 잎). 지구본 k = 4에서는 동점과 4 ulp 이내 근접 동점 모두 0 |
| tiny 경계 여유 | 건조 비율 4.99e-4, 선상지 1.27e-4(상대). FMA 유무로 포함이 바뀌는 점 0 |
| tiny 꼭짓점 쌍 | 모서리 12×255쌍 + 꼭짓점 8×3쌍. 쌍 안 거리 ≤ 2.3e-16, 서로 다른 점 사이 최소 4.4e-3 |

#### C# 설계

파일은 둘입니다. `csharp/Bpcg/Numerics/KdTree.cs`(공개)와 `csharp/Bpcg/Numerics/LibCxx.cs`(internal)이고, 두 파일 머리에 scipy(BSD-3-Clause)와 LLVM(Apache-2.0 WITH LLVM-exception) 출처 주석을 답니다.

```csharp
namespace Bpcg.Numerics;

/// scipy 1.18.1 cKDTree(data) 복제: leafsize 16, balanced_tree, compact_nodes, p = 2, eps = 0.
public sealed class KdTree
{
    public KdTree(double[] data, int m, int leafSize = 16); // (n, m) 행 우선, 1 ≤ m ≤ 3, 복사
    public int N { get; }
    public int M { get; }
    public ReadOnlySpan<long> Indices { get; }  // tree.indices
    public ReadOnlySpan<double> Mins { get; }   // tree.mins
    public ReadOnlySpan<double> Maxes { get; }  // tree.maxes
    public KdNodeArrays Nodes { get; }          // 전위: SplitDim, Split, StartIdx, EndIdx, Less, Greater

    // query(x, k, distance_upper_bound): (q, k) 행 우선, 못 찾으면 idx = N, dist = +∞
    public (double[] Dist, long[] Idx) Query(double[] x, int k,
        double distanceUpperBound = double.PositiveInfinity);
    // query_ball_point(point, r): 점 하나, 순회 순서 그대로
    public long[] QueryBallPoint(ReadOnlySpan<double> point, double r);
    // query_ball_point(x, r, return_length=True)
    public long[] QueryBallPointCount(double[] x, double r);
    // query_pairs(r, output_type="ndarray"): (P, 2) 행 우선, i < j, 순회 순서
    public long[] QueryPairs(double r);
}
```

구현 규칙은 다음과 같습니다.

1. **만들기.**
    - 재귀로 만들고 마디를 전위 순서의 구조체 배열에 둡니다.
    - `LibCxx.NthElement`와 `LibCxx.Partition`에 비교 `col[a] < col[b]`, 술어 `!(col[a] >= pivot)`을 넘깁니다.
    - `nextafter(x, +∞)`는 `Math.BitIncrement(x)`로 옮깁니다.
    - 상자 갱신은 `a > t ? a : t`, `a < t ? a : t`로 씁니다.
2. **점 거리.** `KdTree.SqDist` 한 함수에서만 `s = Math.FusedMultiplyAdd(d, d, s)`를 씁니다(근거 주석 포함, 승인 필요). 다른 계산은 C++ 문장 순서대로 하며 FMA를 쓰지 않습니다. 해당 계산은 `max(0, max(a, b))²`, `max(c, d)²`의 차원 순서 합, side 거리, `r·r`, 상한 제곱입니다.
3. **k-NN.**
    - scipy 힙을 그대로 옮기고 우선순위만 비교합니다. 이웃 힙의 우선순위는 `−d`입니다.
    - NodeInfo(마디, min_distance, side[m])는 질의마다 다시 쓰는 풀에 둡니다.
    - 출력 거리는 `Math.Sqrt(−priority)`입니다.
4. **공 질의와 쌍 질의.** 추적기는 subnomial 판정까지 옮기고, 순회는 재귀로 하며 less를 먼저 봅니다.
5. **병렬.** 질의 행은 서로 독립이므로 `workers=-1`인 위치는 `Parallel.For`로 나누고(확인 필요), 스레드별 작업 공간을 두어 출력 칸에만 씁니다. 만들기와 쌍 질의는 단일 스레드입니다.
6. **입력 검사.** n ≥ 1, 1 ≤ m ≤ 3, 유한한 자료와 질의, k ≥ 1, r ≥ 0을 확인하고, 어기면 한국어 메시지의 `ArgumentException`을 냅니다. n = 0은 모든 호출 위치가 앞에서 막습니다.

호출 위치는 이렇게 옮깁니다.

- **fans.** `foreach (var j in tree.QueryBallPoint(p, r2)) suppressed[j] = true;`로 씁니다. 136행은 받은 순서 그대로 쓰면 됩니다.
- **finder.** 개수 두 배열을 double로 바꿔 나눕니다.
- **globe.** `Idx[r·k]`를 nearest로 쓰고, 평균은 순위 0..k−1 순서로 더합니다(공통 장의 numpy 합 규칙).
- **globe 쌍.** (i, j)마다 `rep[j] = Math.Min(rep[j], i)`로 처리합니다.
- **volume.** `idx == N`이면 −1로 바꿉니다.

#### 대조 시험

`export_golden.py`가 `out/golden/numerics/kdtree_*.npz`를 만들고, meta에 scipy 버전, `platform.machine()`, `sys.platform`, 컴파일러를 적습니다. macOS arm64가 아니면 KD-tree 부분을 만들지 않습니다. 모든 비교는 완전 일치(정수)와 비트 일치(실수)입니다.

| 묶음 | 입력 | 비교하는 출력 |
|---|---|---|
| `build` | 균일 m = 2, 3 (n = 1~3000), 격자 {0..3}³ 중복, 같은 점 50개, 상수 열, 단위 구+중복 | indices, 전위 마디 배열, mins, maxes |
| `query` | 위 자료 + 균일·자료 점·반정수 질의, k = 4(상한 +∞), k = 1(유한 상한) | dist(비트), idx(동점 순서 포함, 못 찾음 = n) |
| `fma_sentinel` | sqrt(FMA 누적) ≠ sqrt(곱·합 따로)인 행만 (m = 2, 3) | dist 비트. FMA 없는 식이 틀린다는 것도 시험 안에서 확인 |
| `ball` | 위 자료 + 여러 반경, 3-4-5에서 r = 5, ulp 경계 400건(점 1개·2개 트리) | 점 하나 목록(순서), 개수 |
| `pairs` | 격자 r = 1.0, 균일 r = 8, 구면+중복 r = 1e-9, 같은 점 r = 0, 빈 결과 | (P, 2) 순서 |
| `tiny_river` | 강 점 2763×2, 상한 132.2047348022461, 동점 46행 + 표본 2000행 | idx, dist |
| `tiny_globe` | L0 단위 벡터 6144×3, 칸 중심 4096행, 면당 32칸 꼭짓점 | k = 4 idx·dist, 쌍 |
| `tiny_ball` | L0 pos와 후보 937개(chord 199991.7878251936), 건조 663개, 히어로 pos와 꼭짓점(r = 3000, 6000) | 개수, 목록(순서) |

크기는 묶음마다 0.1~0.5 MB입니다. golden은 out/golden(git 제외)에 두고, 손으로 만든 작은 사례만 커밋 후보로 둡니다.

#### 결정이 필요한 것

- `KdTree.SqDist`에만 `Math.FusedMultiplyAdd`를 쓰는 예외를 둘지 정해야 합니다. 지시서는 FMA를 금지합니다.
- `workers=-1` 질의에 `Parallel.For`를 써도 되는지 정해야 합니다. 지시서는 'prange만'이라고 합니다.
- KD-tree golden을 macOS arm64에서만 만드는 규칙을 export_golden.py에 강제할지 정해야 합니다.
- 출력 번호형을 `long[]`(numpy intp)으로 둘지 정해야 합니다.
- scipy와 libc++ 코드를 옮긴 것의 라이선스 고지를 어디에 둘지 정해야 합니다.
- 진행 기록에 적을 Python 쪽 의심 지점이 있습니다. 강 질의 결과가 scipy 바퀴의 플랫폼에 따라 마지막 비트에서 달라질 수 있습니다.
- scipy를 올릴 때 KD-tree golden 재검증을 필수 절차로 둘지 정해야 합니다.

### ndimage (gaussian_filter·gaussian_filter1d·distance_transform_edt 대체)

scipy 1.18.1의 세 함수를 `Bpcg.Numerics.NdImage`로 직접 구현합니다. 기준은 설치본 휠 `scipy-1.18.1-cp313-cp313-macosx_14_0_arm64.whl`(Apple clang 15.0.0 빌드, uv.lock 해시 일치)입니다. 단계 0에서 순수 Python 복제본(`math.fma` 사용)을 만들어 scipy와 비트 단위로 대조했습니다. 아래 규칙을 따르면 모든 호출 위치의 dtype·모양·모드·σ와 실제 tiny 자료에서 비트까지 같습니다.

결론은 세 가지입니다.

1. gaussian 계열: macOS arm64 휠의 C 코드는 반지름에 따라 일부 짝을 FMA로 계산합니다. 비트 일치하려면 그 자리에 `Math.FusedMultiplyAdd`가 필요하며, 이는 지시의 FMA 금지와 충돌하므로 결정이 필요합니다.
2. 무게의 `exp`: 이 기계에서 numpy와 libm의 값이 같으므로, macOS에서는 `Math.Exp`로 비트 일치를 기대합니다(.NET이 없어 미검증).
3. distance_transform_edt: 정수 산술만 쓰는 정확한 알고리즘이라 플랫폼과 무관하게 비트까지 같습니다.

#### 호출 위치

| 위치 | 호출 | 입력 (tiny · laptop · lab) | σ → r | 뒤 처리 |
|---|---|---|---|---|
| bake/detail.py:378 | `gaussian_filter(surf, SLOPE_SMOOTH_M / dx, mode="nearest")` | float64 (ny, nx) 51×151 · 501×3001 · 1001×8001 | 1.25→5 · 5→20 · 10→40 | `np.gradient` → `np.hypot` → `clip(S/slope_ref, 0, 1)` |
| bake/detail.py:386 | `gaussian_filter(np.where(soil, soil_factor, 1.0), SOIL_SMOOTH_M / dx, mode="nearest")` | float64, 같은 모양 | 0.75→3 · 3→12 · 6→24 | m_soil |
| bake/detail.py:390 | `distance_transform_edt(~wet) * dx` | bool, `wet.any()`일 때만 | — | `clip((d − water_margin_m)/20, 0, 1)`, 물 칸 0 |
| bake/detail.py:403 | `distance_transform_edt(~mask) * float(voxel_m)` | bool (동굴 입구, 원래 웅덩이), `mask.any()`일 때만 | — | `clip((d − margin)/ramp, 0, 1)`, mask 칸 0 |
| metrics/hypsometry.py:125 | `gaussian_filter1d(frac, smooth_m / bin_m, mode="constant")` | float64 (nb,), 행성 수준 z_mean_m, tiny 115칸 | 2.5→10 | `max` → `find_peaks(prominence = 0.05·top)` → 안정 argsort |
| volume/sample.py:562 | `distance_transform_edt(~is_body) * dx − 0.5 * dx` | bool 히어로 격자 64² · 1280² · 1600², `is_body.any()`일 때만 | — | `np.maximum(·, 0)` → 쌍선형(numba) → `m > 0` 분기 |

σ와 dx는 모두 Python float(double)입니다. detail의 m은 디테일 z → fill_depressions → heightmap_detail.bin(float32)과 corridor manifest.json의 detail 메타(mean_weight, rms_m 등 float64 전체 자릿수)로 이어집니다. 따라서 가우스 출력의 ulp 차이가 파일 문자열까지 번질 수 있습니다.

#### gaussian 무게 (_filters.py:656–666, 747, 753)

1. 반지름은 `r = (int)(truncate * sigma + 0.5)`이고 truncate는 4.0입니다. 호출 위치에서는 모두 r = 4σ입니다.
2. `sigma2 = sigma * sigma`를 구하고, `c = -0.5 / sigma2`를 구합니다.
3. x = −r..r마다 `phi[i] = exp(c * (double)(x * x))`입니다(x²는 정수로 정확합니다).
4. `s = phi.sum()`은 numpy 합 = `0.0 + pairwise_sum`입니다. n = 2r+1 < 8이면 앞에서부터 차례로 더하고, 8 ≤ n ≤ 128이면 누산기 8개와 `((r0+r1)+(r2+r3))+((r4+r5)+(r6+r7))`로 묶은 뒤 나머지를 차례로 더합니다. 더 길면 반으로 나눕니다(공통 장 NpReduce.Sum).
5. `w[i] = phi[i] / s`로 나눕니다(역수를 곱하지 않습니다). 마지막의 `[::-1]` 뒤집기는 phi가 정확히 대칭이라 비트를 바꾸지 않습니다.

그럴듯한 다른 공식은 호출 위치 σ에서 실제로 다른 비트를 냅니다. 아래 표의 수는 무게 n개 가운데 scipy와 다른 개수입니다.

| 위치 | n | 차례 합 | `exp(-x²/(2σ²))` | `phi·(1/s)` | `exp(-0.5(x/σ)²)` |
|---|---|---|---|---|---|
| tiny 경사 σ=1.25 | 11 | 10 | 0 | 8 | 7 |
| tiny 흙 σ=0.75 | 7 | 0 | 0 | 4 | 0 |
| laptop 경사 σ=5 | 41 | 36 | 0 | 20 | 14 |
| laptop 흙 σ=3 | 25 | 17 | 4 | 18 | 6 |
| lab 경사 σ=10 | 81 | 69 | 0 | 42 | 71 |
| lab 흙 σ=6 | 49 | 0 | 16 | 12 | 12 |
| hypsometry σ=2.5 | 21 | 0 | 0 | 6 | 13 |

**exp 의존.** 이 기계에서 `np.exp`(float64)는 macOS libm `exp`와 220만 표본에서 비트까지 같습니다. 호출 위치 σ 7개의 exp 인자 121개에서는 libm 값이 정확 반올림 값과도 같습니다. 그러나 [−8.5, 0] 구간의 무작위 인자에서는 0.16%가 정확 반올림과 다르므로, glibc나 UCRT는 일부 σ에서 1 ulp 다른 무게를 낼 수 있습니다(미검증). exp 하나가 1 ulp 바뀌면 출력 대부분이 최대 4~5 ulp 바뀝니다. 실제 tiny 지표 σ=1.25에서는 7,701칸 중 6,794칸이 바뀌었습니다.

#### correlate1d 경로 (ni_filters.c `NI_Correlate1D`, ni_support.c)

- **줄 버퍼.** 입력 줄을 `(double)`로 복사합니다(float32와 정수는 정확히 넓어집니다). 그다음 양쪽에 r칸씩 덧붙입니다. `nearest`는 첫 값과 끝 값을 r번 반복하고(줄 길이가 r보다 짧아도 같습니다), `constant`는 cval = 0.0으로 채웁니다. `correlate1d`는 `_extend_mode_to_code(mode)`를 is_filter 없이 불러, nearest는 0, constant는 4를 넘깁니다.
- **대칭 판정.** 길이가 홀수이고 `fabs(w[c+k] − w[c−k]) <= DBL_EPSILON`이면 대칭 경로로 갑니다. 가우스 무게는 정확히 대칭이므로 늘 이 경로입니다.
- **C 원문의 계산 순서.** `acc = x[i]·w[0]`으로 시작합니다. 그 뒤 k = r, r−1, …, 1의 순서로(바깥 짝부터) `acc += (x[i−k] + x[i+k])·w[−k]`를 더합니다.
- **출력.** `(type)acc`로 C 형변환합니다. float64는 그대로, float32는 최근접 짝수 반올림, 정수는 0 쪽 자름입니다.
- **gaussian_filter.** 출력 배열을 입력 dtype으로 만들고 축 0 → 축 1 순서로 1차원 필터를 겹칩니다. 둘째 패스는 같은 배열에 덮어쓰지만, 줄을 먼저 버퍼로 읽으므로 결과는 따로 쓴 것과 같습니다. 중간값이 출력 dtype으로 저장되므로 float32 입력은 패스마다 float32로 반올림됩니다. σ ≤ 1e−15인 축은 건너뜁니다. 축 순서를 바꾸면 40×30에서 1,200칸 중 887칸이 다릅니다.
- **gaussian_filter1d.** 축을 건너뛰지 않습니다. σ² = 0이면 Python은 ZeroDivisionError를, r < 0이면 ValueError를 냅니다.
- **NaN과 inf.** IEEE 규칙대로 반지름 안으로 번지며, 따로 처리하지 않습니다.

#### FMA 축약: 휠마다 산술이 다릅니다

C 원문 `oline[ll] += (iline[jj] + iline[-jj]) * fw[jj]`는 Apple clang 기본값(`-ffp-contract=on`)에서 FMA로 융합될 수 있습니다. 설치본 `_nd_image.cpython-313-darwin.so`의 `_NI_Correlate1D`(0x7a1c)를 objdump로 보면 대칭 경로가 둘로 갈립니다.

- **r < 10** (0x7d08 `cmp x26, #0xa`): 스칼라 루프가 모든 짝을 `fadd` 다음 `fmadd`로 계산합니다. 즉 `acc = fma(x[i−k] + x[i+k], w, acc)`입니다.
- **r ≥ 10**: 바깥 `r − r % 8`개 짝은 8개씩 벡터 루프에서 `fadd.2d`(짝 합)와 `fmul.2d`(곱)로 구한 뒤, `fadd`로 차례대로 더합니다(융합 없이 순서 유지). 남은 안쪽 `r % 8`개 짝은 스칼라 `fmadd`입니다(0x8030–0x8050).
- 출력 줄이 입력 줄이나 무게와 겹치면 전부 fmadd 경로로 갑니다. 두 줄 버퍼는 따로 malloc하므로 이 경우는 일어나지 않습니다.

| 휠 (cp313) | 대칭 루프 기계어 | 정책 |
|---|---|---|
| macosx_14_0_arm64 (설치본) | 위의 두 갈래 | MacArm64 |
| manylinux x86_64 (gcc) | `addsd` · `mulsd` · `addsd` | None (모든 짝 비융합) |
| manylinux aarch64 (gcc) | `fadd` · `fmul` · `fadd` | None |
| win_amd64 | `addsd` · `mulsd` · `addsd`, DLL 전체에 FMA 명령 없음 | None |

| 호출 위치 r | MacArm64의 비융합 짝 | FMA 짝 |
|---|---|---|
| tiny 5, 3 | 0 | 전부 |
| laptop 20 / 12 | 16 / 8 | 4 / 4 |
| lab 40 / 24 | 전부 | 0 (None과 같음) |
| hypsometry 10 | 8 | 2 |

**검증.** `math.fma` 복제본은 r = 0..69, 79–81, 95–97, 127–129, 160, 200에서 두 모드 모두 비트까지 맞았습니다. None 정책은 r ≤ 1이거나 (r % 8 = 0이고 r ≥ 16)일 때만 맞습니다.

**차이의 크기.** 호출 위치 σ에서 None과 MacArm64의 차이는 최대 3 ulp(상대 5.5e−16)입니다. tiny 굽기에 None을 넣으면 float64 중간값이 이만큼 달라집니다.

- 디테일 계수 m: 7,701칸 중 318칸(np.gradient 상쇄로 상대 최대 4.3e−14)
- 채우기 전 z: 52칸
- 채운 z: 48칸

그러나 n_filled, float32 heightmap_detail.bin, manifest의 detail 메타는 같았습니다. hypsometry 실제 자료(115칸)에서는 14칸이 달랐지만 봉우리 위치는 같았고, 돌출도 하나의 마지막 비트만 달랐습니다. `Math.FusedMultiplyAdd`는 IEEE 단일 반올림이라 arm64 `fmadd`, x64 FMA3, CRT 대체가 같은 비트를 냅니다. 따라서 C#에서 MacArm64 정책을 쓰면 모든 플랫폼에서 macOS Python과 같은 값이 나옵니다.

#### distance_transform_edt (_morphology.py:2568–2602, ni_morphology.c)

1. `np.where(input, 1, 0).astype(int8)`로 바꿉니다. 0이 아닌 값(NaN 포함)이 전경이고, 0이 배경입니다.
2. ft (2, ny, nx) int32를 0으로 시작합니다. 축 0 패스에서는 열마다 배경 칸에 (j, i)를, 전경 칸의 성분 0에 −1을 넣고 `_VoronoiFT`를 돌립니다. 이어 축 1 패스에서 행마다 `_VoronoiFT`를 돌립니다(Maurer 2003).
3. `_VoronoiFT`는 성분 0 ≥ 0인 후보를 스택에 쌓으며, `c·vR − b·uR − a·wR − a·b·c <= 0`이면 제거를 멈춥니다. 값은 double이지만 모두 2^53보다 작은 정수입니다(한 변 약 2×10^5칸까지). 그래서 정확하며 FMA와도 무관합니다. 배정 단계는 `delta1 <= delta2`이면 앞 후보를 남기므로, 거리가 같으면 앞 후보가 이깁니다.
4. `dt = ft − indices` → float64 → 제곱 → `add.reduce(axis=0)` → `np.sqrt`이고 반환은 float64입니다. 이는 `Math.Sqrt((double)(dy² + dx²))`와 비트까지 같습니다.
5. 300개 무작위 마스크에서 전수 탐색과, laptop·lab 크기(최대 d² = 6.5×10^7)에서 KD-tree 최근접과 비트까지 같았습니다. 따라서 정확한 EDT라면 어느 알고리즘이든 거리가 같고, 같은 알고리즘을 옮기면 특징 색인까지 같습니다(검증).
6. 배경이 하나도 없으면 scipy는 특징 (−1, 0)을 두고 `sqrt((j+1)² + i²)`라는 가짜 거리를 냅니다. 호출 위치는 모두 `.any()`로 이 경우를 막습니다.
7. `edt(x) * dx`는 `edt(x, sampling=dx)`와 다릅니다. dx = 25·100에서는 약 19% 칸이 다르고, 2의 거듭제곱인 dx에서는 같습니다.

#### C# API 제안 (`Bpcg.Numerics.NdImage`)

```csharp
namespace Bpcg.Numerics;

public enum NdBoundaryMode { Nearest, Constant }   // scipy 'nearest' / 'constant'

/// scipy 1.18.1 휠의 NI_Correlate1D 대칭 루프 FMA 축약 방식 (디스어셈블로 확인).
public enum ScipyFmaProfile { MacArm64, None }

public static class NdImage
{
    public const double DefaultTruncate = 4.0;
    public static int GaussianRadius(double sigma, double truncate = DefaultTruncate);
    public static double[] GaussianKernel1D(double sigma, int radius);      // order 0
    public static double[] GaussianFilter1D(ReadOnlySpan<double> input, double sigma,
        NdBoundaryMode mode, double cval = 0.0, double truncate = DefaultTruncate,
        ScipyFmaProfile fma = ScipyFmaProfile.MacArm64);
    public static double[] GaussianFilter2D(ReadOnlySpan<double> input, int ny, int nx,
        double sigma, NdBoundaryMode mode, double cval = 0.0,
        double truncate = DefaultTruncate, ScipyFmaProfile fma = ScipyFmaProfile.MacArm64);
    public static double[] DistanceTransformEdt(ReadOnlySpan<bool> input, int ny, int nx);
    public static int[] EuclideanFeatureTransform(ReadOnlySpan<bool> input, int ny, int nx);
    internal static void Correlate1DSymmetric(ReadOnlySpan<double> ext, int n,
        ReadOnlySpan<double> w, ScipyFmaProfile fma, Span<double> output);
}
```

핵심 루프는 아래와 같습니다. ext는 덧붙인 줄(길이 n + 2r)이고, w는 뒤집은 무게(길이 2r+1, 가운데 w[r])입니다.

```csharp
int nv = fma == ScipyFmaProfile.None ? r : (r >= 10 ? r - r % 8 : 0);
for (int i = 0; i < n; i++)
{
    int c = i + r;
    double acc = ext[c] * w[r];
    int k = r;
    for (; k > r - nv; k--) acc += (ext[c - k] + ext[c + k]) * w[r - k];  // 곱·합 따로 반올림
    for (; k >= 1; k--) acc = Math.FusedMultiplyAdd(ext[c - k] + ext[c + k], w[r - k], acc);
    output[i] = acc;
}
```

구현 규칙은 다음과 같습니다.

- GaussianFilter2D는 `!(sigma > 1e-15)`이면 입력 복사본을 돌려줍니다. 그렇지 않으면 열(축 0)을 먼저, 행(축 1)을 다음에 처리합니다.
- 무게는 double로 계산합니다(`MathF` 금지). 합은 `NpReduce.Sum`으로 구합니다.
- EDT는 `_ComputeFT`/`_VoronoiFT`를 그대로 옮깁니다. fy = −1 표시, fx = 0 초기화, long 산술을 씁니다.
- 순차로 구현합니다(scipy도 순차이고, lab 크기에서 0.1~0.2 s입니다).
- float32, 다른 모드, 3-D는 NotSupportedException을 냅니다.

#### 호출 위치별 C# 대응

- **detail.py:378, :386.** `NdImage.GaussianFilter2D(surf, ny, nx, SlopeSmoothM / dx, NdBoundaryMode.Nearest)`를 씁니다. 흙 입력은 `soil ? soilFactor : 1.0`으로 채운 double[]입니다.
- **detail.py:390, :403.** `NdImage.DistanceTransformEdt(notMask, ny, nx)`의 각 값에 `* dx`를 곱합니다(sqrt 뒤에 곱하며, sampling으로 바꾸지 않습니다). `.any()` 검사는 호출 위치에 그대로 둡니다.
- **sample.py:562.** `d[i] * dx - 0.5 * dx` 순서를 지키고, 이어 `Math.Max(·, 0.0)`을 씁니다. 이 값은 −0.0이나 NaN이 될 수 없으므로 `np.maximum`과 같습니다.
- **hypsometry.py:125.** `NdImage.GaussianFilter1D(frac, smoothM / binM, NdBoundaryMode.Constant)`를 씁니다.

#### golden 대조 시험

export_golden.py는 메타에 `sys.platform`, `platform.machine()`, numpy·scipy 버전, `scipy-*.dist-info/WHEEL`의 Tag를 적습니다. C# 시험은 Tag로 정책을 고릅니다. macosx_14_0_arm64이면 MacArm64, manylinux x86_64·aarch64 또는 win_amd64이면 None이고, 그 밖의 휠이면 실패합니다.

| 대상 | 입력 | 출력 | 크기 | 비교 |
|---|---|---|---|---|
| GaussianKernel1D | σ 16개 (r = 0, 1, 3, 5, 8, 9, 10, 12, 15, 16, 17, 20, 24, 31, 40, 50) | 무게 (2r+1,) | 4 KB | macOS 비트 일치, 그 밖은 호출 위치 σ 비트 일치·나머지 ≤ 1 ulp |
| Correlate1DSymmetric | golden 무게 + 무작위 줄 97칸 × 두 모드 | `ndimage.correlate1d` 결과 | 50 KB | 모든 플랫폼 비트 일치 |
| GaussianFilter1D | tiny 실제 frac 115칸, 합성 길이 1·2·3·9·21·301, σ 2.5 | dens | 10 KB | macOS 비트 일치, 그 밖 ≤ 8 ulp |
| GaussianFilter2D | 실제 회랑 지표 51×151 σ 1.25, 합성 흙 계수 σ 0.75, 31×23·61×47에서 r = 3..40, 짧은 모양 1×1·1×7·7×1·3×2 | 필터 결과 | 250 KB | macOS 비트 일치, 그 밖 ≤ 8 ulp |
| DistanceTransformEdt, EuclideanFeatureTransform | 무작위 마스크 40개, 동률 무늬, 전부 배경, 전부 전경, 실제 ~wet·~sink·~cave·~is_lake | 거리 float64, 색인 int32 | 300 KB | 모든 플랫폼 비트·완전 일치 |

'그 밖 ≤ 8 ulp'의 근거는 exp 1 ulp 교란이 출력을 최대 5 ulp 바꾼다는 측정입니다. 무게 golden이 비트까지 같으면 비트 일치를 요구합니다.

#### 검증 기록

- **복제본.** `scratchpad/stage0/lib-ndimage/replica.py`(numpy pairwise 합, 무게, FMA 정책별 대칭 상관, Maurer EDT)입니다. 이 문서의 수치는 모두 이 복제본과 scipy의 비트 대조에서 나왔습니다.
- **디스어셈블리.** `correlate1d.asm`(macOS), `linux_x86_64_correlate1d.asm`, `linux_aarch64_correlate1d.asm`, `win_amd64_all.asm`입니다. 다른 휠은 uv.lock의 sha256과 같은 파일을 scratch에 받아 objdump만 했습니다(설치 없음).
- **실제 자료.** tiny 굽기를 scratch로 다시 돌려 detail의 가우스·EDT 입력을 잡았습니다. scipy로 다시 돌린 결과는 기존 out/tiny_check와 비트까지 같았습니다.
- **공통 위험.** 이 Mac의 numpy C 코드도 FMA로 축약됩니다(np.interp가 20,000개 모두 융합 복제본과 일치). 다른 라이브러리 대체도 디스어셈블로 확인해야 합니다.

### find_peaks·RegularGridInterpolator·brentq 대체

이 절은 scipy 1.18.1(설치 휠 `cp313-cp313-macosx_14_0_arm64`, uv.lock 고정)의 `scipy.signal.find_peaks`(prominence 경로), `scipy.interpolate.RegularGridInterpolator`(linear, `bounds_error=False`, `fill_value=None`), `scipy.optimize.brentq`를 C#으로 직접 구현하는 방법을 정합니다. 원문은 GitHub `v1.18.1` 태그의 `_peak_finding_utils.pyx`, `_rgi_cython.pyx`, `_poly_common.pxi`, `Zeros/brentq.c`, `_zerosmodule.c`를 읽었고, 설치된 확장 모듈의 기계어를 `otool -tvV`로 확인했습니다. 세 함수 모두 순수 Python 복제를 만들어 scipy와 비트 단위로 맞추었습니다.

결론은 둘입니다. 첫째, find_peaks는 산술이 뺄셈 하나뿐이라 원문 반복문을 그대로 옮기면 비트 단위로 같습니다. 둘째, macOS arm64 휠의 `evaluate_linear_2d`와 `brentq`는 컴파일러(clang으로 보임)가 한 식 안의 곱셈과 덧셈을 FMA(곱셈·덧셈을 한 번만 반올림하는 명령)로 합쳐 두었습니다. 그래서 C#도 그 자리에서만 `Math.FusedMultiplyAdd`를 써야 비트 단위로 같고, 이는 지시 원문의 'Math.FusedMultiplyAdd를 쓰지 않는다'는 규칙과 부딪힙니다(아래 '결정이 필요한 점').

#### 호출 위치와 실제 경로

| 위치 | 호출 | 입력 | scipy 안의 실제 경로 | 결과가 닿는 곳 |
|---|---|---|---|---|
| `metrics/hypsometry.py:131` | `find_peaks(padded, prominence=min_prominence * top)` | float64 (B+2,), 양 끝 0.0 덧댐, pmin = 0.05·top | `_local_maxima_1d` → `_peak_prominences(wlen=-1)` → `pmin <= prominence` | `scorecard.json`과 manifest의 `*.hypsometry_bimodal` |
| `landscape/warmstart.py:117` | `RGI((ys[::-1], xs), img, bounds_error=False, fill_value=None)` | 두 축 각 16 이상(오름차순), values float64 (nyc, nxc) 행 뒤집은 쓰기 가능 view, xi (N, 2) float64 | Cython `find_indices` + `evaluate_linear_2d` | 2단계 솔버 시작 지형 `z_init` (행성·평면 히어로) |
| `hero/flat.py:124` | `RGI((ys, xs), img, bounds_error=False, fill_value=None)` | 같음, n_c = max(16, n/4) | 같음 | `z_pre` → `exhumation_m` → 지질 기둥 |
| `hero/flat.py:91` | `brentq(gap, lo, hi, xtol=1e-12, rtol=1e-12)` | Python float, maxiter 기본 100 | `_zeros._brentq` → `Zeros/brentq.c` | `p_value` → `precip_m_per_yr`, manifest `meta.diag.boundary.precip_m_per_yr` |

`src/` 전체를 grep한 결과 다른 사용처는 없습니다. tiny에서 보간은 16² → 64²이고, laptop 히어로(1280²)에서는 먼저 풀기가 80² → 320², 320² → 1280² 두 번, 평면 미리 풀기가 320² → 1280² 한 번입니다.

#### find_peaks (prominence 경로)

1. `x`는 C 연속 float64로 바뀝니다(`_arg_x_as_expected`). 조건은 prominence 하나이고, `wlen=None`은 -1이 되어 신호 전체를 봅니다.
2. `_local_maxima_1d`: i = 1부터 i < n-1 동안 `x[i-1] < x[i]`이면 i_ahead = i+1에서 시작해 `i_ahead < n-1`이고 `x[i_ahead] == x[i]`인 동안 나아갑니다. `x[i_ahead] < x[i]`이면 봉우리이고 left = i, right = i_ahead-1, mid = (left+right)/2(내림이라 짝수 폭이면 왼쪽 가운데)이며 i = i_ahead로 건너뜁니다. 그 뒤 i를 1 늘립니다. 첫 표본과 마지막 표본, 끝까지 이어지는 평탄면은 봉우리가 아닙니다. NaN은 비교가 모두 거짓이라 봉우리가 되지 못하고, 바로 옆이 NaN인 평탄면도 봉우리가 되지 못합니다.
3. `_peak_prominences`: 봉우리에서 왼쪽으로 i >= 0이고 `x[i] <= x[peak]`인 동안 가며 `x[i] < left_min`일 때만 left_min과 left_base를 바꿉니다(같은 최솟값이 여럿이면 봉우리에 가까운 쪽). 오른쪽도 같고, prominence = x[peak] - max(left_min, right_min)입니다. 바닥 찾기는 NaN에서 멈춥니다. 봉우리 양옆이 엄격히 낮으므로 prominence는 늘 0보다 크고 PeakPropertyWarning은 나지 않습니다.
4. 거르기는 `pmin <= prominence`(경계 포함) 하나이고, 돌려주는 props는 prominences, left_bases, right_bases입니다. 이 모듈의 기계어에서 FMA는 쓰지 않는 `_peak_widths` 안의 1개뿐입니다.
5. 호출부(hypsometry.py:130-141)는 `idx - 1`로 덧댄 칸을 빼고, `np.argsort(-prominences, kind="stable")[:2]`로 돌출도가 큰 둘(같으면 앞 번호)을 고른 뒤 칸 중심 `0.5·(edges[:-1] + edges[1:])`를 오름차순으로 정렬합니다. `n_peaks`는 거른 뒤 봉우리 수입니다.
6. 입력 연쇄: `dens`는 `np.histogram(z, edges, weights=area)`와 `gaussian_filter1d(frac, 2.5, mode="constant")`에서 옵니다. 명시 경계 히스토그램은 65536개 블록마다 기본 argsort(불안정)로 정렬해 가중치를 `cumsum`하고 `cw[searchsorted]`를 누적한 뒤 `np.diff`합니다. 이 둘은 다른 항목의 대체이므로, find_peaks 대조 시험은 Python이 만든 `padded`를 그대로 받아 연쇄와 떼어 봅니다. 평탄 판정이 `==`라 dens의 마지막 자리에 민감하지만, bimodality의 출력은 이산 값(칸 중심, 간격, 합격, 개수)입니다.

C#은 위 반복문을 그대로 옮깁니다. Cython의 `max(a, b)`는 `b > a ? b : a`로 쓰는데, 이 입력에서는 NaN이 바닥 최솟값이 될 수 없고 ±0 동점도 결과를 바꾸지 않아 어느 꼴이든 같습니다.

#### RegularGridInterpolator (linear, 범위 밖 외삽)

- 경로(_rgi.py:448-462): 2차원이고 values가 2차원·쓰기 가능·float64·native 바이트 순서면 Cython 빠른 경로 `evaluate_linear_2d`가 돕니다. 두 호출 모두 해당합니다(붙잡은 values는 float64, writeable). values가 읽기 전용이거나 float32면 numpy 느린 경로 `_evaluate_linear`가 가중치를 먼저 곱하므로 결과가 다릅니다(읽기 전용 실험에서 20,000점 중 7,851점). C#은 빠른 경로만 옮깁니다.
- 축 검사(`_check_points`): 각 축을 float64로 바꾸고, 엄격 오름차순이 아니면 엄격 내림차순인지 보고 축과 values를 그 축으로 뒤집습니다(`np.flip`, 산술 없음). 둘 다 아니면 ValueError입니다. warmstart는 북→남 내림차순 ys를 `ys[::-1]`로, values를 `res.z.reshape(nyc, nxc)[::-1, :]`로 미리 뒤집어 행 r을 `ys[::-1][r]`에 맞춥니다. flat_graph의 행 j는 y = oy - (j+0.5)·dx이므로 이 뒤집기가 맞고, 내림차순 축을 그대로 넘겨도 비트가 같습니다.
- 구간 찾기(`find_interval_ascending`): `x[0] <= v < x[n-1]`이면 `x[i] <= v < x[i+1]`인 유일한 i, `v == x[n-1]`이면 n-2, `v < x[0]`이면 0, `v > x[n-1]`이면 n-2, NaN이면 -1입니다. 앞 점의 구간에서 시작하는 이분 탐색이지만 결과가 유일하므로, 상태 없는 탐색이나 점별 Parallel.For로 바꿔도 같습니다(`searchsorted(side="right") - 1`을 [0, n-2]로 자른 값과 60,000점 일치, NaN만 다름).
- 정규화 거리: `y = (v - g[i]) / (g[i+1] - g[i])`를 점마다 계산합니다. 범위 밖이면 y < 0 또는 y > 1이 되어 가장자리 구간으로 선형 외삽합니다. `fill_value=None`이라 범위 밖 표시는 계산만 되고 쓰이지 않습니다. NaN 좌표는 값 계산을 건너뛰고 `result[nans] = nan`이 덮습니다.
- 값 계산(일반 가지, 휠 기계어 0x7bb0-0x7c18). a = 1 - y0, b = 1 - y1이고, `Fused(p, q, s)`는 p·q + s를 한 번만 반올림합니다.

| 항 | pyx 원문 (`result = result + ...`) | arm64 휠 기계어 | C# |
|---|---|---|---|
| 1 | `values[i0, i1] * (1 - y0) * (1 - y1)` | `fmadd(a*v00, b, +0.0)` | `r = Fused(a * v00, b, 0.0)` |
| 2 | `values[i0, i1+1] * (1 - y0) * y1` | `fmadd(a*v01, y1, r)` | `r = Fused(a * v01, y1, r)` |
| 3 | `values[i0+1, i1] * y0 * (1 - y1)` | `fmadd(y0*v10, b, r)` | `r = Fused(y0 * v10, b, r)` |
| 4 | `values[i0+1, i1+1] * y0 * y1` | `fmadd(y0*v11, y1, r)` | `r = Fused(y0 * v11, y1, r)` |

누적이 +0.0에서 시작하므로 -0.0은 나오지 않습니다(값이 모두 -0.0이어도 +0.0). 축 하나의 길이가 1이면 `Fused(v[i], 1 - y, y * v[i+1])`, 두 축 모두 1이면 `values[0, 0]`입니다. 우리 호출은 두 축이 16 이상이라 일반 가지만 돕니다. 출력은 float64 (N,)입니다.

#### brentq

- 기본값은 `xtol=2e-12`, `rtol=4·eps = 8.881784197001252e-16`, `maxiter=100`(_zeros_py.py:9-11)이고, 호출부는 xtol = rtol = 1e-12입니다. eps는 `np.finfo(float).eps = 2.220446049250313e-16`이며 C#의 `double.Epsilon`(4.9e-324)이 아닙니다.
- 오류: xtol <= 0, rtol < 4·eps, 함수값 NaN(`_wrap_nan_raise`), 양 끝 부호 같음("f(a) and f(b) must have different signs"), maxiter < 0은 ValueError입니다. 반복 초과는 `disp=True`라 RuntimeError("Failed to converge after N iterations.")입니다.
- 알고리즘(`Zeros/brentq.c`):
  1. fpre = f(a), fcur = f(b)(호출 2회). fpre == 0이면 a, fcur == 0이면 b를 돌려줍니다(이때 iterations는 초기화되지 않음). signbit가 같으면 부호 오류입니다.
  2. 반복마다 (가) fpre, fcur가 0이 아니고 signbit가 다르면 xblk = xpre, fblk = fpre, spre = scur = xcur - xpre로 둡니다. (나) abs(fblk) < abs(fcur)이면 xpre ← xcur, xcur ← xblk, xblk ← 옛 xcur로 바꾸고 f도 같이 바꿉니다. (다) delta = (xtol + rtol·abs(xcur))/2, sbis = (xblk - xcur)/2이고, fcur == 0이거나 abs(sbis) < delta면 xcur를 돌려줍니다. (라) abs(spre) > delta이고 abs(fcur) < abs(fpre)이면, xpre == xblk일 때 stry = -fcur·(xcur - xpre)/(fcur - fpre), 아니면 dpre = (fpre - fcur)/(xpre - xcur), dblk = (fblk - fcur)/(xblk - xcur), stry = -fcur·(fblk·dblk - fpre·dpre)/(dblk·dpre·(fblk - fpre))입니다. 2·abs(stry) < min(abs(spre), 3·abs(sbis) - delta)이면 spre = scur, scur = stry이고, 아니면 spre = scur = sbis입니다. (라)의 조건이 거짓이어도 spre = scur = sbis입니다. (마) xpre = xcur, fpre = fcur로 두고, abs(scur) > delta면 xcur += scur, 아니면 xcur += (sbis > 0 ? delta : -delta)입니다. 이어서 fcur = f(xcur)입니다.
  3. maxiter번 돌고도 끝나지 않으면 반복 초과입니다.
- 휠 기계어(`_zeros` 모듈의 `_brentq`)는 세 곳을 FMA로 축약합니다. 나머지 연산(/2와 ×0.5, 2·abs와 abs+abs, 보간식, 분모)은 따로 반올림되며 원문과 값이 같습니다.

| 식 | arm64 휠 기계어 | C# |
|---|---|---|
| `delta = (xtol + rtol*fabs(xcur))/2` | `fmadd(rtol, abs(xcur), xtol)` 뒤 ×0.5 (0x39a4) | `Fused(rtol, Math.Abs(xcur), xtol) * 0.5` |
| 외삽 분자 `fblk*dblk - fpre*dpre` | `fmadd(fblk, dblk, -(fpre*dpre))` (0x3a7c) | `Fused(fblk, dblk, -(fpre * dpre))` |
| `3*fabs(sbis) - delta` | `fnmsub` = 3·abs(sbis) - delta (0x3ab4) | `Fused(3.0, Math.Abs(sbis), -delta)` |

- `gap(p)`는 `budyko_runoff(np.array([p]), np.array([e]))[0] - r`, 곧 phi = e/p, et = p·sqrt(phi·tanh(1/phi)·(1 - exp(-phi))), clip(p - et, 0, p) - r입니다. numpy float64 `tanh`는 NEON 기준 SIMD 구현이라 libm과 다릅니다([0.3, 3]에서 입력의 21%가 1 ulp 차이, 길이 1 배열과 스칼라도 같은 구현). `exp`는 200,000점에서 libm과 같았습니다. gap을 libm tanh로 계산하면 PET 3,004개 중 588개에서 근이 최대 5 ulp 움직였습니다.
- 기본 설정(earth.toml: PET = 0.055·15 + 0.1 = 0.9249999999999999)의 근은 scipy, FMA 복제, FMA 없는 복제, libm tanh 모두 1.2211623135668164(반복 7, 호출 8)입니다. tanh가 비트 일치하지 않을 때의 허용 오차는 abs(ΔP) <= 2·(xtol + rtol·abs(P))입니다. 두 근 모두 각자의 참 근에서 마지막 구간 폭(2·delta) 안에 있기 때문이며, P ≈ 1.22에서 절대 4.4e-12(상대 3.6e-12)입니다.
- `p_value`는 `precip_m_per_yr` 필드(저장 float32라 몇 ulp 차이는 거의 늘 사라짐)와 manifest의 실수 글자(마지막 자리가 달라질 수 있음)에 닿습니다. runoff_eff = max(0.5, 0.1·P, 0.01) = 0.5라 다른 필드에는 닿지 않습니다.

#### macOS arm64 휠의 FMA 축약 (공통 장에 넘길 사실)

- scipy `meson.build`(v1.18.1)에는 `-ffp-contract`나 fast-math 설정이 없고 인텔 컴파일러에만 `-fp-model=strict`가 있습니다. 기계어는 한 식 안의 곱셈과 덧셈을 `fmadd`·`fnmsub`로 합쳤으므로 clang 기본 축약(-ffp-contract=on)으로 보입니다.
- 같은 환경의 다른 확장 모듈에도 스칼라 FMA 명령이 있습니다. `scipy/ndimage/_nd_image`에 108개, `scipy/spatial/_ckdtree`에 112개, `skimage/measure/_marching_cubes_lewiner_cy`에 70개, `numpy/_core/_multiarray_umath`에 588개입니다. ndimage, KD-tree, marching cubes 대체도 축약 자리를 기계어로 확인해야 합니다. `gaussian_filter1d`를 단순 대칭 루프로 복제해 보면 FMA를 넣어도 빼도 맞지 않았으므로, 그 원인은 NdImage 항목에서 따로 확인해야 합니다.
- 축약은 컴파일러와 CPU에 달려 있어 Python 결과 자체가 플랫폼마다 마지막 자리에서 다를 수 있습니다. x86-64 휠(Windows, Linux)은 기준 명령 집합에 FMA가 없어 축약이 없을 것으로 보지만, 이 기계에서는 확인할 수 없습니다. 그래서 golden 자료는 macOS arm64에서만 만들고 플랫폼과 휠 태그를 함께 기록합니다.
- FMA 없이 옮겼을 때의 영향(실측): tiny 평면 히어로에서 두 보간만 FMA 없는 꼴로 바꾸면 float64 필드 7개가 비트 단위로 달라졌습니다(strata_bottom_m 2,315개, exhumation_m 615칸, z_m 173칸 등, 상대 최대 4.2e-14). float32로 바꾸면 모두 같았고, 솔버 반복 수(28)와 이산 필드도 같았습니다. tiny 행성 히어로는 차이가 없었습니다.

#### 검증 실험

| 실험 | 사례 | 결과 |
|---|---|---|
| find_peaks 복제 | 양자화 무작위 신호 20,000개 × pmin 3가지, 문턱 = 돌출도 2,000회, 손 사례 10개(빈 배열, 길이 1·2, 평탄, 짝·홀 평탄 봉우리, 끝까지 평탄, NaN 2가지, -0.0). 모두 62,010회, 봉우리 1,197,857개 | 봉우리, 돌출도, 바닥 모두 비트 일치 |
| bimodality 전체 복제 | 두 가우스 분포 200개(일부는 칸 경계 값) + tiny 행성 `z_mean_m` | 201회 dict 일치 |
| RGI 복제(FMA 꼴) | 무작위 300경우, 191,826점(범위 밖 19,690점, 격자점, 마지막 격자점, NaN, ±0.0 값) | 0 불일치. FMA 없는 꼴은 43,735점 불일치 |
| RGI 실제 호출 | tiny 평면 히어로 미리 풀기·먼저 풀기, 행성 히어로 먼저 풀기. 각 16×16 격자, 4,096점(범위 밖 496점) | FMA 꼴 0 불일치. FMA 없는 꼴 1,008~1,036점, 최대 3 ulp |
| RGI 길이 1 축 | 60,000점 | FMA 꼴 0 불일치. FMA 없는 꼴 14,873점 |
| brentq 복제(FMA 꼴) | 실제 gap의 PET 3,004개 + 무작위 함수 20,000개(3차식, tanh, exp, x³), 두 가지 xtol·rtol | 근, 반복 수, 호출 수 0 불일치. FMA 없는 꼴은 42건이 근 1~3 ulp, 7건이 반복 수 다름 |
| brentq 오류 경로 | 부호 같음, maxiter 0과 1, NaN, xtol 0, rtol 1e-16 | 예외 종류와 글 확인 |

#### C# API

```csharp
namespace Bpcg.Numerics;

public sealed record PeakSet(long[] Peaks, double[] Prominences, long[] LeftBases, long[] RightBases);

public static class Signal
{
    // find_peaks(x, prominence=minProminence). 다른 조건 None, wlen=None.
    public static PeakSet FindPeaks(ReadOnlySpan<double> x, double minProminence);
    public static (long[] Mid, long[] Left, long[] Right) LocalMaxima1D(ReadOnlySpan<double> x);
    // wlen < 2 이면 신호 전체 (None = -1)
    public static (double[] Prominences, long[] LeftBases, long[] RightBases) PeakProminences(
        ReadOnlySpan<double> x, ReadOnlySpan<long> peaks, long wlen = -1);
}

// RegularGridInterpolator((grid0, grid1), values, "linear", bounds_error=False, fill_value=None)
public sealed class RegularGridInterpolator
{
    // values: (grid0.Length, grid1.Length) 행 우선. 축은 엄격 오름 또는 엄격 내림(내림이면 축·values 뒤집음)
    public RegularGridInterpolator(double[] grid0, double[] grid1, double[] values);
    public double[] Evaluate(ReadOnlySpan<double> q0, ReadOnlySpan<double> q1);
    public void Evaluate(ReadOnlySpan<double> q0, ReadOnlySpan<double> q1, Span<double> result);
    internal static int FindIntervalAscending(ReadOnlySpan<double> x, double xval, int prevInterval);
}

public readonly record struct BrentResult(double Root, int Iterations, int FunctionCalls, bool Converged);

public static class Brent
{
    public const double NumpyEps = 2.220446049250313e-16; // np.finfo(float).eps, double.Epsilon 아님
    public const double DefaultXtol = 2e-12;
    public const double DefaultRtol = 4 * NumpyEps;
    public const int DefaultMaxIter = 100;
    public static double BrentQ(Func<double, double> f, double a, double b,
        double xtol = DefaultXtol, double rtol = DefaultRtol, int maxiter = DefaultMaxIter);
    public static BrentResult BrentQFull(Func<double, double> f, double a, double b,
        double xtol = DefaultXtol, double rtol = DefaultRtol, int maxiter = DefaultMaxIter);
}

// arm64 휠이 축약한 자리에서만 쓰는 FMA. 다른 곳의 Math.FusedMultiplyAdd 는 시험으로 막습니다.
internal static class WheelFma
{
    public static double Fused(double p, double q, double s) => Math.FusedMultiplyAdd(p, q, s);
}
```

호출부는 다음처럼 옮깁니다. `Bpcg.Metrics.Hypsometry.Bimodality`는 `Signal.FindPeaks(padded, minProminence * top)` 뒤 돌출도가 크고 같으면 번호가 작은 둘을 직접 고릅니다(`np.argsort(-p, kind="stable")[:2]`와 같음). `Bpcg.Landscape.Warmstart.CoarseWarmStart`는 `xs[k] = ox + (k + 0.5) * dxc`, `ys[k] = oy - (k + 0.5) * dxc`를 Python과 같은 식으로 만들고, 뒤집은 ys와 행을 뒤집은 values(`v[r, c] = z[(nyc-1-r)·nxc + c]`)로 보간기를 만듭니다. 점은 `(pos[c, 1], pos[c, 0])`이고, 끝에 출구 칸을 `zo`로 덮습니다. `Bpcg.Hero.Flat.PresolveSurface`는 `xs = ys = -0.5 * L + (k + 0.5) * dc`를 만들고 values 행을 같은 식으로 뒤집습니다. `Bpcg.Hero.Flat.PrecipForRunoff`는 `Bpcg.Planet.Climate.BudykoRunoff`를 같은 식으로 부르고, `gap(hi) < 0`이면 `hi = r + 2e`로 넓힌 뒤 `Brent.BrentQ(gap, lo, hi, 1e-12, 1e-12)`를 부릅니다. 보간은 점마다 독립이라 Parallel.For로 나눠도 결과가 같고, find_peaks와 brentq는 순차로 둡니다.

#### golden 대조 시험

| 대상 | 입력 | 출력 | 크기 | 비교 |
|---|---|---|---|---|
| `Signal.FindPeaks` | hypsometry.py:131에서 붙잡은 `padded`·pmin(tiny 행성, test_metrics 사례 4개), 손 사례 10개, 고정 시드 무작위 200개(pmin = 0.05·max, 0, 돌출도와 같은 값) | peaks·바닥 int64, prominences float64 | 약 0.2 MB | 정수 완전 일치, 실수 비트 일치 |
| `Hypsometry.Bimodality`(연쇄) | tiny 행성 `z_mean_m`·area, test_metrics 사례 | peaks, separation_m, ok, n_peaks | 묶음 재사용 | 완전 일치(히스토그램·평활 대체가 맞은 뒤) |
| `RegularGridInterpolator` 실제 호출 | warmstart.py:117(평면·행성 히어로), flat.py:124에서 붙잡은 grid0·grid1·values·xi(tiny, 각 4,096점) | (N,) float64 | 약 0.3 MB | 비트 일치 |
| `RegularGridInterpolator` 경계 | 고정 시드 30경우: 내림차순 축, 길이 1 축, ±0.0 값, 가장자리 반 칸·먼 바깥·격자점·마지막 격자점·NaN | (N,) float64 | 약 0.2 MB | 0의 부호와 NaN 위치까지 비트 일치 |
| `Brent.BrentQ` 재생 | flat.py:91의 (x, f(x)) 호출 기록(PET 0.9249999999999999, 0.3, 0.925, 2.0과 격자 50개), Horner 다항식 몇 개 | 근, 반복 수, 호출 수 | 수십 KB | 근 비트 일치, 수 완전 일치, 기록에 없는 x를 부르면 실패 |
| `Brent.BrentQ` 오류 | 부호 같음, maxiter 0·1, NaN, xtol 0, rtol 1e-16 | 예외 | 없음 | 종류 일치 |
| `Flat.PrecipForRunoff` | runoff 0.5, PET 4개 | p | 없음 | numpy 호환 tanh가 있으면 비트 일치, 없으면 abs(ΔP) <= 2·(xtol + rtol·abs(P)) |

#### 결정이 필요한 점

1. 두 대체(보간 일반 가지 4곳 중 첫 항은 `0.0 + 곱`과 같아 실제로는 3곳, 길이 1 가지 1곳, brentq 3곳)에서만 `WheelFma.Fused`로 FMA를 쓰는 예외를 허용할지 정해야 합니다. 허용하지 않으면 실제 호출에서 보간 점의 약 25%가 1~3 ulp 다르므로, 단위 대조는 'ulp 허용'으로 낮추고 끝에서 끝 비교는 float32 기준(위 실측에서는 통과)에 기댑니다.
2. golden 자료를 macOS arm64에서만 만들지 정해야 합니다. 그러면 Windows x64 팀원은 golden을 다시 만들 수 없고 C# 시험만 돌립니다.
3. `budyko_runoff`의 numpy 호환 tanh(NEON SIMD 알고리즘)를 공통 장에서 제공할지 정해야 합니다. climate 필드와 `precip_for_runoff` 근의 비트 일치가 여기에 달려 있습니다.
4. export_golden.py가 scipy 클래스를 기록용으로 감싸 실제 입력을 붙잡아도 되는지 정해야 합니다(src는 고치지 않음).
5. scipy 예외의 C# 대응(ValueError → ArgumentException, RuntimeError → InvalidOperationException)과 메시지 언어는 공통 장에서 정해야 합니다.

### marching cubes·glb 쓰기 (scikit-image·trimesh 대체)

이 절은 `src/bpcg/bake/mesh.py`가 쓰는 scikit-image 0.26.0의 `marching_cubes`와 trimesh 5.1.0의 메시 처리·glb 쓰기를 C#으로 옮기는 계획입니다. 단계 0에서는 C# 설계와 같은 연산 순서로 Python 스칼라 흉내를 짰고, 그 흉내로 tiny 회랑(`voxel_m` 8 m)과 `--set profile.corridor.voxel_m=2.0` 회랑의 `caves.glb`를 바이트까지 재현했습니다. 흉내 스크립트는 세션 scratch(저장소 밖)의 `mc_ref.py`, `normals_ref.py`, `glb_ref.py`, `emulate_chain.py`이며, 단계 1에서 golden 도구로 다시 만듭니다. 따라서 C#의 목표는 golden 기계(macOS arm64)에서 바이트까지 일치하는 것입니다. 다만 Python 기준 휠이 두 곳을 FMA(곱셈과 덧셈을 한 번에 반올림하는 명령)로 계산하므로, 그 두 곳에만 `Math.FusedMultiplyAdd`를 써야 합니다. 이는 스펙의 FMA 금지에 대한 예외이므로 승인이 필요합니다.

#### 쓰는 곳과 인자

| 단계 | 호출 | 인자와 동작 |
|---|---|---|
| `cave_surface` | `skimage.measure.marching_cubes` | `level=0.0`, `spacing=(v, v, v)`, `allow_degenerate=False`입니다. 나머지는 기본값 `gradient_direction='descent'`, `step_size=1`, `method='lewiner'`, `mask=None`입니다. 반환 4개 중 `verts`, `faces`만 씁니다 |
| `build_mesh` | `trimesh.Trimesh` | `process=False`, `validate=False`라서 만들 때는 합치지 않습니다. 그 뒤 `merge_vertices()`, `remove_unreferenced_vertices()`, `vertex_normals`, `visual.vertex_colors = rgba` 순서로 부릅니다 |
| `export_glb` | `trimesh.exchange.gltf.export_glb` | `Scene.add_geometry(mesh, node_name="caves", geom_name="caves")`, `include_normals=True`(`unitize_normals=True`는 기본값)입니다. `.part`에 쓴 뒤 `os.replace`로 바꿉니다 |

설치본 `_marching_cubes_lewiner.py`, `_marching_cubes_lewiner_luts.py`, `trimesh/exchange/gltf/__init__.py`는 GitHub 태그 v0.26.0, 5.1.0의 파일과 바이트가 같습니다. Cython 원본은 `src/skimage/measure/_marching_cubes_lewiner_cy.pyx`(v0.26.0)입니다. 라이선스는 scikit-image가 BSD-3-Clause, trimesh가 MIT입니다.

#### Lewiner 알고리즘 (Cython)

- **훑는 순서.** Cython의 축 이름은 x = axis 2, y = axis 1, z = axis 0입니다. z(바깥) → y → x(안쪽) 순서로 칸 0..n−2를 돕니다. bpcg의 `f`는 (동, 북, 위) 축이므로 바깥 고리가 동쪽, 안쪽 고리가 위쪽 색인입니다.
- **경우 번호.** 꼭짓점 값의 차례는 v0 = im[z,y,x], v1 = im[z,y,x+1], v2 = im[z,y+1,x+1], v3 = im[z,y+1,x]이고, v4..v7은 z+1에서 같은 차례입니다.
    - float32 값을 double로 넓혀 `level`을 뺍니다. level이 0이면 값이 그대로이고 −0.0도 유지됩니다.
    - `v_k − level > 0.0`인 k의 비트를 더한 값이 index입니다. 따라서 값이 0인 꼭짓점은 안쪽으로 셉니다.
    - `CASES[index]`가 (case, config)를 줍니다. case > 0이면 `the_big_switch`가 `TEST*` 표와 `test_face`/`test_internal` 결과로 `TILING*` 표의 삼각형 목록을 고릅니다.
- **꼭짓점·삼각형 순서.**
    - 삼각형 목록의 모서리 번호를 차례로 보며, 그 모서리에 꼭짓점이 없을 때만 새로 만듭니다. 모서리 번호는 0..11이고 12는 칸 가운데입니다.
    - 이미 만든 꼭짓점은 `faceLayer` 두 장으로 찾습니다. 두 장은 현재 z층과 다음 z층이고, (x, y) 칸마다 4칸(j = 0 x 방향 모서리, 1 y 방향, 2 z 방향, 3 가운데)을 둡니다. 새 z층에 들어갈 때 두 장을 맞바꾸고 다음 층을 −1로 지웁니다.
    - 그래서 꼭짓점 번호는 처음 쓰인 순서가 되고, 삼각형 순서는 내보낸 순서가 됩니다.
- **꼭짓점 위치.** 교과서의 a/(a−b) 보간과 연산이 다릅니다. 모서리 끝 (d1, d2)는 `EDGETORELATIVEPOS{X,Y,Z}[vi]`에서 읽습니다. 끝점 값은 `vv`[dz·4 + dy·2 + dx]이고, `vv`는 v의 2↔3, 6↔7을 바꾼 배열입니다.

```text
EPS = 2.220446049250313e-16            (np.spacing(1.0), .pyx 이름 FLT_EPSILON)
w1 = 1.0/(EPS + abs(vv[i1]));  w2 = 1.0/(EPS + abs(vv[i2]))
fx = (0.0 + dx1*w1) + dx2*w2;  ff = (0.0 + w1) + w2          (fy, fz 도 같음)
X  = (float)((double)x + (st*fx)/ff)                           (add_vertex 에서 float32 로 한 번 반올림)
center(12): w_k = 1/(EPS + abs(v_k)), k = 0..7 순서로 fx += cx_k*w_k, ff += w_k
            cx = 0,1,1,0,0,1,1,0  cy = 0,0,1,1,0,0,1,1  cz = 0,0,0,0,1,1,1,1
```

- **0 나눗셈.** `test_internal`의 `t = −b/(2a + EPS)`와 `t = v_a/(v_a − v_b + EPS)`는 분모가 정확히 0일 수 있습니다. 이때 C는 ±inf나 NaN을 내고 판정을 그대로 이어 갑니다. C# double도 같은 의미이므로 그대로 옮기면 됩니다. 손으로 만든 case 4 볼륨(a = −2^−53)에서 휠과 같음을 확인했습니다.
- **normals·values 출력.** float32로 누적한 기울기와 칸 값 범위입니다. bpcg가 버리므로 옮기지 않습니다.
- **표.** EDGETORELATIVEPOS 3개와 `LutProvider` 48개로 모두 51개이고, int8 값이 17,548개입니다. Lewiner 경로는 `CASESCLASSIC`(4,096개)을 쓰지 않습니다. EDGE X·Y·Z 다음 `LutProvider` 인자 순서로 바이트를 이어 붙이면 SHA-256이 `6739663a6fe32e9b7b44169544ca17f62cc7295285c7c0dae25f157ab0f652e7`입니다.

#### FMA 예외 (arm64 휠의 기계어)

macOS arm64 휠은 clang의 곱셈·덧셈 합치기로 컴파일되어 있습니다. `otool -tvV`로 보면 MC 모듈의 FMA 명령은 88개입니다. 함수별로는 `_add_face_from_edge_index` 6개, `calculate_center_vertex` 30개, `test_internal` 23개, `the_big_switch` 17개(인라인된 `test_face`), `get_normals` 3개, `__divdc3` 9개입니다. 이 중 결과에 영향을 주는 자리만 C#에서 FMA로 씁니다.

| 위치 | 원문 | 휠이 계산하는 값 | C# |
|---|---|---|---|
| `test_face` | `A*C - B*D` | fma(A, C, −(B·D)) | FMA. 다만 float32 값의 곱은 double에서 정확하므로 결과는 같습니다(case 3에서 146만 번 시도해 갈림 없음) |
| `test_internal` case 4·10 a | e40·e62 − e73·e51 | fma(e40, e62, −(e73·e51)) | FMA (e40 = v4−v0, e62 = v6−v2, e73 = v7−v3, e51 = v5−v1) |
| 〃 b | v2·e40 + v0·e62 − v1·e73 − v3·e51 | fma(−v3, e51, fma(−e73, v1, fma(e40, v2, v0·e62))) | FMA 세 번 |
| 〃 t | −b/(2a + EPS) | 분모 fma(a, 2, EPS) | 보통 식(2a는 정확하므로 같음) |
| 〃 At, Bt, Ct, Dt | v0 + e40·t, v3 + e73·t, v2 + e62·t, v1 + e51·t | 각각 fma(차이, t, 기준) | FMA |
| 모서리 시험(case 6·7·12·13) | Bt·Ct·Dt = b0 + (b1−b0)·t | fma(b1−b0, t, b0) | FMA (At = 0) |
| test 5·10 판정 | At·Ct − Bt·Dt | fma(At, Ct, −(Bt·Dt)) | FMA |
| 꼭짓점 보간 fx와 가운데 꼭짓점 | 0·w, 1·w의 합 | fma이지만 곱이 정확함 | 보통 식 |
| 꼭짓점 법선 누적(scipy `coo_matmat_dense`) | `Y += x * B` | fma(x, B, Y) | FMA |

FMA를 빼면 결과가 실제로 갈립니다.
- **MC.** FMA 흉내와 보통 흉내가 갈리는 2×2×2 볼륨 3,120개(case 4·6·7)를 찾았는데, 모두 휠이 FMA 쪽과 같았습니다.
    - 예를 들어 float32 볼륨(C 순서, 16진 `415cab48 4145bb8b c15cab48 4145bb8b 415cab48 c15cab48 415cab48 415cab48`)에서 휠은 삼각형 6개를 내지만 보통 식은 2개를 냅니다.
    - 같은 크기의 값이 되풀이될 때 잘 갈립니다. bpcg의 SDF는 대부분 +4·voxel로 잘린 값이라, 큰 회랑에서는 이런 경우가 실제로 생길 수 있습니다.
- **법선.** 누적에서 FMA를 빼면 tiny의 float64 법선 2,122행 중 1,797행이 다릅니다. glb NORMAL의 float32 성분으로는 6,366개 중 238개, 2 m 회랑에서는 137,508개 중 754개가 다릅니다.

#### Python 후처리 (`_marching_cubes_lewiner.py`)

1. `np.ascontiguousarray(volume, np.float32)`로 바꾸며, 짝수 반올림입니다. 다음 조건에서 예외가 납니다.
    - `level < vol.min()` 또는 `level > vol.max()`이면 ValueError를 냅니다.
    - 한 변이 2보다 작아도 ValueError를 냅니다.
    - 꼭짓점이 하나도 없으면 RuntimeError를 냅니다.
    - bpcg는 float64 `f`에서 `f.min() >= 0 or f.max() <= 0`이면 MC를 건너뛰므로 보통은 걸리지 않습니다. 다만 양수가 모두 float32에서 0으로 떨어지는 병적 입력에서는 예외가 납니다. C#도 같은 조건에서 예외를 냅니다.
2. `fliplr`로 꼭짓점을 (axis0, axis1, axis2) 순서로 바꿉니다. `faces.shape = -1, 3`은 NumPy 2.5에서 경고를 내지만 bpcg가 끕니다.
3. descent에서는 삼각형 (a, b, c)를 (c, b, a)로 뒤집습니다.
4. spacing이 (1, 1, 1)이 아니면 `vertices * np.r_[spacing]`을 계산합니다(float32 × float64 → float64).
5. `allow_degenerate=False`이면 `remove_degenerate_faces(vertices.astype(float32), …)`를 부릅니다.
    - 면 순서대로 (1,2), (1,3), (2,3) 쌍을 보며, float32 좌표 세 개가 모두 같으면 `map1[i] = map1[j] = min(map1[i], map1[j])`로 두고 그 면을 버립니다.
    - 끝에 `ok = map1 == arange`, `map2 = cumsum(ok) − 1`을 구합니다. 결과는 `faces2 = map2[map1[남은 면]]`(int64)와 `vertices[ok]`(float32)입니다.
    - 같음 비교는 spacing을 곱해 float32로 바꾼 좌표에서 하므로, 결과가 `voxel_m`에 따라 달라집니다. C#에서는 `(float)((double)vf * s)`로 계산합니다.
    - `map1`은 이행적이지 않습니다. a→b→c 사슬이 생기면 a가 b를 가리킨 채 남고, `map2[b]`는 b 앞의 마지막 남은 꼭짓점이 되어 엉뚱한 꼭짓점을 쓰는 면이 생길 수 있습니다. 정수로 반올림한 무작위 볼륨 40개 중 3개에서 사슬이 생겼고, 흉내도 휠과 같았습니다. 라이브러리 버그로 보지만 그대로 옮깁니다.
    - 2 m 회랑에서는 MC 37번 중 8번이 면을 2~170개씩 지웠습니다. 실제 자료에서 쓰이는 경로입니다.

#### bake/mesh.py 뒤처리

1. **조각과 띠.** 조각은 `ia ∈ range(0, max(nxv − 1, 1), 64)`이고 `ja`도 같습니다. 띠마다 `k0 = floor(lo/v)`, `k1 = ceil(hi/v)`로 정하며, 차이가 2보다 작으면 k1 = k0 + 2로 둡니다. `f = clip(d_cave, ±4v).reshape(tx, ty, zz)`는 C 순서이고, 그 안에서는 z(위) 색인이 가장 빨리 바뀝니다.
2. **좌표 더하기.** `vt + [tx[0], ty[0], zz[0]]`는 float32 + float64 → float64입니다. C#에서는 `(double)vf + off`로 씁니다.
3. **면 순서.** 면은 `fc[:, [0, 2, 1]] + n_vert`입니다. skimage 행 (c, b, a)가 (c, a, b)가 되며, 이는 Cython 원래 (a, b, c)를 돌린 것이므로 감김은 Cython과 같습니다.
4. **무게중심과 걸러내기.** 무게중심 `verts[faces].mean(axis=1)`은 ((p0 + p1) + p2)/3.0입니다(앞에서부터 더함, 검증). d1 < 0인 면만 남긴 뒤, `np.unique(faces)`로 꼭짓점을 원래 순서대로 압축합니다.
5. **diag.** `tiles_with_caves`는 띠가 있는 조각 수이며 MC 결과와 관계가 없습니다(2 m에서 40개를 셌지만 MC는 37번). `voxels`는 평가한 `f` 크기의 합입니다. 키 순서는 tiles, tiles_with_caves, voxels, faces_raw, faces_kept, seconds입니다.
6. **엔진 좌표.** `to_engine` = (x − cx, z − y_offset, −(y − cy))이고 float64입니다.
7. **merge_vertices.**
    - digits는 8입니다. 키는 성분마다 `rint(c·1e8)`(짝수 반올림, int64)입니다.
    - 참조된 꼭짓점만 처음 나온 순서로 남기고, 위치는 첫 꼭짓점의 값을 씁니다.
    - 이때 `vertex_normals`가 아직 캐시에 없으므로 법선은 키에 들어가지 않습니다.
    - 합친 뒤에는 `remove_unreferenced_vertices`가 아무것도 바꾸지 않습니다. 꼭짓점 수는 tiny에서 2,126 → 2,122, 2 m에서 46,651 → 45,836입니다.
8. **면 법선.**
    - e0 = B − A, e1 = C − B로 `np.cross`를 구하며, 곱 두 개를 따로 반올림한 뒤 뺍니다.
    - 길이는 sqrt((x² + y²) + z²)입니다. `np.dot(v*v, [1,1,1])`을 Accelerate dgemv가 앞에서부터 더합니다.
    - 길이 > 1e-13이면 `**= -1`로 역수를 구해 x·inv를 곱합니다. 그렇지 않으면 0 벡터로 둡니다.
9. **각.**
    - u = unitize(B − A), v = unitize(C − A), w = unitize(C − B)입니다. 길이 ≤ 1e-13인 행은 역수를 취하지 않은 x·길이를 돌려주며, 이 동작도 그대로 옮깁니다.
    - r0 = acos(clip((u0v0 + u1v1) + u2v2, −1, 1)), r1 = acos(clip(((−u0)w0 + (−u1)w1) + (−u2)w2, −1, 1)), r2 = (π − r0) − r1입니다.
    - 하나라도 1e-8보다 작으면 셋을 모두 0으로 둡니다.
10. **꼭짓점 법선(각 가중).** 면 법선의 제곱합이 0.5보다 큰 면만 씁니다. 면 번호 순, 꼭짓점 자리 순으로 각이 0이 아니면 `s[v] = fma(각, n_f, s[v])`를 0에서부터 누적하고, 끝에 unitize합니다. mesh.py docstring은 '면적 가중'이라 하지만 실제 동작과 다릅니다.
11. **색.** 탐침점 `p − (0.5·v)·n`을 `to_local` = (X + cx, cy − Z, Y + y_offset)로 바꾸고 `solid_material`(uint8)을 읽습니다. RGB는 `COLOR_RGB[min(m, 11)]`, A는 m입니다.
12. **내보낼 배열.** POSITION은 float32, NORMAL은 한 번 더 unitize한 법선의 float32, COLOR_0은 uint8 RGBA, 인덱스는 uint32입니다.

#### glb 바이트 배치 (trimesh 5.1.0)

- **덩이 구성.** 파일은 머리 12 B(`glTF`, 2, 전체 길이), JSON 덩이 머리 8 B(길이, `JSON`), JSON, BIN 덩이 머리 8 B(길이, `BIN\0`), BIN 순서입니다. 길이는 모두 little-endian uint32이고, 전체 길이는 JSON + BIN + 28입니다.
- **JSON.** `json.dumps(separators=(",", ":"))`로 쓰며, ensure_ascii가 켜져 있고 키를 정렬하지 않습니다. 실제로는 한 줄이며 아래 순서를 그대로 따릅니다.

```text
{"scene":0,"scenes":[{"nodes":[0]}],"asset":{"version":"2.0","generator":"https://github.com/mikedh/trimesh"},
 "accessors":[{"componentType":5125,"type":"SCALAR","bufferView":0,"count":3F,"max":[imax],"min":[imin]},
  {"componentType":5126,"type":"VEC3","byteOffset":0,"bufferView":1,"count":V,"max":[x,y,z],"min":[x,y,z]},
  {"componentType":5121,"normalized":true,"type":"VEC4","byteOffset":0,"bufferView":2,"count":V,"max":[r,g,b,a],"min":[r,g,b,a]},
  {"componentType":5126,"count":V,"type":"VEC3","byteOffset":0,"bufferView":3,"max":[x,y,z],"min":[x,y,z]}],
 "meshes":[{"name":"caves","extras":{},"primitives":[{"attributes":{"POSITION":1,"COLOR_0":2,"NORMAL":3},"indices":0,"mode":4}]}],
 "nodes":[{"name":"caves","mesh":0}],"buffers":[{"byteLength":12F+28V}],
 "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":12F},{"buffer":0,"byteOffset":12F,"byteLength":12V},
  {"buffer":0,"byteOffset":12F+12V,"byteLength":4V},{"buffer":0,"byteOffset":12F+16V,"byteLength":12V}]}
```

- **틀의 세부.** 인덱스 accessor에는 `byteOffset`이 없고, NORMAL accessor만 `count`가 `type`보다 앞에 옵니다. material, TEXCOORD, byteStride, target은 없습니다. trimesh 5.1.0은 빈 `world` 노드도 쓰지 않습니다.
- **max/min.** 각 성분의 float32 최댓값·최솟값을 double로 넓혀 Python `float.__repr__`로 씁니다(예: `137.51589965820312`, `600.0`, `-1.0`, `1e-05`, `-0.0`). 정수는 그대로 씁니다. numpy의 열 max는 ±0이 섞이면 +0.0을, min은 −0.0을 고르므로 C#의 `Math.Max`·`Math.Min` 의미와 같습니다.
- **채움.** JSON 뒤 공백은 `4 − ((len(문자열) + 20) % 4)`개라서, 이미 4의 배수여도 4개를 붙입니다(1~4개). BIN 조각은 4 B 경계까지 0으로 채우는데, 이 네 배열의 길이는 늘 4의 배수입니다.
- **조각 공유.** 바이트가 같은 조각은 bufferView를 함께 쓰는 규칙(해시 비교)이 있습니다. 이 네 배열에서는 사실상 일어나지 않지만, C#도 같은 규칙을 넣습니다.
- **결정성.** 시각, uuid, 임의 이름이 없고, dict 해시는 출력에 나타나지 않습니다. 별개 프로세스 5번(PYTHONHASHSEED 0과 4242 포함)과 기존 `out/tiny_check/corridor/caves.glb`의 SHA-256이 모두 `8d90fc9ddea9031f…`로 같았습니다.

#### 수치 함정 (발생 위치와 계획)

| 위치 | 함정 | 계획 |
|---|---|---|
| MC 입력 | float64 → float32 | `(float)d` (짝수 반올림) |
| MC 꼭짓점 | double 계산 → float32 → ×spacing(double) → float32 | 같은 캐스트 순서 |
| MC 판정 | arm64 휠의 FMA | 위 표대로 FMA (승인 필요) |
| `remove_degenerate_faces` | float32 완전 같음, 비이행 사상 | 그대로 |
| 면 재배열 | `[0, 2, 1]` | 그대로 |
| 무게중심 | 세 값을 앞에서부터 더한 뒤 / 3.0 | 그대로 |
| merge 키 | `rint(x·1e8)` | `(long)Math.Round(x * 1e8)` (ToEven) |
| 면 법선·unitize | 제곱합을 앞에서부터, 역수를 곱함 | 나눗셈으로 바꾸지 않음 |
| 각 | libm acos | `Math.Acos`. macOS에서는 numpy와 비트가 같고(220만 개), 다른 OS에서는 1 ulp 차이가 날 수 있음 |
| 꼭짓점 법선 | scipy COO의 FMA 누적, 0 가중 건너뜀 | FMA (승인 필요) |
| glb float | Python repr | `Bpcg.IO.PyJson`의 float repr |
| `_merge_intervals` | `sorted()`가 안정 정렬 | 안정 정렬 (같은 튜플은 값이 같아 순서 무관) |

#### C# 설계

- **`csharp/Bpcg/Numerics/MarchingCubes.cs`(`Bpcg.Numerics.MarchingCubes`).**
    - `Lewiner(double[] volume, int n0, int n1, int n2, double level, double s0, double s1, double s2, bool allowDegenerate)`는 `(double[] verts, long[] faces)`를 돌려줍니다.
    - .pyx를 줄 단위로 옮깁니다. `Cell`, `faceLayer` 두 장, `TheBigSwitch`, `TestFace`, `TestInternal`, `RemoveDegenerateFaces`를 둡니다.
    - step 1, mask 없음, Lewiner 경로로 고정합니다. normals와 values는 옮기지 않습니다.
    - `allowDegenerate=false`이면 반환되는 꼭짓점 값이 모두 float32로 나타낼 수 있는 값입니다.
- **`MarchingCubesLuts.g.cs`.**
    - 단계 1 스크립트 `csharp/golden/gen_mc_luts.py`(ruff 통과)가 `_to_array`로 표를 풀어, `ReadOnlySpan<sbyte>` 표와 모양 상수로 생성합니다. `CASESCLASSIC`은 뺍니다.
    - 머리 주석에 scikit-image 0.26.0과 파일 SHA-256(luts `a6e6144e…`, wrapper `f482cdb5…`), 표 SHA-256, BSD-3-Clause 고지를 적습니다.
    - C# 시험이 표 SHA-256을 다시 계산해 대조합니다. scikit-image 라이선스 원문은 `docs/licenses/`에 더합니다.
- **`TrimeshOps.cs`(`Bpcg.Numerics.TrimeshOps`).** `MergeVertices`(Dictionary로 처음 나온 순서를 지킴), `RemoveUnreferencedVertices`, `FaceNormals`, `FaceAngles`, `WeightedVertexNormals`, `UnitizeRows`를 둡니다. 모든 반복은 순차입니다.
- **`csharp/Bpcg/IO/Glb.cs`(`Bpcg.IO.Glb`).**
    - `WriteTrimeshLayout(path, uint[] indices, float[] positions, byte[] colors, float[] normals, string name = "caves")`는 쓴 바이트 수를 돌려줍니다.
    - JSON은 사전을 직렬화하지 않고 위 틀을 StringBuilder로 채웁니다.
    - 파일은 `.part`에 쓴 뒤 `File.Move(…, overwrite: true)`로 바꿉니다.
- **`csharp/Bpcg/Bake/Mesh.cs`(`Bpcg.Bake.Mesh`).**
    - 메서드는 `CaveBands`, `CaveSurface`, `BuildMesh`, `ExportGlb`이고 `MergeIntervals`는 private로 둡니다.
    - `sealed record EngineFrame(double Cx, double Cy, double YOffset)`에 `ToEngine`, `ToLocal`, `AsDict`를 둡니다.
    - 상수 64, 2, 4.0, 0.5는 Python처럼 클래스 상수로 둡니다. 조각 반복은 순차입니다.
- **시험 분리.** 대조 시험을 따로 하기 위해 부피 질의(`evaluate_grid`, `evaluate`, `cave_levels`, `cap_a/b`, 격자 값)를 인터페이스로 받게 합니다. golden이 기록한 Python 출력을 돌려 주는 가짜로 바꿔 끼울 수 있어야 하며, volume 담당과 맞춥니다.

#### caves.glb 비교 전략

- **1차: 배열 비교.** glb를 읽어 배열로 비교합니다.
    - 인덱스, POSITION, COLOR_0과, NORMAL max/min을 뺀 JSON 문자열은 완전히 같아야 합니다.
    - NORMAL은 비트 일치를 기대합니다. 다르면 성분마다 절대 오차 2^−23까지 받고, 근거로 acos를 주석에 남깁니다.
    - 근거: acos를 ±1 ulp 흔들면 float64 법선은 최대 5.4e−15 바뀌고, float32 성분의 약 3.4%가 1 ulp 바뀌거나 0 근처에서 부호가 바뀝니다.
- **2차: 바이트 비교.** NORMAL이 비트까지 같으면 파일 바이트도 같아야 합니다. 이는 쓰기 시험을 겸하며, golden 기계에서는 반드시 성립해야 합니다.
- **색이 갈릴 때.** 법선이 1 ulp 바뀌면 색 탐침점이 법선 방향으로 약 2e−14 m 움직입니다. 이 때문에 재질 경계를 넘어 색이 갈리면, 허용 오차를 넓히지 않고 진행 기록에 적습니다.

#### 단계 0 검증 기록

| 실험 | 결과 |
|---|---|
| Python Lewiner 흉내 vs 휠 | 구, 잘린 구, 삼각함수, 정규분포 잡음(14개 case 모두), 정수 반올림 볼륨(0 값·퇴화·비이행 사슬)에서 spacing 0.7·1·2·8 모두 꼭짓점 float32 비트와 면이 완전히 같음 |
| 실제 MC 입력(tiny 3개, 2 m 37개) | 흉내 = 휠 (FMA 유무와 관계없이 같음, 갈리는 판정 없음) |
| FMA 갈림 탐색 | 3,120개 모두 휠 = FMA 쪽 |
| 끝에서 끝 흉내(MC → 조립 → merge → 법선 → 색 → glb) | tiny 97,756 B, 2 m 2,351,124 B 바이트 일치. 법선 누적에서 FMA를 빼면 NORMAL만 다름 |
| glb 쓰기 흉내 vs trimesh, 무작위 메시 400개 | 400개 모두 바이트 일치, 공백 1·2·3·4개가 모두 나옴 |
| `np.arccos` vs `math.acos`, `np.dot(v*v, ones)` vs (x²+y²)+z² | 둘 다 비트 일치 (220만 개, n = 1..100,000) |
| 조각 경계 합성 시험 | k0가 다르면 경계 꼭짓점 38개 중 3~6개가 3.8e−6 m 어긋나 merge되지 않음. tiny와 2 m에서는 남은 근접 중복이 0쌍 |

#### 남은 쟁점

- **결정이 필요한 일.** 다음을 정해야 합니다. 자세한 내용은 '결정할 일'에 적었습니다.
    - FMA 예외 승인
    - golden 기계 고정
    - MC API 범위
    - `Bpcg.Bake.Mesh`와 `Godot.Mesh`의 이름 충돌 처리
    - 부피 질의 인터페이스
    - golden 스크립트가 skimage·trimesh를 직접 불러도 되는지
    - 조각 병렬화 여부
- **Python 쪽 의심 사항.** 고치지 않고 진행 기록에 적습니다.
    - 이웃 조각의 띠 바닥 k0가 다르면, 경계 꼭짓점이 float32 반올림 차이(수 µm) 때문에 합쳐지지 않습니다. 그 결과 이음매에 꼭짓점이 겹치고 법선이 끊깁니다. docstring은 '정확히 겹친다'고 적고 있습니다.
    - docstring은 '면적 가중' 법선이라 하지만, trimesh 5.1.0은 각으로 가중합니다.
    - skimage `remove_degenerate_faces`의 사상은 이행적이지 않습니다.


### numpy 선형대수·FFT·polyfit 대체

이 절의 분석은 에이전트가 거듭 중단되어 결과가 없습니다(10장 D7). core에 필요한 3성분 `norm`·`cross`·`dot`·`einsum`의 정확한 계산 순서는 6장 core ② 절에 있습니다. `np.fft`(bake/detail)와 `np.polyfit`(metrics/drainage, bake/detail)은 그 묶음을 옮길 때(단계 2) 조사해 여기에 적습니다.

### 파일 형식 (.npy·.npz·JSON·TOML·PNG·bin)

이 절은 C# 포트가 쓰고 읽는 파일의 바이트 형식을 정합니다. 근거는 numpy 2.5.3, CPython 3.13.15(zipfile, json, tomllib), matplotlib 3.11.2, Pillow 12.3.0 원본과 tiny 실행(`out/tiny_check/`) 실측입니다. 같은 기계에서 tiny 를 다시 돌리면 `seconds` 키를 뺀 모든 출력이 바이트까지 같았습니다. 비교한 출력은 npy 87개, npz 3개, json 17개, png 24장, bin 24개, u8, glb 입니다. 그래서 C# 출력도 바이트 비교를 기본으로 하고, 예외는 '대조에서 뺄 것'에 적습니다.

#### 한눈에 보기

| 파일 | Python 쓰기 | 형식 | C# | 대조 |
|---|---|---|---|---|
| `<그룹>/<이름>.npy` | `bundle.py:189` `np.save` | npy 1.0, C 순서 | `Bpcg.IO.Npy` 직접 구현 | 바이트 |
| `graph.npz`, `rivers.npz` | `bundle.py:201,215` `np.savez` | zip STORED, 로컬 zip64 extra | `Bpcg.IO.Npz` 직접 구현 | 바이트 (POSIX 에서 만든 golden) |
| manifest·scorecard·textures·strata·entrances·globe `.json` | `bundle.py:117-126` `write_json` | `json.dumps(indent=2, ensure_ascii=False, allow_nan=False)` + `"\n"`, text mode | `Bpcg.IO.PyJson` | 글 (`seconds` 제외) |
| 높이맵 `<stem>.json` | `heightmap.py:111-112` | 위와 같되 `allow_nan` 기본값, binary 쓰기 | `PyJson` | 바이트 |
| `config_digest` | `config.py:79-82` | `json.dumps(sort_keys=True, ensure_ascii=False)` → SHA-256 16진 앞 12자 | `PyJson` + `SHA256.HashData` | 문자열 |
| 높이맵·globe `.bin`, `strata.u8` | `heightmap.py:109`, `globe.py:828-834`, `corridor.py:284-286` | 머리글 없는 LE float32·uint8, C 순서 | `BinaryPrimitives` | 바이트 (NaN 정규화) |
| `textures/*.png` | `textures.py:122` `mpimg.imsave` | RGBA 8 bit PNG | `Bpcg.IO.Png` 직접 구현 | 디코딩한 픽셀 |
| `configs/**/*.toml` | `config.py:95-97` `tomllib` | TOML 1.0.0 | Tomlyn 래퍼 | 값·형·키 순서 |
| `caves.glb` | `mesh.py:218-231` trimesh | glTF 2.0 바이너리 | `Bpcg.IO.Glb` (다른 절) | 다른 절 |

#### .npy

```text
h   = "{'descr': " + repr(descr) + ", 'fortran_order': False, 'shape': " + repr(shape) + ", }"
h  += " " * (21 - len(str(shape[0])))      # ndim > 0 일 때만 (GROWTH_AXIS_MAX_DIGITS)
pad = 64 - ((10 + len(h) + 1) % 64)          # 1..64, 0 이 되지 않음
out = b"\x93NUMPY\x01\x00" + u16le(len(h) + 1 + pad) + h + " " * pad + "\n" + data
```

- 근거는 `numpy/lib/_format_impl.py:445-479`(키를 sorted 순서로, 값은 repr)와 `:397-418`(패딩)입니다. `repr(shape)` 은 `()`, `(6144,)`, `(6144, 6)`, `(6, 32, 32)` 꼴입니다.
- 버전: 길이가 65535 를 넘으면 2.0(u32 길이), latin1 로 인코딩되지 않으면 3.0(utf8)입니다. 이 프로젝트의 0~3차원 배열은 늘 1.0 이고 머리글이 128 바이트입니다. tiny 87개 파일과 0-d·`(0,)`·`(3, 0)` 실험으로 확인했습니다.
- descr: bool `|b1`, int8 `|i1`, uint8 `|u1`, int32 `<i4`, int64 `<i8`, float32 `<f4`, float64 `<f8`.
- `fortran_order` 는 F-연속이면서 C-연속이 아닌 배열만 True 입니다. 묶음은 `ascontiguousarray` 로 쓰므로 늘 False 입니다(실측). C# 도 C 순서로만 씁니다.
- 데이터는 C 순서 LE 원소이고 bool 은 0/1 바이트입니다. (N, L) 필드는 평평한 C# 배열을 그대로 쓰고 머리글에만 모양을 적습니다.
- 읽을 때는 magic, 버전 1~3, 머리글의 세 키를 확인합니다. 키 순서와 공백은 상관없습니다(numpy 는 `ast.literal_eval` 로 읽음). 바이트 순서는 `<`·`|`·`=` 만 받고, `fortran_order` 가 True 면 전치합니다. 원소가 모자라면 오류이고, 뒤에 남는 바이트는 numpy `fromfile(count)` 처럼 무시합니다.
- NaN: numpy `np.nan` 은 `0x7FF8000000000000` 이고 float32 로 바꾸면 `0x7FC00000` 입니다. tiny 출력의 NaN 원소도 모두 이 값입니다. 그런데 .NET `double.NaN` 은 `0xFFF8000000000000` 으로 부호 비트가 서 있습니다. 그래서 float 배열을 쓸 때 NaN 을 양의 quiet NaN 으로 바꿉니다.

#### .npz

`np.savez` 는 `zipfile.ZipFile(..., "w", ZIP_STORED, allowZip64=True)` 를 열고 배열마다 `open(key + ".npy", "w", force_zip64=True)` 로 씁니다(`_npyio_impl.py:756-791`). `savez_compressed` 는 `src/bpcg` 에 없고 analysis 에만 있습니다. 아래 배치로 Python 에서 손으로 만든 zip 이 tiny `planet/graph.npz`(787,410 바이트)와 바이트까지 같았습니다.

| 부분 | 내용 (LE) |
|---|---|
| 로컬 머리글 | `PK\x03\x04`, 필요 버전 45, 플래그 0, 방식 0 (STORED), 시각 `0x0000`, 날짜 `0x0021` (1980-01-01 00:00:00), CRC-32, 압축·원래 크기 `0xFFFFFFFF`, 이름 길이, extra 길이 20, 이름(ASCII) |
| 로컬 extra | `01 00 10 00` + 원래 크기 u64 + 압축 크기 u64 (zip64) |
| 데이터 | `.npy` 바이트. 데이터 서술자 없음 |
| 중앙 디렉터리 | `PK\x01\x02`, 만든 버전 `0x032D` (Unix 3, 4.5), 필요 버전 45, 플래그 0, 방식 0, 시각·날짜, CRC, 실제 압축·원래 크기, 이름 길이, extra 0, 주석 0, 디스크 0, 내부 속성 0, 외부 속성 `0x01800000` (0o600 << 16), 로컬 머리글 위치, 이름 |
| 끝 레코드 | `PK\x05\x06`, 0, 0, 항목 수 두 번, 중앙 디렉터리 크기·위치, 주석 길이 0. zip64 끝 레코드 없음 |

- 항목 순서는 키워드 인자 순서입니다(graph 는 pos, nbr, dist, area, rivers 는 cells, offsets). 4 GiB 를 넘을 때의 zip64 분기는 lab 프로필(약 0.8 GB)에서도 생기지 않으므로 C# 은 이 경우를 예외로 막아도 됩니다.
- 시각은 `ZipInfo` 기본값(1980-01-01)이라 실행마다 같은 바이트가 나옵니다(재실행 실측). Windows 의 Python 은 만든 시스템 바이트가 0 이어서 중앙 디렉터리 항목마다 그 1 바이트가 다릅니다(`zipfile/__init__.py:435-439`).
- `System.IO.Compression.ZipArchive` 로 쓰면 바이트가 달라집니다. 새 항목의 시각이 `DateTimeOffset.Now` 이고, 필요 버전이 10/20 이며, zip64 extra 가 없고, made-by 가 플랫폼을 따르기 때문입니다(dotnet/runtime release/10.0 `ZipArchiveEntry.cs`). 그래도 numpy 는 읽습니다. .NET 이 낼 수 있는 배치 9가지를 Python 으로 만들어 모두 `np.load` 로 읽었습니다. 9가지는 STORED·DEFLATE, 데이터 서술자 16·24 바이트, zip64 extra, UTF-8 플래그, 0x5455 extra, 현재 시각, made-by 0·3 의 조합입니다.
- 읽기는 `ZipArchive` 로 충분해 보입니다. `ZipBlocks.cs` 를 보면 로컬 머리글에서 서명과 길이만 읽습니다. 실제 실행 확인은 단계 1에서 합니다. CRC-32 는 IEEE 다항식(`0xEDB88320`)을 씁니다. `BitOperations.Crc32C` 는 CRC-32C 라 쓰지 않습니다.

#### JSON 쓰기 (`PyJson`)

| 쓰는 곳 | 들여쓰기 | 구분자 (원소, 키) | 그 밖 |
|---|---|---|---|
| `write_json` | 2 | `","`, `": "` | `allow_nan=False`, 끝 `"\n"`, text mode |
| 높이맵 JSON | 2 | `","`, `": "` | `allow_nan` 기본값(True), 끝 `"\n"`, binary |
| config digest | 없음 | `", "`, `": "` | `sort_keys=True`, `allow_nan` 기본값, 끝 줄바꿈 없음 |
| trimesh glb JSON | 없음 | `","`, `":"` | `ensure_ascii=True` (Glb 절) |

- 배치: 들여쓰기가 있으면 원소마다 앞에 `"\n" + " " * (2 × 깊이)` 를 두고, 닫는 괄호는 한 단계 얕은 줄에 둡니다. 빈 리스트와 dict 는 `[]`·`{}` 한 줄로 씁니다. 짧은 숫자 리스트도 원소마다 줄을 바꿉니다.
- 값: `null`, `true`, `false` 와 10진 정수(long, ulong)를 씁니다. Python 에서 int 였던 값은 C# 에서도 정수 형이어야 하고, float 였던 값은 실수 형이어야 합니다. `12` 와 `12.0` 은 다른 출력이기 때문입니다. 예를 들어 `FACE_U.tolist()` 는 `-1.0` 으로 나갑니다.
- 실수는 `float.__repr__` 규칙을 따릅니다. 최단 왕복 숫자열 d₁…dₙ 과 소수점 위치 decpt(값 = 0.d₁…dₙ × 10^decpt)를 구합니다. `-4 < decpt ≤ 16` 이면 고정 소수로 쓰고, 정수 값이면 `.0` 을 붙입니다. 그 밖은 `d₁[.d₂…dₙ]e±XX` 꼴이고 지수는 2자리 이상입니다. `0.0` 과 `-0.0` 은 그대로 씁니다. 예: `1e-05`, `0.0001`, `-0.0001`, `9999999999999998.0`, `1e+16`, `1.335e+18`, `5e-324`. 무작위 400,498 개 값에서 이 규칙과 repr 이 모두 같았습니다.
- C# 은 `double.ToString("R", CultureInfo.InvariantCulture)` 에서 숫자열과 지수만 뽑아 위 규칙으로 다시 배치합니다. "R" 은 .NET Core 3.0 부터 최단 왕복 문자열을 냅니다. 다만 .NET 의 지수 표기 경계와 꼴(`1E+16`)이 Python 과 달라 그대로 쓰지는 않습니다. float32 값은 `(double)` 로 넓힌 뒤 같은 규칙으로 씁니다(`217.03558349609375`). `float.ToString` 은 쓰지 않습니다.
- NaN·±inf: `allow_nan` 이 참이면 `NaN`·`Infinity`·`-Infinity` 로 쓰고, 거짓이면 예외를 냅니다. `write_json` 은 앞 단계 `jsonable` 이 미리 null 로 바꾸므로 예외가 나지 않습니다.
- 문자열(`ensure_ascii=False`): `"` 는 `\"`, `\` 는 `\\` 로 쓰고, `\b \f \n \r \t` 는 짧은 이스케이프로 씁니다. 나머지 U+0000–U+001F 는 `\u00xx`(소문자 16진)입니다. 0x7F, `/`, U+2028·U+2029, 한글, 이모지는 UTF-8 그대로 씁니다. `ensure_ascii=True` 면 0x20–0x7E 밖을 모두 `\uxxxx`(소문자)로 쓰고 BMP 밖은 대리쌍으로 씁니다. 짝 없는 대리 문자는 Python 이 UTF-8 인코딩에서 실패하므로 C# 도 예외를 냅니다.
- 키 순서: `sort_keys` 가 아니면 삽입 순서입니다. C# 값 모델은 `OrderedDictionary<string, object?>`(.NET 9+), `List<object?>`, `long`, `double`, `bool`, `string`, `null` 입니다. `sort_keys` 는 모든 깊이에서 code point 순으로 정렬합니다. UTF-16 ordinal 로 비교하면 U+E000–U+FFFF 와 BMP 밖 글자의 순서가 Python 과 달라지므로(Python 은 `Ａ`(U+FF21) < `😀`) `Rune` 단위로 비교합니다. 지금 키는 모두 ASCII 입니다. 묶음 필드 목록(`sorted(fields)`)은 `StringComparer.Ordinal` 로 정렬합니다.
- 파일은 BOM 없는 UTF-8(`new UTF8Encoding(false, true)`)로 쓰고 끝에 `"\n"` 을 붙입니다. BOM 이 있으면 Python `json.loads` 가 `JSONDecodeError` 를 냅니다. Python `write_json` 은 text mode 라 Windows 에서 CRLF 가 되고, 높이맵은 binary 라 늘 LF 입니다. C# 은 늘 LF 로 씁니다.
- `System.Text.Json` 의 writer 는 쓰지 않습니다. 지수 꼴, 정수 값 실수의 `.0`, 비 ASCII 이스케이프가 Python 과 다릅니다.

`jsonable`(`bundle.py:77-108`)은 `Bundle.Jsonable(value, maxArray = 1024)` 로 규칙 그대로 옮깁니다.

| 입력 | 출력 |
|---|---|
| None, bool, str | 그대로 (bool 판정이 int 보다 먼저) |
| int, np.integer | int |
| float, np.floating | `float(x)`. 유한하지 않으면 null (np.float32 는 double 로 넓힘) |
| ndarray | 원소 수 > maxArray 면 `{"__array__": [모양], "dtype": "float64"}`, 아니면 `tolist()` 를 다시 jsonable |
| dict | 키는 `str(k)`, 값은 재귀 |
| list, tuple | 길이 > maxArray 면 `{"__list__": 길이}`, 아니면 재귀 |
| Path, `__dict__` 객체, 그 밖 | `str`, `vars`, `str` (tiny 에서는 나오지 않음) |

maxArray 는 manifest·scorecard·textures·strata 가 1024 이고, entrances·globe 는 None(줄이지 않음)입니다. tiny 행성 manifest 에서는 `meta.info.bathymetry_m` 등 5개가 요약되고, 히어로 manifest 에서는 `meta.diag.extra_inflow_m3_per_yr` 가 요약됩니다. 요약 여부가 배열 크기에 달려 있어 프로필마다 manifest 모양이 다릅니다. dtype 이름은 numpy `str(dtype)` 과 같은 `float32`, `float64`, `int32`, `int64`, `uint8`, `int8`, `bool` 입니다. `read_bundle` 이 이 문자열로 npy 를 검사합니다.

#### JSON 문자열 안의 Python 서식

globe 의 설명·범례와 scorecard 의 note 는 f-string 으로 만듭니다. 쓰인 서식은 `g`, `.2g`, `.3g`, `.4g`, `.6g`, `.0f`~`.4f`, 서식 없는 `{x}`(실수면 repr), 짧은 ASCII 문자열의 `!r` 입니다. `Bpcg.IO.PyFormat` 은 double 의 정확한 십진값(BigInteger)을 half-even 으로 자르도록 구현합니다. Python 에서 같은 방식을 `format()` 과 553,420 경우 비교해 모두 같았습니다. 예: `format(0.125, ".2f")` = `0.12`, `format(-0.001, ".2f")` = `-0.00`, `format(1234567.0, "g")` = `1.23457e+06`, `format(5e-05, ".2g")` = `5e-05`. .NET `"F"`·`"G"` 서식은 동점 처리를 확인하지 못해 쓰지 않습니다.

- `globe._num(x)` = `float(f"{x:.6g}") + 0.0` 은 `double.Parse(PyFormat.General(x, 6), InvariantCulture) + 0.0` 으로 옮깁니다. `+ 0.0` 은 -0.0 을 0.0 으로 바꿉니다.
- 회랑 입구 좌표의 `ndarray.round(3)` 은 `x * 1000` → half-even → `/ 1000` 으로 계산합니다. .NET `Math.Round(x, 3, MidpointRounding.ToEven)` 도 |x| < 1e16 에서 같은 계산입니다(`Math.cs` 원본). Python `round(x, 3)` 과는 다릅니다(0.0005 → numpy 0.0, Python 0.001). 결과가 -0.0 이면 JSON 에 `-0.0` 으로 나갑니다.
- globe 색의 `int(round(v * 255))` 는 `(int)Math.Round(v * 255.0)`(기본 ToEven)으로 옮깁니다.

#### JSON 읽기

C# 은 세 곳에서 JSON 을 읽습니다. `read_manifest`(묶음에서 이어 돌리기, `config_from_manifest`), `hero_from_run`, globe 의 `_old_files` 입니다. `System.Text.Json` 의 `Utf8JsonReader` 로 위와 같은 순서 있는 값 모델을 만듭니다. 숫자 토큰에 `.`·`e`·`E` 가 있으면 double, 없으면 정수입니다. 이것은 Python `NUMBER_RE` 와 같은 기준이며, `-0` 은 정수 0, `-0.0` 은 -0.0 이 됩니다. 읽은 값은 계산에 들어갑니다. 예를 들어 히어로가 `meta.diag.geology.u_max_m_per_yr` 를 씁니다(`hero/refine.py:83-87`). 그래서 쓰기와 읽기 모두 정확히 왕복해야 합니다. `bpcg all` 은 단계마다 묶음을 디스크에서 다시 읽어, float32 로 줄인 필드와 JSON 을 거친 meta 로 다음 단계를 돌립니다. C# 도 이 왕복을 그대로 따라야 같은 결과가 나옵니다.

#### 설정 해시와 TOML

- digest 입력은 `json.dumps(cfg._data, sort_keys=True, ensure_ascii=False)` 의 UTF-8 바이트이고, 결과는 SHA-256 소문자 16진의 앞 12자입니다. 실측 값은 tiny `32a21abfa80a`, laptop `74c696596a7b`, lab `92f908ecec04` 입니다. manifest 의 `config` 로 다시 만든 Config 도 같은 값을 냅니다. 입력 앞부분은 `{"caves": {"entrance_tolerance_m": 3.0, "incision_fraction": 0.35, "levels": 2, ...` 입니다.
- digest 는 세 파일의 다음 자리에 들어갑니다. 묶음 manifest 는 `format, format_version, kind, bpcg_version, git_commit, config_digest, seed, profile, graph, …`, 회랑 manifest 는 `format, bpcg_version, git_commit, config_digest, seed, profile, frame, …`, `globe.json` 은 `format, format_version, bpcg_version, git_commit, config_digest, seed, profile, title, …` 순서입니다.
- tomllib(TOML 1.0.0)의 형: 정수는 int(크기 제한 없음), 실수는 float(밑줄을 지우고 정확히 반올림)입니다. `6_400.0` 은 float 6400.0, `6_400` 은 int 6400, `1.0e-5` 는 `1e-05`, `0.030` 은 `0.03` 이 됩니다. `inf`·`nan` 은 float 입니다. 날짜·시각은 datetime 이 되어 digest 의 `json.dumps` 가 TypeError 를 냅니다. 배열은 list, 표와 inline table 은 dict 입니다. 키 순서는 문서에 처음 나온 순서이고, 나중에 연 `[b.c]` 는 b 의 끝에 붙습니다. 지금 configs 는 표, 정수, 실수, bool, 문자열, 실수 배열만 씁니다.
- 순서 규칙(`config.py:85-121`): 먼저 행성 TOML 순서를 따릅니다. learned 를 병합할 때 있는 키는 자리를 지키고 새 키는 끝에 붙습니다. 그다음 `profile` 을 맨 끝에, `profile.name` 을 profile 의 끝에 둡니다. `with_overrides` 도 있는 키는 자리를 지키고 새 키는 끝에 붙입니다. tiny 결과는 `planet, plates, …, rivers, detail, profile{grid, hero, corridor, compute, name}` 입니다.

| 패키지 | 최신 (날짜) | 라이선스 | TOML | 키 순서 | 수 |
|---|---|---|---|---|---|
| Tomlyn | 2.10.1 (2026-07-02) | BSD-2-Clause | 1.1.0 만 | `TomlTable` 이 `List<Entry>` 로 삽입 순서 유지 | long·double, `double.TryParse(Float, Invariant)` |
| Samboy063.Tomlet | 6.2.0 (2025-12-24) | MIT | 1.0.0 | `Dictionary`, `Keys` 는 HashSet (순서 계약 없음) | long·double |
| Tommy | 3.1.2 (2022-01-11) | MIT | 1.0.0 | `Dictionary` (순서 계약 없음), 그 뒤 판 없음 | long·double |

Tomlyn 을 권합니다. Python 과 다른 점은 두 가지입니다. 첫째, TOML 1.1 전용 문법(inline table 안 줄바꿈과 끝 쉼표, `\e`·`\xHH`, 초 없는 시각, 비 ASCII bare key)을 C# 은 받고 Python 은 거부합니다. 둘째, 2⁶³ 이상 정수는 Python 만 받습니다. 지금 configs 에는 둘 다 없습니다. 차이가 드러나는 곳은 `--set` 값뿐입니다. `parse_assignment` 는 `tomllib.loads(f"v = {raw}")` 로 읽고, 실패하면 BareText 로 둡니다. 예: `x=1.`·`x=.5`·`x=01` 은 BareText, `x=2026-10-02` 는 date(형 검사에서 거부), `x=inf` 는 float(실수 자리면 거부)입니다.

#### git 커밋과 버전

- `git_commit()`(`bundle.py:60-74`)은 저장소 ROOT 에서 `git rev-parse HEAD` 를 10 s 제한으로 돌려 출력을 strip 합니다. 실행 실패, 0 이 아닌 종료 코드, 빈 출력이면 null 입니다. C# 은 `Process`(셸 없이, stdout 리디렉션)로 같게 구현하고 `Win32Exception` 도 null 로 처리합니다. 저장소가 없는 내보낸 게임에서는 null 입니다.
- `bpcg_version` 은 `"0.1.0"` 입니다(`src/bpcg/__init__.py:7`). C# 은 상수로 두고, Python 파일의 값과 같은지 테스트로 확인합니다. `AssemblyInformationalVersion` 은 .NET 8 SDK 부터 `+<커밋>` 이 붙으므로 쓰지 않습니다.

#### PNG (`bake/textures.py`)

- `mpimg.imsave(path, rgba[f], origin="lower")` 는 행을 뒤집은 뒤(`image.py:1678`) `to_rgba(bytes=True)` 를 부릅니다. float RGBA 는 NaN 이 있는 픽셀을 0 으로 만들고, 0..1 밖이면 ValueError 를 냅니다. 값은 `(xx * 255).astype(np.uint8)` 로 바꾸므로 반올림이 아니라 **버림**입니다(`colorizer.py:173-179`).
- tiny 24장의 98,304 바이트가 버림 공식과 모두 같았고, 반올림으로 계산하면 22,690 바이트가 달랐습니다. `NAN_RGBA` 의 0.55 는 140 이 됩니다.
- 실측 chunk 는 IHDR(RGBA 8 bit, 색 형식 6, 비월 없음), tEXt `Software` = `Matplotlib version3.11.2, https://matplotlib.org/`, pHYs 3937×3937 /m(100 dpi), IDAT 1개(zlib `78 9C`, 줄마다 필터 1·2·4), IEND 입니다. Pillow 기본값은 `compress_level=-1`, `optimize=False` 입니다.
- C# 은 `(byte)(v * 255.0)`(double 로 곱한 뒤 버림), NaN → 0, 범위 밖이면 예외, 행 뒤집기로 픽셀을 만듭니다. 인코더(IHDR, pHYs, IDAT = `ZLibStream`, IEND, CRC-32)는 직접 씁니다. .NET 문서에 따르면 압축 바이트는 판과 엔진에 따라 다를 수 있으므로, 디코딩한 픽셀로 비교합니다. tEXt `Software` 에는 matplotlib 이름을 쓰지 않습니다.
- `volume/slices.py` 의 `_save_png`(matplotlib 축·눈금 그림)는 `tests/test_volume.py` 와 `analysis/figures/render_results.py` 에서만 부릅니다. 그래서 RGB 배열을 만드는 `vertical_slice` 만 옮기고 축 그림은 옮기지 않기를 제안합니다.

#### 머리글 없는 `.bin`·`.u8`

| 파일 | 원소 | 배열 순서 | 근거 |
|---|---|---|---|
| 높이맵 `<stem>.bin` | float32 LE | (height, width). 행은 북 → 남, 열은 서 → 동 | `heightmap.py:86,109` |
| `strata.u8` | uint8 | (행, 층, 열), C 순서 | `corridor.py:280-286` |
| globe `<필드>.bin`, `rivers.bin`, `lakes.bin` | float32 또는 uint8 | (6, N, N), [면][행][열] | `globe.py:828-834,952` |
| `corners_elevation_m.bin` | float32 | (6, N+1, N+1) | `globe.py:953` |

- float64 → float32 변환은 IEEE 최근접 짝수 반올림이라 C# `(float)d` 와 같습니다. 높이맵은 변환한 뒤 NaN·inf 가 있으면 ValueError 를 냅니다(범위를 넘어 inf 가 된 값 포함). globe 연속 필드에서는 NaN 이 '값 없음' 입니다.
- 쓸 때는 `<이름>.part` 에 쓴 뒤 `os.replace` 로 바꿉니다. C# 은 `File.Move(tmp, dst, overwrite: true)` 를 씁니다. 순서도 따릅니다: 높이맵은 `.bin` 다음 `.json`, globe 는 `.bin` 들 → `globe.json` → 묵은 `.bin` 지우기, 회랑은 `manifest.json` 을 마지막에 씁니다. `np.save`·`np.savez` 는 `.part` 없이 바로 씁니다.
- LE 는 `BinaryPrimitives` 로 명시합니다. 또는 `BitConverter.IsLittleEndian` 을 확인한 뒤 `MemoryMarshal.AsBytes` 로 씁니다.

#### 대조에서 뺄 것

- 모든 깊이의 `seconds` 키입니다. 행성 manifest 에 6곳, 히어로 manifest 에 4곳, 회랑 manifest 와 globe.json 에 각 1곳 있습니다(실측). 이것만 빼면 재실행 결과가 같습니다.
- `git_commit`: golden 을 만든 커밋과 비교하는 쪽의 커밋이 다를 때 뺍니다.
- Windows 에서 만든 golden: `write_json` 파일의 CRLF 와 npz 중앙 디렉터리의 made-by 바이트를 뺍니다.
- NaN 의 부호와 payload: x86 에서 연산으로 생긴 NaN 은 부호 비트가 섭니다.

#### Python 쪽 의심 버그 (진행 기록 후보, 고치지 않음)

1. `write_json` 은 text mode 라 Windows 에서 CRLF 로 쓰는데, 높이맵 JSON 은 LF 로 씁니다.
2. `Config.digest` 는 `allow_nan` 기본값을 써서 TOML 의 nan·inf 가 `NaN` 으로 들어갑니다. 반면 manifest 의 `config` 는 `jsonable` 이 null 로 바꾸므로, 다시 만든 Config 의 digest 가 달라집니다. TOML 날짜는 digest 에서 TypeError 를 냅니다. 지금 configs 에는 둘 다 없습니다.
3. `np.save`·`np.savez` 만 임시 파일 없이 씁니다. 중간에 멈추면 반쯤 쓴 파일이 남습니다.

## 5. 공통 수치 규칙 (numpy·numba)

이 장의 분석은 에이전트가 거듭 중단되어 결과가 없습니다(10장 D7). 지금까지 확인된 공통 규칙은 6장 각 절에 흩어져 있고, 단계 1에서 `Bpcg.Numerics` 도우미를 만들며 여기에 모읍니다.

- **pairwise 합.** numpy `sum`·`mean`은 pairwise 합이고 numba 커널 안의 합은 순차 합입니다. `graph.spacing`처럼 하류로 이어지는 값은 pairwise 를 그대로 옮깁니다(6장 core ②).
  - 정확한 순서(단계 1 실험, numpy 2.5.3): 1차원 연속 배열의 `np.add.reduce(x)` 는 `0.0 + pw(x)` 입니다. `pw` 는 길이 8 미만이면 0.0 에서 차례로 더하고, 128 이하이면 누적기 8개(`r[k] += x[i+k]`)를 `((r0+r1)+(r2+r3))+((r4+r5)+(r6+r7))` 로 합친 뒤 나머지를 차례로 더하며, 그보다 길면 `n2 = n/2 − (n/2 % 8)` 에서 나눠 재귀합니다. 길이 0~300 과 511·512·513·1000·1536·4096·6144·10000·65537 의 무작위 배열(크기 10⁻³~10³ 섞음)에서 float64·float32 모두 비트 단위로 같았습니다. 모두 −0.0 인 배열의 합은 +0.0 입니다. `np.mean` 은 이 합을 원소 수로 나눈 값과 같습니다. C# 은 `Bpcg.Numerics.NpReduce`.
  - 길이 3 같은 짧은 축의 합(`np.sum(a*b, axis=-1)`, `norm(axis=-1)`)은 +0.0 에서 시작하는 순차 합입니다(`LinAlg.Dot3Np`·`Norm3`). numba 커널 안의 `ax*bx + ay*by + az*bz` 는 0.0 에서 시작하지 않습니다(`LinAlg.Dot3Numba`).
- **초월 함수.** 이 맥(macOS arm64)에서 numpy·numba·Python `math`의 float64 `tan`·`arctan`·`arctan2`·`sin`은 Apple libm과 비트 단위로 같았고, .NET 10 `Math.*`도 CRT libm을 부릅니다(6장 core ②, geology). 그래서 macOS에서는 초월 함수를 거친 값도 비트 일치를 기대하고, 다른 OS에서는 근거를 단 허용 오차를 씁니다.
- **FMA.** numba는 `a*b + c`를 FMA로 묶지 않습니다(6장 core ①·②). C#도 `Math.FusedMultiplyAdd`를 쓰지 않습니다.
- **부호 있는 0.** 길이 3 축 합은 +0.0에서 시작하는 순차 합입니다. 성분을 골라 쓰는 최적화는 −0.0을 바꿉니다(6장 core ②).
- **정수 변환.** `np.floor(...).astype(int64)`은 C# `(long)Math.Floor(x)`로 옮깁니다. NaN·inf는 .NET 9 이상과 arm64 numpy가 같이 포화합니다(6장 core ①·②).

## 6. 모듈별 수치 함정

각 절은 단계 0 분석의 결과입니다. 제목 끝에 '(비평 거침)'이 없는 절은 독립 비평 없이 한 번 분석한 초안입니다.

### core ① — config·constants·fields·paths·hashing·noise (비평 거침)

범위는 8개 파일 708줄입니다: `src/bpcg/__init__.py`(7), `core/__init__.py`(4), `core/config.py`(246), `core/constants.py`(27), `core/fields.py`(101), `core/paths.py`(28), `core/hashing.py`(49), `core/noise.py`(246). 실험은 저장소 venv(Python 3.13.15, numpy 2.5.3, numba 0.68.0, macOS arm64)에서 했습니다. Tomlyn과 Godot의 동작은 태그 소스(Tomlyn `2.10.1`, Godot `4.7.2-stable`)를 읽어 확인했고, .NET에서 실행해 본 것은 없습니다.

- **해시·노이즈는 비트 단위로 같습니다.** FMA 없는 IEEE 순서 계산(unchecked ulong, `Math.Floor` 뒤 `(long)`, 같은 식 순서)으로 짠 Python 참조와 numba 결과를 맞대었습니다.
    - 1차 분석: `hash3`·`hash_unit` 2,004쌍, `hash_uniform_array` 1,005칸, `gradient_noise3` 35,025건, `fbm3`·`vector_fbm3` 26가지 조합이 모두 같았습니다.
    - 재검증(따로 짠 참조): `hash3`·`hash_unit` 720쌍, `gradient_noise3` 18,072건(−0.0, 1e19, 2^52+0.5 포함), test_noise.py의 GOLDEN 값 전부(`==`), 스레드 1·3·12개의 10만 점 `fbm3`·`vector_fbm3`가 모두 같았습니다.
    - `_noise3_mixed`·`_fbm_kernel` 어셈블리에 `fmadd`·`fmla` 류가 0개입니다. 그래서 이 묶음의 대조는 모두 exact입니다.
- **가장 큰 위험은 config입니다.** `config_digest`는 `json.dumps(sort_keys=True, ensure_ascii=False)`의 글자를 그대로 만들어야 하고, int·float 형과 dict 순서도 지켜야 합니다. earth와 tiny·laptop·lab의 digest는 `32a21abfa80a`·`74c696596a7b`·`92f908ecec04`이고, `out/tiny_check`의 planet·hero·corridor·globe 기록과 같습니다.
- **TOML 읽기가 새 쟁점입니다.** Tomlyn 2.10.1은 2^63 이상 2^64 미만의 양의 정수를 오류 없이 음수 `long`으로 감고, TOML 1.1만 받으며, 맨 앞 BOM을 조용히 뗍니다. Tomlyn을 쓰려면 정수 원문 검사를 더해야 하고, 대안은 tomllib(MIT, 810줄)을 옮기는 것입니다.
- **이 묶음에 없는 함정.** 음수 정수 나눗셈·나머지, 배열 정렬, numpy 축약(sum·mean 등), 백분위수, 초월 함수, 팬시 인덱스, 음수 인덱스는 없습니다.

#### (a) 대응표

`OD`는 `System.Collections.Generic.OrderedDictionary<string, object?>`(.NET 9 이상)입니다. 설정 값 모델은 `OD`(절), `List<object?>`(배열), `long`, `double`, `bool`, `string`, `null`(manifest에서만), 날짜·시각 값 형(거부용)뿐입니다.

| Python | C# 파일 | 형 | 멤버 (C# 서명) |
|---|---|---|---|
| `bpcg.__version__` | `csharp/Bpcg/Package.cs` | `static class Package` | `const string Version = "0.1.0"` (bundle·heightmap·corridor·globe의 `bpcg_version`, 어셈블리 버전에서 끌어오지 않음) |
| `bpcg.core` `__init__` | 없음 | — | docstring뿐이라 파일을 만들지 않습니다 |
| `Section` | `csharp/Bpcg/Core/Config.cs` | `class Section` | `object? this[string key]`(`__getattr__`, 없으면 `KeyNotFoundException`, dict는 `Section`), `bool Contains(string key)`, `object? Get(string key, object? @default = null)`(`get`, 저장된 null은 null, dict 기본값도 `Section`), 형별 읽기 `Section Sec(string)`·`double F(string)`·`long I(string)`·`bool B(string)`·`string S(string)`·`IReadOnlyList<object?> L(string)`·`double[] FArray(string)`, 예외 없는 `bool TryF(string dotted, out double v)`, `OD AsDict()`(깊은 복사), `ToString()` |
| `Config` | 같은 파일 | `sealed class Config : Section` | `new object? this[string dotted]`(`__getitem__`, `Split('.')`, 빈 조각 유지), `Config WithOverrides(IEnumerable<KeyValuePair<string, object?>>)`(순서 유지, 깊은 복사, `int`→`long`, 배열→`List<object?>`, `BareText`→`string`), `string Digest()`, `static Config FromDict(OD data)`(`Config(data)`, bundle의 `config_from_manifest`용) |
| `load_config`, `parse_assignment`, `checked_overrides`, `FIXED_KEYS` | 같은 파일 | `Config`의 static | `static Config LoadConfig(string planet = "earth", string profile = "laptop", IEnumerable<KeyValuePair<string, object?>>? overrides = null)`, `static (string Key, object Value) ParseAssignment(string text)`, `static OD CheckedOverrides(Config cfg, IEnumerable<KeyValuePair<string, object?>> overrides)`, `static IReadOnlyDictionary<string, string> FixedKeys` |
| `BareText` | 같은 파일 | `sealed record BareText(string Text)` | — |
| `_merge`, `_read`, `_kind`, `_finite`, `_coerce`, `_all_keys` | 같은 파일 | `Config`의 private static | `OD Merge(OD @base, OD extra)`, `OD ReadToml(string path)`, `string Kind(object? v)`(그 밖 형은 Python 형 이름 `dict`·`NoneType`·`date`), `double Finite(string key, double v)`, `object Coerce(string key, object? old, object? @new)`, `List<string> AllKeys(OD data, string prefix = "")` |
| `difflib.get_close_matches` 대체 | 같은 파일 | `private static class DiffLib` | `List<string> CloseMatches(string word, IReadOnlyList<string> possibilities, int n = 3, double cutoff = 0.6)`, `double Ratio(int[] a, int[] b)`(code point 배열) |
| `G_GRAV`, `SECONDS_PER_YEAR`, `RHO_WATER`, `MU_WATER`, `gravity` | `csharp/Bpcg/Core/Constants.cs` | `static class Constants` | `const double GGrav = 6.674e-11, SecondsPerYear = 3.15576e7, RhoWater = 1000.0, MuWater = 1e-3`, `double Gravity(double radiusM, double densityKgM3)` |
| `FieldInfo`, `_f` | `csharp/Bpcg/Core/Fields.cs` | `sealed record FieldInfo(string Group, string Unit, string Dtype, string Description)` | `_f`는 생성자로 대신합니다 |
| `FIELDS`, `GROUPS`, `check_fields` | 같은 파일 | `static class Fields` | `IReadOnlyDictionary<string, FieldInfo> All`(51개, 삽입 순서, `Fields.Fields`는 CS0542라 `All`), `IReadOnlyList<string> Groups`(포팅 코드에서 쓰는 곳 없음), `void CheckFields(IEnumerable<string> names)`, 새 `Type ElementType(string dtype)` |
| `ROOT`, `DATA`, `OUT`, `CONFIGS`, `PILOT`, `EXTERNAL`, `DERIVED`, `CACHE`, `_find_root` | `csharp/Bpcg/Core/Paths.cs` | `static class Paths` | `string Root, Data, Out, Configs, Pilot, External, Derived, Cache`(속성), private `string FindRoot()`, 새 `void Configure(string? root = null, string? configs = null, string? @out = null, string? data = null)` |
| `splitmix64`, `hash3`, `hash_unit`, `hash_uniform_array`, `_M1`·`_M2`·`_GOLD`·`_S*`·`_INV53` | `csharp/Bpcg/Core/Hashing.cs` | `static class Hashing` | `ulong SplitMix64(ulong x)`, `ulong Hash3(long a, long b, long c)`, `double HashUnit(long a, long b, long c)`(앞 셋은 `[MethodImpl(AggressiveInlining)]`), `double[] HashUniformArray(long[] ids, long seed, long stream)`, private `const ulong M1, M2, Gold`, `const double Inv53 = 1.0 / 9007199254740992.0` |
| `gradient_noise3`, `fbm3`, `vector_fbm3` | `csharp/Bpcg/Core/Noise.cs` | `static class Noise` | `double GradientNoise3(double x, double y, double z, long seed)`, `double[] Fbm3(double[] points, long seed, int octaves = 5, double gain = 0.5, double lacunarity = 2.0, double frequency = 1.0)`((M,3) 행 우선 → (M,)), `double[] VectorFbm3(...)`(→ (M,3) 행 우선) |
| `_fade`, `_grad_dot`, `_noise3_mixed`, `_fbm_kernel`, `_check_points`, `_octave_tables`, `_check_seed`, `_GRAD3`, `_S32`, `_N_GRAD`, `_STREAM_*`, `_OFFSET_SPAN` | 같은 파일 | `Noise`의 private | `double Fade(double t)`, `double GradDot(ulong h, double dx, double dy, double dz)`, `double Noise3Mixed(double x, double y, double z, ulong s)`, `void FbmKernel(double[] points, ulong[] seeds, double[] offsets, double[] freqs, double[] amps, double invNorm, double[] @out, int nComp, int nOct)`, `void CheckPoints(double[] points)`, `(ulong[] Seeds, double[] Offsets, double[] Freqs, double[] Amps, double InvNorm) OctaveTables(long seed, int nComp, int octaves, double gain, double lacunarity, double frequency)`, `double[] Grad3`(12×3), `const long StreamOctave = 201, StreamOffset = 210, StreamVector = 220`, `const double OffsetSpan = 64.0`. `_check_seed`는 `long` 형이 대신합니다 |

- **이름.** `config.py`의 `Config` 클래스가 모듈 정적 클래스와 이름이 겹쳐, 모듈 함수를 `Config`의 static으로 둡니다. 이름은 규칙대로 기계적으로 바꿉니다(`load_config`→`LoadConfig`, `get`→`Get`, `__getattr__`·`__getitem__`→인덱서). 분석 초안의 `Load`·`GetOr`보다 Python 이름으로 찾기 쉽습니다.
- **형별 읽기.** `F`·`I`·`B`는 호출부의 `float(v)`·`int(v)`·`bool(v)`를 따릅니다. `I`는 실수를 0 쪽으로 자르되, NaN·inf·int64 밖이면 포화하지 않고 예외를 냅니다. `TryF`는 `bake/globe.py:_cfg_float`(AttributeError·TypeError·ValueError → None)를 위한 것이고, `bake/detail.py:detail_settings`는 `Contains`와 `Get`으로 옮깁니다.
- **예외.** Python은 없는 키에 AttributeError(점 접근)나 KeyError(`cfg["a.b"]`)를 내고, dict가 아닌 중간 노드에 TypeError(`__getitem__`, `with_overrides`)를 냅니다. C#은 각각 `KeyNotFoundException`과 `InvalidOperationException`으로 둡니다. 검사를 거친 경로에서는 나오지 않습니다.

#### (b) 수치 함정 표

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| `config.py:81` | json-float-repr | 실수는 `float.__repr__`입니다: 최단 왕복 자릿수, 지수 E가 −4 ≤ E < 16이면 고정 소수점(정수값은 `.0`), 그 밖은 `1e-05`·`1.335e+18`(부호와 두 자리 이상 지수), `-0.0`, `NaN`·`Infinity`·`-Infinity`. .NET `"R"`은 `1E-05`·`6371000`·`-0`을 냅니다 | `PyJson.FormatDouble`이 `"R"`(InvariantCulture)의 부호·자릿수·지수를 Python 규칙으로 다시 배치합니다. 자릿수가 어긋나면 Ryu나 CPython dtoa를 옮깁니다 | exact |
| `config.py:81` | json-layout | 구분자 `', '`·`': '`, 모든 깊이(리스트 안 dict 포함)의 키를 코드 포인트 순으로 정렬, 비ASCII 원문, 이스케이프는 `"`, `\`, `\b`, `\f`, `\n`, `\r`, `\t`와 나머지 0x00–0x1f(`\u001c`처럼 소문자)뿐(0x7f·U+2028은 원문), `true`·`false`·`null` | System.Text.Json writer 대신 직접 짭니다. 키 정렬은 `string.CompareOrdinal`입니다(BMP 밖 글자에서만 다르고 설정 키는 ASCII) | exact |
| `config.py:82` | hex-case | `hexdigest()`는 소문자, `Convert.ToHexString`은 대문자입니다 | `Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..12]`(.NET 9 이상, BOM 없음) | exact |
| `config.py:79-82`, `184-186`; `bundle.py:288-293` | value-type | digest는 int·float 형을 따릅니다(`k_ref=150`은 `4d56e71c846b`, `150.0`은 `32a21abfa80a`). hero·bake·globe는 manifest JSON에서 되읽은 설정을 쓰고, C#이 쓴 manifest를 Python studio·bake가 읽기도 합니다 | `long`·`double`을 구분합니다. 정수값 실수도 `6371000.0`으로 씁니다. JSON 숫자는 글자에 `.`·`e`·`E`가 있으면 double, 없으면 long으로 읽습니다(`-0`은 0) | exact |
| `config.py:97`, `143` | toml-int-wrap | Tomlyn 2.10.1 `Lexer.TryParseDecimalInt64`(L999–1058)는 양수의 int64 상한을 보지 않아 `9223372036854775808`→`long.MinValue`, `18446744073709551615`→`-1`이 됩니다. 16진·8진·2진(L720–775)도 64비트까지 감습니다. 2^64 이상만 오류입니다. tomllib은 크기 제한 없는 int입니다 | Tomlyn을 쓰면 `TomlParser` 사건의 `Span`으로 정수 원문을 다시 읽어 int64 범위를 검사하고, 넘으면 TOML 오류로 둡니다(`--set`에서는 BareText가 되어 거부, 의도한 차이). 대안은 tomllib 이식입니다 | exact |
| `config.py:95-97`, `143-145` | toml-library | tomllib은 TOML 1.0, Tomlyn 2.x는 TOML 1.1 전용입니다(`\e`·`\xHH`, 여러 줄·끝 쉼표 inline table, 초 없는 시각). 차이는 `--set`의 글자 자리 결과뿐이고 글자 자리는 지금 모두 FIXED_KEYS입니다. BOM은 tomllib이 거부하고 Tomlyn `TomlParser.Create`는 조용히 뗍니다(L77, L104). tomllib은 날짜·시각을 `date`·`time` 객체로 돌려줍니다(`07:32`는 BareText) | 바이트를 `new UTF8Encoding(false, true)`로 풀고, U+FEFF로 시작하면 파서 전에 오류를 냅니다. `TomlTable`·`TomlArray`·`TomlTableArray`를 값 모델로 바꿉니다. 날짜·시각은 별도 값 형으로 넘겨 `Coerce`가 거부하게 하고, `LoadConfig`는 바로 거부합니다(Python은 digest에서 TypeError, 의도한 차이) | exact |
| `config.py:72-76`, `85-92`, `118-119` | dict-order | manifest `config`는 정렬 없이 씁니다(`bundle.py:234`). 순서는 TOML 문서 순서이고, `[a.b]`가 `[a]`보다 앞이면 처음 나온 순서입니다. `_merge`·`with_overrides`는 기존 키 자리를 지키고 새 키·절을 뒤에 붙여 `profile`이 맨 뒤 절입니다. `profile.name`은 프로필 파일에 `name`이 없을 때만 절 맨 뒤에 붙고, 있으면 그 자리에서 값만 stem으로 바뀝니다 | `OD`를 씁니다(기존 키 대입은 자리 유지). 파서 결과의 삽입 순서대로 옮깁니다 | exact |
| `config.py:33-34`, `69-77` | aliasing | `with_overrides`는 넣는 값을 복사하지 않고, `Section`은 같은 dict를 감쌉니다 | 넣을 때 깊이 복사하고 setter를 두지 않습니다. 포팅 코드에서는 결과 차이가 없습니다 | n/a |
| `config.py:106-113`, `119` | path-string | `endswith(".toml")`은 서수 비교이고 대소문자를 가립니다(`X.TOML`은 이름 취급). `Path.stem`은 `a.b.toml`→`a.b`, `.toml`→`.toml`(C#은 `""`). 상대 경로는 cwd 기준입니다 | `EndsWith(".toml", StringComparison.Ordinal)`, `Path.GetFileNameWithoutExtension`(`.toml` 단독만 다르므로 무시) | exact |
| `config.py:107`, `112`(+ `cli.py:78`) | path-combine | pathlib `/`는 오른쪽이 절대 경로면 왼쪽을 버립니다(`CONFIGS / "planets" / "/abs/earth.toml"` → `/abs/earth.toml`) | 같은 규칙인 `Path.Combine`을 씁니다. `Path.Join`은 그대로 이어 붙여 다릅니다 | exact |
| `config.py:115-117` | config-source | learned는 `CONFIGS/learned/<행성 파일 이름>`이 있을 때만 합칩니다. 행성을 경로로 줘도 learned는 CONFIGS에서 찾습니다 | 설정 원천의 `TryRead("learned/" + name)` | exact |
| `config.py:137-146` | cli-parse | 첫 `=`에서 나누고 `strip()`합니다. Python 공백 29자에는 U+001C–U+001F가 있지만 .NET `Trim()`(25자)에는 없습니다. 값은 `"v = " + raw` 문서로 읽으므로 뒤 주석은 무시되고, 줄바꿈 뒤 다른 키도 통과합니다(같은 키면 BareText). 디코드 오류일 때만 BareText입니다 | `PyStrip`(29자)으로 자릅니다. 같은 문서 문자열을 파서로 읽고 TOML 오류 형만 잡아 BareText로 둡니다 | exact |
| `config.py:169-201` | coerce-int-range | 정수 자리는 유한한 정수값 실수를 `int(new)`로 받습니다. Python int는 크기 제한이 없어 `1e20`→10^20, `-0.0`→0입니다. 실수 자리의 `float(new)`는 \|int\| ≥ 2^1024에서 ValueError가 아닌 OverflowError를 냅니다((e) 2). 리스트는 길이를 맞추고 원소마다 재귀합니다(빈 리스트는 최상위 float 유한성만) | 같은 순서(BareText→bool→int→float→str→list)로 검사하고, 정수로 바꾸기 전에 \|x\| < 2^63을 확인합니다. 이 검사는 TOML 층에서 이미 감긴 값을 못 잡으므로 toml-int-wrap 대책이 함께 있어야 합니다 | exact |
| `config.py:239` | difflib-hint | ratio = 2M/T이고 M은 `find_longest_match` 탐욕 재귀로 셉니다(LCS 아님). 결과는 (ratio, 키) 내림차순 상위 3개입니다. 단어가 200자 이상이면 autojunk가 걸리지만, 인접 확장 단계에서는 흔한 글자도 붙습니다. 예: `plates.speed` → `[planet.seed, plates.speed_m_per_yr, plates.warp_frequency]` | CPython 3.13의 `__chain_b`·`find_longest_match`·`get_matching_blocks`를 그대로 옮깁니다. `real_quick_ratio`·`quick_ratio`는 상한이라 생략해도 같습니다. 정렬은 ratio 내림차순, 같으면 키 내림차순(`CompareOrdinal`) | exact |
| `config.py:239` | difflib-codepoint | Python은 code point로 셉니다. C# `string`은 UTF-16이라 BMP 밖 글자가 둘로 셉니다. `'caves.levels'`+이모지 10개는 Python 힌트가 `[caves.levels]`이고 UTF-16 흉내는 `[]`입니다 | `EnumerateRunes()`로 `int[]`를 만들어 비교합니다 | exact |
| `config.py:233-246`(+ `cli.py:42-68`) | iteration-order | 넣은 순서대로 검사하고 돌려줍니다. 같은 키를 `--set`으로 두 번 주면 뒤 값이 이기고 자리는 처음 것입니다. `--seed`는 `planet.seed`에 대입합니다 | 덮어쓰기를 `OD`로 모읍니다 | exact |
| `config.py:36-51`, `63-67` | getter-semantics | `get`은 저장된 None을 기본값 대신 돌려주고, dict 기본값도 `Section`으로 감쌉니다. 호출부 `int(v)`는 0 쪽으로 자르고 NaN·inf에서 예외를 냅니다. `str(cfg.profile.get("name") or "laptop")`은 None과 `""`를 모두 기본값으로 바꿉니다 | `Get`은 '없음'과 'null'을 구분합니다. `I`는 포화하지 않습니다. `or` 꼴은 호출부에서 `string.IsNullOrEmpty`로 옮깁니다 | exact |
| `paths.py:12-17` | root-discovery | `__file__`(심볼릭 링크를 푼 소스 경로)의 부모에서 `pyproject.toml`과 `src/bpcg`가 함께 있는 첫 폴더를 찾고, 없으면 import 때의 cwd입니다. Godot 4.7.2는 프로젝트·의존 어셈블리를 `LoadFromStream`으로 읽어 `Assembly.Location`이 빈 문자열이고, `AppContext.BaseDirectory`가 비어 있을 때만 프로젝트 어셈블리 폴더로 채웁니다(`PluginLoadContext.cs` L24–69). 내보낸 게임의 기준 폴더는 `data_*` 폴더입니다 | 순서: `Paths.Configure` 값 → `[CallerFilePath]`로 받은 `Paths.cs` 소스 경로의 부모(`__file__`에 가장 가깝고, CI의 PathMap이면 실패) → `AppContext.BaseDirectory`의 부모 → cwd. `Assembly.Location`은 쓰지 않습니다 | n/a |
| `paths.py:21-23` | env-dependent | `BPCG_DATA`·`BPCG_OUT`은 import 때 한 번 읽고, 빈 문자열은 `.`, 상대 경로는 쓸 때의 cwd 기준입니다. CONFIGS는 바꿀 수 없습니다. 포팅 코드는 `CONFIGS`(config), `OUT`(cli), `ROOT`(git 커밋, `engine/baked`)만 쓰고, `DATA` 아래 경로는 analysis·tools·tests만 씁니다 | 정적 초기화에서 한 번 읽고 `""`는 `"."`으로 둡니다. `Path.GetFullPath`로 미리 고정하지 않습니다. golden 경로도 `Paths.Out`을 따릅니다 | n/a |
| `hashing.py:21-26` | uint64-overflow | 덧셈·곱셈은 넘친 비트를 버리고 `>>`는 논리 시프트입니다 | `unchecked` 블록에서 `ulong`으로만 계산합니다(상수 `UL`, 시프트 수 int). 해시를 `long`에 담아 `>>`하지 않습니다 | exact |
| `hashing.py:32-34`, `noise.py:73-80`, `110` | int64-to-uint64 | numba `np.uint64(int64)`는 비트 재해석이고 `ix + 1`은 int64 넘침을 감습니다. Python 쪽 `np.uint64(-5)`는 OverflowError입니다 | `unchecked((ulong)a)`, `unchecked(ix + 1)` | exact |
| `hashing.py:30-34` 호출부 | nep50-boxing | Python에서 부른 `hash3`는 Python int를 돌려주고, `>> np.uint64(k)`는 np.uint64라 정확합니다(plates:72, model:176, solver:429, sample:96, detail:72, domain:79) | `(long)(Hashing.Hash3(...) >> k)` | exact |
| `hashing.py:40` | exact-conversion | `h >> 11` < 2^53이라 double 변환과 2^−53 곱이 모두 정확합니다(arm64는 `ucvtf #53` 한 명령) | `(double)(h >> 11) * Inv53` | exact |
| `hashing.py:44-49` | numba-typing | 2^63 이상 Python int 시드는 uint64로 받아 `np.int64`에서 감깁니다(2^64 이상은 OverflowError). 실수 ids는 0 쪽으로 자릅니다 | `long[] ids, long seed, long stream` | exact |
| `noise.py:62-70` | floor-cast | `np.floor` 뒤 int64입니다(arm64는 `frintm`과 `fcvtms`). `tx = x - fx`를 실수 fx로 계산해야 −0.0 좌표에서 +0.0이 나옵니다. 2^63 이상 좌표는 arm64 numba와 .NET 9+가 같이 포화하고, x86 numba는 `cvttsd2si`라 INT64_MIN이 될 것입니다(x86에서는 확인 못 함) | `double fx = Math.Floor(x); long ix = (long)fx; double tx = x - fx;` | exact |
| `noise.py:51-56` | signed-zero | 성분이 0인 항도 곱하고 더합니다(`0.0 × 음수` = −0.0). `k = ((h >> 32) * 12) >> 32`는 uint64입니다 | 세 곱과 두 덧셈을 왼쪽부터 하고 0 항을 생략하지 않습니다. `int k = (int)(((h >> 32) * 12UL) >> 32)` | exact |
| `noise.py:45-48`, `91-100`, `133-141` | fma-evaluation-order | numba는 `_fade`, 보간 `n0 + u * (n1 - n0)`, `px * f + offset`을 FMA로 합치지 않습니다 | 같은 식과 결합 순서로 쓰고, `Math.FusedMultiplyAdd`와 식 재배열을 쓰지 않습니다 | exact |
| `noise.py:185-198`, `141` | accumulation-order | 주파수·진폭은 `f *= lacunarity`, `a *= gain`의 반복이고, `total`은 앞에서부터 더하며 `inv_norm = 1.0 / total`입니다. 커널은 `acc * inv_norm`으로 곱합니다(나누지 않음) | 같은 반복문(`Math.Pow` 금지), `acc * invNorm` | exact |
| `noise.py:171-184` | seed-derivation | fbm3는 시드 그대로, vector는 `hash3(seed, c, 220)`을 int64로 재해석합니다. 옥타브 시드는 `hash3(comp, o, 201)`, 이동은 `hash_unit(comp, o, 210 + ax) × 64`입니다 | `long comp = nComp == 1 ? seed : unchecked((long)Hashing.Hash3(seed, c, 220))` | exact |
| `noise.py:113-141` | prange | 점마다 `out[k, c]`만 쓰고, `acc`는 지역 변수이며, 옥타브 합은 순차입니다 | `Parallel.For(0, M, ...)`, 공유 누적 없음. 병렬도 1과 기본값의 비트 일치를 시험합니다 | exact |
| `noise.py:153-159`, `221-223`, `244-246` | dtype-layout | float64 연속 배열로 바꾸고 (M, 3) 모양과 유한성을 검사합니다. `(3,)` 1차원 입력은 거부합니다 | 행 우선 `double[]`(길이 3M)로 받고 길이 % 3과 `double.IsFinite`를 검사합니다. 길이 3인 배열은 (1, 3)으로 받아들여집니다(의도한 차이, 포팅 호출부에는 없음). 좌표를 float32로 만든 호출부가 있으면 그 계산을 재현한 뒤 넓힙니다(호출부 담당 표에서 확인) | exact |
| `constants.py:27` | evaluation-order | `4.0 / 3.0 * π * G * ρ * R`을 왼쪽부터 곱합니다(지구 9.820852275618668, 비트 `0x4023a446bfdd423b`) | 같은 순서로 씁니다. Roslyn이 앞의 상수 부분을 미리 접어도 IEEE 결과는 같습니다 | exact |
| `constants.py:9-12` 호출부 | nep50-scalar | 상수는 Python float라 float32 배열과 연산하면 float32로 남지만, C# `const double`은 double로 올립니다. 지금 호출부(water:333, groundwater:100, globe:724·778)의 피연산자는 float64입니다. `groundwater.py:100`의 `/ MU_WATER`를 `* 1000`으로 바꾸면 무작위 1만 건 중 1,274건이 달라집니다 | 상수는 double로 두고 float32 피연산자일 때만 `(float)`로 바꿉니다. 호출부 식은 연산과 순서를 그대로 둡니다 | n/a |
| `fields.py:23-90` | dtype-mapping | float32 32, uint8 7, bool 6, float64 3, int32 2, int8 1(`subduction_side`)입니다. 지시문의 다섯 형에 bool·int8이 없습니다. `FieldInfo`에는 모양이 없고 `strata_*`만 (N, L)·(N, L+1)입니다(description에만 적힘) | `bool[]`·`sbyte[]`, manifest dtype `"bool"`·`"int8"`, npy `\|b1`·`\|i1`. 평평한 배열에는 열 수를 따로 넘깁니다 | exact |
| `fields.py:97` | string-sort | `sorted(set(...))`는 코드 포인트 순입니다(`['Aa', 'a', 'zz']`) | `StringComparer.Ordinal` | exact |
| `fields.py:50` 등 | unicode-text | unit `°C`(유일한 비ASCII unit)와 한국어 description이 manifest에 원문으로 들어갑니다 | 소스를 UTF-8로 두고 글자 그대로 옮깁니다 | exact |
| `config.py:40`, `141`, `165`, `173-174`, `193`, `201`(+ `cli.py:67`, `174`) | message-format | 메시지·로그는 Python repr입니다. 유한 실수는 JSON과 같지만 비유한값은 `inf`·`-inf`·`nan`(JSON은 `Infinity`·`NaN`)이고, 글자는 작은따옴표, 참거짓은 `True`·`False`입니다 | `PyRepr`를 따로 둡니다. 시험은 핵심어만 봅니다(`실수`, `정수`, `절`, `원소 2개`, `nan·inf`, `덮어쓸 수 없습니다`, `읽지 못했습니다`) | n/a |
| `bundle.py:117-126`(config를 담는 manifest) | json-file-io | Python `read_manifest`는 BOM이 있으면 `JSONDecodeError: Unexpected UTF-8 BOM`으로 실패합니다. `write_text`는 Windows에서 `\n`을 `\r\n`으로 씁니다 | `new UTF8Encoding(false)`로 씁니다(`File.WriteAllText(..., Encoding.UTF8)`은 BOM을 붙임). 줄 끝은 바이트 비교 결정에 맞춥니다 | exact |

#### (c) 외부 호출과 대체 방식

| Python 호출 | 위치 | C# 대체 | 비고 |
|---|---|---|---|
| `tomllib.load`·`loads`·`TOMLDecodeError` | `config.py:97`, `143-145` | `Bpcg.IO.Toml`. (A) Tomlyn 2.10.1(BSD-2-Clause)의 `TomlSerializer.Deserialize<TomlTable>` 또는 `TomlParser` 사건에 정수 원문 범위 검사와 BOM 사전 검사를 더함. (B) tomllib(`_parser.py` 703줄, `_re.py` 107줄, MIT) 이식 | (B)는 TOML 1.0과 BareText 판정이 같습니다. 선택은 열린 질문입니다 |
| `json.dumps(sort_keys=True, ensure_ascii=False)` | `config.py:81` | `Bpcg.IO.PyJson.Dumps(value, sortKeys: true, ensureAscii: false)` | bundle의 `indent=2, allow_nan=False` 형식도 같은 writer가 맡습니다 |
| `hashlib.sha256().hexdigest()[:12]` | `config.py:82` | `SHA256.HashData` + `Convert.ToHexStringLower` | 소문자 |
| `difflib.get_close_matches(n=3, cutoff=0.6)` | `config.py:239` | `Config` 안의 `DiffLib`(CPython 3.13 알고리즘, code point 비교) | 패키지 없음 |
| `copy.deepcopy` | `config.py:54`, `86`, `91` | 값 모델의 `DeepClone` | |
| `math.isfinite`, `math.pi`, `round`, `int`, `float` | `config.py:164`, `182-186`, `constants.py:21-27` | `double.IsFinite`, `Math.PI`(비트 같음), 정수 판정 `Math.Floor(x) == x`, 범위 검사 뒤 `(long)`, `(double)` | `float(int)`와 `(double)long`은 둘 다 최근접 짝수 반올림입니다 |
| `os.environ.get` | `paths.py:21-22` | `Environment.GetEnvironmentVariable` | `""`는 `.` |
| `Path.resolve`·`parents`·`exists`·`is_dir`·`cwd`·`name`·`stem`·`/` | `paths.py:13-17`, `config.py:106-119` | `Path.GetFullPath`, `File.Exists`, `Directory.Exists`, `Directory.GetCurrentDirectory`, `Path.GetFileName`, `Path.GetFileNameWithoutExtension`, `Path.Combine` | `Path.Join`은 쓰지 않습니다 |
| `str.partition`·`strip`·`split`·`endswith` | `config.py:65`, `73`, `107-111`, `137-139`, `237` | `IndexOf('=')`, `PyStrip`, `Split('.')`(빈 조각 유지), `EndsWith(..., StringComparison.Ordinal)` | |
| `numba.njit`·`prange` | `hashing.py`, `noise.py` | static 메서드(`AggressiveInlining`), `Parallel.For` | (d) 참고 |
| `np.floor`, `np.int64(x)`, `np.uint64(x)`, `.view(np.int64)` | `noise.py:62-80`, `176-179` | `Math.Floor`, `(long)`, `unchecked((ulong)x)`, `unchecked((long)u)` | |
| `np.ascontiguousarray(dtype=float64)`, `np.isfinite(...).all()`, `np.empty` | `noise.py:154-157`, `171-188`, `221`, `244`; `hashing.py:46` | 길이·유한성 검사, `new T[n]` | |
| `dataclass(frozen=True)` | `fields.py:11` | `sealed record` | 값으로 같음을 비교합니다 |

#### (d) numba 커널과 prange

| 커널 | 데코레이터 | C# | 병렬 | 경쟁·축약 |
|---|---|---|---|---|
| `splitmix64`, `hash3`, `hash_unit` | `@njit(cache=True, inline="always")` | `Hashing.SplitMix64`·`Hash3`·`HashUnit`(`AggressiveInlining`) | 없음 | 없음 |
| `hash_uniform_array` | `@njit(cache=True)` | 순차 `for` | 없음(칸마다 쓰므로 병렬로 바꿔도 같음) | 없음 |
| `_fade`, `_grad_dot` | `@njit(cache=True, inline="always")` | private static(`AggressiveInlining`) | 없음 | 없음 |
| `_noise3_mixed`, `gradient_noise3` | `@njit(cache=True)` | private·public static | 없음 | 없음 |
| `_fbm_kernel` | `@njit(cache=True, parallel=True)`, `prange(n_points)` | `Parallel.For(0, M, k => ...)` | 점 k | 점마다 `out[k*C + c]`만 쓰고 `acc`는 지역 변수, 옥타브 합은 순차입니다. 스레드 1·2·3·7·12개에서 비트 일치를 확인했습니다 |

- numba 안의 형은 float64·int64·uint64뿐이고 float32는 없습니다. uint64와 int64를 섞으면 numba가 float64로 바꾸는데, `_S30`·`_N_GRAD` 같은 np.uint64 상수가 이 함정을 막고 있습니다.
- C#에서는 시프트 수를 int 리터럴로, 곱하는 수를 `12UL`로 둡니다. 인라인 지정은 성능용이고 결과를 바꾸지 않습니다.

#### (e) 의심 버그

1. **정수 자리의 int64 범위 검사 누락** (`config.py:179-183`, `cli.py:316`·`339`).
    - `checked_overrides(cfg, {"planet.seed": 1e19})`는 `10000000000000000000`과 digest `2e2c320cf96e`를 냅니다. 그 뒤 `np.int64(seed)`에서 OverflowError가 납니다.
    - `--set caves.levels=1e20`은 검사를 통과해 1~3단계를 다 돈 뒤, caves의 `1..8` 검사에서 멈춥니다(실험).
    - C#은 `Coerce`와 TOML 층에서 거부합니다(의도한 차이).
2. **실수 자리의 OverflowError가 오류 처리를 빠져나감** (`config.py:186`, `cli.py:61-64`·`169-172`).
    - `float(new)`는 \|int\| ≥ 2^1024에서 OverflowError를 내는데, 호출부는 `except ValueError`만 잡습니다.
    - `bpcg planet --set landscape.theta=1` 뒤에 0을 400개 붙이면 SystemExit가 아니라 traceback으로 끝납니다(실험).
    - C#은 그런 정수를 TOML 층에서 오류로 두어 '읽지 못했습니다'로 멈춥니다(의도한 차이).
3. **비유한값·긴 리스트가 있으면 단계마다 digest가 달라짐** (`config.py:81` ↔ `bundle.py:77-109`·`234`).
    - digest는 `NaN`·`Infinity`와 리스트 전체를 쓰지만, manifest의 config는 `jsonable`이 null과 `{"__list__": n}`(1,024개 초과)으로 바꿉니다.
    - 그래서 되읽은 설정의 digest가 planet과 다릅니다(`landscape.theta = inf`: `f39db82a6163` 대 `c697abe91c6c`). tomllib은 TOML의 `nan`·`inf`를 받고, `with_overrides`는 검사가 없습니다.
    - 지금 설정 파일과 `--set` 경로에서는 생기지 않습니다. C#은 두 표현을 그대로 재현합니다.
4. **learned 파일 규칙의 문서·코드 불일치** (`configs/learned/README.md` ↔ `config.py:115`).
    - README는 `stream_power.toml`(`theta_ref`, `ks_lo`, `ks_hi`, `s_crit`, `q_unit`)을 설명합니다.
    - `load_config`는 `learned/<행성 파일 이름>`만 읽고 같은 점 경로만 덮어씁니다. README대로 만들면 값이 무시되거나 새 최상위 키로 붙습니다. 지금은 learned 파일이 없습니다.
5. **힌트를 검사하지 못하는 시험** (`tests/test_studio.py:139-140`). `match="landscape.theta"`는 메시지 앞의 입력 키 `landscape.thetaa`에 이미 맞아, 힌트가 없어도 통과합니다. C# 시험은 힌트 목록을 golden과 따로 비교합니다.
6. (낮음) **noise 문서의 '맥·윈도우·리눅스 같음' 주장** (`noise.py:3-4`). \|좌표\| ≥ 2^63이면 arm64 numba는 격자 번호를 포화시키고 x86 numba는 INT64_MIN을 낼 것이라 값이 달라집니다(x86은 명령 의미로 추정). `_check_points`는 유한성만 봅니다. 포팅 호출부의 좌표는 이 범위에 오지 않습니다.

#### (f) golden 대조 계획

| 대상 | 입력 | 출력 | 크기 | 비교 |
|---|---|---|---|---|
| `Config.Digest`·`PyJson` | earth × tiny·laptop·lab와 손 덮어쓰기 7가지(theta=0.5, k_ref=150을 검사 거쳐·안 거쳐, caves.levels=2.0, planet.axis=[0,0,1], seed=3, 새 키 `zz.new.key`) | 정렬 JSON 문자열, digest, manifest 형식(`indent=2`, 정렬 없음) 문자열 | 약 100 KB | exact (문자열) |
| digest 비유한값·왕복 | `with_overrides`로 theta = inf·nan, 1,100개 리스트를 넣고 `jsonable`→JSON→`Config`로 왕복 | 원래 digest와 왕복 digest | < 5 KB | exact ((e) 3 재현) |
| `Config.LoadConfig`·`Merge` | 손 TOML(`[a.b]`가 `[a]`보다 앞, profile의 `name` 있음·없음, 이름이 `.toml`뿐인 파일), `_merge` 직접 호출(기존·새·중첩 키, dict↔스칼라) | manifest 형식 JSON | < 20 KB | exact |
| `Bpcg.IO.Toml` 정수 경계 | `9223372036854775807`, `9223372036854775808`, `+9223372036854775808`, `-9223372036854775808`, `-9223372036854775809`, `18446744073709551615`, `18446744073709551616`, `0x7FFFFFFFFFFFFFFF`, `0x8000000000000000`, `0xFFFFFFFFFFFFFFFF`, 앞 0이 붙은 17자리 16진 `1` | Python int 값과 C# 기대값(범위 안은 long, 밖은 TOML 오류) | < 2 KB | exact. 조용히 감긴 값이 0건이어야 합니다 |
| `ParseAssignment` | 손 원문 약 50개: `0.5`, `false`, 배열, 따옴표 글자, `mars`, `inf`, `-nan`, `-0.0`, `0x10`, `1_000`, `1979-05-27`, `07:32:00`, `07:32`, `{a=1}`, `{a=1,}`, `"a\eb"`, `0.5 # c`, 줄바꿈 뒤 다른 키·같은 키, `k==1`, `.5`, `00`, `1e400`, U+001C·U+FEFF가 앞뒤에 붙은 값, 빈 키·값 | (키, 종류 태그, 값 또는 실수 비트, 오류) | < 10 KB | exact. (A)를 고르면 TOML 1.1 전용·int64 밖 입력은 기대 차이로 표시합니다 |
| `CheckedOverrides`·`Coerce` | earth+tiny에 덮어쓰기 약 45개(2.0→2, 2.5·inf·True 거부, 1→1.0, nan 거부, BareText, 리스트 길이·원소 형, 튜플, date, FIXED_KEYS, 절, 없는 키, `planet.axis.0`, `planet..seed`, 1e20, 10^400) | 형 태그를 단 결과(순서 포함), 또는 예외 종류·핵심어·힌트 | < 20 KB | 값·형·힌트는 exact, 메시지는 핵심어만. int64 밖과 OverflowError는 의도한 차이 |
| `DiffLib.CloseMatches` | `_all_keys` 121개 후보. 손 오타 30개, 실제 키를 1–3글자 바꾼 200개, 200자 이상 단어 2개, `'caves.levels'`+이모지 1–14개, 한글 섞인 오타 | 결과 목록 | < 30 KB | exact. 동점 단위 시험은 `cutoff=0.5`에서 `('ab', [ac, ad, ae, ba])` → `[ba, ae, ad]`입니다(0.6이면 `[]`) |
| `Section` 읽기 | 없는 키, 저장된 null, dict 기본값, 글자 값, 실수·정수 값에 `Get`·`TryF`·`I`·`F` | 값 또는 실패 종류 | < 5 KB | exact |
| `SplitMix64`·`Hash3`·`HashUnit` | 0, ±1, int64 최소·최대, 코드의 갈래 번호(101, 102, 201, 210–212, 220, 7101, 7301, 9101)와 고정 rng int64 세 쌍 1만 개 | uint64, float64 비트 | 약 400 KB | exact |
| `HashUniformArray` | ids = arange(−5, 1000), seed ∈ {0, −3, 2^63−1}, stream ∈ {101, 102}, tiny의 `_jitter_offsets` 인자 | float64 배열 | 약 50 KB | exact |
| `GradientNoise3` | 격자점(−0.0과 (−22, −0.0, −35) 포함), 격자면 ±1e−9, 1e−20, ±1e9, 2^52+0.5, 1e19·1e300 × 시드 {0, 1, −7, int64 최대·최소}, 무작위 5천 점, GOLDEN 3개 | float64 비트 | 약 300 KB | exact (0의 부호 포함) |
| `OctaveTables` | 시드 {0, 123, −5, 2^62} × 성분 1·3 × octaves {1, 4, 5, 6} × 인자 4벌 | seeds, offsets, freqs, amps, inv_norm | < 50 KB | exact |
| `Fbm3`·`VectorFbm3` 호출 지점 | tiny 실행 중 래퍼로 잡은 실제 인자: crust, plates(vector), climate, uplift, geology, solver(L0·히어로), volume(앞 2만 점) | 반환 배열 | 1–3 MB (out/golden에만) | exact, 병렬도 1과 기본값 |
| `Fbm3`·`VectorFbm3` 손 사례 | test_noise.py의 `_P`와 인자 3벌, 무작위 2천 점 × 인자 조합, 빈 입력, 오류 입력(octaves 0, gain 0, frequency −1, NaN, 길이 % 3 ≠ 0) | 반환 배열 또는 예외 | < 200 KB | exact (abs 1e−14 대신 비트) |
| `Constants.Gravity` | (6.371e6, 5514.0), (1, 1), (3.3895e6, 3933.5), 0·음수·nan·inf | float64 비트 또는 오류 | < 1 KB | exact |
| `Fields` 표 | FIELDS 순서대로 5개 열, GROUPS, `check_fields({'z_m', 'zz', 'Aa', 'a'})` | JSON | < 10 KB | exact |
| `PyJson.FormatDouble` | 고정 rng의 유한 double 비트 2만 개와 경계값(±0.0, 5e−324, 2.2250738585072014e−308, 1.7976931348623157e308, 1e16, 9999999999999998.0, 1e15, 1e−4, 1e−5, 0.1+0.2, 2^53, 2^63, 설정 파일의 실수 전부) | repr 문자열 | 약 0.8 MB | exact |
| 언어 사이 manifest 왕복 | C#이 쓴 planet manifest를 Python `read_manifest`·`config_from_manifest`로 읽음 | Python digest가 C# `config_digest`와 같음, BOM 없음 | 시험 1건 | exact (단계 3) |

- 호출 지점 사례는 golden 스크립트 안에서만 소비 모듈의 이름(`bpcg.planet.crust.fbm3` 등)을 래퍼로 바꿔 잡고, src는 고치지 않습니다. 이 사례만 out/golden에 두고 나머지는 1 MB 이하라 커밋할 수 있습니다.

#### (g) 포팅 순서와 위험 메모

1. `Bpcg.IO.PyJson`(digest·manifest writer와 숫자 형을 지키는 reader)을 먼저 만들고, 실수 표기 golden으로 확인합니다. .NET `"R"`의 최단 자릿수가 Python repr과 같은지는 아직 확인하지 못했습니다.
2. `Bpcg.IO.Toml`은 (A)·(B) 중 하나를 먼저 정합니다. 어느 쪽이든 정수 경계, BOM, 날짜, 문서 순서 golden을 통과해야 합니다.
3. `Hashing` → `Noise`. 둘 다 순수 계산이라 golden이 모두 exact로 통과해야 합니다.
4. `Constants`, `Fields`, `Package`.
5. `Config`(값 모델, `LoadConfig`, `Merge`, `WithOverrides`, `Digest`, `FromDict`) → `ParseAssignment`·`CheckedOverrides`·`DiffLib`. digest가 맞아야 hero·corridor·globe의 `config_digest`가 planet과 같습니다(test_cli).
6. `Paths`는 단계 1에서 저장소 찾기만 옮깁니다. 단계 0의 Godot .NET 빌드 확인 때 편집기에서 `AppContext.BaseDirectory`와 `Assembly.Location` 값을 찍어 보고, 설정 원천과 `Paths.Configure`는 단계 4에서 정합니다.

남은 위험은 다음과 같습니다.

- **조용한 오답.** Tomlyn을 검사 없이 쓰면 `--set planet.seed=9223372036854775808`이 오류 없이 시드 `long.MinValue`로 행성을 만듭니다. Python은 같은 입력에서 멈춥니다.
- **문화권 의존 API.** `EndsWith`, `Compare`, `ToString`, `Parse`는 모두 Ordinal·InvariantCulture로 씁니다. .editorconfig에서 CA1305·CA1307·CA1309·CA1310을 오류로 두기를 제안합니다.
- **글자 규칙.** `PyStrip`(29자), code point 단위 difflib, `Path.Combine`, BOM 없는 쓰기를 빠뜨리기 쉽습니다. 각각 golden이나 단위 시험이 하나씩 있어야 합니다.
- **unchecked 필수.** 해시는 `unchecked`가 반드시 있어야 합니다. 시험 프로젝트에서 `CheckForOverflowUnderflow`를 켜도 깨지지 않아야 합니다.
- **golden 위치.** Python golden 스크립트가 `OUT / "golden"`에 쓰면, C# 로더도 `Paths.Out`(BPCG_OUT 반영)을 기준으로 읽어야 같은 파일을 봅니다.

### core ② — cubesphere·distance·graph·resample

대상은 `src/bpcg/core/cubesphere.py`(209줄), `graph.py`(146줄), `distance.py`(274줄), `resample.py`(291줄)입니다. 네 모듈은 설정을 직접 읽지 않고 호출부가 넘긴 n·R·jitter·seed·max_dist 만 씁니다. scipy·scikit-image 호출은 없습니다. numba 커널은 distance 의 6개(힙 도우미 4개, `_propagate_sources`, prange `_final_distance`)와 resample 의 1개(prange `_bilinear_kernel`)입니다.

단계 0 확인 결과(macOS 26.6.2 arm64, numpy 2.5.3, numba 0.68.0)는 다음과 같습니다.

- numpy·numba·Python `math` 의 float64 `tan`·`arctan`·`arctan2`·`sin` 은 Apple libm 과 비트 단위로 같습니다(함수마다 무작위 40만 개, 불일치 0).
- .NET 10 의 `Math.Tan/Atan/Atan2/Sin/Pow` 는 CRT 함수를 그대로 부릅니다(dotnet/runtime release/10.0 `floatdouble.cpp`, `pal.h` 에 재지정 없음). JIT 의 `Math.Pow(x, 2)` → `x*x` 변환도 없습니다(이슈 #13336 열림).
- numba 는 `a*b + c` 를 FMA 로 묶지 않습니다(20만 개 불일치 0, FMA 였다면 46,982개가 다름).
- numpy 코드를 C# 이식안과 같은 순서의 칸별 스칼라 루프로 다시 쓴 Python 참조 구현을 만들었습니다. 이 구현은 아래 대상에서 numpy·numba 결과와 비트 단위로 같았습니다.
    - `cubesphere_grid`, `neighbor_table`
    - `sphere_graph`·`flat_graph`(흔들기 0·0.4·1.0), `unit()`
    - `cell_of`(동점·0 벡터 포함)
    - `_ghost_stencil`, `add_ghost_layers`
    - 두 prange 커널, 힙 전파, `nearest_source`(max_dist 4가지)
- tiny 묶음의 `graph.npz` 도 다시 만든 그래프와 비트 단위로 같습니다.

그래서 macOS arm64 에서는 초월함수를 거친 값까지 비트 일치를 기대값으로 둡니다. 어긋나면 허용 오차로 넘기지 않고 연산 순서 오류부터 찾습니다. 표의 'exact(mac)'는 macOS arm64 에서는 비트 일치, 다른 OS 에서는 (f)의 허용 오차를 뜻합니다. 'exact'는 모든 OS 에서 비트 일치를 뜻합니다.

#### (a) 대응표

| Python | C# 파일 · 형 | 멤버 · 서명 |
|---|---|---|
| `cubesphere.py` | `Core/Cubesphere.cs` · `static class Cubesphere`, `sealed class Grid` | 아래 행들 |
| `FACE_NAMES`, `FACE_U/V/N` | 상수 | `string[] FaceNames`; `double[] FaceU/FaceV/FaceN` (6×3 행 우선, JSON 때문에 double 유지) |
| `FACE_BASIS` | 옮기지 않음 | 테스트에서만 씀 |
| `Grid`, `n_cells`, `area_full` | `Grid` | `int N; double R; double[] Pos (6n²·3); double[] Area (n²); int NCells; double[] AreaFull()` |
| `to_sphere` | `Cubesphere` | `double[] ToSphere(int f, double[] a, double[] b)` (K·3), 스칼라판 `ToSphere(int f, double a, double b, Span<double> p)` |
| `omega` | `Cubesphere` | `double Omega(double X, double Y)` |
| `face_cell_areas` | `Cubesphere` | `double[] FaceCellAreas(int n, double R)` ([j·n+i]) |
| `neighbor_distance` | `Cubesphere` | `double NeighborDistance(ReadOnlySpan<double> p1, ReadOnlySpan<double> p2, double R)`, 배열판 (M·3 쌍) |
| `to_face` | `Cubesphere` | `(long[] F, double[] A, double[] B) ToFace(double[] p)` |
| `cell_of` | `Cubesphere` | `long[] CellOf(double[] p, int n)` |
| `NEIGHBOR_SLOTS`, `CARDINAL_SLOTS` | 상수 | `(int Dj, int Di)[] NeighborSlots`; `int[] CardinalSlots` = {1, 3, 4, 6} |
| `_U_INT/_V_INT/_N_INT`, `_face_with_normal`, `_cross_edge` | private | `int[] FaceUInt…`; `int FaceWithNormal(int ex, int ey, int ez)`; `(int F2, int I3, int J3) CrossEdge(int f, int n, ReadOnlySpan<int> e, ReadOnlySpan<int> t, int k)` |
| `neighbor_table` | `Cubesphere` | `int[] NeighborTable(int n)` (6n²·8, 없으면 −1) |
| `cross_face_pairs` | `Cubesphere` | `long[] CrossFacePairs(int n, int[]? nbr = null)` (k·2, 테스트 전용) |
| `cubesphere_grid` | `Cubesphere` | `Grid CubesphereGrid(int n, double R = 6_371_000.0)` |
| `graph.py` | `Core/Graph.cs` · `static class Graph`, `sealed class CellGraph` | 아래 행들 |
| `CellGraph` | `CellGraph` (공개 생성자) | `string Kind; int[] Shape; double[] Pos (N·3); int[] Nbr (N·8); int NSlots = 8; double[] Dist (N·8); double[] Area; double Spacing; double? R; (double East, double North) Origin; int NCells; double[] Width; double[] Unit(); bool[] BoundaryMask()`; `as_image` 는 옮기지 않음 |
| `_JITTER_STREAM_A/B`, `_jitter_offsets` | private | `const long JitterStreamA = 101, JitterStreamB = 102`; `(double[] Ja, double[] Jb) JitterOffsets(int nCells, double jitter, long seed)` |
| `flat_graph` | `Graph` | `CellGraph FlatGraph(int ny, int nx, double dx, double jitter = 0, long seed = 0, (double East, double North) origin = default)` |
| `sphere_graph` | `Graph` | `CellGraph SphereGraph(int n, double R, double jitter = 0, long seed = 0)` |
| `_distances` | private | `double[] Distances(double[] pos, int[] nbr, int nSlots, double? R)` |
| `distance.py` | `Core/Distance.cs` · `static class Distance` | 아래 행들 |
| `_heap_less/_heap_swap/_sift_up/_sift_down` | private | `HeapLess/HeapSwap/SiftUp/SiftDown` (배열 `double[] hkey, long[] hcell, long[] hsrc`) |
| `_propagate_sources` | internal | `(double[] Key, long[] Src) PropagateSources(double[] pos, int[] nbr, int nSlots, bool[] isSource, double maxKey)` |
| `_final_distance` | internal | `double[] FinalDistance(double[] pos, long[] src, double radius, bool isSphere, double maxDist)` |
| `_check_inputs` | private | `void CheckInputs(CellGraph g, bool[] isSource, double maxDist)` |
| `nearest_source` | `Distance` | `(double[] Dist, long[] Src) NearestSource(CellGraph g, bool[] isSource, double maxDist = double.PositiveInfinity)` |
| `nearest_source_values` | `Distance` | `(double[] Dist, long[] Src, T[] Values) NearestSourceValues<T>(CellGraph g, bool[] isSource, T[] values, int width = 1, double maxDist = +inf)` |
| `resample.py` | `Core/Resample.cs` · `static class Resample`, `sealed record GhostStencil` | 아래 행들 |
| `_EDGE_SIDES`, `_face_with_normal`, `_locate_on_row` | private | `(char Axis, int Sign)[] EdgeSides`; `int FaceWithNormal(double ex, double ey, double ez)`; `void LocateOnRow(ReadOnlySpan<double> q, int f, int f2, int n, int L, long loMin, long loMax, out long idx0, out long idx1, out double w1)` |
| `_ghost_stencil` | internal | `GhostStencil GetGhostStencil(int n, int layers)` (캐시); `record GhostStencil(long[] EdgeDst, long[] EdgeSrc, double[] EdgeW, long[] CornerDst, long[] CornerSrc, double[] CornerW)` |
| `add_ghost_layers` | `Resample` | `double[] AddGhostLayers(double[] faces, int n, int layers = 1)` (float[]·int[] 오버로드, 반환 6·P·P) |
| `_bilinear_kernel` | internal | `void BilinearKernel(double[] padded, double[] unit, int n, int layers, double[] faceU, double[] faceV, double[] faceN, double[] output)` |
| `_check_unit` | private | `double[] CheckUnit(double[] unit)` |
| `sample_sphere` | `Resample` | `double[] SampleSphere(double[] field, int n, double[] unit)` (linear, float[] 오버로드); `T[] SampleSphereNearest<T>(T[] field, int n, double[] unit)` |
| `resample_sphere` | `Resample` | `double[] ResampleSphere(double[] field, int nSrc, CellGraph dst)`; `T[] ResampleSphereNearest<T>(T[] field, int nSrc, CellGraph dst)` |

`NearestSourceValues<T>` 의 채움값은 double·float 이면 NaN, sbyte·short·int·long 이면 −1, 그 밖(bool, byte 등)이면 default 입니다.

#### (b) 수치 함정 표

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| cubesphere.py:16-22 | json-float-format | `FACE_U/V/N` 은 float64 이고 bundle.py:228·textures.py:130 이 `[[0.0, 1.0, 0.0], …]` 로 씀 | `double[]` 유지, PyJson 이 Python repr 로 씀 | exact |
| cubesphere.py:49 | negative-index | `FACE_N[f]` 는 f < 0 이면 뒤에서 셈 | 0 ≤ f < 6 검사 | n/a |
| cubesphere.py:50-51, 73-76; resample.py:94-95, 111-112 | transcendental-libm | `np.tan(np.pi * a / 4.0)` 은 libm tan, 인자는 (π·a)/4 순서 | `Math.Tan(Math.PI * a / 4.0)` | exact(mac) |
| cubesphere.py:52; graph.py:119; resample.py:96, 113 | signed-zero | N + X·U + Y·V 를 세 성분 모두 계산해 0 은 +0.0; 성분을 골라 쓰면(−X/den) −0.0 | `((N[k] + X*U[k]) + Y*V[k]) / den` 를 k = 0..2 모두 | exact(mac) |
| cubesphere.py:53 | power | `X**2` = `X*X`, 합은 (1 + X²) + Y² | `Math.Sqrt(1.0 + X*X + Y*Y)` | exact |
| cubesphere.py:59, 100; distance.py:180 | transcendental-libm | arctan2 는 libm atan2, R 을 앞에서 곱함 | `R * Math.Atan2(...)` | exact(mac) |
| cubesphere.py:68 | linspace | y[k] = k·(2/n) + (−1), 마지막 원소만 1.0 으로 덮음 | Linspace 복제 | exact |
| cubesphere.py:70-71, 202 | reshape-order | meshgrid 'xy' 라 [j, i] 의 열이 i | j 바깥·i 안쪽 루프, [j·n+i] | exact |
| cubesphere.py:79-84 | eval-order | ((ω++ − ω−+) − ω+−) + ω−− 소거 | 같은 괄호 | exact(mac) |
| cubesphere.py:87-88 | aliasing | 거울 평균과 전치 평균은 옛 om 으로 계산 | 새 배열 두 개, 제자리 갱신 금지 | exact(mac) |
| cubesphere.py:89 | pow-libm | `R**2` 는 libm pow 이고 무작위 R 의 0.13% 에서 R*R 과 1ulp 다름(지구 R 은 같음) | `Math.Pow(R, 2.0)` | exact(mac) |
| cubesphere.py:98 | cross-order | cp0 = a1·b2 − a2·b1 … 곱을 따로 반올림 | `Cross3` 같은 식, FMA 금지 | exact |
| cubesphere.py:98-99; graph.py:50, 120 | reduction-start | 길이 3 축 합은 +0.0 에서 시작하는 순차 합 | `s = 0.0; s += …` 세 번 | exact |
| cubesphere.py:106 | argmax-tie | 같은 값이면 앞 면(+X, −X, +Y, −Y, +Z, −Z), NaN 이면 첫 NaN; 기저 벡터 행렬곱은 정확 | 3항 합 6개, `d > best` 엄격 비교 | exact |
| cubesphere.py:107-109 | division-nan | 0 벡터는 0/0 = NaN(예외 없음), 4/π 먼저 계산, 4/π·atan(±1) = ±1.0 | `4.0 / Math.PI * Math.Atan(pu / pn)` | exact(mac) |
| cubesphere.py:116-117 | float-to-int-cast | NaN → 0, ±inf 포화(arm64), 그 뒤 clip(0, n−1); 동점 a = 1 → n → n−1 | `(long)Math.Floor(n * (a + 1.0) / 2.0)` + `Math.Clamp` | exact |
| cubesphere.py:118; distance.py:91 | int-dtype | 셀 번호·src 는 int64 | `long` | exact |
| cubesphere.py:160-183 | fancy-assign | int64 식을 int32 표에 넣음(n ≤ 18918), 마스크가 서로 겹치지 않아 순서 무관 | 칸·슬롯별 직접 계산, 상한 검사 | exact |
| cubesphere.py:191-194 | ordering | −1 에도 `c2 // nn` 을 계산(무해), 쌍 순서는 칸 → 슬롯 1·3·4·6, int64 | `c2 >= 0 &&` 단락 평가, `long[]` | exact |
| graph.py:111, 121 | round-trip | 흔들기 0 이어도 pos = (grid.pos / R)·R | 같은 왕복 | exact(mac) |
| graph.py:63-64, 85-86, 116-119 | eval-order | (hash − 0.5)·jitter, x + ja·dx, unit + (ja·h)·U + (jb·h)·V | 같은 괄호, seed 는 `long` | exact(mac) |
| graph.py:81-83, 87 | eval-order | x = o0 + (i + 0.5)·dx, y = o1 − (j + 0.5)·dx, `jitter > 0` 일 때만 흔듦, z = +0.0 | 같은 식·조건 | exact |
| graph.py:131 | pairwise-sum | spacing = sqrt(pairwise 합 / N), manifest·crust reach·finder margin·solver 노이즈 좌표로 이어짐 | `Math.Sqrt(NpReduce.Mean(area))` | exact(mac) |
| graph.py:137-145 | mask-order | 오름차순, 구면은 a/R·b/R 뒤 거리, 이웃 없으면 inf | 칸·슬롯 루프 | exact(mac) |
| distance.py:27-76 | heap-tie | (키, 셀) 비교, 엄격 비교 sift, src 는 비교하지 않음 | 이진 힙 배열 3개 그대로, `PriorityQueue` 금지 | exact |
| distance.py:99-106, 119 | heap-init | 출발 칸을 셀 번호 순·키 0 으로 넣음(heapify 없음); 낡은 원소는 정확 비교로 버림 | 같은 순서·같은 `!=` | exact |
| distance.py:124-127 | negative-index | −1 이웃은 색인 전에 건너뜀(C# 은 −1 색인이 예외) | `if (v < 0) continue;` 를 먼저 | exact |
| distance.py:128-134 | fma/tie | d2 = (dx² + dy²) + dz², `d2 > max_key` 가지치기, `d2 < key or (== and s < src)` 갱신 | 같은 식, FMA 금지 | exact |
| distance.py:175-180, 186 | eval-order | m 단위 cross·dot(0.0 시작 없음), `d <= max_dist` 가 아니면 inf | 원식 그대로, NeighborDistance 재사용 금지 | exact(mac) |
| distance.py:229-238 | transcendental-branch | max_key = (2R·sin(0.5·max_dist/R))²·(1 + 1e−9) 가 문턱; tiny crust 호출 max_key 1690646598683.4526 | `Math.Sin`, 상수 접기 결과도 같음 | exact(mac) |
| distance.py:242, 265-273 | fill | −1 을 0 으로 바꿔 모은 뒤 dtype 별로 채움(실수 NaN, 부호 정수 −1, 그 밖 0) | `FillValue<T>()`, 닿지 않은 칸은 원본을 읽지 않음 | exact |
| resample.py:27, 40-42 | matvec-exact | 기저 벡터 행렬곱은 ±q_k 와 정확히 같음 | 3항 스칼라 합 | exact |
| resample.py:43-44, 212-213 | eval-order | 스텐실 ((a2 + 1)·n)/2 − 0.5 + L 과 커널 ((a + 1)·0.5)·n − 0.5 + layers 는 다른 식 | 각각 원식 그대로 | exact(mac) |
| resample.py:48 | rounding | `np.rint` 는 짝수 반올림 | `Math.Round(x, MidpointRounding.ToEven)` | exact |
| resample.py:50-54 | assert | 불변식 assert 는 어기면 멈춤 | 늘 검사, `InvalidOperationException` | n/a |
| resample.py:52-53 | floor-clip | 홀수 n 의 가운데 줄은 가중치가 정확히 0·1; 다른 OS 에서는 lo 가 하나 바뀔 수 있음 | `Math.Clamp((long)Math.Floor(m), lo, hi)` | exact(mac) |
| resample.py:64 | cache | lru_cache 는 읽기 전용 배열을 돌려주고, 여러 스레드가 부를 수 있음 | `ConcurrentDictionary` + `Lazy` | n/a |
| resample.py:77-133 | ordering | 모서리는 f → 변 4개 → m → k, 꼭짓점은 f → si → sj → mi → mj, 후보는 u 쪽 먼저 | 같은 중첩 루프 | exact |
| resample.py:125-128 | weights | ÷len(cand), 후보가 하나면 src[0]·가중치 0.0 두 개 추가(NaN × 0 = NaN 유지) | 0 가중치 항도 계산 | exact |
| resample.py:162, 173 | dtype | 정수·float32 입력을 float64 로 복사 | 오버로드에서 double 로 | exact |
| resample.py:176 | aliasing | 모서리 원본은 원래 칸뿐이고 목적지는 유일 | 제자리 루프 가능, FMA 금지 | exact |
| resample.py:177 | reduction-start | 꼭짓점 합은 +0.0 에서 시작(모두 −0.0 이면 +0.0) | `acc = 0.0; acc += v*w` 네 번 | exact |
| resample.py:195-206 | prange/argmax | 점별 쓰기만 함; 면 고르기는 `d > best` | `Parallel.For`, 같은 루프 | exact |
| resample.py:209-232 | float-to-int/fma | int64 n·layers 를 double 로 바꿈, floor → int, [0, size − 2] 로 자름, lerp 세 식은 FMA 없음 | `(int)Math.Floor`, 같은 lerp 식 | exact(mac) |
| resample.py:257-273 | check-order | nearest 는 dtype·n ≥ 2 검사 전에 원 dtype 반환, linear 는 float64 반환 | 메서드 분리, 검사 순서 유지 | exact |
| bundle.py:131-136 | json-float-format | spacing·R·origin 이 json float repr 로 manifest 에 쓰임 | PyJson repr | exact |

#### (c) 외부 호출과 대체 방식

| Python 호출 | 쓰는 곳 | C# 대체 |
|---|---|---|
| `np.tan`, `np.arctan`, `np.arctan2`, `math.atan`, `math.atan2`, `math.sin` | cubesphere, resample, distance | `Math.Tan/Atan/Atan2/Sin` (CRT libm, macOS 에서는 같은 함수) |
| Python `R**2` | cubesphere.py:89 | `Math.Pow(R, 2.0)` |
| `np.sqrt`, `math.sqrt` | 곳곳 | `Math.Sqrt` (IEEE 정확 반올림) |
| `np.linspace` | cubesphere.py:68 | Linspace 복제(둘 곳은 열린 질문) |
| `np.meshgrid`, `np.tile`, `np.repeat`, `np.stack`, `np.concatenate`, `np.full`, `np.full_like`, `np.where`, `np.nonzero` | 곳곳 | 루프, `Array.Fill`, `List<T>` |
| `np.linalg.norm(axis)`, `np.sum(axis=-1)`, `np.cross` | cubesphere, graph | `LinAlg.Norm3`, `LinAlg.Dot3Np`(+0.0 시작), `LinAlg.Cross3`(numpy 순서) |
| `p @ FACE_N.T`, `np.einsum('...k,...k->...')`, `FACE_N @ e`, `q @ FACE_N[f2]` | to_face, resample | 3항 스칼라 합(기저 벡터라 BLAS 결과와 같은 값) |
| `np.argmax` | to_face, resample._face_with_normal | 앞 우선 엄격 `>` 루프 |
| `np.floor(...).astype(int64)`, `np.clip`, `np.rint` | cell_of, _locate_on_row | `(long)Math.Floor`, `Math.Clamp`, `Math.Round(ToEven)` |
| `ndarray.mean()` | graph.py:131 | `NpReduce.Mean`(pairwise) |
| `(a * w).sum(axis=1)` | resample.py:177 | +0.0 시작 순차 합 |
| `functools.lru_cache(maxsize=32)` | resample.py:64 | `ConcurrentDictionary<(int, int), Lazy<GhostStencil>>` |
| `hash_uniform_array` | graph.py:63-64 | `Bpcg.Core.Hashing.HashUniformArray` (core①) |
| numba `njit`, `prange` | distance, resample | 순차 루프, prange 만 `Parallel.For` |

NuGet 패키지는 필요 없습니다.

#### (d) numba 커널과 prange

| 커널 | 장식 | prange | 경쟁·축약 | C# |
|---|---|---|---|---|
| `distance._heap_less`, `_heap_swap` | `@njit(cache=True, inline='always')` | 없음 | 없음 | private static, `AggressiveInlining` |
| `distance._sift_up`, `_sift_down` | `@njit(cache=True)` | 없음 | 없음 | 같은 순차 루프 |
| `distance._propagate_sources` | `@njit(cache=True)` | 없음 | 순차 힙(꺼내는 순서가 결과를 정함) | 순차 루프 그대로 |
| `distance._final_distance` | `@njit(cache=True, parallel=True)` | 1개(칸) | 칸별 `out[c]` 쓰기만, 축약 없음 | `Parallel.For` |
| `resample._bilinear_kernel` | `@njit(cache=True, parallel=True)` | 1개(점) | 점별 `out[k]` 쓰기만, 축약 없음 | `Parallel.For` |

- 커널 안 형은 실수 float64, 정수 int64 이고, 이웃 표 원소만 int32 입니다. float32·uint64 섞임은 없습니다.
- numba 는 FMA 로 묶지 않고 `fastmath` 도 쓰지 않습니다.
- graph.py 가 부르는 `hashing.hash_uniform_array`(@njit)는 core① 소관입니다.
- cubesphere·graph 의 numpy 벡터 연산은 prange 가 아니므로 C# 에서도 순차 루프로 둡니다.

#### (e) 의심 버그

- (경미) resample.py:162-163: `add_ghost_layers` 는 정수 dtype 도 받아 float64 로 바꾸는데, 오류 문구는 '실수 배열이어야 합니다'입니다. 동작에는 영향이 없습니다.
- (설계 확인) earth.toml:88 `landscape.jitter = 1.0` → graph.py:114-120
    - 흔든 대표점이 제 칸 밖으로 나갑니다. tiny L0(n = 32)에서는 6144칸 중 203칸, n = 128 에서는 3315칸입니다.
    - 최소 이웃 거리는 n = 32 에서 0.025·spacing, n = 128 에서 0.0063·spacing 입니다.
    - 테스트의 '제 칸 안' 불변식은 jitter 0.4 에서만 시험합니다.
    - 의도일 수 있지만 짧은 변에서는 경사가 크게 부풀 수 있습니다. 포팅은 그대로 따릅니다.
- 그 밖에 네 모듈에서 버그로 보이는 곳은 찾지 못했습니다. 힙의 라벨 수정, 스텐실 불변식(목적지 유일, 모든 칸 한 번 쓰기, 원본·목적지 분리), 이웃 표 대칭은 실험으로 확인했습니다.

#### (f) golden 대조 계획

| 대상 | 입력 | 출력 | 크기 | 비교 |
|---|---|---|---|---|
| `CubesphereGrid`·`FaceCellAreas` | 호출부 n = 16·32, R = 6371000.0; 손 사례 n = 1, 2, 3, 5, 33, R = 15.0, 1.0, 9964050.17398628(R**2 ≠ R*R) | pos, area | 0.5 MB | exact(mac) |
| `ToSphere`·`Omega` | 손 사례: f = 0..5 × a, b ∈ {−1 − 3/n, −1, −0.37, 0, 0.5, 1, 1 + 1/n, 1 + 3/n} | (K·3), ω | 10 KB | exact(mac) |
| `NeighborDistance` | 손 사례: grid(32) 100쌍, 1e−7 rad 쌍, 같은 점, 반대 점 | f64 | 5 KB | exact(mac) |
| `ToFace`·`CellOf` | 호출부 materials.py:177 (L0 unit 6144점, n = 16), refine.py:79 (히어로 unit 4096점, n = 32); 손 사례: 동점·꼭짓점·0 벡터·−0.0·7·pos, 정규분포 5000점 × n = 1, 3, 16, 32 | f, a, b, cell | 0.6 MB | f·cell exact, a·b exact(mac) |
| `NeighborTable` | 손 사례 n = 1, 2, 3, 4, 5, 16, 32, 33 | int32 | 0.45 MB | exact |
| `CrossFacePairs` | 손 사례 n = 1, 2, 4, 16, 64 | int64 (k·2) | 20 KB | exact |
| `SphereGraph`·`Unit` | 호출부 pipeline.py:612 (16, R), :622 (32, R, 1.0, 0) = tiny graph.npz; 손 사례 (7, R, 1.0, 123456789), (16, R, 0.4, 5) | pos, nbr, dist, area, spacing, unit | 1.3 MB | nbr exact, 나머지 exact(mac) |
| `FlatGraph`·`BoundaryMask` | 호출부 hero/domain.py:90 (64, 64, 100, 1.0, 1738104521, (−3200, 3200)), warmstart.py:81 (16, 16, 400, 1.0, 7919, 같은 origin); 손 사례: presolve 꼴(흔들기 없음), (1, 5, 10), (20, 30, 25, 0.4, 2, (1000, 500)) | pos, nbr, dist, area, spacing, mask | 0.7 MB | exact |
| `PropagateSources` | 호출부 13회의 pos·nbr·is_source·max_key 그대로; 손 사례: 동점(1×5 {0, 4}, 5×5 {2, 22}), 출발 없음, 이웃 없는 2칸, 같은 위치 두 칸, jitter 1.0 평면의 라벨 수정 | key, src | 1.5 MB | exact |
| `FinalDistance` | 위 출력 src + pos·radius·is_sphere·max_dist (inf, 1302515.8981976807) | out | 0.4 MB | 평면 exact, 구면 exact(mac) |
| `NearestSource` | 호출부 (graph, is_source, max_dist) 13회; 손 사례 sphere_graph(24, R, 0.4, 8) + max_dist = 3·spacing, 0 | dist, src | 0.4 MB | src exact, dist exact(mac) |
| `NearestSourceValues<T>` | 손 사례: 테스트 사례(float64, int32 + max_dist 1.5, (N, 2)), uint8·bool·float32; 호출부 water.py:287, materials.py:193, solver.py:425 | dist, src, values | 0.2 MB | exact |
| `GetGhostStencil` | 손 사례 (n, L) = (2,1), (3,1), (4,2), (7,1), (16,1), (16,2), (16,8), (32,1), (33,3) | 배열 6개 | 0.4 MB | exact(mac) |
| `AddGhostLayers` | 호출부 16회; 손 사례: 무작위(음수·−0.0·NaN 한 칸), 전부 −0.0, 상수 3.25 (L = 2), float32·int32, L = 1..3 | padded | 0.6 MB | exact(mac), −0.0 까지 |
| `BilinearKernel` | 호출부 16회의 (padded, unit, n, layers); 손 사례: 면 경계 ±(1 ± 1e−9) 양쪽 점, 꼭짓점 방향, 7·pos | out | 1 MB | exact(mac) |
| `SampleSphere`·`ResampleSphere`(·`Nearest`) | 호출부 materials.transfer_materials 8필드 + 범주, refine.sample_l0_fields 7필드 + template_id, hero_boundary 의 z_m 한 점 | (M,) | 0.5 MB | exact(mac), 범주 exact |

포착 방법은 다음과 같습니다.

- 정의 모듈의 전역 이름을 감싸면 모든 호출부가 잡힙니다: `distance._propagate_sources`, `distance._final_distance`, `resample._bilinear_kernel`, `resample.add_ghost_layers`, `cubesphere.cell_of`.
- 다음 이름은 호출 모듈에 직접 import 되어 있으므로 호출 모듈 쪽 이름을 감쌉니다: `cell_of`(materials), `sphere_graph`(pipeline), `flat_graph`(hero.domain, landscape.warmstart), `nearest_source(_values)`, `resample_sphere`, `sample_sphere`.

tiny `all` 한 번의 호출 수는 다음과 같습니다.

- `_propagate_sources`·`_final_distance` 13회: plates.py:371 ×2, crust.py:246·247, materials.py:193, solver.py:425 ×2, water.py:287 ×3, groundwater.py:164 ×2, finder.py:168.
- `add_ghost_layers`·`_bilinear_kernel` 16회: materials 8회, refine 8회.
- `cell_of` 2회, `sphere_graph` 2회, `flat_graph` 2회.

그래프는 한 번만 저장하고, 커널 입력은 그 그래프를 가리킵니다. 커밋하는 1 MB 이하 묶음에는 손 사례와 n = 7·16 그래프만 넣고 나머지는 out/golden 에 둡니다.

다른 OS 에서는 입력의 1ulp 차이가 소거로 커지므로 척도 기준 절대 오차를 씁니다(eps = 2.22e−16).

- pos: 4·eps·R
- area: 16·eps·R² (포함-배제 소거)
- dist·FinalDistance: 16·eps·R
- spacing: 상대 4·eps
- 보간 값: 8·eps·max abs(field)

정수·셀 번호·src 시험은 Python 이 넘긴 실수 입력을 그대로 받으므로 모든 OS 에서 exact 입니다.

#### (g) 포팅 순서와 위험 메모

1. `Cubesphere`: 상수 → `ToSphere`·`Omega` → `FaceCellAreas`(Linspace 복제) → `NeighborTable`·`CrossFacePairs` → `ToFace`·`CellOf` → `CubesphereGrid` 순서입니다. LinAlg(Cross3·Dot3Np·Norm3)가 먼저 있어야 합니다.
2. `Graph`: core① `Hashing.HashUniformArray` 와 `NpReduce.Mean`(pairwise)이 먼저 있어야 합니다. `CellGraph` 는 묶음 읽기와 plates.same_plate_graph 가 직접 만들므로 공개 생성자를 둡니다.
3. `Distance`: 힙을 그대로 옮기고 `PropagateSources` 를 먼저 대조한 뒤 `FinalDistance`, `NearestSource(Values)` 순서로 갑니다.
4. `Resample`: `GetGhostStencil` → `AddGhostLayers` → `BilinearKernel` → `SampleSphere`·`ResampleSphere` 순서입니다.

위험 메모는 다음과 같습니다.

- 플랫폼: 비트 일치는 macOS arm64 의 같은 libm 에 기댑니다. Linux·Windows 에서는 pos·area·dist·spacing 이 1ulp 다를 수 있고, 이 값들이 모든 하류 단계(D8 경사, 문턱, 노이즈 좌표)의 입력이라 이산 결과가 갈릴 수 있습니다.
- spacing 은 pairwise 합을 그대로 옮겨야 합니다. 순차 합은 시험한 다섯 크기 모두에서 결과가 달랐습니다.
- 힙: 시험한 사례에서는 꺼내는 순서가 결과를 바꾸지 않았지만, 라벨 수정 방식이라 일반적으로는 보장되지 않습니다. 이진 힙을 그대로 옮깁니다.
- 부호 있는 0: 면 기저 성분을 골라 쓰는 최적화나 꼭짓점 합을 첫 항에서 시작하는 최적화는 graph.npz 와 필드의 비트를 바꿉니다.
- `R**2` 는 `Math.Pow` 여야 합니다. 지구 반지름에서는 차이가 없어 tiny 대조로는 잡히지 않으므로 손 사례 R = 9964050.17398628 로 시험합니다.
- 이웃 표는 int32 라 n ≤ 18918 이 상한이고, lab n = 1024 에서 201 MB 입니다(Python 과 같음).
- 흔들기 1.0 에서는 이웃 거리가 0.6%·spacing 까지 짧아져 뒤 단계 경사가 민감합니다. 동작은 그대로 따릅니다.

### hydro — accumulate·depressions·network·routing (비평 거침)

`src/bpcg/hydro/`의 5개 파일(627줄: `__init__.py` 4, `accumulate.py` 66, `depressions.py` 219, `network.py` 109, `routing.py` 229)은 numpy 입력 검사와 numba 커널 12개(`@njit` 9개, `inline="always"` 힙 도우미 3개)로 이루어져 있습니다. scipy, 정렬, numpy 합산, 초월 함수, float32, prange가 없습니다. 커널 안의 실수 연산은 float64 덧셈·뺄셈·나눗셈, 곱셈 한 번(routing.py:65 `(1.0 + eta) * sp`), 비교뿐입니다. a·b + c 꼴이 없어 FMA 축약 문제도 없습니다. 따라서 같은 입력이면 C# 출력은 -0.0 부호까지 **비트 단위로 같아야** 합니다.

hydro는 cfg를 직접 읽지 않습니다. 호출자가 `landscape.fill_epsilon_m`(1.0e-3)과 `landscape.hysteresis_eta`(0.02)를 Python float로 넘깁니다(solver.py:493-494, pipeline.py:241). fans.py:194의 `reroute`는 시험에서만 부릅니다.

단계 0 확인 결과는 다음과 같습니다(실험 파일은 scratchpad의 `stage0/hydro/`).
- tiny `all`(지구본 제외)을 scratch 폴더에 다시 돌리며 hydro 호출을 기록했습니다. 결과 배열 97개(npz 멤버 포함)가 `out/tiny_check`와 비트 단위로 같았습니다.
- 기록한 호출은 327개입니다.
    - 누적 190: solver.py:540-542 각 61, pipeline.py:250-252 각 1, drainage.py:499 4
    - D8 62: solver.py:534 4, solver.py:537 57, pipeline.py:242 1
    - 순서 64, ε 채움 5
    - 채움 4: water.py:330 2, detail.py:333, detail.py:472
    - 강 구간 2
- 이와 별도로 drainage.py:206이 `_check_rcv`를 21번 부릅니다. 입력은 모두 float64 z·dist, int32 nbr, bool 마스크, int64 rcv·order였습니다.
- C#처럼 IEEE double로 차례대로 계산하는 순수 Python 복제본(힙은 `heapq`의 (z, i) 튜플)이 327개 출력과 모두 비트 단위로 같았습니다.

출력이 실제로 쓰이는 범위입니다. 대조 우선순위를 정할 때 씁니다.
- D8의 `slope`는 src 호출 4곳(solver.py:534·537, pipeline.py:242, fans.py:195)이 모두 버리고, 시험만 씁니다.
- prev를 준 D8의 `n_changed`도 버립니다(solver.py:537). 솔버 history의 변화 수는 `_freeze_kernel`(solver.py:538)이 세고, history에 들어가는 D8 값은 첫 반복의 N뿐입니다.
- `donor_lists`, `donors_count`, `outlet_of`는 src가 부르지 않고 tests만 부릅니다.
- 행성 `river_segments` 결과는 개수(manifest meta의 diag `n_river_segments`)로만 쓰입니다. 히어로 결과는 `rivers.npz`(cells·offsets int64, bundle.py:209-216)로 저장됩니다.
- 그래도 모두 1:1 API로 옮기고 같은 기준(exact)으로 대조합니다.

#### (a) 대응표

| Python | C# 파일 · 형 | 멤버 (C# 서명) |
|---|---|---|
| `hydro/__init__.py` | 없음 | docstring만 있고 다시 내보내는 이름이 없습니다 |
| `accumulate.accumulate` | `Hydro/Accumulate.cs` · `public static class AccumulateModule` | `double[] Accumulate(long[] rcv, long[] order, double[] weight)`, 오버로드 `Accumulate(long[] rcv, long[] order, double weight)` |
| `accumulate.donors_count` (시험 전용) | 같은 클래스 | `int[] DonorsCount(long[] rcv)` |
| `accumulate._accumulate_kernel`, `_donors_count_kernel` | 같은 클래스 (internal) | `void AccumulateKernel(long[] rcv, long[] order, double[] acc)`, `int[] DonorsCountKernel(long[] rcv)` |
| `depressions.fill_depressions` | `Hydro/Depressions.cs` · `public static class Depressions` | `double[] FillDepressions(double[] z, int[] nbr, int nSlot, bool[] isOutlet)` |
| `depressions.fill_epsilon` | 같은 클래스 | `double[] FillEpsilon(double[] z, int[] nbr, int nSlot, bool[] isOutlet, double eps)` |
| `depressions._fill_kernel`, `_fill_epsilon_kernel` | 같은 클래스 (internal) | `long FillKernel(double[] z, int[] nbr, int nSlot, bool[] isOutlet, double[] result)`, `long FillEpsilonKernel(double[] z, int[] nbr, int nSlot, bool[] isOutlet, double eps, double[] result)`. 방문 칸 수를 돌려줍니다 |
| `depressions._key_less`, `_heap_push`, `_heap_pop` | 같은 클래스 (internal, AggressiveInlining) | `bool KeyLess(double za, long ia, double zb, long ib)`, `int HeapPush(double[] hz, long[] hi, int size, double z, long i)`, `int HeapPop(double[] hz, long[] hi, int size)` |
| `depressions._check_inputs` | 같은 클래스 (private) | `void CheckInputs(double[] z, int[] nbr, int nSlot, bool[] isOutlet)` |
| `network.outlet_of` (시험 전용) | `Hydro/Network.cs` · `public static class Network` | `long[] OutletOf(long[] rcv, long[] order)` |
| `network.river_segments` | 같은 클래스 | `List<long[]> RiverSegments(long[] rcv, long[] order, bool[] isRiver)` |
| `network._outlet_kernel`, `_segments_kernel` | 같은 클래스 (internal) | `long[] OutletKernel(long[] rcv, long[] order)`, `(long[] Cells, long[] Offsets) SegmentsKernel(long[] rcv, long[] order, bool[] isRiver)` |
| `network._check_order` | 같은 클래스 (private) | `long[] CheckOrder(long[] order, int n)` |
| `routing.d8_receivers` | `Hydro/Routing.cs` · `public static class Routing` | `(long[] Rcv, double[] Slope, int NChanged) D8Receivers(double[] zt, int[] nbr, int nSlot, double[] dist, bool[] isOutlet, long[]? prev = null, double eta = 0.0)` |
| `routing.donor_lists` (시험 전용), `topo_order` | 같은 클래스 | `(long[] Start, long[] Donors) DonorLists(long[] rcv)`, `long[] TopoOrder(long[] rcv)` |
| `routing._d8_kernel` | 같은 클래스 (internal) | `long D8Kernel(double[] zt, int[] nbr, int nSlot, double[] dist, bool[] isOutlet, long[] prev, bool hasPrev, double eta, long[] rcv, double[] slope)` |
| `routing._donor_csr_kernel`, `_topo_kernel` | 같은 클래스 (internal) | `(long[] Start, long[] Donors) DonorCsrKernel(long[] rcv)`, `long TopoKernel(long[] rcv, long[] start, long[] donors, long[] order)` |
| `routing._check_rcv` | 같은 클래스 (internal) | `long[] CheckRcv(long[] rcv)`. 복사하지 않고 같은 배열을 돌려줍니다. metrics/drainage.py:206도 부릅니다 |

형 메모:
- 원소형은 numpy dtype을 따릅니다. 셀 번호 배열(rcv, order, start, donors, 유역, 강 구간)은 int64라 `long[]`입니다. nbr는 CellGraph와 detail.py `_grid_neighbors`의 int32라 `int[]`이고, donors_count는 int32라 `int[]`입니다.
- 묶음의 `receiver`는 FIELDS에서 int32입니다. 하지만 hydro에 넘기기 전에 호출자가 모두 int64로 바꿉니다(scorecard.py:216, water.py:97). 그래서 `CheckRcv(int[])` 오버로드는 두지 않고, 넓히기는 호출자가 맡습니다.
- nbr와 dist는 (N, K) 행 우선 1차원 배열입니다. K는 `nSlot`으로 따로 받습니다(N = 0이면 길이로 알 수 없음). (c, s) 원소는 `nbr[c * nSlot + s]`입니다.
- 정적 클래스 `Accumulate` 안에 메서드 `Accumulate`를 두면 CS0542 오류입니다. 그래서 이름이 겹칠 때만 클래스에 `Module`을 붙이는 안을 제안합니다(공통 결정 사항).
- 반환 배열은 늘 새로 할당합니다. 솔버는 반환된 rcv를 `_freeze_kernel`로 제자리에서 고치면서, prev(지난 rcv)도 읽습니다(solver.py:536-538).
- 커널과 힙 도우미는 internal로 두고, `InternalsVisibleTo("Bpcg.Tests")`로 커널 단위 대조 시험을 합니다. Python 인자 `out`은 C# 예약어라 `result`로 바꿉니다.

#### (b) 수치 함정 표

| 위치 | 종류 | 내용 | C# 방안 | 비교 | 확인 |
|---|---|---|---|---|---|
| depressions.py:20-23 | tie-break | 힙 키는 (z, 셀 번호) 사전식이고 `<`·`==`로 비교합니다. 그래서 ±0은 같은 높이로 보고 번호로 가립니다 | `za < zb \|\| (za == zb && ia < ib)`를 그대로 씁니다. `double.CompareTo`와 `(double, long)` 튜플 기본 비교자도 ±0을 같게 보므로 순서가 같습니다(키에 NaN 없음). 비트 패턴 비교와 z만 쓰는 우선순위는 금지합니다 | exact | 소스(dotnet/runtime `Double.CompareTo`, `ValueTuple.CompareTo`) |
| depressions.py:26-66 | heap-impl | 2진 힙입니다. 칸마다 한 번만 넣으므로 키가 겹치지 않고, (z, i) 순서로 꺼내는 큐라면 어느 구현이든 순서가 같습니다 | 2진 힙을 그대로 옮기고, `hi[0]`은 `HeapPop` 전에 읽습니다. `PriorityQueue`(4진 힙, 기본 `Comparer<TPriority>.Default`)에 (z, i) 튜플 우선순위를 줘도 같습니다 | exact | 복제(heapq) 9호출, 소스 |
| depressions.py:94-105 | queue-order·signed-zero | 웅덩이 FIFO를 힙보다 먼저 비우고, 비면 head·tail을 0으로 되돌립니다. 값은 순수 힙과 같지만 0의 부호는 다를 수 있습니다. 예: nbr `[[3,-1],[2,-1],[1,3],[0,2]]`, z `[-0,+0,+0,+0]`, 출구 {0,1}이면 numba는 out[2] = -0.0, 순수 힙은 +0.0입니다 | 같은 제어 흐름을 쓰고 pit은 `long[n]`으로 둡니다. 순수 힙으로 바꾸지 않습니다 | exact | 실험 |
| depressions.py:109, 152 | negative-index | `k < 0 or visited[k]`로 -1 이웃을 먼저 거릅니다. 순서를 바꾸면 numba는 음수 첨자를 끝에서 세어 조용히 읽습니다 | `if (k < 0 \|\| visited[k]) continue;` | exact | 코드 |
| depressions.py:113-118 | minmax-signed-zero | `z[k] <= zc`이면 zc를 부호째 복사하므로, 출구 -0.0 옆의 +0.0 칸이 -0.0이 됩니다. `Math.Max`는 IEEE 754:2019 maximum이라 (+0, -0)에서 +0을 내고 NaN을 퍼뜨립니다 | 삼항식 `z[k] <= zc ? zc : z[k]`를 그대로 둡니다 | exact | 실험, 소스(Math.Max) |
| depressions.py:149, 156 | float-add | `zmin = out[c] + eps`, `zk = z[k] > zmin ? z[k] : zmin`입니다. zmin은 -0이 될 수 없고 NaN도 없어 Math.Max와 결과가 같지만, 식은 그대로 둡니다. eps를 float로 바꾸면 값이 달라집니다 | double로 계산하고, eps는 TOML·manifest에서 double로 읽습니다 | exact | 복제 5호출 |
| solver.py:531 입력, depressions.py:142 | signed-zero(실데이터) | L0 솔버의 ε 채움 입력에서 출구 4112칸의 z가 정확히 +0.0이고, `out[c] = z[c]`가 부호째 복사합니다 | hydro는 부호를 그대로 옮깁니다. 끝에서 끝 비교에서는 상류(z_outlet)도 +0.0을 내야 비트가 같습니다 | exact | 기록 |
| depressions.py:166, 177; routing.py:169, 178, 181 | dtype-cast | z·zt·dist는 float64로 바꿉니다(float32는 정확히 넓혀짐). 마스크는 bool로 바꾸며 0이 아니면 참입니다. 0차원 입력은 `ascontiguousarray`가 (1,)로 만듭니다 | API는 `double[]`·`int[]`·`bool[]`만 받고, 넓히기는 호출자가 합니다. Npy의 bool 바이트는 `!= 0`으로 정규화합니다 | exact | 기록(모든 호출이 float64·int32·bool), 실험 |
| depressions.py:165-184; routing.py:134-196; accumulate.py:41-55; network.py:86-109 | validation | 검사 순서와 메시지가 정해져 있습니다. nbr는 부호 있는 정수만, rcv·order는 부호 없는 정수도 받습니다. prev는 `_check_rcv`를 먼저 거치므로 범위 오류 메시지가 'rcv'를 가리킵니다. 채움 뒤 방문 칸 수가 N보다 작으면 ValueError입니다 | 같은 순서와 같은 한국어 메시지를 씁니다. 예외 형은 공통 결정을 따르고, 메시지 안의 모양·실수 표기는 비교하지 않습니다 | n/a | 실험 |
| depressions.py:83-85, 195, 215; routing.py:89, 112, 197-198, 223 | uninit-memory | `np.empty` 버퍼는 쓴 뒤에만 읽습니다. 결과는 방문 수 = N 또는 m = N일 때만 돌려줍니다 | `new T[n]` | n/a | 코드 |
| routing.py:36-49 | argmax-tie | 엄격한 `>`로 고르고, 같은 경사면 슬롯이 아니라 번호가 작은 이웃을 고릅니다. 첫 후보는 조건 없이 받습니다. 평면은 `NEIGHBOR_SLOTS`가 래스터 순서라 슬롯 순서와 번호 순서가 같고, 구면은 n=32에서 654/6144행, n=16에서 318/1536행이 다릅니다. 중복 슬롯은 없고, -1 슬롯은 구면 꼭짓점 24행과 평면 가장자리에만 있습니다. 정확한 동률은 tiny 0, 시험 flat(jitter 0) 39, 로페 512² 503입니다 | `best < 0 \|\| sl > bestS \|\| (sl == bestS && k < best)`를 쓰고, `long best = -1`, `int k`로 둡니다 | exact | 실험, 기록 |
| routing.py:43-45 | strict-compare | 후보는 `zk < zc`인 이웃뿐입니다. tiny에는 z̃가 정확히 같은 이웃 쌍이 728개 있습니다(히어로 거친 50, 히어로 676, 선상지 뒤 2). 같은 칸에서 ε를 받은 형제에서 생깁니다. 그 행에는 경사 1.4e-6 이상인 다른 낮은 이웃이 있어 결과가 바뀌지 않습니다 | `<`를 그대로 쓰고 허용 오차를 두지 않습니다 | exact | 기록 |
| routing.py:45 | div-by-zero | numba 기본 `error_model='python'` 때문에 낮은 이웃의 dist가 ±0이면 ZeroDivisionError가 납니다. 64행 히스테리시스 나눗셈은 같은 슬롯을 45행이 먼저 나누므로 0에 닿지 않습니다 | 45행 앞에서 `d == 0.0`이면 `DivideByZeroException`을 던집니다. 64행 검사는 죽은 코드입니다 | n/a | 실험, 코드 |
| routing.py:45 | nan-semantics | dist는 검사하지 않습니다. NaN이 첫 후보 슬롯이면 경사 NaN으로 뽑히고, 뒤 슬롯이면 무시됩니다. inf는 경사 +0, 음수 dist는 음의 경사입니다 | 비교식을 그대로 옮기고, Python에 없는 검사는 더하지 않습니다 | exact | 실험(분석) |
| routing.py:55-68 | hysteresis | `p != best && p != c`이면 `nbr == p`인 첫 슬롯만 보고 break합니다. `zp < zc`이고 `!(bestS > (1.0 + eta) * sp)`이면 옛 셀을 지키므로, 같은 경사나 sp NaN이어도 지킵니다 | `!(a > b)` 모양을 지킵니다. `1.0 + eta`는 반복 밖으로 빼도 됩니다(eta 0.02에서 1.02와 같은 double). `sp + eta*sp`로 바꾸면 무작위 1e6개 중 116,709개가 다릅니다 | exact | 실험 |
| routing.py:71-74 | count | n_changed는 출구를 포함해 rcv ≠ prev인 칸 수이고, prev가 없으면 N입니다. prev가 있을 때의 값은 src가 버립니다 | 같은 식을 씁니다 | exact | 코드, 기록 |
| routing.py:37, 47 | numba-typing | best는 int64(-1)과 int32 k가 합쳐져 int64가 되고, sl·sp·best_s는 float64입니다 | `long best`, `int k`, `double` | exact | typemap(분석) |
| routing.py:197-198; solver.py:536-538 | aliasing | rcv·slope는 호출마다 새 배열이고, 솔버가 반환된 rcv를 고치면서 prev를 읽습니다 | 새 배열을 할당하고, prev 버퍼를 다시 쓰지 않습니다 | exact | 코드 |
| routing.py:79-96 | csr-order | 기여 셀을 c 오름차순으로 채우므로 수신 셀마다 번호 오름차순입니다 | 앞 합 CSR을 그대로 옮깁니다. `Dictionary`·`GroupBy`로 만들지 않습니다 | exact | 실험 |
| routing.py:100-128 | dfs-order | 자기 자신을 가리키는 칸(출구와 웅덩이 바닥)을 번호순으로 먼저 적습니다. 이어서 출구마다 명시 스택으로 전위 DFS를 하며, 기여 셀을 거꾸로 넣어 번호가 작은 칸부터 꺼냅니다. 가장 긴 흐름은 tiny 히어로에서 출구까지 86단계(반복 중 최대 88)입니다 | 힙 메모리에 둔 `long[n]` 스택을 씁니다. 재귀와 `stackalloc`은 쓰지 않습니다 | exact | 복제 64호출 |
| accumulate.py:14-20 | summation-order | order를 거꾸로 돌며 float64를 차례로 흩어 더하므로, 수신 셀마다 기여 셀 값이 번호 내림차순으로 더해집니다. 형제 순서를 바꾸면 칸의 2~15%가 끝자리에서 달라졌습니다 | 같은 순차 반복을 쓰고, order는 반드시 `TopoOrder` 출력을 씁니다 | exact | 복제 190호출, 실험(분석) |
| accumulate.py:49-55 | aliasing-copy | `w.copy()` 뒤에 누적합니다. 솔버는 같은 `w_water`·`area`·`w_sed`(solver.py:510-514)를 반복마다 다시 넘깁니다(solver.py:540-542) | `Clone()` 뒤에 누적하고, 스칼라는 `Array.Fill`로 채웁니다. (N,)이 아닌 배열은 오류로 보고 브로드캐스트하지 않습니다 | exact | 코드, 실험 |
| accumulate.py:43-48; network.py:102-109 | validation-gap | order는 범위만 검사하므로 중복이나 빠진 칸도 받습니다 | 더 엄격한 검사를 넣지 않습니다 | exact | 실험(분석) |
| network.py:14-21 | uninit-memory | `np.empty`라서 위상 순서가 아닌 order가 오면 쓰기 전에 읽습니다. C#은 0을 냅니다 | 맞추지 않고 기록합니다 | n/a | 실험(분석) |
| network.py:38-58 | out-of-bounds | 머리 칸이 order에 두 번 나오면 같은 구간을 두 번 걸어 `cells`(길이 = 강 칸 수) 밖에 씁니다. `cells[:m]`이 잘리므로 길이 검사도 통과합니다. rcv [0,0], order [1,1], 모두 강이면 `[[1,0],[]]`이 나왔습니다 | C#은 IndexOutOfRangeException을 냅니다. 재현하지 않고 기록하며 golden에서 뺍니다 | n/a | 실험 |
| network.py:25-59, 97-99 | ordering | 구간은 머리 칸이 order에 나오는 순서로 놓이고, 구간 안은 상류에서 하류 순입니다. 구간이 없으면 `[]`를 돌려줍니다 | (Cells, Offsets)를 잘라 `List<long[]>`를 만듭니다 | exact | 복제 2호출 |
| network.py:93-96 | validation-gap | 강 칸 수만 비교하므로 합류점이 낀 순환은 통과합니다 | Python과 같게 둡니다 | exact | 실험(분석) |
| docs/pipeline.md:239 | doc-mismatch | 문서는 `outlet_of -> int32`라고 적었지만 코드는 int64를 돌려줍니다 | 코드를 따라 `long[]`을 씁니다 | exact | 코드 |
| 결정 경계 전체 | upstream-sensitivity | 이산 결정은 D8의 `zk < zc`, 최대 경사와 동률 규칙, 히스테리시스 비교뿐입니다. 채움 출력은 입력의 max·min·+ε 조합이라 연속이므로, 끝자리 차이는 끝자리 차이로만 번집니다(±0 부호는 예외). tiny의 여유는 D8 최상위·차상위 상대 3.2e-8, 히스테리시스 경계 상대 1.7e-5(8,841회 평가)입니다 | 대조 시험은 Python 입력을 그대로 씁니다. 끝에서 끝 비교에서 갈림이 나오면 칸 수·위치·원인을 기록합니다 | n/a | 기록 |

grep으로 확인한 결과, hydro에는 다음이 없습니다.
- 정수 `//`·`%`, 정렬과 argsort, NaN·inf의 정수 변환
- numpy 합산과 BLAS, NEP 50 배열·스칼라 승격
- 초월 함수, 반올림, 백분위, linspace·arange, interp
- 마스크 원소 순서, 중복 인덱스 대입과 `add.at`, 정수 넘침
- set·dict 순서, 시간·git·환경 값, prange

`int(...)`는 `int(n_changed)`와 bool 개수 세기뿐입니다. 실수를 문자열로 바꾸는 곳은 예외 메시지뿐입니다.

#### (c) 외부 호출과 대체 방식

| Python 호출 | 쓰는 곳 | C# 대체 |
|---|---|---|
| `numba.njit(cache=True)`, `inline="always"` | 커널 12개 | 보통 정적 메서드로 옮기고, 힙 도우미에는 `[MethodImpl(MethodImplOptions.AggressiveInlining)]`를 붙입니다 |
| numba `error_model='python'`(기본) | routing.py:45 | 명시적인 0 검사와 `DivideByZeroException` |
| `np.ascontiguousarray(..., dtype)`, `.astype(np.int64, copy=False)` | 입력 정리 | 형이 정해진 배열을 받으므로 필요 없습니다 |
| `.min()`, `.max()`, `np.isfinite(...).all()`, `.any()`, `bool.sum()` | 입력 검사, 개수 | 반복문으로 옮깁니다. 정수와 불리언이라 순서와 무관하게 정확합니다 |
| `np.zeros`, `np.empty`, `np.empty_like`, `np.full`, `.copy()`, 슬라이스 copy | 버퍼, 출력 | `new T[n]`, `Array.Fill`, `Clone()`, `AsSpan(..).ToArray()` |
| `np.split(cells, offsets[1:-1])` | 강 구간 | offsets로 잘라 복사한 `List<long[]>` |
| `np.int64(x)`, `float(x)`, `int(x)` | 형 변환 | 암시적 넓히기 |

scipy·scikit-image·trimesh 호출이나 Bpcg.Numerics 도우미는 쓰지 않습니다. hydro는 CellGraph 형에도 기대지 않고 배열만 받습니다. `PriorityQueue`를 써도 순서는 같지만, 커널 단위 대조를 위해 2진 힙을 그대로 옮깁니다.

#### (d) numba 커널과 prange

| 커널 | 반복 순서 | Parallel.For | 비고 |
|---|---|---|---|
| `_fill_kernel` | 출구를 c 오름차순으로 넣은 뒤, pit FIFO를 먼저 꺼내고 그다음 힙의 (z, i) 최소를 꺼냅니다 | 아니오 | 방문 칸 수를 돌려줍니다. pit 순서가 ±0 부호를 정합니다 |
| `_fill_epsilon_kernel` | 출구를 넣은 뒤 힙의 (z, i) 최소를 꺼냅니다 | 아니오 | 순수 힙이며, 값은 ±0까지 꺼내는 순서와 무관합니다 |
| `_key_less`, `_heap_push`, `_heap_pop` | 해당 없음 | 해당 없음 | Python에서 직접 부를 수 있어 커널 단위 golden을 만들 수 있습니다 |
| `_d8_kernel` | c 오름차순, 슬롯 0..K-1 | 아니오 | 칸마다 독립이지만 Python이 순차라 순차로 둡니다 |
| `_donor_csr_kernel`, `_topo_kernel` | c 오름차순, 명시 스택 DFS | 아니오 | 앞 합과 스택 순서에 의존합니다 |
| `_accumulate_kernel` | order 역순 | 아니오 | 흩어 더하는 순서가 결과 비트를 정합니다 |
| `_donors_count_kernel`, `_outlet_kernel`, `_segments_kernel` | c 오름차순 또는 order 순 | 아니오 | 정수만 다룹니다 |

prange와 축약 변수가 없으므로 결과는 `compute.numba_threads`와 무관합니다. boundscheck가 기본값(꺼짐)이라, 잘못된 입력에서 numba는 범위 밖을 조용히 읽고 씁니다. C#은 같은 입력에서 예외를 냅니다((e) 3·4). 정적 가변 상태가 없어 단계 4에서 주 스레드 밖에서 불러도 안전합니다.

#### (e) 의심 버그 (고치지 않고 기록만 합니다)

1. docs/pipeline.md:239의 `outlet_of -> int32`가 코드(network.py:62-70, int64; test_hydro.py:193)와 다릅니다. 출력에는 영향이 없습니다.
2. `river_segments`(network.py:93-96)는 순환을 잡는다고 안내하지만 강 칸 수만 비교합니다. 그래서 rcv=[1,2,0,0], 모두 강이면 오류 없이 [[0,1,2],[3]]을 돌려줍니다. 파이프라인에서는 topo_order가 먼저 순환을 막으므로 영향이 없습니다.
3. `outlet_of`와 `accumulate`는 order가 같은 rcv의 위상 순서(순열)인지 검사하지 않습니다. 맞지 않으면 `_outlet_kernel`이 초기화되지 않은 메모리를 조용히 돌려줍니다. 지금은 모든 호출자가 같은 rcv의 topo_order 결과를 넘깁니다.
4. (추가) `_segments_kernel`(network.py:38-58)은 order에 머리 칸이 중복되면 `cells` 밖에 씁니다. 같은 머리를 6번 넣은 사슬에서는 30칸을 범위 밖에 썼습니다. 오류 없이 힙 메모리를 덮어쓰고 빈 구간을 돌려주며, 길이 검사도 통과합니다. 정상 경로(topo_order 순열)에서는 생기지 않습니다.
5. `d8_receivers`는 dist를 검사하지 않습니다. dist가 0이면 conventions 5절이 정한 ValueError가 아니라 ZeroDivisionError(45행)가 나고, NaN이면 경사 NaN인 수신 셀이 조용히 나옵니다. CellGraph에서는 생기지 않습니다.
6. (추가, 문서) depressions.py 모듈 docstring(9-11행)과 `_fill_kernel` docstring(76-78행)은 결과가 처리 순서와 무관하고 순수 힙과 같다고 적었습니다. 값은 맞지만 0의 부호는 다를 수 있습니다((b) 표의 4칸 사례). 영향은 ±0 비트뿐입니다.

#### (f) golden 대조 계획

`csharp/golden/export_golden.py`는 tiny `all` 흐름(지구본 제외)을 out/golden 아래에 돌립니다. 이때 hydro를 부르는 모듈의 속성을 기록 래퍼로 바꿔 호출 인자를 저장하며, src는 고치지 않습니다(이 방식은 열린 질문입니다). 여기에 tiny에 없는 경계와 동률을 보려고 시험 입력과 손 사례를 더합니다. 그래프 배열(nbr, dist)은 단계별 공유 파일 하나에 둡니다.

비교 규칙은 다음과 같습니다.
- 사칙연산·비교·정수만 쓰고 순서가 같으므로 모두 exact(비트 단위)입니다. ±0도 비트로 봅니다.
- NaN은 입력 dist의 NaN이 전파되는 사례만 넣습니다. inf/inf처럼 새로 생기는 NaN은 x64와 arm64에서 부호 비트가 다를 수 있어 넣지 않습니다.
- Python 동작이 정의되지 않은 입력(위상 순서가 아닌 order, 머리가 중복된 order)은 넣지 않습니다.

| 대상 | 입력 | 출력 | 크기(압축) | 비교 |
|---|---|---|---|---|
| `FillEpsilon` / 커널 | solver L0 첫 호출(6144칸, 출구 4112칸 z = +0.0), 히어로 거친 격자(256칸, 34칸 오름), 히어로 4096칸(433칸 오름), `reroute_after_fans`(42칸 오름) | z̃, 방문 칸 수 | 호출마다 30~60 KB, 공유 그래프 별도(행성 약 330 KB, 히어로 약 180 KB) | exact |
| `FillDepressions` / 커널 | `subsurface.water`(행성, 히어로 호수 42칸), `bake.detail` 2회(7701칸, 두 번째 86칸 오름) | ẑ, 방문 칸 수 | 30~110 KB | exact |
| 채움 손 사례 | ±0 고원(평면 24², 구면 n=6), 출구 -0.0 사슬, pit과 순수 힙이 갈리는 4칸 사례, \|z\| 1e13·2^44, 끊긴 칸, 출구 없음, eps 0·NaN·inf, z NaN, nbr 범위 밖, N=0, K=0 | 출력 또는 예외 종류 | 20 KB 미만(커밋) | exact |
| `HeapPush`/`HeapPop`/`KeyLess` | ±0과 같은 z가 섞인 (z, i) 40개를 넣고 모두 꺼냄 | 꺼낸 순서, 단계별 hz·hi·size | 5 KB(커밋) | exact |
| `D8Receivers` / 커널 | solver L0 1·2회차, 히어로 1회차·마지막 회차(prev 있음, eta 0.02), `reroute_after_fans` | rcv, slope, n_changed | 호출마다 50~60 KB | exact |
| D8 손 사례 | 3×3 동률, 뒤 슬롯에 있는 작은 번호와의 동률(구면 n=4 면 경계), 히스테리시스 7종(같은 경사 유지, sp NaN, p == c, p가 이웃 아님, 옛 셀이 더는 낮지 않음 등), dist NaN(첫·뒤 슬롯)·inf·음수·0, K=0, N=0 | rcv, slope, n_changed 또는 예외 종류 | 10 KB 미만(커밋) | exact |
| 시험 지형 3종 | tests/test_hydro.py `CASES`: flat 64² jitter 0(D8 정확 동률 39), flat_jitter, sphere n=16. 입력 생성 코드는 스크립트에 옮겨 적고 입력 배열을 저장합니다 | ẑ, z̃, rcv, slope, order, acc(area·P), 강 구간 | 사례마다 약 0.5 MB(out/golden만) | exact |
| 히스테리시스 근평탄 사례 | `test_hysteresis_reduces_changes_on_nearly_flat_surface` 설정(평면 64² jitter 0.4 seed 3, prev = rcv0, eta 0과 0.02, n_changed 68과 31) | rcv, slope, n_changed | 약 0.5 MB(out/golden만) | exact |
| 로페 512² | tests/fixtures/lope_tile512_30m.npz(float32를 float64로 넓히고 행을 뒤집음, 출구는 가장자리). 호수 27,522칸, D8 정확 동률 503, 같은 높이 이웃 쌍 49,170 | ẑ, z̃, rcv, slope, order, A | 약 10 MB(out/golden만, 입력은 커밋된 fixture) | exact |
| `DonorLists`·`TopoOrder`·`CheckRcv` | tiny rcv(행성, 히어로, 선상지 뒤), 시험 나무, 숲 2,000칸, 사슬 10만 칸, 별(기여 셀 1만), 순환, uint8·int32 rcv(Python의 넓히기 확인용) | start, donors, order 또는 예외 | tiny 각 30 KB, 사슬 약 300 KB(커밋 안 함) | exact |
| `Accumulate` / 커널 | solver 마지막 회차의 w_water·area·w_sed(행성, 히어로), `reroute_after_fans`, drainage.py:499, 순서에 민감한 별(0, 1e16, 1, -1e16), 스칼라, 중복 order | acc, 입력 weight가 바뀌지 않았는지 | 40~80 KB | exact |
| `DonorsCount`, `OutletOf` | tiny rcv·order, 시험 나무 | int32, int64 | 10~50 KB | exact |
| `RiverSegments` / 커널 | pipeline 호출(행성 1693구간 2032칸, 히어로 1구간 24칸), 손 사례 7종 | cells, offsets(int64) | 30 KB 미만 | exact |

#### (g) 포팅 순서와 위험 메모

hydro는 core에서 nbr 표현만 받으면 되므로 단계 2의 첫 묶음으로 적당합니다. 포팅 순서는 다음과 같습니다.
1. `Routing.CheckRcv`, `DonorCsrKernel`, `TopoKernel`, `DonorLists`, `TopoOrder`
2. `AccumulateModule`
3. `Network`
4. `Depressions`: 힙 도우미를 먼저 커널 단위로 시험합니다
5. `D8Receivers`

위험 메모:
- 정확도 위험은 낮습니다. 대부분은 식을 '정리'하다 생깁니다.
    - `Math.Max`, `a <= b`로 바꾼 부정 비교, `sp + eta*sp`
    - pit FIFO를 뺀 순수 힙, 재귀 DFS, 슬롯 순서 동률
    - 제자리 누적, prev 버퍼 재사용, float eps
- 끝에서 끝 비교에서 hydro가 원인인 방향 갈림은 입력(z, dist)의 끝자리 차이가 D8 여유(상대 3.2e-8)나 히스테리시스 여유(상대 1.7e-5)보다 클 때만 생깁니다. tiny에서는 갈림을 예상하지 않습니다.
- 채움 자체는 연속이라 갈림이 없습니다. 다만 ẑ의 끝자리가 바뀌면 water.py의 호수 판정(ẑ − z > 1e-3 m) 같은 하류 문턱에 닿을 수 있으며, 그 여유는 subsurface 담당이 잽니다.
- 성능 기준은 Python의 1280²(157만 칸) 상한입니다. 채움 두 함수는 3 s 미만, D8·순서·누적은 1 s 미만입니다(`test_benchmark_1280_squared`). C# 실측값은 단계 3에서 적습니다.
- 열린 질문:
    - 셀 번호 원소형(long 제안), CS0542 규칙, (N, K) 표현, 예외 형
    - 커널 internal 공개, golden 캡처 방식, 강 구간 CSR
    - 문서 불일치 처리, golden 압축, 정의되지 않은 입력에서의 C# 예외, Compare 도우미의 NaN 규칙

### planet ① — plates·crust (비평 거침)

범위는 `src/bpcg/planet/__init__.py`(5줄), `plates.py`(691줄), `crust.py`(286줄)입니다. 계약은 docs/pipeline.md 4.1·4.2에 있고, 경계 사례는 tests/test_planet.py에 있습니다. 조사 환경은 macOS 26.6.2 arm64, Python 3.13.15, numpy 2.5.3(BLAS Accelerate), numba 0.68.0입니다. 아래 '검증'은 그 기계에서 돌린 실험 결과이고, 실험 스크립트는 단계 0 작업 폴더에만 두었습니다. 이 절은 분석 초안을 비평 단계에서 다시 실험해 고친 판입니다.

핵심 결론은 다음과 같습니다.

- 범위 안 numba 커널은 5개(prange 4개)입니다. 4개는 사칙연산·sqrt·비교만 쓰고, `_soft_plate_bias_kernel`만 스칼라 `np.exp`(= libm exp)를 씁니다. 같은 순서를 순수 Python으로 재현한 값과 비트 단위로 같았고, numba 1·12 스레드 결과도 같았습니다.
- 초월 함수가 직접 들어오는 곳은 네 곳입니다.
  - 판 씨앗·오일러 극 축의 Box–Muller(`math.log/cos/sin`)
  - 대륙 점수 소프트맥스의 `np.exp`
  - 지각의 `np.exp` 3곳(해양 깊이, 해구, 충돌 두께)
  - core `nearest_source`의 sin·atan2
- 간접 유입도 있습니다. core 그래프의 pos·area·dist는 `np.tan`·`np.arctan2`로 만듭니다. 이 기계에서 numpy float64 exp·sqrt·tan·arctan2·sin·cos는 `math`(libm)와 각 20만 개에서 비트 단위로 같았습니다. .NET 10 `Math.*`도 C 런타임 libm을 부르므로(공통 장), macOS에서는 비트 일치를 기대합니다.
- macOS libm은 올바른 반올림이 아닙니다(decimal로 재현 확인).
  - seed 0·27 씨앗·축의 cos 96회 중 7회, sin 48회 중 2회가 0.51–0.60 ulp 오차였습니다. log 96회는 모두 올바른 반올림이었습니다.
  - tiny 소프트맥스 exp는 16896개 중 32개가 올바른 반올림이 아니었습니다.
  - 따라서 리눅스·윈도우의 C#은 이 값들에서 1 ulp 다를 수 있습니다.
- 초월 함수 값에 기대는 범주 결정은 여유가 큽니다(아래 표). 씨앗·축, 소프트맥스 출력, 거리에 상대 1e-9 섭동을 넣어도 범주가 바뀌지 않았습니다.
- **그러나 core 그래프 pos에는 여유가 없습니다.** `nearest_source`는 키(현 길이 제곱)가 같으면 번호가 작은 출발 칸을 고르는데, 큐브스피어 대칭 때문에 정확한 동률과 1 ulp 근접 동률이 있습니다.
  - tiny 수렴 출발 칸에서 직접 거리가 정확히 같은 후보가 있는 칸이 6개, 상대 간격 1.1e-16인 칸도 있었습니다.
  - pos 성분마다 −1·0·+1 ulp를 무작위로 더해 20번 돌리면, tiny 13번·seed 72 12번에서 `convergence_kind`·`subduction_side`가 바뀌었습니다(합계 13칸·26칸).
  - 값도 크게 뜁니다. tiny 최대 차이는 `dist_convergent_m` 173 km, `z_platform_m` 150 m, `ocean_age_myr` 5.5 Myr, `convergence_m_per_yr` 0.026 m/yr입니다.
  - seed 72에서 area·dist만 섭동하면(10회) 변화가 0이었고, pos만 섭동하면 수렴 출발 칸 기준 src가 10회에서 43칸 바뀌었습니다.
  - 따라서 끝에서 끝 범주 일치의 전제는 core 그래프(pos, `Unit()`)의 비트 일치입니다. `Unit()`은 `pos / R`과 632/1536칸에서 다르므로 행마다 `pos / Norm3(pos)`를 그대로 옮겨야 합니다.

| 격자(면당 n) | 칸 수 | argmax 최소 간격 | 경계 판정 최소 상대 여유 | 대륙 자르기 점수 간격(상대) | 자르기 결정 여유(상대) | 해양판 비율과 0.5의 거리 | 해양-해양 나이 최소 상대 차 | 70 Myr 계단 최소 상대 여유 |
|---|---|---|---|---|---|---|---|---|
| 16 (tiny 거친 격자) | 1536 | 3.1e-5 | 6.4e-4 | 1.7e-3 | 3.4e-4 | 1.9e-2 | 7.8e-3 | 3.5e-4 |
| 24 (tests) | 3456 | 9.7e-6 | 1.2e-3 | 2.1e-3 | 7.2e-5 | 2.9e-2 | 4.3e-3 | 1.4e-4 |
| 128 (laptop 거친 격자) | 98304 | 4.1e-7 | 1.4e-3 | 3.9e-5 | 1.8e-5 | 1.3e-2 | 2.7e-2 | 2.2e-5 |
| 256 (lab 거친 격자) | 393216 | 2.9e-7 | 2.1e-4 | 6.8e-6 | 4.9e-6 | 1.0e-2 | 1.5e-2 | 8.2e-6 |

뒤집기가 일어나는 n=16 사례에서는 모든 회차의 경계 판정을 쟀습니다. 최소 상대 여유는 seed 72 1.8e-3, seed 320 1.4e-2, seed 51(판 16개) 3.7e-3입니다. seed 72의 70 Myr 계단 여유는 1.2e-5입니다. 계단에서 깊이는 78.0 m 뜁니다(5428.3 m → 5350.3 m).

범주 완전 일치 규칙은 네 겹으로 지킵니다.

0. 전제: core 그래프의 pos·nbr·dist·area·spacing·`Unit()`이 Python과 비트 단위로 같은지 먼저 대조합니다. 다르면 planet 끝에서 끝 대조에서 갈리는 칸이 생깁니다. 다른 OS에서 libm 때문에 다르면, 허용 오차를 넓히지 않고 갈린 칸의 수·위치·원인을 진행 기록에 적습니다.
1. 함수·커널 단위 대조는 Python 입력(그래프, 씨앗, ω, 거리 포함)을 그대로 넣습니다. 그러면 범주와 사칙연산 실수가 모든 OS에서 비트 단위로 같아야 합니다.
2. 끝에서 끝 대조에서는 0이 성립할 때 표의 여유가 libm 차이(상대 2.2e-16)보다 10⁹배 이상 크므로 범주가 같습니다.
3. C# 시험에 margin guard를 둡니다. 표의 7가지 여유를 C# 값으로 다시 재고, 상대 1e-10보다 작아지면 실패시킵니다. 경계 판정 여유는 `EnsureDivergentBoundaries`가 부르는 모든 회차에서 잽니다. nearest_source 동률은 설계상 0인 경우가 있으므로 guard가 아니라 0번 전제로 다룹니다.

#### (a) 대응표

`__init__.py`는 docstring만 있고 다시 내보내는 이름이 없으므로 C# 파일을 만들지 않습니다. 비공개 함수와 커널은 golden 대조를 위해 `internal`로 두고 InternalsVisibleTo로 Bpcg.Tests에 공개합니다. 반환 dict의 필드 묶음 형은 core가 정하는 형(가칭 `FieldSet`)을 따릅니다. 이 형은 FIELDS가 float32인 필드(`convergence_m_per_yr` 등)도 메모리에서는 `double[]`로 들고 있어야 합니다. crust·uplift·materials가 float64 값을 그대로 읽고, float32 변환은 bundle에서만 하기 때문입니다.

| Python (`bpcg.planet.plates`) | C# (`Bpcg.Planet.Plates`, csharp/Bpcg/Planet/Plates.cs) |
|---|---|
| `STREAM_PLATE_SEED/POLE/SPEED/WARP` (301–304) | `const long StreamPlateSeed, StreamPole, StreamSpeed, StreamWarp` |
| `MAX_FLIP_ROUNDS`, `BOUNDARY_SMOOTH_ITERATIONS` | `const int MaxFlipRounds = 3, BoundarySmoothIterations = 4` |
| `BOUNDARY_NONE`, `CONVERGENT`, `DIVERGENT`, `TRANSFORM`, `KIND_*` (0–3) | `const byte BoundaryNone, Convergent, Divergent, Transform`, `const byte KindNone, KindOceanContinent, KindOceanOcean, KindCollision` (public: uplift는 KIND 3개, materials는 KIND 2개, crust는 KIND 3개를 씁니다. climate는 `SubSeed`만 씁니다) |
| `_MYR` | `private const double Myr = 1.0e6` |
| `check_sphere_graph`, `check_bool_mask` | `void CheckSphereGraph(CellGraph)`, `bool[] CheckBoolMask(bool[] mask, int nCells, string name)` (dtype 검사는 형으로 대체) |
| `sub_seed` | `long SubSeed(long seed, long stream)` (crust·uplift·climate가 씀) |
| `hashed_unit_vectors`, `plate_seeds`, `plate_omegas` | `double[] HashedUnitVectors(long seed, long stream, int count)`, `double[] PlateSeeds(Config)`, `double[] PlateOmegas(Config, double? radiusM = null)` (모두 M×3 행 우선) |
| `_argmax_dot_kernel`, `assign_plates` | `internal void ArgmaxDotKernel(double[] q, double[] seeds, int[] output)`, `int[] AssignPlates(double[] unit, double[] seeds, Config)` |
| `_classify_kernel`, `classify_boundaries` | `internal void ClassifyKernel(unit, nbr, dist, plate, omega, radius, kappa, btype, gamma, tau, other, halfGap)`, `BoundaryClassification ClassifyBoundaries(CellGraph, int[] plateId, double[] omega, double kappa)`, `record BoundaryClassification(byte[] BoundaryType, double[] Gamma, double[] Tau, int[] Other, double[] HalfGap)` |
| `_smooth_on_cells_kernel`, `smooth_along_boundary` | `internal double[] SmoothOnCellsKernel(double[] values, int[] cells, bool[] member, int[] plate, int[] nbr, int iterations)`, `double[] SmoothAlongBoundary(double[] values, bool[] member, int[] plate, int[] nbr)` |
| `same_plate_graph`, `_boundary_distance` | `CellGraph SamePlateGraph(CellGraph, int[] plateId)` (Pos·Dist·Area 공유, Origin은 Python처럼 기본값), `internal (double[] Dist, long[] Src) BoundaryDistance(CellGraph, bool[] isSource, double[] halfGap)` |
| `_age_consistent_rate_floor`, `seafloor_age_myr` | `internal double[] AgeConsistentRateFloor(double[] distDiv, int[] plate, bool[] continental, int m, double capMyr)`, `double[] SeafloorAgeMyr(double[] distDivergentM, double[] spreadingMPerYr, double capMyr)` |
| `_other_side_kernel`, `_subduction_polarity` | `internal void OtherSideKernel(int[] nbr, int[] plate, bool[] continental, double[] age, int[] cells, double[] contFrac, double[] otherAge)`, `internal (byte[] Kind, sbyte[] Side) SubductionPolarity(CellGraph, int[] plate, int[] other, bool[] isConv, bool[] continental, double[] ageMyr, int nPlates)` |
| `_plate_centroids`, `_away_axis` | `internal double[] PlateCentroids(double[] unit, double[] area, int[] plate, int m)`, `internal double[] AwayAxis(ReadOnlySpan<double> cJ, ReadOnlySpan<double> cK)` |
| `ensure_divergent_boundaries` | `DivergentFix EnsureDivergentBoundaries(CellGraph, int[] plateId, double[] omega, bool[] continental, double kappa, double minRate, int maxRounds = MaxFlipRounds)`, `record DivergentFix(double[] Omega, List<FlipRecord> Flips, BoundaryClassification Cls, List<int> Remaining)`, `record FlipRecord(int Round, int Plate, int Neighbor, string Method)` |
| `generate_plates` | `(FieldSet Fields, PlatesInfo Info) GeneratePlates(CellGraph, bool[] continental, Config)`. `PlatesInfo.ToJsonable()`은 Python 키 이름·삽입 순서를 그대로 냅니다 |

| Python (`bpcg.planet.crust`) | C# (`Bpcg.Planet.Crust`, csharp/Bpcg/Planet/Crust.cs) |
|---|---|
| `STREAM_CONTINENT_NOISE/BIAS` (311·312) | `const long StreamContinentNoise, StreamContinentBias` |
| `CONTINENT_*` 상수 4개 | `const double ContinentNoiseFrequency = 1.2`, `const int ContinentNoiseOctaves = 4`, `const double ContinentPlateBias = 0.3, ContinentBiasSharpness = 12.0` |
| `MARGIN_EDGE_FRACTION`, `TRANSITION_HALF_WIDTH_M`, `PS_*` | `const double MarginEdgeFraction, TransitionHalfWidthM, PsSqrtCoefM, PsBreakMyr, PsDeepM, PsDeepAmpM, PsTauMyr` |
| `CONTINENTAL`, `OCEANIC` (범위 안에서 쓰는 곳 없음) | `const byte Continental = 1, Oceanic = 0` |
| `smoothstep` | `double Smoothstep(double x)`, `double[] Smoothstep(double[] x)` |
| `_soft_plate_bias_kernel`, `continent_score`, `continent_mask` | `internal void SoftPlateBiasKernel(double[] unit, double[] seeds, double[] bias, double sharp, double[] output)`, `double[] ContinentScore(CellGraph, Config)`, `bool[] ContinentMask(CellGraph, Config)` |
| `ocean_depth_m`, `airy_elevation_m`, `trench_offset_m` | `double[] OceanDepthM(double[] ageMyr, double ridgeDepthM)`, `double[] AiryElevationM(double[] thicknessM, Config)`, `double[] TrenchOffsetM(double[] distConvergentM, sbyte[] subductionSide, byte[] convergenceKind, Config)` |
| `_edge_cells` | `internal (bool[] Edge, double[] Half) EdgeCells(int[] nbr, bool[] region, double[] dist)` |
| `generate_crust` | `(FieldSet Fields, CrustInfo Info) GenerateCrust(CellGraph, FieldSet plateFields, bool[] continental, Config)`, `record CrustInfo(double[] ZPlatformBaseM, double[] TrenchM, double[] CoastSignedM)` |

원소 형은 다음과 같습니다.

- 범위 안 실수는 모두 `double`이고 float32 계산은 없습니다.
- `plate_id`·`other`는 `int`, `boundary_type`·`convergence_kind`·`crust_type`은 `byte`, `subduction_side`는 `sbyte`, 대륙 마스크는 `bool[]`, `nearest_source`의 src는 `long[]`입니다.

#### (b) 수치 함정 표

범위 안에는 정수 `//`·`%`, 실수→정수 변환, 반올림, percentile이 없습니다. 실수는 모두 float64이고, NEP 50 아래에서 Python 실수·정수와 섞인 배열도 원래 dtype을 유지합니다(실험으로 확인했습니다). 공통 규칙은 공통 장을 따르고, 여기에는 이 범위의 사례만 적습니다.

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| plates.py:72 | uint64-shift | `hash3(...) >> np.uint64(1)`은 np.uint64이고, int() 뒤에 [0, 2^63)이 됩니다. 음수 시드는 uint64로 재해석합니다 | unchecked `(long)(Hashing.Hash3(seed, stream, 0) >> 1)` | exact |
| plates.py:81-87 | libm | Box–Muller log·cos·sin. `1.0 - u`는 정확하고, u=0이면 sqrt(-0.0) = -0.0입니다 | `Math.Log/Cos/Sin`, `2.0 * Math.PI * u` 순서 유지. macOS에서는 비트, 그 밖에서는 성분당 4·2^-52 | tolerance |
| plates.py:90·517·522·579 | blas-dot | 1-D norm은 `x.dot(x)` → cblas_ddot(Accelerate)입니다. 3원소에서 순차 합과 60만 개 모두 같았고, FMA 사슬과는 91%만 같았습니다 | `LinAlg.Norm3` = `Math.Sqrt((x*x + y*y) + z*z)` | exact |
| plates.py:91 | zero-norm | norm ≤ 0이면 (0,0,1)입니다. 도달하지 않습니다 | 분기 유지, 나눗셈 3번(역수 곱 금지) | exact |
| plates.py:111-122 | op-order·검사 | `axes * (speeds / radius)[:, None]`. speed_m_per_yr 길이가 2가 아니면 튜플 풀기 ValueError가 나고, radius는 검사하지 않습니다 | `axes[3k+i] * (speeds[k] / radius)`. 길이 2 검사는 같은 범주 예외 | exact |
| plates.py:133-136 | argmax-tie | 내적 ((q0s0+q1s1)+q2s2), 엄격한 `>`라 첫 최댓값, NaN 행은 0번 판 | 같은 루프, `Parallel.For` | exact |
| plates.py:151-152·509 | axis-norm | `norm(axis=1)`은 길이 3 add.reduce라 순차 합입니다(60만 행 일치) | 행마다 Norm3 뒤 3번 나눔 | exact |
| plates.py:199-220 | op-order | b̂ 투영·정규화, Δω, Δv·R, `sg += -(…)`, `st += abs(…)`의 순서 | 문장 단위로 그대로, FMA 금지 | exact |
| plates.py:222-231 | tie-break | 반 칸 거리는 엄격한 `<`, other는 맞닿은 슬롯 수 최다이고 같으면 작은 번호 | 같은 이중 루프 | exact |
| plates.py:239-246 | float-threshold | `gm = sg/cnt` 뒤 `gm > κ·tm` 수렴, `gm < (−κ)·tm` 발산 | 같은 비교. 여유는 모든 회차에서 guard | exact |
| plates.py:189-191·318·432 | sentinel | nbr −1(n=16 코너 24 슬롯)을 `v < 0`으로 건너뜁니다. 432행은 단락 평가에 기댑니다 | 검사 순서·단락 평가 유지 | exact |
| plates.py:309-323 | jacobi | 직렬 야코비. 슬롯 순서로 더한 뒤 `s / cnt` | 같은 직렬 루프 | exact |
| plates.py:349-364 | graph-copy | nbr_same은 int32입니다. Pos·Dist·Area를 공유하고, origin은 넘기지 않아 기본값 (0,0)이 됩니다 | Origin 기본값(구면이라 결과 같음), 공유 배열 수정 금지 | exact |
| plates.py:391-392 | scatter-max | `np.maximum.at`은 순서와 무관합니다. 분모 `cap*1e6`을 먼저 계산합니다 | 루프 뒤 `dMax / (capMyr * Myr)` | exact |
| plates.py:405-408 | clip | `(d/v)/1e6`. `v > 0`이 아니면 cap입니다. clip은 NaN을 통과시키고 −0.0을 유지합니다(크기 1–1536 확인) | `Math.Clamp`(v10.0.0도 비교식 구현이라 −0.0·NaN 동작이 같음) | exact |
| plates.py:440 | int-division | `n_cont / n_other >= 0.5`. 정확히 0.5인 칸이 tiny 6칸 있습니다 | `(double)nCont / nOther >= 0.5` | exact |
| plates.py:486-490 | scatter-add | `np.add.at`은 첨자 순서대로 더합니다. 자기 쪽 나이를 모두 더한 뒤 상대 쪽 나이를 더합니다 | oo 오름차순으로 두 번 | exact |
| plates.py:492-497 | float-compare | 판 쌍 평균 나이로 섭입판을 정합니다. NaN이면 작은 번호 | 같은 비교식 | exact |
| plates.py:505-510·554-555·622-623 | bincount | 가중 bincount는 칸 순서대로 더하고, 곱을 먼저 계산합니다 | 루프, `norm > 0 ? cen / norm : 0.0` | exact |
| plates.py:516-523 | cross/argmin | `np.cross` = (a1b2−a2b1, a2b0−a0b2, a0b1−a1b0), 기준 `< 1e-9`, argmin은 첫 최솟값 | `LinAlg.Cross3`·`Norm3`, 엄격한 `<` | exact |
| plates.py:550·576·579 | copy/sign/max | ω 복사, `−ω`는 0을 −0.0으로 바꿉니다, Python `max`는 같으면 앞 값 | `Clone()`, `-x`, `PyMath.PyMax(a, b)` = `b > a ? b : a` | exact |
| plates.py:554-557 | nan-to-num·동률 | 0/0 → 0이라 `> 0.5`가 거짓입니다. n=4·판 24개처럼 작은 대칭 격자에서는 비율이 정확히 0.5인 판이 생깁니다 | `pa > 0 ? oa / pa : 0.0`. area 비트 일치가 전제 | exact |
| plates.py:561-585 | loop-order | 회차·failing 오름차순, nb 정렬, 같은 회차 이웃 미룸, j는 첫 argmax, negate 다음 away, 기록 키 순서. `max_rounds < 0`이면 UnboundLocalError | 같은 순서. maxRounds < 0은 ArgumentOutOfRangeException(차이 기록) | exact |
| plates.py:642-662 | src-tie | src가 γ·half_gap·kind·side를 넘겨줍니다. nearest_source 동률(키 같으면 작은 칸 번호) 때문에 pos 1 ulp 차이로 src가 바뀌고 범주가 바뀝니다 | core NearestSource의 힙 연산·동률 규칙을 그대로 옮기고, pos 비트 일치를 전제로 둡니다 | exact |
| plates.py:648-651 | np-maximum | arm64 numpy `np.maximum(±0.0, ∓0.0)`은 +0.0으로 `Math.Max`와 같습니다. 실제로 ±0이 맞붙지 않습니다 | `Math.Max` | exact |
| plates.py:659-662 | masked-gather | src < 0인 칸은 0번 칸을 읽고 버립니다 | `src >= 0 ? gamma[src] : 0.0` | exact |
| plates.py:674-690 | json | info는 manifest meta.info.plates로 갑니다. 키 삽입 순서, repr 실수, 1024개 초과 배열 요약, NaN은 null, −0.0은 -0.0 | `PlatesInfo.ToJsonable()` + PyJson. seeds·ω는 수치로 비교 | tolerance |
| crust.py:56-57 | clip | clip은 −0.0을 유지하지만 `t*t`라 결과 부호는 사라집니다 | `Math.Clamp(x, 0.0, 1.0)` 뒤 `t * t * (3.0 - 2.0 * t)` | exact |
| crust.py:77 | libm | numba 스칼라 exp = libm exp(16896개 재현 일치). 그중 32개는 올바른 반올림이 아닙니다 | `Math.Exp(sharp * (d - best))`. macOS에서는 비트, 그 밖에서는 1e-15 | tolerance |
| crust.py:118 | stable-sort | −score 오름차순, 같으면 번호순. numpy는 NaN을 끝에 두고, Python은 예외를 내지 않습니다 | (−score, 번호) 비교 정렬. NaN은 numpy처럼 뒤로(예외로 바꾸지 않음) | exact |
| crust.py:119-127 | cumsum/search | 순차 누적, target은 끝값 기준. searchsorted left, k==0은 `<`, 그 밖은 `<=`, `k >= n`은 도달하지 않음 | 순차 루프, 직접 짠 하한 탐색(`Array.BinarySearch` 금지) | exact |
| crust.py:140-143 | step/exp | `np.maximum`은 NaN을 전파합니다. float64 `np.exp` = libm. `t < 70` 계단은 78.0 m 불연속이라 범주처럼 다룹니다 | `t < 70 ? young : old`. 계단 여유를 guard에 넣음 | tolerance |
| crust.py:157·238 | py-max | Python `max(rho_c, rho_w)`, `max(margin, 5e4)` | `PyMath.PyMax` | exact |
| crust.py:159-162 | op-order | 밀도 비를 먼저 계산합니다(0.15151515151515152, 1.4537444933920705) | 같은 괄호 | exact |
| crust.py:181·256 | pow/zero/subnormal | `(d/w)**2`는 x·x와 같습니다. 해구는 언더플로하면 −0.0(tiny 352칸)이고, 비정규수 결과도 있습니다(tiny 3칸, 최소 7.2e-318, laptop 217칸). 충돌 두께는 d/W ≤ 25라 비정규수가 없습니다 | `-D * Math.Exp(-(x * x))`. FTZ를 켜지 않습니다 | exact(0)·tolerance |
| crust.py:185-193 | min-axis | 반 칸 거리는 `min(axis=1)·0.5`, 가장자리가 아니면 0 | 슬롯 루프, 엄격한 `<` | exact |
| crust.py:235 | nan-bits | numpy NaN은 0x7FF8000000000000, float32로 0x7FC00000입니다. .NET `double.NaN`은 `(double)0.0 / (double)0.0` 상수(v10.0.0 Double.cs)라 x64 기본 NaN 0xFFF8…로 접혔을 가능성이 큽니다(미검증) | 파일로 나가는 NaN은 `BitConverter.Int64BitsToDouble(0x7FF8000000000000)` | exact |
| crust.py:238·246-251 | dependency/src-tie | reach에 core spacing이 들어갑니다. 상한 근처 유한/inf는 coast_signed_m에만 남고, 이 값은 manifest에 쓰이지 않습니다. src 동률은 plates와 같습니다 | core 비트 일치 전제 | tolerance |
| crust.py:254-276 | threshold/coverage | 문턱에서 값은 연속입니다. 그러나 n=16·24에서는 near가 비고, n=16–64에서는 띠가 비어 이 분기가 tiny golden에서 돌지 않습니다 | 작은 반지름 golden으로 덮음 | tolerance |
| crust.py:283 | near-zero | 0 근처 z는 상대 오차만 보면 3e-12가 나옵니다 | rtol 1e-13 + atol 1e-8 m | tolerance |

#### (c) 외부 호출과 대체 방식

| 외부 호출 | 위치 | C# 대체 |
|---|---|---|
| `math.log/cos/sin`, `np.exp`(스칼라·float64 배열) | plates.py:81-87, crust.py:77·142·181·256 | `Math.Log/Cos/Sin/Exp` (C 런타임 libm) |
| `math.sqrt`, `np.sqrt` | plates.py:81-82·203, crust.py:141 | `Math.Sqrt` (정확) |
| `np.linalg.norm` 1-D | plates.py:90·517·522·579 | `LinAlg.Norm3` = `Math.Sqrt((x*x + y*y) + z*z)` |
| `np.linalg.norm(axis=1, keepdims)` | plates.py:152·509 | 행마다 같은 식. core `CellGraph.Unit()`도 같은 식(`pos / R` 금지) |
| `np.cross` | plates.py:516·521 | `LinAlg.Cross3` |
| `np.bincount(weights)`, `np.add.at`, `np.maximum.at` | plates.py:391·486-490·508·554-555·622-623 | 원소 순서대로 도는 루프 |
| `np.flatnonzero`, `np.unique`, `np.argmax/argmin` | plates.py:336·457·481·565, 564·571, 574, 520 | bool 표시를 오름차순으로 훑기, 엄격한 비교 |
| `np.lexsort`, `np.cumsum`, `np.searchsorted` | crust.py:118-121 | `NpSort.LexSort`(NaN 끝), 순차 누적, 직접 짠 하한 탐색 |
| `np.clip`, `np.maximum/minimum`, `np.where`, `np.nan_to_num` | 여러 곳 | `Math.Clamp`, `Math.Max/Min`(v10.0.0 구현을 확인, arm64 numpy와 ±0·NaN 동작이 같음), 조건식 |
| `core.noise.vector_fbm3`, `fbm3` | plates.py:146, crust.py:88 | `Bpcg.Core.Noise` (floor와 산술만 쓰므로 비트 일치 전제) |
| `core.distance.nearest_source` | plates.py:371, crust.py:246-247 | `Bpcg.Core.Distance.NearestSource`. src는 힙 순서·동률 규칙(키, 칸 번호)까지 같아야 하고, 거리는 atan2 |
| `core.hashing.hash3/hash_unit` | plates.py:72·80·118, crust.py:99 | `Bpcg.Core.Hashing` |
| core 그래프 pos·area·dist (`np.tan`·`np.arctan2`) | 모든 함수의 입력 | `Bpcg.Core.Graph`. planet 범주의 전제이므로 비트 대조를 먼저 통과해야 합니다 |

scipy 같은 외부 라이브러리는 범위 안에서 쓰지 않습니다.

#### (d) numba 커널과 prange

| 커널 | 병렬 | 반복 단위 | 경쟁·축약 | C# |
|---|---|---|---|---|
| `_argmax_dot_kernel` (plates.py:126) | parallel, prange | 칸 | 칸별 쓰기. best·arg는 지역 변수 | `Parallel.For` |
| `_classify_kernel` (plates.py:159) | parallel, prange | 칸 | 칸별 쓰기 5개(모든 분기에서 5개 모두 씀). sg·st·cnt·hg는 반복 안 지역 변수 | `Parallel.For` |
| `_smooth_on_cells_kernel` (plates.py:299) | 직렬 | 회차 × 경계 칸 | 야코비(cur→nxt) | 직렬 루프 |
| `_other_side_kernel` (plates.py:412) | parallel, prange | 수렴 경계 칸 | 칸별 쓰기 2개. 누적은 지역 변수 | `Parallel.For` |
| `_soft_plate_bias_kernel` (crust.py:61) | parallel, prange | 칸 | 칸별 쓰기. num·den은 지역 변수, 내적은 두 번 계산 | `Parallel.For` |

prange 반복 밖에서 정의된 축약 변수가 없으므로, 스레드 수와 상관없이 결과가 같습니다(numba 1·12 스레드 비트 일치). 커널의 int64 카운터로 나누는 곳(`sg/cnt`, `s/cnt`, `n_cont/n_other`, `s_age/n_age`)은 double로 바꾼 뒤 나눕니다. 작은 정수라 C#의 `(double)` 변환과 결과가 같습니다. numba는 `contract` 플래그를 붙이지 않으므로 FMA로 묶이지 않습니다(순수 Python 재현과 비트 일치).

#### (e) 의심 버그

- **삼중 접합 오염 (plates.py:430-441, 486-490).** `_other_side_kernel`은 다른 판 이웃 전부로 대륙 비율과 나이를 냅니다. 그런데 `_subduction_polarity`는 그 값을 판 쌍 (other[c], plate[c])의 몫으로 씁니다.
  - 근거: 이웃을 `plate[v] == other[c]`로 제한한 변형과 비교했습니다. tiny는 수렴 226칸 중 kind 1칸·side 5칸, seed 5 laptop은 2066칸 중 kind 1칸·side 5칸이 달라졌습니다(재현 확인).
  - 영향: 접합부 몇 칸의 섭입 방향이 바뀝니다. 그대로 옮깁니다.
- **nearest_source 동률 취약성 (core/distance.py의 동률 규칙, 쓰는 곳 plates.py:642-662, crust.py:246-251·272-273).** 대칭 격자의 정확한 동률을 칸 번호로 풀기 때문에, pos가 1 ulp만 달라도 src가 바뀌고 kind·side·γ·half_gap·z가 다른 칸 값을 가져옵니다.
  - 근거: 핵심 결론의 섭동 실험(tiny 20회 중 13회 범주 변화, z 최대 150 m 차이)입니다.
  - 영향: conventions.md 6절의 '맥과 연구실 PC가 같은 행성'은 Python 자체에서도 np.tan 결과가 같을 때만 성립합니다. C#은 core pos 비트 일치로 막고, 고치지 않고 기록합니다.
- **문서와 코드의 차이 (docs/pipeline.md 4.1·4.2).** 포팅은 코드를 따릅니다.
  - 시그니처 `generate_plates(graph, cfg)`는 실제로 `(graph, continental, cfg)`입니다(4.1의 5항은 대륙 마스크를 넘긴다고 적어 문서 안에서도 어긋남).
  - 씨앗은 균등수 두 개가 아니라 네 개로 정규분포 세 개를 만듭니다.
  - 반확장 속도는 경계를 따라 평활한 −0.5γ에 판별 하한을 적용한 값입니다.
  - 대륙 마스크의 판별 치우침은 계단(해시 ±0.3)이 아니라 판 씨앗 소프트맥스(β = 12)로 섞은 값입니다.
  - 해구는 지각 종류를 보지 않습니다.
  - Airy의 z<0 쪽은 명세의 α 교체가 아니라 연속인 `z_air·ρm/(ρm−ρw)`입니다.
- **대륙 자르기의 같음 처리 (crust.py:125·127).** k == 0일 때만 `<`이고 나머지는 `<=`입니다. 정확히 같을 때만 갈리므로 영향은 사실상 없습니다.
- **`max_rounds < 0` (plates.py:561-585).** 루프가 돌지 않아 UnboundLocalError가 납니다. 파이프라인은 늘 3을 쓰므로 영향은 없습니다.

#### (f) golden 대조 계획

모든 함수 대조는 Python 그래프(pos·nbr·dist·area·spacing·R)를 입력으로 함께 씁니다. tiny 거친 격자(n=16)는 압축해서 약 26 KB입니다. C#이 만든 그래프를 쓰면 nearest_source 동률 때문에 범주가 갈릴 수 있습니다.

- 호출 지점 값은 export 스크립트가 해당 함수를 감싸 인자와 결과를 기록하는 방식으로 잡습니다(규칙 해석은 열린 질문).
- tiny에는 뒤집기도, 해령 대체 거리도, 대륙 가장자리 얇아짐·전이 띠도 없습니다. 그래서 같은 크기(1536칸 이하)의 사례를 더합니다.
- 'exact'는 실수의 부호 있는 0까지 비트로 비교하고, NaN은 NaN끼리 같게 봅니다.
- 'tolerance'는 macOS-arm64에서 비트 일치를 따로 확인합니다. 그 밖에서는 `abs(Δ) ≤ 1e-13·abs(ref) + atol`로 봅니다. atol은 z·두께·해구 1e-8 m, 나이 1e-12 Myr입니다. 근거는 1 ulp 섭동의 최대 상대 4.7e-15, 0 근처 z의 절대 3.2e-12 m입니다. 비정규수 해구 값도 atol로 덮습니다.

| 대상 | 입력 | 출력 | 비교 |
|---|---|---|---|
| `SubSeed` | 수제: 시드 {0, 1, 27, 72, 320, −1, int64 양끝} × 용도 {301–304, 311, 312, 321, 331} | int64 표 | exact |
| `HashedUnitVectors`, `PlateSeeds`, `PlateOmegas` | tiny·seed 27·seed 72 설정, 스트레스용 count 1024 | (count,3) | tolerance(성분 4·2^-52, ω rtol 1e-13) |
| `ArgmaxDotKernel`, `AssignPlates` | tiny의 unit·seeds·q(기록), 수제: 같은 씨앗 두 개·NaN 행 | plate_id int32 | exact |
| `ClassifyKernel`, `ClassifyBoundaries` | tiny 최종 ω, seed 72의 회차별 ω, 수제 두 판(±x 회전)·비틀림·κ = 0 | boundary_type, gamma, tau, other, half_gap | exact |
| `SmoothOnCellsKernel`, `SmoothAlongBoundary`, `SamePlateGraph` | tiny의 gamma·마스크·plate·nbr, 빈 마스크 | 평활 값, nbr_same | exact |
| `EnsureDivergentBoundaries`, `PlateCentroids`, `AwayAxis` | tiny, seed 72(negate → away), seed 320(같은 회차 미룸), seed 51·판 16개(3회차, remaining [7, 14]), 비틀림(max_rounds 1·3), 수제 AwayAxis(평행·반평행·argmin 같음) | ω, flips(JSON), cls, remaining, 무게중심 | exact |
| `SeafloorAgeMyr`, `AgeConsistentRateFloor` | tiny 기록 + 수제: d ∈ {−0.0, 0, 1e5, inf, NaN, 1e300}, v ∈ {0.05, 0, −0.0, −1, NaN, +inf, 1e-300} | 나이(−0.0 유지 확인), 판별 하한 | exact |
| `OtherSideKernel`, `SubductionPolarity` | tiny·seed 72의 nbr·plate·other·is_conv·대륙·임시 나이(cont_frac == 0.5 칸 포함) | cont_frac, other_age, kind, side | exact |
| `GeneratePlates` | tiny, seed 72, seed 51·판 16개(대체 해령 거리 35칸, 수렴 경계 없는 판 7의 inf 2칸), n=4·판 24개·seed 0(빈 판 1 → ocean_fraction null, 비율이 정확히 0.5인 판, 5회 뒤집기, remaining [17]) | 필드 8개, info 전체 | 범주 exact, 실수 tolerance |
| `Smoothstep` | 수제 [−inf, −1, −0.0, 0, 0.25, 0.5, 1, 2, inf, NaN] | 값(부호 비트 포함) | exact |
| `SoftPlateBiasKernel`, `ContinentScore` | tiny의 unit·seeds·bias·12.0 | (N,) | tolerance(1e-15) |
| `ContinentMask` | tiny에서 frac {0, 2e-4, 5e-4, 0.25, 0.4, 1.0}(k==0 분기의 n_take 0·1 포함), seed 72 | bool (N,) | exact |
| `OceanDepthM`, `AiryElevationM`, `TrenchOffsetM` | 수제(70 Myr 앞뒤, 음수·NaN·inf 나이, h0 주변 두께, tests의 해구 벡터, d/w ∈ {26.7, 27.0, 27.3}: 정규수·비정규수·−0.0) + tiny 기록 | 깊이, 고도, 해구 | t<70·Airy·0값 exact, exp 값 tolerance |
| `EdgeCells`, `GenerateCrust` | tiny + 모두 해양·모두 대륙 + **작은 반지름 그래프** `sphere_graph(16, 1_000_000.0)`(tiny 설정: near 401칸, 띠 246·266칸, reach 밖 ±inf 24·32칸, 해구 447칸, 압축 약 100 KB) | edge·half, 필드 4개, info 3개 | crust_type·NaN·±inf 위치 exact, 실수 tolerance |
| core 그래프 전제 | C# `SphereGraph(16, R)`, `SphereGraph(16, 1_000_000.0)` | pos·Unit()·area·dist·spacing | exact (planet 끝에서 끝 전에) |
| margin guard | Python 여유 7종(tiny, laptop 거친 격자; 경계 여유는 모든 회차, 70 Myr 계단 포함) | 여유 값 | rtol 1e-6, C# 값은 상대 1e-10 이상 |

예상 크기는 tiny 입출력 약 150 KB, seed 72·320·51 각각 60 KB 안팎, 작은 반지름 사례 약 100 KB, n=4 사례와 수제 사례 각각 10 KB 이하로, 합쳐도 1 MB 제한 안입니다. C# 쪽에는 Python과 대조하지 않는 시험도 둡니다. 병렬 커널을 `MaxDegreeOfParallelism = 1`과 기본값으로 돌려 결과를 비트 단위로 비교합니다.

#### (g) 포팅 순서와 위험 메모

1. core(hashing, noise, graph의 pos·`Unit()`, distance)가 golden 대조를 통과한 뒤 시작합니다. planet-a의 정확 일치는 core의 비트 일치에 기댑니다. 특히 그래프 pos와 `NearestSource`의 힙 동률 규칙이 맞아야 합니다. 1 ulp만 달라도 kind·side가 갈리는 것을 실험으로 확인했습니다.
2. Plates는 다음 순서로 옮깁니다.
   1. 상수
   2. `SubSeed`·`HashedUnitVectors`·`PlateSeeds`·`PlateOmegas`
   3. `ArgmaxDotKernel`·`AssignPlates`
   4. `ClassifyKernel`·`ClassifyBoundaries`
   5. 평활·`SamePlateGraph`
   6. `SeafloorAgeMyr`·판별 하한
   7. 섭입 방향
   8. 무게중심·`AwayAxis`·`EnsureDivergentBoundaries`
   9. `GeneratePlates`·`PlatesInfo`
3. Crust는 다음 순서로 옮깁니다.
   1. `Smoothstep`
   2. `SoftPlateBiasKernel`·`ContinentScore`·`ContinentMask`
   3. `OceanDepthM`·`AiryElevationM`·`TrenchOffsetM`
   4. `EdgeCells`
   5. `GenerateCrust`

   build_materials 안에서는 `ContinentMask`가 `GeneratePlates`보다 먼저 불립니다.
4. 가장 큰 위험은 OS 차이입니다.
   - 초월 함수 값으로 정하는 범주는 여유가 커서 안전합니다.
   - 그래프 pos가 다르면 nearest_source 동률 때문에 몇 칸의 범주와 값(z 최대 150 m)이 갈립니다. 다른 OS의 끝에서 끝 대조에서 갈리면 허용 오차를 넓히지 않고 갈린 칸을 기록합니다.
   - manifest의 seeds·omegas 텍스트는 macOS 밖에서 마지막 자리가 다를 수 있으므로, manifest 대조는 수치로 합니다.
5. 순서에 민감한 곳은 세 군데입니다.
   - `np.add.at`의 두 단계 누적
   - 뒤집기 회차 루프
   - info와 flips 기록의 키 순서

   셋 다 seed 72·320·51과 비틀림 사례로 덮습니다.
6. tiny 거친 격자는 대륙 가장자리 얇아짐과 ±50 km 전이를 한 칸도 쓰지 않습니다. 이 분기는 작은 반지름 golden과 laptop 끝에서 끝 대조에서만 검증됩니다.
7. 비정규수 해구 값이 생기므로 C# 계산 스레드에서 FTZ·DAZ를 켜지 않습니다. 단계 4에서 Godot 작업 스레드의 부동소수 상태도 확인합니다.
8. 삼중 접합 오염과 nearest_source 동률은 고치지 않고 그대로 옮깁니다. Python이 나중에 고쳐지면 golden 자료를 다시 만듭니다.

### planet ② — climate·materials·ocean·uplift

대상은 `src/bpcg/planet/`의 네 파일로, `climate.py`(130줄), `materials.py`(261줄), `ocean.py`(133줄), `uplift.py`(145줄)입니다. 계약은 docs/pipeline.md 1절·4.3·4.4·4.5와 `tests/test_planet.py`를 따릅니다. `planet/__init__.py`는 docstring뿐이라 C# 파일이 필요 없습니다.

네 모듈의 실수 계산은 모두 float64이고 float32 배열이 섞이지 않습니다. 그래서 NEP 50 승격이 일어나는 곳이 없습니다(tiny 출력의 실수 필드가 모두 float64임을 확인했습니다). float32 변환은 bake의 묶음 쓰기에서만 합니다.

아래 '검증'은 이 기계(macOS arm64, numpy 2.5.3, numba 0.68.0)에서 실험으로 확인한 내용입니다. tiny 중간값은 `generate_planet`과 같은 호출(거친 면당 16 = 1536칸, L0 면당 32 = 6144칸, jitter 1.0, seed 0)에서 잡았습니다. 다시 돌린 L0 해수면 −768.2224649760404와 바다 비율 0.667655119177121은 `out/tiny_check/planet/manifest.json`과 비트까지 같습니다.

#### 대응표

네 모듈은 `namespace Bpcg.Planet`의 `public static class`가 되고, 파일은 `csharp/Bpcg/Planet/`의 `Climate.cs`, `Materials.cs`, `Ocean.cs`, `Uplift.cs`입니다. 네 모듈에는 Python 클래스가 없어서 정적 클래스와 이름이 겹치는 형이 없습니다.

필드 dict는 삽입 순서를 지키는 `OrderedDictionary<string, Array>`(.NET 9 이상)로 둡니다. 범주 값은 FIELDS dtype을 따릅니다: `plate_id`는 int[], `boundary_type`·`convergence_kind`·`crust_type`은 byte[], `subduction_side`는 sbyte[], `is_ocean`은 bool[]입니다. 실수 값은 계산하는 동안 double[]입니다. `CellGraph`와 `Config`는 core 포팅의 형입니다.

| Python | C# 멤버 | 비고 |
|---|---|---|
| `climate.STREAM_PRECIP_NOISE` = 331 | `const long StreamPrecipNoise` | 강수 노이즈 시드 갈래 |
| `climate.PRECIP_NOISE_FREQUENCY` = 3.0, `PRECIP_NOISE_OCTAVES` = 5 | `const double PrecipNoiseFrequency`, `const int PrecipNoiseOctaves` | |
| `climate.latitude_rad` | `double[] LatitudeRad(CellGraph graph, Config cfg, double? latDeg = null)` | 구면은 `planet.axis`, 평면은 latDeg 필수 |
| `climate.surface_temperature` | `double[] SurfaceTemperature(double[] latitude, double[]? z, Config cfg)` | pipeline이 최종 고도로 다시 부름 |
| `climate.budyko_runoff` | `double[] BudykoRunoff(double[] precip, double[] pet)`, `double BudykoRunoff(double p, double e)` | 스칼라판은 hero/flat의 brentq용, 같은 식과 같은 tanh |
| `climate.generate_climate` | `OrderedDictionary<string, Array> GenerateClimate(CellGraph graph, Config cfg, double[]? z = null, long? seed = null, double? latDeg = null)` | 키 5개의 순서를 지킴 |
| `materials.CATEGORICAL_FIELDS`, `LINEAR_FIELDS`, `RECOMPUTED_FIELDS` | `static readonly string[] CategoricalFields`, `LinearFields`, `RecomputedFields` | RecomputedFields는 문서용(코드에서 안 씀) |
| `materials.SIGNED_DIST_RANGE_CELLS` = 2.0 | `const double SignedDistRangeCells` | |
| `materials._sea_level_and_mask` (비공개) | `private static (double H, bool[] IsOcean) SeaLevelAndMask(CellGraph graph, double[] zPlatform, Config cfg)` | |
| `materials._area_fraction` (비공개) | `private static double AreaFraction(CellGraph graph, bool[] mask)` | pairwise 합 두 번 |
| `materials.build_materials` | `(OrderedDictionary<string, Array> Fields, MaterialsInfo Info) BuildMaterials(CellGraph graph, Config cfg)` | 필드 21개 |
| `materials._finite_resample` (비공개) | `private static double[] FiniteResample(double[] values, int nSrc, CellGraph fine, double big, long[] nearestIdx)` | |
| `materials.transfer_materials` | `(OrderedDictionary<string, Array> Fields, TransferInfo Info) TransferMaterials(CellGraph coarseGraph, IReadOnlyDictionary<string, Array> coarseFields, MaterialsInfo? coarseInfo, CellGraph fineGraph, Config cfg)` | coarseInfo에서 `ZPlatformBaseM`만 읽음 |
| build의 info dict | `sealed class MaterialsInfo`: `SeaLevelM`, `OceanFraction`, `ContinentalFraction`, `BathymetryM`, `ZPlatformBaseM`, `TrenchM`, `CoastSignedM`, `Plates`, `Seconds` | 직렬화 순서는 Python 키 순서 |
| transfer의 info dict | `sealed class TransferInfo`: `SeaLevelM`, `OceanFraction`, `ContinentalFraction`, `BathymetryM`, `ZPlatformBaseM`, `TrenchM`, `Seconds` | manifest `meta.info`의 앞 7개 키 |
| `ocean.SEA_LEVEL_REL_TOL` = 1e-9, `_MAX_BISECTION` = 200 | `const double SeaLevelRelTol`, `private const int MaxBisection` | |
| `ocean._volume_below` (@njit, 비공개) | `internal static double VolumeBelow(ReadOnlySpan<double> z, ReadOnlySpan<double> area, double h)` | golden용으로 internal(InternalsVisibleTo) |
| `ocean._check_z_area` (비공개) | `private static void CheckZArea(double[] z, double[] area)` | |
| `ocean.ocean_volume` | `double OceanVolume(double[] zPlatform, double[] area, double h)` | |
| `ocean.sea_level` | `double SeaLevel(double[] zPlatform, double[] area, double waterVolume)` | |
| `ocean._label_components` (@njit, 비공개) | `internal static (int[] Labels, int NLab) LabelComponents(bool[] mask, int[] nbr, int width)` | nbr는 (N, width) 행 우선, Python labels는 int64 |
| `ocean.ocean_mask` | `bool[] OceanMask(CellGraph graph, double[] zPlatform, double h)` | |
| `uplift.STREAM_OROGEN_NOISE` = 321, `OROGEN_NOISE_OCTAVES` = 4 | `const long StreamOrogenNoise`, `const int OrogenNoiseOctaves` | |
| `uplift`의 실수 상수 6개 | `CollisionWidthFactor` 1.5, `MinMountainUpliftMPerYr` 1e-9, `RiftMaxDistM` 50 000, `RiftWidthM` 30 000, `OrogenNoiseAmplitude` 0.25, `OrogenNoiseWavelengthFactor` 2.0 | 모두 `const double` |
| `uplift._REQUIRED` (비공개) | `private static readonly string[] Required` | 필드 7개 |
| `uplift.arc_distance_m` | `double ArcDistanceM(Config cfg)` | 기본값 199628.0101733919 m |
| `uplift.noise_points` | `double[] NoisePoints(CellGraph graph, double radiusM)` | (N, 3) 행 우선, 길이 3N |
| `uplift.generate_uplift` | `OrderedDictionary<string, Array> GenerateUplift(CellGraph graph, IReadOnlyDictionary<string, Array> fields, Config cfg)` | 키 3개 |

#### 수치 함정 표

공통 규칙(pairwise 합, NEP 50, 안정 정렬, libm 차이, .NET 형 변환)은 공용 장에 있습니다. 여기에는 이 네 모듈에서 실제로 걸리는 자리만 적습니다.

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| ocean.py:63 | pairwise 합이 분기를 정함 | `hi = z.max() + w / a.sum()`의 `a.sum()`이 이분법 중점 수열 전체를 정합니다. 검증: 순차 합이면 h가 거친 −924.0968734333851 → −924.0968734333842, L0 −768.2224649760404 → −768.2224649760518로 바뀝니다. a.sum()이 1 ulp만 달라도 L0 h가 4.5e-13 m 달라집니다 | `NpReduce.Sum(area)`. LINQ Sum·TensorPrimitives·병렬 합 금지 | exact |
| ocean.py:19-27 | numba 직렬 합 | `_volume_below`는 셀 번호 오름차순 순차 누적입니다. 검증: 다시 컴파일한 arm64 어셈블리에 fmadd·벡터 명령이 없고 IR에 contract·fast 플래그가 없습니다. 무작위 300건이 파이썬 순차 루프와 비트까지 같습니다 | 단순 for, `d = h - z[c]; if (d > 0.0) total += area[c] * d` | exact |
| ocean.py:62-76 | 이분법 분기 | tol 판정, `v < w`, `hi - lo <= 4·spacing(abs(h) + 1)`이 모두 분기입니다. 검증: np.spacing은 nextafter − x와 같습니다(9개 값). tiny 거친은 30회, L0는 31회에 tol로 끝납니다. 분기 여유 최소가 1.8e-10·W라 z.min·z.max가 아닌 칸의 ulp 차이는 h를 바꾸지 않습니다 | `PyMath.Spacing(x) = Math.BitIncrement(x) - x`, 식 순서 그대로 | exact |
| ocean.py:78-82 | 선형 맞춤 순서 | break 뒤에 `lo + ((w − v_lo)·(hi − lo)) / (v_hi − v_lo)`로 맞춥니다. tiny는 이 경로를 지나지 않습니다. 검증: z = [1e6, 1e6+1, 1e6+3], A = [1e12, 1e12, 3e12], W = 1e9에서 33회째 break, h = 1000000.001 | 같은 순서, 손 입력 golden | exact |
| ocean.py:85-112 | 성분 번호 순서 | 작은 셀 번호부터 번호를 붙이는 DFS(LIFO, 슬롯 0..7)입니다. `v >= 0 and mask[v]`는 단락 평가라 −1이 인덱스로 쓰이지 않습니다. 검증: nbr가 대칭입니다(비대칭 0, 중복 0, 빈 슬롯 24). tiny 거친은 성분 2개(913·12칸), L0는 1개입니다 | 같은 DFS, `&&` 순서 유지, labels는 int[] | exact |
| ocean.py:131-132 | bincount와 argmax | 성분 면적은 셀 번호 순 순차 누적이고 pairwise가 아닙니다. 면적이 같으면 첫 번호를 고릅니다. 검증: bincount가 순차 루프와 비트까지 같고, 면적이 같은 두 분지에서 셀 번호가 작은 쪽이 바다가 됩니다 | 순차 누적 루프, `>` 엄격 비교 | exact |
| ocean.py:126 | 엄격 비교 | `z < h`라서 z == h인 칸은 바다가 아닙니다 | `z[c] < h` | exact |
| climate.py:32-36 | BLAS 내적 순서 | norm(axis)와 `unit @ (axis/norm)`(Accelerate dgemv)입니다. 검증: 무작위 축 30개 × L0 6144칸(184320개)이 왼쪽부터 더한 식과 같고, 3-벡터 norm 20000개가 naive sqrt와 같습니다 | `axis[k]/norm`을 먼저, `u0*a0 + u1*a1 + u2*a2`, `Math.Sqrt((a0*a0 + a1*a1) + a2*a2)` | exact |
| climate.py:36, 53, 96-109; uplift.py:63, 115-136 | libm 초월 함수 | asin·sin·exp·tan입니다. 검증: np.arcsin·np.sin·np.exp·np.tan이 libm과 20만~60만 개에서 비트까지 같습니다. macOS에서는 .NET도 같은 libm이라 비트 일치가 기대되고, 다른 OS에서는 1 ulp 다를 수 있습니다 | Math.Asin·Sin·Exp·Tan, `** 2`는 `x * x`(검증: square와 비트 일치) | tolerance |
| climate.py:66-67 | numpy SIMD tanh | np.tanh(float64)는 SVML 유래 16구간 표와 16차 FMA Horner로 계산하며, libm과 입력의 13~22%에서 1 ulp 다릅니다. Math.Tanh를 쓰면 tiny L0의 runoff 324칸, runoff_eff 228칸이 달라집니다. 검증: math.fma로 흉내 낸 구현이 200013/200013 비트를 재현하고, FMA 없는 Horner는 20000개 중 347개가 다릅니다. 길이 1·0차원·strided 배열도 같은 결과입니다 | `NpUfunc.Tanh`(lut16x18 표 + Math.FusedMultiplyAdd), 규칙 예외 필요 | exact |
| climate.py:66-68 | Budyko 식 순서 | `p * sqrt((phi * tanh(1.0/phi)) * (1.0 − exp(−phi)))` 순서이고, tanh 인자는 p/e가 아니라 1.0/phi입니다. 검증: tanh·exp를 ±1 ulp 섭동하면 abs(ΔR) ≤ 1.91·eps·P입니다 | 같은 순서. 다른 OS의 허용 오차는 abs(ΔR) ≤ 4·eps·P | exact |
| climate.py:42, 93-95; uplift.py:57-58 | 도→라디안 공식 | math.radians는 x·(π/180)입니다. .NET `double.DegreesToRadians`는 (x·π)/180이라 12.0에서 비트가 다르고, σ 설정이 12°입니다 | `PyMath.Radians(x) = x * (Math.PI / 180.0)` | exact |
| climate.py:36, 68; uplift.py:139; materials.py:196 | clip 의미 | 스칼라 경계 경로는 NaN을 통과시키고 −0을 그대로 둡니다. 배열 경계 경로(Budyko 상한 p)는 −0을 +0으로 바꿉니다. 이 자리들에는 −0·NaN 입력이 없고, tiny에서 Budyko·age clip이 걸린 칸도 없습니다 | `NpMath.Clip(x, lo, hi)`. Math.Clamp는 경계 예외 때문에 쓰지 않음 | exact |
| climate.py:55, 116, 120-123; uplift.py:93, 140; materials.py:116, 243 | np.maximum | arm64의 np.maximum은 NaN 전파·−0 < +0이라 Math.Max와 의미가 같습니다. 검증: 길이 1·3·17·64 모두 +0이고, tiny 입력·출력에 −0이 없습니다 | `Math.Max(x, 0.0)`, runoff_eff는 `Math.Max(Math.Max(R, f * P), floor)` | exact |
| climate.py:109 | 노이즈 배율 | `band * exp(eta*xi − (0.5*eta)*eta)`이고, eta = 0이면 band를 복사합니다. xi는 core fbm3(시드 `SubSeed(seed, 331)`)입니다 | 같은 순서 | tolerance |
| climate.py:112, 117 | 검증의 NaN | `(x > 0).all()`은 NaN도 실패로 봅니다 | `!(x > 0)`이면 예외 | n/a |
| uplift.py:107 | 단위 변환 순서 | U₀ = craton·1e-6입니다. 검증: 5.0·1e-6 = 4.9999999999999996e-06 ≠ 5.0/1e6 | `craton * 1e-6` | exact |
| uplift.py:112-119 | 가우스 식 | (계수·γ)·exp(−t²), t = (δ − d_arc)/w이고, δ = inf이면 0입니다. 충돌 항은 0인 칸에 더합니다. tiny L0에서 위판 2031칸, 충돌 2086칸입니다 | `(of * g) * Math.Exp(-(t * t))` | tolerance |
| uplift.py:122 | 꼬리 자르기 분기 | mountain < 1e-9이면 0으로 둡니다. exp가 1 ulp 달라 경계를 넘으면 U가 U₀의 2e-4만큼 달라집니다. 검증: tiny에서 경계까지 상대 여유의 최솟값은 7.9e-3입니다 | 같은 비교, 불일치는 기록 | exact |
| uplift.py:123-132 | 노이즈 부분집합 | active 점만 fbm3에 넘기고 `m * (1.0 + 0.25*xi)`를 곱합니다. frequency = R/(2.0·w)입니다. 검증: 부분집합 결과가 전체 결과[active]와 같습니다 | 셀 번호 순으로 모아 Fbm3 | exact |
| uplift.py:135-136 | tiny에서 안 지나가는 분기 | 대륙 열곡(d_div < 50 km)이 tiny에서 0칸입니다 | `rs * Math.Exp(-(q * q))`, q = d/30000, 손 입력 golden | tolerance |
| uplift.py:139-143 | 곱셈 순서 | `(max(U,0)·dur)·1e6`을 왼쪽부터 곱합니다. 검증: U·(dur·1e6)으로 바꾸면 L0 48칸이 다르고, L0 1칸이 상한에 걸립니다 | 같은 순서와 `NpMath.Clip` | exact |
| uplift.py:144; materials.py:198-199 | NaN 비트 | np.nan은 0x7FF8000000000000이고, 묶음 float32로는 0x7FC00000입니다. .NET double.NaN은 `0.0 / 0.0` 상수라 부호 비트가 켜진 값일 수 있습니다(단계 1에서 확인) | `PyMath.NaN`(0x7FF8000000000000), 대조는 NaN 위치만 | exact |
| materials.py:76-77 | pairwise 합(보고용) | 마스크로 압축한 area와 전체 area를 각각 pairwise로 더합니다. 검증: L0 ocean_fraction이 pairwise로 0.667655119177121, 순차 합으로 0.6676551191771177입니다 | 압축 배열을 만든 뒤 `NpReduce.Sum` | exact |
| materials.py:111-131, 247-260 | dict 순서와 시간 | info 키 순서가 manifest meta.info 순서가 됩니다(sort_keys 없음). seconds는 perf_counter로 잽니다 | 명시한 순서로 직렬화, Stopwatch 사용, seconds는 대조에서 뺌 | exact |
| materials.py:174, 211 | core 값이 분기를 정함 | near = abs_f < 2·h_coarse의 h_coarse는 core의 sqrt(area.mean())입니다. 검증: tiny에서 문턱까지 상대 여유의 최솟값은 4.9e-4입니다 | core의 Spacing이 pairwise 평균을 써야 함 | exact |
| materials.py:177-180 | 가장 가까운 칸 | cell_of(atan·floor·clip, core)로 찾은 번호로 범주 5개를 dtype 그대로 gather합니다 | long[] idx를 한 번 만들어 gather | exact |
| materials.py:139-143, 207-213 | inf 자리 값 | inf를 πR(20015086.79602057)로 바꿔 보간하고, 가장 가까운 칸이 inf이면 inf로 되돌립니다. tiny에는 inf가 없습니다 | `Math.PI * R`, 손 입력 golden | exact |
| materials.py:190-200 | NaN 채우기 | nan_to_num(NaN → 0, ±inf → ±최대값) → 가장 가까운 해양 값 → 보간 → clip → 대륙 칸 NaN 순서입니다 | `NpMath.NanToNum`, core NearestSourceValues | exact |
| materials.py:214-219 | 보간 값의 부호 | s_f의 부호로 subduction_side를 다시 정합니다(int64 → int8). 검증: fix 1317칸 중 값이 바뀐 칸 43개, abs(s_f) 최솟값 46.7 m라 ulp 차이로 뒤집히지 않습니다 | `(sbyte)(s < 0.0 ? -1 : 1)` | exact |
| materials.py:181-229 | core 보간 | resample_sphere(linear)를 8번 부르고, 가중치가 atan을 거칩니다. 검증: numba math.atan·exp가 libm과 비트까지 같습니다 | core ResampleSphere, unit()은 캐시해도 됨 | tolerance |

#### 외부 호출과 대체 방식

| Python 호출 | 쓰는 곳 | C# 대체 |
|---|---|---|
| `ndarray.sum()`(float64 연속 배열, pairwise) | ocean.py:63, materials.py:77 | `Bpcg.Numerics.NpReduce.Sum` |
| `ndarray.min()`, `max()` | ocean.py:62-63 | 비교 루프(NaN은 먼저 거름) |
| `np.bincount(x, weights, minlength)` | ocean.py:131 | 순차 누적 루프 |
| `np.argmax` | ocean.py:132 | 첫 최댓값(`>`) 루프 |
| `np.spacing` | ocean.py:75 | `PyMath.Spacing` = `Math.BitIncrement(x) - x` |
| `np.linalg.norm`(3-벡터), `@`(N×3 · 3) | climate.py:33, 36 | 왼쪽부터 곱해 더하는 스칼라 식 |
| `np.arcsin`, `np.sin`, `np.exp`, `math.tan` | climate.py, uplift.py | `Math.Asin`, `Math.Sin`, `Math.Exp`, `Math.Tan` |
| `np.tanh` | climate.py:67 | `Bpcg.Numerics.NpUfunc.Tanh`(numpy simd_tanh_f64 복제) |
| `math.radians` | climate.py:42, 93-95; uplift.py:57-58 | `PyMath.Radians` |
| `np.clip` | climate.py:36, 68; uplift.py:139; materials.py:196 | `NpMath.Clip` |
| `np.maximum`, `np.abs`, `np.sqrt`, `np.isfinite` | 여러 곳 | `Math.Max`, `Math.Abs`, `Math.Sqrt`, `double.IsFinite` |
| `np.nan_to_num`, `np.nan` | materials.py:193, 198-199; uplift.py:144 | `NpMath.NanToNum`, `PyMath.NaN` |
| `time.perf_counter` | materials.py | `Stopwatch.GetTimestamp` |
| 저장소 함수 `fbm3`, `sub_seed`, `CellGraph.unit`, `cell_of`, `resample_sphere`, `nearest_source_values`, `check_fields`, `continent_mask`, `generate_plates`, `generate_crust`, `trench_offset_m`, `check_sphere_graph`, `KIND_*` | climate·uplift·materials | core·planet ① 포팅 결과를 부름 |

scipy·scikit-image·trimesh 호출은 없습니다.

#### numba 커널과 prange

- `ocean._volume_below`는 `@njit(cache=True)` 직렬 커널이고 prange가 없습니다. 축약 변수 `total`은 칸 순서대로 더하며, 어셈블리로 FMA·벡터화가 없음을 확인했습니다. C#도 직렬 for로 두고 Parallel.For를 쓰지 않습니다.
- `ocean._label_components`는 `@njit(cache=True)` 직렬 DFS이고 경쟁이 없습니다. C#도 직렬로 둡니다. 칸은 스택에 한 번씩만 쌓이므로 길이 N 스택은 넘치지 않습니다.
- 네 모듈에는 prange가 없습니다. 부르는 쪽 커널인 `core.noise._fbm_kernel`과 `core.resample._bilinear_kernel`은 prange를 쓰지만 점마다 쓰기만 하므로 스레드 수와 상관없습니다. 부분집합으로 불러도 같은 값이 나옴을 확인했습니다.
- uplift·climate의 칸별 계산은 칸마다 쓰기만 하므로 Parallel.For로 나눠도 됩니다. 다만 합·bincount·이분법은 직렬이어야 합니다. 크기가 작아(laptop L0 157만 칸) 모두 직렬로 두는 것을 제안합니다.

#### 의심 버그

| 위치 | 내용 | 근거 | 영향 |
|---|---|---|---|
| ocean.py:51-82, materials.py:71-73 | 해수면 부피 V(h)는 해수면 아래 모든 칸을 세지만 바다는 가장 큰 연결 성분뿐입니다. 그래서 바다와 이어지지 않은 분지가 담는 물만큼 실제 바다 부피가 W보다 작습니다 | tiny 거친에서 이어지지 않은 낮은 칸 12개가 2.372e16 m³(W의 1.777%)를 담고, 바다 부피는 0.982·W입니다. L0에는 그런 칸이 없습니다 | 해수면이 조금 낮고 바다 비율이 작게 나옵니다. 명세 4.3과 같은 식이라 그대로 옮깁니다 |
| climate.py:109 | 노이즈 보정 −η²/2는 ξ의 분산이 1일 때만 평균을 지키는데, fbm3 ξ의 표준편차는 약 0.153입니다 | n = 16·32·128에서 면적 평균 배율이 0.9440입니다 | 강수가 띠 평균보다 약 5.6% 적습니다. 명세 4.5와 같은 공식입니다 |
| uplift.py:122-132 | 1e-9 자르기 뒤에 노이즈를 곱하므로 0 < m < 1e-9가 남을 수 있습니다(pipeline.md 4.4 문구와 다름) | 코드 순서. tiny의 최솟값은 1.09e-9라 실제로는 생기지 않았습니다 | 작습니다. 남는 값이 0.86e-9 이상이라 NaN 위험은 없습니다 |
| ocean.py:66-82 | 부피 해상도가 tol보다 크면 상대 오차 1e-9를 지키지 못해도 경고 없이 값을 돌려줍니다 | z = [1e6, 1e6+1], W = 1에서 V(h) = 0(상대 오차 1.0)입니다 | 실제 설정에서는 생기지 않습니다(해상도 약 33 m³ ≪ tol 1.3e9 m³) |

#### golden 대조 계획

모든 대조 시험은 Python 호출 지점의 입력을 그대로 받고 출력만 비교합니다. tiny 입력은 `generate_planet`과 같은 호출(거친 16, L0 32)에서 함수 경계마다 잡습니다. 아래의 'macOS exact 기대'는 .NET이 같은 libm을 부른다는 가정에 기댑니다. 다른 OS에서는 근거를 단 작은 허용 오차를 씁니다.

| 대상 | 입력 | 출력 | 크기 | 비교 |
|---|---|---|---|---|
| `Ocean.VolumeBelow` | tiny L0 sea_level 호출의 z·area와 이분법 중점 31개 + lo·hi. 손 입력: d = 0인 칸, h < z.min | V(h) | 약 100 KB | exact |
| `Ocean.SeaLevel` | tiny 거친(1536칸)·L0(6144칸)의 z·area·W. 손 입력: 시험의 해시 5000칸, 선형 맞춤 경로(W = 1e9), 한 칸, 모든 z가 같은 경우 | h, 참고용 반복 수·종료 경로 | 약 250 KB | exact |
| `Ocean.LabelComponents` | tiny 두 호출의 low·nbr. 손 입력: 평면 두 분지, 대각선 사슬, 구면 꼭짓점 칸 | labels(int64), n_lab | 약 300 KB | exact |
| `Ocean.OceanMask` | tiny 두 호출의 nbr·area·z·h. 손 입력: 면적이 같은 두 분지, z == h, 낮은 칸 없음 | bool 마스크 | 약 50 KB(그래프 공유) | exact |
| `NpUfunc.Tanh` | 손 입력: 구간 경계, ±0, 준정규, 큰 값, ±inf, NaN. tiny L0의 1/phi | np.tanh 비트 | 커밋용 2만 개 약 320 KB | exact |
| `Climate.BudykoRunoff` | tiny 두 호출의 precip·pet, 시험 값(P = 1, PET = 1e-3·1·1e3), 1원소 배열 | runoff | 약 150 KB | exact(다른 OS는 abs(ΔR) ≤ 4·eps·P) |
| `Climate.LatitudeRad` | tiny 거친·L0 graph 배열과 unit() 중간값, 기본 축과 [0.1, 0.2, 0.97] 축, 평면 lat 0·30·45·50·±90 | φ | 약 300 KB | ulp ≤ 2 (macOS exact 기대) |
| `Climate.SurfaceTemperature` | tiny φ·z, z = None, 음수 z | T | 약 150 KB | abs ≤ 1e-13 °C |
| `Climate.GenerateClimate` | tiny 두 호출(graph·z·cfg), 평면 4×5 p_noise = 0, 시험의 z = linspace와 −z | 필드 5개 + 중간값 xi·band | 약 400 KB | precip 8 ulp, T 1e-13, pet 4 ulp, runoff 4·eps·P (macOS exact 기대) |
| `Uplift.ArcDistanceM` | 기본 cfg, 경사 10·20·80°, slab_shallow_depth_m = 0 | d_arc | 수백 B | ulp ≤ 2 |
| `Uplift.GenerateUplift` | tiny 두 호출의 필드 7개. 손 입력: 평면 1×400(kind 0~3, side −1·0·1, 음수 γ, 대륙 칸 d_div 0~100 km, δ = inf, collision_width_m 없는 설정) | U, exhumation, dist_arc + mountain(자르기 전·후)·active·xi | 약 400 KB | active·NaN 위치 exact, U·exhumation 4 ulp (macOS exact 기대) |
| `Materials.BuildMaterials` | cfg(earth + tiny), sphere_graph(16) | 필드 21개 + info 값 | 약 450 KB | 범주·is_ocean exact, 실수는 planet ① 기준, 해수면·면적 비율 exact 기대 |
| `Materials.TransferMaterials` | tiny 호출 인자 그대로. 손 입력: 판 하나의 거리를 inf로, coarse_info = None, 모든 칸 대륙 | 필드 21개 + info + idx·s_f·abs_f·near·fix | 약 1.5 MB(out/golden에 두고 커밋은 압축) | 범주(재부호 43칸 포함)·inf 위치 exact, 보간 값은 core 기준 |

#### 포팅 순서와 위험 메모

1. 공용 도우미를 먼저 만듭니다: `NpReduce.Sum`(pairwise), `NpUfunc.Tanh`, `PyMath.Radians`·`Spacing`·`NaN`, `NpMath.Clip`·`NanToNum`. tanh 복제를 시작하기 전에 FMA 규칙 예외를 결정해야 합니다.
2. `Ocean`을 옮깁니다. CellGraph만 쓰고 초월 함수가 없어서, golden을 모두 exact로 맞출 수 있는 첫 모듈입니다.
3. `Uplift`와 `Climate`를 옮깁니다. core의 `Fbm3`와 planet ①의 `SubSeed`가 먼저 있어야 합니다.
4. `Materials`는 planet ①(plates·crust)과 core(cubesphere·resample·distance)가 끝난 뒤 마지막에 옮깁니다.
5. 단계 1에서 .NET 쪽 가정 세 가지를 먼저 확인합니다: Math.Exp·Sin·Asin·Tan·Atan이 macOS libm과 같은 값을 내는지, double.NaN의 비트, RyuJIT가 a·b + c를 FMA로 묶지 않는지.
6. 위험 요소는 다섯 가지입니다.
   - area 합 하나만 순차로 바뀌어도 해수면 비트가 달라지고, 그러면 bathymetry·기후 z·L0 솔버 전체가 달라집니다.
   - tanh를 libm으로 쓰면 L0 칸의 3.7%에서 runoff_eff가 달라지고(최대 상대 2.6e-15), 그 차이가 솔버로 번집니다.
   - rift 분기, inf 경로, 선형 맞춤 경로는 tiny 실행이 지나가지 않으므로, 손 입력 golden이 없으면 대조가 비게 됩니다.
   - manifest meta.info의 키 순서, 실수 표기(PyJson), NaN 비트는 bake와 함께 맞춰야 합니다.
   - 거친 격자의 uplift·climate는 transfer 뒤에 버려지지만, build golden을 위해 그대로 계산합니다.

### geology — model·rocks (비평 거침)

범위는 `src/bpcg/geology/__init__.py`(4줄, docstring뿐), `model.py`(556줄), `rocks.py`(151줄)입니다. 정렬, 합 축약, 정수 나눗셈, float32 산술은 없습니다. 실수 계산은 습곡 변위의 `np.sin` 한 곳과 core `fbm3`(습곡 노이즈)를 빼면 사칙연산과 비교뿐이라, 연산 순서와 형을 그대로 옮기면 비트 단위로 같습니다. 범주 출력(template_id, strata_rock, 층 번호, 암석 번호)은 완전히 같아야 합니다. 위험도는 model 중간, rocks 낮음입니다. 표의 '검증'은 이 머신(macOS 26 arm64, Python 3.13.15, numpy 2.5.3, numba 0.68.0)에서 tiny 파이프라인의 호출 지점 입력을 포착하거나 손 사례로 돌린 실험입니다. 일반 규칙(NEP 50, libm 차이, .NET 형 변환, PyJson)은 공통 장을 따르고 여기서는 발생 위치만 적습니다.

#### (a) 대응표

| Python | C# (`csharp/Bpcg/Geology/`, namespace `Bpcg.Geology`) |
|---|---|
| `geology/__init__.py` | 파일을 만들지 않습니다(docstring만 있고 재수출이 없음) |
| `rocks.py` | `Rocks.cs`, `public static class Rocks` |
| `ALLUVIUM` … `SOIL`, `N_ROCKS` | `public const byte Alluvium = 0` … `Soil = 11`, `public const int NRocks = 12` |
| `ROCK_NAMES` | `static readonly ImmutableArray<string> RockNames` (bake JSON에 그대로 나감) |
| `K_MULT`, `S_CRIT`, `LOG10_PERM` | `static ReadOnlySpan<double> KMult`, `SCrit`, `Log10Perm` (길이 12) |
| `SOLUBLE` | `static ReadOnlySpan<bool> Soluble` |
| `COLOR_RGB` (12, 3) uint8 | `static ReadOnlySpan<byte> ColorRgb` (36개, 행 우선) |
| `T_SLATE_C` … `T_GRANITE_GNEISS_C` | `public const double TSlateC = 250.0` … `TGraniteGneissC = 650.0` |
| `METAMORPHIC_SERIES` | `static readonly IReadOnlyDictionary<byte, ImmutableArray<(double TMinC, byte Product)>> MetamorphicSeries` (계열 안의 순서를 지킴) |
| `_frozen` (비공개) | 없습니다. ReadOnlySpan 속성이 읽기 전용을 대신합니다 |
| `_MAX_STEPS`, `_series_tables`, `_META_T`, `_META_TO` (비공개) | `private static readonly int MaxSteps`(=3), `SeriesTables()`, `internal static readonly double[] MetaT`(12×3, +inf 채움)와 `byte[] MetaTo`(12×3, 자기 번호 채움). G1이 InternalsVisibleTo로 읽습니다 |
| `peak_temperature_c` | `double PeakTemperatureC(double depthM, Config cfg)`, 배열판 `double[] PeakTemperatureC(ReadOnlySpan<double> depthM, Config cfg)` |
| `metamorphose` | `byte Metamorphose(int rock, double tPeakC)`(0..11 검사), 배열판 `byte[] Metamorphose(ReadOnlySpan<byte> rock, ReadOnlySpan<double> tPeakC)`(같은 길이, 브로드캐스트는 시험용 오버로드에만) |
| `model.py` | `Model.cs`: `public static class Model`, 그리고 `Template`, `LayerColumns`, `GeologyDiag` |
| `PLATFORM`, `FOLD_THRUST`, `ARC` | `public const byte Platform = 0, FoldThrust = 1, Arc = 2` |
| `Template` (frozen dataclass) | `public sealed record Template(string Name, ImmutableArray<(byte Rock, double ThicknessM)> Layers, byte Basement)` |
| `TEMPLATES` | `static readonly ImmutableArray<Template> Templates` (hero/flat.py:99도 읽음) |
| `N_TEMPLATES` … `N_LAYERS` | `static readonly int NTemplates`(3), `NTemplateLayers`(5), `NBasementSplits`(1), `NLayers`(6). Python과 같은 식으로 계산합니다 |
| `ARC_BELT_FRACTION`, `ARC_FOLD_GAP_M`, `FOLD_NOISE_WEIGHT` | `const double ArcBeltFraction = 0.4`, `ArcFoldGapM = 50_000.0`, `FoldNoiseWeight = 0.5` |
| `_FOLD_NOISE_STREAM`, `_REQUIRED_TEMPLATE_FIELDS` (비공개) | `private const long FoldNoiseStream = 5201`, `private static readonly string[] RequiredTemplateFields` |
| `_field` (비공개) | `Field<T>(fields, name, int? n)`(sbyte·byte를 그대로), `FieldF64(fields, name, int? n)`(float[]·double[]를 double[]로 넓힘, `astype(float64)` 대응) |
| `_check_template_id`, `_check_columns` (비공개) | `CheckTemplateId(ReadOnlySpan<byte>)`(0..2), `CheckColumns(double[] bottom, byte[] rock, int nCells, int nLayers)` |
| `_as_2d_z` (비공개) | `int ZCols(int zLength, int nCells, int? zCols)`. zCols가 null이면 (N,) 1차원입니다. 1차원이고 nCells = 0이면 numpy와 같은 메시지로 예외를 던집니다 |
| `assign_template` | `byte[] AssignTemplate(IReadOnlyDictionary<string, Array> fields, Config cfg)` |
| `_sub_seed` (비공개) | `internal static long SubSeed(long seed, long stream)`(>> 33, G6이 InternalsVisibleTo로 시험). bake/detail과 volume/sample의 `_sub_seed`(>> 1)와 합치지 않습니다 |
| `_fallback_fbm3` (비공개) | 옮기지 않습니다(ImportError 때만 쓰는 죽은 경로) |
| `_fold_noise`, `_fold_phase` (비공개) | `FoldNoise(double[] points, long seed)`는 `(double[] Noise, string Backend)`를 돌려주고 `Bpcg.Core.Noise.Fbm3`를 기본 인자로 부릅니다. `internal static (double[] Phase, string Backend) FoldPhase(ReadOnlySpan<double> distConvergentM, Config cfg, double[]? unitPoints)` |
| `fold_displacement_from_phase` | `double[] FoldDisplacementFromPhase(ReadOnlySpan<double> foldPhase, ReadOnlySpan<double> uplift, ReadOnlySpan<byte> templateId, Config cfg, double? uMax = null)` |
| `fold_displacement` | `(double[] FoldPhase, double[] DispM) FoldDisplacement(IReadOnlyDictionary<string, Array> fields, ReadOnlySpan<byte> templateId, Config cfg, double[]? unitPoints = null, double? uMax = null)` |
| `template_columns` | `(double[] DepthBottom, byte[] Rock) TemplateColumns(Config cfg)` (3×6, 3×7) |
| `build_columns` | `(double[] StrataBottom, byte[] StrataRock) BuildColumns(ReadOnlySpan<byte> templateId, ReadOnlySpan<double> exhumationM, double[]? foldDispM, Config cfg)` (N×6, N×7) |
| `layer_index_at` (numba, inline) | `[MethodImpl(MethodImplOptions.AggressiveInlining)] public static int LayerIndexAt(ReadOnlySpan<double> bottom, int nLayers, int c, double z)` |
| `_layer_index_kernel`, `_rock_at_kernel`, `_rock_at_points_kernel` (비공개) | `LayerIndexKernel`, `RockAtKernel`, `RockAtPointsKernel` (`Parallel.For`) |
| `layer_index` | `int[] LayerIndex(double[] strataBottom, int nCells, int nLayers, double[] z, int? zCols = null)` |
| `rock_at` | `byte[] RockAt(double[] strataBottom, byte[] strataRock, int nCells, int nLayers, double[] z, int? zCols = null)` |
| `rock_at_points` | `byte[] RockAtPoints(double[] strataBottom, byte[] strataRock, int nCells, int nLayers, long[] cells, double[] z)` |
| `LayerColumns` (dataclass, eq=False) | `public sealed class LayerColumns(double[] bottom, byte[] rock, double[] kMult, double[] sCrit, int nCells, int nLayers)`. 멤버는 `Bottom`, `Rock`, `KMult`, `SCrit`, `NCells`, `NLayers`, `static FromColumns(double[], byte[], int nCells, int nLayers)`, `int[] LayerIndex(double[] z, int? zCols = null)`, `byte[] RockAt(double[] z, int? zCols = null)`입니다. 참조 동등성이라 record를 쓰지 않습니다. 생성자는 길이만 검사하고 kMult·sCrit를 다시 계산하지 않습니다(landscape/warmstart.py:93이 직접 부름) |
| `surface_rock` | `byte[] SurfaceRock(LayerColumns columns, double[] z)` (1차원) |
| `generate_geology` | `(Dictionary<string, Array> Fields, LayerColumns Columns, GeologyDiag Diag) GenerateGeology(IReadOnlyDictionary<string, Array> fields, Config cfg, double[]? unitPoints = null, double? uMax = null)` |
| (새 형) | `public sealed record GeologyDiag(long[] TemplateCounts, double UMaxMPerYr, string Noise, double Seconds)`. JSON 키 순서는 template_counts, u_max_m_per_yr, noise, seconds입니다 |

입력 필드의 형은 다음과 같습니다. subduction_side는 `sbyte[]`, convergence_kind는 `byte[]`이고, dist_convergent_m, dist_arc_m, uplift_m_per_yr, exhumation_m은 `double[]`입니다. FIELDS dtype은 float32이지만 행성 호출 지점에서는 float64임을 검증했습니다. unit_points는 행 우선 `double[3N]`입니다. 출력은 template_id `byte[]`, fold_phase `double[]`, strata_bottom_m `double[6N]`, strata_rock `byte[7N]`이고, float32는 묶음에 쓸 때만 씁니다. 묶음은 필드를 이름순으로 쓰므로(bake/bundle.py:184) 출력 사전의 순서는 파일에 나타나지 않습니다.

#### (b) 수치 함정 표

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| model.py:218 | op-order | φ = ((2.0·π)·δ)/λ입니다. `δ·(2π/λ)`는 36.8 %, `2π·(δ/λ)`는 34.8 %가 마지막 자리에서 다릅니다(검증, 1e6개) | `2.0 * Math.PI * d / lam`을 그대로 씁니다 | exact |
| model.py:228 | op-order | 노이즈 점은 p·(R/λ)입니다. `(p·R)/λ`는 31 %가 다릅니다(검증) | `double s = radius / lam;` 다음에 `p[i] * s` | exact |
| model.py:229, 201-207 | fbm | φ + 0.5·fbm3입니다. fbm3는 기본 인자(octaves 5, gain 0.5, lacunarity 2.0, frequency 1.0)로 부르고, 점에 NaN·inf가 있으면 예외입니다. 노이즈는 δ가 inf인 칸까지 모두 계산한 뒤 가립니다. numba 없는 순수 Python 재구현이 tiny 호출 지점 5120점에서 비트 일치했고, 행성 fold_phase 전체를 다시 계산해도 같았습니다(검증, FMA 없음) | `Noise.Fbm3(points, seed)`(FMA 금지), `phase + 0.5 * (finite ? noise : 0.0)` | exact |
| model.py:213-215 | validation | `not lam > 0` 검사는 NaN, 0, 음수를 막지만 +inf는 통과시킵니다. 그러면 φ = 0.5·fbm3(±0 점)인 상수가 됩니다(seed 0에서 −0.014437573323252061, 검증) | `if (!(lam > 0)) throw`. `lam <= 0`(NaN 통과)이나 IsFinite 검사(+inf 거부)로 바꾸지 않습니다 | exact |
| model.py:217-218, 252, 262 | nan-mask | 유한하지 않은 δ, U, φ 칸에는 +0.0을 넣습니다 | `double.IsFinite`로 분기 | exact |
| model.py:262 | transcendental | `np.sin` float64는 이 머신의 libm `sin`과 351만 점(호출 지점 위상 포함, ctypes libm 대조)에서 비트 일치했습니다(검증). macOS `Math.Sin`도 같다고 보며, glibc와 UCRT는 1 ulp쯤 다를 수 있습니다 | `Math.Sin`. abs(Δ) ≤ 4·ulp(A) ≈ 9.1e-13 m | tolerance |
| model.py:262-263 | op-order·±0 | Δ = (A·ratio)·sin입니다. `A·(ratio·sin)`은 35 %가 다릅니다. ratio = +0이고 sin < 0이면 −0.0이 나옵니다. tiny 행성에서 34칸이고 모두 exh = 0이라 z_top은 +0.0으로 같습니다(검증) | `amp * ratio * s`를 습곡 칸마다 그대로 계산합니다(ratio = 0 지름길 금지) | exact(부호 포함) |
| model.py:252-254, 539 | max | `np.maximum(U, 0)`은 −0.0을 +0.0으로 바꾸고, NaN은 마스크가 지웁니다. 부호 있는 0은 출력에 영향이 없습니다(u_max = 0이면 0 배열을 돌려줌, 검증) | `Math.Max(u, 0.0)`. 최댓값은 어느 반복으로 구해도 됩니다 | exact |
| model.py:261 | min | `np.minimum(u_pos/u_max, 1.0)`에는 NaN이 없습니다. 아주 작은 u_max로 생긴 inf는 1이 됩니다 | `Math.Min` | exact |
| model.py:256-260 | validation | u_max가 NaN, inf, 음수이면 예외입니다. −0.0은 통과해 0 배열을 돌려줍니다. amp는 이른 반환 전에 읽습니다 | `!double.IsFinite(u) \|\| u < 0`이면 예외, `u == 0.0`이면 `new double[n]` | n/a |
| model.py:258, 369-370 | validation | fold_amplitude_m는 검사하지 않습니다. NaN·inf이면 습곡 칸 Δ가 NaN·inf가 되어 build_columns가 'fold_disp_m 에 NaN 이나 inf' 예외를 내고, 음수는 그대로 통과합니다(검증) | BuildColumns의 유한 검사를 그대로 둡니다 | n/a |
| model.py:327, 330 | py-max | 내장 `max(a, b)`는 b > a일 때만 b를 돌려줍니다. surface_temperature_c = nan이면 바닥 1800과 rock [1,3,2,3,1,8,8]이 나오고, Math.Max를 쓰면 NaN 바닥이 됩니다(검증) | `PyMath.Max(a, b) => b > a ? b : a` | exact |
| rocks.py:107-108, 148-150 | table | 없는 단계는 문턱 +inf, 결과 '자기 자신'으로 채웁니다. 그래서 T = +inf이면 단계가 3개 미만인 암석이 원래 암석으로 돌아갑니다(검증, 의심 버그 1) | `MetaT`, `MetaTo`, k 반복을 그대로 옮깁니다 | exact |
| rocks.py:145, 149 | compare | `t >= 문턱`은 float64 비교이고 NaN은 그대로 둡니다. T_peak는 언제나 np.float64이고 float32 깊이도 먼저 넓힙니다(검증) | double의 `>=` | exact |
| rocks.py:128-130, model.py:313-314, 322 | op-order | T = t_surf + grad·깊이, mid = top + 0.5·두께(top 갱신 전)입니다 | 같은 식과 순서, FMA 금지 | exact |
| model.py:311-316 | cumsum | `top += t`로 차례로 더합니다(정수라 정확). 기반암 경계는 (650−10)/0.03 = 21333.333333333336입니다(검증) | double 차례 반복, 경계는 실행 중 나눗셈 | exact |
| model.py:364-374 | op-order | 바닥 = (exh + disp) − depth[t, i]로 한 번만 뺍니다. 층마다 차례로 빼거나 (exh − depth) + disp로 바꾸면 다릅니다(검증) | `zt = exh[c] + disp[c]`(null이면 더하지 않음), `zt - depth[t*L + i]` | exact |
| model.py:364, 371 | aliasing | `exh.copy()` 뒤에 `+=`라 호출자 배열은 그대로입니다. out과 LayerColumns가 같은 배열을 공유하고, 이후 제자리 수정은 없습니다(grep) | 새 배열에 쓰고 같은 참조를 공유합니다 | n/a |
| model.py:317-319, 331-338 | sentinel | 빈 층 −1을 아래부터 위로 채웁니다(`thick <= 0 \|\| rock < 0`). 331-333의 insert는 지금 표에서 죽은 코드이고 −1은 남지 않습니다 | `List<int>`, `Insert(Count - 1, -1)`을 그대로 두고 다 채운 뒤 byte로 | exact |
| model.py:161-169 | compare | 엄격한 `<`와 `>`이고 NaN은 거짓입니다. 화산호(2)가 습곡(1)을 덮습니다. arc_half, d_arc+50 km, fold_w와 같은 값은 고르지 않습니다(검증, 12칸) | 같은 불식, `Math.Abs`, 2 우선 | exact |
| model.py:158-159, 165 | config | arc_half = 0.4·250000 = 100000.0(검증)이고 d_arc + 50000.0은 칸마다 더합니다 | 실행 중에 계산하고 상수로 박지 않습니다 | exact |
| model.py:158, 213, 226-227, 258, 302-303 | config-types | 설정은 모두 `float(...)`(TOML 정수 허용)로, seed는 `int(...)`로 읽습니다. `bpcg all`의 히어로 설정은 manifest JSON에서 다시 읽습니다(왕복 정확) | Config 접근자가 TOML 정수와 실수를 모두 double로, seed는 long으로 | exact |
| model.py:106-114, 155-156, 248-249, 359, 366, 419, 436, 451 | dtype | 실수 입력은 계산 전에 모두 float64로 넓히므로 NEP 50 float32 경로가 없습니다(검증). template_id 호출자는 모두 uint8입니다 | `double[]`와 `ReadOnlySpan<byte>`만 받고 float[]는 `FieldF64`가 넓힙니다 | exact |
| model.py:379-390 | compare | `bottom[c,i] < z`인 첫 층, 없으면 L입니다. 같은 z는 아래층, NaN z는 L, +inf는 0, −inf는 L이고 ±0은 같습니다. NaN 바닥은 건너뜁니다(검증). 솔버는 layers=None일 때 L = 0을 넘기고 0을 받습니다 | `LayerIndexAt`에 `<`, nLayers는 인자로 받습니다 | exact |
| model.py:379-390 대 solver.py:133-144, drainage.py:133-137 | compare | 솔버 `_upper_layer`와 drainage의 안쪽 반복은 `<=`(z 바로 위 층)로, 일부러 다른 약속입니다 | `<=` 반복에는 LayerIndexAt를 쓰지 않습니다. volume/sample.py:318-322(`<`)는 써도 됩니다 | exact |
| model.py:393-411 | prange | 칸이나 점마다 자기 칸만 쓰고 축약은 없습니다 | `Parallel.For`, 안쪽 m은 순차 | exact |
| model.py:130-137 | shape | (N,)와 (N, M)은 펼치면 같지만, N = 0이고 z가 1차원이면 numpy reshape 예외가 납니다. metrics/scorecard가 이 메시지를 scorecard.json에 적습니다(검증, 의심 버그 4) | `int? zCols`로 1차원을 구분하고, 1차원·nCells = 0이면 'cannot reshape array of size 0 into shape (0,newaxis)'로 예외 | exact(메시지) |
| model.py:118-119, 450 | cast | uint8 강제 변환으로 256은 0(통과), −1은 255(범위 예외), 3.7은 3이 됩니다. cells 0.9는 0, NaN은 0입니다(검증) | `byte[]`, `long[]` 형이라 도달하지 않습니다(의심 버그 3) | n/a |
| model.py:423, 437, 456 | dtype | layer_index는 int32(커널은 int64를 돌려줌), rock_at과 rock_at_points는 uint8입니다 | `int[]`, `byte[]` | exact |
| model.py:551 | count | `np.bincount(uint8, minlength=3).tolist()`, tiny 결과는 [5763, 234, 147]입니다 | `long[3]`에 반복문으로 셈 | exact |
| model.py:174-176 | hash | `hash3(int64 seed, 5201, 0) >> 33`으로 31비트 값을 만듭니다. seed 0은 1769400347, −1은 1487617789이고 ±2^63 경계까지 같습니다(검증). bake/detail.py:70-73과 volume/sample.py:94-97의 `_sub_seed`는 `>> 1`과 다른 인자를 씁니다 | `unchecked((long)(Hash3((ulong)seed, (ulong)stream, 0UL) >> 33))`, 클래스마다 따로 둡니다 | exact |
| model.py:179-207 | dead-code | 대체 노이즈(BLAS `@`, libm)는 ImportError 때만 씁니다 | 옮기지 않습니다. backend는 "fbm3" 또는 "none" | n/a |
| model.py:530, 554 | clock | diag seconds는 실행마다 다릅니다 | `Stopwatch`, 대조에서 뺍니다 | n/a |
| model.py:550-555 | json | diag 키 순서가 manifest meta.diag.geology에 그대로 나옵니다(sort_keys 없음, indent 2). u_max는 repr로 적습니다(0.001655391227270644) | 키 순서를 고정한 직렬화와 `PyJson` | n/a |
| rocks.py:47-81 | const | 표 리터럴은 가장 가까운 double로 읽고, numpy 표는 읽기 전용입니다. ROCK_NAMES, COLOR_RGB, SOLUBLE은 bake/corridor.py:288-290과 bake/globe.py:737-743이 JSON에 쓰고, LOG10_PERM은 groundwater.py:99의 `10.0 ** x`가 씁니다 | `ReadOnlySpan<T>`, 문자열과 정수를 그대로 | exact |
| pipeline.py:632, cli.py:283-286, hero/refine.py:83-88, 180-184 | storage-dtype | 메모리의 fold_phase와 strata_bottom_m은 float64, 묶음은 float32입니다. `bpcg all`의 히어로는 묶음을 다시 읽으므로 φ가 최대 1.21e-4 rad 바뀌고(위상 최대 3147 rad), 히어로 Δ가 최대 0.18 m 달라집니다(검증) | 메모리는 `double[]`, 묶음에 쓸 때만 `(float)`. 콘솔 all도 다시 읽고 diag u_max는 왕복 정확한 파서로 읽습니다 | exact |
| hero/flat.py:99 | sum | `sum(t for _, t in TEMPLATES[1].layers)`는 Python 3.13의 보정 합(Neumaier)이지만 두께가 정수라 2600.0으로 정확합니다(검증) | 단순 반복 | exact |

#### (c) 외부 호출과 대체 방식

- numpy 원소별 함수(`asarray`, `ascontiguousarray`, `where`, `isfinite`, `abs`, `maximum`, `minimum`, `diff`, `concatenate`, `broadcast_arrays`, `tile`, `arange`, `full`, `zeros`, `empty`, `bincount`)는 C# 반복문으로 옮깁니다. 형 변환은 넓히기뿐이라 정확합니다.
- `np.sin`은 `Math.Sin`으로 옮기고 허용 오차로 대조합니다. `np.errstate(invalid="ignore")`는 경고만 끄므로 대응 코드가 없습니다.
- `bpcg.core.noise.fbm3`(함수 안에서 늦게 import)는 `Bpcg.Core.Noise.Fbm3(double[] points, long seed)`로 기본 인자 그대로 부릅니다. `bpcg.core.hashing.hash3`은 `Bpcg.Core.Hashing.Hash3`입니다. 둘 다 core 대조 시험이 먼저 통과해야 합니다.
- `hash_unit`, `math.cos`·`math.sin`·`math.sqrt`, 행렬곱 `@`는 대체 노이즈에서만 쓰므로 옮기지 않습니다.
- `numba.njit`과 `prange`는 정적 메서드와 `Parallel.For`로, `time.perf_counter`는 `Stopwatch`로 옮깁니다.
- scipy, scikit-image, trimesh 호출은 없고 NuGet 패키지도 필요 없습니다.
- Python `ValueError`는 공통으로 정할 C# 예외 형으로 옮깁니다. 이 형은 두 곳에서 결과를 바꿉니다. metrics/scorecard의 `_run`(scorecard.py:241-245)은 ValueError 메시지를 scorecard.json의 note에 적으므로, scorecard가 부르는 `LayerIndex`와 `RockAtPoints`의 메시지는 Python 문구 그대로 둡니다. 또 `bpcg all`(cli.py:287-289)은 히어로 단계의 ValueError를 잡아 평면 히어로로 바꾸므로, refine 안의 geology 검사 예외도 같은 형이어야 합니다.

#### (d) numba 커널과 prange

| 커널 | 장식 | prange 축 | 경쟁·축약 | C# |
|---|---|---|---|---|
| `layer_index_at` | `njit(cache=True, inline="always")` | 없음(층을 차례로 보고 첫 일치에서 반환) | 없음 | 정적 메서드입니다. landscape/solver의 `_surface_law_kernel`과 metrics/drainage의 `_layer_cross_kernel`이 직접 부릅니다. volume/sample.py:318-322의 같은 반복(`<`)도 이 메서드로 바꿔 써도 됩니다 |
| `_layer_index_kernel` | `njit(cache=True, parallel=True)` | 칸 c | 없음(`out[c, m]`만 씀) | `Parallel.For(0, nCells, c => …)`, 안쪽 m은 순차 |
| `_rock_at_kernel` | 같음 | 칸 c | 없음 | 같음 |
| `_rock_at_points_kernel` | 같음 | 점 k | 없음(`out[k]`만 쓰고 `cells`의 중복은 읽기만) | `Parallel.For(0, m, k => …)` |

컴파일된 형은 bottom·z가 float64, rock·out이 uint8, layer_index의 out이 int32, cells가 int64입니다(signatures로 검증). 커널 안에 실수 산술이 없으므로 numba 형 승격 함정이 없고, 결과는 스레드 수와 무관합니다. `np.empty` 출력은 커널이 모든 칸을 채우므로 C#의 0 초기화 배열과 결과가 같습니다.

#### (e) 의심 버그

1. **+inf 온도에서 변성이 되돌아감** (`rocks.py:107-108, 148-150`). 없는 단계를 문턱 +inf와 결과 '자기 자신'으로 채웠기 때문에, T = +inf이면 점판암, 석회암, 사암, 화강암, 편암이 원래 암석으로 남습니다(+inf >= +inf가 참). 1e308에서는 정상으로 변성되고, surface_temperature_c = +inf 설정의 템플릿 0 rock 행은 [1,3,8,3,1,4,4]입니다(검증). 지금 파이프라인은 온도가 늘 유한해 영향이 없습니다.
2. **geology 설정 검사의 빈틈** (`model.py:213-215, 258, 302-305, 327, 330`). surface_temperature_c는 검사하지 않습니다. nan이면 오류 없이 바닥 1800 m와 편마암 기반암이 나오고(화산호 템플릿 행 [5,8,8,8,8,8,8]), +inf이면 1번이 드러나며, −inf이면 기반암 경계 깊이가 +inf라 strata_bottom에 −inf가 들어가 솔버 `_layer_arrays`에서야 예외가 납니다. fold_wavelength_m = +inf는 `not lam > 0`을 통과해 위상이 상수가 되고, fold_amplitude_m의 NaN·inf는 build_columns에서야 잡히며 음수는 통과합니다(모두 검증). `--set`과 스튜디오는 `checked_overrides`(config.py:163-166, 186, 221-246)가 nan·inf를 거부하므로, TOML 파일을 직접 고칠 때만 도달합니다(음수 진폭은 `--set`으로도 됨).
3. **암석 번호를 강제 변환한 뒤 검사함** (`model.py:118-119, 487-489, 450`). 256은 0, 3.7은 3이 되어 `LayerColumns.from_columns`를 통과하고(−1은 255가 되어 거부됨), `rock_at_points`는 실수 cells를 버림합니다(NaN은 칸 0, 검증). 손상된 묶음을 거르지 못하지만 파이프라인에서는 닿지 않습니다. C#은 형으로 막습니다.
4. **N = 0이고 z가 1차원이면 reshape 예외** (`model.py:136`, 도달 경로 `metrics/scorecard.py:113-117`). `surface_rock_samples`가 표본 칸 0개로 `layer_index`를 부르면 예외가 나고, `_run`이 rock_consistency 항목을 'pass: false, note: 계산 실패: cannot reshape array of size 0 into shape (0,newaxis)'로 적습니다. 원래 의도인 '표본 없음'(판정 없음)이 되지 않습니다(검증). 표본 칸이 0개일 때, 곧 육지가 없거나 육지가 모두 not_steady나 선상지일 때만 생기는 드문 경우입니다. C#은 같은 메시지로 예외를 던져 같은 scorecard.json을 냅니다.
5. **히어로의 습곡 변위가 L0와 다름** (`hero/refine.py:180-183`, `pipeline.py:632-645`, `model.py:537-540`). L0는 세기 한계를 적용하기 전의 U로 변위를 정하는데, 묶음에는 줄인 U'가 들어갑니다. 히어로는 U'와 L0의 U_max(1.655e-3, diag)로 변위를 다시 구합니다. tiny에서는 습곡충상대 234칸 가운데 147칸의 U가 줄었고, 그 칸들의 최대 U는 9.26e-4에서 2.28e-4 m/yr로 바뀌었습니다. 행성 칸에서 다시 구한 max abs(Δ)는 269.1 m에서 126.1 m로 줄고, 같은 칸의 차이는 최대 264.8 m입니다(검증). 의도인지 확인이 필요하고, 포팅은 그대로 따릅니다.

#### (f) golden 대조 계획

| 대상 | 입력 | 출력 | 크기 | 비교 |
|---|---|---|---|---|
| G1 Rocks 표 | 없음(상수). MetaT·MetaTo는 InternalsVisibleTo로 읽음 | KMult, SCrit, Log10Perm, Soluble, ColorRgb, RockNames, MetaT, MetaTo | < 2 KB | exact(리터럴 파싱도 함께 확인) |
| G2 `Rocks.Metamorphose` | 암석 0..11 × 온도 17개(−inf, −1e308, ±0, 문턱 250·350·400·600·650과 각 nextafter 아래, 1e308, +inf, NaN) | (12, 17) uint8 | < 1 KB | exact. tiny 기본 설정에서는 퇴적층이 변성되지 않으므로(층 가운데 최고 79 °C) 손 사례가 필요합니다 |
| G3 `Rocks.PeakTemperatureC` | 깊이 12개 × 설정 3개(기본, 300 °C·0.1, 600 °C·0.1) | float64 (3, 12) | < 1 KB | exact |
| G4 `Model.TemplateColumns` | 설정 6개: 기본, 300 °C·0.1, 600 °C·0.1, surface_temperature_c = nan, +inf, −inf | depth (3, 6) f64(+inf 포함), rock (3, 7) u8 | < 2 KB | exact. `PyMath.Max`, +inf 표 동작, +inf 깊이를 고정합니다 |
| G5 `Model.AssignTemplate` | tiny 행성 호출 지점(N = 6144, dist_arc_m NaN 4113개)과 손 사례 16칸(경계 같은 값 3종과 nextafter, NaN, ±inf, side 0·−1, kind 3) | template_id u8 | ~120 KB | exact |
| G6 `Model.SubSeed` | seed {0, 1, −1, 2^63−1, −2^63} × stream 5201 | int64 (5,) | < 1 KB | exact |
| G7 `Model.FoldPhase` | ① tiny 행성 dist와 unit_points(6144×3) ② 평면 히어로 (y, pos/R) ③ unit_points 없음 ④ δ {NaN, ±inf, 0, 1e308} ⑤ λ = +inf(통과, 상수 위상), λ ∈ {NaN, 0, −1}(예외) | phase f64, backend | ~420 KB | exact(Fbm3 비트 재현이 전제) |
| G8 `Model.FoldDisplacementFromPhase` | ① tiny 행성(u_max 없음) ② 히어로 refine 호출 지점(묶음에서 읽은 φ와 U', u_max = diag 값) ③ 평면 히어로 ④ 손 사례(U에 NaN, ±inf, 음수, −0.0, φ에 ±inf와 NaN, u_max = 0과 4e-3) | disp f64 | ~360 KB | tolerance abs(Δ) ≤ 4·ulp(A) ≈ 9.1e-13 m. macOS에서는 −0.0 34칸을 포함한 비트 일치를 기대하고 다른 칸 수를 기록합니다 |
| G9 `Model.BuildColumns` | G8 ①②③ 호출 지점의 (tid, exh, Python이 낸 disp), disp = null, 오류 사례(exh NaN, disp inf, tid 3은 예외만 확인) | strata_bottom (N, 6) f64, strata_rock (N, 7) u8 | ~1 MB(압축 전) | exact |
| G10 `LayerIndex`, `RockAt`, `RockAtPoints`, `SurfaceRock` | ① tiny 행성 LayerColumns와 최종 z_m ② caves 호출 지점 zk(N, 2, NaN 포함) ③ finder 호출 지점 zz(N, K) ④ 기둥 몇 개 × z {바닥, nextafter 위·아래, ±0, ±inf, NaN} ⑤ cells int64와 z ⑥ nLayers = 0 ⑦ 1차원 nCells = 0(예외 메시지)과 (0, M)(빈 결과) | int32 / uint8 | ~600 KB | exact. tiny에는 경계와 같은 z가 없으므로(최소 거리 행성 0.84 m, 히어로 0.065 m, 평면 0.028 m, 검증) ④가 필요합니다 |
| G11 `LayerColumns.FromColumns` | G10 ①의 strata와 히어로 refine의 strata | k_mult, s_crit (N, 7) f64 | ~0.7 MB | exact |
| G12 `Model.GenerateGeology` | tiny 행성 호출 지점의 fields 6개와 unit_points | template_id, fold_phase, strata_bottom_m, strata_rock, diag(seconds 제외) | ~750 KB | strata_bottom_m만 macOS에서 비트 일치를 기대하고, 그 밖 OS는 abs(Δ) ≤ 1e-11 m입니다(abs(z_top)과 abs(bottom)이 2^15 m 미만이라 ulp ≤ 3.64e-12, Δ 차이 ≤ 9.1e-13, 바닥이 0 근처여도 성립). 나머지는 exact |

golden 자료는 `csharp/golden/export_golden.py`가 `out/golden/geology/`에 씁니다. 호출 지점 입력은 tiny 파이프라인을 돌리며 해당 함수를 감싸 저장하는데, 이 방식이 '함수를 부르기만 한다'는 규칙에 맞는지는 아직 정하지 않았습니다.

#### (g) 포팅 순서와 위험 메모

1. `Rocks.cs`(G1~G3)를 먼저 옮깁니다. 표, `MetaT`/`MetaTo`, `Metamorphose`는 solver, groundwater, caves, bake 색표가 모두 씁니다.
2. 다음으로 `Model`의 상수, `Template`, `TemplateColumns`(G4), `BuildColumns`(G9), `LayerIndexAt`과 커널 3개(G10), `LayerColumns`(G11)를 옮깁니다. landscape 솔버가 `LayerIndexAt`과 `LayerColumns`를 바로 쓰므로 이 부분이 핵심입니다.
3. core의 `Hashing`과 `Noise.Fbm3` 대조가 통과한 뒤 `SubSeed`(G6), `FoldPhase`(G7), `FoldDisplacementFromPhase`(G8), `AssignTemplate`(G5)를 옮깁니다.
4. 마지막으로 `GenerateGeology`(G12)와 diag 직렬화를 옮깁니다.
5. 위험 메모는 다음과 같습니다.
   - 플랫폼에 따라 마지막 자리가 달라질 수 있는 것은 습곡 변위의 sin뿐입니다. 이 차이가 경계에 걸린 z의 범주를 바꿀 수 있지만 tiny에서는 최소 거리가 0.028 m라 일어나지 않습니다. 큰 프로필에서 범주가 갈리면 허용 오차를 넓히지 않고 갈린 칸의 수와 위치와 원인을 진행 기록에 적습니다.
   - `bpcg all`의 히어로는 float32 묶음을 다시 읽어 지질 기둥을 다시 만듭니다(φ 반올림만으로 Δ가 최대 0.18 m 바뀜). C# 콘솔도 같은 경로를 따라야 끝에서 끝 비교가 맞습니다.
   - `_sub_seed`는 geology, bake/detail, volume/sample에 뜻이 다른 세 벌이 있습니다. 공용 도우미로 합치면 시드가 바뀝니다.
   - `LayerIndexAt`(`<`, 경계는 아래층)과 솔버 `_upper_layer`·drainage 반복(`<=`)은 약속이 다릅니다. 하나로 합치지 않습니다.
   - scorecard가 geology 예외 메시지를 JSON에 적고 `bpcg all`이 히어로 단계의 ValueError로 평면 히어로를 고르므로, 예외 형과 1차원 nCells = 0 예외의 numpy 문구를 그대로 둡니다.
   - tiny 기본 설정에서는 퇴적층 변성 경로를 지나지 않고 기반암 경계의 화강암 → 편마암만 지나갑니다. 나머지 변성 경로는 G2~G4의 손 사례로만 검증합니다.
   - docs/pipeline.md 5.2와 코드가 다른 곳은 세 가지입니다. 문서는 'L은 가장 긴 템플릿 기준', LayerColumns 순서 (bottom, K_mult, s_crit, rock), '각 층의 변성은 가운데 깊이 T_peak'라고 적습니다. 코드는 L = 5 + 기반암 경계 1 = 6, 순서 (bottom, rock, k_mult, s_crit)이고, 기반암 층은 꼭대기 온도와 문턱 온도로 정합니다. C#은 코드를 따릅니다.
   - Roslyn 실수 리터럴이 정확히 반올림된다는 것은 .NET 문서에 기댄 가정이라 단계 1에서 G1로 먼저 확인합니다. `Math.Max`의 ±0 의미는 출력에 영향이 없음을 검증했으므로 위험 요소가 아닙니다.

### landscape — solver·fans·relief·warmstart

대상은 `src/bpcg/landscape/`의 다섯 파일(1,079줄)이고, C#에서는 `csharp/Bpcg/Landscape/`의 네 파일(`namespace Bpcg.Landscape`)로 옮깁니다. 이 묶음은 모두 float64로 계산하며, float32 산술은 없습니다.

아래에서 '검증'은 이 기계에서 scratch 실험으로 확인했다는 뜻입니다. 환경은 macOS 26.6.2 arm64, Python 3.13.15, numpy 2.5.3과 Accelerate, scipy 1.18.1, numba 0.68.0입니다.

#### (a) 대응표

`__init__.py`(4줄)는 docstring만 있고 다시 내보내는 이름이 없습니다. 따라서 C# 파일을 만들지 않습니다.

수신 셀·순서·꼭짓점 번호는 Python에서 int64입니다. C#에서는 hydro와 같게 `int[]`로 두고, golden 로더가 값을 바꿉니다(결정 필요).

| Python | C# 파일 | 형 |
|---|---|---|
| `solver.py` (588줄) | `Landscape/Solver.cs` | `static class Solver`, `sealed class SolverResult`, `sealed record SolverIteration`, `sealed record SolverDiag` |
| `fans.py` (206줄) | `Landscape/Fans.cs` | `static class Fans`, `sealed record FanApex`, `sealed record RerouteResult` |
| `relief.py` (154줄) | `Landscape/Relief.cs` | `static class Relief` |
| `warmstart.py` (127줄) | `Landscape/Warmstart.cs` | `static class Warmstart`, `sealed record WarmStartLevel`, `sealed class WarmStartDiag` |

| Python 멤버 | C# 멤버 |
|---|---|
| `INIT_SLOPE`, `INIT_NOISE_M`, `INIT_NOISE_WAVELENGTH_CELLS`, `FREEZE_AFTER_FLIPS`, `_STREAM_INIT_NOISE` | `const double InitSlope = 1e-3`, `InitNoiseM = 10.0`, `InitNoiseWavelengthCells = 16.0`; `const int FreezeAfterFlips = 16`; `private const long StreamInitNoise = 7101` |
| `SolverResult` | 속성 `Z`, `Receiver`(int[]), `Order`(int[]), `Discharge`, `DrainageArea`, `SedimentFlux`, `Slope`, `KS`, `SCritSurface`, `Iterations`, `History`(`List<SolverIteration>`), `Converged`, `Seconds`, `NFrozen`, `Frozen`(bool[]?), `UpliftEffective`(double[]?) |
| `SolverResult.fields(cast)`, `.diag()` | `IReadOnlyDictionary<string, Array> Fields(bool cast = false)`, `SolverDiag Diag()` |
| history dict | `record SolverIteration(int Iteration, long NChanged, double MaxDz, long NFrozen)` (키 순서 iteration, n_changed, max_dz, n_frozen) |
| `_upper_layer`, `_law_slope` | `private static int UpperLayer(double[] bottom, int nLayers, int c, double z)`, `private static double LawSlope(double ksRef, double kMult, double invN, double qt, double hill, double sCrit, double sMin)` |
| `_receiver_slot_distance` | `private static double ReceiverSlotDistance(int c, int r, int[] nbr, double[] dist, int nSlot)` |
| `_integrate_kernel` | `internal static int IntegrateKernel(int[] order, int[] rcv, int[] nbr, double[] dist, int nSlot, double[] zOutlet, double[] q, double[] aUp, double[] qs, double[] u, double[] width, double[] bottom, int nLayers, double[] kMult, double[] sCrit, double kRef, double uRef, double invN, double theta, double gDep, double rRef, double diff, double sMin, double riseMin, double[] zNew)` |
| `_surface_law_kernel` | `internal static void SurfaceLawKernel(int[] rcv, int[] nbr, double[] dist, int nSlot, double[] z, double[] q, double[] qs, double[] u, double[] bottom, int nLayers, double[] kMult, double[] sCrit, double kRef, double uRef, double invN, double gDep, double rRef, double[] slope, double[] kS, double[] sCritOut)` |
| `_freeze_kernel` | `internal static (long NChanged, long NFrozen) FreezeKernel(double[] zt, int[] prev, int[] rcv, int[] flips, long maxFlips)` |
| `_eps_drop_count`, `_has_eps_drop` | `internal static long EpsDropCount(double[] z, int[] rcv, bool[] isOutlet, double eps)`, `private static bool HasEpsDrop(...)` |
| `_vector`, `_law_params`, `_layer_arrays`, `_check_graph`, `_check_outlets` | 모두 private: `Vector(double[]? x, int n, string name, bool allowNone = false)`와 `Vector(double x, int n)`, `LawParams ReadLawParams(Config cfg)`, `(double[] Bottom, int NLayers, double[] KMult, double[] SCrit) LayerArrays(LayerColumns? layers, int n, double sCritDefault)`, `int CheckGraph(CellGraph g)`, `bool[] CheckOutlets(bool[] m, int n)` |
| `initial_surface` | `public static double[] InitialSurface(CellGraph graph, bool[] isOutlet, double[] zOutlet, Config cfg)` (+ `double zOutlet` 오버로드) |
| `solve_steady_state` | `public static SolverResult SolveSteadyState(CellGraph graph, bool[] isOutlet, double[] zOutlet, double[] uplift, double[] runoffEff, LayerColumns? layers, Config cfg, double[]? zInit = null, double[]? extraInflow = null, int? maxIter = null, Action<string>? log = null)` (+ 오버로드). 시험용으로 `internal static SolverStep Step(...)`을 둡니다 |
| `fans._arc_to_chord`, `_chord_to_arc`, `_receiver_distance` | 모두 private: `double ArcToChord(CellGraph g, double s)`, `double ChordToArc(CellGraph g, double chord)`, `double[] ReceiverDistance(CellGraph g, int[] rcv)` |
| `find_fan_apexes` | `public static int[] FindFanApexes(CellGraph graph, SolverResult result, Config cfg)` |
| `add_fans` | `public static (double[] ZNew, bool[] Fan, bool[] NotSteady, List<FanApex> Apexes) AddFans(CellGraph graph, double[] z, SolverResult result, Config cfg)`; `record FanApex(long Cell, long Receiver, double DischargeM3PerYr, double ZM, double SlopeRatio, int NRaised, double[] Pos)` |
| `reroute` | `public static RerouteResult Reroute(CellGraph graph, double[] z, bool[] isOutlet, double[] runoffEff, Config cfg, double[]? extraInflow = null)`. 파이프라인은 이 함수를 쓰지 않습니다 |
| `relief.N_QUADRATURE`, `_KM`, `_KM2`, `_vec` | `const int NQuadrature = 32`; `private const double Km = 1e3, Km2 = 1e6`; `private static double[] Vec(double[] x, int n, string name)` |
| `channel_head_area`, `river_relief`, `hillslope_relief` | `ChannelHeadArea(double[] kS, double[] sCrit, double[] runoffEff, Config cfg)`, `RiverRelief(double[] kS, double[] runoffEff, double[] aStar, double[] cellArea, Config cfg)`, `HillslopeRelief(double[] sCrit, double[] uplift, double[] aStar, double[] cellArea, Config cfg)`, 모두 `public static double[]` |
| `subgrid_relief` | `public static (double[] ReliefM, double[] ZMeanM) SubgridRelief(CellGraph graph, double[] z, double[] kS, double[] sCrit, double[] runoffEff, double[] uplift, bool[] isOcean, Config cfg)` |
| `warmstart.MIN_COARSE_CELLS`, `MIN_WARM_START_CELLS`, `_block_index` | `const int MinCoarseCells = 64`, `MinWarmStartCells = 16`; `private static (int[] Blk, int Nyc, int Nxc) BlockIndex(CellGraph g, int factor)` |
| `coarse_warm_start` | `public static (double[]? ZInit, WarmStartDiag Diag) CoarseWarmStart(CellGraph graph, bool[] isOutlet, double[] zOutlet, double[] uplift, double[] runoffEff, LayerColumns? columns, Config cfg, double[]? extraInflow = null, int factor = 4, int? maxIter = null, Action<string>? log = null)`. 시험용으로 `internal static double[] InterpolateFromCoarse(CellGraph fine, double[] zCoarse, int nyc, int nxc, double dxc)`를 둡니다 |

#### (b) 수치 함정 표

'비교' 열의 tolerance는 golden 플랫폼(macOS arm64, 같은 libm)에서는 exact로 돌립니다. 다른 플랫폼에서만 상대 1e-14를 씁니다.

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| solver.py:227 | 초월 함수 | numba는 `np.exp(theta*np.log(Q))`를 libm `_exp`·`_log` 호출로 컴파일합니다(asm 확인, fmadd 0개). `pow(Q, θ)`와는 84% 입력에서 다릅니다 | `Math.Exp(theta * Math.Log(q[c]))`를 그대로 씀 | tolerance |
| solver.py:155, 230 | 초월 함수 | 두 pow는 libm pow로 가며 지수 특례가 없습니다. n=2라 inv_n=0.5인데, Apple pow(x,0.5)는 sqrt(x)와 0.11~0.15% 입력에서 다릅니다 | `Math.Pow`를 두 번 그대로 부름. `Math.Sqrt`로 바꾸거나 식을 합치지 않음 | tolerance |
| solver.py:289 | 식 변형 | 표면 커널의 k_s는 pow를 한 번만 부르며, 적분 커널 식과 42% 입력에서 다릅니다 | 커널마다 원래 식을 유지 | tolerance |
| solver.py:222, 287 | 계산 순서 | e = U + ((G·R_ref)·Qs)/Q. e ≤ 0 분기는 초월 함수 없이 순차 합으로만 정해집니다 | 같은 왼쪽 결합 식 | exact |
| solver.py:141-143 / geology/model.py:388 | 경계 비교 | `_upper_layer`는 `<=`(경계 위 점은 위층), `layer_index_at`는 `<`(아래층)를 씁니다. NaN은 둘 다 L입니다 | 두 함수를 따로 두고 연산자를 그대로 둠 | exact |
| solver.py:233-245 | 분기 순서 | `need >= rem`이면 z += rem·s, 아니면 z=up을 대입하고 rem −= need. 경계에서 z==up이 정확해야 위층으로 넘어갑니다 | 같은 순서·같은 대입, FMA 금지 | exact |
| solver.py:220, 225, 246 | NaN 선택 | `z if z >= z_floor else z_floor`는 z가 NaN이면 z_floor를 고르지만, `Math.Max`는 NaN을 돌려줍니다 | 삼항 연산자 | exact |
| solver.py:157-160 | 자르기 순서 | scrit로 먼저 자른 뒤 s_min으로 올립니다. `Math.Clamp`는 min > max이면 예외를 냅니다 | if 두 개를 같은 순서로 | exact |
| solver.py:276-291, 320-327 | prange | 표면 커널은 칸별 쓰기만 합니다. `_eps_drop_count`는 정수 축약이고 NaN도 위반으로 셉니다. 스레드 1·2·12개 결과의 비트가 같았습니다(검증) | 표면 커널은 `Parallel.For`, eps 검사는 순차 루프와 부정형 비교 유지 | exact |
| solver.py:525-531 | 건너뛰기 | 바닥 `z_r + rise_min`과 같은 식이라 2회차부터 늘 참입니다. spy로 보면 채움은 1회차에만 돌았습니다 | `HasEpsDrop` 분기를 그대로 | exact |
| solver.py:536-538 | 별칭 | d8가 새 rcv를 주고 `_freeze_kernel`이 rcv와 flips(int32)를 제자리에서 고칩니다 | prev와 rcv를 서로 다른 버퍼로 | exact |
| solver.py:550, 560 | 멈춤 | `np.max(abs)`는 NaN을 퍼뜨립니다. n_changed==0이면 max_dz==0.0이라 stop_dz는 효과가 없습니다(검증) | NaN을 퍼뜨리는 최대값 루프, 조건식은 그대로 | exact |
| solver.py:428-433 | 시드·순서 | 시드는 hash3(seed,7101,0)>>33이고 seed 0이면 1732317295입니다. pts = pos/(16·spacing)에서 역수를 곱하면 1509원소가 다릅니다. z = (z_src + 1e-3·dist) + 10·fbm 순서입니다 | unchecked ulong 시프트, 나눗셈, 같은 괄호 | exact |
| solver.py:341-346, 115-116, 489-496 | 형·변환 | 입력 float32는 float64로 정확히 올립니다(호출 지점은 모두 float64). cast는 float32 최근접 짝수 반올림입니다. `int(cfg...)`는 0 쪽으로 자릅니다 | double[] 입력, `(float)` 캐스트, 정수는 `Math.Truncate` | exact |
| solver.py:552-554, 119-129; fans.py:148-157; warmstart.py:108-111, 120 | JSON 순서 | history·diag·apex·levels의 키 순서가 sort_keys 없이 manifest meta에 들어갑니다 | 순서가 고정된 record와 PyJson | exact |
| solver.py:467, 584; warmstart.py:61, 120 | 시계 | seconds는 perf_counter의 차이입니다 | `Stopwatch`, 비교할 때 가림 | n/a |
| solver.py 전체(초월 함수) | 플랫폼 | numba·numpy·math는 Apple libm과 같습니다(40만 표본). Linux와 Windows에서는 1 ulp씩 다를 수 있습니다 | libm 탐침으로 비교 모드를 고름. TensorPrimitives 금지 | tolerance |
| fans.py:76 | 정렬 동률 | lexsort는 Q 내림차순, 같으면 칸 번호 오름차순입니다(검증). 키가 유일해 안정 정렬이 필요 없습니다 | (−Q, cell) 비교자로 정렬 | exact |
| fans.py:77-86, 132-136 | KD 경계 | d² ≤ r·r이면 포함하고, d²는 arm64 휠에서 FMA 사슬입니다(query 거리로 검증, 단순 합은 10%가 다름). 단일 점 결과는 정렬되지 않지만 집합으로만 씁니다 | `Bpcg.Numerics.KdTree.QueryBallPoint`(FMA, <=) | exact |
| fans.py:21-34 | 초월 함수(구면) | sin과 asin으로 현과 호를 바꿉니다. L0은 후보가 없어 tiny에는 나타나지 않습니다 | `Math.Sin`, `Math.Asin`, `(2.0*R)*...` 순서 | tolerance |
| fans.py:138-139 | 합 순서 | norm은 sqrt((x²+y²)+z²), matmul은 (r0v0+r1v1)+r2v2이며 둘 다 FMA가 없습니다(M ≤ 1e6으로 검증) | 직접 식 | exact |
| fans.py:134-158 | 순차 갱신 | 꼭짓점을 Q 순서로 처리하며 z_new를 제자리에서 갱신합니다. 원뿔 높이는 입력 z[a] 기준이고 `>`는 엄격합니다. 한 칸도 못 올리면 목록에서 뺍니다 | 같은 순차 루프 | exact |
| relief.py:44, 73-74, 82-83 | pow 특례 | numpy 스칼라 지수 2.0은 x·x, 0.5는 sqrt, −1.0은 1/x로 가고 그 밖은 libm pow입니다(검증). θ=0.5나 1.0이면 특례에 걸립니다 | `NpMath.PowScalarExponent(x, e)` | tolerance |
| relief.py:77-87 | 적분 순서 | np.trapezoid가 아닌 수동 32점 루프입니다. total += (0.5·(g_k+g_prev))·du를 칸마다 순차로 쌓습니다 | 칸별 스칼라 루프(흉내로 비트 일치 검증) | tolerance |
| relief.py:42, 67-68, 104-110, 139-153 | NaN·inf | 비교식이 NaN을 걸러 내고, D=0이면 s_diff=inf입니다. 바다 칸은 NaN을 허용합니다. numpy min/max의 동작은 `Math.Min/Max`와 같습니다 | 같은 조건식, 바다 칸 검사 생략 | exact |
| warmstart.py:67-79 | 합·최솟값 | bincount는 원소 순서대로 순차 합을 하고(검증), minimum.at은 NaN을 퍼뜨리며 (+0,−0)→−0입니다 | 칸 번호 순 단일 루프와 `Math.Min` | exact |
| warmstart.py:113-118 | RGI | ys를 원래 식으로 만든 뒤 뒤집습니다. 빠른 경로는 FMA 사슬입니다(FMA 흉내는 0차이, 단순 식은 26% 차이). find_interval의 끝점·외삽 규칙이 있고, values가 읽기 전용이면 다른 경로를 탑니다 | `Bpcg.Numerics.RegularGridInterpolator`(FMA) | exact |
| warmstart.py:24-30, 89-103 | 정수·재귀 | 음수 없는 //와 %입니다. 재귀 조건은 64, 건너뜀 조건은 16이며, seed+7919는 모든 단계에서 같습니다 | int 나눗셈, 같은 재귀 | exact |
| cli.py:118-128 | golden 경로 | 히어로는 다시 읽은 float32 행성으로 만듭니다. 메모리 안 행성으로 만들면 Q가 상대 2.3e-8 다릅니다(검증) | golden을 CLI 경로로 잡음 | n/a |

#### (c) 외부 호출과 대체 방식

| 외부 호출 | 위치 | 확인한 의미 | C# 대체 |
|---|---|---|---|
| numba 커널의 `np.exp`·`np.log`·`**` | solver.py:155, 227, 230, 289 | `llvm.*.f64`가 libm `_exp`·`_log`·`_pow`를 부릅니다. fast-math와 fmadd는 없고 특례도 없습니다 | `Math.Exp/Log/Pow` |
| numpy 초월 함수(`exp`, `log`, `sin`, `arcsin`, `sqrt`) | relief, fans | libm과 비트가 같습니다 | `Math.*` |
| numpy `**`(스칼라 지수) | relief.py:44, 73, 74, 82, 83 | 2.0·0.5·−1.0 특례가 있습니다 | `NpMath.PowScalarExponent` |
| `np.linalg.norm(axis=1)`, `@` | fans.py:138-139 | 순차 합이고 FMA가 없습니다 | 직접 식 |
| `np.lexsort`, `np.flatnonzero` | fans.py:69, 76 | Q 내림차순, 같으면 번호 오름차순입니다 | 비교자 정렬 |
| `cKDTree(data).query_ball_point(x, r)` | fans.py:77, 85, 132, 136 | p=2, eps=0, return_sorted=None이며 FMA 거리를 씁니다 | `Bpcg.Numerics.KdTree` |
| `RegularGridInterpolator(..., bounds_error=False, fill_value=None)` | warmstart.py:117-118 | linear 빠른 경로와 선형 외삽입니다 | `Bpcg.Numerics.RegularGridInterpolator` |
| `np.bincount`, `np.minimum.at`, `np.divmod`, `np.broadcast_to` | warmstart.py:64-79, 89 | 순차 합, NaN을 퍼뜨리는 최솟값입니다 | 루프 |
| bpcg 다른 묶음 | solver, fans.reroute, warmstart | `hydro.fill_epsilon`·`d8_receivers`·`topo_order`·`accumulate`, `core.distance.nearest_source_values`, `core.hashing.hash3`, `core.noise.fbm3`, `core.graph.flat_graph`, `geology.model.layer_index_at`·`LayerColumns` | 해당 C# 포트 |
| `time.perf_counter` | solver, warmstart | 시간 측정입니다 | `Stopwatch` |

#### (d) numba 커널과 prange

| 커널 | 설정 | 반복 | 경쟁·축약 | C# |
|---|---|---|---|---|
| `_upper_layer`, `_law_slope` | njit inline | 순차 | 없음 | AggressiveInlining 메서드 |
| `_receiver_slot_distance` | njit | 순차 | 없음 | private static |
| `_integrate_kernel` | njit | 순차(σ 순서, z_new[r]에 의존) | 없음 | 순차 for |
| `_surface_law_kernel` | njit parallel | prange 1개 | 칸별 쓰기만 | `Parallel.For` 허용 |
| `_freeze_kernel` | njit | 순차 | 정수 카운터 | 순차 |
| `_eps_drop_count` | njit parallel | prange 1개 | 정수 n_bad 축약(순서 무관) | 순차, 또는 스레드별 정수 합 |

numba의 형은 다음과 같습니다. 배열은 float64, int64(rcv, order), int32(nbr, flips), bool이고, 스칼라는 float64, 카운터는 int64입니다. float32 지역 변수, 정수 나눗셈, 음수 색인은 없습니다.

tiny의 네 풀이를 스레드 1·2·12개로 돌렸을 때 결과의 비트가 같았습니다.

#### (e) 의심 버그

1. solver.py:560 — `max_dz < stop_dz_m`은 늘 참입니다. z_new가 rcv에서만 정해지므로 n_changed==0이면 max_dz==0입니다. stop_dz를 1e-300, 0.1, 1e9로 바꿔도 61회로 결과가 같았습니다. stop_dz ≤ 0이면 수렴할 수 없습니다. 출력에는 영향이 없으며, C#은 그대로 옮깁니다.
2. warmstart.py:59, 98 — factor를 검사하지 않습니다. factor=1이면 RecursionError, factor=0이면 ZeroDivisionError가 납니다(실험). C#에서 같은 재귀를 두면 StackOverflow로 프로세스가 죽습니다. 처리 방법은 결정이 필요합니다.
3. warmstart.py:26-30, 112-118 — 한 변이 factor의 배수가 아니면 마지막 블록(최대 2·factor−1 줄)의 실제 중심과 보간 격자 중심이 어긋납니다. 66×70에서 실제 중심은 6700 m인데 격자 중심은 6600 m에 놓입니다. 시작 지형만 달라지며, 현재 프로필은 해당하지 않습니다.
4. docs/pipeline.md 7.2·7.3 — add_fans는 명세와 달리 4개 값을 돌려주고, 사면 기복의 L_h는 min(A*, 칸 면적)을 씁니다. 코드와 docstring은 서로 맞으며, C#은 코드를 따릅니다.

#### (f) golden 대조 계획

golden 자료는 다음 원칙으로 잡습니다.

- **잡는 경로.** tiny 호출 지점은 CLI all과 같은 경로에서 잡습니다. 행성 묶음을 쓰고 `load_planet_state`와 `config_from_manifest`로 다시 읽은 뒤 히어로를 만듭니다.
- **잡는 방법.** 반복별 상태는 `bpcg.landscape.solver`의 `fill_epsilon`, `_has_eps_drop`, `d8_receivers`, `_freeze_kernel`, `_integrate_kernel` 이름을 spy로 바꿔치기해서 잡습니다. 이렇게 해도 결과의 비트가 같음을 확인했습니다.
- **크기.** 반복별 전체 배열은 L0 1차 6.3 MB, 히어로 10.7 MB입니다. 이 자료는 out/golden에만 두고 커밋하지 않습니다.
- **비교 모드.** golden 메타에 OS와 libm 탐침 표를 넣습니다. C#의 Math.*가 탐침 표와 같으면 exact 모드로 비교합니다. 다르면 초월 함수에서 나온 실수만 상대 1e-14로 비교합니다.
- **tolerance 근거.** ±1 ulp 섭동(입력 결정적) 실험에서 z의 관측 최대 상대 차는 7.5e-16이었습니다. 1e-14는 그 약 13배입니다. 정수·bool·셀 번호·순서는 늘 exact입니다.

| 대상 | 입력 | 출력 | 비교 |
|---|---|---|---|
| libm 탐침 표 | exp·log·pow(9개 지수)·sin·asin 각 1e4~1e5점(손) | 결과 비트 | exact, 모드 결정용 |
| `UpperLayer`, `LawSlope` | 경계·NaN·L=0·두께 0 층·scrit<s_min(손) | 층 번호, 경사 | exact |
| `IntegrateKernel` | tiny 4개 풀이의 1·2·마지막 반복(spy), 여러 경계 넘기·need==rem·e≤0·ε 바닥·L=0·n_bad(손) | z_new, n_bad | tolerance |
| `SurfaceLawKernel` | 4개 풀이의 최종 상태, z==bottom·E≤0(손) | slope, k_s, s_crit | slope·s_crit exact, k_s tolerance |
| `FreezeKernel`, `EpsDropCount` | 히어로 64²의 flips ≥ 1 반복(spy), 되돌림 여부·eps 경계·NaN(손) | rcv, flips, 카운트 | exact |
| `InitialSurface` | L0 1차와 warm 16² 최하단 입력 | z0 | exact |
| `Step`(교사 강요) | 반복마다 Python의 반복 전 상태(z, prev, flips) | skip, rcv, flips, n_changed, n_frozen, order, Q, A, Qs, z_new, max_dz | 정수 exact, 실수 tolerance |
| `SolveSteadyState`(자유 실행) | tiny 4곳과 시험 지형 9개(아래) | 결과 전부, history, 반복별 SHA-256 | 정수 exact, 실수 tolerance |
| `Fields`, `Diag` | 히어로 결과 | dtype·값·키 순서 | exact |
| `FindFanApexes`, `AddFans`, `Reroute` | tiny 히어로(후보 7, 꼭짓점 1), L0(후보 0), 산지 앞 100×240, 손 사례(아래) | 꼭짓점, z_new, fan, apex 목록, 물길 | exact(구면 실수만 tolerance) |
| `Relief.*` | tiny L0 호출 2곳, 손 사례(아래) | a_star, river, hill, relief, z_mean | tolerance |
| Warmstart 거친 문제 | tiny 히어로(spy), 66×70, 출구별 z_outlet·NaN, inflow·columns None | blk, U_c, R_c, out_c, zo_c, inflow_c, cols_c | exact |
| `InterpolateFromCoarse` | 거친 res.z와 고운 그래프 | z_init | exact(FMA 필요) |
| `CoarseWarmStart` 전체 | tiny 히어로, 60×64(건너뜀), 256² factor 2(재귀 두 단계), 66×70 | z_init 또는 null, levels | z_init tolerance, levels exact |

- **자유 실행 시험 지형(9개).** island96, 침강 섬 n=1.5, 퇴적 평야 60², 산지 앞 100×240, 2층 띠(padding 있음과 없음), ε 바닥 띠, 구면 n=16, max_iter=3, 흔들기 없는 대칭 64²입니다.
- **fans 손 사례.** Q 동률, 흔들기 없는 25 m 격자에서 정확히 2r과 2r+25 m 떨어진 후보, R이 작은 구면입니다.
- **relief 손 사례.** k_s=0, A* ≤ 1, A* ≥ 칸 면적, 바다 NaN, θ=0.5·1.0, D=0, U ≤ 0입니다.

**흐름 방향 갈림 보고.** 자유 실행 시험은 반복마다 rcv, flips, n_changed, n_frozen, skip, max_dz, 그리고 z의 SHA-256을 golden과 맞대어 봅니다. 처음 갈린 반복 k*에서 멈추고, 갈린 칸마다 다음을 적습니다.

- 칸 번호와 좌표(평면은 j,i, 구면은 f,j,i)
- Python과 C#의 수신 셀, 그리고 지난 수신 셀
- z̃에서 낮은 이웃 경사 상위 2개와 그 상대 차
- 히스테리시스 여유 best/((1+η)·s_prev) − 1
- flips와 max_flips
- 관련 z̃의 ULP 거리

원인은 다섯 가지로 나눕니다.

- (A) D8 동률 근처(상대 차 < 1e-12)
- (B) 히스테리시스 문턱 근처
- (C) 진동 칸 고정의 `zt[p] < zt[c]` 비교 근처
- (D) ε 채움 건너뛰기 판정
- (E) 그 밖

같은 반복에서 Step 교사 강요 시험이 통과하면 원인을 (A)~(C)로 봅니다. 실패하면 이식 오류로 봅니다.

보고서는 `out/golden/report/landscape_solver_divergence.json`에 쓰고, 칸 수·위치·원인을 진행 기록에 옮깁니다. 갈림은 어느 플랫폼에서든 실패로 남기며, 허용 오차를 넓히지 않습니다.

scratch의 원형 실험으로 이 보고 방식을 시험했습니다. 흔들기 없는 대칭 64² 격자에서 반복 2의 칸 542(j=8, i=30)가 갈렸습니다. 두 경사는 2.648528137423855와 2.6485281374238547로, 상대 차가 1.7e-16이었습니다. 최종 상태는 같았습니다. 노드 흔들기 격자에서는 갈림이 없었습니다.

#### (g) 포팅 순서와 위험 메모

1. **선행 모듈.** hydro(fill_epsilon, d8_receivers, topo_order, accumulate)와 core(CellGraph, flat_graph, hash3, fbm3, nearest_source_values)가 먼저 있어야 합니다. geology의 LayerColumns와 layer_index_at도 필요합니다. Numerics에서는 PowScalarExponent, KdTree(FMA), RegularGridInterpolator(FMA)가 필요하고, PyJson도 필요합니다.
2. **relief.py.** 독립적이고 위험이 낮아 먼저 옮깁니다. 대조로 PowScalarExponent와 libm 모드를 같이 확인합니다.
3. **solver 커널.** UpperLayer, LawSlope, IntegrateKernel, SurfaceLawKernel, FreezeKernel, EpsDropCount를 단위별로 대조합니다. 그다음 InitialSurface, Step(교사 강요), SolveSteadyState(자유 실행) 순서로 갑니다.
4. **fans.py.** KdTree에 의존합니다. reroute는 파이프라인이 쓰지 않으므로 마지막에 둡니다.
5. **warmstart.py.** RGI와 재귀가 있습니다. 순서는 거친 문제 만들기, 보간, 전체 대조입니다.
6. **위험.** 가장 큰 위험은 두 가지입니다. 하나는 .NET Math.*가 같은 libm을 부른다는 가정이 미검증이라는 점입니다. 다른 하나는 FMA 금지 규칙과 scipy arm64 휠의 FMA가 충돌한다는 점입니다. 둘 다 단계 1에서 결정하고 확인해야 합니다.
7. **바꾸지 말 것.** exp(θ·log Q), pow 두 번(적분)과 한 번(표면), 나눗셈 pts, `<=`와 `<`의 경계 비교, 삼항 바닥, 순차 꼭짓점 갱신, 32점 수동 적분, bincount 순차 합은 '단순화'하면 비트가 달라집니다(모두 검증). 계산 순서를 바꾸는 최적화는 금지합니다.

### subsurface — caves·groundwater·soil·water (비평 거침)

`src/bpcg/subsurface/`의 5개 파일 852줄(`__init__` 4, `caves` 160, `groundwater` 194, `soil` 138, `water` 356)을 다룹니다. 이 묶음은 4단계(물 → 흙 → 지하수면 → 동굴)를 맡고, `pipeline.run_stages_2_to_4`가 L0(구면)과 히어로(평면)에서 같은 순서로 부릅니다. tiny `all`과 같은 순서(행성 → 묶음 → 히어로)로 돌린 scratch 실행은 `out/tiny_check`의 .npy와 바이트 단위로 같았습니다. 그 실행에서 호출 9개(L0 5개, 히어로 4개)와 커널 호출 11번(커널 5종)을 가로챘습니다. 입력은 float64, int64(`receiver`, `order`), int32(`nbr`), bool, uint8뿐이고 float32는 없습니다. C# 계획과 같은 연산 순서로 짠 스칼라 Python 구현(`math.*` = libm, `Q^0.5`만 `sqrt`)을 분석과 검토에서 따로 짰는데, 둘 다 9개 호출의 모든 출력과 NaN 비트까지 같았습니다(macOS arm64). .NET SDK가 없어 C# 실행 검증은 하지 못했습니다.

#### (a) 대응표

C# 파일은 `csharp/Bpcg/Subsurface/{Water,Groundwater,Soil,Caves}.cs`이고 namespace는 `Bpcg.Subsurface`입니다. 모듈마다 `public static class`를 하나씩 두며, 이 묶음에는 Python 클래스가 없어 정적 클래스와 이름이 겹치지 않습니다. `__init__.py`는 docstring뿐이고 재노출이 없으므로 C# 파일을 만들지 않고, 설명은 `Water.cs` 머리 XML 주석으로 옮깁니다. 입력·반환용 record 5개(`RoutingInput`, `WaterBodiesResult`, `WaterTableDiag`, `SoilResult`, `CaveLevelsResult`)를 새로 둡니다. numba 커널은 커널 단위 대조 시험이 직접 불러야 하므로 `private`가 아니라 `internal`로 두고, `Bpcg` 프로젝트에 `InternalsVisibleTo("Bpcg.Tests")`를 둡니다.

| Python | C# 멤버와 시그니처 | 비고 |
|---|---|---|
| `water.LAKE_MIN_DEPTH_M`, `RIVER_SURFACE_DEPTH_FRACTION`, `WIDTH_EXPONENT`, `DEPTH_EXPONENT`, `SEA_LEVEL_M` | `Water.LakeMinDepthM`(1e-3), `RiverSurfaceDepthFraction`(0.2), `WidthExponent`(0.5), `DepthExponent`(0.4), `SeaLevelM`(0.0), 모두 `public const double` | `RiverSurfaceDepthFraction`은 `Volume.Sample`도 씁니다. 폭 계산은 `WidthExponent` 대신 `Math.Sqrt`를 씁니다 |
| `water.KIND_NONE..KIND_RIVER` | `internal const byte KindNone = 0, KindOcean = 1, KindLake = 2, KindRiver = 3` | 커널 전용 |
| `water._check_graph`, `_as_vector`, `_as_mask` | `internal static int CheckGraph(CellGraph g)`, `double[] AsVector(double[] x, int n, string name)`, `double[] AsVector(double x, int n, string name)`(allow_scalar), `bool[] AsMask(bool[] x, int n, string name)` | 네 파일 공용, NaN·inf 거절 |
| `water._result_array` | 옮기지 않음 | 형 있는 입력(`RoutingInput`, 배열 인자)으로 대체합니다 |
| `water.routing_from_result` | `public static (int[] Receiver, int[] Order, double[] Discharge) RoutingFromResult(RoutingInput r, int n)` + `public sealed record RoutingInput(int[] Receiver, int[]? Order, double[] Discharge)` | `Order`가 null이면 `Bpcg.Hydro.Routing.TopoOrder`. Python은 `order`를 검사하지 않습니다 |
| `water._water_level_kernel` | `internal static void WaterLevelKernel(int[] order, int[] rcv, byte[] kind, double[] h)` | 순차, 제자리 |
| `water._window_max_chunk` | `internal static void WindowMaxChunk(double[] pos, int[] nbr, int nSlots, double[] z, int[] sources, double r2, int lo, int hi, int[] stamp, double[] output)` | 순차 BFS |
| `water._window_max_kernel` | `internal static void WindowMaxKernel(double[] pos, int[] nbr, int nSlots, double[] z, int[] sources, double r2, int nChunks, double[] output)` | `Parallel.For`(묶음 단위) |
| `water._window_chord_sq` | `internal static double WindowChordSq(CellGraph g, double windowM)` | r2 golden 비교용 |
| `water.window_max` | `public static double[] WindowMax(CellGraph g, double[] z, int[] cells, double windowM)` | M = 0이면 빈 배열 |
| `water.valley_depth` | `public static double[] ValleyDepth(CellGraph g, double[] z, double[] waterLevel, bool[] isRiver, double windowM)` | |
| `water.water_bodies` | `public static WaterBodiesResult WaterBodies(CellGraph g, double[] z, RoutingInput result, bool[] isOcean, Config cfg)` + `public sealed record WaterBodiesResult(double[] WaterLevelM, bool[] IsLake, bool[] IsRiver, double[] RiverWidthM, double[] RiverDepthM, double[] ValleyDepthM)` | `ToFields()`는 Python dict 순서를 따릅니다 |
| `groundwater.SHALLOW_DEPTH_M` | `Groundwater.ShallowDepthM`(0.5) | |
| `groundwater._divide_distance_kernel` | `internal static double[] DivideDistanceKernel(int[] src, double[] delta)` | 순차 |
| `groundwater._water_table_kernel` | `internal static void WaterTableKernel(double[] z, double[] hW, bool[] isWater, int[] src, double[] delta, double[] L, double[] recharge, double[] trans, double maxDepth, double[] output)` | `Parallel.For` |
| `groundwater.hydraulic_conductivity` | `public static double[] HydraulicConductivity(byte[] rock, double gravity)` | 12 이상은 거절 |
| `groundwater.water_table` | `public static (double[] ZGw, WaterTableDiag Diag) WaterTable(CellGraph g, double[] z, double[] waterLevel, bool[] isWater, byte[] surfaceRock, double[] slope, double[] runoff, Config cfg, double? gravity = null, bool[]? isOcean = null)` + 스칼라 `double runoff` 겹지정 | `WaterTableDiag`는 키 순서를 고정해 직렬화합니다((b) dict-order) |
| `soil`의 상수 9개 | `Soil.SoilTempCoefPerC`(0.07), `SoilTempRefC`(15.0), `SoilPrecipRefMPerYr`(1.0), `SoilPrecipFactorMax`(2.0), `MinDenudationMPerYr`(1e-7), `AlluviumMaxSlope`(0.02), `AlluviumQLowM3PerYr`(1e6), `AlluviumQHighM3PerYr`(1e9), `AlluviumSlopeScale`(0.01) | `public const double` |
| `soil.smoothstep01` | `public static double Smoothstep01(double x)`, `double[] Smoothstep01(double[] x)` | volume의 같은 이름 함수(dtype 유지)와 별개 |
| `soil.soil_production_max` | `public static double[] SoilProductionMax(double[] temperature, double[] precip, Config cfg)` | 브로드캐스트 대신 같은 길이 |
| `soil.soil_and_alluvium` | `public static SoilResult SoilAndAlluvium(double[] z, double[] slope, double[] uplift, double[] temperature, double[] precip, double[] discharge, double[] sedimentFlux, double[]? fanRaise, byte[] surfaceRock, Config cfg, bool[]? isOcean = null)` + `public sealed record SoilResult(double[] SoilThicknessM, double[] AlluviumM, bool[] BareRock)` | `z`와 `surfaceRock`은 길이 검사에만 씁니다 |
| `caves.MAX_LEVELS`, `level_field_name` | `Caves.MaxLevels`(8), `public static string LevelFieldName(int k)` | 불변 문화권. FIELDS에 층이 2개뿐이라 실효 상한은 2입니다 |
| `caves._entrance_kernel` | `internal static void EntranceKernel(int[] nbr, int nSlots, double[] z, double[] zk, bool[] soluble, int m, double twoR, double band, double[] levelOut, byte[] entrance)` | `Parallel.For` |
| `caves.cave_levels` | `public static CaveLevelsResult CaveLevels(CellGraph g, double[] z, double[] zGw, double[] valleyDepth, LayerColumns columns, Config cfg)` + `public sealed record CaveLevelsResult(double[][] LevelM, byte[] Entrance)` | `ToFields()`는 `cave_level_<k>_m` 다음 `cave_entrance` 순서이고, pipeline diag의 `caves` 키 순서가 여기에 기댑니다 |

(N, M) 배열(`zk`, `level_out`, `soluble`, `pos` (N,3), `nbr` (N,8), `bottom` (N,L), `rock` (N,L+1))은 모두 행 우선 1차원 배열로 둡니다. 셀 번호 배열(`receiver`, `order`, `src`, `cells`, `stamp`)은 numpy에서 int64이지만 C#에서는 `int[]`로 제안합니다(N ≤ 5000² = 25M).

#### (b) 수치 함정 표

공통 규칙(pairwise 합, libm 차이, 캐스트)은 공용 장을 따르고, 여기서는 발생 위치와 방안만 적습니다.

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| water.py:335 | power-fastpath | numpy v2.5.3 power 루프는 지수가 stride-0 스칼라일 때 −1, 0, 0.5, 1, 2를 특례로 처리하므로 `q_s**0.5`는 `sqrt`가 됩니다(원문 확인). macOS libm `pow(x, 0.5)`는 2M 표본 중 2,605개가 `sqrt`와 다르고, tiny L0 강 2칸의 폭이 달라집니다 | `kW * Math.Sqrt(qS)`로 씁니다. `Math.Pow(qS, 0.5)`는 쓰지 않습니다 | exact |
| water.py:336 | libm-pow | `q_s**0.4`는 특례가 없어 `npy_pow`이고, arm64에서는 libm입니다(2M 표본 차이 0). `Exp(0.4·Log q)`로 바꾸면 L0 강 1,246/2,032칸, 히어로 14/24칸이 달라집니다 | `kD * Math.Pow(qS, 0.4)` | tolerance(macOS 비트 일치 기대, 그 밖 ≤ 2 ulp) |
| water.py:333-334 | division-order | `Q / SPY`를 역수 곱으로 바꾸면 L0 강 1,190/2,032칸, 히어로 10/24칸의 `q_s`가 달라집니다. 강 문턱은 `q_s >= q_min`입니다 | `Q[i] / 3.15576e7`로 나눈 뒤 같은 비교를 씁니다 | exact |
| water.py:332 | threshold-rewrite | `z_hat - z > 1e-3`을 `z_hat > z + 1e-3`으로 바꾸면 문턱 ±3 ulp 표본 2M 중 약 11만 개의 판정이 갈립니다 | 뺄셈 뒤 비교하는 형태를 그대로 씁니다 | exact |
| water.py:345 | fma | 강 수면 `z - 0.2·D`는 곱한 뒤 뺍니다 | `z[i] - 0.2 * depth[i]`, FusedMultiplyAdd 금지 | exact |
| water.py:282, 342; caves.py:62; groundwater.py:49; core/distance.py `nearest_source_values` | nan-bits | 출력 NaN은 모두 상수 `np.nan`(0x7FF8…)이고 float32 묶음에는 0x7FC00000으로 저장됩니다(tiny 묶음에서 확인). .NET `double.NaN`은 0xFFF8…이라 float로 바꾸면 0xFFC00000입니다. 이 묶음은 연산으로 NaN을 만들지 않으므로 상수 하나로 충분합니다 | `PyMath.NaN = BitConverter.Int64BitsToDouble(0x7FF8000000000000)`(제안)로 채웁니다 | exact(NaN 비트 포함) |
| water.py:118-132 | sequential-order | 1차(k = n−1..0, 상류부터)는 강·호수 c → 강 r에서 `h[c] < h[r]`이면 `h[r] = h[c]`, 2차(k = 0..n−1)는 강 c → 물 r에서 `h[r] > h[c]`이면 `h[c] = h[r]`입니다. 결과는 rcv·kind·h0만의 함수여서 rcv와 맞는 위상 순서라면 어느 것을 써도 같습니다. 바뀌면 안 되는 것은 두 훑기의 방향이고, 엄격함(< 대 <=)은 ±0에만 영향이 있습니다 | 같은 방향의 순차 for 두 개로 씁니다. `order`는 받은 값을 쓰고, 없을 때 `TopoOrder`로 만들어도 결과가 같습니다 | exact |
| water.py:154-189 | traversal-order | 창 최댓값은 출발 칸에서 반경 안 칸만 거쳐 닿는 연결 성분의 최댓값이라 BFS 순서, 큐 구현, 묶음 분할과 무관합니다. 반경 밖 칸에 stamp를 찍어도 결과는 같습니다. 계약이 그래프로 제한한 원판이라 KD-tree 공 질의와 다를 수 있습니다 | BFS를 그대로 옮기고 묶음별 `int[] stamp`(−1)를 둡니다. `dx*dx + dy*dy + dw*dw > r2`를 같은 결합과 엄격한 >로 비교합니다. KdTree는 쓰지 않습니다 | exact |
| water.py:211 | int-overflow | 묶음 경계 `ch * m // n_chunks`는 int64이고 음수가 없습니다. int로 곱하면 m = 25M에서 묶음 86개부터 넘칩니다 | `(int)((long)ch * m / nChunks)` | n/a |
| water.py:219-223 | libm-sin | 구면 r2 = (2R·sin(0.5w/R))²이고 tiny L0 값은 0x1.12a87feab6ba4p+21입니다. L0 칸(288 km)이 창(1.5 km)보다 커서 r2 끝자리가 창 소속을 바꾸지 못합니다(L0 강 2,032칸 모두 rim = 자기 z). 평면은 w·w이고, w ≥ πR이면 +∞입니다 | 같은 결합으로 `Math.Sin`을 부르고, `windowM >= Math.PI * R`이면 `double.PositiveInfinity`를 씁니다 | exact |
| water.py:246 | thread-count | `n_chunks = min(get_num_threads(), M)`는 결과에 영향이 없습니다 | 묶음 수는 아무 값이나 씁니다 | n/a |
| water.py:279-288 | mask-order | `flatnonzero`는 오름차순이고 `vd = np.maximum(rim - h, 0)`입니다. 강이 없으면 전부 NaN이고, 강과 그래프로 이어지지 않은 칸도 `nearest_source_values`가 NaN으로 채웁니다 | 오름차순 루프와 `Math.Max`를 쓰고, `NearestSourceValues`의 채움 값도 `PyMath.NaN`으로 맞춥니다 | exact |
| groundwater.py:99-100 | libm-pow | `10.0 ** LOG10_PERM[r]`는 밑이 스칼라라 특례 없이 원소마다 libm `pow`를 부릅니다. 암석 12개의 지수 중 서로 다른 값은 6개입니다. `((k·1000)·g/1e-3)·SPY`를 `k·(ρg/μ·SPY)`나 `k·1e6·g·SPY`로 묶으면 12개 중 9개가 달라집니다 | 표 `Kh0[r] = Math.Pow(10.0, Rocks.Log10Perm[r])`를 만들고 곱은 왼쪽 결합 그대로 둡니다. 6값 비트를 고정하는 단위 시험을 둡니다 | exact(macOS). 다른 OS는 단위 시험이 먼저 드러냅니다 |
| groundwater.py:160-161 | assoc-order | `trans = k_h * (alpha / (1 + beta·S))`는 두께를 먼저 나눈 뒤 곱합니다. `k_h·alpha/(1 + beta·S)`로 쓰면 tiny 히어로 4,096칸 중 1,218칸의 T와 자유 칸 206개 중 9칸의 z_gw가 달라집니다 | `double b = alpha / (1.0 + beta * S[i]); trans[i] = kh[i] * b;`, `recharge[i] = fR * runoff[i]` | exact |
| groundwater.py:77 | fma | `H = R·d·(2L − d)/(2T)`는 왼쪽 결합입니다 | 같은 식을 쓰고 FusedMultiplyAdd는 쓰지 않습니다 | exact |
| groundwater.py:78 | builtin-max | numba의 `max(H, 0.0)`은 Python 규칙입니다(max(−0, 0) = −0, max(NaN, 0) = NaN, max(0, NaN) = 0). H는 실제로 0 이상이지만 규칙이 `Math.Max`와 다릅니다 | `PyMath.PyMax(a, b) = b > a ? b : a`(신설 제안) | exact |
| groundwater.py:68-83 | clip-order | 물 칸 → src < 0 → z로 자르기 → z − max_depth로 자르기 순서가 결과를 정합니다 | 같은 if 순서를 따릅니다 | exact |
| groundwater.py:41-49 | reduction | 출발 칸별 최대 δ를 0에서 시작해 엄격한 >로 갱신하고 s < 0은 건너뜁니다 | 순차 루프로 씁니다(np.maximum.at 같은 병렬 축약 금지) | exact |
| groundwater.py:164 | libm-atan2 | 평면 δ는 sqrt라 정확하고 구면 δ는 R·atan2(core, libm)입니다. tiny L0은 모든 칸이 물이라 이 경로를 지나지 않습니다 | `Core.Distance.NearestSource` 결과를 그대로 씁니다 | tolerance(구면, macOS 밖) |
| groundwater.py:172-191 | pairwise-sum | `area[mask].sum()`은 압축한 배열(오름차순)의 pairwise 합입니다. tiny에서는 드러나지 않습니다(L0 마른 칸 0 → null, 비율 S/S = 1.0, 히어로 면적 균일) | 오름차순 압축 배열에 `NpReduce.Sum`을 쓰고, 구면 손 사례((f))로 고정합니다 | exact |
| groundwater.py:177 | median | `np.median`은 짝수 개면 가운데 두 값을 np.mean으로 `(a + b) / 2` 합니다 | `NpStats.Median` | exact |
| groundwater.py:178-181 | mean-bool | bool 평균은 개수 / 길이를 한 번 반올림한 값입니다 | `(double)count / n` | exact |
| groundwater.py:131, 174-193 | dict-order | diag 키 순서가 manifest `meta.diag.stages.groundwater`의 JSON 순서입니다. `shallow_fraction_with_water`는 None으로 자리를 잡고, NaN과 None은 모두 null이며, `seconds`는 시계 값입니다 | 순서 있는 직렬화를 쓰고 `seconds`는 비교에서 뺍니다 | exact |
| caves.py:149 | assoc-order | `gw + k*f_k*vd`는 스칼라 `k*f_k`를 먼저 곱합니다(지금은 k = 1뿐) | `gw[i] + k * fK * vd[i]` | exact |
| caves.py:150-151 | nan-compare | `rock_at(NaN)`은 기반암입니다(`bottom < NaN`이 모두 거짓). 바닥과 같은 z는 아래층이고, 그 뒤 `& isfinite(zk)`로 지웁니다 | IEEE 비교를 그대로 쓰고 isfinite 마스크를 유지합니다 | exact |
| caves.py:59-93 | prange | 행 c와 `entrance[c]`만 쓰고 `bits`는 지역 변수라 축약이 없습니다. int64 `bits`를 uint8로 저장합니다 | `Parallel.For`, `(byte)bits`. 이웃은 `v >= 0`을 먼저 확인합니다(`&&` 단락) | exact |
| caves.py:66-88 | dead-branch | 기본값은 band = τ + r = 6 = 2r라 `in_band and covered`(docstring의 '자기 자신' 경우)에 닿을 수 없습니다. 벽 규칙은 tiny·협곡 3종에서 0번이고 시험 섬에서만 켜집니다(11번, recharge 0.05 변형 87번). NaN 이웃 경우는 어느 시험에도 없습니다 | 그대로 옮기고 '계단'·섬 사례로 시험합니다 | exact |
| soil.py:49 | libm-exp | `np.exp`(float64)는 arm64에서 libm입니다(1.5M 표본 차이 0) | `Math.Min(cap, pMax * Math.Exp(0.07 * (T[i] - 15.0)) * wet)` | tolerance |
| soil.py:122, 130-132 | libm-log | `np.where`는 모든 칸의 log를 계산하고 −inf는 버립니다. `np.log(1e6)` = 13.815510557964274이지만 `6·ln 10` = …275라 손 상수화하면 값이 달라집니다 | `P0 > E`인 칸에서만 `h0 * Math.Log(P0 / E)`를 계산하고, `Math.Log(1e6)`·`Math.Log(1e9)`는 식으로 둡니다 | tolerance |
| soil.py:27, 123 | clip-sign | numpy 2.5.3 `np.clip`은 −0과 NaN을 그대로 두고, .NET 10 `Math.Clamp`도 같습니다 | `Math.Clamp`(cap ≥ 0 검사가 먼저라 예외가 나지 않음) | exact |
| soil.py:47-49, 87, 120; groundwater.py:143 | maxmin-sign | arm64 `np.maximum`은 ±0 중 +0, `np.minimum`은 −0을 고르고 NaN을 전파합니다. .NET 10 `Math.Max`·`Math.Min`(IEEE 754:2019)과 같습니다 | `Math.Max`, `Math.Min` | exact |
| soil.py:129 | assoc-order | `(g·R_ref)·Qs/q > U`이고, L0은 cfg_l0 때문에 g = 0입니다 | `g * rRef * Qs[i] / qSafe[i] > U[i]` | exact |
| soil.py:28, 133 | smoothstep | `t*t*(3 − 2t)`는 왼쪽 결합이고 `S / 0.01`은 나눗셈입니다 | `Soil.Smoothstep01`을 따로 두고 `* 100`으로 바꾸지 않습니다 | exact |
| soil.py:49, 122, 130-131; water.py:336; groundwater.py:99 | golden-platform | numpy v2.5.3은 x86-64 AVX512F에서 float64 exp·log를 자체 SIMD로, AVX512_SKX + SVML에서 pow를 SVML로 계산합니다. arm64(ASIMD)만 libm으로 떨어집니다 | golden은 macOS arm64에서만 만들고 CPU 기능 목록을 golden 메타에 적습니다 | n/a |
| 입력·설정 검사 전부 | validation | 검사는 `not (a >= 0 and …)` 꼴이라 NaN을 거절합니다. 강이 없으면 `valley_depth`가 일찍 돌아가 `window_m` 검사를 건너뜁니다. 오류 문구의 f-string은 Python repr(`1e-07`, `nan`, `(4096,)`, 목록 repr)입니다 | `!(a >= 0.0 && …)` 꼴과 검사 순서를 유지합니다. 문구 비교 여부는 열린 질문입니다 | n/a |

#### (c) 외부 호출과 대체 방식

| Python 호출 | 쓰는 곳 | C# 대체 |
|---|---|---|
| `ndarray ** 0.5`(스칼라 지수) | water.py:335 | `Math.Sqrt`(numpy 특례와 같음) |
| `ndarray ** 0.4`, `10.0 ** ndarray` | water.py:336, groundwater.py:99 | `Math.Pow`(macOS는 같은 libm) |
| `np.exp`, `np.log` | soil.py:49, 122, 130-131 | `Math.Exp`, `Math.Log` |
| `math.sin`, `math.pi`, `math.inf`, `math.isfinite` | water.py:219-221, 236 | `Math.Sin`, `Math.PI`, `double.PositiveInfinity`, `double.IsFinite` |
| `ndarray.sum()`(실수) | groundwater.py:172-191 | `Bpcg.Numerics.NpReduce.Sum`(pairwise) |
| `bool.sum()`, `np.mean(bool)` | groundwater.py:178-182 | 정수 개수, 개수 / 길이 |
| `np.median` | groundwater.py:177 | `Bpcg.Numerics.NpStats.Median` |
| `np.maximum`, `np.minimum`, `np.clip` | water.py:286, groundwater.py:143, soil.py | `Math.Max`, `Math.Min`, `Math.Clamp` |
| numba 안의 내장 `max` | groundwater.py:78 | `Bpcg.Numerics.PyMath.PyMax`(신설 제안) |
| `numba.get_num_threads` | water.py:246 | 아무 묶음 수(결과 무관) |
| `time.perf_counter` | groundwater.py:131, 193 | `Stopwatch`(비교 제외) |
| `core.distance.nearest_source`, `nearest_source_values` | groundwater.py:164, water.py:287 | `Bpcg.Core.Distance.NearestSource`, `NearestSourceValues`. KdTree로 바꾸지 않습니다. 힙 순서와 '거리가 같으면 작은 출발 칸 번호' 규칙이 vd, h_d, L을 정합니다 |
| `hydro.depressions.fill_depressions`, `hydro.routing.topo_order` | water.py:330, 101 | `Bpcg.Hydro.Depressions.FillDepressions`, `Bpcg.Hydro.Routing.TopoOrder` |
| `geology.model.rock_at`, `LayerColumns`, `geology.rocks.SOLUBLE`·`LOG10_PERM`·`N_ROCKS` | caves.py:150-151, groundwater.py:94-99 | `Bpcg.Geology.Model.RockAt`((N, M) 행 우선), `LayerColumns`, `Bpcg.Geology.Rocks` 표 |
| `core.constants.gravity`·`RHO_WATER`·`MU_WATER`·`SECONDS_PER_YEAR`, `core.fields.FIELDS`, `core.graph.CellGraph` | groundwater.py:100, 158; water.py:333; caves.py:140 | `Bpcg.Core.Constants`, `Bpcg.Core.Fields`, `CellGraph`(멤버 이름은 core가 정함) |

scipy, scikit-image, trimesh 호출은 없습니다.

#### (d) numba 커널과 prange

| 커널 | 형태 | 경쟁·축약 | C# |
|---|---|---|---|
| `water._water_level_kernel` | `@njit`, 순차 두 번 훑기 | 제자리 갱신이라 훑기 방향이 결과를 정합니다. 위상 순서끼리는 결과가 같습니다 | 순차 for 두 개 |
| `water._window_max_chunk` | `@njit`, 순차 BFS | 묶음 안에서 stamp를 공유합니다(출발 번호 k로 표시) | 순차 |
| `water._window_max_kernel` | `@njit(parallel=True)`, `prange(n_chunks)` | 묶음마다 stamp와 큐가 따로이고 `out[k]`가 겹치지 않아 축약이 없습니다. n_chunks 1·2·3·5·12 결과가 같습니다 | `Parallel.For(0, nChunks)` + 묶음별 `int[] stamp` |
| `groundwater._divide_distance_kernel` | `@njit`, 순차 | `lmax[s]` 최댓값이라 순서와 무관합니다 | 순차 |
| `groundwater._water_table_kernel` | `@njit(parallel=True)`, `prange(N)` | 칸별 쓰기뿐이고 `h_w[s]`는 읽기만 합니다 | `Parallel.For(0, n)` |
| `caves._entrance_kernel` | `@njit(parallel=True)`, `prange(N)` | 행 c와 `entrance[c]`만 쓰고 이웃은 읽기만 합니다 | `Parallel.For(0, n)` |

- 6개 커널을 캐시 없이 새로 컴파일해 asm을 보니 fmadd·fmsub가 0개였습니다. `2.0 * x`는 `x + x`로 나오지만 값은 같습니다. RyuJIT도 곱셈과 덧셈을 합치지 않으므로 같은 결합으로 쓰면 비트가 같습니다.
- 커널 안 형은 다음과 같습니다. `bits`는 int64이고 uint8로 저장하며(k < 8), `nbr` 원소는 int32, `src`·`order`·`stamp`·`queue`는 int64, 실수는 모두 float64입니다. float32 지역 변수는 없습니다.
- numba `math.atan2`·`math.sin`은 libm을 부르고, CPython `math.*`와 50만 표본씩 비교해 차이가 0이었습니다(core 거리와 r2의 근거).

#### (e) 의심 버그

1. `groundwater.py:179-181`의 `clipped_max_depth_fraction`은 `depth = z − (z − max_depth)`가 반올림으로 `max_depth`보다 작아진 칸을 세지 못합니다. max_depth 300에서 z ≥ −212 m인 칸은 늘 정확히 300으로 돌아오고, z < −212 m인 마른 칸에서만 3~19%가 빠집니다. tiny에는 그런 칸이 없습니다. 같은 diag 안에서 `shallow_fraction`은 면적 가중이고 `clipped_*`는 칸 수 비율이라는 점도 다릅니다. 진단값에만 영향이 있고, C#은 같은 산술로 같은 값을 냅니다.
2. 물 칸의 동굴 0층 문제입니다. 물 칸은 z_gw = h_w이므로 0층이 수면 높이가 됩니다. 호수 칸의 0층은 바닥보다 최대 4.65 m 위의 물속에 놓이고, 강 칸은 z − 0.2·D라 거의 모두 띠 안에 듭니다. 그래서 덮개 칸과 이웃이면 입구 비트가 켜집니다. tiny 히어로 입구 213칸 가운데 호수가 35칸(호수 42칸 중), 강이 24칸(강 24칸 전부)입니다. 구조상 해안의 얕은 바다 칸(0층 = 해수면 0)에서도 같은 일이 생길 수 있습니다(tiny 0칸). pipeline.md 14절의 '입구가 너무 많다', '강가에서 동굴이 지표를 뚫는다'와 같은 원인으로 보입니다.
3. pipeline.md 8.4와 caves.py:69-88이 다릅니다. 명세는 지표 띠 안의 녹는 칸이면 입구 비트를 켠다고 쓰지만, 코드는 덮개 칸(자신 또는 이웃)과 맞닿을 때만 켜고 벽 규칙을 더합니다. tiny 히어로에서는 띠 안의 녹는 칸 288칸이 비트 없이 NaN입니다. 포팅은 코드를 따릅니다.
4. pipeline.md 8.1과 water.py가 다릅니다. 명세의 수면 규칙은 '상류부터 min' 하나뿐이고 받는 칸의 종류를 제한하지 않습니다. 코드는 1차를 강이 받는 경우로 제한하고(호수·바다는 낮추지 않음), 2차(하류부터 max, 배수 효과)를 더합니다. 강에서 호수 칸을 빼는 규칙과 골짜기 깊이의 0 하한도 명세에 없습니다. tests/test_subsurface.py:146-167이 코드 동작을 고정합니다.
5. 의도 확인이 필요합니다(`soil.py:129` + `pipeline.l0_config`). L0은 솔버 진동을 막으려고 `landscape.deposition_g`를 `deposition_g_l0` = 0으로 바꾸는데, 흙도 같은 키를 읽어 퇴적 지배 판정이 `0 > U`가 됩니다. 그래서 L0 충적층은 침강 칸에만 생깁니다(tiny L0 충적층 0칸).
6. pipeline.md 8.2와 soil.py:137이 다릅니다. 명세는 '경사가 bare_slope보다 가파르면 0이고 bare_rock'만 적지만, 코드는 흙과 충적층이 모두 0인 육지 칸을 맨 암반으로 둡니다. 그래서 P₀ ≤ E인 완만한 칸도 맨 암반입니다. tiny 히어로 맨 암반 2,238칸 중 2,091칸, L0 127칸 전부가 이 경우입니다. 모듈 docstring은 코드와 같고, 포팅은 코드를 따릅니다.
7. 검사 빈틈입니다(water.py:280-285, 100-101). `valley_depth`는 강이 없으면 `window_max`에 가기 전에 돌아가 음수·NaN `window_m`을 오류 없이 받고, `water_bodies`의 `caves.valley_window_m`도 같습니다. `routing_from_result`는 `order`가 rcv와 맞는 위상 순서인지 검사하지 않습니다. pipeline 입력에서는 결과에 영향이 없고, C#은 같은 동작을 옮긴 뒤 진행 기록에만 적습니다.

#### (f) golden 대조 계획

`export_golden.py`는 `bpcg.pipeline`의 다섯 이름(`water_bodies`, `valley_depth`, `soil_and_alluvium`, `water_table`, `cave_levels`)과 커널 전역 이름 다섯 개를 기록용 감싸개로 잠시 바꾼 뒤 tiny `generate_planet`·`generate_hero`를 돌려 인자 복사본과 결과를 저장합니다. scratch 실험에서 호출 9개와 커널 호출 11번이 모두 잡혔습니다. L0의 `water_bodies`·`valley_depth`는 `PlanetState.fields`(메모리의 float64)로 다시 불러도 결과가 같았지만, `fan_raise`(솔버 고도 필요)와 `order`는 상태에 남지 않아 흙 호출은 기록해야만 재현됩니다. `_window_max_chunk`는 njit 안에서 불려 잡을 수 없으므로 `_window_max_kernel` 단위로 비교합니다. 그래프는 단계마다 한 번만 저장하고, 실효 설정(특히 L0의 `landscape.deposition_g = 0.0`)과 CPU 기능 목록을 사례마다 JSON으로 남깁니다. golden은 macOS arm64에서만 만듭니다.

tiny 입력이 지나지 않는 분기는 다음과 같아 손 사례가 반드시 필요합니다.

- 물: 1차 훑기 0번, 2차 훑기는 L0 1칸뿐입니다. 가장 얕은 호수 칸이 0.20 m이고 강 문턱 여유가 61% 이상이라 두 문턱 근처를 지나지 않습니다.
- 지하수: lo로 자른 칸 0, src < 0 칸 0, 구면 마른 칸 0(L0은 모두 물)이고, pairwise 합과 median이 출력에 드러나지 않습니다.
- 흙: 퇴적 지배 칸이 L0·히어로 모두 0이고(히어로 충적층 4칸은 선상지 올림뿐), 흙 상한, P₀ 상한, Q = 0 칸도 0입니다.
- 동굴: 1층 값이 있는 칸이 0입니다(히어로의 1층 녹는 칸 52개는 덮개·입구가 아님). 벽 규칙, band&covered, NaN 이웃 규칙도 0번입니다.

| 대상 | 입력(출처) | 출력 | 크기 | 비교 |
|---|---|---|---|---|
| `Water.WaterBodies` | tiny 히어로·L0 호출 가로채기 | 필드 6개 | 0.5 / 0.76 MB | bool과 폭은 정확합니다. 깊이·수면·골짜기 깊이는 tolerance이고 macOS에서는 비트 일치가 기대됩니다(depth ≤ 2 ulp, 수면·골짜기 절대 1e-9 m) |
| `Water.WaterBodies` 손 사례 | 띠 (a) z = [10, 9, 8, 5, 8.5, 0.5, 0.3, −10](2차 3번), (b) z = [10, 6, 6.0005, 3, −10](1차 1번), order 없음, Q = 1000 m³/s(test_subsurface.py:134-167). 호수 문턱 띠(1e-3 ± 1~3 ulp), 유량 문턱 띠(Q = q_min·SPY·(1 ± k·2^−52), k = 0..4) | 필드 6개 | < 10 KB | 위와 같음 |
| `Water.WaterLevelKernel` | 위 호출의 커널 인자(order, rcv, kind, 호출 전 h). 같은 입력에 `TopoOrder(rcv)`와 다른 위상 순서를 넣은 변형 | 호출 뒤 h | < 0.2 MB | 정확, 세 순서의 결과가 같아야 합니다 |
| `Water.WindowMax` | 40×40 dx 50 격자·창 333/0 m(test_subsurface.py:238-247), tiny L0 창 1500 m와 3e7 m(r2 = +∞), 히어로 강 24칸, M = 0 | rim, r2 | < 0.3 MB | 정확, 묶음 수 1과 8로 두 번 |
| `Water.ValleyDepth` | L0 pipeline 호출(z + relief_m), 8×8 강 없음, 끊긴 그래프(`flat_graph(4, 8, 100)`에서 i ≤ 3과 i ≥ 4 사이 이웃을 −1로 끊음, 강 i = 1, z = 10 + 0.5·c, h = z − 0.3, 창 250 → 오른쪽 16칸 NaN) | (N,) | 0.5 MB / < 2 KB | 정확(NaN 위치와 비트 포함) |
| `Groundwater.HydraulicConductivity` | 암석 0..11 × g {설정 g = 9.820852275618668, 9.81} | 24값 | < 1 KB | 정확(pow 6값 고정) |
| `Groundwater.WaterTable` | tiny 히어로·L0 가로채기. V 골짜기 20×81 dx 25(test_subsurface.py:288-336)의 석회암·recharge 0.5·g 9.81·스칼라 유출, 충적층·recharge 0.01·max_depth 2(마른 1,600칸 모두 lo), 물 없음(src = −1), 셰일(지표로 자름), is_ocean 없음(diag null) | z_gw, diag(seconds 제외) | 0.44 / 0.66 / < 0.2 MB | 정확 |
| `Groundwater.WaterTable` 구면 손 사례 | `sphere_graph(8, 6.371e6)`(384칸), 물 20칸, z = 100 + 400·U, 물 칸 h = z − 1, 암석 0..11, slope 0.2·U, 유출 0.3, `recharge_fraction` 1e-11(마른 364칸 중 자유 181, z 자르기 174, lo 자르기 9, 중앙값은 서로 다른 두 값의 평균)과 1e-9(면적 비율 pairwise 0.6082966403476013, 순차 합이면 …012). 난수 배열은 저장합니다 | z_gw, diag | < 50 KB | 구면 δ(atan2) 때문에 tolerance, macOS에서는 비트 일치 기대. diag는 정확 |
| `Groundwater.DivideDistanceKernel`, `WaterTableKernel` | 위 호출의 커널 인자 | L, z_gw | < 0.3 MB | 정확, WaterTableKernel은 병렬 정도 1과 8 |
| `Caves.CaveLevels` | tiny 히어로·L0 가로채기. 협곡 3종(test_subsurface.py:406-457). 시험 섬 64² 두 설정(벽 규칙 11·87번, 1층 14·105칸). '계단'과 `caves.levels` 1(0층만 출력)·3(FIELDS 오류) 변형 | 층, 입구 비트 | 0.52 / 0.79 MB / < 0.6 MB | 정확 |
| '계단' 정의 | `flat_graph(6, 12, 25)`, c = j·12 + i. z는 3 ≤ i ≤ 5에서 200, (j ≤ 2, i = 5)는 130, 나머지 100. z_gw = 120, vd는 i < 3에서 NaN·그 밖 40, 템플릿 0 기둥(깎인 두께 450), `caves.entrance_tolerance_m` = 10(band 13 > 2r 6) | (칸, 층) 단위로 band&covered 3, 띠·덮개 이웃 3, 벽 규칙 18(그중 NaN 이웃 6), 비트 3인 칸 12 | < 20 KB | 정확 |
| `Caves.EntranceKernel` | 히어로·섬·계단의 커널 인자(nbr, z, zk, soluble, two_r, band) | level_out, entrance | < 0.5 MB | 정확, 병렬 정도 1과 8 |
| `Soil.SoilAndAlluvium` | tiny 히어로·L0 가로채기, 손 9칸(test_subsurface.py:461-494)과 바다 판, 경계 칸(Q = 0, P = 0, U < 0, S = bare_slope, S = 0.02, slope < 0, T = 60), 퇴적 내부 칸(deposition_g 1, U = 1e-4, Q ∈ {3e6, 3e7, 3e8}, Qs = 1e-3·Q, S ∈ {0, 0.005, 0.0199}), is_ocean·fan_raise 없음 변형 | 필드 3개 | < 10 KB / 0.34 / 0.51 MB | bool은 정확, 실수는 tolerance(exp·log, macOS 비트 일치 기대, 그 밖 상대 1e-13) |
| `Soil.SoilProductionMax`, `Smoothstep01` | T ∈ [−40, 60] 0.25 간격 × P ∈ {−1, 0, 0.5, 1, 1.999, 2, 3}, x ∈ {−1, −0.0, 0, 0.1, 0.5, 0.999999, 1, 2, NaN} | (M,), (9,) | < 20 KB | exp는 tolerance, smoothstep은 정확(−0 → +0, NaN → NaN) |
| `Water.RoutingFromResult`, `Caves.LevelFieldName`, 상수 | 띠 사례(order 없음), tiny 호출(order 있음), k = 0..7, 상수 표 | 배열·문자열·값 | < 0.1 MB | 정확 |
| 끝에서 끝(단계 3) | C# tiny `all` | water·subsurface 그룹 float32 .npy | 묶음 일부 | float32 기준. 문턱(rock_at 경계, 띠 판정, 강 문턱)에서 갈린 칸은 수·위치·원인을 진행 기록에 적습니다 |

#### (g) 포팅 순서와 위험 메모

- 먼저 있어야 할 것은 core(`CellGraph`, `Distance.NearestSource`·`NearestSourceValues`, `Constants`, `Fields`, `Config`), hydro(`FillDepressions`, `TopoOrder`), geology(`Rocks`, `Model.RockAt`, `LayerColumns`), Numerics(`NpReduce.Sum`, `NpStats.Median`, `PyMath.PyMax`, `PyMath.NaN`), IO(`PyJson`, diag 직렬화)입니다.
- 순서는 Water 도움 함수(CheckGraph·AsVector·AsMask) → Soil(원소별, 가장 쉬움) → Water(WindowMax → ValleyDepth → WaterLevelKernel → WaterBodies) → Groundwater(HydraulicConductivity → 커널 2개 → WaterTable·diag) → Caves입니다.
- 위험도는 water와 groundwater가 중간입니다. 두 번 훑기의 방향, BFS 창, sqrt 특례, libm pow·atan2, T의 결합 순서, diag의 pairwise 합·median 때문입니다. soil과 caves는 원소별 계산과 비교뿐이라 낮지만, tiny가 퇴적·1층·band&covered를 지나지 않으므로 손 사례 없이는 검증되지 않습니다.
- libm에 기대는 값은 Q^0.4, 10^x(6값), exp, log, 구면 sin(r2), core의 atan2(δ)뿐입니다. 다른 OS에서는 이 값 때문에 z_gw와 동굴 층이 문턱(rock_at 경계, 띠 판정)을 넘나들 수 있습니다.
- vd, h_d, L은 core `NearestSource`의 힙 순서와 동점 규칙을 그대로 탑니다. subsurface 대조가 보로노이 경계 칸에서만 갈리면 core golden부터 확인합니다.
- 출력 NaN은 모두 양의 quiet NaN 상수로 채우고 `double.NaN`은 쓰지 않습니다.
- `WindowMaxKernel`의 묶음별 stamp는 N개라 5000² 히어로를 12묶음으로 돌리면 numba(int64)는 2.4 GB, C#(int)은 1.2 GB가 듭니다. 출발 칸별 방문 목록으로 바꾸는 안(결과 동일)은 열린 질문입니다.
- L0에서는 `WaterBodies` 안의 `ValleyDepth(z)` 결과를 pipeline이 `ValleyDepth(z + relief_m)`로 덮어씁니다. 함수 단위 golden이 앞의 값을 비교하므로 두 번 계산합니다.
- Python `ValueError`의 한국어 문구와 검사 순서를 그대로 옮깁니다. 설정 검사는 NaN을 거절하는 `!(…)` 꼴을 유지합니다.

### metrics — drainage·hypsometry·scorecard

대상은 `src/bpcg/metrics/` 의 네 파일(1,460줄)이고, 계약은 docs/pipeline.md 12절과 tests/test_metrics.py 입니다. 단계 0 실험에서는 tiny 프로필을 메모리에서 다시 돌려 점수표 입력을 잡았고, 다시 만든 점수표가 `out/tiny_check/{planet,hero}/scorecard.json` 과 같음을 확인했습니다. 점수표 입력은 `PlanetState.fields`·`HeroState.fields`·`diag['solver']` 로 그대로 다시 만들 수 있습니다. 히어로는 `diag['extra_inflow_m3_per_yr']` 도 씁니다. 따라서 golden 스크립트가 파이프라인 함수를 가로챌 필요가 없습니다.

- 판정(pass)과 이산 출력(봉우리, 칸 수, 정렬 지수)은 tiny 에서 합격선과 거리가 크거나 정수 비율이라 C# 에서 정확히 같게 만들 수 있습니다.
- value 를 비트 단위로 맞추려면 numpy pairwise 합과 `np.histogram`(배열 bins)의 '정렬 → 누적합 → 차분' 알고리즘을 그대로 옮겨야 합니다. 순차 합으로 바꾸면 tiny 에서도 바다 비율과 수지 값의 마지막 자리가 달라집니다.
- 비트 일치를 보장할 수 없는 값은 `np.polyfit`(LAPACK)을 거친 Hack 지수와 libm 함수를 거친 값입니다. 이 맥에서는 numpy 의 float64 log10·exp·pow 가 libm 과 비트 단위로 같았습니다(40만·20만 표본).
- scipy 1.18.1 macOS arm64 휠의 `gaussian_filter1d` 는 대칭 합의 마지막 두 번만 FMA 로 계산합니다. FMA 없이 옮기면 평활 밀도가 1 ulp 다르지만 봉우리 판정은 바뀌지 않습니다.
- 의심 버그는 두 건입니다((e) 참고).

#### (a) 대응표

| Python | C# (namespace `Bpcg.Metrics`) | 비고 |
|---|---|---|
| `__init__.py` | 파일 없음 | docstring 뿐이고 다시 내보내는 이름이 없음 |
| `drainage` | `Metrics/Drainage.cs`, `public static class Drainage` | |
| `ALIGNMENT_STEPS`, `ALIGNMENT_TOL_DEG`, `EDGE_BAND` | `const int AlignmentSteps = 6`, `const double AlignmentTolDeg = 5.0`, `const double EdgeBand = 0.9` | 설정에 없는 정의값 |
| `_BANDS` | `private static int BandCode(string band)` | all 0, edge 1, center 2 |
| `_receiver_distance_kernel` | `ReceiverDistanceKernel(long[] rcv, int[] nbr, double[] dist, double[] pos, double[] output)` | private, Parallel.For |
| `_longest_path_kernel` | `LongestPathKernel(long[] order, long[] rcv, double[] rdist, double[] areaUp, double[] length, long[] main)` | private, 순차 |
| `_main_stem_kernel`, `_reach_kernel` | `MainStemKernel(long[] order, long[] rcv, long[] main, bool[] output)`, `ReachKernel(long[] order, long[] rcv, bool[] isRiver, bool[] isSink, bool[] ok)` | private, 순차 |
| `_layer_cross_kernel` | `LayerCrossKernel(long[] rcv, double[] z, double[] bottom, int nLayers, bool[] output)` | private, Parallel.For, `Geology.Model.LayerIndexAt` 호출 |
| `_alignment_kernel` | `AlignmentKernel(long[] rcv, bool[] isRiver, long faceCells, long ny, long nx, int band, double edge, int steps, double tolDeg, sbyte[] output)` | private, Parallel.For |
| `_check_graph`, `_rcv_n`, `_order`, `_vec`, `_mask`, `_face_layout` | `CheckGraph`, `RcvN(long[], int?)`, `CheckedOrder(long[]?, long[])`, `Vec(double[] 또는 double, string, int)`, `Mask(bool[], string, int)`, `FaceLayout(CellGraph) → (long FaceCells, long Ny, long Nx)` | private, 한국어 메시지 그대로 |
| `receiver_distance` | `double[] ReceiverDistance(CellGraph graph, long[] receiver)` | |
| `longest_flow_path` | `(double[] Length, long[] MainDonor) LongestFlowPath(CellGraph graph, long[] receiver, long[]? order = null, double[]? drainageArea = null)` | |
| `main_stem_mask` | `bool[] MainStemMask(long[] receiver, long[]? order, long[] mainDonor)` | |
| `hack_fit` | `(double CoefficientKm, double Exponent) HackFit(CellGraph graph, long[] receiver, long[]? order, double[] drainageArea, bool[]? mask = null, double minAreaCells = 10.0)` | `Numerics.Polyfit` |
| `river_reach_fraction` | `double RiverReachFraction(long[] receiver, long[]? order, bool[] isRiver, bool[] isSink)` | |
| `river_backflow_count` | `long RiverBackflowCount(long[] receiver, double[] waterLevel, bool[] isRiver, double tol = 0.0)` | |
| `law_slope_from_fields` | `double[] LawSlopeFromFields(CellGraph graph, long[] receiver, double[] discharge, double[] drainageArea, double[] sedimentFlux, double[] uplift, double[] kS, double[] sCrit, Config cfg)` | |
| `law_cross_mask` | `bool[] LawCrossMask(long[] receiver, double[] z, double[] strataBottom, int nLayers)` | (N, L) 행 우선 |
| `law_consistency` | `LawConsistencyResult LawConsistency(double[] slope, double[] lawSlope, double[] sCrit, bool[]? mask = null)`, `sealed record LawConsistencyResult(double Median, double Max, long N)` | |
| `budget_error` | `double BudgetError(long[] receiver, double[] flux, double[] source, long[]? order = null, bool clipNegative = false)` | |
| `alignment_counts`, `grid_alignment` | `(long Hit, long Used) AlignmentCounts(CellGraph graph, long[] receiver, bool[] isRiver, string band = "all", int steps = AlignmentSteps, double tolDeg = AlignmentTolDeg)`, `double GridAlignment(같은 인자)` | |
| `hypsometry` | `Metrics/Hypsometry.cs`, `public static class Hypsometry` | |
| `BIMODAL_*` 4개 | `const double BimodalBinM = 100.0, BimodalSmoothM = 250.0, BimodalMinSeparationM = 1000.0, BimodalMinProminence = 0.05` | |
| `_vec`, `_area`, `_weighted_fraction` | `Vec/VecBool/VecLong`, `AreaOrOnes(double[]? area, int n)`, `WeightedFraction(bool[] hit, bool[] use, double[] area)` | private |
| 함수 `hypsometry` | `(double[] Edges, double[] Frac) Compute(double[] z, double[]? area, int bins = 100)` 와 `Compute(double[] z, double[]? area, double[] binEdges)` | 모듈과 같은 이름이라 `Compute`(CS0542) |
| `bimodality` | `BimodalityResult Bimodality(double[] z, double[]? area, double binM = …, double smoothM = …, double minSeparationM = …, double minProminence = …)`, `sealed record BimodalityResult(double[] Peaks, double SeparationM, bool Ok, int NPeaks)` | |
| `hypsometry_distance` (안의 `cdf_parts`) | `double HypsometryDistance(double[] zA, double[]? areaA, double[] zB, double[]? areaB)`, private `CdfParts(double[] z, double[]? area)` | |
| `ocean_fraction`, `shelf_area` | `double OceanFraction(bool[] isOcean, double[]? area)`, `(double AreaM2, double Fraction) ShelfArea(double[] z, double[]? area, long[] crustType, bool[] isOcean, double depth = 200.0)` | |
| `flat_fraction`, `gw_surface_fraction` | `double FlatFraction(double[] slope, bool[] landMask, double threshold = 0.02, double[]? area = null)`, `double GwSurfaceFraction(double[] z, double[] zGw, bool[] landMask, double tol = 0.5, double[]? area = null)` | |
| `scorecard` | `Metrics/Scorecard.cs`, `public static class Scorecard` | |
| `DEFAULT_THRESHOLDS`, `EARTH_*_RANGE` | `static readonly IReadOnlyDictionary<string, double> DefaultThresholds`, `(double Lo, double Hi) EarthHackRange = (0.49, 0.6)`, `EarthGwSurfaceRange = (0.22, 0.32)` | rock_samples 는 10000.0 으로 두고 쓸 때 (int) |
| `thresholds`, `rock_consistency`, `even_sample` | `Dictionary<string, double> Thresholds(Config? cfg = null)`, `double RockConsistency(long[] sampleRock, long[] solverRock)`, `long[] EvenSample(bool[] mask, int nSamples)` | |
| `surface_rock_samples`, `cave_soluble_fraction` | `(byte[] Sample, byte[] Solver) SurfaceRockSamples(double[] z, byte[] surfaceRock, double[] strataBottom, byte[] strataRock, int nLayers, long[] cells)`, `(double Fraction, long Count) CaveSolubleFraction(IReadOnlyList<double[]> caveLevels, double[] strataBottom, byte[] strataRock, int nLayers)` | strata_rock 은 (N, L+1) |
| `water_rule_violations` | `WaterRuleCounts WaterRuleViolations(double[] z, double[] zGw, double[] waterLevel, double? maxDepth = null, double tol = 1.0e-3)`, `sealed record WaterRuleCounts(long Above, long Water, long Deep, long Nan, long Total)` | 메서드 이름과 겹치지 않게 함 |
| `_entry` | `ScoreEntry Entry(…)`, `sealed record ScoreEntry(ScoreValue Value, string Unit, string Kind, bool? Pass, string Note)`, `readonly record struct ScoreValue` (Null/Bool/Long/Double) | JSON 형 보존 |
| `_Level` | `private sealed class Level` (Name, F, Graph, Diag, Cfg, Th, `Missing`, `Arr*`, `N`, `Rcv()`, 캐시한 `Order()`, `Root()`, `Flag(name, n)`, `Land(n)`) | |
| `_run`, `_finite_or_none`, `_add_*` | `Run(OrderedDictionary<string, ScoreEntry> output, string key, string kind, string unit, List<string> missing, Func<(ScoreValue, bool?, string)> fn)`, `FiniteOrNone(double)`, `AddPlanetOnly/AddCommon/AddHeroOnly(output, Level)` | private |
| 함수 `scorecard` | `OrderedDictionary<string, ScoreEntry> Compute(FieldSet? planetFields = null, CellGraph? planetGraph = null, FieldSet? heroFields = null, CellGraph? heroGraph = null, ScoreDiag? diag = null, Config? cfg = null)`, `sealed record LevelDiag(bool? Converged, long? Iterations, double[]? ExtraInflowM3PerYr, double[]? LawSlope, (long[] Sample, long[] Solver)? RockSamples)`, `sealed record ScoreDiag(LevelDiag? Planet, LevelDiag? Hero)` | 모듈과 같은 이름이라 `Compute` |
| `failed_checks` | `List<string> FailedChecks(IReadOnlyDictionary<string, ScoreEntry> card)` | 삽입 순서 |

#### (b) 수치 함정 표

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| drainage.py:40-55 | prange·거리 | 칸별 쓰기만 하는 prange 입니다. 첫 일치 슬롯에서 break 하고, 이웃이 아니면 sqrt((dx·dx+dy·dy)+dz·dz) 입니다. numba 는 FMA 축약을 하지 않습니다(20만 표본 확인) | Parallel.For, 같은 결합 순서, FMA 금지 | exact |
| drainage.py:71-87 | 동점·단락 평가 | `m < 0` 이 먼저라 main = −1 일 때 area_up[−1] 을 읽지 않습니다. 같은 길이면 큰 상류 면적, 그다음 작은 번호를 고르고, NaN 이면 처리 순서가 결과를 정합니다 | 같은 순서의 단락 조건, 역 topo 순서의 순차 for | exact |
| drainage.py:97-116 | 순차 커널 | 본류·도달 커널은 하류 값이 먼저 정해져 있어야 합니다 | 순차 for | exact |
| drainage.py:127-138 | 부등호 비대칭 | 시작 층은 `bottom <= z_r`, 지표 층은 `bottom < z_c` 이고 NaN 은 L 입니다 | 두 비교를 따로 구현, bottom[c·L+i] | exact |
| drainage.py:163-188 | 정수 나눗셈 | `np.int64(c0)` 뒤의 `//` 는 피연산자가 모두 0 이상입니다 | long `/` | exact |
| drainage.py:191 | 각도 변환 | `math.degrees` 는 x·(180/π) 를 한 번 곱합니다. .NET 10 `double.RadiansToDegrees` 는 (x·180)/π 라 약 26% 값에서 1 ulp 다릅니다 | `Math.Atan2(dj, di) * (180.0 / Math.PI)`. 5° 문턱까지 최소 0.194° 여유 | exact |
| drainage.py:329 | pairwise → 분기 | `area.mean()` 이 a_min 이 되고 `a_up >= a_min` 을 정합니다. 행성 tiny 는 pairwise …90044, 순차 …90105 이고, 히어로는 28칸이 문턱과 같습니다 | `NpReduce.Mean`, `>=` | exact |
| drainage.py:333-338 | log10·pow | np.log10 은 libm 과 같고(40만 표본), `10**b` 는 note `.3g` 에만 쓰입니다 | `Math.Log10`, `Math.Pow`, `PyFormat.G` | tolerance |
| drainage.py:337 | LAPACK | polyfit 은 열 스케일 뒤 dgelsd(rcond = n·eps)로 풉니다. 닫힌 식과 상대 3.9e-16 차(조건수 52)입니다 | `Polyfit`: 같은 스케일, QR + 2×2 SVD 로 rcond 처리 | 상대 1e-12 |
| drainage.py:423 | 계산 순서 | E = U + ((G·R)·Qs)/Q 입니다 | `gr = g * r` 를 먼저 | exact |
| drainage.py:425 | pow 빠른 길 | numpy 2.5.3 은 지수가 Python float 0.5 이면 sqrt 를 씁니다. 이 맥의 libm pow(x, 0.5) 는 sqrt 와 일부 다릅니다 | θ == 0.5 이면 `Math.Sqrt`, 아니면 `Math.Pow` | tolerance |
| drainage.py:426-429 | NaN·0 나눗셈 | minimum/maximum 은 NaN 을 전파합니다. eps/0 = inf 이고 출구는 NaN 입니다 | `PyMath.NpMinimum/NpMaximum`, IEEE 그대로 | exact |
| drainage.py:469 | 중앙값 | partition 뒤 짝수 개면 (a+b)/2 입니다 | 복사본을 정렬 | exact |
| drainage.py:504-512 | pairwise·max | 수지의 네 합은 pairwise 입니다(히어로 1.907e-16, 순차면 5.72e-16). `max(outlet, cell)` 이고 scale = 0 이면 0.0 또는 inf 입니다 | `NpReduce.Sum`, 삼항 연산, inf 유지(JSON null) | exact |
| drainage.py:576 | 정수 비율 | (hit/used)/(2·tol/45) 입니다 | `(double)hit / used / (2.0 * tolDeg / 45.0)` | exact |
| hypsometry.py:49-52, 71 | pairwise | 면적 비율의 합입니다. tiny 바다 비율은 pairwise …121, 순차 …1177 입니다 | 오름차순으로 모아 `NpReduce.Sum` | exact |
| hypsometry.py:86 | 히스토그램 | 배열 bins + weights 는 블록(65536)마다 argsort → cumsum → searchsorted(앞 경계 left, 마지막 right) → 누적 → diff 로 구합니다. 칸별 합과 상대 3.3e-13 다릅니다 | `NpStats.HistogramArrayBins` | exact |
| hypsometry.py:86 | argsort 동점 | 동점 순서가 cumsum 끝자리를 바꿉니다(검증). arm64 numpy 의 기본 argsort 는 일반 introsort(SMALL_QUICKSORT 15)이고, x86 AVX2/512 는 x86-simd-sort 입니다 | `NpSort.ArgSortQuick`(numpy aquicksort_ 이식), golden 은 arm64 | exact |
| hypsometry.py:78-81, 119-123 | 격자·반올림 | linspace 는 i·step + lo 이고 마지막은 hi 입니다. floor/ceil 을 쓰고 round 는 짝수 반올림입니다 | `NpGrid.Linspace`, `Math.Round(ToEven)` | exact |
| hypsometry.py:125 | ndimage·FMA | σ 2.5 → 반지름 10, 가중치 exp(−0.08·x²)/pairwise 합입니다. scipy arm64 는 jj = −2, −1 두 번만 FMA 로 계산합니다(반지름 8 은 전부, 12 는 마지막 4번, 16 은 0번). FMA 없이 옮기면 141칸 중 27칸이 1 ulp 다릅니다 | `NdImage.GaussianFilter1D` 같은 루프, FMA 없음 | 4 ulp |
| hypsometry.py:131 | find_peaks | 평지는 (left+right)//2 이고, 기슭은 `x[i] <= x[peak]` 동안 찾으며 갱신은 `<` 입니다. 남기는 조건은 `pmin <= prom` 입니다. tiny 여유는 둘째 0.838·top, 셋째 0.002·top 입니다 | `Signal.FindPeaks` | exact |
| hypsometry.py:134-135 | 안정 정렬 | 돌출도가 큰 순으로, 같으면 앞 번호를 고르고 봉우리는 오름차순으로 놓습니다 | `NpSort.ArgSortStable` | exact |
| hypsometry.py:160-168 | KS | 안정 정렬(−0.0 = 0.0), 순차 cumsum, 원래 순서의 pairwise 합, union1d, searchsorted right 를 씁니다 | 같은 순서 | exact |
| scorecard.py:92-96 | 1만 점 표본 | 해시·난수가 아닙니다. flatnonzero 뒤 linspace 등간격과 짝수 반올림입니다(6칸·3점 → [0, 2, 5]). tiny 는 후보가 2032·4091칸이라 이 경로를 타지 않습니다 | `Math.Round(i * step, ToEven)`, 마지막은 M−1 | exact |
| scorecard.py:168 | 결합 순서 | (z − max_depth) − tol 입니다 | 같은 왼쪽 결합 | exact |
| scorecard.py:181-184 | JSON 값 형 | null·bool·int·float 가 섞입니다(backflow·water_rule 은 int, converged 는 bool). NaN·inf 는 null 이 됩니다 | `ScoreValue`, `PyJson` | exact |
| scorecard.py:237-246 | 예외 | ValueError(LinAlgError 포함)만 잡아 '계산 실패: {메시지}' 로 남깁니다. 모양은 '(16,)' 같은 튜플 표기입니다 | 전용 예외형만 catch | exact |
| scorecard.py:259-557 | 문자열 형식 | str(float) 는 repr 이고, `:g`(200, 1e-06), `.3g`(0.488, 3.54e-06, 1e+03), `.4f`, `.0f`(짝수 반올림, '-0'), 'nan' 을 씁니다 | `Bpcg.IO.PyFormat` | exact |
| scorecard.py:604-626 | 순서 | 결과 키 순서와 필드 검사 순서는 dict 삽입 순서입니다 | `OrderedDictionary`, 순서를 보존하는 FieldSet | exact |

metrics 의 산술은 모두 float64 입니다(입력은 `arr(name, float64)` 나 `_vec` 로 먼저 바꿈). 따라서 NEP 50 승격 문제가 없습니다. 음수가 들어오는 `//`·`%`, 음수 색인 감김, np.empty 를 쓰기 전에 읽는 곳, 중복 색인 대입, 정수 넘침, 시간·환경 의존 값도 없습니다.

#### (c) 외부 호출과 대체 방식

| 외부 호출 | 위치 | C# 대체 | 비교 |
|---|---|---|---|
| `sum`/`mean` (pairwise) | 면적 비율, a_min, 수지, 히스토그램 합, 가우스 가중치 합 | `Numerics.NpReduce` | exact |
| `np.histogram`(배열 bins, weights) | hypsometry.py:86 | `NpStats.HistogramArrayBins` + `NpSort.ArgSortQuick` | exact |
| `argsort(stable)`, `sorted`, `union1d`, `searchsorted` | hypsometry.py:134-167 | `NpSort.ArgSortStable`, 정렬 뒤 중복 제거, 이분 탐색 | exact |
| `np.median`, `np.linspace(...).round()` | drainage.py:470, hypsometry.py:81, scorecard.py:95 | `NpStats.Median`, `NpGrid.Linspace`, `Math.Round(ToEven)` | exact |
| `np.polyfit(x, y, 1)` | drainage.py:337 | `Polyfit.Fit1` | 상대 1e-12 |
| `gaussian_filter1d(σ, mode='constant')` | hypsometry.py:125 | `NdImage.GaussianFilter1D` (truncate 4.0, 대칭 루프) | dens 4 ulp |
| `find_peaks(prominence=…)` | hypsometry.py:131 | `Signal.FindPeaks` (wlen 없음) | exact |
| `np.log10`, `np.exp`, `**`, `math.atan2` | drainage, hypsometry | `Math.Log10/Exp/Pow/Atan2` (θ = 0.5 이면 Sqrt) | 맥 exact 기대, 그 밖은 tolerance |
| `str.format`, `json.dumps`(`bake.bundle.write_json`) | 점수표 note, scorecard.json | `Bpcg.IO.PyFormat`, `Bpcg.IO.PyJson` (ensure_ascii=False, indent 2, '·', '²', '−', '≤', 작은따옴표를 그대로) | exact |
| bpcg 내부 | `Hydro.Routing.CheckRcv/TopoOrder`, `Hydro.Accumulate`, `Geology.Model.LayerIndexAt/LayerIndex/RockAtPoints`, `Geology.Rocks.SCrit/Soluble`, `Core.CellGraph`, `Core.Config` | 해당 묶음의 포팅을 씀 | exact |

#### (d) numba 커널과 prange

- drainage.py 에 `@njit(cache=True)` 커널이 6개 있고, hypsometry·scorecard 에는 없습니다.
- prange 3개(`_receiver_distance_kernel`, `_layer_cross_kernel`, `_alignment_kernel`)는 칸 c 의 출력만 쓰고 축약 변수가 없습니다. 공유하는 것은 읽기 전용 배열뿐이라 Parallel.For 로 옮겨도 결과가 스레드 수에 따라 바뀌지 않습니다. `Geology.Model.LayerIndexAt` 은 순수 함수여야 합니다.
- 순차 3개(`_longest_path_kernel`, `_main_stem_kernel`, `_reach_kernel`)는 topo 순서에 기대므로 순차 for 로 옮깁니다.
- 형: prange 색인은 int64 로 추론되었고(typemap 확인), `_alignment_kernel` 은 이 값을 다시 `np.int64(c0)` 로 바꿉니다. 정수에서 실수로 바뀌는 곳은 atan2 인자뿐(sitofp)입니다. numba 는 FMA 축약을 하지 않습니다(검증). int8 출력은 `sbyte[]` 로 둡니다.
- 성능 기준: tests 의 L0 크기 시험(구면 512², 157만 칸)은 정렬 < 2 s, Hack < 5 s, 점수표 < 30 s 를 요구합니다.

#### (e) 의심 버그 (고치지 않고 기록)

1. 행성 법칙 자기일관성의 G 불일치(pipeline.py:668, drainage.py:413-423)입니다. L0 솔버는 `l0_config(cfg)`(deposition_g_l0 = 0.0)로 풀지만 점수표에는 `cfg`(deposition_g = 1.0)가 넘어갑니다. tiny 에서 중앙값 2.49e-7·최댓값 3.54e-6(scorecard.json 값)이, cfg_l0 로 계산하면 5.39e-16·4.06e-15 로 떨어집니다. 지금은 합격선 1e-3 보다 훨씬 작아 판정에 영향이 없지만, 지표가 솔버 일관성 대신 G 항 차이를 잽니다. C# 도 cfg 를 그대로 넘겨 같은 값을 냅니다.
2. 법칙 비교 마스크의 빈틈(scorecard.py:399)입니다. fan·not_steady 칸은 빼지만, 선상지로 올라간 칸을 수신 셀로 가진 기여 셀과 호수 칸은 남습니다. 이 칸들의 경사는 pipeline.py:255 에서 max(z − z_r, 0)/d 로 줄어듭니다. tiny 히어로에서 상대차 > 1e-3 인 9칸은 모두 선상지 칸 2389·2452·2454·2519 로 흘러듭니다. 그중 호수 칸 2520(경사 0)이 최댓값 1 을 만들어 note 에 '최댓값 1' 로 나옵니다. 중앙값 판정은 그대로입니다.

#### (f) golden 대조 계획

| 대상 | 입력 | 출력 | 비교 |
|---|---|---|---|
| `ReceiverDistance` | tiny 행성·히어로 graph + receiver, 이웃이 아닌 수신 셀 손 사례 | (N,) float64 | exact |
| `LongestFlowPath`, `MainStemMask` | tiny receiver·order·drainage_area, 동점 갈래(같은 면적·다른 면적), NaN 면적, 대각 망 40² | length, main int64, stem bool | exact |
| `HackFit` | tiny 행성(mask 육지)·히어로, 대각 망 200²·띠 500, 점 3개 미만·ptp 0 | (c, h) | 상대 1e-12 |
| `RiverReachFraction`, `RiverBackflowCount` | tiny, 시험의 8칸 망 | float, int | exact |
| `LawSlopeFromFields` | tiny 행성(cfg 그대로)·히어로, θ = 0.5, Q = 0, 출구 | (N,) float64 | 상대 1e-14, NaN 위치 exact |
| `LawCrossMask`, `LawConsistency` | tiny(Python law_slope 를 그대로, 홀수·짝수 n), z_r 이 경계와 같은 칸, 합성 5칸 | bool, (median, max, n) | exact |
| `BudgetError` | tiny 물·퇴적물(히어로는 extra inflow 포함), scale 0 두 경우, 대각 망 30² 교란 | float | exact |
| `AlignmentCounts` (+ 커널 flag) | tiny 행성 edge·center, 히어로 all, 직선·말걸음·구면 n = 20 | sbyte flag, (hit, used), 지수 | exact |
| `Hypsometry.Compute` | tiny z_mean_m·area 와 bimodality 경계, 동점 + 가중치 크기 차가 큰 손 사례, 정수 bins, 상수 z | edges, frac | exact |
| `GaussianFilter1D`, `FindPeaks` | 위 frac, 무작위 400칸, 평지·포함 경계 | dens, peaks, prominences | dens 4 ulp, 나머지 exact |
| `Bimodality`, `HypsometryDistance`, 면적 비율 4개 | tiny 호출 지점 입력, 시험 난수 입력(값 저장), −0.0·동점 KS | 결과 값 | exact |
| `EvenSample` 외 scorecard 도우미, `Thresholds` | tiny, (M, n) = (6, 3), (4, 3), (5, 0), (5, 1), (25001, 10000), (123457, 10000) | 칸 번호·표본·개수 | exact |
| `Scorecard.Compute` → scorecard.json | tiny 행성·히어로 전체 입력, 입력 없음, 계산 실패(law_slope 길이 3), NaN Hack | JSON 글자 | 키·unit·kind·pass exact. hack_exponent 는 상대 1e-12, law_consistency 는 절대 1e-12. 두 note 의 `.3g` 숫자만 값 허용 |
| `PyFormat` | 2.5, −0.5, −0.0, 0.125, 1e-06, 1000, 0.4885, nan, inf × str·g·.3g·.4f·.0f | 문자열 | exact |

입력 크기는 행성 float64 배열 하나가 48 KB, strata 가 295 KB, 그래프가 약 0.8 MB 입니다. 그래프는 golden 스크립트가 만든 tiny 묶음의 graph.npz 에서 읽고, 커밋하는 자료는 손 사례만 1 MB 이하로 둡니다.

#### (g) 포팅 순서와 위험 메모

1. 먼저 필요한 것은 다음과 같습니다. Core(CellGraph, Config, FieldSet), Hydro(CheckRcv, TopoOrder, Accumulate), Geology(LayerIndexAt, LayerIndex, RockAtPoints, Rocks 표), Numerics(NpReduce, NpSort.ArgSortQuick·ArgSortStable, NpStats.Median·HistogramArrayBins, NpGrid.Linspace, NdImage.GaussianFilter1D, Signal.FindPeaks, Polyfit, PyMath.NpMaximum·NpMinimum), IO(PyFormat, PyJson).
2. metrics 안의 순서는 Drainage 커널 → Drainage 공개 함수 → Hypsometry(면적 비율 → 히스토그램 → Bimodality → KS) → Scorecard 도우미 → `Scorecard.Compute` → scorecard.json 쓰기입니다.
3. 위험도는 scorecard 가 높습니다(note 형식, JSON 값 형, 예외 의미). drainage(LAPACK, pairwise 분기)와 hypsometry(히스토그램 알고리즘, scipy FMA 꼬리)는 중간입니다. 끝에서 끝 비교에서 scorecard.json 은 hack_exponent 와 law_consistency 의 value·note 숫자를 빼면 글자 단위로 같아야 합니다.
4. pipeline 포팅에 넘길 메모입니다. 행성 점수표에는 cfg(cfg_l0 아님)를 넘깁니다. `scorecard_summary` 의 실패 이름은 서수 비교(StringComparer.Ordinal)로 정렬합니다. 점수표는 manifest.json 의 `meta.diag.scorecard` 에도 들어갑니다.
5. tiny 가 지나가지 않는 경로는 손 사례로 채웁니다. 가장 긴 경로의 동점, 이웃이 아닌 수신 셀, even_sample 의 linspace, z 동점, 계산 실패, θ = 0.5 가 여기에 해당합니다.

### pipeline·cli — pipeline.py·cli.py

대상은 `src/bpcg/pipeline.py`(735줄)와 `src/bpcg/cli.py`(370줄, studio 명령 제외)입니다. 두 파일에는 numba 커널과 prange가 없고, 다른 모듈을 정해진 순서로 부르며 결과 사전을 엮는 일을 합니다. 파일 안 실수 계산은 `strength_limited_uplift`의 거듭제곱·최댓값·최솟값과 `reroute_after_fans`의 경사 나눗셈뿐이라 수치 위험은 낮습니다. 위험은 주로 바깥 계약에 있습니다: 호출 순서, 설정 사본(`cfg`와 `cfg_l0`), diag·info 사전의 키 순서와 값 형, 경로, 종료 코드, 콘솔 문구입니다. 아래 '검증'은 저장소 .venv(Python 3.13, numpy 2.5.3, macOS arm64)에서 돌린 결과입니다. .NET 쪽은 SDK가 없어 dotnet/runtime release/10.0 소스와 문서로만 확인했습니다.

**호출 순서(`bpcg all`, 행성 있음).**

1. `_config`: --set 해석 → --seed 반영 → `load_config` → `checked_overrides`.
2. `apply_threads`.
3. `run_planet` → `generate_planet`:
   - 거친 `sphere_graph` → `build_materials`
   - L0 `sphere_graph`(jitter, seed) → `transfer_materials` → `generate_geology` → `l0_config`
   - `strength_limited_uplift`: 솔버 1회와 `subgrid_relief`
   - `run_stages_2_to_4`: 솔버 → `add_fans` → 꼭짓점이 있으면 `reroute_after_fans` → 바다 칸 수심 → `subgrid_relief` → 기온 → `water_bodies` → `valley_depth` → `surface_rock` → `soil_and_alluvium` → `water_table` → `cave_levels` → `river_segments`
   - `score`(원래 cfg로) → `scorecard_summary`
4. `run_planet`의 나머지: `save_planet_state` → `face_textures`(메모리 행성) → scorecard.json.
5. `config_from_manifest`로 설정을 다시 만듭니다.
6. `run_hero`: `load_planet_state` → `generate_hero`(`find_hero` → `refine_hero`) → `save_hero_state` → scorecard.json. ValueError가 나면 평면 히어로로 바꿉니다.
7. `run_bake`: `bake_config` → `load_hero_state` → `bake_corridor`.
8. `run_globe`: `load_planet_state` → `hero_from_run` → `bake_globe`.

**실행 폴더.**

- `<run>/planet/`: manifest.json, graph.npz, `<그룹>/<필드>.npy`, textures/, scorecard.json
- `<run>/hero/`: 같은 꼴에 rivers.npz를 더함
- `<run>/corridor/`, `<run>/globe/`
- `--engine`이면 `ROOT/engine/baked/`와 `ROOT/engine/baked/globe/`

`<run>`은 `--out`이 있으면 그 값입니다. 없으면 planet·all·hero --flat은 `OUT/<--planet>`, hero --from은 행성 묶음의 부모, bake는 히어로 묶음의 부모를 씁니다. bake의 `--out`은 corridor 위치만 바꾸고, globe는 늘 히어로 묶음의 부모 아래에 씁니다.

#### (a) 대응표

| Python | C# 파일·형·멤버 |
|---|---|
| `pipeline.py` | `csharp/Bpcg/Pipeline.cs`, `namespace Bpcg`, `public static class Pipeline` |
| `Log` | `Action<string>?` (전역 using 별칭 `Log`) |
| `SOLVER_LOG_EVERY` | `public const int SolverLogEvery = 10` |
| `PlanetState` | `public sealed class PlanetState`: `CellGraph Graph`, `FieldMap Fields`, `LayerColumns Columns`, `PyDict Info`, `PyDict Diag` (eq=False라 record 대신 class) |
| `HeroState` | `public sealed class HeroState`: `Graph`, `Fields`, `Columns`, `HeroSite? Site`, `List<long[]> Rivers`, `List<PyDict> FanApexes`, `PyDict Diag` |
| `StageResult` | `public sealed class StageResult`: `FieldMap Fields`, `List<long[]> Rivers`, `List<PyDict> FanApexes`, `SolverResult Solver`, `PyDict Diag` |
| `_say`, `_every` | `private static void Say(Log? log, string msg)`, `private static Log? Every(Log? log, int every)` |
| `apply_threads` | `public static int ApplyThreads(Config cfg)` (전역 최대 병렬도 설정) |
| `_check_cfg`, `_vector` | `private static void CheckCfg(Config? cfg)`, `private static double[] Vector(double x, int n)`, `Vector(double[] x, int n, string name)`, `Vector(float[] x, int n, string name)` |
| `solver_max_iter` | `public static int SolverMaxIter(Config cfg, string level)` |
| `score` | `public static PyDict Score(Config cfg, FieldMap? planetFields = null, CellGraph? planetGraph = null, FieldMap? heroFields = null, CellGraph? heroGraph = null, PyDict? diag = null)` |
| `scorecard_summary` | `public static PyDict ScorecardSummary(PyDict card)` |
| `reroute_after_fans` | `public static RerouteResult RerouteAfterFans(CellGraph graph, double[] z, bool[] isOutlet, long[] receiver, bool[] fan, double[] runoffEff, double[] uplift, Config cfg, double[]? extraInflow = null)`와 `public sealed record RerouteResult(long[] Receiver, long[] Order, double[] DischargeM3PerYr, double[] DrainageAreaM2, double[] SedimentFluxM3PerYr, double[] Slope)` |
| `run_stages_2_to_4` | `public static StageResult RunStages2To4(CellGraph graph, bool[] isOutlet, double[] zOutlet, double[] uplift, double[] runoffEff, double[] precip, LayerColumns columns, Config cfg, double[]? extraInflow = null, double? latDeg = null, Log? log = null, double[]? runoff = null, bool[]? isOcean = null, double[]? zSeafloor = null, double[]? temperatureSeaC = null, int? maxIter = null, double[]? zInit = null)` (스칼라 인자는 부르는 쪽이 `Vector(x, n)`으로 펼침) |
| `l0_config` | `public static Config L0Config(Config cfg)` |
| `strength_limited_uplift` | `public static (double[] Uplift, double[]? ZStart, PyDict Diag) StrengthLimitedUplift(CellGraph graph, bool[] ocean, FieldMap fields, LayerColumns columns, Config cfg, Log? log = null)`, 대조용 `internal static double[] TaperUplift(double[] u, double[] zMean, double zLim, double power, double uBase)` |
| `generate_planet` | `public static PlanetState GeneratePlanet(Config cfg, Log? log)` (Python 기본값 `print`는 부르는 쪽이 넘김) |
| `generate_hero` | `public static HeroState GenerateHero(Config cfg, PlanetState? planet, Log? log)` |
| `cli.py` | `csharp/Bpcg.Cli/Cli.cs`(`namespace Bpcg.Cli`, `public static class Commands`)와 `Program.cs`(`Main`이 `Commands.Main`을 부름) |
| 폴더 상수 5개 | `const string PlanetDir`, `HeroDir`, `CorridorDir`, `TextureDir`, `GlobeDir` (값 planet·hero·corridor·textures·globe) |
| `BAKE_SECTIONS`, `BAKE_SET_PREFIXES` | `static readonly string[] BakeSections`(detail), `BakeSetPrefixes`(detail., profile.corridor.) |
| `_say`, `_set_overrides`, `_config`, `_with_seed`, `_out_dir` | `static void Say(string msg)`, `static OrderedDictionary<string, object?> SetOverrides(IReadOnlyList<string>? items)`, `static Config ConfigFromArgs(CliArgs a)`, `static Config WithSeed(Config cfg, long? seed)`, `static string OutDir(CliArgs a)` |
| `_planet_bundle_dir`, `_hero_bundle_dir` | `static string PlanetBundleDir(string path)`, `static string HeroBundleDir(string path)` |
| `run_planet`, `run_hero` | `public static string RunPlanet(Config cfg, string runDir)`, `public static string RunHero(Config cfg, string runDir, string? planetDir, bool flat)` |
| `bake_config`, `run_bake`, `run_globe` | `public static Config BakeConfig(string heroDir, long? seed = null, IReadOnlyList<string>? sets = null)`, `public static PyDict RunBake(string heroDir, string outDir, bool engine, long? seed = null, IReadOnlyList<string>? sets = null)`, `public static string RunGlobe(string planetDir, string runDir, bool engine)` |
| `_drop_engine_globe` | `static void DropEngineGlobe()` |
| `cmd_planet`, `cmd_hero`, `cmd_bake`, `cmd_all` | `static int CmdPlanet(CliArgs a)`, `CmdHero`, `CmdBake`, `CmdAll` |
| `cmd_studio` | 옮기지 않음 |
| `build_parser`, `main` | `public static CliArgs ParseArgs(string[] argv)`(틀리면 `CliUsageError`, 종료 2), `public static int Main(string[] argv)` |
| `SystemExit(문구)` | `public sealed class CliExit(string message) : Exception(message)` (stderr에 쓰고 종료 1) |
| argparse 결과 | `public sealed record CliArgs(string Command, string Planet, string Profile, string? Out, long? Seed, IReadOnlyList<string>? Set, string? FromDir, bool Flat, string? Hero, bool Engine, bool NoGlobe)` (기본값 earth·laptop·null·null·빈 목록(bake는 null)·null·false·null·false·false) |

`PyDict`(= `OrderedDictionary<string, object?>`)와 `FieldMap`(필드 묶음 형)은 가칭이고, core 항목과 함께 정합니다.

**순환 의존.** Python에서는 다음처럼 서로를 import합니다.

- `pipeline.generate_hero`는 `bpcg.hero`를 함수 안에서 import합니다.
- `hero/refine.py:37`과 `hero/flat.py:40`은 `pipeline`의 상태 형, `Log`, `run_stages_2_to_4`, `score`, `scorecard_summary`, `solver_max_iter`를 맨 위에서 import합니다.
- `bake/bundle.py:315·366`도 상태 형을 함수 안에서 가져옵니다.
- `pipeline.score`는 `metrics`를 함수 안에서 가져옵니다(의존 방향 규칙의 예외).

C#은 한 어셈블리 안 정적 클래스끼리 서로 불러도 되므로 순환을 끊지 않습니다. 상태 형 세 개는 1:1 규칙대로 Pipeline.cs에 두고, `Bpcg.Hero.Refine`·`Flat`이 `Pipeline.RunStages2To4` 등을 직접 부릅니다. 정적 초기화 순서 문제를 피하려고 상수는 `const`로 둡니다. Python에서 `HeroState.site`는 순환을 피하려고 `object`로 두었지만, C#에서는 `HeroSite?`로 형을 줍니다.

**이름 충돌.** 규칙대로 옮기면 `metrics.scorecard.scorecard`·`hydro.accumulate.accumulate`·`metrics.hypsometry.hypsometry`는 형과 같은 이름의 메서드가 되어 CS0542 오류가 납니다. 권고는 메서드 이름을 `Compute`로 하는 것입니다(`Scorecard.Compute`, `Accumulate.Compute`). 모듈과 같은 이름의 클래스(`Config`, `Bundle`)는 모듈 함수를 그 클래스의 static 멤버로 합칩니다. CLI 형을 `Cli`로 하면 `Bpcg` 아래에서 namespace `Bpcg.Cli`로 풀려 CS0118이 나므로, `Commands`로 둡니다.

**종료 코드와 SystemExit 문구.** 종료 코드는 성공 0, SystemExit 1(문구는 stderr), 인자 오류 2, 잡지 않은 예외 1입니다. 아래 문구는 글자 그대로 옮깁니다.

- `--set: {e}`
- `행성 묶음을 찾지 못했습니다: {path} (planet/manifest.json 이 없습니다)`
- `히어로 묶음을 찾지 못했습니다: {path} (manifest.json 이 없습니다)`
- `히어로를 만들려면 --from <행성 묶음> 이나 --flat 이 필요합니다`
- `--set: bake 에서는 detail., profile.corridor. 로 시작하는 키만 바꿀 수 있습니다: {bad} (지형을 바꾸려면 bpcg all 에서 --set)`
- `hero 는 --from <행성 묶음 폴더> 나 --flat 중 하나가 필요합니다`
- `--set 은 hero --flat 에서만 씁니다 (--from 은 묶음 설정을 그대로 씀)`

**콘솔 문구.** 시간 값이 섞여 있으므로 비교 계약으로 보지 않습니다. 다만 문구 틀과 예외 문구는 그대로 옮깁니다. studio/progress.py가 줄 머리를 정규식으로 읽기 때문에, studio가 C# CLI를 부르게 되면 그때부터 계약이 됩니다.

#### (b) 수치 함정 표

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| pipeline.py:566 | pow | `(max(z_mean,0)/z_lim) ** power`는 np.power 스칼라 지수 경로를 탑니다. 지수 -1·0·0.5·1·2는 1/x·1·sqrt·x·x*x로, 그 밖(기본 4.0)은 libm pow로 계산합니다. tiny에서 x*x*x*x와 690칸이 다릅니다 | `NpMath.PowScalar`(같은 빠른 경로 + `Math.Pow`). .NET 10 `Math.Pow`는 CRT pow를 부릅니다 | exact(macOS) |
| pipeline.py:567 | 상수 | `craton * 1e-6`은 4.9999999999999996e-06으로, `/ 1e6`과 다릅니다 | 곱셈 그대로 | exact |
| pipeline.py:566-568 | NaN·부호 0 | maximum(-0.0, 0.0)은 +0, minimum(0.0, -0.0)은 -0, clip(-0.0)은 -0이고 NaN은 전파합니다(arm64 확인) | `Math.Max`·`Math.Min`(IEEE 754:2019)·`Math.Clamp`가 같은 결과 | exact |
| pipeline.py:553-557 | 설정 기본값 | 한계 키가 없으면 U를 그대로 두고 `{'applied': False}`. power 기본값 4.0이 코드에 박혀 있고, z_lim이 0이면 NaN으로 진행해 솔버가 ValueError를 냅니다 | 같은 기본값, Python float() 의미 | exact |
| pipeline.py:517, 575 | max | 순서와 무관하고 NaN을 전파합니다. 육지가 없을 때의 값은 NaN(null)과 0.0으로 서로 다릅니다 | NaN 전파 `NpReduce.Max` | exact |
| pipeline.py:247-252 | 연산 순서 | `area*R_eff + inflow`와 `U*area`를 원소마다 계산하고, accumulate는 순차 커널입니다 | 같은 식, FMA 금지 | exact |
| pipeline.py:253-255 | 가린 최솟값 | `where(nbr==rcv, dist, inf).min(1)`. -1 슬롯의 dist는 inf입니다 | K 슬롯 루프, 초기값 +inf, 출구는 경사 0 | exact |
| pipeline.py:232-245 | 검사·형 | is_outlet은 bool dtype이어야 하고, fan은 astype(bool), receiver는 범위 검사 뒤 int64로 바꿉니다 | bool[]·long[]로 고정 | exact |
| pipeline.py:139-145 | 넓힘 | `_vector`가 float32·정수를 float64로 넓히고 스칼라를 펼칩니다 | Vector 오버로드 | exact |
| pipeline.py:588-706 | 메모리 정밀도 | 메모리 필드는 float64, receiver·order는 int64이고, 저장할 때만 FIELDS dtype으로 바꿉니다 | double[]·long[]로 들고 다님 | exact |
| cli.py:110, 127, 201, 284 | 디스크 왕복 | 히어로·지구본은 다시 읽은 행성으로, 텍스처는 메모리 행성으로 만듭니다. 메모리 행성으로 만든 히어로는 23개 필드가 다릅니다 | 같은 쓰기 → 읽기 순서 | exact |
| pipeline.py:666-673 | 설정 사본 | 점수표에는 cfg(G=1)를, 솔버에는 cfg_l0(G=0)를 넘깁니다 | 고치지 않고 원래 cfg 전달 | exact |
| pipeline.py:529-536 | 설정 사본 | 키가 없으면 같은 객체를, 있으면 검사 없는 with_overrides(float) 결과를 돌려줍니다 | 같은 분기·같은 사용처 | exact |
| pipeline.py:344-475 외 | 시간 | perf_counter 값이 diag.seconds와 콘솔에 들어갑니다 | Stopwatch, 비교에서 제외 | n/a |
| pipeline.py:120-131 | 환경 | 스레드 상한은 os.cpu_count(여기 12)이고 diag.threads에 적힙니다 | `Environment.ProcessorCount`, 비교에서 제외 | n/a |
| pipeline.py:477-518, 684-705 | dict 순서·형 | diag·info가 삽입 순서대로 manifest에 적히고 studio가 키로 읽습니다. update는 자리를 지키고, 5500.0과 5500은 다른 표기입니다 | `OrderedDictionary`, long·double·bool | exact |
| pipeline.py:183-192 | 정렬 | sorted(str)는 코드 포인트 순서이고, pass는 `is False`로 봅니다 | `StringComparer.Ordinal`, `bool?` | exact |
| pipeline.py:501 | 문자열 비교 | `startswith('cave_l')` | `StringComparison.Ordinal` | exact |
| pipeline.py 콘솔 줄 | 서식 | .0f~.3f는 짝수 반올림이고 '-0'이 나올 수 있습니다. dict·list repr, True/False를 씁니다 | `PyFormat.F`·`Repr`, InvariantCulture | n/a |
| pipeline.py:397-402 | 별칭 | 복사한 뒤 바다 칸만 수심으로 바꿉니다 | 새 배열에 덮어쓰기 | exact |
| pipeline.py:415-423 | 연산 순서 | `t_sea - lapse*max(z_air,0)`이고, 구면에서 z_air는 z_mean입니다 | 같은 식, FMA 금지 | exact |
| pipeline.py:106-117 | 클로저 | 1, 10, 20, …번째 줄만 넘깁니다 | 지역 int를 잡는 람다 | exact |
| pipeline.py:124-127, 154-158 | NaN → 정수 | int()가 NaN에서 Python 예외를 내지만, C# 캐스트는 0이 됩니다 | Config 정수 읽기가 예외를 던짐 | exact |
| cli.py:306-360 | argparse | 하위 명령 필수, append(bake만 기본 None), --opt=값, 접두 약어(--s는 모호), 마지막 값 우선을 따릅니다 | 손으로 짠 부분 집합 파서 | exact |
| cli.py:316, 339 | 정수 읽기 | '+7'·' 7 '·'1_000'을 받고 크기 제한이 없습니다 | `PyInt.Parse`로 long, 넘치면 종료 2 | exact |
| cli.py:363-370 | 종료 코드 | 0, SystemExit 1, argparse 2, 예외 1(traceback) | 모든 예외를 잡아 코드로 바꿈 | exact |
| cli.py:42-68 | 덮어쓰기 순서 | --set 다음에 --seed를 넣고, 형식 오류가 파일 오류보다 먼저 납니다. [설정] 줄은 repr로 찍습니다 | 같은 순서, `PyFormat.Repr` | exact |
| cli.py:75-95, 260-266 | 경로 | pathlib 정규화에서 'out/x/'는 'out/x', parent는 'out'입니다. .NET은 'out/x'와 ''를 돌려줍니다 | `Bpcg.IO.PyPath` | exact |
| cli.py:236-255 | 분기 순서 | --flat → --from 없음 → --set 거부 → 묶음 찾기 순서입니다 | 같은 순서·같은 문구 | exact |
| cli.py:140-176 | 분기 순서 | 묶음 설정 → seed → [detail] 추가 → --set → 접두 검사 순서입니다 | 같은 순서, Ordinal 접두 검사 | exact |
| cli.py:272-296 | 예외 형 | ValueError를 모두 잡아 평면 히어로로 바꾸고, 설정은 manifest JSON으로 다시 만듭니다 | 전용 예외형, JSON 정수는 long·소수는 double | exact |
| cli.py:38-39 | 인코딩 | print(flush=True), UTF-8 | Console.Out + UTF-8 출력 | n/a |

#### (c) 외부 호출과 대체 방식

- numpy(asarray, full, where, maximum, minimum, clip, isfinite, arange, astype, copy, 불리언 sum·any·all, max, min(axis=1), `**`)는 C# 루프와 `Bpcg.Numerics`의 `NpMath.PowScalar`·`NpReduce.Max`로 옮깁니다. 이 두 파일에는 실수 합 축약이 없습니다. 합은 모두 불리언 개수라 정확합니다.
- `numba.config.NUMBA_NUM_THREADS`와 `numba.set_num_threads`는 `Environment.ProcessorCount`와 전역 `ParallelOptions.MaxDegreeOfParallelism`으로 옮깁니다.
- `time.perf_counter`는 `Stopwatch.GetTimestamp`·`GetElapsedTime`으로 옮깁니다.
- `argparse`는 손으로 짠 부분 집합 파서로 옮기길 권합니다(System.CommandLine은 MIT라 대안). `sys.exit`·`SystemExit`는 `Main`의 반환값과 `CliExit`로 옮깁니다.
- `pathlib.Path`는 `Bpcg.IO.PyPath`와 `File.Exists`로, `shutil.rmtree`는 `Directory.Delete(path, true)`로, `print(flush=True)`는 `Console.Out`(AutoFlush, UTF-8)으로 옮깁니다.
- 저장소 함수는 각 모듈의 C# 판을 부릅니다:
  - core: Config, load_config, checked_overrides, parse_assignment, check_fields, sphere_graph, ROOT·OUT
  - planet: build_materials, transfer_materials, latitude_rad, surface_temperature
  - geology: generate_geology, surface_rock, LayerColumns
  - hydro: fill_epsilon, d8_receivers, topo_order, accumulate, river_segments
  - landscape: solve_steady_state, add_fans, subgrid_relief
  - subsurface: water_bodies, valley_depth, soil_and_alluvium, water_table, cave_levels
  - metrics: scorecard
  - hero: find_hero, refine_hero, flat_hero
  - bake: save·load_planet_state, save·load_hero_state, write_json, read_manifest, config_from_manifest, face_textures, bake_corridor, bake_globe, hero_from_run
- 실수 표기는 `PyJson`(manifest·scorecard.json)과 `PyFormat`(콘솔)이 Python과 같은 문자열을 냅니다. 둘 다 InvariantCulture를 씁니다.

#### (d) numba 커널과 prange

두 파일에는 `@njit`도 prange도 없습니다. `apply_threads`가 다른 모듈 prange 커널의 스레드 수를 정하므로, C#에서는 `Pipeline.ApplyThreads`가 전역 최대 병렬도를 정하고 모든 `Parallel.For`가 그 값을 씁니다. tiny에서 numba 스레드를 1·3·12로 바꿔도 행성 전 필드가 비트 단위로 같았고, 히어로도 1·12에서 같았습니다. 그래서 C# 대조 시험에 '최대 병렬도 1과 기본값의 결과가 같다'는 조건을 넣습니다. 단독 `bake` 명령은 Python에서 `apply_threads`를 부르지 않습니다(의심 버그 B6).

#### (e) 의심 버그

| 위치 | 내용 | 근거 | 영향 |
|---|---|---|---|
| pipeline.py:666-673 (B1) | 점수표를 cfg_l0가 아닌 cfg로 불러, L0 law_consistency가 솔버가 쓰지 않은 G=1 법칙과 비교합니다 | tiny: 2.4879727678775813e-07(cfg), 5.386731577109604e-16(cfg_l0) | 보고값만 틀리고 합격선은 통과. 재현합니다 |
| cli.py:258-266 (B2) | bake --out을 써도 globe는 `<run>/corridor/manifest.json`에서 히어로 자리를 찾아, 옛 회랑을 읽을 수 있습니다 | globe.py:330-347이 corridor를 hero보다 먼저 봄(코드 읽기) | 낮음 |
| cli.py:75-78 (B3) | --planet에 TOML 경로를 주면 `OUT/<경로>`가 실행 폴더가 되고, 절대 경로면 TOML 경로 자체가 실행 폴더가 됩니다 | pathlib 결합 결과 확인 | 낮음 |
| cli.py:285-289 (B4) | ValueError를 모두 '자리를 찾지 못해'로 보고 평면 히어로로 바꿔, 솔버 내부 오류도 가립니다 | solver.py:530-531, routing.py:224-227도 ValueError를 냄 | 중간(디버깅) |
| docs/pipeline.md:545 (B5) | `pipeline.bake(...)`가 문서에만 있고 코드에는 없습니다 | grep | 낮음, C# API 결정 필요 |
| cli.py:258-269 (B6) | 단독 bake는 `apply_threads`를 부르지 않아 numba_threads를 무시합니다 | 코드 읽기 | 성능에만 영향 |

#### (f) golden 대조 계획

| 대상 | 입력 | 출력 | 크기 | 비교 |
|---|---|---|---|---|
| `RerouteAfterFans` ① | tiny 히어로 호출 지점을 `mock.patch.object(bpcg.pipeline, 'reroute_after_fans', wraps=…)`로 포착 | receiver, order, Q, A↑, Qs, slope | 약 500 KB | exact |
| `RerouteAfterFans` ② | test_pipeline의 손 사례(flat_graph 40², jitter 0.4, seed 3, 원뿔), 바뀐 칸이 많음 | 같음 | 약 150 KB | exact |
| `TaperUplift` | tiny 첫 풀이의 U·z_mean과 손 사례(z_mean 0·-0.0·음수·z_lim·초과, power 4·3·2·0.5·1·-1) | U′, reduced_cells, 최댓값 | 약 160 KB | exact(macOS) |
| `StrengthLimitedUplift` | tiny 행성 호출 지점(graph, ocean, fields, columns, cfg_l0) | U′, z_start, diag(seconds 뺌) | 약 600 KB | 정수 exact, 실수는 솔버·기복 기준 |
| 작은 함수 5개 | L0Config·SolverMaxIter·ApplyThreads·Every·ScorecardSummary 손 사례 표 + tiny scorecard.json | 설정 JSON·digest, 정수, 줄 번호, 요약 | 각 20 KB 미만(커밋) | exact |
| `RunStages2To4` | 호출 지점 3곳 포착: 행성(구면, 수심, z_init), refine 히어로(평면, inflow, lat_deg), 평면 히어로(t_sea 15). refine·flat은 `bpcg.hero.refine`·`bpcg.hero.flat`의 이름을 감쌈 | fields, rivers, fan_apexes, diag(seconds 뺌)와 키 순서 | 2 MB, 1.4 MB, 1.4 MB | 정수·bool exact, 실수는 callee 기준 |
| `GeneratePlanet` | tiny, seed 0과 3 | 메모리 fields, 저장 묶음, info, diag(seconds·threads 뺌) | 각 약 3 MB | 위와 같음 |
| `GenerateHero` | ① 다시 읽은 tiny 행성(CLI 경로) ② planet = null | fields, site, boundary, rivers, fan_apexes | 각 약 1.5 MB | 위와 같음, 칸 번호 exact |
| `Commands.ParseArgs` | 검증한 argv 25개(약어, 모호, 음수, '1_000', 큰 정수, --set=, 필수 누락) | 파싱 값 또는 종료 2 | 10 KB 미만(커밋) | exact |
| `ConfigFromArgs`·`SetOverrides` | tiny + --set 2개 + --seed 3, 오류 5종 | 설정 JSON·digest, [설정] 줄, 문구, 종료 코드 | 30 KB 미만 | exact |
| `BakeConfig` | config.detail을 지운 히어로 manifest 사본, `--set detail.fractal_gain=1.0`, 접두가 틀린 키 | 설정 JSON·digest, 메시지, 문구 | 50 KB 미만 | exact |
| `PyPath` | 'out/x/', 'hero', '.', './a//b/', 'a/..', '//a', '' 등 | 정규화, parent, 결합 | 1 KB | exact |
| CLI 끝에서 끝 | `all --profile tiny`, `hero --flat --seed 3` → bake | 파일 목록, manifest(git_commit·seconds·threads 뺌), 콘솔 태그 순서 | 단계 3 | 구조 exact |

golden은 `out/golden/`에 쓰고, 1 MB 미만의 손 사례만 커밋합니다. 히어로 golden은 반드시 다시 읽은 행성으로 만듭니다.

#### (g) 포팅 순서와 위험 메모

- **단계 1(core와 함께):**
  - `PyDict`, `PyJson`(Python repr·표기, NaN → null), `PyFormat`, `PyPath`, `NpMath.PowScalar`, `NpReduce.Max`를 만듭니다.
  - 전용 ValueError 예외형을 둡니다.
  - Config에 순서 있는 `WithOverrides`와 Python int()·float() 의미의 읽기를 둡니다.
  - CS0542 이름 규칙을 정합니다.
- **단계 2 pipeline·hero 차례:**
  1. 작은 함수: `L0Config`, `SolverMaxIter`, `ApplyThreads`, `Every`, `ScorecardSummary`, `TaperUplift`, `RerouteAfterFans`
  2. `RunStages2To4`(callee 모듈이 모두 대조를 통과한 뒤)
  3. `StrengthLimitedUplift` → `GeneratePlanet`
  4. hero(Finder·Domain·Refine·Flat) → `GenerateHero`
- **cli는 지시 순서대로 마지막에 옮깁니다.** 다만 `ParseArgs`와 `PyPath`는 먼저 만들어도 됩니다.
- **위험 1:** `** power`를 `Math.Pow`로 그대로 옮기면, 설정이 2.0·0.5일 때 numpy와 0.1% 칸이 다릅니다. 반드시 `PowScalar`를 씁니다.
- **위험 2:** 히어로는 디스크 왕복 행성으로 만들어야 합니다. 메모리 행성과는 23개 필드가 다릅니다.
- **위험 3:** 점수표는 원래 cfg로, 솔버는 cfg_l0로 부릅니다. 바꾸면 law_consistency 값이 달라집니다.
- **위험 4:** diag·info의 키 순서와 int·float·bool 형은 manifest 문자열과 studio/summary.py가 읽는 계약입니다.
- **위험 5:** ValueError 형이 맞지 않으면 `all`의 평면 히어로 대체 흐름이 달라집니다.
- **위험 6:** 경로 parent 규칙을 틀리면 bake의 corridor·globe 위치가 달라집니다.
- **위험 7:** 비트 일치는 macOS arm64(같은 libm)를 기준으로 하고, 다른 플랫폼은 허용 오차 모드로 돌리는 방안을 결정해야 합니다.

### hero — domain·finder·flat·refine

대상은 `src/bpcg/hero/` 5개 파일(1,010줄)입니다. hero 안에서 직접 정의한 numba 커널은 없습니다. 위험은 세 곳에 몰려 있습니다.

- `find_hero` 는 백분위 정규화, kd-tree 개수, 8칸 pairwise 합으로 만든 점수의 argmax 한 번으로 히어로 자리를 정합니다. 부분 점수의 비트 하나가 동점을 가르면 히어로 전체가 달라집니다.
- 이 기계의 scipy 1.18.1 휠은 `brentq`, `RegularGridInterpolator`(2차원 선형), `cKDTree` 잎 거리를 FMA 로 컴파일해 두었습니다(역어셈블로 확인). 비트 단위로 맞추려면 C# 도 같은 자리에서 `Math.FusedMultiplyAdd` 를 써야 합니다.
- CLI(`hero --from`, `all`)는 float32 로 저장한 행성 묶음을 다시 읽어 float64 로 바꾼 값으로 히어로를 풉니다. 메모리의 행성으로 풀면 z_outlet 부터 달라집니다.

아래의 '확인'은 이 기계(macOS 26.6.2 arm64, Python 3.13.15, numpy 2.5.3, scipy 1.18.1)에서 한 실험입니다. tiny 입력은 `out/tiny_check/planet` 묶음입니다.

#### 대응표

| Python | C# 파일·형 | 멤버 → C# 시그니처 |
|---|---|---|
| `hero/__init__.py` (16줄) | 없음 | `HeroSite`, `find_hero`, `flat_hero`, `hero_graph`, `refine_hero` 를 다시 내보내기만 합니다. 호출부는 `Bpcg.Hero.Finder.FindHero` 처럼 직접 부릅니다 |
| `hero/domain.py` (200줄) | `Hero/Domain.cs`: `static class Domain`, `readonly record struct EdgeHit(string Edge, int Index, double X, double Y)` | `StreamHeroJitter = 9101`(private const long), `Edges`, `MaxHeroCells = 25_000_000L`; `(int N, double Spacing) HeroGridSize(Config)`; `OrderedDictionary<string,object> HeroGridInfo(Config)`; `long HeroJitterSeed(Config)`; `CellGraph HeroFlatGraph(Config)`; `(double[] East, double[] North) TangentFrame(ReadOnlySpan<double> center, ReadOnlySpan<double> axis)`; `double[] LocalToUnit(HeroSite, ReadOnlySpan<double> x, ReadOnlySpan<double> y, double radiusM)`(M×3 행 우선); `(double East, double North) DirectionToLocal(HeroSite, ReadOnlySpan<double> v)`; `(CellGraph Graph, double[] UnitPoints) HeroGraph(HeroSite, Config)`; `EdgeHit EdgeHit((double Dx, double Dy) dir, int n, double spacing)`; `long[] EdgeCells(int n, string edge, long index, int count = 1)` |
| `hero/finder.py` (257줄) | `Hero/Finder.cs`: `static class Finder`, `sealed class HeroSite` | `ScoreWeights`((string, double)[], 순서 고정), `MaxLatDeg`, `DryRadiusM`, `DryAridityIndex`, `NormalizePercentile`, `CarbonateSamples`(int), `OceanMarginCells`, private `Required`; `HeroSite { double[] CenterUnit, East, North; int L0Cell; double Score; OrderedDictionary<string,double> Parts; double LatDeg = NaN, LonDeg = NaN }`; private `CheckFields(PlanetState)`; `double[] UpliftGradient(CellGraph, ReadOnlySpan<double> uplift)`; `double[] CarbonateFraction(ReadOnlySpan<double> strataBottom, ReadOnlySpan<byte> strataRock, int nLayers, ReadOnlySpan<double> z, ReadOnlySpan<double> relief)`; `double[] DryFraction(CellGraph, ReadOnlySpan<bool> isDry, ReadOnlySpan<long> cells, double radiusM)`; internal `double[] Normalize(ReadOnlySpan<double> x, ReadOnlySpan<bool> mask)`; `bool[] CandidateMask(PlanetState, Config)`; `(double[] Total, OrderedDictionary<string,double[]> Parts, bool[] Candidate) ScoreCells(PlanetState, Config)`; `HeroSite SiteFromCell(PlanetState, int cell, Config, double score = NaN, IReadOnlyDictionary<string,double>? parts = null)`; `HeroSite FindHero(PlanetState, Config)` |
| `hero/flat.py` (270줄) | `Hero/Flat.cs`: `static class Flat` | `UBaseMPerYr`, `UPeakMPerYr`, `UCenterFraction`, `UWidthFraction`, `TemplateId`(byte = `Model.FoldThrust`), `RunoffMPerYr`, `TemperatureSeaC`, `OutletCells`, `OutletZM`, `PresolveCoarsen`, `PresolveMinCells`; `double[] FlatUplift(ReadOnlySpan<double> yFromNorth, double lengthM)`; `double PrecipForRunoff(double runoff, double pet)`(안쪽 `Gap` 은 private); `double ErosionPeriodYr(ReadOnlySpan<double> uplift)`; `double[] PresolveSurface(Config, ReadOnlySpan<double> x, ReadOnlySpan<double> y)`; `double[] FlatExhumation(ReadOnlySpan<double> uplift, ReadOnlySpan<double> zPre)`; `HeroState FlatHero(Config, Action<string>? log = null)` |
| `hero/refine.py` (267줄) | `Hero/Refine.cs`: `static class Refine`, `sealed record HeroBoundaryResult` | `OutletCells = 5`, `LinearFields`(7개, 순서 고정), `NearestFields`; private `Say`; `OrderedDictionary<string, Array> SampleL0Fields(PlanetState, ReadOnlySpan<double> unit)`(double[]×7, byte[] template_id); internal `double UMax(PlanetState)`; `HeroBoundaryResult HeroBoundary(PlanetState, HeroSite, CellGraph, ReadOnlySpan<double> rEff, Config)`(Python dict 키 순서의 속성 + `ToDiag()`); `HeroState RefineHero(PlanetState, HeroSite, Config, Action<string>? log = null)` |

`HeroState`(`Pipeline.cs` 의 `Bpcg.HeroState`) 필드는 두 경로가 다릅니다.

- refine: `LinearFields` 7개 + `template_id`(byte) + `is_ocean`(bool, 모두 false) + 2~4단계 28개입니다. `uplift_m_per_yr` 는 2~4단계 값으로 덮이지만 자리는 첫 자리를 유지합니다.
- flat: 입력 10개(`dist_convergent_m` 포함) + 2~4단계 28개입니다.

계산 중에는 double·byte·bool·long(receiver)을 쓰고, 저장할 때만 FIELDS dtype 으로 바꿉니다.

#### 수치 함정 표

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| domain.py:55 | 반올림 | `int(round(size/dx))` 는 짝수 쪽 반올림(64.5→64, 65.5→66) | `Math.Round` 기본(ToEven) | exact (확인) |
| domain.py:56 | 정수 넘침 | Python 정수는 넘치지 않음. C# `(long)Math.Round(1e300)` 은 포화되고 `n*n` 이 넘쳐 상한 검사를 통과함 | double 로 `nd > 5000` 을 먼저 검사한 뒤 int 로 | exact (확인: 1e300 → ValueError) |
| domain.py:49–54 | 설정 형 | TOML 정수도 float 로 받음, NaN 은 부정형 조건에서 오류 | Toml 래퍼에서 double 로, 같은 부정형 | exact (확인) |
| domain.py:79 | uint64 해시 | 음수 시드는 2의 보수, 2^63 이상은 OverflowError | `Hash3(unchecked((ulong)seed), 9101, 0) >> 33`, `_sub_seed` 와 공용 `SubSeed` | exact (확인: 0→1738104521, −1→1840119892) |
| domain.py:110–122, finder.py:220–229 | dot·norm·cross | 1차원 norm 은 sqrt(dot)이고 dot 은 Accelerate 경로지만, 길이 3 은 (a0b0+a1b1)+a2b2 비FMA 와 같음. cross 는 곱 두 번 뒤 뺄셈 | `LinAlg.Dot3`·`Norm3`·`Cross3` 순차 비FMA | exact (확인: 2만 쌍 100%) |
| domain.py:118, finder.py:226 | argmin 동점 | 같은 값이면 앞 번호(축 z → x 축) | `<` 엄격 비교 | exact (확인) |
| domain.py:114–122 | 두 번 정규화 | 정규화한 center 를 다시 \|c\| 로 나눔(비트가 바뀔 수 있음) | 생략하지 않음 | exact (확인: 879칸) |
| domain.py:134–139 | 성분 순서 | (R·c + x·e) + y·n, norm(axis=1) 은 순차 | 같은 식 | exact (확인: 4096점) |
| domain.py:168 | max 와 NaN | Python `max(1, NaN) = 1` 이라 (1, NaN) 방향이 south·63·y=NaN 을 냄 | `\|dy\| > \|dx\| ? \|dy\| : \|dx\|` | exact (확인) |
| domain.py:174–179 | 동점·floor | \|dx\| ≥ \|dy\| 이면 동·서. floor 후 clamp. Python floor(NaN) 은 예외 | 같은 비교, 유한성 확인 후 (int) | exact (확인) |
| finder.py:98–102 | 8칸 pairwise | `(g*g).sum(axis=1)` = ((a0+a1)+(a2+a3))+((a4+a5)+(a6+a7)), 빈 슬롯은 0.0 자리 | 8칸 고정식 + `Math.Sqrt((2.0*s)/cnt)` | exact (확인: 순차는 57%만 같음) |
| finder.py:114, 188 | nan_to_num | NaN→0, ±inf→±1.7976931348623157e308, −0.0 유지 | `NpMath.NanToNum` | exact (확인) |
| finder.py:114 외 | maximum | NaN 전파, (−0.0, 0.0) → +0.0 | `Math.Max`(IEEE 754-2019) | exact (numpy 는 확인, .NET 은 문서 근거) |
| finder.py:115–118 | 정확한 비율 | k=(i+0.5)/16, zz=z+r·k, mean(bool)=개수/16 | `count / 16.0`, `Geology.Model.RockAt` | exact |
| finder.py:129 | libm sin | chord = (2R)·sin(min((0.5s)/R, π/2)) | `Math.Sin`(맥은 같은 libm) | exact (tiny 여유 5.0e-4) |
| finder.py:135, 139 | kd-tree 규칙 | 노드는 비FMA 직사각형 거리로 거르고, 잎은 fma(dz,dz,fma(dy,dy,dx·dx)) ≤ r² 로 셈 | `KdTree.QueryBallPointCount` + 4ulp 여유 검사 | exact (역어셈블 + 300/300) |
| finder.py:150–155 | 백분위 | q=0.99, vi=(n−1)q, t ≥ 0.5 이면 b−(b−a)(1−t). NaN 이면 max(NaN) → 모두 0. clip 은 NaN·−0.0 을 그대로 둠 | `NpStats.Percentile`, `NpReduce.Max`, `Math.Clamp` | exact (확인) |
| finder.py:171 | 도→라디안 | `math.radians` = x·(π/180). .NET 내장은 (x·π)/180 이라 28% 에서 다름 | `PyMath.Radians` | exact (확인) |
| finder.py:163–171 | 문턱 | \|lat\| ≤ 0.9599…(asin 은 libm), d_ocean > half_diag + 2·spacing | 같은 왼쪽 결합식 | exact (여유 1.6e-4 rad, 6.7e-5) |
| finder.py:207–210 | 합 순서 | 0.3·ug → 0.4·carb → 0.15·rel → 0.15·dry 순으로 누적 | 가중치 배열 순서 | exact (다른 순서면 10칸 다름) |
| finder.py:254, refine.py:132 | argmax | 첫 최댓값, NaN 이 있으면 첫 NaN. 1.0 잘림·k/16 때문에 동점 가능 | `NpSort.ArgMax` | exact |
| finder.py:224, 229 | 라디안→도 | `math.degrees` = x·(180/π). .NET 내장은 (x·180)/π 라 25% 에서 다름 | `PyMath.Degrees`, `Math.Asin`·`Atan2` | tolerance 2ulp (맥에서는 비트 일치 기대) |
| flat.py:70–72 | 제곱·exp | 배열 `**2` 는 x·x. libm `pow(x,2)` 는 0.13% 에서 다름 | `t*t`, `Math.Exp`, Math.Pow 금지 | tolerance rel 1e-15 (맥에서는 비트 일치) |
| flat.py:85–86 | numpy tanh | arm64 np.tanh 는 자체 NEON 구현이라 libm 과 21% 에서 1ulp 차이 | `Math.Tanh` 또는 numpy 알고리즘 이식 | tolerance (아래 brentq 기준) |
| flat.py:91 | brentq FMA | 맥 휠 brentq.c 에 fma 3곳(delta, 외삽 분자, 3\|sbis\|−delta) | `Numerics.Brent.BrentQ` 를 한 줄씩 옮기고 FMA 3곳 | exact (pet 5개 비트 일치) |
| flat.py:154–156 | 식 순서 | pet = 0.055·15 + 0.1 = 0.9249999999999999 | 같은 순서 | exact |
| flat.py:110–120 | 격자 규칙 | n_c = max(16, n//4). n < 64 이면 히어로 격자보다 곱거나 같음. 미리 풀기 반복 상한은 landscape 값 | 같은 정수식 | exact (확인) |
| flat.py:121–125 | RGI FMA | r=(v00·a)·b; fma(v01·a,y1,r); fma(v10·y0,b,r); fma(v11·y0,y1,r). 범위 밖은 선형 외삽, 점은 (y, x) | `RegularGridInterpolator.Linear2D` | exact (4096/4096 일치, 비FMA 는 1036점 다름) |
| flat.py:178 | 단위 벡터 아님 | unit_points 는 pos/R 이고, geology 가 ·(R/λ) 로 다시 반올림 | pos/R 배열을 그대로 넘김 | exact |
| refine.py:74–79, 117 | 구면 표본 | `sample_sphere` linear·nearest. z_out 의 비트가 솔버 결과 전체를 가름 | `Core.Resample.SampleSphere` | exact |
| refine.py:124 | pairwise 합 | `np.sum(area·R_eff)` 는 조각 없는 순수 pairwise. q_in > 0 분기에 쓰임 | `NpReduce.Sum` | exact (1.6M 원소까지 확인) |
| refine.py:128–133, 154 | 기여 셀·감시값 | 오름차순 donors 에서 첫 최대 Q. −1 은 단락 평가로 색인되지 않음 | `ArgMax`, `&&` | exact |
| refine.py:83–88 | JSON 왕복 | u_max 는 meta JSON 값을 우선, 없으면 필드 max(빈 배열이면 ValueError) | 정확 파싱, ValueError 형 | exact |
| refine.py:107, cli.py:127 | 묶음 왕복 | CLI 는 float32 로 저장한 필드를 float64 로 읽어 풂(z_out 222.722040655585 vs 메모리 222.72204679667882) | C# CLI 도 쓰기→읽기, golden 입력은 load 뒤에서 잡음 | exact (확인) |
| refine.py:214 | 각 왕복 | lat_deg = degrees(asin) 를 run_stages 가 radians 로 되돌림 | `PyMath` 두 식 | tolerance (기온은 sin 을 거침) |
| flat.py:216–228, refine.py:223–258 | 사전 순서 | `{**a, **b}` 는 겹친 키의 자리를 유지. diag·boundary 키 순서와 1024 원소 이하 배열이 meta JSON 에 남음 | `OrderedDictionary`, PyJson jsonable 규칙 | exact |
| 여러 곳 | 예외 | ValueError(numpy·scipy 포함)는 `all` 이 잡아 평면 히어로로 바꿈. brentq 미수렴은 RuntimeError | 예외형 하나로 통일 | n/a |
| 여러 곳 | 시간·서식 | diag.seconds, 로그 f-string(`:.3g` → `4e+10`) | 대조에서 뺌 | n/a |
| hero 전체 | NEP 50 | 실수 연산은 모두 float64(입력을 먼저 float64 로 바꿈). np.empty·중복 색인 대입·음수 //·float % 는 없음 | double 계산 | n/a (dtype 확인) |

#### 외부 호출과 대체 방식

| 외부 호출 | 위치 | C# 대체 | 맞출 점 |
|---|---|---|---|
| `cKDTree(pos).query_ball_point(q, chord, return_length=True, workers=-1)` | finder.py:135, 139 | `Numerics.KdTree.QueryBallPointCount` | p=2, eps=0, `<=`. 잎은 FMA, 노드 가지치기는 비FMA 직사각형 거리. leafsize 16·balanced 기본값. 질의별 병렬 가능 |
| `brentq(gap, lo, hi, xtol=1e-12, rtol=1e-12)` | flat.py:91 | `Numerics.Brent.BrentQ` | brentq.c 를 그대로, FMA 3곳, maxiter 100, 래퍼 검사(xtol>0, rtol≥4ε), NaN·부호 ValueError, 미수렴 RuntimeError |
| `RegularGridInterpolator((ys, xs), img, bounds_error=False, fill_value=None)` | flat.py:124 (landscape/warmstart.py 도 씀) | `Numerics.RegularGridInterpolator.Linear2D` | find_interval_ascending 규칙(x[i] ≤ v < x[i+1], 끝값 n−2), 외삽, FMA 식, NaN → NaN |
| `np.percentile(·, 99.0)` | finder.py:150 | `NpStats.Percentile` | linear, NaN 이면 NaN |
| `np.sum`, `(N,8).sum(axis=1)`, `norm(axis=1)` | refine.py:124, finder.py:102, domain.py:139 | `NpReduce` | 1차원 pairwise, 8칸 나무, 3칸 순차 |
| 1차원 `np.linalg.norm`·`@`·`np.cross` | domain·finder | `LinAlg.Dot3/Norm3/Cross3` | 순차 비FMA |
| `np.argmax`·`np.argmin`·`np.flatnonzero` | finder·refine·domain | `NpSort.ArgMax/ArgMin`, 오름차순 수집 | 첫 값, NaN 우선 |
| `np.exp`, budyko 의 `np.tanh`, `math.sin/asin/atan2/degrees/radians` | flat·finder | `Math.*`, `PyMath.Degrees/Radians` | 맥에서는 tanh 를 뺀 나머지가 libm 과 같음 |
| `time.perf_counter` | flat·refine | `Stopwatch` | 대조에서 제외 |
| bpcg 내부 | 전체 | core(`Graph.FlatGraph`, `Hashing.Hash3`, `Distance.NearestSource`, `Resample.SampleSphere`), geology(`Model.RockAt/BuildColumns/FoldDisplacement/FoldDisplacementFromPhase`, `Rocks.Soluble`), planet(`Climate.LatitudeRad/BudykoRunoff`), landscape(`Solver.SolveSteadyState`, `Warmstart.CoarseWarmStart`), pipeline(`RunStages2To4`, `Score`, `ScorecardSummary`, `SolverMaxIter`) | 각 묶음의 golden 이 먼저 통과해야 함 |

#### numba 커널과 prange

hero 4개 모듈에는 `@njit` 와 `prange` 가 없습니다. 부르는 커널은 모두 다른 묶음 소속입니다.

- `core.hashing.hash3` 는 Python 에서 `np.int64` 인자로 직접 부릅니다. ulong 연산이므로 unchecked 로 옮깁니다.
- `geology.model._rock_at_kernel`(prange, `carbonate_fraction`)은 칸마다 자기 출력만 씁니다. Parallel.For 로 옮겨도 되고 축약 변수가 없습니다.
- `core.resample._bilinear_kernel`(prange, `sample_l0_fields`·`hero_boundary`)은 점마다 자기 출력만 씁니다. Parallel.For 로 옮겨도 됩니다.
- `core.graph.hash_uniform_array`(평면 그래프 흔들기)는 순차 루프입니다.
- `cKDTree` 의 `workers=-1` 은 질의마다 개수를 따로 셉니다. C# 에서 질의 단위 Parallel.For 를 써도 결과가 바뀌지 않습니다.
- `flat_hero`·`refine_hero` 가 부르는 솔버·노이즈·2~4단계 커널은 각 묶음 보고서를 따릅니다.

#### 의심 버그

1. **습곡 변위의 U 기준이 섞입니다** (`refine.py:83-88, 180-182`, `pipeline.py:632-661`).
    - 내용: 히어로 변위 Δ = A·(U/U_max)·sin φ 의 U 는 지각 세기 한계로 줄인 유효 융기입니다. 행성 필드가 2~4단계 결과로 덮이기 때문입니다. 반면 U_max 는 줄이기 전 융기로 만든 `diag['geology']['u_max_m_per_yr']` 입니다. L0 기둥은 줄이기 전 U 로 변위를 만들었습니다.
    - 근거(tiny): diag u_max 는 0.001655391227270644 이고, 저장된 유효 U 의 최댓값은 0.00022793628158979118 입니다. 습곡충상대 L0 234칸에서 L0 저장 변위와 히어로식 변위의 차는 최대 264.8 m 입니다. 선택된 칸 2721 에서는 128.38 m 와 117.50 m 입니다.
    - 영향: 히어로 지표 암석과 동굴 층이 L0 의 예측과 어긋납니다. finder 의 탄산염 점수(L0 기둥 기준)가 히어로 지질을 대표하지 못할 수 있습니다. 포팅은 그대로 재현합니다.
2. (영향 없음) `edge_hit` 의 유한성 검사는 Python `max` 의 비대칭 때문에 북 성분 NaN 을 놓칩니다. (1, NaN) 을 넣으면 south·63·y=NaN 이 나옵니다. 실제 입력에서는 도달하지 않습니다. C# 은 같은 동작을 재현합니다.

#### golden 대조 계획

입력 열에서 'tiny' 는 `load_planet_state` 로 읽은 tiny 묶음 행성에서 호출 지점의 값을 잡은 것이고, '손' 은 경계 규칙을 덮으려고 손으로 만든 사례입니다.

| 대상 | 입력 | 출력 | 크기 | 비교 |
|---|---|---|---|---|
| `Domain.HeroGridSize`·`HeroGridInfo` | 손: (6400,100), (6450,100)→64, (6550,100)→66, (6460,100), (300,100), (500000,100), TOML 정수. 오류: (299.9999,100), (500100,100), (1e300,1), (6400,0) | n, 간격, info(형 포함), 오류 | <1 KB | exact |
| `Domain.HeroJitterSeed` | 손: 시드 0, 1, −1, 12345, 2^31, 2^62, 2^63−1 | 정수 | <1 KB | exact |
| `Domain.HeroFlatGraph` | tiny 설정, size 1500 설정 | pos·nbr·dist·area·spacing·origin | ~0.6 MB | exact |
| `Domain.TangentFrame` | 손: 극·극 근처·적도 × 축 4개, 무작위 1000개, 오류 | east, north | ~100 KB | exact |
| `Domain.LocalToUnit`·`HeroGraph`·`DirectionToLocal` | tiny: site(칸 2721)와 히어로 그래프. 손: 무작위 벡터 1000개 | unit(4096×3), (동, 북) | ~110 KB | exact |
| `Domain.EdgeHit`·`EdgeCells` | 손: 축·대각·−0.0·1e−300·NaN·inf·0 방향, 무작위 2000개 × n{3,15,64,65,256} × 간격{25,50,100}. 모든 가장자리 × 경계 index × count | edge, index, x, y / long[] / 오류 | ~0.7 MB | exact |
| `Finder.UpliftGradient` | tiny: L0 nbr·dist·U. 손: 평면 32² 선형장, NaN 한 칸 | grad | ~60 KB | exact |
| `Finder.CarbonateFraction` | tiny: 후보 부분집합. 손: 시험의 PLATFORM 기둥, relief 0·음수·NaN | 비율 | ~70 KB | exact |
| `Finder.DryFraction`(+`KdTree`) | tiny: pos·is_dry·후보·200 km. 손: 평면 10², 거리 = 반경인 점, 경계에 걸친 구면 쌍 300개, 빈 입력 | 비율, 개수, 최소 여유 | ~200 KB | exact |
| `Finder.Normalize` | 손: 빈 mask, 모두 0, p99=0, NaN, −0.0, inf, n=1·2·101. tiny: grad·relief | 배열 | <100 KB | exact |
| `Finder.CandidateMask`·`ScoreCells`·`SiteFromCell`·`FindHero` | tiny 행성. 손: 합성 행성(hot cell, 극 hot cell, 점수가 모두 0 이라 동점, 모두 바다라 오류), 기울어진 축, 범위 밖 칸 | cand, total, parts, site | ~300 KB | exact (lat·lon 은 2ulp) |
| `Flat.FlatUplift`·`ErosionPeriodYr`·`FlatExhumation` | tiny: y, U, z_pre. 손: y·L 경계, U≤0, NaN | U, T_e, exh | ~70 KB | U 는 rel 1e-15, 나머지 exact |
| `Flat.PrecipForRunoff`·`Brent.BrentQ` | 손: pet {0.1, 0.3, 0.9249999999999999, 2, 5}, 오류 2개, 세 분기를 모두 지나는 다항식 | 근, 반복 수 | <1 KB | BrentQ 는 exact, PrecipForRunoff 는 rel 4e-12 |
| `RegularGridInterpolator.Linear2D` | tiny: presolve 의 res.z(16²), 히어로 x·y(외삽 496점). 손: 무작위 비균등 격자, 끝값·NaN | 보간값 | ~80 KB | exact |
| `Flat.FlatHero`(경계조건 부분) | tiny 설정, size 1500 설정 | boundary diag, U, exh, d_conv, tid, runoff_eff, pos/R | ~200 KB | exact |
| `Refine.SampleL0Fields`·`UMax` | tiny: 행성, unit(4096×3). 손: u_max 키가 있음·없음, 유한 U 없음 | 표본 8개, u_max | ~260 KB | exact |
| `Refine.HeroBoundary` | tiny: 기여 셀 없음, east 유입. 손: sphere_graph(8) 합성 — donors Q 다름·동점, q_in ≤ 0, 수신 셀 = 자기(오류), 출구와 겹침 | boundary 전체 | ~50 KB | exact |
| `Refine.RefineHero`(2~4단계 전) | tiny: 행성 + site | tid, disp, strata, is_outlet, extra_inflow, z_outlet | ~300 KB | exact |

행성 입력 배열은 planet 단계 golden 의 tiny 묶음을 공유하고, 파일 하나당 1 MB 를 넘지 않게 나눕니다. golden 메타에는 동점 여유(tiny 1위와 2위 차 0.2), kd-tree 최소 여유(5.0e-4), 위도·바다 거리 문턱 여유를 함께 적습니다.

#### 포팅 순서와 위험 메모

1. `Domain` 을 먼저 옮깁니다. 의존은 `Core.Graph`·`Core.Hashing` 뿐이라 golden 으로 바로 닫을 수 있습니다.
2. `Finder` 를 옮깁니다. `Core.Distance`, `Geology.Model.RockAt`, `Planet.Climate.LatitudeRad`, `Numerics.KdTree`·`NpStats`·`NpReduce`·`NpSort` 가 먼저 필요합니다. 부분 점수 golden 을 모두 비트로 맞춘 뒤에 `FindHero` 를 봅니다.
3. `Flat` 을 옮깁니다. `Numerics.Brent`·`RegularGridInterpolator`, `Planet.Climate.BudykoRunoff`, geology, `Landscape.Solver`·`Warmstart`, `Pipeline.RunStages2To4` 가 필요합니다.
4. `Refine` 을 옮깁니다. 추가로 `Core.Resample.SampleSphere` 가 필요합니다. Flat 과 Refine 은 Pipeline 과 서로 부르므로 단계 2 의 'pipeline·hero' 차례에 함께 옮깁니다.

위험 메모는 다음과 같습니다.

- FMA 허용 여부가 정해지기 전에는 Brent·RGI·KdTree 를 비트로 맞출 수 없습니다. RGI 는 landscape.warmstart 와 같이 씁니다.
- golden 은 이 기계(macOS arm64, numpy 2.5.3, scipy 1.18.1)에서만 만듭니다. Accelerate, NEON tanh, scipy 의 FMA 축약이 플랫폼마다 다르기 때문입니다.
- CLI 의 평면 히어로 대체는 ValueError 에만 걸립니다. 따라서 C# 예외형 규칙이 Refine 보다 먼저 정해져야 합니다.
- 메모리 행성과 묶음 왕복 행성은 히어로 결과가 다릅니다. golden 과 C# 시험의 입력 출처(CLI 경로)를 명시합니다.
- 의심 버그 1 은 고치지 않고 재현합니다. Python 과 같은 수치가 나오는지는 `Refine.RefineHero` golden 의 disp 로 확인합니다.

### volume — sample·slices

범위는 `src/bpcg/volume/`의 `__init__.py`(12줄), `sample.py`(1,044줄), `slices.py`(95줄)입니다. 단계 0에서 numba 커널 4개와 numpy 부분 전체를 'C# 반복문과 같은 모양'으로 Python에서 다시 짰습니다. 조건은 FMA 없는 산술, Python식 min/max, 같은 괄호 순서입니다. 두 히어로에서 결과가 원본과 비트 단위로 같았습니다.

- 히어로 A는 `load_hero_state("out/tiny_check/hero")`, 곧 굽기 경로이고 64×64에 강 1구간입니다.
- 히어로 B는 `tests/test_volume.py`의 `HERO_OVERRIDES` 히어로이고 강 21구간, 합류점 6곳, 동굴 두 층이 있습니다.

이 묶음에는 지수·로그·삼각 함수가 없습니다. smax_k도 다항식 `max(a,b) + h²k/4`입니다. 그래서 모든 대조를 exact로 둡니다(NaN끼리는 같다고 봄).

그 대신 macOS arm64 빌드에만 있는 동작 네 가지를 그대로 재현해야 합니다.

1. libm `hypot`는 `sqrt(fma(b, b, a*a))`입니다.
2. cKDTree의 점 거리는 `fma(dy, dy, dx*dx)`입니다.
3. 정확히 겹친 강 점들 사이의 승자는 cKDTree 트리 순서(libc++ `nth_element`·`partition`)가 정합니다.
4. numpy 실수 `arange`는 `fma(i, delta, start)`로 채웁니다.

#### 대응표

| Python | C# (`csharp/Bpcg/Volume/`) | 비고 |
|---|---|---|
| `__init__.py` | 파일 없음 | `MATERIAL_EMPTY`, `HeroVolume`, `vertical_slice`를 다시 내보내기만 합니다. C#은 `Bpcg.Volume`에서 바로 씁니다 |
| `sample.py` 모듈 상수 20개 | `Sample.cs`: `public static class Sample`의 `const` | `DetailAmplitudeM` … `FarM`, `MaterialEmpty`(byte 255). `_STREAM_*`, `_REQUIRED`는 internal |
| `_sub_seed` | `internal static long SubSeed(long seed, long stream, long k = 0)` | `unchecked((long)(Hash3(..) >> 1))` |
| `smoothstep01` | `public static double Smoothstep01(double t)` (+ 배열판) | `NpClip` 뒤 `t*t*(3.0 - 2.0*t)` |
| `_smax`, `_cell_frac`, `_capsule_dist` | `SMax`, `CellFrac`, `CapsuleDist` (internal static, 인라인) | |
| `_columns_kernel` | `ColumnsKernel(...)` | 출력 10개, 지도는 행 우선 1차원 |
| `_river_project_kernel` | `RiverProjectKernel(...)` | idx는 `long[]`, 없음은 −1 |
| `_point_kernel` | `PointKernel(...)` | 출력 9개 |
| `_bisect_surface_kernel` | `BisectSurfaceKernel(...)` | |
| `catmull_rom_centripetal` | `public static (double[] Points, double[] Span) CatmullRomCentripetal(double[] ctrl, double stepM)` | 점은 (P, 2) 행 우선 |
| `_interp_ctrl` | `internal static double[] InterpCtrl(double[] values, double[] span)` | |
| `class HeroVolume` | `public sealed class HeroVolume` (같은 파일) | 생성자 `HeroVolume(HeroState heroState, Config cfg, double detailAmplitudeM = 2.0)`. 검증 순서와 한국어 메시지는 그대로 두고 `ArgumentException`을 냅니다 |
| 속성 | `Ny, Nx, NLevels, NLayers`(int), `Dx, X0, Y0, Amp, RPass, Wavelength`(double), `Seed`(long), `Z, Soil, Alluvium, ZGw, LakeLevel, LakeDist`(double[ny·nx]), `CaveLevels`(K·ny·nx), `StrataBottom`(ny·nx·L), `StrataRock`(byte, ny·nx·(L+1)), `Soluble`(bool[]), `RiverPts`(P·2), `RiverHw/RiverDepth/RiverZq/RiverLevel`, `RiverConn`(bool[]), `RiverReach`, `Tree`, `CapA/CapB`(E·3), `NUnopened`, `BStart/BItems`(long[]) | bake(corridor·mesh·detail)와 테스트가 읽습니다 |
| `extent`, `cell_centers` | `Extent`(튜플 속성), `CellCenters()` | |
| `_build_lakes/_build_rivers/_build_capsules/_build_buckets` | `BuildLakes/BuildRivers/BuildCapsules/BuildBuckets` | 생성자 안 호출 순서를 그대로 둡니다 |
| `_bilinear` | `double[] Bilinear(double[] image, double[] x, double[] y)` | numpy 곱셈 순서(커널과 다름) |
| `cave_noise`, `cave_distance` | `CaveNoise`, `CaveDistance(double[] x, double[] y, int k)` | |
| `_river_query` | `RiverQuery(...)` → 배열 5개 | |
| `columns` | `VolumeColumns Columns(double[] x, double[] y)` | dict → `sealed class VolumeColumns`(19개 키) |
| `_detail_noise`, `_evaluate` | `DetailNoise`, `EvaluateCore` | |
| `evaluate` | `VolumeEval Evaluate(double[] points, double noiseBandM = +∞)` | dict → `sealed class VolumeEval`(없는 키는 null) |
| `sample` | `(float[] D, byte[] Material, bool[] Water) Sample(double[] points)` | 아래 이름 충돌 참고 |
| `evaluate_grid` | `VolumeEval EvaluateGrid(double[] x, double[] y, double[] up, int nz, double noiseBandM = +∞, EvalKeys keys = D\|Material\|Water)` | (C, Z) 행 우선, `[Flags] enum EvalKeys` |
| `_by_chunks`, `surface_height`, `_surface_height`, `water_surface`, `water_table`, `_check_points` | `ByChunks`, `SurfaceHeight`, `SurfaceHeightCore`, `WaterSurface`, `WaterTable`, `CheckPoints` | |
| `slices.py` 색 상수 4개 | `Slices.cs`: `public static class Slices`의 `static readonly byte[]` | `AirRgb`, `WaterRgb`, `CaveAirRgb`, `WaterTableRgb` |
| `vertical_slice` | `public static RgbImage VerticalSlice(HeroVolume volume, (double X, double Y) p0, (double X, double Y) p1, double zMin, double zMax, double resM)` | `save_path` 없음. `sealed record RgbImage(int Height, int Width, byte[] Data)` |
| `_save_png` | 포팅하지 않음 | 아래 결정 확인 |

- **이름 충돌.** HeroVolume 안에서 `Sample`은 메서드 묶음으로 먼저 찾아지므로 `Sample.FarM`은 컴파일 오류(CS0119)가 납니다. `Sample.cs` 맨 위에 `using static Bpcg.Volume.Sample;`을 두고 상수와 커널을 이름만으로 부르는 것을 제안합니다.
- **원소 형.** bool 필드(is_lake, is_ocean, is_river)와 `RiverConn`, `Water`, `Under`는 `bool[]`입니다. 이 묶음에는 sbyte가 없습니다. 색인 배열(col, idx, todo, cells)은 `long[]`입니다.
- **`_save_png` 미포팅 결정 확인.** 호출자는 `tests/test_volume.py:287`과 `analysis/figures/render_results.py:330` 둘뿐입니다. 둘 다 `save_path`를 넘기고 Python에 남습니다. src 안(bake, cli, pipeline, studio)에는 호출자가 없으므로 결정에 문제가 없습니다. 다만 C# `VerticalSlice`도 C# 호출자가 없어 끝에서 끝 대조(evaluate_grid + water_table) 용도뿐입니다.

#### 수치 함정 표

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| sample.py:240,254 | libm hypot | numba `math.hypot`은 macOS libm이고 정확 반올림이 아닙니다(2만 점 중 15%가 1 ulp 차이). [1e-140, 1e140]에서는 `sqrt(fma(b,b,a*a))`(a, b는 절댓값의 큰 쪽과 작은 쪽)와 같습니다. ℓ은 선분 선택, `riv_l <= hw` 물 규칙, `< FAR_M` 분기에 들어갑니다 | `PyMath.Hypot` = 위 식(`Math.FusedMultiplyAdd`), inf는 +inf. `double.Hypot`(AOCL 알고리즘)과 `Math.Sqrt(x*x+y*y)`(약 5% 불일치)는 쓰지 않습니다 | exact |
| sample.py:680,726,729 | libm hypot | `np.hypot`도 libm입니다. 단순 sqrt를 쓰면 `cave_distance` 3만 점 중 412~443점이 달라집니다 | `PyMath.Hypot` | exact |
| sample.py:714,718-720 | 정수 hypot | CPython `math.hypot(1,1)`은 sqrt(2)와 같고 `1/norm` = 0.7071067811865475(≠ sqrt(0.5))입니다. `-dj/norm`은 정수 부정이라 −0.0이 생기지 않습니다 | `di, dj`를 int로 두고 `norm = Math.Sqrt(di*di + dj*dj)`, `di / norm`, `(-dj) / norm` | exact |
| sample.py:282 | `**2` | numba는 `x**2`를 곱셈으로 바꿉니다. `Math.Pow`는 CRT pow라 다를 수 있습니다(CPython `**2` 복제는 17/20000 불일치) | `ux*ux + uy*uy + uz*uz` | exact |
| sample.py:428 | `**0.5` | numpy `arr ** 0.5`는 `sqrt`가 됩니다(−0.0 보존). libm `pow(x, 0.5)`는 123/100000 불일치입니다 | `Math.Max(Math.Sqrt(Math.Sqrt(dx*dx + dy*dy)), 1e-9)` | exact |
| sample.py:421,428; slices.py:43 | norm | `norm(axis=1)`은 `sqrt(dx*dx + dy*dy)`입니다. 1차원 `norm`(Accelerate `ddot`, n=2)도 FMA 없이 같습니다. 단면 length는 그림 폭의 `ceil`로 갑니다 | `Math.Sqrt(dx*dx + dy*dy)` | exact |
| sample.py:738,748 | arange | numpy 2.5.3(arm64)은 `out[1] = start + step`, `delta = out[1] − start`, `out[i] = fma(i, delta, start)`로 채우고 길이는 `ceil((stop − start)/step)`입니다. 안쪽(start = step)은 무작위 설정의 95%에서 `start + i*step`과 다릅니다. 지금 설정(r = 3 m)에서는 차이가 없습니다 | `NpArange`가 같은 식을 FMA로 재현 | exact |
| slices.py:54,57 | linspace | `y[i] = i*step + start`, 끝은 `stop`. `step == 0`이면 `i/div*delta + start` | `NpLinspace` | exact |
| slices.py:71 | 반올림 | 짝수 반올림, 순서는 `((z_max − z_gw)/(z_max − z_min))*(n_h − 1)` | `Math.Round(v, ToEven)` 뒤 `(long)` | exact |
| slices.py:50-53 | 정수 넘침 | Python 정수는 넘치지 않아 `n_w*n_h > 5e7`에서 ValueError를 냅니다. C#은 `(long)` 포화와 곱 넘침으로 검사를 통과할 수 있습니다 | double로 계산해 IsInfinity와 곱 > 5e7을 먼저 검사 | n/a |
| sample.py:121,343,781-784 | 실수→정수 | numba `int(floor)`는 포화(NaN은 0)하고 Python은 큰 정수를 냅니다. 둘 다 바로 잘라 같은 결과가 됩니다 | `(long)Math.Floor` 뒤 같은 순서로 자름 | exact |
| sample.py:110-394 여러 곳, 600-601 | 내장 min/max | Python 의미입니다. `b > a`일 때만 b이고 같으면 앞 값(`max(−0.0, 0.0) = −0.0`, `max(nan,1) = nan`, `max(1,nan) = 1`), 3인자는 왼쪽부터 접습니다 | `PyMath.Max(a,b) => b > a ? b : a`, `PyMath.Min` 대칭 | exact |
| sample.py:563,845-849,1010 | np.maximum | IEEE 의미(±0에서 +0/−0, NaN 전파) | `Math.Max/Min` | exact |
| sample.py:549-559,1029 | np.fmax | NaN 무시, ±0에서 +0. 호수 넓히기는 원래 `lake`만 읽어 한 칸만 넓힙니다 | `double.MaxNumber`. 원래 배열만 읽는 반복문 | exact |
| sample.py:102,639-640 | np.clip | −0.0과 NaN을 보존합니다 | `x < lo ? lo : (x > hi ? hi : x)` | exact |
| sample.py:144-163 / 637-650 | 쌍선형 순서 | 커널은 `w00*z00 + …`(w00 = (1−tx)(1−ty)), `_bilinear`는 `a00*(1−tx)*(1−ty) + …`(왼쪽부터 곱)입니다. 23.5%에서 값이 다릅니다 | 두 도우미를 따로 둠 | exact |
| sample.py:121-122,641-642 | 음수 색인 | nx·ny = 1이면 `i0 = −1`을 감아 읽습니다(파이프라인은 n ≥ 3) | `Wrap(i, n)` 도우미 | exact |
| sample.py:592-616,803 | KD 동점 | 합류점에서 세 폴리라인의 점이 겹칩니다. cKDTree는 잎 안 `indices` 순서의 첫 점을 주고, 커널 출력은 고른 점에 따라 달라집니다 | scipy 1.18.1 `build.cxx`·`query.cxx`와 libc++ 정렬을 그대로 이식하고 `indices`를 대조 | exact |
| sample.py:803 | KD 거리 | `fma(dy, dy, dx*dx)`, 경계 `reach*reach`, `d² < bound²` 엄격, 없으면 n → −1 | `Math.FusedMultiplyAdd`, 같은 순서의 `RiverReach` | exact |
| sample.py:561-565 | EDT | 정확한 EDT로, 거리는 정수 제곱합의 sqrt입니다. 물이 없으면 FAR_M입니다 | 정수 EDT → `Math.Sqrt` → `*dx − 0.5*dx` → `Math.Max(…, 0)` | exact |
| sample.py:503-504,572-573 | nan_to_num | float64로 넓힌 뒤 NaN은 0, ±inf는 ±1.7976931348623157e308 | `NpNanToNum` | exact |
| sample.py:175,189,514,549 | NaN 비트 | Python NaN은 0x7FF8…입니다. .NET `double.NaN`은 부호 비트가 켜졌을 수 있습니다(미확인) | 비교는 NaN끼리 같다고 보고, 파일에 쓸 때 정규화 | exact |
| sample.py:652-681,860-862 | fbm 호출 | `1.0/λ`(옥타브 3, 점 (x, y, 0)), `1.0/40.0`(옥타브 5). `0.05*2.0*π/λ`는 왼쪽부터 계산합니다. 점마다 독립입니다 | `Core.Noise.Fbm3`를 점별로 호출 | exact |
| sample.py:404-455 | Catmull-Rom | Barry–Goldman centripetal(α = 0.5)입니다. knot 간격은 `sqrt(sqrt(dx²+dy²))`(하한 1e-9), 양 끝은 반사점입니다. 구간마다 `max(ceil(현/step), 1)`점을 knot 매개변수로 균등하게 나눕니다(호 길이·linspace·cumsum 아님, 실제 간격 최대 1.27 m). `local = i/k` | 구간·점·성분 반복문으로 같은 괄호 순서 | exact |
| sample.py:698-758 | 입구 탐색 | 입구 칸은 오름차순, 이웃은 dj −1..1 × di −1..1 순이고 `drop > best`(엄격), 격자 밖은 −inf입니다. 탐색 승자는 첫 참(`argmax`)입니다 | 같은 순서, 첫 참에서 break | exact |
| sample.py:767-790 | 버킷 CSR | `nbx = max(ceil(((x0 + nx*dx) − x0)/(2dx)), 1)`, 채우는 순서는 jb 바깥·ib 안쪽·e 오름차순 | 같은 식과 순서, `long[]` | exact |
| sample.py:494-498,571-576 | 입력 dtype | float32를 float64로 정확히 넓힙니다. 메모리 HeroState와 묶음 HeroState는 입력 값이 다릅니다 | 넓히기 도우미, golden은 호출 지점 입력 | exact |
| sample.py:928 | float32 | `sample()`의 d만 float32입니다 | `(float)d` | exact |
| sample.py:907-918,946-964 | 덩어리·배치 | 점마다 독립이라 덩어리 크기와 무관합니다. 출력은 (C, Z) 행 우선이고, C = 0이면 `{}`입니다 | 색인 long | exact |
| slices.py:58-74 | 그림 순서 | `.T`로 (H, W)로 바꾼 뒤 땅 `COLOR_RGB[min(mat, 11)]` → AIR → CAVE_AIR → 물 → 열마다 지하수면 한 픽셀 순으로 칠합니다 | 같은 우선순위의 픽셀 반복문 | exact |
| sample.py:989-1019 | 지표 풀이 | 고정점 10회 뒤, `m ≤ 0`이고 numpy `d_riv < 0.5`인 기둥만 이분법 48회를 돕니다 | 같은 횟수와 순서 | exact |

#### 외부 호출과 대체 방식

| 호출 | 위치 | C# 대체 | 확인 |
|---|---|---|---|
| `cKDTree(river_pts)`(leafsize 16, balanced_tree, compact_nodes) | sample.py:624 | `Bpcg.Numerics.KdTree`. 중앙값 분할은 `nth_element(start, start + n/2, end)`(비교 `x[a,d] < x[b,d]`), `partition(start, mid, !(x >= split))`, p가 start나 end일 때 대체 분할을 둡니다. 분할 축은 폭이 엄격히 큰 첫 축입니다. libc++(LLVM 17) `__nth_element`, `__sort3`, `__selection_sort`, `min/max_element`, `partition`을 옮깁니다 | 이 모형의 Python 구현이 두 히어로의 강 점과 무작위·중복 60 집합에서 `cKDTree.indices`를 그대로 재현했습니다 |
| `.query(q, k=1, distance_upper_bound=reach, workers=-1)` | sample.py:803 | `KdTree.Query1` → `long[]`(없으면 n). scipy `query.cxx`의 최선 우선 탐색, 잎 안은 `indices` 순, `d < bound` 엄격, 질의별 Parallel.For | 승자 = 잎 순서의 첫 점(6/6), FMA 순서 300/300 |
| `ndimage.distance_transform_edt(~is_body)` | sample.py:562 | `NdImage.DistanceTransformEdt(bool[], ny, nx)`(거리만, sampling 없음) | 전수 탐색과 비트 단위로 같음 |
| `np.hypot`, numba `math.hypot` | 240,254,680,726,729 | `PyMath.Hypot` | libm과 0/550,000 불일치 |
| `np.arange` 실수 3인자, `np.linspace` | 738,748; slices 54,57 | `NpArange`(FMA), `NpLinspace` | 각 9,000·40,000 경우 0 불일치 |
| `np.linalg.norm` | 421,428; slices 43 | `Math.Sqrt(dx*dx + dy*dy)` | 25만 경우 0 불일치 |
| `np.clip/fmax/maximum/minimum/nan_to_num/round` | 여러 곳 | `NpClip`, `double.MaxNumber`, `Math.Max/Min`, `NpNanToNum`, `Math.Round(ToEven)` | numpy 쪽은 실험, .NET 쪽은 문서 기준 |
| `core.noise.fbm3`, `core.hashing.hash3` | 96,658,862 | `Bpcg.Core.Noise.Fbm3`, `Bpcg.Core.Hashing.Hash3` | core 대조에 의존 |
| `geology.rocks`, `subsurface.water.RIVER_SURFACE_DEPTH_FRACTION`(0.2) | sample·slices | `Bpcg.Geology.Rocks`, `Bpcg.Subsurface.Water` 상수 | — |
| matplotlib `Figure`, `FigureCanvasAgg` | slices.py:80-95 | 포팅하지 않음 | 호출자는 Python뿐 |

#### numba 커널과 prange

| 커널 | 반복 | 쓰기 | 축약·경쟁 | C# |
|---|---|---|---|---|
| `_smax`, `_cell_frac`, `_capsule_dist` | inline="always" | 반환값 | — | `AggressiveInlining` static |
| `_columns_kernel` | `prange(C)` 기둥 | `o_*[c]`, `o_cave_*[c,k]`, `o_sb/o_sr[c,li]` | 없음(`ws`, `acc`, `same`은 반복 지역) | `Parallel.For(0, C)` |
| `_river_project_kernel` | `prange(C)` | `o_*[c]` | 없음 | `Parallel.For` |
| `_point_kernel` | `prange(M)` 점 | `o_*[p]` | 없음(`dmin`, `d_sol`, `h_w`는 지역) | `Parallel.For` |
| `_bisect_surface_kernel` | `prange(len(todo))` | `out[todo[q]]`(flatnonzero라 서로 다름) | 없음 | `Parallel.For` |

- 스레드 12개와 1개에서 evaluate의 모든 키와 surface_height가 비트 단위로 같았습니다.
- 이 기계의 numba는 `a*b+c`를 FMA로 축약하지 않습니다(serial·prange 모두, 어셈블리에 fmadd 없음). 그러므로 C#은 보통 산술로 씁니다.
- 커널 안의 실수는 모두 float64, 정수는 int64입니다. `solid`는 uint8이고, `solid if d <= 0 else 255`는 int64를 거쳐 uint8로 저장됩니다.
- 간접 커널인 core `fbm3`의 `_fbm_kernel`은 core 대조에서 확인합니다.

#### 의심 버그

1. **합류점 동점(sample.py:592-616, 803).** 위 구간에 덧붙인 끝점들과 아래 구간 첫 점이 같은 좌표가 됩니다. 가장 가까운 점은 cKDTree 잎 안 순서, 곧 C++ 표준이 정하지 않는 `nth_element`·`partition` 결과로 정해집니다.
   - 근거: B 히어로에는 3점 겹침이 6곳 있습니다. 주변 질의 196개 모두 후보에 따라 출력이 달랐고, 1 m 격자에서 394점이 겹친 점을 받았습니다.
   - 영향: 합류점 주변의 d·물·재질·지표 높이가 scipy 빌드(libc++ 대 libstdc++)에 따라 달라질 수 있습니다(다른 플랫폼은 확인하지 않음). conventions 6절의 플랫폼 간 재현 목표와 어긋납니다.
2. **'1 m 이하 간격' 문서(sample.py:405-409, pipeline.md 10절).** 실제 간격은 최대 1.27 m이고, 45~58%가 1 m를 넘습니다(테스트도 1.6배까지 허용). 영향은 낮습니다.
3. **`evaluate_grid`의 C = 0(sample.py:950-964).** 빈 dict를 돌려주므로 호출자가 KeyError를 냅니다. 지금 파이프라인에서는 C > 0이라 드러나지 않습니다.
4. **pipeline.md 466행 시그니처.** `vertical_slice(volume, p0, p1, z_range, res)`로 적혀 있지만 코드는 `(…, z_min, z_max, res_m, save_path)`입니다. 문서만 어긋납니다.

#### golden 대조 계획

입력은 두 벌입니다. A는 `load_hero_state("out/tiny_check/hero")` + `load_config("earth", "tiny")`이고, B는 `generate_hero(load_config("earth", "tiny", overrides=HERO_OVERRIDES), log=None)`입니다. 손 입력 C는 A에 `rivers=[]`, `cave_entrance=0`을 넣어 KD-tree 없음·캡슐 없음 분기를 엽니다. 1 MB를 넘는 것은 `out/golden/volume/`에만 두고, 커밋용은 점 수를 줄인 판을 씁니다.

| 대상 | 입력 | 출력 | 크기 | 비교 |
|---|---|---|---|---|
| `PyMath.Hypot` | 손으로 만든 2만 쌍(값 범위 안) | np.hypot | 0.5 MB(커밋용 5천 쌍) | exact |
| `NpArange`, `NpLinspace` | r ∈ {3, 2.9, 0.37, 7.3}, dx, λ, n 조합 | 배열 | < 50 KB | exact |
| `SubSeed` | seed 0, −5, 2^62+1, −2^63 × stream·k | int64 | < 1 KB | exact |
| `CatmullRomCentripetal` + `InterpCtrl` | 손 ctrl(n=1·2, 중복점, 격자, 무작위)과 A·B의 `_build_rivers` 입력 | pts, span, 보간값 | 0.15~1.1 MB | exact |
| `DistanceTransformEdt` | A·B의 `~is_body`, 무작위 마스크 20개 | 거리 | < 200 KB | exact |
| `KdTree` | A·B 강 점, 무작위·중복 60 집합, 합류점 1 m 격자 질의 | `indices`, idx | ≤ 1 MB | exact |
| 생성자(Build*) | A·B·C의 HeroVolume 호출 지점 입력 | lake_level, lake_dist, 강 배열, reach, cap_a/b, n_unopened, CSR, 시드 | 0.2~1.3 MB | exact |
| `ColumnsKernel` | 지도 배열 + 기둥 2만 개(영역 밖, 칸 중심, 경계, 강·캡슐 근처) | 출력 10개 | 2.5 MB(커밋용 0.7 MB) | exact |
| `RiverProjectKernel` | Python idx(−1 포함), 합류점 후보별 idx | 출력 5개 | 1 MB | exact |
| `PointKernel` | Columns 출력 + 실제 col/px/py/up/xi 5만 점(C 포함) | 출력 9개 | 4 MB(커밋용 0.8 MB) | exact |
| `BisectSurfaceKernel` | `_surface_height` 호출 지점 입력 | h | < 300 KB | exact |
| `Columns` | x, y 2만 개 | 19개 키 | 3 MB | exact |
| `Evaluate`, `Sample`, `EvaluateGrid` | 고정 시드 5만 점, C 2,000 × Z 40 | 키별 배열, float32 d | 6 MB | exact |
| `SurfaceHeight`, `WaterSurface`, `WaterTable`, `CaveDistance` | test_surface_height 구성, 3만 점 | h, h_w, z_gw, δ_ν | < 1 MB | exact |
| `VerticalSlice` | A·B의 축 방향 20 m 단면과 대각선 7.3 m 단면, 예외 사례 | rgb uint8 | 0.06~1.9 MB | exact |

모든 비교를 exact로 두는 근거는 이 묶음이 사칙연산, sqrt, 결정적 FMA, floor/ceil, 비교만 쓴다는 점입니다. 두 히어로에서 비트 단위 복제도 확인했습니다. NaN은 NaN끼리 같다고 봅니다.

#### 포팅 순서와 위험 메모

1. **선행 작업.** core의 `Hash3`·`Fbm3`가 exact로 통과해야 합니다. 그 밖에 `Geology.Rocks`·`Subsurface.Water` 상수와 Numerics(`PyMath.Max/Min/Hypot/NaN`, `NpClip`, `NpNanToNum`, `NpArange`, `NpLinspace`, `NdImage.DistanceTransformEdt`, `KdTree`)가 필요합니다.
2. **`Sample` 정적 부분.** 상수 → `SubSeed` → `Smoothstep01`·`SMax`·`CellFrac` → `CatmullRomCentripetal`·`InterpCtrl` 순서로 옮깁니다.
3. **커널 4개.** 커널마다 Python 입력을 그대로 받아 대조합니다. `InternalsVisibleTo("Bpcg.Tests")`를 둡니다.
4. **HeroVolume 생성자.** `BuildLakes → BuildRivers → BuildCapsules → BuildBuckets` 순서를 지킵니다. 캡슐 탐색이 동굴 시드와 `CaveDistance`를 쓰기 때문입니다.
5. **메서드와 단면.** `Columns`, `Evaluate`/`Sample`/`EvaluateGrid`, `SurfaceHeight`, `WaterSurface`, `WaterTable`을 옮긴 뒤 `Slices.VerticalSlice`를 옮깁니다. 그다음 bake로 넘어갑니다.
6. **위험 메모.**
   - 가장 큰 위험은 KdTree 순서 재현입니다. scipy 판이나 빌드(libc++)가 바뀌면 `indices`가 바뀔 수 있으므로 uv.lock 버전을 기준으로 두고, golden에 `indices`를 저장합니다.
   - FMA 세 곳(Hypot, KD 거리, arange)은 지시 원문의 'FMA 금지'와 충돌하므로 승인이 필요합니다.
   - golden 기준 플랫폼은 macOS arm64로 고정해야 합니다.
   - 성능은 `EvaluateGrid`가 굽기에서 가장 큰 계산이라는 점이 핵심입니다(tiny 재질 부피 4.65M 점). 점 단위 Parallel.For를 쓰고, 덩어리는 1e6 점으로 둡니다.
   - HeroState의 의존 방향과 `Sample` 이름 충돌은 열린 질문에 있습니다.

### bake ① — bundle·corridor·heightmap·mesh·textures

대상은 `src/bpcg/bake/` 의 `__init__.py`(4줄), `bundle.py`(397), `corridor.py`(551), `heightmap.py`(151), `mesh.py`(231), `textures.py`(133), 모두 1,467줄입니다. 여섯 파일에는 numba 커널이 없고, 계산은 산술·비교·정렬·형 변환뿐입니다. 그래서 위험은 파일 형식(JSON·.npy·.npz·.bin·PNG·glb)의 재현과 scikit-image·trimesh·matplotlib 대체에 몰려 있습니다. '확인'은 numpy 2.5.3, matplotlib 3.11.2, trimesh 5.1.0, scikit-image 0.26.0(macOS arm64)에서 실험한 결과이고, '.NET 미검증'은 SDK 가 없어 확인하지 못한 .NET 동작입니다. 공통 규칙(pairwise 합, NEP 50, 안정 정렬, libm, 캐스트)은 공통 장을 따르고, 여기에는 발생 위치와 방안만 적습니다.

#### (a) 대응표

공통 형 제안은 세 가지이고 다른 담당과 합의해야 합니다.

- `PyDict` = `OrderedDictionary<string, object?>`(.NET 9+)입니다. JSON 객체, manifest, meta, 반환 dict 에 씁니다.
- `NdArray` 는 numpy dtype 이름, shape, 평평한 배열을 묶은 필드 그릇입니다.
- `IHeroVolume` 은 bake 가 쓰는 HeroVolume 멤버를 묶은 인터페이스입니다. 대조 시험에서는 Python 기록값을 돌려주는 가짜로 바꿔 끼웁니다.

`bundle.Bundle` 은 모듈 정적 클래스와 이름이 겹칩니다. 형 이름을 그대로 두고 정적 클래스를 복수형 `Bundles` 로 두는 안을 냅니다(`config.Config` → `Configs` 와 같은 규칙).

| Python | C# 파일 · 형 · 멤버 |
|---|---|
| `bake/__init__.py` | 파일 없음 (docstring 뿐, 다시 내보내는 이름 없음) |
| `bundle` 상수 6개 | `Bake/Bundle.cs` `Bundles`: `Format`, `FormatVersion = 1`, `ManifestFile`, `GraphFile`, `RiversFile`, `JsonArrayMax = 1024` |
| `bundle.Bundle` | `sealed class Bundle { CellGraph Graph; OrderedDictionary<string, NdArray> Fields; PyDict Manifest; PyDict Meta; List<long[]>? Rivers }` |
| `git_commit` | `string? GitCommit()` |
| `jsonable` · `write_json` · `_write_text_atomic` | `object? Jsonable(object? o, int? maxArray = JsonArrayMax)` · `void WriteJson(string path, object? o, int? maxArray = JsonArrayMax)` · `private void WriteTextAtomic(string path, string text)` |
| `_graph_info` · `_cast_field` | `private PyDict GraphInfo(CellGraph g)` · `private NdArray CastField(string name, NdArray a, int n)` |
| `write_bundle` | `PyDict WriteBundle(string path, CellGraph graph, IReadOnlyDictionary<string, NdArray> fields, PyDict? meta = null, Config? cfg = null, IReadOnlyList<long[]>? rivers = null, string? kind = null)` |
| `read_manifest` · `read_bundle` · `config_from_manifest` | `PyDict ReadManifest(string path)` · `Bundle ReadBundle(string path)` · `Config ConfigFromManifest(PyDict man)` |
| `_float_fields` | `private OrderedDictionary<string, NdArray> FloatFields(...)` (float32 → double[]) |
| `save_planet_state` · `load_planet_state` | `PyDict SavePlanetState(string path, PlanetState p, Config cfg)` · `PlanetState LoadPlanetState(string path)` |
| `_site_meta` · `save_hero_state` · `load_hero_state` | `private PyDict? SiteMeta(HeroSite? s)` · `PyDict SaveHeroState(string path, HeroState h, Config cfg)` · `HeroState LoadHeroState(string path)` |
| `corridor` 상수 11개 | `Bake/Corridor.cs` `Corridor`: `StrataDxM = 4.0`, `StrataDzM = 2.0`, `WaterNoneM = -10000.0`, `byte StrataWater = 254`, `byte StrataAir = 255`, `MaxCandidates = 256`, `StrataChunkPoints = 4_000_000`, `WaterSurfaceTolM = 0.01`, `CaveMouthClipM = 20.0`, `string[] DetailStems`, `DetailMinSamples = 8` |
| `_log` · `_remove_stems` · `_centers` | `private void Log(Action<string>? log, string msg)` · `private void RemoveStems(string dir, IEnumerable<string> stems)` · `private (double[] X, double[] Y) Centers(CellGraph g)` |
| `best_donor` · `main_stem` | `long[] BestDonor(int[] receiver, double[] discharge)` · `(long[] Path, int Start) MainStem(int[] receiver, double[] discharge, long start)` |
| `choose_corridor` | `CorridorChoice ChooseCorridor(HeroState h, Config cfg)`; `sealed record CorridorChoice(Rect Rect, string Axis, int Sign, long StartCell, double StartX, double StartY, long ApexCell, string ApexKind, int NEntranceCells, int NStemCells, double LengthM, double WidthM, int NCandidates)` + `PyDict ToPyDict()` (Python 키 순서) |
| `_grid` · `_site_frame` | `internal (double[] Xs, double[] Ys) Grid(Rect r, double step)` (tests/test_detail 이 씀) · `private PyDict SiteFrame(HeroState h, Config cfg, double cx, double cy)` |
| `bake_strata` · `bake_corridor` | `PyDict BakeStrata(IHeroVolume v, Rect rect, EngineFrame f, Config cfg, string outDir)` · `PyDict BakeCorridor(HeroState h, Config cfg, string outDir, string? engineDir = null, Action<string>? log = Console.WriteLine)` |
| `heightmap` 상수 4개 | `Bake/Heightmap.cs` `Heightmap`: `Format = "float32_le"`, `Layout = "row_major"`, `Axes = "x_east_y_up_z_south"`, `string[] RequiredKeys` |
| `stem_paths` · `write_heightmap` | `(string Bin, string Json) StemPaths(string stem)` · `PyDict WriteHeightmap(string stem, double[] z, int rows, int cols, double spacingM, double[]? origin = null)` (+ float[]·int[]·long[] 오버로드, bool 없음) |
| `read_heightmap` · `_write_atomic` | `(float[] Z, int Rows, int Cols, PyDict Meta) ReadHeightmap(string stem)` · `private void WriteAtomic(string path, ReadOnlySpan<byte> data)` |
| `mesh` 상수 4개 | `Bake/Mesh.cs` `Mesh`: `TileVoxels = 64`, `BandPadVoxels = 2`, `SdfClipVoxels = 4.0`, `ColorProbeVoxels = 0.5` |
| `EngineFrame` | `sealed record EngineFrame(double Cx, double Cy, double YOffset)`: `double[] ToEngine(double[] pts)`, `double[] ToLocal(double[] pts)` ((M,3) 평평), `PyDict AsDict()` |
| 사각형 튜플 (새 형) | `readonly record struct Rect(double XMin, double XMax, double YMin, double YMax)` |
| `_merge_intervals` · `cave_bands` | `private List<(double Lo, double Hi)> MergeIntervals(...)` · `List<(double Lo, double Hi)> CaveBands(IHeroVolume v, Rect bbox, double padM)` |
| `cave_surface` | `(double[] Verts, long[] Faces, PyDict Diag) CaveSurface(IHeroVolume v, Rect rect, double voxelM, Action<string>? log = null)` |
| `build_mesh` · `export_glb` | `CaveMesh BuildMesh(double[] vertsLocal, long[] faces, IHeroVolume v, EngineFrame f, double voxelM)`, `sealed class CaveMesh { double[] Vertices; long[] Faces; double[] VertexNormals; byte[] Rgba }` · `long ExportGlb(CaveMesh m, string path)` |
| `textures` 상수·색표 | `Bake/Textures.cs` `Textures`: `string[] Layers`, `double[] NanRgba = {0.55, 0.55, 0.55, 1.0}`, `ElevationPercentile = 99.0`; `_OCEAN`·`_LAND` → `ColormapLuts.BpcgOcean`·`BpcgLand` |
| `elevation_rgba` · `_scalar_rgba` · `face_textures` | `double[] ElevationRgba(double[] z, double depthMax, double heightMax)` ((M,4) 평평) · `private double[] ScalarRgba(double[] v, Colormap c, double lo, double hi)` · `PyDict FaceTextures(PlanetState p, string outDir)` |
| matplotlib 색표 (새 형) | `sealed class Colormap { int N; double[] Lut; double[] Apply(double[] t) }`, 생성 파일 `Bake/ColormapLuts.g.cs` (`BpcgOcean`, `BpcgLand`, `Viridis`, `YlGnBu`, `Tab20`) |

#### (b) 수치 함정 표

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| `bundle.py:125`, `heightmap.py:111` | json-format | indent=2, 구분자 `,`·`: `, 빈 `[]`·`{}`, 실수 repr(`1e+16`, `1e-05`, `-0.0`, `1000000000000000.0`), 비ASCII 원문, 제어문자 `\u00XX` 소문자, DEL·U+2028 원문 (확인) | `Bpcg.IO.PyJson`; System.Text.Json 기본 인코더 금지 | exact |
| `bundle.py:85-108` | jsonable | 판정 순서, float32 → double(`0.10000000149011612`), 비유한 → null, 1024 초과 배열은 `{"__array__": shape, "dtype"}`, 리스트는 `{"__list__": n}`, 키는 `str(k)` (확인) | `Bundles.Jsonable` 이 NdArray 의 shape·numpy dtype 이름을 씀, 키는 string·long 만 | exact |
| `bundle.py:111-114` | newline | `write_text` 는 Windows 에서 CRLF, heightmap .json 은 늘 LF | UTF-8(BOM 없음)·LF 바이트 | exact |
| `bundle.py:60-74,222`, `corridor.py:500` | env | git 커밋은 환경에 따라 다름 | `Process` + 10 s, 실패는 null, 대조 제외 | n/a |
| `bundle.py:141-154` | cast | 정수 필드: 유한·정수값 → iinfo 범위 → astype; float64→float32 RNE; bool 은 NaN → True | 같은 순서로 검사한 뒤 캐스트, bool 은 `v != 0` | exact |
| `bundle.py:184,307,351` | sort-order | `sorted(fields)` 는 코드 포인트 순, FIELDS 밖 키는 버림 | `StringComparer.Ordinal`, 같은 거름 | exact |
| `bundle.py:189` | npy | v1.0, 헤더 128 B(GROWTH_AXIS 여유 + 64 정렬), descr `<f4 <f8 \|u1 \|b1 \|i1 <i4 <i8` (확인) | `Bpcg.IO.Npy` 로 바이트 동일 | exact |
| `bundle.py:189,201-215` | nan-bits | numpy NaN 은 `0x7FC00000` (확인), .NET `double.NaN` 은 음의 NaN 으로 알려짐(.NET 미검증) | 쓸 때 양의 NaN 으로 정규화, 비교는 NaN 끼리 같음 | exact |
| `bundle.py:201-215` | npz | ZIP_STORED, 1980-01-01 고정, force_zip64, version 45, 속성 0o600 (확인) | 내용 비교 기본, 바이트 동일은 자체 ZIP writer | exact(내용) |
| `bundle.py:209-216` | empty | `rivers=[]` → offsets `[0]`, 파일과 `n_segments: 0` 을 쓰고 다시 읽으면 `[]` (확인) | 같은 분기 | exact |
| `bundle.py:217-236` | dict-order | manifest·meta 키 순서, config 의 TOML 순서와 int/float 구분 | PyDict, 순서를 지키는 TOML 읽기 | exact |
| `bundle.py:297-331` | roundtrip | float32 → float64 넓힘, info/diag 는 JSON 왕복 값, hero.refine 이 `diag.geology.u_max_m_per_yr` 를 읽음 | Cli 도 쓰고 다시 읽기, 최단 왕복 표기 + 정확 파싱 | exact |
| `corridor.py:87-103` | lexsort | 받는 칸별 최대 q, 동률(−0.0==0.0)은 작은 번호, NaN 이 최대 (확인) | 오름차순 한 번 훑기, `a > b \|\| (IsNaN(a) && !IsNaN(b))`; `CompareTo` 금지 | exact |
| `corridor.py:106-125` | bound | 하류는 `len<=n` + 순환 오류, 상류는 상한만 | 그대로 | exact |
| `corridor.py:154-162` | tie | `max(key=(Q, −cell))` 는 첫 최대, `argmax` 는 첫 최대·첫 NaN (확인) | 순차 비교, `NpReduce.ArgMax` | exact |
| `corridor.py:165` | norm | `norm(axis=1)` = `sqrt(dx*dx+dy*dy)`(hypot 아님), cumsum 순차 (확인) | `Math.Sqrt(dx*dx + dy*dy)` | exact |
| `corridor.py:170` | round | `linspace` = `i*step`, 끝은 stop; `round` 짝수 (확인) | `PyMath.Linspace`, `Math.Round`(ToEven) | exact |
| `corridor.py:174` | search | `searchsorted` 왼쪽 (확인) | 하한 이분 탐색, `Array.BinarySearch` 금지 | exact |
| `corridor.py:177-194` | py-min-max | `np.any`(±0 거짓, NaN 참), 축 동률은 east, 부호는 `>=0`; 내장 min/max 는 동률에서 첫 인자 (확인) | `PyMath.Min/Max` (`b<a?b:a`), `Math.Min/Max` 금지 | exact |
| `corridor.py:195-200` | exact-compare | rect 모서리가 칸 중심과 겹쳐 경계 포함 판정이 비트에 민감, 후보 키는 엄격한 `>` | 식 순서 그대로, FMA 금지 | exact |
| `corridor.py:218-223`, `mesh.py:126-129` | grid | `floor(d/step + 1e-9) + 1`; `min + i*step`(mesh, 남→북)와 `max − i*step`(corridor, 북→남) | 같은 식 | exact |
| `corridor.py:233-241` | libm | `asin`·`atan2`; `degrees = x*(180/π)`; 1-D norm·`@` 는 순차 합 (확인) | `x * (180.0 / Math.PI)`, `RadiansToDegrees` 금지 | tolerance 1e-12° |
| `corridor.py:264-280` | layout | nz = ceil, step = 4e6 // nz, `vol[k,j,i] = mat[k,i,j]`, 물은 254 (반복 실행 바이트 동일 확인) | 평평 인덱스 변환 | exact |
| `corridor.py:281,352,417-421,470` | neg-zero | `−y_offset`, `−(ys0−cy)`, `round(3)` 에서 나온 `-0.0` 이 JSON 에 남음 (확인) | PyJson `-0.0`, 부호 보존 | exact |
| `corridor.py:351` | floor | `float(math.floor(min))` 은 −0.0 → +0.0, NaN 이면 ValueError (확인) | `Math.Floor(m) + 0.0`, NaN 검사 | exact |
| `corridor.py:360-400` | clip-meta | `np.clip` 은 NaN 통과, ±inf → ±20; n_wet·n_open·detail 은 .json 을 쓴 뒤 dict 에 더함 (확인) | `Math.Clamp`, WriteHeightmap 은 쓴 뒤 PyDict 반환 | exact |
| `corridor.py:380-392` | exception | `except ValueError` 는 numpy 가 낸 ValueError 도 받음 | ValueError 대응 형만 잡음 | exact |
| `corridor.py:470-471` | np-round | `round(3)` = `rint(x*1000)/1000` (확인) | `Math.Round(x*1000.0)/1000.0` (`PyMath.NpRound`) | exact |
| `corridor.py:323-537`, `mesh.py:121,191` | timing | seconds 는 실행마다 다르고 로그 서식도 다름 | Stopwatch, 대조 제외 | n/a |
| `corridor.py:497-539` | dict-order | `{**cor, …}`, file_meta 삽입 순서, caves 키 순서 | PyDict | exact |
| `heightmap.py:68-92` | cast | bool 거부, float64→float32 RNE, 넘치면 inf → 거부 | 오버로드, `(float)` | exact |
| `heightmap.py:102-103` | signed-zero | float32 min/max: ±0 이 섞이면 min −0.0·max +0.0 (확인) | `MathF.Min/Max` (IEEE 2019, .NET 미검증) | exact |
| `heightmap.py:107-112` | bytes | `<f4` 행 우선, .bin 먼저, .json 은 바이너리 LF | `BinaryPrimitives`, `File.Move(overwrite)` | exact |
| `mesh.py:46-54` | op-order | to_engine 과 to_local 은 서로 정확한 역이 아님 | 식 그대로 | exact |
| `mesh.py:66-74` | sort | `(lo, hi)` 사전순 정렬, Python max | 튜플 비교자, `PyMath.Max` | exact |
| `mesh.py:85-88` | overflow | Python 정수는 넘치지 않지만 C# `(int)` 는 포화한 뒤 `+1` 에서 감김 | double 에서 클램프한 뒤 캐스트 | exact |
| `mesh.py:145-155` | layout | meshgrid `ij`, `f[i,j,k]`, k 가 가장 빠름 (확인) | `(i*ny + j)*nz + k` | exact |
| `mesh.py:148-153` | floor | k0·k1 은 음수가 될 수 있음 | `Math.Floor/Ceiling` | exact |
| `mesh.py:157-167` | mc | skip 판정은 float64, MC 는 float32 입출력, spacing 곱 뒤 다시 float32, `vt(f32)+off(f64)` 는 float64 (확인) | MC 대체가 float32 단계를 지킴 | exact |
| `mesh.py:170` | winding | `fc[:, [0,2,1]]` | 같은 순열 | exact |
| `mesh.py:182` | mean | `((v0+v1)+v2)/3.0` (확인) | 같은 식 | exact |
| `mesh.py:203-207` | merge | 키는 `round(V*1e8)` 짝수 반올림 → int64, 첫 등장 순서 (소스 확인) | Dictionary 첫 등장 | exact |
| `mesh.py:208` | normals | 각도 가중(arccos) + 희소 합 + unitize (소스 확인) | topic 담당 | tolerance 1e-12 |
| `mesh.py:209-214` | probe | 법선 오차가 재질(범주)로 번질 수 있으나 tiny 는 1e-3 섭동에도 0/2122 (확인) | exact 비교, 다르면 원인 기록 | exact |
| `mesh.py:218-231` | glb | export 가 법선을 다시 unitize, 위치 float32, 면 uint32, COLOR_0 uint8 | `Bpcg.IO.Glb` | 부분별 |
| `mesh.py:135-172` | seam | 띠 바닥 k0 가 다른 조각의 이음매 꼭짓점이 1e-8 병합에서 안 붙음 (합성 확인) | 같은 float32 계산으로 재현 | exact |
| `textures.py:32-38` | lut | `_lut` 는 (N+3)×4 float64 (확인) | 생성 표 + 비트 대조 | exact |
| `textures.py:46-55,97` | cmap | `xa=t*N`, `xa==N → N−1`, 마스크는 정수 변환 전, astype(int) 는 자르기 (확인) | `Colormap.Apply` | exact |
| `textures.py:84-86,111` | percentile | linear 식, NaN 제거 (확인) | `NpStats.Percentile/NanPercentile` | exact |
| `textures.py:60-113`, `cli.py:110` | input | cli 는 메모리 float64 planet 을 넘기고, 묶음 값으로는 depth_max 가 달라짐 (확인) | Cli 도 메모리 값, golden 은 호출 지점에서 | exact |
| `textures.py:97` | floor-mod | `% 20` 은 내림 나머지, ±inf 는 int64 포화 (확인) | `PyMath.FloorMod` | exact |
| `textures.py:99,104` | nan-reduce | nanmax, 모두 NaN 이면 ValueError | `NpReduce.NanMax` | exact |
| `textures.py:117-124` | png | 행 뒤집기, `(c*255)` 자르기, RGBA8, tEXt·pHYs, Pillow 필터·zlib 이라 바이트 재현 불가 (확인) | `(byte)(c*255.0)`, 자체 writer, 픽셀 대조 | exact(픽셀) |

#### (c) 외부 호출과 대체 방식

| 호출 | 쓰는 곳 | 대체 |
|---|---|---|
| `json.dumps(indent=2, ensure_ascii=False[, allow_nan=False])` | write_json, write_heightmap | `Bpcg.IO.PyJson` writer (UTF-8, LF) |
| `json.loads` | read_manifest, read_heightmap | Utf8JsonReader → PyDict (원문에 `.`·`e` 가 있으면 double, 없으면 long) |
| `subprocess.run(git rev-parse HEAD)` | git_commit | `Process`, 10 s, 실패는 null |
| `np.save`, `np.load` | 묶음 필드 | `Bpcg.IO.Npy` (128 B 헤더, NaN 정규화) |
| `np.savez`, `np.load(npz)` | graph.npz, rivers.npz | `Bpcg.IO.Npz` (내용 동일이 기본) |
| `os.replace` + `.part` | 모든 원자 쓰기 | `File.Move(tmp, dst, overwrite: true)` |
| `shutil.copy2` | engine_dir 복사 | `File.Copy` + `File.SetLastWriteTimeUtc` |
| `time.perf_counter` | seconds | `Stopwatch` (대조 제외) |
| `skimage.measure.marching_cubes(level=0.0, spacing=(v,v,v), allow_degenerate=False)` | cave_surface | `Bpcg.Numerics.MarchingCubes` (topic 담당) |
| `trimesh.Trimesh(process=False, validate=False)`, `merge_vertices`, `remove_unreferenced_vertices` | build_mesh | 직접 구현: `round(V*1e8)` 키로 첫 등장 병합, 순서 유지 압축 |
| `Trimesh.vertex_normals` | build_mesh | 각도 가중 법선 (topic 담당과 공유) |
| `trimesh.exchange.gltf.export_glb(scene, include_normals=True)` | export_glb | `Bpcg.IO.Glb` (topic 담당), 노드·메시 이름 `caves` |
| `matplotlib.colormaps[...]`, `LinearSegmentedColormap.from_list` | textures | 스크립트로 만든 `ColormapLuts.g.cs` + `Colormap.Apply` |
| `matplotlib.image.imsave(origin="lower")` → Pillow | textures | `Bpcg.IO.Png` (RGBA8, ZLibStream, CRC32) |
| `np.percentile`, `np.nanpercentile`, `np.nanmax` | textures | `NpStats`, `NpReduce` |
| `np.lexsort`, `np.argmax`, `np.searchsorted`, `np.linspace`, `ndarray.round` | corridor | 한 번 훑기, `NpReduce.ArgMax`, `NpSort.SearchSortedLeft`, `PyMath.Linspace`·`NpRound` |
| `local_to_unit`, `detail.*`, `HeroVolume` | corridor, mesh | 각 담당 포트, bake 는 `IHeroVolume` 으로 받음 |

#### (d) numba 커널과 prange

여섯 파일에는 `@njit` 과 `prange` 가 없으므로 C# 에서는 모두 순차 반복으로 옮깁니다. 병렬화 후보는 bake_strata 청크, cave_surface 조각, build_mesh 색 탐침입니다. 셋 다 점마다 독립이라 `Parallel.For` 로 바꿔도 값이 같습니다. 다만 cave_surface 는 조각·띠 순서대로 꼭짓점 번호(n_vert)를 매기므로, 계산만 병렬로 하고 이어 붙이기는 원래 순서대로 해야 합니다. 축약 변수는 없습니다. 호출하는 HeroVolume·detail 커널은 각 담당 절을 따릅니다.

#### (e) 의심 버그

1. `mesh.py:135-172` — 이웃 조각의 띠 바닥 k0 가 다르면 이음매 꼭짓점이 정확히 겹치지 않아 1e-8 병합에서 붙지 않습니다. skimage 가 float32 국소 좌표로 꼭짓점을 계산하기 때문입니다. 합성 실험에서 1.9e-6–7.6e-6 m 어긋났고 52개 중 38개만 붙었습니다. tiny 회랑(8 m·2 m)은 k0 가 모두 같아 생기지 않았고, 큰 회랑에서 생기는지는 확인하지 않았습니다. 영향은 음영 이음매와 겹친 꼭짓점이며, C# 은 이를 재현해야 합니다.
2. `mesh.py:196-199` — docstring 은 '면적 가중' 법선이라 하지만, trimesh 5.1.0 은 각도 가중으로 계산합니다. 문서 불일치입니다.
3. `mesh.py:172` — `tiles_with_caves` 는 띠가 있는 조각을 모두 세므로, 면이 0개인 조각도 들어갑니다. 진단값이 부풀 수 있습니다.
4. `bundle.py:111-114` — `write_text` 때문에 Windows 에서는 manifest 류 JSON 이 CRLF 로 써집니다. heightmap .json 은 LF 라 OS 마다 바이트가 달라집니다.
5. `bundle.py:376-388`, `corridor.py:156` — jsonable 은 NaN 을 null 로 쓰는데, 다시 읽을 때 lat/lon 만 NaN 으로 되돌립니다. score 와 fan apex 의 discharge 는 `float(None)` 에서 TypeError 를 냅니다. NaN 이 있을 때만 생깁니다.
6. `corridor.py:119-123` — main_stem 상류 걷기에는 순환 검사가 없어(하류는 ValueError), 순환 receiver 에서 칸이 반복된 길을 돌려줍니다. receiver 는 구성상 순환이 없어 실제 영향은 없습니다.

#### (f) golden 대조 계획

| 대상 | 입력 | 출력 | 크기 | 비교 |
|---|---|---|---|---|
| `Jsonable`·`WriteJson` | 손 사례(NaN·inf, float32, −0.0, 1e16·1e−5·5e−324, 1024/1025 배열·리스트, 2-D 배열, 한글·제어문자·DEL·U+2028, 빈 컨테이너), max_array=None | 파일 바이트 | < 50 KB | exact |
| `WriteBundle`/`ReadBundle` 평면 | tiny hero (cli 경로 load_hero_state) + bake_config | .npy 바이트, npz 내용, manifest(git_commit·seconds·threads 제외) | ≈1.3 MB | exact |
| `WriteBundle` 구면·거부 | test_bake 의 sphere_graph(6)·rng(0), 거부 4종 | .npy, manifest(face_basis), 예외 | < 50 KB | exact |
| `LoadHeroState`/`LoadPlanetState` | Python 이 쓴 golden 묶음 | float64 필드, LayerColumns, HeroSite, rivers, meta | ≈2 MB | exact |
| `BestDonor`·`MainStem` | tiny receiver·discharge + 출발 칸 3개, 손 사례(동률, ±0, NaN, 순환) | int64 배열, path·p0, 예외 | ≈100 KB | exact |
| `ChooseCorridor` | tiny hero; 합성 5×700(후보 > 256), 400×5(north, fan), 강 없음, 영역보다 긴 길이 | dict JSON | ≈300 KB | exact |
| `Grid`·`EngineFrame`·`MergeIntervals` | 손 사례(1e−9 경계, −0.0, 겹침·맞닿음) | 배열·dict·구간 | < 10 KB | exact |
| `SiteFrame` | earth tiny HeroSite, 비자명 축 1개, site None | dict | < 5 KB | origin_unit exact, lat/lon 1e−12° |
| `BakeStrata` | 가짜 볼륨 기록(top, material·water) + rect·frame·cfg; 전체와 40 m 축소판 | strata.u8·.json, strata_top | ≈9 MB / ≈50 KB | exact |
| `WriteHeightmap`/`ReadHeightmap` | rng(20261002), float32 경계값, 정수, ±0, origin −0.0, 'tile.v2', 거부 사례 | .bin·.json 바이트, dict, 예외 | < 20 KB | exact |
| `CaveBands` | tiny 볼륨 속성 + 조각 bbox 3개 | 구간 목록 | ≈80 KB | exact |
| `CaveSurface` | 가짜 볼륨 기록(d_cave, d1) + tiny rect·voxel 8, 첫 캡슐 ±60 m·voxel 2 축소판 | verts, faces, diag, f 블록·MC 중간값 | ≈2 MB / < 1 MB | exact |
| `BuildMesh` | CaveSurface 결과 + frame + solid_material 기록 | 병합 꼭짓점·면, 법선, 탐침 점, rgba | ≈150 KB | 위치·면·색 exact, 법선 1e−12 |
| `ExportGlb` | BuildMesh 결과 | glb 바이트·길이 | ≈100 KB | JSON 파싱 비교 + 버퍼별(법선 float32 1 ulp) |
| 색표 LUT | 이름 5개 | `_lut` float64 | ≈35 KB | exact (비트) |
| `ElevationRgba`·`ScalarRgba` | 손 사례(t=0·1, NaN·inf, −0.0, 최대값 < 1, hi<=lo) | float64 RGBA | < 20 KB | exact |
| `FaceTextures` | cli.run_planet 호출 지점의 메모리 planet(tiny), test 의 sphere_graph(8)·rng(1) | textures.json, 층·면별 RGBA | ≈300 KB | exact |
| `BakeCorridor` (단계 3) | tiny hero 묶음 + bake_config | corridor 폴더 전체 | ≈5 MB | manifest 는 휘발 키 제외 exact, 나머지는 위 규칙 |

#### (g) 포팅 순서와 위험 메모

1. 먼저 갖출 것은 core 의 Config(순서 보존 TOML, digest), `Bpcg.IO.PyJson`·`Npy`·`Npz`, 그리고 PyDict·NdArray·예외 형 합의입니다.
2. 순서는 Heightmap(낮음) → Bundles(중간, 형식) → Textures(중간, LUT 생성 스크립트·Percentile·PNG) → Corridor 고르기 부분(BestDonor·MainStem·ChooseCorridor·Grid·SiteFrame) → Mesh(높음, MarchingCubes·Glb·법선 대체 뒤) → BakeStrata·BakeCorridor(volume·detail 포트 뒤)입니다.
3. bake 함수는 `IHeroVolume` 으로 받아, volume 포트 없이 기록 재생 가짜로 대조할 수 있게 합니다.
4. 위험이 가장 큰 곳은 mesh.py 입니다(skimage float32 단계, trimesh 병합·각도 가중 법선, glb). 그다음은 bundle 형식입니다(실수 표기·−0.0, npy 헤더, NaN 비트, config 순서와 int/float 구분).
5. tiny 굽기를 두 번 돌리면 manifest 의 seconds 말고 모든 파일이 바이트 단위로 같았습니다. C# 도 같은 결정성을 시험합니다.
6. CLI 경로는 그대로 따릅니다. planet 텍스처는 메모리 값으로 만들고, hero·bake 는 다시 읽은 묶음으로 돌립니다. 지름길을 두면 golden 과 어긋납니다.
7. 단계 4 엔진 코드에서는 `Bpcg.Bake.Mesh` 와 `Godot.Mesh` 이름이 겹치므로 별칭을 씁니다.

### bake ② — detail·globe·globe_text

범위는 `src/bpcg/bake/detail.py`(504줄), `globe.py`(1089줄), `globe_text.py`(213줄)입니다. 계약은 docs/pipeline.md 11절(프랙탈 디테일, bake/globe)과 engine/GLOBE.md 의 '파일 형식 요약'에 있습니다. 시험은 tests/test_detail.py, test_globe.py, test_engine_globe.py 입니다. 아래 '확인'은 macOS 26.6.2 arm64, Python 3.13.15, numpy 2.5.3(Accelerate), scipy 1.18.1, numba 0.68.0 에서 tiny 실행(out/tiny_check)의 실제 입력으로 실험한 결과입니다.

- globe 는 numba 를 쓰지 않고 결정적입니다. 같은 묶음으로 다시 구우면 .bin 이 바이트 단위로 같고, globe.json 도 seconds 와 git_commit 을 빼면 같습니다. 이 기계에서 numpy float64 의 tan·arctan·arctan2·log10·exp·cos·log2·power·hypot 은 Apple libm 과 각 20만 개에서 비트 단위로 같습니다. 그래서 libm 을 부르는 .NET Math.* 로 macOS 에서는 비트 일치를 목표로 합니다.
- detail 의 연속값은 FFT, BLAS, LAPACK, FMA 때문에 허용 오차로만 맞출 수 있습니다. 대신 이산 출력(웅덩이 칸, n_filled, float32 heightmap_detail, 동굴 입구 칸)은 완전 일치를 목표로 합니다. 근거는 (b) 의 섭동 실험입니다.
- globe_text 는 글꼴·래스터화·matplotlib 호출이 없는 정적 자료입니다. 한국어 문장, 9 등분해 박아 둔 색표, 범주표와 문장 함수 4개가 전부입니다.

#### (a) 대응표

| Python | C# 파일 | 형 | 위험 |
|---|---|---|---|
| `bpcg.bake.detail` | `csharp/Bpcg/Bake/Detail.cs` | `public static class Detail` + 중첩 record `Settings`, `FractalDiag`, `WeightParts`, `DetailMeta` | 높음 |
| `bpcg.bake.globe` | `csharp/Bpcg/Bake/Globe.cs` | `public static class Globe` + `internal sealed class Resampler`, record `HeroSite`, `MappingCheck`, `ColorStop`, `FaceBasis` | 중간 |
| `bpcg.bake.globe_text` | `csharp/Bpcg/Bake/GlobeText.cs` | `public static class GlobeText` + record `Category(int Id, string Label, int[]? Rgb)` | 낮음 |

2차원 격자는 행 우선 1차원 배열과 (ny, nx) 로 넘깁니다. 함수 `detail_settings` 와 이름이 겹치지 않도록 설정 record 는 `Detail.Settings` 로 둡니다.

| detail (Python) | C# 멤버 |
|---|---|
| 상수 TAPER_OCTAVES … BINS_PER_OCTAVE, NOTE | 같은 값의 `const` (`TaperOctaves`, `PadWavelengths`, `PadMaxCells`, `SlopeSmoothM`, `SoilProbeDepthM`, `SoilSmoothM`, `WaterRampM`, `CaveMarginM`, `CaveRampM`, `SinkRampM`, `BinsPerOctave`, `Note`) |
| `_STREAM_FRACTAL`, `BETA_WAVELENGTH_M` | `internal const long StreamFractal = 7301`, `static readonly (double Lo, double Hi) BetaWavelengthM` |
| `_sub_seed`, `_white_noise_kernel` (비공개) | `long SubSeed(long seed, long k)`, `void WhiteNoiseKernel(long seedA, long seedB, long row0, long col0, double[] dst, int ny, int nx)` |
| `white_noise` | `double[] WhiteNoise(int ny, int nx, long seed, long row0 = 0, long col0 = 0)` |
| `_rfft_k`, `_rfft_weights`, `_raised_cosine` (비공개) | `double[] RfftK(int ny, int nx, double spacingM)`, `double[] RfftWeights(int nx)`, `double RaisedCosine(double t)` |
| `band_limits`, `_band_filter` (비공개) | `(double KLo, double KHi) BandLimits(double spacingM, double minWavelengthM, double maxWavelengthM)`, `(double[] Amp, double[] PowerLaw) BandFilter(double[] k, double kLo, double kHi, double hurst, bool taperTop)` |
| `fractal_field` | `(double[] Field, FractalDiag Diag) FractalField(int ny, int nx, double spacingM, double minWavelengthM, double maxWavelengthM, double hurst, long seed, long row0 = 0, long col0 = 0, double? padM = null)` |
| `_detrend_plane`, `_periodogram` (비공개) | `double[] DetrendPlane(double[] z, int ny, int nx)`, `(double[] P, double[] W, double[] K) Periodogram(double[] z, int ny, int nx, double spacingM)` |
| `band_rms`, `psd_slope`, `central_window` | `double BandRms(double[] z, int ny, int nx, double spacingM, double lamLoM, double lamHiM)`, `double PsdSlope(같은 인자)`, `(double[] Z, int Size) CentralWindow(double[] z, int ny, int nx)` |
| `_grid_neighbors` (비공개), `edge_outlets`, `base_sinks` | `int[] GridNeighbors(int ny, int nx)`, `bool[] EdgeOutlets(bool[] wet, int ny, int nx)`, `bool[] BaseSinks(double[] surf, bool[] wet, int ny, int nx, int[]? nbr = null)` |
| `detail_settings`, `detail_weight` | `Settings? DetailSettings(Config cfg)`, `(double[] M, WeightParts Parts) DetailWeight(double[] surf, double[] gx, double[] gy, int ny, int nx, double voxelM, bool[] wet, IHeroVolumeGrid vol, Settings st)` |
| `_ramp_from`, `_edge_ramp` (비공개) | `double[] RampFrom(bool[] mask, int ny, int nx, double voxelM, double marginM, double rampM)`, `double[] EdgeRamp(int ny, int nx, double voxelM, double fadeM)` |
| `add_fractal_detail` | `(double[] Filled, DetailMeta Meta) AddFractalDetail(double[] surf, double[] gx, double[] gy, int ny, int nx, double voxelM, bool[] wet, IHeroVolumeGrid vol, Config cfg, double[]? mouth = null)` |

| globe (Python) | C# 멤버 |
|---|---|
| 상수 FORMAT … MAPPING_TOL_SLACK, 글 상수 FRAME_RULE·FRAME_RULE_ROTATED·MAPPING·MAPPING_INVERSE·LAYOUT | 같은 값의 `const` (`Format`, `FormatVersion`, `GlobeJson`, `CornersFile`, `DefaultFaceRes`, `MaxFaceRes`, `KNearest`, `SeaLevelM`, `MarkerMaxFraction`, `MappingTolSlack`, `WaterTableGapMaxFraction`, `FrameRule` …) |
| NAN_RGB, PLANET_TO_GODOT, SAMPLING, RIVER_CLASS_EDGES_M3_PER_S, VALLEY_RATIO_RANGE | `static readonly`: `int[] NanRgb`, `double[] PlanetToGodot`(3×3), `(string Key, string Text)[] Sampling`(순서 보존), `double[] RiverClassEdgesM3PerS`, `(double Lo, double Hi) ValleyRatioRange` |
| 모듈 assert, `_say`, `_num`, `_rgb` | 정적 검사 + 시험, `void Say(Action<string>? log, string msg)`, `double Num(double x)`, `int[] Rgb(...)` |
| `to_godot`, `axis_rotation`, `frame_entry` | `double[] ToGodot(double[] v, double[]? rotation = null)`, `double[] AxisRotation(double[] axis)`, `JsonObject FrameEntry(double[] rotation)` |
| `face_bases`, `check_bases` | `double[] FaceBases(FaceBasis? faceBasis = null)`(6×3×3, n·u·v), `void CheckBases(double[] bases, double tol = 1e-9)` |
| `face_directions`, `direction_to_cell` | `double[] FaceDirections(double[] bases, int res, bool corners = false)`, `(int[] F, int[] R, int[] C) DirectionToCell(double[] dirs, double[] bases, int res)` |
| `check_bundle_mapping`, `_face_name` | `MappingCheck CheckBundleMapping(CellGraph graph, double[] bases, double jitter)`, `string FaceName(ReadOnlySpan<double> n)` |
| `lat_lon_deg`, `hero_entry`, `hero_from_run` | `(double Lat, double Lon) LatLonDeg(double[] unit, double[]? rotation = null)`, `JsonObject? HeroEntry(HeroSite? hero, double[]? rotation = null)`, `HeroSite? HeroFromRun(string runDir)` |
| `_Resampler` | `Resampler(double[] unit, double[] bases, int res)`: `int[] Knn`, `int[] Nearest`, `int[] CellOfL0`, `double[] Mean(double[])`, `T[] Pick<T>(T[])`, `byte[] InCell(byte[], string mode)`, `double[] Corners(double[] z, double[] bases)` |
| `_strict`, `_sequential`, `_diverging`, `_elevation_colormap` | `List<ColorStop> Strict(…)`, `Sequential(double lo, double hi, int[][] colors)`, `Diverging(…)`, `ElevationColormap(double dmax, double hmax)` |
| `_pct`, `_plate_rgb`, `_cfg_float`, `cell_km` | `double Pct(double[] v, double q, double dflt)`, `int[] PlateRgb(int k)`, `double? CfgFloat(Config? cfg, string section, string key)`, `double CellKm(double radiusM, int nPerFace)` |
| `_entry`, `_continuous`, `_categorical`, `_area_fraction` | `JsonObject Entry(…)`, `(JsonObject, float[]) Continuous(…)`, `(JsonObject, byte[]) Categorical(…)`, `double[] AreaFraction(long[] ids, double[] area, int n)` |
| `_hydro_note`, `_build_fields`(안쪽 `land_only`·`land_categorical`), `_build_overlays` | `string HydroNote(FieldSet f, bool[] land, double? meanFraction)`, `BuildFields(FieldSet, Resampler, CellGraph, Config?, Action<string>?)` + 지역 함수, `BuildOverlays(FieldSet, Resampler, Config?)` |
| `_write_bin_atomic`, `_old_files`, `copy_globe`, `face_res_error` | `long WriteBinAtomic(string path, ReadOnlySpan<float>)`(byte 겹정의), `HashSet<string> OldFiles(string folder)`, `void CopyGlobe(string src, string dst, IReadOnlyList<string> files)`, `string FaceResError(object faceRes)` |
| `bake_globe`, `main` | `JsonObject BakeGlobe(PlanetState state, Config? cfg, string outDir, int faceRes = 256, HeroSite? hero = null, Action<string>? log = null, FaceBasis? faceBasis = null, string? engineDir = null)`, `int Main(string[] argv)`(포팅 여부는 열린 질문) |

| globe_text (Python) | C# 멤버 |
|---|---|
| GROUPS, ROCK_LABELS | `static readonly string[] Groups`(7), `RockLabels`(12 = N_ROCKS) |
| VIRIDIS, MAGMA, YLGNBU, RDYLBU, RDBU_R, PUOR_R, NONE_RGB, OCEAN_SHORE_RGB | `static readonly int[][] Viridis` …(각 9×3), `int[] NoneRgb`, `OceanShoreRgb` |
| OCEAN_STOPS, LAND_STOPS, SHORE_STOP_M | `(double T, int[] Rgb)[] OceanStops`, `LandStops`, `const double ShoreStopM = -0.5` |
| BOUNDARY_CATS, CRUST_CATS, TEMPLATE_CATS, CAVE_CATS, RIVER_CATS | `Category[]` (`RiverCats[0].Rgb` 는 null) |
| NO_DATA_ID, 문장 상수 7개 | `const int NoDataId = 255`, `const string` 원문(앞 공백 포함) |
| TEXT | `IReadOnlyDictionary<string, (string Label, string Group, string Description, string HowToRead)> Text`(str.format 틀 원문) |
| 함수 4개 | `ElevationLandText(string zName, double? meanFraction)`, `ReliefHydroNote(double? meanFraction, double? gapM, double? valleyRatio)`, `UpliftHow(double loEnd, double hiEnd, bool hasNeg, bool hasPos)`, `TemperatureHow(double loEnd, double hiEnd, double tMin, double tMax)` |

#### (b) 수치 함정 표

| 위치 | 종류 | 내용 | C# 방안 | 비교 |
|---|---|---|---|---|
| detail 70-73 | int-overflow | `np.int64(int(seed))` 는 abs(seed) ≥ 2^63 이면 예외. 갈래 시드는 hash3 >> 1 | `unchecked((long)(Hash3(seed, 7301, k) >> 1))` | exact |
| detail 76-87 | numba-prange | 행만 prange, 칸마다 쓰기. `row0 + j` 는 정수로 정확(2^60 오프셋으로 확인), 음수는 uint64 로 재해석 | 행 Parallel.For, `long r = row0 + j`, `unchecked((ulong)r)` | exact |
| detail 85-87 | libm | Box–Muller 의 log·cos 는 libm. CPython libm 재구현과 비트 차이 0 | `Math.Log`, `Math.Cos`, `TwoPi = 2.0 * Math.PI` | exact(macOS) |
| detail 104-107 | freq-grid | k = 정수 × (1/(n·d)). 띠·첫 옥타브·psd 칸 경계 비교에 씀 | 같은 식 | exact |
| detail 108, 380 | hypot | Apple hypot 는 정확 반올림이 아님(15.9% 다름). sqrt(fma(min,min,max·max)) 와 1,083,145 k + 7,701 경사에서 일치 | `PyMath.Hypot`(inf·NaN·비례 가드 + FMA 식, 열린 질문) | exact |
| detail 120-123 | clip | np.clip 은 -0.0·NaN 을 그대로 둠 | `x < lo ? lo : (x > hi ? hi : x)` | exact |
| detail 133-144 | 마스크·pow | 마스크는 정확 비교, `kk ** -(1+H)`·log2·cos 는 libm | 원소 순서 그대로 | exact(macOS) |
| detail 191-192 | nan-int | `int(ceil(…))` 은 NaN·inf 에서 예외, C# 는 포화 | 유한성 검사 뒤 변환 | n/a |
| detail 203, 211 | pairwise | 2차원 전체 `.sum()` = 평평한 배열 pairwise, (w·amp)·amp | `NpReduce.Sum` | exact |
| detail 212 | fft | pocketfft: r2c 마지막 축 → c2c, 역은 c2c(1/ny) → c2r(1/nx). c2r 은 DC·나이퀴스트 허수부를 버림. tiny 길이 89 는 Bluestein, arm64 FMA 3,264개 | pocketfft 계획 이식(FMA 없음) | 허용 abs ≤ 1e-13 |
| detail 233-237 | blas | `z @ ic` 는 Accelerate gemv 라 순서를 알 수 없음(상대 2.4e-11) | 순차 `LinAlg.Dot` | 허용 |
| detail 252-254 | window | hanning = 0.5 + 0.5·cos(π·n/(M-1)), mean(win²) 은 pairwise | 같은 식 | 허용 |
| detail 277-287 | binning | n_bins = ceil(log2(비)·4), edges = k_lo·pow(비, i/n), searchsorted(right)-1 뒤 clip, bincount 는 순차 | `SearchSortedRight`, 순차 누적 | exact |
| detail 288 | lapack | polyfit 은 열 비례 조정 + gelsd | `Polyfit.Fit`(QR) | 허용 abs ≤ 1e-9 |
| detail 333, 472-473 | flood | n_filled·채움 칸. 상대 4e-14 섭동 300회에서 불변, 이웃 높이 차 ≥ 2.9e-5 m, float32 경계 ≥ 1.4e-9 m | Hydro.FillDepressions | exact |
| detail 378, 386 | fma | Correlate1D 대칭 경로가 fmadd. FMA 없으면 60–65% 칸만 같음 | `NdImage.GaussianFilter`(FMA 는 열린 질문) | 허용 |
| detail 379 | gradient | 안쪽 (f[i+1]-f[i-1])/(2·dx), 끝 (f1-f0)/dx | 같은 식 | exact |
| detail 390, 403 | edt | 정확한 sqrt(정수). 무차별 계산과 비트 일치 | 정확 EDT(long 제곱) | exact |
| detail 450 | round | Python round 는 짝수 쪽(x.5 가능) | `Math.Round`(ToEven) | exact |
| detail 454 | div-zero | top = 0 이면 ZeroDivisionError(잡히지 않음), C# 는 inf | 명시적으로 던짐 | n/a |
| detail 466 | 순서 | z = surf + (amp·m)·field | 왼쪽 결합 그대로 | 허용 |
| detail 476-503 | meta | 24키 순서, 정수/실수 구분, NaN → null | `DetailMeta.ToJson` | 허용(정수는 exact) |
| globe 125-127 | 6자리 | `.6g` 는 정확 값의 짝수 반올림, +0.0. tiny 경계 여유 3.75e-11 | `PyFormat` + `double.Parse` | exact |
| globe 141, 161 | -0.0 | `+ 0.0` 이 -0.0 을 지움(json 은 '-0.0') | 명시적 0 정리 | exact |
| globe 152-161 | 작은 BLAS | 3차원 norm·dot·3×3@v 는 순차 무-FMA 와 같음(10만 개) | `Dot3`, `Cross3` | exact |
| globe 166, 275, 912 | allclose | rtol 1e-5 기본값, 비대칭 식 | `AllClose(rtol, atol)` | exact |
| globe 214-223 | 순서 | t 식, tan((a·π)/4), (n+X·u)+Y·v, 노름 순차 | 원소 식 그대로 | exact(macOS) |
| globe 231-237 | argmax | 첫 최대, (4/π)·atan, floor. 경계 여유 ≥ 3.9e-5 칸 | 엄격한 > 비교 | exact |
| globe 286-291 | degrees | 두 번 정규화, x·(180/π) | `x * (180.0 / Math.PI)` | exact(macOS) |
| globe 375-382 | knn | cKDTree 는 FMA 거리, 동점은 힙 순서. 간격 ≥ 8.5e-8 | KdTree (거리, 번호) | exact |
| globe 384-390 | 합 순서 | ((v0+v1)+v2)+v3 / max(cnt,1). float32 출처라 순서와 무관 | knn 순서 순차 합 | exact |
| globe 410-421 | 꼭짓점 | query_pairs r=1e-9: 겹침 ≤ 2.3e-16, 나머지 ≥ 4.3e-3. 가장 작은 번호가 대표, 고유 6N²+2 | QueryPairs + Unique | exact |
| globe 432-451 | 색표 | 6자리로 줄인 뒤 엄격 증가 검사(B1). k/half 는 참 나눗셈 | `(double)k / half` | exact |
| globe 466-469, 591 | 분위수 | percentile linear 와 median((a+b)/2)은 정의가 다름 | `NpStats` 둘 다 | exact |
| globe 472-476 | 반올림 | 0.70·255 = 178.5 동점(홀수 판 128회) → 178 | `Math.Round`(ToEven) | exact |
| globe 568-570 | 넓이 몫 | bincount 는 순차, area.sum() 은 pairwise | `NpReduce.Sum` | exact |
| globe 779 | NaN | searchsorted 는 NaN 을 가장 큰 값으로 봄(등급 4) | NaN 규칙 포함 | exact |
| globe 893-1035 | json | indent 2, ensure_ascii False, NaN → null, 키 순서. seconds·git_commit 은 실행마다 다름 | PyJson, 두 키는 비교에서 뺌 | exact |
| globe 1063-1067 | 묶음 | 지구본은 float32 로 저장된 묶음에서 구움 | C# 도 묶음을 거침 | exact |
| globe_text 148-213 | 형식 | g·.2g·.0f 는 짝수 반올림, (-0.5,0) 은 '-0', 지수 표기 '1.5e-05' | `PyFormat` | exact |

#### (c) 외부 호출과 대체 방식

| 외부 호출 | 쓰는 곳 | C# 대체 | 비교 |
|---|---|---|---|
| `np.fft.rfft2`, `irfft2(s=)` | detail 212, 253 | `Bpcg.Numerics.Fft` (pocketfft 계획: radix 2·3·4·5·7·11·일반, Bluestein, 비용 규칙) | 허용 |
| `fftfreq`, `rfftfreq`, `np.hypot` | detail 104-108, 380 | 정수×(1/(n·d)), `PyMath.Hypot` | exact |
| `ndimage.gaussian_filter(mode='nearest')` | detail 378, 386 | `NdImage.GaussianFilter`(커널 exp/pairwise 합, 반지름 int(4σ+0.5), 0번 축 → 1번 축) | 허용 |
| `ndimage.distance_transform_edt` | detail 390, 403 | `NdImage.DistanceTransformEdt` | exact |
| `np.polyfit(deg 1)` | detail 288 | `Polyfit.Fit` | 허용 |
| `@` (Accelerate gemv·ddot) | detail 235-236, globe 3차원 | `LinAlg.Dot`, `Dot3` | 허용 / exact |
| `searchsorted(right)`, `bincount(weights)` | detail 281-287, globe 569, 779 | `NpSearch.SearchSortedRight`, 순차 누적 | exact |
| `np.percentile(linear)`, `np.median` | globe 469, 591, 598 | `NpStats.Percentile`, `NpStats.Median` | exact |
| `cKDTree.query(k=4, workers=-1)` | globe 378, 419 | `KdTree.Query`((거리, 번호) 순, 점마다 병렬 가능) | exact |
| `cKDTree.query_pairs(r=1e-9, 'ndarray')` | globe 414 | `KdTree.QueryPairs` 또는 같은 쌍을 내는 격자 해시 | exact |
| `np.unique(return_inverse)`, `minimum.at`, `maximum.at`, `bitwise_or.at` | globe 401-417 | `NpSort.Unique`, 순차 반복 | exact |
| `tan`, `arctan`, `arctan2`, `log10`, `log2`, `exp`, `cos`, `pow`, `math.asin`, `math.atan2` | detail·globe | `Math.*`(macOS 는 같은 libm) | macOS exact |
| `colorsys.hsv_to_rgb` | globe 476 | 3.13 원문을 옮긴 `PyColorsys.HsvToRgb` | exact |
| `json.dumps(indent=2)`, `format(x, spec)`, `str.format` | globe 127, 505, 1026 | `Bpcg.IO.PyJson`, `PyFormat` | exact |
| `git_commit()`, `time.perf_counter` | globe 964, 893-1025 | `GitInfo.Commit()`, `Stopwatch` | 비교 제외 |
| `os.replace`, `shutil.copy2` | globe 833, 858-859 | `File.Move(overwrite: true)`, `File.Copy` | 바이트 |
| `fill_depressions`, `hash3`·`hash_unit`, `HeroVolume.evaluate_grid` | detail | `Bpcg.Hydro.Depressions`, `Bpcg.Core.Hashing`, `Bpcg.Volume`(가짜 주입 인터페이스) | exact |

#### (d) numba 커널과 prange

- `detail._white_noise_kernel`: `@njit(cache=True, parallel=True)` 이고 prange 는 행 j 하나입니다. 칸마다 한 번 쓰기만 하고 축약 변수가 없으므로 `Parallel.For`(행)로 옮겨도 스레드 수와 무관합니다. 해시는 `unchecked` ulong 연산이고, Box–Muller 는 libm log·cos 입니다.
- 간접 호출: `hydro.depressions._fill_kernel`(순차 @njit)을 `base_sinks` 와 마지막 채움에서 두 번 부르고, `HeroVolume` 커널(volume)도 씁니다. 각 묶음의 대조 시험이 따로 보장합니다.
- globe 와 globe_text 에는 numba 가 없습니다. `cKDTree.query(workers=-1)` 는 질의 점마다 독립이라 병렬로 옮겨도 됩니다. `ufunc.at` 누적은 순차 반복으로 둡니다.

#### (e) 의심 버그

1. B1, `globe.py:425-436`: `_sequential` 은 hi == lo 만 +1 로 막습니다. 0 < hi-lo 가 아주 작으면 6자리로 줄인 매듭이 겹쳐 `_strict` 가 ValueError 를 내고, 지구본 굽기 전체가 멈춥니다. `_sequential(0.5, 0.500004)` 과 `(1000, 1000.001)` 에서 재현했습니다. 강수나 유량이 거의 고른 행성에서 생길 수 있습니다.
2. B2, `detail.py:454`: `top_octave_rms_per_std` 가 0 이면 ZeroDivisionError 가 납니다. 회랑은 ValueError 만 잡으므로 `bpcg bake` 가 멈춥니다. 64×64, voxel 2 m, max_wavelength_m 5000 에서 재현했습니다. 기본 설정에서는 생기지 않습니다.
3. B3, `globe_text.py:190-210`: `.0f` 가 (-0.5, 0) 의 값을 '-0' 으로 써서 '≈ -0 °C 이하' 같은 문장이 나옵니다. 보기 문장의 흠뿐이지만 C# 도 같은 '-0' 을 내야 합니다.
4. B4, `detail.py:440`: 모양 검사에 gy 가 빠져 있습니다. 영향은 낮습니다.

모두 고치지 않고 같은 동작으로 옮깁니다.

#### (f) golden 대조 계획

포획 방법은 다음과 같습니다. detail 은 tiny 히어로 묶음으로 `bake_corridor` 를 돌리면서 `bpcg.bake.corridor.add_fractal_detail` 을 감싸 입력과 출력을 저장합니다. 이 방법으로 out/tiny_check 와 비트 단위로 같은 결과를 확인했습니다. 지표 0.5 m 아래 `solid_material` 격자도 함께 저장해 C# 쪽은 가짜 볼륨으로 받습니다. globe 는 `run_globe` 와 같은 경로(`load_planet_state`, `face_basis`, `hero_from_run`)로 입력을 만듭니다.

| 대상 | 입력 | 출력 | 크기 | 비교 |
|---|---|---|---|---|
| `WhiteNoise`, `SubSeed` | tiny 패딩 창 (89,189,0,87,-363)과 2^60·음수·큰 시드 경계 사례 | float64, long | 140 KB | exact(macOS) |
| `RfftK`, `RfftWeights`, `BandLimits` | (89,189,8), (51,151,8), (51,51,8), (64,64,2), (7,8,1) | k, w, 띠 | 200 KB | exact |
| `BandFilter` | tiny k + taperTop 거짓·참 | amp, power_law | 150 KB | 0 패턴 exact, 값 exact(macOS) |
| `FractalField` | tiny 호출 값, 시험 값, pad 0, 예외 2건 | field, diag | 0.7 MB | field abs ≤ 1e-13, diag exact |
| `DetrendPlane`, `BandRms`, `PsdSlope`, `CentralWindow` | tiny surf·filled, 사인+평면 신호, NaN 사례 | 배열, anchor, β | 0.3 MB | 허용(1e-9·max, rel 1e-12, abs 1e-9) |
| `GridNeighbors`, `EdgeOutlets`, `BaseSinks` | tiny surf·wet, 웅덩이 손 사례 128² | nbr, outlet, sink | 0.3 MB | exact |
| `DetailSettings` | 설정 6가지(기본, gain 0, 절 없음, NaN, hurst 1.5, edge 없음) | 값·null·예외 | 5 KB | exact |
| `DetailWeight`, `RampFrom`, `EdgeRamp` | tiny 포획 + 재질 격자, 동굴·빈 마스크, fade 50·0 | m, 항별 지도 | 0.5 MB | EDT 항 exact, 경사 항 abs ≤ 1e-12 |
| `AddFractalDetail` | tiny 포획 전체, 합성 지표 128²(웅덩이·입구) | filled, float32, meta | 0.6 MB | float32·정수 exact, filled abs ≤ 1e-9 m |
| `FaceBases`, `AxisRotation`, `ToGodot`, `FrameEntry` | 기저 4가지(오류 2), 축 4가지(오류 1) | 행렬, JSON | 10 KB | exact |
| `FaceDirections`, `DirectionToCell` | res 2·3·32 중심·꼭짓점, L0 6,144개 + 무작위 4,000 방향 | 방향, 칸 | 0.5 MB | exact(macOS) |
| `CheckBundleMapping` | tiny graph, jitter 1.0, 오류 사례 | MappingCheck | 0.2 MB | exact |
| `Resampler` | tiny 묶음, res 32(커밋)·256(out/golden) | knn, Mean, Pick, InCell, Corners | 0.7 MB / 20 MB | exact |
| 색표·문장 도움 함수 | Num 동점, Sequential 예외, PlateRgb 0..255, Pct q 6개 | 값·예외 | 50 KB | exact |
| `HeroEntry`, `LatLonDeg`, `HeroFromRun` | 시험 벡터, 기울인 축, tiny manifest | JSON, (lat, lon) | 10 KB | exact(macOS) |
| `BakeGlobe` 끝까지 | tiny 묶음 res 32·256, 드문 동굴·호수 주입 | .bin 바이트, globe.json | 0.3 MB / 15.7 MB | exact(seconds·git_commit 제외) |
| `GlobeText` | 상수 덤프, 함수 4개 동점·'-0' 사례 | JSON, 문자열 | 60 KB | exact |

#### (g) 포팅 순서와 위험 메모

1. `GlobeText` 와 `PyFormat`(f·g·e 형식, 짝수 반올림, '-0', nan, str.format 틀)을 먼저 만듭니다. 모든 문장 비교의 바탕입니다.
2. `PyJson`(순서 DOM, Python repr 실수, indent 2, ensure_ascii False)과 `.bin` 원자 쓰기를 bundle 쪽과 함께 확정합니다.
3. globe 의 좌표 함수(`FaceBases` → `FaceDirections` → `DirectionToCell` → `CheckBundleMapping`)를 옮기고, 그다음 `KdTree`(Query, QueryPairs)와 `Resampler` 를 옮깁니다. 이 단계부터 macOS 에서 비트 일치를 요구합니다.
4. 색표, 필드 목록, 덧그림, `BakeGlobe` 를 옮겨 tiny res 32·256 끝까지 비교합니다. C# 파이프라인이 묶음(float32)을 거쳐 굽는지 확인합니다.
5. detail 은 `WhiteNoise`·`SubSeed`(exact) → `PyMath.Hypot`·`RfftK`·`BandFilter` → `Fft` → `FractalField`(허용 오차) 순서입니다.
6. 그다음 `NdImage`(GaussianFilter, DistanceTransformEdt), `DetrendPlane`·`Periodogram`·`BandRms`·`PsdSlope`·`Polyfit`, `DetailWeight` 를 옮기고, 마지막에 `AddFractalDetail` 을 옮겨 이산 출력의 완전 일치를 확인합니다.
7. 위험 메모입니다. (가) FFT, gemv, lstsq, gaussian 의 FMA 때문에 detail 연속값은 비트 일치가 불가능합니다. 이산 출력이 갈리면 허용 오차를 넓히지 않고 칸 수·위치·원인을 적습니다. (나) golden 은 macOS arm64 에서 만든 것이 기준입니다. 다른 OS 의 libm 이나 x86 빌드와의 차이는 열린 질문으로 남깁니다. (다) jitter 0 행성은 kNN 동점이 생길 수 있어 별도 사례로 봅니다. (라) 의심 버그 B1·B2 는 같은 예외로 재현해야 하므로, ValueError 와 다른 예외의 C# 형 대응이 필요합니다.

## 7. golden 자료와 대조 시험 계획

- **만드는 법.** `uv run python csharp/golden/export_golden.py` 한 번으로 모두 다시 만듭니다. 스크립트는 `src/bpcg` 함수를 부르기만 하고, ruff format·check를 지킵니다. 입력은 tiny 프로필 크기와 손으로 만든 경계 사례입니다.
- **놓는 곳.**
  - 모든 사례: `out/golden/<모듈 경로>/<사례>.npz` + `<사례>.json`. `BPCG_OUT`을 따르고 git에서 빠집니다.
  - 커밋하는 사례: 작은 손 사례(각 1 MB 이하)는 `csharp/golden/data/`에도 씁니다(`.gitignore` 예외). Python 없이도 핵심 대조가 돕니다.
  - `out/golden/manifest.json`: Python·numpy·numba·scipy 판, 운영체제, git 커밋, 사례 목록.
- **사례 파일.** npz 안의 배열 이름은 입력 `in.<이름>`, 출력 `out.<이름>`입니다. 글자·설정·예외 핵심어처럼 배열이 아닌 값은 같은 이름의 JSON에 둡니다.
- **시험 구성.** `csharp/Bpcg.Tests/<Pkg>/<Mod>Tests.cs`에 Python 모듈 하나당 시험 클래스 하나, 함수·numba 커널마다 시험을 하나 이상 둡니다. 시험은 Python이 받은 입력을 그대로 받아 출력만 비교합니다. golden이 없으면 건너뛰지 않고 실패합니다(만드는 명령을 메시지로 알림).
- **비교 기준** (`Bpcg.Tests.Compare`).
  - 정수·범주·불리언·셀 번호·흐름 방향·정렬 순서: 완전히 같아야 합니다.
  - 사칙연산과 제곱근만 쓰는 실수: 비트 단위로 같아야 합니다(−0.0 포함).
  - 초월 함수를 거친 실수: macOS에서는 비트 일치를 기대합니다(5장). 다른 OS에서는 시험마다 근거를 주석으로 단 상대 오차(ulp 단위)를 씁니다.
  - 묶음 전체(단계 3): float32 정밀도입니다(docs/conventions.md 3절).
- **흐름 방향이 갈릴 때.** 반복 풀이에서 거의 같은 높이 때문에 방향이 갈리면 허용 오차를 넓히지 않습니다. 대신 갈린 칸의 수·위치·원인을 이 기록의 3장 '대조 결과'와 해당 모듈 절에 적습니다.
- **사례 목록.** 모듈마다 6장 각 절의 '(f) golden 대조 계획' 표를 따릅니다. 단계 1은 core ①·② 표와 `Bpcg.IO` 시험 벡터(실수 표기, npy 머리글, TOML 정수 경계)부터 만듭니다.

## 8. 출력 파일과 끝에서 끝 대조 계획 (단계 3)

### 출력 파일과 끝에서 끝 대조 계획

이 절은 C# 콘솔이 `planet`·`hero`·`bake`·`all`에서 써야 하는 파일 전부와, 단계 3에서 그 파일을 Python 결과와 맞대는 규칙을 정합니다. 단계 0에서 tiny 프로필로 확인한 바탕은 다음과 같습니다(Apple M4 Pro 8P+4E, macOS 26.6.2, 커밋 2dc1d7b).

- Python `bpcg all --profile tiny`를 두 번 돌리면 157개 파일 가운데 153개가 바이트 단위로 같습니다. 나머지 JSON 4개도 `seconds` 키의 값과 planet `meta.diag.threads`만 다릅니다. `NUMBA_NUM_THREADS=1`로 돌린 결과와 다른 세션이 만든 `out/tiny_check`도 같은 규칙으로 같습니다.
- `planet` → `hero --from` → `bake --hero`를 따로 돌린 결과도 `all`과 같습니다.
- 그러므로 아래에서 가리는 값을 빼고 C#과 Python이 다르면, 그 차이는 모두 구현 차이로 보고 원인을 찾습니다.

#### 출력 파일 목록

N은 칸 수(tiny 행성 6144, 히어로 4096)입니다. 규칙 열의 기호는 '파일별 대조 규칙'에서 정합니다. tiny `all`은 157개 파일, 약 23 MB를 씁니다(그 가운데 지구본 15.7 MB는 프로필과 무관합니다). laptop은 약 1.1 GB입니다.

| 경로 (실행 폴더 기준) | 쓰는 함수 | 형식·dtype·모양 (tiny) | 내용 | 규칙 |
|---|---|---|---|---|
| `planet/manifest.json` | `bundle.write_bundle` → `write_json` | JSON | format `bpcg-bundle`, format_version 1, kind, bpcg_version, git_commit, config_digest, seed, profile, graph, face_basis, fields[], rivers(null), config, meta{info, diag} | J |
| `planet/graph.npz` | `write_bundle` (`np.savez`) | 구성원 순서 pos `<f8` (N,3), nbr `<i4` (N,8), dist `<f8` (N,8, 없는 이웃 +inf), area `<f8` (N,) | CellGraph | Z |
| `planet/<그룹>/<이름>.npy` 51개 | `write_bundle` (`np.save`) | FIELDS dtype: float32, float64(discharge·drainage_area·sediment_flux), bool, uint8, int8(subduction_side), int32(plate_id·receiver). (N,), 단 strata_bottom_m (N,6) float32, strata_rock (N,7) uint8 | 필드 | A |
| `planet/textures/<층>_<면>.png` 24개 | `textures.face_textures` (`matplotlib.image.imsave`) | PNG RGBA8, n×n (32×32) | elevation·plate·ocean_age·precip. 픽셀 = trunc(RGBA·255)(반올림으로 하면 elevation_0 한 장에서 1455개 값이 다름), 맨 아래 줄 = j 0. 메모리의 float64 필드로 그림 | P |
| `planet/textures/textures.json`, `planet/scorecard.json` | `face_textures`, `cli.run_planet` → `write_json` | JSON | 층별 파일·값 범위·면 기저 / `meta.diag.scorecard`와 같은 내용 | J |
| `hero/manifest.json`, `hero/graph.npz`, `hero/<그룹>/<이름>.npy` 36개(평면 히어로는 dist_convergent_m을 더해 37개), `hero/scorecard.json` | `save_hero_state`, `run_hero` | 위와 같음, 평면 그래프 64×64 | meta{site, fan_apexes, diag} | J·Z·A |
| `hero/rivers.npz` | `write_bundle` | cells `<i8` (평평), offsets `<i8` (구간 수 + 1) | 강 구간 | Z |
| `corridor/{heightmap,water,water_table,cave_mouth}.bin/.json` | `heightmap.write_heightmap` | 머리글 없는 `<f4` 행 우선 + JSON, 151×51 | 회랑 지표, 수면(없으면 −10000), 지하수면, d_cave(±20 m) | A + J |
| `corridor/{heightmap_detail,cave_mouth_detail}.bin/.json` | 같음 | 같음 | `detail.fractal_gain` > 0이고 띠가 있을 때만 씀(아니면 남은 파일을 지움) | A + J |
| `corridor/surround25.bin/.json`, `corridor/strata_top.bin/.json` | `write_heightmap` | `<f4` 64×64, 301×101 | 히어로 전체 z_m, 재질 부피 윗면 | A + J |
| `corridor/strata.u8`, `corridor/strata.json` | `corridor.bake_strata` | 머리글 없는 uint8 [행][층][열] 101×153×301 + JSON | 재질 0..11, 254 물, 255 공기, counts | B + J |
| `corridor/caves.glb` | `mesh.export_glb` (trimesh 5.1.0) | glTF 2.0 바이너리: indices u32, POSITION f32, COLOR_0 u8 정규화, NORMAL f32 | 동굴 벽. 삼각형이 없으면 쓰지 않고 남은 파일을 지움(평면 tiny가 이 경우) | G |
| `corridor/entrances.json`, `corridor/manifest.json` | `bake_corridor` → `write_json` | JSON (entrances는 `max_array=None`) | 입구 캡슐 양 끝(`ndarray.round(3)`) / frame, site, corridor, hero, files, file_meta, caves, seconds | J |
| `globe/globe.json` | `globe.bake_globe` → `write_json(max_array=None)` | JSON | 면 기저, 사상, 필드 설명·색표, hero, file_bytes | J |
| `globe/corners_elevation_m.bin` | `globe._write_bin_atomic` | `<f4` (6, 257, 257) | 꼭짓점 고도 | A |
| `globe/<필드>.bin` 13개, `globe/{rivers,lakes}.bin` | `_write_bin_atomic` | `<f4` 또는 `u1` (6, 256, 256) | 연속 필드(NaN = 값 없음), 범주 필드(255 = 값 없음), 덧그림 | A·B |
| (`--engine`) `engine/baked/*`, `engine/baked/globe/*` | `shutil.copy2`, `globe.copy_globe` | 위 파일의 복사본 | 단계 3에서는 쓰지 않음 | — |

C#이 함께 지킬 쓰기 규약은 다음과 같습니다.

- JSON, 높이맵, strata.u8, 지구본 .bin, glb는 `<이름>.part`에 쓴 뒤 이름을 바꿉니다. `np.save`·`np.savez`·PNG는 바로 씁니다. 엔진이 .json을 보고 .bin을 읽으므로, 높이맵은 .bin 다음에 .json을 쓰고 지구본은 .bin을 모두 쓴 다음 globe.json을 씁니다. 엔진 폴더로 복사할 때도 globe.json을 맨 끝에 옮깁니다.
- 다시 구울 때의 정리: 디테일을 끄면 `*_detail` 쌍을, 동굴 삼각형이 없으면 `caves.glb`를 지웁니다. 지구본은 지난 globe.json이 가리키던 .bin 가운데 이번에 쓰지 않은 것을 지웁니다(`globe._old_files`). 엔진 폴더에도 같은 정리를 합니다.
- npy는 numpy 2.5.3의 v1.0 머리글을 바이트까지 따릅니다. 키를 정렬한 `{'descr': '<f4', 'fortran_order': False, 'shape': (6144,), }`(descr은 `<f4`·`<f8`·`|b1`·`|u1`·`|i1`·`<i4`·`<i8`) 뒤에 `21 − len(repr(shape[0]))`칸의 공백이 오고, 전체를 64바이트 경계에 맞추는 공백(1..64칸)과 줄바꿈 한 바이트가 붙습니다. 단계 0에서 본 모양((0,)부터 (10⁹,), 2차원 포함)에서는 머리글이 늘 128바이트였습니다. bool은 0/1 바이트로 씁니다.
- npz는 무압축(stored) zip이고 구성원 순서는 위 표와 같습니다. Python이 쓰는 zip 머리에는 항목 시각 1980-01-01, 지역 머리의 zip64 확장, 중앙 디렉터리의 `version made by` 0x032d가 들어갑니다. 이 값이 Windows에서는 0x002d가 되므로, 컨테이너 바이트는 계약에 넣지 않습니다.
- NaN은 양수 quiet NaN(`0x7FC00000`, `0x7FF8000000000000`)으로 써서 Python 출력과 비트까지 같게 합니다. Python 출력의 NaN은 모두 이 꼴이었고, .NET의 `double.NaN`은 부호 비트가 1입니다(`double.IsNegative(double.NaN)`이 참).
- JSON은 UTF-8(BOM 없음)과 줄바꿈 LF로 씁니다. 인자 두 개짜리 `File.WriteAllText(path, text)`는 BOM을 쓰지 않습니다. Python `write_json`은 `Path.write_text`를 써서 Windows에서는 CRLF를 쓰지만, 높이맵 JSON은 늘 LF입니다.

#### 실행마다 달라지는 값과 가리는 법

| 값 | 위치 | 달라지는 까닭 | 단계 3 처리 |
|---|---|---|---|
| 걸린 시간 | 이름이 `seconds`인 모든 키: planet `meta.info.seconds`, `meta.diag.seconds`(+`stages_detail`), `meta.diag.{solver,geology,stages.groundwater,stages.strength_limit}.seconds`; hero `meta.diag.seconds`(+`stages_detail`, `find`; 평면은 `presolve`), `meta.diag.{solver,stages.groundwater,stages.warm_start}.seconds`; corridor `seconds`; globe `seconds`(6자리 반올림) | 벽시계 | 값만 가리고, 키 집합·순서·수 형은 비교합니다(studio `summary.py`가 키를 읽음) |
| 스레드 수 | planet `meta.diag.threads` | numba 스레드 수 / C# 병렬 수 | 가림 |
| git 커밋 | planet·hero·corridor manifest와 globe.json의 `git_commit` | 실행한 커밋, git이 없으면 null | 40자 16진 또는 null인지만 봄 |
| 생성기 표지 | PNG `tEXt` `Software=Matplotlib version3.11.2, …`와 `pHYs`; glb `asset.generator` | 라이브러리 이름·버전 | PNG는 디코드해 비교. glb generator를 바꾸면 그 키와 corridor `file_meta.caves.bytes`를 가림 |
| 컨테이너 바이트 | npz zip 머리, PNG IDAT 압축 | 운영체제·zlib | 구성원·픽셀 단위로 비교 |
| 경로 | 콘솔 메시지에만 있음(파일 안에는 없음. `hero_from_run`의 `source`는 globe.json에 쓰지 않음) | `--out`에 준 꼴 | 로그 비교 때 가림 |
| 시각 | 파일 내용에는 없음(npz 항목 시각은 1980-01-01 고정) | — | — |
| 환경 변수 | `BPCG_OUT`(기본 출력 폴더), `NUMBA_NUM_THREADS`·`DOTNET_PROCESSOR_COUNT`(threads 값만 바꿈) | 실행 환경 | 단계 3은 `--out`을 꼭 줌 |

`bpcg_version`(`0.1.0`, JSON 13개 파일)은 가리지 않습니다. C#은 `src/bpcg/__init__.py`의 `__version__`과 같은 상수를 쓰고, 두 값이 같은지 xUnit으로 봅니다. 출력에 이 밖의 실행 의존 값은 없습니다. 집합 순서가 내용에 들어가는 곳도 없어 `PYTHONHASHSEED`와도 무관함을 확인했습니다.

#### 파일별 대조 규칙

먼저 두 실행 폴더의 상대 경로 목록이 같아야 합니다(조건부 파일 포함). 그다음 파일마다 아래 규칙을 씁니다.

| 기호 | 규칙 |
|---|---|
| B | 바이트 같음. 범주 값이라 하나라도 다르면 실패이고, 다른 칸 수·위치·원인을 진행 기록에 적습니다 |
| A | 배열: dtype·모양 같음. 정수(int8·int32·int64·uint8)와 bool은 완전히 같음. 실수는 먼저 비트 일치 수를 보고, 판정은 'float32 정밀도'로 합니다: NaN·±inf 자리가 같고, 나머지는 두 값을 float32로 바꾼 뒤의 ulp 거리가 1 이하(float32 비트를 크기 순서의 단조 정수로 바꾼 차, −0과 +0은 0). 1을 넘는 칸은 수·최대 ulp·위치를 보고하고 허용 오차를 넓히지 않습니다 |
| J | JSON: (1) 글자 꼴 — CRLF를 LF로 바꾼 뒤 `text == json.dumps(json.loads(text), ensure_ascii=False, indent=2) + "\n"` 이 참이어야 합니다(Python 출력 17개 모두 참). 이 검사로 수 표기(`1e-05`, `1.335e+18`, `-0.0`, `1.0`), 이스케이프, 들여쓰기를 값과 따로 봅니다. (2) 내용 — 위 표의 값을 가린 뒤 키 순서까지 같은 트리여야 합니다. int·float 형을 구분하고, 문자열·정수·bool·null은 같아야 하며, 실수는 A의 ulp 규칙을 씁니다. 표시용으로 반올림한 값은 그 자리 한 단위(globe `_num`의 유효 6자리, entrances의 0.001)까지 봅니다. 숫자를 넣은 문장(globe 설명, 점수표 note)이 다르면 숫자 부분과 나머지를 나눠 보고합니다 |
| P | PNG: RGBA8로 디코드한 픽셀이 모두 같음. 보조 청크와 압축 바이트는 보지 않습니다 |
| G | glb: 머리(`glTF`, 2)와 JSON·BIN 청크 구조, JSON 청크 트리가 같음(`asset.generator` 제외, accessor min·max는 A 규칙). JSON 청크 글자 꼴은 `json.dumps(tree, separators=(',', ':'))` 뒤에 공백 `4 − (길이 + 20) % 4`칸(1..4)을 붙인 것이고, accessor 순서는 indices, POSITION, COLOR_0, NORMAL입니다. indices·COLOR_0은 완전히 같고 POSITION은 A를 씁니다. NORMAL은 단계 2 mesh 대조 시험에서 정한 허용 오차를 씁니다(trimesh 5.1.0 법선은 arccos를 쓰는 각도 가중이라 libm 차이가 남음) |
| Z | npz: 구성원 이름·순서가 같고 구성원마다 A(정수 구성원은 바이트 같음) |

판정 보고는 이산 결정부터 냅니다. 다음 값이 다르면 그 뒤 파일은 대부분 통째로 달라지기 때문입니다: planet `receiver`·`is_river`·`plate_id`, hero `meta.site.l0_cell`·`receiver`, solver `iterations`·`converged`·`n_frozen`, corridor `corridor.{start_cell,apex_cell,rect}`, `frame.y_offset_m`(= floor(지표 최솟값)), `caves.faces_kept`, 각종 `n_*` 수.

#### 글자 꼴을 맞출 곳

공통 규칙(실수 표기, 반올림)은 공통 장에 있고, 여기에는 출력에서 쓰이는 곳만 적습니다.

- `write_json`(manifest 둘, scorecard, textures.json, strata.json, entrances.json, corridor manifest, globe.json)은 `json.dumps(obj, ensure_ascii=False, indent=2, allow_nan=False) + "\n"` 꼴입니다. 키는 넣은 순서를 따릅니다. 설정은 TOML 문서 순서이고, `with_overrides`가 새로 만든 키와 `bake_config`가 더한 `[detail]`은 끝에 옵니다. 원소 구분은 `,`, 키 구분은 `: `이고, 빈 dict·list는 `{}`·`[]`로 씁니다. 비ASCII(한글, `°C`, `³`)는 그대로 두고 `"`·`\`·제어 문자만 이스케이프합니다. 높이맵 JSON도 같은 꼴입니다.
- `config_digest`는 `json.dumps(data, sort_keys=True, ensure_ascii=False)`(구분 `, `·`: `, 들여쓰기·끝 줄바꿈 없음)를 UTF-8로 SHA-256 한 16진 앞 12자입니다. 키 정렬은 서수 비교입니다.
- 실수는 Python `repr`을 따릅니다. 자릿수는 가장 짧은 왕복 자릿수이고, 1e-4 ≤ |x| < 1e16이면 고정 소수점(정수 값도 `.0`), 그 밖은 `1e-05`·`1.335e+18` 꼴입니다. 음의 0은 `-0.0`(entrances.json에 12번)이고, float32 값은 double로 넓혀 같은 규칙으로 씁니다(`217.03558349609375`). NaN·inf는 쓰기 전에 null로 바뀝니다. 정수 토큰은 읽을 때도 정수로 남아야 합니다.
- 문장 속 수: globe.json의 `description`·`how_to_read`는 `:.0f`, `:.1f`, `:.2f`, `:.2g`, `:g`를, `_num`은 `:.6g` 뒤 다시 float로 바꾸고 `+ 0.0`으로 −0을 없앱니다. 점수표 `note`는 `:.4f`, `:.0f`, `:.3g`, `:g`를 씁니다. Python은 정확한 이진 값에서 반올림하고 동률이면 짝수로 갑니다(`2.5:.0f` → `2`, `0.125:.2f` → `0.12`, `100000.5:.6g` → `100000`). `-0.00`의 부호를 남기고, `g`는 `1.23457e+06`·`4e+10` 꼴입니다. .NET `F`·`G` 서식과 다르므로 공통 `PyFormat`으로 씁니다.
- entrances.json의 `ndarray.round(3)`은 numpy 방식 `rint(x·1000)/1000`이라 Python `round`와 다릅니다(`np.round(0.0005, 3)` = 0.0, `round(0.0005, 3)` = 0.001).
- 메시지의 `{value!r}`와 dict·list·bool 출력(`{'cave_level_0_m': 0, …}`, `[5763, 234, 147]`, `True`)은 `PyRepr`로 씁니다.

#### `bpcg all`의 순서와 C#이 구현할 읽기 함수

`cmd_all`은 다음 순서로 돕니다. C#도 이 순서를 그대로 지키며, 특히 3~6의 디스크 왕복을 메모리 전달로 바꾸지 않습니다. 메모리 행성으로 만든 히어로는 디스크에서 읽은 행성으로 만든 히어로와 4096칸 모두에서 다르기 때문입니다(z_m 최대 9.2e-5 m, 단계 0 실험).

1. `_config`: `load_config(--planet, --profile)`에 `--set`과 `--seed`(`planet.seed`)를 `checked_overrides`로 덮어씁니다. 이어 `apply_threads`를 부르고, 실행 폴더를 `--out` 또는 `OUT/<planet>`(`BPCG_OUT`)로 정합니다.
2. `--flat`이면 평면 히어로만 만들고 5로 갑니다. 아니면 `run_planet`이 `generate_planet` → `save_planet_state` → `face_textures`(메모리의 float64 필드로 그림) → `scorecard.json`을 차례로 합니다.
3. `cfg = config_from_manifest(read_manifest(planet))`로 설정을 JSON에서 다시 읽습니다(digest는 그대로).
4. `run_hero`: `load_planet_state`(float32 → float64, receiver는 int32 그대로) → `generate_hero` → `save_hero_state`. `ValueError`가 나면 메시지를 찍고 평면 히어로로 바꿉니다.
5. `run_bake`: `bake_config`(히어로 manifest의 설정에 `[detail]`이 없으면 지금 설정 파일의 절을 더함) → `load_hero_state` → `bake_corridor`.
6. `--flat`이 아니면 `run_globe`가 `read_manifest`·`load_planet_state`를 다시 하고, `hero_from_run`으로 방금 쓴 `corridor/manifest.json`(없으면 `hero/manifest.json`)에서 히어로 자리를 읽어 `bake_globe`를 부릅니다. `--flat --engine`이면 엔진의 지난 지구본을 지웁니다.
7. `[전체] 끝: … s → <폴더>`를 찍습니다.

| Python | C# (제안 위치) | 쓰는 곳 | 지킬 점 |
|---|---|---|---|
| `bundle.read_manifest` | `Bpcg.Bake.Bundle.ReadManifest` | hero `--from`, bake, globe, all | `format`이 `bpcg-bundle`이고 `format_version` ≤ 1인지 확인. `PyJson.Parse`는 정수 토큰(`-?\d+`)을 long, 나머지를 double로 읽고 객체 키 순서를 지킴 |
| `bundle.config_from_manifest` | `Bundle.ConfigFromManifest` | 위와 같음 | int·float 구분을 지켜야 digest가 같음(tiny 32a21abfa80a 확인) |
| `bundle.read_bundle` | `Bundle.ReadBundle` | 아래 둘 | graph.npz 네 배열과 필드 .npy를 manifest의 shape·dtype과 맞춰 봄, rivers.npz(cells, offsets) |
| `bundle.load_planet_state` | `Bundle.LoadPlanetState` | hero, globe | 실수 필드는 double로 넓히고 정수는 그대로 둠, `LayerColumns.from_columns`, info·diag는 JSON 트리(`refine._u_max`가 `diag.geology.u_max_m_per_yr`를 읽음) |
| `bundle.load_hero_state` | `Bundle.LoadHeroState` | bake | `meta.site` → HeroSite(lat·lon이 null이면 NaN), `fan_apexes`(dict 목록), rivers, diag |
| `globe.hero_from_run`, `globe._old_files` | `Bpcg.Bake.Globe.HeroFromRun`, `OldFiles` | globe | corridor → hero manifest 순서로 찾음, 지난 globe.json의 파일 목록 |
| `heightmap.read_heightmap` | `Bpcg.Bake.Heightmap.ReadHeightmap` | 시험용(CLI는 안 씀) | REQUIRED_KEYS, 크기 = width·height·4 |
| `np.load`(npy·npz) | `Bpcg.IO.Npy`, `Bpcg.IO.Npz` | 위 모두 | v1.0~v3.0 머리글, stored·deflate 구성원 모두 읽음 |

`meta`에 넣는 값은 `jsonable` 규칙을 따릅니다. NaN·inf는 null이 되고, 원소가 1024개를 넘는 ndarray는 `{"__array__": [모양], "dtype": "float64"}`, 1024개를 넘는 list는 `{"__list__": 길이}`로 줄어듭니다. tiny에서 요약되는 곳은 planet `meta.info.{bathymetry_m, z_platform_base_m, trench_m}`, `meta.info.plates.{age_provisional_myr, spreading_kinematic_m_per_yr}`, hero `meta.diag.extra_inflow_m3_per_yr`입니다. 같은 JSON을 내려면 C#은 진단 값마다 ndarray인지 list인지와 numpy dtype 이름을 들고 있어야 합니다.

#### CLI 표면

| 명령 | 인자 (기본값) | 동작 |
|---|---|---|
| `planet` | `--planet earth`, `--profile laptop`, `--out`(없으면 `OUT/<planet>`), `--seed` int, `--set K=V` 여러 번 | `_config` → `apply_threads` → `run_planet` |
| `hero` | 위와 같음 + `--from DIR`, `--flat` | `--flat`을 먼저 봄(`--from`은 무시). `--from`은 묶음 폴더나 실행 폴더를 받고 묶음의 설정을 씀(`--seed`만 `with_overrides`로), `--set`을 주면 종료 1, 출력은 `--out` 또는 묶음의 부모 |
| `bake` | `--hero DIR`(필수), `--out`(기본은 히어로 옆 `corridor/`), `--engine`, `--seed` int, `--set`(`detail.`·`profile.corridor.`로 시작하는 키만), `--no-globe` | `run_bake` 뒤 히어로 옆에 `planet/manifest.json`이 있으면 `run_globe(planet, 히어로의 부모)`를 부름. 이 호출은 `--out`과 무관하므로 `hero_from_run`이 히어로 부모 폴더의 지난 corridor manifest나 hero manifest를 읽음(그대로 옮김). 행성 묶음이 없고 `--engine`이면 엔진 지구본을 지움 |
| `all` | 공통 인자 + `--flat`, `--engine` | 위 순서 |

- `--set`은 `parse_assignment` → `checked_overrides` 순서로 처리합니다. `parse_assignment`는 첫 `=`에서 나누고 값을 `v = <값>` TOML로 읽으며, 실패하면 BareText로 둡니다. `checked_overrides`는 없는 키에 difflib로 찾은 비슷한 키를 3개까지 붙이고, 절·`planet.name`·`profile.name`을 거부하고, 형식을 맞추며(2.0 → 2), 실수 자리의 nan·inf를 거부합니다. 이어 `[설정] 키 = repr (기본 repr)` 줄을 찍습니다(`planet.seed`는 찍지 않고, bake에서는 `(묶음 …)`).
- 종료 코드: 성공은 0입니다. `SystemExit(문구)`와 처리하지 않은 `ValueError`는 1로 끝나며 stderr에 씁니다. argparse 오류는 2이고 stderr에 usage를 씁니다. argparse는 고유 접두어를 받습니다(`--prof`는 되고, `--se`는 모호해 2).
- C# 위치(제안): `run_planet`·`run_hero`·`bake_config`·`run_bake`·`run_globe`·`_drop_engine_globe`는 단계 4에서 Godot 노드도 부르므로 라이브러리(`csharp/Bpcg/Runs.cs`)에 두고, 로그 함수와 ROOT·엔진 폴더를 인자로 받습니다. 인자 읽기·`_config`·`cmd_*`·종료 코드는 `csharp/Bpcg.Cli/Program.cs`에 둡니다. `config.py`의 `Config`, `bundle.py`의 `Bundle`처럼 클래스 이름이 모듈 이름과 같으면 모듈 함수를 그 클래스의 정적 멤버로 둡니다(`Config.CheckedOverrides`, `Bundle.ReadBundle`).
- 예외: Python `ValueError`에 대응하는 전용 예외형을 두어, `all`의 평면 히어로 대체가 같은 오류만 잡게 합니다(.NET의 `ArgumentException` 류는 잡지 않음). `SystemExit(문구)`는 종료 코드 1인 예외로 옮깁니다.
- 경로: ROOT는 `paths._find_root`처럼 `pyproject.toml`과 `src/bpcg`가 있는 조상 폴더이고, 없으면 현재 폴더입니다. OUT은 `BPCG_OUT` 또는 `ROOT/out`입니다. git 커밋은 ROOT에서 `git rev-parse HEAD`를 10초 제한으로 돌려 얻고, 실패하면 null입니다. 콘솔 출력은 UTF-8로 맞춥니다.
- 메시지: 문구와 수 형식은 Python과 같게 옮깁니다(`PyFormat`·`PyRepr`). 계약으로 보는 것은 종료 코드, 출력 흐름(진행은 stdout), studio `progress.py`가 읽는 줄(`STAGES`의 끝 무늬, `^솔버 반복 (\d+):`, `WARNINGS`)입니다. 나머지 줄은 단계 3에서 경로와 `숫자 s`를 가린 뒤 참고용으로만 맞댑니다(Python 두 실행의 로그는 이 두 가지만 가리면 같아짐을 확인함).

#### 실행 시간 대조 계획

- 잴 값: 출력 파일의 `seconds` 키를 그대로 씁니다. C#은 Python과 같은 지점에서 `Stopwatch.GetTimestamp()`로 재어 같은 키에 넣습니다(`perf_counter`는 이 Mac에서 `mach_absolute_time`, 해상도 41.7 ns). 프로세스 전체 벽시계는 바깥에서 잽니다. '나머지(시작·import·JIT·쓰기·읽기)'는 벽시계 − (planet.total + hero.find + hero.total + corridor.total + globe.total)로 둡니다.
- Python: numba가 `cache=True`라 캐시가 없으면 첫 실행에 컴파일이 들어갑니다(tiny `all` 벽시계가 캐시 없이 11.5 s, 캐시가 있으면 1.46 s, planet.total은 7.20 s와 0.41 s). '첫 실행' 줄은 `NUMBA_CACHE_DIR=<빈 임시 폴더>`로 따로 잽니다(저장소를 건드리지 않음). 본 표는 한 번 돌려 버린 뒤 5번 잰 중앙값입니다.
- C#: Release 빌드로 한 번 돌려 버린 뒤 5번 잰 중앙값을 씁니다. 기본 설정(계층 컴파일) 열과 `DOTNET_TieredCompilation=0` 열을 둡니다. 프로세스마다 JIT를 다시 하므로 그 비용은 '나머지'에 들어갑니다.
- 스레드: 두 쪽 모두 `profile.compute.numba_threads` = 0이라 12입니다. C#은 `k = want == 0 ? Environment.ProcessorCount : min(want, ProcessorCount)`를 `ParallelOptions.MaxDegreeOfParallelism`으로 씁니다. 스레드를 줄여 잴 때는 설정을 바꾸지 않고 `NUMBA_NUM_THREADS`와 `DOTNET_PROCESSOR_COUNT`(.NET 6부터)로 줄입니다(설정을 바꾸면 digest가 바뀜).
- 프로필: tiny 표는 스펙이 요구하는 것이지만, 1초 남짓이라 시작 비용이 대부분입니다. 그래서 laptop(Python 62.0 s: planet 18.9, hero 27.6, corridor 12.6, globe 0.8)도 같은 방법으로 재기를 권합니다. 같은 기기에서, 전원을 연결하고, 다른 무거운 일 없이 잽니다.
- 진행 기록의 표 꼴:

| 단계 (키) | Python [s] | C# [s] | C# TC=0 [s] | C#/Python | 비고 |
|---|---:|---:|---:|---:|---|
| 프로세스 전체 (벽시계) | | | | | 시작·import·JIT 포함 |
| planet.total (materials_coarse, transfer, geology, strength_limit, stages, scorecard) | | | | | stages_detail 10개는 아래 줄로 |
| hero.find + hero.total (sample, geology, boundary, stages, scorecard) | | | | | |
| corridor.total (volume, choose, heightmap, water, cave_mouth, detail, caves, strata) | | | | | |
| globe.total (index, resample, write) | | | | | |
| 나머지 | | | | | |

표 위에는 기기·OS, Python·numpy·numba 버전, .NET SDK·런타임 버전, 스레드 수, 반복 수, 커밋, 프로필을 적습니다. 첫 실행(numba 캐시 없음) 벽시계는 표 아래에 한 줄로 따로 적습니다.

#### 단계 3 대조 사례

명령(제안)은 다음과 같습니다. Python 기준은 `uv run python -m bpcg.cli all --profile tiny --out out/golden/e2e/py`, C#은 `dotnet run -c Release --project csharp/Bpcg.Cli -- all --profile tiny --out out/golden/e2e/cs`, 대조는 `uv run python csharp/golden/compare_e2e.py out/golden/e2e/py out/golden/e2e/cs`입니다. 대조 도구는 Python으로 쓰고 bpcg의 읽기 함수로 C# 파일을 읽으며, 파일별 보고서와 위 시간 표를 냅니다. 이 절 첫머리의 결과는 단계 0에 만든 원형으로 Python 실행끼리 맞대 얻은 것입니다.

1. `all --profile tiny`(시드 0): 위 규칙을 모두 씁니다. Python 기준 결과는 같은 기기에서 다시 만듭니다. 23 MB라 커밋하지 않고, 플랫폼마다 libm이 달라 다른 기기의 결과는 기준이 될 수 없습니다.
2. `planet` → `hero --from` → `bake --hero`를 따로 돌린 결과: 1과 같아야 합니다(Python에서 같음을 확인).
3. `all --profile tiny --flat`: caves.glb 없음, `site.kind`가 `flat_hero`, 지구본 없음 경로를 봅니다.
4. `all --profile tiny --seed 7`: 다른 시드에서도 이산 결정이 같은지 봅니다.
5. 같은 폴더에 `bake --hero … --set detail.fractal_gain=0`: `*_detail` 지우기와 manifest `files` 목록을 봅니다.
6. 섞어 돌리기: Python `hero --from <C# 실행>`, C# `hero --from <Python 실행>`, Python `bake --hero <C# 히어로>`를 돌립니다. 앞 단계에서 온 차이와 이 단계의 차이를 나눠 보고, Python 읽기 함수(`read_bundle`, `load_*_state`, `read_heightmap`)가 C# 파일을 읽는지도 함께 봅니다.
7. 스레드 수 불변: C#을 `DOTNET_PROCESSOR_COUNT=1`과 기본값으로 돌려, 가린 키 밖에서 바이트까지 같은지 봅니다(Python은 같음을 확인).
8. 엔진: `tests/smoke.gd -- --baked-dir=<C# corridor> --expect-baked`와 `tests/globe_smoke.gd`를 C# 출력으로 헤드리스(임시 HOME)로 돌립니다.

### 실행 시간 (단계 3에서 채움)

| 단계 | Python [s] | C# [s] | 비고 |
|---|---|---|---|
| 행성 (1~4단계, 점수표 포함) | | | |
| 히어로 | | | |
| 굽기 (회랑) | | | |
| 지구본 | | | |
| 전체 (`all --profile tiny`) | | | |

## 9. 고칠 규칙·설치 스크립트·CI 목록

### 고칠 규칙·설치 스크립트·CI 목록

C# 포팅 때문에 바꿔야 하는 저장소 규칙·문서·설치 스크립트·CI 를 단계(0 조사, 1 뼈대·core, 2 묶음별, 3 끝에서 끝, 4 Godot 연결)별로 적습니다. 근거는 2026-10-02 의 저장소 파일, scratchpad 에서 한 실험(저장소는 고치지 않음), Godot 4.7.2-stable 소스와 릴리스 파일, .NET 10 문서입니다. 지시는 설치 스크립트와 CI 를 단계 4에서 고친다고 하지만 단계 1부터 '커밋 전 `dotnet build`·`dotnet test` 통과'를 요구하므로, .NET 점검과 `dotnet` CI 작업은 단계 1로 앞당기고 단계 4에는 Godot .NET 판 전환만 남기기를 제안합니다(충돌 4·5).

#### 단계별 변경 목록

| 단계 | 파일 | 바꿀 내용 | 이유 |
|---|---|---|---|
| 0 | `docs/csharp_port_prompt.md`, `CLAUDE.md` | 둘 다 아직 추적되지 않습니다(`git status` 의 `??`). 진행 기록 초안과 함께 커밋 | 지시: 원문 저장, CLAUDE.md 한 줄(이미 있음) |
| 1 | `.gitignore` | 아래 'C#' 절과 golden 예외 두 줄 | 지금은 `csharp/**/bin`·`obj`·`TestResults` 가 안 막히고, `*.npy`·`*.npz`(43·44행)가 커밋할 golden 을 막음(`git check-ignore` 실험) |
| 1 | `.gitattributes` | 아래 6줄 | 줄바꿈은 `* text=auto eol=lf` 로 이미 LF. `diff=csharp` 는 git 내장 드라이버(diff 머리에 메서드 이름) |
| 1 | `.editorconfig` | 아래 `[*.cs]` 절과 XML 프로젝트 파일 들여쓰기 2 | `dotnet format`·IDE·빌드(`EnforceCodeStyleInBuild`)가 같은 기준을 읽음 |
| 1 | `global.json`(새, 저장소 맨 위) | 아래 내용 | SDK 는 현재 폴더에서 위로 찾으므로 맨 위에 둬야 `dotnet test csharp/...` 와 단계 4의 Godot 빌드가 같은 SDK 를 씀 |
| 1 | `csharp/Directory.Build.props`, `csharp/Directory.Packages.props`, `csharp/Bpcg.slnx`, 프로젝트마다 `packages.lock.json` | 아래 속성, 중앙 버전 관리, lock 파일 커밋. 저장소 맨 위에는 `Directory.Build.props` 를 두지 않음 | `uv.lock`·`UV_LOCKED` 와 같은 고정. 맨 위에 두면 단계 4의 Godot 프로젝트에 섞임 |
| 1 | `.github/workflows/ci.yml` | `dotnet` 작업(3 OS), `lint` 에 `dotnet format`·golden 크기 검사, 머리 주석 | 아래 'CI'. 10절 'CI 통과 후 합침'이 C# 에도 뜻을 가지게 |
| 1 | `tools/doctor.py` | `check_dotnet()`: 저장소 맨 위에서 `dotnet --version`(global.json 해석)과 `--list-sdks`. 없으면 [주의] + `setup_cmd('dotnet')`. 3.9 문법 유지 | CI `test` 작업이 3 OS 에서 `setup.sh --check` 로 doctor 를 돌림(ci.yml 73–80행). [실패] 로 두면 CI 가 깨짐 |
| 1 | `scripts/setup.sh`, `scripts/setup.ps1` | `--dotnet`/`-Dotnet`: SDK 점검과 운영체제별 설치 안내(git·curl 처럼 안내만), 도움말·끝 안내 문구. ps1 은 UTF-8 BOM·CRLF 유지 | Godot .NET 은 `DOTNET_ROOT*`, `/etc/dotnet/install_location*`·레지스트리, 기본 위치만 보고 `~/.dotnet`·PATH 는 보지 않음 → 시스템 설치가 안전 |
| 1 | `.github/pull_request_template.md` | C# 확인 4줄: build·test, `dotnet format --verify-no-changes`, golden 다시 만듦·각 1 MB 이하, 진행 기록 갱신 | |
| 1 | `CONTRIBUTING.md` | 처음 한 번 `--dotnet`, 매번 dotnet 명령 3개, 커밋 범위, NuGet 추가 절차(`csharp/Directory.Packages.props` + lock + 라이선스), 필수 상태 검사에 `dotnet` | |
| 1 | `README.md` | 폴더 표 `csharp/`, 옵션 표 `--dotnet`, 자주 쓰는 명령, 문서 목록(진행 기록), 지원 환경의 .NET 10 행 | .NET 10 공식 목록: macOS 15·26·27, glibc 2.27 이상, Windows 11, Windows 10 은 (E)·(IoT) 판만 |
| 1 | `docs/conventions.md` | 한눈에 보기 C#·NuGet 행, 1절 트리(`csharp/`, 빠져 있던 `hero/`·`studio/`)·의존 방향·500줄 예외, 2절 C# 이름, 3절 형 대응, 5절 C# 스타일, 7절 xUnit, 8절 golden, 9절 옮긴 코드 출처·NuGet 라이선스, 10절 범위·PR, 11절 .NET SDK | 아래 충돌 표 |
| 1 | `docs/pipeline.md` | 머리말에 'csharp/Bpcg 도 이 계약을 따르고 차이는 docs/csharp_port.md 에 적음' 한 줄 | 구현 기준을 쓰는 코드가 둘이 됨 |
| 1 | `docs/roadmap.md` | 포팅 단계 0~4 를 따로 적고 단계 4 날짜를 동결일과 함께 정함 | 충돌 3 |
| 1 | `.vscode/extensions.json`, `.vscode/settings.json` | `ms-dotnettools.csharp` 추천, `[csharp]` 저장 때 서식, `dotnet.defaultSolution` = `csharp/Bpcg.slnx`, `files.exclude` 에 `csharp/**/bin`·`obj` | `.gitignore` 는 두 파일만 올리므로 그대로 |
| 1 | `csharp/README.md`, `csharp/golden/data/README.md`(새) | 빌드·시험 명령(첫 문단 영어), golden 출처·다시 만드는 명령·numpy 판·만든 운영체제 | `tests/fixtures/README.md` 와 같은 출처 규칙 |
| 1 | `pyproject.toml`, `uv.lock`, `.claude/launch.json` | 바꾸지 않음 | ruff 0.16.10 이 `csharp/golden/export_golden.py` 를 검사하고 gitignore 된 `bin`·`obj` 는 건너뜀. pytest 는 `testpaths = ["tests"]` 만 모음(실험). launch.json 은 스튜디오 웹 서버 설정 |
| 2 | `docs/csharp_port.md` | 묶음마다 대응표·대조 결과 갱신, `feat(cs-<묶음>)` 커밋 | 지시 |
| 2 | `docs/licenses/`, 옮긴 `.cs` 머리 주석 | 원본 라이브러리·판·파일과 라이선스: scipy(KD-tree, ndimage, find_peaks, RGI, brentq)·scikit-image(marching cubes)·numpy 는 BSD-3, trimesh 는 MIT, matplotlib 색표 | BSD-3 은 재배포 때 저작권 표시가 조건 |
| 2 | `csharp/golden/data/README.md`, `ci.yml` | 커밋 golden 합계 확인, golden 내보내기가 2분을 넘으면 캐시 | |
| 3 | `.github/workflows/ci.yml` | `dotnet` 작업에 끝에서 끝: `uv run bpcg all --profile tiny` 와 `dotnet run --project csharp/Bpcg.Cli -c Release -- all --profile tiny` 를 `csharp/golden/compare_runs.py`(ruff 대상)로 비교. JSON 은 파싱해 비교 | Python 은 `write_text` 라 윈도우에서 CRLF 로 씀(bundle.py 113행). tiny 지구본 필드 하나가 1.57 MB 라 기준 결과는 커밋 못 함 |
| 3 | `pyproject.toml` | Python 쪽 끝에서 끝 시험을 pytest 로 둘 때만 `dotnet` 표시 등록 | `--strict-markers` |
| 4 | `engine/project.godot` | `[dotnet]` `project/assembly_name="BpcgEngine"` 고정 | 비우면 `config/name` 의 'B-PCG' 가 그대로 csproj·어셈블리 이름(path_utils.cpp) |
| 4 | `engine/BpcgEngine.csproj`·`.sln`(Godot 이 만듦) | `TargetFramework` net8.0 → net10.0, `ProjectReference ../csharp/Bpcg/Bpcg.csproj`, 둘 다 커밋 | ProjectGenerator 의 기본값이 net8.0 |
| 4 | `.editorconfig` | `[*.sln]` 에 `indent_style = tab`, `charset = utf-8-bom` | Godot 은 .sln 을 탭·LF·BOM 으로 씀(DotNetSolution.cs) |
| 4 | `engine/scripts/baked_paths.gd` | 아래 코드. 다른 GDScript 는 그대로 | 지시: user:// 를 읽게 하는 정도만 |
| 4 | `engine/tests/smoke.gd` | C# 노드 스크립트를 불러오는 검사 | `_check_scripts` 가 `.gd` 만 봄(83·86행) |
| 4 | `tests/test_engine_smoke.py`, `tests/test_engine_globe.py` | 후보에 .NET 판 경로, 버전 `4.7.2.stable.mono` 요구, 모듈 고정물에서 실제 HOME 으로 `dotnet build engine/BpcgEngine.csproj`, C# 생성 시험 추가 | 아래 '깨지는 시험' |
| 4 | `src/bpcg/studio/godot.py`, `tests/test_studio.py`, `docs/studio.md` | Godot 후보·버전, 띄우기 전 `dotnet build` | `--path engine` 실행은 미리 빌드한 어셈블리가 있어야 함(충돌 6) |
| 4 | `tools/doctor.py` | Godot 은 `.mono` 판만 [ OK ], Godot 이 SDK 를 찾는지(위 세 곳) 점검 | |
| 4 | `scripts/setup.sh`, `scripts/setup.ps1` | `--godot` 이 .NET 판 zip(아래 표)을 받고 한 겹 폴더를 펼침(`GodotSharp/` 를 실행 파일 옆에), `--godot` 이 `--dotnet` 점검 포함, 크기 문구 | 지금 코드는 .NET 판 zip 에서 setup.sh 366행 `chmod` 가 실패하고 macOS 는 `Godot.app` 을 못 찾음 |
| 4 | `.github/workflows/ci.yml` | `engine`: setup-dotnet, `use-dotnet: true`, `.mono` 버전 확인, smoke·globe 두 파일. `setup`: setup-dotnet | 지금 `use-dotnet: false`(92행), globe 시험은 CI 에서 안 돎(107행) |
| 4 | `docs/conventions.md` | 1절 트리의 engine 설명, 11절 Godot 행, 12절 역할·C# 스타일·HOME 규칙 | 충돌 1·2·16 |
| 4 | `engine/README.md`, `engine/GLOBE.md` | 첫 문단, 한눈에 보기 Godot 행, '왜 4.7.2 표준판인가' → '왜 .NET 판인가', 여는 법(`Godot_mono.app`, `…_mono_win64.exe`, 먼저 `dotnet build`), 폴더·커밋 표(`*.cs`, `*.cs.uid`, csproj, sln), 굽기 폴더 `user://baked`. '10/21 복셀 관문'은 그대로 | |
| 4 | `README.md`, `CONTRIBUTING.md` | 엔진 담당은 .NET SDK 필수, `--godot` 이 .NET 판 | |
| 4 | `docs/design/blueprint.md`(원본 Claude 문서 먼저), `docs/design/decision_log.md` | 8장 '파이썬에서 구워 Godot 은 보여 주기만'과 결정 기록에 새 결정 | 13절: 원본이 맞음 |

#### 지시와 저장소 규칙의 충돌

| 충돌 | 지시 | 현재 규칙 | 제안 | 지금 결정 필요 |
|---|---|---|---|---|
| 1 엔진이 계산함 | 단계 4: C# 노드가 생성·굽기를 주 스레드 밖에서 부름 | 12절 '지질·물·동굴을 계산하지 않습니다', 1절 트리, engine/README 5행, GLOBE.md 5행, 설계도 24·148·352행, decision_log 371행 'SDF를 파이썬과 엔진에 두 번 만드는 구조는 9주에 안 끝난다' | 'GDScript 장면은 계산하지 않고, 계산은 엔진 밖 csharp/Bpcg 만 한다. engine/ 의 C# 노드는 Bpcg 를 불러 user://baked 에 쓰는 연결만 한다'로 단계 4에 고치고, 설계도 원본과 결정 기록에 새 결정을 적음 | 예. 결정 기록이 두 구현을 일정 이유로 거절했으므로 포팅 승인을 기록 |
| 2 Godot 판 | 4.7.2 .NET 판 | 11절 '4.7.2 표준판(.NET 아님)', engine/README '왜 4.7.2 표준판인가'(.NET SDK 를 따로 깔아야 함), setup `--godot`, CI `use-dotnet: false` | 단계 1~3 은 표준판 유지(엔진에 C# 없음), 단계 4에 세 사람·CI·스크립트를 한 번에 .NET 판으로 | 아니요(단계 4 시작 확인 때) |
| 3 단계 4 시점 | 단계 4에서 engine/ 전환 | roadmap: 11/11 꼭 할 일 동결, 11/18 데모 동결 뒤 '버그와 화면만' | 단계 4를 11/11 전에 끝내거나 v1.0(12/4) 뒤로. 데모 빌드는 표준판으로 동결 | 예 |
| 4 설치·점검 시점 | 단계 4에 '설치 스크립트…도 같은 단계에서' | 11절 '설치는 setup 스크립트로', 지시 자신의 '커밋 전 dotnet build·test'(단계 1부터) | 단계 1에 doctor 점검([주의])과 `--dotnet`(점검·안내), 단계 4에는 Godot .NET 판 받기만 | 예 |
| 5 CI 시점 | 단계 4에 'CI의 use-dotnet 설정과 dotnet test 실행' | 10절 'CI 통과 후 합침', CONTRIBUTING 필수 검사 `lint`·`test` | 단계 1에 `dotnet` 작업과 `lint` 의 `dotnet format`, 단계 4에는 `engine` 작업만 .NET 으로 | 예 |
| 6 Python 고정 범위 | 'Python 코드는 포팅 기간 내내 고치지 않고' | 단계 1 `tools/doctor.py`, 단계 4 `tests/test_engine_*.py`·`src/bpcg/studio/godot.py` 를 고쳐야 함 | 고정 대상을 `src/bpcg` 의 생성 코드(studio 제외)로 한정하고 tools/·tests/·studio 의 도구 수정은 허용 | 예 |
| 7 이름 짓기 | 모듈 함수 → PascalCase 정적 메서드, dataclass → class·record | 2절 snake_case 함수, UPPER_SNAKE 상수, `is_`·`has_`, 한 글자 이름은 수식 기호만 | 2절에 아래 'C# 이름·범위' 를 더함 | 예(단계 1 core 가 본보기) |
| 8 500줄 | 모듈 하나 = C# 파일 하나 | 1절 '500줄을 넘으면 나누는 것을 검토'. 넘는 모듈 10개: globe 1,089, sample 1,044, pipeline 735, plates 691, scorecard 631, solver 588, drainage 576, model 556, corridor 551, detail 504 | 1:1 파일은 예외. 나눠야 하면 `partial class` 로 `<Mod>.<부분>.cs` 를 대응표에 적음. Numerics·IO 는 500줄 규칙 그대로 | 아니요(단계 1 검토) |
| 9 계산 함수와 파일 | .npy·.npz·TOML·PNG·glb 직접 구현(Bpcg.IO) | 1절 '읽기는 earth/, 쓰기는 bake/에서만'. 실제로는 core/config.py 가 TOML, bake/bundle.py 가 묶음을 읽음 | Bpcg.IO 는 바이트와 배열 사이의 변환만 하고 경로를 정하지 않음. 부르는 곳은 Core.Config·Bake·Pipeline·Cli 로 한정하고 구조 시험으로 막음. 문구도 현실에 맞춤 | 아니요(단계 1) |
| 10 바이너리·시험 자료 | 커밋 golden 은 1 MB 이하, gitignore 예외 | 8절 '각 1 MB 이하', 7절 '시험 자료는 tests/fixtures/ 에, 출처는 그 README' | `csharp/golden/data/` 를 두 번째 시험 자료 위치로, 출처는 그 README, 각 1 MB·합계 상한을 CI 가 검사. 끝에서 끝 기준 결과는 커밋하지 않고 CI 에서 다시 만듦 | 예(위치와 합계 상한) |
| 11 테스트 도구 | xUnit `csharp/Bpcg.Tests` | 7절 'pytest, tests/test_<모듈>.py', 표시 slow·data·godot | 7절에 C# 행: `<Pkg>/<Mod>Tests.cs`, `[Trait("Category", "Slow")]` 를 CI 에서 뺌, golden 이 없으면 건너뛰지 않고 실패 | 아니요(단계 1) |
| 12 커밋 범위 | '종류(범위): 한국어 요약' | 10절 '범위는 패키지 이름(core, hydro, engine 등)' | 아래 'C# 이름·범위' | 예(첫 커밋 전) |
| 13 PR 크기·합치기 | feat/csharp-port 한 브랜치, push·PR 은 확인 뒤 | 10절 'PR 은 바뀐 줄 400줄 안팎', squash, 리뷰 하루 | 단계·묶음 단위 PR 로 main 에 합치고 합친 뒤 브랜치를 main 에서 다시 시작. 1:1 포팅 PR 은 400줄 예외(대조 시험이 리뷰 근거) | 예 |
| 14 패키지 관리 | NuGet 은 MIT·BSD·Apache-2.0 만, 이유·라이선스를 진행 기록에 | 11절 'uv 만', `uv.lock` 커밋, `uv add --group`, PR 에 이유 | 11절에 NuGet 행: 중앙 버전 관리 + `packages.lock.json` 커밋 + CI `restore --locked-mode`, SDK 는 global.json, 전역 `dotnet tool` 금지, GPL 코드는 C# 으로도 옮기지 않음 | 아니요(첫 패키지 전) |
| 15 문서 언어·위치 | 주석·문서·커밋 언어는 conventions 를 따름 | 한눈에 보기 '주석·docstring 한국어 합니다체, README 첫 문단만 영어', 13절 '문서는 docs/' | `///` 는 한국어 합니다체(`<`·`&` 는 `&lt;`·`&amp;`, 문서 파일 생성 + 경고=오류라 CS1570 이 빌드를 멈춤), `csharp/README.md` 는 engine/README 처럼 첫 문단 영어 | 아니요 |
| 16 HOME 임시 폴더 | 단계 4 Godot .NET | 12절 '스크립트로 실행할 때 HOME 을 임시 폴더로' | Godot 은 HOME 으로 SDK 를 찾지 않지만 `dotnet build` 는 `~/.nuget/packages` 를 씀 → C# 빌드는 실제 HOME 으로 먼저, Godot 실행만 임시 HOME | 아니요(단계 4) |
| 17 설정·경로 | C# 도 configs/ 의 같은 TOML, 숫자를 박지 않음. 단계 4 내보낸 게임 | 6절 'configs/ TOML', 한눈에 보기 '경로는 bpcg.core.paths 로만' | configs/ 를 하나뿐인 원본으로 두고 Bpcg.dll 에 EmbeddedResource 로 넣음(CLI 는 저장소 파일 우선). `Bpcg.Core.Paths` 가 `BPCG_OUT`·`BPCG_DATA`·새 `BPCG_GOLDEN` 과 저장소 맨 위 찾기를 맡음 | 아니요. 단 Config 읽기 API(스트림)는 단계 1에 정함 |
| 18 metrics 의존 | 묶음 의존은 1절을 따름 | 1절 'src 안에서 metrics 를 import 하는 곳은 없어야'. 실제로는 pipeline.py 171행이 함수 안에서 metrics.scorecard 를, hero.flat·refine 이 pipeline 을, pipeline.py 719행이 hero 를 부름 | 1절에 'pipeline 만 metrics 를 부르고, pipeline 과 hero 는 한 단위' 라고 적고 C# 도 같게 | 아니요(단계 1 문서) |

#### 붙여 넣을 설정 (단계 1)

`.gitignore` — golden 예외는 47행 바로 아래(부정 무늬는 `*.npz` 뒤에 와야 함), C# 절은 '엔진 (Godot 4)' 절 앞. 실험에서 `csharp/golden/data/**` 의 npz·npy 만 다시 올라가고 `csharp/golden/big.npz`·`bin/a.dll` 은 빠졌습니다(`git add -A --dry-run`). Godot .NET 의 bin·obj 는 `engine/.godot/mono/temp/` 라 이미 `engine/.godot/` 로 빠집니다.

```gitignore
!csharp/golden/data/**/*.npz
!csharp/golden/data/**/*.npy

# --- C# (csharp/) ---
csharp/**/bin/
csharp/**/obj/
TestResults/
*.user
*.binlog
.vs/
```

`.gitattributes` (끝에 더함):

```gitattributes
*.cs text eol=lf diff=csharp
*.csproj text eol=lf
*.props text eol=lf
*.targets text eol=lf
*.slnx text eol=lf
*.sln text eol=lf
```

`.editorconfig` (`[*.ps1]` 절 뒤). 옵션의 `:warning` 은 net9 이상을 대상으로 하면 빌드에서도 지켜지고(roslyn#52991), 옵션이 없는 규칙만 ID 로 적습니다. 지역 변수·매개변수에는 이름 규칙을 두지 않습니다(Python 이름과 수식 기호를 그대로 쓰기 위함).

```ini
# --- C# (csharp/, 단계 4부터 engine/ 의 .cs). dotnet format 이 읽습니다 ---
[*.cs]
# [*] 과 같은 값이지만 이 절만 봐도 되게 다시 적습니다 (dotnet format 의 CHARSET·ENDOFLINE 이 고침)
indent_style = space
indent_size = 4
end_of_line = lf
charset = utf-8
insert_final_newline = true
trim_trailing_whitespace = true
max_line_length = 100
# using 은 System 먼저·namespace 밖, namespace 는 파일 범위이고 폴더와 같게
dotnet_sort_system_directives_first = true
dotnet_separate_import_directive_groups = false
csharp_using_directive_placement = outside_namespace:warning
csharp_style_namespace_declarations = file_scoped:warning
dotnet_style_namespace_match_folder = true:warning
# var: float·double 이 늘 보이게 형식을 적습니다. new·캐스트처럼 형식이 드러날 때만 var
csharp_style_var_for_built_in_types = false:warning
csharp_style_var_when_type_is_apparent = true:silent
csharp_style_var_elsewhere = false:warning
csharp_prefer_braces = true:warning
dotnet_style_require_accessibility_modifiers = for_non_interface_members:warning
# 식 모양을 바꾸는 제안은 강제하지 않습니다 (계산 순서를 Python 과 같게 둠)
dotnet_style_parentheses_in_arithmetic_binary_operators = always_for_clarity:silent
dotnet_style_prefer_compound_assignment = true:silent
# 이름: 형식·멤버·상수 PascalCase, 인터페이스 I 접두, private 필드 _camelCase.
# 더 좁은 규칙(private, private static)이 먼저 걸립니다(순서는 자동)
dotnet_naming_style.pascal.capitalization = pascal_case
dotnet_naming_style.i_pascal.capitalization = pascal_case
dotnet_naming_style.i_pascal.required_prefix = I
dotnet_naming_style.under_camel.capitalization = camel_case
dotnet_naming_style.under_camel.required_prefix = _
dotnet_naming_symbols.interfaces.applicable_kinds = interface
dotnet_naming_symbols.interfaces.applicable_accessibilities = *
dotnet_naming_symbols.types_members.applicable_kinds = class, struct, enum, delegate, field, method, property, event, local_function
dotnet_naming_symbols.types_members.applicable_accessibilities = *
dotnet_naming_symbols.private_static_fields.applicable_kinds = field
dotnet_naming_symbols.private_static_fields.applicable_accessibilities = private, private_protected
dotnet_naming_symbols.private_static_fields.required_modifiers = static
dotnet_naming_symbols.private_fields.applicable_kinds = field
dotnet_naming_symbols.private_fields.applicable_accessibilities = private, private_protected
dotnet_naming_rule.interfaces.symbols = interfaces
dotnet_naming_rule.interfaces.style = i_pascal
dotnet_naming_rule.interfaces.severity = warning
dotnet_naming_rule.types_members.symbols = types_members
dotnet_naming_rule.types_members.style = pascal
dotnet_naming_rule.types_members.severity = warning
dotnet_naming_rule.private_static_fields.symbols = private_static_fields
dotnet_naming_rule.private_static_fields.style = pascal
dotnet_naming_rule.private_static_fields.severity = warning
dotnet_naming_rule.private_fields.symbols = private_fields
dotnet_naming_rule.private_fields.style = under_camel
dotnet_naming_rule.private_fields.severity = warning
# 옵션이 없는 규칙: 쓰지 않는 using, 서식, 이름, 문화권(InvariantCulture)
dotnet_diagnostic.IDE0005.severity = warning
dotnet_diagnostic.IDE0055.severity = warning
dotnet_diagnostic.IDE1006.severity = warning
dotnet_diagnostic.CA1305.severity = warning
dotnet_diagnostic.CA1310.severity = warning

[*.{csproj,props,targets,slnx,runsettings}]
indent_size = 2
```

`global.json`:

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false,
    "errorMessage": ".NET SDK 10.0 이 없습니다. ./scripts/setup.sh --dotnet (윈도우는 setup.ps1 -Dotnet) 안내를 보세요."
  }
}
```

`csharp/Directory.Build.props` 에는 `TargetFramework` net10.0, `Nullable` enable, `ImplicitUsings` disable, `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `GenerateDocumentationFile` + `NoWarn` CS1591, `RestorePackagesWithLockFile` 을 둡니다. `GenerateDocumentationFile` 은 IDE0005 를 빌드에서 켜는 조건이고, `ImplicitUsings` 를 끄면 파일마다 `using System.IO;` 가 보여 IO 규칙(충돌 9)을 구조 시험으로 확인하기 쉽습니다. lock 파일과 `latestFeature` 를 함께 쓰므로 `PublishTrimmed`·`IsAotCompatible` 처럼 SDK 가 패키지를 넣는 속성은 켜지 않습니다(SDK 판이 바뀌면 NU1004, dotnet/sdk#48795).

#### CI

단계 1에 더할 작업입니다. golden 은 그 운영체제의 numpy 로 다시 만들어 같은 컴퓨터의 Python 과 비교합니다(libm 차이는 운영체제마다 다르므로 3 OS 모두).

```yaml
  dotnet:
    name: C# (${{ matrix.os }})
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, windows-latest, macos-15]
    runs-on: ${{ matrix.os }}
    timeout-minutes: 30
    env:
      DOTNET_NOLOGO: "1"
      DOTNET_CLI_TELEMETRY_OPTOUT: "1"
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-dotnet@v6   # DOTNET_ROOT 를 넣어 줌. 맥은 ~/.dotnet 에 설치
        with:
          global-json-file: global.json
          cache: true
          cache-dependency-path: csharp/**/packages.lock.json
      - uses: astral-sh/setup-uv@c18668ad3cf93ea998bef934396af7bb5c839dc7 # v10.2.0
        with:
          version: ${{ env.UV_VERSION }}
      - run: uv sync --locked
      - run: uv run python csharp/golden/export_golden.py
      - run: dotnet restore csharp/Bpcg.slnx --locked-mode
      - run: dotnet build csharp/Bpcg.slnx -c Release --no-restore
      - run: dotnet test csharp/Bpcg.slnx -c Release --no-build --filter "Category!=Slow"
```

`lint` 작업 끝(Ubuntu 한 곳이면 충분):

```yaml
      - uses: actions/setup-dotnet@v6
        with:
          global-json-file: global.json
      - run: dotnet format csharp/Bpcg.slnx --verify-no-changes
      - name: 커밋한 golden 자료는 각 1 MB 이하
        run: |
          big="$(find csharp/golden/data -type f -size +1000000c 2>/dev/null || true)"
          test -z "$big" || { echo "::error::1 MB 를 넘는 golden 파일: $big"; exit 1; }
```

단계 4의 `engine` 작업은 `chickensoft-games/setup-godot@v2` 앞에 같은 setup-dotnet 단계를 두고 `use-dotnet: true` 로 바꿉니다(이 액션은 .NET 판 zip 만 받고 SDK 는 설치하지 않으며, `GODOT`·`GODOT4` 를 실행 파일과 `GodotSharp` 를 나란히 둔 링크로 줌). 버전 확인 무늬는 `"$GODOT_VERSION".stable.mono*` 로, 시험은 `tests/test_engine_smoke.py tests/test_engine_globe.py` 두 파일로 바꿉니다. 필수 상태 검사(CONTRIBUTING 끝 절)에 `dotnet` 세 개를 더하는 일은 관리자가 합니다.

#### BakedPaths (단계 4)

순서는 명령줄(시험) → 이번 실행에서 C# 이 구운 폴더 → 내보낸 게임의 지난 `user://baked` → `res://baked`(Python `bake --engine`·스튜디오) 입니다. 편집기에서는 `res://baked` 를 그대로 읽으므로 스튜디오가 복사한 실행이 지난 `user://baked` 에 가려지지 않습니다(스튜디오의 `shutil.copy2` 는 원래 수정 시각을 남겨서 시각 비교는 쓰지 않음).

```gdscript
const DEFAULT_DIR := "res://baked"   # 파이썬 굽기(bpcg bake --engine)·스튜디오가 쓰는 곳
const RUNTIME_DIR := "user://baked"  # 엔진 안 C# 생성이 쓰는 곳 (내보낸 게임은 res:// 에 못 씀)
## C# 노드가 굽기를 마치면 이 실행 중 설정에 폴더를 넣습니다 (저장하지 않음).
const RUNTIME_SETTING := "bpcg/runtime_baked_dir"
const ARG_PREFIX := "--baked-dir="


static func dir() -> String:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with(ARG_PREFIX):
			return arg.trim_prefix(ARG_PREFIX).trim_suffix("/")
	var runtime := str(ProjectSettings.get_setting(RUNTIME_SETTING, ""))
	if not runtime.is_empty():
		return runtime
	if OS.has_feature("template") and FileAccess.file_exists(RUNTIME_DIR.path_join("manifest.json")):
		return RUNTIME_DIR
	return DEFAULT_DIR
```

`resolve()` 는 그대로입니다. 읽는 쪽(terrain.gd 10·42행, baked_layers.gd 42·112행, globe_data.gd 92행)은 모두 `BakedPaths` 를 거치고, `caves.glb` 는 `res://` 가 아닌 경로를 이미 실행 중 glTF 로 읽습니다(baked_layers.gd 485행). C# 노드는 `ProjectSettings.GlobalizePath("user://baked")` 의 운영체제 경로로 임시 폴더에 쓴 뒤 `manifest.json`·`globe.json` 을 맨 끝에 두고(Python 의 bake_corridor·copy_globe 와 같은 순서), 주 스레드에서 `ProjectSettings.SetSetting(...)` 후 장면을 다시 엽니다.

#### Godot 4.7.2 .NET 판 받기 (단계 4)

| 운영체제 | zip | 크기(표준판) | zip 안 실행 파일 |
|---|---|---|---|
| macOS | `Godot_v4.7.2-stable_mono_macos.universal.zip` | 200.8 MB (170.6) | `Godot_mono.app/Contents/MacOS/Godot` |
| Linux x86_64 | `Godot_v4.7.2-stable_mono_linux_x86_64.zip` | 107.7 MB (77.9) | `Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64` + `GodotSharp/` |
| Linux arm64 | `Godot_v4.7.2-stable_mono_linux_arm64.zip` | 106.9 MB | 같은 구조, `…_mono_linux.arm64` |
| Windows x64 | `Godot_v4.7.2-stable_mono_win64.zip` | 116.6 MB (86.0) | `Godot_v4.7.2-stable_mono_win64/…_mono_win64.exe`, `…_console.exe` + `GodotSharp/` |
| Windows arm64 | `Godot_v4.7.2-stable_mono_windows_arm64.zip` | 114.9 MB | 같은 구조, `…_mono_windows_arm64(_console).exe` |

Godot 은 `GodotSharp/` 를 실행 파일 폴더(macOS 는 `Contents/Resources`)에서 찾으므로(godotsharp_dirs.cpp), 펼칠 때 둘을 함께 `.tools/godot/` 로 옮기고 Linux 실행 파일만 `godot` 으로 이름을 바꿉니다. 버전 문자열은 `4.7.2.stable.mono.official.<hash>` 꼴입니다(version.h, 표준판은 이 맥에서 `4.7.2.stable.official.ed1daf0bf`). SHA-512 (SHA512-SUMS.txt, 표준판 값은 지금 스크립트와 일치 확인):

```text
0862c53d7158c7a67f745e2e46f90b68cf5343cbe8b95d6d4333c469e42ca104af9c121d1746a50e5d221a99d09d82ef7016495f8e0d09255842884ed0502795  Godot_v4.7.2-stable_mono_macos.universal.zip
1855960b27ee3ef5e66e5e228cced69d55637b24334a7411162687dcd077d8f9f645348cdb8eae984bec8135d49ed855a1e3a16476786b8bce60774fd8402d13  Godot_v4.7.2-stable_mono_linux_x86_64.zip
4b8b700ea21bea16b1a2ed8bc3a266431604f0d6452c819e022bfadea33eb431269bd680b11cd9dab93fc1f487ae4a60d15ee511e5691a82c22a2753b428d263  Godot_v4.7.2-stable_mono_linux_arm64.zip
79229fd112b0c9cbeab82363a4ef7be18ea70f1caf86bf912789335b136fbe7e01db0053a33461438c5da1c680c17bbb10040bd09bedc51221cd4423d0367757  Godot_v4.7.2-stable_mono_win64.zip
b458d0d9fd1b1f36081980dd07e402d71e667f042ab93c337d074ff03fbf3b2eccb3016f964155b7010051f67b928caefb9378fdaac00559e57167b9cf0466d1  Godot_v4.7.2-stable_mono_windows_arm64.zip
```

#### engine/ 이 .NET 프로젝트가 되면 깨지는 시험

- `tests/test_engine_smoke.py`: `_candidates()`(38–52행)에 .NET 판 경로가 없어 로컬에서는 `.tools/godot/Godot.app`(표준판)을 고르고, `version.startswith("4.7.2")`(99행)도 표준판을 통과시킵니다. 표준판은 장면에 붙은 C# 스크립트를 읽지 못하므로 'SCRIPT ERROR 없음' 단언이 깨질 것으로 봅니다(Godot 로 돌려 보지는 못함). C# 어셈블리를 빌드하는 단계도 없습니다.
- `tests/test_engine_globe.py`: 위 `godot_env` 를 가져다 써서 같은 문제이고, CI 는 지금 이 파일을 돌리지 않습니다.
- 쓰레기 파일 검사(`_engine_files`, 125–133·151–164행): Godot.NET.Sdk 가 bin·obj 를 `engine/.godot/mono/temp/` 로 보내므로(Sdk.props) 통과할 것으로 봅니다. 새 `.cs` 마다 `.cs.uid` 가 생기며 커밋합니다.
- `tests/test_studio.py`: 가짜 후보만 써서 통과하지만, 실제 'Godot로 보기'(studio/godot.py 52–67·353–357행)는 표준판을 찾고 빌드 없이 `--path engine` 으로 띄웁니다.
- `engine/tests/smoke.gd`: `.gd` 만 읽어 C# 오류를 잡지 못합니다.
- `tools/doctor.py`, `scripts/setup.*`: 표준판을 [ OK ] 로 보고, .NET 판 zip 은 위 구조 때문에 설치가 멈춥니다. `tests/test_cli.py` 는 영향이 없습니다(Python CLI).

#### C# 이름·범위 (conventions 1·2·10절에 더할 문장)

1절 트리에는 아래 줄을 더하고, `src/bpcg/` 아래에 지금 빠져 있는 `hero/`(히어로 유역)와 `studio/`(스튜디오)도 적습니다.

```text
├── csharp/              C# 포팅 (docs/csharp_port.md). src/bpcg 와 같은 계산, Godot 에 기대지 않음
│   ├── Bpcg/            계산 라이브러리. <Pkg>/<Mod>.cs = src/bpcg/<pkg>/<mod>.py, Numerics/, IO/
│   ├── Bpcg.Cli/        콘솔 프로그램 (planet, hero, bake, all)
│   ├── Bpcg.Tests/      xUnit 대조 시험. <Pkg>/<Mod>Tests.cs
│   └── golden/          export_golden.py (out/golden/ 에 씀), data/ (커밋하는 golden, 각 1 MB 이하)
```

- `src/bpcg/<pkg>/<mod>.py` → `csharp/Bpcg/<Pkg>/<Mod>.cs`, `namespace Bpcg.<Pkg>;`, `public static class <Mod>`. 함수 `topo_order` → `TopoOrder`, 비공개 `_helper` → `private static Helper`, 상수 `FACE_U` → `FaceU`, 참거짓 멤버 `IsOcean`.
- 모듈과 같은 이름의 클래스는 둘뿐입니다(`core/config.py` 의 `Config`, `bake/bundle.py` 의 `Bundle`, AST 로 확인). C# 은 한 namespace 에 같은 이름의 형식을 두 개 둘 수 없으므로, 따로 정적 클래스를 두지 않고 그 클래스에 모듈 함수를 `public static` 으로 둡니다(`Bpcg.Core.Config.LoadConfig(...)`).
- 지역 변수·매개변수는 Python 이름을 그대로 씁니다(`n_cells`, `Q`, `k_s`). numba 커널과 C# 반복문을 줄 단위로 맞대어 보기 위함입니다. 파일·manifest 에 나타나는 문자열은 Python 철자를 지킵니다.
- Python `assert`(4곳)는 늘 도는 검사(`InvalidOperationException`)로, `ValueError`(353곳)는 같은 한국어 메시지의 `ArgumentException` 으로 옮깁니다. 숫자·문자열 바꿈은 `CultureInfo.InvariantCulture` 로 하고, 시험은 de-DE 문화권에서도 돌립니다.
- 커밋 범위: 묶음 포팅은 `cs-<묶음>`(`feat(cs-hydro): routing 포팅, 대조 시험 통과`), 공통 코드는 `cs-numerics`·`cs-io`·`cs-cli`·`cs-tests`, golden 스크립트는 `golden`, 뼈대·CI·진행 기록은 `csharp`, 단계 4의 엔진 작업은 `engine`.

## 10. 결정한 사항과 이유

| 번호 | 날짜 | 결정 | 이유 | 상태 |
|---|---|---|---|---|
| D1 | 2026-10-02 | 단계 0에서 멈추고 코드는 쓰지 않았습니다 | .NET SDK가 없을 때의 지시 | 확정 |
| D2 | 2026-10-02 | `volume/slices.py`의 `_save_png`(matplotlib으로 축·눈금을 붙인 PNG)는 옮기지 않고, `vertical_slice`의 RGB 배열 계산만 옮깁니다. C# 서명에는 `save_path`를 두지 않습니다 | planet·hero·bake·all 경로에서 부르지 않고(`tests/test_volume.py`, `analysis/figures/render_results.py`만 부름), matplotlib 글꼴·안티에일리어싱을 픽셀 단위로 재현할 수 없으며, 확인용 그림은 Python이 계속 그립니다 | 확정 |
| D3 | 2026-10-02 | 저장소 맨 위 `CLAUDE.md`가 없어 새로 만들고 지시가 요구한 한 줄만 넣었습니다 | 지시 | 확정 |
| D4 | 2026-10-02 | 단계 0의 독립 비평을 빼고 '항목별 분석 + 결과 조립'으로 마무리했습니다(core ①, hydro, planet ①, geology, subsurface만 비평을 거침) | 사용자 결정. 50개 에이전트 규모로 2.5~3시간이 걸려 줄임 | 확정 |
| D5 | 2026-10-02 | 단계 0 검토를 건너뛰고 SDK가 준비되면 단계 1을 바로 시작합니다. 단계 1(core)을 마친 뒤의 검토는 지시대로 받습니다 | 사용자 결정. 포팅 자체가 주 목표 | 확정 |
| D6 | 2026-10-02 | .NET SDK는 사용자가 `brew install --cask dotnet-sdk`로 시스템에 설치합니다 | 관리자 암호가 필요하고, 단계 4의 Godot .NET이 기본 위치의 SDK를 찾음(1장) | 확정 |
| D7 | 2026-10-02 | 'numpy·numba 공통 규칙'과 'numpy 선형대수·FFT·polyfit' 분석은 결과 없이 멈췄습니다. 단계 1·2에서 채웁니다 | 12:17 이후 실행 중 에이전트가 '사용자 중단'으로 여러 번 끊겨 처음부터 다시 돌기를 되풀이함 | 확정 |
| D8 | 2026-10-02 | 커밋하는 golden은 `csharp/golden/data/`에 두고 `.gitignore`에 예외를 더했습니다 | 지시(1 MB 이하, 예외 규칙) | 확정 |
| D9 | 2026-10-02 | TOML 읽기는 Tomlyn 대신 Python 3.13 `tomllib`(MIT, 약 800줄)을 `Bpcg.IO.Toml` 로 옮겼습니다. 그래서 NuGet 은 시험용 xunit.v3·xunit.runner.visualstudio·Microsoft.NET.Test.Sdk 만 씁니다(P3 를 바꿈) | 6장 core ① 이 찾은 Tomlyn 의 차이(2⁶³ 이상 정수를 조용히 음수로 감음, TOML 1.1 문법을 받음, BOM 을 조용히 뗌) 없이 `--set` 값까지 Python 과 같은 규칙(BareText 판정 포함)으로 읽기 위해. Python 과 다른 점은 int64 밖 정수를 오류로 두는 것 하나(의도한 차이) | 확정 |
| D10 | 2026-10-02 | numpy 합의 결합 순서를 `0.0 + pairwise` (블록 128, 누적기 8개)로 확정하고 `Bpcg.Numerics.NpReduce` 로 옮겼습니다 | 5장의 실험에서 비트 단위로 같음 | 확정 |
| D11 | 2026-10-02 | 시험 실행기는 Microsoft.Testing.Platform 입니다(`global.json` 의 `test.runner`). NuGet 은 `xunit.v3` 4.0.1(Apache-2.0) 하나만 씁니다(xunit.runner.visualstudio·Microsoft.NET.Test.Sdk 는 뺌, P3 를 바꿈) | .NET 10 SDK 에서 xunit.v3(MTP 2.4)는 VSTest 경로로 `dotnet test` 를 돌릴 수 없음(빌드 오류). 실행은 `dotnet test --solution csharp/Bpcg.slnx` | 확정 |
| D12 | 2026-10-02 | golden 스크립트는 Python 정수 리스트를 배열로 만들 때 dtype 을 늘 적습니다 | `hash3` golden 이 float64 를 거쳐 끝자리가 잘린 것을 C# 대조가 잡음(2⁶³ 미만·이상이 섞인 정수 리스트를 numpy 가 float64 로 만듦). 고친 뒤 통과 | 확정 |
| D13 | 2026-10-03 | core 코드 검토를 따로 받지 않고 단계 2 를 순서대로 진행합니다. 0장 쟁점의 기본값(이름 규칙, 원소형, 예외 형 등)을 그대로 씁니다 | 사용자 결정('순서대로 계속 진행해') | 확정 |
| P1 | 2026-10-02 | 네임스페이스 `Bpcg.Numerics`(numpy·scipy 대체), `Bpcg.IO`(파일 형식) | Python 모듈과 1:1이 아닌 코드를 한곳에 모음 | 제안 (단계 1 기본값) |
| P2 | 2026-10-02 | FIELDS의 bool·int8은 `bool[]`·`sbyte[]` | npy `\|b1`·`\|i1`와 1:1 | 제안 (단계 1 기본값) |
| P3 | 2026-10-02 | NuGet은 시험용 xunit.v3(Apache-2.0)·xunit.runner.visualstudio(Apache-2.0)·Microsoft.NET.Test.Sdk(MIT)만 씁니다(Tomlyn 은 D9 로 뺌) | 1장. 나머지(TOML, CRC32, CLI 인자, zip, SHA-256)는 직접 구현하거나 .NET 에 들어 있음 | 제안 (단계 1 기본값) |
| P4 | 2026-10-02 | 'CI의 use-dotnet 설정'은 GitHub Actions의 `actions/setup-dotnet`(build·test 작업)과 `chickensoft-games/setup-godot`의 `use-dotnet: true`(엔진 작업)로 읽습니다 | 지금 CI가 GitHub Actions이고 setup-godot에 `use-dotnet` 입력이 있음 | 제안 |
| P5 | 2026-10-02 | C# 콘솔의 진행 로그 줄을 Python `cli`와 똑같이 냅니다 | 스튜디오가 그 줄로 진행률을 계산하므로 계산 엔진을 바꿔 끼울 수 있음(2장) | 제안 |

## 11. Python 쪽 의심 버그

포팅은 Python 동작을 그대로 재현하고 이 버그들을 고치지 않습니다. 비평을 거친 묶음(core ①, hydro, planet ①, geology, subsurface)은 비평이 아니라고 본 것을 뺐습니다. 근거와 재현 방법은 6장의 해당 절 '(e) 의심 버그'에 있습니다.

| 번호 | 분석 항목 | 위치 | 내용 | 영향 |
|---|---|---|---|---|
| 1 | core-a | src/bpcg/core/config.py:182-183 (+ src/bpcg/cli.py:316, 339 --seed type=int) | 정수 자리 덮어쓰기에 int64 범위 검사가 없습니다. 그래서 범위 밖 값이 checked_overrides를 통과한 뒤 파이프라인 안에서 실패합니다. | 정상 입력에는 영향이 없습니다. 잘못된 입력이 검사를 통과한 뒤 불분명한 오류로 멈춥니다. C#은 Coerce에서 거부할 예정이며, 이 차이를 의도한 차이로 기록합니다. |
| 2 | core-a | configs/learned/README.md ↔ src/bpcg/core/config.py:115 | learned 파일의 이름과 키 규칙이 문서와 코드에서 다릅니다. | README대로 파일을 만들면 값이 조용히 무시되거나 새 최상위 키로 붙어 실제 설정을 덮어쓰지 못합니다. 지금은 learned 파일이 없어 영향이 없습니다. C#은 코드 동작을 그대로 옮깁니다. |
| 3 | core-a | tests/test_studio.py:139-140 | '비슷한 키를 알려 줌' 시험이 실제로는 힌트를 검사하지 않습니다. | Python 쪽 힌트가 회귀해도 잡지 못합니다. C# 시험은 힌트 목록을 golden과 따로 비교해야 합니다. |
| 4 | core-a (비평) | src/bpcg/core/config.py:186 (+ src/bpcg/cli.py:61-64, 169-172) | 실수 자리 덮어쓰기에서 float(new)는 \|int\| ≥ 2^1024이면 OverflowError를 냅니다. 리스트 원소도 같습니다. checked_overrides가 약속한 ValueError가 아니므로, CLI의 except ValueError가 잡지 못합니다. | 잘못된 입력이 '--set:' 오류 대신 traceback으로 끝납니다. 결과 파일에는 영향이 없습니다. C#은 그런 정수를 TOML 층에서 오류(BareText)로 두어 '읽지 못했습니다'로 멈추며, 이 차이를 의도한 차이로 적습니다. |
| 5 | core-a (비평) | src/bpcg/core/config.py:79-82 ↔ src/bpcg/bake/bundle.py:77-109, 234, 288-293 | digest와 manifest config의 직렬화가 다릅니다. - digest: json.dumps(allow_nan=True)로 NaN·Infinity와 리스트 전체를 씀 - manifest config: jsonable이 NaN·inf를 null로, 1,024개 넘는 리스트를 {'__list__': n}으로 바꿈 그래서 manifest에서 되읽은 설정(hero --from, bake, globe)의 config_digest가 planet의 digest와 달라집니다. | 지금 설정 파일과 --set 경로(nan·inf 거부)에서는 생기지 않습니다. 설정 파일에 nan·inf를 적거나 with_overrides로 넣으면, test_cli가 확인하는 planet·hero·corridor·globe digest 일치가 깨집니다. C#은 두 표현을 그대로 재현해야 Python과 같은 digest를 냅니다. |
| 6 | core-a (비평) | src/bpcg/core/noise.py:3-4, 62-67 | '맥·윈도우·리눅스에서 같은 값' 주장은 \|좌표\| ≥ 2^63에서 성립하지 않을 수 있습니다. _check_points는 유한성만 봅니다. - arm64 numba: frintm·fcvtms로 격자 번호를 포화시킴 - x86 numba: cvttsd2si로 INT64_MIN을 낼 것 | 포팅 호출부의 좌표(단위 구, 미터를 파장으로 나눈 값)는 이 범위에 오지 않으므로 실제 영향은 없습니다. C#(.NET 9 이상의 포화 변환)은 arm64 결과와 같습니다. 문서 주장의 한계로만 진행 기록에 적습니다. |
| 7 | core-b | src/bpcg/core/resample.py:162-163 | (경미) add_ghost_layers 는 정수 dtype 도 받아 float64 로 바꾸는데, 오류 문구는 'faces 는 실수 배열이어야 합니다' 입니다. | 동작에는 영향이 없고 문구만 다릅니다. C# 은 int[] 오버로드로 같은 동작을 유지합니다. |
| 8 | core-b | configs/planets/earth.toml:88 (landscape.jitter = 1.0) → src/bpcg/core/graph.py:114-120 | (설계 확인) 흔들기 1.0 이면 흔든 대표점이 제 칸 밖으로 나가고 이웃 두 점이 거의 겹칠 수 있습니다. tests/test_neighbors.py 의 '흔든 대표점은 여전히 제 칸 안 (jitter < 1)' 불변식은 0.4 에서만 시험합니다. | 설계 의도일 수 있습니다(earth.toml 주석: 격자 정렬 지수 1.12~1.2). 다만 짧은 변에서는 경사 Δz/d 가 수십~수백 배 커질 수 있고, 다른 시드·해상도에서는 bake/globe 사상 검사가 허용치를 넘을 수 있습니다. 포팅은 그대로 따릅니다. |
| 9 | hydro | docs/pipeline.md:239 ↔ src/bpcg/hydro/network.py:62-70 | 문서 계약은 outlet_of가 int32를 돌려준다고 하지만, 코드는 int64를 돌려줍니다. | 출력에는 영향이 없습니다. C#은 코드를 따라 long[]을 쓰고, 문서 불일치는 진행 기록에 적습니다. |
| 10 | hydro | src/bpcg/hydro/network.py:93-96 | river_segments는 '순환이 있는지 확인하세요'라는 메시지로 순환을 잡는다고 안내합니다. 실제로는 강 칸 수만 비교하므로, 합류점이 낀 순환은 오류 없이 통과하고 순환을 담은 구간을 돌려줍니다. | 파이프라인에서는 같은 rcv로 topo_order가 먼저 성공하므로(순환이 있으면 ValueError) 영향이 없습니다. 앞으로 다른 호출자가 생길 때만 문제가 됩니다. |
| 11 | hydro | src/bpcg/hydro/network.py:14-21, 62-70 / accumulate.py:34-57 | outlet_of와 accumulate는 order가 같은 rcv의 위상 순서(순열)인지 검사하지 않습니다. 맞지 않으면 _outlet_kernel이 초기화되지 않은 np.empty 메모리를 조용히 돌려주고, accumulate는 틀린 합을 냅니다. | 지금 호출자는 모두 같은 rcv의 topo_order 결과를 넘기므로 영향이 없습니다. C#은 0으로 초기화하므로, 잘못된 입력에서는 Python과 값이 다릅니다. |
| 12 | hydro | src/bpcg/hydro/routing.py:45,64,178-180 | d8_receivers는 dist의 값을 검사하지 않습니다. dist가 0이면 docs/conventions.md 5절의 'ValueError' 규칙과 달리 numba ZeroDivisionError가 나고, NaN이면 경사 NaN인 수신 셀을 조용히 고릅니다. | CellGraph의 dist는 유한한 양수라 실제 실행에서는 영향이 없습니다. C#은 0 검사로 예외를 재현합니다. |
| 13 | hydro (비평) | src/bpcg/hydro/network.py:38-58, 93-99 (_segments_kernel, river_segments) | _segments_kernel은 order에 머리 칸이 중복되면 cells·offsets 밖에 씁니다(numba boundscheck 꺼짐). cells[:m]이 잘리므로 길이 검사도 통과하고, 빈 구간이 섞인 목록을 오류 없이 돌려줍니다. | 정상 경로(같은 rcv의 topo_order 순열)에서는 생기지 않습니다. 잘못된 입력에서는 Python 프로세스 메모리가 조용히 망가질 수 있습니다. C#은 IndexOutOfRangeException을 내므로 동작이 다르며, 이 입력은 golden에서 뺍니다. |
| 14 | hydro (비평) | src/bpcg/hydro/depressions.py:9-11, 76-78 (docstring) | 모듈 docstring은 두 채움 결과가 같은 높이의 처리 순서와 무관하다고 적고, _fill_kernel docstring은 결과가 순수 힙과 같다고 적었습니다. 그러나 fill_depressions 출력의 0 부호는 처리 순서와 pit FIFO에 달려 있어, 두 서술은 ±0에서 틀립니다. | 수치는 같아 생성 결과에는 영향이 거의 없습니다. 다만 이 서술을 믿고 C#에서 pit을 빼거나 힙 순서를 바꾸면 ±0 비트 대조가 실패할 수 있습니다. |
| 15 | planet-a | src/bpcg/planet/plates.py:430-441, 486-490 | 삼중 접합 오염입니다. _other_side_kernel은 다른 판 이웃 전부(plate[v] != k)로 대륙 비율과 상대 해양 나이를 냅니다. 그런데 _subduction_polarity는 그 값을 판 쌍 (other[c], plate[c])의 나이로 더하고, 해양-대륙/해양-해양 구분에도 씁니다. 세 판이 만나는 칸에서는 셋째 판의 지각 종류와 나이가 쌍 결정에 섞입니다. | 접합부 몇 칸의 섭입 방향(convergence_kind, subduction_side)이 바뀝니다. 그에 따라 해구 위치와 융기 띠가 국소적으로 달라집니다. 정도는 낮습니다. 포팅에서는 그대로 옮깁니다. |
| 16 | planet-a | docs/pipeline.md:80-125 | 명세와 코드가 다릅니다. 명세의 시그니처는 generate_plates(graph, cfg)인데 코드는 (graph, continental, cfg)입니다. 명세는 씨앗을 '해시 균등수 두 개'로 만든다고 하지만, 코드는 균등수 네 개로 정규분포 세 개를 만듭니다. 명세의 반확장 속도는 \|Δv·b̂\|/2인데, 코드는 경계를 따라 평활한 −0.5γ에 판별 하한(_age_consistent_rate_floor)을 적용합니다. 명세는 해구를 '해양 칸에' 더한다고 하지만, 코드는 지각 종류를 보지 않습니다. Airy z<0 쪽도 명세의… | 문서만의 문제입니다. 포팅은 코드를 따르고, 문서 정정은 진행 기록에 남깁니다. |
| 17 | planet-a | src/bpcg/planet/crust.py:125,127 | 대륙 자르기의 같음 처리 규칙이 분기마다 다릅니다. k == 0일 때는 `abs(csum[0]-target) < target`(같으면 덜 가져감)이고, 그 밖에는 `<=`(같으면 더 가져감)입니다. | 정확히 같을 때만 1칸이 달라지므로 사실상 영향이 없습니다. |
| 18 | planet-a | src/bpcg/planet/plates.py:561-585 | ensure_divergent_boundaries(max_rounds < 0)이면 루프가 돌지 않아 cls·failing이 정의되지 않고 UnboundLocalError가 납니다. 입력 검사가 없습니다. | 파이프라인은 늘 3을 쓰므로 영향은 없습니다. C#에서는 ArgumentOutOfRangeException으로 바꾸고 차이를 기록합니다. |
| 19 | planet-a (비평) | src/bpcg/core/distance.py _propagate_sources의 동률 규칙 → src/bpcg/planet/plates.py:642-662,… | nearest_source는 대칭 큐브스피어 격자에서 생기는 정확한 동률을 칸 번호로 풉니다. 그래서 그래프 pos가 1 ulp만 달라도(다른 OS의 np.tan 등) src가 바뀝니다. 그러면 convergence_kind·subduction_side와 γ·half_gap·z가 다른 칸의 값을 가져옵니다. conventions.md 6절의 '맥과 연구실 PC가 같은 행성' 보장이 Python 자체에서도 깨질 수 있습니다. | 다른 OS에서 몇 칸의 범주가 갈리고 값이 크게 뜁니다. C# 포팅은 core 그래프 비트 일치로 막고, Python은 고치지 않고 진행 기록에 적습니다. 해결(예: 동률을 격자 대칭과 무관한 규칙으로 풀기)은 포팅 뒤 Python과 C#을 함께 고칠 일입니다. |
| 20 | planet-a (비평) | docs/pipeline.md:78-127 (4.1 머리·5항, 4.2 대륙 마스크) | 분석이 적은 문서 불일치 외에 두 가지가 더 있습니다. 첫째, 4.2는 대륙 마스크의 판별 치우침을 '해시 ±0.3'의 계단으로 적지만, 코드는 판 씨앗 소프트맥스(β = CONTINENT_BIAS_SHARPNESS = 12)로 부드럽게 섞습니다. 둘째, 4.1 머리의 시그니처 generate_plates(graph, cfg)는 같은 절 5항('대륙 마스크를 먼저 만들어 generate_plates에 넘깁니다')과도 어긋납니다. | 문서만의 문제입니다. 포팅은 코드를 따르고, 문서 정정은 진행 기록에 남깁니다. |
| 21 | planet-b | src/bpcg/planet/ocean.py:51-82, src/bpcg/planet/materials.py:71-73 | 해수면을 정하는 부피 V(h)는 해수면 아래의 모든 칸을 세지만, 바다는 그중 가장 큰 연결 성분뿐입니다. 그래서 바다와 이어지지 않은 해수면 아래 분지가 담는 물만큼 실제 바다 부피가 water_volume_m3보다 작습니다. 모듈 docstring의 '바다 물 부피 보존'과 맞지 않지만, pipeline.md 4.3의 식과는 같습니다. | 그런 분지가 있으면 해수면이 조금 낮게, 바다 비율(planet.ocean_fraction 지표)이 작게 나옵니다. 포팅은 그대로 옮기고 기록만 합니다. |
| 22 | planet-b | src/bpcg/planet/climate.py:109 | 강수 노이즈 exp(η·ξ − η²/2)의 −η²/2는 ξ가 분산 1인 정규분포일 때 평균을 지키는 보정입니다. 그런데 fbm3의 ξ는 표준편차가 약 0.153이라, 면적 평균 배율이 1이 아니라 0.944입니다. | 강수가 띠 평균보다 체계적으로 약 5.6% 적게 나옵니다. 공식이 pipeline.md 4.5와 같으므로 명세 수준의 문제이고, 포팅은 그대로 옮깁니다. |
| 23 | planet-b | src/bpcg/planet/uplift.py:122-132 | 1e-9 꼬리 자르기를 노이즈를 곱하기 전에 하므로, 노이즈 배율(1 + 0.25·ξ, tiny에서 ξ는 약 ±0.56)을 곱한 뒤에는 0보다 크고 1e-9보다 작은 산맥 성분이 남을 수 있습니다. pipeline.md 4.4는 '산맥 성분이 1e-9보다 작으면 0'이라고 적고 있습니다. | 영향은 작습니다. 남는 값이 약 0.86e-9 이상이라 주석이 걱정한 1e-300 같은 값의 NaN 문제는 생기지 않습니다. 문서와 코드가 다른 점만 기록합니다. |
| 24 | planet-b | src/bpcg/planet/ocean.py:66-82 | 부피 해상도(면적 × ulp(h))가 tol(1e-9·W)보다 크면 이분법이 tol을 만족하지 못하고 선형 맞춤 값을 돌려줍니다. 이때 docstring이 약속한 상대 오차 1e-9를 지키지 못해도 경고나 오류가 없습니다. | 실제 설정(W = 1.335e18, 부피 해상도 약 33 m³ ≪ tol 1.3e9 m³)에서는 일어나지 않습니다. |
| 25 | geology | src/bpcg/geology/rocks.py:107-108, 148-150 (metamorphose) | T_peak = +inf이면 변성 단계가 3개보다 적은 암석(slate, limestone, sandstone, granite, schist)이 변성되지 않고 원래 암석으로 돌아갑니다. 없는 단계를 문턱 +inf와 결과 '자기 자신'으로 채운 뒤 t >= 문턱으로 덮어쓰기 때문이고, +inf >= +inf가 참입니다. | 지금 파이프라인은 온도가 늘 유한해 영향이 없습니다. 설정에 inf를 넣을 때만 드러납니다. C#은 같은 표로 그대로 재현합니다. |
| 26 | geology | src/bpcg/geology/model.py:302-305, 327, 330 (template_columns) | geotherm_c_per_m만 유한하고 양수인지 검사하고, surface_temperature_c는 검사하지 않습니다. TOML의 nan이나 inf가 오류 없이 들어갑니다. nan이면 Python 내장 max가 NaN을 삼켜 그럴듯한 기둥이 나옵니다. | 설정 실수가 조용히 틀린 지층이 됩니다. 기본 설정에서는 영향이 없습니다. |
| 27 | geology | src/bpcg/geology/model.py:118-119, 487-489 (_check_columns, LayerColumns.from_columns), 4… | 범위를 검사하기 전에 암석 번호를 uint8로 강제 변환(unsafe)합니다. 그래서 256은 0, 3.7은 3으로 바뀌어 from_columns 검사를 통과합니다. rock_at_points는 실수 cells를 int64로 버림합니다(NaN은 0). | 손상된 묶음이나 잘못된 입력을 거르지 못합니다. 파이프라인 경로에서는 도달하지 않습니다. C#은 형으로 막습니다. |
| 28 | geology | src/bpcg/geology/model.py:136 (_as_2d_z) | 칸 수 N = 0이고 z가 1차원이면 z.reshape(0, -1)이 ValueError를 냅니다. 같은 빈 입력이라도 z가 (0, M)이면 됩니다. | 빈 영역에서 rock_at, layer_index, surface_rock이 실패합니다. 지금은 도달하지 않습니다. |
| 29 | geology | src/bpcg/hero/refine.py:180-183 (+ src/bpcg/pipeline.py:632-645, src/bpcg/geology/model.p… | L0 지질은 세기 한계를 적용하기 전의 U와 그 U_max로 습곡 변위를 정합니다. 묶음에는 줄인 U'가 들어갑니다. 히어로는 U'와 L0의 U_max(diag)로 변위를 다시 구하므로, 같은 자리에서도 히어로 층 경계 고도가 L0와 다릅니다(히어로 습곡이 더 납작해짐). | L0와 히어로의 지층이 일관되지 않습니다. 의도인지 확인이 필요합니다(docstring은 'U_max만 같게'라고만 적음). 포팅은 그대로 따릅니다. |
| 30 | geology (비평) | src/bpcg/metrics/scorecard.py:113-117 (원인 src/bpcg/geology/model.py:136 _as_2d_z) | rock_consistency 점검은 표본 칸이 0개이면 원래 의도한 '표본 없음'(value None, pass None) 대신 '계산 실패'(pass False)로 기록됩니다. surface_rock_samples가 빈 cells로 layer_index를 부르면 1차원 z의 reshape(0, -1)이 ValueError를 내기 때문입니다. | 육지가 없거나 육지가 모두 not_steady·선상지인 드문 경우에만 scorecard.json의 불합격 목록이 거짓으로 늘어납니다. C#은 같은 메시지로 예외를 던져 그대로 재현합니다. |
| 31 | geology (비평) | src/bpcg/geology/model.py:213-215, 258 | 습곡 설정 검사에 빈틈이 있습니다. fold_wavelength_m = +inf는 `not lam > 0`을 통과해 습곡 위상이 칸과 상관없는 상수(0.5·fbm3(±0))가 됩니다. fold_amplitude_m는 검사하지 않아, NaN·inf는 build_columns에서야 잡히고 음수는 습곡을 조용히 뒤집습니다. | --set과 스튜디오는 checked_overrides가 nan·inf를 막으므로, λ = +inf는 TOML 파일을 직접 고칠 때만 들어갑니다. 음수 진폭은 --set으로도 들어갑니다. 기본 설정에서는 영향이 없고, C#은 검사를 더하지 않고 그대로 따릅니다. |
| 32 | landscape | src/bpcg/landscape/solver.py:560 | 멈춤 조건의 max_dz < stop_dz_m은 늘 참입니다. 따라서 landscape.stop_dz_m은 아무 효과가 없습니다. | 현재 출력에는 영향이 없습니다. 다만 문서와 설정은 '고도 변화 < 0.1 m'를 약속하는데, 실제로는 방향 변화가 0인지만으로 멈춥니다. stop_dz_m ≤ 0이면 0.0 < 0.0이 거짓이라 끝까지 수렴하지 않습니다. C#은 그대로 옮깁니다. |
| 33 | landscape | src/bpcg/landscape/warmstart.py:59,98 | factor를 검사하지 않습니다. factor=1이면 무한 재귀가 되고, factor=0이면 ZeroDivisionError가 납니다. | 파이프라인은 기본값 4를 쓰므로 영향이 없습니다. 같은 재귀를 C#으로 옮기면 잡을 수 없는 StackOverflow로 프로세스가 죽습니다. 그래서 오류 경로를 어떻게 처리할지 결정이 필요합니다. |
| 34 | landscape | src/bpcg/landscape/warmstart.py:26-30,112-118 | 한 변이 factor의 배수가 아니면, 마지막 블록은 최대 2·factor−1 줄을 평균합니다. 그런데 보간 격자는 그 블록의 중심을 (nyc−0.5)·dxc에 두므로 블록 실제 중심과 어긋납니다. | 시작 지형이 가장자리에서 조금 어긋날 뿐, 정상상태 법칙과 결과의 정당성에는 영향이 없습니다. 반복 수만 달라질 수 있습니다. 현재 프로필(tiny 64, laptop 1280→320→80, lab 1600→400→100)은 해당하지 않습니다. |
| 35 | landscape | docs/pipeline.md 7.2·7.3 대 src/bpcg/landscape/fans.py:90-105, src/bpcg/landscape/relief.p… | 명세와 코드가 다릅니다. add_fans는 명세의 (z_new, fan_mask, apexes)가 아니라 not_steady를 더한 4개를 돌려줍니다. 사면 기복의 L_h도 명세의 0.5·sqrt(A*)가 아니라 0.5·sqrt(min(A*, 칸 면적))입니다. | 코드는 docstring과 맞고 의도된 동작으로 보입니다. C#은 코드를 따르고, 명세를 고칠지는 사람이 정합니다. |
| 36 | subsurface | src/bpcg/subsurface/groundwater.py:179-181 | diag의 clipped_max_depth_fraction이 lo = z − max_depth로 잘린 칸을 다 세지 못합니다. depth = z − (z − max_depth)가 반올림 때문에 max_depth보다 작아질 수 있어서 depth >= max_depth 판정이 거짓이 됩니다. 같은 diag 안에서 shallow_fraction은 면적 가중이고 clipped_* 은 칸 수 비율이라 가중 방식도 서로 다릅니다. | 진단값(manifest meta.diag.stages.groundwater)만 낮게 나옵니다. 필드에는 영향이 없고, C#은 같은 산술로 같은 값을 냅니다. |
| 37 | subsurface | src/bpcg/subsurface/caves.py:65-77 (+ groundwater.py:68-70) | 물 칸은 z_gw = h_w이므로 동굴 0층이 수면 높이가 됩니다. 호수에서는 0층이 호수 바닥보다 위의 물속에 놓이는데, 지표 띠 판정에 걸려 층 값과 입구 비트가 켜집니다. 강 칸도 0층이 z − 0.2·D라 거의 다 입구가 됩니다. pipeline.md 14절의 '동굴 입구가 너무 많고', '강가에서 동굴이 지표를 뚫습니다'와 같은 원인으로 보입니다. | 입구가 지나치게 많아지고, volume에서 호숫가·강가에 통로가 뚫립니다. 고치지 않고 기록만 합니다. |
| 38 | subsurface | docs/pipeline.md 8.4 <-> src/bpcg/subsurface/caves.py:69-88 | 명세와 코드가 다릅니다. pipeline.md 8.4는 지표 띠 안이고 녹는 칸이면 입구 비트를 켠다고 씁니다. 코드는 덮개 칸(자신 또는 이웃)과 맞닿을 때만 켜고, 명세에 없는 벽 규칙(덮개 칸의 낮은 이웃)을 더합니다. 띠 안이지만 덮개와 닿지 않는 칸은 NaN이고 비트가 없습니다. | 포팅은 코드를 따릅니다. 명세 문구를 고칠지 결정이 필요합니다. |
| 39 | subsurface | docs/pipeline.md 8.1 <-> src/bpcg/subsurface/water.py:126-132 | 명세와 코드가 다릅니다. pipeline.md 8.1에는 수면 1차 훑기(상류부터 min)만 있습니다. 코드의 2차 훑기(하류부터 max: 호수·바다 어귀의 배수 효과로 강 수면을 받는 쪽 수면까지 올림)는 water.py 모듈 docstring에만 있습니다. | 포팅은 코드를 따릅니다. 명세만 보고 옮기면 어귀 수면이 달라집니다. |
| 40 | subsurface | src/bpcg/subsurface/soil.py:129 + src/bpcg/pipeline.py:529-536 (l0_config) | 의도 확인이 필요합니다. L0은 솔버 진동을 막으려고 landscape.deposition_g를 deposition_g_l0 = 0으로 바꿉니다. 흙 모듈도 같은 키를 읽으므로 퇴적 지배 판정이 0 > U가 되어, L0 충적층은 침강(U < 0) 칸에만 생깁니다. | L0 alluvium_m과 bare_rock이 사실상 퇴적 모델 없이 정해집니다. 히어로(L2)에는 영향이 없습니다. |
| 41 | subsurface (비평) | docs/pipeline.md 8.2 <-> src/bpcg/subsurface/soil.py:137 | 명세는 '경사가 bare_slope보다 가파르면 0이고 bare_rock'이라고만 적습니다. 그런데 코드는 흙과 충적층이 모두 0인 육지 칸을 맨 암반으로 두므로, P₀ ≤ E(깎임이 생산보다 빠름)인 완만한 칸도 맨 암반이 됩니다. 모듈 docstring은 코드와 같습니다. | 포팅은 코드를 따르므로 결과에는 영향이 없습니다. 다만 명세만 보고 옮기면 bare_rock이 크게 달라지므로, 문서를 고칠지 정해야 합니다. |
| 42 | subsurface (비평) | src/bpcg/subsurface/water.py:280-285 (+ water.py:100-101) | 검사 빈틈입니다. valley_depth는 강이 없으면 window_max에 가기 전에 돌아가므로 음수나 NaN인 window_m을 오류 없이 받습니다. water_bodies에 넘긴 caves.valley_window_m도 마찬가지입니다. routing_from_result는 order가 rcv와 맞는 위상 순서인지, 순열인지 검사하지 않습니다. | pipeline에서는 늘 올바른 값이 들어오므로 결과에 영향이 없습니다. C#은 검사를 건너뛰는 동작까지 그대로 옮기고, 진행 기록에만 적습니다. |
| 43 | metrics | src/bpcg/pipeline.py:668 (score(cfg, planet_fields=...)), src/bpcg/metrics/drainage.py:41… | 행성 법칙 자기일관성을 L0 솔버가 쓴 설정과 다른 G 로 다시 계산합니다. L0 솔버는 l0_config(cfg)(landscape.deposition_g = deposition_g_l0 = 0.0)로 풀지만, 점수표에는 cfg(deposition_g = 1.0)가 넘어갑니다. 그래서 law_slope_from_fields 가 E = U + 1.0·R_ref·Qs/Q 로 법칙 경사를 계산합니다. | 지표가 솔버 자기일관성 대신 G 항 차이를 잽니다. 지금은 합격선 1e-3 보다 훨씬 작아 판정에는 영향이 없지만, 확산 항 비중이 큰 설정에서는 거짓 불합격이나 실제 불일치를 가리는 일이 생길 수 있습니다. C# 포팅도 같은 값을 내도록 cfg 를 그대로 넘깁니다. |
| 44 | metrics | src/bpcg/metrics/scorecard.py:399 | 법칙 비교 마스크는 fan·not_steady 칸만 빼고, 선상지로 올라간 칸을 수신 셀로 가진 기여 셀과 호수 칸은 남깁니다. 이 칸들의 경사는 reroute_after_fans(pipeline.py:255)가 max(z - z_r, 0)/d 로 다시 계산해 법칙 경사보다 작아집니다. | 중앙값 판정은 그대로라 pass 는 바뀌지 않지만 note 의 최댓값이 오해를 부릅니다. 선상지가 많은 히어로에서는 중앙값까지 오를 수 있습니다. C# 은 같은 마스크를 그대로 옮깁니다. |
| 45 | pipeline-cli | src/bpcg/pipeline.py:666-673 | generate_planet 이 점수표를 cfg_l0 가 아닌 원래 cfg 로 부릅니다. L0 솔버는 deposition_g = deposition_g_l0(0.0)으로 풀었습니다. 그런데 metrics.drainage.law_slope_from_fields 는 cfg.landscape.deposition_g(1.0)로 E = U + G·R_ref·Qs/Q 를 다시 계산하므로, planet.law_consistency 가 솔버가 쓰지 않은 법칙과 비교합니다. | 보고값만 틀리고 합격선 1e-3 은 통과합니다. L0 에서는 G 항이 사면 항에만 들어가 차이가 작지만, 이 검사가 1e-6 수준의 실제 불일치를 가릴 수 있습니다. C# 은 고치지 않고 재현합니다. |
| 46 | pipeline-cli | src/bpcg/cli.py:258-266 (bake/globe.py:324-347) | bake --out X 로 회랑을 다른 곳에 구워도 지구본은 <run>/globe 에 씁니다. 히어로 자리는 hero_from_run(<run>) 으로 찾는데, 이 함수는 <run>/corridor/manifest.json 을 hero/manifest.json 보다 먼저 읽습니다. 그래서 지난 굽기의 회랑이 남아 있으면 그 회랑의 자리·seed·config_digest 를 씁니다. | 낮음. --out 을 쓰고 같은 실행 폴더에 옛 회랑이 남아 있을 때만, 지구본의 히어로 표시와 seed 비교가 틀릴 수 있습니다. |
| 47 | pipeline-cli | src/bpcg/cli.py:75-78 | load_config 는 --planet 으로 TOML 경로도 받지만, _out_dir 은 그 문자열을 그대로 OUT 아래 폴더 이름으로 씁니다. 절대 경로면 pathlib 결합이 OUT 을 버리므로 TOML 파일 경로 자체가 실행 폴더가 됩니다. | 낮음. --planet 에 경로를 주고 --out 을 생략할 때만 생깁니다. bake_config 도 planet.name 으로 설정을 다시 읽으므로, 경로로 준 설정을 찾지 못할 수 있습니다. |
| 48 | pipeline-cli | src/bpcg/cli.py:285-289 | cmd_all 은 run_hero 안에서 난 ValueError 를 모두 '히어로 자리를 찾지 못해' 로 보고 평면 히어로로 바꿉니다. 후보 없음뿐 아니라 솔버 내부 오류('수신 셀이 이웃이 아닌 칸이 … (내부 오류)'), topo_order 순환, NaN 검사, 묶음 읽기·쓰기 오류도 잡힙니다. | 중간(디버깅). 실제 버그가 평면 히어로 대체 뒤에 조용히 가려집니다. C# 도 같은 예외형을 써야 같은 흐름이 재현됩니다. |
| 49 | pipeline-cli | docs/pipeline.md:545 | 13절은 pipeline.py 의 함수로 bake(hero, cfg, out_dir, engine_dir) 를 적지만, 그런 함수는 없습니다. 실제 굽기는 cli.run_bake 가 bake.corridor.bake_corridor(hero_state, cfg, out_dir, engine_dir=None, log=print) 를 부르는 것입니다. | 낮음. 다만 C# Pipeline 에 Bake 래퍼를 둘지 결정해야 합니다(열린 질문). |
| 50 | pipeline-cli | src/bpcg/cli.py:258-269 | cmd_bake 는 apply_threads 를 부르지 않습니다. 그래서 bake 를 따로 돌리면 profile.compute.numba_threads 가 무시되고 numba 기본값(모든 코어)을 씁니다. planet·hero·all 은 적용합니다. | 성능과 자원 사용에만 영향이 있습니다. 결과는 스레드 수와 무관해야 합니다(행성·히어로는 비트 일치를 확인했고, bake 는 확인하지 않음). |
| 51 | hero | src/bpcg/hero/refine.py:83-88,180-182 (pipeline.py:632-661) | 히어로 습곡 변위 Δ = A·(U/U_max)·sin φ 에서 U 와 U_max 의 기준이 다릅니다. U 는 지각 세기 한계로 줄인 유효 융기입니다. 행성 필드 uplift_m_per_yr 가 pipeline.py:661 의 fields.update(st.fields) 로 덮이기 때문입니다. 반면 U_max 는 줄이기 전 융기로 만든 diag['geology']['u_max_m_per_yr'] 입니다(geology/model.py:539,552). L0 기둥은 줄이기 전 U 로 만든 변위를 썼으므로, 융기를 줄인 칸에서는 히어로… | 히어로 지표 암석과 동굴 층이 같은 자리 L0 의 예측과 달라집니다. finder 의 탄산염 점수는 L0 기둥 기준이라 히어로 지질을 대표하지 못할 수 있습니다. 충돌대처럼 융기를 많이 줄인 곳일수록 차이가 큽니다(이론상 최대 fold_amplitude 1500 m). 포팅 규칙에 따라 C# 은 그대로 재현하고 고치지 않습니다. |
| 52 | hero | src/bpcg/hero/domain.py:168-170 | edge_hit 의 유한성 검사가 Python max 의 비대칭 때문에 북 성분 NaN 을 놓칩니다. max(1, NaN) = 1 이라 검사를 통과하고, 북·남 분기로 가서 y = NaN 인 EdgeHit 을 돌려줍니다. | 실제 입력은 유한한 위치 차와 단위 벡터의 내적이라 도달하지 않으므로 영향은 없습니다. C# 은 Math.Max 대신 Python max 의미로 재현해야 같은 동작이 됩니다. |
| 53 | volume | src/bpcg/volume/sample.py:592-616, 803 (_build_rivers, _river_query) | 합류점에서 아래 구간 첫 점과 위 구간들에 덧붙인 끝점이 정확히 같은 좌표가 되어, 가장 가까운 강 점이 여러 개입니다. cKDTree는 그중 잎 안 indices 순서의 첫 점을 돌려줍니다. 이 순서는 C++ 표준이 정하지 않는 std::nth_element·std::partition의 결과입니다. _river_project_kernel은 고른 점의 앞뒤 선분만 보므로 ℓ, 반폭, 깊이, 둑 높이, 수면이 고른 점에 따라 달라집니다. | 합류점 주변 수 m 안의 d, d1, 재질, 물, 지표 높이(굽기 높이맵·물·동굴 입구)가 수학적으로 정해지지 않고, scipy 빌드(libc++ 대 libstdc++)에 따라 달라질 수 있습니다. 다른 플랫폼에서는 확인하지 않았습니다. 이는 conventions 6절의 '맥과 연구실 PC가 같은 행성' 목표와 어긋납니다. C#이 같은 값을 내려면 sci… |
| 54 | volume | src/bpcg/volume/sample.py:404-409 (catmull_rom_centripetal docstring), sample.py:54 (RIVE… | 문서에는 'step_m 이하 간격', '1 m 간격 점'이라고 적혀 있습니다. 실제로는 점 수를 현 길이로 정하고 knot 매개변수를 균등하게 나누므로 간격이 1 m를 넘습니다. | 문서와 동작이 어긋납니다(낮음). KD-tree의 가장 가까운 점과 실제 가장 가까운 선분 사이 오차가 문서에 적힌 것보다 조금 큽니다. |
| 55 | volume | src/bpcg/volume/sample.py:950-964 (evaluate_grid) | C = 0이면 반복이 한 번도 돌지 않아 빈 dict를 돌려주므로, 요청한 키가 없습니다. | 빈 회랑이나 빈 격자로 부르면 호출자(bake.corridor·mesh·detail, slices)가 KeyError를 냅니다. 지금 파이프라인에서는 C > 0이라 드러나지 않습니다(낮음). |
| 56 | volume | docs/pipeline.md:466 (10절 slices) | 문서에는 vertical_slice(volume, p0, p1, z_range, res)라고 적혀 있지만, 코드는 (volume, p0, p1, z_min, z_max, res_m, save_path=None)입니다. | 문서만 어긋납니다(매우 낮음). |
| 57 | bake-a | src/bpcg/bake/mesh.py:135-172 (docstring 12-14) | docstring 은 '높이 격자를 전역 voxel 배수에 맞춰 이웃 조각의 꼭짓점이 정확히 겹친다(merge_vertices 로 붙임)'고 하지만, 이웃 조각의 띠 바닥 k0 가 다르면 같은 꼭짓점이 정확히 겹치지 않습니다. skimage 가 꼭짓점을 float32 국소 격자 좌표(k−k0+t)로 계산하므로, k0 가 다르면 반올림이 달라집니다. | 큰 회랑에서 조각 이음매에 꼭짓점이 겹쳐 남고 법선이 갈라져 음영 이음매가 보일 수 있습니다(틈은 수 μm 라 보이지 않음). C# 은 같은 결과를 재현해야 합니다. |
| 58 | bake-a | src/bpcg/bake/mesh.py:196-199 | build_mesh docstring 은 법선을 '삼각형 감김에서 면적 가중으로' 구한다고 하지만, trimesh 5.1.0 의 vertex_normals 는 각도 가중(Thürrner–Wüthrich, arccos)입니다. | 문서와 실제 계산이 다릅니다. C# 은 실제 동작(각도 가중)을 옮겨야 합니다. |
| 59 | bake-a | src/bpcg/bake/mesh.py:172 | diag['tiles_with_caves'] 는 띠가 하나라도 있는 조각마다 늘어납니다. 모든 띠가 skip(f 부호 변화 없음)되거나 면이 0개인 조각도 셉니다. | manifest caves.tiles_with_caves 와 로그 '조각 n/m' 이 실제보다 클 수 있습니다(진단값만). |
| 60 | bake-a | src/bpcg/bake/bundle.py:111-114 | _write_text_atomic 은 Path.write_text(텍스트 모드)를 쓰므로 Windows 에서 줄 끝이 CRLF 가 됩니다. heightmap 의 .json 은 바이너리로 써서 늘 LF 입니다. | manifest.json·textures.json·strata.json·entrances.json 의 바이트가 OS 마다 다릅니다. 엔진과 JSON 파서에는 영향이 없지만 바이트 비교 golden 이 OS 에 따라 갈립니다. |
| 61 | bake-a | src/bpcg/bake/bundle.py:376-388; src/bpcg/bake/corridor.py:156 | jsonable 은 NaN 을 null 로 쓰는데, 다시 읽는 쪽이 이를 비대칭으로 처리합니다. load_hero_state 는 lat_deg·lon_deg 만 None 을 NaN 으로 되돌리고 score 는 float(None) 으로, choose_corridor 는 fan apex 의 discharge 를 float(None) 으로 읽어 TypeError 를 냅니다. | 점수나 선상지 유량이 NaN 인 히어로 묶음은 다시 읽기나 굽기에서 실패합니다. 정상 경로에서는 NaN 이 생기지 않아 실제 영향은 작습니다. |
| 62 | bake-a | src/bpcg/bake/corridor.py:119-123 | main_stem 의 상류 걷기에는 순환 검사가 없고 len(up)<=n 상한만 있습니다. 하류 걷기는 순환이면 ValueError 를 냅니다. | receiver 는 구성상 순환이 없어 실제 영향은 없습니다. 잘못된 입력에서 오류 대신 이상한 회랑이 나올 수 있습니다. |
| 63 | bake-b | src/bpcg/bake/globe.py:425-436 (_strict·_sequential) | _sequential 은 hi == lo 일 때만 hi = lo + 1 로 막습니다. 0 < hi - lo 가 abs(lo) 에 비해 아주 작으면(대략 5e-6 배 이하), _num 이 6자리로 줄인 색표 매듭이 같아져 _strict 가 ValueError 를 냅니다. 그러면 레이어 하나만 건너뛰지 않고 bake_globe 전체가 멈춥니다. | 강수(p1·p99)나 log10 유량(p1·p99.5)이 거의 고른 행성에서 지구본 굽기가 실패합니다(bpcg bake·all 이 지구본 단계에서 예외). 포팅에서는 같은 예외를 그대로 재현하고 진행 기록에 적습니다. |
| 64 | bake-b | src/bpcg/bake/detail.py:454 (add_fractal_detail) | top_octave_rms_per_std 로 나누는 곳에 가드가 없습니다. 패딩한 창이 max_wavelength_m/2 보다 짧으면(PAD_MAX_CELLS 가 512 로 자를 때) 첫 옥타브에 FFT 칸이 없어 값이 0 이 되고, Python 은 ZeroDivisionError 를 냅니다. bake_corridor 는 ValueError 만 잡으므로 굽기 전체가 멈춥니다. | 비현실적으로 큰 max_wavelength_m 에서만 생깁니다(기본 50 m). 낮음. C# 는 0 으로 나누면 inf 가 되므로, 같은 상황에서 ValueError 대응 형이 아닌 예외를 던지도록 맞춰야 합니다. |
| 65 | bake-b | src/bpcg/bake/globe_text.py:206, 210 (temperature_how), 190, 194 (uplift_how) | .0f 형식은 (-0.5, 0) 의 값을 '-0' 으로 씁니다. 1% 분위 기온이 0 °C 바로 아래(예: -0.4)면 설명에 '가장 진한 파랑 ≈ -0 °C 이하' 가 나옵니다. | 보기 문장의 흠뿐입니다. C# 도 같은 '-0' 을 내야 globe.json 이 같아집니다. |
| 66 | bake-b | src/bpcg/bake/detail.py:440 (add_fractal_detail) | 모양 검사가 wet 과 gx 만 확인하고 gy 는 확인하지 않습니다. gy 모양이 다르면 뒤의 evaluate_grid 에서 덜 분명한 오류가 납니다. | 낮음. 회랑 굽기는 meshgrid 로 같은 모양을 넘깁니다. 동작 보존을 위해 C# 도 같은 검사만 둡니다. |

## 12. 남은 일

### 단계 1: 뼈대와 core

- [x] .NET 10 SDK 설치 확인(`dotnet --info`: SDK 10.0.401, 런타임 10.0.12, osx-arm64)
- [ ] Godot 4.7.2 .NET 판의 net10.0 확인(1장 절차, Godot .NET 판을 받은 뒤. 단계 4 전에)
- [x] `dotnet build`·`dotnet test`(99개 통과)·`dotnet format --verify-no-changes` 통과, 대조 결과를 3장에 기록
- [x] `global.json`, `csharp/Directory.Build.props`, `csharp/Bpcg.slnx`, 프로젝트 3개
- [x] `.gitignore`(golden 예외, C# 빌드 출력), `.gitattributes`, `.editorconfig`(`[*.cs]`)
- [x] `Bpcg.IO`: `Npy`, `Npz`, `PyJson`, `Toml`
- [x] `Bpcg.Numerics`: pairwise 합·평균, `Linspace`, `Cross3`·`Dot3`·`Norm3`
- [x] `Bpcg.Tests`: `Golden` 읽기, `Compare`
- [x] `csharp/golden/export_golden.py` (core·IO 사례)
- [x] core: `Hashing`, `Noise`, `Constants`, `Fields`, `Package`, `Paths`, `Config`, `Cubesphere`, `Graph`, `Distance`, `Resample`
- [x] 진행 기록 갱신, 커밋
- [x] core 코드 검토 (사용자가 생략하고 계속하기로 함, D13)

### 단계 2: 묶음별 포팅 (묶음마다 golden 사례 → C# → 대조 시험 → 기록 → 커밋)

- [x] hydro (2026-10-03, 대조 시험 20개, 모두 비트 일치)
- [ ] planet
- [ ] geology
- [ ] landscape
- [ ] subsurface
- [ ] metrics (FFT·polyfit 조사 포함)
- [ ] pipeline·hero
- [ ] volume
- [ ] bake (FFT 조사 포함)
- [ ] cli (planet, hero, bake, all)

### 단계 3: 끝에서 끝 대조

- [ ] C# 콘솔로 `all --profile tiny`를 돌려 Python 결과와 파일별로 비교(8장 규칙)
- [ ] 단계별 실행 시간 표 채우기(8장)

### 단계 4: Godot 연결 (시작 전에 확인을 받음)

- [x] 0장 쟁점 1(목표) 정하기: Godot 안에서 계산, GDScript 도 C# 으로 (2026-10-03). 9(엔진 계산 규칙 문서)는 아직
- [x] .NET 판 프로젝트를 `csharp/Bpcg.Engine/` 에 새로 만들고 `csharp/Bpcg`를 ProjectReference로 참조 (engine/ 은 아직 그대로)
- [x] C# 처음 화면이 생성·굽기를 주 스레드 밖에서 부르고 `user://runs`에 씀, `BakedPaths`가 그 폴더를 읽음
- [ ] 설치 스크립트, CI(`setup-dotnet`, `use-dotnet: true`, `dotnet test`), docs/conventions.md·engine/README.md의 '표준판' 규칙(9장 목록)

## 13. 부록: 분석이 남긴 질문

0장의 '결정이 필요한 쟁점'으로 모으지 않은 나머지 질문입니다. 그 묶음을 옮길 때 다시 봅니다.

### core-a

- 이름 충돌: config.py의 모듈 함수를 Config 클래스의 static 멤버(Config.Load, Config.ParseAssignment, Config.CheckedOverrides, Config.FixedKeys)로 두는 안을 제안합니다. 규약의 'public static class <Mod>'에서 벗어나므로 승인이 필요합니다. 대안은 별도의 static class ConfigModule입니다.
- FIELDS의 bool·int8 원소 형을 bool[]·sbyte[]로 정할지 결정이 필요합니다. 지시문은 다섯 형만 나열합니다.
- 단계 4의 설정 원천을 정해야 합니다. - (가) configs/**/*.toml을 Bpcg.dll에 EmbeddedResource로 넣고, 파일이 없을 때 씁니다. - (나) Godot 쪽이 res://에서 읽은 글자를 IConfigSource로 넘깁니다. - (다) 첫 실행 때 user://로 복사합니다. 제안은 (가)와 Paths.Configure입니다. Python에 없는 환경 변수(BPCG_ROOT 등)를 C#에만 둘지도 정해야 합니다.
- TOML 패키지로 Tomlyn 2.x(BSD-2-Clause, TOML 1.1 전용, 2.10.1 고정 제안)를 써도 될지 결정이 필요합니다. tomllib은 TOML 1.0이지만, 지금 설정 파일에는 차이가 없고 --set의 일부 오류 종류만 달라집니다.
- int64 밖 정수 덮어쓰기의 차이를 받아들일지 결정이 필요합니다. Python은 checked_overrides를 통과한 뒤 파이프라인에서 OverflowError로 멈추고, C#은 Coerce에서 거부합니다.
- 예외 대응을 정해야 합니다. Python ValueError를 C#에서 ArgumentException으로 할지, 공통 예외(BpcgValueException 등)로 할지입니다. 메시지를 Python과 글자까지 같게 할지도 정해야 합니다. 제안은 시험이 보는 핵심어만 같게 하고, difflib 힌트 목록은 정확히 맞추는 것입니다.
- C#이 쓴 manifest의 bpcg_version을 '0.1.0' 그대로 둘지, 구현을 구분하는 필드를 더할지 정해야 합니다. 필드를 더하면 끝에서 끝 비교가 그 필드를 빼야 합니다.
- manifest JSON을 바이트 단위로 같게 할지(키 순서, 실수 표기, 들여쓰기 포함), 파싱한 값으로 같게 할지 정해야 합니다. config_digest는 어느 쪽이든 글자 단위로 같아야 합니다.
- PyJson의 실수 표기를 .NET "R"의 최단 자릿수에 기댈지, 처음부터 Ryu나 CPython dtoa를 옮길지 정해야 합니다. golden(실수 repr 2만 건) 결과를 보고 결정하기를 제안합니다.
- 배열 API의 공통 형식을 정해야 합니다. double[]로 할지, ReadOnlySpan<double> 입력과 Span<double> 출력으로 할지는 전 모듈에 영향이 있습니다.
- difflib 대체를 Config.cs 안의 private 클래스로 둘지, 공유 위치(예: Bpcg.Text)로 뺄지 정해야 합니다.
- .editorconfig에서 CA1305·CA1307·CA1309·CA1310을 오류로 둘지 정해야 합니다. 대안은 라이브러리에 InvariantGlobalization을 켜는 것이며, 목적은 EndsWith, Compare, ToString, Parse가 문화권을 타지 않게 하는 것입니다.
- TOML 읽기 방식을 정해야 합니다. Tomlyn 2.10.1은 2^63 이상 2^64 미만의 양의 정수를 조용히 음수로 감으므로, 검사 없이 쓰면 안 됩니다. - (A) Tomlyn 2.10.1(BSD-2-Clause)에 정수 원문 범위 검사(TomlParser 사건의 Span)와 BOM 사전 검사를 더하고, TOML 1.1 차이를 받아들입니다. - (B) tomllib(_parser.py 703줄과 _re.py 107줄, SPDX MIT, Taneli Hukkinen)을 옮겨 TOML 1.0과 BareText 판정을 그대로 맞춥니다. 제안은 (B)입니다. 의존성이 없고 --set 판정이 같아집니다.
- 이름을 바꿀지 승인이 필요합니다. 1차 분석의 Config.Load·GetOr 대신 규약대로 Config.LoadConfig와 Get(key, default)을 쓰고, __getattr__과 __getitem__은 인덱서로 두는 안입니다.
- 의심 버그 'digest 불일치'(비유한값·1,024개 넘는 리스트)를 C#이 그대로 재현할지 정해야 합니다. 제안은 재현입니다. Python을 고치지 않는 원칙과 맞습니다.
- C#이 쓰는 manifest의 줄 끝을 정해야 합니다. Python write_text는 Windows에서 CRLF를 씁니다. 운영체제마다 Python과 바이트까지 같게 할지, LF로 고정하고 바이트 비교는 macOS·Linux에서만 할지 정해야 합니다.
- Paths.FindRoot에 [CallerFilePath](빌드할 때의 소스 경로)를 쓸지 승인이 필요합니다. 또 단계 0의 Godot 4.7.2 .NET 확인 때, 편집기에서 AppContext.BaseDirectory와 Assembly.Location 값을 실제로 찍어 볼지 정해야 합니다.
- golden 위치 규칙을 정해야 합니다. Python golden 스크립트가 bpcg.core.paths.OUT/golden에 쓰면 출력 위치가 BPCG_OUT을 따릅니다. C# 로더도 Paths.Out을 따를지, ROOT/out/golden으로 고정할지 정해야 합니다.
- Config.FromDict(manifest 설정으로 Config 만들기)와 Section 생성자를 public으로 둘지, InternalsVisibleTo(Bpcg.Tests)로 감출지 정해야 합니다.

### core-b

- 플랫폼 정책: golden 은 macOS arm64 numpy(= Apple libm)로 만들고, C# 도 같은 libm 을 부르므로 mac 에서는 비트 일치를 기대합니다. Linux·Windows CI 에서는 libm 이 달라 pos·area·dist·spacing 이 1ulp 다를 수 있고, 그 차이가 뒤 단계의 D8·문턱 분기를 바꿀 수 있습니다. CI 를 macOS 러너로 돌릴지, 비-mac 에서는 core② 출력 대신 golden 입력을 받아 뒤 단계를 대조할지 정해야 합니다.
- 커널 단위 대조를 위해 _propagate_sources·_final_distance·_bilinear_kernel·_ghost_stencil 대응 메서드를 internal 로 두고 InternalsVisibleTo 로 Bpcg.Tests 에 공개하자고 제안합니다. 공용 규칙으로 정할지 결정이 필요합니다.
- np.linspace 복제를 둘 곳이 제안 목록(Bpcg.Numerics)에 없습니다. NpGrid(Linspace, Arange)를 새로 만들지 PyMath 에 넣을지 정해야 합니다.
- 예외 대응 규칙: Python ValueError 는 ArgumentException(또는 공용 BpcgValueException), 내부 assert 는 Release 에서도 검사하는 InvalidOperationException 으로 옮기는 것을 공용 규칙으로 둘지 정해야 합니다.
- sample_sphere·resample_sphere 의 method 문자열은 C# 에서 SampleSphere(linear) 와 SampleSphereNearest<T> 로 나누자고 제안합니다. CellGraph.Kind 는 manifest 에 'sphere'·'flat' 로 쓰이므로 string 으로 둘지 enum + 변환으로 둘지 골라야 합니다.
- intp 결과(CellOf, NearestSource 의 src)를 long[] 로 둘지 int[] 로 둘지 정해야 합니다. long[] 은 golden(int64)과 맞지만 하류에서 (int) 변환이 늘어납니다.
- 흔들기 1.0 의 설계 확인(의심 버그 2번)을 Python 쪽 문제로 기록할지 정해야 합니다.
- src 에서 쓰지 않는 FACE_BASIS·CellGraph.as_image·width·boundary_mask·cross_face_pairs 를 옮길지 정해야 합니다. 제안: FACE_BASIS 와 as_image 는 생략하고, 나머지는 테스트와 문서 계약(pipeline.md 3절) 때문에 옮깁니다.
- NeighborTable 은 int32 라 n ≤ 18918 에서만 맞습니다. C# 에서 이를 넘으면 예외를 낼지 정해야 합니다(제안: 예외).

### hydro

- 셀 번호 배열의 원소형을 정해야 합니다(rcv, order, start, donors, 유역, 강 구간). numpy int64를 그대로 long[]로 두자고 제안합니다. dtype을 그대로 따르는 규칙이 단순하고, rivers.npz와 landscape 커널의 order·rcv도 int64이기 때문입니다. N < 2^31이므로 int[]로 줄이는 선택지도 있으며, landscape·subsurface·metrics 담당과 함께 정해야 합니다.
- CS0542 규칙을 정해야 합니다. accumulate.py의 함수 accumulate를 정적 클래스 Accumulate에 넣으면 컴파일 오류가 납니다. 모듈 정적 클래스가 그 멤버나 같은 모듈의 클래스와 이름이 겹치면, 정적 클래스에 Module 접미사를 붙이자고 제안합니다(AccumulateModule, config.py의 ConfigModule 옆에 sealed class Config). 공통 장에서 한 번에 정할 일입니다.
- (N, K) 배열을 C#에서 어떻게 표현할지 정해야 합니다. (int[] nbr, int nSlot) 인자 쌍과 Bpcg.Numerics의 2차원 래퍼 형(예: Array2D<T>(T[] Data, int Rows, int Cols)) 가운데 하나를 고르고, core의 CellGraph(Nbr, Dist) 표현과 맞춰야 합니다. hydro는 bake/detail이 만든 격자 nbr도 받으므로 CellGraph가 아니라 배열을 받아야 합니다.
- 예외 대응을 정해야 합니다. ValueError를 ArgumentException과 전용 형 가운데 무엇으로 옮길지, numba ZeroDivisionError를 명시 검사와 DivideByZeroException으로 재현할지(재현을 제안), 한국어 메시지 문자열까지 같게 할지를 정합니다.
- 커널 가시성을 정해야 합니다. 커널 단위 대조 시험을 하려면 커널과 힙 도우미를 internal로 두고 InternalsVisibleTo("Bpcg.Tests")를 써야 합니다. 이 방식을 허용할지 정합니다.
- golden 캡처 방식을 정해야 합니다. 기록 래퍼로 모듈 속성을 바꿔치기하는 방식은 src를 고치지 않고 반복 중간 상태까지 얻습니다. 이 방식이 'src 함수를 부르기만 한다'는 규칙에 맞는지 판단해야 하며, 맞지 않으면 SolverResult로 다시 만든 입력만 써야 합니다.
- RiverSegments 반환형을 정해야 합니다. Python과 1:1인 List<long[]>에 더해, bake/bundle이 그대로 쓰는 CSR(cells, offsets) 함수를 internal로 따로 내놓을지 정합니다.
- pipeline.md 6절의 outlet_of int32 표기를 문서에서도 고칠지, 진행 기록에만 적을지 정해야 합니다. Python 코드는 고치지 않는 원칙과는 별개 문제입니다.
- golden 파일을 압축할지 정해야 합니다. np.savez_compressed를 쓰면 크기가 약 1/3로 줄지만, C# Npz 읽기가 ZipArchive의 deflate를 지원해야 합니다. 공통 IO에서 정할 일입니다.
- Python 동작이 정의되지 않은 잘못된 입력에서 C#이 어떻게 동작할지 정해야 합니다. 위상 순서가 아닌 order는 Python에서 초기화 안 된 메모리를 읽고, 머리가 중복된 order는 범위 밖에 씁니다. C#이 0 초기화 결과와 IndexOutOfRangeException을 그대로 내도 되는지, 아니면 명시적 ArgumentException 검사를 더할지(Python보다 엄격해짐) 결정이 필요합니다. 검사를 더하지 않고 진행 기록에 적기를 제안합니다.
- Bpcg.Tests.Compare의 exact 비교에서 NaN을 비트로 볼지 IsNaN 동등으로 볼지 공통 장에서 정해야 합니다. hydro는 입력 NaN 전파만 있어 비트 비교로 충분하지만, 새로 생긴 NaN은 x64와 arm64의 부호 비트가 다를 수 있습니다.
- CS0542 해결 방식을 공통 장에서 config.py의 Config와 함께 정해야 합니다. 제안은 정적 클래스를 AccumulateModule로 하는 것이고, 대안은 클래스 이름 Accumulate를 지키고 메서드 이름을 바꾸는 것(예: Accumulate.Run)입니다. 함수 이름 1:1을 지키는 Module 접미사를 제안합니다.
- 1 MB를 넘는 golden(시험 지형 3종, 로페 512²)은 out/golden에만 둡니다. 그 파일이 없을 때 C# 시험을 건너뛸지(Skip) 실패로 볼지 정해야 합니다.

### planet-a

- libm 차이 정책: golden은 macOS arm64 libm 값입니다. CI를 리눅스·윈도우에서 돌리면 초월 함수를 거친 값이 1 ulp 다를 수 있습니다. 측정으로는 macOS cos 7/96, sin 2/48, 소프트맥스 exp 32/16896이 올바른 반올림이 아니었습니다. 또 .NET 런타임의 floatdouble.cpp는 MSVC에서 /fp:fast 지시 아래 컴파일되어, 윈도우에서는 다른 구현을 탈 가능성도 있습니다(미검증). 'tolerance' 대상에 macOS 전용 비트 일치 확인을 Trait로 따로 둘지, 모든 OS에서 허용 오차만 볼지 정해야 합니다.
- golden 스크립트가 호출 지점 인자를 잡으려고 모듈 속성을 감싸는 방식(예: plates._argmax_dot_kernel, classify_boundaries를 기록 래퍼로 바꿔치기)이 'src/bpcg의 함수를 부르기만 한다'는 규칙에 맞는지 정해야 합니다. 맞지 않으면 q와 회차별 ω 같은 중간값은 공개 함수의 입출력으로만 대조합니다.
- Python ValueError를 C#에서 어떤 예외로 옮길지(ArgumentException 계열 또는 공용 BpcgValueException) 공통으로 정해야 합니다. check_bool_mask의 bool dtype 검사와 정수 dtype 변환은 C# 형이 대신하므로 사라집니다.
- generate_plates의 info를 형 있는 클래스(PlatesInfo + ToJsonable)로 둘지, 순서 있는 사전으로 둘지 정해야 합니다. manifest meta.info.plates의 키 이름·삽입 순서(seeds, omegas, omegas_initial, flips, ocean_fraction, plate_area_m2, plates_without_divergent, n_boundary, age_provisional_myr, spreading_kinematic_m_per_yr, spreading_floor_m_per_yr)와 flips 항목의 키 순서(round, plate, neighbor, method)가 Python과 같아야 합니다.
- margin guard 시험을 도입할지 정해야 합니다. 범주 결정 여유가 상대 1e-10 아래로 줄면 실패하는 시험입니다. tiny만 할지, laptop 거친 격자(n=128, 약 0.1 s)까지 할지도 정해야 합니다.
- 내부 칸 목록(np.flatnonzero(...).astype(int64))을 C#에서 int[]로 둘지 long[]로 둘지 정해야 합니다. 공용 규칙(intp → long)에 맞추면 long[]입니다. 제안은 파일·대조로 나가지 않는 내부 배열에는 int[]를 허용하고, nearest_source의 src처럼 대조하는 값만 long[]로 두는 것입니다.
- Python 자체도 OS마다 비트 단위로 같은 값을 내지 않을 수 있습니다. macOS libm은 올바른 반올림이 아니고, 1-D norm은 BLAS ddot을 탑니다(리눅스 OpenBLAS는 미검증). conventions.md 6절의 '맥·연구실 PC가 같은 행성'을 '범주는 같고 실수는 1e-15 수준'으로 고쳐 적을지 정해야 합니다.
- 삼중 접합 오염(의심 버그)을 나중에 Python에서 고칠지는 팀이 결정합니다. 포팅 기간에는 규칙대로 그대로 옮깁니다.
- nearest_source 동률 때문에 core 그래프 pos가 1 ulp만 달라도 planet 범주(kind·side)가 몇 칸 갈립니다. 단계 3 끝에서 끝 대조의 '완전 일치' 판정을 macOS-arm64에서만 하고, 리눅스·윈도우에서는 갈린 칸의 수·위치를 기록만 할지 정해야 합니다.
- Python의 nearest_source 동률 취약성(대칭 격자 동률을 칸 번호로 풂)이 conventions.md 6절의 '맥과 연구실 PC가 같은 행성'과 충돌합니다. 이를 팀에 의심 버그로 올릴지, 포팅이 끝난 뒤 Python·C#을 함께 고칠지 정해야 합니다(포팅 기간에는 그대로 옮김).
- 작은 반지름 그래프 sphere_graph(16, 1_000_000.0)를 '손으로 만든 작은 입력'으로 golden에 넣어도 되는지 확인이 필요합니다. tiny 프로필만으로는 대륙 가장자리 얇아짐·±50 km 전이 분기가 한 번도 돌지 않습니다.
- FieldSet이 FIELDS dtype(float32)이 아니라 실제 numpy dtype(float64)으로 배열을 들도록 core·bake 담당과 합의해야 합니다. plates → crust → uplift → materials가 float64 값을 그대로 넘깁니다.
- NpSort.LexSort의 NaN 정책을 정해야 합니다. numpy처럼 'NaN 끝'으로 통일할지, 입력 검사 예외로 바꿀지 공통으로 결정해야 합니다(Python은 예외를 내지 않음).
- 단계 4에서 Godot 프로세스 안 C# 작업 스레드의 FTZ·DAZ 상태를 확인하는 시험을 둘지 정해야 합니다. 해구 값에 비정규수가 생깁니다(tiny 3칸, laptop 217칸).

### planet-b

- NpUfunc.Tanh의 비트를 맞추려면 Math.FusedMultiplyAdd가 필요합니다. numpy가 FMA를 쓰고, FMA 없는 Horner는 1.7%가 다릅니다. 지시문의 'FusedMultiplyAdd 금지'에 'numpy SIMD 구현을 복제할 때는 예외'를 둘지 정해 주십시오. 대안은 Math.Tanh와 허용 오차인데, 그러면 L0 칸의 3.7%에서 runoff_eff가 달라져 솔버 대조가 흔들립니다.
- golden 허용 오차의 플랫폼 정책을 정해야 합니다. golden을 만든 macOS에서는 libm이 같아 exact가 기대되지만, CI(Linux glibc)나 연구실 PC(Windows UCRT)에서는 exp·sin·asin·tan·atan이 1 ulp 다를 수 있습니다. 시험을 플랫폼별로 나눌지(macOS exact, 그 밖은 ulp 허용), 처음부터 ulp 허용으로 둘지 결정이 필요합니다.
- NaN 비트 규칙을 정해야 합니다. Python 묶음은 양의 quiet NaN(float32 0x7FC00000)을 씁니다. C#이 쓰는 NaN을 모두 PyMath.NaN(0x7FF8000000000000)으로 통일하는 규칙을 공용 장에 넣을지, 대조에서 NaN 페이로드를 무시할지 정해 주십시오. .NET double.NaN의 실제 비트는 SDK를 설치한 뒤 확인합니다.
- 필드 묶음 형을 정해야 합니다. OrderedDictionary<string, Array>(제안)와 core가 정할 전용 FieldSet 형 중에서 고릅니다. info도 sealed class(MaterialsInfo·TransferInfo)와 명시한 직렬화 순서(제안), 또는 순서 있는 dict 중에서 정해야 합니다.
- LabelComponents의 labels 형을 정해야 합니다. int[](제안, 내부 전용)와 long[](numpy int64 그대로) 중에서 고릅니다. cell_of 결과 idx는 규칙대로 long[]로 두었습니다.
- 의심 버그 1(이어지지 않은 분지의 물 부피)과 2(강수 노이즈 평균 0.944)는 명세 공식 그대로이므로 포팅에서는 옮기기만 합니다. 알고리즘 수정을 포팅 뒤에 팀이 따로 다룰지 확인이 필요합니다.
- TransferMaterials golden은 약 1.5 MB라 커밋 한도(1 MB)를 넘습니다. 압축 npz로 커밋할지, out/golden에만 두고 export_golden.py로 다시 만들지 정해 주십시오.
- 거친 격자 build_materials의 uplift·climate 결과는 파이프라인에서 쓰이지 않고 버려집니다. 충실한 포팅과 golden 대조를 위해 C#에서도 그대로 계산하는 것을 제안합니다.
- np.tanh 결과는 numpy 버전과 빌드(Highway 대상 CPU)에 따라 바뀔 수 있습니다. golden manifest에 numpy·numba 버전과 CPU 아키텍처를 적는 규칙을 둘지 정해 주십시오.

### geology

- C# 필드 컨테이너 형과 메모리 dtype 방침을 정해야 합니다. 형은 IReadOnlyDictionary<string, Array> 같은 것이 후보입니다. 행성 호출 지점의 dist_convergent_m, dist_arc_m, uplift_m_per_yr, exhumation_m은 FIELDS로는 float32인데 메모리에서는 float64입니다(검증). fold_phase와 strata_bottom_m 출력도 메모리에서는 float64입니다. C#도 메모리에서는 double[]로 두고 묶음에 쓸 때만 float로 바꾸는지 여러 묶음에 걸쳐 정해야 합니다.
- (N, L) 배열의 모양을 넘기는 방식을 정해야 합니다. nCells와 nLayers를 인자로 넘길지, Bpcg.Numerics에 Array2D<T>(Data, Rows, Cols)를 둘지입니다. 솔버가 L = 0 배열을 넘기므로 길이를 나눠 L을 구하는 방식은 쓸 수 없습니다.
- _fallback_fbm3(ImportError 때만 쓰는 대체 노이즈, BLAS @과 libm 사용)를 옮기지 않는 것에 동의하는지 확인이 필요합니다.
- Python ValueError에 대응하는 C# 예외 형을 정해야 합니다(ArgumentException에 같은 한국어 메시지 등). 또 N = 0이고 z가 1차원일 때 Python만 오류를 내는 동작은 펼친 배열로는 구분할 수 없습니다. C#은 빈 배열을 돌려주는 것으로 두어도 되는지 확인이 필요합니다.
- 습곡 변위 sin의 대조 기준을 정해야 합니다. macOS에서는 비트 일치를 단언하고 Linux와 Windows CI에서만 4·ulp(A) 허용 오차를 쓸지, 모든 OS에서 허용 오차 하나로 할지입니다.
- golden 스크립트가 호출 지점 입력을 얻으려고 실행 중에 함수를 감싸는(monkeypatch) 방식이 'src/bpcg 함수를 부르기만 한다'는 규칙에 맞는지 확인이 필요합니다. 맞지 않으면 generate_planet을 단계별로 다시 부를 공개 함수가 필요합니다.
- 의심 버그 5(히어로가 세기 한계로 줄인 U'와 줄이기 전 U_max로 습곡 변위를 다시 계산해 L0와 최대 265 m 차이)가 의도인지 확인이 필요합니다. 포팅은 그대로 따릅니다.
- Rocks와 Model 범주 상수의 형을 정해야 합니다. byte 배열과 바로 비교하기 좋은 const byte로 할지, const int로 할지입니다.
- C# Math.Max의 IEEE 754-2019 의미(−0 < +0, NaN 전파)와 Roslyn 실수 리터럴의 정확한 반올림은 .NET 문서에 기댄 가정입니다. SDK가 없어 실행으로 확인하지 못했으므로 단계 1에서 xUnit으로 먼저 확인할지 정해야 합니다.
- C# API에서 1차원 z와 2차원 z를 어떻게 구분할지 정해야 합니다. 제안은 LayerIndex와 RockAt의 int? zCols이고, null이면 (N,)입니다. 1차원이고 nCells = 0일 때 numpy 문구('cannot reshape array of size 0 into shape (0,newaxis)')로 예외를 던져 scorecard.json까지 같게 할지 확인이 필요합니다.
- Python ValueError에 대응하는 C# 예외 형을 하나로 정해야 합니다. metrics/scorecard._run(scorecard.py:241-245)과 cli cmd_all의 평면 히어로 대체(cli.py:287-289)가 이 형만 잡게 하려는 것입니다. 메시지 문구는 파일에 남는 경우(scorecard.json)만 Python과 같게 맞춰도 되는지 확인이 필요합니다.
- Bpcg.Tests.Compare의 'exact' 실수 비교가 비트 비교인지 == 비교인지 정해야 합니다. tiny 행성의 습곡 변위에는 −0.0이 34칸 있으므로, 비트 비교라면 C#이 같은 연산으로 −0.0을 내야 합니다.
- diag를 형 있는 record(GeologyDiag)로 둘지, 순서 있는 JSON 노드로 둘지 정해야 합니다. 히어로 refine이 manifest에서 u_max_m_per_yr를 다시 읽고 키 순서도 manifest에 남으므로, 묶음 전체에서 같은 방식을 쓰는 것이 좋습니다.
- _sub_seed가 geology(>> 33), bake/detail(>> 1, k 인자), volume/sample(>> 1, k 인자)에 따로 있습니다. 각 클래스에 SubSeed를 따로 두고 Bpcg.Numerics 공용 도우미로 합치지 않는 데 동의하는지 확인이 필요합니다.

### landscape

- FMA 예외 여부: scipy arm64 휠의 cKDTree 거리와 RegularGridInterpolator 빠른 경로는 FMA로 컴파일되어 있습니다(검증). 지시문은 Math.FusedMultiplyAdd를 금지합니다. Bpcg.Numerics의 KdTree와 RegularGridInterpolator에서만 FMA를 허용할지 정해야 합니다. 허용하지 않으면 warm start z_init의 26% 칸이 1 ulp 다르고(tiny 히어로의 최종 결과는 같았음), 대조를 tolerance로 바꿔야 합니다. x86-64 scipy 휠은 FMA 없이 빌드되었을 것으로 보이며(추정, 미검증), 그렇다면 Python 결과 자체도 플랫폼마다 다릅니다.
- golden 플랫폼과 비교 모드: golden은 macOS arm64에서 만들므로, C# 대조 시험이 exact를 기대할 수 있는 것은 같은 OS·아키텍처·libm에서 돌 때뿐입니다. CI를 macOS arm64 러너에서 돌릴지, Linux에서는 libm 탐침 결과에 따라 tolerance 모드로 돌릴지 정해야 합니다.
- .NET Math.Exp/Log/Pow/Sin/Asin이 macOS에서 libSystem의 libm을 그대로 부르는지는 SDK가 없어 확인하지 못했습니다(가정). 단계 1에서 libm 탐침 표로 확인해야 합니다. pow(x,0.5)를 sqrt로 바꾸는 JIT 최적화가 없는지도 같이 봅니다.
- 수신 셀·순서·꼭짓점 배열의 원소 형을 정해야 합니다. Python은 int64이고 FIELDS의 receiver는 int32입니다. hydro 예시(TopoOrder(int[]))에 맞춰 int[]로 두는 안을 제안합니다(N < 2^31).
- 히어로 golden은 CLI all 경로(행성 묶음을 float32로 쓰고 다시 읽기, config_from_manifest)에서 잡아야 C#의 끝에서 끝 결과와 맞습니다. C# CLI도 같은 파일 왕복을 하는지 pipeline·cli 담당과 맞춰야 합니다.
- coarse_warm_start의 factor < 2를 어떻게 처리할지 정해야 합니다. Python은 RecursionError나 ZeroDivisionError를 냅니다. C#에서 같은 재귀를 두면 StackOverflow로 프로세스가 죽습니다. ArgumentException으로 막는 안(오류 경로만 달라짐)을 제안합니다.
- fans.reroute는 파이프라인이 쓰지 않습니다(pipeline.reroute_after_fans를 씀). Python 시험만 이 함수를 씁니다. 공개 API로 함께 옮길지, 건너뛸지 정해야 합니다.
- 교사 강요 대조를 위한 시험 이음새를 열어도 되는지 정해야 합니다. 대상은 Solver.Step(internal), 반복 관찰 콜백, Warmstart.InterpolateFromCoarse이며, InternalsVisibleTo(Bpcg.Tests)로 엽니다.
- 파일·클래스 이름을 정해야 합니다. 모듈 이름 규칙을 그대로 따르면 Warmstart.cs와 Warmstart이고, 읽기 쉬운 WarmStart를 쓸 수도 있습니다.
- stop_dz_m이 효과가 없는 점(의심 버그 1)을 진행 기록에만 적고 동작을 그대로 옮기는 것이 맞는지 확인이 필요합니다.
- ValueError를 어떤 C# 예외로 옮길지(ArgumentException 계열을 제안), 한국어 메시지를 원문 그대로 둘지 공통 규칙이 필요합니다.

### subsurface

- 단계 함수 반환형을 묶음 사이에 통일해야 합니다. 형 있는 sealed record + ToFields()(Python dict 순서) 안과 Dictionary<string, Array> 안 가운데 pipeline 담당과 함께 정해야 합니다(이 보고서는 record 안).
- 셀 번호 배열의 폭을 정해야 합니다. receiver·order·src·cells는 numpy에서 int64인데 C#은 int[]로 제안합니다(N < 2^31, 히어로 상한 5000² = 25M). Npy 로더의 int64 -> int 범위 검사도 포함합니다. 예시 규칙(TopoOrder(int[]))과 맞지만 확정이 필요합니다.
- NaN 비트 규칙을 정해야 합니다. 양의 quiet NaN 상수(예: PyMath.NaN = 0x7FF8000000000000)를 전역 규칙으로 할지, Compare.Exact가 NaN 부호를 무시할지 결정이 필요합니다. 단계 3에서 .npy 바이트 비교를 하려면 앞쪽이 필요합니다.
- libm 의존 값(Q^0.4, 10^x, exp, log, 구면 sin·atan2)의 golden 비교 정책을 정해야 합니다. macOS arm64에서는 비트 일치가 기대되지만 CI(Linux·Windows)에서는 ulp 허용으로 돌릴지, 비교를 macOS로 한정할지 결정이 필요합니다.
- PyMath에 Python 내장 max 규칙의 PyMax(a, b) = b > a ? b : a와 NaN 상수를 더하는 것을 공용 장 담당과 합의해야 합니다.
- Python ValueError를 C# ArgumentException(같은 한국어 문구, 같은 검사 순서)으로 옮길지, 시험에서 문구까지 비교할지 정해야 합니다.
- _window_max_kernel은 묶음마다 N 길이 stamp를 둡니다(numba int64: 5000² 히어로·12스레드면 2.4 GB). C#에서 int[] stamp를 유지할지, 출발 칸별 방문 목록 초기화로 바꿀지 정해야 합니다(결과 동일, 메모리 절약). '같은 순서의 반복문' 원칙의 해석 문제입니다.
- L0에서는 water_bodies 안의 valley_depth(z) 결과를 pipeline이 곧바로 valley_depth(z + relief_m)로 덮어씁니다. C#도 두 번 계산할지(충실, 비용 작음) 정해야 합니다. 이 보고서는 두 번 계산하는 안입니다.
- 의심 버그 5건을 기록만 하고 고치지 않는 데 동의가 필요합니다. 특히 L0 충적층이 deposition_g_l0 = 0에 묶인 것이 의도인지, pipeline.md 8.1·8.4 문구를 코드에 맞춰 고칠지 확인이 필요합니다.
- config 형 변환 규칙을 core/config 담당이 정해야 합니다. int(cv.levels)는 TOML 실수 2.0을 받고 소수는 버리며, float(...)는 TOML 정수를 받습니다.
- 커널 가시성 규칙을 정해야 합니다. 커널 단위 대조 시험을 하려면 커널을 internal static으로 두고 Bpcg에 InternalsVisibleTo("Bpcg.Tests")를 둬야 합니다. 이를 모든 묶음의 공통 규칙으로 할지 정해야 합니다.
- golden 추출 방식을 정해야 합니다. 기록용 감싸개(모듈 전역 교체)가 명세의 'src/bpcg의 함수를 부르기만'에 맞는지 판단이 필요합니다. 감싸개를 쓰지 않으면 export 스크립트가 run_stages_2_to_4의 순서를 다시 짜야 하는데, soil의 fan_raise는 솔버 고도가 있어야 만들 수 있어 흐름이 어긋날 위험이 있습니다.
- golden을 macOS arm64에서만 만들고 CPU 기능 목록을 메타에 남길지 정해야 합니다. numpy는 x86 AVX512에서 exp·log·pow를 자체 SIMD로 계산합니다. 그래서 CI(Linux·Windows)에서는 golden을 다시 만들지 않고 저장된 golden을 읽기만 하는 안을 제안합니다.
- diag 표현을 정해야 합니다. WaterTableDiag 같은 형 있는 record로 둘지, pipeline diag 전체와 같은 순서 있는 JSON 노드(System.Text.Json.Nodes.JsonObject 등)로 둘지는 pipeline·bake 담당과 함께 정해야 합니다.
- pipeline.md 8.2의 bare_rock 문구(가파르면 맨 암반)를 코드(흙과 충적층이 모두 0이면 맨 암반)에 맞춰 고칠지 정해야 합니다. 8.1(두 번 훑기, 강에서 호수 제외, 골짜기 깊이 0 하한)과 8.4(덮개 이웃 조건, 벽 규칙) 문구도 함께 정해야 합니다.
- 오류 문구를 어디까지 비교할지 정해야 합니다. Python repr 서식(1e-07, nan, (4096,), 목록 repr)까지 재현할지, 예외 형과 한국어 앞부분만 비교할지입니다.
- Config 형 이름을 정해야 합니다. core/config.py의 class Config와 모듈 정적 클래스가 이름이 겹치는 문제를 어떻게 푸느냐에 따라 이 묶음 시그니처의 Config 형 이름이 바뀝니다.
- 동굴의 NaN 이웃 벽 규칙과 band&covered 분기는 Python 시험이 없습니다. golden 손 사례(계단)만으로 충분한지, Python 시험을 더할지 정해야 합니다. Python 시험을 더하는 것이 포팅 기간 동안 Python을 고치지 않는다는 원칙과 충돌하는지도 판단해야 합니다.

### metrics

- scipy 1.18.1 macOS arm64 휠의 gaussian_filter1d 는 대칭 합의 마지막 (radius mod 8)회를 FMA 로 계산합니다. radius 10 이면 jj = -2, -1 입니다. 이 동작을 Math.FusedMultiplyAdd 로 흉내 내 dens 를 비트 단위로 맞출지 정해야 합니다. 흉내 내면 '계산 순서를 바꾸는 최적화와 FMA 금지' 규칙에 어긋나고, x86 scipy 와도 다릅니다. 제안은 규칙대로 FMA 없이 옮기고 dens 는 4 ulp 허용, 봉우리 출력은 정확히 비교하는 것입니다. 다른 묶음의 gaussian_filter 에도 같은 결정이 적용됩니다.
- 모듈과 같은 이름의 함수(hypsometry.hypsometry, scorecard.scorecard, 다른 묶음의 accumulate.accumulate 등)는 C# 에서 정적 클래스와 같은 이름의 멤버가 되어 CS0542 오류가 납니다. 'Compute' 로 바꾸는 공통 규칙을 제안합니다. class Config 처럼 클래스가 모듈과 같은 이름이면 그 클래스를 그대로 두고 모듈 함수를 그 클래스의 static 멤버로 넣는 방안을 제안합니다.
- Python ValueError 에 대응하는 C# 예외형의 이름과 위치를 정해야 합니다(제안: Bpcg.Core.BpcgValueException). numpy LinAlgError 도 여기에 들어갑니다. scorecard._run 은 이 형만 잡고, KeyError·AttributeError 에 해당하는 예외는 잡지 않습니다.
- receiver, order, main_donor 의 C# 원소 형을 정해야 합니다. metrics 는 _check_rcv 처럼 long[](int64)을 제안하지만, FIELDS 의 receiver 는 int32 이므로 hydro 포팅과 같은 형으로 맞춰야 합니다.
- 점수표 입력인 이질 배열 dict(FieldSet: 이름 → bool/byte/sbyte/int/long/float/double 배열, 삽입 순서, (N, L) 열 수)와 diag 형(LevelDiag 의 nullable 필드로 '키 있음'을 표현)을 core·pipeline 과 함께 정해야 합니다.
- Python 서식 도우미의 위치와 이름을 정해야 합니다. 제안은 Bpcg.IO.PyFormat 에 G, F, Repr, Tuple 을 두고 PyJson 과 함께 쓰는 것입니다. 목록에 없는 NpGrid.Linspace, NpStats.HistogramArrayBins, PyMath.NpMaximum/NpMinimum 이름에도 동의가 필요합니다.
- 의심 버그 1(행성 law 계산에 cfg_l0 대신 cfg 사용)은 포팅에서 고치지 않고 cfg 를 그대로 넘기는 것으로 확인받아야 합니다.
- 비교 기준을 정해야 합니다. 이 맥에서는 numpy 의 log10·exp·pow 가 libm 과 비트 단위로 같아 C# Math.* 도 같기를 기대하지만, Windows·Linux CI 에서는 transcendental 을 거친 값(LawSlopeFromFields, HackFit, dens)을 허용 오차로 비교해야 합니다. 플랫폼별로 기준을 두 개 쓸지 정해야 합니다.
- golden 생성 플랫폼을 arm64 맥으로 고정할지 정해야 합니다. numpy argsort 동점 순서(x86 은 x86-simd-sort), scipy FMA 꼬리, LAPACK(Accelerate 와 OpenBLAS)이 모두 플랫폼마다 다릅니다.
- golden 스크립트가 Numerics 대조 자료(gaussian_filter1d, find_peaks, histogram, polyfit)를 만들려고 numpy·scipy 를 직접 불러도 되는지 정해야 합니다. 지시문은 'src/bpcg 의 함수를 부르기만' 하라고 되어 있습니다.
- 끝에서 끝 비교에서 scorecard.json 은 hack_exponent 와 law_consistency 의 value 와 note 숫자 토큰(.3g)만 값 허용 오차로 보고, 나머지는 글자 단위로 같아야 한다는 규칙을 받아들일지 정해야 합니다.

### pipeline-cli

- 콘솔 문구가 계약인가: 권고는 문구 틀(태그, 한국어, 구분자)과 예외 문구는 글자 그대로 옮기고, 숫자는 Python 호환 도우미로 맞추되 시간 값이 섞이므로 golden 비교에서는 빼는 것입니다. 지금 studio 는 Python CLI 를 부르지만, studio 가 C# CLI 를 부르게 할 계획이면 studio/progress.py 정규식이 잡는 줄과 'Traceback (most recent call last)' 표식까지 계약으로 올릴지 정해야 합니다.
- 명령줄 파서: argparse 부분 집합을 손으로 짤지(권고, 의존성 없음), System.CommandLine(MIT)을 쓸지 정해야 합니다. 긴 옵션 접두 약어(--prof, --no-g), --seed 의 '1_000' 과 int64 를 넘는 값, C# 에서 studio 명령을 고를 때의 종료 코드(2 또는 1)도 함께 정합니다.
- 예외 형: Python ValueError 에 대응하는 C# 전용 예외(가칭 Bpcg.ValueError) 하나를 둘지, numpy 가 스스로 내던 ValueError(빈 배열 max 등)도 Bpcg.Numerics 도우미가 같은 형으로 던지게 할지 정해야 합니다. cmd_all 의 평면 히어로 대체가 이 형에 기댑니다.
- 이름 충돌 규칙: Scorecard.Scorecard·Accumulate.Accumulate·Hypsometry.Hypsometry 는 CS0542 오류이므로 메서드 이름을 Compute 로 바꿀지 정해야 합니다. 모듈과 같은 이름의 클래스(Config, Bundle)는 모듈 함수를 그 클래스의 static 멤버로 합칠지, CLI 형은 namespace Bpcg.Cli 와 겹치지 않게 Commands·Program 으로 둘지도 정합니다.
- 동적 사전 형: diag·info·scorecard·fan_apexes 를 OrderedDictionary<string, object?>(가칭 PyDict)로 둘지, 형 있는 record 와 순서를 지키는 직렬화로 둘지 정해야 합니다. 요소형이 섞이고 (N, L) 모양이 있는 필드 묶음 형(가칭 FieldMap)도 core 항목과 함께 정합니다.
- ROOT 찾기: C# 실행 파일 위치(AppContext.BaseDirectory)에서 위로 pyproject.toml 과 src/bpcg 를 찾는 같은 규칙을 쓸지, BPCG_ROOT 같은 환경 변수를 더할지 정해야 합니다. --engine 의 engine/baked 위치와 Godot 의 user:// (단계 4)와의 관계도 함께 정합니다.
- 스레드 수: Environment.ProcessorCount(affinity·cgroup 반영)는 numba 의 os.cpu_count 와 다를 수 있어 manifest 의 diag.threads 가 달라집니다. 이 값을 비교에서 뺄지, C# 도 NUMBA_NUM_THREADS 환경 변수를 읽을지 정해야 합니다.
- docs/pipeline.md 13절의 pipeline.bake(hero, cfg, out_dir, engine_dir) 는 코드에 없습니다. Godot 용으로 C# Pipeline 에 얇은 Bake 래퍼를 둘지, 진행 기록에만 적을지 정해야 합니다.
- 비트 일치 기준 플랫폼: golden 은 macOS arm64(Apple libm)에서 만들어지고, C# Math.Pow·Exp·Log 는 각 플랫폼의 CRT 를 부릅니다. macOS 에서만 비트 일치를 요구하고 Linux·Windows CI 에서는 허용 오차 모드로 돌릴지 정해야 합니다.
- golden 입력 포착 방식: export_golden.py 가 unittest.mock.patch.object 로 호출 지점(bpcg.pipeline.reroute_after_fans, bpcg.pipeline.strength_limited_uplift, bpcg.hero.refine.run_stages_2_to_4, bpcg.hero.flat.run_stages_2_to_4)을 감싸 인자를 저장하는 것이 'src 함수를 부르기만 한다' 는 규칙에 맞는지 확인이 필요합니다.
- 단계 3의 manifest 비교 제외 목록을 확정해야 합니다: git_commit, 모든 *.seconds, meta.diag.threads. C# 도 bpcg_version '0.1.0' 을 그대로 쓸지도 정합니다.
- 메모리 안 셀 번호 배열 형: Python 은 receiver·order·rivers 를 int64 로 들고 다닙니다. C# 에서 long[](충실)과 int[](메모리 절반) 중 무엇을 쓸지 hydro 항목과 함께 정해야 합니다(결과는 같고, rivers.npz 는 어느 쪽이든 int64 로 써야 함).

### hero

- FMA 허용 여부: scipy 1.18.1 macOS arm64 휠은 brentq.c(3곳), RGI evaluate_linear_2d(3곳), cKDTree 잎 거리(2곳)를 FMA 로 컴파일했습니다(역어셈블로 확인). 명세는 Math.FusedMultiplyAdd 를 금하지만, 이 세 Numerics 복제본(Brent, RegularGridInterpolator.Linear2D, KdTree 잎)에서만 FMA 를 허용해야 golden 과 비트 일치합니다. RGI 를 비FMA 로 쓰면 tiny 4096점 중 1036점이 1ulp 다르고, 이 값이 z_pre·z_init 을 거쳐 솔버로 갑니다. 허용할지 정해 주십시오.
- golden 플랫폼 고정: 1차원 dot(Accelerate), numpy 의 NEON tanh, scipy 휠의 FMA 축약은 플랫폼과 휠마다 다릅니다. golden 은 이 기계(macOS arm64, Python 3.13.15, numpy 2.5.3, scipy 1.18.1)에서만 만들고, golden manifest 에 플랫폼과 버전을 적는 규칙을 제안합니다. 팀원이 윈도 x64 에서 golden 을 다시 만들면 값이 달라질 수 있습니다.
- 예외형 규칙: cli.cmd_all 은 run_hero 의 ValueError(numpy·scipy 가 던진 것 포함)를 잡아 평면 히어로로 바꿉니다. Python ValueError 를 C# 의 한 형(예: Bpcg.ValueError)으로 통일할지 정해야 합니다. 같은 형으로 던질 대상은 빈 배열 Max, Brent 의 부호·NaN 오류입니다. brentq 미수렴(RuntimeError)은 다른 형으로 두어 CLI 가 잡지 않게 할지도 정해야 합니다.
- 이름 충돌: PascalCase 로 바꾸면 함수 edge_hit 와 클래스 EdgeHit 가 겹치고, hero_boundary·hero_grid_info 의 결과 형을 만들 때도 같은 문제가 생깁니다. Domain.EdgeHit(...) 메서드가 Bpcg.Hero.EdgeHit 형을 돌려주는 것은 C# 에서 허용됩니다(형 위치에서는 형만 찾음). 다만 읽기 혼란을 줄이려고, Python 클래스는 이름을 그대로 두고 dict 결과 형에는 HeroBoundaryResult 처럼 Result 접미사를 붙이는 규칙을 제안합니다. config.py 의 Config 문제와 함께 정해 주십시오.
- diag·meta 표현: diag, boundary, grid, parts 는 키 순서와 int/float 구분, 1024 원소 이하 배열이 manifest meta JSON 에 그대로 남습니다. 두 방안 중 어느 쪽을 전역으로 쓸지 정해야 합니다: OrderedDictionary<string, object?> 로 통일, 또는 형 있는 record 에 직렬화 순서를 지정.
- KdTree 범위: scipy 와 모든 경우에 같게 하려면 트리 구성(leafsize 16, balanced median)과 추적기(비FMA 직사각형 거리, 증분 갱신)까지 옮겨야 합니다. 대안은 잎 식(FMA)만 맞추고, |d²−r²| ≤ 4ulp 인 쌍을 검출해 진행 기록에 남기는 것입니다. tiny 의 최소 여유는 5e-4 라 대안도 안전합니다. 어느 쪽인지 Numerics 담당과 함께 정해 주십시오.
- np.tanh 처리: arm64 numpy 의 tanh 는 자체 NEON 구현이라 libm 과 21% 의 입력에서 1ulp 다릅니다. NpMath.Tanh 로 numpy 알고리즘을 비트 단위로 옮길지(행성 기후의 budyko_runoff 도 영향), Math.Tanh 와 허용 오차로 둘지 정해야 합니다. 평면 히어로 강수는 libm tanh 로도 현재 비트 일치합니다.
- 의심 버그(refine 습곡 변위의 U·U_max 기준 섞임)를 C# 에서도 그대로 재현한다는 데 동의하시는지 확인 부탁드립니다. 포팅 규칙상 고치지 않습니다.
- 메모리 경로 golden: Python 시험(test_pipeline)은 generate_hero(cfg, planet) 를 메모리 행성(float64)으로 부릅니다. CLI 는 float32 로 저장한 묶음을 다시 읽은 값을 쓰고, 두 경로는 z_outlet 부터 다릅니다. C# Pipeline.GenerateHero 의 golden 을 CLI 경로(묶음 왕복) 하나로 할지, 두 경로 모두 만들지 정해야 합니다.
- 정수 형: 셀 번호 스칼라(HeroSite.L0Cell, inflow_cell, l0_receiver, l0_donor, EdgeHit.Index)는 int 로, 번호 배열(edge_cells, flatnonzero)은 numpy intp 를 따라 long[] 로 두는 안을 제안합니다. receiver 는 메모리에서 long[](int64)이고 저장할 때 int32 입니다. 승인 여부를 알려 주십시오.

### volume

- golden 기준 플랫폼을 정해야 합니다. 이 묶음의 Python 결과는 macOS arm64 빌드의 libm hypot, scipy(clang FMA 축약과 libc++ 정렬), numpy(실수 arange의 FMA)에 묶여 있습니다. golden을 이 기계(macOS arm64, uv.lock 버전)에서만 만들고 C#은 그 값과 exact로 맞춘다고 정할지 결정이 필요합니다. 리눅스·윈도우에서 golden을 다시 만들면 hypot, arange, KD 승자가 달라질 수 있습니다(확인하지 않음).
- Math.FusedMultiplyAdd 사용을 승인받아야 합니다. 지시 원문은 FMA를 쓰지 않는다고 하지만, PyMath.Hypot, KdTree 점 거리, NpArange 세 곳은 FMA 없이는 Python 빌드 동작을 비트 단위로 재현할 수 없습니다. 이 세 곳은 계산 순서를 바꾸는 최적화가 아니라 재현입니다.
- KdTree의 범위를 정해야 합니다. 합류점 승자가 같으려면 KdTree가 scipy 1.18.1의 트리 순서(build.cxx와 libc++ nth_element·partition)를 그대로 따라야 합니다. libc++(Apache-2.0 WITH LLVM-exception) 알고리즘을 옮겨 쓰는 것과, scipy 판을 올릴 때 golden을 다시 만드는 절차를 승인받아야 합니다.
- HeroState 형의 위치를 정해야 합니다. Python은 duck typing이라 volume이 pipeline을 import하지 않습니다. C#에서 Bpcg.Volume이 Pipeline.cs의 HeroState를 참조하면 의존 방향(core → … → volume → bake)이 뒤집힙니다. HeroState를 다른 이름공간으로 옮길지, HeroVolume이 (CellGraph, fields, LayerColumns, rivers)를 따로 받을지 골라야 합니다.
- 이름 충돌을 풀어야 합니다. HeroVolume.Sample 메서드가 정적 클래스 Sample을 가려 HeroVolume 안의 Sample.X 접근이 컴파일 오류(CS0119)가 됩니다. Sample.cs 맨 위에 using static Bpcg.Volume.Sample;을 두는 것을 제안합니다. 또 HeroVolume을 규칙대로 Sample.cs에 둘지(약 1,200줄), partial 파일로 나눌지도 정해야 합니다.
- evaluate_grid(C = 0)를 어떻게 할지 정해야 합니다. Python은 빈 dict를 돌려 호출자가 KeyError를 냅니다. C#은 요청 키마다 길이 0 배열을 돌려주고 이 차이를 진행 기록에 적는 것을 제안합니다.
- nx 또는 ny = 1일 때의 음수 색인 감기를 C#에서 재현할지(Wrap 도우미), 생성자에서 거부할지 정해야 합니다. 파이프라인은 n ≥ 3이라 결과에는 영향이 없습니다.
- NaN 비트 정책을 정해야 합니다. C#이 쓰는 파일의 NaN을 0x7FF8000000000000으로 정규화할지는 IO 층 공통 규칙으로 정하는 것이 좋습니다.
- VerticalSlice는 C# 호출자가 없어 대조 시험 용도뿐입니다. 그래도 옮길지(약 60줄, golden 1개)와, 디버그용으로 축 없는 원시 RGB PNG(Bpcg.IO.Png) 저장 인자를 둘지 정해야 합니다.
- VolumeEval·EvalKeys 형태를 bake 담당과 맞춰야 합니다. dict 대신 형이 있는 클래스를 쓰고, 키는 d, d1, d_cave, material, solid_material, water를 씁니다.

### bake-a

- 이름 충돌 규칙: bundle.py 의 class Bundle 과 config.py 의 class Config 가 모듈 정적 클래스와 이름이 같습니다. 제안은 형 이름(Bundle, Config)을 그대로 두고, 모듈 함수 정적 클래스를 복수형(Bundles, Configs)으로 두는 것입니다. 형 이름은 수많은 시그니처에 나오기 때문입니다. 반대안은 정적 클래스를 지키고 형 이름에 접미사를 붙이는 것입니다. core 담당과 하나의 규칙으로 정해야 합니다.
- JSON 값 모형: manifest·meta·info·diag·fan_apexes·높이맵 반환 dict 를 OrderedDictionary<string, object?>(.NET 9+; 값은 null/bool/long/double/string/List<object?>/PyDict/NdArray)로 통일할지, System.Text.Json.Nodes 를 쓸지 정해야 합니다. 삽입 순서 보존과 long/double 구분이 필수입니다.
- 필드 그릇 형: write_bundle/read_bundle 과 jsonable 요약에는 numpy dtype 이름과 shape 가 필요합니다. NdArray(dtype 문자열 + int[] shape + Array) 같은 공통 형을 둘지 정해야 합니다. FIELDS 의 bool → bool[], int8 → sbyte[] 제안에는 동의합니다(.npy '|b1' 0/1 바이트, '|i1').
- Python 예외 대응: ValueError, FileNotFoundError, TypeError 를 어느 C# 예외 형으로 옮길지 정해야 합니다. corridor 의 except ValueError(add_fractal_detail)와 pytest 의 거부 사례 대조가 이 결정에 기댑니다.
- npz 바이트 동일 여부: CPython zipfile 과 같은 작은 ZIP writer(force_zip64, 1980-01-01 고정 시각, 0o600 속성, version 45)를 직접 만들지, System.IO.Compression 으로 쓰고 내용만 비교할지 정해야 합니다.
- PNG writer: NuGet(ImageSharp 는 Six Labors Split License 라 MIT/BSD/Apache 가 아님) 대신 ZLibStream 과 CRC32 로 만든 자체 encoder 를 제안합니다. tEXt Software 에 'Matplotlib version3.11.2' 를 흉내 낼지(사실과 다름), 'B-PCG <버전>' 으로 쓸지 뺄지, pHYs(3937/m)를 쓸지 정해야 합니다. 대조는 픽셀로 합니다.
- 볼륨 주입: bake 함수가 IHeroVolume 인터페이스를 받게 하고 대조 시험에서 Python 기록값을 돌려주는 가짜를 끼우는 설계를 volume 담당과 합의해야 합니다. 그래야 bake_strata·cave_surface·build_mesh 를 volume 포트와 떼어 대조할 수 있습니다.
- 단계 4 엔진 코드에서 Bpcg.Bake.Mesh(정적 클래스)와 Godot.Mesh 가 'using Godot; using Bpcg.Bake;' 아래에서 모호해집니다(CS0104). 정적 클래스 이름을 지키고 엔진 쪽에서 별칭을 쓸지 정해야 합니다.
- docs/pipeline.md 13절은 pipeline.py 에 bake(hero, cfg, out_dir, engine_dir) 가 있다고 하지만 코드에는 없습니다(굽기는 cli.run_bake → bake_corridor). C# Pipeline.cs 에 새로 만들지 않고 Cli 가 Corridor.BakeCorridor 를 직접 부르는 안을 제안하며, 문서 불일치는 진행 기록에 적습니다.
- 대조에서 뺄 키 목록을 확정해야 합니다. 후보는 git_commit, 모든 seconds·stages_detail 시간, diag.threads(numba 스레드 수), 그리고 C# 쪽에 같은 뜻이 없는 진단값입니다.
- log 매개변수 기본값: Python 기본은 print 입니다. C# 기본을 Console.WriteLine 으로 할지 null(조용히)로 할지 정해야 합니다. CLI 는 늘 명시해서 넘깁니다.
- golden 크기: BakeStrata 전체 기록(약 9 MB)과 CaveSurface 기록(약 2 MB)은 out/golden 에만 두고, 1 MB 이하 커밋용 축소판(40 m 정사각, 첫 캡슐 주변)을 따로 만들지 정해야 합니다.
- Godot 실행 중에는 git 이 없어 git_commit 이 null 이 됩니다. 이를 그대로 둘지, 빌드 때 커밋 해시를 박아 넣을지 정해야 합니다(Python 과 같은 값을 내려면 null 이 맞음).

### bake-b

- FMA 예외: arm64 기준 라이브러리 자체가 FMA 를 쓰는 곳, 즉 Apple hypot = sqrt(fma(min,min,max·max)) 와 scipy Correlate1D 의 acc = fma(a+b, w, acc) 에만 Math.FusedMultiplyAdd 를 허용할지 정해야 합니다. 허용하면 detail_weight 와 k 격자가 macOS golden 과 비트 단위로 같아집니다. 지시 원문은 FMA 를 금지하고 있습니다. 허용하지 않아도 이산 출력은 안전하다는 실험 근거가 있으므로, 권고는 hypot 만 허용하고 gaussian 은 허용 오차로 두는 것입니다.
- golden 기준 플랫폼: Python 결과 자체가 플랫폼마다 다릅니다(Apple libm 과 glibc·UCRT, arm64 scipy·numpy 의 FMA, x86-64 Linux pocketfft 의 80비트 회전각). golden 은 macOS arm64 에서만 만들고, macOS 에서 비트 일치로 본 비교를 다른 OS 의 CI 에서는 허용 오차로 바꿀지 정해야 합니다. macOS CI 러너를 둘지도 함께 정합니다.
- FFT 구현: pocketfft(BSD-3-Clause)의 계획 구조를 Bpcg.Numerics.Fft 로 옮길지, MathNet.Numerics(MIT) 같은 NuGet 을 쓸지 정해야 합니다. 어느 쪽도 비트 일치는 불가능합니다. 권고는 이식입니다. 라이선스 기록이 필요 없고, Bluestein 선택 규칙이 같아 오차가 가장 작습니다.
- PyMath.Hypot 를 Apple 식(FMA)으로 할지, 플랫폼 libm hypot P/Invoke 로 할지 정해야 합니다. P/Invoke 는 같은 OS 의 numpy 와는 같지만, macOS 에서 만든 golden 과는 다른 OS 에서 다를 수 있습니다.
- python -m bpcg.bake.globe(Globe.Main)를 Bpcg.Cli 의 globe 하위 명령으로 옮길지 정해야 합니다. 지시 원문은 cli.py 의 네 명령만 요구하지만, engine/GLOBE.md 가 이 명령을 굽기 방법으로 안내합니다.
- bake 의 2차원 격자 표현: (double[] 행 우선, int ny, int nx)로 넘길지, bake ①(corridor·heightmap)과 함께 Grid2D<T> 같은 record struct 를 둘지 정해야 합니다.
- 예외 형 대응: Python ValueError(회랑이 except ValueError 로 잡아 디테일을 건너뜀)를 C# 의 어떤 형으로 둘지 정해야 합니다. ZeroDivisionError 처럼 잡히면 안 되는 예외와 구분해야 합니다(공용 결정).
- Detail.DetailWeight 가 HeroVolume.evaluate_grid 를 부르므로, 시험에서 가짜 볼륨을 넣을 인터페이스(예: IHeroVolumeGrid.EvaluateGrid(x, y, up, keys))를 volume 포팅과 함께 둘지 정해야 합니다. Python 시험의 _StubVolume 과 같은 역할입니다.
- PyJson 의 순서 DOM(정수·실수·null 구분)과 Python 형식 지정기 PyFormat(format-spec 'f'·'g'·'e', 짝수 반올림, '-0', 'nan', str.format 틀)을 어느 namespace(Bpcg.IO 또는 Bpcg.Numerics)에 둘지, 공용 장에서 함께 맡을지 정해야 합니다.
- 의심 버그 B1(_sequential 의 좁은 범위 예외)을 포팅에서도 그대로 재현하는 것으로 확정할지 확인이 필요합니다(지시 원문은 고치지 않고 기록).
- 이름 규칙: 함수 detail_settings → Detail.DetailSettings 와 겹치지 않도록 설정 record 를 Detail.Settings 로 두는 것이 괜찮은지, 비공개 클래스 _Resampler 를 Globe 안의 internal 중첩 클래스 Resampler 로 두는 것이 괜찮은지 확인이 필요합니다.

### env

- SDK 설치 방식: 추천안 (가)처럼 시스템 설치를 필수로 하고 스크립트는 안내만 할지, 관리자 권한이 없는 경우를 위해 ~/.dotnet에 설치하는 --dotnet 선택 옵션도 둘지 정해야 합니다.
- 단계 4에서 engine/을 .NET 판으로 바꾸면, GDScript만 만지는 사람도 .NET 판 Godot과 SDK 10이 필요합니다. 편집기가 늘 .NET을 초기화하고 project.godot에 'C#' 특징이 붙기 때문입니다. 팀이 이에 동의하는지와, conventions 11절과 engine/README의 '표준판을 쓰는 이유'를 언제 고칠지 정해야 합니다.
- 지시 원문은 CI의 dotnet test를 단계 4로 미뤘습니다. 그러면 단계 1~3의 C# 커밋을 CI가 검증하지 않습니다. C# 빌드·시험 작업만 단계 1부터 더할지 정해야 합니다.
- TOML 라이브러리로 Tomlyn(TOML 1.1, 순서 보존 문서화, int64 밖 정수를 조용히 감음)을 쓸지, Tomlet(TOML 1.0으로 Python과 같은 판, Dictionary 순서는 보장 문서 없음)을 쓸지, TOML 1.0 일부만 직접 구현할지 정해야 합니다.
- 시험 실행을 MTP 모드(global.json test.runner)만으로 할지, VSTest만 아는 IDE를 위해 xunit.runner.visualstudio와 Microsoft.NET.Test.Sdk도 둘지 정해야 합니다. 팀이 쓰는 IDE(Rider, VS Code C# Dev Kit, Visual Studio)를 확인해야 합니다.
- uv.lock처럼 NuGet lock 파일(packages.lock.json)을 커밋하고 CI에서 --locked-mode를 강제할지 정해야 합니다.
- Godot 4.8 정식판이 나오면 올릴지 정해야 합니다(4.8은 net10.0이 최소이고 지금은 dev.7). conventions 11절의 버전 고정 규칙과 함께 정합니다.
- 실제 내보내기 시험 범위를 정해야 합니다. mono 템플릿 1.2 GB와 macOS 서명 설정이 필요합니다. 로컬 한 번으로 충분한지, CI에도 넣을지 정합니다.
- 공통 관례 문제입니다. Python 클래스 이름과 모듈 정적 클래스 이름이 겹치는 경우(config.py의 class Config) 해결안으로, 모듈 함수를 같은 이름 클래스의 static 메서드로 합치는 안(Bpcg.Core.Config.Load, Config.ParseAssignment)을 제안합니다. 관례 담당과 맞춰야 합니다.

### lib-kdtree

- 지시서는 계산 순서를 바꾸는 최적화와 Math.FusedMultiplyAdd를 금지합니다. 그런데 golden을 만든 scipy 바이너리 자체가 거리 제곱을 fmadd로 계산합니다. KdTree.SqDist 한 함수에만 FusedMultiplyAdd를 쓰는 예외를 허락하시겠습니까? 허락하지 않으면 거리 golden은 1 ulp 허용 오차가 필요합니다(지시서상 허용 오차 넓히기 금지와도 충돌). 실제 호출 위치는 거리를 버리지만, 근접 동점과 경계 판정이 드물게 갈릴 수 있습니다(tiny에서는 0건).
- 지시서는 'prange 반복만 Parallel.For로' 옮기라고 합니다. scipy의 workers=-1 질의(finder, globe, volume)도 행마다 독립인 병렬 반복으로 보고 Parallel.For를 써도 됩니까? 결과는 스레드 수와 무관함을 확인했습니다.
- KD-tree golden(그리고 강 동점에 기대는 회랑 golden)은 macOS arm64와 scipy 1.18.1 바퀴에서 만든 것만 기준으로 삼자는 규칙을 export_golden.py에 강제할까요? 윈도·리눅스 팀원이 golden을 다시 만들면 거리 비트와 동점 승자가 달라질 수 있습니다(추정).
- KdTree 출력 번호형을 numpy intp에 맞춰 long[]으로 두자는 제안(공통 규칙 'intp는 int64')에 동의합니까? 호출하는 쪽 C# 코드는 대부분 int 첨자로 다시 바꿔 씁니다.
- scipy(BSD-3-Clause)와 libc++(Apache-2.0 WITH LLVM-exception)의 알고리즘을 줄 단위로 옮기는 것에 대해 출처와 라이선스 고지를 어디에 둘까요? 파일 머리 주석, 진행 기록, docs/licenses 중 어디에 둘지 정해야 하고, 저장소 라이선스는 아직 미정입니다.
- Python 쪽 의심 지점으로 진행 기록에 적을지 정해 주십시오. volume 강 질의의 결과가 scipy 순회 순서, 컴파일러의 FMA, 표준 라이브러리 구현에 달려 있어서, 같은 시드라도 플랫폼마다 회랑 파일이 마지막 비트에서 다를 수 있습니다. Python은 고치지 않는다는 원칙에 따라 기록만 합니다.
- uv.lock의 scipy를 올리면 바퀴의 컴파일러와 libc++가 바뀌어 indices와 거리 비트가 달라질 수 있습니다. scipy 업그레이드 때 KD-tree golden 재검증을 필수 절차로 둘지 정해 주십시오.

### lib-ndimage

- 지시는 Math.FusedMultiplyAdd를 금지하지만, macOS arm64 scipy 휠과 비트 일치하려면 NdImage.Correlate1DSymmetric에서 FMA가 꼭 필요합니다. 이 한 곳의 예외를 승인하시겠습니까? 아니면 FMA 없는 None 정책과 근거를 단 ≤ 3 ulp 허용 오차를 택하시겠습니까? (tiny에서는 파일과 manifest가 같았지만 함수 단위 비트 일치는 깨집니다.)
- 기준 플랫폼을 macOS arm64(golden 생성과 단계 3 비교)로 고정하시겠습니까? 그렇다면 C# 기본 정책을 MacArm64로 두어 Godot 실행 플랫폼과 무관하게 Mac Python과 같은 값을 내게 할지, 아니면 실행 플랫폼의 Python을 흉내 내게 할지 정해 주십시오.
- Apple clang의 -ffp-contract=on 때문에 macOS arm64의 numpy·scipy·scikit-image 컴파일 코드 전반에서 a*b+c가 FMA로 축약될 수 있습니다(np.interp로 확인). 공통 장에 '라이브러리 대체마다 디스어셈블로 FMA 정책을 확인한다'는 규칙을 더하고 담당을 정해 주십시오(cKDTree, find_peaks, RegularGridInterpolator, brentq, marching cubes).
- macOS가 아닌 플랫폼에서 무게까지 비트 일치가 필요합니까? 그렇다면 플랫폼 libm에 기대는 Math.Exp 대신 CORE-MATH cr_exp(MIT)를 무게 계산에 옮겨 쓰는 안이 있습니다.
- golden npz를 np.savez_compressed(deflate)로 써도 Bpcg.IO.Npz가 읽을 수 있게 할지 정해 주십시오. 커밋 상한이 1 MB라 이 항목의 golden(약 0.6~0.9 MB)에 영향이 있습니다.
- ScipyFmaProfile을 공개 열거형과 선택 인자로 둘지, internal로 숨기고 시험에서만 InternalsVisibleTo로 바꿀지 정해 주십시오. 제안 이름(NdImage, NdBoundaryMode, ScipyFmaProfile, GaussianFilter2D)에도 이견이 있으면 알려 주십시오.

### lib-signal-interp-brent

- 지시 원문은 Math.FusedMultiplyAdd를 금지하지만 기준 Python(macOS arm64 scipy 휠)은 보간 3곳(길이 1 가지 1곳 더)과 brentq 3곳에서 FMA로 계산합니다. WheelFma 한 곳에 모은 예외를 허용할까요, 아니면 FMA 없이 옮기고 이 두 대체만 ulp 허용 대조로 낮출까요?
- golden 자료를 macOS arm64에서만 만들도록 고정할까요? 그러면 Windows x64 팀원은 golden을 다시 만들 수 없고 C# 시험만 돌립니다. x86-64 휠에 축약이 없는지 Windows 팀원이 dumpbin 등으로 확인해 둘까요?
- budyko_runoff의 np.tanh(numpy 자체 NEON SIMD 구현) 호환 함수를 공통 장에서 만들까요? climate 필드와 precip_for_runoff 근의 비트 일치가 여기에 달려 있습니다.
- export_golden.py가 scipy 객체를 기록용 래퍼로 바꿔 끼워 실제 입력을 붙잡는 방식이 'src의 함수를 부르기만 한다'는 지시에 맞나요?
- scipy 예외(ValueError, RuntimeError)를 C#에서 어떤 예외 형으로 옮기고, 메시지를 한국어로 바꿀지 scipy 영어 글을 둘지 공통 장에서 정해 주세요.
- bimodality의 np.histogram(명시 경계, weights) 경로(블록별 불안정 argsort와 cumsum)를 어느 항목이 맡을지 정해 주세요. find_peaks 입력의 비트 일치가 여기에 달려 있습니다.

### lib-mesh-glb

- FMA 예외를 승인할지 정해 주십시오. 대상은 MC test_face·test_internal의 식과 꼭짓점 법선 누적에 Math.FusedMultiplyAdd를 쓰는 일입니다. 거절하면 드문 모호 정육면체에서 MC 위상이 갈릴 수 있고, 법선 float32 성분의 0.5~4%가 1 ulp 다를 수 있어 법선 허용 오차가 필요합니다.
- golden을 만드는 기계를 macOS arm64(이 기계)로 고정할지 정해 주십시오. Python 기준 자체가 플랫폼(휠의 FMA, BLAS 합 순서, arccos)마다 다를 수 있습니다. CI가 Linux나 Windows라면 C#은 FMA가 명시돼 MC는 같지만, Math.Acos 차이 때문에 법선이 허용 오차 경로로 갈 수 있습니다.
- C# MC API의 범위를 정해 주십시오. bpcg가 쓰는 인자(level, spacing, allow_degenerate 두 경로)만 둘지, step_size·mask·classic·normals·values까지 옮길지입니다.
- Bpcg.Bake.Mesh(모듈 이름 규칙)는 단계 4에서 using Godot;과 함께 쓰면 Godot.Mesh와 이름이 겹칩니다(CS0104). 엔진 코드에서 별칭으로 피할지, 규칙 예외로 이름을 바꿀지 정해 주십시오.
- 부피 질의 기록·재생 golden을 위해 HeroVolume 질의를 인터페이스로 받게 할지 정해 주십시오(volume 담당과 합의 필요). golden 스크립트가 monkeypatch로 호출을 기록하는 방식이 'src/bpcg 함수를 부르기만' 규칙에 맞는지도 확인이 필요합니다.
- 라이브러리 대체 golden(손으로 만든 MC 볼륨, FMA 갈림 볼륨, trimesh 무작위 메시)에서 export_golden.py가 skimage·trimesh를 직접 불러도 되는지 정해 주십시오.
- 조각·띠 반복을 순차로 둘지 정해 주십시오(스펙은 prange만 병렬화). 나중에 조각별 결과를 순서대로 잇는 병렬화를 허용할지도 함께 정해 주십시오.
- uv.lock의 scikit-image·trimesh·scipy 판이 바뀌면 golden과 C# 포트를 다시 맞춰야 합니다. 판을 고정할지, 판을 바꿀 때 어떤 절차를 따를지 정해 주십시오.
- 조각 경계 꼭짓점이 합쳐지지 않는 Python 의심 버그를 포팅 뒤 Python과 C#에서 함께 고칠 계획이 있는지 정해 주십시오. 지금은 기록만 합니다.

### io-formats

- NaN 을 쓸 때 양의 quiet NaN 으로 정규화해도 됩니까? arm64 Python 출력과는 같아지지만, x86 Python 의 연산 NaN 은 부호 비트가 서 있어 바이트가 다릅니다.
- TOML 에 Tomlyn(TOML 1.1 상위 집합, BSD-2-Clause)을 써도 됩니까? 아니면 tomllib 과 똑같이 TOML 1.0 만 받는 작은 파서를 직접 만들까요? 차이는 --set 값의 경계 사례에서만 드러납니다.
- PNG 에 tEXt Software 를 빼도 됩니까, 아니면 'bpcg 0.1.0 (C#)' 처럼 적을까요? pHYs(100 dpi)는 유지할까요?
- golden 을 Windows 에서 만들 수도 있습니까? 그렇다면 export_golden.py 가 JSON 을 LF 로 다시 쓰고 npz 비교를 항목 단위로만 하도록 해야 합니다. 아니면 golden 은 macOS·Linux 에서만 만들기로 할까요?
- Config·Bundle 처럼 모듈과 클래스 이름이 같은 경우, 같은 이름의 일반 클래스에 정적 멤버를 두는 방식에 동의합니까? 다른 안은 정적 클래스 이름을 ConfigModule·BundleModule 로 하는 것입니다.
- C# 이 npy·npz 도 '.part' 를 거쳐 쓰도록 바꿔도 됩니까? 출력 바이트는 같고 중단에 강해지지만, Python 동작과는 다릅니다.
- 진행 기록의 'Python 쪽 의심 버그' 에 세 가지를 적고 고치지 않는 것으로 할까요? (1) write_json 이 Windows 에서 CRLF 로 씀, (2) Config.digest 의 allow_nan 기본값과 manifest 의 null 변환이 어긋나 nan·inf 설정의 digest 가 왕복에서 바뀜, (3) np.save·np.savez 가 임시 파일 없이 씀.
- PyJson 정수에 BigInteger 가 필요합니까? Python 은 2^63 이상 시드도 받지만 Tomlyn 과 long 은 받지 못합니다. C# 은 int64 범위 밖 시드를 거부해도 됩니까?
- TOML 래퍼 클래스를 Bpcg.IO.Toml 로 하면 Tomlyn 의 형 이름과 겹칠 수 있습니다. Bpcg.IO.TomlConfig 처럼 다른 이름을 쓸까요?
- System.IO.Hashing(MIT) 의 Crc32 를 쓸까요, 아니면 직접 쓴 표 방식 CRC-32 를 쓸까요?

### formats-e2e

- glb asset.generator를 trimesh 문자열 그대로 둘까요(바이트까지 같게 만들 수 있음), bpcg를 밝히는 문자열로 바꿀까요(정직하지만 asset.generator와 file_meta.caves.bytes를 가려야 함)?
- PNG에 tEXt Software 청크를 쓰지 않을까요, bpcg 표지를 쓸까요? 대조는 어느 쪽이든 픽셀만 봅니다.
- 출력에 만든 구현(python·csharp)을 적는 키를 새로 둘까요? 형식을 같게 유지하려면 단계 2·3에서는 두지 않기를 권합니다.
- cli.py를 라이브러리의 Runs.cs와 Bpcg.Cli/Program.cs로 나누는 것을 '모듈 하나 = 파일 하나' 규칙의 예외로 받아들일 수 있나요? 제안된 규칙의 cli.py → csharp/Bpcg.Cli/와는 다릅니다.
- studio가 나중에 C# 콘솔을 부를 계획이 있나요? 있으면 progress.py의 무늬 줄을 계약으로 고정하고, C# 콘솔의 인자와 문구를 Python과 같게 묶어 둡니다.
- 단계 3 시간 표에 laptop 프로필(Python 62 s, 출력 1.1 GB)도 넣을까요? 넣는다면 laptop의 정확도 대조는 보고만 할까요(히어로 고정 칸이 49만 개라 흐름 방향이 갈리기 쉬움)?
- Windows에서 C#은 LF, Python write_json은 CRLF를 쓰는 차이를 받아들일까요?
- 오류 문구에 쓰는 difflib.get_close_matches와 argparse의 고유 접두어 줄임까지 그대로 옮길까요?
- 끝에서 끝 대조 도구를 Python(csharp/golden/compare_e2e.py)으로 두는 데 동의하나요? C# xUnit 끝에서 끝 시험도 함께 둘까요?
- C# 실행 파일 이름을 uv가 PATH에 까는 Python bpcg와 겹치지 않게 할까요(예: bpcg-cs)? usage의 prog는 bpcg로 둘까요?
- 제안된 규칙에는 Bpcg.IO.PyJson이 쓰기만 적혀 있습니다. 읽기(정수·실수 구분, 키 순서)도 같은 모듈에 두면 될까요?
- float32 ulp 1 이하 판정에서, 0 근처의 뺄셈 값처럼 상대 오차가 커지는 필드에 근거를 단 절대 하한을 둘까요, 넘는 칸을 진행 기록에 적는 것으로 끝낼까요?
- bake --out을 다른 폴더로 주면 지구본이 히어로의 부모 폴더에 구워지고 지난 corridor manifest를 읽습니다. 이 동작을 그대로 옮기고 Python 의심 동작 목록에 적으면 될까요?

### rules

- 포팅 자체와 단계 4를 승인하면서 설계도 8장('파이썬에서 구워 Godot 은 보여 주기만')과 결정 기록 371행('SDF를 파이썬과 엔진에 두 번 만드는 구조는 9주에 안 끝난다')을 누가 고칠까요? 원본은 Claude 문서입니다.
- 단계 4(engine/ 의 .NET 전환)를 11/11 꼭 할 일 동결 전에 끝낼까요, v1.0(12/4) 뒤로 미룰까요? 11/18 뒤에는 '버그와 화면만' 고친다는 규칙이 있습니다.
- .NET 점검(doctor·setup --dotnet)과 dotnet CI 작업을 지시와 달리 단계 1로 앞당기는 데 동의하나요?
- 'Python 코드는 고치지 않는다'의 범위를 src/bpcg 의 생성 코드(studio 제외)로 좁히고 tools/doctor.py, tests/test_engine_*.py, src/bpcg/studio/godot.py 의 도구 수정은 허용할까요?
- PR 단위: 단계·묶음별 PR 로 main 에 합칠까요, feat/csharp-port 를 한 번에 합칠까요? 1:1 포팅 PR 의 400줄 예외와 squash 를 그대로 둘지도 정해야 합니다.
- 커밋 범위를 cs-<묶음> 으로 할까요, csharp 하나로 할까요, Python 과 같은 묶음 이름(hydro 등)을 그대로 쓸까요?
- C# 의 지역 변수·매개변수에 Python 이름(snake_case, Q·k_s 같은 수식 기호)을 허용할까요? dataclass 필드도 Python 철자로 둘지(PascalCase 대신) 함께 정해야 합니다.
- 커밋 golden 의 위치(csharp/golden/data/), 합계 상한(제안 5 MB), '1 MB' 의 정의(1,000,000 바이트로 제안), 그리고 기준을 운영체제마다 다시 만들지 개발 맥에서 만든 것으로 고정할지 정해 주세요.
- .NET SDK 를 setup 스크립트가 직접 깔지(.tools/dotnet + DOTNET_ROOT) 시스템 설치 안내만 할지 정해 주세요. 직접 깔면 Godot 편집기를 Finder·탐색기로 열 때 SDK 를 못 찾습니다.
- 일반 Windows 10 을 쓰는 팀원이 있나요? .NET 10 공식 지원 목록에는 Windows 11 과 Windows 10 의 (E)·(IoT) 판만 있습니다.
- Godot 어셈블리 이름을 'BpcgEngine' 으로 고정할까요? 비우면 'B-PCG.csproj' 와 'B-PCG.dll' 이 생깁니다.
- C# 가 쓰는 JSON 의 줄바꿈을 Python 처럼 운영체제 줄바꿈(Windows CRLF)으로 할까요, 늘 LF 로 할까요? 단계 3의 바이트 비교 방식이 달라집니다.
- manifest 에 구현 표시(예: 'implementation': 'csharp')를 더할까요? 출처 기록(6절)에는 좋지만 Python 묶음과 형식이 달라집니다.
- 내보낸 게임에서 설정을 읽기 위해 configs/**/*.toml 을 Bpcg.dll 의 EmbeddedResource 로 넣는 방식에 동의하나요?
- 제안 관례에 없는 솔루션 파일은 csharp/Bpcg.slnx(.NET 10 기본 형식)로 하고 engine/ 의 .sln 은 Godot 이 만든 그대로 두려 합니다. 하나의 솔루션으로 묶기를 원하나요?
