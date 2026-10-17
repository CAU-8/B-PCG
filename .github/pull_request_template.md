## 무엇을 바꿨나

<!-- 한두 문장. 리뷰어가 1분 안에 알 수 있게. 관련 이슈가 있으면 #번호 -->

## 왜

<!-- 이 변경이 없으면 무엇이 곤란한지. 설계도의 어느 장·사슬·스케일과 관련 있는지 -->

## 어떻게 확인했나

<!-- 돌린 명령과 결과 숫자. 화면을 바꿨으면 고치기 전·후 그림 -->

## 확인

- [ ] `dotnet build Bpcg.slnx && dotnet test --solution Bpcg.slnx` 통과
- [ ] `dotnet format Bpcg.slnx --verify-no-changes` 통과
- [ ] `uv run pytest -m "not slow"` 통과
- [ ] `uv run ruff format . && uv run ruff check .` 통과
- [ ] 함수 설명 주석에 단위와 배열 모양을 적음
- [ ] 절대 경로, `System.Random`·전역 난수, GPL import 없음 (analysis/ 제외)
- [ ] 1 MB 넘는 파일을 커밋하지 않음
- [ ] 문서·화면 글을 바꿨으면 CLAUDE.md 의 친절함 기준을 따름 (새 용어는 docs/glossary.md 에)
