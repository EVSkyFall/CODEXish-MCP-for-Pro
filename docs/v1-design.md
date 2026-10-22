# CODEXish v1 design decisions

Scope: the decisions that govern `src/Codexish.Server`. Each item is one decision sentence plus the reasoning
that produced it. Everything this design does **not** guarantee is collected in the final section rather than
being scattered as hedges. The earlier documents in this folder stay as the historical proposal; where they
disagree with this file for v1 code, this file wins.

## 1. Shape of the product

**Decision: one C#/.NET 10 process serves MCP over Streamable HTTP on a loopback Kestrel listener, and an
external tunnel is the only thing that makes it reachable.** There is no TypeScript gateway, no separate
broker and no second model in the server; the Chat model does the reasoning and this process provides tools.

**Decision: v1 grows out of the P0 probe by copying its verified patterns rather than by extending the P0
project.** `src/Codexish.P0` stays byte-identical so the pending Pro/Thinking measurement still has its fixture;
the Reply envelope, exclusive-handle write, SQLite ledger, `ProbeAccessPolicy` host/origin rules and the
`GetFinalPathNameByHandle` check were carried over deliberately.

**Decision: MCP tool names use underscores (`fs_read`), and the dotted names in `tool-contracts.md` are
documentation aliases.** A ChatGPT connector function name must match `^[a-zA-Z0-9_-]+$`.

**Decision: slice 1 registers exactly 21 tools** — `host_capabilities`, `workspace_info`, `fs_list`, `fs_read`,
`fs_search`, `fs_stat`, `fs_write`, `fs_apply_patch`, `shell_run`, `process_start`, `process_poll`,
`process_write`, `process_stop`, `git_status`, `git_diff`, `git_log`, `artifact_read`, `artifact_search`,
`operation_inspect`, `operation_cancel`, `session_checkpoint` — and no `computer_*` tool.

## 2. Configuration and grants

**Decision: one JSON file, `%LOCALAPPDATA%\Codexish\codexish.json` by default, created by `--init` with random
secrets and a PBKDF2-SHA256 password hash.** `--config <path>` overrides the location; nothing is read from
environment variables or from any other credential store.

**Decision: `--init` derives `allow_hosts` from the hostname in `--public-url`, always accepts the documented
ChatGPT callback, and accepts repeatable `--redirect-uri` values for a connector whose callback differs.**
When a redirect_uri is refused, the offered value is written to the rejection log so the user can add it.

**Decision: a grant is per root and has three bits — read, write, shell — and a listed root defaults to all
three.** There is no per-action approval UI and no model-writable approval tool: the local user edits the file.

**Decision: two roots may not name the same directory or nest inside one another, and the server refuses to
start otherwise.** FIFO resources are keyed by `root_id`, so two ids over one tree would give the same files
two independent queues and let concurrent writes interleave.

**Decision: the state directory must lie outside every root, and the server refuses to start otherwise.** The
ledger, the artifact store and the `.bak` backups must not be reachable through `fs_*` or through a shell grant
pointed at a root.

**Decision: `host_capabilities` and `workspace_info` publish the roots, the grants, the execution boundary, the
unimplemented features with a reason each, the protocol and schema versions, the latest checkpoint, and the
frame and page limits together with their source.** A limit the model cannot attribute is indistinguishable
from a product restriction.

## 3. Result envelope and errors

**Decision: every tool result is the P0 envelope inside `CallToolResult` — `schema_version` `v1.0`, `status`,
`data`, `error`, `execution_boundary`, plus the optional `output {text, next_cursor, complete, artifact_id}`
block — carried both as `structuredContent` and as text content.**

**Decision: `execution_boundary` is the literal string `unconfined_user` on every result.** The server has no
OS isolation to report and says so on each call rather than once in a document.

**Decision: `BUSY` and `ALREADY_RUNNING` do not exist.** Contention is expressed as `queued` and `running` with
a handle; a valid request is never refused because something else is happening.

**Decision: `side_effects` is one of `none`, `applied`, `partial`, `unknown`, and `retryable` is always false.**
Re-submitting after reading the current state is a new request, not a retry.

## 4. Paths and files

**Decision: a file input is `root_id` plus a relative path; the path is normalized, refused if it is absolute,
UNC, drive-qualified or contains `..`, and then every opened handle is compared with
`GetFinalPathNameByHandle` against the root's own final path.** A path swapped between check and open is caught
by the handle comparison, not by string matching.

**Decision: a reparse point (junction or symlink) between the root and the target is refused, and `fs_list`
reports it as `kind: reparse_point` without traversing it.**

