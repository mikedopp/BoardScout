using System.Globalization;
using System.Text.RegularExpressions;
using BoardScout.DriveHealth;

namespace BoardScout.Services;

/// <summary>What one Android device says is inside it. Every figure is one the device reported; a missing one stays null.</summary>
internal sealed record PhoneFacts
{
    public string Id { get; init; } = "";
    /// <summary>The name its owner gave it ("Galaxy S23 Ultra", or something personal); hidden in privacy mode.</summary>
    public string? GivenName { get; init; }
    public string Maker { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>The name it is sold under, when the model number is one BoardScout knows or the device says so.</summary>
    public string? MarketName { get; init; }
    public string Kind { get; init; } = "Phone";
    public string? Chip { get; init; }
    public string? ChipDetail { get; init; }
    public int Cores { get; init; }
    public double? MemoryGb { get; init; }
    public long? StorageBytes { get; init; }
    public long? StorageFreeBytes { get; init; }
    public string? Resolution { get; init; }
    public int? RefreshHz { get; init; }
    public string? Android { get; init; }
    public string? OneUi { get; init; }
    public string? SecurityPatch { get; init; }
    public bool HasBattery { get; init; }
    public int? BatteryLevel { get; init; }
    public double? BatteryTempC { get; init; }
    /// <summary>The battery's capacity now as a share of new, where the maker reports it (Samsung does).</summary>
    public int? BatteryHealthPercent { get; init; }
    public int? BatteryCycles { get; init; }
    /// <summary>The day the battery was first used, where the maker keeps it (Samsung does).</summary>
    public DateOnly? BatteryFirstUse { get; init; }
    public string? BatteryState { get; init; }
    public string? BatteryCondition { get; init; }
    public double? UptimeHours { get; init; }

    public string Title(bool privacy)
    {
        if (!privacy && GivenName is { Length: > 0 } given) return given;
        if (MarketName is { Length: > 0 } sold) return sold;
        return $"{Maker} {Model}".Trim() is { Length: > 0 } plain ? plain : "Android device";
    }
}

/// <summary>
/// Reads Android phones, tablets, and TVs through adb, and only when asked: one shell call per device that reads
/// properties and status and changes nothing. No serial number, IMEI, account, app list, or file is read.
/// </summary>
internal static class PhoneInventory
{
    /// <summary>One read-only shell call. Each section starts with a ## line.</summary>
    internal const string Script = """
        echo "##props"; getprop | grep -E '^\[(ro\.product\.(model|manufacturer|brand)|ro\.soc\.(model|manufacturer)|ro\.board\.platform|ro\.hardware|ro\.hardware\.chipname|ro\.build\.version\.(release|security_patch|oneui)|ro\.config\.marketing_name|ro\.product\.marketname|ro\.build\.characteristics)\]'
        echo "##name"; settings get global device_name 2>/dev/null
        echo "##mem"; head -1 /proc/meminfo
        echo "##cpu"; for c in /sys/devices/system/cpu/cpu[0-9]*; do echo "${c##*/} $(cat $c/cpufreq/cpuinfo_max_freq 2>/dev/null)"; done
        echo "##cpuinfo"; grep -m1 -E '^Hardware' /proc/cpuinfo
        echo "##battery"; dumpsys battery 2>/dev/null
        echo "##display"; wm size 2>/dev/null; dumpsys display 2>/dev/null | grep -m1 -E 'DisplayDeviceInfo'
        echo "##df"; df -k /data 2>/dev/null
        echo "##uptime"; cat /proc/uptime 2>/dev/null
        """;

    public sealed record Result(string? Adb, List<PhoneFacts> Phones, int WaitingForApproval, int Offline, string? Problem);

    /// <summary>Every device adb can reach, read once each. Nothing is retried: a device that drops is left alone.</summary>
    public static async Task<Result> ReadAllAsync(CancellationToken ct = default)
    {
        if (AndroidDevices.LocateAdb() is not { } adb)
            return new(null, [], 0, 0, "adb was not found on this PC.");
        var devices = await AndroidDevices.ListAsync(adb, ct);
        var phones = new List<PhoneFacts>();
        foreach (var device in devices.Where(d => d.State == "device"))
        {
            var run = await Smartctl.RunAsync(adb, ["-s", device.Serial, "shell", Script.Replace("\r", "")], TimeSpan.FromSeconds(25), ct);
            if (!run.TimedOut && run.Output.Contains("##props", StringComparison.Ordinal))
                phones.Add(Parse(run.Output, device.Serial));
        }
        return new(adb, phones, devices.Count(d => d.State == "unauthorized"), devices.Count(d => d.State == "offline"), null);
    }

