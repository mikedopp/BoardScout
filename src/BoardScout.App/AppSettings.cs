using System.Text.Json;
using BoardScout.Models;

namespace BoardScout;

/// <summary>User preferences, kept as settings.json in the data folder so the portable app carries them.</summary>
internal sealed class AppSettings
{
    public const double DefaultGlassOpacity = 0.62;
    public const int DefaultGlassBlur = 18;

    public bool GlassEffects { get; set; } = true;

    /// <summary>How see-through the browser dashboard's glass panels are, from 0.35 (most) to 1 (solid).</summary>
    public double GlassOpacity { get; set; } = DefaultGlassOpacity;

    /// <summary>Backdrop blur behind the browser dashboard's glass panels, in pixels.</summary>
    public int GlassBlur { get; set; } = DefaultGlassBlur;

    public bool Motion { get; set; } = true;
    public int TelemetryIntervalMs { get; set; } = 1000;
    public bool MinimizeToTray { get; set; } = true;
    public bool PrivacyMode { get; set; }
    /// <summary>Skitter spiders crawling the Connections map; 0 turns them off.</summary>
    public int Crawlers { get; set; }

    private static readonly JsonSerializerOptions WriteOptions =
        new(BoardScoutJson.Default.Options) { WriteIndented = true };

    private static string? _path;

    public static AppSettings Current { get; private set; } = new();

    public static event EventHandler? Changed;

    public static void Load(string dataRoot)
    {
        _path = Path.Combine(dataRoot, "settings.json");
        try
        {
            if (File.Exists(_path))
                Current = JsonSerializer.Deserialize(File.ReadAllText(_path), BoardScoutJson.Default.AppSettings) ?? new();
        }
        catch
        {
            Current = new();
        }
        Current.Normalize();
    }

    public static void Update(Action<AppSettings> change)
    {
        change(Current);
        Current.Normalize();
        try
        {
            if (_path is not null)
                File.WriteAllText(_path, JsonSerializer.Serialize(Current, WriteOptions));
        }
        catch { }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Glass settings back to their defaults; everything else stays.</summary>
    public static void ResetGlass() => Update(s =>
    {
        s.GlassEffects = true;
        s.GlassOpacity = DefaultGlassOpacity;
        s.GlassBlur = DefaultGlassBlur;
    });

    private void Normalize()
    {
        TelemetryIntervalMs = Math.Clamp(TelemetryIntervalMs, 500, 10_000);
        Crawlers = Math.Clamp(Crawlers, 0, 8);
        GlassOpacity = Math.Round(Math.Clamp(double.IsFinite(GlassOpacity) ? GlassOpacity : DefaultGlassOpacity, 0.35, 1), 2);
        GlassBlur = Math.Clamp(GlassBlur, 0, 40);
    }
}
