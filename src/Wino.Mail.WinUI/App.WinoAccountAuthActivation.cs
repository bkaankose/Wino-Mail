using System;
using Microsoft.Extensions.DependencyInjection;
using Wino.Platform.Windows.Authentication;

namespace Wino.Mail.WinUI;

public partial class App
{
    /// <summary>
    /// Hands the <c>wino-mail://auth-complete</c> redirect to the browser sign-in that is waiting for it (the Wino
    /// Account login or registration dialog). False when nothing waits, which means a stale browser tab.
    /// </summary>
    private bool HandleWinoAccountAuthCallbackActivation(Uri? callback)
    {
        var delivered = Services.GetRequiredService<WindowsExternalAuthCallbacks>().Deliver(callback);
        if (!delivered)
            LogActivation("A Wino Account sign-in redirect arrived while nothing was waiting for it.");

        return delivered;
    }
}
