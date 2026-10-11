using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using BoardScout.Platform;

namespace BoardScout.Services;

// Linux half of network discovery: the Wi-Fi scan list (iw, or NetworkManager), NetworkManager's
// connection names, and the kernel's neighbor cache.
internal static partial class NetworkDiscovery
{
    // NetworkManager asks the card for a fresh scan for the logged-in user; without it a scan needs root.
    private static bool StartWifiScan() =>
        Tools.Has("nmcli") && Tools.Run("nmcli", "device", "wifi", "rescan") is not null;

    // What the Wi-Fi card heard on its last scan (no scan is started here). iw also decodes the WPS
    // device info routers put in their beacons; NetworkManager's list has signal and band only.
    private static List<Beacon> ReadBeacons()
    {
        var result = new List<Beacon>();
        foreach (var link in NetLinks.Read().Where(l => l.Wireless))
        {
            if (Tools.Run("iw", "dev", link.Name, "scan", "dump") is { } dump)
            {
                result.AddRange(ParseIwScan(dump));
                continue;
            }
            var list = Tools.Run("nmcli", "-t", "-f", "BSSID,SSID,FREQ,SIGNAL", "device", "wifi", "list", "ifname", link.Name, "--rescan", "no");
            foreach (var line in (list ?? "").Split('\n'))
            {
                var f = NetLinks.SplitNmcli(line);
                if (f.Count < 4 || ParseMac(f[0]) is not { } bssid) continue;
                var mhz = double.TryParse(new string(f[2].TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
                var percent = int.TryParse(f[3], out var p) ? p : 0;
                result.Add(new Beacon(bssid, f[1].Length == 0 ? null : f[1], percent / 2 - 100, mhz, null, null, null));
            }
        }
        return result;
    }

    private static List<Beacon> ParseIwScan(string text)
    {
        var result = new List<Beacon>();
        foreach (var block in Regex.Split(text, @"(?m)^(?=BSS )").Where(b => b.StartsWith("BSS ", StringComparison.Ordinal)))
        {
            if (ParseMac(block.Substring(4, Math.Min(17, block.Length - 4))) is not { } bssid) continue;
            string? Field(string pattern)
            {
                var match = Regex.Match(block, pattern, RegexOptions.Multiline);
                return match.Success ? match.Groups[1].Value.Trim() : null;
            }
            var mhz = double.TryParse(Field(@"^\s*freq:\s*([\d.]+)"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
            var dbm = double.TryParse(Field(@"^\s*signal:\s*(-?[\d.]+)"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? (int)Math.Round(s) : -100;
            var ssid = Field(@"^\s*SSID:\s*(.*)$");
            var modelName = Field(@"\*\s*Model:\s*(.*)$");
            var modelNumber = Field(@"\*\s*Model Number:\s*(.*)$");
            var model = modelName ?? modelNumber;
            if (modelName is not null && modelNumber is not null && !modelName.Contains(modelNumber, StringComparison.OrdinalIgnoreCase))
                model = $"{modelName} {modelNumber}";
            result.Add(new Beacon(bssid, string.IsNullOrEmpty(ssid) ? null : ssid, dbm, mhz,
                Field(@"\*\s*Manufacturer:\s*(.*)$"), model, Field(@"\*\s*Device name:\s*(.*)$")));
        }
        return result;
    }

    // NetworkManager's connection names ("Home Wi-Fi", "Wired connection 1"); Linux has no Public/Private.
    private static Dictionary<Guid, NetworkProfile> ReadProfiles()
    {
        var result = new Dictionary<Guid, NetworkProfile>();
        var text = Tools.Run("nmcli", "-t", "-f", "NAME,DEVICE", "connection", "show", "--active");
        foreach (var line in (text ?? "").Split('\n'))
        {
            var f = NetLinks.SplitNmcli(line);
            if (f.Count < 2 || f[1].Length == 0 || f[1] == "lo") continue;
            var id = NetworkProbe.InterfaceGuid(f[1]);
            result[id] = new NetworkProfile(id, f[0], "NetworkManager", null);
        }
        return result;
    }

    private static List<(IPAddress Address, string? Mac, AdapterFacts Adapter)> ReadNeighbors(List<AdapterFacts> adapters)
    {
        var result = new List<(IPAddress, string?, AdapterFacts)>();
        foreach (var (address, mac, device) in NetLinks.ReadArp())
        {
            var adapter = adapters.FirstOrDefault(a => a.Name == device && a.IPv4.Any(c => InSubnet(address, c)));
            if (adapter is not null) result.Add((address, mac, adapter));
        }
        return result;
    }
}
