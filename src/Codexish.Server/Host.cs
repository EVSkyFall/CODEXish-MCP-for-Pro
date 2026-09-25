using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace Codexish.Server;

public static class CodexishHost
{
    public static WebApplication Build(CodexishRuntime runtime, int port, Action<string>? rejectionLog = null,
        bool instructions = true)
    {
        var config = runtime.Config;
        var access = new AccessPolicy(config.AllowHosts, config.AllowOrigins);
        var log = rejectionLog ?? Console.WriteLine;
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton(runtime.Desktop);
        // Mounted browser tools are listed and dispatched per request, so a mount that connects, reconnects or is still
        // starting changes the tool list without rebuilding the host.
        builder.Services.AddMcpServer(o =>
        {
            o.ServerInfo = new() { Name = "CODEXish", Version = CodexishRuntime.ServerVersion };
            o.ServerInstructions = instructions ? CodexishRuntime.Instructions : null;
        }).WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
          .WithTools<CodexishTools>().WithTools<DesktopTools>()
          .WithListToolsHandler((_, _) => ValueTask.FromResult(new ModelContextProtocol.Protocol.ListToolsResult { Tools = runtime.Browsers.Tools.Select(t => t.ProtocolTool).ToList() }))
          .WithCallToolHandler((request, token) => runtime.Browsers.Find(request.Params?.Name) is { } tool
              ? tool.InvokeAsync(request, token)
              : throw new ModelContextProtocol.McpProtocolException($"Unknown tool: '{request.Params?.Name}'", ModelContextProtocol.McpErrorCode.InvalidParams));
        var app = builder.Build();
        // Mounts connect only once Kestrel listens, each in the background, so none can delay or stop the host.
        app.Lifetime.ApplicationStarted.Register(runtime.Browsers.Start);
        // In-flight browser calls are cancelled as soon as the host starts stopping, so its request drain is not held
        // by a backend that never answers.
        app.Lifetime.ApplicationStopping.Register(runtime.Browsers.Stop);

        app.Use(async (context, next) =>
        {
            string? reason = access.RejectionReason(context.Request);
            if (reason is not null)
            {
                // JSON escaping prevents untrusted header values from forging extra log lines.
                log($"rejected host={JsonSerializer.Serialize(context.Request.Host.Value)} " +
                    $"origin={JsonSerializer.Serialize(context.Request.Headers.Origin.ToString())} reason={reason}");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });

        // D12: bearer required on /mcp. The 401 points the client at the protected-resource metadata.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/mcp") || runtime.AuthDisabled)
            {
                await next(context);
                return;
            }
            string header = context.Request.Headers.Authorization.ToString();
            string challenge = $"Bearer resource_metadata=\"{Base(runtime, context.Request)}/.well-known/oauth-protected-resource\"";
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                log($"rejected mcp reason=missing_bearer host={JsonSerializer.Serialize(context.Request.Host.Value)}");
                context.Response.Headers.WWWAuthenticate = challenge;
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var (row, reason) = runtime.Tokens.Validate(header[7..].Trim(), "access", Audience(runtime, context.Request));
            if (row is null)
            {
                log($"rejected mcp reason={reason}");
                context.Response.Headers.WWWAuthenticate = challenge + $", error=\"invalid_token\", error_description=\"{reason}\"";
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });

        app.MapGet("/healthz", () => new
        {
            status = "codexish_v1_slice4",
            authentication = runtime.AuthDisabled ? "disabled" : "oauth_bearer",
            binding = "loopback_only",
            paused = runtime.Ledger.Paused
        });

        MapMetadata(app, runtime);
        MapAuthorize(app, runtime, log);
        MapToken(app, runtime, log);
        MapControl(app, runtime, log);
        app.MapMcp("/mcp");
        return app;
    }

    private static string Base(CodexishRuntime runtime, HttpRequest request) =>
        runtime.Config.PublicUrl.Length > 0 ? runtime.Config.PublicUrl : $"{request.Scheme}://{request.Host.Value}";

