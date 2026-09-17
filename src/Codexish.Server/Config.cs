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
    [JsonPropertyName("default")] public string Default { get; set; } = "pwsh";
    [JsonPropertyName("allowed")] public string[] Allowed { get; set; } = ["pwsh", "cmd"];
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
    [JsonPropertyName("refresh_token_days")] public int RefreshTokenDays { get; set; } = 30;
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

    public const string DefaultRedirectUri = "https://chatgpt.com/connector_platform_oauth_redirect";
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public static string DefaultDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codexish");
    public static string DefaultPath => System.IO.Path.Combine(DefaultDirectory, "codexish.json");

    public static ServerConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"No configuration at {path}. Run --init --password <pw> --public-url <url> [--root id=path] first.");
        var config = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path))
            ?? throw new ArgumentException($"Configuration at {path} is not a JSON object.");
        config.Validate();
        return config;
    }

    public void Save(string path)
    {
        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Format), new UTF8Encoding(false));
    }

    public void Validate()
    {
        if (Port is < 0 or > 65535) throw new ArgumentException("port must be between 0 and 65535.");
        if (OAuth.AccessTokenHours <= 0 || OAuth.RefreshTokenDays <= 0)
            throw new ArgumentException("access_token_hours and refresh_token_days must be positive.");
        if (string.IsNullOrWhiteSpace(StateDir)) throw new ArgumentException("state_dir is required.");
        StateDir = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(StateDir));
        if (Roots.Length == 0) throw new ArgumentException("Configure at least one root with --root id=path.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots)
        {
            if (!IsRootId(root.Id)) throw new ArgumentException($"Root id '{root.Id}' must match ^[A-Za-z0-9_-]{{1,64}}$.");
            if (!ids.Add(root.Id)) throw new ArgumentException($"Duplicate root id '{root.Id}'.");
            if (string.IsNullOrWhiteSpace(root.Path)) throw new ArgumentException($"Root '{root.Id}' has no path.");
            root.Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root.Path));
            if (!Directory.Exists(root.Path)) throw new ArgumentException($"Root '{root.Id}' path does not exist: {root.Path}");
            // The ledger, backups and artifacts must not be reachable through file tools or a shell grant.
            if (Contains(root.Path, StateDir) || Contains(StateDir, root.Path) ||
                StateDir.Equals(root.Path, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"state_dir must lie outside every root; '{root.Id}' overlaps {StateDir}.");
        }
        // Two ids on one directory would give the same files two independent FIFO queues, so overlapping
        // roots are refused rather than silently serialized apart.
        for (int outer = 0; outer < Roots.Length; outer++)
            for (int inner = outer + 1; inner < Roots.Length; inner++)
            {
                string a = Roots[outer].Path, b = Roots[inner].Path;
                if (a.Equals(b, StringComparison.OrdinalIgnoreCase) || Contains(a, b) || Contains(b, a))
                    throw new ArgumentException(
                        $"Roots '{Roots[outer].Id}' and '{Roots[inner].Id}' are the same directory or nested; give one root per directory tree.");
            }
        if (Shell.Allowed.Length == 0) throw new ArgumentException("shell.allowed must list at least one interpreter.");
        foreach (string name in Shell.Allowed)
            if (name is not ("pwsh" or "cmd")) throw new ArgumentException($"shell.allowed supports pwsh and cmd only; found '{name}'.");
        if (!Shell.Allowed.Contains(Shell.Default, StringComparer.Ordinal))
            throw new ArgumentException("shell.default must appear in shell.allowed.");
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
        foreach (string redirect in OAuth.RedirectUris)
            if (!Uri.TryCreate(redirect, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Fragment.Length != 0)
                throw new ArgumentException($"oauth.redirect_uris entry '{redirect}' must be an absolute http(s) URI without a fragment.");
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

    private static bool Contains(string outer, string inner) =>
        inner.StartsWith(outer + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // pbkdf2-sha256$<iterations>$<salt base64>$<hash base64>. Secrets are generated, never read from the environment.
    public const int PasswordIterations = 600000;

    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        string[] parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out int iterations) || iterations <= 0) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[2]); expected = Convert.FromBase64String(parts[3]); }
        catch (FormatException) { return false; }
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string NewSecret(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string FindGit()
    {
        List<string> candidates = [];
        if (Environment.GetEnvironmentVariable("ProgramFiles") is { Length: > 0 } programFiles)
            candidates.Add(System.IO.Path.Combine(programFiles, "Git", "cmd", "git.exe"));
        candidates.Add(@"C:\Program Files\Git\cmd\git.exe");
        candidates.Add("/usr/bin/git");
        foreach (string candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        return OperatingSystem.IsWindows() ? "git.exe" : "git";
    }

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
        // The documented ChatGPT callback is always accepted; --redirect-uri adds the connector's own value,
        // which the server logs whenever it rejects one.
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
