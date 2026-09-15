using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Codexish.Server;

// D2. Names use underscores because a connector function name must match ^[a-zA-Z0-9_-]+$; the dotted names in
// docs/tool-contracts.md are documentation aliases for the same contracts.
[McpServerToolType]
public sealed class CodexishTools(CodexishRuntime runtime)
{
    [McpServerTool(Name = "host_capabilities", ReadOnly = true, OpenWorld = false)]
    [Description("Call this first in a session. Returns protocol and schema versions, the configured roots and their " +
        "read/write/shell grants, the execution boundary (there is no sandbox: shell and build commands run as the " +
        "logged-in Windows user), the features this build does not implement and why, the frame and page limits with " +
        "their source, and the latest session_checkpoint so you can resume. Next tool: workspace_info, then fs_list " +
        "or git_status. No error codes other than transport failures.")]
    public CallToolResult HostCapabilities() => Reply.Guard(() => Reply.Ok(runtime.Capabilities()));

    [McpServerTool(Name = "workspace_info", ReadOnly = true, OpenWorld = false)]
    [Description("Returns each root's id, absolute path, grants, whether it is a Git repository, the state directory " +
        "(always outside every root), every live and recorded process with its handle and exit code, the pause state, " +
        "and the latest checkpoint. Use it to choose a root_id before any fs_*, shell_run or git_* call. " +
        "Next tool: fs_list or git_status.")]
    public CallToolResult WorkspaceInfo() => Reply.Guard(() => Reply.Ok(runtime.WorkspaceInfo()));

    [McpServerTool(Name = "fs_list", ReadOnly = true, OpenWorld = false)]
    [Description("Lists entries under a directory in one root. depth 1 is the directory itself; raise it to walk " +
        "deeper. Reparse points (junctions and symlinks) are listed as kind=reparse_point and never traversed. " +
        "Returns entries with kind, size and modified time plus output.next_cursor when the page limit is reached; " +
        "pass that cursor back for the next page. NOT_FOUND: the directory does not exist, re-list the parent. " +
        "OUTSIDE_WORKSPACE: the path left the root, use a path relative to root_id. CURSOR_INVALID: start again " +
        "without a cursor. Next tool: fs_read or fs_search.")]
    public CallToolResult FsList(
        [Description("Root id from workspace_info.")] string root_id,
        [Description("Directory relative to the root. Empty means the root itself.")] string path = "",
        [Description("How many levels to walk, 1-32.")] int depth = 1,
        [Description("Include dotfiles and hidden entries.")] bool hidden = false,
        [Description("output.next_cursor from a previous fs_list on the same directory.")] string? cursor = null) =>
        Reply.Guard(() => runtime.Files.List(root_id, path, depth, hidden, cursor));

    [McpServerTool(Name = "fs_read", ReadOnly = true, OpenWorld = false)]
    [Description("Reads one text file and returns its text, sha256, byte count, encoding, BOM size, newline style, " +
        "line count and whether the returned text is complete. ALWAYS read before writing and pass the returned " +
        "sha256 to fs_write. Use line_from/line_to or byte_offset/max_bytes for part of a large file. Binary files " +
        "and files above the inline budget are stored as an artifact and returned as artifact_id instead of text; " +
        "read those with artifact_read. Text is redacted for a small set of credential patterns. " +
        "NOT_FOUND: list the directory. OUTSIDE_WORKSPACE / UNSUPPORTED_CAPABILITY: the path leaves the root or " +
        "crosses a junction, choose another path. FILE_LOCKED: another program holds the file, retry later. " +
        "Next tool: fs_write with mode=replace and this sha256.")]
    public CallToolResult FsRead(
        [Description("Root id from workspace_info.")] string root_id,
        [Description("File path relative to the root.")] string path,
        [Description("First line to return, 1-based.")] int? line_from = null,
        [Description("Last line to return, inclusive.")] int? line_to = null,
        [Description("Byte offset instead of a line range.")] long? byte_offset = null,
        [Description("Maximum bytes to return for a byte range.")] int? max_bytes = null) =>
        Reply.Guard(() => runtime.Files.Read(root_id, path, line_from, line_to, byte_offset, max_bytes));

