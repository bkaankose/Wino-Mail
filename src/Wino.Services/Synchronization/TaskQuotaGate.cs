#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Interfaces;

namespace Wino.Services.Synchronization;

/// <summary>
/// The quota window of providers that refresh tasks and contacts at most once per window (Google Tasks). The sync
/// coordinator schedules with it and the synchronization manager asks it before an automatic pull; both share this
/// singleton, so neither depends on the other for it.
/// </summary>
public sealed class TaskQuotaGate(IMailProviderRegistry providers, ITaskService taskService, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    // In memory on purpose: a pull that failed (an exhausted quota among others) never reaches the persisted
    // success time, and without this stamp every check would retry it.
    private readonly Dictionary<Guid, DateTimeOffset> _attempts = new();
    private readonly Dictionary<Guid, DateTimeOffset> _skipLogged = new();
    private readonly ILogger _logger = Log.ForContext<TaskQuotaGate>();

    public TimeSpan QuotaWindow { get; } = TimeSpan.FromHours(6);

    public bool IsQuotaBound(MailAccount account, WinoApplicationMode mode)
        => providers.GetTraits(account.ProviderType).UsesQuotaWindowForTasksAndContacts && mode switch
        {
            WinoApplicationMode.Tasks => account.TaskIntegrationSource == AccountIntegrationSource.Provider,
            WinoApplicationMode.Contacts => account.ContactIntegrationSource == AccountIntegrationSource.Provider,
            _ => false
        };

    public async Task<DateTimeOffset?> GetNextTaskPullUtcAsync(MailAccount account)
    {
        if (!IsQuotaBound(account, WinoApplicationMode.Tasks))
            return null;

        var lastSuccess = await GetLastSuccessfulTaskPullUtcAsync(account.Id).ConfigureAwait(false);
        lock (_gate)
            return GetNextTaskPullUtc(account.Id, lastSuccess, _timeProvider.GetUtcNow());
    }

    public async Task<bool> TryBeginTaskPullAsync(MailAccount account)
    {
        if (!IsQuotaBound(account, WinoApplicationMode.Tasks))
            return true;

        var lastSuccess = await GetLastSuccessfulTaskPullUtcAsync(account.Id).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            var next = GetNextTaskPullUtc(account.Id, lastSuccess, now);
            if (next is null)
            {
                _attempts[account.Id] = now;
                _skipLogged.Remove(account.Id);
                return true;
            }

            if (!_skipLogged.TryGetValue(account.Id, out var logged) || logged != next.Value)
            {
                _skipLogged[account.Id] = next.Value;
                _logger.Information("Google Tasks pull for account {AccountId} is skipped until {NextPullUtc:u}; it refreshes once every {Window}.",
                    account.Id, next.Value, QuotaWindow);
            }

            return false;
        }
    }

    public void Forget(Guid accountId)
    {
        lock (_gate)
        {
            _attempts.Remove(accountId);
            _skipLogged.Remove(accountId);
        }
    }

    private DateTimeOffset? GetNextTaskPullUtc(Guid accountId, DateTimeOffset? lastSuccess, DateTimeOffset now)
    {
        var last = lastSuccess;
        if (_attempts.TryGetValue(accountId, out var attempt) && (last is null || attempt > last))
            last = attempt;

        if (last is null)
            return null;

        // A time from the future after a clock change keeps the gate closed until six hours past it: the quota
        // matters more than a refresh that comes late.
        var next = last.Value + QuotaWindow;
        return next > now ? next : null;
    }

    private async Task<DateTimeOffset?> GetLastSuccessfulTaskPullUtcAsync(Guid accountId)
    {
        var state = await taskService.GetTaskSyncStateAsync(accountId, TaskSourceKind.Gmail).ConfigureAwait(false);
        return state?.LastSuccessfulSyncUtc is DateTime value
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : null;
    }
}
