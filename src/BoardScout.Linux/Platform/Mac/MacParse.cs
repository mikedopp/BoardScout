using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoardScout.Services;

namespace BoardScout.Platform.Mac;

/// <summary>
/// What the Mac's own tools printed for one map. Each field is the raw text of one command, so the parsing below
/// runs (and is tested) anywhere; MacCapture runs the commands on a Mac.
/// </summary>
internal sealed record MacOutputs(
    string? Profiler,
    string? Sysctl,
    string? DiskList,
    IReadOnlyDictionary<string, string> DiskInfo,
    string? Df,
    string? Mount,
    string? Ifconfig,
    string? SwVers);

/// <summary>
/// Parsers for macOS command output: system_profiler -json, sysctl, diskutil -plist, df -kP, mount, ifconfig,
/// netstat -ibn, netstat -rn, arp -an, scutil --dns, vm_stat, ioreg -a and sw_vers. Written from Apple's
/// documented formats and published sample output; not yet run on a Mac.
/// </summary>
internal static partial class MacParse
{
    // Every pattern here reads a local tool's output; a time limit still keeps a strange line from stalling a map.
    private static readonly TimeSpan RegexLimit = TimeSpan.FromSeconds(1);

    // ---- sysctl, sw_vers, vm_stat ---------------------------------------------------------------------------

