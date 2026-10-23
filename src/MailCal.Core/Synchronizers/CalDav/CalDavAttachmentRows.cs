#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Serilog;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Core.Synchronizers.CalDav;

/// <summary>
/// Keeps the <see cref="CalendarAttachment"/> rows of one calendar item equal to the ATTACH
/// properties of its CalDAV event. Rows whose remote ID is unchanged keep their ID and downloaded
/// copy; removed attachments lose their row and their downloaded copy.
/// </summary>
internal static class CalDavAttachmentRows
{
    private const int MaximumFileNameLength = 120;
    private static readonly ILogger Logger = Log.ForContext(typeof(CalDavAttachmentRows));
    private static readonly char[] InvalidFileNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <param name="attachmentsRoot">
    /// The calendar attachment folder. Inline attachments are written to
    /// <c>&lt;root&gt;/&lt;itemId:N&gt;/</c>; without a root they stay undownloaded and are read
    /// from the stored ICS copy on download.
    /// </param>
    /// <param name="storeInlineData">False leaves inline data in the ICS copy (used for recurring occurrences).</param>
    public static async Task ReplaceAsync(
        ICalendarService calendarService,
        Guid calendarItemId,
        IReadOnlyList<CalDavEventAttachment>? attachments,
        string? attachmentsRoot,
        bool storeInlineData)
    {
        ArgumentNullException.ThrowIfNull(calendarService);

        var existing = await calendarService.GetAttachmentsAsync(calendarItemId).ConfigureAwait(false) ?? [];
        var existingByRemoteId = existing
            .Where(row => !string.IsNullOrWhiteSpace(row.RemoteAttachmentId))
            .GroupBy(row => row.RemoteAttachmentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var rows = new List<CalendarAttachment>();
        var seenRemoteIds = new HashSet<string>(StringComparer.Ordinal);
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var attachment in attachments ?? Array.Empty<CalDavEventAttachment>())
        {
            if (string.IsNullOrWhiteSpace(attachment.RemoteAttachmentId) || !seenRemoteIds.Add(attachment.RemoteAttachmentId))
                continue;

            existingByRemoteId.TryGetValue(attachment.RemoteAttachmentId, out var previous);

            var fileName = string.IsNullOrWhiteSpace(attachment.FileName) ? "attachment" : attachment.FileName;
            var row = new CalendarAttachment
            {
                Id = previous?.Id ?? Guid.NewGuid(),
                CalendarItemId = calendarItemId,
                RemoteAttachmentId = attachment.RemoteAttachmentId,
                FileName = fileName,
                Size = attachment.Size,
                ContentType = string.IsNullOrWhiteSpace(attachment.ContentType) ? MimeTypes.GetMimeType(fileName) : attachment.ContentType,
                LastModified = previous?.LastModified ?? DateTimeOffset.UtcNow
            };

            if (previous is { IsDownloaded: true } && !string.IsNullOrWhiteSpace(previous.LocalFilePath) && File.Exists(previous.LocalFilePath))
            {
                row.IsDownloaded = true;
                row.LocalFilePath = previous.LocalFilePath;
            }
            else if (attachment.InlineData != null && storeInlineData && !string.IsNullOrWhiteSpace(attachmentsRoot))
            {
                var path = await TryWriteInlineAsync(attachmentsRoot, calendarItemId, fileName, attachment.InlineData, usedPaths).ConfigureAwait(false);
                row.IsDownloaded = path != null;
                row.LocalFilePath = path ?? string.Empty;
            }
            else
            {
                row.LocalFilePath = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(row.LocalFilePath))
                usedPaths.Add(row.LocalFilePath);

            rows.Add(row);
        }

        if (IsUnchanged(existing, rows))
            return;

        await calendarService.DeleteAttachmentsAsync(calendarItemId).ConfigureAwait(false);

        if (rows.Count > 0)
            await calendarService.InsertOrReplaceAttachmentsAsync(rows).ConfigureAwait(false);

        foreach (var removed in existing.Where(row => !rows.Any(kept => kept.Id == row.Id) && row.IsDownloaded))
            TryDeleteOwnedFile(attachmentsRoot, removed.LocalFilePath);
    }

    /// <summary>The folder of one item's attachments: <c>&lt;root&gt;/&lt;itemId:N&gt;</c>.</summary>
    public static string GetItemFolder(string attachmentsRoot, Guid calendarItemId)
        => Path.Combine(attachmentsRoot, calendarItemId.ToString("N"));

    /// <summary>A file name that is valid on Windows and macOS.</summary>
    public static string ToSafeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        var characters = name
            .Select(character => char.IsControl(character) || InvalidFileNameCharacters.Contains(character) ? '_' : character)
            .ToArray();

        var safe = new string(characters).Trim().TrimEnd('.');
        if (safe.Length > MaximumFileNameLength)
        {
            var extension = Path.GetExtension(safe);
            safe = safe[..(MaximumFileNameLength - extension.Length)] + extension;
        }

        return string.IsNullOrWhiteSpace(safe) || safe is "." or ".." ? "attachment" : safe;
    }

    private static bool IsUnchanged(List<CalendarAttachment> existing, List<CalendarAttachment> rows)
    {
        if (existing.Count != rows.Count)
            return false;

        return rows.All(row => existing.Any(old =>
            old.Id == row.Id &&
            string.Equals(old.RemoteAttachmentId, row.RemoteAttachmentId, StringComparison.Ordinal) &&
            string.Equals(old.FileName, row.FileName, StringComparison.Ordinal) &&
            string.Equals(old.ContentType, row.ContentType, StringComparison.Ordinal) &&
            old.Size == row.Size &&
            old.IsDownloaded == row.IsDownloaded &&
            string.Equals(old.LocalFilePath ?? string.Empty, row.LocalFilePath ?? string.Empty, StringComparison.Ordinal)));
    }

    private static async Task<string?> TryWriteInlineAsync(
        string attachmentsRoot,
        Guid calendarItemId,
        string fileName,
        byte[] data,
        HashSet<string> usedPaths)
    {
        try
        {
            var folder = GetItemFolder(attachmentsRoot, calendarItemId);
            Directory.CreateDirectory(folder);

            var safeName = ToSafeFileName(fileName);
            var path = Path.Combine(folder, safeName);
            for (var index = 1; usedPaths.Contains(path); index++)
                path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(safeName)} ({index}){Path.GetExtension(safeName)}");

            await File.WriteAllBytesAsync(path, data).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not store an inline CalDAV attachment of calendar item {CalendarItemId}", calendarItemId);
            return null;
        }
    }

    private static void TryDeleteOwnedFile(string? attachmentsRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(attachmentsRoot) || string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var root = Path.GetFullPath(attachmentsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);

            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Logger.Debug(ex, "Could not delete a removed calendar attachment copy");
        }
    }
}
