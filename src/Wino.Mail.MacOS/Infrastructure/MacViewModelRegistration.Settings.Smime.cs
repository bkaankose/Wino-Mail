using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views.Settings;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Signature and encryption views and ViewModels. Owned by the S/MIME work (WS6).</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterSmimeSettingsViews(IServiceCollection services)
    {
        // Same type and transient lifetime as the WinUI registration. The certificate store itself
        // (ISmimeCertificateService → MacSmimeCertificateService) is a singleton in Composition.
        services.AddTransient<SignatureAndEncryptionPageViewModel>();
        services.AddTransient<SignatureAndEncryptionPageViewController>();
#if DEBUG
        SignatureAndEncryptionPageViewController.SmimeDebug.Register();
#endif
    }
}
