using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

// D10. Artifact bytes live as files under state_dir\artifacts; the row carries mime, size, hash, completeness
// and a generation. Cursors are bound to the generation, so a cursor from a replaced artifact is refused
// instead of being reinterpreted against different bytes.
public sealed class Artifacts(Store store, string directory)
{
    public const int Collecting = 0;
    public const int Complete = 1;
    public const int Incomplete = 2;

    public string Directory { get; } = directory;

    private string PathOf(string id) => Path.Combine(Directory, id + ".bin");

    public ArtifactRow Create(string mime, string kind, string? reference)
    {
        string id = "art_" + Guid.NewGuid().ToString("N");
        string path = PathOf(id);
        System.IO.Directory.CreateDirectory(Directory);
        using (new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { }
        var row = new ArtifactRow(id, mime, 0, null, path, Collecting, 1, DateTimeOffset.UtcNow.ToString("o"));
        store.InsertArtifact(row);
        store.Event("artifact_created", id, new { mime, kind, reference });
        return row;
    }

    public ArtifactRow Save(byte[] bytes, string mime, string kind, string? reference)
    {
        var row = Create(mime, kind, reference);
        File.WriteAllBytes(row.Path, bytes);
        return Finish(row.Id);
    }

    // The live writer keeps FileShare.Read so process_poll and artifact_read can follow a running process.
    // This is deliberately not the exclusive-handle pattern used for user files.
    public FileStream OpenAppend(string id) =>
        new(PathOf(id), FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);

    public FileStream OpenRead(string id) =>
        new(PathOf(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public long Length(string id)
    {
        var info = new FileInfo(PathOf(id));
        return info.Exists ? info.Length : 0;
    }

    public ArtifactRow Finish(string id)
    {
        var row = Row(id);
        using var stream = OpenRead(id);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        long length = Length(id);
        store.UpdateArtifact(id, length, hash, Complete, row.Generation);
        store.Event("artifact_complete", id, new { bytes = length });
        return row with { Bytes = length, Sha256 = hash, Complete = Complete };
    }

    // Called when collection ended before the producer finished, for example a stream read that failed
    // while the child was still writing. The preserved prefix stays readable.
    public void MarkIncomplete(string id, string reason)
    {
        var row = Row(id);
        store.UpdateArtifact(id, Length(id), row.Sha256, Incomplete, row.Generation);
        store.Event("artifact_incomplete", id, new { reason });
    }

    // Replacing an artifact's bytes advances the generation so outstanding cursors become invalid
    // rather than pointing into different content at the same offset.
    public void Truncate(string id)
    {
        var row = Row(id);
        using (new FileStream(PathOf(id), FileMode.Create, FileAccess.Write, FileShare.Read)) { }
        store.UpdateArtifact(id, 0, null, Collecting, row.Generation + 1);
        store.Event("artifact_truncated", id, new { generation = row.Generation + 1 });
    }

    public ArtifactRow Row(string id) => store.Artifact(id)
        ?? throw new CodexishFault("NOT_FOUND", $"Unknown artifact_id '{id}'.");

    public static string Cursor(int generation, long offset) =>
        ServerConfig.Base64Url(Encoding.UTF8.GetBytes($"a1.{generation}.{offset}"));

    public static (int Generation, long Offset) ParseCursor(string cursor)
    {
        string padded = cursor.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        string[] parts;
        try { parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('.'); }
        catch (FormatException) { throw new CodexishFault("CURSOR_INVALID", "The cursor is not a cursor issued by this server."); }
        if (parts.Length != 3 || parts[0] != "a1" || !int.TryParse(parts[1], out int generation) || !long.TryParse(parts[2], out long offset))
            throw new CodexishFault("CURSOR_INVALID", "The cursor is not a cursor issued by this server.");
        return (generation, offset);
    }

    public byte[] ReadBytes(string id, long offset, int count)
    {
        using var stream = OpenRead(id);
        if (offset > stream.Length) return [];
        stream.Position = offset;
        byte[] buffer = new byte[Math.Min(count, Math.Max(0, stream.Length - offset))];
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk == 0) break;
            read += chunk;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    public CallToolResult Read(string id, long? offset, int? maxBytes, int? lineFrom, int? lineTo, string? cursor)
    {
        var row = Row(id);
        long available = Length(id);
        long start = offset ?? 0;
        if (cursor is { Length: > 0 })
        {
            var (generation, cursorOffset) = ParseCursor(cursor);
            if (generation != row.Generation)
                throw new CodexishFault("CURSOR_INVALID",
                    "This cursor belongs to an earlier generation of the artifact; reread from the start.",
                    details: new { cursor_generation = generation, current_generation = row.Generation });
            start = cursorOffset;
        }
        if (start < 0) throw new CodexishFault("INVALID_ARGUMENT", "offset must not be negative.");
        int budget = Math.Clamp(maxBytes ?? Limits.ArtifactBytes, 1, Limits.ArtifactBytes);

        if (lineFrom is not null || lineTo is not null)
            return ReadLines(row, available, lineFrom ?? 1, lineTo, budget);

        if (start >= available && row.Complete == Incomplete)
            throw new CodexishFault("OUTPUT_INCOMPLETE",
                $"Collection for this artifact ended before the producer finished; {available} preserved bytes are readable from offset 0.",
                details: new { bytes = available, artifact_id = id });

        byte[] bytes = ReadBytes(id, start, budget);
        long next = start + bytes.Length;
        bool drained = next >= available;
        var (text, redacted, kinds) = Redaction.Apply(Encoding.UTF8.GetString(bytes));
        return Reply.Ok(new
        {
            artifact_id = id,
            mime = row.Mime,
            generation = row.Generation,
            byte_offset = start,
            bytes_returned = bytes.Length,
            bytes_available = available,
            collection = Describe(row.Complete),
            sha256 = row.Sha256,
            redacted,
            redacted_kinds = kinds,
            redaction_note = Redaction.Disclosure,
            limits_source = Limits.Source
        }, new
        {
            text,
            next_cursor = drained && row.Complete == Complete ? null : Cursor(row.Generation, next),
            complete = drained,
            artifact_id = id
        });
    }

    private CallToolResult ReadLines(ArtifactRow row, long available, int from, int? to, int budget)
    {
        if (from < 1) throw new CodexishFault("INVALID_ARGUMENT", "line_from is 1-based.");
        int last = to ?? int.MaxValue;
        if (last < from) throw new CodexishFault("INVALID_ARGUMENT", "line_to must not be smaller than line_from.");
        using var stream = OpenRead(row.Id);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), true);
        var builder = new StringBuilder();
        int line = 0, returned = 0;
        bool truncated = false;
        while (reader.ReadLine() is { } text)
        {
            line++;
            if (line < from) continue;
            if (line > last) break;
            if (builder.Length + text.Length > budget) { truncated = true; break; }
            builder.Append(text).Append('\n');
            returned++;
        }
        var (redactedText, redacted, kinds) = Redaction.Apply(builder.ToString());
        return Reply.Ok(new
        {
            artifact_id = row.Id,
            mime = row.Mime,
            generation = row.Generation,
            line_from = from,
            lines_returned = returned,
            bytes_available = available,
            collection = Describe(row.Complete),
            truncated_by_budget = truncated,
            redacted,
            redacted_kinds = kinds,
            limits_source = Limits.Source
        }, new { text = redactedText, next_cursor = (string?)null, complete = !truncated, artifact_id = row.Id });
    }

    public CallToolResult Search(string id, string query, bool regex, string? cursor)
    {
        if (string.IsNullOrEmpty(query)) throw new CodexishFault("INVALID_ARGUMENT", "query must not be empty.");
        var row = Row(id);
        long start = 0;
        if (cursor is { Length: > 0 })
        {
            var (generation, cursorOffset) = ParseCursor(cursor);
            if (generation != row.Generation)
                throw new CodexishFault("CURSOR_INVALID", "This cursor belongs to an earlier generation of the artifact.");
            start = cursorOffset;
        }
        var matcher = TextSearch.Compile(query, regex);
        List<object> matches = [];
        long offset = 0;
        int lineNumber = 0;
        bool more = false;
        long nextOffset = start;
        using (var stream = OpenRead(id))
        {
            foreach (var (text, lineStart, lineBytes) in TextSearch.Lines(stream))
            {
                lineNumber++;
                offset = lineStart;
                if (lineStart < start) continue;
                foreach (var span in matcher(text))
                {
                    var (preview, _, _) = Redaction.Apply(TextSearch.Preview(text, span.Start));
                    matches.Add(new { line = lineNumber, byte_offset = lineStart, column = span.Start + 1, length = span.Length, preview });
                    if (matches.Count >= Limits.SearchMatches) break;
                }
                if (matches.Count >= Limits.SearchMatches)
                {
                    more = true;
                    nextOffset = lineStart + lineBytes;
                    break;
                }
            }
        }
        return Reply.Ok(new
        {
            artifact_id = id,
            generation = row.Generation,
            matches,
            match_count = matches.Count,
            collection = Describe(row.Complete),
            scanned_from_byte = start,
            last_scanned_byte = offset,
            limits_source = Limits.Source
        }, new { text = (string?)null, next_cursor = more ? Cursor(row.Generation, nextOffset) : null, complete = !more, artifact_id = id });
    }

    public static string Describe(int complete) => complete switch
    {
        Complete => "complete",
        Incomplete => "incomplete",
        _ => "in_progress"
    };
}
