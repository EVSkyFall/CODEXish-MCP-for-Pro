#if WINDOWS
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
#endif
using System.Text.Json;

namespace Codexish.Server;

// Windows Forms exists only in the Windows build; elsewhere the command-line server remains the whole product.
public static class TrayApplication
{
    public static Task<int> Run(string path, TrayOptions? options = null, bool smoke = false)
    {
#if WINDOWS
        options ??= new TrayOptions();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.EnableVisualStyles();
                Forms.Application.SetCompatibleTextRenderingDefault(false);
                bool start = options.Start;
                ServerConfig? config = null;
                if (!smoke && File.Exists(path)) config = ServerConfig.Load(path);
                else if (!smoke)
                {
                    config = Setup(path, options);
                    if (config is null) { completion.SetResult(0); return; }
                    // First-run setup ends with the server, and a configured tunnel, running.
                    start = true;
                }
                using var context = new Context(config, path, smoke, start);
                Forms.Application.Run(context);
                completion.SetResult(context.ExitCode);
            }
            catch (Exception error) { Console.Error.WriteLine("TRAY_FAILED: " + error.GetType().Name + ": " + error.Message); completion.SetResult(1); }
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
    private sealed class Context : Forms.ApplicationContext
    {
        private readonly Forms.NotifyIcon icon;
        private readonly Forms.Form dispatcher = new() { ShowInTaskbar = false };
        private readonly Forms.Timer timer = new() { Interval = 500 };
        private readonly TrayController? controller;
        private readonly ServerConfig? config;
        private readonly TrayAutostart? autostart;
        private readonly string path;
        private bool exiting;
        public int ExitCode { get; private set; }

        public Context(ServerConfig? config, string path, bool smoke, bool start)
        {
            this.config = config; this.path = path;
            _ = dispatcher.Handle;
            controller = config is null ? null : new TrayController(config);
            // Without a known executable path there is nothing to autostart; the tray itself still runs.
            try { autostart = config is null ? null : TrayAutostart.ForThisProcess(path); }
            catch (Exception error) { controller?.Note("Autostart is unavailable: " + error.GetType().Name + ": " + error.Message); }
            var menu = new Forms.ContextMenuStrip();
            void Add(string title, Func<Task> action) => menu.Items.Add(title, null, async (_, _) => await Guard(action));
            Add("Start server", async () => await controller!.StartAsync());
            Add("Stop server and owned tunnel", async () => await controller!.StopAsync());
            Add("Start configured tunnel", async () => await controller!.StartTunnelAsync());
            Add("Stop owned tunnel", async () => await controller!.StopTunnelAsync());
            menu.Items.Add(new Forms.ToolStripSeparator());
            Add("Status / roots / processes", async () => ShowText("CODEXish status", await controller!.ControlAsync("status")));
            Add("Pause changes", async () => { await controller!.ControlAsync("pause"); });
            Add("Resume changes", async () => { await controller!.ControlAsync("resume"); });
            Add("Stop session children", async () => { await controller!.ControlAsync("kill-children"); });
            Add("Revoke all tokens", async () => { await controller!.ControlAsync("revoke-tokens"); });
            Add("Edit roots and grants", EditRoots);
            Add("Connection log", () => { ShowText("CODEXish diagnostics", string.Join(Environment.NewLine, controller!.Diagnostics)); return Task.CompletedTask; });
            menu.Items.Add(new Forms.ToolStripSeparator());
            // Checked exactly when the shortcut exists; read again every time the menu opens.
            var startWithWindows = new Forms.ToolStripMenuItem("Start with Windows");
            startWithWindows.Click += async (_, _) => await Guard(() =>
            {
                if (autostart is null) return Task.CompletedTask;
                if (autostart.Enabled) autostart.Disable();
                else autostart.Enable();
                controller?.Note((autostart.Enabled ? "Autostart shortcut written: " : "Autostart shortcut removed: ") + autostart.ShortcutPath);
                return Task.CompletedTask;
            });
            menu.Items.Add(startWithWindows);
            menu.Opening += (_, _) => startWithWindows.Checked = autostart?.Enabled == true;
            Add("Exit", Exit);
            icon = new Forms.NotifyIcon { Icon = Drawing.SystemIcons.Application, Text = "CODEXish — stopped", ContextMenuStrip = menu, Visible = true };
            icon.DoubleClick += async (_, _) => await Guard(async () => ShowText("CODEXish status", await controller!.ControlAsync("status")));
            if (!smoke && autostart is not null && controller is not null)
            {
                try { if (autostart.Heal()) controller.Note("The autostart shortcut named a file that no longer exists; it now starts " + Environment.ProcessPath); }
                catch (Exception error) { controller.Note("Repairing the autostart shortcut failed: " + error.GetType().Name + ": " + error.Message); }
            }
            // Posted to the message loop, so the server and tunnel start once the tray is up.
            if (start && controller is not null) dispatcher.BeginInvoke(new Action(async () => await Guard(controller.StartConfiguredAsync)));
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
                else icon.Text = Tooltip(controller!.Status);
            };
            timer.Start();
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
            if (config is null || controller is null) return Task.CompletedTask;
            using var form = new Forms.Form { Text = "CODEXish roots — stop before applying", Width = 800, Height = 400, StartPosition = Forms.FormStartPosition.CenterScreen };
            var grid = new Forms.DataGridView
            {
                Dock = Forms.DockStyle.Fill, AutoGenerateColumns = true,
                DataSource = new System.ComponentModel.BindingList<RootConfig>(config.Roots.Select(r => new RootConfig { Id = r.Id, Path = r.Path, Read = r.Read, Write = r.Write, Shell = r.Shell }).ToList())
            };
            var apply = new Forms.Button { Text = "Save roots and stop server", Dock = Forms.DockStyle.Bottom, Height = 40 };
            apply.Click += async (_, _) => await Guard(async () =>
            {
                grid.EndEdit();
                var candidate = JsonSerializer.Deserialize<ServerConfig>(JsonSerializer.Serialize(config))!;
                candidate.Roots = ((System.ComponentModel.BindingList<RootConfig>)grid.DataSource).ToArray();
                candidate.Validate();
                await controller.StopAsync();
                candidate.Save(path);
                config.Roots = candidate.Roots;
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
            try { if (controller is not null) await controller.DisposeAsync(); }
            finally { icon.Visible = false; ExitThread(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); icon.Dispose(); dispatcher.Dispose(); }
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
                ShowText("CODEXish connection details — keep private", "MCP endpoint: " + created.PublicUrl + "/mcp" + Environment.NewLine +
                    "Client ID: " + created.OAuth.ClientId + Environment.NewLine + "Client secret: " + created.OAuth.ClientSecret + Environment.NewLine +
                    "Configuration: " + path + Environment.NewLine + "Local port: " + created.Port + Environment.NewLine + signInResult + Environment.NewLine +
                    "Use your new CODEXish password on its login page. This is not your OpenAI password.");
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
