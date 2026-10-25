#if WINDOWS
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Codexish.Server;

// Material 3 color roles valued from Windows 11's own light and dark palettes, so the windows read as native: one
// accent (Windows' default blue), neutral surfaces, and status colors only where a status is shown.
internal sealed record Palette(
    bool IsDark, Color Background, Color Surface, Color SurfaceStroke, Color OnSurface, Color OnSurfaceVariant,
    Color Field, Color FieldStroke, Color FieldUnderline, Color Primary, Color OnPrimary, Color PrimaryContainer,
    Color OnPrimaryContainer, Color Error, Color ErrorContainer, Color Warning, Color WarningContainer, Color Success,
    Color Menu, Color MenuStroke, Color MenuHover)
{
    public static readonly Palette Light = new(false,
        Background: Rgb(0xF3F3F3), Surface: Rgb(0xFBFBFB), SurfaceStroke: Rgb(0xE3E3E3), OnSurface: Rgb(0x1A1A1A),
        OnSurfaceVariant: Rgb(0x5D5D5D), Field: Rgb(0xFFFFFF), FieldStroke: Rgb(0xD4D4D4), FieldUnderline: Rgb(0x868686),
        Primary: Rgb(0x005FB8), OnPrimary: Rgb(0xFFFFFF), PrimaryContainer: Rgb(0xE5EFF9), OnPrimaryContainer: Rgb(0x0A3A66),
        Error: Rgb(0xC42B1C), ErrorContainer: Rgb(0xFDE7E9), Warning: Rgb(0x9D5D00), WarningContainer: Rgb(0xFFF4CE),
        Success: Rgb(0x0F7B0F), Menu: Rgb(0xF9F9F9), MenuStroke: Rgb(0xD5D5D5), MenuHover: Rgb(0xEAEAEA));

    public static readonly Palette Dark = new(true,
        Background: Rgb(0x202020), Surface: Rgb(0x2B2B2B), SurfaceStroke: Rgb(0x383838), OnSurface: Rgb(0xFFFFFF),
        OnSurfaceVariant: Rgb(0xC8C8C8), Field: Rgb(0x333333), FieldStroke: Rgb(0x424242), FieldUnderline: Rgb(0x9A9A9A),
        Primary: Rgb(0x60CDFF), OnPrimary: Rgb(0x000000), PrimaryContainer: Rgb(0x1B3850), OnPrimaryContainer: Rgb(0xCDE7FF),
        Error: Rgb(0xFF99A4), ErrorContainer: Rgb(0x442726), Warning: Rgb(0xFCE100), WarningContainer: Rgb(0x433519),
        Success: Rgb(0x6CCB5F), Menu: Rgb(0x2C2C2C), MenuStroke: Rgb(0x454545), MenuHover: Rgb(0x3A3A3A));

    // Windows Forms follows the system's app mode once the tray sets SystemColorMode.System.
    public static Palette Current => Application.IsDarkModeEnabled ? Dark : Light;

    // An M3 state layer: over drawn on under at the given opacity, flattened to an opaque color.
    public static Color Layer(Color under, Color over, float opacity) => Color.FromArgb(
        (int)MathF.Round(under.R + (over.R - under.R) * opacity),
        (int)MathF.Round(under.G + (over.G - under.G) * opacity),
        (int)MathF.Round(under.B + (over.B - under.B) * opacity));

    private static Color Rgb(int value) => Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
}

internal enum TypeRole { Headline, Title, Body, Strong, Small, Code }

// Material 3's type scale on Windows 11's ramp, in px at 100%: headline small 24 (Segoe UI Variable Display Semibold),
// title medium 16 and label large 14 (Text Semibold), body medium 14, body small 12; code 13 in Cascadia Mono. Each face
// falls back to Segoe UI (or Consolas), then to the system's dialog font.
internal static class Typography
{
    private static readonly Dictionary<(TypeRole, int), Font> Fonts = [];
    private static readonly Lazy<(string, FontStyle)> DisplayFace = new(() => Pick(
        ("Segoe UI Variable Display Semibold", FontStyle.Regular), ("Segoe UI Semibold", FontStyle.Regular), ("Segoe UI", FontStyle.Bold)));
    private static readonly Lazy<(string, FontStyle)> StrongFace = new(() => Pick(
        ("Segoe UI Variable Text Semibold", FontStyle.Regular), ("Segoe UI Semibold", FontStyle.Regular), ("Segoe UI", FontStyle.Bold)));
    private static readonly Lazy<(string, FontStyle)> TextFace = new(() => Pick(
        ("Segoe UI Variable Text", FontStyle.Regular), ("Segoe UI", FontStyle.Regular)));
    private static readonly Lazy<(string, FontStyle)> CodeFace = new(() => Pick(
        ("Cascadia Mono", FontStyle.Regular), ("Consolas", FontStyle.Regular)));

