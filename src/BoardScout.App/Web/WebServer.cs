using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoardScout.Live;
using BoardScout.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoardScout.Web;

/// <summary>Where the running dashboard is, so a second launch can open it instead of starting another.</summary>
internal sealed record InstanceInfo(int Pid, int Port, string Url, string Listen);

/// <summary>How to serve the dashboard.</summary>
internal sealed record ServeOptions
{
    /// <summary>An address to serve on besides this computer (e.g. 0.0.0.0); null serves this computer only.</summary>
    public string? Listen { get; init; }
    public int? Port { get; init; }
    public int DefaultPort { get; init; } = 8797;

    /// <summary>Stop when the last dashboard tab closes (after a grace period).</summary>
    public bool StopWhenClosed { get; init; }

    /// <summary>
    /// Started by another BoardScout over SSH: serve this computer only on any free port, print the port and key
    /// as one line for that BoardScout to read, and stop when it disconnects or nobody has used it for a while.
    /// </summary>
    public bool Agent { get; init; }

    /// <summary>The name the other BoardScout reached this computer by, shown in the dashboard's header.</summary>
    public string? Label { get; init; }

    /// <summary>Stop when standard input closes (the SSH session ended).</summary>
    public bool WatchStdin { get; init; }

    public required string DataRoot { get; init; }

    /// <summary>Called with the dashboard's link (with its key) once it is serving.</summary>
    public Action<string>? Started { get; init; }
}

/// <summary>
/// Serves the dashboard: the shell, the shared web views (with a small bridge that stands in for WebView2's
/// messaging), a server-sent event stream per open dashboard, and exports. Every request needs the access key
/// printed at start (kept as a same-site cookie after the first visit), so other users and other sites can't
/// read this computer's details.
/// </summary>
internal static class WebServer
{
    /// <summary>The line an agent prints when it is ready; the rest of the line is JSON with its port and key.</summary>
    public const string AgentReady = "BOARDSCOUT-AGENT ";

    private static readonly string[] Views = ["shell", "overview", "connections", "system", "topology", "machines"];
    private static readonly TimeSpan AgentIdleLimit = TimeSpan.FromMinutes(3);

    public static string InstanceFile(string dataRoot) => Path.Combine(dataRoot, "instance.json");

