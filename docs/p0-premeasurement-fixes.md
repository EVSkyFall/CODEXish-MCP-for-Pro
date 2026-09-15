# P0 pre-measurement fixes — review follow-up

Date: 2026-09-15. Baseline: `0dd0b6ba1f22cd4bf6550cbdcc5b1a0db89eca34`, code `bdb4bf223455dd770709308ab33cb131d6a9eab4`. Target: existing PR #2 branch `feat/p0-review-response-20260915`.

**Delivery status: LOCAL_PATCH_ONLY.** The GitHub tree write was blocked by the platform security decision step. No new commit, CI run, tunnel, or user-PC execution was performed. The local source and patch are supplied for application and build; these fixes are not yet runtime-verified.

## Review decision and evidence provenance

The user supplied `CODEXish-P0-verification-by-Claude.md` (reviewer label Claude Fable 5.1). Its decision is **APPROVE for P0 scope**, conditional on F-1 and F-2 before measurement. The overall design remains **CHANGES_REQUESTED** until M-1–M-8 are recorded. This is an uploaded review, not a GitHub review submitted by this assistant on the reviewer's behalf.

The reviewer reports a Windows 11 Pro 10.0.26200 local restore/build, 48 passing self-tests and HTTP smoke on .NET SDK 10.0.401. The smoke includes a real failing test child, a sleep child continuing past response wait, operation inspection and disabled-desktop errors. Those are **reviewer-reported real-device results**, not new tests run by this assistant. Interactive Notepad input/capture and Pro/Thinking measurements remain unperformed in the report.

## F-1 — accepted and implemented

Repeated `--allow-host` and `--allow-origin`, exact allowlists, unchanged loopback binding, JSON-escaped `rejected host=... origin=... reason=...` stdout. No automatic trust of rejected headers, wildcard hosts, or forwarded Host headers. README includes both reviewer-provided rewrite commands and the distinction from authentication. No tunnel is started by this patch.

## F-2 — accepted and implemented

Default PNG maximum width 1280, native width at 0, no upscaling, real independent X/Y ratios after rounded image sizing. Click uses the observation's immutable transform. An explicit required `coordinate_space` selects `image` (recommended) or `primary_monitor_physical_px`; stale client schemas must refresh instead of silently reinterpreting old coordinates. Coordinate space participates in the invocation digest. Screenshot dimensions and encoded byte size are visible in metadata.

## Tests and deferred findings

Regression tests cover repeated/malformed options, real HTTP Host/Origin accept/reject paths and diagnostics, MCP initialize/list/call with a forwarded public Host, SDK schemas, resize geometry and coordinate boundaries. Windows CI additionally runs the production PNG encoder on **artificial bitmap fixtures**. Those fixtures are neither fake screenshot tool results nor desktop/Chat evidence. Actual execution results are tracked in IMPLEMENTATION_STATUS.md.

C-1–C-7 remain v1 follow-ups as requested. P0 instructions now require hands off input devices during GUI measurement (C-1) and retain the toy-loop limitation (C-7). Job Object, queued recovery semantics, completed-operation cleanup and retryable error changes are not bundled into these two fixes. The single-package architecture and seven tool names are unchanged.

The revised design review corrects the header to 8 measurements and the list to 23 tools. Its §6 item 1 still says M-1–M-6; the actual measurement scope is M-1–M-8, consistent with both review headers. No measurement value is inferred from CI or from the local smoke report.

## Implementation references

The review defines scope. Microsoft API documentation was consulted only for host matching and the image scaling implementation:
- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/host-filtering?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/dotnet/desktop/winforms/advanced/how-to-use-interpolation-mode-to-control-image-quality-during-scaling

Uploaded verification document SHA-256: `fdf14283cc035f495c379a7a1f16c067de7332d44cf342583db55be12f0a260a`.
