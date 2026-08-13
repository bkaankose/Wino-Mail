using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Calendar;

public readonly record struct CalendarDisplayRequest(CalendarDisplayType DisplayType, DateOnly AnchorDate);
