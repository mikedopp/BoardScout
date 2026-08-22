using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using BoardScout.Models;

namespace BoardScout.Services;

internal sealed class SystemTelemetryService : IDisposable
{
    private ulong? _previousIdle;
    private ulong? _previousKernel;
    private ulong? _previousUser;
    private double _lastCpuUsage;

    private long _previousDiskReadBytes;
    private long _previousDiskWriteBytes;
    private long _previousNetSent;
    private long _previousNetReceived;
    private DateTime _previousSampleTime = DateTime.UtcNow;
    private bool _hasPrevious;

    private ManagementObjectSearcher? _thermalSearcher;
    private ManagementObjectSearcher? _fanSearcher;
    private bool _thermalFailed;
    private bool _fanFailed;

    public IReadOnlyList<FanReading> LastFanReadings { get; private set; } = [];

    public SystemTelemetry Sample()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _previousSampleTime).TotalSeconds;
        if (elapsed < 0.01) elapsed = 1;

        var cpuUsage = SampleCpu();
        var (memTotal, memAvailable) = SampleMemory();
        var thermals = SampleThermals();
        var fans = SampleFans();
        LastFanReadings = fans;
        var (diskRead, diskWrite) = SampleDiskIo(elapsed);
        var (netSent, netReceived) = SampleNetwork(elapsed);

        _previousSampleTime = now;

        return new SystemTelemetry(
            cpuUsage, memTotal, memAvailable,
            thermals,
            diskRead, diskWrite,
            netSent, netReceived,
            DateTimeOffset.Now);
    }

    private double SampleCpu()
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
        return _lastCpuUsage;
    }

    private static (ulong Total, ulong Available) SampleMemory()
    {
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref memory);
        return (memory.TotalPhysical, memory.AvailablePhysical);
    }

    private List<ThermalReading> SampleThermals()
    {
        if (_thermalFailed) return [];

        try
        {
            _thermalSearcher ??= new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT * FROM MSAcpi_ThermalZoneTemperature");

            var readings = new List<ThermalReading>();
            foreach (ManagementObject obj in _thermalSearcher.Get())
            {
                var name = obj["InstanceName"]?.ToString() ?? "Zone";
                var tempKelvinTenths = Convert.ToDouble(obj["CurrentTemperature"]);
                var celsius = (tempKelvinTenths / 10.0) - 273.15;
                if (celsius is > -40 and < 150)
                {
                    var zoneName = name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ? "CPU"
                        : name.Contains("GPU", StringComparison.OrdinalIgnoreCase) ? "GPU"
                        : $"Zone {readings.Count + 1}";
                    readings.Add(new ThermalReading(zoneName, Math.Round(celsius, 1)));
                }
            }
            return readings;
        }
        catch
        {
            _thermalFailed = true;
            return [];
        }
    }

    private List<FanReading> SampleFans()
    {
        if (_fanFailed) return [];

        try
        {
            _fanSearcher ??= new ManagementObjectSearcher(
                "SELECT * FROM Win32_Fan");

            var readings = new List<FanReading>();
            foreach (ManagementObject obj in _fanSearcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? $"Fan {readings.Count + 1}";
                var rpm = Convert.ToInt32(obj["DesiredSpeed"]);
                var active = obj["ActiveCooling"] is true;
                readings.Add(new FanReading(name, rpm, active));
            }
            return readings;
        }
        catch
        {
            _fanFailed = true;
            return [];
        }
    }

    private (double ReadBytesPerSec, double WriteBytesPerSec) SampleDiskIo(double elapsed)
    {
        if (GetProcessIoCounters(GetCurrentProcess(), out var counters))
        {
            var readBytes = (long)counters.ReadTransferCount;
            var writeBytes = (long)counters.WriteTransferCount;
            if (_hasPrevious)
            {
                var dr = Math.Max(0, readBytes - _previousDiskReadBytes) / elapsed;
                var dw = Math.Max(0, writeBytes - _previousDiskWriteBytes) / elapsed;
                _previousDiskReadBytes = readBytes;
                _previousDiskWriteBytes = writeBytes;
                return (dr, dw);
            }
            _previousDiskReadBytes = readBytes;
            _previousDiskWriteBytes = writeBytes;
        }
        return (0, 0);
    }

    private (double SentBytesPerSec, double ReceivedBytesPerSec) SampleNetwork(double elapsed)
    {
        try
        {
            long totalSent = 0, totalReceived = 0;
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
                var stats = iface.GetIPStatistics();
                totalSent += stats.BytesSent;
                totalReceived += stats.BytesReceived;
            }

            if (_hasPrevious)
            {
                var sent = Math.Max(0, totalSent - _previousNetSent) / elapsed;
                var received = Math.Max(0, totalReceived - _previousNetReceived) / elapsed;
                _previousNetSent = totalSent;
                _previousNetReceived = totalReceived;
                return (sent, received);
            }
            _previousNetSent = totalSent;
            _previousNetReceived = totalReceived;
            _hasPrevious = true;
        }
        catch { }
        return (0, 0);
    }


    public void Dispose()
    {
        _thermalSearcher?.Dispose();
        _fanSearcher?.Dispose();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}

public sealed record FanReading(string Name, int Rpm, bool Active);
