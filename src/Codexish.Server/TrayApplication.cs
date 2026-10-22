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

    private static string InstanceHash(string configPath)
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

// What the tray icon and the menu's header show for TrayController.Status; null means the configuration is not loaded.
public static class TrayStatus
{
    // Running once the server runs and nothing retries; attention while a part retries or nothing is loaded.
    public static TrayLook Look(string? status) =>
        status is null || status.Contains("retrying", StringComparison.Ordinal) ? TrayLook.Attention
        : status.StartsWith("server running", StringComparison.Ordinal) ? TrayLook.Running
        : TrayLook.Stopped;

    // The header's status line, capitalized and shortened so a long retry cause cannot widen the whole menu.
    public static string Line(string? status)
    {
        string text = string.IsNullOrEmpty(status) ? "Configuration error, retrying every minute" : char.ToUpperInvariant(status[0]) + status[1..];
        return text.Length <= 64 ? text : text[..63] + "…";
    }
}

// Windows Forms exists only in the Windows build; elsewhere the command-line server remains the whole product.
public static class TrayApplication
{
    public static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(60);

    public static Task<int> Run(string path, TrayOptions? options = null, bool smoke = false, string[]? arguments = null)
    {
#if WINDOWS
        options ??= new TrayOptions();
        // A relaunch that fails, or whose child exits before it took over, leaves the tray in this process.
        if (!smoke && arguments is not null && !arguments.Contains(TrayInstance.DetachedMarker) && Relaunch(path, arguments))
            return Task.FromResult(0);
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
                    instance = TrayInstance.TryAcquire(path);
                    // Either way a tray runs for this configuration now, so a waiting launcher can close.
                    TrayInstance.SignalReady(path);
                    if (instance is null)
                    {
                        Forms.MessageBox.Show("CODEXish is already running; its icon is in the notification area.", "CODEXish",
                            Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                        completion.SetResult(0);
                        return;
                    }
                }
                bool start = options.Start;
                ServerConfig? config = null;
                string? loadError = null;
                if (!smoke && File.Exists(path)) (config, loadError) = TrayInstance.TryLoad(path);
                else if (!smoke)
                {
                    config = Setup(path, options);
                    if (config is null) { completion.SetResult(0); return; }
                    // First-run setup ends with the server, and a configured tunnel, running.
                    start = true;
                }
                using var context = new Context(config, loadError, path, smoke, start);
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
        Func<Task> StartTunnel, Func<Task> StopTunnel, Func<Task> Pause, Func<Task> Resume, Func<Task> StopChildren,
        Func<Task> RevokeTokens, Func<Task> RemoveClients, Func<Task> EditRoots, Func<Task> OpenFolder, Func<Task> StartWithWindows,
        Func<Task> Exit);

    // The tray menu in display order, grouped by what the items act on; --design-preview builds the same menu.
    internal static (TrayMenu Menu, Forms.ToolStripMenuItem StartWithWindows) BuildMenu(MenuActions actions, Func<Func<Task>, Task> run)
    {
        var menu = new TrayMenu(new Ui(96, Palette.Current));
        Forms.ToolStripMenuItem Add(string text, string glyph, Func<Task> action, bool emphasized = false)
        {
            var item = menu.Add(text, glyph, emphasized);
            item.Click += async (_, _) => await run(action);
            return item;
        }
        // Bold, as Windows marks the item a double-click on the icon opens.
        Add("Status / roots / processes", "activity", actions.Status, emphasized: true);
        Add("Connection log", "scroll-text", actions.Log);
        menu.Separator();
        Add("Start server", "play", actions.StartServer);
        Add("Stop server and owned tunnel", "square", actions.StopServer);
        Add("Start configured tunnel", "cloud", actions.StartTunnel);
        Add("Stop owned tunnel", "cloud-off", actions.StopTunnel);
        menu.Separator();
        Add("Pause changes", "circle-pause", actions.Pause);
        Add("Resume changes", "circle-play", actions.Resume);
        Add("Stop session children", "octagon-x", actions.StopChildren);
        menu.Separator();
        Add("Revoke all tokens", "key-round", actions.RevokeTokens);
        Add("Remove registered clients", "user-x", actions.RemoveClients);
        menu.Separator();
        Add("Edit roots and grants", "folder-cog", actions.EditRoots);
        Add("Open configuration folder", "folder-open", actions.OpenFolder);
        var startWithWindows = Add("Start with Windows", "toggle-left", actions.StartWithWindows);
        menu.Separator();
        Add("Exit", "power", actions.Exit);
        return (menu, startWithWindows);
    }

    // Checked exactly when the shortcut exists; the switch icon shows the same state.
    internal static void ShowAutostart(Forms.ToolStripMenuItem item, bool enabled)
    {
        item.Checked = enabled;
        item.Tag = new MenuGlyph(enabled ? "toggle-right" : "toggle-left", false);
    }

    private sealed class Context : Forms.ApplicationContext
    {
        private readonly Forms.NotifyIcon icon;
        private readonly TrayMenu menu;
        private TrayLook shown;
        private readonly Forms.Form dispatcher = new() { ShowInTaskbar = false };
        private readonly Forms.Timer timer = new() { Interval = 500 };
        private readonly Forms.Timer reload = new() { Interval = (int)ReloadInterval.TotalMilliseconds };
        private readonly TrayAutostart? autostart;
        private readonly string path;
        private TrayController? controller;
        private ServerConfig? config;
        private string? loadError;
        private bool exiting;
        public int ExitCode { get; private set; }

