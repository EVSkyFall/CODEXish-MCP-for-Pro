using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

// Byte-level text handling shared by fs_read, fs_write and fs_apply_patch. Encoding, BOM and newline style
// are properties of the file on disk, never assumptions made by the tool.
public static class TextCodec
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static (Encoding Encoding, int Bom, string Text) Decode(byte[] bytes)
    {
        Encoding encoding = new UTF8Encoding(false, true);
        int bom = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            throw new CodexishFault("UNSUPPORTED_CAPABILITY", "UTF-32 files are not decoded by this server; read them as an artifact.");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bom = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); bom = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); bom = 2; }
        try { return (encoding, bom, encoding.GetString(bytes, bom, bytes.Length - bom)); }
        catch (DecoderFallbackException)
        {
            throw new CodexishFault("UNSUPPORTED_CAPABILITY", "The file is not valid UTF-8 or UTF-16 text; read it as an artifact.");
        }
    }

    public static string Newline(string text)
    {
        bool crlf = text.Contains("\r\n");
        string stripped = text.Replace("\r\n", "");
        bool lf = stripped.Contains('\n'), cr = stripped.Contains('\r');
        if (crlf && (lf || cr)) return "mixed";
        if (crlf) return "crlf";
        if (lf && cr) return "mixed";
        if (cr) return "cr";
        return lf ? "lf" : "none";
    }

    public static bool LooksBinary(byte[] bytes)
    {
        int limit = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < limit; i++) if (bytes[i] == 0) return true;
        return false;
    }

    // Rewrites text to the file's existing newline style, refusing the conversions P0 also refuses.
    public static string Normalize(string text, string existing)
    {
        if (existing == "mixed")
            throw new CodexishFault("UNSUPPORTED_CAPABILITY", "This file mixes CRLF and LF; rewrite it explicitly instead of letting the server guess.");
        if (existing == "cr")
            throw new CodexishFault("UNSUPPORTED_CAPABILITY", "CR-only line endings are not rewritten by this server.");
        text = text.Replace("\r\n", "\n");
        if (text.Contains('\r'))
            throw new CodexishFault("INVALID_ARGUMENT", "Supply LF or CRLF line endings, not bare CR.");
        return existing == "crlf" ? text.Replace("\n", "\r\n") : text;
    }
}

public sealed class FileService(Workspace workspace, Artifacts artifacts, Store store, string stateDirectory)
{
    private const long InlineLimit = 8L * 1024 * 1024;

    public string BackupDirectory { get; } = Path.Combine(stateDirectory, "backups");

    public CallToolResult Read(string rootId, string path, int? lineFrom, int? lineTo, long? byteOffset, int? maxBytes)
    {
        var target = workspace.Resolve(rootId, path, Grant.Read);
        using var stream = workspace.OpenRead(target);
        long length = stream.Length;
        if (length > InlineLimit)
        {
            var big = ArtifactOf(target, stream, "application/octet-stream");
            return Reply.Ok(new
            {
                root_id = target.Root.Id, path = target.Relative, bytes = big.Bytes, sha256 = big.Sha256,
                binary = false, stored_as_artifact = true, artifact_id = big.Id,
                reason = $"The file is larger than the {InlineLimit} byte inline budget ({Limits.Source}).",
                next_tool = "artifact_read"
            }, new { text = (string?)null, next_cursor = (string?)null, complete = false, artifact_id = big.Id });
        }
        byte[] bytes = new byte[length];
        stream.ReadExactly(bytes);
        string sha = TextCodec.Hash(bytes);
        if (TextCodec.LooksBinary(bytes))
        {
            var artifact = artifacts.Save(bytes, "application/octet-stream", "fs_read_binary", target.Relative);
            return Reply.Ok(new
            {
                root_id = target.Root.Id, path = target.Relative, bytes = bytes.Length, sha256 = sha,
                binary = true, stored_as_artifact = true, artifact_id = artifact.Id,
                next_tool = "artifact_read"
            }, new { text = (string?)null, next_cursor = (string?)null, complete = false, artifact_id = artifact.Id });
        }

        var (encoding, bom, text) = TextCodec.Decode(bytes);
        string newline = TextCodec.Newline(text);
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        int totalLines = lines.Length;
        string selected;
        int first = 1, last = totalLines;
        bool complete = true;
        if (lineFrom is not null || lineTo is not null)
        {
            first = Math.Max(1, lineFrom ?? 1);
            last = Math.Min(totalLines, lineTo ?? totalLines);
            if (first > totalLines)
                throw new CodexishFault("INVALID_ARGUMENT", $"line_from {first} is past the last line ({totalLines}).");
            if (last < first) throw new CodexishFault("INVALID_ARGUMENT", "line_to must not be smaller than line_from.");
            selected = string.Join("\n", lines[(first - 1)..last]);
            complete = first == 1 && last == totalLines;
        }
        else if (byteOffset is not null || maxBytes is not null)
        {
            long offset = byteOffset ?? 0;
            if (offset < 0 || offset > bytes.Length)
                throw new CodexishFault("INVALID_ARGUMENT", $"byte_offset must be between 0 and {bytes.Length}.");
            int budget = Math.Clamp(maxBytes ?? Limits.ReadBytes, 1, Limits.ReadBytes);
            byte[] slice = bytes[(int)offset..(int)Math.Min(bytes.Length, offset + budget)];
            // A byte range is decoded with the file's own encoding; an arbitrary offset can still split a
            // multi-byte character, which is why line ranges are the preferred way to read part of a file.
            selected = encoding.GetString(slice);
            complete = offset == 0 && slice.Length == bytes.Length;
            first = last = 0;
        }
        else
        {
            selected = text;
            if (Encoding.UTF8.GetByteCount(selected) > Limits.ReadBytes)
            {
                int keep = 0, used = 0;
                while (keep < lines.Length && used + Encoding.UTF8.GetByteCount(lines[keep]) + 1 <= Limits.ReadBytes)
                    used += Encoding.UTF8.GetByteCount(lines[keep++]) + 1;
                selected = string.Join("\n", lines[..keep]);
                last = keep;
                complete = false;
            }
        }
        var (redactedText, redacted, kinds) = Redaction.Apply(selected);
        return Reply.Ok(new
        {
            root_id = target.Root.Id,
            path = target.Relative,
            sha256 = sha,
            bytes = bytes.Length,
            encoding = encoding.WebName,
            bom_bytes = bom,
            newline,
            binary = false,
            line_count = totalLines,
            line_from = first,
            line_to = last,
            complete,
            redacted,
            redacted_kinds = kinds,
            redaction_note = redacted ? Redaction.Disclosure : null,
            limits_source = Limits.Source,
            next_tool = "fs_write with mode=replace and this sha256"
        }, new { text = redactedText, next_cursor = (string?)null, complete, artifact_id = (string?)null });
    }

