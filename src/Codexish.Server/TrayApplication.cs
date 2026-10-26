#if WINDOWS
using Forms = System.Windows.Forms;
#endif
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codexish.Server;

// The parts of the tray's lifecycle that need no icon, kept apart so --tray-tests can check them on every platform.
public static class TrayInstance
{
    public const string DetachedMarker = "--tray-detached";

    // This executable, and the assembly to hand to the dotnet host when it was started as "dotnet Codexish.Server.dll".
    public static (string Executable, string? HostedAssembly) SelfLaunch()
    {
        string process = Environment.ProcessPath ?? throw new InvalidOperationException("The path of this executable is unknown.");
        string? assembly = Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? typeof(TrayInstance).Assembly.Location : null;
        return (process, assembly);
    }

    // --tray starts itself again with a hidden console of its own (not a detached process, so the console children of
    // shell_run stay hidden too) and without redirected streams; the visible console that launched it can then close.
    public static ProcessStartInfo DetachedStart(string executable, string? hostedAssembly, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (hostedAssembly is not null) start.ArgumentList.Add(hostedAssembly);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(DetachedMarker);
        return start;
    }

    // One tray per configuration. The names come from the configuration's final path, so a symlink, a junction or an
    // 8.3 alias of the same file gives the same names; they are compared the way the platform compares paths.
    public static string MutexName(string configPath) => @"Local\CODEXish-tray-" + InstanceHash(configPath);

    // Set by the relaunched tray once it holds the mutex, or once it found another tray holding it.
    public static string ReadyEventName(string configPath) => @"Local\CODEXish-tray-ready-" + InstanceHash(configPath);

    internal static string InstanceHash(string configPath)
    {
        string canonical = CanonicalPath(configPath);
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
    }

    // The final path (GetFinalPathNameByHandle) of the configuration file, or, before that file exists, of its deepest
    // existing parent with the rest of the path appended; GetFullPath when neither can be resolved.
    public static string CanonicalPath(string configPath)
    {
        string full = Path.GetFullPath(configPath);
        if (!OperatingSystem.IsWindows()) return full;
        string? existing = full;
        string rest = "";
        while (existing is not null && !File.Exists(existing) && !Directory.Exists(existing))
        {
            rest = rest.Length == 0 ? Path.GetFileName(existing) : Path.Combine(Path.GetFileName(existing), rest);
            existing = Path.GetDirectoryName(existing);
        }
        if (existing is null) return full;
        using var handle = Native.OpenDirectory(existing);
        string? final = handle is null ? null : Native.FinalPath(handle);
        if (final is null) return full;
        final = Path.TrimEndingDirectorySeparator(final);
        return rest.Length == 0 ? final : Path.Combine(final, rest);
    }

    // Whether a relaunched tray took over: it either signaled that it holds the mutex (or found the running tray), or
    // it exited first, in which case the caller runs the tray itself. There is no timeout; whichever happens decides.
    public static bool ChildTookOver(Process child, EventWaitHandle ready)
    {
        using var exited = new ProcessExit(child);
        int first = WaitHandle.WaitAny([ready, exited]);
        return first == 0 || ready.WaitOne(0);
    }

    private sealed class ProcessExit : WaitHandle
    {
        public ProcessExit(Process process) =>
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process.Handle, ownsHandle: false);
    }

    public static void SignalReady(string configPath)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (EventWaitHandle.TryOpenExisting(ReadyEventName(configPath), out var ready))
                using (ready) ready.Set();
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException) { }
    }

    // Null when a live tray already holds the mutex for this configuration.
    public static Mutex? TryAcquire(string configPath)
    {
        var mutex = new Mutex(true, MutexName(configPath), out bool created);
        if (created) return mutex;
        mutex.Dispose();
        return null;
    }

    // A configuration the tray can run, or why it cannot run it yet. Nothing is thrown: the tray shows the reason and
    // tries again.
    public static (ServerConfig? Config, string? Error) TryLoad(string path)
    {
        try
        {
            var config = ServerConfig.Load(path);
            return ServerConfig.TransportRefusal(config, false) is { } refusal ? (null, refusal) : (config, null);
        }
        catch (Exception error) { return (null, error.GetType().Name + ": " + error.Message); }
    }
}

public enum TrayLook { Running, Stopped, Attention }

// What the tray knows about its server: the supervision states of the server and the owned tunnel ("starting",
// "running", "retrying: <cause>", "waiting for the server" or "stopped"), the listening port, and whether changes are
// paused. A configuration that is not loaded has no server at all.
public sealed record TrayState(bool Loaded, string Server, string Tunnel, bool TunnelConfigured, int? Port, bool Paused)
{
    public static readonly TrayState NotLoaded = new(false, "stopped", "stopped", false, null, false);