    /// <summary>"hw.memsize: 17179869184" lines (also "hw.memsize = 17179869184") into a dictionary; the first value wins.</summary>
    public static Dictionary<string, string> Sysctl(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            var equals = line.IndexOf(" = ", StringComparison.Ordinal);
            var cut = colon > 0 && (equals < 0 || colon < equals) ? colon : equals;
            if (cut <= 0) continue;
            var key = line[..cut].Trim();
            if (key.Contains(' ')) continue;
            result.TryAdd(key, line[(cut + (cut == colon ? 2 : 3))..].Trim());
        }
        return result;
    }

    public static long? SysLong(this Dictionary<string, string> s, string key) =>
        s.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>"ProductName: macOS / ProductVersion: 15.5 / BuildVersion: 24F74".</summary>
    public static (string Name, string Version, string? Build) SwVers(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (text ?? "").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return (values.GetValueOrDefault("ProductName") ?? "macOS", values.GetValueOrDefault("ProductVersion") ?? "", values.GetValueOrDefault("BuildVersion"));
    }

    /// <summary>Apple's name for a macOS release, from its major version.</summary>
    public static string? MarketingName(string version)
    {
        var parts = version.Split('.');
        if (!int.TryParse(parts[0], out var major)) return null;
        if (major == 10 && parts.Length > 1 && int.TryParse(parts[1], out var minor))
            return minor switch { 13 => "High Sierra", 14 => "Mojave", 15 => "Catalina", _ => null };
        return major switch { 11 => "Big Sur", 12 => "Monterey", 13 => "Ventura", 14 => "Sonoma", 15 => "Sequoia", 26 => "Tahoe", _ => null };
    }

    /// <summary>
    /// Memory from vm_stat: what is free plus what macOS can take back at once (inactive pages), as psutil counts
    /// "available" on a Mac.
    /// </summary>
    public static (ulong Available, ulong PageSize) VmStat(string? text)
    {
        var page = 4096UL;
        ulong free = 0, inactive = 0;
        foreach (var line in (text ?? "").Split('\n'))
        {
            var size = PageSize().Match(line);
            if (size.Success) { page = ulong.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim().Trim('"');
            if (!ulong.TryParse(line[(colon + 1)..].Trim().TrimEnd('.'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages)) continue;
            if (key == "Pages free") free = pages;
            else if (key == "Pages inactive") inactive = pages;
        }
        return ((free + inactive) * page, page);
    }

    /// <summary>"total = 4096.00M  used = 2559.19M  free = 1536.81M  (encrypted)" → bytes.</summary>
    public static (ulong Total, ulong Free) SwapUsage(string? text)
    {
        static ulong Mb(Match m) => m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (ulong)(v * 1_048_576) : 0;
        var line = text ?? "";
        return (Mb(Regex.Match(line, @"total = ([\d.]+)M", RegexOptions.None, RegexLimit)), Mb(Regex.Match(line, @"free = ([\d.]+)M", RegexOptions.None, RegexLimit)));
    }

    /// <summary>kern.boottime: "{ sec = 1731400000, usec = 123 } Tue Nov 12 …".</summary>
    public static DateTimeOffset? BootTime(string? text) =>
        text is not null && Regex.Match(text, @"sec = (\d+)", RegexOptions.None, RegexLimit) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var sec)
            ? DateTimeOffset.FromUnixTimeSeconds(sec) : null;

    // ---- CPU, identity, memory ------------------------------------------------------------------------------

    public static CpuFacts Cpu(Dictionary<string, string> sysctl, JsonElement? hardware)
    {
        var arm = sysctl.GetValueOrDefault("hw.optional.arm64") == "1" || sysctl.GetValueOrDefault("hw.cputype") == "16777228";
        var name = sysctl.GetValueOrDefault("machdep.cpu.brand_string") ?? Str(hardware, "chip_type") ?? Str(hardware, "cpu_type") ?? (arm ? "Apple silicon" : "Intel processor");
        var mhz = sysctl.SysLong("hw.cpufrequency_max") is { } hz and > 0 ? hz / 1_000_000d : (double?)null;
        var microcode = sysctl.GetValueOrDefault("machdep.cpu.microcode_version") is { Length: > 0 } mc && long.TryParse(mc, out var mcv) ? $"0x{mcv:x}" : null;
        return new CpuFacts(
            name.Trim(),
            sysctl.GetValueOrDefault("machdep.cpu.vendor") ?? (arm ? "Apple" : "GenuineIntel"),
            (int)(sysctl.SysLong("hw.packages") ?? 1),
            (int)(sysctl.SysLong("hw.physicalcpu") ?? sysctl.SysLong("machdep.cpu.core_count") ?? Environment.ProcessorCount),
            (int)(sysctl.SysLong("hw.logicalcpu") ?? sysctl.SysLong("machdep.cpu.thread_count") ?? Environment.ProcessorCount),
            microcode,
            mhz,
            null,
            null,
            null,
            arm ? "aarch64" : "x86_64",
            []);
    }

    /// <summary>"8 Performance + 4 Efficiency cores" on Apple silicon, from hw.perflevelN.</summary>
    public static string? CoreMix(Dictionary<string, string> sysctl)
    {
        var parts = new List<string>();
        for (var level = 0; level < 4; level++)
        {
            if (sysctl.SysLong($"hw.perflevel{level}.physicalcpu") is not { } count || sysctl.GetValueOrDefault($"hw.perflevel{level}.name") is not { } name) continue;
            parts.Add($"{count} {name}");
        }
        return parts.Count > 1 ? string.Join(" + ", parts) + " cores" : null;
    }

    public static MachineIdentity Identity(JsonElement? hardware)
    {
        var model = Str(hardware, "machine_model");
        var name = Str(hardware, "machine_name") ?? "Mac";
        var chip = Str(hardware, "chip_type") ?? Str(hardware, "cpu_type");
        var chassis = name switch
        {
            _ when name.StartsWith("MacBook", StringComparison.Ordinal) => 10,
            _ when name.StartsWith("iMac", StringComparison.Ordinal) => 13,
            _ when name.StartsWith("Mac mini", StringComparison.Ordinal) => 35,
            _ when model?.StartsWith("VirtualMac", StringComparison.Ordinal) == true => 1,
            _ => 3
        };
        // Apple's boot ROM stands where a PC's BIOS would; Macs have no separate board maker or BIOS date.
        return new MachineIdentity("Apple", model?.StartsWith("VirtualMac", StringComparison.Ordinal) == true ? "Virtual Machine" : name, model,
            "Apple", chip ?? model, null, "Apple", Str(hardware, "boot_rom_version"), null, chassis);
    }

    /// <summary>
    /// Memory from SPMemoryDataType. Apple silicon reports one block of unified memory in the chip package; Intel
    /// Macs list each slot ("BANK 0/ChannelA-DIMM0").
    /// </summary>
    public static MemoryFacts Memory(JsonElement? memory, MachineIdentity identity)
    {
        var slots = new List<DimmSlot>();
        var makers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (memory is { } m)
        {
            if (m.TryGetProperty("_items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var label = Str(item, "_name") ?? $"Slot {slots.Count + 1}";
                    var slash = label.IndexOf('/');
                    var size = SizeMb(Str(item, "dimm_size"));
                    var speed = (int)(Number(Str(item, "dimm_speed")) ?? 0);
                    var slot = new DimmSlot(slash > 0 ? label[(slash + 1)..] : label, slash > 0 ? label[..slash] : "", size, speed, speed, 0,
                        Clean(Str(item, "dimm_part_number")), Str(item, "dimm_type") ?? "DDR");
                    slots.Add(slot);
                    if (size > 0 && Clean(Str(item, "dimm_manufacturer")) is { } maker) makers[slot.DeviceLocator + "|" + slot.BankLocator] = Firmware.JedecMaker(maker) ?? maker;
                }
            }
            else if (Str(m, "SPMemoryDataType") is { } total)
            {
                var slot = new DimmSlot("Unified memory", "In the chip package", SizeMb(total), 0, 0, 0, null, Str(m, "dimm_type") ?? "LPDDR");
                slots.Add(slot);
                if (Clean(Str(m, "dimm_manufacturer")) is { } maker) makers[slot.DeviceLocator + "|" + slot.BankLocator] = maker;
            }
        }
        return new MemoryFacts(new FirmwareFacts(slots, slots.Count, identity.ChassisType, identity.SystemVendor, identity.ProductName),
            makers, slots.Count > 0 ? "system_profiler (SPMemoryDataType)" : "not available");
    }

    // ---- Drives and volumes ---------------------------------------------------------------------------------

    /// <summary>
    /// Physical drives from diskutil: the whole disks that aren't APFS containers (those are synthesized on top of
    /// a partition) or disk images. Firmware revisions come from system_profiler's NVMe and SATA lists.
    /// </summary>
    public static List<BlockDisk> Disks(string? diskList, IReadOnlyDictionary<string, string> diskInfo, JsonElement? profiler,
        IReadOnlyDictionary<string, string> usbPathByBsd)
    {
        var list = Plist.Dict(diskList);
        var containers = ContainerStores(list);
        var partitions = new Dictionary<string, List<BlockPartition>>(StringComparer.Ordinal);
        foreach (var entry in list?.List("AllDisksAndPartitions").OfType<Dictionary<string, object?>>() ?? [])
            if (entry.Str("DeviceIdentifier") is { } id)
                partitions[id] = entry.List("Partitions").OfType<Dictionary<string, object?>>()
                    .Select(p => new BlockPartition(p.Str("DeviceIdentifier") ?? "", "", p.Long("Size") ?? 0)).Where(p => p.Name.Length > 0).ToList();
        var firmware = DriveFirmware(profiler);

        var disks = new List<BlockDisk>();
        var whole = list?.List("WholeDisks").OfType<string>().ToList() ?? diskInfo.Keys.Where(k => WholeDisk().IsMatch(k)).ToList();
        foreach (var name in whole.OrderBy(n => int.TryParse(n.AsSpan(4), out var i) ? i : int.MaxValue))
        {
            if (containers.ContainsKey(name)) continue;
            var info = Plist.Dict(diskInfo.GetValueOrDefault(name));
            if (info is null) continue;
            var protocol = info.Str("BusProtocol") ?? "";
            if (protocol == "Disk Image" || info.Str("VirtualOrPhysical") == "Virtual") continue;
            var transport = protocol switch
            {
                "Apple Fabric" or "PCI-Express" or "PCI" or "NVMExpress" => "nvme",
                "SATA" => "sata",
                "USB" => "usb",
                "Secure Digital" => "mmc",
                "Thunderbolt" => "nvme",
                "SAS" or "Fibre Channel Interface" => "scsi",
                _ when info.Str("MediaType")?.Contains("DVD", StringComparison.OrdinalIgnoreCase) == true || protocol == "ATAPI" => "optical",
                _ => "scsi"
            };
            var solid = info.Bool("SolidState");
            var internalDisk = info.Bool("Internal") == true;
            var usbPath = usbPathByBsd.GetValueOrDefault(name);
            var disk = new BlockDisk
            {
                Name = name,
                SysPath = usbPath is not null ? $"{usbPath}/{name}" : $"/dev/{name}",
                DevicePath = info.Str("DeviceNode") ?? $"/dev/{name}",
                SizeBytes = info.Long("TotalSize") ?? info.Long("Size") ?? info.Long("IOKitSize") ?? 0,
                Rotational = solid == false || (solid is null && transport is "sata" or "usb" or "scsi"),
                Removable = !internalDisk && (info.Bool("Removable") == true || info.Bool("RemovableMedia") == true || info.Bool("Ejectable") == true ||
                                              info.Bool("RemovableMediaOrExternalDevice") == true),
                Model = Clean(info.Str("MediaName")) ?? Clean(info.Str("IORegistryEntryName")?.Replace(" Media", "", StringComparison.Ordinal)),
                Firmware = firmware.GetValueOrDefault(name),
                Transport = transport,
                UsbPath = usbPath,
                UsbDriver = transport == "usb" ? "uas" : null,
                Index = disks.Count
            };
            disk.Partitions.AddRange(partitions.GetValueOrDefault(name) ?? []);
            disks.Add(disk);
        }
        return disks;
    }

    /// <summary>APFS containers (disk3) and the partitions they live on (disk0s2), from diskutil list -plist.</summary>
    public static Dictionary<string, List<string>> ContainerStores(Dictionary<string, object?>? list)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entry in list?.List("AllDisksAndPartitions").OfType<Dictionary<string, object?>>() ?? [])
        {
            var stores = entry.List("APFSPhysicalStores").OfType<Dictionary<string, object?>>().Select(s => s.Str("DeviceIdentifier")).OfType<string>().ToList();
            if (entry.Str("DeviceIdentifier") is { } id && (stores.Count > 0 || entry.Str("Content") == "Apple_APFS_Container")) result[id] = stores;
        }
        return result;
    }

    private static Dictionary<string, string> DriveFirmware(JsonElement? profiler)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in new[] { "SPNVMeDataType", "SPSerialATADataType" })
            foreach (var item in Walk(Section(profiler, type)))
                if (Str(item, "bsd_name") is { } bsd && Str(item, "device_revision") is { } revision) result[bsd] = revision.TrimEnd('.');
        return result;
    }

    /// <summary>
    /// Mounted volumes from df -kP and mount: the system volume, its Data volume, and every drive under /Volumes.
    /// The other /System/Volumes (VM, Preboot, Update…) are macOS's own and share the same space.
    /// </summary>
    public static List<Volume> Volumes(string? df, string? mount, string? diskList, IReadOnlyList<BlockDisk> disks)
    {
        var mounts = BoardScout.StorageScan.Scanner.ParseMacMounts(mount ?? "").GroupBy(m => m.MountPoint).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var containers = ContainerStores(Plist.Dict(diskList));
        var known = disks.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var result = new List<Volume>();
        foreach (var line in (df ?? "").Split('\n').Skip(1))
        {
            var m = DfLine().Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            var source = m.Groups["src"].Value.Trim();
            var point = m.Groups["mp"].Value;
            if (!source.StartsWith("/dev/disk", StringComparison.Ordinal)) continue;
            if (point.StartsWith("/System/Volumes/", StringComparison.Ordinal) && point != "/System/Volumes/Data") continue;
            mounts.TryGetValue(point, out var mounted);
            var whole = WholeDisk().Match(source[5..]).Value;
            var physical = containers.TryGetValue(whole, out var stores)
                ? stores.Select(s => WholeDisk().Match(s).Value).Where(known.Contains).Distinct().ToList()
                : known.Contains(whole) ? [whole] : [];
            var label = point switch
            {
                "/" => "Macintosh HD",
                "/System/Volumes/Data" => "Macintosh HD - Data",
                _ when point.StartsWith("/Volumes/", StringComparison.Ordinal) => point[9..],
                _ => Path.GetFileName(point)
            };
            result.Add(new Volume(point, source, mounted?.FileSystem ?? "", Kb(m, "size"), Kb(m, "avail"), label, physical,
                mounted?.Options.Contains("read-only") == true, false));
        }
        return result;

        static long Kb(Match m, string group) => long.TryParse(m.Groups[group].Value, out var kb) ? kb * 1024 : 0;
    }

    /// <summary>
    /// Read and write counters per disk from ioreg -r -c IOBlockStorageDriver -a: the driver's Statistics and the
    /// BSD name of the whole-disk media under it. Times are in nanoseconds.
    /// </summary>
    public static Dictionary<string, DiskCounters> IoregDiskStats(string? xml)
    {
        var result = new Dictionary<string, DiskCounters>(StringComparer.Ordinal);
        if (Plist.Parse(xml) is not List<object?> drivers) return result;
        foreach (var driver in drivers.OfType<Dictionary<string, object?>>())
        {
            var stats = driver.Sub("Statistics");
            var bsd = driver.List("IORegistryEntryChildren").OfType<Dictionary<string, object?>>().Select(c => c.Str("BSD Name")).OfType<string>().FirstOrDefault();
            if (stats is null || bsd is null) continue;
            var busyNs = (stats.Long("Total Time (Read)") ?? 0) + (stats.Long("Total Time (Write)") ?? 0);
            result[bsd] = new DiskCounters(stats.Long("Operations (Read)") ?? 0, (stats.Long("Bytes (Read)") ?? 0) / 512,
                stats.Long("Operations (Write)") ?? 0, (stats.Long("Bytes (Write)") ?? 0) / 512, 0, busyNs / 1_000_000);
        }
        return result;
    }

    // ---- Network --------------------------------------------------------------------------------------------

    /// <summary>
    /// Interfaces from ifconfig -a. Whether an en interface is Wi-Fi or a wired port comes from system_profiler's
    /// network services; en ports it doesn't list (Thunderbolt bridge members) are treated as software links.
    /// </summary>
    public static List<NetLink> Links(string? ifconfig, JsonElement? profiler)
    {
        var hardware = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var service in Walk(Section(profiler, "SPNetworkDataType")))
            if (Str(service, "interface") is { } iface && (Str(service, "hardware") ?? Str(service, "type")) is { } kind) hardware.TryAdd(iface, kind);

        var blocks = new List<(string Name, string Flags, int Mtu, List<string> Lines)>();
        foreach (var raw in (ifconfig ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var head = IfconfigHead().Match(line);
            if (head.Success)
            {
                blocks.Add((head.Groups[1].Value, head.Groups[2].Value, int.TryParse(head.Groups[3].Value, out var mtu) ? mtu : 0, []));
                continue;
            }
            if (blocks.Count > 0 && line.Length > 0 && char.IsWhiteSpace(line[0])) blocks[^1].Lines.Add(line.Trim());
        }

        var links = new List<NetLink>();
        foreach (var (name, flags, mtu, lines) in blocks)
        {
            string? Value(string prefix) => lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Trim();
            var flagSet = flags.Split(',').ToHashSet(StringComparer.Ordinal);
            var profilerKind = hardware.GetValueOrDefault(name);
            var kind = name switch
            {
                _ when flagSet.Contains("LOOPBACK") || name.StartsWith("lo", StringComparison.Ordinal) => "loopback",
                _ when name.StartsWith("bridge", StringComparison.Ordinal) => "bridge",
                _ when name.StartsWith("utun", StringComparison.Ordinal) || name.StartsWith("ipsec", StringComparison.Ordinal) || name.StartsWith("ppp", StringComparison.Ordinal) => "vpn",
                _ when profilerKind is "AirPort" or "Wi-Fi" => "wifi",
                _ when name.StartsWith("en", StringComparison.Ordinal) => "ethernet",
                _ => "virtual"
            };
            var physical = kind is "wifi" || (kind == "ethernet" && profilerKind is not null);
            var status = Value("status:");
            var media = Value("media:") ?? "";
            var speed = MediaSpeed().Match(media);
            var mac = Value("ether ")?.Split(' ')[0];
            var link = new NetLink
            {
                Name = name,
                Index = links.Count + 1,
                DevicePath = physical ? $"/mac/net/{name}" : null,
                Wireless = kind == "wifi",
                OperState = status switch
                {
                    "active" => "up",
                    "inactive" => "down",
                    _ => flagSet.Contains("UP") && flagSet.Contains("RUNNING") ? "up" : flagSet.Contains("UP") ? "unknown" : "down"
                },
                Carrier = status == "active" || (status is null && flagSet.Contains("RUNNING")),
                SpeedMbps = speed.Success ? (long)(double.Parse(speed.Groups[1].Value, CultureInfo.InvariantCulture) * (speed.Groups[2].Value.Length > 0 ? 1000 : 1)) : 0,
                Duplex = media.Contains("full-duplex", StringComparison.Ordinal) ? "full" : media.Contains("half-duplex", StringComparison.Ordinal) ? "half" : null,
                Mac = mac is null ? null : NormalizeMac(mac),
                Mtu = mtu,
                Kind = kind,
                // The locally administered bit: macOS's private Wi-Fi address, or one set by software.
                RandomMac = mac is not null && byte.TryParse(mac.Split(':')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first) && (first & 0x02) != 0
            };
            link.Members.AddRange(lines.Where(l => l.StartsWith("member: ", StringComparison.Ordinal)).Select(l => l[8..].Split(' ')[0]));
            links.Add(link);
        }
        // Bridge members name their bridge.
        foreach (var bridge in links.Where(l => l.Members.Count > 0).ToList())
            for (var i = 0; i < links.Count; i++)
                if (bridge.Members.Contains(links[i].Name)) links[i] = With(links[i], bridge.Name);
        return links;

        static NetLink With(NetLink l, string master)
        {
            var copy = new NetLink
            {
                Name = l.Name, Index = l.Index, DevicePath = l.DevicePath, Wireless = l.Wireless, OperState = l.OperState, Carrier = l.Carrier,
                SpeedMbps = l.SpeedMbps, Duplex = l.Duplex, Mac = l.Mac, Mtu = l.Mtu, Kind = l.Kind, RandomMac = l.RandomMac, Master = master
            };
            copy.Members.AddRange(l.Members);
            return copy;
        }
    }

    /// <summary>
    /// netstat -ibn: one "&lt;Link#n&gt;" row per interface with packets, errors and bytes each way. The address
    /// column is empty for tunnels, so the numbers are read from the right.
    /// </summary>
    public static Dictionary<string, NetCounters> NetstatCounters(string? text)
    {
        var result = new Dictionary<string, NetCounters>(StringComparer.Ordinal);
        foreach (var line in (text ?? "").Split('\n'))
        {
            var f = line.Split((char[])[' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 10 || !f[2].StartsWith("<Link#", StringComparison.Ordinal)) continue;
            var n = f[^7..].Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : -1).ToArray();
            if (n.Any(x => x < 0)) continue;
            // Ipkts Ierrs Ibytes Opkts Oerrs Obytes Coll
            result.TryAdd(f[0].TrimEnd('*'), new NetCounters(n[2], n[0], n[1], 0, n[5], n[3], n[4], 0));
        }
        return result;
    }

    /// <summary>Default routes from netstat -rn -f inet ("default  192.168.1.1  UGScg  en0"), in the system's order.</summary>
    public static List<DefaultRoute> DefaultRoutes(string? text)
    {
        var result = new List<DefaultRoute>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var f = line.Split((char[])[' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 4 || f[0] != "default" || !IPAddress.TryParse(f[1], out var gateway) || gateway.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            if (!f[2].Contains('G')) continue;
            result.Add(new DefaultRoute(f[3], gateway, result.Count));
        }
        return result;
    }

    /// <summary>arp -an: "? (192.168.1.1) at 0:11:22:33:44:55 on en0 ifscope [ethernet]". macOS drops leading zeros.</summary>
    public static List<(IPAddress Address, string Mac, string Interface)> Arp(string? text)
    {
        var result = new List<(IPAddress, string, string)>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var m = ArpLine().Match(line);
            if (!m.Success || !IPAddress.TryParse(m.Groups[1].Value, out var address)) continue;
            var mac = NormalizeMac(m.Groups[2].Value);
            if (mac == "FF:FF:FF:FF:FF:FF") continue;
            result.Add((address, mac, m.Groups[3].Value));
        }
        return result;
    }

    /// <summary>Upstream DNS servers from scutil --dns ("nameserver[0] : 192.168.1.1"), IPv4 only, no loopback.</summary>
    public static List<IPAddress> DnsServers(string? text) =>
        (text ?? "").Split('\n')
            .Select(l => Regex.Match(l, @"nameserver\[\d+\]\s*:\s*(\S+)", RegexOptions.None, RegexLimit))
            .Where(m => m.Success)
            .Select(m => IPAddress.TryParse(m.Groups[1].Value, out var ip) ? ip : null)
            .Where(ip => ip is not null && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            .Select(ip => ip!)
            .Distinct()
            .ToList();

    /// <summary>
    /// The Wi-Fi association from system_profiler SPAirPortDataType. Since macOS 14 the network name reads
    /// "&lt;redacted&gt;" unless the app has Location Services permission; that shows as no name.
    /// </summary>
    public static WifiLink Wifi(string name, string? json)
    {
        var none = new WifiLink(name, false, null, null, null, null, null, null, null, null, null);
        JsonElement? section;
        try { section = Section(Profiler(json), "SPAirPortDataType"); }
        catch (JsonException) { return none; }
        foreach (var top in Walk(section))
        {
            if (!top.TryGetProperty("spairport_airport_interfaces", out var interfaces) || interfaces.ValueKind != JsonValueKind.Array) continue;
            foreach (var iface in interfaces.EnumerateArray())
            {
                if (Str(iface, "_name") != name) continue;
                if (!iface.TryGetProperty("spairport_current_network_information", out var current) || current.ValueKind != JsonValueKind.Object) return none;
                var ssid = Str(current, "_name");
                if (ssid is "<redacted>" or "<private>") ssid = null;
                var channelText = current.TryGetProperty("spairport_network_channel", out var ch) ? ch.ToString() : "";
                var channel = Regex.Match(channelText, @"^\s*(\d+)", RegexOptions.None, RegexLimit) is { Success: true } cm ? int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
                var band = channelText.Contains("6GHz", StringComparison.Ordinal) ? 6 : channelText.Contains("5GHz", StringComparison.Ordinal) ? 5 : channelText.Contains("2GHz", StringComparison.Ordinal) ? 2 : channel > 14 ? 5 : 2;
                double? mhz = channel is { } c ? band switch { 6 => 5950 + 5 * c, 5 => 5000 + 5 * c, _ => c == 14 ? 2484 : 2407 + 5 * c } : null;
                var signal = Regex.Match(Str(current, "spairport_signal_noise") ?? "", @"(-\d+)\s*dBm", RegexOptions.None, RegexLimit) is { Success: true } sm ? int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
                var rate = current.TryGetProperty("spairport_network_rate", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : (double?)null;
                var phy = Str(current, "spairport_network_phymode") ?? "";
                var standard = phy.Contains("be", StringComparison.Ordinal) ? "Wi-Fi 7 (802.11be)"
                    : phy.Contains("ax", StringComparison.Ordinal) ? band == 6 ? "Wi-Fi 6E (802.11ax)" : "Wi-Fi 6 (802.11ax)"
                    : phy.Contains("ac", StringComparison.Ordinal) ? "Wi-Fi 5 (802.11ac)"
                    : phy.Contains('n') ? "Wi-Fi 4 (802.11n)" : null;
                var security = (Str(current, "spairport_security_mode") ?? "").Replace("spairport_security_mode_", "", StringComparison.Ordinal) switch
                {
                    "" => null,
                    "none" => "Open (no encryption)",
                    var s => string.Join(' ', s.Split('_').Select(w => w.Length <= 4 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]))
                };
                return new WifiLink(name, true, ssid, null, signal, mhz, null, rate, standard, security, channel);
            }
        }
        return none;
    }

    // ---- USB, displays, Bluetooth ----------------------------------------------------------------------------

    /// <summary>
    /// The USB tree from system_profiler: SPUSBHostDataType (newer macOS) or SPUSBDataType (older). Device names
    /// follow the Linux pattern (bus-port.port) so the map treats them alike; ports are known only where a
    /// device is plugged in. Returns the BSD names of USB drives with the device each hangs off.
    /// </summary>
    public static (List<UsbBus> Buses, Dictionary<string, string> DiskOwners) Usb(JsonElement? profiler)
    {
        var buses = new List<UsbBus>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var host = Section(profiler, "SPUSBHostDataType");
        var section = host is { ValueKind: JsonValueKind.Array } h && h.GetArrayLength() > 0 ? host : Section(profiler, "SPUSBDataType");
        if (section is not { ValueKind: JsonValueKind.Array } array) return (buses, owners);
        foreach (var bus in array.EnumerateArray())
        {
            var number = buses.Count + 1;
            var busName = Str(bus, "_name") ?? $"USB bus {number}";
            var busSpeed = busName.Contains("3.2", StringComparison.Ordinal) || busName.Contains("usb4", StringComparison.OrdinalIgnoreCase) ? 20000
                : busName.Contains("3.1", StringComparison.Ordinal) || busName.Contains("31", StringComparison.Ordinal) ? 10000
                : busName.Contains('3') ? 5000 : 480;
            var root = new UsbDevice
            {
                Name = $"usb{number}", SysPath = $"/mac/usb{number}", Bus = number, SpeedMbps = busSpeed, Version = VersionFor(busSpeed),
                DeviceClass = 0x09, IsRootHub = true, ProductName = busName,
                Manufacturer = Str(bus, "Driver") ?? Str(bus, "host_controller")
            };
            AddChildren(bus, root, $"{number}-", number, owners);
            buses.Add(new UsbBus(number, root, Str(bus, "Driver") ?? Str(bus, "host_controller") ?? busName, root.SysPath));
        }
        return (buses, owners);
    }

    private static void AddChildren(JsonElement node, UsbDevice parent, string prefix, int bus, Dictionary<string, string> owners)
    {
        if (!node.TryGetProperty("_items", out var items) || items.ValueKind != JsonValueKind.Array) return;
        var n = 0;
        foreach (var item in items.EnumerateArray())
        {
            n++;
            var name = prefix + n.ToString(CultureInfo.InvariantCulture);
            var speed = LinkSpeed(Str(item, "USBDeviceKeyLinkSpeed") ?? Str(item, "device_speed"));
            var hasChildren = item.TryGetProperty("_items", out var sub) && sub.ValueKind == JsonValueKind.Array && sub.GetArrayLength() > 0;
            var product = Str(item, "_name");
            var device = new UsbDevice
            {
                Name = name,
                SysPath = $"{parent.SysPath}/{name}",
                Bus = bus,
                Vendor = Hex(Str(item, "USBDeviceKeyVendorID") ?? Str(item, "vendor_id")),
                Product = Hex(Str(item, "USBDeviceKeyProductID") ?? Str(item, "product_id")),
                Manufacturer = Str(item, "USBDeviceKeyVendorName") ?? Str(item, "manufacturer"),
                ProductName = product,
                SpeedMbps = speed,
                Version = VersionFor(speed),
                MaxPowerMa = Milliamps(Str(item, "USBDeviceKeyPowerAllocation") ?? Str(item, "bus_power_used")),
                DeviceClass = hasChildren || product?.Contains("Hub", StringComparison.OrdinalIgnoreCase) == true ? 0x09 : 0,
                Removable = (Str(item, "USBKeyHardwareType") ?? "") switch { "Removable" => "removable", "Non-removable" or "Built-in" => "fixed", _ => null },
                Parent = parent
            };
            var port = new UsbPort { Name = $"{parent.Name}-port{n}", Number = n, ConnectType = device.Removable == "fixed" ? "hardwired" : "hotplug", State = "configured", Hub = parent, Device = device };
            device.Port = port;
            parent.Ports.Add(port);
            parent.Children.Add(device);
            foreach (var bsd in BsdNames(item))
            {
                owners.TryAdd(bsd, device.SysPath);
                if (device.Interfaces.Count == 0) device.Interfaces.Add(new UsbInterface($"{name}:1.0", 0x08, 0x06, 0x62, "uas", $"{device.SysPath}/{name}:1.0"));
            }
            if (hasChildren) AddChildren(item, device, name + ".", bus, owners);
        }
    }

    // A USB drive's media (old format: "Media": [{ "bsd_name": "disk4" }]) without descending into child devices.
    private static IEnumerable<string> BsdNames(JsonElement item)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (property.Name == "_items") continue;
            if (property.Name is "bsd_name" or "USBDeviceKeyBSDName" && property.Value.ValueKind == JsonValueKind.String && WholeDisk().Match(property.Value.GetString()!) is { Success: true } w)
                yield return w.Value;
            if (property.Value.ValueKind == JsonValueKind.Array)
                foreach (var media in property.Value.EnumerateArray())
                    if (media.ValueKind == JsonValueKind.Object)
                        foreach (var name in BsdNames(media))
                            yield return name;
        }
    }

    /// <summary>"480 Mb/s", "5 Gb/s", "Up to 480 Mb/sec", "super_speed", "high_speed" → Mbps.</summary>
    public static double LinkSpeed(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var m = Regex.Match(text, @"([\d.]+)\s*([MG])b", RegexOptions.IgnoreCase, RegexLimit);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return m.Groups[2].Value.Equals("G", StringComparison.OrdinalIgnoreCase) ? value * 1000 : value;
        return text switch
        {
            "low_speed" => 1.5,
            "full_speed" => 12,
            "high_speed" => 480,
            "super_speed" => 5000,
            "super_speed_plus" => 10000,
            _ => 0
        };
    }

    private static string VersionFor(double mbps) => mbps switch { >= 20000 => "3.20", >= 10000 => "3.10", >= 5000 => "3.00", >= 480 => "2.00", >= 12 => "1.10", > 0 => "1.00", _ => "" };

    /// <summary>"2.5 W (500 mA)" or "500" → 500.</summary>
    private static int Milliamps(string? text) =>
        text is null ? 0
        : Regex.Match(text, @"\((\d+)\s*mA\)", RegexOptions.None, RegexLimit) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
        : int.TryParse(text.Trim(), out var plain) ? plain : 0;

    /// <summary>"0x05ac", "0x05ac  (Apple Inc.)", or "apple_vendor_id" → the number.</summary>
    private static int Hex(string? text)
    {
        if (text is null) return 0;
        if (text == "apple_vendor_id") return 0x05ac;
        var m = Regex.Match(text, @"0x([0-9a-fA-F]+)", RegexOptions.None, RegexLimit);
        return m.Success ? int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>
    /// Screens from SPDisplaysDataType, one entry per graphics processor with its displays. The built-in panel is
    /// an eDP link; external screens on a Mac are DisplayPort (over USB-C/Thunderbolt) unless the name says HDMI.
    /// </summary>
    public static List<DisplayOutput> Displays(JsonElement? profiler)
    {
        var result = new List<DisplayOutput>();
        var card = 0;
        foreach (var gpu in Walk(Section(profiler, "SPDisplaysDataType")))
        {
            var gpuName = Str(gpu, "sppci_model") ?? Str(gpu, "_name") ?? $"card{card}";
            if (!gpu.TryGetProperty("spdisplays_ndrvs", out var screens) || screens.ValueKind != JsonValueKind.Array) { card++; continue; }
            int dp = 0, hdmi = 0, edp = 0;
            foreach (var screen in screens.EnumerateArray())
            {
                var name = Str(screen, "_name");
                var connection = Str(screen, "spdisplays_connection_type") ?? "";
                var builtIn = connection.Contains("internal", StringComparison.OrdinalIgnoreCase) || name is "Color LCD" or "Built-in Retina Display" or "Built-in Liquid Retina XDR Display";
                var connector = builtIn ? $"eDP-{++edp}"
                    : connection.Contains("hdmi", StringComparison.OrdinalIgnoreCase) || name?.Contains("HDMI", StringComparison.OrdinalIgnoreCase) == true ? $"HDMI-A-{++hdmi}"
                    : $"DP-{++dp}";
                var pixels = Regex.Match(Str(screen, "_spdisplays_pixels") ?? Str(screen, "spdisplays_pixelresolution") ?? Str(screen, "spdisplays_resolution") ?? Str(screen, "_spdisplays_resolution") ?? "", @"(\d+)\s*x\s*(\d+)", RegexOptions.None, RegexLimit);
                var refresh = Regex.Match(Str(screen, "_spdisplays_resolution") ?? Str(screen, "spdisplays_resolution") ?? "", @"@\s*([\d.]+)\s*Hz", RegexOptions.None, RegexLimit);
                int? width = pixels.Success ? int.Parse(pixels.Groups[1].Value, CultureInfo.InvariantCulture) : null;
                int? height = pixels.Success ? int.Parse(pixels.Groups[2].Value, CultureInfo.InvariantCulture) : null;
                var vendor = Str(screen, "_spdisplays_display-vendor-id");
                var edid = new Edid(vendor is "610" or "0x610" ? "Apple" : null, name,
                    int.TryParse(Str(screen, "_spdisplays_display-year"), out var year) ? year : null, width, height,
                    refresh.Success ? double.Parse(refresh.Groups[1].Value, CultureInfo.InvariantCulture) : null, null);
                result.Add(new DisplayOutput($"card{card}", $"card{card}-{connector}", Platform.Displays.ConnectorKind(connector), true,
                    Str(screen, "spdisplays_online") != "spdisplays_no", edid, width is null ? null : $"{width}x{height}", null));
            }
            card++;
        }
        return result;
    }

    /// <summary>The graphics processors SPDisplaysDataType names, with core counts where Apple gives them.</summary>
    public static List<string> Gpus(JsonElement? profiler) =>
        Walk(Section(profiler, "SPDisplaysDataType"))
            .Select(g => (Str(g, "sppci_model") ?? Str(g, "_name"), Str(g, "sppci_cores")))
            .Where(g => g.Item1 is not null)
            .Select(g => g.Item2 is { } cores ? $"{g.Item1} ({cores}-core GPU)" : g.Item1!)
            .ToList();

    /// <summary>
    /// The Bluetooth radio and paired devices from SPBluetoothDataType: newer macOS lists device_connected and
    /// device_not_connected; older versions list device_title.
    /// </summary>
    public static List<BluetoothRadio> Bluetooth(JsonElement? profiler)
    {
        var result = new List<BluetoothRadio>();
        foreach (var top in Walk(Section(profiler, "SPBluetoothDataType")))
        {
            var paired = new List<BluetoothDevice>();
            void Read(string key, bool connected)
            {
                if (!top.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array) return;
                foreach (var entry in list.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    foreach (var device in entry.EnumerateObject())
                    {
                        var address = Str(device.Value, "device_address") ?? Str(device.Value, "device_addr") ?? "";
                        var isConnected = connected || Str(device.Value, "device_isconnected") == "attrib_Yes" || Str(device.Value, "device_connected") == "attrib_Yes";
                        paired.Add(new BluetoothDevice(NormalizeMac(address.Replace('-', ':')), device.Name, isConnected));
                    }
                }
            }
            Read("device_connected", true);
            Read("device_not_connected", false);
            Read("device_title", false);
            JsonElement controller = default;
            var hasController = top.TryGetProperty("controller_properties", out controller) && controller.ValueKind == JsonValueKind.Object;
            var state = hasController ? Str(controller, "controller_state") : Str(top, "local_device_title") is null ? null : "attrib_on";
            var chip = hasController ? Str(controller, "controller_chipset") : null;
            if (!hasController && paired.Count == 0 && !top.TryGetProperty("apple_bluetooth_version", out _)) continue;
            result.Add(new BluetoothRadio(chip is null ? "Bluetooth" : $"Bluetooth ({chip})", "/mac/bluetooth", null,
                state is null ? null : state != "attrib_on", paired, true));
        }
        return result;
    }

    // ---- JSON helpers ----------------------------------------------------------------------------------------

    public static JsonElement? Profiler(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        // system_profiler prints one JSON object per call; the clone outlives the document.
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement? Section(JsonElement? profiler, string type) =>
        profiler is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(type, out var section) ? section : null;

    public static JsonElement? First(JsonElement? section) =>
        section is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0 ? a[0] : null;

    /// <summary>Every object in a section and the _items under it.</summary>
    public static IEnumerable<JsonElement> Walk(JsonElement? section)
    {
        if (section is not { ValueKind: JsonValueKind.Array } array) yield break;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            yield return item;
            if (item.TryGetProperty("_items", out var children))
                foreach (var child in Walk(children))
                    yield return child;
        }
    }

    public static string? Str(JsonElement? element, string key) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(key, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null, JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) || Sys.IsFiller(value) || value.Equals("Empty", StringComparison.OrdinalIgnoreCase) ? null : value.Trim();

    private static double? Number(string? text) =>
        text is not null && Regex.Match(text, @"[\d.]+", RegexOptions.None, RegexLimit) is { Success: true } m && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>"16 GB" → 16384, "512 MB" → 512, "empty" → 0.</summary>
    private static int SizeMb(string? text) =>
        Number(text) is not { } n ? 0 : text!.Contains("TB", StringComparison.Ordinal) ? (int)(n * 1_048_576) : text.Contains("GB", StringComparison.Ordinal) ? (int)(n * 1024) : (int)n;

    /// <summary>"0:11:2:33:4:55" → "00:11:02:33:04:55".</summary>
    public static string NormalizeMac(string mac) =>
        string.Join(':', mac.Split(':').Select(p => p.Length == 1 ? "0" + p : p)).ToUpperInvariant();

    [GeneratedRegex(@"^disk\d+", RegexOptions.None, 1000)]
    private static partial Regex WholeDisk();

    [GeneratedRegex(@"page size of (\d+) bytes", RegexOptions.None, 1000)]
    private static partial Regex PageSize();

    [GeneratedRegex(@"^(?<src>.+?)\s+(?<size>\d+)\s+(?<used>\d+)\s+(?<avail>\d+)\s+\d+%(?:\s+\d+\s+\d+\s+\d+%)?\s+(?<mp>/.*)$", RegexOptions.None, 1000)]
    private static partial Regex DfLine();

    [GeneratedRegex(@"^([A-Za-z0-9_.-]+): flags=[0-9a-fA-F]+<([^>]*)>.*?mtu (\d+)", RegexOptions.None, 1000)]
    private static partial Regex IfconfigHead();

    [GeneratedRegex(@"\((\d+(?:\.\d+)?)(G?)base", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex MediaSpeed();

    [GeneratedRegex(@"\(([\d.]+)\) at ([0-9a-fA-F]{1,2}(?::[0-9a-fA-F]{1,2}){5}) on (\S+)", RegexOptions.None, 1000)]
    private static partial Regex ArpLine();
}
