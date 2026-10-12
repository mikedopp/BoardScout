using System.Text.Json;

namespace BoardScout.Remote;

/// <summary>
/// The Machines page from a terminal: --machines lists them, --check NAME (or all) prints each one's summary,
/// --connect NAME opens its dashboard through an SSH tunnel until Ctrl+C. Progress goes to stderr, results to
/// stdout. NAME is a machine from the list, or any SSH destination (user@host, a Host from ~/.ssh/config).
/// </summary>
internal static class RemoteCli
{
    public static async Task<int> RunAsync(string command, string? name, string dataRoot, bool privacy, bool trust, string? key, Action<string>? openBrowser)
    {
        var store = new MachineStore(dataRoot);
        if (command == "machines")
        {
            var all = store.All();
            if (all.Count == 0) Console.WriteLine("No machines yet. Add one on the Machines page, or a Host to ~/.ssh/config.");
            foreach (var m in all)
                Console.WriteLine($"{m.Name,-24} {m.Target}{(m.Port is { } port ? $":{port}" : "")}{(m.Source == "ssh-config" ? "  (from ~/.ssh/config)" : "")}");
            return 0;
        }

        var client = SshTool.Resolve(store.SshClient);
        if (client is null)
        {
            Console.Error.WriteLine("No ssh program was found. Install OpenSSH's client first.");
            return 2;
        }
        List<MachineEntry> machines;
        if (command == "check" && name is "all")
        {
            machines = store.All().ToList();
        }
        else
        {
            var found = store.All().FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || m.Target.Equals(name, StringComparison.OrdinalIgnoreCase) || m.Id == name);
            if (found is null)
            {
                var (target, port, problem) = MachineStore.ParseTarget(name);
                if (problem is not null)
                {
                    Console.Error.WriteLine(problem);
                    return 2;
                }
                found = new MachineEntry { Id = "cli", Name = target!, Target = target!, Port = port };
            }
            if (key is not null) found = found with { Identity = key };
            machines = [found];
        }

        var runner = new RemoteRunner(client, dataRoot, line => Console.Error.WriteLine(line));
        var exit = 0;
        var results = new List<object>();
        foreach (var machine in machines)
        {
            Console.Error.WriteLine($"{machine.Name}: connecting over SSH ({client.Label})…");
            try
            {
                var agent = await PrepareAsync(runner, machine, trust);
                if (command == "check")
                {
                    var summary = await runner.SummaryAsync(machine, agent, privacy, CancellationToken.None);
                    results.Add(new { machine = machine.Name, summary });
                }
                else
                {
                    using var session = await runner.OpenAsync(machine, agent, privacy, Stage, CancellationToken.None);
                    Console.WriteLine($"{machine.Name} ({agent.Pretty}) is open through an SSH tunnel:");
                    Console.WriteLine(session.Url);
                    Console.WriteLine("Only this computer can open it. Ctrl+C disconnects.");
                    openBrowser?.Invoke(session.Url);
                    var ended = new TaskCompletionSource<string>();
                    session.Ended += (_, reason) => ended.TrySetResult(reason);
                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        ended.TrySetResult("Disconnected.");
                    };
                    Console.Error.WriteLine(await ended.Task);
                }
            }
            catch (RemoteException ex)
            {
                exit = 1;
                Console.Error.WriteLine($"{machine.Name}: {ex.Message}{(ex.Detail is { Length: > 0 } d ? $"\n  ({d})" : "")}");
                if (ex.Kind == "no-build" && ex.Rid is { } rid)
                    Console.Error.WriteLine($"  Put {AgentBuilds.FileName(rid, Services.AppInfo.Version)} from the release next to BoardScout, or get it on the Machines page.");
                results.Add(new { machine = machine.Name, error = ex.Message, kind = ex.Kind });
            }
        }
        if (command == "check")
            Console.WriteLine(JsonSerializer.Serialize(results.Count == 1 && name is not "all" ? results[0] : results, new JsonSerializerOptions(MachineSummaries.Ascii) { WriteIndented = true }));
        return exit;

        static void Stage(string stage, double? percent)
        {
            var text = stage switch
            {
                "check" => "checking what's there",
                "copy" => percent is { } p ? $"copying BoardScout… {p:P0}" : "copying BoardScout…",
                "start" => "starting BoardScout there",
                "tunnel" => "opening the SSH tunnel",
                _ => stage
            };
            if (stage != "copy" || percent is null or 0 or >= 1 || Math.Round(percent.Value * 100) % 10 == 0) Console.Error.WriteLine("  " + text);
        }
    }

    private static async Task<PreparedAgent> PrepareAsync(RemoteRunner runner, MachineEntry machine, bool trust)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var host = await runner.ProbeAsync(machine, CancellationToken.None);
                return await runner.PrepareAsync(machine, host, (_, _) => { }, CancellationToken.None);
            }
            catch (RemoteException ex) when (ex.Kind == "hostkey" && attempt == 0)
            {
                var (lines, fingerprints) = await SshTool.ScanHostKeysAsync(runner.Ssh, machine, CancellationToken.None);
                Console.Error.WriteLine($"{machine.Name} isn't known to SSH yet. Its host key fingerprints:");
                foreach (var fingerprint in fingerprints) Console.Error.WriteLine("  " + fingerprint);
                if (!trust)
                {
                    Console.Error.WriteLine("Compare them with the machine's own (ssh-keygen -lf on its /etc/ssh/ssh_host_*_key.pub files),");
                    Console.Error.WriteLine("then run this again with --trust to trust them.");
                    throw new RemoteException("hostkey", "Not trusted yet.");
                }
                SshTool.Trust(runner.DataRoot, lines);
                Console.Error.WriteLine("Trusted (kept in BoardScout's own known_hosts).");
            }
        }
    }
}
