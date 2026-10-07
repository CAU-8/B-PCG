# analysis/

> 설계도에 적힌 숫자 가운데 "지구 반지름으로 정했다", "레이더 자료로는 θ를 잴 수 없었다" 같은 결정은 한 번씩 돌려 본 계산에서 나왔습니다. 이 폴더는 그 **일회성 연구 스크립트**를 모읍니다. 나중에 누가 "그 숫자는 어디서 나왔나"를 물으면, 아래 표의 '뒷받침하는 주장' 열로 스크립트를 찾아 다시 돌려 볼 수 있습니다. 생성기 본체가 아니라서 여기 코드는 생성기에서 쓰지 않습니다.

## 직접 돌려 보기

자료 없이 도는 스크립트 하나로 시작합니다.

```bash
uv run python analysis/scale/scale_ladder_calcs.py
```

- 맞게 돌았으면: 첫 줄이 `R_km g vesc_kms …` 머리글이고, 그 아래 행성 반지름별(6,371 km부터 20 km까지) 중력·칸 크기 표가 나옵니다. 첫 줄의 `d0_km(n=512)` 19.546이 행성 지도(L0) 칸 크기 약 19.5 km입니다.
- 걸리는 시간: 1초 안.

나머지 스크립트는 대부분 가봉 AfriSAR 자료(약 15 GB, NASA 계정 필요)가 있어야 돕니다. 아래 '필요한 자료'를 봅니다.

## 지킬 것

