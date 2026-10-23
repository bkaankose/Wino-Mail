using System;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Badges;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Single source of truth for every unread badge Mail-Cal renders.
/// </summary>
public interface IUnreadBadgeService
{
    /// <summary>
    /// Computes the unread total of every mail account together with the folders that produced it.
    /// Callers that need both a total and a destination must use one snapshot rather than counting twice.
    /// </summary>
    Task<UnreadBadgeSnapshot> GetSnapshotAsync();

    /// <summary>
    /// Unread total of a single account, using the folders that account counts.
    /// </summary>
    Task<int> GetAccountUnreadCountAsync(Guid accountId);
}
