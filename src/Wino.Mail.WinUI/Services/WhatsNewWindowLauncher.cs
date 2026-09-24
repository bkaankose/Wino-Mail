using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Messaging.UI;

namespace Wino.Mail.WinUI.Services;

public sealed class WhatsNewWindowLauncher(IWinoWindowManager windowManager,
                                           INewThemeService themeService,
                                           IWhatsNewService whatsNewService) : IWhatsNewWindowLauncher
{
    public async Task ShowAsync()
    {
        if (windowManager.GetWindow(WinoWindowKind.WhatsNew) is WhatsNewWindow existingWindow)
        {
            windowManager.ActivateWindow(existingWindow);
        }
        else
        {
            var window = windowManager.CreateWindow(WinoWindowKind.WhatsNew, () => new WhatsNewWindow());

            // CreateWindow made it the active window, which is the one the theme service targets.
            // Do not show it before its theme resources and backdrop are ready.
            await themeService.ApplyThemeToActiveWindowAsync();
            windowManager.ActivateWindow(window);
        }

        whatsNewService.MarkOpenedForCurrentVersion();
        WeakReferenceMessenger.Default.Send(new WhatsNewOpened());
    }
}