    public bool ServerRunning => Server == "running";
}

[Flags]
public enum TrayCommand
{
    None = 0, Status = 1 << 0, Log = 1 << 1, StartServer = 1 << 2, StopServer = 1 << 3, StartTunnel = 1 << 4, StopTunnel = 1 << 5,
    PauseChanges = 1 << 6, StopChildren = 1 << 7, RevokeTokens = 1 << 8, RemoveClients = 1 << 9, EditRoots = 1 << 10,
    OpenFolder = 1 << 11, StartWithWindows = 1 << 12, Exit = 1 << 13,
}

// A menu item that is a switch. Its state shows twice, as the drawn switch and as the word in the right-hand column.
public sealed record TrayToggle(string Label, bool On)
{
    public string Word => On ? "On" : "Off";
    public string Glyph => On ? "switch-on" : "switch-off";
    public string AccessibleName => Label + (On ? ", on" : ", off");
}

// Everything the tray menu shows for one state, so the rules can be checked without a menu.
public sealed record TrayMenuModel(string Header, TrayLook Look, TrayCommand Disabled, TrayToggle StartWithWindows, TrayToggle PauseChanges)
{
    public const string ServerOnly = "Available while the server runs.";
    private const TrayCommand NeedsServer = TrayCommand.PauseChanges | TrayCommand.StopChildren | TrayCommand.RevokeTokens | TrayCommand.RemoveClients;

    public bool Enabled(TrayCommand command) => (Disabled & command) == 0;

    public TrayToggle? Toggle(TrayCommand command) =>
        command == TrayCommand.StartWithWindows ? StartWithWindows : command == TrayCommand.PauseChanges ? PauseChanges : null;

    // The accessible description of an item that is greyed out because the server is not running.
    public string? Hint(TrayCommand command) => (NeedsServer & command) != 0 && !Enabled(command) ? ServerOnly : null;

    // Each start applies only while its part is stopped and each stop while it is not, so a part that retries can
    // always be stopped. The session, token and client items and Pause changes act through the running server.
    public static TrayMenuModel From(TrayState state, bool autostart)
    {
        var disabled = TrayCommand.None;
        void Unless(bool applies, TrayCommand command) { if (!applies) disabled |= command; }
        Unless(state.Loaded && state.Server == "stopped", TrayCommand.StartServer);
        Unless(state.Loaded && state.Server != "stopped", TrayCommand.StopServer);
        Unless(state.Loaded && state.Tunnel == "stopped" && state.TunnelConfigured && state.Port is not null, TrayCommand.StartTunnel);
        Unless(state.Loaded && state.Tunnel != "stopped", TrayCommand.StopTunnel);
        Unless(state.Loaded && state.ServerRunning, NeedsServer);
        Unless(state.Loaded, TrayCommand.EditRoots);
        return new TrayMenuModel(TrayStatus.Header(state), TrayStatus.Look(state), disabled,
            new TrayToggle("Start with Windows", autostart), new TrayToggle("Pause changes", state.Paused));
    }
}

// What the tray icon, its tooltip and the menu's header show.
public static class TrayStatus
{
    public const int HeaderLength = 64;
    public const string NotLoadedLine = "Configuration error, retrying every minute";

    // Running once the server runs and nothing retries; attention while a part retries or nothing is loaded.
    public static TrayLook Look(TrayState state) =>
        !state.Loaded || state.Server.StartsWith("retrying", StringComparison.Ordinal) || state.Tunnel.StartsWith("retrying", StringComparison.Ordinal)
            ? TrayLook.Attention
            : state.ServerRunning ? TrayLook.Running : TrayLook.Stopped;

