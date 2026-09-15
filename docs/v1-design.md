# v1 설계 v0.2 — 첫 코딩 슬라이스

기준일: 2026-09-15. 결정 출처: 사용자 제공 CODEXish-GPT-next-input-package.md §D. 구현: PR #3, feat/v1-slice1-coding-core. 코드 기준: c478da8986a320defbac6db566160ea37049b11f.

## 1. 목표와 범위

결정: ChatGPT가 판단하고 단일 Windows MCP 서버가 파일 편집·실행·관찰을 제공한다. 별도 모델/API 키, Codex CLI 대리 판단, Gateway/Agent 분리, mTLS 등록, 멀티테넌트를 기본 경로에 넣지 않는다.

결정: 기존 src/Codexish.P0 프로젝트를 그대로 승격한다. 기본 실행은 v1이고 `--p0`는 기존 측정 도구 7개를 선택한다. 첫 슬라이스에는 코딩 도구 20개와 §D.7의 session.checkpoint를 등록하고 computer.*는 슬라이스 2로 둔다. 측정 case는 [실측 원장](p0-measurement-record.md)의 UNDETERMINED 상태와 별개로 관리한다.

## 2. 실행 구조와 파일

결정: C#/.NET 10, 공식 ModelContextProtocol.AspNetCore, Streamable HTTP `/mcp`, SDK 생성 스키마를 사용한다. P0 Reply envelope를 재사용하고 v1 schema_version은 v1.0이다. 실제 입력 필드와 description의 실행 원본은 V1/Tools.cs, CI의 v1-tools.json이다.

| 소스 | 역할 |
| --- | --- |
| V1/Core.cs | 설정·root grant·SQLite·invocation·FIFO·오류·redaction |
| V1/Files.cs | root-relative 경로·핸들 검사·파일 도구·unified diff·artifact cursor |
| V1/Processes.cs | 동일 바이너리 launcher·프로세스 감독·stdin/출력·Windows Job |
| V1/Auth.cs | 단일 사용자 OAuth AS·resource token 검증·PBKDF2 |
| V1/Server.cs | runtime·MCP/제어 endpoint·고정 Git 조회·instructions |
| V1/Tools.cs | 등록 도구 21개와 다음 행동 설명 |
| V1/SliceTests.cs, V1/FifoRegressionTests.cs | 실제 파일·DB·프로세스·HTTP 및 취소 순서 회귀 시험 |

결정: MCP 서버는 한 프로세스다. 외부 명령을 시작할 때는 같은 실행 파일을 짧은 launcher 모드로 사용하여 Windows Job에 먼저 편입한 다음 stdin gate로 사용자 명령을 해제한다. 이 launcher는 별도 MCP 서버·Gateway·설치 패키지가 아니다.

## 3. 연결·내장 OAuth

결정: 같은 프로세스가 loopback MCP 포트(기본 3000)와 loopback 제어 포트(기본 3001)를 연다. 터널은 MCP 포트의 전체 HTTP 경로를 전달한다. public_url은 `/mcp`가 없는 HTTPS origin이며, resource는 그 origin + `/mcp`다. Host/Origin 명시 허용 옵션과 거부 로그를 유지한다.

결정: 외부 IdP 없이 사전 등록 client 하나와 사용자 비밀번호 하나를 사용한다. 비밀번호는 PBKDF2-SHA256, 무작위 salt 16바이트, 반복 600000회, 결과 32바이트로 해시한다. 설정 파일은 client_id/client_secret/password_hash/redirect_uris를 보유한다. 사용자 계정의 기존 자격 증명 대신 이 서버용 값을 로컬에서 만든다.

결정: Authorization Code + PKCE S256, 정확한 redirect URI와 resource, state 반환, 로그인 ticket/cookie 바인딩을 사용한다. 서버는 protected-resource metadata와 AS metadata를 제공하고 `/authorize`, `/token`을 처리한다. 토큰 endpoint는 static client의 basic/post 인증을 지원한다. 코드 5분, 로그인 ticket 10분, access token 12시간, refresh token 30일이다. 이 만료는 작업 실행 시간 제한과 별개다.

결정: opaque token과 authorization code는 상태 DB에 해시로 저장한다. 코드는 단일 소비, refresh는 회전하며 재사용 시 해당 token family를 철회한다. 매 MCP HTTP 요청에 만료·resource·client·scope를 검사한다. `--no-auth`는 명시적 개발 옵션이며 listener는 loopback을 유지한다.

