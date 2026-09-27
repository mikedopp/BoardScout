using System.Diagnostics;
using System.Net;
using System.Text.Json.Serialization;
using BoardScout.UI;

namespace BoardScout.Services;

/// <summary>Progress and result of an Internet speed test, as sent to the Connections view.</summary>
public sealed class SpeedTestUpdate
{
    [JsonPropertyName("type")] public string Type { get; set; } = "speed";

    /// <summary>latency, download, upload, done, or error.</summary>
    [JsonPropertyName("phase")] public string Phase { get; set; } = "";

    /// <summary>The current phase's speed so far, in megabits per second.</summary>
    [JsonPropertyName("mbps")] public double? Mbps { get; set; }

    [JsonPropertyName("downMbps")] public double? DownloadMbps { get; set; }
    [JsonPropertyName("upMbps")] public double? UploadMbps { get; set; }
    [JsonPropertyName("latencyMs")] public double? LatencyMs { get; set; }
    [JsonPropertyName("jitterMs")] public double? JitterMs { get; set; }
    [JsonPropertyName("usedMB")] public double? UsedMegabytes { get; set; }
    [JsonPropertyName("edge")] public string? Edge { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("checkedAt")] public string? CheckedAt { get; set; }
}

/// <summary>
/// Measures Internet latency, download, and upload speed against Cloudflare's public speed test
/// (speed.cloudflare.com), the same endpoints its web page uses. Runs only when the user asks. Several
/// connections run in parallel; the result is the best steady second after they ramp up, and each
/// direction stops after a few seconds or a data cap, whichever comes first.
/// </summary>
internal static class SpeedTest
{
    public const string Server = "https://speed.cloudflare.com";
    private const int Streams = 4;
    private const long DownloadChunk = 25_000_000;
    private const int UploadChunk = 8_000_000;
    // Enough for a steady second of data on multi-gigabit service; slower connections hit the time limit first.
    private const long DownloadCap = 400_000_000;
    private const long UploadCap = 150_000_000;
    private static readonly TimeSpan DownloadTime = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan UploadTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Sample = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RampUp = TimeSpan.FromMilliseconds(400);

