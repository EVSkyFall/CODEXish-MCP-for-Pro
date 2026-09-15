using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;

namespace Codexish.P0.V1;

public sealed record RootGrant(string Id, string Path, bool Read = true, bool Write = true, bool Shell = true);
public sealed class ServerConfig
{
    public int Port { get; set; } = 3000;
    public int ControlPort { get; set; } = 3001;
    public string StateDirectory { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codexish");
    public RootGrant[] Roots { get; set; } = [];
    public string[] AllowedHosts { get; set; } = [];
    public string[] AllowedOrigins { get; set; } = [];
    public string PublicUrl { get; set; } = "";
    public string ClientId { get; set; } = "codexish-chatgpt";
    public string ClientSecret { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string[] RedirectUris { get; set; } = [];
    public string GitExecutable { get; set; } = "";
    public bool NoAuth { get; set; }
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static ServerConfig Load(string path) => JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), Json) ?? throw new ArgumentException("Invalid config.");
    public void Validate()
    {
        if (Port is < 0 or > 65535 || ControlPort is < 0 or > 65535 || (Port != 0 && Port == ControlPort))
            throw new ArgumentException("Use distinct MCP/control ports.");
        StateDirectory = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(StateDirectory));
        Roots = Roots.Select(r => r with { Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(r.Path)) }).ToArray();
        if (Roots.Length == 0 || Roots.Any(r => string.IsNullOrWhiteSpace(r.Id)) || Roots.Select(r => r.Id).Distinct().Count() != Roots.Length)
            throw new ArgumentException("Configure uniquely named roots.");
        foreach (var root in Roots)
        {
            if (!Directory.Exists(root.Path)) throw new ArgumentException("Root directory must exist: " + root.Id);
            FileFence.NoReparse(root.Path);
            if (FileFence.Within(root.Path, StateDirectory) || FileFence.Within(StateDirectory, root.Path))
                throw new ArgumentException("State directory and roots must not overlap.");
        }
        if (GitExecutable.Length != 0 && (!System.IO.Path.IsPathFullyQualified(GitExecutable) || !File.Exists(GitExecutable)))
            throw new ArgumentException("git_executable must name an existing absolute executable path.");
        if (!NoAuth)
        {
            if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var u) || u.Scheme != "https" || u.UserInfo.Length != 0 || u.AbsolutePath != "/" || u.Query.Length != 0 || u.Fragment.Length != 0)
                throw new ArgumentException("public_url must be an HTTPS origin for OAuth metadata.");
            PublicUrl = u.GetLeftPart(UriPartial.Authority);
            if (ClientId.Length == 0 || ClientSecret.Length == 0 || !PasswordHash.StartsWith("pbkdf2-sha256$", StringComparison.Ordinal) || RedirectUris.Length == 0)
                throw new ArgumentException("Configure client credentials, password_hash and exact redirect_uris.");
            foreach (string redirect in RedirectUris)
                if (!Uri.TryCreate(redirect, UriKind.Absolute, out var r) || r.UserInfo.Length != 0 || r.Fragment.Length != 0 ||
                    (r.Scheme != "https" && !(r.Scheme == "http" && r.IsLoopback)))
                    throw new ArgumentException("Redirects must use HTTPS or a loopback HTTP URI.");
        }
        _ = new ProbeAccessPolicy(AllowedHosts, AllowedOrigins);
    }
}

