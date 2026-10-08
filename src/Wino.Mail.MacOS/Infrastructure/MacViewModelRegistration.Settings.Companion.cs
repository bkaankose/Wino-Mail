using Microsoft.Extensions.DependencyInjection;
using Wino.Core.ViewModels;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Companion settings view and ViewModel. Owned by the Companion work.</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterCompanionSettingsViews(IServiceCollection services)
    {
        // Same type and transient lifetime as the WinUI registration.
        services.AddTransient<CompanionSettingsPageViewModel>();
        services.AddTransient<CompanionSettingsPageViewController>();
    }
}
