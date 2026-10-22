# 작업 규칙

## 먼저 읽을 파일

README.md, docs/requirements.md, docs/architecture.md, docs/tool-contracts.md, docs/security.md, docs/product-assumptions.md, docs/implementation-design.md, docs/implementation-plan.md, docs/design-review.md, IMPLEMENTATION_STATUS.md를 확인한다.

## 제품 의도

ChatGPT 공홈 Chat의 Pro가 직접 판단하고, 이 MCP는 사용자 PC의 실행·관찰 도구를 제공한다. Pro 자체나 병렬 Best-of-N을 구현하는 프로젝트가 아니다. 별도 모델/API 키/Codex CLI 위임을 기본 의존성으로 추가하지 않는다.

사용자 입력 횟수 기반 제품 가정과 한 입력에서 최대한 많은 유효 작업을 완료한다는 목표를 유지한다. 확인되지 않은 할당량 규칙이나 Chat 자동 지속을 사실로 단정하지 않는다.

## 현재 gate

현재 단계는 설계 검토 대기다. docs/design-review.md의 사용자/독립 reviewer 승인 근거 없이 구현 완료나 리뷰 통과를 선언하지 않는다. 설계 승인 뒤 implementation-plan의 T00부터 승인된 범위에서 진행한다. 작성자의 자체 점검은 독립 리뷰가 아니다.

## 구현 원칙

- 유효 작업은 자원별 큐/합류로 처리한다. BUSY/ALREADY_RUNNING gate나 작업 전체의 임의 시간·호출 횟수 cap을 추가하지 않는다.
- 이미 허가된 작업을 반복 승인으로 끊지 않는다. 새로운 권한과 부작용은 실제 사용자 grant로 검증한다.
- raw shell의 cwd를 sandbox라고 하지 않는다. 제어면/승인 자기 변경, Windows 파일 경쟁, 실제 실행 경계를 시험한다.
- 사용자 변경·encoding·미추적 파일을 보존하고 자동 reset/force push/merge/delete로 작업을 끝내지 않는다.
- process 시작/전송 성공과 업무 성공을 분리하고, 불명확한 효과는 unknown으로 남긴다.
- 단절 시 같은 invocation으로 재조회한다. 효과 불확실한 클릭/명령을 새 ID로 맹목 재실행하지 않는다.

실제 Windows/Chat 환경이 없으면 해당 검증만 BLOCKED_EXTERNAL로 기록하고 의존하지 않는 Core·계약·mock 작업을 계속한다. mock/서버 시험을 실계정 또는 Windows 성공으로 보고하지 않는다. 변경과 실제 실행 결과를 IMPLEMENTATION_STATUS.md에 기록한다.
