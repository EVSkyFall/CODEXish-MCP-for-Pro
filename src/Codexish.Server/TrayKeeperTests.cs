using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace Codexish.Server;

// Part of --tray-tests. The task definition and the keeper's start arguments are checked everywhere; the keeper loop,
// which uses Windows' named events, runs on Windows with stand-in children, so no tray, task or window is involved.
internal static class TrayKeeperTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static void Run(string directory, Action<bool, string> Check)
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        // Paths of this platform, so the same checks hold on Linux, where a backslash is part of a file name.
        string install = Path.Combine(directory, "Programs", "Codexish"), starter = Path.Combine(install, TrayKeeper.StarterName);
        string config = Path.GetFullPath(Path.Combine(directory, "codexish.json"));
        var task = new TrayTask(starter, config, "CODEXish-test");
        string xml = TrayTask.Definition(starter, task.Arguments, sid);
        var definition = XDocument.Parse(xml);
        string? Value(string name) => definition.Descendants(Ns + name).FirstOrDefault()?.Value;
        Check(Value("ExecutionTimeLimit") == "PT0S" && Value("Priority") == "5" && Value("MultipleInstancesPolicy") == "IgnoreNew" &&
              Value("DisallowStartIfOnBatteries") == "false" && Value("StopIfGoingOnBatteries") == "false" && Value("StartWhenAvailable") == "true" &&
              Value("RunOnlyIfIdle") == "false" && Value("StopOnIdleEnd") == "false" && Value("Enabled") == "true",
            "the task has no run-time limit, normal priority and no battery or idle conditions, and never starts a second keeper");
        var triggers = definition.Descendants(Ns + "Triggers").Single().Elements().ToList();
        Check(triggers.Select(t => t.Name.LocalName).SequenceEqual(["LogonTrigger", "RegistrationTrigger"]) &&
              triggers.All(t => t.Element(Ns + "Repetition") is { } repeat && repeat.Element(Ns + "Interval")?.Value == "PT1M" &&
                  repeat.Element(Ns + "Duration") is null && repeat.Element(Ns + "StopAtDurationEnd")?.Value == "false") &&
              triggers[0].Element(Ns + "UserId")?.Value == sid &&
              triggers[0].Elements().Select(e => e.Name.LocalName).SequenceEqual(["Enabled", "Repetition", "UserId"]),
            "it starts at this user's sign-in and when it is registered, then checks every minute without end, in the schema's order");
        var principal = definition.Descendants(Ns + "Principal").Single();
        Check(principal.Element(Ns + "UserId")?.Value == sid && principal.Element(Ns + "LogonType")?.Value == "InteractiveToken" &&
              principal.Element(Ns + "RunLevel")?.Value == "LeastPrivilege",
            "it runs as the signed-in user, in that user's session, without administrator rights");
        Check(TrayTask.Action(xml) == (starter, $"--keep --config \"{config}\"") && Value("WorkingDirectory") == install &&
              definition.Descendants(Ns + "Exec").Count() == 1 && TrayTask.Action("<not xml") is null && TrayTask.Action("<Task />") is null &&
              TrayTask.Action($"<Task xmlns=\"{Ns}\"><Actions><Exec><Command>\"C:\\A B\\x.exe\"</Command></Exec></Actions><Later>kept</Later></Task>") == (@"C:\A B\x.exe", ""),
            "its one action starts the windowless keeper for this configuration from the install folder; a definition is read tolerantly");
        Check(task.Location == @"Task Scheduler task \CODEXish-test" && task.Restarts &&
              task.Rewritten("its program C:\\Old\\Codexish.Start.exe no longer exists") ==
              $"The Task Scheduler task \\CODEXish-test was written to start {starter} --keep --config \"{config}\" because its program C:\\Old\\Codexish.Start.exe no longer exists.",
            "the connection log names the task, what it now starts and why it was written");
        Check(task.Starts((starter, task.Arguments)) && !task.Starts((starter, $"--keep --config \"{Path.Combine(directory, "other.json")}\"")) &&
              !task.Starts((starter, task.Arguments + " --keeper-stub")) && !task.Starts((starter, "--keep")) &&
              !task.Starts((Path.Combine(directory, "Codexish.Start.exe"), task.Arguments)),
            "the task counts as this configuration's only when it starts this starter for this configuration file and nothing else");
        if (OperatingSystem.IsWindows())
            Check(task.Starts((starter.ToUpperInvariant(), $"--keep --config \"{config.ToUpperInvariant()}\"")) &&
                  !task.Starts((starter, $"--KEEP --config \"{config}\"")),
                "a path spelled in another case is the same file on Windows, so it neither skips the hand-off nor rewrites the task; an option in another case is not the same option");
        string oddFolder = Path.Combine(directory, "odd");
        Directory.CreateDirectory(oddFolder);
        string odd = Path.Combine(oddFolder, "codexish.json");
        File.WriteAllText(odd, "[]");
        TrayKeeper.Log(odd, "a line");
        File.WriteAllText(odd, "null");
        TrayKeeper.Log(odd, "another line");
        Check(Directory.EnumerateFiles(Path.Combine(oddFolder, "logs"), "keeper-*.log").Any(),
            "a configuration that is valid JSON but no object does not end the keeper's logging; the log goes next to it");
        Check(TrayNotice.StartWithWindows(true, restarts: true) == "Start with Windows is on: CODEXish starts when you sign in, and again whenever it stops without Exit." &&
              TrayNotice.StartWithWindows(true) == "Start with Windows is on: CODEXish starts when you sign in." && TrayNotice.StartWithWindows(false, restarts: true) == "Start with Windows is off.",
            "the notification says when Start with Windows also starts CODEXish again");
        string bin = Path.Combine(directory, "x"), server = Path.Combine(bin, "Codexish.Server.exe");
        var info = TrayKeeper.TrayStart(server, config, "the previous tray (pid 7) exited with code 1 after 00:00:03");
        Check(info.CreateNoWindow && !info.UseShellExecute && info.WorkingDirectory == bin &&
              info.ArgumentList.SequenceEqual(["--tray", "--start", "--config", config, "--tray-detached", "--tray-kept", "--tray-restarted", "the previous tray (pid 7) exited with code 1 after 00:00:03"]) &&
              !TrayKeeper.TrayStart(server, config, null).ArgumentList.Contains("--tray-restarted"),
            "the keeper starts the tray with a hidden console, marked as kept, and tells it why it starts it again");

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("TRAY SKIP windowless starter and keeper loop: Windows only");
            return;
        }
        string built = Path.Combine(AppContext.BaseDirectory, "Codexish.Server.exe"), start = Path.Combine(AppContext.BaseDirectory, TrayKeeper.StarterName);
        if (File.Exists(built) && File.Exists(start))
        {
            byte[] console = File.ReadAllBytes(built), windowless = File.ReadAllBytes(start);
            int field = BitConverter.ToInt32(console, 0x3C) + 4 + 20 + 68;
            int differing = console.Length == windowless.Length ? console.Where((b, i) => b != windowless[i]).Count() : -1;
            Check(BitConverter.ToUInt16(console, field) == 3 && BitConverter.ToUInt16(windowless, field) == 2 && differing == 1,
                "Codexish.Start.exe is this build's own executable with only its subsystem changed to Windows, so it opens no console");
        }
        else Console.WriteLine("TRAY SKIP windowless starter: this build has no Codexish.Start.exe next to it");
        Loop(directory, Check);
    }

    private sealed class Child : TrayKeeper.IChild
    {
        private static int next = 41000;
        private readonly ManualResetEvent done = new(false);
        public int Id { get; } = Interlocked.Increment(ref next);
        public WaitHandle Exited => done;
        public int ExitCode { get; private set; }
        public void End(int code) { ExitCode = code; done.Set(); }
        public void Dispose() { }
    }

    private static (Child Child, string? Restarted) Next(BlockingCollection<(Child, string?)> started)
    {
        if (!started.TryTake(out var item, TimeSpan.FromSeconds(30))) throw new InvalidOperationException("the keeper started no tray within 30 s");
        return item;
    }

    // Everything here runs on this one thread, because a mutex must be released by the thread that holds it.
    [SupportedOSPlatform("windows")]
    private static void Loop(string directory, Action<bool, string> Check)
    {
        string config = Path.Combine(directory, "keeper-" + Guid.NewGuid().ToString("N") + ".json");
        var started = new BlockingCollection<(Child, string?)>();
        var log = new ConcurrentQueue<string>();
        int failFirst = 1;
        var loop = new TrayKeeper.Loop(config, restarted =>
        {
            if (Interlocked.Decrement(ref failFirst) >= 0) throw new IOException("the program is being replaced");
            var child = new Child();
            started.Add((child, restarted));
            return child;
        }, log.Enqueue, failures => TimeSpan.FromMilliseconds(100 * failures));
        var keeping = Task.Run(loop.Run);
        var (first, firstNote) = Next(started);
        Check(firstNote is null && log.Any(l => l.StartsWith("Starting the tray failed: IOException: the program is being replaced. Next attempt in 0 s; failures in a row: 1.", StringComparison.Ordinal)),
            "a tray that cannot be started yet is tried again with backoff, and the failure is logged with its cause");
        Check(Task.Run(new TrayKeeper.Loop(config, _ => throw new InvalidOperationException("a second keeper started a tray"), _ => { }).Run).Result == 0,
            "a second keeper for the same configuration ends at once and starts nothing");

        first.End(1);
        var (second, secondNote) = Next(started);
        Check(secondNote?.StartsWith($"the previous tray (pid {first.Id}) exited with code 1 after 00:00:0", StringComparison.Ordinal) == true &&
              log.Any(l => l.StartsWith($"The tray (pid {first.Id}) exited with code 1 after ", StringComparison.Ordinal) && l.Contains(" without Exit. It starts again in ", StringComparison.Ordinal)),
            "a tray that ends without Exit, crashed or killed, is started again, and the new one is told why");

        TrayKeeper.ChoseExit(config);
        second.End(0);
        Thread.Sleep(600);
        Check(started.Count == 0 && log.Any(l => l.StartsWith("The tray ended with Exit;", StringComparison.Ordinal)),
            "after Exit the keeper starts nothing and waits");
        using (var request = EventWaitHandle.OpenExisting(TrayKeeper.StartEventName(config))) request.Set();
        var (third, thirdNote) = Next(started);
        Check(thirdNote is null, "a start request, as a hand-off sends it, starts the tray again after Exit");

        var other = TrayInstance.TryAcquire(config)!;
        third.End(TrayKeeper.AnotherTrayExitCode);
        Thread.Sleep(600);
        Check(started.Count == 0 && log.Any(l => l.StartsWith("Another CODEXish tray runs for this configuration;", StringComparison.Ordinal)),
            "a kept tray that found another tray running makes the keeper wait for that one");
        other.ReleaseMutex();
        other.Dispose();
        var (fourth, fourthNote) = Next(started);
        Check(fourthNote == "the tray that was already running has ended", "once that tray has ended, the keeper starts its own");

        var output = new StringWriter();
        var stopping = Task.Run(() => TrayKeeper.Stop(config, output));
        Thread.Sleep(300);
        // Opened only now, as a tray that is still starting opens it late: the keeper holds it, so the request waits there.
        using (var stopRequest = EventWaitHandle.OpenExisting(TrayKeeper.StopEventName(config)))
        {
            Check(stopRequest.WaitOne(TimeSpan.FromSeconds(30)),
                "--stop asks the running tray to stop the way Exit stops it, also a tray that opens its stop request only afterwards");
            Thread.Sleep(200);
            Check(!stopping.IsCompleted && !keeping.IsCompleted, "--stop and the keeper wait until the tray has actually ended");
            fourth.End(0);
            Check(keeping.Wait(TimeSpan.FromSeconds(30)) && keeping.Result == 0 && stopping.Wait(TimeSpan.FromSeconds(30)) &&
                  output.ToString().Contains("CODEXish stopped;", StringComparison.Ordinal) && started.Count == 0,
                "then the keeper ends without starting it again, and --stop returns");
        }
        Check(TrayKeeper.StoppedThisSession(config) &&
              new TrayKeeper.Loop(config, _ => throw new InvalidOperationException("a keeper started a tray after --stop"), _ => { }).Run() == 0,
            "after --stop, a keeper the task starts in this sign-in session starts nothing, so an update can replace the files");
        TrayKeeper.ClearStopped(config);
        Check(!TrayKeeper.StoppedThisSession(config), "starting CODEXish by hand ends that");
        File.WriteAllText(TrayKeeper.StoppedMarker(config), "1");
        Check(!TrayKeeper.StoppedThisSession(config) && !File.Exists(TrayKeeper.StoppedMarker(config)),
            "a --stop from an earlier sign-in session no longer holds, and its mark is removed");
        string racing = Path.Combine(directory, "keeper-" + Guid.NewGuid().ToString("N") + ".json");
        var racingStarted = new BlockingCollection<(Child, string?)>();
        var racingLog = new ConcurrentQueue<string>();
        var racingKeeper = Task.Run(new TrayKeeper.Loop(racing, restarted =>
        {
            var child = new Child();
            racingStarted.Add((child, restarted));
            return child;
        }, racingLog.Enqueue, _ => TimeSpan.FromMilliseconds(100)).Run);
        var (ending, _) = Next(racingStarted);
        using (var waiting = new EventWaitHandle(false, EventResetMode.ManualReset, TrayInstance.ReadyEventName(racing)))
        {
            // A hand-off that saw no tray while this one was ending: its start request comes before the keeper sees the end.
            TrayKeeper.ChoseExit(racing);
            using (var request = EventWaitHandle.OpenExisting(TrayKeeper.StartEventName(racing))) request.Set();
            ending.End(0);
            var (again, _) = Next(racingStarted);
            Check(racingLog.Any(l => l.StartsWith("The tray ended with Exit while CODEXish was being started again;", StringComparison.Ordinal)),
                "a start that comes while the tray is ending after Exit is not lost: the keeper starts the tray for it");
            TrayKeeper.ChoseExit(racing);
            TrayKeeper.EndKeeper(racing);
            again.End(0);
        }
        Check(racingKeeper.Wait(TimeSpan.FromSeconds(30)), "that keeper ends too");

        // A configuration file reached through a symbolic link: the mark goes beside the file the task names.
        string realFolder = Path.Combine(directory, "real"), linkFolder = Path.Combine(directory, "link");
        Directory.CreateDirectory(realFolder);
        Directory.CreateDirectory(linkFolder);
        string real = Path.Combine(realFolder, "codexish.json"), alias = Path.Combine(linkFolder, "codexish.json");
        File.WriteAllText(real, "{}");
        bool linked;
        try { File.CreateSymbolicLink(alias, real); linked = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { linked = false; }
        if (linked)
        {
            TrayKeeper.Stop(alias, new StringWriter());
            Check(TrayKeeper.StoppedThisSession(real) && File.Exists(real + ".stopped") && !File.Exists(alias + ".stopped"),
                "--stop through a symbolic link marks the configuration the task names, so its minute check stays stopped");
            TrayKeeper.ClearStopped(alias);
            Check(!File.Exists(real + ".stopped"), "and a start through the link ends that mark");
        }
        else Console.WriteLine("TRAY SKIP --stop through a symbolic link: this session may not create one");

        string closing = Path.Combine(directory, "keeper-" + Guid.NewGuid().ToString("N") + ".json");
        var closingStarted = new BlockingCollection<(Child, string?)>();
        var closingLog = new ConcurrentQueue<string>();
        var closingKeeper = Task.Run(new TrayKeeper.Loop(closing, restarted =>
        {
            var child = new Child();
            closingStarted.Add((child, restarted));
            return child;
        }, closingLog.Enqueue, _ => TimeSpan.FromMilliseconds(100)).Run);
        var (last, _) = Next(closingStarted);
        TrayKeeper.ChoseExit(closing);
        TrayKeeper.EndKeeper(closing);
        last.End(0);
        Check(closingKeeper.Wait(TimeSpan.FromSeconds(30)) && closingKeeper.Result == 0 && closingStarted.Count == 0 &&
              !closingLog.Any(l => l.Contains("without Exit", StringComparison.Ordinal)),
            "Exit with Start with Windows off ends the keeper as well, which starts nothing again and logs no unexpected end");
        // A tray started by hand, without a keeper, that holds its mutex but opens its stop request only after --stop ran.
        string late = Path.Combine(directory, "keeper-" + Guid.NewGuid().ToString("N") + ".json");
        bool reached = false;
        using (var holding = new ManualResetEventSlim())
        {
            var starting = new Thread(() =>
            {
                var held = TrayInstance.TryAcquire(late)!;
                holding.Set();
                Thread.Sleep(500);
                using (var request = new EventWaitHandle(false, EventResetMode.AutoReset, TrayKeeper.StopEventName(late)))
                    reached = request.WaitOne(TimeSpan.FromSeconds(30));
                held.ReleaseMutex();
                held.Dispose();
            });
            starting.Start();
            holding.Wait();
            var lateOutput = new StringWriter();
            var lateStop = Task.Run(() => TrayKeeper.Stop(late, lateOutput));
            Check(lateStop.Wait(TimeSpan.FromSeconds(60)) && starting.Join(TimeSpan.FromSeconds(30)) && reached &&
                  lateOutput.ToString().Contains("CODEXish stopped;", StringComparison.Ordinal),
                "--stop reaches a tray that opens its stop request only after --stop ran, and returns once that tray has let go");
        }
        TrayKeeper.ClearStopped(late);
        var idle = new StringWriter();
        Check(TrayKeeper.Stop(config, idle) == 0 && idle.ToString().StartsWith("CODEXish is not running for ", StringComparison.Ordinal),
            "--stop with nothing running says so");
    }
}
