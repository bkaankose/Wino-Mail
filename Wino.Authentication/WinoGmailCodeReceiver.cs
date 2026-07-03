using System;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Wino.Core.Domain.Interfaces;

namespace Wino.Authentication;

/// <summary>
/// Local loopback OAuth code receiver for Gmail that opens the system browser through
/// <see cref="INativeAppService.LaunchUriAsync"/>.
///
/// The base class opens the browser with "cmd /c start"; inside a packaged WinUI 3 (MSIX) process
/// that shell-out - like a direct ShellExecute or Windows.System.Launcher - silently reports success
/// but launches no browser, so <see cref="GoogleWebAuthorizationBroker"/> blocks forever on the
/// loopback listener and the "Authenticating" step hangs. <see cref="INativeAppService.LaunchUriAsync"/>
/// launches the resolved default-browser executable directly, which works from the packaged host.
/// </summary>
internal sealed class WinoGmailCodeReceiver : LocalServerCodeReceiver
{
    private readonly INativeAppService _nativeAppService;

    public WinoGmailCodeReceiver(INativeAppService nativeAppService)
    {
        _nativeAppService = nativeAppService;
    }

    protected override bool OpenBrowser(string url)
    {
        // Must not block: the broker can invoke this on the UI thread. Launch on a background thread
        // and return immediately; the base receiver then awaits the loopback redirect.
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await _nativeAppService.LaunchUriAsync(new Uri(url)))
                    Serilog.Log.Error("[GmailAuth] Could not open the Gmail authorization URL in a browser.");
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[GmailAuth] Failed to open the Gmail authorization URL.");
            }
        });

        return true;
    }
}
