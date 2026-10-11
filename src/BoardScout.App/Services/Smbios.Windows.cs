using System.Runtime.InteropServices;

namespace BoardScout.Services;

internal static partial class Smbios
{
    /// <summary>The SMBIOS tables Windows exposes to every user (GetSystemFirmwareTable 'RSMB'); no admin needed.</summary>
    public static FirmwareFacts Read()
    {
        try
        {
            var size = GetSystemFirmwareTable(0x52534D42, 0, null, 0);
            if (size <= 8) return new FirmwareFacts([], 0, 0, null, null);
            var table = new byte[size];
            if (GetSystemFirmwareTable(0x52534D42, 0, table, size) != size) return new FirmwareFacts([], 0, 0, null, null);
            // RawSMBIOSData: an 8-byte header, then the structures.
            return Parse(table, 8);
        }
        catch
        {
            return new FirmwareFacts([], 0, 0, null, null);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, int size);
}