    private static string Audience(CodexishRuntime runtime, HttpRequest request) => Base(runtime, request) + "/mcp";

    private static void MapMetadata(WebApplication app, CodexishRuntime runtime)
    {
        Func<HttpRequest, IResult> protectedResource = request => Results.Json(new
        {
            resource = Audience(runtime, request),
            authorization_servers = new[] { Base(runtime, request) },
            scopes_supported = new[] { "mcp" },
            bearer_methods_supported = new[] { "header" }
        });
        app.MapGet("/.well-known/oauth-protected-resource", protectedResource);
        // RFC 9728 puts the resource's path after the well-known name, and a client may ask for that form first.
        app.MapGet("/.well-known/oauth-protected-resource/mcp", protectedResource);

        app.MapGet("/.well-known/oauth-authorization-server", (HttpRequest request) => Results.Json(new
        {
            issuer = Base(runtime, request),
            authorization_endpoint = Base(runtime, request) + "/authorize",
            token_endpoint = Base(runtime, request) + "/token",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" },
            scopes_supported = new[] { "mcp" },
            // Every authorization redirect carries iss (RFC 9207). Without this flag ChatGPT registers a
            // per-connection callback instead of its stable one.
            authorization_response_iss_parameter_supported = true
        }));
    }

    // Listed: the redirect_uri is in oauth.redirect_uris. Any other https callback is accepted as well, but it is
    // never sent an error redirect before the password has been accepted.
    private sealed record AuthorizeRequest(string ClientId, string RedirectUri, bool Listed, string? State,
        string? Challenge, string? ChallengeMethod, string? Scope, string? Resource);

