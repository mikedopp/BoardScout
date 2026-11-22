using System.Globalization;
using RAMSPDToolkit.I2CSMBus;
using RAMSPDToolkit.SPD;
using RAMSPDToolkit.SPD.Interop.Shared;

namespace BoardScout.Services;

/// <summary>
/// What one memory stick's SPD chip says about itself: who made it, when, and (when the stick has one)
/// its temperature sensor. DDR4 sticks carry a sensor only when the maker fitted a TSOD (JEDEC JC-42.4,
/// mostly server and some high-end kits); every DDR5 stick has one in its SPD5118 hub.
/// </summary>
public sealed record DimmSpdInfo(
    byte Address,
    string Kind,
    string? ModuleMaker,
    string? DramMaker,
    string? PartNumber,
    string? Serial,
    DimmMade? Made,
    bool HasThermalSensor,
    double? TemperatureC,
    double? LowLimitC,
    double? HighLimitC,
    double? CriticalHighC,
    string? SensorStatus,
    bool WriteProtected);

/// <summary>An SPD manufacture date: a year and an ISO-style week number, both stored as BCD.</summary>
public readonly record struct DimmMade(int Year, int Week)
{
    /// <summary>Monday of that week, close enough to say how old the stick is.</summary>
    public DateOnly Approx => DateOnly.FromDateTime(ISOWeek.ToDateTime(Year, Math.Clamp(Week, 1, ISOWeek.GetWeeksInYear(Year)), DayOfWeek.Monday));

    public override string ToString() => $"week {Week} of {Year}";
}

internal static class DimmSpd
{
    // DDR4 SPD (JEDEC 21-C 4.1.2.L-5): manufacturing date bytes 323 (year) and 324 (week). DDR5 (JESD400-5): 515 and 516.
    public const int Ddr4DateOffset = 323;
    public const int Ddr5DateOffset = 515;
    // The 4-byte module serial follows the date: DDR4 325-328, DDR5 517-520.
    public const int Ddr4SerialOffset = 325;
    public const int Ddr5SerialOffset = 517;

    /// <summary>
    /// Reads every SPD on the SMBuses the sensor library found. Call it only after a LibreHardwareMonitor
    /// <c>Computer</c> with memory enabled is open (that loads the driver and finds the buses), from the
    /// thread that owns it. It never writes the SPD's memory; the only writes are the page selects the
    /// chip needs to reach bytes past 255, and it puts the chip back on page 0 before it lets go.
    /// </summary>
    public static List<DimmSpdInfo> ReadAll()
    {
        var list = new List<DimmSpdInfo>();
        IReadOnlyList<SMBusInterface> buses;
        try { buses = SMBusManager.RegisteredSMBuses; }
        catch { return list; }

        foreach (var bus in buses)
        {
            // SPD EEPROMs answer at 0x50-0x57, one address per slot on the bus.
            for (byte address = 0x50; address <= 0x57; address++)
            {
                try
                {
                    var detector = new SPDDetector(bus, address);
                    if (!detector.IsValid || detector.Accessor is not { } spd) continue;
                    try { list.Add(Describe(address, detector.SPDMemoryType, spd)); }
                    finally
                    {
                        // Reading byte 0 selects page 0 again. Done here, not left to the accessor's finalizer,
                        // which would switch pages on the GC thread while the sensor library reads the bus,
                        // or never if the app exits first (some boards dislike a DDR4 SPD left on page 1).
                        try { spd.At(0); } catch { }
                        GC.SuppressFinalize(spd);
                    }
                }
                catch
                {
                    // One stick that won't answer shouldn't hide the others.
                }
            }
        }
        return [.. list.DistinctBy(d => d.Address).OrderBy(d => d.Address)];
    }

