using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BoardScout.Services;

/// <summary>One memory slot as the firmware describes it (SMBIOS type 17).</summary>
internal sealed partial record DimmSlot(
    string DeviceLocator,
    string BankLocator,
    int SizeMb,
    int SpeedMts,
    int ConfiguredMts,
    int Rank,
    string? PartNumber,
    string MemoryType)
{
    public bool Populated => SizeMb > 0;

    /// <summary>Channel letter ("A") from locators like "P0 CHANNEL A", "ChannelA-DIMM1", "DIMM_A2", or "A1".</summary>
    public string? Channel
    {
        get
        {
            foreach (var text in new[] { BankLocator, DeviceLocator })
            {
                var match = ChannelPattern().Match(text);
                if (match.Success) return match.Groups[1].Value.ToUpperInvariant();
            }
            return null;
        }
    }

    /// <summary>Position inside the channel, 0 being the first slot the firmware lists.</summary>
    public int? Position
    {
        get
        {
            var match = PositionPattern().Match(DeviceLocator);
            return match.Success ? int.Parse(match.Groups[1].Value) : null;
        }
    }

    [GeneratedRegex(@"(?:CHANNEL\s*|DIMM_?|^)([A-H])(?:\d|\b|-)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPattern();

    [GeneratedRegex(@"(\d+)\s*$")]
    private static partial Regex PositionPattern();
}

/// <summary>What the firmware tables say about the machine: memory slots, chassis, and maker.</summary>
internal sealed record FirmwareFacts(
    IReadOnlyList<DimmSlot> MemorySlots,
    int SlotCount,
    int ChassisType,
    string? SystemMaker,
    string? SystemProduct)
{
    /// <summary>Desktop, Laptop, Server, or Virtual machine, from the SMBIOS chassis type and maker.</summary>
    public string FormFactor
    {
        get
        {
            var maker = $"{SystemMaker} {SystemProduct}";
            if (Regex.IsMatch(maker, @"VMware|VirtualBox|QEMU|KVM|Xen|Parallels|Virtual Machine|Hyper-V|innotek", RegexOptions.IgnoreCase))
                return "Virtual machine";
            return ChassisType switch
            {
                8 or 9 or 10 or 14 or 30 or 31 or 32 => "Laptop",
                11 => "Handheld",
                17 or 23 or 28 or 29 => "Server",
                13 => "All-in-one",
                35 or 36 => "Mini PC",
                _ => "Desktop"
            };
        }
    }
}

/// <summary>
/// Reads the SMBIOS tables Windows exposes to every user (GetSystemFirmwareTable 'RSMB'); no admin needed.
/// </summary>
internal static class Smbios
{
    public static FirmwareFacts Read()
    {
        var slots = new List<DimmSlot>();
        int slotCount = 0, chassis = 0;
        string? maker = null, product = null;
        try
        {
            var size = GetSystemFirmwareTable(0x52534D42, 0, null, 0);
            if (size <= 8) return new FirmwareFacts(slots, 0, 0, null, null);
            var table = new byte[size];
            if (GetSystemFirmwareTable(0x52534D42, 0, table, size) != size) return new FirmwareFacts(slots, 0, 0, null, null);

            // RawSMBIOSData: 8-byte header, then structures: type, length, handle, formatted area, strings.
            for (var p = 8; p + 4 <= table.Length;)
            {
                int type = table[p], length = table[p + 1];
                if (length < 4 || p + length > table.Length) break;
                var (strings, next) = ReadStrings(table, p + length);
                string? Text(int offset)
                {
                    var index = offset < length ? table[p + offset] : 0;
                    var value = index > 0 && index <= strings.Count ? strings[index - 1].Trim() : null;
                    return string.IsNullOrEmpty(value) || value.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase) ||
                           value is "Unknown" or "Not Specified" ? null : value;
                }

                switch (type)
                {
                    case 1:
                        maker = Text(0x04);
                        product = Text(0x05);
                        break;
                    case 3:
                        chassis = table[p + 5] & 0x7F;
                        break;
                    case 16 when length > 0x0E:
                        slotCount += BitConverter.ToUInt16(table, p + 0x0D);
                        break;
                    case 17 when length > 0x12:
                        int sizeMb = BitConverter.ToUInt16(table, p + 0x0C);
                        if (sizeMb == 0xFFFF) sizeMb = 0;
                        else if (sizeMb == 0x7FFF && length > 0x1F) sizeMb = (int)BitConverter.ToUInt32(table, p + 0x1C);
                        else if ((sizeMb & 0x8000) != 0) sizeMb = (sizeMb & 0x7FFF) / 1024; // size given in KB
                        slots.Add(new DimmSlot(
                            Text(0x10) ?? $"Slot {slots.Count + 1}",
                            Text(0x11) ?? "",
                            sizeMb,
                            length > 0x16 ? BitConverter.ToUInt16(table, p + 0x15) : 0,
                            length > 0x21 ? BitConverter.ToUInt16(table, p + 0x20) : 0,
                            length > 0x1B ? table[p + 0x1B] & 0x0F : 0,
                            Text(0x1A),
                            table[p + 0x12] switch { 0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1D => "LPDDR3", 0x1E => "LPDDR4", 0x22 => "DDR5", 0x23 => "LPDDR5", _ => "DDR" }));
                        break;
                }
                if (type == 127) break;
                p = next;
            }
        }
        catch
        {
        }
        return new FirmwareFacts(slots, slotCount > 0 ? slotCount : slots.Count, chassis, maker, product);
    }

    // The string set after a structure: null-terminated strings, ended by an extra null.
    private static (List<string> Strings, int Next) ReadStrings(byte[] table, int start)
    {
        var strings = new List<string>();
        if (start + 1 < table.Length && table[start] == 0 && table[start + 1] == 0) return (strings, start + 2);
        var position = start;
        while (position < table.Length)
        {
            var end = Array.IndexOf(table, (byte)0, position);
            if (end < 0) return (strings, table.Length);
            strings.Add(Encoding.ASCII.GetString(table, position, end - position));
            position = end + 1;
            if (position >= table.Length || table[position] == 0) return (strings, position + 1);
        }
        return (strings, table.Length);
    }

    [DllImport("kernel32.dll")]
    private static extern int GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, int size);
}
