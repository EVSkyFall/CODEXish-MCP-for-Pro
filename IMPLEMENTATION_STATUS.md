# Actual implementation and verification status

Updated 2026-09-17. Mainline **A / PR #4** is unchanged. Desktop **PR #5 remains Draft**. No merge, force push, branch deletion, or whole-product completion is reported.

## Connection hardening — local Windows results (2026-09-26)

Branch `feat/v1-connect-hardening`, based on `b6a19aa` (the PR #6 head); the runs below used the uncommitted working tree. They ran on the authorized Windows 11 PC with portable SDK 10.0.401. Nothing here involved ChatGPT, a tunnel, a notification icon, the user's configuration or the real Startup folder. For current behavior this section supersedes the tray row of the slice 3–4 table below.

| Change | What it does |
| --- | --- |
| OAuth metadata | `authorization_response_iss_parameter_supported: true`; the protected-resource document is also served at `/.well-known/oauth-protected-resource/mcp`; no `openid-configuration`. |
| Scope | Any requested scope, or none, is accepted; the grant and the token response are always `mcp`. The `invalid_scope` path is removed. |
| Resource | `resource` never refuses at `/authorize` or `/token`; tokens are always issued for `<public_url>/mcp`; a value on another origin is logged as `oauth resource differs from public origin value=<json>`. |
| Client authentication | A `client_id` that is present (form or Basic) must match; without one the secret alone authenticates the single client; a missing secret is refused as `missing_client_secret`. |
| Redirect URIs | Configured entries still match exactly; any other absolute https URI without a fragment is accepted. For those, errors before the password stay on the local page, the acceptance is logged as `accepted oauth redirect_uri outside configured list host=<host>`, and the code redirect carries `iss`. The sign-in page names the destination host for every callback. `/token` still requires the same redirect_uri. |
| Refresh tokens | Not rotated or consumed: a refresh validates the token and returns a new access token with the same refresh token. Family revocation on reuse, the "already consumed" refusal and consume-and-revoke are removed. `refresh_token_days` defaults to 0, meaning no expiry, which also covers rows stored under the rotating scheme; a positive value is honored and restarts from each refresh. `revoke-tokens` still revokes every token. |
| Build | NU1901–NU1904 are no longer in the server project's `WarningsAsErrors`. The P0 project is unchanged and still has them. |
| Tray | The setup form adds Local port and "Start CODEXish when I sign in to Windows" (checked); `--tray` takes `--start` and pre-fill values `--public-url`, `--port`, `--root`. `TrayAutostart.cs` writes `CODEXish.lnk` in the Startup folder through `IShellLinkW`/`IPersistFile`; a checkable "Start with Windows" item; a shortcut whose target file is gone or that cannot be read is rewritten at tray start. The server and the owned tunnel are supervised: 1 s doubling to 60 s, reset after 5 healthy minutes, never giving up; user stops end supervision; the tunnel starts only while the server listens; the tooltip shows running, retrying with a cause, or stopped. |
| Runtime | A server start that fails after taking the state-directory lock now releases the lock and the database, so a later attempt in the same process can take them. |

| Command | Result |
| --- | --- |
| Release build, server | 0 warnings, 0 errors |
| Server project copy with its Windows conditions set to false (net10.0, no WPF/Windows Forms), offline restore | 0 warnings, 0 errors; a compile check on Windows, not a Linux run |
| `--self-test` | SELF_TEST_PASSED 261 (237 before); DESKTOP_CORE_PASSED 56; DESKTOP_REGRESSIONS 11 passed, 0 failed. The dangling-link check printed SKIP because this session cannot create symbolic links. |
| `--browser-tests` | BROWSER_TESTS_PASSED 40 |
| `--tray-tests` | TRAY_CONTROLLER_PASSED 36 (13 before); the autostart checks ran against a temporary Startup folder |
| `--tray-tests` built with the previous `Runtime.cs` | TRAY_CONTROLLER_FAILED after 28, at the check that a failed start releases the state-directory lock; its test directory could not be removed while the leaked handle was open and was deleted afterwards |

Not run and not claimed: `--tray-smoke-test`, a plain `--tray` (setup form, menus, the Start with Windows item and the tooltip), an actual Windows sign-in with the shortcut, `--self-test-desktop`, `--self-test-desktop-http`, `--browser-live-test`, the P0 self-test, CI for this branch, a Linux run, and any use of ChatGPT, Tailscale or cloudflared.

## Slice 3–4 integration — local Windows results

Branch `feat/v1-slice3-4-integration`, based on `bddeffe` (PR #5 head). Everything below ran on the authorized Windows 11 PC with portable SDK 10.0.401 against the source containing these changes. No CI run of this branch has been read, and nothing here involved ChatGPT, a tunnel or a personal browser profile.

| Change | What it does |
| --- | --- |
| focus_window activation | Restore when minimized, then already-foreground, SetForegroundWindow, AttachThreadInput + BringWindowToTop, and one synthetic ALT tap, each confirmed through GetForegroundWindow. Results report `activation.method`, `attempted` and `activation_inputs_sent` apart from `delivered`/`planned`; an unconfirmed activation stays EXECUTION_UNKNOWN. No system parameter, lock timeout or other process's permission is changed. |
| Git fixture helper (test only) | Closed stdin, concurrent output reads, hermetic setup (`GIT_CONFIG_NOSYSTEM`, empty `GIT_CONFIG_GLOBAL`, `GIT_OPTIONAL_LOCKS=0`, fsmonitor/maintenance/gc/autocrlf off). The control run keeps the repository's trapped keys. A command still running after 120 s prints STALL diagnostics and fails. |
| Browser mount (slice 3) | `browser_mounts` configuration; backends started directly over stdio; `browser_<id>_<tool>` names of at most 64 characters; existing ledger and grant rules; per-mount state and error in `host_capabilities`; dedicated profiles under `state_dir`. |
| Tray (slice 4) | `--tray` Windows Forms notification icon over an in-process controller; a configured tunnel starts only from its menu item. |
| HTTP desktop acceptance | Explicit `--self-test-desktop-http` entry, not part of `--self-test`. |
| CI | `--browser-tests` and `--tray-tests` steps on Windows and Ubuntu. |

| Command | Result |
| --- | --- |
| Release build, server | 0 warnings, 0 errors |
| Release build, P0 (source unchanged) | 0 warnings, 0 errors |
| Server project copy with its Windows conditions set to false (net10.0, no WPF/Windows Forms), offline restore | 0 warnings, 0 errors; a compile check on Windows, not a Linux run |
| `--self-test` | SELF_TEST_PASSED 235; DESKTOP_CORE_PASSED 46 (37 + 9 activation checks); DESKTOP_REGRESSIONS 11 passed, 0 failed. The dangling-link check printed SKIP because this session cannot create symbolic links. The Git control run still proved five traps firing (clean, external, fsmonitor, hook-post-index-change, smudge). |
| `--self-test` with `CODEXISH_SELFTEST_SHELL=pwsh` | 234 (second-interpreter check SKIP by design); 46; 11 passed, 0 failed |
| `--browser-tests`, five consecutive runs | BROWSER_TESTS_PASSED 33 in each run |
| `--browser-live-test` with node, @playwright/mcp 0.0.81 and Chrome, headless, loopback fixture | BROWSER_LIVE_PASSED 8; the published schema selected `target` for `browser_fill_form` fields and for `browser_click` |
| `--tray-tests` | TRAY_CONTROLLER_PASSED 12 |
| `--tray-smoke-test` | TRAY_UI_SMOKE_PASSED |
| P0 `--self-test` | SELF_TEST_PASSED 97 |
| Git stall diagnostic, invoked once through `dotnet fsi` with a 3 s threshold on a sleeping git alias | STALL lines listed the git, sh and sleep processes with command lines; the tree was ended and the helper failed with its message |

Not run and not claimed: `--self-test-desktop` and `--self-test-desktop-http` (both drive the live desktop), CI for this branch, a Linux execution, interactive tray menus, first-run setup, root editing and tunnel start. The activation chain is covered by deterministic checks only; whether it resolves the EXECUTION_UNKNOWN of the last live fixture run is not established until that run is repeated.

Found and fixed during these runs: one early live browser run left a `chrome_url_fetcher_*` directory in the user TEMP, so the live test now gives the backend a TEMP inside its own trial directory. One of eight `--browser-tests` runs before that fix left an emptied test directory, because a recursive delete on Windows returns while a deleted file is still held open elsewhere; test cleanup now repeats until the directory is gone, and the ten runs after the fix left nothing behind. The unchanged P0 self-test showed the same effect once with an empty `CODEXish-P0-*.state` directory; P0 was not modified.

### Review round 2 (on top of 97bfd10)

The review findings adopted by the lead were implemented while the PC was in use, so local runs were kept to two Release builds and the suites the changes touch. No live desktop, tray icon or live browser test ran in this round.

| Change | Local result |
| --- | --- |
| ALT tap: key-down and key-up results are checked separately, one cleanup key-up releases a delivered key-down whose key-up was not accepted, and a key that may still be held is never reported as a confirmed activation. Detaches run in nested `finally` blocks, a confirmation needs two consecutive samples, and counts survive exceptions. | Both builds 0 warnings, 0 errors; `--desktop-core-test` DESKTOP_CORE_PASSED 56 (46 + 10); `--desktop-regression-test` 11 passed, 0 failed |
| Browser mounts: shutdown terminates the Job Object or process tree before any wait and bounds every wait; a backend that exits on its own releases its session and job; null configuration fields are normalized; ids are unique without case; embedded resources and resource links are redacted; `host_capabilities` states that mounted tools are not contained by the root or its grants. | `--browser-tests` BROWSER_TESTS_PASSED 40 (33 + 7); disposing a backend that ignores end of input took 5.1 s |
| Tray: browser calls are cancelled before the host stops; the control token, client secret and password hash are redacted from tunnel output. | `--tray-tests` TRAY_CONTROLLER_PASSED 13 (12 + 1) |
| Git fixture helper: inherited `GIT_*` variables are removed; the stall report prints command lines only for the stalled process tree. | `--self-test` SELF_TEST_PASSED 237 (235 + 2), DESKTOP_CORE_PASSED 56, DESKTOP_REGRESSIONS 11 passed, 0 failed |

## Current source and completed corrections

Product source: `05ae9e2aaa23833b5e089d73b18dfa0a4f24d8ab` (tree `8f56a9707e08eda023456abb1890321e21b941c6`). This commit was already on the desktop branch when the latest continuation resumed. It supersedes the earlier open-finding statements for S2-01–S2-03; the current continuation verified it rather than recreating its changes.

| Finding | Implemented correction | Regression evidence |
| --- | --- | --- |
| S2-01 | Cached complete UI page response; cursor binds filters and page size | Identical response/next cursor on replay; size mismatch rejected; no duplicate/missing elements |
| S2-02 | Issued element references retain their original snapshots | Re-query yields a different reference for changed state; old moved target refuses input |
| S2-03 | Original max_width retained; minimized window-only views are metadata-only | Native/scaled post-capture remains correct after resizing; no unrelated screenshot on minimize; restore yields image |

See [correction implementation](docs/desktop-corrections.md) and [latest continuation record](docs/continuation-20260917.md). These findings are **implemented and regression-tested**, not proof of full desktop acceptance or independent review approval.

## Verified source CI

[Run 35186928060](https://github.com/EVSkyFall/CODEXish-MCP-for-Pro/actions/runs/35186928060) checked out exactly `05ae9e2`. Its branch label is `feat/v1-slice3-browser-mount`, but the SHA contains the same desktop source, not an implemented browser mount. Both complete job logs and completion metadata were read.

| Runner / SDK | Job | Coding | Desktop core | S2 regression | Total assertions | Build warnings / errors |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| Windows Server 2025 / .NET 10.0.401 | 105090882195 | 236 PASS | 37 PASS | 11 PASS | **284** | **0 / 0** |
| Ubuntu 24.04.5 / .NET 10.0.401 | 105090882020 | 226 PASS | 38 PASS | 11 PASS | **275** | **0 / 0** |

Restore, Release build, all three suites, and artifact upload succeeded. Schema artifacts are Windows `10482876186` and Ubuntu `10481863507`. Platform-specific skips explain the different counts. Existing workflow Node/action deprecation notices are not compiler warnings. CI does not send desktop input or establish ChatGPT behavior.

## Latest local continuation — separate outcomes

A fresh temporary clone of `05ae9e2` was used on the authorized Windows PC with portable SDK 10.0.401; no existing working tree was overwritten.

| Execution | Actual result |
| --- | --- |
| baseline-build: Release build | exit 0; 0 warnings, 0 errors |
| desktop-core: --desktop-core-test | exit 0; 37 PASS, deterministic backend |
| desktop-regressions: --desktop-regression-test | exit 0; 11 PASS, 0 FAIL, deterministic backend |
| baseline-tests: full --self-test | Stopped by operator after initial Git fixture setup stalled at git add tracked.txt; NOT a passing full suite |
| baseline-tests-isolated: second full --self-test with isolated child Git configuration | Same setup stall; stopped by operator; NOT a pass |
| fresh --self-test-desktop, process 102444 | exit 1 after two checks: real opaque PNG and selected-tab UIA passed, then focus_window returned EXECUTION_UNKNOWN; no keyboard/mouse input was delivered |

The latest native run's foreground request was not confirmed by Windows. Its root cause is not established, and the known fixture success from an earlier run is not substituted for this result. The runner closed its own fixture. No existing user document, security setting, privilege, tunnel or credential store was changed. The full-suite setup issue was not fixed by altering its test helper or weakening its assertions.

## Delivery boundary

The server registers **24 base tools** (21 coding/checkpoint tools and computer_observe, computer_query_ui, computer_act) plus the tools of each browser mount that connects at start; without `browser_mounts` the published list is unchanged. `src/Codexish.P0` and deployment scripts are unchanged; the v1 workflow gains the browser and tray test steps. A remains PR #4; PR #3 remains closed with its reference branch retained.

The browser-mount and HTTP desktop acceptance drafts that were blocked in the earlier continuation are now integrated on `feat/v1-slice3-4-integration` with the results above. Browser mounts are verified by deterministic and headless live tests only: not with ChatGPT and not with `profile_mode=existing`. The tray is verified by controller tests and a notification-icon smoke test; its interactive menus were not exercised. The authenticated HTTP desktop acceptance was built but not executed. Deferred extensions are **not implemented**. Notepad Save As, other production apps, physical multi-monitor/mixed-DPI acceptance, and real ChatGPT OAuth/image/continuation M-1–M-8 remain unverified. The blank measurements still support no Case 0/4 conclusion.

## Earlier evidence retained

For recovery source `0c2b2e9`, run `35183820986` passed Windows 236+37 and Ubuntu 226+38 assertions. A prior local `resume-live-1` passed six self-owned WPF checks and saved `CODEXish desktop verification - 한글` as 38 UTF-8 bytes, SHA256 `65AE6CAA2D4F4DFBFAE6DD1E858FD5D9FDF028B8DCDD8EBDE3CF3077BE42912A`. That evidence remains historical; the new focus failure is recorded above. Earlier source and procedure records remain in Git history, `docs/desktop-corrections.md`, and [IMPLEMENTATION_STATUS.slice1.md](IMPLEMENTATION_STATUS.slice1.md).

## Live desktop acceptance on the user's Windows 11 desktop (lead, 2026-09-17, commit after 306b68d)

Run at the user's request with the Claude app in the foreground and no game running. Only self-owned WPF fixture windows were driven.

| Run | Result |
| --- | --- |
| `--self-test-desktop` | DESKTOP_LIVE_PASSED 8: capture, UIA tab, focus, maximize/restore, semantic lookup, semantic click + Unicode typing, CTRL+S saved 38 UTF-8 bytes (SHA256 65AE6CAA...), focus from minimized, focus from behind another process window |
| `--self-test-desktop-http` | DESKTOP_HTTP_PASSED 14: synthetic OAuth, authenticated initialize, PNG + window identity over HTTP, cursor replay, native input with post-action image, stored retry result, exactly-once save (34 bytes), fs_read verification |

Observed `focus_window` activation methods: first focus `attach_thread_input` (after `set_foreground` was not confirmed; the pre-fix code would have returned EXECUTION_UNKNOWN here), from minimized `restore` then `already_foreground`, second fixture `already_foreground`, from behind another process `set_foreground`. `alt_tap` was never needed; `activation_inputs_sent` stayed 0.

Two test-harness bugs were fixed before these runs: the first focus now acts on a settled observation instead of the capture taken while the new window was still activating, and the HTTP acceptance reads `fs_read` text from `output.text`. An earlier attempt the same day was stopped because a game was in the foreground; live desktop tests must only run when no other application is in front and the user is idle.
