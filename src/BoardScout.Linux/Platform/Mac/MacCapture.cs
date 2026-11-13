using System.Diagnostics;
using System.Runtime.InteropServices;
using BoardScout.Services;

namespace BoardScout.Platform.Mac;

/// <summary>
/// The Mac half of the hardware read: runs Apple's own read-only tools (system_profiler, sysctl, diskutil, df,
/// mount, ifconfig, netstat, arp, scutil, vm_stat, ioreg, sw_vers) and turns their output into the same records the
/// Linux edition builds from /sys, so every view works unchanged. Nothing here needs sudo. Not yet run on a Mac.
/// </summary>
internal static partial class MacCapture
{
    // Hardware, memory, drives, USB, screens, network services, Bluetooth: one system_profiler run.
    // SPUSBHostDataType is the newer USB list; older macOS versions only know SPUSBDataType and skip the other.
    private static readonly string[] ProfilerTypes =
    [
        "SPHardwareDataType", "SPMemoryDataType", "SPNVMeDataType", "SPSerialATADataType", "SPUSBDataType", "SPUSBHostDataType",
        "SPDisplaysDataType", "SPNetworkDataType", "SPBluetoothDataType"
    ];

    public static HardwareCapture Read()
    {
        var watch = Stopwatch.StartNew();
        var network = Task.Run(NetworkProbe.Capture);
        var profiler = Task.Run(() => Tools.Run("system_profiler", ["-json", .. ProfilerTypes], 90_000)
                                      ?? Tools.Run("system_profiler", ["-json", .. ProfilerTypes.Where(t => t != "SPUSBHostDataType")], 90_000));
        var sysctl = Tools.Run("sysctl", "hw", "machdep", "kern.osrelease", "kern.osproductversion", "kern.boottime", "vm.swapusage");
        var diskList = Tools.Run("diskutil", "list", "-plist");
        var info = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var disk in (Plist.Dict(diskList)?.List("WholeDisks") ?? []).OfType<string>())
            if (Tools.Run("diskutil", ["info", "-plist", disk], 10_000) is { } text) info[disk] = text;
        var outputs = new MacOutputs(profiler.GetAwaiter().GetResult(), sysctl, diskList, info,
            Tools.Run("df", "-kP"), Tools.Run("mount"), Tools.Run("ifconfig", "-a"), Tools.Run("sw_vers"));

        NetworkFacts facts;
        try
        {
            facts = network.GetAwaiter().GetResult();
        }
        catch
        {
            facts = new NetworkFacts([], new Dictionary<string, RouterFacts>(), new Dictionary<string, DnsFacts>(), [], null);
        }
        return Build(outputs, facts, Environment.UserName == "root", watch.ElapsedMilliseconds);
    }

    /// <summary>Everything the map needs from the tools' output; the tests feed it sample output.</summary>
    public static HardwareCapture Build(MacOutputs o, NetworkFacts network, bool root, long ms)
    {
        var profiler = MacParse.Profiler(o.Profiler);
        var sysctl = MacParse.Sysctl(o.Sysctl);
        var hardware = MacParse.First(MacParse.Section(profiler, "SPHardwareDataType"));
        var identity = MacParse.Identity(hardware);
        var cpu = MacParse.Cpu(sysctl, hardware);
        var (usb, diskOwners) = MacParse.Usb(profiler);
        var disks = MacParse.Disks(o.DiskList, o.DiskInfo, profiler, diskOwners);
        var volumes = MacParse.Volumes(o.Df, o.Mount, o.DiskList, disks);
        var links = MacParse.Links(o.Ifconfig, profiler);
        var memory = MacParse.Memory(MacParse.First(MacParse.Section(profiler, "SPMemoryDataType")), identity);
        var arm = cpu.Architecture == "aarch64";
        // Apple silicon boots through iBoot with its own secure boot chain; Intel Macs use EFI. Macs have no TPM
        // (the Secure Enclave does that job), and reading the boot security level needs root (bputil).
        var security = new SecurityFacts(true, null, null, arm ? "Apple Secure Enclave" : null);
        var usbNames = UsbIds.Resolve(usb.SelectMany(b => b.RootHub.Descendants()).Select(d => (d.Vendor, d.Product)));
        var kernel = sysctl.GetValueOrDefault("kern.osrelease") is { } release ? $"Darwin {release}" : "Darwin";
        return new HardwareCapture(cpu, identity, security, memory, [], usb, disks, volumes, links, MacParse.Displays(profiler), [], [],
            MacParse.Bluetooth(profiler), network, usbNames, Firmware.FormFactor(identity, null), null, kernel, root, ms, DateTimeOffset.Now);
    }

    // ---- Live counters ---------------------------------------------------------------------------------------

    private static uint _host;
    private static ulong _memTotal;

    /// <summary>
    /// Processor time since boot from the Mach host (HOST_CPU_LOAD_INFO: user, system, idle, nice ticks), under
    /// the "cpu" key the Linux sampler uses.
    /// </summary>
    public static unsafe Dictionary<string, CpuTimes> CpuTimes()
    {
        var result = new Dictionary<string, CpuTimes>(StringComparer.Ordinal);
        try
        {
            if (_host == 0) _host = mach_host_self();
            var ticks = stackalloc uint[4];
            uint count = 4;
            if (host_statistics(_host, 3, ticks, ref count) == 0)
                result["cpu"] = new CpuTimes(ticks[0], ticks[3], ticks[1], ticks[2], 0, 0, 0, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return result;
    }

    public static (ulong Total, ulong Available, ulong SwapTotal, ulong SwapFree) Memory()
    {
        if (_memTotal == 0 && Tools.Run("sysctl", "-n", "hw.memsize")?.Trim() is { } text && ulong.TryParse(text, out var total)) _memTotal = total;
        var (available, _) = MacParse.VmStat(Tools.Run("vm_stat"));
        var (swapTotal, swapFree) = MacParse.SwapUsage(Tools.Run("sysctl", "-n", "vm.swapusage"));
        return (_memTotal, Math.Min(available, _memTotal == 0 ? available : _memTotal), swapTotal, swapFree);
    }

    public static Dictionary<string, NetCounters> NetCounters() => MacParse.NetstatCounters(Tools.Run("netstat", "-ibn"));

    public static Dictionary<string, DiskCounters> DiskCounters() =>
        MacParse.IoregDiskStats(Tools.Run("ioreg", "-r", "-c", "IOBlockStorageDriver", "-a", "-d", "2"));

    /// <summary>A cheap fingerprint of what's attached: drives and interfaces (USB changes show on the next map).</summary>
    public static string DeviceSignature()
    {
        string Safe(Func<IEnumerable<string>> read)
        {
            try { return string.Join(",", read()); }
            catch { return ""; }
        }
        return string.Join("|",
            Safe(() => Directory.EnumerateFiles("/dev", "disk*").Order(StringComparer.Ordinal)),
            Safe(() => Directory.EnumerateDirectories("/Volumes").Order(StringComparer.Ordinal)),
            Safe(() => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Select(n => n.Name + ":" + n.OperationalStatus)));
    }

    public static TimeSpan? Uptime() =>
        MacParse.BootTime(Tools.Run("sysctl", "-n", "kern.boottime")) is { } boot ? DateTimeOffset.Now - boot : null;

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial uint mach_host_self();

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static unsafe partial int host_statistics(uint host, int flavor, uint* info, ref uint count);
}
