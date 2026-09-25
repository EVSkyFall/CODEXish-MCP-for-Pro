using System.Globalization;

namespace Codexish.Server;

public sealed record SweepReport(DateTimeOffset At, int Rows, int Files, long Bytes, int ErrorCount, string[] Errors);

// P17. Age-based cleanup of CODEXish's own state only: never root contents, user files or Git history. Anything that
// belongs to a running or reattachable process, queued or running ledger entries, the newest checkpoint, live tokens
// and browser profiles are never deleted. A failed step is recorded and simply tried again at the next sweep.
public sealed class Retention(CodexishRuntime runtime)
{
    public static readonly TimeSpan FirstSweepAfter = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const int ReportedErrors = 20;
    private readonly object sweeping = new();
    private SweepReport? last;

    public SweepReport? Last => Volatile.Read(ref last);

    public object Describe() => new
    {
        output_days = runtime.Config.Retention.OutputDays,
        backup_days = runtime.Config.Retention.BackupDays,
        keep_forever_when = "a value of 0",
        schedule = $"{FirstSweepAfter.TotalSeconds:0} s after start, then every {Interval.TotalHours:0} hours",
        scope = "CODEXish state only: artifacts, finished ledger rows, events, exited processes, expired or revoked tokens, " +
            "older checkpoints and tray logs after output_days; pre-edit backups, quarantined ledgers and unreadable " +
            "configuration copies after backup_days. Never root contents, user files, Git history or browser profiles.",
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
            void Remove(string path)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) return;
                    long length = info.Length;
                    info.Delete();
                    files++;
                    bytes += length;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{path}: {error.GetType().Name}: {error.Message}");
                }
            }
            IEnumerable<string> Files(string directory, string pattern = "*") =>
                Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern) : [];

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
                Step("processes", () =>
                {
                    List<string> removed = [];
                    foreach (var (processId, _, _) in store.FinishedProcesses(cutoff))
                    {
                        rows += store.DeleteProcess(processId);
                        removed.Add(processId);
                    }
                    runtime.Processes.Forget(removed);
                });
                Step("artifacts", () =>
                {
                    // Output of a live process is protected even if its row could not be written.
                    var live = runtime.Processes.Live.Where(p => p.State == "running")
                        .SelectMany(p => new[] { p.StdoutArtifact, p.StderrArtifact }).ToHashSet(StringComparer.Ordinal);
                    foreach (string id in store.UnreferencedArtifacts(cutoff).Where(id => !live.Contains(id)))
                    {
                        Remove(Path.Combine(runtime.Artifacts.Directory, id + ".bin"));
                        rows += store.DeleteArtifact(id);
                    }
                    var referenced = store.ReferencedArtifacts().ToHashSet(StringComparer.Ordinal);
                    foreach (string file in Files(runtime.Artifacts.Directory, "*.bin"))
                    {
                        string id = Path.GetFileNameWithoutExtension(file);
                        if (File.GetLastWriteTimeUtc(file) < cutoffTime.UtcDateTime && !live.Contains(id) &&
                            !referenced.Contains(id) && !store.ArtifactKnown(id))
                            Remove(file);
                    }
                });
                Step("tray logs", () =>
                {
                    foreach (string file in Files(Path.Combine(state, "logs")))
                        if (File.GetLastWriteTimeUtc(file) < cutoffTime.UtcDateTime) Remove(file);
                });
            }
            if (settings.BackupDays > 0)
            {
                var cutoffTime = Cutoff(now, settings.BackupDays);
                Step("backups", () =>
                {
                    foreach (string file in Files(Path.Combine(state, "backups")))
                        if (File.GetLastWriteTimeUtc(file) < cutoffTime.UtcDateTime) Remove(file);
                });
                Step("quarantined ledgers", () =>
                {
                    foreach (string file in Files(state, "ledger.corrupt-*"))
                        if (Aged(file, "ledger.corrupt-") < cutoffTime) Remove(file);
                });
                Step("unreadable configurations", () =>
                {
                    if (runtime.Config.SourcePath is not { } source || Path.GetDirectoryName(source) is not { } directory) return;
                    string prefix = Path.GetFileName(source) + ".broken-";
                    foreach (string file in Files(directory, prefix + "*"))
                        if (Aged(file, prefix) < cutoffTime) Remove(file);
                });
            }

            var report = new SweepReport(now, rows, files, bytes, errors.Count, errors.Take(ReportedErrors).ToArray());
            Volatile.Write(ref last, report);
            try { store.Event("retention_sweep", null, new { rows, files, bytes, errors = errors.Count }); }
            catch (Exception) { /* the report above is kept either way */ }
            if (errors.Count > 0)
                Console.Error.WriteLine($"retention sweep: {errors.Count} step(s) failed and are retried at the next sweep; first: {errors[0]}");
            return report;
        }
    }

    // A retention longer than the calendar reaches back keeps everything.
    private static DateTimeOffset Cutoff(DateTimeOffset now, int days) =>
        days >= (now - DateTimeOffset.MinValue).TotalDays - 1 ? DateTimeOffset.MinValue : now.AddDays(-days);

    // A quarantined or unreadable copy carries the time it was set aside in its name; the file time is the fallback.
    private static DateTimeOffset Aged(string file, string prefix)
    {
        string name = Path.GetFileName(file);
        string stamp = name.Length > prefix.Length ? name[prefix.Length..].Split('.')[0] : "";
        return DateTime.TryParseExact(stamp, "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
    }
}
