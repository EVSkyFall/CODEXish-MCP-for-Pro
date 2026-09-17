using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Codexish.Server;

// Deterministic mount checks: this executable in fixture mode is a real stdio MCP backend behind the real HTTP host.
// No browser, network or ChatGPT is involved.
public static class BrowserTests
{
    public const string LongToolName = "fixture.tool.whose.name.is.long.enough.to.exceed.the.connector.function.name.limit";
    private const string SecretVariable = "CODEXISH_BROWSER_TEST_TOKEN";
    // Built at run time so no credential-shaped literal sits in the source.
    private static string GitHubToken => "ghp_" + new string('b', 36);
    private static string ApiKey => "sk-" + new string('c', 24);

    public sealed class FixtureState(string directory)
    {
        public string Directory { get; } = directory;
        public int Count;
    }

    [McpServerToolType]
    public sealed class FixtureTools(FixtureState state)
    {
        [McpServerTool(Name = "snapshot", ReadOnly = true), Description("Read the test count.")]
        public CallToolResult Snapshot() => new() { Content = [new TextContentBlock { Text = "count=" + state.Count }] };

        [McpServerTool(Name = "edit", ReadOnly = false), Description("Apply one test edit.")]
        public CallToolResult Edit(string text)
        {
            state.Count++;
            File.WriteAllText(Path.Combine(state.Directory, "effect.txt"), text);
            return new() { Content = [new TextContentBlock { Text = "count=" + state.Count }], StructuredContent = JsonSerializer.SerializeToElement(new { count = state.Count }) };
        }

