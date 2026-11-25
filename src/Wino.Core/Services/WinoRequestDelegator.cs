using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AppLock;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Contact;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Requests.Tasks;
using Wino.Core.Integration.Processors;
using Wino.Core.Domain.Messaging.Server;
using Wino.Services.Interfaces;
using Wino.Services.Models.MailItem;

namespace Wino.Core.Services;

public class WinoRequestDelegator : IWinoRequestDelegator
{
    private readonly IWinoRequestProcessor _winoRequestProcessor;
    private readonly IAccountService _accountService;
    private readonly ICalendarService _calendarService;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly ICalDavChangeProcessor _calendarChangeProcessor;
    private readonly IMailProviderRegistry _providers;
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly IApplicationLocalRequestExecutor _applicationLocalRequestExecutor;
    private readonly IAppLockService _appLockService;
    private readonly IMailService _mailService;
    private readonly IMailTransferService _mailTransferService;

    private readonly IMessenger _messenger;

    private readonly IItemLinkService _itemLinks;

    public WinoRequestDelegator(IWinoRequestProcessor winoRequestProcessor,
                                IAccountService accountService,
                                ICalendarService calendarService,
                                ISynchronizationManager synchronizationManager,
                                ICalDavChangeProcessor calendarChangeProcessor,
                                IApplicationConfiguration applicationConfiguration,
                                IApplicationLocalRequestExecutor applicationLocalRequestExecutor,
                                IMessenger messenger,
                                IMailProviderRegistry providers,
                                IAppLockService appLockService,
                                IMailService mailService,
                                IMailTransferService mailTransferService,
                                IItemLinkService itemLinks = null)
    {
        _itemLinks = itemLinks;
        _mailTransferService = mailTransferService ?? throw new ArgumentNullException(nameof(mailTransferService));
        _mailService = mailService ?? throw new ArgumentNullException(nameof(mailService));
        _providers = providers;
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _winoRequestProcessor = winoRequestProcessor;
        _accountService = accountService;
        _calendarService = calendarService;
        _synchronizationManager = synchronizationManager;
        _calendarChangeProcessor = calendarChangeProcessor;
        _applicationConfiguration = applicationConfiguration;
        _applicationLocalRequestExecutor = applicationLocalRequestExecutor;
        _appLockService = appLockService ?? throw new ArgumentNullException(nameof(appLockService));
    }

    // The overloads that return no result throw the refusal, so their callers revert and report it like any other failure.
    private void ThrowIfRefusedWhileLocked()
    {
        if (_appLockService.AreActionsRefused)
            throw new AppLockedException();
    }

    public async Task<WinoRequestDelegationResult> ExecuteWithResultAsync(MailOperationPreperationRequest request)
    {
        if (_appLockService.AreActionsRefused)
            return new WinoRequestDelegationResult(0, new AppLockedException());

        // Mail of another account than the target folder's is copied into that account and then removed from its own.
        var crossAccountItems = GetCrossAccountMoveItems(request);
        if (crossAccountItems.Count > 0)
        {
            var sameAccountItems = request.MailItems.Where(item => item != null && !crossAccountItems.Contains(item)).ToList();

            try
            {
                await _mailTransferService.ValidateMoveAsync(crossAccountItems, request.MoveTargetFolder).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is not (UnavailableSpecialFolderException or InvalidMoveTargetException or MailOperationDecisionRequiredException))
                    Log.Error(ex, "Cross-account move validation failed.");

                return new WinoRequestDelegationResult(0, ex);
            }

            var sameAccountResult = sameAccountItems.Count == 0
                ? new WinoRequestDelegationResult(0, null)
                : await ExecuteWithResultAsync(request with { MailItems = sameAccountItems }).ConfigureAwait(false);

            if (sameAccountResult.Error != null)
                return sameAccountResult;

            try
            {
                await _mailTransferService.StartMoveAsync(crossAccountItems, request.MoveTargetFolder).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is not (UnavailableSpecialFolderException or InvalidMoveTargetException or MailOperationDecisionRequiredException or AppLockedException))
                    Log.Error(ex, "Cross-account move failed to start.");

                return new WinoRequestDelegationResult(sameAccountResult.QueuedCount, ex);
            }

