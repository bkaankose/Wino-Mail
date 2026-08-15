using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.WinUI;

public partial class App
{
    private async Task HandleBillingSuccessProtocolActivationAsync(bool activateWindow)
    {
        var settingsContext = new SettingsPageActivationContext(
            WinoPage.WinoIntelligencePage,
            WinoAccountManagementActivationReason.CheckoutCompleted);

        await EnsureShellWindowAsync(
            WinoApplicationMode.Settings,
            activateWindow,
            suppressStartupFlows: true,
            activationParameter: settingsContext);
    }
}