    public static Font Get(TypeRole role, int dpi)
    {
        lock (Fonts)
        {
            if (Fonts.TryGetValue((role, dpi), out var font)) return font;
            var ((name, style), size) = role switch
            {
                TypeRole.Headline => (DisplayFace.Value, 24f),
                TypeRole.Title => (StrongFace.Value, 16f),
                TypeRole.Strong => (StrongFace.Value, 14f),
                TypeRole.Small => (TextFace.Value, 12f),
                TypeRole.Code => (CodeFace.Value, 13f),
                _ => (TextFace.Value, 14f),
            };
            font = new Font(name, size * dpi / 96f, style, GraphicsUnit.Pixel);
            Fonts[(role, dpi)] = font;
            return font;
        }
    }

    // GDI keeps at most 31 characters of a face name, so a longer name is recognized by its first 31.
    private static (string, FontStyle) Pick(params (string Name, FontStyle Style)[] candidates)
    {
        foreach (var candidate in candidates)
        {
            using var probe = new Font(candidate.Name, 12f, candidate.Style, GraphicsUnit.Pixel);
            string expected = candidate.Name.Length > 31 ? candidate.Name[..31] : candidate.Name;
            if (string.Equals(probe.Name, expected, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return (SystemFonts.MessageBoxFont?.FontFamily.Name ?? FontFamily.GenericSansSerif.Name, candidates[^1].Style);
    }
}

// Metrics for one DPI: everything is laid out on an 8 px grid (4 px for tight pairs) and scaled here.
internal sealed class Ui(int dpi, Palette colors)
{
    public int Dpi { get; } = dpi;
    public Palette Colors { get; } = colors;
    public int Px(float logical) => (int)MathF.Round(logical * Dpi / 96f);
    public Padding Pad(float all) => new(Px(all));
    public Padding Pad(float horizontal, float vertical) => new(Px(horizontal), Px(vertical), Px(horizontal), Px(vertical));
    public Padding Pad(float left, float top, float right, float bottom) => new(Px(left), Px(top), Px(right), Px(bottom));
    public Font Font(TypeRole role) => Typography.Get(role, Dpi);
    public Bitmap? Glyph(string name, Color color, float logical = 16) => Glyphs.Get(name, Px(logical), color);
}

internal static class Assets
{
    public static Stream? Open(string name) => typeof(Assets).Assembly.GetManifestResourceStream("Codexish.Assets." + name);

    // A multi-size icon kept whole, so Windows picks each size it needs; null when the resource is missing or unreadable.
    public static Icon? Icon(string name, int? size = null)
    {
        try
        {
            using var stream = Open(name);
            if (stream is null) return null;
            return size is int edge ? new Icon(stream, edge, edge) : new Icon(stream);
        }
        catch (Exception error) when (error is ArgumentException or IOException or ExternalException) { return null; }
    }

    public static Bitmap? Bitmap(string name, int size)
    {
        using var icon = Icon(name, size);
        return icon?.ToBitmap();
    }

    private static Icon? app;
    public static Icon App => app ??= Icon("codexish.ico") ?? SystemIcons.Application;
}

internal static class TrayIcons
{
    private static readonly Dictionary<(TrayLook, int), Icon> Loaded = [];

    public static Icon For(TrayLook look, int size)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue((look, size), out var icon)) return icon;
            icon = Assets.Icon("tray-" + look.ToString().ToLowerInvariant() + ".ico", size) ?? Assets.App;
            Loaded[(look, size)] = icon;
            return icon;
        }
    }

    public static Bitmap? Image(TrayLook look, int size)
    {
        try { return For(look, size).ToBitmap(); }
        catch (Exception error) when (error is ArgumentException or ExternalException) { return null; }
    }
}

// Lucide icons pre-rasterized by assets/build-icons.ps1 into one black sprite sheet per size. Each is cut out, scaled
// when no sheet has its size, and tinted on first use. A missing resource only means no icon.
internal static class Glyphs
{
    private static readonly int[] SheetSizes = [16, 20, 24, 32, 48];
    private static readonly Dictionary<int, Bitmap?> Sheets = [];
    private static readonly Dictionary<(string, int, int), Bitmap?> Tinted = [];
    private static string[]? names;

