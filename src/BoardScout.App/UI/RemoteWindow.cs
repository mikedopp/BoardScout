using System.Diagnostics;
using BoardScout.Remote;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BoardScout.UI;

/// <summary>
/// Another machine's dashboard, opened from the Machines tab: BoardScout running on that machine, reached through
/// an SSH tunnel on this PC's loopback. The window may only show that one address; links elsewhere open in the
/// default browser. Closing the window ends the SSH session, and BoardScout on the machine stops with it.
/// </summary>
internal sealed class RemoteWindow : Form
{
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    private readonly RemoteSession _session;
    private readonly string _dataRoot;

    public RemoteWindow(RemoteSession session, string dataRoot)
    {
        _session = session;
        _dataRoot = dataRoot;
        var name = AppSettings.Current.PrivacyMode ? "remote machine" : session.Machine.Name;
        Text = $"BoardScout · {name} ({session.Agent.Pretty})";
        Icon = AppTheme.CreateAppIcon();
        BackColor = AppTheme.Background;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1500, 940);
        MinimumSize = new Size(900, 600);
        _view.DefaultBackgroundColor = AppTheme.Background;
        Controls.Add(_view);
        HandleCreated += (_, _) => AppTheme.ApplyWindowTheme(this);
        Shown += async (_, _) => await OpenAsync();
        FormClosed += (_, _) => _view.Dispose();
    }

    public RemoteSession Session => _session;

    private async Task OpenAsync()
    {
        try
        {
            await _view.EnsureCoreWebView2Async(await WebViewHost.EnvironmentAsync(_dataRoot));
            var core = _view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += (_, e) =>
            {
                if (IsTunnel(e.Uri)) return;
                e.Cancel = true;
                OpenExternal(e.Uri);
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.DocumentTitleChanged += (_, _) =>
            {
                if (!AppSettings.Current.PrivacyMode && core.DocumentTitle.StartsWith("BoardScout · ", StringComparison.Ordinal))
                    Text = $"{core.DocumentTitle} — {_session.Machine.Name} over SSH";
            };
            core.Navigate(_session.Url);
        }
        catch (Exception ex)
        {
            Controls.Clear();
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.Muted,
                BackColor = AppTheme.Background,
                Text = $"The remote dashboard needs the Microsoft Edge WebView2 Runtime.\n{ex.Message}\n\nIt is still open at {_session.Url.Split('?')[0]} in a browser on this PC."
            });
        }
    }

    // Only the tunnel's own address, on this PC's loopback.
    private bool IsTunnel(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1" && uri.Port == _session.LocalPort;

    private static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
