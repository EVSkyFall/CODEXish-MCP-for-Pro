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
    // Guards JobHandle: a handle closed on one thread must never be used on another, where Windows may already have
    // given its number to a new job.
    public readonly object JobSync = new();

    // Terminates the job while it is certainly still this process's job.
    public bool TerminateJob()
    {
        lock (JobSync) return Native.TerminateJob(JobHandle);
    }

    public void CloseJobIfIdle()
    {
        lock (JobSync)
        {
            if (JobHandle == 0 || Native.ActiveProcesses(JobHandle) != 0) return;
            Native.CloseJob(JobHandle);
            JobHandle = 0;
        }
    }

    public void CloseJob()
    {
        lock (JobSync)
        {
            Native.CloseJob(JobHandle);
            JobHandle = 0;
        }
    }
    public Task? Collection { get; set; }
    public volatile string State = "running";
    public int? ExitCode { get; set; }
    public bool StdinClosed { get; set; }
    // A process adopted from a previous run of this server: its streams belong to nobody and its exit code
    // cannot be read, but it can still be inspected and stopped.
    public bool Reattached { get; init; }
    public string Supervision { get; set; } = "job_object";
    // For a command string: the shell name and the interpreter executable that actually ran it.
    public string? Shell { get; init; }
    public string? Interpreter { get; init; }
    // Set once the final state is persisted and the process and job handles are released.
    public volatile bool Released;
}

// D7. One supervisor for shell_run and process_start. wait_ms only bounds the response; nothing here kills a
// child because a caller stopped waiting.
public sealed class ProcessSupervisor(Store store, Artifacts artifacts, ServerConfig config) : IDisposable
{
    private readonly ConcurrentDictionary<string, ManagedProcess> processes = new(StringComparer.Ordinal);
    private bool disposed;