- **결과는 설계 문서로.** 결과 숫자는 설계도(`docs/design/blueprint.md`)와 조사 기록(`docs/design/research/`)에 옮겨 적습니다. 아래 표의 '뒷받침하는 주장' 열이 그 연결입니다.
- **GPL 도구는 여기서만.** fastscapelib, topotoolbox, pysheds, richdem은 GPL 라이선스라, 쓰면 그 코드도 GPL로 공개해야 할 수 있습니다. 그래서 이 폴더에서만 씁니다. ruff의 GPL import 검사(TID251)도 여기서는 꺼 둡니다.
- **생성기와 스튜디오는 여기를 쓰지 않습니다.** `src/Bpcg`(C# 생성기)와 `src/bpcg_studio` 는 이 폴더를 부르지 않습니다. 여기서 쓸 만한 계산이 나오면 C#으로 다시 짜서 `src/Bpcg` 로 옮깁니다.
- **경로는 `bpcg_studio.paths` 로.** `EXTERNAL`, `CACHE`, `DERIVED` 를 쓰고 절대 경로를 쓰지 않습니다.
- **저장소 맨 위에서 파일로 실행합니다**: `uv run python analysis/<폴더>/<파일>.py …`. 같은 폴더의 모듈은 `from hydro import d8` 처럼 가져옵니다. `analysis/veg` 는 `afrisar_link.py` 를 거쳐 `analysis/afrisar` 의 `hydro`, `extract` 를 씁니다(옆 폴더 경로를 더하는 곳은 그 파일 하나뿐).

## 낯선 말

| 말 | 뜻 |
|---|---|
| AfriSAR TomoSAR | 2016년 가봉 숲 위를 비행기 레이더로 여러 번 지나며 잰 자료. 숲 지붕부터 땅까지 높이별로 레이더 반사 세기가 들어 있습니다 |
| Fourier, Capon | 높이별 반사 세기를 계산하는 두 방법. Fourier는 높이 칸이 8 m로 거칠고, Capon은 더 곱지만 파일이 큽니다 |
| HH, HV | 레이더 전파의 보내고 받는 방향 조합. HV는 나뭇가지처럼 어지러운 곳에서 세게 돌아옵니다 |
| 지오코딩 | 레이더가 잰 비스듬한 좌표를 지도 좌표(위경도)로 옮기는 일 |
| HAND | 가장 가까운 물길보다 몇 m 높은지(Height Above Nearest Drainage) |
| χ (카이) 분석 | 강을 따라 상류 면적으로 가중한 길이(χ)와 강 높이를 견주어, 둘이 곧은 선이 되는 θ(강이 완만해지는 정도)를 찾는 방법 |
| TWI | 땅이 얼마나 젖기 쉬운지 나타내는 값(상류 면적과 경사로 계산) |
| RH95 | 땅에서부터 레이더 반사의 95%가 모이는 높이. 숲 지붕 높이 대신 씁니다 |
| k-means | 비슷한 것끼리 K개 무리로 나누는 방법 |
| S3 버킷 | 아마존 클라우드의 파일 저장소. 공개 DEM 타일들이 여기 있습니다 |

## 폴더

| 폴더 | 내용 |
| --- | --- |
| `afrisar/` | 가봉 AfriSAR TomoSAR(ORNL DAAC 1577) 지형 분석: 지오코딩, 물길, 경사-면적, χ, 4장 기준값 |
| `veg/` | 같은 자료로 본 식생 규칙 파일럿: 수관 높이·HV 전력과 HAND, 레이더 인공물 진단, 수직 구조 유형 |
| `dem_catalog/` | 전 지구 DEM 의 타일 수·용량을 버킷 목록으로만 센 스크립트와 물길 계산 벤치마크 |
| `scale/` | 행성 크기·칸 크기·과정 규칙의 크기 점검 계산 |
| `demos/` | 작은 데모 그림 (`plot_cubesphere.py`) |
| `legacy/` | 옛 설계 가이드의 그림 생성기 (`make_figures.py`, ruff 검사 제외) |
| `*/results/` | 설계도 숫자를 뒷받침하는 작은 결과 파일 (저장소에 올림, 1 MB 미만) |

중간 결과(수 MB~수십 MB)는 `data/derived/afrisar/`, `data/derived/veg/` 에 쓰고 저장소에는 올리지 않습니다.
zip 에서 꺼낸 HDF5 는 `data/cache/afrisar/` 에 두며 지워도 됩니다.

## 스크립트 표

실행 명령은 모두 저장소 루트 기준입니다. 경로의 `D/` 는 `data/derived/`, `C/` 는 `data/cache/afrisar/data/` 입니다.

### afrisar/

| 파일 | 하는 일 | 입력 | 출력 | 실행 | 뒷받침하는 주장 |
| --- | --- | --- | --- | --- | --- |
| `extract.py` | zip 에서 필요한 h5 만 캐시로 꺼냄. 다른 스크립트가 `ensure_member()`, `tomo_file()` 로 자동 호출 | `data/external/afrisar/Polarimetric_height_profile_1577.zip` | `C/*.h5` | `extract.py --list` / `extract.py` (4개, 3.55 GB) / `extract.py --fourier-only` (0.76 GB) | 부록 A-2 '용량' (풀어 쓰는 4개 3.55 GB) |
| `bench_inzip_read.py` | zip 을 풀지 않고 h5py 로 읽는 시간 측정 | zip | 화면 | `bench_inzip_read.py [data/lope-tomo-capon-hh.h5]` | 작은 배열은 zip 안에서 읽어도 되지만 Tomogram 블록은 풀어 읽어야 함 (`afrisar_usage.json`: TerrainHeight 1.7–2.5 s, 128줄 블록 37.6 s) |
| `geocode.py` | 레이더 격자의 TerrainHeight(SRTM)를 30 m 정규 격자로 지오코딩 | `C/<site>-tomo-fourier-hv.h5` | `D/afrisar/<site>_dem_30m.npz` | `geocode.py lope rabi` | 부록 A-2 '30 m 격자로 옮겨' (띠 면적 로페 924, 라비 930 km² 를 출력) |
| `profile_stats.py` | 프로파일 문턱값으로 지면·수관 꼭대기 대용 높이 | `C/*.h5` 하나 | `D/afrisar/<h5>_profile_stats.npz` | `profile_stats.py lope-tomo-fourier-hv.h5 0.3` | 6장 표 'Fourier 높이 칸 8 m' (Fourier 상단은 8 m 로 양자화돼 높이 통계에 못 씀) |
| `ground_canopy.py` | Capon HH 로 지면, HV 누적 95% 로 수관 꼭대기, 차이로 수관 높이 | `C/lope-tomo-capon-hh.h5`, `capon-hv.h5` (2.8 GB) | `D/afrisar/lope_ground_canopy.npz` | `ground_canopy.py` | 부록 A-2 '정정'(SRTM 대비 지면 위치), 그림 (f) '±15 m 흔들림' |
| `hydro.py` | 채움(priority-flood+ε), D8, 유량, 경사-면적 θ 맞춤. `fill_eps`·`d8`·`accumulate` 는 다른 스크립트가 재사용 | `D/afrisar/<이름>_dem_30m.npz` | `D/afrisar/<이름>_hydro.npz` | `hydro.py lope rabi lope_tomo_dtm` | 부록 A-2 'θ 가 0.07~0.13', '단층 보정 DTM 으로도 0.07~0.08' |
| `chi.py` | 잘리지 않은 유역만 골라 300 m 평활 경사-면적과 χ 분석 | `D/afrisar/<이름>_dem_30m.npz` | 화면 | `chi.py lope rabi lope_tomo_dtm` | 부록 A-2 'χ 분석도 구분력이 없었다' (로페는 20 km² 넘는 완전 유역이 0개라 R² 가 NaN) |
| `ch4_reference.py` | 4장 비교용 기준값: 경사 히스토그램, 고도 백분위, 완전 유역 HI | `D/afrisar/<site>_dem_30m.npz` | `D/afrisar/<site>_ch4_reference.npz` (사본: `results/`) | `ch4_reference.py lope rabi` | 부록 A-2 (6) '경사 상위 5%(0.373)와 고도 적분(0.29)' — 로페 **띠 전체** 값임(아래 '알려진 문제') |
| `eco.py` | 수관 높이를 30 m 격자로 보간해 HAND·경사·고도별로 봄 | `D/afrisar/lope_ground_canopy.npz`, `lope_dem_30m.npz`, `C/lope-tomo-fourier-hv.h5` | `D/afrisar/lope_eco_30m.npz` | `eco.py` | 그림 (e) 'ρ = +0.30' |
| `fig_overview.py` | 위 결과를 2×3 그림 한 장으로 | `D/afrisar/` 의 dem·eco·hydro·ground_canopy, `C/lope-tomo-capon-hv.h5` | `D/afrisar/afrisar_overview.png` (`--out` 으로 바꿈) | `fig_overview.py --out docs/figures/afrisar_overview.png` | `docs/figures/afrisar_overview.png`, 부록 A-2 (3) '경고 사례 그림' |

### veg/

| 파일 | 하는 일 | 입력 | 출력 | 실행 | 뒷받침하는 주장 |
| --- | --- | --- | --- | --- | --- |
| `afrisar_link.py` | `analysis/afrisar` 의 `hydro`, `extract` 와 출력 폴더를 이어 줌 (실행용 아님) | - | - | - | - |
| `pilot_lope.py` | 수관 높이·지면 오프셋을 30 m 격자로 모아 HAND·경사·TWI 와 상관·회귀. 토모 지면으로 보정한 DTM 도 만듦 | `D/afrisar/lope_dem_30m.npz`, `lope_hydro.npz`, `lope_ground_canopy.npz`, `C/lope-tomo-capon-hh.h5` | `D/veg/lope_veg_pilot.npz`, `D/afrisar/lope_tomo_dtm_dem_30m.npz` | `pilot_lope.py` | 부록 A-2 (7) '수관 높이 하위 5%가 13.7 m 에서 막힘', 단층 보정 DTM |
| `diag_range_artifact.py` | 수관 높이의 레인지(입사각)·레인지 방향 경사 의존성, HV 전력 분포 | `D/afrisar/lope_ground_canopy.npz`, `C/lope-tomo-fourier-hv.h5` | `D/veg/lope_radar_diag.npz` | `diag_range_artifact.py` | 부록 A-2 (1) '가까운 쪽 44 m, 먼 쪽 26~30 m' |
| `pilot2_detrended.py` | 레인지 추세를 빼고 평탄지에서 수관 높이·HV 와 HAND | `D/afrisar/lope_dem_30m.npz`, `lope_ground_canopy.npz`, `D/veg/lope_veg_pilot.npz`, `lope_radar_diag.npz`, `C/lope-tomo-capon-hh.h5` | `D/veg/lope_radar_detr.npz` | `pilot2_detrended.py` | 부록 A-2 (1) '관계의 절반가량은 촬영 방식 때문' |
| `pilot3_fourier.py` | Fourier HV 만으로 두 사이트를 같은 방법으로: HV 전력·RH95 유사 높이·HAND | `D/afrisar/<site>_dem_30m.npz`, `<site>_hydro.npz`, `C/<site>-tomo-fourier-hv.h5` | `D/veg/<site>_fourier_metrics.npz` | `pilot3_fourier.py lope` / `rabi` | 부록 A-2 (1) '사바나 후보는 하천 위 6 m 안쪽에서 43~48%, 80 m 위에서 12%' (lope 평탄지 low-HV 비율) |
| `exemplars.py` | 지면 기준으로 줄 세운 Capon HV 프로파일을 k-means(K=5)로 유형화 | `D/afrisar/lope_ground_canopy.npz`, `D/veg/lope_radar_detr.npz`, `C/lope-tomo-capon-hv.h5` | `D/veg/lope_exemplar_profiles.npz` (사본: `results/`) | `exemplars.py` | 부록 A-2 (2) '수직 프로파일이 5가지 유형으로 나뉨' |

### dem_catalog/

모두 공개 버킷의 **목록(메타데이터)만** 읽고 래스터는 받지 않습니다. 인터넷이 필요합니다.

| 파일 | 하는 일 | 입력 | 출력 | 실행 | 뒷받침하는 주장 |
| --- | --- | --- | --- | --- | --- |
| `net.py` | HTTPS 도우미 (기본 SSL 설정, 인증서가 안 보이면 certifi). 실행용 아님 | - | - | - | - |
| `s3_list.py` | S3 버킷을 1,000개씩 끝까지 훑어 DEM 타일 수·용량 (curl 사용) | 버킷 이름 | 화면 | `s3_list.py copernicus-dem-90m` | 6장 표 'GLO-90 타일 26,475개, 71.1 GB' |
| `s3_prefix_sum.py` | 위도 접두어별 32 스레드 병렬로 같은 합계 | 호스트, 키 접두어 | 화면 | `s3_prefix_sum.py copernicus-dem-30m.s3.amazonaws.com Copernicus_DSM_COG_10_` | 6장 표 'GLO-30 589.1 GB', GLO-90 값 교차 확인 |
| `opentopo_sum.py` | OpenTopography S3 접두어별 GeoTIFF 수·용량, 누락 지역(N40 E045) 존재 확인 | 접두어 | 화면 | `opentopo_sum.py COP30 COP90` | 6장 'AWS 판에 없는 25개 타일은 OpenTopography 판(776.9 GB)에서' |
| `cmr_granules.py` | NASA CMR 로 NASADEM·SRTMGL1·ASTGTM 그래뉼 수·용량 | 없음 | 화면 | `cmr_granules.py` | `global_dem_catalog.json` 의 NASA DEM 용량 |
| `bench_flow.py` | 합성 3600×3600(GLO-30 타일 하나) DEM 에 채움→D8→유량→χ→경사-면적 시간 측정 | 없음 | 화면 | `bench_flow.py [3600]` | `global_dem_catalog.json` '타일당 약 3.3 s, 전 지구 약 24시간', 6장 계산량 |
| `results/glo90_summary.txt` | GLO-90 버킷 전체 목록 요약 (2026-10-01) | | | | 6장 GLO-90 숫자. 세션 중 `s3_list.py` 의 변형(옮기지 않음)으로 만든 출력이며, 타일 수·DEM 용량은 위 두 스크립트로 다시 셀 수 있음 |
| `results/glo30_aws_missing_vs_glo90_tiles.txt` | GLO-90 에는 있고 AWS GLO-30 에는 없는 타일 25개 | | | | 6장 'AWS 판 GLO-30 에는 아르메니아·아제르바이잔 쪽 타일 25개가 없음' |

### scale/, demos/, legacy/

| 파일 | 하는 일 | 입력 | 출력 | 실행 | 뒷받침하는 주장 |
| --- | --- | --- | --- | --- | --- |
| `scale/scale_ladder_calcs.py` | 반지름별 g·탈출속도·칸 크기·float32 정밀도, 칸 안 기복, 메모리 | 없음 | 화면 | `scale_ladder_calcs.py` | 3장 '지구 반지름 6,371 km', 'L0 19.5 km'(n=512), `docs/figures/scale_ladder.png` 의 숫자 (그림을 그린 코드는 남아 있지 않음) |
| `scale/process_checks.py` | Q* 사면 길이, Roering R*, Heimsath 토양 두께, ELA, 선상지 경사비, 산지 전면 종단면, 삼각주 면적 | 없음 | 화면 | `process_checks.py` | 4·5장 과정 규칙 기본값의 크기 점검 |
| `demos/plot_cubesphere.py` | 큐브스피어 셀 중심을 면별 색으로 찍는 데모 | 없음 | 창 또는 `--save` 파일 | `plot_cubesphere.py --n 16` | 가이드 1장 격자 |
| `legacy/make_figures.py` | 옛 설계 가이드(`docs/guide/planet_guide.md`)의 그림 22장 생성 | 없음 | `docs/guide/figures/*.png` | `uv run python analysis/legacy/make_figures.py` | 가이드 1~8장 그림 |

## 실행 순서

```bash
# 0. (선택) 미리 꺼내기. 안 해도 각 스크립트가 필요할 때 꺼냄
uv run python analysis/afrisar/extract.py --fourier-only      # 0.76 GB
uv run python analysis/afrisar/extract.py                     # 4개 3.55 GB (Capon 2개 포함)

# 1. 지형 (Fourier HV 만 있으면 됨)
uv run python analysis/afrisar/geocode.py lope rabi
uv run python analysis/afrisar/hydro.py lope rabi
uv run python analysis/afrisar/ch4_reference.py lope rabi
uv run python analysis/afrisar/chi.py lope rabi
uv run python analysis/veg/pilot3_fourier.py lope
uv run python analysis/veg/pilot3_fourier.py rabi
uv run python analysis/afrisar/profile_stats.py lope-tomo-fourier-hv.h5 0.3

# 2. 수관 높이 (Capon HH·HV 2.8 GB 필요)
uv run python analysis/afrisar/ground_canopy.py
uv run python analysis/afrisar/eco.py
uv run python analysis/afrisar/fig_overview.py --out docs/figures/afrisar_overview.png

# 3. 식생 파일럿 (ground_canopy 다음)
uv run python analysis/veg/pilot_lope.py            # lope_tomo_dtm_dem_30m.npz 도 만듦
uv run python analysis/afrisar/hydro.py lope_tomo_dtm
uv run python analysis/afrisar/chi.py lope_tomo_dtm
uv run python analysis/veg/diag_range_artifact.py
uv run python analysis/veg/pilot2_detrended.py      # pilot_lope, diag_range_artifact 다음
uv run python analysis/veg/exemplars.py             # pilot2_detrended 다음

# 4. 테스트 고정 자료 다시 만들기 (geocode 다음)
uv run python tools/make_tile512_fixture.py lope rabi
```

`dem_catalog/`, `scale/` 스크립트는 서로 독립이라 아무 때나 돌려도 됩니다.

## 필요한 자료

| 자료 | 위치 | 크기 | 받는 법 |
| --- | --- | --- | --- |
| AfriSAR TomoSAR zip (ORNL DAAC 1577) | `data/external/afrisar/Polarimetric_height_profile_1577.zip` | 약 15 GB | NASA Earthdata 계정으로 ORNL DAAC 에서 받음 (https://doi.org/10.3334/ORNLDAAC/1577) |
| 꺼낸 HDF5 | `data/cache/afrisar/data/` | Fourier HV 2개 0.76 GB + Capon HH·HV 2.8 GB | `extract.py` 가 만듦 (지워도 됨) |
| 중간 결과 | `data/derived/afrisar/`, `data/derived/veg/` | 합계 약 260 MB | 위 순서로 다시 만듦 |

`dem_catalog/` 는 인터넷만, `scale/`·`legacy/`·`demos/` 는 아무 자료도 필요 없습니다.

자료 출처: Hawkins, B.P., N. Pinto, M. Lavalle, and S. Hensley. 2018. AfriSAR: Polarimetric Height Profiles by TomoSAR,
Lope and Rabi Forests, Gabon, 2016. ORNL DAAC. https://doi.org/10.3334/ORNLDAAC/1577

## 다시 돌려 확인한 것 (2026-10-02)

임시 폴더에서 저장소로 옮긴 뒤, 같은 입력으로 다시 돌려 예전 결과와 **비트 단위로 같음**을 확인했습니다:
`geocode.py lope rabi`, `hydro.py lope rabi lope_tomo_dtm`, `ch4_reference.py lope rabi`(`results/` 와 파일까지 같음),
`eco.py`, `diag_range_artifact.py`, `pilot3_fourier.py lope rabi`, `profile_stats.py lope-tomo-fourier-hv.h5 0.3`,
`tools/make_tile512_fixture.py lope rabi`(`tests/fixtures/` 와 파일까지 같음). `chi.py lope` 는 화면 출력이 예전과 같습니다.
`scale/` 두 스크립트는 화면 출력이 예전과 글자 하나까지 같습니다.

Capon 파일(2.8 GB)이 필요한 `ground_canopy.py`, `fig_overview.py`, `pilot_lope.py`, `pilot2_detrended.py`, `exemplars.py` 는
import 만 확인했고 다시 돌리지 않았습니다. 이들의 예전 결과는 `data/derived/` 에 그대로 복사해 두었습니다.

## 알려진 문제

- 설계도 부록 A-2 (6)은 '같은 조각(512 × 512)의 경사 상위 5% 0.373, 고도 적분 0.29' 라고 적었지만, 이 값은
  `ch4_reference.py` 가 잰 로페 **띠 전체** 값입니다. `tests/fixtures/lope_tile512_30m.npz` 조각 자체는 경사 p95 0.387, HI 0.207 입니다.
- `legacy/make_figures.py` 는 한글 글꼴로 숨은 시스템 글꼴(`.Apple SD Gothic NeoI`)을 고를 수 있고, 그러면 수식 안 마이너스(U+2212)가
  네모로 나옵니다. 그래서 `docs/guide/figures/` 에는 볼트에서 옮긴 원본 그림을 그대로 두었습니다.
- `profile_stats.py` 의 기본 문턱값은 0.2 인데, 2026-10-01 에 남긴 Fourier 결과는 0.3 으로 만든 것입니다
  (`lope_tomo_capon_hv_profile_stats.npz` 의 문턱값은 기록이 없습니다).
