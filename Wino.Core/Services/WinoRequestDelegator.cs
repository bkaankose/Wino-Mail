using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Messages.Synchronization;
using Wino.Core.Requests;

namespace Wino.Core.Services
{
    public class WinoRequestDelegator : IWinoRequestDelegator
    {
        private readonly IWinoRequestProcessor _winoRequestProcessor;
        private readonly IWinoSynchronizerFactory _winoSynchronizerFactory;
        private readonly IFolderService _folderService;
        private readonly IDialogService _dialogService;
        private readonly ILogger _logger = Log.ForContext<WinoRequestDelegator>();

        public WinoRequestDelegator(IWinoRequestProcessor winoRequestProcessor,
                                    IWinoSynchronizerFactory winoSynchronizerFactory,
                                    IFolderService folderService,
                                    IDialogService dialogService)
        {
            _winoRequestProcessor = winoRequestProcessor;
            _winoSynchronizerFactory = winoSynchronizerFactory;
            _folderService = folderService;
            _dialogService = dialogService;
        }

        public async Task ExecuteAsync(MailOperationPreperationRequest request)
        {
            var requests = new List<IRequest>();

            try
            {
                requests = await _winoRequestProcessor.PrepareRequestsAsync(request);
            }
            catch (UnavailableSpecialFolderException unavailableSpecialFolderException)
            {
                _dialogService.InfoBarMessage(Translator.Info_MissingFolderTitle,
                                              string.Format(Translator.Info_MissingFolderMessage, unavailableSpecialFolderException.SpecialFolderType),
                                              InfoBarMessageType.Warning,
                                              Translator.SettingConfigureSpecialFolders_Button,
                                              () =>
                                              {
                                                  _dialogService.HandleSystemFolderConfigurationDialogAsync(unavailableSpecialFolderException.AccountId, _folderService);
                                              });
            }
            catch (InvalidMoveTargetException)
            {
                _dialogService.InfoBarMessage(Translator.Info_InvalidMoveTargetTitle, Translator.Info_InvalidMoveTargetMessage, InfoBarMessageType.Warning);
            }
            catch (NotImplementedException)
            {
                _dialogService.ShowNotSupportedMessage();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Request creation failed.");
                _dialogService.InfoBarMessage(Translator.Info_RequestCreationFailedTitle, ex.Message, InfoBarMessageType.Error);
            }

            if (requests == null || !requests.Any()) return;

            var accountIds = requests.GroupBy(a => a.Item.AssignedAccount.Id);

            // Queue requests for each account and start synchronization.
            foreach (var accountId in accountIds)
            {
                foreach (var accountRequest in accountId)
                {
                    QueueRequest(accountRequest, accountId.Key);
                }

                QueueSynchronization(accountId.Key);
            }
        }

        public async Task ExecuteAsync(FolderOperation operation, IMailItemFolder folderStructure)
        {
            IRequest request = null;

            try
            {
                request = await _winoRequestProcessor.PrepareFolderRequestAsync(operation, folderStructure);
            }
            catch (NotImplementedException)
            {
                _dialogService.ShowNotSupportedMessage();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Folder operation execution failed.");
            }

            // _synchronizationWorker.Queue(request);
        }

        public Task ExecuteAsync(DraftPreperationRequest draftPreperationRequest)
        {
            var request = new CreateDraftRequest(draftPreperationRequest);

            QueueRequest(request, draftPreperationRequest.Account.Id);
            QueueSynchronization(draftPreperationRequest.Account.Id);

            return Task.CompletedTask;
        }

        public Task ExecuteAsync(SendDraftPreparationRequest sendDraftPreperationRequest)
        {
            var request = new SendDraftRequest(sendDraftPreperationRequest);

            QueueRequest(request, sendDraftPreperationRequest.MailItem.AssignedAccount.Id);
            QueueSynchronization(sendDraftPreperationRequest.MailItem.AssignedAccount.Id);

            return Task.CompletedTask;
        }

        private void QueueRequest(IRequest request, Guid accountId)
        {
            var synchronizer = _winoSynchronizerFactory.GetAccountSynchronizer(accountId);

            if (synchronizer == null)
            {
                _logger.Warning("Synchronizer not found for account {AccountId}.", accountId);
                _logger.Warning("Skipping queueing request {Operation}.", request.Operation);

                return;
            }

            synchronizer.QueueRequest(request);
        }

        private void QueueSynchronization(Guid accountId)
        {
            var options = new SynchronizationOptions()
            {
                AccountId = accountId,
                Type = SynchronizationType.ExecuteRequests
            };

            WeakReferenceMessenger.Default.Send(new NewSynchronizationRequested(options));
        }
    }
}
