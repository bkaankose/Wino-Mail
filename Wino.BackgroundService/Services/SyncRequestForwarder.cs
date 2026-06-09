using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Messaging;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.BackgroundService.Services;

/// <summary>
/// Companion-internal bridge for the legacy server messages: WinoRequestDelegator and
/// ImapSynchronizer publish NewMail/NewCalendarSynchronizationRequested on the local
/// messenger after queueing work; this recipient turns them into direct synchronization
/// manager calls and reports completion back to the UI as a forwarded UI message.
/// </summary>
public sealed class SyncRequestForwarder :
    IRecipient<NewMailSynchronizationRequested>,
    IRecipient<NewCalendarSynchronizationRequested>
{
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IAccountService _accountService;
    private readonly ILogger _logger = Log.ForContext<SyncRequestForwarder>();

    public SyncRequestForwarder(ISynchronizationManager synchronizationManager, IAccountService accountService)
    {
        _synchronizationManager = synchronizationManager;
        _accountService = accountService;

        WeakReferenceMessenger.Default.Register<NewMailSynchronizationRequested>(this);
        WeakReferenceMessenger.Default.Register<NewCalendarSynchronizationRequested>(this);
    }

    public async void Receive(NewMailSynchronizationRequested message)
    {
        MailSynchronizationResult syncResult;

        try
        {
            syncResult = await _synchronizationManager.SynchronizeMailAsync(message.Options).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Requested mail synchronization failed.");
            syncResult = MailSynchronizationResult.Failed(exception);
        }

        UIMessagePublisherProvider.Current.Publish(new AccountSynchronizationCompleted(
            message.Options.AccountId,
            syncResult.CompletedState,
            message.Options.GroupedSynchronizationTrackingId,
            message.Options.Type));

        if (syncResult.CompletedState is SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted)
        {
            await ClearInvalidCredentialAttentionIfNeededAsync(message.Options.AccountId).ConfigureAwait(false);
        }
    }

    public async void Receive(NewCalendarSynchronizationRequested message)
    {
        try
        {
            await _synchronizationManager.SynchronizeCalendarAsync(message.Options).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Requested calendar synchronization failed.");
        }
    }

    private async Task ClearInvalidCredentialAttentionIfNeededAsync(Guid accountId)
    {
        try
        {
            var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);

            if (account?.AttentionReason != AccountAttentionReason.InvalidCredentials)
                return;

            await _accountService.ClearAccountAttentionAsync(accountId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to clear account attention after synchronization.");
        }
    }
}
