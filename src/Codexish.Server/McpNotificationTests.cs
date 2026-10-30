using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Codexish.Server;

public static class McpNotificationTests
{
    private sealed class FixtureState(string directory, bool emptyAcknowledgement = false)
    {
        private int lists, listens;
        public Func<CancellationToken, Task>? Notify;
        public McpServerOptions Options()
        {
            return new()
            {
                ServerInfo = new() { Name = "notification-fixture", Version = "1" },
                Capabilities = new() { Tools = new() { ListChanged = true }, Resources = new() { ListChanged = true } },
                Handlers = new()
                {
                    ListToolsHandler = async (request, token) =>
                    {
                        Notify ??= async ct => await request.Server.SendNotificationAsync("notifications/tools/list_changed", ct);
                        int count = Interlocked.Increment(ref lists);
                        File.WriteAllText(Path.Combine(directory, "lists"), count.ToString());
                        if (File.Exists(Path.Combine(directory, "hold")))
                        {
                            File.WriteAllText(Path.Combine(directory, "entered"), "yes");
                            while (File.Exists(Path.Combine(directory, "hold"))) await Task.Delay(10, token);
                        }
                        var tools = new List<Tool> { new() { Name = "original", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) } };
                        if (File.Exists(Path.Combine(directory, "added"))) tools.Add(new() { Name = "added", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) });
                        return new ListToolsResult { Tools = tools };
                    },
                    SubscriptionsListenHandler = async (request, token) =>
                    {
                        File.WriteAllText(Path.Combine(directory, "listens"), Interlocked.Increment(ref listens).ToString());
                        JsonObject Meta() => new() { [MetaKeys.SubscriptionId] = JsonSerializer.SerializeToNode(request.JsonRpcRequest.Id, McpJsonUtilities.DefaultOptions) };
                        await request.Server.SendMessageAsync(new JsonRpcNotification
                        {
                            Method = NotificationMethods.SubscriptionsAcknowledgedNotification,
                            Params = new JsonObject { ["notifications"] = emptyAcknowledgement ? new JsonObject() : new JsonObject { ["toolsListChanged"] = true, ["resourcesListChanged"] = true }, ["_meta"] = Meta() }
                        }, token);
                        if (emptyAcknowledgement) return new EmptyResult();
                        Notify = async ct => await request.Server.SendMessageAsync(new JsonRpcNotification
                        {
                            Method = "notifications/tools/list_changed", Params = new JsonObject { ["_meta"] = Meta() }
                        }, ct);
                        File.WriteAllText(Path.Combine(directory, "subscribed"), "yes");
                        await Task.Delay(Timeout.Infinite, token);
                        return new EmptyResult();
                    }
                }
            };
        }
        public async Task Watch(CancellationToken token)
        {
            while (!File.Exists(Path.Combine(directory, "burst"))) await Task.Delay(10, token);
            File.WriteAllText(Path.Combine(directory, "added"), "yes");
            while (Notify is null) await Task.Delay(10, token);
            await Notify(token);
            while (!File.Exists(Path.Combine(directory, "entered"))) await Task.Delay(10, token);
            await Notify(token);
            await Notify(token);
            File.WriteAllText(Path.Combine(directory, "burst-sent"), "yes");
        }
    }

    public static async Task<int> Fixture(string[] args)
    {
        string directory = CommandLine.Values(args, "--mcp-notification-fixture")[0];
        var state = new FixtureState(directory);
        await using var server = McpServer.Create(new StreamServerTransport(Console.OpenStandardInput(), Console.OpenStandardOutput()), state.Options());
        state.Notify = async token => await server.SendNotificationAsync("notifications/tools/list_changed", token);
        using var stopping = new CancellationTokenSource();
        var watch = state.Watch(stopping.Token);
        try { await server.RunAsync(stopping.Token); }
        finally { stopping.Cancel(); try { await watch; } catch (OperationCanceledException) { } }
        return 0;
    }

    private static async Task Until(Func<bool> predicate, string label)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new InvalidOperationException("notification fixture wait: " + label);
            await Task.Delay(20);
        }
    }
    private static int Count(string folder, string name)
    {
        try { return int.TryParse(File.ReadAllText(Path.Combine(folder, name)), out var n) ? n : 0; }
        catch (IOException) { return 0; }
    }
    public static async Task Run(Action<bool, string> check)
    {
        foreach (bool pinned in new[] { true, false }) await Stdio(check, pinned);
        await Http(check, false);
        await Http(check, true);
    }
    private static async Task Stdio(Action<bool, string> check, bool pinned)
    {
        string folder = Path.Combine(Path.GetTempPath(), "codexish-notify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var runtime = new CodexishRuntime(SelfTest.BuildConfig(folder, "fixture-password"));
            var definition = new McpDefinition { Name = "notify", Identity = "notify", Cwd = folder, Command = Environment.ProcessPath!, Args = ["--mcp-notification-fixture", folder] };
            if (Path.GetFileNameWithoutExtension(definition.Command).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) definition.Args = [Assembly.GetExecutingAssembly().Location, .. definition.Args];
            await using var mount = CreateMount(runtime, definition);
            mount.Handshake.Pinned = pinned;
            mount.Start();
            await mount.Settled.WaitAsync(TimeSpan.FromSeconds(30));
            if (mount.State != "connected") Console.Error.WriteLine("Notification fixture diagnostics: " + JsonSerializer.Serialize(mount.Describe()));
            check(mount.State == "connected", $"stdio notification fixture connects ({(pinned ? "2025-11-25" : "discover")})");
            if (!pinned) await Until(() => File.Exists(Path.Combine(folder, "subscribed")) && Count(folder, "lists") >= 2, "subscription acknowledgement relist");
            await Burst(check, mount, folder, pinned ? "stdio pinned" : "stdio subscribed");
        }
        finally { TestCleanup.RemoveDirectory(folder); }
    }
    private static async Task Http(Action<bool, string> check, bool stateless)
    {
        string folder = Path.Combine(Path.GetTempPath(), "codexish-http-notify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var state = new FixtureState(folder, stateless);
        var options = state.Options();
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddMcpServer(o => { o.ServerInfo = options.ServerInfo; o.Capabilities = options.Capabilities; o.Handlers = options.Handlers; })
            .WithHttpTransport(o => o.SessionMode = stateless ? HttpServerSessionMode.Stateless : HttpServerSessionMode.Stateful);
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        using var stopping = new CancellationTokenSource();
        var watcher = stateless ? Task.CompletedTask : state.Watch(stopping.Token);
        try
        {
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var runtime = new CodexishRuntime(SelfTest.BuildConfig(folder, "fixture-password"));
            var definition = new McpDefinition { Name = "notify", Identity = "notify", Cwd = folder, Transport = "http", Url = address + "/mcp" };
            await using var mount = CreateMount(runtime, definition);
            mount.Handshake.Pinned = !stateless;
            mount.Start();
            await mount.Settled.WaitAsync(TimeSpan.FromSeconds(30));
            check(mount.State == "connected", $"HTTP notification fixture connects ({(stateless ? "stateless" : "stateful")})");
            if (stateless)
            {
                await Until(() => Count(folder, "listens") == 1, "empty acknowledgement");
                // The production resubscription backoff starts at one second. This observes beyond it.
                await Task.Delay(1300);
                check(Count(folder, "listens") == 1, "stateless empty subscription acknowledgement is not re-sent");
            }
            else
            {
                await Burst(check, mount, folder, "stateful HTTP");
            }
        }
        finally
        {
            stopping.Cancel();
            try { await watcher; } catch (OperationCanceledException) { }
            await app.StopAsync();
            TestCleanup.RemoveDirectory(folder);
        }
    }
    private static McpMount CreateMount(CodexishRuntime runtime, McpDefinition definition)
    {
        McpMount? mount = null;
        mount = new McpMount(runtime, definition, () =>
        {
            if (mount is not null) mount.Publish(mount.BackendTools.Select(tool => (tool, "mcp_notify_" + tool.Name)));
        });
        return mount;
    }
    private static async Task Burst(Action<bool, string> check, McpMount mount, string folder, string kind)
    {
        int before = Count(folder, "lists");
        int notificationsBefore = mount.ToolNotificationCount;
        File.WriteAllText(Path.Combine(folder, "hold"), "yes");
        File.WriteAllText(Path.Combine(folder, "burst"), "yes");
        await Until(() => File.Exists(Path.Combine(folder, "burst-sent")), "three notifications sent");
        // The first list stays blocked until all three notifications have actually reached the client.
        await Until(() => mount.ToolNotificationCount == notificationsBefore + 3, "all three notifications observed by mount");
        File.Delete(Path.Combine(folder, "hold"));
        await Until(() => Count(folder, "lists") >= before + 2 && mount.BackendTools.Any(t => t.Name == "added"), "coalesced second relist");
        await Until(() => !mount.ToolsRelisting, "coalesced relisting completed");
        check(Count(folder, "lists") == before + 2, $"{kind}: three notifications during a slow list coalesce to two relists");
        check(mount.Tools.Any(t => t.ProtocolTool.Name == "mcp_notify_added"), $"{kind}: new backend tool is published after list_changed");
        string manifest = Directory.GetFiles(Path.Combine(folder, "state", "mcp-mounts"), "*.manifest.json").Single();
        check(File.ReadAllText(manifest).Contains("\"added\""), $"{kind}: refreshed tools are saved in the manifest");
    }
}
