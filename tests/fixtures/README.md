# tests/fixtures

테스트에서 쓰는 작은 고정 자료입니다. 저장소에 올립니다(각 약 0.87 MB).

## 실제 지형 조각: `lope_tile512_30m.npz`, `rabi_tile512_30m.npz`

가봉 로페(Lope)와 라비(Rabi) 숲의 30 m 지형에서 빈칸 없이 잘라 낸 512 × 512 칸(약 15.4 km × 15.4 km) 조각입니다.
물길 코드(채움, D8, 유량)를 실제 지형으로 시험하는 데 씁니다(설계도 부록 A-2 의 (6), 7장 자동 테스트).

| 키 | 모양·형식 | 뜻 |
| --- | --- | --- |
| `z` | (512, 512) float32 | 높이 [m]. 빈칸(NaN) 없음. 행은 남→북(y 증가), 열은 서→동(x 증가) |
| `dx` | 실수 | 칸 크기 [m] (30.0) |
| `x0`, `y0` | 실수 | 조각 첫 칸(행 0, 열 0)의 지역 좌표 [m]. 원점은 원본 띠의 평균 위경도 |
| `lat0`, `lon0` | 실수 | 지역 좌표 원점의 위도·경도 [도] (원본 DEM 과 같음) |
| `src` | 문자열 | 잘라 낸 원본 파일 이름 (`lope_dem_30m.npz` 등) |
| `row0`, `col0` | 정수 | 원본 DEM 안에서 조각이 시작하는 행·열 |

지역 좌표는 등거리 근사입니다: `x = (lon - lon0) · R · cos(lat0)`, `y = (lat - lat0) · R` (R = 6,378,137 m, 라디안).

| 파일 | 원본 위치(행, 열) | 기복 |
| --- | --- | --- |
| `lope_tile512_30m.npz` | 735:1247, 278:790 | 537 m |
| `rabi_tile512_30m.npz` | 272:784, 750:1262 | 112 m |

### 주의

- 높이는 AfriSAR 자료의 `TerrainHeight` 로, SRTM 에서 온 **표면 높이**입니다(숲 꼭대기가 섞여 땅보다 높게 잡힘).
  절대 높이나 경사의 '정답'으로 쓰지 말고, 코드가 실제 지형에서 깨지지 않는지 보는 데만 씁니다.
- 높이 기준은 원자료를 따릅니다(ORNL 가이드: WGS84 타원체 위 높이). 해발 높이와 수 m 이상 다를 수 있습니다.

### 다시 만드는 법

```bash
uv run python analysis/afrisar/extract.py --fourier-only   # zip 에서 Fourier HV 2개 꺼내기 (약 0.76 GB)
uv run python analysis/afrisar/geocode.py lope rabi         # data/derived/afrisar/<site>_dem_30m.npz
uv run python tools/make_tile512_fixture.py lope rabi       # 이 폴더에 다시 씀
```

`make_tile512_fixture.py` 는 원본 DEM 에서 빈칸 없는 가장 큰 정사각형을 찾아 그 가운데 512 × 512 를 자릅니다.
2026-10-02 에 위 순서로 다시 만들어 지금 파일과 바이트 단위로 같음을 확인했습니다.

### 자료 출처

Hawkins, B.P., N. Pinto, M. Lavalle, and S. Hensley. 2018.
*AfriSAR: Polarimetric Height Profiles by TomoSAR, Lope and Rabi Forests, Gabon, 2016.*
ORNL DAAC, Oak Ridge, Tennessee, USA. https://doi.org/10.3334/ORNLDAAC/1577

NASA 지구과학 자료는 제한 없이 쓸 수 있고 인용을 권합니다. 원자료(zip 약 15 GB)는 NASA Earthdata 계정으로
받아 `data/external/afrisar/` 에 두며 저장소에는 넣지 않습니다.
