using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

// D8. Read-only Git runs the configured binary with fixed argument sets and a fixed set of -c overrides that
// disable every documented path from a repository into arbitrary code: hooks, fsmonitor, pager, external diff,
// editor and textconv. Git writes are not tools; they go through shell_run under the root's shell grant.
public sealed partial class GitService(ServerConfig config, Workspace workspace, Artifacts artifacts, Store store, string hooksDirectory)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9._/@^~{}-]+$")] private static partial Regex RefPattern();

    // ref and path arrive from the model. Anything that could be read as an option is refused before git runs,
    // and --end-of-options / -- are used so git itself cannot reinterpret a value as a switch.
    public static string CheckRef(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new CodexishFault("INVALID_ARGUMENT", "ref must not be empty.");
        if (reference.StartsWith('-'))
            throw new CodexishFault("INVALID_ARGUMENT", "ref must not start with '-'; option-like refs are refused before git runs.");
        if (reference.Length > 256 || !RefPattern().IsMatch(reference))
            throw new CodexishFault("INVALID_ARGUMENT",
                "ref must match ^[A-Za-z0-9._/@^~{}-]+$ and be at most 256 characters.");
        // Range syntax such as main..HEAD stays legal; a filesystem traversal is never a revision.
        if (reference is ".." or "." || reference.StartsWith("../", StringComparison.Ordinal) ||
            reference.StartsWith("./", StringComparison.Ordinal) || reference.Contains("/../", StringComparison.Ordinal))
            throw new CodexishFault("INVALID_ARGUMENT", "ref must be a revision, not a filesystem path traversal.");
        return reference;
    }

    private List<string> BaseArguments() =>
    [
        // An empty directory is used instead of a device name so no hook can be found under any name.
        "-c", "core.hooksPath=" + hooksDirectory,
        "-c", "core.fsmonitor=false",
        "-c", "core.pager=cat",
        "-c", "diff.external=",
        "-c", "core.editor=true",
        // A signature check shells out to gpg, and either maintenance task can start a background process.
        "-c", "log.showSignature=false",
        "-c", "maintenance.auto=false",
        "-c", "gc.auto=0",
        "--no-optional-locks"
    ];

    // --no-lazy-fetch keeps a read of a partial clone from starting a network fetch, which would run the
    // remote helper and any credential helper. It does not exist in older git (2.40 rejects it), so support is
    // probed once and the flag is only used where it is understood.
    private int lazyFetch = -1;

    private async Task<bool> SupportsNoLazyFetch(RootConfig root, CancellationToken token)
    {
        if (lazyFetch >= 0) return lazyFetch == 1;
        var probe = BaseArguments();
        probe.AddRange(["--no-lazy-fetch", "--version"]);
        var (exitCode, _, _) = await Run(root, probe, token);
        lazyFetch = exitCode == 0 ? 1 : 0;
        store.Event("git_capability", root.Id, new { no_lazy_fetch = lazyFetch == 1 });
        return lazyFetch == 1;
    }

    // A clean or process filter declared by .gitattributes also runs during status and diff, so the filters the
    // repository declares are read by NAME (never by value) and then disabled for the real query. git config
    // exits 1 when nothing matches, which is not an error here.
    private async Task<List<string>> Arguments(RootConfig root, CancellationToken token)
    {
        var arguments = BaseArguments();
        if (await SupportsNoLazyFetch(root, token)) arguments.Add("--no-lazy-fetch");
        var query = BaseArguments();
        query.AddRange(["config", "--null", "--name-only", "--get-regexp", @"^filter\..*\.(clean|process|required)$"]);
        var (exitCode, stdout, _) = await Run(root, query, token);
        if (exitCode is not (0 or 1)) return arguments;
        foreach (string name in stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string key = name.Trim();
            if (key.Length == 0 || key.Contains('=') || key.Any(char.IsControl)) continue;
            arguments.Add("-c");
            arguments.Add(key + (key.EndsWith(".required", StringComparison.OrdinalIgnoreCase) ? "=false" : "="));
        }
        return arguments;
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> Run(RootConfig root, List<string> arguments, CancellationToken token)
    {
        var gate = locks.GetOrAdd(root.Id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(hooksDirectory);
            var start = new ProcessStartInfo(config.Git.Path)
            {
                WorkingDirectory = root.Path,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var process = new Process { StartInfo = start };
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new CodexishFault("UNSUPPORTED_CAPABILITY",
                    $"git could not be started from '{config.Git.Path}': {e.Message}. Set git.path in codexish.json.");
            }
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            store.Event("git", root.Id, new { arguments = arguments.Count, exit_code = process.ExitCode });
            return (process.ExitCode, await stdout, await stderr);
        }
        finally { gate.Release(); }
    }

    private static CallToolResult Failed(string command, int exitCode, string stderr) =>
        Reply.Error("EXECUTION_FAILED", $"git {command} exited with code {exitCode}.", details: new
        {
            exit_code = exitCode,
            stderr = Redaction.Apply(stderr).Text,
            note = "This is git's own exit code and stderr, not an interpretation."
        }, recovery: "read stderr, fix the repository state, then call the tool again");

    public async Task<CallToolResult> Status(string rootId, CancellationToken token)
    {
        var target = workspace.Resolve(rootId, null, Grant.Read);
        var arguments = await Arguments(target.Root, token);
        arguments.AddRange(["status", "--porcelain=v2", "--branch", "--untracked-files=all"]);
        var (exitCode, stdout, stderr) = await Run(target.Root, arguments, token);
        if (exitCode != 0) return Failed("status", exitCode, stderr);
        string? branch = null, upstream = null, head = null;
        int ahead = 0, behind = 0;
        List<object> entries = [];
        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal)) branch = line[14..];
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal)) upstream = line[18..];
            else if (line.StartsWith("# branch.oid ", StringComparison.Ordinal)) head = line[13..];
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                string[] parts = line[12..].Split(' ');
                if (parts.Length == 2)
                {
                    int.TryParse(parts[0].TrimStart('+'), out ahead);
                    int.TryParse(parts[1].TrimStart('-'), out behind);
                }
            }
            else if (line.StartsWith("1 ", StringComparison.Ordinal))
            {
                string[] fields = line.Split(' ', 9);
                if (fields.Length >= 9) entries.Add(Entry(fields[1], fields[8], "modified", null));
            }
            else if (line.StartsWith("2 ", StringComparison.Ordinal))
            {
                string[] fields = line.Split(' ', 10);
                if (fields.Length >= 10)
                {
                    string[] paths = fields[9].Split('\t');
                    entries.Add(Entry(fields[1], paths[0], "renamed", paths.Length > 1 ? paths[1] : null));
                }
            }
            else if (line.StartsWith("u ", StringComparison.Ordinal))
            {
                string[] fields = line.Split(' ', 11);
                if (fields.Length >= 11) entries.Add(Entry(fields[1], fields[10], "unmerged", null));
            }
            else if (line.StartsWith("? ", StringComparison.Ordinal)) entries.Add(Entry("??", line[2..], "untracked", null));
            else if (line.StartsWith("! ", StringComparison.Ordinal)) entries.Add(Entry("!!", line[2..], "ignored", null));
        }
        return Reply.Ok(new
        {
            root_id = target.Root.Id,
            branch,
            head,
            upstream,
            ahead,
            behind,
            entries,
            entry_count = entries.Count,
            untracked_included = true,
            execution = Execution,
            next_tool = "git_diff for content, fs_read for a single file"
        });
    }

    private static object Entry(string xy, string path, string kind, string? from) => new
    {
        path,
        renamed_from = from,
        staged = Describe(xy.Length > 0 ? xy[0] : '.'),
        worktree = Describe(xy.Length > 1 ? xy[1] : '.'),
        kind,
        code = xy
    };

    private static string Describe(char code) => code switch
    {
        '.' => "unchanged", 'M' => "modified", 'A' => "added", 'D' => "deleted", 'R' => "renamed",
        'C' => "copied", 'U' => "unmerged", '?' => "untracked", '!' => "ignored", _ => code.ToString()
    };

    public async Task<CallToolResult> Diff(string rootId, string? reference, string? path, bool staged, CancellationToken token)
    {
        var target = workspace.Resolve(rootId, null, Grant.Read);
        string? relative = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            // The same fence as the file tools: a path that leaves the root never reaches git.
            relative = workspace.Resolve(rootId, path, Grant.Read).Relative;
            if (relative.Length == 0)
                throw new CodexishFault("INVALID_ARGUMENT", "path must name a file or directory inside the root, not the root itself.");
        }
        // Model-supplied values are validated before any git process starts, including the filter query.
        string? checkedRef = string.IsNullOrWhiteSpace(reference) ? null : CheckRef(reference);
        var arguments = await Arguments(target.Root, token);
        arguments.AddRange(["diff", "--no-ext-diff", "--no-textconv"]);
        if (staged) arguments.Add("--cached");
        arguments.Add("--end-of-options");
        if (checkedRef is not null) arguments.Add(checkedRef);
        arguments.Add("--");
        if (relative is not null) arguments.Add(relative);
        var (exitCode, stdout, stderr) = await Run(target.Root, arguments, token);
        if (exitCode != 0) return Failed("diff", exitCode, stderr);
        var (text, redacted, kinds) = Redaction.Apply(stdout);
        string? artifactId = null;
        bool complete = true;
        if (Encoding.UTF8.GetByteCount(text) > Limits.ReadBytes)
        {
            artifactId = artifacts.Save(Encoding.UTF8.GetBytes(stdout), "text/x-diff", "git_diff", target.Root.Id).Id;
            text = text[..Math.Min(text.Length, Limits.ReadBytes)];
            complete = false;
        }
        return Reply.Ok(new
        {
            root_id = target.Root.Id,
            @ref = reference,
            path = relative,
            staged,
            empty = stdout.Length == 0,
            bytes = Encoding.UTF8.GetByteCount(stdout),
            redacted,
            redacted_kinds = kinds,
            execution = Execution,
            note = "git_diff does not show untracked files; call git_status for those and fs_read for their content.",
            next_tool = complete ? "fs_read or fs_apply_patch" : "artifact_read"
        }, new { text, next_cursor = (string?)null, complete, artifact_id = artifactId });
    }

    public async Task<CallToolResult> Log(string rootId, int maxCount, string? reference, CancellationToken token)
    {
        var target = workspace.Resolve(rootId, null, Grant.Read);
        if (maxCount is < 1 or > 1000)
            throw new CodexishFault("INVALID_ARGUMENT", "max_count must be between 1 and 1000; page with an older ref for more.");
        string? checkedRef = string.IsNullOrWhiteSpace(reference) ? null : CheckRef(reference);
        var arguments = await Arguments(target.Root, token);
        arguments.AddRange(["log", "--no-show-signature", "--format=%H%x1f%an%x1f%aI%x1f%s", "-n", maxCount.ToString()]);
        arguments.Add("--end-of-options");
        if (checkedRef is not null) arguments.Add(checkedRef);
        var (exitCode, stdout, stderr) = await Run(target.Root, arguments, token);
        if (exitCode != 0) return Failed("log", exitCode, stderr);
        List<object> commits = [];
        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')))
        {
            string[] fields = line.Split('\u001f');
            if (fields.Length == 4)
                commits.Add(new { sha = fields[0], author = fields[1], date = fields[2], subject = Redaction.Apply(fields[3]).Text });
        }
        return Reply.Ok(new
        {
            root_id = target.Root.Id, @ref = reference, max_count = maxCount,
            commits, commit_count = commits.Count, execution = Execution,
            next_tool = "git_diff with one of these sha values as ref"
        });
    }

    public const string Execution =
        "The configured git binary ran with core.hooksPath pointing at an empty directory and with " +
        "core.fsmonitor, core.pager, diff.external, core.editor, log.showSignature, maintenance.auto, gc.auto, " +
        "--no-optional-locks, --no-ext-diff and --no-textconv fixed (plus --no-lazy-fetch where git supports it), and every clean or process " +
        "filter the repository declares was read by name and disabled, so this read did not execute " +
        "repository-supplied code. Git writes are not tools; run them through shell_run.";

    public bool Available => File.Exists(config.Git.Path) || !Path.IsPathRooted(config.Git.Path);
}
