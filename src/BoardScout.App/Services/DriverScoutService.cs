using System.Diagnostics;
using System.Text.Json;
using BoardScout.Models;

namespace BoardScout.Services;

public sealed class DriverScoutService
{
    private readonly string _toolRoot;
    private readonly string _dataRoot;

    public event EventHandler<string>? OutputReceived;

    public DriverScoutService()
    {
        _toolRoot = Path.Combine(AppContext.BaseDirectory, "DriverScout");
        _dataRoot = ResolveWritableDataRoot();
        Directory.CreateDirectory(ScanDirectory);
        Directory.CreateDirectory(ReportDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }

    public string DataRoot => _dataRoot;
    public string ScanDirectory => Path.Combine(_dataRoot, "Scans");
    public string ReportDirectory => Path.Combine(_dataRoot, "Reports");
    public string CacheDirectory => Path.Combine(_dataRoot, "Cache");

    public string? GetLatestScanPath() => GetLatestJson(ScanDirectory, "scan_*.json");
    public string? GetLatestReportPath() => GetLatestJson(ReportDirectory, "report_*.json");

    public async Task<string> ScanAsync(CancellationToken cancellationToken)
    {
        EnsureToolExists("Invoke-HardwareScan.ps1");
        var started = DateTime.UtcNow;
        await RunPowerShellAsync(
            Path.Combine(_toolRoot, "Invoke-HardwareScan.ps1"),
            ["-OutDir", ScanDirectory, "-Quiet"],
            cancellationToken);

        return GetLatestAfter(ScanDirectory, "scan_*.json", started)
            ?? throw new InvalidOperationException("DriverScout completed without producing a scan.");
    }

    public async Task<string> CheckDriversAsync(string scanPath, CancellationToken cancellationToken)
    {
        EnsureToolExists("Invoke-UpdateCheck.ps1");
        if (!File.Exists(scanPath))
            throw new FileNotFoundException("The scan selected for the driver check no longer exists.", scanPath);

        var started = DateTime.UtcNow;
        await RunPowerShellAsync(
            Path.Combine(_toolRoot, "Invoke-UpdateCheck.ps1"),
            ["-ScanFile", scanPath, "-OutDir", ReportDirectory, "-CacheDir", CacheDirectory],
            cancellationToken);

        return GetLatestAfter(ReportDirectory, "report_*.json", started)
            ?? throw new InvalidOperationException("DriverScout completed without producing a driver report.");
    }

    public async Task<ScanManifest> LoadScanAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var scan = await JsonSerializer.DeserializeAsync<ScanManifest>(stream, JsonDefaults.Options, cancellationToken);
        if (scan is null || scan.SystemInfo.Baseboard is null)
            throw new InvalidDataException("The selected file is not a compatible BoardScout scan.");
        return scan;
    }

    public async Task<DriverReport> LoadReportAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<DriverReport>(stream, JsonDefaults.Options, cancellationToken)
            ?? throw new InvalidDataException("The selected file is not a compatible DriverScout report.");
    }

    public void OpenDataFolder()
    {
        Directory.CreateDirectory(_dataRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", _dataRoot) { UseShellExecute = true });
    }

    private async Task RunPowerShellAsync(
        string scriptPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _toolRoot
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) OutputReceived?.Invoke(this, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) OutputReceived?.Invoke(this, "ERROR: " + e.Data); };

        if (!process.Start()) throw new InvalidOperationException("Windows PowerShell could not be started.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"DriverScout exited with code {process.ExitCode}. See the Scan Log tab for details.");
    }

    private void EnsureToolExists(string file)
    {
        var path = Path.Combine(_toolRoot, file);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "The bundled DriverScout engine is missing. Keep the DriverScout folder beside BoardScout.exe.",
                path);
    }

    private static string ResolveWritableDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("BOARDSCOUT_DATA");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return Path.GetFullPath(configured);
        }

        var portable = Path.Combine(AppContext.BaseDirectory, "Data");
        if (CanWrite(portable)) return portable;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BoardScout");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static string? GetLatestJson(string directory, string pattern) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;

    private static string? GetLatestAfter(string directory, string pattern, DateTime started) =>
        Directory.EnumerateFiles(directory, pattern)
            .Where(path => File.GetLastWriteTimeUtc(path) >= started.AddSeconds(-2))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
}