    public static Bitmap? Get(string name, int size, Color color)
    {
        lock (Tinted)
        {
            var key = (name, size, color.ToArgb());
            if (Tinted.TryGetValue(key, out var cached)) return cached;
            Bitmap? result = null;
            int index = Array.IndexOf(Names(), name);
            int sheetSize = SheetSizes.FirstOrDefault(s => s >= size, SheetSizes[^1]);
            if (index >= 0 && size > 0 && Sheet(sheetSize) is { } sheet && (index + 1) * sheetSize <= sheet.Width)
            {
                using var cell = sheet.Clone(new Rectangle(index * sheetSize, 0, sheetSize, sheetSize), PixelFormat.Format32bppArgb);
                result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(result);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix(new float[][]
                {
                    [0, 0, 0, 0, 0], [0, 0, 0, 0, 0], [0, 0, 0, 0, 0], [0, 0, 0, color.A / 255f, 0],
                    [color.R / 255f, color.G / 255f, color.B / 255f, 0, 1],
                }));
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                graphics.DrawImage(cell, new Rectangle(0, 0, size, size), 0, 0, sheetSize, sheetSize, GraphicsUnit.Pixel, attributes);
            }
            Tinted[key] = result;
            return result;
        }
    }

    private static string[] Names()
    {
        if (names is not null) return names;
        try
        {
            using var stream = Assets.Open("glyphs.txt");
            names = stream is null ? [] : new StreamReader(stream).ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (IOException) { names = []; }
        return names;
    }

    private static Bitmap? Sheet(int size)
    {
        if (Sheets.TryGetValue(size, out var sheet)) return sheet;
        try
        {
            using var stream = Assets.Open($"glyphs-{size}.png");
            if (stream is not null)
            {
                using var loaded = new Bitmap(stream);
                sheet = new Bitmap(loaded);
            }
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or IOException) { sheet = null; }
        Sheets[size] = sheet;
        return sheet;
    }
}

internal static class DesignNative
{
    // Windows 11 rounds a popup that asks for it; elsewhere the call fails and the popup stays square.
    public static void RoundCorners(nint window)
    {
        try
        {
            int round = 3; // DWMWCP_ROUNDSMALL
            _ = DwmSetWindowAttribute(window, 33, ref round, sizeof(int));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    public static int DpiAt(Point point, int fallback)
    {
        try
        {
            nint monitor = MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, 2); // MONITOR_DEFAULTTONEAREST
            return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0 ? (int)dpi : fallback;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return fallback; }
    }

    // --design-preview: windows made on this thread see 96 DPI, so each render has exactly the scale it asks for.
    public static void UnawareThread()
    {
        try { _ = SetThreadDpiAwarenessContext(-1); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private struct NativePoint { public int X, Y; }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}

internal static class Shapes
{
    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color BackgroundOf(Control control, Palette colors) => control.Parent?.BackColor ?? colors.Background;
}

// Controls whose metrics come from a Ui, so a window that moves to a screen with another DPI can rescale them.
internal interface IRescalable
{
    void Rescale(Ui ui);
}

internal enum ButtonLook { Filled, Tonal, Outlined, Text }

// A Windows button drawn with M3 states: hover and press layers, a disabled tone, and a focus ring for the keyboard.
// It stays a Button, so Accept/Cancel, keyboard activation and its accessible role are unchanged.
internal sealed class ActionButton : Button, IRescalable
{
    private bool hovered, pressed;
    private string? glyph;

    public ActionButton(Ui ui, string text, ButtonLook look, string? glyph = null)
    {
        Ui = ui;
        Look = look;
        this.glyph = glyph;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        UseMnemonic = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Text = text;
        Rescale(ui);
    }

    public Ui Ui { get; private set; }
    public ButtonLook Look { get; }
    // The content color of an icon button inside a field, where the accent would be too loud.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Quiet { get; init; }
    // Text this button may later show, so its width does not jump when it does.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? AlternateText { get; init; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Glyph
    {
        get => glyph;
        set { glyph = value; Invalidate(); }
    }

    public void Rescale(Ui ui)
    {
        Ui = ui;
        Font = ui.Font(TypeRole.Body);
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int height = Ui.Px(32);
        if (Text.Length == 0) return new Size(height, height);
        int text = Math.Max(Measure(Text), AlternateText is null ? 0 : Measure(AlternateText));
        int icon = glyph is null ? 0 : Ui.Px(16) + Ui.Px(8);
        int width = text + icon + 2 * Ui.Px(Look == ButtonLook.Text ? 8 : 16);
        return new Size(Math.Max(width, Look == ButtonLook.Text ? height : Ui.Px(96)), height);
    }

    private int Measure(string value) =>
        TextRenderer.MeasureText(value, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var colors = Ui.Colors;
        var graphics = e.Graphics;
        Color back = Shapes.BackgroundOf(this, colors);
        graphics.Clear(back);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var (container, content, stroke) = Look switch
        {
            ButtonLook.Filled => (colors.Primary, colors.OnPrimary, Color.Empty),
            ButtonLook.Tonal => (colors.PrimaryContainer, colors.OnPrimaryContainer, Color.Empty),
            ButtonLook.Outlined => (colors.Field, colors.OnSurface, colors.FieldStroke),
            _ => (back, Quiet ? colors.OnSurfaceVariant : colors.Primary, Color.Empty),
        };
        if (!Enabled)
        {
            content = Palette.Layer(back, colors.OnSurface, 0.38f);
            if (Look is ButtonLook.Filled or ButtonLook.Tonal) container = Palette.Layer(back, colors.OnSurface, 0.12f);
        }
        else if (pressed || hovered)
            container = Palette.Layer(container, Look is ButtonLook.Filled or ButtonLook.Tonal ? content : colors.OnSurface, pressed ? 0.12f : 0.07f);
        float radius = Ui.Px(4);
        var shape = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = Shapes.Rounded(shape, radius))
        {
            if (container != back)
                using (var fill = new SolidBrush(container)) graphics.FillPath(fill, path);
            if (stroke != Color.Empty)
                using (var pen = new Pen(stroke)) graphics.DrawPath(pen, path);
        }
        int iconSize = Ui.Px(16);
        Bitmap? image = glyph is null ? null : Glyphs.Get(glyph, iconSize, content);
        int textWidth = Text.Length == 0 ? 0 : Measure(Text);
        int gap = image is not null && textWidth > 0 ? Ui.Px(8) : 0;
        int total = (image is null ? 0 : iconSize) + gap + textWidth;
        int x = (Width - total) / 2;
        if (image is not null)
        {
            graphics.DrawImage(image, x, (Height - iconSize) / 2, iconSize, iconSize);
            x += iconSize + gap;
        }
        if (textWidth > 0)
            TextRenderer.DrawText(graphics, Text, Font, new Rectangle(x, 0, Width - x, Height), content, container,
                TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(colors.OnSurface, Ui.Px(2));
            using var path = Shapes.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), radius);
            graphics.DrawPath(ring, path);
        }
    }
}

