# CODEXish MCP for Pro — P0 probe

A single C#/.NET 10 Streamable HTTP application for measuring whether the selected Chat Pro model can actually use tools and observe/control a disposable Windows Notepad. This branch responds to the independent CHANGES_REQUESTED review of PR #1. It is **P0**, not the 23-tool v1 product.

## Build and run

Install the .NET 10 SDK. From the repository root:

```powershell
dotnet restore src/Codexish.P0/Codexish.P0.csproj
dotnet run --project src/Codexish.P0 -- --self-test
dotnet run --project src/Codexish.P0
```

The last command creates a new disposable fixture, prints its path, and listens at `http://127.0.0.1:3000/mcp`. It exposes seven tools: `echo`, `read_file`, `write_file`, `run_command`, `screenshot`, `click`, `type_text`. The official SDK generates their JSON Schemas. `--no-instructions` turns off server instructions for an A/B trial.

`echo` lists six fixture files. Each must contain its ordinal integer (case01.txt → 1, etc.). `run_command` accepts only `test`, `sleep`, or `inspect`: a fixed C# child validator, a fixed 60-second child, or operation lookup. This is a tool-loop probe, **not yet an arbitrary source-code repair benchmark**. No caller-supplied shell or JavaScript is executed. Test failure is represented by `test_passed=false` and the real nonzero exit code; a successfully collected process result does not mean the test passed.

Writes require the hash returned by `read_file`. They use one exclusive handle and retain byte encoding, BOM, and homogeneous LF/CRLF. Old bytes are saved under the sibling `.state` directory before a **non-atomic in-place** write. The SQLite invocation ledger prevents replay of completed effects and marks unfinished operations unknown after restart. `wait_ms` is only a response wait, not a process deadline. Use `run_command(command="inspect", operation_id=...)` to query pending effects.

## Actual Windows desktop probe

Use a disposable Windows login/VM desktop with no private windows or credentials. Open Notepad yourself. Identify the PID of the Notepad window being tested, put it on the primary monitor, and keep it in the foreground. Restart the probe with the previously printed fixture path:

```powershell
dotnet run --project src/Codexish.P0 -- --resume "C:\path\printed-by-probe" --notepad-pid 1234 --disposable-desktop
```

The acknowledgement flag does not create an OS sandbox. The screenshot captures the **entire primary monitor**. Input checks select only the configured Notepad process; they do not make the desktop or filesystem a security boundary. No administrator elevation, clipboard access, or synthetic screen fallback is used.

During P0 GUI steps, do not touch the mouse or keyboard: the existing last-input check invalidates an observation after local input (review C-1). Take a screenshot before each action and afterward. `type_text` accepts exactly one of literal `text` or `key` (`CTRL+S`, `CTRL+A`, `ENTER`, `ESC`). Use the save path returned by `echo`. Verify the saved result with `read_file("gui-result.txt")`; this file cannot be written with `write_file`, so the tool cannot forge GUI success. UIA, other monitors and mixed-DPI acceptance testing are deferred.

## F-1: tunnel Host and Origin configuration

The listener stays on `127.0.0.1`. `--allow-host` adds one exact hostname (no scheme, port, wildcard, or suffix match); repeat it for multiple names. Loopback hosts remain allowed. `--allow-origin` separately adds an exact HTTP(S) origin, including a nondefault port when applicable; repeat it as needed. Requests without Origin do not need an origin allowance. A host allowance never disables Origin checks.

```powershell
dotnet run --project src/Codexish.P0 -c Release -- --resume "C:\path\printed-by-probe" --notepad-pid 1234 --disposable-desktop --port 3000 --allow-host example.trycloudflare.com
```

On 403, stdout now prints `rejected host="..." origin="..." reason=...`. Check the value against the intended client before adding `--allow-origin https://expected-client.example`. Never copy arbitrary rejected headers into an allowlist automatically. Header values are escaped in logs; no authorization headers are logged.

When preserving local Host instead of using `--allow-host`, these are the review's Host-header rewrite commands:

```powershell
cloudflared tunnel --url http://127.0.0.1:3000 --http-host-header 127.0.0.1:3000
ngrok http 3000 --host-header=localhost:3000
```

Choose one tunnel, not both. These commands only fix Host forwarding; they do not add authentication. Host/Origin checks are not access control for remote clients. Use a private or authenticated tunnel for desktop access. This change neither provisions a tunnel nor changes account credentials. Secure MCP Tunnel organization access, Windows client operation and fees remain unmeasured.

## F-2: scaled screenshots and click units

`screenshot(max_width=1280)` is the default. `max_width=0` returns native resolution; larger values never upscale. Negative values return `INVALID_ARGUMENT`. For a 2560×1440 primary monitor, the default PNG is 1280×720 and both scale factors are 2. This is a geometry example, not a measured Chat payload limit.

The returned top-level `width`/`height` remain **physical** dimensions. `image.width`/`image.height` describe the actual PNG; `image_to_desktop.scale_x/scale_y` use physical dimensions divided by the respective rounded image dimensions. `png_bytes` records encoded size.

**Upgrade note:** refresh the connector's tool schema. `click` now requires `coordinate_space` so cached clients cannot silently change coordinate units. Use `"image"` with the screenshot's `observation_id`; the server converts automatically using that observation's scale (floored to an integer). Do not multiply coordinates yourself. Existing physical coordinates are accepted only with the explicit value `"primary_monitor_physical_px"`.

```json
{"x":640,"y":360,"observation_id":"<latest observation>","invocation_id":"<new action ID>","coordinate_space":"image"}
```

Each observation retains its own immutable transform. Capture again after an action. Invalid or stale coordinates produce no click. The same invocation ID with different coordinate units is an idempotency conflict, not a retry.

## Measurement

Record M-1–M-8 for Pro and Thinking, with and without server/Project instructions. Store the exact model label, prompts, screenshots, confirmation behavior, call count, final files and wall times. Use `echo(delay_60_seconds=true)` for the HTTP-timeout experiment; `run_command sleep` tests a different property: a child continuing past the response wait. Logs in `.state/calls.jsonl` contain tool timing metadata, not model-thinking time or Chat UI behavior.

A 15-call exercise is a target workload, not a guaranteed minimum number of calls or a success metric by itself. Successful early completion is not a failure. The user controls model selection and connector attachment; server-side tests cannot establish those observations.

See [review response](docs/review-response.md) and [actual status](IMPLEMENTATION_STATUS.md). Historical v0.1 docs describe a superseded proposal, not implemented services.
