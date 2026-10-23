#nullable enable
using System;

namespace MailCal.Mail.ViewModels.Companion;

/// <summary>
/// What the companion's quick-add form holds: created directly, or handed to the full event editor.
/// </summary>
/// <param name="CalendarId">Calendar the event goes to, or null to let the editor choose.</param>
/// <param name="Title">Event title; may be empty when handed to the editor.</param>
/// <param name="Start">Local start. For an all-day event, the day.</param>
/// <param name="End">Local end. For an all-day event, the day after.</param>
/// <param name="IsAllDay">Whether the event covers whole days.</param>
public sealed record CompanionEventDraft(
    Guid? CalendarId,
    string Title,
    DateTime Start,
    DateTime End,
    bool IsAllDay);
