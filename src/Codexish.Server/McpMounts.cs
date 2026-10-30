using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Codexish.Server;

// All mounts share a publication lock and immutable snapshots. File reads never invoke config recovery.
public sealed class McpMounts : IAsyncDisposable
{
    private sealed record Entry(string Key, string Signature, string PolicyState, McpMount Mount, BrowserMountConfig? Browser, string? Profile, McpDefinition Display);
    private sealed record Published(Entry[] Entries, McpServerTool[] Tools, McpServerTool[] BrowserTools, (Entry Entry, McpServerTool Tool)[] Allocations, McpProjectFile[] Projects, int BrowserCount, string[] Warnings);
    private readonly CodexishRuntime owner;
    private readonly object publication = new();
    private readonly object reloadSync = new();
    private readonly CancellationTokenSource stopping = new();
    private Published snapshot = new([], [], [], [], [], 0, []);
    private readonly object prepareSync = new();
    private Task? preparation;
    private bool manifestsPrepared;
    private McpDefinition[] topDefinitions = [];
    private bool topDefinitionsParsed;
    private string[] topWarnings = [];
    private HashSet<string> changedPaths = new(StringComparer.Ordinal);
    private McpProjectFile[] projects = [];
    private readonly Dictionary<string, (bool Exists, long Length, long Ticks)> stamps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> fileWarnings = new(StringComparer.Ordinal);
    private const string ManifestOnlySignature = "manifest-only";
    private readonly List<Task> retirements = [];
    private readonly Dictionary<string, Task> lifetimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<McpMount> retiredMounts = [];
    private Task reloadLoop = Task.CompletedTask;
    private int started, disposed;
    private string? reloadError;
    public McpMounts(CodexishRuntime runtime)
    {
        owner = runtime;
    }
    // Only the local top-level configuration and state manifests are touched here. Slow project
    // shares, environment refresh and executable resolution remain background preparation work.
    public void PrepareManifests()
    {
        lock (prepareSync)
        {
            if (manifestsPrepared || preparation is not null) return;
            manifestsPrepared = true;
            lock (reloadSync)
            {
                try { ApplyDefinitions([], force: true, manifestsOnly: true); }
                catch (Exception error) { Warn("Mount manifest preparation failed: " + error.Message); }
            }
        }
    }
    public Task PrepareAsync()
    {
        lock (prepareSync)
        {
            PrepareManifests();
            return preparation ??= Task.Run(() =>
            {
                lock (reloadSync)
                {
                    // Stamp before reading; a save during this work is detected by the next poll.
                    try { Changed(); ReadDefinitionsAndApply(); }
                    catch (Exception error)
                    {
                        // Initial optional discovery has the same recovery boundary as subsequent polls.
                        // Do not retain a faulted preparation task that could later short-circuit disposal.
                        stamps.Clear();
                        Warn("Mount configuration reload failed: " + error.Message);
                    }
                }
            });
        }
    }
    public IReadOnlyList<McpServerTool> Tools => Volatile.Read(ref snapshot).Tools;
    public IReadOnlyList<McpServerTool> BrowserTools => Volatile.Read(ref snapshot).BrowserTools;
    public McpServerTool? Find(string? name) => Tools.FirstOrDefault(t => t.ProtocolTool.Name == name);
    public IReadOnlyList<IMcpResourceTarget> ResourceTargets => Volatile.Read(ref snapshot).Entries.Select(e => (IMcpResourceTarget)e.Mount).ToArray();
    public McpMount Current(McpMount prior) => Volatile.Read(ref snapshot).Entries
        .FirstOrDefault(e => e.Mount.Definition.State != "invalid_config" && e.Mount.Identity.Equals(prior.Identity, StringComparison.OrdinalIgnoreCase)
            && e.Mount.IsBrowser == prior.IsBrowser
            && e.Mount.Definition.IsProject == prior.Definition.IsProject
            && e.Mount.Definition.Source == prior.Definition.Source)?.Mount ?? prior;
    public string Summary() => Summary(false);
    public string BrowserSummary() => Summary(true);
    private string Summary(bool browser)
    {
        var current = Volatile.Read(ref snapshot);
        var entries = current.Entries.Where(e => (e.Browser is not null) == browser).ToArray();
        return entries.Length == 0 ? "none configured" : string.Join(", ", entries.Select(e => $"{e.Mount.Definition.Identity}={e.Mount.State} ({current.Allocations.Count(x => ReferenceEquals(x.Entry, e))} tools, {e.Mount.ToolsSource})"));
    }
    public string ComposeInstructions()
    {
        var blocks = Volatile.Read(ref snapshot).Entries.Where(e => Publishes(e.Mount.Definition))
            .Where(e => !string.IsNullOrWhiteSpace(e.Mount.Instructions))
            .Select(e => $"## {e.Mount.Identity}\n{e.Mount.Instructions!.Trim()}").ToArray();
        return CodexishRuntime.Instructions + (blocks.Length == 0 ? "" : "\n\nMounted MCP servers provided the instructions below for their own tools and resources, each under its server id. They name tools by the server's own names; each mounted tool's description names its server and backend tool.\n\n" + string.Join("\n\n", blocks));
    }
    public (string[] Tools, object Browser, object Mcp) CaptureCapabilities()
    {
        var current = Volatile.Read(ref snapshot);
        return (current.Tools.Select(t => t.ProtocolTool.Name).ToArray(), DescribeBrowsers(current), Describe(current));
    }
    public object Describe() => Describe(Volatile.Read(ref snapshot));
    private object Describe(Published current)
    {
        return new
        {
            mounted = current.Entries.Where(e => e.Browser is null).Select(e => DescribeEntry(e, current)).ToArray(),
            warnings = current.Warnings,
            project_files = current.Projects.Select(p => new { root_id = p.RootId, state = p.State, error = p.Error, warnings = p.Warnings, kept_previous = p.KeptPrevious }).ToArray(),
            environment_source = ChildEnvironment.LastSource, environment_error = ChildEnvironment.LastError,
            reload = ReloadRule, reload_error = reloadError, boundary = Boundary, read_only_tools = ReadOnlyDisclosure
        };
    }
    public object DescribeBrowsers() => DescribeBrowsers(Volatile.Read(ref snapshot));
    private object DescribeBrowsers(Published current)
    {
        return new
        {
            mounted = current.Entries.Where(e => e.Browser is not null).Select(e => DescribeEntry(e, current)).ToArray(),
            configured = current.BrowserCount,
            transport = "stdio child process driven by the official MCP client; CODEXish opens no browser debugging listener",
            profile = "dedicated: CODEXish passes --user-data-dir under state_dir to a Playwright backend, unless the mount's own args already select browser state; existing: the configured arguments select the browser state",
            supervision = "Mounts connect in the background after the server listens. A backend that fails to start or exits is restarted after 1 s, doubling to 60 s between attempts, without giving up; five healthy minutes reset the delay. There is no deadline for a backend's handshake.",
            manifest = "The last tool list each backend reported is kept in state_dir/browser-profiles/<id>.manifest.json and stays listed while that backend is down; calls then answer BROWSER_UNAVAILABLE with the next retry time.",
            limits_source = "Action and navigation limits are the backend's own options and defaults; CODEXish adds no action deadline.",
            boundary = Boundary, read_only_tools = ReadOnlyDisclosure, reload = ReloadRule
        };
    }
    private const string Boundary = "Mounted backend tools, for example file upload, script evaluation or run_code_unsafe, run as the logged-in user and are not contained by the root or its grants: the grants only decide whether CODEXish forwards a call, and the root is the backend's working directory, not a filesystem, network or code sandbox.";
    private const string ReadOnlyDisclosure = "Tools listed in read_only_tools are forwarded without an invocation_id and bypass the ledger because the local configuration says so; CODEXish does not verify that they are free of side effects.";
    private const string ReloadRule = "Mount configuration is read from disk at start and checked every 3 seconds. Saved changes drain changed or removed instances and apply without restarting CODEXish; roots, grants, port and OAuth take effect at restart.";
    private JsonObject DescribeEntry(Entry entry, Published current)
    {
        var node = JsonSerializer.SerializeToNode(entry.Mount.Describe())!.AsObject();
        // Display the current file spelling even when its effective hash did not require a restart.
        node["command"] = entry.Display.Transport == "stdio" ? Redaction.Apply(entry.Display.Command).Text : null;
        node["args"] = entry.Display.Transport == "stdio" ? JsonSerializer.SerializeToNode(Redaction.DisplayArguments(entry.Display.Args)) : null;
        node["url"] = entry.Display.Transport == "stdio" ? null : Redaction.DisplayUrl(entry.Display.Url);
        // The core supplies tool names too; manager owns final collision-resolved names.
        node["tools"] = JsonSerializer.SerializeToNode(current.Allocations.Where(x => ReferenceEquals(x.Entry, entry)).Select(x => x.Tool.ProtocolTool.Name));
        if (entry.Browser is { } b)
        {
            node["id"] = b.Id; node["root_id"] = b.RootId; node["kind"] = b.Kind;
            node["profile_mode"] = b.ProfileMode; node["profile_directory"] = entry.Profile;
            node["warning"] = BrowserMounts.ProfileWarning(b);
        }
        return node;
    }
    internal (string State, bool Session, bool Job, int? ProcessId, int Attempts, DateTimeOffset? NextRetry, string ToolsSource) BrowserResources(string id)
    {
        var m = Volatile.Read(ref snapshot).Entries.Where(e => e.Browser?.Id.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(e => e.Mount.WillStart).First().Mount;
        return (m.State, m.HasSession, m.HasJob, m.ProcessId, m.Attempts, m.NextRetry, m.ToolsSource);
    }
    public void Start()
    {
        if (stopping.IsCancellationRequested || Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref started, 1) != 0) return;
        reloadLoop = Task.Run(async () =>
        {
            try
            {
                try { await PrepareAsync().ConfigureAwait(false); }
                catch (Exception error) when (!stopping.IsCancellationRequested)
                {
                    lock (reloadSync) { stamps.Clear(); Warn("Mount configuration reload failed: " + error.Message); }
                }
                if (stopping.IsCancellationRequested) return;
                lock (reloadSync)
                    foreach (var e in Volatile.Read(ref snapshot).Entries)
                        if (e.Signature != ManifestOnlySignature &&
                            (!lifetimes.TryGetValue(e.Key, out var predecessor) || predecessor.IsCompleted)) e.Mount.Start();
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stopping.Token);
                    try
                    {
                        lock (reloadSync)
                            {
                                PruneRetirements();
                                if (Changed()) ApplyRead(changedPaths);
                            }
                    }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        lock (reloadSync)
                        {
                            // Changed consumed the observed stamps. Retry even if the file is not saved again.
                            stamps.Clear();
                            Warn($"Mount configuration reload failed: {error.GetType().Name}: {error.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        });
    }
    public async Task InitializeAsync() { Start(); await PrepareAsync(); await Task.WhenAll(Volatile.Read(ref snapshot).Entries.Select(e => e.Mount.Settled)); }
    private IEnumerable<string> Paths() => (owner.Config.SourcePath is { } p ? new[] { p } : []).Concat(owner.Config.Roots.Select(r => Path.Combine(r.Path, ".mcp.json")));
    private bool Changed()
    {
        bool changed = false;
        changedPaths = new(StringComparer.Ordinal);
        foreach (var path in Paths())
        {
            (bool Exists, long Length, long Ticks) stamp;
            try { var f = new FileInfo(path); stamp = (f.Exists, f.Exists ? f.Length : 0, f.Exists ? f.LastWriteTimeUtc.Ticks : 0); }
            catch { stamp = (false, -1, 0); }
            if (!stamps.TryGetValue(path, out var before) || before != stamp) { changed = true; changedPaths.Add(path); }
            stamps[path] = stamp;
        }
        return changed;
    }
    internal Action? BeforeReloadRead { get; set; }
    internal Action? AfterReloadRead { get; set; }
    private void ReadDefinitionsAndApply() => ApplyRead(Paths().ToHashSet(StringComparer.Ordinal), force: true);
    private void ApplyRead(HashSet<string> changed, bool force = false)
    {
        lock (reloadSync)
        {
            BeforeReloadRead?.Invoke();
            ApplyDefinitions(changed, force);
        }
    }
    internal Func<string, byte[]> ReadConfigurationFile { get; set; } = File.ReadAllBytes;
    private void ApplyDefinitions(HashSet<string> changed, bool force, bool manifestsOnly = false)
    {
        var config = owner.Config;
        bool mainChanged = force || config.SourcePath is { } source && changed.Contains(source);
        bool mainReadFailed = false, browserReadFailed = false;
        if (mainChanged && config.SourcePath is { } path)
        {
            try
            {
                using var doc = McpConfiguration.ParseDocument(ReadConfigurationFile(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("codexish.json is not an object.");
                config.McpServers = ServerConfig.TryGetReadableProperty(doc.RootElement, "mcp_servers", out var primary) ? primary.Clone() : null;
                config.McpServersAlias = ServerConfig.TryGetReadableProperty(doc.RootElement, "mcpServers", out var alias) ? alias.Clone() : null;
                reloadError = null;
                try
                {
                    config.BrowserMounts = ServerConfig.TryGetReadableProperty(doc.RootElement, "browser_mounts", out var browsers)
                        ? ServerConfig.ParseBrowserMounts(browsers) : [];
                }
                catch (Exception error) { browserReadFailed = true; Warn("browser_mounts keeps its previous definitions: " + error.Message, path); }
            }
            catch (Exception error)
            {
                mainReadFailed = true;
                if (McpConfiguration.IsTransientReadFailure(error)) stamps.Remove(path);
                Warn("codexish.json keeps its previous definitions: " + error.Message, path);
            }
        }
        if (mainChanged && (!mainReadFailed || !topDefinitionsParsed))
        {
            if (!mainReadFailed && !browserReadFailed && config.SourcePath is { } healthyPath) fileWarnings.Remove(healthyPath);
            List<string> warnings = [];
            topDefinitions = McpConfiguration.ParseTopLevel(config, warnings);
            topDefinitionsParsed = true;
            topWarnings = warnings.Distinct(StringComparer.Ordinal).Select(w => Redaction.Apply(w).Text).ToArray();
        }
        if (!manifestsOnly) projects = config.Roots.Select(r =>
        {
            var previous = projects.FirstOrDefault(p => p.RootId.Equals(r.Id, StringComparison.OrdinalIgnoreCase));
            string projectPath = Path.Combine(r.Path, ".mcp.json");
            var result = force || previous is null || changed.Contains(projectPath)
                ? McpConfiguration.ReadProject(r, previous, ReadConfigurationFile) : previous;
            if (result.RetryRead) stamps.Remove(projectPath);
            return result;
        }).ToArray();
        if (!manifestsOnly) AfterReloadRead?.Invoke();
        var definitions = McpConfiguration.ApplyOverrides(topDefinitions, projects.SelectMany(p => p.Definitions));
        var prior = Volatile.Read(ref snapshot).Entries.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);
        string Effective(string key, McpDefinition definition, bool sourceChanged)
            => manifestsOnly ? ManifestOnlySignature : !sourceChanged && prior.TryGetValue(key, out var old) && old.Signature != ManifestOnlySignature ? old.Signature : McpEffectiveConfiguration.Fingerprint(config, definition);
        var desired = new List<(string Key, string Signature, McpDefinition Definition, BrowserMountConfig? Browser, string? Profile)>();
        var ids = BrowserMounts.NewIdSet();
        int ordinal = 0;
        foreach (var raw in config.BrowserMounts ?? [])
        {
            var b = BrowserMounts.Normalize(raw);
            var problem = raw is null ? "the browser_mounts entry is null" : BrowserMounts.Invalid(b, ids);
            var root = config.Roots.FirstOrDefault(r => r.Id.Equals(b.RootId, StringComparison.OrdinalIgnoreCase));
            var profile = problem is null ? Path.Combine(config.StateDir, "browser-profiles", b.Id) : "";
            var d = new McpDefinition { Name = b.Id, Identity = "browser:" + b.Id, Source = "codexish.json", RootId = b.RootId,
                RootPath = root?.Path, Command = b.Command, Args = problem is null ? BrowserMounts.LaunchArguments(b, profile) : [], ReadOnlyTools = b.ReadOnlyTools,
                State = problem is null ? "not_started" : "invalid_config", Error = problem };
            if (BrowserMounts.ProfileWarning(b) is { } warning) d.Warnings.Add(warning);
            bool ownsProfile = (b.Kind == "playwright" && b.ProfileMode == "dedicated" && !BrowserMounts.OwnsBrowserState(b)) || b.Args.Any(a => a is not null && a.Contains("{profile_dir}", StringComparison.Ordinal));
            string key = "browser:" + b.Id.Length + ":" + b.Id + ":" + (problem is null ? 0 : ordinal + 1);
            d.Signature = Effective(key, d, mainChanged && !mainReadFailed && !browserReadFailed);
            desired.Add((key, d.Signature, d, b, ownsProfile ? profile : null));
            ordinal++;
        }
        ordinal = 0;
        foreach (var d in definitions)
        {
            string key = (d.IsProject ? "project:" : "mcp:") + d.Identity.Length + ":" + d.Identity + ":" + (d.Error == "duplicate of an earlier entry" ? ordinal + 1 : 0);
            bool sourceChanged = d.IsProject
                ? (force || changed.Contains(Path.Combine(d.RootPath!, ".mcp.json"))) && !projects.Any(p => p.RootId.Equals(d.RootId, StringComparison.OrdinalIgnoreCase) && p.KeptPrevious)
                : mainChanged && !mainReadFailed;
            desired.Add((key, Effective(key, d, sourceChanged), d, null, null));
            ordinal++;
        }
        var next = new List<Entry>();
        var replacements = new List<(Entry Old, Entry? New)>();
        foreach (var item in desired)
        {
            bool retainedSource = item.Definition.IsProject
                ? projects.Any(p => p.RootId == item.Definition.RootId && p.State is "unreadable" or "unparseable")
                : mainReadFailed || item.Browser is not null && browserReadFailed;
            string policyState = item.Definition.State + "\n" + item.Definition.OverriddenBy;
            if (prior.Remove(item.Key, out var old) && old.Signature != ManifestOnlySignature && (old.Signature == item.Signature && old.PolicyState == policyState || retainedSource && old.PolicyState == policyState))
            { old.Mount.RefreshConfigurationWarnings(item.Definition.Warnings.ToArray()); old.Mount.RefreshLaunchDefinition(item.Definition); next.Add(old with { Display = item.Definition }); continue; }
            var mount = new McpMount(owner, item.Definition, Republish, item.Browser);
            if (old is not null) mount.MarkStarting();
            var entry = new Entry(item.Key, item.Signature, policyState, mount, item.Browser, item.Profile, item.Definition);
            next.Add(entry);
            if (old is not null) replacements.Add((old, entry));
            else if (!manifestsOnly && started != 0 && !stopping.IsCancellationRequested)
            {
                if (lifetimes.TryGetValue(item.Key, out var predecessor) && !predecessor.IsCompleted)
                    Track(item.Key, StartAfter(predecessor, mount));
                else mount.Start();
            }
        }
        replacements.AddRange(prior.Values.Select(e => (e, (Entry?)null)));
        var drains = new Dictionary<McpMount, Task>();
        lock (publication)
        {
            // Retire before publication so even already-resolved wrappers cannot acquire an old session afterward.
            foreach (var (old, fresh) in replacements)
                drains[old.Mount] = old.Mount.DrainAsync(removed: fresh is null);
            var entries = next.ToArray();
            var all = Allocate(entries).ToArray();
            Volatile.Write(ref snapshot, new(entries, all.Select(x => x.Tool).ToArray(), all.Where(x => x.Entry.Browser is not null).Select(x => x.Tool).ToArray(), all, projects, config.BrowserMounts?.Length ?? 0, topWarnings));
        }
        foreach (var (old, fresh) in replacements)
        {
            lock (retirements) retiredMounts.Add(old.Mount);
            var drain = drains[old.Mount];
            var predecessor = lifetimes.GetValueOrDefault(old.Key, Task.CompletedTask);
            Track(old.Key, CompleteRetirement(old.Mount, StartAfter(Task.WhenAll(predecessor, drain), fresh?.Mount)));
        }
        PruneRetirements();
        if (!manifestsOnly) fileWarnings.Remove("<mount preparation>");
    }
    private async Task StartAfter(Task predecessor, McpMount? fresh)
    {
        await predecessor.ConfigureAwait(false);
        if (!stopping.IsCancellationRequested && Volatile.Read(ref started) != 0) fresh?.Start();
    }
    private async Task CompleteRetirement(McpMount retired, Task work)
    {
        try { await work.ConfigureAwait(false); }
        finally { lock (retirements) retiredMounts.Remove(retired); }
    }
    private void Track(string key, Task task)
    {
        lifetimes[key] = task;
        lock (retirements) retirements.Add(task);
    }
    private void PruneRetirements()
    {
        foreach (var key in lifetimes.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray()) lifetimes.Remove(key);
        lock (retirements)
        {
            retirements.RemoveAll(t => t.IsCompleted);
        }
    }
    private void Warn(string text, string? file = null)
    {
        reloadError = Redaction.Apply(text).Text;
        // A lasting source problem stays visible, but does not append the same SQLite event every poll.
        string key = file ?? "<mount preparation>";
        if (fileWarnings.TryGetValue(key, out var previous) && previous == reloadError) return;
        fileWarnings[key] = reloadError;
        // A logging failure must not terminate configuration supervision either.
        try { owner.Store.Event("mcp_reload_warning", null, new { error = reloadError }); } catch { }
    }
    private static bool Publishes(McpDefinition d) => d.State is not ("disabled" or "needs_shell_grant" or "overridden" or "invalid_config");
    private void Republish()
    {
        lock (publication)
        {
            var all = Allocate(snapshot.Entries);
            Volatile.Write(ref snapshot, snapshot with { Tools = all.Select(x => x.Tool).ToArray(), BrowserTools = all.Where(x => x.Entry.Browser is not null).Select(x => x.Tool).ToArray(), Allocations = all.ToArray() });
        }
    }
    private static List<(Entry Entry, McpServerTool Tool)> Allocate(Entry[] entries)
    {
        var taken = new HashSet<string>(CodexishRuntime.ToolNames, StringComparer.Ordinal);
        List<(Entry, McpServerTool)> result = [];
        foreach (var e in entries.Where(e => Publishes(e.Mount.Definition)))
            foreach (var backend in e.Mount.BackendTools)
            {
                var d = e.Mount.Definition;
                string plain = e.Browser is { } b ? BrowserMounts.ToolName(b.Id, backend.Name) : ToolName(d.Identity, backend.Name, d.IsProject);
                string name = taken.Contains(plain) ? HashedName(e.Browser is null ? "mcp" : "browser", e.Browser?.Id ?? d.Identity, backend.Name) : plain;
                // A literal earlier name may itself equal a later hash candidate. Rehash in precedence order.
                int ordinal = 2;
                while (!taken.Add(name))
                    name = HashedName(e.Browser is null ? "mcp" : "browser", e.Browser?.Id ?? d.Identity, backend.Name, ordinal++);
                result.Add((e, e.Mount.CreateTool(backend, name)));
            }
        return result;
    }
    internal static string ToolName(string identity, string backend, bool project = false)
    {
        string raw = "mcp_" + (project ? identity.Replace('/', '_') : identity) + "_" + backend;
        string plain = Regex.Replace(raw, "[^A-Za-z0-9_-]", "_");
        // Project slash is a structural separator, not a sanitized character.
        return raw == plain && plain.Length <= 64 ? plain : HashedName("mcp", identity, backend);
    }
    internal static string HashedName(string prefix, string identity, string backend, int? ordinal = null)
    {
        string server = Regex.Replace(identity, "[^A-Za-z0-9_-]", "_");
        server = server[..Math.Min(12, server.Length)];
        string tool = Regex.Replace(backend, "[^A-Za-z0-9_-]", "_");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToLowerInvariant() + "\n" + backend + (ordinal is null ? "" : "\n" + ordinal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))))[..10].ToLowerInvariant();
        int available = 64 - prefix.Length - server.Length - hash.Length - 3;
        return $"{prefix}_{server}_{tool[..Math.Min(available, tool.Length)]}_{hash}";
    }
    public void Stop()
    {
        lock (reloadSync)
        {
            try { stopping.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var e in snapshot.Entries) e.Mount.Stop();
            lock (retirements) foreach (var retired in retiredMounts) retired.Stop();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Stop(); await reloadLoop;
        if (preparation is { } pendingPreparation) await pendingPreparation;
        await Task.WhenAll(snapshot.Entries.Select(e => e.Mount.DisposeAsync().AsTask()));
        Task[] pending; lock (retirements) pending = retirements.ToArray();
        await Task.WhenAll(pending);
        stopping.Dispose();
    }
}
