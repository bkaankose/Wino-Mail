using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterAccounts(IServiceCollection services)
    {
        services.AddSingleton(provider => new ExternalBrowserAuthPresenter(provider.GetRequiredService<IDispatcher>(),
            () => provider.GetRequiredService<GtkDialogService>().ResolveOwner(), provider.GetRequiredService<IClipboardService>()));
        services.AddSingleton<IExternalBrowserAuthenticationPresenter>(provider => provider.GetRequiredService<ExternalBrowserAuthPresenter>());
        services.AddSingleton(provider => new GtkEmbeddedBrowserAuthBroker(provider.GetRequiredService<IDispatcher>(),
            () => provider.GetRequiredService<GtkDialogService>().ResolveOwner()));
        services.AddSingleton<IEmbeddedBrowserAuthBroker>(provider => provider.GetRequiredService<GtkEmbeddedBrowserAuthBroker>());
#if DEBUG
        Diagnostics.UriCaptureLauncher.Register(services);
#endif
    }
}
