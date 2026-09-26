using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Services;

public sealed class AccountCapabilityService : IAccountCapabilityService
{
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IAccountService _accountService;
    private readonly IContactService _contactService;
    private readonly ITaskService _taskService;
    private readonly ICalendarService _calendarService;
    private readonly IAuthenticationProvider _authenticationProvider;

    public AccountCapabilityService(
        ISynchronizationManager synchronizationManager,
        IAccountService accountService,
        IContactService contactService,
        ITaskService taskService,
        ICalendarService calendarService,
        IAuthenticationProvider authenticationProvider = null)
    {
        _synchronizationManager = synchronizationManager;
        _accountService = accountService;
        _contactService = contactService;
        _taskService = taskService;
        _calendarService = calendarService;
        _authenticationProvider = authenticationProvider;
    }

    public async Task EnsureLocalCapabilityStoresAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account.IsTaskAccessEnabled && account.TaskIntegrationSource == AccountIntegrationSource.Local)
            await _taskService.EnsureLocalTaskListAsync(account.Id, account.Name).ConfigureAwait(false);

        if (account.IsContactAccessEnabled && account.ContactIntegrationSource == AccountIntegrationSource.Local)
            await _contactService.EnsureLocalAddressBookAsync(account.Id, account.Name).ConfigureAwait(false);
    }

    public async Task<MailAccount> ApplyAsync(
        MailAccount account,
        bool includeMail,
        bool includeCalendar,
        bool includeContacts,
        bool includeTasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!includeMail && !includeCalendar && !includeContacts && !includeTasks)
            throw new InvalidOperationException("At least one account capability must remain enabled.");
        if (includeContacts && account.ProviderType is not (MailProviderType.Gmail or MailProviderType.Outlook))
            throw new NotSupportedException("Provider contacts are available only for Gmail and Outlook accounts.");

        var previous = CapabilityFlags.Capture(account);
        var synchronizer = await _synchronizationManager.GetSynchronizerAsync(account.Id).ConfigureAwait(false);
        var synchronizerAccount = synchronizer?.Account;
        var isOAuthProvider = account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook;
        var shouldRemoveProviderTasksAfterCommit = false;
        var shouldRemoveCalendarDataAfterCommit = !includeCalendar && previous.Calendar;
        var shouldRemoveMailDataAfterCommit = !includeMail && previous.Mail;
        HashSet<Guid> existingProviderTaskListIds = isOAuthProvider
            ? (await _taskService.GetTaskListsAsync(account.Id).ConfigureAwait(false))
                .Where(list => list.SourceKind == (account.ProviderType == MailProviderType.Gmail
                    ? TaskSourceKind.Gmail
                    : TaskSourceKind.Outlook))
                .Select(list => list.Id)
                .ToHashSet()
            : [];

        ApplyFlags(account, includeMail, includeCalendar, includeContacts, includeTasks, previous, isOAuthProvider);
        if (synchronizerAccount is not null)
            ApplyFlags(synchronizerAccount, includeMail, includeCalendar, includeContacts, includeTasks, previous, isOAuthProvider);

        try
        {
            var requiresInteractiveAuthorization = isOAuthProvider &&
                ((!previous.Mail && includeMail) ||
                 (!previous.Calendar && includeCalendar) ||
                 (!previous.Contacts && includeContacts) ||
                 (!previous.Tasks && includeTasks) ||
                 (previous.ContactReauthorization && includeContacts) ||
                 (previous.TaskReauthorization && includeTasks));
            if (requiresInteractiveAuthorization)
            {
                await _synchronizationManager.HandleAuthorizationAsync(
                    account.ProviderType,
                    account,
                    account.ProviderType == MailProviderType.Gmail,
                    forceInteractive: true).ConfigureAwait(false);

                if (includeTasks && account.ProviderType == MailProviderType.Outlook &&
                    _authenticationProvider?.GetAuthenticator(account.ProviderType) is ISubstrateTaskTokenProvider substrateTokenProvider)
                {
                    try
                    {
                        await substrateTokenProvider.EnsureSubstrateTaskConsentAsync(account).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Exchange consent is optional. Graph task sync remains available and
                        // cached groups stay visible when the separate resource is unavailable.
                    }
                }
            }

            if (includeContacts && !previous.Contacts)
            {
                var result = await _synchronizationManager.SynchronizeContactsAsync(new ContactSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = ContactSynchronizationType.Full
                }, cancellationToken).ConfigureAwait(false);
                if (result.CompletedState != SynchronizationCompletedState.Success)
                    throw result.Exception ?? new InvalidOperationException("Contact synchronization failed.");

                await _contactService.DeleteAddressBooksBySourceAsync(account.Id, ContactSourceKind.Local).ConfigureAwait(false);
            }
            else if (!includeContacts && previous.Contacts)
            {
                // Provider contacts go away; the People mode stays on with a local address book.
                var source = account.ProviderType == MailProviderType.Gmail ? ContactSourceKind.Gmail : ContactSourceKind.Outlook;
                await _contactService.DeleteAddressBooksBySourceAsync(account.Id, source).ConfigureAwait(false);
                await _contactService.EnsureLocalAddressBookAsync(account.Id, account.Name).ConfigureAwait(false);
            }

            if (isOAuthProvider)
            {
                if (includeTasks && !previous.Tasks)
                {
                    var result = await _synchronizationManager.SynchronizeTasksAsync(new TaskSynchronizationOptions
                    {
                        AccountId = account.Id,
                        Type = TaskSynchronizationType.Full
                    }, cancellationToken).ConfigureAwait(false);
                    if (result.CompletedState != SynchronizationCompletedState.Success)
                        throw result.Exception ?? new InvalidOperationException("Task synchronization failed.");
                }
                else if (!includeTasks && previous.Tasks)
                {
                    // Defer cache removal until the account flags are committed. If the
                    // capability transition fails, the previous read-only cache remains
                    // available for recovery.
                    shouldRemoveProviderTasksAfterCommit = true;
                }
            }
            else
            {
                await _taskService.EnsureLocalTaskListAsync(account.Id, account.Name).ConfigureAwait(false);
            }

            account.IsContactReauthorizationRequired = false;
            account.IsTaskReauthorizationRequired = false;
            await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);

            // Data removal runs only after the flags are committed, so a failed transition
            // never leaves an account that still claims a mode whose data is gone.
            if (shouldRemoveProviderTasksAfterCommit)
            {
                var source = account.ProviderType == MailProviderType.Gmail ? TaskSourceKind.Gmail : TaskSourceKind.Outlook;
                await _taskService.DeleteTaskListsBySourceAsync(account.Id, source).ConfigureAwait(false);

                // Provider tasks go away; the To Do mode stays on with a local list.
                await _taskService.EnsureLocalTaskListAsync(account.Id, account.Name).ConfigureAwait(false);
            }

            if (shouldRemoveCalendarDataAfterCommit)
            {
                await _synchronizationManager.CancelSynchronizationsAsync(account.Id).ConfigureAwait(false);
                await _calendarService.DeleteAccountCalendarDataAsync(account.Id).ConfigureAwait(false);
            }

            if (shouldRemoveMailDataAfterCommit)
            {
                await _synchronizationManager.CancelSynchronizationsAsync(account.Id).ConfigureAwait(false);
                await _accountService.DeleteAccountMailDataAsync(account.Id).ConfigureAwait(false);
            }

            return await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);
        }
        catch
        {
            previous.Restore(account);
            if (synchronizerAccount is not null)
                previous.Restore(synchronizerAccount);

            if (!previous.Contacts)
            {
                var source = account.ProviderType == MailProviderType.Gmail ? ContactSourceKind.Gmail : ContactSourceKind.Outlook;
                await _contactService.DeleteAddressBooksBySourceAsync(account.Id, source).ConfigureAwait(false);
            }
            if (isOAuthProvider && !previous.Tasks)
            {
                var source = account.ProviderType == MailProviderType.Gmail ? TaskSourceKind.Gmail : TaskSourceKind.Outlook;
                var currentProviderLists = await _taskService.GetTaskListsAsync(account.Id).ConfigureAwait(false);
                foreach (var list in currentProviderLists.Where(list => list.SourceKind == source && !existingProviderTaskListIds.Contains(list.Id)))
                    await _taskService.RemoveTaskListAsync(list.Id).ConfigureAwait(false);
            }
            else if (shouldRemoveProviderTasksAfterCommit || shouldRemoveCalendarDataAfterCommit || shouldRemoveMailDataAfterCommit)
            {
                // A deferred removal has not run when the account update fails. Restore
                // the persisted flags so the cached data stays usable.
                try
                {
                    await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original transition failure; the next account reload
                    // will surface the inconsistent state for repair.
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Keeps the mode and backend flags in step with the granted flags. The details page only
    /// offers on/off per mode, so on Gmail and Outlook "on" always means the provider backend.
    /// Calendar turns fully off. People and To Do keep running against a local store, matching
    /// the confirmation the page shows for those two modes.
    /// </summary>
    private static void ApplyFlags(
        MailAccount account,
        bool includeMail,
        bool includeCalendar,
        bool includeContacts,
        bool includeTasks,
        CapabilityFlags previous,
        bool isOAuthProvider)
    {
        account.IsMailAccessGranted = includeMail;
        account.IsCalendarAccessGranted = includeCalendar;
        account.IsContactAccessGranted = includeContacts;
        account.IsTaskAccessGranted = includeTasks;

        if (!isOAuthProvider)
            return;

        if (includeCalendar)
        {
            account.IsCalendarAccessEnabled = true;
            account.CalendarIntegrationSource = AccountIntegrationSource.Provider;
        }
        else if (previous.Calendar)
        {
            account.IsCalendarAccessEnabled = false;
            account.CalendarIntegrationSource = AccountIntegrationSource.Local;
        }

        if (includeContacts)
        {
            account.IsContactAccessEnabled = true;
            account.ContactIntegrationSource = AccountIntegrationSource.Provider;
        }
        else if (previous.Contacts)
        {
            account.IsContactAccessEnabled = true;
            account.ContactIntegrationSource = AccountIntegrationSource.Local;
        }

        if (includeTasks)
        {
            account.IsTaskAccessEnabled = true;
            account.TaskIntegrationSource = AccountIntegrationSource.Provider;
        }
        else if (previous.Tasks)
        {
            account.IsTaskAccessEnabled = true;
            account.TaskIntegrationSource = AccountIntegrationSource.Local;
        }
    }

    /// <summary>Snapshot of every capability flag, so a failed transition can put them all back.</summary>
    private readonly record struct CapabilityFlags(
        bool Mail,
        bool Calendar,
        bool Contacts,
        bool Tasks,
        bool ContactReauthorization,
        bool TaskReauthorization,
        bool CalendarEnabled,
        AccountIntegrationSource CalendarSource,
        bool ContactsEnabled,
        AccountIntegrationSource ContactsSource,
        bool TasksEnabled,
        AccountIntegrationSource TasksSource)
    {
        public static CapabilityFlags Capture(MailAccount account) => new(
            account.IsMailAccessGranted,
            account.IsCalendarAccessGranted,
            account.IsContactAccessGranted,
            account.IsTaskAccessGranted,
            account.IsContactReauthorizationRequired,
            account.IsTaskReauthorizationRequired,
            account.IsCalendarAccessEnabled,
            account.CalendarIntegrationSource,
            account.IsContactAccessEnabled,
            account.ContactIntegrationSource,
            account.IsTaskAccessEnabled,
            account.TaskIntegrationSource);

        public void Restore(MailAccount account)
        {
            account.IsMailAccessGranted = Mail;
            account.IsCalendarAccessGranted = Calendar;
            account.IsContactAccessGranted = Contacts;
            account.IsTaskAccessGranted = Tasks;
            account.IsContactReauthorizationRequired = ContactReauthorization;
            account.IsTaskReauthorizationRequired = TaskReauthorization;
            account.IsCalendarAccessEnabled = CalendarEnabled;
            account.CalendarIntegrationSource = CalendarSource;
            account.IsContactAccessEnabled = ContactsEnabled;
            account.ContactIntegrationSource = ContactsSource;
            account.IsTaskAccessEnabled = TasksEnabled;
            account.TaskIntegrationSource = TasksSource;
        }
    }
}
