#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Messages;
using Wino.Messaging.Client.Mails;

namespace Wino.Mail.WinUI.Navigation.Rules;

/// <summary>
/// Selecting another folder while the mail list is already on screen must not rebuild the
/// page. The list reloads for the new folder and the reading pane is released instead.
/// </summary>
public sealed class MailFolderReentryRule : INavigationReentryRule
{
    public WinoPage Page => WinoPage.MailListPage;

    public ReentryDecision Evaluate(NavigationContext context)
    {
        if (!context.IsTargetActive || context.Parameter is not NavigateMailFolderEventArgs folderArgs)
            return ReentryDecision.Navigate();

        return ReentryDecision.HandleInPlace(() =>
        {
            WeakReferenceMessenger.Default.Send(
                new ActiveMailFolderChangedEvent(folderArgs.BaseFolderMenuItem, folderArgs.FolderInitLoadAwaitTask));
            WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());
        });
    }
}
