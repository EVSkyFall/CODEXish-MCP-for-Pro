using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Security.Principal;
using System.Xml.Linq;

namespace Codexish.Server;

// What Start with Windows turns on: the Task Scheduler task when the windowless starter is installed, otherwise the
// Startup-folder shortcut.
public interface IAutostart
{
    bool Enabled { get; }
    // How the connection log names it.
    string Location { get; }
    // Whether it also starts CODEXish again when it stops without Exit.
    bool Restarts { get; }
    void Enable();
    void Disable();
    // Rewrites it on evidence only and says why, or returns null when it was left as it is.
    string? Heal();
    string Rewritten(string reason);
}

public static class Autostart
{
    // The task wherever the starter sits next to this executable; the shortcut for a dotnet-hosted or partial install.
    [SupportedOSPlatform("windows")]
    public static IAutostart ForThisProcess(string configPath) =>
        (IAutostart?)TrayTask.ForThisProcess(configPath) ?? TrayAutostart.ForThisProcess(configPath);
}

// A per-user task: at sign-in, and when it is registered, it starts Codexish.Start.exe --keep and then checks every
// minute; while the keeper runs, nothing new is started. Registered through the Task Scheduler's own COM interface
// (Schedule.Service), which keeps every path exactly as written. The settings whose defaults would harm a server are
// set: no run-time limit (the default ends a task after 72 hours), normal priority (the default 7 is below normal and
// would slow every build ChatGPT runs), no battery or idle conditions. It runs as the signed-in user, needs no
// administrator rights, and only while that user is signed in.
public sealed class TrayTask : IAutostart
{
    public const string DefaultName = "CODEXish";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const int CreateOrUpdate = 6, InteractiveToken = 3;
    private readonly TrayAutostart? shortcut;
    private readonly string? testArguments;

    // testArguments: only --keeper-live-test, which adds --keeper-stub.
    public TrayTask(string starter, string configPath, string name = DefaultName, TrayAutostart? shortcut = null, string? testArguments = null)
    {
        Starter = starter;
        ConfigPath = Path.GetFullPath(configPath);
        Name = name;
        this.shortcut = shortcut;
        this.testArguments = testArguments;
    }

    public string Name { get; }
    public string Starter { get; }
    public string ConfigPath { get; }
    public string Arguments => $"{TrayKeeper.KeepMarker} --config \"{ConfigPath}\"" + (testArguments is null ? "" : " " + testArguments);
    public string Location => $"Task Scheduler task \\{Name}";
    public bool Restarts => true;

    [SupportedOSPlatform("windows")]
    public static TrayTask? ForThisProcess(string configPath)
    {
        var (process, hosted) = TrayInstance.SelfLaunch();
        string starter = Path.Combine(Path.GetDirectoryName(process) ?? "", TrayKeeper.StarterName);
        return hosted is null && File.Exists(starter) ? new TrayTask(starter, configPath, shortcut: TrayAutostart.ForThisProcess(configPath)) : null;
    }