결정: logical session은 인증 토큰의 session에 바인딩한다. refresh는 같은 session을 유지하고 새로운 로그인의 token family는 새 session이다. HTTP 연결 종료는 product session 종료가 아니다. no-auth 모드의 session은 local이다.

## 4. 저장·FIFO·복구

결정: 기본 상태 디렉터리는 `%LOCALAPPDATA%\Codexish`이며 모든 root와 겹치지 않는다. SQLite `codexish.db` 하나에 invocations, processes, artifacts, events, tokens를 두고, 파일 출력·백업·cursor key·로컬 제어 토큰도 상태 디렉터리에 둔다. JSON 경로의 환경변수 치환은 하지 않으므로 기본값을 쓰거나 절대 경로를 설정한다.

결정: 같은 `(session, invocation_id)`와 같은 effect digest는 저장 결과 또는 진행 중 task에 합류하고, 다른 인자는 IDEMPOTENCY_CONFLICT다. wait_ms는 digest에서 제외한다. accepted ledger를 기록한 뒤 효과를 시작한다. 최종 DB commit 실패는 메모리에 unknown/persist_failed로 남겨 inspect가 endless running을 반환하지 않게 한다. 진단 event 기록 실패는 이미 결정된 업무 결과를 덮지 않는다.

결정: root 등 충돌 자원의 FIFO 체인은 수락 lock 안에서 append한다. 동일 경로의 root alias는 같은 자원을 사용한다. 취소된 중간 operation의 결과는 즉시 반환할 수 있지만, 뒤 작업의 barrier에는 기존 선행 task까지 유지한다. 읽기는 변경 큐를 우회하고 독립 자원은 진행한다.

결정: 재시작 시 시작 전 queued는 cancelled/no effects, 시작된 미완료 기록은 unknown으로 복구한다. unknown을 자동 재실행하지 않는다. 큐에서 아직 시작되지 않은 취소도 no effects로 보고한다. 존재하지 않는 cancellation target은 NOT_FOUND다.

## 5. 도구 계약과 현재 지원

결정: docs/tool-contracts.md의 해당 기능 계약을 기본으로 사용하고, 아래 현재 슬라이스의 구체적 입력·지원 변형은 도구 스키마와 함께 명시한다. 미지원 기능을 등록된 도구 이름만으로 완료 처리하지 않는다.

| 그룹 | 구현된 도구 | 현재 입력·결과 |
| --- | --- | --- |
| host/workspace | host.capabilities, workspace.info | roots/grants/session/실행 경계/미지원 이유/마지막 checkpoint |
| fs 읽기 | fs.list, fs.read, fs.search, fs.stat | root_id + 상대 path, 행·검색 페이지, 필요 시 artifact, 실제 byte hash |
| fs 변경 | fs.write, fs.apply_patch | invocation_id, expected_sha256; create 또는 기존 파일 replacement; unified diff는 파일별 expected hash와 부분 결과 |
| 실행 | shell.run, process.start | 명시적 pwsh/cmd 또는 executable+args, root 내 cwd, wait_ms, operation/process handle |
| 프로세스 | process.poll, process.write, process.stop | session 소유 핸들, stdin UTF-8, stop·출력 drain 상태 |
| Git 읽기 | git.status, git.diff, git.log | 설정한 절대 Git 경로와 고정 조회 인자, diff staged 옵션, log count |
| artifact/operation | artifact.read, artifact.search, operation.inspect, operation.cancel | session/generation/범위 바인딩 cursor, 확정 결과·불확실 효과 조회·취소 |
| 재개 | session.checkpoint | goal, completion_condition, remaining_steps, handles, invocation_id |

결정: 파일은 root-relative 경로를 사용하고 Windows의 열린 핸들 최종 경로를 검사한다. reparse 경로는 따라가지 않는다. 교체는 한 exclusive handle에서 expected hash 확인·백업·in-place 쓰기를 수행하며 기존 encoding/BOM/동일 줄바꿈을 보존한다. 백업은 상태 디렉터리 backups의 `.bak`이며 응답에서 식별자를 제공한다. `fs.write(mode=create)`는 비존재 조건으로 생성한다.

결정: 현재 fs.apply_patch는 기존 텍스트 파일 수정용 unified hunk와 no-final-newline 표기를 지원한다. 파일별 적용 결과·충돌·PATCH_PARTIAL을 반환한다. patch 자체의 파일 생성·삭제·rename은 구현 잔여이며 생성은 fs.write의 create를 사용한다.

