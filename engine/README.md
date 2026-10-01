# engine/ — Godot 4.7.2 프로젝트

This folder is the Godot 4.7.2 (standard, non-.NET) project for B-PCG. It only loads files baked by the Python package (`engine/baked/`) and shows them: a heightmap becomes a mesh plus a collision shape, and the player can walk on it. The axis convention is X = east, Y = up, Z = south, in metres, in a local frame around a corridor reference point. A headless smoke test runs with `uv run pytest -m godot`.

엔진은 지질·물·동굴을 계산하지 않습니다. 파이썬이 구운 파일을 불러와 보여 주고, 그 위를 걷게 합니다.

## 한눈에 보기

| 항목 | 정한 것 |
|---|---|
| Godot | 4.7.2 표준판(.NET 아님), 공식 빌드(단정밀도). 세 사람 모두 같은 버전 |
| 렌더러 | Forward+ (`config/features` 에 `"Forward Plus"`). macOS 는 Metal 로 돕니다 |
| 물리 | Jolt Physics. `project.godot` 에 적어 둡니다 (4.7.2 의 값: `DEFAULT`, `Jolt Physics`, `GodotPhysics3D`, `Dummy`) |
| 좌표 | X = 동, Y = 위, Z = 남. 단위 m. 회랑 기준점을 원점으로 한 지역 좌표 |
| 지형 넘김 | 높이맵 `.bin`(float32) + `.json` → `ArrayMesh`(그리기) + `HeightMapShape3D`(충돌) |
| 메시 넘김 | glb 타일. `NORMAL` 필수, 꼭짓점마다 붙는 값은 `COLOR_0` |
| 카메라 | `far = 12000 m`. 기본값 4000 m 는 6~8 km 회랑 끝까지 보이지 않습니다 |
| 입력 | `move_forward/back/left/right` = W/S/A/D, `jump` = Space (물리 키 위치 기준) |
| 검사 | `uv run pytest -m godot` (Godot 가 없으면 건너뜀) |

## 왜 4.7.2 표준판인가

- **GDScript 로 충분합니다.** 엔진은 구운 파일을 읽어 그리기만 하므로 C# 이 필요 없습니다. .NET 판은 세 사람 모두 .NET SDK 를 따로 깔아야 합니다.
- **버전을 하나로 고정합니다.** 버전이 섞이면 편집기가 `project.godot`, `.tscn`, `.import` 를 계속 고쳐 써서 PR 마다 쓸데없는 변경이 생깁니다. `tests/test_engine_smoke.py` 도 4.7.2 만 받아들입니다.
- **공식 빌드는 단정밀도(float32)입니다.** float32 는 지구 반지름(6,371 km) 근처에서 0.5 m 단위로밖에 위치를 나타내지 못해 화면이 떨립니다. 8 km 안에서는 0.5 mm 단위라 충분합니다. 그래서 파이썬이 회랑 기준점에서 지역 좌표로 바꿔 굽고, 기준점은 manifest 에 적습니다. 배정밀도 빌드는 직접 컴파일해야 해서 쓰지 않습니다.

Godot 실행 파일은 `.tools/godot/` 에 둡니다 (git 에서 빠짐). `scripts/setup.sh --godot` 가 받아 줍니다. 다른 곳에 있으면 환경 변수 `GODOT` 에 경로를 넣습니다.

## 여는 법과 돌리는 법

**편집기로 열기.** 편집기는 내 설정을 써야 하므로 HOME 을 바꾸지 않습니다.

```sh
# macOS
.tools/godot/Godot.app/Contents/MacOS/Godot --path engine -e
# Windows
.tools\godot\Godot_v4.7.2-stable_win64.exe --path engine -e
```

프로젝트 관리자에서 `engine/project.godot` 를 가져와도 됩니다. 시작 장면은 `scenes/main.tscn` 이고, 프로젝트 실행(F5, macOS 는 Cmd+B)으로 돌리면 걸을 수 있습니다. 화면을 누르면 마우스로 둘러보고, Esc 로 마우스를 놓습니다.

**연기 검사(smoke test).** 보통은 pytest 로 돌립니다. 임시 HOME 처리와 Godot 찾기를 대신 해 줍니다.

```sh
uv run pytest -m godot              # 또는 uv run pytest tests/test_engine_smoke.py
```

직접 돌릴 때는 반드시 `--headless` 와 임시 HOME 을 씁니다. HOME 을 그대로 두면 Godot 가 내 편집기 설정(`~/Library/Application Support/Godot/editor_settings-4.7.tres`)을 덮어씁니다. macOS 는 `XDG_*` 변수를 무시하므로 HOME 을 바꿔야 합니다. Windows 는 `APPDATA`, `LOCALAPPDATA` 를 바꿔야 해서 pytest 로 돌리는 편이 안전합니다.

```sh
G=.tools/godot/Godot.app/Contents/MacOS/Godot
HOME=$(mktemp -d) $G --headless --path engine --import                       # 처음 한 번, 스크립트를 더한 뒤
HOME=$(mktemp -d) $G --headless --path engine --script res://tests/smoke.gd   # BPCG_SMOKE_OK 가 찍혀야 통과
```

