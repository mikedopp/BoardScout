using System.Diagnostics;

namespace BoardScout.UI;

/// <summary>
/// One shared ~60 fps UI-thread ticker for every animation, stopped whenever nothing is moving.
/// A step receives elapsed seconds and returns true while it still has work to do.
/// </summary>
internal static class Motion
{
    private static readonly System.Windows.Forms.Timer Ticker = new() { Interval = 15 };
    private static readonly List<Func<float, bool>> Steps = [];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _lastTick;

    static Motion() => Ticker.Tick += (_, _) => Tick();

    /// <summary>Animations run only when the user allows motion here and in Windows.</summary>
    public static bool Enabled => AppSettings.Current.Motion && SystemInformation.UIEffectsEnabled;

    public static void Start(Func<float, bool> step)
    {
        if (!Steps.Contains(step)) Steps.Add(step);
        if (Ticker.Enabled) return;
        _lastTick = Clock.Elapsed.TotalSeconds;
        Ticker.Start();
    }

    /// <summary>Frame-rate independent exponential approach; a higher rate settles faster.</summary>
    public static float Approach(float current, float target, float rate, float seconds) =>
        target + (current - target) * MathF.Exp(-rate * seconds);

    private static void Tick()
    {
        var now = Clock.Elapsed.TotalSeconds;
        var seconds = (float)Math.Clamp(now - _lastTick, 0, 0.05);
        _lastTick = now;
        for (var i = Steps.Count - 1; i >= 0; i--)
        {
            bool running;
            try { running = Steps[i](seconds); }
            catch { running = false; }
            if (!running) Steps.RemoveAt(i);
        }
        if (Steps.Count == 0) Ticker.Stop();
    }
}
