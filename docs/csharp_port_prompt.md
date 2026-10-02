B-PCG 생성 코드의 C# 포팅
작업 목표

포팅 작업의 목표는 src/bpcg의 Python 생성 코드를 C#으로 옮겨, Godot 4.7.2 .NET 판 안에서 행성을 만들고 엔진용 파일을 굽게 하는 것이다. C# 코드는 같은 설정 파일과 같은 시드를 받으면 Python 코드와 같은 행성 묶음, 히어로 묶음, 회랑 파일을 내야 한다. Python 코드는 포팅 기간 내내 고치지 않고, C# 결과가 맞는지 판정하는 기준으로만 쓴다. 판정의 근거는 'golden 자료'인데, golden 자료는 Python 코드가 정해진 입력에서 낸 출력을 파일로 저장한 것이다. C# 코드가 같은 입력에서 낸 출력을 golden 자료와 맞대어 보는 테스트는 '대조 시험'이라 한다.

저장소의 현재 구조

생성기 본체인 src/bpcg는 Python 3.13으로 작성되었고, 포팅 대상은 그중 약 14,500 줄이다. 계산은 numpy 배열과 numba 커널로 이루어지는데, numba는 Python 함수를 기계어로 컴파일하는 도구이다. prange는 numba 커널 안의 반복을 여러 스레드에 나누어 돌리는 구문이며, 경쟁 조건이 없는 반복에만 쓰인다. scipy는 cKDTree, ndimage, find_peaks, RegularGridInterpolator, brentq를 부르는 곳에서 쓰이고, 동굴 메시는 scikit-image의 marching cubes와 trimesh의 glb 쓰기로 만든다. marching cubes는 3차원 격자 값에서 같은 값을 잇는 면을 삼각형 메시로 뽑는 알고리즘이고, glb는 glTF 3D 모델을 한 파일에 담는 바이너리 형식이다. 지형 모양을 정하는 난수는 core/hashing.py의 splitmix64 해시로만 만들기 때문에, C#에서도 같은 값을 비트 단위로 재현할 수 있다. splitmix64는 64비트 정수를 곱셈과 비트 이동으로 섞어 고르게 퍼진 값을 만드는 함수이다. 엔진(engine/)은 현재 Godot 4.7.2 표준판이며, GDScript로 구운 파일을 읽어 보여 주기만 한다.

먼저 읽을 문서

코드를 쓰기 전에 README.md, CONTRIBUTING.md, docs/conventions.md, docs/pipeline.md, engine/README.md, engine/GLOBE.md를 읽는다. docs/pipeline.md는 단계마다 공식과 함수 계약을 고정한 구현 기준이고, src/bpcg/core/fields.py의 FIELDS는 필드 이름·단위·dtype을 정한 유일한 목록이다. 설정 값은 configs/의 TOML 파일에 있으므로, C# 코드도 같은 파일을 읽고 숫자를 코드에 박지 않는다. 엔진이 읽는 파일 형식은 engine/README.md의 '파이썬 → Godot 넘김 형식' 절과 engine/GLOBE.md의 '파일 형식 요약' 절에 있다.

포팅 범위

포팅 대상은 core, planet, geology, hydro, landscape, subsurface, metrics, hero, volume, bake 묶음과 pipeline.py이다. cli.py에서는 planet, hero, bake, all 명령만 옮기고, studio 명령과 studio/ 묶음은 옮기지 않는다. analysis/, tools/, tests/의 코드도 Python으로 남긴다. 포팅은 동작을 그대로 옮기는 일이므로 알고리즘과 상수를 바꾸지 않으며, Python 쪽에서 버그로 의심되는 곳은 고치지 않고 '진행 기록'에 적는다. 진행 기록의 뜻과 내용은 '진행 기록' 절에 있다. 다만 volume/slices.py가 matplotlib으로 축과 눈금을 붙여 저장하는 PNG 그림은 단계 0에서 포팅 여부를 정한다.

폴더와 프로젝트 구성

