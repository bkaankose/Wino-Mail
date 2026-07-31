using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Web.WebView2.Core;

namespace Wino.Editor;

public static class WinoWebViewEnvironment
{
    private static readonly object EnvironmentLock = new();
    private static bool _environmentConfigured;
    private static Task<CoreWebView2Environment>? _sharedEnvironmentTask;

    public static void ConfigureProcessEnvironment()
    {
        lock (EnvironmentLock)
        {
            if (_environmentConfigured) return;

            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00FFFFFF");
            Environment.SetEnvironmentVariable(
                "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                "--enable-features=OverlayScrollbar,msOverlayScrollbarWinStyle,msOverlayScrollbarWinStyleAnimation,msWebView2CodeCache");
            _environmentConfigured = true;
        }
    }

    public static Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        ConfigureProcessEnvironment();

        lock (EnvironmentLock)
        {
            if (_sharedEnvironmentTask is null ||
                _sharedEnvironmentTask.IsFaulted ||
                _sharedEnvironmentTask.IsCanceled)
            {
                _sharedEnvironmentTask = CoreWebView2Environment.CreateAsync().AsTask();
            }

            return _sharedEnvironmentTask;
        }
    }
}
