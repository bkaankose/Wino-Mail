using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Wino.Mail.WinUI.Extensions;

public static class WebViewExtensions
{
    private static readonly object _environmentLock = new();
    private static bool _environmentInitialized;
    private static Task<CoreWebView2Environment>? _sharedEnvironmentTask;

    /// <summary>
    /// Sets WebView2 environment variables once per process.
    /// Must be called before any WebView2 is initialized.
    /// </summary>
    public static void EnsureWebView2Environment()
    {
        if (_environmentInitialized) return;

        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00FFFFFF");
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "--enable-features=OverlayScrollbar,msOverlayScrollbarWinStyle,msOverlayScrollbarWinStyleAnimation,msWebView2CodeCache");

        _environmentInitialized = true;
    }

    public static Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        EnsureWebView2Environment();

        lock (_environmentLock)
        {
            _sharedEnvironmentTask ??= CoreWebView2Environment.CreateAsync().AsTask();
            return _sharedEnvironmentTask;
        }
    }

    /// <summary>
    /// Executes a script function in the WebView2 control.
    /// </summary>
    /// <param name="parameters">Parameters should be serialized to json</param>
    public static async Task<string> ExecuteScriptFunctionAsync(this Microsoft.UI.Xaml.Controls.WebView2 Chromium, string functionName, params string[] parameters)
    {
        if (Chromium?.CoreWebView2 == null) return string.Empty;

        string script = functionName + "(" + string.Join(", ", parameters) + ");";

        return await Chromium.ExecuteScriptAsync(script);
    }

    public static async Task<string> ExecuteScriptFunctionSafeAsync(this Microsoft.UI.Xaml.Controls.WebView2 Chromium, string functionName, params string[] parameters)
    {
        if (Chromium == null) return string.Empty;

        try
        {
            return await Chromium.ExecuteScriptFunctionAsync(functionName, parameters: parameters);
        }
        catch { }

        return string.Empty;
    }

    public static async Task<string> ExecuteScriptSafeAsync(this Microsoft.UI.Xaml.Controls.WebView2 Chromium, string script)
    {
        if (Chromium == null) return string.Empty;

        try
        {
            return await Chromium.ExecuteScriptAsync(script);
        }
        catch { }

        return string.Empty;
    }
}
