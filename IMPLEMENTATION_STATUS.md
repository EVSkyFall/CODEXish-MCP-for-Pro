# 실제 구현·검증 상태

## Latest triage follow-up (2026-09-15)

The user requested the supplied Codex/Claude triage. Preserve reviewer commits `9137ce3` (F-1/F-2) and `52147cd` (delivery/CI evidence). The first attempted ref update detected those concurrent commits and was rejected as non-fast-forward; no force push was used. This change is based on `52147cd57abb85857b4f952fb33384c269aa7baa` and adds only L-1 plus H-1/H-2/H-3 procedure corrections and the explicit non-FIFO P0 contract.

L-1 maps missing selected Notepad in Current() to WINDOW_NOT_FOUND / side_effects=none before input. The new Windows-only regression covers construction with a missing PID, not an initialized Notepad exiting at runtime; the latter branch is reviewed statically. No additional GUI tool or v1 feature is introduced.

**Current code: `41afc82e25ced21421d0998d2257977a407e3a75`, pushed to PR #2 without force.** GitHub Actions pull_request run [34933335170](https://github.com/EVSkyFall/CODEXish-MCP-for-Pro/actions/runs/34933335170) completed successfully. The checkout was PR merge ref `79545138b4f4f1afb6536d9cc59f4a234e451dfe` for this head. Job metadata and both complete job logs were fetched through the GitHub connector; the results below are actual runner output, not the earlier reviewer report.

| Runner / SDK | Job ID | Restore / Release build / self-test | Compiler | Self-test checks | Schema artifact ID |
| --- | --- | --- | --- | --- | --- |
| Windows Server 2025 10.0.26100 / .NET 10.0.401 | 104265935383 | SUCCESS / SUCCESS / SUCCESS | 0 warnings, 0 errors | 97 PASS | 10381949733 |
| Ubuntu 24.04.5 / .NET 10.0.401 | 104265935532 | SUCCESS / SUCCESS / SUCCESS | 0 warnings, 0 errors | 93 PASS | 10381899772 |

Windows job completed 2026-09-15 05:36:21 UTC; Ubuntu completed 05:35:31 UTC. Windows log PASS 76 is the new missing-PID constructor check; PASS 94–95 use synthetic bitmaps for PNG encoding. Linux skips that constructor, the two bitmap checks and the Windows file-sharing check. Its 93 checks are not four failures. Both logs finish with SELF_TEST_PASSED and explicitly state that Chat and interactive desktop measurements were not performed. The workflow emits Node.js/action deprecation warnings separately; zero compiler warnings does not mean the entire workflow log contains no warnings. Artifacts contain SDK tool schemas, not GUI evidence.

Local diff/encoding/JSON/XML checks and clean application of the incremental patch onto the exact `52147cd` file subset also passed. This container still has no dotnet executable; compilation and self-tests above ran on GitHub runners. This status update changes documentation only and does not change the tested C# source.

Latest uploaded verification §§6a–6b reports F-1/F-2 96/96 tests and Notepad HWND/capture inspection with no clicks or input. The complete same-seven-tool reference journey, Save As behavior, and Pro/Thinking M-1–M-8 remain NOT_RUN. Follow README Measurement: fresh state, unsaved tab with image-only nonce, and ChatGPT on another device.

## Historical F-1/F-2 evidence retained from 52147cd

The records below are the preceding delivery record, not claims about the new triage commit. Later section labels and counts refer to their named commits.

기준일: 2026-09-15. 전체 v1: **CHANGES_REQUESTED**. 최신 첨부 검증 리뷰는 기존 P0 코드 `bdb4bf2`/문서 `0dd0b6b`에 대해 **APPROVE for P0 scope**, F-1/F-2 수정 후 실측 조건이다.

## F-1/F-2 후속 변경

같은 PR #2 브랜치에 명시적 Host/Origin 허용 목록·거부 로그, 기본 1280px 캡처·관찰별 좌표 변환을 추가한다. `click.coordinate_space`는 필수 인자이므로 커넥터 스키마를 새로고침한다. 상세는 [반영 기록](docs/p0-premeasurement-fixes.md), 사용법은 README.

**현재 전달 상태: PUSHED_BY_REVIEWER — commit `9137ce3` (2026-09-15).** GitHub create_tree 요청이 OpenAI의 보안 판정 단계에서 차단되어 이 assistant는 원격 브랜치를 수정하지 못했다. 사용자 지시로 독립 리뷰어(Claude Fable 5.1)가 `CODEXish-P0-F1-F2.patch`를 변경 없이 적용해 정확히 10개 파일을 commit·push했다(커밋된 blob sha256 10/10이 `VALIDATION.json`과 일치). push가 트리거한 CI: run 34932378478(push)와 34932381896(pull_request) 모두 success. windows-latest job 104263105954와 ubuntu-latest job 104263105648에서 restore/build/`--self-test`/artifact 전 스텝 success, self-test Windows 96개·Linux 93개(Windows 전용 검사 3개 skip) 통과, 컴파일 경고 0(CA1416 해소). 리뷰어의 로컬 검증(Windows 11 Pro 10.0.26200, .NET SDK 10.0.401)도 `dotnet build -c Release` 경고 0·오류 0, `--self-test` SELF_TEST_PASSED 96이다. 이는 이 assistant가 직접 실행한 결과가 아니며, 로컬 컨테이너는 여전히 .NET 미설치다. 아래 실제 실행한 CI 절의 run 34928723766은 수정 전 baseline의 결과다.

