using System.Text.RegularExpressions;

namespace Codexish.Server;

// D13. Best effort by construction: a small, published pattern set over text that is about to leave the
// machine. It is not a secret scanner, it does not inspect entropy, and it never claims a clean result.
public static partial class Redaction
{
    [GeneratedRegex(@"ghp_[A-Za-z0-9]{36}")] private static partial Regex GitHubToken();
    [GeneratedRegex(@"sk-[A-Za-z0-9_-]{20,}")] private static partial Regex ApiKey();
    [GeneratedRegex(@"AKIA[0-9A-Z]{16}")] private static partial Regex AwsKeyId();
    [GeneratedRegex(@"Bearer [A-Za-z0-9._-]{20,}")] private static partial Regex BearerToken();
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")] private static partial Regex PrivateKey();

    private static readonly (string Kind, Func<Regex> Pattern)[] Patterns =
    [
        ("github_token", GitHubToken),
        ("api_key", ApiKey),
        ("aws_access_key_id", AwsKeyId),
        ("bearer_token", BearerToken),
        ("private_key", PrivateKey)
    ];

    public static (string Text, bool Redacted, string[] Kinds) Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return (text, false, []);
        List<string> kinds = [];
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
        "Redaction is best effort over a small published pattern set; it is not a guarantee that no secret is present. " +
        "Environment variables are never returned. Artifacts keep the raw local bytes on disk and are redacted on read.";
}