    public static PhoneFacts Parse(string output, string id)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var current = "";
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("##", StringComparison.Ordinal)) { current = line[2..].Trim(); sections[current] = []; }
            else if (current.Length > 0) sections[current].Add(line);
        }
        List<string> Section(string name) => sections.GetValueOrDefault(name) ?? [];

        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Section("props"))
            if (Regex.Match(line, @"^\[([^\]]+)\]:\s*\[(.*)\]\s*$") is { Success: true } m) props[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        string? Prop(params string[] keys) => keys.Select(k => props.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var maker = Pretty(Prop("ro.product.manufacturer", "ro.product.brand") ?? "");
        var model = Prop("ro.product.model") ?? "";
        var characteristics = Prop("ro.build.characteristics") ?? "";
        var kind = characteristics.Contains("tv", StringComparison.OrdinalIgnoreCase) ? "TV"
            : characteristics.Contains("tablet", StringComparison.OrdinalIgnoreCase) ? "Tablet" : "Phone";

        // Cores grouped by their top speed, fastest first: "1 × 3.36 + 4 × 2.80 + 3 × 2.02 GHz".
        var speeds = Section("cpu").Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 1 && p[0].StartsWith("cpu", StringComparison.Ordinal))
            .Select(p => p.Length > 1 && long.TryParse(p[1], out var khz) ? khz : 0).ToList();
        var groups = speeds.Where(s => s > 0).GroupBy(s => s).OrderByDescending(g => g.Key).ToList();
        var layout = groups.Count switch
        {
            0 => "",
            1 => $"{groups[0].Key / 1e6:0.00} GHz",
            _ => string.Join(" + ", groups.Select(g => $"{g.Count()} × {g.Key / 1e6:0.00}")) + " GHz"
        };
        var cores = speeds.Count;
        var chipCode = Prop("ro.soc.model", "ro.hardware.chipname") ??
                       Section("cpuinfo").Select(l => Regex.Match(l, @"^Hardware\s*:\s*(.+)$")).FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim() ??
                       Prop("ro.board.platform", "ro.hardware");

        double? memory = Section("mem").Select(l => Regex.Match(l, @"MemTotal:\s*(\d+)\s*kB")).FirstOrDefault(m => m.Success) is { } mem
            ? long.Parse(mem.Groups[1].Value, CultureInfo.InvariantCulture) / 1048576d : null;

        long? size = null, free = null;
        if (Section("df").LastOrDefault(l => Regex.IsMatch(l, @"\s/data(/\S*)?\s*$")) is { } df &&
            df.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 4 } parts &&
            long.TryParse(parts[1], out var blocks) && long.TryParse(parts[3], out var available))
        {
            size = blocks * 1024;
            free = available * 1024;
        }

        string? resolution = null;
        if (Section("display").Select(l => Regex.Match(l, @"Physical size:\s*(\d+)x(\d+)")).FirstOrDefault(m => m.Success) is { } physical)
        {
            int a = int.Parse(physical.Groups[1].Value, CultureInfo.InvariantCulture), b = int.Parse(physical.Groups[2].Value, CultureInfo.InvariantCulture);
            resolution = $"{Math.Max(a, b)} × {Math.Min(a, b)}";
        }
        var rates = Section("display").SelectMany(l => Regex.Matches(l, @"fps=(\d+(?:\.\d+)?)").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))).ToList();
        int? refresh = rates.Count > 0 ? (int)Math.Round(rates.Max()) : null;

        var battery = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Section("battery"))
            // Samsung writes its own figures in brackets ("mSavedBatteryAsoc: [90]"); the log lines further down start with a date and are skipped.
            if (Regex.Match(line, @"^\s*([A-Za-z][A-Za-z ]*?)\s*:\s*(.*)$") is { Success: true } b)
                battery.TryAdd(b.Groups[1].Value.Trim(), b.Groups[2].Value.Trim().TrimStart('[').TrimEnd(']').Trim());
        int? Number(string key) => battery.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        // A TV or a box reports a stand-in battery; only a device with a real one names its chemistry.
        var hasBattery = kind != "TV" && battery.GetValueOrDefault("present") == "true" && battery.GetValueOrDefault("technology") is { Length: > 0 };
        var level = Number("level") is { } l && Number("scale") is { } scale and > 0 ? (int)Math.Round(l * 100d / scale) : Number("level");
        // Samsung keeps its own estimate of capacity against new (a newer figure on recent phones, an older one on all
        // of them), a use counter in hundredths of a cycle, and the day the battery was first used.
        var health = Number("mSavedBatteryBsoh") is { } bsoh and > 0 and <= 100 ? bsoh
            : Number("mSavedBatteryAsoc") is { } asoc and > 0 and <= 100 ? asoc : (int?)null;
        DateOnly? firstUse = battery.TryGetValue("battery FirstUseDate", out var used) &&
                             DateOnly.TryParseExact(used, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day.Year >= 2008
            ? day : null;
        var cycles = Number("Cycle count") is { } count and > 0 ? count
            : Number("mSavedBatteryUsage") is { } usage and > 0 ? usage / 100 : (int?)null; // whole cycles completed
        var powered = new[] { "AC powered", "USB powered", "Wireless powered", "Dock powered" }.Any(k => battery.GetValueOrDefault(k) == "true");
        var state = Number("status") switch
        {
            2 => "Charging",
            5 => "Full",
            3 or 4 => powered ? "Plugged in, not charging" : "On battery",
            _ => null
        };
        var condition = Number("health") switch
        {
            2 => "Good", 3 => "Overheated", 4 => "Dead", 5 => "Over voltage", 6 => "Failed", 7 => "Cold", _ => null
        };

        string? oneUi = Prop("ro.build.version.oneui") is { } ui && int.TryParse(ui, out var code) && code >= 10000
            ? $"{code / 10000}.{code / 100 % 100}" : null;
        double? uptime = Section("uptime").FirstOrDefault() is { } up && double.TryParse(up.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds / 3600 : null;
        var given = Section("name").FirstOrDefault(l => l.Trim() is { Length: > 0 } and not "null")?.Trim();

        return new PhoneFacts
        {
            Id = id,
            GivenName = given,
            Maker = maker,
            Model = model,
            MarketName = Prop("ro.config.marketing_name", "ro.product.marketname") ?? KnownName(maker, model),
            Kind = kind,
            Chip = chipCode is null ? null : ChipName(chipCode),
            ChipDetail = cores > 0 ? $"{cores} cores" + (layout.Length > 0 ? " · " + layout : "") : null,
            Cores = cores,
            MemoryGb = memory,
            StorageBytes = size,
            StorageFreeBytes = free,
            Resolution = resolution,
            RefreshHz = refresh,
            Android = Prop("ro.build.version.release"),
            OneUi = oneUi,
            SecurityPatch = Prop("ro.build.version.security_patch"),
            HasBattery = hasBattery,
            BatteryLevel = hasBattery ? level : null,
            BatteryTempC = hasBattery && Number("temperature") is { } tenths ? tenths / 10d : null,
            BatteryHealthPercent = hasBattery ? health : null,
            BatteryCycles = hasBattery ? cycles : null,
            BatteryFirstUse = hasBattery ? firstUse : null,
            BatteryState = hasBattery ? state : null,
            BatteryCondition = hasBattery ? condition : null,
            UptimeHours = uptime
        };
    }

    /// <summary>The plates for a device: chip, memory, storage, display, system. A part it said nothing about gets none.</summary>
    public static List<RigSpec> Specs(PhoneFacts phone)
    {
        var specs = new List<RigSpec>();
        if (phone.Chip is { Length: > 0 } chip) specs.Add(new("Chip", chip, phone.ChipDetail ?? ""));
        if (phone.MemoryGb is { } memory)
            specs.Add(new("Memory", $"{Marketed(memory, [1, 1.5, 2, 3, 4, 6, 8, 12, 16, 18, 24, 32]):0.#} GB", $"{memory:0.0} GB usable"));
        if (phone.StorageBytes is { } size)
            specs.Add(new("Storage", $"{Marketed(size / 1e9, [8, 16, 32, 64, 128, 256, 512, 1024, 2048]):0} GB",
                phone.StorageFreeBytes is { } free ? $"{RigSpecs.Size(free)} free" : ""));
        if (phone.Resolution is { } resolution)
            specs.Add(new("Display", resolution, phone.RefreshHz is { } hz ? $"{hz} Hz" : ""));
        if (phone.Android is { Length: > 0 } android)
            specs.Add(new("System", $"Android {android}", string.Join(" · ", new[]
            {
                phone.OneUi is { } ui ? $"One UI {ui}" : null,
                phone.SecurityPatch is { Length: > 0 } patch ? $"patched {patch}" : null
            }.Where(p => p is not null))));
        return specs;
    }

    // What the box says: the smallest size sold that holds what the device reports (some is always held back).
    internal static double Marketed(double reported, double[] sold) => sold.FirstOrDefault(s => s >= reported * 0.98, Math.Ceiling(reported));

    private static string Pretty(string maker) =>
        maker.Length == 0 ? "" : maker.Equals("samsung", StringComparison.OrdinalIgnoreCase) ? "Samsung" : char.ToUpperInvariant(maker[0]) + maker[1..];

    /// <summary>The names the common flagship chips are sold under; anything else is shown by its own code.</summary>
    internal static string ChipName(string code) => code.Trim().ToUpperInvariant() switch
    {
        "SM8750" => "Snapdragon 8 Elite",
        "SM8650" => "Snapdragon 8 Gen 3",
        "SM8550" => "Snapdragon 8 Gen 2",
        "SM8475" => "Snapdragon 8+ Gen 1",
        "SM8450" => "Snapdragon 8 Gen 1",
        "SM8350" => "Snapdragon 888",
        "S5E9945" => "Exynos 2400",
        "S5E9925" => "Exynos 2200",
        "S5E9840" => "Exynos 2100",
        _ => code.Trim()
    };

    /// <summary>Galaxy model numbers BoardScout can put a name to; the letter after the digits is the region.</summary>
    internal static string? KnownName(string maker, string model)
    {
        if (!maker.Equals("Samsung", StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(model, @"^SM-([A-Z]\d{3})");
        if (!match.Success) return null;
        return match.Groups[1].Value switch
        {
            "S901" => "Galaxy S22", "S906" => "Galaxy S22+", "S908" => "Galaxy S22 Ultra",
            "S911" => "Galaxy S23", "S916" => "Galaxy S23+", "S918" => "Galaxy S23 Ultra",
            "S921" => "Galaxy S24", "S926" => "Galaxy S24+", "S928" => "Galaxy S24 Ultra",
            "S931" => "Galaxy S25", "S936" => "Galaxy S25+", "S938" => "Galaxy S25 Ultra",
            "T290" => "Galaxy Tab A 8.0 (2019)",
            _ => null
        };
    }

    /// <summary>A made-up Galaxy in the shape a real Galaxy S23 Ultra answered in, for the tests and the off-screen QA render.</summary>
    internal const string SampleProbe = """
        ##props
        [ro.board.platform]: [kalama]
        [ro.build.characteristics]: [phone]
        [ro.build.version.oneui]: [80000]
        [ro.build.version.release]: [16]
        [ro.build.version.security_patch]: [2026-10-01]
        [ro.hardware]: [qcom]
        [ro.product.manufacturer]: [samsung]
        [ro.product.model]: [SM-S918U]
        [ro.soc.manufacturer]: [QTI]
        [ro.soc.model]: [SM8550]
        ##name
        Galaxy S23 Ultra
        ##mem
        MemTotal:       11482880 kB
        ##cpu
        cpu0 2016000
        cpu1 2016000
        cpu2 2016000
        cpu3 2803200
        cpu4 2803200
        cpu5 2803200
        cpu6 2803200
        cpu7 3360000
        ##cpuinfo
        ##battery
        Current Battery Service state:
          AC powered: false
          USB powered: true
          Wireless powered: false
          Charge counter: 3912000
          status: 2
          health: 2
          present: true
          level: 78
          scale: 100
          voltage: 4213
          temperature: 312
          technology: Li-ion
          battery FirstUseDate: [20230402]
          mSavedBatteryAsoc: [91]
          mSavedBatteryUsage: [41250]

        [BattActionChangedLogBuffer]
        11-20 12:48:45.540  Sending ACTION_BATTERY_CHANGED: level:41, status:3, health:2, temperature:266, cycle_count:0, present:true, scale:100, technology:Li-ion
        ##display
        Physical size: 1440x3088
          DisplayDeviceInfo{"Built-in Screen": uniqueId="local:0", 1440 x 3088, modeId 6, renderFrameRate 24.000002, supportedRefreshRates [120.00001, 60.000004, 24.000002], supportedModes [{id=1, width=1440, height=3088, fps=120.00001, vsync=120.00001}, {id=3, width=1440, height=3088, fps=60.000004}, {id=6, width=1440, height=3088, fps=24.000002}]}
        ##df
        Filesystem       1K-blocks      Used Available Use% Mounted on
        /dev/block/dm-52 478150656 187040000 291110656  40% /data/user/0
        ##uptime
        432120.55 3401233.10
        """;
}
