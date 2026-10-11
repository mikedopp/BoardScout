using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoardScout;

/// <summary>Preferences for the Linux edition, kept as settings.json in the data folder.</summary>
internal sealed class AppSettings
{
    public const double DefaultGlassOpacity = 0.62;
    public const int DefaultGlassBlur = 18;

    /// <summary>Translucent glass surfaces; off gives solid panels everywhere.</summary>
    public bool GlassEffects { get; set; } = true;

    /// <summary>How see-through glass panels are, from 0.35 (most) to 1 (solid).</summary>
    public double GlassOpacity { get; set; } = DefaultGlassOpacity;

    /// <summary>Backdrop blur behind glass panels, in pixels.</summary>
    public int GlassBlur { get; set; } = DefaultGlassBlur;

    public bool Motion { get; set; } = true;
    public int TelemetryIntervalMs { get; set; } = 1000;
    public bool PrivacyMode { get; set; }

    /// <summary>Skitter spiders crawling the Connections map; 0 turns them off.</summary>
    public int Crawlers { get; set; }

    /// <summary>Open the dashboard in the browser when BoardScout starts on a desktop.</summary>
    public bool OpenBrowser { get; set; } = true;

    /// <summary>The local port the dashboard is served on.</summary>
    public int Port { get; set; } = 8797;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static string? _path;
    private static readonly object Gate = new();

    public static AppSettings Current { get; private set; } = new();

    public static event EventHandler? Changed;

    public static void Load(string dataRoot)
    {
        _path = Path.Combine(dataRoot, "settings.json");
        try
        {
            if (File.Exists(_path))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new();
        }
        catch
        {
            Current = new();
        }
        Current.Normalize();
    }

    public static void Update(Action<AppSettings> change)
    {
        lock (Gate)
        {
            change(Current);
            Current.Normalize();
            try
            {
                if (_path is not null) File.WriteAllText(_path, JsonSerializer.Serialize(Current, Json));
            }
            catch { }
        }
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
        Port = Port is >= 1024 and <= 65535 ? Port : 8797;
    }
}
