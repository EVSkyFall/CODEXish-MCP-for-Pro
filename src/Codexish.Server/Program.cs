using System.Globalization;
using Codexish.Server;

if (args.Contains("--design-preview"))
{
    int at = Array.IndexOf(args, "--design-preview");
    if (at + 1 >= args.Length)
    {
        Console.Error.WriteLine("Usage: --design-preview <directory>");
        return 2;
    }
    return DesignPreview.Run(args[at + 1]);
}
if (args.Contains("--tray-smoke-test")) return await TrayApplication.Run("", smoke: true);
if (args.Contains("--tray-tests")) return await TrayTests.Run();
if (args.Contains("--mcp-initialize-first-fixture")) return await McpHandshakeIntegrationTests.Fixture(args);
if (args.Contains("--mcp-notification-fixture")) return await McpNotificationTests.Fixture(args);
if (args.Contains("--mcp-http-fixture")) return await McpHttpTests.Fixture(args);
if (args.Contains("--local-mcp-fixture")) return await LocalMcpIntegrationTests.Fixture(args);
if (args.Contains("--browser-fixture")) return await BrowserTests.Fixture(args);
if (args.Contains("--browser-tests")) return await BrowserTests.Run();
if (args.Contains("--browser-live-test"))
{
    int at = Array.IndexOf(args, "--browser-live-test");
    if (args.Length < at + 4)
    {
        Console.Error.WriteLine("Usage: --browser-live-test <node executable> <@playwright/mcp cli.js> <chrome executable>");
        return 2;
    }
    return await BrowserLiveTests.Run(args[at + 1], args[at + 2], args[at + 3]);
}
// Explicit live acceptance: it drives a new self-owned window through the authenticated HTTP host.
if (args.Contains("--self-test-desktop-http")) return await DesktopHttpAcceptance.Run();
if (args.Contains("--desktop-regression-test")) return await DesktopRegressionTests.Run();
if (args.Contains("--desktop-fixture")) return DesktopLiveTests.Fixture(args[^1]);
if (args.Contains("--self-test-desktop")) return await DesktopLiveTests.Run();
if (args.Contains("--desktop-core-test")) return await DesktopTests.Run();
if (args.Contains("--self-test"))
{
    int result = await SelfTest.Run();
    if (result != 0) return result;
    result = await DesktopTests.Run();
    return result == 0 ? await DesktopRegressionTests.Run() : result;
}

string? Option(string name) => CommandLine.Values(args, name).FirstOrDefault();
bool noAuth = args.Contains("--no-auth");
string configPath;
TrayOptions? tray = null;
ServerConfig? loaded = null;
// Only parsing, --init and loading are covered: the tray and the running server keep their own error handling.
try
{
    configPath = Option("--config") ?? ServerConfig.DefaultPath;
    if (args.Contains("--tray")) tray = TrayOptions.Parse(args);
    else if (args.Contains("--init")) return CommandLine.Init(args, configPath, Console.Out);
    else
    {
        loaded = ServerConfig.Load(configPath);
        if (noAuth && ServerConfig.NoAuthRefusal(loaded) is { } refusal) throw new ArgumentException(refusal);
        if (ServerConfig.TransportRefusal(loaded, noAuth) is { } insecure) throw new ArgumentException(insecure);
    }
}
catch (ArgumentException error) { return CommandLine.Refuse(error, Console.Error); }
if (tray is not null) return await TrayApplication.Run(configPath, tray, arguments: args);
var config = loaded!;