GDScript 구문 오류나 셰이더 컴파일 오류가 있어도 Godot 는 종료 코드 0 으로 끝납니다. 그래서 `tests/smoke.gd` 는 성공하면 `BPCG_SMOKE_OK` 를 찍고 `quit(0)`, 실패하면 `BPCG_SMOKE_FAIL: <이유>` 를 찍고 `quit(1)` 합니다. 검사하는 것은 다음과 같습니다.

1. `scripts/` 의 모든 `.gd` 가 구문 오류 없이 읽힘
2. 단면 셰이더가 컴파일됨 (headless 의 가짜 렌더러도 셰이더를 컴파일합니다. 실패하면 uniform 목록이 비어 있음)
3. 표본 높이맵: 꼭짓점 수 = width × height, 높이 범위가 `.json` 과 1e-3 m 안에서 같음, 법선이 위를 봄, 삼각형 앞면이 위를 봄, 충돌 모양 크기와 높이 범위가 맞음
4. 시작 장면: 지형 메시·충돌체, 단면 셰이더 재질, 하늘·안개, 해, 카메라 `far ≥ 10000 m`
5. 물리: 위에서 쏜 광선이 높이맵 표본 높이를 0.05 m 안에서 맞힘, 플레이어가 땅에 내려섬

## 폴더

| 경로 | 역할 | git |
|---|---|---|
| `project.godot` | 프로젝트 설정: 이름, 시작 장면, 입력, 물리 엔진, Blender 가져오기 끔 | 올림 |
| `scenes/` | 장면(`.tscn`). `main.tscn` 이 시작 장면 | 올림 |
| `scripts/` | GDScript. `heightmap_loader.gd`(높이맵 읽기), `terrain.gd`(지형 노드), `player.gd`(걷기), `main.gd`(시작 장면) | 올림 |
| `shaders/` | `cross_section.gdshader`: 자르는 면 한쪽을 버리고 높이별 층 무늬로 칠함 (층 무늬는 자리표시, 나중에 지층 3D 텍스처로 바꿈) | 올림 |
| `tests/` | `smoke.gd`: headless 연기 검사 | 올림 |
| `samples/` | 작은 표본. `sample.bin/.json` (129 × 129, 10 m 간격, 65 KB)과 이를 만드는 `make_sample.py` | 올림 |
| `baked/` | 파이썬 굽기 결과. 없을 수 있습니다 | 빠짐 |
| `.godot/` | Godot 캐시. 지워도 `--import` 로 다시 생깁니다 | 빠짐 |

`terrain.gd` 는 먼저 `res://baked/heightmap` 을 찾고, 없으면 `res://samples/sample` 을 씁니다. 그래서 `baked/` 가 없어도 장면이 열립니다. 표본을 다시 만들 때는 저장소 맨 위에서 `uv run python engine/samples/make_sample.py` 를 돌립니다 (난수를 쓰지 않아 같은 파일이 나옵니다).

## 파이썬 → Godot 넘김 형식

### 좌표

Godot 지역 좌표는 X = 동, Y = 위, Z = 남인 오른손 좌표계이고 단위는 m 입니다. Godot 카메라의 앞(-Z)이 북쪽입니다. glTF 도 Y 가 위인 오른손 좌표계라 glb 안의 좌표도 바꾸지 않고 같은 축으로 씁니다.

### 높이맵 (`<stem>.bin` + `<stem>.json`)

파이썬은 `bpcg.bake.heightmap.write_heightmap()` 으로 쓰고, 엔진은 `HeightmapLoader.load_stem()` 으로 읽습니다.

- `.bin`: float32 리틀 엔디언, 머리글 없음, 행 우선. 크기는 정확히 width × height × 4 바이트입니다.
- 배열 `z[row, col]`: 열(col)이 늘면 동쪽(+X), 행(row)이 늘면 남쪽(+Z). 0번 행이 북쪽 끝이라 북쪽이 위인 래스터를 뒤집지 않고 넣습니다.
- 표본 `(row, col)` 의 위치 = `origin + (col × spacing_m, z[row, col], row × spacing_m)`. `origin` 은 북서쪽 모서리 표본을 높이 0 에 놓았을 때의 지역 좌표입니다.
- NaN 이나 무한대는 받지 않습니다. 굽기 전에 채우거나 잘라 냅니다.

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

`width` 는 열 수(X), `height` 는 행 수(Z)이고 고도가 아닙니다. `min`, `max` 는 float32 로 바꾼 뒤의 값이라 `.bin` 과 정확히 같습니다.

엔진에서는 이렇게 씁니다.

