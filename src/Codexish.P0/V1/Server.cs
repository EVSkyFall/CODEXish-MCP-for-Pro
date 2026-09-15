using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Codexish.P0.V1;

public sealed class Runtime : IDisposable
{
    public ServerConfig Config { get; }
    public Store Store { get; }
    public Operations Operations { get; }
    public Artifacts Artifacts { get; }
    public Files Files { get; }
    public Processes Processes { get; }
    public OAuth OAuth { get; }
    public string ControlToken { get; }
    public Runtime(ServerConfig config)
    {
        config.Validate(); Config = config; Store = new(config.StateDirectory); Operations = new(Store);
        Artifacts = new(Store, Path.Combine(config.StateDirectory, "artifacts")); Files = new(config, Store, Artifacts);
        Processes = new(config, Store, Files, Artifacts); OAuth = new(config, Store);
        string tokenPath = Path.Combine(config.StateDirectory, "control.token");
        if (!File.Exists(tokenPath)) { using var f = new FileStream(tokenPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); f.Write(Encoding.ASCII.GetBytes(global::Codexish.P0.V1.OAuth.RandomToken())); f.Flush(true); }
        ControlToken = File.ReadAllText(tokenPath).Trim();
    }
    public CallToolResult Capabilities(string session) => VReply.Ok(new { schema_version = "v1.0", tools = Tools.Names, session_id = session,
        roots = Config.Roots.Select(r => new { id = r.Id, path = r.Path, read = r.Read, write = r.Write, shell = r.Shell }),
        execution_boundary = "unconfined_user", authentication = Config.NoAuth ? "none_loopback_development" : "oauth_authorization_code_pkce_s256",
        queue = "acceptance_order_fifo_per_resource", paused = Operations.Paused,
        last_checkpoint = CheckpointValue(session),
        response_pages = new { file_lines = 200, artifact_bytes = 65536, search_rows = 200, source = "server response-page configuration; full results remain in artifacts" },
        unsupported = new { computer = "slice_2", browser_mount = "slice_3", tray = "slice_4", pty = "not_implemented", patch_rename_delete_create = "modify-existing unified hunks only; fs.write supports create" } });
    public JsonElement? CheckpointValue(string session)
    {
        string? json = Store.Scalar("SELECT data FROM events WHERE session=$s AND kind='checkpoint' ORDER BY seq DESC LIMIT 1", ("$s", session));
        return json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
    }
    public CallToolResult Checkpoint(string session, string goal, string completion, string[] remaining, string[] handles)
    {
        if (!Config.Roots.Any(r => r.Write)) throw new ProbeFault("PERMISSION_DENIED", "Checkpoint is a full-control tool.");
        var value = new { goal = Redaction.Text(goal), completion_condition = Redaction.Text(completion), remaining_steps = remaining.Select(Redaction.Text), handles };
        Store.Exec("INSERT INTO events(utc,session,kind,data) VALUES($u,$s,'checkpoint',$d)", ("$u", DateTimeOffset.UtcNow.ToString("O")), ("$s", session), ("$d", JsonSerializer.Serialize(value)));
        return VReply.Ok(value);
    }
    public async Task<CallToolResult> Git(string session, string rootId, string cwd, string command, bool staged = false, int count = 20)
    {
        Files.Grant(rootId); if (Config.GitExecutable.Length == 0) return VReply.Error("UNSUPPORTED_CAPABILITY", "Set git_executable to a fixed absolute path.");
        if (count < 1) return VReply.Error("INVALID_ARGUMENT", "log count must be positive.");
        List<string> args = ["--no-pager", "--no-optional-locks", "--no-lazy-fetch", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
            "-c", "core.fsmonitor=false", "-c", "core.pager=cat", "-c", "diff.external=", "-c", "log.showSignature=false", "-c", "maintenance.auto=false", "-c", "gc.auto=0"];
        // clean/process filters can also run during status/diff. Read NAMES only, never config values.
        var filterQuery = await Processes.Run(session, rootId, cwd, Config.GitExecutable,
            args.Concat(["config", "--null", "--name-only", "--get-regexp", "^filter\\..*\\.(clean|process|required)$"]).ToArray(), CancellationToken.None, true);
        if (VReply.Data(filterQuery).GetProperty("exit_code").GetInt32() is not (0 or 1)) return filterQuery;
        string artifact = VReply.Data(filterQuery).GetProperty("process").GetProperty("stdout_artifact").GetString()!;
        using (var names = new FileStream(Artifacts.Locate(session, artifact).path, FileMode.Open, FileAccess.Read, FileShare.Read))
            foreach (string key in Encoding.UTF8.GetString(FileFence.Bytes(names)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                args.Add("-c"); args.Add(key + (key.EndsWith(".required", StringComparison.OrdinalIgnoreCase) ? "=false" : "="));
            }
        if (command == "status") args.AddRange(["status", "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=all"]);
        else if (command == "diff") { args.AddRange(["diff", "--no-textconv", "--no-ext-diff", "--ignore-submodules=all"]); if (staged) args.Add("--cached"); }
        else if (command == "log") args.AddRange(["log", "--no-show-signature", "--no-textconv", "--no-ext-diff", "-n", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "--format=%H%x09%an%x09%s"]);
        else return VReply.Error("INVALID_ARGUMENT", "Unknown fixed Git query.");
        return await Processes.Run(session, rootId, cwd, Config.GitExecutable, args.ToArray(), CancellationToken.None, true);
    }
    public void Dispose()
    {
        Task finish = Operations.Close(); Processes.KillAll().GetAwaiter().GetResult(); finish.GetAwaiter().GetResult(); Processes.Dispose(); Store.Dispose();
    }
}

public static class Host
{
    public const string Instructions = "You are operating the user's Windows PC through these tools. Work until the stated completion condition is met and verified; do not stop to ask unless permission is missing or the user cancels. Read before editing; use returned hashes. After every edit run the relevant tests and fix observed failures. For GUI, when available: observe before acting, re-observe after, crop/zoom when unsure, never repeat an unverified click. Prefer semantic targets over coordinates. Report actual exit codes, diffs and saved file contents; unknown effects are inspected, never replayed. Long processes return handles: poll them; a response wait is not a deadline. Call host.capabilities for implemented tools and use session.checkpoint to preserve the goal, remaining work and handles. These instructions do not override ChatGPT confirmations or extend a host turn.";
    public static WebApplication Build(Runtime runtime)
    {
        var c = runtime.Config; var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // Do not log OAuth request query strings or bodies.
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, c.Port); k.Listen(IPAddress.Loopback, c.ControlPort); });
        builder.Services.AddSingleton(runtime); builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer(o => { o.ServerInfo = new() { Name = "CODEXish", Version = "1.0.0-slice1" }; o.ServerInstructions = Instructions; })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless).WithTools<Tools>();
        var app = builder.Build();
        string[] hosts = c.AllowedHosts.Concat(c.NoAuth ? [] : new[] { new Uri(c.PublicUrl).Host }).ToArray();
        string[] origins = c.AllowedOrigins.Concat(c.NoAuth ? [] : new[] { c.PublicUrl }).ToArray();
        var policy = new ProbeAccessPolicy(hosts, origins);
        app.Use(async (context, next) =>
        {
            string? reason = policy.RejectionReason(context.Request);
            if (reason is not null)
            {
                Console.WriteLine($"rejected host={JsonSerializer.Serialize(context.Request.Host.Value)} origin={JsonSerializer.Serialize(context.Request.Headers.Origin.ToString())} reason={reason}");
                context.Response.StatusCode = 403; return;
            }
            // The separate listener is not forwarded by the MCP tunnel. Host checks alone cannot detect Host rewriting.
            string[] urls = app.Urls.ToArray();
            int controlPort = c.ControlPort != 0 ? c.ControlPort : (urls.Length > 1 ? new Uri(urls[1]).Port : -1);
            bool controlListener = context.Connection.LocalPort == controlPort;
            bool controlPath = context.Request.Path == "/control";
            if (controlPath || controlListener)
            {
                string h = context.Request.Host.Host, supplied = context.Request.Headers["X-Codexish-Control"].ToString();
                if (!controlPath || !controlListener || (h != "127.0.0.1" && h != "localhost") ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(runtime.ControlToken)))
                { context.Response.StatusCode = 403; return; }
            }
            if (context.Request.Path.StartsWithSegments("/mcp"))
            {
                if (context.Request.Query.ContainsKey("access_token")) { context.Response.StatusCode = 400; return; }
                string? session = "local";
                if (!c.NoAuth)
                {
                    string header = context.Request.Headers.Authorization.ToString();
                    session = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? runtime.OAuth.ValidateAccess(header[7..]) : null;
                    if (session is null)
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Headers.WWWAuthenticate = "Bearer resource_metadata=\"" + c.PublicUrl + "/.well-known/oauth-protected-resource\", scope=\"codexish\"";
                        return;
                    }
                }
                context.Items["codexish.session"] = session;
            }
            await next(context);
        });
        app.MapGet("/healthz", () => new { status = "coding_core", auth = !c.NoAuth, execution_boundary = "unconfined_user" });
        if (!c.NoAuth) runtime.OAuth.Map(app);
        app.MapPost("/control", async (HttpContext context) =>
        {
            var request = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(context.Request.Body);
            string action = request?.GetValueOrDefault("action", "") ?? "";
            switch (action)
            {
                case "pause": runtime.Operations.Pause(true); break;
                case "resume": runtime.Operations.Pause(false); break;
                case "kill-children": await runtime.Processes.KillAll(); break;
                case "revoke-tokens": runtime.OAuth.Revoke(); break;
                case "end-session": await runtime.Processes.EndSession(request!.GetValueOrDefault("session_id", "local")); break;
                default: return Results.BadRequest(new { error = "invalid_action" });
            }
            return Results.Json(new { action, paused = runtime.Operations.Paused, execution_boundary = "unconfined_user" });
        });
        app.MapMcp("/mcp"); return app;
    }
    public static async Task<int> Run(string[] args)
    {
        string? configPath = ProbeOptions.Values(args, "--config").FirstOrDefault();
        if (configPath is null) { Console.Error.WriteLine("v1: --config <codexish.json> [--no-auth] [--allow-host name] [--allow-origin origin]. Use --p0 for the original probe."); return 2; }
        var config = ServerConfig.Load(configPath); config.NoAuth = args.Contains("--no-auth");
        config.AllowedHosts = config.AllowedHosts.Concat(ProbeOptions.Values(args, "--allow-host")).ToArray();
        config.AllowedOrigins = config.AllowedOrigins.Concat(ProbeOptions.Values(args, "--allow-origin")).ToArray();
        using var runtime = new Runtime(config); await using var app = Build(runtime);
        Console.WriteLine($"CODEXish v1 MCP: http://127.0.0.1:{config.Port}/mcp; control listener: {config.ControlPort}; execution_boundary=unconfined_user");
        Console.WriteLine("Forward only the MCP port. Control token and state remain local; see README. No model/API key is used.");
        await app.RunAsync(); return 0;
    }
    public static int HashPassword()
    {
        Console.Error.Write("New CODEXish password (hidden): "); var password = new StringBuilder();
        while (true) { var key = Console.ReadKey(true); if (key.Key == ConsoleKey.Enter) break; if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; } else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar); }
        Console.Error.WriteLine(); if (password.Length == 0) return 2; Console.WriteLine(Passwords.Hash(password.ToString())); return 0;
    }
}
