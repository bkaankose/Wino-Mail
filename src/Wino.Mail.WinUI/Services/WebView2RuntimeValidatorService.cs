using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Extensions;

namespace Wino.Mail.WinUI.Services;

public class WebView2RuntimeValidatorService : IWebView2RuntimeValidatorService
{
    public async Task<bool> IsRuntimeAvailableAsync()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(version))
            {
                return false;
            }

            await WebViewExtensions.GetSharedEnvironmentAsync();

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebView2 runtime validation failed.");
            return false;
        }
    }
}
