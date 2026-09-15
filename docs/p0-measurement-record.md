# P0 measurement record

Date: 2026-09-15. Source: user-supplied CODEXish-GPT-next-input-package.md §§A–C. This record preserves the supplied categories and marks empty cells as 미기록.

## A. Reviewer verification notice

The supplied notice reports that Claude Fable 5.1 reproduced code 41afc82 / documentation c1b04cd on the user's Windows 11 Pro 10.0.26200 and .NET SDK 10.0.401: Release build with 0 warnings and 0 errors, SELF_TEST_PASSED 97. It also reports successful CI runs 34933335170/34933330570, Windows 97 and Ubuntu 93. L-1 and the revised measurement procedure are approved; P0 code and procedure are complete.

This is reviewer-reported evidence about the named P0 revision, not a new user-PC execution by this assistant and not a filled-in T/P trial.

## B.1 결과표

| ID | Trial T (Thinking) | Trial P (Pro) | Trial P' (선택) |
| --- | --- | --- | --- |
| M-1 도구 노출·실제 호출 (모델 라벨, 403 여부) | 미기록 | 미기록 | 미기록 |
| M-2 한 입력의 호출 수: 총 / screenshot / 행동 / 파일·명령, 완수 여부, 중단 메시지 | 미기록 | 미기록 | 미기록 |
| M-3 쓰기 확인 횟수, "기억" 적용 여부, 확인 대기 시간 | 미기록 | 미기록 | 미기록 |
| M-4 초기 화면 nonce를 정확히 보고했는가 | 미기록 | 미기록 | 미기록 |
| M-5 `echo(delay_60_seconds=true)` 결과(성공/timeout 초) | 미기록 | 미기록 | 미기록 |
| M-6 T vs P의 M-2와 총 벽시계 시간 | 미기록 | 미기록 | 미기록 |
| M-7 GUI 완수, 행동 수, Save As 동작, gui-result.txt 텍스트·바이트·해시 | 미기록 | 미기록 | 미기록 |
| M-8 단계당(행동+관찰) 평균/최대 시간, 확인 대기 제외 | 미기록 | 미기록 | 미기록 |
| Trial R 결과 (하네스 증명) | 미기록 | | |
| 터널 종류, `rejected host=` 로그 유무, 사용한 `--allow-host/--allow-origin` | 미기록 | | |

## B.2 첨부

각 trial의 calls.jsonl: 없음. 모델 응답 전문: 없음. 연결 화면·모델 라벨·확인 대화상자·최종 보고 스크린샷: 없음. gui-result.txt 바이트 덤프: 없음. 이전 서버 CI/코드 리뷰는 위 실측 첨부를 대체하지 않는다.

## C. Case decision

**UNDETERMINED — 측정 결과 미기록.** Case 0의 양쪽 실패도 Case 4의 정상 완료도 입증되지 않았다. Pro 미지원, Thinking 전환, GUI 실패 또는 성공을 관찰 사실로 기록하지 않는다.

가정: 최신 사용자의 proceed/continue와 §D·§E의 직접 구현 지시는 서버 측 v1 첫 슬라이스를 계속하라는 작업 승인이다. 그 지시에 따라 PR #3을 구현하되, 이를 §B 측정 통과로 해석하지 않는다. §E.1의 PR #2 ready 전환 조건은 확인되지 않았으므로 기존 Draft를 유지한다. Case 값이 들어오면 §C의 낮은 번호 우선 규칙을 적용한다.

## 다음 실측에 사용할 구성

§B.0의 R → T → P → 선택 P' 순서를 유지한다. P/T는 아래 두 과제를 한 입력에 합친다. 이는 과제를 따로 측정했던 이전 README.p0.md의 trial 구성만 대체하며, fresh fixture·새 미저장 탭·image-only nonce·다른 기기에서 ChatGPT 조작 등 초기 조건은 유지한다. nonce를 프롬프트나 모델이 읽는 파일에 넣지 않는다.

> 이 대화에서 두 작업을 순서대로, 중간에 나에게 묻지 말고 끝까지 진행해. (1) workspace의 테스트가 실패한다. 통과할 때까지 고쳐라. (2) 그 다음 screenshot을 찍고 메모장 본문에 보이는 코드를 그대로 보고하라. 이어서 Ctrl+A 후 `CODEXISH-P0-OK`를 입력하고 CTRL+S로 echo가 알려준 경로에 저장하라. 대화상자도 화면을 보고 처리하라. 저장 후 read_file로 확인하라. 마지막에 두 작업의 실제 결과(테스트 exit code, 저장된 파일 내용)를 보고하라.

V1 브랜치에서 이 P0 측정을 실행할 때는 `--p0`로 시작한다. 기본 v1 endpoint는 GUI 도구를 등록하지 않는다. 실제 계정에서의 도구/이미지 전달과 OAuth 연결 성공 여부는 시험별로 별도 기록한다.
