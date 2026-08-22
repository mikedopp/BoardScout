using System.Diagnostics;
using System.Net.Http.Headers;

namespace BoardScout.Services;

internal sealed class DriverDownloadService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders =
        {
            { "User-Agent", "BoardScout/0.5.0" }
        }
    };

    public string DownloadFolder { get; }

    public DriverDownloadService(string dataRoot)
    {
        DownloadFolder = Path.Combine(dataRoot, "Downloads");
        Directory.CreateDirectory(DownloadFolder);
    }

    public async Task<DownloadOutcome> DownloadAsync(
        string url, string componentName,
        IProgress<(long bytes, long? total)>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return new DownloadOutcome(false, true, null, "Invalid URL");

        try
        {
            using var headResponse = await Http.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, uri), ct);

            var contentType = headResponse.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
                !headResponse.IsSuccessStatusCode)
            {
                OpenInBrowser(uri);
                return new DownloadOutcome(false, true, null, null);
            }

            var fileName = ResolveFileName(headResponse.Content.Headers, uri, componentName);
            var localPath = Path.Combine(DownloadFolder, fileName);

            if (File.Exists(localPath))
            {
                var existingSize = new FileInfo(localPath).Length;
                var expectedSize = headResponse.Content.Headers.ContentLength;
                if (expectedSize.HasValue && existingSize == expectedSize.Value)
                    return new DownloadOutcome(true, false, localPath, null);
            }

            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                progress?.Report((downloaded, total));
            }

            return new DownloadOutcome(true, false, localPath, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DownloadOutcome(false, false, null, ex.Message);
        }
    }

    public void OpenDownloadFolder()
    {
        if (Directory.Exists(DownloadFolder))
            Process.Start(new ProcessStartInfo(DownloadFolder) { UseShellExecute = true });
    }

    private static void OpenInBrowser(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static string ResolveFileName(
        HttpContentHeaders headers, Uri uri, string componentName)
    {
        var disposition = headers.ContentDisposition?.FileNameStar
                          ?? headers.ContentDisposition?.FileName?.Trim('"');

        if (!string.IsNullOrWhiteSpace(disposition))
            return SanitizeFileName(disposition);

        var pathName = Path.GetFileName(uri.LocalPath);
        if (!string.IsNullOrWhiteSpace(pathName) && pathName.Contains('.'))
            return SanitizeFileName(pathName);

        var safe = SanitizeFileName(componentName);
        return $"{safe}.download";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length > 200 ? name[..200] : name;
    }
}

public record DownloadOutcome(bool Downloaded, bool OpenedBrowser, string? LocalPath, string? Error);
