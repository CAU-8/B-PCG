# 생성 파이프라인 명세 (학습 전 버전)

이 문서는 `src/bpcg`가 행성 하나를 만드는 계산을 공식과 함수 계약으로 고정합니다. 실제 지형 자료로 값을 맞추는 단계(설계도 6장)는 아직 쓰지 않고, 모든 상수는 `configs/planets/earth.toml`의 문헌 기본값을 씁니다. 맞춘 값이 생기면 `configs/learned/`가 같은 키를 덮어쓰고, 코드는 바꾸지 않습니다.

가이드(`docs/guide/planet_guide.md`)와 설계도(`docs/design/blueprint.md`)가 다르면 설계도를 따르고, 이 문서는 둘을 합친 구현 기준입니다. 바꾼 곳은 각 절에 '가이드와 다름'으로 적습니다.

## 1. 흐름

```text
1단계 재료 (거친 격자, 면당 coarse_n)
  plates → crust → ocean(해수면) → uplift → climate → geology(템플릿·깎인 두께)
        │ 매끄러운 값은 L0 로 보간 (core/resample), 범주 값은 가장 가까운 칸
        ▼
2단계 솔버 (L0, 면당 l0_n, 노드 흔들기)
  [채움 → D8(히스테리시스) → Q, Qs → 경사 법칙 → 층별 적분] 을 방향이 안 바뀔 때까지
  L0 는 두 번 풉니다: 첫 풀이의 평균 지표로 융기를 줄이고(지각 세기 한계) 그 지형에서 다시
        ▼
3단계 후처리 (L0): 선상지 → 물길만 다시 계산 / 기복 보정(골짜기 바닥 + 아격자 기복)
4단계 3D 값 (L0): 물(호수·강·수면) → 흙 → 지하수면 → 동굴 층
        ▼ 묶음 (out/<run>/planet/)
히어로 (L2, 평면 25 m): 후보 찾기 → L0 경계조건 → 거친 격자 먼저 → 2~4단계를 같은 코드로 다시
        ▼ 묶음 (out/<run>/hero/)
미시 (L3): 3D 샘플 함수 → 회랑 굽기 (높이맵, 동굴 메시, 지층 부피) → engine/baked/
```

'평면 히어로 먼저'(설계도 8장)를 위해 히어로는 L0 없이 가짜 경계조건으로도 돕니다(`hero/flat.py`).

## 2. 공통 약속

- **그래프.**
    - 모든 셀 계산은 `bpcg.core.graph.CellGraph`(pos, nbr, dist, area)를 받습니다.
    - 구면은 `sphere_graph(n, R, jitter, seed)`, 평면은 `flat_graph(ny, nx, dx, jitter, seed, origin)`로 만듭니다.
    - 평면 셀 번호는 `c = j·nx + i`이고, 0번 행이 북쪽 끝입니다.
- **필드.**
    - 셀마다 하나인 값은 `dict[str, np.ndarray]`(길이 N)로 넘깁니다.
    - 이름·단위·dtype은 `bpcg.core.fields.FIELDS`에 있는 것만 씁니다.
    - 2차원 필드(`strata_*`)만 (N, L)입니다.
- **설정.** `cfg = bpcg.core.config.load_config(planet, profile)`로 읽습니다. 함수는 필요한 값만 `cfg.<절>.<키>`로 읽고, 숫자를 코드에 박지 않습니다.
- **단위.**
    - 기본 단위는 m, yr, m/yr, m³/yr, °C, 라디안입니다. 나이만 Myr입니다.
    - `SECONDS_PER_YEAR = 3.15576e7`입니다.
- **결정성.**
    - 지형 모양에 영향을 주는 난수는 `bpcg.core.hashing`과 `bpcg.core.noise`(정수 해시)로만 만들고, 시드는 `cfg.planet.seed`입니다.
    - 같은 높이일 때의 순서는 셀 번호로 정합니다.
- **정밀도.** 솔버 고도와 채움 높이는 float64로 계산합니다. 저장할 때만 FIELDS의 dtype으로 바꿉니다.
- **Numba.**
    - 커널은 `@njit(cache=True)`를 붙인 모듈 최상위 함수로 둡니다.
    - 커널에는 배열과 숫자만 넘깁니다. `prange`는 경쟁이 없을 때만 씁니다.
- **반환.** 각 단계 함수는 `dict` 필드와 진단값(`dict`: 반복 수, 시간 등)을 돌려줍니다. 파일 쓰기는 `bake/`만 합니다.

## 3. core (공통 뼈대)

이미 있는 것: `cubesphere`(사상, 역사상 `to_face`·`cell_of`, `neighbor_table`, `cross_face_pairs`, 면적), `graph`, `hashing`, `config`, `fields`, `paths`.

| 모듈 | 함수 | 계약 |
|---|---|---|
| `core/constants.py` | `gravity(radius_m, density)` | g = 4/3·π·G·ρ·R. `G_GRAV = 6.674e-11`, `SECONDS_PER_YEAR`, `RHO_WATER = 1000`, `MU_WATER = 1e-3` Pa·s |
| `core/noise.py` | `gradient_noise3(x, y, z, seed)` | 정수 격자 해시 기울기 노이즈(퀸틱 보간), 값 대략 [-1, 1], numba |
| | `fbm3(points (M,3), seed, octaves=5, gain=0.5, lacunarity=2.0, frequency=1.0)` | 가이드 5장 ξ: Σ g^o ν(2^o λ p + o_o) / Σ g^o. 옥타브마다 시드를 바꿈 |
| | `vector_fbm3(points, seed, ...)` | 서로 다른 시드의 fbm 3개 → (M, 3) |
| `core/distance.py` | `nearest_source(graph, is_source, max_dist=inf) -> (dist, src)` | 여러 출발점 다익스트라. 키는 그래프 경로 길이가 아니라 '전파된 출발점까지의 직선 거리'(구면은 대원 거리)라서 등거리선이 팔각형이 되지 않습니다(설계도 5장). src는 가장 가까운 출발 셀 번호, 출발점이 없거나 max_dist를 넘으면 dist = inf, src = -1 |
| `core/resample.py` | `add_ghost_layers(faces (6,n,n), layers=1)` | 가이드 7장: 고스트 줄을 인접 면 첫 줄을 따라 정확한 위치에서 1D 보간. 꼭짓점 칸은 인접 고스트 두 개의 평균 |
| | `sample_sphere(field (6n²,), n, unit (M,3), method="linear"\|"nearest")` | 역사상 → 면 좌표 → 고스트를 붙인 쌍선형 보간. nearest는 `field[cell_of]` |
| | `resample_sphere(field, n_src, graph_dst, method)` | 거친 격자 → L0. 범주 값(판 번호 등)은 nearest |

검사(tests/test_noise.py, test_distance.py, test_ghost.py):

- 노이즈는 같은 시드에서 같은 값이 나오고, 값이 [-1.2, 1.2] 안이며, 시드가 다르면 값이 다릅니다.
- 거리
    - 평면 한 점 출발이면 유클리드 거리와 상대 오차 1% 이내입니다. 그래프 거리면 8%까지 틀립니다.
    - 구면 한 점 출발이면 대원 거리와 1% 이내입니다.
- 고스트(가이드 7장): 경계 오차 / 내부 오차 < 2이고, n이 두 배가 되면 3 < 오차비 < 5입니다.

## 4. planet (1단계 재료, 사슬 1과 기후)

