using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Codexish.Server;

public sealed record InvocationRow(string Id, string Digest, string Tool, string Status, string? Result);

public sealed record ProcessRow(string ProcessId, int Pid, long StartTime, string State, int? ExitCode,
    string Lifetime, string RootId, string? StdoutArtifact, string? StderrArtifact);

public sealed record ArtifactRow(string Id, string Mime, long Bytes, string? Sha256, string Path,
    int Complete, int Generation, string CreatedAt);

public sealed record TokenRow(string Hash, string Kind, string ClientId, string Audience, DateTimeOffset ExpiresAt,
    bool Revoked, bool PkceUsed, string Family, string Scope);

public sealed record LedgerRebuild(DateTimeOffset At, string[] Quarantined, string Reason);

// A client that registered itself through /register (RFC 7591). The secret is stored only as a hash.
public sealed record ClientRow(string ClientId, string? SecretHash, string[] RedirectUris, string AuthMethod, string? Name,
    string CreatedAt, string? LastSignedInAt);

// D10: one SQLite file in the state directory, WAL, every table the slice needs. All access is serialized on
// one connection; the ledger relies on the same lock to make acceptance order observable.
public sealed class Store : IDisposable
{
    // The status of an invocation row whose stored values cannot be read.
    public const string Unreadable = "unreadable";

    private readonly SqliteConnection db;
    private readonly object gate = new();
    private readonly string path;
    private readonly TimeSpan transientBudget = TimeSpan.FromSeconds(new SqliteConnectionStringBuilder().DefaultTimeout);
    private int malformedRows;
    private bool disposed;

    // Self-test injection point for D9's persist-failure path. Never set outside --self-test.
    public bool FailNextResultWrite { get; set; }

    // Set when the database was unreadable at startup and a fresh one replaced it.
    public LedgerRebuild? Rebuilt { get; }
    public List<string> MigrationErrors { get; } = [];
    public int MalformedRowsSkipped => Volatile.Read(ref malformedRows);

    private static readonly (string Table, string Column, string Definition)[] Migrations =
    [
        // Databases written by earlier builds lack these columns; adding one that exists is a no-op.
        ("tokens", "family", "TEXT NOT NULL DEFAULT ''"),
        ("tokens", "scope", "TEXT NOT NULL DEFAULT ''"),
        ("tokens", "revoked_at", "TEXT"),
        ("processes", "updated_at", "TEXT")
    ];

