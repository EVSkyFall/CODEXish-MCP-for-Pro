using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codexish.Server;

public sealed class RootConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("read")] public bool Read { get; set; } = true;
    [JsonPropertyName("write")] public bool Write { get; set; } = true;
    [JsonPropertyName("shell")] public bool Shell { get; set; } = true;
}

public sealed class ShellConfig
{
    public static readonly string[] Supported = ["pwsh", "powershell", "cmd"];

    [JsonPropertyName("default")] public string Default { get; set; } = "pwsh";
    [JsonPropertyName("allowed")] public string[] Allowed { get; set; } = ["pwsh", "powershell", "cmd"];

    // The user's own list, minus names this build does not support. Names are matched without case.
    [JsonIgnore]
    public string[] Usable => (Allowed ?? [])
        .Select(n => Supported.FirstOrDefault(s => s.Equals(n?.Trim(), StringComparison.OrdinalIgnoreCase)))
        .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();

    // shell.default when it is usable; otherwise the first usable allowed shell, so the allowed list stays the policy.
    [JsonIgnore]
    public string? EffectiveDefault => Canonical(Default) is { } name && Usable.Contains(name) ? name : Usable.FirstOrDefault();

    public static string? Canonical(string? name) =>
        Supported.FirstOrDefault(s => s.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed class GitConfig
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

public sealed class OAuthConfig
{
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "codexish-chatgpt";
    [JsonPropertyName("client_secret")] public string ClientSecret { get; set; } = "";
    [JsonPropertyName("redirect_uris")] public string[] RedirectUris { get; set; } = [];
    [JsonPropertyName("password_hash")] public string PasswordHash { get; set; } = "";
    [JsonPropertyName("access_token_hours")] public int AccessTokenHours { get; set; } = 12;
    // 0 means refresh tokens never expire; a positive value counts days since the token's last refresh.
    [JsonPropertyName("refresh_token_days")] public int RefreshTokenDays { get; set; }
}

// Age-based cleanup of CODEXish's own state; 0 (or less) keeps that class of state forever.
public sealed class RetentionConfig
{
    [JsonPropertyName("output_days")] public int OutputDays { get; set; } = 30;
    [JsonPropertyName("backup_days")] public int BackupDays { get; set; } = 90;
}

public sealed class ServerConfig
{
    [JsonPropertyName("public_url")] public string PublicUrl { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; } = 3000;
    [JsonPropertyName("allow_hosts")] public string[] AllowHosts { get; set; } = [];
    [JsonPropertyName("allow_origins")] public string[] AllowOrigins { get; set; } = [];
    [JsonPropertyName("state_dir")] public string StateDir { get; set; } = "";
    [JsonPropertyName("roots")] public RootConfig[] Roots { get; set; } = [];
    [JsonPropertyName("shell")] public ShellConfig Shell { get; set; } = new();
    [JsonPropertyName("git")] public GitConfig Git { get; set; } = new();
    [JsonPropertyName("oauth")] public OAuthConfig OAuth { get; set; } = new();
    [JsonPropertyName("control_token")] public string ControlToken { get; set; } = "";
    [JsonPropertyName("browser_mounts")] public BrowserMountConfig[] BrowserMounts { get; set; } = [];
    [JsonPropertyName("tunnel")] public TunnelConfig Tunnel { get; set; } = new();
    [JsonPropertyName("retention")] public RetentionConfig Retention { get; set; } = new();

    // Problems that no longer stop the server: they go to the startup log and host_capabilities.warnings.
    [JsonIgnore] public List<string> Warnings { get; private set; } = [];

    // The file this configuration was loaded from or last saved to, when there is one.
    [JsonIgnore] public string? SourcePath { get; set; }

    public const string DefaultRedirectUri = "https://chatgpt.com/connector_platform_oauth_redirect";
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public static string DefaultDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codexish");
    public static string DefaultPath => System.IO.Path.Combine(DefaultDirectory, "codexish.json");

    public static string UtcStamp(DateTimeOffset time) => time.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);

    // A file that cannot be parsed is kept as <file>.broken-<utc>. When <file>.bak parses, it is loaded and restored
    // as the main file; otherwise the real parse error is raised.
    public static ServerConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"No configuration at {path}. Run --init --password <pw> --public-url <url> [--root id=path] first.");
        byte[] bytes = File.ReadAllBytes(path);
        ServerConfig config;
        string? recovery = null;
        try { config = Parse(bytes, path); }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            (config, recovery) = Recover(path, bytes, error);
        }
        config.SourcePath = System.IO.Path.GetFullPath(path);
        config.Validate();
        if (recovery is not null)
        {
            config.Warnings.Add(recovery);
            Console.Error.WriteLine("WARNING: " + recovery);
        }
        return config;
    }

    private static ServerConfig Parse(byte[] bytes, string path) =>
        JsonSerializer.Deserialize<ServerConfig>(bytes)
            ?? throw new InvalidDataException($"The configuration at {path} is not a JSON object.");

    // The unreadable bytes are kept first. The backup replaces the main file only while that file still holds exactly
    // those bytes: if another writer saved in between, its newer file is loaded instead, or, when that one is
    // unreadable as well, the backup is used without overwriting anything.
    internal static (ServerConfig Config, string Note) Recover(string path, byte[] unreadable, Exception error)
    {
        string broken;
        try { broken = KeepBroken(path, unreadable, DateTimeOffset.UtcNow); }
        catch (Exception keep) when (keep is IOException or UnauthorizedAccessException) { broken = "(not kept: " + keep.Message + ")"; }
        byte[]? current = null;
        try { current = File.ReadAllBytes(path); }
        catch (Exception gone) when (gone is IOException or UnauthorizedAccessException) { current = null; }
        bool unchanged = current is not null && current.AsSpan().SequenceEqual(unreadable);
        if (!unchanged && current is not null)
            try
            {
                return (Parse(current, path), $"The configuration at {path} could not be read ({error.Message}) and was kept as {broken}; " +
                    "another writer had saved a new version meanwhile, which was loaded instead.");
            }
            catch (Exception newer) when (newer is JsonException or InvalidDataException) { /* the newer file is unreadable too */ }
        string backup = path + ".bak";
        byte[]? saved = null;
        ServerConfig? recovered = null;
        try
        {
            if (File.Exists(backup))
            {
                saved = File.ReadAllBytes(backup);
                recovered = Parse(saved, backup);
            }
        }
        catch (Exception unreadableBackup) when (unreadableBackup is JsonException or InvalidDataException or IOException) { recovered = null; }
        if (recovered is null || saved is null)
            throw new InvalidDataException($"The configuration at {path} could not be read: {error.Message} " +
                $"The unreadable file was kept as {broken}, and there is no readable {System.IO.Path.GetFileName(backup)}.", error);
        if (!unchanged)
            return (recovered, $"The configuration at {path} could not be read ({error.Message}) and was kept as {broken}; " +
                $"it changed again while being recovered, so {System.IO.Path.GetFileName(backup)} was loaded without overwriting it.");
        WriteAtomically(path, saved, null);
        return (recovered, $"The configuration at {path} could not be read ({error.Message}); it was kept as {broken}, and the previous " +
            $"version {System.IO.Path.GetFileName(backup)} was loaded and restored as the main file.");
    }

    // <file>.broken-<utc>, never overwriting an earlier copy: a name that is taken gets -2, -3 and so on.
    internal static string KeepBroken(string path, byte[] bytes, DateTimeOffset at)
    {
        string stem = path + ".broken-" + UtcStamp(at);
        for (int attempt = 1; ; attempt++)
        {
            string candidate = attempt == 1 ? stem : stem + "-" + attempt.ToString(CultureInfo.InvariantCulture);
            try
            {
                using var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
                stream.Flush(true);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
        }
    }

    // The previous version is kept as <file>.bak, and the file itself is always either the old or the new version.
    public void Save(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string? directory = System.IO.Path.GetDirectoryName(full);
        if (directory is not null) Directory.CreateDirectory(directory);
        WriteAtomically(full, new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(this, Format)), full + ".bak");
        SourcePath = full;
    }

    // A temporary file in the same directory is written and flushed, then swapped in with File.Replace, or moved into
    // place when there is nothing to replace.
    internal static void WriteAtomically(string path, byte[] bytes, string? backup)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            else File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* a leftover temporary file is harmless */ }
        }
    }

    // Only a port outside 0-65535 and an unusable public_url still refuse to start. Everything else that is wrong is
    // skipped or replaced by its default, and recorded as a warning.
    public void Validate()
    {
        List<string> warnings = [];
        if (Port is < 0 or > 65535) throw new ArgumentException("port must be between 0 and 65535.");
        NormalizeNulls(warnings);
        // An entry the access policy cannot use is dropped here, so AccessPolicy never sees it.
        string[] hosts = AllowHosts.Where(AccessPolicy.IsExactHost).Select(h => h!).ToArray();
        foreach (string? rejected in AllowHosts.Where(h => !AccessPolicy.IsExactHost(h)))
            warnings.Add($"allow_hosts entry '{rejected ?? "null"}' is not an exact hostname without scheme, port or wildcard and is ignored.");
        AllowHosts = hosts;
        string[] origins = AllowOrigins.Where(o => AccessPolicy.NormalizeOrigin(o) is not null).Select(o => o!).ToArray();
        foreach (string? rejected in AllowOrigins.Where(o => AccessPolicy.NormalizeOrigin(o) is null))
            warnings.Add($"allow_origins entry '{rejected ?? "null"}' is not an exact http(s) origin and is ignored.");
        AllowOrigins = origins;
        if (OAuth.AccessTokenHours <= 0)
        {
            warnings.Add($"oauth.access_token_hours is {OAuth.AccessTokenHours}; access tokens last the default 12 hours instead.");
            OAuth.AccessTokenHours = 12;
        }
        if (string.IsNullOrWhiteSpace(StateDir))
        {
            warnings.Add($"state_dir is not set; {DefaultDirectory} is used.");
            StateDir = DefaultDirectory;
        }
        StateDir = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(StateDir));

        List<RootConfig> usable = [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots ?? [])
        {
            if (root is null) { warnings.Add("A null entry in roots is ignored."); continue; }
            root.Id ??= "";
            if (!IsRootId(root.Id)) { warnings.Add($"Root id '{root.Id}' does not match ^[A-Za-z0-9_-]{{1,64}}$; that entry is ignored."); continue; }
            if (!ids.Add(root.Id)) { warnings.Add($"Root id '{root.Id}' appears more than once; only its first entry is used."); continue; }
            if (string.IsNullOrWhiteSpace(root.Path)) { warnings.Add($"Root '{root.Id}' has no path; that entry is ignored."); continue; }
            try { root.Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root.Path)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                warnings.Add($"Root '{root.Id}' path '{root.Path}' is not a usable path ({error.Message}); that entry is ignored.");
                continue;
            }
            if (!Directory.Exists(root.Path))
                warnings.Add($"Root '{root.Id}' path does not exist: {root.Path}. Calls on it fail until the directory exists.");
            if (PathRules.IsInside(root.Path, StateDir) || PathRules.IsInside(StateDir, root.Path))
                warnings.Add($"state_dir {StateDir} overlaps root '{root.Id}' ({root.Path}), so the ledger, backups and artifacts " +
                    "are reachable through that root's file and shell tools. Move state_dir outside every root.");
            usable.Add(root);
        }
        Roots = usable.ToArray();
        if (Roots.Length == 0)
            warnings.Add("No usable root is configured. File, shell and Git tools need one; the desktop tools keep working.");

        string[] unsupported = Shell.Allowed.Where(n => ShellConfig.Canonical(n) is null).Select(n => n ?? "null").ToArray();
        if (unsupported.Length > 0)
            warnings.Add($"shell.allowed names {string.Join(", ", unsupported)} are not supported and are ignored; supported: {string.Join(", ", ShellConfig.Supported)}.");
        if (Shell.Usable.Length == 0)
            warnings.Add("shell.allowed lists no supported interpreter, so command strings are refused; executable plus args still run.");
        else if (Shell.EffectiveDefault != ShellConfig.Canonical(Shell.Default))
            warnings.Add($"shell.default '{Shell.Default}' is not an allowed, supported interpreter; commands without a shell use {Shell.EffectiveDefault}.");

        if (PublicUrl.Length > 0)
        {
            if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                throw new ArgumentException("public_url must be an absolute http(s) URL.");
            PublicUrl = url.GetLeftPart(UriPartial.Authority);
            // The tunnel terminates TLS, so the browser sends Origin: https://<host> while Kestrel sees
            // scheme http. Without this the login form's own POST would be rejected as cross-origin.
            if (!AllowOrigins.Contains(PublicUrl, StringComparer.OrdinalIgnoreCase))
                AllowOrigins = [.. AllowOrigins, PublicUrl];
        }
        List<string> redirects = [];
        foreach (string redirect in OAuth.RedirectUris)
            if (redirect is not null && Uri.TryCreate(redirect, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Fragment.Length == 0)
                redirects.Add(redirect);
            else warnings.Add($"oauth.redirect_uris entry '{redirect}' is not an absolute http(s) URI without a fragment and is ignored.");
        OAuth.RedirectUris = redirects.ToArray();
        // An empty secret matches nothing, so these would silently lock everyone out.
        if (string.IsNullOrEmpty(OAuth.ClientSecret))
            warnings.Add("oauth.client_secret is empty, so no token request can authenticate; create a new configuration with --init or the tray setup.");
        if (string.IsNullOrEmpty(ControlToken))
            warnings.Add("control_token is empty, so the local control API and the tray's status and control items refuse every request.");
        if (PasswordHashProblem(OAuth.PasswordHash) is { } problem)
            warnings.Add($"oauth.password_hash is not usable ({problem}), so no password can sign in; create a new configuration with --init or the tray setup.");
        Warnings = warnings;
    }

    // JSON null for any string, list or section means that property's default, so nothing later meets a null.
    // Each browser mount entry is validated on its own when the mounts start, so one malformed entry cannot stop the
    // server.
    private void NormalizeNulls(List<string> warnings)
    {
        var defaults = new ServerConfig();
        PublicUrl ??= defaults.PublicUrl;
        StateDir ??= defaults.StateDir;
        ControlToken ??= defaults.ControlToken;
        AllowHosts ??= [];
        AllowOrigins ??= [];
        Roots ??= [];
        BrowserMounts ??= [];
        Retention ??= new();
        Shell ??= new();
        Shell.Default ??= new ShellConfig().Default;
        Shell.Allowed ??= new ShellConfig().Allowed;
        Git ??= new();
        Git.Path ??= "";
        OAuth ??= new();
        OAuth.ClientId ??= new OAuthConfig().ClientId;
        OAuth.ClientSecret ??= "";
        OAuth.PasswordHash ??= "";
        OAuth.RedirectUris ??= [];
        Tunnel ??= new();
        Tunnel.Command ??= "";
        Tunnel.Args ??= [];
        if (Tunnel.Args.Any(a => a is null))
        {
            warnings.Add("tunnel.args contains null entries; they are ignored.");
            Tunnel.Args = Tunnel.Args.Where(a => a is not null).ToArray();
        }
    }

    // D12: --no-auth is only for a pure loopback configuration. ProbeAccessPolicy always allows loopback hosts,
    // so an empty allow_hosts list really does mean nothing but 127.0.0.1 and localhost can reach the server.
    // A bearer token must not travel over plain http; only a loopback development run may use one.
    public static string? TransportRefusal(ServerConfig config, bool noAuth) =>
        noAuth || config.PublicUrl.Length == 0 || config.PublicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"public_url is {config.PublicUrl}. Authentication requires https, because the access token would " +
              "otherwise cross the tunnel in clear text. Use an https tunnel URL, or run loopback-only with --no-auth.";

    public static string? NoAuthRefusal(ServerConfig config) => config.AllowHosts.Length == 0 ? null :
        "--no-auth is accepted only for pure loopback development. This configuration allows the public host(s) " +
        string.Join(", ", config.AllowHosts) + "; remove them from allow_hosts or start without --no-auth.";

    public RootConfig Root(string id) => Roots.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new CodexishFault("NOT_FOUND", $"Unknown root_id '{id}'. Call workspace_info for the configured roots.");

    [JsonIgnore] public string Resource => PublicUrl.Length > 0 ? PublicUrl + "/mcp" : "";

    private static bool IsRootId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    // pbkdf2-sha256$<iterations>$<salt base64>$<hash base64>. Secrets are generated, never read from the environment.
    public const int PasswordIterations = 600000;

    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    // Why a stored hash cannot verify any password, or null when it is well formed.
    public static string? PasswordHashProblem(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "empty";
        string[] parts = stored.Split('$');
        if (parts.Length != 4) return "wrong_field_count";
        if (parts[0] != "pbkdf2-sha256") return "unknown_algorithm";
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations) || iterations <= 0) return "bad_iteration_count";
        try
        {
            if (Convert.FromBase64String(parts[2]).Length == 0 || Convert.FromBase64String(parts[3]).Length == 0) return "empty_salt_or_hash";
        }
        catch (FormatException) { return "bad_base64"; }
        return null;
    }

    public static bool VerifyPassword(string password, string stored)
    {
        if (PasswordHashProblem(stored) is not null) return false;
        try
        {
            string[] parts = stored.Split('$');
            int iterations = int.Parse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture);
            byte[] salt = Convert.FromBase64String(parts[2]), expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException or FormatException or OverflowException) { return false; }
    }

    public static string NewSecret(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // The git that --init writes into the file. The server looks git up again on every call, so this is only a first
    // preference.
    public static string FindGit() => GitService.Locate("") ?? (OperatingSystem.IsWindows() ? "git.exe" : "git");

    // --init builds a complete configuration with fresh random secrets; it never reads an existing credential store.
    public static ServerConfig Create(string publicUrl, string password, IEnumerable<(string Id, string Path)> roots,
        IEnumerable<string> redirectUris, int port, string? stateDir)
    {
        var config = new ServerConfig
        {
            PublicUrl = publicUrl,
            Port = port,
            StateDir = stateDir ?? DefaultDirectory,
            Roots = roots.Select(r => new RootConfig { Id = r.Id, Path = r.Path }).ToArray(),
            Git = new GitConfig { Path = FindGit() },
            ControlToken = NewSecret()
        };
        config.OAuth.ClientSecret = NewSecret();
        config.OAuth.PasswordHash = HashPassword(password);
        // Any https callback is accepted at /authorize; the listed ones additionally receive OAuth error redirects.
        // --redirect-uri is needed only for a callback that is not https, which the server logs when it refuses one.
        List<string> redirects = [DefaultRedirectUri, .. redirectUris];
        config.OAuth.RedirectUris = redirects.Distinct(StringComparer.Ordinal).ToArray();
        // The tunnel hostname comes from public_url so the Host allowlist matches the issued OAuth metadata.
        if (Uri.TryCreate(publicUrl, UriKind.Absolute, out var url) &&
            Uri.CheckHostName(url.Host) is UriHostNameType.Dns or UriHostNameType.IPv4 &&
            url.Host is not ("localhost" or "127.0.0.1"))
            config.AllowHosts = [url.Host];
        config.Validate();
        return config;
    }
}
