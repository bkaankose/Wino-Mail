using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views.Mail.Compose;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterCompose(IServiceCollection services)
    {
        services.AddSingleton<ComposeDetachedDraftRouter>();
        services.AddSingleton<IApplicationLifecycleHook>(provider => provider.GetRequiredService<ComposeDetachedDraftRouter>());
    }
}
