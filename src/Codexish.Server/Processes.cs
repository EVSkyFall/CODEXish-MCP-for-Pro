using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

public sealed class ManagedProcess
{
    public required string ProcessId { get; init; }
    public required Process Process { get; init; }
    public required int Pid { get; init; }
    public required long StartTime { get; init; }
    public required string Lifetime { get; init; }
    public required string RootId { get; init; }
    public required string StdoutArtifact { get; init; }
    public required string StderrArtifact { get; init; }
    public required string Display { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public nint JobHandle { get; set; }
    public Task? Collection { get; set; }
    public volatile string State = "running";
    public int? ExitCode { get; set; }
    public bool StdinClosed { get; set; }
}

// D7. One supervisor for shell_run and process_start. wait_ms only bounds the response; nothing here kills a
// child because a caller stopped waiting.
public sealed class ProcessSupervisor(Store store, Artifacts artifacts, ServerConfig config) : IDisposable
{
    private readonly ConcurrentDictionary<string, ManagedProcess> processes = new(StringComparer.Ordinal);
    private bool disposed;

    public IReadOnlyCollection<ManagedProcess> Live => processes.Values.ToArray();

    public ManagedProcess Start(Target cwd, string? command, string? shell, string? executable, string[]? args, string lifetime)
    {
        if (lifetime is not ("session" or "persistent"))
            throw new CodexishFault("INVALID_ARGUMENT", "lifetime must be session or persistent.");
        var start = new ProcessStartInfo
        {
            WorkingDirectory = cwd.FullPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        string display;
        if (!string.IsNullOrEmpty(executable))
        {
            if (!string.IsNullOrEmpty(command))
                throw new CodexishFault("INVALID_ARGUMENT", "Supply either executable+args or command+shell, not both.");
            start.FileName = executable;
            foreach (string argument in args ?? []) start.ArgumentList.Add(argument);
            display = executable + (args is { Length: > 0 } ? " " + string.Join(' ', args) : "");
        }
        else if (!string.IsNullOrEmpty(command))
        {
            if (string.IsNullOrEmpty(shell))
                throw new CodexishFault("INVALID_ARGUMENT",
                    $"A command string requires an explicit shell. Allowed: {string.Join(", ", config.Shell.Allowed)}.");
            if (!config.Shell.Allowed.Contains(shell, StringComparer.Ordinal))
                throw new CodexishFault("PERMISSION_DENIED",
                    $"shell '{shell}' is not in shell.allowed ({string.Join(", ", config.Shell.Allowed)}).");
            if (shell == "pwsh")
            {
                start.FileName = "pwsh";
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add(command);
            }
            else
            {
                // cmd.exe does not follow CommandLineToArgvW quoting; /s /c "..." passes the rest verbatim.
                start.FileName = "cmd.exe";
                start.Arguments = "/s /c \"" + command + "\"";
            }
            display = shell + ": " + command;
        }
        else throw new CodexishFault("INVALID_ARGUMENT", "Supply executable+args, or command with an explicit shell.");

        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new CodexishFault("EXECUTION_FAILED", "The child process could not be started.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            process.Dispose();
            throw new CodexishFault("NOT_FOUND", $"Could not start '{start.FileName}': {e.Message}");
        }
        // The job is assigned immediately after Start. A grandchild spawned inside this very short window
        // could escape supervision; that race is not closed here (CREATE_SUSPENDED would be needed).
        nint job = 0;
        if (lifetime == "session")
        {
            job = Native.CreateKillOnCloseJob();
            if (job != 0 && !Native.AssignProcess(job, process.Handle)) { Native.CloseJob(job); job = 0; }
        }
        long startTime;
        try { startTime = process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception) { startTime = DateTime.UtcNow.Ticks; }

        var stdout = artifacts.Create("text/plain", "stdout", cwd.Root.Id);
        var stderr = artifacts.Create("text/plain", "stderr", cwd.Root.Id);
        var managed = new ManagedProcess
        {
            ProcessId = "proc_" + Guid.NewGuid().ToString("N"),
            Process = process,
            Pid = process.Id,
            StartTime = startTime,
            Lifetime = lifetime,
            RootId = cwd.Root.Id,
            StdoutArtifact = stdout.Id,
            StderrArtifact = stderr.Id,
            Display = display,
            StartedUtc = DateTimeOffset.UtcNow,
            JobHandle = job
        };
        processes[managed.ProcessId] = managed;
        Persist(managed);
        store.Event("process_start", managed.ProcessId, new { pid = managed.Pid, lifetime = managed.Lifetime, root = cwd.Root.Id, display });
        managed.Collection = Collect(managed);
        return managed;
    }

    private void Persist(ManagedProcess managed) => store.UpsertProcess(new ProcessRow(managed.ProcessId, managed.Pid,
        managed.StartTime, managed.State, managed.ExitCode, managed.Lifetime, managed.RootId,
        managed.StdoutArtifact, managed.StderrArtifact));

    private async Task Collect(ManagedProcess managed)
    {
        Task stdout = Pump(managed.Process.StandardOutput.BaseStream, managed.StdoutArtifact);
        Task stderr = Pump(managed.Process.StandardError.BaseStream, managed.StderrArtifact);
        try { await managed.Process.WaitForExitAsync(); }
        catch (Exception) { /* state is reconciled below */ }
        try { await Task.WhenAll(stdout, stderr); } catch (Exception) { }
        FinishIfCollecting(managed.StdoutArtifact);
        FinishIfCollecting(managed.StderrArtifact);
        try { managed.ExitCode = managed.Process.ExitCode; managed.State = "exited"; }
        catch (Exception) { managed.State = "exited_unknown_code"; }
        Persist(managed);
        store.Event("process_exit", managed.ProcessId, new { state = managed.State, exit_code = managed.ExitCode });
    }

    // A collected stream is finalized only if nothing marked it incomplete first.
    private void FinishIfCollecting(string artifactId)
    {
        try
        {
            if (store.Artifact(artifactId)?.Complete == Artifacts.Collecting) artifacts.Finish(artifactId);
        }
        catch (Exception) { }
    }

    private async Task Pump(Stream source, string artifactId)
    {
        byte[] buffer = new byte[16384];
        // FileShare.Read on the writer is what lets process_poll and artifact_read follow a live process.
        using var sink = artifacts.OpenAppend(artifactId);
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer);
                if (read == 0) return;
                await sink.WriteAsync(buffer.AsMemory(0, read));
                await sink.FlushAsync();
            }
        }
        catch (Exception)
        {
            // The producer stopped being readable before the stream ended: the preserved prefix stays readable.
            artifacts.MarkIncomplete(artifactId, "stream_read_failed");
        }
    }

    public ManagedProcess Lookup(string processId) => processes.TryGetValue(processId, out var managed)
        ? managed
        : throw new CodexishFault("NOT_FOUND",
            store.Process(processId) is null
                ? $"Unknown process_id '{processId}'."
                : $"Process '{processId}' belongs to an earlier run of this server; its recorded state is in workspace_info.");

    // A PID alone is not an identity: a stable handle is the GUID bound to PID and process start time.
    public static bool SameProcess(ProcessRow row, int pid, long startTime) => row.Pid == pid && row.StartTime == startTime;

    public static string Cursor(long stdout, long stderr) =>
        ServerConfig.Base64Url(Encoding.UTF8.GetBytes($"p1.{stdout}.{stderr}"));

    public static (long Stdout, long Stderr) ParseCursor(string cursor)
    {
        string padded = cursor.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        string[] parts;
        try { parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('.'); }
        catch (FormatException) { throw new CodexishFault("CURSOR_INVALID", "The cursor is not a cursor issued by this server."); }
        if (parts.Length != 3 || parts[0] != "p1" || !long.TryParse(parts[1], out long stdout) || !long.TryParse(parts[2], out long stderr))
            throw new CodexishFault("CURSOR_INVALID", "The cursor is not a process cursor issued by this server.");
        return (stdout, stderr);
    }

    public async Task<CallToolResult> Poll(string processId, string? cursor, int waitMs, CancellationToken token)
    {
        var managed = Lookup(processId);
        var (stdoutAt, stderrAt) = cursor is { Length: > 0 } ? ParseCursor(cursor) : (0L, 0L);
        long stdoutLength = artifacts.Length(managed.StdoutArtifact), stderrLength = artifacts.Length(managed.StderrArtifact);
        if (stdoutAt > stdoutLength || stderrAt > stderrLength)
            throw new CodexishFault("CURSOR_INVALID",
                "This cursor is past the collected output; the stream was replaced. Poll again from the start.",
                details: new { stdout_bytes = stdoutLength, stderr_bytes = stderrLength });
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(0, waitMs));
        while (stdoutAt == stdoutLength && stderrAt == stderrLength && managed.State == "running"
               && DateTimeOffset.UtcNow < deadline && !token.IsCancellationRequested)
        {
            await Task.Delay(50, CancellationToken.None);
            stdoutLength = artifacts.Length(managed.StdoutArtifact);
            stderrLength = artifacts.Length(managed.StderrArtifact);
        }
        int budget = Limits.ArtifactBytes / 2;
        byte[] outBytes = artifacts.ReadBytes(managed.StdoutArtifact, stdoutAt, budget);
        byte[] errBytes = artifacts.ReadBytes(managed.StderrArtifact, stderrAt, budget);
        var (outText, outRedacted, outKinds) = Redaction.Apply(Encoding.UTF8.GetString(outBytes));
        var (errText, errRedacted, errKinds) = Redaction.Apply(Encoding.UTF8.GetString(errBytes));
        long nextOut = stdoutAt + outBytes.Length, nextErr = stderrAt + errBytes.Length;
        bool drained = nextOut >= stdoutLength && nextErr >= stderrLength;
        return Reply.Ok(new
        {
            process_id = managed.ProcessId,
            pid = managed.Pid,
            state = managed.State,
            running = managed.State == "running",
            exit_code = managed.ExitCode,
            lifetime = managed.Lifetime,
            stdout = outText,
            stderr = errText,
            stdout_bytes = outBytes.Length,
            stderr_bytes = errBytes.Length,
            stdout_total = stdoutLength,
            stderr_total = stderrLength,
            stdout_artifact = managed.StdoutArtifact,
            stderr_artifact = managed.StderrArtifact,
            redacted = outRedacted || errRedacted,
            redacted_kinds = outKinds.Concat(errKinds).Distinct().ToArray(),
            stream_order = "bytes are preserved in order within each stream; the two streams are not interleaved",
            limits_source = Limits.Source,
            next_tool = managed.State == "running" ? "process_poll with next_cursor" : "artifact_read for the full output"
        }, new { text = outText, next_cursor = Cursor(nextOut, nextErr), complete = drained && managed.State != "running", artifact_id = managed.StdoutArtifact });
    }

    public CallToolResult WriteStdin(string processId, string text)
    {
        var managed = Lookup(processId);
        if (managed.State != "running")
            throw new CodexishFault("PROCESS_EXITED", $"Process '{processId}' has exited; read its output with artifact_read.");
        if (managed.StdinClosed)
            throw new CodexishFault("PROCESS_EXITED", "stdin was closed by a graceful stop; start a new process to send more input.");
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        managed.Process.StandardInput.BaseStream.Write(bytes);
        managed.Process.StandardInput.BaseStream.Flush();
        store.Event("process_write", processId, new { bytes = bytes.Length });
        return Reply.Ok(new
        {
            process_id = processId, bytes_written = bytes.Length, encoding = "utf-8",
            note = "Delivery to stdin is confirmed; the child's reaction is not. Poll for output.",
            next_tool = "process_poll"
        });
    }

    public async Task<CallToolResult> Stop(string processId, string mode)
    {
        var managed = Lookup(processId);
        if (mode is not ("graceful" or "kill_tree"))
            throw new CodexishFault("INVALID_ARGUMENT", "mode must be graceful or kill_tree.");
        if (managed.State != "running")
            return Reply.Ok(new { process_id = processId, state = managed.State, exit_code = managed.ExitCode,
                stopped = false, reason = "already_exited" });
        if (mode == "graceful")
        {
            try { managed.Process.StandardInput.Close(); managed.StdinClosed = true; } catch (IOException) { }
            store.Event("process_stop", processId, new { mode });
            await Task.WhenAny(managed.Collection ?? Task.CompletedTask, Task.Delay(500));
            return Reply.Ok(new
            {
                process_id = processId, mode, state = managed.State, exit_code = managed.ExitCode,
                effect = "stdin was closed; Windows has no console signal this server can deliver to an unowned console",
                note = "A child that ignores EOF keeps running. Use mode=kill_tree to end it and its descendants.",
                next_tool = "process_poll"
            });
        }
        bool viaJob = Native.TerminateJob(managed.JobHandle);
        if (!viaJob)
        {
            try { managed.Process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception e) { throw new CodexishFault("EXECUTION_FAILED", $"Could not terminate the tree: {e.Message}", "unknown"); }
        }
        store.Event("process_stop", processId, new { mode, via_job = viaJob });
        await Task.WhenAny(managed.Collection ?? Task.CompletedTask, Task.Delay(3000));
        return Reply.Ok(new
        {
            process_id = processId, mode, state = managed.State, exit_code = managed.ExitCode,
            terminated_via = viaJob ? "job_object" : "process_tree",
            note = "Descendants were terminated with the process. Effects already applied are not undone.",
            next_tool = "process_poll"
        });
    }

    public object Describe(ManagedProcess managed) => new
    {
        process_id = managed.ProcessId,
        pid = managed.Pid,
        state = managed.State,
        exit_code = managed.ExitCode,
        lifetime = managed.Lifetime,
        root_id = managed.RootId,
        started_utc = managed.StartedUtc,
        command = managed.Display,
        stdout_artifact = managed.StdoutArtifact,
        stderr_artifact = managed.StderrArtifact
    };

    // D9/restart: invocations recover by status, process rows recover by identity. A persistent child that is
    // still alive is re-attached; output produced while the server was down was not captured by anyone.
    public List<object> Recover()
    {
        List<object> recovered = [];
        foreach (var row in store.Processes("running"))
        {
            if (row.Lifetime == "persistent" && Alive(row.Pid, row.StartTime))
            {
                store.UpsertProcess(row with { State = "running" });
                recovered.Add(new { process_id = row.ProcessId, pid = row.Pid, state = "running", reattached = true,
                    output_since_restart = "not_captured" });
                continue;
            }
            store.UpsertProcess(row with { State = "exited_unknown_code", ExitCode = null });
            recovered.Add(new { process_id = row.ProcessId, pid = row.Pid, state = "exited_unknown_code", reattached = false,
                reason = row.Lifetime == "persistent" ? "process_gone" : "session_lifetime_ended_with_previous_run" });
        }
        if (recovered.Count > 0) store.Event("process_recovery", null, new { processes = recovered });
        return recovered;
    }

    private static bool Alive(int pid, long startTime)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTime;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public int KillSessionChildren()
    {
        int killed = 0;
        foreach (var managed in processes.Values.Where(p => p.Lifetime == "session" && p.State == "running"))
        {
            if (!Native.TerminateJob(managed.JobHandle))
                try { managed.Process.Kill(entireProcessTree: true); } catch (Exception) { continue; }
            killed++;
        }
        return killed;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var managed in processes.Values)
        {
            // Closing the job handle ends lifetime=session trees. Persistent children never get a job and
            // keep running by design.
            Native.CloseJob(managed.JobHandle);
            try { managed.Process.Dispose(); } catch (Exception) { }
        }
    }
}
