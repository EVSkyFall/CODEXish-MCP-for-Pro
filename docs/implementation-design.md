# 구현 설계 v0.1 — 검토 요청안

기준일: 2026-09-15. 기준 commit: `43d5f8793e4397894009f7860609a67d92084d48`.
상태: **PROPOSED / 사용자 설계 검토 대기**. 이 문서의 선택은 승인 전 제안이다. 실행 코드, 독립 리뷰 승인, Windows 시험 결과를 뜻하지 않는다.

상위 계약은 [README](../README.md), [요구사항](requirements.md), [아키텍처](architecture.md), [도구 계약](tool-contracts.md), [보안](security.md), [제품 가정](product-assumptions.md)이다. 기존 파일을 대체하지 않고 구현 결정을 보충한다. 변경이 필요한 의미는 [검토표](design-review.md)에 표시한다.

## 1. 유지할 목표와 제외할 것

**6 Pro Chat을 사용자 입력 횟수 기반으로 활용한다는 사용자 제공 가정을 유지한다.** 목표는 한 입력 안에서 관찰 → 수정 → 실행 → 실패 확인 → 재수정 → 검증을 최대한 완료하는 것이다. 실제 사용량 계산이나 무제한 턴 지속을 검증했다는 뜻은 아니다.

Pro가 판단하고 MCP는 실행 도구를 제공한다. 별도 LLM/API 키, Codex CLI를 모델 대리 실행기로 호출하는 구조, Pro 자체의 병렬 추론/Best-of-N 구현은 기본 경로에 넣지 않는다. MCP sampling, 자동 사용자 메시지 생성, Chat 자동 재호출에도 의존하지 않는다.

작업 전체의 임의 실행 시간·도구 호출 횟수 cap, 유효 요청의 `BUSY`/`ALREADY_RUNNING` 거절, 이미 허가된 작업의 반복 승인은 금지한다. 프로토콜 프레임 크기·응답 페이지·인증 만료는 작업 한도와 구분한다. 실제 제약은 출처·설정·영향을 노출한다.

## 2. 결정 제안

| ID | 제안 | 이유와 경계 |
| --- | --- | --- |
| D-01 | Gateway: TypeScript strict + Node.js 24 LTS + 공식 MCP SDK | MCP/HTTP와 Windows native 코드를 분리한다. 정확한 SDK·patch 버전은 T00에서 설치·검증 후 lockfile에 고정한다. |
| D-02 | Windows: C#/.NET 10, OS 독립 Core와 Windows 전용 어댑터 분리 | Win32/UIA/프로세스 감독을 Windows 계층에 둔다. WPF는 로컬 제어 UI용이다. |
| D-03 | Chat → Gateway는 HTTPS Streamable HTTP, Agent → Gateway는 outbound WSS | PC의 공개 수신 포트는 불필요하다. 구형 SSE 지원은 실계정 시험에서 필요할 때만 추가한다. |
| D-04 | Gateway와 Agent 각각 SQLite, Agent journal/artifact가 실행 근거의 원본 | 첫 버전은 단일 Gateway 인스턴스다. Redis·다중 서버 합의·클라우드 artifact 저장은 기본 의존성이 아니다. |
| D-05 | 공개 MCP 인증은 기존 OAuth 제공자 + PKCE, 기기 연결은 별도 mTLS | 사용자 토큰과 기기 신원을 혼용하지 않는다. 자체 OAuth 서버 구현은 첫 버전에서 제외한다. |
| D-06 | 요청 수명, operation 수명, process 수명을 분리 | HTTP 종료가 실제 실행 종료가 되지 않도록 한다. |
| D-07 | Windows 파일 교체와 승인 제어면은 먼저 실험으로 검증 | 보장하지 못한 기능은 지원으로 광고하지 않는다. 해당 어댑터만 검증 대기이고 독립 Core 작업은 계속한다. |
| D-08 | 배포 시 protected broker / worker / interactive helper 역할을 분리 | 프로세스 분리 자체를 보안 경계라고 하지 않는다. OS 자격·ACL·무결성 수준의 실증이 추가로 필요하다. |

