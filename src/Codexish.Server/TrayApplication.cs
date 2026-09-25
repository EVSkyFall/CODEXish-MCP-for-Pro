#if WINDOWS
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
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

    // One tray per configuration: the name comes from the full configuration path, compared the way the platform does.
    public static string MutexName(string configPath)
    {
        string full = Path.GetFullPath(configPath);
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
        return @"Local\CODEXish-tray-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
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

// Windows Forms exists only in the Windows build; elsewhere the command-line server remains the whole product.
public static class TrayApplication
{
    public static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(60);

    public static Task<int> Run(string path, TrayOptions? options = null, bool smoke = false, string[]? arguments = null)
    {
#if WINDOWS
        options ??= new TrayOptions();
        // A relaunch that fails leaves the tray in this process, exactly as before.
        if (!smoke && arguments is not null && !arguments.Contains(TrayInstance.DetachedMarker) && Relaunch(arguments))
            return Task.FromResult(0);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Mutex? instance = null;
            try
            {
                Forms.Application.EnableVisualStyles();
                Forms.Application.SetCompatibleTextRenderingDefault(false);
                if (!smoke)
                {
                    instance = TrayInstance.TryAcquire(path);
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
    private static bool Relaunch(string[] arguments)
    {
        try
        {
            var (executable, hosted) = TrayInstance.SelfLaunch();
            using var child = Process.Start(TrayInstance.DetachedStart(executable, hosted, arguments));
            return child is not null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
    }

    private sealed class Context : Forms.ApplicationContext
    {
        private readonly Forms.NotifyIcon icon;
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
            var menu = new Forms.ContextMenuStrip();
            void Add(string title, Func<Task> action) => menu.Items.Add(title, null, async (_, _) => await Guard(action));
            Add("Start server", async () => await Controller().StartAsync());
            Add("Stop server and owned tunnel", async () => await Controller().StopAsync());
            Add("Start configured tunnel", async () => await Controller().StartTunnelAsync());
            Add("Stop owned tunnel", async () => await Controller().StopTunnelAsync());
            menu.Items.Add(new Forms.ToolStripSeparator());
            Add("Status / roots / processes", ShowStatus);
            Add("Pause changes", async () => { await Controller().ControlAsync("pause"); });
            Add("Resume changes", async () => { await Controller().ControlAsync("resume"); });
            Add("Stop session children", async () => { await Controller().ControlAsync("kill-children"); });
            Add("Revoke all tokens", async () => { await Controller().ControlAsync("revoke-tokens"); });
            Add("Edit roots and grants", EditRoots);
            Add("Connection log", () =>
            {
                ShowText("CODEXish connection log", controller is null
                    ? NotLoaded()
                    : $"Complete log: {controller.LogFile}{Environment.NewLine}The most recent {TrayController.MemoryLines} lines:{Environment.NewLine}{Environment.NewLine}" +
                      string.Join(Environment.NewLine, controller.Diagnostics));
                return Task.CompletedTask;
            });
            Add("Open configuration folder", () =>
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
                return Task.CompletedTask;
            });
            menu.Items.Add(new Forms.ToolStripSeparator());
            // Checked exactly when the shortcut exists; read again every time the menu opens.
            var startWithWindows = new Forms.ToolStripMenuItem("Start with Windows");
            startWithWindows.Click += async (_, _) => await Guard(() =>
            {
                if (autostart is null) throw new InvalidOperationException(autostartProblem ?? "Autostart is unavailable.");
                if (autostart.Enabled) autostart.Disable();
                else autostart.Enable();
                controller?.Note((autostart.Enabled ? "Autostart shortcut written: " : "Autostart shortcut removed: ") + autostart.ShortcutPath);
                return Task.CompletedTask;
            });
            menu.Items.Add(startWithWindows);
            menu.Opening += (_, _) => startWithWindows.Checked = autostart?.Enabled == true;
            Add("Exit", Exit);
            icon = new Forms.NotifyIcon { Icon = Drawing.SystemIcons.Application, Text = "CODEXish — stopped", ContextMenuStrip = menu, Visible = true };
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
                    if (!icon.Visible || menu.Items.Count < 10 || dispatcher.Handle == 0) ExitCode = 1;
                    Console.WriteLine(ExitCode == 0
                        ? "TRAY_UI_SMOKE_PASSED: notification icon, menu and STA dispatcher created and cleaned; no server, tunnel or user configuration opened."
                        : "TRAY_UI_SMOKE_FAILED");
                    await Exit();
                }
                else icon.Text = Tooltip(controller?.Status ?? "configuration error, retrying every minute");
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
            if (controller is null) ShowText("CODEXish status", NotLoaded());
            else ShowText("CODEXish status", await controller.ControlAsync("status"));
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
            catch (Exception error) { ShowText("CODEXish operation failed", error.GetType().Name + ": " + Redaction.Apply(error.Message).Text); }
        }

        private Task EditRoots()
        {
            if (config is null || controller is null) throw new InvalidOperationException(NotLoaded());
            var editing = config;
            var owner = controller;
            using var form = new Forms.Form { Text = "CODEXish roots — stop before applying", Width = 800, Height = 400, StartPosition = Forms.FormStartPosition.CenterScreen };
            var grid = new Forms.DataGridView
            {
                Dock = Forms.DockStyle.Fill, AutoGenerateColumns = true,
                DataSource = new System.ComponentModel.BindingList<RootConfig>(editing.Roots.Select(r => new RootConfig { Id = r.Id, Path = r.Path, Read = r.Read, Write = r.Write, Shell = r.Shell }).ToList())
            };
            var apply = new Forms.Button { Text = "Save roots and stop server", Dock = Forms.DockStyle.Bottom, Height = 40 };
            apply.Click += async (_, _) => await Guard(async () =>
            {
                grid.EndEdit();
                var candidate = JsonSerializer.Deserialize<ServerConfig>(JsonSerializer.Serialize(editing))!;
                candidate.Roots = ((System.ComponentModel.BindingList<RootConfig>)grid.DataSource).ToArray();
                candidate.Validate();
                await owner.StopAsync();
                candidate.Save(path);
                editing.Roots = candidate.Roots;
                if (candidate.Warnings.Count > 0) ShowText("CODEXish roots saved with warnings", string.Join(Environment.NewLine, candidate.Warnings));
                form.Close();
            });
            form.Controls.Add(grid);
            form.Controls.Add(apply);
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
            if (disposing) { timer.Dispose(); reload.Dispose(); icon.Dispose(); dispatcher.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private static void ShowText(string title, string text)
    {
        var form = new Forms.Form { Text = title, Width = 850, Height = 540, StartPosition = Forms.FormStartPosition.CenterScreen };
        form.Controls.Add(new Forms.TextBox { Multiline = true, ReadOnly = true, Dock = Forms.DockStyle.Fill, ScrollBars = Forms.ScrollBars.Both, Text = text });
        form.Show();
    }

    private static ServerConfig? Setup(string path, TrayOptions options)
    {
        ServerConfig? created = null;
        var (rootId, rootPath) = options.RootEntry();
        using var form = new Forms.Form { Text = "CODEXish first-run setup", Width = 700, Height = 500, StartPosition = Forms.FormStartPosition.CenterScreen };
        var panel = new Forms.TableLayoutPanel { Dock = Forms.DockStyle.Fill, ColumnCount = 2, RowCount = 8, Padding = new Forms.Padding(16) };
        var url = new Forms.TextBox { Width = 440, PlaceholderText = "https://your-tunnel-host", Text = options.PublicUrl ?? "" };
        var port = new Forms.TextBox { Width = 120, Text = options.Port ?? "3000" };
        var root = new Forms.TextBox { Width = 440, PlaceholderText = "Existing project directory", Text = rootPath };
        var password = new Forms.TextBox { Width = 440, UseSystemPasswordChar = true };
        var callback = new Forms.TextBox { Width = 440, Text = ServerConfig.DefaultRedirectUri };
        var signIn = new Forms.CheckBox { Text = "Start CODEXish when I sign in to Windows", Checked = true, AutoSize = true };
        foreach (var (label, control) in new (string, Forms.Control)[] { ("Public HTTPS origin", url), ("Local port", port), ("Project root (read / write / shell)", root), ("New CODEXish password", password), ("OAuth callback", callback) })
        {
            panel.Controls.Add(new Forms.Label { Text = label, AutoSize = true });
            panel.Controls.Add(control);
        }
        panel.Controls.Add(new Forms.Label { AutoSize = true });
        panel.Controls.Add(signIn);
        var save = new Forms.Button { Text = "Create local configuration", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                if (password.Text.Length == 0) throw new ArgumentException("Enter a new server password.");
                if (!int.TryParse(port.Text.Trim(), out int localPort)) throw new ArgumentException("Enter the local port as a number, for example 3000.");
                string[] callbacks = string.IsNullOrWhiteSpace(callback.Text) ? [] : [callback.Text.Trim()];
                created = ServerConfig.Create(url.Text, password.Text, [(rootId, root.Text)], callbacks, localPort, null);
                created.Save(path);
                string signInResult = "Start at sign-in: off. Turn it on with Start with Windows in the tray menu.";
                if (signIn.Checked)
                {
                    try
                    {
                        var autostart = TrayAutostart.ForThisProcess(path);
                        autostart.Enable();
                        signInResult = "Start at sign-in: on (" + autostart.ShortcutPath + ").";
                    }
                    catch (Exception error) { signInResult = "Start at sign-in could not be set up (" + error.Message + "). Use Start with Windows in the tray menu."; }
                }
                string warnings = created.Warnings.Count == 0 ? "" :
                    Environment.NewLine + "Warnings:" + Environment.NewLine + string.Join(Environment.NewLine, created.Warnings);
                ShowText("CODEXish connection details — keep private", "MCP endpoint: " + created.PublicUrl + "/mcp" + Environment.NewLine +
                    "Client ID: " + created.OAuth.ClientId + Environment.NewLine + "Client secret: " + created.OAuth.ClientSecret + Environment.NewLine +
                    "Configuration: " + path + Environment.NewLine + "Local port: " + created.Port + Environment.NewLine + signInResult + Environment.NewLine +
                    "Use your new CODEXish password on its login page. This is not your OpenAI password." + warnings);
                form.Close();
            }
            catch (Exception error) { created = null; Forms.MessageBox.Show(form, error.Message, "Configuration not saved"); }
        };
        panel.Controls.Add(save);
        form.Controls.Add(panel);
        form.ShowDialog();
        return created;
    }
#endif
}
