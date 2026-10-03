# engine/ — Godot 4.7.2 .NET 프로젝트

This folder is the Godot 4.7.2 .NET (C#) project for B-PCG. It can generate a planet inside the engine with the `Bpcg` C# library (`src/Bpcg`), bake the corridor and globe files into `user://runs/`, and show them: the corridor heightmap, the surrounding 25 m terrain, water surfaces, the water table, cave meshes, a strata cross-section, and the whole planet as a globe. The player can fly through everything (noclip) or walk. Axes: X = east, Y = up, Z = south, in metres, in a local frame around a corridor reference point. Smoke tests run with `uv run pytest -m godot tests/test_engine.py`.

엔진은 C# 입니다. 처음 화면에서 행성을 만들면 `Bpcg` 라이브러리가 주 스레드 밖에서 행성 → 히어로 → 회랑 굽기 → 지구본을 돌리고, 끝나면 회랑 장면으로 넘어갑니다. 콘솔(`uv run bpcg all`)이나 스튜디오가 구운 폴더도 그대로 엽니다. 예전 GDScript 판(표준판)을 2026-10 에 C# 으로 옮겼습니다(아래 '예전 GDScript 판과의 대응').

## 한눈에 보기

| 항목 | 정한 것 |
|---|---|
| Godot | 4.7.2 **.NET 판** (`4.7.2.stable.mono.official.ed1daf0bf`), 공식 빌드(단정밀도). 표준판으로는 열리지 않습니다 |
| .NET | SDK 10 (저장소 맨 위 `global.json`). 게임 어셈블리 `Bpcg.Engine` = net10.0, `../src/Bpcg` 를 ProjectReference 로 참조 (편집기 실행에서도 Bpcg 는 Release 로 빌드) |
| 렌더러 | Forward+ (`config/features` 에 `"Forward Plus"`). macOS 는 Metal 로 돕니다 |
| 물리 | Jolt Physics (`project.godot`) |
| 좌표 | X = 동, Y = 위, Z = 남. 단위 m. 회랑 기준점을 원점으로 한 지역 좌표 |
| 장면 | `scenes/start.tscn`(처음 화면, 시작 장면) · `scenes/main.tscn`(회랑) · `scenes/globe.tscn`(지구본) |
| 스크립트 | `Scripts/*.cs` (아래 '폴더') |
| 지형 넘김 | 높이맵 `.bin`(float32) + `.json` → `ArrayMesh`(그리기) + `HeightMapShape3D`(충돌) |
| 메시 넘김 | glb. `NORMAL` 필수, 꼭짓점마다 붙는 값은 `COLOR_0` |
| 카메라 | `far = 40000 m`. 주변 25 m 지형(한 변 32~40 km) 끝까지 보이게 |
| 생성 결과 | `user://runs/<날짜-시각>-<프로필>-s<시드>/{planet,hero,corridor,globe}` (macOS: `~/Library/Application Support/Godot/app_userdata/B-PCG/runs/`) |
| 설정 | 저장소의 `configs/` 를 그대로 읽음 (프로젝트 폴더에서 위로 찾음, 환경 변수 `BPCG_CONFIGS` 로 바꿈) |
| 검사 | `uv run pytest -m godot tests/test_engine.py` (Godot .NET 판이나 dotnet 이 없으면 건너뜀) |

## 준비

.NET 10 SDK 와 Godot 4.7.2 .NET 판이 필요합니다. Godot 은 설치 스크립트가 SHA-512 를 확인하고 `.tools/godot-net/` 에 받습니다.

```sh
./scripts/setup.sh --godot          # 윈도우: powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -Godot
```

아래 명령은 저장소 맨 위에서 `GODOT=.tools/godot-net/Godot_mono.app/Contents/MacOS/Godot` (맥) 로 둔 것으로 씁니다. 리눅스는 `.tools/godot-net/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64`, 윈도우는 `.tools\godot-net\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe` 입니다. 다른 곳의 Godot .NET 판은 환경 변수 `GODOT_NET` 으로 알려 줍니다.

## 여는 법과 돌리는 법

```sh
# 편집기로 열기 (실행 F5 / Cmd+B 를 누르면 편집기가 C# 을 빌드하고 처음 화면이 뜸)
$GODOT --path engine -e

# 편집기 없이 바로 실행: 먼저 빌드하고 띄웁니다
dotnet build engine/Bpcg.Engine.csproj
$GODOT --path engine

# 구운 폴더를 바로 보기 (처음 화면을 건너뜀). 콘솔·스튜디오가 구운 실행 폴더의 corridor/ 를 줍니다
$GODOT --path engine -- --baked-dir=/절대/경로/<실행>/corridor

# 화면 없이 생성만 (자동 실행·시험용). 끝나면 BPCG_GENERATE_OK <실행 폴더> 를 찍고 끝납니다
$GODOT --headless --path engine -- --generate --profile=tiny --seed=0 --quit-when-done
```

처음 한 번(또는 장면·셰이더를 바꾼 뒤)은 `--headless --path engine --import` 로 가져오기를 해 둡니다. `engine/baked/` 에 큰 동굴 메시가 있으면 처음 가져오기에 1분 안팎 걸립니다.

**처음 화면.** 행성 설정(`configs/planets`), 프로필(`configs/profiles`, 첫 줄 주석이 설명으로 나옴), 시드를 고르고 '행성 만들기'를 누릅니다. 기록 줄과 단계별 진행률(스튜디오의 단계 표를 옮긴 `StageProgress`)이 나오고, 끝나면 회랑 장면이 열립니다. 오른쪽 '지난 결과'에서 예전 실행을 회랑이나 지구본으로 다시 엽니다. tiny 프로필은 개발 맥(M4 Pro)에서 약 3 초입니다.

**조작.** 시작은 노클립(날기)입니다. 화면 왼쪽 아래에 같은 표가 나옵니다(H 로 숨김).

| 키 | 하는 일 |
|---|---|
| V | 날기(노클립) ↔ 걷기. 날기는 땅과 동굴 벽을 뚫고 지나갑니다. 땅속에서 걷기로 바꾸면 지면 위로 올라옵니다 |
| W A S D | 이동 (날기는 바라보는 방향, 걷기는 수평) |
| Space · E / Q · Ctrl | 위 / 아래 (걷기에서 Space 는 뛰기) |
| Shift, 마우스 휠 | 5 배 빠르게, 나는 속력 1.25 배씩 바꾸기 (1 ~ 3000 m/s) |
| 화면 클릭 / Esc | 마우스로 둘러보기 / 마우스 놓기 (패널 버튼을 누를 때) |
| 1 ~ 8 | 레이어 켜기·끄기 (순서는 오른쪽 패널, 도움말 줄에 지금 번호가 나옴). 보통 8 이 프랙탈 디테일(보기용) |
| Shift + 1 ~ 6 | 그 레이어만 보기 (나머지 숨김) |
| 0 | 모두 보기 (지하수면·단면은 끈 처음 상태) |
| X | 지층 단면 켜기·끄기. 눈앞 15 m 에 수직으로 선 면을 놓고, 그 면과 눈 사이의 지형·동굴·물을 버립니다 |
| [ / ] | 단면을 당기기 / 밀기 (10 m/s, Shift 면 50 m/s) |
| T / Shift + T | 다음 / 이전 동굴 입구 앞으로 옮겨 입구 안쪽을 보기 |
| G | 지형 색: 자연색 ↔ 지질도(흙 아래 10 m 안의 첫 기반암) |
| F | 손전등 (동굴 안에서) |
| Tab / H | 레이어 패널 / 도움말 숨기기 |
| M | 지구본 장면(행성 전체)으로 가기. 지구본에서 M 을 누르면 돌아옴 |
| N | 처음 화면(새 행성 만들기·지난 결과)으로 가기. 지구본에서도 같음 |

지구본 장면의 조작(끌어서 돌리기, 휠, Space 자동 회전, F 히어로로, 1~9 레이어, R·K·L·G, [ ], 클릭 고정)은 [GLOBE.md](GLOBE.md) 에 있습니다.

**레이어.** 오른쪽 패널에서 버튼으로도 켜고 끕니다. 줄마다 '만 보기' 버튼이 있습니다.

| 번호 | 레이어 | 파일 | 보이는 것 |
|---|---|---|---|
| 1 | 회랑 지형 (2 m) | `heightmap` | 걷는 지표. 자연색은 재질 부피 맨 위 층(흙 = 풀, 충적층 = 모래빛, 드러난 암반 = 암석 색) |
| 2 | 주변 지형 (25 m) | `surround25` | 히어로 유역 전체. 회랑 자리는 비우고 고운 지형에 맡깁니다(경계에 30 m 치마) |
| 3 | 호수·강 수면 | `water` | 반투명 물 |
| 4 | 지하수면 | `water_table` | 반투명 하늘색 면. 처음 켤 때 메시를 만듭니다 |
| 5 | 동굴 | `caves.glb` | 동굴 벽. 앞면이 동굴 안을 보므로 밖(땅속, 단면)에서는 동굴 속이 들여다보입니다. 벽 색 = 벽 뒤 암석 |
| 6 | 지층 단면 | `strata.u8`, `strata_top` | 자르는 면 위의 재질 부피. 지하수면 아래는 파랗게, 지하수면은 하늘색 선, 10 m 마다 가는 선·50 m 마다 굵은 선, 구운 깊이보다 깊은 곳은 회색 빗금, 동굴은 뚫려 보임 |
| 7 | 동굴 입구 구멍 | `cave_mouth` | 지표가 동굴 빈 곳 안인 곳의 지형을 뚫음 (선택, '만 보기' 대상 아님) |
| 8 | 프랙탈 디테일 | `heightmap_detail`, `cave_mouth_detail` | 켜면 회랑 지형이 50 m 보다 짧은 파장의 거칠기를 더한 지표로 바뀌고, 끄면 기본 `heightmap` 으로 돌아감. 파일이 있을 때만 있고 처음에 켜짐 (선택) |

**프랙탈 디테일(8).** 히어로 격자(25 m)는 50 m 보다 짧은 파장을 그리지 못해서, 2 m 회랑 지표가 50 m 아래에서 실제 산지보다 매끈합니다. 굽기(`configs/planets/*.toml` 의 `[detail]`)가 히어로가 그린 가장 짧은 옥타브의 거칠기를 더 짧은 파장(50 m → 4 m)으로 이어 붙여 `heightmap_detail` 을 쓰고, 그 지표에서 다시 잰 동굴 거리를 `cave_mouth_detail` 로 씁니다. 물가·동굴 입구 둘레·원래 웅덩이·회랑 가장자리에는 넣지 않는 보기용 지표이고 솔버 결과가 아닙니다. 다른 세기로 보려면 `uv run bpcg bake --hero <실행>/hero --set detail.fractal_gain=1.0` 처럼 다시 굽습니다. 엔진은 두 높이맵의 메시·충돌 모양을 한 번씩 만들어 기억해 두므로 그 뒤의 켜고 끄기는 바로 됩니다.

**지구본 찾기.** 지구본은 `<굽기 폴더>/globe`, 없으면 실행 폴더의 `<굽기 폴더>/../globe` 에서 읽습니다. 그래서 `bpcg bake --engine`(engine/baked + engine/baked/globe)과 실행 폴더(runs/…/corridor + runs/…/globe)를 둘 다 읽습니다. 읽는 순서는 명령줄 `--baked-dir` → 처음 화면이 고른 실행 → `res://baked` (`Scripts/BakedPaths.cs`) 입니다.

## 검사

검사는 Python(pytest)이 돌립니다. [tests/test_engine.py](../tests/test_engine.py) 가 C# 빌드, 가져오기, 엔진 안 tiny 생성, 검사 장면 실행을 차례로 하고, 출력의 `BPCG_*` 표시와 굽기 결과 파일을 맞대어 판정합니다.

```sh
uv run pytest -m godot tests/test_engine.py
```

| 검사 | 보는 것 |
|---|---|
| `test_engine_imports_cleanly` | 가져오기가 프로젝트 폴더에 `.uid`·`.import` 만 만듦 |
| `test_engine_without_bake` | 빈 굽기 폴더: 회랑은 표본 지형만, 지구본은 '자료가 없습니다' 알림 |
| `test_engine_generates_and_loads` | 엔진 안 tiny 생성 → 회랑·지구본 검사, 동굴 삼각형·입구 수·필드 수가 굽기 결과와 같음 |
| `test_engine_matches_cli` | 엔진 안 생성 결과 = 콘솔 `all` 결과 (manifest 의 `seconds` 만 뺌) |
| `test_engine_loads_console_bake` | 콘솔로 구운 동굴 회랑과 지구본 약속 대체 자료(`tests/globe_fixture.py`)도 읽음 |
| `test_engine_globe_malformed` | 크기가 틀린 지구본 자료에 '읽지 못했습니다' 알림 |

엔진 안에서 노드를 들여다보는 부분(`Tests/SmokeTest.cs`, `Tests/GlobeSmokeTest.cs`)은 Godot 프로세스 안에서 돌아야 해서 C# 입니다. 손으로 돌릴 때는 HOME 을 임시 폴더로 바꿉니다(편집기 설정과 user:// 를 건드리지 않게).

```sh
cd engine && dotnet build Bpcg.Engine.csproj
H=$(mktemp -d)
HOME=$H $GODOT --headless --path . --import
HOME=$H $GODOT --headless --path . -- --generate --profile=tiny --quit-when-done      # BPCG_GENERATE_OK <실행>
HOME=$H $GODOT --headless --path . res://Tests/smoke.tscn -- --baked-dir=<실행>/corridor --expect-baked
HOME=$H $GODOT --headless --path . res://Tests/globe_smoke.tscn -- --baked-dir=<실행>/corridor --expect-globe
HOME=$H $GODOT --path . --resolution 1600x900 res://Tests/screenshots.tscn -- --shots-dir=/절대/경로 --baked-dir=<실행>/corridor
```

화면 캡처(`Tests/screenshots.tscn`)는 회랑·지구본을 여러 시점·레이어 조합으로 찍어 PNG 로 남깁니다. 창을 띄우며, 찍는 동안 키보드·마우스 입력을 받지 않습니다.

## 폴더

| 경로 | 내용 |
|---|---|
| `project.godot`, `Bpcg.Engine.csproj`, `Bpcg.Engine.sln`, `packages.lock.json` | Godot 프로젝트와 C# 프로젝트 (sln 은 편집기 빌드용) |
| `scenes/` | `start.tscn`, `main.tscn`, `globe.tscn` |
| `Scripts/` | 처음 화면 `StartMenu`·`StageProgress`·`EnginePaths`, 회랑 `Main`·`HeightmapTerrain`·`HeightmapLoader`·`BakedLayers`·`StrataVolume`·`Player`·`Hud`, 지구본 `GlobeMain`·`GlobeView`·`GlobeData`·`GlobeCamera`·`GlobeHud`, 공용 `BakedPaths`·`Files`(파일·JSON)·`Ui` |
| `Tests/` | 검사 장면 `smoke.tscn`·`globe_smoke.tscn`·`screenshots.tscn` 과 그 C# |
| `shaders/` | 단면·지층·동굴·물·지구본·대기 셰이더 (`.gdshader`, `.gdshaderinc`) |
| `samples/` | 굽기 폴더가 없을 때 쓰는 작은 표본 높이맵 (`make_sample.py` 로 만듦) |
| `GLOBE.md` | 지구본 장면과 `globe/` 파일 형식 |
| `baked/` | (git 제외) `bpcg bake --engine`·스튜디오 'Godot로 보기' 가 회랑을 복사해 두는 곳 = `res://baked` |

## 굽기 → Godot 넘김 형식

### 좌표

Godot 지역 좌표는 X = 동, Y = 위, Z = 남인 오른손 좌표계이고 단위는 m 입니다. Godot 카메라의 앞(-Z)이 북쪽입니다. glTF 도 Y 가 위인 오른손 좌표계라 glb 안의 좌표도 바꾸지 않고 같은 축으로 씁니다.

### 높이맵 (`<stem>.bin` + `<stem>.json`)

C# 굽기(`src/Bpcg/Bake/Heightmap.cs`)가 쓰고, 엔진은 `HeightmapLoader.LoadStem()` 으로 읽습니다.

- `.bin`: float32 리틀 엔디언, 머리글 없음, 행 우선. 크기는 정확히 width × height × 4 바이트입니다.
- 배열 `z[row, col]`: 열(col)이 늘면 동쪽(+X), 행(row)이 늘면 남쪽(+Z). 0번 행이 북쪽 끝입니다.
- 표본 `(row, col)` 의 위치 = `origin + (col × spacing_m, z[row, col], row × spacing_m)`. `origin` 은 북서쪽 모서리 표본을 높이 0 에 놓았을 때의 지역 좌표입니다.
- NaN 이나 무한대는 받지 않습니다.

```json
{
  "format": "float32_le",
  "layout": "row_major",
  "axes": "x_east_y_up_z_south",
  "width": 129,
  "height": 129,
  "spacing_m": 10.0,
  "origin": [-640.0, 0.0, -640.0],
  "min": 5.009469032287598,
  "max": 140.1663055419922,
  "bpcg_version": "0.1.0"
}
```

`width` 는 열 수(X), `height` 는 행 수(Z)이고 고도가 아닙니다. `min`, `max` 는 float32 로 바꾼 뒤의 값이라 `.bin` 과 정확히 같습니다. 메시는 배열을 직접 채워 `ArrayMesh.AddSurfaceFromArrays()` 로 만들고(법선은 중심 차분), 충돌은 `HeightMapShape3D` 에 같은 float 배열을 넣어 수평으로 `spacing_m` 배 늘입니다.

### 회랑 묶음

`bpcg bake`(`--engine` 이면 `engine/baked/` 에도)와 처음 화면의 생성이 쓰는 파일과 엔진의 쓰임입니다. 형식은 `docs/pipeline.md` 11장.

| 파일 | 엔진의 쓰임 |
|---|---|
| `manifest.json` | 회랑 사각형(`corridor.rect_engine_m`), 높이 기준(`frame.y_offset_m`), 파일 목록, 동굴 삼각형 수 |
| `heightmap`, `surround25` | 지형 메시·충돌 (`HeightmapTerrain`) |
| `heightmap_detail`, `cave_mouth_detail` | (있으면) 프랙탈 디테일 레이어. `heightmap`·`cave_mouth` 와 같은 격자 |
| `water` | 물이 있는 표본(−10000 보다 큼)이 닿는 칸만 사각형으로 |
| `water_table` | 지하수면 메시(처음 켤 때)와 단면의 지하수면 선 |
| `strata.u8` + `strata.json` + `strata_top` | `R8` 3D 텍스처(가로 = 열, 세로 = 층, 깊이 = 행)와 윗면 `RF` 텍스처. 한 변이 2048 을 넘으면 쓰지 않습니다 |
| `cave_mouth` | `RF` 텍스처. 음수인 곳의 지형을 뚫음 |
| `caves.glb` | 편집기가 가져왔으면 `GD.Load()`, 아니면 실행 중 `GltfDocument` |
| `entrances.json` | 입구로 옮겨 가기 (T) |
| `globe/` | 지구본 장면의 자료 (`globe.json` + 필드 `.bin`) ([GLOBE.md](GLOBE.md)) |

### glb

동굴처럼 높이맵으로 나타낼 수 없는 모양은 glb(glTF 2.0 바이너리)로 넘깁니다. `NORMAL` 을 꼭 넣고, 꼭짓점마다 붙는 값(재질 번호 등)은 `COLOR_0` 에 넣습니다. `res://` 안에 두면 편집기가 가져오며(`.import` 생김), git 에서 빠지는 곳은 실행 중에 `GltfDocument.AppendFromFile()` 로 읽습니다.

## 커밋하는 것과 하지 않는 것

- **올림:** `project.godot`, `*.tscn`, `*.cs`, `*.gdshader`, `Bpcg.Engine.csproj`·`.sln`·`packages.lock.json`, 그리고 Godot 가 만든 `*.uid` 와 `*.import`. `.uid` 가 없으면 사람마다 다른 UID 가 생겨 장면 참조가 깨집니다.
- **올리지 않음:** `.godot/` (캐시, C# 빌드 출력 `.godot/mono/temp` 포함), `baked/`, `export/`, `*.pck`. 모두 저장소 맨 위 `.gitignore` 에 있습니다.
- 새 스크립트나 셰이더를 더하면 `--import` 를 한 번 돌리거나 편집기로 열어 `.uid` 를 만든 뒤 같이 커밋합니다.

## C# 스타일

저장소의 `.editorconfig` 와 `Directory.Build.props`(경고는 오류, 빌드 중 코드 스타일 검사)를 따르고, `dotnet format Bpcg.slnx --verify-no-changes` 가 통과해야 합니다. Godot 노드 클래스는 `partial` 이고 파일 이름과 클래스 이름이 같아야 합니다. 이름에 단위를 붙이고(`SpacingM`, `WalkSpeedMS`), 주석과 화면 글은 한국어 합니다체로 씁니다. 검사 표시(`BPCG_SMOKE_OK` 등)만 영어입니다. 계산 코드는 엔진에 두지 않고 `src/Bpcg` 에 둡니다(엔진은 Godot 자료형, Bpcg 는 쓰지 않음).

## 예전 GDScript 판과의 대응

GDScript 판(표준판, 2026-10 까지)은 git 기록의 `engine/scripts/*.gd` 에 있습니다.

| 예전 (GDScript) | 지금 (C#) | 비고 |
|---|---|---|
| `scripts/baked_paths.gd` | `Scripts/BakedPaths.cs` | 명령줄 → 처음 화면이 고른 폴더(`RuntimeDir`) → `res://baked` |
| `scripts/heightmap_loader.gd`, `terrain.gd` | `Scripts/HeightmapLoader.cs`, `HeightmapTerrain.cs` | 내보내기 속성 이름은 PascalCase (`Material`, `SkirtM`) |
| `scripts/player.gd`, `hud.gd`, `main.gd` | `Scripts/Player.cs`, `Hud.cs`, `Main.cs` | N 키와 '처음 화면' 버튼을 더함 |
| `scripts/strata_volume.gd`, `baked_layers.gd` | `Scripts/StrataVolume.cs`, `BakedLayers.cs` | |
| `scripts/globe_*.gd`, `globe.gd` | `Scripts/GlobeData.cs`, `GlobeView.cs`, `GlobeCamera.cs`, `GlobeHud.cs`, `GlobeMain.cs` | 자료 없음 알림이 처음 화면을 안내 |
| (스튜디오의 '실행' 탭) | `Scripts/StartMenu.cs`, `StageProgress.cs`, `EnginePaths.cs` | 새로 만듦 |
| `tests/smoke.gd`, `globe_smoke.gd`, `screenshots.gd` | `Tests/SmokeTest.cs`, `GlobeSmokeTest.cs`, `Screenshots.cs` + `.tscn` | C# 은 `--script` 로 돌리기 어려워 검사 장면으로 돌림 |

2026-10-03 확인: 같은 자료로 찍은 화면 14장 가운데 13장이 GDScript 판과 화면 가운데 픽셀까지 같았습니다(남은 1장은 날아가는 도중을 찍어 프레임 시간에 따라 다름).

## 남은 일

- 내보낸 게임에는 저장소의 `configs/` 가 없으므로, 설정을 게임에 넣어 `user://` 로 풀어 쓰는 방법을 정해야 합니다(`Scripts/EnginePaths.cs` 의 TODO).
- 10/21 런타임 복셀 관문(godot_voxel GDExtension, `docs/roadmap.md`)은 아직 시험하지 않았습니다. 넣을 때는 `engine/addons/` 아래에 두고 headless 로 `ClassDB.ClassExists("VoxelLodTerrain")` 부터 확인합니다. 안 되면 지금의 높이맵·glb 넘김 형식을 그대로 씁니다.
