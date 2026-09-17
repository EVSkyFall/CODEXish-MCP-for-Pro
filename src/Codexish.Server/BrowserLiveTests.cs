using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

// Explicit integration trial: a locally installed Playwright MCP drives a fresh headless browser with a dedicated
// profile under the test's state_dir, against a loopback page served by the test host itself.
public static class BrowserLiveTests
{
    // Test watchdog only: a backend call that never completes fails the trial instead of hanging it.
    private static readonly TimeSpan CallWatchdog = TimeSpan.FromSeconds(120);

    public static async Task<int> Run(string node, string cli, string browser)
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-browser-live-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "root");
        Directory.CreateDirectory(root);
        // The backend inherits TEMP/TMP; pointing them into this trial's directory removes Chrome's temp files with it.
        string temp = Path.Combine(directory, "temp");
        Directory.CreateDirectory(temp);
        string? originalTemp = Environment.GetEnvironmentVariable("TEMP"), originalTmp = Environment.GetEnvironmentVariable("TMP");
        Environment.SetEnvironmentVariable("TEMP", temp);
        Environment.SetEnvironmentVariable("TMP", temp);
        int passed = 0;
        void Check(bool value, string text)
        {
            if (!value) throw new InvalidOperationException(text);
            Console.WriteLine($"BROWSER LIVE PASS {++passed:00} {text}");
        }
        try
        {
            var config = ServerConfig.Create("", "local-test-only", [("test", root)], [], 0, Path.Combine(directory, "state"));
            config.BrowserMounts = [new() { Id = "pw", RootId = "test", Kind = "playwright", ProfileMode = "dedicated", Command = node,
                Args = [cli, "--headless", "--browser", "chrome", "--executable-path", browser, "--output-dir", root], ReadOnlyTools = ["browser_snapshot"] }];
            using (var runtime = new CodexishRuntime(config, true))
            {
                await runtime.Browsers.InitializeAsync();
                Console.WriteLine("BACKEND " + JsonSerializer.Serialize(runtime.Browsers.Describe()));
                Check(runtime.Browsers.Tools.Count > 0, "the installed Playwright MCP backend completes the stdio handshake and lists its tools");
                await using var app = CodexishHost.Build(runtime, 0);
                app.MapGet("/fixture", () => Results.Content("""<!doctype html><html><head><meta charset="utf-8"><title>CODEXish browser fixture</title></head><body><label for="message">Message</label><input id="message"><button id="save" onclick="document.getElementById('result').textContent=document.getElementById('message').value">Save fixture</button><p id="result"></p></body></html>""", "text/html; charset=utf-8"));
                await app.StartAsync();
                try
                {
                    string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                    await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(address + "/mcp") }),
                        new() { ProtocolVersion = CodexishRuntime.ProtocolVersion });
                    var listed = await client.ListToolsAsync();
                    var mounted = listed.Where(t => t.Name.StartsWith("browser_pw_", StringComparison.Ordinal)).ToArray();
                    Check(mounted.Length == runtime.Browsers.Tools.Count && mounted.All(t => t.Name.Length <= BrowserMounts.MaxToolName && Regex.IsMatch(t.Name, "^[a-zA-Z0-9_-]+$")),
                        $"all {mounted.Length} mounted tools are listed over HTTP with valid connector names");
                    var capabilities = (await client.CallToolAsync(new CallToolRequestParams { Name = "host_capabilities", Arguments = new Dictionary<string, JsonElement>() }))
                        .StructuredContent!.Value.GetProperty("data").GetProperty("browser").GetProperty("mounted")[0];
                    string profile = Path.Combine(config.StateDir, "browser-profiles", "pw");
                    Check(capabilities.GetProperty("state").GetString() == "connected" && capabilities.GetProperty("profile_directory").GetString() == profile && Directory.Exists(profile),
                        "the mount is connected with its dedicated profile directory under state_dir");

                    // The wrapped schema from tools/list decides the element reference key: Playwright MCP 0.0.81 renamed ref to target.
                    JsonElement Schema(string backend) => listed.Single(t => t.Name == BrowserMounts.ToolName("pw", backend)).ProtocolTool.InputSchema;
                    JsonElement Resolve(JsonElement schemaRoot, JsonElement node)
                    {
                        while (node.TryGetProperty("$ref", out var reference) && reference.GetString() is { } pointer && pointer.StartsWith("#/", StringComparison.Ordinal))
                        {
                            node = schemaRoot;
                            foreach (string segment in pointer[2..].Split('/')) node = node.GetProperty(segment.Replace("~1", "/").Replace("~0", "~"));
                        }
                        return node;
                    }
                    string ReferenceKey(JsonElement properties) =>
                        properties.TryGetProperty("target", out _) ? "target" : properties.TryGetProperty("ref", out _) ? "ref"
                        : throw new InvalidOperationException("The schema has neither target nor ref: " + properties.GetRawText());
                    var fillSchema = Schema("browser_fill_form");
                    var fillArguments = Resolve(fillSchema, fillSchema.GetProperty("properties").GetProperty("arguments"));
                    var fields = Resolve(fillSchema, fillArguments.GetProperty("properties").GetProperty("fields"));
                    string fillKey = ReferenceKey(Resolve(fillSchema, Resolve(fillSchema, fields.GetProperty("items"))).GetProperty("properties"));
                    var clickSchema = Schema("browser_click");
                    string clickKey = ReferenceKey(Resolve(clickSchema, clickSchema.GetProperty("properties").GetProperty("arguments")).GetProperty("properties"));
                    Console.WriteLine($"SCHEMA browser_fill_form fields[].{fillKey}; browser_click {clickKey}");

                    int sequence = 0;
                    async Task<CallToolResult> Call(string backend, Dictionary<string, object?> body)
                    {
                        var watchdog = Stopwatch.StartNew();
                        var result = await client.CallToolAsync(new CallToolRequestParams
                        {
                            Name = BrowserMounts.ToolName("pw", backend),
                            Arguments = new Dictionary<string, JsonElement>
                            {
                                ["arguments"] = JsonSerializer.SerializeToElement(body),
                                ["invocation_id"] = JsonSerializer.SerializeToElement("live-" + ++sequence)
                            }
                        });
                        while (Reply.StatusOf(result) is "running" or "queued")
                        {
                            if (watchdog.Elapsed > CallWatchdog) throw new InvalidOperationException($"{backend}: test watchdog, operation still {Reply.StatusOf(result)}");
                            await Task.Delay(100);
                            string operation = result.StructuredContent!.Value.GetProperty("data").GetProperty("operation_id").GetString()!;
                            result = await client.CallToolAsync(new CallToolRequestParams
                            {
                                Name = "operation_inspect",
                                Arguments = new Dictionary<string, JsonElement> { ["operation_id"] = JsonSerializer.SerializeToElement(operation) }
                            });
                        }
                        if (Reply.CodeOf(result) is { } error) throw new InvalidOperationException($"{backend}: {error} {result.StructuredContent}");
                        return result;
                    }
                    string Text(CallToolResult result) =>
                        string.Join("\n", result.StructuredContent!.Value.GetProperty("data").GetProperty("text").EnumerateArray().Select(t => t.GetString()));

                    await Call("browser_navigate", new() { ["url"] = address + "/fixture" });
                    string snapshot = Text(await Call("browser_snapshot", new()));
                    Check(snapshot.Contains("CODEXish browser fixture", StringComparison.Ordinal), "the headless browser navigates to the owned loopback fixture");
                    Check(Directory.EnumerateFileSystemEntries(profile).Any(), "the browser keeps its state in the dedicated profile directory under state_dir");
                    string Reference(string role) => Regex.Match(snapshot, role + @"[^\n]*\[ref=([^\]]+)\]").Groups[1].Value;
                    string editor = Reference("textbox"), button = Reference("button");
                    Check(editor.Length > 0 && button.Length > 0, "the accessibility snapshot supplies element references");
                    const string expected = "CODEXish browser verification 한글";
                    await Call("browser_fill_form", new()
                    {
                        ["fields"] = new[] { new Dictionary<string, object?> { ["name"] = "Message", ["type"] = "textbox", [fillKey] = editor, ["value"] = expected } }
                    });
                    await Call("browser_click", new() { ["element"] = "Save fixture", [clickKey] = button });
                    string final = Text(await Call("browser_snapshot", new()));
                    Check(final.Contains(expected, StringComparison.Ordinal), $"browser_fill_form ({fillKey}) and browser_click ({clickKey}) change the real page");
                    var image = await Call("browser_take_screenshot", new() { ["type"] = "png" });
                    Check(image.Content.OfType<ImageContentBlock>().Any(), "the backend screenshot passes through the HTTP proxy as image content");
                    await Call("browser_close", new());
                }
                finally { await app.StopAsync(); }
            }
            Console.WriteLine($"BROWSER_LIVE_PASSED: {passed}; installed Playwright MCP with a dedicated headless profile and a loopback fixture, not ChatGPT or a personal profile.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine($"BROWSER_LIVE_FAILED after {passed}: {error}"); return 1; }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", originalTemp);
            Environment.SetEnvironmentVariable("TMP", originalTmp);
            // The browser can hold its profile files for a moment after the backend exits.
            TestCleanup.RemoveDirectory(directory);
        }
    }
}
