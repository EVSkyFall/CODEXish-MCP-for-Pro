using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Codexish.P0;

[McpServerToolType]
public sealed class ProbeTools(ProbeRuntime runtime)
{
    [McpServerTool(Name = "echo", ReadOnly = true, OpenWorld = false)]
    [Description("Start here to see fixture filenames and the GUI save path. Optional delay_60_seconds measures a real 60-second HTTP wait, not a process deadline.")]
    public async Task<CallToolResult> Echo(string message, bool delay_60_seconds = false, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        runtime.Log("echo", "called");
        try
        {
            if (delay_60_seconds) await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            return Reply.Ok(new { message, files = ProbeRuntime.Names, gui_save_path = Path.Combine(runtime.Root, "gui-result.txt"),
                elapsed_ms = watch.Elapsed.TotalMilliseconds, execution_boundary = "unconfined_user", fixture_only = true });
        }
        finally { runtime.Log("echo", "finished_or_cancelled", watch.Elapsed.TotalMilliseconds); }
    }

    [McpServerTool(Name = "read_file", ReadOnly = true, OpenWorld = false)]
    [Description("Read one disposable fixture file, with byte hash. Edit using write_file, then run_command test. gui-result.txt is read-only through MCP so GUI success cannot be forged by write_file.")]
    public CallToolResult ReadFile(string path) => Reply.Guard(() => runtime.Read(path));

    [McpServerTool(Name = "write_file", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Replace one caseNN.txt after verifying expected_sha256 under an exclusive file handle. Preserves encoding/BOM/newlines and stores a backup. This is in-place, NOT crash-atomic. Reuse invocation_id on transport retry; run test afterward.")]
    public Task<CallToolResult> WriteFile(string path, string text, string expected_sha256, string invocation_id) =>
        runtime.Invoke(invocation_id, "write_file", new { path, text, expected_sha256 }, "files",
            () => Task.FromResult(runtime.Write(path, text, expected_sha256)), 1000);

    [McpServerTool(Name = "run_command", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("P0 fixed commands only: test runs a trusted C# fixture validator child; sleep starts a 60-second child; inspect retrieves operation_id. No shell or caller-supplied code. wait_ms returns a handle without killing the child. Poll inspect until completed; verify exit_code, not merely process start.")]
    public Task<CallToolResult> RunCommand(string command, string invocation_id = "", string operation_id = "", int wait_ms = 1000)
    {
        if (command == "inspect") return Task.FromResult(runtime.Inspect(operation_id));
        if (command is not ("test" or "sleep")) return Task.FromResult(Reply.Error("PERMISSION_DENIED", "Only test, sleep, inspect are allowed."));
        return runtime.Invoke(invocation_id, "run_command", new { command }, command == "test" ? "files" : "sleep",
            () => runtime.RunChild(command), wait_ms);
    }

    [McpServerTool(Name = "screenshot", ReadOnly = true, OpenWorld = false)]
    [Description("Capture the REAL primary Windows monitor as PNG image content plus physical pixel size and observation_id. Requires an explicitly selected foreground Notepad on a disposable desktop. No synthetic fallback. Use this before and after each click/type; mixed-DPI and other monitors are outside P0.")]
    public CallToolResult Screenshot() { runtime.Log("screenshot", "called"); return Reply.Guard(runtime.Desktop.Observe); }

    [McpServerTool(Name = "click", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Click primary-monitor physical pixel x,y from the latest screenshot. Foreground Notepad identity, bounds and last-input marker must still match. Always screenshot afterward; STALE_OBSERVATION requires re-observation, not blind retry.")]
    public Task<CallToolResult> Click(int x, int y, string observation_id, string invocation_id) =>
        runtime.Invoke(invocation_id, "click", new { x, y, observation_id }, "desktop",
            () => Task.FromResult(runtime.Desktop.Click(x, y, observation_id)), 1000);

    [McpServerTool(Name = "type_text", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Type literal text OR one allowed key (CTRL+S, CTRL+A, ENTER, ESC) into the selected Notepad/dialog. Supply exactly one of text/key. Observe first and screenshot afterward. Save only to echo's gui_save_path, then read_file gui-result.txt to verify. No clipboard.")]
    public Task<CallToolResult> TypeText(string observation_id, string invocation_id, string? text = null, string? key = null) =>
        runtime.Invoke(invocation_id, "type_text", new { observation_id, text, key }, "desktop",
            () => Task.FromResult(runtime.Desktop.Type(observation_id, text, key)), 1000);
}

public sealed class ProbeFault(string code, string message, string effects = "none") : Exception(message)
{
    public string Code { get; } = code;
    public string Effects { get; } = effects;
}

public static class Reply
{
    public static CallToolResult Ok(object data) => Pack("succeeded", data, null);
    public static CallToolResult Pending(string id) => Pack("running", new { operation_id = id }, null);
    public static CallToolResult Error(string code, string message, string effects = "none") =>
        Pack(code == "EXECUTION_UNKNOWN" ? "unknown" : "failed", null,
            new { code, message, retryable = false, side_effects = effects,
                recovery = new { action = "inspect_or_reobserve_before_replanning" } });
    private static CallToolResult Pack(string status, object? data, object? error)
    {
        var body = JsonSerializer.SerializeToElement(new { schema_version = "p0.2", status, data, error,
            execution_boundary = "unconfined_user" });
        return new() { IsError = error is not null, StructuredContent = body,
            Content = [new TextContentBlock { Text = body.GetRawText() }] };
    }
    public static CallToolResult Guard(Func<CallToolResult> action)
    {
        try { return action(); }
        catch (ProbeFault e) { return Error(e.Code, e.Message, e.Effects); }
        catch (FileNotFoundException) { return Error("NOT_FOUND", "Fixture file does not exist."); }
        catch (IOException) { return Error("IO_ERROR", "I/O failed; inspect local logs."); }
        catch (Exception) { return Error("EXECUTION_FAILED", "Inspect local logs; no automatic replay.", "unknown"); }
    }
}
