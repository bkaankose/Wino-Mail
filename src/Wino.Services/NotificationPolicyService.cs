using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;

namespace Wino.Services;

/// <summary>
/// Owns snooze and quiet hours evaluation. Expiry is lazy rather than timer-driven: whoever reads
/// the state next clears an elapsed snooze, so correctness never depends on a timer having fired.
/// </summary>
public class NotificationPolicyService(
    IPreferencesService preferencesService,
    IUserPresenceStateProvider userPresenceStateProvider) : INotificationPolicyService
{
    private readonly IPreferencesService _preferencesService = preferencesService;
    private readonly IUserPresenceStateProvider _userPresenceStateProvider = userPresenceStateProvider;

    public NotificationSnoozeState GetSnoozeState(DateTimeOffset now)
    {
        var preset = _preferencesService.NotificationSnoozePreset;

        if (preset == NotificationSnoozePreset.None)
            return NotificationSnoozeState.Delivering;

        var untilTicks = _preferencesService.NotificationSnoozeUntilUtcTicks;

        if (untilTicks == 0)
            return new NotificationSnoozeState(true, preset, null);

        var until = new DateTimeOffset(untilTicks, TimeSpan.Zero);

        if (until > now)
            return new NotificationSnoozeState(true, preset, until.ToLocalTime());

        // Elapsed. Heal the stored state so every later reader agrees, and so the change notification
        // reaches the settings page and the companion flyout.
        EndSnooze();

        return NotificationSnoozeState.Delivering;
    }

    public void StartSnooze(NotificationSnoozePreset preset, DateTimeOffset? customUntilLocal, DateTimeOffset now)
    {
        if (preset == NotificationSnoozePreset.None)
        {
            EndSnooze();
            return;
        }

        var until = ResolveSnoozeEnd(preset, customUntilLocal, now);

        _preferencesService.NotificationSnoozeUntilUtcTicks = until?.UtcTicks ?? 0;
        _preferencesService.NotificationSnoozePreset = preset;
        _preferencesService.LastUsedSnoozePreset = preset;
    }

    public void EndSnooze()
    {
        _preferencesService.NotificationSnoozeUntilUtcTicks = 0;
        _preferencesService.NotificationSnoozePreset = NotificationSnoozePreset.None;
    }

    public bool IsQuietNow(DateTimeOffset nowLocal)
    {
        if (!_preferencesService.AreQuietHoursEnabled)
            return false;

        var start = _preferencesService.QuietHoursStart;
        var end = _preferencesService.QuietHoursEnd;

        // An empty window means never, not always.
        if (start == end)
            return false;

        var days = _preferencesService.QuietHoursDays;
        var timeOfDay = nowLocal.TimeOfDay;

        if (start < end)
            return timeOfDay >= start && timeOfDay < end && IsDaySelected(days, nowLocal.DayOfWeek);

        // The window spans midnight, so it belongs to the day it started on. A Friday 18:30 window
        // is still a Friday window at 02:00 on Saturday, even when Saturday is not selected.
        if (timeOfDay >= start)
            return IsDaySelected(days, nowLocal.DayOfWeek);

        if (timeOfDay < end)
            return IsDaySelected(days, nowLocal.AddDays(-1).DayOfWeek);

        return false;
    }

    public NotificationDeliveryDecision Evaluate(NotificationKind kind, MailAccountPreferences? accountPreferences, DateTimeOffset now)
    {
        if (GetSnoozeState(now).IsSnoozed)
            return NotificationDeliveryDecision.Suppress(NotificationSuppressionReason.Snoozed);

        if (_preferencesService.SnoozeWhilePresenting && _userPresenceStateProvider.IsPresenting())
            return NotificationDeliveryDecision.Suppress(NotificationSuppressionReason.Snoozed);

        if (!IsTypeEnabled(kind, accountPreferences))
            return NotificationDeliveryDecision.Suppress(NotificationSuppressionReason.TypeDisabled);

        if (accountPreferences is not null && !accountPreferences.IsNotificationsEnabled)
            return NotificationDeliveryDecision.Suppress(NotificationSuppressionReason.AccountDisabled);

        if (IsHeldByQuietHours(accountPreferences, now.ToLocalTime()))
            return NotificationDeliveryDecision.Suppress(NotificationSuppressionReason.QuietHours);

        return NotificationDeliveryDecision.Deliver;
    }

    private bool IsTypeEnabled(NotificationKind kind, MailAccountPreferences? accountPreferences)
        => kind switch
        {
            NotificationKind.Mail => NotificationSettingsResolver.ResolveMail(_preferencesService, accountPreferences).IsEnabled,
            NotificationKind.CalendarReminder => NotificationSettingsResolver.ResolveCalendarRemindersEnabled(_preferencesService, accountPreferences),
            NotificationKind.TaskReminder => _preferencesService.AreTaskRemindersEnabled,
            _ => true
        };

    private bool IsHeldByQuietHours(MailAccountPreferences? accountPreferences, DateTimeOffset nowLocal)
    {
        var stance = accountPreferences?.QuietHoursStance ?? AccountQuietHoursStance.Follow;

        return stance switch
        {
            AccountQuietHoursStance.AlwaysNotify => false,
            AccountQuietHoursStance.NeverOutsideWorkingHours => !IsWithinWorkingHours(nowLocal),
            _ => IsQuietNow(nowLocal)
        };
    }

    /// <summary>
    /// Reuses the calendar's working hours preferences rather than introducing a third time model.
    /// </summary>
    private bool IsWithinWorkingHours(DateTimeOffset nowLocal)
    {
        if (!_preferencesService.IsWorkingHoursEnabled)
            return true;

        var timeOfDay = nowLocal.TimeOfDay;

        if (timeOfDay < _preferencesService.WorkingHourStart || timeOfDay >= _preferencesService.WorkingHourEnd)
            return false;

        return IsWithinWorkingDays(nowLocal.DayOfWeek);
    }

    private bool IsWithinWorkingDays(DayOfWeek day)
    {
        var start = (int)_preferencesService.WorkingDayStart;
        var end = (int)_preferencesService.WorkingDayEnd;
        var current = (int)day;

        if (end < start)
        {
            end += 7;

            if (current < start)
                current += 7;
        }

        return current >= start && current <= end;
    }

    private static bool IsDaySelected(QuietHoursDays days, DayOfWeek day)
        => days.HasFlag(ToQuietHoursDay(day));

    private static QuietHoursDays ToQuietHoursDay(DayOfWeek day)
        => day switch
        {
            DayOfWeek.Monday => QuietHoursDays.Monday,
            DayOfWeek.Tuesday => QuietHoursDays.Tuesday,
            DayOfWeek.Wednesday => QuietHoursDays.Wednesday,
            DayOfWeek.Thursday => QuietHoursDays.Thursday,
            DayOfWeek.Friday => QuietHoursDays.Friday,
            DayOfWeek.Saturday => QuietHoursDays.Saturday,
            _ => QuietHoursDays.Sunday
        };

    private static DateTimeOffset? ResolveSnoozeEnd(NotificationSnoozePreset preset, DateTimeOffset? customUntilLocal, DateTimeOffset now)
        => preset switch
        {
            NotificationSnoozePreset.ThirtyMinutes => now.AddMinutes(30),
            NotificationSnoozePreset.OneHour => now.AddHours(1),
            NotificationSnoozePreset.TwoHours => now.AddHours(2),
            NotificationSnoozePreset.RestOfDay => NextLocalMidnight(now),
            NotificationSnoozePreset.UntilTomorrowMorning => NextLocalMidnight(now).AddHours(8),
            NotificationSnoozePreset.Custom => customUntilLocal,
            _ => null
        };

    /// <summary>
    /// Midnight at the start of tomorrow, local time. Built from the local calendar date rather than
    /// by adding 24 hours so it stays correct across a daylight saving transition.
    /// </summary>
    private static DateTimeOffset NextLocalMidnight(DateTimeOffset now)
    {
        var tomorrow = now.ToLocalTime().Date.AddDays(1);

        return new DateTimeOffset(tomorrow, TimeZoneInfo.Local.GetUtcOffset(tomorrow));
    }
}