모두 거친 격자(`profile.grid.coarse_n_per_face`)에서 계산하고 `resample_sphere`로 L0에 옮깁니다. 해구 단면처럼 좁은 것만 L0에서 거리로 다시 계산합니다.

### 4.1 판 `planet/plates.py`

`generate_plates(graph, cfg) -> (fields, info)`

1. **씨앗.** M = `plates.count`개를 만듭니다. 해시 균등수 두 개를 Box–Muller로 정규분포 3개로 바꾸고 정규화하면 구면에 고르게 퍼집니다.
2. **판 배정(가이드 5장).**
    - π(c) = argmax_k normalize(p + A_w·ξ⃗(f_w·p))·s_k로 정합니다. A_w = `warp_amplitude`, f_w = `warp_frequency`, ξ⃗ = `vector_fbm3`입니다.
    - 판 번호는 0..M-1입니다.
3. **판 운동.**
    - 각 판의 오일러 극 축은 해시로 정한 단위 벡터입니다.
    - 각속도 크기는 |ω| = v/R이고, v는 `speed_m_per_yr` 범위에서 해시로 고릅니다.
    - 속도는 v(p) = ω × R·p [m/yr]입니다.
4. **경계 판정(가이드 5장).**
    - 다른 판에 속한 이웃 쌍마다 b̂, Δv, γ = −Δv·b̂, τ = |Δv·(p×b̂)|를 구합니다.
    - 칸 값은 다른 판 이웃들의 평균입니다.
    - 판정은 γ > κτ이면 수렴(1), γ < −κτ이면 발산(2), 나머지는 변환(3)입니다. κ = `boundary_kappa`입니다.
5. **모든 해양판에 발산 경계 두기(설계도 5장 17번).**
    - 해양 면적이 절반을 넘는 판에 발산 경계 칸이 하나도 없으면, 그 판의 ω를 가장 큰 이웃 판의 ω에서 멀어지는 쪽(ω_k ← −ω_k)으로 뒤집고 4를 다시 합니다(최대 3회).
    - 판의 해양 면적은 4.2의 대륙 마스크로 판단하므로, 대륙 마스크를 먼저 만들어 `generate_plates`에 넘깁니다.
6. **거리(`core/distance.nearest_source`).**
    - 수렴 경계 칸에서 `dist_convergent_m`, 발산 경계 칸에서 `dist_divergent_m`을 구합니다.
    - 가장 가까운 경계 칸의 값을 넘겨받아 `convergence_m_per_yr`(γ)와 `spreading_m_per_yr`(|Δv·b̂|/2)를 채웁니다.
7. **섭입 방향(`convergence_kind`, `subduction_side`).** 수렴 경계마다 양쪽 지각 종류로 정합니다.
    - 해양-대륙이면 해양 쪽이 섭입합니다(1).
    - 해양-해양이면 더 늙은 쪽이 섭입합니다(2). 나이는 4.2와 같이 계산하고, 같으면 판 번호가 작은 쪽이 섭입합니다.
    - 대륙-대륙이면 충돌입니다(3).
    - 각 칸은 가장 가까운 수렴 경계의 위판에 속하면 +1, 섭입판에 속하면 −1, 충돌이면 0입니다.

### 4.2 지각 `planet/crust.py`

`continent_mask(graph, cfg) -> bool` · `generate_crust(graph, plate_fields, continental, cfg) -> fields`

- **대륙 마스크.**
    - 낮은 주파수 fbm(주파수 1.2, 옥타브 4) + 판별 치우침(해시 ±0.3)이 가장 큰 쪽부터 면적 가중으로 `continental_area_fraction`을 채우는 문턱으로 자릅니다.
    - 대륙은 판 경계와 상관없이 놓입니다.
- **해양저 나이(설계도 5장 17번).**
    - age = dist_divergent / spreading [yr]를 Myr로 바꾸고, `age_cap_myr`는 안전장치로만 씁니다.
    - 대륙 칸은 NaN입니다.
- **지각 두께.**
    - 대륙은 35 km에, 충돌 경계(kind 3) 근처에서 `collision_thickening_m`·exp(−(δ_conv/`thickening_width_m`)²)를 더합니다.
    - 대륙 가장자리에서는 해양 지각까지 거리 δ_oc가 `margin_width_m` 안이면 두께를 smoothstep으로 7 km 쪽으로 줄입니다. smoothstep(δ_oc/margin_width)이 0일 때 0.55·35 km입니다.
    - 해양은 7 km입니다.
- **기준 고도 `z_platform_m`(해수면 정하기 전).**
    - 대륙(Airy, 대륙-해양 전이에만 쓰임, 설계도 5장 2번)
        - z = `continental_platform_m` + (H − 35 km)·α입니다.
        - z ≥ 0이면 α = (ρm−ρc)/ρm, z < 0이면 α = (ρm−ρc)/(ρm−ρw)입니다.
    - 해양(Parsons & Sclater 1977): t < 70 Myr이면 d = 2500 + 350√t, 아니면 d = 6400 − 3200·e^(−t/62.8) [m]이고, z = −d입니다.
    - 해구: 섭입판 쪽(side −1)의 해양 칸에 −`trench_depth_m`·exp(−(δ_conv/`trench_width_m`)²)를 더합니다. 변위(m)이므로 속도와 섞지 않습니다(설계도 5장 7번).
    - 전이: 대륙-해양 경계 양쪽 50 km에서 두 값을 smoothstep으로 섞습니다.

### 4.3 해수면 `planet/ocean.py`

`sea_level(z_platform, area, water_volume) -> h` · `ocean_mask(graph, z_platform, h) -> bool`

- **해수면 h.** V(h) = Σ A·max(h − z, 0) = `water_volume_m3`를 이분법(상대 오차 1e-9)으로 풉니다. 바다 비율은 결과로 나옵니다(설계도 5장 3번).
- **바다 마스크.** z < h이면서 가장 큰 연결 성분에 속하는 칸입니다. 이어지지 않은 낮은 땅은 육지입니다.
- **수심.** 바다 칸 고도는 z_platform − h입니다. 이후 모든 고도는 이 해수면을 0으로 하는 값입니다.

### 4.4 융기 `planet/uplift.py`

`generate_uplift(graph, fields, cfg) -> fields`. U는 m/yr이고, 바다 칸은 0입니다.

- **기본 깎임.** 모든 대륙 칸과, 바다와 이어지지 않아 육지가 된 해양 지각 칸에 U₀ = `craton_erosion_m_per_myr`·1e-6을 줍니다(설계도 5장 2번). 해양 지각 육지에 U = 0을 두면 그 칸의 k_s가 0이 되어 기복 보정이 무한대가 됩니다.
- **섭입 위판(kind 1·2, side +1).**
    - 해구-화산호 거리는 d_arc = h₁/tan(θ₁) + (h_arc − h₁)/tan(θ₂)입니다(설계도 5장 16번, 기본값으로 약 200 km).
    - 융기는 U += `orogen_factor`·γ·exp(−((δ_conv − d_arc)/w)²)이고, w = `orogen_width_m`입니다.
