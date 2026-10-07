# 함께 일하는 방법

> 세 사람이 같은 저장소를 고치면, 남의 작업을 덮어쓰거나 깨진 코드를 `main`에 올리기 쉽습니다. 그래서 일은 모두 자기 브랜치에서 하고, 시험을 통과한 뒤 다른 팀원 한 명이 읽어 본 것만 `main`에 합칩니다. 이 파일은 그 하루 순서를 적습니다. 규칙의 전체 목록은 [docs/conventions.md](docs/conventions.md)에 있습니다.

## 처음 한 번

```bash
git clone https://github.com/CAU-8/B-PCG.git b-pcg
cd b-pcg
./scripts/setup.sh            # 맥·리눅스. 엔진을 맡았으면 ./scripts/setup.sh --godot
```

윈도우에서는 PowerShell에서 돌립니다.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
```

- 생성기와 엔진은 C#이라 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)가 먼저 있어야 합니다(맥은 `brew install --cask dotnet-sdk`).
- 스크립트가 파이썬 쪽(uv, 파이썬 3.13, 패키지)을 설치하고 `tools/doctor.py`로 점검합니다. 마지막 줄이 `필수 실패 0개` 면 됩니다.
- 진짜 지구 자료(약 4.5 GB)가 필요하면 `--data`(윈도우는 `-Data`)를 붙입니다.

## 매번 하는 순서

### 1. 최신 `main`에서 브랜치 만들기

```bash
git switch main && git pull
git switch -c feat/priority-flood
```

- 왜: 남이 어제 합친 것을 받은 뒤 시작해야, 나중에 합칠 때 부딪히는 곳이 줄어듭니다.
- 브랜치 이름은 `종류/짧은-설명` 입니다. 종류는 아래 '커밋 종류' 표와 같습니다.

### 2. 고치고 검사하기

```bash
dotnet build Bpcg.slnx && dotnet test --solution Bpcg.slnx
dotnet format Bpcg.slnx --verify-no-changes
uv run pytest -m "not slow"
uv run ruff format . && uv run ruff check .
```

| 명령 | 무엇을 보나 | 걸리는 시간 (맥북 에어 M3) |
|---|---|---|
| `dotnet build` + `dotnet test` | C#이 경고 없이 빌드되는지, C# 시험(Python 판과 비트까지 같은지 포함) | 빌드가 끝난 뒤 시험 약 6초 |
| `dotnet format … --verify-no-changes` | C# 코드 모양(띄어쓰기, 중괄호)이 규칙대로인지 | 약 12초 |
| `uv run pytest -m "not slow"` | C# 명령줄이 쓴 결과 파일, 스튜디오 | 약 40초 |
| `ruff` | 파이썬 코드 모양과 흔한 실수 | 몇 초 |

엔진을 고쳤으면 `uv run pytest -m godot tests/test_engine.py`도 돌립니다(Godot .NET 판이 있어야 함).

### 3. 커밋하기

```bash
git commit -m "feat(hydro): 우선순위 홍수 채우기 추가"
```

메시지는 `종류(범위): 한국어 요약` 입니다. 범위는 고친 묶음 이름입니다(`core`, `hydro`, `bake`, `cli`, `engine`, `studio`, `compare` 등).

### 4. 올리고 PR 열기

```bash
git push -u origin feat/priority-flood
gh pr create
```

PR 양식의 확인 목록을 채웁니다. 리뷰어가 무엇을 왜 바꿨는지 1분 안에 알 수 있게 씁니다.

### 5. 합치기

- 리뷰어 1명이 승인하고 CI(GitHub이 PR마다 자동으로 돌리는 검사)가 통과하면 합칩니다.
- 합칠 때는 squash를 씁니다. 브랜치의 커밋 여러 개를 하나로 묶어 `main`의 기록을 기능 하나에 한 줄로 남기는 방식입니다.
- 합친 브랜치는 지웁니다.

## 막혔을 때

| 보이는 것 | 까닭 | 고치는 법 |
|---|---|---|
| `dotnet format … --verify-no-changes` 실패 | C# 코드 모양이 규칙과 다름 | `dotnet format Bpcg.slnx` 를 돌리면 고쳐 줍니다 |
| `ruff format --check` 또는 `ruff check` 실패 | 파이썬 코드 모양이나 import 순서 | `uv run ruff format .` 와 `uv run ruff check --fix .` |
| 빌드 오류 `CS…` 와 함께 경고가 오류로 나옴 | 이 저장소는 경고도 오류로 셉니다 | 경고 내용을 고칩니다. 끄지 않습니다 |
| C# 시험에서 `큰 golden 사례 … 가 없어 건너뜁니다` | 큰 대조 자료는 저장소에 없음 | 정상입니다. 작은 자료로 하는 시험은 그대로 돕니다 |
| pytest 에서 `Godot 4.7.2 .NET 판이 없습니다` 로 건너뜀 | Godot .NET 판을 받지 않음 | 엔진을 맡지 않았다면 정상입니다 |

## 커밋 종류

| 종류 | 언제 |
|---|---|
| `feat` | 새 기능 |
| `fix` | 버그 수정 |
| `docs` | 문서만 |
| `test` | 시험만 |
| `refactor` | 하는 일은 같고 코드 구조만 바꿈 |
| `perf` | 속도·메모리 개선 |
| `chore` | 설정, 의존성, 설치 스크립트 |
| `data` | 자료 받기, 목록 파일, 시험 자료 |
| `exp` | `analysis/` 의 실험 |

## 리뷰할 때 보는 것

- 시험이 있는가. 허용 오차(얼마나 틀려도 통과로 볼지)에 근거가 적혀 있는가
- 함수 설명 주석(C# `///`, 파이썬 docstring)에 단위가 있는가(m, yr, m/yr …)
- 절대 경로, `System.Random`·전역 난수, GPL 라이선스 패키지 import가 없는가
- 문서와 화면 글이 [CLAUDE.md](CLAUDE.md)의 친절함 기준을 따르는가
- [설계도](docs/design/blueprint.md)와 어긋나지 않는가. 어긋나면 설계도를 먼저 고칠지 이야기합니다

## 패키지를 더할 때

```bash
uv add <패키지> --group <dev|data|mesh|analysis|gpl|notebook>   # 파이썬
dotnet add <프로젝트> package <패키지>                            # C#
```

- 파이썬은 `pyproject.toml`과 `uv.lock`, C#은 `packages.lock.json`을 함께 커밋합니다.
- PR 설명에 왜 필요한지와 라이선스를 적습니다.
- `pip install`은 쓰지 않습니다. 팀원마다 설치된 판이 달라지기 때문입니다.

## 금요일 통합일

그 주의 PR을 모두 합치고, 데모 빌드가 도는지 확인합니다. 과학 계산 기능은 데모 빌드에 들어가야 '완료'로 칩니다.

## GitHub 저장소 설정 (처음 한 번, 관리자)

- 저장소: [CAU-8/B-PCG](https://github.com/CAU-8/B-PCG) (공개).
- Settings → Branches → `main` 보호 규칙:
    - PR 필수, 승인 1명
    - 상태 검사 `lint`, `test`, `csharp` 필수
    - 새 커밋이 올라오면 지난 승인을 무효로
- Settings → General → Pull Requests: squash merge만 켜고, "Automatically delete head branches"를 켭니다.
