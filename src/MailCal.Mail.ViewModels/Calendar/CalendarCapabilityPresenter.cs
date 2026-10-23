#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Calendar.ViewModels;

/// <summary>
/// Turns <see cref="CalendarCapabilities"/> into the values and translated texts the calendar
/// pages bind to, so every page words a capability the same way.
/// </summary>
public static class CalendarCapabilityPresenter
{
    /// <summary>Below this much free server storage the compose page warns before saving.</summary>
    public const long LowQuotaThresholdBytes = 5L * 1024 * 1024;

    /// <summary>
    /// What a calendar can do before the capability service answered: the fixed provider sets,
    /// and the conservative CalDAV defaults. Matches <c>CalendarCapabilityService</c>.
    /// </summary>
    public static CalendarCapabilities GetFallback(MailAccount? account, AccountCalendar? calendar)
    {
        if (account == null)
            return CalendarCapabilities.None;

        var capabilities = CalendarCapabilities.ForSource(CalendarCapabilities.ResolveSource(account));

        return calendar?.IsReadOnly == true && !capabilities.IsReadOnly
            ? capabilities with { IsReadOnly = true }
            : capabilities;
    }

    public static bool HasLowQuota(CalendarCapabilities capabilities)
        => capabilities.QuotaAvailableBytes is long available && available < LowQuotaThresholdBytes;

    public static string GetAttendeeLimitText(CalendarCapabilities capabilities)
        => capabilities.SupportsAttendees && capabilities.MaxAttendees is int max && max > 0
            ? string.Format(CultureInfo.CurrentCulture, Translator.CalendarCapability_AttendeeLimit, max)
            : string.Empty;

    public static string GetQuotaWarningText(CalendarCapabilities capabilities)
        => HasLowQuota(capabilities) && capabilities.QuotaAvailableBytes is long available
            ? string.Format(CultureInfo.CurrentCulture, Translator.CalendarCapability_QuotaLow, FormatStorageSize(available))
            : string.Empty;

    /// <summary>One paragraph describing what the calendars of an account can do.</summary>
    public static string BuildSummary(CalendarCapabilities capabilities)
    {
        if (capabilities.Source == CalendarCapabilitySource.None)
            return string.Empty;

        var parts = new List<string>();

        if (capabilities.IsReadOnly)
            parts.Add(Translator.CalendarCapability_SummaryReadOnly);

        parts.Add(capabilities.SupportsAttachments
            ? Translator.CalendarCapability_SummaryAttachmentsSupported
            : Translator.CalendarCapability_SummaryAttachmentsNotSupported);

        if (!capabilities.SupportsAttendees)
        {
            parts.Add(Translator.CalendarCapability_SummaryAttendeesNotSupported);
        }
        else
        {
            parts.Add(capabilities.SendsInvitations
                ? Translator.CalendarCapability_SummaryInvitationsSent
                : Translator.CalendarCapability_SummaryInvitationsNotSent);

            if (capabilities.MaxAttendees is int max && max > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.CalendarCapability_SummaryAttendeeLimit, max));
        }

        if (capabilities.QuotaAvailableBytes is long available && available >= 0)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.CalendarCapability_SummaryQuota, FormatStorageSize(available)));

        // CalDAV before the first discovery only has the conservative defaults.
        if (capabilities.Source == CalendarCapabilitySource.CalDav && !capabilities.IsServerReported)
            parts.Add(Translator.CalendarCapability_SummaryPendingDiscovery);

        return string.Join(" ", parts);
    }

    public static string FormatStorageSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var display = (double)Math.Max(0, bytes);
        var unit = 0;

        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", display, units[unit]);
    }
}
