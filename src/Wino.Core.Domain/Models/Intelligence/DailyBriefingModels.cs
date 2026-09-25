#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Models.Intelligence;

public sealed record LocalIntelligenceAccessSnapshot(
    Guid LocalAccountId,
    Guid WinoAccountId,
    bool HasAiPack,
    bool HasIntelligenceConsent,
    Guid? MailboxId,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsEligible => HasAiPack && HasIntelligenceConsent && MailboxId is not null;
}

public sealed record DailyBriefingAccount(MailAccount Account, Guid? MailboxId = null);

/// <summary>
/// Which indicators the user wants to see for one account. Filtering happens at display
/// time so a settings change never rewrites a stored artifact.
/// </summary>
public sealed record DailyBriefingIndicatorState(
    bool IsPriorityVisible,
    bool IsHeadlineVisible,
    IReadOnlySet<string> EnabledLabels)
{
    public static DailyBriefingIndicatorState AllVisible(IReadOnlySet<string>? enabledLabels = null)
        => new(true, true, enabledLabels ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// One briefing entry: a message Classification included, described only in terms Classification and Enrichment can
/// actually support.
/// </summary>
public sealed record DailyBriefingFact(
    Guid LocalAccountId,
    Guid MailUniqueId,
    string RemoteMessageId,
    string ContentHash,
    string Subject,
    string SenderName,
    string SenderAddress,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<string> Labels,
    string Priority,
    IReadOnlyList<MailSmartAction> Actions,
    string Headline,
    string Summary,
    DateTime FirstImportedUtc,
    DailyBriefingIndicatorState? IndicatorState = null,
    bool IsIgnored = false)
{
    public IReadOnlyList<string> VisibleLabels
    {
        get
        {
            if (IndicatorState is null || IndicatorState.EnabledLabels.Count == 0)
            {
                return Labels;
            }

            var visible = new List<string>(Labels.Count);
            foreach (var label in Labels)
            {
                if (IndicatorState.EnabledLabels.Contains(label))
                {
                    visible.Add(label);
                }
            }

            return visible;
        }
    }

    public bool IsPriorityVisible => IndicatorState?.IsPriorityVisible ?? true;

    public bool IsHeadlineVisible => IndicatorState?.IsHeadlineVisible ?? true;

    /// <summary>
    /// New since the briefing was last viewed. Keyed on when the artifact first arrived
    /// locally, which replaces the artifact revision the old change feed carried.
    /// </summary>
    public bool IsNewSince(DateTime? lastViewedUtc)
        => lastViewedUtc is null || FirstImportedUtc > lastViewedUtc.Value;

    /// <summary>
    /// Whether the card belongs on the given local day. A card always shows on the day its message
    /// arrived, and also on every day one of its dated smart actions covers: an event or a booking
    /// shows on each day from its start to its end, a due or delivery date on that day.
    /// </summary>
    public bool AppearsOn(DateOnly day, TimeZoneInfo timeZone)
    {
        if (ToLocalDay(ReceivedAt, timeZone) == day)
        {
            return true;
        }

        foreach (var action in Actions)
        {
            var covers = action switch
            {
                CalendarEventAction calendarEvent => Covers(day, ToLocalDay(calendarEvent.Start, timeZone),
                    calendarEvent.End is { } end ? EndDay(end, calendarEvent.AllDay, timeZone) : null),
                BookingAction { Start: { } start } booking => Covers(day, ToLocalDay(start, timeZone),
                    booking.End is { } end ? ToLocalDay(end, timeZone) : null),
                BookingAction { End: { } end } => ToLocalDay(end, timeZone) == day,
                PaymentDueAction { DueDate: { } dueDate } => dueDate == day,
                TaskAction { DueDate: { } dueDate } => dueDate == day,
                ShipmentAction { ExpectedDelivery: { } expectedDelivery } => expectedDelivery == day,
                _ => false,
            };

            if (covers)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Covers(DateOnly day, DateOnly start, DateOnly? end)
        => end is { } last && last > start ? day >= start && day <= last : day == start;

    /// <summary>
    /// An all-day event's end is exclusive midnight, so an event ending at 00:00 on the next day
    /// does not cover that day.
    /// </summary>
    private static DateOnly EndDay(DateTime end, bool allDay, TimeZoneInfo timeZone)
    {
        var day = ToLocalDay(end, timeZone);
        return allDay && end.TimeOfDay == TimeSpan.Zero ? day.AddDays(-1) : day;
    }

    private static DateOnly ToLocalDay(DateTimeOffset value, TimeZoneInfo timeZone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, timeZone).DateTime);

    /// <summary>A UTC action time is shown in the user's zone; any other time is already local to the event.</summary>
    private static DateOnly ToLocalDay(DateTime value, TimeZoneInfo timeZone)
        => value.Kind == DateTimeKind.Utc
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(value, timeZone))
            : DateOnly.FromDateTime(value);
}

/// <summary>Briefing entries for one local day, newest first.</summary>
public sealed record DailyBriefingFactsResult(
    DateOnly Day,
    IReadOnlyList<DailyBriefingFact> Facts,
    int IgnoredCount)
{
    public static DailyBriefingFactsResult Empty(DateOnly day) => new(day, [], 0);
}

public sealed record DailyBriefingUnseenState(bool HasUnseenContent, DateTime? LastViewedUtc);