    // Self-test switch that forces the no-job path so the fallback is exercised on Windows too.
    public static bool DisableJobObjects { get; set; }

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
        string? chosenShell = null, interpreter = null;
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
            string[] allowed = config.Shell.Usable;
            // Without a shell the configured default runs the command; the allowed list stays the user's own policy.
            chosenShell = string.IsNullOrWhiteSpace(shell) ? config.Shell.EffectiveDefault : ShellConfig.Canonical(shell);
            if (chosenShell is null || !allowed.Contains(chosenShell))
                throw new CodexishFault("PERMISSION_DENIED",
                    string.IsNullOrWhiteSpace(shell)
                        ? "No shell is allowed in shell.allowed, so a command string cannot run; use executable plus args."
                        : $"shell '{shell}' is not in shell.allowed ({string.Join(", ", allowed)}).");
            interpreter = ResolveShell(chosenShell);
            start.FileName = interpreter;
            if (chosenShell == "cmd")
            {
                // cmd.exe does not follow CommandLineToArgvW quoting; /s /c "..." passes the rest verbatim.
                start.Arguments = "/s /c \"" + command + "\"";
            }
            else
            {
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add(command);
            }
            display = chosenShell + ": " + command;
        }
        else throw new CodexishFault("INVALID_ARGUMENT", "Supply executable+args, or a command string.");

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
        ManagedProcess? managed = null;
        try
        {
            string supervision = lifetime == "session" ? "process_tree_fallback" : "not_supervised_persistent";
            if (lifetime == "session" && !DisableJobObjects)
            {
                job = Native.CreateKillOnCloseJob();
                if (job != 0 && !Native.AssignProcess(job, process.Handle)) { Native.CloseJob(job); job = 0; }
                if (job != 0) supervision = "job_object";
            }
            // A job that could not be created is reported, never a reason to refuse the user's command: the child
            // keeps running and its tree is ended with Process.Kill(entireProcessTree) instead.
            if (lifetime == "session" && job == 0)
                store.Event("job_object_unavailable", null, new { supervision, platform = Environment.OSVersion.Platform.ToString() });
            long startTime;
            try { startTime = process.StartTime.ToUniversalTime().Ticks; }
            catch (Exception) { startTime = DateTime.UtcNow.Ticks; }

            var stdout = artifacts.Create("text/plain", "stdout", cwd.Root.Id);
            var stderr = artifacts.Create("text/plain", "stderr", cwd.Root.Id);
            managed = new ManagedProcess
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
                JobHandle = job,
                Supervision = supervision,
                Shell = chosenShell,
                Interpreter = interpreter
            };
            processes[managed.ProcessId] = managed;
            Persist(managed);
            store.Event("process_start", managed.ProcessId, new { pid = managed.Pid, lifetime = managed.Lifetime, root = cwd.Root.Id, display, interpreter });
            managed.Collection = Collect(managed);
            return managed;
        }
        catch (Exception error)
        {
            // Nothing may keep running that no handle describes: the child and its tree end before the error surfaces.
            int pid = 0;
            try { pid = process.Id; } catch (InvalidOperationException) { }
            if (!Native.TerminateJob(job))
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* it already exited */ }
            Native.CloseJob(job);
            if (managed is not null) processes.TryRemove(managed.ProcessId, out _);
            process.Dispose();
            try { store.Event("process_start_failed", null, new { pid, error = error.GetType().Name, message = error.Message, cleanup = "terminated" }); }
            catch (Exception) { /* the error below is the report */ }
            throw new CodexishFault("EXECUTION_FAILED",
                $"The child started but could not be recorded ({error.GetType().Name}: {error.Message}); it was terminated with its descendants.",
                "unknown", details: new { pid, cleanup = "terminated" });
        }
    }

    // pwsh: PATH, then the newest %ProgramFiles%\PowerShell\*\pwsh.exe, then Windows PowerShell. Looked up on every call,
    // so an interpreter installed, moved or upgraded later is found without a restart.
    public static string ResolveShell(string shell) => ResolveShell(shell, Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("ProgramFiles"), OperatingSystem.IsWindows() ? Environment.SystemDirectory : null);

    public static string ResolveShell(string shell, string? pathVariable, string? programFiles, string? systemDirectory) => shell switch
    {
        "cmd" => Existing(systemDirectory is null ? null : Path.Combine(systemDirectory, "cmd.exe")) ?? "cmd.exe",
        "powershell" => WindowsPowerShell(systemDirectory) ?? OnPath("powershell", pathVariable) ?? "powershell.exe",
        _ => OnPath("pwsh", pathVariable) ?? NewestPwsh(programFiles) ?? WindowsPowerShell(systemDirectory) ?? "pwsh"
    };

    private static string? Existing(string? path) => path is not null && File.Exists(path) ? path : null;

    internal static string? OnPath(string name, string? pathVariable)
    {
        string file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (string directory in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (Path.IsPathRooted(directory) && Existing(Path.Combine(directory.Trim('"'), file)) is { } found) return found;
            }
            catch (ArgumentException) { /* a malformed PATH entry is skipped */ }
        }
        return null;
    }

    private static string? NewestPwsh(string? programFiles)
    {
        if (string.IsNullOrEmpty(programFiles)) return null;
        string parent = Path.Combine(programFiles, "PowerShell");
        if (!Directory.Exists(parent)) return null;
        static Version Named(string directory)
        {
            string digits = new(Path.GetFileName(directory).TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray());
            return Version.TryParse(digits.Contains('.') ? digits : digits + ".0", out var version) ? version : new Version(0, 0);
        }
        try
        {
            return Directory.EnumerateDirectories(parent)
                .Select(d => (Directory: d, Executable: Path.Combine(d, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")))
                .Where(c => File.Exists(c.Executable))
                .OrderByDescending(c => Named(c.Directory)).ThenByDescending(c => File.GetLastWriteTimeUtc(c.Executable))
                .Select(c => c.Executable).FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? WindowsPowerShell(string? systemDirectory) =>
        systemDirectory is null ? null : Existing(Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));

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
        try
        {
            Persist(managed);
            store.Event("process_exit", managed.ProcessId, new { state = managed.State, exit_code = managed.ExitCode });
        }
        catch (Exception) { /* the in-memory state stays authoritative for process_poll */ }
        finally { Release(managed); }
    }

    // After the final state is recorded only the metadata stays: the process handle is closed, and so is the job once
    // no descendant is left in it. A job that still holds descendants keeps them supervised until they are gone.
    private static void Release(ManagedProcess managed)
    {
        try { managed.Process.Dispose(); } catch (Exception) { }
        managed.CloseJobIfIdle();
        managed.Released = true;
    }

    // Jobs whose last descendant has exited since the process itself did.
    public int ReleaseIdleJobs()
    {
        int closed = 0;
        foreach (var managed in processes.Values.Where(p => p.Released && p.JobHandle != 0))
        {
            managed.CloseJobIfIdle();
            if (managed.JobHandle == 0) closed++;
        }
        return closed;
    }

    // Retention removed these rows; the in-memory entries of exited processes go with them.
    public void Forget(IEnumerable<string> processIds)
    {
        foreach (string id in processIds)
            if (processes.TryGetValue(id, out var managed) && managed.State != "running" && managed.JobHandle == 0)
                processes.TryRemove(id, out _);
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

    // The cursor carries a tag derived from the process id, so a cursor from one process cannot be replayed
    // against another process's streams.
    private static string Tag(string processId) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(processId))).ToLowerInvariant()[..8];

    public static string Cursor(string processId, long stdout, long stderr) =>
        ServerConfig.Base64Url(Encoding.UTF8.GetBytes($"p1.{Tag(processId)}.{stdout}.{stderr}"));

    public static (long Stdout, long Stderr) ParseCursor(string processId, string cursor)
    {
        string padded = cursor.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        string[] parts;
        try { parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('.'); }
        catch (FormatException) { throw new CodexishFault("CURSOR_INVALID", "The cursor is not a cursor issued by this server."); }
        if (parts.Length != 4 || parts[0] != "p1" || !long.TryParse(parts[2], out long stdout) || !long.TryParse(parts[3], out long stderr))
            throw new CodexishFault("CURSOR_INVALID", "The cursor is not a process cursor issued by this server.");
        if (!string.Equals(parts[1], Tag(processId), StringComparison.Ordinal))
            throw new CodexishFault("CURSOR_INVALID",
                "This cursor was issued for a different process; poll that process, or start again without a cursor.");
        return (stdout, stderr);
    }

    public async Task<CallToolResult> Poll(string processId, string? cursor, int waitMs, CancellationToken token)
    {
        var managed = Lookup(processId);
        Refresh(managed);
        var (stdoutAt, stderrAt) = cursor is { Length: > 0 } ? ParseCursor(processId, cursor) : (0L, 0L);
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
            supervision = managed.Supervision,
            reattached = managed.Reattached,
            output_since_restart = managed.Reattached ? "not_captured" : null,
            shell = managed.Shell,
            interpreter = managed.Interpreter,
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
        }, new { text = outText, next_cursor = Cursor(managed.ProcessId, nextOut, nextErr),
            complete = drained && managed.State != "running", artifact_id = managed.StdoutArtifact });
    }

    public CallToolResult WriteStdin(string processId, string text)
    {
        var managed = Lookup(processId);
        if (managed.Reattached)
            throw new CodexishFault("UNSUPPORTED_CAPABILITY",
                $"Process '{processId}' was adopted after a server restart, so its stdin belongs to nobody. " +
                "It can still be polled and stopped.");
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
        Refresh(managed);
        if (mode is not ("graceful" or "kill_tree"))
            throw new CodexishFault("INVALID_ARGUMENT", "mode must be graceful or kill_tree.");
        if (managed.State != "running")
            return Reply.Ok(new { process_id = processId, state = managed.State, exit_code = managed.ExitCode,
                stopped = false, reason = "already_exited" });
        if (mode == "graceful")
        {
            if (managed.Reattached)
                return Reply.Ok(new
                {
                    process_id = processId, mode, state = managed.State, stopped = false,
                    reason = "reattached_after_restart",
                    note = "This process was adopted after a server restart, so there is no stdin to close. Use kill_tree.",
                    next_tool = "process_stop with mode=kill_tree"
                });
            try { managed.Process.StandardInput.Close(); managed.StdinClosed = true; }
            catch (IOException) { }
            catch (InvalidOperationException) { }
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
        // The job object is the Windows mechanism; elsewhere, and for persistent children that never get a
        // job, the runtime's own process-tree termination is used and the result says which one ran.
        bool viaJob = managed.TerminateJob();
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
        supervision = managed.Supervision,
        reattached = managed.Reattached,
        root_id = managed.RootId,
        started_utc = managed.StartedUtc,
        command = managed.Display,
        shell = managed.Shell,
        interpreter = managed.Interpreter,
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
            if (row.Lifetime == "persistent" && Alive(row.Pid, row.StartTime) && Reattach(row))
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

    // The adopted process object is not a child of this server: its streams are gone and its exit code is not
    // readable, so the entry reports what can still be observed and refuses what cannot.
    private bool Reattach(ProcessRow row)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(row.Pid);
            processes[row.ProcessId] = new ManagedProcess
            {
                ProcessId = row.ProcessId,
                Process = process,
                Pid = row.Pid,
                StartTime = row.StartTime,
                Lifetime = row.Lifetime,
                RootId = row.RootId,
                StdoutArtifact = row.StdoutArtifact ?? "",
                StderrArtifact = row.StderrArtifact ?? "",
                Display = "(re-attached after a server restart)",
                StartedUtc = new DateTimeOffset(new DateTime(row.StartTime, DateTimeKind.Utc)),
                Reattached = true,
                Supervision = "process_tree_fallback"
            };
            return true;
        }
        catch (Exception) { return false; }
    }

    // A re-attached process has no collection task to update its state, so liveness is re-read on demand.
    private void Refresh(ManagedProcess managed)
    {
        if (!managed.Reattached || managed.State != "running") return;
        if (Alive(managed.Pid, managed.StartTime)) return;
        managed.State = "exited_unknown_code";
        try { Persist(managed); }
        finally { Release(managed); }
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
            if (!managed.TerminateJob())
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
            // Closing the job handle ends a lifetime=session tree on Windows. Where no job exists (Linux, or a
            // job that could not be created) the same contract is kept by terminating the tree directly.
            // Persistent children never get a job and keep running by design.
            if (managed.Lifetime == "session")
            {
                if (managed.JobHandle != 0) managed.CloseJob();
                else if (managed.State == "running")
                    try { managed.Process.Kill(entireProcessTree: true); } catch (Exception) { }
            }
            try { managed.Process.Dispose(); } catch (Exception) { }
        }
    }
}
