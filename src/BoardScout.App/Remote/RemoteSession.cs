using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BoardScout.Services;

namespace BoardScout.Remote;

/// <summary>A machine that is ready to run BoardScout: which build, where it lives there, and what the machine is.</summary>
internal sealed record PreparedAgent(string Os, string Rid, string Folder, string RemoteName, string Pretty, bool? Admin, string? Note);

/// <summary>
/// An open remote dashboard: one SSH connection that runs BoardScout on the machine (serving only that machine
/// itself) and carries the tunnel from a local port to it. Disposing it ends the connection, and with it both;
/// the remote copy also stops on its own when its SSH session ends or nobody has used it for a few minutes.
/// </summary>
internal sealed class RemoteSession : IDisposable
{
    private readonly Process _ssh;
    private int _ended;

    public RemoteSession(MachineEntry machine, PreparedAgent agent, Process ssh, int localPort, string key, string platform)
    {
        Machine = machine;
        Agent = agent;
        _ssh = ssh;
        LocalPort = localPort;
        Url = $"http://127.0.0.1:{localPort}/?key={key}";
        Platform = platform;
        _ssh.Exited += (_, _) => End("The SSH connection closed, and BoardScout stopped on the machine.");
    }

    public MachineEntry Machine { get; }
    public PreparedAgent Agent { get; }
    public int LocalPort { get; }

    /// <summary>The dashboard's link with its key: http://127.0.0.1:port/?key=…</summary>
    public string Url { get; }

