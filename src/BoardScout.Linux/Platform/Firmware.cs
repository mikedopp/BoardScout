using System.Globalization;
using BoardScout.Services;

namespace BoardScout.Platform;

/// <summary>What the firmware says about the machine (/sys/class/dmi/id): maker, board, BIOS, and chassis.</summary>
internal sealed record MachineIdentity(
    string? SystemVendor,
    string? ProductName,
    string? ProductVersion,
    string? BoardVendor,
    string? BoardName,
    string? BoardVersion,
    string? BiosVendor,
    string? BiosVersion,
    string? BiosDate,
    int ChassisType)
{
    public string? Board => Join(BoardVendor, BoardName);
    public string? System => Join(SystemVendor, ProductName);

    private static string? Join(string? vendor, string? name) =>
        name is null ? vendor : vendor is null || name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? name : $"{vendor} {name}";
}

internal sealed record SecurityFacts(bool Uefi, bool? SecureBoot, int? TpmVersion, string? TpmMaker);

/// <summary>Memory slots plus who made each stick (JEDEC maker code), from udev's copy of the SMBIOS tables.</summary>
internal sealed record MemoryFacts(FirmwareFacts Firmware, IReadOnlyDictionary<string, string> Makers, string Source);

internal static class Firmware
{
    public static MachineIdentity ReadIdentity()
    {
        static string? D(string name) => Sys.Text($"/sys/class/dmi/id/{name}");
        return new MachineIdentity(D("sys_vendor"), D("product_name"), D("product_version"), D("board_vendor"), D("board_name"),
            D("board_version"), D("bios_vendor"), D("bios_version"), D("bios_date"), Sys.Int("/sys/class/dmi/id/chassis_type") ?? 0);
    }

    public static SecurityFacts ReadSecurity()
    {
        var uefi = Sys.Exists("/sys/firmware/efi");
        bool? secureBoot = null;
        if (uefi)
        {
            // efivarfs: four attribute bytes, then the value (1 = enforcing).
            var data = Sys.Bytes("/sys/firmware/efi/efivars/SecureBoot-8be4df61-93ca-11d2-aa0d-00e098032b8c", 16);
            if (data is { Length: >= 5 }) secureBoot = data[4] == 1;
        }
        var tpm = Sys.Int("/sys/class/tpm/tpm0/tpm_version_major");
        if (tpm is null && Sys.Exists("/sys/class/tpm/tpm0")) tpm = Sys.Exists("/sys/class/tpm/tpm0/device/description") ? 1 : null;
        return new SecurityFacts(uefi, secureBoot, tpm, Sys.Text("/sys/class/tpm/tpm0/device/description"));
    }

    /// <summary>
    /// Memory slots. systemd-udevd copies them out of the SMBIOS tables at boot (MEMORY_DEVICE_* properties),
    /// readable by every user; root can also read the raw table, which older systems need.
    /// </summary>
    public static MemoryFacts ReadMemory(MachineIdentity identity)
    {
        var properties = ReadUdevDmi();
        if (properties.Count > 0 && properties.Keys.Any(k => k.StartsWith("MEMORY_DEVICE_", StringComparison.Ordinal)))
            return FromUdev(properties, identity);
        if (Sys.Bytes("/sys/firmware/dmi/tables/DMI", 1 << 20) is { Length: > 4 } table)
            return new MemoryFacts(Smbios.Parse(table, 0), new Dictionary<string, string>(), "SMBIOS table (root)");
        return new MemoryFacts(new FirmwareFacts([], 0, identity.ChassisType, identity.SystemVendor, identity.ProductName),
            new Dictionary<string, string>(), "not available");
    }