// A single-line text field drawn like Windows 11's: a rounded box with a bottom line that turns into the accent while
// it has focus. Icon buttons can sit at its end (show/hide).
internal sealed class FieldBox : Panel, IRescalable
{
    private readonly List<ActionButton> trailing = [];
    private bool hovered;

    public FieldBox(Ui ui, string text, bool display = false)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Ui = ui;
        Display = display;
        Input = new TextBox { BorderStyle = BorderStyle.None, Text = text, ReadOnly = display, Margin = Padding.Empty, AutoSize = false };
        Input.GotFocus += (_, _) => Invalidate();
        Input.LostFocus += (_, _) => Invalidate();
        Input.MouseEnter += (_, _) => Hover(true);
        Input.MouseLeave += (_, _) => Hover(ClientRectangle.Contains(PointToClient(Cursor.Position)));
        Controls.Add(Input);
        Rescale(ui);
    }

    public TextBox Input { get; }
    public Ui Ui { get; private set; }
    // A read-only value: no accent line, a quieter fill.
    public bool Display { get; }

    private Color Fill => Display ? Palette.Layer(Ui.Colors.Field, Ui.Colors.OnSurface, Ui.Colors.IsDark ? 0.02f : 0.025f)
        : hovered && !Input.Focused ? Palette.Layer(Ui.Colors.Field, Ui.Colors.OnSurface, 0.03f) : Ui.Colors.Field;

    public void Rescale(Ui ui)
    {
        Ui = ui;
        Input.Font = ui.Font(Display ? TypeRole.Code : TypeRole.Body);
        // The edit control lays text out with GDI's line height, which can exceed the Font's own (Cascadia Mono's does).
        Input.Height = TextRenderer.MeasureText("Ag", Input.Font, Size.Empty, TextFormatFlags.NoPadding).Height;
        Input.ForeColor = ui.Colors.OnSurface;
        Input.BackColor = Fill;
        BackColor = Fill;
        Height = PreferredHeight();
        foreach (var button in trailing) button.Rescale(ui);
        PerformLayout();
        Invalidate();
    }

    public ActionButton AddButton(string glyph, string accessibleName)
    {
        var button = new ActionButton(Ui, "", ButtonLook.Text, glyph) { Quiet = true, AccessibleName = accessibleName, AutoSize = false, Margin = Padding.Empty };
        trailing.Add(button);
        Controls.Add(button);
        PerformLayout();
        return button;
    }

    private int PreferredHeight() => Math.Max(Ui.Px(32), Input.Height + Ui.Px(12));

    public override Size GetPreferredSize(Size proposedSize) => new(Math.Max(proposedSize.Width, Ui.Px(120)), PreferredHeight());

    private void Hover(bool value)
    {
        if (hovered == value) return;
        hovered = value;
        Input.BackColor = Fill;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { Hover(true); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover(ClientRectangle.Contains(PointToClient(Cursor.Position))); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { Input.Focus(); base.OnClick(e); }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int inset = Ui.Px(2);
        int right = Width - inset;
        for (int i = trailing.Count - 1; i >= 0; i--)
        {
            var button = trailing[i];
            int edge = Height - 2 * Ui.Px(4);
            button.Size = new Size(edge + Ui.Px(4), edge);
            right -= button.Width;
            button.Location = new Point(right, (Height - edge) / 2);
            right -= inset;
        }
        int left = Ui.Px(11);
        Input.Location = new Point(left, (Height - Input.Height) / 2);
        Input.Width = Math.Max(0, right - left - (trailing.Count == 0 ? Ui.Px(9) : 0));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var colors = Ui.Colors;
        var graphics = e.Graphics;
        graphics.Clear(Shapes.BackgroundOf(this, colors));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shapes.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Ui.Px(4));
        using (var fill = new SolidBrush(Fill)) graphics.FillPath(fill, path);
        using (var pen = new Pen(colors.FieldStroke)) graphics.DrawPath(pen, path);
        if (Display) return;
        bool focused = Input.Focused;
        int line = focused ? Ui.Px(2) : 1;
        using var outline = Shapes.Rounded(new RectangleF(0, 0, Width, Height), Ui.Px(4));
        graphics.SetClip(outline);
        using (var under = new SolidBrush(focused ? colors.Primary : colors.FieldUnderline))
            graphics.FillRectangle(under, 0, Height - line, Width, line);
        graphics.ResetClip();
    }
}

