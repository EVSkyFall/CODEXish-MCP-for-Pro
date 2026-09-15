using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol.Protocol;

namespace Codexish.P0.V1;

public static class FileFence
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static bool Within(string root, string path)
    {
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return string.Equals(root, path, Comparison) || path.StartsWith(prefix, Comparison);
    }
    public static void NoReparse(string path)
    {
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
        {
            // GetAttributes also detects a dangling link; File.Exists alone would miss it.
            try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new ProbeFault("OUTSIDE_WORKSPACE", "Reparse points and symbolic links are not followed."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    public static string Resolve(RootGrant root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0') ||
            relative.Split(['/', '\\']).Any(x => x == "..")) throw new ProbeFault("OUTSIDE_WORKSPACE", "Use a root-relative path without parent traversal.");
        string path = Path.GetFullPath(Path.Combine(root.Path, relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(root.Path, path)) throw new ProbeFault("OUTSIDE_WORKSPACE", "Path is outside the selected root.");
        NoReparse(path); return path;
    }
    public static FileStream Open(RootGrant root, string relative, bool write = false, bool create = false)
    {
        string path = Resolve(root, relative);
        var stream = new FileStream(path, create ? FileMode.CreateNew : FileMode.Open,
            write ? FileAccess.ReadWrite : FileAccess.Read, write ? FileShare.None : FileShare.Read);
        try { NoReparse(path); NativeDesktop.CheckFileHandle(stream, root.Path); return stream; }
        catch { stream.Dispose(); throw; }
    }
    // Hold an opened directory while checking its final Windows path. No sandbox claim.
    public static IDisposable DirectoryHandle(RootGrant root, string relative)
    {
        string path = Resolve(root, relative);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException();
        if (!OperatingSystem.IsWindows()) return new Empty();
        var h = CreateFileW(path, 0x80, 3, 0, 3, 0x02000000 | 0x00200000, 0);
        if (h.IsInvalid) { h.Dispose(); throw new IOException("Cannot open directory handle.", Marshal.GetLastWin32Error()); }
        try
        {
            NoReparse(path);
            var b = new StringBuilder(32768); uint n = GetFinalPathNameByHandleW(h, b, (uint)b.Capacity, 0);
            string final = b.ToString(); if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
            if (n == 0 || n >= b.Capacity || !Within(root.Path, final)) throw new ProbeFault("OUTSIDE_WORKSPACE", "Directory handle is outside the selected root.");
            return h;
        }
        catch { h.Dispose(); throw; }
    }
    private sealed class Empty : IDisposable { public void Dispose() { } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint sa, uint mode, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    public static byte[] Bytes(Stream s) { using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
}

public sealed class Artifacts(Store store, string directory)
{
    private sealed record Page(string Session, string Artifact, string View, long Offset, long End, int Size);
    private readonly byte[] cursorKey = LoadCursorKey(directory);
    private static byte[] LoadCursorKey(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cursor.key");
        if (!File.Exists(path))
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(RandomNumberGenerator.GetBytes(32)); file.Flush(true);
        }
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != 32) throw new InvalidDataException("Invalid local artifact cursor key.");
        return bytes;
    }
    public string Create(string session, byte[]? bytes = null, bool complete = true)
    {
        string id = "art_" + Guid.NewGuid().ToString("N"); string path = Path.Combine(directory, id);
        Directory.CreateDirectory(directory);
        using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { if (bytes is not null) f.Write(bytes); f.Flush(true); }
        store.Exec("INSERT INTO artifacts VALUES($i,$s,$p,$c)", ("$i", id), ("$s", session), ("$p", path), ("$c", complete ? 1 : 0));
        return id;
    }
    public (string path, bool complete) Locate(string session, string id)
    {
        lock (store.Gate)
        {
            using var c = store.Command("SELECT path,complete FROM artifacts WHERE id=$i AND session=$s", ("$i", id), ("$s", session));
            using var r = c.ExecuteReader(); if (!r.Read()) throw new ProbeFault("NOT_FOUND", "No artifact in this session.");
            return (r.GetString(0), r.GetInt32(1) != 0);
        }
    }
    public void Complete(string session, string id) => store.Exec("UPDATE artifacts SET complete=1 WHERE session=$s AND id=$i", ("$s", session), ("$i", id));
    private string Sign(Page p)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(p);
        return Convert.ToBase64String(data) + "." + Convert.ToBase64String(HMACSHA256.HashData(cursorKey, data));
    }
    private Page Verify(string cursor, string session, string id)
    {
        try
        {
            var parts = cursor.Split('.'); if (parts.Length != 2) throw new FormatException();
            byte[] data = Convert.FromBase64String(parts[0]), sig = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(sig, HMACSHA256.HashData(cursorKey, data))) throw new FormatException();
            var p = JsonSerializer.Deserialize<Page>(data)!;
            if (p.Session != session || p.Artifact != id || p.Offset < 0 || p.End < p.Offset || p.Size <= 0) throw new FormatException();
            return p;
        }
        catch (Exception e) when (e is FormatException or JsonException or NullReferenceException)
        { throw new ProbeFault("INVALID_CURSOR", "Cursor does not match this session/artifact/generation."); }
    }
    public CallToolResult Read(string session, string id, string? cursor = null, int pageBytes = 65536)
    {
        if (pageBytes is <= 0 or > 65536) throw new ProbeFault("INVALID_ARGUMENT", "Page size is 1..65536 bytes (response-page configuration, not an output cap).");
        var original = Locate(session, id); Page p; bool sourceComplete;
        if (cursor is null)
        {
            // Freeze the redacted UTF-8 view of the currently available prefix; retries never read a moving page.
            byte[] raw;
            using (var f = new FileStream(original.path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) raw = FileFence.Bytes(f);
            string view = Create(session, Encoding.UTF8.GetBytes(Redaction.Text(Encoding.UTF8.GetString(raw))), original.complete);
            long length = new FileInfo(Locate(session, view).path).Length;
            p = new(session, id, view, 0, length, pageBytes); sourceComplete = original.complete;
        }
        else { p = Verify(cursor, session, id); sourceComplete = Locate(session, p.View).complete; }
        var location = Locate(session, p.View);
        using var stream = new FileStream(location.path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != p.End) throw new ProbeFault("INVALID_CURSOR", "Snapshot generation changed.");
        stream.Position = p.Offset;
        byte[] bytes = new byte[(int)Math.Min(p.Size, p.End - p.Offset)]; stream.ReadExactly(bytes);
        // Byte payload preserves UTF-8 codepoints even when a page boundary splits a multibyte character.
        long next = p.Offset + bytes.Length;
        return VReply.Ok(new { artifact_id = id, generation = p.View, offset = p.Offset, end = next,
            view_bytes = p.End, text = Encoding.UTF8.GetString(bytes), utf8_base64 = Convert.ToBase64String(bytes),
            redaction = "best_effort_utf8_view", complete = sourceComplete, page_complete = next == p.End,
            replay_cursor = Sign(p), next_cursor = next < p.End ? Sign(p with { Offset = next }) : null,
            follow = !sourceComplete && next == p.End ? "call artifact.read without cursor for a fresh prefix snapshot" : null });
    }
    public CallToolResult Search(string session, string id, string query, int offset = 0, int pageSize = 200)
    {
        if (offset < 0 || pageSize is <= 0 or > 200) throw new ProbeFault("INVALID_ARGUMENT", "Use a nonnegative result offset and a 1..200 row page.");
        var (path, complete) = Locate(session, id);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        string[] lines = Redaction.Text(Encoding.UTF8.GetString(FileFence.Bytes(f))).Split('\n');
        var matches = lines.Select((text, i) => new { line = i + 1, text }).Where(x => x.text.Contains(query, StringComparison.Ordinal)).ToArray();
        return VReply.Ok(new { artifact_id = id, complete, matches = matches.Skip(offset).Take(pageSize), next_offset = offset + pageSize < matches.Length ? (int?)(offset + pageSize) : null });
    }
}

public sealed class Files(ServerConfig config, Store store, Artifacts artifacts)
{
    public RootGrant Grant(string id, string permission = "read")
    {
        var r = config.Roots.SingleOrDefault(x => x.Id == id) ?? throw new ProbeFault("OUTSIDE_WORKSPACE", "Unknown root ID.");
        bool allowed = permission switch { "read" => r.Read, "write" => r.Write, "shell" => r.Shell, _ => false };
        if (!allowed) throw new ProbeFault("PERMISSION_DENIED", "Root grant does not permit " + permission + ".");
        return r;
    }
    public CallToolResult Read(string session, string rootId, string path, int startLine = 1, int lineCount = 200)
    {
        if (startLine < 1 || lineCount is <= 0 or > 200) throw new ProbeFault("INVALID_ARGUMENT", "Line pages use start_line >= 1 and line_count 1..200.");
        using var f = FileFence.Open(Grant(rootId), path); byte[] bytes = FileFence.Bytes(f);
        store.Event(session, "fs.read", new { root_id = rootId, path, bytes = bytes.Length });
        try
        {
            var d = ProbeRuntime.Decode(bytes); string redacted = Redaction.Text(d.text); var lines = redacted.Split('\n');
            return VReply.Ok(new { root_id = rootId, path, sha256 = ProbeRuntime.Hash(bytes), bytes = bytes.Length,
                encoding = d.encoding.WebName, bom_bytes = d.bom, content_redacted = redacted != d.text,
                start_line = startLine, text = string.Join("\n", lines.Skip(startLine - 1).Take(lineCount)),
                total_lines = lines.Length, next_line = startLine - 1 + lineCount < lines.Length ? (int?)(startLine + lineCount) : null });
        }
        catch (Exception e) when (e is DecoderFallbackException || e is ProbeFault { Code: "UNSUPPORTED_CAPABILITY" })
        { return VReply.Ok(new { root_id = rootId, path, sha256 = ProbeRuntime.Hash(bytes), bytes = bytes.Length, binary = true, artifact_id = artifacts.Create(session, bytes), text_view = "artifact.read produces a best-effort redacted UTF-8 view; raw bytes remain local" }); }
    }
    public CallToolResult Stat(string rootId, string path, bool hash = false)
    {
        var root = Grant(rootId); string p = FileFence.Resolve(root, path);
        if (Directory.Exists(p)) { using var h = FileFence.DirectoryHandle(root, path); return VReply.Ok(new { root_id = rootId, path, kind = "directory" }); }
        using var f = FileFence.Open(root, path); return VReply.Ok(new { root_id = rootId, path, kind = "file", bytes = f.Length,
            modified_utc = File.GetLastWriteTimeUtc(p), sha256 = hash ? ProbeRuntime.Hash(FileFence.Bytes(f)) : null });
    }
    public CallToolResult List(string session, string rootId, string path, int depth = 1)
    {
        if (depth < 0) throw new ProbeFault("INVALID_ARGUMENT", "depth must be nonnegative.");
        var root = Grant(rootId); var entries = new List<object>(); var errors = new List<object>();
        Walk(root, path, depth, (relative, directory) => entries.Add(new { path = relative, kind = directory ? "directory" : "file" }), errors);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { entries, errors });
        return VReply.Ok(new { root_id = rootId, path, entries = entries.Take(200), errors,
            total_entries = entries.Count, complete = entries.Count <= 200, artifact_id = artifacts.Create(session, json), next_tool = "artifact.read" });
    }
    private static void Walk(RootGrant root, string path, int depth, Action<string, bool> visit, List<object> errors)
    {
        using var h = FileFence.DirectoryHandle(root, path); if (depth == 0) return;
        foreach (string entry in Directory.EnumerateFileSystemEntries(FileFence.Resolve(root, path)).Order(StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(root.Path, entry).Replace('\\', '/');
            try
            {
                FileFence.NoReparse(entry); bool dir = Directory.Exists(entry); visit(rel, dir);
                if (dir && depth > 1) Walk(root, rel, depth - 1, visit, errors);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ProbeFault)
            { errors.Add(new { path = rel, code = e is ProbeFault p ? p.Code : "IO_ERROR" }); }
        }
    }
    public CallToolResult Search(string session, string rootId, string query, bool regex = false, string glob = "*")
    {
        var root = Grant(rootId); var matches = new List<object>(); var errors = new List<object>();
        Regex? pattern = regex ? new Regex(query, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking) : null;
        Walk(root, "", int.MaxValue, (path, directory) =>
        {
            if (directory || !FileSystemName.MatchesSimpleExpression(glob, path, OperatingSystem.IsWindows())) return;
            try
            {
                using var f = FileFence.Open(root, path); string text = Redaction.Text(ProbeRuntime.Decode(FileFence.Bytes(f)).text);
                foreach (var (line, i) in text.Split('\n').Select((s, i) => (s, i)))
                    if (pattern is null ? line.Contains(query, StringComparison.Ordinal) : pattern.IsMatch(line)) matches.Add(new { path, line = i + 1, text = line });
            }
            catch (Exception e) when (e is IOException or DecoderFallbackException or ProbeFault) { errors.Add(new { path, code = "UNREADABLE_OR_BINARY" }); }
        }, errors);
        return VReply.Ok(new { root_id = rootId, matches = matches.Take(200), errors, total_matches = matches.Count,
            complete = matches.Count <= 200, artifact_id = artifacts.Create(session, JsonSerializer.SerializeToUtf8Bytes(new { matches, errors })), next_tool = "artifact.read" });
    }
    private static byte[] Encode(byte[] before, string text)
    {
        var d = ProbeRuntime.Decode(before); string stripped = d.text.Replace("\r\n", "");
        if ((d.text.Contains("\r\n") && stripped.Contains('\n')) || stripped.Contains('\r'))
            throw new ProbeFault("UNSUPPORTED_CAPABILITY", "Mixed or bare-CR line endings require an explicit future conversion mode.");
        text = text.Replace("\r\n", "\n"); if (text.Contains('\r')) throw new ProbeFault("INVALID_ARGUMENT", "Use LF or CRLF text.");
        if (d.text.Contains("\r\n")) text = text.Replace("\n", "\r\n");
        return before.Take(d.bom).Concat(d.encoding.GetBytes(text)).ToArray();
    }
    public CallToolResult Write(string session, string rootId, string path, string text, string? expected, string mode = "replace")
    {
        var root = Grant(rootId, "write"); if (mode is not ("replace" or "create")) throw new ProbeFault("INVALID_ARGUMENT", "Use create or replace.");
        using var parent = FileFence.DirectoryHandle(root, Path.GetDirectoryName(path.Replace('\\', '/')) ?? "");
        if (mode == "create")
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
            using var output = FileFence.Open(root, path, true, true);
            try { output.Write(bytes); output.Flush(true); }
            catch (IOException) { throw new ProbeFault("EXECUTION_UNKNOWN", "Create/write interrupted; inspect the file.", "partial"); }
            return VReply.Ok(new { root_id = rootId, path, sha256 = ProbeRuntime.Hash(bytes), save_mode = "exclusive_create_non_atomic", bytes = bytes.Length });
        }
        using var f = FileFence.Open(root, path, true); byte[] before = FileFence.Bytes(f);
        if (expected is null || !ProbeRuntime.Hash(before).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new ProbeFault("FILE_CHANGED", "Reread and reconcile the current file hash.");
        byte[] after = Encode(before, text);
        string backupDir = Path.Combine(config.StateDirectory, "backups"); Directory.CreateDirectory(backupDir);
        string backup = Guid.NewGuid().ToString("N") + ".bak";
        using (var b = new FileStream(Path.Combine(backupDir, backup), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { b.Write(before); b.Flush(true); }
        try { f.Position = 0; f.Write(after); f.SetLength(after.Length); f.Flush(true); }
        catch (IOException) { throw new ProbeFault("EXECUTION_UNKNOWN", "In-place write interrupted; inspect file and backup " + backup, "partial"); }
        return VReply.Ok(new { root_id = rootId, path, old_sha256 = ProbeRuntime.Hash(before), sha256 = ProbeRuntime.Hash(after), backup,
            bytes = after.Length, save_mode = "exclusive_in_place_non_atomic" });
    }
    public CallToolResult Patch(string session, string rootId, string patch, Dictionary<string, string> expected)
    {
        Grant(rootId, "write"); var changes = UnifiedPatch.Parse(patch); var results = new List<object>(); int applied = 0;
        foreach (var change in changes)
        {
            CallToolResult result = VReply.Guard(() =>
            {
                if (!expected.TryGetValue(change.Path, out var hash)) throw new ProbeFault("INVALID_ARGUMENT", "Provide expected hash for every patch file.");
                string old; using (var f = FileFence.Open(Grant(rootId), change.Path)) old = ProbeRuntime.Decode(FileFence.Bytes(f)).text;
                return Write(session, rootId, change.Path, change.Apply(old), hash);
            });
            if (VReply.Status(result) == "succeeded") applied++;
            results.Add(new { path = change.Path, result = result.StructuredContent });
        }
        object data = new { files = results, applied, total = changes.Count, atomic = false, diff_artifact = artifacts.Create(session, Encoding.UTF8.GetBytes(patch)) };
        return applied == changes.Count ? VReply.Ok(data) : VReply.Error("PATCH_PARTIAL", "Inspect each file result; no automatic rollback.", applied > 0 ? "partial" : "none", data);
    }
}

public sealed record UnifiedPatch(string Path, string[] Body)
{
    public static List<UnifiedPatch> Parse(string patch)
    {
        var lines = patch.Replace("\r\n", "\n").Split('\n'); var files = new List<UnifiedPatch>(); int i = 0;
        while (i < lines.Length)
        {
            if (lines[i].Length == 0 || lines[i].StartsWith("diff --git ") || lines[i].StartsWith("index ")) { i++; continue; }
            if (!lines[i].StartsWith("--- ") || i + 1 == lines.Length || !lines[i + 1].StartsWith("+++ ")) throw new ProbeFault("INVALID_PATCH", "Expected unified diff file headers.");
            string from = lines[i++][4..].Split('\t')[0], to = lines[i++][4..].Split('\t')[0];
            if (from.StartsWith("a/")) from = from[2..]; if (to.StartsWith("b/")) to = to[2..];
            if (from != to || to == "/dev/null") throw new ProbeFault("UNSUPPORTED_CAPABILITY", "Slice 1 patches modify existing files; use fs.write create for new files. Renames/deletes are not yet supported.");
            var body = new List<string>();
            while (i < lines.Length && lines[i].StartsWith("@@ ", StringComparison.Ordinal))
            {
                var h = Header(lines[i]); body.Add(lines[i++]);
                int oldLeft = h.oldCount, newLeft = h.newCount;
                while (oldLeft > 0 || newLeft > 0)
                {
                    if (i >= lines.Length || lines[i].Length == 0) throw new ProbeFault("INVALID_PATCH", "Incomplete hunk.");
                    string line = lines[i++]; char op = line[0];
                    if (op is ' ' or '-') oldLeft--;
                    if (op is ' ' or '+') newLeft--;
                    if (op is not (' ' or '-' or '+') || oldLeft < 0 || newLeft < 0) throw new ProbeFault("INVALID_PATCH", "Hunk line counts do not match.");
                    body.Add(line);
                    if (i < lines.Length && lines[i] == "\\ No newline at end of file") body.Add(lines[i++]);
                }
            }
            if (body.Count == 0) throw new ProbeFault("INVALID_PATCH", "No hunks.");
            files.Add(new(to, body.ToArray()));
        }
        if (files.Count == 0 || files.Select(x => x.Path).Distinct().Count() != files.Count) throw new ProbeFault("INVALID_PATCH", "Nonempty unique file patches required.");
        return files;
    }
    private static (int oldStart, int oldCount, int newStart, int newCount) Header(string line)
    {
        var m = Regex.Match(line, @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(?:.*)$");
        if (!m.Success) throw new ProbeFault("INVALID_PATCH", "Expected hunk header.");
        try { return (int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1,
            int.Parse(m.Groups[3].Value), m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1); }
        catch (OverflowException) { throw new ProbeFault("INVALID_PATCH", "Hunk position is out of range."); }
    }
    public string Apply(string old)
    {
        bool finalNewline = old.EndsWith('\n'); string[] source = old.Length == 0 ? [] : old.Replace("\r\n", "\n").Split('\n'); if (finalNewline) source = source[..^1];
        var output = new List<string>(); int pos = 0, i = 0; bool any = false;
        while (i < Body.Length)
        {
            if (Body[i].Length == 0) { i++; continue; }
            var h = Header(Body[i++]); int start = h.oldStart, count = h.oldCount, added = h.newCount;
            int at = count == 0 ? start : start - 1;
            if (at < pos || at > source.Length) throw new ProbeFault("PATCH_CONFLICT", "Hunks overlap or are out of range.");
            output.AddRange(source[pos..at]); pos = at;
            int targetAt = added == 0 ? h.newStart : h.newStart - 1;
            if (targetAt != output.Count) throw new ProbeFault("INVALID_PATCH", "New hunk position does not match prior output.");
            int read = 0, written = 0;
            while (i < Body.Length && !Body[i].StartsWith("@@ ") && Body[i].Length != 0)
            {
                string line = Body[i++]; if (line.StartsWith("\\ No newline")) continue;
                char op = line[0]; string text = line[1..];
                bool noNewline = i < Body.Length && Body[i].StartsWith("\\ No newline");
                if (op is ' ' or '-') { if (pos >= source.Length || source[pos] != text) throw new ProbeFault("PATCH_CONFLICT", "Hunk context does not match."); pos++; read++; }
                if (op is ' ' or '+') { output.Add(text); written++; if (noNewline) finalNewline = false; else if (op == '+' && pos >= source.Length) finalNewline = true; }
                if (op is not (' ' or '-' or '+')) throw new ProbeFault("INVALID_PATCH", "Unsupported hunk line.");
            }
            if (read != count || written != added) throw new ProbeFault("INVALID_PATCH", "Hunk line counts do not match.");
            any = true;
        }
        if (!any) throw new ProbeFault("INVALID_PATCH", "No hunks.");
        output.AddRange(source[pos..]); return string.Join("\n", output) + (finalNewline && output.Count > 0 ? "\n" : "");
    }
}
