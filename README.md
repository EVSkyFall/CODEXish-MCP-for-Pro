# CODEXish MCP for Pro

A single C#/.NET 10 Streamable HTTP application that gives a ChatGPT connector real tools on a Windows PC.
Two projects live here:

- **[v1](#v1-slice-1-coding-core) — `src/Codexish.Server`**: the coding core (files, patches, shell and process
  supervision, read-only Git, artifacts, an invocation ledger, a built-in OAuth authorization server and a loopback
  control API; 21 tools), the desktop tools `computer_observe`, `computer_query_ui` and `computer_act` (slice 2),
  optional tools mounted from an existing browser MCP server ([slice 3](#browser-mounts-slice-3)) and a Windows
  tray ([slice 4](#tray-slice-4-windows)).
- **[P0 probe](#p0-probe) — `src/Codexish.P0`**: the seven-tool measurement fixture that answers whether the
  selected Chat Pro model can use tools and observe/control a disposable Notepad at all. It is unchanged and
  still runnable; the M-1 to M-8 measurements are still unperformed.

Design decisions for v1 are in [docs/v1-design.md](docs/v1-design.md) and the slice order is in
[docs/v1-plan.md](docs/v1-plan.md).

## v1 slice 1: coding core

### Create a configuration

```powershell
dotnet run --project src/Codexish.Server -c Release -- --init `
  --password "<a password you choose>" `
  --public-url "https://<your-tunnel-host>" `
  --root "proj=C:\Projects\Example"
```

`--init` writes `%LOCALAPPDATA%\Codexish\codexish.json` (override with `--config <path>`), generates the client
secret and the loopback control token with `RandomNumberGenerator`, stores the password as a PBKDF2-SHA256 hash,
and puts the hostname from `--public-url` into `allow_hosts` and its origin into `allow_origins` (the tunnel
terminates TLS, so the login form's own POST arrives with an https Origin over an http connection).
`--public-url` must be https unless you run with `--no-auth`, because the access token would otherwise cross
the tunnel in clear text. Roots may not overlap: one root per directory tree. Repeat `--root id=path` for more roots; each root
grants read, write and shell unless you edit the file afterwards. `--state-dir` moves the ledger, artifacts and
backups; it must stay outside every root or the server refuses to start. Add `--redirect-uri <uri>` (repeatable)
if your connector's callback differs from the default ChatGPT one. **`--init` prints the client secret and the
control token once. Treat both as passwords.**

### Run it

```powershell
dotnet run --project src/Codexish.Server -c Release
cloudflared tunnel --url http://127.0.0.1:3000
```

The listener is loopback only; the tunnel is what makes it reachable. Host and Origin checks are not
authentication — the bearer token is. `--no-auth` is accepted only when `allow_hosts` is empty, that is, for
loopback development.

### Connect the ChatGPT connector

| Field | Value |
| --- | --- |
| MCP endpoint | `<public_url>/mcp` |
| Authorization URL | `<public_url>/authorize` |
| Token URL | `<public_url>/token` |
| Client ID | `codexish-chatgpt` (from `codexish.json`) |
| Client secret | printed by `--init`, stored in `codexish.json` |
| Scope | `mcp` (the only scope this server issues) |
| PKCE | S256, required whenever the client sends a `code_challenge` |

Signing in opens a single password form served by this server. If the connector's callback is refused, the
server prints `rejected oauth stage=authorize reason=redirect_uri_mismatch offered_redirect_uri="..."` — rerun
`--init` with that value as `--redirect-uri`.

### ChatGPT Project custom instructions

Paste this into the Project's custom instructions so the harness text survives a client that does not surface
`initialize.instructions`:

> You are operating the user's Windows PC through these tools. Work until the stated completion condition is met
> and verified; do not stop to ask unless permission is missing or the user cancels. Read before editing and pass
> the returned sha256 to writes. After every edit run the relevant tests or build and fix observed failures. Long
> commands return handles: poll them; a response wait is not a deadline and never kills the process. Reuse
> invocation_id only when retrying the same action; inspect unknown operations, never replay them. Report actual
> exit codes, diffs, and file contents, not inferred success. Call session_checkpoint after each milestone so work
> can resume across turns.

### Local control

The control API answers only when the connection is loopback, the `Host` header is loopback, and the request
carries the control token. It is never reachable through the tunnel and it is not an MCP tool.

```powershell
$t = (Get-Content "$env:LOCALAPPDATA\Codexish\codexish.json" | ConvertFrom-Json).control_token
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:3000/control/pause         -Headers @{ "X-Codexish-Control" = $t }
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:3000/control/resume        -Headers @{ "X-Codexish-Control" = $t }
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:3000/control/kill-children -Headers @{ "X-Codexish-Control" = $t }
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:3000/control/revoke-tokens -Headers @{ "X-Codexish-Control" = $t }
Invoke-RestMethod -Method Get  -Uri http://127.0.0.1:3000/control/status        -Headers @{ "X-Codexish-Control" = $t }
```

Pause is a hold, not a refusal: while paused, a write is accepted into its queue and returned as `queued` with
`paused: true`, reads keep working, and the queue drains on resume.

### Browser mounts (slice 3)

CODEXish does not drive a browser itself. It starts an existing browser MCP server as a stdio child process and
publishes that server's tools next to its own. For Playwright MCP installed with `npm install @playwright/mcp` in a
tools directory, add this to `codexish.json`:

```json
"browser_mounts": [
  {
    "id": "pw",
    "root_id": "proj",
    "kind": "playwright",
    "command": "C:\\Program Files\\nodejs\\node.exe",
    "args": ["C:\\Tools\\playwright-mcp\\node_modules\\@playwright\\mcp\\cli.js", "--headless", "--browser", "chrome"],
    "profile_mode": "dedicated",
    "read_only_tools": ["browser_snapshot"]
  }
]
```

- `command` is started directly with `args`, without a shell, and with the root as its working directory. `.cmd`
  shims such as `npx` cannot be started this way; run `node` with the path to `cli.js`.
- `profile_mode: "dedicated"` (the default) keeps the browser's state in `<state_dir>\browser-profiles\<id>`:
  CODEXish appends `--user-data-dir` with that directory for `kind: "playwright"`. A dedicated mount whose own
  `args` contain `--user-data-dir`, `--cdp-endpoint`, `--extension`, `--storage-state` or `--config` is not started
  and is reported as `invalid_config`. `profile_mode: "existing"` passes `args` unchanged; choose it only when you
  deliberately point the backend at an existing profile, CDP endpoint or extension. `{profile_dir}` in `args`
  expands to the per-mount directory in both modes. `kind: "custom"` mounts another stdio MCP server without
  profile handling.
- Tools appear as `browser_<id>_<backend tool>`, for example `browser_pw_browser_navigate`. A name that is not
  `^[a-zA-Z0-9_-]+$` or would exceed 64 characters is shortened and given a short hash suffix. Every mounted tool
  takes the backend's own inputs inside `arguments`. Tools listed in `read_only_tools` run directly and bypass the
  ledger because your configuration says so; CODEXish does not check that they are free of side effects. Every other
  tool is treated as a change: it needs an `invocation_id`, runs through the ledger (an identical retry returns the
  stored result), and its `wait_ms` is a response wait only.
- Starting a mount and calling a read-only tool need read and shell grants on `root_id`; other tools also need write.
- The backend receives a small environment: system and user profile paths, `PATH`, `TEMP` and `DOTNET_ROOT`.
  Variables whose names look like credentials are never passed, and proxy variables are not passed either.
- A mount that fails to start does not stop the server. `host_capabilities` reports each mount's `state`
  (`connected`, `unavailable`, `invalid_config`, `exited`) with its error and the last lines of its stderr. Mounts
  are read when the server starts.
- Mounted tools run unconfined as you and are not contained by the root or its grants. Tools such as
  `browser_file_upload`, `browser_evaluate` or `browser_run_code_unsafe` can read files or run code anywhere your
  account can; the grants only decide whether CODEXish forwards a call, and the root is the backend's working
  directory, not a filesystem, network or code sandbox.

### Tray (slice 4, Windows)

```powershell
dotnet run --project src/Codexish.Server -c Release -- --tray
dotnet run --project src/Codexish.Server -c Release -- --tray --config D:\Codexish\codexish.json
```

`--tray` runs the same server behind a notification-area icon. Without a configuration file it first shows a setup
form (public https origin, one project root, a new CODEXish password and the OAuth callback), writes the file like
`--init` and shows the client secret once. The server starts only from **Start server**. The menu also offers status
with roots and processes, pause and resume, stopping session children, token revocation, the connection rejection
log, and **Edit roots and grants**, which stops the server and saves the file.

**Start configured tunnel** runs `tunnel.command` with `tunnel.args` as a child of the tray; `{port}` becomes the
listening port. Nothing downloads or selects a tunnel, starting the server never starts one, and stopping the server
or exiting the tray stops it:

```json
"tunnel": { "command": "C:\\Tools\\cloudflared.exe", "args": ["tunnel", "--url", "http://127.0.0.1:{port}"] }
```

The tunnel process inherits the tray's environment, so a tunnel tool can read its own settings from environment
variables. Its output appears in the connection log with the control token, client secret and password hash from
`codexish.json` replaced by redaction markers. The tray exists only in the Windows build; elsewhere `--tray` prints a
message and exits with code 2.

### Tests

```powershell
dotnet run --project src/Codexish.Server -c Release -- --self-test
dotnet run --project src/Codexish.Server -c Release -- --browser-tests
dotnet run --project src/Codexish.Server -c Release -- --tray-tests
```

The self-test uses real files, a real SQLite ledger, real child processes, a real booby-trapped Git repository
and a real in-process HTTP listener. It opens no tunnel, sends no desktop input and performs no ChatGPT
measurement. Windows-only checks print `SKIP` elsewhere. `--browser-tests` runs this executable as a stdio MCP
fixture behind the real HTTP host; `--tray-tests` drives the tray controller over the local control endpoint without
an icon or a tunnel. All three run in CI on Windows and Ubuntu.

Explicit local checks, never run in CI:

- `--browser-live-test <node.exe> <@playwright/mcp cli.js> <chrome.exe>`: a headless browser with a dedicated
  profile against a loopback page served by the test itself.
- `--tray-smoke-test`: creates and removes a notification icon.
- `--self-test-desktop` and `--self-test-desktop-http`: control a new self-owned window on the interactive desktop,
  the second through the authenticated HTTP host. Both take the foreground and send real input.

### What v1 slice 1 does not do

There is no sandbox: `shell_run`, builds, tests and any Git hook they invoke run with your full Windows rights.
`mode=replace` is not crash-atomic, `fs_apply_patch` has no rollback, redaction is a small published pattern set
and not a guarantee, and no part of this has been measured against ChatGPT Pro. The complete list is the final
section of [docs/v1-design.md](docs/v1-design.md).

## P0 probe

The sections below describe the unchanged P0 measurement fixture. It is **P0**, not the v1 product, and it
responds to the independent CHANGES_REQUESTED review of PR #1. It measures whether the selected Chat Pro model
can actually use tools and observe or control a disposable Windows Notepad.

### Build and run

Install the .NET 10 SDK. From the repository root:

```powershell
dotnet restore src/Codexish.P0/Codexish.P0.csproj
dotnet run --project src/Codexish.P0 -- --self-test
dotnet run --project src/Codexish.P0
```

The last command creates a new disposable fixture, prints its path, and listens at `http://127.0.0.1:3000/mcp`. It exposes seven tools: `echo`, `read_file`, `write_file`, `run_command`, `screenshot`, `click`, `type_text`. The official SDK generates their JSON Schemas. `--no-instructions` turns off server instructions for an A/B trial.

`echo` lists six fixture files. Each must contain its ordinal integer (case01.txt → 1, etc.). `run_command` accepts only `test`, `sleep`, or `inspect`: a fixed C# child validator, a fixed 60-second child, or operation lookup. This is a tool-loop probe, **not yet an arbitrary source-code repair benchmark**. No caller-supplied shell or JavaScript is executed. Test failure is represented by `test_passed=false` and the real nonzero exit code; a successfully collected process result does not mean the test passed.

Writes require the hash returned by `read_file`. They use one exclusive handle and retain byte encoding, BOM, and homogeneous LF/CRLF. Old bytes are saved under the sibling `.state` directory before a **non-atomic in-place** write. The SQLite invocation ledger prevents replay of completed effects and marks unfinished operations unknown after restart. Resource semaphores provide mutual exclusion, not acceptance-order FIFO; wait for each operation to complete before a dependent action. `wait_ms` is only a response wait, not a process deadline. Use `run_command(command="inspect", operation_id=...)` to query pending effects.

### Actual Windows desktop probe

Use a test desktop with no private windows or credentials visible: screenshot captures the **entire primary monitor**. Choose a Notepad process in the current session. **Ctrl+N must open a new unsaved tab**; the installed Notepad can restore an existing file on startup. Never type into a restored document. Put the window and any save dialog completely inside the primary monitor, not merely overlapping it.

**Operate ChatGPT and its write confirmations from another device** (phone, tablet, or other PC). Using ChatGPT on the controlled desktop takes foreground focus away from Notepad; none of P0's seven tools can restore it. After starting the server and tunnel, the last local setup action is to bring the new Notepad tab to the foreground. During a GUI run, do not touch the PC's mouse or keyboard. The last-input check invalidates observations after local input. Keep host confirmation behavior unchanged.

For one freshly created trial, record the fixture path and stop the initial server. Resume that same trial for GUI setup only:

```powershell
dotnet run --project src/Codexish.P0 -- --resume "C:\path\printed-for-this-new-trial" --notepad-pid 1234 --disposable-desktop
```

Never resume a previous reference/Pro/Thinking/A/B trial. `--disposable-desktop` acknowledges the test scope; it creates no sandbox. Input checks select the configured Notepad PID. No administrator elevation, clipboard access, or synthetic screen fallback is used.

Take a screenshot before and after each action. `type_text` accepts exactly one of literal `text` or `key` (`CTRL+S`, `CTRL+A`, `ENTER`, `ESC`). If an action is pending, inspect its operation until complete before the next action. A still-unchanged frame calls for another observation, not another Ctrl+S. Use echo's `gui_save_path` and verify with `read_file("gui-result.txt")`; this file is not writable through `write_file`. UIA, focus recovery, other monitors and mixed-DPI acceptance testing remain v1 work.

### F-1: tunnel Host and Origin configuration

The listener stays on `127.0.0.1`. `--allow-host` adds one exact hostname (no scheme, port, wildcard, or suffix match); repeat it for multiple names. Loopback hosts remain allowed. `--allow-origin` separately adds an exact HTTP(S) origin, including a nondefault port when applicable; repeat it as needed. Requests without Origin do not need an origin allowance. A host allowance never disables Origin checks.

```powershell
dotnet run --project src/Codexish.P0 -c Release -- --resume "C:\path\printed-by-probe" --notepad-pid 1234 --disposable-desktop --port 3000 --allow-host example.trycloudflare.com
```

On 403, stdout now prints `rejected host="..." origin="..." reason=...`. Check the value against the intended client before adding `--allow-origin https://expected-client.example`. Never copy arbitrary rejected headers into an allowlist automatically. Header values are escaped in logs; no authorization headers are logged.

When preserving local Host instead of using `--allow-host`, these are the review's Host-header rewrite commands:

```powershell
cloudflared tunnel --url http://127.0.0.1:3000 --http-host-header 127.0.0.1:3000
ngrok http 3000 --host-header=localhost:3000
```

Choose one tunnel, not both. These commands only fix Host forwarding; they do not add authentication. Host/Origin checks are not access control for remote clients. Use a private or authenticated tunnel for desktop access. This change neither provisions a tunnel nor changes account credentials. Secure MCP Tunnel organization access, Windows client operation and fees remain unmeasured.

### F-2: scaled screenshots and click units

`screenshot(max_width=1280)` is the default. `max_width=0` returns native resolution; larger values never upscale. Negative values return `INVALID_ARGUMENT`. For a 2560×1440 primary monitor, the default PNG is 1280×720 and both scale factors are 2. This is a geometry example, not a measured Chat payload limit.

The returned top-level `width`/`height` remain **physical** dimensions. `image.width`/`image.height` describe the actual PNG; `image_to_desktop.scale_x/scale_y` use physical dimensions divided by the respective rounded image dimensions. `png_bytes` records encoded size.

**Upgrade note:** refresh the connector's tool schema. `click` now requires `coordinate_space` so cached clients cannot silently change coordinate units. Use `"image"` with the screenshot's `observation_id`; the server converts automatically using that observation's scale (floored to an integer). Do not multiply coordinates yourself. Existing physical coordinates are accepted only with the explicit value `"primary_monitor_physical_px"`.

```json
{"x":640,"y":360,"observation_id":"<latest observation>","invocation_id":"<new action ID>","coordinate_space":"image"}
```

Each observation retains its own immutable transform. Capture again after an action. Invalid or stale coordinates produce no click. The same invocation ID with different coordinate units is an idempotency conflict, not a retry.

### Measurement

This procedure adopts the supplied `CODEXish-P0-codex-review-triage.md` §3. H-1 is the other-device requirement, H-2 is fresh state, and H-3 replaces the old M-4 marker score with an image-only nonce. M-1–M-8 are measurement IDs; Codex finding M-1 (persistence) and M-2 (FIFO) are separate IDs.

0. **Reference run first, on its own fresh fixture.** A person or script directs the same seven MCP tools over loopback to finish the identical GUI task, with no manual desktop input after setup and no alternate GUI automation. Record each returned frame, tool result, and saved bytes/hash. In particular verify Save As PID checks and literal full-path entry. Failure here is harness/environment evidence, not evidence that Pro cannot do the task. Use a separate device or prearranged script so initiating the run does not steal focus.
1. **Fresh state for every task/model/instructions trial.** Start without `--resume`, record the newly printed fixture path (also its sibling `.state` directory), and assign a new trial label and invocation IDs. Never reuse a completed or reference trial. On that fixture record `run_command test` with a real failing exit code and `read_file("gui-result.txt")` returning `NOT_FOUND`; distinguish preflight calls from measured calls. For GUI, create a new Ctrl+N Notepad tab and manually type a new random 6–8-character nonce without saving. Do not put this nonce in the model prompt, fixture files, echo message, or other text accessible to the model. The initial screenshot must be its only source. Record the expected nonce separately for scoring. Stop/restart with `--resume` only to finish configuration of this **same, not-yet-measured** fresh trial, adding its Notepad PID and desktop flag. This setup exception is not permission to reuse state between trials.
2. Start one controlled tunnel using the F-1 options above. Inspect Host/Origin rejection logs and add only the intended exact values as needed; lack of the custom log does not prove an Origin problem. Finish server/tunnel setup, then put the new unsaved tab fully on the primary monitor and in the foreground. No local input from this point through the GUI run.
3. Use ChatGPT on another device, in a new conversation with the exact intended Pro label and connector. Perform the existing write confirmations there. Where offered, select remembering the choice for this conversation and record whether it actually persists.
4. Use the task prompts below, without adding the nonce. Each code or GUI task is its own measured trial. Follow pending operations to completion and reobserve after actions. After Enter in Save As, transient `NOT_FOUND`/`FILE_LOCKED` can mean save processing is unfinished: reobserve and reread, not blindly repeat the save. Record final text, byte count, BOM/encoding, and raw-byte hash; equal decoded text is not proof of equal bytes.
5. Repeat with Thinking and with `--no-instructions`, restarting at step 1 each time. Refresh the connector's schema/instructions between A/B configurations. Hold any Project/custom instructions constant or record them as a separate variable; they must not silently reintroduce the server instructions in the off trial.
6. Record the table below. Keep setup/reference calls outside the measured call count and preserve their logs separately. A successful short run is not hours-long reliability evidence.
7. Stop the tunnel after the trial and preserve that trial's fixture, sibling `.state`, frames, results, prompts and timing record together, separate from every other trial.

**Code task (fixed-fixture tool loop, not general source repair):**

> workspace의 테스트가 실패한다. 통과할 때까지 고쳐라. 중간에 묻지 말고 끝까지 진행하라.

**GUI task (no example or expected nonce in this prompt):**

> 먼저 screenshot을 찍고 메모장 본문에 보이는 코드를 그대로 보고하라. 그 다음 Ctrl+A 후 `CODEXISH-P0-OK`를 입력하고 CTRL+S로 echo가 알려준 경로에 저장하라. 대화상자도 화면을 보고 처리하라. 저장 후 read_file로 확인하라.

| ID | Record |
| --- | --- |
| M-1 | Exact displayed model, tool visibility and actual successful call; screenshot evidence. Diagnose transport 403 separately from model support. |
| M-2 | Calls in one user input: total **and screenshot / action / file-command / other** counts; task outcome and final/stop message. The ordinary path is roughly 17 calls in the review; that includes observation overhead, not a target or cap. |
| M-3 | Write confirmations, whether remembering works, and confirmation-wait intervals measured on the Chat device. |
| M-4 | Correct report of the unpredictable **initial image-only nonce, before replacement or file verification**. Mentioning the supplied `CODEXISH-P0-OK` is not image-reception evidence. |
| M-5 | `echo(delay_60_seconds=true)` observed duration/timeout. Success proves survival through 60 seconds only, not the ceiling. `run_command sleep` instead checks child lifetime past response wait. The reviewer proposes 120/180-second variants after success; this unchanged P0 API has no such echo variant, so record them as not run unless a separately identified variation is actually tested. |
| M-6 | Matched Pro/Thinking trial M-2 values and total wall time, with instructions settings and fresh-state evidence. |
| M-7 | Observe/action task completion, action count, Save As behavior, final file text **and** bytes/encoding/hash. |
| M-8 | Define a step as an action plus its follow-up observation; record mean/max wall time, tool durations and inter-call gaps separately. `.state/calls.jsonl` has server-side call metadata; gaps can include model reasoning, network and confirmation wait. Use the Chat-side record to separate confirmation time rather than attributing the whole gap to Pro reasoning. |

A 15-call exercise is a target workload, not a guaranteed minimum or a success metric by itself. Successful early completion is not failure. CI and the reference run do not establish Pro/Thinking image ingestion, confirmations, or continuation behavior.

See [review response](docs/review-response.md), [pre-measurement fixes and v1 deferrals](docs/p0-premeasurement-fixes.md), and [actual status](IMPLEMENTATION_STATUS.md).