- **충돌(kind 3).** U += `collision_factor`·γ·exp(−(δ_conv/w_c)²)입니다. w_c = `collision_width_m`이고, 없으면 1.5w입니다.
- **꼬리 자르기.** 산맥 성분이 1e-9 m/yr(1 m/Myr의 1/1000)보다 작으면 0으로 둡니다. 가우스 꼬리의 1e-300 같은 값이 뒤 단계 거듭제곱에서 0으로 사라져 NaN을 만들기 때문입니다.
- **대륙 열곡.** 대륙 칸이고 δ_div < 50 km이면 U += `rift_subsidence_m_per_yr`·exp(−(δ_div/30 km)²)입니다.
- **노이즈.** 산맥 성분에 (1 + 0.25·fbm3)을 곱합니다.
- **깎인 두께.** `exhumation_m` = clip(max(U, 0)·`orogen_duration_myr`·1e6, 0, `exhumation_cap_m`)입니다(설계도 5장 8번).
- **화산호 거리.** `dist_arc_m`은 side +1 칸에서 d_arc, 그 밖에는 NaN입니다.

### 4.5 가벼운 기후 `planet/climate.py`

`generate_climate(graph, cfg, z=None, seed) -> fields`

- **위도.** φ = asin(p̂·axis)입니다.
- **강수.** P = [p_base + p_eq·e^(−(φ/σ₁)²) + p_mid·e^(−((|φ|−φ_m)/σ₂)²)]·exp(η·ξ_P(p) − η²/2)입니다(가이드 5장, 각도는 도 단위 설정을 라디안으로).
- **기온.** T = T_eq − (T_eq − T_pole)·sin²φ − lapse·max(z, 0)입니다. z가 없으면 0을 씁니다.
- **증발산과 유출.**
    - 잠재 증발산은 PET = a·max(T, 0) + b입니다.
    - Budyko(1974): ET = P·sqrt((PET/P)·tanh(P/PET)·(1 − e^(−PET/P))), R = P − ET입니다.
- **침식용 유출(설계도 5장 1번).** R_eff = max(R, `runoff_floor_fraction`·P, `runoff_floor_m_per_yr`)입니다.
- **두 번 부릅니다.** 솔버 전에 z = 기준 고도로 한 번, 솔버 뒤 최종 고도로 기온만 다시 부릅니다. 기후-지형 바깥 반복은 여유 작업이라 하지 않습니다.

## 5. geology (1단계, 사슬 2의 암석)

### 5.1 암석 표 `geology/rocks.py`

| id | 이름 | K 배율 | S_crit | log10 k [m²] (Gleeson 2011, 원문 확인 필요) | 녹음 | 색 |
|---|---|---|---|---|---|---|
| 0 | alluvium 충적층 | 3.0 | 0.4 | −10.9 | 아니오 | 모래색 |
| 1 | sandstone 사암 | 0.5 | 0.75 | −12.5 | 아니오 | |
| 2 | shale 셰일 | 2.0 | 0.5 | −16.5 | 아니오 | |
| 3 | limestone 석회암 | 0.6 | 0.8 | −11.8 | 예 | |
| 4 | granite 화강암 | 0.3 | 0.8 | −14.1 | 아니오 | |
| 5 | volcanic 화산암 | 0.5 | 0.75 | −12.5 | 아니오 | |
| 6 | slate 점판암 | 0.8 | 0.7 | −14.1 | 아니오 | |
| 7 | schist 편암 | 0.6 | 0.7 | −14.1 | 아니오 | |
| 8 | gneiss 편마암 | 0.35 | 0.8 | −14.1 | 아니오 | |
| 9 | marble 대리암 | 0.5 | 0.8 | −11.8 | 예 | |
| 10 | quartzite 규암 | 0.25 | 0.85 | −14.1 | 아니오 | |
| 11 | soil 흙 | 4.0 | 0.6 | −11.0 | 아니오 | |

**변성(설계도 2장).** 최고 온도 T_peak = `surface_temperature_c` + `geotherm_c_per_m`·묻힌 깊이입니다.

- 셰일은 ≥ 250 °C에서 점판암, ≥ 400 °C에서 편암, ≥ 600 °C에서 편마암이 됩니다.
- 석회암은 ≥ 350 °C에서 대리암이 됩니다.
- 사암은 ≥ 350 °C에서 규암이 됩니다.
- 화강암은 ≥ 650 °C에서 편마암이 됩니다.

`metamorphose(rock, T_peak)`은 벡터로 계산합니다.

### 5.2 3D 지질 모델 `geology/model.py`

**템플릿(위층부터 (암석, 두께 m), 마지막은 기반암).**

| 번호 | 템플릿 | 층 |
|---|---|---|
| 0 | 탄산염 탁상지 | 사암 150, 석회암 500, 셰일 250, 석회암 600, 사암 300, 기반암 화강암 |
| 1 | 습곡충상대(자그로스·쥐라·캐나다 로키형) | 셰일 300, 석회암 800, 셰일 400, 사암 500, 석회암 600, 기반암 화강암 |
| 2 | 기반암 + 화산호 | 화산암 800, 기반암 화강암 |

**`assign_template(fields, cfg)`.**

- 2: side +1이고 |δ_conv − d_arc| < 0.4·`arc_belt_width_m`인 칸
- 1: kind 3이고 δ_conv < `fold_belt_width_m`인 칸, 또는 side +1이고 d_arc + 50 km < δ_conv < `fold_belt_width_m`인 칸
- 0: 나머지

**습곡.**

- 위상은 φ_fold = 2π·δ_conv/λ + 0.5·fbm3(p·R/λ)입니다.
- 층 변위는 Δ = A·(U/U_max)·sin(φ_fold)이고, 템플릿 1에서만 씁니다. A = `fold_amplitude_m`, λ = `fold_wavelength_m`입니다.

**층 고도(설계도 5장 '지층 높이는 누적 융기로').**

- 기둥 꼭대기는 z_top = `exhumation_m` + Δ입니다. 처음엔 해수면에서 쌓였고, 깎인 두께만큼 솟았다는 뜻입니다.
- 층 i의 바닥 고도는 z_top − Σ_{j≤i} t_j입니다.
- 각 층의 변성은 그 층 가운데 깊이의 T_peak로 정합니다. 층마다 하나로 정하므로 솔버가 쓴 암석과 보이는 암석이 늘 같습니다.

**`build_columns(template_id, exhumation, fold_disp, cfg) -> (strata_bottom (N,L), strata_rock (N,L+1))`.** L은 가장 긴 템플릿 기준입니다. 짧은 템플릿은 남는 층을 두께 0(바닥 = 앞층 바닥)으로 채웁니다.

**`rock_at(strata_bottom, strata_rock, z)`.** z보다 낮은 바닥을 가진 첫 층이 그 암석이고, 모든 바닥보다 낮으면 기반암입니다. numba, 칸·높이 배열을 받습니다.

**`LayerColumns(bottom, K_mult, s_crit, rock)`.** 솔버에 넘기는 묶음입니다.

검사: 지층 순서(위에서 아래로 템플릿 순서), 변성 표, rock_at 경계값, 깎인 두께 0이면 꼭대기 층이 지표에 드러남.

## 6. hydro (물길, 가이드 2장)

모든 함수는 그래프 배열(`nbr`, `dist`, `area`)과 numba 커널로 계산합니다. MFD는 만들지 않습니다(설계도: D8 + 노드 흔들기).

