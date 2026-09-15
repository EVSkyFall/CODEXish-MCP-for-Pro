# 실제 구현·검증 상태 — v1 slice 1

기준일: 2026-09-15. 작업 브랜치: feat/v1-slice1-coding-core, PR #3, base: feat/p0-review-response-20260915 (c1b04cd).

**상태: 첫 v1 코딩 슬라이스 구현 및 서버 측 CI 통과. 실제 Chat/GUI 측정은 미기록.** 최신 사용자 패키지 §D의 직접 구현 지시를 수행했으며, 새 코드에 대한 독립 리뷰 승인이나 전체 v1 완료를 뜻하지 않는다.

## A. 사용자 제공 검증 통보 반영

CODEXish-GPT-next-input-package.md §A는 리뷰어 Claude Fable 5.1이 사용자 Windows 11 Pro 10.0.26200 / .NET SDK 10.0.401에서 41afc82 코드와 c1b04cd 문서를 재현했다고 보고한다. Release build는 경고 0·오류 0, self-test 97이며 CI 34933335170/34933330570도 성공을 확인했다고 한다. L-1과 실측 절차는 승인되어 P0 코드·절차는 완료 상태다.

위는 리뷰어가 보고한 사용자 하드웨어 결과다. 아래 새 CI 결과는 이 assistant가 GitHub job 상태와 전체 로그를 직접 조회한 별도 증거다. 원본 P0 진행 기록은 [IMPLEMENTATION_STATUS.p0.md](IMPLEMENTATION_STATUS.p0.md)에 그대로 보존했다.

## B/C. 미기록 실측과 case

[docs/p0-measurement-record.md](docs/p0-measurement-record.md)에 §B 결과표를 그대로 기록했고 빈칸은 미기록으로 남겼다. R/T/P/P'의 호출 기록·이미지·저장 byte 증거는 없다. **Case=UNDETERMINED**이며 Case 0 실패 또는 Case 4 성공을 추정하지 않는다. PR #2는 해당 case 기반 ready 조건이 확인되지 않아 Draft를 유지한다. 서버 개발은 최신 proceed/continue 및 §D·§E 구현 지시를 근거로 진행했다.

## C. 검증한 코드와 실제 CI

