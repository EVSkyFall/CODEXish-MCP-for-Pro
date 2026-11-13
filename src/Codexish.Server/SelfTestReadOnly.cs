using System.Net;
using System.Text;
using System.Text.Json;
using static Codexish.Server.SelfTest;

namespace Codexish.Server;

// v1.0.3: a read_only review instance reached by a browser extension, through the real HTTP host with --no-auth.
internal static class ReadOnlyTests
{
    private const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";

    public static async Task Run(string temp, string password)
    {
        Defaults();
        Origins();
        await ReviewInstance(temp, password);
        await DefaultInstance(temp, password);
    }

    private static void Defaults()
    {
        Check(!JsonSerializer.Deserialize<ServerConfig>("{}")!.ReadOnly &&
              !JsonSerializer.Deserialize<ServerConfig>("""{ "read_only": null }""")!.ReadOnly &&
              JsonSerializer.Deserialize<ServerConfig>("""{ "read_only": true }""")!.ReadOnly,
            "read_only is false when missing or null and true when set");
    }

    private static void Origins()
    {
        Check(AccessPolicy.NormalizeOrigin(Extension) == Extension &&
              AccessPolicy.NormalizeOrigin("CHROME-EXTENSION://ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP") == Extension,
            "a chrome-extension origin with a 32-letter a-p id is accepted, compared without case and normalized to lower case");
        string[] malformed =
        [
            Extension[..^1], Extension + "a", "chrome-extension://abcdefghijklmnopabcdefghijklmnoq", "chrome-extension://abcdefghijklmnopabcdefghijklmno1",
            Extension + "/", Extension + "/path", Extension + ":8080", Extension + "?q=1", Extension + "#f",
            "chrome-extension://user@abcdefghijklmnopabcdefghijklmnop", "moz-extension://abcdefghijklmnopabcdefghijklmnop", "chrome-extension://"
        ];
        Check(malformed.All(o => AccessPolicy.NormalizeOrigin(o) is null),
            "malformed extension origins are rejected: wrong length, letters outside a-p, a path, a port, a query, a fragment, user info or another scheme");
    }

    private static ServerConfig Loopback(string directory, string password, bool readOnly, params string[] origins)
    {
        var config = BuildConfig(directory, password);
        config.PublicUrl = "";
        config.AllowOrigins = origins;
        config.ReadOnly = readOnly;
        config.Validate();
        return config;
    }

