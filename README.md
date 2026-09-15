# CODEXish MCP for Pro — v1 coding slice

Windows PC에서 파일 편집·명령 실행·Git 조회를 제공하는 단일 C#/.NET 10 MCP 서버다. 판단은 연결한 ChatGPT가 수행한다. 현재 v1 endpoint는 **코딩 도구 20개 + session.checkpoint**, 내장 단일 사용자 OAuth와 로컬 제어 endpoint를 제공한다.

[실제 구현·CI 상태](IMPLEMENTATION_STATUS.md) · [설계 v0.2](docs/v1-design.md) · [슬라이스 계획](docs/v1-plan.md) · [P0 실측 원장](docs/p0-measurement-record.md)

## 1. 빌드와 자체 시험

.NET 10 SDK와 Git이 있는 환경에서 저장소 루트 기준으로 실행한다. 기존 P0 프로젝트를 승격했으므로 프로젝트 경로는 그대로다.

```powershell
dotnet restore src/Codexish.P0/Codexish.P0.csproj
dotnet build src/Codexish.P0/Codexish.P0.csproj -c Release --no-restore
dotnet run --project src/Codexish.P0 -c Release --no-build -- --self-test
```

self-test는 새로운 임시 root·DB·프로세스·합성 인증 값을 사용한다. P0, v1 coding, FIFO cancellation 세 suite가 실행되며 마지막 `ALL_SELF_TEST_SUITES_PASSED`와 exit code 0을 확인한다. 중간 suite의 TOTAL_PASSED는 뒤 FIFO suite를 포함하지 않는다. 검증한 c478da8에서는 Windows 97+123+4=224, Linux 93+121+4=218이다.

## 2. 로컬 설정

저장소의 [codexish.json](codexish.json)은 **자리표시자 예제**다. 이를 모든 workspace root 밖의 로컬 파일로 복사한 뒤 실제 값으로 편집한다. 권장 위치는 `%LOCALAPPDATA%\Codexish\codexish.json`이다. 편집된 인증 설정을 저장소에 commit하거나 Chat에 붙여넣지 않는다.

| 필드 | 설정 |
| --- | --- |
| roots | 이미 존재하는 작업 폴더의 id와 절대 path. read/write/shell은 각각 설정하며 생략 시 true |
| public_url | 터널의 HTTPS origin, 예: https://your-host.example. 끝에 /mcp를 붙이지 않는다 |
| client_id | 이 서버의 사전 등록 OAuth client ID. 예제는 codexish-chatgpt |
| client_secret | 이 서버용으로 로컬에서 새로 만든 무작위 비밀값. ChatGPT 커넥터의 client secret과 동일 값 |
| password_hash | 아래 --hash-password로 만든 새 서버 로그인 비밀번호의 해시 |
| redirect_uris | 현재 커넥터가 요구하는 정확한 OAuth callback URI. 예제 도메인을 그대로 쓰지 않는다 |
| git_executable | 설치된 git.exe의 절대 경로. 예제 경로와 다르면 수정. 빈 문자열이면 Git 조회는 미지원으로 표시 |
| allowed_hosts / allowed_origins | 추가할 정확한 Host/Origin. public_url의 Host/Origin은 자동으로 포함 |
| port / control_port | 기본 3000 / 3001. 서로 다른 loopback 포트 |

state_directory를 생략하면 Windows의 LocalApplicationData/Codexish를 사용한다. DB, artifacts, backups, cursor key, control.token이 여기에 생성된다. 지정할 때는 절대 경로를 쓴다. **JSON 문자열 안의 `%LOCALAPPDATA%`, `$env:...`, `~`는 확장되지 않는다.** root와 상태 디렉터리는 양방향으로 겹치면 안 된다.

새 로그인 비밀번호의 해시는 다음 명령으로 만든다. 콘솔 입력 문자는 표시되지 않으며 출력된 `pbkdf2-sha256$...` 문자열을 로컬 설정에 넣는다. 이는 OpenAI 계정 비밀번호가 아니다.

```powershell
dotnet run --project src/Codexish.P0 -c Release --no-build -- --hash-password
```

client secret도 이 서버 전용으로 새로 생성한다. 모델 API key나 기존 서비스 token은 필요하지 않다. 이 문서의 설정 작업은 사용자가 로컬에서 수행하는 절차이며, 저장소 작업 중 실제 인증정보를 생성·조회·등록하지 않았다.

## 3. 서버·터널·OAuth 연결