// A label whose text starts exactly at its edge, without GDI's side padding, so captions line up with the fields and
// cards below them. It wraps to the width its layout gives it.
internal sealed class TextLabel : Label
{
    private TextFormatFlags Flags => TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl |
        (TextAlign is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter ? TextFormatFlags.HorizontalCenter : 0);

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width is > 1 and < int.MaxValue / 2 ? Math.Max(1, proposedSize.Width - Padding.Horizontal) : int.MaxValue;
        var size = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, new Size(width, int.MaxValue), Flags);
        return new Size(size.Width + Padding.Horizontal, size.Height + Padding.Vertical);
    }

    protected override void OnPaint(PaintEventArgs e) =>
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical),
            Enabled ? ForeColor : SystemColors.GrayText, BackColor, Flags);
}

// A check box drawn like Windows 11's: a rounded square that fills with the accent and shows a check when on. It stays a
// CheckBox, so its keyboard handling, state and accessible role are unchanged.
internal sealed class CheckField : CheckBox, IRescalable
{
    private bool hovered, pressed;

    public CheckField(Ui ui, string text, bool value)
    {
        Ui = ui;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        UseMnemonic = false;
        AutoSize = true;
        Text = text;
        Checked = value;
        Rescale(ui);
    }

    public Ui Ui { get; private set; }

    public void Rescale(Ui ui)
    {
        Ui = ui;
        Font = ui.Font(TypeRole.Body);
        Invalidate();
    }

    private int Box => Ui.Px(20);
    private int Ring => Ui.Px(3);

    public override Size GetPreferredSize(Size proposedSize)
    {
        int edge = Box + 2 * Ring;
        if (Text.Length == 0) return new Size(edge, edge);
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        return new Size(edge + Ui.Px(6) + text.Width, Math.Max(edge, text.Height));
    }

    protected override void OnMouseEnter(EventArgs eventargs) { hovered = true; Invalidate(); base.OnMouseEnter(eventargs); }
    protected override void OnMouseLeave(EventArgs eventargs) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(eventargs); }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left) { pressed = true; Invalidate(); }
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent) { pressed = false; Invalidate(); base.OnMouseUp(mevent); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var colors = Ui.Colors;
        var graphics = pevent.Graphics;
        var back = Shapes.BackgroundOf(this, colors);
        graphics.Clear(back);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int box = Box;
        float top = (Height - box) / 2f;
        var square = new RectangleF(Ring + 0.5f, top + 0.5f, box - 1, box - 1);
        using (var path = Shapes.Rounded(square, Ui.Px(4)))
        {
            if (Checked)
            {
                var fill = !Enabled ? Palette.Layer(back, colors.OnSurface, 0.2f)
                    : Palette.Layer(colors.Primary, colors.OnPrimary, pressed ? 0.2f : hovered ? 0.1f : 0f);
                using (var brush = new SolidBrush(fill)) graphics.FillPath(brush, path);
                int mark = Ui.Px(14);
                if (Glyphs.Get("check", mark, Enabled ? colors.OnPrimary : back) is { } check)
                    graphics.DrawImage(check, Ring + (box - mark) / 2, (int)top + (box - mark) / 2, mark, mark);
            }
            else
            {
                using (var brush = new SolidBrush(Palette.Layer(back, colors.OnSurface, pressed ? 0.1f : hovered ? 0.06f : 0.02f))) graphics.FillPath(brush, path);
                using var pen = new Pen(Enabled ? colors.FieldUnderline : Palette.Layer(back, colors.OnSurface, 0.3f));
                graphics.DrawPath(pen, path);
            }
        }
        if (Text.Length > 0)
            TextRenderer.DrawText(graphics, Text, Font, new Rectangle(box + 2 * Ring + Ui.Px(6), 0, Width, Height),
                Enabled ? colors.OnSurface : Palette.Layer(back, colors.OnSurface, 0.38f), back,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            int grow = Ui.Px(2);
            using var ring = new Pen(colors.OnSurface, grow);
            using var path = Shapes.Rounded(new RectangleF(Ring - grow + 1, top - grow + 1, box + 2 * grow - 2, box + 2 * grow - 2), Ui.Px(6));
            graphics.DrawPath(ring, path);
        }
    }
}

