using System.Text.RegularExpressions;

namespace BoardScout.Services;

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