C# 코드는 저장소 최상위의 csharp/ 폴더에 두고, 계산 라이브러리와 콘솔 프로그램과 테스트를 서로 다른 프로젝트로 나눈다. csharp/Bpcg/는 Godot에 의존하지 않는 계산 라이브러리이고, csharp/Bpcg.Cli/는 콘솔 프로그램, csharp/Bpcg.Tests/는 xUnit 테스트 프로젝트이다. engine/에는 Godot .NET 판의 프로젝트 파일을 새로 만들고, 프로젝트 파일이 ProjectReference로 csharp/Bpcg/를 참조하게 한다. ProjectReference는 다른 C# 프로젝트를 함께 빌드해 쓰게 하는 참조이다. 계산 라이브러리를 engine/ 밖에 두는 이유는 Godot 프로젝트가 폴더 안의 모든 .cs 파일을 함께 컴파일하기 때문이다. 대상 프레임워크는 모든 프로젝트에서 net10.0으로 맞추는데, .NET 8과 .NET 9는 2026년 11월 10일에 지원이 끝나고 Godot 4.8은 net10.0을 최소 요구 사항으로 삼을 예정이기 때문이다. Godot 4.7.2 .NET 판에서 net10.0 빌드가 되는지는 단계 0에서 확인하고, 다른 배치가 낫다고 판단하면 이유를 적어 제안한다.

코드 대응 규칙

Python 모듈 하나는 같은 이름의 C# 파일 하나로 옮기고, 모듈 경로는 namespace로 옮긴다. 모듈 수준 함수는 정적 클래스의 메서드가 되고, dataclass는 class나 record가 된다. 그 예로서 bpcg.hydro.routing의 topo_order는 Bpcg.Hydro.Routing.TopoOrder가 된다. 필드 이름, 설정 키, 파일 이름처럼 파일과 manifest에 나타나는 문자열은 Python과 같은 철자를 지킨다. 셀마다 하나인 필드는 길이 N의 1차원 배열로 두고, strata_*처럼 (N, L) 모양인 필드는 행 우선 순서의 1차원 배열로 둔다. 원소 형은 FIELDS의 dtype에 맞춰 float, double, byte, int, long 가운데에서 고르고, numba 커널은 같은 순서로 도는 반복문으로 옮긴다. prange 반복만 Parallel.For로 옮기며, 스레드 수에 따라 결과가 바뀌어서는 안 된다. 묶음 사이의 의존 방향은 docs/conventions.md 1절을 따르고, 계산 코드에서는 Godot의 Vector3 같은 엔진 자료형을 쓰지 않는데, 공식 빌드의 엔진 자료형이 단정밀도이기 때문이다.

외부 라이브러리 대체

scipy, scikit-image, trimesh가 하던 일은 C#으로 직접 구현하는 것을 기본으로 한다. 직접 구현할 대상은 KD-tree의 query·query_ball_point·query_pairs 질의, gaussian_filter와 gaussian_filter1d, distance_transform_edt이다. find_peaks의 prominence 계산, RegularGridInterpolator의 선형 보간과 범위 밖 외삽, brentq, marching cubes, glb 쓰기도 직접 구현한다. 각 구현은 원래 라이브러리의 경계 처리와 기본 인자까지 같게 만들고, 같은 입력에서 낸 golden 자료와 대조한다. .npy와 .npz 읽기·쓰기도 직접 구현하는데, C#이 쓴 묶음을 Python에 남는 studio와 분석 스크립트가 그대로 읽어야 하기 때문이다. NuGet 패키지는 MIT·BSD·Apache-2.0 라이선스만 쓰며, 패키지를 더할 때는 이유와 라이선스를 진행 기록에 적는다. TOML 읽기처럼 직접 구현할 이유가 적은 기능에는 패키지를 써도 된다. matplotlib 색표는 같은 RGBA 값을 담은 표로 옮겨, bake/textures.py의 PNG와 같은 픽셀 값을 낸다.

정수와 정렬의 차이

Python의 //와 %는 내림 몫과 나누는 수의 부호를 따르는 나머지를 내지만, C#의 /와 %는 0 쪽으로 자른 몫과 나뉘는 수의 부호를 따르는 나머지를 낸다. 따라서 음수가 들어올 수 있는 나눗셈은 Python과 같은 결과가 나오도록 따로 맞춘다. np.lexsort, kind="stable" 정렬, Python의 sorted는 같은 값의 원래 순서를 지키는 안정 정렬이지만, C#의 Array.Sort는 불안정 정렬이다. 정렬은 안정 정렬로 옮기고, 같은 높이의 순서를 셀 번호로 정하는 저장소 규칙까지 재현한다. NaN과 inf를 정수로 바꾸는 동작은 numpy와 .NET이 다르므로, Python 코드가 기대는 결과를 조건문으로 직접 만든다. splitmix64 해시는 ulong 연산의 넘침을 그대로 두도록 unchecked 문맥에서 계산한다.

