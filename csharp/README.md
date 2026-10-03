# csharp/ — B-PCG C# port

This folder holds the C# port of the generator in `src/bpcg` (target: .NET 10, `net10.0`). The Python code stays the reference; every C# function is checked against "golden" outputs written by Python.

`src/bpcg` 의 생성 코드를 C# 으로 옮긴 곳입니다. 진행 상태와 규칙은 [docs/csharp_port.md](../docs/csharp_port.md), 지시 원문은 [docs/csharp_port_prompt.md](../docs/csharp_port_prompt.md)에 있습니다.

| 폴더 | 내용 |
|---|---|
| `Bpcg/` | 계산 라이브러리 (Godot 에 의존하지 않음). `Core/`·`Hydro/` … 는 Python 묶음과 1:1, `Numerics/`·`IO/` 는 numpy·scipy·파일 형식 대체 |
| `Bpcg.Cli/` | 콘솔 프로그램 (planet·hero·bake·all, 단계 2 끝에 옮김). 실행 단위는 라이브러리의 `Runs.cs` |
| `Bpcg.Engine/` | Godot 4.7.2 .NET 프로젝트 (engine/ 의 C# 판, 엔진 안에서 행성 생성). [Bpcg.Engine/README.md](Bpcg.Engine/README.md) |
| `Bpcg.Tests/` | 대조 시험 (xUnit). Python 모듈 하나에 시험 클래스 하나 |
| `golden/export_golden.py` | golden 자료를 만드는 Python 스크립트 |
| `golden/data/` | 커밋하는 작은 golden 사례 (각 1 MB 이하) |

## 명령

```sh
uv run python csharp/golden/export_golden.py   # golden 자료 (out/golden/ + csharp/golden/data/)
dotnet build csharp/Bpcg.slnx
dotnet test csharp/Bpcg.slnx
dotnet format csharp/Bpcg.slnx --verify-no-changes
```

.NET 10 SDK 가 필요합니다(macOS: `brew install --cask dotnet-sdk`). 저장소 맨 위 `global.json` 이 SDK 판을 고정합니다.