    private static async Task ReviewInstance(string temp, string password)
    {
        var config = Loopback(Path.Combine(temp, "review"), password, true, Extension, "chrome-extension://nope");
        Check(config.Roots.All(r => r.Read && !r.Write && !r.Shell) &&
              config.Warnings.Contains("read_only is on, so root 'proj' is read-only.") &&
              config.Warnings.Contains("allow_origins entry 'chrome-extension://nope' is not an exact http(s) or chrome-extension origin and is ignored.") &&
              config.AllowOrigins.SequenceEqual([Extension]),
            "read_only turns roots configured with write and shell into read-only roots with a warning, and a malformed extension origin is skipped with the new warning");
        Check(ServerConfig.NoAuthRefusal(config) is null && ServerConfig.TransportRefusal(config, true) is null,
            "the review configuration is a pure loopback configuration that --no-auth accepts");
        File.WriteAllText(Path.Combine(config.Roots[0].Path, "seen.txt"), "seen\n");
        using var runtime = new CodexishRuntime(config, authDisabled: true);
        List<string> log = [];
        await using var app = CodexishHost.Build(runtime, 0, line => { lock (log) log.Add(line); });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
            var (status, initialized) = await Post(http, Extension, "initialize", new
            {
                protocolVersion = CodexishRuntime.ProtocolVersion, capabilities = new { },
                clientInfo = new { name = "codexish-self-test", version = CodexishRuntime.ServerVersion }
            });
            Check(status == HttpStatusCode.OK && initialized!.Value.GetProperty("result").GetProperty("instructions").GetString() == CodexishRuntime.ReadOnlyInstructions,
                "a listed extension origin reaches the --no-auth host, and initialize returns the read-only instructions");
            var (_, listed) = await Post(http, Extension, "tools/list", new { });
            string[] names = listed!.Value.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();
            Check(names.Length == 11 && names.SequenceEqual(CodexishRuntime.ReadOnlyToolNames.Order(StringComparer.Ordinal)),
                "a read_only host lists exactly the 11 read tools");
            foreach (string removed in new[] { "fs_write", "computer_act", "shell_run" })
            {
                var (_, refused) = await Post(http, Extension, "tools/call", new { name = removed, arguments = new { } });
                Check(refused!.Value.TryGetProperty("error", out var error) && !refused.Value.TryGetProperty("result", out _) &&
                      error.GetProperty("message").GetString() == $"Unknown tool: '{removed}'",
                    $"{removed} called through the read_only HTTP host fails as an unknown tool");
            }
            var (_, read) = await Post(http, Extension, "tools/call", new { name = "fs_read", arguments = new { root_id = "proj", path = "seen.txt" } });
            Check(read!.Value.GetProperty("result").GetProperty("structuredContent").GetProperty("status").GetString() == "succeeded",
                "fs_read still reads a file through the read_only host");
            var (_, capabilities) = await Post(http, Extension, "tools/call", new { name = "host_capabilities", arguments = new { } });
            var (_, workspace) = await Post(http, Extension, "tools/call", new { name = "workspace_info", arguments = new { } });
            var described = capabilities!.Value.GetProperty("result").GetProperty("structuredContent").GetProperty("data");
            var info = workspace!.Value.GetProperty("result").GetProperty("structuredContent").GetProperty("data");
            Check(described.GetProperty("read_only").GetBoolean() && described.GetProperty("tools").GetArrayLength() == 11 &&
                  info.GetProperty("read_only").GetBoolean() && !info.GetProperty("roots")[0].GetProperty("grants").GetProperty("Write").GetBoolean(),
                "host_capabilities and workspace_info report read_only true, the 11 tools and the read-only grants");
            var (forbidden, _) = await Post(http, "chrome-extension://ponmlkjihgfedcbaponmlkjihgfedcba", "tools/list", new { });
            Check(forbidden == HttpStatusCode.Forbidden && log.Any(l => l.Contains("reason=origin_not_allowed", StringComparison.Ordinal)),
                "an unlisted extension origin gets 403 origin_not_allowed");
        }
        finally { await app.StopAsync(); }
    }

    private static async Task DefaultInstance(string temp, string password)
    {
        var config = Loopback(Path.Combine(temp, "review-default"), password, false);
        Check(config.Roots.All(r => r.Write && r.Shell) && !config.Warnings.Any(w => w.StartsWith("read_only", StringComparison.Ordinal)),
            "with read_only false the root grants stay as configured, without a warning");
        using var runtime = new CodexishRuntime(config, authDisabled: true);
        await using var app = CodexishHost.Build(runtime, 0, _ => { });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
            var (_, initialized) = await Post(http, null, "initialize", new
            {
                protocolVersion = CodexishRuntime.ProtocolVersion, capabilities = new { },
                clientInfo = new { name = "codexish-self-test", version = CodexishRuntime.ServerVersion }
            });
            var (_, listed) = await Post(http, null, "tools/list", new { });
            string[] names = listed!.Value.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();
            var (_, capabilities) = await Post(http, null, "tools/call", new { name = "host_capabilities", arguments = new { } });
            Check(names.Length == 24 && names.SequenceEqual(CodexishRuntime.ToolNames.Order(StringComparer.Ordinal)) &&
                  initialized!.Value.GetProperty("result").GetProperty("instructions").GetString() == CodexishRuntime.Instructions &&
                  !capabilities!.Value.GetProperty("result").GetProperty("structuredContent").GetProperty("data").GetProperty("read_only").GetBoolean(),
                "with read_only false the host lists the same 24 tools as before, with the full instructions and read_only false");
        }
        finally { await app.StopAsync(); }
    }

    // One JSON-RPC request, with an Origin header when one is given; the body is null when the request was refused.
    private static async Task<(HttpStatusCode Status, JsonElement? Body)> Post(HttpClient http, string? origin, string method, object parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters }), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", CodexishRuntime.ProtocolVersion);
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return (response.StatusCode, null);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = string.Join("\n", text.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => l[6..].TrimEnd('\r')));
        using var document = JsonDocument.Parse(text);
        return (response.StatusCode, document.RootElement.Clone());
    }
}