결정: stdout/stderr는 각자의 원시 byte 순서를 로컬 artifact에 보존한다. 모델은 redacted UTF-8 snapshot을 페이지로 읽으며 utf8_base64는 그 snapshot의 정확한 byte 범위다. cursor는 서명되고 session/artifact/view/범위에 묶이며 동일 cursor 재시도는 동일 페이지다. 진행 중 출력에 새 내용이 필요하면 cursor 없이 새 snapshot을 읽는다.

결정: shell.run은 종료 결과를 수집하고 wait_ms 초과 시 operation.inspect용 핸들을 반환한다. process.start는 시작 결과와 process handle을 반환한다. session lifetime은 명시적인 session 종료 때 정리하고 persistent는 그 종료 후 유지한다. Windows Job을 사용자 명령 시작 전에 배정하고 종료·stdout/stderr drain을 구분한다.

결정: Git 조회 helper는 read grant로 실행하며 임의 shell grant를 요구하지 않는다. hook/fsmonitor/pager/textconv/external diff 및 설정된 clean/process filter를 비활성화하고 사용자 Git 환경 변수를 분리한다. 변경·commit·push는 shell.run의 명시적인 사용자 작업이다.

## 6. grant·제어·하네스

결정: root grant는 id/path/read/write/shell이며 생략한 권한은 true다. 행동별 승인 UI를 추가하지 않는다. 변경은 현재 grant를 집행하고 실제 실행 범위는 모든 결과에 unconfined_user로 표기한다.

결정: `/control`은 별도 control_port에서만, loopback Host와 X-Codexish-Control의 로컬 token을 확인한다. MCP 포트에서는 올바른 token과 재작성된 loopback Host가 있어도 거부한다. 이를 통해 터널의 Host rewrite와 로컬 요청을 분리한다. 한 프로세스·한 DB 구조는 유지한다.

결정: 제어 action은 pause/resume/kill-children/revoke-tokens이며 session lifetime을 종료하는 end-session도 제공한다. pause는 새 변경에 PAUSED/accepted=false/isError=false를 반환하고 기존 실행을 자동으로 죽이지 않는다. stop/cancel은 pause 중에도 사용할 수 있다.

결정: 서버 instructions·도구 description·Project 지시문은 읽기→수정→검증→실패 수정, 장기 handle 조회, unknown 재조회, 실제 결과 보고를 안내한다. session.checkpoint는 append-only event에 저장하고 host.capabilities가 같은 logical session의 마지막 요약을 반환한다. 출력 redaction은 환경변수 이름 패턴과 정규식의 best-effort 처리다.

## 7. 검증과 다음 슬라이스

결정: CI는 실제 임시 root·DB·자식 프로세스·HTTP OAuth/MCP 요청으로 검증한다. 실행 근거는 [진행 상태](../IMPLEMENTATION_STATUS.md)와 [계획](v1-plan.md)에 남긴다. computer.observe/query_ui/act는 다음 슬라이스, browser.* 외부 MCP 프록시는 슬라이스 3, tray/setup은 슬라이스 4다.

프로토콜 구현 참고: [MCP authorization 2025-11-25](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization), [OpenAI Developer mode](https://developers.openai.com/api/docs/guides/developer-mode). 해당 문서는 프로토콜·연결 설정 근거이며 이 서버의 실계정 측정 결과가 아니다.

## 비보장 목록

실행은 OS sandbox가 아니며 raw shell은 로그인 사용자 권한 범위에 접근한다. 다중 파일 적용·in-place 쓰기는 crash-atomic transaction이 아니고 외부 효과의 exactly-once도 보장하지 않는다. persistent는 session 종료를 넘는 수명이지 서버 종료·crash·재부팅 생존이 아니다. 현재 프로세스 입력은 pipe이며 PTY/ConPTY가 없고, artifact 도구는 일반 binary download API가 아니다. redaction은 모든 비밀 탐지를 보장하지 않으며 원시 출력은 로컬 상태 디렉터리에 남는다. fs.apply_patch의 생성·삭제·rename, 모든 filesystem/race 조합, 장시간 부하·적대적 OAuth 감사는 완료되지 않았다. Chat의 지속 시간·사용량 계산·대상 모델의 연속 호출·이미지 수신·실제 로그인·GUI 성공·터널 가용성/과금은 미실측이며 서버 CI로 대체하지 않는다.
