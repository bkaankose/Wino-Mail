#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using SQLite;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

/// <summary>A file to add to an event, read from the path the compose page recorded.</summary>
internal sealed record ExchangeAttachmentContent(Guid DraftId, string FileName, string ContentType, byte[] Content);

/// <summary>What an event edit changes in the event's attachments: the stored ones to remove and the files to add.</summary>
internal sealed record ExchangeAttachmentPlan(IReadOnlyList<CalendarAttachment> Removed, IReadOnlyList<CalendarEventComposeAttachmentDraft> Added)
{
    public bool IsEmpty => Removed.Count == 0 && Added.Count == 0;
}

/// <summary>
/// Event attachments shared by the EWS, MAPI and ActiveSync transports: the plan an edit's wanted list makes against
/// the stored rows, and the store of the attachment metadata the server reported.
/// </summary>
internal static class ExchangeCalendarAttachments
{
    /// <summary>
    /// Null when <paramref name="wanted"/> is null, which leaves the attachments as they are. Otherwise a wanted entry
    /// whose id is a stored attachment's keeps it, a new entry is added, and a stored attachment missing from the list
    /// is removed.
    /// </summary>
    internal static ExchangeAttachmentPlan? Plan(IReadOnlyList<CalendarAttachment>? stored, IReadOnlyList<CalendarEventComposeAttachmentDraft>? wanted)
    {
        if (wanted == null)
            return null;

        stored ??= [];
        var storedIds = stored.Select(attachment => attachment.Id).ToHashSet();
        var keptIds = wanted.Select(draft => draft.Id).ToHashSet();
        return new ExchangeAttachmentPlan(
            stored.Where(attachment => !keptIds.Contains(attachment.Id)).ToList(),
            wanted.Where(draft => !storedIds.Contains(draft.Id)).ToList());
    }

    /// <summary>The files to add, read in full; a file that is no longer readable fails the edit rather than being skipped.</summary>
    internal static async Task<List<ExchangeAttachmentContent>> ReadAsync(IEnumerable<CalendarEventComposeAttachmentDraft>? drafts, CancellationToken cancellationToken)
    {
        var result = new List<ExchangeAttachmentContent>();
        foreach (var draft in drafts ?? [])
        {
            if (string.IsNullOrWhiteSpace(draft.FilePath) || !File.Exists(draft.FilePath))
                throw new FileNotFoundException("The file to attach to the event is not readable.", draft.FilePath);

            var name = string.IsNullOrWhiteSpace(draft.FileName) ? Path.GetFileName(draft.FilePath) : draft.FileName;
            var content = await File.ReadAllBytesAsync(draft.FilePath, cancellationToken).ConfigureAwait(false);
            result.Add(new ExchangeAttachmentContent(draft.Id, name, MimeTypes.GetMimeType(name), content));
        }

        return result;
    }

    /// <summary>
    /// Makes the stored attachment rows of an event match what the server reported. A row the server still has keeps its
    /// id and, while its name, size and modification time are unchanged, its downloaded copy.
    /// </summary>
    internal static async Task ReconcileAsync(SQLiteAsyncConnection connection, Guid calendarItemId, IReadOnlyList<SyncedCalendarAttachment> server)
    {
        var stored = await connection.Table<CalendarAttachment>().Where(row => row.CalendarItemId == calendarItemId).ToListAsync().ConfigureAwait(false);

        var existing = new Dictionary<string, CalendarAttachment>(StringComparer.Ordinal);
        var removed = new List<CalendarAttachment>();
        var reported = server.Select(attachment => attachment.RemoteId).ToHashSet(StringComparer.Ordinal);
        foreach (var row in stored)
        {
            if (string.IsNullOrEmpty(row.RemoteAttachmentId) || !reported.Contains(row.RemoteAttachmentId) || !existing.TryAdd(row.RemoteAttachmentId, row))
                removed.Add(row);
        }

        var written = new List<CalendarAttachment>();
        foreach (var attachment in server.GroupBy(attachment => attachment.RemoteId, StringComparer.Ordinal).Select(group => group.First()))
        {
            existing.TryGetValue(attachment.RemoteId, out var row);
            var fileName = string.IsNullOrWhiteSpace(attachment.FileName) ? "attachment" : attachment.FileName;
            var contentType = string.IsNullOrWhiteSpace(attachment.ContentType) ? MimeTypes.GetMimeType(fileName) : attachment.ContentType;
            // MAPI reuses an attachment number once its attachment is gone (MS-OXCMSG 2.2.2.6 asks only for uniqueness
            // within the message), so a changed name, size or modification time means a different file.
            var sameContent = row != null && row.Size == attachment.Size && string.Equals(row.FileName, fileName, StringComparison.Ordinal) &&
                              (attachment.LastModified == null || attachment.LastModified == row.LastModified);
            var lastModified = attachment.LastModified ?? row?.LastModified ?? DateTimeOffset.UtcNow;

            if (row != null && sameContent && row.ContentType == contentType && row.LastModified == lastModified)
                continue;

            written.Add(new CalendarAttachment
            {
                Id = row?.Id ?? Guid.NewGuid(),
                CalendarItemId = calendarItemId,
                RemoteAttachmentId = attachment.RemoteId,
                FileName = fileName,
                Size = attachment.Size,
                ContentType = contentType,
                IsDownloaded = sameContent && row?.IsDownloaded == true,
                LocalFilePath = sameContent ? row?.LocalFilePath ?? string.Empty : string.Empty,
                LastModified = lastModified
            });
        }

        if (removed.Count == 0 && written.Count == 0)
            return;

        await connection.RunInTransactionAsync(transaction =>
        {
            foreach (var row in removed)
                transaction.Delete<CalendarAttachment>(row.Id);
            foreach (var row in written)
                transaction.InsertOrReplace(row, typeof(CalendarAttachment));
        }).ConfigureAwait(false);
    }
}
