using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.Calendar.Data;

public sealed record CalendarEventDisplayModeOption(CalendarEventDisplayMode Mode, string DisplayName, string Description);
