using System.Text;
using System.Text.Json;

namespace Codexish.Server;

// The raw definitions are deliberately not deserialized into typed options: a malformed optional server
// must never trigger configuration backup/recovery or prevent another server from starting.
public sealed class McpDefinition
{
    public string Name { get; set; } = "";
    public string Identity { get; set; } = "";
    public string Source { get; set; } = "codexish.json";
    public string? RootId { get; set; }
    public string? RootPath { get; set; }
    public bool IsProject { get; set; }
    public string Transport { get; set; } = "stdio";
    public string Command { get; set; } = "";
    public string[] Args { get; set; } = [];
    public string? Cwd { get; set; }
    public string? Url { get; set; }
    public Dictionary<string, string?> Env { get; set; } = new(StringComparer.Ordinal);
    public string[] EnvVars { get; set; } = [];
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> EnvHttpHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? BearerTokenEnvVar { get; set; }
    public string[]? EnabledTools { get; set; }
    public string[] DisabledTools { get; set; } = [];
    public string[] ReadOnlyTools { get; set; } = [];
    public string State { get; set; } = "not_started";
    public string InitialState { get; set; } = "not_started";
    public string? Error { get; set; }
    public string? OverriddenBy { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string Signature { get; set; } = "";
    internal McpDefinition Copy()
    {
        var copy = (McpDefinition)MemberwiseClone();
        copy.Warnings = [.. Warnings];
        copy.Args = [.. Args]; copy.EnvVars = [.. EnvVars];
        copy.ReadOnlyTools = [.. ReadOnlyTools]; copy.DisabledTools = [.. DisabledTools];
        copy.EnabledTools = EnabledTools is null ? null : [.. EnabledTools];
        copy.Env = new(Env, Env.Comparer); copy.Headers = new(Headers, Headers.Comparer);
        copy.EnvHttpHeaders = new(EnvHttpHeaders, EnvHttpHeaders.Comparer);
        return copy;
    }
}

public sealed record McpProjectFile(string RootId, string State, string? Error, McpDefinition[] Definitions, bool KeptPrevious = false)
{
    public string[] Warnings { get; init; } = [];
}

public static class McpConfiguration
{
    public static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static McpDefinition[] ParseTopLevel(ServerConfig config)
    {
        var primary = ParseObject(config.McpServers, config, null, "mcp_servers");
        var alias = ParseObject(config.McpServersAlias, config, null, "mcpServers");
        var names = primary.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in alias.Where(d => names.Contains(d.Name)))
        {
            string warning = $"mcpServers server '{entry.Name}' is also in mcp_servers; mcp_servers wins.";
            config.Warnings.Add(warning);
            primary.First(d => d.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)).Warnings.Add(warning);
        }
        return [.. primary, .. alias.Where(d => !names.Contains(d.Name))];
    }

    public static McpDefinition[] ParseProject(JsonElement document, RootConfig root)
    {
        if (document.ValueKind != JsonValueKind.Object)
            throw new JsonException("The project MCP configuration is not an object.");
        JsonElement? value = document.TryGetProperty("mcpServers", out var servers) ? servers : null;
        return ParseObject(value, new ServerConfig { Roots = [root] }, root, "mcpServers");
    }

    public static McpProjectFile ReadProject(RootConfig root, McpProjectFile? previous = null)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root.Path, ".mcp.json")), JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("The project MCP configuration is not an object.");
            var context = new ServerConfig { Roots = [root] };
            JsonElement? raw = document.RootElement.TryGetProperty("mcpServers", out var servers) ? servers : null;
            var definitions = ParseObject(raw, context, root, "mcpServers");
            return new(root.Id, "read", null, definitions) { Warnings = context.Warnings.ToArray() };
        }
        catch (FileNotFoundException) { return new(root.Id, "absent", null, []); }
        catch (DirectoryNotFoundException) { return new(root.Id, "absent", null, []); }
        catch (JsonException error) { return new(root.Id, "unparseable", Redaction.Apply(error.Message).Text, previous?.Definitions ?? [], previous is not null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(root.Id, "unreadable", Redaction.Apply(error.Message).Text, previous?.Definitions ?? [], previous is not null); }
    }

    public static McpDefinition[] ApplyOverrides(IEnumerable<McpDefinition> top, IEnumerable<McpDefinition> project)
    {
        var entries = top.ToArray();
        var projects = project.Select(d => d.Copy()).ToArray();
        foreach (var item in projects)
        {
            // Re-evaluate a previously retained project reading when a top-level override disappears.
            if (item.State == "overridden") { item.State = item.InitialState; item.OverriddenBy = null; }
            var replacement = entries.FirstOrDefault(d => d.Identity.Equals(item.Identity, StringComparison.OrdinalIgnoreCase) ||
                (d.RootId is not null && d.RootId.Equals(item.RootId, StringComparison.OrdinalIgnoreCase) && d.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)));
            if (replacement is not null && item.State != "invalid_config")
            { item.State = "overridden"; item.OverriddenBy = replacement.Identity; }
        }
        return [.. entries, .. projects];
    }

    private static McpDefinition[] ParseObject(JsonElement? value, ServerConfig config, RootConfig? projectRoot, string field)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return [];
        if (value.Value.ValueKind != JsonValueKind.Object)
        { config.Warnings.Add($"{field} is not an object and is ignored."); return []; }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<McpDefinition> result = [];
        foreach (var property in value.Value.EnumerateObject())
        {
            var item = ParseDefinition(property.Name, property.Value, config, projectRoot);
            if (!names.Add(property.Name)) { item.State = "invalid_config"; item.Error = "duplicate of an earlier entry"; }
            result.Add(item);
        }
        return result.ToArray();
    }

    private static McpDefinition ParseDefinition(string name, JsonElement raw, ServerConfig config, RootConfig? root)
    {
        var item = new McpDefinition { Name = name, Identity = root is null ? name : root.Id + "/" + name,
            Source = root is null ? "codexish.json" : root.Id + "/.mcp.json", RootId = root?.Id, RootPath = root?.Path,
            IsProject = root is not null, Signature = (root is null ? name : root.Id + "/" + name) + "\n" + Canonical(raw) };
        void Invalid(string reason) { item.State = "invalid_config"; item.Error ??= reason; }
        if (raw.ValueKind != JsonValueKind.Object) { Invalid("server definition must be an object"); return item; }
        bool Has(string key) => raw.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null;
        string? Scalar(JsonElement value, string key)
        {
            if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) { Invalid($"{key} must contain scalar values"); return null; }
            return value.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => value.GetString(), _ => value.GetRawText() };
        }
        string? Str(string key) => raw.TryGetProperty(key, out var v) ? Scalar(v, key) : null;
        string[] Array(string key)
        {
            if (!raw.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return [];
            if (v.ValueKind != JsonValueKind.Array)
            {
                if (v.ValueKind == JsonValueKind.Object) { Invalid($"{key} must contain scalar values"); return []; }
                item.Warnings.Add($"{key} is a scalar; it is treated as a one-element array.");
                return Scalar(v, key) is { } one ? [one] : [];
            }
            return v.EnumerateArray().Select(x => Scalar(x, key)).OfType<string>().ToArray();
        }
        Dictionary<string, string?> Map(string key, StringComparer comparer)
        {
            var values = new Dictionary<string, string?>(comparer);
            if (!raw.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return values;
            if (v.ValueKind != JsonValueKind.Object) { Invalid($"{key} must be an object of scalar values"); return values; }
            foreach (var p in v.EnumerateObject()) values[p.Name] = Scalar(p.Value, key + "." + p.Name);
            return values;
        }
        bool? Boolean(string key)
        {
            if (!raw.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool parsed)) return parsed;
            item.Warnings.Add($"{key} is not true or false and is ignored."); return null;
        }
        string? type = Str("type");
        bool command = Has("command"), url = Has("url");
        bool explicitUsable = (type == "stdio" && command) || ((type is "http" or "streamable-http" or "sse") && url);
        item.Transport = explicitUsable ? (type == "sse" ? "sse" : type == "stdio" ? "stdio" : "http") : url ? "http" : "stdio";
        if (type is not null && !explicitUsable) item.Warnings.Add($"type '{type}' is unknown or lacks its required field; transport is inferred as {item.Transport}.");
        if (!command && !url) Invalid("neither command nor url is configured");
        if (root is null)
        {
            item.RootId = Str("root_id");
            if (item.RootId is { } id)
            {
                var configured = config.Roots.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (configured is null) Invalid($"root '{id}' is not a configured root of the running server; roots take effect when CODEXish restarts");
                else item.RootPath = configured.Path;
            }
        }
        else if (raw.TryGetProperty("root_id", out _)) item.Warnings.Add("root_id in a project file is ignored; the containing root is used.");
        item.Cwd = Str("cwd");
        item.EnabledTools = raw.TryGetProperty("enabled_tools", out _) ? Array("enabled_tools") : null;
        item.DisabledTools = Array("disabled_tools"); item.ReadOnlyTools = Array("read_only_tools");
        if (item.Transport == "stdio")
        {
            item.Command = Str("command") ?? ""; item.Args = Array("args"); item.Env = Map("env", StringComparer.Ordinal); item.EnvVars = Array("env_vars");
            foreach (string key in new[] { "url", "headers", "http_headers", "env_http_headers", "bearer_token_env_var" })
                if (raw.TryGetProperty(key, out _)) item.Warnings.Add($"{key} is ignored on a stdio server.");
            if (item.Command.Length == 0) Invalid("command is empty");
            if (Path.GetExtension(item.Command).Equals(".cmd", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(item.Command).Equals(".bat", StringComparison.OrdinalIgnoreCase))
                if (item.Args.Any(a => a.IndexOfAny(['\r', '\n', '\0']) >= 0)) Invalid("args contains CR, LF or NUL which a batch file cannot receive");
        }
        else
        {
            item.Url = Str("url");
            foreach (var p in Map("http_headers", StringComparer.OrdinalIgnoreCase)) if (p.Value is not null) item.Headers[p.Key] = p.Value;
            foreach (var p in Map("headers", StringComparer.OrdinalIgnoreCase)) if (p.Value is not null) item.Headers[p.Key] = p.Value;
            foreach (var p in Map("env_http_headers", StringComparer.OrdinalIgnoreCase)) if (p.Value is not null) item.EnvHttpHeaders[p.Key] = p.Value;
            item.BearerTokenEnvVar = Str("bearer_token_env_var");
            foreach (string key in new[] { "command", "args", "env", "env_vars" })
                if (raw.TryGetProperty(key, out _)) item.Warnings.Add($"{key} is ignored on an HTTP server.");
            if (item.Url is null || (!item.Url.Contains("${", StringComparison.Ordinal) && (!Uri.TryCreate(item.Url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))))
                Invalid("url must be an absolute http(s) URI");
        }
        bool disabled = Boolean("disabled") == true | Boolean("enabled") == false;
        if (item.State != "invalid_config") item.State = disabled ? "disabled" : root is { Shell: false } ? "needs_shell_grant" : "not_started";
        item.InitialState = item.State;
        return item;
    }

    public static string Canonical(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            void Write(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                { writer.WriteStartObject(); foreach (var p in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); Write(p.Value); } writer.WriteEndObject(); }
                else if (element.ValueKind == JsonValueKind.Array)
                { writer.WriteStartArray(); foreach (var child in element.EnumerateArray()) Write(child); writer.WriteEndArray(); }
                else element.WriteTo(writer);
            }
            Write(value);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
