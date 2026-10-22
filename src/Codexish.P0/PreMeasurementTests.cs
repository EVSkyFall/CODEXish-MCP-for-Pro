using System.Drawing;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Codexish.P0;

// F-1/F-2 and L-1 constructor regression tests. Bitmap fixtures never count as real desktop or Chat evidence.
internal static class PreMeasurementTests
{
    public static async Task Run(ProbeRuntime runtime, Action<bool, string> check)
    {
        void Fault(Action action, string code, string label)
        {
            try { action(); check(false, label); }
            catch (ProbeFault e) { check(e.Code == code && e.Effects == "none", label); }
        }
        void InvalidOption(Action action, string label)
        {
            try { action(); check(false, label); }
            catch (ArgumentException) { check(true, label); }
        }
        check(ProbeOptions.Values(["--allow-host", "one.example", "--allow-host", "two.example"], "--allow-host")
            .SequenceEqual(["one.example", "two.example"]), "F1 repeated allow-host arguments retained");
        check(ProbeOptions.Values(["--allow-origin", "https://one.example", "--allow-origin", "https://two.example"], "--allow-origin")
            .Length == 2, "F1 repeated allow-origin arguments retained");
        InvalidOption(() => ProbeOptions.Values(["--allow-host", "--port", "3000"], "--allow-host"), "F1 missing host value rejected");
        InvalidOption(() => new ProbeAccessPolicy(["*.example"]), "F1 wildcard host rejected");
        InvalidOption(() => new ProbeAccessPolicy(["https://one.example"]), "F1 URL is not a hostname option");
        InvalidOption(() => new ProbeAccessPolicy(["one.example:3000"]), "F1 host option excludes ports");
        InvalidOption(() => new ProbeAccessPolicy(allowedOrigins: ["https://one.example/private"]), "F1 origin path rejected");
        InvalidOption(() => new ProbeAccessPolicy(allowedOrigins: ["https://user:password@one.example"]), "F1 origin credentials rejected");
        var defaultRequest = new DefaultHttpContext().Request;
        defaultRequest.Scheme = "http"; defaultRequest.Host = new HostString("tunnel.example");
        check(new ProbeAccessPolicy().RejectionReason(defaultRequest) == "host_not_allowed", "F1 public host denied without opt-in");
        await Http(runtime, check);
        if (OperatingSystem.IsWindows())
        {
            // Constructor rejection only: no Notepad launch/input, and not a live-exit regression.
            InvalidOption(() => new NativeDesktop(int.MaxValue), "L1 missing Notepad PID rejected during construction (not live-exit test)");
        }
        else Console.WriteLine("SKIP L1 missing-PID constructor: non-Windows environment; Current() mapping reviewed statically.");

        var half = ScreenshotGeometry.Fit(2560, 1440);
        check(half.ImageWidth == 1280 && half.ImageHeight == 720 && half.ScaleX == 2 && half.ScaleY == 2,
            "F2 default 2560x1440 capture maps to 1280x720");
        check(half.ToPhysical(640, 360, "image") == (1280, 720), "F2 image coordinates converted once on server");
        check(half.ToPhysical(1280, 720, "primary_monitor_physical_px") == (1280, 720), "F2 explicit physical coordinates unchanged");
        var native = ScreenshotGeometry.Fit(2560, 1440, 0);
        check(native.ImageWidth == 2560 && native.ImageHeight == 1440 && native.ScaleX == 1 && native.ScaleY == 1,
            "F2 max_width zero preserves native dimensions");
        check(native.ToPhysical(640, 360, "image") == (640, 360), "F2 each observation has its own transform");
        check(ScreenshotGeometry.Fit(800, 600) == new ScreenshotGeometry(800, 600, 800, 600), "F2 small captures never upscaled");
        check(ScreenshotGeometry.Fit(2560, 1440, 4096) == native, "F2 large maximum never upscales");
        var fractional = ScreenshotGeometry.Fit(1919, 1081, 1280);
        check(fractional.ImageHeight == 721 && fractional.ScaleY == 1081d / 721,
            "F2 rounded image height uses actual independent vertical scale");
        check(fractional.ToPhysical(1279, 720, "image") == ((int)(1279L * 1919 / 1280), (int)(720L * 1081 / 721)),
            "F2 fractional transform floors into physical bounds");
        check(ScreenshotGeometry.Fit(2560, 1440, 1).ImageHeight == 1, "F2 tiny requested width has positive height");
        Fault(() => ScreenshotGeometry.Fit(2560, 1440, -1), "INVALID_ARGUMENT", "F2 negative maximum rejected");
        Fault(() => half.ToPhysical(1280, 0, "image"), "INVALID_ARGUMENT", "F2 image right boundary rejected");
        Fault(() => half.ToPhysical(0, 720, "image"), "INVALID_ARGUMENT", "F2 image lower boundary rejected");
        Fault(() => half.ToPhysical(-1, 0, "image"), "INVALID_ARGUMENT", "F2 negative coordinate rejected");
        Fault(() => half.ToPhysical(2560, 0, "primary_monitor_physical_px"), "INVALID_ARGUMENT", "F2 physical right boundary rejected");
        Fault(() => half.ToPhysical(1, 1, "guess"), "INVALID_ARGUMENT", "F2 unknown coordinate space rejected");
        var badWidth = new ProbeTools(runtime).Screenshot(-1);
        check(badWidth.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "INVALID_ARGUMENT",
            "F2 invalid screenshot maximum rejected before OS capture");
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            // Test the production encoder with an artificial bitmap; do NOT invoke desktop capture/input in CI.
            using var fixture = new Bitmap(2560, 1440);
            using (var graphics = Graphics.FromImage(fixture)) graphics.Clear(Color.White);
            using var smallBytes = new MemoryStream(NativeDesktop.EncodePng(fixture, half));
            using var small = new Bitmap(smallBytes);
            check(small.Width == half.ImageWidth && small.Height == half.ImageHeight,
                "F2 Windows PNG encoder matches image metadata (synthetic bitmap, not screen)");
            using var fullBytes = new MemoryStream(NativeDesktop.EncodePng(fixture, native));
            using var full = new Bitmap(fullBytes);
            check(full.Width == native.PhysicalWidth && full.Height == native.PhysicalHeight,
                "F2 Windows native PNG dimensions preserved (synthetic bitmap, not screen)");
        }
        else Console.WriteLine("SKIP F2 GDI PNG encoding: non-Windows environment; pure coordinate tests ran.");
    }

    private static async Task Http(ProbeRuntime runtime, Action<bool, string> check)
    {
        var rejected = new List<string>();
        await using var app = ProbeHost.Build(runtime, 0, access: new ProbeAccessPolicy(
            ["tunnel.example", "second.example"], ["https://client.example", "https://second-client.example:8443"]),
            rejectionLog: line => { rejected.Add(line); Console.WriteLine(line); });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
            async Task<HttpStatusCode> Send(string host, string? origin = null, string? forwardedHost = null)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/healthz"); req.Headers.Host = host;
                if (origin is not null) req.Headers.Add("Origin", origin);
                if (forwardedHost is not null) req.Headers.Add("X-Forwarded-Host", forwardedHost);
                using var res = await http.SendAsync(req); return res.StatusCode;
            }
            check(await Send("tunnel.example") == HttpStatusCode.OK, "F1 configured tunnel host accepted without Origin");
            check(await Send("SECOND.EXAMPLE:443") == HttpStatusCode.OK, "F1 repeated host accepted case-insensitively");
            check(await Send("localhost:3000") == HttpStatusCode.OK, "F1 loopback Host still accepted");
            check(await Send("tunnel.example.attacker.invalid") == HttpStatusCode.Forbidden, "F1 suffix impersonation rejected");
            check(await Send("tunnel.example", "https://attacker.invalid") == HttpStatusCode.Forbidden, "F1 host allowance does not disable Origin checks");
            check(await Send("tunnel.example", "https://client.example") == HttpStatusCode.OK, "F1 explicit HTTPS origin accepted");
            check(await Send("tunnel.example", "https://client.example:443") == HttpStatusCode.OK, "F1 default origin port normalized");
            check(await Send("tunnel.example", "https://second-client.example:8443") == HttpStatusCode.OK, "F1 second exact origin accepted");
            check(await Send("tunnel.example", "https://client.example:444") == HttpStatusCode.Forbidden, "F1 wrong origin port rejected");
            check(await Send("localhost:3000", "http://localhost:3000") == HttpStatusCode.OK, "F1 local same-origin preserved");
            check(await Send("tunnel.example", "null") == HttpStatusCode.Forbidden, "F1 opaque null origin rejected");
            check(await Send("tunnel.example", "https://client.example/path") == HttpStatusCode.Forbidden, "F1 request origin with path rejected");
            check(await Send("tunnel.example", "https://client.example,https://attacker.invalid") == HttpStatusCode.Forbidden, "F1 multiple origins rejected");
            check(await Send("attacker.invalid", forwardedHost: "tunnel.example") == HttpStatusCode.Forbidden, "F1 forwarded header cannot bypass Host check");
            check(rejected.Any(l => l.Contains("host=\"tunnel.example\" origin=\"https://attacker.invalid\" reason=origin_not_allowed")),
                "F1 rejection log includes Host Origin and reason");
            check(rejected.All(l => !l.Contains('\n') && !l.Contains('\r')), "F1 rejection logs stay on a single line");

            // Exercise MCP itself behind an unchanged public Host, not just healthz.
            http.DefaultRequestHeaders.Host = "tunnel.example";
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
            http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
            async Task<JsonElement> Rpc(string method, object parameters)
            {
                using var res = await http.PostAsync("/mcp", new StringContent(JsonSerializer.Serialize(new
                    { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters }), Encoding.UTF8, "application/json"));
                res.EnsureSuccessStatusCode(); string text = await res.Content.ReadAsStringAsync();
                if (res.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    text = string.Join("\n", text.Split('\n').Where(l => l.StartsWith("data: ")).Select(l => l[6..].TrimEnd('\r')));
                using var doc = JsonDocument.Parse(text); return doc.RootElement.GetProperty("result").Clone();
            }
            var init = await Rpc("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "F1-regression", version = "0.2.1" } });
            check(init.GetProperty("serverInfo").GetProperty("version").GetString() == "0.2.1", "F1 MCP initializes with forwarded public Host");
            var tools = (await Rpc("tools/list", new { })).GetProperty("tools").EnumerateArray().ToArray();
            var screen = tools.Single(t => t.GetProperty("name").GetString() == "screenshot").GetProperty("inputSchema");
            check(screen.GetProperty("properties").GetProperty("max_width").GetProperty("default").GetInt32() == 1280, "F2 SDK schema exposes screenshot default width");
            var click = tools.Single(t => t.GetProperty("name").GetString() == "click").GetProperty("inputSchema");
            check(click.GetProperty("required").EnumerateArray().Any(v => v.GetString() == "coordinate_space"), "F2 click schema requires explicit units to avoid stale-client reinterpretation");
            var echo = await Rpc("tools/call", new { name = "echo", arguments = new { message = "F1 public Host" } });
            check(echo.GetProperty("structuredContent").GetProperty("data").GetProperty("message").GetString() == "F1 public Host", "F1 MCP call succeeds with allowed public Host");
        }
        finally { await app.StopAsync(); }
    }
}
