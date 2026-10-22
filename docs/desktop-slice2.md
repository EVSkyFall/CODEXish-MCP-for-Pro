# Desktop slice 2 — preview and review handoff

2026-09-17. PR #5 is a Draft stacked on approved mainline A / PR #4. Source checkpoint: `0c2b2e90c5ef8e911172d4ee615ee707c8b59f82`. [Actual status](../IMPLEMENTATION_STATUS.md) records tests and their provenance. No merge or whole-slice approval is implied.

## Surface

`computer_observe` returns PNG image content and structured metadata: window IDs, PID/start identity, virtual-desktop physical bounds, foreground, cursor, DPI, image-to-desktop transform, and separately sampled UIA focused controls and selected tabs. `max_width=1280` is the default; zero requests native capture. `window_only=true` or four physical crop fields select detail. InputTick is reported but does not itself invalidate an observation.

`computer_query_ui` queries only the selected window's UIA tree on a dedicated MTA worker. It filters role/name/text and returns physical bounds and element references. Password values are omitted. A response page contains at most the requested 1–200 elements; the full query snapshot remains in memory. The replay/reference limitations below are still open.

`computer_act` uses the existing ledger and the `desktop:input` resource. Its actions are `click_element`, `click_coordinate`, `double_click`, `right_click`, `move`, `scroll`, `drag`, `type_text`, `key_combo`, `key_press`, `focus_window`, `minimize_window`, `maximize_window`, and `restore_window`. `coordinate_space` is required: `image` or `desktop_physical_px` for point coordinates, `none` for keyboard/window/element actions. Post-action observation is returned by default.

Native input is checked against the observed window identity, bounds, foreground, display layout and DPI. Semantic clicks re-query and compare their target and hit-test the visible point. Partial insertion releases outstanding delivered key/button downs only; it never replays the action. Known applied input remains applied/partial even if cleanup or subsequent capture fails. Input delivery does not verify an application-level result such as saving a file.

The Windows build adds WPF/UI Automation references to the existing executable. The portable build does not emulate a desktop or substitute synthetic screenshots. Fake pixels occur only in the explicitly deterministic test backend.

## Build and tests

The normal server configuration and authentication remain those of mainline A in the root README. Refresh a connected app's tool schema before expecting the additional three names. This preview has known defects and is not marked ready for general desktop use.

```powershell
dotnet restore src/Codexish.Server/Codexish.Server.csproj
dotnet build src/Codexish.Server/Codexish.Server.csproj -c Release --no-restore
dotnet run --project src/Codexish.Server -c Release --no-build -- --self-test
```

The last command includes the deterministic desktop suite and does not inject desktop input. The explicit command below instead opens and controls a new self-owned WPF fixture, then closes it and removes its test files. It exercises native capture/UIA/input without opening an existing user document. It can temporarily change foreground focus.

```powershell
dotnet run --project src/Codexish.Server -c Release --no-build -- --self-test-desktop
```

The live test's six checks verify PNG content/dimensions, selected tab metadata, maximize/restore with observation-only settling, semantic editor lookup, actual click and Unicode input, and saved UTF-8 bytes. It calls the same desktop service used by the MCP tools, but it is not an end-to-end HTTP desktop invocation and is not Notepad Save As. HTTP self-tests separately inspect registration/schema; the deterministic desktop tests separately exercise ledger idempotency of image-bearing results.

## Open findings — not resolved by the passing tests

| ID | Source-inspection finding | Required correction/evidence |
| --- | --- | --- |
| S2-01 | Replaying a UI cursor creates a fresh next_cursor; a different page_size can also change the returned range. | Freeze page range/size and replay response identity; verify identical retries and continued paging. |
| S2-02 | PublicElement uses observation ID plus UIA runtime ID as a mutable dictionary key. Re-querying that observation can replace the snapshot behind an already issued element_id. | Preserve each issued reference's original state; test element movement followed by re-query and use of the old reference. |
| S2-03 | Post-action capture passes the previous image width instead of the original max_width option. Native-resolution behavior after resize is not retained. A window-only post-capture of a minimized window can fail. | Retain capture options and specify minimized post-observation behavior; validate geometry after resize/minimize without replaying window actions. |

A proposed combined correction was blocked at tool dispatch and did not change the source. The recovery publication contains the pre-existing implementation only. No claim is made that S2-01–03 were fixed, tested, or independently approved.

Next review should also cover UIA provider failure/hang behavior, image/result retention, foreground changes between validation and insertion, and all listed actions on real target apps. Current tests cover a useful narrow fixture journey, not all actions and failure combinations. Real multi-monitor and mixed-DPI acceptance remain unperformed; synthetic negative-origin/DPI tests are not substitutes. Existing P0 M-1–M-8 remain unrecorded.

## Continuation and preserved work

Use PR #5 and the source checkpoint, not PR #3's reference implementation. The interrupted Windows scratch work was backed up before validation. GitHub publication used the exact recovered blob hashes. P0 files and existing CI/deployment scripts were not edited. Further work stays on the slice-2 branch; browser mounting and tray remain separate later slices. User credentials, existing documents and security settings were not changed during this validation.
