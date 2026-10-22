# 구현 계획과 검증 순서

기준일: 2026-09-15. 상태: **설계 검토용 계획 / 모든 구현 task 미착수**.
선행 문서: [구현 설계](implementation-design.md), [요구사항](requirements.md), [검토표](design-review.md).

## 1. 리뷰 절차

`Plan → 사용자 또는 독립 reviewer의 Pre-Review → Implement → 실제 검증 → Post-Review` 순서다. 현재는 Plan 산출물을 제출하는 단계다. 작성자의 자체 점검을 독립 reviewer의 승인으로 바꾸지 않는다.

검토자는 대상 commit, `APPROVE` 또는 `CHANGES_REQUESTED`, 허용한 구현 범위, 남은 gate를 기록한다. 조건부 승인은 미검증 어댑터 공개를 허용하지 않는다. 설계 승인이 나면 승인 범위의 연속 작업마다 사용자에게 같은 확인을 다시 요구하지 않는다. 범위·권한·핵심 설계가 달라질 때만 재검토한다.

T03/T04는 상세 구현 방법을 결정하기 위한 의도적인 spike다. 이 실험이 끝나지 않았다는 이유로 모든 Core 개발을 막지 않는다. 반대로 실패한 spike를 우회해 강한 보장을 이름만 남겨 구현하지 않는다.

## 2. 작업 분해

| Task | 구현할 내용 | 선행 | 실제 완료 근거 |
| --- | --- | --- | --- |
| T00 | Gateway/계약/Core 최소 scaffold, 버전 lock, schema/golden vectors, 테스트 기반 | 설계 승인 | Node/.NET restore·build·typecheck·contract test 로그. 실행 못 한 OS는 별도 표시 |
| T01 | 인증된 disposable MCP probe: text/image/write/start/poll | T00 | SDK client 시험 + 정확한 6 Pro Chat 증거. 두 결과를 분리 |
| T02 | OAuth resource server, pairing/mTLS/WSS, 계정·기기 격리와 철회 | T00 | 잘못된 audience, 만료, 다른 owner/device, 인증서 위조, 연결 epoch, 재접속 시험 |
| T03 | Windows 제어면/broker/worker/승인 UI 경계 spike와 보안 ADR | T00 | shell→policy/DB/IPC 변조 및 자기 승인 시도, helper→보호 UI 입력 시험, reviewer 판정 |
| T04 | Windows conditional file replacement와 경로 fence spike | T00 | 외부 editor 수정·rename·reparse 교체, handle/share mode, crash 시험. 지원 filesystem 명시 |
| T05 | 영속 invocation, resource queue, 승인 상태, journal, artifact/cursor | T00 | 중복/다른 인자/crash/출력 분할/큐 독립성/철회 시험. mock과 실제 DB 시험 분리 |
| T06 | P1 host/session/workspace/fs 읽기 및 검증된 관찰 도구 | T02, T05 | READ_ONLY 우회 금지, 경로/다른 session/cursor/data 보호 시험 |
| T07 | write/patch, supervisor, shell, process poll/stdin/stop | T03, T04, T05, T06 | 경쟁 편집, 테스트 실패→수정→재검증, wait/프로세스 수명/child/unknown 시험 |
| T08 | managed browser, semantic action, image/DOM/console/network | T03, T05, T07 | 개발 서버 start→브라우저 확인→수정→재관찰. navigation stale와 upload 범위 시험 |
| T09 | Windows capture/UIA/action, 좌표 변환, 사용자 개입 처리 | T03, T05, T06 | 대화형 Windows에서 mixed DPI·음수 monitor·창 이동·focus·잠금·부분 입력 시험 |
| T10 | Git 조회/변경, worktree 생성·조회, 사용자 변경 보존 | T03, T05, T07, R-07 결정 | staged/unstaged/untracked, hook/fsmonitor 우회, HEAD 충돌, worktree 보존 시험 |
| T11 | P2 통합, 재접속 재개, 코드/웹/GUI의 단일 입력 E2E | T07–T10 | AC-01–AC-14, AC-16–AC-17 실제 환경 증거와 최종 diff. 예외별 상태 명시 |
| T12 | 설치·연결 UX, 운영 안내, 배포/복구 runbook | 관련 어댑터 검증 | 깨끗한 Windows 계정에서 설치→pairing→grant→관찰/작업→취소→철회 흐름 |
| T13 | P3 LSP, 선택 batch, 고급 lifecycle·어댑터 | P2 완료 후 별도 범위 승인 | AC-15와 확장별 계약/실환경 검증 |

T01의 실계정 항목이 외부 환경 때문에 막혀도 T00/T02/T05와 안전한 mock 개발은 가능하다. T08과 T09는 선행 조건을 만족하면 독립 구현 가능하다. 시간·호출 수 예산을 task 완료 조건으로 사용하지 않는다.

## 3. 요구사항 추적

