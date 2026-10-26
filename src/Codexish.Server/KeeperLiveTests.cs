using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Codexish.Server;

// --keeper-live-test, run on purpose only. It registers a temporary Task Scheduler task of its own
// (\CODEXish-live-test-<id>) for a configuration in a new temporary folder and lets Windows start the windowless keeper
// with a stand-in tray, which holds the tray's names but opens no window and no icon. It then kills the stand-in to see
// the keeper start it again, kills the keeper to see the task's minute check start it again, ends the stand-in the way
// Exit does and opens Codexish.Start.exe by hand to see the waiting keeper start it again, deletes the task, stops both
// the way --stop does and removes the folder. The installed CODEXish, its task, its state and its port are never
// touched.
internal static class KeeperLiveTests
{
    public static async Task<int> Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("KEEPER_LIVE_SKIPPED: Windows only");
            return 0;
        }
        string starter = Path.Combine(AppContext.BaseDirectory, TrayKeeper.StarterName);
        string directory = Path.Combine(Path.GetTempPath(), "codexish-keeper-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string config = Path.Combine(directory, "codexish.json");
        // The stand-in never loads it; the keeper only looks for state_dir in it.
        File.WriteAllText(config, "{}");
        var task = new TrayTask(starter, config, "CODEXish-live-test-" + Guid.NewGuid().ToString("N")[..8], testArguments: "--keeper-stub");
        int passed = 0;
        void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException(text);
            Console.WriteLine($"KEEPER LIVE PASS {passed + 1:00} {text}");
            passed++;
        }
        // Every process this test identified as its own, by pid and start time, so a pid Windows has given to another
        // program since is never taken for one of them.
        var owned = new List<(int Pid, DateTime Started)>();
        DateTime? StartedAt(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.StartTime;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        }
        void Own(int? pid)
        {
            if (pid is { } id && StartedAt(id) is { } started) owned.Add((id, started));
        }
        var seen = new HashSet<int>();
        (int Pid, string Launch)? Stand()
        {
            foreach (string file in Directory.EnumerateFiles(directory, "stub-*.txt"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(file)["stub-".Length..], out int pid) && seen.Add(pid))
                {
                    Own(pid);
                    return (pid, File.ReadAllText(file));
                }
            return null;
        }
        static int? KeeperOf(string launch) =>
            Regex.Match(launch, @"^by " + Regex.Escape(TrayKeeper.StarterName) + @" (\d+)", RegexOptions.IgnoreCase) is { Success: true } found
                ? int.Parse(found.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        // A keeper of this build that started after the given moment: the task's, since nothing else starts one then.
        int? KeeperSince(DateTime moment)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(TrayKeeper.StarterName)))
                using (process)
                {
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, starter, StringComparison.OrdinalIgnoreCase) && process.StartTime > moment)
                            return process.Id;
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            return null;
        }
        // Gone once every handle is closed, which is when its holder is ending.
        static bool Gone(string name)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (!Mutex.TryOpenExisting(name, out var mutex)) return true;
                mutex.Dispose();
                if (clock.Elapsed > TimeSpan.FromSeconds(30)) return false;
                Thread.Sleep(100);
            }
        }
        string KeeperLog() => Directory.Exists(Path.Combine(directory, "logs"))
            ? string.Concat(Directory.EnumerateFiles(Path.Combine(directory, "logs"), "keeper-*.log").Select(File.ReadAllText)) : "";
        static bool Alive(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
        }
        // A launcher's wait for a tray, held by the test; closed while the stand-in ends with Exit, so the keeper sees no
        // start waiting then.
        var ready = new EventWaitHandle(false, EventResetMode.ManualReset, TrayInstance.ReadyEventName(config));
        try
        {
            if (!File.Exists(starter)) throw new InvalidOperationException("this build has no " + starter);
            task.Enable();
            string registered = task.RegisteredXml() ?? "";
            Check(task.RegisteredForThis() && registered.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", StringComparison.Ordinal) &&
                  registered.Contains("<Priority>5</Priority>", StringComparison.Ordinal) && registered.Contains("<LogonType>InteractiveToken</LogonType>", StringComparison.Ordinal),
                $"the task {task.Name} is registered for this user without administrator rights, with its settings as written");
            var found = ready.WaitOne(TimeSpan.FromSeconds(60)) ? Stand() : null;
            Check(found is not null, "Windows started the keeper as soon as the task was registered, and the keeper started the stand-in tray");
            var first = found!.Value;
            Own(KeeperOf(first.Launch));
            Check(first.Launch.StartsWith("by " + TrayKeeper.StarterName + " ", StringComparison.OrdinalIgnoreCase) &&
                  !first.Launch.Contains("claude", StringComparison.OrdinalIgnoreCase),
                "the stand-in is a child of the windowless keeper, which Windows started, and not of anything this test runs in: " + first.Launch);

            ready.Reset();
            using (var stand = Process.GetProcessById(first.Pid)) stand.Kill();
            var second = ready.WaitOne(TimeSpan.FromSeconds(60)) ? Stand() : null;
            Check(second is not null, "a stand-in killed from outside is started again by the keeper");
            string logs = Path.Combine(directory, "logs");
            await Task.Delay(200);
            Check(Directory.Exists(logs) && Directory.EnumerateFiles(logs, "keeper-*.log").Any(f => File.ReadAllText(f).Contains($"(pid {first.Pid}) exited with code", StringComparison.Ordinal)),
                "the keeper's log says which tray ended, with its exit code, and when it starts again");

            // The keeper itself ended from outside: the task's minute check starts a new one, which brings the tray back
            // whether the running stand-in outlived its keeper or not.
            int keeper = KeeperOf(second!.Value.Launch) ?? throw new InvalidOperationException("the stand-in names no keeper: " + second.Value.Launch);
            ready.Reset();
            var clock = Stopwatch.StartNew();
            var killedAt = DateTime.Now;
            using (var process = Process.GetProcessById(keeper)) process.Kill();
            int? next = null;
            while (next is null && clock.Elapsed < TimeSpan.FromSeconds(150))
            {
                await Task.Delay(1000);
                next = KeeperSince(killedAt);
            }
            var restarted = clock.Elapsed;
            bool outlived = Alive(second.Value.Pid);
            if (next is not null && outlived)
                using (var stand = Process.GetProcessById(second.Value.Pid)) stand.Kill();
            var third = next is not null && ready.WaitOne(TimeSpan.FromSeconds(60)) ? Stand() : null;
            Check(third is not null && KeeperOf(third.Value.Launch) == next,
                $"a keeper killed from outside is started again by the task's minute check ({restarted.TotalSeconds:0} s here), and the new one " +
                (outlived ? "waited for the stand-in that outlived its keeper and started its own once that one was ended"
                    : "started the tray again, since the stand-in had ended with its keeper"));
            // Only now is it known to be this test's keeper: its stand-in names it.
            Own(next);

            ready.Dispose();
            // The stand-in ends the way the tray's Exit does: it tells the keeper, then ends.
            using (var stopRequest = EventWaitHandle.OpenExisting(TrayKeeper.StopEventName(config))) stopRequest.Set();
            var waitedSince = Stopwatch.StartNew();
            while (!KeeperLog().Contains("The tray ended with Exit;", StringComparison.Ordinal) && waitedSince.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(200);
            await Task.Delay(3000);
            Check(KeeperLog().Contains("The tray ended with Exit;", StringComparison.Ordinal) && !Alive(third!.Value.Pid) && Stand() is null && Alive(next!.Value),
                "after Exit the keeper stays and starts nothing");
            ready = new EventWaitHandle(false, EventResetMode.ManualReset, TrayInstance.ReadyEventName(config));
            using (var byHand = Process.Start(new ProcessStartInfo(starter) { UseShellExecute = false, ArgumentList = { "--config", config, "--keeper-stub" } })!)
            {
                bool ended = byHand.WaitForExit(60_000);
                var fourth = ready.WaitOne(TimeSpan.FromSeconds(60)) ? Stand() : null;
                Check(ended && byHand.ExitCode == 0 && fourth is not null && KeeperOf(fourth.Value.Launch) == next,
                    "Codexish.Start.exe opened by hand asks the waiting keeper to start the tray and ends, so the tray is the keeper's child again");
            }

            task.Disable();
            Check(task.RegisteredXml() is null, "the task is deleted again");
            var output = new StringWriter();
            Check(TrayKeeper.Stop(config, output) == 0 && output.ToString().Contains("CODEXish stopped;", StringComparison.Ordinal) &&
                  Gone(TrayKeeper.MutexName(config)) && Gone(TrayInstance.MutexName(config)),
                "--stop ends the stand-in and the keeper and returns once both have let go");
            Console.WriteLine($"KEEPER_LIVE_PASSED: {passed}; a temporary task of its own and a stand-in tray only, deleted again.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"KEEPER_LIVE_FAILED after {passed}: {error}");
            return 1;
        }
        finally
        {
            // The task goes first, so its minute check cannot start another keeper while the rest is stopped.
            try { task.Disable(); } catch (Exception error) { Console.Error.WriteLine("Deleting the task failed: " + error.Message); }
            try { TrayKeeper.Stop(config, TextWriter.Null); } catch (Exception error) { Console.Error.WriteLine("Stopping failed: " + error.Message); }
            ready.Dispose();
            // Whatever a failed check left behind: this test's own keepers and stand-ins, never another program.
            foreach (var (pid, started) in owned)
                try
                {
                    using var left = Process.GetProcessById(pid);
                    if (left.StartTime == started && !left.HasExited) left.Kill();
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            TestCleanup.RemoveDirectory(directory);
        }
    }
}
