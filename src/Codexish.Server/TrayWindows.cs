#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Codexish.Server;

// A window on the design grid: a header, a body that scrolls when the screen is too short for it, and a footer of
// buttons. At run time it is built for the DPI of the screen under the cursor and opens centered there; --design-preview
// builds it for a given DPI and never shows it.
internal sealed class DesignForm : Form
{
    private readonly Dictionary<Control, TypeRole> roles = [];
    private readonly TableLayoutPanel root;
    private readonly TableLayoutPanel header;
    private readonly FlowLayoutPanel footer;
    private readonly Screen? screen;
    private TableLayoutPanel? stack;

    private DesignForm(string title, Ui ui, Screen? screen)
    {
        Ui = ui;
        this.screen = screen;
        SuspendLayout();
        if (screen is null) AutoScaleMode = AutoScaleMode.None;
        else
        {
            // Built for this DPI already; Windows Forms rescales only when the window moves to a screen with another one.
            AutoScaleDimensions = new SizeF(ui.Dpi, ui.Dpi);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.Manual;
            Location = screen.WorkingArea.Location;
        }
        Text = title;
        Font = ui.Font(TypeRole.Body);
        BackColor = ui.Colors.Background;
        ForeColor = ui.Colors.OnSurface;
        Icon = Assets.App;
        root = Stack(1, ui.Pad(24, 20, 24, 24));
        root.Dock = DockStyle.Fill;
        root.AutoSize = false;
        root.RowCount = 3;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header = Stack(2, Padding.Empty);
        header.ColumnStyles.Clear();
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Margin = ui.Pad(0, 0, 0, 20);
        footer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, Margin = ui.Pad(0, 24, 0, 0), Padding = Padding.Empty, BackColor = ui.Colors.Background,
        };
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        ResumeLayout(false);
    }

    public Ui Ui { get; private set; }

    public static DesignForm Open(string title)
    {
        var cursor = Cursor.Position;
        return new DesignForm(title, new Ui(DesignNative.DpiAt(cursor, 96), Palette.Current), Screen.FromPoint(cursor));
    }

    public static DesignForm ForPreview(string title, Ui ui) => new(title, ui, null);

    private TableLayoutPanel Stack(int columns, Padding padding)
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = columns, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
            Margin = Padding.Empty, Padding = padding, BackColor = Ui.Colors.Background,
        };
        for (int i = 0; i < columns; i++) panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        return panel;
    }

    public Label Label(string text, TypeRole role, Color? color = null, Color? back = null)
    {
        var label = new TextLabel
        {
            Text = text, AutoSize = true, UseMnemonic = false, Dock = DockStyle.Fill, Margin = Padding.Empty, Font = Ui.Font(role),
            ForeColor = color ?? Ui.Colors.OnSurface, BackColor = back ?? Ui.Colors.Background,
        };
        roles[label] = role;
        return label;
    }

    // Each row after the first in a column keeps a gap above it.
    public static void Append(TableLayoutPanel parent, Control control, int gap)
    {
        control.Margin = new Padding(control.Margin.Left, parent.Controls.Count == 0 ? 0 : gap, control.Margin.Right, control.Margin.Bottom);
        parent.Controls.Add(control);
    }

    public static void ScrollIntoView(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent is ScrollableControl { AutoScroll: true } scroller) { scroller.ScrollControlIntoView(control); return; }
    }

    public void SetHeader(Image? image, string headline, string? supporting, bool compact = false)
    {
        header.Controls.Clear();
        if (image is not null)
        {
            int edge = Ui.Px(compact ? 24 : 40);
            var picture = new PictureBox
            {
                Image = image, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(edge, edge), Margin = Ui.Pad(0, compact ? 0 : 2, 16, 0),
                BackColor = Ui.Colors.Background, AccessibleRole = AccessibleRole.Graphic, AccessibleName = headline,
            };
            header.Controls.Add(picture, 0, 0);
            header.SetRowSpan(picture, 2);
        }
        var title = Label(headline, compact ? TypeRole.Title : TypeRole.Headline);
        header.Controls.Add(title, 1, 0);
        if (supporting is not null)
        {
            var text = Label(supporting, TypeRole.Body, Ui.Colors.OnSurfaceVariant);
            text.Margin = Ui.Pad(0, 4, 0, 0);
            header.Controls.Add(text, 1, 1);
        }
    }

    public TableLayoutPanel ScrollingBody()
    {
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Ui.Colors.Background };
        stack = Stack(1, Padding.Empty);
        stack.Dock = DockStyle.Top;
        scroller.Controls.Add(stack);
        scroller.TabIndex = 1;
        root.Controls.Add(scroller, 0, 1);
        return stack;
    }

    public void FillBody(Control body)
    {
        body.Dock = DockStyle.Fill;
        body.Margin = Padding.Empty;
        body.TabIndex = 1;
        root.Controls.Add(body, 0, 1);
    }

    // Left to right as they appear; the first is the window's main action.
    public void Buttons(params ActionButton[] buttons)
    {
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            buttons[i].Margin = new Padding(i == 0 ? 0 : Ui.Px(8), 0, 0, 0);
            footer.Controls.Add(buttons[i]);
        }
        for (int i = 0; i < buttons.Length; i++) buttons[i].TabIndex = i;
        footer.TabIndex = 2;
    }

    // A section: a short heading above a card that groups its rows.
    public Card Section(TableLayoutPanel parent, string title)
    {
        var heading = Label(title, TypeRole.Strong);
        heading.Margin = Ui.Pad(0, 0, 0, 8);
        Append(parent, heading, Ui.Px(24));
        var card = new Card(Ui);
        parent.Controls.Add(card);
        return card;
    }

    // A labeled field with supporting text below it and, optionally, a control beside it.
    public FieldBox Field(TableLayoutPanel parent, string label, string value, string? support = null, string? placeholder = null,
        Control? beside = null, int width = 0, bool display = false)
    {
        var back = parent.BackColor;
        var group = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = Padding.Empty,
            Padding = Padding.Empty, BackColor = back,
        };
        group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var caption = Label(label, TypeRole.Body, Ui.Colors.OnSurface, back);
        caption.Margin = Ui.Pad(0, 0, 0, 6);
        group.Controls.Add(caption, 0, 0);
        group.SetColumnSpan(caption, 2);
        var field = new FieldBox(Ui, value, display) { Margin = Padding.Empty };
        if (width > 0) { field.Width = Ui.Px(width); field.Anchor = AnchorStyles.Left | AnchorStyles.Top; }
        else field.Dock = DockStyle.Fill;
        field.Input.AccessibleName = label;
        if (support is not null) field.Input.AccessibleDescription = support;
        if (placeholder is not null) field.Input.PlaceholderText = placeholder;
        group.Controls.Add(field, 0, 1);
        if (beside is not null)
        {
            beside.Margin = new Padding(Ui.Px(8), 0, 0, 0);
            beside.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            group.Controls.Add(beside, 1, 1);
        }
        else group.SetColumnSpan(field, 2);
        if (support is not null)
        {
            var help = Label(support, TypeRole.Small, Ui.Colors.OnSurfaceVariant, back);
            help.Margin = Ui.Pad(0, 6, 0, 0);
            group.Controls.Add(help, 0, 2);
            group.SetColumnSpan(help, 2);
        }
        Append(parent, group, Ui.Px(16));
        return field;
    }

    // A value to read and copy: the value in a read-only field and a Copy button beside it.
    public FieldBox Value(TableLayoutPanel parent, string label, string value, bool secret = false)
    {
        var copy = new ActionButton(Ui, "Copy", ButtonLook.Outlined, "copy") { AlternateText = "Not copied", AccessibleName = "Copy " + label };
        var field = Field(parent, label, value, beside: copy, display: true);
        copy.Click += (_, _) => CopyToClipboard(copy, value);
        if (secret)
        {
            field.Input.UseSystemPasswordChar = true;
            var show = field.AddButton("eye", "Show " + label);
            show.Click += (_, _) =>
            {
                bool reveal = field.Input.UseSystemPasswordChar;
                field.Input.UseSystemPasswordChar = !reveal;
                show.Glyph = reveal ? "eye-off" : "eye";
                show.AccessibleName = (reveal ? "Hide " : "Show ") + label;
            };
        }
        return field;
    }

    // The button shows whether the copy worked, then returns to its own text.
    public static void CopyToClipboard(ActionButton button, string text)
    {
        var (restText, restGlyph) = button.Tag is (string t, string g) ? (t, g) : (button.Text, button.Glyph ?? "");
        button.Tag = (restText, restGlyph);
        bool copied;
        try
        {
            System.Windows.Forms.Clipboard.SetDataObject(text, true, 10, 100);
            copied = true;
        }
        catch (Exception error) when (error is ExternalException or ThreadStateException or ArgumentException) { copied = false; }
        button.Text = copied ? "Copied" : "Not copied";
        button.Glyph = copied ? "check" : "circle-alert";
        var timer = new System.Windows.Forms.Timer { Interval = 1600 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            if (button.IsDisposed) return;
            button.Text = restText;
            button.Glyph = restGlyph.Length == 0 ? null : restGlyph;
        };
        timer.Start();
    }

    // As wide as asked and as tall as the content; at run time never larger than the screen, where the body scrolls.
    public void Fit(int width, int height = 0)
    {
        int clientWidth = Ui.Px(width);
        int inner = clientWidth - root.Padding.Horizontal;
        int clientHeight = height > 0 ? Ui.Px(height) : root.Padding.Vertical
            + (header.Controls.Count == 0 ? 0 : header.GetPreferredSize(new Size(inner, 0)).Height + header.Margin.Vertical)
            + (stack?.GetPreferredSize(new Size(inner, 0)).Height ?? 0)
            + footer.GetPreferredSize(new Size(inner, 0)).Height + footer.Margin.Vertical;
        var client = new Size(clientWidth, clientHeight);
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var frame = SizeFromClientSize(Size.Empty);
            client = new Size(Math.Min(client.Width, area.Width - frame.Width - Ui.Px(32)), Math.Min(client.Height, area.Height - frame.Height - Ui.Px(32)));
        }
        ClientSize = client;
        MinimumSize = SizeFromClientSize(new Size(Math.Min(client.Width, Ui.Px(440)), Math.Min(client.Height, Ui.Px(320))));
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Ui = new Ui(e.DeviceDpiNew, Ui.Colors);
        Font = Ui.Font(TypeRole.Body);
        void Walk(Control control)
        {
            if (control is IRescalable scaled) scaled.Rescale(Ui);
            else if (roles.TryGetValue(control, out var role)) control.Font = Ui.Font(role);
            foreach (Control child in control.Controls) Walk(child);
        }
        Walk(this);
    }

    // --design-preview: every handle is created while the window stays hidden, then its content is printed.
    public Bitmap Render()
    {
        static void Create(Control control)
        {
            _ = control.Handle;
            foreach (Control child in control.Controls) Create(child);
        }
        Create(this);
        PerformLayout();
        var bitmap = new Bitmap(root.Width, root.Height, PixelFormat.Format24bppRgb);
        root.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }
}

