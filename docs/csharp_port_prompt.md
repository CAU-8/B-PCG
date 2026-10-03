B-PCG 생성 코드의 C# 포팅
작업 목표

https://github.com/CAU-8/B-PCG 저장소의 src/bpcg Python 코드를 C#으로 옮긴다. 대상은 core, planet, geology, hydro, landscape, subsurface, metrics, hero, volume, bake 묶음과 pipeline.py, 그리고 cli.py의 planet·hero·bake·all 명령이다. 코드는 csharp/Bpcg/의 net10.0 class library와 csharp/Bpcg.Cli/ 콘솔 프로그램에 두며, 나중에 Godot .NET 판이 그대로 참조할 수 있도록 Godot에 의존하지 않게 쓴다. 이전 지시와 다른 부분은 새 지시를 따르고, 이미 만든 C# 코드는 버리지 않고 이어서 쓴다. 새 대화에서는 csharp/에 아직 없는 모듈부터 이어서 옮긴다.

하지 않는 일

코드를 옮기는 일 외의 작업은 모두 뒤로 미룬다. 계획 문서, 진행 기록 문서, Python 결과와 비교하는 시험 자료와 테스트 프로젝트, 속도 최적화, CI·설치 스크립트·규칙 문서 수정, Godot 프로젝트 전환이 그러한 작업이다. studio, analysis/, tools/, tests/도 옮기지 않으며, Python 코드는 읽기만 하고 고치지 않는다. 판단이 필요한 곳에서는 멈추어 묻지 않고, // TODO(port): 주석에 쟁점을 적은 뒤 다음 코드로 넘어간다. 다만 .NET SDK가 없어 빌드할 수 없으면 멈추고 보고한다.

옮기는 규칙

동작은 그대로 옮기므로 알고리즘·상수·필드 이름·설정 키·파일 형식을 바꾸지 않고, 설정은 configs/의 TOML 파일을 그대로 읽는다. numba 커널은 같은 순서로 도는 반복문으로 옮기고, prange 반복만 Parallel.For로 옮긴다. scipy, scikit-image, trimesh가 하던 일은 C#으로 직접 구현하거나 MIT·BSD·Apache-2.0 라이선스의 NuGet 패키지로 대신한다. Python의 음수 //·%, 안정 정렬, float32와 Python 실수의 형 승격, splitmix64 해시의 ulong 넘침은 C#에서 결과가 달라지기 쉬우므로 옮기는 시점에 바로 맞춘다. 주석은 Python docstring에서 단위와 배열 모양만 짧게 옮긴다.

작업 순서
csharp/Bpcg/와 csharp/Bpcg.Cli/ 프로젝트, .npy·.npz 읽기·쓰기, TOML 읽기를 만든다.
core → hydro → planet → geology → landscape → subsurface → metrics → pipeline·hero → volume → bake → cli 순서로 옮긴다.
묶음 하나를 옮길 때마다 dotnet build가 통과하면 feat/csharp-port 브랜치에 커밋한다.
마지막으로 콘솔 프로그램에서 tiny 프로필로 all 명령이 끝까지 도는지만 확인한다.