실수 계산의 차이

numpy의 sum과 mean은 pairwise summation으로 더하므로, 앞에서부터 차례로 더하는 C# 반복문과 마지막 자리가 다를 수 있다. pairwise summation은 배열을 반으로 나누어 각각 더한 뒤 두 합을 다시 더하는 방식이며, numba 커널 안의 합은 앞에서부터 차례로 더한다. 합이 분기 조건에 쓰이면 numpy의 pairwise 구현을 그대로 옮기고, 그렇지 않으면 근거를 단 허용 오차로 다룬다. 형 승격도 다른데, NumPy 2가 따르는 NEP 50 규칙에서는 float32 배열과 Python 실수의 연산 결과가 float32로 남는 반면 C#에서 float와 double을 섞으면 double이 된다. numba 커널 안의 형 승격은 numpy 배열 연산과 또 다르므로, 연산이 어느 쪽에서 일어나는지 보고 C# 형을 고른다. 계산 순서를 바꾸는 최적화와 Math.FusedMultiplyAdd는 쓰지 않고, np.percentile과 np.median은 numpy의 기본 선형 보간 정의를 따른다. manifest.json의 config_digest는 Python json.dumps(sort_keys=True, ensure_ascii=False)의 출력을 SHA-256으로 해시한 16진 문자열의 앞 12자이므로, 실수 표기까지 같은 문자열을 만들어야 같은 값이 나온다.

golden 자료와 대조 시험

golden 자료는 새 Python 스크립트 csharp/golden/export_golden.py가 만들고, uv run python csharp/golden/export_golden.py 한 번으로 언제든 다시 만들 수 있어야 한다. golden 자료 스크립트는 src/bpcg의 함수를 부르기만 하고, 출력은 git에서 빠지는 out/golden/에 쓰며, CI 린트를 통과하도록 ruff format과 ruff check를 지킨다. 입력은 몇 초 안에 끝나는 configs/profiles/tiny.toml 크기를 쓰고, 경계 사례가 필요하면 손으로 만든 작은 입력을 더한다. 대조 시험은 함수와 numba 커널 단위로 따로 두는데, 앞 단계의 작은 차이가 뒤 단계로 번지면 원인을 찾을 수 없기 때문이다. 각 대조 시험은 Python이 받은 입력을 그대로 받아 출력만 비교하고, 끝에서 끝 비교는 단계 3에서 따로 한다. 커밋하는 golden 자료는 docs/conventions.md의 규칙대로 1 MB 이하로 제한하고, .gitignore가 *.npy와 *.npz를 막으므로 예외 규칙도 함께 고친다.

일치 기준

정수, 범주, 불리언, 셀 번호, 흐름 방향, 정렬 순서는 완전히 같아야 한다. 사칙연산과 제곱근만 쓰는 실수 계산은 연산 순서와 형이 같으면 비트 단위로 같아야 하는데, IEEE 754가 연산 결과의 반올림을 하나로 정하기 때문이다. 지수·로그·삼각 함수를 거친 값은 언어마다 수학 함수의 구현이 달라 마지막 자리가 다를 수 있으므로, 근거를 주석으로 남긴 상대 오차 안에서 같으면 된다. 묶음 전체를 비교할 때는 docs/conventions.md 3절이 엔진 비교에 쓰는 기준과 같이 float32 정밀도를 기준으로 삼는다. 반복 풀이에서 거의 같은 높이 때문에 흐름 방향이 갈리면, 허용 오차를 넓히지 않고 갈린 셀의 수와 위치와 원인을 진행 기록에 적는다.

진행 기록

'진행 기록'은 docs/csharp_port.md 한 파일이며, 작업이 여러 대화에 걸쳐도 이어 갈 수 있도록 현재 상태를 담는다. 진행 기록에는 Python 모듈과 C# 파일의 대응표, 모듈별 상태와 대조 결과, 외부 라이브러리 대체 방식, 결정한 사항과 이유, Python 쪽 의심 버그, 남은 일을 적는다. 지시 원문이 docs/csharp_port_prompt.md에 없으면 단계 0에서 원문 그대로 저장한다. 저장소 최상위 CLAUDE.md에는 C# 포팅 작업을 이어 갈 때 지시 원문과 진행 기록을 먼저 읽는다는 한 줄을 더한다. 진행 기록은 모듈 하나를 마칠 때마다 갱신하며, 대화 맥락이 압축되더라도 진행 기록만 읽으면 다음 할 일을 알 수 있어야 한다.

