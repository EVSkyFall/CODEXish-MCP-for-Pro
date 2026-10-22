using System.Text;
#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
#endif

namespace Codexish.Server;

// --design-preview <dir>: the tray's windows, menu and icons rendered to PNG at 100% and 150%, light and dark, and the
// sign-in and error pages written as standalone HTML. No window is ever shown: each one is created hidden and printed,
// so it is safe while another CODEXish runs. It opens no server, port, tunnel, state directory or configuration file.
public static class DesignPreview
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var utf8 = new UTF8Encoding(false);
        foreach (var (name, html) in CodexishHost.PreviewPages()) File.WriteAllText(Path.Combine(directory, name), html, utf8);
#if WINDOWS
        int result = 1;
        var thread = new Thread(() =>
        {
            try { result = Windows(directory); }
            catch (Exception error) { Console.Error.WriteLine("DESIGN_PREVIEW_FAILED: " + error); }
        }) { Name = "CODEXish design preview" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
#else
        Console.WriteLine($"DESIGN_PREVIEW: the web pages are in {directory}; window renders need the Windows build.");
        return 0;
#endif
    }
#if WINDOWS
    private static int Windows(string directory)
    {
        DesignNative.UnawareThread();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        int written = 0;
        string secret = ServerConfig.NewSecret();
        foreach (var (mode, theme) in new[] { (SystemColorMode.Classic, "light"), (SystemColorMode.Dark, "dark") })
        {
            Application.SetColorMode(mode);
            foreach (int dpi in new[] { 96, 144 })
            {
                var ui = new Ui(dpi, Palette.Current);
                string suffix = $"{theme}-{dpi * 100 / 96}";
                void Window(string name, string title, Action<DesignForm> build)
                {
                    using var form = DesignForm.ForPreview(title, ui);
                    build(form);
                    using var content = form.Render();
                    using var framed = Frame(content, title, ui);
                    framed.Save(Path.Combine(directory, $"{name}-{suffix}.png"), ImageFormat.Png);
                    written++;
                }
                Window("setup", "CODEXish first-run setup", form => TrayWindows.Setup(form, SampleSetup));
                Window("setup-error", "CODEXish first-run setup", form =>
                {
                    var view = TrayWindows.Setup(form, SampleSetup with { Folder = "" });
                    view.ShowError("Enter a new server password.", view.Password);
                    form.Fit(600);
                });
                Window("connection-details", "CODEXish connection details — keep private", form => TrayWindows.Connection(form,
                    new ConnectionDetails("https://desktop.example.ts.net/mcp", "codexish-chatgpt", secret, SampleConfiguration, 3000,
                        @"Start at sign-in: on (C:\Users\you\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\CODEXish.lnk).", true, [])));
                Window("connection-details-no-folder", "CODEXish connection details — keep private", form => TrayWindows.Connection(form,
                    new ConnectionDetails("https://desktop.example.ts.net/mcp", "codexish-chatgpt", secret, SampleConfiguration, 3000,
                        "Start at sign-in: off. Turn it on with Start with Windows in the tray menu.", false,
                        ["No usable root is configured. File, shell and Git tools need one; the desktop tools keep working."])));
                Window("roots", "CODEXish roots and grants", form => TrayWindows.Roots(form,
                [
                    new RootConfig { Id = "project", Path = @"C:\Projects\Example" },
                    new RootConfig { Id = "docs", Path = @"D:\Documents\Specs", Write = false, Shell = false },
                    new RootConfig { Id = "scratch", Path = @"C:\Temp\scratch", Shell = false },
                ]));
                Window("roots-empty", "CODEXish roots and grants", form => TrayWindows.Roots(form, []));
                Window("text-viewer", "CODEXish status", form => TrayWindows.Viewer(form, SampleStatus, null));
                Window("text-viewer-error", "CODEXish operation failed", form => TrayWindows.Viewer(form,
                    "InvalidOperationException: Start the server first.", Tone.Error));
                Menu(directory, ui, suffix);
                written++;
            }
        }
        Application.SetColorMode(SystemColorMode.Classic);
        Icons(directory);
        written++;
        Console.WriteLine($"DESIGN_PREVIEW_PASSED: {written} PNG files and 3 HTML pages in {directory}; no window was shown.");
        return 0;
    }

    private const string SampleConfiguration = @"C:\Users\you\AppData\Local\Codexish\codexish.json";

    private static readonly SetupValues SampleSetup = new("https://desktop.example.ts.net", "3000", @"C:\Projects\Example",
        ServerConfig.DefaultRedirectUri, SampleConfiguration);

    private const string SampleStatus = """
        {
          "paused": false,
          "processes": [
            {
              "process_id": "p_7f3a",
              "command": "dotnet test",
              "root_id": "project",
              "lifetime": "session",
              "state": "running"
            }
          ],
          "roots": [
            { "Id": "project", "Path": "C:\\Projects\\Example", "Read": true, "Write": true, "Shell": true },
            { "Id": "docs", "Path": "D:\\Documents\\Specs", "Read": true, "Write": false, "Shell": false }
          ],
          "state_dir": "C:\\Users\\you\\AppData\\Local\\Codexish",
          "authentication": "oauth_bearer",
          "registered_clients": [
            { "client_name": "ChatGPT", "auth_method": "none", "last_sign_in": "2026-10-22T08:14:03Z" }
          ]
        }
        """;

    // The tray menu as it opens: header with the running badge, every group, Start with Windows on, one item hovered.
    private static void Menu(string directory, Ui ui, string suffix)
    {
        Task Nothing() => Task.CompletedTask;
        var (menu, startWithWindows) = TrayApplication.BuildMenu(new TrayApplication.MenuActions(Nothing, Nothing, Nothing, Nothing, Nothing,
            Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing), action => action());
        using var strip = menu.Strip;
        TrayApplication.ShowAutostart(startWithWindows, true);
        menu.Apply(ui, TrayStatus.Line("server running, tunnel stopped"), TrayLook.Running);
        _ = strip.Handle;
        strip.PerformLayout();
        strip.Items[5].Select();
        int margin = ui.Px(24);
        using var image = new Bitmap(strip.Width, strip.Height, PixelFormat.Format24bppRgb);
        strip.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        using var canvas = new Bitmap(image.Width + 2 * margin, image.Height + 2 * margin, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(ui.Colors.IsDark ? Color.FromArgb(0x10, 0x1A, 0x24) : Color.FromArgb(0xD9, 0xE4, 0xEF));
            graphics.DrawImage(image, margin, margin);
        }
        canvas.Save(Path.Combine(directory, $"tray-menu-{suffix}.png"), ImageFormat.Png);
    }

    // The three tray states and the app icon at the sizes Windows asks for, on a light and a dark taskbar, each also
    // magnified four times without smoothing so the pixels can be checked.
    private static void Icons(string directory)
    {
        int[] sizes = [16, 20, 24, 32, 48];
        var rows = new (string Name, Func<int, Bitmap?> Image)[]
        {
            ("running", size => TrayIcons.Image(TrayLook.Running, size)),
            ("stopped", size => TrayIcons.Image(TrayLook.Stopped, size)),
            ("attention", size => TrayIcons.Image(TrayLook.Attention, size)),
            ("app", size => Assets.Bitmap("codexish.ico", size)),
        };
        const int zoom = 4, gap = 16, label = 96;
        int cell = 48 + gap;
        int panelWidth = label + sizes.Length * cell + gap + 16 * zoom + gap + 24 * zoom + gap;
        int rowHeight = 24 * zoom + gap;
        using var canvas = new Bitmap(panelWidth * 2, rows.Length * rowHeight + gap, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(canvas);
        var font = Typography.Get(TypeRole.Small, 96);
        foreach (var (dark, left) in new[] { (false, 0), (true, panelWidth) })
        {
            var back = dark ? Color.FromArgb(0x1C, 0x1C, 0x1C) : Color.FromArgb(0xEE, 0xEE, 0xEE);
            using (var fill = new SolidBrush(back)) graphics.FillRectangle(fill, left, 0, panelWidth, canvas.Height);
            for (int row = 0; row < rows.Length; row++)
            {
                int top = gap + row * rowHeight;
                TextRenderer.DrawText(graphics, rows[row].Name, font, new Point(left + gap, top), dark ? Color.White : Color.Black, back);
                int x = left + label;
                foreach (int size in sizes)
                {
                    using var image = rows[row].Image(size);
                    if (image is not null) graphics.DrawImage(image, x, top, size, size);
                    x += cell;
                }
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                foreach (int size in new[] { 16, 24 })
                {
                    using var image = rows[row].Image(size);
                    if (image is not null) graphics.DrawImage(image, x, top, size * zoom, size * zoom);
                    x += size * zoom + gap;
                }
                graphics.InterpolationMode = InterpolationMode.Default;
                graphics.PixelOffsetMode = PixelOffsetMode.Default;
            }
        }
        canvas.Save(Path.Combine(directory, "tray-icons.png"), ImageFormat.Png);
    }

    // The client area under a plain title bar with the window's icon and title, so a render reads as the window. The real
    // frame is drawn by Windows and cannot be printed from a hidden window.
    private static Bitmap Frame(Bitmap content, string title, Ui ui)
    {
        var colors = ui.Colors;
        int bar = ui.Px(32);
        var framed = new Bitmap(content.Width + 2, content.Height + bar + 2, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(framed);
        graphics.Clear(colors.IsDark ? Color.FromArgb(0x48, 0x48, 0x48) : Color.FromArgb(0xBF, 0xBF, 0xBF));
        var caption = colors.IsDark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.White;
        using (var fill = new SolidBrush(caption)) graphics.FillRectangle(fill, 1, 1, content.Width, bar);
        int icon = ui.Px(16);
        using (var image = Assets.Bitmap("codexish.ico", icon))
            if (image is not null) graphics.DrawImage(image, 1 + ui.Px(12), 1 + (bar - icon) / 2, icon, icon);
        TextRenderer.DrawText(graphics, title, ui.Font(TypeRole.Small), new Rectangle(1 + ui.Px(40), 1, content.Width - ui.Px(200), bar),
            colors.OnSurface, caption, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        using (var symbols = new Font("Segoe Fluent Icons", 10f * ui.Dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel))
            if (symbols.Name == "Segoe Fluent Icons")
            {
                int width = ui.Px(46);
                string[] glyphs = ["\uE921", "\uE922", "\uE8BB"];
                for (int i = 0; i < glyphs.Length; i++)
                    TextRenderer.DrawText(graphics, glyphs[i], symbols, new Rectangle(1 + content.Width - (glyphs.Length - i) * width, 1, width, bar),
                        colors.OnSurface, caption, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        graphics.DrawImage(content, 1, 1 + bar);
        return framed;
    }
#endif
}
