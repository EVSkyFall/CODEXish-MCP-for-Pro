using System.Diagnostics;
using System.Text;
using static Codexish.Server.SelfTest;

namespace Codexish.Server;

// Real child processes only: the interpreter actually runs, the exit codes are the OS exit codes, and the
// kill_tree check ends a real grandchild. Windows-only mechanisms print SKIP elsewhere.
internal static class ProcessTests
{
    public static async Task Run(CodexishRuntime runtime, CodexishTools tools, string root)
    {
        if (Shells.Preferred is not { } shell)
        {
            Skip("shell and process checks: neither cmd.exe nor pwsh is available");
            return;
        }

        var streams = await tools.ShellRun("proj", "sh1", command: Shells.Streams(shell), shell: shell, wait_ms: 30000);
        Check(Status(streams) == "succeeded" && Data(streams).GetProperty("exit_code").GetInt32() == 7,
            $"{shell} returns the real exit code, not an inferred success");
        string stdout = Data(streams).GetProperty("stdout_preview").GetString()!;
        string stderr = Data(streams).GetProperty("stderr_preview").GetString()!;
        Check(stdout.IndexOf("one", StringComparison.Ordinal) < stdout.IndexOf("two", StringComparison.Ordinal) &&
              !stdout.Contains("three") && stderr.Contains("three"),
            "stdout keeps its byte order and stderr is collected separately");
        string outArtifact = Data(streams).GetProperty("stdout_artifact").GetString()!;
        Check(Text(tools.ArtifactRead(outArtifact, null, null, null, null, null)).Contains("two"),
            "the full command output is readable as an artifact");

        if (Shells.HasPwsh && shell != "pwsh")
        {
            var other = await tools.ShellRun("proj", "sh2", command: "Write-Output 'from pwsh'; exit 3", shell: "pwsh", wait_ms: 30000);
            Check(Data(other).GetProperty("exit_code").GetInt32() == 3 &&
                  Data(other).GetProperty("stdout_preview").GetString()!.Contains("from pwsh"),
                "the second allowed interpreter also runs and reports its exit code");
        }
        else if (!Shells.HasPwsh) Skip("second interpreter check: PowerShell 7 is not installed");
        else Skip("second interpreter check: pwsh is already the interpreter under test");

        Check(Error(await tools.ShellRun("proj", "sh3", command: "echo x", shell: "bash", wait_ms: 1000)) == "PERMISSION_DENIED",
            "a shell outside shell.allowed is refused");
        var defaulted = await tools.ShellRun("proj", "sh4", command: "Write-Output 'defaulted'", wait_ms: 60000);
        if (Error(defaulted) == "NOT_FOUND") Skip("default shell check: no PowerShell interpreter is installed");
        else
            Check(Status(defaulted) == "succeeded" && Data(defaulted).GetProperty("stdout_preview").GetString()!.Contains("defaulted") &&
                  Data(defaulted).GetProperty("shell").GetString() == runtime.Config.Shell.EffectiveDefault &&
                  Data(defaulted).GetProperty("interpreter").GetString() is { Length: > 0 },
                "a command without a shell runs in shell.default and the result names the interpreter that ran it");
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (OperatingSystem.IsWindows() && File.Exists(powershell))
        {
            var classic = await tools.ShellRun("proj", "sh4b", command: "Write-Output ('ps' + $PSVersionTable.PSVersion.Major)", shell: "powershell", wait_ms: 60000);
            Check(Status(classic) == "succeeded" && Data(classic).GetProperty("stdout_preview").GetString()!.Contains("ps5") &&
                  Data(classic).GetProperty("interpreter").GetString()!.Equals(powershell, StringComparison.OrdinalIgnoreCase),
                "shell=powershell runs Windows PowerShell 5.1 and reports it");
        }
        else Skip("shell=powershell check: Windows PowerShell is not installed");
        var structured = await tools.ShellRun("proj", "sh5", executable: Shells.Executable(shell),
            args: Shells.DirectArguments(shell, "structured"), wait_ms: 30000);
        Check(Data(structured).GetProperty("stdout_preview").GetString()!.Contains("structured"),
            "structured executable plus args runs without a shell");

        // wait_ms is a response wait: the child must survive it.
        var handle = await tools.ShellRun("proj", "sh6", command: Shells.Sleep(shell, 20), shell: shell, wait_ms: 300);
        Check(Status(handle) == "running", "an elapsed wait_ms returns a handle instead of a result");
        string processId = Data(handle).GetProperty("process_id").GetString()!;
        int pid = Data(handle).GetProperty("pid").GetInt32();
        Check(Alive(pid), "the child keeps running after wait_ms elapses; nothing was killed");
        var live = runtime.Processes.Lookup(processId);
        Check(ProcessSupervisor.SameProcess(runtime.Store.Process(processId)!, pid, live.StartTime),
            "a process handle is bound to both PID and start time");
        Check(!ProcessSupervisor.SameProcess(runtime.Store.Process(processId)!, pid, live.StartTime + 1),
            "the same PID with a different start time is not the same process, so PID reuse cannot be mistaken for it");
        var stopped = await tools.ProcessStop(processId, "kill_tree", "stop1");
        Check(Status(stopped) == "succeeded", "kill_tree terminates a running child");
        Check(Data(stopped).GetProperty("terminated_via").GetString() == (OperatingSystem.IsWindows() ? "job_object" : "process_tree"),
            "kill_tree reports which mechanism ended the tree");

        var interactive = await tools.ProcessStart("proj", "ps1", command: Shells.EchoStdin(shell), shell: shell, wait_ms: 300);
        string stdinId = Data(interactive).GetProperty("process_id").GetString()!;
        Check(Status(interactive) == "running", "process_start returns a handle for a long-running process");
        await tools.ProcessWrite(stdinId, "beta\nalpha\n", "in1");
        var stopStdin = await tools.ProcessStop(stdinId, "graceful", "stop2");
        Check(Status(stopStdin) == "succeeded", "graceful stop closes stdin without forcing a kill");
        var echoed = await tools.ProcessPoll(stdinId, null, 15000);
        Check(Data(echoed).GetProperty("stdout").GetString()!.Contains("alpha"),
            "process_write reached stdin and the program acted on it");
        // A poll returns as soon as output is available, so the exit is awaited by re-polling with the cursor
        // until the state changes: pwsh on Linux needs more than the 500 ms grace inside process_stop to finish.
        var exitDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (Data(echoed).GetProperty("state").GetString() == "running" && DateTimeOffset.UtcNow < exitDeadline)
            echoed = await tools.ProcessPoll(stdinId, Output(echoed).GetProperty("next_cursor").GetString(), 2000);
        Check(Data(echoed).GetProperty("state").GetString() == "exited" &&
              Data(echoed).GetProperty("exit_code").GetInt32() == 0,
            "an exited process keeps reporting its real exit code");

        var counter = await tools.ProcessStart("proj", "ps2", command: Shells.Ticks(shell, 5), shell: shell, wait_ms: 200);
        string counterId = Data(counter).GetProperty("process_id").GetString()!;
        var first = await tools.ProcessPoll(counterId, null, 8000);
        string firstCursor = Output(first).GetProperty("next_cursor").GetString()!;
        Check(Data(first).GetProperty("stdout").GetString()!.Contains("tick 1"),
            "process_poll returns output while the process is still running");
        var second = await tools.ProcessPoll(counterId, firstCursor, 8000);
        string secondText = Data(second).GetProperty("stdout").GetString()!;
        Check(!secondText.Contains("tick 1") && secondText.Length > 0,
            "a poll cursor returns only the bytes produced since the previous poll");
        Check(Error(await tools.ProcessPoll(counterId, ProcessSupervisor.Cursor(counterId, 1 << 24, 0), 0)) == "CURSOR_INVALID",
            "a cursor past the collected output is refused instead of reinterpreted");
        Check(Error(await tools.ProcessPoll(counterId, ProcessSupervisor.Cursor("proc_someone_else", 0, 0), 0)) == "CURSOR_INVALID",
            "a cursor issued for another process is refused instead of applied to these streams");
        await tools.ProcessStop(counterId, "kill_tree", "stop3");

        // The shell chain releases when the child has started, so a long-running process must not block the
        // next command in the same root.
        var blocker = await tools.ProcessStart("proj", "chain1", command: Shells.Sleep(shell, 30), shell: shell,
            wait_ms: 300, lifetime: "session");
        string blockerId = Data(blocker).GetProperty("process_id").GetString()!;
        Check(Status(blocker) == "running", "a long-running process in a root is still running");
        var overtake = await tools.ShellRun("proj", "chain2", command: Shells.Streams(shell), shell: shell, wait_ms: 30000);
        Check(Status(overtake) == "succeeded",
            "a later command in the same root runs while an earlier long-running process is still alive");
        await tools.ProcessStop(blockerId, "kill_tree", "chain-stop");

        await JobObject(tools);
        await JobFallback(root, shell);
        await Lifetime(root, shell);
    }