작업 순서
단계 0, 조사와 계획: 문서와 코드를 읽고 진행 기록의 초안을 쓴다. 초안에는 대응표, 외부 라이브러리 대체 계획, 모듈별 수치 함정, dotnet --info로 본 SDK 상태, Godot 4.7.2 .NET 판의 net10.0 빌드 확인 결과, 고칠 규칙·설치 스크립트·CI의 목록이 들어간다. .NET SDK가 없으면 설치 방법을 보고하고, 코드는 쓰지 않은 채 멈추어 검토를 기다린다.
단계 1, 뼈대와 core: csharp/ 아래 프로젝트, golden 자료 스크립트, .npy·.npz 읽기·쓰기, 비교 도우미를 만들고 core를 옮긴다. core의 코드가 이후 모든 모듈의 본보기가 되므로 다시 멈추어 검토를 받는다.
단계 2, 묶음별 포팅: hydro → planet → geology → landscape → subsurface → metrics → pipeline·hero → volume → bake → cli 순서로 옮긴다. pipeline과 hero는 서로를 부르므로 함께 옮기고, 묶음 하나가 대조 시험을 통과할 때마다 진행 기록을 갱신해 커밋한다.
단계 3, 끝에서 끝 대조: C# 콘솔 프로그램으로 tiny 프로필의 all 명령을 돌려, Python이 만든 행성 묶음·히어로 묶음·회랑 파일과 비교한다. 단계별 실행 시간도 Python과 나란히 진행 기록에 적는다.
단계 4, Godot 연결: 시작하기 전에 한 번 더 확인을 받은 뒤 engine/을 .NET 판 프로젝트로 바꾸고, C# 노드가 생성과 굽기를 주 스레드 밖에서 부르게 한다. 내보낸 게임에서는 res://에 쓸 수 없으므로 실행 중 결과는 user:// 아래에 쓰고, 기존 GDScript는 BakedPaths가 그 폴더를 읽게 하는 정도로만 고친다. 설치 스크립트, CI의 use-dotnet 설정과 dotnet test 실행, docs/conventions.md와 engine/README.md의 '표준판' 규칙도 같은 단계에서 고친다.
작업 방식

작업은 feat/csharp-port 브랜치에서 하고, 커밋 메시지는 종류(범위): 한국어 요약 형식을 따른다. 커밋하기 전에는 dotnet build와 dotnet test가 통과해야 하고, uv run pytest -m "not slow"도 깨지지 않아야 한다. C# 서식은 dotnet format으로 맞추고 규칙은 .editorconfig에 더하며, 주석·문서·커밋 메시지의 언어는 docs/conventions.md의 규칙을 따른다. push와 PR 생성은 확인을 받은 뒤에만 한다. 대조 시험이 실패했는데 원인을 찾지 못한 경우, 지시와 저장소 규칙이 충돌하는 경우, 범위를 넓혀야 하는 경우에는 멈추고 질문한다. 테스트를 통과시키려고 허용 오차를 넓히거나 시험을 건너뛰는 일은 하지 않으며, 멈출 때는 한 일과 대조 결과와 결정이 필요한 쟁점을 짧게 보고한다.

다루지 않는 범위와 의의

포팅 작업은 studio, analysis/, tools/, Python 테스트를 옮기지 않으며, 알고리즘 개선과 엔진 안에서 파일 대신 메모리로 결과를 넘기는 연결도 다루지 않는다. 그 대신 포팅이 끝나면 Godot .NET 판 안에서 Python 없이 행성을 만들고 구울 수 있다. C#이 쓴 파일은 Python 결과와 형식이 같으므로, 기존 GDScript 장면과 Python studio도 그대로 쓸 수 있다. 또한 golden 자료와 대조 시험이 남으므로, 이후 Python 코드가 바뀌어도 같은 방법으로 C# 코드가 따라왔는지 확인할 수 있다.
