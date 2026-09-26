using System.Globalization;
using System.Text.RegularExpressions;

namespace Codexish.Server;

public sealed record SweepReport(DateTimeOffset At, int Rows, int Files, long Bytes, int ErrorCount, string[] Errors);

// P17. Age-based cleanup of CODEXish's own state only: never root contents, user files or Git history. Anything that
// belongs to a running or reattachable process, queued or running ledger entries, the newest checkpoint, live tokens
// and browser profiles are never deleted. A failed step is recorded and simply tried again at the next sweep.
// state_dir may lie inside a root, so a file is deleted only when its whole name is one CODEXish itself gives its files;
// everything else in these folders is left alone, and no directory is ever deleted.
public sealed partial class Retention(CodexishRuntime runtime)
{
    public static readonly TimeSpan FirstSweepAfter = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const int ReportedErrors = 20;
    private readonly object sweeping = new();
    private SweepReport? last;

    [GeneratedRegex("^art_[0-9a-f]{32}$")] private static partial Regex ArtifactId();
    [GeneratedRegex(@"^art_[0-9a-f]{32}\.bin$")] private static partial Regex ArtifactFile();
    [GeneratedRegex(@"^[0-9a-f]{32}\.bak$")] private static partial Regex BackupFile();
    [GeneratedRegex(@"^tray-[0-9]{8}\.log$")] private static partial Regex TrayLog();
    // ServerConfig.UtcStamp: yyyyMMdd'T'HHmmssfff'Z'.
    [GeneratedRegex(@"^ledger\.corrupt-(?<stamp>[0-9]{8}T[0-9]{9}Z)\.db(-wal|-shm|-journal)?$")] private static partial Regex QuarantinedLedger();

    public static bool IsArtifactId(string id) => ArtifactId().IsMatch(id);
    public static bool IsArtifactFile(string name) => ArtifactFile().IsMatch(name);
    public static bool IsBackupFile(string name) => BackupFile().IsMatch(name);
    public static bool IsTrayLog(string name) => TrayLog().IsMatch(name);
    public static bool IsQuarantinedLedger(string name) => QuarantinedLedger().IsMatch(name);

    // <config file name>.broken-<stamp>, with the -2, -3 ... suffix ServerConfig.KeepBroken adds on a collision.
    public static Regex UnreadableConfiguration(string configFileName) =>
        new("^" + Regex.Escape(configFileName) + @"\.broken-(?<stamp>[0-9]{8}T[0-9]{9}Z)(-[0-9]+)?$");

    public SweepReport? Last => Volatile.Read(ref last);

    public object Describe() => new
    {
        output_days = runtime.Config.Retention.OutputDays,
        backup_days = runtime.Config.Retention.BackupDays,
        keep_forever_when = "a value of 0",
        schedule = $"{FirstSweepAfter.TotalSeconds:0} s after start, then every {Interval.TotalHours:0} hours",
        scope = "CODEXish state only, and only files named the way CODEXish names them: artifacts, finished ledger rows, " +
            "events, exited processes, expired or revoked tokens, older checkpoints, client registrations that never signed in " +
            "and tray logs after output_days; " +
            "pre-edit backups, quarantined ledgers and unreadable configuration copies after backup_days. Never root " +
            "contents, other files, directories, Git history or browser profiles.",
        last_sweep = Last is { } sweep
            ? new { at = sweep.At, rows = sweep.Rows, files = sweep.Files, bytes = sweep.Bytes, errors = sweep.ErrorCount, error_samples = sweep.Errors }
            : null
    };

