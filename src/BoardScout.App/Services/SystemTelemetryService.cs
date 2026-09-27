using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using BoardScout.Models;
using LibreHardwareMonitor.Hardware;

namespace BoardScout.Services;

/// <summary>
/// Samples CPU, memory, sensor, and network activity. <see cref="Sample"/> blocks (the first call
/// opens the sensor driver, later calls read hardware registers), so it belongs on a background
/// thread. Calls are serialized: the sensor library is never used from two threads at once.
/// </summary>
internal sealed class SystemTelemetryService : IDisposable
{
    // Hardware that has not reported a temperature or fan after this many passes is skipped from
    // then on; updating it cost ~80 ms every second for nothing on machines without PawnIO.
    private const int SensorDiscoveryPasses = 3;
    private static readonly TimeSpan InterfaceRefresh = TimeSpan.FromSeconds(15);
    private static readonly Lazy<(bool Installed, string? Version)> PawnIo = new(ReadPawnIo);

    private readonly object _gate = new();
    private ulong? _previousIdle;
    private ulong? _previousKernel;
    private ulong? _previousUser;
    private double _lastCpuUsage;

    private NetworkInterface[] _interfaces = [];
    private DateTime _interfacesReadUtc = DateTime.MinValue;
    private long _previousNetSent;
    private long _previousNetReceived;
    private bool _hasNetworkBaseline;
    private DateTime _previousSampleUtc = DateTime.UtcNow;
    private readonly Dictionary<string, (long Sent, long Received)> _interfaceCounters = [];
    private readonly Dictionary<int, (long Read, long Written)> _diskCounters = [];
    private int[] _diskNumbers = [];
    private DateTime _disksReadUtc = DateTime.MinValue;

    private Computer? _computer;
    private bool _sensorsFailed;
    private int _discoveryPasses;
    private readonly HashSet<IHardware> _sensorHardware = [];
    private IHardware[]? _lockedSensorHardware;
    private bool _disposed;

    public static bool IsElevated { get; } = CheckElevated();

    public SensorStatus SensorStatus { get; private set; } = SensorStatus.Pending;

    /// <summary>When set, samples also carry per-adapter and per-disk rates for the Connections view.</summary>
    public bool DetailedRates { get; set; }

    public SystemTelemetry Sample()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = DateTime.UtcNow;
            var elapsed = Math.Max(0.05, (now - _previousSampleUtc).TotalSeconds);
            _previousSampleUtc = now;
            var detailed = DetailedRates;

            var cpuUsage = SampleCpu();
            var (memTotal, memAvailable) = SampleMemory();
            var (thermals, fans) = SampleSensors();
            var (netSent, netReceived, interfaceRates) = SampleNetwork(now, elapsed, detailed);

