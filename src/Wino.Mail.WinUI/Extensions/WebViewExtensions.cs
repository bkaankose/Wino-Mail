using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Wino.Editor;

namespace Wino.Mail.WinUI.Extensions;

public static class WebViewExtensions
{
    /// <summary>
    /// Sets WebView2 environment variables once per process.
    /// Must be called before any WebView2 is initialized.
    /// </summary>
    public static void EnsureWebView2Environment()
    {
        WinoWebViewEnvironment.ConfigureProcessEnvironment();
    }

    public static Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        return WinoWebViewEnvironment.GetSharedEnvironmentAsync();
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
