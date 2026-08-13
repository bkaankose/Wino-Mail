using System;
using System.IO;
using System.Linq;
using System.Text;
using MimeKit;

namespace Wino.Core.Extensions;

public static class CalendarInvitationExtensions
{
    public static string ExtractInvitationUid(this MimeMessage message)
    {
        if (message == null)
        {
            return null;
        }

        var icsContent = GetCalendarContent(message);
        if (string.IsNullOrWhiteSpace(icsContent))
        {
            return null;
        }

        var unfolded = UnfoldIcs(icsContent);
        var veventSection = ExtractFirstVEventSection(unfolded);
        if (string.IsNullOrWhiteSpace(veventSection))
        {
            return null;
        }

        return TryReadIcsProperty(veventSection, "UID", out var uid)
            ? uid
            : null;
    }

    private static string GetCalendarContent(MimeMessage message)
    {
        var textPart = message.BodyParts
            .OfType<TextPart>()
            .FirstOrDefault(p => p.ContentType?.MimeType?.Equals("text/calendar", StringComparison.OrdinalIgnoreCase) == true);

        if (textPart != null)
        {
            return textPart.Text;
        }

        var mimePart = message.BodyParts
            .OfType<MimePart>()
            .FirstOrDefault(p => p.ContentType?.MimeType?.Equals("text/calendar", StringComparison.OrdinalIgnoreCase) == true);

        if (mimePart == null)
        {
            return null;
        }

        using var stream = new MemoryStream();
        mimePart.Content.DecodeTo(stream);
        var bytes = stream.ToArray();
        if (bytes.Length == 0)
        {
            return null;
        }

        var charset = mimePart.ContentType?.Charset;
        var encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        return encoding.GetString(bytes);
    }

    private static string UnfoldIcs(string content)
        => content
            .Replace("\r\n ", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n\t", string.Empty, StringComparison.Ordinal)
            .Replace("\n ", string.Empty, StringComparison.Ordinal)
            .Replace("\n\t", string.Empty, StringComparison.Ordinal);

    private static string ExtractFirstVEventSection(string ics)
    {
        const string beginVevent = "BEGIN:VEVENT";
        const string endVevent = "END:VEVENT";

        var beginIndex = ics.IndexOf(beginVevent, StringComparison.OrdinalIgnoreCase);
        if (beginIndex < 0)
        {
            return string.Empty;
        }

        var endIndex = ics.IndexOf(endVevent, beginIndex, StringComparison.OrdinalIgnoreCase);
        if (endIndex < 0)
        {
            return ics[beginIndex..];
        }

        return ics.Substring(beginIndex, endIndex - beginIndex + endVevent.Length);
    }

    private static bool TryReadIcsProperty(string icsSection, string propertyName, out string value)
    {
        value = string.Empty;
        var lines = icsSection.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0 || colonIndex >= line.Length - 1)
            {
                continue;
            }

            value = line[(colonIndex + 1)..].Trim();
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }
}
