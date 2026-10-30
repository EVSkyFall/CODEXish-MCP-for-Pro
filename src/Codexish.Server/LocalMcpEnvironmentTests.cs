using System.Diagnostics;
using System.Text.Json;
using static Codexish.Server.SelfTest;

namespace Codexish.Server;

internal static class LocalMcpEnvironmentTests
{
    public static void Run(string temp)
    {
        var source = new Dictionary<string, string?>(ChildEnvironment.Comparer) { ["A"] = "value", ["EMPTY"] = "" };
        var warnings = new List<string>();
        string Expand(string value) => ChildEnvironment.Expand(value, source, "test", warnings.Add, temp);
        Check(Expand("${A}") == "value", "MCP environment expands a variable");
        Check(Expand("${MISSING:-default}") == "default" && Expand("${EMPTY:-default}") == "default", "MCP unset and empty variables use defaults");
        Check(Expand("${MISSING:-${EMPTY:-${A}}}") == "value", "MCP variable defaults expand recursively");
        Check(Expand("$A $$ literal ${unterminated") == "$A $$ literal ${unterminated", "MCP leaves literal dollars and unmatched expansion intact");
        Check(Expand("a${MISSING}b") == "ab" && warnings.Any(w => w.Contains("test") && w.Contains("MISSING")), "MCP undefined variable expands empty with field warning");
        Check(Expand("${CLAUDE_PROJECT_DIR}") == temp, "MCP project directory fallback");
        source["CLAUDE_PROJECT_DIR"] = "override";
        Check(Expand("${CLAUDE_PROJECT_DIR}") == "override", "MCP explicit project directory wins");
        Check(Expand("${a:-different}") == (OperatingSystem.IsWindows() ? "value" : "different"), "MCP platform variable name comparison");
        var before = new Dictionary<string, string?> { ["PATH"] = "old", ["REMOVED"] = "old", ["TEMP"] = "registry-temp" };
        var after = new Dictionary<string, string?> { ["path"] = "new", ["ADDED"] = "new-value", ["TEMP"] = "registry-temp" };
        var process = new Dictionary<string, string?> { ["PATH"] = "old", ["REMOVED"] = "old", ["TEMP"] = "process-temp", ["ONLY_PROCESS"] = "kept" };
        var overlay = ChildEnvironment.Overlay(process, before, after, StringComparer.OrdinalIgnoreCase);
        Check(overlay["PATH"] == "new" && overlay["ADDED"] == "new-value" && !overlay.ContainsKey("REMOVED"), "MCP fresh environment applies added changed removed values case-insensitively");
        Check(overlay["TEMP"] == "process-temp" && overlay["ONLY_PROCESS"] == "kept", "MCP fresh environment preserves deliberate process-only overrides");
        var baseline = new UserEnvironmentBaseline();
        int reads = 0;
        Dictionary<string, string?> Reader() { reads++; return reads == 1 ? before : after; }
        baseline.Initialize(Reader); baseline.Initialize(Reader);
        var fresh = baseline.Capture(process, Reader);
        Check(reads == 2 && fresh["PATH"] == "new", "MCP B0 survives runtime-style reinitialization and B1 is read per child");
        var retryBaseline = new UserEnvironmentBaseline(); int retries = 0;
        Dictionary<string, string?> RetryReader() { if (++retries == 1) throw new IOException("synthetic failure"); return before; }
        try { retryBaseline.Initialize(RetryReader); } catch (IOException) { }
        retryBaseline.Capture(process, RetryReader);
        Check(retries == 3, "MCP failed B0 read is retried at next child rather than latched");
        source["PATH"] = "synthetic-path"; source["CUSTOM_SECRET"] = "local-mcp-synthetic-secret"; source["HTTP_PROXY"] = "synthetic-proxy";
        var automatic = ChildEnvironment.MountEnvironment(source);
        Check(!automatic.ContainsKey("CUSTOM_SECRET") && !automatic.ContainsKey("HTTP_PROXY") && automatic["PATH"] == "synthetic-path", "MCP automatic allowlist excludes secrets and proxies");
        var explicitEnv = ChildEnvironment.MountEnvironment(source, ["CUSTOM_SECRET", "HTTP_PROXY", "UNKNOWN"],
            new Dictionary<string, string?> { ["EMPTY"] = "", ["NULL"] = null, ["SET"] = "set" }, warnings.Add);
        Check(explicitEnv["CUSTOM_SECRET"] == source["CUSTOM_SECRET"] && explicitEnv["HTTP_PROXY"] == "synthetic-proxy", "MCP explicitly named secret and proxy variables pass through");
        Check(explicitEnv["EMPTY"] == "" && explicitEnv["SET"] == "set" && !explicitEnv.ContainsKey("NULL") && warnings.Any(w => w.Contains("UNKNOWN")), "MCP explicit empty null and undefined environment rules");
        Redaction.RefreshEnvironment();
        Check(Redaction.Apply("local-mcp-synthetic-secret").Redacted, "MCP additive redaction survives RefreshEnvironment");
        Redaction.AddHeaderValue("Authorization", "local-mcp-header-value");
        Redaction.AddHeaderValue("Accept", "application/json");
        Redaction.AddEnvironmentValues(new Dictionary<string, string?> { ["TOKENIZERS_PARALLELISM"] = "false", ["ORDINARY"] = "ordinary-value" });
        Redaction.AddUrlQueryValues(new Uri("https://fixture.invalid/?v=1&type=application/json&token=query-secret-long"));
        Check(Redaction.Apply("local-mcp-header-value").Redacted && Redaction.Apply("query-secret-long").Redacted, "MCP sensitive header and named query values are registered for redaction");
        Check(Redaction.Apply("false 1 8080 application/json ordinary-value").Text == "false 1 8080 application/json ordinary-value", "MCP short and ordinary environment header and query values remain unchanged");
        Check(Redaction.DisplayArguments(["--api-key", "ordinary-key-value", "--token=ordinary-token-value", "--name", "safe"]).SequenceEqual(["--api-key", "[REDACTED]", "--token=[REDACTED]", "--name", "safe"]), "MCP argument display masks secret-pattern flag values");
        Check(Redaction.DisplayUrl("https://user:password@example.com:4321/private-token/path?q=secret#fragment") == "https://example.com:4321/…", "MCP URL display reveals authority only, never path credentials or query");
        string dir = Path.Combine(temp, "mcp-command-env"); Directory.CreateDirectory(dir);
        string first = Path.Combine(dir, "first"), second = Path.Combine(dir, "second"); Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        string suffix = OperatingSystem.IsWindows() ? ".cmd" : "";
        string firstTool = Path.Combine(first, "fixture" + suffix), secondTool = Path.Combine(second, "fixture" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.WriteAllText(firstTool, "fixture"); File.WriteAllText(secondTool, "fixture");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(firstTool, UnixFileMode.UserRead | UnixFileMode.UserExecute); File.SetUnixFileMode(secondTool, UnixFileMode.UserRead | UnixFileMode.UserExecute); }
        var pathEnv = new Dictionary<string, string?>(ChildEnvironment.Comparer) { ["PATH"] = first + Path.PathSeparator + second, ["PATHEXT"] = ".EXE;.CMD" };
        Check(string.Equals(ChildEnvironment.ResolveCommand("fixture", pathEnv, dir), firstTool, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "MCP command resolution is PATH directory-major");
        Check(ChildEnvironment.ResolveCommand(Path.Combine("first", "fixture" + suffix), pathEnv, dir) == firstTool, "MCP relative command resolves against cwd");
        Check(ChildEnvironment.ResolveCommand("does-not-exist", pathEnv, dir) == "does-not-exist", "MCP unresolved command is started unchanged");
        string batch = ChildEnvironment.BatchArguments("C:\\path with space\\shim.cmd", ["a b", "^&|<>()!", "%VAR%", "x\"y", "slash\\"]);
        Check(batch.StartsWith("/e:ON /v:OFF /d /c \"\"C:") && batch.Contains("%%cd:~,%VAR%%cd:~,%") && batch.Contains("\"x\"\"y\"") && batch.Contains("\"slash\\\\\""), "MCP batch quoting matches Rust escaping with outer quotes");
        foreach (string invalid in new[] { "a\rb", "a\nb", "a\0b" })
        {
            bool rejected = false;
            try { ChildEnvironment.BatchArguments("test.cmd", [invalid]); } catch (CodexishFault fault) { rejected = fault.Code == "INVALID_ARGUMENT"; }
            Check(rejected, "MCP batch rejects CR LF or NUL");
        }
        if (OperatingSystem.IsWindows())
        {
            Check(ChildEnvironment.ReadWindowsEnvironment().ContainsKey("PATH"), "MCP real current-user environment contains PATH");
            File.WriteAllText(Path.Combine(first, "ignored.ps1"), "fixture");
            pathEnv["PATHEXT"] = ".PS1;.CMD";
            Check(ChildEnvironment.ResolveCommand("ignored", pathEnv, dir) == "ignored", "MCP PATHEXT ignores unsupported extensions");
            File.WriteAllText(Path.Combine(first, "fixture"), "npm shell script");
            Check(string.Equals(ChildEnvironment.ResolveCommand(Path.Combine(first, "fixture"), pathEnv, dir), firstTool, StringComparison.OrdinalIgnoreCase), "MCP explicit extensionless Windows path prefers cmd over npm shell script");
            string originalPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            try
            {
                File.WriteAllText(Path.Combine(first, "cmd.exe"), "fake"); File.WriteAllText(Path.Combine(first, "powershell.exe"), "fake");
                Environment.SetEnvironmentVariable("PATH", first + ";" + originalPath);
                Check(ProcessSupervisor.ResolveShell("cmd") == Path.Combine(Environment.SystemDirectory, "cmd.exe") && ProcessSupervisor.ResolveShell("powershell") == Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "MCP cmd and Windows PowerShell prefer system locations over child PATH");
            }
            finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
        }
        else Console.WriteLine("SKIP MCP Windows current-user environment and PATHEXT checks: not Windows");
        var fingerprintConfig = new ServerConfig { SourcePath = Path.Combine(temp, "codexish.json") };
        fingerprintConfig.McpServers = JsonSerializer.SerializeToElement(new { fingerprint = new { command = "missing-fixture", args = new[] { "a" }, env = new { EXPLICIT = "one" }, unknown = "before" } });
        var definition = McpConfiguration.ParseTopLevel(fingerprintConfig).Single();
        string fingerprint = McpEffectiveConfiguration.Fingerprint(fingerprintConfig, definition);
        fingerprintConfig.McpServers = JsonSerializer.SerializeToElement(new { fingerprint = new { command = "missing-fixture", args = new[] { "a" }, env = new { EXPLICIT = "one" }, unknown = new { future_setting = true } } });
        definition = McpConfiguration.ParseTopLevel(fingerprintConfig).Single();
        Check(fingerprint == McpEffectiveConfiguration.Fingerprint(fingerprintConfig, definition) && fingerprint.Length == 64, "MCP effective fingerprint ignores actually parsed unknown raw fields and retains only SHA256");
        definition.Env["EXPLICIT"] = "two";
        Check(fingerprint != McpEffectiveConfiguration.Fingerprint(fingerprintConfig, definition), "MCP effective fingerprint detects explicit environment changes");
        definition.Transport = "http"; definition.Url = "http://127.0.0.1/fixture"; definition.Headers["X-Fixture"] = "one";
        fingerprint = McpEffectiveConfiguration.Fingerprint(fingerprintConfig, definition);
        definition.Headers["X-Fixture"] = "two";
        Check(fingerprint != McpEffectiveConfiguration.Fingerprint(fingerprintConfig, definition), "MCP effective fingerprint detects expanded HTTP headers");
        const string processName = "CODEXISH_LOCAL_MCP_PROCESS_ONLY_TEST";
        string? old = Environment.GetEnvironmentVariable(processName);
        try
        {
            Environment.SetEnvironmentVariable(processName, "process-only");
            Check(ChildEnvironment.Capture()[processName] == "process-only", "MCP child base retains a process-only variable");
        }
        finally { Environment.SetEnvironmentVariable(processName, old); }
    }
}
