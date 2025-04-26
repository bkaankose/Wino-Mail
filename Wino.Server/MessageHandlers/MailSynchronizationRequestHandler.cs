using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Server;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers;

/// <summary>
/// Handler for NewMailSynchronizationRequested from the client.
/// </summary>
public class MailSynchronizationRequestHandler : ServerMessageHandler<NewMailSynchronizationRequested, MailSynchronizationResult>
{
    public override WinoServerResponse<MailSynchronizationResult> FailureDefaultResponse(Exception ex)
        => WinoServerResponse<MailSynchronizationResult>.CreateErrorResponse(ex.Message);

    private readonly ISynchronizerFactory _synchronizerFactory;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;

    public MailSynchronizationRequestHandler(ISynchronizerFactory synchronizerFactory,
                                         INotificationBuilder notificationBuilder,
                                         IAccountService accountService,
                                         IFolderService folderService)
    {
        _synchronizerFactory = synchronizerFactory;
        _notificationBuilder = notificationBuilder;
        _accountService = accountService;
        _folderService = folderService;
    }

    protected override async Task<WinoServerResponse<MailSynchronizationResult>> HandleAsync(NewMailSynchronizationRequested message, CancellationToken cancellationToken = default)
    {
        var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(message.Options.AccountId);

        // 1. Don't send message for sync completion when we execute requests.
        // People are usually interested in seeing the notification after they trigger the synchronization.

        // 2. Don't send message for sync completion when we are synchronizing from the server.
        // It happens very common and there is no need to send a message for each synchronization.

        bool shouldReportSynchronizationResult =
            message.Options.Type != MailSynchronizationType.ExecuteRequests &&
            message.Options.Type != MailSynchronizationType.IMAPIdle &&
            message.Source == SynchronizationSource.Client;

        try
        {
            var synchronizationResult = await synchronizer.SynchronizeMailsAsync(message.Options, cancellationToken).ConfigureAwait(false);

            bool isNotificationsEnabled = await _accountService.IsNotificationsEnabled(synchronizer.Account.Id).ConfigureAwait(false);

            if (isNotificationsEnabled && (synchronizationResult.DownloadedMessages?.Any() ?? false))
            {
                var accountInboxFolder = await _folderService.GetSpecialFolderByAccountIdAsync(message.Options.AccountId, SpecialFolderType.Inbox);

                if (accountInboxFolder != null)
                {
                    await _notificationBuilder.CreateNotificationsAsync(accountInboxFolder.Id, synchronizationResult.DownloadedMessages);
                }
            }

            var isSynchronizationSucceeded = synchronizationResult.CompletedState == SynchronizationCompletedState.Success;

            // IDLE requests might be canceled successfully.
            if (message.Options.Type == MailSynchronizationType.IMAPIdle && synchronizationResult.CompletedState == SynchronizationCompletedState.Canceled)
            {
                isSynchronizationSucceeded = true;
            }

            // Update badge count of the notification task.
            if (isSynchronizationSucceeded)
            {
                await _notificationBuilder.UpdateTaskbarIconBadgeAsync();
            }

            if (shouldReportSynchronizationResult)
            {
                var completedMessage = new AccountSynchronizationCompleted(message.Options.AccountId,
                                                                           isSynchronizationSucceeded ? SynchronizationCompletedState.Success : SynchronizationCompletedState.Failed,
                                                                           message.Options.GroupedSynchronizationTrackingId);

                WeakReferenceMessenger.Default.Send(completedMessage);
            }

            return WinoServerResponse<MailSynchronizationResult>.CreateSuccessResponse(synchronizationResult);
        }
        // TODO: Following cases might always be thrown from server. Handle them properly.

        //catch (AuthenticationAttentionException)
        //{
        //    // TODO
        //    // await SetAccountAttentionAsync(accountId, AccountAttentionReason.InvalidCredentials);
        //}
        //catch (SystemFolderConfigurationMissingException)
        //{
        //    // TODO
        //    // await SetAccountAttentionAsync(accountId, AccountAttentionReason.MissingSystemFolderConfiguration);
        //}
        catch (Exception)
        {
            throw;
        }
    }
}
