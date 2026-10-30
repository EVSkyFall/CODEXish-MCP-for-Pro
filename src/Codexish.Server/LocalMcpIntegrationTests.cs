using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Codexish.Server;

public static class LocalMcpIntegrationTests
{
    public const string FixtureInstructions = "Synthetic local MCP fixture instructions.";
    public static async Task<int> Fixture(string[] args)
    {
        string directory = CommandLine.Values(args, "--local-mcp-fixture")[0];
        if (CommandLine.Values(args, "--launch-log").FirstOrDefault() is { } launchLog)
            File.AppendAllText(launchLog, JsonSerializer.Serialize(new { pid = Environment.ProcessId, args, commandline = string.Join(' ', args) }) + "\n");
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { names = new { type = "array", items = new { type = "string" } } } });
        await using var server = McpServer.Create(new StreamServerTransport(Console.OpenStandardInput(), Console.OpenStandardOutput()), new McpServerOptions
        {
            ServerInfo = new() { Name = "local-mcp-fixture", Version = "1" }, ServerInstructions = FixtureInstructions,
            Capabilities = new() { Tools = new() },
            Handlers = new()
            {
                ListToolsHandler = (request, cancellation) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools = [new Tool { Name = "inspect", Description = "Inspect synthetic fixture values.", InputSchema = schema },
                        new Tool { Name = "slow", Description = "Wait for the fixture release file.", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }]
                }),
                CallToolHandler = async (request, cancellation) =>
                {
                    if (request.Params!.Name == "slow")
                    {
                        File.WriteAllText(Path.Combine(directory, "slow-started"), Environment.ProcessId.ToString());
                        while (!File.Exists(Path.Combine(directory, "slow-release"))) await Task.Delay(20, cancellation);
                    }
                    var names = request.Params.Arguments is { } input && input.TryGetValue("names", out var selected)
                        ? selected.EnumerateArray().Select(n => n.GetString()!).ToArray() : [];
                    return new CallToolResult { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new
                    {
                        pid = Environment.ProcessId, args = Environment.GetCommandLineArgs(),
                        values = names.ToDictionary(name => name, Environment.GetEnvironmentVariable),
                        names = Environment.GetEnvironmentVariables().Keys.Cast<string>().ToArray()
                    }) }] };
                }
            }
        });
        await server.RunAsync();
        return 0;
    }

    private static async Task<bool> Until(Func<bool> predicate, int seconds = 45, CodexishRuntime? runtime = null)
    {
        void Diagnose()
        {
            if (runtime is null) return;
            Console.Error.WriteLine("MCP integration wait failed: " + Redaction.Apply(JsonSerializer.Serialize(new
            {
                mounts = runtime.Mounts.Describe(),
                tools = runtime.Mounts.Tools.Select(t => t.ProtocolTool.Name).ToArray()
            })).Text);
        }
        var watch = Stopwatch.StartNew();
        try
        {
            while (!predicate())
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) { Diagnose(); return false; }
                await Task.Delay(50);
            }
            return true;
        }
        catch { Diagnose(); throw; }
    }
    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; }
    }
    private static JsonElement Data(CallToolResult result) => result.StructuredContent!.Value.GetProperty("data");
    private static JsonElement FixtureData(CallToolResult result) => JsonDocument.Parse(Data(result).GetProperty("text")[0].GetString()!).RootElement.Clone();

    public static async Task Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-local-mcp-test-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "root"), denied = Path.Combine(directory, "denied");
        Directory.CreateDirectory(root); Directory.CreateDirectory(denied);
        string executable = Environment.ProcessPath!;
        string[] fixtureArgs = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Assembly.GetExecutingAssembly().Location, "--local-mcp-fixture", root] : ["--local-mcp-fixture", root];
        JsonObject Definition(string? rootId = null) => new()
        {
            ["command"] = executable, ["args"] = JsonSerializer.SerializeToNode(fixtureArgs),
            ["read_only_tools"] = new JsonArray("inspect", "slow"), ["root_id"] = rootId
        };
        var stable = Definition("root");
        stable["env_vars"] = new JsonArray("CODEXISH_MCP_EXPLICIT_SECRET");
        stable["env"] = new JsonObject { ["EXPLICIT_VALUE"] = "${CODEXISH_MCP_EXPANSION}", ["EMPTY_VALUE"] = "", ["NULL_VALUE"] = null, ["HTTP_PROXY"] = "synthetic-proxy" };
        string? oldSecret = Environment.GetEnvironmentVariable("CODEXISH_MCP_EXPLICIT_SECRET"), oldValue = Environment.GetEnvironmentVariable("CODEXISH_MCP_EXPANSION"), oldReload = Environment.GetEnvironmentVariable("CODEXISH_MCP_RELOAD_ARGS");
        Environment.SetEnvironmentVariable("CODEXISH_MCP_RELOAD_ARGS", "before-save");
        Environment.SetEnvironmentVariable("CODEXISH_MCP_EXPLICIT_SECRET", "synthetic-local-mcp-secret"); Environment.SetEnvironmentVariable("CODEXISH_MCP_EXPANSION", "expanded-value");
        try
        {
            File.WriteAllText(Path.Combine(root, ".mcp.json"), new JsonObject { ["mcpServers"] = new JsonObject { ["project"] = Definition(), ["override"] = Definition(), ["collision"] = Definition() } }.ToJsonString());
            File.WriteAllText(Path.Combine(denied, ".mcp.json"), new JsonObject { ["mcpServers"] = new JsonObject { ["locked"] = Definition() } }.ToJsonString());
            var config = ServerConfig.Create("", "fixture-password", [("root", root), ("denied", denied)], [], 0, Path.Combine(directory, "state"));
            config.Roots.Single(r => r.Id == "denied").Shell = false;
            config.McpServers = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["broken"] = new JsonObject { ["command"] = new JsonObject { ["nested"] = true } },
                ["stable"] = stable, ["envreload"] = Definition(), ["override"] = Definition("root"), ["root_collision"] = Definition(), ["disabled"] = new JsonObject { ["command"] = executable, ["disabled"] = true }
            });
            var initialServers = JsonNode.Parse(config.McpServers!.Value.GetRawText())!.AsObject();
            initialServers["envreload"]!["args"]!.AsArray().Add("${CODEXISH_MCP_RELOAD_ARGS}");
            config.McpServers = JsonSerializer.SerializeToElement(initialServers);
            string path = Path.Combine(directory, "codexish.json"); config.Save(path);
            var loaded = ServerConfig.Load(path);
            check(!Directory.EnumerateFiles(directory, "*.broken*").Any() && loaded.McpServers is not null, "MCP nested config error does not trigger configuration recovery");
            using var runtime = new CodexishRuntime(loaded, true);
            await using var app = CodexishHost.Build(runtime, 0);
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            async Task<McpClient> Client(bool instructions = true) => await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(address + "/mcp") }), new() { ProtocolVersion = CodexishRuntime.ProtocolVersion });
            await using var client = await Client();
            JsonElement State(string identity) => JsonSerializer.SerializeToElement(runtime.Mounts.Describe()).GetProperty("mounted").EnumerateArray().Single(s => s.GetProperty("identity").GetString() == identity);
            // Newly saved definitions do not exist in the published snapshot until the reload loop observes them.
            bool HasState(string identity, string state)
            {
                var entry = JsonSerializer.SerializeToElement(runtime.Mounts.Describe()).GetProperty("mounted")
                    .EnumerateArray().SingleOrDefault(s => s.GetProperty("identity").GetString() == identity);
                return entry.ValueKind != JsonValueKind.Undefined && entry.GetProperty("state").GetString() == state;
            }
            Task<CallToolResult> Call(string tool, object input) => client.CallToolAsync(new CallToolRequestParams { Name = tool, Arguments = new Dictionary<string, JsonElement> { ["arguments"] = JsonSerializer.SerializeToElement(input) } }).AsTask();
            async Task<int> Pid(string tool) => FixtureData(await Call(tool, new { })).GetProperty("pid").GetInt32();
            void Save(JsonObject node) { string next = path + ".test-next"; File.WriteAllText(next, node.ToJsonString()); File.Move(next, path, true); }
            JsonObject Current() => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Task<bool> AwaitState(Func<bool> predicate, int seconds = 45) => Until(predicate, seconds, runtime);
            try
            {
                check(await AwaitState(() => State("stable").GetProperty("state").GetString() == "connected" && State("root/project").GetProperty("state").GetString() == "connected"), "MCP good stdio and root project servers connect beside invalid definitions");
                check(State("broken").GetProperty("state").GetString() == "invalid_config" && State("disabled").GetProperty("state").GetString() == "disabled" && State("denied/locked").GetProperty("state").GetString() == "needs_shell_grant" && State("root/override").GetProperty("state").GetString() == "overridden", "MCP invalid disabled shell-grant and overridden states stay isolated");
                var names = (await client.ListToolsAsync()).Select(t => t.Name).ToArray();
                check(names.Contains("mcp_stable_inspect") && names.Contains("mcp_root_project_inspect") && !names.Contains("mcp_root_override_inspect") && !names.Contains("mcp_denied_locked_inspect"), "MCP only startable entries publish correctly namespaced tools");
                check(await AwaitState(() => State("root/collision").GetProperty("state").GetString() == "connected" && State("root_collision").GetProperty("state").GetString() == "connected"), "MCP prefix-ambiguous top-level and project servers both connect");
                string hashed = State("root/collision").GetProperty("tools").EnumerateArray().Select(t => t.GetString()!).Single(n => n.Contains("inspect"));
                check(hashed != "mcp_root_collision_inspect" && hashed.Length <= 64 && Reply.CodeOf(await Call(hashed, new { })) is null && Reply.CodeOf(await Call("mcp_root_collision_inspect", new { })) is null, "MCP prefix collision preserves earlier plain name and hashes later tool with both callable");
                var result = FixtureData(await Call("mcp_stable_inspect", new { names = new[] { "CODEXISH_MCP_EXPLICIT_SECRET", "EXPLICIT_VALUE", "EMPTY_VALUE", "NULL_VALUE", "HTTP_PROXY" } }));
                var values = result.GetProperty("values");
                check(values.GetProperty("EXPLICIT_VALUE").GetString() == "expanded-value" && values.GetProperty("EMPTY_VALUE").GetString() == "" && values.GetProperty("NULL_VALUE").ValueKind == JsonValueKind.Null && values.GetProperty("HTTP_PROXY").GetString() == "synthetic-proxy", "MCP real stdio child receives expanded explicit empty null and proxy values");
                check(result.GetProperty("names").EnumerateArray().Any(n => n.GetString() == "CODEXISH_MCP_EXPLICIT_SECRET") && !values.GetProperty("CODEXISH_MCP_EXPLICIT_SECRET").GetString()!.Contains("synthetic-local-mcp-secret"), "MCP explicit secret reaches child and its echoed value is redacted");
                await using (var initialized = await Client()) check(initialized.ServerInstructions?.Contains(FixtureInstructions) == true, "MCP backend instructions appear in real host initialization");
                check(Directory.EnumerateFiles(Path.Combine(config.StateDir, "mcp-mounts"), "*.manifest.json").Any(f => File.ReadAllText(f).Contains(FixtureInstructions)), "MCP backend instructions are preserved in manifests");
                check(!HasState("shim", "connected") && !HasState("invalidshim", "invalid_config"),
                    "MCP reload polling treats an unpublished new server as pending, not an exception");
                if (OperatingSystem.IsWindows())
                {
                    string shimDirectory = Path.Combine(directory, "shim path"); Directory.CreateDirectory(shimDirectory);
                    string shim = Path.Combine(shimDirectory, "mcpfixture.cmd");
                    File.WriteAllText(shim, "@echo off\r\n" + string.Join(' ', new[] { executable }.Concat(fixtureArgs).Select(a => "\"" + a + "\"")) + " %*\r\n");
                    string[] special = ["with space", "^", "&", "|", "<", ">", "(", ")", "%", "!", "a\"b", "trailing\\"];
                    var shimNode = Current();
                    shimNode["mcp_servers"]!["shim"] = new JsonObject { ["command"] = "mcpfixture", ["args"] = JsonSerializer.SerializeToNode(special), ["root_id"] = "root", ["read_only_tools"] = new JsonArray("inspect"), ["env"] = new JsonObject { ["PATH"] = shimDirectory + ";${PATH}" } };
                    shimNode["mcp_servers"]!["invalidshim"] = new JsonObject { ["command"] = shim, ["args"] = new JsonArray("invalid\rargument") };
                    Save(shimNode);
                    check(await AwaitState(() => HasState("shim", "connected")), "MCP Windows bare command finds cmd shim on explicitly expanded PATH");
                    var shimData = FixtureData(await Call("mcp_shim_inspect", new { }));
                    check(shimData.GetProperty("args").EnumerateArray().Select(a => a.GetString()).TakeLast(special.Length).SequenceEqual(special) && !File.Exists(Path.Combine(root, "(")) && !File.Exists(Path.Combine(root, ")")), "MCP Windows shim delivers spaces and every cmd metacharacter literally without stray files");
                    var shimMount = runtime.Mounts.ResourceTargets.OfType<McpMount>().Single(m => m.Identity == "shim");
                    check(shimMount.ProcessId is { } cmdPid && cmdPid != shimData.GetProperty("pid").GetInt32(), "MCP Windows shim reports the cmd supervisor process id");
                    check(await AwaitState(() => HasState("invalidshim", "invalid_config")), "MCP Windows batch CR argument is invalid_config");
                    var invalidProcess = await new CodexishTools(runtime).ProcessStart("root", "invalid-batch-argument", executable: shim, args: ["bad\nargument"], wait_ms: 10000);
                    check(Reply.CodeOf(invalidProcess) == "INVALID_ARGUMENT", "MCP process_start rejects an unrepresentable batch argument");
                }
                else Console.WriteLine("SKIP MCP Windows shim argument round trip and process_start batch validation: not Windows");
                int stablePid = await Pid("mcp_stable_inspect");
                var node = Current(); node["mcp_servers"]!["added"] = Definition(); Save(node);
                check(await AwaitState(() => runtime.Mounts.Tools.Any(t => t.ProtocolTool.Name == "mcp_added_inspect")), "MCP reload starts newly added server");
                check(await Pid("mcp_stable_inspect") == stablePid, "MCP reload leaves unchanged server process alive");
                check(await AwaitState(() => State("envreload").GetProperty("state").GetString() == "connected"), "MCP variable-dependent reload fixture connects");
                int envPid = await Pid("mcp_envreload_inspect");
                Environment.SetEnvironmentVariable("CODEXISH_MCP_RELOAD_ARGS", "after-save");
                await Task.Delay(3500); // exceed a reload polling interval without changing a file
                check(await Pid("mcp_envreload_inspect") == envPid, "MCP changing an environment variable alone does not restart a server");
                string identicalText = File.ReadAllText(path); File.WriteAllText(path, identicalText);
                check(await AwaitState(() => State("envreload").GetProperty("state").GetString() == "connected" && runtime.Mounts.ResourceTargets.OfType<McpMount>().Single(m => m.Identity == "envreload").ProcessId != envPid), "MCP identical-text save detects changed effective args");
                check(await AwaitState(() => State("envreload").GetProperty("state").GetString() == "connected") && await Pid("mcp_envreload_inspect") != envPid && await Pid("mcp_stable_inspect") == stablePid, "MCP identical save restarts only variable-dependent server, preserving unrelated process");
                check(FixtureData(await Call("mcp_envreload_inspect", new { })).GetProperty("args").EnumerateArray().Last().GetString() == "after-save", "MCP replacement receives the newly expanded argument");
                var slow = Call("mcp_stable_slow", new { });
                check(await AwaitState(() => File.Exists(Path.Combine(root, "slow-started"))), "MCP slow call dispatched to old instance");
                node = Current(); node["mcp_servers"]!["stable"]!["args"]!.AsArray().Add("--changed-definition"); Save(node);
                check(await AwaitState(() => State("stable").GetProperty("state").GetString() is "starting" or "draining"), "MCP changed server enters draining or replacement-starting state");
                check(!slow.IsCompleted && Alive(stablePid), "MCP definition change does not cancel already dispatched read call");
                File.WriteAllText(Path.Combine(root, "slow-release"), "release");
                check(FixtureData(await slow).GetProperty("pid").GetInt32() == stablePid, "MCP old instance completes the slow call before stopping");
                check(await AwaitState(() => State("stable").GetProperty("state").GetString() == "connected") && await Pid("mcp_stable_inspect") != stablePid && !Alive(stablePid), "MCP replacement starts only after old process stops");
                int newPid = await Pid("mcp_stable_inspect");
                int envBeforeMalformed = await Pid("mcp_envreload_inspect");
                Environment.SetEnvironmentVariable("CODEXISH_MCP_RELOAD_ARGS", "must-wait-for-valid-save");
                string complete = File.ReadAllText(path); File.WriteAllText(path, "{\"mcp_servers\":");
                check(await AwaitState(() => JsonSerializer.SerializeToElement(runtime.Mounts.Describe()).GetProperty("reload_error").ValueKind == JsonValueKind.String), "MCP half-written main configuration reports reload error");
                check(await Pid("mcp_stable_inspect") == newPid && !Directory.EnumerateFiles(directory, "*.broken*").Any(), "MCP half-write keeps running process and performs no recovery writes");
                check(await Pid("mcp_envreload_inspect") == envBeforeMalformed, "MCP malformed save cannot apply changed environment to retained definitions");
                File.WriteAllText(path, complete);
                node = Current(); node["mcp_servers"]!.AsObject().Remove("added"); node["mcp_servers"]!.AsObject().Remove("override"); Save(node);
                check(await AwaitState(() => !runtime.Mounts.Tools.Any(t => t.ProtocolTool.Name == "mcp_added_inspect") && runtime.Mounts.Tools.Any(t => t.ProtocolTool.Name == "mcp_root_override_inspect")), "MCP removal unlists server and removing override starts project entry");
                File.WriteAllText(Path.Combine(root, ".mcp.json"), "{");
                check(await AwaitState(() => JsonSerializer.SerializeToElement(runtime.Mounts.Describe()).GetProperty("project_files").EnumerateArray().Any(f => f.GetProperty("root_id").GetString() == "root" && f.GetProperty("state").GetString() == "unparseable")), "MCP malformed project file is reported distinctly");
                check(Reply.CodeOf(await Call("mcp_root_project_inspect", new { })) is null, "MCP malformed project file keeps previously running servers");
            }
            finally { File.WriteAllText(Path.Combine(root, "slow-release"), "release"); await app.StopAsync(); }
            runtime.Dispose();
            // A tray restart may hand the runtime a stale in-memory configuration. The on-disk mount entries win.
            File.Delete(Path.Combine(root, ".mcp.json"));
            var stale = ServerConfig.Load(path);
            var saved = Current(); saved["mcp_servers"] = new JsonObject { ["freshdisk"] = Definition("root") }; Save(saved);
            using (var rebuilt = new CodexishRuntime(stale, true))
            {
                await using var freshApp = CodexishHost.Build(rebuilt, 0, instructions: false);
                await freshApp.StartAsync();
                try
                {
                    check(await Until(() => rebuilt.Mounts.Tools.Any(t => t.ProtocolTool.Name == "mcp_freshdisk_inspect"), runtime: rebuilt) && !rebuilt.Mounts.Tools.Any(t => t.ProtocolTool.Name == "mcp_stable_inspect"), "MCP stale runtime config reloads current definitions from disk at start");
                    string freshAddress = freshApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                    await using var noInstructions = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(freshAddress + "/mcp") }), new() { ProtocolVersion = CodexishRuntime.ProtocolVersion });
                    check(string.IsNullOrEmpty(noInstructions.ServerInstructions), "MCP --no-instructions omits native and mounted instructions from real host");
                    check(JsonSerializer.SerializeToElement(rebuilt.Mounts.Describe()).GetProperty("project_files").EnumerateArray().Any(f => f.GetProperty("root_id").GetString() == "root" && f.GetProperty("state").GetString() == "absent"), "MCP missing project configuration reports absent");
                }
                finally { await freshApp.StopAsync(); }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXISH_MCP_EXPLICIT_SECRET", oldSecret); Environment.SetEnvironmentVariable("CODEXISH_MCP_EXPANSION", oldValue); Environment.SetEnvironmentVariable("CODEXISH_MCP_RELOAD_ARGS", oldReload);
            TestCleanup.RemoveDirectory(directory);
        }
    }
}
