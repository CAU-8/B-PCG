# data 폴더

> 생성기 규칙 속 숫자(강이 하류로 완만해지는 정도, 강 가파름, 산비탈이 버티는 경사)는 지금 논문 값입니다. 이 숫자를 진짜 지구에 맞추고, 만든 행성을 진짜 지구와 같은 잣대로 견주려면 진짜 지구 자료가 필요합니다. 이 폴더는 그 자료를 두는 곳입니다. 대부분은 시험용으로 고른 유역 40개 둘레의 자료(파일럿 자료, 약 4.5 GB)입니다. 자료를 맞추거나 견주는 일을 맡지 않았다면 받지 않아도 됩니다.

**원자료는 git에 올리지 않습니다.** 공개 저장소이고, 자료마다 다시 나눠 줄 수 있는 조건(라이선스)이 다르기 때문입니다. 이 폴더에서 저장소에 올라가는 파일은 `README.md` 와 `manifest.json` 둘뿐입니다(`.gitignore` 의 `data/*`). `git add -f` 로 억지로 넣지 않습니다. 자료는 각자 받고, 같은 자료인지는 `manifest.json` 의 크기와 sha256(파일 내용으로 만든 지문)으로 확인합니다.

## 직접 돌려 보기

```bash
uv run python tools/download_pilot.py            # 없는 파일만 받기 (처음이면 약 4.5 GB)
uv run python tools/download_pilot.py --verify-only
```

- 맞게 받았으면: 점검이 `맞음 (크기): 63개` 와 `검사 통과.` 로 끝납니다.
- 걸리는 시간: 점검은 1초 안. 받기는 인터넷 속도에 따라 다릅니다.

## 낯선 말

| 말 | 뜻 |
|---|---|
| DEM | 땅 높이를 바둑판 칸마다 적은 지도(수치 고도 모델) |
| EGM2008 | 높이 0 m의 기준면. 바다 수면을 지구 전체로 이은 울퉁불퉁한 면이고, ETOPO와 GLO-90이 같은 기준을 씁니다 |
| pixel-is-point | 칸 값이 칸 한가운데가 아니라 칸 모서리 점의 값이라는 표시. 그래서 타일 경계가 반 칸 밀립니다 |
| WFS | 지도 도형을 인터넷으로 내려 주는 표준 방식. OCTOPUS가 이렇게 자료를 줍니다 |
| 10Be 침식률 | 하천 모래 속 베릴륨-10(우주에서 날아오는 입자가 땅 겉에서 만드는 원자)의 양으로 잰, 유역이 수천~수만 년 동안 평균으로 깎인 속도 |
| mm/kyr | 1,000년에 깎인 mm. 100 mm/kyr = 1만 년에 1 m |
| `/vsizip/` | zip을 풀지 않고 안의 파일을 바로 읽는 GDAL(지도 파일 도구)의 방법 |

## 폴더 역할

| 폴더 | 담는 것 | 없어지면 |
|---|---|---|
| `pilot/` | 스크립트로 다시 받을 수 있는 파일럿 자료 (약 4.5 GB) | `tools/download_pilot.py` 로 다시 받습니다 |
| `external/` | 계정이 필요하거나 손으로 받은 자료 (지금은 AfriSAR) | 사이트에서 손으로 받습니다 |
| `derived/` | `analysis/` 스크립트가 만든 중간 결과 | 스크립트를 다시 돌립니다 |
| `cache/` | 지워도 되는 임시 파일 (zip 에서 꺼낸 파일 등) | 필요할 때 다시 생깁니다 |
| `logs/` | 내려받기 기록 | 지워도 됩니다 |