    [McpServerTool(Name = "fs_search", ReadOnly = true, OpenWorld = false)]
    [Description("Searches file contents under one root in process; no external searcher is launched and no shell " +
        "grant is used. Set regex=true for a .NET regular expression, otherwise the query is literal. Optionally " +
        "restrict with a glob such as src/**/*.cs. Returns path, line, column and a redacted preview per match, the " +
        "files skipped and why, and output.next_cursor when the match page is full. INVALID_ARGUMENT: the regular " +
        "expression did not compile, simplify it. Next tool: fs_read on a matching path.")]
    public CallToolResult FsSearch(
        [Description("Root id from workspace_info.")] string root_id,
        [Description("Text or regular expression to find.")] string query,
        [Description("Directory to search under, relative to the root. Empty searches the whole root.")] string path = "",
        [Description("Treat query as a regular expression.")] bool regex = false,
        [Description("Glob filter on the relative path, for example src/**/*.cs.")] string? glob = null,
        [Description("output.next_cursor from a previous fs_search.")] string? cursor = null) =>
        Reply.Guard(() => runtime.Files.Search(root_id, path, query, regex, glob, cursor));

    [McpServerTool(Name = "fs_stat", ReadOnly = true, OpenWorld = false)]
    [Description("Returns one entry's kind, size, timestamps, read-only flag and, for text files, encoding, BOM size, " +
        "newline style and sha256 without returning the content. Use it to check whether a file changed before a big " +
        "read. NOT_FOUND: the entry does not exist. Next tool: fs_read.")]
    public CallToolResult FsStat(
        [Description("Root id from workspace_info.")] string root_id,
        [Description("Path relative to the root.")] string path,
        [Description("Compute sha256 as well.")] bool hash = true) =>
        Reply.Guard(() => runtime.Files.Stat(root_id, path, hash));

    [McpServerTool(Name = "fs_write", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Writes one file. mode=create refuses to touch an existing file and writes atomically. " +
        "mode=replace requires expected_sha256 from fs_read, takes one exclusive handle, saves the previous bytes to " +
        "the state directory, and rewrites in place preserving encoding, BOM and the file's own LF or CRLF endings " +
        "(save_mode reports that this is not crash-atomic). Reuse invocation_id only to retry the same write after a " +
        "lost response. FILE_CHANGED: someone edited the file, fs_read again and merge. FILE_LOCKED: another handle " +
        "is open, retry. UNSUPPORTED_CAPABILITY: the file mixes newline styles, rewrite it explicitly. " +
        "IDEMPOTENCY_CONFLICT: that invocation_id belongs to different arguments, use a new one. " +
        "Next tool: run the project's tests with shell_run.")]
    public Task<CallToolResult> FsWrite(
        [Description("Root id from workspace_info; the root needs the write grant.")] string root_id,
        [Description("File path relative to the root.")] string path,
        [Description("Complete new content of the file.")] string text,
        [Description("create for a new file, replace for an existing one.")] string mode,
        [Description("New id for a new write; the same id only to retry that write.")] string invocation_id,
        [Description("sha256 from fs_read. Required for replace, forbidden for create.")] string? expected_sha256 = null) =>
        runtime.Ledger.Invoke(invocation_id, "fs_write", new { root_id, path, text, mode, expected_sha256 },
            $"files:{root_id}", _ => Task.FromResult(Reply.Guard(() => runtime.Files.Write(root_id, path, text, mode, expected_sha256))), 15000);

    [McpServerTool(Name = "fs_apply_patch", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Applies a unified diff that may touch several files. List every touched file in expected[] with its " +
        "sha256 from fs_read; use an empty sha256 only for a file the patch creates with '--- /dev/null'. Preflight " +
        "checks every file first and changes nothing if any check fails. After preflight, files are applied one by " +
        "one and each result is reported: there is no rollback and no whole-patch transaction, so read the per-file " +
        "results. PATCH_FAILED: context did not match or preflight refused, fs_read the listed files and rebuild the " +
        "patch. FILE_CHANGED: a file changed between read and apply. EXECUTION_FAILED with side_effects=partial: some " +
        "files were changed, inspect each one. Next tool: fs_read the changed files, then shell_run the tests.")]
    public Task<CallToolResult> FsApplyPatch(
        [Description("Root id from workspace_info; the root needs the write grant.")] string root_id,
        [Description("Unified diff text with ---/+++ headers and @@ hunks.")] string patch,
        [Description("One entry per file the patch touches.")] PatchExpectation[] expected,
        [Description("New id for a new patch; the same id only to retry it.")] string invocation_id) =>
        runtime.Ledger.Invoke(invocation_id, "fs_apply_patch", new { root_id, patch, expected = expected.Select(e => new { e.Path, e.ExpectedSha256 }) },
            $"files:{root_id}", _ => Task.FromResult(Reply.Guard(() => runtime.Patches.Apply(root_id, patch, expected))), 30000);

