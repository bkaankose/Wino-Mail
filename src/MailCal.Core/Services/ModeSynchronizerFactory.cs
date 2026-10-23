using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Extensions;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Synchronization;

namespace MailCal.Core.Services;

/// <summary>
/// Resolves a mode independently from the account's mail provider. A selected provider or
/// DAV source that is not viable remains unavailable; it never falls back to local storage.
/// </summary>
public sealed class ModeSynchronizerFactory :
    IMailSynchronizerFactory,
    ICalendarSynchronizerFactory,
    IContactSynchronizerFactory,
    ITaskSynchronizerFactory
{
    private readonly IAccountService _accountService;
    private readonly ISynchronizerFactory _accountSynchronizerFactory;

    public ModeSynchronizerFactory(
        IAccountService accountService,
        ISynchronizerFactory accountSynchronizerFactory)
    {
        _accountService = accountService;
        _accountSynchronizerFactory = accountSynchronizerFactory;
    }

    async Task<IMailSynchronizer> IMailSynchronizerFactory.GetSynchronizerAsync(Guid accountId)
    {
        var (account, synchronizer) = await ResolveAccountAsync(accountId).ConfigureAwait(false);
        var available = account.IsMailAccessGranted && synchronizer is not null;

        return new MailAdapter(account, synchronizer, available, Translator.Synchronizer_MailUnavailable);
    }

    async Task<ICalendarSynchronizer> ICalendarSynchronizerFactory.GetSynchronizerAsync(Guid accountId)
    {
        var (account, synchronizer) = await ResolveAccountAsync(accountId).ConfigureAwait(false);
        var available = account.IsCalendarAccessEnabled && IsCalendarSourceViable(account);

        return new CalendarAdapter(account, synchronizer, available, Translator.Synchronizer_CalendarUnavailable);
    }

    async Task<IContactSynchronizer> IContactSynchronizerFactory.GetSynchronizerAsync(Guid accountId)
    {
        var (account, synchronizer) = await ResolveAccountAsync(accountId).ConfigureAwait(false);
        var available = account.IsContactAccessEnabled && IsContactSourceViable(account);

        return new ContactAdapter(account, synchronizer, available, Translator.Synchronizer_ContactsUnavailable);
    }

    async Task<ITaskSynchronizer> ITaskSynchronizerFactory.GetSynchronizerAsync(Guid accountId)
    {
        var (account, synchronizer) = await ResolveAccountAsync(accountId).ConfigureAwait(false);
        var available = account.IsTaskAccessEnabled && IsTaskSourceViable(account);

        return new TaskAdapter(account, synchronizer, available, Translator.Synchronizer_TasksUnavailable);
    }

    private async Task<(MailAccount Account, IMailCalSynchronizerBase Synchronizer)> ResolveAccountAsync(Guid accountId)
    {
        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"Account {accountId} does not exist.");
        var synchronizer = await _accountSynchronizerFactory.GetAccountSynchronizerAsync(accountId).ConfigureAwait(false);

        return (account, synchronizer);
    }

    private static bool IsCalendarSourceViable(MailAccount account)
        => account.GetEffectiveCalendarIntegrationSource() switch
        {
            AccountIntegrationSource.Local => true,
            AccountIntegrationSource.Provider => account.IsCalendarAccessGranted && IsProviderAccount(account),
            AccountIntegrationSource.Dav => account.IsCalendarAccessGranted &&
                                            account.ProviderType == MailProviderType.IMAP4 &&
                                            !string.IsNullOrWhiteSpace(account.ServerInformation?.CalDavServiceUrl),
            _ => false
        };

    private static bool IsContactSourceViable(MailAccount account)
        => account.ContactIntegrationSource switch
        {
            AccountIntegrationSource.Local => true,
            AccountIntegrationSource.Provider => account.IsContactAccessGranted && IsProviderAccount(account),
            // An empty CardDAV URL is filled in by discovery on the account address when contacts synchronize.
            AccountIntegrationSource.Dav => !string.IsNullOrWhiteSpace(account.ServerInformation?.CardDavServiceUrl) ||
                                            !string.IsNullOrWhiteSpace(account.Address),
            _ => false
        };

    private static bool IsTaskSourceViable(MailAccount account)
        => account.TaskIntegrationSource switch
        {
            AccountIntegrationSource.Local => true,
            AccountIntegrationSource.Provider => account.IsTaskAccessGranted && IsProviderAccount(account),
            AccountIntegrationSource.Dav => false,
            _ => false
        };

    private static bool IsProviderAccount(MailAccount account)
        => account.ProviderType is MailProviderType.Outlook or MailProviderType.Gmail;

    private abstract class AdapterBase(MailAccount account, IMailCalSynchronizerBase synchronizer, bool isAvailable, string unavailableReason)
        : IModeSynchronizer
    {
        protected IMailCalSynchronizerBase Synchronizer { get; } = synchronizer;
        public MailAccount Account { get; } = account;
        public bool IsAvailable { get; } = isAvailable && synchronizer is not null;
        public string UnavailableReason { get; } = unavailableReason;

        protected void EnsureAvailable()
        {
            if (!IsAvailable)
                throw new InvalidOperationException(UnavailableReason);
        }

        // Same rule as SynchronizationManager: an account that needs attention never synchronizes.
        // Account is read from storage when the adapter is resolved. Queued requests stay queued.
        protected bool IsNetworkAccessBlocked => Account.IsNetworkAccessBlocked();

        protected void QueueRequests<TRequest>(IReadOnlyList<TRequest> requests) where TRequest : IRequestBase
        {
            EnsureAvailable();

            foreach (var request in requests)
                Synchronizer.QueueRequest(request);
        }
    }

    private sealed class MailAdapter(MailAccount account, IMailCalSynchronizerBase synchronizer, bool available, string reason)
        : AdapterBase(account, synchronizer, available, reason), IMailSynchronizer
    {
        public MailSynchronizerCapabilities Capabilities { get; } = new(available, available);

        public Task<MailSynchronizationResult> SynchronizeAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
        {
            EnsureAvailable();
            if (IsNetworkAccessBlocked)
                return Task.FromResult(MailSynchronizationResult.Canceled);

            return Synchronizer.SynchronizeMailsAsync(options, cancellationToken);
        }

        public Task<MailSynchronizationResult> ExecuteRequestsAsync(IReadOnlyList<IMailActionRequest> requests, CancellationToken cancellationToken = default)
        {
            QueueRequests(requests);
            if (IsNetworkAccessBlocked)
                return Task.FromResult(MailSynchronizationResult.Canceled);

            return Synchronizer.SynchronizeMailsAsync(new MailSynchronizationOptions { AccountId = Account.Id, Type = MailSynchronizationType.ExecuteRequests }, cancellationToken);
        }
    }

    private sealed class CalendarAdapter(MailAccount account, IMailCalSynchronizerBase synchronizer, bool available, string reason)
        : AdapterBase(account, synchronizer, available, reason), ICalendarSynchronizer
    {
        public CalendarSynchronizerCapabilities Capabilities { get; } = new(available, available);

        public Task<CalendarSynchronizationResult> SynchronizeAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
        {
            EnsureAvailable();
            if (IsNetworkAccessBlocked)
                return Task.FromResult(CalendarSynchronizationResult.Canceled);

            return Synchronizer.SynchronizeCalendarEventsAsync(options, cancellationToken);
        }

        public Task<CalendarSynchronizationResult> ExecuteRequestsAsync(IReadOnlyList<ICalendarActionRequest> requests, CancellationToken cancellationToken = default)
        {
            QueueRequests(requests);
            if (IsNetworkAccessBlocked)
                return Task.FromResult(CalendarSynchronizationResult.Canceled);

            return Synchronizer.SynchronizeCalendarEventsAsync(new CalendarSynchronizationOptions { AccountId = Account.Id, Type = CalendarSynchronizationType.ExecuteRequests }, cancellationToken);
        }
    }

    private sealed class ContactAdapter(MailAccount account, IMailCalSynchronizerBase synchronizer, bool available, string reason)
        : AdapterBase(account, synchronizer, available, reason), IContactSynchronizer
    {
        public ContactSynchronizerCapabilities Capabilities { get; } = new(available, available, available);

        public Task<ContactSynchronizationResult> SynchronizeAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken = default)
        {
            EnsureAvailable();
            if (IsNetworkAccessBlocked)
                return Task.FromResult(ContactSynchronizationResult.Canceled);

            return Synchronizer.SynchronizeContactsAsync(options, cancellationToken);
        }

        public Task<ContactSynchronizationResult> ExecuteRequestsAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken = default)
        {
            QueueRequests(requests);
            if (IsNetworkAccessBlocked)
                return Task.FromResult(ContactSynchronizationResult.Canceled);

            return Synchronizer.SynchronizeContactsAsync(new ContactSynchronizationOptions { AccountId = Account.Id, Type = ContactSynchronizationType.ExecuteRequests }, cancellationToken);
        }
    }

    private sealed class TaskAdapter(MailAccount account, IMailCalSynchronizerBase synchronizer, bool available, string reason)
        : AdapterBase(account, synchronizer, available, reason), ITaskSynchronizer
    {
        public TaskSynchronizerCapabilities Capabilities { get; } = new(available, available, available, available);

        public Task<TaskSynchronizationResult> SynchronizeAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken = default)
        {
            EnsureAvailable();
            if (IsNetworkAccessBlocked)
                return Task.FromResult(TaskSynchronizationResult.Canceled);

            return Synchronizer.SynchronizeTasksAsync(options, cancellationToken);
        }

        public Task<TaskSynchronizationResult> ExecuteRequestsAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken = default)
        {
            QueueRequests(requests);
            if (IsNetworkAccessBlocked)
                return Task.FromResult(TaskSynchronizationResult.Canceled);

            return Synchronizer.SynchronizeTasksAsync(new TaskSynchronizationOptions { AccountId = Account.Id, Type = TaskSynchronizationType.ExecuteRequests }, cancellationToken);
        }
    }
}
