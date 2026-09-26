using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace Codexish.Server;

// Sign-in autostart is CODEXish.lnk in the per-user Startup folder. It is written through shell32's IShellLinkW and
// IPersistFile rather than WScript.Shell, because Windows Script Host is being retired. The Startup folder is a
// constructor argument so the tests never touch the real one.
public sealed class TrayAutostart
{
    public const string ShortcutName = "CODEXish.lnk";
    private const int ShowMinimizedNoActive = 7;
    private readonly string executable, workingDirectory;

    public TrayAutostart(string startupDirectory, string executable, string configPath, string? hostedAssembly = null)
    {
        ShortcutPath = Path.Combine(startupDirectory, ShortcutName);
        this.executable = executable;
        workingDirectory = Path.GetDirectoryName(hostedAssembly ?? executable) ?? "";
        // The full path, so the shortcut works from its own working directory whatever --config was relative to.
        string full = Path.GetFullPath(configPath);
        bool isDefault = string.Equals(full, Path.GetFullPath(ServerConfig.DefaultPath), StringComparison.OrdinalIgnoreCase);
        Arguments = (hostedAssembly is null ? "" : $"\"{hostedAssembly}\" ") + "--tray --start" + (isDefault ? "" : $" --config \"{full}\"");
    }

    public static TrayAutostart ForThisProcess(string configPath)
    {
        // Started as "dotnet Codexish.Server.dll", the shortcut has to hand the assembly to the dotnet host.
        var (process, assembly) = TrayInstance.SelfLaunch();
        return new TrayAutostart(Environment.GetFolderPath(Environment.SpecialFolder.Startup), process, configPath, assembly);
    }

    public string ShortcutPath { get; }
    public string Arguments { get; }
    public bool Enabled => File.Exists(ShortcutPath);

    [SupportedOSPlatform("windows")]
    public void Enable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(executable);
            link.SetArguments(Arguments);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription("Starts the CODEXish server and its tray icon at sign-in");
            // The console window of this executable opens minimized and without taking focus from the desktop.
            link.SetShowCmd(ShowMinimizedNoActive);
            ((IPersistFile)link).Save(ShortcutPath, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    public void Disable() => File.Delete(ShortcutPath);

    [SupportedOSPlatform("windows")]
    public (string Target, string Arguments, string WorkingDirectory) Read()
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(ShortcutPath, 0);
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var directory = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            link.GetArguments(arguments, arguments.Capacity);
            link.GetWorkingDirectory(directory, directory.Capacity);
            return (target.ToString(), arguments.ToString(), directory.ToString());
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    // Self-heal on evidence only: an existing shortcut whose target file is gone, or that cannot be read at all, is
    // rewritten to this executable. A missing shortcut means autostart is off and stays off.
    [SupportedOSPlatform("windows")]
    public bool Heal()
    {
        if (!Enabled) return false;
        string target;
        try { target = Read().Target; }
        catch (Exception) { target = ""; }
        if (target.Length > 0 && File.Exists(target)) return false;
        Enable();
        return true;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
