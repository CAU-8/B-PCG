# tests/golden/data — 커밋하는 golden 자료

포팅 때 Python 생성기(지금은 지운 `src/bpcg`)의 golden 스크립트가 만든 대조 시험 자료 중 작은 손 사례만 여기에 둡니다(각 파일 1 MB 이하, docs/conventions.md 8절). 전체 사례는 그때 만든 `out/golden/` 에 있고 git 에서 빠집니다. Python 생성기를 지웠으므로 이 자료는 더 만들 수 없고, C# 이 바뀌어도 이 값과 같아야 하는 고정 기준입니다.

- 출처: 포팅 당시 `src/bpcg` 함수 출력 (스크립트는 git 기록의 `csharp/golden/export_golden.py`). 만든 환경(Python·numpy·numba·scipy 판, 운영체제)은 `manifest.json` 에 있습니다.
- 사례 파일: `<모듈>/<사례>.npz`(배열 이름 `in.<이름>`·`out.<이름>`) + 같은 이름의 `.json`(배열이 아닌 값).
- 초월 함수(tan·atan·sin 등)를 거친 값은 만든 운영체제의 libm 을 따릅니다. 지금 자료는 macOS arm64 에서 만들었습니다.
