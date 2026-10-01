## 무엇을 바꿨나

<!-- 한두 문장. 관련 이슈가 있으면 #번호 -->

## 왜

<!-- 설계도 어느 장·사슬·스케일과 관련 있는지 -->

## 확인

- [ ] `uv run pytest -m "not slow"` 통과
- [ ] `uv run ruff format . && uv run ruff check .` 통과
- [ ] 단위와 배열 모양을 docstring에 적음
- [ ] 절대 경로, 전역 난수, GPL import 없음 (analysis/ 제외)
- [ ] 1 MB 넘는 파일을 커밋하지 않음
- [ ] 문서(설계도·README·docs)를 같이 고쳐야 하면 고침
