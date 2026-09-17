# Actual implementation and verification status

Updated 2026-09-17. Mainline **A / PR #4** is unchanged. Desktop **PR #5 remains Draft**. No merge, force push, branch deletion, or whole-product completion is reported.

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
