using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;

namespace Codexish.P0;

public static class SelfTest
{
    private static int passed;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine($"PASS {++passed:00} {label}");
    }
    private static JsonElement Data(CallToolResult r) => r.StructuredContent!.Value.GetProperty("data");
    private static string? Error(CallToolResult r) => r.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString();
    private static string? Status(CallToolResult r) => r.StructuredContent!.Value.GetProperty("status").GetString();

    public static async Task<int> Run()
    {
        string root = ProbeRuntime.CreateFixture();
        try
        {
            using (var runtime = new ProbeRuntime(root))
            {
                var tools = new ProbeTools(runtime);
                Check(ProbeRuntime.Names.Length == 6, "six disposable fixtures");
                var read = tools.ReadFile("case01.txt");
                string hash = Data(read).GetProperty("sha256").GetString()!;
                Check(Data(read).GetProperty("text").GetString() == "0\n", "read actual fixture and hash");
                Check(Error(tools.ReadFile("../case01.txt")) == "OUTSIDE_WORKSPACE", "reject parent paths");
                Check(Error(tools.ReadFile(".codexish-p0")) == "OUTSIDE_WORKSPACE", "do not expose private marker");
                Check(Error(await tools.WriteFile("gui-result.txt", "fake", "x", "no-forge")) == "OUTSIDE_WORKSPACE", "cannot forge GUI result through write tool");
                Check(Error(await tools.WriteFile("case01.txt", "1\n", "wrong", "bad-hash")) == "FILE_CHANGED", "expected hash required");
                Check(File.ReadAllText(Path.Combine(root, "case01.txt")) == "0\n", "hash failure preserves original");
                var wrote = await tools.WriteFile("case01.txt", "1\n", hash, "one");
                Check(Status(wrote) == "succeeded", "conditional write succeeds");
                string backup = Data(wrote).GetProperty("backup").GetString()!;
                Check(File.ReadAllText(Path.Combine(runtime.StateDirectory, backup)) == "0\n", "backup contains old bytes");
                Check(Data(wrote).GetProperty("save_mode").GetString() == "exclusive_in_place_non_atomic", "non-atomic contract disclosed");
                int backups = Directory.GetFiles(runtime.StateDirectory, "*.bak").Length;
                var replay = await tools.WriteFile("case01.txt", "1\n", hash, "one");
                Check(Status(replay) == "succeeded" && Directory.GetFiles(runtime.StateDirectory, "*.bak").Length == backups, "retry returns stored result without repeating write");
                Check(Error(await tools.WriteFile("case01.txt", "2\n", hash, "one")) == "IDEMPOTENCY_CONFLICT", "different arguments conflict");

                string second = Path.Combine(root, "case02.txt");
                var utf16 = new UnicodeEncoding(false, true, true);
                File.WriteAllText(second, "old\r\n", utf16);
                var secondHash = Data(tools.ReadFile("case02.txt")).GetProperty("sha256").GetString()!;
                await tools.WriteFile("case02.txt", "new\n", secondHash, "utf16");
                byte[] bytes = File.ReadAllBytes(second);
                Check(bytes[0] == 0xff && bytes[1] == 0xfe && ProbeRuntime.Decode(bytes).text == "new\r\n", "UTF-16 BOM and CRLF preserved");
                File.WriteAllText(second, "old\n", new UTF8Encoding(true));
                secondHash = Data(tools.ReadFile("case02.txt")).GetProperty("sha256").GetString()!;
                await tools.WriteFile("case02.txt", "new\r\n", secondHash, "utf8");
                bytes = File.ReadAllBytes(second);
                Check(bytes[0] == 0xef && ProbeRuntime.Decode(bytes).text == "new\n", "UTF-8 BOM and LF preserved");
                File.WriteAllText(second, "a\r\nb\n", new UTF8Encoding(false));
                secondHash = Data(tools.ReadFile("case02.txt")).GetProperty("sha256").GetString()!;
                Check(Error(await tools.WriteFile("case02.txt", "x", secondHash, "mixed")) == "UNSUPPORTED_CAPABILITY", "mixed newline conversion rejected");
                if (OperatingSystem.IsWindows())
                {
                    using var other = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    Check(Error(await tools.WriteFile("case02.txt", "x", secondHash, "locked")) == "FILE_LOCKED", "Windows incompatible shared handle rejects exclusive write");
                }
                else Console.WriteLine("SKIP Windows sharing/native desktop: non-Windows environment");

                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int effects = 0;
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Func<Task<CallToolResult>> action = async () => { entered.TrySetResult(true); await release.Task; Interlocked.Increment(ref effects); return Reply.Ok(new { count = effects }); };
                var pending = await runtime.Invoke("slow", "test-slow", new { value = 1 }, "slow-resource", action, 0);
                Check(Status(pending) == "running", "response wait returns operation handle");
                await entered.Task;
                var joined = runtime.Invoke("slow", "test-slow", new { value = 1 }, "slow-resource", action, 5000);
                var queued = runtime.Invoke("queued", "test-queue", new { value = 2 }, "slow-resource", () => Task.FromResult(Reply.Ok(new { ran = true })), 0);
                Check(Status(await queued) == "running", "same resource queues instead of busy rejection");
                var independent = await runtime.Invoke("other", "test-other", new { value = 3 }, "other-resource", () => Task.FromResult(Reply.Ok(new { ran = true })), 5000);
                Check(Status(independent) == "succeeded", "independent resource progresses");
                Check(Data(tools.ReadFile("case01.txt")).GetProperty("text").GetString() == "1\n", "read continues while operation waits");
                release.SetResult(true);
                Check(Status(await joined) == "succeeded" && effects == 1, "duplicate joins same effect");
                var queueDone = await runtime.Invoke("queued", "test-queue", new { value = 2 }, "slow-resource", () => throw new Exception("must not replay"), 5000);
                Check(Status(queueDone) == "succeeded", "queued work automatically completes");
                Check(Error(runtime.Inspect("missing")) == "NOT_FOUND", "unknown operation ID rejected");
                Check(Error(await tools.RunCommand("whoami", "forbidden")) == "PERMISSION_DENIED", "no arbitrary shell command");
                Check(Error(tools.Screenshot()) == "UNSUPPORTED_CAPABILITY", "no synthetic capture fallback");
                Check(Error(await tools.Click(1, 1, "invented", "no-input", "image")) == "UNSUPPORTED_CAPABILITY", "GUI input disabled without local opt-in");
                var failed = await runtime.RunChild("test");
                Check(Data(failed).GetProperty("exit_code").GetInt32() == 1 && !Data(failed).GetProperty("test_passed").GetBoolean(), "real child reports failing fixture");
                for (int i = 0; i < ProbeRuntime.Names.Length; i++)
                    File.WriteAllText(Path.Combine(root, ProbeRuntime.Names[i]), $"{i + 1}\n");
                var succeeded = await runtime.RunChild("test");
                Check(Data(succeeded).GetProperty("exit_code").GetInt32() == 0 && Data(succeeded).GetProperty("test_passed").GetBoolean(), "real child reports passing fixture and drained output");
                await HttpTests(runtime, true);
                await HttpTests(runtime, false);
                await PreMeasurementTests.Run(runtime, Check);
            }
            using (var db = new SqliteConnection($"Data Source={root}.state/p0.db;Pooling=False"))
            {
                db.Open(); using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT INTO invocations VALUES('crash','digest','running',NULL)"; cmd.ExecuteNonQuery();
            }
            using (var restarted = new ProbeRuntime(root))
            {
                Check(Status(restarted.Inspect("one")) == "succeeded", "result persists across restart");
                Check(Error(restarted.Inspect("crash")) == "EXECUTION_UNKNOWN", "unfinished record becomes unknown on restart");
            }
            Console.WriteLine($"SELF_TEST_PASSED: {passed}; Chat and interactive desktop measurements NOT performed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine($"SELF_TEST_FAILED after {passed}: {e}"); return 1; }
        finally
        {
            // These paths were generated exclusively for this test invocation.
            try { Directory.Delete(root, true); Directory.Delete(root + ".state", true); } catch (IOException) { }
        }
    }

    private static async Task HttpTests(ProbeRuntime runtime, bool instructions)
    {
        await using var app = ProbeHost.Build(runtime, 0, instructions);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
            async Task<JsonElement> Rpc(string method, object parameters)
            {
                var request = new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters };
                using var response = await http.PostAsync("/mcp", new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"));
                string text = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {text}");
                if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    text = string.Join("\n", text.Split('\n').Where(line => line.StartsWith("data: ")).Select(line => line[6..].TrimEnd('\r')));
                using var document = JsonDocument.Parse(text);
                return document.RootElement.GetProperty("result").Clone();
            }
            var initialized = await Rpc("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "P0-self-test", version = "0.2.0" } });
            Check(initialized.GetProperty("protocolVersion").GetString() == "2025-11-25", "HTTP initialize negotiated protocol");
            Check(initialized.TryGetProperty("instructions", out var instr) == instructions && (!instructions || instr.GetString() == ProbeHost.Instructions), "instructions A/B configuration");
            var listed = await Rpc("tools/list", new { });
            var tools = listed.GetProperty("tools").EnumerateArray().ToArray();
            Check(tools.Length == 7 && tools.Select(t => t.GetProperty("name").GetString()).Distinct().Count() == 7, "HTTP tools/list exposes exactly seven tools");
            Check(tools.All(t => t.GetProperty("inputSchema").GetProperty("type").GetString() == "object"), "official SDK generated object schemas");
            Check(tools.Single(t => t.GetProperty("name").GetString() == "write_file").GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean() == false, "write tool not marked read-only");
            var echo = await Rpc("tools/call", new { name = "echo", arguments = new { message = "protocol smoke" } });
            Check(echo.GetProperty("structuredContent").GetProperty("data").GetProperty("message").GetString() == "protocol smoke", "HTTP tool call has text and structured content");
            var missing = await Rpc("tools/call", new { name = "read_file", arguments = new { path = "../private" } });
            Check(missing.GetProperty("isError").GetBoolean(), "HTTP tool error preserved");
            using var spoof = new HttpRequestMessage(HttpMethod.Get, "/healthz"); spoof.Headers.Host = "attacker.invalid";
            using var blocked = await http.SendAsync(spoof);
            Check(blocked.StatusCode == HttpStatusCode.Forbidden, "untrusted Host blocked");
            using var cross = new HttpRequestMessage(HttpMethod.Get, "/healthz"); cross.Headers.Add("Origin", "https://attacker.invalid");
            using var crossResponse = await http.SendAsync(cross);
            Check(crossResponse.StatusCode == HttpStatusCode.Forbidden, "cross-origin access blocked");
            if (instructions)
            {
                string? output = Environment.GetEnvironmentVariable("CODEXISH_TEST_OUTPUT");
                if (output is not null) { Directory.CreateDirectory(output); await File.WriteAllTextAsync(Path.Combine(output, "p0-tools.json"), listed.GetRawText()); }
            }
        }
        finally { await app.StopAsync(); }
    }
}
