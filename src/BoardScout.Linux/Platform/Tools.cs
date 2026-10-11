using System.Collections.Concurrent;
using System.Diagnostics;

namespace BoardScout.Platform;

/// <summary>
/// Optional command-line tools that add detail when they are installed (iw, nmcli, bluetoothctl, fwupdmgr,
/// systemctl, apt-get, dpkg-query, rpm, snap, flatpak, dotnet). Each runs read-only, with a time limit and the
/// C locale; a missing tool just means a missing detail. Every run is recorded for the Scan Log.
/// </summary>
internal static class Tools
{
    // sbin isn't on a regular user's PATH on Debian and Ubuntu, but iw and friends live there.
    private static readonly string[] ExtraDirectories = ["/usr/sbin", "/sbin", "/usr/local/sbin"];
    private static readonly ConcurrentDictionary<string, string?> Found = new();

    public static event Action<string>? Ran;

    /// <summary>Full path of a tool, or null when it isn't installed.</summary>
    public static string? Find(string name) => Found.GetOrAdd(name, static n =>
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(ExtraDirectories))
        {
            var candidate = Path.Combine(directory, n);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    });

    public static bool Has(string name) => Find(name) is not null;

    /// <summary>Runs a tool and returns what it printed, or null when it is missing, fails, or runs too long.</summary>
    public static string? Run(string name, IEnumerable<string> arguments, int timeoutMs = 4000, bool acceptFailure = false)
    {
        var path = Find(name);
        if (path is null) return null;
        var args = arguments.ToList();
        var start = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";
        // Tools that page or prompt must never wait for a terminal.
        start.Environment["PAGER"] = "cat";
        start.Environment["SYSTEMD_PAGER"] = "";
        var watch = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Ran?.Invoke($"{name} {string.Join(' ', args)}: stopped after {timeoutMs / 1000.0:0.#} s");
                return null;
            }
            Task.WaitAll([output, errors], 1000);
            Ran?.Invoke($"{name} {string.Join(' ', args)}: exit {process.ExitCode} in {watch.ElapsedMilliseconds} ms");
            return process.ExitCode == 0 || acceptFailure ? output.Result : null;
        }
        catch (Exception ex)
        {
            Ran?.Invoke($"{name} {string.Join(' ', args)}: {ex.Message}");
            return null;
        }
    }

    public static string? Run(string name, params string[] arguments) => Run(name, (IEnumerable<string>)arguments);

    /// <summary>Runs a tool and returns its exit code with both outputs; -1 when it is missing or runs too long.</summary>
    public static (int Code, string Output, string Error) RunFull(string name, IEnumerable<string> arguments, int timeoutMs)
    {
        var path = Find(name);
        if (path is null) return (-1, "", $"{name} isn't installed");
        var args = arguments.ToList();
        var start = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        try
        {
            using var process = Process.Start(start);
            if (process is null) return (-1, "", "could not start");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Ran?.Invoke($"{name} {string.Join(' ', args)}: stopped after {timeoutMs / 1000.0:0.#} s");
                return (-1, "", "it took too long");
            }
            Task.WaitAll([output, errors], 1000);
            Ran?.Invoke($"{name} {string.Join(' ', args)}: exit {process.ExitCode}");
            return (process.ExitCode, output.Result, errors.Result);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
