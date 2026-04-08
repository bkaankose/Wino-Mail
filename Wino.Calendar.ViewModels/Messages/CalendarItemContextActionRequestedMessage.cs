using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.ViewModels.Messages;

public sealed record CalendarItemContextActionRequestedMessage(
    CalendarItemViewModel CalendarItemViewModel,
    CalendarContextMenuAction Action);
