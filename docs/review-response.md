# Review response — P0, not full v1 approval

**Follow-up:** verification §6a accepts the F-1/F-2 patch after reviewer-reported Windows 96/96 tests. The subsequent Codex review and Claude triage request L-1 plus H-1/H-2/H-3 procedure changes. See [pre-measurement fixes](p0-premeasurement-fixes.md) and [Measurement](../README.md#measurement). This is P0 approval only; the remaining text records the earlier design-review response.

Reviewed source: user-uploaded `CODEXish-review-by-Claude.md`, 2026-09-15; reviewer label Claude Fable 5.1; commit 8cecfc7680ddb032eddb7c35837a9ed4bedb9621; decision CHANGES_REQUESTED. This response does not change that decision.

## Accepted direction

Decision: one C#/.NET 10 process plus an existing tunnel, rather than a custom Gateway/Agent transport.
Decision: test Pro tool calling and actual GUI observation/input first; no T03/T04 architectural spike gates for P0.
Decision: keep the 23 native tools in review §2.5 as the proposed v1 list; existing browser MCP integration is separate work, not a second implementation of a browser agent.
Decision: fixed Git observation helpers are allowed under R-07; no separate reviewer gate.
Decision: keep lifetimes, ID/digest conflict detection, unknown/no replay, queues, expected hashes, encoding preservation, and observable outcomes.
Decision: provide server instructions and useful tool descriptions now, not after the adapters.

## Explicit corrections and scope differences

- The review's actual table has **eight** measurements M-1–M-8, not six; its explicit tool list has **23**, not the later stated 20. Neither GUI measurement is dropped.
- An exclusive in-place write is not atomic replacement. This P0 labels that contract explicitly and keeps a pre-write backup; it does not claim to meet the old crash-atomic FS-02 wording.
- A disposable directory and Notepad do not isolate arbitrary `node -e`/`npm test` execution or hide other windows in a full-screen capture. P0 uses a fixed compiled C# validator, requires explicit disposable-desktop opt-in, and does not publish a no-auth endpoint. It is a reduced protocol/interaction fixture, not the full source-code repair acceptance test.
- The seven-tool proposal has no Ctrl+S primitive although its GUI task needs one. P0 retains seven names and adds an explicit fixed-key option to `type_text`, rather than smuggling key codes through text.
- Existing browser MCP tools will not automatically inherit this server's grants, annotations or idempotency. Their integration and exposed effects remain explicit work.
- A hand-written OAuth demo is not selected as production authorization merely because it is short. P0 is loopback/private-test only; a tested standards-based OAuth path is still needed for a public endpoint. No account, token or IdP was provisioned.
- Fewer than ten calls in one successful trial does not by itself falsify the product premise. Record workload completion and why it stopped; do not optimize call count by padding the loop.

## What this change supplies

One executable project: official MCP HTTP transport, seven attributed tools, server instructions toggle, real primary-screen capture and selected Notepad input code, conditional file writes, fixed child commands, operation retry/polling, and a SQLite invocation ledger. The embedded `--self-test` exercises actual file/DB/child-process and HTTP behavior when built; CI targets Linux and Windows separately. Native desktop interactions remain a local interactive experiment.

M-1–M-8: **BLOCKED_EXTERNAL; values not measured**. Required evidence is a user-authorized Windows desktop and the exact target Chat model/connector. No screenshots or timing results are fabricated.

Secure MCP Tunnel: official documentation describes private developer-mode connections and separate Platform Tunnels permissions/runtime credentials. User-organization entitlement, Windows binary execution, and traffic fees are **unverified**. Documentation alone is not a successful connection or evidence of free usage.

## Reference documentation consulted

- Official C# SDK: https://github.com/modelcontextprotocol/csharp-sdk
- Package selected for this probe: https://www.nuget.org/packages/ModelContextProtocol.AspNetCore/2.2.0
- Developer mode: https://developers.openai.com/api/docs/guides/developer-mode
- Secure MCP Tunnel: https://developers.openai.com/api/docs/guides/secure-mcp-tunnels
- Windows sharing semantics: https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare

## Non-guarantees

This is not v1, a sandbox, production authentication, crash-atomic multi-file storage, guaranteed exactly-once OS effects, or guaranteed continuation of Chat reasoning. It does not implement arbitrary shell, UIA, a tray UI, browser MCP composition, or the complete 23-tool v1 surface. Source creation and CI success do not establish Pro/Thinking behavior or interactive Windows GUI success.
