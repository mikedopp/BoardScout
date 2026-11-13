using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BoardScout.StorageScan;

/// <summary>The ten buckets a file can land in. The order is the legend's order and the index is what the UI uses.</summary>
public static class Cats
{
    public const int Apps = 0, Developer = 1, Photos = 2, Movies = 3, Documents = 4, Music = 5, System = 6, Caches = 7, Trash = 8, Other = 9;
    public static readonly string[] Ids = { "apps", "developer", "photos", "movies", "documents", "music", "system", "caches", "trash", "other" };
    public static int Count => Ids.Length;

    static readonly HashSet<string> PhotoExt = Set("jpg jpeg png heic heif gif webp bmp tif tiff raw dng cr2 cr3 nef arw orf rw2 svg psd");
    static readonly HashSet<string> MovieExt = Set("mp4 mkv mov avi wmv webm m4v mpg mpeg flv ts m2ts vob 3gp lrv insv");
    static readonly HashSet<string> MusicExt = Set("mp3 flac wav m4a aac ogg oga wma opus aiff m4b mid midi");
    static readonly HashSet<string> DocExt = Set("pdf doc docx xls xlsx ppt pptx txt md csv odt ods odp rtf epub mobi pages numbers key one json xml yaml yml");

    static HashSet<string> Set(string words) => new(words.Split(' '), StringComparer.OrdinalIgnoreCase);

    /// <summary>The bucket for a file by its extension alone (when no folder rule already decided it).</summary>
    public static int ByExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return Other;
        var ext = name.AsSpan(dot + 1).ToString();
        if (PhotoExt.Contains(ext)) return Photos;
        if (MovieExt.Contains(ext)) return Movies;
        if (MusicExt.Contains(ext)) return Music;
        if (DocExt.Contains(ext)) return Documents;
        return Other;
    }
}

/// <summary>Which folder rules apply: a Windows drive (Windows, Program Files, $Recycle.Bin), a Linux filesystem (/usr, /opt, /var/cache) or a Mac (/System, /Applications, ~/Library/Caches).</summary>
public enum ScanFlavor { Windows, Linux, Mac }

public sealed record CacheRoot(string Path, long Bytes);

