using System;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

/// <summary>
/// A contact's birthday as the Contact Object Protocol stores it (MS-OXOCNTC 2.2.1.5): PidTagBirthday is the day at
/// 11:59 UTC, and PidLidBirthdayLocal is the day at 0:00 in the writer's zone, saved without any time zone conversion.
/// EWS and MAPI read and write it the same way, so both transports store the same day.
/// </summary>
public static class ExchangeContactBirthday
{
    /// <summary>The year Outlook stores for a birthday without a year; written when the local contact has none.</summary>
    public const int YearlessPlaceholder = 1604;

    // PtypTime is a FILETIME, which starts in 1601; an earlier value is not a stored birthday.
    private static readonly DateTime FirstFileTime = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The birthday day from the two properties: PidLidBirthdayLocal's raw date when it is set (it carries the day
    /// itself), otherwise the UTC calendar date of PidTagBirthday. Null when neither holds a day.
    /// </summary>
    public static DateTime? Read(DateTime? birthdayLocalRaw, DateTime? birthdayUtc)
    {
        if (birthdayLocalRaw is { } local && ToUtc(local) >= FirstFileTime)
            return ToUtc(local).Date;

        if (birthdayUtc is { } utc && ToUtc(utc) >= FirstFileTime)
            return ToUtc(utc).Date;

        return null;
    }

    /// <summary>Stores the day on the contact; the yearless placeholder this client writes reads back as no year.</summary>
    public static void Apply(AccountContact contact, DateTime? day)
    {
        contact.BirthdayYear = day?.Year is { } year && year != YearlessPlaceholder ? year : null;
        contact.BirthdayMonth = day?.Month;
        contact.BirthdayDay = day?.Day;
    }

    /// <summary>The local contact's birthday day, or null when it has none or the parts do not form a date.</summary>
    public static DateTime? FromContact(AccountContact contact)
    {
        if (contact.BirthdayMonth is not { } month || contact.BirthdayDay is not { } day || month is < 1 or > 12)
            return null;

        var year = contact.BirthdayYear ?? YearlessPlaceholder;
        if (year is < 1601 or > 9999 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return null;

        return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>PidTagBirthday: the day at 11:59 UTC (MS-OXOCNTC 2.2.1.5.1).</summary>
    public static DateTime ToBirthdayUtc(DateTime day)
        => new(day.Year, day.Month, day.Day, 11, 59, 0, DateTimeKind.Utc);

    /// <summary>PidLidBirthdayLocal: the day at 0:00, saved without time zone conversion (MS-OXOCNTC 2.2.1.5.2).</summary>
    public static DateTime ToBirthdayLocalRaw(DateTime day)
        => new(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Utc);

    // Both readers return the stored value in UTC (MAPI decodes the FILETIME, EWS runs in a UTC service zone); a value
    // without a zone is that UTC value.
    private static DateTime ToUtc(DateTime value)
        => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
