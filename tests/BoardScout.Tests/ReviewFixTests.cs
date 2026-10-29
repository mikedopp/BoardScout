using BoardScout.Remote;
using BoardScout.Services;
using BoardScout.Web;
using Xunit;

namespace BoardScout.Tests;

// Four defects found by an outside review while every other test passed. Each test fails on the old code.
public class ReviewFixTests
{
    // ---- Remote agent copy: the portable exe has its pages beside it, so it can't be copied alone ----------

    [Fact]
    public void Portable_single_file_is_not_copied_as_an_agent()
    {
        // Single-file without packed content runs from its own folder.
        Assert.False(AgentBuilds.IsStandalone(true, @"C:\Apps\BoardScout\", @"C:\Apps\BoardScout\BoardScout.exe"));
        Assert.False(AgentBuilds.IsStandalone(true, @"C:\Apps\BoardScout", @"c:\apps\boardscout\BoardScout.exe"));
    }

    [Fact]
    public void Standalone_single_file_runs_from_its_unpacked_cache_and_can_be_copied()
    {
        Assert.True(AgentBuilds.IsStandalone(true, @"C:\Users\x\AppData\Local\Temp\.net\BoardScout\abc123\", @"C:\Apps\BoardScout\BoardScout.exe"));
    }

    [Fact]
    public void A_build_that_is_not_one_file_is_never_copied()
    {
        Assert.False(AgentBuilds.IsStandalone(false, @"C:\x\unpacked\", @"C:\Apps\BoardScout\BoardScout.exe"));
        Assert.False(AgentBuilds.IsStandalone(true, @"C:\x\unpacked\", null));
    }

    // ---- Driver downloads: same file name from different URLs must not share a cache entry ---------------

    [Fact]
    public void Two_urls_with_the_same_file_name_get_different_cache_paths()
    {
        var a = DriverDownloadService.CachePath(@"C:\d", new Uri("https://one.example/audio/setup.exe"), "setup.exe");
        var b = DriverDownloadService.CachePath(@"C:\d", new Uri("https://two.example/lan/setup.exe"), "setup.exe");
        Assert.NotEqual(a, b);
        Assert.Equal("setup.exe", Path.GetFileName(a));
        Assert.Equal(a, DriverDownloadService.CachePath(@"C:\d", new Uri("https://one.example/audio/setup.exe"), "setup.exe"));
    }

    [Fact]
    public void A_cached_file_is_reused_only_when_size_and_modified_time_still_match()
    {
        var path = Path.Combine(Path.GetTempPath(), "bs-cache-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, new byte[100]);
            var stamp = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
            File.SetLastWriteTimeUtc(path, stamp.UtcDateTime);

            Assert.True(DriverDownloadService.IsCurrent(path, 100, stamp));
            Assert.True(DriverDownloadService.IsCurrent(path, 100, null));
            Assert.False(DriverDownloadService.IsCurrent(path, 99, stamp));                    // different size
            Assert.False(DriverDownloadService.IsCurrent(path, 100, stamp.AddDays(30)));       // same size, newer installer
            Assert.False(DriverDownloadService.IsCurrent(path, null, stamp));                  // server gave no size
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Privacy mode: a summary gathered before it was switched on is hidden when shown ------------------

    [Fact]
    public void Privacy_hides_a_cached_summarys_name_and_home_folder()
    {
        var gathered = new MachineSummary
        {
            Hostname = "werc-0",
            Title = "werc-0 · Ubuntu",
            Fullest = "/home/mike/media",
            Verdict = "Backups at /home/mike/.cache/boardscout, from 192.168.1.20 or mike@example.com",
            Checks = [new SummaryCheck("Firewall", "warn", "werc-0 off; C:\\Users\\Mike\\notes")],
            Fixes = ["Clean /home/mike/Downloads"]
        };

        var shown = MachineSummaries.ForDisplay(gathered, privacy: true);
        var all = string.Join("|", shown.Hostname, shown.Title, shown.Fullest, shown.Verdict, shown.Checks[0].Detail, shown.Fixes[0]);

        Assert.Equal(Services.Privacy.HostPlaceholder, shown.Hostname);
        foreach (var leaked in new[] { "werc-0", "mike", "Mike", "192.168.1.20", "example.com" })
            Assert.DoesNotContain(leaked, all);
        Assert.StartsWith("/home/…", shown.Fullest);
        Assert.Equal("werc-0", gathered.Hostname); // the kept copy is untouched, so turning privacy off shows it again
    }

    [Fact]
    public void Without_privacy_the_summary_is_shown_as_gathered()
    {
        var gathered = new MachineSummary { Hostname = "werc-0", Fullest = "/home/mike/media" };
        Assert.Same(gathered, MachineSummaries.ForDisplay(gathered, privacy: false));
    }

    // ---- Two local dashboards on one machine ----------------------------------------------------------------

    [Fact]
    public void Dashboards_on_different_ports_use_different_cookie_names()
    {
        Assert.NotEqual(WebServer.CookieName(false, 8811), WebServer.CookieName(false, 8812));
        Assert.Equal(WebServer.CookieName(false, 8811), WebServer.CookieName(false, 8811));
        Assert.NotEqual(WebServer.CookieName(true, 8811), WebServer.CookieName(true, 8811)); // agents: random per run
    }
}
