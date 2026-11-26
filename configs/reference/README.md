# configs/reference

스튜디오의 '진짜 지구와 비교' 탭은 생성 결과를 진짜 지구 값과 나란히 놓습니다. 그런데 지구 원자료(약 4.5 GB)는 git 에 올리지 않습니다. 그래서 원자료를 한 번 읽어 분포·곡선·표만 남긴 작은 파일을 여기에 둡니다. 자료를 받지 않은 팀원도 이 파일로 비교 탭을 씁니다.

| 파일 | 담는 것 | 만드는 명령 |
|---|---|---|
| `earth.json` | 지구 자료 일곱 가지의 분포·곡선·표 (43 kB) | `uv run bpcg earth-ref` |

## 직접 돌려 보기

```bash
uv run bpcg earth-ref
```

- 지구 자료(`data/pilot/`, `data/derived/afrisar`, `data/derived/veg`)와 `data` 패키지 묶음(`uv sync --inexact --group data`)이 있어야 합니다. 받는 법은 [data/README.md](../../data/README.md)에 있습니다.
- 맞게 돌았으면 마지막 줄이 `[지구 기준] 끝: 자료 7개 (…), 빠진 자료 없음, 43 kB, … s → …/configs/reference/earth.json` 입니다.
- 약 1분 35초 걸리고, 메모리를 가장 많이 쓸 때 약 3.5 GB 입니다(맥북 에어 M3, 2026-11-27).
- 자료 하나만 다시 읽으려면 `--only glo90,octopus` 처럼 씁니다. 나머지는 지난 파일의 값을 그대로 둡니다.

## 손으로 고치지 않습니다

- 이 파일은 `src/bpcg_studio/earthref.py` 가 씁니다. 손으로 고치면 다음에 만들 때 사라집니다.
- 구간(분포를 나누는 칸의 경계)은 `src/bpcg_studio/earthstats.py` 의 상수와 같아야 합니다. 상수를 바꾸면 이 파일을 다시 만듭니다. 다르면 `tests/test_earth.py` 의 `test_committed_reference_matches_code_bins` 가 실패합니다.
- 프로필을 더하거나 L0 면당 칸 수(`profile.grid.l0_n_per_face`)를 바꾸면, 그 칸 크기의 지구 값이 없으므로 이 파일을 다시 만듭니다.

## 출처와 라이선스

이 파일은 원자료가 아니라 원자료에서 센 분포·곡선·표입니다. 그래도 원자료의 출처 표기와 이용 조건을 따릅니다. 자료마다 라이선스와 출처 문구는 파일 안 `sources` 에 있고, 스튜디오 탭 맨 아래에도 나옵니다.

- **Copernicus DEM GLO-90** 에서 센 값(파일럿 유역 40곳의 경사·기복·고도 적분)은 가공한 결과라 이 문구를 함께 적습니다: produced using Copernicus WorldDEM™-90 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved.
- **OCTOPUS v2** 에서 센 값(침식률 × 경사 개수 표)은 CC BY-NC-SA 4.0 으로 다룹니다(라이선스가 아직 정해지지 않았습니다. [data/README.md](../../data/README.md) '아직 정해지지 않은 라이선스').
- **Global Aridity Index v3.1** 은 Readme 에 비상업 이용이라고 적혀 있습니다. 이 과제는 비상업이고, 결과를 공개할 때 이 조건을 함께 적습니다.
- 나머지(ETOPO 2022, CHELSA, RiverATLAS, AfriSAR)의 출처는 [data/README.md](../../data/README.md)의 '출처 표기'와 같습니다.
