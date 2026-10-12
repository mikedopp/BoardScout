using System.Text.Json;
using BoardScout.Services;

namespace BoardScout.Remote;

/// <summary>
/// The Machines page's engine, shared by the browser dashboard and the Windows app: the list of machines, what
/// each is doing (checking, copying BoardScout, open), the problems SSH ran into and the fixes on offer, and the
/// open remote dashboards. It talks to the page with JSON messages, like the other views.
/// </summary>
internal sealed class MachinesController : IDisposable
{
    private readonly string _dataRoot;
    private readonly Action<string> _send;
    private readonly Action<string> _log;
    private readonly MachineStore _store;
    private readonly Dictionary<string, State> _states = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _checks = new(3, 3);
    private (string Rid, double? Percent)? _download;
    private string? _downloadError;
    private bool _disposed;

    public MachinesController(string dataRoot, Action<string> send, Action<string> log)
    {
        _dataRoot = dataRoot;
        _send = send;
        _log = log;
        _store = new MachineStore(dataRoot);
    }

    /// <summary>
    /// Opens a remote dashboard in a window of the app's own (the Windows app); returns false to leave it to the
    /// page, which opens it in a browser tab.
    /// </summary>
    public Func<RemoteSession, bool>? OpenNative { get; set; }

    /// <summary>Raised when a remote dashboard closes, so the window showing it can close too.</summary>
    public event Action<RemoteSession>? SessionEnded;

    private sealed class State
    {
        public string Status = "idle";
        public string? Stage;
        public double? Percent;
        public RemoteException? Problem;
        public string? LastAction;
        public IReadOnlyList<string>? HostKeyLines;
        public IReadOnlyList<string>? Fingerprints;
        public PreparedAgent? Agent;
        public MachineSummary? Summary;
        public RemoteSession? Session;
        public CancellationTokenSource? Work;
    }

