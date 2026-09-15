using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;

namespace Codexish.P0;

public sealed class ProbeRuntime : IDisposable
{
    public static readonly string[] Names = Enumerable.Range(1, 6).Select(i => $"case{i:00}.txt").ToArray();
    public string Root { get; }
    public string StateDirectory { get; }
    public NativeDesktop Desktop { get; }
    private readonly object gate = new();
    private readonly SqliteConnection db;
    private readonly FileStream instanceLock;
    private readonly Dictionary<string, Task<CallToolResult>> live = new();
    private bool stopping;
    private bool disposed;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> resources = new();
    private readonly ConcurrentDictionary<Process, byte> children = new();

    public static string CreateFixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "CODEXish-P0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".codexish-p0"), "Disposable P0 fixture v2");
        foreach (string name in Names) File.WriteAllText(Path.Combine(root, name), "0\n", new UTF8Encoding(false));
        return root;
    }

    public ProbeRuntime(string root, int? notepadPid = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        RejectReparse(Root);
        if (!File.Exists(Path.Combine(Root, ".codexish-p0")))
            throw new ArgumentException("Use an automatically created disposable fixture with --resume.");
        StateDirectory = Root + ".state";
        Directory.CreateDirectory(StateDirectory);
        RejectReparse(StateDirectory);
        instanceLock = new FileStream(Path.Combine(StateDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(StateDirectory, "p0.db"), Pooling = false }.ToString());
        db.Open();
        Sql("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; " +
            "CREATE TABLE IF NOT EXISTS invocations(id TEXT PRIMARY KEY,digest TEXT NOT NULL,status TEXT NOT NULL,result TEXT); " +
            "UPDATE invocations SET status='unknown' WHERE status IN ('queued','running');");
        Desktop = new NativeDesktop(notepadPid);
    }

    private void Sql(string sql, params (string key, object value)[] args)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
        cmd.ExecuteNonQuery();
    }

    public void Log(string tool, string phase, double? elapsedMs = null)
    {
        lock (gate) File.AppendAllText(Path.Combine(StateDirectory, "calls.jsonl"),
            JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, tool, phase, elapsed_ms = elapsedMs }) + "\n");
    }

    public async Task<CallToolResult> Invoke(string id, string tool, object args, string resource, Func<Task<CallToolResult>> action, int waitMs)
    {
        Log(tool, "called");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || waitMs < 0)
            return Reply.Error("INVALID_ARGUMENT", "Supply an invocation_id (1-128 characters) and nonnegative wait_ms.");
        // Only server-defined typed argument records are hashed; no cross-language canonicalization layer.
        string digest = Hash(Encoding.UTF8.GetBytes(tool + "\n" + JsonSerializer.Serialize(args)));
        Task<CallToolResult> task;
        lock (gate)
        {
            if (stopping) return Reply.Error("CANCELLED", "The local server is stopping.");
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT digest,status,result FROM invocations WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != digest) return Reply.Error("IDEMPOTENCY_CONFLICT", "This invocation_id belongs to different arguments.");
                if (!reader.IsDBNull(2)) return JsonSerializer.Deserialize<CallToolResult>(reader.GetString(2))!;
                if (!live.TryGetValue(id, out task!)) return Reply.Error("EXECUTION_UNKNOWN", "Previous execution has no confirmed result. Inspect effects; do not replay.", "unknown");
            }
            else
            {
                reader.Close();
                Sql("INSERT INTO invocations(id,digest,status) VALUES($id,$digest,'queued')", ("$id", id), ("$digest", digest));
                task = Task.Run(async () =>
                {
                    var queue = resources.GetOrAdd(resource, _ => new SemaphoreSlim(1));
                    await queue.WaitAsync();
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        lock (gate) Sql("UPDATE invocations SET status='running' WHERE id=$id", ("$id", id));
                        CallToolResult result;
                        try
                        {
                            lock (gate) if (stopping) throw new ProbeFault("CANCELLED", "Cancelled before execution by local shutdown.");
                            result = await action();
                        }
                        catch (ProbeFault e) { result = Reply.Error(e.Code, e.Message, e.Effects); }
                        catch (Exception e)
                        {
                            Console.Error.WriteLine($"{tool}: {e.GetType().Name}");
                            result = Reply.Error("EXECUTION_UNKNOWN", "Execution failed without confirmed effects; inspect before replanning.", "unknown");
                        }
                        string status = result.StructuredContent?.GetProperty("status").GetString() ?? "unknown";
                        lock (gate) Sql("UPDATE invocations SET status=$status,result=$result WHERE id=$id",
                            ("$status", status), ("$result", JsonSerializer.Serialize(result)), ("$id", id));
                        return result;
                    }
                    finally { queue.Release(); Log(tool, "effect_finished", clock.Elapsed.TotalMilliseconds); }
                });
                live.Add(id, task);
            }
        }
        // A response wait is not the lifetime of the accepted operation.
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(waitMs))) == task) return await task;
        return Reply.Pending(id);
    }

    public CallToolResult Inspect(string id)
    {
        Log("run_command.inspect", "called");
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT status,result FROM invocations WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return Reply.Error("NOT_FOUND", "Unknown operation_id.");
            if (!reader.IsDBNull(1)) return JsonSerializer.Deserialize<CallToolResult>(reader.GetString(1))!;
            return reader.GetString(0) == "unknown"
                ? Reply.Error("EXECUTION_UNKNOWN", "Interrupted before a durable result. No automatic replay.", "unknown") : Reply.Pending(id);
        }
    }

    private string Resolve(string name, bool write = false)
    {
        if (!Names.Contains(name, StringComparer.Ordinal) && !(name == "gui-result.txt" && !write))
            throw new ProbeFault("OUTSIDE_WORKSPACE", "Only the six named case files are writable; gui-result.txt is observation-only.");
        string path = Path.Combine(Root, name);
        RejectReparse(path);
        return path;
    }

    private static void RejectReparse(string path)
    {
        for (string? p = path; p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new ProbeFault("OUTSIDE_WORKSPACE", "Reparse points/symlinks are not supported by P0.");
    }

    private FileStream Open(string name, bool write)
    {
        var path = Resolve(name, write);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read, write ? FileShare.None : FileShare.Read); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
        { throw new ProbeFault("FILE_LOCKED", "Another handle has incompatible sharing; reread after it is released."); }
        try { NativeDesktop.CheckFileHandle(stream, Root); return stream; }
        catch { stream.Dispose(); throw; }
    }

    private static byte[] Bytes(Stream stream) { using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray(); }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static (Encoding encoding, int bom, string text) Decode(byte[] bytes)
    {
        Encoding enc = new UTF8Encoding(false, true); int bom = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            throw new ProbeFault("UNSUPPORTED_CAPABILITY", "P0 supports UTF-8 and UTF-16, not UTF-32.");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { bom = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { enc = new UnicodeEncoding(false, false, true); bom = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { enc = new UnicodeEncoding(true, false, true); bom = 2; }
        return (enc, bom, enc.GetString(bytes, bom, bytes.Length - bom));
    }

    public CallToolResult Read(string name)
    {
        Log("read_file", "called");
        using var stream = Open(name, false);
        byte[] bytes = Bytes(stream);
        var decoded = Decode(bytes);
        return Reply.Ok(new { path = name, text = decoded.text, sha256 = Hash(bytes), bytes = bytes.Length,
            encoding = decoded.encoding.WebName, bom_bytes = decoded.bom });
    }

    public CallToolResult Write(string name, string text, string expected)
    {
        using var stream = Open(name, true);
        byte[] before = Bytes(stream);
        if (!Hash(before).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new ProbeFault("FILE_CHANGED", "Expected hash does not match. Reread and merge user changes.");
        var (encoding, bom, oldText) = Decode(before);
        string stripped = oldText.Replace("\r\n", "");
        if (oldText.Contains("\r\n") && (stripped.Contains('\n') || stripped.Contains('\r')))
            throw new ProbeFault("UNSUPPORTED_CAPABILITY", "P0 does not rewrite files with mixed line endings.");
        if (stripped.Contains('\r')) throw new ProbeFault("UNSUPPORTED_CAPABILITY", "P0 does not rewrite CR-only line endings.");
        text = text.Replace("\r\n", "\n");
        if (text.Contains('\r')) throw new ProbeFault("INVALID_ARGUMENT", "Supply LF or CRLF line endings, not bare CR.");
        if (oldText.Contains("\r\n")) text = text.Replace("\n", "\r\n");
        byte[] after = before.Take(bom).Concat(encoding.GetBytes(text)).ToArray();
        string backup = Guid.NewGuid().ToString("N") + ".bak";
        using (var saved = new FileStream(Path.Combine(StateDirectory, backup), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { saved.Write(before); saved.Flush(true); }
        try { stream.Position = 0; stream.Write(after); stream.SetLength(after.Length); stream.Flush(true); }
        catch (IOException) { throw new ProbeFault("EXECUTION_UNKNOWN", $"In-place write interrupted; protected local backup: {backup}", "partial"); }
        return Reply.Ok(new { path = name, old_sha256 = Hash(before), sha256 = Hash(after), backup,
            save_mode = "exclusive_in_place_non_atomic", complete = true });
    }

    public async Task<CallToolResult> RunChild(string command)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve current executable.");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Root };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(command == "test" ? "--fixture-test" : "--fixture-sleep");
        start.ArgumentList.Add(Root);
        using var process = new Process { StartInfo = start };
        lock (gate)
        {
            if (stopping) throw new ProbeFault("CANCELLED", "Local shutdown prevents new child processes.");
            if (!process.Start()) throw new ProbeFault("EXECUTION_FAILED", "Could not start fixture child.");
            children.TryAdd(process, 0);
        }
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return Reply.Ok(new { process_state = "exited", exit_code = process.ExitCode, stdout = await stdout, stderr = await stderr,
                test_passed = command == "test" ? process.ExitCode == 0 : (bool?)null, output_complete = true });
        }
        finally { children.TryRemove(process, out _); }
    }

    public static int CheckFixture(string root)
    {
        foreach (var (name, i) in Names.Select((name, i) => (name, i + 1)))
        {
            string value = File.ReadAllText(Path.Combine(root, name)).Trim();
            if (value != i.ToString()) { Console.WriteLine($"FAIL {name}: expected integer {i}; actual {value}"); return 1; }
        }
        Console.WriteLine("PASS: all six fixture checks."); return 0;
    }

    public void Dispose()
    {
        Task<CallToolResult>[] tasks;
        lock (gate)
        {
            if (disposed) return;
            disposed = stopping = true;
            foreach (var child in children.Keys)
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            tasks = live.Values.ToArray();
        }
        try { Task.WaitAll(tasks); } finally { db.Dispose(); instanceLock.Dispose(); }
    }
}
