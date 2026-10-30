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
        List<string> parseWarnings = [];
        var entries = McpConfiguration.ParseTopLevel(config, parseWarnings);
        Check(entries.Length == 4 && entries[0].Command == "first" && entries[1].Error == "duplicate of an earlier entry" && entries[2].Error == "duplicate of an earlier entry" && entries[3].Name == "b" && parseWarnings.SequenceEqual(["mcpServers server 'a' is also in mcp_servers; mcp_servers wins."]) && config.Warnings.Count == 0, "primary wins alias; exact and case duplicates remain isolated and reported");
        Check(McpConfiguration.ParseTopLevel(Config("""{"mcp_servers":7,"mcpServers":{"a":{"command":"ok"}}}""")).Single().Command == "ok", "non-object primary does not hide valid alias");
        Check(One("""{"command":"x","url":"http://localhost","type":"stdio"}""").Transport == "stdio" &&
              One("""{"command":"x","url":"http://localhost","type":"http"}""").Transport == "http" &&
              One("""{"command":"x","url":"http://localhost","type":"sse"}""").Transport == "sse" &&
              One("""{"command":"x","type":"http"}""").Transport == "stdio" &&
              One("""{"command":"x","type":"future"}""").Warnings.Count > 0, "explicit type and inference follow usable fields");
        Check(One("""{"command":"x","disabled":"TrUe"}""").State == "disabled" && One("""{"command":"x","enabled":"FALSE"}""").State == "disabled" &&
              One("""{"command":"x","enabled":22}""").State == "not_started", "boolean strings honored; malformed enabled ignored");
        Check(One("""{"command":"x","disabled":null,"enabled":null}""").State == "not_started" &&
              One("""{"command":"x","disabled":null,"enabled":null}""").Warnings.Count == 0, "null enable flags keep the default without a warning");
        var unicode = McpConfiguration.ParseTopLevel(Config("""{"mcp_servers":{"\uD800":{"command":"x"},"bad":{"command":"\uD800"},"ok":{"command":"x"}}}"""));
        Check(unicode.Length == 3 && unicode[0].State == "invalid_config" && unicode[1].State == "invalid_config" && unicode[2].Command == "x" &&
              unicode.Take(2).All(d => !d.Error!.EndsWith('.') && !d.Error.Contains("InvalidOperationException")), "lone-surrogate name and value are isolated with clause reasons; next entry remains usable");
        using (var tolerantBrowsers = McpConfiguration.ParseDocument(System.Text.Encoding.UTF8.GetBytes("""[{"id":"ok", /* entry comment */ "command":"x","args":["a",],},]""")))
        {
            var browser = ServerConfig.ParseBrowserMounts(tolerantBrowsers.RootElement).Single();
            Check(browser.ParseError is null && browser.Id == "ok" && browser.Command == "x" && browser.Args.SequenceEqual(["a"]),
                "browser entries use the same comment and trailing-comma tolerance as the containing file");
        }
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
            var tolerant = "{/* startup comment */ \"state_dir\":" + JsonSerializer.Serialize(safe.StateDir) + ",\"mcp_servers\":{\"ok\":{\"command\":\"works\",},},}";
            File.WriteAllBytes(configPath, [0xef, 0xbb, 0xbf, .. System.Text.Encoding.UTF8.GetBytes(tolerant)]);
            byte[] tolerantBytes = File.ReadAllBytes(configPath);
            var tolerantLoaded = ServerConfig.Load(configPath);
            Check(McpConfiguration.ParseTopLevel(tolerantLoaded).Single().Command == "works" && File.ReadAllBytes(configPath).SequenceEqual(tolerantBytes) &&
                  Directory.GetFiles(directory, "*.broken-*").Length == 0, "startup BOM comments and trailing commas survive without recovery or rewriting");
            File.WriteAllBytes(projectPath, [0xef, 0xbb, 0xbf, .. System.Text.Encoding.UTF8.GetBytes("""{/* comment */ "mcpServers":{"bad":{"command":"\uD800"},"ok":{"command":"x"},},}""")]);
            var unicodeProject = McpConfiguration.ReadProject(root);
            Check(unicodeProject.State == "read" && unicodeProject.Definitions[0].State == "invalid_config" && unicodeProject.Definitions[1].Command == "x", "project BOM and Unicode failure are contained to one definition");
            File.WriteAllText(configPath, """{"browser_mounts":[{"id":"bad","command":"\uD800"},{"id":"ok","command":"x"}],"mcp_servers":{"bad":{"command":"\uD800"},"ok":{"command":"works"}}}""");
            var unicodeLoaded = ServerConfig.Load(configPath);
            Check(unicodeLoaded.BrowserMounts.Length == 2 && unicodeLoaded.BrowserMounts[0].ParseError is not null && unicodeLoaded.BrowserMounts[1].Id == "ok" &&
                  McpConfiguration.ParseTopLevel(unicodeLoaded)[0].State == "invalid_config" && Directory.GetFiles(directory, "*.broken-*").Length == 0,
                  "startup isolates malformed browser and MCP entries without backup recovery");
            var rootsCopy = unicodeLoaded.CopyForRootEdit([root]);
            rootsCopy.Validate();
            ServerConfig.SaveRoots(configPath, rootsCopy.Roots);
            var rootsReload = ServerConfig.Load(configPath);
            Check(rootsReload.Roots.Single().Id == root.Id && McpConfiguration.ParseTopLevel(rootsReload)[0].State == "invalid_config" &&
                  rootsReload.BrowserMounts[0].ParseError is not null, "roots edit preserves malformed optional definitions without decoding or blocking the save");
            using var first = JsonDocument.Parse("""{"b":1,"a":2,"a":3}"""); using var second = JsonDocument.Parse("""{"a":2,"a":3,"b":1}""");
            Check(McpConfiguration.Canonical(first.RootElement) == McpConfiguration.Canonical(second.RootElement), "canonical definitions preserve duplicate keys and ignore property order");
        }
        finally { TestCleanup.RemoveDirectory(directory); }
    }
}
