using BoardScout.DriveHealth;
using BoardScout.Models;
using BoardScout.Services;
using Xunit;

namespace BoardScout.Tests;

// Drive health over time: smartctl's JSON in, readings kept per drive, a verdict from the whole history.
// Serial numbers here are made up.
public class DriveHealthTests
{
    private static string Nvme(long written, long mediaErrors, int percentUsed = 3, long hours = 1200) => $$"""
        {
          "device": { "name": "/dev/sda", "type": "nvme", "protocol": "NVMe" },
          "model_name": "Example NVMe 1TB",
          "serial_number": "TEST0001",
          "firmware_version": "1.0",
          "user_capacity": { "bytes": 1000204886016 },
          "smart_status": { "passed": true },
          "temperature": { "current": 41 },
          "power_on_time": { "hours": {{hours}} },
          "power_cycle_count": 300,
          "nvme_smart_health_information_log": {
            "critical_warning": 0, "temperature": 41, "available_spare": 100, "available_spare_threshold": 10,
            "percentage_used": {{percentUsed}}, "data_units_read": 2000000, "data_units_written": {{written}},
            "power_cycles": 300, "power_on_hours": {{hours}}, "unsafe_shutdowns": 12,
            "media_errors": {{mediaErrors}}, "num_err_log_entries": 0
          }
        }
        """;

    [Fact]
    public void Nvme_counters_are_read_in_bytes()
    {
        var r = Smartctl.Parse(Nvme(written: 10_000_000, mediaErrors: 0), DateTimeOffset.Now);
        Assert.Equal("ok", r.Status);
        Assert.Equal(10_000_000L * 512_000, r.BytesWritten);
        Assert.Equal(3, r.PercentUsed);
        Assert.Equal(1200, r.PowerOnHours);
        Assert.Equal("SSD", r.Media);
    }

    [Fact]
    public async Task Growing_media_errors_turn_into_a_trend_and_a_suggestion()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-drivehealth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(DriveHealthService.Folder(root));
            var now = DateTimeOffset.Now;
            var key = Store.KeyFor("Example NVMe 1TB", "TEST0001", 1000204886016);
            for (var day = 0; day < 10; day++)
            {
                var r = Smartctl.Parse(Nvme(written: 10_000_000 + day * 40_000, mediaErrors: day < 7 ? 0 : day - 6, hours: 1200 + day * 8),
                    now.AddDays(day - 9));
                r.Key = key;
                r.Letters = ["X:"];
                store.Append(r);
            }
            // the last pass is fresh, so the refresh only loads the history and reads no real drive
            store.AppendVolumes([new VolumeReading { At = now, Letter = "X:", Size = 1_000_000_000_000, Free = 600_000_000_000, DriveKey = key }]);
            await DriveHealthService.RefreshAsync(root, force: false);

            var report = DriveHealthService.ForLetter("x:");
            Assert.NotNull(report);
            Assert.Equal(10, report!.Readings);
            Assert.Equal("act", report.Verdict);
            Assert.Contains(report.Findings, f => f.Basis == "trend");
            Assert.Equal("Act now", DriveHealthService.VerdictLabel(report.Verdict));
            Assert.Contains("1,272 h on", DriveHealthService.LifeLine(report));
            Assert.Contains("10 readings since", DriveHealthService.Tooltip(report));

            var problems = DriveHealthService.Problems().ToList();
            Assert.NotEmpty(problems);
            Assert.Contains(problems, p => p.Finding.Severity == "critical" && p.Title.StartsWith("Example NVMe 1TB (X:)", StringComparison.Ordinal));
            Assert.DoesNotContain(problems, p => p.Finding.Code == "vol-full");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Drive_keys_never_depend_on_letters_or_disk_numbers()
    {
        Assert.Equal(Store.KeyFor("Example NVMe 1TB", "TEST0001", 1), Store.KeyFor("Example NVMe 1TB", " TEST0001 ", 2));
        Assert.DoesNotContain(' ', Store.KeyFor("Example NVMe 1TB", "TEST0001", 1));
    }

    [Theory]
    [InlineData("healthy", "Healthy")]
    [InlineData("watch", "Watch")]
    [InlineData("attention", "Needs a look")]
    [InlineData("act", "Act now")]
    [InlineData("unknown", "Not read")]
    public void Verdicts_read_as_words(string verdict, string label) =>
        Assert.Equal(label, DriveHealthService.VerdictLabel(verdict));
}
