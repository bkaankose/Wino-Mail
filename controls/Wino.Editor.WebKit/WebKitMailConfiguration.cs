using WebKit;

namespace Wino.Editor.WebKit;

/// <summary>
/// Configuration for web views that show or edit mail. Mail markup is untrusted and remote images can carry
/// trackers, so the view keeps cookies, caches and site storage in memory only (the Windows reader and composer run
/// WebView2 InPrivate for the same reason).
/// </summary>
public static class WebKitMailConfiguration
{
    public static WKWebViewConfiguration Create() => new()
    {
        WebsiteDataStore = WKWebsiteDataStore.NonPersistentDataStore
    };
}
