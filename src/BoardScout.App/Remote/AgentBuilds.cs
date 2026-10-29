using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BoardScout.Remote;

/// <summary>
/// The BoardScout build a remote machine runs. It must be this exact version (the two talk to each other), and
/// it comes from, in order: this program itself when it is the same kind of build, a file beside it (put the
/// release's Linux binaries next to BoardScout.exe), BoardScout's download folder, or the project's build
/// folder. When none has it, the Machines page offers to fetch it from this version's GitHub release, checked
/// against the release's SHA256SUMS before it is used.
/// </summary>
internal static class AgentBuilds
{
    public const string ReleaseHost = "github.com/mikedopp/BoardScout/releases";

    /// <summary>Every build a machine can run, for the Dependencies list and the download offer.</summary>
    public static readonly string[] Rids = ["win-x64", "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64", "linux-arm"];

    private static readonly Dictionary<string, (long Length, DateTime Written, string Hash)> Hashes = [];

    /// <summary>The release's file name: BoardScout-2.0.0-linux-x64, BoardScout-2.0.0-win-x64.exe.</summary>
    public static string FileName(string rid, string version) => rid.StartsWith("win", StringComparison.Ordinal)
        ? $"BoardScout-{version}-{rid}.exe"
        : $"BoardScout-{version}-{rid}";

    /// <summary>What the copy on the machine is called (one per version; older ones are removed).</summary>
    public static string RemoteName(string rid, string version) => rid.StartsWith("win", StringComparison.Ordinal)
        ? $"BoardScout-{version}-{rid}.exe"
        : $"boardscout-{version}-{rid}";

    public static string SumsName(string rid, string version) => rid.StartsWith("win", StringComparison.Ordinal)
        ? $"BoardScout-{version}-SHA256SUMS.txt"
        : $"BoardScout-{version}-linux-SHA256SUMS.txt";

    public static string DownloadFolder(string dataRoot) => Path.Combine(dataRoot, "remote-builds");

    /// <summary>The build this program is: its own file can be copied when a machine needs the same one.</summary>
    public static string OwnRid => RuntimeInformation.RuntimeIdentifier;

    /// <summary>
    /// True when this program is the standalone build: one file with Assets, Shell and DriverScout packed inside, so
    /// it can be copied as is. The portable build is a single-file exe too, but those folders sit beside it, so
    /// copying only the exe would leave the remote agent without its pages.
    /// </summary>
#pragma warning disable IL3000 // The empty location inside a single-file bundle is exactly what this asks about.
    public static bool IsSingleFile => IsStandalone(string.IsNullOrEmpty(typeof(AgentBuilds).Assembly.Location), AppContext.BaseDirectory, Environment.ProcessPath);
#pragma warning restore IL3000

    /// <summary>
    /// A bundle that carries its content unpacks it to a cache folder and runs from there, so the base directory is
    /// not the exe's own folder. A bundle that leaves the content loose runs from the folder it sits in.
    /// </summary>
    public static bool IsStandalone(bool singleFile, string baseDirectory, string? processPath)
    {
        if (!singleFile || string.IsNullOrEmpty(processPath)) return false;
        var exeFolder = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(exeFolder)) return false;
        return !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeFolder)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Where a build for this machine is on this computer, and what it is (for the log), or null.</summary>
    public static (string Path, string From)? Find(string rid, string version, string dataRoot)
    {
        var name = FileName(rid, version);
        if (rid == OwnRid && IsSingleFile && Environment.ProcessPath is { } self && File.Exists(self))
            return (self, "this program");
        var exeFolder = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        foreach (var (folder, from) in new[] { (exeFolder, "beside BoardScout"), (DownloadFolder(dataRoot), "downloaded from GitHub") })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) return (path, from);
        }
        // Running from a checkout: the build folder a few levels up holds every build of this version.
        var dir = new DirectoryInfo(exeFolder);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "build", name);
            if (File.Exists(path)) return (path, "the build folder");
        }
        return null;
    }

    public static string Sha256(string path)
    {
        var info = new FileInfo(path);
        lock (Hashes)
        {
            if (Hashes.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc) return known.Hash;
        }
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        lock (Hashes) Hashes[path] = (info.Length, info.LastWriteTimeUtc, hash);
        return hash;
    }

    /// <summary>
    /// Fetches this version's build for a machine from GitHub, only when the person asks. The release's checksum
    /// file is fetched first; the build is kept only if its SHA256 matches.
    /// </summary>
    public static async Task<string> DownloadAsync(string rid, string version, string dataRoot, Action<long, long?> progress, CancellationToken token)
    {
        var name = FileName(rid, version);
        var baseUrl = $"https://github.com/mikedopp/BoardScout/releases/download/v{version}/";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"BoardScout/{version}");
        string sums;
        try
        {
            sums = await http.GetStringAsync(baseUrl + SumsName(rid, version), token);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteException("no-build", $"GitHub has no BoardScout {version} release with checksums to download from.", ex.Message) { Rid = rid };
        }
        var expected = ExpectedHash(sums, name)
                       ?? throw new RemoteException("no-build", $"The {version} release doesn't list {name}.") { Rid = rid };

        var folder = DownloadFolder(dataRoot);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        var part = target + ".part";
        using (var response = await http.GetAsync(baseUrl + name, HttpCompletionOption.ResponseHeadersRead, token))
        {
            if (!response.IsSuccessStatusCode)
                throw new RemoteException("no-build", $"GitHub answered {(int)response.StatusCode} for {name}.") { Rid = rid };
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var file = File.Create(part);
            var buffer = new byte[256 * 1024];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                progress(done, total);
            }
        }
        var actual = Sha256(part);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new RemoteException("no-build", $"The downloaded {name} didn't match the release's checksum, so it was deleted.", $"expected {expected}, got {actual}") { Rid = rid };
        }
        File.Move(part, target, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return target;
    }

    /// <summary>The hash a SHA256SUMS file lists for a file name ("hash  name" or "hash *name").</summary>
    public static string? ExpectedHash(string sums, string name)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var parts = raw.Trim().Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim() == name && parts[0].Length == 64) return parts[0].ToLowerInvariant();
        }
        return null;
    }
}