    [McpServerTool(Name = "shell_run", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Runs a build, test or other command in a root that has the shell grant. Prefer executable plus args; " +
        "a command string requires an explicit shell from the allowed list. The child runs with the logged-in user's " +
        "full rights - this is not a sandbox. If it finishes within wait_ms you get the real exit code with stdout and " +
        "stderr previews; otherwise you get process_id and status running and the child keeps running, because wait_ms " +
        "is only a response wait and never kills anything. Full output is always available as artifacts. " +
        "PERMISSION_DENIED: the root has no shell grant or the shell is not allowed. NOT_FOUND: the executable does " +
        "not exist. Next tool: process_poll with the returned process_id, or artifact_read for full output.")]
    public Task<CallToolResult> ShellRun(
        [Description("Root id from workspace_info; the root needs the shell grant.")] string root_id,
        [Description("New id for a new command; the same id only to retry it.")] string invocation_id,
        [Description("Working directory relative to the root. Empty means the root itself.")] string cwd = "",
        [Description("Command line for the chosen shell. Requires shell.")] string? command = null,
        [Description("pwsh or cmd. Required when command is used.")] string? shell = null,
        [Description("Executable to run directly. Preferred over command.")] string? executable = null,
        [Description("Arguments for executable, already split.")] string[]? args = null,
        [Description("How long to wait for the response, in milliseconds. Not a deadline.")] int wait_ms = 10000,
        [Description("session ends the tree when this server exits; persistent survives it.")] string lifetime = "session") =>
        Start("shell_run", root_id, invocation_id, cwd, command, shell, executable, args, wait_ms, lifetime);

    [McpServerTool(Name = "process_start", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Starts a long-running process (dev server, watcher, REPL) and returns its handle immediately. Same " +
        "supervisor and same arguments as shell_run; use this when you expect the process to outlive the response. " +
        "lifetime=session ends the whole tree when the server exits, lifetime=persistent leaves it running. " +
        "Next tool: process_poll for incremental output, process_write for stdin, process_stop to end it.")]
    public Task<CallToolResult> ProcessStart(
        [Description("Root id from workspace_info; the root needs the shell grant.")] string root_id,
        [Description("New id for a new process; the same id only to retry the start.")] string invocation_id,
        [Description("Working directory relative to the root.")] string cwd = "",
        [Description("Command line for the chosen shell. Requires shell.")] string? command = null,
        [Description("pwsh or cmd. Required when command is used.")] string? shell = null,
        [Description("Executable to run directly. Preferred over command.")] string? executable = null,
        [Description("Arguments for executable, already split.")] string[]? args = null,
        [Description("Response wait in milliseconds before the handle is returned.")] int wait_ms = 1000,
        [Description("session or persistent.")] string lifetime = "session") =>
        Start("process_start", root_id, invocation_id, cwd, command, shell, executable, args, wait_ms, lifetime);

    private Task<CallToolResult> Start(string tool, string rootId, string invocationId, string cwd, string? command,
        string? shell, string? executable, string[]? args, int waitMs, string lifetime) =>
        runtime.Ledger.Invoke(invocationId, tool,
            new { root_id = rootId, cwd, command, shell, executable, args, lifetime }, $"shell:{rootId}",
            job => Reply.GuardAsync(async () =>
            {
                var target = runtime.Workspace.Resolve(rootId, cwd, Grant.Shell);
                runtime.Workspace.VerifyDirectory(target);
                var managed = runtime.Processes.Start(target, command, shell, executable, args, lifetime);
                // The resource chain is released here: a later command in this root must not wait for a dev server.
                job.Started();
                var started = DateTimeOffset.UtcNow;
                if (await Task.WhenAny(managed.Collection!, Task.Delay(Math.Max(0, waitMs))) == managed.Collection)
                    return Reply.Ok(Finished(managed, started));
                return Reply.Status("running", Handle(managed, "The process is still running; wait_ms elapsed and nothing was killed."));
            }),
            waitMs + 5000, releaseChainOnStart: true);

