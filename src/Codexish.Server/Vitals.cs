using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Codexish.Server;

// A server ended from outside (when the program it was started from closes its job, or by taskkill or Task Manager)
// runs no code at all, so it cannot say why it stopped. What it can leave is a heartbeat: alive.json in state_dir names
// the process that holds the state, how it was started and when it was last alive, and says why once it stops on its
// own. The next start reads it, so a heartbeat that simply stopped is reported instead of lost. Writing it is
// best-effort: a heartbeat that cannot be written is retried with backoff and never stops the server.
public sealed class Vitals : IDisposable
{
    public const string FileName = "alive.json";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    public const string CleanStop = "stopped by CODEXish";

    public sealed class Record
    {
        [JsonPropertyName("pid")] public int Pid { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("started")] public DateTimeOffset? Started { get; set; }
        [JsonPropertyName("alive")] public DateTimeOffset? Alive { get; set; }
        [JsonPropertyName("launch")] public string? Launch { get; set; }
        [JsonPropertyName("stopped")] public string? Stopped { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly HashSet<Vitals> active = [];
    private static readonly Lazy<string> launch = new(DescribeLaunch);
    private static readonly DateTimeOffset processStarted = ProcessStarted();

    static Vitals()
    {
        // The process is about to end; the reason is all that can still be written.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => EndAll(Crashed(e.ExceptionObject));
    }

    private readonly object gate = new();
    private readonly byte[]? found;
    private readonly Record record;
    private readonly Timer timer;
    private readonly TimeSpan interval;
    private bool ended;
    private int failures;
    private string pendingReason = CleanStop;

    public string Path { get; }
    // The record found before this start, or null when there was none or it could not be read.
    public Record? Previous { get; }
    public string? PreviousUnreadable { get; }
    public string? Redirect { get; }

    // Why this server stopped, or the reason its disposal will record. Once a reason is recorded it stays: a crash or
    // the end of the Windows session is not overwritten by the disposal that may still run after it.
    public string StopReason
    {
        get { lock (gate) return record.Stopped ?? pendingReason; }
        set { lock (gate) pendingReason = value; }
    }

    // How this process was started, for the log and for the next start to quote: its chain of parent processes by
    // name, and whether a job object holds it. Names only, never command lines, which can carry secrets.
    public static string Launch => launch.Value;

    private Vitals(string stateDir, SafeFileHandle? lockHandle, TimeSpan interval)
    {
        this.interval = interval;
        Path = System.IO.Path.Combine(stateDir, FileName);
        try
        {
            if (File.Exists(Path))
            {
                found = File.ReadAllBytes(Path);
                Previous = JsonSerializer.Deserialize<Record>(found);
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            PreviousUnreadable = $"{FileName} in state_dir could not be read ({error.GetType().Name}: {FirstLine(error.Message)}), " +
                "so whether the previous CODEXish stopped on its own is unknown; it was replaced.";
        }
        if (OperatingSystem.IsWindows() && lockHandle is { IsInvalid: false } && Native.FinalPath(lockHandle) is { } final &&
            !ThroughLink(stateDir) && Redirection(System.IO.Path.Combine(stateDir, "instance.lock"), final) is { } redirected)
            Redirect = $"Windows redirects state_dir {stateDir} into {redirected.Directory}, because this CODEXish was started from " +
                $"inside the packaged app {redirected.Package}. Programs started outside that app (the sign-in shortcut, Task Scheduler, " +
                "Explorer) see a state_dir without this configuration and ledger, and the app can end this server without notice " +
                "whenever it closes, updates or switches accounts.";
        var now = DateTimeOffset.UtcNow;
        record = new Record { Pid = Environment.ProcessId, Version = CodexishRuntime.ServerVersion, Started = now, Alive = now, Launch = Launch };
        lock (gate)
        {
            // Registered before the first write, so a crash or the end of the session from here on is recorded.
            lock (active) active.Add(this);
            timer = new Timer(_ => Beat(), null, Timeout.Infinite, Timeout.Infinite);
            Persist();
        }
    }

    public static Vitals Begin(string stateDir, SafeFileHandle? lockHandle = null, TimeSpan? interval = null) =>
        new(stateDir, lockHandle, interval ?? Interval);

    // A warning when the previous server on this state_dir ended without stopping, or crashed.
    public string? PreviousEnd
    {
        get
        {
            if (Previous is not { } previous || FromThisProcess(previous)) return null;
            string who = $"The previous CODEXish on this state_dir (pid {previous.Pid}, version {previous.Version ?? "unknown"}, " +
                $"started {Local(previous.Started)})";
            string started = previous.Launch is { Length: > 0 } how ? $" It had been started {how}." : "";
            if (string.IsNullOrEmpty(previous.Stopped))
                return $"{who} ended without stopping; its heartbeat stopped at {Local(previous.Alive)}. A process ended from outside " +
                    "(when the program it was started from closes its job, or by taskkill or Task Manager) runs no code and leaves no " +
                    "other trace." + started;
            return previous.Stopped.StartsWith("crashed", StringComparison.Ordinal) ? $"{who} {previous.Stopped} at {Local(previous.Alive)}." + started : null;
        }
    }

    // For the server_start event: what the previous record said, whichever way it ended.
    public object PreviousSummary() => Previous is { } p
        ? new { pid = p.Pid, version = p.Version, started = p.Started, alive = p.Alive, launch = p.Launch, stopped = p.Stopped }
        : new { unreadable = PreviousUnreadable };

    // Records why this process stops, at once, for the cases where nothing may run after it.
    public void End(string reason) => Finish(reason, restore: false);

    // A start that never completed puts back exactly what it found, readable or not, so the end of an earlier server
    // is still reported by the start that succeeds.
    public void Abandon(string reason) => Finish(reason, restore: true);

    public static void EndAll(string reason)
    {
        Vitals[] current;
        lock (active) current = [.. active];
        foreach (var vitals in current) vitals.End(reason);
    }

    public static string Crashed(object error) => "crashed: " + Describe(error);

    public void Dispose() => End(StopReason);

    private void Finish(string reason, bool restore)
    {
        lock (gate)
        {
            if (ended) return;
            ended = true;
            record.Stopped = reason;
            record.Alive = DateTimeOffset.UtcNow;
            timer.Dispose();
            byte[] content = restore && found is not null ? found : JsonSerializer.SerializeToUtf8Bytes(record, Json);
            // The process may end right after this, so the last record gets a few quick attempts instead of a next beat.
            for (int attempt = 0; attempt < 3 && !Write(content); attempt++) Thread.Sleep(50 << attempt);
        }
        lock (active) active.Remove(this);
    }

    private void Beat()
    {
        lock (gate)
        {
            if (ended) return;
            record.Alive = DateTimeOffset.UtcNow;
            Persist();
        }
    }

    // Under gate. A write that fails, for example while another program holds the file open, is tried again with the
    // usual backoff instead of waiting a whole interval.
    private void Persist()
    {
        bool written = Write(JsonSerializer.SerializeToUtf8Bytes(record, Json));
        failures = written ? 0 : failures + 1;
        timer.Change(written ? interval : Backoff.Delay(failures), Timeout.InfiniteTimeSpan);
    }

    // A whole file or none: a process killed in the middle of a write leaves the previous heartbeat readable.
    private bool Write(byte[] content)
    {
        string temporary = Path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, Path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    // An earlier server in this very process whose stop could not be written was not ended from outside: the process
    // is still alive.
    private static bool FromThisProcess(Record previous) => previous.Pid == Environment.ProcessId && previous.Started >= processStarted;

    // A packaged (MSIX) app's processes see <AppData>\<Local|Roaming|LocalLow>\<rest> at
    // <AppData>\Local\Packages\<package>\LocalCache\<Local|Roaming|LocalLow>\<rest>. Only that exact correspondence between
    // the path asked for and the final path of the opened file counts, so a junction, a moved profile or a path chosen
    // inside the package folder is not taken for one. Windows paths compared as text, so it can be checked anywhere.
    public static (string Package, string Directory)? Redirection(string requested, string final)
    {
        string physical = final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        if (physical.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) physical = @"\\" + physical[4..];
        var comparison = StringComparison.OrdinalIgnoreCase;
        const string packages = @"\Packages\", cache = @"\LocalCache\";
        for (int at = physical.IndexOf(packages, comparison); at >= 0; at = physical.IndexOf(packages, at + 1, comparison))
        {
            string local = physical[..at];
            int cut = local.LastIndexOf('\\');
            if (cut < 0 || !local[(cut + 1)..].Equals("Local", comparison)) continue;
            int name = at + packages.Length;
            int slash = physical.IndexOf('\\', name);
            if (slash <= name || !physical.AsSpan(slash).StartsWith(cache, comparison)) continue;
            int kindStart = slash + cache.Length;
            int kindEnd = physical.IndexOf('\\', kindStart);
            if (kindEnd < 0) continue;
            string kind = physical[kindStart..kindEnd];
            if (!kind.Equals("Local", comparison) && !kind.Equals("Roaming", comparison) && !kind.Equals("LocalLow", comparison)) continue;
            if (!requested.Equals(local[..cut] + "\\" + kind + physical[kindEnd..], comparison)) return null;
            return (physical[name..slash], physical[..physical.LastIndexOf('\\')]);
        }
        return null;
    }

    // Where a file really opens when Windows redirects it into a package's LocalCache, or null. A configuration there
    // is invisible to every program Windows itself starts.
    public static string? RedirectedFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            string full = System.IO.Path.GetFullPath(path);
            if (!File.Exists(full) || ThroughLink(full)) return null;
            using var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Native.FinalPath(handle) is { } final && Redirection(full, final) is { } redirected
                ? System.IO.Path.Combine(redirected.Directory, System.IO.Path.GetFileName(full))
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    // A junction or symbolic link in the path explains a different final path by itself, wherever it points; the
    // package's view of AppData is not a link, so only a path without one can show that view.
    public static bool ThroughLink(string path)
    {
        try
        {
            for (string? at = System.IO.Path.GetFullPath(path); at is not null; at = System.IO.Path.GetDirectoryName(at))
                if ((File.Exists(at) || Directory.Exists(at)) && (File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0) return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return false;
    }

    private static string DescribeLaunch()
    {
        if (!OperatingSystem.IsWindows()) return "on a platform where CODEXish does not read its parent processes";
        try
        {
            var processes = Native.Processes();
            using var self = Process.GetCurrentProcess();
            var chain = new List<string>();
            int pid = self.Id;
            DateTime childStart = self.StartTime;
            while (chain.Count < 16 && processes.TryGetValue(pid, out var entry) && entry.Parent > 4 && entry.Parent != pid)
            {
                DateTime? parentStart = StartTime(entry.Parent);
                // A parent that exited leaves its id free for a later process; one that started after its child is not the parent.
                if (!processes.TryGetValue(entry.Parent, out var parent) || parentStart > childStart)
                {
                    chain.Add($"process {entry.Parent} (exited)");
                    break;
                }
                if (parentStart is null)
                {
                    // Its start time cannot be read, so it may be a later process that took an exited parent's id.
                    chain.Add($"{parent.Name} {entry.Parent} (unverified)");
                    break;
                }
                chain.Add($"{parent.Name} {entry.Parent}");
                pid = entry.Parent;
                childStart = parentStart.Value;
            }
            string by = chain.Count == 0 ? "with no parent process left" : "by " + string.Join(" <- ", chain);
            return by + (Native.InAnyJob() ? ", inside a job object" : ", outside any job object");
        }
        catch (Exception error) { return $"by a parent that could not be read ({error.GetType().Name})"; }
    }

    private static DateTime? StartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTimeOffset ProcessStarted()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return new DateTimeOffset(self.StartTime);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or ArgumentException)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    private static string Local(DateTimeOffset? at) =>
        at is { } value ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) : "an unknown time";

    private static string FirstLine(string text) => text.Split('\n', 2)[0].TrimEnd('\r');

    private static string Describe(object error) => error is Exception exception
        ? Redaction.Apply(exception.GetType().Name + ": " + FirstLine(exception.Message)).Text
        : "a non-exception object was thrown";
}
