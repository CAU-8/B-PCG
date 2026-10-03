# 함께 일하는 방법

규칙의 전체 목록은 [docs/conventions.md](docs/conventions.md)에 있습니다. 이 파일은 하루 작업의 순서만 적습니다.

## 처음 한 번

```bash
git clone <저장소 주소> b-pcg
cd b-pcg
./scripts/setup.sh            # 맥·리눅스. 엔진 담당은 ./scripts/setup.sh --godot
```

윈도우에서는 PowerShell에서 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
```

생성기와 엔진은 C#이라 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)를 먼저 설치합니다(맥은 `brew install --cask dotnet-sdk`). 설치 스크립트는 운영체제를 확인하고 uv, 파이썬 3.13, 패키지를 설치하고 .NET SDK가 있는지 본 뒤 `tools/doctor.py`로 점검합니다. `--godot`는 Godot 4.7.2 .NET 판을 `.tools/godot-net/`에 받습니다. 파일럿 자료(약 4.5 GB)가 필요하면 `--data`(윈도우는 `-Data`)를 붙입니다.

## 매번

1. main을 최신으로 맞추고 브랜치를 만듭니다.

    ```bash
    git switch main && git pull
    git switch -c feat/priority-flood
    ```

2. 코드를 고치고 테스트와 린트를 돌립니다.

    ```bash
    dotnet build Bpcg.slnx && dotnet test --solution Bpcg.slnx
    dotnet format Bpcg.slnx --verify-no-changes
    uv run pytest -m "not slow"
    uv run ruff format . && uv run ruff check .
    ```

    엔진을 고쳤으면 `uv run pytest -m godot tests/test_engine.py`도 돌립니다.

3. 커밋합니다. 메시지 형식은 `종류(범위): 한국어 요약`입니다.

    ```bash
    git commit -m "feat(hydro): 우선순위 홍수 채우기 추가"
    ```

4. 올리고 PR을 엽니다. PR 양식의 확인 목록을 채웁니다.

    ```bash
    git push -u origin feat/priority-flood
    gh pr create
    ```

5. 리뷰어 1명이 승인하고 CI가 통과하면 squash로 합칩니다. 합친 브랜치는 지웁니다.

## 커밋 종류

| 종류 | 언제 |
|---|---|
| `feat` | 새 기능 |
| `fix` | 버그 수정 |
| `docs` | 문서만 |
| `test` | 테스트만 |
| `refactor` | 동작은 같고 구조만 바꿈 |
| `perf` | 속도·메모리 개선 |
| `chore` | 설정, 의존성, 설치 스크립트 |
| `data` | 자료 받기·manifest·시험 자료 |
| `exp` | analysis/ 의 실험 |

## 리뷰할 때 보는 것

- 테스트가 있는가, 허용 오차에 근거가 있는가
- 단위가 docstring에 적혀 있는가(m, yr, m/yr …)
- 절대 경로, 전역 난수, GPL import가 없는가
- 설계도(docs/design/blueprint.md)와 어긋나지 않는가. 어긋나면 설계도를 먼저 고칠지 이야기합니다

## 패키지를 더할 때

```bash
uv add <패키지> --group <dev|data|mesh|analysis|gpl|notebook>
```

`pyproject.toml`과 `uv.lock`을 함께 커밋하고, PR 설명에 왜 필요한지와 라이선스를 적습니다. `pip install`은 쓰지 않습니다.

## 금요일 통합일

그 주의 PR을 모두 합치고 데모 빌드가 도는지 확인합니다. 과학 모듈은 데모 빌드에 들어가야 '완료'로 칩니다.

## GitHub 저장소 설정 (처음 한 번, 관리자)

- 저장소 이름은 `b-pcg`로 합니다.
- Settings → Branches → `main` 보호 규칙을 다음처럼 둡니다.
    - PR 필수, 승인 1명
    - 상태 검사 `lint`, `test`, `csharp` 필수
    - 오래된 승인 무효화
- Settings → General → Pull Requests에서 squash merge만 켜고, "Automatically delete head branches"를 켭니다.