    private object Handle(ManagedProcess managed, string note) => new
    {
        operation_id = managed.ProcessId,
        process_id = managed.ProcessId,
        pid = managed.Pid,
        lifetime = managed.Lifetime,
        supervision = managed.Supervision,
        root_id = managed.RootId,
        command = managed.Display,
        stdout_artifact = managed.StdoutArtifact,
        stderr_artifact = managed.StderrArtifact,
        note,
        next_tool = "process_poll"
    };

    private object Finished(ManagedProcess managed, DateTimeOffset started)
    {
        var stdout = Redaction.Apply(Preview(managed.StdoutArtifact));
        var stderr = Redaction.Apply(Preview(managed.StderrArtifact));
        return new
        {
            process_id = managed.ProcessId,
            pid = managed.Pid,
            state = managed.State,
            exit_code = managed.ExitCode,
            succeeded = managed.ExitCode == 0,
            duration_ms = (DateTimeOffset.UtcNow - started).TotalMilliseconds,
            command = managed.Display,
            stdout_preview = stdout.Text,
            stderr_preview = stderr.Text,
            stdout_bytes = runtime.Artifacts.Length(managed.StdoutArtifact),
            stderr_bytes = runtime.Artifacts.Length(managed.StderrArtifact),
            stdout_artifact = managed.StdoutArtifact,
            stderr_artifact = managed.StderrArtifact,
            preview_truncated = runtime.Artifacts.Length(managed.StdoutArtifact) > Limits.PreviewBytes ||
                                runtime.Artifacts.Length(managed.StderrArtifact) > Limits.PreviewBytes,
            redacted = stdout.Redacted || stderr.Redacted,
            limits_source = Limits.Source,
            next_tool = "artifact_read for the full output; report this exit code, not an inferred success"
        };
    }

    private string Preview(string artifactId) =>
        System.Text.Encoding.UTF8.GetString(runtime.Artifacts.ReadBytes(artifactId, 0, Limits.PreviewBytes));

    [McpServerTool(Name = "process_poll", ReadOnly = true, OpenWorld = false)]
    [Description("Reads new output from a running or exited process. Pass the cursor from the previous poll to get " +
        "only the bytes since then; byte order is preserved within each stream and stdout and stderr stay separate. " +
        "Returns running state, the real exit code once it exits, and output.next_cursor. wait_ms waits for new " +
        "output without killing anything. Exited processes stay pollable. CURSOR_INVALID: poll again without a " +
        "cursor. NOT_FOUND: the handle is from an earlier server run, see workspace_info. " +
        "Next tool: process_write, process_stop, or artifact_read for the whole log.")]
    public Task<CallToolResult> ProcessPoll(
        [Description("process_id from shell_run or process_start.")] string process_id,
        [Description("output.next_cursor from the previous poll.")] string? cursor = null,
        [Description("How long to wait for new output, in milliseconds.")] int wait_ms = 1000,
        CancellationToken cancellationToken = default) =>
        Reply.GuardAsync(() => runtime.Processes.Poll(process_id, cursor, wait_ms, cancellationToken));

    [McpServerTool(Name = "process_write", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Writes text to a running process's stdin as UTF-8. Include the newline the program expects. The " +
        "result confirms how many bytes were delivered, not how the program reacted: poll for that. Reuse " +
        "invocation_id only to retry the same input after a lost response - never resend blindly, the first write " +
        "may already have arrived. PROCESS_EXITED: the process is gone, read its output with artifact_read. " +
        "Next tool: process_poll.")]
    public Task<CallToolResult> ProcessWrite(
        [Description("process_id from shell_run or process_start.")] string process_id,
        [Description("Exact text to write, including any trailing newline.")] string text,
        [Description("New id for new input; the same id only to retry it.")] string invocation_id) =>
        runtime.Ledger.Invoke(invocation_id, "process_write", new { process_id, text }, $"process:{process_id}",
            _ => Task.FromResult(Reply.Guard(() => runtime.Processes.WriteStdin(process_id, text))), 5000);

