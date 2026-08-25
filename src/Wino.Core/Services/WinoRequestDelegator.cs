using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Contact;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.Mail;
using Wino.Messaging.Server;

namespace Wino.Core.Services;

public class WinoRequestDelegator : IWinoRequestDelegator
{
    private readonly IWinoRequestProcessor _winoRequestProcessor;
    private readonly IFolderService _folderService;
    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;
    private readonly ICalendarService _calendarService;
    private readonly IContactService _contactService;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IImapChangeProcessor _imapChangeProcessor;
    private readonly IApplicationConfiguration _applicationConfiguration;

    public WinoRequestDelegator(IWinoRequestProcessor winoRequestProcessor,
                                IFolderService folderService,
                                IMailDialogService dialogService,
                                IAccountService accountService,
                                ICalendarService calendarService,
                                IContactService contactService,
                                ISynchronizationManager synchronizationManager,
                                IImapChangeProcessor imapChangeProcessor,
                                IApplicationConfiguration applicationConfiguration)
    {
        _winoRequestProcessor = winoRequestProcessor;
        _folderService = folderService;
        _dialogService = dialogService;
        _accountService = accountService;
        _calendarService = calendarService;
        _contactService = contactService;
        _synchronizationManager = synchronizationManager;
        _imapChangeProcessor = imapChangeProcessor;
        _applicationConfiguration = applicationConfiguration;
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

        if (folderRequest.Action is FolderOperation.Delete or FolderOperation.CreateSubFolder)
        {
            await QueueFoldersOnlySynchronizationAsync(accountId);
        }
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
            CalendarSynchronizerOperation.UpdateEvent => new UpdateCalendarEventRequest(
                calendarPreparationRequest.CalendarItem,
                calendarPreparationRequest.Attendees,
                calendarPreparationRequest.Reminders)
            {
                OriginalItem = calendarPreparationRequest.OriginalItem,
                OriginalAttendees = calendarPreparationRequest.OriginalAttendees,
                OriginalReminders = calendarPreparationRequest.OriginalReminders
            },
            CalendarSynchronizerOperation.ChangeStartAndEndDate => new ChangeStartAndEndDateRequest(
                calendarPreparationRequest.CalendarItem,
                calendarPreparationRequest.Attendees,
                calendarPreparationRequest.Reminders)
            {
                OriginalItem = calendarPreparationRequest.OriginalItem,
                OriginalAttendees = calendarPreparationRequest.OriginalAttendees,
                OriginalReminders = calendarPreparationRequest.OriginalReminders
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

        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        if (account?.IsCalendarAccessEnabled == true && !account.IsCalendarAccessGranted)
        {
            await ExecuteLocalCalendarRequestAsync(account, request).ConfigureAwait(false);
            return;
        }

        await QueueRequestAsync(request, accountId);
        await QueueCalendarSynchronizationAsync(accountId);
    }

    private async Task ExecuteLocalCalendarRequestAsync(MailAccount account, IRequestBase request)
    {
        var handler = new ImapSynchronizer.LocalCalendarOperationHandler(
            account,
            _imapChangeProcessor,
            _calendarService,
            _applicationConfiguration.ApplicationDataFolderPath,
            "local");

        switch (request)
        {
            case CreateCalendarEventRequest createRequest:
                await handler.CreateCalendarEventAsync(createRequest).ConfigureAwait(false);
                break;
            case ChangeStartAndEndDateRequest changeDateRequest:
                await handler.UpdateCalendarEventAsync(changeDateRequest).ConfigureAwait(false);
                break;
            case UpdateCalendarEventRequest updateRequest:
                await handler.UpdateCalendarEventAsync(updateRequest).ConfigureAwait(false);
                break;
            case DeleteCalendarEventRequest deleteRequest:
                await handler.DeleteCalendarEventAsync(deleteRequest).ConfigureAwait(false);
                break;
            case AcceptEventRequest acceptRequest:
                await handler.AcceptEventAsync(acceptRequest).ConfigureAwait(false);
                break;
            case DeclineEventRequest declineRequest:
                await handler.DeclineEventAsync(declineRequest).ConfigureAwait(false);
                break;
            case TentativeEventRequest tentativeRequest:
                await handler.TentativeEventAsync(tentativeRequest).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"Local calendar request {request.GetType().Name} is not supported.");
        }
    }

    public Task ExecuteAsync(ContactOperationPreparationRequest preparationRequest)
        => ExecuteAsync(preparationRequest is null ? [] : new[] { preparationRequest });