**Decision: `mode=create` proves the fence on the nearest existing parent directory, creates with
`FileMode.CreateNew`, then re-proves the fence on the created file handle and deletes the file if that check
fails.** The target does not exist yet, so there is nothing else to open at check time.

**Decision: `mode=replace` requires `expected_sha256` from `fs_read`, takes one exclusive handle, writes the
previous bytes to the state directory first, then rewrites in place, and reports
`save_mode: exclusive_in_place_non_atomic`.** Encoding, BOM and the file's own homogeneous LF or CRLF endings
are preserved; a file that mixes newline styles is refused rather than silently normalized.

**Decision: `fs_apply_patch` takes a unified diff plus one `expected_sha256` per touched file, runs a full
preflight over every file before touching anything, then applies file by file and reports each file's own
result.** A file the patch creates carries an empty sha256 and must not exist; a file the patch deletes has its
sha256 verified before deletion; a hash is checked again under the exclusive handle at apply time.

**Decision: `fs_apply_patch` has no rollback and claims no whole-patch atomicity.** Preflight is all-or-nothing;
apply is per file, and a partial run returns `side_effects: partial` with each file's outcome.

**Decision: binary files and files above the inline budget are returned as an artifact reference, never as
mangled text.**

## 5. Shell and processes

**Decision: every process result reports the `supervision` mechanism that will end it — `job_object`,
`process_tree_fallback` or `not_supervised_persistent`.** A Job Object that cannot be created is recorded in
the events table and downgrades supervision to `Process.Kill(entireProcessTree)`; it never costs the caller
their command, and it is also the ordinary path on Linux, where there are no Job Objects.

**Decision: a `persistent` process that is still alive after a restart is adopted as a usable handle, not only
as a database row.** It can be polled and stopped, it reports `reattached: true` and
`output_since_restart: not_captured`, its exit code is `exited_unknown_code` because the server no longer owns
the process, and `process_write` is refused because its stdin belongs to nobody.

**Decision: a process cursor is bound to the process that issued it.** A cursor from another process is
`CURSOR_INVALID` rather than an offset into a different pair of streams.

**Decision: `shell_run` and `process_start` are the same supervisor; structured `executable` plus `args` is
preferred and a `command` string requires an explicit shell from `shell.allowed`.** `cmd` is invoked as
`cmd.exe /s /c "<command>"` because cmd does not follow `CommandLineToArgvW` quoting; `pwsh` is invoked as
`pwsh -NoProfile -NonInteractive -Command <command>` through the argument list.

**Decision: `wait_ms` bounds the response only.** When it elapses the call returns `status: running` with a
`process_id`, the child keeps running, and no timeout in this server ever kills anything.

**Decision: a process handle is a GUID bound to the PID and the process start time, and an exited process stays
inspectable.** A recycled PID cannot be mistaken for the original process.

