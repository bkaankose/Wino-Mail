using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Mail;
using Wino.Messaging.Server;

namespace Wino.Core.Services;

public class WinoRequestDelegator : IWinoRequestDelegator
{
    private readonly IWinoRequestProcessor _winoRequestProcessor;
    private readonly IFolderService _folderService;
    private readonly IMailDialogService _dialogService;

    public WinoRequestDelegator(IWinoRequestProcessor winoRequestProcessor,
                                IFolderService folderService,
                                IMailDialogService dialogService)
    {
        _winoRequestProcessor = winoRequestProcessor;
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

    public async Task ExecuteAsync(CalendarOperationPreparationRequest calendarPreparationRequest)
    {
        IRequestBase request = calendarPreparationRequest.Operation switch
        {
            CalendarSynchronizerOperation.CreateEvent => new CreateCalendarEventRequest(calendarPreparationRequest.CalendarItem, calendarPreparationRequest.Attendees),
            CalendarSynchronizerOperation.DeleteEvent => new DeleteCalendarEventRequest(calendarPreparationRequest.CalendarItem),
            CalendarSynchronizerOperation.AcceptEvent => new AcceptEventRequest(calendarPreparationRequest.CalendarItem, calendarPreparationRequest.ResponseMessage),
            CalendarSynchronizerOperation.DeclineEvent => CreateDeclineRequest(calendarPreparationRequest.CalendarItem, calendarPreparationRequest.ResponseMessage),
            CalendarSynchronizerOperation.TentativeEvent => new TentativeEventRequest(calendarPreparationRequest.CalendarItem, calendarPreparationRequest.ResponseMessage),
            // Future support for update operations
            // CalendarSynchronizerOperation.UpdateEvent => new UpdateCalendarEventRequest(calendarPreparationRequest.CalendarItem, calendarPreparationRequest.Attendees),
            _ => throw new NotImplementedException($"Calendar operation {calendarPreparationRequest.Operation} is not implemented yet.")
        };

        await QueueRequestAsync(request, calendarPreparationRequest.CalendarItem.AssignedCalendar.AccountId);
        await QueueCalendarSynchronizationAsync(calendarPreparationRequest.CalendarItem.AssignedCalendar.AccountId);
    }

    private IRequestBase CreateDeclineRequest(CalendarItem calendarItem, string responseMessage)
    {
        // For Outlook accounts, declined events are deleted by the server after synchronization.
        // Use OutlookDeclineEventRequest to handle UI removal.
        if (calendarItem.AssignedCalendar?.MailAccount?.ProviderType == MailProviderType.Outlook)
        {
            return new OutlookDeclineEventRequest(calendarItem, responseMessage);
        }

        return new DeclineEventRequest(calendarItem, responseMessage);
    }

    private async Task QueueRequestAsync(IRequestBase request, Guid accountId)
    {
        // Don't trigger synchronization for individual requests - we'll trigger it once for all requests
        await SynchronizationManager.Instance.QueueRequestAsync(request, accountId, triggerSynchronization: false);
    }

    private Task QueueSynchronizationAsync(Guid accountId)
    {
        var options = new MailSynchronizationOptions()
        {
            AccountId = accountId,
            Type = MailSynchronizationType.ExecuteRequests
        };

        WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(options));
        return Task.CompletedTask;
    }

    private Task QueueCalendarSynchronizationAsync(Guid accountId)
    {
        var options = new CalendarSynchronizationOptions()
        {
            AccountId = accountId,
            Type = CalendarSynchronizationType.ExecuteRequests
        };

        WeakReferenceMessenger.Default.Send(new NewCalendarSynchronizationRequested(options));
        return Task.CompletedTask;
    }
}
