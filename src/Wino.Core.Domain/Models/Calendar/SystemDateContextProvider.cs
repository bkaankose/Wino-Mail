using System;
using System.Globalization;

namespace Wino.Core.Domain.Models.Calendar;

public sealed class SystemDateContextProvider : IDateContextProvider
{
    public CultureInfo Culture => CultureInfo.CurrentCulture;

    public TimeZoneInfo TimeZone => TimeZoneInfo.Local;

    public DateOnly GetToday()
    {
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZone);
        return DateOnly.FromDateTime(localNow.DateTime);
    }
}
