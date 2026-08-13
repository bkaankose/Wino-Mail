using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.MailItem;

public enum MailSearchScope
{
    CurrentFolder,
    CurrentAccount,
    AllAccounts,
}

public enum MailSearchReach
{
    DownloadedOnly,
    IncludeServer,
}

public sealed record MailSearchCriteria(
    string Query,
    SearchMode ExecutionMode,
    MailSearchScope Scope,
    MailSearchReach Reach,
    string Sender,
    DateTimeOffset? ReceivedAfterUtc,
    DateTimeOffset? ReceivedBeforeUtc,
    bool HasAttachments,
    bool IsUnread,
    bool IsFlagged,
    IReadOnlyList<Guid> FolderIds,
    IReadOnlyList<Guid> AccountIds)
{
    public static MailSearchCriteria Empty { get; } = new(
        string.Empty,
        SearchMode.Local,
        MailSearchScope.CurrentFolder,
        MailSearchReach.DownloadedOnly,
        string.Empty,
        null,
        null,
        false,
        false,
        false,
        [],
        []);
}

public sealed record RemoteMailSearchCriteria(
    string Query,
    string Sender,
    DateTimeOffset? ReceivedAfterUtc,
    DateTimeOffset? ReceivedBeforeUtc,
    bool HasAttachments,
    bool IsUnread,
    bool IsFlagged);
