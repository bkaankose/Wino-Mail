using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;

namespace Wino.Mail.ViewModels.Calendar.Messages;

public sealed record CalendarItemContextActionRequestedMessage(
    CalendarItemViewModel CalendarItemViewModel,
    CalendarContextMenuAction Action);
