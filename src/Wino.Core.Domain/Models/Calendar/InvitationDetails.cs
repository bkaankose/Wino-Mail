using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>One ATTENDEE line of an invitation.</summary>
public sealed class InvitationAttendee
{
    public string Name { get; set; }
    public string Email { get; set; }
    public bool IsOptional { get; set; }

    /// <summary>PARTSTAT: NEEDS-ACTION, ACCEPTED, TENTATIVE, DECLINED.</summary>
    public string ParticipationStatus { get; set; } = "NEEDS-ACTION";
}

/// <summary>
/// What a meeting message's text/calendar part says about the meeting: enough for the reading
/// pane's invitation card. Provider-neutral; every Exchange path and the Gmail/Outlook syncs
/// produce a text/calendar part in this shape.
/// </summary>
public sealed class InvitationDetails
{
    /// <summary>REQUEST, CANCEL or REPLY.</summary>
    public string Method { get; set; }
    public string Uid { get; set; }
    public string Summary { get; set; }
    public string Location { get; set; }
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public bool IsAllDay { get; set; }
    public string OrganizerName { get; set; }
    public string OrganizerEmail { get; set; }
    public List<InvitationAttendee> Attendees { get; } = new();

    public bool IsRequest => string.Equals(Method, "REQUEST", StringComparison.OrdinalIgnoreCase);
    public bool IsCancellation => string.Equals(Method, "CANCEL", StringComparison.OrdinalIgnoreCase);
    public bool IsReply => string.Equals(Method, "REPLY", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses the first VEVENT of an iCalendar text (RFC 5545 lines, folded or not). Never throws on
    /// odd input: unknown properties are ignored and unparsable dates stay null.
    /// </summary>
    public static InvitationDetails Parse(string ics)
    {
        var details = new InvitationDetails();
        if (string.IsNullOrWhiteSpace(ics))
            return details;

        var unfolded = ics.Replace("\r\n", "\n").Replace("\n ", string.Empty).Replace("\n\t", string.Empty);
        var lines = unfolded.Split('\n');

        var inEvent = false;
        var seenEvent = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (seenEvent) break;
                inEvent = true;
                seenEvent = true;
                continue;
            }

            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                inEvent = false;
                continue;
            }

            var colon = IndexOfValueColon(line);
            if (colon < 0)
                continue;

            var nameAndParams = line.Substring(0, colon);
            var value = line.Substring(colon + 1);
            var parameters = nameAndParams.Split(';');
            var name = parameters[0].ToUpperInvariant();
            var paramMap = parameters.Skip(1)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0].ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.First()[1].Trim('"'));

            if (!inEvent)
            {
                if (name == "METHOD") details.Method = value.Trim();
                continue;
            }

            switch (name)
            {
                case "UID": details.Uid = value.Trim(); break;
                case "SUMMARY": details.Summary = Unescape(value); break;
                case "LOCATION": details.Location = Unescape(value); break;
                case "DTSTART":
                    details.Start = ParseDate(value, paramMap, out var allDayStart);
                    details.IsAllDay |= allDayStart;
                    break;
                case "DTEND":
                    details.End = ParseDate(value, paramMap, out _);
                    break;
                case "ORGANIZER":
                    details.OrganizerEmail = StripMailto(value);
                    details.OrganizerName = paramMap.TryGetValue("CN", out var cn) ? cn : null;
                    break;
                case "ATTENDEE":
                    details.Attendees.Add(new InvitationAttendee
                    {
                        Email = StripMailto(value),
                        Name = paramMap.TryGetValue("CN", out var acn) ? acn : null,
                        IsOptional = paramMap.TryGetValue("ROLE", out var role) && role.Equals("OPT-PARTICIPANT", StringComparison.OrdinalIgnoreCase),
                        ParticipationStatus = paramMap.TryGetValue("PARTSTAT", out var partstat) ? partstat.ToUpperInvariant() : "NEEDS-ACTION"
                    });
                    break;
            }
        }

        return details;
    }

    /// <summary>The ':' that ends the name/parameter part: the first colon outside double quotes.</summary>
    private static int IndexOfValueColon(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) return i;
        }

        return -1;
    }

    private static string StripMailto(string value)
    {
        var v = value.Trim();
        return v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? v.Substring(7) : v;
    }

    private static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next switch { 'n' or 'N' => '\n', ',' => ',', ';' => ';', '\\' => '\\', _ => next });
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        return sb.ToString();
    }

    private static DateTimeOffset? ParseDate(string value, Dictionary<string, string> parameters, out bool allDay)
    {
        allDay = false;
        var v = value.Trim();

        if ((parameters.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase)) || v.Length == 8)
        {
            allDay = true;
            return DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.Zero)
                : null;
        }

        var utc = v.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        var text = utc ? v.Substring(0, v.Length - 1) : v;
        if (!DateTime.TryParseExact(text, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return null;

        if (utc)
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));

        if (parameters.TryGetValue("TZID", out var tzid))
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(tzid);
                return new DateTimeOffset(parsed, zone.GetUtcOffset(parsed));
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
    }
}
