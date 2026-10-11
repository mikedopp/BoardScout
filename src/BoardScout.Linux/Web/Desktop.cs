using System.Diagnostics;

namespace BoardScout.Web;

/// <summary>Desktop integration: opening the browser, and an app-menu entry with an icon (user-level, no root).</summary>
internal static class Desktop
{
    /// <summary>A graphical session for this user, where opening a browser makes sense.</summary>
    public static bool HasDisplay =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public static void OpenBrowser(string url)
    {
        // A root process opening the user's browser would run it as root; print the link instead.
        if (!HasDisplay || Environment.GetEnvironmentVariable("SUDO_USER") is not null) return;
        try
        {
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(url);
            Process.Start(start)?.Dispose();
        }
        catch
        {
            Console.WriteLine("Couldn't open a browser (xdg-open is missing); open the link above yourself.");
        }
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string BinPath => Path.Combine(Home, ".local", "bin", "boardscout");
    private static string EntryPath => Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Home, ".local", "share"), "applications", "boardscout.desktop");
    private static string IconPath => Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Home, ".local", "share"), "icons", "hicolor", "scalable", "apps", "boardscout.svg");

    /// <summary>Copies the program to ~/.local/bin and adds BoardScout to the app menu. Touches nothing outside your home folder.</summary>
    public static int Install()
    {
        var self = Environment.ProcessPath;
        if (self is null || !File.Exists(self))
        {
            Console.Error.WriteLine("Can't find the running program to install.");
            return 1;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(BinPath)!);
        // The standalone binary carries its web views inside; the portable folder keeps them beside the program.
        var standalone = !Directory.Exists(Path.Combine(Path.GetDirectoryName(self)!, "Assets"));
        string target;
        if (standalone)
        {
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(BinPath), StringComparison.Ordinal)) File.Copy(self, BinPath, overwrite: true);
            File.SetUnixFileMode(BinPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                           UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            target = BinPath;
            Console.WriteLine($"Installed {BinPath}");
        }
        else
        {
            // The portable folder keeps its web views beside the program: point the menu entry at it where it is.
            target = self;
            Console.WriteLine($"Using the portable folder in place: {Path.GetDirectoryName(self)}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(IconPath)!);
        var icon = Path.Combine(AppContext.BaseDirectory, "Shell", "icon.svg");
        if (File.Exists(icon)) File.Copy(icon, IconPath, overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        File.WriteAllText(EntryPath, $"""
            [Desktop Entry]
            Type=Application
            Name=BoardScout
            GenericName=Hardware dashboard
            Comment=Motherboard, connections, drives, and system health in your browser
            Exec="{target}" --stop-when-closed
            Icon=boardscout
            Terminal=false
            Categories=System;Monitor;HardwareSettings;
            Keywords=hardware;motherboard;usb;pcie;drives;sensors;network;
            StartupNotify=false

            """);
        Console.WriteLine($"Added {EntryPath}");
        if (Platform.Tools.Has("update-desktop-database")) Platform.Tools.Run("update-desktop-database", Path.GetDirectoryName(EntryPath)!);
        Console.WriteLine("BoardScout is in your app menu. Remove it with: boardscout --uninstall");
        return 0;
    }

    public static int Uninstall()
    {
        foreach (var path in new[] { EntryPath, IconPath, BinPath })
        {
            if (!File.Exists(path)) continue;
            File.Delete(path);
            Console.WriteLine($"Removed {path}");
        }
        Console.WriteLine($"Settings stay in {DataFolder.Root}; delete that folder too to remove them.");
        return 0;
    }
}