            return new WinoRequestDelegationResult(sameAccountResult.QueuedCount + crossAccountItems.Count, null);
        }

        List<IMailActionRequest> requests;

        try
        {
            requests = await _winoRequestProcessor.PrepareRequestsAsync(request);
        }
        catch (Exception ex)
        {
            if (ex is not (UnavailableSpecialFolderException or InvalidMoveTargetException or NotImplementedException or MailOperationDecisionRequiredException))
                Log.Error(ex, "Request creation failed.");

            return new WinoRequestDelegationResult(0, ex);
        }

        if (requests == null || !requests.Any())
            return new WinoRequestDelegationResult(0, null);

        var accountGroups = requests
            .GroupBy(a => a.Item.AssignedAccount.Id)
            .ToDictionary(
                group => group.Key,
                group => group.Cast<IRequestBase>().ToList());

        try
        {
            await QueueRequestPackAsync(accountGroups).ConfigureAwait(false);
        }
        catch (AppLockedException ex)
        {
            return new WinoRequestDelegationResult(0, ex);
        }

        foreach (var accountGroup in requests.GroupBy(a => a.Item.AssignedAccount.Id))
        {
            await QueueSynchronizationAsync(accountGroup.Key);
        }

        return new WinoRequestDelegationResult(requests.Count, null);
    }

    private static List<MailCopy> GetCrossAccountMoveItems(MailOperationPreperationRequest request)
    {
        if (request.Action != MailOperation.Move || request.MoveTargetFolder is not { } target || target.MailAccountId == Guid.Empty)
            return [];

        return request.MailItems?
            .Where(item => item?.AssignedAccount != null && item.AssignedAccount.Id != target.MailAccountId)
            .ToList() ?? [];
    }

    public async Task<WinoRequestDelegationResult> ExecuteAsync(FolderOperationPreperationRequest folderRequest)
    {
        if (folderRequest == null || folderRequest.Folder == null) return new WinoRequestDelegationResult(0, null);
        if (_appLockService.AreActionsRefused) return new WinoRequestDelegationResult(0, new AppLockedException());

        IRequestBase request = null;

        var accountId = folderRequest.Folder.MailAccountId;

        try
        {
            request = await _winoRequestProcessor.PrepareFolderRequestAsync(folderRequest);
        }
        catch (NotImplementedException ex)
        {
            return new WinoRequestDelegationResult(0, ex);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Folder operation execution failed.");
            return new WinoRequestDelegationResult(0, ex);
        }

        if (request == null) return new WinoRequestDelegationResult(0, null);

        try
        {
            await QueueRequestAsync(request, accountId);
        }
        catch (AppLockedException ex)
        {
            return new WinoRequestDelegationResult(0, ex);
        }

        await QueueSynchronizationAsync(accountId);

        if (folderRequest.Action is FolderOperation.Delete or FolderOperation.CreateSubFolder)
        {
            await QueueFoldersOnlySynchronizationAsync(accountId);
        }

        return new WinoRequestDelegationResult(1, null);
    }

    public async Task ExecuteAsync(DraftPreparationRequest draftPreperationRequest)
    {
        if (draftPreperationRequest.Account.UsesActiveSync)
            return;
        var request = new CreateDraftRequest(draftPreperationRequest);
        var accountId = draftPreperationRequest.Account.Id;

        try
        {
            ThrowIfRefusedWhileLocked();
            await QueueRequestAsync(request, accountId);
        }
        catch (AppLockedException)
        {
            // The caller stored this draft only for this request; kept without it, it would never reach the server.
            await _mailService.DeleteMailAsync(accountId, draftPreperationRequest.CreatedLocalDraftCopy.Id).ConfigureAwait(false);
            throw;
        }

        await QueueSynchronizationAsync(accountId);
    }

    public async Task ExecuteAsync(SendDraftPreparationRequest sendDraftPreperationRequest)
    {
        ThrowIfRefusedWhileLocked();

        var request = new SendDraftRequest(sendDraftPreperationRequest);
        var account = sendDraftPreperationRequest.MailItem.AssignedAccount;

        await QueueRequestAsync(request, account.Id);
        await QueueSynchronizationAsync(account.Id);
    }

    public async Task<WinoRequestDelegationResult> ExecuteAsync(CalendarOperationPreparationRequest calendarPreparationRequest)
    {
        if (calendarPreparationRequest == null)
            return new WinoRequestDelegationResult(0, null);

        if (_appLockService.AreActionsRefused)
            return new WinoRequestDelegationResult(0, new AppLockedException());

        var resolvedCalendar = await ResolveCalendarAsync(calendarPreparationRequest).ConfigureAwait(false);
        if (resolvedCalendar?.IsReadOnly == true)
            return new WinoRequestDelegationResult(0, new ReadOnlyCalendarException(resolvedCalendar.Id));

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
                OriginalReminders = calendarPreparationRequest.OriginalReminders,
                Attachments = calendarPreparationRequest.Attachments
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
            return new WinoRequestDelegationResult(0, null);

        var accountId = calendarPreparationRequest.Operation == CalendarSynchronizerOperation.CreateEvent
            ? calendarPreparationRequest.ComposeResult.AccountId
            : calendarPreparationRequest.CalendarItem.AssignedCalendar.AccountId;
        var accountName = calendarPreparationRequest.Operation == CalendarSynchronizerOperation.CreateEvent
            ? null
            : calendarPreparationRequest.CalendarItem.AssignedCalendar.MailAccount?.Name;

        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);

        try
        {
            if (account?.IsCalendarAccessEnabled == true && !account.IsCalendarAccessGranted)
            {
                await ExecuteLocalCalendarRequestAsync(account, request).ConfigureAwait(false);
                return new WinoRequestDelegationResult(1, null);
            }

            await QueueRequestAsync(request, accountId);
        }
        catch (AppLockedException ex)
        {
            return new WinoRequestDelegationResult(0, ex);
        }

        await QueueCalendarSynchronizationAsync(accountId);

        return new WinoRequestDelegationResult(1, null);
    }

    private async Task ExecuteLocalCalendarRequestAsync(MailAccount account, IRequestBase request)
    {
        if (_appLockService.AreActionsRefused)
            throw new AppLockedException();

        var handler = new LocalCalendarOperationHandler(
            account,
            _calendarChangeProcessor,
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

        ThrowIfRefusedWhileLocked();

        var preparedRequests = new List<IContactActionRequest>();

        foreach (var preparationRequest in valid)
        {
            var request = await _winoRequestProcessor
                .PrepareContactRequestAsync(preparationRequest)
                .ConfigureAwait(false);

            preparedRequests.Add(request);
        }

        foreach (var group in preparedRequests.GroupBy(request => request.MailAccountId))
        {
            await QueueRequestsAsync(group.Cast<IRequestBase>(), group.Key).ConfigureAwait(false);

            _messenger.Send(new NewContactSynchronizationRequested(new ContactSynchronizationOptions
            {
                AccountId = group.Key,
                AddressBookId = group.First().AddressBookId,
                Type = ContactSynchronizationType.ExecuteRequests
            }));
        }
    }

    public async Task ExecuteAsync(Guid accountId, IEnumerable<IRequestBase> requests)
    {
        ThrowIfRefusedWhileLocked();

        var requestList = new List<IRequestBase>();

        foreach (var request in requests?.Where(request => request is not null) ?? [])
        {
            if (request is TaskActionRequest taskRequest)
            {
                var prepared = await _winoRequestProcessor.PrepareTaskRequestAsync(new TaskOperationPreparationRequest(
                    taskRequest.AccountId,
                    taskRequest.Operation,
                    taskRequest.List,
                    taskRequest.Task,
                    taskRequest.Step,
                    taskRequest.OriginalList,
                    taskRequest.OriginalTask,
                    taskRequest.OriginalStep,
                    taskRequest.Group,
                    taskRequest.OriginalGroup)).ConfigureAwait(false);

                if (prepared != null)
                    prepared.Trace = taskRequest.Trace;
                if (prepared is TaskActionRequest preparedTaskRequest && taskRequest.AllowUndo)
                    prepared = preparedTaskRequest with { AllowUndo = true };

                requestList.Add(prepared ?? taskRequest);
                continue;
            }

            requestList.Add(request);
        }

        if (requestList.Count == 0)
            return;

        await QueueRequestsAsync(requestList, accountId).ConfigureAwait(false);

        PublishSynchronizationRequests(accountId, requestList);
    }

    public Task ExecuteLocalAsync(IRequestBase request)
    {
        // Callers keep their own copy of the change and revert it only when this throws.
        if (_appLockService.AreActionsRefused)
            return Task.FromException(new AppLockedException());

        return _applicationLocalRequestExecutor.ExecuteAsync(request);
    }

    private async Task<IRequestBase> CreateCalendarEventRequestAsync(CalendarOperationPreparationRequest calendarPreparationRequest)
    {
        var composeResult = calendarPreparationRequest.ComposeResult
                            ?? throw new InvalidOperationException("Create event requests require a compose result.");
        var assignedCalendar = await _calendarService.GetAccountCalendarAsync(composeResult.CalendarId).ConfigureAwait(false);

        if (assignedCalendar == null)
            throw new InvalidOperationException($"Calendar {composeResult.CalendarId} could not be resolved.");

        var request = new CreateCalendarEventRequest(composeResult, assignedCalendar);

        // The event's links are stored when its row is stored, which happens only after the server confirmed it.
        if (_itemLinks is not null)
        {
            foreach (var link in composeResult.PendingLinks ?? [])
            {
                // The calendar may have been changed in the composer.
                link.TargetAccountId = assignedCalendar.AccountId;
                _itemLinks.ExpectLink(request.PreparedItem.Id, link);
            }
        }

        return request;
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
        if (calendarItem.AssignedCalendar?.MailAccount is { } account &&
            _providers.GetTraits(account.ProviderType).RemovesDeclinedEvents)
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

        _messenger.Send(new NewMailSynchronizationRequested(options));
        return Task.CompletedTask;
    }

    private Task QueueFoldersOnlySynchronizationAsync(Guid accountId)
    {
        var options = new MailSynchronizationOptions()
        {
            AccountId = accountId,
            Type = MailSynchronizationType.FoldersOnly
        };

        _messenger.Send(new NewMailSynchronizationRequested(options));
        return Task.CompletedTask;
    }

    private Task QueueCalendarSynchronizationAsync(Guid accountId)
    {
        var options = new CalendarSynchronizationOptions()
        {
            AccountId = accountId,
            Type = CalendarSynchronizationType.ExecuteRequests
        };

        _messenger.Send(new NewCalendarSynchronizationRequested(options));
        return Task.CompletedTask;
    }

    private void PublishSynchronizationRequests(Guid accountId, IReadOnlyCollection<IRequestBase> requests)
    {
        var hasCalendarRequests = requests.Any(request => request is ICalendarActionRequest);
        var hasContactRequests = requests.Any(request => request is IContactActionRequest);
        var hasTaskRequests = requests.Any(request => request is ITaskActionRequest);
        var hasMailRequests = requests.Any(request => request is IMailActionRequest or IFolderActionRequest or ICategoryActionRequest);

        if (hasCalendarRequests)
        {
            _messenger.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
            {
                AccountId = accountId,
                Type = CalendarSynchronizationType.ExecuteRequests
            }));
        }

        if (hasContactRequests)
        {
            foreach (var addressBookId in requests.OfType<IContactActionRequest>().Select(request => request.AddressBookId).Distinct())
            {
                _messenger.Send(new NewContactSynchronizationRequested(new ContactSynchronizationOptions
                {
                    AccountId = accountId,
                    AddressBookId = addressBookId,
                    Type = ContactSynchronizationType.ExecuteRequests
                }));
            }
        }

        if (hasTaskRequests)
        {
            _messenger.Send(new NewTaskSynchronizationRequested(new TaskSynchronizationOptions
            {
                AccountId = accountId,
                Type = TaskSynchronizationType.ExecuteRequests
            }));
        }

        if (hasMailRequests || (!hasCalendarRequests && !hasContactRequests && !hasTaskRequests))
        {
            _messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.ExecuteRequests
            }));
        }

        if (requests.Any(request => request is DeleteFolderRequest or CreateSubFolderRequest or CreateRootFolderRequest))
        {
            _messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.FoldersOnly
            }));
        }
    }
}
