# 도구 계약

상태: 구현 전 제안 계약. 모든 도구명·필드는 CODEXish의 설계이며 Codex에서 복사한 API나 현재 호출 가능한 서버 명세가 아니다. 구현 시 JSON Schema와 capability registry를 이 문서에 맞춰 작성하고 클라이언트 호환성을 검증한다.

목표는 [사용자 입력 한 번 안의 지속 작업](architecture.md)이다. 도구 결과는 다음 관찰·수정·검증에 필요한 식별자와 복구 경로를 함께 전달한다.

## 1. 공통 계약

### 컨텍스트와 도구 노출

기기 작업은 `device_id`, `session_id`, `workspace_id`에 바인딩한다. 상태 조회에서 핸들로 컨텍스트를 찾는 경우에도 소유권을 다시 확인한다. 인증 주체·허가 상태를 모델이 입력한 문자열로 신뢰하지 않는다.

변경 작업은 `invocation_id`를 요구하고 동일 논리 작업의 재전송에 재사용한다. 세션 내 ID·인자 해시가 다르면 충돌이다. 단순 조회에는 부작용 중복 방지 키가 필요 없지만 추적용 request ID를 남긴다. 경로·cursor·element ID·artifact ID는 해당 workspace·세션·관찰 범위 밖에서 재사용할 수 있는 권한 토큰이 아니다.

도구 설명은 사용 상황, 효과, 필요한 권한, 반환 핸들, 실패 시 다음 행동을 명시한다. 구현되지 않았거나 허용되지 않은 capability는 노출하지 않으며 직접 호출도 실행 경로에서 검증한다. 읽기와 변경을 하나의 범용 dispatcher에 섞어 `readOnlyHint: true`로 표시하지 않는다.