    private State StateOf(string id)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(id, out var state)) _states[id] = state = new State();
            return state;
        }
    }

    // ---- Messages from the page -------------------------------------------------------------------------

    public async Task HandleAsync(JsonElement message)
    {
        string? Text(string name) => message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var id = Text("id");
        switch (Text("type"))
        {
            case "ready":
            case "refresh":
                SendState();
                break;
            case "add":
                var (machine, problem) = _store.Add(Text("target"), Text("name"), Text("identity"), Text("folder"));
                _send(JsonSerializer.Serialize(new { type = "added", ok = machine is not null, id = machine?.Id, message = problem }));
                if (machine is not null) _log($"MACHINES: added {machine.Name}.");
                SendState();
                break;
            case "remove" when id is not null:
                Disconnect(id);
                _store.Remove(id);
                lock (_gate) _states.Remove(id);
                SendState();
                break;
            case "unhide":
                _store.Unhide();
                SendState();
                break;
            case "open" when id is not null:
                await OpenAsync(id);
                break;
            case "show" when id is not null:
                Show(id);
                break;
            case "check" when id is not null:
                await CheckAsync(id);
                break;
            case "checkall":
                await Task.WhenAll(_store.All().Select(m => CheckAsync(m.Id)));
                break;
            case "disconnect" when id is not null:
                Disconnect(id);
                SendState();
                break;
            case "cancel" when id is not null:
                StateOf(id).Work?.Cancel();
                break;
            case "trust" when id is not null:
                await TrustAsync(id);
                break;
            case "fixkey" when id is not null:
                await FixKeyAsync(id);
                break;
            case "sshclient":
                _store.SshClient = Text("client") ?? "auto";
                _log($"MACHINES: using {SshTool.Resolve(_store.SshClient)?.Label ?? "no SSH program"}.");
                SendState();
                // Chosen as the fix for a machine's problem: try that machine again with it.
                if (Text("retry") is { } retry) await Retry(retry, StateOf(retry).LastAction);
                break;
            case "download" when Text("rid") is { } rid:
                await DownloadAsync(rid);
                break;
        }
    }

    // ---- Opening and checking ----------------------------------------------------------------------------

    private RemoteRunner Runner()
    {
        var client = SshTool.Resolve(_store.SshClient) ?? throw new RemoteException("no-ssh", OperatingSystem.IsWindows()
            ? "No SSH program was found. Add OpenSSH Client in Windows Settings > System > Optional features, or install Git for Windows."
            : "No ssh program was found. Install your distribution's openssh-client package.");
        return new RemoteRunner(client, _dataRoot, _log);
    }

    private async Task OpenAsync(string id)
    {
        if (_store.Find(id) is not { } machine) return;
        var state = StateOf(id);
        if (state.Session is { Alive: true })
        {
            Show(id);
            return;
        }
        await RunAsync(machine, state, "open", async (runner, token) =>
        {
            var host = await runner.ProbeAsync(machine, token);
            var agent = state.Agent = await runner.PrepareAsync(machine, host, Progress(state), token);
            var session = await runner.OpenAsync(machine, agent, AppSettings.Current.PrivacyMode, Progress(state), token);
            session.Ended += (ended, reason) =>
            {
                lock (_gate)
                {
                    if (state.Session != ended) return;
                    state.Session = null;
                    if (state.Status == "open") state.Status = "idle";
                }
                _log($"REMOTE: {machine.Name}: {reason}");
                SessionEnded?.Invoke(ended);
                if (!_disposed) SendState();
            };
            lock (_gate)
            {
                state.Session = session;
                state.Status = "open";
            }
            SendState();
            Show(id);
        });
    }

    private void Show(string id)
    {
        var session = StateOf(id).Session;
        if (session is not { Alive: true }) return;
        if (OpenNative?.Invoke(session) == true) return;
        _send(JsonSerializer.Serialize(new { type = "open", id, url = session.Url }));
    }

    private async Task CheckAsync(string id)
    {
        if (_store.Find(id) is not { } machine) return;
        var state = StateOf(id);
        await _checks.WaitAsync();
        try
        {
            await RunAsync(machine, state, "check", async (runner, token) =>
            {
                var host = await runner.ProbeAsync(machine, token);
                var agent = state.Agent = await runner.PrepareAsync(machine, host, Progress(state), token);
                SetStage(state, "summary", null);
                var summary = await runner.SummaryAsync(machine, agent, AppSettings.Current.PrivacyMode, token);
                // When it was checked, by this computer's clock (the machine's may be in another time zone).
                summary = summary with { CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) };
                lock (_gate)
                {
                    state.Summary = summary;
                    state.Status = state.Session is { Alive: true } ? "open" : "ok";
                }
                _log($"REMOTE: checked {machine.Name}: {summary.Os}, {summary.Fixes.Count} things to fix, {summary.Checks.Count} checks not passing.");
            });
        }
        finally
        {
            _checks.Release();
        }
    }

    private async Task RunAsync(MachineEntry machine, State state, string action, Func<RemoteRunner, CancellationToken, Task> work)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (state.Status == "working") return;
            state.Work?.Dispose();
            cts = state.Work = new CancellationTokenSource();
            state.Status = "working";
            state.Stage = "probe";
            state.Percent = null;
            state.Problem = null;
            state.Fingerprints = null;
            state.HostKeyLines = null;
            state.LastAction = action;
        }
        SendState();
        try
        {
            await work(Runner(), cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            lock (_gate) state.Status = state.Session is { Alive: true } ? "open" : "idle";
        }
        catch (RemoteException ex)
        {
            await ProblemAsync(machine, state, ex);
        }
        catch (Exception ex)
        {
            await ProblemAsync(machine, state, new RemoteException("other", ex.Message));
        }
        finally
        {
            lock (_gate)
            {
                if (state.Status == "working") state.Status = state.Session is { Alive: true } ? "open" : state.Summary is not null ? "ok" : "idle";
                state.Stage = null;
                state.Percent = null;
            }
            if (!_disposed) SendState();
        }
    }

    private async Task ProblemAsync(MachineEntry machine, State state, RemoteException problem)
    {
        _log($"REMOTE: {machine.Name}: {problem.Message}{(problem.Detail is { Length: > 0 } d ? $" ({d})" : "")}");
        IReadOnlyList<string>? lines = null, fingerprints = null;
        if (problem.Kind == "hostkey")
        {
            // Show the keys the machine offers now, so they can be compared before they're trusted.
            try
            {
                var client = SshTool.Resolve(_store.SshClient);
                if (client is not null) (lines, fingerprints) = await SshTool.ScanHostKeysAsync(client, machine, CancellationToken.None);
            }
            catch (RemoteException scan)
            {
                problem = new RemoteException("unreachable", scan.Message, scan.Detail);
            }
        }
        lock (_gate)
        {
            state.Status = "error";
            state.Problem = problem;
            state.HostKeyLines = lines;
            state.Fingerprints = fingerprints;
        }
    }

    private Action<string, double?> Progress(State state) => (stage, percent) => SetStage(state, stage, percent);

    private void SetStage(State state, string stage, double? percent)
    {
        lock (_gate)
        {
            // Copy progress arrives for every quarter megabyte; the page only needs whole percents.
            var whole = percent is { } p ? Math.Floor(p * 100) : (double?)null;
            if (state.Stage == stage && state.Percent == whole) return;
            state.Stage = stage;
            state.Percent = whole;
        }
        SendState();
    }

    private void Disconnect(string id)
    {
        RemoteSession? session;
        lock (_gate)
        {
            var state = StateOf(id);
            session = state.Session;
            state.Session = null;
            if (state.Status == "open") state.Status = state.Summary is not null ? "ok" : "idle";
        }
        session?.Dispose();
    }

    /// <summary>Closes the remote dashboard shown in a window that the person closed.</summary>
    public void Closed(RemoteSession session)
    {
        string? id;
        lock (_gate) id = _states.FirstOrDefault(s => s.Value.Session == session).Key;
        if (id is null) return;
        Disconnect(id);
        SendState();
    }

    // ---- Fixes on offer ------------------------------------------------------------------------------------

    private async Task TrustAsync(string id)
    {
        var state = StateOf(id);
        IReadOnlyList<string>? lines;
        string? action;
        lock (_gate)
        {
            lines = state.HostKeyLines;
            action = state.LastAction;
        }
        if (lines is null || lines.Count == 0) return;
        SshTool.Trust(_dataRoot, lines);
        _log($"MACHINES: trusted the host key of {_store.Find(id)?.Name} ({string.Join(", ", state.Fingerprints ?? [])}), at your request.");
        await Retry(id, action);
    }

    private async Task FixKeyAsync(string id)
    {
        var state = StateOf(id);
        var machine = _store.Find(id);
        var path = machine?.Identity ?? state.Problem?.Detail?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            lock (_gate) state.Problem = new RemoteException("key-permissions", "BoardScout couldn't tell which key file SSH refused. Choose the key file for this machine.", state.Problem?.Detail);
            SendState();
            return;
        }
        var (ok, message) = SshTool.FixKeyPermissions(path);
        _log($"MACHINES: {(ok ? "limited" : "couldn't limit")} {path} to your account, SYSTEM, and Administrators, at your request. {message}");
        if (ok) await Retry(id, state.LastAction);
        else
        {
            lock (_gate) state.Problem = new RemoteException("key-permissions", message, path);
            SendState();
        }
    }

    private Task Retry(string id, string? action) => action == "check" ? CheckAsync(id) : OpenAsync(id);

    private async Task DownloadAsync(string rid)
    {
        if (!AgentBuilds.Rids.Contains(rid)) return;
        lock (_gate)
        {
            if (_download is not null) return;
            _download = (rid, null);
            _downloadError = null;
        }
        SendState();
        var name = AgentBuilds.FileName(rid, AppInfo.Version);
        _log($"MACHINES: downloading {name} from {AgentBuilds.ReleaseHost}, at your request.");
        var lastPercent = -1d;
        try
        {
            await AgentBuilds.DownloadAsync(rid, AppInfo.Version, _dataRoot, (done, total) =>
            {
                var percent = total is > 0 ? Math.Floor(done * 100d / total.Value) : (double?)null;
                if (percent == lastPercent) return;
                lastPercent = percent ?? -1;
                lock (_gate) _download = (rid, percent);
                SendState();
            }, CancellationToken.None);
            _log($"MACHINES: {name} downloaded and matched the release's checksum.");
        }
        catch (Exception ex)
        {
            _downloadError = ex.Message;
            _log("MACHINES: " + ex.Message);
        }
        finally
        {
            lock (_gate) _download = null;
            SendState();
        }
        // Carry on with whatever was waiting for this build.
        List<(string Id, string? Action)> waiting;
        lock (_gate) waiting = _states.Where(s => s.Value.Problem is { Kind: "no-build" } p && p.Rid == rid).Select(s => (s.Key, s.Value.LastAction)).ToList();
        if (_downloadError is null) await Task.WhenAll(waiting.Select(w => Retry(w.Id, w.Action)));
    }

    // ---- What the page shows ------------------------------------------------------------------------------

    public void SendState()
    {
        if (_disposed) return;
        var privacy = AppSettings.Current.PrivacyMode;
        var clients = SshTool.Available();
        var current = SshTool.Resolve(_store.SshClient);
        var machines = _store.All();
        object payload;
        lock (_gate)
        {
            var index = 0;
            payload = new
            {
                type = "machines",
                privacy,
                version = AppInfo.Version,
                platform = OperatingSystem.IsWindows() ? "windows" : "linux",
                ssh = new
                {
                    choice = _store.SshClient,
                    current = current is null ? null : new { id = current.Id, label = current.Label, path = privacy ? Privacy.Scrub(current.Ssh) : current.Ssh },
                    options = clients.Select(c => new { id = c.Id, label = c.Label, path = privacy ? Privacy.Scrub(c.Ssh) : c.Ssh })
                },
                download = _download is { } d ? new { rid = d.Rid, percent = d.Percent } : null,
                downloadError = _downloadError,
                hidden = _store.HiddenCount,
                machines = machines.Select(m =>
                {
                    index++;
                    _states.TryGetValue(m.Id, out var s);
                    var p = s?.Problem;
                    return new
                    {
                        id = m.Id,
                        name = privacy ? $"Machine {index}" : m.Name,
                        target = privacy ? "hidden" : m.Target + (m.Port is { } port ? $":{port}" : ""),
                        source = m.Source,
                        keyFile = m.Identity is not null,
                        status = s?.Status ?? "idle",
                        stage = s?.Stage,
                        percent = s?.Percent,
                        action = s?.LastAction,
                        problem = p is null ? null : new
                        {
                            kind = p.Kind,
                            message = p.Message,
                            detail = privacy ? Scrub(p.Detail) : p.Detail,
                            rid = p.Rid,
                            fingerprints = s!.Fingerprints,
                            canFixKey = p.Kind == "key-permissions" && OperatingSystem.IsWindows(),
                            canUseGit = p.Kind == "key-permissions" && clients.Any(c => c.Id == "git") && current?.Id != "git"
                        },
                        host = s?.Agent is { } a ? new { os = a.Os, pretty = a.Pretty, rid = a.Rid, admin = a.Admin, note = a.Note } : null,
                        summary = s?.Summary,
                        session = s?.Session is { Alive: true } session ? new { url = session.Url, since = session.StartedUtc.ToLocalTime().ToString("HH:mm") } : null
                    };
                }).ToList()
            };
        }
        _send(JsonSerializer.Serialize(payload, Views.Json.Options));
    }

    private static string? Scrub(string? text) => text is null ? null :
        System.Text.RegularExpressions.Regex.Replace(Privacy.Scrub(text), @"\b\d{1,3}(\.\d{1,3}){3}\b|\b[\w.-]+@[\w.-]+\b", "•••");

    public void Dispose()
    {
        _disposed = true;
        List<RemoteSession> sessions;
        lock (_gate) sessions = _states.Values.Select(s => s.Session).OfType<RemoteSession>().ToList();
        foreach (var session in sessions) session.Dispose();
        foreach (var state in _states.Values) state.Work?.Cancel();
    }
}
