using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// Which folders a search covers. Every scope stays inside the accounts of the active folder:
/// one account for an account folder, all of its accounts for a merged folder.
/// </summary>
public enum MailSearchScope
{
    CurrentFolder,
    Subfolders,
    AllFolders,
}

public enum MailSearchReach
{
    DownloadedOnly,
    IncludeServer,
}

public enum MailReadStatusFilter
{
    All,
    Unread,
    Read,
}

public enum MailSearchDateRange
{
    AnyTime,
    Today,
    LastSevenDays,
    LastThirtyDays,
    Custom,
}

/// <summary>
/// The search filters that local search, Gmail, Outlook and IMAP can all apply.
/// Custom dates are local calendar days; both ends are included.
/// </summary>
public sealed record MailSearchFilters(
    string Sender,
    string Subject,
    MailSearchDateRange DateRange,
    DateTime? CustomStartDate,
    DateTime? CustomEndDate,
    MailReadStatusFilter ReadStatus,
    bool HasAttachments,
    bool IsFlagged)
{
    public static MailSearchFilters Empty { get; } = new(
        string.Empty,
        string.Empty,
        MailSearchDateRange.AnyTime,
        null,
        null,
        MailReadStatusFilter.All,
        false,
        false);

    public bool HasDateFilter => DateRange != MailSearchDateRange.AnyTime &&
                                 (DateRange != MailSearchDateRange.Custom || CustomStartDate is not null || CustomEndDate is not null);

    public int ActiveCount =>
        (string.IsNullOrWhiteSpace(Sender) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(Subject) ? 0 : 1) +
        (HasDateFilter ? 1 : 0) +
        (ReadStatus == MailReadStatusFilter.All ? 0 : 1) +
        (HasAttachments ? 1 : 0) +
        (IsFlagged ? 1 : 0);

    public (DateTimeOffset? AfterUtc, DateTimeOffset? BeforeUtc) ResolveUtcRange(DateTime localNow)
    {
        var today = localNow.Date;
        var (start, end) = DateRange switch
        {
            MailSearchDateRange.Today => (today, today.AddDays(1)),
            MailSearchDateRange.LastSevenDays => (today.AddDays(-6), today.AddDays(1)),
            MailSearchDateRange.LastThirtyDays => (today.AddDays(-29), today.AddDays(1)),
            MailSearchDateRange.Custom => (CustomStartDate?.Date, CustomEndDate?.Date.AddDays(1)),
            _ => ((DateTime?)null, (DateTime?)null),
        };

        return (ToUtc(start), ToUtc(end));
    }

    private static DateTimeOffset? ToUtc(DateTime? localDate)
        => localDate is { } value
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local)).ToUniversalTime()
            : null;
}

public sealed record MailSearchCriteria(
    string Query,
    SearchMode ExecutionMode,
    MailSearchScope Scope,
    MailSearchReach Reach,
    string Sender,
    string Subject,
    DateTimeOffset? ReceivedAfterUtc,
    DateTimeOffset? ReceivedBeforeUtc,
    bool HasAttachments,
    MailReadStatusFilter ReadStatus,
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
        string.Empty,
        null,
        null,
        false,
        MailReadStatusFilter.All,
        false,
        Array.Empty<Guid>(),
        Array.Empty<Guid>());

    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(Sender) ||
        !string.IsNullOrWhiteSpace(Subject) ||
        ReceivedAfterUtc is not null ||
        ReceivedBeforeUtc is not null ||
        HasAttachments ||
        ReadStatus != MailReadStatusFilter.All ||
        IsFlagged;

    /// <summary>A search runs when there is a query, a filter, or both.</summary>
    public bool IsActive => !string.IsNullOrWhiteSpace(Query) || HasFilters;
}

public sealed record RemoteMailSearchCriteria(
    string Query,
    string Sender,
    string Subject,
    DateTimeOffset? ReceivedAfterUtc,
    DateTimeOffset? ReceivedBeforeUtc,
    bool HasAttachments,
    MailReadStatusFilter ReadStatus,
    bool IsFlagged);

/// <summary>
/// Applies the shared search filters to a stored mail. Local search uses the same rules in SQL,
/// and online results pass through this after download so every provider returns the same set.
/// </summary>
public static class MailSearchFilterMatcher
{
    public static bool Matches(
        MailCopy mail,
        string sender,
        string subject,
        DateTimeOffset? receivedAfterUtc,
        DateTimeOffset? receivedBeforeUtc,
        MailReadStatusFilter readStatus,
        bool hasAttachments,
        bool isFlagged)
    {
        if (mail is null)
            return false;

        if (!string.IsNullOrWhiteSpace(sender) &&
            !Contains(mail.FromName, sender) &&
            !Contains(mail.FromAddress, sender))
            return false;

        if (!string.IsNullOrWhiteSpace(subject) && !Contains(mail.Subject, subject))
            return false;

        var receivedUtc = mail.CreationDate.Kind == DateTimeKind.Utc
            ? mail.CreationDate
            : mail.CreationDate.ToUniversalTime();
        if (receivedAfterUtc is { } after && receivedUtc < after.UtcDateTime)
            return false;
        if (receivedBeforeUtc is { } before && receivedUtc >= before.UtcDateTime)
            return false;

        return readStatus switch
        {
            MailReadStatusFilter.Unread when mail.IsRead => false,
            MailReadStatusFilter.Read when !mail.IsRead => false,
            _ => (!hasAttachments || mail.HasAttachments) && (!isFlagged || mail.IsFlagged),
        };
    }

    private static bool Contains(string value, string term)
        => !string.IsNullOrEmpty(value) && value.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);
}
