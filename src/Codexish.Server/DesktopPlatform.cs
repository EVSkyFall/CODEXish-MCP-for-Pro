using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Automation;
#endif

namespace Codexish.Server;

public static class DesktopPlatform
{
    public static IDesktopPlatform Create()
    {
#if WINDOWS
        return new WindowsDesktopPlatform();
#else
        return new Unavailable();
#endif
    }
#if !WINDOWS
    private sealed class Unavailable : IDesktopPlatform
    {
        private static Exception Missing() => new CodexishFault("UNSUPPORTED_CAPABILITY", "Desktop tools require the Windows build and an interactive desktop.");
        public DesktopScene Inspect() => throw Missing();
        public byte[] Capture(DesktopGeometry geometry) => throw Missing();
        public DesktopUi Query(DesktopWindow window) => throw Missing();
        public long HitTest(int x, int y) => throw Missing();
        public int Send(DesktopInput[] inputs, DesktopRect desktop) => throw Missing();
        public WindowActionResult WindowAction(DesktopWindow window, string action) => throw Missing();
    }
#endif
}

#if WINDOWS
internal sealed class WindowsDesktopPlatform(int? fixturePid = null) : IDesktopPlatform
{
    private static void Ready()
    {
        SetThreadDpiAwarenessContext(-4); // Physical pixels, including on mixed-DPI desktops; only this worker thread.
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) throw new CodexishFault("UNSUPPORTED_CAPABILITY", "The interactive input desktop is unavailable or locked.");
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformationW(desktop, 2, name, name.Capacity * 2, out _) || name.ToString() != "Default")
                throw new CodexishFault("UNSUPPORTED_CAPABILITY", "Secure/UAC desktops are not controlled.");
        }
        finally { CloseDesktop(desktop); }
    }
    public DesktopScene Inspect()
    {
        Ready(); var windows = new List<DesktopWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || !GetWindowRect(handle, out var bounds)) return true;
            GetWindowThreadProcessId(handle, out uint pid);
            if (fixturePid.HasValue && pid != fixturePid.Value) return true;
            var title = new StringBuilder(1024); GetWindowTextW(handle, title, title.Capacity);
            long started;
            try { using var process = Process.GetProcessById((int)pid); started = process.StartTime.ToUniversalTime().Ticks; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
            windows.Add(new($"win_{handle:x}_{pid}_{started}", handle, (int)pid, started,
                Redaction.Apply(title.ToString()).Text, new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), IsIconic(handle), pid == Environment.ProcessId, GetDpiForWindow(handle)));
            return true;
        }, 0);
        var rect = new DesktopRect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        var monitors = new List<string>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint hdc, ref Rect r, nint data) => { monitors.Add($"{r.Left},{r.Top},{r.Right},{r.Bottom}"); return true; }, 0);
        var tick = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() }; GetLastInputInfo(ref tick); GetCursorPos(out var cursor);
        nint foreground = GetForegroundWindow();
        return new(rect, string.Join(";", monitors.Order(StringComparer.Ordinal)), windows.ToArray(), windows.FirstOrDefault(w => w.Handle == foreground)?.Id,
            tick.Tick, cursor.X, cursor.Y);
    }
    public byte[] Capture(DesktopGeometry geometry)
    {
        Ready(); var r = geometry.Source;
        using var raw = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(raw))
        {
            nint source = GetDC(0), destination = g.GetHdc();
            try
            {
                // Call BitBlt directly: CopyFromScreen validates its enum and rejects combined ROP flags.
                if (source == 0 || !BitBlt(destination, 0, 0, r.Width, r.Height, source, r.X, r.Y, 0x40cc0020))
                    throw new CodexishFault("EXECUTION_FAILED", "BitBlt could not capture the interactive desktop.", details: new { stage = "capture", win32_error = Marshal.GetLastWin32Error() });
            }
            finally { g.ReleaseHdc(destination); if (source != 0) ReleaseDC(0, source); }
        }
        using var output = new MemoryStream();
        if (geometry.Width == r.Width && geometry.Height == r.Height) raw.Save(output, ImageFormat.Png);
        else
        {
            using var resized = new Bitmap(geometry.Width, geometry.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(resized)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(raw, 0, 0, geometry.Width, geometry.Height); }
            resized.Save(output, ImageFormat.Png);
        }
        return output.ToArray();
    }
    public DesktopUi Query(DesktopWindow window)
    {
        var items = new List<DesktopElement>();
        try
        {
            Ready(); var root = AutomationElement.FromHandle((nint)window.Handle);
            // Never search the entire desktop recursively. Search only this explicitly selected window.
            var found = root.FindAll(TreeScope.Subtree, Condition.TrueCondition);
            foreach (AutomationElement e in found)
            {
                try
                {
                    var c = e.Current; var b = c.BoundingRectangle;
                    if (b.IsEmpty || double.IsInfinity(b.X) || double.IsNaN(b.X)) continue;
                    bool selected = e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection) && ((SelectionItemPattern)selection).Current.IsSelected;
                    string? text = null;
                    if (!c.IsPassword && e.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) text = Redaction.Apply(((ValuePattern)value).Current.Value).Text;
                    string id = string.Join("_", e.GetRuntimeId());
                    items.Add(new(id, c.ControlType.ProgrammaticName.Replace("ControlType.", ""), c.IsPassword ? "[password control]" : Redaction.Apply(c.Name).Text,
                        new((int)b.X, (int)b.Y, (int)Math.Ceiling(b.Width), (int)Math.Ceiling(b.Height)), c.HasKeyboardFocus, c.IsPassword, c.IsEnabled, c.IsOffscreen, selected, text));
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
            }
            return new(items.ToArray());
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException or CodexishFault)
        { return new(items.ToArray(), e.GetType().Name); }
    }
    public long HitTest(int x, int y) => GetAncestor(WindowFromPoint(new() { X = x, Y = y }), 2);
    public int Send(DesktopInput[] inputs, DesktopRect desktop)
    {
        Ready(); VerifyIntegrity(GetForegroundWindow()); var native = inputs.Select(i => Convert(i, desktop)).ToArray();
        return checked((int)SendInput((uint)native.Length, native, Marshal.SizeOf<Input>()));
    }
    private static Input Convert(DesktopInput i, DesktopRect desktop)
    {
        if (i.Kind is "key" or "unicode")
        {
            bool extended = i.Kind == "key" && (i.Code is >= 33 and <= 46 || i.Code is 0x5b or 0x5c);
            return new() { Type = 1, Data = new() { Key = new() { Vk = i.Kind == "key" ? (ushort)i.Code : (ushort)0,
                Scan = i.Kind == "unicode" ? (ushort)i.Code : (ushort)0,
                Flags = (i.Up ? 2u : 0) | (i.Kind == "unicode" ? 4u : 0) | (extended ? 1u : 0) } } };
        }
        uint flags = i.Kind switch { "move" => 0xc001u, "left" => i.Up ? 4u : 2u, "right" => i.Up ? 16u : 8u, "wheel" => 0x800u, _ => throw new ArgumentException("Invalid input plan") };
        return new() { Type = 0, Data = new() { Mouse = new() { Flags = flags, MouseData = unchecked((uint)i.Code),
            X = i.Kind == "move" ? (int)((((long)i.X - desktop.X) * 65536 + 32768) / desktop.Width) : 0,
            Y = i.Kind == "move" ? (int)((((long)i.Y - desktop.Y) * 65536 + 32768) / desktop.Height) : 0 } } };
    }
    public WindowActionResult WindowAction(DesktopWindow window, string action)
    {
        Ready(); nint handle = (nint)window.Handle; VerifyIntegrity(handle);
        if (action == "focus_window") return ForegroundActivation.Run(new ForegroundSteps(handle));
        int mode = action switch { "minimize_window" => 6, "maximize_window" => 3, "restore_window" => 9, _ => throw new ArgumentException("Unknown window action") };
        bool submitted = ShowWindowAsync(handle, mode);
        return new(submitted, submitted ? "show_window_async" : null, 0, ["show_window_async"]);
    }
    // A background server loses to the Windows foreground lock, so SetForegroundWindow alone is not enough.
    // These are the Win32 calls only; ForegroundActivation owns their order, confirmation and cleanup. No system
    // parameter, lock timeout, other process's permission or privilege is changed.
    private sealed class ForegroundSteps(nint handle) : IForegroundSteps
    {
        public bool Minimized => IsIconic(handle);
        public bool Foreground => GetForegroundWindow() == handle && !IsIconic(handle);
        public void Restore() => ShowWindow(handle, 9);
        public void SetForeground() => SetForegroundWindow(handle);
        public void BringToTop() => BringWindowToTop(handle);
        public (uint Self, uint Foreground, uint Target) InputThreads()
        {
            nint foreground = GetForegroundWindow();
            return (GetCurrentThreadId(), foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _), GetWindowThreadProcessId(handle, out _));
        }
        public bool AttachInput(uint thread, uint other, bool attach) => AttachThreadInput(thread, other, attach);
        // Windows permits SetForegroundWindow after the calling process generated the last input event.
        public int SendAlt(bool up) => checked((int)SendInput(1,
            [new Input { Type = 1, Data = new() { Key = new() { Vk = 0x12, Flags = up ? 2u : 0 } } }], Marshal.SizeOf<Input>()));
        public void Pause() => Thread.Sleep(15);
    }
    // Read security labels only; never elevate or change another process token.
    private static void VerifyIntegrity(nint window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        static int Level(uint processId)
        {
            nint process = OpenProcess(0x1000, false, processId);
            if (process == 0) throw new CodexishFault("UNSUPPORTED_CAPABILITY", "Cannot verify the target's integrity level.");
            nint token = 0, buffer = 0;
            try
            {
                if (!OpenProcessToken(process, 8, out token)) throw new CodexishFault("UNSUPPORTED_CAPABILITY", "Cannot query target integrity.");
                GetTokenInformation(token, 25, 0, 0, out int size);
                buffer = Marshal.AllocHGlobal(size);
                if (!GetTokenInformation(token, 25, buffer, size, out _)) throw new CodexishFault("UNSUPPORTED_CAPABILITY", "Cannot read target integrity label.");
                nint sid = Marshal.ReadIntPtr(buffer);
                byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { if (buffer != 0) Marshal.FreeHGlobal(buffer); if (token != 0) CloseHandle(token); CloseHandle(process); }
        }
        if (Level(pid) > Level((uint)Environment.ProcessId))
            throw new CodexishFault("UNSUPPORTED_CAPABILITY", "Higher-integrity windows are not controlled; no elevation is attempted.");
    }
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(nint token, int kind, nint data, int bytes, out int needed);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Tick; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Key; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    private delegate bool EnumWindow(nint handle, nint param);
    private delegate bool EnumMonitor(nint monitor, nint hdc, ref Rect rectangle, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint param);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, EnumMonitor callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder title, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder data, int bytes, out int needed);
}
#endif
