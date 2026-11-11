#nullable enable
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Wino.Services.Extensions;

namespace Wino.Services;

/// <summary>
/// Event notes are stored as HTML from the editor. DESCRIPTION is plain text (RFC 5545), so the
/// HTML goes to X-ALT-DESC;FMTTYPE=text/html, which Outlook, Thunderbird and Apple Calendar read,
/// and DESCRIPTION carries its text.
/// </summary>
public static partial class CalDavEventDescription
{
    public const string AlternateDescriptionProperty = "X-ALT-DESC";
    private const string HtmlFormatType = "text/html";

    [GeneratedRegex(@"<(?:/?[a-zA-Z][a-zA-Z0-9-]*(?:\s[^<>]*)?/?|!--[\s\S]*?--)>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    /// <summary>The DESCRIPTION text and, for HTML notes with text, the X-ALT-DESC HTML.</summary>
    public static (string? Text, string? Html) Split(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return (null, null);

        if (!HtmlTagRegex().IsMatch(description))
            return (description.Trim(), null);

        var text = HtmlAgilityPackExtensions.GetAccessibleText(description);
        return string.IsNullOrWhiteSpace(text) ? (null, null) : (text, description.Trim());
    }

    /// <summary>The notes of an imported event: X-ALT-DESC HTML when present, otherwise DESCRIPTION.</summary>
    public static string Read(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var html = calendarEvent.Properties
            .Where(property => string.Equals(property.Name, AlternateDescriptionProperty, StringComparison.OrdinalIgnoreCase))
            .Where(property => string.Equals(property.Parameters.Get("FMTTYPE"), HtmlFormatType, StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Value as string)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return html ?? calendarEvent.Description ?? string.Empty;
    }

    /// <summary>Writes DESCRIPTION and X-ALT-DESC of <paramref name="target"/> from the stored notes.</summary>
    public static void Apply(CalendarEvent target, string? description)
    {
        ArgumentNullException.ThrowIfNull(target);

        var (text, html) = Split(description);
        target.Description = text;
        target.Properties.Clear(AlternateDescriptionProperty);

        if (html == null)
            return;

        var property = new CalendarProperty(AlternateDescriptionProperty, html);
        property.Parameters.Set("FMTTYPE", HtmlFormatType);
        target.Properties.Add(property);
    }
}
