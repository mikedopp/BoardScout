using BoardScout.Services;
using Xunit;

namespace BoardScout.Tests;

/// <summary>The Storage tab's "what fills a drive" scan: the storage scanner inside BoardScout, and what it may be pointed at.</summary>
public class StorageScanTests
{
    private static void Make(string root, string relative, int bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public async Task Scanner_sorts_a_made_up_tree_into_the_right_buckets()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-storage-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Make(root, @"Users\me\Pictures\a.jpg", 1000);
            Make(root, @"Users\me\Videos\b.mp4", 5000);
            Make(root, @"Users\me\proj\node_modules\x\big.png", 2000); // a developer folder beats the .png extension
            Make(root, @"Users\me\proj\node_modules\tmp\index.js", 300); // an npm package named tmp is not a cache
            Make(root, @"Users\me\AppData\Local\Temp\t.tmp", 600);     // caches
            Make(root, @"$Recycle.Bin\S-1\deleted.mp4", 3000);         // trash beats the .mp4 extension
            Make(root, @"Windows\System32\k.dll", 4000);               // system
            Make(root, @"Program Files\App\app.exe", 8000);            // apps

            var scanner = new BoardScout.StorageScan.Scanner();
            await scanner.RunAsync(root, CancellationToken.None);
            var bytes = scanner.Bytes;

            Assert.Equal(1000, bytes[BoardScout.StorageScan.Cats.Photos]);
            Assert.Equal(5000, bytes[BoardScout.StorageScan.Cats.Movies]);
            Assert.Equal(2300, bytes[BoardScout.StorageScan.Cats.Developer]);
            Assert.Equal(600, bytes[BoardScout.StorageScan.Cats.Caches]);
            Assert.Equal(3000, bytes[BoardScout.StorageScan.Cats.Trash]);
            Assert.Equal(4000, bytes[BoardScout.StorageScan.Cats.System]);
            Assert.Equal(8000, bytes[BoardScout.StorageScan.Cats.Apps]);
            Assert.Equal(8, scanner.Files);
            Assert.Equal(0, scanner.SkippedLinks);
            Assert.Single(scanner.CacheRoots());
        }
        finally { try { Directory.Delete(root, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public async Task A_cancelled_scan_stops()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BoardScout.StorageScan.Scanner().RunAsync(Path.GetTempPath(), cts.Token));
    }

    [Fact]
    public void The_page_can_name_a_drive_the_way_the_Filesystems_list_does()
    {
        var root = StorageFill.ScannableDrives().First();               // "C:\"
        Assert.Equal(root, StorageFill.MatchDrive(root.TrimEnd('\\')));    // "C:"
        Assert.Equal(root, StorageFill.MatchDrive(root.ToLowerInvariant()));
        Assert.Null(StorageFill.MatchDrive(root + "Windows"));            // a folder is never accepted
        Assert.Null(StorageFill.MatchDrive(null));
    }

    [Fact]
    public void Only_drive_roots_can_be_scanned()
    {
        var drives = StorageFill.ScannableDrives();
        Assert.NotEmpty(drives);
        // every allowed entry is a bare drive root, so the dashboard can never be pointed at a folder such as C:\Users
        Assert.All(drives, d => Assert.Equal(Path.GetPathRoot(d), d));
        Assert.DoesNotContain(drives, d => string.Equals(d, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase));
    }
}
