# CODEXish development

Read IMPLEMENTATION_STATUS.md, docs/desktop-slice2.md, README.md, docs/v1-design.md and docs/v1-plan.md first.

## Current user decision and branch

The user approved mainline A / PR #4 (`feat/v1-slice1-opus`). PR #4 is Ready for review; PR #3 is closed as reference/ported implementation with its branch retained. Do not reopen the choice, merge PRs, delete branches or replace A with the reference implementation without a new user request.

Current continuation is Draft PR #5, `feat/v1-slice2-desktop`, based on A at `3cb4676`. Recovery source is `0c2b2e9`. It registers 24 underscore-named tools: 21 coding/checkpoint plus computer_observe, computer_query_ui and computer_act. The earlier slice-1 prohibition on adding computer tools applied to that first slice, not this expressly requested continuation. P0 remains unchanged.

## Completion and review

Slice 2 is IN_PROGRESS, not complete. Read S2-01 through S2-03 in docs/desktop-slice2.md. Passing tests do not close UI cursor replay, element-reference rebinding or post-capture option findings. A proposed correction was blocked before execution and not rerouted; do not report it as applied. Preserve the recovered source and evidence rather than silently overwriting an interrupted working tree.

The existing docs/v1-design.md remains the mainline A coding decision record, above superseded v0.1 proposals. The desktop preview supplements it. Browser MCP mounting is slice 3, tray is slice 4; no extra model/API key, Gateway/Agent split, custom WSS, mTLS enrollment or broker split.

Keep request/operation/process lifetimes distinct. Reuse invocation IDs only for identical retries. Inspect unknown effects and never blindly replay input. Conflicting work uses the resource queue rather than BUSY; reads remain independent. Do not add task time/call caps or repeated approvals. Keep root grants, the disclosed unconfined_user boundary and the existing control contract. No clipboard, elevation or security-setting changes.

Record actual commands, exit codes, source hashes and environment. Windows fixture evidence is not Notepad Save As, multi-monitor hardware acceptance, HTTP desktop E2E or ChatGPT/Pro M-1–M-8. Default --self-test uses a deterministic desktop backend and does not inject input; explicit --self-test-desktop controls only its own newly created fixture process. Do not use an existing user document as a test fixture.

The root README describes the coding base. Desktop preview usage/status is docs/desktop-slice2.md. Historical evidence is preserved in IMPLEMENTATION_STATUS.slice1.md and the existing review documents. Merge remains the user's action.
