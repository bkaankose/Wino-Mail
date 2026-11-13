#nullable enable
using System;
using System.Globalization;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.ViewModels.Helpers;

/// <summary>
/// The date and time range of an event mail, as the mail list calendar row and the reader's invitation card show it.
/// </summary>
public static class EventDateRangeFormatter
{
    public static CultureInfo DisplayCulture => CultureInfo.DefaultThreadCurrentUICulture ?? CultureInfo.CurrentUICulture;

    /// <summary>The invitation's range in local time; empty when it has no start.</summary>
    public static string Format(MailInvitation invitation, TimeFormatPreference timeFormatPreference, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        if (invitation.Start is not DateTimeOffset start)
            return string.Empty;

        var end = invitation.End ?? start;

        // All-day dates are calendar dates at offset zero; converting them to local time could move them a day.
        var startLocal = invitation.IsAllDay ? start.DateTime : start.LocalDateTime;
        var endLocal = invitation.IsAllDay ? end.DateTime : end.LocalDateTime;

        return Format(startLocal, endLocal, invitation.IsAllDay, timeFormatPreference, culture);
    }

    /// <param name="endLocal">For all-day events the exclusive end date, as iCalendar stores it.</param>
    public static string Format(DateTime startLocal, DateTime endLocal, bool isAllDay, TimeFormatPreference timeFormatPreference, CultureInfo culture)
    {
        if (endLocal < startLocal)
        {
            endLocal = startLocal;
        }

        if (isAllDay)
        {
            var adjustedEnd = endLocal.Date > startLocal.Date ? endLocal.Date.AddDays(-1) : startLocal.Date;

            if (adjustedEnd.Date > startLocal.Date)
            {
                return $"{startLocal.ToString("d", culture)} - {adjustedEnd.ToString("d", culture)} ({Translator.CalendarItemAllDay})";
            }

            return $"{startLocal.ToString("d", culture)} ({Translator.CalendarItemAllDay})";
        }

        var displayType = DateTimeDisplayFormatter.GetTimeDisplayType(timeFormatPreference, culture);
        var timeFormat = DateTimeDisplayFormatter.GetTimeFormat(displayType);

        if (startLocal.Date == endLocal.Date)
        {
            return $"{startLocal.ToString($"ddd, MMM d {timeFormat}", culture)} - {endLocal.ToString(timeFormat, culture)}";
        }

        return $"{startLocal.ToString($"ddd, MMM d {timeFormat}", culture)} - {endLocal.ToString($"ddd, MMM d {timeFormat}", culture)}";
    }
}
