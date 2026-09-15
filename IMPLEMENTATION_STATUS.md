# 실제 구현·검증 상태

기준일: 2026-09-15. 전체 v1: **CHANGES_REQUESTED**. 최신 첨부 검증 리뷰는 기존 P0 코드 `bdb4bf2`/문서 `0dd0b6b`에 대해 **APPROVE for P0 scope**, F-1/F-2 수정 후 실측 조건이다.

## F-1/F-2 후속 변경

같은 PR #2 브랜치에 명시적 Host/Origin 허용 목록·거부 로그, 기본 1280px 캡처·관찰별 좌표 변환을 추가한다. `click.coordinate_space`는 필수 인자이므로 커넥터 스키마를 새로고침한다. 상세는 [반영 기록](docs/p0-premeasurement-fixes.md), 사용법은 README.

**현재 전달 상태: LOCAL_PATCH_ONLY / REMOTE_WRITE_BLOCKED.** GitHub create_tree 요청이 OpenAI의 보안 판정 단계에서 차단되었고 원격 브랜치 수정·새 CI 실행은 수행하지 못했다. 다른 쓰기 경로로 우회하지 않았다. 새 회귀 테스트는 작성했지만 실행·통과로 기록하지 않는다. 로컬 컨테이너는 .NET/C# compiler 미설치이고 2026-09-15 SDK 다운로드 URL 조회는 DNS 실패(curl exit 6)였다. 아래 CI 기록은 수정 전 baseline의 결과다.

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
