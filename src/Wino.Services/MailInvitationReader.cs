#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using MailKit;
using MimeKit;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using IcalCalendar = Ical.Net.Calendar;

namespace Wino.Services;

/// <summary>
/// Reads the iMIP part (RFC 6047) of a message: an inline text/calendar alternative or a calendar
/// attachment, in any transfer encoding and charset.
/// </summary>
public static class MailInvitationReader
{
    private static readonly ILogger Logger = Log.ForContext(typeof(MailInvitationReader));

    /// <summary>
    /// The item type of a message from its BODYSTRUCTURE, without downloading the message. A
    /// text/calendar part without a method parameter is an invitation only when it is not an
    /// attachment (RFC 6047 section 2.4 requires the parameter); an attached calendar file without
    /// one is plain mail.
    /// </summary>
    public static MailItemType GetItemType(BodyPart? body)
    {
        var parts = EnumerateCalendarBodyParts(body).ToList();
        var part = parts.FirstOrDefault(value => !string.IsNullOrWhiteSpace(GetMethodParameter(value.ContentType))) ?? parts.FirstOrDefault();
        return part == null ? MailItemType.Mail : GetItemType(MapPartMethod(GetMethodParameter(part.ContentType), part.IsAttachment));
    }

    /// <summary>The item type of a downloaded message, by the same rule as <see cref="GetItemType(BodyPart?)"/>.</summary>
    public static MailItemType GetItemType(MimeMessage? message)
    {
        if (message?.Body == null)
            return MailItemType.Mail;

        var parts = EnumerateParts(message.Body).Where(part => part.ContentType?.IsMimeType("text", "calendar") == true).ToList();
        var part = parts.FirstOrDefault(value => !string.IsNullOrWhiteSpace(GetMethodParameter(value))) ?? parts.FirstOrDefault();
        return part == null ? MailItemType.Mail : GetItemType(MapPartMethod(GetMethodParameter(part), part.IsAttachment));
    }

    public static MailItemType GetItemType(MailInvitationMethod method)
        => method switch
        {
            MailInvitationMethod.Request => MailItemType.CalendarInvitation,
            MailInvitationMethod.Cancel => MailItemType.CalendarCancellation,
            MailInvitationMethod.Reply => MailItemType.CalendarResponse,
            _ => MailItemType.Mail
        };

    /// <summary>The text/calendar parts of a BODYSTRUCTURE. Parts of attached messages are not counted.</summary>
    private static IEnumerable<BodyPartBasic> EnumerateCalendarBodyParts(BodyPart? part)
    {
        switch (part)
        {
            case BodyPartMultipart multipart:
                foreach (var child in multipart.BodyParts)
                {
                    foreach (var calendarPart in EnumerateCalendarBodyParts(child))
                        yield return calendarPart;
                }

                break;
            case BodyPartMessage:
                break;
            case BodyPartBasic basic when basic.ContentType?.IsMimeType("text", "calendar") == true:
                yield return basic;
                break;
        }
    }

    private static string? GetMethodParameter(ContentType? contentType)
        => contentType?.Parameters.TryGetValue("method", out string? value) == true ? value?.Trim() : null;

    /// <summary>
    /// The part that carries the invitation: a text/calendar part (inline or attached) or an .ics
    /// attachment, preferring one with a method parameter. Parts of attached messages are not used.
    /// </summary>
    public static MimePart? FindCalendarPart(MimeMessage? message)
    {
        if (message?.Body == null)
            return null;

        var candidates = EnumerateParts(message.Body).Where(IsCalendarPart).ToList();
        return candidates.FirstOrDefault(part => !string.IsNullOrWhiteSpace(GetMethodParameter(part))) ?? candidates.FirstOrDefault();
    }

    public static string? GetMethodParameter(MimePart part)
        => GetMethodParameter(part.ContentType);

    public static string ReadText(MimePart part)
    {
        ArgumentNullException.ThrowIfNull(part);

        if (part is TextPart textPart)
            return textPart.Text ?? string.Empty;

        if (part.Content == null)
            return string.Empty;

        using var stream = new MemoryStream();
        part.Content.DecodeTo(stream);
        return GetEncoding(part.ContentType?.Charset).GetString(stream.ToArray());
    }

    public static MailInvitation? Parse(MimeMessage? message)
    {
        var part = FindCalendarPart(message);
        if (part == null)
            return null;

        string text;
        try
        {
            text = ReadText(part);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not decode the calendar part of a message");
            return null;
        }

        var isInlineCalendarPart = part.ContentType?.IsMimeType("text", "calendar") == true && !part.IsAttachment;
        return Parse(text, GetMethodParameter(part), isInlineCalendarPart);
    }