    // A Job Object that cannot be created must not cost the user their command: the child keeps running and
    // the result says which mechanism will end it. This is also the Linux path, where there is no job at all.
    private static async Task JobFallback(string root, string shell)
    {
        string alternate = Path.Combine(Path.GetDirectoryName(root)!, "nojob");
        Directory.CreateDirectory(Path.Combine(alternate, "proj"));
        var config = BuildConfig(alternate, ServerConfig.NewSecret(12));
        int pid;
        ProcessSupervisor.DisableJobObjects = true;
        try
        {
            using var runtime = new CodexishRuntime(config);
            var tools = new CodexishTools(runtime);
            var child = await tools.ShellRun("proj", "nojob1", command: Shells.Sleep(shell, 30), shell: shell,
                wait_ms: 300, lifetime: "session");
            pid = Data(child).GetProperty("pid").GetInt32();
            Check(Status(child) == "running" && Data(child).GetProperty("supervision").GetString() == "process_tree_fallback",
                "a session child whose Job Object is unavailable still starts and reports the fallback supervision");
            Check(runtime.Store.Events("job_object_unavailable").Count == 1,
                "the missing Job Object is recorded rather than hidden");
            Check(runtime.Processes.KillSessionChildren() == 1 && await Gone(pid),
                "kill-children ends a fallback-supervised session child");
        }
        finally { ProcessSupervisor.DisableJobObjects = false; }

        ProcessSupervisor.DisableJobObjects = true;
        try
        {
            using (var runtime = new CodexishRuntime(config))
            {
                var tools = new CodexishTools(runtime);
                var child = await tools.ShellRun("proj", "nojob2", command: Shells.Sleep(shell, 30), shell: shell,
                    wait_ms: 300, lifetime: "session");
                pid = Data(child).GetProperty("pid").GetInt32();
                Check(Alive(pid), "the fallback-supervised child is running before the runtime is disposed");
            }
            Check(await Gone(pid), "disposing the runtime ends a session child even without a Job Object");
        }
        finally { ProcessSupervisor.DisableJobObjects = false; }
    }

