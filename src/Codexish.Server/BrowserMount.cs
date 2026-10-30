using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Codexish.Server;

public sealed class BrowserMountConfig
{
    [JsonIgnore] public string? ParseError { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "playwright";
    [JsonPropertyName("root_id")] public string RootId { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "playwright";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("args")] public string[] Args { get; set; } = [];
    [JsonPropertyName("profile_mode")] public string ProfileMode { get; set; } = "dedicated";
    [JsonPropertyName("read_only_tools")] public string[] ReadOnlyTools { get; set; } = [];
}

// Browser-specific profiles and public compatibility facade; session lifetimes and publication are shared
// with local MCP mounts so reload, resources and notifications obey one coherent contract.
public sealed class BrowserMounts(CodexishRuntime owner) : IAsyncDisposable
{
    public const int MaxToolName = 64;
    private static readonly string[] ProfileFlags = ["--user-data-dir", "--cdp-endpoint", "--extension", "--storage-state", "--config"];
    public IReadOnlyList<McpServerTool> Tools => owner.Mounts.BrowserTools;
    public McpServerTool? Find(string? name) => Tools.FirstOrDefault(t => t.ProtocolTool.Name == name);
    public string Summary() => owner.Mounts.BrowserSummary();
    public object Describe() => owner.Mounts.DescribeBrowsers();
    public void Start() => owner.Mounts.Start();
    public void Stop() => owner.Mounts.Stop();
    public Task InitializeAsync() => owner.Mounts.InitializeAsync();
    public ValueTask DisposeAsync() => owner.Mounts.DisposeAsync();
    internal (string State, bool Session, bool Job, int? ProcessId, int Attempts, DateTimeOffset? NextRetry, string ToolsSource) Resources(string id) => owner.Mounts.BrowserResources(id);

