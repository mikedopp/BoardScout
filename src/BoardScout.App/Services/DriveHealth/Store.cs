using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BoardScout.DriveHealth;

/// <summary>
/// The log on disk. Append-only text, so a crash costs at most one line and anything (another app, a spreadsheet) can read it:
///   drives\&lt;key&gt;.jsonl          one reading per line, oldest first
///   volumes.jsonl                 how full each volume was, one line per volume per pass
///   raw\&lt;key&gt;\&lt;yyyy-MM&gt;.jsonl.gz  smartctl's whole reply, for questions nobody has thought of yet
///   devices.json                  which smartctl -d type answered for each drive
///   latest.json                   the newest reading, findings and volumes, for other apps
/// </summary>
public sealed class Store
{
    public string Folder { get; }
    public string DrivesFolder => Path.Combine(Folder, "drives");
    public string RawFolder => Path.Combine(Folder, "raw");
    public string VolumesPath => Path.Combine(Folder, "volumes.jsonl");
    public string DevicesPath => Path.Combine(Folder, "devices.json");
    public string LatestPath => Path.Combine(Folder, "latest.json");
    public string LockPath => Path.Combine(Folder, "record.lock");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    static readonly JsonSerializerOptions Pretty = new(Json) { WriteIndented = true };

    public Store(string folder) { Folder = folder; }

    // ---- keys

    public static bool UsableSerial(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        return s.Distinct().Count() > 2 && s != "0123456789ABCDEF" && !Regex.IsMatch(s, "^0+$");
    }

    /// <summary>A drive is known by its model and serial, never by its letter or disk number: those move when USB drives are replugged.</summary>
    public static string KeyFor(string? model, string? serial, long? capacity)
    {
        string raw = UsableSerial(serial) ? $"{model?.Trim()}_{serial!.Trim()}" : $"{model?.Trim()}_{capacity}";
        string key = Regex.Replace(raw.Trim(), @"[^A-Za-z0-9._-]+", "_").Trim('_');
        return key.Length > 90 ? key[..90] : key;
    }

    // ---- readings

    public void Append(DriveReading r)
    {
        Directory.CreateDirectory(DrivesFolder);
        AppendLine(Path.Combine(DrivesFolder, r.Key + ".jsonl"), JsonSerializer.Serialize(r, Json));
    }

    public List<DriveReading> Load(string key) => ReadLines<DriveReading>(Path.Combine(DrivesFolder, key + ".jsonl"));

    public Dictionary<string, List<DriveReading>> LoadAll()
    {
        var all = new Dictionary<string, List<DriveReading>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(DrivesFolder)) return all;
        foreach (var file in Directory.EnumerateFiles(DrivesFolder, "*.jsonl"))
        {
            var list = ReadLines<DriveReading>(file);
            if (list.Count > 0) all[Path.GetFileNameWithoutExtension(file)] = list.OrderBy(x => x.At).ToList();
        }
        return all;
    }

    public void AppendVolumes(IEnumerable<VolumeReading> volumes)
    {
        Directory.CreateDirectory(Folder);
        var sb = new StringBuilder();
        foreach (var v in volumes) sb.Append(JsonSerializer.Serialize(v, Json)).Append('\n');
        if (sb.Length > 0) AppendLine(VolumesPath, sb.ToString().TrimEnd('\n'));
    }

    public List<VolumeReading> LoadVolumes() => ReadLines<VolumeReading>(VolumesPath).OrderBy(v => v.At).ToList();

    /// <summary>Each call adds one gzip member; gzip readers (and .NET's GZipStream) read the members back to back.</summary>
    public void AppendRaw(string key, DateTimeOffset at, string json)
    {
        try
        {
            string dir = Path.Combine(RawFolder, key);
            Directory.CreateDirectory(dir);
            string line;
            using (var doc = JsonDocument.Parse(json)) line = "{\"at\":" + JsonSerializer.Serialize(at, Json) + ",\"smartctl\":" + JsonSerializer.Serialize(doc.RootElement) + "}\n";
            using var fs = new FileStream(Path.Combine(dir, at.ToString("yyyy-MM") + ".jsonl.gz"), FileMode.Append, FileAccess.Write, FileShare.Read);
            using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
            var bytes = Encoding.UTF8.GetBytes(line);
            gz.Write(bytes, 0, bytes.Length);
        }
        catch (JsonException) { /* not JSON: nothing worth keeping */ }
    }

    public IEnumerable<string> ReadRaw(string key, string month)
    {
        string path = Path.Combine(RawFolder, key, month + ".jsonl.gz");
        if (!File.Exists(path)) yield break;
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null) if (line.Length > 0) yield return line;
    }

    // ---- which -d type answered for each drive

    public sealed class DeviceNote
    {
        public string? Type { get; set; }
        public string? WindowsSerial { get; set; }
        public int? DiskNumber { get; set; }
        public DateTimeOffset? ProbedAt { get; set; }
        public bool ProbeFailed { get; set; }
        public DateTimeOffset? LastFullReading { get; set; }
        /// <summary>Size in bytes: with the disk number, it links a drive whose USB adapter reports a junk serial.</summary>
        public long? Capacity { get; set; }
    }

    public Dictionary<string, DeviceNote> LoadDevices()
    {
        try
        {
            if (File.Exists(DevicesPath))
                return JsonSerializer.Deserialize<Dictionary<string, DeviceNote>>(File.ReadAllText(DevicesPath), Json) ?? new();
        }
        catch { /* rebuilt on the next pass */ }
        return new();
    }

    public void SaveDevices(Dictionary<string, DeviceNote> devices) => WriteAtomic(DevicesPath, JsonSerializer.Serialize(devices, Pretty));

    public void WriteLatest(object latest) => WriteAtomic(LatestPath, JsonSerializer.Serialize(latest, Pretty));

    // ---- one writer at a time (the app and the scheduled task can both record)

    public FileStream? TryLock()
    {
        Directory.CreateDirectory(Folder);
        try { return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ---- file helpers

    static void AppendLine(string path, string text)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(true);
    }

    static List<T> ReadLines<T>(string path)
    {
        var list = new List<T>();
        if (!File.Exists(path)) return list;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<T>(line, Json) is T item) list.Add(item); }
            catch (JsonException) { /* skip a damaged line rather than lose the history */ }
        }
        return list;
    }

    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(tmp, path, true); return; }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(150 * attempt); }
        }
    }
}
