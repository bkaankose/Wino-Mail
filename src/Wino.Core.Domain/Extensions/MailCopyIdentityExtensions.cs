using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Extensions;

/// <summary>
/// Server message identity helpers.
/// Gmail materializes one <see cref="MailCopy"/> row per label for the same server message:
/// every row shares Id, ThreadId and FileId, and differs only in UniqueId and FolderId.
/// Only one of those rows may ever be surfaced in a mail list.
/// </summary>
public static class MailCopyIdentityExtensions
{
    /// <summary>
    /// Server message id, falling back to the local identity so rows without a server id
    /// (local drafts, orphans) are never collapsed together.
    /// </summary>
    public static string ResolveServerMailId(this MailCopy mail)
        => string.IsNullOrWhiteSpace(mail?.Id)
            ? mail?.UniqueId.ToString("N") ?? string.Empty
            : mail.Id;

    /// <summary>
    /// Account of the mail, preferring the hydrated account and falling back to a folder lookup
    /// for copies that have not been hydrated yet.
    /// </summary>
    public static Guid ResolveAccountId(this MailCopy mail, IReadOnlyDictionary<Guid, Guid> accountIdsByFolderId)
    {
        if (mail?.AssignedAccount != null)
            return mail.AssignedAccount.Id;

        if (mail != null && accountIdsByFolderId != null && accountIdsByFolderId.TryGetValue(mail.FolderId, out var accountId))
            return accountId;

        return Guid.Empty;
    }

    /// <summary>
    /// Collapses label siblings down to a single copy per (account, server message id).
    /// </summary>
    /// <param name="isPreferredCopy">
    /// The caller's notion of "belongs to the surface being rendered". The copy that satisfies it
    /// wins, because folder scoped actions and list seed checks must target the visible folder.
    /// Gmail does not guarantee INBOX comes first in LabelIds, so insertion order cannot be trusted.
    /// </param>
    public static IEnumerable<MailCopy> CollapseServerMessageDuplicates(
        this IEnumerable<MailCopy> mails,
        IReadOnlyDictionary<Guid, Guid> accountIdsByFolderId,
        Func<MailCopy, bool> isPreferredCopy)
    {
        ArgumentNullException.ThrowIfNull(mails);
        ArgumentNullException.ThrowIfNull(isPreferredCopy);

        return mails
            .GroupBy(mail => (mail.ResolveAccountId(accountIdsByFolderId), mail.ResolveServerMailId()))
            .Select(group => group
                .OrderByDescending(isPreferredCopy)
                .ThenByDescending(mail => mail.CreationDate)
                .ThenBy(mail => mail.FolderId)
                .ThenBy(mail => mail.UniqueId)
                .First());
    }
}
