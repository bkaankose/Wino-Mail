using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views.Settings;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Email template views and ViewModels. Owned by the email template settings work.</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterTemplateSettingsViews(IServiceCollection services)
    {
        // Same types and transient lifetime as the WinUI registration.
        services.AddTransient<EmailTemplatesPageViewModel>();
        services.AddTransient<CreateEmailTemplatePageViewModel>();
        services.AddTransient<EmailTemplatesPageViewController>();
        services.AddTransient<CreateEmailTemplatePageViewController>();
#if DEBUG
        EmailTemplatesDebug.Register();
#endif
    }
}