            return new SystemTelemetry(
                cpuUsage, memTotal, memAvailable,
                thermals, fans,
                netSent, netReceived,
                DateTimeOffset.Now)
            {
                InterfaceRates = interfaceRates,
                DiskRates = SampleDisks(now, elapsed, detailed)
            };
        }
    }

    // Cumulative byte counters from each physical disk, read through zero-access handles (no admin needed).
    private Dictionary<int, LinkRate>? SampleDisks(DateTime now, double elapsed, bool detailed)
    {
        if (!detailed)
        {
            _diskCounters.Clear();
            return null;
        }
        if (now - _disksReadUtc > InterfaceRefresh)
        {
            try { _diskNumbers = [.. DeviceTree.DiskNumbers().Values.Distinct().Order()]; } catch { _diskNumbers = []; }
            _disksReadUtc = now;
        }

        var rates = new Dictionary<int, LinkRate>();
        foreach (var number in _diskNumbers)
        {
            if (DeviceTree.DiskCounters(number) is not { } counters) continue;
            if (_diskCounters.TryGetValue(number, out var previous) && counters.Read >= previous.Read && counters.Written >= previous.Written)
                rates[number] = new LinkRate((counters.Read - previous.Read) / elapsed, (counters.Written - previous.Written) / elapsed);
            _diskCounters[number] = counters;
        }
        return rates;
    }

    private (List<ThermalReading> Thermals, List<FanReading> Fans) SampleSensors()
    {
        var thermals = new List<ThermalReading>();
        var fans = new List<FanReading>();
        if (_sensorsFailed) return (thermals, fans);

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

            IEnumerable<IHardware> hardware = _lockedSensorHardware ?? (IEnumerable<IHardware>)_computer.Hardware;
            foreach (var hw in hardware)
            {
                var before = thermals.Count + fans.Count;
                hw.Update();
                CollectSensors(hw, thermals, fans);
                foreach (var sub in hw.SubHardware)
                {
                    sub.Update();
                    CollectSensors(sub, thermals, fans);
                }
                if (_lockedSensorHardware is null && thermals.Count + fans.Count > before)
                    _sensorHardware.Add(hw);
            }

            if (_lockedSensorHardware is null && ++_discoveryPasses >= SensorDiscoveryPasses)
                _lockedSensorHardware = [.. _sensorHardware];

            UpdateStatus(thermals.Count, fans.Count, null);
        }
        catch (Exception ex)
        {
            _sensorsFailed = true;
            UpdateStatus(0, 0, ex.Message);
        }
        return (thermals, fans);
    }

    private void UpdateStatus(int temperatureZones, int fans, string? error)
    {
        var (installed, version) = PawnIo.Value;
        SensorStatus = new SensorStatus(true, IsElevated, installed, version, temperatureZones, fans, error);
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
                    fans.Add(new FanReading(ClassifyFan(hw, sensor), rpm, rpm > 0));
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

    private (double SentBytesPerSec, double ReceivedBytesPerSec, Dictionary<string, LinkRate>? PerInterface) SampleNetwork(
        DateTime now, double elapsed, bool detailed)
    {
        try
        {
            // Enumerating adapters is the slow part (~30 ms); their byte counters are cheap to re-read.
            if (now - _interfacesReadUtc > InterfaceRefresh)
            {
                // Packet-filter layers (WFP, QoS) enumerate as extra "Up" adapters that repeat the real
                // adapter's byte counters; they have no IP addresses, so requiring one skips them.
                _interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up &&
                                i.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                                HasAddress(i))
                    .ToArray();
                _interfacesReadUtc = now;
                _hasNetworkBaseline = false; // a changed adapter set would otherwise read as one huge spike
            }

            long totalSent = 0, totalReceived = 0;
            var perInterface = detailed ? new Dictionary<string, LinkRate>() : null;
            if (!detailed) _interfaceCounters.Clear();
            foreach (var iface in _interfaces)
            {
                var stats = iface.GetIPStatistics();
                totalSent += stats.BytesSent;
                totalReceived += stats.BytesReceived;
                if (perInterface is null) continue;
                if (_interfaceCounters.TryGetValue(iface.Id, out var previous))
                    perInterface[iface.Id] = new LinkRate(
                        Math.Max(0, stats.BytesReceived - previous.Received) / elapsed,
                        Math.Max(0, stats.BytesSent - previous.Sent) / elapsed);
                _interfaceCounters[iface.Id] = (stats.BytesSent, stats.BytesReceived);
            }

            var rates = _hasNetworkBaseline
                ? (Math.Max(0, totalSent - _previousNetSent) / elapsed,
                   Math.Max(0, totalReceived - _previousNetReceived) / elapsed)
                : (0d, 0d);
            _previousNetSent = totalSent;
            _previousNetReceived = totalReceived;
            _hasNetworkBaseline = true;
            return (rates.Item1, rates.Item2, perInterface);
        }
        catch
        {
            _interfacesReadUtc = DateTime.MinValue;
            return (0, 0, null);
        }
    }

    private static bool HasAddress(NetworkInterface nic)
    {
        try { return nic.GetIPProperties().UnicastAddresses.Count > 0; }
        catch (NetworkInformationException) { return false; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }

    private static (bool Installed, string? Version) ReadPawnIo()
    {
        try
        {
            var installed = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
            return (installed, installed ? LibreHardwareMonitor.PawnIo.PawnIo.Version?.ToString() : null);
        }
        catch
        {
            return (false, null);
        }
    }

    private static bool CheckElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