internal sealed record SetupValues(string PublicUrl, string Port, string Folder, string Callback, string ConfigurationPath);

internal sealed record SetupView(FieldBox Url, FieldBox Port, FieldBox Password, FieldBox Callback, FieldBox Folder, CheckField SignIn,
    Banner Error, ActionButton Create, DesignForm Form)
{
    public void ShowError(string message, FieldBox? field)
    {
        Error.SetText("Configuration not saved", [message]);
        Error.Visible = true;
        if (!Form.Visible) return;
        DesignForm.ScrollIntoView(Error);
        (field?.Input ?? (Control)Create).Focus();
    }
}

internal sealed record ConnectionDetails(string Endpoint, string ClientId, string ClientSecret, string ConfigurationPath, int Port,
    string StartAtSignIn, bool FolderShared, IReadOnlyList<string> Warnings);

internal sealed class RootsView
{
    public Func<RootConfig[], Task>? Save { get; set; }
}

internal static class TrayWindows
{
    public static SetupView Setup(DesignForm form, SetupValues values)
    {
        var ui = form.Ui;
        form.SetHeader(Assets.Bitmap("codexish.ico", ui.Px(40)), "Set up CODEXish",
            "ChatGPT reaches this PC through your tunnel. These settings are saved to " + values.ConfigurationPath + ".");
        var body = form.ScrollingBody();
        var error = new Banner(ui, Tone.Error, "Configuration not saved", []) { Visible = false };
        error.Margin = ui.Pad(0, 0, 0, 0);
        body.Controls.Add(error);

        var connection = form.Section(body, "Connection");
        var url = form.Field(connection, "Public HTTPS origin", values.PublicUrl,
            "The https address your tunnel publishes, for example https://pc.example.ts.net.", "https://your-tunnel-host");
        var port = form.Field(connection, "Local port", values.Port, "CODEXish listens on this port on this PC only.", width: 120);
        var password = form.Field(connection, "New CODEXish password", "",
            "You type it on the CODEXish sign-in page. It is not your OpenAI password.");
        password.Input.UseSystemPasswordChar = true;
        var reveal = password.AddButton("eye", "Show password");
        reveal.Click += (_, _) =>
        {
            bool show = password.Input.UseSystemPasswordChar;
            password.Input.UseSystemPasswordChar = !show;
            reveal.Glyph = show ? "eye-off" : "eye";
            reveal.AccessibleName = show ? "Hide password" : "Show password";
        };
        var callback = form.Field(connection, "OAuth callback", values.Callback,
            "Any https callback is accepted. List one here only when it is not https.");

        var sharing = form.Section(body, "Sharing");
        var browse = new ActionButton(ui, "Browse…", ButtonLook.Outlined) { AccessibleName = "Browse for the project folder" };
        var folder = form.Field(sharing, "Project folder", values.Folder,
            "ChatGPT can read, write and run commands in this folder. Leave it empty to share nothing yet and add folders later " +
            "from the tray menu: Edit roots and grants.", "Leave empty to share no folder yet", beside: browse);
        browse.Click += (_, _) => PickFolder(form, folder.Input, "Choose the project folder ChatGPT may use");

        var signIn = new CheckField(ui, "Start CODEXish when I sign in to Windows", true) { BackColor = ui.Colors.Background };
        DesignForm.Append(body, signIn, ui.Px(18));

        var create = new ActionButton(ui, "Create configuration", ButtonLook.Filled);
        var cancel = new ActionButton(ui, "Cancel", ButtonLook.Outlined) { DialogResult = DialogResult.Cancel };
        form.Buttons(create, cancel);
        form.AcceptButton = create;
        form.CancelButton = cancel;
        form.Fit(600);
        return new SetupView(url, port, password, callback, folder, signIn, error, create, form);
    }

