namespace BoardScout;

/// <summary>
/// Where BoardScout keeps its settings, log, and the running instance's address: $BOARDSCOUT_DATA, else
/// $XDG_DATA_HOME/boardscout, else ~/.local/share/boardscout. Nothing else on the system is written.
/// </summary>
internal static class DataFolder
{
    private static string? _root;

    public static string Root => _root ??= Resolve();

    public static string Resolve()
    {
        var chosen = Environment.GetEnvironmentVariable("BOARDSCOUT_DATA");
        if (string.IsNullOrWhiteSpace(chosen))
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var baseDir = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".local", "share");
            chosen = Path.Combine(baseDir, "boardscout");
        }
        Directory.CreateDirectory(chosen);
        // The folder holds the dashboard's access token: only this user may read it.
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(chosen, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { }
        }
        return chosen;
    }

    /// <summary>Writes a file only this user can read (the instance file carries the access token).</summary>
    public static void WritePrivate(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { }
        }
        File.Move(temp, path, overwrite: true);
    }
}
