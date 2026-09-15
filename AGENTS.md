# CODEXish development

Read README.md, IMPLEMENTATION_STATUS.md, docs/p0-measurement-record.md, docs/v1-design.md, docs/v1-plan.md, then the applicable sections of docs/tool-contracts.md and actual source.

## Current authorization and scope

The latest user-supplied CODEXish-GPT-next-input-package.md, especially D.2–D.7 and E, specifies the first v1 coding slice. It supersedes the older multi-process proposal for this implementation. This is not an independent approval of the newly written code. Its measurement table is blank: record UNDETERMINED, never infer Case 0 or Case 4 from missing data or CI. Do not mark actual Pro/Thinking or GUI measurements complete.

Continue PR #3 on feat/v1-slice1-coding-core, based on PR #2's feat/p0-review-response-20260915. Preserve other authors' commits. Read the current ref before writing; on conflict reconcile rather than force-push. PR #2 readiness depends on the unrecorded case and remains separate. Merge is the user's decision.

## Implementation

One C#/.NET 10 project, currently src/Codexish.P0. Default startup runs v1 with --config; --p0 selects the retained seven-tool measurement probe. V1 currently registers the 20 coding tools plus session.checkpoint. computer.* is slice 2, external browser MCP mounting slice 3, tray/setup slice 4. Do not rebuild a Gateway/Agent, external model, Codex delegation, or mTLS registration system.

Use root grants, acceptance-order FIFO including cancellation barriers, the P0 Reply envelope, expected-hash in-place writes with backups and encoding preservation, durable invocation IDs and unknown(persist_failed). Reads bypass mutation queues. Do not add BUSY/ALREADY_RUNNING rejection, repeated approval, or task time/call caps. A response wait is not an execution deadline. Inspect uncertain effects instead of replaying them.

Execution remains unconfined_user. No sandbox claim, credential harvesting, user-PC configuration/security changes, elevation, or safeguard/deployment-script edits. Tests use newly generated temporary roots, synthetic credentials, and owned child processes. Preserve the existing NativeDesktop.cs during slice 1.

## Evidence and handoff

Run --self-test through the existing two-OS CI; require a successful process exit and cleanup, not just an intermediate PASS marker. Record code SHA, tested merge SHA, run/job IDs, separate P0/v1/FIFO counts, compiler warnings, skipped OS-specific checks, and remaining coverage. Static review, CI, reviewer-reported user-machine results and real Chat trials are distinct evidence classes.

The current v1 setup is in README.md; README.p0.md and IMPLEMENTATION_STATUS.p0.md preserve their prior contents. Archived P0 command lines require --p0 when run on this v1 branch. Historical v0.1 documents remain intact; do not silently restore their superseded architecture or infer new approval gates from them.
