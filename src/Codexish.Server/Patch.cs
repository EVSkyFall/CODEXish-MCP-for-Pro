using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

public sealed class PatchExpectation
{
    [JsonPropertyName("path")]
    [Description("Path of this file relative to the root, matching the path in the diff header.")]
    public string Path { get; set; } = "";

    [JsonPropertyName("expected_sha256")]
    [Description("sha256 from the preceding fs_read. Empty or omitted only for a file the patch creates.")]
    public string? ExpectedSha256 { get; set; }
}

public sealed record PatchHunk(int OldStart, int OldCount, int NewStart, int NewCount, List<string> Lines);

public sealed record PatchFile(string Path, bool Create, bool Delete, List<PatchHunk> Hunks)
{
    public bool OldLacksFinalNewline { get; set; }
    public bool NewLacksFinalNewline { get; set; }
}

// D6: a unified diff may touch several files. Preflight checks every file first; apply then runs file by file
// and reports each one. There is no rollback and no claim of whole-patch atomicity.
public static class UnifiedDiff
{
    public static List<PatchFile> Parse(string patch)
    {
        if (string.IsNullOrWhiteSpace(patch)) throw new CodexishFault("INVALID_ARGUMENT", "patch must not be empty.");
        string[] lines = patch.Replace("\r\n", "\n").Split('\n');
        List<PatchFile> files = [];
        PatchFile? current = null;
        PatchHunk? hunk = null;
        string? oldHeader = null;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("--- ", StringComparison.Ordinal)) { oldHeader = line[4..].Trim(); hunk = null; continue; }
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                string newHeader = line[4..].Trim();
                bool create = IsNull(oldHeader);
                bool delete = IsNull(newHeader);
                if (create && delete) throw new CodexishFault("INVALID_ARGUMENT", "A diff section cannot create and delete the same file.");
                string path = Strip(delete ? oldHeader ?? "" : newHeader);
                if (path.Length == 0) throw new CodexishFault("INVALID_ARGUMENT", "A diff section has no usable file path.");
                current = new PatchFile(path, create, delete, []);
                files.Add(current);
                hunk = null;
                continue;
            }
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (current is null) throw new CodexishFault("INVALID_ARGUMENT", "A hunk appears before any file header in the patch.");
                hunk = ParseHunkHeader(line);
                current.Hunks.Add(hunk);
                continue;
            }
            if (hunk is null || current is null) continue;
            if (line.StartsWith('\\'))
            {
                string previous = hunk.Lines.Count > 0 ? hunk.Lines[^1] : " ";
                if (previous.StartsWith('-') || previous.StartsWith(' ')) current.OldLacksFinalNewline = true;
                if (previous.StartsWith('+') || previous.StartsWith(' ')) current.NewLacksFinalNewline = true;
                continue;
            }
            if (line.Length == 0)
            {
                // A completely empty line inside a hunk is an empty context line unless the hunk is finished.
                if (Counted(hunk) >= hunk.OldCount + hunk.NewCount) { hunk = null; continue; }
                hunk.Lines.Add(" ");
                continue;
            }
            if (line[0] is ' ' or '+' or '-') hunk.Lines.Add(line);
            else hunk = null;
        }
        if (files.Count == 0) throw new CodexishFault("INVALID_ARGUMENT", "The patch contains no '--- / +++' file sections.");
        foreach (var file in files)
            if (file.Hunks.Count == 0) throw new CodexishFault("INVALID_ARGUMENT", $"The diff section for '{file.Path}' has no hunks.");
        return files;
    }

    private static int Counted(PatchHunk hunk) =>
        hunk.Lines.Count(l => l.StartsWith(' ')) * 2 + hunk.Lines.Count(l => l.StartsWith('-') || l.StartsWith('+'));

    private static bool IsNull(string? header) =>
        header is not null && (header == "/dev/null" || header.StartsWith("/dev/null\t", StringComparison.Ordinal));

    private static string Strip(string header)
    {
        string path = header.Split('\t')[0].Trim();
        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)) path = path[2..];
        return path;
    }

    private static PatchHunk ParseHunkHeader(string line)
    {
        int end = line.IndexOf("@@", 2, StringComparison.Ordinal);
        if (end < 0) throw new CodexishFault("INVALID_ARGUMENT", $"Malformed hunk header: {line}");
        string[] parts = line[2..end].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].StartsWith('-') || !parts[1].StartsWith('+'))
            throw new CodexishFault("INVALID_ARGUMENT", $"Malformed hunk header: {line}");
        var (oldStart, oldCount) = Range(parts[0][1..]);
        var (newStart, newCount) = Range(parts[1][1..]);
        return new PatchHunk(oldStart, oldCount, newStart, newCount, []);
    }

    private static (int Start, int Count) Range(string value)
    {
        string[] parts = value.Split(',');
        if (!int.TryParse(parts[0], out int start) || start < 0)
            throw new CodexishFault("INVALID_ARGUMENT", $"Malformed hunk range: {value}");
        int count = 1;
        if (parts.Length > 1 && !int.TryParse(parts[1], out count))
            throw new CodexishFault("INVALID_ARGUMENT", $"Malformed hunk range: {value}");
        return (start, count);
    }

    // Applies every hunk at its stated position with exact context. No fuzz, no offset search.
    public static List<string> Apply(PatchFile file, List<string> original)
    {
        List<string> output = [];
        int cursor = 0;
        foreach (var hunk in file.Hunks)
        {
            int start = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1;
            if (start < cursor || start > original.Count)
                throw new CodexishFault("PATCH_FAILED", $"Hunk at old line {hunk.OldStart} of '{file.Path}' is out of order or past the end of the file.");
            for (int i = cursor; i < start; i++) output.Add(original[i]);
            cursor = start;
            foreach (string line in hunk.Lines)
            {
                string content = line.Length > 0 ? line[1..] : "";
                switch (line[0])
                {
                    case ' ':
                        if (cursor >= original.Count || original[cursor] != content)
                            throw new CodexishFault("PATCH_FAILED",
                                $"Context line {cursor + 1} of '{file.Path}' does not match the file. Reread the file and rebuild the patch.");
                        output.Add(original[cursor++]);
                        break;
                    case '-':
                        if (cursor >= original.Count || original[cursor] != content)
                            throw new CodexishFault("PATCH_FAILED",
                                $"Removed line {cursor + 1} of '{file.Path}' does not match the file. Reread the file and rebuild the patch.");
                        cursor++;
                        break;
                    case '+':
                        output.Add(content);
                        break;
                }
            }
        }
        for (int i = cursor; i < original.Count; i++) output.Add(original[i]);
        return output;
    }

    public static List<string> Split(string text, out bool endsWithNewline)
    {
        endsWithNewline = text.EndsWith('\n');
        string body = endsWithNewline ? text[..^1] : text;
        return body.Length == 0 && endsWithNewline ? [""] : [.. body.Split('\n')];
    }

    public static string Join(List<string> lines, bool endsWithNewline) =>
        string.Join("\n", lines) + (endsWithNewline ? "\n" : "");
}

