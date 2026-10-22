# CODEXish v1 slice plan

v1 is delivered in five slices. Each one ends with a build that has zero warnings, a self-test that passes on
Windows, and a documented list of what it still does not do. A slice is not started before the previous one is
green, and no slice is marked done on the strength of source existing.

| Slice | Content | State |
| --- | --- | --- |
| 1 | Coding core | Implemented; see `docs/v1-design.md` |
| 2 | `computer_*` desktop control | Not started |
| 3 | External browser MCP mount | Not started |
| 4 | Tray application | Not started |
| 5 | Remaining deferrals | Not started |

## Slice 1 — coding core (this slice)

The 21 tools listed in `docs/v1-design.md`: capabilities and workspace, the `fs_*` family including
`fs_apply_patch`, `shell_run` and the `process_*` family under a Windows Job Object, read-only `git_*`, the
artifact store with cursors, the invocation ledger with per-resource FIFO chains and persist-failure handling,
`operation_inspect`/`operation_cancel`, `session_checkpoint`, the built-in OAuth authorization server, and the
loopback `/control` API.

Deliberately excluded: any desktop capture or input tool, any browser tool, any LSP tool, git write tools,
`fs_mkdir`/`fs_move`/`fs_delete`, an approval UI, and clipboard access.

## Slice 2 — `computer_*`

Promote the P0 desktop code into the server and finish the gaps the P0 review recorded.

- `computer_observe`: screenshot plus window list, focus, cursor and a UIA summary in one observation, with the
  F-2 geometry (default 1280 px wide, `max_width=0` for native, per-observation immutable transform, explicit
  `coordinate_space`).
- `focus_window`: the H-1 gap. P0 had no way to bring its target back to the foreground, which is why the P0
  measurement has to be driven from a second device.
- `computer_query_ui`: UIA element search by role, name and text, bound to an observation, with cursor paging;
  the focused control and the active tab title become part of the observation metadata (P0 verification §6b).
- `computer_act`: click, double click, right click, drag, move, scroll, type text, key press and key combination,
  each validated against the observation immediately before delivery, with the observation after the action in
  the same result.
- Key release on partial input: Codex finding L-2. After a partial `SendInput`, release the key-downs that were
  actually delivered, and never replay the action.
- Multi-monitor and mixed DPI acceptance testing, which P0 declared out of scope.

## Slice 3 — external browser MCP mount

Mount an existing browser MCP server rather than reimplementing CDP. The server proxies its tools under the
same envelope, the same grants and the same ledger, and `host_capabilities` reports which browser backend is
mounted and what it is allowed to touch. A dedicated browser profile is the default; attaching to the user's
own profile is an explicit opt-in, and the debugging endpoint is never exposed beyond loopback.

## Slice 4 — tray application

A Windows tray app that owns the lifecycle the CLI currently leaves to the user: start and stop the server,
start and stop the tunnel, show the current roots and grants, show live processes and queued work, expose the
`/control` actions (pause, resume, kill children, revoke tokens) as menu items, and surface the rejection log so
a failed connector attempt is visible without reading stdout. Grants stay file-backed; the tray edits the file,
it does not invent a second policy store.

## Slice 5 — remaining deferrals

- `fs_mkdir`, `fs_move`, `fs_delete` with explicit scope and expected state.
- `workspace_list_worktrees`, `workspace_create_worktree`, `workspace_remove_worktree`.
- `session_journal` as a cursor-paged view over the `events` table.
- LSP diagnostics and symbols, once the execution boundary for language servers is decided.
- The P0 C-1 to C-7 follow-ups that are not already covered by slice 1.
- Closing the Job Object assignment race with `CREATE_SUSPENDED`, if measurement shows it matters.
- A second identity model (per-device keys or mTLS) if single-user password plus OAuth proves insufficient.

## Ordering rationale

The coding core comes first because it is what a Chat model needs to do useful work at all, and because it is
the part that can be verified without a human at the keyboard: files, processes, git and the protocol are all
testable in CI. Desktop control comes second because it needs an interactive Windows session and a human to
watch it, so it cannot gate the parts that do not. The browser mount is third because it depends on choosing a
backend, the tray is fourth because it is a lifecycle convenience over an already working server, and the
remaining deferrals are last because each is independently useful but none of them blocks a full coding loop.