// A rounded surface that groups related rows, like a Windows 11 settings card.
internal sealed class Card : TableLayoutPanel, IRescalable
{
    public Card(Ui ui)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        Ui = ui;
        Rescale(ui);
    }

    public Ui Ui { get; private set; }

    public void Rescale(Ui ui)
    {
        Ui = ui;
        Padding = ui.Pad(16);
        BackColor = ui.Colors.Surface;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(Shapes.BackgroundOf(this, Ui.Colors));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shapes.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Ui.Px(8));
        using (var fill = new SolidBrush(Ui.Colors.Surface)) graphics.FillPath(fill, path);
        using var pen = new Pen(Ui.Colors.SurfaceStroke);
        graphics.DrawPath(pen, path);
    }
}

internal enum Tone { Info, Warning, Error, Success }

// An inline message (M3 banner, Windows InfoBar): a tinted surface, an icon for its tone, an optional title and lines.
internal sealed class Banner : TableLayoutPanel, IRescalable
{
    private readonly PictureBox icon = new() { SizeMode = PictureBoxSizeMode.CenterImage, Margin = Padding.Empty, AccessibleRole = AccessibleRole.Graphic };
    private readonly Tone tone;
    private string? title;
    private IReadOnlyList<string> lines = [];

    public Banner(Ui ui, Tone tone, string? title, IReadOnlyList<string> lines)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        this.tone = tone;
        ColumnCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        AccessibleRole = tone is Tone.Error or Tone.Warning ? AccessibleRole.Alert : AccessibleRole.Grouping;
        icon.AccessibleName = tone switch { Tone.Error => "Error", Tone.Warning => "Warning", Tone.Success => "Done", _ => "Note" };
        Ui = ui;
        SetText(title, lines);
    }

    public Ui Ui { get; private set; }

    private (Color Container, Color Accent, string Glyph) Look => tone switch
    {
        Tone.Warning => (Ui.Colors.WarningContainer, Ui.Colors.Warning, "triangle-alert"),
        Tone.Error => (Ui.Colors.ErrorContainer, Ui.Colors.Error, "circle-alert"),
        Tone.Success => (Palette.Layer(Ui.Colors.Surface, Ui.Colors.Success, 0.10f), Ui.Colors.Success, "circle-check"),
        _ => (Ui.Colors.PrimaryContainer, Ui.Colors.Primary, "info"),
    };

    public void SetText(string? heading, IReadOnlyList<string> text)
    {
        title = heading;
        lines = text;
        AccessibleName = heading ?? (text.Count > 0 ? text[0] : null);
        Rebuild();
    }

    public void Rescale(Ui ui)
    {
        Ui = ui;
        Rebuild();
    }

    private void Rebuild()
    {
        SuspendLayout();
        Controls.Clear();
        RowStyles.Clear();
        var (container, accent, glyph) = Look;
        BackColor = container;
        Padding = Ui.Pad(12, 10, 16, 10);
        icon.BackColor = container;
        icon.Image = Ui.Glyph(glyph, accent);
        icon.Size = new Size(Ui.Px(16), Ui.Px(20));
        icon.Margin = Ui.Pad(0, 0, 12, 0);
        var rows = new List<TextLabel>();
        if (title is not null) rows.Add(Line(title, TypeRole.Strong));
        bool bullets = lines.Count > 1;
        foreach (string line in lines) rows.Add(Line(bullets ? "•  " + line : line, TypeRole.Body));
        RowCount = Math.Max(1, rows.Count);
        Controls.Add(icon, 0, 0);
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].Margin = i == 0 ? Padding.Empty : Ui.Pad(0, 4, 0, 0);
            Controls.Add(rows[i], 1, i);
        }
        ResumeLayout(true);
        Invalidate();
    }

    private TextLabel Line(string text, TypeRole role) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false, Font = Ui.Font(role),
        ForeColor = Ui.Colors.OnSurface, BackColor = Look.Container,
    };

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(Shapes.BackgroundOf(this, Ui.Colors));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shapes.Rounded(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Ui.Px(6));
        using (var fill = new SolidBrush(Look.Container)) graphics.FillPath(fill, path);
        using var pen = new Pen(Palette.Layer(Look.Container, Look.Accent, 0.18f));
        graphics.DrawPath(pen, path);
    }
}

// The tray menu's first row: the state icon, CODEXish and the server and tunnel status. It is not a command, so it is
// disabled; screen readers read its text.
internal sealed class StatusHeader : ToolStripMenuItem
{
    private Ui? ui;
    private string detail = "";
    private Bitmap? badge;

    public StatusHeader() => Enabled = false;

    public void Update(Ui scale, string status, Bitmap? image)
    {
        ui = scale;
        detail = status;
        badge?.Dispose();
        badge = image;
        Text = "CODEXish: " + status;
    }