    private static Dictionary<string, string> ReadUdevDmi()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // udev's database entry for the DMI device; "E:KEY=value" lines.
        foreach (var line in Sys.Lines("/run/udev/data/+dmi:id"))
        {
            if (!line.StartsWith("E:", StringComparison.Ordinal)) continue;
            var equals = line.IndexOf('=');
            if (equals > 2) result[line[2..equals]] = line[(equals + 1)..];
        }
        if (result.Count > 0) return result;
        var text = Tools.Run("udevadm", "info", "--query=property", "--path=/devices/virtual/dmi/id");
        foreach (var line in (text ?? "").Split('\n'))
        {
            var equals = line.IndexOf('=');
            if (equals > 0) result[line[..equals]] = line[(equals + 1)..].Trim();
        }
        return result;
    }

    public static MemoryFacts FromUdev(IReadOnlyDictionary<string, string> p, MachineIdentity identity)
    {
        var slots = new List<DimmSlot>();
        var makers = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = int.TryParse(p.GetValueOrDefault("MEMORY_ARRAY_NUM_DEVICES"), out var n) ? n : 0;
        for (var i = 0; i < Math.Max(count, 64); i++)
        {
            string? V(string key)
            {
                var value = p.GetValueOrDefault($"MEMORY_DEVICE_{i}_{key}")?.Trim();
                return string.IsNullOrEmpty(value) || Sys.IsFiller(value) ? null : value;
            }
            var locator = V("LOCATOR");
            if (locator is null && V("SIZE") is null && p.GetValueOrDefault($"MEMORY_DEVICE_{i}_PRESENT") is null)
            {
                if (i >= count) break;
                continue;
            }
            var present = p.GetValueOrDefault($"MEMORY_DEVICE_{i}_PRESENT") != "0";
            var sizeBytes = long.TryParse(V("SIZE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0;
            var type = V("TYPE");
            var slot = new DimmSlot(
                locator ?? $"Slot {i + 1}",
                V("BANK_LOCATOR") ?? "",
                present ? (int)(sizeBytes / 1_048_576) : 0,
                int.TryParse(V("SPEED_MTS"), out var speed) ? speed : 0,
                int.TryParse(V("CONFIGURED_SPEED_MTS"), out var configured) ? configured : 0,
                int.TryParse(V("RANK"), out var rank) ? rank : 0,
                V("PART_NUMBER"),
                type is null || type.Equals("Other", StringComparison.OrdinalIgnoreCase) ? "DDR" : type);
            slots.Add(slot);
            if (present && JedecMaker(V("MANUFACTURER")) is { } maker) makers[slot.DeviceLocator + "|" + slot.BankLocator] = maker;
        }
        var chassis = identity.ChassisType;
        return new MemoryFacts(new FirmwareFacts(slots, count > 0 ? count : slots.Count, chassis, identity.SystemVendor, identity.ProductName),
            makers, "udev (from the SMBIOS tables)");
    }

    /// <summary>
    /// Module maker from SMBIOS: a name ("Kingston") or a JEDEC JEP106 code ("802C0000802C" → Micron).
    /// </summary>
    public static string? JedecMaker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hex = value.Trim();
        if (!hex.All(char.IsAsciiHexDigit)) return value.Trim();
        if (hex.Length < 4) return null;
        // Two bytes: how many 0x7F continuation codes precede the maker (its JEP106 bank, less one; the top bit
        // is parity), and the maker's code in that bank. Some firmware stores them the other way round.
        var first = Convert.ToInt32(hex[..2], 16);
        var second = Convert.ToInt32(hex[2..4], 16);
        return Maker(first & 0x7F, second) ?? Maker(second & 0x7F, first);

        static string? Maker(int bank, int code) => (bank, code) switch
        {
            (0, 0x2C) => "Micron",
            (0, 0xCE) => "Samsung",
            (0, 0xAD) => "SK hynix",
            (1, 0x98) => "Kingston",
            (2, 0x9E) => "Corsair",
            (2, 0xFE) => "Elpida",
            (3, 0x0B) => "Nanya",
            (4, 0xCB) => "ADATA",
            (4, 0xCD) => "G.Skill",
            (5, 0x9B) => "Crucial",
            _ => null
        };
    }

    /// <summary>Desktop, Laptop, Server, Mini PC, and so on, from the SMBIOS chassis type.</summary>
    public static string FormFactor(MachineIdentity identity, string? deviceTreeModel)
    {
        if (deviceTreeModel is not null && deviceTreeModel.Contains("Raspberry Pi", StringComparison.OrdinalIgnoreCase)) return "Single-board computer";
        var facts = new FirmwareFacts([], 0, identity.ChassisType, identity.SystemVendor, identity.ProductName);
        if (identity.ChassisType == 0 && deviceTreeModel is not null) return "Single-board computer";
        return facts.FormFactor;
    }
}
