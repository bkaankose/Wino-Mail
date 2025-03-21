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
using Wino.Core.Requests.Mail;
using Wino.Messaging.Server;

namespace Wino.Core.Services;

public class WinoRequestDelegator : IWinoRequestDelegator
{
    private readonly IWinoRequestProcessor _winoRequestProcessor;
    private readonly IWinoServerConnectionManager _winoServerConnectionManager;
    private readonly IFolderService _folderService;
    private readonly IMailDialogService _dialogService;

    public WinoRequestDelegator(IWinoRequestProcessor winoRequestProcessor,
                                IWinoServerConnectionManager winoServerConnectionManager,
                                IFolderService folderService,
                                IMailDialogService dialogService)
    {
        _winoRequestProcessor = winoRequestProcessor;
        _winoServerConnectionManager = winoServerConnectionManager;
        _folderService = folderService;
        _dialogService = dialogService;
    }

    public async Task ExecuteAsync(MailOperationPreperationRequest request)
    {
        var requests = new List<IMailActionRequest>();

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
        catch (InvalidMoveTargetException invalidMoveTargetException)
        {
            switch (invalidMoveTargetException.Reason)
            {
                case InvalidMoveTargetReason.NonMoveTarget:
                    _dialogService.InfoBarMessage(Translator.Info_InvalidMoveTargetTitle, Translator.Info_InvalidMoveTargetMessage, InfoBarMessageType.Warning);
                    break;
                case InvalidMoveTargetReason.MultipleAccounts:
                    _dialogService.InfoBarMessage(Translator.Info_InvalidMoveTargetTitle, Translator.Exception_InvalidMultiAccountMoveTarget, InfoBarMessageType.Warning);
                    break;
                default:
                    break;
            }
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
                await QueueRequestAsync(accountRequest, accountId.Key);
            }

            await QueueSynchronizationAsync(accountId.Key);
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

        await QueueRequestAsync(request, accountId);
        await QueueSynchronizationAsync(accountId);
    }

    public async Task ExecuteAsync(DraftPreparationRequest draftPreperationRequest)
    {
        var request = new CreateDraftRequest(draftPreperationRequest);

        await QueueRequestAsync(request, draftPreperationRequest.Account.Id);
        await QueueSynchronizationAsync(draftPreperationRequest.Account.Id);
    }

    public async Task ExecuteAsync(SendDraftPreparationRequest sendDraftPreperationRequest)
    {
        var request = new SendDraftRequest(sendDraftPreperationRequest);

        await QueueRequestAsync(request, sendDraftPreperationRequest.MailItem.AssignedAccount.Id);
        await QueueSynchronizationAsync(sendDraftPreperationRequest.MailItem.AssignedAccount.Id);
    }

    private async Task QueueRequestAsync(IRequestBase request, Guid accountId)
    {
        try
        {
            await EnsureServerConnectedAsync();
            await _winoServerConnectionManager.QueueRequestAsync(request, accountId);
        }
        catch (WinoServerException serverException)
        {
            _dialogService.InfoBarMessage("Wino Server Exception", serverException.Message, InfoBarMessageType.Error);
        }
    }

    private async Task QueueSynchronizationAsync(Guid accountId)
    {
        await EnsureServerConnectedAsync();

        var options = new MailSynchronizationOptions()
        {
            AccountId = accountId,
            Type = MailSynchronizationType.ExecuteRequests
        };

        WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(options, SynchronizationSource.Client));
    }

    private async Task EnsureServerConnectedAsync()
    {
        if (_winoServerConnectionManager.Status == WinoServerConnectionStatus.Connected) return;

        await _winoServerConnectionManager.ConnectAsync();
    }
}
