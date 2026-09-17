# CODEXish v1 slice plan

Updated 2026-09-17. Mainline A remains the user's selected baseline; no merge is performed by this plan. Current evidence and execution blocks are in [IMPLEMENTATION_STATUS.md](../IMPLEMENTATION_STATUS.md).

| Slice | Scope | State |
| --- | --- | --- |
| 1 | Coding core, 21 tools, OAuth and local control | PR #4 mainline A, Ready for review; unmerged |
| 2 | computer_observe, computer_query_ui, computer_act | PR #5 Draft; S2-01–03 fixed and CI verified; broader live acceptance still incomplete |
| 3 | Existing browser MCP stdio mount | Module draft only; runtime integration blocked before execution, not published |
| 4 | Tray and installation/lifecycle UX | Not implemented |
| 5 | Independent extensions | Deferred; not implemented |

## Current desktop work

Source 05ae9e2 freezes UI page responses and original element references, retains the requested capture maximum, and represents a minimized window-only observation without capturing unrelated pixels. Eleven regressions accompany these corrections. Exact-source CI passes 284 Windows and 275 Linux assertions across coding, desktop core and correction suites.

The latest local full-suite attempts stalled during Git fixture setup and were stopped, not counted as passes. The fresh native fixture passed capture/UIA checks but returned EXECUTION_UNKNOWN on focus; no keyboard/mouse input followed. Diagnose foreground behavior and run authenticated HTTP desktop and target-application acceptance when the corresponding work is allowed. Do not infer these results from CI or earlier WPF successes.

## Browser mount

Use an existing Playwright or Chrome DevTools MCP backend rather than implementing CDP. Preserve its input schema and image content, apply the existing grants and invocation ledger, and report the selected backend and actual boundaries. A dedicated profile is the default; personal-profile attachment requires an explicit selection. The currently preserved draft is not an implemented mount and was not exposed through the server.

## Tray

Wrap existing server/control lifecycle in a Windows tray: server status, roots/grants, live processes/queues, pause/resume/kill-children/revoke tokens and diagnostics. Grants remain file-backed; no second policy store or repetitive approval UI. No tray completion is claimed while it has no executable integration or tests.

## Deferred work and verification

Scoped filesystem management, worktrees, journal paging, LSP and optional tunnel integration remain separate extensions. Actual ChatGPT OAuth, image reception, continuation and the Pro/Thinking measurement table remain unrecorded. Existing user decisions do not create measurement evidence. Keep original P0 and reviewer history, and retain branches for the user's eventual merge sequence.