    // Every state that is not the default: the server, the tunnel when one is configured or still owned, and paused
    // changes, for example "Server running · tunnel stopped · changes paused".
    public static string Header(TrayState state)
    {
        if (!state.Loaded) return NotLoadedLine;
        List<Part> parts = [Part.Of("Server", state.Server)];
        if (state.TunnelConfigured || state.Tunnel != "stopped") parts.Add(Part.Of("tunnel", state.Tunnel));
        if (state.Paused) parts.Add(new Part("changes paused", null, false));
        string text = string.Join(" · ", parts);
        // A long retry cause must not widen the whole menu or push the other states out, so only causes are shortened,
        // the longest first, and every state word stays.
        while (text.Length > HeaderLength)
        {
            int longest = -1;
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].Cause is { Length: > 0 } && (longest < 0 || parts[i].ToString().Length > parts[longest].ToString().Length)) longest = i;
            if (longest < 0) break;
            var cut = parts[longest];
            parts[longest] = cut with { Cause = cut.Cause![..^1].TrimEnd(), Cut = true };
            text = string.Join(" · ", parts);
        }
        return text.Length <= HeaderLength ? text : text[..(HeaderLength - 1)] + "…";
    }

    private sealed record Part(string Words, string? Cause, bool Cut)
    {
        public static Part Of(string name, string state)
        {
            int separator = state.IndexOf(": ", StringComparison.Ordinal);
            return separator < 0 ? new Part(name + " " + state, null, false) : new Part(name + " " + state[..separator], state[(separator + 2)..], false);
        }

        public override string ToString() =>
            Cause is null ? Words : Cause.Length == 0 ? Words + "…" : Words + ": " + Cause + (Cut ? "…" : "");
    }
}

// The notification each menu action ends with. Each text comes from what the action actually did: the state after it
// or the control endpoint's answer, never from what was asked.
public static class TrayNotice
{
    public static string ServerStarted(TrayState after) =>
        after.ServerRunning ? $"Server started on port {after.Port}."
        : Retrying(after.Server) is { } cause ? "The server did not start yet; CODEXish keeps retrying: " + cause + "."
        : "Server " + after.Server + ".";

    public static string ServerStopped(TrayState after) => after.Server == "stopped" ? "Server stopped." : "Server " + after.Server + ".";

    public static string TunnelStarted(TrayState after) =>
        after.Tunnel == "running" ? "Tunnel started."
        : Retrying(after.Tunnel) is { } cause ? "The tunnel did not start yet; CODEXish keeps retrying: " + cause + "."
        : "Tunnel " + after.Tunnel + ".";

    public static string TunnelStopped(TrayState after) => after.Tunnel == "stopped" ? "Tunnel stopped." : "Tunnel " + after.Tunnel + ".";

    public static string Paused(bool paused) => paused ? "Changes paused: writes wait until you resume." : "Changes resumed.";

    public static string StartWithWindows(bool on, bool restarts = false) =>
        !on ? "Start with Windows is off."
        : restarts ? "Start with Windows is on: CODEXish starts when you sign in, and again whenever it stops without Exit."
        : "Start with Windows is on: CODEXish starts when you sign in.";

    // Saving stops the server and starts again whatever was running; the text says whether that start succeeded.
    public static string RootsSaved(bool restarted, TrayState after) =>
        !restarted ? "Roots saved."
        : after.ServerRunning ? "Roots saved; the server restarted."
        : "Roots saved. " + ServerStarted(after);

    // The answer of /control/<action>. A field the answer lacks only makes the text less specific.
    public static string Control(string action, string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException) { root = default; }
        int? Count(string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.TryGetInt32(out int count) ? count : null;
        bool? Flag(string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean() : null;
        return action switch
        {
            "pause" or "resume" => Paused(Flag("paused") ?? action == "pause"),
            "kill-children" => Count("killed") switch
            {
                0 => "No processes were running.",
                1 => "Stopped 1 process ChatGPT started.",
                { } killed => $"Stopped {killed} processes ChatGPT started.",
                null => "Stopped the processes ChatGPT started.",
            },
            "revoke-tokens" => "All tokens revoked: ChatGPT will ask for the password again.",
            "remove-clients" => Count("removed") switch
            {
                1 => "Removed 1 registered client.",
                { } removed => $"Removed {removed} registered clients.",
                null => "Removed the registered clients.",
            },
            _ => "Done: " + action + ".",
        };
    }

    private static string? Retrying(string state) =>
        state.StartsWith("retrying: ", StringComparison.Ordinal) ? state["retrying: ".Length..].TrimEnd().TrimEnd('.') : null;
}

// Windows Forms exists only in the Windows build; elsewhere the command-line server remains the whole product.
public static class TrayApplication
{
    public static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(60);