public sealed class PatchService(Workspace workspace, FileService files, Store store)
{
    private sealed record Plan(PatchFile File, Target Target, string? Expected, byte[]? Before, Encoding? Encoding,
        int Bom, string Newline, List<string>? Result, bool EndsWithNewline);

    public CallToolResult Apply(string rootId, string patch, PatchExpectation[] expected)
    {
        var parsed = UnifiedDiff.Parse(patch);
        var declared = expected.ToDictionary(e => e.Path.Replace('\\', '/').Trim(), e => e.ExpectedSha256, StringComparer.OrdinalIgnoreCase);
        List<Plan> plans = [];
        List<object> preflight = [];
        bool blocked = false;

        foreach (var file in parsed)
        {
            string path = file.Path.Replace('\\', '/');
            try
            {
                if (!declared.TryGetValue(path, out string? sha))
                    throw new CodexishFault("INVALID_ARGUMENT",
                        $"'{path}' is changed by the patch but has no entry in expected[]. List every touched file.");
                var target = workspace.Resolve(rootId, path, Grant.Write);
                if (file.Create)
                {
                    if (!string.IsNullOrEmpty(sha))
                        throw new CodexishFault("INVALID_ARGUMENT", $"'{path}' is created by the patch, so expected_sha256 must be empty.");
                    if (File.Exists(target.FullPath) || Directory.Exists(target.FullPath))
                        throw new CodexishFault("FILE_CHANGED", $"'{path}' is created by the patch but already exists.");
                    var created = UnifiedDiff.Apply(file, []);
                    plans.Add(new Plan(file, target, null, null, null, 0, "lf", created, !file.NewLacksFinalNewline));
                }
                else
                {
                    if (string.IsNullOrEmpty(sha))
                        throw new CodexishFault("INVALID_ARGUMENT", $"'{path}' already exists, so expected_sha256 is required.");
                    using var stream = workspace.OpenRead(target);
                    byte[] before = new byte[stream.Length];
                    stream.ReadExactly(before);
                    if (!TextCodec.Hash(before).Equals(sha, StringComparison.OrdinalIgnoreCase))
                        throw new CodexishFault("FILE_CHANGED", $"expected_sha256 for '{path}' does not match the file on disk.");
                    var (encoding, bom, text) = TextCodec.Decode(before);
                    string newline = TextCodec.Newline(text);
                    if (newline is "mixed" or "cr")
                        throw new CodexishFault("UNSUPPORTED_CAPABILITY", $"'{path}' has {newline} line endings; this server does not rewrite them.");
                    var original = UnifiedDiff.Split(text.Replace("\r\n", "\n"), out bool endsWithNewline);
                    if (file.OldLacksFinalNewline && endsWithNewline)
                        throw new CodexishFault("PATCH_FAILED", $"The patch says '{path}' has no final newline, but the file does.");
                    var result = file.Delete ? null : UnifiedDiff.Apply(file, original);
                    if (file.Delete && original.Count > 0 && file.Hunks.Sum(h => h.Lines.Count(l => l.StartsWith('-'))) != original.Count)
                        throw new CodexishFault("PATCH_FAILED", $"The deletion diff for '{path}' does not remove every line of the file.");
                    bool resultEnds = file.Delete ? endsWithNewline : !file.NewLacksFinalNewline;
                    plans.Add(new Plan(file, target, sha, before, encoding, bom, newline, result, resultEnds));
                }
                preflight.Add(new { path, operation = Operation(file), preflight = "ok" });
            }
            catch (CodexishFault fault)
            {
                blocked = true;
                preflight.Add(new { path, operation = Operation(file), preflight = "failed", code = fault.Code, message = fault.Message });
            }
        }

        if (blocked)
            return Reply.Error("PATCH_FAILED",
                "Preflight rejected the patch; no file was changed. Fix the listed files and submit a new patch.",
                data: new { root_id = rootId, applied = false, files = preflight, atomicity = "preflight_all_or_nothing" },
                recovery: "fs_read the listed files and rebuild the patch");

        List<object> results = [];
        int applied = 0, failed = 0;
        foreach (var plan in plans)
        {
            try
            {
                results.Add(Execute(plan));
                applied++;
            }
            catch (CodexishFault fault)
            {
                failed++;
                results.Add(new { path = plan.File.Path, operation = Operation(plan.File), status = "failed",
                    code = fault.Code, message = fault.Message });
            }
        }
        store.Event("fs_apply_patch", rootId, new { applied, failed });
        var data = new
        {
            root_id = rootId,
            files = results,
            applied_files = applied,
            failed_files = failed,
            atomicity = "per_file; there is no rollback and no whole-patch transaction",
            next_tool = "fs_read the changed files, then run the project's tests with shell_run"
        };
        return failed == 0
            ? Reply.Ok(data)
            : Reply.Error("EXECUTION_FAILED",
                $"{applied} file(s) were changed and {failed} failed. Nothing was rolled back; inspect each file.",
                applied > 0 ? "partial" : "none", data: data, recovery: "fs_read each listed file before retrying");
    }

