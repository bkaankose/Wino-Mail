using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Companion.Backend;

internal sealed class CompanionBackendControl(
    IAccountService accountService,
    ISynchronizationManager synchronizationManager) : ICompanionBackendControl
{
    public Task<string> GetVersionAsync() =>
        Task.FromResult(typeof(CompanionBackendControl).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");

    public async Task<bool> HasAccountsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await accountService.GetAccountsAsync().ConfigureAwait(false)).Count > 0;
    }

    public async Task SynchronizeAllAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
        await Parallel.ForEachAsync(accounts, cancellationToken, async (account, token) =>
        {
            if (!account.IsMailAccessGranted)
            {
                return;
            }

            await synchronizationManager.SynchronizeMailAsync(
                new MailSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = MailSynchronizationType.InboxOnly,
                },
                token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        return FlushCoreAsync(cancellationToken);
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
        await Parallel.ForEachAsync(accounts, cancellationToken, async (account, token) =>
        {
            if (account.IsMailAccessGranted)
            {
                await synchronizationManager.SynchronizeMailAsync(
                    new MailSynchronizationOptions
                    {
                        AccountId = account.Id,
                        Type = MailSynchronizationType.ExecuteRequests,
                    },
                    token).ConfigureAwait(false);
            }

            if (account.IsCalendarAccessGranted)
            {
                await synchronizationManager.SynchronizeCalendarAsync(
                    new CalendarSynchronizationOptions
                    {
                        AccountId = account.Id,
                        Type = CalendarSynchronizationType.ExecuteRequests,
                    },
                    token).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }
}
