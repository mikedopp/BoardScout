using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BoardScout.UI;

/// <summary>
/// Shared WebView2 plumbing for the Topology and System tabs: one browser environment whose profile
/// lives in the BoardScout data folder (the default beside the exe breaks on read-only folders), and a
/// virtual https host for Assets so ES modules such as QuickLiquid load offline.
/// </summary>
internal static class WebViewHost
{
    public const string AssetHost = "app.boardscout";
    private static Task<CoreWebView2Environment>? _environment;

    public static string? RuntimeVersion
    {
        get
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { return null; }
        }
    }

    /// <summary>Creates the view's browser, maps Assets, and navigates to the page.</summary>
    public static async Task InitializeAsync(WebView2 view, string dataRoot, string page)
    {
        _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(dataRoot, "WebView2"));
        var environment = await _environment;
        await view.EnsureCoreWebView2Async(environment);

        var core = view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        // Map before the first navigation; a mapping added afterwards never takes effect.
        core.SetVirtualHostNameToFolderMapping(AssetHost,
            Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
                uri.Host.Equals(AssetHost, StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.Navigate($"https://{AssetHost}/{page}");
    }

    private static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
