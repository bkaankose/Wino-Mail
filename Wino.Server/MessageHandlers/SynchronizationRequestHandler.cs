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

namespace Wino.Server.MessageHandlers
{
    /// <summary>
    /// Handler for NewSynchronizationRequested from the client.
    /// </summary>
    public class SynchronizationRequestHandler : ServerMessageHandler<NewSynchronizationRequested, SynchronizationResult>
    {
        public override WinoServerResponse<SynchronizationResult> FailureDefaultResponse(Exception ex)
            => WinoServerResponse<SynchronizationResult>.CreateErrorResponse(ex.Message);

        private readonly ISynchronizerFactory _synchronizerFactory;
        private readonly INotificationBuilder _notificationBuilder;
        private readonly IFolderService _folderService;

        public SynchronizationRequestHandler(ISynchronizerFactory synchronizerFactory,
                                             INotificationBuilder notificationBuilder,
                                             IFolderService folderService)
        {
            _synchronizerFactory = synchronizerFactory;
            _notificationBuilder = notificationBuilder;
            _folderService = folderService;
        }

        protected override async Task<WinoServerResponse<SynchronizationResult>> HandleAsync(NewSynchronizationRequested message, CancellationToken cancellationToken = default)
        {
            var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(message.Options.AccountId);

            // 1. Don't send message for sync completion when we execute requests.
            // People are usually interested in seeing the notification after they trigger the synchronization.

            // 2. Don't send message for sync completion when we are synchronizing from the server.
            // It happens very common and there is no need to send a message for each synchronization.

            bool shouldReportSynchronizationResult =
                message.Options.Type != SynchronizationType.ExecuteRequests &&
                message.Source == SynchronizationSource.Client;

            try
            {
                var synchronizationResult = await synchronizer.SynchronizeAsync(message.Options, cancellationToken).ConfigureAwait(false);

                if (synchronizationResult.DownloadedMessages?.Any() ?? false || !synchronizer.Account.Preferences.IsNotificationsEnabled)
                {
                    var accountInboxFolder = await _folderService.GetSpecialFolderByAccountIdAsync(message.Options.AccountId, SpecialFolderType.Inbox);

                    if (accountInboxFolder != null)
                    {
                        await _notificationBuilder.CreateNotificationsAsync(accountInboxFolder.Id, synchronizationResult.DownloadedMessages);
                    }
                }

                var isSynchronizationSucceeded = synchronizationResult.CompletedState == SynchronizationCompletedState.Success;

                if (shouldReportSynchronizationResult)
                {
                    var completedMessage = new AccountSynchronizationCompleted(message.Options.AccountId,
                                                                               isSynchronizationSucceeded ? SynchronizationCompletedState.Success : SynchronizationCompletedState.Failed,
                                                                               message.Options.GroupedSynchronizationTrackingId);

                    WeakReferenceMessenger.Default.Send(completedMessage);
                }

                return WinoServerResponse<SynchronizationResult>.CreateSuccessResponse(synchronizationResult);
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
}
