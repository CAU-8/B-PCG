# csharp/Bpcg.Engine — Godot 4.7.2 .NET 프로젝트

This folder is the Godot 4.7.2 .NET (C#) project that replaces `engine/`. It generates a planet inside the engine with the `Bpcg` C# library (no Python), bakes the corridor and globe files into `user://runs/`, and shows them in the same corridor and globe scenes as `engine/`, now written in C#.

`engine/`(GDScript, 표준판)을 C# 으로 옮긴 Godot 프로젝트입니다. 처음 화면에서 행성을 만들면 `Bpcg` 라이브러리가 주 스레드 밖에서 행성 → 히어로 → 회랑 굽기 → 지구본을 돌리고, 끝나면 회랑 장면으로 넘어갑니다. Python 은 쓰지 않습니다.

## 한눈에 보기

| 항목 | 정한 것 |
|---|---|
| Godot | 4.7.2 **.NET 판** (`4.7.2.stable.mono.official.ed1daf0bf`). 표준판으로는 열리지 않습니다 |
| .NET | SDK 10 (`global.json`), 게임 어셈블리 `Bpcg.Engine` = net10.0, `../Bpcg` 를 ProjectReference 로 참조 |
| 장면 | `scenes/start.tscn`(처음 화면, 시작 장면) · `scenes/main.tscn`(회랑) · `scenes/globe.tscn`(지구본) |
| 스크립트 | `Scripts/*.cs`. `engine/scripts/*.gd` 와 1:1 (아래 표) + 처음 화면·진행률·경로 |
| 셰이더·표본 | `engine/` 의 `.gdshader`·`.gdshaderinc`, `samples/sample.*` 를 그대로 복사 |
| 생성 결과 | `user://runs/<날짜-시각>-<프로필>-s<시드>/{planet,hero,corridor,globe}` (macOS: `~/Library/Application Support/Godot/app_userdata/B-PCG/runs/`) |
| 설정 | 저장소의 `configs/` 를 그대로 읽음 (프로젝트 폴더에서 위로 찾음, 환경 변수 `BPCG_CONFIGS` 로 바꿈) |
| 검사 | `Tests/smoke.tscn`, `Tests/globe_smoke.tscn`, `Tests/screenshots.tscn` (아래 '검사') |

## 준비

Godot .NET 판은 아직 설치 스크립트가 받지 않습니다. macOS 에서는 아래처럼 받습니다(SHA-512 는 [docs/csharp_port.md](../../docs/csharp_port.md) 1장의 값 `0862c53d…` 와 같아야 함).

```sh
mkdir -p .tools/godot-net && cd .tools/godot-net
curl -fLO https://github.com/godotengine/godot/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_macos.universal.zip
shasum -a 512 Godot_v4.7.2-stable_mono_macos.universal.zip && ditto -x -k Godot_v4.7.2-stable_mono_macos.universal.zip .
```

아래 명령은 저장소 맨 위에서 `GODOT=.tools/godot-net/Godot_mono.app/Contents/MacOS/Godot` 로 둔 것으로 씁니다.

## 여는 법과 돌리는 법

```sh
# 편집기로 열기 (실행 F5 / Cmd+B 를 누르면 편집기가 C# 을 빌드합니다)
$GODOT --path csharp/Bpcg.Engine -e

# 편집기 없이 바로 실행: 먼저 빌드하고 띄웁니다
dotnet build csharp/Bpcg.Engine/Bpcg.Engine.csproj
$GODOT --path csharp/Bpcg.Engine

# 지난 결과나 다른 곳에서 구운 폴더를 바로 보기 (처음 화면을 건너뜀). Python 이 구운 폴더도 됩니다
$GODOT --path csharp/Bpcg.Engine -- --baked-dir=/절대/경로/<실행>/corridor

# 화면 없이 생성만 (자동 실행·시험용). 끝나면 BPCG_GENERATE_OK <실행 폴더> 를 찍고 끝납니다
$GODOT --headless --path csharp/Bpcg.Engine -- --generate --profile=tiny --seed=0 --quit-when-done
```

처음 한 번(또는 장면·셰이더를 바꾼 뒤)은 `--headless --path csharp/Bpcg.Engine --import` 로 가져오기를 해 둡니다.

**처음 화면.** 행성 설정(`configs/planets`), 프로필(`configs/profiles`, 첫 줄 주석이 설명으로 나옴), 시드를 고르고 '행성 만들기'를 누릅니다. 기록 줄과 단계별 진행률([studio/progress.py](../../src/bpcg/studio/progress.py) 의 단계 표를 옮김)이 나오고, 끝나면 회랑 장면이 열립니다. 오른쪽 '지난 결과'에서 예전 실행을 회랑이나 지구본으로 다시 엽니다. tiny 프로필은 이 맥(M4 Pro)에서 약 3 초입니다.

**조작.** `engine/README.md` 의 '조작' 표와 같고, 두 장면 모두 **N** 이 처음 화면으로 돌아가는 키로 더해졌습니다.

**지구본 찾기.** 지구본은 `<굽기 폴더>/globe`, 없으면 실행 폴더의 `<굽기 폴더>/../globe` 에서 읽습니다. 그래서 `bpcg bake --engine`(engine/baked + engine/baked/globe)과 실행 폴더(runs/…/corridor + runs/…/globe)를 둘 다 읽습니다.

## engine/ 과의 대응

| engine/ (GDScript) | 여기 (C#) | 비고 |
|---|---|---|
| `scripts/baked_paths.gd` | `Scripts/BakedPaths.cs` | 명령줄 → 처음 화면이 고른 폴더(`RuntimeDir`) → `res://baked` |
| `scripts/heightmap_loader.gd` | `Scripts/HeightmapLoader.cs` | |
| `scripts/terrain.gd` | `Scripts/HeightmapTerrain.cs` | 내보내기 속성 이름은 PascalCase (`Material`, `SkirtM`) |
| `scripts/player.gd` | `Scripts/Player.cs` | `ground_height_at` Callable → `Func<Vector3, float>` |
| `scripts/hud.gd` | `Scripts/Hud.cs` | 'N 처음 화면' 버튼 |
| `scripts/strata_volume.gd` | `Scripts/StrataVolume.cs` | |
| `scripts/baked_layers.gd` | `Scripts/BakedLayers.cs` | |
| `scripts/main.gd` | `Scripts/Main.cs` | N 키 |
| `scripts/globe_data.gd` | `Scripts/GlobeData.cs` | 사전 대신 `Cell` 레코드, 기본 폴더에 실행 폴더 규칙 |
| `scripts/globe.gd` | `Scripts/GlobeView.cs` | |
| `scripts/globe_camera.gd` | `Scripts/GlobeCamera.cs` | |
| `scripts/globe_hud.gd` | `Scripts/GlobeHud.cs` | 자료 없음 알림이 Python 명령 대신 처음 화면을 안내 |
| `scripts/globe_main.gd` | `Scripts/GlobeMain.cs` | N 키 |
| (스튜디오의 '실행' 탭) | `Scripts/StartMenu.cs`, `StageProgress.cs`, `EnginePaths.cs` | 새로 만듦 |
| — | `Scripts/Files.cs`, `Scripts/Ui.cs` | 파일·JSON 읽기, 글꼴·버튼 도우미 |
| `tests/smoke.gd`, `tests/globe_smoke.gd`, `tests/screenshots.gd` | `Tests/SmokeTest.cs`, `GlobeSmokeTest.cs`, `Screenshots.cs` + 각 `.tscn` | C# 은 `--script` 로 돌리기 어려워 검사 장면으로 돌립니다 |

JSON 은 `Bpcg.IO.PyJson.Loads` 로 읽고, 파일은 res:// 와 운영체제 경로를 모두 받도록 Godot `FileAccess` 로 읽습니다.

## 검사

편집기 설정을 건드리지 않게 HOME 을 임시 폴더로 바꿔 돌립니다. 성공·실패 표시는 GDScript 판과 같습니다.

```sh
cd csharp/Bpcg.Engine && dotnet build Bpcg.Engine.csproj
H=$(mktemp -d)
HOME=$H $GODOT --headless --path . --import
HOME=$H $GODOT --headless --path . -- --generate --profile=tiny --quit-when-done      # BPCG_GENERATE_OK <실행>
HOME=$H $GODOT --headless --path . res://Tests/smoke.tscn -- --baked-dir=<실행>/corridor --expect-baked
HOME=$H $GODOT --headless --path . res://Tests/globe_smoke.tscn -- --baked-dir=<실행>/corridor --expect-globe
HOME=$H $GODOT --path . --resolution 1600x900 res://Tests/screenshots.tscn -- --shots-dir=/절대/경로 --baked-dir=<실행>/corridor
```

`--baked-dir` 를 빈 폴더로 주면 표본 지형만(회랑), '자료 없음' 알림만(지구본) 검사합니다. 화면 캡처는 창을 띄우며, 찍는 동안 키보드·마우스 입력을 받지 않습니다.

2026-10-03 확인: tiny 프로필을 엔진 안에서 만든 결과가 `Bpcg.Cli all --profile tiny` 결과와 파일 바이트까지 같고(manifest 의 걸린 시간만 다름), 위 네 검사가 C#·Python 굽기 결과 모두에서 통과했습니다. 같은 자료로 찍은 화면 13장이 GDScript 판과 화면 가운데 픽셀까지 같습니다(남은 1장은 날아가는 도중을 찍어 프레임 시간에 따라 다름).

## 남은 일

- `scripts/setup.*`·`tools/doctor.py`·CI 가 아직 표준판과 `engine/` 을 봅니다. `tests/test_engine_*.py` 와 스튜디오의 'Godot로 보기'도 그대로입니다.
- 내보낸 게임에는 저장소의 `configs/` 가 없으므로, 설정을 게임에 넣어 `user://` 로 풀어 쓰는 방법을 정해야 합니다(`EnginePaths.cs` 의 TODO).
- 이 프로젝트로 `engine/` 과 Python 을 대체하는 정리(폴더 옮기기, 규칙 문서·README 고치기)는 아직 하지 않았습니다.