    private ArtifactRow ArtifactOf(Target target, FileStream stream, string mime)
    {
        var artifact = artifacts.Create(mime, "fs_read_large", target.Relative);
        using (var sink = artifacts.OpenAppend(artifact.Id))
        {
            stream.Position = 0;
            stream.CopyTo(sink);
        }
        return artifacts.Finish(artifact.Id);
    }

    public CallToolResult Stat(string rootId, string path, bool hash)
    {
        var target = workspace.Resolve(rootId, path, Grant.Read);
        if (Directory.Exists(target.FullPath))
        {
            workspace.VerifyDirectory(target);
            var directory = new DirectoryInfo(target.FullPath);
            return Reply.Ok(new
            {
                root_id = target.Root.Id, path = target.Relative, kind = "directory",
                created_utc = directory.CreationTimeUtc, modified_utc = directory.LastWriteTimeUtc,
                entries = directory.EnumerateFileSystemInfos().Count(), next_tool = "fs_list"
            });
        }
        using var stream = workspace.OpenRead(target);
        var info = new FileInfo(target.FullPath);
        byte[]? bytes = stream.Length <= InlineLimit ? new byte[stream.Length] : null;
        if (bytes is not null) stream.ReadExactly(bytes);
        string? sha = hash && bytes is not null ? TextCodec.Hash(bytes) : null;
        string? encoding = null, newline = null;
        int? bom = null;
        bool binary = bytes is not null && TextCodec.LooksBinary(bytes);
        if (bytes is not null && !binary)
        {
            try
            {
                var decoded = TextCodec.Decode(bytes);
                encoding = decoded.Encoding.WebName;
                bom = decoded.Bom;
                newline = TextCodec.Newline(decoded.Text);
            }
            catch (CodexishFault) { binary = true; }
        }
        return Reply.Ok(new
        {
            root_id = target.Root.Id, path = target.Relative, kind = "file", bytes = info.Length,
            created_utc = info.CreationTimeUtc, modified_utc = info.LastWriteTimeUtc,
            read_only = info.IsReadOnly, binary, encoding, bom_bytes = bom, newline, sha256 = sha,
            next_tool = "fs_read"
        });
    }

