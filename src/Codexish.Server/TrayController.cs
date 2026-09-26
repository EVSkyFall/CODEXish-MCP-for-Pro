using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

// What --tray was started with: --start, and values that pre-fill the first-run form. The password is never taken
// from the command line; the user types it into the form.
public sealed record TrayOptions(bool Start = false, string? PublicUrl = null, string? Port = null, string? Root = null)
{
    public static TrayOptions Parse(string[] args) => new(args.Contains("--start"),
        CommandLine.Values(args, "--public-url").FirstOrDefault(), CommandLine.Values(args, "--port").FirstOrDefault(),
        CommandLine.Values(args, "--root").FirstOrDefault());

    // --root takes id=path like --init; a bare path becomes the root "project".
    public (string Id, string Path) RootEntry()
    {
        string value = Root ?? "";
        int separator = value.IndexOf('=');
        return separator > 0 && value[..separator].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? (value[..separator], value[(separator + 1)..])
            : ("project", value);
    }
}

// The tray is a view/controller in the server process, not an additional identity or approval service. The server
// and the owned tunnel are supervised separately: a failed start, and a stop nobody asked for, are retried after 1 s,
// doubling to a 60 s ceiling, without ever giving up; five minutes of healthy running start the schedule over. A user
// stop ends supervision of that component until the user starts it again.
public sealed class TrayController(ServerConfig config) : IAsyncDisposable
{
    public static readonly TimeSpan HealthyRun = Backoff.HealthyRun;
    // The connection log window shows this many recent lines; the files under state_dir\logs keep every line.
    public const int MemoryLines = 5000;

    private sealed class Supervision
    {
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Loop = Task.CompletedTask;
        public volatile string State = "starting";

        // The first outcome, whichever it is, releases the caller that started the component.
        public void Enter(string state)
        {
            State = state;
            Settled.TrySetResult();
        }
    }

    private WebApplication? app;
    private CodexishRuntime? runtime;
    private Process? tunnel;
    private nint tunnelJob;
    private Task? stdout, stderr;
    private Supervision? serverWatch, tunnelWatch;
    private TaskCompletionSource serverChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Queue<string> diagnostics = new();
    public bool Running => app is not null;
    public Uri? Address { get; private set; }
    public string[] Diagnostics { get { lock (diagnostics) return diagnostics.ToArray(); } }
    internal IHostApplicationLifetime? HostLifetime => app?.Lifetime;
    public string LogDirectory => Path.Combine(config.StateDir, "logs");
    public string LogFile => Path.Combine(LogDirectory, "tray-" + DateTime.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ".log");

    // Whether the owned tunnel runs inside the kill-on-close job that ends it with this process.
    internal bool TunnelInJob
    {
        get
        {
            lock (sync)
            {
                try { return tunnel is { HasExited: false } process && Native.InJob(process.Handle, tunnelJob); }
                catch (InvalidOperationException) { return false; }
            }
        }
    }

