#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Extensions;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Services;

namespace MailCal.Core.Synchronizers.CalDav;

/// <summary>
/// Event attachments. Every server: ATTACH URIs and inline data are imported as rows and can be
/// downloaded. Servers with <c>calendar-managed-attachments</c> (RFC 8607, iCloud): files are
/// uploaded with <c>?action=attachment-add</c> and removed with <c>?action=attachment-remove</c>,
/// falling back to a PUT without the ATTACH property when the server keeps it.
/// </summary>
public sealed partial class CalDavSynchronizationEngine
{
    internal const string ManagedAttachmentsCapability = "calendar-managed-attachments";

    /// <summary>Largest attachment that is downloaded; the body is streamed to disk.</summary>
    internal const long MaximumAttachmentDownloadBytes = 100L * 1024 * 1024;

    /// <summary>Largest file that is uploaded; the DAV transport buffers the request body in memory.</summary>
    internal const long MaximumAttachmentUploadBytes = 50L * 1024 * 1024;

    public async Task DownloadAttachmentAsync(
        MailAccount account,
        CalendarItem item,
        CalendarAttachment attachment,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(localFilePath));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        // Inline data stored at import, or an earlier download: copy the local file.
        if (attachment.IsDownloaded && !string.IsNullOrWhiteSpace(attachment.LocalFilePath) && File.Exists(attachment.LocalFilePath))
        {
            if (!PathsEqual(attachment.LocalFilePath, localFilePath))
                File.Copy(attachment.LocalFilePath, localFilePath, overwrite: true);

            await _calendarService.MarkAttachmentDownloadedAsync(attachment.Id, localFilePath).ConfigureAwait(false);
            return;
        }

        var snapshot = await _changeProcessor
            .GetCalendarItemIcsAsync(account.Id, item.CalendarId, item.Id)
            .ConfigureAwait(false);

        var source = string.IsNullOrWhiteSpace(snapshot?.IcsContent)
            ? null
            : _client.ReadEventAttachments(snapshot.IcsContent, item.RemoteEventId)
                .FirstOrDefault(value => string.Equals(value.RemoteAttachmentId, attachment.RemoteAttachmentId, StringComparison.Ordinal));

        var partialPath = localFilePath + ".part";