| 원본 요구사항 | 구현 Task | 검증 |
| --- | --- | --- |
| PR-01, PR-02, PR-03 | T01, T05, T07, T11 | 제품 가정 유지, AC-01/10/16, 정확한 Chat 표면 증거 |
| ARC-01, SEC-01 | T02, T05, T06 | outbound 연결, 인증·철회·AC-11 |
| CAP-01, CAP-02, APR-01 | T02, T03, T05, T06 | AC-04/09/17, 숨긴 도구 직접 호출 |
| FS-01, ART-01 | T05, T06 | AC-07/11/12, cursor 재생·누락·보존 상태 |
| FS-02 | T04, T07 | AC-05, encoding/BOM/newline, 부분 적용 |
| EX-01, RUN-01, RUN-02 | T05, T07 | AC-06/08/10, crash injection, 프로세스 감독 |
| GIT-01, WT-01 | T10 | AC-13, 사용자 변경/미추적 파일 보존 |
| CU-01, CU-02, CU-03 | T09, T11 | AC-03, 실제 화면·좌표·부분 효과 |
| BR-01 | T08, T11 | AC-02, 전용 profile과 network 경계 |
| LSP-01 | T13 | AC-15, 문서 version/위치 encoding |
| SEC-02, SEC-03 | T02–T10 | AC-04/05/09/11/12/17, 제어면·비밀·권한 우회 |
| JRN-01, ERR-01 | T05, T07–T11 | AC-06/07/14/16, 실패·부분 효과·unknown 보존 |

이는 coverage 계획이지 시험 통과표가 아니다. 특정 요구사항의 일부만 구현하면 부분 완료로 남긴다.

## 4. 첫 실행 가능한 단위

첫 implementation PR은 T00과 T01의 **서버 측** 부분을 대상으로 한다. 실제 PC 제어 전 synthetic fixture에서 표준 MCP schema와 read/write/image/start/poll 계약을 확인한다. 첫 PR 완료를 제품 MVP 완료로 부르지 않는다.

첫 Windows 통합 단위는 인증된 Agent roundtrip과 P1 읽기다. 최종 MVP 판정은 P2의 코드·웹·GUI 흐름까지 포함한다. 보안 검증을 건너뛴 broad shell을 P1 읽기 서버에 추가하지 않는다.

T01 Chat 시나리오는 시험용 텍스트 결함을 읽고, 수정하고, 고의로 실패하는 검증 결과를 보고, 재수정·성공 검증까지 한 입력에서 수행하게 한다. 이미지에 담긴 무해한 표식을 후속 판단에 사용하는지도 기록한다. 도구 호출 로그만 있고 모델이 이미지에 반응한 증거가 없으면 image 이해를 통과 처리하지 않는다.

## 5. 실패 주입 지점

invocation DB commit 직전/직후, ACK 직전/직후, 큐 대기, native 효과 직후/최종 journal 이전, 출력 chunk flush/index 기록 사이, Gateway 재시작, Agent/감독 프로세스 재시작을 각각 시험한다.

검사할 불변 조건은 동일 ID 중복 효과 금지, 다른 인자 충돌, 실제 효과가 불명확할 때 unknown, 사용자 변경 보존, 미승인 효과 없음, 다른 owner 결과 비노출, cursor 안정성, 독립 작업 계속 진행이다. 프로세스/GUI exactly-once처럼 증명하지 않은 보장을 테스트 이름으로 만들지 않는다.

## 6. 증거 형식과 환경 분리

각 시험 기록에는 commit, task/요구사항/AC ID, 날짜, OS·SDK·browser·Agent 버전, 명령/절차, 실제 exit code, 결과 artifact, 미시험 이유를 포함한다. Chat 시험은 제품 표면, 화면의 정확한 모델명, 사용자 입력 수, 승인, 호출/재수정, 종료 원인을 추가한다. 인증 token이나 비밀은 저장하지 않는다.

상태는 `NOT_STARTED`, `IN_PROGRESS`, `PASSED`, `FAILED`, `BLOCKED_EXTERNAL`, `NOT_APPLICABLE`로 구분한다. 문서 점검은 따로 `DOC_CHECK`로 기록한다. Linux mock 성공을 Windows 성공으로, 서버 protocol 성공을 6 Pro Chat E2E 성공으로 승격하지 않는다.

CI 계획은 OS 독립 계약/Core 검증과 Windows native 단위/통합을 분리한다. headless Windows runner만으로 대화형 UIA·실제 모니터·사용자 입력·Chat 호환성을 판정하지 않는다. 실제 desktop 시험은 해당 환경의 명시적인 증거가 필요하다.

## 7. 승인 이후 이어받기

[AGENTS.md](../AGENTS.md)와 [IMPLEMENTATION_STATUS.md](../IMPLEMENTATION_STATUS.md)를 읽고 검토 승인 범위를 확인한 뒤 T00부터 진행한다. 아직 승인되지 않은 상태를 임의로 `APPROVED`로 바꾸지 않는다.

구현 PR마다 실제 변경·실행 시험·실패·미검증·다음 작업을 상태 파일에 갱신한다. 환경이 없으면 해당 native/실계정 검증만 차단하고 의존하지 않는 작업을 계속한다. 완료 보고는 코드 존재가 아니라 실제 검증 근거를 기준으로 한다.