    [McpServerTool(Name = "process_stop", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Ends a process. mode=graceful closes stdin and reports the state without forcing anything, so a " +
        "program that ignores EOF keeps running. mode=kill_tree terminates the process and its descendants through " +
        "the Windows Job Object the child was assigned at start. Effects already applied are not undone. " +
        "NOT_FOUND: unknown handle. Next tool: process_poll to confirm the exit code.")]
    public Task<CallToolResult> ProcessStop(
        [Description("process_id from shell_run or process_start.")] string process_id,
        [Description("graceful or kill_tree.")] string mode,
        [Description("New id for a new stop; the same id only to retry it.")] string invocation_id) =>
        runtime.Ledger.Invoke(invocation_id, "process_stop", new { process_id, mode }, $"process:{process_id}",
            _ => Reply.GuardAsync(() => runtime.Processes.Stop(process_id, mode)), 10000);

    [McpServerTool(Name = "git_status", ReadOnly = true, OpenWorld = false)]
    [Description("Runs git status in a root and returns the branch, HEAD, upstream, ahead/behind counts and one entry " +
        "per change with its staged and worktree state. Untracked files are always included, which git_diff does not " +
        "show. The git binary runs with hooks, fsmonitor, pager, external diff and editor disabled, so reading a " +
        "repository does not execute its code. EXECUTION_FAILED: git's own exit code and stderr are in the error " +
        "details. Next tool: git_diff for content, fs_read for an untracked file.")]
    public Task<CallToolResult> GitStatus(
        [Description("Root id from workspace_info.")] string root_id, CancellationToken cancellationToken = default) =>
        Reply.GuardAsync(() => runtime.Git.Status(root_id, cancellationToken));

    [McpServerTool(Name = "git_diff", ReadOnly = true, OpenWorld = false)]
    [Description("Returns a unified diff for a root. staged=true diffs the index; ref compares against a commit or " +
        "branch; path limits the diff to one file or directory inside the root. External diff and textconv are " +
        "disabled. A diff larger than the frame budget is stored as an artifact and output.artifact_id is returned. " +
        "INVALID_ARGUMENT: the ref or path was rejected before git ran, use a plain ref name and a path inside the " +
        "root. Next tool: fs_read, fs_apply_patch, or artifact_read for a large diff.")]
    public Task<CallToolResult> GitDiff(
        [Description("Root id from workspace_info.")] string root_id,
        [Description("Commit, branch or tag to compare against.")] string? @ref = null,
        [Description("File or directory inside the root to limit the diff to.")] string? path = null,
        [Description("Diff the staged index instead of the working tree.")] bool staged = false,
        CancellationToken cancellationToken = default) =>
        Reply.GuardAsync(() => runtime.Git.Diff(root_id, @ref, path, staged, cancellationToken));

    [McpServerTool(Name = "git_log", ReadOnly = true, OpenWorld = false)]
    [Description("Returns recent commits as sha, author, ISO date and subject. Use ref to start from a branch or " +
        "commit and max_count to bound the page. INVALID_ARGUMENT: the ref was rejected before git ran. " +
        "Next tool: git_diff with one of the returned sha values.")]
    public Task<CallToolResult> GitLog(
        [Description("Root id from workspace_info.")] string root_id,
        [Description("How many commits to return, 1-1000.")] int max_count = 20,
        [Description("Commit, branch or tag to start from.")] string? @ref = null,
        CancellationToken cancellationToken = default) =>
        Reply.GuardAsync(() => runtime.Git.Log(root_id, max_count, @ref, cancellationToken));

