# 요구사항과 수용 기준

## 최상위 설계 원칙

**“ChatGPT 공홈 Chat의 6 Pro는 토큰제 할당량이 아니라 사용자 입력 횟수제 할당량을 사용한다”는 사용자 제공 제품 가정이 최우선이다. 한 번의 사용자 입력 안에서 가능한 한 많은 도구 호출·관찰·수정·실행·테스트 반복을 수행하여 Codex에 가까운 장시간 작업 루프를 구성한다.**

성공 지표는 추가 입력 없이 완료한 실제 작업과 검증된 결과다. 무의미한 도구 호출 수, 추정 토큰 비용, 비공개 한도의 추측을 최적화하지 않는다. 이 가정은 공식 할당량 정책의 확인 결과가 아니며 [제품 가정 문서](product-assumptions.md)에 검증 상태를 관리한다.

**현재 구현·실계정 시험 상태는 전부 미구현/미시험이다.** 아래 요구사항은 앞으로 충족해야 할 계약이다. 문서의 MUST는 이 프로젝트의 요구사항이며 외부 제품이 이미 보장하는 기능이라는 뜻이 아니다.

## 1. 범위와 단계

| 단계 | 결과 | 포함 항목 | 완료 근거 |
| --- | --- | --- | --- |
| P0: 호환성 확인 | 대상 계정·모델의 실제 연결 가능성 확인 | disposable MCP 환경, 정확한 6 Pro 선택, 읽기·이미지·쓰기 승인·연속 호출 | [실계정 확인표](product-assumptions.md)의 증거. 구현이 필요하며 현재 미수행 |
| P1: 관찰 기반 | Windows 기기와 workspace의 읽기·상태를 전달 | Gateway/Agent/auth, capabilities, fs 읽기, Git 조회, process 조회, desktop/UIA, 준비된 browser 관찰, artifact, journal | READ_ONLY의 의미와 경계를 실제 시험 |
| P2: 최소 작업 제품 | 한 입력에서 편집·실행·검증 가능한 FULL_CONTROL | patch/write, shell/process lifecycle, Git 변경, worktree 생성, GUI/browser action, 승인·취소·복구 | 코드·웹·GUI 흐름과 실패 복구를 실제 수행 |
| P3: 개발 도구 확장 | 더 정확한 탐색과 앱별 처리 | LSP, browser styles/performance, 파일 lifecycle·worktree 제거 고도화, watcher, 조건부 batch | 각 어댑터의 실행 효과·권한·버전 검증 |
| 이후 선택 확장 | 다른 실행 환경과 재사용 작업 | WSL/SSH, 앱 API adapter, record/replay, 워크플로·스케줄 | 별도 설계. Chat 자동 지속이나 별도 모델 호출을 전제하지 않음 |

MVP 완료는 P1만의 조회 성공이 아니라 **P2까지의 작업 흐름 성공**이다. 요금제별 READ_ONLY/FULL_CONTROL 하드코딩, Codex 내부 API 복제, 공개되지 않은 사용량 제어, API 모델로의 자동 전환은 범위에 포함하지 않는다. 선택 기능 때문에 기본 작업 흐름 완료를 지연시키지 않는다.

## 2. 요구사항 추적표