**Decision: `lifetime: session` children are assigned to a Windows Job Object with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` immediately after `Process.Start`, so the whole tree ends when the server
exits or when `process_stop mode=kill_tree` terminates the job; `lifetime: persistent` children get no job and
survive the server.**

**Decision: `mode=graceful` closes stdin and reports the resulting state without forcing anything.** Windows has
no console signal this server can deliver to a child it does not share a console with, so a child that ignores
EOF stays running and the caller is told to use `kill_tree`.

**Decision: stdout and stderr are collected as separate artifacts with independent cursors, and the writer keeps
`FileShare.Read` so `process_poll` and `artifact_read` can follow a live process.**

**Decision: on restart, a `persistent` process row whose PID and start time still match a live process is
re-attached as `running` with `reattached: true` and `output_since_restart: not_captured`; anything else becomes
`exited_unknown_code`.**

## 6. Git

**Decision: `git_status`, `git_diff` and `git_log` run the configured git binary with fixed argument sets and,
on every call, `-c core.hooksPath=<empty directory in the state dir> -c core.fsmonitor=false -c core.pager=cat
-c diff.external= -c core.editor=true --no-optional-locks`, plus `--no-ext-diff --no-textconv` on diff and
`GIT_TERMINAL_PROMPT=0` in the environment.** Reading a repository must not execute the repository's code.

**Decision: before each read, the repository's declared clean and process filters are listed by name with
`git config --null --name-only --get-regexp '^filter\..*\.(clean|process|required)$'` and each one is
disabled with `-c <name>=` (`=false` for `.required`).** A filter declared by `.gitattributes` runs during
status and diff as well, so disabling hooks alone would not be enough. Only names are read, never values.

**Decision: `--no-lazy-fetch` is probed once and used only where git understands it.** It stops a read of a
partial clone from starting a network fetch, which would run the remote and credential helpers; git 2.40
rejects the option, so an unconditional flag would break every call on that version.

**Decision: `ref` and `path` arrive from the model, so a ref must match `^[A-Za-z0-9._/@^~{}-]+$`, must not
start with `-` and must not be a filesystem traversal; `--end-of-options` precedes any ref and `--` precedes any
path; a path is resolved through the same root fence as the file tools.** Rejection happens before the git
process starts.

**Decision: there are no git write tools.** Commits, checkouts and pushes run through `shell_run` under the
root's shell grant, so one execution policy covers every path that runs code.

## 7. Ledger, ordering and recovery

**Decision: the idempotency key is `invocation_id` and the digest is SHA-256 of the tool name and the serialized
arguments; the same id with the same digest joins the live task or returns the stored result, and the same id
with a different digest is `IDEMPOTENCY_CONFLICT`.**

**Decision: acceptance order is the order of the ledger insert under the gate lock, and each resource
(`files:<root>`, `shell:<root>`, `git:<root>`, `process:<id>`) runs its accepted work in exactly that order.**
This closes the P0 contract gap that Codex finding M-2 identified; P0 promised mutual exclusion only.

**Decision: a `shell:<root>` chain item completes when the child has started, not when it exits.** A dev server
started through `process_start` must not block every later command in that root.

**Decision: reads never enter a queue.** `fs_read`, `fs_list`, `fs_search`, `fs_stat`, `artifact_*`,
`operation_inspect`, `workspace_info` and `host_capabilities` take no `invocation_id` and proceed while a
resource is busy.

**Decision: if the final result write fails after the effect completed, the result is kept in memory and
returned as `status: unknown` with `error.code: EXECUTION_UNKNOWN`, `reason: persist_failed`, the result
attached in `data`, and the recovery instruction "inspect effects; do not replay"; a retry with the same id
returns the same thing instead of executing again.** Diagnostic logging is wrapped so a logging failure can
never replace a committed result. This is Codex finding M-1.

**Decision: on restart a `queued` row becomes `cancelled` with `side_effects: none` and a `running` row becomes
`unknown`.** Nothing that was merely queued can have had an effect; anything that was running might have.

**Decision: `operation_cancel` cancels a queued operation outright and, for a running one, records a
cancellation request and reports `side_effects: unknown`.** Cancellation is not rollback.

## 8. Artifacts and large results

**Decision: one SQLite file in the state directory holds `invocations`, `processes`, `artifacts`, `events`,
`tokens` and `checkpoints`; artifact bytes live as files under `state_dir\artifacts`.**

**Decision: an artifact cursor is bound to the artifact's generation, and a cursor from an earlier generation is
`CURSOR_INVALID` rather than an offset into different bytes.**

**Decision: an artifact whose collection ended before the producer finished is marked incomplete, keeps its
preserved prefix readable, and reports `OUTPUT_INCOMPLETE` when a read asks for what was never collected.**

**Decision: page and frame limits are the only limits in this server, and every paginated result carries
`next_cursor` plus the limit's source.** Nothing is dropped silently.

## 9. Authentication, control and redaction

**Decision: `public_url` must be https whenever authentication is enabled, and its origin is added to
`allow_origins` automatically.** The tunnel terminates TLS, so the browser posts the login form with
`Origin: https://<host>` while Kestrel sees scheme http; without the automatic allowance the server's own login
form would be rejected as cross-origin.

**Decision: the only scope is `mcp`.** An empty scope becomes `mcp`, anything else is `invalid_scope` at
`/authorize`, the granted scope is stored on the token row, and `/mcp` refuses a token that does not carry it.

**Decision: refresh rotation is one atomic consume-and-revoke.** Two concurrent exchanges of the same refresh
token cannot both succeed; the loser is `invalid_grant`.

**Decision: replaying a refresh token that was already rotated away revokes every token of that family.** The
family is the authorization the tokens descend from, and the revocation is recorded in the events table.

**Decision: the authorization response carries `iss`** (RFC 9207), on both the success redirect and the error
redirect.

**Decision: authentication is a built-in single-user OAuth 2.1 authorization server on the same listener — no
external IdP — with `S256` PKCE, opaque 32-byte tokens stored only as SHA-256 hashes with an audience and an
expiry, a rotating refresh token, and both `client_secret_post` and `client_secret_basic`.**

**Decision: a request to `/mcp` without a valid bearer token is `401` with
`WWW-Authenticate: Bearer resource_metadata="<public_url>/.well-known/oauth-protected-resource"`, and both
metadata documents are served unauthenticated.**

