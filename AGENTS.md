# CODEXish development

Read README.md, IMPLEMENTATION_STATUS.md and docs/review-response.md first.

The user-supplied design review of 8cecfc7 is CHANGES_REQUESTED. The later CODEXish-P0-verification-by-Claude.md approves bdb4bf2/0dd0b6b for P0 scope, conditional on F-1 (Host/Origin configuration) and F-2 (scaled screenshots). Apply those fixes on PR #2; keep C-1–C-7 as v1 follow-ups. Full v1 is not approved before M-1–M-8 measurements. The earlier universal pre-approval prohibition is superseded for P0 only.

Build one C#/.NET 10 application. Chat Pro performs reasoning; this server provides tools, not another model. Do not rebuild the proposed TypeScript Gateway, custom WSS, mTLS enrollment or broker split for v1.

Keep request/operation/process lifetimes distinct; reuse invocation IDs on retry; never replay unknown effects; queue conflicting effects rather than returning BUSY. Observe before and after GUI input. Report actual tests and saved files, not inferred success.

Do not confuse a fixed P0 fixture with arbitrary shell execution or a complete coding agent. Source existence is not a successful build. Linux/headless CI is not interactive Windows or Pro Chat evidence. Record M-1 through M-8 honestly.

Preserve the original v0.1 documents as historical proposals pending consolidation after measurements. New code follows the P0 review exception and docs/review-response.md. Do not mark the whole design APPROVED or merge without authorization.
