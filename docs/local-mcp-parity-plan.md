# Local MCP parity plan

Updated 2026-10-30. Branch `claude/stoic-ptolemy-t6updn`. Status: plan only; nothing below is implemented or measured.

## Goal and user decisions

The goal is that a ChatGPT conversation connected to CODEXish works in the same environment as an agent running locally on the same PC (Claude Code, Codex CLI): the same local MCP servers, started the same way, with the same environment, and the same information reaching the model.

| Decision | Source |
| --- | --- |
| Parity with a locally running agent is the goal. | User, 2026-10-29: "기본적으론 로컬에서 실행되는 에이전트랑 동일한 환경을 구현하는게 목표야" |
| Configuration mirror scope B: servers in `codexish.json` (in the `mcpServers` shape other agents use) **and** each root's `.mcp.json`. User-level files of other agents (`~/.codex/config.toml`, `~/.claude.json`) are not read. | User, 2026-10-30: "B로 하자." |
| A root's `.mcp.json` is trusted exactly when that root has the shell grant. No approval prompt is added: the shell grant already lets any command run in that root. | Proposed with B and accepted with it. |
| Environment: automatic inheritance stays an allowlist; what the user names explicitly (`env` values, `env_vars` names) is passed as written, whatever the name looks like. | Follows from the parity goal: Codex passes `env` and `env_vars`, Claude Code expands `${VAR}`; neither forwards the whole environment by default. |

Everything that already exists for `browser_mounts` keeps working unchanged unless a section below says otherwise: tool names `browser_<id>_<tool>`, the `BROWSER_*` error codes, manifests under `state_dir/browser-profiles`, profile handling and the root grant rules.

## 1. Configuration

### 1.1 `codexish.json`

A new top-level object `mcp_servers` maps a server name to a definition. The same object is also read from the key `mcpServers`, so a block copied from another agent's configuration can be pasted unchanged. When a name appears under both keys, `mcp_servers` wins and a warning names the duplicate. JSON `null` for the object, a definition or any field means its default, as elsewhere in the configuration.

Fields of a definition (all optional unless stated):

| Field | Meaning |
| --- | --- |
| `type` | `"stdio"`, `"http"`, `"streamable-http"` or `"sse"`. Any other value, or none, is inferred: a `url` makes it HTTP, otherwise `command` makes it stdio. |
| `command`, `args` | stdio: the executable and its arguments. |
| `env` | stdio: object of variable name to value, set in the child as written after expansion (§2). |
| `env_vars` | stdio: array of variable names whose values are taken from the current user environment (§3). |
| `cwd` | stdio working directory. Relative paths resolve against the bound root, or against the folder of `codexish.json` when no root is bound. Default: the bound root, else the user profile folder. |
| `url`, `headers` | HTTP: the endpoint and extra request headers (values expanded, §2). |
| `root_id` | Binds the server to a root (§1.4). Optional in `codexish.json`. |
| `read_only_tools` | Backend tool names forwarded without `invocation_id`, as for browser mounts. |
| `disabled: true` or `enabled: false` | The server is listed with state `disabled` and not started. |

