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
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Category;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Messaging.Server;

namespace Wino.Core.Services;

public class WinoRequestDelegator : IWinoRequestDelegator
{
    private readonly IWinoRequestProcessor _winoRequestProcessor;
    private readonly IFolderService _folderService;
    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;
    private readonly ICalendarService _calendarService;
    private readonly ISmimeService _smimeService;

    public WinoRequestDelegator(IWinoRequestProcessor winoRequestProcessor,
                                IFolderService folderService,
                                IMailDialogService dialogService,
                                IAccountService accountService,
                                ICalendarService calendarService,
                                ISmimeService smimeService)
    {
        _winoRequestProcessor = winoRequestProcessor;
        _folderService = folderService;
        _dialogService = dialogService;
        _accountService = accountService;
        _calendarService = calendarService;
        _smimeService = smimeService;
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

        var accountGroups = requests
            .GroupBy(a => a.Item.AssignedAccount.Id)
            .ToDictionary(
                group => group.Key,
                group => group.Cast<IRequestBase>().ToList());

        await QueueRequestPackAsync(accountGroups).ConfigureAwait(false);

        // Queue requests for each account and start synchronization.
        foreach (var accountGroup in requests.GroupBy(a => a.Item.AssignedAccount.Id))
        {
            await QueueSynchronizationAsync(accountGroup.Key);
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

        if (folderRequest.Action is FolderOperation.Delete or FolderOperation.CreateSubFolder or FolderOperation.CreateRootFolder)
        {
            await QueueFoldersOnlySynchronizationAsync(accountId);
        }
    }

    public async Task ExecuteAsync(MailCategoryOperationRequest categoryOperationRequest)
    {
        if (categoryOperationRequest?.Category == null)
            return;

        IRequestBase request = categoryOperationRequest.ChangeType switch
        {
            MailCategoryChangeType.Create => new MailCategoryCreateRequest(categoryOperationRequest.Category),
            MailCategoryChangeType.Update => new MailCategoryUpdateRequest(categoryOperationRequest.Category,
                                                                           categoryOperationRequest.PreviousName,
                                                                           categoryOperationRequest.PreviousRemoteId,
                                                                           categoryOperationRequest.AffectedMessages),
            MailCategoryChangeType.Delete => new MailCategoryDeleteRequest(categoryOperationRequest.Category,
                                                                           categoryOperationRequest.PreviousRemoteId,
                                                                           categoryOperationRequest.AffectedMessages),
            _ => null
        };

        if (request == null)
            return;

        await QueueRequestAsync(request, categoryOperationRequest.AccountId).ConfigureAwait(false);
        await QueueSynchronizationAsync(categoryOperationRequest.AccountId).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(MailCategoryAssignmentOperationRequest categoryAssignmentRequest)
    {
        var requests = categoryAssignmentRequest?.Targets?
            .Where(target => target?.Item != null)
            .Select(target => (IRequestBase)new MailCategoryAssignmentRequest(target.Item,
                                                                              categoryAssignmentRequest.CategoryId,
                                                                              categoryAssignmentRequest.CategoryName,
                                                                              target.CategoryNames,
                                                                              categoryAssignmentRequest.IsAssigned))
            .ToList() ?? [];

        if (requests.Count == 0)
            return;

        await QueueRequestsAsync(requests, categoryAssignmentRequest.AccountId).ConfigureAwait(false);
        await QueueSynchronizationAsync(categoryAssignmentRequest.AccountId).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(DraftPreparationRequest draftPreperationRequest)
    {
        var request = new CreateDraftRequest(draftPreperationRequest);
        var accountId = draftPreperationRequest.Account.Id;

        await QueueRequestAsync(request, accountId);
        await QueueSynchronizationAsync(accountId);
    }

    public async Task ExecuteAsync(SendDraftPreparationRequest sendDraftPreperationRequest)
    {
        // The UI prepares the message unprotected and only sets the S/MIME flags;
        // all cryptography runs here in the companion process.
        if (sendDraftPreperationRequest.SmimeSign || sendDraftPreperationRequest.SmimeEncrypt)
        {
            var protectedBase64Mime = await _smimeService.ApplyDraftSecurityAsync(
                sendDraftPreperationRequest.Base64MimeMessage,
                sendDraftPreperationRequest.SmimeSign,
                sendDraftPreperationRequest.SmimeEncrypt,
                sendDraftPreperationRequest.SmimeSigningCertificateThumbprint).ConfigureAwait(false);

            sendDraftPreperationRequest = sendDraftPreperationRequest with { Base64MimeMessage = protectedBase64Mime };
        }

        var request = new SendDraftRequest(sendDraftPreperationRequest);
        var account = sendDraftPreperationRequest.MailItem.AssignedAccount;

        await QueueRequestAsync(request, account.Id);
        await QueueSynchronizationAsync(account.Id);
    }

    public async Task ExecuteAsync(CalendarOperationPreparationRequest calendarPreparationRequest)
    {
        if (calendarPreparationRequest == null)
            return;

        var resolvedCalendar = await ResolveCalendarAsync(calendarPreparationRequest).ConfigureAwait(false);
        if (resolvedCalendar?.IsReadOnly == true)
        {
            _dialogService.ShowReadOnlyCalendarMessage();
            return;
        }

        IRequestBase request = calendarPreparationRequest.Operation switch
        {
            CalendarSynchronizerOperation.CreateEvent => await CreateCalendarEventRequestAsync(calendarPreparationRequest).ConfigureAwait(false),
            CalendarSynchronizerOperation.DeleteEvent => new DeleteCalendarEventRequest(calendarPreparationRequest.CalendarItem),
            CalendarSynchronizerOperation.AcceptEvent => new AcceptEventRequest(calendarPreparationRequest.CalendarItem, calendarPreparationRequest.ResponseMessage),
            CalendarSynchronizerOperation.DeclineEvent => CreateDeclineRequest(calendarPreparationRequest.CalendarItem, calendarPreparationRequest.ResponseMessage),
            CalendarSynchronizerOperation.TentativeEvent => new TentativeEventRequest(calendarPreparationRequest.CalendarItem, calendarPreparationRequest.ResponseMessage),
            CalendarSynchronizerOperation.UpdateEvent => new UpdateCalendarEventRequest(calendarPreparationRequest.CalendarItem, calendarPreparationRequest.Attendees)
            {
                OriginalItem = calendarPreparationRequest.OriginalItem,
                OriginalAttendees = calendarPreparationRequest.OriginalAttendees
            },
            CalendarSynchronizerOperation.ChangeStartAndEndDate => new ChangeStartAndEndDateRequest(calendarPreparationRequest.CalendarItem, calendarPreparationRequest.Attendees)
            {
                OriginalItem = calendarPreparationRequest.OriginalItem,
                OriginalAttendees = calendarPreparationRequest.OriginalAttendees
            },
            _ => throw new NotImplementedException($"Calendar operation {calendarPreparationRequest.Operation} is not implemented yet.")
        };

        if (request == null)
            return;

        var accountId = calendarPreparationRequest.Operation == CalendarSynchronizerOperation.CreateEvent
            ? calendarPreparationRequest.ComposeResult.AccountId
            : calendarPreparationRequest.CalendarItem.AssignedCalendar.AccountId;
        var accountName = calendarPreparationRequest.Operation == CalendarSynchronizerOperation.CreateEvent
            ? null
            : calendarPreparationRequest.CalendarItem.AssignedCalendar.MailAccount?.Name;

        await QueueRequestAsync(request, accountId);
        await QueueCalendarSynchronizationAsync(accountId);
    }

    public async Task ExecuteAsync(Guid accountId, IEnumerable<IRequestBase> requests)
    {
        var requestList = requests?.Where(a => a != null).ToList() ?? [];
        if (requestList.Count == 0)
            return;

        await QueueRequestsAsync(requestList, accountId).ConfigureAwait(false);

        await QueueSynchronizationAsync(accountId).ConfigureAwait(false);

        if (requestList.Any(r => r is DeleteFolderRequest or CreateSubFolderRequest or CreateRootFolderRequest))
        {
            await QueueFoldersOnlySynchronizationAsync(accountId).ConfigureAwait(false);
        }
    }

    private async Task<IRequestBase> CreateCalendarEventRequestAsync(CalendarOperationPreparationRequest calendarPreparationRequest)
    {
        var composeResult = calendarPreparationRequest.ComposeResult
                            ?? throw new InvalidOperationException("Create event requests require a compose result.");
        var assignedCalendar = await _calendarService.GetAccountCalendarAsync(composeResult.CalendarId).ConfigureAwait(false);

        if (assignedCalendar == null)
            throw new InvalidOperationException($"Calendar {composeResult.CalendarId} could not be resolved.");

        return new CreateCalendarEventRequest(composeResult, assignedCalendar);
    }

    private async Task<AccountCalendar> ResolveCalendarAsync(CalendarOperationPreparationRequest calendarPreparationRequest)
    {
        if (calendarPreparationRequest.Operation == CalendarSynchronizerOperation.CreateEvent)
        {
            var calendarId = calendarPreparationRequest.ComposeResult?.CalendarId ?? Guid.Empty;
            return calendarId == Guid.Empty
                ? null
                : await _calendarService.GetAccountCalendarAsync(calendarId).ConfigureAwait(false);
        }

        if (calendarPreparationRequest.CalendarItem?.AssignedCalendar is AccountCalendar assignedCalendar)
            return assignedCalendar;

        var fallbackCalendarId = calendarPreparationRequest.CalendarItem?.CalendarId ?? Guid.Empty;
        return fallbackCalendarId == Guid.Empty
            ? null
            : await _calendarService.GetAccountCalendarAsync(fallbackCalendarId).ConfigureAwait(false);
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
        await SynchronizationManager.Instance.QueueRequestAsync(request, accountId, triggerSynchronization: false).ConfigureAwait(false);
    }

    private async Task QueueRequestsAsync(IEnumerable<IRequestBase> requests, Guid accountId)
    {
        await SynchronizationManager.Instance.QueueRequestsAsync(requests, accountId, triggerSynchronization: false).ConfigureAwait(false);
    }

    private async Task QueueRequestPackAsync(IReadOnlyDictionary<Guid, List<IRequestBase>> requestsByAccount)
    {
        await SynchronizationManager.Instance.QueueRequestPackAsync(requestsByAccount, triggerSynchronization: false).ConfigureAwait(false);
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

    private Task QueueFoldersOnlySynchronizationAsync(Guid accountId)
    {
        var options = new MailSynchronizationOptions()
        {
            AccountId = accountId,
            Type = MailSynchronizationType.FoldersOnly
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
