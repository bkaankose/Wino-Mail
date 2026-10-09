using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views.Settings;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Account signature views and ViewModels. Owned by the signature settings work.</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterSignatureSettingsViews(IServiceCollection services)
    {
        // Same type and transient lifetime as the WinUI registration.
        services.AddTransient<SignatureManagementPageViewModel>();
        services.AddTransient<SignatureManagementPageViewController>();
#if DEBUG
        SignatureDebug.Register();
#endif
    }
}
