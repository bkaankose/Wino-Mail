using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Mails;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Mail.ViewModels.Messages;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// Hosts a draft the current list does not show (Mac <c>MailListPageViewController.ReadingPane</c>) and returns the reading
/// pane to idle when a composer finished sending or discarding.
/// </summary>
internal sealed class ComposeDetachedDraftRouter(IMessenger messenger, INavigationService navigation, IDispatcher dispatcher)
    : IApplicationLifecycleHook, IRecipient<ComposeDetachedDraftRequested>, IRecipient<DisposeRenderingFrameRequested>
{
    public Task ServicesReadyAsync()
    {
        messenger.Register<ComposeDetachedDraftRequested>(this);
        messenger.Register<DisposeRenderingFrameRequested>(this);
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted)
    {
        messenger.UnregisterAll(this);
        return Task.CompletedTask;
    }

    public void Receive(ComposeDetachedDraftRequested message)
    {
        if (message.Draft is not { } draft) return;
        _ = dispatcher.ExecuteOnUIThread(() =>
        {
            messenger.Send(new ClearMailSelectionsRequested());
            navigation.Navigate(WinoPage.ComposePage, draft, NavigationReferenceFrame.RenderingFrame);
        });
    }

    public void Receive(DisposeRenderingFrameRequested message)
        => _ = dispatcher.ExecuteOnUIThread(() => navigation.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame));
}
