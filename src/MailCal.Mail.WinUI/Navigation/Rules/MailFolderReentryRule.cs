#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Navigation;
using MailCal.Mail.ViewModels.Messages;
using MailCal.Messaging.Client.Mails;

namespace MailCal.Mail.WinUI.Navigation.Rules;

/// <summary>
/// Selecting another folder while the mail list is already on screen must not rebuild the
/// page. The list reloads for the new folder and the reading pane is released instead.
/// </summary>
public sealed class MailFolderReentryRule : INavigationReentryRule
{
    public MailCalPage Page => MailCalPage.MailListPage;

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
