using System.Net;
using Codexish.P0;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

if (args.Contains("--self-test")) return await SelfTest.Run();
if (args.Contains("--fixture-test")) return ProbeRuntime.CheckFixture(args[^1]);
if (args.Contains("--fixture-sleep")) { await Task.Delay(TimeSpan.FromSeconds(60)); return 0; }

string? Option(string name) => ProbeOptions.Values(args, name).FirstOrDefault();
string[] allowedHosts = ProbeOptions.Values(args, "--allow-host");
string[] allowedOrigins = ProbeOptions.Values(args, "--allow-origin");
// Validate network options before creating any disposable files.
var access = new ProbeAccessPolicy(allowedHosts, allowedOrigins);
int port = int.Parse(Option("--port") ?? "3000");
string root = Option("--resume") ?? ProbeRuntime.CreateFixture();
int? notepad = int.TryParse(Option("--notepad-pid"), out int pid) ? pid : null;
if (notepad.HasValue && !args.Contains("--disposable-desktop"))
    throw new ArgumentException("Desktop capture requires --disposable-desktop on an isolated test desktop.");
using var runtime = new ProbeRuntime(root, notepad);
var app = ProbeHost.Build(runtime, port, !args.Contains("--no-instructions"), access);
Console.WriteLine($"P0 fixture: {root}\nMCP: http://127.0.0.1:{port}/mcp\n" +
    "Loopback P0 probe. Host/Origin checks are NOT authentication. Use controlled tunnel access. Ctrl+C stops it.");
await app.RunAsync();
return 0;

namespace Codexish.P0
{
    public static class ProbeHost
    {
        public const string Instructions = "Work within the user's task and this disposable fixture. Read before editing; " +
            "use the returned hash. After edits run the test and fix observed failures until the stated completion " +
            "condition is met. Reuse invocation_id only for retries of the same action. Inspect unknown operations; " +
            "never blindly replay a click. Before GUI input take a screenshot, and after input take another. " +
            "Screenshots default to max_width=1280; use 0 for native resolution. For click use coordinate_space=image " +
            "and image x,y from that observation_id; the server converts to physical pixels. " +
            "Report actual test exit codes and saved file content. Stop for user cancellation or missing permission. " +
            "Server instructions cannot extend a Chat turn or override host confirmations.";

        public static WebApplication Build(ProbeRuntime runtime, int port, bool instructions = true,
            ProbeAccessPolicy? access = null, Action<string>? rejectionLog = null)
        {
            access ??= new ProbeAccessPolicy();
            rejectionLog ??= Console.WriteLine;
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Services.AddSingleton(runtime);
            builder.Services.AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "CODEXish-P0", Version = "0.2.1" };
                o.ServerInstructions = instructions ? Instructions : null;
            }).WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
              .WithTools<ProbeTools>();
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                string? reason = access.RejectionReason(context.Request);
                if (reason is not null)
                {
                    // JSON escaping prevents untrusted header values from forging extra log lines.
                    rejectionLog($"rejected host={System.Text.Json.JsonSerializer.Serialize(context.Request.Host.Value)} " +
                        $"origin={System.Text.Json.JsonSerializer.Serialize(context.Request.Headers.Origin.ToString())} reason={reason}");
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                await next(context);
            });
            app.MapGet("/healthz", () => new { status = "p0_probe", authentication = "none", binding = "loopback_only" });
            app.MapMcp("/mcp");
            return app;
        }
    }
}

namespace Codexish.P0
{
    public static class ProbeOptions
    {
        public static string[] Values(string[] args, string name)
        {
            var values = new List<string>();
            for (int i = 0; i < args.Length; i++)
                if (args[i] == name)
                {
                    if (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Missing value for {name}");
                    values.Add(args[++i]);
                }
            return values.ToArray();
        }
    }

    // Host and Origin are routing/browser checks, NOT client authentication.
    public sealed class ProbeAccessPolicy
    {
        private readonly HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost" };
        private readonly HashSet<string> origins = new(StringComparer.OrdinalIgnoreCase);

        public ProbeAccessPolicy(IEnumerable<string>? allowedHosts = null, IEnumerable<string>? allowedOrigins = null)
        {
            foreach (string host in allowedHosts ?? [])
            {
                if (string.IsNullOrWhiteSpace(host) || host != host.Trim() ||
                    host.IndexOfAny([':', '/', '\\', '*', '@', '?', '#']) >= 0 ||
                    Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
                    throw new ArgumentException("--allow-host requires an exact hostname without scheme, port or wildcard.");
                hosts.Add(host);
            }
            foreach (string origin in allowedOrigins ?? [])
                origins.Add(NormalizeOrigin(origin) ?? throw new ArgumentException(
                    "--allow-origin requires an exact http(s) origin without credentials, path, query or fragment."));
        }

        public string? RejectionReason(HttpRequest request)
        {
            string host;
            try { host = request.Host.Host; }
            catch (FormatException) { return "invalid_host"; }
            if (!hosts.Contains(host)) return "host_not_allowed";
            string origin = request.Headers.Origin.ToString();
            if (!request.Headers.ContainsKey("Origin")) return null;
            string? normalized = NormalizeOrigin(origin);
            if (normalized is null) return "invalid_origin";
            if (origins.Contains(normalized)) return null;
            // Default local same-origin behavior. A forwarded HTTPS origin needs explicit opt-in.
            string? sameOrigin = NormalizeOrigin($"{request.Scheme}://{request.Host.Value}");
            return string.Equals(normalized, sameOrigin, StringComparison.OrdinalIgnoreCase) ? null : "origin_not_allowed";
        }

        private static string? NormalizeOrigin(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Contains('\\') ||
                value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return null;
            return uri.GetLeftPart(UriPartial.Authority);
        }
    }
}
