using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BoardScout.Remote;

/// <summary>
/// Ties the ssh processes BoardScout starts to BoardScout itself. On Windows they join a job object that closes
/// with this process, so a BoardScout that crashes or is ended from Task Manager takes its SSH connections (and
/// with them the dashboards they started on other machines) along. On Linux, ssh reads its standard input from
/// BoardScout; when BoardScout goes, that input ends, and so does BoardScout on the machine.
/// </summary>
internal static class ChildProcesses
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private static readonly Lazy<IntPtr> Job = new(Create);

    public static void Tie(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (Job.Value != IntPtr.Zero) AssignProcessToJobObject(Job.Value, process.Handle);
        }
        catch
        {
            // Already gone, or not allowed: it still ends when BoardScout ends it.
        }
    }

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        return SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) ? job : IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
