using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace BoardScout.Services;

/// <summary>Interrupt and DPC time one driver used during a measurement.</summary>
internal sealed record DriverInterruptLoad(
    string Driver, string? Description, long IsrCount, double IsrTotalMs, double IsrMaxUs,
    long DpcCount, double DpcTotalMs, double DpcMaxUs, IReadOnlyList<string> Devices)
{
    public double TotalMs => IsrTotalMs + DpcTotalMs;
}

internal sealed record InterruptProfile(
    double Seconds, int Cores, IReadOnlyList<DriverInterruptLoad> Drivers, long EventsLost, string? Error)
{
    public static InterruptProfile Failed(string error) => new(0, Environment.ProcessorCount, [], 0, error);
}

/// <summary>
/// Measures which drivers spend processor time in interrupt handlers (ISRs) and deferred procedure calls
/// (DPCs) — the work that delays audio and input when it runs long. Uses a private Windows kernel trace
/// session (the same data LatencyMon and xperf read), so it needs administrator rights. Nothing is saved
/// to disk; the trace runs for a few seconds and is summarized in memory.
/// </summary>
internal static class InterruptProfiler
{
    private const string SessionName = "BoardScout Interrupt Profile";
    private static readonly Guid SessionGuid = new("5B1E8E5A-7D3C-4A43-9F6E-3E0C2F6A9B21");
    private static readonly Guid SystemTraceControlGuid = new("9E814AAD-3204-11D2-9A82-006008A86939");
    private static readonly Guid PerfInfoGuid = new("CE1DBFB4-137E-4DA6-87B0-3F59AA102CBC");

    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint EventTraceSystemLoggerMode = 0x02000000;
    private const uint EventTraceFlagDpc = 0x00000020;
    private const uint EventTraceFlagInterrupt = 0x00000040;
    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeRawTimestamp = 0x00001000;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const uint ControlStop = 1;
    private const int ErrorAlreadyExists = 183;
    private const int PropertiesSize = 120;
    private const int LogFileSize = 448;

    private sealed class RoutineStats
    {
        public long IsrCount, DpcCount, IsrTicks, DpcTicks, IsrMax, DpcMax;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr record);

    public static Task<InterruptProfile> MeasureAsync(TimeSpan duration, DeviceNode? tree, CancellationToken token) =>
        Task.Run(() => Measure(duration, tree, token), token);

