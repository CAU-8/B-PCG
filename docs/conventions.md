# B-PCG 개발 규칙

세 사람이 같은 저장소에서 일하기 위한 규칙입니다. 처음 정한 날은 2026-10-02이고, 바꾸려면 PR로 이 파일을 고칩니다. 일하는 순서(브랜치, PR, 리뷰)는 [CONTRIBUTING.md](../CONTRIBUTING.md)에 있습니다.

## 한눈에 보기

| 항목 | 규칙 |
|---|---|
| 이름 | 폴더·GitHub 저장소·배포 이름은 `b-pcg`, 파이썬 import 이름은 `bpcg` |
| 파이썬 | 3.13 고정(`.python-version`). uv로만 설치하고 `uv.lock`을 커밋합니다 |
| 언어 | 변수·함수·파일 이름은 영어. 주석·docstring·문서·커밋·PR은 한국어(합니다체). README 첫 문단만 영어 요약 |
| 스타일 | `ruff format` + `ruff check`(한 줄 100자). CI가 통과해야 합칩니다 |
| 단위 | SI에 시간만 연(yr): m, yr, m/yr, m³/yr, kg/m³. 각도는 안에서 라디안, 입출력에서만 도 |
| 격자 | 가이드 1장 큐브스피어 규칙. `n`은 면 한 변의 칸 수, `n_cells = 6·n²`, 셀 번호 `c = f·n² + j·n + i` |
| 경로 | 코드에 절대 경로를 쓰지 않습니다. `bpcg.core.paths`로만 찾습니다 |
| 데이터 | `data/`는 올리지 않습니다(README와 manifest만). 커밋하는 바이너리는 1 MB 이하 |
| 라이선스 격리 | GPL 도구(fastscapelib, TopoToolbox, pysheds, richdem)는 `analysis/`에서만 import합니다. ruff가 막습니다 |
| 재현성 | 난수는 시드를 받은 `np.random.Generator`로만. 지형 모양을 바꾸는 노이즈는 정수 해시로 |
| git | main 직접 커밋 금지. 기능 브랜치 → PR → 리뷰 1명 → squash 합치기 |
| 커밋 메시지 | `종류(범위): 한국어 요약`. 예: `feat(hydro): 우선순위 홍수 채우기 추가` |

## 1. 폴더 구조

```text
b-pcg/
├── src/bpcg/            생성기 본체 (파이썬 패키지)
│   ├── core/            격자·이웃·거리·행성 상수·노이즈·경로 (가이드 1장, 공통 뼈대)
│   ├── earth/           진짜 지구 자료 읽기, 격자 위 지구 기준선
│   ├── planet/          사슬 1 뼈대: 판·지각·해양저·해수면·융기, 가벼운 기후
│   ├── geology/         사슬 2 암석: 3D 지질 템플릿, 변성 정도 표
│   ├── hydro/           물길: 웅덩이 채우기, D8 + 노드 흔들기, 유량 누적 (가이드 2장)
│   ├── landscape/       정상상태 솔버, G-법칙 퇴적, 선상지 후처리 (가이드 4장)
│   ├── subsurface/      사슬 3 내부: 흙, 지하수면, 2층 동굴
│   ├── volume/          3D 샘플 함수 (가이드 7장)
│   ├── bake/            묶음·회랑 메시·높이맵을 엔진용 파일로 쓰기
│   └── metrics/         점수표 지표
├── tests/               pytest. fixtures/ 에 1 MB 이하 시험 자료
├── analysis/            한 번 돌리는 연구 스크립트. GPL 도구 허용. src 가 여기를 import 하면 안 됨
├── tools/               설치 전에도 도는 스크립트 (자료 받기, 환경 점검)
├── scripts/             설치 스크립트 (setup.sh, setup.ps1)
├── configs/             planets/(행성 값), profiles/(해상도), learned/(맞춘 값)
├── engine/              Godot 4.7.2 프로젝트. 구운 파일을 불러와 보여 주기만 함
├── docs/                규칙, 개발 단계, 설계도 사본, 가이드, 그림, 라이선스 원문
├── data/                (git 제외) pilot/ external/ derived/ cache/ logs/
├── out/                 (git 제외) 생성 결과
└── .tools/              (git 제외) Godot 실행 파일 등
```