    public bool TunnelRunning
    {
        get
        {
            var process = tunnel;
            // The supervisor may release an exited process while this is read.
            try { return process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public static TimeSpan RetryDelay(int failures) => Backoff.Delay(failures);

    public static int FailuresAfterRun(int failures, TimeSpan healthy) => Backoff.FailuresAfterRun(failures, healthy);

    // For the icon tooltip: running, retrying with a short cause, waiting, or stopped.
    public string Status
    {
        get
        {
            Supervision? server, owned;
            lock (sync) { server = serverWatch; owned = tunnelWatch; }
            string text = "server " + (server?.State ?? "stopped");
            if (owned is not null || !string.IsNullOrWhiteSpace(config.Tunnel.Command)) text += ", tunnel " + (owned?.State ?? "stopped");
            return text;
        }
    }

    public void Note(string value) => Log(value);

    private void Log(string value)
    {
        string line = DateTimeOffset.UtcNow.ToString("O") + " " + Scrub(value);
        lock (diagnostics)
        {
            diagnostics.Enqueue(line);
            while (diagnostics.Count > MemoryLines) diagnostics.Dequeue();
            // Every line also goes to disk, redacted the same way; a file that cannot be written never stops logging.
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogFile, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
    }

    // The configuration's own secrets are not environment variables, so the shared redaction cannot know them; they
    // are replaced first, before the published patterns run, so a pattern match cannot split them.
    internal string Scrub(string value)
    {
        foreach (var (secret, kind) in new[] { (config.ControlToken, "control_token"), (config.OAuth.ClientSecret, "client_secret"), (config.OAuth.PasswordHash, "password_hash") })
            if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, $"[REDACTED:{kind}]", StringComparison.Ordinal);
        return Redaction.Apply(value).Text;
    }

    private static string Describe(Exception error) =>
        error.GetType().Name + ": " + error.Message.Split('\n', 2)[0].TrimEnd('\r');

    // Start, stop and their tunnel counterparts run one at a time, so an automatic start can never overtake a stop that
    // is still in progress, while a start requested after the stop completed simply proceeds.
    private readonly SemaphoreSlim lifecycle = new(1, 1);

    private async Task Transition(Func<Task> action)
    {
        await lifecycle.WaitAsync();
        try { await action(); }
        finally { lifecycle.Release(); }
    }

    // Self-test injection point for a failing tunnel stop. Never set outside --tray-tests.
    internal bool FailNextTunnelStop { get; set; }

    // Returns once the first attempt has an outcome. A failed attempt is not thrown: supervision keeps retrying and
    // Status and Diagnostics carry the cause.
    public Task StartAsync() => Transition(StartCore);

    private async Task StartCore()
    {
        if (ServerConfig.TransportRefusal(config, false) is { } refusal) throw new ArgumentException(refusal);
        Supervision watch;
        lock (sync)
        {
            if (serverWatch is null)
            {
                var created = new Supervision();
                created.Loop = Task.Run(() => SuperviseServer(created));
                serverWatch = created;
            }
            watch = serverWatch;
        }
        await watch.Settled.Task;
    }

    // The tunnel starts only on request: its menu item, --start, or the end of first-run setup. Starting the server
    // alone never starts it. It inherits the tray's environment on purpose, because tunnel tools commonly read their
    // settings from environment variables.
    public Task StartTunnelAsync() => Transition(StartTunnelCore);

    private async Task StartTunnelCore()
    {
        if (string.IsNullOrWhiteSpace(config.Tunnel.Command)) throw new InvalidOperationException("Configure tunnel.command and tunnel.args; no tunnel is downloaded or selected automatically.");
        Supervision watch;
        lock (sync)
        {
            if (serverWatch is null) throw new InvalidOperationException("Start the authenticated server before its tunnel.");
            if (tunnelWatch is null)
            {
                var created = new Supervision();
                created.Loop = Task.Run(() => SuperviseTunnel(created));
                tunnelWatch = created;
            }
            watch = tunnelWatch;
        }
        await watch.Settled.Task;
    }

    // --tray --start, the end of first-run setup and a configuration that loads after an error: the server, then the
    // configured tunnel, both supervised, as one transition.
    public Task StartConfiguredAsync() => Transition(async () =>
    {
        await StartCore();
        if (!string.IsNullOrWhiteSpace(config.Tunnel.Command)) await StartTunnelCore();
    });

    public async Task<string> ControlAsync(string action)
    {
        await gate.WaitAsync();
        try
        {
            if (app is null || Address is null) throw new InvalidOperationException("Start the server first.");
            if (action is not ("status" or "pause" or "resume" or "kill-children" or "revoke-tokens" or "remove-clients")) throw new ArgumentException("Unknown local action.");
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

    public Task StopTunnelAsync() => Transition(async () =>
    {
        Supervision? owned;
        lock (sync) { owned = tunnelWatch; tunnelWatch = null; }
        await End(owned);
        await gate.WaitAsync();
        try { await StopTunnelCore(); }
        finally { gate.Release(); }
    });

    // A tunnel that cannot be stopped never keeps the server running: the server is still stopped, and the tunnel
    // failure is logged and reported afterwards on its own.
    public Task StopAsync() => Transition(async () =>
    {
        Supervision? server, owned;
        lock (sync) { server = serverWatch; owned = tunnelWatch; serverWatch = tunnelWatch = null; }
        await End(owned);
        await End(server);
        Exception? tunnelFailure = null;
        await gate.WaitAsync();
        try
        {
            try { await StopTunnelCore(); }
            catch (Exception error)
            {
                tunnelFailure = error;
                Log("Stopping the owned tunnel failed: " + Describe(error));
            }
            if (app is not null)
            {
                await StopHost();
                Log("Server stopped. Persistent child lifetime remains unchanged.");
            }
        }
        finally { gate.Release(); }
        if (tunnelFailure is not null)
            throw new InvalidOperationException("The server stopped, but stopping the owned tunnel failed: " + Describe(tunnelFailure), tunnelFailure);
    });

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally
        {
            lifecycle.Dispose();
            gate.Dispose();
        }
    }

    private async Task End(Supervision? watch)
    {
        if (watch is null) return;
        watch.Stop.Cancel();
        try { await watch.Loop; }
        catch (Exception error) { Log("Supervision ended with " + Describe(error)); }
        watch.Stop.Dispose();
    }

    private async Task<bool> Retry(Supervision watch, string cause, string summary, int failures, CancellationToken token)
    {
        var delay = RetryDelay(failures);
        // Logged before Enter releases the caller, so a caller that reads Diagnostics next already finds the cause.
        Log($"{cause.TrimEnd('.')}. Next attempt in {delay.TotalSeconds:0} s; failures in a row: {failures}.");
        watch.Enter("retrying: " + Scrub(summary));
        try
        {
            await Task.Delay(delay, token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    private async Task SuperviseServer(Supervision watch)
    {
        var token = watch.Stop.Token;
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            Task stopping;
            try { stopping = await StartHost(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                if (!await Retry(watch, "Server start failed: " + Describe(error), Describe(error), ++failures, token)) break;
                continue;
            }
            watch.Enter("running");
            var healthy = Stopwatch.StartNew();
            try { await stopping.WaitAsync(token); }
            catch (OperationCanceledException) { break; }
            failures = FailuresAfterRun(failures, healthy.Elapsed) + 1;
            string cause = $"Server stopped without a user stop after {healthy.Elapsed:c}";
            await gate.WaitAsync(CancellationToken.None);
            try { await StopHost(); }
            catch (Exception error) { cause += "; releasing it failed with " + Describe(error); }
            finally { gate.Release(); }
            if (!await Retry(watch, cause, "stopped without a user stop", failures, token)) break;
        }
    }

    // One start attempt. The returned task completes when the host begins stopping, which a user stop never lets
    // this loop observe because the loop is ended first.
    private async Task<Task> StartHost(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var candidate = new CodexishRuntime(config);
            WebApplication? host = null;
            try
            {
                // Browser mounts connect in the background once the host listens; none of them can hold this start.
                host = CodexishHost.Build(candidate, config.Port, Log);
                await host.StartAsync();
                var address = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.Lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
                runtime = candidate; app = host;
                ServerChanged(address);
                Log("Server started at " + address + "; browser mounts: " + candidate.Browsers.Summary());
                foreach (string warning in candidate.Warnings) Log("Warning: " + warning);
                return stopping.Task;
            }
            catch
            {
                // The candidate runtime holds the state-directory lock; it is released whatever the host does.
                try { if (host is not null) await host.DisposeAsync(); }
                finally { candidate.Dispose(); }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    // Each release runs whatever the step before it did, so the runtime and its state-directory lock are always freed.
    private async Task StopHost()
    {
        var host = app;
        var owned = runtime;
        if (host is null) return;
        try
        {
            // Browser calls in flight are cancelled before the host drains its requests, so a backend that never
            // answers cannot hold the stop.
            owned?.Browsers.Stop();
            await host.StopAsync();
        }
        finally
        {
            try { await host.DisposeAsync(); }
            finally
            {
                app = null;
                runtime = null;
                try { owned?.Dispose(); }
                finally { ServerChanged(null); }
            }
        }
    }

    private void ServerChanged(Uri? address)
    {
        TaskCompletionSource previous;
        lock (sync)
        {
            Address = address;
            previous = serverChanged;
            serverChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        previous.TrySetResult();
    }

    private async Task SuperviseTunnel(Supervision watch)
    {
        var token = watch.Stop.Token;
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            Process? process;
            try
            {
                await WaitForServer(watch, token);
                process = await StartTunnel(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                if (!await Retry(watch, "Owned tunnel failed to start: " + Describe(error), Describe(error), ++failures, token)) break;
                continue;
            }
            if (process is null) continue;
            watch.Enter("running");
            var healthy = Stopwatch.StartNew();
            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException) { break; }
            // Descendants the tunnel left behind end with its job, so they cannot hold its port or pipes.
            CloseTunnelJob();
            failures = FailuresAfterRun(failures, healthy.Elapsed) + 1;
            string code;
            try { code = process.ExitCode.ToString(); }
            catch (InvalidOperationException) { code = "unknown"; }
            if (!await Retry(watch, $"Owned tunnel exited with code {code} without a user stop after {healthy.Elapsed:c}",
                    $"exited with code {code}", failures, token)) break;
        }
    }

    // A tunnel only ever publishes this server's own listener. While the server is down another program could hold
    // the port, and a tunnel started then would publish that program instead.
    private async Task WaitForServer(Supervision watch, CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (sync)
            {
                if (Address is not null) return;
                changed = serverChanged.Task;
            }
            watch.Enter("waiting for the server");
            await changed.WaitAsync(token);
        }
    }

    // Null when the server stopped between the wait and this attempt; the loop then waits for it again.
    private async Task<Process?> StartTunnel(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (Address is not { } address) return null;
            // An exited predecessor is released here; its output readers end by themselves when its pipes close.
            tunnel?.Dispose();
            tunnel = null;
            var start = new ProcessStartInfo(config.Tunnel.Command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string argument in config.Tunnel.Args) start.ArgumentList.Add(argument.Replace("{port}", address.Port.ToString(), StringComparison.Ordinal));
            var process = Process.Start(start) ?? throw new IOException("Tunnel process did not start.");
            nint job = 0;
            try
            {
                // A kill-on-close job ends the tunnel and its descendants with this process, even when the tray is killed.
                CloseTunnelJob();
                job = Native.CreateKillOnCloseJob();
                if (job != 0 && !Native.AssignProcess(job, process.Handle)) { Native.CloseJob(job); job = 0; }
                if (job == 0 && OperatingSystem.IsWindows()) Log("The owned tunnel could not be placed in a job object; it may outlive a tray that is killed.");
                async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() is { } line) Log("Tunnel: " + line); }
                var output = Drain(process.StandardOutput);
                var errors = Drain(process.StandardError);
                lock (sync) { tunnel = process; tunnelJob = job; }
                stdout = output;
                stderr = errors;
                Log("Owned tunnel process started; remote connectivity is not yet verified.");
                return process;
            }
            catch
            {
                // A tunnel that started but could not be set up ends with its tree before the next attempt. Nothing may
                // keep referring to the process or the job handle that are released here.
                lock (sync)
                    if (ReferenceEquals(tunnel, process)) { tunnel = null; tunnelJob = 0; }
                if (!Native.TerminateJob(job))
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { /* it already exited */ }
                Native.CloseJob(job);
                process.Dispose();
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private void CloseTunnelJob()
    {
        nint job;
        lock (sync) { job = tunnelJob; tunnelJob = 0; }
        Native.CloseJob(job);
    }

    private async Task StopTunnelCore()
    {
        if (tunnel is null) return;
        bool live = !tunnel.HasExited;
        nint job;
        lock (sync) job = tunnelJob;
        if (live && !Native.TerminateJob(job)) tunnel.Kill(entireProcessTree: true);
        CloseTunnelJob();
        if (FailNextTunnelStop)
        {
            FailNextTunnelStop = false;
            throw new IOException("Injected failure while stopping the owned tunnel (tray tests only).");
        }
        await tunnel.WaitForExitAsync();
        // The output of a tree this stop ended is complete once its pipes close. A tunnel that had already exited by
        // itself may have left a descendant holding them, so its readers are left to finish on their own.
        if (live) await Task.WhenAll(stdout ?? Task.CompletedTask, stderr ?? Task.CompletedTask);
        tunnel.Dispose();
        tunnel = null;
        Log("Owned tunnel stopped.");
    }
}

// Controller lifecycle over the real local HTTP control endpoint, supervision and the autostart shortcut. It creates no
// notification icon, starts no external tunnel and never touches the real Startup folder.
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
        static async Task<bool> Eventually(Func<bool> condition, int seconds)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) return false;
                await Task.Delay(50);
            }
            return true;
        }
        try
        {
            Check(Enumerable.Range(0, 10).Select(n => TrayController.RetryDelay(n).TotalSeconds).SequenceEqual([1, 1, 2, 4, 8, 16, 32, 60, 60, 60]) &&
                  TrayController.RetryDelay(int.MaxValue) == TimeSpan.FromSeconds(60),
                "retries wait 1 s, doubling to a 60 s ceiling that holds however many failures follow");
            Check(TrayController.FailuresAfterRun(6, TimeSpan.FromMinutes(5)) == 0 && TrayController.FailuresAfterRun(6, TimeSpan.FromSeconds(299)) == 6,
                "five minutes of healthy running start the schedule over; a shorter run keeps it");
            var options = TrayOptions.Parse(["--tray", "--start", "--public-url", "https://pc.example", "--port", "3100", "--root", @"proj=C:\Projects\X"]);
            Check(options.Start && options.PublicUrl == "https://pc.example" && options.Port == "3100" && options.RootEntry() == ("proj", @"C:\Projects\X") &&
                  !TrayOptions.Parse(["--tray"]).Start && TrayOptions.Parse(["--tray", "--root", @"D:\code"]).RootEntry() == ("project", @"D:\code"),
                "--tray reads --start and the values that pre-fill the first-run form; a bare --root path becomes the root 'project'");

            var config = ServerConfig.Create("https://tray-test.invalid", "test-only-password", [("test", Path.Combine(directory, "root"))], [], 0, Path.Combine(directory, "state"));
            Check(config.OAuth.RefreshTokenDays == 0, "a new configuration keeps refresh tokens without expiry");
            // A configured tunnel whose executable does not exist: if startup tried to launch it, StartAsync would fail.
            config.Tunnel = new TunnelConfig { Command = Path.Combine(directory, "tunnel-never-started" + (OperatingSystem.IsWindows() ? ".exe" : "")), Args = ["--url", "http://127.0.0.1:{port}"] };
            await using var controller = new TrayController(config);
            Check(!controller.Running && controller.Status == "server stopped, tunnel stopped", "controller starts stopped");
            await controller.StartAsync();
            var address = controller.Address;
            Check(controller.Running && address?.IsLoopback == true, "server starts inside the controller process on loopback");
            Check(!controller.TunnelRunning && controller.Status == "server running, tunnel stopped", "server startup does not start the configured tunnel");
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
            Check(JsonDocument.Parse(await controller.ControlAsync("remove-clients")).RootElement.TryGetProperty("removed", out _),
                "removing registered clients reaches the existing controller");
            await controller.StopAsync();
            Check(!controller.Running && !controller.TunnelRunning, "stop disposes the host and runtime");
            await controller.StartAsync();
            Check(controller.Running && !controller.TunnelRunning, "the same controller restarts with the existing local state and still no tunnel");

            int Count(TrayController target, string text) => target.Diagnostics.Count(x => x.Contains(text, StringComparison.Ordinal));
            int starts = Count(controller, "Server started at");
            controller.HostLifetime!.StopApplication();
            Check(await Eventually(() => Count(controller, "Server stopped without a user stop") == 1 && Count(controller, "Server started at") == starts + 1 && controller.Running, 30),
                "a host that stops without a user stop is restarted, and the cause is in the diagnostics");

            // A tunnel that prints the configuration's secrets and exits by itself: its output is redacted, and supervision
            // restarts it until the user stops it.
            config.Tunnel = OperatingSystem.IsWindows()
                ? new TunnelConfig { Command = Path.Combine(Environment.SystemDirectory, "cmd.exe"), Args = ["/c", "echo", "tunnel", config.ControlToken, config.OAuth.ClientSecret, config.OAuth.PasswordHash, "{port}"] }
                : new TunnelConfig { Command = "/bin/sh", Args = ["-c", "echo tunnel \"$1\" \"$2\" \"$3\" {port}", "sh", config.ControlToken, config.OAuth.ClientSecret, config.OAuth.PasswordHash] };
            await controller.StartTunnelAsync();
            Check(await Eventually(() => Count(controller, "Owned tunnel process started") >= 2, 30) &&
                  Count(controller, "Owned tunnel exited with code 0 without a user stop") >= 1,
                "an owned tunnel that exits without a user stop is restarted, and its exit code is in the diagnostics");
            await controller.StopTunnelAsync();
            int tunnelStarts = Count(controller, "Owned tunnel process started");
            await Task.Delay(3000);
            Check(Count(controller, "Owned tunnel process started") == tunnelStarts && !controller.TunnelRunning && controller.Status == "server running, tunnel stopped",
                "stopping the tunnel ends its supervision: nothing restarts it");
            Check(await Eventually(() => controller.Diagnostics.Any(x => x.Contains("Tunnel: tunnel", StringComparison.Ordinal)), 30), "the tunnel's own output reaches the diagnostics");
            string echoed = controller.Diagnostics.First(x => x.Contains("Tunnel: tunnel", StringComparison.Ordinal));
            Check(echoed.Contains("[REDACTED:control_token]", StringComparison.Ordinal) && echoed.Contains("[REDACTED:client_secret]", StringComparison.Ordinal) &&
                echoed.Contains("[REDACTED:password_hash]", StringComparison.Ordinal) && controller.Diagnostics.All(x => !x.Contains(config.ControlToken, StringComparison.Ordinal) &&
                !x.Contains(config.OAuth.ClientSecret, StringComparison.Ordinal) && !x.Contains(config.OAuth.PasswordHash, StringComparison.Ordinal)),
                "tunnel output that repeats the control token, client secret or password hash is redacted in the diagnostics");
            await controller.StopAsync();
            Check(controller.Diagnostics.Length > 0 && controller.Diagnostics.All(x => !x.Contains(config.ControlToken) && !x.Contains(config.OAuth.ClientSecret)),
                "diagnostics do not contain control or client secrets");

            // A busy port: the first attempt fails, supervision keeps trying, and the server comes up once the port is free.
            // The loopback port is chosen by the system; the one reserved for the user's live server is never used.
            var blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            while (((IPEndPoint)blocker.LocalEndpoint).Port == 38451) { blocker.Stop(); blocker = new TcpListener(IPAddress.Loopback, 0); blocker.Start(); }
            int port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            Directory.CreateDirectory(Path.Combine(directory, "busy-root"));
            var busyConfig = ServerConfig.Create("https://tray-busy.invalid", "test-only-password", [("busy", Path.Combine(directory, "busy-root"))], [], port, Path.Combine(directory, "busy-state"));
            await using var busy = new TrayController(busyConfig);
            try
            {
                await busy.StartAsync();
                Check(!busy.Running && busy.Status.StartsWith("server retrying: IOException", StringComparison.Ordinal) && Count(busy, "Server start failed: IOException") >= 1,
                    "a server start on a busy port returns as retrying with its concrete cause instead of failing");
            }
            finally { blocker.Stop(); }
            Check(await Eventually(() => busy.Running, 30) && busy.Address?.Port == port && busy.Status == "server running",
                "the supervised start succeeds on a later attempt once the port is free");
            await busy.StopAsync();
            Check(!busy.Running && busy.Status == "server stopped", "a user stop ends server supervision");

            // --tray --start and the end of first-run setup: the server, then the configured tunnel.
            await busy.StartConfiguredAsync();
            Check(busy.Running && !busy.TunnelRunning && Count(busy, "Owned tunnel process started") == 0,
                "starting the configuration without tunnel.command starts only the server");
            await busy.StopAsync();
            busyConfig.Tunnel = OperatingSystem.IsWindows()
                ? new TunnelConfig { Command = Path.Combine(Environment.SystemDirectory, "PING.EXE"), Args = ["-n", "60", "127.0.0.1"] }
                : new TunnelConfig { Command = "/bin/sh", Args = ["-c", "sleep 60"] };
            await busy.StartConfiguredAsync();
            Check(busy.Running && busy.TunnelRunning && busy.Status == "server running, tunnel running",
                "starting the configuration starts the server and then the configured tunnel");
            if (OperatingSystem.IsWindows())
                Check(busy.TunnelInJob, "the owned tunnel runs inside a kill-on-close job, so it cannot outlive a killed tray");
            else Console.WriteLine("TRAY SKIP tunnel job object: Windows only");
            await busy.StopAsync();
            await Task.Delay(1500);
            Check(!busy.Running && !busy.TunnelRunning && busy.Status == "server stopped, tunnel stopped",
                "stopping the server stops the owned tunnel and neither is restarted");

            // A tunnel stop that throws never keeps the server running; its failure is reported on its own.
            await busy.StartConfiguredAsync();
            busy.FailNextTunnelStop = true;
            string? stopFailure = null;
            try { await busy.StopAsync(); }
            catch (InvalidOperationException error) { stopFailure = error.Message; }
            Check(stopFailure is not null && stopFailure.StartsWith("The server stopped, but stopping the owned tunnel failed: IOException", StringComparison.Ordinal) &&
                  !busy.Running && busy.Address is null && Count(busy, "Stopping the owned tunnel failed: IOException") == 1,
                "a tunnel stop that throws still stops the server, and the tunnel failure is reported separately");
            Check(await Eventually(() => !busy.TunnelRunning, 30), "the owned tunnel was ended before its stop failed");
            await busy.StopTunnelAsync();
            Check(!busy.Running && !busy.TunnelRunning && busy.Status == "server stopped, tunnel stopped" && Count(busy, "Owned tunnel stopped.") >= 2,
                "the failed tunnel stop can be completed afterwards");

            // A directory where the database file belongs makes every attempt fail right after the state directory
            // lock is taken. Once it is gone, a later attempt must be able to take the lock again.
            Directory.CreateDirectory(Path.Combine(directory, "blocked-root"));
            string blockedState = Path.Combine(directory, "blocked-state");
            Directory.CreateDirectory(Path.Combine(blockedState, "codexish.db"));
            var blockedConfig = ServerConfig.Create("https://tray-blocked.invalid", "test-only-password", [("blocked", Path.Combine(directory, "blocked-root"))], [], 0, blockedState);
            await using var blocked = new TrayController(blockedConfig);
            await blocked.StartAsync();
            Check(!blocked.Running && blocked.Status.StartsWith("server retrying: SqliteException", StringComparison.Ordinal),
                "a start that fails after the state directory lock was taken is retried with its cause");
            Directory.Delete(Path.Combine(blockedState, "codexish.db"));
            Check(await Eventually(() => blocked.Running, 30),
                "the failed attempt released the state directory lock, so a later attempt starts the server");
            await blocked.StopAsync();

            // Diagnostics: the most recent lines in memory, every line on disk, redacted the same way.
            await using var logged = new TrayController(blockedConfig);
            for (int line = 1; line <= TrayController.MemoryLines + 100; line++) logged.Note($"line {line} {blockedConfig.ControlToken}");
            string[] kept = logged.Diagnostics;
            string[] onDisk = File.ReadAllLines(logged.LogFile);
            Check(kept.Length == TrayController.MemoryLines && kept[0].EndsWith("line 101 [REDACTED:control_token]", StringComparison.Ordinal) &&
                  onDisk.Count(l => l.Contains(" line ", StringComparison.Ordinal)) == TrayController.MemoryLines + 100 &&
                  onDisk.All(l => !l.Contains(blockedConfig.ControlToken, StringComparison.Ordinal)) &&
                  logged.LogFile.StartsWith(Path.Combine(blockedConfig.StateDir, "logs"), StringComparison.Ordinal),
                $"the connection log keeps the latest {TrayController.MemoryLines} lines in memory and every line, redacted, in state_dir\\logs");

            // Relaunch without a console, one tray per configuration, and loading that never throws.
            var detached = TrayInstance.DetachedStart(@"C:\Apps\Codexish.Server.exe", null, ["--tray", "--start"]);
            var hosted = TrayInstance.DetachedStart("dotnet", @"C:\Apps\Codexish.Server.dll", ["--tray"]);
            Check(!detached.UseShellExecute && detached.CreateNoWindow && !detached.RedirectStandardInput && !detached.RedirectStandardOutput &&
                  !detached.RedirectStandardError && detached.ArgumentList.SequenceEqual(["--tray", "--start", TrayInstance.DetachedMarker]) &&
                  hosted.ArgumentList.SequenceEqual([@"C:\Apps\Codexish.Server.dll", "--tray", TrayInstance.DetachedMarker]),
                "--tray relaunches the same executable with the same arguments plus the detached marker, a hidden console and no redirection");
            string configPath = Path.Combine(directory, "tray-config", "codexish.json");
            Check(TrayInstance.MutexName(configPath) == TrayInstance.MutexName(OperatingSystem.IsWindows() ? configPath.ToUpperInvariant() : configPath) &&
                  TrayInstance.MutexName(configPath) != TrayInstance.MutexName(configPath + ".other"),
                "the tray mutex is named after the full configuration path, compared the way the platform compares paths");
            if (OperatingSystem.IsWindows())
            {
                var first = TrayInstance.TryAcquire(configPath);
                var second = TrayInstance.TryAcquire(configPath);
                Check(first is not null && second is null, "a second tray for the same configuration finds the first one's mutex");
                first!.ReleaseMutex();
                first.Dispose();
                var third = TrayInstance.TryAcquire(configPath);
                Check(third is not null, "the configuration is free again once that tray is gone");
                third!.ReleaseMutex();
                third.Dispose();
            }
            else Console.WriteLine("TRAY SKIP single-instance mutex: the tray exists only on Windows");
            string ready = TrayInstance.ReadyEventName(configPath), mutex = TrayInstance.MutexName(configPath);
            Check(ready != mutex && ready[ready.LastIndexOf('-')..] == mutex[mutex.LastIndexOf('-')..] &&
                  ready == TrayInstance.ReadyEventName(OperatingSystem.IsWindows() ? configPath.ToUpperInvariant() : configPath),
                "the relaunch's ready event is named from the same hash as the tray mutex");
            if (OperatingSystem.IsWindows())
            {
                // The names come from the final path: a junction to the configuration's folder yields the same names,
                // both for an existing file and for one that does not exist yet.
                string real = Path.Combine(directory, "real-config"), alias = Path.Combine(directory, "alias-config");
                Directory.CreateDirectory(real);
                File.WriteAllText(Path.Combine(real, "codexish.json"), "{}");
                var link = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (string argument in new[] { "/c", "mklink", "/J", alias, real }) link.ArgumentList.Add(argument);
                using (var mklink = Process.Start(link)!)
                {
                    mklink.StandardOutput.ReadToEnd();
                    mklink.StandardError.ReadToEnd();
                    mklink.WaitForExit();
                }
                if (Directory.Exists(alias))
                {
                    Check(TrayInstance.MutexName(Path.Combine(alias, "codexish.json")) == TrayInstance.MutexName(Path.Combine(real, "codexish.json")) &&
                          TrayInstance.ReadyEventName(Path.Combine(alias, "codexish.json")) == TrayInstance.ReadyEventName(Path.Combine(real, "codexish.json")) &&
                          TrayInstance.MutexName(Path.Combine(alias, "later.json")) == TrayInstance.MutexName(Path.Combine(real, "later.json")),
                        "a configuration reached through a junction gets the tray names of its final path, whether or not the file exists yet");
                    Directory.Delete(alias);
                }
                else Console.WriteLine("TRAY SKIP junction alias: mklink /J could not create a junction here");

                // The launcher waits for the relaunched tray's signal or its exit, whichever comes first, with no timeout.
                using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, ready);
                Process Stub(string program, params string[] arguments)
                {
                    var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, program))
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                    };
                    foreach (string argument in arguments) start.ArgumentList.Add(argument);
                    return Process.Start(start)!;
                }
                using (var quitter = Stub("cmd.exe", "/c", "exit", "3"))
                    Check(!TrayInstance.ChildTookOver(quitter, signal) && quitter.HasExited && quitter.ExitCode == 3 && !signal.WaitOne(0),
                        "a relaunched tray that exits before it signals leaves the tray to the launching process");
                TrayInstance.SignalReady(configPath + ".nobody-waits");
                TrayInstance.SignalReady(configPath);
                using (var runner = Stub("PING.EXE", "-n", "60", "127.0.0.1"))
                {
                    bool tookOver = TrayInstance.ChildTookOver(runner, signal);
                    bool stillRunning = !runner.HasExited;
                    runner.Kill();
                    runner.WaitForExit();
                    Check(tookOver && stillRunning && signal.WaitOne(0),
                        "a relaunched tray that signals takes over while it keeps running, so the launcher never waits for its exit");
                }
            }
            else Console.WriteLine("TRAY SKIP detached relaunch handshake and final-path names: the tray exists only on Windows");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, "{ unreadable");
            var (unreadable, why) = TrayInstance.TryLoad(configPath);
            var plain = ServerConfig.Create("https://tray-plain.invalid", "test-only-password", [("busy", Path.Combine(directory, "busy-root"))], [], 0, Path.Combine(directory, "plain-state"));
            plain.PublicUrl = "http://tray-plain.invalid";
            plain.Save(configPath);
            var (refused, refusal) = TrayInstance.TryLoad(configPath);
            plain.PublicUrl = "https://tray-plain.invalid";
            plain.Save(configPath);
            var (usable, none) = TrayInstance.TryLoad(configPath);
            Check(unreadable is null && why!.Contains("could not be read", StringComparison.Ordinal) && refused is null && refusal!.Contains("requires https", StringComparison.Ordinal) &&
                  usable is not null && none is null,
                "loading for the tray never throws: an unreadable file or a refused public_url comes back as the reason to show and retry");