    private object Execute(Plan plan)
    {
        var file = plan.File;
        if (file.Create)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(UnifiedDiff.Join(plan.Result!, plan.EndsWithNewline));
            using var stream = workspace.CreateNew(plan.Target);
            stream.Write(bytes);
            stream.Flush(true);
            return new { path = file.Path, operation = "create", status = "succeeded", old_sha256 = (string?)null,
                sha256 = TextCodec.Hash(bytes), bytes = bytes.Length, backup = (string?)null, save_mode = "atomic_create_new" };
        }
        using (var stream = workspace.OpenExclusive(plan.Target))
        {
            byte[] before = new byte[stream.Length];
            stream.ReadExactly(before);
            // The hash is checked again under the exclusive handle: the file may have changed since preflight.
            if (!TextCodec.Hash(before).Equals(plan.Expected, StringComparison.OrdinalIgnoreCase))
                throw new CodexishFault("FILE_CHANGED", $"'{file.Path}' changed between preflight and apply.");
            string backup = files.Backup(before);
            if (file.Delete)
            {
                stream.Dispose();
                File.Delete(plan.Target.FullPath);
                return new { path = file.Path, operation = "delete", status = "succeeded",
                    old_sha256 = TextCodec.Hash(before), sha256 = (string?)null, bytes = 0, backup, save_mode = "delete_after_backup" };
            }
            string text = UnifiedDiff.Join(plan.Result!, plan.EndsWithNewline);
            if (plan.Newline == "crlf") text = text.Replace("\n", "\r\n");
            byte[] after = before.Take(plan.Bom).Concat(plan.Encoding!.GetBytes(text)).ToArray();
            stream.Position = 0;
            stream.Write(after);
            stream.SetLength(after.Length);
            stream.Flush(true);
            return new { path = file.Path, operation = "modify", status = "succeeded", old_sha256 = TextCodec.Hash(before),
                sha256 = TextCodec.Hash(after), bytes = after.Length, backup, save_mode = "exclusive_in_place_non_atomic" };
        }
    }

    private static string Operation(PatchFile file) => file.Create ? "create" : file.Delete ? "delete" : "modify";
}
