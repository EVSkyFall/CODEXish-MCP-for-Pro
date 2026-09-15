using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Codexish.P0.V1;

[McpServerToolType]
public sealed class Tools(Runtime runtime, IHttpContextAccessor context)
{
    public static readonly string[] Names = ["host.capabilities", "workspace.info", "fs.list", "fs.read", "fs.search", "fs.stat", "fs.write", "fs.apply_patch", "shell.run", "process.start", "process.poll", "process.write", "process.stop", "git.status", "git.diff", "git.log", "artifact.read", "artifact.search", "operation.inspect", "operation.cancel", "session.checkpoint"];
    private string Session => context.HttpContext?.Items["codexish.session"] as string ?? throw new ProbeFault("PERMISSION_DENIED", "Authenticated session missing.");
    private Task<CallToolResult> Change(string id, string name, object args, string resource, Func<string, CancellationToken, Task<CallToolResult>> effect, int wait = 1000, bool recovery = false)
    {
        // Capture the principal before HTTP completion; queued work must not access HttpContext later.
        string session = Session;
        return runtime.Operations.Invoke(session, id, name, args, Resources(resource), ct => effect(session, ct), wait, recovery);
    }
    private string[] Resources(string resource)
    {
        if (!resource.StartsWith("root:", StringComparison.Ordinal)) return [resource];
        var root = runtime.Config.Roots.SingleOrDefault(r => r.Id == resource[5..]);
        if (root is null) return [resource]; // The adapter reports unknown-root before effects.
        return runtime.Config.Roots.Where(r => FileFence.Within(r.Path, root.Path) || FileFence.Within(root.Path, r.Path))
            .Select(r => "path:" + (OperatingSystem.IsWindows() ? r.Path.ToUpperInvariant() : r.Path)).Distinct().ToArray();
    }
    [McpServerTool(Name = "host.capabilities", ReadOnly = true)]
    [Description("Start here. Lists real tools, root grants, session ID, execution boundary, unsupported functions and last checkpoint. Use workspace.info and fs.read next.")]
    public CallToolResult Capabilities() => runtime.Capabilities(Session);
    [McpServerTool(Name = "workspace.info", ReadOnly = true)]
    [Description("Inspect configured roots and grants before choosing root_id/cwd. cwd is not a sandbox. Use git.status for repository state and fs.list/read for contents.")]
    public CallToolResult Workspace() => VReply.Ok(new { roots = runtime.Config.Roots, session_id = Session, execution_boundary = "unconfined_user" });
    [McpServerTool(Name = "fs.list", ReadOnly = true)]
    [Description("List a root-relative directory without following reparse points. Full results are retained in the returned artifact; page it with artifact.read if the preview is incomplete.")]
    public CallToolResult List(string root_id, string path = "", int depth = 1) => VReply.Guard(() => runtime.Files.List(Session, root_id, path, depth));
    [McpServerTool(Name = "fs.read", ReadOnly = true)]
    [Description("Read UTF-8/UTF-16 text with original byte sha256, BOM and line pagination. Pass next_line to continue. Preserve that hash for fs.write/apply_patch. Redacted text is not the exact original; do not overwrite secrets with placeholders.")]
    public CallToolResult Read(string root_id, string path, int start_line = 1, int line_count = 200) => VReply.Guard(() => runtime.Files.Read(Session, root_id, path, start_line, line_count));
    [McpServerTool(Name = "fs.search", ReadOnly = true)]
    [Description("Search root files using literal text or nonbacktracking regex and a glob. Reports skipped files/errors. Full matches are in the artifact; read that artifact rather than assuming the preview is complete.")]
    public CallToolResult Search(string root_id, string query, bool regex = false, string glob = "*") => VReply.Guard(() => runtime.Files.Search(Session, root_id, query, regex, glob));
    [McpServerTool(Name = "fs.stat", ReadOnly = true)]
    [Description("Read kind/size/mtime and optional original byte hash for a root-relative target. Use fs.read before editing; FILE_CHANGED requires rereading, not force replacement.")]
    public CallToolResult Stat(string root_id, string path, bool hash = false) => VReply.Guard(() => runtime.Files.Stat(root_id, path, hash));
    [McpServerTool(Name = "fs.write", ReadOnly = false, Destructive = true)]
    [Description("Create a nonexistent UTF-8 file or replace an existing file under an exclusive handle with expected_sha256. Replacement preserves encoding/BOM/newlines and saves .bak in local state. In-place, not crash-atomic. Reuse invocation_id only for identical retries. Run relevant tests afterward.")]
    public Task<CallToolResult> Write(string root_id, string path, string text, string invocation_id, string? expected_sha256 = null, string mode = "replace") =>
        Change(invocation_id, "fs.write", new { root_id, path, text, expected_sha256, mode }, "root:" + root_id,
            (s, _) => Task.FromResult(runtime.Files.Write(s, root_id, path, text, expected_sha256, mode)));
    [McpServerTool(Name = "fs.apply_patch", ReadOnly = false, Destructive = true)]
    [Description("Apply unified diff hunks to existing files with expected_sha256 keyed by each relative path. Each file reports success/failure; the group is not atomic and is never auto-rolled back. Create files with fs.write. Inspect partial results before a new patch, then run tests.")]
    public Task<CallToolResult> Patch(string root_id, string patch, Dictionary<string, string> expected_sha256, string invocation_id) =>
        Change(invocation_id, "fs.apply_patch", new { root_id, patch, expected_sha256 = expected_sha256.OrderBy(x => x.Key).ToArray() }, "root:" + root_id,
            (s, _) => Task.FromResult(runtime.Files.Patch(s, root_id, patch, expected_sha256)));
    [McpServerTool(Name = "shell.run", ReadOnly = false, Destructive = true, OpenWorld = true)]
    [Description("Run an explicit shell command (pwsh/cmd/sh) or executable+args, not both, under the root shell grant. wait_ms is only response wait: running returns operation_id; inspect until final exit code. For servers use process.start to obtain process/output handles immediately. Output pages retain full artifacts. Execution is unconfined_user.")]
    public Task<CallToolResult> Shell(string root_id, string invocation_id, string cwd = "", string? executable = null, string[]? args = null, string? shell = null, string? command = null, int wait_ms = 1000)
    {
        if ((executable is null) == (shell is null) || (shell is not null && (command is null || args is not null)) || (executable is not null && command is not null))
            return Task.FromResult(VReply.Error("INVALID_ARGUMENT", "Choose executable+args OR shell+command."));
        try
        {
            var target = shell is not null ? Processes.Shell(shell, command!) : (executable!, args ?? []);
            return Change(invocation_id, "shell.run", new { root_id, cwd, executable, args, shell, command }, "root:" + root_id,
                (s, ct) => runtime.Processes.Run(s, root_id, cwd, target.Item1, target.Item2, ct), wait_ms);
        }
        catch (Exception e) { return Task.FromResult(VReply.From(e)); }
    }
    [McpServerTool(Name = "process.start", ReadOnly = false, Destructive = true, OpenWorld = true)]
    [Description("Start a real executable with structured args, root-relative cwd and session/persistent lifetime. Returns a stable process handle and stdout/stderr artifact IDs. Poll process.poll; successful start does not mean tests passed. Windows Job assignment precedes target launch. Pipes only; no PTY yet.")]
    public Task<CallToolResult> Start(string root_id, string executable, string[] args, string invocation_id, string cwd = "", string lifetime = "session") =>
        Change(invocation_id, "process.start", new { root_id, executable, args, cwd, lifetime }, "root:" + root_id,
            async (s, _) => VReply.Ok((await runtime.Processes.Start(s, root_id, cwd, executable, args, lifetime)).Snapshot()));
    [McpServerTool(Name = "process.poll", ReadOnly = true)]
    [Description("Inspect managed process state, exit_code and output_complete. Read its stdout/stderr artifacts with artifact.read; exit and output draining are separate. Does not kill or restart the process.")]
    public CallToolResult Poll(string process_id) => VReply.Guard(() => runtime.Processes.Poll(Session, process_id));
    [McpServerTool(Name = "process.write", ReadOnly = false, Destructive = true)]
    [Description("Write literal UTF-8 to process stdin, optionally closing it after delivery. Reuse invocation_id on response loss; unknown/partial input must be inspected, never blindly replayed. Poll process/output afterward.")]
    public Task<CallToolResult> ProcessWrite(string process_id, string text, string invocation_id, bool close_stdin = false) =>
        Change(invocation_id, "process.write", new { process_id, text, close_stdin }, "stdin:" + process_id,
            (s, _) => runtime.Processes.Write(s, process_id, text, close_stdin));
    [McpServerTool(Name = "process.stop", ReadOnly = false, Destructive = true)]
    [Description("Stop only a managed process tree in this session. Available while paused. Returns actual process/output state; termination is not rollback of file/network effects. Poll afterward if still running.")]
    public Task<CallToolResult> Stop(string process_id, string invocation_id) =>
        Change(invocation_id, "process.stop", new { process_id }, "stop:" + process_id,
            (s, _) => runtime.Processes.Stop(s, process_id), recovery: true);
    [McpServerTool(Name = "git.status", ReadOnly = true)]
    [Description("Fixed trusted Git query with hooks/fsmonitor/pager disabled and optional index writes disabled. Includes staged/unstaged/untracked paths. Use fs.read for untracked contents and git.diff for tracked differences. Does not require arbitrary shell grant.")]
    public async Task<CallToolResult> GitStatus(string root_id, string cwd = "") { try { return await runtime.Git(Session, root_id, cwd, "status"); } catch (Exception e) { return VReply.From(e); } }
    [McpServerTool(Name = "git.diff", ReadOnly = true)]
    [Description("Read tracked Git differences, optionally staged, with external diff/textconv/pager disabled. Untracked content is not in Git diff: consult git.status and fs.read. Larger output is in returned artifacts.")]
    public async Task<CallToolResult> GitDiff(string root_id, string cwd = "", bool staged = false) { try { return await runtime.Git(Session, root_id, cwd, "diff", staged); } catch (Exception e) { return VReply.From(e); } }
    [McpServerTool(Name = "git.log", ReadOnly = true)]
    [Description("Read a fixed-format recent commit log from the configured absolute Git binary, without pager/signature/textconv commands. count limits this query, not task duration. Inspect returned exit_code and output artifact.")]
    public async Task<CallToolResult> GitLog(string root_id, string cwd = "", int count = 20) { try { return await runtime.Git(Session, root_id, cwd, "log", count: count); } catch (Exception e) { return VReply.From(e); } }
    [McpServerTool(Name = "artifact.read", ReadOnly = true)]
    [Description("Page a session-owned artifact through an immutable redacted UTF-8 snapshot. Reusing replay_cursor returns the identical page; next_cursor continues. utf8_base64 preserves split codepoints. At an incomplete prefix end call without cursor for a new snapshot; that is not EOF. Raw original bytes stay local.")]
    public CallToolResult ArtifactRead(string artifact_id, string? cursor = null, int page_bytes = 65536) => VReply.Guard(() => runtime.Artifacts.Read(Session, artifact_id, cursor, page_bytes));
    [McpServerTool(Name = "artifact.search", ReadOnly = true)]
    [Description("Search the currently available redacted artifact text. Paginate matches with next_offset; ongoing artifacts can grow. Use artifact.read for stable replayable content pages.")]
    public CallToolResult ArtifactSearch(string artifact_id, string query, int offset = 0) => VReply.Guard(() => runtime.Artifacts.Search(Session, artifact_id, query, offset));
    [McpServerTool(Name = "operation.inspect", ReadOnly = true)]
    [Description("Resolve a pending or uncertain invocation by its operation_id. Returns persisted result or memory unknown(persist_failed). Never repair uncertainty by reissuing the effect under a new ID.")]
    public CallToolResult Inspect(string operation_id) => runtime.Operations.Inspect(Session, operation_id);
    [McpServerTool(Name = "operation.cancel", ReadOnly = false, Destructive = true)]
    [Description("Request cancellation of this session's operation. Available while paused. Started effects are not rolled back. Inspect the original operation until terminal; use process.stop for a process whose start operation already completed.")]
    public Task<CallToolResult> Cancel(string operation_id, string invocation_id) =>
        Change(invocation_id, "operation.cancel", new { operation_id }, "cancel:" + operation_id,
            (s, _) => Task.FromResult(VReply.Ok(new { cancellation_requested = true, operation_id, target = runtime.Operations.Cancel(s, operation_id).StructuredContent })), recovery: true);
    [McpServerTool(Name = "session.checkpoint", ReadOnly = false, Destructive = false)]
    [Description("Persist goal, completion condition, remaining steps and handles for resume; no permission changes. host.capabilities returns the last checkpoint. Use observed facts only, not hidden reasoning or a claim of completed work.")]
    public Task<CallToolResult> Checkpoint(string goal, string completion_condition, string[] remaining_steps, string[] handles, string invocation_id) =>
        Change(invocation_id, "session.checkpoint", new { goal, completion_condition, remaining_steps, handles }, "checkpoint:" + Session,
            (s, _) => Task.FromResult(runtime.Checkpoint(s, goal, completion_condition, remaining_steps, handles)));
}