    [McpServerTool(Name = "artifact_read", ReadOnly = true, OpenWorld = false)]
    [Description("Reads a stored result: full command output, a large diff, or a binary or oversized file returned by " +
        "fs_read. Page with output.next_cursor, or use line_from/line_to. The response says whether collection is " +
        "complete, in_progress or incomplete. Text is redacted on read; the local file keeps the raw bytes. " +
        "CURSOR_INVALID: the artifact was replaced, read again from the start. OUTPUT_INCOMPLETE: collection ended " +
        "before the producer finished, the preserved prefix is still readable from offset 0. " +
        "Next tool: artifact_search to find a line in a long log.")]
    public CallToolResult ArtifactRead(
        [Description("artifact_id from a previous result.")] string artifact_id,
        [Description("Byte offset to read from.")] long? offset = null,
        [Description("Maximum bytes to return.")] int? max_bytes = null,
        [Description("First line to return, 1-based.")] int? line_from = null,
        [Description("Last line to return, inclusive.")] int? line_to = null,
        [Description("output.next_cursor from the previous read.")] string? cursor = null) =>
        Reply.Guard(() => runtime.Artifacts.Read(artifact_id, offset, max_bytes, line_from, line_to, cursor));

    [McpServerTool(Name = "artifact_search", ReadOnly = true, OpenWorld = false)]
    [Description("Finds text in a stored artifact without downloading all of it. Returns line number, byte offset, " +
        "column and a redacted preview per match, plus output.next_cursor when the match page is full. Use it to " +
        "locate the failing assertion in a long build log before reading around it with artifact_read. " +
        "CURSOR_INVALID: search again without a cursor. Next tool: artifact_read with the returned line number.")]
    public CallToolResult ArtifactSearch(
        [Description("artifact_id from a previous result.")] string artifact_id,
        [Description("Text or regular expression to find.")] string query,
        [Description("Treat query as a regular expression.")] bool regex = false,
        [Description("output.next_cursor from a previous search.")] string? cursor = null) =>
        Reply.Guard(() => runtime.Artifacts.Search(artifact_id, query, regex, cursor));

    [McpServerTool(Name = "operation_inspect", ReadOnly = true, OpenWorld = false)]
    [Description("Looks up an accepted operation by its invocation_id or operation_id and returns its stored result, " +
        "or queued/running if it has not finished. Use this after a lost response instead of repeating the call. " +
        "status=unknown with reason persist_failed means the effect happened but the record did not survive: inspect " +
        "the target, never replay. NOT_FOUND: that id was never accepted, so nothing ran. " +
        "Next tool: whatever the reported state calls for; never a blind retry.")]
    public CallToolResult OperationInspect(
        [Description("The invocation_id or operation_id to look up.")] string operation_id) =>
        Reply.Guard(() => runtime.Ledger.Inspect(operation_id));

    [McpServerTool(Name = "operation_cancel", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Requests cancellation of one accepted operation. An operation still queued is cancelled with no " +
        "effects. A running operation is asked to stop: effects that already happened are not undone and the result " +
        "reports side_effects=unknown. This is not a rollback and it does not stop a child process - use " +
        "process_stop for that. NOT_FOUND: unknown id. Next tool: operation_inspect for the real terminal state.")]
    public CallToolResult OperationCancel(
        [Description("The invocation_id or operation_id to cancel.")] string operation_id) =>
        Reply.Guard(() => runtime.Ledger.Cancel(operation_id));

    [McpServerTool(Name = "session_checkpoint", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Stores the observable state of the work so a later turn can resume it: the goal, the condition that " +
        "proves it is done, what is finished, what is left, and the handles (paths, process ids, operation ids) the " +
        "next turn needs. Call it after each milestone. host_capabilities returns the latest checkpoint. It stores " +
        "text only and changes no permission. INVALID_ARGUMENT: goal and completion_condition must be observable. " +
        "Next tool: continue the remaining work.")]
    public CallToolResult SessionCheckpoint(
        [Description("What the user asked for, in one sentence.")] string goal,
        [Description("The observable condition that proves the goal is met, such as a passing test command.")] string completion_condition,
        [Description("Steps already finished and verified.")] string[] done,
        [Description("Steps still to do.")] string[] remaining,
        [Description("Handles the next turn needs: paths, process ids, operation ids, artifact ids.")] string[] handles) =>
        Reply.Guard(() => runtime.Checkpoint(goal, completion_condition, done, remaining, handles));
}
