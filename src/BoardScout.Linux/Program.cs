using System.Text.Json;
using System.Text.Json.Nodes;
using BoardScout.Live;
using BoardScout.Models;
using BoardScout.Platform;
using BoardScout.Remote;
using BoardScout.Services;
using BoardScout.Views;
using BoardScout.Web;

// The Linux edition: only the parsers it shares with its tests run anywhere else.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace BoardScout;

/// <summary>Command-line options for the Linux edition.</summary>
internal sealed record LaunchOptions
{
    public bool Help { get; init; }
    public bool Version { get; init; }
    public string? Listen { get; init; }
    public int? Port { get; init; }
    public bool OpenBrowser { get; init; } = true;
    public bool StopWhenClosed { get; init; }
    public string? Command { get; init; }
    public bool Privacy { get; init; }
    public bool Sweep { get; init; }
    public bool Profile { get; init; }
    public string? Directory { get; init; }
    public string? Error { get; init; }

    /// <summary>Started over SSH by another BoardScout (see ServeOptions.Agent).</summary>
    public bool Agent { get; init; }
    public string? Label { get; init; }
    public bool WatchStdin { get; init; }

    /// <summary>A data folder for this run (sets $BOARDSCOUT_DATA).</summary>
    public string? Data { get; init; }

    /// <summary>The machine for --check and --connect.</summary>
    public string? Machine { get; init; }
    public bool Trust { get; init; }
    public string? Key { get; init; }

    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var options = new LaunchOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            options = arg switch
            {
                "-h" or "--help" => options with { Help = true },
                "-V" or "--version" => options with { Version = true },
                "--listen" => Next() is { } value && ParseListen(value) is var (host, port) && host is not null
                    ? options with { Listen = host, Port = port ?? options.Port, OpenBrowser = false }
                    : options with { Error = "--listen needs an address, like --listen 0.0.0.0 or --listen 192.168.1.20:8797" },
                "--port" => int.TryParse(Next(), out var p) && p is >= 1024 and <= 65535 ? options with { Port = p } : options with { Error = "--port needs a number from 1024 to 65535" },
                "--no-browser" => options with { OpenBrowser = false },
                "--stop-when-closed" => options with { StopWhenClosed = true },
                "--privacy" => options with { Privacy = true },
                "--sweep" => options with { Sweep = true },
                "--profile" => options with { Profile = true },
                "--connections-json" or "--plan-json" or "--system-json" or "--scan-json" or "--summary-json" or "--diagnostics" or "--install" or "--uninstall" or "--machines" => options with { Command = arg[2..] },
                "--selftest" => Next() is { } directory ? options with { Command = "selftest", Directory = directory } : options with { Error = "--selftest needs a folder to write into" },
                "--check" or "--connect" => Next() is { } machine && !machine.StartsWith('-') ? options with { Command = arg[2..], Machine = machine } : options with { Error = $"{arg} needs a machine: a name from --machines, a Host from ~/.ssh/config, or user@host" },
                "--trust" => options with { Trust = true },
                "--key" => Next() is { } key ? options with { Key = key } : options with { Error = "--key needs a private key file" },
                "--agent" => options with { Agent = true, OpenBrowser = false },
                "--label" => Next() is { } label ? options with { Label = label } : options with { Error = "--label needs a name" },
                "--watch-stdin" => options with { WatchStdin = true },
                "--data" => Next() is { } data ? options with { Data = data } : options with { Error = "--data needs a folder" },
                _ => options with { Error = $"Unknown option: {arg}" }
            };
            if (options.Error is not null) break;
        }
        return options;
    }

    private static (string? Host, int? Port) ParseListen(string value)
    {
        value = value.Trim();
        if (value is "local" or "localhost") return ("127.0.0.1", null);
        int? port = null;
        var host = value;
        var colon = value.LastIndexOf(':');
        if (colon > 0 && !value.EndsWith(']') && value.Count(c => c == ':') == 1 || value.StartsWith('[') && value.Contains("]:"))
        {
            if (!int.TryParse(value[(colon + 1)..], out var p)) return (null, null);
            port = p;
            host = value[..colon];
        }
        return System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _) ? (host, port) : (null, null);
    }
}

