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
        ConcurrentConfiguration(temp, password);
        await MalformedEntries(temp, password);
        DriveRoot(temp, password);
        await Secrets(temp, password);
        ShellResolution(temp);
        GitResolution(temp);
        await LedgerRebuild(temp, password);
        LedgerRows(temp);
        await MigrationUnderLock(temp);
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

    // C1 and C2: recovery never overwrites a file another writer saved in between, and an unreadable copy never
    // overwrites an earlier one.
    private static void ConcurrentConfiguration(string temp, string password)
    {
        string directory = Path.Combine(temp, "config-race");
        Directory.CreateDirectory(Path.Combine(directory, "proj"));
        string path = Path.Combine(directory, "codexish.json");
        var config = ServerConfig.Create("https://race.test", password, [("proj", Path.Combine(directory, "proj"))], [], 3000,
            Path.Combine(directory, "state"));
        config.Save(path);
        config.Port = 3001;
        config.Save(path);
        byte[] failed = Encoding.UTF8.GetBytes("{ \"port\": ");
        var parseError = new JsonException("simulated parse failure");
        // Another writer saved a readable version after the unreadable bytes were read.
        config.Port = 3002;
        config.Save(path);
        var (newer, loadedNote) = ServerConfig.Recover(path, failed, parseError);
        Check(newer.Port == 3002 && ServerConfig.Load(path).Port == 3002 && loadedNote.Contains("another writer", StringComparison.Ordinal),
            "when another writer saved a readable file in between, that file is loaded and the backup is not restored over it");
        // Another writer left a different unreadable file: the backup is used and nothing is overwritten.
        byte[] other = Encoding.UTF8.GetBytes("{ \"port\": 99");
        File.WriteAllBytes(path, other);
        var (fallback, keptNote) = ServerConfig.Recover(path, failed, parseError);
        Check(fallback.Port == 3001 && File.ReadAllBytes(path).SequenceEqual(other) && keptNote.Contains("without overwriting", StringComparison.Ordinal),
            "when the file changed to something unreadable again, the backup is loaded without overwriting that newer file");

        var at = DateTimeOffset.UtcNow;
        string first = ServerConfig.KeepBroken(path, [1], at), second = ServerConfig.KeepBroken(path, [2], at), third = ServerConfig.KeepBroken(path, [3], at);
        var recognized = global::Codexish.Server.Retention.UnreadableConfiguration("codexish.json");
        Check(first == path + ".broken-" + ServerConfig.UtcStamp(at) && second == first + "-2" && third == first + "-3" &&
              File.ReadAllBytes(first).SequenceEqual(new byte[] { 1 }) && File.ReadAllBytes(second).SequenceEqual(new byte[] { 2 }) &&
              File.ReadAllBytes(third).SequenceEqual(new byte[] { 3 }) && new[] { first, second, third }.All(f => recognized.IsMatch(Path.GetFileName(f))),
            "an unreadable copy never overwrites an earlier one: a taken name gets -2, -3, and retention still recognizes each");
    }

    // C3 and C4: malformed access entries and JSON nulls anywhere become warnings or defaults, never a failed start.
    private static async Task MalformedEntries(string temp, string password)
    {
        string directory = Path.Combine(temp, "malformed");
        string root = Path.Combine(directory, "proj");
        Directory.CreateDirectory(root);
        static string Json(string value) => JsonSerializer.Serialize(value);
        string entriesPath = Path.Combine(directory, "entries.json");
        File.WriteAllText(entriesPath, $$"""
            {
              "public_url": "https://entries.test",
              "state_dir": {{Json(Path.Combine(directory, "entries-state"))}},
              "roots": [{ "id": "proj", "path": {{Json(root)}} }],
              "allow_hosts": ["good.example", "bad host", null, "*.wild.example", "host:8080", "http://scheme.example"],
              "allow_origins": ["https://ok.example", "not an origin", null, "https://path.example/x"],
              "oauth": { "client_secret": "secret", "password_hash": {{Json(ServerConfig.HashPassword(password))}} },
              "control_token": "control"
            }
            """);
        var entries = ServerConfig.Load(entriesPath);
        bool Warned(string field, string entry) => entries.Warnings.Any(w => w.StartsWith($"{field} entry '{entry}'", StringComparison.Ordinal));
        Check(entries.AllowHosts.SequenceEqual(["good.example"]) && entries.AllowOrigins.SequenceEqual(["https://ok.example", "https://entries.test"]) &&
              new[] { "bad host", "null", "*.wild.example", "host:8080", "http://scheme.example" }.All(h => Warned("allow_hosts", h)) &&
              new[] { "not an origin", "null", "https://path.example/x" }.All(o => Warned("allow_origins", o)),
            "malformed allow_hosts and allow_origins entries are skipped with a warning each, and the valid entries are kept");
        Check(new AccessPolicy(["bad host", null, "good.example", "::::", ""], ["nope", null, "https://ok.example", "ftp://x.example"]).AllowsPublicHost &&
              !new AccessPolicy(["bad host", null], [null, "nope"]).AllowsPublicHost,
            "the access policy skips entries it cannot use instead of throwing, and keeps the valid ones");
        using (var runtime = new CodexishRuntime(entries))
        {
            // Entries that reach the host without passing validation still cannot stop it.
            entries.AllowHosts = ["bad host", "good.example"];
            entries.AllowOrigins = ["nope", "https://ok.example"];
            await using var app = CodexishHost.Build(runtime, 0, _ => { });
            Check(app.Services is not null, "a host whose configuration still carries malformed access entries builds");
        }

        // C4: JSON null for every string, list and section, first at the top level. state_dir is null here, so this
        // configuration is only loaded, never run.
        string nullsPath = Path.Combine(directory, "nulls.json");
        File.WriteAllText(nullsPath, """
            { "public_url": null, "allow_hosts": null, "allow_origins": null, "state_dir": null, "roots": null, "shell": null,
              "git": null, "oauth": null, "control_token": null, "browser_mounts": null, "tunnel": null, "retention": null }
            """);
        var empty = ServerConfig.Load(nullsPath);
        Check(empty.PublicUrl == "" && empty.AllowHosts.Length == 0 && empty.AllowOrigins.Length == 0 &&
              empty.StateDir == Path.TrimEndingDirectorySeparator(Path.GetFullPath(ServerConfig.DefaultDirectory)) && empty.Roots.Length == 0 &&
              empty.Shell.EffectiveDefault == "pwsh" && empty.Git.Path == "" && empty.OAuth.ClientId == new OAuthConfig().ClientId &&
              empty.OAuth.RedirectUris.Length == 0 && empty.ControlToken == "" && empty.BrowserMounts.Length == 0 &&
              empty.Tunnel.Command == "" && empty.Tunnel.Args.Length == 0 && empty.Retention.OutputDays == 30 &&
              empty.Warnings.Any(w => w.Contains("state_dir is not set", StringComparison.Ordinal)),
            "JSON null for every top-level string, list and section loads as that property's default");

        // Then inside sections, root entries and browser mount entries, with a configuration the server really runs.
        string nestedPath = Path.Combine(directory, "nested.json");
        File.WriteAllText(nestedPath, $$"""
            {
              "public_url": "https://nested.test", "state_dir": {{Json(Path.Combine(directory, "nested-state"))}},
              "roots": [null, { "id": null, "path": null }, { "id": "proj", "path": {{Json(root)}} }],
              "shell": { "default": null, "allowed": null }, "git": { "path": null },
              "oauth": { "client_id": null, "client_secret": null, "redirect_uris": null, "password_hash": null },
              "tunnel": { "command": null, "args": [null, "--flag"] }, "retention": {},
              "browser_mounts": [null, { "id": null, "root_id": null, "kind": null, "command": null, "args": null, "profile_mode": null, "read_only_tools": null }],
              "allow_hosts": [null], "allow_origins": [null], "control_token": null
            }
            """);
        var nested = ServerConfig.Load(nestedPath);
        using (var runtime = new CodexishRuntime(nested))
        {
            var mounts = JsonSerializer.SerializeToElement(runtime.Browsers.Describe()).GetProperty("mounted");
            Check(nested.Roots.Select(r => r.Id).SequenceEqual(["proj"]) && nested.Shell.EffectiveDefault == "pwsh" && nested.Git.Path == "" &&
                  nested.OAuth.ClientId == new OAuthConfig().ClientId && nested.OAuth.RedirectUris.Length == 0 && nested.Tunnel.Args.SequenceEqual(["--flag"]) &&
                  mounts.GetArrayLength() == 2 && mounts.EnumerateArray().All(m => m.GetProperty("state").GetString() == "invalid_config") &&
                  Status(new CodexishTools(runtime).HostCapabilities()) == "succeeded",
                "JSON null inside sections, root entries and browser mount entries loads as defaults, and the server runs with them");
        }
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

    // S2: migrations that meet another connection's write lock wait it out instead of leaving a column missing.
    private static async Task MigrationUnderLock(string temp)
    {
        string path = Path.Combine(temp, "migration-lock.db");
        string connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using (var setup = new SqliteConnection(connectionString))
        {
            setup.Open();
            using var command = setup.CreateCommand();
            // The two tables as an earlier build wrote them, before the migrated columns existed.
            command.CommandText = """
                CREATE TABLE tokens(hash TEXT PRIMARY KEY, kind TEXT NOT NULL, client_id TEXT NOT NULL, audience TEXT NOT NULL,
                    expires_at TEXT NOT NULL, revoked INTEGER NOT NULL, pkce_used INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE processes(process_id TEXT PRIMARY KEY, pid INTEGER NOT NULL, start_time INTEGER NOT NULL,
                    state TEXT NOT NULL, exit_code INTEGER, lifetime TEXT NOT NULL, root_id TEXT NOT NULL,
                    stdout_artifact TEXT, stderr_artifact TEXT);
                """;
            command.ExecuteNonQuery();
        }
        using var holder = new SqliteConnection(connectionString);
        holder.Open();
        void Holder(string sql)
        {
            using var command = holder.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        Holder("BEGIN EXCLUSIVE");
        var opening = Task.Run(() => new Store(path));
        bool waited;
        try
        {
            await Task.Delay(500);
            waited = !opening.IsCompleted;
        }
        finally { Holder("COMMIT"); }
        using var store = await opening;
        bool Columns(string sql)
        {
            try { store.Execute(sql); return true; }
            catch (SqliteException) { return false; }
        }
        Check(waited && store.MigrationErrors.Count == 0 &&
              Columns("UPDATE tokens SET family=family, scope=scope, revoked_at=revoked_at WHERE 0") &&
              Columns("UPDATE processes SET updated_at=updated_at WHERE 0"),
            "migrations that meet another connection's write lock wait for it, and every migrated column exists afterwards");
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

    // P17 and R1-R4: age-based cleanup of CODEXish's own state, and only of files it named itself, with everything that
    // must survive checked as well.
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
        // Client registrations (pass 4): only an old one that never signed in and holds no live token goes.
        string Registration(string created, string? signedIn)
        {
            string id = ClientRegistry.Prefix + Guid.NewGuid().ToString("N");
            Sql("INSERT INTO clients(client_id,secret_hash,redirect_uris,auth_method,name,created_at,last_signed_in_at) VALUES($id,NULL,'[\"https://ok.example/cb\"]','none',NULL,$c,$s)",
                ("$id", id), ("$c", created), ("$s", signedIn));
            return id;
        }
        string unusedOld = Registration(old, null), signedInOld = Registration(old, old), unusedRecent = Registration(recent, null);
        string holdsToken = Registration(old, null);
        Sql("INSERT INTO tokens(hash,kind,client_id,audience,expires_at,revoked,pkce_used,family,scope,revoked_at) VALUES('client-live',$k,$c,'a',$e,0,1,'','mcp',NULL)",
            ("$k", "refresh"), ("$c", holdsToken), ("$e", DateTimeOffset.MaxValue.ToString("o")));
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
        static string NewId() => "art_" + Guid.NewGuid().ToString("N");
        string exitedId = NewId(), runningId = NewId(), recentId = NewId(), looseId = NewId(), freshId = NewId(), absentId = NewId();
        string exitedOutput = Artifact(exitedId, old);
        Process("proc_exited_old", "exited", old, exitedId);
        string runningOutput = Artifact(runningId, old);
        Process("proc_running_old", "running", old, runningId);
        string recentOutput = Artifact(recentId, old);
        Process("proc_exited_recent", "exited", recent, recentId);
        string loose = Artifact(looseId, old);
        string fresh = Artifact(freshId, recent);
        Artifact(absentId, old, file: false);
        string orphan = Path.Combine(runtime.Artifacts.Directory, NewId() + ".bin");
        File.WriteAllText(orphan, "no row");
        File.SetLastWriteTimeUtc(orphan, now.AddDays(-40).UtcDateTime);

        string Aged(string path, int days)
        {
            Touch(path);
            File.SetLastWriteTimeUtc(path, now.AddDays(-days).UtcDateTime);
            return path;
        }
        string state = config.StateDir;
        string Day(int daysAgo) => now.AddDays(-daysAgo).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        string oldBackup = Aged(Path.Combine(state, "backups", Guid.NewGuid().ToString("N") + ".bak"), 100);
        string keptBackup = Aged(Path.Combine(state, "backups", Guid.NewGuid().ToString("N") + ".bak"), 40);
        string oldLog = Aged(Path.Combine(state, "logs", $"tray-{Day(40)}.log"), 40);
        string todayLog = Aged(Path.Combine(state, "logs", $"tray-{Day(0)}.log"), 0);
        string profile = Aged(Path.Combine(state, "browser-profiles", "pw", "Preferences"), 400);
        string quarantine = Touch(Path.Combine(state, "ledger.corrupt-" + ServerConfig.UtcStamp(now.AddDays(-100)) + ".db"));
        string brokenOld = Touch(config.SourcePath + ".broken-" + ServerConfig.UtcStamp(now.AddDays(-100)));
        string brokenNew = Touch(config.SourcePath + ".broken-" + ServerConfig.UtcStamp(now.AddDays(-10)));

        // R1: state_dir may lie inside a root, so files CODEXish did not name must survive however old they are. Each
        // folder the sweep reads gets the same user files, plus names that only resemble CODEXish's own.
        string configDirectory = Path.GetDirectoryName(config.SourcePath)!;
        string ancient = ServerConfig.UtcStamp(now.AddDays(-400));
        string hex = Guid.NewGuid().ToString("N");
        string[] folders = [runtime.Artifacts.Directory, Path.Combine(state, "backups"), Path.Combine(state, "logs"), state, configDirectory];
        List<string> foreign = folders.SelectMany(folder => new[] { "notes.txt", "report.log", "a.bak", "x.bin" }
            .Select(name => Aged(Path.Combine(folder, name), 400))).ToList();
        foreach (var (folder, name) in new[]
        {
            (runtime.Artifacts.Directory, "art_" + hex + ".bin.txt"), (runtime.Artifacts.Directory, "ART_" + hex.ToUpperInvariant() + ".bin"),
            (runtime.Artifacts.Directory, "art_notes.bin"), (Path.Combine(state, "backups"), "notes-" + hex + ".bak"),
            (Path.Combine(state, "logs"), "tray-2020.log"), (Path.Combine(state, "logs"), "tray-20200101.log.txt"),
            (state, "ledger.corrupt-notes.db"), (state, "ledger.corrupt-" + ancient + ".db.txt"),
            (configDirectory, "codexish.json.broken-notes"), (configDirectory, "other.json.broken-" + ancient)
        })
            foreign.Add(Aged(Path.Combine(folder, name), 400));
        string lookalike = Path.Combine(state, "logs", $"tray-{Day(400)}.log");
        Directory.CreateDirectory(lookalike);
        Directory.SetLastWriteTimeUtc(lookalike, now.AddDays(-400).UtcDateTime);
        // A row whose id CODEXish never issues, pointing at the user's x.bin.
        Artifact("x", old, file: false);

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
        Check(store.Client(unusedOld) is null && store.Client(signedInOld) is not null && store.Client(unusedRecent) is not null &&
              store.Client(holdsToken) is not null,
            "a client registration that never signed in goes after output_days; signed-in, recent and token-holding registrations stay");
        Check(store.Process("proc_exited_old") is null && !File.Exists(exitedOutput) && store.Process("proc_running_old") is not null &&
              File.Exists(runningOutput) && store.Process("proc_exited_recent") is not null && File.Exists(recentOutput),
            "an exited process past output_days goes with its output, while a running process and a recent exit keep theirs");
        Check(!File.Exists(loose) && store.Artifact(looseId) is null && File.Exists(fresh) && store.Artifact(freshId) is not null && !File.Exists(orphan),
            "old unreferenced artifacts and orphaned artifact files are removed, recent ones stay");
        Check(store.Artifact(absentId) is null, "an old artifact row whose file is already gone is removed");
        Check(!File.Exists(oldBackup) && File.Exists(keptBackup) && !File.Exists(oldLog) && File.Exists(todayLog) && File.Exists(profile) &&
              !File.Exists(quarantine) && !File.Exists(brokenOld) && File.Exists(brokenNew),
            "backups, quarantined ledgers and unreadable configuration copies go after backup_days, tray logs after output_days, and browser profiles stay");
        Check(foreign.All(File.Exists) && Directory.Exists(lookalike) && store.Artifact("x") is not null,
            "files CODEXish did not name survive in every folder the sweep reads, as do a directory named like a tray log and a row whose id CODEXish never issues");
        var described = Data(tools.HostCapabilities()).GetProperty("retention");
        Check(described.GetProperty("output_days").GetInt32() == 30 && described.GetProperty("backup_days").GetInt32() == 90 &&
              described.GetProperty("last_sweep").GetProperty("files").GetInt32() == report.Files && report.Files == 7 && report.Bytes > 0,
            "host_capabilities reports the retention settings and the last sweep, which removed exactly the seven expired CODEXish files");

        // R2: a file that cannot be removed keeps its row, so the next sweep tries again. Only Windows refuses to delete
        // a file that is open.
        if (OperatingSystem.IsWindows())
        {
            string lockedId = NewId();
            string locked = Artifact(lockedId, old);
            SweepReport held;
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                held = runtime.Retention.Sweep(now);
            Check(held.ErrorCount == 1 && held.Errors[0].Contains(lockedId, StringComparison.Ordinal) && File.Exists(locked) && store.Artifact(lockedId) is not null,
                "an artifact whose file cannot be removed keeps its row, and the failure is reported");
            var retried = runtime.Retention.Sweep(now);
            Check(retried.ErrorCount == 0 && !File.Exists(locked) && store.Artifact(lockedId) is null,
                "the next sweep removes that file and then its row");
        }
        else Skip("retention with a locked artifact file: only Windows refuses to delete an open file");

        // R3: an exited process whose job still holds a live process keeps its row and its output until that process ends.
        if (OperatingSystem.IsWindows())
        {
            var quick = await tools.ShellRun("proj", "retention-held-1", executable: Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                args: ["/c", "exit", "0"], wait_ms: 60000);
            string processId = Data(quick).GetProperty("process_id").GetString()!;
            var managed = runtime.Processes.Lookup(processId);
            for (int attempt = 0; attempt < 200 && !managed.Released; attempt++) await Task.Delay(50);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            };
            foreach (string argument in new[] { "-n", "120", "127.0.0.1" }) start.ArgumentList.Add(argument);
            using var descendant = System.Diagnostics.Process.Start(start)!;
            nint job = Native.CreateKillOnCloseJob();
            bool assigned = Native.AssignProcess(job, descendant.Handle);
            managed.CloseJob();
            lock (managed.JobSync) managed.JobHandle = job;
            Sql("UPDATE processes SET updated_at=$o WHERE process_id=$id", ("$o", old), ("$id", processId));
            Sql("UPDATE artifacts SET created_at=$o WHERE id=$a OR id=$b", ("$o", old), ("$a", managed.StdoutArtifact), ("$b", managed.StderrArtifact));
            string heldOutput = Path.Combine(runtime.Artifacts.Directory, managed.StdoutArtifact + ".bin");
            runtime.Retention.Sweep(now);
            Check(assigned && managed.Released && store.Process(processId) is not null && File.Exists(heldOutput) &&
                  store.Artifact(managed.StdoutArtifact) is not null && runtime.Processes.HoldsJob(processId),
                "an exited process whose job still holds a live descendant keeps its row and its output");
            descendant.Kill();
            descendant.WaitForExit();
            for (int attempt = 0; attempt < 200 && Native.ActiveProcesses(job) != 0; attempt++) await Task.Delay(50);
            runtime.Retention.Sweep(now);
            Check(store.Process(processId) is null && !File.Exists(heldOutput) && store.Artifact(managed.StdoutArtifact) is null &&
                  !runtime.Processes.HoldsJob(processId),
                "once that descendant has ended, the next sweep closes the job and removes the row and its output");
        }
        else Skip("retention of a process whose job holds descendants: job objects exist only on Windows");

        // R4: bytes that vanished answer ARTIFACT_EXPIRED; an artifact whose row went as well answers NOT_FOUND.
        var saved = runtime.Artifacts.Save(Encoding.UTF8.GetBytes("line one\nline two\n"), "text/plain", "test", null);
        File.Delete(saved.Path);
        var expired = tools.ArtifactRead(saved.Id);
        string Message(ModelContextProtocol.Protocol.CallToolResult result) =>
            result.StructuredContent!.Value.GetProperty("error").GetProperty("message").GetString() ?? "";
        Check(Error(expired) == "ARTIFACT_EXPIRED" && Message(expired).Contains("output_days", StringComparison.Ordinal) &&
              Error(tools.ArtifactRead(saved.Id, line_from: 1)) == "ARTIFACT_EXPIRED" && Error(tools.ArtifactSearch(saved.Id, "line")) == "ARTIFACT_EXPIRED",
            "reading or searching an artifact whose file vanished answers ARTIFACT_EXPIRED with the reason");
        var removed = tools.ArtifactRead(looseId);
        Check(Error(removed) == "NOT_FOUND" && Message(removed).Contains("retention", StringComparison.Ordinal),
            "an artifact that retention removed entirely answers NOT_FOUND and names retention as a cause");

        config.Retention.OutputDays = 0;
        Sql("INSERT INTO events(utc,kind,ref,json) VALUES($o,'kept-forever',NULL,'{}')", ("$o", old));
        runtime.Retention.Sweep(now);
        Check(store.Events("kept-forever").Count == 1, "output_days 0 keeps that state forever");
    }
}
