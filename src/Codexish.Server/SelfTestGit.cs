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

        if (Raw(git, root, "init", "-b", "main") != 0)
        {
            Skip("git checks: git init failed in this environment");
            return;
        }
        Raw(git, root, "config", "user.email", "selftest@example.invalid");
        Raw(git, root, "config", "user.name", "CODEXish self test");
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "first\nsecond\n", new UTF8Encoding(false));
        Raw(git, root, "add", "tracked.txt");
        Raw(git, root, "commit", "-m", "initial commit");
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "first\nCHANGED\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "staged.txt"), "staged content\n", new UTF8Encoding(false));
        Raw(git, root, "add", "staged.txt");
        File.WriteAllText(Path.Combine(root, "untracked.txt"), "untracked content\n", new UTF8Encoding(false));

        // Traps are installed only after the setup writes, so setup does not trip them.
        foreach (string name in new[] { "fsmonitor", "external", "textconv", "pager", "editor" })
            Trap(Path.Combine(traps, name + ".sh"), name);
        string hooks = Path.Combine(root, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        foreach (string hook in new[] { "post-index-change", "pre-auto-gc", "post-checkout", "fsmonitor-watchman", "pre-commit" })
            Trap(Path.Combine(hooks, hook), "hook-" + hook);
        string Posix(string path) => path.Replace('\\', '/');
        Raw(git, root, "config", "core.fsmonitor", Posix(Path.Combine(traps, "fsmonitor.sh")));
        Raw(git, root, "config", "diff.external", Posix(Path.Combine(traps, "external.sh")));
        Raw(git, root, "config", "diff.marker.textconv", Posix(Path.Combine(traps, "textconv.sh")));
        Raw(git, root, "config", "core.pager", Posix(Path.Combine(traps, "pager.sh")));
        Raw(git, root, "config", "core.editor", Posix(Path.Combine(traps, "editor.sh")));
        File.WriteAllText(Path.Combine(root, ".gitattributes"), "*.txt diff=marker\n", new UTF8Encoding(false));

        // Control: the same repository without the fixed options must actually execute the trap.
        Raw(git, root, "status", "--porcelain=v2");
        Raw(git, root, "diff");
        int tripped = Directory.GetFiles(markers).Length;
        Check(tripped > 0, $"the repository traps really execute when git runs unprotected ({tripped} marker(s))");
        foreach (string file in Directory.GetFiles(markers)) File.Delete(file);

        var status = await tools.GitStatus("proj");
        var diff = await tools.GitDiff("proj", null, null, false);
        var staged = await tools.GitDiff("proj", null, null, true);
        var head = await tools.GitDiff("proj", "HEAD", "tracked.txt", false);
        var log = await tools.GitLog("proj", 10, null);
        Check(Directory.GetFiles(markers).Length == 0,
            "git_status, git_diff and git_log executed no hook, fsmonitor, external diff, textconv, pager or editor");

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

    private static int Raw(string git, string cwd, params string[] arguments)
    {
        var start = new ProcessStartInfo(git)
        {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
