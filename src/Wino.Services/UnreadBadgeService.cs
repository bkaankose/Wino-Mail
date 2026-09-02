using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Badges;

namespace Wino.Services;

/// <summary>
/// Builds the one badge snapshot every surface reads: the navigation account badge, the Windows
/// taskbar badge, and the launch destination that follows the taskbar badge.
/// </summary>
public class UnreadBadgeService : IUnreadBadgeService
{
    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly ILogger _logger = Log.ForContext<UnreadBadgeService>();

    public UnreadBadgeService(IAccountService accountService, IFolderService folderService)
    {
        _accountService = accountService;
        _folderService = folderService;
    }

    public async Task<UnreadBadgeSnapshot> GetSnapshotAsync()
    {
        try
        {
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            var accountBadges = new List<AccountUnreadBadge>();

            foreach (var account in accounts)
            {
                // An account without mail access stops synchronizing, so anything it still holds is stale.
                if (!account.IsMailAccessGranted)
                    continue;

                var contributions = await _folderService.GetCountedFolderUnreadCountsAsync(account.Id).ConfigureAwait(false);

                accountBadges.Add(new AccountUnreadBadge(
                    account.Id,
                    contributions.Sum(contribution => contribution.UnreadCount),
                    account.Preferences.IsAccountBadgeEnabled,
                    account.Preferences.IsTaskbarBadgeEnabled,
                    contributions));
            }

            return new UnreadBadgeSnapshot(accountBadges);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to build the unread badge snapshot.");

            return UnreadBadgeSnapshot.Empty;
        }
    }

    public async Task<int> GetAccountUnreadCountAsync(Guid accountId)
    {
        try
        {
            var contributions = await _folderService.GetCountedFolderUnreadCountsAsync(accountId).ConfigureAwait(false);

            return contributions.Sum(contribution => contribution.UnreadCount);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to calculate the unread count for account {AccountId}.", accountId);

            return 0;
        }
    }
}