    public static void Connection(DesignForm form, ConnectionDetails details)
    {
        var ui = form.Ui;
        form.SetHeader(ui.Glyph("circle-check", ui.Colors.Success, 40), "CODEXish is set up", "Keep these details private.");
        var body = form.ScrollingBody();
        body.Controls.Add(new Banner(ui, Tone.Info, null,
            ["ChatGPT registers itself automatically (Create MCP App → OAuth). The client ID and secret are only for apps that ask for them."]));

        var connect = form.Section(body, "Connect");
        form.Value(connect, "MCP endpoint", details.Endpoint);
        form.Value(connect, "Client ID", details.ClientId);
        form.Value(connect, "Client secret", details.ClientSecret, secret: true);

        var computer = form.Section(body, "This PC");
        form.Value(computer, "Configuration", details.ConfigurationPath);
        form.Value(computer, "Local port", details.Port.ToString(CultureInfo.InvariantCulture));
        DesignForm.Append(computer, form.Label(details.StartAtSignIn, TypeRole.Body, ui.Colors.OnSurface, ui.Colors.Surface), ui.Px(16));
        DesignForm.Append(computer, form.Label("Sign in with your new CODEXish password. It is not your OpenAI password.",
            TypeRole.Body, ui.Colors.OnSurfaceVariant, ui.Colors.Surface), ui.Px(8));

        if (!details.FolderShared)
            DesignForm.Append(body, new Banner(ui, Tone.Info, "No folder is shared yet",
                ["To share one, open the CODEXish tray menu and choose Edit roots and grants."]), ui.Px(24));
        if (details.Warnings.Count > 0)
            DesignForm.Append(body, new Banner(ui, Tone.Warning, "Warnings", details.Warnings), ui.Px(details.FolderShared ? 24 : 12));

        var close = new ActionButton(ui, "Close", ButtonLook.Filled);
        close.Click += (_, _) => form.Close();
        form.Buttons(close);
        form.AcceptButton = close;
        form.CancelButton = close;
        form.Fit(640);
    }

