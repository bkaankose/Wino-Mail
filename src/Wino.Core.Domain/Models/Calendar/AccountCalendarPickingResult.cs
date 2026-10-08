#nullable enable

using Wino.Core.Domain.Entities.Calendar;

namespace Wino.Core.Domain.Models.Calendar;

public sealed record AccountCalendarPickingResult(AccountCalendar? PickedCalendar, bool ShouldNavigateToCalendarSettings);
