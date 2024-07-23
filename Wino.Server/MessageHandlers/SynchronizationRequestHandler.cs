using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
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
        public override SynchronizationResult FailureDefaultResponse(Exception ex) => SynchronizationResult.Failed(ex);

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

        protected override async Task<SynchronizationResult> HandleAsync(NewSynchronizationRequested message, CancellationToken cancellationToken = default)
        {
            var synchronizer = await _synchronizerFactory.GetAccountSynchronizerAsync(message.Options.AccountId);

            bool shouldReportSynchronizationResult = message.Options.Type != SynchronizationType.ExecuteRequests;

            var synchronizationResult = await synchronizer.SynchronizeAsync(message.Options, cancellationToken).ConfigureAwait(false);

            if (synchronizationResult.DownloadedMessages.Any())
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
                WeakReferenceMessenger.Default.Send(new AccountSynchronizationCompleted(message.Options.AccountId,
                                                                       isSynchronizationSucceeded ? SynchronizationCompletedState.Success : SynchronizationCompletedState.Failed,
                                                                       message.Options.GroupedSynchronizationTrackingId));
            }

            return synchronizationResult;
        }
    }
}