    private static DimmSpdInfo Describe(byte address, SPDMemoryType type, SPDAccessor spd)
    {
        var kind = type switch
        {
            SPDMemoryType.SPD_DDR4_SDRAM or SPDMemoryType.SPD_DDR4E_SDRAM or SPDMemoryType.SPD_LPDDR4_SDRAM or SPDMemoryType.SPD_LPDDR4X_SDRAM => "DDR4",
            SPDMemoryType.SPD_DDR5_SDRAM or SPDMemoryType.SPD_LPDDR5_SDRAM => "DDR5",
            _ => type.ToString().Replace("SPD_", "", StringComparison.Ordinal).Replace("_SDRAM", "", StringComparison.Ordinal)
        };

        DimmMade? made = null;
        try
        {
            var offset = kind == "DDR5" ? Ddr5DateOffset : Ddr4DateOffset;
            made = DecodeDate(spd.At((ushort)offset), spd.At((ushort)(offset + 1)), DateTime.UtcNow.Year);
        }
        catch { }

        string? serial = null;
        try
        {
            var offset = kind == "DDR5" ? Ddr5SerialOffset : Ddr4SerialOffset;
            serial = SerialFromBytes([spd.At((ushort)offset), spd.At((ushort)(offset + 1)), spd.At((ushort)(offset + 2)), spd.At((ushort)(offset + 3))]);
        }
        catch { serial = CleanSerial(Safe(spd.ModuleSerialNumber)); }

        bool hasSensor = false;
        double? temp = null, low = null, high = null, crit = null;
        if (spd is DDR4AccessorBase d4)
        {
            hasSensor = d4.HasThermalSensor;
            if (hasSensor && d4.UpdateTemperature())
            {
                temp = Celsius(d4.Temperature);
                low = Celsius(d4.ThermalSensorLowLimit);
                high = Celsius(d4.ThermalSensorHighLimit);
            }
        }
        else if (spd is DDR5AccessorBase d5)
        {
            hasSensor = d5.HasThermalSensor;
            if (hasSensor && d5.UpdateTemperature())
            {
                temp = Celsius(d5.Temperature);
                low = Celsius(d5.ThermalSensorLowLimit);
                high = Celsius(d5.ThermalSensorHighLimit);
            }
            if (spd is DDR5Accessor full && hasSensor)
                crit = Celsius(full.ThermalSensorCriticalHighLimit);
        }

        return new DimmSpdInfo(address, kind,
            Clean(Safe(spd.GetModuleManufacturerString)), Clean(Safe(spd.GetDRAMManufacturerString)),
            Clean(Safe(spd.ModulePartNumber)), serial,
            made, hasSensor, temp, low, high, crit, AlarmText(temp, low, high, crit), Safe(() => spd.HasSPDWriteProtection));
    }

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); } catch { return default; }
    }

    private static double? Celsius(float value) => float.IsFinite(value) && value is > -40 and < 150 ? Math.Round(value, 2) : null;

    // ---- Pure helpers (unit tested) -----------------------------------------------------------------------

    /// <summary>
    /// SPD dates are two BCD bytes: year since 2000 and week 1-52/53. Makers that skip it leave 0x00 or 0xFF,
    /// which read as no date. <paramref name="thisYear"/> rejects dates from the future.
    /// </summary>
    public static DimmMade? DecodeDate(byte yearBcd, byte weekBcd, int thisYear)
    {
        if (!TryBcd(yearBcd, out var yy) || !TryBcd(weekBcd, out var ww)) return null;
        if (ww is < 1 or > 53) return null;
        var year = 2000 + yy;
        if (year > thisYear) return null;
        return new DimmMade(year, ww);
    }

    private static bool TryBcd(byte value, out int result)
    {
        int hi = value >> 4, lo = value & 0xF;
        result = hi * 10 + lo;
        return hi <= 9 && lo <= 9;
    }

    /// <summary>The SPD serial as the maker's label prints it: four bytes in hex, none when blank.</summary>
    public static string? SerialFromBytes(byte[] bytes) => CleanSerial(Convert.ToHexString(bytes));

    /// <summary>Blank, all-zero or all-F serials are the maker not filling it in.</summary>
    public static string? CleanSerial(string? serial)
    {
        var s = Clean(serial);
        if (s is null) return null;
        return s.Trim('0').Length == 0 || s.Trim('F', 'f').Length == 0 ? null : s;
    }

    private static string? Clean(string? text)
    {
        if (text is null) return null;
        var s = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length == 0 || s.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    /// <summary>
    /// Whether a reading is past the stick's own limits. Worked out from the reading rather than taken from the
    /// chip's status register, which the library fills only on a full refresh.
    /// </summary>
    public static string? AlarmText(double? temp, double? low, double? high, double? critical)
    {
        if (temp is not { } t) return null;
        if (critical is { } c && c > 0 && t >= c) return "above its critical limit";
        if (high is { } h && h > 0 && t > h) return "above its high limit";
        if (low is { } l && l > 0 && t < l) return "below its low limit";
        return null;
    }

    /// <summary>
    /// The sensor library names a stick's hardware "DIMM #n", or "maker - part (#n)" once it has read the part
    /// number, where n is the SPD address less 0x50, not the stick's place in the list.
    /// </summary>
    public static bool IsZoneFor(string zone, byte address)
    {
        var n = address - 0x50;
        return zone == $"DIMM #{n}" || zone.EndsWith($"(#{n})", StringComparison.Ordinal);
    }

    /// <summary>"about 3 years old" from a manufacture date.</summary>
    public static string AgeText(DimmMade made, DateOnly today)
    {
        var days = today.DayNumber - made.Approx.DayNumber;
        if (days < 0) return "made recently";
        var years = days / 365.25;
        if (years < 1) return $"about {Math.Max(1, (int)Math.Round(days / 30.4))} months old";
        return years < 1.5 ? "about a year old" : $"about {Math.Round(years):0} years old";
    }

    /// <summary>
    /// The facts a memory slot shows for its stick's SPD. <paramref name="elevated"/> false means the SPD
    /// could not be read at all, which is said once rather than guessed.
    /// </summary>
    public static List<(string Label, string Value)> Facts(DimmSpdInfo? spd, bool elevated, DateOnly today)
    {
        var facts = new List<(string, string)>();
        if (spd is null)
        {
            facts.Add(("SPD", elevated ? "This stick's SPD chip did not answer (reading it needs the PawnIO driver)" : "Run BoardScout as administrator to read the stick's own chip (temperature, maker, date)"));
            return facts;
        }

        if (spd.HasThermalSensor)
        {
            var reading = spd.TemperatureC is { } t ? $"{t:0.0} °C" : "sensor did not answer";
            var limit = spd.CriticalHighC ?? spd.HighLimitC;
            facts.Add(("Temperature", limit is { } l ? $"{reading} (alarm at {l:0} °C)" : reading));
            if (spd.SensorStatus is { } status) facts.Add(("Sensor alarm", $"Stick is {status}"));
        }
        else
        {
            facts.Add(("Temperature", spd.Kind == "DDR4"
                ? "No sensor on this stick (DDR4 without a TSOD chip)"
                : "No sensor reported by this stick"));
        }

        if (spd.ModuleMaker is { } maker) facts.Add(("Module maker", maker));
        if (spd.DramMaker is { } dram) facts.Add(("Memory chips", dram));
        facts.Add(("Made", spd.Made is { } made ? $"{made} ({AgeText(made, today)})" : "Not recorded by the maker"));
        if (spd.Serial is { } serial) facts.Add(("Serial", serial));
        return facts;
    }
}
