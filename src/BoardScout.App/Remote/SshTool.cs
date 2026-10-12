using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace BoardScout.Remote;

internal sealed record SshResult(int ExitCode, string Output, string Error, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// <summary>An SSH program BoardScout can use: Windows' own OpenSSH, Git's, or the system's on Linux.</summary>
internal sealed record SshClient(string Id, string Label, string Ssh, string Scp, string Keyscan, string Keygen)
{
    /// <summary>Git for Windows' ssh is an MSYS2 program; it must not rewrite arguments that look like paths.</summary>
    public bool Msys => Id == "git";
}

/// <summary>Why an SSH step failed, sorted into what the Machines page can explain or offer to fix.</summary>
internal sealed class RemoteException(string kind, string message, string? detail = null) : Exception(message)
{
    /// <summary>hostkey, hostkey-changed, key-permissions, auth, unreachable, no-ssh, unsupported, no-build,
    /// noexec, start-failed, cancelled, other.</summary>
    public string Kind { get; } = kind;

    /// <summary>What SSH or the machine actually said, for the details line.</summary>
    public string? Detail { get; } = detail;

    public string? Fingerprint { get; init; }
    public string? Rid { get; init; }
}

/// <summary>
/// Runs ssh, scp, and ssh-keyscan for the Machines page. BoardScout never asks for or sees a password:
/// BatchMode keeps SSH from prompting, so only keys (from ~/.ssh, the SSH agent, or a key file chosen for a
/// machine) can sign in. Host keys BoardScout was told to trust live in its own known_hosts file, next to the
/// user's; a key that changed for a host the user already knows is still refused.
/// </summary>
internal static class SshTool
{
    private static readonly Regex Fingerprint = new(@"SHA256:[A-Za-z0-9+/=]+", RegexOptions.Compiled);

    /// <summary>The SSH programs found on this computer, best first.</summary>
    public static IReadOnlyList<SshClient> Available()
    {
        var list = new List<SshClient>();
        if (OperatingSystem.IsWindows())
        {
            var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH");
            if (File.Exists(Path.Combine(system, "ssh.exe")))
                list.Add(Client("windows", "Windows OpenSSH", system, ".exe"));
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
            {
                var git = Path.Combine(root, "Git", "usr", "bin");
                if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(git, "ssh.exe")))
                {
                    list.Add(Client("git", "Git for Windows' OpenSSH", git, ".exe"));
                    break;
                }
            }
        }
        else
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(Path.Combine(dir, "ssh")))
                {
                    list.Add(Client("system", "OpenSSH", dir, ""));
                    break;
                }
            }
        }
        return list;
    }

    private static SshClient Client(string id, string label, string dir, string ext) => new(id, label,
        Path.Combine(dir, "ssh" + ext), Path.Combine(dir, "scp" + ext), Path.Combine(dir, "ssh-keyscan" + ext), Path.Combine(dir, "ssh-keygen" + ext));

    /// <summary>The chosen program ("auto" or an id from <see cref="Available"/>), or the best one found.</summary>
    public static SshClient? Resolve(string? choice)
    {
        var available = Available();
        return available.FirstOrDefault(c => c.Id == choice) ?? available.FirstOrDefault();
    }

    /// <summary>For the Dependencies list: which ssh this BoardScout would use.</summary>
    public static string Describe() => Resolve(null) is { } client ? $"{client.Label}: {client.Ssh}" : "No ssh program found";

    // ---- Options ---------------------------------------------------------------------------------------

    /// <summary>BoardScout's own known_hosts: host keys accepted on the Machines page.</summary>
    public static string KnownHostsFile(string dataRoot) => Path.Combine(dataRoot, "known_hosts");

    private static List<string> Options(SshClient client, MachineEntry machine, string dataRoot, bool scp)
    {
        var known = KnownHostsFile(dataRoot).Replace('\\', '/');
        var options = new List<string>
        {
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=10",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3",
            "-o", "LogLevel=ERROR",
            "-o", "StrictHostKeyChecking=yes",
            "-o", $"UserKnownHostsFile=~/.ssh/known_hosts ~/.ssh/known_hosts2 \"{known}\""
        };
        if (machine.Port is { } port) options.AddRange([scp ? "-P" : "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (!string.IsNullOrWhiteSpace(machine.Identity)) options.AddRange(["-i", machine.Identity, "-o", "IdentitiesOnly=yes"]);
        return options;
    }

    private static ProcessStartInfo Start(SshClient client, string program, IEnumerable<string> arguments, bool input)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // No prompts of any kind, even from a graphical askpass helper.
        info.Environment["SSH_ASKPASS_REQUIRE"] = "never";
        info.Environment.Remove("SSH_ASKPASS");
        if (client.Msys)
        {
            info.Environment["MSYS_NO_PATHCONV"] = "1";
            info.Environment["MSYS2_ARG_CONV_EXCL"] = "*";
        }
        _ = input;
        return info;
    }

    // ---- Running ---------------------------------------------------------------------------------------

    /// <summary>Runs one command on the machine and waits for it. Input, when given, is streamed to its stdin.</summary>
    public static Task<SshResult> RunAsync(SshClient client, MachineEntry machine, string dataRoot, string command, TimeSpan timeout,
        CancellationToken token, Stream? input = null, Action<long>? sent = null)
    {
        var arguments = Options(client, machine, dataRoot, scp: false);
        arguments.AddRange(["-T", "--", machine.Target, command]);
        return RunProcessAsync(client, client.Ssh, arguments, timeout, token, input, sent);
    }

    /// <summary>Copies a file to the machine with scp (used for Windows machines, which have SFTP built in).</summary>
    public static Task<SshResult> CopyAsync(SshClient client, MachineEntry machine, string dataRoot, string localFile, string remotePath,
        TimeSpan timeout, CancellationToken token)
    {
        var arguments = Options(client, machine, dataRoot, scp: true);
        arguments.AddRange(["-q", "-B", "--", localFile, $"{machine.Target}:{remotePath}"]);
        return RunProcessAsync(client, client.Scp, arguments, timeout, token, null, null);
    }

    /// <summary>Starts ssh without waiting: a long-running command, or (command null, with -N) a tunnel.</summary>
    public static Process StartLong(SshClient client, MachineEntry machine, string dataRoot, IEnumerable<string> extra, string? command)
    {
        var arguments = Options(client, machine, dataRoot, scp: false);
        arguments.AddRange(extra);
        arguments.Add("-T");
        arguments.Add("--");
        arguments.Add(machine.Target);
        if (command is not null) arguments.Add(command);
        var process = new Process { StartInfo = Start(client, client.Ssh, arguments, input: true), EnableRaisingEvents = true };
        if (!process.Start()) throw new RemoteException("no-ssh", $"{client.Ssh} could not be started.");
        ChildProcesses.Tie(process);
        return process;
    }

    private static async Task<SshResult> RunProcessAsync(SshClient client, string program, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken token, Stream? input, Action<long>? sent)
    {
        if (!File.Exists(program)) throw new RemoteException("no-ssh", $"{Path.GetFileName(program)} isn't installed here ({program}).");
        using var process = new Process { StartInfo = Start(client, program, arguments, input is not null) };
        process.Start();
        ChildProcesses.Tie(process);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        var timedOut = false;
        try
        {
            if (input is not null)
            {
                var buffer = new byte[256 * 1024];
                long total = 0;
                int read;
                var stdin = process.StandardInput.BaseStream;
                while ((read = await input.ReadAsync(buffer, limit.Token)) > 0)
                {
                    await stdin.WriteAsync(buffer.AsMemory(0, read), limit.Token);
                    total += read;
                    sent?.Invoke(total);
                }
                await stdin.FlushAsync(limit.Token);
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(limit.Token);
            return new SshResult(process.ExitCode, await output, await error, false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !token.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { }
            if (!timedOut) throw;
        }
        catch (IOException)
        {
            // ssh exited while input was still being written (it failed to connect, or the far side stopped reading).
            await process.WaitForExitAsync(CancellationToken.None);
            return new SshResult(process.ExitCode, await Safe(output), await Safe(error), false);
        }
        return new SshResult(-1, await Safe(output), await Safe(error), timedOut);
    }

    private static async Task<string> Safe(Task<string> text)
    {
        try { return await text; } catch { return ""; }
    }

    // ---- Host keys ---------------------------------------------------------------------------------------

    /// <summary>Where ssh would really connect for this machine (its config resolved): host, port, user.</summary>
    public static async Task<(string Host, int Port, string User)> ResolveAsync(SshClient client, MachineEntry machine, CancellationToken token)
    {
        var arguments = new List<string> { "-G" };
        if (machine.Port is { } port) arguments.AddRange(["-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        arguments.AddRange(["--", machine.Target]);
        var result = await RunProcessAsync(client, client.Ssh, arguments, TimeSpan.FromSeconds(10), token, null, null);
        string Value(string key) => result.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..].Trim() ?? "";
        var host = Value("hostname");
        return (host.Length > 0 ? host : machine.Target.Split('@').Last(), int.TryParse(Value("port"), out var p) ? p : 22, Value("user"));
    }

    /// <summary>
    /// The machine's host keys, read without signing in (ssh-keyscan), with their SHA256 fingerprints for the
    /// person to compare before trusting them.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Lines, IReadOnlyList<string> Fingerprints)> ScanHostKeysAsync(SshClient client, MachineEntry machine, CancellationToken token)
    {
        var (host, port, _) = await ResolveAsync(client, machine, token);
        var scan = await RunProcessAsync(client, client.Keyscan, ["-T", "6", "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--", host],
            TimeSpan.FromSeconds(20), token, null, null);
        var lines = scan.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (lines.Count == 0) throw new RemoteException("unreachable", $"Couldn't read {host}'s host key.", LastLine(scan.Error));
        var fingerprints = new List<string>();
        var temp = Path.Combine(Path.GetTempPath(), $"boardscout-hostkeys-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllLinesAsync(temp, lines, token);
            var listed = await RunProcessAsync(client, client.Keygen, ["-l", "-f", temp], TimeSpan.FromSeconds(10), token, null, null);
            foreach (var line in listed.Output.Split('\n'))
            {
                var match = Fingerprint.Match(line);
                if (!match.Success) continue;
                var type = Regex.Match(line, @"\(([A-Z0-9-]+)\)\s*$").Groups[1].Value;
                fingerprints.Add(type.Length > 0 ? $"{type} {match.Value}" : match.Value);
            }
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
        return (lines, fingerprints.Distinct().ToList());
    }

    /// <summary>Adds host keys the person accepted to BoardScout's own known_hosts.</summary>
    public static void Trust(string dataRoot, IEnumerable<string> lines)
    {
        var file = KnownHostsFile(dataRoot);
        var existing = File.Exists(file) ? File.ReadAllLines(file).ToHashSet(StringComparer.Ordinal) : [];
        var add = lines.Where(l => !existing.Contains(l)).ToList();
        if (add.Count > 0) File.AppendAllLines(file, add);
    }

    // ---- Explaining failures --------------------------------------------------------------------------

    /// <summary>Turns ssh's complaint into something the Machines page can say or offer to fix.</summary>
    public static RemoteException Explain(SshResult result, string what)
    {
        var error = result.Error;
        if (result.TimedOut) return new RemoteException("unreachable", $"{what} took too long and was stopped.", LastLine(error));
        if (error.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Host key for", StringComparison.OrdinalIgnoreCase) && error.Contains("has changed", StringComparison.OrdinalIgnoreCase))
            return new RemoteException("hostkey-changed",
                "This machine's host key is different from the one SSH remembers. That happens after a reinstall, or when something is in the way. BoardScout won't connect until you check it and remove the old key from known_hosts.",
                LastLine(error));
        if (error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) || error.Contains("No ", StringComparison.Ordinal) && error.Contains("host key is known", StringComparison.OrdinalIgnoreCase))
            return new RemoteException("hostkey", "SSH hasn't seen this machine before. Compare its fingerprint with the machine's own, then trust it.", LastLine(error));
        if (error.Contains("bad permissions", StringComparison.OrdinalIgnoreCase) || error.Contains("UNPROTECTED PRIVATE KEY FILE", StringComparison.OrdinalIgnoreCase))
            return new RemoteException("key-permissions", OperatingSystem.IsWindows()
                    ? "Windows' SSH ignores a key file that other accounts can read. BoardScout can limit the file to you, SYSTEM, and Administrators, or you can choose Git's SSH, which doesn't check."
                    : "SSH ignores a key file that other users can read. Run chmod 600 on it.",
                RefusedKey(error) ?? LastLine(error));
        if (error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || error.Contains("Too many authentication failures", StringComparison.OrdinalIgnoreCase))
            return new RemoteException("auth",
                "The machine didn't accept any of your SSH keys. BoardScout signs in with keys only, never passwords: add your public key to that account's authorized_keys, or choose the key file for this machine.",
                LastLine(error));
        if (Regex.IsMatch(error, "Could not resolve hostname|Name or service not known|No such host|nodename nor servname", RegexOptions.IgnoreCase))
            return new RemoteException("unreachable", "SSH couldn't find that machine's name on the network.", LastLine(error));
        if (Regex.IsMatch(error, "Connection refused|timed out|No route to host|Network is unreachable|Connection reset|Connection closed", RegexOptions.IgnoreCase))
            return new RemoteException("unreachable", "The machine didn't answer on its SSH port. Is it on, and is its SSH server running?", LastLine(error));
        var last = LastLine(error);
        return new RemoteException("other", $"{what} failed{(result.ExitCode > 0 ? $" (exit {result.ExitCode})" : "")}.", last.Length > 0 ? last : LastLine(result.Output));
    }

    /// <summary>The key file SSH refused: Load key "C:\\Users\\me\\.ssh\\id": bad permissions (Windows doubles the backslashes).</summary>
    public static string? RefusedKey(string error)
    {
        var match = Regex.Match(error, @"Load key ""(?<path>[^""]+)""");
        return match.Success ? match.Groups["path"].Value.Replace(@"\\", @"\") : null;
    }

    public static string LastLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("BOARDSCOUT-", StringComparison.Ordinal)) ?? "";

    /// <summary>
    /// Limits a private key file to its owner, SYSTEM, and Administrators, which Windows' OpenSSH requires
    /// (it refuses keys that other accounts, even deleted ones, can still read). Only on the person's request.
    /// </summary>
    public static (bool Ok, string Message) FixKeyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows()) return (false, "Run chmod 600 on the key file.");
        if (!File.Exists(path)) return (false, "That key file isn't there any more.");
        var info = new ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { path, "/inheritance:r", "/grant:r", $"{Environment.UserDomainName}\\{Environment.UserName}:F", "/grant:r", "*S-1-5-18:F", "/grant:r", "*S-1-5-32-544:F" })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return process.ExitCode == 0
            ? (true, "The key file now opens only for you, SYSTEM, and Administrators.")
            : (false, "Windows refused the change: " + LastLine(output));
    }
}
