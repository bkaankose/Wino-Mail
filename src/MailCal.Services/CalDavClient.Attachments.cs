#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.CardDav;
using IcalCalendar = Ical.Net.Calendar;

namespace MailCal.Services;

/// <summary>
/// Event attachments: ATTACH properties (RFC 5545 section 3.8.1.1), downloads, and managed
/// attachments (RFC 8607) where the server advertises <c>calendar-managed-attachments</c>.
/// </summary>
public sealed partial class CalDavClient
{
    private const string ManagedIdParameter = "MANAGED-ID";
    private const string CalManagedIdHeader = "Cal-Managed-ID";
    private const int AttachmentCopyBufferSize = 81920;

    private static readonly string[] FileNameParameters = ["FILENAME", "X-APPLE-FILENAME", "X-FILENAME"];

    public IReadOnlyList<CalDavEventAttachment> ReadEventAttachments(string icsContent, string remoteEventId)
    {
        if (string.IsNullOrWhiteSpace(icsContent))
            return Array.Empty<CalDavEventAttachment>();

        try
        {
            var calendar = IcalCalendar.Load(icsContent);
            var events = calendar?.Events?.Where(value => value != null).ToList() ?? [];
            if (events.Count == 0)
                return Array.Empty<CalDavEventAttachment>();

            var separatorIndex = remoteEventId?.IndexOf("::", StringComparison.Ordinal) ?? -1;
            var uid = separatorIndex >= 0 ? remoteEventId![..separatorIndex] : remoteEventId ?? string.Empty;
            var occurrenceKey = separatorIndex >= 0 ? remoteEventId![(separatorIndex + 2)..] : string.Empty;

            var sameUid = events.Where(value => string.Equals(value.Uid, uid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameUid.Count == 0)
                sameUid = events;

            var master = sameUid.FirstOrDefault(value => GetRecurrenceId(value) == null) ?? sameUid[0];
            var target = master;

            // An occurrence key is expressed in the master's value type, so without a master start
            // there is nothing to compare against and the master's attachments apply.
            if (!string.IsNullOrWhiteSpace(occurrenceKey) && master.Start is { } masterStart)
            {
                target = sameUid.FirstOrDefault(value =>
                    GetRecurrenceId(value) is { } recurrenceId &&
                    string.Equals(CalDavOccurrenceKey.Create(recurrenceId, masterStart), occurrenceKey, StringComparison.OrdinalIgnoreCase)) ?? master;
            }

            return ParseAttachments(target);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to read attachments from a CalDAV ICS payload.");
            return Array.Empty<CalDavEventAttachment>();
        }
    }

    public async Task<long> DownloadAttachmentAsync(
        CalDavConnectionSettings connectionSettings,
        Uri attachmentUri,
        bool useAccountCredentials,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachmentUri);
        ArgumentNullException.ThrowIfNull(destination);

        if (!attachmentUri.IsAbsoluteUri ||
            (attachmentUri.Scheme != Uri.UriSchemeHttps && attachmentUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new NotSupportedException($"Calendar attachments with the '{attachmentUri.Scheme}' scheme cannot be downloaded.");
        }

        var authentication = new DavAuthenticationProfile { Kind = DavAuthenticationKind.Anonymous };
        if (useAccountCredentials)
        {
            ValidateConnectionSettings(connectionSettings);
            authentication = CreateAuthentication(connectionSettings);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, attachmentUri);
        using var response = await _davTransport.SendAsync(request, authentication, cancellationToken).ConfigureAwait(false);
        await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        if (response.Content == null)
            return 0;

        if (response.Content.Headers.ContentLength is long announced && announced > maximumBytes)
            throw new InvalidDataException($"The calendar attachment is larger than {maximumBytes} bytes.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[AttachmentCopyBufferSize];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException($"The calendar attachment is larger than {maximumBytes} bytes.");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    public async Task<CalDavManagedAttachmentResult> AddManagedAttachmentAsync(
        CalDavConnectionSettings connectionSettings,
        string exactHref,
        string eTag,
        string fileName,
        string contentType,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);
        ArgumentNullException.ThrowIfNull(content);

        if (string.IsNullOrWhiteSpace(exactHref))
            throw new ArgumentException("The exact CalDAV resource href is required to add an attachment.", nameof(exactHref));

        var safeFileName = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName.Trim();
        var uri = AppendQuery(new Uri(exactHref, UriKind.Absolute), "action=attachment-add");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(content)
        };

        request.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            ? mediaType
            : new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.TryAddWithoutValidation("Content-Disposition", BuildContentDisposition(safeFileName));

        if (!string.IsNullOrWhiteSpace(eTag))
            request.Headers.TryAddWithoutValidation("If-Match", eTag);

        using var response = await SendAsync(connectionSettings, request, cancellationToken).ConfigureAwait(false);
        return await ReadManagedAttachmentResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CalDavManagedAttachmentResult> RemoveManagedAttachmentAsync(
        CalDavConnectionSettings connectionSettings,
        string exactHref,
        string eTag,
        string managedId,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);

        if (string.IsNullOrWhiteSpace(exactHref))
            throw new ArgumentException("The exact CalDAV resource href is required to remove an attachment.", nameof(exactHref));

        if (string.IsNullOrWhiteSpace(managedId))
            throw new ArgumentException("The managed attachment ID is required.", nameof(managedId));

        var uri = AppendQuery(
            new Uri(exactHref, UriKind.Absolute),
            $"action=attachment-remove&managed-id={Uri.EscapeDataString(managedId)}");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent([])
        };

