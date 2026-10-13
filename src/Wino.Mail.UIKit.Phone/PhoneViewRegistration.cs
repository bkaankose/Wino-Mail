using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Phone.Mail;

namespace Wino.Mail.UIKit.Phone;

public static partial class PhoneViewRegistration
{
    /// <summary>The iPhone shell, its host, its routes and its pages.</summary>
    public static IServiceCollection AddPhoneShell(this IServiceCollection services)
    {
        services.AddSingleton<IUIKitPageRegistry, PhonePageRegistry>();
        services.AddSingleton<PhoneShellHost>();
        services.AddSingleton<IUIKitShellHost>(provider => provider.GetRequiredService<PhoneShellHost>());
        services.AddTransient<PhoneShellViewController>();

        // Mail flow (Mailboxes is created by the shell as the Mail stack's root). The reader's Move opens the list's Move sheet.
        services.AddTransient<MailListPageViewController>();
        services.AddSingleton<IMailMovePresenter, PhoneMailMovePresenter>();
        RegisterContactsViews(services);
        RegisterTasksViews(services);
#if DEBUG
        // Simulator aids for the phone Mail flow (Mailboxes rows, native pops, the reader toolbar).
        services.AddSingleton<Wino.Mail.ViewModels.Lifecycle.IApplicationLifecycleHook, PhoneMailDebugHook>();
#endif
        RegisterCalendarViews(services);
        return services;
    }

    static partial void RegisterCalendarViews(IServiceCollection services);
}
