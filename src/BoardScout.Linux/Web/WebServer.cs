using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoardScout.Live;
using BoardScout.Platform;
using BoardScout.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoardScout.Web;

/// <summary>Where the running dashboard is, so a second launch can open it instead of starting another.</summary>
internal sealed record InstanceInfo(int Pid, int Port, string Url, string Listen);

/// <summary>
/// Serves the dashboard: the shell, the shared web views (with a small bridge that stands in for WebView2's
/// messaging), a server-sent event stream per open dashboard, and exports. Every request needs the access key
/// printed at start (kept as a same-site cookie after the first visit), so other users and other sites can't
/// read this computer's details.
/// </summary>
internal static class WebServer
{
    private const string Cookie = "boardscout_key";
    private static readonly string[] Views = ["shell", "connections", "system", "topology"];

    public static string InstanceFile => Path.Combine(DataFolder.Root, "instance.json");

    public static async Task<int> RunAsync(LaunchOptions options)
    {
        var hub = Hub.Current;
        Tools.Ran += line => hub.Log("RAN: " + line);
        var lan =options.Listen is not null && options.Listen != "127.0.0.1" && options.Listen != "localhost";
        var address = options.Listen is null or "localhost" ? IPAddress.Loopback : IPAddress.Parse(options.Listen.Trim('[', ']'));
        var port = options.Port ?? AppSettings.Current.Port;
        port = FreePort(address, port);
        if (port == 0)
        {
            Console.Error.WriteLine($"No free port near {options.Port ?? AppSettings.Current.Port}; choose one with --port.");
            return 1;
        }
        var token = RandomNumberGenerator.GetHexString(32, lowercase: true);
        var hostForUrl = lan ? (options.Listen is "0.0.0.0" or "::" or "[::]" ? Dns.GetHostName() : options.Listen) : "127.0.0.1";
        var url = $"http://{hostForUrl}:{port}/?key={token}";
        hub.ListenUrl = lan ? $"http://{hostForUrl}:{port}/" : $"http://127.0.0.1:{port}/";

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(address, port);
            if (!lan) kestrel.Listen(IPAddress.IPv6Loopback, port, _ => { });
        });
        var app = builder.Build();
        hub.Quit = () => app.Lifetime.StopApplication();

        app.Use(async (context, next) =>
        {
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
            if (!lan && context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
            {
                context.Response.StatusCode = 403;
                return;
            }
            if (context.Request.Query.TryGetValue("key", out var key))
            {
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key.ToString()), Encoding.ASCII.GetBytes(token)))
                {
                    context.Response.Cookies.Append(Cookie, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true });
                    context.Response.Redirect(path);
                    return;
                }
            }
            var cookie = context.Request.Cookies[Cookie] ?? "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(cookie), Encoding.ASCII.GetBytes(token)))
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(LockedPage);
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
        DataFolder.WritePrivate(InstanceFile, JsonSerializer.Serialize(new InstanceInfo(Environment.ProcessId, port, url, options.Listen ?? "127.0.0.1")));
        hub.Log($"BoardScout {AppInfo.Version} serving {hub.ListenUrl} ({(lan ? "on your network" : "this computer only")}).");
        _ = hub.LoadAsync(rediscover: false);

        Console.WriteLine($"BoardScout {AppInfo.Version} is running.");
        Console.WriteLine($"Open: {url}");
        if (lan) Console.WriteLine("Anyone who has this link can see this computer's details. Keep it private; Ctrl+C stops BoardScout.");
        else Console.WriteLine("Only this computer can open it. Ctrl+C stops BoardScout.");
        if (options.OpenBrowser) Desktop.OpenBrowser(url);

        await app.WaitForShutdownAsync();
        stopping.Cancel();
        try { await loops; } catch (OperationCanceledException) { }
        // Only this instance's own address: a newer instance may already have written its own.
        try
        {
            if (File.Exists(InstanceFile) && JsonSerializer.Deserialize<InstanceInfo>(File.ReadAllText(InstanceFile))?.Pid == Environment.ProcessId)
                File.Delete(InstanceFile);
        }
        catch { }
        return 0;
    }

    /// <summary>The running dashboard's link, when one is running for this user.</summary>
    public static InstanceInfo? RunningInstance()
    {
        try
        {
            if (!File.Exists(InstanceFile)) return null;
            var info = JsonSerializer.Deserialize<InstanceInfo>(File.ReadAllText(InstanceFile));
            if (info is null || info.Pid == Environment.ProcessId || !Directory.Exists($"/proc/{info.Pid}")) return null;
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
        if (name is not ("connections" or "system" or "topology")) return Results.NotFound();
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
}