    public static Task<int> Run(string path, TrayOptions? options = null, bool smoke = false, string[]? arguments = null)
    {
#if WINDOWS
        options ??= new TrayOptions();
        bool kept = arguments?.Contains(TrayKeeper.KeptMarker) == true;
        string? restarted = null;
        try { restarted = arguments is null ? null : CommandLine.Values(arguments, TrayKeeper.RestartedOption).FirstOrDefault(); }
        catch (ArgumentException) { /* a keeper note without its text is simply not logged */ }
        if (!smoke && arguments is not null && !arguments.Contains(TrayInstance.DetachedMarker))
        {
            // A start by hand ends an earlier --stop.
            TrayKeeper.ClearStopped(path);
            IAutostart? autostart = null;
            try { autostart = Autostart.ForThisProcess(path); }
            catch (Exception error) { Console.Error.WriteLine("CODEXish: autostart could not be read: " + error.GetType().Name + ": " + error.Message); }
            if (TrayKeeper.HandOff(path, autostart, Console.Out)) return Task.FromResult(0);
            // A relaunch that fails, or whose child exits before it took over, leaves the tray in this process.
            if (Relaunch(path, arguments)) return Task.FromResult(0);
        }
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Mutex? instance = null;
            try
            {
                Forms.Application.EnableVisualStyles();
                Forms.Application.SetCompatibleTextRenderingDefault(false);
                // Light or dark as Windows' app mode says; where that cannot be followed, the windows stay light.
                try { Forms.Application.SetColorMode(Forms.SystemColorMode.System); }
                catch (Exception error) { Console.Error.WriteLine("The tray keeps the light color mode: " + error.GetType().Name + ": " + error.Message); }
                if (!smoke)
                {
                    // Read before the ready signal: the launcher waiting for it then exits, and its own parents can no
                    // longer be named after that.
                    _ = Vitals.Launch;
                    instance = TrayInstance.TryAcquire(path);
                    // Either way a tray runs for this configuration now, so a waiting launcher can close.
                    TrayInstance.SignalReady(path);
                    if (instance is null)
                    {
                        // The keeper waits for that tray to end instead; nobody is there to read a message.
                        if (kept)
                        {
                            completion.SetResult(TrayKeeper.AnotherTrayExitCode);
                            return;
                        }
                        Forms.MessageBox.Show("CODEXish is already running; its icon is in the notification area.", "CODEXish",
                            Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                        completion.SetResult(0);
                        return;
                    }
                }
                bool start = options.Start;
                ServerConfig? config = null;
                string? loadError = null;
                // Started at sign-in (--start), the file existed when autostart was turned on, so a file that cannot be
                // seen now is retried like an unreadable one: never a first run whose setup would overwrite it.
                if (!smoke && (File.Exists(path) || options.Start)) (config, loadError) = TrayInstance.TryLoad(path);
                else if (!smoke)
                {
                    config = Setup(path, options);
                    if (config is null) { completion.SetResult(0); return; }
                    // First-run setup ends with the server, and a configured tunnel, running.
                    start = true;
                }
                using var context = new Context(config, loadError, path, smoke, start, restarted);
                Forms.Application.Run(context);
                completion.SetResult(context.ExitCode);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("TRAY_FAILED: " + error.GetType().Name + ": " + error.Message);
                // The detached tray has no console, so a failure is shown instead of lost.
                try { Forms.MessageBox.Show("The CODEXish tray stopped: " + error.GetType().Name + ": " + Redaction.Apply(error.Message).Text, "CODEXish"); }
                catch (Exception) { /* nothing else can report it */ }
                completion.TrySetResult(1);
            }
            finally
            {
                if (instance is not null)
                {
                    try { instance.ReleaseMutex(); } catch (ApplicationException) { }
                    instance.Dispose();
                }
            }
        }) { Name = "CODEXish tray", IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
#else
        Console.Error.WriteLine("The tray requires the Windows build; the command-line server remains available.");
        return Task.FromResult(2);
#endif
    }
#if WINDOWS
    // True only once the detached child holds the tray mutex, or found the tray that does. If it exits first, or the
    // relaunch cannot be set up at all, this process runs the tray itself.
    private static bool Relaunch(string path, string[] arguments)
    {
        try
        {
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, TrayInstance.ReadyEventName(path));
            var (executable, hosted) = TrayInstance.SelfLaunch();
            using var child = Process.Start(TrayInstance.DetachedStart(executable, hosted, arguments));
            if (child is null) return false;
            if (TrayInstance.ChildTookOver(child, ready)) return true;
            Console.Error.WriteLine($"The detached tray exited with code {child.ExitCode} before it took over; the tray runs in this process.");
            return false;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("The tray runs in this process; the detached relaunch failed: " + error.GetType().Name + ": " + error.Message);
            return false;
        }
    }

    internal sealed record MenuActions(Func<Task> Status, Func<Task> Log, Func<Task> StartServer, Func<Task> StopServer,
        Func<Task> StartTunnel, Func<Task> StopTunnel, Func<Task> PauseChanges, Func<Task> StopChildren,
        Func<Task> RevokeTokens, Func<Task> RemoveClients, Func<Task> EditRoots, Func<Task> OpenFolder, Func<Task> StartWithWindows,
        Func<Task> Exit);

