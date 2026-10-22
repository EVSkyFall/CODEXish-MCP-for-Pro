using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

// Thrown by tool implementations to produce a structured error envelope without unwinding to a generic failure.
public sealed class CodexishFault(string code, string message, string effects = "none", object? details = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public string Effects { get; } = effects;
    public object? Details { get; } = details;
}

// Protocol frame and page budgets. Every value is reported with its source so the model can tell a
// transport budget from a product limit; nothing here rejects a valid request, it only paginates.
public static class Limits
{
    public const int ReadBytes = 262144;
    public const int PreviewBytes = 8192;
    public const int ListEntries = 500;
    public const int SearchMatches = 200;
    public const int ArtifactBytes = 262144;
    public const string Source = "server response frame budget (single process default, not a product cap)";

    public static object Describe() => new
    {
        max_read_bytes = ReadBytes,
        max_artifact_read_bytes = ArtifactBytes,
        stream_preview_bytes = PreviewBytes,
        max_list_entries = ListEntries,
        max_search_matches = SearchMatches,
        source = Source,
        note = "Exceeding a page returns next_cursor; no data is discarded and no call is refused."
    };
}

public static class Reply
{
    public const string SchemaVersion = "v1.0";
    public const string Boundary = "unconfined_user";

    public static CallToolResult Ok(object data, object? output = null) => Pack("succeeded", data, output, null);
    public static CallToolResult Status(string status, object? data, object? output = null) => Pack(status, data, output, null);
    public static CallToolResult Running(string operationId, object? extra = null) =>
        Pack("running", Merge(new { operation_id = operationId }, extra), null, null);
    public static CallToolResult Queued(string operationId, bool paused, object? extra = null) =>
        Pack("queued", Merge(new { operation_id = operationId, paused }, extra), null, null);

    public static CallToolResult Error(string code, string message, string effects = "none",
        object? details = null, object? data = null, string? reason = null, string? recovery = null)
    {
        string status = code switch
        {
            "EXECUTION_UNKNOWN" => "unknown",
            "CANCELLED" => "cancelled",
            _ => "failed"
        };
        return Pack(status, data, null, new
        {
            code,
            message,
            retryable = false,
            side_effects = effects,
            reason,
            details,
            recovery = new { action = recovery ?? "inspect_state_then_replan", replay = "never_replay_unknown_effects" }
        });
    }

    public static CallToolResult Fault(CodexishFault fault) =>
        Error(fault.Code, fault.Message, fault.Effects, fault.Details);

    private static object? Merge(object first, object? extra)
    {
        if (extra is null) return first;
        var merged = new Dictionary<string, JsonElement>();
        foreach (var part in new[] { first, extra })
            foreach (var property in JsonSerializer.SerializeToElement(part).EnumerateObject())
                merged[property.Name] = property.Value;
        return merged;
    }

    private static CallToolResult Pack(string status, object? data, object? output, object? error)
    {
        var body = JsonSerializer.SerializeToElement(new
        {
            schema_version = SchemaVersion,
            status,
            data,
            output,
            error,
            execution_boundary = Boundary
        });
        return new CallToolResult
        {
            IsError = error is not null,
            StructuredContent = body,
            Content = [new TextContentBlock { Text = body.GetRawText() }]
        };
    }

    // Synchronous tool bodies funnel through this so a thrown fault never degrades into a protocol error.
    public static CallToolResult Guard(Func<CallToolResult> action)
    {
        try { return action(); }
        catch (CodexishFault e) { return Fault(e); }
        catch (FileNotFoundException) { return Error("NOT_FOUND", "The target file does not exist inside the granted root."); }
        catch (DirectoryNotFoundException) { return Error("NOT_FOUND", "The target directory does not exist inside the granted root."); }
        catch (UnauthorizedAccessException) { return Error("PERMISSION_DENIED", "Windows denied access to the target with the server's own user rights."); }
        catch (IOException) { return Error("IO_ERROR", "A local I/O operation failed; inspect the target and retry as a new request."); }
        catch (Exception) { return Error("EXECUTION_FAILED", "The tool failed before a confirmed result; inspect local state. No automatic replay."); }
    }

    public static async Task<CallToolResult> GuardAsync(Func<Task<CallToolResult>> action)
    {
        try { return await action(); }
        catch (CodexishFault e) { return Fault(e); }
        catch (FileNotFoundException) { return Error("NOT_FOUND", "The target file does not exist inside the granted root."); }
        catch (DirectoryNotFoundException) { return Error("NOT_FOUND", "The target directory does not exist inside the granted root."); }
        catch (UnauthorizedAccessException) { return Error("PERMISSION_DENIED", "Windows denied access to the target with the server's own user rights."); }
        catch (IOException) { return Error("IO_ERROR", "A local I/O operation failed; inspect the target and retry as a new request."); }
        catch (Exception) { return Error("EXECUTION_FAILED", "The tool failed before a confirmed result; inspect local state. No automatic replay."); }
    }

    public static string? StatusOf(CallToolResult result) =>
        result.StructuredContent?.GetProperty("status").GetString();

    public static string? CodeOf(CallToolResult result)
    {
        var error = result.StructuredContent?.GetProperty("error");
        return error is null || error.Value.ValueKind == JsonValueKind.Null ? null : error.Value.GetProperty("code").GetString();
    }
}
