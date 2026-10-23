#nullable enable

using MailCal.Core.Domain.Entities.Calendar;

namespace MailCal.Core.Domain.Models.Calendar;

public sealed record AccountCalendarPickingResult(AccountCalendar? PickedCalendar, bool ShouldNavigateToCalendarSettings);
