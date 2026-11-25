using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>One reminder as the reminder editor shows it: seconds before the start (negative after it) and how it notifies.</summary>
public sealed record CalendarReminderDraft(long OffsetSeconds, CalendarItemReminderType ReminderType);