**Decision: the `/authorize` GET form carries every incoming OAuth parameter into the POST as hidden fields plus
a single-use CSRF nonce, and the POST revalidates all of them.** The issued code stays bound to the caller that
started the flow.

**Decision: PKCE is verified exactly when the authorization request carried a `code_challenge`; a confidential
client that never sent one may omit the verifier, and `pkce_used` is recorded on the token row and in the
events table.**

**Decision: an absent `resource` indicator defaults to `<public_url>/mcp`, and a present one must equal it.**

**Decision: `--no-auth` is accepted only when `allow_hosts` is empty.** The listener is loopback-only and the
access policy always allows loopback hosts, so an empty allow list really does mean nothing but this machine.

**Decision: `/control/pause`, `/resume`, `/kill-children`, `/revoke-tokens` and `/control/status` are accepted
only when the connection's remote address is loopback and the `Host` header is loopback and the request carries
`X-Codexish-Control: <control_token>`.** The control API is not an MCP tool and is not reachable through the
tunnel Host.

**Decision: pause is a hold, not a rejection.** While paused, a mutating invocation is accepted into its queue
and returned as `status: queued` with `paused: true`, reads keep working, and the dequeuers continue on resume.

**Decision: the values of environment variables whose names match TOKEN, SECRET, PASSWORD, API_KEY or
PRIVATE_KEY are replaced wherever they appear in output.** The environment is snapshotted once at startup,
longest value first, and values shorter than eight characters are ignored as too likely to be ordinary words.

**Decision: environment variables are never returned in any result, and stdout/stderr previews, `fs_read` text,
git output and `artifact_read` text are filtered through a small published credential pattern set that replaces
a match with `[REDACTED:<kind>]` and sets `redacted: true`.** Artifacts keep the raw bytes on disk.

## 10. Harness

**Decision: `initialize.instructions` tells the model to work to a verified completion condition, to read before
editing and pass the hash, to run the tests after every edit, to poll handles rather than treat a response wait
as a deadline, to reuse `invocation_id` only for retries, to inspect unknown operations instead of replaying
them, to report actual exit codes and diffs, and to checkpoint after each milestone.**

**Decision: every tool description states when to use the tool, what it returns, which tool to call next, and
what to do for each error code it can produce.**

**Decision: `session_checkpoint` stores an observable goal, completion condition, done and remaining steps and
handles, and `host_capabilities` returns the latest one.** It stores text and grants nothing.

## What this design does not guarantee

- **No sandbox.** `shell_run`, `process_start`, builds, tests and any hook they invoke run with the logged-in
  Windows user's full rights. The `fs_*` fence constrains the structured tools only; it constrains nothing that
  a shell command can reach. Job Objects supervise lifetime, not privilege.
- **No crash atomicity for `mode=replace`.** The in-place write is not atomic; the previous bytes are preserved
  in the state directory and `save_mode` says so on every result.
- **No whole-patch transaction.** `fs_apply_patch` preflights all files and then applies per file. There is no
  rollback, and a partial result names every file.
- **Redaction is best effort.** It is a small published pattern set over text that is about to leave the
  machine. It does not detect secrets by entropy, does not cover screenshots or binary artifacts, and a clean
  result is not evidence that no secret was returned.
- **The Job Object race is not closed.** A child is assigned to the job immediately after `Process.Start`; a
  grandchild spawned inside that window could escape supervision. Closing it would need `CREATE_SUSPENDED`.
- **`graceful` is not a signal.** It closes stdin. A child that ignores EOF keeps running.
- **`persistent` does not mean durable.** It means the child is not killed when this server exits. It does not
  survive a reboot, and output produced while the server was down was captured by nobody.
- **Host and Origin checks are not authentication.** They are routing and browser checks; the bearer token is
  the authentication.
- **A single-user password is the whole identity model.** There are no accounts, no roles, no per-device keys
  and no mTLS in this slice.
- **Exactly-once execution is not promised for shell, process or GUI effects.** The ledger prevents a repeated
  effect for a repeated `invocation_id`; it cannot make an unconfirmed effect confirmable.
- **Adopting a process across a restart is not the same as owning it.** A re-attached persistent process has
  no readable exit code and no stdin, and whatever it printed while the server was down was captured by nobody.
- **A cursor is an ordering position, not a snapshot.** A file created between two `fs_list` or `fs_search`
  pages, sorted before the cursor, does not appear in that listing.
- **Nothing here has been measured against ChatGPT Pro.** The P0 measurement M-1 to M-8 is still unperformed,
  and no tunnel was opened by this work.
