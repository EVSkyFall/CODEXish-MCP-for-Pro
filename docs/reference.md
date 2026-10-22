# CODEXish reference

Everything the [README](../README.md) leaves out: configuration, OAuth details, the control API, shells and Git, the
ledger and retention, browser mounts, the tray, tests and known gaps.

## Install from source

Publish a self-contained build once. The .NET runtime is bundled into the output folder, so running it later needs
no SDK and no separately installed runtime:

```powershell
dotnet publish src/Codexish.Server -c Release -r win-x64 --self-contained true -o "$env:LOCALAPPDATA\Programs\Codexish"
$codexish = "$env:LOCALAPPDATA\Programs\Codexish\Codexish.Server.exe"
```

To update, exit the tray (or stop the server) and publish again into the same folder.

## Configuration

The tray's first-run form does this for you (see [Tray](#tray)). From the command line:

```powershell
& $codexish --init `
  --password "<a password you choose>" `
  --public-url "https://<your-tunnel-host>" `
  --port <port> `
  --root "proj=C:\Projects\Example"
```

`--init` writes `%LOCALAPPDATA%\Codexish\codexish.json` (override with `--config <path>`), generates the client
secret and the loopback control token with `RandomNumberGenerator`, stores the password as a PBKDF2-SHA256 hash,
and puts the hostname from `--public-url` into `allow_hosts` and its origin into `allow_origins` (the tunnel
terminates TLS, so the login form's own POST arrives with an https Origin over an http connection).
`--public-url` must be https unless you run with `--no-auth`, because the access token would otherwise cross
the tunnel in clear text. `--port` is the local listening port (default 3000). Repeat `--root id=path` for more
roots; each root grants read, write and shell unless you edit the file afterwards. Roots may overlap or nest: each
call uses the grants of the `root_id` it names, and changes to files reachable through several roots wait in one
queue. `--state-dir` moves the ledger, artifacts and backups; keep it outside every root, because a root that
contains it exposes them to that root's tools. Any https callback is accepted (see below), so
`--redirect-uri <uri>` (repeatable) is only needed for a connector whose callback is not https.
**`--init` prints the client secret and the control token once. Treat both as passwords.**

Only a `port` outside 0-65535, an unusable `public_url`, an http `public_url` with authentication, `--no-auth`
with a public host, and another server already running on the same `state_dir` still refuse to start. Everything
else that is wrong in the file is skipped or replaced by its
default and reported as a warning in the startup log and in `host_capabilities.warnings`: a root entry with a bad or
duplicate id or no path is ignored; a root whose directory does not exist stays configured with `exists=false`, and
calls on it fail until the directory exists, without a restart; a server with no usable root still serves the
desktop tools; an `allow_hosts` or `allow_origins` entry that is not an exact hostname or origin is ignored; and
`null` for any setting means that setting's default (an explicit empty `shell.allowed`, `[]`, still allows no shell).
Saving (the tray's setup and **Edit roots and grants**) writes a temporary file and swaps it in, and keeps the
previous version as `codexish.json.bak`. If `codexish.json` cannot be parsed, it is kept as
`codexish.json.broken-<utc>` (`-2`, `-3` and so on when that name is taken) and a readable `codexish.json.bak` is
loaded and restored in its place. If another program saved `codexish.json` in the meantime, nothing is overwritten:
a readable new version is loaded instead, and otherwise the `.bak` is loaded without being restored.

## Running without the tray

```powershell
& $codexish
```

The listener is loopback only; the tunnel is what makes it reachable. Host and Origin checks are not
authentication — the bearer token is. `--no-auth` is accepted only when `allow_hosts` is empty, that is, for
loopback development. To have the server start with Windows and restart by itself, use the
[tray](#tray) with **Start with Windows**.

## Tunnel

Use Tailscale Funnel as the persistent tunnel. Its `https://<machine>.<tailnet>.ts.net` address does not change, so
it is the `--public-url` and the connector is registered once:

```powershell
tailscale set --unattended
tailscale funnel --bg <port>
```

`--unattended` keeps Tailscale running while you are signed out, and `--bg` keeps the funnel configured across
restarts. Disable key expiry for this machine in the Tailscale admin console, or it drops off the tailnet when its
key expires. Give CODEXish a dedicated, uncommon local port: anything listening on the funneled port is published to
the internet, including another program that takes the port while CODEXish is not running.

For a trial, a cloudflared quick tunnel needs no account:

```powershell
cloudflared tunnel --url http://127.0.0.1:<port>
```

Its `https://….trycloudflare.com` URL changes on every start, so each time `public_url` and `allow_hosts` in
`codexish.json` must be updated (or `--init` rerun) and the connector registered again.

## OAuth and ChatGPT

1. In ChatGPT on the web, open **Settings → Security and login** and turn on **Developer mode** (some accounts show
   **Create MCP App** without a toggle).
2. Open [chatgpt.com/plugins](https://chatgpt.com/plugins), choose **Add**, then **Create MCP App**. Enter a name, a
   description, **Server URL** `<public_url>/mcp` and **Authentication** OAuth, confirm **I understand and want to
   continue**, and choose **Create**. (**Create plugin** and **Upload plugin archive** are for packaged plugins and may
   only work in the desktop app; they are not needed.)
3. ChatGPT registers itself with this server through Dynamic Client Registration (RFC 7591) and opens the CODEXish
   sign-in page. Check the host it names, then sign in with your CODEXish password, not your OpenAI password.

If a dialog offers manual OAuth client fields instead, enter the static client from `codexish.json`; it keeps working
next to registered clients.

| Field | Value |
| --- | --- |
| MCP URL | `<public_url>/mcp` |
| Registration | `<public_url>/register`, advertised as `registration_endpoint`; client ID metadata documents (CIMD) are not offered |
| Authorization URL | `<public_url>/authorize` |
| Token URL | `<public_url>/token` |
| Manual client ID | `codexish-chatgpt` (from `codexish.json`) |
| Manual client secret | printed by `--init`, stored in `codexish.json`; required on every token request of the static client |
| Scope | any value, or none; the server always grants `mcp`, its only scope |
| Callback | a registered client: exactly one of the `redirect_uris` it registered; the static client: any absolute `https` URI without a fragment, and one that is not https must be listed in `oauth.redirect_uris` |
| PKCE | S256; required for a registered public client, and whenever a client sends a `code_challenge` |
| Refresh | the refresh token is kept, not rotated, and does not expire (`oauth.refresh_token_days` 0); access tokens last `oauth.access_token_hours` (12) |

`POST /register` takes `redirect_uris` (absolute https without a fragment, or http on `localhost`, `127.0.0.1` or
`[::1]`), `token_endpoint_auth_method` (`client_secret_basic` when absent, `client_secret_post`, or `none` for a
public client; any other value is replaced by `client_secret_basic` and the response says so) and `client_name`;
other metadata is ignored. It answers `201` with a `dcr_…` client ID and, unless the client is public, a client
secret that never expires; the server keeps only its hash. Registering needs no token, so anyone who can reach the
URL can register, but a registration alone gets nothing: a code is issued only after your password. Registrations
that never complete a sign-in are anonymous state, so only the newest 1,000 are kept (the oldest unused go first)
and retention removes them after `output_days`; a client that has signed in stays until you remove it.
`/control/status` and the tray status list the registered clients (name, method, creation, last sign-in and callback
hosts, never a secret). **Remove registered clients** in the tray, or `/control/remove-clients`, removes them all and
revokes their tokens; ChatGPT registers again the next time it connects.

Signing in opens a single password form served by this server. For a registered client it shows the name the client
gave itself ("ChatGPT wants to connect to this PC", or "An unnamed client"); anyone can register any name, so the
line that matters is the host the browser returns to, for example "After sign-in you will return to chatgpt.com".
Check it before you type the password. A registered client's callback must be one it registered; any other gets the
local error page. For a callback of the static client outside `oauth.redirect_uris`, errors before the password is
accepted stay on that local page instead of being redirected, and the server logs
`accepted oauth redirect_uri outside configured list host=<host>`. A refused callback is logged as
`rejected oauth stage=authorize reason=redirect_uri_mismatch offered_redirect_uri="..."`. One that is not https can
be added to `oauth.redirect_uris` in `codexish.json`, or passed as `--redirect-uri` when you run `--init`; one with a
fragment is never a valid OAuth callback. The token request must repeat the callback of its sign-in. A `resource`
parameter never causes a refusal: tokens are always issued for `<public_url>/mcp`, and a value on another origin is
logged as `oauth resource differs from public origin value=...`. Every authorization redirect carries `iss`, the
metadata says so (`authorization_response_iss_parameter_supported`), and the protected-resource document is served
at both `/.well-known/oauth-protected-resource` and `/.well-known/oauth-protected-resource/mcp`.

At the token endpoint every client authenticates the way it registered: a confidential registered client with its
`client_id` and its secret (Basic or form), a public one with its `client_id` and the PKCE `code_verifier`. For the
static client a `client_id` that is present must match, and without one its secret (form field or Basic
credentials) identifies it. Codes and tokens belong to the client that obtained them, a refresh must come from that
client, and an unknown or removed client gets `invalid_client`. A confidential client's secret is always required,
so a leaked refresh token is useless on its own, and refresh tokens are therefore kept instead of rotated for every
client: a refresh response lost in the tunnel, or two refreshes racing, no longer disconnect the connector. A
positive `oauth.refresh_token_days` is still honored, counted from the token's last refresh. Configuration files
written before this change contain `"refresh_token_days": 30`; set it to `0` for refresh tokens that never expire.
**Revoke all tokens** in the tray, or `/control/revoke-tokens`, ends every access and refresh token and keeps the
registrations.

## Project custom instructions

Paste this into the Project's custom instructions so the harness text survives a client that does not surface
`initialize.instructions`:

> You are operating the user's Windows PC through these tools. Work until the stated completion condition is met
> and verified; do not stop to ask unless permission is missing or the user cancels. Read before editing and pass
> the returned sha256 to writes. After every edit run the relevant tests or build and fix observed failures. Long
> commands return handles: poll them; a response wait is not a deadline and never kills the process. Reuse
> invocation_id only when retrying the same action; inspect unknown operations, never replay them. Report actual
> exit codes, diffs, and file contents, not inferred success. Call session_checkpoint after each milestone so work
> can resume across turns.

## Local control API

The control API answers only when the connection is loopback, the `Host` header is loopback, and the request
carries the control token. It is never reachable through the tunnel and it is not an MCP tool. The examples use the
default port 3000; use your configured `port`.

```powershell
$t = (Get-Content "$env:LOCALAPPDATA\Codexish\codexish.json" | ConvertFrom-Json).control_token
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:3000/control/pause         -Headers @{ "X-Codexish-Control" = $t }
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:3000/control/resume        -Headers @{ "X-Codexish-Control" = $t }
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:3000/control/kill-children -Headers @{ "X-Codexish-Control" = $t }
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:3000/control/revoke-tokens -Headers @{ "X-Codexish-Control" = $t }
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:3000/control/remove-clients -Headers @{ "X-Codexish-Control" = $t }
Invoke-RestMethod -Method Get  -Uri http://127.0.0.1:3000/control/status        -Headers @{ "X-Codexish-Control" = $t }
```

Pause is a hold, not a refusal: while paused, a write is accepted into its queue and returned as `queued` with
`paused: true`, reads keep working, and the queue drains on resume.

## Shells and Git

A `command` string without `shell` runs in `shell.default`. The supported shells are `pwsh`, `powershell` (Windows
PowerShell 5.1) and `cmd`, and new configurations allow all three; `shell.allowed` stays your own policy, so a shell
that is not in it is refused. `pwsh` is looked up on every call: on `PATH`, then the newest
`%ProgramFiles%\PowerShell\*\pwsh.exe`, then Windows PowerShell. Every result names the `interpreter` that ran the
command. Unsupported names in `shell.allowed` are ignored with a warning, and a `shell.default` that is not allowed
falls back to the first allowed shell.

Git is looked up on every call as well: `git.path` when that file exists, then `git` on `PATH`, then
`%ProgramFiles%\Git\cmd\git.exe`, then `%LOCALAPPDATA%\Programs\Git\cmd\git.exe`, so a moved or upgraded Git keeps
working without editing the file. `host_capabilities` and `workspace_info` report the path in use. Git runs without
any `GIT_*` variable inherited from the server's environment.

## Ledger and retention

The ledger is the SQLite database `codexish.db` in `state_dir`. If SQLite reports it as damaged or not a database at
startup (including `PRAGMA quick_check`), it and its `-wal`, `-shm` and `-journal` files are renamed to
`ledger.corrupt-<utc>.*`, a fresh database is created and `host_capabilities.ledger_rebuilt` says so. **The fresh
ledger has no tokens, so the ChatGPT connector has to sign in again.** Brief lock or I/O errors from other programs
are retried within the normal command timeout, schema upgrades at startup included, and a row that cannot be read
affects only the response that needed it.

CODEXish deletes its own old state, never your roots, files or Git history:

```json
"retention": { "output_days": 30, "backup_days": 90 }
```

About a minute after start and then every 6 hours, artifacts (command output and stored reads), finished ledger
entries, events, exited processes with their output, expired or revoked tokens, older checkpoints, client
registrations that never completed a sign-in and the tray's log files older than `output_days` are deleted; pre-edit backups, quarantined ledgers and `codexish.json.broken-*` copies
older than `backup_days` are deleted. A missing section means these defaults, and `0` keeps that state forever.
Running and reattachable processes, queued or running operations, the newest checkpoint, live tokens and browser
profiles are never deleted, and neither is an exited process whose job still holds a running descendant, nor its
output, until that descendant ends. A failed step is retried at the next sweep; `host_capabilities.retention` shows
the settings and the last sweep.

Only files carrying the names CODEXish gives them are deleted: `art_<id>.bin` in `artifacts`, `<id>.bak` in
`backups`, `tray-<yyyyMMdd>.log` in `logs`, `ledger.corrupt-<utc>.db` (and its sidecars) in `state_dir`, and
`codexish.json.broken-<utc>` next to the configuration. Any other file in those folders, and every directory, is left
alone, even when `state_dir` lies inside a root. An artifact's record goes only once its file is gone, so a file
that cannot be deleted yet is tried again at the next sweep. Reading output that retention removed answers
`ARTIFACT_EXPIRED` or `NOT_FOUND`; run the command again for fresh output.

## Browser mounts

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
  `args` already contain `--user-data-dir`, `--cdp-endpoint`, `--extension`, `--storage-state` or `--config` starts
  with those `args` unchanged and without CODEXish's `--user-data-dir`, and `host_capabilities` shows a warning for
  it. `profile_mode: "existing"` passes `args` unchanged; choose it only when you deliberately point the backend at
  an existing profile, CDP endpoint or extension. `{profile_dir}` in `args` expands to the per-mount directory in
  both modes. `kind: "custom"` mounts another stdio MCP server without profile handling.
- Tools appear as `browser_<id>_<backend tool>`, for example `browser_pw_browser_navigate`. A name that is not
  `^[a-zA-Z0-9_-]+$` or would exceed 64 characters is shortened and given a short hash suffix. Every mounted tool
  takes the backend's own inputs inside `arguments`. Tools listed in `read_only_tools` run directly and bypass the
  ledger because your configuration says so; CODEXish does not check that they are free of side effects. Every other
  tool is treated as a change: it needs an `invocation_id`, runs through the ledger (an identical retry returns the
  stored result), and its `wait_ms` is a response wait only.
- Starting a mount and calling a read-only tool need read and shell grants on `root_id`; other tools also need write.
- The backend receives a small environment: system and user profile paths, `PATH`, `TEMP` and `DOTNET_ROOT`.
  Variables whose names look like credentials are never passed, and proxy variables are not passed either.
- Mounts connect in the background once the server listens, each on its own, so a backend that is slow or never
  answers its handshake delays nothing else. A backend that fails to start or exits is restarted after 1 s, doubling
  to at most 60 s between attempts, without giving up; five healthy minutes after a completed handshake and tool
  listing reset the delay. `host_capabilities`
  reports each mount's `state` (`starting`, `connected`, `retrying`, `invalid_config`, `stopped`), its attempts, last
  error, next retry time and the last lines of its stderr. Only an entry that is invalid in the file itself
  (`invalid_config`) is not retried.
- The tool list a backend last reported is saved as `<state_dir>\browser-profiles\<id>.manifest.json`. While the
  backend is down, and after a restart until it connects, those tools stay listed and a call answers
  `BROWSER_UNAVAILABLE` with the mount's state and the time of its next retry. Mounts are read when the server
  starts.
- Mounted tools run unconfined as you and are not contained by the root or its grants. Tools such as
  `browser_file_upload`, `browser_evaluate` or `browser_run_code_unsafe` can read files or run code anywhere your
  account can; the grants only decide whether CODEXish forwards a call, and the root is the backend's working
  directory, not a filesystem, network or code sandbox.

## Tray

```powershell
& $codexish --tray --public-url "https://<machine>.<tailnet>.ts.net" --port <port> --root "proj=C:\Projects\Example"
& $codexish --tray --config D:\Codexish\codexish.json
```

`--tray` runs the same server behind a notification-area icon. It first starts itself again in the background with
a hidden console and exits as soon as that copy holds the tray (or has found one already running), so the console
window of a shortcut, a double-click or a terminal closes at once and closing a terminal no longer ends CODEXish; if
that relaunch fails, or the copy exits before it takes over, the tray runs in the original process. One tray runs
per configuration file, however its path is spelled (a junction, symbolic link or short name leads to the same
file): a second `--tray` for the same `codexish.json` says "CODEXish is already running; its icon is in the
notification area" and exits.

Without a configuration file the tray first shows a setup form. Its connection section asks for the public https
origin, the local port (3000 unless `--port` says otherwise), a new CODEXish password (with a show/hide button) and
the OAuth callback; its sharing section for one project folder, with **Browse…**. **Start CODEXish when I sign in to
Windows** is checked. `--public-url`, `--port` and `--root` only pre-fill the form; the password is never taken from
the command line. A folder left empty writes no root at all; folders can be added later with **Edit roots and
grants**. The form writes the file like `--init` and shows the connection details once: the MCP endpoint, the client
ID and the masked client secret, each with **Copy**, and any warnings. ChatGPT registers itself (Create MCP App, then
OAuth), so the client ID and secret are only for apps that ask for them. The tray then starts the server and a
configured tunnel. When the file exists but cannot be loaded even from
`codexish.json.bak`, or its `public_url` is http, the icon starts anyway, shows the reason in a balloon and in the
status window, offers **Open configuration folder**, and tries again every minute; once the file loads, it starts
the server and a configured tunnel.

**Start with Windows**, a checkable menu item and the setup checkbox, writes `CODEXish.lnk` into your Startup folder.
It runs this executable with `--tray --start` (plus `--config "<path>"` for a configuration outside the default
location) from the executable's folder, with the console window minimized; unchecking it deletes the shortcut. When
the tray starts and the shortcut names a file that no longer exists, for example after the install folder moved, the
shortcut is rewritten to the running executable. `--start` starts the server once the tray is up, then the tunnel if
`tunnel.command` is configured. A plain `--tray` waits for **Start server**.

The icon shows the state with a badge: green while the server runs, grey while it is stopped, and amber while the
server or the owned tunnel is retrying or the configuration has not loaded. The first row of the menu repeats it, and
double-clicking the icon opens the status. The menu, the windows and their icons follow Windows' light or dark app
mode and the display scale of the screen they open on.

The server and the owned tunnel are supervised. A failed server start (a busy port, for example), a server that
stops without being asked to, and an owned tunnel that exits without being asked to are retried after 1 s, doubling
to at most 60 s between attempts, without ever giving up; five minutes of healthy running reset the delay. **Stop
server and owned tunnel**, **Stop owned tunnel**, **Edit roots and grants** and **Exit** end supervision of what they
stop until you start it again. Starts and stops run one after another, so an automatic start never overtakes a stop
in progress. If stopping the owned tunnel fails, the server stops anyway and the tunnel failure is reported on its
own. The icon's tooltip shows whether each part is running, retrying (with a short cause) or stopped, and every
failure and restart is in the connection log with its cause.

The menu also offers status with roots, processes and registered clients, pause and resume, stopping session
children, revoking all tokens, **Remove registered clients**, the connection log, **Open configuration folder**, and
**Edit roots and grants**: each root with its folder (**Browse…**) and Read, Write and Shell, **Add root** and a
remove button per row; **Save and restart** saves the file and restarts whatever was running, server and owned tunnel, so ChatGPT stays connected. The connection log window shows the latest 5,000 lines; every line, redacted the same way, is also
appended to `<state_dir>\logs\tray-<yyyyMMdd>.log`, which the window names and retention removes after
`output_days`.

**Start configured tunnel** runs `tunnel.command` with `tunnel.args` as a child of the tray; `{port}` becomes the
listening port. Nothing downloads or selects a tunnel. Starting the server alone never starts it, and it is only
started while this server is listening, so it never publishes another program that took the port. The tunnel runs
inside a kill-on-close job object, so it ends with the tray even when the tray is killed. Stopping the server or
exiting the tray stops it:

```json
"tunnel": { "command": "C:\\Tools\\cloudflared.exe", "args": ["tunnel", "--url", "http://127.0.0.1:{port}"] }
```

A Tailscale Funnel set up as in [Tunnel](#tunnel) needs no `tunnel` entry: Tailscale publishes the port by itself.
The tunnel process inherits the tray's environment, so a tunnel tool can read its own settings from environment
variables. Its output appears in the connection log with the control token, client secret and password hash from
`codexish.json` replaced by redaction markers. The tray exists only in the Windows build; elsewhere `--tray` prints a
message and exits with code 2.

## Tests

```powershell
dotnet run --project src/Codexish.Server -c Release -- --self-test
dotnet run --project src/Codexish.Server -c Release -- --browser-tests
dotnet run --project src/Codexish.Server -c Release -- --tray-tests
```

The self-test uses real files, a real SQLite ledger, real child processes, a real booby-trapped Git repository
and a real in-process HTTP listener. It opens no tunnel, sends no desktop input and performs no ChatGPT
measurement. Windows-only checks print `SKIP` elsewhere. `--browser-tests` runs this executable as a stdio MCP
fixture behind the real HTTP host, including fixture modes that exit after a number of calls, never answer the
handshake, or fail their first starts; `--tray-tests` drives the tray controller over the local control endpoint,
including supervision (a busy port, a stopped host, short-lived test tunnels) and, on Windows, the autostart shortcut
in a temporary Startup folder, without an icon or an external tunnel. All three run in CI on Windows and Ubuntu.

Explicit local checks, never run in CI:

- `--browser-live-test <node.exe> <@playwright/mcp cli.js> <chrome.exe>`: a headless browser with a dedicated
  profile against a loopback page served by the test itself.
- `--tray-smoke-test`: creates and removes a notification icon.
- `--design-preview <dir>`: renders the setup, connection details, roots and text windows, the tray menu and the
  tray icons to PNG at 100% and 150%, light and dark, and writes the sign-in and error pages as HTML. Every window is
  created hidden and printed, so nothing appears on screen; it opens no server, port or configuration. Windows only
  for the PNGs.
- `assets/build-icons.ps1` regenerates `assets/generated` (the app and tray icons and the glyph sheets) from the SVG
  sources in `assets/`; the build only embeds its output. Lucide's license is in
  [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).
- `--self-test-desktop` and `--self-test-desktop-http`: control a new self-owned window on the interactive desktop,
  the second through the authenticated HTTP host. Both take the foreground and send real input.

## Limits and known gaps

There is no sandbox: `shell_run`, builds, tests and any Git hook they invoke run with your full Windows rights.
`mode=replace` is not crash-atomic, `fs_apply_patch` has no rollback, redaction is a small published pattern set
and not a guarantee, and no part of this has been measured against ChatGPT Pro. The complete list is the final
section of [docs/v1-design.md](v1-design.md).

Known gaps that are recorded but not addressed yet: a child is placed in its Job Object just after it starts, so a
grandchild spawned in that moment can escape it (starting suspended would close this); Git output and directory
walks are read whole rather than streamed; continuation cursors for `fs_read`, artifacts and search, and binding
each cursor to the identity of what it pages through, are incomplete; stored artifacts are not re-verified against
their hash; backend schemas that use `$dynamicRef` are not rebased; the foreground confirmation window of desktop
actions is unchanged; desktop actions on UAC prompts, the secure desktop and higher-integrity windows are refused;
and OAuth client ID metadata documents (CIMD) are not implemented, so clients register through `/register` or use
the static client.
