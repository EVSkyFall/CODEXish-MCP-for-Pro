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

// D10: one SQLite file in the state directory, WAL, every table the slice needs. All access is serialized on
// one connection; the ledger relies on the same lock to make acceptance order observable.
public sealed class Store : IDisposable
{
    private readonly SqliteConnection db;
    private readonly object gate = new();
    private bool disposed;

    // Self-test injection point for D9's persist-failure path. Never set outside --self-test.
    public bool FailNextResultWrite { get; set; }

    public Store(string path)
    {
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        Execute("""
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
                audience TEXT NOT NULL, expires_at TEXT NOT NULL, revoked INTEGER NOT NULL, pkce_used INTEGER NOT NULL DEFAULT 0,
                family TEXT NOT NULL DEFAULT '', scope TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS checkpoints(seq INTEGER PRIMARY KEY AUTOINCREMENT, utc TEXT NOT NULL, json TEXT NOT NULL);
            """);
        // A database written by an earlier build has no family column; adding it is a no-op afterwards.
        try { Execute("ALTER TABLE tokens ADD COLUMN family TEXT NOT NULL DEFAULT ''"); }
        catch (SqliteException) { }
        try { Execute("ALTER TABLE tokens ADD COLUMN scope TEXT NOT NULL DEFAULT ''"); }
        catch (SqliteException) { }
    }

    private static string Now => DateTimeOffset.UtcNow.ToString("o");

    public void Execute(string sql, params (string Key, object? Value)[] args) => ExecuteCount(sql, args);

    public int ExecuteCount(string sql, params (string Key, object? Value)[] args)
    {
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = sql;
            foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
            return command.ExecuteNonQuery();
        }
    }

    // Used by the self-test to make a real SQLite write fail instead of only an injected flag.
    public void Pragma(string pragma) => Execute(pragma);

    private T? Read<T>(string sql, Func<SqliteDataReader, T> map, params (string Key, object? Value)[] args) where T : class
    {
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = sql;
            foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            return reader.Read() ? map(reader) : null;
        }
    }

    private List<T> ReadAll<T>(string sql, Func<SqliteDataReader, T> map, params (string Key, object? Value)[] args)
    {
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = sql;
            foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            List<T> rows = [];
            while (reader.Read()) rows.Add(map(reader));
            return rows;
        }
    }

    public void Event(string kind, string? reference, object payload) =>
        Execute("INSERT INTO events(utc,kind,ref,json) VALUES($utc,$kind,$ref,$json)",
            ("$utc", Now), ("$kind", kind), ("$ref", reference), ("$json", JsonSerializer.Serialize(payload)));

    public List<(long Seq, string Utc, string Kind, string? Reference, string? Json)> Events(string? kind = null) =>
        ReadAll("SELECT seq,utc,kind,ref,json FROM events WHERE ($kind IS NULL OR kind=$kind) ORDER BY seq",
            r => (r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4)), ("$kind", kind));

    public InvocationRow? Invocation(string id) =>
        Read("SELECT id,digest,tool,status,result FROM invocations WHERE id=$id",
            r => new InvocationRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4)), ("$id", id));

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
            INSERT INTO processes(process_id,pid,start_time,state,exit_code,lifetime,root_id,stdout_artifact,stderr_artifact)
            VALUES($id,$pid,$start,$state,$exit,$life,$root,$out,$err)
            ON CONFLICT(process_id) DO UPDATE SET pid=$pid,start_time=$start,state=$state,exit_code=$exit,
                lifetime=$life,root_id=$root,stdout_artifact=$out,stderr_artifact=$err
            """,
            ("$id", row.ProcessId), ("$pid", row.Pid), ("$start", row.StartTime), ("$state", row.State),
            ("$exit", row.ExitCode), ("$life", row.Lifetime), ("$root", row.RootId),
            ("$out", row.StdoutArtifact), ("$err", row.StderrArtifact));

    public ProcessRow? Process(string processId) =>
        Read("SELECT process_id,pid,start_time,state,exit_code,lifetime,root_id,stdout_artifact,stderr_artifact FROM processes WHERE process_id=$id",
            MapProcess, ("$id", processId));

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

    public TokenRow? Token(string hash) =>
        Read("SELECT hash,kind,client_id,audience,expires_at,revoked,pkce_used,family,scope FROM tokens WHERE hash=$h",
            r => new TokenRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                DateTimeOffset.Parse(r.GetString(4)), r.GetInt32(5) != 0, r.GetInt32(6) != 0, r.GetString(7),
                r.GetString(8)), ("$h", hash));

    // One atomic consume-and-revoke: two concurrent refreshes cannot both see the token as live.
    public bool ConsumeRefresh(string hash) =>
        ExecuteCount("UPDATE tokens SET revoked=1 WHERE hash=$h AND revoked=0 AND kind='refresh'", ("$h", hash)) == 1;

    public void RevokeToken(string hash) => Execute("UPDATE tokens SET revoked=1 WHERE hash=$h", ("$h", hash));

    // Replaying a rotated refresh token means the family is compromised: every token issued from that same
    // authorization is revoked, not just the one presented.
    public int RevokeFamily(string family)
    {
        if (family.Length == 0) return 0;
        var live = ReadAll("SELECT hash FROM tokens WHERE family=$f AND revoked=0", r => r.GetString(0), ("$f", family));
        Execute("UPDATE tokens SET revoked=1 WHERE family=$f", ("$f", family));
        Event("token_family_revoked", family, new { revoked = live.Count });
        return live.Count;
    }

    public int RevokeAllTokens()
    {
        var live = ReadAll("SELECT hash FROM tokens WHERE revoked=0", r => r.GetString(0));
        Execute("UPDATE tokens SET revoked=1 WHERE revoked=0");
        return live.Count;
    }

    public void AddCheckpoint(string json) =>
        Execute("INSERT INTO checkpoints(utc,json) VALUES($n,$j)", ("$n", Now), ("$j", json));

    public (long Seq, string Utc, string Json)? LatestCheckpoint()
    {
        var rows = ReadAll("SELECT seq,utc,json FROM checkpoints ORDER BY seq DESC LIMIT 1",
            r => (Seq: r.GetInt64(0), Utc: r.GetString(1), Json: r.GetString(2)));
        return rows.Count == 0 ? null : rows[0];
    }

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
