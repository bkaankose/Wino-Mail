using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Calendar;

public readonly record struct CalendarDisplayRequest(CalendarDisplayType DisplayType, DateOnly AnchorDate);
