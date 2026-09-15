using System.Net;
using Codexish.P0;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

if (args.Contains("--self-test")) return await SelfTest.Run();
if (args.Contains("--fixture-test")) return ProbeRuntime.CheckFixture(args[^1]);
if (args.Contains("--fixture-sleep")) { await Task.Delay(TimeSpan.FromSeconds(60)); return 0; }

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i < 0 ? null : i + 1 < args.Length ? args[i + 1]
        : throw new ArgumentException($"Missing value for {name}");
}
int port = int.Parse(Option("--port") ?? "3000");
string root = Option("--resume") ?? ProbeRuntime.CreateFixture();
int? notepad = int.TryParse(Option("--notepad-pid"), out int pid) ? pid : null;
if (notepad.HasValue && !args.Contains("--disposable-desktop"))
    throw new ArgumentException("Desktop capture requires --disposable-desktop on an isolated test desktop.");
using var runtime = new ProbeRuntime(root, notepad);
var app = ProbeHost.Build(runtime, port, !args.Contains("--no-instructions"));
Console.WriteLine($"P0 fixture: {root}\nMCP: http://127.0.0.1:{port}/mcp\n" +
    "Private/local probe only. Do NOT expose this unauthenticated endpoint publicly. Ctrl+C stops it.");
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
            "Report actual test exit codes and saved file content. Stop for user cancellation or missing permission. " +
            "Server instructions cannot extend a Chat turn or override host confirmations.";

        public static WebApplication Build(ProbeRuntime runtime, int port, bool instructions = true)
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Services.AddSingleton(runtime);
            builder.Services.AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "CODEXish-P0", Version = "0.2.0" };
                o.ServerInstructions = instructions ? Instructions : null;
            }).WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
              .WithTools<ProbeTools>();
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                string host = context.Request.Host.Host;
                if (host != "127.0.0.1" && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                { context.Response.StatusCode = 403; return; }
                string origin = context.Request.Headers.Origin.ToString();
                if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                    uri.Scheme != "http" || !uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                    uri.Port != context.Request.Host.Port))
                { context.Response.StatusCode = 403; return; }
                await next(context);
            });
            app.MapGet("/healthz", () => new { status = "p0_probe", authentication = "loopback_only" });
            app.MapMcp("/mcp");
            return app;
        }
    }
}