    // The tray menu in display order, grouped by what the items act on; --design-preview builds the same menu.
    internal static TrayMenu BuildMenu(MenuActions actions, Func<Func<Task>, Task> run)
    {
        var menu = new TrayMenu(new Ui(96, Palette.Current));
        void Add(TrayCommand command, string text, string glyph, Func<Task> action, bool emphasized = false) =>
            menu.Add(command, text, glyph, emphasized).Click += async (_, _) => await run(action);
        // Bold, as Windows marks the item a double-click on the icon opens.
        Add(TrayCommand.Status, "Status / roots / processes", "activity", actions.Status, emphasized: true);
        Add(TrayCommand.Log, "Connection log", "scroll-text", actions.Log);
        menu.Separator();
        Add(TrayCommand.StartServer, "Start server", "play", actions.StartServer);
        Add(TrayCommand.StopServer, "Stop server and owned tunnel", "square", actions.StopServer);
        Add(TrayCommand.StartTunnel, "Start configured tunnel", "cloud", actions.StartTunnel);
        Add(TrayCommand.StopTunnel, "Stop owned tunnel", "cloud-off", actions.StopTunnel);
        menu.Separator();
        Add(TrayCommand.PauseChanges, "Pause changes", "switch-off", actions.PauseChanges);
        Add(TrayCommand.StopChildren, "Stop session children", "octagon-x", actions.StopChildren);
        menu.Separator();
        Add(TrayCommand.RevokeTokens, "Revoke all tokens", "key-round", actions.RevokeTokens);
        Add(TrayCommand.RemoveClients, "Remove registered clients", "user-x", actions.RemoveClients);
        menu.Separator();
        Add(TrayCommand.EditRoots, "Edit roots and grants", "folder-cog", actions.EditRoots);
        Add(TrayCommand.OpenFolder, "Open configuration folder", "folder-open", actions.OpenFolder);
        Add(TrayCommand.StartWithWindows, "Start with Windows", "switch-off", actions.StartWithWindows);
        menu.Separator();
        Add(TrayCommand.Exit, "Exit", "power", actions.Exit);
        return menu;
    }

    private sealed class Context : Forms.ApplicationContext
    {
        private readonly Forms.NotifyIcon icon;
        private readonly TrayMenu menu;
        private TrayMenuModel? applied;
        private TrayLook shown;
        private readonly Forms.Form dispatcher = new() { ShowInTaskbar = false };
        private readonly Forms.Timer timer = new() { Interval = 500 };
        private readonly Forms.Timer reload = new() { Interval = (int)ReloadInterval.TotalMilliseconds };
        private readonly IAutostart? autostart;
        private readonly string path;
        private readonly bool smoke;
        private readonly EventWaitHandle? stopRequest;
        private readonly RegisteredWaitHandle? stopWait;
        private TrayController? controller;
        private ServerConfig? config;
        private string? loadError;
        private string? startupNote;
        private string? keeperNote;
        private bool autostartOn;
        private bool exiting;
        public int ExitCode { get; private set; }