- 메시: `PackedVector3Array` 등을 직접 채워 `ArrayMesh.add_surface_from_arrays()` 로 만듭니다. 법선은 중심 차분으로 직접 계산합니다. `SurfaceTool.generate_normals()` 는 느려서 쓰지 않습니다. 이 Mac 에서 잰 값: 100만 꼭짓점(1024 × 1024)에 0.35 초.
- 충돌: `HeightMapShape3D` 에 같은 float 배열을 넣고, 수평으로 `spacing_m` 배 늘여 타일 가운데에 놓습니다 (`shape_transform()`). 100만 표본에 5.5 ms. 정사각형이 아닌 높이맵도 Jolt 에서 광선 오차 0.1 mm 안으로 맞는 것을 확인했습니다.
- 한 장은 1024 × 1024 표본 안팎까지가 알맞습니다. 회랑이 더 크면 타일로 나눠 각 타일의 `origin` 을 다르게 줍니다.
- `.bin` 은 Godot 자원이 아니어서 내보내기(export) 때 빠집니다. 내보내기 설정의 '자원이 아닌 파일 포함' 필터에 `*.bin` 을 넣습니다.

### glb 타일

동굴·절벽처럼 높이맵으로 나타낼 수 없는 모양은 glb(glTF 2.0 바이너리)로 넘깁니다.

- `NORMAL` 을 꼭 넣습니다.
- 꼭짓점마다 붙는 값(지층 번호, 흙 두께 등)은 `COLOR_0` 에 넣습니다. Godot 에서는 메시의 `ARRAY_COLOR`, 셰이더의 `COLOR` 로 읽힙니다.
- 불러오기는 두 가지가 다 됩니다. `res://` 안에 두면 편집기가 가져오며(`.import` 생김), `baked/` 처럼 git 에서 빠지는 곳은 실행 중에 `GLTFDocument.append_from_file()` 로 읽어도 됩니다.

### manifest (지역 좌표 기준점)

여러 타일을 묶는 manifest 형식은 `docs/bundle_format.md` 에서 정합니다 (아직 코드 없음). 엔진 쪽에서 필요한 것은 다음과 같습니다.

- 지역 좌표의 원점이 된 회랑 기준점의 행성 위 위치 (float64 로 적음. 엔진은 표시용으로만 씀)
- 축 약속 (`x_east_y_up_z_south`)과 단위 (m)
- 타일 목록: 높이맵이나 glb 의 경로(stem)와 각 타일의 `origin`
- 만든 bpcg 버전, git 커밋, 설정, 시드 (`docs/conventions.md` 의 기록 규칙)

## 커밋하는 것과 하지 않는 것

- **올림:** `project.godot`, `*.tscn`, `*.gd`, `*.gdshader`, 그리고 Godot 가 만든 `*.uid` 와 `*.import`. `.uid` 가 없으면 사람마다 다른 UID 가 생겨 장면 참조가 깨집니다.
- **올리지 않음:** `.godot/` (캐시), `baked/` (파이썬 결과), `export/`, `*.pck`. 모두 저장소 맨 위 `.gitignore` 에 있습니다.
- 새 스크립트나 셰이더를 더하면 `--import` 를 한 번 돌리거나 편집기로 열어 `.uid` 를 만든 뒤 같이 커밋합니다.

## GDScript 스타일

[Godot 공식 스타일 가이드](https://docs.godotengine.org/en/stable/tutorials/scripting/gdscript/gdscript_styleguide.html)를 따릅니다.

- **이름.** 파일은 `snake_case.gd` / `.tscn` / `.gdshader`. 함수·변수·신호는 `snake_case`, `class_name` 과 노드 이름은 `PascalCase`, 상수는 `CONSTANT_CASE`, 밖에서 쓰지 않는 것은 앞에 `_`.
- **단위.** 파이썬과 같이 이름에 단위를 붙입니다: `spacing_m`, `walk_speed_m_s`, `gravity_m_s2`, `mouse_sensitivity_rad`.
- **들여쓰기.** 탭. 이어지는 줄은 탭 두 개. 한 줄은 100자 안. 함수 사이는 빈 줄 두 줄.
- **타입.** 변수는 `:=` 나 타입을 적고, 함수 매개변수와 반환 타입을 적습니다.
- **순서.** `class_name` → `extends` → `##` 설명 → 신호 → enum → 상수 → `@export` → 변수 → `@onready` → `_init`/`_ready` 등 내장 함수 → 공개 함수 → `_` 함수.
- **주석과 메시지.** `##` 문서 주석, `#` 주석, `print` 메시지는 한국어 합니다체로 씁니다. 검사 표시(`BPCG_SMOKE_OK` 등)만 영어입니다.

## 10/21 런타임 복셀 관문

10/21 점검에서 실행 중 복셀 지형과 파기를 시험합니다 (`docs/roadmap.md`). 쓸 도구는 godot_voxel v1.7x 의 GDExtension 판이고, 지금은 설치하지 않았습니다. 시험할 때는 Godot 4.7 용 GDExtension 판을 `engine/addons/` 아래에 넣고, 다음이 `true` 인지 headless 로 먼저 확인합니다.

```gdscript
ClassDB.class_exists("VoxelLodTerrain")   # 지금은 false
```

안 되면 미리 구운 회랑 메시(glb)와 단면 셰이더로 갑니다. 이 경우에도 위의 높이맵·glb 넘김 형식은 그대로 씁니다.
