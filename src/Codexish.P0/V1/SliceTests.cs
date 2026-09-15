using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.Protocol;

namespace Codexish.P0.V1;

// All roots, credentials, processes and repositories in these tests are newly generated fixtures.
// They are not evidence of a real ChatGPT connector, user desktop or production deployment.
public static class SliceTests
{
    private static int passed;
    private static readonly List<string> checks = [];
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException(name);
        checks.Add(name); Console.WriteLine($"V1 PASS {++passed:000} {name}");
    }
    private static string? Error(CallToolResult r)
    {
        var e = r.StructuredContent!.Value.GetProperty("error");
        return e.ValueKind == JsonValueKind.Null ? null : e.GetProperty("code").GetString();
    }
    private static JsonElement D(CallToolResult r) => VReply.Data(r);
    private static void Fault(Action action, string code, string label)
    {
        try { action(); throw new InvalidOperationException("Expected " + code + ": " + label); }
        catch (ProbeFault e) { Check(e.Code == code, label); }
    }
    private static ServerConfig Config(string parent, bool auth = false)
    {
        string root = Path.Combine(parent, "root"); Directory.CreateDirectory(root);
        return new() { Port = 0, ControlPort = 0, StateDirectory = Path.Combine(parent, "state"), Roots = [new("work", root)],
            NoAuth = !auth, PublicUrl = auth ? "https://mcp.example.test" : "", ClientId = "fixture-client",
            ClientSecret = "fixture-client-secret-not-real", PasswordHash = auth ? Passwords.Hash("fixture-password-not-real") : "",
            RedirectUris = ["https://client.example.test/callback"], GitExecutable = FindGit() };
    }
    private static string FindGit()
    {
        foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(path.Trim('"'), OperatingSystem.IsWindows() ? "git.exe" : "git");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return "";
    }
    private sealed class FixtureContext : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
    private static FixtureContext Context(string session)
    {
        var c = new DefaultHttpContext(); c.Items["codexish.session"] = session; return new() { HttpContext = c };
    }
    private static (string exe, string[] args) Self(string mode)
    {
        var s = Processes.SelfStart("--v1-test-child"); s.ArgumentList.Add(mode); return (s.FileName, s.ArgumentList.ToArray());
    }
    public static async Task<int> Run()
    {
        string parent = Path.Combine(Path.GetTempPath(), "codexish-v1-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(parent);
        try
        {
            await FilesAndRecovery(parent);
            await ProcessTests(Path.Combine(parent, "process-tests"));
            await HttpTests(Path.Combine(parent, "http-tests"), false);
            await HttpTests(Path.Combine(parent, "oauth-tests"), true);
            await CleanupRegression(parent);
            // Git loose objects are read-only on Windows. Cleanup is part of the test run,
            // and must succeed before any overall success marker or success artifact is emitted.
            DeleteOwnedFixture(parent);
            Check(!Directory.Exists(parent), "owned fixture cleanup removes read-only Git objects before success");
            string? evidence = Environment.GetEnvironmentVariable("CODEXISH_TEST_OUTPUT");
            if (evidence is not null)
            {
                Directory.CreateDirectory(evidence);
                await File.WriteAllTextAsync(Path.Combine(evidence, "v1-test-results.json"), JsonSerializer.Serialize(new { os = Environment.OSVersion.ToString(), p0 = SelfTest.Passed, v1 = passed, total = SelfTest.Passed + passed, checks, fixture_cleanup_complete = true, chat_measured = false, desktop_measured = false }, ServerConfig.Json));
            }
            Console.WriteLine($"V1_SELF_TEST_PASSED: {passed}; P0_PASSED: {SelfTest.Passed}; TOTAL_PASSED: {passed + SelfTest.Passed}; Chat and interactive desktop NOT measured.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine($"V1_SELF_TEST_FAILED after {passed}: {e}"); return 1; }
        finally
        {
            // On a failed test, make one best-effort cleanup attempt without replacing its error.
            // This helper is called only for the newly generated fixture, never a configured root.
            if (Directory.Exists(parent))
                try { DeleteOwnedFixture(parent); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { Console.Error.WriteLine("fixture_cleanup_failed: " + e.GetType().Name); }
        }
    }
    private static void DeleteOwnedFixture(string path)
    {
        var directory = new DirectoryInfo(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { directory.Delete(); return; }
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            var attributes = entry.Attributes;
            // Do not enumerate a link target or change its attributes, even within test data.
            if ((attributes & FileAttributes.ReparsePoint) != 0) { entry.Delete(); continue; }
            if (entry is DirectoryInfo child) DeleteOwnedFixture(child.FullName);
            else
            {
                if ((attributes & FileAttributes.ReadOnly) != 0) entry.Attributes = attributes & ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        if ((directory.Attributes & FileAttributes.ReadOnly) != 0) directory.Attributes &= ~FileAttributes.ReadOnly;
        directory.Delete();
    }
    private static async Task CleanupRegression(string parent)
    {
        string owned = Path.Combine(parent, "cleanup-owned"), target = Path.Combine(parent, "cleanup-target");
        Directory.CreateDirectory(owned); Directory.CreateDirectory(target);
        string readOnly = Path.Combine(owned, "read-only.txt"), preserved = Path.Combine(target, "preserved.txt");
        File.WriteAllText(readOnly, "fixture"); File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
        File.WriteAllText(preserved, "unchanged");
        string link = Path.Combine(owned, "link");
        if (OperatingSystem.IsWindows())
            await Exec(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), ["/d", "/c", "mklink", "/J", link, target], parent);
        else Directory.CreateSymbolicLink(link, target);
        DeleteOwnedFixture(owned);
        Check(!Directory.Exists(owned) && File.ReadAllText(preserved) == "unchanged", "fixture cleanup clears read-only files but unlinks rather than follows reparse targets");
    }
    private static async Task FilesAndRecovery(string parent)
    {
        var config = Config(parent); string root = config.Roots[0].Path;
        string artifactId, replayCursor, firstPage;
        using (var r = new Runtime(config))
        {
            var context = Context("test"); var t = new Tools(r, context);
            Check(Tools.Names.Length == 21 && Tools.Names.Distinct().Count() == 21, "20 coding tools plus checkpoint; no computer tool");
            Check(D(t.Workspace()).GetProperty("roots").GetArrayLength() == 1, "workspace exposes configured roots");
            Check(t.Capabilities().StructuredContent!.Value.GetProperty("execution_boundary").GetString() == "unconfined_user", "execution boundary on envelope");
            var create = await t.Write("work", "sample.txt", "alpha\nbeta\n", "create", mode: "create");
            Check(Error(create) is null && File.ReadAllText(Path.Combine(root, "sample.txt")) == "alpha\nbeta\n", "fs.write creates a real file");
            Check(Error(await t.Write("work", "sample.txt", "clobber", "create-again", mode: "create")) is not null, "create never replaces an existing file");
            var read = t.Read("work", "sample.txt", line_count: 1); string hash = D(read).GetProperty("sha256").GetString()!;
            Check(D(read).GetProperty("text").GetString() == "alpha" && D(read).GetProperty("next_line").GetInt32() == 2, "fs.read line pagination and hash");
            Check(D(t.Stat("work", "sample.txt", true)).GetProperty("sha256").GetString() == hash, "fs.stat hashes actual bytes");
            Check(D(t.List("work")).GetProperty("total_entries").GetInt32() == 1, "fs.list uses the configured root");
            Check(D(t.Search("work", "beta")).GetProperty("total_matches").GetInt32() == 1, "fs.search literal finds correct line");
            Check(D(t.Search("work", "^a.*", true, "*.txt")).GetProperty("total_matches").GetInt32() == 1, "fs.search regex and glob");
            Check(Error(t.Read("work", "../outside")) == "OUTSIDE_WORKSPACE", "parent traversal rejected");
            Check(Error(t.Read("work", Path.Combine(root, "sample.txt"))) == "OUTSIDE_WORKSPACE", "absolute path rejected");
            Check(Error(t.Read("work", "missing.txt")) == "NOT_FOUND", "missing file error has no execution uncertainty");
            Check(Error(await t.Write("work", "sample.txt", "bad", "bad-hash", "wrong")) == "FILE_CHANGED" && File.ReadAllText(Path.Combine(root, "sample.txt")) == "alpha\nbeta\n", "hash mismatch preserves original");
            var replaced = await t.Write("work", "sample.txt", "changed\n", "replace", hash);
            Check(Error(replaced) is null && File.ReadAllText(Path.Combine(root, "sample.txt")) == "changed\n", "conditional in-place replacement");
            string backup = D(replaced).GetProperty("backup").GetString()!;
            Check(File.ReadAllText(Path.Combine(config.StateDirectory, "backups", backup)) == "alpha\nbeta\n", "backup contains prior bytes outside workspace");
            Check(D(replaced).GetProperty("save_mode").GetString() == "exclusive_in_place_non_atomic", "non-atomic write contract explicit");
            Check(Error(await t.Write("work", "sample.txt", "changed\n", "replace", hash)) is null && Directory.GetFiles(Path.Combine(config.StateDirectory, "backups")).Length == 1, "same ID retry returns stored write without replay");
            Check(Error(await t.Write("work", "sample.txt", "different", "replace", hash)) == "IDEMPOTENCY_CONFLICT", "same ID different text conflicts");
            byte[] unicode = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("old\r\n")).ToArray();
            File.WriteAllBytes(Path.Combine(root, "utf16.txt"), unicode);
            Check(Error(await t.Write("work", "utf16.txt", "new\n", "unicode", ProbeRuntime.Hash(unicode))) is null && File.ReadAllBytes(Path.Combine(root, "utf16.txt")).SequenceEqual(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("new\r\n"))), "UTF16 BOM and CRLF preserved");
            File.WriteAllText(Path.Combine(root, "p1.txt"), "one\ntwo\n"); File.WriteAllText(Path.Combine(root, "p2.txt"), "before\n");
            string Patch(string path, string old, string text) => $"--- a/{path}\n+++ b/{path}\n@@ -1 +1 @@\n-{old}\n+{text}\n";
            var part = await t.Patch("work", Patch("p1.txt", "one", "ONE") + Patch("p2.txt", "before", "after"),
                new() { ["p1.txt"] = ProbeRuntime.Hash(File.ReadAllBytes(Path.Combine(root, "p1.txt"))), ["p2.txt"] = "wrong" }, "partial-patch");
            Check(Error(part) == "PATCH_PARTIAL" && D(part).GetProperty("applied").GetInt32() == 1 && File.ReadAllText(Path.Combine(root, "p2.txt")) == "before\n", "unified patch reports per-file partial application");
            Check(File.ReadAllText(Path.Combine(root, "p1.txt")) == "ONE\ntwo\n", "patch preserves unchanged lines");
            Check(UnifiedPatch.Parse(Patch("p1.txt", "-- special", "ok"))[0].Apply("-- special\n") == "ok\n", "removed line resembling a diff header is parsed as hunk content");
            Check(UnifiedPatch.Parse("--- a/x\n+++ b/x\n@@ -0,0 +1 @@\n+hello\n")[0].Apply("") == "hello\n", "insertion into empty existing file");
            Check(UnifiedPatch.Parse("--- a/x\n+++ b/x\n@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file\n")[0].Apply("old") == "new", "patch preserves explicit absence of final newline");
            Fault(() => UnifiedPatch.Parse("--- a/x\n+++ b/x\n@@ -1 +3 @@\n-a\n+b\n")[0].Apply("a\n"), "INVALID_PATCH", "new hunk position validated");
            Fault(() => UnifiedPatch.Parse(Patch("x", "wrong", "new"))[0].Apply("old\n"), "PATCH_CONFLICT", "patch context mismatch rejected");
            var readOnly = new RootGrant("readonly", root, true, false, false); config.Roots = [config.Roots[0], readOnly];
            Check(Error(await t.Write("readonly", "sample.txt", "bad", "denied", hash)) == "PERMISSION_DENIED", "write grant enforced at execution");
            Check(Error(t.Read("unknown-root", "x")) == "OUTSIDE_WORKSPACE", "unregistered root rejected");
            string outside = Path.Combine(parent, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "other.txt"), "outside");
            if (OperatingSystem.IsWindows())
            {
                await Exec(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), ["/d", "/c", "mklink", "/J", Path.Combine(root, "junction"), outside], root);
                Check(Error(t.Read("work", "junction/other.txt")) == "OUTSIDE_WORKSPACE", "Windows junction traversal rejected");
                Directory.Delete(Path.Combine(root, "junction"));
                using var locked = new FileStream(Path.Combine(root, "sample.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                Check(Error(await t.Write("work", "sample.txt", "bad", "locked", hash)) == "FILE_LOCKED", "exclusive Windows sharing conflict reported");
            }
            else
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
                Check(Error(t.Read("work", "link/other.txt")) == "OUTSIDE_WORKSPACE", "Unix symbolic link traversal rejected (not Windows evidence)");
                Directory.Delete(Path.Combine(root, "link"));
            }
            Check(FileFence.Within(Path.GetPathRoot(root)!, root) && !FileFence.Within(root, root + "-neighbor/file"), "volume root and sibling path boundaries");
            File.WriteAllText(Path.Combine(root, "secrets-synthetic.txt"), "password=synthetic-value\n");
            var redacted = t.Read("work", "secrets-synthetic.txt");
            Check(D(redacted).GetProperty("content_redacted").GetBoolean() && !D(redacted).GetProperty("text").GetString()!.Contains("synthetic-value"), "file read redacts synthetic secret assignment");
            Check(Redaction.WithSecrets("prefix ENV_FAKE_VALUE suffix", ["ENV_FAKE_VALUE"]) == "prefix [REDACTED] suffix", "environment value redaction without emitting values");
            artifactId = r.Artifacts.Create("test", Encoding.UTF8.GetBytes("한글-abcdef\n"));
            var page = t.ArtifactRead(artifactId, page_bytes: 4); replayCursor = D(page).GetProperty("replay_cursor").GetString()!; firstPage = page.StructuredContent!.Value.GetRawText();
            Check(t.ArtifactRead(artifactId, replayCursor).StructuredContent!.Value.GetRawText() == firstPage, "artifact cursor retry is byte-stable");
            var reconstructed = new List<byte>(); string? cursor = replayCursor;
            while (cursor is not null) { var next = t.ArtifactRead(artifactId, cursor); reconstructed.AddRange(Convert.FromBase64String(D(next).GetProperty("utf8_base64").GetString()!)); cursor = D(next).GetProperty("next_cursor").GetString(); }
            Check(Encoding.UTF8.GetString(reconstructed.ToArray()) == "한글-abcdef\n", "artifact pages reconstruct multibyte UTF8");
            Check(D(t.ArtifactSearch(artifactId, "abcdef")).GetProperty("matches").GetArrayLength() == 1, "artifact search returns matching line");
            Check(Error(new Tools(r, Context("other")).ArtifactRead(artifactId, replayCursor)) == "NOT_FOUND", "artifact ownership enforced across sessions");
            Check(Error(t.ArtifactRead(artifactId, replayCursor + "tamper")) == "INVALID_CURSOR", "cursor signature rejects tampering");
            await QueueTests(r, t, context);
            Check(Error(await t.Checkpoint("repair", "exit zero", ["test"], ["handle"], "checkpoint")) is null && D(t.Capabilities()).GetProperty("last_checkpoint").GetProperty("goal").GetString() == "repair", "checkpoint retained in host capabilities");
            int effects = 0;
            var persist = await r.Operations.Invoke("test", "persist-fault", "fixture", new { a = 1 }, ["db-test"], _ =>
            { effects++; r.Store.Exec("PRAGMA query_only=ON"); return Task.FromResult(VReply.Ok(new { done = true })); }, 10000);
            Check(Error(persist) == "EXECUTION_UNKNOWN" && D(persist).GetProperty("reason").GetString() == "persist_failed", "real SQLITE_READONLY final commit becomes memory unknown(persist_failed)");
            Check(Error(r.Operations.Inspect("test", "persist-fault")) == "EXECUTION_UNKNOWN", "inspect exposes persistence uncertainty, not endless running");
            var retried = await r.Operations.Invoke("test", "persist-fault", "fixture", new { a = 1 }, ["db-test"], _ => { effects++; return Task.FromResult(VReply.Ok(new { })); });
            Check(Error(retried) == "EXECUTION_UNKNOWN" && effects == 1, "persistence failure retry never replays effect");
            r.Store.Exec("PRAGMA query_only=OFF");
            r.Store.Exec("INSERT INTO invocations VALUES('test','crashed-queued','digest','queued',NULL)");
        }
        using (var reopened = new Runtime(config))
        {
            Check(Error(reopened.Operations.Inspect("test", "persist-fault")) == "EXECUTION_UNKNOWN", "restart retains unconfirmed started effect as unknown");
            Check(Error(reopened.Operations.Inspect("test", "crashed-queued")) == "CANCELLED", "restart cancels never-started queue with no effects");
            Check(reopened.Artifacts.Read("test", artifactId, replayCursor).StructuredContent!.Value.GetRawText() == firstPage, "artifact cursor remains valid across restart");
            Check(Error(reopened.Operations.Inspect("test", "replace")) is null, "completed write result survives restart");
        }
    }
    private static async Task QueueTests(Runtime r, Tools t, FixtureContext context)
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var order = new List<int>();
        Task<CallToolResult> first = r.Operations.Invoke("test", "fifo1", "fifo", new { n = 1 }, ["fifo"], async ct => { order.Add(1); entered.SetResult(true); await release.Task.WaitAsync(ct); return VReply.Ok(new { }); }, 10000);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = r.Operations.Invoke("test", "fifo2", "fifo", new { n = 2 }, ["fifo"], _ => { order.Add(2); return Task.FromResult(VReply.Ok(new { })); }, 0);
        var third = r.Operations.Invoke("test", "fifo3", "fifo", new { n = 3 }, ["fifo"], _ => { order.Add(3); return Task.FromResult(VReply.Ok(new { })); }, 0);
        Check(VReply.Status(await second) == "running" && VReply.Status(await third) == "running", "queued requests return inspect handles, never BUSY");
        Check(Error(await r.Operations.Invoke("test", "independent", "fifo", new { }, ["different"], _ => Task.FromResult(VReply.Ok(new { })), 10000)) is null, "independent resource progresses during held queue");
        Check(Error(t.Read("work", "sample.txt")) is null, "read bypasses mutation queue");
        release.SetResult(true); await first;
        for (int i = 0; i < 2; i++) await r.Operations.Invoke("test", "fifo" + (i + 2), "fifo", new { n = i + 2 }, ["fifo"], _ => throw new InvalidOperationException("replay"), 10000);
        Check(order.SequenceEqual([1, 2, 3]), "FIFO execution follows acceptance order");
        var alias = r.Config.Roots[0] with { Id = "alias" }; r.Config.Roots = r.Config.Roots.Append(alias).ToArray();
        string aliasPath = Path.Combine(alias.Path, "alias.txt");
        var a = t.Write("work", "alias.txt", "first", "alias-create", mode: "create");
        var b = t.Write("alias", "alias.txt", "second", "alias-replace", ProbeRuntime.Hash(Encoding.UTF8.GetBytes("first")));
        await Task.WhenAll(a, b);
        Check(Error(await b) is null && File.ReadAllText(aliasPath) == "second", "root aliases share acceptance-order FIFO");
        r.Operations.Pause(true);
        var paused = await t.Write("work", "paused.txt", "x", "paused-id", mode: "create");
        Check(VReply.Status(paused) == "paused" && paused.IsError == false && !File.Exists(Path.Combine(r.Config.Roots[0].Path, "paused.txt")), "pause returns normal PAUSED without accepting mutation");
        r.Operations.Pause(false);
        Check(Error(await t.Write("work", "paused.txt", "x", "paused-id", mode: "create")) is null, "same unaccepted paused request executes after resume");
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CallToolResult> blocker = r.Operations.Invoke("test", "context-block", "block", new { }, ["path:" + (OperatingSystem.IsWindows() ? r.Config.Roots[0].Path.ToUpperInvariant() : r.Config.Roots[0].Path)], ct => held.Task.WaitAsync(ct).ContinueWith(_ => VReply.Ok(new { })), 10000);
        var pending = t.Write("work", "captured-session.txt", "ok", "capture-session", mode: "create");
        context.HttpContext = null; held.SetResult(true); await blocker;
        Check(Error(await pending) is null && File.Exists(Path.Combine(r.Config.Roots[0].Path, "captured-session.txt")), "queued tool captures authenticated session before HTTP context ends");
        context.HttpContext = Context("test").HttpContext;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = r.Operations.Invoke("test", "cancel-block", "block", new { }, ["cancel-queue"], async ct => { await gate.Task.WaitAsync(ct); return VReply.Ok(new { }); }, 0); await block;
        await r.Operations.Invoke("test", "cancel-queued", "block", new { }, ["cancel-queue"], _ => throw new InvalidOperationException("cancelled work executed"), 0);
        r.Operations.Cancel("test", "cancel-queued"); gate.SetResult(true);
        var cancelled = await r.Operations.Invoke("test", "cancel-queued", "block", new { }, ["cancel-queue"], _ => throw new InvalidOperationException(), 10000);
        Check(Error(cancelled) == "CANCELLED" && cancelled.StructuredContent!.Value.GetProperty("error").GetProperty("side_effects").GetString() == "none", "queued cancellation reports no effects");
    }
    private static async Task ProcessTests(string parent)
    {
        var config = Config(parent); using var r = new Runtime(config); var t = new Tools(r, Context("process"));
        var (exe, args) = Self("emit");
        var result = await t.Shell("work", "emit", executable: exe, args: args, wait_ms: 10000);
        Check(Error(result) is null && D(result).GetProperty("exit_code").GetInt32() == 7, "real executable returns its failing exit code");
        Check(D(result).GetProperty("stdout").GetProperty("text").GetString() == "first\nsecond\n" && D(result).GetProperty("stderr").GetProperty("text").GetString() == "err-first\nerr-second\n", "stdout and stderr preserve separate byte order");
        string shell = OperatingSystem.IsWindows() ? "cmd" : "sh";
        string command = OperatingSystem.IsWindows() ? "echo shell-ok& exit /b 3" : "printf 'shell-ok\\n'; exit 3";
        var shellResult = await t.Shell("work", "actual-shell", shell: shell, command: command, wait_ms: 10000);
        Check(Error(shellResult) is null && D(shellResult).GetProperty("exit_code").GetInt32() == 3 && D(shellResult).GetProperty("stdout").GetProperty("text").GetString()!.Contains("shell-ok"), "explicit operating-system shell executes real command");
        var sleeper = Self("sleep");
        var wait = await t.Shell("work", "wait-test", executable: sleeper.exe, args: sleeper.args, wait_ms: 0);
        Check(VReply.Status(wait) == "running" && D(wait).GetProperty("operation_id").GetString() == "wait-test", "wait_ms returns operation handle without deadline");
        var started = await r.Processes.Start("process", "work", "", sleeper.exe, sleeper.args, "session");
        var persistent = await r.Processes.Start("process", "work", "", sleeper.exe, sleeper.args, "persistent");
        Check(!started.Launcher.HasExited && !persistent.Launcher.HasExited && started.TargetPid != started.Launcher.Id, "child and launcher are alive after response");
        Check(Error(r.Processes.Poll("other-session", started.Id)) == "NOT_FOUND", "process handle session ownership checked");
        if (OperatingSystem.IsWindows()) Check(started.Job is not null, "Windows Job established before target launch gate");
        var stdin = Self("stdin"); var input = await r.Processes.Start("process", "work", "", stdin.exe, stdin.args, "session");
        Check(Error(await t.ProcessWrite(input.Id, "한글 input\n", "stdin-write", true)) is null, "stdin text delivered and close requested");
        await input.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Check(D(t.ArtifactRead(input.Stdout)).GetProperty("text").GetString() == "한글 input\n", "stdin UTF8 roundtrip through real target");
        await r.Processes.EndSession("process");
        Check(started.Launcher.HasExited && !persistent.Launcher.HasExited, "session end kills session children but retains persistent process");
        Check(Error(await t.Stop(persistent.Id, "stop-persistent")) is null && persistent.Launcher.HasExited, "process.stop terminates managed persistent tree");
        var complete = r.Processes.Poll("process", started.Id);
        Check(D(complete).GetProperty("output_complete").GetBoolean(), "output is drained before completion flag");
        r.Operations.Cancel("process", "wait-test");
        await GitTests(r);
    }
    private static async Task GitTests(Runtime r)
    {
        if (r.Config.GitExecutable.Length == 0) throw new InvalidOperationException("Git is required on the test runner.");
        string root = r.Config.Roots[0].Path, git = r.Config.GitExecutable;
        await Exec(git, ["init", "-q"], root);
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "before\n");
        await Exec(git, ["add", "tracked.txt"], root);
        await Exec(git, ["-c", "user.name=Fixture", "-c", "user.email=fixture@invalid", "commit", "-qm", "fixture"], root);
        string hook = Path.Combine(root, "fake-hook.sh"); string marker = Path.Combine(root, "hook-ran");
        File.WriteAllText(hook, "#!/bin/sh\nprintf 'executed' > '" + marker.Replace('\\', '/') + "'\nprintf 'converted\\n'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string hookCommand = "sh '" + hook.Replace('\\', '/') + "'";
        await Exec(git, ["config", "diff.fixture.textconv", hookCommand], root);
        await Exec(git, ["config", "diff.external", hookCommand], root);
        await Exec(git, ["config", "core.fsmonitor", hook], root);
        await Exec(git, ["config", "core.pager", hookCommand], root);
        await Exec(git, ["config", "core.hooksPath", root], root);
        await Exec(git, ["config", "filter.fixture.clean", hookCommand], root);
        await Exec(git, ["config", "filter.fixture.process", hookCommand], root);
        await Exec(git, ["config", "filter.fixture.required", "true"], root);
        File.WriteAllText(Path.Combine(root, ".gitattributes"), "tracked.txt diff=fixture\n");
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "after\n");
        File.WriteAllText(Path.Combine(root, "untracked.txt"), "new\n");
        // Demonstrate that the malicious fixture would run without the disabled textconv path.
        await Exec(git, ["-c", "core.fsmonitor=false", "-c", "diff.external=", "diff", "--textconv", "--no-ext-diff", "--", "tracked.txt"], root);
        Check(File.Exists(marker), "malicious textconv fixture positive control actually executes"); File.Delete(marker);
        File.WriteAllText(Path.Combine(root, ".gitattributes"), "tracked.txt diff=fixture filter=fixture\n");
        r.Config.Roots = [r.Config.Roots[0] with { Shell = false }];
        var status = await r.Git("git", "work", "", "status");
        var diff = await r.Git("git", "work", "", "diff");
        var log = await r.Git("git", "work", "", "log");
        Check(D(status).GetProperty("exit_code").GetInt32() == 0 && D(status).GetProperty("stdout").GetProperty("text").GetString()!.Contains("untracked.txt"), "git.status includes untracked paths without shell grant");
        Check(D(diff).GetProperty("exit_code").GetInt32() == 0 && D(diff).GetProperty("stdout").GetProperty("text").GetString()!.Contains("+after"), "git.diff returns tracked real diff with textconv disabled");
        Check(D(log).GetProperty("exit_code").GetInt32() == 0 && D(log).GetProperty("stdout").GetProperty("text").GetString()!.Contains("fixture"), "git.log fixed format returns actual commit");
        Check(!File.Exists(marker), "read-only Git queries do not execute configured hook/fsmonitor/textconv/pager/external diff/clean/process filter fixture");
    }
    private static async Task Exec(string exe, string[] args, string cwd)
    {
        var s = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) s.ArgumentList.Add(arg);
        using var p = Process.Start(s)!; Task<string> output = p.StandardOutput.ReadToEndAsync(), error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(); await output; string err = await error;
        if (p.ExitCode != 0) throw new InvalidOperationException("Fixture setup command failed: " + exe + " " + string.Join(' ', args) + ": " + err);
    }
    public static async Task<int> Child(string[] args)
    {
        string mode = args[^1];
        if (mode == "sleep") { await Task.Delay(TimeSpan.FromSeconds(60)); return 0; }
        if (mode == "stdin") { await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput()); return 0; }
        if (mode == "emit")
        {
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes("first\nsecond\n"));
            await Console.OpenStandardError().WriteAsync(Encoding.UTF8.GetBytes("err-first\nerr-second\n")); return 7;
        }
        return 2;
    }
    private static async Task<JsonElement> Rpc(HttpClient http, string method, object parameters)
    {
        var body = new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters };
        using var response = await http.PostAsync("/mcp", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        string text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("MCP HTTP " + response.StatusCode + ": " + text);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream") text = string.Join("\n", text.Split('\n').Where(x => x.StartsWith("data: ")).Select(x => x[6..].TrimEnd('\r')));
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("result", out var result)) throw new InvalidOperationException("RPC error: " + text);
        return result.Clone();
    }
    private static async Task HttpTests(string parent, bool auth)
    {
        var config = Config(parent, auth); using var r = new Runtime(config); await using var app = Host.Build(r); await app.StartAsync();
        try
        {
            string[] urls = app.Urls.ToArray(); Check(urls.Length == 2, "distinct MCP and control loopback listeners");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(urls[0]), Timeout = TimeSpan.FromSeconds(15) };
            using var control = new HttpClient { BaseAddress = new Uri(urls[1]), Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
            if (auth) await OAuthTests(r, http);
            var initialized = await Rpc(http, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "v1-test", version = "1" } });
            Check(initialized.GetProperty("instructions").GetString() == Host.Instructions, "v1 initialize contains continuation and verification instructions");
            var list = await Rpc(http, "tools/list", new { }); var tools = list.GetProperty("tools").EnumerateArray().ToArray();
            Check(tools.Select(x => x.GetProperty("name").GetString()).Order().SequenceEqual(Tools.Names.Order()), "HTTP advertises exactly 21 implemented tools");
            Check(tools.All(x => x.GetProperty("inputSchema").GetProperty("type").GetString() == "object"), "SDK-generated schemas are objects");
            Check(!tools.Single(x => x.GetProperty("name").GetString() == "fs.write").GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "write tool not annotated read-only");
            var capabilities = await Rpc(http, "tools/call", new { name = "host.capabilities", arguments = new { } });
            Check(capabilities.GetProperty("structuredContent").GetProperty("data").GetProperty("execution_boundary").GetString() == "unconfined_user", "HTTP call resolves authenticated session and real capabilities");
            var write = await Rpc(http, "tools/call", new { name = "fs.write", arguments = new { root_id = "work", path = "http.txt", text = "http fixture", mode = "create", invocation_id = "http-write" } });
            Check(!write.GetProperty("isError").GetBoolean() && File.ReadAllText(Path.Combine(config.Roots[0].Path, "http.txt")) == "http fixture", "real MCP fs.write works through SDK dispatch");
            var request = new StringContent("{\"action\":\"pause\"}", Encoding.UTF8, "application/json");
            using var mainControl = new HttpRequestMessage(HttpMethod.Post, "/control") { Content = request }; mainControl.Headers.Add("X-Codexish-Control", r.ControlToken);
            using var mainDenied = await http.SendAsync(mainControl);
            Check(mainDenied.StatusCode == HttpStatusCode.Forbidden, "MCP listener refuses control even with local Host and correct token");
            using var badControl = new HttpRequestMessage(HttpMethod.Post, "/control") { Content = new StringContent("{\"action\":\"pause\"}", Encoding.UTF8, "application/json") }; badControl.Headers.Host = auth ? "mcp.example.test" : "attacker.invalid"; badControl.Headers.Add("X-Codexish-Control", r.ControlToken);
            using var denied = await control.SendAsync(badControl); Check(denied.StatusCode == HttpStatusCode.Forbidden, "control rejects tunnel/public Host");
            using var noToken = await control.PostAsync("/control", new StringContent("{\"action\":\"pause\"}", Encoding.UTF8, "application/json"));
            Check(noToken.StatusCode == HttpStatusCode.Forbidden, "control requires local control token");
            control.DefaultRequestHeaders.Add("X-Codexish-Control", r.ControlToken);
            using var pause = await control.PostAsync("/control", new StringContent("{\"action\":\"pause\"}", Encoding.UTF8, "application/json"));
            Check(pause.IsSuccessStatusCode && r.Operations.Paused, "local control pause accepted");
            var paused = await Rpc(http, "tools/call", new { name = "fs.write", arguments = new { root_id = "work", path = "pause.txt", text = "no", mode = "create", invocation_id = "http-paused" } });
            Check(!paused.GetProperty("isError").GetBoolean() && paused.GetProperty("structuredContent").GetProperty("status").GetString() == "paused", "HTTP paused mutation returns isError false");
            using var resume = await control.PostAsync("/control", new StringContent("{\"action\":\"resume\"}", Encoding.UTF8, "application/json"));
            Check(resume.IsSuccessStatusCode && !r.Operations.Paused, "local control resume accepted");
            if (auth)
            {
                using var revoke = await control.PostAsync("/control", new StringContent("{\"action\":\"revoke-tokens\"}", Encoding.UTF8, "application/json"));
                using var unauthorized = await http.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
                Check(revoke.IsSuccessStatusCode && unauthorized.StatusCode == HttpStatusCode.Unauthorized, "local revoke invalidates bearer on subsequent MCP request");
            }
            string? evidence = Environment.GetEnvironmentVariable("CODEXISH_TEST_OUTPUT");
            if (evidence is not null) { Directory.CreateDirectory(evidence); await File.WriteAllTextAsync(Path.Combine(evidence, "v1-tools.json"), list.GetRawText()); }
        }
        finally { await app.StopAsync(); }
    }
    private static async Task OAuthTests(Runtime r, HttpClient http)
    {
        var config = r.Config; http.DefaultRequestHeaders.Host = "mcp.example.test";
        Check(Passwords.Verify("fixture-password-not-real", config.PasswordHash) && !Passwords.Verify("incorrect", config.PasswordHash), "PBKDF2 password verification");
        using var unauth = await http.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Check(unauth.StatusCode == HttpStatusCode.Unauthorized && unauth.Headers.WwwAuthenticate.ToString().Contains("resource_metadata"), "MCP 401 supplies protected resource metadata challenge");
        using var prm = await http.GetAsync("/.well-known/oauth-protected-resource");
        var resource = JsonDocument.Parse(await prm.Content.ReadAsStringAsync()).RootElement;
        Check(resource.GetProperty("resource").GetString() == r.OAuth.Resource, "canonical OAuth resource metadata");
        using var asm = await http.GetAsync("/.well-known/oauth-authorization-server");
        var metadata = JsonDocument.Parse(await asm.Content.ReadAsStringAsync()).RootElement;
        Check(metadata.GetProperty("code_challenge_methods_supported")[0].GetString() == "S256", "AS advertises PKCE S256");
        string verifier = new('a', 64), challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string Authorize(string redirect, string audience) => QueryHelpers.AddQueryString("/authorize", new Dictionary<string, string?> { ["response_type"] = "code", ["client_id"] = config.ClientId, ["redirect_uri"] = redirect, ["resource"] = audience, ["code_challenge_method"] = "S256", ["code_challenge"] = challenge, ["scope"] = "codexish offline_access", ["state"] = "fixture-state" });
        using var badRedirect = await http.GetAsync(Authorize("https://attacker.invalid/callback", r.OAuth.Resource));
        Check(badRedirect.StatusCode == HttpStatusCode.BadRequest && badRedirect.Headers.Location is null, "unregistered redirect rejected without redirecting");
        using var badAudience = await http.GetAsync(Authorize(config.RedirectUris[0], "https://other.invalid/mcp"));
        Check(badAudience.StatusCode == HttpStatusCode.BadRequest, "wrong authorization resource rejected");
        async Task<string> Code(bool testLogin = false)
        {
            using var login = await http.GetAsync(Authorize(config.RedirectUris[0], r.OAuth.Resource));
            string html = await login.Content.ReadAsStringAsync(); string ticket = Regex.Match(html, "name=\"ticket\" value=\"([^\"]+)\"").Groups[1].Value;
            if (ticket.Length == 0) throw new InvalidOperationException("Login ticket missing: " + html);
            if (testLogin)
            {
                using var missingCookie = await http.PostAsync("/authorize", new FormUrlEncodedContent(new Dictionary<string, string> { ["ticket"] = ticket, ["password"] = "fixture-password-not-real" }));
                Check(missingCookie.StatusCode == HttpStatusCode.BadRequest, "login ticket requires matching CSRF cookie");
            }
            using var post = new HttpRequestMessage(HttpMethod.Post, "/authorize") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["ticket"] = ticket, ["password"] = "fixture-password-not-real" }) };
            post.Headers.Add("Cookie", "codexish-login=" + ticket);
            using var response = await http.SendAsync(post);
            if (response.StatusCode != HttpStatusCode.Redirect) throw new InvalidOperationException("Login failed: " + await response.Content.ReadAsStringAsync());
            var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            Check(query["state"].ToString() == "fixture-state" && query["iss"].ToString() == config.PublicUrl, "authorization preserves state and returns issuer");
            return query["code"].ToString();
        }
        Dictionary<string, string> TokenForm(string code) => new() { ["grant_type"] = "authorization_code", ["client_id"] = config.ClientId, ["client_secret"] = config.ClientSecret, ["code"] = code, ["redirect_uri"] = config.RedirectUris[0], ["resource"] = r.OAuth.Resource, ["code_verifier"] = verifier };
        async Task<(HttpStatusCode status, JsonElement data)> Exchange(Dictionary<string, string> form)
        {
            using var response = await http.PostAsync("/token", new FormUrlEncodedContent(form));
            using var d = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return (response.StatusCode, d.RootElement.Clone());
        }
        string code = await Code(true); var bad = TokenForm(code); bad["code_verifier"] = new string('b', 64);
        Check((await Exchange(bad)).status == HttpStatusCode.BadRequest, "wrong PKCE verifier rejected");
        var wrongClient = TokenForm(code); wrongClient["client_secret"] = "wrong";
        Check((await Exchange(wrongClient)).status == HttpStatusCode.Unauthorized, "wrong static client secret rejected");
        var wrongResource = TokenForm(code); wrongResource["resource"] = "https://other.invalid/mcp";
        Check((await Exchange(wrongResource)).status == HttpStatusCode.BadRequest, "token exchange enforces resource audience");
        var good = await Exchange(TokenForm(code)); Check(good.status == HttpStatusCode.OK && good.data.GetProperty("expires_in").GetInt32() == 43200, "valid authorization code exchange issues 12-hour access token");
        string access = good.data.GetProperty("access_token").GetString()!, refresh = good.data.GetProperty("refresh_token").GetString()!;
        Check((await Exchange(TokenForm(code))).status == HttpStatusCode.BadRequest, "authorization code consumed exactly once");
        Check(r.Store.Scalar("SELECT hash FROM tokens WHERE hash=$h", ("$h", access)) is null && r.Store.Scalar("SELECT hash FROM tokens WHERE hash=$h", ("$h", OAuth.Digest(access))) is not null, "tokens stored by hash, not bearer plaintext");
        string? session = r.OAuth.ValidateAccess(access); Check(session is not null, "issued bearer validates to a logical session");
        var now = DateTimeOffset.UtcNow; r.OAuth.Clock = () => now.AddHours(13);
        Check(r.OAuth.ValidateAccess(access) is null, "expired access token rejected");
        var refreshForm = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = config.ClientId, ["client_secret"] = config.ClientSecret, ["resource"] = r.OAuth.Resource, ["refresh_token"] = refresh };
        var rotated = await Exchange(refreshForm); string rotatedAccess = rotated.data.GetProperty("access_token").GetString()!;
        Check(rotated.status == HttpStatusCode.OK && r.OAuth.ValidateAccess(rotatedAccess) == session, "refresh rotates tokens and preserves logical session");
        Check((await Exchange(refreshForm)).status == HttpStatusCode.BadRequest && r.OAuth.ValidateAccess(rotatedAccess) is null, "refresh reuse rejected and token family revoked");
        r.OAuth.Clock = () => DateTimeOffset.UtcNow;
        string expiredCode = await Code(); r.OAuth.Clock = () => DateTimeOffset.UtcNow.AddMinutes(6);
        Check((await Exchange(TokenForm(expiredCode))).status == HttpStatusCode.BadRequest, "expired authorization code rejected");
        r.OAuth.Clock = () => DateTimeOffset.UtcNow;
        var final = await Exchange(TokenForm(await Code()));
        http.DefaultRequestHeaders.Authorization = new("Bearer", final.data.GetProperty("access_token").GetString());
    }
}