**의존 방향.** 아래쪽이 위쪽을 import할 수 있고 거꾸로는 안 됩니다. `metrics`는 무엇이든 import할 수 있지만, `src` 안에서 `metrics`를 import하는 곳은 없어야 합니다.

```text
core → earth, planet, geology, hydro → landscape → subsurface → volume → bake
```

`analysis/`와 `tools/`는 `src/bpcg`를 쓸 수 있지만, `src/bpcg`가 이 둘을 import해서는 안 됩니다.

**무엇을 어디에 두나.**

- 계산 함수(과학)는 파일을 읽거나 쓰지 않습니다. 읽기는 `earth/`, 쓰기는 `bake/`에서만 합니다.
- 파일 하나가 500줄을 넘으면 나누는 것을 검토합니다.
- 실험 코드는 `analysis/`에서 시작하고, 테스트를 붙여 `src/bpcg`로 옮깁니다.

## 2. 이름 짓기

| 대상 | 규칙 | 예 |
|---|---|---|
| 모듈·함수·변수 | snake_case, 함수는 동사로 시작 | `priority_flood`, `fill_depressions` |
| 클래스 | PascalCase | `Grid`, `CellGraph` |
| 상수 | UPPER_SNAKE | `FACE_U`, `EARTH_RADIUS_M` |
| 참·거짓 | `is_`, `has_` 로 시작 | `is_ocean`, `has_cave` |
| 테스트 | `tests/test_<모듈>.py`, `test_<무엇이_어떻다>` | `test_area_sum_equals_sphere` |
| 브랜치 | `종류/짧은-영어-설명` | `feat/priority-flood`, `fix/seam-areas` |

**수식 기호.** 설계도 2장 표의 기호를 ASCII로 그대로 씁니다. 아래 기본 단위와 다를 때만 이름 끝에 단위를 붙입니다(`_km`, `_mm_per_yr`, `_myr`).

| 기호 | 코드 이름 | 뜻 | 기본 단위 |
|---|---|---|---|
| z | `z` | 고도(해수면 기준) | m |
| Q | `Q` | 유량 | m³/yr |
| A | `A` | 셀 면적 또는 상류 면적 | m² |
| S | `S` | 경사(높이/거리) | m/m |
| k_s | `k_s` | 강의 가파름 | Q의 단위에 맞춤 |
| θ | `theta` | 하류로 갈수록 완만해지는 정도 | 없음 |
| S_crit | `s_crit` | 산비탈이 버티는 가장 가파른 경사 | m/m |
| U | `U` | 융기 속도 | m/yr |
| P | `P` | 강수 | m/yr |
| d | `d` | 이웃 사이 거리 | m |

한 글자 이름은 수식 기호와 격자 첨자(`f`, `i`, `j`, `c`, `n`)에만 씁니다.

## 3. 단위와 정밀도

- 안쪽 계산은 m, yr, m/yr, m³/yr, kg/m³, 라디안입니다. mm/yr, 도, km는 입력을 읽을 때와 그림·보고서에서만 바꿉니다.
- 함수가 받는 배열의 단위는 docstring에 `[m]`처럼 적습니다.
- 솔버 고도와 웅덩이 채우기 높이는 float64입니다. 채우기에 쓰는 기울기 ε = 1e-3 m는 float32에서 사라지기 때문입니다.
- 엔진으로 넘기는 파일은 float32로 저장합니다(높이맵, 메시).
- 파이썬과 엔진의 값 비교는 float32 정밀도에 맞춥니다. 0.1 mm 일치 시험은 하지 않습니다.

## 4. 격자와 좌표

