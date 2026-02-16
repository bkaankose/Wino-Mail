using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services;

public class WebView2RuntimeValidatorService : IWebView2RuntimeValidatorService
{
    public Task<bool> IsRuntimeAvailableAsync()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var hasRuntime = !string.IsNullOrWhiteSpace(version);

            return Task.FromResult(hasRuntime);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebView2 runtime validation failed.");
            return Task.FromResult(false);
        }
    }
}
