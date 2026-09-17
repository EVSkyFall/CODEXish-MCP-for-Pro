# Actual implementation and verification status

Updated: 2026-09-17. Mainline: **A / PR #4**. Desktop continuation: **Draft PR #5**, not merged or approved as complete.

## Current lineage

The user's approval selects PR #4 (`feat/v1-slice1-opus`) as mainline. PR #4 is Ready for review. PR #3 is closed as reference/ported implementation, with its branch retained. No PR was merged and no branch was deleted in this continuation. Merge remains the user's action, with P0 before mainline A.

Desktop branch `feat/v1-slice2-desktop` builds on A at `3cb46760abd7a7ff42e5cd31e3e4aa005711e5ae`. Earlier desktop commits `f7044fc`, `67338aa`, `a1d6124` are preserved. Recovery integration commit: **`0c2b2e90c5ef8e911172d4ee615ee707c8b59f82`**, tree `fe5f32bb5bae63de5bc9778990001e59b4bbf024`.

The recovered working copy was backed up before validation. The 11 integration file Git blob hashes match the published source after normalizing checkout CRLF to Git LF. `src/Codexish.P0` remains unchanged from `c1b04cd`; workflows and deployment settings were not edited. This status update changes documentation only.

## Actual tests on the recovered source

| Environment | Build | Coding tests | Deterministic desktop tests | Total | Live desktop fixture |
| --- | --- | ---: | ---: | ---: | --- |
| Authorized Windows 11 PC / portable .NET 10.0.401 | exit 0; 0 warnings, 0 errors | 235 PASS | 37 PASS | 272 | 6 PASS, exit 0 |
| GitHub Windows Server 2025 / .NET 10.0.401 | exit 0; 0 warnings, 0 errors | 236 PASS | 37 PASS | 273 | Not run |
| GitHub Ubuntu 24.04.5 / .NET 10.0.401 | exit 0; 0 warnings, 0 errors | 226 PASS | 38 PASS | 264 | Not run |

The local Windows log explicitly skips a dangling-symbolic-link test because that session may not create symbolic links. No privilege or OS setting was changed to run it. Windows CI executes it. Linux skips Windows-specific sharing/Job checks and adds the explicit unavailable-native-desktop check. Counts are assertions across suites, not separate end-to-end user tasks.

Verified code CI: [run 35183820986](https://github.com/EVSkyFall/CODEXish-MCP-for-Pro/actions/runs/35183820986), checkout exactly `0c2b2e9`. Full logs were read for Windows job `105081481480` and Ubuntu job `105081481674`; restore, Release build, self-test and artifact upload completed. Schema artifacts: Windows `10481153210`, Ubuntu `10481636120`. Existing Node/action deprecation warnings are separate from the zero compiler warnings.

Fresh local log labels are `resume-build-1`, `resume-self-tests-1`, `resume-live-1` in the existing CODEXish temporary evidence directory. Test children use the existing isolated environment/SDK runner. The live fixture creates only its own new WPF process, captures that window, exercises UIA, maximizes/restores, clicks its editor, types Unicode, and saves through Ctrl+S. Exact saved text: `CODEXish desktop verification - 한글`; 38 UTF-8 bytes; SHA256 `65AE6CAA2D4F4DFBFAE6DD1E858FD5D9FDF028B8DCDD8EBDE3CF3077BE42912A`. Its temporary window and files are cleaned by the test. This is not Notepad Save As or a ChatGPT trial.

An earlier interrupted `desktop-final-live` log failed during restore with STALE_OBSERVATION and no delivered input. It remains a failed historical run. The recovered fixture's observation-only settling sequence was exercised in the fresh passing run; it does not repeat the action to wait for animation.

## Delivered scope and remaining work

The server registers 24 underscore-named tools: the existing 21 coding/checkpoint tools plus `computer_observe`, `computer_query_ui`, `computer_act`. The native backend is Windows-only. Read [desktop preview and open findings](docs/desktop-slice2.md) before using the new surface. The root README and `docs/v1-design.md` still describe the stable coding base; this preview supplements them, not the archived PR #3 implementation.

**Slice 2 remains IN_PROGRESS.** Open findings S2-01 through S2-03 cover UI cursor replay stability, re-query element-reference rebinding, and post-action capture options/minimized window behavior. A single proposed correction was blocked before execution by the tool's safety-decision stage. Before/after hashes confirm it was not applied; it was not rerouted. The passing tests do not cover or close these findings.

Not measured: Notepad Save As, other production apps, physical multi-monitor/mixed-DPI acceptance, real ChatGPT OAuth/image ingestion/continuation or Pro/Thinking M-1 through M-8. No measurements are inferred from CI or the WPF fixture. Browser mounting is slice 3; tray is slice 4.

The preceding status document is preserved byte-for-byte as [IMPLEMENTATION_STATUS.slice1.md](IMPLEMENTATION_STATUS.slice1.md). Its "current" and "not run" statements refer to its historical slice-1/P0 dates, not to the new desktop fixture evidence above.
