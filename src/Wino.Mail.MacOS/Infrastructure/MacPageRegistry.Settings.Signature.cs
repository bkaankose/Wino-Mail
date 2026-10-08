using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Account signatures (Manage accounts › account › Signature). Owned by the signature settings work.</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterSignatureSettingsPages()
    {
        Register<SignatureManagementPageViewController>(WinoPage.SignatureManagementPage, MacPageHost.SettingsWindow);
    }
}
