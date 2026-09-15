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
            await Protocol(http, access, rejected);
            await Control(http, runtime, config);
            await TokenLifecycle(http, runtime, config, access, password);
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
        Directory.Delete(temp, true);
    }

    private static async Task Metadata(HttpClient http, ServerConfig config)
    {
        var resource = JsonDocument.Parse(await http.GetStringAsync("/.well-known/oauth-protected-resource")).RootElement;
        Check(resource.GetProperty("resource").GetString() == config.PublicUrl + "/mcp" &&
              resource.GetProperty("authorization_servers").EnumerateArray().First().GetString() == config.PublicUrl,
            "the protected resource document names this server's MCP endpoint and authorization server");
        var server = JsonDocument.Parse(await http.GetStringAsync("/.well-known/oauth-authorization-server")).RootElement;
        Check(server.GetProperty("issuer").GetString() == config.PublicUrl &&
              server.GetProperty("authorization_endpoint").GetString() == config.PublicUrl + "/authorize" &&
              server.GetProperty("token_endpoint").GetString() == config.PublicUrl + "/token",
            "the authorization server document publishes the issuer and both endpoints");
        Check(server.GetProperty("code_challenge_methods_supported").EnumerateArray().Single().GetString() == "S256" &&
              server.GetProperty("grant_types_supported").EnumerateArray().Select(g => g.GetString()).OrderBy(g => g)
                  .SequenceEqual(["authorization_code", "refresh_token"]),
            "the metadata advertises S256 PKCE and the two supported grants");
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

        using (var wrongRedirect = await http.GetAsync($"/authorize?response_type=code&client_id={config.OAuth.ClientId}" +
            "&redirect_uri=https://attacker.invalid/callback&state=xyz123"))
        {
            Check(wrongRedirect.StatusCode == HttpStatusCode.BadRequest, "an unregistered redirect_uri is refused before any code is issued");
            Check(rejected.Any(l => l.Contains("reason=redirect_uri_mismatch") && l.Contains("attacker.invalid")),
                "the offered redirect_uri is logged so the user can add the connector's real callback");
        }
        using (var wrongClient = await http.GetAsync($"/authorize?response_type=code&client_id=someone-else&redirect_uri={Uri.EscapeDataString(redirect)}"))
            Check(wrongClient.StatusCode == HttpStatusCode.BadRequest, "an unknown client_id is refused");

        string form = await http.GetStringAsync(query);
        Check(form.Contains($"value=\"{challenge}\"") && form.Contains("value=\"xyz123\"") && form.Contains(WebUtility.HtmlEncode(redirect)),
            "the login form carries every OAuth parameter into the POST as hidden fields");
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

    private static async Task<string> Authorize(HttpClient http, ServerConfig config, string password, string redirect, string? challenge)
    {
        string query = $"/authorize?response_type=code&client_id={Uri.EscapeDataString(config.OAuth.ClientId)}" +
                       $"&redirect_uri={Uri.EscapeDataString(redirect)}&state=s&scope=mcp" +
                       (challenge is null ? "" : $"&code_challenge={challenge}&code_challenge_method=S256");
        string form = await http.GetStringAsync(query);
        var fields = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = config.OAuth.ClientId, ["redirect_uri"] = redirect,
            ["state"] = "s", ["code_challenge"] = challenge ?? "", ["code_challenge_method"] = challenge is null ? "" : "S256",
            ["scope"] = "mcp", ["resource"] = "", ["nonce"] = NonceField().Match(form).Groups[1].Value, ["password"] = password
        };
        using var granted = await http.PostAsync("/authorize", new FormUrlEncodedContent(fields));
        return QueryHelpers.ParseQuery(granted.Headers.Location!.Query)["code"].ToString();
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
        Check(names.Length == 21 && names.SequenceEqual(CodexishRuntime.ToolNames.OrderBy(n => n, StringComparer.Ordinal)),
            "tools/list exposes exactly the 21 slice-1 tool names");
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

        if (OperatingSystem.IsWindows())
        {
            var child = await tools.ShellRun("proj", "control-child", command: "ping -n 30 127.0.0.1 >nul", shell: "cmd",
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
                try { using var process = System.Diagnostics.Process.GetProcessById(pid); gone = process.HasExited; }
                catch (ArgumentException) { gone = true; }
                if (!gone) await Task.Delay(100);
            }
            Check(gone, "kill-children actually ended the running session child");
        }
    }

    private static async Task TokenLifecycle(HttpClient http, CodexishRuntime runtime, ServerConfig config, string access, string password)
    {
        string expired = ServerConfig.NewSecret();
        runtime.Store.InsertToken(new TokenRow(Tokens.HashToken(expired), "access", config.OAuth.ClientId,
            config.Resource, DateTimeOffset.UtcNow.AddMinutes(-5), false, true));
        Check(await McpStatus(http, expired) == HttpStatusCode.Unauthorized, "an expired access token is refused");
        string foreign = ServerConfig.NewSecret();
        runtime.Store.InsertToken(new TokenRow(Tokens.HashToken(foreign), "access", config.OAuth.ClientId,
            "https://elsewhere.invalid/mcp", DateTimeOffset.UtcNow.AddHours(1), false, true));
        Check(await McpStatus(http, foreign) == HttpStatusCode.Unauthorized, "a token minted for another resource is refused");
        Check(await McpStatus(http, ServerConfig.NewSecret()) == HttpStatusCode.Unauthorized, "an invented token is refused");

        // Delta 9: an explicit resource indicator must name this server; an absent one defaults to it.
        using (var wrongResource = await http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = "whatever", ["resource"] = "https://elsewhere.invalid/mcp",
            ["client_id"] = config.OAuth.ClientId, ["client_secret"] = config.OAuth.ClientSecret
        })))
            Check(JsonDocument.Parse(await wrongResource.Content.ReadAsStringAsync()).RootElement
                .GetProperty("error").GetString() == "invalid_target",
                "a resource indicator that is not this server's MCP endpoint is refused at the token endpoint");

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

        Dictionary<string, string> Refresh(string token) => new()
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = token,
            ["client_id"] = config.OAuth.ClientId, ["client_secret"] = config.OAuth.ClientSecret
        };
        JsonElement rotated;
        using (var response = await http.PostAsync("/token", new FormUrlEncodedContent(Refresh(refresh))))
        {
            Check(response.StatusCode == HttpStatusCode.OK, "a refresh token exchanges for a new access token");
            rotated = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        using (var replay = await http.PostAsync("/token", new FormUrlEncodedContent(Refresh(refresh))))
            Check(JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString() == "invalid_grant",
                "refresh rotation revokes the presented refresh token");
        string rotatedAccess = rotated.GetProperty("access_token").GetString()!;
        Check(await McpStatus(http, rotatedAccess) == HttpStatusCode.OK, "the rotated access token works on /mcp");

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/control/revoke-tokens"))
        {
            request.Headers.Add("X-Codexish-Control", config.ControlToken);
            using var response = await http.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.OK, "revoke-tokens is accepted from loopback");
        }
        Check(await McpStatus(http, rotatedAccess) == HttpStatusCode.Unauthorized, "every issued token is 401 after revoke-tokens");
        Check(await McpStatus(http, access) == HttpStatusCode.Unauthorized, "the session's original token is revoked too");
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
