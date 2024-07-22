﻿using System;
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
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests;
using Wino.Messaging.Client.Synchronization;

namespace Wino.Core.Services
{
    public class WinoRequestDelegator : IWinoRequestDelegator
    {
        private readonly IWinoRequestProcessor _winoRequestProcessor;
        private readonly IWinoServerConnectionManager _winoServerConnectionManager;
        private readonly IFolderService _folderService;
        private readonly IDialogService _dialogService;
        private readonly ILogger _logger = Log.ForContext<WinoRequestDelegator>();

        public WinoRequestDelegator(IWinoRequestProcessor winoRequestProcessor,
                                    IWinoServerConnectionManager winoServerConnectionManager,
                                    IFolderService folderService,
                                    IDialogService dialogService)
        {
            _winoRequestProcessor = winoRequestProcessor;
            _winoServerConnectionManager = winoServerConnectionManager;
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

        public async Task ExecuteAsync(FolderOperationPreperationRequest folderRequest)
        {
            if (folderRequest == null || folderRequest.Folder == null) return;

            IRequestBase request = null;

            var accountId = folderRequest.Folder.MailAccountId;

            try
            {
                request = await _winoRequestProcessor.PrepareFolderRequestAsync(folderRequest);
            }
            catch (NotImplementedException)
            {
                _dialogService.ShowNotSupportedMessage();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Folder operation execution failed.");
            }

            if (request == null) return;

            QueueRequest(request, accountId);
            QueueSynchronization(accountId);
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

        private void QueueRequest(IRequestBase request, Guid accountId)
            => _winoServerConnectionManager.QueueRequest(request, accountId);

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
