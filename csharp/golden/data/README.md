# csharp/golden/data — 커밋하는 golden 자료

`csharp/golden/export_golden.py` 가 만든 대조 시험 자료 중 작은 손 사례만 여기에 둡니다(각 파일 1 MB 이하, docs/conventions.md 8절). 전체 사례는 `out/golden/` 에 있고 git 에서 빠집니다.

- 다시 만들기: `uv run python csharp/golden/export_golden.py` (이 폴더와 `out/golden/` 을 함께 다시 씀).
- 출처: 저장소의 `src/bpcg` 함수 출력. 만든 환경(Python·numpy·numba·scipy 판, 운영체제)은 `manifest.json` 에 있습니다.
- 사례 파일: `<모듈>/<사례>.npz`(배열 이름 `in.<이름>`·`out.<이름>`) + 같은 이름의 `.json`(배열이 아닌 값).
- 초월 함수(tan·atan·sin 등)를 거친 값은 만든 운영체제의 libm 을 따릅니다. 지금 자료는 macOS arm64 에서 만들었습니다.
