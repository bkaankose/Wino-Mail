using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Models.Badges;

/// <summary>
/// One counted folder and what it contributed to its account total.
/// </summary>
public sealed record UnreadBadgeFolderContribution(Guid FolderId, string FolderName, int UnreadCount);

/// <summary>
/// The unread total of a single account and the folders it was built from.
/// </summary>
public sealed record AccountUnreadBadge(
    Guid AccountId,
    int UnreadCount,
    bool IsAccountBadgeEnabled,
    bool ContributesToTaskbar,
    IReadOnlyList<UnreadBadgeFolderContribution> Contributions)
{
    /// <summary>Counted folders that actually hold unread mail right now.</summary>
    public IReadOnlyList<UnreadBadgeFolderContribution> UnreadContributions
        => Contributions.Where(static contribution => contribution.UnreadCount > 0).ToList();
}

/// <summary>
/// One consistent view of every badge in the application, taken at a single point in time.
/// The taskbar badge and the launch destination both read this, so the number the user sees
/// and the folder Wino opens can never disagree.
/// </summary>
public sealed record UnreadBadgeSnapshot(IReadOnlyList<AccountUnreadBadge> Accounts)
{
    public static readonly UnreadBadgeSnapshot Empty = new([]);

    /// <summary>Sum of the unread totals of every account allowed to contribute.</summary>
    public int TaskbarUnreadCount => Accounts.Where(static account => account.ContributesToTaskbar).Sum(static account => account.UnreadCount);

    /// <summary>Accounts that are both allowed to contribute and actually have unread mail.</summary>
    public IReadOnlyList<AccountUnreadBadge> TaskbarContributors
        => Accounts.Where(static account => account.ContributesToTaskbar && account.UnreadCount > 0).ToList();

    public AccountUnreadBadge GetAccount(Guid accountId)
        => Accounts.FirstOrDefault(account => account.AccountId == accountId);
}
