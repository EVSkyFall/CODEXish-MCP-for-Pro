using System.Diagnostics;

namespace Codexish.Server;

// The self-test drives real interpreters. cmd.exe exists only on Windows and pwsh may be absent anywhere, so
// every process check asks this helper which interpreter to use and what to say to it. Windows keeps cmd as the
// preferred shell because that is the path the Windows runner has always exercised.
internal static class Shells
{
    public static bool HasPwsh { get; }
    public static bool HasCmd { get; }
    public static string? Preferred { get; }

    static Shells()
    {
        HasPwsh = Probe("pwsh", ["-NoProfile", "-Command", "exit 0"]);
        HasCmd = OperatingSystem.IsWindows() && Probe("cmd.exe", ["/c", "exit", "0"]);
        Preferred = OperatingSystem.IsWindows()
            ? HasCmd ? "cmd" : HasPwsh ? "pwsh" : null
            : HasPwsh ? "pwsh" : null;
        // CODEXISH_SELFTEST_SHELL=pwsh runs the whole suite through the interpreter the Linux runner uses,
        // which is how the pwsh command forms are exercised from a Windows machine.
        string forced = Environment.GetEnvironmentVariable("CODEXISH_SELFTEST_SHELL") ?? "";
        if (forced == "pwsh" && HasPwsh) Preferred = "pwsh";
        else if (forced == "cmd" && HasCmd) Preferred = "cmd";
        if (forced.Length > 0) Console.WriteLine($"NOTE self-test shell forced to {Preferred ?? "none"} by CODEXISH_SELFTEST_SHELL");
    }

    private static bool Probe(string executable, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return false;
            process.WaitForExit(20000);
            return true;
        }
        catch (Exception) { return false; }
    }

    public static string Executable(string shell) => shell == "cmd" ? "cmd.exe" : "pwsh";

    public static string[] DirectArguments(string shell, string text) => shell == "cmd"
        ? ["/c", "echo " + text]
        : ["-NoProfile", "-NonInteractive", "-Command", "Write-Output '" + text + "'"];

    public static string Sleep(string shell, int seconds) => shell == "cmd"
        ? $"ping -n {seconds + 1} 127.0.0.1 >nul"
        : $"Start-Sleep -Seconds {seconds}";

    // Two ordered stdout lines, one stderr line, and a nonzero exit code.
    public static string Streams(string shell) => shell == "cmd"
        ? "echo one& echo two& echo three 1>&2& exit /b 7"
        : "Write-Output 'one'; Write-Output 'two'; [Console]::Error.WriteLine('three'); exit 7";

    public static string Ticks(string shell, int count) => shell == "cmd"
        ? $"for /L %i in (1,1,{count}) do @(echo tick %i& ping -n 2 127.0.0.1 >nul)"
        : $"1..{count} | ForEach-Object {{ Write-Output \"tick $_\"; Start-Sleep -Milliseconds 400 }}";

    // Reads stdin to EOF and writes it back, so closing stdin with a graceful stop ends the process.
    public static string EchoStdin(string shell) => shell == "cmd"
        ? "sort"
        : "$text = [Console]::In.ReadToEnd(); Write-Output $text";

    public static string Bulk(string shell, int lines) => shell == "cmd"
        ? $"for /L %i in (1,1,{lines}) do @echo line %i padding padding padding padding padding"
        : $"1..{lines} | ForEach-Object {{ 'line {{0}} padding padding padding padding padding' -f $_ }}";

    // Starts a detached grandchild and prints its PID, then stays alive. Only the supervisor can end the tree.
    public static string Grandchild(string shell) => shell == "cmd"
        ? "$c = Start-Process -FilePath cmd.exe -ArgumentList '/c ping -n 30 127.0.0.1' -PassThru; Write-Output $c.Id; Start-Sleep -Seconds 30"
        : "$c = Start-Process -FilePath sleep -ArgumentList '30' -PassThru; Write-Output $c.Id; Start-Sleep -Seconds 30";
}
