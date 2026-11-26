# engine/ — Godot 4.7.2 .NET 프로젝트

This folder is the Godot 4.7.2 .NET (C#) project for B-PCG. It can generate a planet inside the engine with the `Bpcg` C# library (`src/Bpcg`), bake the corridor and globe files into `user://runs/`, and show them: the corridor heightmap, the surrounding 25 m terrain, water surfaces, the water table, cave meshes, a strata cross-section, and the whole planet as a globe. The player can fly through everything (noclip) or walk. Axes: X = east, Y = up, Z = south, in metres, in a local frame around a corridor reference point. Smoke tests run with `uv run pytest -m godot tests/test_engine.py`.

> 생성기가 만든 숫자 지도만으로는 "이 골짜기를 걸으면 어떤 느낌인가"를 알 수 없습니다. 이 폴더는 그 땅을 걸어 보는 Godot(3D 세계를 보여 주는 오픈소스 게임 엔진) 게임 프로젝트입니다. 처음 화면에서 행성을 만들면, 생성기(C# 라이브러리 `src/Bpcg`)가 행성 → 강 유역 → 회랑(히어로 유역 안에서 골라 사람이 걸어 다니는 6 km × 1 km 땅) → 지구본을 차례로 만들고 회랑 장면을 엽니다. 명령줄이나 스튜디오(매개변수를 바꿔 행성을 다시 만들고 결과를 지도로 보는 브라우저 도구)가 미리 구운 폴더도 그대로 엽니다. Godot는 **.NET 판**이어야 합니다. 표준판으로는 C# 코드를 읽지 못해 열리지 않습니다.

## 직접 돌려 보기

### 1. 준비 (처음 한 번)

```bash
./scripts/setup.sh --godot          # 윈도우: powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -Godot
```

설치 스크립트가 Godot 4.7.2 .NET 판을 `.tools/godot-net/` 에 받고, 받은 파일이 공식 파일과 같은지(SHA-512) 확인합니다. .NET 10 SDK도 있어야 합니다.

아래 명령은 저장소 맨 위에서 Godot 실행 파일 경로를 `GODOT` 변수에 담아 둔 상태로 적었습니다.

| 운영체제 | `GODOT` |
|---|---|
| 맥 | `.tools/godot-net/Godot_mono.app/Contents/MacOS/Godot` |
| 리눅스 | `.tools/godot-net/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64` |
| 윈도우 | `.tools\godot-net\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe` |

다른 곳에 있는 Godot .NET 판은 환경 변수 `GODOT_NET` 으로 알려 줍니다.

### 2. 엔진 안에서 행성 만들기

```bash
$GODOT --path engine -e     # 편집기로 열기
```

1. 편집기에서 실행(F5, 맥은 Cmd+B)을 누르면 편집기가 C#을 빌드하고 처음 화면이 뜹니다.
2. 프로필(격자 크기와 해상도를 미리 묶어 둔 설정 이름)을 `tiny`로 두고 '행성 만들기'를 누릅니다.
3. 단계별 진행 막대가 끝까지 차면 회랑 장면이 열립니다. 같은 tiny 생성을 명령줄로 돌리면 맥북 에어 M3(8코어, 16 GB)에서 1.2 s 걸립니다(2026-11-26, 마지막 줄 `[전체] 끝: 1.2 s`). 엔진에서는 위 1번의 C# 빌드와 회랑 장면을 여는 시간이 더 듭니다.

### 3. 명령줄로 구운 결과 보기

```bash
uv run bpcg all --profile tiny
dotnet build engine/Bpcg.Engine.csproj
$GODOT --path engine -- --baked-dir="$(pwd)/out/earth/corridor"
```

처음 화면을 건너뛰고 바로 회랑이 열립니다. `--baked-dir` 에는 실행 폴더 안의 `corridor/` 를 절대 경로로 줍니다. 회랑에서 N을 누르면 처음 화면으로 돌아가고, 거기서 고르거나 새로 만든 실행이 `--baked-dir` 보다 앞섭니다.

걸어 볼 강 유역(히어로 유역)은 생성기가 점수로 자동으로 고릅니다. 원하는 곳에 두려면 첫 줄에 위도와 경도를 넘깁니다. 좌표는 스튜디오의 행성 지도나 `[히어로] 후보:` 기록 줄에서 읽습니다.

```bash
uv run bpcg all --profile tiny --set 'hero.site_lat_lon_deg=[8.39, -0.92]'
```

그 자리가 바다나 호수 위이거나, 물이 빠져나가지 못하는 분지의 바닥이거나, 위도 55° 밖이거나, 해안에 너무 가까우면, 실행이 멈추고 가장 가까운 후보 자리를 `hero.site_lat_lon_deg 를 [위도, 경도] 로 바꾸세요` 꼴로 알려 줍니다.

이미 만든 행성에서 자리만 옮길 때는 행성을 다시 만들지 않습니다. 히어로 유역과 회랑만 다시 만듭니다.

```bash
uv run bpcg hero --from out/earth --set 'hero.site_lat_lon_deg=[8.39, -0.92]'
```

```bash
uv run bpcg bake --hero out/earth/hero
```

| 다른 실행 방법 | 명령 |
|---|---|
| 같은 시드의 노이즈 비교본 열기 | `uv run bpcg noise --profile tiny --seed 0` 뒤 `$GODOT --path engine -- --baked-dir="$(pwd)/out/earth-noise/corridor"`. 처음 화면에서는 '만드는 방법'을 '노이즈 비교본'으로 고릅니다([docs/noise_world.md](../docs/noise_world.md)) |
| 편집기 없이 처음 화면부터 | `dotnet build engine/Bpcg.Engine.csproj` 뒤 `$GODOT --path engine` |
| 화면 없이 생성만 (자동 실행·시험용) | `HOME=$(mktemp -d) $GODOT --headless --path engine -- --generate --profile=tiny --seed=0 --quit-when-done`. 끝나면 `BPCG_GENERATE_OK <실행 폴더>` 를 찍습니다. `--noise` 를 더하면 노이즈 비교본을 만듭니다 |
| 새 파일 가져오기 (처음 한 번, 장면·셰이더를 바꾼 뒤) | `HOME=$(mktemp -d) $GODOT --headless --path engine --import`. 동굴 메시가 100 MB를 넘으면 1분 안팎 걸립니다 |

화면 없이 돌리는 두 명령은 `HOME` 을 임시 폴더로 바꿔서 돌립니다. `--import` 는 편집기를 화면 없이 띄우는 명령이라, `HOME` 을 그대로 두면 내 Godot 편집기 설정 파일을 덮어씁니다([docs/history.md](../docs/history.md)의 2026-10-02 줄). 리눅스에서 `XDG_CONFIG_HOME`·`XDG_DATA_HOME` 을 쓰고 있다면 그 둘도, 윈도우에서는 `APPDATA`·`LOCALAPPDATA` 를 임시 폴더로 바꿉니다(시험이 쓰는 방법: [tests/test_engine.py](../tests/test_engine.py)의 `_isolated_env`).

---

## 조작

시작은 날기(땅을 뚫고 지나감)입니다. 화면 왼쪽 아래에 같은 표가 나옵니다(H로 숨김).

| 키 | 하는 일 |
|---|---|
| V | 날기 ↔ 걷기. 날기는 땅과 동굴 벽을 뚫고 지나갑니다. 걷기는 땅과 동굴 벽에 막히고, 동굴 입구로 들어가 통로를 걷습니다(아래 '동굴 걸어 들어가기'). 바위 속에서 걷기로 바꾸면 지면 위로 올라오고, 동굴 안이면 그 자리의 통로 바닥에 내려섭니다 |
| W A S D | 이동 (날기는 바라보는 방향, 걷기는 수평) |
| Space · E / Q · Ctrl | 위 / 아래 (걷기에서 Space는 뛰기) |
| Shift, 마우스 휠(트랙패드는 두 손가락 스크롤) | 5배 빠르게, 나는 속력을 1.25배씩 바꾸기 (1~3,000 m/s) |
| 화면 클릭 / Esc | 마우스로 둘러보기 / 마우스 놓기 (패널 단추를 누를 때) |
| 1 ~ 8 | 레이어 켜기·끄기 (번호는 오른쪽 패널과 도움말 줄에 나옴) |
| Shift + 1 ~ 6 | 그 레이어만 보기 (나머지 숨김) |
| 0 | 모두 보기 (지하수면·단면은 끈 처음 상태) |
| X | 단면 칼(눈앞의 땅을 잘라 속 지층을 보여 주는 세로 평면) 켜기·끄기. 눈앞 15 m에 세로 면을 세우고, 그 면과 눈 사이의 땅·동굴·물을 지워 땅속 지층을 보여 줍니다 |
| [ / ] | 단면을 당기기 / 밀기 (10 m/s, Shift면 50 m/s) |
| T / Shift + T | 다음 / 이전 동굴 입구 앞으로 옮겨 가 입구 안쪽을 보기. 날기면 산비탈 밖 공중에서 보고, 걷기면 입구 아래 가장자리 바로 밖 땅에 서서 굴 안쪽을 봅니다(W로 걸어 들어감) |
| G | 지형 색: 자연색 ↔ 지질도(흙 아래 10 m 안의 첫 암석) |
| F | 손전등 (동굴 안에서) |
| Tab / H | 레이어 패널 / 도움말 숨기기 |
| M | 지구본 장면(행성 전체)으로. 지구본에서 M을 누르면 돌아옴 |
| N | 처음 화면(새 행성 만들기·지난 결과)으로. 지구본과 회랑을 불러오는 화면에서도 같음 |
| - / = | 글자·패널 크기를 10%씩 줄이기 / 키우기 (60~250%). 고른 크기는 다음 실행에도 씀 |
| P | 왼쪽 위 첫 줄(화면 속도와 그린 삼각형 수) 숨기기·켜기 ('화면 속도와 그리기 비용' 절) |

지구본 장면의 조작(끌어서 돌리기, 휠, Space 자동 회전, F 유역으로, 1~9 레이어 등)은 [GLOBE.md](GLOBE.md)에 있습니다.

## 레이어

오른쪽 패널의 단추로도 켜고 끕니다. 줄마다 '만 보기' 단추가 있습니다.

| 번호 | 레이어 | 파일 | 보이는 모습 |
|---|---|---|---|
| 1 | 회랑 지형 (걷는 땅) | `heightmap` | 걷는 땅. 높이 간격은 프로필의 `corridor.voxel_m` (laptop 2 m, tiny 8 m). 자연색은 땅속 암석 상자의 맨 위 층 색입니다(흙 = 풀색, 강이 쌓은 모래·자갈 = 모래색, 드러난 암석 = 암석 색) |
| 2 | 주변 지형 (유역 전체) | `surround25` | 강 유역 전체. 높이 간격은 `hero.spacing_m` (laptop 25 m, tiny 100 m). 회랑 자리는 비워 2 m 지형이 보이게 하고, 경계에 30 m 치마를 둘러 틈을 가립니다 |
| 3 | 호수·강 수면 | `water` | 반투명한 물 |
| 4 | 지하수면(땅속 빈틈이 물로 가득 차기 시작하는 높이) | `water_table` | 땅속에서 물이 차 있는 높이를 반투명 하늘색 면으로. 처음 켤 때 만듭니다 |
| 5 | 동굴 | `caves.glb` | 동굴 벽. 벽의 앞면이 동굴 안쪽을 보고 있어, 밖(땅속, 단면)에서는 동굴 속이 들여다보입니다. 벽 색은 벽 뒤 암석 색. 걷기용 충돌면이 붙어 있고, 레이어를 숨겨도 충돌면은 남습니다(회랑 지형과 같음) |
| 6 | 지층 단면 | `strata.u8`, `strata_top`, `water_table_lower` | 단면 칼이 자른 면에 그린 땅속 암석. 층진 암석 안의 밝고 어두운 띠는 보기용 층리입니다(같은 암석, 어두운 띠는 재질 번호 + 16). 아래 지하수면 아래는 파랗게, 두 지하수면은 하늘색 선(셰일 같은 막는 층 위에 물이 얹힌 곳은 선이 둘), 10 m마다 가는 선·50 m마다 굵은 선, 구운 깊이보다 깊은 곳은 회색 빗금. 구운 깊이(약 300 m)보다 깊은 지하수면은 보이지 않습니다 |
| 7 | 동굴 입구 구멍 | `cave_mouth` | 땅 표면이 동굴 빈 곳과 겹치는 곳을 뚫어 입구를 보이고, 걷기용 충돌면에도 같은 자리에 구멍을 냅니다. 끄면 그림과 충돌면이 함께 막힙니다('만 보기' 대상 아님) |
| 8 | 프랙탈 디테일 | `heightmap_detail`, `cave_mouth_detail` | 회랑 지형에 50 m보다 짧은 잔 거칠기를 더한 보기용 지형. 파일이 있을 때만 있고 처음에 켜져 있습니다 |

**프랙탈 디테일은 왜 따로 있나.** 강 유역 지도는 칸이 25 m라, 칸 2개(50 m)보다 짧은 굴곡을 그리지 못합니다. 그래서 2 m 회랑 지형이 진짜 산지보다 매끈해 보입니다. 굽기가 유역 지도의 가장 짧은 굴곡을 더 짧은 파장(50 m → 4 m)으로 이어 붙여 `heightmap_detail` 을 씁니다.

- 물가, 동굴 입구 둘레, 원래 웅덩이(사방이 더 높은 땅에 막혀 물이 고이는 자리), 회랑 가장자리에는 더하지 않습니다.
- 보기용이고 솔버(지형 높이를 구하는 계산기) 결과가 아닙니다. 점수표(만든 지형이 물리 법칙과 지구 통계에 맞는지 채점한 표)도 바꾸지 않습니다.
- 다른 세기로 보려면 다시 굽습니다: `uv run bpcg bake --hero <실행>/hero --set detail.fractal_gain=1.0`.
- 회랑에 들어갈 때는 디테일 지형 하나만 만듭니다. 기본 지형은 다 불러온 뒤 다른 스레드에서 미리 계산해 두었다가 처음 끌 때 만들고, 그다음부터는 만들어 둔 두 지형을 바꿔 끼우므로 바로 바뀝니다.

## 동굴 걸어 들어가기

동굴 벽의 그림(메시)만 있으면 동굴에는 날기로만 들어갈 수 있습니다. 걷기는 충돌면(걷는 플레이어가 서고 부딪히는 보이지 않는 면) 위에서만 움직이기 때문입니다. 그래서 동굴 벽에 충돌면을 붙이고, 지형의 충돌면에는 동굴 입구 자리에 구멍을 뚫습니다.

**해 보기.**

1. `V` 로 걷기로 바꿉니다.
2. `T` 를 누릅니다. 다음 입구 굴(산비탈에서 통로까지 수평으로 뚫린 굴)의 아래 가장자리 바로 밖 땅에 서서 굴 안쪽을 봅니다. 지표로 열린 구멍이 없는 입구 굴은 건너뜁니다.
3. `W` 로 걸어 들어갑니다. 굴 안은 어두우니 `F` 로 손전등을 켭니다.
4. 싱크홀(빗물이 땅속으로 들어가는 구멍) 위로 걸어가면 수직굴로 떨어져 아래 통로 바닥에 섭니다. 시험 회랑(아래 '검사'의 동굴 회랑)의 싱크홀은 139.5 m 아래 통로로 이어집니다.

동굴 안에서 `V` 로 날기와 걷기를 오가도 지표로 올라가지 않습니다. 몸 가운데에서 아래로 쏜 광선이 동굴 바닥을 먼저 맞히고, 위와 옆으로 쏜 광선이 동굴 벽의 뒷면을 먼저 맞히지 않으면 동굴 안으로 보기 때문입니다(`CaveCollision.Inside`). 동굴 벽의 앞면은 빈 곳을 보므로, 바위 속에서는 가장 가까운 벽의 뒷면이 먼저 맞습니다. 바위 속에서 걷기로 바꾸면 전처럼 지면 위로 올라옵니다. 재질 부피(`strata.u8`)의 공기 상자로는 가리지 않습니다. 상자가 4 m × 4 m × 2 m 라 반지름 2 m 통로 안의 점이 바위 상자에 들 수 있기 때문입니다.

**어떻게 만드나.**

- **동굴 벽.** `caves.glb` 의 삼각형을 가로·세로 128 m 조각으로 나누고, 조각마다 삼각형 충돌 모양(`ConcavePolygonShape3D`) 하나를 둡니다(`Scripts/CaveCollision.cs`). 물리 엔진은 플레이어에서 먼 조각을 경계 상자만 보고 건너뜁니다. 벽의 앞면이 동굴 안쪽을 보므로 충돌도 앞면만 막습니다. 그래서 동굴 안에서는 바닥과 벽에 막히고, 바위 속에서는 벽 뒤를 지나갑니다.
- **지형의 입구 구멍.** `cave_mouth` 는 지형 표본마다 동굴까지의 거리(m)이고, 음수면 그 지표 점이 동굴 빈 곳 안입니다. 지형 충돌면(`HeightMapShape3D`)은 높이가 NaN인 표본에 닿은 삼각형 6개를 충돌에서 뺍니다(Godot 문서의 구멍 기능). 그래서 `cave_mouth` 가 음수인 표본을 NaN으로 비웁니다.
- **구멍 가장자리 덧판과 마개.** 표본 하나를 비우면 둘레 한 칸(laptop 2 m)까지 함께 빠져, 충돌면의 구멍이 보이는 입구보다 커집니다. 그 가장자리로 떨어지면 동굴 벽 바깥의 바위 속으로 빠집니다. 그래서 빠진 삼각형을 `cave_mouth` 가 0 이 되는 선(삼각형 안에서 꼭짓점 값을 선형으로 이은 선)으로 자릅니다(`HeightmapLoader.BuildHoleSet`). 0 이상인 부분(동굴 밖 땅)은 덧판(앞뒤 양면 `ConcavePolygonShape3D`)으로 다시 붙이고, 0 미만인 부분은 마개로 따로 둡니다. 예를 들어 세 꼭짓점의 값이 (−1, 1, 1) m 인 삼각형은 첫 꼭짓점에서 두 변의 가운데까지가 마개이고, 남은 사다리꼴이 덧판입니다.
- **입구 구멍 끄기.** '동굴 입구 구멍'(7번)을 끄면 마개만 붙여 구멍을 막습니다. 높이맵 충돌 조각은 그대로 두므로 바로 바뀝니다.
- **주변 지형.** 주변 지형(유역 전체)의 충돌면은 회랑 경계 안쪽 한 칸(laptop 25 m, tiny 100 m) 띠에 남아 회랑 지형과 겹칩니다. 두 지형은 회랑 가장자리에서 높이가 다릅니다. 가장자리에서 주변 지형이 회랑 지형보다 가장 많이 높은 곳은 laptop 프로필 시드 0 회랑(25 m 칸)에서 10.0 m, 시험 회랑(tiny, 100 m 칸)에서 24.7 m 입니다. 그래서 겹친 띠가 없으면 회랑 밖으로 걸어 나갈 때 그 높이의 벽에 막힙니다. 다만 동굴이 지나는 주변 지형 삼각형은 회랑 안 부분을 비웁니다(`HeightmapLoader.BuildRectClear`). 그러지 않으면 보이지 않는 주변 지형 면이 싱크홀 속에서 플레이어를 받습니다.
- **회랑 가장자리 벽.** 동굴 메시는 회랑 사각형에서 잘리므로, 회랑 밖으로 이어지는 통로는 끝이 열려 있습니다. 그 끝으로 걸어 나가면 아무 면도 없는 땅속으로 떨어집니다. 그래서 회랑 가장자리를 따라 동굴 최저점 1 m 아래부터 지표 0.5 m 아래까지 보이지 않는 벽을 세웁니다(`CaveCollision.BuildEdgeWall`). 벽은 회랑 안쪽을 보는 앞면만 막아, 지표를 걷는 플레이어는 벽 위를 지나갑니다.
- **낙하 한계.** 걷다가 지형·주변 지형·동굴 가운데 가장 낮은 면보다 100 m 아래로 떨어지면 처음 자리로 돌아옵니다(`Main.FallMarginM`). 동굴 바닥과 유역 출구 쪽의 낮은 땅은 이 한계보다 늘 높으므로, 그 위에 선 플레이어는 되돌려지지 않습니다.

**한계.**

- 45°보다 기운 면은 걸어 오르지 못합니다(`CharacterBody3D` 의 바닥 각도 기본값). 동굴 메시는 2 m 칸으로 뽑아 바닥이 울퉁불퉁해, 통로가 평평해도 이런 면이 끼어 막힐 수 있습니다. laptop 프로필 시드 0 회랑에서는 입구 굴 11곳 가운데 9곳에서 25 m 걸어 들어갔고, 1곳은 회랑 가장자리 바로 안쪽의 50° 면에 막혔고, 1곳은 지표로 열린 구멍이 없었습니다(2026-11-26).
- 지름 1 m 안팎의 작은 입구 구멍(`cave_mouth` 가 가장 작은 표본이 −0.6 m 보다 큰 곳)은 몸 반지름 0.4 m 인 플레이어가 지나가지 못하고 가장자리에 걸립니다.
- 동굴 안의 물은 막지 않습니다. 물에 잠긴 아래층 통로에서도 바닥을 걷습니다.
- tiny 프로필 회랑(8 m 간격)은 가장 가는 통로(반지름 2 m)를 메시로 그리지 못해 걸어 들어갈 수 없습니다. 걸어 보려면 2 m 로 다시 굽습니다: `uv run bpcg bake --hero <실행>/hero --set profile.corridor.voxel_m=2.0`.
- 입구와 이어지지 않는 막힌 동굴 조각은 메시에 없어 충돌면도 없습니다([docs/pipeline.md](../docs/pipeline.md) 11장).

**불러오기 시간과 메모리.** 동굴 걷기에 필요한 일은 회랑 진행 막대의 '동굴 충돌면 만드는 중' 단계에서 합니다. 동굴 충돌체를 만들고, 회랑 가장자리 벽을 세우고, 주변 지형 충돌면에서 동굴 자리를 비웁니다. 아래는 laptop 프로필 시드 0 회랑(6 km × 1 km, 동굴 삼각형 205,495개, 동굴 충돌 조각 72개)을 맥북 에어 M3(8코어, 16 GB)에서 화면 없이 세 번 연 값입니다(2026-11-26, `Tests/load_timing.tscn`, 부하 평균 4~5).

| 잰 값 | 시간 |
|---|---|
| 회랑에 들어가는 시간 전체 | 0.92~1.14 s (가운데값 0.93 s) |
| 그 가운데 '동굴 충돌면 만드는 중' 단계 | 0.18~0.20 s |
| 그 단계에서 가장 긴 한 조각: 동굴 메시의 삼각형을 한 번에 꺼내는 `Mesh.GetFaces()` | 약 0.07 s |

- 삼각형을 충돌 조각별로 나누는 일은 2만 개씩 나눠 프레임 사이에 화면을 그립니다(`CaveCollision.FacesPerStep`). 한 번에 나누면 그 프레임이 약 0.1 s 멈췄습니다.
- `Mesh.GetFaces()` 는 Godot 함수 한 번이라 더 나누지 못합니다. 그래서 이 조각이 불러오는 동안 가장 긴 프레임(약 0.07 s)이 됩니다.
- '동굴 입구 구멍'(7번)을 끄고 켜는 데는 0 ms, 1 ms 가 걸렸습니다(물리 한 걸음 포함, 한 번 잼).
- Godot 가 잡은 메모리(정적 메모리)는 회랑을 한 프레임에 다 불러오던 코드에서 쟀습니다. 동굴 걷기를 더하기 전·후를 세 번씩 번갈아 연 가운데값입니다(같은 날, 동굴 삼각형 205,192개인 굽기). 불러온 직후 536 MB → 585 MB(49 MB 늘어남), 가장 많을 때 1,114 MB → 1,162 MB 입니다.

## 화면 크기

맥 레티나처럼 화면 배율이 2인 화면에서는 창(1152 × 648)과 글자가 절반 크기로 보입니다. 그래서 처음 뜰 때 창과 글자를 화면 배율만큼 키웁니다(`UiScale`).

- 창은 쓸 수 있는 화면의 90%까지 키웁니다. 창 크기를 따로 주었으면 그대로 둡니다.
- 3D 화면은 원래 해상도로 그립니다. 글자와 패널만 커집니다.
- 글자 배율은 화면 배율과 창 크기 배율 가운데 큰 값입니다. 창 크기 배율은 창이 기본 창(1152 × 648)의 몇 배인지입니다(가로·세로 가운데 작은 쪽).
- 그래서 배율 1인 프로젝터에서 1920 × 1080 창을 띄우면 글자를 1.67배로 그립니다. 화면 배율만 보면 1배로 그려, 글자가 창에서 차지하는 몫이 노트북 기본 창(배율 2, 2304 × 1296)의 0.6배로 줄어듭니다.
- 창이 작아지면 패널과 도움말이 겹치지 않게 배율을 줄입니다(2D 화면이 1024 × 600보다 작아지지 않게, 1배보다 작게는 줄이지 않음).
- `-` / `=` 로 더 키우거나 줄이고, 고른 크기는 `user://settings.cfg` 에 남습니다.
- 마우스 둘러보기와 지구본 끌기는 화면 픽셀 기준이라, 배율을 바꿔도 감도가 같습니다.

## 화면 속도와 그리기 비용

회랑 장면 왼쪽 위의 첫 줄은 화면 속도와 그리기 비용입니다(P로 숨김). 예: `화면 속도 60 fps · 삼각형 102만 + 그림자 135만`.

- **화면 속도(fps)** 는 1초에 새로 그린 화면 수입니다. 맥북 화면은 1초에 60번 바뀌므로 60이면 끊김 없이 움직입니다. 수직 동기화(화면이 바뀌는 때에 맞춰 기다리기)를 끄면 60을 넘게도 나오지만, 맥이 창 화면을 받아 가는 사정에 따라 60 근처에서 멈출 때도 있습니다. 그래서 설정끼리는 아래 '한 장' 값으로 견줍니다.
- **GPU 한 장 ms** 는 그래픽 칩이 화면 한 장을 그린 시간입니다. 16.7 ms보다 짧으면 60 fps를 지킬 여유가 있습니다. 맥의 Metal 판 Godot는 이 시간을 재 주지 않아 맥에서는 줄에 나오지 않습니다.
- **삼각형** 은 지난 화면에서 실제로 그린 삼각형 수입니다. **그림자** 는 그림자 지도(해 쪽에서 땅을 한 번 더 그려 어디가 그늘인지 정하는 그림)를 만들려고 다시 그린 삼각형 수입니다.

### 지형을 조각으로 나눠 그리는 까닭

회랑 지형(2 m 간격, 삼각형 300만 개)과 주변 지형(25 m 간격, 327만 개)을 메시 하나씩으로 그리면, Godot는 화면에 조금만 걸쳐도 메시 전체를 그립니다. 그림자 지도는 눈에서 1,000 m 안을 거리별로 네 겹으로 나눠 겹마다 다시 그립니다. 그래서 그림자에만 삼각형 2,520만 개를 그렸고, laptop 회랑이 1초에 약 30장만 그려졌습니다. 지금은 두 가지로 줄여 그립니다(`HeightmapLoader.BuildChunkArrays`, `HeightmapTerrain.ApplySteps`).

1. **조각.** 지형을 64 × 64 칸씩 나눕니다(회랑 47 × 8 = 376조각, 주변 지형 20 × 20 = 400조각). Godot는 화면 밖 조각과 그림자 거리 밖 조각을 건너뜁니다.
2. **거친 메시(LOD, 먼 물체를 삼각형을 줄인 모양으로 그리는 기법).** 조각마다 2·4·8·16·32칸마다 한 줄만 쓰는 거친 삼각형 목록을 미리 만듭니다. 목록마다 그 조각의 실제 높이와 가장 크게 어긋나는 값(m)을 재 두고, 그 어긋남이 화면에서 1 px보다 작아지는 거리부터 거친 목록으로 그립니다.
    - 예: 1920 × 1080 화면에서 1 px은 1 km 앞에서 약 1.4 m입니다. 그래서 어긋남이 1.4 m인 목록은 1 km보다 먼 조각에 씁니다.

헷갈리기 쉬운 점:

- 조각 둘레의 꼭짓점은 거친 목록에서도 모두 씁니다. 그래서 옆 조각이 다른 간격으로 그려져도 경계에 틈이 생기지 않습니다.
- 거친 메시는 실제 지표보다 몇 m 높거나 낮게 그려집니다. 그대로 두면 땅속 층 색을 고를 때 흙 대신 그 아래 암석 색이 칠해집니다. 그래서 지형 셰이더는 층 깊이를 그린 면 대신 높이맵 텍스처의 높이에서 잽니다(`cross_section.gdshader` 의 `surface_y`).
- 회랑 가장자리에 닿은 조각은 4배 먼 거리부터 거친 목록으로 바꿉니다. 가장자리에서 아래로 내린 30 m 벽(치마)의 윗부분이 먼 곳에서 가는 흰 점선으로 드러나지 않게 하기 위해서입니다.
- 거친 메시는 보이는 모양만 바꿉니다. 걷기와 충돌은 원래 높이맵 그대로 계산합니다.
- 먼 곳을 더 거칠게 그려 더 빨리 하려면 프로젝트 설정 `rendering/mesh_lod/lod_change/threshold_pixels`(기본 1 px)를 키웁니다. 2 px이면 어긋남이 2 px보다 작은 거리부터 거친 목록을 씁니다.

### 고치기 전후의 화면 속도 (laptop 시드 0)

laptop 시드 0 회랑(2026-11-26에 지금 코드로 만든, 남북으로 긴 6 km × 1 km 회랑)을 맥북 에어 M3(GPU 10코어), Godot 4.7.2 Metal에서 아래 '그리기 비용 재기' 명령으로 쟀습니다. 그때 부하 평균은 3~5였습니다. '고치기 전'은 지형을 메시 하나로 그리던 코드입니다(다른 스레드 불러오기와 동굴 걷기는 같음). 'fps'는 사람이 보는 화면 속도, '한 장'은 화면에 내보내지 않고 같은 장면을 거듭 그려 잰 한 장의 시간입니다.

| 시점 | 창 | 고치기 전 | 고친 뒤 |
|---|---|---|---|
| 시작 시점 (회랑 가운데 40 m 위에서 북쪽을 봄) | 1920 × 1080 | 41 fps, 한 장 24.1 ms, 삼각형 660만 + 그림자 2,520만 | 79 fps, 한 장 9.3 ms, 삼각형 211만 + 그림자 106만 |
| 회랑 한쪽 끝 눈높이에서 회랑 전체를 따라 봄 | 1920 × 1080 | 43 fps, 한 장 22.6 ms, 660만 + 2,520만 | 86 fps, 한 장 9.4 ms, 258만 + 125만 |
| 하늘(1.6 km 위)에서 회랑과 주변 지형 | 1920 × 1080 | 57 fps, 한 장 17.3 ms, 660만 + 1,309만 | 100 fps, 한 장 8.0 ms, 194만 + 8.1만 |
| 시작 시점 | 2304 × 1296 (이 맥북의 기본 창) | 38 fps, 한 장 25.6 ms | 60 fps, 한 장 11.5 ms |
| 회랑 전체를 따라 봄 | 2304 × 1296 | 40 fps, 한 장 24.0 ms | 78 fps, 한 장 11.7 ms |
| 하늘에서 | 2304 × 1296 | 51 fps, 한 장 19.1 ms | 77 fps, 한 장 10.2 ms |

- 회랑 장면을 여는 시간은 1.3 s에서 1.5 s(1920 × 1080 창), 1.4 s에서 1.4 s(2304 × 1296 창)로 거의 같습니다. 조각과 거친 목록은 회랑 파일을 읽는 다른 스레드에서 조각마다 따로 계산하고(여러 코어), 주 스레드는 프레임마다 조각 몇 개씩 노드로 붙입니다('회랑을 불러오는 동안' 절).
- 그림자를 끄면 시작 시점은 한 장 9.3 ms에서 7.2 ms가 됩니다(1920 × 1080). 그림자 지도 크기, 그림자 겹 수(4 → 2), 그림자 거리(1,000 → 500 m)도 바꿔 봤지만 차이가 흔들림(±2 ms)보다 작아 그대로 둡니다.

### 그리기 비용 재기

```bash
dotnet build engine/Bpcg.Engine.csproj
H=$(mktemp -d)
HOME=$H $GODOT --headless --path engine --import
HOME=$H $GODOT --path engine --disable-vsync --resolution 1920x1080 res://Tests/perf.tscn -- --baked-dir="$(pwd)/out/earth/corridor"
```

창이 떠서 세 시점(시작 시점, 회랑 끝에서 회랑 전체를 따라 본 눈높이, 하늘)마다 그림자·주변 지형을 켜고 끈 네 조합을 잽니다. 줄마다 `[그리기] <시점> · <설정>: … fps, 화면에 내보내지 않고 그리기만 한 장 … ms, 삼각형 … + 그림자 …` 가 나오고, 마지막 줄은 `BPCG_PERF_OK 12` 입니다. laptop 회랑에서 1분 안팎(50~80 s) 걸립니다(맥북 에어 M3). 재는 동안 창을 가리거나 화면을 끄면 Godot가 그리지 않아 `BPCG_PERF_FAIL` 로 끝납니다.

## 회랑을 불러오는 동안

laptop 회랑 폴더는 파일이 약 108 MB입니다. 엔진은 이 파일을 읽고, 지형 메시와 충돌면, 땅속 암석 텍스처를 만들어야 걸어 다닐 수 있습니다. 이 일을 한 프레임에 다 하면 그동안 화면이 멈춥니다. 그래서 회랑 장면은 불러오는 동안 화면 가운데에 진행 막대를 띄우고, 일을 둘로 나눕니다.

1. **다른 스레드에서 읽기.** 파일을 읽고, 지형 메시의 꼭짓점 배열과 충돌면 조각을 계산하고, 동굴 메시(`caves.glb`)를 해석합니다(`Scripts/CorridorFiles.cs`). 화면은 계속 그려집니다.
2. **주 스레드에서 조금씩 만들기.** 지형 메시 조각(64 × 64 칸, '화면 속도와 그리기 비용' 절)이나 충돌 조각 하나씩, 한 프레임에 약 25 ms만큼 만들고 화면을 그립니다(`BakedLayers.Build`, `HeightmapTerrain.ApplySteps`).

진행 막대 아래 글은 지금 하는 일과 단계 번호를 `회랑 지형 메시와 충돌면 만드는 중 (9/13단계) · 1.2 s` 꼴로 보여 줍니다. 기다리지 않으려면 N(처음 화면)이나 M(지구본)을 누릅니다.

**충돌면은 왜 조각으로 나누나.** Godot의 물리 엔진(Jolt)은 정사각 높이맵만 빠른 높이장(height field)으로 만들고, 가로·세로가 다른 높이맵은 삼각형 메시로 바꿉니다. laptop 회랑은 3,001 × 501 표본이라 메시로 바꾸는 데 한 번에 수 초 걸렸습니다. 그래서 충돌면을 한 변이 많아야 257 표본인 정사각 조각으로 나눕니다. 이웃 조각은 경계 표본 한 줄을 같이 써서 틈이 없습니다(`HeightmapLoader.BuildShapeTiles`).

**잰 시간.** laptop 시드 0 회랑(파일 108 MB)을 맥북 에어 M3(8코어, 16 GB)에서 화면 없이 쟀습니다(2026-11-26, `Tests/load_timing.tscn`). 그때 다른 작업이 함께 돌아 부하 평균이 39~113이었습니다. 그래서 고치기 전 코드와 고친 코드를 번갈아 돌려 가운데값을 비교했습니다. 괄호 안은 가장 작은 값~가장 큰 값입니다.

| 잰 값 | 고치기 전 | 고친 뒤 |
|---|---|---|
| 회랑에 들어가는 시간 (8번) | 25.0 s (11.9~36.7 s) | 3.8 s (1.5~7.4 s) |
| 그동안 그린 프레임 | 1개. 그동안 화면이 멈춤 | 284개 (136~456개) |
| 가장 긴 프레임 | 들어가는 시간 전체 | 0.51 s (0.17~1.43 s) |
| 같은 실행에서 두 번째로 들어가는 시간 (3번) | 21.2 s | 2.0 s, 가장 긴 프레임 0.13 s |
| 프랙탈 디테일을 처음 끄는 시간 (8번) | 0.002 s | 0.43 s (0.21~1.29 s) |

- 가장 긴 프레임은 여덟 번 모두 장면을 붙이는 첫 프레임이었습니다. 이 프레임은 첫 진입에서만 길고, 같은 실행에서 두 번째로 들어갈 때(지구본에서 M으로 돌아올 때와 같음)는 8~28 ms입니다.
- 회랑 장면 안에서 잰 만들기 단계의 가장 긴 프레임은 가운데값 0.11 s입니다(콘솔의 `[회랑] 불러오기 끝:` 줄).
- 프랙탈 디테일을 처음 끌 때는 기본 지형을 그때 만들어 잠깐 멈춥니다. 전에는 들어갈 때 두 지형을 다 만들어 두었기 때문에 바로 바뀌었습니다.
- 화면 없이 재면 텍스처와 메시를 그래픽 카드로 올리는 시간이 빠집니다. 2026-11-26에 지금 코드로 만든 laptop 시드 0 회랑은 화면 없이 0.9 s(연기 검사), 창을 띄우면 1.4~1.5 s 걸렸습니다(부하 평균 3~5, `Tests/perf.tscn` 의 `[그리기] 회랑 장면 불러오기 끝` 줄).

**불러오기 문제.** 목차 파일(`manifest.json`)이 없거나, 목차에 있는 파일이 없거나, 파일을 읽지 못하면 멈추지 않습니다. 그 레이어 없이 회랑을 열고, 화면 가운데의 '불러오기 문제' 상자에 줄마다 무엇이 없는지, 받은 경로나 값, 다시 굽는 명령을 적습니다. 같은 줄이 콘솔에 `[회랑] 불러오기 문제:` 로 나옵니다. 상자는 Esc로 마우스를 놓고 '닫기'를 눌러 닫습니다.

## 지구본과 구운 폴더를 찾는 순서

- **회랑 폴더.** 처음 화면이 고른 실행 → 명령줄 `--baked-dir` → `res://baked`(`engine/baked/`) 순서로 찾습니다(`Scripts/BakedPaths.cs`). `--baked-dir` 로 띄우면 처음 한 번만 처음 화면을 건너뜁니다. N으로 처음 화면에 돌아와 다른 실행을 고르거나 새로 만들면 그 실행이 열립니다.
- **지구본.** `<회랑 폴더>/globe` 에서 읽고, 없으면 `<회랑 폴더>/../globe` 에서 읽습니다. 그래서 `bpcg bake --engine` 결과(`engine/baked/globe`)와 실행 폴더(`<실행>/globe`)를 둘 다 엽니다.

---

## 막혔을 때

| 보이는 현상 | 까닭 | 고치는 법 |
|---|---|---|
| 편집기가 C# 스크립트를 읽지 못함 | Godot 표준판으로 열었음 | `.tools/godot-net/` 의 .NET 판(이름에 `mono`)으로 엽니다 |
| 편집기를 처음 열 때 1분 넘게 멈춘 듯함 | 편집기가 100 MB가 넘는 동굴 메시를 가져오는 중 (통로를 물길로 바꾸기 전에 구운 폴더가 `engine/baked/` 에 남아 있을 때). 게임은 이 가져온 사본을 쓰지 않고 파일을 직접 읽습니다 | 기다립니다. 다음부터는 캐시를 씁니다. 새로 구우면 동굴 메시가 1~6 MB라 금방 끝납니다 |
| 회랑 가운데에 '불러오기 문제' 상자가 뜸 | 회랑 폴더의 파일이 없거나 망가짐 | 상자의 줄마다 빠진 파일과 다시 굽는 명령이 있습니다. 그 명령으로 다시 굽거나, N으로 처음 화면에 가서 새로 만듭니다 |
| 지구본에 '자료가 없습니다' | 굽기 폴더 옆에 `globe/` 가 없음 | `bpcg all` 로 다시 만들거나, 처음 화면에서 행성을 만듭니다(`bake --no-globe` 는 지구본을 쓰지 않음) |
| 회랑에 작은 표본 지형만 보임 | 구운 폴더를 찾지 못함 ('불러오기 문제' 상자에 찾은 경로가 나옴) | `--baked-dir` 에 `corridor/` 를 절대 경로로 줍니다 |
| 새 스크립트를 더했더니 장면 참조가 깨짐 | `.uid` 파일이 없음 | `HOME=$(mktemp -d) $GODOT --headless --path engine --import` 를 돌리고 생긴 `.uid` 를 같이 커밋합니다 |
| 걷기로 동굴 입구에 들어가지 못하고 보이지 않는 땅에 섬 | '동굴 입구 구멍'(7번) 레이어가 꺼져 있음 | 7번을 켭니다. 끄면 그림과 충돌면이 함께 막힙니다 |
| T 를 눌렀더니 걷기가 날기로 바뀜 | 설 자리가 있는 입구 굴이 하나도 없음(굴이 지표로 열리지 않았거나, 회랑 간격이 굵어 메시가 굴을 그리지 못함) | tiny 회랑은 2 m 로 다시 굽습니다(위 '동굴 걸어 들어가기'의 한계) |

## 검사

pytest가 C# 빌드, 가져오기, 엔진 안 tiny 생성, 검사 장면 실행을 차례로 하고, 출력의 `BPCG_*` 표시와 굽기 결과 파일을 맞대어 판정합니다([tests/test_engine.py](../tests/test_engine.py)).

```bash
uv run pytest -m godot tests/test_engine.py
```

Godot .NET 판이나 dotnet이 없으면 건너뜁니다.

| 검사 | 확인하는 내용 |
|---|---|
| `test_engine_imports_cleanly` | 가져오기가 프로젝트 폴더에 `.uid`·`.import` 만 만드는지 |
| `test_engine_without_bake` | 빈 굽기 폴더: 회랑은 표본 지형만('불러오기 문제' 상자에 까닭), 지구본은 '자료가 없습니다' 알림 |
| `test_engine_generates_and_loads` | 엔진 안 tiny 생성 → 회랑·지구본 검사. 동굴 삼각형·입구 수·필드 수가 굽기 결과와 같은지. 회랑을 여러 프레임에 나눠 불러오는지, 지형을 한 번만 만드는지, 충돌 조각의 솔기를 넘어 걷는지, `--baked-dir` 로 띄워도 처음 화면으로 돌아오는지. 동굴 메시가 있으면 동굴 충돌면이 메시 삼각형을 모두 담는지, 걷다 떨어지면 처음 자리로 돌아오는지(낙하 한계) |
| `test_engine_shows_load_problems` | 동굴 메시를 망가뜨리고 지하수면 파일을 지운 묶음: 두 문제가 화면 상자에 나오고, 나머지 레이어로 회랑이 열리는지 |
| `test_engine_matches_cli` | 엔진 안에서 만든 결과 = 명령줄 `all` 결과 (목록 파일의 `seconds` 만 뺌) |
| `test_engine_loads_console_bake` | 명령줄로 구운 동굴 회랑(2 m 간격)과 지구본 대체 자료(`tests/globe_fixture.py`)도 읽는지. 걷는 플레이어가 T 로 입구 앞에 서서 굴 안으로 12 m 걸어 들어가 통로 바닥에 서는지, 입구 구멍(입구 굴·싱크홀)마다 그 위에서 떨어져 동굴 바닥에 서는지 |
| `test_engine_globe_malformed` | 크기가 틀린 지구본 자료에 '읽지 못했습니다' 알림 |
| `test_fixture_globe_follows_contract` | 시험용 지구본 대체 자료(`tests/globe_fixture.py`)가 지구본 파일 형식 약속을 지키는지 (Godot 없이 돎) |

엔진 안의 노드를 들여다보는 검사(`Tests/SmokeTest.cs`, `Tests/GlobeSmokeTest.cs`)는 Godot 안에서 돌아야 해서 C#입니다. 손으로 돌릴 때는 HOME을 임시 폴더로 바꿉니다. 그러지 않으면 내 편집기 설정과 `user://` 를 덮어씁니다([docs/history.md](../docs/history.md)의 2026-10-02 줄, Godot 설정 사고).

```bash
cd engine && dotnet build Bpcg.Engine.csproj
H=$(mktemp -d)
HOME=$H $GODOT --headless --path . --import
HOME=$H $GODOT --headless --path . -- --generate --profile=tiny --quit-when-done      # BPCG_GENERATE_OK <실행>
HOME=$H $GODOT --headless --path . res://Tests/smoke.tscn -- --baked-dir=<실행>/corridor --expect-baked
HOME=$H $GODOT --headless --path . res://Tests/globe_smoke.tscn -- --baked-dir=<실행>/corridor --expect-globe
HOME=$H $GODOT --path . --resolution 1600x900 res://Tests/screenshots.tscn -- --shots-dir=/절대/경로 --baked-dir=<실행>/corridor
HOME=$H $GODOT --headless --path . res://Tests/load_timing.tscn -- --baked-dir=<실행>/corridor       # BPCG_LOAD_TIMING 1번째 진입 … ms
HOME=$H $GODOT --path . --disable-vsync --resolution 1920x1080 res://Tests/perf.tscn -- --baked-dir=<실행>/corridor
```

화면 캡처(`Tests/screenshots.tscn`)는 회랑·지구본을 여러 시점과 레이어 조합으로 찍어 PNG로 남깁니다. 창을 띄우며, 찍는 동안 키보드·마우스 입력을 받지 않습니다. 화면 속도 줄은 찍을 때마다 숫자가 달라 숨깁니다. 진입 시간 재기(`Tests/load_timing.tscn`)는 회랑 장면을 붙인 때부터 다 불러올 때까지 걸린 시간과, 그동안 가장 길게 멈춘 프레임을 찍습니다. 같은 실행에서 두 번 들어가고, 첫 진입 뒤에는 프랙탈 디테일을 처음 끄는 시간도 잽니다. 화면 없이 돌리면 텍스처와 메시를 그래픽 카드로 올리는 시간이 빠집니다. 그리기 비용 재기(`Tests/perf.tscn`)는 '화면 속도와 그리기 비용' 절에 있습니다.

연기 검사(`smoke.tscn`)는 그리기에 대해 다음 항목을 확인합니다: 지형 조각 수와 가장 고운 삼각형 수, 거친 삼각형 목록이 조각을 겹침·틈 없이 꼭 한 번 덮는지(조각 크기 6 × 3가지, 간격 8가지), 지형 재질에 회랑 높이맵이 들어갔는지, 치마의 `is_skirt`, 화면 속도 줄과 P 키, 화면 배율·창 크기로 정한 글자 배율.

---

## 데모 실행 파일 만들기 (macOS)

발표장에서는 편집기 없이 앱 하나로 여는 편이 안전합니다. `export_presets.cfg` 에 macOS 내보내기 설정 "macOS" 가 있어, 애플 실리콘(arm64)용 앱 `engine/export/B-PCG.app` 을 만듭니다(git 제외). 검사 장면(`Tests/`)과 `baked/` 는 앱에 넣지 않습니다.

- 앱을 만들려면 Godot 내보내기 템플릿(앱의 바탕이 되는, 미리 빌드한 Godot 실행 파일 묶음)이 있어야 합니다. .NET 판 템플릿 `Godot_v4.7.2-stable_mono_export_templates.tpz` 는 1.2 GB입니다. 받을 주소와 SHA-512는 [docs/csharp_port.md](../docs/csharp_port.md)의 '받을 파일과 SHA-512'에 있습니다.
- Godot는 템플릿을 `<HOME>/Library/Application Support/Godot/export_templates/4.7.2.stable.mono/` 에서 찾습니다. 내보내기도 화면 없는 편집기로 돌므로 임시 HOME을 쓰고, 그 임시 HOME 아래에 템플릿 폴더를 링크해 둡니다.
- 애플 실리콘용으로 내보내려면 프로젝트 설정 `rendering/textures/vram_compression/import_etc2_astc`(텍스처를 애플 GPU가 읽는 압축 형식으로도 가져오기)가 켜져 있어야 합니다. 꺼져 있으면 Godot가 내보내기를 거절하므로 `project.godot` 에 켜 두었습니다.
- 인텔 맥에서도 열려면 `binary_format/architecture` 를 `universal` 로 바꿉니다.

```bash
dotnet build engine/Bpcg.Engine.csproj
H=$(mktemp -d)
HOME=$H $GODOT --headless --path engine --import
HOME=$H $GODOT --headless --path engine --export-release "macOS" export/B-PCG.app
```

템플릿이 없으면 마지막 명령이 `ERROR: Cannot export project with preset "macOS" due to configuration errors:` 와, 템플릿을 찾지 못했다는 줄(한국어 맥에서는 `예상된 경로에서 찾은 내보내기 템플릿이 없습니다:`)을 찍고 끝납니다. 여기까지만 돌려 확인했습니다(2026-11-26). 템플릿을 받은 뒤 앱이 만들어지고 열리는지는 아직 확인하지 못했습니다 [확인 필요]. 내보낼 때 .NET이 맥용 런타임 패키지(NuGet)를 한 번 받는지도 확인해야 합니다 [확인 필요].

- 만든 앱도 시작 화면부터 엽니다. 행성 만들기는 저장소의 `configs/` 를 앱 위치에서 위로 올라가며 찾으므로, 앱이 저장소 안(`engine/export/`)에 있을 때만 됩니다. 다른 맥에서는 지난 결과 열기만 됩니다('남은 일').
- 코드 서명은 Godot 내장 임시 서명(기본값)입니다. 다른 맥으로 옮긴 앱이 열리는지는 확인하지 못했습니다 [확인 필요].

## 한눈에 보기

| 항목 | 정한 내용 |
|---|---|
| Godot | 4.7.2 **.NET 판** (`4.7.2.stable.mono.official.ed1daf0bf`), 공식 빌드(단정밀도) |
| .NET | SDK 10(저장소 맨 위 `global.json`). 게임 어셈블리 `Bpcg.Engine` 은 net10.0이고 `../src/Bpcg` 를 참조합니다(편집기 실행에서도 Bpcg는 Release로 빌드) |
| 그리기·물리 | Forward+(맥은 Metal), Jolt Physics |
| 좌표 | X = 동, Y = 위, Z = 남, 단위 m. 회랑 기준점을 원점으로 한 지역 좌표 |
| 장면 | `scenes/start.tscn`(처음 화면, 시작 장면) · `scenes/main.tscn`(회랑) · `scenes/globe.tscn`(지구본) |
| 카메라 | 40,000 m(40 km)까지 그림. 주변 25 m 지형(한 변 32~40 km) 끝까지 보이게 |
| 지형 그리기 | 64 × 64 칸 조각, 먼 조각은 거친 메시(어긋남 1 px 미만), 해 그림자는 눈에서 1,000 m 안만 |
| 내보내기 | `export_presets.cfg` 의 "macOS" (애플 실리콘 arm64, `export/B-PCG.app`) |
| 생성 결과 | `user://runs/<날짜-시각>-<프로필>-s<시드(난수 계산기가 출발점으로 삼는 번호)>/{planet,hero,corridor,globe}` (맥: `~/Library/Application Support/Godot/app_userdata/B-PCG/runs/`) |
| 설정 | 저장소의 `configs/` 를 그대로 읽음(프로젝트 폴더에서 위로 찾음, 환경 변수 `BPCG_CONFIGS` 로 바꿈) |

## 폴더

| 경로 | 들어 있는 파일 |
|---|---|
| `project.godot`, `Bpcg.Engine.csproj`, `Bpcg.Engine.sln`, `packages.lock.json` | Godot 프로젝트와 C# 프로젝트 (sln은 편집기 빌드용) |
| `scenes/` | `start.tscn`, `main.tscn`, `globe.tscn` |
| `Scripts/` | 처음 화면 `StartMenu`·`StageProgress`·`EnginePaths`, 회랑 `Main`·`CorridorFiles`(다른 스레드에서 회랑 파일 읽기)·`HeightmapTerrain`·`HeightmapLoader`·`BakedLayers`·`CaveCollision`(동굴 걷기용 충돌면)·`StrataVolume`·`Player`·`Hud`·`LoadScreen`(불러오기 진행 막대와 문제 상자), 지구본 `GlobeMain`·`GlobeView`·`GlobeData`·`GlobeCamera`·`GlobeHud`, 공용 `BakedPaths`·`Files`(파일·JSON)·`Ui`·`UiScale`(창·글자 배율)·`FrameStats`(화면 속도·그린 삼각형 수) |
| `Tests/` | 검사 장면 `smoke.tscn`·`globe_smoke.tscn`·`screenshots.tscn`·`load_timing.tscn`·`perf.tscn`(그리기 비용 재기) 과 그 C# |
| `export_presets.cfg` | 데모 실행 파일 내보내기 설정 ('데모 실행 파일 만들기' 절) |
| `shaders/` | 단면·지층·동굴·물·지구본·대기 셰이더 (`.gdshader`, `.gdshaderinc`) |
| `samples/` | 굽기 폴더가 없을 때 쓰는 작은 표본 높이맵 (`make_sample.py` 로 만듦) |
| `GLOBE.md` | 지구본 장면과 `globe/` 파일 형식 |
| `baked/` | (git 제외) `bpcg bake --engine`·스튜디오 'Godot로 보기'가 회랑을 복사해 두는 곳 = `res://baked` |

## 굽기 → Godot 파일 형식

### 좌표

Godot 지역 좌표는 X = 동, Y = 위, Z = 남인 오른손 좌표계이고 단위는 m입니다. 카메라의 앞(−Z)이 북쪽입니다. glTF도 Y가 위인 오른손 좌표계라 glb 안의 좌표도 그대로 씁니다.

### 높이맵 (`<이름>.bin` + `<이름>.json`)

C# 굽기(`src/Bpcg/Bake/Heightmap.cs`)가 쓰고, 엔진은 `HeightmapLoader.LoadStem()` 으로 읽습니다.

- `.bin`: float32 리틀 엔디언, 머리글 없음, 행 우선. 크기는 정확히 width × height × 4바이트입니다.
- 배열 `z[행, 열]`: 열이 늘면 동쪽(+X), 행이 늘면 남쪽(+Z)입니다. 0번 행이 북쪽 끝입니다.
- 표본 `(행, 열)` 의 위치 = `origin + (열 × spacing_m, z[행, 열], 행 × spacing_m)`. `origin` 은 북서쪽 모서리 표본을 높이 0에 놓았을 때의 지역 좌표입니다.
- NaN이나 무한대는 받지 않습니다.

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

`width` 는 열 수(X), `height` 는 행 수(Z)이고 높이가 아닙니다. `min`·`max` 는 float32로 바꾼 뒤의 값이라 `.bin` 과 정확히 같습니다. 메시는 `ArrayMesh.AddSurfaceFromArrays()` 로 만들고(법선은 양옆 차이로), 충돌은 `HeightMapShape3D` 에 같은 배열을 넣어 수평으로 `spacing_m` 배 늘입니다. 충돌면의 구멍(동굴 입구, 주변 지형의 회랑 자리)은 그 표본의 높이를 NaN으로 바꿔 뚫습니다. 충돌 조각마다 첫 표본(조각의 북서쪽 모서리)은 비우지 않습니다. 첫 표본이 NaN이면 그 조각 `HeightMapShape3D` 의 최저·최고 높이가 NaN이 되고 구멍도 생기지 않기 때문입니다(Godot 4.7.2 + Jolt에서 확인).

### 회랑 묶음

`bpcg bake`(`--engine` 이면 `engine/baked/` 에도)와 처음 화면의 생성이 쓰는 파일 묶음(한 단계가 만든 지도 파일과 목차를 한 폴더에 모은 결과물), 그리고 엔진의 쓰임입니다. 파일마다 내용은 [docs/pipeline.md](../docs/pipeline.md) 11장에 있습니다.

| 파일 | 엔진의 쓰임 |
|---|---|
| `manifest.json` | 회랑 사각형(`corridor.rect_engine_m`), 높이 기준(`frame.y_offset_m`), 파일 목록, 동굴 삼각형 수. 둘레 지형(`surround25`)은 이 사각형 안을 비우고, `heightmap` 이 그 자리를 채웁니다. 굽기는 `heightmap` 의 첫 표본과 마지막 표본을 사각형의 변 위에 둡니다 |
| `heightmap`, `surround25` | 지형 메시·충돌 (`HeightmapTerrain`) |
| `heightmap_detail`, `cave_mouth_detail` | (있으면) 프랙탈 디테일 레이어. `heightmap`·`cave_mouth` 와 같은 격자 |
| `water` | 물이 있는 표본(−10,000보다 큼)이 닿는 칸만 사각형으로 |
| `water_table` | 지하수면 메시(처음 켤 때)와 단면의 지하수면 선. 땅을 파면 처음 만나는 물의 높이 |
| `water_table_lower` | (있으면) 아래 지하수면: 막는 층 아래 녹는 암석의 물 높이. 단면은 이 아래를 젖은 땅으로 칠합니다. 없는 옛 굽기는 `water_table` 과 같다고 봅니다 |
| `strata.u8` + `strata.json` + `strata_top` | `R8` 3D 텍스처(가로 = 열, 세로 = 층, 깊이 = 행)와 윗면 `RF` 텍스처. 한 변이 2,048을 넘으면 쓰지 않습니다. 색은 `strata.json` 의 `legend`(재질 번호 → 색)로 칠합니다: 암석 0~11, 층리의 어두운 띠 17~26, 물 254, 공기 255 |
| `cave_mouth` | `RF` 텍스처. 음수인 곳의 지형을 뚫고, 걷기용 충돌면에도 구멍을 냄(`HeightmapLoader.BuildHoleSet`) |
| `caves.glb` | 늘 실행 중에 `GltfDocument` 로 읽습니다(다른 스레드). 편집기가 가져온(import) 사본은 다시 구운 뒤 가져오기를 돌리지 않으면 옛 동굴일 수 있어 쓰지 않습니다. 읽은 뒤 128 m 조각마다 걷기용 충돌면을 붙입니다(`CaveCollision`) |
| `entrances.json` | 동굴 입구로 옮겨 가기 (T) |
| `globe/` | 지구본 장면의 자료 (`globe.json` + 필드 `.bin`, [GLOBE.md](GLOBE.md)) |

### glb

동굴처럼 높이맵으로 나타낼 수 없는 모양은 glb(glTF 2.0 바이너리)로 넘깁니다. `NORMAL` 을 꼭 넣고, 꼭짓점마다 붙는 값(재질 번호 등)은 `COLOR_0` 에 넣습니다. 엔진은 glb가 어디에 있든 실행 중에 `GltfDocument.AppendFromFile()` 로 읽습니다. `res://` 안에 두면 편집기가 가져오기도 하지만(`.import` 생김) 게임은 그 사본을 쓰지 않습니다.

## 커밋하는 파일과 제외하는 파일

- **올림:** `project.godot`, `*.tscn`, `*.cs`, `*.gdshader`, `Bpcg.Engine.csproj`·`.sln`·`packages.lock.json`, 그리고 Godot가 만든 `*.uid` 와 `*.import`. `.uid` 가 없으면 사람마다 다른 번호가 생겨 장면 참조가 깨집니다.
- **올리지 않음:** `.godot/`(캐시와 C# 빌드 출력), `baked/`, `export/`, `*.pck`. 모두 저장소 맨 위 `.gitignore` 에 있습니다.
- 새 스크립트나 셰이더를 더하면 `--import` 를 한 번 돌리거나 편집기로 열어 `.uid` 를 만든 뒤 같이 커밋합니다.

## C# 규칙

- 저장소의 `.editorconfig` 와 `Directory.Build.props`(경고는 오류, 빌드 중 코드 모양 검사)를 따르고, `dotnet format Bpcg.slnx --verify-no-changes` 가 통과해야 합니다.
- Godot 노드 클래스는 `partial` 이고, 파일 이름과 클래스 이름이 같아야 합니다.
- 이름에 단위를 붙입니다(`SpacingM`, `WalkSpeedMS`).
- 주석과 화면 글은 한국어 합니다체로, [CLAUDE.md](../CLAUDE.md)와 [Humanize.md](../Humanize.md)의 규칙을 따릅니다. 검사 표시(`BPCG_SMOKE_OK` 등)만 영어입니다.
- 계산 코드는 엔진에 두지 않고 `src/Bpcg` 에 둡니다. 엔진은 Godot 자료형을 쓰고, `Bpcg` 는 Godot를 모릅니다.

<details><summary>예전 GDScript 판과의 대응</summary>

예전 GDScript 판을 C#으로 옮긴 사정은 [docs/history.md](../docs/history.md)의 2026-10-03 줄에 있습니다.

| 예전 (GDScript) | 지금 (C#) | 비고 |
|---|---|---|
| `scripts/baked_paths.gd` | `Scripts/BakedPaths.cs` | 명령줄 → 처음 화면이 고른 폴더(`RuntimeDir`) → `res://baked` |
| `scripts/heightmap_loader.gd`, `terrain.gd` | `Scripts/HeightmapLoader.cs`, `HeightmapTerrain.cs` | 내보내기 속성 이름은 PascalCase (`Material`, `SkirtM`) |
| `scripts/player.gd`, `hud.gd`, `main.gd` | `Scripts/Player.cs`, `Hud.cs`, `Main.cs` | N 키와 '처음 화면' 단추를 더함 |
| `scripts/strata_volume.gd`, `baked_layers.gd` | `Scripts/StrataVolume.cs`, `BakedLayers.cs` | |
| `scripts/globe_*.gd`, `globe.gd` | `Scripts/GlobeData.cs`, `GlobeView.cs`, `GlobeCamera.cs`, `GlobeHud.cs`, `GlobeMain.cs` | 자료가 없으면 처음 화면을 안내 |
| (스튜디오의 '실행' 탭) | `Scripts/StartMenu.cs`, `StageProgress.cs`, `EnginePaths.cs` | 새로 만듦 |
| `tests/smoke.gd`, `globe_smoke.gd`, `screenshots.gd` | `Tests/SmokeTest.cs`, `GlobeSmokeTest.cs`, `Screenshots.cs` + `.tscn` | C#은 `--script` 로 돌리기 어려워 검사 장면으로 돌림 |

</details>

## 남은 일

- 내보낸 게임에는 저장소의 `configs/` 가 없습니다. 설정을 게임에 넣어 `user://` 로 풀어 쓰는 방법을 정해야 합니다(`Scripts/EnginePaths.cs` 의 TODO).
- 데모 앱 내보내기는 템플릿(1.2 GB)을 받지 않아 끝까지 돌려 보지 못했습니다('데모 실행 파일 만들기' 절).
- 하늘에서 내려다보면 주변 지형이 한 장에 약 7 ms를 씁니다(2304 × 1296 창, 주변 지형을 끄면 한 장 10.2 → 3.1 ms). 삼각형을 줄여도, 한쪽 면만 그려도 줄지 않아 화소마다 칠하는 비용으로 봅니다.
- 맥(Metal)에서는 GPU 시간을 재지 못해, 화면 속도가 60 아래로 떨어진 까닭이 GPU인지 CPU인지 HUD로는 구분할 수 없습니다. 그리기 비용 재기 장면의 '한 장' 값으로 봅니다.
- 10/21 런타임 복셀 관문(실행 중에 땅을 파고 쌓는 godot_voxel 확장, [docs/roadmap.md](../docs/roadmap.md))은 아직 시험하지 않았습니다. 넣을 때는 `engine/addons/` 아래에 두고, 화면 없이 `ClassDB.ClassExists("VoxelLodTerrain")` 부터 확인합니다. 안 되면 지금의 높이맵·glb 형식을 그대로 씁니다.