        public Context(ServerConfig? config, string? loadError, string path, bool smoke, bool start, string? restarted = null)
        {
            this.path = path; this.loadError = loadError; this.smoke = smoke;
            keeperNote = restarted;
            _ = dispatcher.Handle;
            // Without a known executable path there is nothing to autostart; the tray itself still runs.
            string? autostartProblem = null;
            try { autostart = smoke ? null : Autostart.ForThisProcess(path); }
            catch (Exception error) { autostartProblem = "Autostart is unavailable: " + error.GetType().Name + ": " + error.Message; }
            if (!smoke)
            {
                // --stop ends the tray the way Exit does.
                stopRequest = new EventWaitHandle(false, EventResetMode.AutoReset, TrayKeeper.StopEventName(path));
                stopWait = ThreadPool.RegisterWaitForSingleObject(stopRequest,
                    (_, _) => dispatcher.BeginInvoke(new Action(async () => await Exit())), null, Timeout.Infinite, executeOnlyOnce: true);
            }
            async Task Control(string action) => Notify(TrayNotice.Control(action, await Controller().ControlAsync(action)));
            menu = BuildMenu(new MenuActions(
                Status: ShowStatus,
                Log: () =>
                {
                    ShowText("CODEXish connection log", controller is null
                        ? NotLoaded()
                        : $"Complete log: {controller.LogFile}{Environment.NewLine}The most recent {TrayController.MemoryLines} lines:{Environment.NewLine}{Environment.NewLine}" +
                          string.Join(Environment.NewLine, controller.Diagnostics), controller is null ? Tone.Warning : null);
                    return Task.CompletedTask;
                },
                StartServer: async () => { await Controller().StartAsync(); Notify(TrayNotice.ServerStarted(Controller().State)); },
                StopServer: async () => { await Controller().StopAsync(); Notify(TrayNotice.ServerStopped(Controller().State)); },
                StartTunnel: async () => { await Controller().StartTunnelAsync(); Notify(TrayNotice.TunnelStarted(Controller().State)); },
                StopTunnel: async () => { await Controller().StopTunnelAsync(); Notify(TrayNotice.TunnelStopped(Controller().State)); },
                // The switch flips the hold the server has now, which another local client may have changed since the menu opened.
                PauseChanges: () => Control(Controller().Paused ? "resume" : "pause"),
                StopChildren: () => Control("kill-children"),
                RevokeTokens: () => Control("revoke-tokens"),
                RemoveClients: () => Control("remove-clients"),
                EditRoots: EditRoots,
                OpenFolder: () =>
                {
                    string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                    Directory.CreateDirectory(folder);
                    Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
                    return Task.CompletedTask;
                },
                StartWithWindows: () =>
                {
                    if (autostart is null) throw new InvalidOperationException(autostartProblem ?? "Autostart is unavailable.");
                    if (autostart.Enabled) autostart.Disable();
                    else autostart.Enable();
                    bool on = autostartOn = autostart.Enabled;
                    controller?.Note((on ? "Autostart written: " : "Autostart removed: ") + autostart.Location);
                    Notify(TrayNotice.StartWithWindows(on, autostart.Restarts));
                    return Task.CompletedTask;
                },
                Exit: Exit), Guard);
            // The state is read again, and the menu sized for the screen it opens on, every time it opens; the timer
            // below keeps it current while it stays open.
            menu.Strip.Opening += (_, _) =>
            {
                // Read once per opening: for the task it is a Task Scheduler query, too slow for the 500 ms timer. A query
                // that fails keeps the switch as it was last read, and the menu opens anyway.
                try { autostartOn = autostart?.Enabled == true; }
                catch (Exception error) { controller?.Note($"Reading the {autostart?.Location} failed: " + error.GetType().Name + ": " + error.Message); }
                applied = Model();
                menu.Apply(new Ui(DesignNative.DpiAt(Forms.Cursor.Position, menu.Strip.DeviceDpi), Palette.Current), applied);
            };
            shown = !smoke && config is null ? TrayLook.Attention : TrayLook.Stopped;
            icon = new Forms.NotifyIcon { Icon = TrayIcons.For(shown, Forms.SystemInformation.SmallIconSize.Width), Text = "CODEXish — stopped", ContextMenuStrip = menu.Strip, Visible = true };
            icon.DoubleClick += async (_, _) => await Guard(ShowStatus);
            if (!smoke && autostart is not null)
            {
                try
                {
                    if (autostart.Heal() is { } reason) autostartProblem = autostart.Rewritten(reason);
                }
                catch (Exception error) { autostartProblem = $"Repairing the {autostart.Location} failed: " + error.GetType().Name + ": " + error.Message; }
            }
            reload.Tick += (_, _) => Reload();
            if (!smoke)
            {
                Microsoft.Win32.SystemEvents.SessionEnded += SessionEnded;
                startupNote = autostartProblem;
                if (config is not null) Attach(config, start);
                else ReportLoadError();
            }
            timer.Tick += async (_, _) =>
            {
                if (smoke)
                {
                    timer.Stop();
                    if (!icon.Visible || menu.Strip.Items.Count < 10 || dispatcher.Handle == 0) ExitCode = 1;
                    Console.WriteLine(ExitCode == 0
                        ? "TRAY_UI_SMOKE_PASSED: notification icon, menu and STA dispatcher created and cleaned; no server, tunnel or user configuration opened."
                        : "TRAY_UI_SMOKE_FAILED");
                    await Exit();
                }
                else
                {
                    var state = State();
                    string header = TrayStatus.Header(state);
                    icon.Text = Tooltip(header);
                    var look = TrayStatus.Look(state);
                    if (look != shown)
                    {
                        shown = look;
                        icon.Icon = TrayIcons.For(look, Forms.SystemInformation.SmallIconSize.Width);
                    }
                    if (menu.Strip.Visible && Model(state) is var model && model != applied)
                    {
                        applied = model;
                        menu.Apply(menu.Ui, model);
                    }
                }
            };
            timer.Start();
        }

        private TrayState State() => controller?.State ?? TrayState.NotLoaded;

        private TrayMenuModel Model(TrayState? state = null) => TrayMenuModel.From(state ?? State(), autostartOn);

