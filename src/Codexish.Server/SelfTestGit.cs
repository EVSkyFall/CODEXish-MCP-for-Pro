using System.Diagnostics;
using System.Text;
using static Codexish.Server.SelfTest;

namespace Codexish.Server;

// D8 proof: the repository is booby-trapped with an fsmonitor program, an external diff, a textconv filter,
// a pager, an editor and hooks that all write marker files. A control run without the fixed options proves the
// traps fire; the tools then run and must leave the marker directory empty.
internal static class GitTests
{
    private static string markers = "";
    private static string emptyGlobalConfig = "";
    // Test-harness stall diagnostic only: product git calls have no timeout.
    private static TimeSpan stallAfter = TimeSpan.FromSeconds(120);

    public static async Task Run(CodexishRuntime runtime, CodexishTools tools, string root)
    {
        string git = runtime.Config.Git.Path;
        if (!File.Exists(git) && Path.IsPathRooted(git))
        {
            Skip("git checks: no git binary at the configured path");
            return;
        }
        string temp = Path.GetDirectoryName(root)!;
        markers = Path.Combine(temp, "markers");
        string traps = Path.Combine(temp, "traps");
        Directory.CreateDirectory(markers);
        Directory.CreateDirectory(traps);
        emptyGlobalConfig = Path.Combine(temp, "empty-global.gitconfig");
        File.WriteAllText(emptyGlobalConfig, "");

        if (Setup(git, root, "init", "-b", "main") != 0)
        {
            Skip("git checks: git init failed in this environment");
            return;
        }
        Setup(git, root, "config", "user.email", "selftest@example.invalid");
        Setup(git, root, "config", "user.name", "CODEXish self test");
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "first\nsecond\n", new UTF8Encoding(false));
        Setup(git, root, "add", "tracked.txt");
        Setup(git, root, "commit", "-m", "initial commit");
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "first\nCHANGED\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "staged.txt"), "staged content\n", new UTF8Encoding(false));
        Setup(git, root, "add", "staged.txt");
        File.WriteAllText(Path.Combine(root, "untracked.txt"), "untracked content\n", new UTF8Encoding(false));