    public override Size GetPreferredSize(Size constrainingSize)
    {
        if (ui is null) return base.GetPreferredSize(constrainingSize);
        int text = Math.Max(TextRenderer.MeasureText("CODEXish", ui.Font(TypeRole.Strong)).Width, TextRenderer.MeasureText(detail, ui.Font(TypeRole.Small)).Width);
        return new Size(TextLeft(ui) + text + ui.Px(16), ui.Px(52));
    }

    private static int TextLeft(Ui ui) => ui.Px(8) + ui.Px(24) + ui.Px(12);

    protected override void OnPaint(PaintEventArgs e)
    {
        if (ui is null) { base.OnPaint(e); return; }
        var graphics = e.Graphics;
        using (var fill = new SolidBrush(ui.Colors.Menu)) graphics.FillRectangle(fill, 0, 0, Width, Height);
        int size = ui.Px(24);
        if (badge is not null)
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(badge, ui.Px(8), (Height - size) / 2, size, size);
        }
        var strong = ui.Font(TypeRole.Strong);
        var small = ui.Font(TypeRole.Small);
        int titleHeight = TextRenderer.MeasureText("CODEXish", strong).Height;
        int detailHeight = TextRenderer.MeasureText(detail, small).Height;
        int top = (Height - titleHeight - detailHeight) / 2;
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        int left = TextLeft(ui);
        TextRenderer.DrawText(graphics, "CODEXish", strong, new Rectangle(left, top, Width - left, titleHeight), ui.Colors.OnSurface, ui.Colors.Menu, flags);
        TextRenderer.DrawText(graphics, detail, small, new Rectangle(left, top + titleHeight, Width - left, detailHeight), ui.Colors.OnSurfaceVariant, ui.Colors.Menu, flags);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) badge?.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed record MenuEntry(TrayCommand Command, string Glyph, bool Emphasized);

// The menu's switches, drawn rather than taken from Lucide so on and off differ in color and in shape even at 16 px:
// on is a pill filled with the accent and its knob on the right, off a muted outline with its knob on the left. A
// disabled switch keeps its shape in the disabled tone.
internal static class Switches
{
    private static readonly Dictionary<(bool, bool, int, int, int), Bitmap> Drawn = [];

    public static bool Draws(string glyph) => glyph is "switch-on" or "switch-off";

    public static Bitmap Get(Ui ui, bool on, bool enabled)
    {
        var colors = ui.Colors;
        Color disabled = Palette.Layer(colors.Menu, colors.OnSurface, 0.38f);
        Color track = !enabled ? disabled : on ? colors.Primary : colors.OnSurfaceVariant;
        Color knob = on ? (enabled ? colors.OnPrimary : colors.Menu) : track;
        int size = ui.Px(16);
        lock (Drawn)
        {
            var key = (on, enabled, size, track.ToArgb(), knob.ToArgb());
            if (Drawn.TryGetValue(key, out var cached)) return cached;
            float s = ui.Dpi / 96f;
            var image = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float height = 10 * s, top = (size - height) / 2;
                if (on)
                {
                    using (var path = Shapes.Rounded(new RectangleF(0, top, size, height), height / 2))
                    using (var fill = new SolidBrush(track)) graphics.FillPath(fill, path);
                    float diameter = 6 * s;
                    using var dot = new SolidBrush(knob);
                    graphics.FillEllipse(dot, size - 2 * s - diameter, top + (height - diameter) / 2, diameter, diameter);
                }
                else
                {
                    float stroke = Math.Max(1, s);
                    using (var path = Shapes.Rounded(new RectangleF(stroke / 2, top + stroke / 2, size - stroke, height - stroke), (height - stroke) / 2))
                    using (var pen = new Pen(track, stroke)) graphics.DrawPath(pen, path);
                    float diameter = 4 * s;
                    using var dot = new SolidBrush(knob);
                    graphics.FillEllipse(dot, 3 * s, top + (height - diameter) / 2, diameter, diameter);
                }
            }
            Drawn[key] = image;
            return image;
        }
    }
}

