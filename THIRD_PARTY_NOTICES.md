# 다른 프로젝트에서 가져온 코드와 자료의 라이선스

> 이 저장소의 코드와 문서는 누구나 고쳐 쓰고 다시 나눠 줄 수 있습니다. 다만 다른 프로젝트에서 옮겨 온 함수와 색 목록, 진짜 지구 자료에서 잘라 온 파일은 원래 조건을 따릅니다. 이 문서는 그런 파일이 어디 있고, 어떤 조건을 지켜야 하는지 적습니다. 새 코드나 자료를 넣을 때 지킬 규칙은 [docs/conventions.md](docs/conventions.md) 9절에 있습니다.

## 1. 이 저장소의 라이선스

라이선스 글이 없는 코드는 GitHub에 공개되어 있어도, 남이 복사하거나 고쳐 쓸 수 없습니다. 저작권법은 따로 허락하지 않은 권리를 모두 저작자에게 남겨 두기 때문입니다. 그래서 맨 위의 [LICENSE](LICENSE)에 허락하는 범위를 적었습니다.

- **라이선스.** MIT 라이선스(저작권 문구와 라이선스 글을 함께 남기면 누구나 쓰고, 고치고, 다시 나눠 주고, 팔 수 있게 허락하는 라이선스)입니다. 만든 사람은 아무것도 보증하지 않습니다.
- **덮는 범위.** 코드, 문서, 그림, 스튜디오 로고(`src/bpcg_studio/static/logo.gif`)입니다. 3절의 자료 파일은 빠집니다.
- **저작권자.** `B-PCG contributors`입니다. 이 저장소에 코드와 문서를 쓴 모든 사람을 가리킵니다. 누가 무엇을 썼는지는 git 기록에 있습니다.

그럼 MIT 저장소에 다른 라이선스의 코드를 섞어도 될까요? 2절의 라이선스는 모두 섞을 수 있습니다. 조건은 원래 저작권 문구와 라이선스 글을 함께 남기는 일입니다. 몇몇 라이선스는 원본에서 바꾼 점의 요약도 요구합니다(2절 끝). 그래서 원문을 [docs/licenses/](docs/licenses/)에 둡니다.

## 2. 다른 프로젝트에서 옮겨 온 코드와 색표

C# 생성기는 golden 파일(옛 파이썬 생성기의 결과를 정답으로 저장해 두고, 새 결과가 비트까지 같은지 비교하는 시험 자료)과 비트까지 같은 값을 내야 합니다. 그래서 몇몇 함수는 numpy·scipy·CPython(파이썬 표준 구현)의 계산 순서와 상수표를 그대로 옮겼습니다. 그림과 지형 텍스처의 색은 matplotlib 색표(값을 색으로 바꾸는 표)의 값을 그대로 옮겼습니다.

| 파일 | 옮겨 온 원본 | 라이선스 | 원문 |
|---|---|---|---|
| `src/Bpcg/IO/Toml.cs` | CPython 3.13 `Lib/tomllib`(설정 파일 TOML을 읽는 코드). 원래 tomli 프로젝트의 코드입니다 | MIT, Taneli Hukkinen | 파일 머리 주석 |
| `src/Bpcg/Numerics/NpUfunc.cs` | numpy 2.5.3의 tanh 계산(`loops_hyperbolic.dispatch.cpp.src`)이 쓰는 16구간 표와 다항식. numpy가 Intel SVML에서 바꿔 온 코드입니다 | BSD-3-Clause, NumPy Developers | [numpy_license.txt](docs/licenses/numpy_license.txt) |
| `src/Bpcg/Numerics/NpReduce.cs` | numpy가 배열을 반씩 나눠 더하는 순서(`loops_utils.h.src`의 pairwise 합) | BSD-3-Clause, NumPy Developers | [numpy_license.txt](docs/licenses/numpy_license.txt) |
| `src/Bpcg/Numerics/Interp.cs`의 `Brentq` | scipy의 근 찾기 코드 `optimize/Zeros/brentq.c` | BSD-3-Clause, SciPy Developers | [scipy_license.txt](docs/licenses/scipy_license.txt) |
| `src/Bpcg/Numerics/SciPy.cs`의 가우시안 필터 | scipy.ndimage의 1차원 필터 계산(`NI_Correlate1D`)과 가우시안 커널 | BSD-3-Clause, SciPy Developers | [scipy_license.txt](docs/licenses/scipy_license.txt) |
| `src/Bpcg/Core/Config.cs`의 `DiffLib` | CPython 3.13 `Lib/difflib.py`의 `SequenceMatcher`와 `get_close_matches`. 설정 키를 틀리게 적으면 비슷한 키를 알려 줄 때 씁니다 | PSF 라이선스 2판 | [python_license.txt](docs/licenses/python_license.txt) |
| `src/Bpcg.Figures/Raster.cs`, `src/Bpcg/Compare/CompareRender.cs` | matplotlib의 값 범위 맞추기(`Normalize`, `TwoSlopeNorm`)와 언덕 그림자(`LightSource`의 hillshade와 soft 섞기) 식 | matplotlib 라이선스 | [matplotlib_license.txt](docs/licenses/matplotlib_license.txt) |
| `src/Bpcg/Bake/ColormapData.cs`, `src/Bpcg.Figures/FigureColormapData.cs` | matplotlib 3.11.2 색표의 값. 색표마다 라이선스가 다릅니다(아래 표) | 아래 표 | 아래 표 |