- **면 기저.** `bpcg.core.cubesphere`의 `FACE_U`, `FACE_V`, `FACE_N`이 기준입니다(가이드 1장 표, 오른손 좌표계 u×v = n). 묶음 manifest에도 이 표를 그대로 씁니다.
- **셀 번호.** `c = f·n² + j·n + i`입니다. i는 u 방향, j는 v 방향이고, 셀 중심은 `a_i = -1 + (2i+1)/n`입니다.
- **배열 모양.** 셀마다 하나인 값은 `(n_cells,)` 평평한 배열로 저장합니다. 면별로 볼 때만 `reshape(6, n, n)`으로 `[f, j, i]`를 씁니다.
- **경위도.** `earth/`의 입출력 경계에서만 씁니다(도 단위). 안쪽은 단위 벡터입니다.
- **엔진 좌표.** 파이썬이 회랑 기준점에서 지역 좌표로 바꿔 굽습니다. Godot 좌표는 X = 동, Y = 위, Z = 남입니다. 기준점은 manifest에 적습니다. 자세한 형식은 [engine/README.md](../engine/README.md)에 있습니다.

## 5. 코드 스타일

- **포맷과 린트.** 저장할 때 `ruff format`을 돌리고, `ruff check`를 통과시킵니다(설정은 `pyproject.toml`).
- **타입 힌트.** 다른 모듈이 부르는 함수에는 타입 힌트를 붙입니다.
- **docstring.** 첫 줄에 한 문장 요약을 쓰고, 단위와 배열 모양을 적습니다.

    ```python
    def fill_depressions(z: np.ndarray, nbr: np.ndarray) -> np.ndarray:
        """웅덩이를 채워 모든 셀이 바다로 흐르게 합니다.

        z: (n_cells,) 고도 [m], float64. nbr: (n_cells, 8) 이웃 셀 번호, 없으면 -1.
        반환: 채운 고도 [m], 늘 z 이상.
        """
    ```

- **주석.** '무엇'보다 '왜'를 씁니다. 근거가 논문이면 `# Yuan 2019 식 (7)`처럼 출처를 남깁니다.
- **Numba.**
    - 커널은 모듈 최상위 함수에 `@njit(cache=True)`를 붙입니다.
    - 커널에는 배열과 숫자만 넘깁니다. dict나 dataclass는 커널 밖에서 풉니다.
    - `prange`는 경쟁 조건이 없을 때만 씁니다.
    - 같은 높이일 때의 순서는 셀 번호로 정해 결과가 늘 같게 합니다.
- **예외.** 입력이 잘못되면 한국어 메시지로 `ValueError`를 냅니다. `assert`는 테스트와 내부 불변식에만 씁니다.

## 6. 재현성과 설정

- **설정 파일.** 값은 `configs/`의 TOML에서 읽습니다. 행성 값은 `planets/`, 해상도는 `profiles/`, 맞춘 값은 `learned/`에 둡니다. 코드 안에 숫자를 흩어 두지 않습니다.
- **난수.** 시드를 받은 `np.random.Generator`만 씁니다. 전역 `np.random.seed`는 쓰지 않습니다.
- **노이즈.** 지형 모양을 바꾸는 노이즈와 노드 흔들기는 정수 해시로 만듭니다. numpy 난수열은 버전마다 달라질 수 있어서(NEP 19), 맥과 연구실 PC가 같은 행성을 만들려면 해시가 필요합니다.
- **기록.** 생성 결과의 manifest에는 git 커밋, 설정 파일 내용, 시드, bpcg 버전을 적습니다.

## 7. 테스트

- **도구와 위치.** pytest를 씁니다. `src/bpcg/<묶음>/<모듈>.py`를 시험하는 파일은 `tests/test_<모듈>.py`입니다.
- **표시(marker).**

    | 표시 | 뜻 |
    |---|---|
    | `slow` | 오래 걸림 |
    | `data` | `data/pilot`이 필요함, 없으면 건너뜀 |
    | `godot` | Godot이 필요함, 없으면 건너뜀 |

    CI는 `-m "not slow"`로 돕니다.
