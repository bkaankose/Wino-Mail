using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Views.Mail;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Intelligence;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Mail mode views and ViewModels. Owned by the Mail feature. Lifetimes follow the Windows App.xaml.cs registrations.</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterMailViews(IServiceCollection services)
    {
        // The reader ViewModel needs the appearance; Windows registers this in its platform container.
        services.TryAddSingleton<IUnderlyingThemeService, MacUnderlyingThemeService>();

        services.AddTransient<MailListPageViewModel>();
        services.AddTransient<MailRenderingPageViewModel>();
        services.AddTransient<ComposePageViewModel>();
        services.AddTransient<IdlePageViewModel>();
        services.AddTransient<WinoIntelligenceHeaderPresenter>();

        services.AddTransient<MailListPageViewController>();
        services.AddTransient<MailRenderingPageViewController>();
        services.AddTransient<ComposePageViewController>();
        services.AddTransient<IdlePageViewController>();
    }
}
