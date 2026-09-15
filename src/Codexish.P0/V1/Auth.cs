using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Codexish.P0.V1;

public static class Passwords
{
    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return "pbkdf2-sha256$600000$" + Convert.ToBase64String(salt) + "$" +
            Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32));
    }
    public static bool Verify(string password, string stored)
    {
        try
        {
            string[] p = stored.Split('$'); if (p.Length != 4 || p[0] != "pbkdf2-sha256") return false;
            int iterations = int.Parse(p[1]); if (iterations <= 0) return false;
            byte[] expected = Convert.FromBase64String(p[3]);
            return CryptographicOperations.FixedTimeEquals(expected,
                Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[2]), iterations, HashAlgorithmName.SHA256, expected.Length));
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException) { return false; }
    }
}

public sealed class OAuth(ServerConfig config, Store store)
{
    private sealed record AuthRequest(string Client, string Redirect, string Resource, string State, string Challenge, string Scope);
    private sealed record TokenMeta(string Client, string Resource, string Scope, string? Redirect = null, string? Challenge = null);
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public string Resource => config.PublicUrl + "/mcp";
    public static string RandomToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public static string Digest(string value) => ProbeRuntime.Hash(Encoding.UTF8.GetBytes(value));
    private long Now => Clock().ToUnixTimeSeconds();
    private void Put(string token, string kind, string session, long expires, object data) => store.Exec(
        "INSERT INTO tokens VALUES($h,$k,$s,$e,0,$d)", ("$h", Digest(token)), ("$k", kind), ("$s", session), ("$e", expires), ("$d", JsonSerializer.Serialize(data)));
    private (string session, long expires, bool used, string data)? Get(string token, string kind)
    {
        using var c = store.Command("SELECT session,expires,used,data FROM tokens WHERE hash=$h AND kind=$k", ("$h", Digest(token)), ("$k", kind));
        using var r = c.ExecuteReader(); return r.Read() ? (r.GetString(0), r.GetInt64(1), r.GetInt32(2) != 0, r.GetString(3)) : null;
    }
    public string? ValidateAccess(string token)
    {
        lock (store.Gate)
        {
            var row = Get(token, "access"); if (row is null || row.Value.used || row.Value.expires <= Now) return null;
            var m = JsonSerializer.Deserialize<TokenMeta>(row.Value.data)!;
            return m.Resource == Resource && m.Client == config.ClientId && m.Scope.Split(' ').Contains("codexish") ? row.Value.session : null;
        }
    }
    public void Revoke() => store.Exec("UPDATE tokens SET used=1");
    public void Map(WebApplication app)
    {
        app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(new { resource = Resource, authorization_servers = new[] { config.PublicUrl }, scopes_supported = new[] { "codexish" }, bearer_methods_supported = new[] { "header" } }));
        app.MapGet("/.well-known/oauth-protected-resource/mcp", () => Results.Json(new { resource = Resource, authorization_servers = new[] { config.PublicUrl }, scopes_supported = new[] { "codexish" }, bearer_methods_supported = new[] { "header" } }));
        app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new { issuer = config.PublicUrl,
            authorization_endpoint = config.PublicUrl + "/authorize", token_endpoint = config.PublicUrl + "/token",
            response_types_supported = new[] { "code" }, grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" }, token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post" },
            scopes_supported = new[] { "codexish", "offline_access" } }));
        app.MapGet("/authorize", (HttpContext c) => Begin(c));
        app.MapPost("/authorize", (HttpContext c) => Login(c));
        app.MapPost("/token", (HttpContext c) => Exchange(c));
    }
    private static string One(IQueryCollection q, string key) => q.TryGetValue(key, out var v) && v.Count == 1 ? v[0]! : "";
    private static string One(IFormCollection q, string key) => q.TryGetValue(key, out var v) && v.Count == 1 ? v[0]! : "";
    private static IResult Error(string name, int status = 400) => Results.Json(new { error = name }, statusCode: status);
    private static void NoCache(HttpContext c)
    {
        c.Response.Headers.CacheControl = "no-store"; c.Response.Headers.Pragma = "no-cache";
        c.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    private IResult Begin(HttpContext c)
    {
        NoCache(c); var q = c.Request.Query;
        var request = new AuthRequest(One(q, "client_id"), One(q, "redirect_uri"), One(q, "resource"), One(q, "state"), One(q, "code_challenge"), One(q, "scope"));
        if (One(q, "response_type") != "code" || request.Client != config.ClientId || !config.RedirectUris.Contains(request.Redirect, StringComparer.Ordinal) ||
            request.Resource != Resource || One(q, "code_challenge_method") != "S256" || !Regex.IsMatch(request.Challenge, "^[A-Za-z0-9_-]{43}$")) return Error("invalid_request");
        if (request.Scope.Length == 0) request = request with { Scope = "codexish" };
        var scopes = request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains("codexish") || scopes.Except(["codexish", "offline_access"]).Any()) return Error("invalid_scope");
        string ticket = RandomToken(); lock (store.Gate) Put(ticket, "login", "", Now + 600, request);
        c.Response.Cookies.Append("codexish-login", ticket, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/authorize", MaxAge = TimeSpan.FromMinutes(10) });
        c.Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        return Results.Content("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>CODEXish sign in</title><h1>CODEXish</h1>" +
            "<p>Sign in to connect your coding tools to the registered client.</p><form method=\"post\" action=\"/authorize\">" +
            "<input type=\"hidden\" name=\"ticket\" value=\"" + WebUtility.HtmlEncode(ticket) + "\">" +
            "<label>Password <input type=\"password\" name=\"password\" autocomplete=\"current-password\" required></label><button>Sign in</button></form></html>", "text/html; charset=utf-8");
    }
    private async Task<IResult> Login(HttpContext c)
    {
        NoCache(c); if (!c.Request.HasFormContentType) return Error("invalid_request");
        var form = await c.Request.ReadFormAsync(); string ticket = One(form, "ticket");
        string cookie = c.Request.Cookies["codexish-login"] ?? "";
        if (ticket.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ticket), Encoding.UTF8.GetBytes(cookie))) return Error("invalid_request");
        if (!Passwords.Verify(One(form, "password"), config.PasswordHash)) return Error("access_denied", 401);
        lock (store.Gate)
        {
            var row = Get(ticket, "login"); if (row is null || row.Value.used || row.Value.expires <= Now) return Error("invalid_request");
            var request = JsonSerializer.Deserialize<AuthRequest>(row.Value.data)!;
            string code = RandomToken(), session = "sess_" + Guid.NewGuid().ToString("N");
            store.Exec("BEGIN IMMEDIATE");
            try
            {
                store.Exec("UPDATE tokens SET used=1 WHERE hash=$h", ("$h", Digest(ticket)));
                Put(code, "code", session, Now + 300, new TokenMeta(request.Client, request.Resource, request.Scope, request.Redirect, request.Challenge));
                store.Exec("COMMIT");
            }
            catch { store.Exec("ROLLBACK"); throw; }
            c.Response.Cookies.Delete("codexish-login", new CookieOptions { Path = "/authorize", Secure = true });
            return Results.Redirect(QueryHelpers.AddQueryString(request.Redirect, new Dictionary<string, string?> { ["code"] = code, ["state"] = request.State, ["iss"] = config.PublicUrl }));
        }
    }
    private async Task<IResult> Exchange(HttpContext c)
    {
        NoCache(c); if (!c.Request.HasFormContentType) return Error("invalid_request");
        var form = await c.Request.ReadFormAsync(); string client = One(form, "client_id"), secret = One(form, "client_secret");
        string auth = c.Request.Headers.Authorization.ToString();
        if (auth.Length != 0)
        {
            if (!auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) || secret.Length != 0) return Error("invalid_client", 401);
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..])); int colon = decoded.IndexOf(':');
                if (colon < 0) return Error("invalid_client", 401);
                string basicClient = WebUtility.UrlDecode(decoded[..colon]);
                if (client.Length != 0 && client != basicClient) return Error("invalid_client", 401);
                client = basicClient; secret = WebUtility.UrlDecode(decoded[(colon + 1)..]);
            }
            catch (FormatException) { return Error("invalid_client", 401); }
        }
        if (client != config.ClientId || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(config.ClientSecret))) return Error("invalid_client", 401);
        string grant = One(form, "grant_type"), resource = One(form, "resource");
        if (resource != Resource) return Error("invalid_target");
        string kind = grant == "authorization_code" ? "code" : grant == "refresh_token" ? "refresh" : "";
        if (kind.Length == 0) return Error("unsupported_grant_type");
        string token = One(form, kind == "code" ? "code" : "refresh_token");
        lock (store.Gate)
        {
            var row = Get(token, kind); if (row is null || row.Value.expires <= Now) return Error("invalid_grant");
            if (row.Value.used)
            {
                if (kind == "refresh") store.Exec("UPDATE tokens SET used=1 WHERE session=$s", ("$s", row.Value.session));
                return Error("invalid_grant");
            }
            var meta = JsonSerializer.Deserialize<TokenMeta>(row.Value.data)!;
            if (meta.Client != client || meta.Resource != resource) return Error("invalid_grant");
            if (kind == "code")
            {
                string verifier = One(form, "code_verifier");
                if (One(form, "redirect_uri") != meta.Redirect || !Regex.IsMatch(verifier, "^[A-Za-z0-9._~-]{43,128}$") ||
                    WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != meta.Challenge) return Error("invalid_grant");
            }
            string access = RandomToken(), refresh = RandomToken();
            store.Exec("BEGIN IMMEDIATE");
            try
            {
                store.Exec("UPDATE tokens SET used=1 WHERE hash=$h", ("$h", Digest(token)));
                Put(access, "access", row.Value.session, Now + 12 * 3600, meta);
                Put(refresh, "refresh", row.Value.session, Now + 30 * 86400, meta);
                store.Exec("COMMIT");
            }
            catch { store.Exec("ROLLBACK"); throw; }
            return Results.Json(new { access_token = access, token_type = "Bearer", expires_in = 12 * 3600, refresh_token = refresh, scope = meta.Scope });
        }
    }
}
