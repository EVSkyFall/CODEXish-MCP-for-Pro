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
        string inspectTool = CommandLine.Values(args, "--fixture-tool").FirstOrDefault() ?? "inspect";
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
                    Tools = [new Tool { Name = inspectTool, Description = "Inspect synthetic fixture values.", InputSchema = schema },
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
                        pid = Environment.ProcessId, backend_tool = request.Params.Name, args = Environment.GetCommandLineArgs(), cwd = Environment.CurrentDirectory,
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
    private static bool IsCmd(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
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
        string expandedCwd = Path.Combine(root, "expanded-cwd"); Directory.CreateDirectory(expandedCwd);
        stable["cwd"] = "${CODEXISH_MCP_CWD}";
        stable["env_vars"] = new JsonArray("CODEXISH_MCP_EXPLICIT_SECRET");
        stable["env"] = new JsonObject { ["EXPLICIT_VALUE"] = "${CODEXISH_MCP_EXPANSION}", ["EMPTY_VALUE"] = "", ["NULL_VALUE"] = null, ["HTTP_PROXY"] = "synthetic-proxy" };
        string? oldSecret = Environment.GetEnvironmentVariable("CODEXISH_MCP_EXPLICIT_SECRET"), oldValue = Environment.GetEnvironmentVariable("CODEXISH_MCP_EXPANSION"), oldReload = Environment.GetEnvironmentVariable("CODEXISH_MCP_RELOAD_ARGS");
        var extraEnvironment = new Dictionary<string, string?>
        {
            ["CODEXISH_MCP_CWD"] = Environment.GetEnvironmentVariable("CODEXISH_MCP_CWD"),
            ["CODEXISH_MCP_UNNAMED_SECRET"] = Environment.GetEnvironmentVariable("CODEXISH_MCP_UNNAMED_SECRET"),
            ["CLAUDE_PROJECT_DIR"] = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR"),
            ["CODEXISH_MCP_PROCESS_ONLY"] = Environment.GetEnvironmentVariable("CODEXISH_MCP_PROCESS_ONLY")
        };
        Environment.SetEnvironmentVariable("CODEXISH_MCP_CWD", expandedCwd);
        Environment.SetEnvironmentVariable("CODEXISH_MCP_UNNAMED_SECRET", "synthetic-unnamed-secret");
        Environment.SetEnvironmentVariable("CLAUDE_PROJECT_DIR", null);
        Environment.SetEnvironmentVariable("CODEXISH_MCP_RELOAD_ARGS", "before-save");
        Environment.SetEnvironmentVariable("CODEXISH_MCP_EXPLICIT_SECRET", "synthetic-local-mcp-secret"); Environment.SetEnvironmentVariable("CODEXISH_MCP_EXPANSION", "expanded-value");
        try
        {
            var projectDefinition = Definition(); projectDefinition["cwd"] = "${CLAUDE_PROJECT_DIR}/expanded-cwd";
            projectDefinition["env"] = new JsonObject { ["PROJECT_DIRECTORY"] = "${CLAUDE_PROJECT_DIR}" };
            projectDefinition["args"]!.AsArray().Add("${CLAUDE_PROJECT_DIR}");
            File.WriteAllText(Path.Combine(root, ".mcp.json"), new JsonObject { ["mcpServers"] = new JsonObject { ["project"] = projectDefinition, ["override"] = Definition(), ["collision"] = Definition() } }.ToJsonString());
            string[] instructionFiles = ["AGENTS.override.md", "AGENTS.md", "CLAUDE.md", "CLAUDE.local.md", ".claude/CLAUDE.md"];
            Directory.CreateDirectory(Path.Combine(root, ".claude"));
            foreach (string file in instructionFiles) File.WriteAllText(Path.Combine(root, file), "fixture instructions");
            File.WriteAllText(Path.Combine(denied, ".mcp.json"), new JsonObject { ["mcpServers"] = new JsonObject { ["locked"] = Definition() } }.ToJsonString());
            var config = ServerConfig.Create("", "fixture-password", [("root", root), ("denied", denied), ("missing", Path.Combine(directory, "missing-root"))], [], 0, Path.Combine(directory, "state"));
            config.Roots.Single(r => r.Id == "denied").Shell = false;
            config.McpServers = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["broken"] = new JsonObject { ["command"] = new JsonObject { ["nested"] = true } },
                ["stable"] = stable, ["envreload"] = Definition(), ["override"] = Definition("root"), ["root_collision"] = Definition(), ["disabled"] = new JsonObject { ["command"] = executable, ["disabled"] = true }
            });
            var initialServers = JsonNode.Parse(config.McpServers!.Value.GetRawText())!.AsObject();
            initialServers["envreload"]!["args"]!.AsArray().Add("${CODEXISH_MCP_RELOAD_ARGS}");
            config.McpServers = JsonSerializer.SerializeToElement(initialServers);
            // Deliberately collide browser prefixes and a mounted tool with native mcp_resource_list.
            string firstBrowserCwd = Path.Combine(root, "browser-first"), secondBrowserCwd = Path.Combine(root, "browser-second"), nativeCollisionCwd = Path.Combine(root, "native-collision");
            Directory.CreateDirectory(firstBrowserCwd); Directory.CreateDirectory(secondBrowserCwd); Directory.CreateDirectory(nativeCollisionCwd);
            config.Roots = [..config.Roots, new RootConfig { Id = "browser-first", Path = firstBrowserCwd }, new RootConfig { Id = "browser-second", Path = secondBrowserCwd }];
            config.BrowserMounts = [
                new() { Id = "a_b", RootId = "browser-first", Kind = "custom", Command = executable, Args = [..fixtureArgs, "--fixture-tool", "c"], ReadOnlyTools = ["c"] },
                new() { Id = "a", RootId = "browser-second", Kind = "custom", Command = executable, Args = [..fixtureArgs, "--fixture-tool", "b_c"], ReadOnlyTools = ["b_c"] }
            ];
            var collisionServers = JsonNode.Parse(config.McpServers!.Value.GetRawText())!.AsObject();
            var nativeCollision = Definition("root"); nativeCollision["cwd"] = nativeCollisionCwd;
            nativeCollision["args"] = JsonSerializer.SerializeToNode(fixtureArgs.Concat(new[] { "--fixture-tool", "list" }).ToArray());
            nativeCollision["read_only_tools"] = new JsonArray("list"); collisionServers["resource"] = nativeCollision;
            config.McpServers = JsonSerializer.SerializeToElement(collisionServers);
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
                check(await AwaitState(() => HasState("stable", "connected") && HasState("root/project", "connected")), "MCP good stdio and root project servers connect beside invalid definitions");
                check(State("broken").GetProperty("state").GetString() == "invalid_config" && State("disabled").GetProperty("state").GetString() == "disabled" && State("denied/locked").GetProperty("state").GetString() == "needs_shell_grant" && State("root/override").GetProperty("state").GetString() == "overridden", "MCP invalid disabled shell-grant and overridden states stay isolated");
                var workspace = await client.CallToolAsync(new CallToolRequestParams { Name = "workspace_info" });
                var workspaceRoots = Data(workspace).GetProperty("roots").EnumerateArray().ToArray();
                var files = workspaceRoots.Single(r => r.GetProperty("id").GetString() == "root").GetProperty("instruction_files").EnumerateArray().ToArray();
                check(files.Select(f => f.GetProperty("path").GetString()).SequenceEqual(instructionFiles) && files.All(f => f.GetProperty("size").GetInt64() == new FileInfo(Path.Combine(root, f.GetProperty("path").GetString()!)).Length), "workspace_info lists all five instruction files with relative path and size through real host");
                check(workspaceRoots.Single(r => r.GetProperty("id").GetString() == "missing").GetProperty("instruction_files").GetArrayLength() == 0, "workspace_info reports no instruction files for a missing root");
                var names = (await client.ListToolsAsync()).Select(t => t.Name).ToArray();
                check(names.Contains("mcp_stable_inspect") && names.Contains("mcp_root_project_inspect") && !names.Contains("mcp_root_override_inspect") && !names.Contains("mcp_denied_locked_inspect"), "MCP only startable entries publish correctly namespaced tools");
                check(await AwaitState(() => State("root/collision").GetProperty("state").GetString() == "connected" && State("root_collision").GetProperty("state").GetString() == "connected"), "MCP prefix-ambiguous top-level and project servers both connect");
                string hashed = State("root/collision").GetProperty("tools").EnumerateArray().Select(t => t.GetString()!).Single(n => n.Contains("inspect"));
                check(hashed != "mcp_root_collision_inspect" && hashed.Length <= 64 && Reply.CodeOf(await Call(hashed, new { })) is null && Reply.CodeOf(await Call("mcp_root_collision_inspect", new { })) is null, "MCP prefix collision preserves earlier plain name and hashes later tool with both callable");
                string browserHash = McpMounts.HashedName("browser", "a", "b_c"), nativeHash = McpMounts.HashedName("mcp", "resource", "list");
                check(await AwaitState(() => runtime.Mounts.Tools.Any(t => t.ProtocolTool.Name == "browser_a_b_c") && runtime.Mounts.Tools.Any(t => t.ProtocolTool.Name == browserHash) && runtime.Mounts.Tools.Any(t => t.ProtocolTool.Name == nativeHash)), "MCP browser and native collision fixtures publish every routed tool");
                var browserFirst = FixtureData(await Call("browser_a_b_c", new { }));
                var browserSecond = FixtureData(await Call(browserHash, new { }));
                check(browserFirst.GetProperty("backend_tool").GetString() == "c" && browserSecond.GetProperty("backend_tool").GetString() == "b_c" &&
                    browserFirst.GetProperty("cwd").GetString() == firstBrowserCwd && browserSecond.GetProperty("cwd").GetString() == secondBrowserCwd &&
                    browserFirst.GetProperty("pid").GetInt32() == runtime.Browsers.Resources("a_b").ProcessId && browserSecond.GetProperty("pid").GetInt32() == runtime.Browsers.Resources("a").ProcessId &&
                    browserFirst.GetProperty("pid").GetInt32() != browserSecond.GetProperty("pid").GetInt32(), "Browser plain and hashed collision names route to distinct expected backend names, directories and process identities");
                var mountedNativeCollision = FixtureData(await Call(nativeHash, new { }));
                var nativeList = await client.CallToolAsync(new CallToolRequestParams { Name = "mcp_resource_list", Arguments = new Dictionary<string, JsonElement> { ["server"] = JsonSerializer.SerializeToElement("resource") } });
                check(mountedNativeCollision.GetProperty("backend_tool").GetString() == "list" && mountedNativeCollision.GetProperty("cwd").GetString() == nativeCollisionCwd &&
                    mountedNativeCollision.GetProperty("pid").GetInt32() == runtime.Mounts.ResourceTargets.OfType<McpMount>().Single(m => m.Identity == "resource").ProcessId &&
                    Reply.CodeOf(nativeList) is null && Data(nativeList).GetProperty("servers")[0].GetProperty("server").GetString() == "resource", "Native resource-list remains native while its hashed mounted collision routes to the expected backend process and directory");
                var result = FixtureData(await Call("mcp_stable_inspect", new { names = new[] { "CODEXISH_MCP_EXPLICIT_SECRET", "EXPLICIT_VALUE", "EMPTY_VALUE", "NULL_VALUE", "HTTP_PROXY" } }));
                var values = result.GetProperty("values");
                check(values.GetProperty("EXPLICIT_VALUE").GetString() == "expanded-value" && values.GetProperty("EMPTY_VALUE").GetString() == "" && values.GetProperty("NULL_VALUE").ValueKind == JsonValueKind.Null && values.GetProperty("HTTP_PROXY").GetString() == "synthetic-proxy", "MCP real stdio child receives expanded explicit empty null and proxy values");
                check(result.GetProperty("names").EnumerateArray().Any(n => n.GetString() == "CODEXISH_MCP_EXPLICIT_SECRET") && !values.GetProperty("CODEXISH_MCP_EXPLICIT_SECRET").GetString()!.Contains("synthetic-local-mcp-secret"), "MCP explicit secret reaches child and its echoed value is redacted");
                var childNames = result.GetProperty("names").EnumerateArray().Select(n => n.GetString()).ToArray();
                check(childNames.Contains("PATH", StringComparer.OrdinalIgnoreCase) && !childNames.Contains("CODEXISH_MCP_UNNAMED_SECRET"), "MCP real stdio child receives allowlisted PATH but not an unnamed secret variable");
                check(result.GetProperty("cwd").GetString() == expandedCwd, "MCP real stdio cwd uses expanded configuration value");
                var projectData = FixtureData(await Call("mcp_root_project_inspect", new { names = new[] { "PROJECT_DIRECTORY" } }));
                check(projectData.GetProperty("cwd").GetString() == expandedCwd && projectData.GetProperty("values").GetProperty("PROJECT_DIRECTORY").GetString() == root && projectData.GetProperty("args").EnumerateArray().Last().GetString() == root, "MCP real project server expands CLAUDE_PROJECT_DIR in cwd env and args");
                var capabilities = await client.CallToolAsync(new CallToolRequestParams { Name = "host_capabilities" });
                check(Data(capabilities).GetProperty("mcp").GetProperty("mounted").EnumerateArray().Single(m => m.GetProperty("identity").GetString() == "stable").GetProperty("instructions").GetString() == FixtureInstructions, "host_capabilities reports mounted server instructions through real host");
                Environment.SetEnvironmentVariable("CODEXISH_MCP_PROCESS_ONLY", "process-only-after-runtime");
                string shellExecutable = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
                string[] shellArgs = OperatingSystem.IsWindows() ? ["/d", "/c", "echo %CODEXISH_MCP_PROCESS_ONLY%"] : ["-c", "printf '%s' \"$CODEXISH_MCP_PROCESS_ONLY\""];
                var shellResult = await client.CallToolAsync(new CallToolRequestParams { Name = "shell_run", Arguments = new Dictionary<string, JsonElement>
                {
                    ["root_id"] = JsonSerializer.SerializeToElement("root"), ["invocation_id"] = JsonSerializer.SerializeToElement("process-only-environment"),
                    ["executable"] = JsonSerializer.SerializeToElement(shellExecutable), ["args"] = JsonSerializer.SerializeToElement(shellArgs), ["wait_ms"] = JsonSerializer.SerializeToElement(30000)
                } });
                check(Reply.CodeOf(shellResult) is null && Data(shellResult).GetProperty("stdout_preview").GetString()!.Trim() == "process-only-after-runtime", "shell_run real child sees a variable set only in the running server process");
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
                    check(shimMount.ProcessId is { } cmdPid && cmdPid != shimData.GetProperty("pid").GetInt32() && IsCmd(cmdPid), "MCP Windows shim reports the cmd supervisor process id");
                    check(await AwaitState(() => HasState("invalidshim", "invalid_config")), "MCP Windows batch CR argument is invalid_config");
                    var invalidProcess = await new CodexishTools(runtime).ProcessStart("root", "invalid-batch-argument", executable: shim, args: ["bad\nargument"], wait_ms: 10000);
                    check(Reply.CodeOf(invalidProcess) == "INVALID_ARGUMENT", "MCP process_start rejects an unrepresentable batch argument");
                }
                else Console.WriteLine("SKIP MCP Windows shim argument round trip and process_start batch validation: not Windows");
                int stablePid = await Pid("mcp_stable_inspect");
                var node = Current(); node["mcp_servers"]!["added"] = Definition(); Save(node);
                check(await AwaitState(() => runtime.Mounts.Tools.Any(t => t.ProtocolTool.Name == "mcp_added_inspect")), "MCP reload starts newly added server");
                check(await Pid("mcp_stable_inspect") == stablePid, "MCP reload leaves unchanged server process alive");
                int addedPid = await Pid("mcp_added_inspect");
                check(await AwaitState(() => State("envreload").GetProperty("state").GetString() == "connected"), "MCP variable-dependent reload fixture connects");
                int envPid = await Pid("mcp_envreload_inspect");
                Environment.SetEnvironmentVariable("CODEXISH_MCP_RELOAD_ARGS", "after-save");
                await Task.Delay(3500); // exceed a reload polling interval without changing a file
                check(await Pid("mcp_envreload_inspect") == envPid, "MCP changing an environment variable alone does not restart a server");
                string identicalText = File.ReadAllText(path); File.WriteAllText(path, identicalText);
                check(await AwaitState(() => State("envreload").GetProperty("state").GetString() == "connected" && runtime.Mounts.ResourceTargets.OfType<McpMount>().Single(m => m.Identity == "envreload").ProcessId != envPid), "MCP identical-text save detects changed effective args");
                check(await AwaitState(() => State("envreload").GetProperty("state").GetString() == "connected") && await Pid("mcp_envreload_inspect") != envPid && await Pid("mcp_stable_inspect") == stablePid, "MCP identical save restarts only variable-dependent server, preserving unrelated process");
                check(FixtureData(await Call("mcp_envreload_inspect", new { })).GetProperty("args").EnumerateArray().Last().GetString() == "after-save", "MCP replacement receives the newly expanded argument");
                int literalPid = await Pid("mcp_envreload_inspect");
                node = Current(); var literalArgs = node["mcp_servers"]!["envreload"]!["args"]!.AsArray(); literalArgs[literalArgs.Count - 1] = "after-save"; Save(node);
                check(await AwaitState(() => State("envreload").GetProperty("args").EnumerateArray().Last().GetString() == "after-save") && await Pid("mcp_envreload_inspect") == literalPid,
                    "MCP equivalent variable-to-literal save updates displayed raw args without restarting the current process");
                Environment.SetEnvironmentVariable("CODEXISH_MCP_RELOAD_ARGS", "must-not-revive-old-expression");
                using (var ended = Process.GetProcessById(literalPid)) ended.Kill(entireProcessTree: true);
                check(await AwaitState(() => State("envreload").GetProperty("state").GetString() == "connected" && runtime.Mounts.ResourceTargets.OfType<McpMount>().Single(m => m.Identity == "envreload").ProcessId != literalPid) &&
                    FixtureData(await Call("mcp_envreload_inspect", new { })).GetProperty("args").EnumerateArray().Last().GetString() == "after-save",
                    "MCP natural retry launches the latest equivalent literal definition rather than an obsolete environment expression");
                Environment.SetEnvironmentVariable("CODEXISH_MCP_RELOAD_ARGS", "after-save");
                node = Current(); var restoredArgs = node["mcp_servers"]!["envreload"]!["args"]!.AsArray(); restoredArgs[restoredArgs.Count - 1] = "${CODEXISH_MCP_RELOAD_ARGS}"; Save(node);
                check(await AwaitState(() => State("envreload").GetProperty("args").EnumerateArray().Last().GetString() == "${CODEXISH_MCP_RELOAD_ARGS}"),
                    "MCP subsequent equivalent save restores variable spelling for later environment-reload checks");
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
                check(await AwaitState(() => !Alive(addedPid)), "MCP removing a configured server ends its process");
                int projectOverridePid = await Pid("mcp_root_override_inspect");
                node = Current(); node["mcp_servers"]!["override"] = Definition("root"); Save(node);
                check(await AwaitState(() => State("root/override").GetProperty("state").GetString() == "overridden" && runtime.Mounts.Tools.Any(t => t.ProtocolTool.Name == "mcp_override_inspect") && !Alive(projectOverridePid)), "MCP adding override at runtime stops project instance and starts configured replacement");
                node = Current(); node["browser_mounts"] = JsonSerializer.SerializeToNode(new[] { new BrowserMountConfig { Id = "reloadbrowser", RootId = "root", Kind = "custom", Command = executable, Args = fixtureArgs, ReadOnlyTools = ["inspect"] } }); Save(node);
                check(await AwaitState(() => runtime.Mounts.Tools.Any(t => t.ProtocolTool.Name == "browser_reloadbrowser_inspect")), "Browser mount added at runtime publishes callable tool");
                int browserPid = await Pid("browser_reloadbrowser_inspect");
                node = Current(); node["browser_mounts"]![0]!["args"]!.AsArray().Add("--browser-reloaded"); Save(node);
                check(await AwaitState(() => runtime.Browsers.Resources("reloadbrowser").State == "connected" && runtime.Browsers.Resources("reloadbrowser").ProcessId != browserPid && !Alive(browserPid)), "Browser mount definition change restarts only its backend");
                check(FixtureData(await Call("browser_reloadbrowser_inspect", new { })).GetProperty("args").EnumerateArray().Last().GetString() == "--browser-reloaded", "Reloaded browser mount runs changed arguments through real host");
                int replacementBrowserPid = await Pid("browser_reloadbrowser_inspect");
                node = Current(); node["browser_mounts"] = new JsonArray(); Save(node);
                check(await AwaitState(() => !runtime.Mounts.Tools.Any(t => t.ProtocolTool.Name == "browser_reloadbrowser_inspect") && !Alive(replacementBrowserPid)), "Browser mount removal unlists tools and ends backend process");
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
            foreach (var (name, value) in extraEnvironment) Environment.SetEnvironmentVariable(name, value);
            TestCleanup.RemoveDirectory(directory);
        }
    }
}
