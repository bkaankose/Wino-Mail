using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Core.SearchBar;

namespace Wino.Mail.ViewModels.Search;

public static class MailSearchCriteriaFactory
{
    public static MailSearchCriteria Create(
        string query,
        SearchBarReach reach,
        MailSearchScope scope,
        MailSearchFilters filters,
        DateTime localNow,
        IReadOnlyList<Guid> folderIds,
        IReadOnlyList<Guid> accountIds)
    {
        filters ??= MailSearchFilters.Empty;

        var online = reach == SearchBarReach.IncludeServer;
        var (afterUtc, beforeUtc) = filters.ResolveUtcRange(localNow);

        return new MailSearchCriteria(
            (query ?? string.Empty).Trim(),
            online ? SearchMode.Online : SearchMode.Local,
            scope,
            online ? MailSearchReach.IncludeServer : MailSearchReach.DownloadedOnly,
            filters.Sender.Trim(),
            filters.Subject.Trim(),
            afterUtc,
            beforeUtc,
            filters.HasAttachments,
            filters.ReadStatus,
            filters.IsFlagged,
            folderIds,
            accountIds);
    }
}
