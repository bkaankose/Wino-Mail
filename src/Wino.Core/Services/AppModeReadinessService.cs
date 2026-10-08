using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.UI;

namespace Wino.Core.Services;

/// <summary>
/// Evaluates <see cref="AppModeReadiness"/> from the local database and turns the account,
/// capability and synchronization messages into a single invalidation signal.
/// </summary>
public sealed class AppModeReadinessService : IAppModeReadinessService
{
    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly ICalendarService _calendarService;
    private readonly ITaskQueryService _taskQueryService;
    private readonly IContactQueryService _contactQueryService;
    private readonly IWinoLogger _logger;

    public event EventHandler ReadinessInvalidated;

    public AppModeReadinessService(
        IAccountService accountService,
        IFolderService folderService,
        ICalendarService calendarService,
        ITaskQueryService taskQueryService,
        IContactQueryService contactQueryService,
        IWinoLogger logger = null)
    {
        _accountService = accountService;
        _folderService = folderService;
        _calendarService = calendarService;
        _taskQueryService = taskQueryService;
        _contactQueryService = contactQueryService;
        _logger = logger;

        RegisterInvalidationMessages(WeakReferenceMessenger.Default);
    }

    public async Task<AppModeReadiness> GetReadinessAsync(WinoApplicationMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == WinoApplicationMode.Settings)
            return AppModeReadiness.Ready(mode);

        try
        {
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
            cancellationToken.ThrowIfCancellationRequested();

            var enabledAccounts = accounts.Where(account => AppModeReadiness.IsModeEnabled(account, mode)).ToList();
            var accountsWithData = enabledAccounts.Count == 0
                ? new HashSet<Guid>()
                : await GetAccountsWithDataAsync(mode, enabledAccounts).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return AppModeReadiness.Evaluate(mode, accounts, account => accountsWithData.Contains(account.Id));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed check must never lock the user out of a mode that may well work.
            _logger?.CaptureException(ex, nameof(AppModeReadinessService));
            return AppModeReadiness.Ready(mode);
        }
    }

    private async Task<HashSet<Guid>> GetAccountsWithDataAsync(WinoApplicationMode mode, IReadOnlyList<MailAccount> enabledAccounts)
    {
        var result = new HashSet<Guid>();

        switch (mode)
        {
            case WinoApplicationMode.Mail:
                foreach (var account in enabledAccounts)
                {
                    var folders = await _folderService.GetFoldersAsync(account.Id).ConfigureAwait(false);
                    if (folders?.Count > 0)
                        result.Add(account.Id);
                }
                break;

            case WinoApplicationMode.Calendar:
                foreach (var account in enabledAccounts)
                {
                    var calendars = await _calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false);
                    if (calendars?.Count > 0)
                        result.Add(account.Id);
                }
                break;

            case WinoApplicationMode.Tasks:
                var lists = await _taskQueryService.GetTaskListsAsync().ConfigureAwait(false) ?? [];
                foreach (var account in enabledAccounts)
                {
                    // To Do creates a local list for these accounts on its first load, so they
                    // are usable without waiting for a provider.
                    if (RequiresLocalTaskList(account) || lists.Any(list => list.MailAccountId == account.Id))
                        result.Add(account.Id);
                }
                break;

            case WinoApplicationMode.Contacts:
                var destinations = await _contactQueryService.GetCreateDestinationsAsync().ConfigureAwait(false) ?? [];
                foreach (var destination in destinations)
                    result.Add(destination.MailAccountId);
                break;
        }

        return result;
    }

    /// <summary>Mirrors the To Do page's local fallback list rule.</summary>
    private static bool RequiresLocalTaskList(MailAccount account)
        => account.IsTaskAccessEnabled &&
           (account.ProviderType is not (MailProviderType.Gmail or MailProviderType.Outlook) ||
            !account.IsTaskAccessGranted ||
            account.IsTaskReauthorizationRequired);

    private void RegisterInvalidationMessages(IMessenger messenger)
    {
        messenger.Register<AppModeReadinessService, AccountCreatedMessage>(this, static (r, _) => r.Invalidate());
        messenger.Register<AppModeReadinessService, AccountUpdatedMessage>(this, static (r, _) => r.Invalidate());
        messenger.Register<AppModeReadinessService, AccountRemovedMessage>(this, static (r, _) => r.Invalidate());
        messenger.Register<AppModeReadinessService, AccountsMenuRefreshRequested>(this, static (r, _) => r.Invalidate());
        messenger.Register<AppModeReadinessService, AccountFolderConfigurationUpdated>(this, static (r, _) => r.Invalidate());
        messenger.Register<AppModeReadinessService, AccountSynchronizationCompleted>(this, static (r, _) => r.Invalidate());
        messenger.Register<AppModeReadinessService, TaskSynchronizationCompleted>(this, static (r, _) => r.Invalidate());
        messenger.Register<AppModeReadinessService, ContactSynchronizationCompleted>(this, static (r, _) => r.Invalidate());
        messenger.Register<AppModeReadinessService, CalendarListAdded>(this, static (r, _) => r.Invalidate());
        messenger.Register<AppModeReadinessService, CalendarListDeleted>(this, static (r, _) => r.Invalidate());
        messenger.Register<AppModeReadinessService, AccountCalendarSynchronizationStateChanged>(this, static (r, m) =>
        {
            if (!m.IsSynchronizationInProgress)
                r.Invalidate();
        });
    }

    private void Invalidate() => ReadinessInvalidated?.Invoke(this, EventArgs.Empty);
}
