using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol.Protocol;

namespace Codexish.P0;

// P0 only: a disposable, unlocked primary-monitor desktop with one selected Notepad process.
// Target checks reduce accidental input; they are not a sandbox for the logged-in OS user.
public sealed class NativeDesktop
{
    private readonly object gate = new();
    private readonly int? pid;
    private readonly long started;
    private Snapshot? last;
    private sealed record Snapshot(string Id, nint Window, Rect Bounds, int Width, int Height, uint InputTick, ScreenshotGeometry? Geometry = null);

    public NativeDesktop(int? notepadPid)
    {
        pid = notepadPid;
        if (!pid.HasValue) return;
        if (!OperatingSystem.IsWindows()) throw new ArgumentException("Desktop probing requires Windows.");
        using var p = Process.GetProcessById(pid.Value);
        using var self = Process.GetCurrentProcess();
        if (!p.ProcessName.Equals("notepad", StringComparison.OrdinalIgnoreCase) || p.SessionId != self.SessionId)
            throw new ArgumentException("Select a Notepad process in this interactive user session.");
        started = p.StartTime.ToUniversalTime().Ticks;
    }

    private Snapshot Current()
    {
        if (!OperatingSystem.IsWindows() || !pid.HasValue)
            throw new ProbeFault("UNSUPPORTED_CAPABILITY", "Desktop is disabled. Explicitly select disposable Notepad locally.");
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) throw new ProbeFault("UNSUPPORTED_CAPABILITY", "Input desktop is unavailable or protected.");
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) || name.ToString() != "Default")
                throw new ProbeFault("UNSUPPORTED_CAPABILITY", "Locked or secure desktops are not supported.");
        }
        finally { CloseDesktop(desktop); }
        using var p = Process.GetProcessById(pid.Value);
        if (p.HasExited || p.StartTime.ToUniversalTime().Ticks != started)
            throw new ProbeFault("WINDOW_NOT_FOUND", "The selected Notepad process no longer exists.");
        nint window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out uint owner);
        if (owner != pid || !GetWindowRect(window, out Rect rect) || IsIconic(window))
            throw new ProbeFault("STALE_OBSERVATION", "Bring the selected Notepad or its save dialog to the foreground.");
        int width = GetSystemMetrics(0), height = GetSystemMetrics(1);
        if (width <= 0 || height <= 0 || rect.Left >= width || rect.Top >= height || rect.Right <= 0 || rect.Bottom <= 0)
            throw new ProbeFault("UNSUPPORTED_CAPABILITY", "P0 requires the selected window on the primary monitor.");
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref input)) throw new ProbeFault("EXECUTION_FAILED", "Cannot inspect input state.");
        return new Snapshot(Guid.NewGuid().ToString("N"), window, rect, width, height, input.Tick);
    }

    public CallToolResult Observe(int maxWidth = 1280)
    {
        lock (gate)
        {
            if (maxWidth < 0) throw new ProbeFault("INVALID_ARGUMENT", "max_width must be nonnegative; 0 means native resolution.");
            if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return Reply.Error("UNSUPPORTED_CAPABILITY", "Actual Windows capture is unavailable; no synthetic image substituted.");
            using var dpi = new DpiScope();
            DateTimeOffset begin = DateTimeOffset.UtcNow;
            Snapshot before = Current();
            using var image = new Bitmap(before.Width, before.Height);
            using (Graphics graphics = Graphics.FromImage(image))
                graphics.CopyFromScreen(0, 0, 0, 0, new Size(before.Width, before.Height), CopyPixelOperation.SourceCopy);
            var geometry = ScreenshotGeometry.Fit(before.Width, before.Height, maxWidth);
            byte[] png = EncodePng(image, geometry);
            Snapshot after = Current();
            if (!Same(before, after)) throw new ProbeFault("STALE_OBSERVATION", "Target changed during capture; observe again.");
            last = after with { Geometry = geometry };
            var result = Reply.Ok(new { observation_id = after.Id, window_id = after.Window.ToString(), process_id = pid,
                capture_started_at = begin, capture_finished_at = DateTimeOffset.UtcNow,
                width = after.Width, height = after.Height, coordinate_space = "primary_monitor_physical_px",
                image = new { width = geometry.ImageWidth, height = geometry.ImageHeight },
                image_to_desktop = new { scale_x = geometry.ScaleX, scale_y = geometry.ScaleY, offset_x = 0, offset_y = 0 },
                click_coordinate_space = "image", max_width = maxWidth, png_bytes = png.Length,
                window_bounds = new { x = after.Bounds.Left, y = after.Bounds.Top, right = after.Bounds.Right, bottom = after.Bounds.Bottom },
                source = "actual_screen_capture", next_tool = "click or type_text, then screenshot" });
            result.Content.Add(ImageContentBlock.FromBytes(png, "image/png"));
            return result;
        }
    }

    [SupportedOSPlatform("windows6.1")]
    internal static byte[] EncodePng(Bitmap source, ScreenshotGeometry geometry)
    {
        using var bytes = new MemoryStream();
        if (source.Width == geometry.ImageWidth && source.Height == geometry.ImageHeight)
            source.Save(bytes, ImageFormat.Png);
        else
        {
            using var resized = new Bitmap(geometry.ImageWidth, geometry.ImageHeight);
            using (Graphics graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(0, 0, resized.Width, resized.Height),
                    0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
            }
            resized.Save(bytes, ImageFormat.Png);
        }
        return bytes.ToArray();
    }

    private static bool Same(Snapshot a, Snapshot b) => a.Window == b.Window && a.Bounds.Equals(b.Bounds)
        && a.Width == b.Width && a.Height == b.Height && a.InputTick == b.InputTick;
    private Snapshot Validate(string id)
    {
        var current = Current();
        if (last is null || last.Id != id || !Same(last, current))
            throw new ProbeFault("STALE_OBSERVATION", "Window, focus, layout, or input changed. Call screenshot before acting.");
        return last; // Keep the immutable image transform from the validated observation.
    }

    public CallToolResult Click(int x, int y, string observationId, string coordinateSpace)
    {
        lock (gate)
        {
            using var dpi = new DpiScope();
            Snapshot target = Validate(observationId);
            // Explicit coordinate_space prevents old cached schemas from silently changing click units.
            (x, y) = target.Geometry!.ToPhysical(x, y, coordinateSpace);
            GetWindowThreadProcessId(WindowFromPoint(new PointNative { X = x, Y = y }), out uint owner);
            if (owner != pid) throw new ProbeFault("PERMISSION_DENIED", "The coordinate does not belong to the selected Notepad process.");
            last = null;
            if (!SetCursorPos(x, y)) throw new ProbeFault("EXECUTION_FAILED", "Could not position the pointer.");
            Send([new Input { Type = 0, Union = new InputUnion { Mouse = new MouseInput { Flags = 2 } } },
                  new Input { Type = 0, Union = new InputUnion { Mouse = new MouseInput { Flags = 4 } } }]);
            return Reply.Ok(new { input_delivered = true, business_outcome = "not_verified", next_tool = "screenshot",
                physical_x = x, physical_y = y, coordinate_space = "primary_monitor_physical_px" });
        }
    }

    public CallToolResult Type(string observationId, string? text, string? key)
    {
        lock (gate)
        {
            if ((text is null) == (key is null)) throw new ProbeFault("INVALID_ARGUMENT", "Supply exactly one of text or key.");
            List<Input> inputs = [];
            if (text is not null)
                foreach (char c in text) { inputs.Add(Keyboard(0, c, 4)); inputs.Add(Keyboard(0, c, 6)); }
            else
            {
                ushort vk = key switch { "CTRL+S" => 0x53, "CTRL+A" => 0x41, "ENTER" => 0x0d, "ESC" => 0x1b,
                    _ => throw new ProbeFault("INVALID_ARGUMENT", "Allowed keys: CTRL+S, CTRL+A, ENTER, ESC.") };
                bool ctrl = key!.StartsWith("CTRL+", StringComparison.Ordinal);
                if (ctrl) inputs.Add(Keyboard(0x11, 0, 0));
                inputs.Add(Keyboard(vk, 0, 0)); inputs.Add(Keyboard(vk, 0, 2));
                if (ctrl) inputs.Add(Keyboard(0x11, 0, 2));
            }
            using var dpi = new DpiScope();
            Validate(observationId); last = null;
            Send(inputs.ToArray());
            return Reply.Ok(new { input_delivered = true, business_outcome = "not_verified", next_tool = "screenshot" });
        }
    }

    private static Input Keyboard(ushort vk, ushort scan, uint flags) => new()
        { Type = 1, Union = new InputUnion { Keyboard = new KeyInput { Vk = vk, Scan = scan, Flags = flags } } };
    private static void Send(Input[] inputs)
    {
        if (inputs.Length == 0) return;
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return;
        // Release only our possible held modifier/button. This is cleanup, never a replay of the action.
        Input[] releases = [Keyboard(0x11, 0, 2), new Input { Type = 0, Union = new InputUnion { Mouse = new MouseInput { Flags = 4 } } }];
        SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
        throw new ProbeFault("EXECUTION_UNKNOWN", "Input was rejected or partially delivered. Reobserve; do not repeat blindly.", sent == 0 ? "unknown" : "partial");
    }

    public static void CheckFileHandle(FileStream stream, string root)
    {
        if (!OperatingSystem.IsWindows()) return; // Linux checks are not Windows-native test evidence.
        var path = new StringBuilder(1024);
        uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, path, (uint)path.Capacity, 0);
        if (length >= path.Capacity) { path = new StringBuilder(checked((int)length + 1)); length = GetFinalPathNameByHandle(stream.SafeFileHandle, path, (uint)path.Capacity, 0); }
        if (length == 0 || length >= path.Capacity) throw new ProbeFault("OUTSIDE_WORKSPACE", "Cannot verify the opened handle's path.");
        string final = path.ToString();
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
        if (!final.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ProbeFault("OUTSIDE_WORKSPACE", "Opened handle does not belong to the disposable root.");
    }

    private sealed class DpiScope : IDisposable
    {
        private readonly nint previous;
        public DpiScope() { if (OperatingSystem.IsWindows()) previous = SetThreadDpiAwarenessContext(new nint(-4)); }
        public void Dispose() { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Tick; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointNative p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW")]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int size, out uint needed);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}

// Pure geometry is also tested on Linux. It does not fabricate a desktop observation.
public sealed record ScreenshotGeometry(int PhysicalWidth, int PhysicalHeight, int ImageWidth, int ImageHeight)
{
    public double ScaleX => (double)PhysicalWidth / ImageWidth;
    public double ScaleY => (double)PhysicalHeight / ImageHeight;

    public static ScreenshotGeometry Fit(int width, int height, int maxWidth = 1280)
    {
        if (width <= 0 || height <= 0 || maxWidth < 0)
            throw new ProbeFault("INVALID_ARGUMENT", "Positive physical dimensions and nonnegative max_width required.");
        int imageWidth = maxWidth == 0 ? width : Math.Min(width, maxWidth);
        int imageHeight = Math.Max(1, (int)Math.Round((double)height * imageWidth / width, MidpointRounding.AwayFromZero));
        return new(width, height, imageWidth, imageHeight);
    }

    public (int X, int Y) ToPhysical(int x, int y, string coordinateSpace)
    {
        if (coordinateSpace is not ("image" or "primary_monitor_physical_px"))
            throw new ProbeFault("INVALID_ARGUMENT", "coordinate_space must be image or primary_monitor_physical_px.");
        int width = coordinateSpace == "image" ? ImageWidth : PhysicalWidth;
        int height = coordinateSpace == "image" ? ImageHeight : PhysicalHeight;
        if (x < 0 || y < 0 || x >= width || y >= height)
            throw new ProbeFault("INVALID_ARGUMENT", "Coordinates are outside this observation's selected coordinate space.");
        return coordinateSpace == "image"
            ? ((int)((long)x * PhysicalWidth / ImageWidth), (int)((long)y * PhysicalHeight / ImageHeight))
            : (x, y);
    }
}
