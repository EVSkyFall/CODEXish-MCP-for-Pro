using ModelContextProtocol.Client;
namespace Codexish.Server;

internal static class McpHandshakeTests
{
    public static void Run(Action<bool,string> check)
    {
        var mode = new McpHandshake();
        var initial = mode.ProbeTimeout;
        mode.Failed(false, false, false, exchanged: false);
        check(!mode.Pinned && mode.ProbeTimeout == initial, "a down server does not change the handshake attempt");
        mode.Failed(true, false, false);
        check(mode.ProbeTimeout == initial, "probe timeout alone does not change the handshake attempt");
        mode.Failed(true, true, false);
        check(mode.ProbeTimeout == initial + initial && !mode.Pinned, "probe timeout plus rejected initialize doubles the discover probe");
        check(McpHandshake.DoubleProbe(TimeSpan.MaxValue) == Timeout.InfiniteTimeSpan &&
            McpHandshake.DoubleProbe(Timeout.InfiniteTimeSpan) == Timeout.InfiniteTimeSpan, "probe doubling saturates at infinite without overflow");
        mode.Failed(false, false, true);
        check(mode.Pinned, "a connection closed before answering pins initialize on the next attempt");
        mode.Remembered = true;
        mode.Failed(false, false, false, exchanged: false);
        check(mode.Pinned, "connection failure before exchange does not flip a remembered handshake mode");
        check(McpHandshake.ClosedBeforeAnswer(new HttpRequestException(HttpRequestError.ResponseEnded), false),
            "HTTP ResponseEnded is concrete closed-before-answer handshake evidence");
        mode.Failed(false, false, false);
        check(!mode.Pinned, "any failed remembered handshake switches to the other mode");
        mode.Failed(false, false, false);
        check(mode.Pinned, "a remembered fallback never latches to one protocol mode");
        var initialization = McpMount.InitialOptions(new McpHandshake());
        check(initialization.InitializationTimeout == Timeout.InfiniteTimeSpan && initialization.ProtocolVersion is null,
            "actual mount client options remove initialization deadline and leave protocol unpinned initially");
        check(McpMount.InitialOptions(new McpHandshake { Pinned = true }).ProtocolVersion == "2025-11-25",
            "actual fallback options initialize first only after handshake evidence");
        using var http = McpMount.CreateHttpClient();
        check(http.Timeout == Timeout.InfiniteTimeSpan, "HTTP client has no implicit 100-second deadline");
        var options = McpMount.HttpOptions(new Uri("http://127.0.0.1/mcp"), new(), "http");
        check(options.ConnectionTimeout == Timeout.InfiniteTimeSpan && options.MaxReconnectionAttempts == int.MaxValue &&
            options.TransportMode == HttpTransportMode.AutoDetect, "HTTP connection and reconnection options have no hidden deadline or retry limit");
        check(McpMount.HttpOptions(new Uri("http://127.0.0.1/sse"), new(), "sse").TransportMode == HttpTransportMode.Sse,
            "explicit SSE uses the SSE transport");
    }
}