    private static InterruptProfile Measure(TimeSpan duration, DeviceNode? tree, CancellationToken token)
    {
        if (!SystemTelemetryService.IsElevated)
            return InterruptProfile.Failed("Measuring interrupt time per driver needs administrator rights.");
        var drivers = LoadedDrivers();
        if (drivers.Count == 0 || drivers.All(d => d.Base == 0))
            return InterruptProfile.Failed("Windows did not list driver addresses, so interrupt time can't be tied to drivers.");

        var stats = new Dictionary<long, RoutineStats>();
        var perfA = BitConverter.ToInt64(PerfInfoGuid.ToByteArray(), 0);
        var perfB = BitConverter.ToInt64(PerfInfoGuid.ToByteArray(), 8);
        void OnEvent(IntPtr record)
        {
            // EVENT_RECORD: EVENT_HEADER (Flags @4, TimeStamp @16, ProviderId @24, Opcode @45), then
            // BufferContext @80, UserDataLength @86, UserData @96.
            if (Marshal.ReadInt64(record, 24) != perfA || Marshal.ReadInt64(record, 32) != perfB) return;
            var opcode = Marshal.ReadByte(record, 45);
            var isr = opcode is 67 or 50;
            var dpc = opcode is 66 or 68 or 69;
            if (!isr && !dpc) return;
            var length = (ushort)Marshal.ReadInt16(record, 86);
            var data = Marshal.ReadIntPtr(record, 96);
            if (data == IntPtr.Zero || length < 12) return;
            var pointer64 = (Marshal.ReadInt16(record, 4) & 0x0020) == 0; // EVENT_HEADER_FLAG_32_BIT_HEADER
            var initial = Marshal.ReadInt64(data, 0);
            var routine = pointer64 && length >= 16 ? Marshal.ReadInt64(data, 8) : Marshal.ReadInt32(data, 8) & 0xFFFFFFFFL;
            var ticks = Marshal.ReadInt64(record, 16) - initial;
            if (ticks < 0 || ticks > 10_000_000_000) return;
            if (!stats.TryGetValue(routine, out var entry)) stats[routine] = entry = new RoutineStats();
            if (isr)
            {
                entry.IsrCount++;
                entry.IsrTicks += ticks;
                if (ticks > entry.IsrMax) entry.IsrMax = ticks;
            }
            else
            {
                entry.DpcCount++;
                entry.DpcTicks += ticks;
                if (ticks > entry.DpcMax) entry.DpcMax = ticks;
            }
        }

        var properties = AllocateProperties(systemLogger: true);
        var logfile = IntPtr.Zero;
        var name = Marshal.StringToHGlobalUni(SessionName);
        EventRecordCallback callback = OnEvent;
        ulong session = 0, trace = ulong.MaxValue;
        long lost = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = StartTraceW(out session, SessionName, properties);
            if (result == ErrorAlreadyExists)
            {
                // Left over from an earlier run that did not finish: stop it and start fresh.
                ControlTraceW(0, SessionName, properties, ControlStop);
                Marshal.FreeHGlobal(properties);
                properties = AllocateProperties(systemLogger: true);
                result = StartTraceW(out session, SessionName, properties);
            }
            if (result != 0)
                return InterruptProfile.Failed($"Windows would not start the interrupt trace (error {result}). Another tool may be tracing the kernel.");

            logfile = Marshal.AllocHGlobal(LogFileSize);
            for (var i = 0; i < LogFileSize; i += 8) Marshal.WriteInt64(logfile, i, 0);
            // EVENT_TRACE_LOGFILEW: LoggerName @8, ProcessTraceMode @28, EventRecordCallback @424.
            Marshal.WriteIntPtr(logfile, 8, name);
            Marshal.WriteInt32(logfile, 28, unchecked((int)(ProcessTraceModeRealTime | ProcessTraceModeEventRecord | ProcessTraceModeRawTimestamp)));
            Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(callback));
            trace = OpenTraceW(logfile);
            if (trace == ulong.MaxValue)
                return InterruptProfile.Failed($"Windows would not open the interrupt trace (error {Marshal.GetLastPInvokeError()}).");

