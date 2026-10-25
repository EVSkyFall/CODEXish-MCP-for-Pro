using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Codexish.Server;

// Start with Windows, and starting again. A per-user Task Scheduler task runs Codexish.Start.exe (this program marked as
// a Windows program, so nothing opens a console) at sign-in and checks every minute that it still runs. That keeper
// starts the tray with a hidden console and starts it again, with the usual backoff, whenever it ends without the user
// choosing Exit. CODEXish then never runs as a child of whatever started it (a terminal, a Claude Code session), so it
// is never ended along with it. All names are per configuration and per sign-in session (Local\).
public static class TrayKeeper
{
    public const string StarterName = "Codexish.Start.exe";
    public const string KeepMarker = "--keep";
    public const string KeptMarker = "--tray-kept";
    public const string RestartedOption = "--tray-restarted";
    public const int AnotherTrayExitCode = 3;
    // A hand-off that sees no tray within this long runs the tray where it was started instead; it never stops one.
    public static readonly TimeSpan HandOffWait = TimeSpan.FromSeconds(30);

    public static string MutexName(string configPath) => @"Local\CODEXish-keeper-" + TrayInstance.InstanceHash(configPath);
    // Auto-reset: start the tray now (a hand-off while the keeper waits after Exit or a failure).
    public static string StartEventName(string configPath) => @"Local\CODEXish-keeper-start-" + TrayInstance.InstanceHash(configPath);
    // Manual reset: the keeper ends (--stop).
    public static string QuitEventName(string configPath) => @"Local\CODEXish-keeper-quit-" + TrayInstance.InstanceHash(configPath);
    // Manual reset, set by the tray when the user chose Exit, so the keeper waits instead of starting it again.
    public static string ExitEventName(string configPath) => @"Local\CODEXish-tray-exit-" + TrayInstance.InstanceHash(configPath);
    // Auto-reset, waited on by the tray: stop as Exit does (--stop).
    public static string StopEventName(string configPath) => @"Local\CODEXish-tray-stop-" + TrayInstance.InstanceHash(configPath);

    public interface IChild : IDisposable
    {
        int Id { get; }
        WaitHandle Exited { get; }
        int ExitCode { get; }
    }

    private sealed class ProcessChild(Process process) : IChild
    {
        private readonly ProcessExit exited = new(process);
        public int Id => process.Id;
        public WaitHandle Exited => exited;
        public int ExitCode => process.ExitCode;
        public void Dispose() { exited.Dispose(); process.Dispose(); }

