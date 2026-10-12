using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BoardScout.Models;
using BoardScout.Platform;
using BoardScout.Views;

namespace BoardScout.Live;

/// <summary>
/// Samples the kernel's counters (/proc/stat, /proc/meminfo, /proc/net/dev, /proc/diskstats, hwmon) and turns
/// the change since the last sample into rates: what the header tiles and the Connections map's flowing dots show.
/// </summary>
internal sealed class Telemetry
{
    private Dictionary<string, CpuTimes>? _cpu;
    private Dictionary<string, NetCounters>? _net;
    private Dictionary<string, DiskCounters>? _disk;
    private long _at;
    private readonly Dictionary<string, (double Dropped, long At)> _drops = [];

    public (HeaderTelemetry Header, ConnectionFlow Flow) Sample(HardwareCapture? capture)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _at == 0 ? 0 : Stopwatch.GetElapsedTime(_at, now).TotalSeconds;
        _at = now;

        var cpu = Cpu.ReadTimes();
        var usage = _cpu is not null && _cpu.TryGetValue("cpu", out var before) && cpu.TryGetValue("cpu", out var after) ? Cpu.Usage(before, after) : 0;
        _cpu = cpu;

        var (total, available, _, _) = Cpu.ReadMemory();
        var chips = Sensors.Read();
        var flow = new ConnectionFlow { CpuPercent = Math.Round(usage, 1) };
        if (Sensors.Cpu(chips) is { } cpuTemp) flow.Temperatures["cpu"] = Math.Round(cpuTemp, 1);
        if (Sensors.Gpu(chips) is { } gpuTemp) flow.Temperatures["gpu"] = Math.Round(gpuTemp, 1);
        if (Sensors.Chipset(chips) is { } chipsetTemp) flow.Temperatures["chipset"] = Math.Round(chipsetTemp, 1);

        // Network: per interface, plus the total over physical adapters for the header tile.
        var net = NetLinks.ReadCounters();
        double netIn = 0, netOut = 0;
        var physical = capture?.Links.Where(l => l.Physical).Select(l => l.Name).ToHashSet() ?? [];
        foreach (var (name, counters) in net)
        {
            if (name == "lo") continue;
            double rin = 0, rout = 0, pin = 0, pout = 0, dropRate = 0;
            if (seconds > 0 && _net is not null && _net.TryGetValue(name, out var old))
            {
                rin = Math.Max(0, counters.BytesIn - old.BytesIn) / seconds;
                rout = Math.Max(0, counters.BytesOut - old.BytesOut) / seconds;
                pin = Math.Max(0, counters.PacketsIn - old.PacketsIn) / seconds;
                pout = Math.Max(0, counters.PacketsOut - old.PacketsOut) / seconds;
                var dropped = counters.DroppedIn + counters.DroppedOut + counters.ErrorsIn + counters.ErrorsOut;
                var droppedBefore = old.DroppedIn + old.DroppedOut + old.ErrorsIn + old.ErrorsOut;
                dropRate = Math.Max(0, dropped - droppedBefore) / seconds;
            }
            if (physical.Contains(name)) { netIn += rin; netOut += rout; }
            flow.Network[Format.NetKey(name)] =
            [
                Math.Round(rin), Math.Round(rout), Math.Round(pin), Math.Round(pout), Math.Round(dropRate, 1),
                counters.BytesIn, counters.BytesOut, counters.PacketsIn, counters.PacketsOut,
                counters.DroppedIn + counters.DroppedOut, counters.ErrorsIn + counters.ErrorsOut
            ];
        }
        _net = net;

        // Disks: [read B/s, written B/s, reads/s, writes/s, requests in flight, % of the interval busy].
        var disks = Block.ReadCounters();
        if (capture is not null)
        {
            foreach (var disk in capture.Disks.Where(d => !d.Optical))
            {
                if (!disks.TryGetValue(disk.Name, out var counters)) continue;
                double read = 0, write = 0, reads = 0, writes = 0, busy = 0;
                if (seconds > 0 && _disk is not null && _disk.TryGetValue(disk.Name, out var old))
                {
                    read = Math.Max(0, counters.ReadSectors - old.ReadSectors) * 512 / seconds;
                    write = Math.Max(0, counters.WriteSectors - old.WriteSectors) * 512 / seconds;
                    reads = Math.Max(0, counters.ReadOps - old.ReadOps) / seconds;
                    writes = Math.Max(0, counters.WriteOps - old.WriteOps) / seconds;
                    busy = Math.Clamp(Math.Max(0, counters.BusyMs - old.BusyMs) / (seconds * 10), 0, 100);
                }
                flow.Disks[disk.Index.ToString(CultureInfo.InvariantCulture)] =
                    [Math.Round(read), Math.Round(write), Math.Round(reads), Math.Round(writes), counters.InFlight, Math.Round(busy, 1)];
            }
        }
        _disk = disks;

        var header = new HeaderTelemetry(
            Math.Round(usage, 1),
            Math.Round((total - available) / 1_073_741_824d, 2),
            Math.Round(total / 1_073_741_824d, 2),
            flow.Temperatures.TryGetValue("cpu", out var c) ? c : null,
            flow.Temperatures.TryGetValue("gpu", out var g) ? g : null,
            flow.Temperatures.TryGetValue("chipset", out var h) ? h : null,
            Sensors.Fans(chips).Select(f => new HeaderFan(f.Name, f.Rpm)).ToList(),
            Math.Round(netIn),
            Math.Round(netOut),
            Sensors.GpuWatts(chips) is { } watts ? Math.Round(watts, 1) : null,
            DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        return (header, flow);
    }

    public static string FlowJson(ConnectionFlow flow) => JsonSerializer.Serialize(flow, Json.Options);

    public static string HeaderJson(HeaderTelemetry header) =>
        $"{{\"type\":\"telemetry\",\"data\":{JsonSerializer.Serialize(header, Json.Options)}}}";
}