// Reuses the P0 Reply envelope and encoding primitives; not another server project.
public static class VReply
{
    public static CallToolResult Pack(string status, object? data = null, object? error = null) => Reply.Pack(status, data, error, "v1.0");
    public static CallToolResult Ok(object data) => Pack("succeeded", data);
    public static CallToolResult Error(string code, string message, string effects = "none", object? data = null) =>
        Pack(code == "EXECUTION_UNKNOWN" ? "unknown" : code == "CANCELLED" ? "cancelled" : "failed", data,
            new { code, message, side_effects = effects, retryable = code == "FILE_LOCKED", recovery = new { action = effects == "none" ? "correct_arguments_or_reread" : "inspect_effects_do_not_replay" } });
    public static CallToolResult From(Exception e) => e switch
    {
        ProbeFault f => Error(f.Code, f.Message, f.Effects),
        FileNotFoundException or DirectoryNotFoundException => Error("NOT_FOUND", "Requested path does not exist."),
        UnauthorizedAccessException => Error("PERMISSION_DENIED", "The operating system denied access."),
        IOException io when (io.HResult & 0xffff) is 32 or 33 => Error("FILE_LOCKED", "Release the conflicting handle, then reread."),
        ArgumentException or FormatException or JsonException or NotSupportedException => Error("INVALID_ARGUMENT", "Invalid argument format."),
        _ => Error("EXECUTION_UNKNOWN", "Inspect the operation and local state before replanning.", "unknown")
    };
    public static CallToolResult Guard(Func<CallToolResult> action) { try { return action(); } catch (Exception e) { return From(e); } }
    public static string Status(CallToolResult r) => r.StructuredContent!.Value.GetProperty("status").GetString()!;
    public static JsonElement Data(CallToolResult r) => r.StructuredContent!.Value.GetProperty("data");
}

public sealed class Store : IDisposable
{
    public object Gate { get; } = new();
    public SqliteConnection Db { get; }
    private readonly FileStream instance;
    public Store(string directory)
    {
        Directory.CreateDirectory(directory); FileFence.NoReparse(directory);
        instance = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "codexish.db"), Pooling = false }.ToString());
        Db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; " +
            "CREATE TABLE IF NOT EXISTS invocations(session TEXT,id TEXT,digest TEXT,status TEXT,result TEXT,PRIMARY KEY(session,id)); " +
            "CREATE TABLE IF NOT EXISTS processes(id TEXT PRIMARY KEY,session TEXT,data TEXT); " +
            "CREATE TABLE IF NOT EXISTS artifacts(id TEXT PRIMARY KEY,session TEXT,path TEXT,complete INTEGER); " +
            "CREATE TABLE IF NOT EXISTS events(seq INTEGER PRIMARY KEY AUTOINCREMENT,utc TEXT,session TEXT,kind TEXT,data TEXT); " +
            "CREATE TABLE IF NOT EXISTS tokens(hash TEXT PRIMARY KEY,kind TEXT,session TEXT,expires INTEGER,used INTEGER,data TEXT); " +
            "UPDATE invocations SET status='cancelled' WHERE status='queued'; " +
            "UPDATE invocations SET status='unknown' WHERE status='running';");
    }
    public int Exec(string sql, params (string, object?)[] values)
    {
        lock (Gate) { using var c = Command(sql, values); return c.ExecuteNonQuery(); }
    }
    public SqliteCommand Command(string sql, params (string, object?)[] values)
    {
        var c = Db.CreateCommand(); c.CommandText = sql;
        foreach (var (key, value) in values) c.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return c;
    }
    public string? Scalar(string sql, params (string, object?)[] values)
    {
        lock (Gate) { using var c = Command(sql, values); return c.ExecuteScalar() as string; }
    }
    public void Event(string session, string kind, object data)
    {
        // Diagnostic/event failure must never overwrite a committed business result.
        try { Exec("INSERT INTO events(utc,session,kind,data) VALUES($u,$s,$k,$d)",
            ("$u", DateTimeOffset.UtcNow.ToString("O")), ("$s", session), ("$k", kind), ("$d", JsonSerializer.Serialize(data))); }
        catch (Exception e) when (e is SqliteException or IOException or ObjectDisposedException) { Console.Error.WriteLine("event_persist_failed: " + e.GetType().Name); }
    }
    public void Dispose() { Db.Dispose(); instance.Dispose(); }
}

