using Microsoft.Extensions.DependencyInjection;
using Wino.Core.ViewModels;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Theme gallery and editor views and ViewModels.</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterThemeSettingsViews(IServiceCollection services)
    {
        // Same types and transient lifetime as the WinUI registration.
        services.AddTransient<ApplicationThemeGalleryPageViewModel>();
        services.AddTransient<ApplicationThemeEditorPageViewModel>();
        services.AddTransient<ApplicationThemeGalleryPageViewController>();
        services.AddTransient<ApplicationThemeEditorPageViewController>();
#if DEBUG
        ThemesDebug.Register();
#endif
    }
}
