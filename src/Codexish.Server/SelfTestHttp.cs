using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using static Codexish.Server.SelfTest;

namespace Codexish.Server;

// D11 and D12 over a real in-process Kestrel listener: the OAuth flow, the bearer gate on /mcp, the loopback
// control API and the MCP protocol surface are all exercised through HTTP.
internal static partial class HttpTests
{
    [GeneratedRegex("name=\"nonce\" value=\"([^\"]+)\"")] private static partial Regex NonceField();

    public static async Task Run(CodexishRuntime runtime, ServerConfig config, string password)
    {
        List<string> rejected = [];
        await using var app = CodexishHost.Build(runtime, 0, line => { rejected.Add(line); Console.WriteLine(line); });
        await app.StartAsync();
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
            await Metadata(http, config);
            string access = await OAuthFlow(http, runtime, config, password, rejected);
            await Redirects(http, config, password, rejected);
            await ClientAuthentication(http, config, password, rejected);
            await Protocol(http, access, rejected);
            await Control(http, runtime, config);
            await TokenLifecycle(http, runtime, config, access, password);
            await TunnelOrigin(http, config, password);
            await Scopes(http, runtime, config, password, rejected);
            await Resources(http, runtime, config, password, rejected);
            await ConcurrentRefresh(http, config, password);
            Configuration(config);
            Check(ServerConfig.NoAuthRefusal(config) is { Length: > 0 },
                "--no-auth is refused while a public host is allowed");
            Check(ServerConfig.NoAuthRefusal(SelfTest.BuildConfig(Path.Combine(Path.GetDirectoryName(config.StateDir)!, "loopback"),
                ServerConfig.NewSecret(12))) is null,
                "--no-auth is accepted for a pure loopback configuration");
        }
        finally { await app.StopAsync(); }
    }

    // Delta 5: --init derives the Host allowlist from public_url and keeps the documented ChatGPT callback
    // alongside any connector-specific one the user supplies.
    // A tunnel terminates TLS, so the browser's Origin is https while Kestrel sees an http request. The
    // login form's own POST must not be rejected as cross-origin.
    private static async Task TunnelOrigin(HttpClient http, ServerConfig config, string password)
    {
        Check(config.AllowOrigins.Contains(config.PublicUrl, StringComparer.OrdinalIgnoreCase),
            "the public_url origin is allowed automatically so the login form can post to itself");
        string redirect = config.OAuth.RedirectUris[0];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/authorize");
        request.Headers.Host = config.AllowHosts[0];
        request.Headers.Add("Origin", config.PublicUrl);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = config.OAuth.ClientId, ["redirect_uri"] = redirect,
            ["state"] = "s", ["scope"] = "mcp", ["nonce"] = "stale", ["password"] = password
        });
        using var response = await http.SendAsync(request);
        Check(response.StatusCode != HttpStatusCode.Forbidden,
            "a login POST arriving through the tunnel with its https Origin is not rejected as cross-origin");
        Check(ServerConfig.TransportRefusal(config, false) is null,
            "an https public_url is accepted with authentication enabled");
        var insecure = SelfTest.BuildConfig(Path.Combine(Path.GetDirectoryName(config.StateDir)!, "insecure"),
            ServerConfig.NewSecret(12));
        insecure.PublicUrl = "http://tunnel.example";
        insecure.Validate();
        Check(ServerConfig.TransportRefusal(insecure, false) is { Length: > 0 } &&
              ServerConfig.TransportRefusal(insecure, true) is null,
            "a plain http public_url is refused with authentication enabled and allowed only with --no-auth");
    }

    // Any requested scope is accepted and the grant is always mcp, so a client asking for more or other scopes still
    // connects instead of being refused.
    private static async Task Scopes(HttpClient http, CodexishRuntime runtime, ServerConfig config, string password, List<string> rejected)
    {
        string redirect = config.OAuth.RedirectUris[0];
        List<(string? Returned, string Stored)> grants = [];
        foreach (string? requested in new[] { "admin", "openid profile offline_access", "mcp extra", null })
        {
            var location = await SignIn(http, config, password, redirect, null, requested);
            using var exchanged = await http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = QueryHelpers.ParseQuery(location.Query)["code"].ToString(),
                ["redirect_uri"] = redirect, ["client_id"] = config.OAuth.ClientId, ["client_secret"] = config.OAuth.ClientSecret
            }));
            var body = JsonDocument.Parse(await exchanged.Content.ReadAsStringAsync()).RootElement;
            grants.Add((body.GetProperty("scope").GetString(),
                runtime.Store.Token(Tokens.HashToken(body.GetProperty("access_token").GetString()!))!.Scope));
        }
        Check(grants.Count == 4 && grants.All(g => g.Returned == Tokens.Scope && g.Stored == Tokens.Scope),
            "any requested scope, or none, is accepted, and the token response and the token row both say mcp");
        Check(!rejected.Any(l => l.Contains("unsupported_scope", StringComparison.Ordinal) || l.Contains("invalid_scope", StringComparison.Ordinal)),
            "no requested scope is ever a rejection reason");
        string scopeless = ServerConfig.NewSecret();
        runtime.Store.InsertToken(new TokenRow(Tokens.HashToken(scopeless), "access", config.OAuth.ClientId,
            config.Resource, DateTimeOffset.UtcNow.AddHours(1), false, true, "", ""));
        Check(await McpStatus(http, scopeless) == HttpStatusCode.Unauthorized,
            "a token without the mcp scope cannot be used on /mcp");
    }

    // A resource indicator never refuses a request; tokens are always for <public_url>/mcp, and a value naming
    // another origin is logged.
    private static async Task Resources(HttpClient http, CodexishRuntime runtime, ServerConfig config, string password, List<string> rejected)
    {
        string redirect = config.OAuth.RedirectUris[0];
        const string elsewhere = "https://elsewhere.invalid/mcp";
        var location = await SignIn(http, config, password, redirect, null, "mcp", elsewhere);
        string code = QueryHelpers.ParseQuery(location.Query)["code"].ToString();
        Check(code.Length > 20 && rejected.Contains($"oauth resource differs from public origin value={JsonSerializer.Serialize(elsewhere)}"),
            "a resource on another origin does not stop authorization and is logged with its value");
        using (var exchanged = await http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["resource"] = elsewhere,
            ["client_id"] = config.OAuth.ClientId, ["client_secret"] = config.OAuth.ClientSecret
        })))
        {
            Check(exchanged.StatusCode == HttpStatusCode.OK, "a resource on another origin does not stop the token exchange either");
            string access = JsonDocument.Parse(await exchanged.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;
            Check(runtime.Store.Token(Tokens.HashToken(access))!.Audience == config.Resource && await McpStatus(http, access) == HttpStatusCode.OK,
                "the token is issued for this server's own MCP endpoint whatever resource was presented");
        }
        int logged = rejected.Count(l => l.StartsWith("oauth resource differs", StringComparison.Ordinal));
        await SignIn(http, config, password, redirect, null, "mcp", config.PublicUrl + "/");
        Check(rejected.Count(l => l.StartsWith("oauth resource differs", StringComparison.Ordinal)) == logged,
            "a resource on the public origin, even with another path, is not reported");
    }

    // A refresh response lost in the tunnel, or two refreshes racing, must never disconnect the connector.
    private static async Task ConcurrentRefresh(HttpClient http, ServerConfig config, string password)
    {
        string redirect = config.OAuth.RedirectUris[0];
        string code = await Authorize(http, config, password, redirect, null);
        string refresh;
        using (var exchanged = await http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect,
            ["client_id"] = config.OAuth.ClientId, ["client_secret"] = config.OAuth.ClientSecret
        })))
            refresh = JsonDocument.Parse(await exchanged.Content.ReadAsStringAsync()).RootElement
                .GetProperty("refresh_token").GetString()!;

        Dictionary<string, string> Body() => new()
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refresh,
            ["client_id"] = config.OAuth.ClientId, ["client_secret"] = config.OAuth.ClientSecret
        };
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => http.PostAsync("/token", new FormUrlEncodedContent(Body()))));
        List<(HttpStatusCode Status, string? Refresh, string? Access)> results = [];
        foreach (var response in responses)
        {
            using (response)
            {
                var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                results.Add((response.StatusCode, body.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
                    body.TryGetProperty("access_token", out var a) ? a.GetString() : null));
            }
        }
        Check(results.All(r => r.Status == HttpStatusCode.OK && r.Refresh == refresh) && results.Select(r => r.Access).Distinct().Count() == 4,
            $"four concurrent refreshes with one refresh token all succeed and hand the same refresh token back ({results.Count(r => r.Status == HttpStatusCode.OK)} of 4)");
    }

    private static void Configuration(ServerConfig existing)
    {
        string temp = Path.Combine(Path.GetDirectoryName(existing.StateDir)!, "init-check");
        Directory.CreateDirectory(Path.Combine(temp, "proj"));
        var created = ServerConfig.Create("https://tunnel.example/ignored/path", ServerConfig.NewSecret(12),
            [("proj", Path.Combine(temp, "proj"))], ["https://chatgpt.com/aip/x/oauth/callback"], 3000,
            Path.Combine(temp, "state"));
        Check(created.AllowHosts.SequenceEqual(["tunnel.example"]),
            "--init puts the hostname from --public-url into allow_hosts");
        Check(created.PublicUrl == "https://tunnel.example", "--init keeps only the origin of --public-url");
        Check(created.OAuth.RedirectUris.SequenceEqual(
                [ServerConfig.DefaultRedirectUri, "https://chatgpt.com/aip/x/oauth/callback"]),
            "--redirect-uri adds the connector's callback without dropping the documented one");
        Check(created.OAuth.ClientSecret.Length >= 40 && created.ControlToken.Length >= 40 &&
              created.OAuth.ClientSecret != created.ControlToken,
            "--init generates distinct random secrets for the client and the control API");
        Check(ServerConfig.VerifyPassword("wrong", created.OAuth.PasswordHash) == false &&
              created.OAuth.PasswordHash.StartsWith("pbkdf2-sha256$600000$", StringComparison.Ordinal),
            "the password is stored as a PBKDF2-SHA256 hash and a wrong password does not verify");
        Check(created.OAuth.RefreshTokenDays == 0, "--init writes refresh_token_days 0, so refresh tokens do not expire");
        Directory.Delete(temp, true);
    }

    private static async Task Metadata(HttpClient http, ServerConfig config)
    {
        string document = await http.GetStringAsync("/.well-known/oauth-protected-resource");
        var resource = JsonDocument.Parse(document).RootElement;
        Check(resource.GetProperty("resource").GetString() == config.PublicUrl + "/mcp" &&
              resource.GetProperty("authorization_servers").EnumerateArray().First().GetString() == config.PublicUrl,
            "the protected resource document names this server's MCP endpoint and authorization server");
        Check(await http.GetStringAsync("/.well-known/oauth-protected-resource/mcp") == document,
            "the same protected resource document is served at the RFC 9728 path-suffixed location");
        using (var oidc = await http.GetAsync("/.well-known/openid-configuration"))
            Check(oidc.StatusCode == HttpStatusCode.NotFound, "no OpenID configuration is advertised, because no id_token is ever issued");
        var server = JsonDocument.Parse(await http.GetStringAsync("/.well-known/oauth-authorization-server")).RootElement;
        Check(server.GetProperty("issuer").GetString() == config.PublicUrl &&
              server.GetProperty("authorization_endpoint").GetString() == config.PublicUrl + "/authorize" &&
              server.GetProperty("token_endpoint").GetString() == config.PublicUrl + "/token",
            "the authorization server document publishes the issuer and both endpoints");
        Check(server.GetProperty("code_challenge_methods_supported").EnumerateArray().Single().GetString() == "S256" &&
              server.GetProperty("grant_types_supported").EnumerateArray().Select(g => g.GetString()).OrderBy(g => g)
                  .SequenceEqual(["authorization_code", "refresh_token"]),
            "the metadata advertises S256 PKCE and the two supported grants");
        Check(server.GetProperty("authorization_response_iss_parameter_supported").GetBoolean(),
            "the metadata advertises that every authorization response carries iss");
    }

    private static async Task<string> OAuthFlow(HttpClient http, CodexishRuntime runtime, ServerConfig config,
        string password, List<string> rejected)
    {
        using (var anonymous = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") })
        using (var denied = await http.SendAsync(anonymous))
        {
            Check(denied.StatusCode == HttpStatusCode.Unauthorized, "/mcp without a bearer token is 401");
            Check(denied.Headers.WwwAuthenticate.ToString() ==
                  $"Bearer resource_metadata=\"{config.PublicUrl}/.well-known/oauth-protected-resource\"",
                "the 401 carries the exact WWW-Authenticate resource_metadata challenge");
        }

        string redirect = config.OAuth.RedirectUris[0];
        string verifier = ServerConfig.NewSecret(48);
        string challenge = ServerConfig.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        string query = $"/authorize?response_type=code&client_id={Uri.EscapeDataString(config.OAuth.ClientId)}" +
                       $"&redirect_uri={Uri.EscapeDataString(redirect)}&state=xyz123&scope=mcp" +
                       $"&code_challenge={challenge}&code_challenge_method=S256";

        using (var plain = await http.GetAsync($"/authorize?response_type=code&client_id={config.OAuth.ClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("http://attacker.invalid/callback")}&state=xyz123"))
        {
            Check(plain.StatusCode == HttpStatusCode.BadRequest && plain.Headers.Location is null,
                "a redirect_uri that is neither listed nor https is refused on the local page before any code is issued");
            Check(rejected.Any(l => l.Contains("reason=redirect_uri_mismatch") && l.Contains("http://attacker.invalid/callback")),
                "the refused redirect_uri is logged so a callback that is not https can be listed");
        }
        using (var fragment = await http.GetAsync($"/authorize?response_type=code&client_id={config.OAuth.ClientId}" +
            $"&redirect_uri={Uri.EscapeDataString("https://attacker.invalid/callback#part")}&state=xyz123"))
            Check(fragment.StatusCode == HttpStatusCode.BadRequest && fragment.Headers.Location is null, "an https redirect_uri with a fragment is refused");
        using (var wrongClient = await http.GetAsync($"/authorize?response_type=code&client_id=someone-else&redirect_uri={Uri.EscapeDataString(redirect)}"))
            Check(wrongClient.StatusCode == HttpStatusCode.BadRequest, "an unknown client_id is refused");

        string form = await http.GetStringAsync(query);
        Check(form.Contains($"value=\"{challenge}\"") && form.Contains("value=\"xyz123\"") && form.Contains(WebUtility.HtmlEncode(redirect)),
            "the login form carries every OAuth parameter into the POST as hidden fields");
        Check(form.Contains("After sign-in you will return to <strong>chatgpt.com</strong>"),
            "the sign-in page names the host a listed callback returns to");
        string nonce = NonceField().Match(form).Groups[1].Value;

        var fields = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = config.OAuth.ClientId, ["redirect_uri"] = redirect,
            ["state"] = "xyz123", ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["scope"] = "mcp", ["resource"] = "", ["nonce"] = nonce, ["password"] = "not-the-password"
        };
        using (var wrongPassword = await http.PostAsync("/authorize", new FormUrlEncodedContent(fields)))
        {
            Check(wrongPassword.StatusCode == HttpStatusCode.Unauthorized, "a wrong password does not issue a code");
            fields["nonce"] = NonceField().Match(await wrongPassword.Content.ReadAsStringAsync()).Groups[1].Value;
        }
        fields["password"] = password;
        string code;
        using (var granted = await http.PostAsync("/authorize", new FormUrlEncodedContent(fields)))
        {
            Check(granted.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, "the correct password redirects back to the client");
            var location = granted.Headers.Location!;
            var parsed = QueryHelpers.ParseQuery(location.Query);
            code = parsed["code"].ToString();
            Check(location.GetLeftPart(UriPartial.Path) == redirect && parsed["state"].ToString() == "xyz123" && code.Length > 20,
                "the redirect carries a single-use code and the original state");
            // RFC 9207
            Check(parsed["iss"].ToString() == config.PublicUrl,
                "the authorization response carries the issuer identifier");
        }

        Dictionary<string, string> Exchange(string theCode, string theVerifier, string? secret = null) => new()
        {
            ["grant_type"] = "authorization_code", ["code"] = theCode, ["redirect_uri"] = redirect,
            ["client_id"] = config.OAuth.ClientId, ["client_secret"] = secret ?? config.OAuth.ClientSecret,
            ["code_verifier"] = theVerifier
        };

        using (var wrongSecret = await http.PostAsync("/token", new FormUrlEncodedContent(Exchange(code, verifier, "wrong-secret"))))
        {
            Check(wrongSecret.StatusCode == HttpStatusCode.Unauthorized, "a wrong client secret is rejected at the token endpoint");
            Check(JsonDocument.Parse(await wrongSecret.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString() == "invalid_client",
                "the token error body follows the OAuth error format");
        }
        using (var wrongVerifier = await http.PostAsync("/token", new FormUrlEncodedContent(Exchange(code, ServerConfig.NewSecret(48)))))
            Check(JsonDocument.Parse(await wrongVerifier.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString() == "invalid_grant",
                "a PKCE verifier that does not match the challenge is rejected");
        using (var reused = await http.PostAsync("/token", new FormUrlEncodedContent(Exchange(code, verifier))))
            Check(JsonDocument.Parse(await reused.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString() == "invalid_grant",
                "a code consumed by a failed exchange cannot be replayed");

        // A fresh authorization for the token that the rest of the checks use.
        string secondVerifier = ServerConfig.NewSecret(48);
        string secondChallenge = ServerConfig.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(secondVerifier)));
        string secondCode = await Authorize(http, config, password, redirect, secondChallenge);
        JsonElement tokens;
        using (var exchanged = await http.PostAsync("/token", new FormUrlEncodedContent(Exchange(secondCode, secondVerifier))))
        {
            Check(exchanged.StatusCode == HttpStatusCode.OK, "the authorization code exchanges for a token");
            tokens = JsonDocument.Parse(await exchanged.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        string access = tokens.GetProperty("access_token").GetString()!;
        Check(tokens.GetProperty("token_type").GetString() == "Bearer" &&
              tokens.GetProperty("expires_in").GetInt32() == config.OAuth.AccessTokenHours * 3600 &&
              tokens.GetProperty("refresh_token").GetString()!.Length > 20,
            "the token response carries a bearer access token, its lifetime and a refresh token");
        Check(runtime.Store.Token(Tokens.HashToken(access))!.PkceUsed, "the token row records that PKCE was used");
        Check(runtime.Store.Events("token_issued").Count >= 2, "token issuance is recorded in the events table");

        // Confidential client without PKCE: allowed only because the authorization request carried no challenge.
        string plainCode = await Authorize(http, config, password, redirect, null);
        using (var noPkce = await http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = plainCode, ["redirect_uri"] = redirect,
            ["client_id"] = config.OAuth.ClientId, ["client_secret"] = config.OAuth.ClientSecret
        })))
        {
            Check(noPkce.StatusCode == HttpStatusCode.OK, "a confidential client may omit PKCE when it never sent a challenge");
            var body = JsonDocument.Parse(await noPkce.Content.ReadAsStringAsync()).RootElement;
            Check(!runtime.Store.Token(Tokens.HashToken(body.GetProperty("access_token").GetString()!))!.PkceUsed,
                "the token row records that this grant used no PKCE");
        }

        // Client secret in an Authorization: Basic header instead of the form.
        string basicCode = await Authorize(http, config, password, redirect, null);
        using (var basic = new HttpRequestMessage(HttpMethod.Post, "/token"))
        {
            basic.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{config.OAuth.ClientId}:{config.OAuth.ClientSecret}")));
            basic.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = basicCode, ["redirect_uri"] = redirect
            });
            using var response = await http.SendAsync(basic);
            Check(response.StatusCode == HttpStatusCode.OK, "client_secret_basic authentication is accepted at the token endpoint");
        }
        return access;
    }

    private static async Task<string> Authorize(HttpClient http, ServerConfig config, string password, string redirect, string? challenge) =>
        QueryHelpers.ParseQuery((await SignIn(http, config, password, redirect, challenge)).Query)["code"].ToString();

    // The whole browser leg: the GET form, then the POST with its nonce and the password. Returns the redirect target.
    private static async Task<Uri> SignIn(HttpClient http, ServerConfig config, string password, string redirect, string? challenge,
        string? scope = "mcp", string? resource = null)
    {
        string query = $"/authorize?response_type=code&client_id={Uri.EscapeDataString(config.OAuth.ClientId)}" +
                       $"&redirect_uri={Uri.EscapeDataString(redirect)}&state=s" +
                       (scope is null ? "" : $"&scope={Uri.EscapeDataString(scope)}") +
                       (resource is null ? "" : $"&resource={Uri.EscapeDataString(resource)}") +
                       (challenge is null ? "" : $"&code_challenge={challenge}&code_challenge_method=S256");
        string form = await http.GetStringAsync(query);
        var fields = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = config.OAuth.ClientId, ["redirect_uri"] = redirect,
            ["state"] = "s", ["code_challenge"] = challenge ?? "", ["code_challenge_method"] = challenge is null ? "" : "S256",
            ["scope"] = scope ?? "", ["resource"] = resource ?? "", ["nonce"] = NonceField().Match(form).Groups[1].Value, ["password"] = password
        };
        using var granted = await http.PostAsync("/authorize", new FormUrlEncodedContent(fields));
        if (granted.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found) || granted.Headers.Location is null)
            throw new InvalidOperationException($"sign-in did not redirect: HTTP {(int)granted.StatusCode}");
        return granted.Headers.Location;
    }

    // Any https callback is accepted for the configured client. One outside the configured list never receives an
    // error redirect before the password is accepted, so /authorize is not an unauthenticated open redirect.
    private static async Task Redirects(HttpClient http, ServerConfig config, string password, List<string> rejected)
    {
        const string outside = "https://attacker.invalid/callback";
        string listed = config.OAuth.RedirectUris[0];
        string Query(string redirect, string responseType = "code") =>
            $"/authorize?response_type={responseType}&client_id={Uri.EscapeDataString(config.OAuth.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}&state=outside-state&scope=mcp";

        using (var early = await http.GetAsync(Query(outside, "token")))
            Check(early.StatusCode == HttpStatusCode.BadRequest && early.Headers.Location is null,
                "an error before the password is shown on the local page and never redirected to an unlisted callback");
        using (var method = await http.GetAsync(Query(outside) + "&code_challenge=abc&code_challenge_method=plain"))
            Check(method.StatusCode == HttpStatusCode.BadRequest && method.Headers.Location is null,
                "an unsupported PKCE method for an unlisted callback stays on the local page as well");
        using (var listedError = await http.GetAsync(Query(listed, "token")))
        {
            var target = listedError.Headers.Location;
            var parsed = target is null ? null : QueryHelpers.ParseQuery(target.Query);
            Check(listedError.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found && target!.GetLeftPart(UriPartial.Path) == listed &&
                  parsed!["error"].ToString() == "unsupported_response_type" && parsed["iss"].ToString() == config.PublicUrl,
                "a listed callback still receives an OAuth error redirect that carries iss");
        }

        string form = await http.GetStringAsync(Query(outside));
        Check(form.Contains("After sign-in you will return to <strong>attacker.invalid</strong>"),
            "the sign-in page names the host an unlisted https callback returns to");
        Check(rejected.Contains("accepted oauth redirect_uri outside configured list host=attacker.invalid"),
            "accepting an https callback outside the configured list is logged with its host");
        var fields = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = config.OAuth.ClientId, ["redirect_uri"] = outside, ["state"] = "outside-state",
            ["scope"] = "mcp", ["resource"] = "", ["code_challenge"] = "", ["code_challenge_method"] = "",
            ["nonce"] = NonceField().Match(form).Groups[1].Value, ["password"] = "not-the-password"
        };
        using (var wrong = await http.PostAsync("/authorize", new FormUrlEncodedContent(fields)))
        {
            Check(wrong.StatusCode == HttpStatusCode.Unauthorized && wrong.Headers.Location is null,
                "a wrong password for an unlisted callback redirects nowhere");
            fields["nonce"] = NonceField().Match(await wrong.Content.ReadAsStringAsync()).Groups[1].Value;
        }
        fields["password"] = password;
        string code;
        using (var granted = await http.PostAsync("/authorize", new FormUrlEncodedContent(fields)))
        {
            var target = granted.Headers.Location!;
            var parsed = QueryHelpers.ParseQuery(target.Query);
            code = parsed["code"].ToString();
            Check(granted.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found && target.GetLeftPart(UriPartial.Path) == outside &&
                  code.Length > 20 && parsed["state"].ToString() == "outside-state" && parsed["iss"].ToString() == config.PublicUrl,
                "after the password is accepted the code goes to the unlisted https callback, with state and iss");
        }
        Dictionary<string, string> Exchange(string redirect) => new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect,
            ["client_id"] = config.OAuth.ClientId, ["client_secret"] = config.OAuth.ClientSecret
        };
        using (var mismatch = await http.PostAsync("/token", new FormUrlEncodedContent(Exchange(listed))))
            Check(JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString() == "invalid_grant",
                "the token request still has to repeat the redirect_uri of the authorization request");
        string second = await Authorize(http, config, password, outside, null);
        code = second;
        using (var exchanged = await http.PostAsync("/token", new FormUrlEncodedContent(Exchange(outside))))
            Check(exchanged.StatusCode == HttpStatusCode.OK, "a code sent to an unlisted https callback exchanges with that same redirect_uri");
    }

    // A client_id that is present must match; without one, the secret alone authenticates the single configured
    // client. The secret itself is always required.
    private static async Task ClientAuthentication(HttpClient http, ServerConfig config, string password, List<string> rejected)
    {
        string redirect = config.OAuth.RedirectUris[0];
        async Task<HttpResponseMessage> Exchange(string code, string? clientId, string? secret, string? basic = null)
        {
            var body = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect };
            if (clientId is not null) body["client_id"] = clientId;
            if (secret is not null) body["client_secret"] = secret;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/token") { Content = new FormUrlEncodedContent(body) };
            if (basic is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basic)));
            return await http.SendAsync(request);
        }
        static async Task<string?> ErrorOf(HttpResponseMessage response) =>
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;

        using (var formOnly = await Exchange(await Authorize(http, config, password, redirect, null), null, config.OAuth.ClientSecret))
            Check(formOnly.StatusCode == HttpStatusCode.OK, "a token request without client_id authenticates with the client secret alone");
        using (var basicOnly = await Exchange(await Authorize(http, config, password, redirect, null), null, null, ":" + config.OAuth.ClientSecret))
            Check(basicOnly.StatusCode == HttpStatusCode.OK, "Basic credentials with an empty client_id authenticate with the secret alone");
        string code = await Authorize(http, config, password, redirect, null);
        using (var otherClient = await Exchange(code, "someone-else", config.OAuth.ClientSecret))
            Check(otherClient.StatusCode == HttpStatusCode.Unauthorized && await ErrorOf(otherClient) == "invalid_client",
                "a client_id that is present must still be this client's");
        using (var otherBasic = await Exchange(code, null, null, "someone-else:" + config.OAuth.ClientSecret))
            Check(otherBasic.StatusCode == HttpStatusCode.Unauthorized, "a Basic client_id that is present must still be this client's");
        using (var noSecret = await Exchange(code, config.OAuth.ClientId, null))
            Check(noSecret.StatusCode == HttpStatusCode.Unauthorized && await ErrorOf(noSecret) == "invalid_client" &&
                  rejected.Any(l => l.Contains("reason=missing_client_secret")),
                "the client secret stays mandatory");
        using (var accepted = await Exchange(code, config.OAuth.ClientId, config.OAuth.ClientSecret))
            Check(accepted.StatusCode == HttpStatusCode.OK, "refused client authentication does not consume the authorization code");
    }

    private static async Task<JsonElement> Rpc(HttpClient http, string access, string method, object parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", CodexishRuntime.ProtocolVersion);
        using var response = await http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {text}");
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = string.Join("\n", text.Split('\n').Where(l => l.StartsWith("data: ")).Select(l => l[6..].TrimEnd('\r')));
        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetProperty("result").Clone();
    }

    private static async Task Protocol(HttpClient http, string access, List<string> rejected)
    {
        var initialized = await Rpc(http, access, "initialize", new
        {
            protocolVersion = CodexishRuntime.ProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "codexish-self-test", version = CodexishRuntime.ServerVersion }
        });
        Check(initialized.GetProperty("protocolVersion").GetString() == CodexishRuntime.ProtocolVersion,
            "the MCP session negotiates the protocol version over HTTP with a bearer token");
        Check(initialized.GetProperty("instructions").GetString() == CodexishRuntime.Instructions,
            "initialize returns the harness instructions");
        var listed = await Rpc(http, access, "tools/list", new { });
        var tools = listed.GetProperty("tools").EnumerateArray().ToArray();
        string[] names = tools.Select(t => t.GetProperty("name").GetString()!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Check(names.Length == 24 && names.SequenceEqual(CodexishRuntime.ToolNames.OrderBy(n => n, StringComparer.Ordinal)),
            "tools/list exposes exactly the 24 coding and desktop tool names");
        Check(tools.All(t => t.GetProperty("inputSchema").GetProperty("type").GetString() == "object"),
            "every tool has an object input schema generated by the SDK");
        Check(tools.Single(t => t.GetProperty("name").GetString() == "fs_write")
            .GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean() == false,
            "fs_write is not annotated read-only");
        Check(tools.All(t => t.GetProperty("description").GetString()!.Contains("next tool", StringComparison.OrdinalIgnoreCase)),
            "every tool description names the next tool to call");
        if (Environment.GetEnvironmentVariable("CODEXISH_TEST_OUTPUT") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "v1-tools.json"), listed.GetRawText());
        }
        var failure = await Rpc(http, access, "tools/call", new { name = "fs_read", arguments = new { root_id = "proj", path = "../escape" } });
        Check(failure.GetProperty("isError").GetBoolean() &&
              failure.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == "OUTSIDE_WORKSPACE",
            "a tool error keeps isError true and the structured error code");
        var call = await Rpc(http, access, "tools/call", new { name = "host_capabilities", arguments = new { } });
        Check(call.GetProperty("structuredContent").GetProperty("execution_boundary").GetString() == "unconfined_user",
            "every result over HTTP carries the execution boundary");

        using var spoofed = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        spoofed.Headers.Host = "attacker.invalid";
        using var blocked = await http.SendAsync(spoofed);
        Check(blocked.StatusCode == HttpStatusCode.Forbidden, "an untrusted Host header is still rejected");
        using var cross = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        cross.Headers.Add("Origin", "https://attacker.invalid");
        using var crossBlocked = await http.SendAsync(cross);
        Check(crossBlocked.StatusCode == HttpStatusCode.Forbidden, "a cross-origin request is still rejected");
        Check(rejected.Any(l => l.StartsWith("rejected host=", StringComparison.Ordinal) && l.Contains("reason=")) &&
              rejected.All(l => !l.Contains('\n') && !l.Contains('\r')),
            "the Host and Origin rejection log keeps the P0 single-line format");
    }

    private static async Task Control(HttpClient http, CodexishRuntime runtime, ServerConfig config)
    {
        async Task<HttpStatusCode> Send(string path, string? token, string? host = null, HttpMethod? method = null)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path);
            if (token is not null) request.Headers.Add("X-Codexish-Control", token);
            if (host is not null) request.Headers.Host = host;
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }

        Check(await Send("/control/status", config.ControlToken, method: HttpMethod.Get) == HttpStatusCode.OK,
            "the loopback control API answers with the control token");
        Check(await Send("/control/status", null, method: HttpMethod.Get) == HttpStatusCode.Forbidden,
            "the control API refuses a request without the control token");
        Check(await Send("/control/status", "wrong-token", method: HttpMethod.Get) == HttpStatusCode.Forbidden,
            "the control API refuses a wrong control token");
        Check(await Send("/control/status", config.ControlToken, host: config.AllowHosts[0], method: HttpMethod.Get) == HttpStatusCode.Forbidden,
            "the control API is unreachable through the tunnel Host even with the right token");

        Check(await Send("/control/pause", config.ControlToken) == HttpStatusCode.OK, "pause is accepted from loopback");
        var tools = new CodexishTools(runtime);
        var held = await tools.FsWrite("proj", "paused.txt", "written after resume\n", "create", "pause-1");
        Check(Status(held) == "queued" && Data(held).GetProperty("paused").GetBoolean(),
            "a mutating call during a pause is accepted and queued, never rejected");
        Check(Status(tools.FsRead("proj", "readme.txt", null, null, null, null)) == "succeeded", "reads continue while paused");
        Check(!File.Exists(Path.Combine(config.Roots[0].Path, "paused.txt")), "the held write has not taken effect yet");
        Check(await Send("/control/resume", config.ControlToken) == HttpStatusCode.OK, "resume is accepted from loopback");
        for (int attempt = 0; attempt < 50 && !File.Exists(Path.Combine(config.Roots[0].Path, "paused.txt")); attempt++)
            await Task.Delay(100);
        Check(File.ReadAllText(Path.Combine(config.Roots[0].Path, "paused.txt")) == "written after resume\n",
            "the queued write completes by itself after resume");
        Check(Status(tools.OperationInspect("pause-1")) == "succeeded", "the held operation keeps its original id and result");

        if (Shells.Preferred is { } shell)
        {
            var child = await tools.ShellRun("proj", "control-child", command: Shells.Sleep(shell, 30), shell: shell,
                wait_ms: 300, lifetime: "session");
            int pid = Data(child).GetProperty("pid").GetInt32();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/control/kill-children");
            request.Headers.Add("X-Codexish-Control", config.ControlToken);
            using var response = await http.SendAsync(request);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Check(body.GetProperty("killed").GetInt32() >= 1, "kill-children reports the session processes it ended");
            bool gone = false;
            for (int attempt = 0; attempt < 30 && !gone; attempt++)
            {
                gone = !ProcessTests.Alive(pid);
                if (!gone) await Task.Delay(100);
            }
            Check(gone, "kill-children actually ended the running session child");
        }
        else Skip("kill-children check: no interpreter is available to start a session child");
    }

    private static async Task TokenLifecycle(HttpClient http, CodexishRuntime runtime, ServerConfig config, string access, string password)
    {
        string expired = ServerConfig.NewSecret();
        runtime.Store.InsertToken(new TokenRow(Tokens.HashToken(expired), "access", config.OAuth.ClientId,
            config.Resource, DateTimeOffset.UtcNow.AddMinutes(-5), false, true, "", Tokens.Scope));
        Check(await McpStatus(http, expired) == HttpStatusCode.Unauthorized, "an expired access token is refused");
        string foreign = ServerConfig.NewSecret();
        runtime.Store.InsertToken(new TokenRow(Tokens.HashToken(foreign), "access", config.OAuth.ClientId,
            "https://elsewhere.invalid/mcp", DateTimeOffset.UtcNow.AddHours(1), false, true, "", Tokens.Scope));
        Check(await McpStatus(http, foreign) == HttpStatusCode.Unauthorized, "a token minted for another resource is refused");
        Check(await McpStatus(http, ServerConfig.NewSecret()) == HttpStatusCode.Unauthorized, "an invented token is refused");

        string redirect = config.OAuth.RedirectUris[0];
        string verifier = ServerConfig.NewSecret(48);
        string challenge = ServerConfig.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        string code = await Authorize(http, config, password, redirect, challenge);
        JsonElement first;
        using (var exchanged = await http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect,
            ["client_id"] = config.OAuth.ClientId, ["client_secret"] = config.OAuth.ClientSecret, ["code_verifier"] = verifier
        })))
            first = JsonDocument.Parse(await exchanged.Content.ReadAsStringAsync()).RootElement.Clone();
        string refresh = first.GetProperty("refresh_token").GetString()!;
        string firstAccess = first.GetProperty("access_token").GetString()!;
        Check(runtime.Store.Token(Tokens.HashToken(refresh))!.ExpiresAt == DateTimeOffset.MaxValue,
            "with refresh_token_days 0 the refresh token is stored without an expiry");

        Dictionary<string, string> Refresh(string token) => new()
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = token,
            ["client_id"] = config.OAuth.ClientId, ["client_secret"] = config.OAuth.ClientSecret
        };
        async Task<(HttpStatusCode Status, JsonElement Body)> Refreshed(string token)
        {
            using var response = await http.PostAsync("/token", new FormUrlEncodedContent(Refresh(token)));
            return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
        }
        var renewed = await Refreshed(refresh);
        Check(renewed.Status == HttpStatusCode.OK && renewed.Body.GetProperty("refresh_token").GetString() == refresh &&
              renewed.Body.GetProperty("scope").GetString() == Tokens.Scope,
            "a refresh issues a new access token and hands the same refresh token back");
        string renewedAccess = renewed.Body.GetProperty("access_token").GetString()!;
        Check(renewedAccess != firstAccess && await McpStatus(http, renewedAccess) == HttpStatusCode.OK &&
              await McpStatus(http, firstAccess) == HttpStatusCode.OK,
            "the new access token works and the earlier one is not revoked by the refresh");
        string family = runtime.Store.Token(Tokens.HashToken(renewedAccess))!.Family;
        Check(family.Length > 0 && family == runtime.Store.Token(Tokens.HashToken(refresh))!.Family,
            "a refreshed access token stays in the family of the authorization that created it");
        var again = await Refreshed(refresh);
        Check(again.Status == HttpStatusCode.OK && again.Body.GetProperty("refresh_token").GetString() == refresh &&
              await McpStatus(http, again.Body.GetProperty("access_token").GetString()!) == HttpStatusCode.OK &&
              await McpStatus(http, renewedAccess) == HttpStatusCode.OK,
            "presenting the same refresh token again, as after a lost response, keeps working and revokes nothing");
        Check(runtime.Store.Events("token_family_revoked").Count == 0, "no refresh revokes a token family");

        // A refresh token stored by the earlier rotating scheme: a finite expiry now in the past, and no scope column value.
        string legacy = ServerConfig.NewSecret();
        runtime.Store.InsertToken(new TokenRow(Tokens.HashToken(legacy), "refresh", config.OAuth.ClientId, config.Resource,
            DateTimeOffset.UtcNow.AddDays(-1), false, true, "legacy-family", ""));
        var legacyRefresh = await Refreshed(legacy);
        Check(legacyRefresh.Status == HttpStatusCode.OK && legacyRefresh.Body.GetProperty("refresh_token").GetString() == legacy &&
              await McpStatus(http, legacyRefresh.Body.GetProperty("access_token").GetString()!) == HttpStatusCode.OK,
            "a refresh token stored under the rotating scheme keeps working, past its old expiry and without a stored scope");

        // A positive refresh_token_days already in a file is honored, counted from the token's last refresh.
        config.OAuth.RefreshTokenDays = 30;
        try
        {
            string lapsed = ServerConfig.NewSecret(), active = ServerConfig.NewSecret();
            runtime.Store.InsertToken(new TokenRow(Tokens.HashToken(lapsed), "refresh", config.OAuth.ClientId, config.Resource,
                DateTimeOffset.UtcNow.AddMinutes(-1), false, true, "positive", Tokens.Scope));
            runtime.Store.InsertToken(new TokenRow(Tokens.HashToken(active), "refresh", config.OAuth.ClientId, config.Resource,
                DateTimeOffset.UtcNow.AddDays(1), false, true, "positive", Tokens.Scope));
            var refused = await Refreshed(lapsed);
            Check(refused.Status == HttpStatusCode.BadRequest && refused.Body.GetProperty("error").GetString() == "invalid_grant",
                "a positive refresh_token_days is honored: a refresh token past its expiry is refused");
            var extended = await Refreshed(active);
            Check(extended.Status == HttpStatusCode.OK && extended.Body.GetProperty("refresh_token").GetString() == active &&
                  runtime.Store.Token(Tokens.HashToken(active))!.ExpiresAt > DateTimeOffset.UtcNow.AddDays(29),
                "each refresh moves a positive expiry to a full lifetime from now");
        }
        finally { config.OAuth.RefreshTokenDays = 0; }

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/control/revoke-tokens"))
        {
            request.Headers.Add("X-Codexish-Control", config.ControlToken);
            using var response = await http.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.OK, "revoke-tokens is accepted from loopback");
        }
        Check(await McpStatus(http, renewedAccess) == HttpStatusCode.Unauthorized, "every issued token is 401 after revoke-tokens");
        Check(await McpStatus(http, access) == HttpStatusCode.Unauthorized, "the session's original token is revoked too");
        var afterRevoke = await Refreshed(refresh);
        Check(afterRevoke.Status == HttpStatusCode.BadRequest && afterRevoke.Body.GetProperty("error").GetString() == "invalid_grant",
            "revoke-tokens also ends the refresh token that never expires");
    }

    private static async Task<HttpStatusCode> McpStatus(HttpClient http, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = Guid.NewGuid().ToString("N"),
                method = "initialize",
                @params = new { protocolVersion = CodexishRuntime.ProtocolVersion, capabilities = new { }, clientInfo = new { name = "probe", version = "1" } }
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", CodexishRuntime.ProtocolVersion);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }
}
