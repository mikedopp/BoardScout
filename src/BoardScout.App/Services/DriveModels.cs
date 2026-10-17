using System.Text.RegularExpressions;

namespace BoardScout.Services;

/// <summary>What a disk reports about itself. Bus is the STORAGE_BUS_TYPE (7 USB, 11 SATA, 17 NVMe; the Linux edition
/// maps its transports onto the same numbers). Spinning is true for hard drives (the disk reports a seek penalty).
/// PowerTable is an NVMe drive's own power states.</summary>
internal sealed record DiskFacts(int Number, string? Vendor, string? Product, string? Firmware, int Bus, long? SizeBytes,
    double? TemperatureC, bool? Spinning = null, bool SpinningFromModel = false, DrivePowerTable? PowerTable = null);

/// <summary>
/// An NVMe drive's power states, in watts, from its Identify data: the most it may draw (state 0, MaxW), what it says it
/// draws idle and busy in state 0 (IdleW, ActiveW; null when it doesn't say), and its deepest sleep state (SleepW; null
/// when it has no sleep states).
/// </summary>
internal sealed record DrivePowerTable(double MaxW, double? IdleW, double? ActiveW, double? SleepW, int States)
{
    /// <summary>The power states in NVMe Identify Controller data (NPSS at byte 263, 32-byte descriptors from 2048).</summary>
    internal static DrivePowerTable? FromIdentify(ReadOnlySpan<byte> identify)
    {
        if (identify.Length < 4096) return null;
        var states = identify[263] + 1;
        static double? Scaled(int value, int scale) => value > 0 && scale is 1 or 2 ? value * (scale == 1 ? 0.0001 : 0.01) : null;
        double max = 0;
        double? idle = null, active = null, sleep = null;
        for (var i = 0; i < Math.Min(states, 32); i++)
        {
            var d = identify.Slice(2048 + i * 32, 32);
            // MP in centiwatts (or 100 µW units when MXPS is set); bit 1 of byte 3 marks a non-operational (sleep) state.
            var watts = BitConverter.ToUInt16(d) * ((d[3] & 1) != 0 ? 0.0001 : 0.01);
            var asleep = (d[3] & 2) != 0;
            if (i == 0)
            {
                max = watts;
                idle = Scaled(BitConverter.ToUInt16(d[16..]), d[18] >> 6);
                active = Scaled(BitConverter.ToUInt16(d[20..]), d[22] >> 6);
            }
            if (asleep && watts > 0) sleep = Math.Min(sleep ?? double.MaxValue, watts);
        }
        return max > 0 ? new DrivePowerTable(Math.Round(max, 4), idle, active, sleep, states) : null;
    }
}

/// <summary>What a drive's model number says about it. Shared by the Windows and Linux editions.</summary>
internal static partial class DriveModels
{
    /// <summary>
    /// Hard drive or SSD from the model number, for drives that don't report a seek penalty, or null when the
    /// model isn't a family BoardScout knows. USB bridges often split the model across vendor and product
    /// ("WDC WD10" + "JPVT-75A1YT0"), so both are joined without spaces first.
    /// </summary>
    internal static bool? SpinningFromModelNumber(string? vendor, string? product)
    {
        var model = string.Concat($"{vendor}{product}".Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        if (model.Length == 0) return null;
        if (SolidStateModel().IsMatch(model)) return false;
        if (SpinningModel().IsMatch(model)) return true;
        return null;
    }

    /// <summary>True for 2.5-inch (laptop) hard drive families, which draw a fraction of a 3.5-inch drive's power.</summary>
    internal static bool LaptopHardDrive(string? vendor, string? product) =>
        LaptopModel().IsMatch(string.Concat($"{vendor}{product}".Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant());

    // 2.5-inch hard drives: WD Blue/Black mobile (WD10JPVT, WD5000BEVT, WD20SPZX) and the drives inside WD's portable
    // cases (WD20NMVW, WD20SMRW), Seagate laptop (ST1000LM024, ST500LT012), HGST Travelstar (HTS541010A9E680),
    // Toshiba (MQ01ABD100, MK5065GSX), Samsung Spinpoint M (HN-M101MBB).
    [GeneratedRegex(@"^(WDC)?WD\d{2,5}(BPVX|BPVT|JPVX|JPVT|LPVX|LPCX|LPZX|SPZX|SDZX|BEKT|BEVT|BEKX|BPKX|BJKT|NMVW|NMZW|SMRW|SMZW|SDRW)|^ST\d{3,5}(LM|LT|LX)\d|^(HGST|HITACHI)?HT(S|E)\d{4}|^(TOSHIBA)?(MQ0\d|MK\d{4}G)|^(SAMSUNG|ST\d+LM\d+)?HN-M\d")]
    private static partial Regex LaptopModel();

    // SSDs: the word itself, NVMe, WD Blue/Black/Green SSDs (WDS…, SN…), Samsung (MZ…), Crucial (CT…MX/BX/P/T),
    // Kingston A400/NV, SanDisk SDSSD.
    [GeneratedRegex(@"SSD|NVME|SOLID|^(WDC)?WDS\d|SN\d{3}|^(SAMSUNG)?MZ[0-9A-Z]{4}|^CT\d+(MX|BX|P\d|T\d)|SA400|SNV\d|SDSSD")]
    private static partial Regex SolidStateModel();

    // Hard drives: WD (WD10JPVT, WD5000BEVT, WD40EFRX), Seagate (ST1000LM024, ST500LT012, ST4000DM004),
    // HGST/Hitachi (HTS541010A9E680, HUS724040ALE640), Toshiba (MQ01ABD100, DT01ACA300, HDWD110),
    // Samsung/Seagate laptop (HN-M101MBB, HD103SJ).
    [GeneratedRegex(@"^(WDC)?WD\d{2,5}[A-Z]{2,4}|^ST\d{3,5}[A-Z]{2}\d|^(HGST|HITACHI)?H(TS|TE|DS|DT|US|UH|UA)\d{4}|^(TOSHIBA)?(MQ0\d|MG0\d|DT0\d|HDW[A-Z]\d|MD0\d|MK\d{4})|^(SAMSUNG|ST\d+LM\d+)?(HN-M\d|HD\d{3}[A-Z]{2})")]
    private static partial Regex SpinningModel();
}