    public static RootsView Roots(DesignForm form, IEnumerable<RootConfig> roots)
    {
        var ui = form.Ui;
        var view = new RootsView();
        form.SetHeader(ui.Glyph("folder-cog", ui.Colors.Primary, 32), "Roots and grants",
            "Folders ChatGPT may use. Read lets it open files, Write lets it change them, and Shell lets it run commands there.");
        var body = form.ScrollingBody();
        var card = new Card(ui);
        body.Controls.Add(card);
        var table = new TableLayoutPanel { ColumnCount = 7, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = ui.Colors.Surface };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ui.Px(152)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < 3; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ui.Px(56)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.Controls.Add(table);
        var add = new ActionButton(ui, "Add root", ButtonLook.Text, "plus");
        DesignForm.Append(card, add, ui.Px(8));
        DesignForm.Append(body, new Banner(ui, Tone.Info, null,
            ["Saving restarts the server for a moment so the new folders take effect. ChatGPT stays signed in."]), ui.Px(16));

        var rows = new List<RootRow>();
        var headings = new[] { "ID", "Folder", "", "Read", "Write", "Shell", "" }.Select((text, column) =>
        {
            var heading = form.Label(text, TypeRole.Small, ui.Colors.OnSurfaceVariant, ui.Colors.Surface);
            heading.Margin = ui.Pad(column == 0 ? 0 : 8, 0, 0, 6);
            heading.TextAlign = column is >= 3 and <= 5 ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
            return heading;
        }).ToArray();
        var empty = form.Label("No folder is shared. Add a root to share one with ChatGPT.", TypeRole.Body, ui.Colors.OnSurfaceVariant, ui.Colors.Surface);
        empty.Margin = ui.Pad(0, 4, 0, 4);
        form.Disposed += (_, _) => { foreach (var label in headings.Append(empty)) label.Dispose(); };
        void Layout()
        {
            table.SuspendLayout();
            table.Controls.Clear();
            table.RowStyles.Clear();
            if (rows.Count == 0)
            {
                table.Controls.Add(empty, 0, 0);
                table.SetColumnSpan(empty, 7);
            }
            else
                for (int column = 0; column < headings.Length; column++) table.Controls.Add(headings[column], column, 0);
            for (int i = 0; i < rows.Count; i++) rows[i].Place(table, i + 1);
            table.ResumeLayout(true);
        }
        RootRow Row(RootConfig root)
        {
            RootRow? row = null;
            row = new RootRow(form, root, () =>
            {
                int at = rows.IndexOf(row!);
                rows.Remove(row!);
                row!.Dispose();
                Layout();
                Control next = rows.Count == 0 ? add : rows[Math.Min(at, rows.Count - 1)].Remove;
                next.Focus();
            }, () => rows.Where(r => r != row).Select(r => r.Id.Input.Text.Trim()));
            return row;
        }
        foreach (var root in roots) rows.Add(Row(root));
        Layout();
        add.Click += (_, _) =>
        {
            var row = Row(new RootConfig());
            rows.Add(row);
            Layout();
            row.Id.Input.Focus();
            DesignForm.ScrollIntoView(row.Id);
        };

        var save = new ActionButton(ui, "Save and restart", ButtonLook.Filled);
        var cancel = new ActionButton(ui, "Cancel", ButtonLook.Outlined) { DialogResult = DialogResult.Cancel };
        // Enter does not save here: saving stops the server, so it takes the button itself.
        form.CancelButton = cancel;
        save.Click += async (_, _) =>
        {
            if (view.Save is { } handler) await handler(rows.Select(r => r.ToConfig()).OfType<RootConfig>().ToArray());
        };
        form.Buttons(save, cancel);
        form.Fit(760);
        return view;
    }