    public static async Task<int> RunAsync(ServeOptions options)
    {
        var hub = Hub.Current;
        hub.DataRoot = options.DataRoot;
        if (options.Agent) hub.Agent = new AgentInfo(options.Label ?? "");
        var lan = !options.Agent && options.Listen is not null && options.Listen is not ("127.0.0.1" or "localhost");
        var address = options.Agent || options.Listen is null or "localhost" ? IPAddress.Loopback : IPAddress.Parse(options.Listen.Trim('[', ']'));
        var port = options.Agent ? options.Port ?? 0 : FreePort(address, options.Port ?? options.DefaultPort);
        if (!options.Agent && port == 0)
        {
            Console.Error.WriteLine($"No free port near {options.Port ?? options.DefaultPort}; choose one with --port.");
            return 1;
        }
        var token = RandomNumberGenerator.GetHexString(32, lowercase: true);
        // Several dashboards can share 127.0.0.1 (this one and remote ones through SSH tunnels); browsers keep
        // cookies per host, not per port, so each agent's key gets its own cookie name.
        var cookie = options.Agent ? "boardscout_" + RandomNumberGenerator.GetHexString(8, lowercase: true) : "boardscout_key";

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(address, port);
            if (!lan && !options.Agent) kestrel.Listen(IPAddress.IPv6Loopback, port, _ => { });
        });
        var app = builder.Build();
        hub.Quit = () => app.Lifetime.StopApplication();

        app.Use(async (context, next) =>
        {
            hub.Touch();
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "SAMEORIGIN";
            headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
                                                 "img-src 'self' data: blob:; connect-src 'self'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";
            var path = context.Request.Path.Value ?? "/";
            if (path == "/api/ping")
            {
                await next();
                return;
            }
            // Bound to this computer only: refuse other host names (a page on another site can't rebind to us).
            // The port isn't checked: through an SSH tunnel the browser uses the tunnel's own port.
            if (!lan && context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
            {
                context.Response.StatusCode = 403;
                return;
            }
            if (context.Request.Query.TryGetValue("key", out var key))
            {
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key.ToString()), Encoding.ASCII.GetBytes(token)))
                {
                    context.Response.Cookies.Append(cookie, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true });
                    context.Response.Redirect(path);
                    return;
                }
            }
            var presented = context.Request.Cookies[cookie] ?? "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(presented), Encoding.ASCII.GetBytes(token)))
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(options.Agent ? AgentLockedPage : LockedPage);
                return;
            }
            await next();
        });

        app.MapGet("/api/ping", () => Results.Text($"BoardScout {AppInfo.Version}"));
        app.MapGet("/", () => ShellFile("index.html"));
        app.MapGet("/shell/{**path}", (string path) => ShellFile(path));
        app.MapGet("/views/vendor/{**path}", (string path) => StaticFile(Path.Combine(AppContext.BaseDirectory, "Assets", "vendor"), path));
        app.MapGet("/views/{name}.html", (string name) => ViewPage(name));
        app.MapGet("/api/events", (HttpContext context) => EventsAsync(context, hub));
        app.MapPost("/api/msg/{view}", async (string view, HttpContext context) =>
        {
            if (!Views.Contains(view) || context.Request.Headers["X-BoardScout"] != "1") return Results.StatusCode(403);
            JsonElement message;
            try
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                message = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
            var client = hub.Find(context.Request.Headers["X-BoardScout-Client"].ToString());
            _ = Task.Run(() => hub.HandleAsync(client, view, message));
            return Results.Accepted();
        });
        app.MapGet("/api/export/scan.json", () => Download(hub.ExportScan(), "scan"));
        app.MapGet("/api/export/connections.json", () => Download(hub.ExportConnections(), "connections"));
        app.MapGet("/api/diagnostics", () => Results.Text(hub.Diagnostics(), "text/plain; charset=utf-8"));

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
        app.Lifetime.ApplicationStopping.Register(hub.CloseAll);
        var loops = Task.WhenAll(hub.RunTelemetryAsync(stopping.Token), hub.RunDeviceWatchAsync(stopping.Token));
        if (options.StopWhenClosed) WatchForLastClose(hub, app.Lifetime);

        await app.StartAsync();
        if (options.Agent)
        {
            var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            port = bound is not null && Uri.TryCreate(bound, UriKind.Absolute, out var uri) ? uri.Port : port;
        }
        var url = $"http://{(lan ? (options.Listen is "0.0.0.0" or "::" or "[::]" ? Dns.GetHostName() : options.Listen) : "127.0.0.1")}:{port}/?key={token}";
        hub.ListenUrl = url[..url.IndexOf("?key=", StringComparison.Ordinal)];
        if (!options.Agent)
            WritePrivate(InstanceFile(options.DataRoot), JsonSerializer.Serialize(new InstanceInfo(Environment.ProcessId, port, url, options.Listen ?? "127.0.0.1")));
        hub.Log(options.Agent
            ? $"BoardScout {AppInfo.Version} started over SSH{(string.IsNullOrEmpty(options.Label) ? "" : $" by a BoardScout that calls this computer {options.Label}")}; serving this computer only."
            : $"BoardScout {AppInfo.Version} serving {hub.ListenUrl} ({(lan ? "on your network" : "this computer only")}).");
        _ = hub.LoadAsync(rediscover: false);

        if (options.Agent)
        {
            // The BoardScout that started this reads this one line, then reaches the port through its SSH tunnel.
            Console.Out.WriteLine(AgentReady + JsonSerializer.Serialize(new { port, key = token, version = AppInfo.Version, platform = Hub.PlatformName }));
            Console.Out.Flush();
            WatchAgent(hub, app.Lifetime, options.WatchStdin, stopping.Token);
        }
        else
        {
            Console.WriteLine($"BoardScout {AppInfo.Version} is running.");
            Console.WriteLine($"Open: {url}");
            if (lan) Console.WriteLine("Anyone who has this link can see this computer's details. Keep it private; Ctrl+C stops BoardScout.");
            else Console.WriteLine("Only this computer can open it. Ctrl+C stops BoardScout.");
        }
        options.Started?.Invoke(url);

        await app.WaitForShutdownAsync();
        stopping.Cancel();
        try { await loops; } catch (OperationCanceledException) { }
        if (!options.Agent)
        {
            // Only this instance's own address: a newer instance may already have written its own.
            try
            {
                var file = InstanceFile(options.DataRoot);
                if (File.Exists(file) && JsonSerializer.Deserialize<InstanceInfo>(File.ReadAllText(file))?.Pid == Environment.ProcessId)
                    File.Delete(file);
            }
            catch { }
        }
        return 0;
    }

    /// <summary>The running dashboard's link, when one is running for this user.</summary>
    public static InstanceInfo? RunningInstance(string dataRoot)
    {
        try
        {
            var file = InstanceFile(dataRoot);
            if (!File.Exists(file)) return null;
            var info = JsonSerializer.Deserialize<InstanceInfo>(File.ReadAllText(file));
            if (info is null || info.Pid == Environment.ProcessId || !Alive(info.Pid)) return null;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var host = info.Listen is "0.0.0.0" or "::" or "[::]" or "127.0.0.1" ? "127.0.0.1" : info.Listen;
            var answer = http.GetStringAsync($"http://{host}:{info.Port}/api/ping").GetAwaiter().GetResult();
            return answer.StartsWith("BoardScout", StringComparison.Ordinal) ? info : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    // An agent stops when its SSH session ends (standard input closes, or nothing reads what it writes), or when
    // nobody has used it for a few minutes: a BoardScout that went away without saying so leaves nothing running.
    private static void WatchAgent(Hub hub, IHostApplicationLifetime lifetime, bool watchStdin, CancellationToken stopping)
    {
        if (watchStdin)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var input = Console.OpenStandardInput();
                    var buffer = new byte[256];
                    while (await input.ReadAsync(buffer, stopping) > 0) { }
                    hub.Log("The SSH session ended; stopping.");
                    lifetime.StopApplication();
                }
                catch (OperationCanceledException) { }
                catch { }
            }, stopping);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stopping);
                    try
                    {
                        Console.Out.WriteLine("BOARDSCOUT-ALIVE");
                        Console.Out.Flush();
                    }
                    catch (IOException)
                    {
                        hub.Log("The SSH session ended; stopping.");
                        lifetime.StopApplication();
                        return;
                    }
                    if (!hub.AnyClients && hub.Idle > AgentIdleLimit)
                    {
                        hub.Log($"Nobody has used this dashboard for {AgentIdleLimit.TotalMinutes:0} minutes; stopping.");
                        lifetime.StopApplication();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, stopping);
    }

    // A dashboard opened from the app menu stops when its last browser tab closes (after a grace period).
    private static void WatchForLastClose(Hub hub, IHostApplicationLifetime lifetime)
    {
        var everConnected = false;
        CancellationTokenSource? pending = null;
        hub.ClientsChanged += () =>
        {
            if (hub.AnyClients)
            {
                everConnected = true;
                pending?.Cancel();
                return;
            }
            if (!everConnected) return;
            pending?.Cancel();
            var cts = pending = new CancellationTokenSource();
            _ = Task.Delay(TimeSpan.FromSeconds(45), cts.Token).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && !hub.AnyClients)
                {
                    hub.Log("The last dashboard tab closed; stopping.");
                    lifetime.StopApplication();
                }
            }, TaskScheduler.Default);
        };
    }

    private static async Task EventsAsync(HttpContext context, Hub hub)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var client = hub.Connect();
        var token = context.RequestAborted;
        try
        {
            await context.Response.WriteAsync(": connected\n\n", token);
            await context.Response.Body.FlushAsync(token);
            var reader = client.Outbox.Reader;
            while (!token.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                bool more;
                try
                {
                    more = await reader.WaitToReadAsync(heartbeat.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    await context.Response.WriteAsync(": ping\n\n", token);
                    await context.Response.Body.FlushAsync(token);
                    continue;
                }
                if (!more) break;
                var builder = new StringBuilder();
                // An event's data may span lines, as long as each line carries its own "data:" prefix.
                while (reader.TryRead(out var line)) builder.Append("data: ").Append(line.Replace("\r", "").Replace("\n", "\ndata: ")).Append("\n\n");
                await context.Response.WriteAsync(builder.ToString(), token);
                await context.Response.Body.FlushAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            hub.Disconnect(client);
        }
    }

    // The shared views talk to WebView2; the bridge script gives them the same API over this server.
    private static IResult ViewPage(string name)
    {
        if (name is not ("overview" or "connections" or "system" or "topology" or "machines")) return Results.NotFound();
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", name + ".html");
        if (!File.Exists(path)) return Results.NotFound();
        var html = File.ReadAllText(path);
        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        var bridge = $"<script src=\"/shell/bridge.js\" data-view=\"{name}\"></script>";
        html = head >= 0 ? html.Insert(head + 6, bridge) : bridge + html;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static IResult ShellFile(string path) => StaticFile(Path.Combine(AppContext.BaseDirectory, "Shell"), path);

    private static IResult StaticFile(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
            return Results.NotFound();
        var type = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".json" => "application/json",
            ".woff2" => "font/woff2",
            ".txt" or ".md" or "" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };
        return Results.File(full, type);
    }

    private static IResult Download(string? json, string what)
    {
        if (json is null) return Results.Problem("Nothing has been read yet.", statusCode: 503);
        var host = AppSettings.Current.PrivacyMode ? Privacy.HostPlaceholder : Environment.MachineName;
        return Results.File(Encoding.UTF8.GetBytes(json), "application/json", $"boardscout-{what}-{host}-{DateTime.Now:yyyyMMdd-HHmm}.json");
    }

    /// <summary>Writes a file only this user can read (the instance file carries the access key).</summary>
    public static void WritePrivate(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { }
        }
        File.Move(temp, path, overwrite: true);
    }

    // The configured port, or the next free one after it.
    private static int FreePort(IPAddress address, int start)
    {
        for (var port = start; port < start + 20 && port <= 65535; port++)
        {
            try
            {
                using var probe = new TcpListener(address, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException)
            {
            }
        }
        return 0;
    }

    private const string LockedPage = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>BoardScout</title>
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0c1620;color:#e8edf4;font:15px/1.5 system-ui,sans-serif}
        main{max-width:440px;padding:28px;border-radius:18px;background:#111e2c;border:1px solid #1c2a3c}h1{font-size:18px;margin:0 0 8px}p{color:#8a9bb2;margin:6px 0}
        code{background:#0c1620;padding:2px 6px;border-radius:6px;color:#cfe6ff}</style></head>
        <body><main><h1>This dashboard needs its key</h1>
        <p>Open the link BoardScout printed when it started (it ends in <code>?key=…</code>).</p>
        <p>On this computer, running <code>boardscout</code> again opens it for you.</p></main></body></html>
        """;

    private const string AgentLockedPage = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>BoardScout</title>
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0c1620;color:#e8edf4;font:15px/1.5 system-ui,sans-serif}
        main{max-width:440px;padding:28px;border-radius:18px;background:#111e2c;border:1px solid #1c2a3c}h1{font-size:18px;margin:0 0 8px}p{color:#8a9bb2;margin:6px 0}</style></head>
        <body><main><h1>Open this from the Machines page</h1>
        <p>This dashboard was started over SSH by another BoardScout, and only the link it opened carries the key.</p>
        <p>Go back to Machines and choose Open again.</p></main></body></html>
        """;
}
