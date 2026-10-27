#nullable enable

using System;
using System.Collections.Generic;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;

namespace Wino.Mail.ViewModels.Companion;

/// <summary>
/// One event as a single companion row. A snapshot taken when the companion content loads,
/// so the countdown reads relative to that moment like the rest of the surface.
/// </summary>
public sealed class CompanionEventItem
{
    private const int ImminentMinutes = 60;

    internal CompanionEventItem(CalendarItemViewModel item, DateTime now, bool isOnline)
    {
        Item = item;
        IsOnline = isOnline;
        TimeText = string.Format(Translator.Companion_EventTimeFormat, item.StartDate.ToString("t"), item.EndDate.ToString("t"));
        CalendarName = item.AssignedCalendar?.Name ?? string.Empty;
        CalendarColorHex = Wino.Calendar.ViewModels.CalendarAgenda.ColorHex(item) ?? string.Empty;
        AttendeeCount = item.Attendees.Count;

        var minutes = (int)Math.Round((item.StartDate - now).TotalMinutes);
        IsImminent = minutes < ImminentMinutes;
        LeadText = minutes <= 0
            ? Translator.Companion_EventNow
            : IsImminent ? string.Format(Translator.Companion_EventInMinutesFormat, minutes) : item.StartDate.ToString("t");

        var details = new List<string> { Title, TimeText };
        if (HasLocation)
            details.Add(Location);
        if (!string.IsNullOrEmpty(CalendarName))
            details.Add(CalendarName);
        if (HasAttendees)
            details.Add(string.Format(Translator.Companion_EventAttendeesFormat, AttendeeCount));
        if (IsOnline)
            details.Add(Translator.Companion_EventOnline);

        DetailsText = string.Join(Environment.NewLine, details);
        AccessibleText = string.Join(", ", details);
    }

    public CalendarItemViewModel Item { get; }

    public string Title => Item.Title ?? string.Empty;

    public string Location => Item.Location?.Trim() ?? string.Empty;

    public bool HasLocation => !string.IsNullOrEmpty(Location);

    /// <summary>The location as it follows the title on the row's single line.</summary>
    public string LocationSuffix => HasLocation ? " · " + Location : string.Empty;

    public string TimeText { get; }

    /// <summary>"Now" or "in N min" within the next hour, otherwise the start time.</summary>
    public string LeadText { get; }

    public bool IsImminent { get; }

    public string CalendarName { get; }

    public string CalendarColorHex { get; }

    public bool IsOnline { get; }

    public int AttendeeCount { get; }

    public bool HasAttendees => AttendeeCount > 0;

    public string PrimaryActionText => IsOnline ? Translator.Companion_Join : Translator.Companion_View;

    /// <summary>Everything the row carries, one item per line, for the tooltip.</summary>
    public string DetailsText { get; }

    public string AccessibleText { get; }
}