설정 완료 후 실행한다. 다음 `$env:`는 PowerShell이 확장하는 명령 인자다.

```powershell
dotnet run --project src/Codexish.P0 -c Release --no-build -- --config "$env:LOCALAPPDATA\Codexish\codexish.json"
```

한 프로세스가 `http://127.0.0.1:3000`과 `http://127.0.0.1:3001`을 연다. 외부 터널은 **3000의 전체 origin 경로**를 전달하도록 설정한다. `/mcp`뿐 아니라 OAuth discovery, `/authorize`, `/token`도 같은 public_url에서 도달해야 한다. 3001은 로컬 제어용이다. 기존 터널·배포 설정은 이 서버가 자동 변경하지 않는다.

ChatGPT Developer mode에서 remote MCP 앱을 만들 때 서버 URL을 `public_url + /mcp`로 지정하고 OAuth와 사전 등록 client_id/client_secret을 사용한다. 서버 설정에는 커넥터가 요구하는 정확한 callback을 redirect_uris로 등록한다. 자동 DCR/CIMD 대신 static credentials 경로를 선택한다. UI에서 연결하면 CODEXish 로그인 페이지에서 앞서 정한 **서버용 새 비밀번호**로 로그인한다. 연결 후 앱의 스키마를 새로고침하고 대화에서 선택하여 host.capabilities를 호출한다.