    public Store(string path)
    {
        this.path = path;
        SqliteConnection? connection = null;
        try
        {
            connection = Open(path);
            Initialize(connection);
            // quick_check reads every page, so a damaged database is found here rather than in the middle of a call.
            string check = Scalar(connection, "PRAGMA quick_check") ?? "";
            if (!check.Equals("ok", StringComparison.OrdinalIgnoreCase))
                throw new SqliteException("quick_check reported: " + check, 11);
        }
        catch (SqliteException error) when (IsCorrupt(error))
        {
            connection?.Dispose();
            connection = null;
            Rebuilt = Quarantine(error.Message);
            connection = Open(path);
            MigrationErrors.Clear();
            Initialize(connection);
        }
        db = connection;
        if (Rebuilt is { } rebuilt)
            Event("ledger_rebuilt", null, new { at = rebuilt.At, quarantined = rebuilt.Quarantined, reason = rebuilt.Reason });
        foreach (string problem in MigrationErrors) Event("migration_failed", null, new { problem });
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    // Every statement, the migrations included, goes through WithRetry, so a lock held for a moment by another
    // connection cannot leave a column missing.
    private void Initialize(SqliteConnection connection)
    {
        RunRetried(connection, """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS invocations(id TEXT PRIMARY KEY, digest TEXT NOT NULL, tool TEXT NOT NULL,
                status TEXT NOT NULL, result TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS processes(process_id TEXT PRIMARY KEY, pid INTEGER NOT NULL, start_time INTEGER NOT NULL,
                state TEXT NOT NULL, exit_code INTEGER, lifetime TEXT NOT NULL, root_id TEXT NOT NULL,
                stdout_artifact TEXT, stderr_artifact TEXT);
            CREATE TABLE IF NOT EXISTS artifacts(id TEXT PRIMARY KEY, mime TEXT NOT NULL, bytes INTEGER NOT NULL,
                sha256 TEXT, path TEXT NOT NULL, complete INTEGER NOT NULL, generation INTEGER NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(seq INTEGER PRIMARY KEY AUTOINCREMENT, utc TEXT NOT NULL, kind TEXT NOT NULL,
                ref TEXT, json TEXT);
            CREATE TABLE IF NOT EXISTS tokens(hash TEXT PRIMARY KEY, kind TEXT NOT NULL, client_id TEXT NOT NULL,
                audience TEXT NOT NULL, expires_at TEXT NOT NULL, revoked INTEGER NOT NULL, pkce_used INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS checkpoints(seq INTEGER PRIMARY KEY AUTOINCREMENT, utc TEXT NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS clients(client_id TEXT PRIMARY KEY, secret_hash TEXT, redirect_uris TEXT NOT NULL,
                auth_method TEXT NOT NULL, name TEXT, created_at TEXT NOT NULL, last_signed_in_at TEXT);
            """);
        foreach (var (table, column, definition) in Migrations)
        {
            try { RunRetried(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition}"); }
            catch (SqliteException error) when (error.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
            catch (SqliteException error) when (!IsCorrupt(error))
            {
                // Only the affected column is missing afterwards; everything else keeps working.
                MigrationErrors.Add($"Adding {table}.{column} failed: {error.Message}");
            }
        }
    }

    // The damaged files are renamed, never deleted, so they can still be inspected or recovered by hand.
    private LedgerRebuild Quarantine(string reason)
    {
        var at = DateTimeOffset.UtcNow;
        string stamp = ServerConfig.UtcStamp(at);
        string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        List<string> moved = [];
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            string source = path + suffix;
            if (!File.Exists(source)) continue;
            string target = System.IO.Path.Combine(directory, $"ledger.corrupt-{stamp}{System.IO.Path.GetExtension(path)}{suffix}");
            File.Move(source, target);
            moved.Add(System.IO.Path.GetFileName(target));
        }
        string message = $"The ledger database could not be read ({reason}); it was moved aside as {string.Join(", ", moved)} and a fresh one was created.";
        Console.Error.WriteLine("WARNING: " + message);
        return new LedgerRebuild(at, moved.ToArray(), reason);
    }

    // SQLITE_CORRUPT and SQLITE_NOTADB.
    public static bool IsCorrupt(SqliteException error) => (error.SqliteErrorCode & 0xff) is 11 or 26;

    // SQLITE_BUSY, SQLITE_LOCKED and SQLITE_IOERR, the codes a scanner or another reader can cause for a moment.
    public static bool IsTransient(int code) => (code & 0xff) is 5 or 6 or 10;

    // A value that cannot be read back from a row, as opposed to a failure of the database itself.
    public static bool IsMalformedValue(Exception error) =>
        error is FormatException or InvalidCastException or OverflowException or InvalidOperationException or ArgumentException &&
        error is not SqliteException;

    // Retried with backoff for as long as the existing command timeout allows; after that the error surfaces through the
    // caller's own contract (persist_failed or unknown). Nothing is remembered between calls.
    internal static T WithRetry<T>(Func<T> action, TimeSpan budget, Action<TimeSpan>? sleep = null)
    {
        var watch = Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(25);
        while (true)
        {
            try { return action(); }
            catch (SqliteException error) when (IsTransient(error.SqliteErrorCode) && watch.Elapsed < budget)
            {
                (sleep ?? Thread.Sleep)(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
        }
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void RunRetried(SqliteConnection connection, string sql) =>
        WithRetry(() => { Run(connection, sql); return 0; }, transientBudget);

    private static string? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    private static string Now => DateTimeOffset.UtcNow.ToString("o");

    public void Execute(string sql, params (string Key, object? Value)[] args) => ExecuteCount(sql, args);

    public int ExecuteCount(string sql, params (string Key, object? Value)[] args)
    {
        lock (gate)
        {
            return WithRetry(() =>
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
                return command.ExecuteNonQuery();
            }, transientBudget);
        }
    }

    // Used by the self-test to make a real SQLite write fail instead of only an injected flag.
    public void Pragma(string pragma) => Execute(pragma);

    private T? Read<T>(string sql, Func<SqliteDataReader, T> map, params (string Key, object? Value)[] args) where T : class
    {
        lock (gate)
        {
            return WithRetry(() =>
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
                using var reader = command.ExecuteReader();
                return reader.Read() ? map(reader) : null;
            }, transientBudget);
        }
    }

    // A row whose values cannot be read is skipped and counted, so it never takes the rest of the table with it.
    private List<T> ReadAll<T>(string sql, Func<SqliteDataReader, T> map, params (string Key, object? Value)[] args)
    {
        lock (gate)
        {
            return WithRetry(() =>
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
                using var reader = command.ExecuteReader();
                List<T> rows = [];
                while (reader.Read())
                {
                    try { rows.Add(map(reader)); }
                    catch (Exception error) when (IsMalformedValue(error)) { Interlocked.Increment(ref malformedRows); }
                }
                return rows;
            }, transientBudget);
        }
    }

    // Events are diagnostics: one that cannot be written goes to stderr and never aborts startup, a tool call or a
    // process.
    public void Event(string kind, string? reference, object payload)
    {
        try
        {
            Execute("INSERT INTO events(utc,kind,ref,json) VALUES($utc,$kind,$ref,$json)",
                ("$utc", Now), ("$kind", kind), ("$ref", reference), ("$json", JsonSerializer.Serialize(payload)));
        }
        catch (Exception error)
        {
            try { Console.Error.WriteLine($"event {kind} was not recorded: {error.GetType().Name}: {error.Message}"); }
            catch (IOException) { /* nowhere left to report it */ }
        }
    }

    public List<(long Seq, string Utc, string Kind, string? Reference, string? Json)> Events(string? kind = null) =>
        ReadAll("SELECT seq,utc,kind,ref,json FROM events WHERE ($kind IS NULL OR kind=$kind) ORDER BY seq",
            r => (r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4)), ("$kind", kind));

    // An invocation row whose values cannot be read comes back with status "unreadable", so the ledger can report it
    // instead of failing the call.
    public InvocationRow? Invocation(string id)
    {
        try
        {
            return Read("SELECT id,digest,tool,status,result FROM invocations WHERE id=$id",
                r => new InvocationRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4)), ("$id", id));
        }
        catch (Exception error) when (IsMalformedValue(error))
        {
            Interlocked.Increment(ref malformedRows);
            return new InvocationRow(id, "", "", Unreadable, null);
        }
    }

    public void InsertInvocation(string id, string digest, string tool) =>
        Execute("INSERT INTO invocations(id,digest,tool,status,result,created_at,updated_at) VALUES($id,$d,$t,'queued',NULL,$n,$n)",
            ("$id", id), ("$d", digest), ("$t", tool), ("$n", Now));

    public void UpdateInvocationStatus(string id, string status) =>
        Execute("UPDATE invocations SET status=$s,updated_at=$n WHERE id=$id", ("$s", status), ("$n", Now), ("$id", id));

    public void CompleteInvocation(string id, string status, string result)
    {
        if (FailNextResultWrite)
        {
            FailNextResultWrite = false;
            throw new SqliteException("Injected result-persistence failure (self-test only).", 8);
        }
        Execute("UPDATE invocations SET status=$s,result=$r,updated_at=$n WHERE id=$id",
            ("$s", status), ("$r", result), ("$n", Now), ("$id", id));
    }

    // D9 restart semantics for invocations: nothing queued ever ran, so it is cancelled with no effects;
    // anything running may or may not have applied its effect, so it stays unknown for inspection.
    public (int Cancelled, int Unknown) RecoverInvocations()
    {
        var queued = ReadAll("SELECT id FROM invocations WHERE status='queued' AND result IS NULL", r => r.GetString(0));
        var running = ReadAll("SELECT id FROM invocations WHERE status='running' AND result IS NULL", r => r.GetString(0));
        Execute("UPDATE invocations SET status='cancelled',updated_at=$n WHERE status='queued' AND result IS NULL", ("$n", Now));
        Execute("UPDATE invocations SET status='unknown',updated_at=$n WHERE status='running' AND result IS NULL", ("$n", Now));
        if (queued.Count > 0 || running.Count > 0)
            Event("restart_recovery", null, new { cancelled = queued, unknown = running });
        return (queued.Count, running.Count);
    }

    public void UpsertProcess(ProcessRow row) =>
        Execute("""
            INSERT INTO processes(process_id,pid,start_time,state,exit_code,lifetime,root_id,stdout_artifact,stderr_artifact,updated_at)
            VALUES($id,$pid,$start,$state,$exit,$life,$root,$out,$err,$n)
            ON CONFLICT(process_id) DO UPDATE SET pid=$pid,start_time=$start,state=$state,exit_code=$exit,
                lifetime=$life,root_id=$root,stdout_artifact=$out,stderr_artifact=$err,updated_at=$n
            """,
            ("$id", row.ProcessId), ("$pid", row.Pid), ("$start", row.StartTime), ("$state", row.State),
            ("$exit", row.ExitCode), ("$life", row.Lifetime), ("$root", row.RootId),
            ("$out", row.StdoutArtifact), ("$err", row.StderrArtifact), ("$n", Now));

    public ProcessRow? Process(string processId)
    {
        try
        {
            return Read("SELECT process_id,pid,start_time,state,exit_code,lifetime,root_id,stdout_artifact,stderr_artifact FROM processes WHERE process_id=$id",
                MapProcess, ("$id", processId));
        }
        catch (Exception error) when (IsMalformedValue(error))
        {
            Interlocked.Increment(ref malformedRows);
            return null;
        }
    }

    public List<ProcessRow> Processes(string? state = null) =>
        ReadAll("SELECT process_id,pid,start_time,state,exit_code,lifetime,root_id,stdout_artifact,stderr_artifact FROM processes " +
            "WHERE ($state IS NULL OR state=$state) ORDER BY rowid", MapProcess, ("$state", state));

    private static ProcessRow MapProcess(SqliteDataReader r) => new(r.GetString(0), r.GetInt32(1), r.GetInt64(2),
        r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetString(5), r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8));

    public void InsertArtifact(ArtifactRow row) =>
        Execute("INSERT INTO artifacts(id,mime,bytes,sha256,path,complete,generation,created_at) VALUES($id,$m,$b,$s,$p,$c,$g,$n)",
            ("$id", row.Id), ("$m", row.Mime), ("$b", row.Bytes), ("$s", row.Sha256), ("$p", row.Path),
            ("$c", row.Complete), ("$g", row.Generation), ("$n", row.CreatedAt));

    public void UpdateArtifact(string id, long bytes, string? sha256, int complete, int generation) =>
        Execute("UPDATE artifacts SET bytes=$b,sha256=$s,complete=$c,generation=$g WHERE id=$id",
            ("$b", bytes), ("$s", sha256), ("$c", complete), ("$g", generation), ("$id", id));

    public ArtifactRow? Artifact(string id) =>
        Read("SELECT id,mime,bytes,sha256,path,complete,generation,created_at FROM artifacts WHERE id=$id",
            r => new ArtifactRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetString(7)), ("$id", id));

    public void InsertToken(TokenRow row) =>
        Execute("INSERT INTO tokens(hash,kind,client_id,audience,expires_at,revoked,pkce_used,family,scope) VALUES($h,$k,$c,$a,$e,0,$p,$f,$s)",
            ("$h", row.Hash), ("$k", row.Kind), ("$c", row.ClientId), ("$a", row.Audience),
            ("$e", row.ExpiresAt.ToString("o")), ("$p", row.PkceUsed ? 1 : 0), ("$f", row.Family), ("$s", row.Scope));

    // A row that cannot be read throws a value error, which Tokens.Validate reports as an invalid token.
    public TokenRow? Token(string hash) =>
        Read("SELECT hash,kind,client_id,audience,expires_at,revoked,pkce_used,family,scope FROM tokens WHERE hash=$h",
            r => new TokenRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.GetInt32(5) != 0, r.GetInt32(6) != 0, r.GetString(7), r.GetString(8)), ("$h", hash));

    public void SetTokenExpiry(string hash, DateTimeOffset expires) =>
        Execute("UPDATE tokens SET expires_at=$e WHERE hash=$h", ("$e", expires.ToString("o")), ("$h", hash));

    public void RevokeToken(string hash) =>
        Execute("UPDATE tokens SET revoked=1,revoked_at=$n WHERE hash=$h", ("$n", Now), ("$h", hash));

    public int RevokeAllTokens()
    {
        var live = ReadAll("SELECT hash FROM tokens WHERE revoked=0", r => r.GetString(0));
        Execute("UPDATE tokens SET revoked=1,revoked_at=$n WHERE revoked=0", ("$n", Now));
        return live.Count;
    }

    public void InsertClient(ClientRow row) =>
        Execute("INSERT INTO clients(client_id,secret_hash,redirect_uris,auth_method,name,created_at,last_signed_in_at) VALUES($id,$s,$r,$m,$n,$c,NULL)",
            ("$id", row.ClientId), ("$s", row.SecretHash), ("$r", JsonSerializer.Serialize(row.RedirectUris)), ("$m", row.AuthMethod),
            ("$n", row.Name), ("$c", row.CreatedAt));

    // A row whose values cannot be read is an unknown client, never an error that stops authorization.
    public ClientRow? Client(string clientId)
    {
        try
        {
            return Read("SELECT client_id,secret_hash,redirect_uris,auth_method,name,created_at,last_signed_in_at FROM clients WHERE client_id=$id",
                MapClient, ("$id", clientId));
        }
        catch (Exception error) when (IsMalformedValue(error))
        {
            Interlocked.Increment(ref malformedRows);
            return null;
        }
    }

    public bool ClientExists(string clientId) =>
        Read("SELECT client_id FROM clients WHERE client_id=$id", r => r.GetString(0), ("$id", clientId)) is not null;

    public List<ClientRow> Clients() =>
        ReadAll("SELECT client_id,secret_hash,redirect_uris,auth_method,name,created_at,last_signed_in_at FROM clients ORDER BY created_at, rowid",
            MapClient);

    private static ClientRow MapClient(SqliteDataReader r)
    {
        string[] uris;
        try { uris = JsonSerializer.Deserialize<string[]>(r.GetString(2)) ?? throw new FormatException("redirect_uris is null"); }
        catch (JsonException error) { throw new FormatException("redirect_uris is not a JSON array of strings", error); }
        return new ClientRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), uris, r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6));
    }

    public void MarkClientSignedIn(string clientId, string at) =>
        Execute("UPDATE clients SET last_signed_in_at=$a WHERE client_id=$id", ("$a", at), ("$id", clientId));

    // A registration that never completed a sign-in and holds no live token. A client that signed in is never selected.
    private const string UnusedClient =
        "last_signed_in_at IS NULL AND client_id NOT IN (SELECT client_id FROM tokens WHERE revoked=0)";

    // Keeps the newest unused registrations and removes older ones beyond that number.
    public int EvictUnusedClients(int keep) =>
        ExecuteCount($"DELETE FROM clients WHERE client_id IN (SELECT client_id FROM clients WHERE {UnusedClient} " +
            "ORDER BY created_at DESC, rowid DESC LIMIT -1 OFFSET $k)", ("$k", keep));

    // Every registered client goes, and every token issued to a registered client id is revoked, including tokens of a
    // client removed earlier. The configured static client is never touched, whatever its id looks like.
    public (int Removed, int Revoked) RemoveRegisteredClients(string now, string staticClientId)
    {
        int removed = ExecuteCount("DELETE FROM clients WHERE client_id <> $static", ("$static", staticClientId));
        int revoked = ExecuteCount("UPDATE tokens SET revoked=1,revoked_at=$n WHERE revoked=0 AND substr(client_id,1,4)='dcr_' AND client_id <> $static",
            ("$n", now), ("$static", staticClientId));
        return (removed, revoked);
    }

    public void AddCheckpoint(string json) =>
        Execute("INSERT INTO checkpoints(utc,json) VALUES($n,$j)", ("$n", Now), ("$j", json));

    public (long Seq, string Utc, string Json)? LatestCheckpoint()
    {
        var rows = ReadAll("SELECT seq,utc,json FROM checkpoints ORDER BY seq DESC LIMIT 1",
            r => (Seq: r.GetInt64(0), Utc: r.GetString(1), Json: r.GetString(2)));
        return rows.Count == 0 ? null : rows[0];
    }

    // Retention (P17). Every statement touches only rows past the cutoff and outside the protected states; timestamps
    // are ISO 8601 UTC strings, which order the same way as the instants they name.
    public int StampUndatedRows(string now) =>
        ExecuteCount("UPDATE tokens SET revoked_at=$n WHERE revoked=1 AND (revoked_at IS NULL OR revoked_at='')", ("$n", now)) +
        ExecuteCount("UPDATE processes SET updated_at=$n WHERE updated_at IS NULL OR updated_at=''", ("$n", now));

    // Deletes in small batches, each its own statement, so a large cleanup never holds the store lock long enough to
    // delay a tool call.
    private int DeleteInBatches(string table, string key, string condition, params (string Key, object? Value)[] args)
    {
        int total = 0;
        while (true)
        {
            int removed = ExecuteCount($"DELETE FROM {table} WHERE {key} IN (SELECT {key} FROM {table} WHERE {condition} LIMIT 500)", args);
            total += removed;
            if (removed == 0) return total;
        }
    }

    public int DeleteFinishedInvocations(string cutoff) =>
        DeleteInBatches("invocations", "id", "status NOT IN ('queued','running') AND updated_at < $c", ("$c", cutoff));

    public int DeleteEvents(string cutoff) => DeleteInBatches("events", "seq", "utc < $c", ("$c", cutoff));

    public int DeleteOldCheckpoints(string cutoff) =>
        DeleteInBatches("checkpoints", "seq", "utc < $c AND seq < (SELECT MAX(seq) FROM checkpoints)", ("$c", cutoff));

    // Expired or revoked tokens only. A refresh token counts as expired only while refresh_token_days is positive,
    // matching Tokens.Validate, so a kept refresh token stored with an old finite expiry is never removed.
    public int DeleteDeadTokens(string cutoff, bool refreshTokensExpire) =>
        DeleteInBatches("tokens", "hash", "(revoked=1 AND revoked_at < $c) OR (revoked=0 AND expires_at < $c AND (kind <> 'refresh' OR $r = 1))",
            ("$c", cutoff), ("$r", refreshTokensExpire ? 1 : 0));

    public List<(string ProcessId, string? Stdout, string? Stderr)> FinishedProcesses(string cutoff) =>
        ReadAll("SELECT process_id,stdout_artifact,stderr_artifact FROM processes WHERE state <> 'running' AND updated_at < $c",
            r => (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)), ("$c", cutoff));

    public int DeleteProcess(string processId) => ExecuteCount("DELETE FROM processes WHERE process_id=$id", ("$id", processId));

    // A client that has signed in is never removed by age.
    public int DeleteUnusedClients(string cutoff) =>
        DeleteInBatches("clients", "client_id", $"{UnusedClient} AND created_at < $c", ("$c", cutoff));

    // Artifacts that no remaining process row refers to, so the output of a process is kept exactly as long as the process.
    public List<string> UnreferencedArtifacts(string cutoff) =>
        ReadAll("SELECT id FROM artifacts WHERE created_at < $c AND id NOT IN " +
            "(SELECT stdout_artifact FROM processes WHERE stdout_artifact IS NOT NULL UNION SELECT stderr_artifact FROM processes WHERE stderr_artifact IS NOT NULL)",
            r => r.GetString(0), ("$c", cutoff));

    public List<string> ReferencedArtifacts() =>
        ReadAll("SELECT stdout_artifact FROM processes WHERE stdout_artifact IS NOT NULL UNION SELECT stderr_artifact FROM processes WHERE stderr_artifact IS NOT NULL",
            r => r.GetString(0));

    public bool ArtifactKnown(string id) => Read("SELECT id FROM artifacts WHERE id=$id", r => r.GetString(0), ("$id", id)) is not null;

    public int DeleteArtifact(string id) => ExecuteCount("DELETE FROM artifacts WHERE id=$id", ("$id", id));

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            db.Dispose();
        }
    }
}
