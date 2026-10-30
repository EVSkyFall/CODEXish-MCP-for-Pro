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
    private sealed record Entry(string Key, string Signature, string PolicyState, McpMount Mount, BrowserMountConfig? Browser, string? Profile);
    private sealed record Published(Entry[] Entries, McpServerTool[] Tools, McpServerTool[] BrowserTools, (Entry Entry, McpServerTool Tool)[] Allocations);
    private readonly CodexishRuntime owner;
    private readonly object publication = new();
    private readonly object reloadSync = new();
    private readonly CancellationTokenSource stopping = new();
    private Published snapshot = new([], [], [], []);
    private McpProjectFile[] projects = [];
    private readonly Dictionary<string, (bool Exists, long Length, long Ticks)> stamps = new(StringComparer.Ordinal);
    private readonly List<Task> retirements = [];
    private readonly Dictionary<string, Task> lifetimes = new(StringComparer.Ordinal);
    private readonly List<McpMount> retiredMounts = [];
    private Task reloadLoop = Task.CompletedTask;
    private int started, disposed;
    private string? reloadError;
    public McpMounts(CodexishRuntime runtime)
    {
        owner = runtime;
        ReadDefinitionsAndApply();
    }
    public IReadOnlyList<McpServerTool> Tools => Volatile.Read(ref snapshot).Tools;
    public IReadOnlyList<McpServerTool> BrowserTools => Volatile.Read(ref snapshot).BrowserTools;
    public McpServerTool? Find(string? name) => Tools.FirstOrDefault(t => t.ProtocolTool.Name == name);
    public IReadOnlyList<IMcpResourceTarget> ResourceTargets => Volatile.Read(ref snapshot).Entries.Select(e => (IMcpResourceTarget)e.Mount).ToArray();
    public McpMount Current(McpMount prior) => Volatile.Read(ref snapshot).Entries
        .FirstOrDefault(e => e.Mount.Identity.Equals(prior.Identity, StringComparison.OrdinalIgnoreCase)
            && e.Mount.IsBrowser == prior.IsBrowser
            && e.Mount.Definition.IsProject == prior.Definition.IsProject
            && e.Mount.Definition.Source == prior.Definition.Source)?.Mount ?? prior;
    public string Summary() => Summary(false);
    public string BrowserSummary() => Summary(true);
    private string Summary(bool browser)
    {
        var entries = Volatile.Read(ref snapshot).Entries.Where(e => (e.Browser is not null) == browser).ToArray();
        return entries.Length == 0 ? "none configured" : string.Join(", ", entries.Select(e => $"{e.Mount.Definition.Identity}={e.Mount.State}"));
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
        lock (reloadSync)
        lock (publication)
            return (snapshot.Tools.Select(t => t.ProtocolTool.Name).ToArray(), DescribeBrowsers(), Describe());
    }
    public object Describe()
    {
        var current = Volatile.Read(ref snapshot);
        return new
        {
            mounted = current.Entries.Where(e => e.Browser is null).Select(e => DescribeEntry(e, current)).ToArray(),
            project_files = projects.Select(p => new { root_id = p.RootId, state = p.State, error = p.Error, warnings = p.Warnings, kept_previous = p.KeptPrevious }).ToArray(),
            environment_source = ChildEnvironment.LastSource, environment_error = ChildEnvironment.LastError,
            reload = ReloadRule, reload_error = reloadError, boundary = Boundary, read_only_tools = ReadOnlyDisclosure
        };
    }
    public object DescribeBrowsers()
    {
        var current = Volatile.Read(ref snapshot);
        return new
        {
            mounted = current.Entries.Where(e => e.Browser is not null).Select(e => DescribeEntry(e, current)).ToArray(),
            configured = owner.Config.BrowserMounts?.Length ?? 0,
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
        var m = Volatile.Read(ref snapshot).Entries.Single(e => e.Browser?.Id == id).Mount;
        return (m.State, m.HasSession, m.HasJob, m.ProcessId, m.Attempts, m.NextRetry, m.ToolsSource);
    }
    public void Start()
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref started, 1) != 0) return;
        foreach (var e in snapshot.Entries) e.Mount.Start();
        reloadLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stopping.Token);
                    if (Changed()) ReadDefinitionsAndApply();
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        });
    }
    public async Task InitializeAsync() { Start(); await Task.WhenAll(snapshot.Entries.Select(e => e.Mount.Settled)); }
    private IEnumerable<string> Paths() => (owner.Config.SourcePath is { } p ? new[] { p } : []).Concat(owner.Config.Roots.Select(r => Path.Combine(r.Path, ".mcp.json")));
    private bool Changed()
    {
        bool changed = false;
        foreach (var path in Paths())
        {
            (bool Exists, long Length, long Ticks) stamp;
            try { var f = new FileInfo(path); stamp = (f.Exists, f.Exists ? f.Length : 0, f.Exists ? f.LastWriteTimeUtc.Ticks : 0); }
            catch { stamp = (false, -1, 0); }
            if (!stamps.TryGetValue(path, out var before) || before != stamp) changed = true;
            stamps[path] = stamp;
        }
        return changed;
    }
    private void ReadDefinitionsAndApply() { lock (reloadSync) ApplyDefinitions(); }
    private void ApplyDefinitions()
    {
        var config = owner.Config;
        bool mainReadFailed = false, browserReadFailed = false;
        if (config.SourcePath is { } path)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path), McpConfiguration.JsonOptions);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("codexish.json is not an object.");
                config.McpServers = doc.RootElement.TryGetProperty("mcp_servers", out var primary) ? primary.Clone() : null;
                config.McpServersAlias = doc.RootElement.TryGetProperty("mcpServers", out var alias) ? alias.Clone() : null;
                reloadError = null;
                try
                {
                    config.BrowserMounts = doc.RootElement.TryGetProperty("browser_mounts", out var browsers)
                        ? browsers.Deserialize<BrowserMountConfig[]>() ?? [] : [];
                }
                catch (JsonException error) { browserReadFailed = true; Warn("browser_mounts keeps its previous definitions: " + error.Message); }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { mainReadFailed = true; Warn("codexish.json keeps its previous definitions: " + error.Message); }
        }
        projects = config.Roots.Select(r => McpConfiguration.ReadProject(r, projects.FirstOrDefault(p => p.RootId == r.Id))).ToArray();
        var definitions = McpConfiguration.ApplyOverrides(McpConfiguration.ParseTopLevel(config), projects.SelectMany(p => p.Definitions));
        var desired = new List<(string Key, string Signature, McpDefinition Definition, BrowserMountConfig? Browser, string? Profile)>();
        var ids = BrowserMounts.NewIdSet();
        int ordinal = 0;
        foreach (var raw in config.BrowserMounts ?? [])
        {
            var b = BrowserMounts.Normalize(raw);
            var problem = raw is null ? "the browser_mounts entry is null." : BrowserMounts.Invalid(b, ids);
            var root = config.Roots.FirstOrDefault(r => r.Id.Equals(b.RootId, StringComparison.OrdinalIgnoreCase));
            var profile = problem is null ? Path.Combine(config.StateDir, "browser-profiles", b.Id) : "";
            var d = new McpDefinition { Name = b.Id, Identity = "browser:" + b.Id, Source = "codexish.json", RootId = b.RootId,
                RootPath = root?.Path, Command = b.Command, Args = problem is null ? BrowserMounts.LaunchArguments(b, profile) : [], ReadOnlyTools = b.ReadOnlyTools,
                State = problem is null ? "not_started" : "invalid_config", Error = problem };
            if (BrowserMounts.ProfileWarning(b) is { } warning) d.Warnings.Add(warning);
            bool ownsProfile = (b.Kind == "playwright" && b.ProfileMode == "dedicated" && !BrowserMounts.OwnsBrowserState(b)) || b.Args.Any(a => a is not null && a.Contains("{profile_dir}", StringComparison.Ordinal));
            d.Signature = McpEffectiveConfiguration.Fingerprint(config, d);
            desired.Add(("browser:" + b.Id.ToLowerInvariant() + (problem is null ? "" : ":invalid:" + ordinal), d.Signature, d, b, ownsProfile ? profile : null));
            ordinal++;
        }
        ordinal = 0;
        foreach (var d in definitions)
        {
            desired.Add(((d.IsProject ? "project:" : "mcp:") + d.Identity.ToLowerInvariant() + (d.Error == "duplicate of an earlier entry" ? ":duplicate:" + ordinal : ""), McpEffectiveConfiguration.Fingerprint(config, d), d, null, null));
            ordinal++;
        }
        var prior = snapshot.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var next = new List<Entry>();
        var replacements = new List<(Entry Old, Entry? New)>();
        foreach (var item in desired)
        {
            bool retainedSource = item.Definition.IsProject
                ? projects.Any(p => p.RootId == item.Definition.RootId && p.State is "unreadable" or "unparseable")
                : mainReadFailed || item.Browser is not null && browserReadFailed;
            string policyState = item.Definition.State + "\n" + item.Definition.OverriddenBy;
            if (prior.Remove(item.Key, out var old) && (old.Signature == item.Signature || retainedSource && old.PolicyState == policyState))
            { next.Add(old); continue; }
            var mount = new McpMount(owner, item.Definition, Republish, item.Browser);
            if (old is not null) mount.MarkStarting();
            var entry = new Entry(item.Key, item.Signature, policyState, mount, item.Browser, item.Profile);
            next.Add(entry);
            if (old is not null) replacements.Add((old, entry));
            else if (started != 0)
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
            snapshot = new(next.ToArray(), [], [], []);
            Republish();
        }
        foreach (var (old, fresh) in replacements)
        {
            lock (retirements) retiredMounts.Add(old.Mount);
            var drain = drains[old.Mount];
            var predecessor = lifetimes.GetValueOrDefault(old.Key, Task.CompletedTask);
            Track(old.Key, StartAfter(Task.WhenAll(predecessor, drain), fresh?.Mount));
        }
        Changed();
    }
    private async Task StartAfter(Task predecessor, McpMount? fresh)
    {
        await predecessor.ConfigureAwait(false);
        if (!stopping.IsCancellationRequested && Volatile.Read(ref started) != 0) fresh?.Start();
    }
    private void Track(string key, Task task) { lifetimes[key] = task; lock (retirements) retirements.Add(task); }
    private void Warn(string text) { reloadError = Redaction.Apply(text).Text; owner.Store.Event("mcp_reload_warning", null, new { error = reloadError }); }
    private static bool Publishes(McpDefinition d) => d.State is not ("disabled" or "needs_shell_grant" or "overridden" or "invalid_config");
    private void Republish()
    {
        lock (publication)
        {
            var all = Allocate(snapshot.Entries);
            Volatile.Write(ref snapshot, new(snapshot.Entries, all.Select(x => x.Tool).ToArray(), all.Where(x => x.Entry.Browser is not null).Select(x => x.Tool).ToArray(), all.ToArray()));
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
        await Task.WhenAll(snapshot.Entries.Select(e => e.Mount.DisposeAsync().AsTask()));
        Task[] pending; lock (retirements) pending = retirements.ToArray();
        await Task.WhenAll(pending);
        stopping.Dispose();
    }
}