    /// <param name="methodParameter">The method parameter of the MIME part, used when the iCalendar object has no METHOD.</param>
    /// <param name="defaultsToRequest">
    /// Without any METHOD, an inline text/calendar part is a request, as the list types it. An attached
    /// calendar file needs a METHOD in its object or its MIME part to be an invitation.
    /// </param>
    public static MailInvitation? Parse(string icsContent, string? methodParameter, bool defaultsToRequest)
    {
        if (string.IsNullOrWhiteSpace(icsContent) || icsContent.IndexOf("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase) < 0)
            return null;

        IcalCalendar? calendar;
        try
        {
            calendar = IcalCalendar.Load(icsContent);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not read the iCalendar object of an invitation");
            return null;
        }

        if (calendar == null)
            return null;

        var calendarEvent = SelectEvent(calendar);
        if (calendarEvent == null || string.IsNullOrWhiteSpace(calendarEvent.Uid))
            return null;

        var method = string.IsNullOrWhiteSpace(calendar.Method)
            ? MapPartMethod(methodParameter, isAttachment: !defaultsToRequest)
            : MapMethod(calendar.Method);
        var isAllDay = calendarEvent.Start != null && !calendarEvent.Start.HasTime;
        var start = ToDateTimeOffset(calendarEvent.Start);
        var end = calendarEvent.End != null
            ? ToDateTimeOffset(calendarEvent.End)
            : start.HasValue && calendarEvent.Duration is { } duration
                ? start.Value.Add(duration.ToTimeSpanUnspecified())
                : isAllDay && start.HasValue ? start.Value.AddDays(1) : start;

        return new MailInvitation
        {
            Method = method,
            Uid = calendarEvent.Uid,
            Sequence = calendarEvent.Sequence,
            RecurrenceId = calendarEvent.RecurrenceIdentifier?.StartTime is { } recurrenceId ? CalDavOccurrenceKey.Create(recurrenceId) : string.Empty,
            Summary = calendarEvent.Summary ?? string.Empty,
            Location = calendarEvent.Location ?? string.Empty,
            Description = calendarEvent.Description?.Trim() ?? string.Empty,
            Start = start,
            End = end,
            IsAllDay = isAllDay,
            IsRecurring = calendarEvent.RecurrenceRule != null || (calendarEvent.RecurrenceDates?.GetAllPeriods().Any() ?? false),
            OrganizerName = calendarEvent.Organizer?.CommonName?.Trim() ?? string.Empty,
            OrganizerEmail = CalDavUserIdentity.ExtractEmail(calendarEvent.Organizer?.Value?.OriginalString),
            Attendees = calendarEvent.Attendees
                .Select(attendee => attendee?.Value is { } address
                    ? new MailInvitationAttendee
                    {
                        Name = attendee.CommonName?.Trim() ?? string.Empty,
                        Email = CalDavUserIdentity.ExtractEmail(address.OriginalString),
                        Status = MapParticipationStatus(attendee.ParticipationStatus),
                        IsOptional = string.Equals(attendee.Role, "OPT-PARTICIPANT", StringComparison.OrdinalIgnoreCase)
                    }
                    : null)
                .OfType<MailInvitationAttendee>()
                .Where(attendee => attendee.Email.Length > 0)
                .ToList(),
            RawIcs = icsContent
        };
    }

    /// <summary>True when the event the invitation describes has STATUS:CANCELLED.</summary>
    public static bool IsEventCancelled(string icsContent)
    {
        try
        {
            return string.Equals(SelectEvent(IcalCalendar.Load(icsContent))?.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The stored component of an invitation's event: <paramref name="recurrenceId"/> empty for the
    /// series or single event, else that occurrence's override. Null when the object has no event
    /// with this UID or cannot be read.
    /// </summary>
    public static StoredInvitationEvent? FindStoredEvent(string icsContent, string uid, string recurrenceId, CalDavUserIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        try
        {
            var events = IcalCalendar.Load(icsContent)?.Events
                .Where(value => value != null && string.Equals(value.Uid, uid, StringComparison.Ordinal))
                .ToList() ?? [];

            if (events.Count == 0)
                return null;

            var master = events.FirstOrDefault(value => value.RecurrenceIdentifier == null);
            var match = string.IsNullOrEmpty(recurrenceId)
                ? master
                : events.FirstOrDefault(value => value.RecurrenceIdentifier?.StartTime is { } id && CalDavOccurrenceKey.Create(id) == recurrenceId);

            // An override has the series' ORGANIZER; a missing override is described by the master.
            var source = match ?? master;
            var own = source?.Attendees.FirstOrDefault(attendee => attendee?.Value is { } address && identity.IsUser(address.OriginalString));

            return new StoredInvitationEvent(
                match?.Sequence,
                master?.Sequence,
                CalDavUserIdentity.ExtractEmail(source?.Organizer?.Value?.OriginalString),
                own == null ? null : MapParticipationStatus(own.ParticipationStatus));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not read the stored copy of invitation {Uid}", uid);
            return null;
        }
    }

    /// <summary>Calendar user addresses compared as mail addresses, ignoring case and the <c>mailto:</c> prefix.</summary>
    public static bool IsSameAddress(string? first, string? second)
    {
        var left = CalDavUserIdentity.ExtractEmail(first);
        return left.Length > 0 && string.Equals(left, CalDavUserIdentity.ExtractEmail(second), StringComparison.OrdinalIgnoreCase);
    }

    public static AttendeeStatus MapParticipationStatus(string? participationStatus)
        => participationStatus?.Trim().ToUpperInvariant() switch
        {
            "ACCEPTED" => AttendeeStatus.Accepted,
            "DECLINED" => AttendeeStatus.Declined,
            "TENTATIVE" => AttendeeStatus.Tentative,
            _ => AttendeeStatus.NeedsAction
        };

    /// <summary>The series master when the object has one, otherwise its first event.</summary>
    private static CalendarEvent? SelectEvent(IcalCalendar? calendar)
    {
        var events = calendar?.Events.Where(value => value != null).ToList() ?? [];
        return events.FirstOrDefault(value => value.RecurrenceIdentifier == null) ?? events.FirstOrDefault();
    }

    /// <summary>The method of a calendar part from its parameter; without one only an inline part is a request.</summary>
    private static MailInvitationMethod MapPartMethod(string? methodParameter, bool isAttachment)
    {
        if (!string.IsNullOrWhiteSpace(methodParameter))
            return MapMethod(methodParameter);

        return isAttachment ? MailInvitationMethod.Other : MailInvitationMethod.Request;
    }

    private static MailInvitationMethod MapMethod(string method)
    {
        return method.Trim().ToUpperInvariant() switch
        {
            "REQUEST" => MailInvitationMethod.Request,
            "CANCEL" => MailInvitationMethod.Cancel,
            "REPLY" => MailInvitationMethod.Reply,
            _ => MailInvitationMethod.Other
        };
    }

    /// <summary>
    /// DATE values keep their calendar date at offset zero. Floating times are the user's local time.
    /// A TZID that iCalendar cannot resolve is looked up as a system zone, else read as local time.
    /// </summary>
    private static DateTimeOffset? ToDateTimeOffset(CalDateTime? value)
    {
        if (value == null)
            return null;

        if (!value.HasTime)
            return new DateTimeOffset(DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Unspecified), TimeSpan.Zero);

        var wallClock = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        if (value.IsFloating)
            return new DateTimeOffset(wallClock, TimeZoneInfo.Local.GetUtcOffset(wallClock));

        try
        {
            return new DateTimeOffset(DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc));
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(value.TzId) && TimeZoneInfo.TryFindSystemTimeZoneById(value.TzId, out var zone))
                return new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock));

            Logger.Warning(ex, "Unknown time zone {TzId} in an invitation; reading it as local time", value.TzId);
            return new DateTimeOffset(wallClock, TimeZoneInfo.Local.GetUtcOffset(wallClock));
        }
    }

    private static IEnumerable<MimePart> EnumerateParts(MimeEntity entity)
    {
        switch (entity)
        {
            case Multipart multipart:
                foreach (var child in multipart)
                {
                    foreach (var part in EnumerateParts(child))
                        yield return part;
                }

                break;
            case MimePart part:
                yield return part;
                break;
        }
    }

    private static bool IsCalendarPart(MimePart part)
        => part.ContentType?.IsMimeType("text", "calendar") == true ||
           part.ContentType?.IsMimeType("application", "ics") == true ||
           part.FileName?.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) == true;

    private static Encoding GetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
            return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(charset.Trim().Trim('"'));
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }
}

/// <summary>
/// What the calendar stores for an invitation's event. <see cref="Sequence"/> is null when the
/// requested override is missing; <see cref="OwnStatus"/> is the user's PARTSTAT there, or in the
/// master when the override is missing.
/// </summary>
public sealed record StoredInvitationEvent(int? Sequence, int? SeriesSequence, string OrganizerEmail, AttendeeStatus? OwnStatus);
