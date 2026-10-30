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
        runtime.Mounts.PrepareManifests();
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
        }).WithHttpTransport(o =>
        {
            o.SessionMode = HttpServerSessionMode.Stateless;
            o.ConfigureSessionOptions = (_, options, _) =>
            {
                options.ServerInstructions = instructions ? runtime.Mounts.ComposeInstructions() : null;
                return Task.CompletedTask;
            };
        })
          .WithTools<CodexishTools>().WithTools<DesktopTools>()
          .WithListToolsHandler((_, _) => ValueTask.FromResult(new ModelContextProtocol.Protocol.ListToolsResult { Tools = runtime.Mounts.Tools.Select(t => t.ProtocolTool).ToList() }))
          .WithCallToolHandler((request, token) => runtime.Mounts.Find(request.Params?.Name) is { } tool
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
            // Client registration is public by design, so only the Host allowlist applies to it.
            bool registration = string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/register", StringComparison.OrdinalIgnoreCase);
            string? reason = access.RejectionReason(context.Request, checkOrigin: !registration);
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
            status = "codexish",
            version = CodexishRuntime.ServerVersion,
            authentication = runtime.AuthDisabled ? "disabled" : "oauth_bearer",
            binding = "loopback_only",
            paused = runtime.Ledger.Paused
        });

        MapMetadata(app, runtime);
        MapRegistration(app, runtime, log);
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
            // RFC 7591. Client ID metadata documents are deliberately not advertised: a client that sees them offered
            // tries them first and would not fall back to registration.
            registration_endpoint = Base(runtime, request) + "/register",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { ClientRegistry.Basic, ClientRegistry.Post, ClientRegistry.Public },
            scopes_supported = new[] { "mcp" },
            // Every authorization redirect carries iss (RFC 9207). Without this flag ChatGPT registers a
            // per-connection callback instead of its stable one.
            authorization_response_iss_parameter_supported = true
        }));
    }

    // RFC 7591. Public by design: no bearer token and no Origin requirement; the Host allowlist still applies. Unused
    // registrations are bounded by ClientRegistry.UnusedLimit and removed by retention after output_days.
    private static void MapRegistration(WebApplication app, CodexishRuntime runtime, Action<string> log)
    {
        app.MapPost("/register", async (HttpRequest request) =>
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            JsonElement body;
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body);
                body = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                log("rejected oauth stage=register reason=invalid_client_metadata detail=\"not JSON\"");
                return RegistrationError("invalid_client_metadata", "The registration request must be a JSON object (RFC 7591).");
            }
            try
            {
                var (registered, error, description) = runtime.Clients.Register(body);
                if (registered is null)
                {
                    log($"rejected oauth stage=register reason={error} detail={JsonSerializer.Serialize(description)}");
                    runtime.Store.Event("oauth_rejected", "register", new { reason = error, detail = description });
                    return RegistrationError(error!, description!);
                }
                var hosts = ((string[])registered["redirect_uris"]).Select(ClientRegistry.Host).Distinct(StringComparer.Ordinal);
                log($"registered oauth client client_id={registered["client_id"]} auth_method={registered["token_endpoint_auth_method"]} " +
                    $"name={JsonSerializer.Serialize(registered.GetValueOrDefault("client_name"))} redirect_hosts={JsonSerializer.Serialize(hosts)}");
                return Results.Json(registered, statusCode: StatusCodes.Status201Created);
            }
            catch (Microsoft.Data.Sqlite.SqliteException error)
            {
                log($"failed oauth stage=register reason=storage detail={JsonSerializer.Serialize(error.Message)}");
                return Results.Json(new { error = "server_error", error_description = "The registration could not be stored; try again." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    private static IResult RegistrationError(string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);

    // Listed: the redirect_uri is in oauth.redirect_uris, or it is one a registered client registered. Any other https
    // callback is accepted for the static client as well, but it is never sent an error redirect before the password has
    // been accepted. Registered and ClientName describe a client that registered itself through /register.
    private sealed record AuthorizeRequest(string ClientId, string RedirectUri, bool Listed, string? State,
        string? Challenge, string? ChallengeMethod, string? Scope, string? Resource, bool Registered, string? ClientName);

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
            // A registered client that has signed in is never evicted or removed by age. A failed write is logged; the
            // sign-in itself still succeeds, and the client's live tokens protect it as well.
            if (parsed!.Registered)
                try { runtime.Clients.MarkSignedIn(parsed.ClientId); }
                catch (Microsoft.Data.Sqlite.SqliteException error)
                {
                    log($"failed oauth stage=authorize reason=sign_in_time_not_recorded client_id={parsed.ClientId} detail={JsonSerializer.Serialize(error.Message)}");
                }
            string code = runtime.Tokens.IssueCode(parsed.ClientId, parsed.RedirectUri, parsed.Challenge,
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
        bool configured = !string.IsNullOrEmpty(clientId) && string.Equals(clientId, oauth.ClientId, StringComparison.Ordinal);
        ClientRow? registered = configured ? null : runtime.Clients.Find(clientId);
        if (!configured && registered is null)
        {
            log($"rejected oauth stage=authorize reason=unknown_client offered_client_id={JsonSerializer.Serialize(clientId ?? "")}");
            return (null, Results.Content(ErrorPage("Unknown client_id. Check the connector configuration, or connect it again so it can register."),
                "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest));
        }
        bool listed;
        if (registered is not null)
        {
            // A registered client chose its callbacks itself, so only those are accepted, exactly as registered.
            listed = !string.IsNullOrEmpty(redirectUri) && registered.RedirectUris.Contains(redirectUri, StringComparer.Ordinal);
            if (!listed)
            {
                log($"rejected oauth stage=authorize reason=redirect_uri_not_registered client_id={registered.ClientId} " +
                    $"offered_redirect_uri={JsonSerializer.Serialize(redirectUri ?? "")}");
                runtime.Store.Event("oauth_rejected", "authorize", new { reason = "redirect_uri_not_registered", client_id = registered.ClientId, offered = redirectUri });
                return (null, Results.Content(ErrorPage(
                    "redirect_uri is not one of the redirect_uris this client registered. Connect the client again so it registers its current callback."),
                    "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest));
            }
        }
        else
        {
            listed = !string.IsNullOrEmpty(redirectUri) && oauth.RedirectUris.Contains(redirectUri, StringComparer.Ordinal);
            if (!listed && !IsHttpsCallback(redirectUri))
            {
                // The offered value is logged so a callback that is not https can be listed with --redirect-uri.
                log($"rejected oauth stage=authorize reason=redirect_uri_mismatch offered_redirect_uri={JsonSerializer.Serialize(redirectUri ?? "")}");
                runtime.Store.Event("oauth_rejected", "authorize", new { reason = "redirect_uri_mismatch", offered = redirectUri });
                return (null, Results.Content(ErrorPage(
                    "redirect_uri must be an absolute https URI without a fragment. The offered value was logged; a callback that is not https has to be listed with --redirect-uri when you run --init."),
                    "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest));
            }
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
        // Without a secret, PKCE is the only thing that binds the code to the client that asked for it.
        if (registered is not null && ClientRegistry.IsPublic(registered) && string.IsNullOrEmpty(challenge))
        {
            log($"rejected oauth stage=authorize reason=pkce_required_for_public_client client_id={registered.ClientId}");
            return (null, Fail("invalid_request", "A public client must send a code_challenge with code_challenge_method=S256."));
        }
        if (!listed) log($"accepted oauth redirect_uri outside configured list host={Destination(redirectUri!)}");
        // Neither the requested scope nor the resource indicator refuses a request: the grant is always mcp for
        // <public_url>/mcp.
        NoteResource(resource, issuer, log);
        return (new AuthorizeRequest(clientId!, redirectUri!, listed, state, string.IsNullOrEmpty(challenge) ? null : challenge,
            challengeMethod, scope, string.IsNullOrEmpty(resource) ? null : resource, registered is not null, registered?.Name), null);
    }

    private static bool IsHttpsCallback(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Fragment.Length == 0;

    private static string Destination(string redirectUri) => ClientRegistry.Host(redirectUri);

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

    // The pages are self-contained: system fonts, inline styles and the CODEXish mark as inline SVG, light and dark from
    // prefers-color-scheme, and nothing fetched from anywhere. Material 3 roles carry Windows 11's palette, as in the tray.
    private const string PageStyle = """
        :root{color-scheme:light dark;--bg:#f3f3f3;--surface:#fbfbfb;--stroke:#e3e3e3;--on:#1a1a1a;--on-variant:#5d5d5d;
        --field:#fff;--field-stroke:#d4d4d4;--underline:#868686;--primary:#005fb8;--on-primary:#fff;--container:#e5eff9;
        --on-container:#0a3a66;--error:#c42b1c;--error-container:#fde7e9;--shadow:0 8px 24px rgb(0 0 0/.08),0 1px 2px rgb(0 0 0/.06)}
        @media (prefers-color-scheme:dark){:root{--bg:#202020;--surface:#2b2b2b;--stroke:#383838;--on:#fff;--on-variant:#c8c8c8;
        --field:#333;--field-stroke:#424242;--underline:#9a9a9a;--primary:#60cdff;--on-primary:#000;--container:#1b3850;
        --on-container:#cde7ff;--error:#ff99a4;--error-container:#442726;--shadow:0 8px 24px rgb(0 0 0/.4)}}
        *{box-sizing:border-box}
        html{-webkit-text-size-adjust:100%;text-size-adjust:100%}
        body{margin:0;min-height:100vh;display:grid;place-items:center;padding:24px 16px;background:var(--bg);color:var(--on);
        font:14px/20px "Segoe UI Variable Text","Segoe UI",system-ui,-apple-system,BlinkMacSystemFont,Roboto,"Helvetica Neue",Arial,sans-serif}
        main{width:100%;max-width:400px;padding:32px 24px 24px;border:1px solid var(--stroke);border-radius:12px;background:var(--surface);box-shadow:var(--shadow)}
        .brand{display:flex;align-items:center;gap:16px;margin:0 0 24px}
        .brand svg{flex:none;width:40px;height:40px}
        h1{margin:0;font:600 24px/32px "Segoe UI Variable Display","Segoe UI",system-ui,-apple-system,BlinkMacSystemFont,Roboto,"Helvetica Neue",Arial,sans-serif}
        p{margin:0}
        .lead{margin-top:2px;color:var(--on-variant)}
        .who{margin:0 0 12px;overflow-wrap:anywhere}
        .dest{margin:0 0 24px;padding:12px 16px;border-radius:8px;background:var(--container);color:var(--on-container);font-size:12px;line-height:16px}
        .dest strong{display:block;margin-top:4px;font-size:16px;line-height:24px;font-weight:600;overflow-wrap:anywhere}
        label{display:block;margin:0 0 6px;font-weight:600}
        input[type=password]{display:block;width:100%;height:40px;margin:0;padding:0 12px;border:1px solid var(--field-stroke);
        border-bottom-color:var(--underline);border-radius:4px;background:var(--field);color:var(--on);font:inherit;font-size:16px}
        input[type=password]:hover{border-color:var(--underline)}
        input[type=password]:focus{outline:2px solid transparent;border-color:var(--field-stroke);border-bottom-color:var(--primary);box-shadow:inset 0 -1px 0 var(--primary)}
        .hint{margin-top:6px;color:var(--on-variant);font-size:12px;line-height:16px}
        button{display:block;width:100%;height:40px;margin:24px 0 0;padding:0 24px;border:0;border-radius:20px;background:var(--primary);
        color:var(--on-primary);font:inherit;font-weight:600;cursor:pointer}
        button:hover{background:color-mix(in srgb,var(--primary) 90%,var(--on-primary))}
        button:active{background:color-mix(in srgb,var(--primary) 84%,var(--on-primary))}
        button:focus-visible{outline:2px solid var(--on);outline-offset:2px}
        .err,.refused{display:flex;gap:12px;align-items:flex-start;border-radius:8px}
        .err{margin:0 0 16px;padding:12px 16px;background:var(--error-container)}
        .err svg,.refused svg{flex:none;width:20px;height:20px;margin-top:0;color:var(--error)}
        .refused svg{width:24px;height:24px;margin-top:4px}
        .refused h1{font-size:20px;line-height:32px}
        .refused p{margin-top:4px;overflow-wrap:anywhere}
        @media (max-width:440px){main{padding:24px 16px 16px}}
        """;

    // The CODEXish mark: Lucide's square-terminal (ISC) with its square filled, as in assets/icon/codexish.svg.
    private const string Logo = """<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="1.5" y="1.5" width="21" height="21" rx="4.5" fill="#005FB8"/><path d="m7 11 2-2-2-2M11 13h4" transform="translate(12 12) scale(1.3) translate(-11 -10)" fill="none" stroke="#FFFFFF" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>""";

    // Lucide's circle-alert (ISC).
    private const string AlertIcon = """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="10"/><line x1="12" x2="12" y1="8" y2="12"/><line x1="12" x2="12.01" y1="16" y2="16"/></svg>""";

    // Delta 3: every incoming parameter is carried into the POST as a hidden field and revalidated there,
    // so the issued code stays bound to the caller that started the flow.
    private static string LoginForm(AuthorizeRequest request, string nonce, string? message) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="color-scheme" content="light dark">
        <title>CODEXish sign in</title>
        <style>{{PageStyle}}</style></head><body><main>
        <form method="post" action="/authorize">
        <div class="brand">{{Logo}}<div><h1>CODEXish</h1><p class="lead">Sign in to connect this Windows PC.</p></div></div>
        {{Requester(request)}}<div class="dest">After sign-in you will return to <strong>{{Encode(Destination(request.RedirectUri))}}</strong></div>
        {{Message(message)}}
        <label for="password">CODEXish password</label>
        <input id="password" type="password" name="password" autocomplete="current-password" autofocus required aria-describedby="password-hint">
        <p class="hint" id="password-hint">The password set up for CODEXish on this PC, not your OpenAI password.</p>
        <input type="hidden" name="response_type" value="code">
        <input type="hidden" name="client_id" value="{{Encode(request.ClientId)}}">
        <input type="hidden" name="redirect_uri" value="{{Encode(request.RedirectUri)}}">
        <input type="hidden" name="state" value="{{Encode(request.State)}}">
        <input type="hidden" name="code_challenge" value="{{Encode(request.Challenge)}}">
        <input type="hidden" name="code_challenge_method" value="{{Encode(request.ChallengeMethod)}}">
        <input type="hidden" name="scope" value="{{Encode(request.Scope)}}">
        <input type="hidden" name="resource" value="{{Encode(request.Resource)}}">
        <input type="hidden" name="nonce" value="{{Encode(nonce)}}">
        <button type="submit">Sign in</button></form></main></body></html>
        """;

    private static string Message(string? message) =>
        message is null ? "" : $"<div class=\"err\" role=\"alert\">{AlertIcon}<p>{Encode(message)}</p></div>";

    // The name a registered client gave itself. Anyone can register any name, so the destination host below it stays
    // the thing to check.
    private static string Requester(AuthorizeRequest request) => !request.Registered ? "" :
        $"<p class=\"who\"><strong>{Encode(request.ClientName ?? "An unnamed client")}</strong> wants to connect to this PC.</p>";

    private static string ErrorPage(string message) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="color-scheme" content="light dark">
        <title>CODEXish</title>
        <style>{{PageStyle}}</style></head><body><main>
        <div class="brand">{{Logo}}<p class="lead">CODEXish</p></div>
        <div class="refused" role="alert">{{AlertIcon}}<div><h1>Authorization refused</h1>
        <p>{{Encode(message)}}</p></div></div></main></body></html>
        """;

    // --design-preview: the sign-in page as a registered client sees it, the same page after a wrong password, and the
    // local error page, from sample values.
    internal static IReadOnlyList<(string Name, string Html)> PreviewPages()
    {
        var request = new AuthorizeRequest("sample-client", ServerConfig.DefaultRedirectUri, true, "sample-state", "sample-challenge", "S256",
            "mcp", null, true, "ChatGPT");
        return
        [
            ("sign-in.html", LoginForm(request, "sample-nonce", null)),
            ("sign-in-wrong-password.html", LoginForm(request, "sample-nonce", "Incorrect password.")),
            ("error.html", ErrorPage("Unknown client_id. Check the connector configuration, or connect it again so it can register.")),
        ];
    }

    private static void MapToken(WebApplication app, CodexishRuntime runtime, Action<string> log)
    {
        app.MapPost("/token", async (HttpRequest request) =>
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!request.HasFormContentType) return TokenError("invalid_request", "Use application/x-www-form-urlencoded.");
            var form = await request.ReadFormAsync();
            var oauth = runtime.Config.OAuth;
            var (client, clientReason) = AuthenticateClient(runtime, request, form);
            if (client is null)
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
                if (!string.Equals(code.ClientId, client.Id, StringComparison.Ordinal))
                {
                    log("rejected oauth stage=token reason=code_issued_to_other_client");
                    return TokenError("invalid_grant", "The code was issued to a different client.");
                }
                if (client.Public && code.Challenge is null)
                {
                    log("rejected oauth stage=token reason=pkce_required_for_public_client");
                    return TokenError("invalid_grant", "A public client's code must carry a PKCE challenge.");
                }
                // Delta 8: PKCE is mandatory exactly when the authorization request carried a challenge.
                if (!Tokens.VerifyPkce(code.Challenge, form["code_verifier"].ToString()))
                {
                    log("rejected oauth stage=token reason=pkce_verification_failed");
                    return TokenError("invalid_grant", "code_verifier does not match the code_challenge.");
                }
                return Issue(runtime, oauth, client.Id, audience, code.Challenge is not null, Tokens.NewFamily(), null);
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
                if (!string.Equals(row.ClientId, client.Id, StringComparison.Ordinal))
                {
                    log("rejected oauth stage=token reason=refresh_token_of_other_client");
                    return TokenError("invalid_grant", "The refresh token was issued to a different client.");
                }
                // Neither rotated nor consumed: rotation's replay revocation disconnected the connector whenever a
                // refresh response was lost in the tunnel or two refreshes raced. A confidential client's secret, required
                // above, keeps a leaked refresh token useless on its own.
                runtime.Tokens.Renew(refresh);
                return Issue(runtime, oauth, client.Id, audience, row.PkceUsed, row.Family, refresh);
            }
            return TokenError("unsupported_grant_type", "Use authorization_code or refresh_token.");
        });
    }

    // A refresh grant hands back the refresh token it presented; only the authorization code grant creates one. Every
    // token carries the client it was issued to.
    private static IResult Issue(CodexishRuntime runtime, OAuthConfig oauth, string clientId, string audience, bool pkceUsed,
        string family, string? refresh)
    {
        var now = DateTimeOffset.UtcNow;
        var accessLifetime = TimeSpan.FromHours(oauth.AccessTokenHours);
        string access = runtime.Tokens.Issue("access", clientId, audience, now + accessLifetime, pkceUsed, family);
        refresh ??= runtime.Tokens.Issue("refresh", clientId, audience, Tokens.RefreshExpiry(oauth, now), pkceUsed, family);
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

    private sealed record TokenClient(string Id, bool Public);

    // The static client: a client_id that is present must be its own, and without one the secret alone identifies it;
    // its secret is always required. A registered confidential client needs its client_id and its own secret (Basic or
    // form); a registered public client sends its client_id and no secret, and PKCE binds its code instead. An unknown
    // or removed client fails.
    private static (TokenClient? Client, string Reason) AuthenticateClient(CodexishRuntime runtime, HttpRequest request, IFormCollection form)
    {
        var oauth = runtime.Config.OAuth;
        string? basicId = null;
        string secret;
        string header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())); }
            catch (FormatException) { return (null, "malformed_basic_credentials"); }
            int separator = decoded.IndexOf(':');
            if (separator < 0) return (null, "malformed_basic_credentials");
            basicId = Uri.UnescapeDataString(decoded[..separator]);
            secret = Uri.UnescapeDataString(decoded[(separator + 1)..]);
        }
        else secret = form["client_secret"].ToString();
        string formId = form["client_id"].ToString();
        if (!string.IsNullOrEmpty(basicId) && formId.Length > 0 && !string.Equals(basicId, formId, StringComparison.Ordinal))
            return (null, "conflicting_client_id");
        string clientId = !string.IsNullOrEmpty(basicId) ? basicId : formId;
        if (clientId.Length == 0 || string.Equals(clientId, oauth.ClientId, StringComparison.Ordinal))
        {
            if (secret.Length == 0) return (null, "missing_client_secret");
            if (!Tokens.SecretMatches(oauth.ClientSecret, secret)) return (null, "wrong_client_secret");
            return (new TokenClient(oauth.ClientId, false), "ok");
        }
        var registered = runtime.Clients.Find(clientId);
        if (registered is null) return (null, "unknown_client");
        if (ClientRegistry.IsPublic(registered)) return (new TokenClient(registered.ClientId, true), "ok");
        if (secret.Length == 0) return (null, "missing_client_secret");
        if (!ClientRegistry.SecretMatches(registered, secret)) return (null, "wrong_client_secret");
        return (new TokenClient(registered.ClientId, false), "ok");
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
        app.MapPost("/control/remove-clients", (HttpContext context) => Guard(context, () =>
        {
            var (removed, revoked) = runtime.Clients.RemoveAll();
            return new
            {
                removed, revoked,
                note = "Every client that registered itself is removed and its tokens are revoked; the configured client is unchanged. " +
                       "A connector registers again the next time it connects."
            };
        }));
        app.MapGet("/control/status", (HttpContext context) => Guard(context, () => new
        {
            paused = runtime.Ledger.Paused,
            processes = runtime.Processes.Live.Select(runtime.Processes.Describe),
            roots = runtime.Config.Roots.Select(r => new { r.Id, r.Path, r.Read, r.Write, r.Shell }),
            state_dir = runtime.Config.StateDir,
            authentication = runtime.AuthDisabled ? "disabled" : "oauth_bearer",
            registered_clients = runtime.Clients.Describe()
        }));
    }
}