    public async Task RunAsync(CancellationToken token)
    {
        try { await Task.Delay(FirstSweepAfter, token); }
        catch (OperationCanceledException) { return; }
        while (!token.IsCancellationRequested)
        {
            try { Sweep(DateTimeOffset.UtcNow); }
            catch (Exception error) { Console.Error.WriteLine($"retention sweep failed: {error.GetType().Name}: {error.Message}"); }
            try { await Task.Delay(Interval, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    public SweepReport Sweep(DateTimeOffset now)
    {
        lock (sweeping)
        {
            now = now.ToUniversalTime();
            var store = runtime.Store;
            var settings = runtime.Config.Retention;
            string state = runtime.Config.StateDir;
            int rows = 0, files = 0;
            long bytes = 0;
            List<string> errors = [];
            void Step(string name, Action action)
            {
                try { action(); }
                catch (Exception error) { errors.Add($"{name}: {error.GetType().Name}: {error.Message}"); }
            }
            // True when the file is gone afterwards, whether this call removed it or it was already absent.
            bool Remove(string path)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) return !Directory.Exists(path);
                    long length = info.Length;
                    info.Delete();
                    files++;
                    bytes += length;
                    return true;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{path}: {error.GetType().Name}: {error.Message}");
                    return false;
                }
            }
            // Only files, and only those whose whole name matches; subdirectories are never entered.
            IEnumerable<string> Named(string directory, Func<string, bool> ours) =>
                Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Where(f => ours(Path.GetFileName(f)))
                    : [];

            Step("idle jobs", () => runtime.Processes.ReleaseIdleJobs());
            if (settings.OutputDays > 0)
            {
                var cutoffTime = Cutoff(now, settings.OutputDays);
                string cutoff = cutoffTime.ToString("o");
                Step("undated rows", () => store.StampUndatedRows(now.ToString("o")));
                Step("invocations", () => rows += store.DeleteFinishedInvocations(cutoff));
                Step("events", () => rows += store.DeleteEvents(cutoff));
                Step("checkpoints", () => rows += store.DeleteOldCheckpoints(cutoff));
                Step("tokens", () => rows += store.DeleteDeadTokens(cutoff, runtime.Config.OAuth.RefreshTokenDays > 0));
                Step("registered clients", () => rows += store.DeleteUnusedClients(cutoff));
                Step("processes", () =>
                {
                    List<string> removed = [];
                    foreach (var (processId, _, _) in store.FinishedProcesses(cutoff))
                    {
                        // Its job still holds live descendants: the row and its output stay until they are gone.
                        if (runtime.Processes.HoldsJob(processId)) continue;
                        rows += store.DeleteProcess(processId);
                        removed.Add(processId);
                    }
                    runtime.Processes.Forget(removed);
                });
                Step("artifacts", () =>
                {
                    // Output of a live process is protected even if its row could not be written.
                    var live = runtime.Processes.Live.Where(p => p.State == "running" || p.JobHandle != 0)
                        .SelectMany(p => new[] { p.StdoutArtifact, p.StderrArtifact }).ToHashSet(StringComparer.Ordinal);
                    foreach (string id in store.UnreferencedArtifacts(cutoff).Where(id => IsArtifactId(id) && !live.Contains(id)))
                        // The row goes only once its file is gone, so a file that could not be removed is retried next time.
                        if (Remove(Path.Combine(runtime.Artifacts.Directory, id + ".bin"))) rows += store.DeleteArtifact(id);
                    var referenced = store.ReferencedArtifacts().ToHashSet(StringComparer.Ordinal);
                    foreach (string file in Named(runtime.Artifacts.Directory, IsArtifactFile))
                    {
                        string id = Path.GetFileNameWithoutExtension(file);
                        if (File.GetLastWriteTimeUtc(file) < cutoffTime.UtcDateTime && !live.Contains(id) &&
                            !referenced.Contains(id) && !store.ArtifactKnown(id))
                            Remove(file);
                    }
                });
                Step("tray logs", () =>
                {
                    foreach (string file in Named(Path.Combine(state, "logs"), IsTrayLog))
                        if (File.GetLastWriteTimeUtc(file) < cutoffTime.UtcDateTime) Remove(file);
                });
            }
            if (settings.BackupDays > 0)
            {
                var cutoffTime = Cutoff(now, settings.BackupDays);
                Step("backups", () =>
                {
                    foreach (string file in Named(Path.Combine(state, "backups"), IsBackupFile))
                        if (File.GetLastWriteTimeUtc(file) < cutoffTime.UtcDateTime) Remove(file);
                });
                Step("quarantined ledgers", () =>
                {
                    foreach (string file in Named(state, IsQuarantinedLedger))
                        if (Stamped(QuarantinedLedger().Match(Path.GetFileName(file))) < cutoffTime) Remove(file);
                });
                Step("unreadable configurations", () =>
                {
                    if (runtime.Config.SourcePath is not { } source || Path.GetDirectoryName(source) is not { } directory) return;
                    var pattern = UnreadableConfiguration(Path.GetFileName(source));
                    foreach (string file in Named(directory, pattern.IsMatch))
                        if (Stamped(pattern.Match(Path.GetFileName(file))) < cutoffTime) Remove(file);
                });
            }

            var report = new SweepReport(now, rows, files, bytes, errors.Count, errors.Take(ReportedErrors).ToArray());
            Volatile.Write(ref last, report);
            store.Event("retention_sweep", null, new { rows, files, bytes, errors = errors.Count });
            if (errors.Count > 0)
                Console.Error.WriteLine($"retention sweep: {errors.Count} step(s) failed and are retried at the next sweep; first: {errors[0]}");
            return report;
        }
    }

    // A retention longer than the calendar reaches back keeps everything.
    private static DateTimeOffset Cutoff(DateTimeOffset now, int days) =>
        days >= (now - DateTimeOffset.MinValue).TotalDays - 1 ? DateTimeOffset.MinValue : now.AddDays(-days);

    // A quarantined or unreadable copy carries the time it was set aside in its name. A stamp that is not a real
    // time is kept forever.
    private static DateTimeOffset Stamped(Match match) =>
        DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : DateTimeOffset.MaxValue;
}
