using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Codexish.Server;

public sealed class CodexishRuntime : IDisposable
{
    public const string ProtocolVersion = "2025-11-25";
    // From the assembly, so a release only sets the version at build time; "+<commit>" build metadata is dropped.
    public static readonly string ServerVersion =
        typeof(CodexishRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(CodexishRuntime).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
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
    public ClientRegistry Clients { get; }
    public BrowserMounts Browsers { get; }
    public bool AuthDisabled { get; }
    public string HooksDirectory { get; }
    public IReadOnlyList<object> RecoveredProcesses { get; }
    public (int Cancelled, int Unknown) RecoveredInvocations { get; }
    public Retention Retention { get; }
    public Vitals Heartbeat { get; }

    private readonly FileStream instanceLock;
    private readonly CancellationTokenSource sweeps = new();
    private Task sweepLoop = Task.CompletedTask;
    private bool disposed;

    // Everything that no longer stops the server but should be seen: configuration problems, a rebuilt ledger,
    // failed migrations and ledger rows that could not be read.
    public IReadOnlyList<string> Warnings
    {
        get
        {
            List<string> warnings = [.. Config.Warnings];
            if (Store.Rebuilt is { } rebuilt)
                warnings.Add($"The ledger database was unreadable at {rebuilt.At:o} ({rebuilt.Reason}); it was moved aside as " +
                    $"{string.Join(", ", rebuilt.Quarantined)} and a fresh one was created. Issued tokens were lost, so ChatGPT must sign in again.");
            warnings.AddRange(Store.MigrationErrors);
            if (Store.MalformedRowsSkipped > 0)
                warnings.Add($"{Store.MalformedRowsSkipped} ledger row(s) could not be read and were skipped.");
            if (Heartbeat.PreviousEnd is { } ended) warnings.Add(ended);
            if (Heartbeat.PreviousUnreadable is { } unreadable) warnings.Add(unreadable);
            if (Heartbeat.Redirect is { } redirect) warnings.Add(redirect);
            return warnings;
        }
    }

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
        instanceLock = StateLockedException.Open(config.StateDir);
        Vitals? heartbeat = null;
        try
        {
            Store = new Store(Path.Combine(config.StateDir, "codexish.db"));
            Workspace = new Workspace(config);
            Artifacts = new Artifacts(Store, Path.Combine(config.StateDir, "artifacts"));
            Ledger = new Ledger(Store);
            Files = new FileService(Workspace, Artifacts, Store, config.StateDir);
            Patches = new PatchService(Workspace, Files, Store);
            Processes = new ProcessSupervisor(Store, Artifacts, config);
            Git = new GitService(config, Workspace, Artifacts, Store, HooksDirectory);
            Tokens = new Tokens(Store, config);
            Clients = new ClientRegistry(Store, config);
            Browsers = new BrowserMounts(this);
            RecoveredInvocations = Store.RecoverInvocations();
            RecoveredProcesses = Processes.Recover();
            // Only the holder of the state lock writes the heartbeat, so the previous record is read before it is replaced.
            Heartbeat = heartbeat = Vitals.Begin(config.StateDir, instanceLock.SafeFileHandle);
            Store.Event("server_start", null, new
            {
                version = ServerVersion, auth = authDisabled ? "disabled" : "oauth", launch = Vitals.Launch, previous = heartbeat.PreviousSummary()
            });
            Retention = new Retention(this);
            sweepLoop = Task.Run(() => Retention.RunAsync(sweeps.Token));
        }
        catch (Exception error)
        {
            // A start that fails here releases the state directory, so the next attempt in this process is not
            // refused by a lock this one still holds.
            try
            {
                heartbeat?.Abandon("the start failed: " + error.GetType().Name);
                Store?.Dispose();
            }
            finally { instanceLock.Dispose(); }
            throw;
        }
    }

    // D14. The harness text a connector shows the model before it plans anything.
    public const string Instructions =
        "You are operating the user's Windows PC through these tools. Work until the stated completion condition " +
        "is met and verified; do not stop to ask unless permission is missing or the user cancels. Read before " +
        "editing and pass the returned sha256 to writes. After every edit run the relevant tests or build and fix " +
        "observed failures. Long commands return handles: poll them; a response wait is not a deadline and never " +
        "kills the process. " + Ledger.InvocationIdGuidance + " Inspect unknown operations, " +
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
        roots = Config.Roots.Select(r => new { id = r.Id, path = r.Path, exists = Directory.Exists(r.Path), grants = new { r.Read, r.Write, r.Shell } }),
        warnings = Warnings,
        tools = ToolNames.Concat(Browsers.Tools.Select(t => t.ProtocolTool.Name)).ToArray(),
        browser = Browsers.Describe(),
        desktop = new { native_available = OperatingSystem.IsWindows(), coordinate_space = "virtual desktop physical pixels", uia_thread = "dedicated MTA", input_tick = "metadata only" },
        unsupported = Unsupported.Select(u => new { feature = u.Feature, reason = u.Reason }),
        limits = Limits.Describe(),
        shell = new
        {
            @default = Config.Shell.Default, allowed = Config.Shell.Allowed, usable = Config.Shell.Usable,
            used_without_shell = Config.Shell.EffectiveDefault,
            interpreters = Config.Shell.Usable.ToDictionary(s => s, ProcessSupervisor.ResolveShell)
        },
        git = new { configured = Config.Git.Path, path = Git.Resolved, available = Git.Available, read_only = true, execution = GitService.Execution },
        ledger_rebuilt = Store.Rebuilt is { } rebuilt ? new { at = rebuilt.At, quarantined = rebuilt.Quarantined, reason = rebuilt.Reason } : null,
        retention = Retention.Describe(),
        redaction = Redaction.Disclosure,
        authentication = AuthDisabled ? "disabled (loopback development only)" : "built-in OAuth 2.1 with PKCE, opaque bearer tokens",
        paused = Ledger.Paused,
        checkpoint = LatestCheckpoint(),
        checkpoints = NewestCheckpoints(),
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
        state_dir_note = Config.Roots.Any(r => PathRules.IsInside(r.Path, Config.StateDir) || PathRules.IsInside(Config.StateDir, r.Path))
            ? "state_dir overlaps a root, so the ledger, backups and artifacts are reachable through that root's tools; see warnings."
            : "Outside every root: the ledger, backups and artifacts are not reachable through fs_* tools.",
        warnings = Warnings,
        git = new { path = Git.Resolved, available = Git.Available },
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

