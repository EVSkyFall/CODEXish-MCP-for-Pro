# CODEXish v1 slice plan

Updated 2026-09-17. Mainline A remains the user's selected baseline; no merge is performed by this plan. Current evidence and execution blocks are in [IMPLEMENTATION_STATUS.md](../IMPLEMENTATION_STATUS.md).

| Slice | Scope | State |
| --- | --- | --- |
| 1 | Coding core, 21 tools, OAuth and local control | PR #4 mainline A, Ready for review; unmerged |
| 2 | computer_observe, computer_query_ui, computer_act | PR #5 Draft; S2-01–03 fixed and CI verified; focus_window activation chain added on `feat/v1-slice3-4-integration` and covered by deterministic checks; live desktop acceptance still incomplete |
| 3 | Existing browser MCP stdio mount | Integrated on `feat/v1-slice3-4-integration`; 40 deterministic checks pass locally after review round 2, and 8 headless live Playwright checks passed in the first round; not measured with ChatGPT |
| 4 | Tray and installation/lifecycle UX | Integrated on the same branch; 13 controller checks pass locally after review round 2 and a notification-icon smoke test passed in the first round. Connection hardening (`feat/v1-connect-hardening`) adds sign-in autostart, `--start` and supervision; 34 controller checks pass locally. Interactive menus not exercised |
| 5 | Independent extensions | Deferred; not implemented |

## Current desktop work

Source 05ae9e2 freezes UI page responses and original element references, retains the requested capture maximum, and represents a minimized window-only observation without capturing unrelated pixels. Eleven regressions accompany these corrections. Exact-source CI passes 284 Windows and 275 Linux assertions across coding, desktop core and correction suites.

The last local fresh fixture passed capture/UIA checks but returned EXECUTION_UNKNOWN on focus, and no keyboard/mouse input followed. focus_window now restores a minimized target, then tries SetForegroundWindow, AttachThreadInput with BringWindowToTop, and one synthetic ALT tap, confirming the foreground on two consecutive samples after each step and reporting the method, the ALT events it sent and any cleanup key-up. A key-down that may still be held is never reported as confirmed. Nineteen deterministic checks cover the order, the stop rules, key-up and detach failures, late and flickering confirmation and the failure report. The Git fixture helper that stalled other local full-suite attempts now builds its repository in a hermetic Git environment and reports a stall instead of hanging. Still to run on the desktop: `--self-test-desktop` and the authenticated `--self-test-desktop-http`, followed by target-application acceptance. Do not infer these results from CI or earlier WPF successes.

## Browser mount

An existing Playwright or other stdio MCP backend is configured under `browser_mounts` and started directly, with the root as working directory and a minimal environment that never carries credential-named variables. Its tools are published as `browser_<id>_<tool>` (shortened with a hash suffix to 64 characters when needed) with the backend's input schema under `arguments`. Tools listed in `read_only_tools` run directly with read and shell grants; every other tool needs an `invocation_id`, runs through the ledger on a per-mount queue and needs read, write and shell grants. Mounted tools run as the logged-in user and are not contained by the root or its grants. Image content passes through; backend text, embedded resources and resource links are redacted like other output. Shutdown terminates a backend's Job Object or process tree before waiting and bounds every wait, and a backend that exits on its own releases its session and job.

A dedicated profile is the default: a Playwright mount receives `--user-data-dir` under `state_dir`, and profile, CDP, extension, storage-state or config flags in its own arguments make the mount `invalid_config` unless `profile_mode=existing` is chosen explicitly. A mount that cannot start is reported in `host_capabilities` with its state, error and stderr tail, and the coding and desktop tools start regardless. Not yet exercised: ChatGPT use of mounted tools, `profile_mode=existing`, and backends other than Playwright MCP 0.0.81.

## Tray

`--tray` wraps the existing server and control lifecycle in a Windows notification icon: start/stop, status with roots and processes, pause/resume, stopping session children, token revocation, the connection log (rejections, tunnel output, supervision), and editing roots/grants, which stops the server and saves the file. A first run without configuration shows a setup form equivalent to `--init`, with a local port and a sign-in autostart checkbox, and ends with the server running. **Start with Windows** keeps a `CODEXish.lnk` in the Startup folder that runs `--tray --start`, and the tray rewrites it when its target file no longer exists. A configured tunnel runs as a child of the tray, from its menu item, `--start` or the end of setup, only while this server listens, and stops with the server. The server and the owned tunnel are supervised: failures and unrequested stops are retried with a 1 s to 60 s backoff that never gives up, and a user stop ends supervision. Grants remain file-backed; there is no second policy store and no repetitive approval UI. The controller, its supervision and the shortcut (in a temporary Startup folder) are tested without an icon; the menus, setup form and tunnel item have not been exercised interactively.

## Deferred work and verification

Scoped filesystem management, worktrees, journal paging, LSP and further tunnel integration remain separate extensions. Actual ChatGPT OAuth, image reception, continuation and the Pro/Thinking measurement table remain unrecorded. Existing user decisions do not create measurement evidence. Keep original P0 and reviewer history, and retain branches for the user's eventual merge sequence.