    public static string ToolName(string mount, string backend)
    {
        string name = "browser_" + mount + "_" + Regex.Replace(backend, "[^A-Za-z0-9_-]", "_");
        if (name.Length <= MaxToolName && Regex.IsMatch(backend, "^[A-Za-z0-9_-]+$")) return name;
        // The hash of the original name keeps shortened or sanitized names distinct and stable across restarts.
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(backend)))[..10].ToLowerInvariant();
        return name[..Math.Min(name.Length, MaxToolName - hash.Length - 1)] + "_" + hash;
    }

    // A mount whose own args already select browser state keeps them unchanged and gets no second --user-data-dir.
    internal static bool OwnsBrowserState(BrowserMountConfig mount) =>
        mount.Args.Any(a => a is not null && ProfileFlags.Any(flag => a == flag || a.StartsWith(flag + "=", StringComparison.Ordinal)));

    internal static string? ProfileWarning(BrowserMountConfig mount) =>
        mount.Kind == "playwright" && mount.ProfileMode == "dedicated" && OwnsBrowserState(mount)
            ? "Its own args already select browser state (--user-data-dir, --cdp-endpoint, --extension, --storage-state or --config), " +
              "so it runs with those args unchanged and without the dedicated profile directory CODEXish would supply."
            : null;

    internal static string[] LaunchArguments(BrowserMountConfig mount, string profileDirectory)
    {
        var arguments = mount.Args.Select(a => a.Replace("{profile_dir}", profileDirectory, StringComparison.Ordinal)).ToList();
        // An explicit directory also avoids Chrome refusing remote debugging for a default-looking profile.
        if (mount.Kind == "playwright" && mount.ProfileMode == "dedicated" && !OwnsBrowserState(mount))
        {
            arguments.Add("--user-data-dir");
            arguments.Add(profileDirectory);
        }
        return arguments.ToArray();
    }

    // Only what a browser backend needs to start; a variable whose name looks like a credential is never inherited.
    internal static Dictionary<string, string?> ChildEnvironment() =>
        Codexish.Server.ChildEnvironment.MountEnvironment(Codexish.Server.ChildEnvironment.Capture(), [],
            new Dictionary<string, string?>(), _ => { });

    // JSON null for a field or collection becomes an empty value, so a malformed entry fails its own validation
    // instead of throwing out of initialization.
    internal static BrowserMountConfig Normalize(BrowserMountConfig? mount)
    {
        mount ??= new BrowserMountConfig { Id = "", Kind = "", ProfileMode = "" };
        mount.Id ??= "";
        mount.RootId ??= "";
        mount.Kind ??= "";
        mount.Command ??= "";
        mount.ProfileMode ??= "";
        mount.Args ??= [];
        mount.ReadOnlyTools ??= [];
        return mount;
    }

    // Ids are compared without case on every platform, as root ids are: a dedicated profile directory is named after
    // the id, and on Windows "pw" and "PW" would share it.
    internal static ISet<string> NewIdSet() => new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal static string? Invalid(BrowserMountConfig mount, ISet<string> ids)
    {
        if (mount.ParseError is not null) return mount.ParseError;
        if (!Regex.IsMatch(mount.Id, "^[A-Za-z0-9_-]{1,24}$")) return "id must be 1-24 ASCII letters, digits, underscores or hyphens.";
        if (ids.Contains(mount.Id)) return $"id '{mount.Id}' is already used by an earlier mount (ids are compared without case).";
        if (string.IsNullOrWhiteSpace(mount.Command)) return "command is required.";
        if (string.IsNullOrWhiteSpace(mount.RootId)) return "root_id is required.";
        if (mount.Kind is not ("playwright" or "custom")) return "kind must be playwright or custom.";
        if (mount.ProfileMode is not ("dedicated" or "existing")) return "profile_mode must be dedicated or existing.";
        if (mount.Args.Any(a => a is null)) return "args must not contain null.";
        if (mount.ReadOnlyTools.Any(t => t is null)) return "read_only_tools must not contain null.";
        // Only a valid entry takes its id, so an invalid entry cannot block a valid one that follows it.
        ids.Add(mount.Id);
        return null;
    }

    internal static JsonElement WrapSchema(JsonElement backendSchema, bool readOnly, Action<string>? warning = null)
    {
        try { return WrapSchemaCore(backendSchema, readOnly); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            warning?.Invoke("The backend input schema could not be wrapped (" + error.GetType().Name + "); arguments accepts any object.");
            return WrapSchemaCore(JsonSerializer.SerializeToElement(new { type = "object" }), readOnly);
        }
    }
    private static JsonElement WrapSchemaCore(JsonElement backendSchema, bool readOnly)
    {
        var inner = JsonNode.Parse(backendSchema.GetRawText()) as JsonObject ?? new JsonObject { ["type"] = "object" };
        Rebase(inner);
        var outer = new JsonObject { ["type"] = "object" };
        // A dialect declaration belongs to the schema root, which is now the wrapper.
        if (inner.TryGetPropertyValue("$schema", out var dialect))
        {
            inner.Remove("$schema");
            outer["$schema"] = dialect;
        }
        outer["properties"] = new JsonObject
        {
            ["arguments"] = inner,
            ["invocation_id"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = readOnly
                    ? "Not needed for this read-only tool; reads are not queued or recorded."
                    : "Required. " + Ledger.InvocationIdGuidance + " Inspect unknown effects instead of replaying."
            },
            ["wait_ms"] = new JsonObject
            {
                ["type"] = "integer", ["minimum"] = 0, ["default"] = 1000,
                ["description"] = "Response wait only; a running backend call continues and operation_inspect follows it."
            }
        };
        outer["required"] = readOnly ? new JsonArray("arguments") : new JsonArray("arguments", "invocation_id");
        outer["additionalProperties"] = false;
        return JsonSerializer.SerializeToElement(outer);
    }

    private static void Rebase(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("$id")) return; // A schema resource with its own base keeps its reference scope.
            if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference))
            {
                if (reference == "#") obj["$ref"] = "#/properties/arguments";
                else if (reference.StartsWith("#/", StringComparison.Ordinal)) obj["$ref"] = "#/properties/arguments/" + reference[2..];
            }
            foreach (var child in obj.ToArray()) if (child.Value is not null) Rebase(child.Value);
        }
        else if (node is JsonArray array)
            foreach (var child in array) if (child is not null) Rebase(child);
    }

    // Object keys are sorted so equivalent JSON retries share a digest; array order and strings are unchanged.
    internal static JsonElement Canonical(JsonElement input)
    {
        JsonNode? Sort(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => new JsonObject(element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Name, Sort(p.Value)))),
            JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(Sort).ToArray()),
            _ => JsonNode.Parse(element.GetRawText())
        };
        return JsonSerializer.SerializeToElement(Sort(input));
    }

    internal static JsonElement Redacted(JsonElement element)
    {
        JsonNode? Clean(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => JsonValue.Create(Redaction.Apply(e.GetString()!).Text),
            JsonValueKind.Array => new JsonArray(e.EnumerateArray().Select(Clean).ToArray()),
            JsonValueKind.Object => new JsonObject(e.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, Clean(p.Value)))),
            _ => JsonNode.Parse(e.GetRawText())
        };
        return JsonSerializer.SerializeToElement(Clean(element));
    }

    // Text blocks travel as data.text. Every other block except binary image and audio is redacted string by string,
    // so embedded text resources, resource links, their URIs, names and descriptions and any later block type are
    // covered; base64 payloads ("data" of image/audio, "blob" of binary resources) are left intact.
    internal static IEnumerable<ContentBlock> PassThrough(IEnumerable<ContentBlock> content)
    {
        foreach (var block in content)
        {
            if (block is TextContentBlock) continue;
            yield return block is ImageContentBlock or AudioContentBlock ? block : RedactedBlock(block);
        }
    }

    private static ContentBlock RedactedBlock(ContentBlock block)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(block, McpJsonUtilities.DefaultOptions);
            Scrub(node);
            return node.Deserialize<ContentBlock>(McpJsonUtilities.DefaultOptions)
                ?? throw new JsonException("The redacted content block could not be read back.");
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new TextContentBlock { Text = $"[CODEXish omitted a '{block.Type}' content block it could not redact: {error.GetType().Name}]" };
        }
    }

    internal static void Scrub(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            bool binary = obj["type"] is JsonValue kind && kind.TryGetValue<string>(out var type) && type is "image" or "audio";
            foreach (var (name, child) in obj.ToArray())
            {
                if (name == "blob" || (binary && name == "data")) continue;
                if (child is JsonValue value && value.TryGetValue<string>(out var text)) obj[name] = Redaction.Apply(text).Text;
                else Scrub(child);
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)) array[i] = Redaction.Apply(text).Text;
                else Scrub(array[i]);
            }
        }
    }

}