- **허용 오차.** 오차 기준에는 근거를 주석으로 남깁니다. 예: `# 가이드 1장: 면적 합 상대 오차 < 1e-12`.
- **시험 자료.** `tests/fixtures/`에 1 MB 이하로 두고, 출처를 `tests/fixtures/README.md`에 적습니다.
- **GPL 도구와 비교.** `analysis/`에서 미리 계산해 결과만 `tests/fixtures/`에 저장합니다. 테스트는 GPL 도구를 import하지 않고 저장된 결과와 비교만 합니다.
- **완료의 뜻.** 과학 모듈은 테스트가 있고, 데모 빌드에 들어가야 '완료'입니다(설계도 8장).

## 8. 데이터와 파일

- **폴더 역할.**

    | 폴더 | 내용 |
    |---|---|
    | `data/pilot/` | 스크립트로 다시 받을 수 있는 자료 |
    | `data/external/` | 계정이 필요하거나 손으로 받은 자료(AfriSAR) |
    | `data/derived/` | 분석 중간 결과 |
    | `data/cache/` | 지워도 되는 파일 |

    목록과 크기, 해시는 `data/manifest.json`에 있습니다. 자세한 설명은 [data/README.md](../data/README.md)에 있습니다.
- **커밋하는 것.** 원자료, zip, GeoTIFF, GeoJSON, 큰 npz는 커밋하지 않습니다. 커밋하는 바이너리는 시험 자료와 문서 근거용 작은 결과(각 1 MB 이하)뿐입니다. 그보다 큰 파일은 먼저 팀에 묻습니다(Git LFS는 아직 쓰지 않음).
- **다른 디스크에 둘 때.** 큰 자료는 `BPCG_DATA=/다른/디스크/data`로 옮겨 쓸 수 있습니다.
- **결과 파일.** 생성 결과는 `out/`에, 엔진용 결과는 `engine/baked/`에 씁니다. 둘 다 git에서 빠집니다.

## 9. 라이선스

- **코드 라이선스.** 아직 정하지 않았습니다(개발 단계 0단계의 남은 일). 정하면 `LICENSE`에 적고, 데이터 라이선스와 분리합니다.
- **GPL 도구.** `analysis/`에서만 씁니다. 핵심 코드는 MIT인 pyflwdir과 직접 짠 numba 코드로 만듭니다.
- **연구 코드.** Tzathas 2024 코드는 비상업 연구용이라 가져다 쓰지 않습니다. 논문을 보고 다시 구현합니다.
- **데이터.** 출처와 의무 문구는 `data/README.md`와 `docs/licenses/`에 있습니다. OCTOPUS로 맞춘 표에는 `CC BY-NC-SA, OCTOPUS 유래`를 적습니다.

## 10. git과 협업 요약

- **브랜치.**
    - `main`은 보호합니다. 직접 커밋하지 않고, PR + 리뷰 1명 승인 + CI 통과 후 squash로 합칩니다.
    - 브랜치 종류는 `feat/`, `fix/`, `docs/`, `test/`, `refactor/`, `perf/`, `chore/`, `data/`, `exp/`입니다.
- **커밋 메시지.**
    - 형식은 `종류(범위): 한국어 요약`입니다. 범위는 패키지 이름입니다(`core`, `hydro`, `engine` 등).
    - 본문에는 왜 바꿨는지를 씁니다. 이슈가 있으면 `#12`처럼 적습니다.
- **PR 크기와 리뷰.** PR은 작게 유지합니다(바뀐 줄 400줄 안팎). 리뷰는 하루 안에 합니다.
- **금요일 통합일.** 그 주의 PR을 합치고, 데모 빌드가 도는지 확인합니다.
- **태그.**

    | 태그 | 날짜 | 시점 |
    |---|---|---|
    | `v0.1-pitch` | 10/23 | 중간 피치 |
    | `v0.2-freeze` | 11/11 | 꼭 할 일 기능 동결 |
    | `v0.3-demo-freeze` | 11/18 | 데모 동결 |
    | `v1.0` | 12/4 | 최종 데모 |

