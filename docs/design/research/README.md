# docs/design/research

설계도(`docs/design/blueprint.md`)의 선행 연구 표와 자료 용량 같은 숫자가 어디서 왔는지 찾을 때 여는 폴더입니다. 설계도를 쓸 때 모은 조사 결과 원본(JSON)입니다. 2026-10-01 의 Claude 조사 세션에서
나온 그대로이고, 내용은 고치지 않았습니다. 설계도와 다르면 설계도가 맞습니다.

| 파일 | 예전 이름 | 내용 |
| --- | --- | --- |
| `prior_art_research.json` | `research.json` | 선행 연구·기존 생성기 조사 (학술 지형 생성, 행성 생성기, 물길·침식 도구) |
| `prior_art_verify.json` | `verify.json` | 위 조사의 반박 검증 (원문 대조, 정정한 주장) |
| `roadmap_synthesis.json` | `roadmap.json` | 범위·일정·위험을 묶은 종합안과 규모 관련 버그 목록 |
| `global_dem_catalog.json` | `global_dem.json` | 전 지구 DEM 목록(GLO-30/90, GEDTM30, FABDEM 등)의 용량·라이선스·받는 법 |
| `afrisar_usage.json` | `afrisar_usage.json` | AfriSAR(ORNL 1577) 자료를 어디에 쓸 수 있는지와 파일럿 결과 |

## 경로 표기

JSON 안의 로컬 경로는 개인 컴퓨터 경로를 지우려고 아래처럼 바꿔 두었습니다(글자만 바꿈, JSON 구조는 그대로).

| 표기 | 원래 뜻 |
| --- | --- |
| `<scratchpad>` | 조사 세션의 임시 폴더 (지금은 없음) |
| `<repo>` | 이 저장소 폴더 |
| `<vault>` | 옵시디언 볼트의 B-PCG 폴더 (`planet_guide.md` 원본이 있던 곳) |

임시 폴더에 있던 스크립트와 결과 중 남길 것은 저장소로 옮겼습니다. 예전 이름과 지금 위치는 아래와 같습니다.

| 예전 (`<scratchpad>/…`) | 지금 |
| --- | --- |
| `afrisar/geocode.py`, `hydro.py`, `chi.py`, `eco.py` | `analysis/afrisar/` 같은 이름 |
| `afrisar/tomo.py`, `ground.py`, `fig.py`, `ch4_stats.py` | `analysis/afrisar/profile_stats.py`, `ground_canopy.py`, `fig_overview.py`, `ch4_reference.py` |
| `ztest/inzip2.py` | `analysis/afrisar/bench_inzip_read.py` |
| `veg/pilot_lope.py`, `diag_lope.py`, `pilot2_lope.py`, `pilot3.py`, `exemplars.py` | `analysis/veg/pilot_lope.py`, `diag_range_artifact.py`, `pilot2_detrended.py`, `pilot3_fourier.py`, `exemplars.py` |
| `s3_list.py`, `verify_demcat/s3sum.py`, `otsum.py`, `cmr.py`, `mydem/bench/bench.py` | `analysis/dem_catalog/s3_list.py`, `s3_prefix_sum.py`, `opentopo_sum.py`, `cmr_granules.py`, `bench_flow.py` |
| `scale.py`, `checks.py` | `analysis/scale/scale_ladder_calcs.py`, `process_checks.py` |
| `wheels.py` (+ `wheels2.py`) | `tools/check_wheels.py` |
| `afrisar/out/*.npz`, `veg/*.npz` | `data/derived/afrisar/`, `data/derived/veg/` (저장소에 안 올림). 작은 결과만 `analysis/*/results/` |
| `afrisar/out/lopeTomoDTM_*` | `data/derived/afrisar/lope_tomo_dtm_*` |
| `afrisar/out/afrisar_overview.png`, `scale_ladder.png` | `docs/figures/` |
| `copdem_license.pdf` | `docs/licenses/copernicus_dem_licence_cdse_2025-02-21.pdf` |

`demcat_agent/s3par.py`, `otlist.py`, `mydem/s3list_my.py`, `veg/pdftxt.py` 처럼 표에 없는 임시 스크립트는 옮기지 않았습니다.
