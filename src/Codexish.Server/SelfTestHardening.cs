using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static Codexish.Server.SelfTest;

namespace Codexish.Server;

// Availability hardening (pass 2): nothing optional stops the server, damaged state heals or is contained to its own
// unit, and CODEXish's own state is cleaned by age. Every check uses its own temporary state; nothing is written at a
// drive root and no desktop input is sent.
internal static class HardeningTests
{
    public static async Task Run(string temp, string password)
    {
        Tolerance(temp);
        await MissingRoot(temp, password);
        ConfigurationFiles(temp, password);
        DriveRoot(temp, password);
        await Secrets(temp, password);
        ShellResolution(temp);
        GitResolution(temp);
        await LedgerRebuild(temp, password);
        LedgerRows(temp);
        await Orphans(temp, password);
        await Retention(temp, password);
    }

    private static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "placeholder");
        return path;
    }

    private static async Task<bool> Gone(int pid)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (!ProcessTests.Alive(pid)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    // P1: problems that used to refuse startup are skipped or tolerated and reported as warnings.
    private static void Tolerance(string temp)
    {
        string directory = Path.Combine(temp, "tolerance");
        string good = Path.Combine(directory, "good");
        Directory.CreateDirectory(good);
        var config = new ServerConfig
        {
            StateDir = Path.Combine(good, "state"),
            Roots =
            [
                new RootConfig { Id = "good", Path = good },
                new RootConfig { Id = "bad id!", Path = good },
                new RootConfig { Id = "GOOD", Path = directory },
                new RootConfig { Id = "empty", Path = "" },
                new RootConfig { Id = "later", Path = Path.Combine(directory, "later") },
                new RootConfig { Id = "inner", Path = Path.Combine(good, "sub") }
            ],
            Shell = new ShellConfig { Default = "bash", Allowed = ["bash", "PWSH", "cmd"] }
        };
        config.OAuth.AccessTokenHours = 0;
        config.OAuth.RedirectUris = ["https://valid.example/cb", "not a uri", "https://fragment.example/cb#x"];
        config.Validate();
        string warnings = string.Join("\n", config.Warnings);
        Check(config.Roots.Select(r => r.Id).SequenceEqual(["good", "later", "inner"]) &&
              warnings.Contains("'bad id!'") && warnings.Contains("'GOOD' appears more than once") && warnings.Contains("'empty' has no path"),
            "bad, duplicate and empty root entries are skipped with a warning instead of refusing startup");
        Check(warnings.Contains("'later' path does not exist") && warnings.Contains("state_dir") && warnings.Contains("overlaps root 'good'"),
            "a missing root directory and a state_dir inside a root are warnings, not refusals");
        Check(config.Shell.Usable.SequenceEqual(["pwsh", "cmd"]) && config.Shell.EffectiveDefault == "pwsh" &&
              warnings.Contains("bash") && warnings.Contains("shell.default 'bash'"),
            "unsupported shell names are ignored with a warning, and an unusable default falls back to the first allowed shell");
        Check(config.OAuth.AccessTokenHours == 12 && config.OAuth.RedirectUris.SequenceEqual(["https://valid.example/cb"]) &&
              warnings.Contains("'not a uri'"),
            "an invalid token lifetime or redirect entry falls back or is skipped with a warning");
        var empty = new ServerConfig { StateDir = Path.Combine(directory, "empty-state") };
        empty.Validate();
        Check(empty.Roots.Length == 0 && empty.Warnings.Any(w => w.Contains("No usable root", StringComparison.Ordinal)),
            "a configuration with no usable root still validates, with a warning");
        Check(PathRules.IsInside(good, Path.Combine(good, "x")) && !PathRules.IsInside(good, good + "x") &&
              PathRules.IsInside(Path.GetPathRoot(good)!, good),
            "containment appends a separator only when the root does not already end with one");
    }

    // P1: a root whose directory is missing stays configured and works as soon as the directory exists.
    private static async Task MissingRoot(string temp, string password)
    {
        string directory = Path.Combine(temp, "missing-root");
        string later = Path.Combine(directory, "later");
        var config = BuildConfig(directory, password);
        config.Roots = [.. config.Roots, new RootConfig { Id = "later", Path = later }];
        config.Validate();
        using var runtime = new CodexishRuntime(config);
        var tools = new CodexishTools(runtime);
        var capabilities = Data(tools.HostCapabilities());
        var reported = capabilities.GetProperty("roots").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "later");
        Check(Status(tools.HostCapabilities()) == "succeeded" && !reported.GetProperty("exists").GetBoolean() &&
              capabilities.GetProperty("warnings").EnumerateArray().Any(w => w.GetString()!.Contains("'later' path does not exist")),
            "a missing root is listed with exists=false and a warning in host_capabilities");
        Check(!Data(tools.WorkspaceInfo()).GetProperty("roots").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "later")
                .GetProperty("exists").GetBoolean(),
            "workspace_info reports exists=false for the missing root");
        var refused = tools.FsList("later", "", 1, false, null);
        Check(Error(refused) == "NOT_FOUND" && refused.StructuredContent!.Value.GetProperty("error").GetProperty("message").GetString()!.Contains("does not exist right now"),
            "a call on the missing root fails with a clear error");
        Directory.CreateDirectory(later);
        File.WriteAllText(Path.Combine(later, "seen.txt"), "seen\n");
        Check(Status(tools.FsList("later", "", 1, false, null)) == "succeeded" &&
              Status(await tools.FsWrite("later", "written.txt", "text\n", "create", "late-1")) == "succeeded",
            "the same root works as soon as its directory exists, without a restart");
        Check(Data(tools.HostCapabilities()).GetProperty("tools").EnumerateArray().Any(t => t.GetString() == "computer_observe"),
            "the desktop tools stay listed whatever the roots are");
        var none = BuildConfig(Path.Combine(temp, "no-roots"), password);
        none.Roots = [];
        none.Validate();
        using var bare = new CodexishRuntime(none);
        var bareCapabilities = new CodexishTools(bare).HostCapabilities();
        Check(Status(bareCapabilities) == "succeeded" && Data(bareCapabilities).GetProperty("roots").GetArrayLength() == 0 &&
              Data(bareCapabilities).GetProperty("tools").EnumerateArray().Count(t => t.GetString()!.StartsWith("computer_", StringComparison.Ordinal)) == 3,
            "a server without any usable root starts and keeps its desktop tools");
    }

    // P3: saves are atomic and keep the previous version; an unreadable file recovers from it.
    private static void ConfigurationFiles(string temp, string password)
    {
        string directory = Path.Combine(temp, "config-files");
        Directory.CreateDirectory(Path.Combine(directory, "proj"));
        string path = Path.Combine(directory, "codexish.json");
        var config = ServerConfig.Create("https://config.test", password, [("proj", Path.Combine(directory, "proj"))], [], 3000,
            Path.Combine(directory, "state"));
        config.Save(path);
        config.Port = 3001;
        config.Save(path);
        Check(ServerConfig.Load(path).Port == 3001 && ServerConfig.Load(path + ".bak").Port == 3000 &&
              !Directory.EnumerateFiles(directory, "*.tmp-*").Any(),
            "saving replaces the file atomically, keeps the previous version as .bak and leaves no temporary file");
        File.WriteAllText(path, "{ \"port\": ");
        var recovered = ServerConfig.Load(path);
        string[] broken = Directory.GetFiles(directory, "codexish.json.broken-*");
        Check(recovered.Port == 3000 && ServerConfig.Load(path).Port == 3000 && broken.Length == 1 &&
              File.ReadAllText(broken[0]) == "{ \"port\": " && recovered.Warnings.Any(w => w.Contains(".bak", StringComparison.Ordinal)),
            "an unreadable configuration is kept as .broken-<utc>, and the readable .bak is loaded, restored and reported");
        File.Delete(path + ".bak");
        File.WriteAllText(path, "not json at all");
        string? error = null;
        try { ServerConfig.Load(path); }
        catch (InvalidDataException failure) { error = failure.Message; }
        Check(error is not null && error.Contains("could not be read") && Directory.GetFiles(directory, "codexish.json.broken-*").Length == 2,
            "without a readable .bak the real parse error is raised and the unreadable file is still kept");
    }

    // P4: a root set to the drive root resolves its children. Only reads happen; nothing is written at the drive root.
    private static void DriveRoot(string temp, string password)
    {
        string full = Path.GetFullPath(temp);
        string drive = Path.GetPathRoot(full)!;
        string child = Path.GetRelativePath(drive, full);
        var config = BuildConfig(Path.Combine(temp, "drive-root"), password);
        config.Roots = [new RootConfig { Id = "drive", Path = drive, Write = false, Shell = false }];
        config.Validate();
        Check(config.Roots.Length == 1 && config.Warnings.Any(w => w.Contains("overlaps root 'drive'")),
            "a drive root is accepted, with a warning because state_dir lies inside it");
        try
        {
            var workspace = new Workspace(config);
            var target = workspace.Resolve("drive", child, Grant.Read);
            workspace.VerifyDirectory(target);
            Check(string.Equals(target.FullPath, Path.TrimEndingDirectorySeparator(full), PathRules.Compare),
                "a child directory resolves through a root set to the drive root");
            using var runtime = new CodexishRuntime(config);
            var listed = new CodexishTools(runtime).FsList("drive", child, 1, false, null);
            Check(Status(listed) == "succeeded" && Data(listed).GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("path").GetString()!.EndsWith("/drive-root", StringComparison.Ordinal)),
                "fs_list reads a directory through a drive-root root");
        }
        catch (CodexishFault fault) when (fault.Code == "UNSUPPORTED_CAPABILITY")
        {
            Skip("drive root check: the temporary directory's path crosses a junction or symlink");
        }
    }

    // P5: an empty secret never authenticates and a damaged password hash is a failed sign-in, not an exception.
    private static async Task Secrets(string temp, string password)
    {
        Check(!Tokens.SecretMatches("", "") && !Tokens.SecretMatches(null, null) && !Tokens.SecretMatches("", "x") && Tokens.SecretMatches("a", "a"),
            "an empty configured secret matches nothing, not even an empty offer");
        Check(ServerConfig.PasswordHashProblem("x$y") == "wrong_field_count" &&
              ServerConfig.PasswordHashProblem("md5$1$AAAA$AAAA") == "unknown_algorithm" &&
              ServerConfig.PasswordHashProblem("pbkdf2-sha256$zero$AAAA$AAAA") == "bad_iteration_count" &&
              ServerConfig.PasswordHashProblem("pbkdf2-sha256$1$***$AAAA") == "bad_base64" &&
              ServerConfig.PasswordHashProblem(ServerConfig.HashPassword("p")) is null && !ServerConfig.VerifyPassword("p", "garbage"),
            "malformed password hashes are recognized and never verify");

        var config = BuildConfig(Path.Combine(temp, "secrets"), password);
        config.OAuth.ClientSecret = "";
        config.OAuth.PasswordHash = "pbkdf2-sha256$1$***$AAAA";
        List<string> log = [];
        using var runtime = new CodexishRuntime(config);
        await using var app = CodexishHost.Build(runtime, 0, line => { lock (log) log.Add(line); });
        await app.StartAsync();
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()) };
            foreach (string secret in new[] { "", "anything" })
            {
                using var token = await http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["refresh_token"] = "x", ["client_id"] = config.OAuth.ClientId, ["client_secret"] = secret
                }));
                Check(token.StatusCode == HttpStatusCode.Unauthorized,
                    $"with an empty configured client secret, the offered secret '{secret}' does not authenticate");
            }
            string redirect = config.OAuth.RedirectUris[0];
            string form = await http.GetStringAsync($"/authorize?response_type=code&client_id={config.OAuth.ClientId}&redirect_uri={Uri.EscapeDataString(redirect)}&state=s");
            string nonce = System.Text.RegularExpressions.Regex.Match(form, "name=\"nonce\" value=\"([^\"]+)\"").Groups[1].Value;
            using var signIn = await http.PostAsync("/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["response_type"] = "code", ["client_id"] = config.OAuth.ClientId, ["redirect_uri"] = redirect, ["state"] = "s",
                ["nonce"] = nonce, ["password"] = password
            }));
            Check(signIn.StatusCode == HttpStatusCode.Unauthorized && (await signIn.Content.ReadAsStringAsync()).Contains("damaged") &&
                  log.Any(l => l.Contains("reason=malformed_password_hash detail=bad_base64")),
                "a damaged password hash turns sign-in into a logged failure instead of an exception");
        }
        finally { await app.StopAsync(); }
    }

    // P6: pwsh is looked up on PATH, then in Program Files (newest), then Windows PowerShell, on every call.
    private static void ShellResolution(string temp)
    {
        string root = Path.Combine(temp, "shells");
        string pathDirectory = Path.Combine(root, "path");
        string programFiles = Path.Combine(root, "pf");
        string system = Path.Combine(root, "system");
        string onPath = Touch(Path.Combine(pathDirectory, Executable("pwsh")));
        Touch(Path.Combine(programFiles, "PowerShell", "6", Executable("pwsh")));
        string newest = Touch(Path.Combine(programFiles, "PowerShell", "7", Executable("pwsh")));
        string classic = Touch(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
        string pathVariable = string.Join(Path.PathSeparator, Path.Combine(root, "nothing-here"), pathDirectory);
        Check(ProcessSupervisor.ResolveShell("pwsh", pathVariable, programFiles, system) == onPath &&
              ProcessSupervisor.ResolveShell("pwsh", "", programFiles, system) == newest &&
              ProcessSupervisor.ResolveShell("pwsh", "", null, system) == classic &&
              ProcessSupervisor.ResolveShell("powershell", pathVariable, programFiles, system) == classic,
            "pwsh resolves from PATH, then the newest Program Files install, then Windows PowerShell");
    }

    // P12: git is looked up on every call: git.path when it exists, PATH, Program Files, then the per-user install.
    private static void GitResolution(string temp)
    {
        string root = Path.Combine(temp, "git-locate");
        string configured = Touch(Path.Combine(root, "custom", Executable("git")));
        string onPath = Touch(Path.Combine(root, "path", Executable("git")));
        string machine = Touch(Path.Combine(root, "pf", "Git", "cmd", Executable("git")));
        string user = Touch(Path.Combine(root, "local", "Programs", "Git", "cmd", Executable("git")));
        string missing = Path.Combine(root, "moved", Executable("git"));
        string pathVariable = Path.Combine(root, "path");
        Check(GitService.Locate(configured, pathVariable, Path.Combine(root, "pf"), Path.Combine(root, "local")) == configured &&
              GitService.Locate(missing, pathVariable, Path.Combine(root, "pf"), Path.Combine(root, "local")) == onPath &&
              GitService.Locate(missing, "", Path.Combine(root, "pf"), Path.Combine(root, "local")) == machine &&
              GitService.Locate(missing, "", null, Path.Combine(root, "local")) == user &&
              GitService.Locate(missing, "", null, null) is null,
            "git resolves from git.path, then PATH, then Program Files, then the per-user Git install");
    }

    // P7: a ledger that SQLite cannot read is moved aside and replaced, and the server starts.
    private static async Task LedgerRebuild(string temp, string password)
    {
        var config = BuildConfig(Path.Combine(temp, "rebuild"), password);
        Directory.CreateDirectory(config.StateDir);
        byte[] junk = Encoding.ASCII.GetBytes(new string('x', 8192));
        File.WriteAllBytes(Path.Combine(config.StateDir, "codexish.db"), junk);
        using (var runtime = new CodexishRuntime(config))
        {
            var tools = new CodexishTools(runtime);
            var capabilities = Data(tools.HostCapabilities());
            var rebuilt = capabilities.GetProperty("ledger_rebuilt");
            string quarantined = rebuilt.GetProperty("quarantined").EnumerateArray().First().GetString()!;
            Check(quarantined.StartsWith("ledger.corrupt-", StringComparison.Ordinal) &&
                  File.ReadAllBytes(Path.Combine(config.StateDir, quarantined)).SequenceEqual(junk) &&
                  capabilities.GetProperty("warnings").EnumerateArray().Any(w => w.GetString()!.Contains("sign in again")),
                "a file that is not a database is moved aside as ledger.corrupt-<utc>, reported, and the warning says to sign in again");
            Check(Status(await tools.FsWrite("proj", "after-rebuild.txt", "fresh\n", "create", "rebuilt-1")) == "succeeded" &&
                  runtime.Store.Events("ledger_rebuilt").Count == 1,
                "the fresh ledger records work and the rebuild event");
        }

        // A real database with a damaged page: quick_check finds it at startup.
        var damaged = BuildConfig(Path.Combine(temp, "damaged"), password);
        Directory.CreateDirectory(damaged.StateDir);
        string file = Path.Combine(damaged.StateDir, "codexish.db");
        using (var store = new Store(file))
            for (int i = 0; i < 300; i++) store.Event("filler", null, new { text = new string('f', 1000) });
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 4096L * 20;
            stream.Write(Enumerable.Repeat((byte)0xA5, 4096).ToArray());
        }
        using (var runtime = new CodexishRuntime(damaged))
            Check(runtime.Store.Rebuilt is { Quarantined.Length: > 0 } && Status(new CodexishTools(runtime).HostCapabilities()) == "succeeded",
                "a database with a damaged page is found by quick_check at startup and rebuilt");
    }

    // P8: migrations, transient errors and unreadable rows are each contained.
    private static void LedgerRows(string temp)
    {
        string path = Path.Combine(temp, "migration.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE VIEW tokens AS SELECT 'h' AS hash";
            command.ExecuteNonQuery();
        }
        using (var store = new Store(path))
        {
            store.AddCheckpoint("{\"n\":1}");
            Check(store.MigrationErrors.Count > 0 && store.MigrationErrors.All(e => e.Contains("tokens.", StringComparison.Ordinal)) &&
                  store.LatestCheckpoint() is not null,
                "a migration that fails for a reason other than a duplicate column is logged and startup continues");
        }
        using (var again = new Store(Path.Combine(temp, "fresh.db"))) { }
        using (var reopened = new Store(Path.Combine(temp, "fresh.db")))
            Check(reopened.MigrationErrors.Count == 0, "reopening a current database ignores the duplicate-column answers of its migrations");

        int calls = 0;
        List<TimeSpan> slept = [];
        int value = Store.WithRetry(() => ++calls switch
        {
            1 => throw new SqliteException("busy", 5),
            2 => throw new SqliteException("locked", 6),
            3 => throw new SqliteException("disk I/O error", 10),
            _ => 42
        }, TimeSpan.FromSeconds(30), slept.Add);
        Check(value == 42 && calls == 4 && slept.Count == 3 && slept[2] > slept[0],
            "SQLITE_BUSY, SQLITE_LOCKED and SQLITE_IOERR are retried with a growing delay");
        int permanent = 0, spent = 0;
        try { Store.WithRetry<int>(() => { permanent++; throw new SqliteException("readonly", 8); }, TimeSpan.FromSeconds(30), _ => { }); }
        catch (SqliteException) { }
        try { Store.WithRetry<int>(() => { spent++; throw new SqliteException("busy", 5); }, TimeSpan.Zero, _ => { }); }
        catch (SqliteException) { }
        Check(permanent == 1 && spent == 1, "an error that is not transient, or a transient one past the command timeout, surfaces at once");
    }

    // P9 and P10: a child is never orphaned by a failure after it started, and exited processes release their handles.
    private static async Task Orphans(string temp, string password)
    {
        if (Shells.Preferred is not { } shell)
        {
            Skip("orphan and handle checks: no interpreter is available");
            return;
        }
        var config = BuildConfig(Path.Combine(temp, "orphans"), password);
        using var runtime = new CodexishRuntime(config);
        var tools = new CodexishTools(runtime);
        var quick = await tools.ShellRun("proj", "release-1", executable: Shells.Executable(shell), args: Shells.DirectArguments(shell, "released"), wait_ms: 60000);
        var managed = runtime.Processes.Lookup(Data(quick).GetProperty("process_id").GetString()!);
        bool closed;
        try { _ = managed.Process.Handle; closed = false; }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { closed = true; }
        Check(Status(quick) == "succeeded" && managed.Released && closed && managed.JobHandle == 0 &&
              Status(await tools.ProcessPoll(managed.ProcessId, null, 0)) == "succeeded",
            "an exited process releases its process and job handles and stays pollable from its metadata");

        // Artifact creation fails right after the child started: the child must not keep running.
        string artifacts = runtime.Artifacts.Directory;
        Directory.Delete(artifacts, true);
        File.WriteAllText(artifacts, "not a directory");
        try
        {
            var failed = await tools.ShellRun("proj", "orphan-1", command: Shells.Sleep(shell, 30), shell: shell, wait_ms: 10000);
            var events = runtime.Store.Events("process_start_failed");
            int pid = events.Count == 1 ? JsonDocument.Parse(events[0].Json!).RootElement.GetProperty("pid").GetInt32() : 0;
            Check(Error(failed) == "EXECUTION_FAILED" && pid > 0 && await Gone(pid),
                "a child whose start cannot be recorded is terminated with its tree before the error is returned");
        }
        finally
        {
            File.Delete(artifacts);
            Directory.CreateDirectory(artifacts);
        }
    }

    // P17: age-based cleanup of CODEXish's own state, with everything that must survive checked as well.
    private static async Task Retention(string temp, string password)
    {
        string directory = Path.Combine(temp, "retention");
        var config = BuildConfig(directory, password);
        config.SourcePath = Path.Combine(directory, "codexish.json");
        using var runtime = new CodexishRuntime(config);
        var tools = new CodexishTools(runtime);
        var store = runtime.Store;
        var now = DateTimeOffset.UtcNow;
        string old = now.AddDays(-40).ToString("o"), recent = now.AddDays(-1).ToString("o");
        void Sql(string sql, params (string, object?)[] args) => store.Execute(sql, args);
        string Invocation(string id, string status, string when) => $"INSERT INTO invocations(id,digest,tool,status,result,created_at,updated_at) VALUES('{id}','d','t','{status}',{(status == "queued" ? "NULL" : "'{}'")},'{when}','{when}')";
        Sql(Invocation("old-done", "succeeded", old));
        Sql(Invocation("old-queued", "queued", old));
        Sql(Invocation("new-done", "succeeded", recent));
        Sql("INSERT INTO events(utc,kind,ref,json) VALUES($o,'old-event',NULL,'{}')", ("$o", old));
        Sql("INSERT INTO checkpoints(utc,json) VALUES($o,'{\"n\":1}')", ("$o", old));
        Sql("INSERT INTO checkpoints(utc,json) VALUES($o,'{\"n\":2}')", ("$o", old));
        void Token(string hash, string kind, string expires, int revoked, string? revokedAt) =>
            Sql("INSERT INTO tokens(hash,kind,client_id,audience,expires_at,revoked,pkce_used,family,scope,revoked_at) VALUES($h,$k,'c','a',$e,$r,1,'','mcp',$ra)",
                ("$h", hash), ("$k", kind), ("$e", expires), ("$r", revoked), ("$ra", revokedAt));
        Token("expired-access", "access", old, 0, null);
        Token("revoked-old", "access", now.AddDays(1).ToString("o"), 1, old);
        Token("revoked-undated", "refresh", DateTimeOffset.MaxValue.ToString("o"), 1, null);
        Token("live-access", "access", now.AddHours(1).ToString("o"), 0, null);
        Token("kept-refresh", "refresh", old, 0, null);
        string Artifact(string id, string when, bool file = true)
        {
            string path = Path.Combine(runtime.Artifacts.Directory, id + ".bin");
            if (file) File.WriteAllText(path, "output of " + id);
            Sql("INSERT INTO artifacts(id,mime,bytes,sha256,path,complete,generation,created_at) VALUES($id,'text/plain',1,NULL,$p,1,1,$n)",
                ("$id", id), ("$p", path), ("$n", when));
            return path;
        }
        void Process(string id, string state, string when, string output) =>
            Sql("INSERT INTO processes(process_id,pid,start_time,state,exit_code,lifetime,root_id,stdout_artifact,stderr_artifact,updated_at) VALUES($id,1,1,$s,0,'persistent','proj',$a,NULL,$n)",
                ("$id", id), ("$s", state), ("$a", output), ("$n", when));
        string exitedOutput = Artifact("art_exited_old", old);
        Process("proc_exited_old", "exited", old, "art_exited_old");
        string runningOutput = Artifact("art_running_old", old);
        Process("proc_running_old", "running", old, "art_running_old");
        string recentOutput = Artifact("art_exited_recent", old);
        Process("proc_exited_recent", "exited", recent, "art_exited_recent");
        string loose = Artifact("art_loose_old", old);
        string fresh = Artifact("art_loose_recent", recent);
        string orphan = Path.Combine(runtime.Artifacts.Directory, "art_orphan.bin");
        File.WriteAllText(orphan, "no row");
        File.SetLastWriteTimeUtc(orphan, now.AddDays(-40).UtcDateTime);

        string Aged(string path, int days)
        {
            Touch(path);
            File.SetLastWriteTimeUtc(path, now.AddDays(-days).UtcDateTime);
            return path;
        }
        string oldBackup = Aged(Path.Combine(config.StateDir, "backups", "old.bak"), 100);
        string keptBackup = Aged(Path.Combine(config.StateDir, "backups", "kept.bak"), 40);
        string oldLog = Aged(Path.Combine(config.StateDir, "logs", "tray-old.log"), 40);
        string todayLog = Aged(Path.Combine(config.StateDir, "logs", "tray-today.log"), 0);
        string profile = Aged(Path.Combine(config.StateDir, "browser-profiles", "pw", "Preferences"), 400);
        string quarantine = Touch(Path.Combine(config.StateDir, "ledger.corrupt-" + ServerConfig.UtcStamp(now.AddDays(-100)) + ".db"));
        string brokenOld = Touch(config.SourcePath + ".broken-" + ServerConfig.UtcStamp(now.AddDays(-100)));
        string brokenNew = Touch(config.SourcePath + ".broken-" + ServerConfig.UtcStamp(now.AddDays(-10)));

        var report = runtime.Retention.Sweep(now);
        bool Row(string sql) => store.ExecuteCount(sql) > 0;
        Check(report.ErrorCount == 0 && store.Invocation("old-done") is null && store.Invocation("old-queued") is not null && store.Invocation("new-done") is not null,
            "finished invocations past output_days are removed, while queued work and recent results stay");
        Check(!store.Events("old-event").Any() && store.LatestCheckpoint() is { Json: "{\"n\":2}" } &&
              !Row("UPDATE checkpoints SET json=json WHERE json='{\"n\":1}'"),
            "old events and all but the newest checkpoint are removed");
        Check(store.Token("expired-access") is null && store.Token("revoked-old") is null && store.Token("revoked-undated") is not null &&
              store.Token("live-access") is not null && store.Token("kept-refresh") is not null,
            "expired and revoked tokens past output_days are removed; live tokens and a kept refresh token stay");
        Check(store.Process("proc_exited_old") is null && !File.Exists(exitedOutput) && store.Process("proc_running_old") is not null &&
              File.Exists(runningOutput) && store.Process("proc_exited_recent") is not null && File.Exists(recentOutput),
            "an exited process past output_days goes with its output, while a running process and a recent exit keep theirs");
        Check(!File.Exists(loose) && store.Artifact("art_loose_old") is null && File.Exists(fresh) && !File.Exists(orphan),
            "old unreferenced artifacts and orphaned artifact files are removed, recent ones stay");
        Check(!File.Exists(oldBackup) && File.Exists(keptBackup) && !File.Exists(oldLog) && File.Exists(todayLog) && File.Exists(profile) &&
              !File.Exists(quarantine) && !File.Exists(brokenOld) && File.Exists(brokenNew),
            "backups, quarantined ledgers and unreadable configuration copies go after backup_days, tray logs after output_days, and browser profiles stay");
        var described = Data(tools.HostCapabilities()).GetProperty("retention");
        Check(described.GetProperty("output_days").GetInt32() == 30 && described.GetProperty("backup_days").GetInt32() == 90 &&
              described.GetProperty("last_sweep").GetProperty("files").GetInt32() == report.Files && report.Files >= 7 && report.Bytes > 0,
            "host_capabilities reports the retention settings and the last sweep");

        config.Retention.OutputDays = 0;
        Sql("INSERT INTO events(utc,kind,ref,json) VALUES($o,'kept-forever',NULL,'{}')", ("$o", old));
        runtime.Retention.Sweep(now);
        Check(store.Events("kept-forever").Count == 1, "output_days 0 keeps that state forever");
        await Task.CompletedTask;
    }
}