        if (!string.IsNullOrWhiteSpace(eTag))
            request.Headers.TryAddWithoutValidation("If-Match", eTag);

        using var response = await SendAsync(connectionSettings, request, cancellationToken).ConfigureAwait(false);
        return await ReadManagedAttachmentResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every ATTACH property of <paramref name="sourceEvent"/> that has a URI or inline data.</summary>
    internal static IReadOnlyList<CalDavEventAttachment> ParseAttachments(CalendarEvent? sourceEvent)
    {
        var attachments = sourceEvent?.Attachments;
        if (attachments == null || attachments.Count == 0)
            return Array.Empty<CalDavEventAttachment>();

        var result = new List<CalDavEventAttachment>();
        foreach (var attachment in attachments)
        {
            var parsed = ParseAttachment(attachment);
            if (parsed != null)
                result.Add(parsed);
        }

        return result.ToArray();
    }

    private static CalDavEventAttachment? ParseAttachment(Attachment? attachment)
    {
        if (attachment == null)
            return null;

        var managedId = GetParameter(attachment, ManagedIdParameter);
        var contentType = attachment.FormatType ?? GetParameter(attachment, "FMTTYPE");
        var fileName = FileNameParameters.Select(name => GetParameter(attachment, name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

        if (attachment.Data is { Length: > 0 } data)
        {
            var inlineId = CalDavEventAttachment.InlineIdPrefix + Convert.ToHexString(SHA256.HashData(data))[..32].ToLowerInvariant();

            return new CalDavEventAttachment
            {
                FileName = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName,
                ContentType = contentType ?? string.Empty,
                Size = data.Length,
                ManagedId = managedId,
                InlineData = data,
                RemoteAttachmentId = string.IsNullOrWhiteSpace(managedId) ? inlineId : managedId
            };
        }

        var uri = attachment.Uri?.OriginalString?.Trim();
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = GetLastSegment(uri);

        _ = long.TryParse(GetParameter(attachment, "SIZE"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var size);

        return new CalDavEventAttachment
        {
            Uri = uri,
            FileName = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName,
            ContentType = contentType ?? string.Empty,
            Size = Math.Max(0, size),
            ManagedId = managedId,
            RemoteAttachmentId = string.IsNullOrWhiteSpace(managedId) ? uri : managedId
        };
    }

    private static string GetParameter(Attachment attachment, string name)
        => attachment.Parameters?.Get(name)?.Trim() ?? string.Empty;

    private static string GetLastSegment(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return string.Empty;

        var segment = parsed.Segments.LastOrDefault(value => value.Trim('/').Length > 0)?.Trim('/') ?? string.Empty;
        return Uri.UnescapeDataString(segment);
    }

    private static Uri AppendQuery(Uri uri, string query)
    {
        var builder = new UriBuilder(uri);
        var existing = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(existing) ? query : $"{existing}&{query}";
        return builder.Uri;
    }

    /// <summary>
    /// RFC 8607 section 3.3.1 example form. Non-ASCII names get an ASCII fallback plus the RFC 6266
    /// <c>filename*</c> parameter.
    /// </summary>
    internal static string BuildContentDisposition(string fileName)
    {
        var fallback = new StringBuilder(fileName.Length);
        var isAscii = true;

        foreach (var character in fileName)
        {
            if (character is < (char)0x20 or > (char)0x7E)
            {
                isAscii = false;
                fallback.Append('_');
            }
            else if (character is '"' or '\\')
            {
                fallback.Append('_');
            }
            else
            {
                fallback.Append(character);
            }
        }

        var value = $"attachment;filename=\"{fallback}\"";
        return isAscii ? value : $"{value};filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    private static async Task<CalDavManagedAttachmentResult> ReadManagedAttachmentResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var managedId = response.Headers.TryGetValues(CalManagedIdHeader, out var values)
            ? values.FirstOrDefault()?.Trim() ?? string.Empty
            : string.Empty;

        var icsContent = string.Empty;
        var mediaType = response.Content?.Headers.ContentType?.MediaType;
        if (response.Content != null && string.Equals(mediaType, "text/calendar", StringComparison.OrdinalIgnoreCase))
            icsContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return new CalDavManagedAttachmentResult
        {
            ETag = response.Headers.ETag?.ToString() ?? string.Empty,
            ManagedId = managedId,
            Location = response.Headers.Location?.ToString() ?? string.Empty,
            IcsContent = icsContent.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) ? icsContent : string.Empty
        };
    }
}

/// <summary>Text-level edits of ATTACH properties that keep every other line of a resource as it is.</summary>
public static class CalDavAttachmentIcs
{
    /// <summary>
    /// Removes the ATTACH properties whose MANAGED-ID is in <paramref name="managedIds"/>. Lines are
    /// unfolded only to inspect them; the kept lines are written back exactly as they were.
    /// </summary>
    public static string RemoveManagedAttachments(string icsContent, IReadOnlyCollection<string> managedIds)
    {
        ArgumentNullException.ThrowIfNull(icsContent);

        if (managedIds == null || managedIds.Count == 0)
            return icsContent;

        var ids = new HashSet<string>(managedIds.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal);
        var physicalLines = icsContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>(physicalLines.Length);
        var logical = new List<string>();

        void Flush()
        {
            if (logical.Count == 0)
                return;

            var unfolded = string.Concat(logical.Select((line, index) => index == 0 ? line : line[1..]));
            if (!IsRemovedAttach(unfolded, ids))
                output.AddRange(logical);

            logical.Clear();
        }

        foreach (var line in physicalLines)
        {
            if (logical.Count > 0 && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                logical.Add(line);
                continue;
            }

            Flush();
            logical.Add(line);
        }

        Flush();
        return string.Join("\r\n", output);
    }

    private static bool IsRemovedAttach(string line, HashSet<string> managedIds)
    {
        if (!line.StartsWith("ATTACH", StringComparison.OrdinalIgnoreCase) || line.Length <= 6 || line[6] is not (';' or ':'))
            return false;

        var inQuotes = false;
        var parameterStart = 7;

        for (var index = 6; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes || character is not (';' or ':'))
                continue;

            if (index > parameterStart && IsManagedIdParameter(line[parameterStart..index], managedIds))
                return true;

            if (character == ':')
                return false;

            parameterStart = index + 1;
        }

        return false;
    }

    private static bool IsManagedIdParameter(string parameter, HashSet<string> managedIds)
    {
        var separator = parameter.IndexOf('=');
        if (separator <= 0 || !string.Equals(parameter[..separator], "MANAGED-ID", StringComparison.OrdinalIgnoreCase))
            return false;

        return managedIds.Contains(parameter[(separator + 1)..].Trim('"'));
    }
}
