using BoardScout.Services;
using BoardScout.UI;

namespace BoardScout;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--scan", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--check-drivers", StringComparer.OrdinalIgnoreCase))
        {
            return RunHeadlessAsync(args).GetAwaiter().GetResult();
        }

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
}