        // Traps are installed only after the setup writes, so setup does not trip them.
        foreach (string name in new[] { "fsmonitor", "external", "textconv", "pager", "editor", "clean", "smudge" })
            Trap(Path.Combine(traps, name + ".sh"), name);
        string hooks = Path.Combine(root, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        foreach (string hook in new[] { "post-index-change", "pre-auto-gc", "post-checkout", "fsmonitor-watchman", "pre-commit" })
            Trap(Path.Combine(hooks, hook), "hook-" + hook);
        string Posix(string path) => path.Replace('\\', '/');
        Setup(git, root, "config", "core.fsmonitor", Posix(Path.Combine(traps, "fsmonitor.sh")));
        Setup(git, root, "config", "diff.external", Posix(Path.Combine(traps, "external.sh")));
        Setup(git, root, "config", "diff.marker.textconv", Posix(Path.Combine(traps, "textconv.sh")));
        Setup(git, root, "config", "core.pager", Posix(Path.Combine(traps, "pager.sh")));
        Setup(git, root, "config", "core.editor", Posix(Path.Combine(traps, "editor.sh")));
        // A clean filter also runs during status and diff, which is why the tools read the declared filter
        // names and disable them before the real query.
        Setup(git, root, "config", "filter.marker.clean", Posix(Path.Combine(traps, "clean.sh")));
        Setup(git, root, "config", "filter.marker.smudge", Posix(Path.Combine(traps, "smudge.sh")));
        Setup(git, root, "config", "filter.marker.required", "true");
        File.WriteAllText(Path.Combine(root, ".gitattributes"), "*.txt diff=marker filter=marker\n", new UTF8Encoding(false));

        // Control: the same repository without the fixed options must actually execute the trap, so these runs
        // keep the repository's trapped keys and optional locks.
        Control(git, root, "status", "--porcelain=v2");
        Control(git, root, "diff");
        var tripped = Directory.GetFiles(markers).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Check(tripped.Length > 0, $"the repository traps really execute when git runs unprotected ({string.Join(", ", tripped)})");
        Check(tripped.Contains("clean.marker"),
            "the clean filter declared by .gitattributes really runs during an unprotected status or diff");
        foreach (string file in Directory.GetFiles(markers)) File.Delete(file);

        var status = await tools.GitStatus("proj");
        var diff = await tools.GitDiff("proj", null, null, false);
        var staged = await tools.GitDiff("proj", null, null, true);
        var head = await tools.GitDiff("proj", "HEAD", "tracked.txt", false);
        var log = await tools.GitLog("proj", 10, null);
        Check(Directory.GetFiles(markers).Length == 0,
            "git_status, git_diff and git_log executed no hook, fsmonitor, external diff, textconv, clean filter, pager or editor");

        var entries = Data(status).GetProperty("entries").EnumerateArray().ToArray();
        Check(Data(status).GetProperty("branch").GetString() == "main" && Data(status).GetProperty("head").GetString()!.Length == 40,
            "git_status reports the branch and HEAD object id");
        Check(entries.Any(e => e.GetProperty("path").GetString() == "untracked.txt" && e.GetProperty("kind").GetString() == "untracked"),
            "git_status reports untracked files");
        Check(entries.Any(e => e.GetProperty("path").GetString() == "staged.txt" && e.GetProperty("staged").GetString() == "added"),
            "git_status reports a staged addition");
        Check(entries.Any(e => e.GetProperty("path").GetString() == "tracked.txt" && e.GetProperty("worktree").GetString() == "modified"),
            "git_status reports an unstaged modification");
        Check(Text(diff).Contains("+CHANGED") && !Text(diff).Contains("untracked content"),
            "git_diff shows the worktree change and, as documented, not untracked files");
        Check(Text(staged).Contains("staged content"), "git_diff --cached shows the staged content");
        Check(Text(head).Contains("+CHANGED"), "git_diff against a ref and a single path works");
        var commits = Data(log).GetProperty("commits").EnumerateArray().ToArray();
        Check(commits.Length == 1 && commits[0].GetProperty("subject").GetString() == "initial commit",
            "git_log returns real commit metadata");

        // Delta 3a: ref and path come from the model, so option-like values are refused before git is started.
        int before = runtime.Store.Events("git").Count;
        Check(Error(await tools.GitDiff("proj", "--output=x", null, false)) == "INVALID_ARGUMENT", "git_diff refuses an --output= ref");
        Check(Error(await tools.GitDiff("proj", "-p", null, false)) == "INVALID_ARGUMENT", "git_diff refuses a '-p' ref");
        Check(Error(await tools.GitLog("proj", 5, "--output=x")) == "INVALID_ARGUMENT", "git_log refuses an --output= ref");
        Check(Error(await tools.GitDiff("proj", null, "../escape", false)) == "OUTSIDE_WORKSPACE", "git_diff refuses a path that leaves the root");
        Check(Error(await tools.GitDiff("proj", "../../etc", null, false)) == "INVALID_ARGUMENT", "git_diff refuses a traversal ref");
        Check(runtime.Store.Events("git").Count == before,
            "every rejected ref and path was refused before the git process was started");
        Check(!File.Exists(Path.Combine(root, "x")), "no file named by the injected --output= option was created");
        Check(Error(await tools.GitLog("proj", 0, null)) == "INVALID_ARGUMENT", "git_log validates max_count");
    }

