using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;

namespace Wino.Mail.WinUI.Services;

public static class HostedContentPopoutCoordinator
{
    public static async Task<bool> PopOutCurrentContentAsync(IHostedPopoutSource source)
    {
        if (!source.CanPopOutCurrentContent())
            return false;

        var content = source.GetCurrentHostedContent();
        if (content is not FrameworkElement frameworkElement || frameworkElement is not IPopoutClient client || !client.SupportsPopOut)
            return false;

        var descriptor = source.CreatePopoutDescriptor(client);
        var windowManager = WinoApplication.Current.Services.GetRequiredService<IWinoWindowManager>();

        if (windowManager.GetWindow(WinoWindowKind.HostedPopout, descriptor.WindowName) is HostedContentPopoutWindow existingWindow)
        {
            windowManager.ActivateWindow(existingWindow);
            return false;
        }

        var detachedContent = source.DetachHostedContent();
        if (detachedContent is IPopoutClient detachedClient)
        {
            detachedClient.OnPopoutStateChanged(true);
        }

        HostedContentPopoutWindow? popoutWindow = null;

        popoutWindow = (HostedContentPopoutWindow)windowManager.CreateWindow(
            WinoWindowKind.HostedPopout,
            () => new HostedContentPopoutWindow(descriptor, () =>
            {
                source.OnHostedPopoutClosed(detachedContent, descriptor);
            }),
            descriptor.WindowName);

        popoutWindow.SetHostedContent(detachedContent);
        source.OnHostedContentPoppedOut(detachedContent, popoutWindow, descriptor);
        windowManager.ActivateWindow(popoutWindow);

        var themeService = WinoApplication.Current.Services.GetService<INewThemeService>();
        if (themeService != null)
        {
            await themeService.ApplyThemeToActiveWindowAsync();
        }

        return true;
    }
}