    public static async Task<SpeedTestUpdate> RunAsync(Action<SpeedTestUpdate> progress, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(1), MaxConnectionsPerServer = Streams + 1 };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"BoardScout/{VersionButton.AppVersion}");

        progress(new SpeedTestUpdate { Phase = "latency" });
        var (latency, jitter, edge) = await MeasureLatencyAsync(http, token);

        long downloaded = 0;
        var download = await MeasureAsync("download", DownloadTime, DownloadCap, progress,
            (stop, count) => DownloadWorkerAsync(http, stop, count), () => Interlocked.Read(ref downloaded),
            value => Interlocked.Add(ref downloaded, value), token);

        long uploaded = 0;
        var upload = await MeasureAsync("upload", UploadTime, UploadCap, progress,
            (stop, count) => UploadWorkerAsync(http, stop, count), () => Interlocked.Read(ref uploaded),
            value => Interlocked.Add(ref uploaded, value), token);

        return new SpeedTestUpdate
        {
            Phase = "done",
            DownloadMbps = Math.Round(download, 1),
            UploadMbps = Math.Round(upload, 1),
            LatencyMs = latency,
            JitterMs = jitter,
            UsedMegabytes = Math.Round((downloaded + uploaded) / 1e6, 1),
            Edge = edge,
            CheckedAt = DateTimeOffset.Now.ToString("HH:mm:ss")
        };
    }

    // Time to the response headers of an empty download, on a warm connection. The first request (which
    // also opens the connection) is not counted.
    private static async Task<(double? Latency, double? Jitter, string? Edge)> MeasureLatencyAsync(HttpClient http, CancellationToken token)
    {
        var samples = new List<double>();
        string? edge = null;
        for (var i = 0; i < 7; i++)
        {
            var watch = Stopwatch.StartNew();
            using var response = await http.GetAsync($"{Server}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, token);
            watch.Stop();
            response.EnsureSuccessStatusCode();
            // cf-ray ends with the code of the Cloudflare location that answered, e.g. "…-SLC".
            if (edge is null && response.Headers.TryGetValues("cf-ray", out var ray))
                edge = ray.FirstOrDefault()?.Split('-').LastOrDefault();
            if (i > 0) samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        if (samples.Count == 0) return (null, null, edge);
        // Jitter: how much one round trip differs from the next, in the order they happened.
        var jitter = samples.Zip(samples.Skip(1), (a, b) => Math.Abs(b - a)).DefaultIfEmpty(0).Average();
        var sorted = samples.Order().ToList();
        return (Math.Round(sorted[sorted.Count / 2], 1), Math.Round(jitter, 1), edge);
    }

    private static async Task<double> MeasureAsync(
        string phase, TimeSpan duration, long cap, Action<SpeedTestUpdate> progress,
        Func<CancellationToken, Action<long>, Task> worker, Func<long> total, Action<long> add, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watch = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, Streams).Select(_ => worker(stop.Token, add)).ToList();

        var samples = new List<(double At, long Bytes)> { (0, 0) };
        while (watch.Elapsed < duration && total() < cap && !workers.All(w => w.IsCompleted))
        {
            await Task.Delay(Sample, token);
            var now = watch.Elapsed.TotalSeconds;
            samples.Add((now, total()));
            if (samples.Count % 3 == 0)
                progress(new SpeedTestUpdate { Phase = phase, Mbps = Math.Round(RateOver(samples, now - 1, now), 1) });
        }
        samples.Add((watch.Elapsed.TotalSeconds, total()));
        stop.Cancel();
        try { await Task.WhenAll(workers); } catch (OperationCanceledException) { } catch (HttpRequestException) { } catch (IOException) { }
        token.ThrowIfCancellationRequested();
        // Nothing moved at all: report why instead of a speed of zero.
        if (samples[^1].Bytes == 0 && workers.FirstOrDefault(w => w.IsFaulted)?.Exception?.GetBaseException() is { } failure)
            throw new HttpRequestException($"The {phase} test failed: {failure.Message}", failure);

        // The best steady second after the connections ramped up. Very fast lines finish before a full
        // second has passed; then it is the average after the ramp-up.
        var start = RampUp.TotalSeconds;
        var end = samples[^1].At;
        if (end - start < 1) return RateOver(samples, Math.Min(start, end / 3), end);
        var best = 0d;
        foreach (var (at, _) in samples.Where(s => s.At >= start && s.At + 1 <= end))
            best = Math.Max(best, RateOver(samples, at, at + 1));
        return best;
    }

    // Megabits per second between two moments, from the byte-count samples nearest to them.
    private static double RateOver(List<(double At, long Bytes)> samples, double from, double to)
    {
        var first = samples.LastOrDefault(s => s.At <= from, samples[0]);
        var last = samples.LastOrDefault(s => s.At <= to, samples[^1]);
        var seconds = last.At - first.At;
        return seconds <= 0 ? 0 : (last.Bytes - first.Bytes) * 8 / seconds / 1e6;
    }

    private static async Task DownloadWorkerAsync(HttpClient http, CancellationToken stop, Action<long> count)
    {
        var buffer = new byte[64 * 1024];
        while (!stop.IsCancellationRequested)
        {
            using var response = await http.GetAsync($"{Server}/__down?bytes={DownloadChunk}", HttpCompletionOption.ResponseHeadersRead, stop);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(stop);
            int read;
            while ((read = await stream.ReadAsync(buffer, stop)) > 0) count(read);
        }
    }

    private static async Task UploadWorkerAsync(HttpClient http, CancellationToken stop, Action<long> count)
    {
        var payload = new byte[UploadChunk];
        Random.Shared.NextBytes(payload); // incompressible, so nothing along the way can shrink it
        while (!stop.IsCancellationRequested)
        {
            using var content = new CountingContent(payload, count);
            using var response = await http.PostAsync($"{Server}/__up", content, stop);
            response.EnsureSuccessStatusCode();
        }
    }

    // Reports bytes as they are written to the socket, so upload progress is live.
    private sealed class CountingContent(byte[] payload, Action<long> count) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            const int slice = 64 * 1024;
            for (var offset = 0; offset < payload.Length; offset += slice)
            {
                var length = Math.Min(slice, payload.Length - offset);
                await stream.WriteAsync(payload.AsMemory(offset, length), token);
                count(length);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = payload.Length;
            return true;
        }
    }
}
