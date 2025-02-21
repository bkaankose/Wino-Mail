using System;
using System.Threading.Tasks;

namespace Wino.Core.UWP.Extensions;

public static class WebViewExtensions
{
    /// <summary>
    /// Executes a script function in the WebView2 control.
    /// </summary>
    /// <param name="isChromiumDisposed">Weird parameter that needed in mailRendering page. TODO: should be reconsidered.</param>
    /// <param name="parameters">Parameters should be serialized to json</param>
    public static async Task<string> ExecuteScriptFunctionAsync(this Microsoft.UI.Xaml.Controls.WebView2 Chromium, string functionName, bool isChromiumDisposed = false, params string[] parameters)
    {
        string script = functionName + "(" + string.Join(", ", parameters) + ");";

        return isChromiumDisposed ? string.Empty : await Chromium.ExecuteScriptAsync(script);
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
