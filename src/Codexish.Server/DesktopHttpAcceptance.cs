using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

// Explicit test mode: synthetic OAuth, a fresh root, and only a new owned WPF window.
// Uses the normal HTTP host and tools; no replacement backend or production settings.
public static class DesktopHttpAcceptance
{
    private static int passed, sequence;
    private static JsonElement Data(CallToolResult r) => r.StructuredContent!.Value.GetProperty("data");
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException(name);
        Console.WriteLine($"DESKTOP HTTP PASS {++passed:00} {name}");
    }
    private static void Require(CallToolResult r, string step)
    {
        if (Reply.CodeOf(r) is { } code) throw new InvalidOperationException(step + ": " + code);
    }
    private static async Task<JsonElement> Rpc(HttpClient http, string method, object parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++sequence, method, @params = parameters }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        string text = await response.Content.ReadAsStringAsync();
        foreach (string line in text.Split('\n').Reverse())
        {
            string json = line.StartsWith("data: ", StringComparison.Ordinal) ? line[6..] : line;
            if (!json.TrimStart().StartsWith('{')) continue;
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("result", out var result)) return result.Clone();
            if (document.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException("MCP protocol error " + error.GetProperty("code").GetInt32());
        }
        throw new InvalidOperationException("No MCP JSON result.");
    }
    private static async Task<CallToolResult> Call(HttpClient http, string name, object arguments) =>
        (await Rpc(http, "tools/call", new { name, arguments })).Deserialize<CallToolResult>()!;
    private static async Task<string> Login(HttpClient http, ServerConfig config, string password)
    {
        const string redirect = "https://fixture.invalid/callback";
        string verifier = ServerConfig.NewSecret(40);
        var fields = new Dictionary<string, string> {
            ["response_type"] = "code", ["client_id"] = config.OAuth.ClientId, ["redirect_uri"] = redirect,
            ["scope"] = "mcp", ["state"] = "desktop-http", ["resource"] = config.Resource,
            ["code_challenge"] = ServerConfig.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))),
            ["code_challenge_method"] = "S256" };
        string url = "/authorize?" + string.Join("&", fields.Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.Value)));
        string form = await http.GetStringAsync(url);
        var match = Regex.Match(form, "name=\"nonce\" value=\"([^\"]+)\"");
        if (!match.Success) throw new InvalidOperationException("Login nonce missing.");
        fields["nonce"] = match.Groups[1].Value; fields["password"] = password;
        using var authorized = await http.PostAsync("/authorize", new FormUrlEncodedContent(fields));
        Check(authorized.StatusCode == HttpStatusCode.Redirect, "synthetic OAuth login returns redirect");
        var query = QueryHelpers.ParseQuery(authorized.Headers.Location!.Query);
        Check(query["state"] == "desktop-http" && query["iss"] == config.PublicUrl, "state and issuer retained");
        using var exchanged = await http.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["grant_type"] = "authorization_code", ["code"] = query["code"].ToString(),
            ["client_id"] = config.OAuth.ClientId, ["client_secret"] = config.OAuth.ClientSecret,
            ["redirect_uri"] = redirect, ["resource"] = config.Resource, ["code_verifier"] = verifier }));
        exchanged.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await exchanged.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("access_token").GetString()!;
    }
    public static async Task<int> Run()
    {
#if WINDOWS
        passed = sequence = 0;
        string directory = Path.Combine(Path.GetTempPath(), "codexish-desktop-http-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "root"), fixtureDirectory = Path.Combine(root, "fixture");
        Directory.CreateDirectory(fixtureDirectory); Process? fixture = null;
        try
        {
            string password = ServerConfig.NewSecret(20);
            var config = ServerConfig.Create("https://fixture.invalid", password, [("fixture", root)],
                ["https://fixture.invalid/callback"], 0, Path.Combine(directory, "state"));
            using var runtime = new CodexishRuntime(config);
            await using var app = CodexishHost.Build(runtime, 0, _ => { });
            await app.StartAsync();
            try
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var http = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
                using (var denied = await http.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json")))
                    Check(denied.StatusCode == HttpStatusCode.Unauthorized, "unauthenticated MCP rejected before fixture input");
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(http, config, password));
                var init = await Rpc(http, "initialize", new { protocolVersion = CodexishRuntime.ProtocolVersion,
                    capabilities = new { }, clientInfo = new { name = "desktop-http-fixture", version = "1" } });
                Check(init.GetProperty("protocolVersion").GetString() == CodexishRuntime.ProtocolVersion, "authenticated initialize succeeds");
                var listed = await Rpc(http, "tools/list", new { });
                var names = listed.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
                Check(new[] { "computer_observe", "computer_query_ui", "computer_act", "operation_inspect", "fs_read" }.All(names.Contains), "desktop and verification tools registered");
                string executable = Environment.ProcessPath!;
                var start = new ProcessStartInfo(executable) { UseShellExecute = false };
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("--desktop-fixture"); start.ArgumentList.Add(fixtureDirectory);
                fixture = Process.Start(start)!;
                string ready = Path.Combine(fixtureDirectory, "ready.json");
                for (int i = 0; i < 150 && !File.Exists(ready) && !fixture.HasExited; i++) await Task.Delay(100);
                using var readyData = JsonDocument.Parse(await File.ReadAllTextAsync(ready));
                long handle = readyData.RootElement.GetProperty("handle").GetInt64();
                string window = $"win_{handle:x}_{fixture.Id}_{fixture.StartTime.ToUniversalTime().Ticks}";
                Check(GetForegroundWindow() == (nint)handle, "new fixture owns foreground before any screenshot");
                var before = await Call(http, "computer_observe", new { window_id = window, window_only = true, max_width = 640 });
                Require(before, "observe");
                Check(before.Content.OfType<ImageContentBlock>().Count() == 1 && Data(before).GetProperty("window_id").GetString() == window,
                    "HTTP carries fixture PNG and bound window identity");
                Check(Data(before).GetProperty("ui").GetProperty("active_tabs").EnumerateArray().Any(t => t.GetString() == "Untitled fixture"), "selected-tab UIA metadata retained");
                string observation = Data(before).GetProperty("observation_id").GetString()!;
                var page = await Call(http, "computer_query_ui", new { observation_id = observation, page_size = 1 }); Require(page, "query");
                string cursor = Data(page).GetProperty("next_cursor").GetString()!;
                var next = await Call(http, "computer_query_ui", new { observation_id = observation, page_size = 1, cursor });
                var replay = await Call(http, "computer_query_ui", new { observation_id = observation, page_size = 1, cursor });
                Check(next.StructuredContent!.Value.GetRawText() == replay.StructuredContent!.Value.GetRawText(), "HTTP cursor replay is identical including next cursor");
                var edit = await Call(http, "computer_query_ui", new { observation_id = observation, role = "Edit", name = "CODEXish fixture editor" });
                Require(edit, "editor lookup"); string element = Data(edit).GetProperty("elements")[0].GetProperty("element_id").GetString()!;
                async Task<CallToolResult> Complete(CallToolResult value)
                {
                    for (int i = 0; Reply.StatusOf(value) is "running" or "queued"; i++)
                    {
                        if (i >= 150) throw new InvalidOperationException("Test watchdog: operation remains pending.");
                        string id = Data(value).GetProperty("operation_id").GetString()!;
                        await Task.Delay(100); value = await Call(http, "operation_inspect", new { operation_id = id });
                    }
                    return value;
                }
                async Task<CallToolResult> Action(string action, string? key = null, string? text = null, string? elementId = null, string? invocation = null)
                {
                    var value = await Complete(await Call(http, "computer_act", new { observation_id = observation, action,
                        coordinate_space = "none", invocation_id = invocation ?? Guid.NewGuid().ToString("N"), key, text, element_id = elementId }));
                    Require(value, action);
                    observation = Data(value).GetProperty("observation").GetProperty("data").GetProperty("observation_id").GetString()!;
                    return value;
                }
                await Action("click_element", elementId: element);
                await Action("key_combo", key: "CTRL+A");
                const string expected = "CODEXish HTTP desktop - 한글🙂";
                string originalObservation = observation, writeId = Guid.NewGuid().ToString("N");
                var typed = await Action("type_text", text: expected, invocation: writeId);
                Check(typed.Content.OfType<ImageContentBlock>().Count() == 1, "native input returns post-action image over HTTP");
                var duplicate = await Complete(await Call(http, "computer_act", new { observation_id = originalObservation,
                    action = "type_text", coordinate_space = "none", invocation_id = writeId, text = expected }));
                Require(duplicate, "identical retry");
                Check(duplicate.StructuredContent!.Value.GetRawText() == typed.StructuredContent!.Value.GetRawText(), "HTTP retry returns stored image-bearing result");
                await Action("key_combo", key: "CTRL+S");
                string savedPath = Path.Combine(fixtureDirectory, "saved.txt");
                for (int i = 0; i < 100 && !File.Exists(savedPath); i++) await Task.Delay(100);
                byte[] saved = await File.ReadAllBytesAsync(savedPath);
                Check(saved.SequenceEqual(Encoding.UTF8.GetBytes(expected)), "HTTP-driven Unicode input saved exactly once with exact UTF-8 bytes");
                var read = await Call(http, "fs_read", new { root_id = "fixture", path = "fixture/saved.txt" }); Require(read, "fs verification");
                // fs_read returns the file text in the envelope's output block, not in data.
                Check(read.StructuredContent!.Value.GetProperty("output").GetProperty("text").GetString() == expected, "HTTP fs_read independently verifies GUI output");
                Console.WriteLine($"DESKTOP_HTTP_SAVED bytes={saved.Length} sha256={Convert.ToHexString(SHA256.HashData(saved))}");
                Check(Directory.GetFiles(fixtureDirectory).Select(Path.GetFileName).Order().SequenceEqual(new[] { "ready.json", "saved.txt" }), "fixture contains only ready record and saved text");
                Console.WriteLine($"DESKTOP_HTTP_PASSED: {passed}; own WPF window and synthetic OAuth only; not ChatGPT or Notepad Save As.");
                return 0;
            }
            finally { await app.StopAsync(); }
        }
        catch (Exception e) { Console.Error.WriteLine("DESKTOP_HTTP_FAILED: " + e); return 1; }
        finally
        {
            if (fixture is not null)
            {
                if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(5000)) fixture.Kill(); }
                fixture.Dispose();
            }
            TestCleanup.RemoveDirectory(directory);
        }
#else
        Console.Error.WriteLine("DESKTOP_HTTP_NOT_RUN: interactive Windows build required."); return 2;
#endif
    }
#if WINDOWS
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
#endif
}