        // Windows may hold a balloon back, for example under Do Not Disturb; the menu and its header stay the record. A
        // balloon holds at most 255 characters, which a long retry cause could exceed.
        private void Notify(string text) =>
            icon.ShowBalloonTip(5000, "CODEXish", text.Length <= 255 ? text : text[..254] + "…", Forms.ToolTipIcon.Info);

        private TrayController Controller() => controller ?? throw new InvalidOperationException(NotLoaded());

        private string NotLoaded() =>
            $"The configuration at {path} is not loaded:{Environment.NewLine}{loadError}{Environment.NewLine}{Environment.NewLine}" +
            "CODEXish tries to load it again every minute and then starts the server. Open configuration folder shows the file, " +
            "and a readable codexish.json.bak next to it is restored automatically. To create a new configuration instead, " +
            "choose Exit and start CODEXish without --start.";

        private void Attach(ServerConfig loaded, bool start)
        {
            config = loaded;
            loadError = null;
            controller = new TrayController(loaded);
            // The log lives in state_dir, so how this tray was started and what happened to the autostart shortcut are
            // written once a configuration loads.
            controller.Note($"CODEXish {CodexishRuntime.ServerVersion} tray, pid {Environment.ProcessId}, started {Vitals.Launch}.");
            if (startupNote is not null) controller.Note(startupNote);
            startupNote = null;
            if (keeperNote is not null) controller.Note("Started again by the keeper: " + keeperNote + ".");
            keeperNote = null;
            // Posted to the message loop, so the server and tunnel start once the tray is up.
            if (start) dispatcher.BeginInvoke(new Action(async () => await Guard(controller.StartConfiguredAsync)));
        }

        private void ReportLoadError()
        {
            string text = loadError ?? "unknown error";
            icon.ShowBalloonTip(15000, "CODEXish configuration error",
                (text.Length > 200 ? text[..199] + "…" : text) + " CODEXish tries again every minute.", Forms.ToolTipIcon.Error);
            reload.Start();
        }

        // Once the file loads, the tray proceeds as --start would have: the server, then a configured tunnel.
        private void Reload()
        {
            var (loaded, error) = TrayInstance.TryLoad(path);
            if (loaded is null)
            {
                if (error != loadError)
                {
                    loadError = error;
                    ReportLoadError();
                }
                return;
            }
            reload.Stop();
            string? earlier = loadError;
            Attach(loaded, start: true);
            controller?.Note("Configuration loaded after an earlier attempt failed: " + earlier);
            icon.ShowBalloonTip(5000, "CODEXish", "The configuration loaded; the server is starting.", Forms.ToolTipIcon.Info);
        }

        private async Task ShowStatus()
        {
            if (controller is null) ShowText("CODEXish status", NotLoaded(), Tone.Warning);
            else ShowText("CODEXish status", Indented(await controller.ControlAsync("status")));
        }

        // A notification icon's tooltip holds at most 127 characters.
        private static string Tooltip(string status)
        {
            string text = "CODEXish — " + status;
            return text.Length <= 127 ? text : text[..126] + "…";
        }

        private async Task Guard(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) { ShowText("CODEXish operation failed", error.GetType().Name + ": " + Redaction.Apply(error.Message).Text, Tone.Error); }
        }

        private Task EditRoots()
        {
            if (config is null || controller is null) throw new InvalidOperationException(NotLoaded());
            var editing = config;
            var owner = controller;
            using var form = DesignForm.Open("CODEXish roots and grants");
            var view = TrayWindows.Roots(form, editing.Roots);
            view.Save = roots => Guard(async () =>
            {
                var candidate = JsonSerializer.Deserialize<ServerConfig>(JsonSerializer.Serialize(editing))!;
                candidate.Roots = roots;
                candidate.Validate();
                // Anything not stopped, running or still retrying, was wanted on, so it comes back after the save.
                var before = owner.State;
                bool serverWasRunning = before.Server != "stopped", tunnelWasRunning = before.Tunnel != "stopped";
                await owner.StopAsync();
                candidate.Save(path);
                editing.Roots = candidate.Roots;
                // The running configuration is reused on the next start; recompute its warnings for the new roots.
                editing.Validate();
                // Saving must not leave ChatGPT disconnected: bring back exactly what was on.
                if (serverWasRunning) await owner.StartAsync();
                if (tunnelWasRunning) await owner.StartTunnelAsync();
                Notify(TrayNotice.RootsSaved(serverWasRunning, owner.State));
                if (candidate.Warnings.Count > 0) ShowText("CODEXish roots saved with warnings", string.Join(Environment.NewLine, candidate.Warnings), Tone.Warning);
                form.Close();
            });
            form.ShowDialog();
            return Task.CompletedTask;
        }

