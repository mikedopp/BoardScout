using System.Globalization;
using System.Runtime.InteropServices;

namespace BoardScout.Platform;

/// <summary>
/// Reads the kernel's own files under /sys and /proc. Every call is a plain read of a world-readable file;
/// nothing here writes, opens a device, or sends anything to hardware. Missing files read as null.
/// </summary>
internal static partial class Sys
{
    public static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The value, or null when it is missing, empty, or a placeholder firmware leaves in.</summary>
    public static string? Text(string path)
    {
        var value = Read(path)?.Trim('\0', ' ');
        return string.IsNullOrEmpty(value) || IsFiller(value) ? null : value;
    }

    public static byte[]? Bytes(string path, int max = 1 << 20)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var buffer = new byte[max];
            var total = 0;
            int read;
            while (total < max && (read = stream.Read(buffer, total, max - total)) > 0) total += read;
            return buffer[..total];
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<string> Lines(string path)
    {
        string[] lines;
        try
        {
            lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch
        {
            lines = [];
        }
        return lines;
    }

    public static long? Long(string path) =>
        long.TryParse(Read(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static int? Int(string path) =>
        int.TryParse(Read(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>"0x8086" → 0x8086.</summary>
    public static int? Hex(string path)
    {
        var text = Read(path);
        if (text is null) return null;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>The name a symlink points at ("../../../bus/pci/drivers/nvme" → "nvme"), or null.</summary>
    public static string? LinkName(string path)
    {
        try
        {
            var target = new FileInfo(path).LinkTarget;
            return target is null ? null : Path.GetFileName(target.TrimEnd('/'));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The fully resolved path (every symlink followed), or null when it doesn't exist.</summary>
    public static string? Real(string path)
    {
        if (!Path.Exists(path)) return null;
        try
        {
            var resolved = realpath(path, IntPtr.Zero);
            if (resolved == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(resolved); }
            finally { free(resolved); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Path.GetFullPath(path);
        }
    }

    public static IEnumerable<string> Children(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).Order(StringComparer.Ordinal).ToList()
                : [];
        }
        catch
        {
            return [];
        }
    }

    public static bool Exists(string path) => Path.Exists(path);

    /// <summary>Placeholder strings firmware and bridges fill in when a value isn't set.</summary>
    public static bool IsFiller(string value) =>
        value.Trim('0', 'F', 'f', ' ', '-', '.').Length == 0 ||
        value.Contains("O.E.M", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase) ||
        value is "Default string" or "System Product Name" or "System manufacturer" or "System Serial Number" or
            "Not Specified" or "Not Applicable" or "None" or "Unknown" or "Type1ProductConfigId" or "Base Board Product Name";

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr realpath(string path, IntPtr resolved);

    [LibraryImport("libc")]
    private static partial void free(IntPtr pointer);
}
