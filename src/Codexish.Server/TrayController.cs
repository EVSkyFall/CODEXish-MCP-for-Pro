using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Codexish.Server;

public sealed class TunnelConfig
{
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("args")] public string[] Args { get; set; } = [];
}

// The tray is a view/controller in the server process, not an additional identity or approval service.
public sealed class TrayController(ServerConfig config) : IAsyncDisposable
{
    private WebApplication? app;
    private CodexishRuntime? runtime;
    private Process? tunnel;
    private Task? stdout, stderr;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<string> diagnostics = [];
    public bool Running => app is not null;
    public bool TunnelRunning => tunnel is { HasExited: false };
    public Uri? Address { get; private set; }
    public string[] Diagnostics { get { lock (diagnostics) return diagnostics.ToArray(); } }

    private void Log(string value)
    {
        string line = DateTimeOffset.UtcNow.ToString("O") + " " + Redaction.Apply(value).Text;
        lock (diagnostics) diagnostics.Add(line);
    }

    public async Task StartAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (app is not null) return;
            if (ServerConfig.TransportRefusal(config, false) is { } refusal) throw new ArgumentException(refusal);
            var candidate = new CodexishRuntime(config);
            WebApplication? host = null;
            try
            {
                await candidate.Browsers.InitializeAsync();
                host = CodexishHost.Build(candidate, config.Port, Log);
                await host.StartAsync();
                Address = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                runtime = candidate; app = host;
                Log("Server started at " + Address + "; browser mounts: " + candidate.Browsers.Summary());
            }
            catch
            {
                if (host is not null) await host.DisposeAsync();
                candidate.Dispose();
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<string> ControlAsync(string action)
    {
        await gate.WaitAsync();
        try
        {
            if (app is null || Address is null) throw new InvalidOperationException("Start the server first.");
            if (action is not ("status" or "pause" or "resume" or "kill-children" or "revoke-tokens")) throw new ArgumentException("Unknown local action.");
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(action == "status" ? HttpMethod.Get : HttpMethod.Post, new Uri(Address, "/control/" + action));
            request.Headers.Add("X-Codexish-Control", config.ControlToken);
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            string result = await response.Content.ReadAsStringAsync();
            Log("Control " + action + " completed");
            return result;
        }
        finally { gate.Release(); }
    }

    // Only an explicit menu action starts the tunnel; starting the server never does.
    public async Task StartTunnelAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (TunnelRunning) return;
            if (app is null || Address is null) throw new InvalidOperationException("Start the authenticated server before its tunnel.");
            if (string.IsNullOrWhiteSpace(config.Tunnel.Command)) throw new InvalidOperationException("Configure tunnel.command and tunnel.args; no tunnel is downloaded or selected automatically.");
            tunnel?.Dispose();
            var start = new ProcessStartInfo(config.Tunnel.Command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string argument in config.Tunnel.Args) start.ArgumentList.Add(argument.Replace("{port}", Address.Port.ToString(), StringComparison.Ordinal));
            tunnel = Process.Start(start) ?? throw new IOException("Tunnel process did not start.");
            async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() is { } line) Log("Tunnel: " + line); }
            stdout = Drain(tunnel.StandardOutput);
            stderr = Drain(tunnel.StandardError);
            Log("Owned tunnel process started; remote connectivity is not yet verified.");
        }
        finally { gate.Release(); }
    }

    private async Task StopTunnelCore()
    {
        if (tunnel is null) return;
        if (!tunnel.HasExited) tunnel.Kill(entireProcessTree: true);
        await tunnel.WaitForExitAsync();
        await Task.WhenAll(stdout ?? Task.CompletedTask, stderr ?? Task.CompletedTask);
        tunnel.Dispose();
        tunnel = null;
        Log("Owned tunnel stopped.");
    }

    public async Task StopTunnelAsync()
    {
        await gate.WaitAsync();
        try { await StopTunnelCore(); }
        finally { gate.Release(); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try
        {
            await StopTunnelCore();
            if (app is null) return;
            await app.StopAsync();
            await app.DisposeAsync();
            app = null;
            runtime?.Dispose();
            runtime = null;
            Address = null;
            Log("Server stopped. Persistent child lifetime remains unchanged.");
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        gate.Dispose();
    }
}

// Controller lifecycle over the real local HTTP control endpoint; it creates no notification icon and starts no tunnel.
public static class TrayTests
{
    public static async Task<int> Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "codexish-tray-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "root"));
        int passed = 0;
        void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException(text);
            Console.WriteLine($"TRAY PASS {++passed:000} {text}");
        }
        try
        {
            var config = ServerConfig.Create("https://tray-test.invalid", "test-only-password", [("test", Path.Combine(directory, "root"))], [], 0, Path.Combine(directory, "state"));
            // A configured tunnel whose executable does not exist: if startup tried to launch it, StartAsync would fail.
            config.Tunnel = new TunnelConfig { Command = Path.Combine(directory, "tunnel-never-started" + (OperatingSystem.IsWindows() ? ".exe" : "")), Args = ["--url", "http://127.0.0.1:{port}"] };
            await using var controller = new TrayController(config);
            Check(!controller.Running, "controller starts stopped");
            await controller.StartAsync();
            var address = controller.Address;
            Check(controller.Running && address?.IsLoopback == true, "server starts inside the controller process on loopback");
            Check(!controller.TunnelRunning, "server startup does not start the configured tunnel");
            await controller.StartAsync();
            Check(controller.Address == address, "repeated start joins the existing host");
            var status = JsonDocument.Parse(await controller.ControlAsync("status"));
            Check(status.RootElement.GetProperty("roots").GetArrayLength() == 1, "tray status uses the authenticated local control endpoint");
            await controller.ControlAsync("pause");
            Check(JsonDocument.Parse(await controller.ControlAsync("status")).RootElement.GetProperty("paused").GetBoolean(), "pause is reflected in local status");
            await controller.ControlAsync("resume");
            Check(!JsonDocument.Parse(await controller.ControlAsync("status")).RootElement.GetProperty("paused").GetBoolean(), "resume releases the hold");
            Check(JsonDocument.Parse(await controller.ControlAsync("kill-children")).RootElement.TryGetProperty("killed", out _), "stopping session children reaches the existing controller");
            Check(JsonDocument.Parse(await controller.ControlAsync("revoke-tokens")).RootElement.TryGetProperty("revoked", out _), "token revocation reaches the existing controller");
            await controller.StopAsync();
            Check(!controller.Running && !controller.TunnelRunning, "stop disposes the host and runtime");
            await controller.StartAsync();
            Check(controller.Running && !controller.TunnelRunning, "the same controller restarts with the existing local state and still no tunnel");
            await controller.StopAsync();
            Check(controller.Diagnostics.Length > 0 && controller.Diagnostics.All(x => !x.Contains(config.ControlToken) && !x.Contains(config.OAuth.ClientSecret)),
                "diagnostics do not contain control or client secrets");
            Console.WriteLine($"TRAY_CONTROLLER_PASSED: {passed}; local HTTP lifecycle only, no notification icon or external tunnel.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine($"TRAY_CONTROLLER_FAILED after {passed}: {error}"); return 1; }
        finally { TestCleanup.RemoveDirectory(directory); }
    }
}
