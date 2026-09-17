# CODEXish development

Read IMPLEMENTATION_STATUS.md, docs/continuation-20260917.md, docs/desktop-corrections.md, README.md, docs/v1-design.md and docs/v1-plan.md first.

## Current user decision

Mainline A is PR #4 (`feat/v1-slice1-opus`). PR #4 is Ready for review; PR #3 is closed as a reference with its branch preserved. The user asked for continuous implementation, not a new choice of baseline. Do not merge PRs or delete branches; merge remains the user's action.

Desktop continuation is Draft PR #5 (`feat/v1-slice2-desktop`). Current product source is 05ae9e2. S2-01–S2-03 are implemented and covered by 11 passing regressions; older statements that they are unimplemented are historical. Both source CI jobs passed. Broader desktop acceptance is not complete: the latest own-window live run failed when Windows did not confirm focus, before keyboard/mouse delivery. Do not replace that failure with a prior successful run.

## Follow-up and blocked writes

Browser integration and a separate HTTP-desktop test CLI entry were blocked before execution by tool safety decisions. They were not published or executed through another route. Their unintegrated drafts were preserved outside the temporary repository. Do not claim browser tools, a tray or HTTP desktop acceptance now exist in the product. Record the exact scope of any subsequently allowed work and actual results; never weaken tests or protections just to obtain a pass.

The current code still has 24 underscore-named tools: 21 coding/checkpoint and computer_observe, computer_query_ui, computer_act. P0 remains unchanged. The single C# server, root grants, unconfined_user disclosure, ledger/FIFO and existing control contract remain the mainline decisions. Do not rebuild a Gateway/Agent split, external model, WSS or mTLS layer.

Keep request/operation/process lifetimes distinct. Reuse invocation IDs only for identical retries; inspect uncertain effects without replay. Conflicting mutations queue, reads stay independent. Do not add task time/call caps or repeated action approvals. No credential-store reading, clipboard, elevation, user security-setting changes, or deployment/safeguard script edits.

Tests must identify their environment and source. Default --self-test does not inject desktop input. --self-test-desktop controls only its own newly created fixture process. Preserve failed and stopped runs alongside successful ones. CI/mock/WPF results do not fill the unrecorded Pro/Thinking M-1–M-8 table. New source alone is not a successful build or a completed feature.
