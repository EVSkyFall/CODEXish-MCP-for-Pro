# CODEXish development

Read README.md, IMPLEMENTATION_STATUS.md and docs/review-response.md first.

The design review of 8cecfc7 remains CHANGES_REQUESTED for full v1 until M-1–M-8. The newer verification §6a accepts F-1/F-2 after reviewer-reported Windows build and 96/96 tests. The user supplied the Codex read-only review and Claude triage and asked to proceed. Publish F-1/F-2 plus the triage's L-1 PID-error mapping and H-1/H-2/H-3 measurement procedure changes on PR #2. Record constructor-test coverage separately from the statically reviewed live-exit branch. Keep C-1–C-7 and the triaged persistence/FIFO/partial-key findings for v1. P0 resource exclusion is mutual exclusion, not acceptance-order FIFO; do not call its tests ordered-queue tests.

Build one C#/.NET 10 application. Chat Pro performs reasoning; this server provides tools, not another model. Do not rebuild the proposed TypeScript Gateway, custom WSS, mTLS enrollment or broker split for v1.

Keep request/operation/process lifetimes distinct; reuse invocation IDs on retry; never replay unknown effects; serialize conflicting effects rather than returning BUSY (P0 does not promise FIFO). Observe before and after GUI input. Report actual tests and saved files, not inferred success.

Do not confuse a fixed P0 fixture with arbitrary shell execution or a complete coding agent. Source existence is not a successful build. Linux/headless CI is not interactive Windows or Pro Chat evidence. Use README Measurement: another device for ChatGPT, fresh fixture per trial, unsaved Notepad tab with image-only nonce, and a same-tool reference run. Record M-1 through M-8 honestly.

Preserve the original v0.1 documents as historical proposals pending consolidation after measurements. New code follows the P0 review exception and docs/review-response.md. Do not mark the whole design APPROVED or merge without authorization.