        private sealed class ProcessExit : WaitHandle
        {
            public ProcessExit(Process process) =>
                SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process.Handle, ownsHandle: false);
        }
    }

    // The console build next to the starter, started with a hidden console that the console children of shell_run
    // inherit.
    public static ProcessStartInfo TrayStart(string server, string configPath, string? restarted, params string[] mode)
    {
        var start = new ProcessStartInfo(server) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(server) ?? "" };
        string[] arguments = mode.Length > 0 ? mode : ["--tray", "--start"];
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (string argument in new[] { "--config", Path.GetFullPath(configPath), TrayInstance.DetachedMarker, KeptMarker }) start.ArgumentList.Add(argument);
        if (restarted is not null)
        {
            start.ArgumentList.Add(RestartedOption);
            start.ArgumentList.Add(restarted);
        }
        return start;
    }

    public static IChild StartProcess(ProcessStartInfo start) =>
        new ProcessChild(Process.Start(start) ?? throw new InvalidOperationException(start.FileName + " did not start."));

    // --keep. Only one keeper runs per configuration in a sign-in session; a second one ends at once.
    public static int Run(string configPath, bool stub = false)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        string server = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", typeof(TrayKeeper).Assembly.GetName().Name + ".exe");
        string[] mode = stub ? ["--keeper-stub-child"] : [];
        return new Loop(configPath, restarted => StartProcess(TrayStart(server, configPath, restarted, mode)), line => Log(configPath, line)).Run();
    }

    [SupportedOSPlatform("windows")]
    public sealed class Loop(string configPath, Func<string?, IChild> start, Action<string> log, Func<int, TimeSpan>? delay = null)
    {
        private readonly Func<int, TimeSpan> delay = delay ?? Backoff.Delay;

        public int Run()
        {
            if (StoppedThisSession(configPath)) return 0;
            using var keeper = new Mutex(false, MutexName(configPath));
            try
            {
                if (!keeper.WaitOne(0)) return 0;
            }
            catch (AbandonedMutexException) { /* an earlier keeper was ended; this one takes over */ }
            try
            {
                // Opened and closed inside the mutex, so the next keeper never opens them while an ending one holds them set.
                using var request = new EventWaitHandle(false, EventResetMode.AutoReset, StartEventName(configPath));
                using var quit = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName(configPath));
                using var exitChosen = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName(configPath));
                // Held for the keeper's whole life, so --stop also reaches a tray that has not opened it yet.
                using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName(configPath));
                // A --stop while this keeper was starting may have found none of its events; its mark is already written.
                if (StoppedThisSession(configPath)) return 0;
                Keep(request, quit, exitChosen, stop);
                return 0;
            }
            finally { keeper.ReleaseMutex(); }
        }

        private void Keep(EventWaitHandle request, EventWaitHandle quit, EventWaitHandle exitChosen, EventWaitHandle stop)
        {
            int failures = 0;
            string? restarted = null;
            while (!quit.WaitOne(0))
            {
                exitChosen.Reset();
                stop.Reset();
                IChild child;
                try { child = start(restarted); }
                catch (Exception error)
                {
                    var wait = delay(++failures);
                    log($"Starting the tray failed: {Describe(error)}. Next attempt in {Seconds(wait)} s; failures in a row: {failures}.");
                    if (WaitHandle.WaitAny([quit, request], wait) == 0) return;
                    continue;
                }
                var ran = Stopwatch.StartNew();
                int code, pid = child.Id;
                using (child)
                {
                    if (WaitHandle.WaitAny([child.Exited, quit]) == 1)
                    {
                        // --stop: the tray stops the way Exit stops it, and the keeper ends once it has.
                        stop.Set();
                        child.Exited.WaitOne();
                        return;
                    }
                    code = child.ExitCode;
                }
                var elapsed = TimeSpan.FromSeconds(Math.Round(ran.Elapsed.TotalSeconds));
                if (code == AnotherTrayExitCode && !exitChosen.WaitOne(0))
                {
                    log("Another CODEXish tray runs for this configuration; the keeper starts its own once that one ends.");
                    if (!WaitForOtherTray(quit)) return;
                    restarted = "the tray that was already running has ended";
                }
                if (exitChosen.WaitOne(0))
                {
                    failures = 0;
                    restarted = null;
                    request.Reset();
                    if (LauncherWaits())
                    {
                        log("The tray ended with Exit while CODEXish was being started again; the keeper starts it.");
                        continue;
                    }
                    log("The tray ended with Exit; the keeper starts it again when CODEXish is started or at the next sign-in.");
                    if (WaitHandle.WaitAny([quit, request]) == 0) return;
                    continue;
                }
                if (code == AnotherTrayExitCode) continue;
                failures = Backoff.FailuresAfterRun(failures, ran.Elapsed) + 1;
                var next = delay(failures);
                restarted = $"the previous tray (pid {pid}) exited with code {code} after {elapsed:c}";
                log($"The tray (pid {pid}) exited with code {code} after {elapsed:c} without Exit. It starts again in {Seconds(next)} s; failures in a row: {failures}.");
                if (WaitHandle.WaitAny([quit, request], next) == 0) return;
            }
        }

        // A hand-off that came while the tray was still ending waits for a tray, though its request was just cleared.
        private bool LauncherWaits()
        {
            if (!EventWaitHandle.TryOpenExisting(TrayInstance.ReadyEventName(configPath), out var ready)) return false;
            using (ready) return !ready.WaitOne(0);
        }

        // The handle is closed before the next start, so the tray that starts creates the mutex afresh.
        private bool WaitForOtherTray(WaitHandle quit)
        {
            if (!Mutex.TryOpenExisting(TrayInstance.MutexName(configPath), out var other)) return true;
            using (other)
            {
                int which;
                try { which = WaitHandle.WaitAny([other, quit]); }
                catch (AbandonedMutexException) { which = 0; }
                if (which == 0) other.ReleaseMutex();
                return which == 0;
            }
        }
    }

    // --stop keeps CODEXish stopped for the rest of this sign-in session, so the task's minute check cannot start it
    // while, for example, an update replaces its files: <config>.stopped names the session. Starting CODEXish by hand,
    // turning Start with Windows on or signing in again ends it, and a session that cannot be read never keeps it
    // stopped.
    public static string StoppedMarker(string configPath) => TrayInstance.CanonicalPath(configPath) + ".stopped";

    public static bool StoppedThisSession(string configPath)
    {
        try
        {
            string marker = StoppedMarker(configPath);
            if (!File.Exists(marker) || Native.LogonSessionId() is not { } session) return false;
            if (File.ReadAllText(marker).Trim() == session.ToString("x", CultureInfo.InvariantCulture)) return true;
            File.Delete(marker);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return false;
    }

    public static void ClearStopped(string configPath)
    {
        try { File.Delete(StoppedMarker(configPath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    private static void MarkStopped(string configPath)
    {
        if (Native.LogonSessionId() is not { } session) return;
        try { File.WriteAllText(StoppedMarker(configPath), session.ToString("x", CultureInfo.InvariantCulture)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    // The tray, when the user chose Exit, tells its keeper not to start it again.
    public static void ChoseExit(string configPath)
    {
        if (OperatingSystem.IsWindows()) Signal(ExitEventName(configPath));
    }

    // After ChoseExit, when Start with Windows is off: nothing can ask the keeper for a start any more, so it ends too
    // and lets go of the program's files.
    public static void EndKeeper(string configPath)
    {
        if (OperatingSystem.IsWindows()) Signal(QuitEventName(configPath));
    }

    // Codexish.Start.exe opened by hand, without --keep. That is a start, so an earlier --stop ends. A keeper that waits
    // after Exit starts its tray again; otherwise the task starts a keeper, so it does not run inside whatever opened this
    // file, and only without the task does this process keep CODEXish itself.
    public static int StartByHand(string configPath, bool stub)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        ClearStopped(configPath);
        if (Mutex.TryOpenExisting(TrayInstance.MutexName(configPath), out var running))
        {
            running.Dispose();
            return 0;
        }
        IAutostart? autostart = null;
        try { autostart = Autostart.ForThisProcess(configPath); }
        catch (Exception) { /* without it, this process keeps CODEXish */ }
        if (HandOff(configPath, autostart, TextWriter.Null)) return 0;
        // A keeper the task did not start, or whose task could not be read, is asked as a hand-off asks: the open ready
        // event tells it that a start is waiting even if the request itself arrives while a tray is ending. Only a tray
        // that is ready counts; a keeper that was ending anyway has let go by then, and this process keeps CODEXish.
        using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, TrayInstance.ReadyEventName(configPath)))
            if (Signal(StartEventName(configPath)) && ready.WaitOne(HandOffWait)) return 0;
        return Run(configPath, stub);
    }

    // Started any other way while the task for this configuration exists, the tray asks the keeper to run it instead,
    // so it never stays a child of what started it. False leaves the tray to run where it was started, as before.
    public static bool HandOff(string configPath, IAutostart? autostart, TextWriter output)
    {
        if (!OperatingSystem.IsWindows() || autostart is not TrayTask task) return false;
        try
        {
            // First-run setup runs where it was started: the keeper's tray always starts with --start.
            if (!File.Exists(configPath)) return false;
            if (!task.RegisteredForThis()) return false;
            if (Vitals.RedirectedFile(configPath) is { } redirected)
            {
                output.WriteLine($"CODEXish: the configuration opens at {redirected}, which programs started by Windows cannot see, so the tray runs here.");
                return false;
            }
            // A tray that runs already answers as it always has; a start request now would undo its next Exit.
            if (Mutex.TryOpenExisting(TrayInstance.MutexName(configPath), out var running))
            {
                running.Dispose();
                return false;
            }
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, TrayInstance.ReadyEventName(configPath));
            if (!Signal(StartEventName(configPath))) task.Run();
            if (ready.WaitOne(HandOffWait))
            {
                output.WriteLine($"CODEXish runs from its {task.Location} now.");
                return true;
            }
            output.WriteLine($"CODEXish: the {task.Location} did not start the tray within {HandOffWait.TotalSeconds:0} s, so it runs here.");
        }
        catch (Exception error) { output.WriteLine($"CODEXish: handing the start to the {task.Location} failed ({Describe(error)}), so the tray runs here."); }
        return false;
    }

    // --stop: the keeper ends, the tray stops as Exit stops it, and this returns once both are gone. There is no time
    // limit: it waits for what actually happens.
    public static int Stop(string configPath, TextWriter output)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("CODEXish: --stop needs Windows.");
            return 2;
        }
        // First, so a keeper the task starts from now on starts nothing.
        MarkStopped(configPath);
        // Held, and set again while this waits, until the tray has let go: a tray that is still starting, and opens its
        // stop request only later, gets it then.
        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName(configPath));
        stop.Set();
        bool told = Signal(QuitEventName(configPath));
        int seconds = 0;
        bool tray = WaitReleased(TrayInstance.MutexName(configPath), () =>
        {
            stop.Set();
            if (++seconds == 10)
                output.WriteLine("Still waiting for the CODEXish tray to stop. A tray from before Codexish.Start.exe does not hear --stop: choose Exit in its menu, and this returns.");
        });
        // A keeper that is still starting finds the mark and ends; one that missed the request is asked again.
        bool keeper = WaitReleased(MutexName(configPath), () => Ask(QuitEventName(configPath)));
        output.WriteLine(told || tray || keeper
            ? "CODEXish stopped; it stays stopped until it is started again or at the next sign-in."
            : $"CODEXish is not running for {Path.GetFullPath(configPath)}; it stays stopped until it is started again or at the next sign-in.");
        return 0;
    }

    // Self-test injection point for --keeper-live-test: a stand-in for the tray that holds its names and waits for
    // --stop or for Exit, and says how it was started. Never used by the real keeper.
    public static int StubChild(string configPath)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        using var instance = TrayInstance.TryAcquire(configPath);
        if (instance is null) return AnotherTrayExitCode;
        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName(configPath));
        string record = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "stub-" + Environment.ProcessId + ".txt");
        File.WriteAllText(record, Vitals.Launch);
        TrayInstance.SignalReady(configPath);
        stop.WaitOne();
        ChoseExit(configPath);
        instance.ReleaseMutex();
        return 0;
    }

    // Signal for a callback, which the platform check before it does not reach.
    private static void Ask(string name)
    {
        if (OperatingSystem.IsWindows()) Signal(name);
    }

    [SupportedOSPlatform("windows")]
    private static bool Signal(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var handle)) return false;
        using (handle) handle.Set();
        return true;
    }

    // Whether something held it. There is no time limit; every second without it, the request is sent again.
    private static bool WaitReleased(string name, Action again)
    {
        if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
        using (mutex)
        {
            try
            {
                while (!mutex.WaitOne(TimeSpan.FromSeconds(1))) again();
            }
            catch (AbandonedMutexException) { /* its holder was ended; it is free now */ }
            mutex.ReleaseMutex();
        }
        return true;
    }

    // keeper-<yyyyMMdd>.log in state_dir\logs, which retention removes with the tray's logs. state_dir is read from the
    // file without loading it, so the keeper never repairs or rewrites a configuration. A line that cannot be written,
    // for whatever reason, is dropped: logging never ends the keeper.
    internal static void Log(string configPath, string value)
    {
        try
        {
            string full = Path.GetFullPath(configPath);
            string? stateDir = null;
            if (File.Exists(full))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(full));
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("state_dir", out var state) &&
                    state.ValueKind == JsonValueKind.String) stateDir = state.GetString();
            }
            string logs = Path.Combine(string.IsNullOrWhiteSpace(stateDir) ? Path.GetDirectoryName(full) ?? "." : stateDir, "logs");
            Directory.CreateDirectory(logs);
            string line = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + Redaction.Apply(value).Text;
            File.AppendAllText(Path.Combine(logs, "keeper-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log"),
                line + Environment.NewLine, new System.Text.UTF8Encoding(false));
        }
        catch (Exception) { }
    }

    private static string Seconds(TimeSpan delay) => delay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);

    private static string Describe(Exception error) => error.GetType().Name + ": " + error.Message.Split('\n', 2)[0].TrimEnd('\r');
}
