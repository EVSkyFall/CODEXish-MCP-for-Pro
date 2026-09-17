# Actual implementation and verification status

Updated 2026-09-17. Mainline **A / PR #4** is unchanged. Desktop **PR #5 remains Draft**. No merge, force push, branch deletion, or whole-product completion is reported.

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

The published server still registers **24 tools**: 21 coding/checkpoint tools and computer_observe, computer_query_ui, computer_act. `src/Codexish.P0` and deployment/workflow scripts are unchanged. A remains PR #4; PR #3 remains closed with its reference branch retained.

A browser-mount module was drafted in a temporary clone. Its requested Config/Runtime/Host/Program integration was blocked before execution by the tool's safety-decision stage. A separate opt-in HTTP desktop acceptance test was drafted, but adding its CLI entry was also blocked before execution. Both drafts were moved outside the source tree and were **not committed, built, exposed as tools, or executed**. Tracked-source diff was empty after preservation. These blocks were not bypassed through another execution or publication route.

Browser mounting is **not integrated**. Tray/lifecycle UI and deferred extensions are **not implemented**. Authenticated HTTP desktop acceptance, Notepad Save As, other production apps, physical multi-monitor/mixed-DPI acceptance, and real ChatGPT OAuth/image/continuation M-1–M-8 remain unverified. The blank measurements still support no Case 0/4 conclusion.

## Earlier evidence retained

For recovery source `0c2b2e9`, run `35183820986` passed Windows 236+37 and Ubuntu 226+38 assertions. A prior local `resume-live-1` passed six self-owned WPF checks and saved `CODEXish desktop verification - 한글` as 38 UTF-8 bytes, SHA256 `65AE6CAA2D4F4DFBFAE6DD1E858FD5D9FDF028B8DCDD8EBDE3CF3077BE42912A`. That evidence remains historical; the new focus failure is recorded above. Earlier source and procedure records remain in Git history, `docs/desktop-corrections.md`, and [IMPLEMENTATION_STATUS.slice1.md](IMPLEMENTATION_STATUS.slice1.md).