            if (OperatingSystem.IsWindows())
            {
                string startup = Path.Combine(directory, "Startup");
                string executable = Environment.ProcessPath!;
                string custom = Path.Combine(directory, "custom config.json");
                var autostart = new TrayAutostart(startup, executable, custom);
                Check(new TrayAutostart(startup, executable, ServerConfig.DefaultPath).Arguments == "--tray --start" &&
                      autostart.Arguments == $"--tray --start --config \"{custom}\"",
                    "the shortcut passes --config only for a configuration outside the default path");
                Check(!autostart.Enabled, "autostart is off while no shortcut exists");
                autostart.Enable();
                var written = autostart.Read();
                Check(autostart.Enabled && autostart.ShortcutPath == Path.Combine(startup, "CODEXish.lnk") &&
                      written.Target.Equals(executable, StringComparison.OrdinalIgnoreCase) && written.Arguments == autostart.Arguments &&
                      written.WorkingDirectory.Equals(Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase),
                    "Start with Windows writes CODEXish.lnk with this executable, --tray --start and the executable's folder");
                Check(!autostart.Heal() && autostart.Read().Target.Equals(executable, StringComparison.OrdinalIgnoreCase),
                    "a shortcut whose target exists is left as it is");
                string moved = Path.Combine(directory, "old-install", "Codexish.Server.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
                File.WriteAllText(moved, "placeholder");
                new TrayAutostart(startup, moved, custom).Enable();
                File.Delete(moved);
                Check(autostart.Heal() && autostart.Read().Target.Equals(executable, StringComparison.OrdinalIgnoreCase),
                    "a shortcut whose target file no longer exists is rewritten to the current executable");
                File.WriteAllText(autostart.ShortcutPath, "not a shell link");
                Check(autostart.Heal() && autostart.Read().Target.Equals(executable, StringComparison.OrdinalIgnoreCase),
                    "an unreadable shortcut is rewritten as well");
                autostart.Disable();
                Check(!autostart.Enabled && !autostart.Heal() && !autostart.Enabled,
                    "turning autostart off removes the shortcut, and self-heal never re-creates a removed one");
            }
            else Console.WriteLine("TRAY SKIP autostart shortcut: Windows only");

            Console.WriteLine($"TRAY_CONTROLLER_PASSED: {passed}; local HTTP lifecycle, supervision and a temporary Startup folder only, no notification icon or external tunnel.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine($"TRAY_CONTROLLER_FAILED after {passed}: {error}"); return 1; }
        finally { TestCleanup.RemoveDirectory(directory); }
    }
}
