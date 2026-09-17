# CODEXish v1 slice plan

Updated 2026-09-17 after user approval of mainline A / PR #4. Follow [current implementation status](../IMPLEMENTATION_STATUS.md) for actual evidence; source existence or CI alone does not establish real ChatGPT completion.

| Slice | Scope | Current state |
| --- | --- | --- |
| 1 | Coding core, 21 underscore tools including checkpoint, embedded OAuth and local control | Mainline A, PR #4 Ready for review; not merged |
| 2 | computer_observe, computer_query_ui, computer_act | Draft PR #5; recovered implementation and tests, open findings S2-01–03 |
| 3 | Existing browser MCP server mount | Not started |
| 4 | Tray and installation/lifecycle UX | Not started |
| 5 | Remaining independent extensions | Deferred |

## Slice 1

Preserve A's files, shell/process execution and Windows Job supervision, fixed Git reads, artifacts/cursors, SQLite invocation recovery, per-resource FIFO, root grants, OAuth and local control. Git writes stay under shell_run, not new Git write tools. PR #3 remains a reference with selected improvements already ported; its branch is retained.

## Slice 2

Promote the P0 desktop concepts without changing P0 itself. Current implementation includes virtual-desktop physical pixels, window/cursor/focus metadata, scaled/cropped PNG, observation-bound coordinates, selected-window UIA, active tabs, focus recovery, mouse/keyboard/window actions and default action-plus-observation. Partial input cleanup releases only delivered outstanding key-downs. InputTick is metadata.

Actual evidence is 272 local Windows assertions plus six self-owned live fixture checks; exact source CI reports 273 Windows and 264 Linux assertions. Counts differ because of OS/privilege-specific checks. Keep the live fixture separate from fake-backend and HTTP registration tests.

Completion still requires closing the findings in [desktop-slice2.md](desktop-slice2.md), validating re-query/replay behavior, and recording broader actual target-app and physical display tests. Passing one WPF fixture journey does not satisfy Notepad Save As, all actions, multi-monitor/mixed-DPI or Pro/Thinking measurements. Do not mark the slice complete or start advertising later browser/tray work as implemented.

## Slice 3

Mount an existing Playwright/Chrome DevTools MCP rather than implementing CDP. Proxy names, descriptions, permissions and results explicitly, with a dedicated browser profile by default. Report the mounted backend and its boundaries. Personal-profile attachment is an explicit selection, not an implicit fallback.

## Slice 4

Wrap the existing control/lifecycle in a Windows tray: start/stop, roots/grants, live processes and queued work, pause/resume/kill-children/revoke tokens, and visible connection diagnostics. Keep grants file-backed; do not introduce a second permission store or repeated action approval UI.

## Slice 5

Deferred items include scoped fs_mkdir/fs_move/fs_delete, worktree operations, a paged session journal, LSP diagnostics/symbols, remaining process/native robustness work, browser batching and optional tunnel integration. These are not part of the present desktop completion claim. No new identity architecture, privileges or policy limits are approved merely by this list.

## Review and merge

A is the approved development line; P0 precedes A for the user's eventual merge. PR #5 is stacked on A so its desktop diff remains separate. Keep branches and reviewer history. A green build supports review, not automatic merge or fabricated model measurements.
