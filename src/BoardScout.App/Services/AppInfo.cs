using System.Reflection;

namespace BoardScout.Services;

/// <summary>The app's own version, read from the build. Shared by the Windows and Linux editions.</summary>
internal static class AppInfo
{
    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>The commit the build was made from ("1.10.0+abc1234…" → "abc1234"), when the build stamped one.</summary>
    public static string? Commit { get; } = ReadCommit();

    private static string? ReadCommit()
    {
        var full = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plus = full?.IndexOf('+') ?? -1;
        if (full is null || plus < 0 || plus == full.Length - 1) return null;
        var commit = full[(plus + 1)..];
        return commit.Length > 7 ? commit[..7] : commit;
    }
}