MCP의 `content`, `structuredContent`, `isError`, schema를 사용한다. 구조화된 결과는 텍스트 JSON으로도 전달해 호환성을 검증하고, 화면은 image content로 제공한다. [MCP 도구 규격](https://modelcontextprotocol.io/specification/2025-06-18/server/tools)

### 결과 envelope

다음 JSON은 `structuredContent`에 들어갈 **본문 예시**다. MCP의 표준 최상위 필드라는 뜻은 아니다. 현재 schema version은 문서 초안을 구분하는 값이다.

```json
{
  "schema_version": "0.1",
  "invocation_id": "inv_example",
  "status": "running",
  "data": {"operation_id": "op_example", "process_id": "proc_example"},
  "output": {
    "text": "Build started",
    "next_cursor": "cursor_example",
    "complete": false,
    "artifact_id": "art_example"
  },
  "error": null
}
```

- `status`: `accepted`, `queued`, `awaiting_approval`, `running`, `succeeded`, `failed`, `cancelled`, `unknown` 중 하나다. 순수 조회는 그 조회의 완료 상태를 나타내고, 조회된 operation의 상태는 `data`에 담는다.
- `data`: 도구별 데이터다. 실행·대기 작업은 조회 가능한 `operation_id`를 반환한다.
- `output`: 생략 가능하다. cursor는 불투명 문자열이고 원본·snapshot 범위와 읽은 위치에 바인딩한다. `complete`는 해당 출력의 수집 완료 여부이며 프로세스 성공을 뜻하지 않는다.
- `error`: 성공·정상 진행이면 `null`, 오류면 아래 공통 구조를 따른다. 알려진 효과·부분 실행도 표현한다.

stdout와 stderr는 구분해 수집하고, 합쳐서 보여줄 때 스트림·순번·시각을 남긴다. 이미지·바이너리는 MIME type과 artifact 메타데이터를 사용한다. 한 응답의 미리보기 길이와 검색 페이지 크기는 전송·문맥 관리 변수이며 작업 전체의 데이터 손실이나 임의 제한이 아니다.

### 긴 작업과 취소

긴 작업은 기다리기만 하는 HTTP 호출에 묶지 않는다. 시작·조회·취소를 제공하고 실제 완료까지 이어갈 수 있는 핸들을 반환한다. MCP 확장 작업 기능이나 서버가 클라이언트를 자동 재호출하는 능력에 의존하지 않는다.

`wait_ms`는 응답 대기 시간이고 실행 deadline은 별개다. 대기 종료는 정상적인 상태 반환이다. 실행 deadline을 선택했다면 만료 시 처리와 효과 상태를 보고한다. `cancel`은 요청된 중단이지 이미 발생한 효과의 취소가 아니다.

## 2. Host, session, operation, workspace

| 제안 도구 | 계약 | 프로필 |
| --- | --- | --- |
| `host.capabilities` | protocol/schema version, 실제 구현·허용된 기능, 유효 profile/mode, 실행 경계, 제약 출처, 누락 이유 | R/F |
| `host.list_devices` | 사용자 소유 기기의 이름·OS·온라인 여부·마지막 관측 시각. 비밀정보 제외 | R/F |
| `session.inspect` | 현재 세션·권한 범위·작업 핸들·checkpoint·journal 위치 | R/F |
| `session.journal` | 세션의 이벤트를 cursor로 조회·검색 | R/F |
| `session.checkpoint` | 관찰 가능한 목표·완료/남은 작업·근거를 저장. 권한 변경 기능 없음 | F |
| `operation.inspect` | invocation/operation 상태·대기·효과·오류·새 이벤트·결과 조회 | R/F |
| `operation.cancel` | 지정 작업만 취소 요청하고 실제 terminal 상태를 조회하도록 안내 | F |
| `workspace.info` | roots·경로 종류·모드·저장소·현재 worktree 정보 | R/F |
| `workspace.list_worktrees` | 경로·branch·HEAD·변경·작업 참조 조회 | R/F |
| `workspace.create_worktree` | repo, base ref, 요청된 branch, 목적 경로, 변경 포함 여부를 검증 후 생성 | F |
| `workspace.remove_worktree` | 지정 worktree와 변경·의존성을 검사하고 허가된 제거 실행 | F |

R/F는 `READ_ONLY`와 `FULL_CONTROL`, F는 `FULL_CONTROL`에서만 노출할 수 있다는 뜻이다. F라고 해서 해당 세션에 자동 허용되지는 않는다. 세션·workspace 등록과 grant 변경은 로컬 사용자의 설정 경로에서 수행한다. R 세션에 시작된 프로세스가 없어도 관찰 가능한 프로세스·operation만 조회할 수 있다.

## 3. Filesystem

| 제안 도구 | 핵심 입력 | 결과·불변 조건 |
| --- | --- | --- |
| `fs.list` | 상대 경로, depth, hidden 포함 여부, cursor | 항목·종류·다음 cursor. reparse point를 무조건 따라가지 않는다. |
| `fs.stat` | 경로 | 크기·시각·종류·encoding 정보와 요청 시 hash |
| `fs.read` | 경로, 줄 범위 또는 byte 범위 | 줄 번호·encoding·줄바꿈·hash·완전성. binary는 artifact로 반환 |
| `fs.search` | query, literal/regex, root, glob, cursor | 파일·줄 번호·일치 구간. 검색 범위와 제외·오류를 표시 |
| `fs.write` | 경로, 내용/encoding, create 또는 replace, 기대 상태 | F. create는 비존재, replace는 현재 hash 검증. 원자적 저장 |
| `fs.apply_patch` | unified diff, 대상별 expected hash | F. 변경 전후 hash·적용 결과·diff artifact |
| `fs.mkdir`, `fs.move`, `fs.delete` | 정확한 대상, 기대 상태, 필요한 grant | F. 후속 기능. 재귀·덮어쓰기·제거 범위를 숨기지 않는다. |

파일은 기본적으로 workspace 상대 경로로 지정한다. 절대 경로가 필요하면 등록된 root와의 관계를 명확하게 검증한다. `..`, 대소문자, UNC, 8.3 alias, symlink, junction, reparse point와 검사 이후 경로 교체까지 고려한다. [경로와 실제 실행 경계](security.md#실제-실행-경계)

텍스트 편집은 기존 encoding, BOM, 줄바꿈을 보존한다. 변환을 요청받았을 때만 변환한다. patch 적용 전후의 사용자 변경을 보호할 수 있는 조건부 교체 계약이 필요하며, hash를 한 번 읽고 무조건 덮어쓰는 구현은 충분하지 않다.

같은 볼륨의 임시 파일에 기록·flush 후 원자적 교체를 시도하고 실제 Windows 파일 시스템 보장 범위를 검증한다. 여러 파일 patch를 전체 원자적 트랜잭션이라고 주장하지 않는다. 파일별 성공·실패·전후 hash를 반환하고 부분 적용을 명시한다. 자동 rollback이 나중의 사용자 변경을 덮어써서는 안 된다.

```json
{
  "device_id": "device_example",
  "session_id": "sess_example",
  "workspace_id": "ws_example",
  "invocation_id": "inv_patch_example",
  "path": "src/example.txt",
  "expected_sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "patch": "--- a/src/example.txt\n+++ b/src/example.txt\n@@ -1 +1 @@\n-old\n+new\n"
}
```

hash는 형식 설명용 값이다. 실제 호출은 바로 앞의 `fs.read` 등에서 얻은 파일 hash를 사용한다.

## 4. Shell과 process

| 제안 도구 | 입력·동작 | 반환 |
| --- | --- | --- |
| `shell.run` | 명시된 shell/command 또는 executable/args, cwd, 승인된 env 참조. F | 완료 시 exit code·stdout/stderr·기간. 길어지면 operation/process 핸들 |
| `process.start` | executable/args 또는 명시된 shell command, cwd, PTY 여부, 수명 정책. F | operation ID·안정적인 process ID·PID·수명 정책 |
| `process.poll` | process ID, cursor, 선택적 wait | 새 stdout/stderr·다음 cursor·running/exited 상태·실제 exit code |
| `process.inspect` | 허가된 프로세스 선택 | 상태·소유 세션·시작 시각·진행 작업. 명령줄 비밀정보 보호 |
| `process.write` | process ID, stdin bytes/text, invocation ID. F | 실제 전달량·결과. 응답 유실 시 맹목적으로 재입력하지 않음 |
| `process.stop` | process ID, 종료 방식, invocation ID. F | 취소 요청·종료 결과·자식 프로세스 상태·잔여 효과 |

입력은 structured args를 우선하고 shell 문자열을 쓸 때 해석기를 명시한다. cwd를 workspace로 지정하는 것만으로 파일·네트워크 접근이 제한되지는 않는다. 빌드·테스트·Git hook도 코드를 실행한다. 실제 격리와 grant를 적용한다.

ConPTY를 대화형 터미널 후보로 사용한다. 웹 개발 서버 같은 장기 프로세스는 시작 후 poll한다. 이미 종료된 프로세스는 exit code를 계속 조회할 수 있어야 하며, PID 재사용을 다른 작업으로 오인하지 않도록 안정적인 핸들과 시작 정보를 검증한다. 사용자가 시작한 무관한 프로세스를 자동 정리하지 않는다.

## 5. Git과 worktree

| 도구군 | 제안 도구 | 조건 |
| --- | --- | --- |
| 읽기 | `git.status`, `git.diff`, `git.log`, `git.branch` | R/F. `git.branch`는 조회 전용이다. 기준 ref·worktree·staged/unstaged/untracked 범위를 표시 |
| 변경 | `git.checkout`, `git.stage`, `git.commit`, `git.merge` | F. 대상 ref·경로·기대 HEAD와 효과를 명시하고 기존 충돌·변경을 보존 |
| 외부 전송·파괴적 변경 | `git.push`, `git.force_push`, `git.reset_hard` | F. 각 효과에 대한 유효 grant 필요. 자동 승인으로 해석하지 않음 |
| worktree | `workspace.*_worktree`, `workspace.list_worktrees` | 생성·제거는 F, 목록은 R/F. [아키텍처](architecture.md)의 lifecycle 적용 |

`git.diff`는 신규 미추적 파일을 빠뜨리지 않도록 상태 목록과 전체 신규 내용의 조회 경로를 함께 제공한다. Git 종료 코드와 실제 HEAD·index·worktree 상태를 구분한다. 충돌 발생은 충돌 파일과 상태를 반환하며 자동 reset으로 해결하지 않는다.

조회 도구는 외부 diff/textconv/pager 같은 임의 코드 실행 경로를 통제한다. commit hook 등 쓰기 단계의 실행도 프로세스 정책을 적용한다. 구조화 Git 도구가 shell 실행 권한을 우회하는 통로가 되어서는 안 된다.

## 6. Computer Use

### 관찰 → 대상 확인 → 행동 → 결과 확인

| 제안 도구 | 역할 |
| --- | --- |
| `computer.observe` | R/F. 허용된 desktop/window/region의 screenshot·창 목록·포커스·커서·UIA 요약을 한 관찰로 묶음 |
| `computer.query_ui` | R/F. observation/window 기준으로 role·name·text·범위 검색, cursor 페이지 제공 |
| `computer.act` | F. 관찰 ID·대상·행동을 검증하고 실행, 다음 관찰과 실제 결과 반환 |
| `computer.batch` | F, 후속 선택 기능. 승인 범위 안에서 중간 상태를 확인하는 제한된 작업 묶음 |

`computer.observe`는 immutable `observation_id`, UTC 시각, device·desktop session·window 식별자, 모니터 배치, 좌표계와 이미지 변환 정보를 반환한다. screenshot과 UIA 수집 사이에도 화면은 변할 수 있으므로 수집 시점·부분 실패를 기록한다. “한 관찰”은 화면 전체의 원자적 snapshot 보장을 의미하지 않는다.

모델이 이미지를 볼 수 있도록 MCP image content를 반환하며 artifact만 던져놓지 않는다. 원본 해상도·crop 조회도 지원한다. 임의 픽셀 크기를 제품의 고정 제한으로 정하지 않는다.

### 다중 모니터와 좌표 변환

정규 좌표계는 Windows virtual desktop의 physical pixel이다. 왼쪽·위쪽 모니터는 음수 좌표를 가질 수 있다. Agent의 DPI awareness, UIA bounds와 screenshot의 스케일·원점을 명시적으로 정규화한다. Windows는 DPI awareness에 따라 앱이 보는 좌표·이미지 스케일이 달라질 수 있다. [Microsoft DPI 문서](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows)

다음은 축소된 모니터 이미지의 **기하 정보 예시**이며 권장 해상도나 한도가 아니다.

```json
{
  "observation_id": "obs_example",
  "window_id": "win_example",
  "coordinate_space": "virtual_desktop_physical_px",
  "monitor": {"x": -2560, "y": 0, "width": 2560, "height": 1440, "dpi_scale": 1.25},
  "image": {"width": 1280, "height": 720},
  "image_to_desktop": {"scale_x": 2, "scale_y": 2, "offset_x": -2560, "offset_y": 0}
}
```

이 예시의 이미지 좌표 `(u, v)`는 desktop의 `(-2560 + 2u, 2v)`로 변환한다. crop·resize·모니터 변경이 있으면 변환과 관찰 ID를 갱신한다. DPI scale을 다시 곱해 이중 변환하지 않는다. 브라우저의 CSS pixel 좌표를 이 desktop 좌표와 혼용하지 않는다.

### UIA와 행동 계약

UIA element는 observation에 묶인 `element_id`, role/control type, name, bounds, enabled, focused, 지원 pattern 정보를 반환한다. 민감 필드는 모델용 결과에서 제거한다. 가능한 경우 앱 API → UIA element → 좌표 순으로 사용한다.

행동은 `click_element`, `click_coordinate`, `double_click`, `right_click`, `move_pointer`, `drag`, `type_text`, `key_press`, `key_combo`, `scroll`, `focus_window`, `minimize_window`, `maximize_window`, `restore_window`를 제안한다. 대기는 별도 관찰 조회나 명시적 wait 옵션으로 표현한다. 타깃·포커스·window 소유 프로세스·desktop session을 실행 직전에 확인한다.

```json
{
  "device_id": "device_example",
  "session_id": "sess_example",
  "workspace_id": "ws_example",
  "invocation_id": "inv_click_example",
  "observation_id": "obs_example",
  "window_id": "win_example",
  "action": {"type": "click_element", "element_id": "uia_example"},
  "observe_after": true
}
```

타깃·모니터·레이아웃·포커스 등이 변경되면 입력 전에 `STALE_OBSERVATION`과 새 관찰을 반환한다. 화면 전체 hash 변화만으로 애니메이션 화면을 영원히 거절하지 않도록, 타깃에 관련된 유효성 조건을 검사한다. 검사와 입력 사이 경쟁을 완전히 없앨 수 있다고 주장하지 않으며 실행 후 관찰로 결과를 확인한다.

입력 전달 성공은 저장·클릭의 업무 효과 성공이 아니다. 부분 입력이나 실행 후 캡처 실패는 `side_effects`와 관찰 오류를 따로 기록한다. 성공 여부를 확인하지 못한 클릭을 자동 반복하지 않는다.

SendInput은 UIPI와 무결성 수준의 제약을 받는다. 실패 원인이 항상 상세하게 식별되는 것은 아니므로 근거 없이 UIPI로 단정하지 않는다. 자동 권한 상승을 해결책으로 적용하지 않는다. [Microsoft SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

후속 `computer.batch`는 관찰 없이 긴 좌표열을 재생하는 도구가 아니다. 단계별 타깃·효과 확인과 부분 완료 보고가 필요하고, 중간에 승인 대상이 생기면 해당 단계에서 멈춘다. 로그인 보안·결제·삭제 같은 민감한 확인 단계를 무관찰 batch로 묶지 않는다.

## 7. Browser / CDP

브라우저는 별도의 semantic 도구를 제공한다. 기본 작업 대상은 사용자가 허가한 전용 프로필이고 기존 개인 프로필에 대한 연결은 opt-in이다. 디버깅 endpoint를 공용 네트워크에 노출하지 않는다. 브라우저 실행·새 탭·탐색은 관찰 도구에 숨기지 않는 F 동작이다.

| 제안 도구 | 입력·출력 | 프로필 |
| --- | --- | --- |
| `browser.start` | 허가된 전용 프로필로 브라우저를 준비하고 browser/tab 핸들·연결 상태·초기 관찰·수명 정책 반환 | F |
| `browser.attach` | 사용자가 opt-in한 기존 브라우저에 연결하고 실제 프로필·대상과 데이터 권한 검증 후 핸들 반환 | F |
| `browser.close` | 지정 브라우저 연결·소유 프로세스의 종료 범위를 명시. 사용자 브라우저는 연결 해제와 프로세스 종료를 구분 | F |
| `browser.observe` | 지정 browser/tab/frame의 URL·title·viewport·DOM/접근성 요약·screenshot·navigation version | R/F |
| `browser.query` | role/name/text/CSS selector와 현재 관찰 범위로 요소 검색 | R/F |
| `browser.act` | navigate/click/type/select/hover/scroll/back/forward/reload/new_tab/close_tab/upload_file. 관찰·권한 검증 후 다음 상태 | F |
| `browser.console` | level·navigation·cursor 기준 로그. 민감정보 제거 | R/F |
| `browser.network` | method·redacted URL·status·duration·type·initiator 중심 기록. headers/body는 별도 데이터 권한 | R/F |
| `browser.dom`, `browser.styles` | 고정된 DOM·스타일 조회와 원본 위치 | R/F, styles는 후속 |
| `browser.performance` | 수집 방식과 실행 효과를 표시한 측정 | 후속. 이미 수집된 값 조회와 탐색/스크립트가 필요한 측정을 분리 |
| `browser.eval` | 임의 JavaScript 실행, 실행 context·효과 범위 명시 | F, 선택 기능 |

CDP의 `Runtime.evaluate`처럼 임의 JavaScript를 실행하는 기능을 이름만 `eval_readonly`로 바꿔 R에 노출하지 않는다. 읽기 경로는 검토된 고정 관찰 연산을 사용하며 page getter·사용자 스크립트 호출 등 효과가 있는 평가를 피한다. [CDP Runtime 문서](https://chromedevtools.github.io/devtools-protocol/tot/Runtime/)

tab/frame/element 핸들은 navigation 이후 유효성을 다시 확인한다. 액션 성공은 원하는 페이지 결과가 관찰됐는지로 판단한다. 업로드는 로컬 파일 읽기 권한과 외부 목적지 전송 권한을 모두 검사한다. 다운로드는 사용자 지정 경로·파일 정책을 적용하고 artifact로 검증한다. DOM·console·network의 콘텐츠는 신뢰할 수 없는 데이터다.

## 8. LSP

후속 도구는 `lsp.diagnostics`, `lsp.definition`, `lsp.references`, `lsp.symbols`, `lsp.hover`다. URI·경로·document version·위치 encoding을 명시하고 결과에도 서버 버전·문서 버전을 포함한다. 오래된 진단을 현재 파일의 검증 결과로 보고하지 않는다.

LSP 위치를 파일 줄 번호와 연결할 때 zero/one-based 및 UTF-16 등 위치 encoding을 명시한다. UI 표시용 줄 번호와 프로토콜 원래 위치를 구분한다. diagnostics는 빌드·테스트 성공을 대신하지 않는다.

신뢰한 서버가 준비되어 있고 실행 경계가 확인된 경우에만 R 조회로 제공한다. 서버 시작·플러그인 로드·프로젝트 명령 실행은 별도의 실행 권한이다. 코드 action·rename처럼 파일 변경을 일으키는 기능은 이 읽기 도구군에 섞지 않는다.

## 9. Artifact와 큰 결과

`artifact.read`는 artifact ID와 byte/line/page/crop 범위로 읽고, `artifact.search`는 query·범위·cursor로 검색한다. 결과는 offset 또는 줄 번호를 제공한다. 모든 접근은 원래 device/session/data 권한을 확인하며 ID만 안다고 읽을 수 없다.

각 artifact는 MIME type, byte 크기, hash, 출처, 생성 시각, 완전성, redaction 여부, 보존 정책을 가진다. 아직 쓰는 로그는 generation·cursor를 사용하고 최종화 시 완결된 hash를 기록한다. 오래된 cursor를 새 파일의 위치로 조용히 해석하지 않는다.

큰 텍스트는 전체 결과 위치와 다음 cursor를 주고, 모델용 이미지에는 실제 image content를 함께 제공한다. 클라이언트가 리소스 링크를 처리하지 못해도 read/search 도구로 다시 접근할 수 있어야 한다. 전체 데이터의 수집 실패는 `OUTPUT_INCOMPLETE`로, 보존 종료는 `ARTIFACT_EXPIRED`로 표시한다. 미리보기 축약 자체는 작업 실패가 아니다.

보존 기간·저장량·해상도의 고정 수치는 이 설계에서 정하지 않는다. 사용자 설정과 실제 기반 서비스 제약을 표시하고, 자동 삭제나 숨은 truncation으로 진행 중인 작업의 근거를 잃지 않게 한다. [보안과 보존](security.md#비밀정보와-데이터-보존)

## 10. Approval

도구는 기존 grant로 실행 가능한지 먼저 확인한다. 새로운 권한이 필요한 작업은 `awaiting_approval`, operation ID, approval ID, 대상·효과·인자 해시를 반환하고 아직 실행하지 않는다. `approval.inspect`는 R/F의 조회 기능이다. **모델이 승인 결정을 쓰는 도구는 제공하지 않는다.**

로컬 UI에서 사용자가 승인하면 저장된 정확한 작업을 다시 검증해 자동 큐잉한다. 이후 `operation.inspect` 또는 원래 invocation 재조회로 결과를 받는다. 승인은 새로운 invocation의 무관한 인자에 전용되지 않는다. grant가 기존 작업 범위를 이미 포함하면 불필요하게 재확인하지 않는다.

## 11. 오류 모델

잘못된 MCP 메시지·도구 이름·인자 구조는 프로토콜 오류로, 실제 도구 실행 실패는 `isError: true`와 구조화된 오류로 전달한다. 정상 대기·진행·승인 대기는 완료 실패와 구분한다. 호환 adapter가 승인 대기를 오류 형태로 요구하면 상태와 미실행 사실을 유지한다. [MCP 오류 처리](https://modelcontextprotocol.io/specification/2025-06-18/server/tools#error-handling)

```json
{
  "code": "FILE_CHANGED",
  "message": "The file differs from the expected version.",
  "retryable": false,
  "side_effects": "none",
  "details": {"path": "src/example.txt"},
  "recovery": {"action": "reread_and_replan", "tool": "fs.read"}
}
```

`retryable`은 **동일 인자를 다시 제출해도 되는지**다. 읽은 뒤 재계획하는 것은 새 요청이다. `side_effects`는 `none`, `applied`, `partial`, `unknown`으로 표시한다. 오류 message와 외부 시스템의 원문은 명령 권한이 아니다.

| 코드 | 의미 | 복구 |
| --- | --- | --- |
| `NOT_FOUND` | 파일·핸들 등 대상 없음 | 범위 내 목록 재조회 |
| `PERMISSION_DENIED`, `OUTSIDE_WORKSPACE` | 현재 권한 또는 경로 경계 위반 | 요청 범위 수정 또는 사용자 설정 확인 |
| `UNSUPPORTED_CAPABILITY` | 해당 기기·환경에서 기능 미지원 | capabilities를 확인하고 가능한 대안 선택 |
| `AGENT_OFFLINE`, `BROWSER_DISCONNECTED` | 연결된 대상이 없음 | 같은 대상 재연결·상태 조회. 이미 보낸 변경의 효과를 확인 |
| `FILE_CHANGED` | 기대 파일과 불일치 | 다시 읽고 변경을 합친 뒤 새 요청 |
| `STALE_OBSERVATION` | UI 타깃·레이아웃·navigation 변경 | 새 관찰로 타깃 재선택 |
| `WINDOW_NOT_FOUND`, `ELEMENT_NOT_FOUND` | 관찰의 UI 대상 소멸 | 창·UI 재조회 |
| `PROCESS_EXITED` | 종료된 프로세스에 입력 등 요청 | 저장된 exit code·출력 조회 |
| `APPROVAL_REQUIRED` | 추가 허가가 필요하며 아직 실행하지 않음 | 로컬 UI에서 결정 후 operation 조회 |
| `APPROVAL_DENIED` | 사용자가 해당 요청을 거절함 | 반복 제출하지 않고 허용된 작업만 진행 |
| `TIMEOUT` | 실제 deadline 또는 기반 시스템 제한 도달 | 출처·단계·작업 상태를 조회. 전송 실패를 실행 취소로 단정하지 않음 |
| `CANCELLED` | 확인된 중단 | 부분 효과와 결과 확인 |
| `EXECUTION_FAILED` | 명령·빌드·Git 등 실행 실패 | exit code·stderr·진단을 보고 수정. 무관한 오류로 치환하지 않음 |
| `EXECUTION_UNKNOWN` | 효과 발생 여부를 확정할 수 없음 | journal과 실제 상태 대조, 자동 재실행 금지 |
| `IDEMPOTENCY_CONFLICT` | 같은 invocation에 다른 인자 | 기존 작업 확인, 별개 작업이면 새 ID |
| `OUTPUT_INCOMPLETE`, `ARTIFACT_EXPIRED`, `CURSOR_INVALID` | 결과 누락·보존 종료·조회 위치 불일치 | 가능한 원본·snapshot 재조회, 복구 불가 부분 명시 |

이 오류 집합에 `BUSY`/`ALREADY_RUNNING` 같은 유효 작업 거절 코드는 두지 않는다. 대기와 합류는 정상 상태로 표현한다.
