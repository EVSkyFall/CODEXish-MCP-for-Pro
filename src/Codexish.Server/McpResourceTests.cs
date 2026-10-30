using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Codexish.Server;

// Hermetic SDK sessions in two pipes, never the user's configured MCP servers or state.
public static class McpResourceTests
{
    private sealed class Target(string identity, McpClient? client, string state = "connected", bool denied = false, bool isBrowser = false) : IMcpResourceTarget
    {
        public string Identity => identity;
        public bool IsBrowser => isBrowser;
        public int Acquired;
        public int Active;
        public McpResourceLease AcquireResourceLease()
        {
            if (denied) throw new CodexishFault("PERMISSION_DENIED", "fixture grant missing");
            Interlocked.Increment(ref Acquired);
            Interlocked.Increment(ref Active);
            return new(client, state, null, () => Interlocked.Decrement(ref Active));
        }
        public CallToolResult Unavailable() => Reply.Error("MCP_UNAVAILABLE", $"MCP server '{identity}' is {state}; nothing was sent to it. CODEXish keeps restarting it.", details: new { server = identity, state, next_retry = (string?)null });
    }

    public static async Task Run(Action<bool, string> check)
    {
        int pages = 0;
        await McpResources.Pages(cursor => { pages++; return Task.FromResult<string?>("same"); });
        check(pages == 2, "resource pagination stops on repeated cursors");
        string secret = "ghp_" + new string('r', 36);
        var cleaned = McpResources.Clean(new { uri = "fixture:" + secret, nested = new { text = secret, blob = secret }, array = new[] { secret } });
        check(!cleaned.GetProperty("uri").GetString()!.Contains(secret) && cleaned.GetProperty("nested").GetProperty("text").GetString() != secret && cleaned.GetProperty("array")[0].GetString() != secret, "resource URI, nested metadata and array strings are redacted");
        check(cleaned.GetProperty("nested").GetProperty("blob").GetString() == secret, "resource blobs remain byte-for-byte intact");
        var down = new Target("down", null, "retrying");
        var notStarted = new Target("pending", null, "not_started");
        var denied = new Target("denied", null, "retrying", true);
        var unavailable = new McpResources(() => [down, notStarted, denied]);
        var list = await unavailable.List();
        var entries = list.StructuredContent!.Value.GetProperty("data").GetProperty("servers");
        check(Reply.CodeOf(list) is null && entries.GetArrayLength() == 3 && entries[0].GetProperty("state").GetString() == "retrying" && entries[1].GetProperty("state").GetString() == "not_started" && entries[2].GetProperty("state").GetString() == "permission_denied", "resource listing isolates down, not-started and missing-grant servers");
        check(Reply.CodeOf(await unavailable.List("typo")) == "NOT_FOUND" && Reply.CodeOf(await unavailable.Read("typo", "fixture:a")) == "NOT_FOUND", "unknown resource server is NOT_FOUND for list and read");
        check(Reply.CodeOf(await unavailable.Read("DENIED", "fixture:a")) == "PERMISSION_DENIED" && Reply.CodeOf(await unavailable.Read("DOWN", "fixture:a")) == "MCP_UNAVAILABLE", "resource read checks grants and resolves server case-insensitively");
        check(down.Active == 0 && notStarted.Active == 0, "resource leases release after unavailable calls");

        var inbound = new Pipe();
        var outbound = new Pipe();
        using var shutdown = new CancellationTokenSource();
        var allowList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int listCalls = 0;
        var callerReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = McpServer.Create(new StreamServerTransport(inbound.Reader.AsStream(), outbound.Writer.AsStream()), new McpServerOptions
        {
            ServerInfo = new() { Name = "resource-test", Version = "1" },
            Capabilities = new() { Resources = new() },
            Handlers = new()
            {
                ListResourcesHandler = async (request, cancellation) =>
                {
                    Interlocked.Increment(ref listCalls);
                    await allowList.Task.WaitAsync(cancellation);
                    return new ListResourcesResult
                    {
                        Resources = [new Resource { Uri = "fixture:" + secret, Name = "text " + secret, MimeType = "text/plain" }],
                        NextCursor = "repeat"
                    };
                },
                ListResourceTemplatesHandler = (request, cancellation) => ValueTask.FromResult(new ListResourceTemplatesResult
                {
                    ResourceTemplates = [new ResourceTemplate { UriTemplate = "fixture:{name}", Name = "template", Description = secret, MimeType = "text/plain" }]
                }),
                ReadResourceHandler = async (request, cancellation) =>
                {
                    if (request.Params!.Uri == "fixture:missing") throw new McpProtocolException("fixture resource does not exist", McpErrorCode.InvalidParams);
                    if (request.Params.Uri == "fixture:caller-cancel") { callerReadStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellation); }
                    if (request.Params.Uri == "fixture:transport-loss") { transportReadStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellation); }
                    return new ReadResourceResult
                    {
                        Contents = [new TextResourceContents { Uri = request.Params.Uri, MimeType = "text/plain", Text = secret }, new BlobResourceContents { Uri = "fixture:blob", MimeType = "application/octet-stream", Blob = System.Text.Encoding.UTF8.GetBytes(secret) }]
                    };
                }
            }
        });
        var running = server.RunAsync(shutdown.Token);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(inbound.Writer.AsStream(), outbound.Reader.AsStream()), new McpClientOptions
        {
            ClientInfo = new() { Name = "resource-test-client", Version = "1" },
            ProtocolVersion = "2025-11-25", InitializationTimeout = Timeout.InfiniteTimeSpan
        });
        var connected = new Target("browser:fixture", client, isBrowser: true);
        var mounted = new McpResources(() => [connected, down]);
        try
        {
            var waiting = await mounted.List("BROWSER:FIXTURE", 0);
            check(waiting.StructuredContent!.Value.GetProperty("data").GetProperty("servers")[0].GetProperty("state").GetString() == "waiting", "slow resource list returns waiting without cancelling backend");
            var joined = mounted.List("browser:fixture", 10000);
            allowList.TrySetResult();
            var ready = await joined;
            var entry = ready.StructuredContent!.Value.GetProperty("data").GetProperty("servers")[0];
            check(entry.GetProperty("state").GetString() == "ready" && entry.GetProperty("resources").GetArrayLength() == 2 && listCalls == 2, "resource list reuses one in-flight listing and pages until repeated cursor");
            check(entry.GetProperty("templates").GetArrayLength() == 1 && !entry.GetRawText().Contains(secret), "resource lists templates and redacts metadata through SDK fixture");
            var read = await mounted.Read("browser:fixture", "fixture:text");
            var blocks = read.Content.OfType<EmbeddedResourceBlock>().ToArray();
            check(Reply.CodeOf(read) is null && blocks.Length == 2 && blocks[0].Resource is TextResourceContents text && !text.Text.Contains(secret), "resource read passes embedded text with redaction");
            check(blocks[1].Resource is BlobResourceContents blob && blob.Blob.Span.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(secret)), "resource read passes fixture blob unchanged");
            check(Reply.CodeOf(await mounted.Read("browser:fixture", "fixture:missing")) == "MCP_BACKEND_ERROR", "resource protocol errors use MCP_BACKEND_ERROR");
            check(connected.Active == 0, "completed SDK resource operations release drain leases");
            var browserFoo = new Target("browser:foo", client, isBrowser: true);
            var literalBrowser = new Target("browser:foo", client);
            var literalMcp = new Target("mcp:bar", client);
            var aliases = new McpResources(() => [browserFoo, literalBrowser, literalMcp]);
            foreach (string address in new[] { "browser:foo", "mcp:browser:foo", "mcp:mcp:bar" })
            {
                var addressed = await aliases.Read(address.ToUpperInvariant(), "fixture:text");
                check(Reply.CodeOf(addressed) is null && addressed.StructuredContent!.Value.GetProperty("data").GetProperty("server").GetString() == address,
                    "resource address routes exactly to " + address);
            }
            check(browserFoo.Acquired == 1 && literalBrowser.Acquired == 1 && literalMcp.Acquired == 1,
                "browser and literal reserved-prefix MCP identities each acquire only their own resource target");
            check(Reply.CodeOf(await aliases.Read("mcp:bar", "fixture:text")) == "NOT_FOUND", "resource MCP escape removes exactly one prefix");
            var aliasList = await aliases.List();
            var addresses = aliasList.StructuredContent!.Value.GetProperty("data").GetProperty("servers").EnumerateArray().Select(e => e.GetProperty("server").GetString()).ToArray();
            check(addresses.SequenceEqual(new[] { "browser:foo", "mcp:browser:foo", "mcp:mcp:bar" }), "resource list publishes unambiguous round-trippable addresses");
            using var callerCancellation = new CancellationTokenSource();
            var cancelledRead = mounted.Read("browser:fixture", "fixture:caller-cancel", callerCancellation.Token);
            await callerReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            callerCancellation.Cancel();
            check(Reply.CodeOf(await cancelledRead.WaitAsync(TimeSpan.FromSeconds(10))) == "CANCELLED", "resource read retains caller-requested cancellation");
            var lostRead = mounted.Read("browser:fixture", "fixture:transport-loss");
            await transportReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await client.DisposeAsync();
            var lost = await lostRead.WaitAsync(TimeSpan.FromSeconds(10));
            check(Reply.CodeOf(lost) == "MCP_UNAVAILABLE", "resource read session disposal is transport unavailable, never caller-cancelled");
            check(lost.StructuredContent!.Value.GetProperty("error").GetProperty("details").GetProperty("provider_error").GetString() is "OperationCanceledException" or "TaskCanceledException",
                "resource fixture proves non-caller SDK OperationCanceledException follows transport-failure path");
        }
        finally
        {
            allowList.TrySetResult();
            shutdown.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }

        var noResourceIn = new Pipe();
        var noResourceOut = new Pipe();
        using var stopNoResources = new CancellationTokenSource();
        await using var noResourceServer = McpServer.Create(new StreamServerTransport(noResourceIn.Reader.AsStream(), noResourceOut.Writer.AsStream()),
            new McpServerOptions { ServerInfo = new() { Name = "without-resources", Version = "1" }, Capabilities = new() });
        var noResourceRunning = noResourceServer.RunAsync(stopNoResources.Token);
        await using var noResourceClient = await McpClient.CreateAsync(new StreamClientTransport(noResourceIn.Writer.AsStream(), noResourceOut.Reader.AsStream()),
            new McpClientOptions { ClientInfo = new() { Name = "no-resource-test", Version = "1" }, ProtocolVersion = "2025-11-25", InitializationTimeout = Timeout.InfiniteTimeSpan });
        try
        {
            var noResources = await new McpResources(() => [new Target("empty", noResourceClient)]).List();
            check(noResources.StructuredContent!.Value.GetProperty("data").GetProperty("servers")[0].GetProperty("state").GetString() == "none",
                "server without resources capability is reported as none without a backend request");
        }
        finally
        {
            stopNoResources.Cancel();
            try { await noResourceRunning; } catch (OperationCanceledException) { }
        }
    }
}
