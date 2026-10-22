# CODEXish development

Read IMPLEMENTATION_STATUS.md, docs/continuation-20260917.md, docs/desktop-corrections.md, README.md, docs/v1-design.md and docs/v1-plan.md first.

## Current user decision

Mainline A is PR #4 (`feat/v1-slice1-opus`). PR #4 is Ready for review; PR #3 is closed as a reference with its branch preserved. The user asked for continuous implementation, not a new choice of baseline. Do not merge PRs or delete branches; merge remains the user's action.

Desktop continuation is Draft PR #5 (`feat/v1-slice2-desktop`). Current product source is 05ae9e2. S2-01–S2-03 are implemented and covered by 11 passing regressions; older statements that they are unimplemented are historical. Both source CI jobs passed. Broader desktop acceptance is not complete: the latest own-window live run failed when Windows did not confirm focus, before keyboard/mouse delivery. Do not replace that failure with a prior successful run.

## Follow-up and blocked writes

Browser integration and a separate HTTP-desktop test CLI entry were blocked before execution by the ChatGPT-side tool safety stage. At the user's direction the independent reviewer (Claude) integrated the preserved drafts on branch feat/v1-slice3-4-integration: browser mounts (BrowserMount.cs, --browser-tests, --browser-live-test), the tray (TrayController.cs, TrayApplication.cs, --tray, --tray-tests, --tray-smoke-test) and the opt-in --self-test-desktop-http entry, plus the focus_window activation chain and a hermetic git fixture helper. Live desktop acceptance of the activation chain and --self-test-desktop-http are still unverified. Record the exact scope of any further work and actual results; never weaken tests or protections just to obtain a pass. Before any live desktop test, check that no other application (for example a game) is in the foreground and that the user is idle; live tests take the foreground.

The server registers 24 underscore-named native tools (21 coding/checkpoint and computer_observe, computer_query_ui, computer_act) plus the tools of any configured browser mount, re-exposed under hashed names of at most 64 characters. P0 remains unchanged. The single C# server, root grants, unconfined_user disclosure, ledger/FIFO and existing control contract remain the mainline decisions. Do not rebuild a Gateway/Agent split, external model, WSS or mTLS layer.

Keep request/operation/process lifetimes distinct. Reuse invocation IDs only for identical retries; inspect uncertain effects without replay. Conflicting mutations queue, reads stay independent. Do not add task time/call caps or repeated action approvals. No credential-store reading, clipboard, elevation, user security-setting changes, or deployment/safeguard script edits.

Tests must identify their environment and source. Default --self-test does not inject desktop input. --self-test-desktop controls only its own newly created fixture process. Preserve failed and stopped runs alongside successful ones. CI/mock/WPF results do not fill the unrecorded Pro/Thinking M-1–M-8 table. New source alone is not a successful build or a completed feature.
