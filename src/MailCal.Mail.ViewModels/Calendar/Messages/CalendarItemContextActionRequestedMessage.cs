using MailCal.Calendar.ViewModels.Data;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Calendar.ViewModels.Messages;

public sealed record CalendarItemContextActionRequestedMessage(
    CalendarItemViewModel CalendarItemViewModel,
    CalendarContextMenuAction Action);