    // The grandchild is started by the child, so only supervision of the whole tree can end it.
    private static async Task JobObject(CodexishTools tools)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip("kill_tree grandchild check: the Windows Job Object is the mechanism under test");
            return;
        }
        if (!Shells.HasPwsh)
        {
            Skip("kill_tree grandchild check: PowerShell 7 is needed to start a detached grandchild");
            return;
        }
        var parent = await tools.ProcessStart("proj", "job1", command: Shells.Grandchild("cmd"), shell: "pwsh",
            wait_ms: 500, lifetime: "session");
        string parentId = Data(parent).GetProperty("process_id").GetString()!;
        int grandchild = 0;
        for (int attempt = 0; attempt < 20 && grandchild == 0; attempt++)
        {
            var poll = await tools.ProcessPoll(parentId, null, 1000);
            string text = Data(poll).GetProperty("stdout").GetString() ?? "";
            foreach (string line in text.Split('\n'))
                if (int.TryParse(line.Trim(), out int value)) { grandchild = value; break; }
        }
        if (grandchild == 0)
        {
            Skip("kill_tree grandchild check: the child did not report a grandchild PID");
            await tools.ProcessStop(parentId, "kill_tree", "job-stop");
            return;
        }
        Check(Alive(grandchild), "a grandchild started by the child is running before the stop");
        var stopped = await tools.ProcessStop(parentId, "kill_tree", "job-stop");
        Check(Data(stopped).GetProperty("terminated_via").GetString() == "job_object", "kill_tree used the Windows Job Object");
        Check(await Gone(grandchild), "kill_tree ended the grandchild as well, not only the direct child");
    }

    public static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<bool> Gone(int pid)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            if (!Alive(pid)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    // A second runtime with its own state directory is disposed on purpose to prove the lifetime contract.
    private static async Task Lifetime(string root, string shell)
    {
        string parent = Path.GetDirectoryName(root)!;
        string alternate = Path.Combine(parent, "alt");
        Directory.CreateDirectory(Path.Combine(alternate, "proj"));
        var config = BuildConfig(alternate, ServerConfig.NewSecret(12));
        int sessionPid, persistentPid;
        using (var runtime = new CodexishRuntime(config))
        {
            var tools = new CodexishTools(runtime);
            var session = await tools.ShellRun("proj", "life1", command: Shells.Sleep(shell, 30), shell: shell,
                wait_ms: 300, lifetime: "session");
            var persistent = await tools.ShellRun("proj", "life2", command: Shells.Sleep(shell, 30), shell: shell,
                wait_ms: 300, lifetime: "persistent");
            sessionPid = Data(session).GetProperty("pid").GetInt32();
            persistentPid = Data(persistent).GetProperty("pid").GetInt32();
            Check(Alive(sessionPid) && Alive(persistentPid), "both lifetimes start real children");
        }
        Check(await Gone(sessionPid), "lifetime=session children die when the runtime is disposed");
        Check(Alive(persistentPid), "lifetime=persistent children survive the runtime that started them");

        // Delta 10: a restart re-attaches a live persistent process by PID and start time.
        using (var restarted = new CodexishRuntime(config))
        {
            var recovered = restarted.RecoveredProcesses
                .Select(p => System.Text.Json.JsonSerializer.SerializeToElement(p)).ToArray();
            Check(recovered.Any(p => p.GetProperty("pid").GetInt32() == persistentPid &&
                                     p.GetProperty("state").GetString() == "running" &&
                                     p.GetProperty("reattached").GetBoolean()),
                "a live persistent process is re-attached after a restart with output_since_restart not_captured");
            Check(recovered.Any(p => p.GetProperty("pid").GetInt32() == sessionPid &&
                                     p.GetProperty("state").GetString() == "exited_unknown_code"),
                "a session process from the previous run is recorded as exited with an unknown code");

            var tools = new CodexishTools(restarted);
            string handle = restarted.Store.Processes().First(p => p.Pid == persistentPid).ProcessId;
            var polled = await tools.ProcessPoll(handle, null, 200);
            Check(Status(polled) == "succeeded" && Data(polled).GetProperty("state").GetString() == "running" &&
                  Data(polled).GetProperty("reattached").GetBoolean() &&
                  Data(polled).GetProperty("output_since_restart").GetString() == "not_captured",
                "a re-attached persistent process can still be polled and says its output was not captured");
            Check(Error(await tools.ProcessWrite(handle, "ignored", "reattach-write")) == "UNSUPPORTED_CAPABILITY",
                "stdin is refused for a re-attached process instead of failing obscurely");
            Check(Status(await tools.ProcessStop(handle, "graceful", "reattach-graceful")) == "succeeded",
                "a graceful stop of a re-attached process explains that there is no stdin to close");
            var killed = await tools.ProcessStop(handle, "kill_tree", "reattach-kill");
            Check(Status(killed) == "succeeded" && await Gone(persistentPid),
                "a re-attached persistent process can still be stopped by handle");
        }
        try
        {
            using var leftover = Process.GetProcessById(persistentPid);
            leftover.Kill(entireProcessTree: true);
            leftover.WaitForExit(5000);
        }
        catch (Exception) { }
    }
}

