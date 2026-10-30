using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

// Hermetic loopback child servers. All headers and environment values below are synthetic fixture data.
public static class McpHttpTests
{
    public static async Task<int> Fixture(string[] args)
    {
        int index = Array.IndexOf(args, "--mcp-http-fixture");
        string directory = args[index + 1]; int port = int.Parse(args[index + 2]);
        bool stateful = args[index + 3] == "stateful";
        bool hangList = args[index + 3] == "hanglist";
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer(o => { o.ServerInfo = new() { Name = "http-fixture", Version = "1" }; o.ServerInstructions = "Synthetic HTTP fixture instructions."; })
            .WithHttpTransport(o => o.SessionMode = stateful ? HttpServerSessionMode.Stateful : HttpServerSessionMode.Stateless)
            .WithListToolsHandler(async (_, cancellation) =>
            {
                if (hangList)
                {
                    File.WriteAllText(Path.Combine(directory, "listing-started"), "yes");
                    await Task.Delay(Timeout.Infinite, cancellation);
                }
                return new ListToolsResult
                {
                Tools = new[] { "ping", "slow", "slow_change", "error500", "reset404", "die" }.Select(name => new Tool { Name = name,
                    Description = name, InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }).ToList()
                };
            })
            .WithCallToolHandler(async (request, cancellation) =>
            {
                if (request.Params!.Name is "slow" or "slow_change")
                {
                    File.WriteAllText(Path.Combine(directory, request.Params.Name + "-started"), "yes");
                    await Task.Delay(1500, cancellation);
                }
                return new CallToolResult { Content = [new TextContentBlock { Text = "http-fixture-ok" }] };
            });
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Method == "POST" && context.Request.Path == "/mcp")
            {
                context.Request.EnableBuffering();
                JsonDocument? body = null;
                try { body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
                catch (JsonException) { }
                context.Request.Body.Position = 0;
                using (body)
                {
                    if (body?.RootElement.TryGetProperty("method", out var method) == true && method.GetString() == "tools/call")
                    {
                        var name = body.RootElement.GetProperty("params").GetProperty("name").GetString();
                        File.WriteAllText(Path.Combine(directory, "headers.json"), JsonSerializer.Serialize(new
                        {
                            configured = context.Request.Headers["X-Fixture-Config"] == "fixture-config-value",
                            environment = context.Request.Headers["X-Fixture-Env"] == "fixture-env-value",
                            bearer = context.Request.Headers.Authorization == "Bearer fixture-bearer-value"
                        }));
                        if (name == "error500") { context.Response.StatusCode = 500; return; }
                        if (name == "reset404") { context.Response.StatusCode = 404; return; }
                        if (name == "die")
                        {
                            File.WriteAllText(Path.Combine(directory, "received-changing-call"), "yes");
                            Environment.Exit(0);
                        }
                    }
                }
            }
            await next(context);
        });
        app.MapMcp("/mcp");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        ServerConfig.WriteAtomically(Path.Combine(directory, "address"), System.Text.Encoding.UTF8.GetBytes(address), null);
        await app.WaitForShutdownAsync();
        return 0;
    }

    public static async Task Run(Action<bool,string> check)
    {
        foreach (string mode in new[] { "stateless", "stateful" }) await RunMode(mode, check);
        await ShutdownDuringList(check);
    }
    private static async Task RunMode(string mode, Action<bool,string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-http-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? oldHeader = Environment.GetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_HEADER");
        string? oldBearer = Environment.GetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_BEARER");
        Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_HEADER", "fixture-env-value");
        Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_BEARER", "fixture-bearer-value");
        Process? fixture = null;
        try
        {
            fixture = StartFixture(directory, 0, mode);
            await Until(() => File.Exists(Path.Combine(directory, "address")), "fixture listen");
            string address = File.ReadAllText(Path.Combine(directory, "address"));
            int port = new Uri(address).Port;
            var config = ServerConfig.Create("", "fixture-password", [("test", directory)], [], 0, Path.Combine(directory, "state"));
            config.McpServers = JsonSerializer.SerializeToElement(new Dictionary<string,object> { ["httpfixture"] = new
            {
                url = address + "/mcp", headers = new Dictionary<string,string> { ["X-Fixture-Config"] = "fixture-config-value" },
                env_http_headers = new Dictionary<string,string> { ["X-Fixture-Env"] = "CODEXISH_HTTP_FIXTURE_HEADER" },
                bearer_token_env_var = "CODEXISH_HTTP_FIXTURE_BEARER", read_only_tools = new[] { "ping", "slow", "error500", "reset404" }
            } });
            using var runtime = new CodexishRuntime(config, true);
            await using var host = CodexishHost.Build(runtime, 0);
            await host.StartAsync();
            string hostAddress = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await runtime.Mounts.InitializeAsync();
            var target = (McpMount)runtime.Mounts.ResourceTargets.Single(x => x.Identity == "httpfixture");
            check(target.State == "connected", mode + " HTTP MCP connects through real mount supervision");
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(hostAddress + "/mcp") }),
                new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = Timeout.InfiniteTimeSpan });
            async Task<CallToolResult> Call(string name, bool changing = false)
            {
                var arguments = new Dictionary<string,JsonElement> { ["arguments"] = JsonSerializer.SerializeToElement(new { }), ["wait_ms"] = JsonSerializer.SerializeToElement(30000) };
                if (changing) arguments["invocation_id"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("N"));
                return await client.CallToolAsync(new CallToolRequestParams { Name = "mcp_httpfixture_" + name, Arguments = arguments });
            }
            var listed = await client.ListToolsAsync();
            check(listed.Any(x => x.Name == "mcp_httpfixture_ping") && Reply.CodeOf(await Call("ping")) is null, mode + " HTTP tools list and call through real CODEXish host");
            var headers = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "headers.json"))).RootElement;
            check(headers.GetProperty("configured").GetBoolean() && headers.GetProperty("environment").GetBoolean() && headers.GetProperty("bearer").GetBoolean(), mode + " configured, environment and bearer headers reach fixture");
            int attempts = target.Attempts;
            var survives500 = Call("slow");
            await Until(() => File.Exists(Path.Combine(directory, "slow-started")), "slow call before HTTP 500");
            var failure = await Call("error500");
            check(Reply.CodeOf(failure) == "MCP_BACKEND_ERROR" && target.State == "connected" && target.Attempts == attempts,
                mode + " HTTP 500 is backend error without reconnect");
            check(Reply.CodeOf(await survives500) is null, mode + " HTTP 500 does not cancel a different in-flight call");
            File.Delete(Path.Combine(directory, "slow-started"));
            var slow = Call("slow");
            var changingSlow = Call("slow_change", changing: true);
            await Until(() => File.Exists(Path.Combine(directory, "slow-started")) && File.Exists(Path.Combine(directory, "slow_change-started")), "read and changing calls entered backend");
            var reset = await Call("reset404");
            var slowResult = await slow;
            var changingResult = await changingSlow;
            check(Reply.CodeOf(reset) == "MCP_UNAVAILABLE", mode + " HTTP 404 triggers unavailable and reconnect");
            if (mode == "stateful")
            {
                const string loss = "The MCP transport did not produce a confirmed result; do not replay an uncertain action.";
                check(Reply.CodeOf(slowResult) == "MCP_UNAVAILABLE" && slowResult.StructuredContent!.Value.GetProperty("error").GetProperty("message").GetString() == loss,
                    "stateful HTTP 404 SDK session expiry makes in-flight read transport-unavailable, not caller-cancelled");
                check(Reply.CodeOf(changingResult) == "EXECUTION_UNKNOWN" && changingResult.StructuredContent!.Value.GetProperty("error").GetProperty("message").GetString() == loss,
                    "stateful HTTP 404 SDK session expiry makes in-flight change unknown, never CANCELLED");
            }
            else
                check(Reply.CodeOf(slowResult) is null && Reply.CodeOf(changingResult) is null,
                    "stateless HTTP 404 has no SDK session to expire and other in-flight calls finish");
            await Until(() => target.State == "connected", "404 reconnect");
            check(Reply.CodeOf(await Call("ping")) is null, mode + " calls succeed after 404 reconnect");
            fixture.Kill(entireProcessTree: true); await fixture.WaitForExitAsync(); fixture.Dispose(); fixture = null;
            var down = await Call("ping");
            check(Reply.CodeOf(down) == "MCP_UNAVAILABLE" && down.StructuredContent!.Value.GetProperty("error").GetProperty("side_effects").GetString() == "none" && target.State == "retrying",
                mode + " stopped HTTP fixture returns nothing-sent unavailable and retries");
            File.Delete(Path.Combine(directory, "address"));
            fixture = StartFixture(directory, port, mode);
            await Until(() => File.Exists(Path.Combine(directory, "address")) && target.State == "connected", "HTTP restart reconnect");
            check(Reply.CodeOf(await Call("ping")) is null, mode + " fixed-port fixture restart reconnects without CODEXish restart");
            var unknown = await Call("die", changing: true);
            check(File.Exists(Path.Combine(directory, "received-changing-call")) && Reply.CodeOf(unknown) == "EXECUTION_UNKNOWN" &&
                unknown.StructuredContent!.Value.GetProperty("error").GetProperty("side_effects").GetString() == "unknown",
                mode + " fixture dies after receiving changing call: EXECUTION_UNKNOWN, never nothing-sent");
            await host.StopAsync();
        }
        finally
        {
            if (fixture is not null) { try { fixture.Kill(entireProcessTree: true); } catch { } fixture.Dispose(); }
            Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_HEADER", oldHeader);
            Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_BEARER", oldBearer);
            try { Directory.Delete(directory, true); } catch { }
        }
    }
    private static async Task ShutdownDuringList(Action<bool,string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-http-hang-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var fixture = StartFixture(directory, 0, "hanglist");
        CodexishRuntime? runtime = null;
        try
        {
            await Until(() => File.Exists(Path.Combine(directory, "address")), "hanging-list fixture listen");
            string address = File.ReadAllText(Path.Combine(directory, "address"));
            var config = ServerConfig.Create("", "fixture-password", [("test", directory)], [], 0, Path.Combine(directory, "state"));
            config.McpServers = JsonSerializer.SerializeToElement(new { hanging = new { url = address + "/mcp" } });
            runtime = new CodexishRuntime(config, true);
            runtime.Mounts.Start();
            await Until(() => File.Exists(Path.Combine(directory, "listing-started")), "initial tools/list entered");
            var disposing = runtime.Mounts.DisposeAsync().AsTask();
            bool ended = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(15))) == disposing;
            check(ended, "shutdown cancels a hung initial tools/list without adding a handshake or call deadline");
            if (ended) await disposing;
        }
        finally
        {
            try { fixture.Kill(entireProcessTree: true); } catch { }
            runtime?.Dispose();
            try { Directory.Delete(directory, true); } catch { }
        }
    }
    private static Process StartFixture(string directory, int port, string mode)
    {
        string executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in new[] { "--mcp-http-fixture", directory, port.ToString(), mode }) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }
    private static async Task Until(Func<bool> predicate, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(45)) throw new Exception("HTTP fixture did not reach " + description);
            await Task.Delay(20);
        }
    }
}