라이선스마다 지킬 조건은 이렇습니다.

- **BSD-3-Clause.** 원래 저작권 문구, 조건 세 줄, 면책 글을 남기면 고쳐 쓰고 나눠 줄 수 있습니다. 원작자의 허락 없이 그 이름을 우리 결과물 홍보에 쓰지 못합니다.
- **PSF 라이선스.** 파이썬 소프트웨어 재단의 라이선스입니다. 저작권 문구를 남기고, 원본에서 무엇을 바꿨는지 짧게 적어야 합니다.
- **matplotlib 라이선스.** PSF 라이선스를 바탕으로 쓴 라이선스라 조건이 같습니다.

색표마다 만든 곳과 라이선스는 이렇습니다. `BpcgOcean`, `BpcgLand`(구운 지형 텍스처의 바다·육지 색)는 이 저장소가 정한 색이라 MIT입니다.

| 색표 | 쓰는 파일 | 만든 곳 | 라이선스 | 원문 |
|---|---|---|---|---|
| viridis, magma | 두 파일 모두 (magma는 `FigureColormapData.cs`만) | Nathaniel J. Smith, Stéfan van der Walt, Eric Firing | CC0 1.0(저작권을 포기한다는 선언. 출처 표시만 부탁함) | 없음 |
| Blues, YlGnBu | 두 파일 모두 (Blues는 `FigureColormapData.cs`만. 지형 색표 `Terrain`의 앞쪽 128색도 Blues) | Cynthia Brewer, Mark Harrower, 펜실베이니아 주립대(ColorBrewer) | Apache-2.0(라이선스 글 사본을 함께 줘야 함) | [matplotlib_license_colorbrewer.txt](docs/licenses/matplotlib_license_colorbrewer.txt), [apache_license_2.0.txt](docs/licenses/apache_license_2.0.txt) |
| gist_earth | `FigureColormapData.cs` (지형 색표 `Terrain`의 뒤쪽 128색도 gist_earth) | David H. Munro(Yorick·Gist), 캘리포니아 대학교 | Yorick 라이선스(BSD와 비슷하게 저작권 문구를 남기면 쓸 수 있음) | [matplotlib_license_yorick.txt](docs/licenses/matplotlib_license_yorick.txt) |
| copper, tab20 | copper는 `FigureColormapData.cs`, tab20은 두 파일 모두 | matplotlib 개발팀. tab20 값은 Vega의 category20 팔레트에서 왔다고 matplotlib가 적어 둡니다 | matplotlib 라이선스 | [matplotlib_license.txt](docs/licenses/matplotlib_license.txt) |

**원본에서 바꾼 점.** PSF 라이선스와 matplotlib 라이선스는 원본을 바탕으로 만든 코드에 무엇을 바꿨는지 짧게 적으라고 합니다.

- **matplotlib.** 색표는 matplotlib 3.11.2가 계산한 조회표 값을 C# 배열로 옮겼고, 값은 고치지 않았습니다. 값 범위 맞추기와 언덕 그림자 식은 C#으로 다시 썼습니다. 이 식으로 그린 그림은 matplotlib 그림과 눈으로 보아 같은 수준까지만 맞춥니다.
- **CPython `difflib`.** 두 함수를 C#으로 옮겼습니다. C# 문자열은 UTF-16 단위로 글자를 세므로, 파이썬과 같게 유니코드 코드 포인트(글자 하나마다 붙은 번호) 단위로 바꿔 비교합니다.

## 3. 진짜 지구 자료에서 잘라 온 파일

이 파일들은 이 저장소가 만든 자료가 아니라서 MIT가 덮지 않습니다. 원자료의 조건을 따릅니다. 원자료 전체를 받는 법은 [data/README.md](data/README.md)에 있습니다.