    public static void Viewer(DesignForm form, string text, Tone? tone)
    {
        var ui = form.Ui;
        var image = tone switch
        {
            Tone.Error => ui.Glyph("circle-alert", ui.Colors.Error, 24),
            Tone.Warning => ui.Glyph("triangle-alert", ui.Colors.Warning, 24),
            Tone.Success => ui.Glyph("circle-check", ui.Colors.Success, 24),
            _ => null,
        };
        form.SetHeader(image, form.Text, null, compact: true);
        var card = new Card(ui) { AutoSize = false };
        card.Padding = ui.Pad(12, 8, 4, 4);
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill, Margin = Padding.Empty, Text = text.ReplaceLineEndings("\r\n"), Font = ui.Font(TypeRole.Code),
            BackColor = ui.Colors.Surface, ForeColor = ui.Colors.OnSurface, AccessibleName = form.Text,
        };
        card.Controls.Add(box);
        form.FillBody(card);
        var copy = new ActionButton(ui, "Copy all", ButtonLook.Outlined, "copy") { AlternateText = "Not copied" };
        copy.Click += (_, _) => DesignForm.CopyToClipboard(copy, box.Text);
        var close = new ActionButton(ui, "Close", ButtonLook.Filled);
        close.Click += (_, _) => form.Close();
        form.Buttons(copy, close);
        form.AcceptButton = close;
        form.CancelButton = close;
        form.Shown += (_, _) => { box.SelectionStart = 0; box.SelectionLength = 0; };
        // Sized to the text, between a short message and a long log, which scrolls.
        var code = Typography.Get(TypeRole.Code, 96);
        string[] lines = box.Text.Split("\r\n");
        int longest = lines.Length == 0 ? 0 : lines.Max(line => line.Length);
        int character = TextRenderer.MeasureText("0000000000", code, Size.Empty, TextFormatFlags.NoPadding).Width / 10;
        int lineHeight = TextRenderer.MeasureText("Ag", code, Size.Empty, TextFormatFlags.NoPadding).Height;
        form.Fit(Math.Clamp(longest * character + 120, 560, 860), Math.Clamp(lines.Length * lineHeight + 196, 280, 560));
    }

    public static void PickFolder(IWin32Window owner, TextBox target, string description)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        string current = target.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(owner) == DialogResult.OK) target.Text = dialog.SelectedPath;
    }

    private sealed class RootRow : IDisposable
    {
        private readonly CheckField read, write, shell;
        private readonly ActionButton browse;

        public RootRow(DesignForm form, RootConfig root, Action remove, Func<IEnumerable<string>> otherIds)
        {
            var ui = form.Ui;
            Id = new FieldBox(ui, root.Id) { Dock = DockStyle.Fill, Margin = ui.Pad(0, 4, 0, 4) };
            Id.Input.PlaceholderText = "project";
            Folder = new FieldBox(ui, root.Path) { Dock = DockStyle.Fill, Margin = ui.Pad(8, 4, 0, 4) };
            Folder.Input.PlaceholderText = @"C:\Projects\Example";
            browse = new ActionButton(ui, "Browse…", ButtonLook.Outlined) { Anchor = AnchorStyles.Left, Margin = ui.Pad(8, 4, 0, 4) };
            browse.Click += (_, _) =>
            {
                TrayWindows.PickFolder(form, Folder.Input, "Choose a folder ChatGPT may use");
                if (Id.Input.Text.Trim().Length == 0 && Folder.Input.Text.Trim().Length > 0) Id.Input.Text = TrayOptions.SuggestRootId(Folder.Input.Text, otherIds());
            };
            CheckField Grant(bool value) => new(ui, "", value) { Anchor = AnchorStyles.None, Margin = ui.Pad(8, 2, 0, 2), BackColor = ui.Colors.Surface };
            read = Grant(root.Read);
            write = Grant(root.Write);
            shell = Grant(root.Shell);
            Remove = new ActionButton(ui, "", ButtonLook.Text, "trash-2") { Quiet = true, Anchor = AnchorStyles.Left, Margin = ui.Pad(8, 4, 0, 4) };
            Remove.Click += (_, _) => remove();
        }

        public FieldBox Id { get; }
        public FieldBox Folder { get; }
        public ActionButton Remove { get; }

        public void Place(TableLayoutPanel table, int row)
        {
            string name = $"root {row}";
            Id.Input.AccessibleName = $"ID, {name}";
            Folder.Input.AccessibleName = $"Folder, {name}";
            browse.AccessibleName = $"Browse for the folder of {name}";
            read.AccessibleName = $"Read, {name}";
            write.AccessibleName = $"Write, {name}";
            shell.AccessibleName = $"Shell, {name}";
            Remove.AccessibleName = $"Remove {name}";
            Control[] cells = [Id, Folder, browse, read, write, shell, Remove];
            for (int column = 0; column < cells.Length; column++) table.Controls.Add(cells[column], column, row);
        }

        // A row left completely empty is not a root.
        public RootConfig? ToConfig()
        {
            string id = Id.Input.Text.Trim(), path = Folder.Input.Text.Trim();
            return id.Length == 0 && path.Length == 0 ? null
                : new RootConfig { Id = id, Path = path, Read = read.Checked, Write = write.Checked, Shell = shell.Checked };
        }

        public void Dispose()
        {
            foreach (Control control in new Control[] { Id, Folder, browse, read, write, shell, Remove }) control.Dispose();
        }
    }
}
#endif
