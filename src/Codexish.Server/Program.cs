using Codexish.Server;

if (args.Contains("--desktop-fixture")) return DesktopLiveTests.Fixture(args[^1]);
if (args.Contains("--self-test-desktop")) return await DesktopLiveTests.Run();
if (args.Contains("--desktop-core-test")) return await DesktopTests.Run();
if (args.Contains("--self-test"))
{
    int result = await SelfTest.Run();
    return result == 0 ? await DesktopTests.Run() : result;
}

string? Option(string name) => CommandLine.Values(args, name).FirstOrDefault();
string configPath = Option("--config") ?? ServerConfig.DefaultPath;

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
          Redirect URI(s)     {string.Join(", ", created.OAuth.RedirectUris)}

        Local control token (loopback only, never send it through the tunnel)
          {created.ControlToken}

        Roots: {string.Join(", ", created.Roots.Select(r => $"{r.Id}={r.Path}"))}
        State dir: {created.StateDir}
        Allowed Host header(s): {string.Join(", ", created.AllowHosts.Concat(["127.0.0.1", "localhost"]))}

        If the connector's callback differs, the server logs the offered redirect_uri on rejection;
        rerun --init with --redirect-uri <that value> to accept it.
        """);
    return 0;
}

var config = ServerConfig.Load(configPath);
bool noAuth = args.Contains("--no-auth");
if (noAuth && ServerConfig.NoAuthRefusal(config) is { } refusal) throw new ArgumentException(refusal);
if (ServerConfig.TransportRefusal(config, noAuth) is { } insecure) throw new ArgumentException(insecure);

using var runtime = new CodexishRuntime(config, noAuth);
var app = CodexishHost.Build(runtime, config.Port, instructions: !args.Contains("--no-instructions"));
Console.WriteLine($"""
    CODEXish v1 slice 2 (coding core and desktop)
      MCP           http://127.0.0.1:{config.Port}/mcp  (published as {config.PublicUrl}/mcp)
      Roots         {string.Join(", ", config.Roots.Select(r => $"{r.Id}:{(r.Read ? "r" : "")}{(r.Write ? "w" : "")}{(r.Shell ? "x" : "")}"))}
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