    public static string Definition(string starter, string arguments, string userSid)
    {
        XElement Repeat() => new(Ns + "Repetition", new XElement(Ns + "Interval", "PT1M"), new XElement(Ns + "StopAtDurationEnd", "false"));
        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Description", "Starts the CODEXish server and its tray icon at sign-in, and again whenever it stops without Exit.")),
            new XElement(Ns + "Triggers",
                // The order the schema gives: the base trigger's elements, Repetition among them, before UserId.
                new XElement(Ns + "LogonTrigger", new XElement(Ns + "Enabled", "true"), Repeat(), new XElement(Ns + "UserId", userSid)),
                new XElement(Ns + "RegistrationTrigger", new XElement(Ns + "Enabled", "true"), Repeat())),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userSid), new XElement(Ns + "LogonType", "InteractiveToken"), new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "true"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "IdleSettings", new XElement(Ns + "StopOnIdleEnd", "false"), new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "RunOnlyIfIdle", "false"),
                new XElement(Ns + "WakeToRun", "false"),
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                new XElement(Ns + "Priority", "5")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", starter),
                    new XElement(Ns + "Arguments", arguments),
                    new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(starter) ?? ""))));
        return task.ToString();
    }

    // The first Exec action of a registered definition; elements a later Task Scheduler adds are ignored.
    public static (string Command, string Arguments)? Action(string xml)
    {
        try
        {
            var exec = XDocument.Parse(xml).Descendants(Ns + "Exec").FirstOrDefault();
            string? command = exec?.Element(Ns + "Command")?.Value;
            return command is null ? null : (command.Trim('"'), exec!.Element(Ns + "Arguments")?.Value ?? "");
        }
        catch (System.Xml.XmlException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    public string? RegisteredXml()
    {
        try { return (string)Folder().GetTask(Name).Xml; }
        catch (Exception error) when (NotFound(error)) { return null; }
    }

    // Registered and starting this keeper for this configuration.
    [SupportedOSPlatform("windows")]
    public bool RegisteredForThis() => RegisteredXml() is { } xml && Action(xml) is { } action && Starts(action);

    // Whether an action starts this keeper for this configuration, however either path is spelled: a different case,
    // a junction or a short name leads to the same files. The other arguments are matched exactly, as the program
    // reads them.
    public bool Starts((string Command, string Arguments) action)
    {
        try
        {
            var (rest, config) = SplitConfig(action.Arguments);
            return config is not null && string.Equals(rest, SplitConfig(Arguments).Others, StringComparison.Ordinal) &&
                TrayInstance.InstanceHash(config) == TrayInstance.InstanceHash(ConfigPath) &&
                string.Equals(TrayInstance.CanonicalPath(action.Command), TrayInstance.CanonicalPath(Starter),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private static (string Others, string? Config) SplitConfig(string arguments) =>
        Regex.Match(arguments, "--config \"([^\"]+)\"") is { Success: true } found
            ? (arguments.Remove(found.Index, found.Length), found.Groups[1].Value) : (arguments, null);

    public bool Enabled => OperatingSystem.IsWindows() && RegisteredXml() is not null;

    [SupportedOSPlatform("windows")]
    public void Enable()
    {
        if (Vitals.RedirectedFile(ConfigPath) is { } redirected)
            throw new InvalidOperationException($"The configuration opens at {redirected}, inside another app's private folder, so a CODEXish started " +
                $"by Windows could not find it. Copy that folder's contents to {Path.GetDirectoryName(ConfigPath)} with Explorer while CODEXish is stopped, then turn this on.");
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The signed-in user has no security identifier.");
        TrayKeeper.ClearStopped(ConfigPath);
        Folder().RegisterTask(Name, Definition(Starter, Arguments, sid), CreateOrUpdate, null, null, InteractiveToken, null);
        // One sign-in start, not two.
        if (shortcut?.Enabled == true) shortcut.Disable();
    }

    [SupportedOSPlatform("windows")]
    public void Disable()
    {
        try { Folder().DeleteTask(Name, 0); }
        catch (Exception error) when (NotFound(error)) { }
        if (shortcut?.Enabled == true) shortcut.Disable();
    }

    [SupportedOSPlatform("windows")]
    public void Run() => Folder().GetTask(Name).Run(null);

    // The task is rewritten when it starts another program or another configuration than this tray's, and the Startup
    // shortcut of an earlier version becomes the task. A configuration only another app can see is left alone, because
    // a task could not start CODEXish with it. Autostart that is off stays off.
    [SupportedOSPlatform("windows")]
    public string? Heal()
    {
        string? reason;
        if (RegisteredXml() is { } xml)
        {
            var action = Action(xml);
            reason = action is not { } found ? "its definition named no program"
                : !File.Exists(found.Command) ? $"its program {found.Command} no longer exists"
                : !Starts(found) ? $"it started {found.Command} {found.Arguments}"
                : null;
        }
        else reason = shortcut?.Enabled == true ? "Start with Windows was the Startup-folder shortcut " + shortcut.ShortcutPath : null;
        if (reason is null || Vitals.RedirectedFile(ConfigPath) is not null) return null;
        Enable();
        return reason;
    }

    public string Rewritten(string reason) => $"The {Location} was written to start {Starter} {Arguments} because {reason}.";

    [SupportedOSPlatform("windows")]
    private static dynamic Folder()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        service.Connect();
        return service.GetFolder("\\");
    }

    // The COM binder turns the Task Scheduler's "not found" into FileNotFoundException or COMException; the HRESULT is the same.
    private static bool NotFound(Exception error) => error.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003);
}
