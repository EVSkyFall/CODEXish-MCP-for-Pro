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
            int captured = 0;
            var reader = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    var capability = JsonSerializer.SerializeToElement(runtime.Capabilities());
                    var mounted = capability.GetProperty("browser").GetProperty("mounted").EnumerateArray()
                        .Concat(capability.GetProperty("mcp").GetProperty("mounted").EnumerateArray()).ToArray();
                    var declared = mounted.SelectMany(entry => entry.GetProperty("tools").EnumerateArray().Select(t => t.GetString()!)).ToArray();
                    var reported = capability.GetProperty("tools").EnumerateArray().Select(t => t.GetString()!).ToArray();
                    if (mounted.Length != 7 || !reported.Order().SequenceEqual(CodexishRuntime.ToolNames.Concat(declared).Order()) || reported.Distinct().Count() != reported.Length)
                        throw new InvalidOperationException("capability generation mixes server declarations and global tool names");
                    Interlocked.Increment(ref captured);
                }
            });
            for (int i = 0; i < 100; i++)
            {
                var replacement = System.Text.Json.Nodes.JsonNode.Parse(baseline.GetRawText())!.AsObject();
                replacement["resource"]!["disabled"] = i % 2 == 0;
                config.McpServers = JsonSerializer.SerializeToElement(replacement);
                reload.Invoke(runtime.Mounts, null);
            }
            await reader;
            check(captured == 200, "manager: 200 capability snapshots during 100 forced reloads retain every server and exactly native plus declared mounted tools");

        }
        finally { TestCleanup.RemoveDirectory(directory); }
        await RapidReplacement(check);
        await PausedReplacement(check);
        RetainedBrowser(check);
        await ReloadRecoversUnexpectedFailure(check);
        await SaveDuringReload(check);
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
            runtime.Mounts.Start();
            check(await Until(() => ((McpMount)runtime.Mounts.ResourceTargets.Single()).State == "connected"), "manager: rapid-replacement fixture A connects");
            var original = (McpMount)runtime.Mounts.ResourceTargets.Single();
            int originalPid = original.ProcessId!.Value;
            using var lease = original.AcquireResourceLease();
            var call = lease.Client!.CallToolAsync(new CallToolRequestParams { Name = "slow", Arguments = new Dictionary<string, JsonElement>() });
            check(await Until(() => File.Exists(Path.Combine(directory, "slow-started"))), "manager: old instance has a dispatched slow call before reload");
            var reload = typeof(McpMounts).GetMethod("ReadDefinitionsAndApply", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Save("B"); reload.Invoke(runtime.Mounts, null);
            var middle = (McpMount)runtime.Mounts.ResourceTargets.Single();
            Save("C"); reload.Invoke(runtime.Mounts, null);
            var latest = (McpMount)runtime.Mounts.ResourceTargets.Single();
            await Task.Delay(200);
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
                typeof(McpMounts).GetMethod("ReadDefinitionsAndApply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime.Mounts, null);
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

    private static void RetainedBrowser(Action<bool, string> check)
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

}
