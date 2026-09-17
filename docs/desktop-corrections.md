# Desktop corrections — S2-01 through S2-03

2026-09-17. This record supersedes the three open-finding rows in desktop-slice2.md and the earlier recovery status. Mainline A and the P0 source remain unchanged. It is implementation/test evidence, not an independent approval or a ChatGPT measurement.

## Implemented corrections

- S2-01: a UI cursor retains its query, page size, original element array and rendered response. Replaying it returns the same complete structured response and next cursor. Changing page size returns CURSOR_INVALID. Replaying a frozen page does not resample the window; actions still validate current state before delivery.
- S2-02: every issued element reference has a distinct immutable snapshot binding. Re-querying the same provider runtime ID cannot replace the state behind an older reference. An old reference to a moved element remains stale even after a new query.
- S2-03: observations retain the requested max_width, including zero, and reuse that option after actions. A minimized window-only observation returns metadata with image_available=false, image_unavailable_reason=minimized_window and no PNG. It does not expand into a capture of unrelated windows. Its observation can be used to restore the selected window.

## Actual red/green evidence

The new DesktopRegressionTests suite was run against the previous implementation before correction: exit 1, two assertions passed and seven failed. After correction: exit 0, eleven assertions passed, zero failed. The final two checks exercise restore from the now-successful minimized metadata response.

Windows 11 / portable .NET SDK 10.0.401, fresh isolated working copy:

| Run | Result |
| --- | --- |
| s2-green-build | exit 0; 0 compiler warnings, 0 errors |
| s2-green-focused | exit 0; 11 regression assertions |
| s2-isolated-tests | exit 0; coding 235 + desktop core 37 + new regressions 11 = 283 assertions |
| s2-live | exit 0; six real own-window fixture checks |

The full suite skipped the local dangling-link privilege check; no Windows setting was changed. A preceding test run was stopped while Git setup was not making progress and is not counted as passed. The later run completed with an isolated HOME and Git global/system configuration. A proposed test-helper alteration was blocked before execution and SelfTestGit.cs was not changed.

The live fixture captured its own fresh WPF window, queried UIA, maximized/restored, clicked the editor, typed Unicode and saved through Ctrl+S. Exact output: CODEXish desktop verification - 한글 (38 UTF-8 bytes), SHA256 65AE6CAA2D4F4DFBFAE6DD1E858FD5D9FDF028B8DCDD8EBDE3CF3077BE42912A. This was not Notepad Save As, mixed-DPI hardware or a ChatGPT client trial.

The new suite runs under --self-test and individually under --desktop-regression-test. CI for the published commit must be read separately; the local results above are not a claim that CI has run.

Remaining acceptance: actual other applications, physical multi-monitor/mixed-DPI, UIA provider stalls, long-lived retention, foreground races and P0 M-1 through M-8. Browser mounting is a separate next slice. No merge is authorized by this record.