// Windows 11's context menu look on a ToolStrip: a flat surface with a hairline border, rounded hover highlights inset
// from the edges, tinted Lucide icons and quiet separators.
internal sealed class MenuRenderer(Func<Ui> scale) : ToolStripRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var fill = new SolidBrush(scale().Colors.Menu);
        e.Graphics.FillRectangle(fill, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(scale().Colors.MenuStroke);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    // An item may be laid out wider than the visible menu, so insets are measured from the menu's own edges.
    private (int Left, int Right) Inset(ToolStripItem item, ToolStrip? strip)
    {
        int inset = scale().Px(4);
        return (inset - item.Bounds.Left, (strip?.Width ?? item.Bounds.Right) - inset - item.Bounds.Left);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var ui = scale();
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var (left, right) = Inset(e.Item, e.ToolStrip);
        var bounds = new RectangleF(left, 1, right - left, e.Item.Height - 2);
        using var path = Shapes.Rounded(bounds, ui.Px(4));
        using var fill = new SolidBrush(ui.Colors.MenuHover);
        graphics.FillPath(fill, path);
    }

    // Text and icon are both centered on the item's full height, so they share one line whatever the padding. The
    // right-hand column (where a shortcut would be) holds a switch's On or Off in the secondary color.
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var colors = scale().Colors;
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        Color disabled = Palette.Layer(colors.Menu, colors.OnSurface, 0.38f);
        if (e.Item is ToolStripMenuItem { ShortcutKeyDisplayString: { } word } && e.Text == word)
        {
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, new Rectangle(e.TextRectangle.X, 0, e.TextRectangle.Width, e.Item.Height),
                e.Item.Enabled ? colors.OnSurfaceVariant : disabled, flags | TextFormatFlags.Right);
            return;
        }
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, new Rectangle(e.TextRectangle.X, 0, e.Item.Width - e.TextRectangle.X, e.Item.Height),
            e.Item.Enabled ? colors.OnSurface : disabled, flags | TextFormatFlags.EndEllipsis);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is null) return;
        e.Graphics.DrawImage(e.Image, e.ImageRectangle.X + (e.ImageRectangle.Width - e.Image.Width) / 2,
            (e.Item.Height - e.Image.Height) / 2, e.Image.Width, e.Image.Height);
    }

    // A checked item is a switch that shows its state itself, so no check mark is drawn over it.
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(scale().Colors.MenuStroke);
        var (left, right) = Inset(e.Item, e.ToolStrip);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, left, y, right, y);
    }
}

// The tray's context menu. Its fonts, icons and spacing are set for the DPI of the screen it opens on, every time it
// opens.
internal sealed class TrayMenu
{
    private Ui ui;

    public TrayMenu(Ui initial)
    {
        ui = initial;
        Strip = new ContextMenuStrip { ShowCheckMargin = false, ShowImageMargin = true, Renderer = new MenuRenderer(() => ui) };
        Strip.HandleCreated += (_, _) => DesignNative.RoundCorners(Strip.Handle);
        Header = new StatusHeader();
        Strip.Items.Add(Header);
        Strip.Items.Add(new ToolStripSeparator());
    }

    public ContextMenuStrip Strip { get; }
    public StatusHeader Header { get; }
    public Ui Ui => ui;
    public Dictionary<TrayCommand, ToolStripMenuItem> Items { get; } = [];

    public ToolStripMenuItem Add(TrayCommand command, string text, string glyph, bool emphasized = false)
    {
        var item = new ToolStripMenuItem(text) { Tag = new MenuEntry(command, glyph, emphasized), ImageScaling = ToolStripItemImageScaling.None };
        Strip.Items.Add(item);
        Items[command] = item;
        return item;
    }

    public void Separator() => Strip.Items.Add(new ToolStripSeparator());

    // The whole model every time: which items apply, the switches' state, words and accessible names, and the header.
    public void Apply(Ui scale, TrayMenuModel model)
    {
        ui = scale;
        Strip.SuspendLayout();
        Strip.Font = scale.Font(TypeRole.Body);
        Strip.Padding = scale.Pad(0, 4, 0, 4);
        Strip.ImageScalingSize = new Size(scale.Px(16), scale.Px(16));
        Header.Update(scale, model.Header, TrayIcons.Image(model.Look, scale.Px(24)));
        Color disabled = Palette.Layer(scale.Colors.Menu, scale.Colors.OnSurface, 0.38f);
        foreach (ToolStripItem item in Strip.Items)
        {
            if (item is ToolStripSeparator separator) { separator.Margin = scale.Pad(0, 2, 0, 2); continue; }
            if (item is not ToolStripMenuItem menuItem || item.Tag is not MenuEntry entry) continue;
            menuItem.Enabled = model.Enabled(entry.Command);
            menuItem.AccessibleDescription = model.Hint(entry.Command);
            menuItem.Font = scale.Font(entry.Emphasized ? TypeRole.Strong : TypeRole.Body);
            menuItem.Padding = scale.Pad(4, 5, 12, 5);
            string glyph = entry.Glyph;
            if (model.Toggle(entry.Command) is { } toggle)
            {
                // Checked stays in step for screen readers, which also hear the state in the name.
                menuItem.Checked = toggle.On;
                menuItem.ShortcutKeyDisplayString = toggle.Word;
                menuItem.AccessibleName = toggle.AccessibleName;
                glyph = toggle.Glyph;
            }
            menuItem.Image = Switches.Draws(glyph) ? Switches.Get(scale, glyph == "switch-on", menuItem.Enabled)
                : scale.Glyph(glyph, menuItem.Enabled ? scale.Colors.OnSurfaceVariant : disabled);
        }
        Strip.ResumeLayout(true);
    }
}
#endif