코드에서는 이 경로를 직접 쓰지 않고 `bpcg_studio.paths`(파이썬)·`Bpcg.Core.Paths`(C#) 의 `PILOT`, `EXTERNAL`, `DERIVED`, `CACHE` 를 씁니다.

## 자료 목록

크기는 10진 단위입니다 (1 MB = 10^6 B). 장 번호는 설계 문서(구현 가이드)의 장입니다.

| 경로 (`data/` 아래) | 무엇 | 크기 | 출처 | 라이선스 | 얻는 법 | 쓰는 곳 |
|---|---|---|---|---|---|---|
| `pilot/etopo/ETOPO_2022_v1_60s_N90W180_surface.nc` | 지구 전체 고도, 육지+해저, 60초(약 1.8 km) | 478.3 MB | NOAA NCEI | 퍼블릭 도메인 | 스크립트 | 5장 (지구 고도 분포와 비교) |
| `pilot/chelsa/CHELSA_bio12_1981-2010_V.2.1.tif` | 연강수 1981-2010, 30초 | 349.3 MB | CHELSA V2.1 | CC0 (확인 안 됨) | 스크립트 | 3장 (유량 Q = P·A 로 바꿀 때) |
| `pilot/aridity/Global-AI_ET0__annual_v3_1.zip` | 건조 지수(AI)와 잠재 증발산(ET0), 연간 | 645.8 MB | figshare (Zomer 외) | CC BY 4.0 / 비상업 (아래 참고) | 스크립트 | 3장 (기후 구분), 5장 (기후 규칙) |
| `pilot/riveratlas/RiverATLAS_Data_v10.gdb.zip` | 전 세계 하천 구간과 속성 | 2,506.5 MB | HydroSHEDS RiverATLAS v1.0 | CC BY 4.0, 일부 열 ODbL | 스크립트 | 3장 (하천 차수·경사 비교) |
| `pilot/octopus/crn_int_basins.geojson`, `crn_aus_basins.geojson` | 10Be 유역 평균 침식률 5,673개 유역 | 281.6 MB | OCTOPUS v2 (WFS) | CC BY-NC-SA 4.0 으로 취급 (미해결) | 스크립트 | 3장 (침식률 정답값) |
| `pilot/octopus/pilot_basins.csv` | 파일럿 유역 40개 목록 (`configs/pilot_basins.csv` 의 사본) | 6.7 kB | OCTOPUS 에서 골라냄 | OCTOPUS 를 따름 | 스크립트 | 3장 |
| `pilot/glo90/*.tif` (55개) | 파일럿 유역을 덮는 90 m 고도 타일 | 245.9 MB | Copernicus DEM GLO-90 (AWS) | Copernicus DEM 라이선스 (출처 문구 의무) | 스크립트 | 3장 (경사-면적 회귀, θ·k_sn·S_crit) |
| `external/afrisar/Polarimetric_height_profile_1577.zip` | AfriSAR 레이더 숲 높이 단면 (가봉) | 15,105.1 MB | ORNL DAAC 1577 | NASA 공개 자료, 인용 요청 | 손으로 (NASA Earthdata 로그인) | 지금은 없음 (식생 구조 참고 후보) |

파일마다 주소, 크기, sha256, 라이선스는 `manifest.json` 에 있습니다.

## 자료별 주의할 점

2026-10-02 자료 점검에서 확인한 내용입니다. 코드는 SI 단위(m, m/yr, yr)로 바꿔서 씁니다.

**ETOPO 2022**
- 위도가 남에서 북으로 갑니다. 0번 행이 남쪽(-89.99°)이므로 북쪽이 위인 그림을 그리려면 뒤집습니다.
- 빙상은 얼음 윗면 고도입니다 (기반암 아님). 단위 m, 기준 EGM2008. `_FillValue` 는 -99999 이지만 실제로 나오지 않습니다.

**Copernicus GLO-90**
- nodata 가 정해져 있지 않고 바다는 0 입니다. 0 을 바다로 볼지 따로 정해야 합니다. 55개 중 10개 타일은 30% 넘게 바다이고, `N36_00_W123_00` 은 99.3% 가 바다입니다.
- pixel-is-point 입니다 (`AREA_OR_POINT=Point`). 타일 경계가 반 칸 밀려 있습니다 (예: 29.999583 ~ 30.999583).
- float32 1200×1200, 3초 간격, 단위 m, 기준 EGM2008 (ETOPO 와 같음). `S24_00_E031_00` 의 최저값 -255.6 m 는 실제 노천 광산입니다.

**Global Aridity Index v3.1**
- 저장된 AI 값에 **0.0001 을 곱해야** 합니다 (Readme 에 "10,000 을 곱했다"고 적혀 있고, GeoTIFF 에는 배율 태그가 없습니다).
- nodata 가 정해져 있지 않습니다. 바다는 0, 65535 는 아주 습한 칸(예: 체라푼지)에 나오므로 포화값이나 결측으로 다룹니다.
- 기간이 1970-2000 으로 CHELSA(1981-2010)와 다릅니다. ET0 는 mm/yr 입니다.
- zip 을 풀지 않고 GDAL `/vsizip/` 로 바로 읽을 수 있습니다. zip 안의 `__MACOSX/` 항목은 쓰레기 파일입니다.
- 라이선스: figshare 에는 CC BY 4.0 으로 적혀 있지만, zip 안의 Readme 에는 "비상업 이용으로 제공"한다고 적혀 있습니다. 둘 다 기록해 두고, 이 과제는 비상업이므로 쓰되 결과를 공개할 때 이 조건을 함께 적습니다.

**CHELSA bio12**
- 값이 **이미 mm/yr** 입니다 (태그 `kg m-2 year-1`, 배율 없음). 3장 문서의 "WorldClim 월 강수를 합산" 단계는 필요 없습니다. m/yr 로 바꾸려면 1000 으로 나눕니다.
- 북위 84° 까지만 있습니다. nodata 는 65535 이고, 바다도 값이 채워져 있습니다.
- 라이선스 CC0 는 예전 스크립트에 적힌 것이고 온라인으로 다시 확인하지 못했습니다.

**OCTOPUS v2**
- 결측값이 숫자로 들어 있습니다. `EBE_MMKYR`, `AREA`, `SLP_AVE`, `ELEV_AVE` 의 -9999.99, `GLA_PCNT` 의 -99.99 를 먼저 걸러냅니다.
- 도형 중 국제 레이어 301개, 호주 레이어 86개가 유효하지 않습니다. 면적 계산이나 자르기 전에 `make_valid` 를 씁니다.
- 호주 레이어(`crn_aus_basins`)에는 `GLA_PCNT` 열이 없습니다.
- `EBE_MMKYR` 는 mm/kyr 입니다. m/yr 로 바꾸려면 1e-6 을 곱합니다. `SLP_AVE` 는 m/km 로 보이지만 확인하지 않았습니다.
- WFS 는 https 접속이 안 되어 http 로 받습니다. WFS 내용이 바뀔 수 있으므로 다시 받은 파일은 `manifest.json` 과 크기·해시가 다를 수 있습니다 (검사에서 경고만 합니다).

**pilot_basins.csv (파일럿 유역 40개)**
- 기준은 저장소에 올린 `configs/pilot_basins.csv` 이고, `data/pilot/octopus/pilot_basins.csv` 는 그 사본입니다.
- 고르는 규칙: 빙하 없고 면적 10~1000 km² 인 유역을 침식률 순서로 5구간으로 나누고, 구간마다 OBSID1 해시 순서로 아직 안 나온 지역(`REGION_INT`)을 8개씩 고릅니다. 19개 나라, 40개 지역, 침식률 3.99~1262.93 mm/kyr, 면적 10.24~911.31 km², 타일 55개입니다.
- 40개 중 36개는 `GLA_PCNT` 가 -99.99 (빙하 비율 모름)인데, 지금 규칙은 이것을 "빙하 없음"으로 봅니다.
- `EBE_mm_per_kyr` 는 mm/kyr, `AREA_km2` 는 km² 입니다. `tiles` 열은 `;` 로 나눈 GLO-90 타일 이름입니다. 날짜 변경선을 넘는 유역은 `lon_min > lon_max` 로 적습니다 (지금 40개 중에는 없습니다).

**RiverATLAS v1.0**
- `sgr_dk_sav` 열은 없고 `sgr_dk_rav` (하천 경사, **dm/km**) 만 있습니다. `slp_dg_*` 는 **도×10** 으로 저장되어 있습니다.
- 하천 경사는 EarthEnv-DEM90 으로 계산된 값이라 Copernicus 고도와 다를 수 있습니다.
- 일부 열은 ODbL 1.0 (같은 조건으로 공유) 입니다 (HydroATLAS 기술 문서 4.1절).
- 풀면 약 7 GB 입니다. `/vsizip/` 로 읽을 수 있지만 느립니다 (유역 하나의 범위를 읽는 데 약 11초). 여러 번 훑을 일이 있으면 `data/cache/` 에 풀거나 GeoParquet 로 잘라 `data/derived/` 에 둡니다.

**AfriSAR (ORNL DAAC 1577)**
- HDF5(과학 자료용 파일 꼴) 18개가 들어 있습니다. 좌표가 지도 좌표가 아니라 레이더에서 잰 비스듬한 거리(slant range)이고, 칸마다 위경도가 따로 있습니다. 지금 생성기에서는 쓰지 않습니다.
- 스크립트로 다시 받을 수 없습니다 (NASA Earthdata 로그인 필요). zip 은 풀지 말고 그대로 둡니다.

## 출처 표기

이 자료로 만든 그림, 보고서, 공개 결과물에는 아래를 적습니다.

**Copernicus DEM GLO-90 (라이선스 6조, 반드시 원문 그대로)**
- 받은 그대로 배포하거나 보여줄 때:
  > © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved.
- 고치거나 가공한 경우 (경사, 유역 등):
  > produced using Copernicus WorldDEM™-90 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved
- 배포할 때 라이선스나 안내문에 넣는 면책 문장:
  > The organisations in charge of the Copernicus programme by law or by delegation do not incur any liability for any use of the Copernicus WorldDEM™-90
- Copernicus 쪽이 이 과제를 공식으로 지지하는 것처럼 보이게 쓰면 안 됩니다.

**인용**
- ETOPO 2022: NOAA National Centers for Environmental Information (2022), ETOPO 2022 Global Relief Model, doi:10.25921/fd45-gt74
- CHELSA: Karger et al. (2017) Scientific Data 4:170122; Karger et al. (2021) EnviDat, doi:10.16904/envidat.228.v2.1
- Global Aridity Index v3.1: Zomer, Xu, Trabucco (2022) Scientific Data 9:409
- RiverATLAS: Linke et al. (2019) Scientific Data 6:283
- OCTOPUS v2: Codilean et al. (2022) Earth System Science Data 14:3695-3713, doi:10.5194/essd-14-3695-2022
- AfriSAR: Hawkins, Pinto, Lavalle, Hensley (2018) AfriSAR: Polarimetric Height Profiles by TomoSAR, Lope and Rabi Forests, Gabon, 2016. ORNL DAAC, doi:10.3334/ORNLDAAC/1577

**아직 정해지지 않은 라이선스**
- OCTOPUS: 예전 스크립트는 CC BY-NC-SA 4.0, octopusdata.org 바닥글과 Zenodo 기록은 CC BY 4.0 으로 적혀 있습니다. 확인할 때까지 더 엄격한 CC BY-NC-SA 4.0 으로 다룹니다. 이 자료로 만든 결과(예: `configs/pilot_basins.csv`, 3장에서 맞춘 값)를 공개할 때는 출처와 이 조건을 함께 적습니다.
- Aridity v3.1: 위의 "비상업 이용" 참고.

## 검사하기

저장소 루트에서 실행합니다. 네트워크를 쓰지 않습니다.

```bash
uv run python tools/download_pilot.py --verify-only                 # 크기만 비교 (1초 이내)
uv run python tools/download_pilot.py --verify-only --verify-hash   # sha256 까지 비교
```

- 없거나 크기가 다른 파일이 있으면 목록을 보여주고 1 로 끝납니다.
- AfriSAR 처럼 손으로 받는 자료가 없는 것은 "선택 자료"로만 알려 주고 실패로 치지 않습니다.
- OCTOPUS GeoJSON 은 WFS 원본이 바뀔 수 있어서 크기가 달라도 경고만 합니다.

## 다시 받기

```bash
uv run python tools/download_pilot.py
```

- 없는 파일만 받습니다. 이미 있고 크기가 `manifest.json` 과 같으면 건너뜁니다. 처음부터 받으면 약 4.5 GB 입니다.
- curl 이 필요합니다 (macOS, Windows 10 이상, 대부분의 Linux 에 들어 있습니다). 표준 라이브러리만 쓰므로 패키지를 설치하기 전에도 Python 3.11 이상이면 `python tools/download_pilot.py` 로 돌릴 수 있습니다.
- 받는 중에는 `<이름>.part` 로 쓰고, 다 받은 뒤에 원래 이름으로 바꿉니다. 중간에 끊기면 다시 실행하세요. `.part` 부터 이어받습니다.
- GLO-90 타일이 HTTP 404 이면 바다만 있는 칸이라 타일이 없는 것으로 보고 넘어갑니다. 다른 실패(연결 끊김, 403 등)는 모아서 마지막에 보여주고 1 로 끝납니다.
- 파일럿 유역은 `configs/pilot_basins.csv` 를 그대로 씁니다. 그래서 OCTOPUS WFS 내용이 바뀌어도 모두 같은 타일을 받습니다.
- AfriSAR 는 [ORNL DAAC 안내 페이지](https://daac.ornl.gov/AFRISAR/guides/Polarimetric_height_profile.html)에서 NASA Earthdata 계정으로 받아 `external/afrisar/` 에 zip 그대로 둡니다.

**관리자용 (유역을 바꿀 때)**

```bash
uv run python tools/download_pilot.py --reselect --write-manifest
```

- `--reselect` 는 OCTOPUS 유역에서 40개를 다시 골라 `configs/pilot_basins.csv` 를 새로 씁니다. 지금 가진 OCTOPUS 파일로 다시 고르면 지금 목록과 똑같이 나옵니다.
- `--write-manifest` 는 받은 파일의 크기와 sha256 으로 `manifest.json` 을 새로 씁니다. 원본이 바뀌어 크기가 달라진 파일을 받아들일 때도 씁니다.
- 두 파일(`configs/pilot_basins.csv`, `data/manifest.json`)을 함께 커밋합니다.

## 다른 디스크에 두기 (BPCG_DATA)

자료가 크면 환경 변수 `BPCG_DATA` 로 데이터 폴더를 옮길 수 있습니다. 옮긴 폴더 안의 구조(`pilot/`, `external/`, ...)는 그대로 둡니다. `bpcg_studio.paths`, `Bpcg.Core.Paths`, `tools/download_pilot.py` 가 모두 이 값을 따릅니다. `manifest.json` 은 저장소의 `data/` 에 그대로 두고, 그 안의 경로는 데이터 폴더를 기준으로 읽습니다.

```bash
# macOS, Linux
export BPCG_DATA="<외장 디스크 경로>/bpcg-data"
```

```powershell
# Windows PowerShell
$env:BPCG_DATA = "<외장 디스크 경로>\bpcg-data"
```

한 번만 바꿔 실행하려면 `--data <폴더>` 를 붙여도 됩니다.

## manifest.json 형식

```json
{
  "version": 1,
  "generated": "2026-10-02",
  "files": [
    {
      "path": "pilot/glo90/...tif",
      "bytes": 4888670,
      "sha256": "...",
      "group": "pilot",
      "source_url": "https://...",
      "license": "...",
      "redownload": "script",
      "note": "..."
    }
  ],
  "glo90_no_tile": []
}
```

- `path` 는 데이터 폴더(기본 `data/`, 또는 `BPCG_DATA`) 기준 상대 경로입니다.
- `group` 은 `pilot` 또는 `external`, `redownload` 는 `script`(스크립트로 다시 받음) 또는 `manual`(손으로 받음)입니다.
- `glo90_no_tile` 은 바다만 있어서 서버에 타일이 없는 칸(HTTP 404)의 이름입니다. 지금은 비어 있습니다.
- `.DS_Store` 와 `logs/` 는 넣지 않습니다.
