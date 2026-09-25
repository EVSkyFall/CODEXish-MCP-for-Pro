using System.Net;

namespace Codexish.Server;

// Host and Origin are routing and browser checks, NOT client authentication. Identical semantics to the P0
// ProbeAccessPolicy, which the F-1 review accepted: exact hostnames, exact origins, a rejection log, and no
// trust in forwarded headers. Authentication is the bearer token from D12.
public sealed class AccessPolicy
{
    private readonly HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "[::1]", "::1" };
    private readonly HashSet<string> origins = new(StringComparer.OrdinalIgnoreCase);

    // An entry that is not an exact hostname or origin is ignored here; ServerConfig.Validate reports it as a warning.
    // One bad entry never keeps the server from starting.
    public AccessPolicy(IEnumerable<string?>? allowedHosts = null, IEnumerable<string?>? allowedOrigins = null)
    {
        foreach (string? host in allowedHosts ?? [])
        {
            if (!IsExactHost(host)) continue;
            hosts.Add(host!);
            AllowsPublicHost = true;
        }
        foreach (string? origin in allowedOrigins ?? [])
            if (NormalizeOrigin(origin) is { } normalized) origins.Add(normalized);
    }

    public static bool IsExactHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && host == host.Trim() &&
        host.IndexOfAny([':', '/', '\\', '*', '@', '?', '#']) < 0 &&
        Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;

    public string? RejectionReason(HttpRequest request)
    {
        string host;
        try { host = request.Host.Host; }
        catch (FormatException) { return "invalid_host"; }
        if (!hosts.Contains(host)) return "host_not_allowed";
        if (!request.Headers.ContainsKey("Origin")) return null;
        string? normalized = NormalizeOrigin(request.Headers.Origin.ToString());
        if (normalized is null) return "invalid_origin";
        if (origins.Contains(normalized)) return null;
        string? sameOrigin = NormalizeOrigin($"{request.Scheme}://{request.Host.Value}");
        return string.Equals(normalized, sameOrigin, StringComparison.OrdinalIgnoreCase) ? null : "origin_not_allowed";
    }

    // True when a non-loopback host was configured; --no-auth is refused in that case.
    public bool AllowsPublicHost { get; }

    public static string? NormalizeOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Contains('\\') ||
            value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    // D11: /control is accepted only when the connection itself is loopback and the Host header is loopback,
    // so a tunnel that forwards to 127.0.0.1 can never reach it.
    public static bool IsLoopbackControl(HttpContext context)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote)) return false;
        string host = context.Request.Host.Host;
        return host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
    }
}
