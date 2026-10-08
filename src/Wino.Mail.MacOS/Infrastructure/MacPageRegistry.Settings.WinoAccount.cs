using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Wino Account management. Owned by the Wino Account page work (WS10).</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterWinoAccountPages()
    {
        // The billing return (wino://billing/success) navigates here with WinoAccountManagementActivationReason.CheckoutCompleted.
        Register<WinoAccountManagementPageViewController>(WinoPage.WinoAccountManagementPage, MacPageHost.SettingsWindow);
    }
}
