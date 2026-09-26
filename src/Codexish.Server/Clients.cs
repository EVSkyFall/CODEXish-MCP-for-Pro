using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codexish.Server;

// RFC 7591 Dynamic Client Registration for connectors that register themselves, such as ChatGPT Plugins. The static
// client from codexish.json is not part of this registry and keeps working exactly as before.
public sealed class ClientRegistry(Store store, ServerConfig config)
{
    public const string Prefix = "dcr_";
    public const string Basic = "client_secret_basic";
    public const string Post = "client_secret_post";
    public const string Public = "none";
    public const int DefaultUnusedLimit = 1000;
    public const int NameLength = 200;
    public static readonly string[] GrantTypes = ["authorization_code", "refresh_token"];
    public static readonly string[] ResponseTypes = ["code"];

    // Registrations that never completed a sign-in are anonymous state anyone on the internet can create, so only the
    // newest ones are kept. A client that has signed in is never counted and never evicted. Tests lower this number.
    public int UnusedLimit { get; set; } = DefaultUnusedLimit;

    public static bool IsRegisteredId(string? clientId) =>
        clientId is { Length: 36 } && clientId.StartsWith(Prefix, StringComparison.Ordinal) &&
        clientId.AsSpan(Prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    public ClientRow? Find(string? clientId) => IsRegisteredId(clientId) ? store.Client(clientId!) : null;

    public static bool IsPublic(ClientRow client) => client.AuthMethod == Public;

    public static bool SecretMatches(ClientRow client, string? offered) =>
        client.SecretHash is { Length: > 0 } hash && !string.IsNullOrEmpty(offered) &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Tokens.HashToken(offered)), Encoding.ASCII.GetBytes(hash));

    // RFC 7591 section 3.2: the registered metadata, or the error code and description for a 400.
    public (Dictionary<string, object>? Registered, string? Error, string? Description) Register(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return (null, "invalid_client_metadata", "The registration request must be a JSON object.");
        if (!body.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array || uris.GetArrayLength() == 0)
            return (null, "invalid_redirect_uri",
                "redirect_uris is required: a non-empty array of absolute https URIs, or http URIs on localhost, 127.0.0.1 or [::1].");
        List<string> redirects = [];
        foreach (var entry in uris.EnumerateArray())
        {
            string? uri = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
            if (uri is null || !IsRegistrableRedirect(uri))
                return (null, "invalid_redirect_uri",
                    $"redirect_uris entry {(uri is null ? entry.GetRawText() : JsonSerializer.Serialize(uri))} must be an absolute https URI " +
                    "without a fragment, or an http URI on localhost, 127.0.0.1 or [::1].");
            if (!redirects.Contains(uri, StringComparer.Ordinal)) redirects.Add(uri);
        }
        // Absent means client_secret_basic; any other value is replaced by it, and the response says so (RFC 7591 section 2).
        string method = body.TryGetProperty("token_endpoint_auth_method", out var offered) && offered.ValueKind == JsonValueKind.String
            ? offered.GetString()! : Basic;
        if (method is not (Basic or Post or Public)) method = Basic;
        string? name = body.TryGetProperty("client_name", out var named) && named.ValueKind == JsonValueKind.String
            ? CleanName(named.GetString()) : null;

        string clientId = Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string? secret = method == Public ? null : ServerConfig.NewSecret();
        var issued = DateTimeOffset.UtcNow;
        store.InsertClient(new ClientRow(clientId, secret is null ? null : Tokens.HashToken(secret), redirects.ToArray(), method, name,
            issued.ToString("o", CultureInfo.InvariantCulture), null));
        // The new registration is the newest, so it always survives its own eviction pass.
        try { store.EvictUnusedClients(Math.Max(1, UnusedLimit)); }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine($"evicting unused client registrations failed and is tried at the next registration: {error.Message}");
        }
        store.Event("client_registered", clientId, new { auth_method = method, name, redirect_hosts = redirects.Select(Host).Distinct().ToArray() });

        var registered = new Dictionary<string, object>
        {
            ["client_id"] = clientId,
            ["client_id_issued_at"] = issued.ToUnixTimeSeconds()
        };
        if (secret is not null)
        {
            registered["client_secret"] = secret;
            registered["client_secret_expires_at"] = 0;
        }
        registered["redirect_uris"] = redirects.ToArray();
        registered["token_endpoint_auth_method"] = method;
        registered["grant_types"] = GrantTypes;
        registered["response_types"] = ResponseTypes;
        if (name is not null) registered["client_name"] = name;
        return (registered, null, null);
    }

    // An absolute https URI without a fragment, or http on a loopback name (RFC 8252 section 7.3).
    public static bool IsRegistrableRedirect(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Fragment.Length == 0 && !value.Contains('#') &&
        (uri.Scheme == Uri.UriSchemeHttps ||
         (uri.Scheme == Uri.UriSchemeHttp && uri.Host is "localhost" or "127.0.0.1" or "[::1]"));

    // Control and formatting characters (which include the bidirectional overrides) are removed, the rest is trimmed and
    // cut to 200 characters without splitting a surrogate pair. Nothing left means no name.
    public static string? CleanName(string? value)
    {
        if (value is null) return null;
        var kept = new StringBuilder(value.Length);
        foreach (char c in value)
            if (char.GetUnicodeCategory(c) is not (UnicodeCategory.Control or UnicodeCategory.Format)) kept.Append(c);
        string name = kept.ToString().Trim();
        if (name.Length > NameLength) name = name[..(char.IsHighSurrogate(name[NameLength - 1]) ? NameLength - 1 : NameLength)].TrimEnd();
        return name.Length == 0 ? null : name;
    }

    public void MarkSignedIn(string clientId) =>
        store.MarkClientSignedIn(clientId, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

    // A user action: every registered client goes and every token issued to one is revoked. The static client stays.
    public (int Removed, int Revoked) RemoveAll()
    {
        var result = store.RemoveRegisteredClients(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), config.OAuth.ClientId);
        store.Event("clients_removed", null, new { removed = result.Removed, revoked = result.Revoked });
        return result;
    }

    // For the local status: never the secret, and only the host of each callback.
    public object[] Describe() => store.Clients().Select(c => (object)new
    {
        client_id = c.ClientId,
        name = c.Name,
        auth_method = c.AuthMethod,
        created_at = c.CreatedAt,
        last_signed_in_at = c.LastSignedInAt,
        redirect_hosts = c.RedirectUris.Select(Host).Distinct(StringComparer.Ordinal).ToArray()
    }).ToArray();

    // Punycode rather than Unicode, so a look-alike name cannot pass for the host it imitates.
    public static string Host(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)) return redirectUri;
        string host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
    }
}
