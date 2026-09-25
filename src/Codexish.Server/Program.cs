using Codexish.Server;

if (args.Contains("--tray-smoke-test")) return await TrayApplication.Run("", smoke: true);
if (args.Contains("--tray-tests")) return await TrayTests.Run();
if (args.Contains("--browser-fixture")) return await BrowserTests.Fixture(CommandLine.Values(args, "--browser-fixture")[0], args.Contains("--ignore-stdin-eof"));
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
string configPath = Option("--config") ?? ServerConfig.DefaultPath;
if (args.Contains("--tray")) return await TrayApplication.Run(configPath, TrayOptions.Parse(args));

if (args.Contains("--init"))
{
    string password = Option("--password")
        ?? throw new ArgumentException("--init requires --password <pw>. It is hashed with PBKDF2-SHA256; nothing is read from the environment.");
    string publicUrl = Option("--public-url")
        ?? throw new ArgumentException("--init requires --public-url <https://your-tunnel-host> so OAuth metadata and the Host allowlist match.");
    var roots = CommandLine.Values(args, "--root").Select(value =>
    {
        int separator = value.IndexOf('=');
        if (separator <= 0) throw new ArgumentException($"--root expects id=path, received '{value}'.");
        return (Id: value[..separator], Path: value[(separator + 1)..]);
    }).ToArray();
    if (roots.Length == 0) throw new ArgumentException("--init requires at least one --root id=path.");
    var created = ServerConfig.Create(publicUrl, password, roots, CommandLine.Values(args, "--redirect-uri"),
        int.Parse(Option("--port") ?? "3000"), Option("--state-dir"));
    created.Save(configPath);
    Console.WriteLine($"""
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

        Roots: {string.Join(", ", created.Roots.Select(r => $"{r.Id}={r.Path}"))}
        State dir: {created.StateDir}
        Allowed Host header(s): {string.Join(", ", created.AllowHosts.Concat(["127.0.0.1", "localhost"]))}

        Any https callback without a fragment is accepted, and the sign-in page names the host it returns to.
        Only a callback that is not https has to be listed: the server logs a refused one with its offered
        redirect_uri, and --init --redirect-uri <that value> lists it.
        """);
    return 0;
}

var config = ServerConfig.Load(configPath);
bool noAuth = args.Contains("--no-auth");
if (noAuth && ServerConfig.NoAuthRefusal(config) is { } refusal) throw new ArgumentException(refusal);
if (ServerConfig.TransportRefusal(config, noAuth) is { } insecure) throw new ArgumentException(insecure);

using var runtime = new CodexishRuntime(config, noAuth);
// A mount that cannot start is reported in host_capabilities; the coding and desktop tools start regardless.
await runtime.Browsers.InitializeAsync();
var app = CodexishHost.Build(runtime, config.Port, instructions: !args.Contains("--no-instructions"));
Console.WriteLine($"""
    CODEXish v1 (coding core, desktop, browser mounts)
      MCP           http://127.0.0.1:{config.Port}/mcp  (published as {config.PublicUrl}/mcp)
      Roots         {string.Join(", ", config.Roots.Select(r => $"{r.Id}:{(r.Read ? "r" : "")}{(r.Write ? "w" : "")}{(r.Shell ? "x" : "")}"))}
      Browser       {runtime.Browsers.Summary()}
      State         {config.StateDir}
      Auth          {(noAuth ? "DISABLED (loopback development only)" : "OAuth bearer required on /mcp")}
      Control       POST http://127.0.0.1:{config.Port}/control/pause with header X-Codexish-Control
    The listener is loopback only; expose it with a tunnel. Host and Origin checks are not authentication.
    Commands run as this Windows user: there is no sandbox. Ctrl+C stops the server.
    """);
await app.RunAsync();
return 0;

namespace Codexish.Server
{
    public static class CommandLine
    {
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
    }
}
