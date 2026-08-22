using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using BoardScout.Models;
using LibreHardwareMonitor.Hardware;

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

    private Computer? _computer;
    private bool _lhmFailed;

    public IReadOnlyList<FanReading> LastFanReadings { get; private set; } = [];

    public SystemTelemetry Sample()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _previousSampleTime).TotalSeconds;
        if (elapsed < 0.01) elapsed = 1;

        var cpuUsage = SampleCpu();
        var (memTotal, memAvailable) = SampleMemory();
        var (thermals, fans) = SampleSensors();
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

    private (List<ThermalReading> Thermals, List<FanReading> Fans) SampleSensors()
    {
        if (_lhmFailed) return ([], []);

        try
        {
            if (_computer is null)
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsGpuEnabled = true
                };
                _computer.Open();
            }

            var thermals = new List<ThermalReading>();
            var fans = new List<FanReading>();

            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                foreach (var sub in hardware.SubHardware)
                    sub.Update();

                CollectSensors(hardware, thermals, fans);
                foreach (var sub in hardware.SubHardware)
                    CollectSensors(sub, thermals, fans);
            }

            return (thermals, fans);
        }
        catch
        {
            _lhmFailed = true;
            return ([], []);
        }
    }

    private static void CollectSensors(IHardware hw, List<ThermalReading> thermals, List<FanReading> fans)
    {
        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Value is null) continue;

            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                {
                    var temp = sensor.Value.Value;
                    if (temp is <= 0 or > 150) break;
                    var zone = ClassifyThermalZone(hw, sensor);
                    if (!thermals.Exists(t => t.Zone == zone))
                        thermals.Add(new ThermalReading(zone, Math.Round(temp, 1)));
                    break;
                }
                case SensorType.Fan:
                {
                    var rpm = (int)sensor.Value.Value;
                    var name = ClassifyFan(hw, sensor);
                    fans.Add(new FanReading(name, rpm, rpm > 0));
                    break;
                }
            }
        }
    }

    private static string ClassifyThermalZone(IHardware hw, ISensor sensor)
    {
        var name = sensor.Name;
        if (hw.HardwareType is HardwareType.Cpu)
        {
            if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                return "CPU";
            if (name.Contains("CCD", StringComparison.OrdinalIgnoreCase))
                return name;
            return $"CPU {name}";
        }
        if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            return "GPU";
        if (name.Contains("VRM", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Voltage Regulator", StringComparison.OrdinalIgnoreCase))
            return "VRM";
        if (name.Contains("Chipset", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("PCH", StringComparison.OrdinalIgnoreCase))
            return "Chipset";
        if (name.Contains("System", StringComparison.OrdinalIgnoreCase))
            return "System";
        return name;
    }

    private static string ClassifyFan(IHardware hw, ISensor sensor)
    {
        var name = sensor.Name;
        if (name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            return "CPU Fan";
        if (name.Contains("Chassis", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("System", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Case", StringComparison.OrdinalIgnoreCase))
            return name;
        if (name.StartsWith("Fan #", StringComparison.OrdinalIgnoreCase))
            return name;
        if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            return "GPU Fan";
        return name;
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
        if (_computer is not null)
        {
            _computer.Close();
            _computer = null;
        }
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