internal static class Program
{
    private const string Usage = """
        BoardScout for Linux: your motherboard, connections, drives, and system health, in the browser.

        Usage: boardscout [options]

          (no options)          Serve the dashboard on this computer and open it in your browser
          --port N              Use port N (default 8797, or the next free one)
          --listen ADDRESS      Serve on your network too (e.g. 0.0.0.0 or 192.168.1.20[:port]);
                                anyone with the printed link can open it
          --no-browser          Don't open a browser (print the link only)
          --stop-when-closed    Stop when the last dashboard tab closes (the app-menu entry uses this)

          --install             Add BoardScout to your app menu (~/.local; no root needed)
          --uninstall           Remove the app-menu entry and ~/.local/bin/boardscout

          --connections-json    Print the Connections map (add --privacy to mask, --sweep to ping the network first)
          --plan-json           Print the optimization plan (add --profile to count interrupts for 10 s)
          --system-json         Print the System tab's data
          --scan-json           Print the hardware scan (what Export saves)
          --diagnostics         Print what an issue report needs (always masked)
          --selftest DIR        Write all of the above into DIR, for checking a machine without a browser
          --summary-json        Print this machine's one-line health summary

        Other machines, over SSH (your keys and ~/.ssh/config; BoardScout never asks for a password):
          --machines            List the machines on the Machines page
          --check NAME|all      Print a machine's health summary (NAME: from --machines, a Host, or user@host)
          --connect NAME        Open a machine's dashboard through an SSH tunnel; Ctrl+C disconnects
          --trust               Trust a machine's host key the first time (its fingerprint is printed)
          --key FILE            Sign in with this private key

          --data DIR            Keep settings and data in DIR for this run
          -V, --version         Print the version
          -h, --help            Show this help

        Settings, the log, and the running dashboard's address live in ~/.local/share/boardscout
        (or $BOARDSCOUT_DATA). Nothing here needs root; nothing is installed system-wide. With sudo, the
        power table adds the processor's energy counters and NVMe drives' power tables. On a machine
        it opens over SSH, BoardScout keeps its copy in ~/.cache/boardscout.
        """;