## 11. 환경과 의존성

- **uv만 씁니다.**
    - 설치는 `scripts/setup.sh`(맥·리눅스) 또는 `scripts/setup.ps1`(윈도우)로 합니다.
    - `pip install`은 쓰지 않습니다. `uv.lock`과 실제 설치가 어긋납니다.
- **의존성 묶음.**

    | 묶음 | 내용 | 설치 |
    |---|---|---|
    | 기본 | numpy, scipy, numba | 늘 |
    | `dev` | pytest, ruff, hypothesis, matplotlib | 늘(`uv sync`) |
    | `data` | h5py, xarray, rasterio, pyogrio, geopandas 등 | 늘(`uv sync`) |
    | `mesh` | scikit-image, trimesh | 늘(`uv sync`) |
    | `analysis` | pyflwdir, landlab | `--group analysis` |
    | `gpl` | fastscapelib, topotoolbox | `--group gpl`, analysis 전용 |
    | `notebook` | jupyterlab | `--group notebook` |

- **패키지 추가.** `uv add <패키지> --group <묶음>`으로 더하고, PR 설명에 이유를 씁니다. `uv.lock`도 같이 커밋합니다.
- **지원 환경.**
    - macOS 15 이상(Apple Silicon): 잠근 rasterio 바이너리가 macOS 15용입니다.
    - Windows 10/11 x64.
    - Linux x86_64(glibc 2.28 이상).
    - Linux ARM에서는 `gpl` 묶음을 설치할 수 없습니다.
    - Windows ARM64는 x64 파이썬을 에뮬레이션으로 씁니다(시험 안 함).
    - Intel Mac은 지원하지 않습니다(numba 0.68에 macOS x86_64 바이너리가 없음).
- **파이썬 3.13인 이유.** fastscapelib과 TopoToolbox가 3.14용 바이너리를 내지 않아서, 세 운영체제 모두에서 바이너리로 설치되는 가장 새 버전이 3.13입니다(2026-10-02 확인). 올릴 때는 `tools/check_wheels.py`로 먼저 확인합니다.
- **Godot.** 4.7.2 표준판(.NET 아님)을 모두 같은 버전으로 씁니다. 버전이 섞이면 `project.godot`와 `.import` 파일이 계속 바뀝니다. `scripts/setup.sh --godot`가 `.tools/godot/`에 받습니다.

## 12. 엔진(GDScript)

- **스타일.** Godot 공식 스타일을 따릅니다. 들여쓰기는 탭, 함수·변수는 snake_case, `class_name`은 PascalCase입니다.
- **엔진의 역할.** 지질·물·동굴을 계산하지 않습니다. 파이썬이 구운 파일(`engine/baked/`)만 불러와 보여 줍니다.
- **커밋하는 것.** `*.import`와 `*.uid`는 커밋하고, `.godot/`는 커밋하지 않습니다.
- **스크립트로 실행할 때.** `--headless`로 돌리고, HOME을 임시 폴더로 바꿉니다. 그러지 않으면 내 컴퓨터의 Godot 편집기 설정이 덮어써집니다.

## 13. 문서

- **문서 위치.** 문서는 `docs/`에 한국어로 씁니다. 설계도의 원본은 Claude 문서이고, `docs/design/`은 그 사본입니다. 두 곳이 다르면 원본이 맞습니다.
- **설계 문서 작성 원칙.**
    1. 확정본을 앞에 두고 기록은 부록으로 보냅니다.
    2. 용어는 화면에 보이는 현상으로 먼저 정의합니다.
    3. 내용을 거시 → 중간 → 미시 스케일로 나눕니다.
    4. 기능은 인과 사슬 3개로 설명합니다.
    5. 차별점은 '빈틈 → 해결 → 검증 기준' 표로 씁니다.
- **문서도 함께 고칩니다.** 코드가 바뀌어 문서가 틀리게 되면 같은 PR에서 문서도 고칩니다.
