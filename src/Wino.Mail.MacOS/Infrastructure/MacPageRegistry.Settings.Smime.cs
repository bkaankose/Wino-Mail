using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Signature and encryption (S/MIME). Owned by the S/MIME work (WS6).</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterSmimeSettingsPages()
    {
        Register<SignatureAndEncryptionPageViewController>(WinoPage.SignatureAndEncryptionPage, MacPageHost.SettingsWindow);
    }
}