로컬에서 실행한 검사는 `git diff --cached --check`, 변경 텍스트 UTF-8/마지막 newline/trailing whitespace, Markdown fence 및 JSON 예시 파싱, csproj/manifest XML 파싱이다. 이 검사는 C# compilation/런타임 시험을 대신하지 않는다.

## 사용자 제공 독립 검증

첨부 `CODEXish-P0-verification-by-Claude.md` §1은 사용자 Windows 11 Pro 10.0.26200/.NET 10.0.401에서 restore/build 성공, 48개 self-test, HTTP smoke 재현을 보고한다. 이는 **리뷰어의 실행 보고**로 기록하며 이 assistant가 사용자 PC에서 직접 실행했다고 주장하지 않는다. 대화형 메모장 캡처·입력과 M-1–M-8은 여전히 미실측이다.

검증한 코드 commit: `bdb4bf223455dd770709308ab33cb131d6a9eab4`.
후속 상태 문서 수정은 위 코드의 검증 결과를 기록하며 새로운 실계정 시험을 뜻하지 않는다.

## 실제 실행한 CI

GitHub Actions run: https://github.com/EVSkyFall/CODEXish-MCP-for-Pro/actions/runs/34928723766

| 환경 | Job ID | restore / build / --self-test | 스키마 artifact |
| --- | --- | --- | --- |
| windows-latest | 104252250359 | 모두 SUCCESS | p0-evidence-windows-latest, ID 10381171483 |
| ubuntu-latest | 104252250555 | 모두 SUCCESS | p0-evidence-ubuntu-latest, ID 10380508178 |

2026-09-15 04:25 UTC 완료된 job 결과와 artifact 목록을 GitHub connector로 확인했다. 테스트는 실제 파일·SQLite DB·자식 프로세스 및 HTTP MCP 요청을 사용한다. 테스트 이름만 작성하고 통과로 처리한 것이 아니다.

검사 범위: expected-hash 실패 시 원본 보존, 변경 전 backup, UTF-8/UTF-16 BOM·줄바꿈 보존, 중복 재조회·인자 충돌, 대기 핸들·동일 자원 큐·독립 작업 진행, 고정 자식 검증기의 실패/성공 exit code, initialize·tools/list·tools/call, instructions on/off, 구조화 오류, Host/Origin 거부, 재시작 후 결과 보존·unknown. Windows job에는 공유 핸들 충돌 시 FILE_LOCKED 검사도 포함된다.

첫 실행(run 34928470689)의 Windows 로그에서 48개 검사 통과를 확인했다. 이 실행에는 SQLitePCLRaw.lib.e_sqlite3 2.1.11의 NU1903 경고와 상대 artifact 경로 오류가 있었다. 이후 native dependency를 2.1.12로 고정하고 NU1901–NU1904를 오류로 취급하며 artifact 경로를 절대 경로로 수정했다. 위 최종 run은 수정 후 재검증 결과다. Windows 플랫폼 분석 경고(CA1416)는 별개이며 경고 없는 빌드나 GUI 검증 완료를 주장하지 않는다.

## 상태 경계

| 항목 | 상태 |
| --- | --- |
| 단일 C# P0 서버·7개 도구·서버 측 프로토콜 시험 | 구현 및 CI 검증 |
| 주 모니터 캡처·선택한 메모장 입력 | 코드 빌드 완료, 실제 대화형 GUI 실행은 미수행 |
| M-1–M-8 Pro/Thinking 실측 | BLOCKED_EXTERNAL; 실제 값·스크린샷 없음 |
| Secure MCP Tunnel | 공식 문서 확인만 수행; 조직 권한·Windows client·요금 미확인 |
| OAuth·트레이·UIA·브라우저 MCP 연결·전체 23도구 | 미구현; P0 완료로 대체하지 않음 |
| 로컬 컨테이너의 .NET 실행 | .NET 미설치 및 다운로드 DNS 실패. 위 검증은 GitHub runner에서 수행 |

현재 P0는 고정 fixture를 수정하는 도구 루프 시험이다. 임의 프로젝트 소스코드 수리 시험이나 완성된 v1 에이전트가 아니다. 사용자 PC, Platform 조직, 터널, 인증정보는 변경하지 않았다. 외부 desktop 연결 검색에서 미설치 후보만 확인했으며 실제 장치 접근 권한은 얻지 않았다.

기존 v0.1 문서는 과거 제안으로 남아 있다. 해당 문서의 multi-process 선행 조건은 P0 예외를 막지 않지만, 전체 설계 승인이나 실측 완료로 자동 전환되지도 않는다.
