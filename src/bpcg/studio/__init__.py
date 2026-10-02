"""B-PCG 스튜디오: 매개변수를 바꿔 파이프라인을 돌리고 결과를 브라우저에서 보는 로컬 도구.

`uv run bpcg studio` 로 켭니다 (docs/studio.md).

| 모듈 | 하는 일 |
|---|---|
| params | configs/*.toml 에서 매개변수 표(설명·단위·분류)를 만듦 |
| progress | 파이프라인 기록 줄 → 단계별 진행률 (단계 표 STAGES 한 곳) |
| jobs | `bpcg all` 과 그림 스크립트를 하위 프로세스로 돌림, job.json, 실행 목록 |
| summary | 개요 탭: 바뀐 값, 걸린 시간, 솔버 수렴, 점수표 설명, 그림 설명 |
| views | 필드를 지도용 격자(행성 위경도, 히어로 축소)로 바꾸고 이름·단위·읽는 법을 붙임 |
| godot | Godot 찾기, 회랑 파일을 engine/baked 로 복사, 가져오기(임시 HOME), 실행 |
| server | 127.0.0.1 에서만 듣는 HTTP 서버 (JSON API + static/) |
| names | 바깥에서 받은 파일·폴더 이름 검사 (경로·드라이브 섞인 이름 막기) |

스튜디오는 실행 입구라서 계산 모듈을 읽기만 하고, 계산 모듈은 스튜디오를 import 하지 않습니다.
"""
