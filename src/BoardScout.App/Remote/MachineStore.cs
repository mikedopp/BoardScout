using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BoardScout.Remote;

/// <summary>
/// The Machines page's list: machines added there (machines.json in the data folder: names, addresses, key file
/// paths; never a password or a key itself) plus every Host in ~/.ssh/config, so machines SSH already knows show
/// up without being added again.
/// </summary>
internal sealed class MachineStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly Regex Target = new(@"^[A-Za-z0-9._%+-]+(@[A-Za-z0-9._:%\[\]-]+)?$", RegexOptions.Compiled);
    private readonly string _file;
    private readonly object _gate = new();
    private Saved _saved = new();

    public MachineStore(string dataRoot)
    {
        _file = Path.Combine(dataRoot, "machines.json");
        try
        {
            if (File.Exists(_file)) _saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file)) ?? new();
        }
        catch
        {
            _saved = new();
        }
    }

    /// <summary>Which SSH program to use: "auto" or one of <see cref="SshTool.Available"/>'s ids.</summary>
    public string SshClient
    {
        get { lock (_gate) return _saved.SshClient; }
        set { lock (_gate) { _saved.SshClient = value; Save(); } }
    }

    /// <summary>Saved machines first, then ~/.ssh/config hosts that aren't hidden or already saved.</summary>
    public IReadOnlyList<MachineEntry> All()
    {
        lock (_gate)
        {
            var list = new List<MachineEntry>(_saved.Machines);
            foreach (var alias in SshConfigHosts())
            {
                if (_saved.Hidden.Contains(alias) || list.Any(m => m.Target.Equals(alias, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new MachineEntry { Id = "c" + Hash(alias), Name = alias, Target = alias, Source = "ssh-config" });
            }
            return list;
        }
    }

    public MachineEntry? Find(string id) => All().FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// Adds a machine from what was typed: an ssh config alias, host, user@host, or user@host:port. Returns the
    /// problem in plain words when it can't be used.
    /// </summary>
    public (MachineEntry? Machine, string? Problem) Add(string? typed, string? name, string? identity, string? remoteFolder)
    {
        var (target, port, problem) = ParseTarget(typed);
        if (problem is not null) return (null, problem);
        if (!string.IsNullOrWhiteSpace(identity))
        {
            identity = identity.Trim().Trim('"');
            if (identity.StartsWith("~/", StringComparison.Ordinal) || identity.StartsWith("~\\", StringComparison.Ordinal))
                identity = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), identity[2..]);
            if (!File.Exists(identity)) return (null, "That key file isn't there.");
            if (identity.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)) return (null, "Choose the private key (the file without .pub).");
        }
        if (!string.IsNullOrWhiteSpace(remoteFolder) && !RemoteCommands.SafeFolder(remoteFolder.Trim())) return (null, "The remote folder may use letters, digits, spaces, and . _ - / \\ ~ : only.");
        var label = string.IsNullOrWhiteSpace(name) ? target!.Split('@').Last() : name.Trim();
        if (label.Length > 60) label = label[..60];
        var machine = new MachineEntry
        {
            Id = "m" + Hash($"{target}|{port}"),
            Name = label,
            Target = target!,
            Port = port,
            Identity = string.IsNullOrWhiteSpace(identity) ? null : identity,
            RemoteFolder = string.IsNullOrWhiteSpace(remoteFolder) ? null : remoteFolder.Trim(),
            Source = "saved"
        };
        lock (_gate)
        {
            _saved.Machines.RemoveAll(m => m.Id == machine.Id);
            _saved.Machines.Add(machine);
            _saved.Hidden.Remove(machine.Target);
            Save();
        }
        return (machine, null);
    }

    /// <summary>Removes a saved machine, or hides an ~/.ssh/config host from the page (the config isn't touched).</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            var saved = _saved.Machines.FirstOrDefault(m => m.Id == id);
            if (saved is not null) _saved.Machines.Remove(saved);
            else if (All().FirstOrDefault(m => m.Id == id) is { Source: "ssh-config" } host) _saved.Hidden.Add(host.Target);
            Save();
        }
    }

    /// <summary>Shows hidden ~/.ssh/config hosts again.</summary>
    public void Unhide()
    {
        lock (_gate)
        {
            _saved.Hidden.Clear();
            Save();
        }
    }

    public int HiddenCount { get { lock (_gate) return _saved.Hidden.Count; } }

    public static (string? Target, int? Port, string? Problem) ParseTarget(string? typed)
    {
        var text = (typed ?? "").Trim();
        if (text.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase)) text = text[4..].Trim();
        if (text.Length == 0) return (null, null, "Type a host name, user@host, or a Host from your SSH config.");
        if (text.StartsWith('-')) return (null, null, "A machine can't start with a dash.");
        int? port = null;
        var match = Regex.Match(text, @"^(?<target>[^:\s]+):(?<port>\d{1,5})$");
        if (match.Success)
        {
            text = match.Groups["target"].Value;
            port = int.Parse(match.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (port is < 1 or > 65535) return (null, null, "That port number is out of range.");
        }
        if (!Target.IsMatch(text)) return (null, null, "Use letters, digits, and . _ - @ only, like admin@192.168.1.20 or a Host from your SSH config.");
        return (text, port, null);
    }

    /// <summary>Host names from ~/.ssh/config (and files it Includes), leaving out patterns like * and !.</summary>
    public static IReadOnlyList<string> SshConfigHosts()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var hosts = new List<string>();
        Read(Path.Combine(home, ".ssh", "config"), 0);
        return hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        void Read(string path, int depth)
        {
            if (depth > 3 || !File.Exists(path)) return;
            string[] lines;
            try { lines = File.ReadAllLines(path); } catch { return; }
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split((char[])[' ', '\t', '='], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2) continue;
                if (parts[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var name in parts[1].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                        if (!name.Contains('*') && !name.Contains('?') && !name.StartsWith('!') && Target.IsMatch(name)) hosts.Add(name);
                }
                else if (parts[0].Equals("Include", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var pattern in parts[1].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        var full = pattern.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, pattern[2..])
                            : Path.IsPathRooted(pattern) ? pattern : Path.Combine(home, ".ssh", pattern);
                        var folder = Path.GetDirectoryName(full);
                        if (folder is null || !Directory.Exists(folder)) continue;
                        foreach (var file in Directory.GetFiles(folder, Path.GetFileName(full)).Order()) Read(file, depth + 1);
                    }
                }
            }
        }
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..10];

    private void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(_saved, Options));
        }
        catch { }
    }

    private sealed class Saved
    {
        [JsonPropertyName("sshClient")] public string SshClient { get; set; } = "auto";
        [JsonPropertyName("machines")] public List<MachineEntry> Machines { get; set; } = [];
        [JsonPropertyName("hidden")] public HashSet<string> Hidden { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