코드 commit: `c478da8986a320defbac6db566160ea37049b11f`.
테스트 checkout: PR merge SHA `af9860e8ee3493123408b92e2e6238daf3d58f74`.
CI run: [34963266835](https://github.com/EVSkyFall/CODEXish-MCP-for-Pro/actions/runs/34963266835), pull_request, conclusion SUCCESS.

| OS / SDK | Job ID | Restore / Release build / self-test | 컴파일 경고 / 오류 | P0 | v1 slice | FIFO 회귀 | 합계 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Windows Server 2025 10.0.26100 / .NET 10.0.401 | 104361647043 | SUCCESS / SUCCESS / SUCCESS | 0 / 0 | 97 | 123 | 4 | **224** |
| Ubuntu 24.04.5 / .NET 10.0.401 | 104361646799 | SUCCESS / SUCCESS / SUCCESS | 0 / 0 | 93 | 121 | 4 | **218** |

Windows job 종료 2026-09-15 11:27:55 UTC, Ubuntu 11:26:41 UTC. 두 전체 로그에서 fixture cleanup 뒤 ALL_SELF_TEST_SUITES_PASSED와 정상 step 종료를 확인했다. V1 suite 자체의 TOTAL_PASSED 220/214는 P0+v1 소계이고 뒤 FIFO 4개를 포함한 총합이 224/218이다. OS별 차이는 Windows 고유 공유 핸들/PNG/생성자/Job 검사를 Linux에서 실행하지 않는 데서 온다.

Artifact: Windows `10393944179` (p0-evidence-windows-latest), Ubuntu `10393909056` (p0-evidence-ubuntu-latest). 각각 P0 schema, v1-tools.json, v1-test-results.json, fifo-regression-results.json의 4개 파일을 업로드했다. Windows zip SHA-256은 `2baf9989605446d15a88aaa3c10fd719707925c2b943a1940b25c6e9059704d9`, Ubuntu는 `c9429a2cccd3d96637ef951a6a3c4a4b74364632d8233f88b39b23f2cb5e82e6`다. 이 artifact는 GUI 캡처나 Chat 대화 증거가 아니다.

workflow의 Node/action deprecation 경고는 그대로 있다. 컴파일 경고 0과 전체 로그에 경고가 전혀 없다는 주장은 구분한다. 로컬 컨테이너에는 dotnet이 없고 SDK 다운로드 DNS 조회도 실패했으므로, C# 검증은 GitHub runner에서 수행했다.

## D. 이번 이어받기에서 수정한 실제 실패

이전 코드 09dabd28의 run 34949928577은 Windows에서 218개 assertion 뒤 Git의 read-only object 파일 정리에 실패했다. 중간 PASS 문구만으로 성공 처리하지 않았다. 22157cf는 새로 만든 시험 디렉터리에만 적용하는 read-only 정리 함수를 추가하고 reparse target을 따라가지 않는 회귀 검사, 정리 후 최종 성공 출력으로 수정했다.

c478da8은 FIFO 취소 경로를 보강했다. A 실행 중 queued B를 취소해 B의 결과가 먼저 완료되어도 C의 자원 barrier가 A를 포함하도록 한다. 별도 4개 검사가 즉시 취소 결과·C 대기·최종 [A,C] 순서·없는 취소 target의 NOT_FOUND를 확인한다. 기존 사용자/reviewer 커밋을 보존하고 force push하지 않았다.

## E. 구현·검증 범위

| 영역 | 실제 근거 |
| --- | --- |
| 파일 6개 도구 | 실제 임시 root의 읽기/list/search/stat/create/조건부 replace, mismatch 보존, .bak, encoding/BOM/줄바꿈, unified diff 부분 결과, junction/symlink 경로 거부 |
| operation·FIFO·checkpoint | 같은 ID 합류/충돌, root alias, 읽기·독립 작업 진행, pause/resume, 취소 barrier, checkpoint, 재시작 cancelled/unknown |
| persist_failed | 효과 뒤 SQLite PRAGMA query_only=ON으로 실제 SQLITE_READONLY를 유발; memory unknown·inspect·같은 ID 효과 미재실행 |
| 프로세스 | 실제 executable과 cmd/sh의 종료 코드, stdout/stderr 순서, stdin UTF-8, wait 핸들·자식 생존, Windows Job launch gate, session/persistent·stop·drain |
| Git | 임시 악성 textconv의 실제 실행을 양성 대조로 확인한 뒤 고정 status/diff/log에서 hook·fsmonitor·pager·textconv·external diff·clean/process fixture 미실행 확인 |
| 내장 OAuth와 MCP | 합성 client/password로 401·discovery·authorize·PKCE·token·21개 도구·실제 HTTP fs.write, redirect/resource/client/verifier 거부, 만료·회전·단일 소비·철회 |
| 로컬 control | 별도 listener, public Host와 없는 token 거부, MCP 포트는 loopback Host+올바른 token도 거부, local pause/resume/revoke |

전체 구현된 도구: host.capabilities, workspace.info, fs.list, fs.read, fs.search, fs.stat, fs.write, fs.apply_patch, shell.run, process.start, process.poll, process.write, process.stop, git.status, git.diff, git.log, artifact.read, artifact.search, operation.inspect, operation.cancel, session.checkpoint.

## F. 잔여·가정·정책

computer.observe/query_ui/act는 이번 등록 목록에서 제외했고 P0 NativeDesktop는 변경 없이 보존했다. 외부 브라우저 MCP 마운트, tray·설치 UX, worktree/LSP/batch/다중 PC는 예정된 후속 슬라이스다. fs.apply_patch의 생성·삭제·rename 변형, PTY/ConPTY, 대용량 보존/정리·부하 및 모든 실패 조합은 미구현/미검증이다. 기존 파일 수정 patch와 파일 create 도구가 있다는 이유로 이 변형들을 완료로 표시하지 않는다.

가정: §D의 직접 지시에 따라 서버 측 첫 슬라이스를 진행한다. 포트 기본값은 MCP 3000/control 3001이며 둘 다 같은 프로세스의 loopback listener다. 상태는 기본 LocalApplicationData/Codexish, credentials·callback·Git 위치는 사용자가 로컬에서 새 설정으로 채운다. HTTP 연결 종료와 product session 종료를 분리하며 refresh는 session을 유지한다.

정책 제안(미채택): 없음. 반복 승인/BUSY/작업 시간·호출 cap을 추가하지 않았다. 사용자의 기존 자격 증명·PC 보안·배포·safeguard 설정을 읽거나 변경하지 않았다. 실제 터널 생성·사용자 OAuth 로그인·Chat 모델 호출·대화형 GUI 측정은 수행하지 않았다.

[README.md](README.md)의 설치·연결 절차와 [docs/v1-plan.md](docs/v1-plan.md)를 따라 다음 실제 연결 및 후속 슬라이스를 진행한다. 새 코드에 대한 독립 리뷰와 사용자 merge는 별도다.