public sealed class Operations(Store store)
{
    private sealed record Running(string Digest, TaskCompletionSource<CallToolResult> Completion, CancellationTokenSource Cancel);
    private readonly Dictionary<(string, string), Running> live = new();
    private readonly Dictionary<(string, string), CallToolResult> uncertain = new();
    private readonly Dictionary<string, Task> tails = new(StringComparer.Ordinal);
    private TaskCompletionSource<bool> resumed = Completed();
    private bool paused;
    private bool closing;
    private static TaskCompletionSource<bool> Completed() { var t = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); t.SetResult(true); return t; }
    public bool Paused { get { lock (store.Gate) return paused; } }
    public void Pause(bool value)
    {
        lock (store.Gate) { if (value && !paused) resumed = new(TaskCreationOptions.RunContinuationsAsynchronously); paused = value; if (!value) resumed.TrySetResult(true); }
    }
    public async Task<CallToolResult> Invoke(string session, string id, string tool, object args, string[] resources,
        Func<CancellationToken, Task<CallToolResult>> effect, int waitMs = 1000, bool bypassPause = false)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || waitMs < 0) return VReply.Error("INVALID_ARGUMENT", "invocation_id and nonnegative wait_ms required.");
        string digest = ProbeRuntime.Hash(Encoding.UTF8.GetBytes(tool + "\n" + JsonSerializer.Serialize(args)));
        var key = (session, id); Task<CallToolResult> resultTask;
        lock (store.Gate)
        {
            using var c = store.Command("SELECT digest,status,result FROM invocations WHERE session=$s AND id=$i", ("$s", session), ("$i", id));
            using var r = c.ExecuteReader();
            if (r.Read())
            {
                if (r.GetString(0) != digest) return VReply.Error("IDEMPOTENCY_CONFLICT", "Same ID has different effect arguments.");
                if (uncertain.TryGetValue(key, out var failed)) return failed;
                if (!r.IsDBNull(2)) return JsonSerializer.Deserialize<CallToolResult>(r.GetString(2))!;
                if (!live.TryGetValue(key, out var run)) return Recovered(id, r.GetString(1));
                resultTask = run.Completion.Task;
            }
            else
            {
                r.Close();
                if (closing) return VReply.Error("CANCELLED", "Server is shutting down.");
                if (paused && !bypassPause) return VReply.Pack("paused", new { reason = "PAUSED", accepted = false, operation_id = id });
                try { store.Exec("INSERT INTO invocations VALUES($s,$i,$d,'queued',NULL)", ("$s", session), ("$i", id), ("$d", digest)); }
                catch (SqliteException) { return VReply.Error("PERSIST_FAILED", "Request was not accepted; no effect was started."); }
                var run = new Running(digest, new(TaskCreationOptions.RunContinuationsAsynchronously), new());
                live.Add(key, run);
                string[] keys = resources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                Task prior = Task.WhenAll(keys.Select(k => tails.GetValueOrDefault(k, Task.CompletedTask)));
                // Acceptance and tail append share a lock. A cancelled queued result may finish early,
                // but its resource barrier MUST still include every predecessor's completion.
                Task barrier = Task.WhenAll(prior, run.Completion.Task);
                foreach (string resource in keys) tails[resource] = barrier;
                resultTask = run.Completion.Task;
                _ = Task.Run(async () =>
                {
                    CallToolResult result; bool started = false;
                    try
                    {
                        await prior.WaitAsync(run.Cancel.Token);
                        while (true)
                        {
                            Task ready;
                            lock (store.Gate)
                            {
                                run.Cancel.Token.ThrowIfCancellationRequested();
                                if (bypassPause || !paused)
                                {
                                    store.Exec("UPDATE invocations SET status='running' WHERE session=$s AND id=$i", ("$s", session), ("$i", id));
                                    started = true; break;
                                }
                                ready = resumed.Task;
                            }
                            await ready.WaitAsync(run.Cancel.Token);
                        }
                        result = await effect(run.Cancel.Token);
                    }
                    catch (OperationCanceledException) { result = VReply.Error("CANCELLED", "Cancellation requested; inspect any started effects.", started ? "partial" : "none"); }
                    catch (SqliteException) when (!started) { result = VReply.Error("PERSIST_FAILED", "No effect was started."); }
                    catch (Exception e) { result = VReply.From(e); }
                    lock (store.Gate)
                    {
                        try { store.Exec("UPDATE invocations SET status=$st,result=$r WHERE session=$s AND id=$i",
                            ("$st", VReply.Status(result)), ("$r", JsonSerializer.Serialize(result)), ("$s", session), ("$i", id)); }
                        catch (SqliteException)
                        {
                            result = VReply.Error("EXECUTION_UNKNOWN", "persist_failed: result commit failed; never replay this invocation.", started ? "unknown" : "none",
                                new { operation_id = id, reason = "persist_failed", state_source = "memory" });
                            uncertain[key] = result;
                        }
                        run.Completion.TrySetResult(result);
                        live.Remove(key);
                        run.Cancel.Dispose();
                    }
                    store.Event(session, tool, new { operation_id = id, status = VReply.Status(result) });
                    await barrier;
                    lock (store.Gate)
                        foreach (string resource in keys) if (ReferenceEquals(tails.GetValueOrDefault(resource), barrier)) tails.Remove(resource);
                });
            }
        }
        if (resultTask.IsCompleted || await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromMilliseconds(waitMs))) == resultTask) return await resultTask;
        return VReply.Pack("running", new { operation_id = id, next_tool = "operation.inspect" });
    }
    private static CallToolResult Recovered(string id, string status) => status == "cancelled"
        ? VReply.Error("CANCELLED", "Queued operation was never started before restart.")
        : VReply.Error("EXECUTION_UNKNOWN", "Interrupted operation has no confirmed result; inspect effects.", "unknown", new { operation_id = id });
    public CallToolResult Inspect(string session, string id)
    {
        lock (store.Gate)
        {
            if (uncertain.TryGetValue((session, id), out var u)) return u;
            using var c = store.Command("SELECT status,result FROM invocations WHERE session=$s AND id=$i", ("$s", session), ("$i", id));
            using var r = c.ExecuteReader();
            if (!r.Read()) return VReply.Error("NOT_FOUND", "No operation in this session.");
            if (!r.IsDBNull(1)) return JsonSerializer.Deserialize<CallToolResult>(r.GetString(1))!;
            if (live.ContainsKey((session, id))) return VReply.Pack(paused ? "paused" : r.GetString(0), new { operation_id = id });
            return Recovered(id, r.GetString(0));
        }
    }
    public CallToolResult Cancel(string session, string id)
    {
        lock (store.Gate)
        {
            // The tool wrapper must not report cancellation_requested=true for an unknown target.
            if (store.Scalar("SELECT id FROM invocations WHERE session=$s AND id=$i", ("$s", session), ("$i", id)) is null)
                throw new ProbeFault("NOT_FOUND", "No operation in this session.");
            if (live.TryGetValue((session, id), out var r)) r.Cancel.Cancel();
            return Inspect(session, id);
        }
    }
    public Task Close()
    {
        lock (store.Gate) { closing = true; foreach (var r in live.Values) r.Cancel.Cancel(); resumed.TrySetResult(true); return Task.WhenAll(live.Values.Select(x => x.Completion.Task)); }
    }
}

public static partial class Redaction
{
    [GeneratedRegex(@"(?i)\b(api[_-]?key|access[_-]?token|refresh[_-]?token|password|secret)\s*[:=]\s*([^\s,;]+)")]
    private static partial Regex Named();
    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{16,}\b")]
    private static partial Regex Key();
    private static readonly Lazy<string[]> EnvironmentSecrets = new(() => Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .Where(x => Regex.IsMatch((string)x.Key, "(?i)(TOKEN|SECRET|PASSWORD|API.?KEY|PRIVATE.?KEY)"))
        .Select(x => x.Value?.ToString() ?? "").Where(x => x.Length > 0).OrderByDescending(x => x.Length).ToArray());
    public static string Text(string text) => WithSecrets(text, EnvironmentSecrets.Value);
    internal static string WithSecrets(string text, IEnumerable<string> values)
    {
        foreach (string value in values.Where(x => x.Length != 0).OrderByDescending(x => x.Length)) text = text.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        return Key().Replace(Named().Replace(text, "$1=[REDACTED]"), "[REDACTED]");
    }
}
