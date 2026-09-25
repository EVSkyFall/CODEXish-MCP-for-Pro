using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

// Handed to every effect so it can observe cancellation and, for process starts, release the resource
// chain as soon as the child exists instead of when it exits.
public sealed class Job(string id, CancellationToken token, TaskCompletionSource start)
{
    public string Id { get; } = id;
    public CancellationToken Token { get; } = token;
    public void Started() => start.TrySetResult();
}

// D9. Idempotency key = invocation_id, digest = SHA-256(tool + JSON args). Acceptance order is the order of
// the ledger insert under this gate, and each resource runs its accepted work in exactly that order.
public sealed class Ledger : IDisposable
{
    private readonly Store store;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly Dictionary<string, Task<CallToolResult>> live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> chains = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CallToolResult> persistFailed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> cancellations = new(StringComparer.Ordinal);
    private readonly HashSet<string> cancelRequested = new(StringComparer.Ordinal);
    private TaskCompletionSource resume = Released();
    private bool stopping;

    public Ledger(Store store, Action<string>? log = null)
    {
        this.store = store;
        this.log = log ?? Console.Error.WriteLine;
    }

    public bool Paused { get; private set; }

    private static TaskCompletionSource Released()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    // D11: pause holds the dequeuers. Accepted work stays accepted and keeps its place in the queue.
    public void Pause()
    {
        lock (gate)
        {
            if (Paused) return;
            Paused = true;
            resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        store.Event("control_pause", null, new { paused = true });
    }

    public void Resume()
    {
        TaskCompletionSource? released = null;
        lock (gate)
        {
            if (!Paused) return;
            Paused = false;
            released = resume;
            resume = Released();
        }
        released.TrySetResult();
        store.Event("control_resume", null, new { paused = false });
    }

    public static string Digest(string tool, object args) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool + "\n" + JsonSerializer.Serialize(args)))).ToLowerInvariant();

    public async Task<CallToolResult> Invoke(string id, string tool, object args, string resource,
        Func<Job, Task<CallToolResult>> action, int waitMs, bool releaseChainOnStart = false)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            return Reply.Error("INVALID_ARGUMENT", "Supply an invocation_id of 1-128 characters; reuse it only to retry the same action.");
        if (waitMs < 0)
            return Reply.Error("INVALID_ARGUMENT", "wait_ms must be zero or greater. It is a response wait, never an execution deadline.");
        string digest = Digest(tool, args);
        try { return await Accept(id, tool, args, resource, action, waitMs, releaseChainOnStart, digest); }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            // The call was never recorded, so nothing ran; the same call can simply be sent again.
            try { log($"ledger_unavailable {id}: {error.Message}"); } catch (IOException) { }
            return Reply.Error("EXECUTION_FAILED", "The ledger could not record this call, so nothing was started. Send the same call again.",
                details: new { operation_id = id, ledger_error = error.Message });
        }
    }

    private async Task<CallToolResult> Accept(string id, string tool, object args, string resource,
        Func<Job, Task<CallToolResult>> action, int waitMs, bool releaseChainOnStart, string digest)
    {
        Task<CallToolResult> task;
        bool pausedAtAcceptance;
        lock (gate)
        {
            if (stopping) return Reply.Error("CANCELLED", "The local server is stopping; nothing was started.");
            var row = store.Invocation(id);
            if (row is not null)
            {
                if (row.Status == Store.Unreadable) return Unreadable(id);
                if (row.Digest != digest)
                    return Reply.Error("IDEMPOTENCY_CONFLICT",
                        "This invocation_id was accepted with different arguments. Inspect it, or use a new id for a different action.",
                        details: new { operation_id = id, recorded_tool = row.Tool });
                if (persistFailed.TryGetValue(id, out var unknown)) return unknown;
                if (row.Result is not null) return Stored(id, row.Result);
                if (!live.TryGetValue(id, out task!)) return Recovered(row.Status, id);
            }
            else
            {
                store.InsertInvocation(id, digest, tool);
                // The row above is the acceptance; a diagnostic event that cannot be written must not undo it.
                try { store.Event("accepted", id, new { tool, resource, digest }); }
                catch (Microsoft.Data.Sqlite.SqliteException) { }
                var proxy = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancellation = new CancellationTokenSource();
                cancellations[id] = cancellation;
                task = proxy.Task;
                live[id] = task;
                Task previous = chains.TryGetValue(resource, out var tail) ? tail : Task.CompletedTask;
                // Continuations attach to the resource chain in the same order the rows were inserted,
                // so the dequeuer starts accepted work in acceptance order (Codex finding M-2).
                Task link = previous.ContinueWith(_ => Link(id, tool, action, proxy, started, cancellation, releaseChainOnStart),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                chains[resource] = link;
            }
            pausedAtAcceptance = Paused;
        }
        if (pausedAtAcceptance) return Reply.Queued(id, true, new { hold = "control_pause", resume_with = "POST /control/resume on loopback" });
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(waitMs))) == task) return await task;
        // A response wait is not the lifetime of the accepted operation.
        return Reply.Running(id);
    }

    private async Task Link(string id, string tool, Func<Job, Task<CallToolResult>> action,
        TaskCompletionSource<CallToolResult> proxy, TaskCompletionSource started,
        CancellationTokenSource cancellation, bool releaseChainOnStart)
    {
        Task resumeTask;
        lock (gate) resumeTask = resume.Task;
        await resumeTask;
        bool cancelled;
        string? unrecorded = null;
        lock (gate)
        {
            cancelled = cancelRequested.Remove(id) || stopping;
            // The start is recorded before the effect runs; if that record cannot be written the effect is not started,
            // so a restart can never mistake a real effect for queued work that never ran.
            if (!cancelled)
                try { store.UpdateInvocationStatus(id, "running"); }
                catch (Microsoft.Data.Sqlite.SqliteException error) { unrecorded = error.Message; }
        }
        if (cancelled)
        {
            Finish(id, Reply.Error("CANCELLED", "Cancelled while queued; the effect never started.", recovery: "no_effects_to_inspect"), proxy);
            return;
        }
        if (unrecorded is not null)
        {
            Finish(id, Reply.Error("EXECUTION_FAILED", "The ledger could not record the start of this operation, so it was not started. " +
                "Send it again with a new invocation_id.", details: new { operation_id = id, ledger_error = unrecorded }), proxy);
            return;
        }
        Task<CallToolResult> effect = Execute(id, tool, action, proxy, started, cancellation);
        // Shell and process starts release the chain once the child exists; file and git effects hold it
        // until they finish, so a later write to the same root cannot overtake an earlier one.
        await Task.WhenAny(effect, releaseChainOnStart ? started.Task : effect);
    }

    private async Task<CallToolResult> Execute(string id, string tool, Func<Job, Task<CallToolResult>> action,
        TaskCompletionSource<CallToolResult> proxy, TaskCompletionSource started, CancellationTokenSource cancellation)
    {
        CallToolResult result;
        try
        {
            result = await action(new Job(id, cancellation.Token, started));
        }
        catch (CodexishFault fault) { result = Reply.Fault(fault); }
        catch (OperationCanceledException)
        {
            result = Reply.Error("CANCELLED", "The operation was cancelled after it started; inspect the target for partial effects.", "unknown");
        }
        catch (Exception e)
        {
            log($"{tool}: {e.GetType().Name}");
            result = Reply.Error("EXECUTION_UNKNOWN", "Execution failed without a confirmed effect; inspect before replanning.", "unknown");
        }
        return Finish(id, result, proxy);
    }

    // The committed result is decided here. A failure of the final UPDATE, or of any diagnostic logging,
    // must never turn a real effect into a replayable request (Codex finding M-1).
    private CallToolResult Finish(string id, CallToolResult result, TaskCompletionSource<CallToolResult> proxy)
    {
        CallToolResult reported = result;
        try
        {
            store.CompleteInvocation(id, Reply.StatusOf(result) ?? "unknown", JsonSerializer.Serialize(result));
        }
        catch (Exception e)
        {
            lock (gate) persistFailed[id] = reported = PersistFailed(id, result);
            try { log($"persist_failed {id}: {e.GetType().Name}"); } catch (IOException) { }
            try { store.Event("persist_failed", id, new { error = e.GetType().Name }); } catch (Exception) { }
        }
        try { store.Event("finished", id, new { status = Reply.StatusOf(reported) }); } catch (Exception) { }
        lock (gate)
        {
            live.Remove(id);
            if (cancellations.Remove(id, out var cancellation)) cancellation.Dispose();
        }
        proxy.TrySetResult(reported);
        return reported;
    }

    private static CallToolResult PersistFailed(string id, CallToolResult result) =>
        Reply.Error("EXECUTION_UNKNOWN",
            "The effect completed but its result could not be stored, so this operation is not durable. " +
            "Inspect the actual effect; do not replay it.",
            "applied",
            details: new { operation_id = id },
            data: new { operation_id = id, result = result.StructuredContent },
            reason: "persist_failed",
            recovery: "inspect effects; do not replay");

    // A stored record that cannot be read affects only the response that needed it.
    private static CallToolResult Stored(string id, string json)
    {
        try
        {
            if (JsonSerializer.Deserialize<CallToolResult>(json) is { StructuredContent: not null } result) return result;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or ArgumentException) { }
        return Unreadable(id);
    }

    private static CallToolResult Unreadable(string id) =>
        Reply.Error("EXECUTION_UNKNOWN",
            "The stored record of this operation cannot be read, so its outcome is unconfirmed here. Inspect the target; do not replay it.",
            "unknown", details: new { operation_id = id }, reason: "stored_result_unreadable", recovery: "inspect effects; do not replay");

    private static CallToolResult Recovered(string status, string id) => status switch
    {
        "cancelled" => Reply.Error("CANCELLED", "This operation was queued when the server restarted and never ran.",
            recovery: "no_effects_to_inspect"),
        "unknown" => Reply.Error("EXECUTION_UNKNOWN",
            "This operation was running when the server restarted; its effect is unconfirmed.", "unknown",
            details: new { operation_id = id }, reason: "restart_interrupted", recovery: "inspect effects; do not replay"),
        "queued" => Reply.Queued(id, false),
        _ => Reply.Running(id)
    };

    public CallToolResult Inspect(string id)
    {
        lock (gate)
        {
            var row = store.Invocation(id);
            if (row is null)
                return Reply.Error("NOT_FOUND", "Unknown operation_id. Only accepted invocations are recorded.");
            if (row.Status == Store.Unreadable) return Unreadable(id);
            if (persistFailed.TryGetValue(id, out var unknown)) return unknown;
            if (row.Result is not null) return Stored(id, row.Result);
            if (row.Status == "queued") return Reply.Queued(id, Paused);
            if (row.Status == "running") return Reply.Running(id, new { tool = row.Tool });
            return Recovered(row.Status, id);
        }
    }

    // Cancel is a request about one operation, not a rollback of effects that already happened.
    public CallToolResult Cancel(string id)
    {
        CancellationTokenSource? cancellation = null;
        lock (gate)
        {
            var row = store.Invocation(id);
            if (row is null) return Reply.Error("NOT_FOUND", "Unknown operation_id; nothing was cancelled.");
            if (row.Status == Store.Unreadable) return Unreadable(id);
            if (persistFailed.ContainsKey(id) || row.Result is not null)
                return Reply.Ok(new { operation_id = id, cancelled = false, state = row.Status, reason = "already_terminal" });
            if (row.Status == "queued")
            {
                cancelRequested.Add(id);
                store.Event("cancel_requested", id, new { state = "queued" });
                return Reply.Ok(new { operation_id = id, cancelled = true, state = "queued", side_effects = "none",
                    next_tool = "operation_inspect" });
            }
            cancellations.TryGetValue(id, out cancellation);
        }
        cancellation?.Cancel();
        store.Event("cancel_requested", id, new { state = "running" });
        return Reply.Ok(new
        {
            operation_id = id,
            cancelled = false,
            state = "running",
            requested = true,
            side_effects = "unknown",
            note = "Cancellation was requested. Effects already applied are not undone.",
            next_tool = "operation_inspect"
        });
    }

    public void Dispose()
    {
        Task<CallToolResult>[] tasks;
        TaskCompletionSource released;
        lock (gate)
        {
            if (stopping) return;
            stopping = true;
            tasks = live.Values.ToArray();
            released = resume;
            Paused = false;
            resume = Released();
        }
        released.TrySetResult();
        try { Task.WaitAll(tasks); } catch (AggregateException) { }
    }
}
