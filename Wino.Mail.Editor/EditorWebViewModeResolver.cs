using Microsoft.Web.WebView2.Core;

namespace Wino.Mail.Editor;

internal static class EditorWebViewModeResolver
{
    public static EditorWebViewMode Resolve(EditorWebViewMode requestedMode)
    {
        if (requestedMode != EditorWebViewMode.Auto) return requestedMode;

        try
        {
            return string.IsNullOrWhiteSpace(
                CoreWebView2Environment.GetAvailableBrowserVersionString())
                ? EditorWebViewMode.WebView
                : EditorWebViewMode.WebView2;
        }
        catch
        {
            return EditorWebViewMode.WebView;
        }
    }
}
