using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.UIKit.Views.Extras;

/// <summary>
/// Creates the shell-extras presenters once services are ready, so What's New answers <c>WhatsNewOpenRequested</c>
/// (About page) and the briefing badge state is known before a shell asks for it (Mac: the shell resolves them).
/// </summary>
internal sealed class ShellExtrasLifecycleHook(IServiceProvider services) : IApplicationLifecycleHook
{
    public Task ServicesReadyAsync()
    {
        services.GetRequiredService<IUIKitWhatsNewPresenter>();
        services.GetRequiredService<IUIKitDailyBriefingPresenter>();
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted) => Task.CompletedTask;
}
