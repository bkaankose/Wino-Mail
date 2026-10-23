#nullable enable
using System;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Extensions;

namespace MailCal.Core.Domain.Models.Calendar;

/// <summary>Where a <see cref="CalendarCapabilities"/> value came from.</summary>
public enum CalendarCapabilitySource
{
    /// <summary>The account has no usable calendar.</summary>
    None = 0,
    Local = 1,
    Outlook = 2,
    Gmail = 3,
    CalDav = 4
}

/// <summary>
/// What a calendar (or every calendar of an account) can do, independent of the provider.
/// Shared ViewModels read this instead of checking the provider type. Outlook, Gmail and local
/// calendars have fixed sets; CalDAV values come from what the server advertised.
/// </summary>
public sealed record CalendarCapabilities
{
    public CalendarCapabilitySource Source { get; init; }

    /// <summary>
    /// True when the values were derived from what the server reported. False for the fixed
    /// provider sets and for the conservative CalDAV defaults used before the first discovery.
    /// </summary>
    public bool IsServerReported { get; init; }

    public bool IsReadOnly { get; init; }

    /// <summary>Files can be attached to an event and reach the server (RFC 8607 on CalDAV).</summary>
    public bool SupportsAttachments { get; init; }

    public bool SupportsAttendees { get; init; }

    /// <summary>The server delivers invitations and replies itself (CalDAV: implicit scheduling, RFC 6638).</summary>
    public bool SendsInvitations { get; init; }

    public bool SupportsOnlineMeeting { get; init; }
    public bool SupportsReminders { get; init; }

    /// <summary>The server stores default alarms for new events (CalDAV: calendar-default-alarms).</summary>
    public bool SupportsDefaultAlarms { get; init; }

    /// <summary>Most attendees one event may have; null when the server sets no limit.</summary>
    public int? MaxAttendees { get; init; }

    public long? QuotaAvailableBytes { get; init; }
    public long? QuotaUsedBytes { get; init; }

    /// <summary>Largest event resource the server accepts in bytes; null when unlimited or unknown.</summary>
    public long? MaxResourceSize { get; init; }

    public static CalendarCapabilities None { get; } = new();

    public static CalendarCapabilities Local { get; } = new()
    {
        Source = CalendarCapabilitySource.Local,
        SupportsAttachments = true,
        SupportsAttendees = true,
        SupportsReminders = true
    };

    public static CalendarCapabilities Outlook { get; } = new()
    {
        Source = CalendarCapabilitySource.Outlook,
        SupportsAttachments = true,
        SupportsAttendees = true,
        SendsInvitations = true,
        SupportsOnlineMeeting = true,
        SupportsReminders = true
    };

    public static CalendarCapabilities Gmail { get; } = new()
    {
        Source = CalendarCapabilitySource.Gmail,
        SupportsAttachments = true,
        SupportsAttendees = true,
        SendsInvitations = true,
        SupportsOnlineMeeting = true,
        SupportsReminders = true
    };

    /// <summary>What every CalDAV server can do, used until the server has been discovered.</summary>
    public static CalendarCapabilities CalDavDefault { get; } = new()
    {
        Source = CalendarCapabilitySource.CalDav,
        SupportsAttendees = true,
        SupportsReminders = true
    };

    /// <summary>
    /// Which capability source an account's calendars come from. Shared by the capability service
    /// and by the ViewModels' fallback before the service answers, so both agree.
    /// </summary>
    public static CalendarCapabilitySource ResolveSource(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return account.GetEffectiveCalendarIntegrationSource() switch
        {
            AccountIntegrationSource.Local => CalendarCapabilitySource.Local,
            AccountIntegrationSource.Provider when account.ProviderType == MailProviderType.Outlook => CalendarCapabilitySource.Outlook,
            AccountIntegrationSource.Provider when account.ProviderType == MailProviderType.Gmail => CalendarCapabilitySource.Gmail,
            AccountIntegrationSource.Dav when account.ProviderType == MailProviderType.IMAP4 &&
                                              account.ServerInformation?.CalendarSupportMode == ImapCalendarSupportMode.CalDav => CalendarCapabilitySource.CalDav,
            _ => CalendarCapabilitySource.None
        };
    }

    /// <summary>The fixed set of a source; for CalDAV the conservative defaults before discovery.</summary>
    public static CalendarCapabilities ForSource(CalendarCapabilitySource source)
        => source switch
        {
            CalendarCapabilitySource.Local => Local,
            CalendarCapabilitySource.Outlook => Outlook,
            CalendarCapabilitySource.Gmail => Gmail,
            CalendarCapabilitySource.CalDav => CalDavDefault,
            _ => None
        };
}
