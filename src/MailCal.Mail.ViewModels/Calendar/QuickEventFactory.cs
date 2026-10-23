#nullable enable
using System;
using System.Threading.Tasks;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Validation;

namespace MailCal.Calendar.ViewModels;

/// <summary>
/// Builds and dispatches a quick event: a title, a time range (or whole days) and a calendar, with no
/// attendees, reminders or recurrence. Shared by the calendar page and the tray companion.
/// </summary>
public static class QuickEventFactory
{
    private static readonly CalendarEventComposeResultValidator Validator = new();

    /// <param name="start">Local start. For an all-day event, the first day.</param>
    /// <param name="end">Local end. For an all-day event, the day after the last day.</param>
    public static CalendarEventComposeResult Create(
        IAccountCalendar calendar,
        string title,
        string? location,
        DateTime start,
        DateTime end,
        bool isAllDay)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        return new CalendarEventComposeResult
        {
            CalendarId = calendar.Id,
            AccountId = calendar.AccountId,
            Title = title,
            Location = location ?? string.Empty,
            HtmlNotes = string.Empty,
            StartDate = isAllDay ? start.Date : start,
            EndDate = isAllDay ? end.Date : end,
            IsAllDay = isAllDay,
            TimeZoneId = TimeZoneInfo.Local.Id,
            ShowAs = calendar.DefaultShowAs,
            SelectedReminders = [],
            Attendees = [],
            Attachments = [],
            Recurrence = string.Empty,
            RecurrenceSummary = string.Empty
        };
    }

    /// <summary>
    /// Validates the event and queues its creation. Throws <see cref="MailCal.Core.Domain.Exceptions.CalendarEventComposeValidationException"/>
    /// with a user-facing message when the event is incomplete.
    /// </summary>
    public static Task CreateAsync(IMailCalRequestDelegator delegator, CalendarEventComposeResult composeResult)
    {
        ArgumentNullException.ThrowIfNull(delegator);

        Validator.Validate(composeResult);
        return delegator.ExecuteAsync(new CalendarOperationPreparationRequest(
            CalendarSynchronizerOperation.CreateEvent,
            ComposeResult: composeResult));
    }
}
