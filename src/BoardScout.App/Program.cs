using System.Diagnostics;
using BoardScout.Services;
using BoardScout.UI;

namespace BoardScout;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // --data DIR comes first: the data folder (settings, scans, the log) is read from BOARDSCOUT_DATA.
        if (Value(args, "--data") is { } data) Environment.SetEnvironmentVariable("BOARDSCOUT_DATA", Path.GetFullPath(data));

        // Without a window: started over SSH by another BoardScout (--agent), or serving the browser dashboard (--serve).
        if (Has(args, "--agent") || Has(args, "--serve"))
            return ServeAsync(args).GetAwaiter().GetResult();
        if (Has(args, "--summary-json"))
            return PrintSummaryAsync(Has(args, "--privacy")).GetAwaiter().GetResult();
        if (Has(args, "--machines") || Value(args, "--check") is not null || Value(args, "--connect") is not null)
            return RemoteAsync(args).GetAwaiter().GetResult();

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

        // --snapshot DIR: QA renders of the window, drawn off-screen (never a capture of the desktop), then exit.
        Application.Run(new MainForm(Value(args, "--snapshot") is { } shots ? Path.GetFullPath(shots) : null));
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

    private static bool Has(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    // BoardScout.exe is a Windows program, not a console one: typed at a prompt, its output only shows after it
    // borrows that prompt's console. Redirected output (a pipe, a file, an SSH session) needs nothing.
    private static void UseParentConsole()
    {
        if (!Console.IsOutputRedirected) AttachConsole(-1);
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // The browser dashboard on this PC: for another BoardScout over SSH (--agent: this PC only, any free port,
    // stops when it disconnects), or on its own (--serve [--listen ADDRESS] [--port N] [--no-browser]).
    private static async Task<int> ServeAsync(string[] args)
    {
        var agent = Has(args, "--agent");
        if (!agent) UseParentConsole();
        try
        {
            var dataRoot = DriverScoutService.ResolveWritableDataRoot();
            AppSettings.Load(dataRoot);
            Privacy.Learn(null);
            if (agent && Has(args, "--privacy")) AppSettings.Current.PrivacyMode = true;
            int? port = int.TryParse(Value(args, "--port"), out var p) && p is >= 1024 and <= 65535 ? p : null;
            var listen = Value(args, "--listen");
            if (!agent && Web.WebServer.RunningInstance(dataRoot) is { } running)
            {
                Console.WriteLine($"BoardScout is already serving: {running.Url}");
                if (!Has(args, "--no-browser")) OpenBrowser(running.Url);
                return 0;
            }
            return await Web.WebServer.RunAsync(new Web.ServeOptions
            {
                Agent = agent,
                Label = Value(args, "--label"),
                WatchStdin = Has(args, "--watch-stdin"),
                Listen = agent ? null : listen,
                Port = port,
                DataRoot = dataRoot,
                Started = agent || listen is not null || Has(args, "--no-browser") ? null : OpenBrowser
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine($"Details: {WriteCrashLog(ex)}");
            return 1;
        }
    }

    // One line about this PC's health, for a BoardScout elsewhere that checks every machine at once.
    private static async Task<int> PrintSummaryAsync(bool privacy)
    {
        try
        {
            AppSettings.Load(DriverScoutService.ResolveWritableDataRoot());
            Privacy.Learn(null);
            var summary = await Views.WindowsOverview.SummaryAsync(privacy || AppSettings.Current.PrivacyMode);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary, Remote.MachineSummaries.Ascii));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // --machines, --check NAME|all, --connect NAME: the Machines page from a terminal.
    private static async Task<int> RemoteAsync(string[] args)
    {
        UseParentConsole();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var dataRoot = DriverScoutService.ResolveWritableDataRoot();
        AppSettings.Load(dataRoot);
        Privacy.Learn(null);
        var command = Has(args, "--machines") ? "machines" : Value(args, "--check") is not null ? "check" : "connect";
        return await Remote.RemoteCli.RunAsync(command, Value(args, "--" + command), dataRoot, Has(args, "--privacy") || AppSettings.Current.PrivacyMode,
            Has(args, "--trust"), Value(args, "--key"), Has(args, "--no-browser") ? null : OpenBrowser);
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
