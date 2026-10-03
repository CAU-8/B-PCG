"""B-PCG 스튜디오: 매개변수를 바꿔 생성기(C# 콘솔)를 돌리고 결과를 브라우저에서 보는 로컬 도구.

`uv run bpcg studio` 로 켭니다 (docs/studio.md).

| 모듈 | 하는 일 |
|---|---|
| params | configs/*.toml 에서 매개변수 표(설명·단위·분류)를 만듦 |
| progress | 파이프라인 기록 줄 → 단계별 진행률 (단계 표 STAGES 한 곳) |
| jobs | C# 콘솔 `all` 을 하위 프로세스로 돌림, job.json, 실행 목록 |
| summary | 개요 탭: 바뀐 값, 걸린 시간, 솔버 수렴, 점수표 설명, 그림 설명 |
| views | 필드를 지도용 격자(행성 위경도, 히어로 축소)로 바꾸고 이름·단위·읽는 법을 붙임 |
| godot | Godot .NET 판 찾기, 회랑 파일을 engine/baked 로 복사, 빌드·가져오기(임시 HOME), 실행 |
| server | 127.0.0.1 에서만 듣는 HTTP 서버 (JSON API + static/) |
| names | 바깥에서 받은 파일·폴더 이름 검사 (경로·드라이브 섞인 이름 막기) |
| csharp | C# 콘솔(src/Bpcg.Cli) 빌드·실행 명령 |
| cli | 명령줄 입구 `bpcg` (studio, 나머지는 C# 콘솔로 넘김) |
| paths, config, bundle | 저장소 경로, 설정 읽기·덮어쓰기 검사, 묶음 manifest·JSON 쓰기 |
| fields, hero | 필드 표(이름·단위), 히어로 격자 크기 검사 |

생성 계산은 C# 이 하고, 스튜디오는 그 결과 파일을 읽기만 합니다.
"""

__version__ = "0.1.0"
