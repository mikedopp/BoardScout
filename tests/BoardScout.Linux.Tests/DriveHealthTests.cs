using BoardScout.DriveHealth;
using BoardScout.Models;
using BoardScout.Services;
using BoardScout.Views;
using Xunit;

namespace BoardScout.Linux.Tests;

/// <summary>
/// Storage › drive health on Linux: kept readings matched to lsblk's disks (NVMe through its controller), the
/// suggestions they raise, and the root note. Models and serials here are made up.
/// </summary>
public class DriveHealthTests
{
    [Fact]
    public async Task Disks_get_their_health_from_the_kept_readings()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-linux-drivehealth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new Store(DriveHealthService.Folder(root));
            var now = DateTimeOffset.Now;
            store.Append(new DriveReading
            {
                At = now, Key = "example-sata", Source = "smartctl", Status = "ok", Model = "Example SATA 500GB", Device = "/dev/sda",
                Protocol = "ATA", Media = "HDD", SmartPassed = true, PowerOnHours = 5_000, Uncorrectable = 1, Capacity = 500_107_862_016
            });
            store.Append(new DriveReading
            {
                At = now, Key = "example-nvme", Source = "smartctl", Status = "ok", Model = "Example NVMe 1TB", Device = "/dev/nvme0",
                Protocol = "NVMe", Media = "SSD", SmartPassed = true, PowerOnHours = 900, PercentUsed = 4, Capacity = 1_000_204_886_016
            });
            // without root: what lsblk and the kernel know, and the device path to match it by
            store.Append(new DriveReading
            {
                At = now, Key = "example-usb", Source = "linux", Status = "needs-admin", Model = "Example USB 1TB", Device = "/dev/sdb"
            });
            // a phone reported to a hub never matches a local disk name
            store.Append(new DriveReading { At = now, Key = "example-phone", Source = "app", Status = "ok", Model = "Example Phone", Device = "sdc" });
            store.AppendVolumes([new VolumeReading { At = now, Letter = "/", Size = 500_000_000_000, Free = 200_000_000_000, DriveKey = "example-sata" }]);

            // the last pass is fresh, so this only loads the history: no real drive is read
            await DriveHealthService.RefreshAsync(root, force: false);

            Assert.Equal("Example SATA 500GB", DriveHealthService.ForDevice("sda")?.Latest.Model);
            Assert.Equal("Example NVMe 1TB", DriveHealthService.ForDevice("nvme0n1")?.Latest.Model);
            Assert.Null(DriveHealthService.ForDevice("nvme1n1"));
            Assert.Null(DriveHealthService.ForDevice("nvme0n10x"));
            Assert.Equal("Example USB 1TB", DriveHealthService.ForDevice("sdb")?.Latest.Model);

            var sata = DriveHealthService.ForDevice("sda")!;
            Assert.Contains("5,000 h on", DriveHealthService.LifeLine(sata));
            Assert.NotEqual("healthy", sata.Verdict);
            var usb = DriveHealthService.ForDevice("sdb")!;
            Assert.Equal(OperatingSystem.IsWindows() ? "Needs admin rights to read" : "Needs root to read", DriveHealthService.LifeLine(usb));

            var problems = DriveHealthService.Problems().ToList();
            Assert.Contains(problems, p => p.Title.StartsWith("Example SATA 500GB", StringComparison.Ordinal));
            Assert.DoesNotContain(problems, p => p.Title.StartsWith("Example NVMe 1TB", StringComparison.Ordinal));
            Assert.False(DriveHealthService.AllLocked);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Disk_rows_carry_health_only_when_there_is_some()
    {
        var plain = new DiskRow("sda", "Example", "sata", 1, "HDD", null, null, null);
        Assert.Null(plain.Health);
        Assert.Null(plain.Life);
        var read = plain with { Health = "attention", HealthLabel = "Needs a look", HealthTip = "tip", Life = "5,000 h on" };
        Assert.Equal("Needs a look", read.HealthLabel);
    }

    [Theory]
    [InlineData("/home/a/.local/share/boardscout", "/home/a", true)]
    [InlineData("/home/a", "/home/a", false)]
    [InlineData("/home/ab/.local", "/home/a", false)]
    [InlineData("/var/lib/boardscout", "/home/a", false)]
    public void Only_folders_inside_the_users_home_go_back_to_them(string path, string home, bool inside) =>
        Assert.Equal(inside, DriveHealthCli.Inside(path, home));

    [Fact]
    public void A_dev_path_finds_the_same_drive() =>
        Assert.Equal(DriveHealthService.ForDevice("sdz"), DriveHealthService.ForDevice("/dev/sdz"));

    [Fact]
    public void Without_sudo_the_user_keeps_their_own_data_folder() =>
        Assert.Null(Environment.IsPrivilegedProcess ? null : DriveHealthCli.SudoUserData());

    [Fact]
    public void The_command_line_knows_drive_health() =>
        Assert.Equal("drive-health", LaunchOptions.Parse(["--drive-health"]).Command);
}
