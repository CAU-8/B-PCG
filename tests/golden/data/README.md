# tests/golden/data — 커밋하는 golden 자료

golden 자료는 "이 입력이면 이 출력이 나와야 한다"를 적어 둔 정답 자료입니다. 생성기를 Python에서 C#으로 옮길 때, C#이 Python과 비트까지 같은 값을 내는지 이 자료로 확인했습니다. 포팅 때 Python 생성기(지금은 지운 `src/bpcg`)의 golden 스크립트가 만든 대조 시험 자료 중 작은 손 사례만 여기에 둡니다(각 파일 1 MB 이하, docs/conventions.md 8절). 나머지 큰 사례는 그때 `out/golden/` 에만 만들었고 git 에 올리지 않았습니다. Python 생성기를 지웠으므로 이 자료는 더 만들 수 없고, C# 이 바뀌어도 이 값과 같아야 하는 고정 기준입니다.

- 출처: 포팅 당시 `src/bpcg` 함수 출력 (스크립트는 git 기록의 `csharp/golden/export_golden.py`). 만든 환경(Python·numpy·numba·scipy 판, 운영체제)은 `manifest.json` 에 있습니다.
- 사례 파일: `<모듈>/<사례>.npz`(배열 이름 `in.<이름>`·`out.<이름>`) + 같은 이름의 `.json`(배열이 아닌 값).
- 초월 함수(tan·atan·sin 등)를 거친 값은 만든 운영체제의 libm 을 따릅니다. 지금 자료는 macOS arm64 에서 만들었습니다.
- 예외: 설정 사례(`core/config` 의 `load_earth_*`, `overrides`)는 `configs/planets/earth.toml` 의 글자를 정답으로 담습니다. 그래서 읽는 규칙이 그대로여도 `earth.toml` 에 키를 더하거나 값을 바꾸면 정답이 달라집니다. 그때만 `uv run python tests/golden/refresh_config.py` 로 다시 씁니다. 이 스크립트는 Python 판 `load_config` 와 같은 규칙으로 읽고, 2026-10-19 에 포팅 때 자료(바꾸기 전 `earth.toml`)를 글자 하나 다르지 않게 다시 만드는 것을 확인했습니다. 덮어쓰기의 형 검사(`checked`)와 오류 사례는 설정 내용과 상관없어 그대로 둡니다.
- 예외: 행성 옮기기 사례(`planet/transfer_*`)의 `dist_divergent_m`은 정답과 다릅니다. 정답 자료를 만든 뒤, 발산 경계까지 거리를 경계 양쪽에 반대 부호를 붙여 옮기도록 계산을 바꿨기 때문입니다(docs/pipeline.md 4.1 6번). 그래서 `PlanetMaterialsTests.TransferMaterialsMatchesPython`은 이 필드만 따로 봅니다. 가장 가까운 거친 칸이 발산 경계 칸이 아닌 L0 칸은 정답과 비트까지 같아야 하고, 나머지 L0 칸은 정답 이하여야 합니다. 커밋한 두 사례(`transfer_c8_f12`, `transfer_c8_f12_noinfo`)에서는 864칸 가운데 137칸이 달라지고, 융기를 포함한 다른 필드는 모두 정답과 같습니다. 새 계산이 맞는지는 `RiftDistanceTests`가 손으로 놓은 판 경계의 실제 거리와 비교해 봅니다.
- 예외: 필드 표(`core/fields/fields_table.json`)는 손으로 고칩니다. `ConfigTests.FieldsTable` 이 이 파일의 필드 줄(이름, 묶음, 단위, 자료형, 설명)을 `src/Bpcg/Core/Fields.cs` 의 `Fields.All` 과 한 줄씩 비교하기 때문입니다. 묶음 목록(`groups`)과, 없는 필드 이름을 줬을 때의 오류 글(`check_error`)도 비교합니다. 그래서 필드를 더하거나 필드 설명·오류 글을 바꾸면, 같은 커밋에서 이 파일의 같은 자리도 고칩니다.

## 사례를 읽는 시험

- 시험은 사례를 환경 변수 `BPCG_GOLDEN` 의 폴더 → `out/golden/` → 이 폴더 순서로 찾습니다(`tests/Bpcg.Tests/Golden.cs` 의 `Golden.Load`).
- 어디에도 없을 때, 큰 사례 목록(`Golden.cs` 의 `LargeCases`)에 있는 이름이면 그 시험을 건너뜁니다. 지금 이렇게 건너뛰는 시험은 27개입니다(2026-11-26). 큰 사례 파일은 새로 만들 수 없습니다.
- 목록에 없는 이름이면 건너뛰지 않고 실패합니다. 이름을 잘못 적은 시험이 조용히 건너뛰어져 영영 돌지 않는 일을 막기 위해서입니다. 커밋한 사례인데 파일이 없을 때도 실패합니다.
- 행성 1단계 사례(`planet/` 의 `functions`, `materials_*` 넷, `transfer_c8_f12`·`transfer_c8_f12_noinfo`, `ocean_sphere8`, `flat24`)는 `tests/Bpcg.Tests/Planet/PlanetFunctionTests.cs` 와 `PlanetMaterialsTests.cs` 가 읽습니다. 같은 시험이 쓰는 `planet/transfer_c16_f32` 는 커밋하지 않은 큰 사례라 건너뜁니다.
- `numerics/tanh/vectors` 는 `tests/Bpcg.Tests/Numerics/NpUfuncTests.cs` 가 읽습니다.
