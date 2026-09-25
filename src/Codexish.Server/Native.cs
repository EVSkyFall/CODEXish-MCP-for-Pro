using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Codexish.Server;

// Windows interop for the path fence and child-process supervision. Every entry point is guarded by
// OperatingSystem.IsWindows() at the call site; Linux keeps the documented weaker guarantees.
internal static class Native
{
    private const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileReadAttributes = 0x0080;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    // Resolves every junction, symlink and 8.3 alias in the chain to the real object the handle already refers to.
    public static string? FinalPath(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var buffer = new StringBuilder(1024);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity) return null;
        string final = buffer.ToString();
        return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

    // Directories cannot be opened with FileStream on Windows; FILE_FLAG_BACKUP_SEMANTICS is required.
    public static SafeFileHandle? OpenDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateFile(path, FileReadAttributes, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimit
    {
        public long PerProcessUserTime, PerJobUserTime;
        public uint Flags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong Read, Write, Other, ReadTransfer, WriteTransfer, OtherTransfer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimit
    {
        public JobBasicLimit Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static extern nint CreateJobObject(nint security, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(nint job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    // A job with KILL_ON_JOB_CLOSE ends the whole tree when the last handle closes, which is how
    // lifetime=session children die with the server even if they spawned their own grandchildren.
    public static nint CreateKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        nint job = CreateJobObject(0, null);
        if (job == 0) return 0;
        var limit = new JobExtendedLimit();
        limit.Basic.Flags = JobObjectLimitKillOnJobClose;
        int size = Marshal.SizeOf<JobExtendedLimit>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limit, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                CloseHandle(job);
                return 0;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return job;
    }

    public static bool AssignProcess(nint job, nint process) =>
        OperatingSystem.IsWindows() && job != 0 && AssignProcessToJobObject(job, process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    private const int JobObjectBasicAccountingInformation = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(nint job, int infoClass, out JobBasicAccounting info, uint length, nint returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(nint process, nint job, out bool result);

    // How many processes are still alive in a job, or null when that cannot be read.
    public static int? ActiveProcesses(nint job)
    {
        if (!OperatingSystem.IsWindows() || job == 0) return null;
        return QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out var info, (uint)Marshal.SizeOf<JobBasicAccounting>(), 0)
            ? (int)info.ActiveProcesses : null;
    }

    public static bool InJob(nint process, nint job) =>
        OperatingSystem.IsWindows() && job != 0 && IsProcessInJob(process, job, out bool result) && result;

    public static bool TerminateJob(nint job) => OperatingSystem.IsWindows() && job != 0 && TerminateJobObject(job, 1);

    public static void CloseJob(nint job) { if (OperatingSystem.IsWindows() && job != 0) CloseHandle(job); }
}
