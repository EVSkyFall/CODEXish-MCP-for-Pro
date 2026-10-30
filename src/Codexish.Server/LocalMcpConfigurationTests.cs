using System.Text.Json;

namespace Codexish.Server;

public static class LocalMcpConfigurationTests
{
    public static void Run()
    {
        void Check(bool ok, string label) => SelfTest.Check(ok, "local MCP configuration: " + label);
        static ServerConfig Config(string json) => JsonSerializer.Deserialize<ServerConfig>(json)!;
        static McpDefinition One(string json) => McpConfiguration.ParseTopLevel(Config("{\"mcp_servers\":{\"test\":" + json + "}}"))[0];
        var config = Config("""{"mcp_servers":{"a":{"command":"first"},"A":{"command":"second"},"a":{"command":"third"}},"mcpServers":{"a":{"command":"alias"},"b":{"command":"other"}}} """);
        var entries = McpConfiguration.ParseTopLevel(config);
        Check(entries.Length == 4 && entries[0].Command == "first" && entries[1].Error == "duplicate of an earlier entry" && entries[2].Error == "duplicate of an earlier entry" && entries[3].Name == "b" && config.Warnings.Any(w => w.Contains("a")), "primary wins alias; exact and case duplicates remain isolated and reported");
        Check(McpConfiguration.ParseTopLevel(Config("""{"mcp_servers":7,"mcpServers":{"a":{"command":"ok"}}}""")).Single().Command == "ok", "non-object primary does not hide valid alias");
        Check(One("""{"command":"x","url":"http://localhost","type":"stdio"}""").Transport == "stdio" &&
              One("""{"command":"x","url":"http://localhost","type":"http"}""").Transport == "http" &&
              One("""{"command":"x","url":"http://localhost","type":"sse"}""").Transport == "sse" &&
              One("""{"command":"x","type":"http"}""").Transport == "stdio" &&
              One("""{"command":"x","type":"future"}""").Warnings.Count > 0, "explicit type and inference follow usable fields");
        Check(One("""{"command":"x","disabled":"TrUe"}""").State == "disabled" && One("""{"command":"x","enabled":"FALSE"}""").State == "disabled" &&
              One("""{"command":"x","enabled":22}""").State == "not_started", "boolean strings honored; malformed enabled ignored");
        var coerced = One("""{"command":42,"args":"x","env":{"PORT":8080,"FLAG":true,"EMPTY":"","SKIP":null},"future":{"nested":[1]}}""");
        Check(coerced.Command == "42" && coerced.Args.SequenceEqual(["x"]) && coerced.Env["PORT"] == "8080" && coerced.Env["FLAG"] == "true" && coerced.Env["EMPTY"] == "" && coerced.Env["SKIP"] is null && coerced.State == "not_started", "scalar coercion, null and empty env values, unknown fields");
        Check(One("""{"command":"x","env":{"bad":{}}}""").State == "invalid_config" && One("""{"command":"x","args":[{}]}""").Error!.Contains("args"), "nested scalar values invalidate just their definition");
        var ignored = One("""{"url":"http://localhost","env":{"bad":{}},"env_vars":{},"headers":{"x":2}}""");
        Check(ignored.State == "not_started" && ignored.Headers["x"] == "2" && ignored.Warnings.Any(w => w.Contains("env_vars")), "irrelevant transport fields ignored, even if malformed");
        var headers = One("""{"url":"http://localhost","http_headers":{"X":"old","y":true},"headers":{"x":"new"},"env_http_headers":{"Authorization":"MY_TOKEN"}}""");
        Check(headers.Headers["X"] == "new" && headers.Headers["y"] == "true" && headers.EnvHttpHeaders["authorization"] == "MY_TOKEN", "header alias merge uses case-insensitive primary precedence");
        Check(One("""{"url":"file:///tmp/x"}""").State == "invalid_config" && One("""{"url":"${ENDPOINT}"}""").State == "not_started" &&
              One("""{"command":"x","root_id":"missing"}""").Error == "root 'missing' is not a configured root of the running server; roots take effect when CODEXish restarts", "URL validation waits for expansion and unknown root explains restart");
        Check(One("""{"command":"x.cmd","args":["bad\nline"]}""").State == "invalid_config", "unrepresentable batch arguments isolated");
        string directory = Path.Combine(Path.GetTempPath(), "codexish-mcp-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var root = new RootConfig { Id = "root", Path = directory, Shell = false };
            string projectPath = Path.Combine(directory, ".mcp.json");
            Check(McpConfiguration.ReadProject(root).State == "absent", "missing project file is absent");
            File.WriteAllText(projectPath, """{/* comment */ "mcpServers":{"test":{"command":"x","root_id":{}},},} """);
            var project = McpConfiguration.ReadProject(root);
            Check(project.State == "read" && project.Definitions.Single().State == "needs_shell_grant" && project.Definitions[0].Identity == "root/test" && project.Definitions[0].Warnings.Count == 1, "project comments/trailing comma, bound identity, ignored root_id and shell grant");
            var topConfig = Config("""{"mcp_servers":{"test":{"command":"x","root_id":"root"}}}"""); topConfig.Roots = [root];
            var top = McpConfiguration.ParseTopLevel(topConfig);
            Check(McpConfiguration.ApplyOverrides(top, project.Definitions)[1].State == "overridden" &&
                  McpConfiguration.ApplyOverrides([], project.Definitions)[0].State == "needs_shell_grant", "override removal restores project's original disabled/grant state");
            var literal = McpConfiguration.ParseTopLevel(Config("""{"mcp_servers":{"ROOT/TEST":{"command":"x"}}}"""));
            var literalOverride = McpConfiguration.ApplyOverrides(literal, project.Definitions);
            Check(literalOverride[1].State == "overridden" && literalOverride[1].OverriddenBy == "ROOT/TEST" &&
                  literalOverride[0].RootId is null && literalOverride[0].State == "not_started" &&
                  McpConfiguration.ApplyOverrides([], project.Definitions)[0].State == "needs_shell_grant",
                  "literal root/name top-level identity overrides case-insensitively without root binding, and removal restores project state");
            File.WriteAllText(projectPath, "{ incomplete");
            var incomplete = McpConfiguration.ReadProject(root, project);
            Check(incomplete.State == "unparseable" && incomplete.KeptPrevious && ReferenceEquals(incomplete.Definitions, project.Definitions), "half-written project retains previous definitions");
            File.Delete(projectPath); Directory.CreateDirectory(projectPath);
            Check(McpConfiguration.ReadProject(root, project).State == "unreadable", "non-file project location is unreadable rather than absent");
            Directory.Delete(projectPath);
            string configPath = Path.Combine(directory, "codexish.json");
            var safe = new ServerConfig { StateDir = Path.Combine(directory, "state"), Roots = [root] }; safe.Save(configPath);
            File.WriteAllText(configPath, "{\"state_dir\":" + JsonSerializer.Serialize(safe.StateDir) + ",\"mcp_servers\":{\"bad\":{\"env\":{\"nested\":[]}},\"ok\":{\"command\":\"works\"}}}");
            string original = File.ReadAllText(configPath);
            var loaded = ServerConfig.Load(configPath);
            Check(File.ReadAllText(configPath) == original && Directory.GetFiles(directory, "*.broken-*").Length == 0 && McpConfiguration.ParseTopLevel(loaded)[1].Command == "works", "nested server type error never invokes .broken/.bak startup recovery");
            using var first = JsonDocument.Parse("""{"b":1,"a":2,"a":3}"""); using var second = JsonDocument.Parse("""{"a":2,"a":3,"b":1}""");
            Check(McpConfiguration.Canonical(first.RootElement) == McpConfiguration.Canonical(second.RootElement), "canonical definitions preserve duplicate keys and ignore property order");
        }
        finally { TestCleanup.RemoveDirectory(directory); }
    }
}
