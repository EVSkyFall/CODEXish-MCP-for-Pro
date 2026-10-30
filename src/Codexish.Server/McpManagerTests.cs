using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Codexish.Server;

// Publication tests use on-disk manifests, the same cached lists used before any backend connects.
// The notification timing check changes only the backend's list under test, then invokes the real allocator.
public static class McpManagerTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-manager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string state = Path.Combine(directory, "state");
            var config = new ServerConfig { StateDir = state, Roots = [new RootConfig { Id = "a", Path = directory }] };
            config.McpServers = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["a_b"] = new { command = "unused-fixture", read_only_tools = new[] { "echo" } },
                ["resource"] = new { command = "unused-fixture", read_only_tools = new[] { "list" } },
                ["x_"] = new { command = "unused-fixture" },
                ["x!"] = new { command = "unused-fixture" }
            });
            config.BrowserMounts = [new() { Id = "a_b", RootId = "a", Kind = "custom", Command = "unused-fixture" },
                new() { Id = "a", RootId = "a", Kind = "custom", Command = "unused-fixture" }];
            File.WriteAllText(Path.Combine(directory, ".mcp.json"), """{"mcpServers":{"b":{"command":"unused-fixture","read_only_tools":["echo"]}}}""");
            void Manifest(string identity, string backend, bool browser = false, params string[] additional)
            {
                string path = browser ? Path.Combine(state, "browser-profiles", identity + ".manifest.json") :
                    Path.Combine(state, "mcp-mounts", Regex.Replace(identity, "[^A-Za-z0-9_-]", "_") + "-" +
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToLowerInvariant())))[..10].ToLowerInvariant() + ".manifest.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new { tools = new[] { backend }.Concat(additional).Select(name => new Tool { Name = name, InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }).ToArray() }, McpJsonUtilities.DefaultOptions));
            }
            Manifest("a_b", "echo"); Manifest("a/b", "echo"); Manifest("resource", "list");
            // Earlier literal names occupy both the normal hash and ordinal-2 candidates.
            string occupiedMcp = McpMounts.HashedName("mcp", "x!", "echo");
            string occupiedMcp2 = McpMounts.HashedName("mcp", "x!", "echo", 2);
            Manifest("x_", occupiedMcp["mcp_x__".Length..], false, occupiedMcp2["mcp_x__".Length..]);
            Manifest("x!", "echo");
            string occupiedBrowser = McpMounts.HashedName("browser", "a", "b_c");
            string occupiedBrowser2 = McpMounts.HashedName("browser", "a", "b_c", 2);
            Manifest("a_b", "c", true, occupiedBrowser["browser_a_b_".Length..], occupiedBrowser2["browser_a_b_".Length..]);
            Manifest("a", "b_c", true);
            using var runtime = new CodexishRuntime(config, true);
            await runtime.Mounts.PrepareAsync();
            var names = runtime.Mounts.Tools.Select(t => t.ProtocolTool.Name).ToArray();
            string projectHash = McpMounts.HashedName("mcp", "a/b", "echo");
            check(names.Contains("mcp_a_b_echo") && names.Contains(projectHash) && names.Count(n => n == "mcp_a_b_echo") == 1,
                "manager: top-level a_b keeps the plain name and project a/b keeps its tool with an identity hash");
            check(!names.Contains("mcp_resource_list") && names.Contains(McpMounts.HashedName("mcp", "resource", "list")),
                "manager: native resource-list name is reserved and a colliding backend is renamed rather than dropped");
            check(names.Contains("browser_a_b_c") && names.Contains(McpMounts.HashedName("browser", "a", "b_c", 3)) && names.Distinct().Count() == names.Length,
                "manager: earlier browser owns plain collision name and later browser uses its identity hash");
            check(runtime.Mounts.Find(projectHash)!.ProtocolTool.Description!.Contains("MCP server 'a/b' (its tool 'echo')") &&
                runtime.Mounts.Find("mcp_a_b_echo")!.ProtocolTool.Description!.Contains("MCP server 'a_b' (its tool 'echo')"),
                "manager: both colliding names retain their own server and backend routing descriptions");
            string ExpectedThird(string prefix, string identity, string backend)
            {
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToLowerInvariant() + "\n" + backend + "\n3")))[..10].ToLowerInvariant();
                return prefix + "_" + Regex.Replace(identity, "[^A-Za-z0-9_-]", "_") + "_" + backend + "_" + hash;
            }
            string thirdMcp = ExpectedThird("mcp", "x!", "echo"), thirdBrowser = ExpectedThird("browser", "a", "b_c");
            check(names.Contains(occupiedMcp) && names.Contains(occupiedMcp2) && names.Contains(thirdMcp) &&
                  runtime.Mounts.Find(occupiedMcp)!.ProtocolTool.Description!.Contains("MCP server 'x_'") &&
                  runtime.Mounts.Find(thirdMcp)!.ProtocolTool.Description!.Contains("MCP server 'x!'"),
                "manager: MCP hash collision with earlier literal names rehashes ordinal 2 then 3 without dropping or rerouting tools");
            check(names.Contains(occupiedBrowser) && names.Contains(occupiedBrowser2) && names.Contains(thirdBrowser) &&
                  runtime.Mounts.Find(thirdBrowser)!.ProtocolTool.Description!.Contains("browser backend 'a'") && names.Distinct().Count() == names.Length,
                "manager: browser hash collision with literal earlier names uses the same deterministic ordinal fallback");
            var earlier = (McpMount)runtime.Mounts.ResourceTargets.Single(t => t.Identity == "a_b");
            var toolsField = typeof(McpMount).GetField("backendTools", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var republish = typeof(McpMounts).GetMethod("Republish", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var known = earlier.BackendTools;
            toolsField.SetValue(earlier, Array.Empty<Tool>()); republish.Invoke(runtime.Mounts, null);
            check(runtime.Mounts.Find("mcp_a_b_echo")!.ProtocolTool.Description!.Contains("MCP server 'a/b'"),
                "manager: never-connected earlier server has no name reservation until its tools are known");
            toolsField.SetValue(earlier, known); republish.Invoke(runtime.Mounts, null);
            check(runtime.Mounts.Tools.Select(t => t.ProtocolTool.Name).SequenceEqual(names),
                "manager: after earlier tools arrive the final allocation is identical to all-manifests startup order");
            check(McpMounts.ToolName("a/b", "echo", false) != "mcp_a_b_echo" && McpMounts.ToolName("a/b", "echo", true) == "mcp_a_b_echo" &&
                McpMounts.ToolName(new string('a', 100), new string('b', 100)).Length == 64,
                "manager: top-level slash is sanitized with a hash, project separator is structural, long tool names fit 64 characters");
            var baseline = config.McpServers!.Value.Clone();
            var reload = typeof(McpMounts).GetMethod("ReadDefinitionsAndApply", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int captured = 0, toolReads = 0, finished = 0;
            using var reading = new ManualResetEventSlim();
            var reader = Task.Run(() =>
            {
                while (Volatile.Read(ref finished) == 0 || captured < 200)
                {
                    var tools = runtime.Mounts.Tools.Select(t => t.ProtocolTool.Name).ToArray();
                    if (!tools.Contains("mcp_a_b_echo") || !tools.Contains(projectHash) || tools.Distinct().Count() != tools.Length)
                        throw new InvalidOperationException("tools/list observed empty, partial, or duplicate publication during reload");
                    Interlocked.Increment(ref toolReads);
                    var capability = JsonSerializer.SerializeToElement(runtime.Capabilities());
                    var mounted = capability.GetProperty("browser").GetProperty("mounted").EnumerateArray()
                        .Concat(capability.GetProperty("mcp").GetProperty("mounted").EnumerateArray()).ToArray();
                    var declared = mounted.SelectMany(entry => entry.GetProperty("tools").EnumerateArray().Select(t => t.GetString()!)).ToArray();
                    var reported = capability.GetProperty("tools").EnumerateArray().Select(t => t.GetString()!).ToArray();
                    if (mounted.Length != 7 || !reported.Order().SequenceEqual(CodexishRuntime.ToolNames.Concat(declared).Order()) || reported.Distinct().Count() != reported.Length)
                        throw new InvalidOperationException("capability generation mixes server declarations and global tool names");
                    Interlocked.Increment(ref captured);
                    reading.Set();
                }
            });
            reading.Wait();
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    var replacement = System.Text.Json.Nodes.JsonNode.Parse(baseline.GetRawText())!.AsObject();
                    replacement["resource"]!["disabled"] = i % 2 == 0;
                    config.McpServers = JsonSerializer.SerializeToElement(replacement);
                    reload.Invoke(runtime.Mounts, null);
                }
            }
            finally { Volatile.Write(ref finished, 1); }
            await reader;
            check(captured >= 200 && toolReads >= 200, "manager: concurrent tools/list and capability reads during 100 nonstarted reloads never observe partial publication and retain native plus declared tools");

        }
        finally { TestCleanup.RemoveDirectory(directory); }
        await RapidReplacement(check);
        await PausedReplacement(check);
        await RetainedBrowser(check);
        await ReloadRecoversUnexpectedFailure(check);
        await SaveDuringReload(check);
        await SourceScopedReload(check);
        await KelvinIdentities(check);
        await NonblockingCapabilities(check);
        await NonblockingInitialPreparation(check);
        await StopBeforeStart(check);
        await ConfigurationWarningsRefresh(check);
        await InitialPreparationFailureRecovers(check);
        await ConfigurationIoFailureRetries(check);
        await StableConfigurationFailures(check);
        await StartupLifetimeOrdering(check);
        await NullBrowserReason(check);
        await RetainedWarmMountStarts(check);
        await RetainedInMemoryMcpStarts(check);
        await SuccessfulEmptyParseRemainsEmpty(check);
    }
    private static async Task RapidReplacement(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-rapid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string configPath = Path.Combine(directory, "codexish.json"), launchLog = Path.Combine(directory, "launches.log");
        string executable = Environment.ProcessPath!;
        string[] prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Assembly.GetExecutingAssembly().Location] : [];
        var config = new ServerConfig { StateDir = Path.Combine(directory, "state"), SourcePath = configPath };
        void Save(string generation)
        {
            File.WriteAllText(configPath, JsonSerializer.Serialize(new { mcp_servers = new Dictionary<string, object>
            {
                ["rapid"] = new { command = executable, args = prefix.Concat(new[] { "--local-mcp-fixture", directory, "--launch-log", launchLog, "--generation", generation }).ToArray(), read_only_tools = new[] { "slow", "inspect" } }
            }}));
        }
        static async Task<bool> Until(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(45)) return false; await Task.Delay(25); }
            return true;
        }
        Save("A");
        try
        {
            using var runtime = new CodexishRuntime(config, true);
            await runtime.Mounts.PrepareAsync();
            runtime.Mounts.Start();
            check(await Until(() => ((McpMount)runtime.Mounts.ResourceTargets.Single()).State == "connected"), "manager: rapid-replacement fixture A connects");
            var original = (McpMount)runtime.Mounts.ResourceTargets.Single();
            int originalPid = original.ProcessId!.Value;
            using var lease = original.AcquireResourceLease();
            var call = lease.Client!.CallToolAsync(new CallToolRequestParams { Name = "slow", Arguments = new Dictionary<string, JsonElement>() });
            check(await Until(() => File.Exists(Path.Combine(directory, "slow-started"))), "manager: old instance has a dispatched slow call before reload");
            Save("B");
            check(await Until(() => !ReferenceEquals(original, runtime.Mounts.ResourceTargets.Single())), "manager: polling observes replacement B without a concurrent forced reload");
            var middle = (McpMount)runtime.Mounts.ResourceTargets.Single();
            Save("C");
            check(await Until(() => !ReferenceEquals(middle, runtime.Mounts.ResourceTargets.Single())), "manager: polling observes replacement C while B is pending");
            var latest = (McpMount)runtime.Mounts.ResourceTargets.Single();
            check(original.State == "draining" && middle.ProcessId is null && latest.ProcessId is null && !call.IsCompleted,
                "manager: rapid A-to-B-to-C reload does not start either replacement while A's call is active");
            File.WriteAllText(Path.Combine(directory, "slow-release"), "release");
            await call;
            lease.Dispose();
            check(await Until(() => latest.State == "connected"), "manager: latest replacement starts after the original call finishes");
            bool originalAlive;
            try { using var old = Process.GetProcessById(originalPid); originalAlive = !old.HasExited; } catch (ArgumentException) { originalAlive = false; }
            check(!originalAlive && latest.ProcessId != originalPid && middle.ProcessId is null,
                "manager: A exits before C is connected and superseded B never starts");
            check(await Until(() => RetiredCount(runtime.Mounts) == 0), "manager: completed retirement tasks, mounts and lifetime keys are pruned after replacement");
            string launches = File.ReadAllText(launchLog);
            check(launches.Contains("--generation A", StringComparison.Ordinal) && launches.Contains("--generation C", StringComparison.Ordinal) && !launches.Contains("--generation B", StringComparison.Ordinal),
                "manager: fixture launch log confirms only generations A and C actually launched");
        }
        finally
        {
            // Release a fixture on a failed assertion as well; no test leaves a permanently held drain.
            File.WriteAllText(Path.Combine(directory, "slow-release"), "release");
            TestCleanup.RemoveDirectory(directory);
        }
    }

    private static async Task PausedReplacement(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-paused-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "codexish.json");
        string executable = Environment.ProcessPath!;
        string[] prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? [Assembly.GetExecutingAssembly().Location] : [];
        var config = new ServerConfig { StateDir = Path.Combine(directory, "state"), SourcePath = path,
            Roots = [new RootConfig { Id = "fixture", Path = directory, Write = false }] };
        void Save(string generation, bool readOnly)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { mcp_servers = new Dictionary<string, object>
            {
                ["queued"] = new { command = executable, root_id = "fixture", args = prefix.Concat(new[] { "--local-mcp-fixture", directory, "--generation", generation }).ToArray(), read_only_tools = readOnly ? new[] { "inspect" } : Array.Empty<string>() }
            }}));
        }
        static async Task<bool> Until(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(45)) return false; await Task.Delay(25); }
            return true;
        }
        Save("old", false);
        try
        {
            using var runtime = new CodexishRuntime(config, true);
            await runtime.Mounts.PrepareAsync();
            await using var host = CodexishHost.Build(runtime, 0);
            await host.StartAsync();
            try
            {
                check(await Until(() => ((McpMount)runtime.Mounts.ResourceTargets.Single()).State == "connected"), "manager: queued-call fixture starts with inspect classified as changing");
                int oldPid = ((McpMount)runtime.Mounts.ResourceTargets.Single()).ProcessId!.Value;
                string address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(address + "/mcp") }), new() { ProtocolVersion = CodexishRuntime.ProtocolVersion });
                runtime.Ledger.Pause();
                var queued = await client.CallToolAsync(new CallToolRequestParams { Name = "mcp_queued_inspect", Arguments = new Dictionary<string, JsonElement>
                {
                    ["arguments"] = JsonSerializer.SerializeToElement(new { }), ["invocation_id"] = JsonSerializer.SerializeToElement("paused-reload"), ["wait_ms"] = JsonSerializer.SerializeToElement(0)
                }});
                check(Reply.StatusOf(queued) == "queued", "manager: pause accepts changing call into ledger without dispatch");
                Save("new", true);
                check(await Until(() => ((McpMount)runtime.Mounts.ResourceTargets.Single()).State == "connected" && ((McpMount)runtime.Mounts.ResourceTargets.Single()).ProcessId != oldPid),
                    "manager: mount reload starts current instance while ledger remains paused");
                runtime.Ledger.Resume();
                check(await Until(() => Reply.StatusOf(runtime.Ledger.Inspect("paused-reload")) is not ("queued" or "running")), "manager: resumed queued call finishes on current configuration");
                var result = runtime.Ledger.Inspect("paused-reload");
                check(Reply.CodeOf(result) is null, "manager: queued call re-evaluates current read_only_tools and succeeds without root write grant");
                using var returned = JsonDocument.Parse(result.StructuredContent!.Value.GetProperty("data").GetProperty("text")[0].GetString()!);
                var data = returned.RootElement;
                check(data.GetProperty("pid").GetInt32() != oldPid && data.GetProperty("args").EnumerateArray().Any(a => a.GetString() == "new"),
                    "manager: accepted-before-reload invocation executes exactly on the new backend, not the retired one");
            }
            finally { runtime.Ledger.Resume(); await host.StopAsync(); }
        }
        finally { TestCleanup.RemoveDirectory(directory); }
    }

    private static async Task RetainedBrowser(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-browser-retain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "codexish.json");
        string variable = "CODEXISH_MANAGER_RELOAD_TEST", original = Environment.GetEnvironmentVariable(variable) ?? "";
        try
        {
            Environment.SetEnvironmentVariable(variable, "before");
            var config = new ServerConfig { StateDir = Path.Combine(directory, "state"), Roots = [new RootConfig { Id = "root", Path = directory }],
                BrowserMounts = [new BrowserMountConfig { Id = "retained", RootId = "root", Kind = "custom", Command = "unused-fixture", Args = ["${" + variable + "}"] }] };
            config.Save(path);
            using var runtime = new CodexishRuntime(config, true);
            await runtime.Mounts.PrepareAsync();
            var before = runtime.Mounts.ResourceTargets.Single();
            Environment.SetEnvironmentVariable(variable, "after");
            File.WriteAllText(path, "{\"browser_mounts\":{\"wrong\":\"type\"}}");
            var reload = typeof(McpMounts).GetMethod("ReadDefinitionsAndApply", BindingFlags.Instance | BindingFlags.NonPublic)!;
            reload.Invoke(runtime.Mounts, null);
            check(ReferenceEquals(before, runtime.Mounts.ResourceTargets.Single()),
                "manager: malformed browser_mounts retains its exact previous instance even when a referenced environment value changed");
            config.Save(path);
            reload.Invoke(runtime.Mounts, null);
            check(!ReferenceEquals(before, runtime.Mounts.ResourceTargets.Single()),
                "manager: repaired browser configuration applies the changed effective environment on next save");
        }
        finally { Environment.SetEnvironmentVariable(variable, original.Length == 0 ? null : original); TestCleanup.RemoveDirectory(directory); }
    }

    private static async Task ReloadRecoversUnexpectedFailure(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-reload-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "codexish.json");
        void Save(string identity)
        {
            string next = path + ".next";
            File.WriteAllText(next, JsonSerializer.Serialize(new { mcp_servers = new Dictionary<string, object>
            { [identity] = new { command = "unused-fixture", disabled = true } } }));
            File.Move(next, path, overwrite: true);
        }
        static async Task<bool> Until(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(30)) return false; await Task.Delay(20); }
            return true;
        }
        Save("before");
        try
        {
            using var runtime = new CodexishRuntime(new ServerConfig { StateDir = Path.Combine(directory, "state"), SourcePath = path }, true);
            await runtime.Mounts.PrepareAsync();
            var original = runtime.Mounts.ResourceTargets.Single();
            int reads = 0;
            string secret = "ghp_" + new string('q', 36);
            runtime.Mounts.BeforeReloadRead = () =>
            {
                if (Interlocked.Increment(ref reads) == 1) throw new InvalidOperationException("injected reader failure " + secret);
            };
            Save("after-failure");
            byte[] unchangedFile = File.ReadAllBytes(path);
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            runtime.Mounts.Start();
            string? Warning() => JsonSerializer.SerializeToElement(runtime.Mounts.Describe()).GetProperty("reload_error").GetString();
            check(await Until(() => Warning()?.Contains("injected reader failure", StringComparison.Ordinal) == true),
                "manager: unexpected reload reader exception is reported instead of ending supervision");
            string warning = Warning()!;
            check(!warning.Contains(secret, StringComparison.Ordinal) && warning.Contains("REDACTED", StringComparison.Ordinal) &&
                  ReferenceEquals(original, runtime.Mounts.ResourceTargets.Single()),
                "manager: unexpected reader warning is redacted and the previous mount survives the failed iteration");
            check(await Until(() => runtime.Mounts.ResourceTargets.Single().Identity == "after-failure") && reads >= 2 &&
                  File.ReadAllBytes(path).SequenceEqual(unchangedFile) && File.GetLastWriteTimeUtc(path) == timestamp,
                "manager: reload retries consumed file stamps and applies the exact unchanged file after a reader failure");
            Save("subsequent-save");
            check(await Until(() => runtime.Mounts.ResourceTargets.Single().Identity == "subsequent-save") && Warning() is null,
                "manager: later saves still apply normally after unexpected reload recovery");
            int PriorFailures() => runtime.Store.Events("mcp_reload_warning").Count(e => e.Json?.Contains("injected reader failure", StringComparison.Ordinal) == true);
            check(PriorFailures() == 1, "manager: first unexpected reload failure has one diagnostic before recurrence");
            Interlocked.Exchange(ref reads, 0);
            Save("same-failure-again");
            check(await Until(() => PriorFailures() == 2),
                "manager: the same unexpected failure after a successful full pass records a new diagnostic event");
            check(await Until(() => runtime.Mounts.ResourceTargets.Single().Identity == "same-failure-again") && Warning() is null,
                "manager: repeated unexpected failure recovers again without another file save");
        }
        finally { TestCleanup.RemoveDirectory(directory); }
    }

    private static async Task SaveDuringReload(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-save-during-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "codexish.json");
        void Save(string identity)
        {
            File.WriteAllText(path + ".next", JsonSerializer.Serialize(new { mcp_servers = new Dictionary<string, object>
            { [identity] = new { command = "unused-fixture", disabled = true } } }));
            File.Move(path + ".next", path, overwrite: true);
        }
        static async Task<bool> Until(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(30)) return false; await Task.Delay(20); }
            return true;
        }
        Save("initial");
        try
        {
            using var runtime = new CodexishRuntime(new ServerConfig { StateDir = Path.Combine(directory, "state"), SourcePath = path }, true);
            await runtime.Mounts.PrepareAsync();
            int saves = 0;
            byte[]? savedBytes = null;
            DateTime savedAt = default;
            runtime.Mounts.AfterReloadRead = () =>
            {
                if (Interlocked.Increment(ref saves) != 1) return;
                Save("generation-B-saved-during-read");
                savedBytes = File.ReadAllBytes(path);
                savedAt = File.GetLastWriteTimeUtc(path);
            };
            Save("generation-A");
            runtime.Mounts.Start();
            check(await Until(() => Volatile.Read(ref saves) > 0 && runtime.Mounts.ResourceTargets.Single().Identity == "generation-A"),
                "manager: newer save lands after generation A was read but before A was published");
            check(await Until(() => runtime.Mounts.ResourceTargets.Single().Identity == "generation-B-saved-during-read"),
                "manager: save during reload is not consumed by trailing stamp bookkeeping and is applied on the next tick");
            check(savedBytes is not null && File.ReadAllBytes(path).SequenceEqual(savedBytes) && File.GetLastWriteTimeUtc(path) == savedAt,
                "manager: newer generation is applied without another file write or timestamp change");
        }
        finally { TestCleanup.RemoveDirectory(directory); }
    }

    private static int RetiredCount(McpMounts mounts)
    {
        int total = 0;
        foreach (string name in new[] { "retiredMounts", "retirements", "lifetimes" })
        {
            var collection = typeof(McpMounts).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mounts)!;
            total += (int)collection.GetType().GetProperty("Count")!.GetValue(collection)!;
        }
        return total;
    }

    private static async Task<bool> Observe(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(30)) return false; await Task.Delay(20); }
        return true;
    }

    private static async Task SourceScopedReload(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-source-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "codexish.json"), projectPath = Path.Combine(directory, ".mcp.json");
        string variable = "CODEXISH_MANAGER_SOURCE_SCOPE", old = Environment.GetEnvironmentVariable(variable) ?? "";
        string Definition(string generation) => JsonSerializer.Serialize(new { command = "unused-fixture", disabled = true, args = new[] { "${" + variable + "}", generation } });
        void SaveProject(string generation) => File.WriteAllText(projectPath, "{\"mcpServers\":{\"project\":" + Definition(generation) + "}}");
        void SaveMain(string generation) => File.WriteAllText(path, "{\"mcp_servers\":{\"top\":" + Definition(generation) + "}}");
        try
        {
            Environment.SetEnvironmentVariable(variable, "original"); SaveMain("main-A"); SaveProject("project-A");
            using var runtime = new CodexishRuntime(new ServerConfig { StateDir = Path.Combine(directory, "state"), SourcePath = path,
                Roots = [new RootConfig { Id = "root", Path = directory }] }, true);
            await runtime.Mounts.PrepareAsync();
            IMcpResourceTarget Target(string identity) => runtime.Mounts.ResourceTargets.Single(t => t.Identity == identity);
            var top = Target("top"); var project = Target("root/project");
            Environment.SetEnvironmentVariable(variable, "project-change"); SaveProject("project-B"); runtime.Mounts.Start();
            check(await Observe(() => !ReferenceEquals(project, Target("root/project"))) && ReferenceEquals(top, Target("top")),
                "manager: project-file save re-fingerprints that project only, retaining unchanged top-level environment signature");
            project = Target("root/project");
            Environment.SetEnvironmentVariable(variable, "main-change"); SaveMain("main-B");
            check(await Observe(() => !ReferenceEquals(top, Target("top"))) && ReferenceEquals(project, Target("root/project")),
                "manager: main-file save re-fingerprints main definitions only, retaining unchanged project environment signature");
            top = Target("top");
            SaveProject("project-B");
            check(await Observe(() => !ReferenceEquals(project, Target("root/project"))) && ReferenceEquals(top, Target("top")),
                "manager: a later project save applies its previously deferred environment change without restarting main definitions");
        }
        finally { Environment.SetEnvironmentVariable(variable, old.Length == 0 ? null : old); TestCleanup.RemoveDirectory(directory); }
    }

    private static async Task KelvinIdentities(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-kelvin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "codexish.json");
        void Save(string generation) => File.WriteAllText(path, JsonSerializer.Serialize(new { mcp_servers = new Dictionary<string, object>
        {
            ["\u212A"] = new { command = "unused-fixture", disabled = true, args = new[] { generation } },
            ["k"] = new { command = "unused-fixture", disabled = true, args = new[] { generation } },
            ["unrelated"] = new { command = "unused-fixture", disabled = true }
        }}));
        try
        {
            Save("first");
            using var runtime = new CodexishRuntime(new ServerConfig { StateDir = Path.Combine(directory, "state"), SourcePath = path }, true);
            await runtime.Mounts.PrepareAsync();
            var before = runtime.Mounts.ResourceTargets.ToArray();
            check(before.Length == 3 && before.Select(t => t.Identity).Contains("\u212A") && before.Select(t => t.Identity).Contains("k"),
                "manager: Kelvin-sign and ASCII k identities survive initial preparation without lowercased-key collision");
            Save("second-generation"); runtime.Mounts.Start();
            check(await Observe(() => runtime.Mounts.ResourceTargets.Any(t => t.Identity == "k" && !before.Contains(t))) &&
                  runtime.Mounts.ResourceTargets.Count == 3 && runtime.Mounts.ResourceTargets.Any(t => t.Identity == "\u212A" && !before.Contains(t)),
                "manager: Kelvin-sign and ASCII k identities both reload while unrelated definition remains available");
            check(ReferenceEquals(before.Single(t => t.Identity == "unrelated"), runtime.Mounts.ResourceTargets.Single(t => t.Identity == "unrelated")),
                "manager: identity comparer containment does not restart an unrelated unchanged entry");
        }
        finally { TestCleanup.RemoveDirectory(directory); }
    }

    private static async Task NonblockingCapabilities(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-nonblocking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "codexish.json");
        void Save(string generation) => File.WriteAllText(path, JsonSerializer.Serialize(new { mcp_servers = new Dictionary<string, object>
            { [generation] = new { command = "unused-fixture", disabled = true } } }));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            Save("initial");
            using var runtime = new CodexishRuntime(new ServerConfig { StateDir = Path.Combine(directory, "state"), SourcePath = path }, true);
            await runtime.Mounts.PrepareAsync();
            runtime.Mounts.BeforeReloadRead = () => { entered.Set(); release.Wait(); };
            Save("replacement"); runtime.Mounts.Start();
            try
            {
                check(await Observe(() => entered.IsSet), "manager: configuration reader is observably blocked before replacement publication");
                var capabilities = Task.Run(() => JsonSerializer.SerializeToElement(runtime.Capabilities()));
                bool returned = await Observe(() => capabilities.IsCompleted);
                check(returned && !release.IsSet, "manager: host_capabilities returns while the reload reader lock remains held, without waiting for release");
                if (returned) check((await capabilities).GetProperty("mcp").GetProperty("mounted").EnumerateArray().Single().GetProperty("identity").GetString() == "initial",
                    "manager: nonblocking capabilities retains the complete prior generation while reload is blocked");
            }
            finally { release.Set(); }
            check(await Observe(() => runtime.Mounts.ResourceTargets.Single().Identity == "replacement"),
                "manager: blocked read publishes its replacement after release without preventing future progress");
        }
        finally { release.Set(); TestCleanup.RemoveDirectory(directory); }
    }

    private static async Task NonblockingInitialPreparation(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-initial-nonblocking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            string state = Path.Combine(directory, "state"), configPath = Path.Combine(directory, "codexish.json");
            var config = new ServerConfig { StateDir = state, Roots = [new RootConfig { Id = "root", Path = directory }],
                McpServers = JsonSerializer.SerializeToElement(new { cached = new { command = "unused-fixture", read_only_tools = new[] { "inspect" } } }),
                BrowserMounts = [new BrowserMountConfig { Id = "cached", RootId = "root", Kind = "custom", Command = "unused-fixture", ReadOnlyTools = ["inspect"] }] };
            config.Save(configPath);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cached")))[..10].ToLowerInvariant();
            foreach (string manifest in new[] { Path.Combine(state, "mcp-mounts", "cached-" + hash + ".manifest.json"), Path.Combine(state, "browser-profiles", "cached.manifest.json") })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                File.WriteAllBytes(manifest, JsonSerializer.SerializeToUtf8Bytes(new { tools = new[] { new Tool { Name = "inspect", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) } } }, McpJsonUtilities.DefaultOptions));
            }
            using var runtime = new CodexishRuntime(config, true);
            runtime.Mounts.BeforeReloadRead = () => { entered.Set(); release.Wait(); };
            await using var host = CodexishHost.Build(runtime, 0);
            Task start = host.StartAsync();
            try
            {
                check(await Observe(() => entered.IsSet), "manager: first mount preparation is observably blocked on its reader");
                check(await Observe(() => start.IsCompleted) && !release.IsSet,
                    "manager: host starts listening without waiting for first mount fingerprint preparation");
                await start;
                string address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var http = new HttpClient();
                var response = http.GetAsync(address + "/healthz");
                check(await Observe(() => response.IsCompleted) && !release.IsSet && (await response).IsSuccessStatusCode,
                    "manager: listening host answers health requests while initial mount configuration remains blocked");
                await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(address + "/mcp") }), new() { ProtocolVersion = CodexishRuntime.ProtocolVersion });
                var listed = (await client.ListToolsAsync()).Select(t => t.Name).ToArray();
                check(!release.IsSet && listed.Contains("mcp_cached_inspect") && listed.Contains("browser_cached_inspect"),
                    "manager: MCP and browser manifests are both listed immediately after host start while background preparation is held");
                var unavailable = await client.CallToolAsync(new CallToolRequestParams { Name = "mcp_cached_inspect", Arguments = new Dictionary<string, JsonElement>
                    { ["arguments"] = JsonSerializer.SerializeToElement(new { }) } });
                var error = unavailable.StructuredContent!.Value.GetProperty("error");
                check(!release.IsSet && Reply.CodeOf(unavailable) == "MCP_UNAVAILABLE" && error.GetProperty("side_effects").GetString() == "none" &&
                      error.GetProperty("message").GetString() == "MCP server 'cached' is not_started; nothing was sent to it. CODEXish keeps restarting it.",
                    "manager: cached MCP tool immediately returns verbatim A.6 unavailable with effects none, never unknown tool");
                var browserUnavailable = await client.CallToolAsync(new CallToolRequestParams { Name = "browser_cached_inspect", Arguments = new Dictionary<string, JsonElement>
                    { ["arguments"] = JsonSerializer.SerializeToElement(new { }) } });
                check(!release.IsSet && Reply.CodeOf(browserUnavailable) == "BROWSER_UNAVAILABLE" &&
                      browserUnavailable.StructuredContent!.Value.GetProperty("error").GetProperty("side_effects").GetString() == "none",
                    "manager: cached browser tool also dispatches immediately to its unavailable response before background starts");
            }
            finally { release.Set(); await start; await host.StopAsync(); }
        }
        finally { release.Set(); TestCleanup.RemoveDirectory(directory); }
    }

    private static async Task StopBeforeStart(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-stop-before-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var runtime = new CodexishRuntime(new ServerConfig { StateDir = Path.Combine(directory, "state"),
                McpServers = JsonSerializer.SerializeToElement(new { stopped = new { command = "unused-fixture" } }) }, true);
            await runtime.Mounts.PrepareAsync();
            var mount = (McpMount)runtime.Mounts.ResourceTargets.Single();
            runtime.Mounts.Stop(); runtime.Mounts.Start(); mount.Start();
            // Start synchronously decides whether to schedule a supervision task; no timing sleep is needed.
            bool started = (bool)typeof(McpMount).GetField("started", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mount)!;
            check(!started && !mount.HasSession && mount.ProcessId is null,
                "manager: neither manager nor direct mount Start schedules supervision after Stop");
        }
        finally { TestCleanup.RemoveDirectory(directory); }
    }

    private static async Task ConfigurationWarningsRefresh(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-warning-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new ServerConfig { StateDir = Path.Combine(directory, "state") };
            void SetArgs(bool scalar) => config.McpServers = JsonDocument.Parse(scalar
                ? "{\"warnings\":{\"command\":\"unused-fixture\",\"args\":\"same\"}}"
                : "{\"warnings\":{\"command\":\"unused-fixture\",\"args\":[\"same\"]}}").RootElement.Clone();
            SetArgs(false);
            // A malformed optional manifest produces a real runtime warning, independent of parse warnings.
            string manifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("warnings")))[..10].ToLowerInvariant();
            string manifest = Path.Combine(config.StateDir, "mcp-mounts", "warnings-" + manifestHash + ".manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!); File.WriteAllText(manifest, "{");
            using var runtime = new CodexishRuntime(config, true);
            await runtime.Mounts.PrepareAsync();
            var original = runtime.Mounts.ResourceTargets.Single();
            string[] Warnings() => JsonSerializer.SerializeToElement(runtime.Mounts.Describe()).GetProperty("mounted")[0]
                .GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray();
            int configWarningCount = config.Warnings.Count;
            string runtimeWarning = Warnings().Single(w => w.StartsWith("manifest read:", StringComparison.Ordinal));
            var reload = typeof(McpMounts).GetMethod("ReadDefinitionsAndApply", BindingFlags.Instance | BindingFlags.NonPublic)!;
            SetArgs(true); reload.Invoke(runtime.Mounts, null);
            check(ReferenceEquals(original, runtime.Mounts.ResourceTargets.Single()) && Warnings().Any(w => w.Contains("args is a scalar", StringComparison.Ordinal)),
                "manager: equivalent array-to-scalar config keeps the mount but refreshes its parse warning");
            for (int i = 0; i < 5; i++) reload.Invoke(runtime.Mounts, null);
            check(Warnings().Count(w => w.Contains("args is a scalar", StringComparison.Ordinal)) == 1 && config.Warnings.Count == configWarningCount,
                "manager: repeated equivalent reloads deduplicate parse warnings without growing shared config warnings");
            SetArgs(false); reload.Invoke(runtime.Mounts, null);
            check(ReferenceEquals(original, runtime.Mounts.ResourceTargets.Single()) && !Warnings().Any(w => w.Contains("args is a scalar", StringComparison.Ordinal)),
                "manager: equivalent scalar-to-array repair clears the obsolete parse warning without restart");
            check(Warnings().Contains(runtimeWarning) && config.Warnings.Count == configWarningCount,
                "manager: configuration warning refresh preserves independent runtime warnings and leaves shared warnings unchanged");
            string variable = "CODEXISH_MANAGER_DISPLAY_ARGS", previous = Environment.GetEnvironmentVariable(variable) ?? "";
            try
            {
                Environment.SetEnvironmentVariable(variable, "same");
                config.McpServers = JsonSerializer.SerializeToElement(new { warnings = new { command = "unused-fixture", args = new[] { "${" + variable + "}" } } });
                reload.Invoke(runtime.Mounts, null);
                check(ReferenceEquals(original, runtime.Mounts.ResourceTargets.Single()) &&
                      JsonSerializer.SerializeToElement(runtime.Capabilities()).GetProperty("mcp").GetProperty("mounted")[0].GetProperty("args")[0].GetString() == "${" + variable + "}",
                    "manager: equivalent variable spelling refreshes as-written argument display without replacing the mount");
                SetArgs(false); reload.Invoke(runtime.Mounts, null);
                check(ReferenceEquals(original, runtime.Mounts.ResourceTargets.Single()) &&
                      JsonSerializer.SerializeToElement(runtime.Capabilities()).GetProperty("mcp").GetProperty("mounted")[0].GetProperty("args")[0].GetString() == "same",
                    "manager: variable-to-literal effective-equivalent args keep the instance and display the current literal configuration");
            }
            finally { Environment.SetEnvironmentVariable(variable, previous.Length == 0 ? null : previous); }
        }
        finally { TestCleanup.RemoveDirectory(directory); }
    }

    private static async Task InitialPreparationFailureRecovers(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-initial-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "codexish.json");
        File.WriteAllText(path, "{\"mcp_servers\":{\"recovered\":{\"command\":\"unused-fixture\"}}}");
        try
        {
            using var runtime = new CodexishRuntime(new ServerConfig { StateDir = Path.Combine(directory, "state"), SourcePath = path }, true);
            int reads = 0;
            McpMount? capturedWarm = null;
            bool warmStayedUnstarted = false;
            runtime.Mounts.BeforeReloadRead = () =>
            {
                if (Interlocked.Increment(ref reads) == 1) throw new InvalidOperationException("initial injected reader failure");
                warmStayedUnstarted = capturedWarm is not null && !(bool)typeof(McpMount).GetField("started", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capturedWarm)!;
            };
            Task preparation = runtime.Mounts.PrepareAsync();
            await preparation;
            string? error = JsonSerializer.SerializeToElement(runtime.Mounts.Describe()).GetProperty("reload_error").GetString();
            check(preparation.IsCompletedSuccessfully && error?.Contains("initial injected reader failure", StringComparison.Ordinal) == true && runtime.Mounts.ResourceTargets.Any(t => t.Identity == "recovered"),
                "manager: initial reader exception produces a warning without faulting the cached preparation task");
            var warmTarget = runtime.Mounts.ResourceTargets.Single(t => t.Identity == "recovered");
            capturedWarm = (McpMount)warmTarget;
            runtime.Mounts.Start();
            check(await Observe(() => reads >= 2 && !ReferenceEquals(warmTarget, runtime.Mounts.ResourceTargets.Single(t => t.Identity == "recovered")) &&
                JsonSerializer.SerializeToElement(runtime.Mounts.Describe()).GetProperty("reload_error").ValueKind == JsonValueKind.Null),
                "manager: normal polling retries the initial preparation failure and applies the unchanged file");
            check(warmStayedUnstarted, "manager: failed initial full pass never starts its warm manifest instance before successful polling recovery");
            Task disposal = runtime.Mounts.DisposeAsync().AsTask(); await disposal;
            check(disposal.IsCompletedSuccessfully,
                "manager: disposal completes after initial preparation failure and successful polling recovery");
        }
        finally { TestCleanup.RemoveDirectory(directory); }
    }

    private static async Task ConfigurationIoFailureRetries(Action<bool, string> check)
    {
        foreach (bool projectFailure in new[] { false, true })
        {
            string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-io-retry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string mainPath = Path.Combine(directory, "codexish.json"), projectPath = Path.Combine(directory, ".mcp.json");
            string Json(string field, string name, string generation) => JsonSerializer.Serialize(new Dictionary<string, object>
                { [field] = new Dictionary<string, object> { [name] = new { command = "unused-fixture", disabled = true, args = new[] { generation } } } });
            File.WriteAllText(mainPath, Json("mcp_servers", "main", "before"));
            File.WriteAllText(projectPath, Json("mcpServers", "project", "before"));
            string targetPath = projectFailure ? projectPath : mainPath, identity = projectFailure ? "root/project" : "main";
            try
            {
                using var runtime = new CodexishRuntime(new ServerConfig { StateDir = Path.Combine(directory, "state"), SourcePath = mainPath,
                    Roots = [new RootConfig { Id = "root", Path = directory }] }, true);
                await runtime.Mounts.PrepareAsync();
                var before = runtime.Mounts.ResourceTargets.Single(t => t.Identity == identity);
                int attempts = 0;
                runtime.Mounts.ReadConfigurationFile = path =>
                {
                    if (path == targetPath && Interlocked.Increment(ref attempts) == 1)
                        throw new IOException("injected transient configuration sharing violation");
                    return File.ReadAllBytes(path);
                };
                File.WriteAllText(targetPath, Json(projectFailure ? "mcpServers" : "mcp_servers", projectFailure ? "project" : "main", "after-transient-io"));
                byte[] unchanged = File.ReadAllBytes(targetPath); DateTime modified = File.GetLastWriteTimeUtc(targetPath);
                runtime.Mounts.Start();
                bool Reported()
                {
                    var status = JsonSerializer.SerializeToElement(runtime.Mounts.Describe());
                    return projectFailure
                        ? status.GetProperty("project_files").EnumerateArray().Any(p => p.GetProperty("root_id").GetString() == "root" && p.GetProperty("state").GetString() == "unreadable")
                        : status.GetProperty("reload_error").ValueKind == JsonValueKind.String && status.GetProperty("reload_error").GetString()!.Contains("injected transient", StringComparison.Ordinal);
                }
                check(await Observe(Reported) && ReferenceEquals(before, runtime.Mounts.ResourceTargets.Single(t => t.Identity == identity)),
                    "manager: " + (projectFailure ? "project" : "main") + " transient I/O failure reports the cause and retains the previous instance");
                check(await Observe(() => attempts >= 2 && !ReferenceEquals(before, runtime.Mounts.ResourceTargets.Single(t => t.Identity == identity))),
                    "manager: " + (projectFailure ? "project" : "main") + " failed-read stamp is dropped and the unchanged saved file is retried");
                check(File.ReadAllBytes(targetPath).SequenceEqual(unchanged) && File.GetLastWriteTimeUtc(targetPath) == modified,
                    "manager: " + (projectFailure ? "project" : "main") + " I/O retry applies without a second file save or timestamp change");
            }
            finally { TestCleanup.RemoveDirectory(directory); }
        }
    }

    private static async Task StableConfigurationFailures(Action<bool, string> check)
    {
        foreach (bool denied in new[] { false, true })
        {
            string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-stable-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "codexish.json");
            if (denied) File.WriteAllText(path, "{}");
            try
            {
                using var runtime = new CodexishRuntime(new ServerConfig { StateDir = Path.Combine(directory, "state"), SourcePath = path }, true);
                int reads = 0;
                runtime.Mounts.ReadConfigurationFile = file =>
                {
                    Interlocked.Increment(ref reads);
                    if (denied) throw new UnauthorizedAccessException("injected configuration access denied");
                    return File.ReadAllBytes(file);
                };
                await runtime.Mounts.PrepareAsync();
                int initialReads = reads;
                var changed = typeof(McpMounts).GetMethod("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var apply = typeof(McpMounts).GetMethod("ApplyRead", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var paths = typeof(McpMounts).GetField("changedPaths", BindingFlags.Instance | BindingFlags.NonPublic)!;
                for (int i = 0; i < 5; i++)
                    if ((bool)changed.Invoke(runtime.Mounts, null)!) apply.Invoke(runtime.Mounts, [paths.GetValue(runtime.Mounts), false]);
                check(reads == initialReads, "manager: " + (denied ? "denied" : "missing") + " configuration keeps its stamp across five polling passes");
                check(runtime.Store.Events("mcp_reload_warning").Count == 1,
                    "manager: repeated " + (denied ? "denied" : "missing") + " configuration warning is recorded once in SQLite across preparation and polls");
                // Explicit re-reads of the same file still deduplicate identical diagnostic text.
                var reload = typeof(McpMounts).GetMethod("ReadDefinitionsAndApply", BindingFlags.Instance | BindingFlags.NonPublic)!;
                reload.Invoke(runtime.Mounts, null); reload.Invoke(runtime.Mounts, null);
                check(runtime.Store.Events("mcp_reload_warning").Count == 1,
                    "manager: identical per-file warning text remains deduplicated on repeated actual reads");
            }
            finally { TestCleanup.RemoveDirectory(directory); }
        }
    }

    private static async Task StartupLifetimeOrdering(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-start-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var started = typeof(McpMount).GetField("started", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            var config = new ServerConfig { StateDir = Path.Combine(directory, "warm"), McpServers = JsonSerializer.SerializeToElement(new
                { ordered = new { command = "unused-fixture" } }) };
            using (var runtime = new CodexishRuntime(config, true))
            {
                var prepareSync = typeof(McpMounts).GetField("prepareSync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime.Mounts)!;
                lock (prepareSync)
                {
                    runtime.Mounts.Start();
                    runtime.Mounts.PrepareManifests();
                    var warm = (McpMount)runtime.Mounts.ResourceTargets.Single();
                    check(!(bool)started.GetValue(warm)! && warm.Attempts == 0,
                        "manager: Start before manifest preparation never starts the manifest-only instance");
                }
                await runtime.Mounts.PrepareAsync();
            }
            config.StateDir = Path.Combine(directory, "predecessor");
            config.SourcePath = Path.Combine(directory, "codexish.json");
            File.WriteAllText(config.SourcePath, "{\"mcp_servers\":{\"ordered\":{\"command\":\"unused-fixture\"}}}");
            using (var runtime = new CodexishRuntime(config, true))
            {
                await runtime.Mounts.PrepareAsync();
                var mount = (McpMount)runtime.Mounts.ResourceTargets.Single();
                var predecessor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var lifetimes = (Dictionary<string, Task>)typeof(McpMounts).GetField("lifetimes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime.Mounts)!;
                // Model the existing replacement continuation: the initial loop must not bypass it.
                var startAfter = typeof(McpMounts).GetMethod("StartAfter", BindingFlags.Instance | BindingFlags.NonPublic)!;
                lifetimes["mcp:7:ordered:0"] = (Task)startAfter.Invoke(runtime.Mounts, [predecessor.Task, mount])!;
                try
                {
                    runtime.Mounts.Start();
                    // A poll proves the initial start loop has already been passed, without using a tight timing bound.
                    int polled = 0; runtime.Mounts.BeforeReloadRead = () => Interlocked.Increment(ref polled);
                    File.AppendAllText(config.SourcePath, " ");
                    var loop = typeof(McpMounts).GetField("reloadLoop", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    bool observedPoll = await Observe(() => Volatile.Read(ref polled) != 0);
                    check(observedPoll && !(bool)started.GetValue(mount)! && !((Task)loop.GetValue(runtime.Mounts)!).IsCompleted,
                        "manager: initial start loop does not start a mount while its predecessor lifetime is unfinished");
                    predecessor.TrySetResult();
                    check(await Observe(() => (bool)started.GetValue(mount)!),
                        "manager: initial mount starts after its predecessor lifetime completes");
                }
                finally { predecessor.TrySetResult(); }
            }
        }
        finally { TestCleanup.RemoveDirectory(directory); }
    }

    private static async Task NullBrowserReason(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-null-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var runtime = new CodexishRuntime(new ServerConfig { StateDir = directory, BrowserMounts = [null!] }, true);
            await runtime.Mounts.PrepareAsync();
            var browser = JsonSerializer.SerializeToElement(runtime.Mounts.DescribeBrowsers()).GetProperty("mounted")[0];
            check(browser.GetProperty("error").GetString() == "the browser_mounts entry is null",
                "manager: null browser entry reason has no final period");
        }
        finally { TestCleanup.RemoveDirectory(directory); }
    }

    private static async Task RetainedWarmMountStarts(Action<bool, string> check)
    {
        foreach (bool malformedMain in new[] { true, false })
        {
            string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-warm-retain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "codexish.json");
            string executable = Environment.ProcessPath!;
            string[] prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? [Assembly.GetExecutingAssembly().Location] : [];
            string invalid = malformedMain ? "{ not json" : "{\"browser_mounts\":{\"wrong\":\"type\"}}";
            File.WriteAllText(path, invalid);
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            try
            {
                using var runtime = new CodexishRuntime(new ServerConfig
                {
                    StateDir = Path.Combine(directory, "state"), SourcePath = path,
                    Roots = [new RootConfig { Id = "root", Path = directory }],
                    BrowserMounts = [new BrowserMountConfig { Id = "retained", RootId = "root", Kind = "custom", Command = executable,
                        Args = prefix.Concat(new[] { "--local-mcp-fixture", directory }).ToArray(), ReadOnlyTools = ["inspect"] }]
                }, true);
                runtime.Mounts.PrepareManifests();
                var warm = (McpMount)runtime.Mounts.ResourceTargets.Single();
                check(warm.State == "not_started" && warm.Attempts == 0,
                    "manager: retained in-memory browser is published but not started by manifest-only pass with " + (malformedMain ? "bad JSON" : "wrong browser type"));
                await runtime.Mounts.PrepareAsync();
                var prepared = (McpMount)runtime.Mounts.ResourceTargets.Single();
                check(!ReferenceEquals(warm, prepared),
                    "manager: full pass fingerprints and replaces retained warm browser despite " + (malformedMain ? "bad JSON" : "wrong browser type"));
                runtime.Mounts.Start();
                check(await Observe(() => prepared.State == "connected") && File.ReadAllText(path) == invalid && File.GetLastWriteTimeUtc(path) == timestamp,
                    "manager: retained warm browser connects without a re-save despite " + (malformedMain ? "bad JSON" : "wrong browser type"));
                check(!(bool)typeof(McpMount).GetField("started", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(warm)!,
                    "manager: original warm browser remains unstarted after replacement connects despite " + (malformedMain ? "bad JSON" : "wrong browser type"));
            }
            finally { TestCleanup.RemoveDirectory(directory); }
        }
    }

    private static async Task RetainedInMemoryMcpStarts(Action<bool, string> check)
    {
        foreach (bool alias in new[] { false, true })
        foreach (bool missing in new[] { false, true })
        {
            string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-memory-retain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "codexish.json");
            const string invalid = "{ not json";
            if (!missing) File.WriteAllText(path, invalid);
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            string executable = Environment.ProcessPath!;
            string[] prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? [Assembly.GetExecutingAssembly().Location] : [];
            var definition = JsonSerializer.SerializeToElement(new { retained = new { command = executable,
                args = prefix.Concat(new[] { "--local-mcp-fixture", directory }).ToArray(), read_only_tools = new[] { "inspect" } } });
            string context = (alias ? "mcpServers" : "mcp_servers") + (missing ? " with missing main file" : " with malformed main file");
            try
            {
                using var runtime = new CodexishRuntime(new ServerConfig
                {
                    StateDir = Path.Combine(directory, "state"), SourcePath = path,
                    McpServers = alias ? null : definition, McpServersAlias = alias ? definition : null
                }, true);
                runtime.Mounts.PrepareManifests();
                var warm = runtime.Mounts.ResourceTargets.OfType<McpMount>().SingleOrDefault(t => t.Identity == "retained");
                check(warm is not null && warm.State == "not_started" && warm.Attempts == 0,
                    "manager: in-memory " + context + " publishes an unstarted warm MCP instance");
                await runtime.Mounts.PrepareAsync();
                var prepared = runtime.Mounts.ResourceTargets.OfType<McpMount>().Single(t => t.Identity == "retained");
                check(!ReferenceEquals(warm, prepared), "manager: full pass fingerprints retained in-memory " + context);
                runtime.Mounts.Start();
                check(await Observe(() => prepared.State == "connected") &&
                    (missing ? !File.Exists(path) : File.ReadAllText(path) == invalid && File.GetLastWriteTimeUtc(path) == timestamp),
                    "manager: retained in-memory " + context + " connects without a re-save");
                check(!(bool)typeof(McpMount).GetField("started", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(warm)!,
                    "manager: original warm " + context + " stays unstarted after replacement connects");
            }
            finally { TestCleanup.RemoveDirectory(directory); }
        }
    }

    private static async Task SuccessfulEmptyParseRemainsEmpty(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-empty-retain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "codexish.json");
        File.WriteAllText(path, "{}");
        try
        {
            var config = new ServerConfig { StateDir = Path.Combine(directory, "state"), SourcePath = path };
            using var runtime = new CodexishRuntime(config, true);
            await runtime.Mounts.PrepareAsync();
            // Simulate a stale in-memory entry after a real successful empty parse.
            config.McpServers = JsonSerializer.SerializeToElement(new { stale = new { command = "unused-fixture" } });
            File.WriteAllText(path, "{ not json");
            typeof(McpMounts).GetMethod("ReadDefinitionsAndApply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime.Mounts, null);
            check(runtime.Mounts.ResourceTargets.Count == 0,
                "manager: failed read after successful empty parse does not resurrect stale in-memory MCP entries");
        }
        finally { TestCleanup.RemoveDirectory(directory); }
    }

}