    private static void Trap(string path, string name)
    {
        string marker = markers.Replace('\\', '/') + "/" + name + ".marker";
        File.WriteAllText(path, $"#!/bin/sh\necho tripped > \"{marker}\"\nexit 0\n", new UTF8Encoding(false));
        // Without the execute bit git would refuse to run the trap on Linux and the control check would be vacuous.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    // Fixture construction must not depend on the machine: system/global config can enable an fsmonitor daemon,
    // auto maintenance or line-ending conversion, and a detached helper that inherits the output pipes never lets
    // them reach EOF.
    private static int Setup(string git, string cwd, params string[] arguments) => Raw(git, cwd, true, arguments);

    private static int Control(string git, string cwd, params string[] arguments) => Raw(git, cwd, false, arguments);

    private static int Raw(string git, string cwd, bool hermetic, params string[] arguments)
    {
        var start = new ProcessStartInfo(git)
        {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        if (hermetic)
            foreach (string option in new[] { "core.fsmonitor=false", "maintenance.auto=false", "gc.auto=0", "core.autocrlf=false" })
            {
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(option);
            }
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = emptyGlobalConfig;
        if (hermetic) start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!Task.WhenAll(stdout, stderr, process.WaitForExitAsync()).Wait(stallAfter))
        {
            string command = "git " + string.Join(' ', start.ArgumentList);
            // Both are read before the diagnostics and the kill, which change them.
            bool exited = process.HasExited, pipesOpen = !stdout.IsCompleted || !stderr.IsCompleted;
            Console.WriteLine("STALL " + command);
            foreach (string line in ProcessDiagnostics(process.Id)) Console.WriteLine("STALL " + line);
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* it may already have exited while a helper holds the pipes */ }
            throw new InvalidOperationException(
                $"git fixture command did not finish within {stallAfter.TotalSeconds:0} s (git exited={exited}, output pipes still open={pipesOpen}): {command}. " +
                "The STALL lines list the stalled process tree and every git process with its command line.");
        }
        return process.ExitCode;
    }

    // Lists the stalled process's descendants and every git process, with command lines, for the stall report.
    private static IEnumerable<string> ProcessDiagnostics(int stalled)
    {
        var rows = new List<(int Pid, int Parent, string Name, string CommandLine)>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // System.Diagnostics.Process does not expose command lines; CIM does without another package.
                var start = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true
                };
                foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                    "Get-CimInstance Win32_Process | ForEach-Object { \"$($_.ProcessId)`t$($_.ParentProcessId)`t$($_.Name)`t$($_.CommandLine)\" }" })
                    start.ArgumentList.Add(argument);
                using var lister = Process.Start(start)!;
                lister.StandardInput.Close();
                var output = lister.StandardOutput.ReadToEndAsync();
                var errors = lister.StandardError.ReadToEndAsync();
                if (!Task.WhenAll(output, errors, lister.WaitForExitAsync()).Wait(TimeSpan.FromSeconds(60)))
                {
                    try { lister.Kill(entireProcessTree: true); } catch (Exception) { }
                    return ["process listing did not finish"];
                }
                foreach (string line in output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] fields = line.Split('\t', 4);
                    if (fields.Length == 4 && int.TryParse(fields[0], out int pid) && int.TryParse(fields[1], out int parent))
                        rows.Add((pid, parent, fields[2], fields[3]));
                }
            }
            else
            {
                foreach (string directory in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(directory), out int pid)) continue;
                    try
                    {
                        string stat = File.ReadAllText(Path.Combine(directory, "stat"));
                        string[] after = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                        string name = stat[(stat.IndexOf('(') + 1)..stat.LastIndexOf(')')];
                        string commandLine = File.ReadAllText(Path.Combine(directory, "cmdline")).Replace('\0', ' ').Trim();
                        rows.Add((pid, int.Parse(after[1]), name, commandLine));
                    }
                    catch (Exception) { /* the process ended while it was being listed */ }
                }
            }
        }
        catch (Exception e) { return [$"process listing failed: {e.GetType().Name}"]; }
        var tree = new HashSet<int> { stalled };
        for (bool grew = true; grew; )
        {
            grew = false;
            foreach (var row in rows)
                if (tree.Contains(row.Parent) && tree.Add(row.Pid)) grew = true;
        }
        return rows.Where(r => tree.Contains(r.Pid) || r.Name.StartsWith("git", StringComparison.OrdinalIgnoreCase))
            .Select(r => $"pid={r.Pid} parent={r.Parent} {(tree.Contains(r.Pid) ? "stalled_tree" : "other_git")} {Redaction.Apply(r.CommandLine).Text}")
            .ToArray();
    }
}
