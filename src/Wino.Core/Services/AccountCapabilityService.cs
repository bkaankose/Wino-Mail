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
    private readonly IAuthenticationProvider _authenticationProvider;

    public AccountCapabilityService(
        ISynchronizationManager synchronizationManager,
        IAccountService accountService,
        IContactService contactService,
        ITaskService taskService,
        IAuthenticationProvider authenticationProvider = null)
    {
        _synchronizationManager = synchronizationManager;
        _accountService = accountService;
        _contactService = contactService;
        _taskService = taskService;
        _authenticationProvider = authenticationProvider;
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

        var previousMail = account.IsMailAccessGranted;
        var previousCalendar = account.IsCalendarAccessGranted;
        var previousContacts = account.IsContactAccessGranted;
        var previousTasks = account.IsTaskAccessGranted;
        var previousContactReauthorization = account.IsContactReauthorizationRequired;
        var previousTaskReauthorization = account.IsTaskReauthorizationRequired;
        var synchronizer = await _synchronizationManager.GetSynchronizerAsync(account.Id).ConfigureAwait(false);
        var synchronizerAccount = synchronizer?.Account;
        var shouldRemoveProviderTasksAfterCommit = false;
        HashSet<Guid> existingProviderTaskListIds = account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook
            ? (await _taskService.GetTaskListsAsync(account.Id).ConfigureAwait(false))
                .Where(list => list.SourceKind == (account.ProviderType == MailProviderType.Gmail
                    ? TaskSourceKind.Gmail
                    : TaskSourceKind.Outlook))
                .Select(list => list.Id)
                .ToHashSet()
            : [];

        account.IsMailAccessGranted = includeMail;
        account.IsCalendarAccessGranted = includeCalendar;
        account.IsContactAccessGranted = includeContacts;
        account.IsTaskAccessGranted = includeTasks;
        if (synchronizerAccount is not null)
        {
            synchronizerAccount.IsMailAccessGranted = includeMail;
            synchronizerAccount.IsCalendarAccessGranted = includeCalendar;
            synchronizerAccount.IsContactAccessGranted = includeContacts;
            synchronizerAccount.IsTaskAccessGranted = includeTasks;
        }

        try
        {
            var requiresInteractiveAuthorization = account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook &&
                ((!previousMail && includeMail) ||
                 (!previousCalendar && includeCalendar) ||
                 (!previousContacts && includeContacts) ||
                 (!previousTasks && includeTasks) ||
                 (previousContactReauthorization && includeContacts) ||
                 (previousTaskReauthorization && includeTasks));
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

            if (includeContacts && !previousContacts)
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
            else if (!includeContacts && previousContacts)
            {
                var source = account.ProviderType == MailProviderType.Gmail ? ContactSourceKind.Gmail : ContactSourceKind.Outlook;
                await _contactService.DeleteAddressBooksBySourceAsync(account.Id, source).ConfigureAwait(false);
                await _contactService.EnsureLocalAddressBookAsync(account.Id, account.Name).ConfigureAwait(false);
            }

            if (account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook)
            {
                var source = account.ProviderType == MailProviderType.Gmail ? TaskSourceKind.Gmail : TaskSourceKind.Outlook;
                if (includeTasks && !previousTasks)
                {
                    var result = await _synchronizationManager.SynchronizeTasksAsync(new TaskSynchronizationOptions
                    {
                        AccountId = account.Id,
                        Type = TaskSynchronizationType.Full
                    }, cancellationToken).ConfigureAwait(false);
                    if (result.CompletedState != SynchronizationCompletedState.Success)
                        throw result.Exception ?? new InvalidOperationException("Task synchronization failed.");
                }
                else if (!includeTasks && previousTasks)
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
            if (shouldRemoveProviderTasksAfterCommit)
            {
                var source = account.ProviderType == MailProviderType.Gmail ? TaskSourceKind.Gmail : TaskSourceKind.Outlook;
                await _taskService.DeleteTaskListsBySourceAsync(account.Id, source).ConfigureAwait(false);
            }
            return await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);
        }
        catch
        {
            account.IsMailAccessGranted = previousMail;
            account.IsCalendarAccessGranted = previousCalendar;
            account.IsContactAccessGranted = previousContacts;
            account.IsTaskAccessGranted = previousTasks;
            account.IsContactReauthorizationRequired = previousContactReauthorization;
            account.IsTaskReauthorizationRequired = previousTaskReauthorization;
            if (synchronizerAccount is not null)
            {
                synchronizerAccount.IsMailAccessGranted = previousMail;
                synchronizerAccount.IsCalendarAccessGranted = previousCalendar;
                synchronizerAccount.IsContactAccessGranted = previousContacts;
                synchronizerAccount.IsTaskAccessGranted = previousTasks;
            }

            if (!previousContacts)
            {
                var source = account.ProviderType == MailProviderType.Gmail ? ContactSourceKind.Gmail : ContactSourceKind.Outlook;
                await _contactService.DeleteAddressBooksBySourceAsync(account.Id, source).ConfigureAwait(false);
            }
            if (account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook && !previousTasks)
            {
                var source = account.ProviderType == MailProviderType.Gmail ? TaskSourceKind.Gmail : TaskSourceKind.Outlook;
                var currentProviderLists = await _taskService.GetTaskListsAsync(account.Id).ConfigureAwait(false);
                foreach (var list in currentProviderLists.Where(list => list.SourceKind == source && !existingProviderTaskListIds.Contains(list.Id)))
                    await _taskService.RemoveTaskListAsync(list.Id).ConfigureAwait(false);
            }
            else if (shouldRemoveProviderTasksAfterCommit)
            {
                // The deferred delete has not run when the account update fails. Restore
                // the persisted capability flags so the cached provider data stays usable.
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
}
