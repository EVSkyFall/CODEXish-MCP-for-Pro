using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

public static class McpHandshakeIntegrationTests
{
    private const string Instructions = "Initialize-first fixture instructions retained while offline.";
    // Deliberately minimal legacy peer: it exits without answering discovery rather than returning method-not-found.
    public static async Task<int> Fixture(string[] args)
    {
        string directory = CommandLine.Values(args, "--mcp-initialize-first-fixture")[0];
        if (File.Exists(Path.Combine(directory, "offline"))) return 7;
        string? first = await Console.In.ReadLineAsync();
        if (first is null) return 8;
        var request = JsonNode.Parse(first)!.AsObject();
        string? method = request["method"]?.GetValue<string>();
        File.AppendAllText(Path.Combine(directory, "first-methods"), method + "\n");
        if (method != "initialize") return 9;
        async Task Answer(JsonObject input)
        {
            if (input["id"] is not { } id) return;
            JsonNode? result = input["method"]?.GetValue<string>() switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = "2025-11-25", ["serverInfo"] = new JsonObject { ["name"] = "initialize-first", ["version"] = "1" },
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() }, ["instructions"] = Instructions
                },
                "tools/list" => new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "inspect", ["inputSchema"] = new JsonObject { ["type"] = "object" } }) },
                "ping" => new JsonObject(),
                _ => null
            };
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
            if (result is not null) response["result"] = result;
            else response["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
            await Console.Out.WriteLineAsync(response.ToJsonString());
            await Console.Out.FlushAsync();
        }
        await Answer(request);
        while (await Console.In.ReadLineAsync() is { } line) await Answer(JsonNode.Parse(line)!.AsObject());
        return 0;
    }
    private static async Task Until(Func<bool> predicate, string label)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new InvalidOperationException("handshake fixture wait: " + label);
            await Task.Delay(30);
        }
    }
    public static async Task Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "codexish-handshake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string command = Environment.ProcessPath!;
            string[] args = ["--mcp-initialize-first-fixture", folder];
            if (Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) args = [Assembly.GetExecutingAssembly().Location, .. args];
            var config = SelfTest.BuildConfig(folder, "fixture-password");
            config.McpServers = JsonSerializer.SerializeToElement(new { legacy = new { command, args, cwd = folder, read_only_tools = new[] { "inspect" } } });
            string configPath = Path.Combine(folder, "codexish.json");
            config.Save(configPath);
            string manifest;
            using (var runtime = new CodexishRuntime(config, true))
            {
                await using var app = CodexishHost.Build(runtime, 0);
                await app.StartAsync();
                try
                {
                    var mount = (McpMount)runtime.Mounts.ResourceTargets.Single(t => t.Identity == "legacy");
                    await Until(() => mount.State == "connected", "initialize-first fallback connects");
                    var methods = File.ReadAllLines(Path.Combine(folder, "first-methods"));
                    check(methods.SequenceEqual(new[] { "server/discover", "initialize" }), "initialize-first backend exits on discover and connects on exactly the second attempt");
                    check(mount.Handshake.Pinned && mount.Handshake.Remembered && mount.Attempts == 1, "successful fallback pins and remembers initialize mode after one failed attempt");
                    manifest = Directory.GetFiles(Path.Combine(config.StateDir, "mcp-mounts"), "*.manifest.json").Single();
                    var saved = JsonNode.Parse(File.ReadAllText(manifest))!;
                    check(saved["handshake_mode"]?.GetValue<string>() == "initialize_2025-11-25" && saved["instructions"]?.GetValue<string>() == Instructions,
                        "successful fallback manifest records pinned mode and server instructions");
                }
                finally { await app.StopAsync(); }
            }
            // A fresh runtime proves the saved mode, not just the previous in-memory value, is used first.
            using (var runtime = new CodexishRuntime(ServerConfig.Load(configPath), true))
            {
                await using var app = CodexishHost.Build(runtime, 0);
                await app.StartAsync();
                try
                {
                    var mount = (McpMount)runtime.Mounts.ResourceTargets.Single(t => t.Identity == "legacy");
                    await Until(() => mount.State == "connected", "remembered mode reconnects");
                    check(File.ReadAllLines(Path.Combine(folder, "first-methods")).SequenceEqual(new[] { "server/discover", "initialize", "initialize" }),
                        "fresh runtime tries manifest's successful initialize mode first");
                }
                finally { await app.StopAsync(); }
            }
            File.WriteAllText(Path.Combine(folder, "offline"), "yes");
            using (var runtime = new CodexishRuntime(ServerConfig.Load(configPath), true))
            {
                await using var app = CodexishHost.Build(runtime, 0);
                await app.StartAsync();
                try
                {
                    var mount = (McpMount)runtime.Mounts.ResourceTargets.Single(t => t.Identity == "legacy");
                    await Until(() => mount.State == "retrying", "offline backend retries");
                    check(mount.ToolsSource == "manifest" && mount.Instructions == Instructions, "offline backend loads instructions and tools from manifest");
                    string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                    await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(address + "/mcp") }), new() { ProtocolVersion = "2025-11-25", InitializationTimeout = Timeout.InfiniteTimeSpan });
                    check(client.ServerInstructions?.Contains("## legacy\n" + Instructions, StringComparison.Ordinal) == true,
                        "real host initialize returns cached backend instructions while backend is down");
                }
                finally { await app.StopAsync(); }
            }
        }
        finally { TestCleanup.RemoveDirectory(folder); }
    }
}