        [McpServerTool(Name = "picture", ReadOnly = true), Description("Return an explicitly synthetic PNG.")]
        public CallToolResult Picture() => new() { Content = [ImageContentBlock.FromBytes(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aOcQAAAAASUVORK5CYII="), "image/png")] };

        [McpServerTool(Name = "error", ReadOnly = false), Description("Return an explicit backend tool error.")]
        public CallToolResult Error() => new() { IsError = true, Content = [new TextContentBlock { Text = "test backend error" }] };

        [McpServerTool(Name = "process", ReadOnly = true), Description("Report this fixture's process id, arguments and environment variable names.")]
        public CallToolResult ProcessInfo() => new()
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                args = Environment.GetCommandLineArgs(),
                environment = Environment.GetEnvironmentVariables().Keys.Cast<string>().ToArray()
            }) }]
        };

        [McpServerTool(Name = LongToolName, ReadOnly = true), Description("A tool whose backend name must be shortened for a connector.")]
        public CallToolResult Long() => new() { Content = [new TextContentBlock { Text = "long" }] };

        [McpServerTool(Name = "resource", ReadOnly = true), Description("Return resource blocks that carry credential-shaped strings.")]
        public CallToolResult Resource() => new()
        {
            Content =
            [
                new EmbeddedResourceBlock { Resource = new TextResourceContents { Uri = "file:///fixture/" + ApiKey, MimeType = "text/plain", Text = "before " + GitHubToken + " after" } },
                new ResourceLinkBlock { Uri = "https://fixture.invalid/" + ApiKey, Name = "link " + GitHubToken, Description = "description " + GitHubToken }
            ]
        };
    }

    // With ignoreEndOfInput the fixture keeps running after its input closes, so shutdown has to terminate it.
    public static async Task<int> Fixture(string directory, bool ignoreEndOfInput = false)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new FixtureState(directory));
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<FixtureTools>();
        using var host = builder.Build();
        await host.RunAsync();
        if (ignoreEndOfInput) await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static async Task<int> Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-browser-test-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "root");
        Directory.CreateDirectory(root);
        int passed = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            Console.WriteLine($"BROWSER PASS {++passed:000} {label}");
        }
        Environment.SetEnvironmentVariable(SecretVariable, "browser-test-" + Guid.NewGuid().ToString("N"));
        int fixturePid = 0, stubbornPid = 0, exiterPid = 0;
        try
        {
            Check(BrowserMounts.ToolName("pw", "browser_click") == "browser_pw_browser_click", "a valid short backend name stays readable under the mount prefix");
            string shortened = BrowserMounts.ToolName("fixture", LongToolName);
            Check(shortened.Length <= BrowserMounts.MaxToolName && Regex.IsMatch(shortened, "^[a-zA-Z0-9_-]+$") && shortened == BrowserMounts.ToolName("fixture", LongToolName),
                "a long dotted backend name is shortened deterministically to a valid name of at most 64 characters");
            Check(BrowserMounts.ToolName("fixture", LongToolName + "_a") != BrowserMounts.ToolName("fixture", LongToolName + "_b"),
                "names that share a shortened prefix stay distinct through the hash suffix");
            Check(BrowserMounts.ToolName("m", "a.b") != BrowserMounts.ToolName("m", "a_b") && Regex.IsMatch(BrowserMounts.ToolName("m", "a.b"), "^[a-zA-Z0-9_-]+$"),
                "a sanitized name cannot collide with a backend name that was already valid");

            string profile = Path.Combine(directory, "state", "browser-profiles", "pw");
            var dedicated = new BrowserMountConfig { Id = "pw", RootId = "test", Command = "node", Args = ["cli.js", "--headless"] };
            Check(BrowserMounts.LaunchArguments(dedicated, profile).SequenceEqual(["cli.js", "--headless", "--user-data-dir", profile]),
                "a dedicated Playwright mount always receives an explicit user data directory");
            var existing = new BrowserMountConfig { Id = "own", RootId = "test", Command = "node", ProfileMode = "existing", Args = ["cli.js", "--user-data-dir={profile_dir}"] };
            Check(BrowserMounts.LaunchArguments(existing, profile).SequenceEqual(["cli.js", "--user-data-dir=" + profile]),
                "profile_mode=existing passes only the configured arguments, with {profile_dir} expanded");
            var custom = new BrowserMountConfig { Id = "cdp", RootId = "test", Kind = "custom", Command = "backend", Args = ["--port", "0"] };
            Check(BrowserMounts.LaunchArguments(custom, profile).SequenceEqual(["--port", "0"]), "a custom backend keeps exactly its configured arguments");
            Check(BrowserMounts.Invalid(new() { Id = "pw", RootId = "test", Command = "node", Args = ["--user-data-dir", "profile"] }, BrowserMounts.NewIdSet()) is not null &&
                BrowserMounts.Invalid(existing, BrowserMounts.NewIdSet()) is null,
                "a profile flag in a dedicated Playwright mount's own arguments is a configuration error; profile_mode=existing accepts it");
            var ids = BrowserMounts.NewIdSet();
            Check(BrowserMounts.Invalid(new() { Id = "pw", RootId = "test", Command = "node" }, ids) is null &&
                BrowserMounts.Invalid(new() { Id = "PW", RootId = "test", Command = "node" }, ids) is { } duplicate && duplicate.Contains("already used", StringComparison.Ordinal),
                "mount ids that differ only in case are duplicates, because they would share one dedicated profile directory");

            var wrapped = BrowserMounts.WrapSchema(JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["$schema"] = "http://json-schema.org/draft-07/schema#", ["type"] = "object",
                ["properties"] = new Dictionary<string, object> { ["item"] = new Dictionary<string, string> { ["$ref"] = "#/$defs/item" } },
                ["$defs"] = new Dictionary<string, object> { ["item"] = new Dictionary<string, string> { ["type"] = "string" } }
            }), readOnly: false);
            var inner = wrapped.GetProperty("properties").GetProperty("arguments");
            Check(inner.GetProperty("properties").GetProperty("item").GetProperty("$ref").GetString() == "#/properties/arguments/$defs/item" &&
                wrapped.GetProperty("$schema").GetString() == "http://json-schema.org/draft-07/schema#" && !inner.TryGetProperty("$schema", out _) &&
                wrapped.GetProperty("required").EnumerateArray().Select(r => r.GetString()).SequenceEqual(["arguments", "invocation_id"]),
                "a wrapped backend schema keeps local references resolvable, declares its dialect once and requires invocation_id for changes");

            var environment = BrowserMounts.ChildEnvironment();
            Check(environment.Keys.Any(k => k.Equals("PATH", StringComparison.OrdinalIgnoreCase)) && !environment.Keys.Any(Redaction.IsSecretName),
                "the backend launch environment keeps PATH and no credential-named variable");

            var config = ServerConfig.Create("", "test-only-password", [("test", root)], [], 0, Path.Combine(directory, "state"));
            string exe = Environment.ProcessPath!;
            var fixtureArgs = new List<string>();
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) fixtureArgs.Add(Assembly.GetExecutingAssembly().Location);
            fixtureArgs.AddRange(["--browser-fixture", root]);
            string missingBackend = Path.Combine(directory, "missing-backend" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            config.BrowserMounts =
            [
                // kind=playwright with the dedicated profile proves the launch really receives the profile directory.
                new() { Id = "fixture", RootId = "test", Kind = "playwright", ProfileMode = "dedicated", Command = exe, Args = [.. fixtureArgs],
                    ReadOnlyTools = ["snapshot", "picture", "process", "resource", LongToolName] },
                new() { Id = "missing", RootId = "test", Kind = "custom", Command = missingBackend },
                new() { Id = "badprofile", RootId = "test", Kind = "playwright", Command = exe, Args = ["--user-data-dir", root] },
                new() { Id = "FIXTURE", RootId = "test", Kind = "custom", Command = exe, Args = [.. fixtureArgs] },
                new() { Id = "exiter", RootId = "test", Kind = "custom", Command = exe, Args = [.. fixtureArgs], ReadOnlyTools = ["snapshot"] },
                new() { Id = "stubborn", RootId = "test", Kind = "custom", Command = exe, Args = [.. fixtureArgs, "--ignore-stdin-eof"] }
            ];
            string effect = Path.Combine(root, "effect.txt");
            var runtime = new CodexishRuntime(config, true);
            bool disposed = false;
            try
            {
                await runtime.Browsers.InitializeAsync();
                string[] mounted = runtime.Browsers.Tools.Select(t => t.ProtocolTool.Name).ToArray();
                Check(mounted.Count(n => n.StartsWith("browser_fixture_", StringComparison.Ordinal)) == 7 && mounted.Length == 21,
                    "a real stdio handshake discovers seven tools on each of the three connected fixture mounts");
                Check(mounted.All(n => n.Length <= BrowserMounts.MaxToolName && Regex.IsMatch(n, "^[a-zA-Z0-9_-]+$")) && mounted.Contains(shortened),
                    "every mounted tool name matches ^[a-zA-Z0-9_-]+$ and is at most 64 characters, including the shortened long name");
                stubbornPid = runtime.Browsers.Resources("stubborn").ProcessId ?? 0;

                await using var app = CodexishHost.Build(runtime, 0);
                await app.StartAsync();
                try
                {
                    string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                    await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(address + "/mcp") }),
                        new() { ProtocolVersion = CodexishRuntime.ProtocolVersion });
                    var listed = await client.ListToolsAsync();
                    Check(listed.Count == CodexishRuntime.ToolNames.Length + 21 && CodexishRuntime.ToolNames.All(n => listed.Any(t => t.Name == n)),
                        "HTTP lists all coding and desktop tools together with the connected mounts' tools while other mounts failed");
                    var empty = new Dictionary<string, JsonElement>();
                    var capabilities = (await client.CallToolAsync(new CallToolRequestParams { Name = "host_capabilities", Arguments = empty })).StructuredContent!.Value.GetProperty("data");
                    var browser = capabilities.GetProperty("browser");
                    var states = browser.GetProperty("mounted").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!, m => m);
                    Check(states["fixture"].GetProperty("state").GetString() == "connected" && states["missing"].GetProperty("state").GetString() == "unavailable" &&
                        states["missing"].GetProperty("error").GetString() is { Length: > 0 },
                        "host_capabilities reports a mount that failed to start with its state and error");
                    Check(states["badprofile"].GetProperty("state").GetString() == "invalid_config" &&
                        states["badprofile"].GetProperty("error").GetString()!.Contains("profile_mode=existing", StringComparison.Ordinal),
                        "a dedicated Playwright mount with its own profile flag is reported as invalid configuration and never started");
                    Check(states["FIXTURE"].GetProperty("state").GetString() == "invalid_config" && states["FIXTURE"].GetProperty("tools").GetArrayLength() == 0,
                        "a mount whose id differs from an earlier one only in case is reported as invalid configuration and never started");
                    Check(capabilities.GetProperty("tools").GetArrayLength() == CodexishRuntime.ToolNames.Length + 21,
                        "host_capabilities lists the mounted tool names beside the base tools");
                    Check(browser.GetProperty("boundary").GetString()!.Contains("not contained by the root or its grants", StringComparison.Ordinal) &&
                        browser.GetProperty("read_only_tools").GetString()!.Contains("bypass the ledger", StringComparison.Ordinal),
                        "host_capabilities states that mounted tools run unconfined and that read_only_tools bypass the ledger by configuration");
                    Check(Reply.CodeOf(await client.CallToolAsync(new CallToolRequestParams { Name = "workspace_info", Arguments = empty })) is null,
                        "coding tools keep working while a configured mount is unavailable");

                    async Task<CallToolResult> Call(string backend, object body, string? id = null, string mount = "fixture")
                    {
                        var arguments = new Dictionary<string, JsonElement> { ["arguments"] = JsonSerializer.SerializeToElement(body) };
                        if (id is not null) arguments["invocation_id"] = JsonSerializer.SerializeToElement(id);
                        return await client.CallToolAsync(new CallToolRequestParams { Name = BrowserMounts.ToolName(mount, backend), Arguments = arguments });
                    }
                    string[] Texts(CallToolResult result) =>
                        result.StructuredContent!.Value.GetProperty("data").GetProperty("text").EnumerateArray().Select(t => t.GetString()!).ToArray();

                    var edit = listed.Single(t => t.Name == BrowserMounts.ToolName("fixture", "edit")).ProtocolTool;
                    Check(edit.InputSchema.GetProperty("required").EnumerateArray().Any(p => p.GetString() == "invocation_id") &&
                        edit.InputSchema.GetProperty("properties").GetProperty("arguments").GetProperty("properties").TryGetProperty("text", out _) &&
                        edit.Annotations?.ReadOnlyHint == false,
                        "a mutating backend tool keeps its input schema inside arguments and requires a ledger invocation_id");
                    var snapshotTool = listed.Single(t => t.Name == BrowserMounts.ToolName("fixture", "snapshot")).ProtocolTool;
                    Check(!snapshotTool.InputSchema.GetProperty("required").EnumerateArray().Any(p => p.GetString() == "invocation_id") &&
                        snapshotTool.Annotations?.ReadOnlyHint == true,
                        "only locally configured read-only tools skip the invocation ledger");

                    var before = await Call("snapshot", new { });
                    Check(Reply.CodeOf(before) is null && Texts(before).SequenceEqual(["count=0"]), "HTTP to stdio returns the backend text inside the envelope");
                    Check(Reply.CodeOf(await Call("edit", new { text = "no id" })) == "INVALID_ARGUMENT" && !File.Exists(effect),
                        "a mutating call without invocation_id never reaches the backend");
                    var first = await Call("edit", new { text = "written through MCP" }, "browser-once");
                    Check(Reply.CodeOf(first) is null && File.ReadAllText(effect) == "written through MCP", "a mounted mutation has a real file effect");
                    var second = await Call("edit", new { text = "written through MCP" }, "browser-once");
                    Check(first.StructuredContent!.Value.GetRawText() == second.StructuredContent!.Value.GetRawText() && Texts(await Call("snapshot", new { })).SequenceEqual(["count=1"]),
                        "an identical retry returns the stored result without replaying the backend effect");
                    Check(Reply.CodeOf(await Call("edit", new { text = "other" }, "browser-once")) == "IDEMPOTENCY_CONFLICT",
                        "changed arguments under the same invocation_id conflict instead of replaying");
                    Check((await Call("picture", new { })).Content.OfType<ImageContentBlock>().Count() == 1, "image content survives the stdio and HTTP hops");
                    var failed = await Call("error", new { }, "browser-error");
                    Check(Reply.CodeOf(failed) == "BROWSER_BACKEND_ERROR" && Texts(failed).SequenceEqual(["test backend error"]), "a backend tool error stays an error with its text");
                    Check(Texts(await Call(LongToolName, new { })).SequenceEqual(["long"]), "the shortened tool name still calls the backend's original tool");

                    var resource = await Call("resource", new { });
                    string delivered = JsonSerializer.Serialize(resource.Content, McpJsonUtilities.DefaultOptions) + resource.StructuredContent?.GetRawText();
                    var embedded = resource.Content.OfType<EmbeddedResourceBlock>().SingleOrDefault();
                    var link = resource.Content.OfType<ResourceLinkBlock>().SingleOrDefault();
                    Check(Reply.CodeOf(resource) is null && !delivered.Contains(GitHubToken, StringComparison.Ordinal) && !delivered.Contains(ApiKey, StringComparison.Ordinal) &&
                        embedded?.Resource is TextResourceContents { Text: "before [REDACTED:github_token] after" } textResource &&
                        textResource.Uri.ToString()!.Contains("[REDACTED:api_key]", StringComparison.Ordinal) &&
                        link is { Name: "link [REDACTED:github_token]", Description: "description [REDACTED:github_token]" } &&
                        link.Uri.ToString()!.Contains("[REDACTED:api_key]", StringComparison.Ordinal),
                        "embedded text resources and resource links from a backend are redacted in their text, URIs, names and descriptions");

                    var info = JsonDocument.Parse(Texts(await Call("process", new { }))[0]).RootElement;
                    fixturePid = info.GetProperty("pid").GetInt32();
                    string[] launched = info.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray();
                    string expectedProfile = Path.Combine(config.StateDir, "browser-profiles", "fixture");
                    Check(launched.SkipWhile(a => a != "--user-data-dir").Skip(1).FirstOrDefault() == expectedProfile && Directory.Exists(expectedProfile) &&
                        states["fixture"].GetProperty("profile_directory").GetString() == expectedProfile,
                        "the dedicated profile directory under state_dir is created, passed to the backend and reported");
                    string[] inherited = info.GetProperty("environment").EnumerateArray().Select(e => e.GetString()!).ToArray();
                    Check(!inherited.Contains(SecretVariable, StringComparer.OrdinalIgnoreCase) && !inherited.Any(Redaction.IsSecretName),
                        "the backend process inherits no credential-named variable from the server");

                    config.Roots[0].Write = false;
                    Check(Reply.CodeOf(await Call("edit", new { text = "denied" }, "browser-denied")) == "PERMISSION_DENIED" && File.ReadAllText(effect) == "written through MCP",
                        "the root write grant is enforced for mutating backend tools");
                    Check(Reply.CodeOf(await Call("snapshot", new { })) is null, "a read-only backend tool remains available without the write grant");
                    config.Roots[0].Shell = false;
                    Check(Reply.CodeOf(await Call("snapshot", new { })) == "PERMISSION_DENIED", "without the shell grant no backend call is sent");
                    config.Roots[0].Write = true;
                    config.Roots[0].Shell = true;

                    // A backend that exits on its own releases its session and job at once.
                    var exiter = runtime.Browsers.Resources("exiter");
                    exiterPid = exiter.ProcessId ?? 0;
                    using (var exiting = Process.GetProcessById(exiterPid)) exiting.Kill(entireProcessTree: true);
                    var exitWatch = Stopwatch.StartNew();
                    while (runtime.Browsers.Resources("exiter").State != "exited" && exitWatch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
                    var exited = runtime.Browsers.Resources("exiter");
                    var afterExit = await Call("snapshot", new { }, mount: "exiter");
                    Check(exiter is { State: "connected", Session: true } && (exiter.Job || !OperatingSystem.IsWindows()) &&
                        exited is { State: "exited", Session: false, Job: false } && Reply.CodeOf(afterExit) == "BROWSER_UNAVAILABLE",
                        "a backend that exits on its own is reported exited, releases its session and job, and its tools answer BROWSER_UNAVAILABLE");
                }
                finally { await app.StopAsync(); }

                // Shutdown must not wait on a backend that ignores the end of its input.
                var disposeWatch = Stopwatch.StartNew();
                var disposal = Task.Run(runtime.Dispose);
                disposed = await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(60))) == disposal;
                Console.WriteLine($"BROWSER NOTE runtime disposal took {disposeWatch.Elapsed.TotalSeconds:0.0} s");
                Check(disposed && stubbornPid != 0 && !Alive(stubbornPid) && runtime.Browsers.Resources("stubborn").State == "stopped" && fixturePid != 0 && !Alive(fixturePid),
                    "disposal terminates a backend that ignores end of input after the bounded grace period and ends every backend process");
            }
            finally
            {
                if (!disposed) runtime.Dispose();
            }

            // JSON null collections and fields: a null browser_mounts or tunnel is "none configured", and malformed
            // entries are reported per mount without throwing out of initialization.
            string malformedState = Path.Combine(directory, "malformed-state");
            string nullSections = Path.Combine(directory, "null-sections.json");
            File.WriteAllText(nullSections, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["state_dir"] = malformedState, ["roots"] = new[] { new { id = "test", path = root } }, ["browser_mounts"] = null, ["tunnel"] = null
            }));
            var nullConfig = ServerConfig.Load(nullSections);
            Check(nullConfig.BrowserMounts.Length == 0 && nullConfig.Tunnel is { Command: "", Args.Length: 0 },
                "a JSON null browser_mounts or tunnel loads as none configured");
            string malformedPath = Path.Combine(directory, "malformed.json");
            File.WriteAllText(malformedPath, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["state_dir"] = malformedState,
                ["roots"] = new[] { new { id = "test", path = root } },
                ["browser_mounts"] = new object?[]
                {
                    null,
                    new Dictionary<string, object?> { ["id"] = null, ["kind"] = "custom", ["command"] = missingBackend, ["root_id"] = "test" },
                    new Dictionary<string, object?> { ["id"] = "nullargs", ["kind"] = "custom", ["command"] = missingBackend, ["root_id"] = "test", ["args"] = null, ["read_only_tools"] = null },
                    new Dictionary<string, object?> { ["id"] = "nullitem", ["kind"] = "custom", ["command"] = missingBackend, ["root_id"] = "test", ["args"] = new string?[] { "x", null } },
                    new Dictionary<string, object?> { ["id"] = "nullkind", ["command"] = missingBackend, ["root_id"] = "test", ["kind"] = null, ["profile_mode"] = null }
                }
            }));
            using (var malformedRuntime = new CodexishRuntime(ServerConfig.Load(malformedPath), true))
            {
                await malformedRuntime.Browsers.InitializeAsync();
                string?[] malformedStates = JsonSerializer.SerializeToElement(malformedRuntime.Browsers.Describe()).GetProperty("mounted").EnumerateArray()
                    .Select(m => m.GetProperty("state").GetString()).ToArray();
                Check(malformedStates.SequenceEqual(["invalid_config", "invalid_config", "unavailable", "invalid_config", "invalid_config"]),
                    "null mount entries, ids, argument items, kinds and profile modes are invalid_config and null lists become empty, without throwing out of initialization");
            }

            Console.WriteLine($"BROWSER_TESTS_PASSED: {passed}; real stdio fixture behind the real HTTP host, not a browser or ChatGPT trial.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine($"BROWSER_TESTS_FAILED after {passed}: {error}"); return 1; }
        finally
        {
            Environment.SetEnvironmentVariable(SecretVariable, null);
            foreach (int pid in new[] { fixturePid, stubbornPid, exiterPid })
                if (pid != 0 && Alive(pid))
                    try { using var leftover = Process.GetProcessById(pid); leftover.Kill(entireProcessTree: true); }
                    catch (Exception) { /* it ended while being stopped */ }
            TestCleanup.RemoveDirectory(directory);
        }
    }
}
