using System;
using System.Globalization;

namespace Wino.Core.Domain.Models.Calendar;

public interface IDateContextProvider
{
    CultureInfo Culture { get; }
    TimeZoneInfo TimeZone { get; }
    DateOnly GetToday();
}