        try
        {
            if (source?.InlineData != null)
            {
                await File.WriteAllBytesAsync(partialPath, source.InlineData, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var attachmentUri = ResolveAttachmentUri(source?.Uri, attachment.RemoteAttachmentId, snapshot?.ExactHref)
                                    ?? throw new InvalidOperationException(
                                        "The CalDAV attachment is no longer part of the stored event. Synchronize the calendar and retry.");

                var (connection, calendar) = await ResolveWriteContextAsync(account, item.CalendarId, cancellationToken).ConfigureAwait(false);
                var state = await _stateStore.GetAsync(account.Id, cancellationToken).ConfigureAwait(false);
                var useCredentials = IsAccountOrigin(attachmentUri, connection.ServiceUri, state, calendar.RemoteCalendarId, snapshot?.ExactHref);

                if (!useCredentials && attachmentUri.Scheme != Uri.UriSchemeHttps)
                    throw new NotSupportedException("Calendar attachments from other servers are only downloaded over HTTPS.");

                await using (var stream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await _client
                        .DownloadAttachmentAsync(connection, attachmentUri, useCredentials, stream, MaximumAttachmentDownloadBytes, cancellationToken)
                        .ConfigureAwait(false);
                }

                Logger.Information(
                    "Downloaded CalDAV attachment {AttachmentId} of {CalendarItemId} ({Origin} origin)",
                    attachment.Id,
                    item.Id,
                    useCredentials ? "account" : "foreign");
            }

            File.Move(partialPath, localFilePath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(partialPath);
            throw;
        }

        await _calendarService.MarkAttachmentDownloadedAsync(attachment.Id, localFilePath).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads new files and removes dropped managed attachments after the event itself was
    /// written, then stores the server's resource (ICS copy, ETag, attachment rows) for every row
    /// of the series. A file that fails is logged and skipped: the event write already succeeded.
    /// </summary>
    private async Task ApplyAttachmentChangesAsync(
        MailAccount account,
        CalendarItem item,
        IReadOnlyList<CalendarEventComposeAttachmentDraft>? attachments,
        bool isNewEvent,
        CancellationToken cancellationToken)
    {
        if (attachments == null || (isNewEvent && attachments.Count == 0))
            return;

        var existing = isNewEvent
            ? new List<CalendarAttachment>()
            : await _calendarService.GetAttachmentsAsync(item.Id).ConfigureAwait(false) ?? new List<CalendarAttachment>();

        var wantedIds = attachments.Select(draft => draft.Id).ToHashSet();
        var uploads = attachments.Where(draft => !existing.Any(row => row.Id == draft.Id)).ToList();
        var removals = existing.Where(row => !wantedIds.Contains(row.Id)).ToList();

        if (uploads.Count == 0 && removals.Count == 0)
            return;

        var state = await _stateStore.GetAsync(account.Id, cancellationToken).ConfigureAwait(false);
        if (!state.HasDavCapability(ManagedAttachmentsCapability))
        {
            Logger.Warning(
                "CalDAV server of {AccountName} does not support managed attachments; {UploadCount} added and {RemovalCount} removed attachments of {RemoteEventId} are ignored",
                account.Name,
                uploads.Count,
                removals.Count,
                item.RemoteEventId);
            return;
        }

        var (connection, calendar) = await ResolveWriteContextAsync(account, item.CalendarId, cancellationToken).ConfigureAwait(false);
        using var calendarLock = await CalDavConnection.LockCalendarAsync(item.CalendarId, cancellationToken).ConfigureAwait(false);

        var snapshot = await RequireResourceSnapshotAsync(account, item).ConfigureAwait(false);
        var resource = new AttachmentResource(snapshot.ExactHref, snapshot.ETag, snapshot.IcsContent);
        var changed = false;

        foreach (var draft in uploads)
        {
            try
            {
                var uploaded = await UploadAttachmentAsync(connection, item, resource, draft, cancellationToken).ConfigureAwait(false);
                if (uploaded == null)
                    continue;

                resource = uploaded;
                changed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Error(ex, "Could not upload attachment {FileName} to CalDAV event {RemoteEventId}", draft.FileName, item.RemoteEventId);
            }
        }

        foreach (var row in removals)
        {
            try
            {
                var removed = await RemoveAttachmentAsync(connection, calendar, item, resource, row, cancellationToken).ConfigureAwait(false);
                if (removed == null)
                    continue;

                resource = removed;
                changed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Error(ex, "Could not remove attachment {FileName} from CalDAV event {RemoteEventId}", row.FileName, item.RemoteEventId);
            }
        }

        if (!changed)
            return;

        await SaveSeriesSnapshotAsync(account, item, resource.ExactHref, resource.ETag, resource.IcsContent).ConfigureAwait(false);
        await SaveSeriesAttachmentRowsAsync(item, resource.IcsContent).ConfigureAwait(false);
    }

    private async Task<AttachmentResource?> UploadAttachmentAsync(
        CalDavConnectionSettings connection,
        CalendarItem item,
        AttachmentResource resource,
        CalendarEventComposeAttachmentDraft draft,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(draft.FilePath) || !File.Exists(draft.FilePath))
        {
            Logger.Warning("Attachment file of {RemoteEventId} is missing and was not uploaded", item.RemoteEventId);
            return null;
        }

        var fileInfo = new FileInfo(draft.FilePath);
        if (fileInfo.Length > MaximumAttachmentUploadBytes)
        {
            Logger.Warning("Attachment of {RemoteEventId} is {Size} bytes, above the upload limit, and was not uploaded", item.RemoteEventId, fileInfo.Length);
            return null;
        }

        var fileName = string.IsNullOrWhiteSpace(draft.FileName) ? fileInfo.Name : draft.FileName;
        var content = await File.ReadAllBytesAsync(draft.FilePath, cancellationToken).ConfigureAwait(false);

        var result = await _client
            .AddManagedAttachmentAsync(connection, resource.ExactHref, resource.ETag, fileName, MimeTypes.GetMimeType(fileName), content, cancellationToken)
            .ConfigureAwait(false);

        var updated = await ReadBackAsync(connection, resource.ExactHref, result.ETag, result.IcsContent, cancellationToken).ConfigureAwait(false);
        var managedId = string.IsNullOrWhiteSpace(result.ManagedId)
            ? FindNewManagedId(resource.IcsContent, updated.IcsContent, item.RemoteEventId, fileName)
            : result.ManagedId;

        Logger.Information("Uploaded managed attachment {ManagedId} to CalDAV event {RemoteEventId}", managedId, item.RemoteEventId);
        return updated;
    }

    /// <summary>
    /// Removes one managed attachment. Rows that are not managed by the server (a plain ATTACH URI
    /// written by another client) are left alone: Mail-Cal does not own them.
    /// </summary>
    private async Task<AttachmentResource?> RemoveAttachmentAsync(
        CalDavConnectionSettings connection,
        CalDavCalendar calendar,
        CalendarItem item,
        AttachmentResource resource,
        CalendarAttachment row,
        CancellationToken cancellationToken)
    {
        var managedId = row.RemoteAttachmentId ?? string.Empty;
        var isManaged = !string.IsNullOrWhiteSpace(managedId) && _client
            .ReadEventAttachments(resource.IcsContent, item.RemoteEventId)
            .Any(value => string.Equals(value.ManagedId, managedId, StringComparison.Ordinal));

        if (!isManaged)
        {
            Logger.Information("Attachment {AttachmentId} of {RemoteEventId} is not a managed attachment and was not removed", row.Id, item.RemoteEventId);
            return null;
        }

        var result = await _client
            .RemoveManagedAttachmentAsync(connection, resource.ExactHref, resource.ETag, managedId, cancellationToken)
            .ConfigureAwait(false);

        var updated = await ReadBackAsync(connection, resource.ExactHref, result.ETag, result.IcsContent, cancellationToken).ConfigureAwait(false);
        if (!HasManagedAttachment(updated.IcsContent, item.RemoteEventId, managedId))
            return updated;

        // iCloud answers the remove action with 200 and keeps the attachment. RFC 8607 section 3.6
        // allows removing it by writing the resource without the ATTACH property.
        Logger.Information("CalDAV server kept managed attachment {ManagedId} after attachment-remove; removing it with PUT", managedId);

        var icsContent = CalDavAttachmentIcs.RemoveManagedAttachments(updated.IcsContent, new[] { managedId });
        var write = await _client
            .UpsertCalendarEventAsync(connection, calendar, new CalDavWriteRequest
            {
                RemoteEventId = item.RemoteEventId.GetProviderRemoteEventId(),
                ExactHref = updated.ExactHref,
                ETag = updated.ETag,
                IcsContent = icsContent
            }, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(write.ETag)
            ? await ReadBackAsync(connection, updated.ExactHref, string.Empty, string.Empty, cancellationToken).ConfigureAwait(false)
            : new AttachmentResource(updated.ExactHref, write.ETag, icsContent);
    }

    /// <summary>
    /// The resource after an attachment action: the server's answer when it returned both the
    /// calendar data and an ETag (RFC 8607 allows either), otherwise a GET. iCloud returns neither.
    /// </summary>
    private async Task<AttachmentResource> ReadBackAsync(
        CalDavConnectionSettings connection,
        string exactHref,
        string eTag,
        string icsContent,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(eTag) && !string.IsNullOrWhiteSpace(icsContent))
            return new AttachmentResource(exactHref, eTag, icsContent);

        var resource = await _client.GetEventResourceAsync(connection, exactHref, cancellationToken).ConfigureAwait(false);
        if (resource == null || resource.IsDeleted || string.IsNullOrWhiteSpace(resource.IcsContent))
            throw new InvalidOperationException("The CalDAV event disappeared while its attachments were changed.");

        return new AttachmentResource(
            string.IsNullOrWhiteSpace(resource.ExactHref) ? exactHref : resource.ExactHref,
            string.IsNullOrWhiteSpace(resource.ETag) ? eTag : resource.ETag,
            resource.IcsContent);
    }

    /// <summary>Stores the attachment rows of the item and, for a series, of every row sharing the resource.</summary>
    private async Task SaveSeriesAttachmentRowsAsync(CalendarItem item, string icsContent)
    {
        var rows = new Dictionary<Guid, string> { [item.Id] = item.RemoteEventId };
        var seriesParentId = item.IsRecurringChild
            ? item.RecurringCalendarItemId
            : item.IsRecurringParent ? item.Id : null;

        if (seriesParentId is { } parentId)
        {
            var parent = await _calendarService.GetCalendarItemAsync(parentId).ConfigureAwait(false);
            if (parent != null)
                rows.TryAdd(parent.Id, parent.RemoteEventId);

            var children = await _calendarService.GetRecurringChildrenAsync(parentId).ConfigureAwait(false);
            foreach (var child in children ?? [])
                rows.TryAdd(child.Id, child.RemoteEventId);
        }

        foreach (var (calendarItemId, remoteEventId) in rows)
        {
            await CalDavAttachmentRows.ReplaceAsync(
                _calendarService,
                calendarItemId,
                _client.ReadEventAttachments(icsContent, remoteEventId),
                attachmentsRoot: null,
                storeInlineData: false).ConfigureAwait(false);
        }
    }

    private bool HasManagedAttachment(string icsContent, string remoteEventId, string managedId)
        => _client.ReadEventAttachments(icsContent, remoteEventId)
            .Any(value => string.Equals(value.ManagedId, managedId, StringComparison.Ordinal));

    private string FindNewManagedId(string previousIcs, string currentIcs, string remoteEventId, string fileName)
    {
        var previous = _client.ReadEventAttachments(previousIcs, remoteEventId)
            .Select(value => value.ManagedId)
            .ToHashSet(StringComparer.Ordinal);

        var added = _client.ReadEventAttachments(currentIcs, remoteEventId)
            .Where(value => !string.IsNullOrWhiteSpace(value.ManagedId) && !previous.Contains(value.ManagedId))
            .ToList();

        return (added.FirstOrDefault(value => string.Equals(value.FileName, fileName, StringComparison.Ordinal)) ?? added.FirstOrDefault())?.ManagedId
               ?? string.Empty;
    }

    private static Uri? ResolveAttachmentUri(string? sourceUri, string? remoteAttachmentId, string? resourceHref)
    {
        foreach (var candidate in new[] { sourceUri, remoteAttachmentId })
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith(CalDavEventAttachment.InlineIdPrefix, StringComparison.Ordinal))
                continue;

            if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp))
            {
                return absolute;
            }

            // Only the ATTACH value itself may be relative to the resource; a MANAGED-ID is not a URI.
            if (ReferenceEquals(candidate, sourceUri) &&
                Uri.TryCreate(resourceHref, UriKind.Absolute, out var baseUri) &&
                Uri.TryCreate(baseUri, candidate, out var relative))
            {
                return relative;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the URI belongs to the CalDAV server itself: the service URL, a calendar home,
    /// the calendar or the event resource. Only those origins receive the account credentials.
    /// </summary>
    internal static bool IsAccountOrigin(
        Uri target,
        Uri? serviceUri,
        CalDavAccountState? state,
        string? remoteCalendarId,
        string? resourceHref)
    {
        var origins = new List<Uri>();
        if (serviceUri != null)
            origins.Add(serviceUri);

        var hrefs = new List<string?>(state?.CalendarHomeHrefs ?? new List<string>()) { state?.PrincipalHref, remoteCalendarId, resourceHref };
        foreach (var href in hrefs)
        {
            if (string.IsNullOrWhiteSpace(href))
                continue;

            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                origins.Add(absolute);
        }

        return origins.Any(origin =>
            string.Equals(origin.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(origin.Host, target.Host, StringComparison.OrdinalIgnoreCase) &&
            origin.Port == target.Port);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.Ordinal);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Debug(ex, "Could not delete a partial attachment download");
        }
    }

    private sealed record AttachmentResource(string ExactHref, string ETag, string IcsContent);
}
