using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol.Protocol;

namespace Codexish.P0.V1;

// This is a transient launcher using the SAME application binary, not a second MCP server.
// Its stdin gate is released only after Windows Job assignment; no user command runs before assignment.
public sealed record ChildSpec(string Executable, string[] Args, string Cwd, string ReadyPath, bool CleanGitEnvironment = false);
public sealed class Processes(ServerConfig config, Store store, Files files, Artifacts artifacts) : IDisposable
{
    public sealed class Entry
    {
        public required string Id { get; init; }
        public required string Session { get; init; }
        public required string Lifetime { get; init; }
        public required string Stdout { get; init; }
        public required string Stderr { get; init; }
        public required Process Launcher { get; init; }
        public WindowsJob? Job { get; set; }
        public int TargetPid { get; set; }
        public int? ExitCode { get; set; }
        public bool OutputComplete { get; set; }
        public string State { get; set; } = "running";
        public Task Completion { get; set; } = Task.CompletedTask;
        public readonly SemaphoreSlim InputGate = new(1);
        public object Snapshot() => new { process_id = Id, pid = TargetPid, launcher_pid = Launcher.Id, lifetime = Lifetime,
            process_state = State, exit_code = ExitCode, output_complete = OutputComplete, stdout_artifact = Stdout,
            stderr_artifact = Stderr, execution_boundary = "unconfined_user", supervision = OperatingSystem.IsWindows() ? "windows_job" : "process_tree_best_effort" };
    }
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly object lifecycle = new();
    private bool closing;
    public static ProcessStartInfo SelfStart(string flag)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable not available.");
        var s = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) s.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        s.ArgumentList.Add(flag); return s;
    }
    public async Task<Entry> Start(string session, string rootId, string cwd, string executable, string[] args, string lifetime, bool cleanGit = false, bool readOnlyGit = false)
    {
        if (lifetime is not ("session" or "persistent") || string.IsNullOrWhiteSpace(executable)) throw new ProbeFault("INVALID_ARGUMENT", "Explicit executable and session/persistent lifetime required.");
        var grant = files.Grant(rootId, readOnlyGit ? "read" : "shell");
        using var dir = FileFence.DirectoryHandle(grant, cwd);
        string working = FileFence.Resolve(grant, cwd);
        string id = "proc_" + Guid.NewGuid().ToString("N");
        string ready = Path.Combine(config.StateDirectory, id + ".ready");
        var spec = new ChildSpec(executable, args, working, ready, cleanGit);
        var start = SelfStart("--v1-child"); start.WorkingDirectory = working;
        var p = new Process { StartInfo = start };
        var e = new Entry { Id = id, Session = session, Lifetime = lifetime, Launcher = p,
            Stdout = artifacts.Create(session, complete: false), Stderr = artifacts.Create(session, complete: false) };
        lock (lifecycle)
        {
            if (closing) { p.Dispose(); throw new ProbeFault("CANCELLED", "Server shutdown prevents new processes."); }
            try
            {
                if (!p.Start()) throw new ProbeFault("EXECUTION_FAILED", "Launcher did not start.");
                if (OperatingSystem.IsWindows()) e.Job = new WindowsJob(p);
                entries[id] = e;
            }
            catch { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } p.Dispose(); throw; }
        }
        // Drain both streams immediately and preserve per-stream byte order in local artifacts.
        async Task Drain(Stream input, string artifact)
        {
            using var output = new FileStream(artifacts.Locate(session, artifact).path, FileMode.Append, FileAccess.Write, FileShare.Read);
            byte[] buffer = new byte[16384]; int n;
            try
            {
                while ((n = await input.ReadAsync(buffer)) != 0) { await output.WriteAsync(buffer.AsMemory(0, n)); await output.FlushAsync(); }
                output.Flush(true); artifacts.Complete(session, artifact);
            }
            catch { StopEntry(e); throw; }
        }
        Task stdout = Drain(p.StandardOutput.BaseStream, e.Stdout), stderr = Drain(p.StandardError.BaseStream, e.Stderr);
        e.Completion = Task.Run(async () =>
        {
            try
            {
                await p.WaitForExitAsync(); e.ExitCode = p.ExitCode; e.State = "exited";
                await Task.WhenAll(stdout, stderr); e.OutputComplete = true;
            }
            catch (Exception) { e.State = "unknown"; }
            try { Persist(e); } catch (Exception) { e.State = "unknown"; }
        });
        try
        {
            await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(spec)); await p.StandardInput.FlushAsync();
            // No execution deadline: readiness ends on a real launch result or launcher exit.
            while (!File.Exists(ready) && !p.HasExited) await Task.Delay(5);
            if (!File.Exists(ready)) throw new ProbeFault("EXECUTION_FAILED", "Launcher exited before target readiness; inspect its output.");
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(ready));
            if (!result.RootElement.TryGetProperty("pid", out var pid)) throw new ProbeFault("EXECUTION_FAILED", "Target executable could not be started.");
            e.TargetPid = pid.GetInt32(); Persist(e); return e;
        }
        catch { StopEntry(e); await e.Completion; throw; }
        finally { try { File.Delete(ready); } catch (IOException) { } }
    }
    private void Persist(Entry e) => store.Exec("INSERT INTO processes VALUES($i,$s,$d) ON CONFLICT(id) DO UPDATE SET data=$d",
        ("$i", e.Id), ("$s", e.Session), ("$d", JsonSerializer.Serialize(e.Snapshot())));
    public CallToolResult Poll(string session, string id)
    {
        if (entries.TryGetValue(id, out var e) && e.Session == session) return VReply.Ok(e.Snapshot());
        string? saved = store.Scalar("SELECT data FROM processes WHERE id=$i AND session=$s", ("$i", id), ("$s", session));
        if (saved is null) return VReply.Error("NOT_FOUND", "No process in this session.");
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(saved)!;
        if (data["process_state"].GetString() == "running")
            return VReply.Error("EXECUTION_UNKNOWN", "Previous server process ended; do not control a reused PID.", "unknown", new { process_id = id, previous = data });
        return VReply.Ok(data);
    }
    private Entry Get(string session, string id) => entries.TryGetValue(id, out var e) && e.Session == session ? e : throw new ProbeFault("NOT_FOUND", "No live supervisor in this session.");
    public async Task<CallToolResult> Write(string session, string id, string text, bool close = false)
    {
        var e = Get(session, id); await e.InputGate.WaitAsync();
        try
        {
            if (e.Launcher.HasExited) return VReply.Error("PROCESS_EXITED", "Process already exited.");
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            try { await e.Launcher.StandardInput.BaseStream.WriteAsync(bytes); await e.Launcher.StandardInput.BaseStream.FlushAsync(); if (close) e.Launcher.StandardInput.Close(); }
            catch (IOException) { return VReply.Error("EXECUTION_UNKNOWN", "Stdin delivery failed or was partial; inspect, do not resend blindly.", "unknown"); }
            return VReply.Ok(new { process_id = id, bytes_written = bytes.Length, stdin_closed = close });
        }
        finally { e.InputGate.Release(); }
    }
    private static void StopEntry(Entry e)
    {
        try { if (e.Job is not null) e.Job.Terminate(); else if (!e.Launcher.HasExited) e.Launcher.Kill(true); }
        catch (InvalidOperationException) { }
    }
    public async Task<CallToolResult> Stop(string session, string id) { var e = Get(session, id); StopEntry(e); await e.Completion; return VReply.Ok(e.Snapshot()); }
    public async Task EndSession(string session) { var all = entries.Values.Where(x => x.Session == session && x.Lifetime == "session").ToArray(); foreach (var e in all) StopEntry(e); await Task.WhenAll(all.Select(x => x.Completion)); }
    public async Task KillAll() { var all = entries.Values.ToArray(); foreach (var e in all) StopEntry(e); await Task.WhenAll(all.Select(x => x.Completion)); }
    public async Task<CallToolResult> Run(string session, string rootId, string cwd, string executable, string[] args, CancellationToken ct, bool gitRead = false)
    {
        var e = await Start(session, rootId, cwd, executable, args, "session", gitRead, gitRead);
        using var cancel = ct.Register(() => StopEntry(e)); if (ct.IsCancellationRequested) StopEntry(e);
        await e.Completion;
        return VReply.Ok(new { process = e.Snapshot(), process_id = e.Id, exit_code = e.ExitCode,
            stdout = VReply.Data(artifacts.Read(session, e.Stdout)), stderr = VReply.Data(artifacts.Read(session, e.Stderr)),
            cancelled = ct.IsCancellationRequested, exit_success = e.ExitCode == 0 });
    }
    public static (string executable, string[] args) Shell(string shell, string command) => shell switch
    {
        "pwsh" => ("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command]),
        "cmd" when OperatingSystem.IsWindows() => (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), ["/d", "/s", "/c", command]),
        "sh" when !OperatingSystem.IsWindows() => ("/bin/sh", ["-c", command]),
        _ => throw new ProbeFault("INVALID_ARGUMENT", "Select pwsh, cmd on Windows, sh on Unix, or use explicit executable/args.")
    };
    public void Dispose()
    {
        lock (lifecycle) closing = true;
        KillAll().GetAwaiter().GetResult();
        foreach (var e in entries.Values) { e.Job?.Dispose(); e.Launcher.Dispose(); e.InputGate.Dispose(); }
    }
    public static async Task<int> Child()
    {
        Stream input = Console.OpenStandardInput(); var bytes = new List<byte>(); byte[] one = new byte[1];
        while (await input.ReadAsync(one) == 1 && one[0] != (byte)'\n') bytes.Add(one[0]);
        if (bytes.Count == 0) return 125; // Parent died before releasing the launch gate.
        var spec = JsonSerializer.Deserialize<ChildSpec>(bytes.ToArray())!;
        var start = new ProcessStartInfo(spec.Executable) { UseShellExecute = false, WorkingDirectory = spec.Cwd,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in spec.Args) start.ArgumentList.Add(arg);
        if (spec.CleanGitEnvironment)
        {
            foreach (string k in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(k);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        }
        using var target = new Process { StartInfo = start };
        try { if (!target.Start()) throw new InvalidOperationException(); }
        catch (Exception) { await Ready(spec.ReadyPath, new { error = "target_start_failed" }); return 127; }
        await Ready(spec.ReadyPath, new { pid = target.Id });
        Task stdout = target.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        Task stderr = target.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        _ = Task.Run(async () => { try { await input.CopyToAsync(target.StandardInput.BaseStream); target.StandardInput.Close(); } catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { } });
        await target.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); return target.ExitCode;
    }
    private static async Task Ready(string path, object value) { string temp = path + ".tmp"; await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value)); File.Move(temp, path); }
}

public sealed class WindowsJob : IDisposable
{
    private readonly SafeFileHandle handle;
    public WindowsJob(Process process)
    {
        handle = CreateJobObjectW(0, null);
        try
        {
            if (handle.IsInvalid) throw new IOException("CreateJobObject failed.");
            var info = new Extended { Basic = new Basic { Flags = 0x2000 } };
            if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<Extended>()) || !AssignProcessToJobObject(handle, process.Handle))
                throw new IOException("Job assignment failed before target release.");
        }
        catch { handle.Dispose(); throw; }
    }
    public void Terminate() { if (!handle.IsClosed && !handle.IsInvalid) TerminateJobObject(handle, 137); }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct Basic { public long ProcessTime, JobTime; public uint Flags; public nuint MinWs, MaxWs; public uint Active; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct Extended { public Basic Basic; public Io Io; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(nint sa, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref Extended info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
