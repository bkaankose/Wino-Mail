using MailCal.Core.Domain.Enums;

namespace MailCal.Calendar.ViewModels.Data;

public sealed record CalendarEventDisplayModeOption(CalendarEventDisplayMode Mode, string DisplayName, string Description);
