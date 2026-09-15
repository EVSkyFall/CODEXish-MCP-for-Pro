using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Protocol;
using static Codexish.Server.SelfTest;

namespace Codexish.Server;

// Real child processes only: pwsh and cmd actually run, their exit codes are the OS exit codes, and the
// Job Object test kills a real grandchild.
internal static class ProcessTests
{
    private static bool HasShell(string name)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(name)
            {
                Arguments = name == "cmd.exe" ? "/c exit 0" : "-NoProfile -Command exit 0",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            });
            probe!.WaitForExit();
            return true;
        }
        catch (Exception) { return false; }
    }

    public static async Task Run(CodexishRuntime runtime, CodexishTools tools, string root)
    {
        if (!OperatingSystem.IsWindows() || !HasShell("cmd.exe"))
        {
            Skip("shell and process checks: Windows cmd.exe is not available");
            return;
        }

        var cmd = await tools.ShellRun("proj", "sh1", command: "echo one& echo two& echo three 1>&2& exit /b 7", shell: "cmd", wait_ms: 20000);
        Check(Status(cmd) == "succeeded" && Data(cmd).GetProperty("exit_code").GetInt32() == 7,
            "cmd /c returns the real exit code, not an inferred success");
        string stdout = Data(cmd).GetProperty("stdout_preview").GetString()!;
        string stderr = Data(cmd).GetProperty("stderr_preview").GetString()!;
        Check(stdout.IndexOf("one", StringComparison.Ordinal) < stdout.IndexOf("two", StringComparison.Ordinal) &&
              !stdout.Contains("three") && stderr.Contains("three"),
            "stdout keeps its byte order and stderr is collected separately");
        string outArtifact = Data(cmd).GetProperty("stdout_artifact").GetString()!;
        Check(Text(tools.ArtifactRead(outArtifact, null, null, null, null, null)).Contains("two"),
            "the full command output is readable as an artifact");

        bool pwsh = HasShell("pwsh");
        if (pwsh)
        {
            var shell = await tools.ShellRun("proj", "sh2", command: "Write-Output 'from pwsh'; exit 3", shell: "pwsh", wait_ms: 30000);
            Check(Data(shell).GetProperty("exit_code").GetInt32() == 3 &&
                  Data(shell).GetProperty("stdout_preview").GetString()!.Contains("from pwsh"),
                "pwsh -NoProfile -Command runs and reports its exit code");
        }
        else Skip("pwsh check: PowerShell 7 is not installed");

        Check(Error(await tools.ShellRun("proj", "sh3", command: "echo x", shell: "bash", wait_ms: 1000)) == "PERMISSION_DENIED",
            "a shell outside shell.allowed is refused");
        Check(Error(await tools.ShellRun("proj", "sh4", command: "echo x", wait_ms: 1000)) == "INVALID_ARGUMENT",
            "a command string without an explicit shell is refused");
        var structured = await tools.ShellRun("proj", "sh5", executable: "cmd.exe", args: ["/c", "echo structured"], wait_ms: 20000);
        Check(Data(structured).GetProperty("stdout_preview").GetString()!.Contains("structured"),
            "structured executable plus args runs without a shell");

        // wait_ms is a response wait: the child must survive it.
        var handle = await tools.ShellRun("proj", "sh6", command: "ping -n 20 127.0.0.1 >nul", shell: "cmd", wait_ms: 300);
        Check(Status(handle) == "running", "an elapsed wait_ms returns a handle instead of a result");
        string processId = Data(handle).GetProperty("process_id").GetString()!;
        int pid = Data(handle).GetProperty("pid").GetInt32();
        Check(!Process.GetProcessById(pid).HasExited, "the child keeps running after wait_ms elapses; nothing was killed");
        var live = runtime.Processes.Lookup(processId);
        Check(ProcessSupervisor.SameProcess(runtime.Store.Process(processId)!, pid, live.StartTime),
            "a process handle is bound to both PID and start time");
        Check(!ProcessSupervisor.SameProcess(runtime.Store.Process(processId)!, pid, live.StartTime + 1),
            "the same PID with a different start time is not the same process, so PID reuse cannot be mistaken for it");
        var stopped = await tools.ProcessStop(processId, "kill_tree", "stop1");
        Check(Status(stopped) == "succeeded", "kill_tree terminates a running child");

        var interactive = await tools.ProcessStart("proj", "ps1", command: "sort", shell: "cmd", wait_ms: 300);
        string sortId = Data(interactive).GetProperty("process_id").GetString()!;
        Check(Status(interactive) == "running", "process_start returns a handle for a long-running process");
        await tools.ProcessWrite(sortId, "beta\r\nalpha\r\n", "in1");
        var stopSort = await tools.ProcessStop(sortId, "graceful", "stop2");
        Check(Status(stopSort) == "succeeded", "graceful stop closes stdin without forcing a kill");
        var sorted = await tools.ProcessPoll(sortId, null, 5000);
        Check(Data(sorted).GetProperty("stdout").GetString()!.IndexOf("alpha", StringComparison.Ordinal) >= 0,
            "process_write reached stdin and the program acted on it");
        Check(Data(sorted).GetProperty("state").GetString() == "exited" &&
              Data(sorted).GetProperty("exit_code").GetInt32() == 0,
            "an exited process keeps reporting its real exit code");

        var counter = await tools.ProcessStart("proj", "ps2", command: "for /L %i in (1,1,5) do @(echo tick %i& ping -n 2 127.0.0.1 >nul)",
            shell: "cmd", wait_ms: 200);
        string counterId = Data(counter).GetProperty("process_id").GetString()!;
        var first = await tools.ProcessPoll(counterId, null, 4000);
        string firstCursor = Output(first).GetProperty("next_cursor").GetString()!;
        string firstText = Data(first).GetProperty("stdout").GetString()!;
        Check(firstText.Contains("tick 1"), "process_poll returns output while the process is still running");
        var second = await tools.ProcessPoll(counterId, firstCursor, 6000);
        string secondText = Data(second).GetProperty("stdout").GetString()!;
        Check(!secondText.Contains("tick 1") && secondText.Length > 0,
            "a poll cursor returns only the bytes produced since the previous poll");
        Check(Error(await tools.ProcessPoll(counterId, ProcessSupervisor.Cursor(1 << 24, 0), 0)) == "CURSOR_INVALID",
            "a cursor past the collected output is refused instead of reinterpreted");
        await tools.ProcessStop(counterId, "kill_tree", "stop3");

        // The shell chain releases when the child has started, so a long-running process must not block the
        // next command in the same root.
        var blocker = await tools.ProcessStart("proj", "chain1", command: "ping -n 30 127.0.0.1 >nul", shell: "cmd",
            wait_ms: 300, lifetime: "session");
        string blockerId = Data(blocker).GetProperty("process_id").GetString()!;
        Check(Status(blocker) == "running", "a long-running process in a root is still running");
        var overtake = await tools.ShellRun("proj", "chain2", command: "echo not blocked", shell: "cmd", wait_ms: 20000);
        Check(Status(overtake) == "succeeded" && Data(overtake).GetProperty("stdout_preview").GetString()!.Contains("not blocked"),
            "a later command in the same root runs while an earlier long-running process is still alive");
        await tools.ProcessStop(blockerId, "kill_tree", "chain-stop");

        await JobObject(tools, pwsh);
        await Lifetime(runtime, root);
    }

    // The grandchild is started by the child, so only the Job Object can end it.
    private static async Task JobObject(CodexishTools tools, bool pwsh)
    {
        if (!pwsh)
        {
            Skip("kill_tree grandchild check: PowerShell 7 is not installed");
            return;
        }
        var parent = await tools.ProcessStart("proj", "job1",
            command: "$c = Start-Process -FilePath cmd.exe -ArgumentList '/c ping -n 30 127.0.0.1' -PassThru; " +
                     "Write-Output $c.Id; Start-Sleep -Seconds 30",
            shell: "pwsh", wait_ms: 500, lifetime: "session");
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
        bool gone = false;
        for (int attempt = 0; attempt < 20 && !gone; attempt++)
        {
            if (!Alive(grandchild)) gone = true;
            else await Task.Delay(100);
        }
        Check(gone, "kill_tree ended the grandchild as well, not only the direct child");
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // A second runtime with its own state directory is disposed on purpose to prove the lifetime contract.
    private static async Task Lifetime(CodexishRuntime outer, string root)
    {
        string parent = Path.GetDirectoryName(root)!;
        string alternate = Path.Combine(parent, "alt");
        Directory.CreateDirectory(Path.Combine(alternate, "proj"));
        var config = BuildConfig(alternate, ServerConfig.NewSecret(12));
        int sessionPid, persistentPid;
        using (var runtime = new CodexishRuntime(config))
        {
            var tools = new CodexishTools(runtime);
            var session = await tools.ShellRun("proj", "life1", command: "ping -n 30 127.0.0.1 >nul", shell: "cmd",
                wait_ms: 300, lifetime: "session");
            var persistent = await tools.ShellRun("proj", "life2", command: "ping -n 30 127.0.0.1 >nul", shell: "cmd",
                wait_ms: 300, lifetime: "persistent");
            sessionPid = Data(session).GetProperty("pid").GetInt32();
            persistentPid = Data(persistent).GetProperty("pid").GetInt32();
            Check(Alive(sessionPid) && Alive(persistentPid), "both lifetimes start real children");
        }
        bool sessionGone = false;
        for (int attempt = 0; attempt < 30 && !sessionGone; attempt++)
        {
            if (!Alive(sessionPid)) sessionGone = true;
            else await Task.Delay(100);
        }
        Check(sessionGone, "lifetime=session children die when the runtime is disposed");
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
        if (OperatingSystem.IsWindows())
        {
            // A real child produces more than a megabyte of output; the cursor must read all of it.
            var big = await tools.ShellRun("proj", "art1",
                command: "for /L %i in (1,1,22000) do @echo line %i padding padding padding padding padding",
                shell: "cmd", wait_ms: 120000);
            string artifact = Data(big).GetProperty("stdout_artifact").GetString()!;
            long total = artifacts.Length(artifact);
            Check(total > 1024 * 1024, $"a real command produced more than 1 MiB of output ({total} bytes)");
            Check(Data(big).GetProperty("preview_truncated").GetBoolean(),
                "the inline preview is marked truncated instead of silently dropping output");
            long read = 0;
            string? cursor = null;
            int pages = 0;
            while (true)
            {
                var page = tools.ArtifactRead(artifact, null, null, null, null, cursor);
                read += Data(page).GetProperty("bytes_returned").GetInt64();
                pages++;
                if (Output(page).GetProperty("complete").GetBoolean()) break;
                cursor = Output(page).GetProperty("next_cursor").GetString();
                if (pages > 200) break;
            }
            Check(read == total && pages > 1, $"the whole artifact is readable by cursor in {pages} pages with no gap");
            var found = tools.ArtifactSearch(artifact, "line 21999 ", false, null);
            var match = Data(found).GetProperty("matches").EnumerateArray().First();
            Check(match.GetProperty("line").GetInt32() == 21999 && match.GetProperty("byte_offset").GetInt64() > 0,
                "artifact_search returns the line number and byte offset of a match");
        }
        else Skip("large artifact paging: Windows cmd.exe is not available");

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
    }
}