Unknown fields are ignored (other agents' fields such as `startup_timeout_sec`, `tool_timeout_sec`, `enabled_tools` included; there are no CODEXish timeouts to map them to). `env`/`env_vars` on an HTTP server and `url`/`headers` on a stdio server are ignored with a warning naming the field.

A definition that cannot be used (neither `command` nor `url`, a `url` that is not an absolute http(s) URI, a `root_id` that names no configured root, a non-string array entry) is listed with state `invalid_config` and its reason, and nothing else is affected. A configuration problem never stops the server.

### 1.2 A root's `.mcp.json`

For every configured root, the file `<root>/.mcp.json` is read when it exists. Its `mcpServers` object uses the same definition fields; each server is bound to that root (`root_id` in the file is ignored). Servers from this file start only while the root has the shell grant; otherwise each is listed with state `needs_shell_grant` and nothing is started. A file that is absent is not a problem; a file that cannot be read or parsed is reported with its error in `host_capabilities` and its servers are not started.

### 1.3 Identity, precedence and tool names

- Identity: a `codexish.json` server is `<name>`; a project server is `<root_id>/<name>`. Identities are compared without case, as root and mount ids are.
- A `codexish.json` server with `root_id: R` and name `N` overrides `N` from `R`'s `.mcp.json`: the project entry is listed with state `overridden` and not started. A `codexish.json` server without `root_id` overrides nothing.
- Tool names: `mcp_<name>_<tool>` for `codexish.json` servers and `mcp_<root_id>_<name>_<tool>` for project servers. Characters outside `[A-Za-z0-9_-]` become `_`. When anything was replaced or the result exceeds 64 characters, the name is cut and given a 10-hex-digit SHA-256 suffix computed over the identity, a newline and the backend tool name, so different servers and different tools never share a name and the name is stable across restarts. Browser mount names keep their current function unchanged.
- Name collisions are contained to the single tool: a mounted tool whose name is already taken (a native tool, another mount's tool, or an earlier tool of the same mount) is not published, the mount stays connected, and a warning names the tool, its server and the owner of the name. This also replaces the current browser-mount behaviour of failing the whole mount on a collision.

### 1.4 Root binding and grants

A server bound to a root (project servers always, `codexish.json` servers with `root_id`) follows the browser-mount grant rules: starting it and calling a read-only tool need read and shell grants on the root; other tools also need write. Grants are read on every call. A `codexish.json` server without `root_id` has no grant check; its entry in the file is the user's permission.

## 2. Variable expansion

`${NAME}` and `${NAME:-default}` are expanded in `command`, each `args` entry, `env` values, `cwd`, `url` and `headers` values, the way Claude Code expands `.mcp.json`. The default text is expanded with the same rules, so nested defaults work. Values come from the current user environment (§3). For project servers `CLAUDE_PROJECT_DIR` resolves to the root path when the environment does not define it. A `$` that does not start `${...}` is left unchanged.

An undefined variable without a default expands to the empty string, the server is still started, and a warning names the variable and the field. The real start or call failure, if any, then shows the consequence; expansion never refuses a server.

## 3. Child environment

### 3.1 Current user environment (all children)

CODEXish starts at sign-in and keeps running, so its own environment is a snapshot from that moment; a tool installed later (its PATH entry) or a variable set later (for example `UNITY_PORT` written by a Unity window) is invisible to its children, unlike to a newly started local agent. To match a fresh agent, every child start builds the current user environment:

- Windows: `CreateEnvironmentBlock` (userenv.dll) for the current process token with `bInherit = FALSE` gives the environment Windows would give a newly started program (system, user and volatile variables, PATH merged). The result is the CODEXish process environment with every variable from that block applied on top (names compared without case), so variables only CODEXish's own process has are kept.
- If the call fails, the CODEXish process environment is used unchanged and one `fresh_environment_unavailable` event records the error; nothing is refused.
- Other platforms: the process environment.

This applies to `shell_run` and `process_start` children (their start environment, which today is the inherited process environment), to stdio mount children (browser and MCP), and to the values used for expansion (§2). Secret-named values found in the new block are added to the redaction set (never removed), so output that echoes them is still redacted.

### 3.2 stdio mount children

The environment of a stdio mount child is, in order:

1. The allowlist used today: the names returned by the SDK's `StdioClientTransportOptions.GetDefaultEnvironmentVariables()` plus CODEXish's current extra names, with their values taken from the current user environment (§3.1) where present, otherwise from the process. Secret-named variables are still never inherited automatically.
2. Every name in `env_vars`, with its value from the current user environment. A name that is not defined is skipped with a warning.
3. Every entry of `env`, after expansion, set as written. An empty value removes the variable.

Steps 2 and 3 are explicit user configuration and are passed whatever the name looks like. Values set through steps 2 and 3 whose names match the redaction name pattern are added to the redaction set. `host_capabilities` lists the names a child received in steps 2 and 3, never values.

### 3.3 Windows command resolution (stdio mounts, browser and MCP)

Published configurations mostly run `npx` or `uvx`. On Windows a `.cmd` shim cannot be started directly, so the command is resolved first:

- A `command` without a directory part is searched in the child's PATH (§3.2), trying the name as given when it has an extension, otherwise each PATHEXT extension of the child environment (default `.COM;.EXE;.BAT;.CMD`) in order. A `command` with a directory part but no extension that does not exist as given is tried with each PATHEXT extension. Nothing found: the command is started as given, and the start error becomes the retry reason.
- A resolved `.cmd` or `.bat` file is started through the child environment's `ComSpec` (fallback: `cmd.exe` in the system directory) as `/d /s /c "<quoted file> <quoted args>"`, each part quoted by the CommandLineToArgvW rules. The kill-on-close job covers cmd and its children. The documentation states that cmd expands `%NAME%` inside arguments started this way.
- Other platforms are unchanged.

## 4. HTTP servers

An HTTP server is connected with the SDK's `HttpClientTransport`: `Endpoint` the expanded `url`, `TransportMode` `Sse` for `type: "sse"` and `AutoDetect` otherwise (Streamable HTTP first, then SSE), `AdditionalHeaders` the expanded `headers`. `ConnectionTimeout` is set to `Timeout.InfiniteTimeSpan`, because stdio mounts have no handshake deadline either; if the SDK rejects that value, the largest value it accepts is used and the code says why. The SDK's own HTTP client and system proxy behaviour are used unchanged. OAuth for remote servers is not part of this work; ChatGPT's own connectors serve remote services.

An HTTP server is supervised like a stdio one: it connects in the background after the host listens, and when the session ends (`McpClient.Completion` completes) or a call fails at the transport level, the session is disposed and reconnected after the same backoff (1 s doubling to 60 s, never giving up, five healthy minutes reset it). There is no process, job or stderr tail; the last error is the state's reason.

## 5. Supervision, tool lists and manifests

- Every server (browser and MCP, stdio and HTTP) registers a handler for `notifications/tools/list_changed`. A notification re-lists the tools; notifications that arrive during a re-list cause exactly one more re-list after it. A successful re-list replaces the published tools (with the per-tool collision rule), saves the manifest and records an `mcp_tools_changed` event naming added and removed tools. A failed re-list keeps the previous list with a warning; the next notification or reconnect tries again. Tools are also re-listed after every reconnect, as today.
- The client advertises the roots capability and answers `roots/list` with the bound root as one `file://` URI, or an empty list for an unbound server. No sampling or elicitation capability is advertised.
- MCP server manifests are saved as `<state_dir>/mcp-mounts/<safe identity>-<hash>.manifest.json` with the tool list, server version and server instructions; they are loaded at start and listed while the server is down, exactly as browser manifests are. Manifests of removed servers are left on disk and simply not loaded; nothing is deleted.

## 6. Calls

Mounted MCP tools behave like mounted browser tools: inputs inside `arguments`, `invocation_id` and the ledger for every tool not in `read_only_tools`, a per-server FIFO (`mcp:<identity>`), `wait_ms` as a response wait only, results with backend text, structured content and non-binary blocks redacted and image/audio passed through. Error codes for MCP servers are `MCP_UNAVAILABLE` (nothing was sent: the server is starting, retrying, disabled or needs a grant; with state, last error and next retry) and `MCP_BACKEND_ERROR` (the backend returned a tool or protocol error). `EXECUTION_UNKNOWN`, `CANCELLED` and `INVALID_ARGUMENT` keep their current meaning. There is no call deadline.

## 7. Resources

Two native, read-only tools give the model what local agents expose as resource tools; they cover browser mounts and MCP servers alike:

- `mcp_resource_list` with optional `server` (an MCP identity or a browser mount id): for each selected connected server, its resources (uri, name, title, description, mimeType, size) and resource templates (uriTemplate, name, description, mimeType), all pages. A server that is down, has no resources capability or fails is reported in its own entry with the reason; the call itself succeeds.
- `mcp_resource_read` with `server` and `uri`: the contents of that resource. Text contents are redacted; blob contents pass through unchanged as embedded resource blocks. Unknown server → `NOT_FOUND`; server down → `MCP_UNAVAILABLE`; backend error → `MCP_BACKEND_ERROR`.

A root-bound server needs read and shell grants for both. With these the native tool count becomes 26, and every place that states 24 is updated. MCP prompts are not exposed: local agents offer them as user-typed slash commands, which have no counterpart here.

## 8. Server instructions

The instructions a server returns at initialization (`McpClient.ServerInstructions`) are kept per server (redacted), saved in its manifest and shown in `host_capabilities`. CODEXish's own server instructions are composed per request through `HttpServerTransportOptions.ConfigureSessionOptions` (invoked on every request in stateless mode): the existing text followed by a section with each mounted server's instructions under its id. `--no-instructions` still turns all of it off.

## 9. Project instruction files

`workspace_info` lists, per root, which of `AGENTS.override.md`, `AGENTS.md`, `CLAUDE.md` and `CLAUDE.local.md` exist directly in the root, with relative path and size. A root whose folder cannot be read lists none. The server instructions gain one sentence telling the model to read the files listed for a root before changing it, and the AGENTS.md or CLAUDE.md files in folders it works in. Contents are not inlined; `fs_read` reads them.

## 10. Reload without restart

Local agents read MCP configuration when a session starts; CODEXish runs for days, so it follows the files instead:

- Every 3 seconds the length and last-write time of `codexish.json` and of each root's `.mcp.json` are compared with the last values seen. On a change the file is parsed without any recovery step (no `.bak` restore, no rename, nothing written). If it cannot be parsed, the running servers stay as they are and a warning names the file and the error; the next change is tried again.
- A parsed change is applied by identity: removed servers stop and their tools disappear; added servers start; servers whose definition changed (compared as canonical JSON of the definition before expansion) restart; unchanged servers are not touched. `browser_mounts` reload the same way.
- Other settings (roots, grants, port, OAuth) still take effect at the next start, as today. A change of an environment variable alone does not restart a server; saving its file does.

## 11. Reporting

`host_capabilities` gains an `mcp` section like `browser`: per server its identity, source (`codexish.json` or `<root_id>/.mcp.json`), root, transport, a display of the command or URL (URL user information removed), state (`not_started`, `starting`, `connected`, `retrying`, `invalid_config`, `disabled`, `needs_shell_grant`, `overridden`, `stopped`, `exited_unknown`), error, warnings, attempts, next retry, connection time, server version, instructions, tools and their source, the names passed through `env`/`env_vars`, and the stderr tail while not connected; plus the state of every root's `.mcp.json`, the environment source (`current_user_environment` or `process_environment` with its error) and the reload rule. The `tools` list includes the mounted MCP names. The tray's start log line and `Summary()` include MCP servers.

## 12. Not part of this work

- One backend instance per ChatGPT conversation. Local agents start a server per session; CODEXish shares one across conversations, which suits a single Unity editor and can mix the state of servers that keep per-session state. Stateless HTTP gives no conversation identity to separate them.
- MCP prompts, sampling and elicitation; OAuth for remote HTTP servers; Codex TOML project files; user-level configuration of other agents; a tray editor for servers.
- Honouring a backend's `readOnlyHint` instead of `read_only_tools` (unchanged decision: only local configuration makes a tool read-only).

## 13. Build for Abandonment checklist

- No new deadline, cap, latch or refusal: no handshake or call timeout (HTTP connection timeout infinite), backoff never gives up, invalid entries and failed files are contained to themselves, undefined variables expand to empty, collisions skip one tool.
- Unknown fields, unknown `type` values and future definition fields are tolerated; nothing pins an SDK, agent or protocol version.
- The reload path never writes, renames or deletes configuration files.
- Windows interfaces used are long-standing (`CreateEnvironmentBlock`, PATHEXT, `ComSpec`), each with a fallback that keeps today's behaviour.

## 14. Tests

All new checks run inside the existing `--browser-tests` and `--self-test` entries, so the CI workflows need no change. Fixtures are this executable in fixture modes, as today. Every existing check keeps passing; checks that count native tools are updated from 24 to 26, and no check is removed or weakened.

1. Configuration: `mcp_servers` and `mcpServers` both read, duplicate warning; stdio/HTTP inference; `disabled`/`enabled:false`; unknown fields ignored; JSON `null`; an invalid entry is `invalid_config` while the next one connects; ignored-field warnings.
2. Expansion: `${A}`, `${A:-d}`, nested default, literal `$`, undefined → empty with warning, in every listed field; `CLAUDE_PROJECT_DIR` for a project server.
3. stdio environment (fixture `process` tool reports names; a new fixture tool reports the values of names the test asks for): allowlist present; `env_vars` value delivered; a secret-named variable delivered when named and absent when not; an `env` value delivered; a secret value echoed by the fixture is redacted in the result.
4. Current user environment: the merge function with an injected block (block wins, process-only names kept, names compared without case on Windows); on Windows the real `CreateEnvironmentBlock` path returns a PATH; `shell_run` still sees a variable set only in the test process. No registry writes.
5. Windows command resolution (Windows only, Linux prints SKIP with the reason): a `.cmd` shim in a temporary folder on the child PATH starts the fixture by bare name; arguments with spaces and quotes arrive intact; resolution by PATHEXT without extension.
6. HTTP: an HTTP fixture mode (Streamable HTTP on 127.0.0.1, port written to a file) is listed and called through the real host; a configured header reaches it; stopping the fixture gives `MCP_UNAVAILABLE` with a next retry; restarting it reconnects without a CODEXish restart.
7. `list_changed`: a fixture tool that adds a tool at run time; the new tool is published and the manifest updated; two quick notifications produce at most two re-lists.
8. Resources: a fixture text resource, blob resource and template; `mcp_resource_list` (all and by server, a down server reported in its entry), `mcp_resource_read` (text redacted, blob intact), unknown server `NOT_FOUND`, down server `MCP_UNAVAILABLE`.
9. Server instructions: fixture instructions appear in `host_capabilities`, in the manifest, and in the instructions of an `initialize` through the real host; absent with `--no-instructions`.
10. Project files: a root with shell grant starts its `.mcp.json` server under `mcp_<root>_<name>_`; a root without shell grant lists `needs_shell_grant` and starts no process; a `codexish.json` override marks the project entry `overridden`; an unreadable file is reported and nothing else changes.
11. Reload: adding a server starts it while an unchanged one keeps its process id; changing one server's args restarts only it; removing one ends its process and unlists its tools; a half-written file keeps the running set and reports the error, and the completed file is then applied.
12. Collisions: a backend tool whose mounted name collides is skipped with a warning and the mount's other tools work (browser and MCP).
13. Instruction files: `workspace_info` lists the files present in a test root and nothing for a missing root.

Acceptance: `--self-test` and `--browser-tests` pass on the CI matrix (ubuntu-latest, windows-latest) with the new counts reported. Live acceptance on the owner's PC (a real Unity MCP over stdio through `uvx`/`npx` and over HTTP, used from ChatGPT) is separate and is recorded in IMPLEMENTATION_STATUS.md only when it has run.

## 15. Documentation

`docs/reference.md` gains a "Local MCP servers" section and its browser-mount section is updated (bare `npx` works, reload, per-tool collisions, current user environment); the README feature list, AGENTS.md (26 native tools and the decisions above) and IMPLEMENTATION_STATUS.md (actual results only) are updated.

## 16. Model-facing text

The exact descriptions of `mcp_resource_list` and `mcp_resource_read`, the mounted MCP tool description template, the server-instructions section header, the instruction-file sentence and the `MCP_UNAVAILABLE`/`MCP_BACKEND_ERROR` messages are fixed in the appendix below and are used verbatim.

## Appendix: model-facing text

(To be filled before implementation.)