    public async Task ExecuteAsync(IReadOnlyList<ContactOperationPreparationRequest> preparationRequests)
    {
        var valid = preparationRequests?.Where(request => request?.Contact is not null).ToList() ?? [];

        if (valid.Count == 0)
            return;

        foreach (var preparationRequest in valid)
        {
            var contact = preparationRequest.Contact;
            switch (preparationRequest.Operation)
            {
                case ContactSynchronizerOperation.Create:
                    await _contactService.StageCreateAsync(contact).ConfigureAwait(false);
                    break;
                case ContactSynchronizerOperation.Update:
                    await _contactService.StageUpdateAsync(contact).ConfigureAwait(false);
                    break;
                case ContactSynchronizerOperation.Delete:
                    await _contactService.StageDeleteAsync(contact.Id).ConfigureAwait(false);
                    break;
            }

            await QueueRequestAsync(
                new ContactActionRequest(contact, preparationRequest.Operation, preparationRequest.OriginalContact, preparationRequest.Photo),
                contact.MailAccountId).ConfigureAwait(false);
        }

        foreach (var group in valid.GroupBy(request => request.Contact.MailAccountId))
        {
            WeakReferenceMessenger.Default.Send(new NewContactSynchronizationRequested(new ContactSynchronizationOptions
            {
                AccountId = group.Key,
                AddressBookId = group.First().Contact.AddressBookId,
                Type = ContactSynchronizationType.ExecuteRequests
            }));
        }
    }

    public async Task ExecuteAsync(Guid accountId, IEnumerable<IRequestBase> requests)
    {
        var requestList = requests?.Where(a => a != null).ToList() ?? [];
        if (requestList.Count == 0)
            return;

        await QueueRequestsAsync(requestList, accountId).ConfigureAwait(false);

        PublishSynchronizationRequests(accountId, requestList);
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
        await _synchronizationManager.QueueRequestAsync(request, accountId, triggerSynchronization: false).ConfigureAwait(false);
    }

    private async Task QueueRequestsAsync(IEnumerable<IRequestBase> requests, Guid accountId)
    {
        await _synchronizationManager.QueueRequestPackAsync(
            new Dictionary<Guid, List<IRequestBase>>
            {
                [accountId] = requests.ToList()
            },
            triggerSynchronization: false).ConfigureAwait(false);
    }

    private async Task QueueRequestPackAsync(IReadOnlyDictionary<Guid, List<IRequestBase>> requestsByAccount)
    {
        await _synchronizationManager.QueueRequestPackAsync(requestsByAccount, triggerSynchronization: false).ConfigureAwait(false);
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

    private static void PublishSynchronizationRequests(Guid accountId, IReadOnlyCollection<IRequestBase> requests)
    {
        var hasCalendarRequests = requests.Any(request => request is ICalendarActionRequest);
        var hasContactRequests = requests.Any(request => request is IContactActionRequest);
        var hasTaskRequests = requests.Any(request => request is ITaskActionRequest);
        var hasMailRequests = requests.Any(request => request is IMailActionRequest or IFolderActionRequest or ICategoryActionRequest);

        if (hasCalendarRequests)
        {
            WeakReferenceMessenger.Default.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
            {
                AccountId = accountId,
                Type = CalendarSynchronizationType.ExecuteRequests
            }));
        }

        if (hasContactRequests)
        {
            foreach (var addressBookId in requests.OfType<IContactActionRequest>().Select(request => request.AddressBookId).Distinct())
            {
                WeakReferenceMessenger.Default.Send(new NewContactSynchronizationRequested(new ContactSynchronizationOptions
                {
                    AccountId = accountId,
                    AddressBookId = addressBookId,
                    Type = ContactSynchronizationType.ExecuteRequests
                }));
            }
        }

        if (hasTaskRequests)
        {
            WeakReferenceMessenger.Default.Send(new NewTaskSynchronizationRequested(new TaskSynchronizationOptions
            {
                AccountId = accountId,
                Type = TaskSynchronizationType.ExecuteRequests
            }));
        }

        if (hasMailRequests || (!hasCalendarRequests && !hasContactRequests && !hasTaskRequests))
        {
            WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.ExecuteRequests
            }));
        }

        if (requests.Any(request => request is DeleteFolderRequest or CreateSubFolderRequest or CreateRootFolderRequest))
        {
            WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.FoldersOnly
            }));
        }
    }
}