internal static class ArtifactTests
{
    public static async Task Run(CodexishRuntime runtime, CodexishTools tools)
    {
        var artifacts = runtime.Artifacts;
        if (Shells.Preferred is { } shell)
        {
            // A real child produces more than a megabyte of output; the cursor must read all of it.
            var big = await tools.ShellRun("proj", "art1", command: Shells.Bulk(shell, 22000), shell: shell, wait_ms: 180000);
            string artifact = Data(big).GetProperty("stdout_artifact").GetString()!;
            long total = artifacts.Length(artifact);
            Check(total > 1024 * 1024, $"a real command produced more than 1 MiB of output ({total} bytes)");
            Check(Data(big).GetProperty("preview_truncated").GetBoolean(),
                "the inline preview is marked truncated instead of silently dropping output");
            long read = 0;
            string? cursor = null;
            int pages = 0;
            while (pages < 200)
            {
                var page = tools.ArtifactRead(artifact, null, null, null, null, cursor);
                read += Data(page).GetProperty("bytes_returned").GetInt64();
                pages++;
                if (Output(page).GetProperty("complete").GetBoolean()) break;
                cursor = Output(page).GetProperty("next_cursor").GetString();
            }
            Check(read == total && pages > 1, $"the whole artifact is readable by cursor in {pages} pages with no gap");
            var found = tools.ArtifactSearch(artifact, "line 21999 ", false, null);
            var match = Data(found).GetProperty("matches").EnumerateArray().First();
            Check(match.GetProperty("line").GetInt32() == 21999 && match.GetProperty("byte_offset").GetInt64() > 0,
                "artifact_search returns the line number and byte offset of a match");
        }
        else Skip("large artifact paging: no interpreter is available to produce the output");

        var row = artifacts.Save(Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n"), "text/plain", "self_test", null);
        var lines = tools.ArtifactRead(row.Id, null, null, 2, 3, null);
        Check(Text(lines) == "beta\ngamma\n", "artifact_read returns a line range");
        string stale = Output(tools.ArtifactRead(row.Id, null, 4, null, null, null)).GetProperty("next_cursor").GetString()!;
        artifacts.Truncate(row.Id);
        Check(Error(tools.ArtifactRead(row.Id, null, null, null, null, stale)) == "CURSOR_INVALID",
            "a cursor from an earlier generation is refused, never reinterpreted against new bytes");

        var partial = artifacts.Create("text/plain", "self_test", null);
        using (var sink = artifacts.OpenAppend(partial.Id)) sink.Write(Encoding.UTF8.GetBytes("collected prefix\n"));
        artifacts.MarkIncomplete(partial.Id, "producer_died");
        var prefix = tools.ArtifactRead(partial.Id, null, null, null, null, null);
        Check(Text(prefix) == "collected prefix\n" && Data(prefix).GetProperty("collection").GetString() == "incomplete",
            "the preserved prefix of an abandoned artifact is still readable and marked incomplete");
        Check(Error(tools.ArtifactRead(partial.Id, 17, null, null, null, null)) == "OUTPUT_INCOMPLETE",
            "reading past the end of an abandoned artifact reports OUTPUT_INCOMPLETE");
        Check(Error(tools.ArtifactRead("art_missing", null, null, null, null, null)) == "NOT_FOUND",
            "an unknown artifact_id is NOT_FOUND");

        string secret = "AKIA" + new string('Q', 16);
        var leaky = artifacts.Save(Encoding.UTF8.GetBytes($"aws={secret}\n"), "text/plain", "self_test", null);
        var readBack = tools.ArtifactRead(leaky.Id, null, null, null, null, null);
        Check(!Text(readBack).Contains(secret) && Data(readBack).GetProperty("redacted").GetBoolean(),
            "artifact_read redacts on read while the local file keeps the raw bytes");
        Check(File.ReadAllText(Path.Combine(artifacts.Directory, leaky.Id + ".bin")).Contains(secret),
            "the local artifact file is the unredacted original");

        // Environment-variable values are stripped from every redacted output, by value, not by name.
        const string variable = "CODEXISH_SELFTEST_API_KEY";
        const string shortVariable = "CODEXISH_SELFTEST_TOKEN";
        string planted = "plnt-" + ServerConfig.NewSecret(18);
        Environment.SetEnvironmentVariable(variable, planted);
        Environment.SetEnvironmentVariable(shortVariable, "abc123");
        try
        {
            Redaction.RefreshEnvironment();
            var carrier = artifacts.Save(Encoding.UTF8.GetBytes($"config value = {planted}\n"), "text/plain", "self_test", null);
            var redacted = tools.ArtifactRead(carrier.Id, null, null, null, null, null);
            Check(!Text(redacted).Contains(planted) && Text(redacted).Contains("[REDACTED:environment_value]"),
                "the value of an environment variable whose name looks like a secret is redacted from output");
            Check(Data(redacted).GetProperty("redacted_kinds").EnumerateArray()
                    .Any(k => k.GetString() == "environment_value"),
                "the redaction kind names the environment value");
            Check(File.ReadAllText(Path.Combine(artifacts.Directory, carrier.Id + ".bin")).Contains(planted),
                "the local artifact file still holds the original environment value");
            Check(!Redaction.Apply("the word abc123 appears in ordinary output").Redacted,
                $"an environment value shorter than {Redaction.MinimumEnvironmentValue} characters is not treated as a secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            Environment.SetEnvironmentVariable(shortVariable, null);
            Redaction.RefreshEnvironment();
        }
    }
}