    public CallToolResult List(string rootId, string path, int depth, bool hidden, string? cursor)
    {
        var target = workspace.Resolve(rootId, path, Grant.Read);
        workspace.VerifyDirectory(target);
        if (depth is < 1 or > 32) throw new CodexishFault("INVALID_ARGUMENT", "depth must be between 1 and 32.");
        string after = DecodeCursor(cursor);
        List<object> entries = [];
        bool more = false;
        // The cursor names the last entry actually returned, so the next page resumes at the first one that
        // was not returned rather than skipping it.
        string? next = null;
        foreach (var entry in Walk(target.FullPath, target.Relative, depth, hidden))
        {
            if (string.CompareOrdinal(entry.Path, after) <= 0) continue;
            if (entries.Count >= Limits.ListEntries) { more = true; break; }
            entries.Add(new { path = entry.Path, kind = entry.Kind, bytes = entry.Bytes, modified_utc = entry.Modified });
            next = entry.Path;
        }
        return Reply.Ok(new
        {
            root_id = target.Root.Id, path = target.Relative, depth, hidden,
            entries, entry_count = entries.Count, truncated = more, limits_source = Limits.Source,
            next_tool = more ? "fs_list with next_cursor" : "fs_read"
        }, new { text = (string?)null, next_cursor = more && next is not null ? EncodeCursor(next) : null, complete = !more, artifact_id = (string?)null });
    }

    private static string EncodeCursor(string value) => ServerConfig.Base64Url(Encoding.UTF8.GetBytes("l1." + value));

