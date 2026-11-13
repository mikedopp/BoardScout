using BoardScout.Services;

namespace BoardScout.Live;

/// <summary>The browser dashboard's side of the Storage tab's "what fills a drive" view (Windows). The scan itself is StorageFill.</summary>
internal sealed partial class Hub
{
    private StorageFill? _storageFill;

    private StorageFill Fill => _storageFill ??= new StorageFill(
        (json, essential) => Broadcast("fill", json, essential ? null : c => !c.Hidden),
        Log,
        text => AppSettings.Current.PrivacyMode ? Privacy.Scrub(text) : text);
}
