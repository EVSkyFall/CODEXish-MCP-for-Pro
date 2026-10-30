using System.Security.Cryptography;
using System.Text.Json;

namespace Codexish.Server;

// Never expose the preimage: explicit environment/header values may be credentials. Only this digest is
// retained by the reload manager, and it is never part of capabilities, manifests, events or diagnostics.
public static class McpEffectiveConfiguration
{
    public static string Fingerprint(ServerConfig ownerConfig, McpDefinition definition)
    {
        var basis = ChildEnvironment.Capture();
        string Expand(string value) => ChildEnvironment.Expand(value, basis, "fingerprint", projectRoot: definition.IsProject ? definition.RootPath : null);
        string directory = definition.Cwd is { } cwd ? Expand(cwd) : definition.RootPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? resolutionError = null;
        try
        {
            if (!Path.IsPathRooted(directory)) directory = Path.GetFullPath(directory,
                definition.RootPath ?? Path.GetDirectoryName(ownerConfig.SourcePath ?? ServerConfig.DefaultPath)!);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        { resolutionError = error.GetType().Name; }
        string? command = null, url = null;
        string[]? arguments = null;
        Dictionary<string, string?>? explicitEnvironment = null;
        Dictionary<string, string>? headers = null;
        if (definition.Transport == "stdio")
        {
            var expanded = new Dictionary<string, string?>(ChildEnvironment.Comparer);
            foreach (var (name, value) in definition.Env) expanded[name] = value is null ? null : Expand(value);
            // Only steps 2 and 3 are fingerprinted; the automatic inheritance allowlist is deliberately excluded.
            explicitEnvironment = new(ChildEnvironment.Comparer);
            foreach (string name in definition.EnvVars)
                if (basis.TryGetValue(name, out var value)) explicitEnvironment[name] = value;
            foreach (var (name, value) in expanded)
                if (value is not null) explicitEnvironment[name] = value;
            var child = ChildEnvironment.MountEnvironment(basis, definition.EnvVars, expanded);
            command = Expand(definition.Command);
            try { command = ChildEnvironment.ResolveCommand(command, child, directory); }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
            { resolutionError = error.GetType().Name; }
            arguments = definition.Args.Select(Expand).ToArray();
        }
        else
        {
            url = Expand(definition.Url ?? "");
            headers = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, variable) in definition.EnvHttpHeaders)
                if (basis.TryGetValue(variable, out var value) && value is not null) headers[name] = value;
            foreach (var (name, value) in definition.Headers) headers[name] = Expand(value);
            if (!headers.ContainsKey("Authorization") && definition.BearerTokenEnvVar is { } variableName
                && basis.TryGetValue(variableName, out var token) && token is not null) headers["Authorization"] = "Bearer " + token;
        }
        var effective = JsonSerializer.SerializeToElement(new
        {
            identity = definition.Identity, source = definition.Source, root_id = definition.RootId, root_path = definition.RootPath,
            is_project = definition.IsProject, transport = definition.Transport,
            command, arguments, cwd = directory, url, headers, explicit_environment = explicitEnvironment,
            read_only_tools = definition.ReadOnlyTools.Order(StringComparer.Ordinal).ToArray(),
            enabled_tools = definition.EnabledTools?.Order(StringComparer.Ordinal).ToArray(),
            disabled_tools = definition.DisabledTools.Order(StringComparer.Ordinal).ToArray(),
            state = definition.State, error = definition.Error, overridden_by = definition.OverriddenBy, resolution_error = resolutionError
        });
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(BrowserMounts.Canonical(effective)))).ToLowerInvariant();
    }
}