/// <summary>
/// Reads a drive's folder tree (read-only: it lists names and sizes and never reads or changes a file's contents; under
/// a drive's Windows folder it opens each file for its attributes only, to count a hard-linked file once) on several
/// threads and adds every file's size to a bucket. Folder rules decide the bucket first (Recycle Bin, caches,
/// developer folders, Windows, Program Files, /usr, /opt); otherwise the extension does.
/// It can also take the same tree from a listing made elsewhere (a server's <c>find</c>), see <see cref="ReadListingAsync"/>.
/// </summary>
public sealed class Scanner
{
    static readonly HashSet<string> CacheNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "temp", "tmp", "cache", "caches", ".cache", "code cache", "gpucache", "grshadercache", "shadercache", "dawncache", "dawngraphitecache",
        "dawnwebgpucache", "inetcache", "crashdumps", "cache_data", "service worker", ".npm", "npm-cache", "__pycache__", "webcache",
    };

    static readonly HashSet<string> DevNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".gradle", ".nuget", ".vs", ".m2", ".venv", "venv", ".android", ".cargo", ".rustup", ".dotnet", ".pnpm-store",
        "obj", ".idea", ".vscode",
    };

    // Linux folders by absolute path. Apps are checked first so /usr/local is Apps although /usr is System data.
    static readonly HashSet<string> LinuxApps = new(StringComparer.Ordinal)
    {
        "/opt", "/snap", "/usr/local", "/var/lib/snapd", "/var/lib/flatpak", "/var/lib/docker", "/var/lib/containerd", "/var/lib/libvirt/images",
    };

    static readonly HashSet<string> LinuxSystem = new(StringComparer.Ordinal)
    {
        "/usr", "/lib", "/lib32", "/lib64", "/libx32", "/bin", "/sbin", "/boot", "/etc", "/var/log", "/var/crash", "/var/backups",
        "/var/lib/dpkg", "/var/lib/apt", "/var/lib/systemd", "/var/lib/initramfs-tools", "/lost+found",
    };

    // macOS folders by absolute path (a home folder is /Users/<name>). /Library/Caches and ~/Library/Caches are Caches by name.
    static readonly HashSet<string> MacApps = new(StringComparer.Ordinal)
    {
        "/Applications", "/opt/homebrew", "/usr/local", "/opt/local", "/System/Applications",
    };

    static readonly HashSet<string> MacSystem = new(StringComparer.Ordinal)
    {
        "/System", "/Library", "/usr", "/bin", "/sbin", "/cores", "/private/etc", "/private/var/db", "/private/var/log", "/private/var/vm",
        "/private/var/protected",
    };

    /// <summary>Linux filesystems that hold no files on a disk; a drive list should never offer them.</summary>
    public static readonly IReadOnlySet<string> PseudoFilesystems = new HashSet<string>(StringComparer.Ordinal)
    {
        "proc", "sysfs", "devtmpfs", "devpts", "tmpfs", "cgroup", "cgroup2", "securityfs", "pstore", "efivarfs", "bpf", "debugfs", "tracefs",
        "configfs", "fusectl", "mqueue", "hugetlbfs", "autofs", "rpc_pipefs", "binfmt_misc", "nsfs", "squashfs", "overlay", "ramfs", "fuse.portal",
        "fuse.gvfsd-fuse", "nfsd", "selinuxfs",
    };

    /// <summary>
    /// The shell command a Linux server runs to list one filesystem for <see cref="ReadListingAsync"/>: every entry under the
    /// mount point, one per line as "type blocks links inode relative-path", never crossing into another filesystem, at the lowest CPU and
    /// disk priority (ionice only where the server has it). Every line starts with a NUL byte, which no file name can hold,
    /// so a name with a line break in it cannot pass for a line of its own. <paramref name="mount"/> must already be a
    /// checked mount point (it is single-quoted here). Needs GNU find; without it nothing comes back, see
    /// <see cref="ListingFeed.Records"/>.
    /// </summary>
    public static string FindCommand(string mount)
    {
        if (string.IsNullOrEmpty(mount) || mount[0] != '/' || mount.Contains('\'') || mount.Contains('\n'))
            throw new ArgumentException("not a mount point", nameof(mount));
        // %b = 512-byte blocks the file really takes (a sparse VM image counts what it holds, not its nominal size);
        // %n and %i let a file with several hard links be counted once, as df counts it
        return $"nice -n 19 $(command -v ionice >/dev/null 2>&1 && echo ionice -c 3) find '{mount}' -xdev -printf '\\0%y %b %n %i %P\\n' 2>/dev/null";
    }

    const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;
    readonly record struct Entry(string Name, bool IsDir, long Length, FileAttributes Attr);
    readonly record struct Work(string Path, int Class, int Depth, string Parent, string? CacheRoot, bool Links = false);

    readonly ScanFlavor _flavor;
    readonly long[] _bytes = new long[Cats.Count];
    readonly ConcurrentDictionary<string, long> _cacheRoots = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentQueue<Work> _queue = new();
    readonly ConcurrentDictionary<(uint Volume, ulong File), byte> _linkedFiles = new();
    HashSet<string> _otherMounts = new(StringComparer.Ordinal);
    long _files, _skipped, _dirs, _linkNames;
    int _pending;
    volatile string _current = "";

    public Scanner() : this(OperatingSystem.IsWindows() ? ScanFlavor.Windows : OperatingSystem.IsMacOS() ? ScanFlavor.Mac : ScanFlavor.Linux) { }
    public Scanner(ScanFlavor flavor) { _flavor = flavor; }

    public ScanFlavor Flavor => _flavor;
    public long[] Bytes { get { var r = new long[_bytes.Length]; for (int i = 0; i < r.Length; i++) r[i] = Volatile.Read(ref _bytes[i]); return r; } }
    public long Files => Interlocked.Read(ref _files);
    public long Directories => Interlocked.Read(ref _dirs);
    /// <summary>Cloud-only placeholders (Windows) that take no room on the drive and so were not counted.</summary>
    public long SkippedCloud => Interlocked.Read(ref _skipped);
    /// <summary>Extra names of hard-linked files under a drive's Windows folder: each file was counted once, at its first name.</summary>
    public long SkippedLinks => Interlocked.Read(ref _linkNames);
    public string Current => _current;

    public IReadOnlyList<CacheRoot> CacheRoots(int top = 40) =>
        _cacheRoots.Select(p => new CacheRoot(p.Key, p.Value)).OrderByDescending(c => c.Bytes).Take(top).ToList();

    /// <summary>
    /// A cache by its name ("Temp", "Cache", ".cache"), unless it sits inside a developer or app folder: node_modules\tmp is
    /// an npm package and the /tmp inside a Docker image layer is part of the image, so neither is something to clear.
    /// </summary>
    static bool IsCache(int inherited, string name) => inherited != Cats.Developer && inherited != Cats.Apps && CacheNames.Contains(name);

    /// <summary>The bucket a Windows folder gives its files and subfolders, or -1 when it decides nothing (the extension will).</summary>
    public static int DirClass(int inherited, string name, int depth, string parent)
    {
        if (inherited == Cats.Trash) return Cats.Trash;
        if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return Cats.Trash;
        if (IsCache(inherited, name)) return Cats.Caches;
        if (inherited >= 0) return inherited;
        if (DevNames.Contains(name)) return Cats.Developer;
        if (depth == 1)
        {
            if (name.StartsWith('$')) return Cats.System;
            switch (name.ToLowerInvariant())
            {
                case "windows": case "recovery": case "system volume information": case "programdata": case "windows.old": case "config.msi":
                    return Cats.System;
                case "program files": case "program files (x86)": case "windowsapps":
                    return Cats.Apps;
            }
        }
        if (name.Equals("Programs", StringComparison.OrdinalIgnoreCase) && parent.Equals("Local", StringComparison.OrdinalIgnoreCase)) return Cats.Apps;
        return -1;
    }

    /// <summary>
    /// The bucket a Linux folder gives its files and subfolders, or -1. <paramref name="absolute"/> is the folder's full path
    /// ("/usr/local"), so the system rules only apply where the folders really are, not inside a data drive mounted elsewhere.
    /// </summary>
    public static int LinuxDirClass(int inherited, string absolute, string name, string parent)
    {
        if (inherited == Cats.Trash) return Cats.Trash;
        // a desktop's ~/.local/share/Trash, and the .Trash-1000 folder a removable drive gets
        if (name.StartsWith(".Trash-", StringComparison.Ordinal) || name == ".Trash" || (name == "Trash" && parent == "share")) return Cats.Trash;
        if (IsCache(inherited, name)) return Cats.Caches;
        if (LinuxApps.Contains(absolute)) return Cats.Apps;
        if (inherited >= 0) return inherited;
        if (DevNames.Contains(name)) return Cats.Developer;
        if (LinuxSystem.Contains(absolute)) return Cats.System;
        return -1;
    }

    /// <summary>
    /// The bucket a macOS folder gives its files and subfolders, or -1. <paramref name="absolute"/> is the full path. The
    /// startup disk's "/" holds the sealed system volume and, through firmlinks, the data volume (/Users, /Applications,
    /// /Library, /private/var); /System/Volumes/Data is its own mount point and is skipped, so nothing is counted twice.
    /// Written from Apple's documented layout, not checked on a Mac yet.
    /// </summary>
    public static int MacDirClass(int inherited, string absolute, string name, string parent)
    {
        if (inherited == Cats.Trash) return Cats.Trash;
        // ~/.Trash, and the .Trashes folder a removable drive gets
        if (name == ".Trash" || name == ".Trashes") return Cats.Trash;
        if (IsCache(inherited, name) || absolute == "/private/var/folders") return Cats.Caches;
        if (MacApps.Contains(absolute) || IsHomeChild(absolute, "Applications")) return Cats.Apps;
        if (inherited >= 0) return inherited;
        if (DevNames.Contains(name) || IsHomeChild(absolute, "Library/Developer")) return Cats.Developer;
        if (MacSystem.Contains(absolute)) return Cats.System;
        return -1;
    }

    /// <summary>"/Users/me/Library/Developer" is the home folder's "Library/Developer" (one level under /Users only).</summary>
    static bool IsHomeChild(string absolute, string rest)
    {
        if (!absolute.StartsWith("/Users/", StringComparison.Ordinal)) return false;
        int slash = absolute.IndexOf('/', 7);
        return slash > 7 && string.Equals(absolute[(slash + 1)..], rest, StringComparison.Ordinal);
    }

    /// <summary>A macOS file's bucket; <paramref name="dirAbsolute"/> is the folder it is in.</summary>
    public static int MacFileClass(int dirClass, string dirAbsolute, string name)
    {
        if (dirClass >= 0) return dirClass;
        // the swap and sleep files when /private/var/vm is not its own volume
        if (dirAbsolute == "/private/var/vm" || name == "sleepimage") return Cats.System;
        return Cats.ByExtension(name);
    }

    public static int FileClass(int dirClass, string name, int depth)
    {
        if (dirClass >= 0) return dirClass;
        if (depth == 0)
        {
            if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)
                || name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase)) return Cats.System;
        }
        return Cats.ByExtension(name);
    }

    /// <summary>A Linux file's bucket; <paramref name="dirAbsolute"/> is the folder it is in.</summary>
    public static int LinuxFileClass(int dirClass, string dirAbsolute, string name)
    {
        if (dirClass >= 0) return dirClass;
        if (dirAbsolute == "/" && (name == "swapfile" || name == "swap.img" || name.StartsWith("vmlinuz", StringComparison.Ordinal) || name.StartsWith("initrd.img", StringComparison.Ordinal)))
            return Cats.System;
        return Cats.ByExtension(name);
    }

    int Dir(int inherited, string full, string name, int depth, string parent) => _flavor switch
    {
        ScanFlavor.Linux => LinuxDirClass(inherited, full, name, parent),
        ScanFlavor.Mac => MacDirClass(inherited, full, name, parent),
        _ => DirClass(inherited, name, depth, parent),
    };

    int File(int dirClass, string dirFull, string name, int depth) => _flavor switch
    {
        ScanFlavor.Linux => LinuxFileClass(dirClass, dirFull, name),
        ScanFlavor.Mac => MacFileClass(dirClass, dirFull, name),
        _ => FileClass(dirClass, name, depth),
    };

    /// <summary>Walks a drive (Windows) or one filesystem (Linux: it stops at other mount points, as <c>find -xdev</c> does).</summary>
    public async Task RunAsync(string root, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        if (!OperatingSystem.IsWindows())
        {
            _otherMounts = ReadMountPoints();
            _otherMounts.Remove(root.Length > 1 ? root.TrimEnd('/') : root);
        }
        // the root's own rules (scanning /boot or /usr/local directly)
        string rootPath = root.Length > 1 ? root.TrimEnd('/') : root, rootName = Path.GetFileName(root.TrimEnd('/'));
        int rootClass = _flavor switch
        {
            ScanFlavor.Linux => LinuxDirClass(-1, rootPath, rootName, ""),
            ScanFlavor.Mac => MacDirClass(-1, rootPath, rootName, ""),
            _ => -1,
        };
        Interlocked.Increment(ref _pending);
        _queue.Enqueue(new Work(root, rootClass, 0, "", rootClass == Cats.Caches ? root : null));
        int threads = Math.Clamp(Environment.ProcessorCount, 4, 12);
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() => Worker(ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Every mount point this machine has, so a walk can stay on its own filesystem: /proc/self/mountinfo on Linux, the
    /// output of <c>mount</c> on macOS (which has no /proc).
    /// </summary>
    public static HashSet<string> ReadMountPoints()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (System.IO.File.Exists("/proc/self/mountinfo"))
            {
                foreach (var line in System.IO.File.ReadLines("/proc/self/mountinfo"))
                {
                    var parts = line.Split(' ');
                    if (parts.Length > 4) set.Add(UnescapeMount(parts[4]));
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                using var mount = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/sbin/mount")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                });
                if (mount != null)
                {
                    var text = mount.StandardOutput.ReadToEnd();
                    mount.WaitForExit(5000);
                    foreach (var point in ParseMacMounts(text)) set.Add(point.MountPoint);
                }
            }
        }
        catch (Exception) { /* no mount table: nothing to stop at */ }
        return set;
    }

    public sealed record MacMount(string Source, string MountPoint, string FileSystem, IReadOnlyList<string> Options);

    /// <summary>
    /// macOS <c>mount</c> output, one line per mount: "/dev/disk3s1s1 on / (apfs, sealed, local, read-only, journaled)".
    /// A mount point may hold spaces ("/Volumes/My Disk"); the type and options are the last parentheses.
    /// </summary>
    public static List<MacMount> ParseMacMounts(string text)
    {
        var list = new List<MacMount>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int on = line.IndexOf(" on ", StringComparison.Ordinal);
            int open = line.LastIndexOf(" (", StringComparison.Ordinal);
            if (on <= 0 || open <= on + 4 || !line.EndsWith(')')) continue;
            var options = line[(open + 2)..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (options.Length == 0) continue;
            list.Add(new MacMount(line[..on], line[(on + 4)..open], options[0], options[1..]));
        }
        return list;
    }

    /// <summary>mountinfo and /proc/mounts write a space as \040, a tab as \011 and a backslash as \134.</summary>
    public static string UnescapeMount(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length && IsOctal(s[i + 1]) && IsOctal(s[i + 2]) && IsOctal(s[i + 3]))
            {
                sb.Append((char)((s[i + 1] - '0') * 64 + (s[i + 2] - '0') * 8 + (s[i + 3] - '0')));
                i += 3;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
        static bool IsOctal(char c) => c >= '0' && c <= '7';
    }

    void Worker(CancellationToken ct)
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { /* not fatal */ }
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };
        var spin = new SpinWait();
        while (!ct.IsCancellationRequested)
        {
            if (!_queue.TryDequeue(out var w))
            {
                if (Volatile.Read(ref _pending) == 0) return;
                spin.SpinOnce(sleep1Threshold: 8);
                continue;
            }
            try { Process(w, options, ct); }
            catch (Exception) { /* a folder that vanished or refused: skip it */ }
            finally { Interlocked.Decrement(ref _pending); }
        }
    }

    void Process(Work w, EnumerationOptions options, CancellationToken ct)
    {
        _current = w.Path;
        Interlocked.Increment(ref _dirs);
        bool windows = OperatingSystem.IsWindows();
        var entries = new FileSystemEnumerable<Entry>(w.Path, (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.Attributes), options);
        long cacheBytes = 0;
        string parentName = Path.GetFileName(w.Path.TrimEnd('/', '\\'));
        foreach (var e in entries)
        {
            if (ct.IsCancellationRequested) return;
            if (e.IsDir)
            {
                // junctions and symlinks would count a folder twice (or loop): skip them
                if ((e.Attr & FileAttributes.ReparsePoint) != 0) continue;
                string full = Path.Join(w.Path, e.Name);
                // another filesystem mounted inside this one (and /proc, /sys, /dev, /run) is not on this drive
                if (_otherMounts.Count > 0 && _otherMounts.Contains(full)) continue;
                int cls = Dir(w.Class, full, e.Name, w.Depth + 1, parentName);
                string? cacheRoot = w.CacheRoot;
                if (cls == Cats.Caches && w.Class != Cats.Caches) cacheRoot = full;
                // the Windows folder keeps thousands of files under two names (WinSxS and System32): see WindowsSize
                bool links = w.Links || (windows && _flavor == ScanFlavor.Windows && w.Depth == 0 && e.Name.Equals("Windows", StringComparison.OrdinalIgnoreCase));
                Interlocked.Increment(ref _pending);
                _queue.Enqueue(new Work(full, cls, w.Depth + 1, parentName, cacheRoot, links));
            }
            else
            {
                // OneDrive-style placeholders take no room on this drive
                if ((e.Attr & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0)
                {
                    Interlocked.Increment(ref _skipped);
                    continue;
                }
                // a Linux symlink takes a few bytes; its target is counted where it lives
                if (!windows && (e.Attr & FileAttributes.ReparsePoint) != 0) continue;
                long size = e.Length;
                if (windows && (w.Links || (e.Attr & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0))
                {
                    size = WindowsSize(Path.Join(w.Path, e.Name), e.Length, e.Attr, w.Links);
                    if (size < 0) { Interlocked.Increment(ref _linkNames); continue; } // another name for a file already counted
                }
                int cls = File(w.Class, w.Path, e.Name, w.Depth);
                Interlocked.Add(ref _bytes[cls], size);
                Interlocked.Increment(ref _files);
                if (cls == Cats.Caches && w.CacheRoot != null) cacheBytes += size;
            }
        }
        if (cacheBytes > 0 && w.CacheRoot != null) _cacheRoots.AddOrUpdate(w.CacheRoot, cacheBytes, (_, old) => old + cacheBytes);
    }

    // ---- Windows: the room a file really takes
    //
    // A folder listing gives a file's nominal size. Two kinds of file take less than that: a compressed or sparse file,
    // and a file with several names (hard links), which is one file however many folders list it. Windows only tells the
    // link count for an open file, so files under the Windows folder (where nearly all hard links are) are opened for
    // their attributes only: no data is read and nothing is changed. Elsewhere only the folder listing is used.

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern uint GetCompressedFileSizeW(string path, out uint high);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleInfo info);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ByHandleInfo
    {
        public uint Attributes; public long Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    /// <summary>
    /// The bytes a Windows file takes on the drive, or -1 when it is another name of a hard-linked file already counted.
    /// Falls back to the listed size when Windows will not say.
    /// </summary>
    long WindowsSize(string path, long listed, FileAttributes attr, bool links)
    {
        // the \\?\ prefix lets the calls reach paths past 260 characters, which the Windows folder has
        string full = path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path;
        try
        {
            if (links)
            {
                const uint ReadAttributes = 0x80, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
                using var h = CreateFileW(full, ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
                if (!h.IsInvalid && GetFileInformationByHandle(h, out var info) && info.Links > 1
                    && !_linkedFiles.TryAdd((info.VolumeSerial, ((ulong)info.IndexHigh << 32) | info.IndexLow), 0)) return -1;
            }
            if ((attr & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
            {
                uint low = GetCompressedFileSizeW(full, out uint high);
                if (low != 0xFFFFFFFF || System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 0) return ((long)high << 32) | low;
            }
        }
        catch (Exception) { /* keep the listed size */ }
        return listed;
    }

    readonly record struct Frame(string Rel, string Abs, string Name, int Class, string? CacheRoot);

    /// <summary>
    /// Sorts a listing made by <see cref="FindCommand"/> on a Linux server (streamed over SSH) with the Linux rules:
    /// one entry per line, "type blocks links inode relative-path", in find's order (a folder comes before what is in it).
    /// Only regular files ('f') are counted; links, sockets and devices take no room worth showing.
    /// </summary>
    public async Task ReadListingAsync(TextReader reader, string mount, CancellationToken ct)
    {
        var feed = BeginListing(mount);
        long n = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if ((++n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            feed.Add(line);
        }
        feed.Complete();
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The same as <see cref="ReadListingAsync"/>, one line at a time, for a caller whose SSH client hands over lines
    /// as they arrive. Call <see cref="ListingFeed.Complete"/> at the end so the last cache folders are counted.
    /// One feed is read by one thread at a time; the scanner's totals can be read from any thread meanwhile.
    /// </summary>
    public ListingFeed BeginListing(string mount) => new(this, mount);

    public sealed class ListingFeed
    {
        readonly Scanner _s;
        readonly string _mount;
        readonly List<Frame> _stack;
        readonly Dictionary<string, long> _cacheBytes = new(StringComparer.Ordinal);
        readonly HashSet<long> _linked = new();
        long _n, _records;
        bool _marked;

        /// <summary>
        /// Lines read so far that were real entries. Still 0 after <see cref="Complete"/> means the server sent no listing
        /// (no GNU find, or the mount could not be read): say so, rather than show a drive nobody read.
        /// </summary>
        public long Records => Interlocked.Read(ref _records);

        internal ListingFeed(Scanner s, string mount)
        {
            _s = s;
            _mount = mount.Length > 1 ? mount.TrimEnd('/') : mount;
            int rootClass = LinuxDirClass(-1, _mount, _mount == "/" ? "" : _mount[(_mount.LastIndexOf('/') + 1)..], "");
            _stack = new List<Frame> { new("", _mount, "", rootClass, rootClass == Cats.Caches ? _mount : null) };
        }

        string Abs(string rel) => rel.Length == 0 ? _mount : (_mount == "/" ? "/" + rel : _mount + "/" + rel);

        /// <summary>
        /// One line of the listing: "f 24 1 393217 usr/lib/x.so" = type, 512-byte blocks, hard links, inode, then the path
        /// relative to the mount (which may hold spaces). A file with more than one link is counted at its first name only.
        /// </summary>
        public void Add(string line)
        {
            _n++;
            // FindCommand starts every line with a NUL. Once one is seen, a line without it is the tail of a file name
            // that holds a line break, not an entry. (A listing with no NULs at all is an older or hand-made one.)
            if (line.Length > 0 && line[0] == '\0') { _marked = true; line = line[1..]; }
            else if (_marked) return;
            if (line.Length < 8 || line[1] != ' ') return;
            int a = line.IndexOf(' ', 2), b = a < 0 ? -1 : line.IndexOf(' ', a + 1), c = b < 0 ? -1 : line.IndexOf(' ', b + 1);
            if (c < 0
                || !long.TryParse(line.AsSpan(2, a - 2), out long blocks)
                || !long.TryParse(line.AsSpan(a + 1, b - a - 1), out long links)
                || !long.TryParse(line.AsSpan(b + 1, c - b - 1), out long inode)) return;
            if (blocks < 0 || blocks > long.MaxValue / 512) return;
            Interlocked.Increment(ref _records);
            long size = blocks * 512;
            char type = line[0];
            string rel = line[(c + 1)..].TrimEnd('\r');
            if (type == 'f' && links > 1 && !_linked.Add(inode)) return; // another name for a file already counted
            if (rel.Length == 0) { Interlocked.Increment(ref _s._dirs); return; } // the mount point itself
            int slash = rel.LastIndexOf('/');
            string parentRel = slash < 0 ? "" : rel[..slash];
            string name = slash < 0 ? rel : rel[(slash + 1)..];
            while (_stack.Count > 1 && !string.Equals(_stack[^1].Rel, parentRel, StringComparison.Ordinal)) _stack.RemoveAt(_stack.Count - 1);
            var top = _stack[^1];
            if (!string.Equals(top.Rel, parentRel, StringComparison.Ordinal)) return; // out of order: a folder find could not list
            if (type == 'd')
            {
                Interlocked.Increment(ref _s._dirs);
                string abs = Abs(rel);
                int cls = LinuxDirClass(top.Class, abs, name, top.Name);
                string? cacheRoot = top.CacheRoot;
                if (cls == Cats.Caches && top.Class != Cats.Caches) cacheRoot = abs;
                _stack.Add(new Frame(rel, abs, name, cls, cacheRoot));
                if ((_n & 0xFF) == 0) _s._current = abs;
            }
            else if (type == 'f')
            {
                int cls = LinuxFileClass(top.Class, top.Abs, name);
                Interlocked.Add(ref _s._bytes[cls], size);
                Interlocked.Increment(ref _s._files);
                if (cls == Cats.Caches && top.CacheRoot != null)
                    _cacheBytes[top.CacheRoot] = _cacheBytes.GetValueOrDefault(top.CacheRoot) + size;
            }
            if ((_n & 0x3FFF) == 0) Flush();
        }

        public void Complete() => Flush();

        void Flush()
        {
            foreach (var (k, v) in _cacheBytes) _s._cacheRoots.AddOrUpdate(k, v, (_, old) => old + v);
            _cacheBytes.Clear();
        }
    }
}
