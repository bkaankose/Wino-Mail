namespace Wino.Mail.Controls.Core;

public enum MailListDateBucket
{
    Today,
    Yesterday,
    LastWeek,
    LastMonth,
    LastThreeMonths,
    LastSixMonths,
    LastYear,
    LastTwoYears,
    LastFiveYears,
    LastTenYears,
    OlderThanTenYears,
}

/// <summary>
/// Fixed relative date ranges for <see cref="MailListGroupMode.DayGroups"/>. A date belongs to the
/// first range whose start is on or before it, so the ranges never overlap.
/// </summary>
public static class MailListDateBuckets
{
    public static MailListDateBucket For(DateTime localDate, DateTime today)
    {
        var day = localDate.Date;
        today = today.Date;

        if (day >= today) return MailListDateBucket.Today;
        if (day >= today.AddDays(-1)) return MailListDateBucket.Yesterday;
        if (day >= today.AddDays(-7)) return MailListDateBucket.LastWeek;
        if (day >= today.AddMonths(-1)) return MailListDateBucket.LastMonth;
        if (day >= today.AddMonths(-3)) return MailListDateBucket.LastThreeMonths;
        if (day >= today.AddMonths(-6)) return MailListDateBucket.LastSixMonths;
        if (day >= today.AddYears(-1)) return MailListDateBucket.LastYear;
        if (day >= today.AddYears(-2)) return MailListDateBucket.LastTwoYears;
        if (day >= today.AddYears(-5)) return MailListDateBucket.LastFiveYears;
        if (day >= today.AddYears(-10)) return MailListDateBucket.LastTenYears;
        return MailListDateBucket.OlderThanTenYears;
    }
}
