using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

// Evidence, not an exception-message heuristic, determines the next discovery attempt.
public sealed class McpHandshake
{
    public bool Pinned { get; set; }
    public bool Remembered { get; set; }
    public TimeSpan ProbeTimeout { get; set; } = new McpClientOptions().DiscoverProbeTimeout;
    public string Mode => Pinned ? "initialize_2025-11-25" : "discover";
    public void Failed(bool probeTimedOut, bool initializeRejected, bool closedBeforeAnswer, bool exchanged = true)
    {
        if (!exchanged) return;
        if (Remembered) { Pinned = !Pinned; return; }
        if (!Pinned && probeTimedOut && initializeRejected)
            ProbeTimeout = DoubleProbe(ProbeTimeout);
        else if (!Pinned && closedBeforeAnswer) Pinned = true;
    }
    internal static bool ClosedBeforeAnswer(Exception error, bool processExited) => error is IOException || error is HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } || processExited;
    public static TimeSpan DoubleProbe(TimeSpan current) => current == Timeout.InfiniteTimeSpan || current.Ticks > TimeSpan.MaxValue.Ticks / 2
        ? Timeout.InfiniteTimeSpan : TimeSpan.FromTicks(current.Ticks * 2);
}

internal sealed class ObservedClientTransport(IClientTransport inner) : IClientTransport
{
    public string Name => inner.Name;
    public bool AnyAnswer, ProbeTimedOut, InitializeRejected, Connected, SentSuccessfully;
    private RequestId? discoverId, initializeId;
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var transport = await inner.ConnectAsync(cancellationToken);
        Connected = true;
        return new ObservedTransport(transport, this);
    }
    private sealed class ObservedTransport : ITransport
    {
        readonly ITransport inner;
        readonly ObservedClientTransport owner;
        readonly Channel<JsonRpcMessage> messages = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        public ObservedTransport(ITransport inner, ObservedClientTransport owner) { this.inner = inner; this.owner = owner; _ = Pump(); }
        public string? SessionId => inner.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader => messages.Reader;
        public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            lock (owner)
            {
                if (message is JsonRpcRequest request)
                {
                    if (request.Method == "server/discover") owner.discoverId = request.Id;
                    if (request.Method == "initialize")
                    {
                        owner.initializeId = request.Id;
                        owner.ProbeTimedOut = owner.discoverId is not null && !owner.AnyAnswer;
                    }
                }
            }
            await inner.SendMessageAsync(message, cancellationToken);
            lock (owner) owner.SentSuccessfully = true;
        }
        async Task Pump()
        {
            try
            {
                await foreach (var message in inner.MessageReader.ReadAllAsync())
                {
                    lock (owner)
                    {
                        if (message is JsonRpcResponse or JsonRpcError) owner.AnyAnswer = true;
                        if (message is JsonRpcError error && owner.initializeId is { } id && error.Id == id) owner.InitializeRejected = true;
                    }
                    await messages.Writer.WriteAsync(message);
                }
                messages.Writer.TryComplete();
            }
            catch (Exception error) { messages.Writer.TryComplete(error); }
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
