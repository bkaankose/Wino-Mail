#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;

namespace Wino.Services;

public sealed class IntelligenceMessageContextResolver(
    IDatabaseService databaseService,
    IAccountService accountService,
    IMimeFileService mimeFileService,
    ISynchronizationManager synchronizationManager) : IIntelligenceMessageContextResolver
{
    public async Task<SemanticIndexAvailableRange?> GetAvailableRangeAsync(
        Guid localAccountId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = await accountService.GetAccountAsync(localAccountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The mail account no longer exists.");
        const string sql = """
            SELECT m.Id AS ProviderMessageId, m.CreationDate, m.ImapUid, m.ImapUidValidity,
                   f.RemoteFolderId, f.UidValidity AS FolderUidValidity
            FROM MailCopy m
            INNER JOIN MailItemFolder f ON f.Id = m.FolderId
            WHERE f.MailAccountId = ? AND m.IsDraft = 0 AND f.RemoteFolderId IS NOT NULL AND f.RemoteFolderId <> ''
              AND f.SpecialFolderType NOT IN (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """;
        var rows = await databaseService.Connection.QueryAsync<AvailabilityRow>(sql,
            account.Id,
            (int)SpecialFolderType.Draft, (int)SpecialFolderType.Deleted, (int)SpecialFolderType.Junk,
            (int)SpecialFolderType.Chat, (int)SpecialFolderType.Category, (int)SpecialFolderType.Unread,
            (int)SpecialFolderType.Forums, (int)SpecialFolderType.Updates, (int)SpecialFolderType.Personal,
            (int)SpecialFolderType.Promotions, (int)SpecialFolderType.Social, (int)SpecialFolderType.More,
            (int)SpecialFolderType.Other).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var messages = rows
            .GroupBy(row => ToRemoteMessageId(account.ProviderType, row), StringComparer.Ordinal)
            .Select(group => group.Max(row => ToUtc(row.CreationDate)))
            .ToArray();
        if (messages.Length == 0)
            return null;
        var counts = messages
            .GroupBy(receivedAt => DateOnly.FromDateTime(receivedAt.UtcDateTime))
            .ToDictionary(group => group.Key, group => group.Count());
        return new SemanticIndexAvailableRange(counts.Keys.Min(), counts.Keys.Max(), counts);
    }

    public async Task<IReadOnlyList<IntelligenceMessageCandidate>> GetCandidatesAsync(
        Guid localAccountId,
        DateTimeOffset? cutoffUtc = null,
        CancellationToken cancellationToken = default)
        => await GetCandidatesAsync(localAccountId, cutoffUtc, null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<IntelligenceMessageCandidate>> GetCandidatesAsync(
        Guid localAccountId,
        DateTimeOffset? cutoffUtc,
        DateTimeOffset? throughUtcExclusive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = await accountService.GetAccountAsync(localAccountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The mail account no longer exists.");
        var cutoffClause = cutoffUtc is null ? string.Empty : " AND m.CreationDate >= ?";
        var throughClause = throughUtcExclusive is null ? string.Empty : " AND m.CreationDate < ?";
        var sql = $$"""
            SELECT m.UniqueId, m.Id AS ProviderMessageId, m.FileId, m.Subject, m.FromAddress, m.FromName, m.CreationDate,
                   m.IsRead, m.IsFlagged, m.HasAttachments,
                   m.ThreadId, m.Importance, m.ImapUid, m.ImapUidValidity, f.RemoteFolderId,
                   f.UidValidity AS FolderUidValidity, f.SpecialFolderType
            FROM MailCopy m
            INNER JOIN MailItemFolder f ON f.Id = m.FolderId
            WHERE f.MailAccountId = ? AND m.IsDraft = 0 AND f.RemoteFolderId IS NOT NULL AND f.RemoteFolderId <> ''
              AND f.SpecialFolderType NOT IN (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
              {{cutoffClause}}{{throughClause}}
            ORDER BY m.CreationDate DESC, m.Id;
            """;
        var arguments = new List<object>
        {
            account.Id,
            (int)SpecialFolderType.Draft, (int)SpecialFolderType.Deleted, (int)SpecialFolderType.Junk,
            (int)SpecialFolderType.Chat, (int)SpecialFolderType.Category, (int)SpecialFolderType.Unread,
            (int)SpecialFolderType.Forums, (int)SpecialFolderType.Updates, (int)SpecialFolderType.Personal,
            (int)SpecialFolderType.Promotions, (int)SpecialFolderType.Social, (int)SpecialFolderType.More,
            (int)SpecialFolderType.Other,
        };
        if (cutoffUtc is not null) arguments.Add(cutoffUtc.Value.UtcDateTime);
        if (throughUtcExclusive is not null) arguments.Add(throughUtcExclusive.Value.UtcDateTime);
        var rows = await databaseService.Connection.QueryAsync<CandidateRow>(sql, arguments.ToArray()).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = rows.Select(row => ToCandidate(account.ProviderType, account.Address, row))
            .GroupBy(x => x.RemoteMessageId, StringComparer.Ordinal)
            .Select(group => group.First() with
            {
                RemoteFolderIds = group.SelectMany(x => x.RemoteFolderIds).Distinct(StringComparer.Ordinal).ToArray(),
                FileIds = group.SelectMany(x => x.FileIds).Distinct().ToArray(),
                IsOutgoing = group.Any(x => x.IsOutgoing),
            })
            .OrderByDescending(x => x.ReceivedAt)
            .ThenBy(x => x.RemoteMessageId, StringComparer.Ordinal)
            .ToList();
        var latestOutgoingByThread = candidates
            .Where(x => x.IsOutgoing && !string.IsNullOrWhiteSpace(x.ThreadId))
            .GroupBy(x => x.ThreadId!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Max(y => y.ReceivedAt), StringComparer.Ordinal);
        return candidates.Select(candidate => candidate with
        {
            HasLaterOutgoingReply = !string.IsNullOrWhiteSpace(candidate.ThreadId) &&
                                    latestOutgoingByThread.TryGetValue(candidate.ThreadId, out var sentAt) &&
                                    sentAt > candidate.ReceivedAt,
        }).ToArray();
    }

    public async Task<IntelligenceMessageCandidate?> FindCandidateAsync(
        Guid localAccountId,
        string messageId,
        CancellationToken cancellationToken = default)
        => (await GetCandidatesAsync(localAccountId, null, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => string.Equals(x.RemoteMessageId, messageId, StringComparison.Ordinal) ||
                                 string.Equals(x.ProviderMessageId, messageId, StringComparison.Ordinal));

    public async Task<SemanticMailContent> GetContentAsync(
        Guid localAccountId,
        IntelligenceMessageCandidate candidate,
        CancellationToken cancellationToken = default)
        => await ResolveContentAsync(
            mimeFileService,
            localAccountId,
            candidate.FileIds,
            async () =>
            {
                var bodySynchronizer = await synchronizationManager.GetSynchronizerAsync(localAccountId).ConfigureAwait(false)
                    as ISemanticMailBodySynchronizer
                    ?? throw new NotSupportedException("Mail body retrieval is not supported for this account.");
                return await bodySynchronizer.GetSemanticBodyAsync(candidate.Locator, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

    internal static async Task<SemanticMailContent> ResolveContentAsync(
        IMimeFileService mimeFileService,
        Guid localAccountId,
        IReadOnlyList<Guid> fileIds,
        Func<Task<SemanticMailContent>> resolveRemoteAsync,
        CancellationToken cancellationToken = default)
    {
        foreach (var fileId in fileIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await mimeFileService.IsMimeExistAsync(localAccountId, fileId).ConfigureAwait(false))
                continue;
            var mime = await mimeFileService.GetMimeMessageInformationAsync(fileId, localAccountId, cancellationToken).ConfigureAwait(false);
            return GetLocalContent(mime.MimeMessage);
        }
        return await resolveRemoteAsync().ConfigureAwait(false);
    }

    private static IntelligenceMessageCandidate ToCandidate(MailProviderType providerType, string accountAddress, CandidateRow row)
    {
        var canonical = ToRemoteMessageId(providerType, row);
        var uidValidity = row.ImapUidValidity == 0 ? row.FolderUidValidity : row.ImapUidValidity;
        var isOutgoing = row.SpecialFolderType == SpecialFolderType.Sent ||
                         string.Equals(row.FromAddress, accountAddress, StringComparison.OrdinalIgnoreCase);
        return new(row.UniqueId, canonical, row.ProviderMessageId, [row.FileId], row.Subject ?? string.Empty,
            row.FromAddress ?? string.Empty, row.FromName ?? string.Empty, row.CreationDate, row.ThreadId, isOutgoing,
            row.IsRead, row.IsFlagged, row.HasAttachments, false,
            row.Importance.ToString().ToLowerInvariant(), [row.RemoteFolderId],
            new MailBodyLocator(canonical, row.RemoteFolderId, row.ImapUid, uidValidity, row.ProviderMessageId));
    }

    private static string ToRemoteMessageId(MailProviderType providerType, AvailabilityRow row) => providerType switch
    {
        MailProviderType.Outlook => RemoteMessageId.ForOutlook(row.ProviderMessageId),
        MailProviderType.Gmail => RemoteMessageId.ForGmail(row.ProviderMessageId),
        MailProviderType.IMAP4 => RemoteMessageId.ForImap(row.RemoteFolderId,
            row.ImapUidValidity == 0 ? row.FolderUidValidity : row.ImapUidValidity, row.ImapUid),
        _ => throw new NotSupportedException($"Mail intelligence is not supported for {providerType}.")
    };

    private static DateTimeOffset ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value),
        DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };

    private static SemanticMailContent GetLocalContent(MimeKit.MimeMessage message)
    {
        var body = !string.IsNullOrWhiteSpace(message.HtmlBody)
            ? new MailBodyContent(MailBodyFormat.Html, message.HtmlBody)
            : !string.IsNullOrWhiteSpace(message.TextBody)
                ? new MailBodyContent(MailBodyFormat.PlainText, message.TextBody)
                : new MailBodyContent(MailBodyFormat.PlainText, string.Empty);
        return new SemanticMailContent(
            body,
            message.From.Mailboxes.Select(x => new MailAddress(x.Address, x.Name)).ToArray(),
            message.To.Mailboxes.Select(x => x.Address).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray(),
            message.Cc.Mailboxes.Select(x => x.Address).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray());
    }

    private class AvailabilityRow
    {
        public string ProviderMessageId { get; set; } = string.Empty;
        public DateTime CreationDate { get; set; }
        public uint ImapUid { get; set; }
        public uint ImapUidValidity { get; set; }
        public string RemoteFolderId { get; set; } = string.Empty;
        public uint FolderUidValidity { get; set; }
    }

    private sealed class CandidateRow : AvailabilityRow
    {
        public Guid UniqueId { get; set; }
        public Guid FileId { get; set; }
        public string? Subject { get; set; }
        public string? FromAddress { get; set; }
        public string? FromName { get; set; }
        public bool IsRead { get; set; }
        public bool IsFlagged { get; set; }
        public bool HasAttachments { get; set; }
        public string? ThreadId { get; set; }
        public MailImportance Importance { get; set; }
        public SpecialFolderType SpecialFolderType { get; set; }
    }
}