    // The newest checkpoint of all tasks.
    public object? LatestCheckpoint() => Store.LatestCheckpoint() is { } row ? DescribeCheckpoint(row) : null;

    // The newest checkpoint of every task, newest first. Retention bounds the list.
    public object[] NewestCheckpoints() => Store.NewestCheckpoints().Select(DescribeCheckpoint).ToArray();

    // A stored checkpoint that is not valid JSON is reported as unreadable in this one response.
    private static object DescribeCheckpoint((long Seq, string Utc, string Json, string Task) row)
    {
        try { return new { seq = row.Seq, utc = row.Utc, task = row.Task, checkpoint = JsonDocument.Parse(row.Json).RootElement.Clone() }; }
        catch (JsonException error)
        {
            return new { seq = row.Seq, utc = row.Utc, task = row.Task, unreadable = true, error = "The stored checkpoint is not valid JSON: " + error.Message };
        }
    }

    public CallToolResult Checkpoint(string goal, string completionCondition, string[] done, string[] remaining, string[] handles,
        string? task = null)
    {
        if (string.IsNullOrWhiteSpace(goal) || string.IsNullOrWhiteSpace(completionCondition))
            throw new CodexishFault("INVALID_ARGUMENT", "goal and completion_condition are required and must be observable.");
        string line = string.IsNullOrWhiteSpace(task) ? Store.DefaultTask : task.Trim();
        var checkpoint = new { goal, completion_condition = completionCondition, done, remaining, handles };
        Store.AddCheckpoint(JsonSerializer.Serialize(checkpoint), line);
        Store.Event("checkpoint", line, checkpoint);
        return Reply.Ok(new
        {
            stored = true,
            task = line,
            checkpoint,
            note = "At the start of the next turn host_capabilities returns this as the newest checkpoint of its task in " +
                "checkpoints, and as checkpoint while no other task has a newer one.",
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

    // Every step runs even when an earlier one fails, and the store and the state-directory lock are released in
    // nested finally blocks, so a later start in this process is never refused by a lock this one still holds. The
    // first failure is rethrown once everything has run.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Exception? first = null;
        void Step(Action action)
        {
            try { action(); }
            catch (Exception error) { first ??= error; }
        }
        try
        {
            Step(sweeps.Cancel);
            // A sweep in progress finishes its current statement set before the store closes.
            Step(() =>
            {
                try { sweepLoop.GetAwaiter().GetResult(); }
                catch (Exception) { /* a failed sweep was already reported */ }
            });
            Step(sweeps.Dispose);
            Step(Browsers.Stop);
            Step(Ledger.Dispose);
            Step(Processes.Dispose);
            // The thread pool keeps a caller's UI synchronization context from deadlocking the asynchronous disposal.
            Step(() => Task.Run(() => Browsers.DisposeAsync().AsTask()).GetAwaiter().GetResult());
            Step(() => { if (desktop.IsValueCreated) desktop.Value.Dispose(); });
            Step(Heartbeat.Dispose);
            Step(() => Store.Event("server_stop", null, new { reason = Heartbeat.StopReason }));
        }
        finally
        {
            try { Store.Dispose(); }
            finally { instanceLock.Dispose(); }
        }
        if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }
}

// Another CODEXish holds this state directory. Only the sharing violation on its lock file becomes this exception;
// any other failure to open the file is thrown as it is.
public sealed class StateLockedException(string stateDir, IOException cause)
    : IOException($"another CODEXish is already running with state_dir {stateDir}; stop it first or use another --state-dir.", cause)
{
    public string StateDir { get; } = stateDir;

    public static FileStream Open(string stateDir)
    {
        try { return new FileStream(Path.Combine(stateDir, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when (IsSharingViolation(error)) { throw new StateLockedException(stateDir, error); }
    }

    // Windows reports ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33) as an HRESULT; elsewhere .NET locks
    // with flock and reports its EWOULDBLOCK errno, 11 on Linux and 35 on macOS and the BSDs.
    public static bool IsSharingViolation(IOException error) =>
        OperatingSystem.IsWindows() ? error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
        : OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? error.HResult == 11
        : error.HResult == 35;
}