| ID | 요구사항 | 시점 | 설계 근거 |
| --- | --- | --- | --- |
| PR-01 | 최상위 제품 가정을 README·아키텍처·본 문서 첫 원칙에 유지하고 미확인 수치를 확정하지 않는다. | 문서/P0 | [가정](product-assumptions.md) |
| PR-02 | 한 사용자 입력에서 관찰·수정·테스트 실패의 재계획을 이어갈 수 있는 도구와 후속 핸들을 제공한다. | P2 | [아키텍처](architecture.md) |
| PR-03 | Chat 턴 수명, CODEXish session, operation/process 수명을 구분한다. 서버가 Chat 추론을 연장한다고 주장하지 않는다. | P0–P2 | [아키텍처](architecture.md) |
| ARC-01 | Remote MCP Gateway와 outbound Windows Local Agent를 분리하고 PC 공개 포트를 필수로 요구하지 않는다. | P1 | [연결 구조](architecture.md) |
| CAP-01 | READ_ONLY/FULL_CONTROL, workspace mode, 사용자 grant, 실제 실행 경계의 교집합을 집행한다. | P1–P2 | [보안](security.md) |
| CAP-02 | 실제 가용 기능과 미지원 이유를 노출한다. 임의 shell/JS/LSP 실행을 읽기 전용으로 위장하지 않는다. | P1–P3 | [도구 계약](tool-contracts.md) |
| FS-01 | 범위·줄 번호·encoding·hash를 포함해 파일·목록·검색을 읽는다. 큰 결과는 재조회 가능하다. | P1 | [파일 도구](tool-contracts.md#3-filesystem) |
| FS-02 | 기대 파일 상태를 검증해 patch/write하고 encoding·줄바꿈·사용자 변경을 보존한다. 부분 적용도 명시한다. | P2 | [파일 도구](tool-contracts.md#3-filesystem) |
| EX-01 | 짧은 명령의 결과와 장기 프로세스 핸들·증분 출력·stdin·종료 상태를 제공한다. wait와 실행 제한을 구분한다. | P2 | [프로세스 도구](tool-contracts.md#4-shell과-process) |
| GIT-01 | status/diff/log/ref와 staged/unstaged/untracked 변경을 빠짐없이 검토할 수 있다. | P1–P2 | [Git](tool-contracts.md#5-git과-worktree) |
| WT-01 | base ref·branch·경로가 분명한 worktree를 만들고 사용자 변경·미추적 파일을 보존한다. | P2–P3 | [동시 작업](architecture.md) |
| CU-01 | screenshot·창·UIA·포커스·좌표 변환이 연결된 immutable 관찰을 제공한다. | P1 | [Computer Use](tool-contracts.md#6-computer-use) |
| CU-02 | semantic 타깃을 우선하고 stale 검사를 수행하며 action 후 실제 결과를 관찰한다. | P2 | [Computer Use](tool-contracts.md#6-computer-use) |
| CU-03 | 다중 모니터·음수 좌표·DPI·crop/resize·사용자 입력·관리자/잠금 상태를 구분한다. | P1–P2 | [Computer Use](tool-contracts.md#6-computer-use) |
| BR-01 | 전용 browser profile과 opt-in attach, DOM/접근성·console/network·action을 분리 제공한다. | P1–P2 | [브라우저](tool-contracts.md#7-browser--cdp) |
| LSP-01 | 문서 버전·위치 encoding이 명시된 진단·정의·참조·심볼·hover를 제공한다. | P3 | [LSP](tool-contracts.md#8-lsp) |
| ART-01 | 큰 출력·이미지의 허용된 데이터를 보존하고 cursor/read/search로 접근하게 한다. 누락·redaction·만료를 표시한다. | P1 | [Artifact](tool-contracts.md#9-artifact와-큰-결과) |
| APR-01 | 기존 허가를 재사용하고 새 권한만 신뢰할 수 있는 UI에서 확인한다. 승인은 정확한 인자·대상에 바인딩한다. | P1–P2 | [승인](security.md) |
| SEC-01 | 사용자·기기·세션 인증과 접근 철회를 실행 경로에서 집행한다. | P1 | [보안](security.md) |
| SEC-02 | 파일·shell·Git·LSP·CDP·GUI의 실제 권한 경계를 구분하고 우회를 시험한다. | 해당 기능 도입 시 | [실행 경계](security.md#실제-실행-경계) |
| SEC-03 | 비밀정보와 신뢰할 수 없는 UI/파일/로그가 권한을 확대하지 못하게 한다. | P1–P3 | [보안](security.md) |
| RUN-01 | 같은 invocation 재전송은 합류·결과 재조회하고 효과가 불확실하면 자동 재실행하지 않는다. | P2 | [복구](architecture.md) |
| RUN-02 | 바쁘다는 이유로 유효 요청을 거절하지 않고 자원 큐·합류를 사용한다. 실행 직전 권한·대상을 다시 검증한다. | P2 | [동시 작업](architecture.md) |
| JRN-01 | append-only journal·checkpoint·artifact로 오류와 변경·프로세스·검증 결과를 재구성한다. | P1–P2 | [영속 상태](architecture.md) |
| ERR-01 | 구조화 오류, 부분 효과, unknown 상태, 복구 행동을 반환하고 원래 실패를 숨기지 않는다. | P1–P2 | [오류 모델](tool-contracts.md#11-오류-모델) |

## 3. 대표 사용자 흐름의 수용 기준

모든 시나리오는 시험용 파일·앱·브라우저·기기를 사용한다. 입력 횟수와 반복 횟수의 기록은 측정값이며 제품에 도입할 고정 한도가 아니다. 서버 단위 시험과 실제 Chat 통합 시험을 별도로 보고한다.

### AC-01. 한 입력으로 코드 수정과 재검증

사용자가 허가된 저장소에서 작은 결함 수정을 지시한다. 모델이 파일과 Git 상태를 읽고, 기대 hash로 수정하고, 테스트 실패를 관찰한 뒤 재수정·재검증한다. 최종 보고에는 diff·테스트 결과·artifact 근거가 포함된다. 중간에 이미 허가된 동작을 이유로 새 사용자 입력을 요구하지 않는다.

**기록:** 사용자 입력 수, Chat이 실제 수행한 도구 호출과 수정/검증 반복, 성공·실패, 경과 시간, 필요한 승인·질문, 종료 원인. Chat이 먼저 종료하면 서버 기능 성공과 단일 입력 흐름 미완료를 구분한다. PR-02를 단위 테스트만으로 충족했다고 보고하지 않는다.

### AC-02. 웹 결과를 관찰하며 수정

모델이 개발 서버를 시작하고 핸들을 받은 뒤 전용 browser에서 페이지를 연다. screenshot과 DOM·console/network로 문제를 확인하고 수정·재확인한다. reload 이후 기존 element ID가 무효이면 재관찰한다. 서버 프로세스가 종료돼야 tool call이 끝나는 구조여서는 안 된다.

### AC-03. Windows 앱의 자연스러운 조작

허용된 앱을 찾고 screenshot/UIA로 대상 버튼·입력 필드를 식별한다. semantic action을 우선하며 필요할 때 이미지 좌표를 변환해 조작한다. 결과를 다시 관찰해 실제 저장·변경을 확인한다. mixed DPI, 음수 좌표 모니터, crop/resize, 앱 이동, 포커스 변경을 포함한다. 입력 API의 성공만으로 완료 판정하지 않는다.

## 4. 실패·경계 수용 기준

| 시험 | 절차와 기대 결과 | 관련 ID |
| --- | --- | --- |
| AC-04: 읽기 전용 우회 | R에서 write·shell·브라우저 탐색·eval·LSP 시작을 직접 호출한다. 실행되지 않아야 하고 미지원/권한 이유를 표시한다. 허용된 읽기는 계속 된다. | CAP-01/02, SEC-02 |
| AC-05: 변경 경쟁 | 파일을 읽은 후 사용자가 수정하거나 경로의 reparse target을 바꾼다. 기존 사용자 변경·외부 파일을 덮어쓰지 않는다. | FS-02, SEC-02 |
| AC-06: 중복·crash | 같은 ID 재전송, 같은 ID의 다른 인자, 수락 전후 단절, 효과 적용 직후 crash를 시험한다. 중복 효과 없이 결과 재조회하거나 unknown으로 남긴다. | RUN-01, ERR-01 |
| AC-07: 긴 출력 | 큰 stdout/stderr·검색 결과를 만들고 cursor로 합친 결과를 허용된 원본과 비교한다. 누락은 명시하고 재시도 cursor가 다른 데이터로 바뀌지 않는다. | ART-01, EX-01 |
| AC-08: 큐와 합류 | 같은 자원에 유효한 작업을 동시에 요청한다. busy 거절 없이 큐 순서·자동 시작·합류를 확인한다. 읽기·독립 작업은 진행된다. 큐 실행 전에 hash/관찰을 다시 검증한다. | RUN-02 |
| AC-09: 승인 | 기존 grant 안의 작업은 계속 진행된다. 새 권한이 필요한 요청은 실행 전 정확한 대상을 표시하고, 승인 후 자동 큐잉된다. 인자 변경·철회·위조 승인에는 효과가 없어야 한다. | APR-01, SEC-01 |
| AC-10: 취소와 수명 | poll 대기 종료·네트워크 단절·사용자 취소·session 종료·Agent 재시작을 나눠 시험한다. 선택한 process 수명과 실제 child 상태를 보고하고 무관한 프로세스를 죽이지 않는다. | EX-01, PR-03 |
| AC-11: 기기·세션 격리 | 다른 사용자의 device/session/process/artifact ID로 접근한다. 데이터·작업이 노출되지 않는다. offline 기기의 작업을 다른 기기에 보내지 않는다. | SEC-01 |
| AC-12: 비밀·주입 | 시험용 가짜 비밀을 로그·DOM·URL·screenshot·artifact에 넣고 가짜 권한 지시를 표시한다. 데이터 보호 경로와 grant 불변을 확인하며 탐지 한계도 기록한다. | SEC-03 |
| AC-13: worktree | 기존 변경·미추적 파일이 있는 저장소에서 지정 ref의 worktree를 생성한다. 변경 포함 정책과 HEAD를 확인하고 제거 시 사용자 변경을 보존한다. | WT-01, GIT-01 |
| AC-14: journal 재개 | checkpoint·journal만으로 완료/남은 작업과 실제 파일·Git·process 상태를 대조한다. 실패·부분 효과·unknown 기록이 사라지지 않아야 한다. | JRN-01, RUN-01 |
| AC-15: LSP 버전 | 파일 편집 전후 진단을 비교하고 문서 버전·UTF 위치 변환을 확인한다. 오래된 진단과 최신 빌드 검증을 혼동하지 않는다. | LSP-01 |
| AC-16: 외부 제약 | 실제 전송·OS·클라이언트 제한을 유발한다. 출처·단계·효과를 보고하고 timeout을 작업 취소나 성공으로 임의 변환하지 않는다. | PR-03, ERR-01 |
| AC-17: 실행 경계 | 시험 프로세스·Git/LSP 경로에서 허가 밖 파일·네트워크에 접근을 시도한다. 강제 격리를 주장한 경계는 집행돼야 하고, 미격리 실행은 실제 권한으로 표시돼야 한다. | SEC-02, CAP-01 |

## 5. 비기능 요구사항

- **관찰 가능성:** 요청·상태·완료·실패·효과·인자 식별값을 연결하고 사용자에게 진행·대기·취소 경로를 제공한다.
- **지연과 문맥:** action 후 관찰, 증분 출력, 선택 조회를 지원한다. 성능 수치는 기준 환경과 측정 절차가 생긴 후 기록한다.
- **데이터 무결성:** 허용된 전체 결과의 접근 경로를 유지하고 사용자 변경·encoding·기존 Git 상태를 보존한다. 비밀정보 보호에 따른 redaction은 별도로 표시한다.
- **호환성:** 실제 협상된 MCP 버전·SDK·Agent·브라우저 버전을 기록한다. 지원하지 않는 결과 형식과 capability는 조용히 무시하지 않는다.
- **사용자 통제:** 허가 지속 범위·보존 정책·프로세스 수명을 확인·변경할 수 있다. 완료를 자동 push·삭제·권한 확장으로 해석하지 않는다.
- **지속 실행:** 유효 작업을 busy 거절이나 근거 없는 시간·횟수 cap으로 끊지 않는다. 실제 서비스 제한·사용자 취소·데이터 불일치는 원인과 복구 방법을 명시한다.

## 6. 완료 보고 규칙

문서 검증은 링크·JSON 예시·용어·요구사항 연결·diff 검토를 뜻한다. 구현 검증은 별도로 테스트·실행 결과를 요구한다. 실제 6 Pro Chat 시험 없이 “한 입력의 장시간 루프 검증 완료”라고 보고하지 않는다.

구현 단계의 보고서는 다음을 포함한다.

1. 구현한 요구사항 ID와 아직 미구현인 항목.
2. 실제 실행한 시험·환경·결과와 evidence/artifact 위치.
3. 실패·미시험·부분 성공·unknown 상태를 구분한 설명.
4. 최종 diff, 사용자 데이터 보존 확인, 남은 운영·제품 호환성 문제.

설계 단계에서 모든 요구사항에 체크 표시를 달거나 이 표를 통과한 테스트 결과처럼 사용하지 않는다.
