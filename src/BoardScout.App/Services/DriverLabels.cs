namespace BoardScout.Services;

/// <summary>
/// The driver check's own codes ("update-available", "ms-catalog", "gpu") as words for the Drivers table.
/// The codes stay as they are in scans, reports, and exports; only what is shown changes.
/// </summary>
internal static class DriverLabels
{
    public const string NotChecked = "not checked";

    public static string Status(string? code) => (code ?? "").Trim().ToLowerInvariant() switch
    {
        "update-available" => "Update available",
        "current" => "Up to date",
        "manual-check" => "Check by hand",
        "error" => "Check failed",
        "unknown" => "Could not tell",
        "" or NotChecked => "Not checked",
        var other => Sentence(other)
    };

    public static string Category(string? code) => (code ?? "").Trim().ToLowerInvariant() switch
    {
        "bios" => "BIOS",
        "gpu" => "Graphics",
        "" => "—",
        var other => Sentence(other)
    };

    /// <summary>Who the newer version was found at: the maker's name, or the kind of maker when the check covers several.</summary>
    public static string Source(string? code) => (code ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "—" => "—",
        "amd" => "AMD",
        "asrock" => "ASRock",
        "nvidia" => "NVIDIA",
        "ms-catalog" => "Microsoft Update Catalog",
        "dell-catalog" => "Dell",
        "lenovo-catalog" => "Lenovo",
        "fwupd" => "LVFS (fwupd)",
        "oem-bios" => "PC maker",
        "ssd-vendor" => "Drive maker",
        "router-firmware" => "Router maker",
        "mikrotik-api" => "MikroTik",
        "ubiquiti-api" => "Ubiquiti",
        var other => Sentence(other)
    };

    // "some-new-code" reads as "Some new code" until it gets a name of its own above.
    private static string Sentence(string code)
    {
        var words = code.Replace('-', ' ').Replace('_', ' ').Trim();
        return words.Length == 0 ? "—" : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