Node 24와 .NET 10의 지원 계열은 공식 페이지로 확인했다. 현재 실행 컨테이너가 이 스택을 갖췄다는 뜻은 아니다. [Node.js](https://nodejs.org/) · [.NET 지원 정책](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)

### 제안 소스 구조

```text
apps/gateway/                 MCP transport, OAuth 검증, 기기 라우팅
packages/contracts/           JSON Schema 원본과 언어 간 golden vectors
src/Agent.Core/               policy, scheduler, invocation, journal, artifact
src/Agent.Transport/          outbound WSS, 인증, 재접속
src/Agent.Windows/            파일 handle, Job Object, UIA, capture, input
src/Agent.Broker/             보호된 제어 상태와 실행 승인 경로
src/Agent.Desktop/            로그인 사용자 세션의 interactive helper
src/Agent.Control/            WPF 설정, 승인, 진행, 취소
src/Agent.Browser/            전용 browser와 semantic/CDP 어댑터
tests/                       contract, core, integration, Windows, E2E
```

위 경로는 생성할 구조이지 현재 존재하는 코드가 아니다. 초기 scaffold는 필요한 프로젝트부터 만들고 빈 어댑터를 완성 기능처럼 등록하지 않는다.

## 3. 인증·연결 계약

### 3.1 ChatGPT 진입점

MCP canonical resource는 배포 설정의 HTTPS `/mcp` URL이다. `GET/POST /mcp`와 협상한 session 종료 방식은 공식 SDK에 맡긴다. 매 요청에서 서명, issuer, audience/resource, 만료, scope, 내부 계정·기기 접근권을 검증한다. MCP session ID는 인증 수단이 아니다.

`/.well-known/oauth-protected-resource`와 401의 `WWW-Authenticate`가 올바른 인증 서버를 가리키게 한다. 첫 배포는 **사전 등록 OAuth client + Authorization Code/PKCE S256**을 우선한다. 실제 관리 화면에 표시된 redirect URI를 정확히 등록한다. 제공자가 지원하지 않으면 인증을 끄지 말고 호환 제공자를 선택한다. CIMD/DCR는 이후 확장 가능하지만 첫 버전 필수 사항은 아니다.

이는 ChatGPT의 지원 경로 안에서 선택한 배포 방식이다. 일반 API key나 Agent용 인증서를 ChatGPT 사용자 인증 대신 넣지 않는다. [OpenAI 인증](https://developers.openai.com/plugins/build/auth) · [Developer mode](https://developers.openai.com/api/docs/guides/developer-mode) · [MCP authorization](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization)

### 3.2 기기 등록과 WSS

인증된 사용자 관리 경로에서 pairing을 시작하고 Agent가 로컬 생성한 기기 키의 공개 부분/CSR을 제출한다. 로컬 UI와 계정 관리 화면에서 같은 계정·기기 fingerprint를 확인한 뒤 일회용 enrollment를 소비한다. 유효기간과 추측 방어는 배포 보안 설정이며 작업 시간 cap이 아니다. 개인키는 Windows 보호 저장소에 두며 명령 인자·로그·모델 결과로 보내지 않는다.

Agent는 별도 agent endpoint에 mTLS로 WSS 연결한다. Gateway는 인증서와 등록된 owner/device를 매핑한다. 모델이 보낸 owner 문자열은 무시한다. 인증서 검증이 reverse proxy에 있다면 외부에서 온 동일 이름 header를 제거하고, 신뢰된 proxy 경로만 내부 Gateway에 접근시킨다.

기기별 연결 epoch를 증가시키며 이전 연결의 새 dispatch를 fencing한다. 재접속은 동일 invocation으로 상태를 조정한다. 다른 PC로 자동 failover하지 않는다. 기기 철회는 새 인증·dispatch를 막고, Agent가 철회를 수신했는지 별도로 보고한다. 오프라인 기기에서의 즉시 철회를 주장하지 않는다. 실행 중 프로세스 취소와 이미 발생한 효과는 별도 상태다.

### 3.3 공개 endpoint의 준비 조건

P0의 disposable fixture도 인증된 endpoint로 시험한다. 개발용 무인증 transport fixture가 필요하면 자동 테스트 내부/loopback에만 묶고 실제 PC 어댑터·비밀·개인 파일을 연결하지 않는다. HTTPS 연결만 된 것을 인증 완료로 판정하지 않는다. Secure MCP Tunnel은 필수 의존성이 아니다.

## 4. 계약 원본과 도구 등록

JSON Schema를 입력·출력·내부 wire의 단일 계약 원본으로 두고 TypeScript/C#에서 같은 golden vectors를 검사한다. MCP 버전과 자체 `schema_version`/`wire_version`은 분리한다. 협상한 실제 MCP 버전을 기록하며 지원하지 않는 필드를 조용히 무시하지 않는다.

registry 항목은 tool name, 입력/출력 schema, 읽기/변경 효과, 필요한 grant, resource keys, 어댑터, 지원 환경을 연결한다. Gateway와 Agent 모두 검사한다. `readOnlyHint`는 권한 검사를 대신하지 않는다. 구현하지 않은 기능은 목록에서 제외하고 `host.capabilities`에 미지원 이유를 제공한다.

여러 기기에서 기능이 다르면 도구 목록은 인증된 사용자에게 실제 가능한 기능의 집합이고, 특정 호출은 **선택 기기의 capability와 session 권한 교집합**으로 다시 제한한다. 도구 목록을 캐시한 호스트에서도 철회된 권한으로 실행되지 않아야 한다.

외부 도구 이름은 기존 `host.*`, `fs.*`, `process.*`, `computer.*`, `browser.*` 등을 유지한다. 범용 `execute` 도구 하나로 읽기·쓰기·권한 변경을 숨기지 않는다. 모델이 grant나 승인 결정을 쓰는 도구는 제공하지 않는다.

### 공통 결과의 추가 구분

기존 envelope를 유지하며 `data` 안에 아래 정보를 필요한 도구에서 추가한다. 형식 예시이며 실제 실행 결과가 아니다.

```json
{
  "schema_version": "0.1",
  "invocation_id": "inv_example",
  "status": "running",
  "data": {
    "operation_id": "op_example",
    "delivery_state": "agent_acknowledged",
    "state_source": "agent",
    "process_id": "proc_example",
    "process_state": "running",
    "execution_boundary": "unconfined_user",
    "business_outcome": "not_verified"
  },
  "error": null
}
```

정상 대기·큐·승인 대기는 `isError=false`다. 실제 실패는 기존 오류 코드와 `side_effects`를 쓴다. `unknown`은 실패와 구분하고 자동 재실행 금지 경로를 제공한다. image content와 `structuredContent`를 실제 모델이 받는지는 P0에서 확인한다. [MCP transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)

## 5. dispatch, idempotency, crash recovery

### 5.1 식별자와 인자

중복 방지 key는 `(owner, device, session, invocation_id)`다. MCP JSON-RPC request ID나 WSS message ID는 이 key를 대신하지 않는다. 변경 호출은 모델이 같은 논리 요청을 재전송할 때 같은 invocation ID를 재사용한다.

Gateway와 Agent는 검증된 인자를 동일한 canonical JSON 규칙으로 직렬화해 SHA-256 digest를 만든다. tool, schema version, device/workspace, 실제 효과 인자와 expected state를 포함한다. OAuth token, transport request ID, 응답 대기 시간은 포함하지 않는다. grant revision은 승인·실행 시 별도로 기록/재검사하며 이전 revision에 영구 고정하지 않는다.

중복 JSON key, 비정상 숫자는 거부하고 64-bit sequence/offset은 필요한 곳에서 decimal string으로 전달한다. 문자열 내용·경로·shell command를 자의적으로 trim/lowercase하지 않는다. canonicalization은 T00 golden vector로 확정한다. secret 원문 대신 reference를 인자로 사용하고 digest를 비밀 원문의 공개 지문으로 노출하지 않는다.

### 5.2 실행 순서

1. Gateway는 인증/권한을 확인하고 논리 operation mapping을 기록한다.
2. Agent는 schema, 계정·기기·session, 현재 grant, idempotency를 검사한다.
3. 새 요청이면 invocation과 accepted journal event를 **같은 DB transaction으로 commit한 뒤** ACK한다.
4. 권한이 부족하면 `awaiting_approval`; 충분하면 resource queue로 이동한다.
5. 큐 실행 직전에 grant·대상 version·실행 환경을 다시 검사하고 실행 시작을 영속화한다.
6. 어댑터가 효과와 관찰을 기록한다. 최종 결과·journal 참조를 commit한 뒤 응답한다.

같은 ID/같은 인자는 기존 operation에 합류한다. 같은 ID/다른 인자는 `IDEMPOTENCY_CONFLICT`다. 승인 대기 중 인자가 달라져도 예전 승인을 재사용하지 않는다.

### 5.3 단절과 unknown

| 상황 | 처리 |
| --- | --- |
| Agent에 전송하지 못함 | `AGENT_OFFLINE`, `delivery_state=not_sent`; 효과 없음이 확인되는 범위만 표시 |
| 전송 후 ACK 유실 | `sent_unacknowledged`; invocation 조회/같은 ID 재전송으로 조정. 새 ID로 반복하지 않음 |
| accepted/running의 응답 유실 | Agent의 영속 operation 결과를 조회 |
| 효과 발생 후 완료 commit 전 crash | Agent가 `unknown`으로 복구하고 파일·HEAD·프로세스·UI 상태를 대조 |
| Gateway 재시작 | 인증 재확인 후 Agent 상태로 routing projection을 재구성 |

Gateway의 전달 불확실성을 Agent의 확정 상태로 덮어쓰지 않는다. `state_source`와 마지막 관측 시각을 남긴다. `unknown`에서 결과를 확인해 상태를 조정할 때에도 이전 unknown event를 삭제하지 않는다. 임의 shell, GUI 클릭, 외부 push의 exactly-once를 보장하지 않는다.

WSS envelope는 `wire_version`, message ID, connection epoch, operation/invocation 참조, message type, payload를 포함한다. `request/accepted/response/event/cancel/heartbeat`를 분리한다. event 중복은 영속 sequence로 제거한다. 큼직한 payload는 협상한 프레임 단위로 분할하며 프레임 한도를 전체 출력 손실로 바꾸지 않는다.

## 6. 저장·큐·출력

Agent DB의 최소 논리 테이블은 `sessions`, `grants`, `invocations`, `journal_events`, `approvals`, `processes`, `artifacts`, `artifact_chunks`다. Gateway DB는 사용자·기기 바인딩, 철회, 연결 epoch, operation routing만 소유한다. SQLite WAL/transaction을 사용하되 설정·디스크 오류·crash 시험 없이 내구성을 보장했다고 하지 않는다.

journal은 append-only event와 재구성 가능한 projection을 분리한다. DB transaction과 외부 파일/GUI 효과는 하나의 원자적 transaction이 아니다. artifact는 flush된 chunk와 DB index를 연결하고, 시작 시 orphan/missing chunk를 대조한다. 저장 실패를 성공으로 숨기지 않는다.

resource key는 workspace/file, Git common directory, browser context/tab, desktop input stream 등으로 나눈다. 필요한 key를 정렬해 함께 예약하고 교착을 방지한다. 승인 대기는 resource를 점유하지 않는다. 같은 자원의 준비된 작업은 큐에서 자동 시작하며 독립 작업·읽기는 진행한다. DB lock 경합도 내부 재시도/큐로 처리하며 제품의 busy 거절로 노출하지 않는다. 실제 디스크 부족은 별도의 storage 오류로 보고한다.

stdout와 stderr는 각자의 byte 순서를 보존한다. 통합 event 순서는 관측한 순서이지 두 OS stream의 절대적인 발생 순서 보장은 아니다. cursor는 artifact generation, 읽기 범위, offset, 소유 session에 바인딩한다. 같은 cursor의 재시도는 같은 확정 구간을 반환하고, 아직 출력이 없다는 것과 EOF를 구분한다. 최종 exit와 출력 drain 완료도 따로 표시한다.

모델에 보낼 redacted artifact와 보호된 로컬 원본을 분리한다. redaction은 가능한 한 Agent에서 Gateway 전송 전에 수행한다. 임의 화면·로그의 모든 비밀을 탐지할 수 있다고 주장하지 않는다. 원본은 명시적 보존 정책 없이는 별도로 축적하지 않는다.

## 7. Windows 어댑터 설계

### 7.1 실행·승인 제어면

**같은 OS 사용자로 실행하는 임의 코드를 경로 검사나 같은 사용자 WPF 승인 창만으로 격리할 수 있다고 가정하지 않는다.** raw shell이 Agent 설정/DB/IPC를 수정하거나 승인 UI를 조작할 수 있는지도 위협 모델에 포함한다.

권장 배포는 보호된 broker 상태/기기 키, 별도 실행 worker, 로그인 세션의 제한된 desktop helper, 사용자가 직접 여는 보호된 승인 UI를 분리하는 것이다. broker는 모델 입력 명령을 자신의 높은 권한으로 실행하지 않는다. worker에 불필요한 credential/handle을 전달하지 않는다. 실제 서비스 identity, ACL, UI 무결성 경계는 **T03 실험과 독립/사용자 검토 후** 확정한다. 서비스만 설치하면 대화형 desktop이 생긴다고 가정하지 않는다. [Interactive Services](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services)

격리가 없는 실행은 `execution_boundary=unconfined_user`로 표시한다. `workspace_write`라는 이름으로 제한된 shell이라고 광고하지 않으며 실제 OS 사용자 권한 범위의 명시적 grant가 필요하다. grant 자체가 OS sandbox를 만들어 주지는 않는다. 이 경로의 제어면 자기 변경 방어를 입증하지 못하면 해당 보안 수용 기준은 통과가 아니다. 사용자 동의 없이 이를 안전한 기본값으로 선택하지 않는다.

보호된 UI/IPC 검증이 안 된 상태에서 raw host 실행을 출시 기능으로 열지 않는다. 이는 구현 단계의 정확성 gate이지 허가된 작업마다 반복 승인을 추가하자는 뜻이 아니다. 이미 검증된 파일/관찰 기능과 독립 Core 개발은 계속 진행한다.

### 7.2 파일

기본 입력은 root ID와 상대 경로다. 처음에는 지원을 검증한 local filesystem에서 시작하고 UNC/WSL/reparse 등은 실제 capability로 구분한다. 문자열 prefix만 확인하지 않고 handle 기준 대상·부모·volume/file identity를 확인한다. denial은 다른 파일에 대한 조용한 fallback이 아니다.

`create`는 원자적 비존재 조건을, `replace/apply_patch`는 expected byte hash·encoding/BOM/줄바꿈 보존을 요구한다. multi-file patch는 전체 preflight 후 파일별 적용 결과를 기록하며 전체 원자성을 약속하지 않는다.

**hash 재검사 + ReplaceFile만으로 외부 editor의 마지막 순간 변경을 보존한다고 주장하지 않는다.** Windows handle/share mode와 parent 교체 경쟁까지 시험해야 한다. T04에서 동시 수정·rename·junction 교체 시험을 통과하는 conditional replacement 경로를 선택한다. 지원하지 못하는 filesystem/대상에서는 기능을 비활성으로 알리고 read-only를 유지한다. 사용자 변경을 덮어쓸 수 있는 구현으로 FS-02를 완료 처리하지 않는다. [ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)

### 7.3 process / shell

`process.start` operation 성공은 프로세스 시작 확인이다. **테스트 성공은 process exit code와 출력으로 별도 판단한다.** `shell.run`은 같은 내부 supervisor를 쓰되 프로세스 종료까지 operation이 running일 수 있다. `wait_ms` 경과 시 핸들을 반환하고 실행을 죽이지 않는다.

stable process ID에 PID와 시작 identity를 연결한다. structured executable/args를 우선하고 shell 문자열은 해석기를 명시한다. Job Object로 자신의 child만 감독한다. ConPTY를 통한 입력과 일반 stdout/stderr pipe는 capability를 나눠 시험한다.

`session` 수명은 명시적인 제품 session 종료 시 중단 대상이다. `persistent`는 Chat/HTTP 연결 종료와 session 종료 후에도 유지할 수 있지만 재부팅/감독 프로세스 crash 생존을 보장하지 않는다. 최초 supervisor 구현은 자식 정리를 우선하고 restart 후 확인 불가 exit code를 만들어 내지 않는다. OS 상태를 대조해 exited/lost/unknown을 기록한다. 완료된 start operation의 cancel과 실행 중 process.stop을 혼동하지 않는다.

### 7.4 Git / worktree

staged/unstaged/untracked를 구분하고 expected HEAD/index 상태와 실제 결과를 기록한다. Git common directory 자원 큐로 metadata 변경을 정렬한다. worktree 생성은 현재 변경을 자동 복제하지 않고 base ref, branch, destination, 변경 포함 정책을 명시한다. 완료를 자동 push/merge/delete로 해석하지 않는다.

조회 구현은 외부 diff/textconv/pager뿐 아니라 fsmonitor/hook/config 경로의 사용자 코드 실행도 검사한다. **READ_ONLY에서 내부 Git helper를 시작할 수 있는지**는 기존 '새 프로세스 금지' 문구와의 의미 정합성을 R-07에서 검토한다. 승인 전에는 임의 subprocess를 조회 기능으로 몰래 허용하지 않는다. 미준비 Git 관찰은 미지원으로 표시하고 파일 조회를 계속 제공한다.

### 7.5 Browser

첫 경로는 전용 profile의 managed Chromium과 .NET Playwright/CDP 어댑터다. 개인 profile attach는 opt-in 확장으로 두며 CDP를 외부에 노출하지 않는다. browser start/탐색/새 탭은 변경 기능이다.

관찰은 검토된 DOM/accessibility snapshot과 screenshot, console/network metadata를 제공한다. 임의 JavaScript eval은 별도 쓰기 capability이고 MVP 필수 경로가 아니다. tab/frame/navigation version에 element 참조를 묶고 실행 전에 다시 검증한다.

전용 profile은 network sandbox가 아니다. URL allowlist만으로 redirect·subresource·download·websocket·localhost 접근을 모두 통제했다고 주장하지 않는다. 외부 목적지를 강제 제한한다고 광고하려면 실제 egress 통제와 우회 시험이 필요하다. upload는 데이터 원본과 목적지 둘 다 검사한다.

### 7.6 Computer use

capture/UIA/input은 로그인된 대화형 사용자 세션의 helper가 처리한다. 기본은 앱 API/UIA semantic target이고 좌표는 fallback이다. observation에는 immutable ID, 수집 시작/끝 시각, window/process identity, desktop/layout revision, image-to-desktop transform을 넣는다.

canonical 좌표는 virtual desktop physical pixel이다. 음수 좌표·mixed DPI·crop/resize를 metadata로 변환하며 DPI를 이중 곱하지 않는다. screenshot과 UIA는 동시에 원자적으로 수집되지 않으므로 시간차/부분 실패를 표시한다.

행동 큐에서 꺼낼 때 target, 창 identity, focus, 사용자 입력에 의한 변경을 재확인한다. 화면 전체 hash 변화만으로 애니메이션 화면을 계속 거부하지 않는다. action 후 observation을 기본 제공하고 전송 성공과 업무 효과 확인을 분리한다. stale/부분 입력/사후 캡처 실패 시 클릭을 맹목적으로 반복하지 않는다.

protected control UI를 일반 computer action의 타깃에서 제외한다. 이것만으로 raw shell 우회를 막았다고 하지 않으며 7.1의 OS 경계 시험이 필요하다. 잠금/UAC secure desktop/더 높은 무결성 창은 가용성을 정확히 보고한다. 자동 권한 상승이나 clipboard 몰래 사용은 하지 않는다. [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

## 8. 첫 구현의 범위

P0 probe는 synthetic text/image, disposable fixture write, start/poll을 이용해 정확한 6 Pro Chat에서 도구 사용을 확인한다. Gateway fixture 통과를 Windows PC 실행 성공으로 바꾸지 않는다. 실제 사용자 계정/Windows 접근이 없으면 그 시험만 `BLOCKED_EXTERNAL`로 기록한다.

P1은 인증·기기 routing·영속 Core·읽기·관찰이다. P2는 파일 편집, process/shell, Git/worktree 변경, managed browser, Windows action과 코드/웹/GUI E2E까지다. **P1만 구현하고 MVP 완료라고 하지 않는다.** LSP·임의 eval·batch·WSL/SSH·고급 packaging은 핵심 작업 흐름 뒤에 둔다.

구현 순서, 요구사항 추적, 실제 증거 형식은 [구현 계획](implementation-plan.md)에 정의한다. 승인 상태는 [검토표](design-review.md), 실제 진행은 [IMPLEMENTATION_STATUS](../IMPLEMENTATION_STATUS.md)가 관리한다.
