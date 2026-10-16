using Glimmer.WinForms;

namespace BoardScout.UI;

/// <summary>
/// The version number on a Glimmer orb badge, aurora like the browser dashboard's. The orb thinks while a scan,
/// driver check, or download runs, turns mint for a moment when it finishes, and stays red after a failure until
/// the next run or a click. Clicking it opens the version pop-out (runtime, sensors, troubleshooting, settings,
/// dependencies, requirements, legal).
/// </summary>
internal sealed class VersionButton : GlimmerBadge
{
    public static string AppVersion => Services.AppInfo.Version;

    public VersionButton()
    {
        Preset = "aurora";
        Font = new Font(MonoFamily(), 9.25f);
        ForeColor = Color.FromArgb(0xCF, 0xE6, 0xFF);
        Height = 30;
        Text = $"v{AppVersion}";
        AccessibleName = $"BoardScout version {AppVersion}";
        AccessibleDescription = "Opens runtime, sensor, troubleshooting, settings, and dependency details.";
        RefreshMotion();
    }

    /// <summary>The orb holds still when motion is off, and the pill goes flat when glass is off.</summary>
    public void RefreshMotion()
    {
        MotionEnabled = Motion.Enabled;
        GlassEffects = Glass.Enabled;
    }

    // The dashboard's --mono stack: Cascadia Mono where Windows has it, Consolas otherwise.
    private static string MonoFamily()
    {
        foreach (var name in (string[])["Cascadia Mono SemiBold", "Cascadia Mono", "Consolas"])
        {
            using var probe = new Font(name, 9f);
            if (probe.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return FontFamily.GenericMonospace.Name;
    }
}