    private static void MapAuthorize(WebApplication app, CodexishRuntime runtime, Action<string> log)
    {
        app.MapGet("/authorize", (HttpRequest request) =>
        {
            var query = request.Query;
            var (parsed, failure) = Parse(runtime, Base(runtime, request), query["response_type"], query["client_id"],
                query["redirect_uri"], query["state"], query["code_challenge"], query["code_challenge_method"],
                query["scope"], query["resource"], log);
            if (failure is not null) return failure;
            string nonce = runtime.Tokens.IssueNonce();
            return Results.Content(LoginForm(parsed!, nonce, null), "text/html; charset=utf-8");
        });

        app.MapPost("/authorize", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "invalid_request" });
            var form = await request.ReadFormAsync();
            // Every parameter is revalidated on POST; the hidden fields are not trusted to have stayed constant.
            var (parsed, failure) = Parse(runtime, Base(runtime, request), form["response_type"], form["client_id"],
                form["redirect_uri"], form["state"], form["code_challenge"], form["code_challenge_method"],
                form["scope"], form["resource"], log);
            if (failure is not null) return failure;
            if (!runtime.Tokens.ConsumeNonce(form["nonce"]))
            {
                log("rejected oauth stage=authorize reason=stale_or_missing_form_nonce");
                string retryNonce = runtime.Tokens.IssueNonce();
                return Results.Content(LoginForm(parsed!, retryNonce, "This form expired. Sign in again."),
                    "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
            }
            if (ServerConfig.PasswordHashProblem(runtime.Config.OAuth.PasswordHash) is { } problem)
            {
                log($"rejected oauth stage=authorize reason=malformed_password_hash detail={problem}");
                runtime.Store.Event("oauth_rejected", "authorize", new { reason = "malformed_password_hash", detail = problem });
                string retryNonce = runtime.Tokens.IssueNonce();
                return Results.Content(LoginForm(parsed!, retryNonce,
                    "The password hash stored in codexish.json is damaged, so no password can sign in. Write a new configuration with --init or the tray setup."),
                    "text/html; charset=utf-8", statusCode: StatusCodes.Status401Unauthorized);
            }
            if (!ServerConfig.VerifyPassword(form["password"].ToString(), runtime.Config.OAuth.PasswordHash))
            {
                log("rejected oauth stage=authorize reason=wrong_password");
                runtime.Store.Event("oauth_rejected", "authorize", new { reason = "wrong_password" });
                string retryNonce = runtime.Tokens.IssueNonce();
                return Results.Content(LoginForm(parsed!, retryNonce, "Incorrect password."),
                    "text/html; charset=utf-8", statusCode: StatusCodes.Status401Unauthorized);
            }
            string code = runtime.Tokens.IssueCode(parsed!.ClientId, parsed.RedirectUri, parsed.Challenge,
                parsed.Resource ?? Audience(runtime, request));
            runtime.Store.Event("oauth_code_issued", parsed.ClientId,
                new { redirect_uri = parsed.RedirectUri, listed = parsed.Listed, pkce = parsed.Challenge is not null });
            var target = new StringBuilder(parsed.RedirectUri);
            target.Append(parsed.RedirectUri.Contains('?') ? '&' : '?');
            target.Append("code=").Append(Uri.EscapeDataString(code));
            if (parsed.State is { Length: > 0 }) target.Append("&state=").Append(Uri.EscapeDataString(parsed.State));
            // RFC 9207: the issuer travels with the response so a client cannot be tricked into sending the
            // code to a different authorization server's token endpoint.
            target.Append("&iss=").Append(Uri.EscapeDataString(Base(runtime, request)));
            return Results.Redirect(target.ToString());
        });
    }

    private static (AuthorizeRequest?, IResult?) Parse(CodexishRuntime runtime, string issuer, string? responseType,
        string? clientId, string? redirectUri, string? state, string? challenge, string? challengeMethod, string? scope,
        string? resource, Action<string> log)
    {
        var oauth = runtime.Config.OAuth;
        // client_id and redirect_uri are validated before anything is redirected anywhere.
        if (string.IsNullOrEmpty(clientId) || !string.Equals(clientId, oauth.ClientId, StringComparison.Ordinal))
        {
            log($"rejected oauth stage=authorize reason=unknown_client offered_client_id={JsonSerializer.Serialize(clientId ?? "")}");
            return (null, Results.Content(ErrorPage("Unknown client_id. Check the connector configuration."),
                "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest));
        }
        bool listed = !string.IsNullOrEmpty(redirectUri) && oauth.RedirectUris.Contains(redirectUri, StringComparer.Ordinal);
        if (!listed && !IsHttpsCallback(redirectUri))
        {
            // The offered value is logged so a callback that is not https can be listed with --redirect-uri.
            log($"rejected oauth stage=authorize reason=redirect_uri_mismatch offered_redirect_uri={JsonSerializer.Serialize(redirectUri ?? "")}");
            runtime.Store.Event("oauth_rejected", "authorize", new { reason = "redirect_uri_mismatch", offered = redirectUri });
            return (null, Results.Content(ErrorPage(
                "redirect_uri must be an absolute https URI without a fragment. The offered value was logged; a callback that is not https has to be listed with --redirect-uri when you run --init."),
                "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest));
        }
        // A listed callback receives the remaining errors as OAuth error redirects. Any other https callback sees them
        // on the local error page: redirecting there before the password is accepted would make /authorize an
        // unauthenticated open redirect.
        IResult Fail(string error, string description) => listed
            ? RedirectError(issuer, redirectUri!, state, error, description)
            : Results.Content(ErrorPage(description), "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
        if (!string.Equals(responseType, "code", StringComparison.Ordinal))
        {
            log("rejected oauth stage=authorize reason=unsupported_response_type");
            return (null, Fail("unsupported_response_type", "Only response_type=code is supported."));
        }
        if (!string.IsNullOrEmpty(challenge) && !string.Equals(challengeMethod, "S256", StringComparison.Ordinal))
        {
            log("rejected oauth stage=authorize reason=unsupported_code_challenge_method");
            return (null, Fail("invalid_request", "code_challenge_method must be S256."));
        }
        if (!listed) log($"accepted oauth redirect_uri outside configured list host={Destination(redirectUri!)}");
        // Neither the requested scope nor the resource indicator refuses a request: the grant is always mcp for
        // <public_url>/mcp.
        NoteResource(resource, issuer, log);
        return (new AuthorizeRequest(clientId, redirectUri!, listed, state, string.IsNullOrEmpty(challenge) ? null : challenge,
            challengeMethod, scope, string.IsNullOrEmpty(resource) ? null : resource), null);
    }

    private static bool IsHttpsCallback(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Fragment.Length == 0;

    // Punycode rather than Unicode, so a look-alike name cannot pass for the host it imitates.
    private static string Destination(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)) return redirectUri;
        string host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
    }

    private static void NoteResource(string? resource, string origin, Action<string> log)
    {
        if (string.IsNullOrEmpty(resource)) return;
        if (Uri.TryCreate(resource, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme + "://" + uri.Authority, origin, StringComparison.OrdinalIgnoreCase)) return;
        log($"oauth resource differs from public origin value={JsonSerializer.Serialize(resource)}");
    }

    private static IResult RedirectError(string issuer, string redirectUri, string? state, string error, string description)
    {
        var target = new StringBuilder(redirectUri);
        target.Append(redirectUri.Contains('?') ? '&' : '?');
        target.Append("error=").Append(Uri.EscapeDataString(error));
        target.Append("&error_description=").Append(Uri.EscapeDataString(description));
        if (state is { Length: > 0 }) target.Append("&state=").Append(Uri.EscapeDataString(state));
        target.Append("&iss=").Append(Uri.EscapeDataString(issuer));
        return Results.Redirect(target.ToString());
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    // Delta 3: every incoming parameter is carried into the POST as a hidden field and revalidated there,
    // so the issued code stays bound to the caller that started the flow.
    private static string LoginForm(AuthorizeRequest request, string nonce, string? message) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>CODEXish sign in</title>
        <style>body{font:16px system-ui;margin:0;display:grid;place-items:center;min-height:100vh;background:#f6f6f7;color:#18181b}
        form{background:#fff;padding:24px;border-radius:12px;box-shadow:0 1px 4px rgba(0,0,0,.12);width:min(360px,92vw)}
        h1{font-size:18px;margin:0 0 4px}p{margin:0 0 16px;color:#52525b;font-size:13px}
        input[type=password]{width:100%;padding:10px;border:1px solid #d4d4d8;border-radius:8px;font-size:15px;box-sizing:border-box}
        button{margin-top:12px;width:100%;padding:10px;border:0;border-radius:8px;background:#18181b;color:#fff;font-size:15px}
        .dest{margin:0 0 16px;padding:10px 12px;border:1px solid #d4d4d8;border-radius:8px;background:#fafafa;font-size:13px;color:#3f3f46}
        .dest strong{display:block;margin-top:2px;font-size:18px;color:#18181b;overflow-wrap:anywhere}
        .err{color:#b91c1c;font-size:13px;margin-bottom:12px}</style></head><body>
        <form method="post" action="/authorize">
        <h1>CODEXish</h1><p>Sign in to connect this Windows PC.</p>
        <div class="dest">After sign-in you will return to <strong>{{Encode(Destination(request.RedirectUri))}}</strong></div>
        {{Message(message)}}
        <input type="password" name="password" autocomplete="current-password" autofocus required>
        <input type="hidden" name="response_type" value="code">
        <input type="hidden" name="client_id" value="{{Encode(request.ClientId)}}">
        <input type="hidden" name="redirect_uri" value="{{Encode(request.RedirectUri)}}">
        <input type="hidden" name="state" value="{{Encode(request.State)}}">
        <input type="hidden" name="code_challenge" value="{{Encode(request.Challenge)}}">
        <input type="hidden" name="code_challenge_method" value="{{Encode(request.ChallengeMethod)}}">
        <input type="hidden" name="scope" value="{{Encode(request.Scope)}}">
        <input type="hidden" name="resource" value="{{Encode(request.Resource)}}">
        <input type="hidden" name="nonce" value="{{Encode(nonce)}}">
        <button type="submit">Sign in</button></form></body></html>
        """;

    private static string Message(string? message) =>
        message is null ? "" : $"<div class=\"err\">{Encode(message)}</div>";

    private static string ErrorPage(string message) => $"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>CODEXish</title></head>
        <body style="font:16px system-ui;padding:24px"><h1 style="font-size:18px">Authorization refused</h1>
        <p>{Encode(message)}</p></body></html>
        """;

    private static void MapToken(WebApplication app, CodexishRuntime runtime, Action<string> log)
    {
        app.MapPost("/token", async (HttpRequest request) =>
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!request.HasFormContentType) return TokenError("invalid_request", "Use application/x-www-form-urlencoded.");
            var form = await request.ReadFormAsync();
            var oauth = runtime.Config.OAuth;
            if (!ClientAuthenticated(request, form, oauth, out string clientReason))
            {
                log($"rejected oauth stage=token reason={clientReason}");
                runtime.Store.Event("oauth_rejected", "token", new { reason = clientReason });
                return TokenError("invalid_client", "Client authentication failed.", StatusCodes.Status401Unauthorized);
            }
            // Tokens are always issued for this server's MCP endpoint; a resource indicator is never a reason to refuse.
            string audience = Audience(runtime, request);
            NoteResource(form["resource"].ToString(), Base(runtime, request), log);
            string grant = form["grant_type"].ToString();
            if (grant == "authorization_code")
            {
                var code = runtime.Tokens.ConsumeCode(form["code"].ToString());
                if (code is null)
                {
                    log("rejected oauth stage=token reason=unknown_or_used_code");
                    return TokenError("invalid_grant", "The authorization code is unknown, expired or already used.");
                }
                if (!string.Equals(code.RedirectUri, form["redirect_uri"].ToString(), StringComparison.Ordinal))
                {
                    log("rejected oauth stage=token reason=redirect_uri_mismatch");
                    return TokenError("invalid_grant", "redirect_uri does not match the authorization request.");
                }
                if (!string.Equals(code.ClientId, oauth.ClientId, StringComparison.Ordinal))
                    return TokenError("invalid_grant", "The code was issued to a different client.");
                // Delta 8: PKCE is mandatory exactly when the authorization request carried a challenge.
                if (!Tokens.VerifyPkce(code.Challenge, form["code_verifier"].ToString()))
                {
                    log("rejected oauth stage=token reason=pkce_verification_failed");
                    return TokenError("invalid_grant", "code_verifier does not match the code_challenge.");
                }
                return Issue(runtime, oauth, audience, code.Challenge is not null, Tokens.NewFamily(), null);
            }
            if (grant == "refresh_token")
            {
                string refresh = form["refresh_token"].ToString();
                var (row, reason) = runtime.Tokens.Validate(refresh, "refresh", audience);
                if (row is null)
                {
                    log($"rejected oauth stage=token reason=refresh_{reason}");
                    return TokenError("invalid_grant", $"The refresh token is {reason}.");
                }
                // Neither rotated nor consumed: rotation's replay revocation disconnected the connector whenever a
                // refresh response was lost in the tunnel or two refreshes raced. The client secret required above
                // keeps a leaked refresh token useless on its own.
                runtime.Tokens.Renew(refresh);
                return Issue(runtime, oauth, audience, row.PkceUsed, row.Family, refresh);
            }
            return TokenError("unsupported_grant_type", "Use authorization_code or refresh_token.");
        });
    }

    // A refresh grant hands back the refresh token it presented; only the authorization code grant creates one.
    private static IResult Issue(CodexishRuntime runtime, OAuthConfig oauth, string audience, bool pkceUsed,
        string family, string? refresh)
    {
        var now = DateTimeOffset.UtcNow;
        var accessLifetime = TimeSpan.FromHours(oauth.AccessTokenHours);
        string access = runtime.Tokens.Issue("access", oauth.ClientId, audience, now + accessLifetime, pkceUsed, family);
        refresh ??= runtime.Tokens.Issue("refresh", oauth.ClientId, audience, Tokens.RefreshExpiry(oauth, now), pkceUsed, family);
        return Results.Json(new
        {
            access_token = access,
            token_type = "Bearer",
            expires_in = (int)accessLifetime.TotalSeconds,
            refresh_token = refresh,
            scope = Tokens.Scope
        });
    }

    private static IResult TokenError(string error, string description, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error, error_description = description }, statusCode: status);

    // A client_id that is present, in the form or in Basic credentials, must be this client's. Without one, the secret
    // alone identifies the single configured client. The secret is always required.
    private static bool ClientAuthenticated(HttpRequest request, IFormCollection form, OAuthConfig oauth, out string reason)
    {
        string? basicId = null;
        string secret;
        string header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())); }
            catch (FormatException) { reason = "malformed_basic_credentials"; return false; }
            int separator = decoded.IndexOf(':');
            if (separator < 0) { reason = "malformed_basic_credentials"; return false; }
            basicId = Uri.UnescapeDataString(decoded[..separator]);
            secret = Uri.UnescapeDataString(decoded[(separator + 1)..]);
        }
        else secret = form["client_secret"].ToString();
        foreach (string? offered in new[] { basicId, form["client_id"].ToString() })
            if (!string.IsNullOrEmpty(offered) && !string.Equals(offered, oauth.ClientId, StringComparison.Ordinal))
            {
                reason = "unknown_client";
                return false;
            }
        if (secret.Length == 0) { reason = "missing_client_secret"; return false; }
        if (!Tokens.SecretMatches(oauth.ClientSecret, secret)) { reason = "wrong_client_secret"; return false; }
        reason = "ok";
        return true;
    }

    // D11. Never reachable through the tunnel Host, never an MCP tool.
    private static void MapControl(WebApplication app, CodexishRuntime runtime, Action<string> log)
    {
        bool Allowed(HttpContext context)
        {
            if (!AccessPolicy.IsLoopbackControl(context))
            {
                log($"rejected control reason=not_loopback host={JsonSerializer.Serialize(context.Request.Host.Value)}");
                return false;
            }
            string offered = context.Request.Headers["X-Codexish-Control"].ToString();
            if (!Tokens.SecretMatches(runtime.Config.ControlToken, offered))
            {
                log("rejected control reason=bad_control_token");
                return false;
            }
            return true;
        }

        IResult Guard(HttpContext context, Func<object> action) =>
            Allowed(context) ? Results.Json(action()) : Results.StatusCode(StatusCodes.Status403Forbidden);

        app.MapPost("/control/pause", (HttpContext context) => Guard(context, () =>
        {
            runtime.Ledger.Pause();
            return new { paused = true, note = "Mutating invocations are still accepted and queued; reads continue." };
        }));
        app.MapPost("/control/resume", (HttpContext context) => Guard(context, () =>
        {
            runtime.Ledger.Resume();
            return new { paused = false, note = "Queued work resumes in acceptance order." };
        }));
        app.MapPost("/control/kill-children", (HttpContext context) => Guard(context, () =>
            new { killed = runtime.Processes.KillSessionChildren(), scope = "lifetime=session processes started by this server" }));
        app.MapPost("/control/revoke-tokens", (HttpContext context) => Guard(context, () =>
            new { revoked = runtime.Tokens.RevokeAll(), note = "Every issued access and refresh token is now invalid." }));
        app.MapGet("/control/status", (HttpContext context) => Guard(context, () => new
        {
            paused = runtime.Ledger.Paused,
            processes = runtime.Processes.Live.Select(runtime.Processes.Describe),
            roots = runtime.Config.Roots.Select(r => new { r.Id, r.Path, r.Read, r.Write, r.Shell }),
            state_dir = runtime.Config.StateDir,
            authentication = runtime.AuthDisabled ? "disabled" : "oauth_bearer"
        }));
    }
}
