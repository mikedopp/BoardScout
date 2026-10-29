using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using BoardScout.Models;
using BoardScout.Services;

namespace BoardScout.Remote;

/// <summary>
/// Builds a machine's one-row summary from what both editions already make: the System view's data, the
/// Connections map, and the optimization plan.
/// </summary>
internal static class MachineSummaries
{
    /// <summary>
    /// ASCII-only JSON (other characters escaped): it travels through Windows PowerShell's console output, which
    /// would mangle anything else.
    /// </summary>
    public static readonly JsonSerializerOptions Ascii = new() { Encoder = JavaScriptEncoder.Default };

    public static MachineSummary Build(string platform, string title, SystemInfoSnapshot system, ConnectionsSnapshot map, OptimizationPlan plan,
        IEnumerable<(string Name, long Size, long Free)> volumes, double? cpuTemp, bool elevated, long gatheredMs, bool privacy)
    {
        var nodes = Walk(map.Root).ToList();
        var fullest = volumes.Where(v => v.Size > 0).Select(v => (v.Name, Percent: Math.Round((v.Size - v.Free) * 100d / v.Size, 1)))
            .OrderByDescending(v => v.Percent).FirstOrDefault();
        var fixes = plan.Sections.SelectMany(s => s.Items).Where(i => i.Tone is "warn" or "improve")
            .OrderBy(i => i.Tone == "warn" ? 0 : 1).Select(i => i.Title).Distinct().ToList();
        return new MachineSummary
        {
            Platform = platform,
            Hostname = privacy ? Privacy.HostPlaceholder : Environment.MachineName,
            Title = privacy ? Privacy.Scrub(title) : title,
            Os = system.Os.Caption,
            OsBuild = system.Os.Build,
            Support = system.Os.SupportStatus,
            SupportTone = system.Os.SupportTone,
            Verdict = system.Os.Verdict,
            Cpu = system.Build.Cpu,
            Memory = system.Build.Memory,
            Uptime = system.Os.Uptime,
            Patches = system.Os.PatchHealth,
            PatchTone = system.Os.PatchTone,
            Checks = (system.Readiness?.Checks ?? []).Where(c => c.State is "warn" or "fail").Select(c => new SummaryCheck(c.Label, c.State, c.Detail)).ToList(),
            Fixes = fixes,
            SlowLinks = nodes.Count(n => n.Link?.Degraded == true),
            Problems = nodes.Count(n => n.Problem),
            Fullest = fullest.Name is null ? null : privacy && fullest.Name.StartsWith("/home/", StringComparison.Ordinal) ? "/home/…" : fullest.Name,
            FullestPercent = fullest.Name is null ? null : fullest.Percent,
            CpuTemp = cpuTemp is { } t ? Math.Round(t) : null,
            Elevated = elevated,
            Version = AppInfo.Version,
            GatheredMs = gatheredMs,
            CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        };
    }

    private static readonly System.Text.RegularExpressions.Regex HomePath = new(
        @"(?<=/home/|/Users/|[A-Za-z]:\\Users\\|[A-Za-z]:/Users/)[^/\\\s""']+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex Address = new(
        @"\b\d{1,3}(\.\d{1,3}){3}\b|[\w.+-]+@[\w-]+(\.[\w-]+)+|\b([0-9A-F]{2}[:-]){5}[0-9A-F]{2}\b", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Text that came from another computer, with its user names, home folders, addresses and e-mails hidden.</summary>
    public static string ScrubRemote(string text) => Address.Replace(HomePath.Replace(Privacy.Scrub(text), "…"), "•••");

    /// <summary>
    /// A summary as the page shows it. The summary is kept as gathered, so privacy mode is applied here, every time
    /// it is sent: turning it on after a machine was checked must hide that machine's name and home folder too.
    /// </summary>
    public static MachineSummary ForDisplay(MachineSummary summary, bool privacy)
    {
        if (!privacy) return summary;
        string Hide(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (summary.Hostname.Length >= 3 && summary.Hostname != Privacy.HostPlaceholder)
                text = text.Replace(summary.Hostname, Privacy.HostPlaceholder, StringComparison.OrdinalIgnoreCase);
            return ScrubRemote(text);
        }
        return summary with
        {
            Hostname = Privacy.HostPlaceholder,
            Title = Hide(summary.Title),
            Verdict = Hide(summary.Verdict),
            Fullest = summary.Fullest is null ? null : Hide(summary.Fullest),
            Checks = summary.Checks.Select(c => new SummaryCheck(Hide(c.Label), c.State, Hide(c.Detail))).ToList(),
            Fixes = summary.Fixes.Select(Hide).ToList()
        };
    }

    private static IEnumerable<ConnectionNode> Walk(ConnectionNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Walk(child))
                yield return descendant;
    }
}