    private static string DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return "";
        string padded = cursor.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded)); }
        catch (FormatException) { throw new CodexishFault("CURSOR_INVALID", "The cursor is not a cursor issued by this server."); }
        if (!decoded.StartsWith("l1.", StringComparison.Ordinal))
            throw new CodexishFault("CURSOR_INVALID", "The cursor is not a listing cursor issued by this server.");
        return decoded[3..];
    }

    private sealed record Entry(string Path, string Kind, long? Bytes, DateTime? Modified);

    // Reparse points are listed as their own kind and never traversed, so a junction cannot widen the walk.
    private static IEnumerable<Entry> Walk(string absolute, string relative, int depth, bool hidden)
    {
        List<Entry> found = [];
        void Recurse(string directory, string prefix, int level)
        {
            IEnumerable<FileSystemInfo> children;
            try { children = new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal); }
            catch (UnauthorizedAccessException) { return; }
            catch (DirectoryNotFoundException) { return; }
            foreach (var child in children)
            {
                bool isHidden = (child.Attributes & FileAttributes.Hidden) != 0 || child.Name.StartsWith('.');
                if (isHidden && !hidden) continue;
                string childPath = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    found.Add(new Entry(childPath, "reparse_point", null, child.LastWriteTimeUtc));
                    continue;
                }
                if (child is DirectoryInfo)
                {
                    found.Add(new Entry(childPath, "directory", null, child.LastWriteTimeUtc));
                    if (level >= depth) continue;
                    // Re-read immediately before descending: the entry may have been replaced by a link
                    // between the enumeration and this step.
                    child.Refresh();
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    Recurse(child.FullName, childPath, level + 1);
                }
                else if (child is FileInfo file)
                    found.Add(new Entry(childPath, "file", file.Length, file.LastWriteTimeUtc));
            }
        }
        Recurse(absolute, relative, 1);
        return found.OrderBy(e => e.Path, StringComparer.Ordinal);
    }

    public CallToolResult Search(string rootId, string path, string query, bool regex, string? glob, string? cursor)
    {
        var target = workspace.Resolve(rootId, path, Grant.Read);
        workspace.VerifyDirectory(target);
        if (string.IsNullOrEmpty(query)) throw new CodexishFault("INVALID_ARGUMENT", "query must not be empty.");
        var matcher = TextSearch.Compile(query, regex);
        Regex? globRegex = glob is { Length: > 0 } ? GlobToRegex(glob) : null;
        string after = DecodeCursor(cursor);
        List<object> matches = [];
        List<string> skipped = [];
        bool more = false;
        // A page ends between files and the cursor names the last file finished, so no match is dropped and
        // none is returned twice. A single file with many matches overruns the page instead of losing matches.
        string? next = null;
        foreach (var entry in Walk(target.FullPath, target.Relative, 32, true).Where(e => e.Kind == "file"))
        {
            if (string.CompareOrdinal(entry.Path, after) <= 0) continue;
            if (globRegex is not null && !globRegex.IsMatch(entry.Path)) continue;
            if (matches.Count >= Limits.SearchMatches) { more = true; break; }
            var file = workspace.Resolve(rootId, entry.Path, Grant.Read);
            try
            {
                using var stream = workspace.OpenRead(file);
                if (stream.Length > InlineLimit) { skipped.Add(entry.Path + " (larger than the inline budget)"); continue; }
                int lineNumber = 0;
                foreach (var (text, _, _) in TextSearch.Lines(stream))
                {
                    lineNumber++;
                    foreach (var span in matcher(text))
                    {
                        var (preview, _, _) = Redaction.Apply(TextSearch.Preview(text, span.Start));
                        matches.Add(new { path = entry.Path, line = lineNumber, column = span.Start + 1, length = span.Length, preview });
                    }
                }
                next = entry.Path;
            }
            catch (CodexishFault fault) { skipped.Add($"{entry.Path} ({fault.Code})"); }
            catch (IOException) { skipped.Add(entry.Path + " (IO_ERROR)"); }
        }
        return Reply.Ok(new
        {
            root_id = target.Root.Id, path = target.Relative, query, regex, glob,
            matches, match_count = matches.Count, skipped, truncated = more,
            searched = "in-process; no external searcher is launched", limits_source = Limits.Source
        }, new { text = (string?)null, next_cursor = more && next is not null ? EncodeCursor(next) : null, complete = !more, artifact_id = (string?)null });
    }

    public static Regex GlobToRegex(string glob)
    {
        var pattern = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*') { pattern.Append(".*"); i++; if (i + 1 < glob.Length && glob[i + 1] == '/') i++; }
            else if (c == '*') pattern.Append("[^/]*");
            else if (c == '?') pattern.Append("[^/]");
            else if (c == '/') pattern.Append('/');
            else pattern.Append(Regex.Escape(c.ToString()));
        }
        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    }

    // D6 create: atomic CreateNew; replace: one exclusive handle, expected hash, backup, in-place write.
    public CallToolResult Write(string rootId, string path, string text, string mode, string? expectedSha256)
    {
        var target = workspace.Resolve(rootId, path, Grant.Write);
        if (mode == "create")
        {
            if (!string.IsNullOrEmpty(expectedSha256))
                throw new CodexishFault("INVALID_ARGUMENT", "mode=create must not carry expected_sha256; the file must not exist yet.");
            string created = text.Contains("\r\n") ? text : text.Replace("\r\n", "\n");
            byte[] bytes = new UTF8Encoding(false).GetBytes(created);
            using var stream = workspace.CreateNew(target);
            stream.Write(bytes);
            stream.Flush(true);
            store.Event("fs_write", target.Relative, new { root = target.Root.Id, mode, bytes = bytes.Length });
            return Reply.Ok(new
            {
                root_id = target.Root.Id, path = target.Relative, mode, bytes = bytes.Length,
                sha256 = TextCodec.Hash(bytes), encoding = "utf-8", bom_bytes = 0,
                newline = TextCodec.Newline(created), save_mode = "atomic_create_new", complete = true,
                next_tool = "fs_read to confirm, then run the project's tests with shell_run"
            });
        }
        if (mode != "replace")
            throw new CodexishFault("INVALID_ARGUMENT", "mode must be create or replace.");
        if (string.IsNullOrEmpty(expectedSha256))
            throw new CodexishFault("INVALID_ARGUMENT", "mode=replace requires expected_sha256 from a preceding fs_read.");
        using (var stream = workspace.OpenExclusive(target))
        {
            byte[] before = new byte[stream.Length];
            stream.ReadExactly(before);
            if (!TextCodec.Hash(before).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new CodexishFault("FILE_CHANGED",
                    "expected_sha256 does not match the file on disk. Reread, merge the user's change, and write again.");
            var (encoding, bom, oldText) = TextCodec.Decode(before);
            string written = TextCodec.Normalize(text, TextCodec.Newline(oldText));
            byte[] after = before.Take(bom).Concat(encoding.GetBytes(written)).ToArray();
            string backup = Backup(before);
            try
            {
                stream.Position = 0;
                stream.Write(after);
                stream.SetLength(after.Length);
                stream.Flush(true);
            }
            catch (IOException)
            {
                throw new CodexishFault("EXECUTION_UNKNOWN",
                    $"The in-place write was interrupted; the previous bytes are preserved locally as {backup}.", "partial");
            }
            store.Event("fs_write", target.Relative, new { root = target.Root.Id, mode, backup });
            return Reply.Ok(new
            {
                root_id = target.Root.Id, path = target.Relative, mode,
                old_sha256 = TextCodec.Hash(before), sha256 = TextCodec.Hash(after), bytes = after.Length,
                encoding = encoding.WebName, bom_bytes = bom, newline = TextCodec.Newline(oldText), backup,
                save_mode = "exclusive_in_place_non_atomic", complete = true,
                next_tool = "run the project's tests with shell_run"
            });
        }
    }

    public string Backup(byte[] bytes)
    {
        Directory.CreateDirectory(BackupDirectory);
        string name = Guid.NewGuid().ToString("N") + ".bak";
        using var saved = new FileStream(Path.Combine(BackupDirectory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        saved.Write(bytes);
        saved.Flush(true);
        return "backups/" + name;
    }

    public Workspace Workspace => workspace;
    public Store Store => store;
}
