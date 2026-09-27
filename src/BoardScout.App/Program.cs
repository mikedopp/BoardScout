using BoardScout.Services;
using BoardScout.UI;

namespace BoardScout;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--system-json", StringComparer.OrdinalIgnoreCase))
            return PrintSystemJsonAsync().GetAwaiter().GetResult();

        if (args.Contains("--connections-json", StringComparer.OrdinalIgnoreCase))
            return PrintConnectionsJsonAsync(args.Contains("--privacy", StringComparer.OrdinalIgnoreCase),
                args.Contains("--sweep", StringComparer.OrdinalIgnoreCase)).GetAwaiter().GetResult();

        if (args.Contains("--plan-json", StringComparer.OrdinalIgnoreCase))
            return PrintPlanJsonAsync(args.Contains("--privacy", StringComparer.OrdinalIgnoreCase),
                args.Contains("--profile", StringComparer.OrdinalIgnoreCase)).GetAwaiter().GetResult();

        if (args.Contains("--scan", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--check-drivers", StringComparer.OrdinalIgnoreCase))
        {
            return RunHeadlessAsync(args).GetAwaiter().GetResult();
        }

        // A window that vanishes should leave evidence behind.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            var log = WriteCrashLog(e.Exception);
            MessageBox.Show(
                $"BoardScout hit an unexpected error and kept running.\n\n{e.Exception.Message}\n\nDetails: {log}",
                "BoardScout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);

        Application.Run(new MainForm());
        return 0;
    }

    private static async Task<int> RunHeadlessAsync(string[] args)
    {
        try
        {
            var service = new DriverScoutService();
            service.OutputReceived += (_, line) => Console.WriteLine(line);

            var scanPath = service.GetLatestScanPath();
            if (args.Contains("--scan", StringComparer.OrdinalIgnoreCase) || scanPath is null)
            {
                scanPath = await service.ScanAsync(CancellationToken.None);
            }

            var snapshot = await service.LoadScanAsync(scanPath, CancellationToken.None);
            Console.WriteLine($"BOARD={snapshot.SystemInfo.Baseboard.Manufacturer} {snapshot.SystemInfo.Baseboard.Product}");
            Console.WriteLine($"COMPONENTS={snapshot.Components.Count}");
            Console.WriteLine($"VOLUMES={snapshot.Volumes.Count}");

            if (args.Contains("--check-drivers", StringComparer.OrdinalIgnoreCase))
            {
                var reportPath = await service.CheckDriversAsync(scanPath, CancellationToken.None);
                var report = await service.LoadReportAsync(reportPath, CancellationToken.None);
                Console.WriteLine($"DRIVER_RESULTS={report.Results.Count}");
                Console.WriteLine($"UPDATES={report.Results.Count(r => r.Status == "update-available")}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // Prints what the System tab receives, using the latest cached scan when there is one.
    private static async Task<int> PrintSystemJsonAsync()
    {
        try
        {
            var service = new DriverScoutService();
            var scanPath = service.GetLatestScanPath();
            var scan = scanPath is null ? null : await service.LoadScanAsync(scanPath, CancellationToken.None);
            Console.WriteLine(await SystemInfoService.GatherJsonAsync(scan));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // Prints what the Connections tab receives. Contains local addresses and MACs unless --privacy is given;
    // --sweep pings every address on the local network first, like "Find more devices".
    private static async Task<int> PrintPlanJsonAsync(bool privacy, bool profile)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Privacy.Learn(null);
            var json = await PlanService.GatherJsonAsync(privacy, profile);
            Console.WriteLine(System.Text.Json.Nodes.JsonNode.Parse(json)?.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<int> PrintConnectionsJsonAsync(bool privacy, bool sweep)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Privacy.Learn(null);
            var json = await ConnectionsService.GatherJsonAsync(privacy, sweep);
            Console.WriteLine(System.Text.Json.Nodes.JsonNode.Parse(json)?.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static string WriteCrashLog(Exception? exception)
    {
        try
        {
            var path = Path.Combine(DriverScoutService.ResolveWritableDataRoot(), "crash.log");
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}] BoardScout {VersionButton.AppVersion} on .NET {Environment.Version}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}");
            return path;
        }
        catch
        {
            return "(crash log could not be written)";
        }
    }
}
