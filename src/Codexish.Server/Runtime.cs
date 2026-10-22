using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

public sealed class CodexishRuntime : IDisposable
{
    public const string ProtocolVersion = "2025-11-25";
    public const string ServerVersion = "1.0.0-slice4";
    private readonly Lazy<DesktopService> desktop = new(() => new DesktopService());
    public DesktopService Desktop => desktop.Value;

    public ServerConfig Config { get; }
    public Store Store { get; }
    public Workspace Workspace { get; }
    public Artifacts Artifacts { get; }
    public Ledger Ledger { get; }
    public FileService Files { get; }
    public PatchService Patches { get; }
    public ProcessSupervisor Processes { get; }
    public GitService Git { get; }
    public Tokens Tokens { get; }
    public BrowserMounts Browsers { get; }
    public bool AuthDisabled { get; }
    public string HooksDirectory { get; }
    public IReadOnlyList<object> RecoveredProcesses { get; }
    public (int Cancelled, int Unknown) RecoveredInvocations { get; }

    private readonly FileStream instanceLock;
    private bool disposed;

    public CodexishRuntime(ServerConfig config, bool authDisabled = false)
    {
        Config = config;
        AuthDisabled = authDisabled;
        // The environment is snapshotted once here so its secret-looking values can be stripped from output.
        Redaction.RefreshEnvironment();
        Directory.CreateDirectory(config.StateDir);
        HooksDirectory = Path.Combine(config.StateDir, "git-hooks-empty");
        Directory.CreateDirectory(HooksDirectory);
        Directory.CreateDirectory(Path.Combine(config.StateDir, "artifacts"));
        Directory.CreateDirectory(Path.Combine(config.StateDir, "backups"));
        instanceLock = new FileStream(Path.Combine(config.StateDir, "instance.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Store = new Store(Path.Combine(config.StateDir, "codexish.db"));
        Workspace = new Workspace(config);
        Artifacts = new Artifacts(Store, Path.Combine(config.StateDir, "artifacts"));
        Ledger = new Ledger(Store);
        Files = new FileService(Workspace, Artifacts, Store, config.StateDir);
        Patches = new PatchService(Workspace, Files, Store);
        Processes = new ProcessSupervisor(Store, Artifacts, config);
        Git = new GitService(config, Workspace, Artifacts, Store, HooksDirectory);
        Tokens = new Tokens(Store, config);
        Browsers = new BrowserMounts(this);
        RecoveredInvocations = Store.RecoverInvocations();
        RecoveredProcesses = Processes.Recover();
        Store.Event("server_start", null, new { version = ServerVersion, auth = authDisabled ? "disabled" : "oauth" });
    }

    // D14. The harness text a connector shows the model before it plans anything.
    public const string Instructions =
        "You are operating the user's Windows PC through these tools. Work until the stated completion condition " +
        "is met and verified; do not stop to ask unless permission is missing or the user cancels. Read before " +
        "editing and pass the returned sha256 to writes. After every edit run the relevant tests or build and fix " +
        "observed failures. Long commands return handles: poll them; a response wait is not a deadline and never " +
        "kills the process. Reuse invocation_id only when retrying the same action; inspect unknown operations, " +
        "never replay them. Report actual exit codes, diffs, and file contents, not inferred success. Call " +
        "session_checkpoint after each milestone so work can resume across turns. For GUI: observe before acting; " +
        "prefer UIA elements, explicitly choose image coordinates or physical pixels, and inspect the observation " +
        "returned after every action. Focus the selected window when needed. Crop for detail, never replay " +
        "unverified input, and verify saved files separately from input delivery.";

    private static readonly (string Feature, string Reason)[] Unsupported =
    [

        ("browser automation of its own", "Browser tools come only from configured browser_mounts backends (see browser); this server adds no browser driver and no network sandbox."),
        ("lsp_*", "Not implemented; diagnostics come from the project's own build and test commands through shell_run."),
        ("git write tools (commit, checkout, push)", "Deliberate: Git writes run through shell_run under the root's shell grant, so one execution policy covers them."),
        ("approval tools", "There is no per-action approval UI. Grants live in codexish.json and are decided by the local user."),
        ("clipboard, env passthrough", "Environment variables are never returned, and the clipboard is not a capability of this slice."),
        ("fs_mkdir / fs_move / fs_delete", "Not in this slice; fs_apply_patch creates and deletes files, and shell_run covers the rest."),
        ("OS-isolated execution", "There is no sandbox. shell_run, builds, tests and git hooks run with the logged-in user's full rights.")
    ];

    public object Capabilities() => new
    {
        server = new { name = "CODEXish", version = ServerVersion },
        protocol_version = ProtocolVersion,
        schema_version = Reply.SchemaVersion,
        execution_boundary = Reply.Boundary,
        boundaries = new
        {
            structured_file_scope = "fs_* tools are fenced to the configured roots and verify every opened handle.",
            os_isolated_execution = "none verified; no sandbox, job objects supervise lifetime only, not privilege",
            user_privilege_scope = "shell_run, process_start, builds, tests and any git hook run as the logged-in Windows user."
        },
        roots = Config.Roots.Select(r => new { id = r.Id, path = r.Path, grants = new { r.Read, r.Write, r.Shell } }),
        tools = ToolNames.Concat(Browsers.Tools.Select(t => t.ProtocolTool.Name)).ToArray(),
        browser = Browsers.Describe(),
        desktop = new { native_available = OperatingSystem.IsWindows(), coordinate_space = "virtual desktop physical pixels", uia_thread = "dedicated MTA", input_tick = "metadata only" },
        unsupported = Unsupported.Select(u => new { feature = u.Feature, reason = u.Reason }),
        limits = Limits.Describe(),
        shell = new { @default = Config.Shell.Default, allowed = Config.Shell.Allowed },
        git = new { path = Config.Git.Path, available = Git.Available, read_only = true, execution = GitService.Execution },
        redaction = Redaction.Disclosure,
        authentication = AuthDisabled ? "disabled (loopback development only)" : "built-in OAuth 2.1 with PKCE, opaque bearer tokens",
        paused = Ledger.Paused,
        checkpoint = LatestCheckpoint(),
        recovery = new
        {
            invocations_cancelled_on_restart = RecoveredInvocations.Cancelled,
            invocations_unknown_on_restart = RecoveredInvocations.Unknown,
            processes = RecoveredProcesses
        },
        next_tool = "workspace_info"
    };

    public object WorkspaceInfo() => new
    {
        roots = Config.Roots.Select(r => new
        {
            id = r.Id,
            path = r.Path,
            grants = new { r.Read, r.Write, r.Shell },
            exists = Directory.Exists(r.Path),
            is_git_repository = Directory.Exists(Path.Combine(r.Path, ".git")) || File.Exists(Path.Combine(r.Path, ".git"))
        }),
        state_dir = Config.StateDir,
        state_dir_note = "Outside every root by construction: the ledger, backups and artifacts are not reachable through fs_* tools.",
        execution_boundary = Reply.Boundary,
        processes = Processes.Live.Select(Processes.Describe),
        recorded_processes = Store.Processes().Select(p => new
        {
            process_id = p.ProcessId, pid = p.Pid, state = p.State, exit_code = p.ExitCode,
            lifetime = p.Lifetime, root_id = p.RootId, stdout_artifact = p.StdoutArtifact, stderr_artifact = p.StderrArtifact
        }),
        paused = Ledger.Paused,
        limits = Limits.Describe(),
        checkpoint = LatestCheckpoint(),
        next_tool = "fs_list or git_status"
    };

    public object? LatestCheckpoint()
    {
        var row = Store.LatestCheckpoint();
        if (row is null) return null;
        return new { seq = row.Value.Seq, utc = row.Value.Utc, checkpoint = JsonDocument.Parse(row.Value.Json).RootElement.Clone() };
    }

    public CallToolResult Checkpoint(string goal, string completionCondition, string[] done, string[] remaining, string[] handles)
    {
        if (string.IsNullOrWhiteSpace(goal) || string.IsNullOrWhiteSpace(completionCondition))
            throw new CodexishFault("INVALID_ARGUMENT", "goal and completion_condition are required and must be observable.");
        var checkpoint = new { goal, completion_condition = completionCondition, done, remaining, handles };
        Store.AddCheckpoint(JsonSerializer.Serialize(checkpoint));
        Store.Event("checkpoint", null, checkpoint);
        return Reply.Ok(new
        {
            stored = true,
            checkpoint,
            note = "host_capabilities returns the latest checkpoint at the start of the next turn.",
            next_tool = "continue the remaining work"
        });
    }

    public static readonly string[] ToolNames =
    [
        "host_capabilities", "workspace_info", "fs_list", "fs_read", "fs_search", "fs_stat", "fs_write",
        "fs_apply_patch", "shell_run", "process_start", "process_poll", "process_write", "process_stop",
        "git_status", "git_diff", "git_log", "artifact_read", "artifact_search", "operation_inspect",
        "operation_cancel", "session_checkpoint", "computer_observe", "computer_query_ui", "computer_act"
    ];

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Browsers.Stop();
        Ledger.Dispose();
        Processes.Dispose();
        // The thread pool keeps a caller's UI synchronization context from deadlocking the asynchronous disposal.
        Task.Run(() => Browsers.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        if (desktop.IsValueCreated) desktop.Value.Dispose();
        Store.Event("server_stop", null, new { });
        Store.Dispose();
        instanceLock.Dispose();
    }
}
