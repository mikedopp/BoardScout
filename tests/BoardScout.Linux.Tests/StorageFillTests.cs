using BoardScout.Services;
using Xunit;

namespace BoardScout.Linux.Tests;

/// <summary>Storage › What fills a drive on Linux: which mounts are offered, and the Linux rules the scan sorts by.</summary>
public class StorageFillTests
{
    [Theory]
    [InlineData("/", "ext4", 100L, true)]
    [InlineData("/srv/sync", "ext4", 100L, true)]
    [InlineData("/boot/efi", "vfat", 100L, true)]
    [InlineData("/home", "btrfs", 100L, true)]
    [InlineData("/proc", "proc", 0L, false)]
    [InlineData("/run", "tmpfs", 100L, false)]
    [InlineData("/dev/shm", "tmpfs", 100L, false)]
    [InlineData("/sys/fs/cgroup", "cgroup2", 0L, false)]
    [InlineData("/snap/core22/1380", "squashfs", 100L, false)]
    [InlineData("/var/lib/docker/overlay2/abc/merged", "overlay", 100L, false)]
    [InlineData("/mnt/empty", "ext4", 0L, false)]
    public void Only_real_filesystems_are_offered(string mount, string format, long total, bool offered) =>
        Assert.Equal(offered, StorageFill.IsLinuxScannable(mount, format, total));

    [Fact]
    public async Task A_server_listing_is_sorted_with_the_Linux_rules()
    {
        // type, 512-byte blocks, links, inode, path: what BoardScout.StorageScan.Scanner.FindCommand prints
        const string listing = "d 8 1 2 \nd 8 1 3 usr\nd 8 1 4 usr/bin\nf 900 1 5 usr/bin/bash\nd 8 1 6 var\nd 8 1 7 var/cache\nf 50 1 8 var/cache/x\n" +
                               "d 8 1 9 home\nd 8 1 10 home/me\nf 70 1 11 home/me/clip.mkv\n";
        var scanner = new BoardScout.StorageScan.Scanner(BoardScout.StorageScan.ScanFlavor.Linux);
        await scanner.ReadListingAsync(new StringReader(listing), "/", CancellationToken.None);
        var b = scanner.Bytes;
        Assert.Equal(900 * 512, b[BoardScout.StorageScan.Cats.System]);
        Assert.Equal(50 * 512, b[BoardScout.StorageScan.Cats.Caches]);
        Assert.Equal(70 * 512, b[BoardScout.StorageScan.Cats.Movies]);
        Assert.Equal(3, scanner.Files);
    }

    [Fact]
    public void The_listing_command_stays_on_one_filesystem_and_runs_last_in_line() =>
        Assert.Equal("nice -n 19 $(command -v ionice >/dev/null 2>&1 && echo ionice -c 3) find '/srv/sync' -xdev -printf '\\0%y %b %n %i %P\\n' 2>/dev/null",
            BoardScout.StorageScan.Scanner.FindCommand("/srv/sync"));

    [Fact]
    public async Task A_file_name_with_a_line_break_cannot_add_an_entry()
    {
        // Every real line starts with a NUL; the file "a\nf 900 1 5 home/fake" spills a second line without one.
        const string listing = "\0d 8 1 2 \n\0d 8 1 3 home\n\0f 70 1 4 home/a\nf 900 1 5 home/fake\n";
        var scanner = new BoardScout.StorageScan.Scanner(BoardScout.StorageScan.ScanFlavor.Linux);
        var feed = scanner.BeginListing("/");
        foreach (var line in listing.Split('\n')) feed.Add(line);
        feed.Complete();
        Assert.Equal(1, scanner.Files);
        Assert.Equal(3, feed.Records);
        Assert.True(scanner.Bytes.Sum() < 900 * 512);
    }

    [Fact]
    public void A_listing_with_nothing_in_it_has_no_records()
    {
        var feed = new BoardScout.StorageScan.Scanner(BoardScout.StorageScan.ScanFlavor.Linux).BeginListing("/");
        feed.Complete();
        Assert.Equal(0, feed.Records);
    }
}