            var handles = new[] { trace };
            var consumer = new Thread(() => ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "Interrupt trace" };
            watch.Restart();
            consumer.Start();
            token.WaitHandle.WaitOne(duration);
            ControlTraceW(session, null, properties, ControlStop);
            session = 0;
            consumer.Join(TimeSpan.FromSeconds(10));
            // EVENT_TRACE_PROPERTIES: EventsLost @88, RealTimeBuffersLost @100.
            lost = Marshal.ReadInt32(properties, 88) + Marshal.ReadInt32(properties, 100);
        }
        finally
        {
            if (session != 0) ControlTraceW(session, null, properties, ControlStop);
            if (trace != ulong.MaxValue) CloseTrace(trace);
            GC.KeepAlive(callback);
            if (logfile != IntPtr.Zero) Marshal.FreeHGlobal(logfile);
            Marshal.FreeHGlobal(properties);
            Marshal.FreeHGlobal(name);
        }
        token.ThrowIfCancellationRequested();
        return Summarize(stats, drivers, tree, watch.Elapsed.TotalSeconds, lost);
    }

    private static InterruptProfile Summarize(Dictionary<long, RoutineStats> stats, List<(long Base, string Name)> drivers,
        DeviceNode? tree, double seconds, long lost)
    {
        var frequency = (double)System.Diagnostics.Stopwatch.Frequency;
        var byDriver = new Dictionary<string, RoutineStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var (routine, entry) in stats)
        {
            var driver = DriverFor(routine, drivers) ?? "unknown";
            if (!byDriver.TryGetValue(driver, out var total)) byDriver[driver] = total = new RoutineStats();
            total.IsrCount += entry.IsrCount;
            total.IsrTicks += entry.IsrTicks;
            total.IsrMax = Math.Max(total.IsrMax, entry.IsrMax);
            total.DpcCount += entry.DpcCount;
            total.DpcTicks += entry.DpcTicks;
            total.DpcMax = Math.Max(total.DpcMax, entry.DpcMax);
        }

        var devices = DevicesByDriver(tree);
        var list = byDriver
            .Select(pair => new DriverInterruptLoad(
                pair.Key,
                Describe(pair.Key),
                pair.Value.IsrCount,
                Math.Round(pair.Value.IsrTicks * 1000 / frequency, 2),
                Math.Round(pair.Value.IsrMax * 1_000_000 / frequency, 1),
                pair.Value.DpcCount,
                Math.Round(pair.Value.DpcTicks * 1000 / frequency, 2),
                Math.Round(pair.Value.DpcMax * 1_000_000 / frequency, 1),
                devices.GetValueOrDefault(pair.Key) ?? []))
            .OrderByDescending(d => d.TotalMs)
            .ToList();
        return new InterruptProfile(Math.Round(seconds, 1), Environment.ProcessorCount, list, lost, null);
    }

    private static string? DriverFor(long address, List<(long Base, string Name)> drivers)
    {
        // Drivers are sorted by load address: the routine belongs to the last driver loaded at or below it.
        int low = 0, high = drivers.Count - 1, found = -1;
        var target = (ulong)address;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if ((ulong)drivers[mid].Base <= target) { found = mid; low = mid + 1; }
            else high = mid - 1;
        }
        return found >= 0 ? drivers[found].Name : null;
    }

    private static List<(long Base, string Name)> LoadedDrivers()
    {
        var result = new List<(long, string)>();
        EnumDeviceDrivers(null, 0, out var needed);
        if (needed <= 0) return result;
        var bases = new IntPtr[needed / IntPtr.Size];
        if (!EnumDeviceDrivers(bases, needed, out _)) return result;
        var name = new StringBuilder(260);
        foreach (var image in bases)
        {
            if (image == IntPtr.Zero) continue;
            name.Clear();
            if (GetDeviceDriverBaseNameW(image, name, name.Capacity) > 0) result.Add((image.ToInt64(), name.ToString()));
        }
        result.Sort((a, b) => ((ulong)a.Item1).CompareTo((ulong)b.Item1));
        return result;
    }

    // Driver file (lowercase) → the devices whose function driver it is, from the device tree.
    private static Dictionary<string, List<string>> DevicesByDriver(DeviceNode? tree)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (tree is null) return map;
        var images = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in tree.Descendants())
        {
            if (string.IsNullOrEmpty(node.Service) || node.Problem != 0) continue;
            if (!images.TryGetValue(node.Service, out var image))
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{node.Service}");
                    image = key?.GetValue("ImagePath") is string path ? Path.GetFileName(path.Trim('"')) : $"{node.Service}.sys";
                }
                catch
                {
                    image = $"{node.Service}.sys";
                }
                images[node.Service] = image;
            }
            if (image is null) continue;
            Add(image, node);
            // Shared layers run the interrupt code for the devices whose drivers plug into them.
            if (node.IsClass("Display")) Add("dxgkrnl.sys", node);
            else if (node.IsClass("Net") && (node.IdStarts(@"PCI\") || node.IdStarts(@"USB\"))) Add("ndis.sys", node);
            else if (node.IsClass("SCSIAdapter") && node.IdStarts(@"PCI\")) Add("storport.sys", node);
        }
        return map;

        void Add(string image, DeviceNode node)
        {
            if (!map.TryGetValue(image, out var names)) map[image] = names = [];
            var label = node.BusReportedName ?? node.Name;
            if (!names.Contains(label) && names.Count < 6) names.Add(label);
        }
    }

    private static string? Describe(string driver) => driver.ToLowerInvariant() switch
    {
        "ntoskrnl.exe" => "Windows kernel: timers and scheduling",
        "hal.dll" => "Hardware abstraction layer",
        "ndis.sys" => "Network adapters (shared network driver layer)",
        "tcpip.sys" => "TCP/IP network stack",
        "netio.sys" => "Network I/O",
        "storport.sys" => "Storage port driver (NVMe and SATA disks)",
        "stornvme.sys" => "NVMe drives",
        "storahci.sys" => "SATA (AHCI) drives",
        "classpnp.sys" or "disk.sys" => "Disk class driver",
        "usbxhci.sys" => "USB 3 controllers",
        "ucx01000.sys" => "USB host controller extension",
        "usbhub3.sys" => "USB hubs",
        "usbport.sys" => "USB 2 controllers",
        "dxgkrnl.sys" => "DirectX graphics kernel",
        "nvlddmkm.sys" => "NVIDIA graphics driver",
        "amdkmdag.sys" or "atikmdag.sys" or "amdkmdap.sys" => "AMD graphics driver",
        "igdkmd64.sys" or "igdkmdn64.sys" => "Intel graphics driver",
        "hdaudbus.sys" => "HD Audio bus",
        "portcls.sys" => "Windows audio",
        "wdf01000.sys" => "Kernel driver framework (used by many devices)",
        "acpi.sys" => "ACPI: power and motherboard devices",
        "i8042prt.sys" => "PS/2 keyboard and mouse",
        "kbdclass.sys" or "mouclass.sys" => "Keyboard and mouse",
        "hidclass.sys" or "hidusb.sys" => "USB input devices",
        "bthport.sys" or "bthusb.sys" => "Bluetooth",
        "wfplwfs.sys" => "Windows Filtering Platform (firewall)",
        _ when driver.StartsWith("rt", StringComparison.OrdinalIgnoreCase) && driver.Contains("x64", StringComparison.OrdinalIgnoreCase) => "Realtek network driver",
        _ when driver.StartsWith("Netwtw", StringComparison.OrdinalIgnoreCase) => "Intel Wi-Fi driver",
        _ when driver.StartsWith("e1", StringComparison.OrdinalIgnoreCase) => "Intel Ethernet driver",
        _ => null
    };

    private static IntPtr AllocateProperties(bool systemLogger)
    {
        var size = PropertiesSize + 1024;
        var properties = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i += 8) Marshal.WriteInt64(properties, i, 0);
        // WNODE_HEADER: BufferSize @0, Guid @24, ClientContext @40 (1 = QPC timestamps), Flags @44.
        Marshal.WriteInt32(properties, 0, size);
        var guid = (systemLogger ? SessionGuid : SystemTraceControlGuid).ToByteArray();
        Marshal.Copy(guid, 0, properties + 24, 16);
        Marshal.WriteInt32(properties, 40, 1);
        Marshal.WriteInt32(properties, 44, unchecked((int)WnodeFlagTracedGuid));
        // BufferSize (KB) @48, MinimumBuffers @52, MaximumBuffers @56, LogFileMode @64, FlushTimer @68,
        // EnableFlags @72, LoggerNameOffset @116.
        Marshal.WriteInt32(properties, 48, 256);
        Marshal.WriteInt32(properties, 52, 32);
        Marshal.WriteInt32(properties, 56, 128);
        Marshal.WriteInt32(properties, 64, unchecked((int)(EventTraceRealTimeMode | (systemLogger ? EventTraceSystemLoggerMode : 0))));
        Marshal.WriteInt32(properties, 68, 1);
        Marshal.WriteInt32(properties, 72, unchecked((int)(EventTraceFlagDpc | EventTraceFlagInterrupt)));
        Marshal.WriteInt32(properties, 116, PropertiesSize);
        return properties;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out ulong session, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong session, string? name, IntPtr properties, uint code);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong[] handles, int count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong handle);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDeviceDrivers(IntPtr[]? images, int size, out int needed);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    private static extern int GetDeviceDriverBaseNameW(IntPtr image, StringBuilder name, int size);
}
