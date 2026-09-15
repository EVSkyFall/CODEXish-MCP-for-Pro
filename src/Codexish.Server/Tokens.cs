using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Codexish.Server;

public sealed record AuthorizationCode(string ClientId, string RedirectUri, string? Challenge, string? Resource,
    string Scope, DateTimeOffset Expires);

// D12. Opaque tokens: 32 random bytes, stored only as a SHA-256 hash with an audience and an expiry, so the
// database never holds a usable credential. Authorization codes and login nonces are short-lived and in memory.
public sealed class Tokens(Store store, ServerConfig config)
{
    private readonly ConcurrentDictionary<string, AuthorizationCode> codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> nonces = new(StringComparer.Ordinal);

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(15);

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public const string Scope = "mcp";

    public string Issue(string kind, string clientId, string audience, TimeSpan lifetime, bool pkceUsed,
        string family, string scope)
    {
        string token = ServerConfig.NewSecret();
        store.InsertToken(new TokenRow(HashToken(token), kind, clientId, audience, DateTimeOffset.UtcNow + lifetime,
            false, pkceUsed, family, scope));
        store.Event("token_issued", kind, new { client_id = clientId, audience, pkce_used = pkceUsed, family, scope,
            expires_in = (int)lifetime.TotalSeconds });
        return token;
    }

    // Only the mcp scope exists in this slice; anything else is refused at /authorize rather than downgraded.
    public static string? NormalizeScope(string? requested)
    {
        string[] parts = (requested ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Scope;
        return parts.All(p => p == Scope) ? Scope : null;
    }

    public static bool Grants(TokenRow row, string scope) =>
        row.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);

    public bool ConsumeRefresh(string token) => store.ConsumeRefresh(HashToken(token));

    public static string NewFamily() => ServerConfig.NewSecret(16);

    public TokenRow? Row(string token) => store.Token(HashToken(token));

    public int RevokeFamily(string family) => store.RevokeFamily(family);

    public (TokenRow? Row, string Reason) Validate(string token, string kind, string audience)
    {
        var row = store.Token(HashToken(token));
        if (row is null) return (null, "unknown_token");
        if (row.Kind != kind) return (null, "wrong_token_kind");
        if (row.Revoked) return (null, "revoked");
        if (row.ExpiresAt <= DateTimeOffset.UtcNow) return (null, "expired");
        // Resource indicators default to <public_url>/mcp; a token minted for something else is not accepted here.
        if (audience.Length > 0 && !string.Equals(row.Audience, audience, StringComparison.Ordinal)) return (null, "wrong_audience");
        // A token that does not carry the mcp scope cannot be used on /mcp, whatever else it may carry.
        if (kind == "access" && !Grants(row, Scope)) return (null, "insufficient_scope");
        return (row, "ok");
    }

    public void Revoke(string token) => store.RevokeToken(HashToken(token));

    public int RevokeAll() => store.RevokeAllTokens();

    public string IssueCode(string clientId, string redirectUri, string? challenge, string? resource, string scope)
    {
        string code = ServerConfig.NewSecret();
        codes[code] = new AuthorizationCode(clientId, redirectUri, challenge, resource, scope, DateTimeOffset.UtcNow + CodeLifetime);
        return code;
    }

    // Single use: the code is removed whether or not the rest of the exchange succeeds.
    public AuthorizationCode? ConsumeCode(string code)
    {
        if (!codes.TryRemove(code, out var entry)) return null;
        return entry.Expires <= DateTimeOffset.UtcNow ? null : entry;
    }

    public string IssueNonce()
    {
        string nonce = ServerConfig.NewSecret(16);
        nonces[nonce] = DateTimeOffset.UtcNow + NonceLifetime;
        foreach (var stale in nonces.Where(n => n.Value <= DateTimeOffset.UtcNow).Select(n => n.Key).ToArray())
            nonces.TryRemove(stale, out _);
        return nonce;
    }

    public bool ConsumeNonce(string? nonce) =>
        nonce is { Length: > 0 } && nonces.TryRemove(nonce, out var expiry) && expiry > DateTimeOffset.UtcNow;

    public static bool VerifyPkce(string? challenge, string? verifier)
    {
        if (string.IsNullOrEmpty(challenge)) return true;
        if (string.IsNullOrEmpty(verifier) || verifier.Length is < 43 or > 128) return false;
        string computed = ServerConfig.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(challenge));
    }

    public static bool SecretMatches(string expected, string? offered) =>
        offered is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(offered));

    public string Audience => config.Resource;
}
