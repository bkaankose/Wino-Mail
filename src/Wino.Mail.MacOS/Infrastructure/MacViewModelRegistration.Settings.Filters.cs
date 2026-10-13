using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Mail filter views and ViewModels (rule list and rule editor).</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterFilterSettingsViews(IServiceCollection services)
    {
        // Same types and transient lifetime as the WinUI registration.
        services.AddTransient<MailFiltersPageViewController>();
        services.AddTransient<MailFilterEditorPageViewController>();
#if DEBUG
        MailFiltersDebug.Register();
#endif
    }
}
