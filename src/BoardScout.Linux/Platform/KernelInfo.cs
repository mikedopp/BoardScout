using System.Text.RegularExpressions;

namespace BoardScout.Platform;

/// <summary>A kernel driver (module): its version when it declares one, and where it came from.</summary>
internal sealed record DriverInfo(string Name, string? Version, string Source, bool Proprietary, bool BuiltIn);

/// <summary>Kernel driver details, the sound cards' codecs, and SATA link speeds.</summary>
internal static partial class KernelInfo
{
    /// <summary>
    /// What the kernel says about a driver. In-tree drivers carry no version of their own (they ship with the
    /// kernel); out-of-tree ones (NVIDIA, DKMS packages) set a version and taint the kernel with "O" or "P".
    /// </summary>
    public static DriverInfo? Driver(string? name, string kernel)
    {
        if (string.IsNullOrEmpty(name)) return null;
        // The driver's module name can differ from its driver name ("xhci_hcd" is in "xhci_pci").
        var module = Sys.Exists($"/sys/module/{name}") ? name : Sys.Exists($"/sys/module/{name.Replace('-', '_')}") ? name.Replace('-', '_') : null;
        if (module is null) return new DriverInfo(name, null, $"Built into the kernel ({kernel})", false, true);
        var taint = Sys.Read($"/sys/module/{module}/taint") ?? "";
        var version = Sys.Read($"/sys/module/{module}/version");
        var proprietary = taint.Contains('P');
        var outOfTree = taint.Contains('O') || proprietary;
        var builtIn = !Sys.Exists($"/sys/module/{module}/initstate");
        var source = proprietary ? "Proprietary, installed separately"
            : outOfTree ? "Out-of-tree module (DKMS or a vendor package)"
            : builtIn ? $"Built into the kernel ({kernel})"
            : $"Kernel module from the kernel ({kernel})";
        return new DriverInfo(name, version, source, proprietary, builtIn);
    }

    /// <summary>Sound cards with their codec ("Realtek ALC897"), keyed by the PCI address of their controller.</summary>
    public static Dictionary<string, string> AudioCodecs()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var card in Sys.Children("/proc/asound").Where(n => CardName().IsMatch(n)))
        {
            var address = Pci.AddressIn(Sys.Real($"/sys/class/sound/{card}/device"));
            if (address is null) continue;
            var codecs = Sys.Children($"/proc/asound/{card}")
                .Where(f => f.StartsWith("codec#", StringComparison.Ordinal))
                .Select(f => Sys.Lines($"/proc/asound/{card}/{f}").FirstOrDefault(l => l.StartsWith("Codec:", StringComparison.Ordinal))?[6..].Trim())
                .Where(c => !string.IsNullOrEmpty(c) && !c.Contains("HDMI", StringComparison.OrdinalIgnoreCase) && !c.Contains("Generic", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (codecs.Count > 0) result[address] = codecs[0]!;
        }
        return result;
    }

    /// <summary>The SATA port count of an AHCI controller and the negotiated speed of each port in use.</summary>
    public static (int Ports, Dictionary<int, string> Speeds) SataPorts(string controllerPath)
    {
        var speeds = new Dictionary<int, string>();
        var ports = 0;
        foreach (var entry in Sys.Children(controllerPath))
        {
            var match = AtaPort().Match(entry);
            if (!match.Success) continue;
            ports++;
            var number = int.Parse(match.Groups[1].Value);
            var speed = Sys.Read($"/sys/class/ata_link/link{number}/sata_spd");
            if (speed is not null && speed != "<unknown>") speeds[number] = speed;
        }
        return (ports, speeds);
    }

    /// <summary>What a SATA drive's IDENTIFY data says about it.</summary>
    internal sealed record AtaIdentity(double? MaxGbps, string? Firmware, int? Rpm, bool SolidState);

    /// <summary>
    /// A SATA drive's own fastest link, full firmware revision, and spin speed, from the IDENTIFY data libata
    /// publishes (/sys/class/ata_device/devN.0/id). Only those words are read: never the serial number.
    /// </summary>
    public static AtaIdentity? AtaIdentify(int port) => ParseIdentify(Sys.Read($"/sys/class/ata_device/dev{port}.0/id"));

    public static AtaIdentity? ParseIdentify(string? text)
    {
        var words = text?.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words is null || words.Length < 218) return null;
        int Word(int index) => int.TryParse(words[index], System.Globalization.NumberStyles.HexNumber, null, out var value) ? value : 0;
        // Word 76: SATA generations supported (bit 1 = 1.5, bit 2 = 3, bit 3 = 6 Gbps); 0 or 0xFFFF means not reported.
        var gens = Word(76);
        double? max = gens is 0 or 0xFFFF ? null : (gens & 0x8) != 0 ? 6 : (gens & 0x4) != 0 ? 3 : (gens & 0x2) != 0 ? 1.5 : null;
        // Words 23-26: firmware revision, two ASCII characters per word, high byte first.
        var firmware = new string(Enumerable.Range(23, 4).SelectMany(i => new[] { (char)(Word(i) >> 8), (char)(Word(i) & 0xFF) }).ToArray()).Trim('\0', ' ');
        // Word 217: nominal media rotation rate (1 = solid state, 0x0401-0xFFFE = RPM).
        var rotation = Word(217);
        return new AtaIdentity(max, firmware.Length > 0 && firmware.All(c => c is >= ' ' and <= '~') ? firmware : null,
            rotation is >= 0x0401 and <= 0xFFFE ? rotation : null, rotation == 1);
    }

    /// <summary>"3.0 Gbps" → (3, "SATA II · 3 Gbps").</summary>
    public static (double Gbps, string Label) SataSpeed(string speed) => speed switch
    {
        _ when speed.StartsWith("6", StringComparison.Ordinal) => (6, "SATA III · 6 Gbps"),
        _ when speed.StartsWith("3", StringComparison.Ordinal) => (3, "SATA II · 3 Gbps"),
        _ when speed.StartsWith("1.5", StringComparison.Ordinal) => (1.5, "SATA I · 1.5 Gbps"),
        _ => (0, $"SATA · {speed}")
    };

    /// <summary>The ATA port a disk is on ("…/0000:00:17.0/ata1/host0/…" → 1).</summary>
    public static int? AtaPortOf(string sysPath)
    {
        var match = Regex.Match(sysPath, @"/ata(\d+)/");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    public static string? Tainted()
    {
        var value = Sys.Long("/proc/sys/kernel/tainted");
        if (value is null or 0) return null;
        var flags = new List<string>();
        if ((value & 1) != 0) flags.Add("a proprietary module is loaded");
        if ((value & 4096) != 0) flags.Add("an out-of-tree module is loaded");
        if ((value & 8192) != 0) flags.Add("an unsigned module is loaded");
        if ((value & 128) != 0) flags.Add("the kernel hit an oops");
        if ((value & 512) != 0) flags.Add("the kernel issued a warning");
        if ((value & 2048) != 0) flags.Add("a workaround for a firmware bug is in use");
        return flags.Count == 0 ? $"flags {value}" : string.Join("; ", flags);
    }

    [GeneratedRegex(@"^card\d+$")]
    private static partial Regex CardName();

    [GeneratedRegex(@"^ata(\d+)$")]
    private static partial Regex AtaPort();
}