    public string Platform { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>Raised once, when the remote dashboard or its tunnel goes away (with the reason).</summary>
    public event Action<RemoteSession, string>? Ended;

    public bool Alive => _ended == 0;

    private void End(string reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        try { if (!_ssh.HasExited) _ssh.Kill(entireProcessTree: true); } catch { }
        Ended?.Invoke(this, reason);
    }

    public void Dispose()
    {
        End("Disconnected.");
        _ssh.Dispose();
    }
}

/// <summary>The steps behind the Machines page and --connect/--check: probe, copy, run, tunnel.</summary>
internal sealed class RemoteRunner(SshClient ssh, string dataRoot, Action<string> log)
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30);

    public SshClient Ssh => ssh;
    public string DataRoot => dataRoot;

    private Task<SshResult> RunAsync(MachineEntry machine, string command, TimeSpan timeout, CancellationToken token, Stream? input = null, Action<long>? sent = null) =>
        SshTool.RunAsync(ssh, machine, dataRoot, command, timeout, token, input, sent);

    /// <summary>What the machine is (Linux or Windows, which processor), from one harmless echo.</summary>
    public async Task<RemoteHost> ProbeAsync(MachineEntry machine, CancellationToken token)
    {
        var first = await RunAsync(machine, RemoteCommands.Probe, Short, token);
        // 255 is ssh's own failure (no connection, no key, unknown host); anything else came from the machine.
        if (first.ExitCode == 255 || first.TimedOut) throw SshTool.Explain(first, "Connecting");
        var host = RemoteCommands.ParseProbe(first.Output);
        if (host is null)
        {
            var second = await RunAsync(machine, RemoteCommands.ProbePowerShell, Short, token);
            if (second.ExitCode == 255 || second.TimedOut) throw SshTool.Explain(second, "Connecting");
            host = RemoteCommands.ParseProbe(second.Output.Replace("BSP2", "BSP2 ", StringComparison.Ordinal));
        }
        return host ?? throw new RemoteException("unsupported", "The machine answered, but not like Linux or Windows.", SshTool.LastLine(first.Output + "\n" + first.Error));
    }

    /// <summary>
    /// Makes sure this version of BoardScout is on the machine: reads what is there (processor, C library, any
    /// copy and its SHA256), and copies the build over only when it's missing or different.
    /// </summary>
    public async Task<PreparedAgent> PrepareAsync(MachineEntry machine, RemoteHost probe, Action<string, double?> progress, CancellationToken token)
    {
        var version = AppInfo.Version;
        if (probe.Rid is null && probe.Os != "linux") throw new RemoteException("unsupported", probe.Unsupported ?? "No BoardScout build runs on this machine.");
        progress("check", null);
        if (probe.Os == "linux")
        {
            var folder = RemoteCommands.LinuxFolder(machine.RemoteFolder);
            var check = await RunAsync(machine, RemoteCommands.LinuxCheck(folder, version), Short, token);
            if (check.ExitCode == 255 || check.TimedOut) throw SshTool.Explain(check, "Checking the machine");
            var values = RemoteCommands.Values(check.Output);
            if (!values.ContainsKey("ARCH")) throw new RemoteException("other", "Couldn't prepare BoardScout's folder on the machine.", SshTool.LastLine(check.Error));
            var host = RemoteCommands.Linux(values["ARCH"], values.GetValueOrDefault("LIBC") == "musl");
            if (host.Rid is not { } rid) throw new RemoteException("unsupported", host.Unsupported ?? "No BoardScout build runs on this machine.");
            var name = AgentBuilds.RemoteName(rid, version);
            var local = AgentBuilds.Find(rid, version, dataRoot)
                        ?? throw new RemoteException("no-build", $"This machine needs BoardScout's {rid} build, which isn't on this computer.") { Rid = rid };
            var hash = AgentBuilds.Sha256(local.Path);
            if (RemoteCommands.Files(check.Output).GetValueOrDefault(name) != hash)
            {
                var size = new FileInfo(local.Path).Length;
                log($"REMOTE: copying BoardScout {version} ({rid}, {size / 1_048_576d:0} MB, from {local.From}) to {machine.Name}.");
                progress("copy", 0);
                await using var stream = File.OpenRead(local.Path);
                var copied = await RunAsync(machine, RemoteCommands.LinuxReceive(folder, name), TimeSpan.FromMinutes(30), token, stream,
                    sent => progress("copy", Math.Min(1, sent / (double)size)));
                if (copied.ExitCode == 255) throw SshTool.Explain(copied, "Copying BoardScout");
                var arrived = RemoteCommands.Values(copied.Output).GetValueOrDefault("HASH");
                if (!copied.Ok || arrived != hash)
                    throw new RemoteException("other", "The copy on the machine didn't arrive whole. Try again.", SshTool.LastLine(copied.Error) is { Length: > 0 } e ? e : $"expected {hash[..12]}…, got {(arrived is { Length: > 12 } a ? a[..12] + "…" : "nothing")}");
            }
            return new PreparedAgent("linux", rid, folder, name, values.GetValueOrDefault("PRETTY") ?? "Linux", null, host.Note);
        }
        else
        {
            var rid = probe.Rid!;
            var folder = RemoteCommands.WindowsFolder(machine.RemoteFolder);
            var check = await RunAsync(machine, RemoteCommands.WindowsCheck(folder, version), TimeSpan.FromSeconds(60), token);
            if (check.ExitCode == 255 || check.TimedOut) throw SshTool.Explain(check, "Checking the machine");
            var values = RemoteCommands.Values(check.Output);
            if (!values.TryGetValue("FOLDER", out var remoteFolder) || remoteFolder.Length == 0)
                throw new RemoteException("other", "Couldn't prepare BoardScout's folder on the machine.", SshTool.LastLine(check.Error + "\n" + check.Output));
            var name = AgentBuilds.RemoteName(rid, version);
            var local = AgentBuilds.Find(rid, version, dataRoot)
                        ?? throw new RemoteException("no-build", $"This machine needs BoardScout's {rid} build, which isn't on this computer.") { Rid = rid };
            var hash = AgentBuilds.Sha256(local.Path);
            if (RemoteCommands.Files(check.Output).GetValueOrDefault(name) != hash)
            {
                var size = new FileInfo(local.Path).Length;
                log($"REMOTE: copying BoardScout {version} ({rid}, {size / 1_048_576d:0} MB, from {local.From}) to {machine.Name}.");
                progress("copy", null);
                var destination = RemoteCommands.ScpPath(remoteFolder, values.GetValueOrDefault("PROFILE") ?? "", $"agent\\{name}.part");
                var copied = await SshTool.CopyAsync(ssh, machine, dataRoot, local.Path, destination, TimeSpan.FromMinutes(30), token);
                if (!copied.Ok) throw SshTool.Explain(copied, "Copying BoardScout");
                var finished = await RunAsync(machine, RemoteCommands.WindowsFinish(folder, name), TimeSpan.FromSeconds(60), token);
                var arrived = RemoteCommands.Values(finished.Output).GetValueOrDefault("HASH");
                if (arrived != hash)
                    throw new RemoteException("other", "The copy on the machine didn't arrive whole. Try again.", SshTool.LastLine(finished.Error) is { Length: > 0 } e ? e : $"expected {hash[..12]}…");
            }
            var admin = values.TryGetValue("ADMIN", out var a) ? a.Equals("True", StringComparison.OrdinalIgnoreCase) : (bool?)null;
            return new PreparedAgent("windows", rid, folder, name, RemoteCommands.WindowsName(values.GetValueOrDefault("PRETTY") ?? "Windows"), admin, probe.Note);
        }
    }

    private static string Command(PreparedAgent agent, string arguments) => agent.Os == "linux"
        ? RemoteCommands.LinuxRun(agent.Folder, agent.RemoteName, arguments)
        : RemoteCommands.WindowsRun(agent.Folder, agent.RemoteName, arguments);

    private static string Quote(PreparedAgent agent, string text) => agent.Os == "linux" ? RemoteCommands.ShQuote(text) : RemoteCommands.PsQuote(text);

    /// <summary>
    /// Starts BoardScout on the machine as a dashboard serving only that machine, through one SSH connection that
    /// also forwards a free local port to it, and reads the key it prints. A remote port that turns out to be
    /// taken is tried again with another.
    /// </summary>
    public async Task<RemoteSession> OpenAsync(MachineEntry machine, PreparedAgent agent, bool privacy, Action<string, double?> progress, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await StartAsync(machine, agent, privacy, progress, Random.Shared.Next(20000, 61000), token);
            }
            catch (RemoteException ex) when (ex.Kind == "port-busy" && attempt < 4)
            {
                log($"REMOTE: {machine.Name}: that port was taken there; trying another.");
            }
        }
    }

    private async Task<RemoteSession> StartAsync(MachineEntry machine, PreparedAgent agent, bool privacy, Action<string, double?> progress, int remotePort, CancellationToken token)
    {
        progress("start", null);
        var localPort = FreeLocalPort();
        var arguments = $"--agent --port {remotePort} --label {Quote(agent, RemoteCommands.Label(machine.Name))}{(privacy ? " --privacy" : "")}{(agent.Os == "linux" ? " --watch-stdin" : "")}";
        var process = SshTool.StartLong(ssh, machine, dataRoot,
            ["-o", "ExitOnForwardFailure=yes", "-L", $"127.0.0.1:{localPort}:127.0.0.1:{remotePort}"], Command(agent, arguments));
        var errors = new LinkedList<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (errors)
            {
                errors.AddLast(e.Data.Trim());
                while (errors.Count > 20) errors.RemoveFirst();
            }
        };
        process.BeginErrorReadLine();
        string LastError() { lock (errors) return errors.LastOrDefault() ?? ""; }

        // The first start of a build unpacks it (a few seconds; longer for the Windows build); then it reads the hardware.
        AgentAnnouncement? ready = null;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                while (ready is null)
                {
                    var line = await process.StandardOutput.ReadLineAsync(wait.Token);
                    if (line is null) break;
                    if (line.StartsWith(Web.WebServer.AgentReady, StringComparison.Ordinal))
                        ready = JsonSerializer.Deserialize<AgentAnnouncement>(line[Web.WebServer.AgentReady.Length..]);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
            }
            catch (JsonException)
            {
            }
        }
        if (ready is null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            var said = LastError();
            string all;
            lock (errors) all = string.Join('\n', errors);
            if (all.Contains("address already in use", StringComparison.OrdinalIgnoreCase) || all.Contains("Failed to bind", StringComparison.OrdinalIgnoreCase))
                throw new RemoteException("port-busy", "That port is taken on the machine.", said);
            if (process.ExitCode == 255) throw SshTool.Explain(new SshResult(255, "", all, false), "Starting BoardScout");
            if (said.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || said.Contains("noexec", StringComparison.OrdinalIgnoreCase))
                throw new RemoteException("noexec", "The machine won't run programs from BoardScout's folder (it may be mounted noexec). Choose another remote folder for this machine.", said);
            if (said.Contains("GLIBC", StringComparison.Ordinal))
                throw new RemoteException("start-failed", "This Linux is older than .NET 10, which BoardScout is built on, supports.", said);
            throw new RemoteException("start-failed", "BoardScout didn't start on the machine.", said.Length > 0 ? said : $"exit code {process.ExitCode}");
        }
        // Keep reading what it prints (a heartbeat every few seconds), or its output pipe would fill and stall it.
        _ = Task.Run(async () =>
        {
            try { while (await process.StandardOutput.ReadLineAsync() is not null) { } } catch { }
        });

        progress("tunnel", null);
        var session = new RemoteSession(machine, agent, process, localPort, ready.Key, ready.Platform ?? agent.Os);
        if (!await AnswersAsync(localPort, process, token))
        {
            session.Dispose();
            throw new RemoteException("start-failed", "BoardScout started on the machine, but the SSH tunnel to it didn't open.",
                LastError() is { Length: > 0 } e ? e : "Port forwarding may be off on that SSH server (AllowTcpForwarding).");
        }
        log($"REMOTE: {machine.Name} ({agent.Pretty}) is open through an SSH tunnel on 127.0.0.1:{localPort}.");
        return session;
    }

    /// <summary>Runs BoardScout once on the machine and reads its summary (for the table of every machine).</summary>
    public async Task<MachineSummary> SummaryAsync(MachineEntry machine, PreparedAgent agent, bool privacy, CancellationToken token)
    {
        var result = await RunAsync(machine, Command(agent, "--summary-json" + (privacy ? " --privacy" : "")), TimeSpan.FromMinutes(4), token);
        if (result.ExitCode == 255 || result.TimedOut) throw SshTool.Explain(result, "Checking the machine");
        var json = result.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (json is null)
            throw new RemoteException("start-failed", "BoardScout ran on the machine but didn't report back.", SshTool.LastLine(result.Error) is { Length: > 0 } e ? e : $"exit code {result.ExitCode}");
        return JsonSerializer.Deserialize<MachineSummary>(json) ?? throw new RemoteException("other", "The machine's summary couldn't be read.");
    }

    private static async Task<bool> AnswersAsync(int port, Process tunnel, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (var i = 0; i < 40; i++)
        {
            if (tunnel.HasExited) return false;
            try
            {
                var answer = await http.GetStringAsync($"http://127.0.0.1:{port}/api/ping", token);
                if (answer.StartsWith("BoardScout", StringComparison.Ordinal)) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
            await Task.Delay(250, token);
        }
        return false;
    }

    private static int FreeLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record AgentAnnouncement(
        [property: System.Text.Json.Serialization.JsonPropertyName("port")] int Port,
        [property: System.Text.Json.Serialization.JsonPropertyName("key")] string Key,
        [property: System.Text.Json.Serialization.JsonPropertyName("version")] string? Version,
        [property: System.Text.Json.Serialization.JsonPropertyName("platform")] string? Platform);
}
