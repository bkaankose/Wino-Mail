using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad.Mail;
using Wino.Mail.UIKit.Pad.Shell;

namespace Wino.Mail.UIKit.Pad;

public static partial class PadViewRegistration
{
    /// <summary>The iPad shell, its host, its routes and its pages.</summary>
    public static IServiceCollection AddPadShell(this IServiceCollection services)
    {
        services.AddSingleton<IUIKitPageRegistry, PadPageRegistry>();
        services.AddSingleton<PadShellHost>();
        services.AddSingleton<IUIKitShellHost>(provider => provider.GetRequiredService<PadShellHost>());
        services.AddTransient<PadShellViewController>();

        // Mail.
        services.AddSingleton<PadMailMovePresenter>();
        services.AddSingleton<IMailMovePresenter>(provider => provider.GetRequiredService<PadMailMovePresenter>());
        services.AddTransient<MailListPageViewController>();
        RegisterContactsViews(services);
        RegisterTasksViews(services);
        RegisterCalendarViews(services);
        return services;
    }

    static partial void RegisterCalendarViews(IServiceCollection services);
}
