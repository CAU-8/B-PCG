# configs/learned

실제 지형 자료에서 맞춘 값(가이드 3장, 설계도 6장)을 두는 곳입니다. 손으로 고치지 않고 `analysis/` 의 맞추기 스크립트가 씁니다.

- `stream_power.toml`: `theta_ref`, `ks_lo`, `ks_hi`, `s_crit`, `q_unit = "m3/yr"` 와 비교용 k_sn. 아직 없습니다(3~4주차 θ 파일럿).
- OCTOPUS 로 맞춘 표와 회귀 계수는 파일 머리에 `# 라이선스: CC BY-NC-SA 4.0, OCTOPUS 유래` 를 적어 코드 라이선스와 구분합니다.
- 파일마다 만든 스크립트, 입력 자료 버전, git 커밋을 머리 주석으로 남깁니다.
