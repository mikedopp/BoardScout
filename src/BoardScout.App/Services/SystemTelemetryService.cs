using System.Runtime.InteropServices;
using BoardScout.Models;

namespace BoardScout.Services;

internal sealed class SystemTelemetryService
{
    private ulong? _previousIdle;
    private ulong? _previousKernel;
    private ulong? _previousUser;
    private double _lastCpuUsage;

    public SystemTelemetry Sample()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var idleValue = ToUInt64(idle);
            var kernelValue = ToUInt64(kernel);
            var userValue = ToUInt64(user);

            if (_previousIdle.HasValue && _previousKernel.HasValue && _previousUser.HasValue)
            {
                var idleDelta = idleValue - _previousIdle.Value;
                var totalDelta = (kernelValue - _previousKernel.Value) + (userValue - _previousUser.Value);
                if (totalDelta > 0)
                    _lastCpuUsage = Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
            }

            _previousIdle = idleValue;
            _previousKernel = kernelValue;
            _previousUser = userValue;
        }

        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref memory);
        return new SystemTelemetry(_lastCpuUsage, memory.TotalPhysical, memory.AvailablePhysical, DateTimeOffset.Now);
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