    private static async Task<int> Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            Console.Error.WriteLine("Run boardscout --help for the options.");
            return 2;
        }
        if (options.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (options.Version)
        {
            Console.WriteLine($"BoardScout {AppInfo.Version}{(AppInfo.Commit is { } commit ? $" ({commit})" : "")}");
            return 0;
        }
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("This is the Linux edition of BoardScout. On Windows, run BoardScout.exe.");
            return 2;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (options.Data is not null) Environment.SetEnvironmentVariable("BOARDSCOUT_DATA", Path.GetFullPath(options.Data));
        AppSettings.Load(DataFolder.Root);
        Privacy.Learn(null);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);
        var privacy = options.Privacy || AppSettings.Current.PrivacyMode;

        try
        {
            switch (options.Command)
            {
                case "install":
                    return Desktop.Install();
                case "uninstall":
                    return Desktop.Uninstall();
                case "connections-json":
                    Console.WriteLine(Pretty(await ConnectionsJsonAsync(privacy, options.Sweep)));
                    return 0;
                case "plan-json":
                    Console.WriteLine(Pretty(await PlanJsonAsync(privacy, options.Profile)));
                    return 0;
                case "system-json":
                    Console.WriteLine(Pretty(await LinuxSystemInfo.GatherJsonAsync(await Task.Run(HardwareCapture.Read))));
                    return 0;
                case "scan-json":
                    Console.WriteLine(LinuxOverview.ScanJson(await Task.Run(HardwareCapture.Read), privacy));
                    return 0;
                case "diagnostics":
                    await Hub.Current.LoadAsync(rediscover: false);
                    Console.WriteLine(Hub.Current.Diagnostics());
                    return 0;
                case "selftest":
                    return await SelfTestAsync(options.Directory!, privacy);
                case "summary-json":
                    Console.WriteLine(JsonSerializer.Serialize(await SummaryAsync(privacy), MachineSummaries.Ascii));
                    return 0;
                case "machines" or "check" or "connect":
                    return await RemoteCli.RunAsync(options.Command, options.Machine, DataFolder.Root, privacy, options.Trust, options.Key,
                        options.OpenBrowser && AppSettings.Current.OpenBrowser ? Desktop.OpenBrowser : null);
            }

            Tools.Ran += line => Hub.Current.Log("RAN: " + line);
            if (options.Agent)
            {
                // Started over SSH by another BoardScout: serve only this machine, on any free port, until it disconnects.
                if (options.Privacy) AppSettings.Current.PrivacyMode = true;
                return await WebServer.RunAsync(new ServeOptions
                {
                    Agent = true,
                    Port = options.Port,
                    Label = options.Label,
                    WatchStdin = options.WatchStdin,
                    DataRoot = DataFolder.Root
                });
            }
            if (WebServer.RunningInstance(DataFolder.Root) is { } running)
            {
                Console.WriteLine($"BoardScout is already running: {running.Url}");
                if (options.OpenBrowser) Desktop.OpenBrowser(running.Url);
                return 0;
            }
            return await WebServer.RunAsync(new ServeOptions
            {
                Listen = options.Listen,
                Port = options.Port,
                DefaultPort = AppSettings.Current.Port,
                StopWhenClosed = options.StopWhenClosed,
                DataRoot = DataFolder.Root,
                Started = options.OpenBrowser && AppSettings.Current.OpenBrowser ? Desktop.OpenBrowser : null
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine($"Details: {WriteCrashLog(ex)}");
            return 1;
        }
    }

    private static async Task<string> ConnectionsJsonAsync(bool privacy, bool sweep)
    {
        var capture = await Task.Run(HardwareCapture.Read);
        var discovery = await NetworkDiscovery.RunAsync(capture.Network, CancellationToken.None, sweep);
        return LinuxConnections.ToJson(LinuxConnections.Build(capture, privacy, discovery).Snapshot);
    }

    private static async Task<string> PlanJsonAsync(bool privacy, bool profile)
    {
        var capture = await Task.Run(HardwareCapture.Read);
        var map = LinuxConnections.Build(capture, privacy, null);
        var cores = LinuxPlan.SampleCoresAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var power = LinuxPlan.SamplePowerAsync(capture, TimeSpan.FromSeconds(1), CancellationToken.None);
        var irq = profile ? await LinuxPlan.ProfileAsync(capture, TimeSpan.FromSeconds(10), privacy, CancellationToken.None) : null;
        return JsonSerializer.Serialize(LinuxPlan.Build(capture, map, await cores, irq, privacy, await power), Json.Options);
    }

    // Everything the dashboard shows, written as files: for checking a machine over SSH without a browser.
    private static async Task<int> SelfTestAsync(string directory, bool privacy)
    {
        Directory.CreateDirectory(directory);
        var capture = await Task.Run(HardwareCapture.Read);
        var discovery = await NetworkDiscovery.RunAsync(capture.Network, CancellationToken.None);
        var map = LinuxConnections.Build(capture, privacy, discovery);
        var power = LinuxPlan.SamplePowerAsync(capture, TimeSpan.FromSeconds(1), CancellationToken.None);
        var cores = await LinuxPlan.SampleCoresAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var irq = await LinuxPlan.ProfileAsync(capture, TimeSpan.FromSeconds(3), privacy, CancellationToken.None);
        var plan = LinuxPlan.Build(capture, map, cores, irq, privacy, await power);
        var telemetry = new Telemetry();
        telemetry.Sample(capture, privacy);
        await Task.Delay(1000);
        var (header, flow) = telemetry.Sample(capture, privacy);
        var files = new Dictionary<string, string>
        {
            ["connections.json"] = Pretty(LinuxConnections.ToJson(map.Snapshot)),
            ["plan.json"] = Pretty(JsonSerializer.Serialize(plan, Json.Options)),
            ["system.json"] = Pretty(await LinuxSystemInfo.GatherJsonAsync(capture)),
            ["scan.json"] = LinuxOverview.ScanJson(capture, privacy),
            ["overview.json"] = Pretty(JsonSerializer.Serialize(LinuxOverview.Build(capture, map, plan, privacy), Json.Options)),
            ["telemetry.json"] = Pretty(JsonSerializer.Serialize(new { header, flow }, Json.Options))
        };
        foreach (var (name, text) in files) await File.WriteAllTextAsync(Path.Combine(directory, name), text);
        await Hub.Current.LoadAsync(rediscover: false);
        await File.WriteAllTextAsync(Path.Combine(directory, "diagnostics.txt"), Hub.Current.Diagnostics());
        var nodes = LinuxConnections.Walk(map.Snapshot.Root).ToList();
        Console.WriteLine($"Wrote {files.Count + 1} files to {Path.GetFullPath(directory)}");
        Console.WriteLine($"MAP={nodes.Count} cards, {nodes.Count(n => n.Warning is not null)} warnings, {nodes.Count(n => n.Link?.Degraded == true)} degraded links");
        Console.WriteLine($"PLAN={plan.Sections.Count} sections, {plan.Sections.Sum(s => s.Items.Count(i => i.Tone is "improve" or "warn"))} things to improve");
        Console.WriteLine($"READ={capture.CaptureMs} ms");
        return 0;
    }

    // This machine's one-row health summary, for a BoardScout elsewhere that checks every machine at once.
    private static async Task<MachineSummary> SummaryAsync(bool privacy)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var capture = await Task.Run(HardwareCapture.Read);
        var map = LinuxConnections.Build(capture, privacy, null);
        var cores = await LinuxPlan.SampleCoresAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var plan = LinuxPlan.Build(capture, map, cores, null, privacy);
        var system = JsonSerializer.Deserialize<SystemInfoSnapshot>(await LinuxSystemInfo.GatherJsonAsync(capture)) ?? new SystemInfoSnapshot();
        var title = LinuxOverview.Build(capture, map, plan, privacy).Title;
        return MachineSummaries.Build("linux", title, system, map.Snapshot, plan, capture.Volumes.Select(v => (v.MountPoint, v.SizeBytes, v.FreeBytes)),
            Sensors.Cpu(Sensors.Read()), capture.Root, clock.ElapsedMilliseconds, privacy);
    }

    private static string Pretty(string json) => JsonNode.Parse(json)?.ToJsonString(Json.Indented) ?? json;

    private static string WriteCrashLog(Exception? exception)
    {
        try
        {
            var path = Path.Combine(DataFolder.Root, "crash.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] BoardScout {AppInfo.Version} on .NET {Environment.Version}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            return path;
        }
        catch
        {
            return "(crash log could not be written)";
        }
    }
}