        public Context(ServerConfig? config, string? loadError, string path, bool smoke, bool start)
        {
            this.path = path; this.loadError = loadError;
            _ = dispatcher.Handle;
            // Without a known executable path there is nothing to autostart; the tray itself still runs.
            string? autostartProblem = null;
            try { autostart = smoke ? null : TrayAutostart.ForThisProcess(path); }
            catch (Exception error) { autostartProblem = "Autostart is unavailable: " + error.GetType().Name + ": " + error.Message; }
            (menu, var startWithWindows) = BuildMenu(new MenuActions(
                Status: ShowStatus,
                Log: () =>
                {
                    ShowText("CODEXish connection log", controller is null
                        ? NotLoaded()
                        : $"Complete log: {controller.LogFile}{Environment.NewLine}The most recent {TrayController.MemoryLines} lines:{Environment.NewLine}{Environment.NewLine}" +
                          string.Join(Environment.NewLine, controller.Diagnostics), controller is null ? Tone.Warning : null);
                    return Task.CompletedTask;
                },
                StartServer: async () => await Controller().StartAsync(),
                StopServer: async () => await Controller().StopAsync(),
                StartTunnel: async () => await Controller().StartTunnelAsync(),
                StopTunnel: async () => await Controller().StopTunnelAsync(),
                Pause: async () => { await Controller().ControlAsync("pause"); },
                Resume: async () => { await Controller().ControlAsync("resume"); },
                StopChildren: async () => { await Controller().ControlAsync("kill-children"); },
                RevokeTokens: async () => { await Controller().ControlAsync("revoke-tokens"); },
                RemoveClients: async () => { await Controller().ControlAsync("remove-clients"); },
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
                    controller?.Note((autostart.Enabled ? "Autostart shortcut written: " : "Autostart shortcut removed: ") + autostart.ShortcutPath);
                    return Task.CompletedTask;
                },
                Exit: Exit), Guard);
            // The autostart state is read again, and the menu sized for the screen it opens on, every time it opens.
            menu.Strip.Opening += (_, _) =>
            {
                ShowAutostart(startWithWindows, autostart?.Enabled == true);
                string? status = controller?.Status;
                menu.Apply(new Ui(DesignNative.DpiAt(Forms.Cursor.Position, menu.Strip.DeviceDpi), Palette.Current),
                    TrayStatus.Line(status), TrayStatus.Look(status));
            };
            shown = !smoke && config is null ? TrayLook.Attention : TrayLook.Stopped;
            icon = new Forms.NotifyIcon { Icon = TrayIcons.For(shown, Forms.SystemInformation.SmallIconSize.Width), Text = "CODEXish — stopped", ContextMenuStrip = menu.Strip, Visible = true };
            icon.DoubleClick += async (_, _) => await Guard(ShowStatus);
            if (!smoke && autostart is not null)
            {
                try
                {
                    if (autostart.Heal()) autostartProblem = "The autostart shortcut named a file that no longer exists; it now starts " + Environment.ProcessPath;
                }
                catch (Exception error) { autostartProblem = "Repairing the autostart shortcut failed: " + error.GetType().Name + ": " + error.Message; }
            }
            reload.Tick += (_, _) => Reload();
            if (!smoke)
            {
                if (config is not null) Attach(config, start);
                else ReportLoadError();
                if (autostartProblem is not null) controller?.Note(autostartProblem);
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
                    string? status = controller?.Status;
                    icon.Text = Tooltip(status ?? "configuration error, retrying every minute");
                    var look = TrayStatus.Look(status);
                    if (look != shown)
                    {
                        shown = look;
                        icon.Icon = TrayIcons.For(look, Forms.SystemInformation.SmallIconSize.Width);
                    }
                }
            };
            timer.Start();
        }

        private TrayController Controller() => controller ?? throw new InvalidOperationException(NotLoaded());

        private string NotLoaded() =>
            $"The configuration at {path} is not loaded:{Environment.NewLine}{loadError}{Environment.NewLine}{Environment.NewLine}" +
            "CODEXish tries to load it again every minute and then starts the server. Open configuration folder shows the file, " +
            "and a readable codexish.json.bak next to it is restored automatically.";

        private void Attach(ServerConfig loaded, bool start)
        {
            config = loaded;
            loadError = null;
            controller = new TrayController(loaded);
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
            Attach(loaded, start: true);
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
                bool serverWasRunning = owner.Running, tunnelWasRunning = owner.TunnelRunning;
                await owner.StopAsync();
                candidate.Save(path);
                editing.Roots = candidate.Roots;
                // The running configuration is reused on the next start; recompute its warnings for the new roots.
                editing.Validate();
                // Saving must not leave ChatGPT disconnected: bring back exactly what was running.
                if (serverWasRunning) await owner.StartAsync();
                if (tunnelWasRunning) await owner.StartTunnelAsync();
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
            finally { icon.Visible = false; ExitThread(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); reload.Dispose(); icon.Dispose(); menu.Strip.Dispose(); dispatcher.Dispose(); }
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
                        var autostart = TrayAutostart.ForThisProcess(path);
                        autostart.Enable();
                        signInResult = "Start at sign-in: on (" + autostart.ShortcutPath + ").";
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
