using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ModelContextProtocol.Client;

namespace Codexish.Server;

// B0 belongs to the process, not to a runtime rebuilt by the tray.
public static class ChildEnvironment
{
    private static readonly object Sync = new();
    private static readonly UserEnvironmentBaseline baseline = new();
    public static string LastSource { get; private set; } = "process_environment";
    public static string? LastError { get; private set; }
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static void Initialize(Action<string>? report = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Sync)
        {
            try { baseline.Initialize(ReadWindowsEnvironment); LastError = null; }
            catch (Exception error) { Failed(error, report); }
        }
    }

    public static Dictionary<string, string?> Capture(Action<string>? report = null)
    {
        var process = ProcessEnvironment();
        if (!OperatingSystem.IsWindows()) return process;
        lock (Sync)
        {
            try
            {
                // If the initial read failed, retry it instead of remembering a permanent failure.
                var result = baseline.Capture(process, ReadWindowsEnvironment);
                LastSource = "current_user_environment"; LastError = null;
                return result;
            }
            catch (Exception error) { Failed(error, report); return process; }
        }
    }

    private static void Failed(Exception error, Action<string>? report)
    {
        LastSource = "process_environment";
        LastError = error.GetType().Name + ": " + Redaction.Apply(error.Message).Text;
        try { report?.Invoke(LastError); } catch (Exception) { }
    }

    internal static Dictionary<string, string?> ProcessEnvironment()
    {
        var result = new Dictionary<string, string?>(Comparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string name) result[name] = entry.Value?.ToString();
        return result;
    }

    internal static Dictionary<string, string?> Overlay(IReadOnlyDictionary<string, string?> process,
        IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after, StringComparer comparer)
    {
        var result = new Dictionary<string, string?>(process, comparer);
        var old = new Dictionary<string, string?>(before, comparer);
        var now = new Dictionary<string, string?>(after, comparer);
        foreach (var (name, value) in now)
            if (!old.TryGetValue(name, out var previous) || previous != value) result[name] = value;
        foreach (var name in old.Keys)
            if (!now.ContainsKey(name)) result.Remove(name);
        return result;
    }

    internal static Dictionary<string, string?> ReadWindowsEnvironment()
    {
        using var process = Process.GetCurrentProcess();
        if (!OpenProcessToken(process.Handle, 0x0008, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!CreateEnvironmentBlock(out var block, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (nint p = block; Marshal.ReadInt16(p) != 0;)
                {
                    string entry = Marshal.PtrToStringUni(p)!;
                    int equals = entry.IndexOf('=', 1); // leading '=' denotes a drive working-directory entry
                    if (equals > 0) result[entry[..equals]] = entry[(equals + 1)..];
                    p += (entry.Length + 1) * 2;
                }
                return result;
            }
            finally { DestroyEnvironmentBlock(block); }
        }
        finally { CloseHandle(token); }
    }

    public static Dictionary<string, string?> MountEnvironment(IReadOnlyDictionary<string, string?> source,
        IEnumerable<string>? envVars = null, IReadOnlyDictionary<string, string?>? expandedEnv = null, Action<string>? warning = null)
    {
        var result = new Dictionary<string, string?>(Comparer);
        var names = StdioClientTransportOptions.GetDefaultEnvironmentVariables().Keys.Concat(new[] {
            "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "ProgramData", "WINDIR", "COMSPEC", "TMP", "LANG",
            "LC_ALL", "DISPLAY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DOTNET_ROOT" });
        foreach (string name in names)
            if (!Redaction.IsSecretName(name) && !name.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase)
                && source.TryGetValue(name, out var value)) result[name] = value;
        foreach (string name in envVars ?? [])
            if (source.TryGetValue(name, out var value)) result[name] = value;
            else warning?.Invoke($"env_vars variable '{name}' is undefined");
        foreach (var (name, value) in expandedEnv ?? new Dictionary<string, string?>())
            if (value is not null) result[name] = value;
        Redaction.AddEnvironmentValues(result);
        return result;
    }

    public static string Expand(string text, IReadOnlyDictionary<string, string?> environment, string field,
        Action<string>? warning = null, string? projectRoot = null)
    {
        var values = new Dictionary<string, string?>(environment, Comparer);
        if (projectRoot is not null && !values.ContainsKey("CLAUDE_PROJECT_DIR")) values["CLAUDE_PROJECT_DIR"] = projectRoot;
        string ExpandInner(string input)
        {
            var result = new StringBuilder();
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] != '$' || i + 1 == input.Length || input[i + 1] != '{') { result.Append(input[i]); continue; }
                int end = i + 2, depth = 1;
                for (; end < input.Length; end++)
                {
                    if (input[end] == '$' && end + 1 < input.Length && input[end + 1] == '{') { depth++; end++; }
                    else if (input[end] == '}' && --depth == 0) break;
                }
                if (end == input.Length) { result.Append(input[i]); continue; }
                string body = input[(i + 2)..end];
                int fallback = body.IndexOf(":-", StringComparison.Ordinal);
                string name = fallback < 0 ? body : body[..fallback];
                bool defined = values.TryGetValue(name, out var value);
                if (fallback >= 0 && string.IsNullOrEmpty(value)) result.Append(ExpandInner(body[(fallback + 2)..]));
                else if (defined) result.Append(value);
                else warning?.Invoke($"{field}: variable '{name}' is undefined; expanded to an empty string");
                i = end;
            }
            return result.ToString();
        }
        return ExpandInner(text);
    }

    public static string ResolveCommand(string command, IReadOnlyDictionary<string, string?> environment, string workingDirectory)
    {
        bool windows = OperatingSystem.IsWindows();
        bool hasDirectory = command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar)
            || (windows && command.Contains('\\'));
        var extensions = ((environment.TryGetValue("PATHEXT", out var ext) ? ext : null) ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => new[] { ".com", ".exe", ".bat", ".cmd" }.Contains(e, StringComparer.OrdinalIgnoreCase)).ToArray();
        bool Usable(string path)
        {
            if (!File.Exists(path)) return false;
            if (windows) return true;
            try { return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
        if (hasDirectory || Path.IsPathRooted(command))
        {
            string path = Path.GetFullPath(command, workingDirectory);
            if (windows && !new[] { ".com", ".exe", ".bat", ".cmd" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                foreach (string extension in extensions) if (Usable(path + extension)) return path + extension;
            if (Usable(path)) return path;
            return command;
        }
        foreach (string directory in ((environment.TryGetValue("PATH", out var pathValue) ? pathValue : null) ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string dir = directory.Trim('"');
                if (!Path.IsPathRooted(dir)) continue; // never search the current directory
                string path = Path.Combine(dir, command);
                if (!windows) { if (Usable(path)) return path; }
                else if (extensions.Contains(Path.GetExtension(command), StringComparer.OrdinalIgnoreCase))
                { if (Usable(path)) return path; }
                else foreach (string extension in extensions) if (Usable(path + extension)) return path + extension;
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return command;
    }

    public static bool IsBatch(string command) => Path.GetExtension(command).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(command).Equals(".bat", StringComparison.OrdinalIgnoreCase);

    public static string BatchArguments(string command, IEnumerable<string> arguments)
    {
        string Quote(string value)
        {
            if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new CodexishFault("INVALID_ARGUMENT", "Batch file arguments cannot contain CR, LF or NUL.");
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') slashes++;
                else
                {
                    if (c == '"') { result.Append('\\', slashes); result.Append('"'); }
                    else if (c == '%') result.Append("%%cd:~,");
                    slashes = 0;
                }
                result.Append(c);
            }
            result.Append('\\', slashes); result.Append('"');
            return result.ToString();
        }
        return "/e:ON /v:OFF /d /c \"" + string.Join(' ', new[] { command }.Concat(arguments).Select(Quote)) + "\"";
    }

    public static void ConfigureStart(ProcessStartInfo start, string command, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        string resolved = ResolveCommand(command, environment, start.WorkingDirectory);
        start.ArgumentList.Clear(); start.Arguments = "";
        if (OperatingSystem.IsWindows() && IsBatch(resolved))
        { start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"); start.Arguments = BatchArguments(resolved, arguments); }
        else { start.FileName = resolved; foreach (string argument in arguments) start.ArgumentList.Add(argument); }
        start.Environment.Clear();
        foreach (var (name, value) in environment) start.Environment[name] = value;
    }

    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("userenv.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [DllImport("userenv.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyEnvironmentBlock(nint environment);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}

// Injectable reader keeps the process-static lifecycle testable without touching the registry or real user state.
internal sealed class UserEnvironmentBaseline
{
    private Dictionary<string, string?>? initial;
    internal void Initialize(Func<Dictionary<string, string?>> reader) => initial ??= reader();
    internal Dictionary<string, string?> Capture(IReadOnlyDictionary<string, string?> process,
        Func<Dictionary<string, string?>> reader)
    {
        Initialize(reader);
        var current = reader();
        Redaction.AddEnvironmentValues(current);
        return ChildEnvironment.Overlay(process, initial!, current, StringComparer.OrdinalIgnoreCase);
    }
}
