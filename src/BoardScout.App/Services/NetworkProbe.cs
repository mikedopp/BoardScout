using System.Net;
using System.Net.NetworkInformation;
using BoardScout.Models;

namespace BoardScout.Services;

internal sealed record AdapterFacts(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool Up,
    long SpeedBitsPerSecond,
    string? Mac,
    IReadOnlyList<string> IPv4,
    IReadOnlyList<(string Address, string Scope)> IPv6,
    IReadOnlyList<IPAddress> Gateways,
    IReadOnlyList<IPAddress> DnsServers,
    IReadOnlyList<IPAddress> DhcpServers,
    int Index,
    bool Primary);

internal sealed record RouterFacts(IPAddress Address, string? Mac, string? HostName, long? PingMs);

internal sealed record DnsFacts(IPAddress Address, string? HostName);

internal sealed record WifiFacts(
    Guid InterfaceId,
    string State,
    string? Ssid,
    string? Bssid,
    int? SignalPercent,
    int? RssiDbm,
    int? Channel,
    double? FrequencyMhz,
    string? Phy,
    double? ReceiveMbps,
    double? TransmitMbps,
    string? Security);

internal sealed record NetworkFacts(
    IReadOnlyList<AdapterFacts> Adapters,
    IReadOnlyDictionary<string, RouterFacts> Routers,
    IReadOnlyDictionary<string, DnsFacts> Dns,
    IReadOnlyList<WifiFacts> Wifi,
    bool? InternetAccess);

/// <summary>
/// The network path out of this PC: adapters and their addresses, the router (gateway) with its MAC and
/// name, DNS servers, Wi-Fi association details, and whether the operating system sees Internet access.
/// Everything here stays on the local network. The public IP lookup is separate and only runs when the
/// user asks. Reading adapters, the router, Wi-Fi, and connectivity is platform code
/// (NetworkProbe.Windows.cs, and the Linux edition's NetworkProbe.Linux.cs).
/// </summary>
internal static partial class NetworkProbe
{
    public const string WanLookupUrl = "https://1.1.1.1/cdn-cgi/trace";

    public static NetworkFacts Capture()
    {
        var adapters = ReadAdapters();
        var gateways = adapters.Where(a => a.Up).SelectMany(a => a.Gateways).Distinct().ToList();
        var dnsServers = adapters.Where(a => a.Up).SelectMany(a => a.DnsServers).Distinct().ToList();

        // Reverse lookups and pings run in parallel so a silent router costs one timeout, not several.
        var routerTasks = gateways.ToDictionary(g => g.ToString(), g => Task.Run(() => ReadRouter(g)));
        var dnsTasks = dnsServers.ToDictionary(d => d.ToString(), d => Task.Run(async () => new DnsFacts(d, await ReverseLookupAsync(d))));
        var wifi = ReadWifi();
        var internet = InternetAccess();
        Task.WaitAll([.. routerTasks.Values, .. dnsTasks.Values], TimeSpan.FromSeconds(3));

        return new NetworkFacts(
            adapters,
            routerTasks.Where(t => t.Value.IsCompletedSuccessfully).ToDictionary(t => t.Key, t => t.Value.Result),
            dnsTasks.Where(t => t.Value.IsCompletedSuccessfully).ToDictionary(t => t.Key, t => t.Value.Result),
            wifi,
            internet);
    }

    /// <summary>Asks Cloudflare which public IP this network reaches the Internet from. Only on request.</summary>
    public static async Task<WanLookup> LookupPublicAddressAsync(CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"BoardScout/{AppInfo.Version}");
            var text = await http.GetStringAsync(WanLookupUrl, token);
            var fields = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .GroupBy(parts => parts[0].Trim())
                .ToDictionary(g => g.Key, g => g.First()[1].Trim());
            return fields.TryGetValue("ip", out var ip) && IPAddress.TryParse(ip, out _)
                ? new WanLookup
                {
                    Ok = true,
                    Ip = ip,
                    Location = fields.GetValueOrDefault("loc"),
                    Edge = fields.GetValueOrDefault("colo"),
                    CheckedAt = DateTimeOffset.Now.ToString("HH:mm:ss")
                }
                : new WanLookup { Error = "Cloudflare's answer had no IP address in it." };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new WanLookup { Error = "Cloudflare did not answer within 8 seconds." };
        }
        catch (HttpRequestException ex)
        {
            return new WanLookup { Error = $"Could not reach Cloudflare: {ex.Message}" };
        }
    }

    public static string? Band(double? frequencyMhz, int? channel)
    {
        if (frequencyMhz is { } mhz)
            return mhz switch { < 2500 => "2.4 GHz", < 5900 => "5 GHz", _ => "6 GHz" };
        return channel switch { null => null, <= 14 => "2.4 GHz", _ => null };
    }
}
