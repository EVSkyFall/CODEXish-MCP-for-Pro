using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol;
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
        bool vanished = false;
        bool hangList = args[index + 3] == "hanglist";
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer(o => { o.ServerInfo = new() { Name = "http-fixture", Version = "1" }; o.ServerInstructions = "Synthetic HTTP fixture instructions."; o.Capabilities = new() { Tools = new() { ListChanged = true } }; })
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
                Tools = new[] { "ping", "slow", "slow_change", "error500", "error500_change", "reset404", "reset404_change", "die", "ping_change", "malformed", "malformed_change", "unknown", "dup_schema", "vanish", "remove_queued" }.Where(name => name != "vanish" || !vanished).Select(name => new Tool { Name = name,
                    Description = name == "dup_schema" ? "" : name, InputSchema = name == "dup_schema" ? JsonDocument.Parse("{\"type\":\"object\",\"type\":\"object\"}").RootElement.Clone() : JsonSerializer.SerializeToElement(new { type = "object" }) }).ToList()
                };
            })
            .WithCallToolHandler(async (request, cancellation) =>
            {
                if (request.Params!.Name == "ping_change") File.WriteAllText(Path.Combine(directory, "changing-ping-received"), "yes");
                if (request.Params!.Name == "remove_queued")
                {
                    vanished = true;
                    await request.Server.SendNotificationAsync("notifications/tools/list_changed", cancellation);
                }
                if (request.Params!.Name is "slow" or "slow_change")
                {
                    File.WriteAllText(Path.Combine(directory, request.Params.Name + "-started"), "yes");
                    while (!File.Exists(Path.Combine(directory, "release-" + request.Params.Name))) await Task.Delay(10, cancellation);
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
                        if (name is "error500" or "error500_change") { context.Response.StatusCode = 500; return; }
                        if (name is "reset404" or "reset404_change") { context.Response.StatusCode = 404; return; }
                        if (name is "malformed" or "malformed_change" or "unknown")
                        {
                            context.Response.ContentType = "application/json";
                            object result = name == "unknown" ? new { content = (object)new object[] { new { type = "text", text = "known" }, new { type = "future-block", value = "fixture-bearer-value" } } }
                                : new { content = (object)"not-an-array" };
                            await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = body.RootElement.GetProperty("id").Clone(), result });
                            return;
                        }
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
        string? oldUrl = Environment.GetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_URL");
        string? oldConfig = Environment.GetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_CONFIG");
        Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_HEADER", "fixture-env-value");
        Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_BEARER", null);
        Process? fixture = null;
        try
        {
            int fixedPort = FixedFixturePort();
            fixture = StartFixture(directory, fixedPort, mode);
            await Until(() => File.Exists(Path.Combine(directory, "address")), "fixture listen");
            string address = File.ReadAllText(Path.Combine(directory, "address"));
            int port = new Uri(address).Port;
            var config = ServerConfig.Create("", "fixture-password", [("test", directory)], [], 0, Path.Combine(directory, "state"));
            config.McpServers = JsonSerializer.SerializeToElement(new Dictionary<string,object> { ["httpfixture"] = new
            {
                cwd = directory, url = "${CODEXISH_HTTP_FIXTURE_URL}", headers = new Dictionary<string,string> { ["X-Fixture-Config"] = "${CODEXISH_HTTP_FIXTURE_CONFIG}", ["Accept"] = "application/json" },
                env_http_headers = new Dictionary<string,string> { ["X-Fixture-Env"] = "CODEXISH_HTTP_FIXTURE_HEADER" },
                bearer_token_env_var = "CODEXISH_HTTP_FIXTURE_BEARER", read_only_tools = new[] { "ping", "slow", "error500", "reset404", "malformed", "unknown", "remove_queued" }
            } });
            using var runtime = new CodexishRuntime(config, true);
            Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_URL", address + "/mcp?v=1");
            Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_CONFIG", "fixture-config-value");
            Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_BEARER", "fixture-bearer-value");
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
            Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_BEARER", null);
            Redaction.RefreshEnvironment();
            File.WriteAllText(Path.Combine(directory, "redaction.txt"), "false 1 application/json fixture-bearer-value");
            var read = await client.CallToolAsync(new CallToolRequestParams { Name = "fs_read", Arguments = new Dictionary<string,JsonElement> {
                ["root_id"] = JsonSerializer.SerializeToElement("test"), ["path"] = JsonSerializer.SerializeToElement("redaction.txt") } });
            string readJson = read.StructuredContent!.Value.GetRawText();
            check(readJson.Contains("false 1 application/json") && !readJson.Contains("fixture-bearer-value"), mode + " header added after runtime registers additive redaction while ordinary query and Accept remain intact through fs_read");
            Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_BEARER", "fixture-bearer-value");
            check(listed.Single(t => t.Name == "mcp_httpfixture_dup_schema").Description!.StartsWith("dup_schema") &&
                Reply.CodeOf(await Call("dup_schema", true)) is null, mode + " duplicate-key backend schema publishes and calls with any-object fallback; empty description uses backend name");
            // A real SDK client is ended independently, then installed under the mount lock so
            // supervision cannot race the actual dispatch method's completed-session check.
            // Reflection is confined to this regression; production has no test gate.
            await using var endedTransport = new HttpClientTransport(new() { Endpoint = new Uri(address + "/mcp") });
            var endedClient = await McpClient.CreateAsync(endedTransport,
                new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = Timeout.InfiniteTimeSpan });
            await endedClient.DisposeAsync();
            var sync = typeof(McpMount).GetField("sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
            lock (sync)
            {
                var session = typeof(McpMount).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
                var field = session.GetType().GetField("Client")!;
                var original = field.GetValue(session);
                try
                {
                    field.SetValue(session, endedClient);
                    var tool = runtime.Mounts.Find("mcp_httpfixture_ping_change")!;
                    var execute = tool.GetType().GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var completedResult = ((Task<CallToolResult>)execute.Invoke(tool, [JsonSerializer.SerializeToElement(new { }), CancellationToken.None])!).GetAwaiter().GetResult();
                    check(endedClient.Completion.IsCompleted && Reply.CodeOf(completedResult) == "MCP_UNAVAILABLE" && Effects(completedResult) == "none" && target.State == "retrying" &&
                        !File.Exists(Path.Combine(directory, "changing-ping-received")), mode + " completed client before changing dispatch returns unavailable none, sends nothing and requests reconnect");
                    check(McpResultDecoder.IsTransportLoss(new InvalidOperationException("synthetic not-connected failure"), endedClient.Completion.IsCompleted),
                        mode + " completed session makes a non-transport exception transport loss by state, not message");
                }
                finally { field.SetValue(session, original); }
            }
            await Until(() => target.State == "connected", "completed-client reconnect");
            check(Reply.CodeOf(await Call("ping")) is null, mode + " completed client reconnect restores calls");
            int attempts = target.Attempts;
            using (var lease = target.AcquireResourceLease())
            {
                var malformed = await Call("malformed");
                var malformedChange = await Call("malformed_change", true);
                using var after = target.AcquireResourceLease();
                check(Reply.CodeOf(malformed) == "MCP_BACKEND_ERROR" && Effects(malformed) == "none" && Reply.CodeOf(malformedChange) == "MCP_BACKEND_ERROR" && Effects(malformedChange) == "unknown" &&
                    ReferenceEquals(lease.Client, after.Client) && target.State == "connected" && target.Attempts == attempts && !fixture.HasExited,
                    mode + " malformed result is per-call backend error with unchanged session and fixture pid");
                const string unreadable = "MCP server 'httpfixture' sent an answer CODEXish could not read; check the state the tool acts on before another action.";
                check(new[] { malformed, malformedChange }.All(result => result.StructuredContent!.Value.GetProperty("error").GetProperty("message").GetString() == unreadable &&
                    result.StructuredContent.Value.GetProperty("error").GetProperty("details").TryGetProperty("provider_error", out _)),
                    mode + " unreadable answers use exact A.7 text and provider_error for read and changing calls");
            }
            var unknownBlock = await Call("unknown");
            var texts = unknownBlock.StructuredContent!.Value.GetProperty("data").GetProperty("text").EnumerateArray().Select(x => x.GetString()).ToArray();
            check(Reply.CodeOf(unknownBlock) is null && texts.Length == 2 && texts[0] == "known" && texts[1]!.Contains("future-block") &&
                texts[1]!.Contains("[REDACTED:environment_value]") && !unknownBlock.StructuredContent.Value.GetRawText().Contains("fixture-bearer-value"),
                mode + " unknown content block degrades to redacted raw text while known blocks survive");
            if (mode == "stateful")
            {
                runtime.Ledger.Pause();
                try
                {
                    var queued = await client.CallToolAsync(new CallToolRequestParams { Name = "mcp_httpfixture_vanish", Arguments = new Dictionary<string, JsonElement> {
                        ["arguments"] = JsonSerializer.SerializeToElement(new { }), ["invocation_id"] = JsonSerializer.SerializeToElement("vanished-queued"), ["wait_ms"] = JsonSerializer.SerializeToElement(0) } });
                    check(Reply.StatusOf(queued) == "queued", "queued disappearing tool is accepted without dispatch");
                    await Call("remove_queued");
                    await Until(() => !target.BackendTools.Any(t => t.Name == "vanish"), "queued tool removal notification");
                }
                finally { runtime.Ledger.Resume(); }
                await Until(() => Reply.StatusOf(runtime.Ledger.Inspect("vanished-queued")) is not ("queued" or "running"), "queued removed tool terminal result");
                var removed = runtime.Ledger.Inspect("vanished-queued");
                check(Reply.CodeOf(removed) == "MCP_UNAVAILABLE" && Effects(removed) == "none", "queued tool removed before dispatch returns unavailable with no effects");
            }
            var survives500 = Call("slow");
            await Until(() => File.Exists(Path.Combine(directory, "slow-started")), "slow call before HTTP 500");
            var failure = await Call("error500");
            check(Reply.CodeOf(failure) == "MCP_BACKEND_ERROR" && target.State == "connected" && target.Attempts == attempts,
                mode + " HTTP 500 is backend error without reconnect");
            check(Effects(failure) == "none" && Effects(await Call("error500_change", true)) == "unknown" && target.Attempts == attempts,
                mode + " HTTP 500 preserves read-only and changing effects without reconnect");
            File.WriteAllText(Path.Combine(directory, "release-slow"), "yes");
            check(Reply.CodeOf(await survives500) is null, mode + " HTTP 500 does not cancel a different in-flight call");
            File.Delete(Path.Combine(directory, "slow-started"));
            File.Delete(Path.Combine(directory, "release-slow"));
            var slow = Call("slow");
            var changingSlow = Call("slow_change", changing: true);
            await Until(() => File.Exists(Path.Combine(directory, "slow-started")) && File.Exists(Path.Combine(directory, "slow_change-started")), "read and changing calls entered backend");
            var reset = await Call("reset404");
            if (mode == "stateless") { File.WriteAllText(Path.Combine(directory, "release-slow"), "yes"); File.WriteAllText(Path.Combine(directory, "release-slow_change"), "yes"); }
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
            var changed404 = await Call("reset404_change", true);
            check(Reply.CodeOf(changed404) == "MCP_UNAVAILABLE" && Effects(changed404) == "none", mode + " actual HTTP 404 changing request proves no effects");
            await Until(() => target.State == "connected", "changing404 reconnect");
            fixture.Kill(entireProcessTree: true); await fixture.WaitForExitAsync(); fixture.Dispose(); fixture = null;
            var downChange = await Call("ping_change", true);
            check(Reply.CodeOf(downChange) == "EXECUTION_UNKNOWN" && Effects(downChange) == "unknown", mode + " changing request hitting a lost connection preserves uncertain effects");
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
            Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_URL", oldUrl);
            Environment.SetEnvironmentVariable("CODEXISH_HTTP_FIXTURE_CONFIG", oldConfig);
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
            config.McpServers = JsonSerializer.SerializeToElement(new { hanging = new { cwd = directory, url = address + "/mcp" } });
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
    private static string? Effects(CallToolResult result) => result.StructuredContent?.GetProperty("error").GetProperty("side_effects").GetString();
    private static int FixedFixturePort()
    {
        // Explicit non-ephemeral fixture ports cannot be consumed by an outbound ephemeral connection on restart.
        while (true)
        {
            int port = Random.Shared.Next(10000, 20000);
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
            try { listener.Start(); return port; }
            catch (System.Net.Sockets.SocketException) { }
            finally { listener.Stop(); }
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