OpenAI 공식 [Developer mode 문서](https://developers.openai.com/api/docs/guides/developer-mode)는 static OAuth credentials, streaming HTTP와 앱 새로고침을 설명한다. 위 절차는 그 경로와 이 서버 구현을 연결한 안내다. 이 코드의 실제 사용자 계정 로그인 시험은 아직 수행하지 않았다.

| 서버 경로 | 역할 |
| --- | --- |
| /.well-known/oauth-protected-resource, /.well-known/oauth-protected-resource/mcp | canonical resource와 AS discovery |
| /.well-known/oauth-authorization-server | authorize/token endpoint 및 PKCE 정보 |
| /authorize | 단일 사용자 비밀번호 로그인 |
| /token | 코드 교환·refresh 회전 |
| /mcp | bearer 인증을 검사하는 MCP endpoint |
| /healthz | 서버 상태·인증 모드 |

HTTPS public_url이 바뀌면 설정과 커넥터 URL도 함께 갱신하고 다시 연결한다. 정확한 --allow-host/--allow-origin 옵션을 반복해서 추가할 수도 있다. 403은 `rejected host=... origin=... reason=...` 로그로 구분하고 의도한 주소만 설정한다. Host 허용은 OAuth 인증을 대체하지 않는다.

개발용 직접 loopback 시험에만 `--no-auth`를 명시할 수 있다. 그 경우에도 root 설정은 필요하고 모든 listener는 loopback이다. 정상 외부 연결 절차는 위 OAuth 경로다.

## 4. 도구와 작업 흐름

| 그룹 | 현재 등록 도구 |
| --- | --- |
| 환경 | host.capabilities, workspace.info |
| 파일 | fs.list, fs.read, fs.search, fs.stat, fs.write, fs.apply_patch |
| 실행 | shell.run, process.start, process.poll, process.write, process.stop |
| Git 조회 | git.status, git.diff, git.log |
| 결과·복구 | artifact.read, artifact.search, operation.inspect, operation.cancel |
| 재개 | session.checkpoint |

처음에 host.capabilities와 workspace.info로 root_id와 지원 범위를 확인한다. fs.read의 sha256을 fs.write 또는 fs.apply_patch에 사용하고, 수정 후 shell.run으로 실제 프로젝트 테스트를 실행한다. 새 파일은 fs.write의 mode=create다. fs.apply_patch는 현재 기존 파일 수정용 unified diff이며 파일별 expected_sha256 map을 받는다. 생성·삭제·rename patch는 아직 지원하지 않는다.

shell.run은 `shell=pwsh`/`cmd`와 command 또는 executable+args를 명시한다. Windows 예시 입력 형태는 다음과 같다.

```json
{
  "root_id": "work",
  "cwd": "",
  "shell": "pwsh",
  "command": "dotnet test",
  "invocation_id": "run-tests-001",
  "wait_ms": 1000
}
```

`status=running`과 operation_id를 받으면 operation.inspect로 조회한다. process.start는 process_id를 반환하며 process.poll/write/stop을 사용한다. 같은 논리 요청의 재전송은 같은 invocation_id, 다른 수정·실행은 새 ID를 사용한다. wait_ms가 끝났다는 이유로 프로세스가 종료되지는 않는다. 수집 성공과 테스트 exit code 0은 별개다.

파일 교체 전 원본은 상태 디렉터리 backups에 `.bak`으로 저장된다. 응답의 backup 식별자로 찾는다. 저장은 exclusive_in_place_non_atomic이며 자동 rollback이나 사용자 파일 강제 복원은 하지 않는다.

stdout/stderr 원본은 각각 순서대로 로컬 artifact에 남는다. artifact.read는 redacted UTF-8 snapshot과 cursor를 제공한다. 같은 replay_cursor는 같은 페이지이며 큰 결과는 next_cursor를 따라 읽는다. 진행 중 source의 새 출력을 보려면 cursor 없이 다시 조회하여 새 snapshot을 만든다. utf8_base64는 해당 redacted view의 바이트로, 원시 binary export를 뜻하지 않는다.

session.checkpoint에 목표·완료 조건·남은 단계·핸들을 기록하면 같은 logical session의 host.capabilities가 마지막 요약을 반환한다. refresh token은 session을 유지하지만 새 로그인은 새 session이다. HTTP 연결이 끊기는 것은 session 종료가 아니다.

## 5. 로컬 제어와 프로세스 수명

로컬 control.token 값을 X-Codexish-Control 헤더에 넣어 **control_port의 POST /control**로 요청한다. 토큰을 모델·터널·로그로 전송하지 않는다. MCP 포트의 /control은 올바른 token 및 loopback Host여도 거부한다.

```json
{"action":"pause"}
```

지원 action은 pause, resume, kill-children, revoke-tokens다. 명시적인 product session 종료에는 `{ "action": "end-session", "session_id": "host.capabilities에서 받은 session_id" }`를 사용한다. pause는 새 변경 도구에 accepted=false/PAUSED/isError=false를 반환하며 실행 중 자식을 자동 종료하지 않는다. process.stop과 operation.cancel은 pause 중에도 사용한다.

session lifetime 프로세스는 end-session에서 종료하고 persistent는 유지한다. 둘 다 process.stop, kill-children, 서버 종료에서는 정리한다. Windows에서는 사용자 명령을 시작하기 전에 Job에 편입한다. stdin은 UTF-8 pipe이며 PTY/ConPTY는 이번 슬라이스에 없다.

## 6. Project 지시문 초안

아래 텍스트는 ChatGPT Project 지시문에 사용할 초안이다. 서버 instructions도 같은 읽기·검증·복구 원칙을 제공한다.

> Use CODEXish tools for the requested Windows workspace task. Read host.capabilities and workspace.info first. Work until the stated completion condition is met and verified; ask only when permission or essential user input is missing, or stop when the user cancels. Read before editing and use returned hashes. After every edit run the relevant tests, inspect failures, and fix them. Long processes return handles: poll them; a response wait is not a deadline. Inspect unknown effects instead of replaying them. Save the goal, completion condition, remaining steps and handles with session.checkpoint. Report actual exit codes, diffs and saved contents. Use GUI tools only when the server advertises them; observe before acting and verify afterward, prefer semantic targets, and never blindly repeat an unverified click.

## 7. 보존된 P0와 다음 슬라이스

v1의 computer.observe/query_ui/act는 아직 도구 목록에 없다. 다음 슬라이스에서 focus_window, UIA와 활성 탭, virtual desktop 변환, 행동 후 관찰을 구현한다. 브라우저 MCP 마운트와 tray는 그 다음 순서다.

기존 P0는 다음처럼 선택한다. [README.p0.md](README.p0.md)는 이전 설명을 원문 그대로 보존했으므로, **그 문서의 서버 시작 명령을 이 브랜치에서 사용할 때는 `--p0`를 추가**한다. PR #2 브랜치 자체의 명령은 변하지 않았다.

```powershell
dotnet run --project src/Codexish.P0 -c Release --no-build -- --p0 --port 3000
```

T/P 통합 측정 프롬프트와 아직 빈 결과표는 [P0 실측 원장](docs/p0-measurement-record.md)에 있다. 서버 CI는 실제 Chat 도구 수신이나 GUI 성공 판정이 아니다. 현재 지원 차이와 비보장 범위는 [설계 마지막 절](docs/v1-design.md#비보장-목록)에 모아 두었다.