        private async Task Exit()
        {
            if (exiting) return;
            exiting = true;
            timer.Stop();
            reload.Stop();
            try { if (controller is not null) await controller.DisposeAsync(); }
            finally
            {
                if (!smoke)
                {
                    // A keeper, if one started this tray, waits instead of starting it again.
                    TrayKeeper.ChoseExit(path);
                    if (!KeeperCanBeAsked()) TrayKeeper.EndKeeper(path);
                }
                icon.Visible = false;
                ExitThread();
            }
        }

        // Only the task for this configuration asks a waiting keeper for a start, by hand-off or at sign-in. When that
        // cannot be read, the keeper waits, as it would with the task there.
        private bool KeeperCanBeAsked()
        {
            try { return autostart is TrayTask task && task.RegisteredForThis(); }
            catch (Exception) { return true; }
        }

        // Windows ends the process right after this event, so the reason has to be written now.
        private void SessionEnded(object? sender, Microsoft.Win32.SessionEndedEventArgs e)
        {
            string reason = e.Reason == Microsoft.Win32.SessionEndReasons.Logoff ? "Windows signed the user out" : "Windows shut down or restarted";
            Vitals.EndAll(reason);
            controller?.Note(reason + "; CODEXish ends with the session.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Microsoft.Win32.SystemEvents.SessionEnded -= SessionEnded;
                stopWait?.Unregister(null);
                stopRequest?.Dispose();
                timer.Dispose(); reload.Dispose(); icon.Dispose(); menu.Strip.Dispose(); dispatcher.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static void ShowText(string title, string text, Tone? tone = null)
    {
        var form = DesignForm.Open(title);
        TrayWindows.Viewer(form, text, tone);
        form.Show();
    }

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // The control endpoint answers in compact JSON; the status window shows it indented.
    private static string Indented(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, IndentedJson).ReplaceLineEndings(Environment.NewLine);
        }
        catch (JsonException) { return json; }
    }

    private static ServerConfig? Setup(string path, TrayOptions options)
    {
        ServerConfig? created = null;
        var (rootId, rootPath) = options.RootEntry();
        using var form = DesignForm.Open("CODEXish first-run setup");
        var view = TrayWindows.Setup(form, new SetupValues(options.PublicUrl ?? "", options.Port ?? "3000", rootPath, ServerConfig.DefaultRedirectUri, path));
        view.Create.Click += (_, _) =>
        {
            FieldBox? field = null;
            try
            {
                if (view.Password.Input.Text.Length == 0) { field = view.Password; throw new ArgumentException("Enter a new server password."); }
                if (!int.TryParse(view.Port.Input.Text.Trim(), out int localPort)) { field = view.Port; throw new ArgumentException("Enter the local port as a number, for example 3000."); }
                string[] callbacks = string.IsNullOrWhiteSpace(view.Callback.Input.Text) ? [] : [view.Callback.Input.Text.Trim()];
                // An empty folder writes no root entry; folders can be added later with Edit roots and grants.
                created = ServerConfig.Create(view.Url.Input.Text.Trim(), view.Password.Input.Text, TrayOptions.SetupRoots(rootId, view.Folder.Input.Text),
                    callbacks, localPort, null);
                created.Save(path);
                string signInResult = "Start at sign-in: off. Turn it on with Start with Windows in the tray menu.";
                if (view.SignIn.Checked)
                {
                    try
                    {
                        var autostart = Autostart.ForThisProcess(path);
                        autostart.Enable();
                        signInResult = "Start at sign-in: on (" + autostart.Location + ").";
                    }
                    catch (Exception error) { signInResult = "Start at sign-in could not be set up (" + error.Message + "). Use Start with Windows in the tray menu."; }
                }
                var details = DesignForm.Open("CODEXish connection details — keep private");
                TrayWindows.Connection(details, new ConnectionDetails(created.PublicUrl + "/mcp", created.OAuth.ClientId, created.OAuth.ClientSecret,
                    path, created.Port, signInResult, created.Roots.Length > 0, created.Warnings));
                details.Show();
                form.Close();
            }
            catch (Exception error)
            {
                created = null;
                field ??= error.Message.Contains("public_url", StringComparison.Ordinal) ? view.Url
                    : error.Message.Contains("port", StringComparison.OrdinalIgnoreCase) ? view.Port : null;
                view.ShowError(error.Message, field);
            }
        };
        form.ShowDialog();
        return created;
    }
#endif
}