CodexishRuntime opened;
try { opened = new CodexishRuntime(config, noAuth); }
catch (StateLockedException error) { return CommandLine.Refuse(error, Console.Error); }
using var runtime = opened;
// Browser mounts connect in the background once the listener is up, so none of them can delay or stop the server.
var app = CodexishHost.Build(runtime, config.Port, instructions: !args.Contains("--no-instructions"));
Console.WriteLine($"""
    CODEXish v1 (coding core, desktop, browser mounts)
      MCP           http://127.0.0.1:{config.Port}/mcp  (published as {config.PublicUrl}/mcp)
      Roots         {string.Join(", ", config.Roots.Select(r => $"{r.Id}:{(r.Read ? "r" : "")}{(r.Write ? "w" : "")}{(r.Shell ? "x" : "")}{(Directory.Exists(r.Path) ? "" : " (missing)")}"))}
      Browser       {runtime.Browsers.Summary()}; MCP {runtime.Mounts.Summary()}; mounts connect after start, see host_capabilities
      State         {config.StateDir}
      Auth          {(noAuth ? "DISABLED (loopback development only)" : "OAuth bearer required on /mcp")}
      Control       POST http://127.0.0.1:{config.Port}/control/pause with header X-Codexish-Control
    The listener is loopback only; expose it with a tunnel. Host and Origin checks are not authentication.
    Commands run as this Windows user: there is no sandbox. Ctrl+C stops the server.
    """);
foreach (string warning in runtime.Warnings) Console.WriteLine("WARNING: " + warning);
await app.RunAsync();
return 0;

namespace Codexish.Server
{
    public static class CommandLine
    {
        public const string NoFolderShared =
            "No folder is shared yet; add one with --root id=path, the tray's Edit roots and grants, or by editing codexish.json.";

        public static string[] Values(string[] args, string name)
        {
            List<string> values = [];
            for (int i = 0; i < args.Length; i++)
                if (args[i] == name)
                {
                    if (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Missing value for {name}");
                    values.Add(args[++i]);
                }
            return values.ToArray();
        }

        // A bad or missing argument, or a state_dir another CODEXish holds, is the user's to fix, so it is one readable
        // line rather than a stack trace.
        public static int Refuse(Exception error, TextWriter output)
        {
            output.WriteLine("CODEXish: " + error.Message.ReplaceLineEndings(" "));
            return 2;
        }

        // Roots are optional, as in the tray's first-run form: without one the file has "roots": [].
        public static int Init(string[] args, string configPath, TextWriter output)
        {
            string? Option(string name) => Values(args, name).FirstOrDefault();
            string password = Option("--password")
                ?? throw new ArgumentException("--init requires --password <pw>. It is hashed with PBKDF2-SHA256; nothing is read from the environment.");
            string publicUrl = Option("--public-url")
                ?? throw new ArgumentException("--init requires --public-url <https://your-tunnel-host> so OAuth metadata and the Host allowlist match.");
            var roots = Values(args, "--root").Select(value =>
            {
                int separator = value.IndexOf('=');
                if (separator <= 0) throw new ArgumentException($"--root expects id=path, received '{value}'.");
                return (Id: value[..separator], Path: value[(separator + 1)..]);
            }).ToArray();
            string portText = Option("--port") ?? "3000";
            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
                throw new ArgumentException($"--port expects a number from 0 to 65535, received '{portText}'.");
            var created = ServerConfig.Create(publicUrl, password, roots, Values(args, "--redirect-uri"), port, Option("--state-dir"));
            created.Save(configPath);
            output.WriteLine($"""
                Wrote {configPath}

                ChatGPT connector fields
                  MCP endpoint        {created.PublicUrl}/mcp
                  Authorization URL   {created.PublicUrl}/authorize
                  Token URL           {created.PublicUrl}/token
                  Client ID           {created.OAuth.ClientId}
                  Client secret       {created.OAuth.ClientSecret}
                  Scope               mcp
                  Redirect URIs       any https callback; listed: {string.Join(", ", created.OAuth.RedirectUris)}

                Local control token (loopback only, never send it through the tunnel)
                  {created.ControlToken}

                Roots: {(created.Roots.Length == 0 ? "none" : string.Join(", ", created.Roots.Select(r => $"{r.Id}={r.Path}")))}
                State dir: {created.StateDir}
                Allowed Host header(s): {string.Join(", ", created.AllowHosts.Concat(["127.0.0.1", "localhost"]))}

                Any https callback without a fragment is accepted, and the sign-in page names the host it returns to.
                Only a callback that is not https has to be listed: the server logs a refused one with its offered
                redirect_uri, and --init --redirect-uri <that value> lists it.
                """);
            foreach (string warning in created.Warnings) output.WriteLine("WARNING: " + warning);
            if (created.Roots.Length == 0) output.WriteLine("WARNING: " + NoFolderShared);
            return 0;
        }
    }
}
