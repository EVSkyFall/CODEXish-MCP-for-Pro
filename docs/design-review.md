# Pre-implementation 설계 검토

기준일: 2026-09-15. 대상: [구현 설계 v0.1](implementation-design.md)와 [구현 계획](implementation-plan.md).
원본 기준 commit: `43d5f8793e4397894009f7860609a67d92084d48`.

**현재 판정: AWAITING_REVIEW.** 작성자 자체 점검만 수행하는 단계이며 사용자/독립 reviewer의 승인 기록은 없다. 이 문서를 추가했다는 사실은 설계 승인이나 구현 승인으로 간주하지 않는다.

## 1. 원본 문서 확인

README와 기존 docs 5개를 실제로 읽었다. 원본의 최상위 제품 가정, outbound Agent, 두 capability profile, resource queue, 장기 process, artifact/journal, P0–P3, AC-01–AC-17을 유지했다. 원본 요구사항을 삭제하거나 미구현 기능을 완료 표시하지 않았다.

## 2. 작성자 점검에서 발견한 주요 쟁점

| ID | 중요도 | 발견 사항 | 설계에서의 처리 | 남은 확인 |
| --- | --- | --- | --- | --- |
| R-01 | 높음 | 일반 MCP 지원은 정확한 6 Pro Chat의 연속 호출·이미지 전달·승인 재개 증명이 아님 | P0 disposable probe와 실제 Chat E2E 분리 | T01 실계정 결과 |
| R-02 | 높음 | 동일 OS 사용자 raw shell이 로컬 policy/DB/IPC/승인 UI를 건드릴 가능성 | protected broker/worker/helper 분리, unconfined 실행의 실제 범위 공개 | T03 OS 경계와 자기 승인 방어; 프로세스 분리만으로 통과 금지 |
| R-03 | 높음 | hash 확인 후 ReplaceFile 사이에 외부 editor 변경을 덮어쓸 수 있음 | conditional replacement spike를 독립 선행 작업으로 지정 | T04 실제 handle/경쟁 시험; 실패 시 해당 capability 비활성 |
| R-04 | 높음 | 전송 ACK와 실행 완료를 혼동하면 응답 유실 후 중복 효과 발생 | accepted 전 DB commit, logical invocation, delivery/state source 분리 | T05 crash 주입, T07/T09 실제 효과 조정 |
| R-05 | 중간 | process 시작 성공을 테스트 성공으로 오해하거나 poll timeout으로 프로세스를 죽임 | operation/process/business outcome과 wait/실행 수명 분리 | T07 child·exit·output drain 검증 |
| R-06 | 중간 | 전용 browser/URL allowlist를 network sandbox로 오해 | 실제 egress 범위와 강제 통제 여부를 capability로 구분 | T08 redirect/subresource/upload/local-network 시험 |
| R-07 | 결정 필요 | READ_ONLY의 '새 프로세스 금지'와 내부 Git 조회 helper의 관계가 불명확 | 임의 실행 금지는 유지; 검토 전 조회 이름으로 subprocess 권한을 열지 않음 | 검토자가 사전 준비 관찰기 또는 제한된 내부 trusted helper의 의미를 결정 |
| R-08 | 중간 | SQLite 기록과 외부 효과/artifact 파일을 단일 원자 transaction처럼 해석 | DB transaction과 외부 효과 분리, orphan/missing chunk 조정 | T05 storage crash와 unknown 보존 |
| R-09 | 중간 | GUI 이미지/UIA를 원자 snapshot으로 오해하거나 입력 성공만 보고 완료 | 시간차·target identity·사후 관찰·부분 효과 표시 | T09 실제 Windows 시험 |

R-02와 R-03은 해결 코드를 확인한 문제가 아니다. 위험과 검증 경로를 설계에 반영했을 뿐, 실험으로 닫아야 한다. 첫 T00/T01 scaffold 승인은 이 보장을 검증했다고 선언하는 승인이 아니다.

## 3. 권장 검토 결정

권장안은 **T00–T05의 기반 구현과 위험 spike를 먼저 승인하고**, 각 어댑터는 필요한 경계 증거를 확인한 뒤 활성화하는 방식이다. 이는 P2 목표를 줄이지 않으며, 불필요한 새 사용자 입력 없이 승인 범위 작업을 이어가기 위한 순서다.

아래 항목은 검토자의 판단을 위한 것이며 아직 체크하지 않았다.

- [ ] D-01–D-08의 스택·역할 분리와 별도 모델 없는 실행 도구 방향이 요구에 맞는다.
- [ ] P0/P1과 최종 P2 MVP의 차이가 명확하고 코드/웹/Windows GUI가 최종 범위에 남아 있다.
- [ ] R-02/R-03 spike 완료 전 안전하지 않은 어댑터를 지원으로 광고하지 않는 방식에 동의한다.
- [ ] R-07: READ_ONLY 내부 helper의 허용 의미를 명시하고 기존 문서와 함께 정합화한다.
- [ ] 미검증 Windows/Chat 결과를 blocked로 남기되 독립 Core 작업은 진행한다.

## 4. 리뷰 결과 기록 양식

```text
Reviewer:
Role: user / independent reviewer
Reviewed commit:
Decision: APPROVE / CHANGES_REQUESTED
Approved implementation scope:
R-07 decision:
Remaining gates and required evidence:
Date and review reference:
```

작성자가 실제로 호출하지 않은 sub-agent나 존재하지 않는 reviewer의 이름·판정을 작성하지 않는다. 구현 후 Post-Review는 실제 diff와 테스트 증거를 대상으로 별도 진행한다.

## 5. 문서 점검과 미수행 항목

문서 점검 범위는 UTF-8, fenced block 구조, JSON 예시 파싱, 내부 링크 파일 경로, task ID, 원본 요구사항 ID/AC ID의 계획 coverage다. 외부 링크의 모든 미래 가용성이나 source 구현을 검증하는 시험이 아니다. 구체적인 실행 결과는 [진행 상태](../IMPLEMENTATION_STATUS.md)에 남긴다.

MCP handshake, OAuth/mTLS 구현, DB/crash test, Windows native, browser, 대화형 desktop, 6 Pro Chat E2E는 **전부 미수행**이다. `APPROVE`가 기재되기 전 구현을 시작하지 않는다.
