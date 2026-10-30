using System.Collections;
using System.Text.RegularExpressions;

namespace Codexish.Server;

// D13. Best effort by construction: a small, published pattern set plus the values of environment variables
// whose names look like secrets, applied to text that is about to leave the machine. It is not a secret
// scanner, it does not inspect entropy, and it never claims a clean result.
public static partial class Redaction
{
    [GeneratedRegex(@"ghp_[A-Za-z0-9]{36}")] private static partial Regex GitHubToken();
    [GeneratedRegex(@"sk-[A-Za-z0-9_-]{20,}")] private static partial Regex ApiKey();
    [GeneratedRegex(@"AKIA[0-9A-Z]{16}")] private static partial Regex AwsKeyId();
    [GeneratedRegex(@"Bearer [A-Za-z0-9._-]{20,}")] private static partial Regex BearerToken();
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")] private static partial Regex PrivateKey();
    [GeneratedRegex(@"(?i)(TOKEN|SECRET|PASSWORD|API.?KEY|PRIVATE.?KEY)")] private static partial Regex SecretName();

    private static readonly (string Kind, Func<Regex> Pattern)[] Patterns =
    [
        ("github_token", GitHubToken),
        ("api_key", ApiKey),
        ("aws_access_key_id", AwsKeyId),
        ("bearer_token", BearerToken),
        ("private_key", PrivateKey)
    ];

    // A value shorter than this is too likely to be a common word to replace blindly.
    public const int MinimumEnvironmentValue = 8;

    private static string[] environmentValues = [];
    private static readonly object AdditiveSync = new();
    private static string[] additiveValues = [];

    public static void AddSensitiveValue(string? value)
    {
        if (value is null || value.Length < MinimumEnvironmentValue) return;
        lock (AdditiveSync)
            additiveValues = additiveValues.Append(value).Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length).ToArray();
    }

    public static void AddEnvironmentValues(IEnumerable<KeyValuePair<string, string?>> environment)
    {
        foreach (var (name, value) in environment)
            if (IsSecretName(name)) AddSensitiveValue(value);
    }

    public static void AddHeaderValue(string name, string? value, bool fromEnvironment = false)
    {
        if (fromEnvironment || IsSecretName(name) || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            AddSensitiveValue(value);
    }

    public static void AddUrlQueryValues(Uri endpoint)
    {
        foreach (string part in endpoint.Query.TrimStart('?').Split('&'))
        {
            int equals = part.IndexOf('=');
            if (equals < 0) continue;
            string name = Uri.UnescapeDataString(part[..equals].Replace('+', ' '));
            if (IsSecretName(name)) AddSensitiveValue(Uri.UnescapeDataString(part[(equals + 1)..].Replace('+', ' ')));
        }
    }

    public static string[] DisplayArguments(IEnumerable<string> arguments)
    {
        bool hideNext = false;
        return arguments.Select(argument =>
        {
            if (hideNext) { hideNext = false; return "[REDACTED]"; }
            if (argument.StartsWith('-'))
            {
                int equals = argument.IndexOf('=');
                string name = equals < 0 ? argument : argument[..equals];
                if (IsSecretName(name))
                {
                    if (equals >= 0) return Apply(name).Text + "=[REDACTED]";
                    hideNext = true;
                }
            }
            return Apply(argument).Text;
        }).ToArray();
    }

    public static string? DisplayUrl(string? value)
    {
        if (value is null) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return Apply(uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)).Text + "/…";
        // An unexpanded or malformed URL can still contain credentials in every component: do not echo it.
        return "[unresolved URL]/…";
    }

    // The server snapshots the environment once at startup: a long-running process does not see its parent's
    // environment change, and re-enumerating on every redaction would cost a dictionary copy per call.
    public static void RefreshEnvironment()
    {
        List<string> values = [];
        try
        {
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is not string name || !SecretName().IsMatch(name)) continue;
                if (entry.Value?.ToString() is { } value && value.Length >= MinimumEnvironmentValue) values.Add(value);
            }
        }
        catch (Exception) { /* an unreadable environment block must not disable the pattern set */ }
        // Longest first, so a value that contains a shorter one is replaced whole.
        environmentValues = values.Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length).ToArray();
    }

    public static int EnvironmentValueCount => environmentValues.Length;

    public static bool IsSecretName(string name) => SecretName().IsMatch(name);

    public static (string Text, bool Redacted, string[] Kinds) Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return (text, false, []);
        List<string> kinds = [];
        string[] secrets = environmentValues.Concat(Volatile.Read(ref additiveValues)).Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length).ToArray();
        foreach (string value in secrets)
        {
            if (!text.Contains(value, StringComparison.Ordinal)) continue;
            if (!kinds.Contains("environment_value")) kinds.Add("environment_value");
            text = text.Replace(value, "[REDACTED:environment_value]", StringComparison.Ordinal);
        }
        foreach (var (kind, pattern) in Patterns)
        {
            var regex = pattern();
            if (!regex.IsMatch(text)) continue;
            kinds.Add(kind);
            text = regex.Replace(text, $"[REDACTED:{kind}]");
        }
        return (text, kinds.Count > 0, kinds.ToArray());
    }

    public const string Disclosure =
        "Redaction is best effort over a small published pattern set plus the values of environment variables " +
        "whose names match TOKEN, SECRET, PASSWORD, API_KEY or PRIVATE_KEY; it is not a guarantee that no secret " +
        "is present. Environment variables are never returned. Artifacts keep the raw local bytes on disk and " +
        "are redacted on read.";
}
