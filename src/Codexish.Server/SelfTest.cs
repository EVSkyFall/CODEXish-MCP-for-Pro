using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

// Every check below runs against real files, a real SQLite ledger, real child processes, a real git binary and
// a real in-process HTTP server. Nothing is stubbed and no result is asserted without exercising the code path.
public static class SelfTest
{
    private static int passed;

    public static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine($"PASS {++passed:00} {label}");
    }

    public static void Skip(string label) => Console.WriteLine($"SKIP {label}");

    public static JsonElement Data(CallToolResult result) => result.StructuredContent!.Value.GetProperty("data");
    public static JsonElement Output(CallToolResult result) => result.StructuredContent!.Value.GetProperty("output");
    public static string? Error(CallToolResult result) => Reply.CodeOf(result);
    public static string? Status(CallToolResult result) => Reply.StatusOf(result);
    public static string? Effects(CallToolResult result) =>
        result.StructuredContent!.Value.GetProperty("error").GetProperty("side_effects").GetString();
    public static string? Reason(CallToolResult result) =>
        result.StructuredContent!.Value.GetProperty("error").GetProperty("reason").GetString();
    public static string Text(CallToolResult result) => Output(result).GetProperty("text").GetString() ?? "";

    public static ServerConfig BuildConfig(string temp, string password, params string[] allowHosts)
    {
        string root = Path.Combine(temp, "proj");
        Directory.CreateDirectory(root);
        var config = new ServerConfig
        {
            PublicUrl = "https://codexish.test",
            Port = 0,
            AllowHosts = allowHosts,
            StateDir = Path.Combine(temp, "state"),
            Roots = [new RootConfig { Id = "proj", Path = root }],
            Git = new GitConfig { Path = ServerConfig.FindGit() },
            ControlToken = ServerConfig.NewSecret()
        };
        config.OAuth.ClientSecret = ServerConfig.NewSecret();
        config.OAuth.PasswordHash = ServerConfig.HashPassword(password);
        config.OAuth.RedirectUris = [ServerConfig.DefaultRedirectUri, "https://chatgpt.com/aip/callback"];
        config.Validate();
        return config;
    }

    public static async Task<int> Run()
    {
        string temp = Path.Combine(Path.GetTempPath(), "codexish-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string password = ServerConfig.NewSecret(12);
        try
        {
            var config = BuildConfig(temp, password, "codexish.test");
            string root = config.Roots[0].Path;
            using (var runtime = new CodexishRuntime(config))
            {
                var tools = new CodexishTools(runtime);
                Capabilities(runtime, tools);
                Fences(runtime, tools, root);
                Paging(tools, root);
                await Writes(runtime, tools, root);
                await Patches(tools, root);
                await LedgerTests(runtime, tools);
                await ProcessTests.Run(runtime, tools, root);
                await GitTests.Run(runtime, tools, root);
                await ArtifactTests.Run(runtime, tools);
                await HttpTests.Run(runtime, config, password);
            }
            Restart(config);
            Console.WriteLine($"SELF_TEST_PASSED: {passed}; no tunnel was opened, no desktop input was sent, " +
                "and no ChatGPT connector measurement was performed.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"SELF_TEST_FAILED after {passed}: {e}");
            return 1;
        }
        finally
        {
            // This directory was created by this test invocation only. A child process that has not finished
            // exiting can still hold its working directory, so the delete is retried before giving up loudly.
            // A recursive delete can also return while a deleted file is still held open elsewhere, leaving the
            // emptied directories behind, so success means the directory is actually gone.
            bool removed = false;
            for (int attempt = 0; attempt < 20 && !removed; attempt++)
            {
                try { ForceDelete(temp); removed = !Directory.Exists(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (!removed) Thread.Sleep(500);
            }
            if (!removed) Console.Error.WriteLine($"WARNING: could not remove the test directory {temp}");
        }
    }

    // Git marks loose objects read-only on both platforms, which a plain recursive delete refuses to remove.
    private static void ForceDelete(string path)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)) Unlock(entry);
        Unlock(path);
        Directory.Delete(path, true);
    }

    private static void Unlock(string entry)
    {
        try
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception) { /* an entry that cannot be unlocked is reported by the delete retry loop */ }
    }

    private static void Capabilities(CodexishRuntime runtime, CodexishTools tools)
    {
        var capabilities = Data(tools.HostCapabilities());
        Check(capabilities.GetProperty("schema_version").GetString() == "v1.0", "envelope reports schema_version v1.0");
        Check(capabilities.GetProperty("execution_boundary").GetString() == "unconfined_user", "capabilities state the unconfined execution boundary");
        Check(capabilities.GetProperty("tools").EnumerateArray().Count() == 24, "capabilities list 21 coding and 3 desktop tools");
        Check(capabilities.GetProperty("desktop").GetProperty("native_available").GetBoolean() == OperatingSystem.IsWindows(),
            "capabilities report the actual desktop platform");
        Check(capabilities.GetProperty("limits").GetProperty("source").GetString()!.Contains("frame budget"),
            "frame and page limits are reported with their source");
        var workspace = Data(tools.WorkspaceInfo());
        Check(workspace.GetProperty("roots").EnumerateArray().Single().GetProperty("id").GetString() == "proj",
            "workspace_info lists the configured root and its grants");
        Check(!workspace.GetProperty("state_dir").GetString()!.StartsWith(runtime.Config.Roots[0].Path, StringComparison.OrdinalIgnoreCase),
            "state directory lies outside every root");
        var checkpoint = tools.SessionCheckpoint("fix the failing test", "dotnet test exits 0", ["read the file"], ["fix it"], ["proj/src"]);
        Check(Status(checkpoint) == "succeeded", "session_checkpoint stores an observable goal");
        Check(Data(tools.HostCapabilities()).GetProperty("checkpoint").GetProperty("checkpoint").GetProperty("goal").GetString()
            == "fix the failing test", "host_capabilities returns the latest checkpoint");
        Check(Error(tools.SessionCheckpoint("", "", [], [], [])) == "INVALID_ARGUMENT", "checkpoint requires an observable goal");

        // Two ids over one directory would give the same files two independent FIFO queues.
        string root = runtime.Config.Roots[0].Path;
        var overlapping = new ServerConfig
        {
            StateDir = runtime.Config.StateDir + "-overlap",
            Roots = [new RootConfig { Id = "outer", Path = root }, new RootConfig { Id = "inner", Path = Path.Combine(root, "src") }]
        };
        Check(Refused(overlapping.Validate), "a root nested inside another root is refused at startup");
        var duplicated = new ServerConfig
        {
            StateDir = runtime.Config.StateDir + "-overlap",
            Roots = [new RootConfig { Id = "one", Path = root }, new RootConfig { Id = "two", Path = root }]
        };
        Check(Refused(duplicated.Validate), "two ids over the same directory are refused at startup");
    }

    private static bool Refused(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }

    private static void Fences(CodexishRuntime runtime, CodexishTools tools, string root)
    {
        File.WriteAllText(Path.Combine(root, "readme.txt"), "alpha\nbeta\ngamma\n", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "app.cs"), "// token here\nint answer = 42;\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "src", "notes.md"), "answer = 42 twice: 42\n", new UTF8Encoding(false));

        var read = tools.FsRead("proj", "readme.txt", null, null, null, null);
        Check(Text(read) == "alpha\nbeta\ngamma\n", "fs_read returns the actual file text");
        Check(Data(read).GetProperty("newline").GetString() == "lf" && Data(read).GetProperty("line_count").GetInt32() == 4,
            "fs_read reports newline style and line count");
        var ranged = tools.FsRead("proj", "readme.txt", 2, 2, null, null);
        Check(Text(ranged) == "beta" && !Output(ranged).GetProperty("complete").GetBoolean(),
            "fs_read line range is marked incomplete");
        var listed = tools.FsList("proj", "", 2, false, null);
        Check(Data(listed).GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("path").GetString() == "src/app.cs"),
            "fs_list walks to the requested depth");
        var stat = tools.FsStat("proj", "readme.txt", true);
        Check(Data(stat).GetProperty("sha256").GetString() == Data(read).GetProperty("sha256").GetString(),
            "fs_stat hash matches fs_read");
        var search = tools.FsSearch("proj", "42", "", false, "src/**/*.md", null);
        Check(Data(search).GetProperty("matches").EnumerateArray().Count() == 2, "fs_search returns every match on a line with a glob filter");
        var regex = tools.FsSearch("proj", @"answer\s*=\s*\d+", "", true, null, null);
        Check(Data(regex).GetProperty("matches").EnumerateArray().Any(m => m.GetProperty("path").GetString() == "src/app.cs"),
            "fs_search regex mode finds a pattern across files");

        Check(Error(tools.FsRead("proj", "../outside.txt", null, null, null, null)) == "OUTSIDE_WORKSPACE", "parent traversal is rejected");
        Check(Error(tools.FsRead("proj", @"C:\Windows\win.ini", null, null, null, null)) == "OUTSIDE_WORKSPACE", "absolute paths are rejected");
        Check(Error(tools.FsRead("proj", @"\\server\share\file.txt", null, null, null, null)) == "OUTSIDE_WORKSPACE", "UNC paths are rejected");
        Check(Error(tools.FsRead("nope", "readme.txt", null, null, null, null)) == "NOT_FOUND", "an unknown root_id is rejected");

        string secret = "ghp_" + new string('a', 36);
        File.WriteAllText(Path.Combine(root, "leak.txt"), $"token={secret}\n", new UTF8Encoding(false));
        var leaked = tools.FsRead("proj", "leak.txt", null, null, null, null);
        Check(!Text(leaked).Contains(secret) && Text(leaked).Contains("[REDACTED:github_token]"), "fs_read redacts a known credential pattern");
        Check(Data(leaked).GetProperty("redacted").GetBoolean(), "the redaction flag is set on the result");

        if (OperatingSystem.IsWindows())
        {
            string junction = Path.Combine(root, "escape");
            var link = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/s /c \"mklink /J \"{junction}\" \"{Path.GetTempPath().TrimEnd('\\')}\"\"",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            })!;
            link.WaitForExit();
            if (link.ExitCode == 0 && Directory.Exists(junction))
            {
                Check(Error(tools.FsList("proj", "escape", 1, false, null)) == "UNSUPPORTED_CAPABILITY",
                    "a junction inside the root is refused instead of followed");
                Check(Data(tools.FsList("proj", "", 1, false, null)).GetProperty("entries").EnumerateArray()
                    .Any(e => e.GetProperty("path").GetString() == "escape" && e.GetProperty("kind").GetString() == "reparse_point"),
                    "fs_list reports a junction as reparse_point without traversing it");
                Directory.Delete(junction);

            }
            else Skip("junction fence: mklink /J unavailable in this session");
        }
        else Skip("junction fence and Windows sharing checks: not Windows");

        // A link whose target does not exist must still be refused as a reparse point, whatever an existence
        // check happens to say about it on this platform.
        string dangling = Path.Combine(root, "dangling.txt");
        bool created = false;
        try { File.CreateSymbolicLink(dangling, "missing-target.txt"); created = true; }
        catch (Exception) { Skip("dangling link fence: this session may not create symbolic links"); }
        if (created)
        {
            // File.Exists is platform-dependent for a broken link (.NET falls back to lstat on Unix and reads the
            // link's own attributes on Windows), so it is recorded, not asserted; the fence must not depend on it.
            Console.WriteLine($"NOTE dangling link: File.Exists={File.Exists(dangling)} on {Environment.OSVersion.Platform}");
            Check(Error(tools.FsRead("proj", "dangling.txt", null, null, null, null)) == "UNSUPPORTED_CAPABILITY",
                "a dangling reparse point is refused even though an existence check says it is not there");
            File.Delete(dangling);
        }
    }

    // A page boundary must neither drop an entry nor return one twice; the fixture is larger than one page
    // in both directions and is removed again so it does not distort the later git checks.
    private static void Paging(CodexishTools tools, string root)
    {
        string many = Path.Combine(root, "many");
        Directory.CreateDirectory(many);
        var utf8 = new UTF8Encoding(false);
        const int files = 620;
        for (int i = 0; i < files; i++)
            File.WriteAllText(Path.Combine(many, $"f{i:0000}.txt"), "needle one\nneedle two\n", utf8);

        HashSet<string> listed = [];
        bool duplicate = false;
        string? cursor = null;
        int pages = 0;
        while (pages < 20)
        {
            var page = tools.FsList("proj", "many", 1, false, cursor);
            foreach (var entry in Data(page).GetProperty("entries").EnumerateArray())
                if (!listed.Add(entry.GetProperty("path").GetString()!)) duplicate = true;
            pages++;
            if (Output(page).GetProperty("complete").GetBoolean()) break;
            cursor = Output(page).GetProperty("next_cursor").GetString();
        }
        Check(pages > 1 && listed.Count == files && !duplicate,
            $"fs_list paged {files} entries over {pages} pages with no entry lost or repeated");

        HashSet<string> found = [];
        duplicate = false;
        cursor = null;
        pages = 0;
        while (pages < 50)
        {
            var page = tools.FsSearch("proj", "needle", "many", false, null, cursor);
            foreach (var match in Data(page).GetProperty("matches").EnumerateArray())
                if (!found.Add(match.GetProperty("path").GetString() + ":" + match.GetProperty("line").GetInt32()))
                    duplicate = true;
            pages++;
            if (Output(page).GetProperty("complete").GetBoolean()) break;
            cursor = Output(page).GetProperty("next_cursor").GetString();
        }
        Check(pages > 1 && found.Count == files * 2 && !duplicate,
            $"fs_search paged {files * 2} matches over {pages} pages with no match lost or repeated");
        Check(Error(tools.FsList("proj", "many", 1, false, "not-a-cursor")) == "CURSOR_INVALID",
            "a cursor this server did not issue is refused");
        Directory.Delete(many, true);
    }

    private static async Task Writes(CodexishRuntime runtime, CodexishTools tools, string root)
    {
        string path = Path.Combine(root, "readme.txt");
        string hash = Data(tools.FsRead("proj", "readme.txt", null, null, null, null)).GetProperty("sha256").GetString()!;
        Check(Error(await tools.FsWrite("proj", "readme.txt", "x\n", "replace", "bad-hash", "0000")) == "FILE_CHANGED",
            "replace requires a matching expected_sha256");
        Check(File.ReadAllText(path) == "alpha\nbeta\ngamma\n", "a hash mismatch leaves the original bytes untouched");
        var wrote = await tools.FsWrite("proj", "readme.txt", "alpha\ndelta\n", "replace", "w1", hash);
        Check(Status(wrote) == "succeeded" && File.ReadAllText(path) == "alpha\ndelta\n", "replace rewrites the file in place");
        string backup = Data(wrote).GetProperty("backup").GetString()!;
        Check(File.ReadAllText(Path.Combine(runtime.Config.StateDir, backup.Replace('/', Path.DirectorySeparatorChar)))
            == "alpha\nbeta\ngamma\n", "the previous bytes are written to a backup in the state directory");
        Check(Data(wrote).GetProperty("save_mode").GetString() == "exclusive_in_place_non_atomic", "the non-atomic contract is disclosed");

        Check(Error(await tools.FsWrite("proj", "readme.txt", "y\n", "create", "c1")) == "FILE_CHANGED", "create refuses an existing file");
        var created = await tools.FsWrite("proj", "src/new.txt", "fresh\n", "create", "c2");
        Check(Status(created) == "succeeded" && File.ReadAllText(Path.Combine(root, "src", "new.txt")) == "fresh\n",
            "create writes a new file atomically");
        Check(Data(created).GetProperty("save_mode").GetString() == "atomic_create_new", "create reports atomic_create_new");

        string utf16 = Path.Combine(root, "utf16.txt");
        File.WriteAllText(utf16, "old\r\n", new UnicodeEncoding(false, true, true));
        string utf16Hash = Data(tools.FsRead("proj", "utf16.txt", null, null, null, null)).GetProperty("sha256").GetString()!;
        await tools.FsWrite("proj", "utf16.txt", "new\n", "replace", "w2", utf16Hash);
        byte[] bytes = File.ReadAllBytes(utf16);
        Check(bytes[0] == 0xff && bytes[1] == 0xfe && TextCodec.Decode(bytes).Text == "new\r\n", "UTF-16 BOM and CRLF are preserved");

        string bom = Path.Combine(root, "bom.txt");
        File.WriteAllText(bom, "old\n", new UTF8Encoding(true));
        string bomHash = Data(tools.FsRead("proj", "bom.txt", null, null, null, null)).GetProperty("sha256").GetString()!;
        await tools.FsWrite("proj", "bom.txt", "new\r\n", "replace", "w3", bomHash);
        bytes = File.ReadAllBytes(bom);
        Check(bytes[0] == 0xef && TextCodec.Decode(bytes).Text == "new\n", "UTF-8 BOM is preserved and CRLF input is written as LF");

        string mixed = Path.Combine(root, "mixed.txt");
        File.WriteAllText(mixed, "a\r\nb\n", new UTF8Encoding(false));
        string mixedHash = Data(tools.FsRead("proj", "mixed.txt", null, null, null, null)).GetProperty("sha256").GetString()!;
        Check(Data(tools.FsRead("proj", "mixed.txt", null, null, null, null)).GetProperty("newline").GetString() == "mixed",
            "fs_read reports a mixed newline file as mixed");
        Check(Error(await tools.FsWrite("proj", "mixed.txt", "x", "replace", "w4", mixedHash)) == "UNSUPPORTED_CAPABILITY",
            "rewriting a mixed newline file is refused, not guessed");

        byte[] binary = [0x50, 0x4b, 0x03, 0x04, 0x00, 0x01, 0x02];
        File.WriteAllBytes(Path.Combine(root, "blob.bin"), binary);
        var binaryRead = tools.FsRead("proj", "blob.bin", null, null, null, null);
        Check(Data(binaryRead).GetProperty("binary").GetBoolean(), "a binary file is detected");
        string artifact = Data(binaryRead).GetProperty("artifact_id").GetString()!;
        Check(runtime.Artifacts.Length(artifact) == binary.Length, "a binary file is returned as an artifact, not as text");

        if (OperatingSystem.IsWindows())
        {
            using var other = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string current = Data(tools.FsRead("proj", "readme.txt", null, null, null, null)).GetProperty("sha256").GetString()!;
            Check(Error(await tools.FsWrite("proj", "readme.txt", "z\n", "replace", "w5", current)) == "FILE_LOCKED",
                "an incompatible share on Windows returns FILE_LOCKED instead of a partial write");
        }
    }

    private static async Task Patches(CodexishTools tools, string root)
    {
        File.WriteAllText(Path.Combine(root, "one.txt"), "one\ntwo\nthree\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "two.txt"), "alpha\nbeta\n", new UTF8Encoding(false));
        string oneHash = Data(tools.FsRead("proj", "one.txt", null, null, null, null)).GetProperty("sha256").GetString()!;
        string twoHash = Data(tools.FsRead("proj", "two.txt", null, null, null, null)).GetProperty("sha256").GetString()!;

        string patch = """
            --- a/one.txt
            +++ b/one.txt
            @@ -1,3 +1,3 @@
             one
            -two
            +TWO
             three
            --- a/two.txt
            +++ b/two.txt
            @@ -1,2 +1,2 @@
             alpha
            -beta
            +BETA

            """;
        var badPreflight = await tools.FsApplyPatch("proj", patch,
            [new PatchExpectation { Path = "one.txt", ExpectedSha256 = oneHash },
             new PatchExpectation { Path = "two.txt", ExpectedSha256 = new string('0', 64) }], "p1");
        Check(Error(badPreflight) == "PATCH_FAILED" && Effects(badPreflight) == "none", "a preflight failure reports no side effects");
        Check(File.ReadAllText(Path.Combine(root, "one.txt")) == "one\ntwo\nthree\n", "a preflight failure changes nothing at all");

        var applied = await tools.FsApplyPatch("proj", patch,
            [new PatchExpectation { Path = "one.txt", ExpectedSha256 = oneHash },
             new PatchExpectation { Path = "two.txt", ExpectedSha256 = twoHash }], "p2");
        Check(Status(applied) == "succeeded" && File.ReadAllText(Path.Combine(root, "one.txt")) == "one\nTWO\nthree\n" &&
            File.ReadAllText(Path.Combine(root, "two.txt")) == "alpha\nBETA\n", "a multi-file patch applies to every file");
        Check(Data(applied).GetProperty("files").EnumerateArray().Count() == 2, "every file in the patch gets its own result");

        string createPatch = """
            --- /dev/null
            +++ b/created.txt
            @@ -0,0 +1,2 @@
            +brand
            +new

            """;
        var createdResult = await tools.FsApplyPatch("proj", createPatch,
            [new PatchExpectation { Path = "created.txt", ExpectedSha256 = null }], "p3");
        Check(Status(createdResult) == "succeeded" && File.ReadAllText(Path.Combine(root, "created.txt")) == "brand\nnew\n",
            "a patch creates a file when expected_sha256 is empty");
        var recreate = await tools.FsApplyPatch("proj", createPatch,
            [new PatchExpectation { Path = "created.txt", ExpectedSha256 = null }], "p4");
        Check(Error(recreate) == "PATCH_FAILED", "a create hunk is refused when the file already exists");

        string createdHash = Data(tools.FsRead("proj", "created.txt", null, null, null, null)).GetProperty("sha256").GetString()!;
        string deletePatch = """
            --- a/created.txt
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -brand
            -new

            """;
        Check(Error(await tools.FsApplyPatch("proj", deletePatch,
            [new PatchExpectation { Path = "created.txt", ExpectedSha256 = new string('1', 64) }], "p5")) == "PATCH_FAILED",
            "a delete hunk verifies expected_sha256 before removing anything");
        Check(File.Exists(Path.Combine(root, "created.txt")), "the file survives a failed delete preflight");
        var deleted = await tools.FsApplyPatch("proj", deletePatch,
            [new PatchExpectation { Path = "created.txt", ExpectedSha256 = createdHash }], "p6");
        Check(Status(deleted) == "succeeded" && !File.Exists(Path.Combine(root, "created.txt")),
            "a verified delete hunk removes the file after backing it up");

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(root, "three.txt"), "x\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "four.txt"), "y\n", new UTF8Encoding(false));
            string threeHash = Data(tools.FsRead("proj", "three.txt", null, null, null, null)).GetProperty("sha256").GetString()!;
            string fourHash = Data(tools.FsRead("proj", "four.txt", null, null, null, null)).GetProperty("sha256").GetString()!;
            string partial = """
                --- a/three.txt
                +++ b/three.txt
                @@ -1 +1 @@
                -x
                +X
                --- a/four.txt
                +++ b/four.txt
                @@ -1 +1 @@
                -y
                +Y

                """;
            using (new FileStream(Path.Combine(root, "four.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var mixedResult = await tools.FsApplyPatch("proj", partial,
                    [new PatchExpectation { Path = "three.txt", ExpectedSha256 = threeHash },
                     new PatchExpectation { Path = "four.txt", ExpectedSha256 = fourHash }], "p7");
                Check(Effects(mixedResult) == "partial", "a per-file failure after preflight is reported as a partial effect");
                var perFile = Data(mixedResult).GetProperty("files").EnumerateArray().ToArray();
                Check(perFile[0].GetProperty("status").GetString() == "succeeded" &&
                      perFile[1].GetProperty("code").GetString() == "FILE_LOCKED",
                    "each file reports its own outcome and nothing is rolled back");
            }
            Check(File.ReadAllText(Path.Combine(root, "three.txt")) == "X\n" && File.ReadAllText(Path.Combine(root, "four.txt")) == "y\n",
                "the applied file keeps its change and the failed file is untouched");
        }
    }

    private static async Task LedgerTests(CodexishRuntime runtime, CodexishTools tools)
    {
        var ledger = runtime.Ledger;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> order = [];
        int effects = 0;

        Task<CallToolResult> Slow(string id, int waitMs) => ledger.Invoke(id, "test_slow", new { value = 1 }, "files:proj", async _ =>
        {
            entered.TrySetResult(true);
            await gate.Task;
            Interlocked.Increment(ref effects);
            lock (order) order.Add("slow");
            return Reply.Ok(new { done = true });
        }, waitMs);

        var slow = await Slow("fifo-1", 0);
        Check(Status(slow) == "running", "a response wait that elapses returns an operation handle, not a failure");
        await entered.Task;
        var joined = Slow("fifo-1", 10000);
        var second = ledger.Invoke("fifo-2", "test_fast", new { value = 2 }, "files:proj", _ =>
        {
            lock (order) order.Add("fast");
            return Task.FromResult(Reply.Ok(new { done = true }));
        }, 0);
        Check(Status(await second) == "queued" || Status(await second) == "running",
            "a second invocation on a busy resource is accepted, never rejected as busy");
        var conflict = await ledger.Invoke("fifo-1", "test_slow", new { value = 99 }, "files:proj",
            _ => Task.FromResult(Reply.Ok(new { })), 0);
        Check(Error(conflict) == "IDEMPOTENCY_CONFLICT", "the same invocation_id with different arguments conflicts");
        Check(Status(tools.FsRead("proj", "readme.txt", null, null, null, null)) == "succeeded",
            "reads proceed while a resource queue is busy");
        var inspected = tools.OperationInspect("fifo-2");
        Check(Status(inspected) is "queued" or "running", "operation_inspect reports queued work before it runs");
        gate.SetResult(true);
        Check(Status(await joined) == "succeeded" && effects == 1, "the same id and digest joins the live effect instead of repeating it");
        await ledger.Invoke("fifo-2", "test_fast", new { value = 2 }, "files:proj", _ => throw new Exception("must not run twice"), 5000);
        Check(order.Count == 2 && order[0] == "slow" && order[1] == "fast",
            "per-resource execution order equals acceptance order even when the first item is slow");
        Check(Error(tools.OperationInspect("never-accepted")) == "NOT_FOUND", "an unknown operation_id is NOT_FOUND, not an assumed success");

        var cancelGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = ledger.Invoke("cancel-blocker", "test_block", new { }, "files:cancel", async _ =>
        {
            await cancelGate.Task;
            return Reply.Ok(new { });
        }, 0);
        var queued = ledger.Invoke("cancel-target", "test_queued", new { }, "files:cancel",
            _ => Task.FromResult(Reply.Ok(new { ran = true })), 10000);
        await blocker;
        var cancelled = tools.OperationCancel("cancel-target");
        Check(Data(cancelled).GetProperty("cancelled").GetBoolean(), "a queued operation can be cancelled before it runs");
        cancelGate.SetResult(true);
        var cancelledResult = await queued;
        Check(Error(cancelledResult) == "CANCELLED" && Effects(cancelledResult) == "none",
            "a cancelled queued operation reports no side effects");

        // Cancelling one queued item must not let a later item overtake the running one: the chain keeps its
        // acceptance order and the cancelled item simply never runs.
        var holdGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> chainOrder = [];
        var running = ledger.Invoke("chain-running", "test_hold", new { }, "files:chain", async _ =>
        {
            holdEntered.TrySetResult(true);
            await holdGate.Task;
            lock (chainOrder) chainOrder.Add("running");
            return Reply.Ok(new { });
        }, 0);
        var doomed = ledger.Invoke("chain-cancelled", "test_doomed", new { }, "files:chain", _ =>
        {
            lock (chainOrder) chainOrder.Add("cancelled-ran");
            return Task.FromResult(Reply.Ok(new { }));
        }, 15000);
        var follower = ledger.Invoke("chain-follower", "test_follower", new { }, "files:chain", _ =>
        {
            lock (chainOrder) chainOrder.Add("follower");
            return Task.FromResult(Reply.Ok(new { }));
        }, 15000);
        await running;
        await holdEntered.Task;
        var cancelQueued = tools.OperationCancel("chain-cancelled");
        Check(Data(cancelQueued).GetProperty("cancelled").GetBoolean() && chainOrder.Count == 0,
            "cancelling a queued item while another is running does not start anything early");
        holdGate.SetResult(true);
        var doomedResult = await doomed;
        var followerResult = await follower;
        Check(Error(doomedResult) == "CANCELLED" && Effects(doomedResult) == "none",
            "the cancelled queued item reports CANCELLED with no side effects");
        Check(Status(followerResult) == "succeeded" && chainOrder.SequenceEqual(["running", "follower"]),
            "the item behind the cancelled one still runs after the running item, in acceptance order");

        // D9/M-1: a failure of the final result write must not turn a completed effect into a replayable request.
        string target = Path.Combine(runtime.Config.Roots[0].Path, "persist.txt");
        File.WriteAllText(target, "before\n", new UTF8Encoding(false));
        string hash = Data(tools.FsRead("proj", "persist.txt", null, null, null, null)).GetProperty("sha256").GetString()!;
        runtime.Store.FailNextResultWrite = true;
        var unknown = await tools.FsWrite("proj", "persist.txt", "after\n", "replace", "persist-1", hash);
        Check(Error(unknown) == "EXECUTION_UNKNOWN" && Reason(unknown) == "persist_failed",
            "a result that cannot be stored is reported as unknown with reason persist_failed");
        Check(Effects(unknown) == "applied" && File.ReadAllText(target) == "after\n",
            "the effect itself completed and is reported as applied");
        Check(Data(unknown).GetProperty("result").GetProperty("data").GetProperty("sha256").GetString() is { Length: 64 },
            "the unstored result is attached to the unknown envelope");
        Check(Reason(tools.OperationInspect("persist-1")) == "persist_failed", "operation_inspect keeps reporting persist_failed");
        var replay = await tools.FsWrite("proj", "persist.txt", "after\n", "replace", "persist-1", hash);
        Check(Reason(replay) == "persist_failed" && File.ReadAllText(target) == "after\n",
            "a retry of an unknown operation returns unknown instead of executing the effect again");

        // The same path again with a real SQLite failure instead of the injected flag: the effect runs, then
        // the connection is made read-only so the result write genuinely fails.
        string real = Path.Combine(runtime.Config.Roots[0].Path, "real-persist.txt");
        var unstored = await ledger.Invoke("persist-2", "test_real_persist", new { }, "files:persist", _ =>
        {
            File.WriteAllText(real, "real effect\n", new UTF8Encoding(false));
            runtime.Store.Pragma("PRAGMA query_only=1");
            return Task.FromResult(Reply.Ok(new { effect = "applied" }));
        }, 15000);
        runtime.Store.Pragma("PRAGMA query_only=0");
        Check(Error(unstored) == "EXECUTION_UNKNOWN" && Reason(unstored) == "persist_failed" && Effects(unstored) == "applied",
            "a real read-only database failure after the effect is reported as unknown(persist_failed)");
        Check(File.ReadAllText(real) == "real effect\n" && Reason(tools.OperationInspect("persist-2")) == "persist_failed",
            "the effect of the unstored operation is on disk and operation_inspect keeps saying so");
    }

    private static void Restart(ServerConfig config)
    {
        using (var store = new Store(Path.Combine(config.StateDir, "codexish.db")))
        {
            store.InsertInvocation("restart-queued", "digest", "fs_write");
            store.InsertInvocation("restart-running", "digest", "fs_write");
            store.UpdateInvocationStatus("restart-running", "running");
        }
        using var runtime = new CodexishRuntime(config);
        var tools = new CodexishTools(runtime);
        Check(Status(tools.OperationInspect("w1")) == "succeeded", "a stored result survives a restart");
        var cancelled = tools.OperationInspect("restart-queued");
        Check(Error(cancelled) == "CANCELLED" && Effects(cancelled) == "none",
            "an operation still queued at restart becomes cancelled with no side effects");
        var unknown = tools.OperationInspect("restart-running");
        Check(Error(unknown) == "EXECUTION_UNKNOWN" && Effects(unknown) == "unknown",
            "an operation running at restart becomes unknown, never an assumed success");
        Check(Data(tools.HostCapabilities()).GetProperty("recovery").GetProperty("invocations_cancelled_on_restart").GetInt32() == 1,
            "restart recovery counts are reported in host_capabilities");
    }
}