| 모듈 | 함수 | 계약 |
|---|---|---|
| `hydro/depressions.py` | `fill_depressions(z, nbr, is_outlet) -> z_hat` | priority-flood. 바다·출구에서 시작. ẑ ≥ z, 연결된 호수마다 ẑ 일정 |
| | `fill_epsilon(z, nbr, is_outlet, eps) -> z_tilde` | z̃_c' = max(z_c', z̃_c + ε). 바다·출구가 아닌 모든 칸에 확실히 낮은 이웃이 있음 |
| `hydro/routing.py` | `d8_receivers(zt, nbr, dist, is_outlet, prev=None, eta=0.0) -> (rcv, slope, n_changed)` | r(c) = argmax (z̃_c − z̃_c')/d. 출구는 자기 자신. prev가 있으면 히스테리시스(새 경사 > (1+η)·옛 경사일 때만 바꿈, 옛 수신 셀이 더 이상 낮지 않으면 바꿈). n_changed는 prev와 다른 칸 수 |
| | `topo_order(rcv) -> order` | 하류가 먼저 오는 순서(출구부터 깊이 우선). rank(r(c)) < rank(c) |
| `hydro/accumulate.py` | `accumulate(rcv, order, weight) -> acc` | acc_c = weight_c + Σ_{기여 셀} acc. 질량 보존 |
| | `donors_count(rcv) -> int32` | 기여 셀 수 |
| `hydro/network.py` | `outlet_of(rcv, order) -> int32` | 칸마다 흘러 나가는 출구 칸(유역 번호) |
| | `river_segments(rcv, order, is_river) -> list[np.ndarray]` | 강 칸을 하류 방향 순서로 잇고, 합류점(강 기여 셀 ≥ 2)과 출구에서 끊은 구간들. 각 구간은 상류→하류 칸 번호 |

검사(tests/test_hydro.py):

- 싱크가 없습니다. 출구가 아닌 모든 칸에서 z̃_r < z̃_c입니다.
- 질량이 보존됩니다. Σ_출구 Q = Σ A·P(상대 1e-12).
- ẑ ≥ z이고, 연결된 호수마다 ẑ가 일정합니다.
- 실제 지형 조각으로 비교합니다.
    - 로페 512² 조각(`tests/fixtures/lope_tile512_30m.npz`)에서 pyflwdir(MIT, `importorskip`)과 상류 면적이 일치합니다.
    - 큰 강(상류 1 km² 이상) 칸 겹침 0.95 이상, 흐름 방향 일치 99% 이상입니다. ε 채움 평지는 판정에서 뺍니다.

## 7. landscape (2·3단계, 사슬 2)

### 7.1 정상상태 솔버 `landscape/solver.py`

`solve_steady_state(graph, is_outlet, z_outlet, uplift, runoff_eff, layers, cfg, z_init=None, extra_inflow=None) -> SolverResult`

`SolverResult`(dataclass)는 다음을 담습니다.

| 이름 | 내용 |
|---|---|
| `z` | float64 |
| `receiver` | 수신 셀 |
| `order` | 하류부터 순서 |
| `discharge` | Q [m³/yr] |
| `drainage_area` | 상류 면적 |
| `sediment_flux` | Qs [m³/yr] |
| `slope` | 경사 |
| `k_s` | 강 가파름 |
| `s_crit_surface` | 지표 암석의 임계 경사 |
| `iterations` | 반복 수 |
| `history` | 반복마다 방향 변화 수와 max\|Δz\| |
| `converged` | 멈춤 조건을 만족했는지 |
| `seconds` | 걸린 시간 |

**한 번의 반복(가이드 4장 + 설계도 2·5장).**

1. 물길을 다시 계산합니다.
    - z̃ = fill_epsilon(z, ε)입니다.
    - r = d8_receivers(z̃, prev = 지난 r, η)입니다. 첫 반복은 η = 0입니다.
    - σ = topo_order(r)입니다.
2. 유량과 퇴적물을 모읍니다.
    - Q = accumulate(A·R_eff) + `extra_inflow`입니다. 들어오는 물은 히어로 경계 칸에서만 씁니다.
    - A↑ = accumulate(A)입니다.
    - Qs = max(accumulate(U·A), 0)입니다. 침강(U < 0)이 퇴적물을 덜어 내고, 0에서 자릅니다(설계도 5장 4번).
3. 경사 법칙을 칸마다 계산합니다(Yuan 2019 정상상태 + 사면 확산, 설계도 2장 표 첫 줄).
    - 필요한 깎임: E = U + G·R_ref·Qs/Q입니다. G = `deposition_g`, R_ref = `runoff_ref_m_per_yr`입니다. E ≤ 0이면 S = s_min입니다.
    - 하천 경사: S_r = (E/K)^(1/n)·Q^(−θ)입니다. K = K_ref·(암석 K 배율), K_ref = U_ref/k_ref^n이며, 기준 암석·U_ref에서 k_s = k_ref가 됩니다.
    - 사면 경사(선형 확산 정상상태): S_h = E·a/D입니다. a = A↑/w는 비유역 면적, w = sqrt(칸 면적), D = `hillslope_diffusivity`입니다.
    - 두 과정이 함께 깎으므로 조화 결합합니다: S = 1/(1/S_r + 1/S_h). 그다음 암석별 S_crit로 자르고, s_min 아래로는 내려가지 않게 합니다.
4. 층 경계별로 적분합니다(설계도 2장 '암석 강도와 층 경계별 적분').
    - 하류부터(σ 순서) z_c = z_r + ∫₀^d S(z) dx를 계산합니다. 경사는 높이에 따라 층마다 다릅니다.
    - z = z_r에서 출발해 지금 층의 경사 S_i로 올라갑니다. 다음 층 바닥(경계)까지 남은 수평 거리 (b − z)/S_i가 남은 거리보다 짧으면 경계에서 다음 층으로 바꿔 이어 갑니다.
    - 출구 칸은 z = z_outlet입니다.
    - `layers`가 None이면 기준 암석 하나(K 배율 1, S_crit = `s_crit_default`)로 계산합니다.
5. 멈춤 조건은 n_changed == 0 그리고 max|z_new − z| < `stop_dz_m`입니다(설계도 5장). 상한은 `max_flow_iterations`이고, 반복 수와 시간을 기록합니다.

**초기 지형.** z_init이 없으면 z_outlet + (출구까지 대원 거리 × 1e-3) + 10 m·fbm입니다. 물길이 처음부터 바다 쪽을 향하게 합니다.

**L0 퇴적 항(`deposition_g_l0`).**

- 현상: L0 칸(19.5 km)은 거의 모두 큰 강입니다. G-법칙 퇴적 항이 켜져 있으면 Qs/Q가 물길 방향을 따라 바뀌고, 그 변화가 다시 방향을 바꿉니다.
- 결과: 보정 실험에서 200회 안에 수렴하지 않았고, 진동으로 고정된 칸이 26%였습니다.
- 처리: `pipeline.l0_config(cfg)`가 L0에서만 G = `deposition_g_l0`(기본 0)으로 바꿉니다. 히어로(L2)는 `deposition_g`를 그대로 씁니다.

**지각 세기 한계(L0, `pipeline.strength_limited_uplift`).**

- 현상: 지각은 무한히 높은 산을 버티지 못해, 평균 고도가 약 5 km를 넘는 고원은 드뭅니다(티베트 평균 약 5 km). 경사 법칙만으로는 이 한계가 없습니다.
- 결과: 한계가 없으면 넓은 충돌대에서 강이 1,500 km를 흐르며 높이를 쌓아, L0 골짜기 바닥이 10 km를 넘었습니다(n = 1일 때 77 km).
- 처리: 한 번 풀어 평균 지표 z_mean(골짜기 바닥 + 기복 보정, 7.3절)을 구합니다. 그다음 U > 0인 칸을 U' = max(U·clip(1 − (z_mean/z_lim)^p, 0, 1), min(U, U₀))로 줄이고, 첫 풀이 지형을 z_init으로 다시 풉니다. z_lim = `elevation_limit_m`, p = `elevation_limit_power`, U₀는 기본 깎임입니다.
- 반복 안에 넣지 않는 이유: 반복마다 U를 고치면 융기와 높이가 서로를 따라 진동했습니다(진폭 2.7 km). 그래서 바깥에서 한 번만 고칩니다.
- 출력: 줄인 U'가 `uplift_m_per_yr`로 묶음에 들어가고, 히어로는 이 값을 보간합니다. 진단값은 `diag["strength_limit"]`입니다.
- 지표: 보정 뒤 L0 육지 평균 지표의 최고는 5.48 km이고, 50 % 값은 576 m입니다.

**거친 격자 먼저 풀기(평면 히어로, `landscape/warmstart.py`).**

- 현상: 25 m 히어로(1280², 160만 칸)를 노이즈 지형에서 바로 풀면, 물길망이 자리를 잡기 전에 큰 유역끼리 서로를 빼앗으며 진동합니다.
- 결과: 800회에도 고도 변화가 1.3 km였고, 진동으로 고정된 칸이 36%였습니다.
- 처리: `coarse_warm_start`가 4배 거친 격자에서 같은 경계조건으로 먼저 풉니다. 거친 칸 값은 다음과 같습니다.
    - 융기와 유출: 칸 평균
    - 들어오는 물: 합
    - 출구: 고운 출구가 하나라도 있으면 출구이고, 높이는 그중 최솟값
    - 지질 기둥: 가운데 고운 칸의 기둥
- 거친 격자 한 변이 4 × 64칸 이상이면 같은 방법을 한 번 더 씁니다. 그 결과를 쌍선형 보간해 z_init으로 넘깁니다.
- 정상상태 법칙은 시작 지형과 상관없이 같습니다. 바뀌는 것은 반복 수뿐입니다.
- 지표: 169회에 수렴했습니다. 고정 칸은 30%로 남아 있습니다(14장).
- 반복 상한: 히어로는 `profile.hero.max_flow_iterations`(laptop 800, lab 1000, tiny 400)를 씁니다.

**가이드와 다름.**

- 경사는 min(k_s Q^−θ, S_crit)에서 조화 결합 + G-법칙 + 층별 적분으로 바뀌었습니다.
- 멈춤 조건은 '방향 변화 0 그리고 0.1 m'입니다.
- k_s는 학습값 보간(가이드 5장) 대신 이론식 k_s = (E/K)^(1/n)입니다.

검사(tests/test_solver.py):

- 수렴합니다. 원형 섬(평면 96², 400 m)에서 200회 안에 멈춥니다.
- 법칙이 자기일관적입니다. 균일 암석에서 S < S_crit인 칸은 |S − 법칙|/법칙 < 1e-3입니다.
- 퇴적물 수지는 Σ_출구 Qs = Σ max-클립 전 U·A(침강 없을 때) 상대 1e-6입니다.
- 2층 해석해: 1차원 강(평면 1×N, 상류 면적 선형 증가)의 층별 적분 결과가 손으로 푼 조각별 해와 1e-3 m 안입니다.
- 결과에는 웅덩이가 없습니다.

### 7.2 선상지 `landscape/fans.py`

`add_fans(graph, z, result, cfg) -> (z_new, fan_mask, apexes)`

- **꼭짓점.** 강 칸 중 S_c / S_{r(c)} ≥ `slope_drop_ratio`이고 Q가 [min, max]인 곳입니다. 서로 2·radius 안이면 Q가 큰 하나만 남깁니다.
- **부채꼴.** 꼭짓점에서 거리 ℓ < radius인 칸 중 하류 쪽 반평면(꼭짓점 → 수신 셀 방향과의 내적 > 0)에 원뿔 z_cone = z_apex − slope·ℓ을 만들고, z = max(z, z_cone)입니다.
- **표시.** 올린 칸에 `fan`과 `not_steady`를 답니다. 그 뒤 물길만 다시 계산합니다(채움 → D8 → Q). 호수가 생기면 그대로 둡니다.

### 7.3 기복 보정 `landscape/relief.py`

`subgrid_relief(graph, z, k_s, s_crit, runoff_eff, uplift, cfg) -> (relief, z_mean)`. L0에서만 씁니다.

- **하천 시작점.** Q* = (k_s/S_crit)^(1/θ), A* = Q*/R_eff입니다. 알려진 빈칸: 설계도 5장 9번의 Kargère 2025 식으로 바꿀 예정입니다.
- **칸 안 하천 기복.**
    - Hack 법칙 L(A) = c_H·A^h(km, km²)를 역으로 씁니다: A(x) = (x/c_H)^(1/h).
    - A* 지점 x*부터 칸 면적 지점 x_c까지 ∫ k_s·(R_eff·A(x))^(−θ) dx를 로그 간격 32점 사다리꼴로 적분합니다.
    - A* ≤ 1 m²이거나 값이 유한하지 않은 칸(k_s ≈ 0)은 적분이 무한대가 되므로 0으로 둡니다.
- **사면 기복.** L_h = 0.5·sqrt(A*), 기복 = L_h·min(S_crit, U·L_h/D)/2입니다.
- **결과.** relief = 하천 + 사면(바다 0), z_mean = z + `mean_fraction`·relief입니다.

## 8. subsurface (4단계, 사슬 3)

### 8.1 물 `subsurface/water.py`

`water_bodies(graph, z, result, is_ocean, cfg) -> fields`

- **호수.** ẑ = fill_depressions(z)에서 ẑ − z > 1e-3 m인 육지 칸이 호수입니다.
- **강.** Q/SECONDS_PER_YEAR ≥ `min_discharge_m3_per_s`인 육지 칸입니다. 폭은 W = k_W·Q_s^0.5, 깊이는 D = k_D·Q_s^0.4입니다(Q_s는 m³/s).
- **수면 h_w.**
    - 바다는 0, 호수는 ẑ, 강은 z − 0.2·D입니다.
    - 상류부터 h_w(r(c)) ← min(h_w(r(c)), h_w(c))로 역류를 막습니다(가이드 6장).
    - 물이 없으면 NaN입니다.
- **골짜기 깊이.**
    - 강 칸마다 반경 `valley_window_m` 안 칸들의 최고 z를 구합니다. 그래프 이웃을 따라 그 거리만큼 퍼뜨린 최댓값입니다.
    - 골짜기 깊이 = 그 최고 z − h_w입니다.
    - 다른 칸은 가장 가까운 강의 값(nearest_source로 넘겨받음)을 씁니다.

### 8.2 흙 `subsurface/soil.py`

`soil_and_alluvium(z, slope, uplift, temperature, precip, result, fan_raise, surface_rock, cfg) -> fields`

- **흙 생산 상한.** P₀ = min(`production_max_cap`, `production_max`·e^(0.07(T−15))·min(2, P/1 m/yr))입니다. 습하고 따뜻하면 mm/yr까지 허용합니다(설계도 5장 14번).
- **흙 두께.**
    - 깎임은 E = max(U, 1e-7)입니다.
    - h* = h₀·ln(P₀/E)(P₀ > E일 때, 아니면 0)이고, `thickness_cap_m`으로 자릅니다.
    - 경사가 `bare_slope`보다 가파르면 0이고 `bare_rock`입니다.
- **충적층.**
    - 퇴적이 지배하는 칸(G·R_ref·Qs/Q > U, 경사 < 0.02)에 쌓입니다.
    - 두께는 H = `alluvium_max`·sst((ln Q − ln 1e6)/(ln 1e9 − ln 1e6))·(1 − sst(S/0.01))입니다(가이드 6장 모양).
    - 선상지에는 올린 높이를 더합니다.

### 8.3 지하수면 `subsurface/groundwater.py`

`water_table(graph, z, water_level, is_water, surface_rock, slope, runoff, cfg, gravity) -> (z_gw, diag)`. Fan 2013·Haitjema 2005를 줄인 어림식이고, 발표에서 '물리 모델'이라 부르지 않습니다.

- **물가 거리.** 물 칸(바다·호수·강)에서 nearest_source로 δ와 출발 칸 s를 구합니다. h_d = water_level[s]입니다.
- **길이 L의 정의(설계도 5장 10번).** 같은 물가를 출발점으로 하는 칸들 가운데 가장 먼 δ, 즉 그 물가 영역의 분수령까지 거리입니다.
- **투수와 대수층.**
    - 수리전도도는 K_h = 10^(log k)·ρ_w·g/μ × SECONDS_PER_YEAR [m/yr]입니다.
    - 대수층 두께는 b = α/(1 + β·S)이고, 투수량 계수 T = K_h·b입니다.
- **함양.** R_g = `recharge_fraction`·유출입니다.
- **수위.** 띠 대수층 Dupuit 해 H = R_g·δ·(2L − δ)/(2T) ≥ 0을 쓰고, z_gw = h_d + H입니다.
- **규칙.**
    - z_gw ≤ max(z, h_w)이고, 물 칸에서는 z_gw = h_w입니다. make_figures 586행 버그를 고친 규칙입니다.
    - z − `max_depth_m`보다 낮아지지 않게 합니다.
- **진단.** 지하수가 땅 겉 0.5 m 안에 닿는 육지 비율입니다(창발 지표, 지구 22~32%).

### 8.4 동굴 층 `subsurface/caves.py`

`cave_levels(graph, z, z_gw, valley_depth, columns, cfg) -> fields`

- **층 높이.** 층 k(0..levels−1)의 높이는 z_k = z_gw + k·f_k·골짜기 깊이입니다. 0층은 지금 지하수면이라 잠기고, 위층은 옛 지하수면이라 마릅니다(설계도 5장).
- **녹는 암석.** z_k에서 `rock_at`이 녹는 암석(석회암·대리암)일 때만 값이 있고, 아니면 NaN입니다.
- **덮개.** z_k < z − 2·passage_radius여야 땅속입니다.
- **입구.** |z − z_k| ≤ `entrance_tolerance_m` + passage_radius이고 녹는 암석이면, 그 층이 골짜기 벽·지표와 만나는 곳이라 `cave_entrance` 비트를 켭니다. 동굴이 있는 칸과 이웃이면 반드시 켭니다.

## 9. hero (L2 히어로 유역)

| 모듈 | 함수 | 계약 |
|---|---|---|
| `hero/finder.py` | `find_hero(planet, cfg) -> HeroSite` | L0 육지 칸에 점수를 매기고 가장 높은 칸을 고릅니다. 점수 = 0.3·융기 기울기(정규화) + 0.4·지표 탄산염 + 0.15·기복(정규화) + 0.15·200 km 안 건조 칸 비율. 동굴이 보이려면 탄산염이 있어야 하므로 탄산염을 가장 무겁게 둡니다. 위도 55° 넘으면 0점. HeroSite: 중심 단위 벡터, 동·북 단위 벡터, L0 칸, 점수 내역 |
| `hero/domain.py` | `hero_graph(site, cfg) -> (graph, unit_points)` | 접평면 평면 격자(한 변 `profile.hero.size_m`, 간격 `spacing_m`, 노드 흔들기). 국소 (동, 북)를 구면 점으로 바꿔 L0 값을 표본합니다. 영역은 이 두 값으로 늘리고 줄입니다(`--set profile.hero.size_m=40000.0`). 한 변은 간격의 가장 가까운 정수배로 맞추고(실제 값은 `diag['grid']`), 칸 수가 5000²를 넘으면 오류입니다. 영역이 클수록 바다에서 먼 후보 칸이 필요하고, 회랑은 영역 안으로 줄여 맞춥니다. 거친 격자 한 변이 16칸보다 작으면 먼저 풀기를 건너뜁니다 |
| `hero/refine.py` | `refine_hero(planet, site, cfg) -> HeroState` | L0 값 보간(U, 유출, 기온, 강수, 깎인 두께, 습곡 위상, 템플릿은 nearest) → 지질 기둥 → 경계조건 → 2~4단계를 같은 함수로 |
| `hero/flat.py` | `flat_hero(cfg) -> HeroState` | L0 없이 가짜 경계조건: 남북 방향 섭입 단면 U(y) = 0.1 mm/yr + 2 mm/yr·exp(−((y − 0.3L)/0.25L)²), 템플릿 1, 유출 0.5 m/yr, 기온 15 °C, 출구는 남쪽 가장자리 가운데 5칸(z = 200 m) |

**경계조건(설계도 3장 가벼운 버전).**

- **출구.** L0 수신 셀 방향으로 히어로 가장자리와 만나는 곳의 5칸이고, z_outlet은 그 위치의 L0 고도(골짜기 바닥)입니다.
- **나머지 가장자리.** 닫힌 칸입니다(일반 칸, 출구 아님). 그래서 영역 전체가 하나의 유역이 됩니다.
- **들어오는 물.** L0에서 상류 칸들이 히어로 영역으로 보내는 유량 Q_in = Q_L0(중심) − A_hero·R_eff가 0보다 크면, L0 기여 셀 방향 가장자리 칸 하나에 `extra_inflow`로 넣습니다.
- **이음매.** L0과 만나는 띠는 엔진에서 안개로 가립니다.

**HeroState.**

| 이름 | 내용 |
|---|---|
| `graph` | 평면 그래프 |
| `fields` | 필드 dict |
| `columns` | 지질 기둥 |
| `site` | HeroSite 또는 None |
| `rivers` | 강 구간 목록 |
| `fan_apexes` | 선상지 꼭짓점 |
| `diag` | 진단값 |

## 10. volume (L3 3D 샘플 함수, 가이드 7장)

`HeroVolume(hero_state, cfg)`

- **입력.** 히어로 지도들을 쌍선형 보간기로 감쌉니다. 강 구간은 Catmull-Rom으로 부드럽게 해 1 m 간격 점과 KD-tree로 둡니다.
- **`sample(points (M,3)) -> (d float32, material uint8, water bool)`.** 점은 국소 좌표 (동, 북, 위) [m]입니다.

`sample`이 하는 계산은 다음과 같습니다.

1. 지표: d₀ = 위 − (z_s + m·A_d·ξ(x))입니다. A_d = 2 m, m은 물에서 30 m 안이면 0인 smoothstep, ξ = fbm3(x/40 m)입니다.
2. 강바닥: 가장 가까운 강 점까지 수평 거리 ℓ과 W, D, 둑 높이 z_q로 d_riv = min(W/2, D)·(sqrt((ℓ/(W/2))² + (v/D)²) − 1)이고, v = 위 − z_q입니다. 그다음 d₁ = smax_k(d₀, −d_riv), u = d₁ < 0입니다(가이드 7장).
3. 재질
    - 지표 아래 깊이 < 흙 두께이면 흙(11)이고, 충적층이 있으면 충적층(0)입니다.
    - 그 밖에는 `rock_at`입니다(층마다 변성 적용).
4. 동굴: 층 k마다 d_k = max(|ν₁(x,y)|·λ_c − r, |위 − z_k| − r)입니다. ν₁은 수평 2D fbm(파장 `noise_wavelength_m`), r = `passage_radius_m`, λ_c = 파장/(2π)입니다.
    - 녹는 암석이 아니거나 z_k가 NaN이면 d_k = +∞입니다.
    - 입구 칸에서는 골짜기 쪽 수평 통로(캡슐 거리)를 합쳐 입구가 반드시 열리게 합니다.
    - d = max(d₁, −min_k d_k)입니다.
5. 물: w = (d > 0) ∧ (위 < (u이면 z_gw, 아니면 h_w))입니다(가이드 7장 물 규칙).

`slices.py`의 `vertical_slice(volume, p0, p1, z_range, res) -> image`는 확인용 단면(재질 색 + 물 + 지하수면 선)을 만듭니다.

## 11. bake (출력)

| 모듈 | 함수 | 계약 |
|---|---|---|
| `bake/bundle.py` | `write_bundle(path, graph, fields, meta)` / `read_bundle(path)` | `manifest.json`(bpcg 버전, git 커밋, 설정 해시, 그래프 종류·모양·R·간격, `face_basis`, 필드 목록: 이름·파일·dtype·단위·그룹·모양) + 그룹 폴더의 `<이름>.npy`. 그래프는 `graph.npz`(pos, nbr, dist, area) |
| `bake/corridor.py` | `bake_corridor(hero_state, cfg, out_dir, engine_dir=None)` | 아래 표 |
| `bake/textures.py` | `face_textures(planet, out_dir)` | 면 6개 PNG(평균 고도 색, 판, 해양저 나이, 강수). 궤도 장면용 |
| `bake/globe.py` | `bake_globe(planet, cfg, out_dir, hero=…, face_basis=…, engine_dir=…)` | 엔진 지구본 장면의 자료 `globe/`: `globe.json`(면 기저, 등각 대응 규칙, 필드마다 이름·단위·색표·설명·읽는 법) + 면당 N²(기본 256) 필드 `.bin`과 (N+1)² 모서리 고도. Godot 좌표 = (x, z, −y). 형식은 engine/GLOBE.md |

**회랑 굽기.**

- **회랑 고르기.** 폭 `width_m` × 길이 `length_m` 직사각형입니다. 선상지 꼭짓점(없으면 출구 쪽 큰 강)에서 출발해 본류를 거슬러 오르는 방향으로 놓고, 동굴 입구를 가장 많이 포함하게 시작점을 강 위에서 옮겨 고릅니다.
- **쓰는 파일(`out_dir`, 그리고 `engine_dir`가 있으면 `engine/baked/`에도 같은 이름).**

| 파일 | 내용 |
|---|---|
| `heightmap.bin/.json` | 회랑 지표, 간격 `voxel_m`, 강바닥 깎기 포함(`bake/heightmap.write_heightmap`) |
| `surround25.bin/.json` | 히어로 영역 전체 25 m 지표 |
| `water.bin/.json` | 수면(물 없으면 −10000) |
| `water_table.bin/.json` | 지하수면 |
| `caves.glb` | 동굴 층 띠 안에서 SDF를 `voxel_m`으로 marching cubes, NORMAL과 COLOR_0(재질 색) 포함 |
| `strata.u8/.json` | 회랑 재질 부피(수평 4 m, 수직 2 m, uint8). 위는 `strata_top`을 따라감 |
| `strata_top.bin/.json` | 재질 부피의 윗면(수평 4 m) |
| `cave_mouth.bin/.json` | 지표 점의 동굴 거리 d_cave(높이맵 격자, ±20 m로 자름). 엔진은 음수인 곳의 지형을 뚫어 동굴 입구를 보입니다. SDF라 쌍선형 보간해도 구멍 가장자리가 매끄럽습니다 |
| `heightmap_detail.bin/.json` | `heightmap`에 프랙탈 디테일을 더하고 새로 생긴 웅덩이를 채운 지표(같은 격자, `bake/detail.py`). 보기용이고 솔버 결과가 아닙니다. `detail.fractal_gain` > 0일 때만 씁니다 |
| `cave_mouth_detail.bin/.json` | `cave_mouth`와 같되 d_cave를 `heightmap_detail` 지표에서 잰 값. 엔진의 '프랙탈 디테일' 층이 켜져 있으면 이것으로 지형을 뚫습니다 |
| `entrances.json` | 동굴 입구 캡슐 양 끝(엔진 좌표): `inside`는 통로 한가운데, `outside`는 산비탈 밖 공중. 엔진의 입구로 옮겨 가기(T)가 씁니다. 목록을 줄이지 않고 씁니다 |
| `manifest.json` | 국소 좌표 원점(구면 단위 벡터, 동·북 벡터, 위경도), 회랑 사각형, 파일 목록, 시드, 설정 해시, git 커밋 |

엔진 축은 X = 동, Y = 위, Z = 남입니다. 히어로 평면의 (동, 북)은 엔진의 (X, −Z)입니다.

**프랙탈 디테일(`bake/detail.py`, 설정 `[detail]`).**

- **현상.** 실제 산지는 작게 볼수록 계속 거칩니다. 고도 2D 파워 스펙트럼 P(k) ∝ k^−β에서 GLO-90 산지 타일 16개는 β ≈ 3.0(2–15 km), β ≈ 4.2(0.2–2 km)이고, 히어로(25 m)도 2.98, 4.26으로 맞습니다. 그런데 2 m 회랑은 4–20 m에서 β ≈ 4.74로 매끈합니다. 25 m 격자는 칸 2개(50 m, 나이퀴스트)보다 짧은 파장을 그리지 못하고 그 근처도 보간으로 약해지며, 회랑은 그 지도를 쌍선형 보간한 뒤 40 m fbm(진폭 2 m) 하나만 더하기 때문입니다.
- **규칙.**
    - 이음 기준: 회랑 지표에서 히어로가 그린 가장 짧은 옥타브(`max_wavelength_m` ~ 2배)의 띠 RMS a를 잽니다.
    - 자기 아핀 잡음: 전역 칸 번호의 정수 해시(시드 갈래 7301)로 만든 흰 가우스 잡음을 FFT로 걸러 P ∝ k^−(2+2H)(`hurst` = H)를 [1/`max_wavelength_m`, 1/`min_wavelength_m`] 띠에만 둡니다. 띠 첫 옥타브의 RMS가 `fractal_gain`·a·2^−H가 되게 맞추므로, 파장이 반으로 줄 때마다 거칠기가 2^−H배로 이어집니다. 디테일은 원래 지표에 더해지고, 원래 지표의 같은 띠 성분(40 m fbm 등)은 그대로 남습니다.
    - 어디에 얼마나: 계수 m = clip(경사/`slope_ref`, 0, 1) × (지표 0.5 m 아래가 흙·충적층이면 `soil_factor`) × 물 항 × 동굴 항 × 웅덩이 항 × 가장자리 항.
        - 물: 물 칸에서 `water_margin_m` 안은 0, 그 뒤 20 m에 걸쳐 1. 물 칸은 비트까지 그대로입니다.
        - 동굴: 원래 지표의 입구 구멍(`cave_mouth` < 0)에서 2 m 안은 0, 그 뒤 10 m에 걸쳐 1. 입구 둘레를 들어 올리면 지표 아래로 잘라 둔 동굴 벽과 지표 사이에 틈이 생기기 때문입니다.
        - 웅덩이: 원래 지표의 닫힌 웅덩이 칸은 0, 그 뒤 6 m에 걸쳐 1. 원래 우묵한 곳은 비트까지 그대로입니다.
        - 가장자리: 회랑 가장자리에서 `edge_fade_m`에 걸쳐 0 → 1. 주변 25 m 지형과의 단차를 늘리지 않습니다.
    - 웅덩이 채우기: 더한 잡음이 만든 닫힌 웅덩이를 priority-flood로 채웁니다. 출구는 가장자리·물 칸, 원래 웅덩이 칸, 동굴 입구 구멍(물이 빠지는 싱크홀)입니다. 채운 곳은 평평한 작은 면이 됩니다.
    - `water`, `strata`, `caves`는 바꾸지 않습니다. `fractal_gain` = 0이면 `*_detail` 파일을 쓰지 않고, 지난 굽기의 파일도 지웁니다.
    - 설정: `[detail]`은 굽기에서만 쓰는 절입니다. 그 절이 생기기 전에 만든 히어로 묶음은 지금 `configs/planets/<행성>.toml`의 값으로 굽고(`cli.bake_config`), `bpcg bake --set detail.fractal_gain=2.0`처럼 굽기 키(`detail.*`, `profile.corridor.*`)만 바꿔 다시 구울 수 있습니다.
    - 엔진: 디테일을 켜면 지형 색칠과 단면이 재질 부피의 층 깊이를 그린 지표에서 잽니다(윗면 + 디테일 − 기본 지표). 그러지 않으면 디테일이 낮춘 골에서 흙 아래 암석 색이 얼룩으로 보입니다.
- **지표.** `manifest.file_meta.heightmap_detail.detail`에 a(`anchor_rms_m`), 더한 RMS·최댓값, 회랑 가운데 정사각 창의 4–50 m β 전후(`beta_before`, `beta_after`), 채운 칸 수(`n_filled`)를 적습니다. earth_v2 회랑(2 m)에서 a = 3.4 m(50–100 m 띠)입니다. 기본 `fractal_gain` = 2는 RMS 1.1 m(최대 7.0 m), β 5.00 → 3.83, 채운 칸 1.8%이고, 1이면 RMS 0.55 m, β 4.28, 채운 칸 0.3%입니다. 이 회랑은 어디서나 흙이 덮여 `soil_factor`가 계속 걸리므로, 사용자가 '조금 강조'를 원해 기본을 2로 두었습니다.

## 12. metrics (점수표, 설계도 7장)

`scorecard(planet=None, hero=None) -> dict`. 항목마다 `{"value", "unit", "kind": "forced"|"emergent"|"check", "pass": bool|None, "note"}`를 둡니다.

| 지표 | 종류 | 기준 |
|---|---|---|
| 바다 비율 | emergent | 보고(지구 0.708) |
| 대륙붕 넓이(대륙 지각, 수심 < 200 m) | emergent | 보고, 지각평형 흉내라는 단서 |
| 고도 분포 봉우리 2개(면적 가중 히스토그램) | forced(반쯤 입력) | 봉우리 두 개가 1 km 넘게 떨어짐 |
| Hack 법칙 지수(본류) | emergent | 보고(지구 약 0.49~0.6) |
| 평탄지 비율(L2, 경사 < 0.02) | emergent | 보고 |
| 지하수가 땅 겉에 닿는 육지 비율 | emergent | 보고(지구 22~32%) |
| 하천이 바다·호수·출구에 닿는 비율, 역류 구간 | check | 100%, 0 |
| 법칙 자기일관성 | check | 중앙값 < 1e-3 |
| 퇴적물·물 수지 | check | 1e-6 |
| 경사를 만든 암석 = 보이는 암석 | check | 100%(지표 표본 1만 점) |
| 동굴 ⊂ 녹는 암석, 물 규칙 위반 | check | 100%, 0 |
| 격자 정렬 지수(가이드 2장) | check | 면 경계 띠와 안쪽 모두 보고, 목표 < 1.3 |

## 13. 실행

| 명령 | 하는 일 |
|---|---|
| `uv run bpcg planet --profile laptop --out out/earth` | 1~4단계, L0 묶음, 면 텍스처, 점수표 |
| `uv run bpcg hero --from out/earth` | 히어로 L2 (L0 경계조건) |
| `uv run bpcg hero --flat --out out/flat` | 평면 히어로 (가짜 경계조건) |
| `uv run bpcg bake --hero out/earth/hero --engine` | 회랑 굽기, 옆에 행성 묶음이 있으면 지구본(`globe/`)도. `engine/baked/`에도 씀. `--set detail.*`, `--no-globe` |
| `uv run bpcg all --profile tiny --out out/tiny` | 전부 한 번 |

파이프라인 함수는 `bpcg/pipeline.py`의 `generate_planet(cfg)`, `generate_hero(cfg, planet=None)`, `bake(hero, cfg, out_dir, engine_dir)`입니다.

## 14. 알려진 빈칸 (설계도 5장 18번 포함)

- **데이터 학습 전 버전.** θ, k_s, S_crit은 문헌 기본값입니다(설계도 6장 이후 `configs/learned/`).
- **하천 시작점.** Q* 식은 Kargère 2025 원문 식으로 바꿀 예정입니다.
- **더 넣을 과정.**
    - 바다로 나간 퇴적물이 대륙붕에 쌓이는 과정
    - 굽힘 지각평형(전륙분지·해구 바깥 융기)
    - 닫힌 호수 물수지(여유)
    - 화산 원뿔(여유)
    - 지형성 강수(여유)
- **확인이 필요한 값.** Gleeson 2011 투수성 값과 Fan 2013 α·β는 원문 확인이 필요합니다.
- **손 보정 뒤 남은 문제(earth_v2, laptop).**
    - 히어로 솔버에서 진동으로 고정된 칸이 30%입니다.
    - 선상지 판정 `slope_drop_ratio` 4는 n = 1 기준입니다. n = 2에서는 경사 차이가 융기 차이의 제곱근이라, 평면 히어로에서는 선상지가 생기지 않습니다. 실제 히어로에는 선상지 2,874칸이 있습니다.
    - `soil.bare_slope` 0.8이 규암을 뺀 모든 S_crit 이상이라, '가파르면 맨 암반' 규칙이 거의 걸리지 않습니다.
        - 그래서 earth_v2 회랑은 지표 0.5 m 아래가 모두 흙(두께 1.6–1.8 m)이고, 프랙탈 디테일의 흙 항이 어디서나 `soil_factor` 0.35입니다. 가파른 암반만 더 거칠게 하는 대비는 흙 모델이 맨 암반을 만들어야 보입니다.
    - 육지의 91~96%에서 지하수면이 지표에 닿습니다(지구 22~32%).
    - 동굴 입구가 너무 많고, 단면에서 지표 가까이 큰 빈 곳이 보입니다.
    - 바다 비율은 0.625입니다(지구 0.709).
    - 아래층 동굴이 지하수면에 붙어 있어, 지하수면이 지표에 닿는 강가에서 동굴이 지표를 뚫습니다. earth_v2 회랑에서는 지표 점의 6.6%가 동굴 입구 구멍입니다(`cave_mouth` < 0).