| 파일 | 원자료 | 조건 | 인용 |
|---|---|---|---|
| `tests/fixtures/lope_tile512_30m.npz`, `tests/fixtures/rabi_tile512_30m.npz` | AfriSAR(NASA가 가봉 숲을 항공 레이더로 찍은 자료) 안의 지형 높이. 높이는 SRTM(2000년에 우주왕복선 레이더로 잰 높이 지도)에서 왔습니다 | NASA 자료라 쓰는 데 제한이 없고, 인용을 요청합니다 | Hawkins 외 2018, doi:10.3334/ORNLDAAC/1577 |
| `analysis/afrisar/results/*.npz`, `analysis/veg/results/lope_exemplar_profiles.npz`, `docs/figures/afrisar_overview.png` | AfriSAR에서 계산한 결과와 그림 | 위와 같음 | 위와 같음 |
| `configs/pilot_basins.csv` | OCTOPUS v2(강 모래로 잰 유역의 침식 속도 모음)에서 고른 유역 40개의 행 | 라이선스를 아직 확인하지 못해, 더 엄격한 CC BY-NC-SA 4.0으로 다룹니다. 출처를 밝히고, 상업적으로 쓰지 않고, 다시 나눠 줄 때 같은 조건을 붙입니다 | Codilean 외 2022, doi:10.5194/essd-14-3695-2022 |
| `docs/licenses/copernicus_dem_licence_cdse_2025-02-21.pdf` | Copernicus DEM(유럽 연합과 유럽 우주국 ESA의 높이 지도)의 라이선스 문서 | 문서를 낸 곳의 저작물입니다. 참고용으로 둡니다 | — |
| `docs/licenses/*.txt` | 2절 라이선스의 원문 | 원문을 낸 곳의 저작물입니다 | — |

## 4. 설치해서 쓰는 패키지

아래 패키지는 저장소에 들어 있지 않고, 각자 컴퓨터에 설치할 때 받습니다. 그래서 저장소에 원문을 둘 의무는 없고, 라이선스만 적어 둡니다. 판은 잠금 파일(`packages.lock.json`, `uv.lock`)을 따릅니다.

| 쓰는 곳 | 패키지 | 라이선스 |
|---|---|---|
| 생성기 본체 `src/Bpcg` | 없음 | — |
| 결과 그림 `src/Bpcg.Figures` (명령줄 `src/Bpcg.Cli`와 C# 시험도 이 프로젝트를 거쳐 씀) | ScottPlot, SkiaSharp, HarfBuzzSharp, PureHDF | MIT |
| 엔진 `engine/` | GodotSharp, GodotSharpEditor, Godot.SourceGenerators | MIT |
| C# 시험 `tests/Bpcg.Tests` | xunit.v3과 그 하위 패키지 / 함께 딸려 오는 Microsoft.Testing.Platform 등 | Apache-2.0 / MIT |
| 계산 속도 비교 도구 `tools/ComputeBench` | ILGPU 1.5.3 | University of Illinois/NCSA(MIT와 거의 같은 조건) |
| 파이썬 기본 묶음 (스튜디오는 numpy와 scipy만 부름) | numpy, scipy, numba | BSD |
| 파이썬 `dev` 묶음 | pytest, ruff / hypothesis / matplotlib | MIT / MPL-2.0 / matplotlib 라이선스 |
| 파이썬 `data` 묶음 | h5py, h5netcdf, rasterio, geopandas / xarray, pyarrow / pyogrio | BSD / Apache-2.0 / MIT |
| 파이썬 `mesh` 묶음 | scikit-image / trimesh | BSD / MIT |
| 파이썬 `analysis` 묶음 | pyflwdir, landlab | MIT |
| 파이썬 `gpl` 묶음 | fastscapelib, topotoolbox | GPL-3.0. `analysis/`에서만 부릅니다 |
| 파이썬 `notebook` 묶음 | jupyterlab, ipykernel | BSD |

빌드한 프로그램을 나눠 줄 때는 조건이 하나 더 생깁니다. Godot로 내보낸 게임이나 `dotnet publish` 결과에는 위 패키지와 Godot 엔진이 들어갑니다. 이 실행 파일을 나눠 주려면 그 라이선스 글도 함께 넣어야 합니다. Godot는 크레딧 화면이나 함께 내는 파일에 Godot 라이선스 글과 Godot가 쓰는 제3자 라이선스 목록을 넣으라고 안내합니다([Godot 문서 'Complying with licenses'](https://docs.godotengine.org/en/stable/about/complying_with_licenses.html)).

## 더 볼 곳

- 새 코드나 자료를 넣을 때 지킬 규칙: [docs/conventions.md](docs/conventions.md) 9절
- 받는 자료의 출처와 꼭 붙여야 하는 문구: [data/README.md](data/README.md)
- 시험용 지형 조각을 다시 만드는 법: [tests/fixtures/README.md](tests/fixtures/README.md)
