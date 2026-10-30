using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

public static class McpHostResourceTests
{
    private static readonly string Secret = "ghp_" + new string('q', 36);
    public static async Task<int> Fixture(string[] args)
    {
        var values = CommandLine.Values(args, "--mcp-host-resource-fixture");
        string label = values[0];
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var request = JsonNode.Parse(line)!.AsObject();
            if (request["id"] is not { } id) continue;
            string? method = request["method"]?.GetValue<string>();
            if (method == "resources/list" && label.StartsWith("hanging", StringComparison.Ordinal) && !File.Exists("allow-" + label)) continue; // deliberately never answers; still reads cancellations/other requests
            JsonNode? result = null;
            switch (method)
            {
                case "initialize": result = JsonSerializer.SerializeToNode(new { protocolVersion = "2025-11-25", serverInfo = new { name = label, version = "1" }, capabilities = new { tools = new { }, resources = new { } }, instructions = "resource fixture " + label }); break;
                case "tools/list": result = JsonNode.Parse("{\"tools\":[]}"); break;
                case "resources/list": result = JsonSerializer.SerializeToNode(new { resources = new[] { new { uri = "fixture:text", name = label + " " + Secret } } }); break;
                case "resources/templates/list": result = JsonNode.Parse("{\"resourceTemplates\":[]}"); break;
                case "resources/read":
                    string uri = request["params"]!["uri"]!.GetValue<string>();
                    result = uri == "fixture:malformed" ? JsonValue.Create(42) : uri == "fixture:unknown"
                        ? JsonNode.Parse("{\"contents\":[{\"uri\":\"fixture:future\",\"type\":\"future-resource\",\"payload\":\"" + Secret + "\"},{\"uri\":\"fixture:good\",\"text\":\"still present\"}]}")
                        : JsonSerializer.SerializeToNode(new { contents = new object[] { new { uri, mimeType = "text/plain", text = label + ":" + Environment.ProcessId + ":" + Secret }, new { uri = "fixture:blob", mimeType = "application/octet-stream", blob = Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)) } } });
                    break;
                case "ping": result = new JsonObject(); break;
            }
            var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
            if (result is null) answer["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
            else answer["result"] = result;
            await Console.Out.WriteLineAsync(answer.ToJsonString());
            await Console.Out.FlushAsync();
        }
        return 0;
    }
    private static async Task Until(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate()) { if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new InvalidOperationException("resource host fixture did not connect"); await Task.Delay(25); }
    }
    public static async Task Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "codexish-host-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var config = SelfTest.BuildConfig(folder, "fixture-only-password");
            config.Roots = [.. config.Roots, new RootConfig { Id = "denied", Path = folder, Shell = false }];
            string command = Environment.ProcessPath!;
            string[] Args(string label) => Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? [Assembly.GetExecutingAssembly().Location, "--mcp-host-resource-fixture", label] : ["--mcp-host-resource-fixture", label];
            config.BrowserMounts = [
                new() { Id = "foo", RootId = "proj", Kind = "custom", Command = "", Args = Args("invalid") },
                new() { Id = "foo", RootId = "proj", Kind = "custom", Command = command, Args = Args("browser") }
            ];
            config.McpServers = JsonSerializer.SerializeToElement(new Dictionary<string, object> {
                ["browser:foo"] = new { command, args = Args("literal-mcp"), cwd = folder },
                ["reconnect"] = new { command, args = Args("hanging-reconnect"), cwd = folder },
                ["hanging"] = new { command, args = Args("hanging"), cwd = folder },
                ["denied"] = new { command, args = Args("denied"), cwd = folder, root_id = "denied" },
                ["disabled"] = new { command, args = Args("disabled"), cwd = folder, disabled = true }
            });
            config.Save(Path.Combine(folder, "codexish.json"));
            using var runtime = new CodexishRuntime(config, true);
            await using var app = CodexishHost.Build(runtime, 0);
            await app.StartAsync();
            try
            {
                try { await Until(() => runtime.Mounts.ResourceTargets.OfType<McpMount>().Count(m => m.Identity == "browser:foo" && m.State == "connected") == 2); }
                catch { Console.Error.WriteLine(JsonSerializer.Serialize(runtime.Mounts.ResourceTargets.OfType<McpMount>().Select(m => m.Describe()))); throw; }
                string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(address + "/mcp") }), new() { ProtocolVersion = "2025-11-25", InitializationTimeout = Timeout.InfiniteTimeSpan });
                ValueTask<CallToolResult> Call(string name, object args) => client.CallToolAsync(new CallToolRequestParams { Name = name, Arguments = JsonSerializer.SerializeToElement(args).Deserialize<Dictionary<string, JsonElement>>() });
                foreach (var pair in new[] { ("browser:foo", "browser"), ("mcp:browser:foo", "literal-mcp") })
                {
                    var list = await Call("mcp_resource_list", new { server = pair.Item1 });
                    var entry = list.StructuredContent!.Value.GetProperty("data").GetProperty("servers")[0];
                    check(entry.GetProperty("state").GetString() == "ready" && entry.GetProperty("resources")[0].GetProperty("name").GetString()!.StartsWith(pair.Item2) && !entry.GetRawText().Contains(Secret), "real host resource list reaches distinct " + pair.Item1);
                    var read = await Call("mcp_resource_read", new { server = pair.Item1, uri = "fixture:text" });
                    var embedded = read.Content.OfType<EmbeddedResourceBlock>().ToArray();
                    check(embedded[0].Resource is TextResourceContents text && text.Text.StartsWith(pair.Item2 + ":") && !text.Text.Contains(Secret), "real host resource read reaches distinct " + pair.Item1 + " despite invalid browser duplicate");
                    check(embedded[1].Resource is BlobResourceContents blob && blob.DecodedData.Span.SequenceEqual(Encoding.UTF8.GetBytes(Secret)) && blob.Blob.Span.SequenceEqual(Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)))) && !read.StructuredContent!.Value.GetRawText().Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret))) && !read.Content.OfType<TextContentBlock>().Any(t => t.Text.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)))), "real resource blob appears only as embedded resource, not duplicated in envelope");
                }
                check(Reply.CodeOf(await Call("mcp_resource_read", new { server = "denied", uri = "fixture:text" })) == "PERMISSION_DENIED", "real host resource read checks root grants before state");
                var deniedList = await Call("mcp_resource_list", new { server = "denied" });
                check(deniedList.StructuredContent!.Value.GetProperty("data").GetProperty("servers")[0].GetProperty("state").GetString() == "permission_denied", "real host resource list contains grant failure per server");
                var disabled = await Call("mcp_resource_read", new { server = "disabled", uri = "fixture:text" });
                var error = disabled.StructuredContent!.Value.GetProperty("error");
                check(Reply.CodeOf(disabled) == "MCP_UNAVAILABLE" && error.GetProperty("side_effects").GetString() == "none" && error.GetProperty("message").GetString() == "MCP server 'disabled' is disabled in codexish.json; nothing was sent to it and CODEXish does not start it. The local user enables it by removing \"disabled\": true or \"enabled\": false from its entry in codexish.json; a saved change applies within seconds, without restarting CODEXish.", "real host resource read uses exact appendix A.6 disabled text");
                var target = runtime.Mounts.ResourceTargets.OfType<McpMount>().Single(m => !m.IsBrowser && m.Identity == "browser:foo");
                int? pid = target.ProcessId;
                var malformed = await Call("mcp_resource_read", new { server = "mcp:browser:foo", uri = "fixture:malformed" });
                var unknown = await Call("mcp_resource_read", new { server = "mcp:browser:foo", uri = "fixture:unknown" });
                check(Reply.CodeOf(malformed) == "MCP_BACKEND_ERROR" && target.State == "connected" && target.ProcessId == pid, "malformed resource result is backend error without session restart");
                var malformedError = malformed.StructuredContent!.Value.GetProperty("error");
                check(malformedError.GetProperty("message").GetString() == "MCP server 'browser:foo' sent an answer CODEXish could not read; check the state the tool acts on before another action." && malformedError.GetProperty("side_effects").GetString() == "none" && malformedError.GetProperty("details").GetProperty("provider_error").GetString() == "Resource result contents must be an array." && !malformedError.GetProperty("details").TryGetProperty("protocol_error", out _),
                    "unreadable resource answer uses exact A.7 message and provider_error, not protocol_error");
                check(Reply.CodeOf(unknown) is null && unknown.Content.OfType<TextContentBlock>().Any(t => t.Text.Contains("future-resource") && !t.Text.Contains(Secret)) && unknown.Content.OfType<EmbeddedResourceBlock>().Any(b => b.Resource is TextResourceContents t && t.Text == "still present"), "unknown resource block degrades to redacted text while valid blocks survive");
                await Until(() => runtime.Mounts.ResourceTargets.OfType<McpMount>().Any(m => m.Identity == "reconnect" && m.State == "connected"));
                var reconnect = runtime.Mounts.ResourceTargets.OfType<McpMount>().Single(m => m.Identity == "reconnect");
                int previousPid = reconnect.ProcessId!.Value;
                var reconnectWaiting = await Call("mcp_resource_list", new { server = "reconnect", wait_ms = 0 });
                check(reconnectWaiting.StructuredContent!.Value.GetProperty("data").GetProperty("servers")[0].GetProperty("state").GetString() == "waiting", "resource listing belongs to a live session before forced fixture exit");
                File.WriteAllText(Path.Combine(folder, "allow-hanging-reconnect"), "yes");
                using (var child = Process.GetProcessById(previousPid)) child.Kill();
                await Until(() => reconnect.State == "connected" && reconnect.ProcessId is { } nextPid && nextPid != previousPid);
                var afterReconnect = await Call("mcp_resource_list", new { server = "reconnect" });
                check(afterReconnect.StructuredContent!.Value.GetProperty("data").GetProperty("servers")[0].GetProperty("state").GetString() == "ready", "same mount after reconnect does not reuse its previous session's hung listing");
                await Until(() => runtime.Mounts.ResourceTargets.OfType<McpMount>().Any(m => m.Identity == "hanging" && m.State == "connected"));
                var old = runtime.Mounts.ResourceTargets.OfType<McpMount>().Single(m => m.Identity == "hanging");
                var waiting = await Call("mcp_resource_list", new { server = "hanging", wait_ms = 0 });
                check(waiting.StructuredContent!.Value.GetProperty("data").GetProperty("servers")[0].GetProperty("state").GetString() == "waiting", "real host hung resource listing returns waiting");
                var definitions = JsonNode.Parse(config.McpServers!.Value.GetRawText())!;
                definitions["hanging"]!["args"] = JsonSerializer.SerializeToNode(Args("replacement"));
                config.McpServers = JsonSerializer.SerializeToElement(definitions);
                config.Save(Path.Combine(folder, "codexish.json"));
                await Until(() => runtime.Mounts.ResourceTargets.OfType<McpMount>().Any(m => m.Identity == "hanging" && !ReferenceEquals(m, old) && m.State == "connected"));
                var replacedList = await Call("mcp_resource_list", new { server = "hanging" });
                var replaced = replacedList.StructuredContent!.Value.GetProperty("data").GetProperty("servers")[0];
                check(replaced.GetProperty("state").GetString() == "ready" && replaced.GetProperty("resources")[0].GetProperty("name").GetString()!.StartsWith("replacement"), "retiring hung listing never holds drain and new session has a fresh listing");
            }
            finally { await app.StopAsync(); }
        }
        finally { TestCleanup.RemoveDirectory(folder); }
    }
}
