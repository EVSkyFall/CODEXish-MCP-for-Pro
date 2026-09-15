# v1 구현 계획 — v0.2

기준일: 2026-09-15. 출처: 사용자 next-input-package §D.5–D.8. 설계: [v1-design.md](v1-design.md). 실제 증거: [IMPLEMENTATION_STATUS.md](../IMPLEMENTATION_STATUS.md).

| 슬라이스 | 범위 | 상태와 완료 근거 |
| --- | --- | --- |
| 1. 코딩 코어 | host/workspace/fs/shell/process/git-read/artifact/operation 20개 + checkpoint, roots/grants, SQLite/FIFO/persist_failed, 내장 OAuth, 로컬 control | PR #3 구현. c478da8의 CI run 34963266835: Windows 224, Linux 218 검사가 정상 종료·정리까지 통과. 설정·연결 문서는 후속 docs commit. 실제 사용자 OAuth/Chat 호출 검증은 별도 잔여 |
| 2. Computer Use | computer.observe/query_ui/act, focus_window, UIA role/name/bounds/focused/활성 문서·탭, virtual desktop 좌표, 행동 후 관찰, 부분 입력 해제 | NOT_STARTED. P0 코드는 --p0로 보존. 실제 메모장과 사용 앱에서 focus 복구→본문 입력→Save As→저장 byte 확인, mixed DPI/모니터·stale·부분 입력 시험으로 완료 판정 |
| 3. 브라우저 마운트 | 외부 stdio Playwright MCP/Chrome DevTools MCP를 browser.*로 프록시 | NOT_STARTED. 직접 브라우저 어댑터를 만들지 않음. 연결·목록·호출·오류·child 종료·스키마 전달과 실제 개발 서버 확인 흐름으로 검증 |
| 4. 트레이·설치 UX | 기존 /control을 감싼 tray, 로컬 설정·연결·첫 로그인 UX | NOT_STARTED. 사용자 세션 프로세스 유지. 설치→시작→연결→pause/resume→child 종료→토큰 철회와 종료 후 정리 시험 |
| 5. 잔여·확장 | worktree/LSP/computer.batch/Secure MCP Tunnel 통합/다중 PC 등 | 이번 PR 범위 밖. 코어 계약 잔여와 검증 격차를 먼저 분리 추적 |

## 슬라이스 1의 실제 검사 범위

Files: 임시 root에서 create/read/list/search/stat/replace, hash 불일치 보존, 상태 디렉터리 `.bak`, UTF-16 BOM/CRLF와 P0 UTF-8 보존, unified diff context/위치/줄바꿈/파일별 부분 결과. Windows junction와 공유 핸들, Unix symlink를 각 환경에서 시험했다.

Operations: 동일 ID 합류·다른 인자 충돌, 수락 순 FIFO, root alias, 독립 자원·읽기 진행, HTTP context 종료 전 session 캡처, pause/resume, queued 취소·재시작 cancelled, started 재시작 unknown. 실제 SQLite query_only 설정으로 최종 commit을 실패시켜 memory unknown(persist_failed)와 효과 미재실행을 확인했다. 별도 4개 회귀 검사는 취소된 중간 작업이 선행 barrier를 끊지 않는지 확인한다.

Processes/Git: 실제 executable exit 7·cmd/sh exit 3, stdout/stderr 독립 순서, wait handle·자식 생존·stdin UTF-8, Windows Job launch gate, session/persistent 분리·stop·drain. 임시 Git 저장소의 악성 textconv 양성 대조 후 고정 조회 도구에서 hook/fsmonitor/textconv/pager/external diff/clean/process fixture가 실행되지 않는지 확인했다.

HTTP/Auth/Control: 실제 HTTP MCP initialize/list/call/fs.write, 21개 도구 스키마, instructions, OAuth 401/discovery→login ticket/cookie→PKCE→token→authenticated MCP, redirect/resource/client/verifier 오류, 코드 단일 소비·만료, access 12h·refresh 회전·재사용 철회. 다른 listener/공개 Host/없는 제어 token 거부와 local pause/resume/revoke를 시험했다. 인증 값은 테스트 전용이다.

## 슬라이스 1의 잔여 계약과 실환경 검증

- fs.apply_patch의 파일 생성·삭제·rename은 미구현 변형이다. 현재 기존 파일 수정과 파일별 부분 결과만 지원한다. 새 파일은 fs.write create로 처리한다.
- PTY/ConPTY, 대용량 streaming view의 효율화·artifact 보존/정리 정책, 모든 프로세스·스토리지 실패 조합은 완료 항목에 포함하지 않는다.
- 실제 ChatGPT OAuth 연결, 모델별 도구 사용, 장시간 실행·부하 시험과 독립적인 새 코드 보안 검토는 아직 없다.
- P0 M-1–M-8은 [실측 원장](p0-measurement-record.md) 그대로 미기록이다. 새 서버 CI가 그 빈칸을 채우지 않는다.

## 후속 작업 규칙

첫 PR의 사용자 지시 범위는 별도 재승인을 반복하지 않는다. 현재 명시된 잔여·차이를 실제 증거로 갱신한다. 다음 computer 슬라이스에서 v1 도구 등록을 추가하되 P0와 혼합해 기존 측정값을 바꾸지 않는다. 사용자 변경·이전 reviewer 기록을 보존하고 자동 merge/force push를 하지 않는다. 최종 v1 완료는 슬라이스 1의 도구 이름 존재가 아니라 코드와 실제 작업 흐름 검증으로 판정한다.
