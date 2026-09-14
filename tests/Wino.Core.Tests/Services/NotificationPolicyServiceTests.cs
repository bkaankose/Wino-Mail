using System;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class NotificationPolicyServiceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    #region Snooze

    [Fact]
    public void GetSnoozeState_ReportsDeliveringWhenNothingIsSnoozed()
    {
        var service = CreateService(out _);

        service.GetSnoozeState(Noon).IsSnoozed.Should().BeFalse();
    }

    [Fact]
    public void StartSnooze_OneHour_EndsOneHourLater()
    {
        var service = CreateService(out var preferences);

        service.StartSnooze(NotificationSnoozePreset.OneHour, customUntilLocal: null, Noon);

        preferences.Object.NotificationSnoozePreset.Should().Be(NotificationSnoozePreset.OneHour);
        new DateTimeOffset(preferences.Object.NotificationSnoozeUntilUtcTicks, TimeSpan.Zero)
            .Should().Be(Noon.AddHours(1));
    }

    [Fact]
    public void GetSnoozeState_StaysSnoozedOneTickBeforeExpiry()
    {
        var service = CreateService(out _);

        service.StartSnooze(NotificationSnoozePreset.OneHour, customUntilLocal: null, Noon);

        service.GetSnoozeState(Noon.AddHours(1).AddTicks(-1)).IsSnoozed.Should().BeTrue();
    }

    [Fact]
    public void GetSnoozeState_ClearsItselfOnceTheSnoozeHasElapsed()
    {
        var service = CreateService(out var preferences);

        service.StartSnooze(NotificationSnoozePreset.OneHour, customUntilLocal: null, Noon);

        var state = service.GetSnoozeState(Noon.AddHours(1));

        state.IsSnoozed.Should().BeFalse();

        // Expiry is lazy, so reading must heal the stored state rather than leave it stale.
        preferences.Object.NotificationSnoozePreset.Should().Be(NotificationSnoozePreset.None);
        preferences.Object.NotificationSnoozeUntilUtcTicks.Should().Be(0);
    }

    [Fact]
    public void GetSnoozeState_UntilTurnedBackOn_NeverExpires()
    {
        var service = CreateService(out var preferences);

        service.StartSnooze(NotificationSnoozePreset.UntilTurnedBackOn, customUntilLocal: null, Noon);

        preferences.Object.NotificationSnoozeUntilUtcTicks.Should().Be(0);

        var state = service.GetSnoozeState(Noon.AddYears(1));

        state.IsSnoozed.Should().BeTrue();
        state.Until.Should().BeNull();
    }

    [Fact]
    public void StartSnooze_RestOfDay_EndsAtTheNextLocalMidnight()
    {
        var service = CreateService(out var preferences);
        var now = DateTimeOffset.Now;

        service.StartSnooze(NotificationSnoozePreset.RestOfDay, customUntilLocal: null, now);

        var until = new DateTimeOffset(preferences.Object.NotificationSnoozeUntilUtcTicks, TimeSpan.Zero).ToLocalTime();

        until.Date.Should().Be(now.LocalDateTime.Date.AddDays(1));
        until.TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void StartSnooze_UntilTomorrowMorning_EndsAtEightLocal()
    {
        var service = CreateService(out var preferences);
        var now = DateTimeOffset.Now;

        service.StartSnooze(NotificationSnoozePreset.UntilTomorrowMorning, customUntilLocal: null, now);

        var until = new DateTimeOffset(preferences.Object.NotificationSnoozeUntilUtcTicks, TimeSpan.Zero).ToLocalTime();

        until.Date.Should().Be(now.LocalDateTime.Date.AddDays(1));
        until.Hour.Should().Be(8);
    }

    [Fact]
    public void StartSnooze_RemembersTheChosenPresetForNextTime()
    {
        var service = CreateService(out var preferences);

        service.StartSnooze(NotificationSnoozePreset.TwoHours, customUntilLocal: null, Noon);
        service.EndSnooze();

        preferences.Object.LastUsedSnoozePreset.Should().Be(NotificationSnoozePreset.TwoHours);
    }

    #endregion

    #region Quiet hours

    [Fact]
    public void IsQuietNow_IsFalseWhenTheScheduleIsDisabled()
    {
        var service = CreateService(out var preferences);

        preferences.Object.AreQuietHoursEnabled = false;
        preferences.Object.QuietHoursStart = new TimeSpan(18, 30, 0);
        preferences.Object.QuietHoursEnd = new TimeSpan(8, 0, 0);

        service.IsQuietNow(new DateTimeOffset(2026, 9, 14, 20, 0, 0, TimeSpan.Zero)).Should().BeFalse();
    }

    [Fact]
    public void IsQuietNow_HonoursAWindowInsideOneDay()
    {
        var service = CreateQuietHours(new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0), QuietHoursDays.All);

        // Monday 14 September 2026.
        service.IsQuietNow(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero)).Should().BeTrue();
        service.IsQuietNow(new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero)).Should().BeFalse();
        service.IsQuietNow(new DateTimeOffset(2026, 9, 14, 17, 0, 0, TimeSpan.Zero)).Should().BeFalse();
    }

    [Fact]
    public void IsQuietNow_AttributesAMidnightWrapToTheDayItStartedOn()
    {
        // Friday only. The window runs Friday 18:30 to Saturday 08:00, so it must still hold at
        // 02:00 on Saturday even though Saturday itself is not selected.
        var service = CreateQuietHours(new TimeSpan(18, 30, 0), new TimeSpan(8, 0, 0), QuietHoursDays.Friday);

        service.IsQuietNow(new DateTimeOffset(2026, 9, 18, 20, 0, 0, TimeSpan.Zero)).Should().BeTrue();
        service.IsQuietNow(new DateTimeOffset(2026, 9, 19, 2, 0, 0, TimeSpan.Zero)).Should().BeTrue();

        // Saturday evening is outside the Friday window.
        service.IsQuietNow(new DateTimeOffset(2026, 9, 19, 20, 0, 0, TimeSpan.Zero)).Should().BeFalse();
    }

    [Fact]
    public void IsQuietNow_TreatsAnEmptyWindowAsNeverRatherThanAlways()
    {
        var service = CreateQuietHours(new TimeSpan(9, 0, 0), new TimeSpan(9, 0, 0), QuietHoursDays.All);

        service.IsQuietNow(new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero)).Should().BeFalse();
    }

    [Fact]
    public void IsQuietNow_IsFalseOnADayThatIsNotSelected()
    {
        var service = CreateQuietHours(new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0), QuietHoursDays.Weekend);

        // Monday.
        service.IsQuietNow(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero)).Should().BeFalse();
    }

    #endregion

    #region Evaluate

    [Fact]
    public void Evaluate_SuppressesEverythingWhileSnoozed()
    {
        var service = CreateService(out _);

        service.StartSnooze(NotificationSnoozePreset.OneHour, customUntilLocal: null, Noon);

        var decision = service.Evaluate(NotificationKind.Mail, CreateAccountPreferences(), Noon);

        decision.ShouldDeliver.Should().BeFalse();
        decision.Reason.Should().Be(NotificationSuppressionReason.Snoozed);
    }

    [Fact]
    public void Evaluate_SuppressesWhenTheAccountHasNotificationsOff()
    {
        var service = CreateService(out _);
        var account = CreateAccountPreferences();

        account.IsNotificationsEnabled = false;

        service.Evaluate(NotificationKind.Mail, account, Noon).ShouldDeliver.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_SuppressesWhenTheTypeIsTurnedOff()
    {
        var service = CreateService(out var preferences);

        preferences.Object.AreCalendarRemindersEnabled = false;

        var decision = service.Evaluate(NotificationKind.CalendarReminder, CreateAccountPreferences(), Noon);

        decision.ShouldDeliver.Should().BeFalse();
        decision.Reason.Should().Be(NotificationSuppressionReason.TypeDisabled);
    }

    [Fact]
    public void Evaluate_LetsAnAccountOptOutOfQuietHours()
    {
        var service = CreateQuietHours(new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0), QuietHoursDays.All);
        var account = CreateAccountPreferences();
        var duringQuietHours = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        account.QuietHoursStance = AccountQuietHoursStance.Follow;
        service.Evaluate(NotificationKind.Mail, account, duringQuietHours).ShouldDeliver.Should().BeFalse();

        account.QuietHoursStance = AccountQuietHoursStance.AlwaysNotify;
        service.Evaluate(NotificationKind.Mail, account, duringQuietHours).ShouldDeliver.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_DeliversWhenNothingIsSuppressingIt()
    {
        var service = CreateService(out _);

        service.Evaluate(NotificationKind.Mail, CreateAccountPreferences(), Noon).ShouldDeliver.Should().BeTrue();
    }

    #endregion

    private static NotificationPolicyService CreateQuietHours(TimeSpan start, TimeSpan end, QuietHoursDays days)
    {
        var service = CreateService(out var preferences);

        preferences.Object.AreQuietHoursEnabled = true;
        preferences.Object.QuietHoursStart = start;
        preferences.Object.QuietHoursEnd = end;
        preferences.Object.QuietHoursDays = days;

        return service;
    }

    private static NotificationPolicyService CreateService(out Mock<IPreferencesService> preferences)
    {
        preferences = CreatePreferences();

        var presence = new Mock<IUserPresenceStateProvider>();
        presence.Setup(provider => provider.IsPresenting()).Returns(false);
        presence.Setup(provider => provider.IsSystemQuietTimeActive()).Returns(false);

        return new NotificationPolicyService(preferences.Object, presence.Object);
    }

    /// <summary>
    /// A preferences mock whose notification properties behave like real storage, so the service
    /// can be exercised end to end rather than only through its reads.
    /// </summary>
    private static Mock<IPreferencesService> CreatePreferences()
    {
        var preferences = new Mock<IPreferencesService>();

        preferences.SetupProperty(service => service.NotificationSnoozePreset, NotificationSnoozePreset.None);
        preferences.SetupProperty(service => service.NotificationSnoozeUntilUtcTicks, 0L);
        preferences.SetupProperty(service => service.LastUsedSnoozePreset, NotificationSnoozePreset.OneHour);
        preferences.SetupProperty(service => service.AreQuietHoursEnabled, false);
        preferences.SetupProperty(service => service.QuietHoursStart, new TimeSpan(18, 30, 0));
        preferences.SetupProperty(service => service.QuietHoursEnd, new TimeSpan(8, 0, 0));
        preferences.SetupProperty(service => service.QuietHoursDays, QuietHoursDays.Weekdays);
        preferences.SetupProperty(service => service.SnoozeWhilePresenting, false);
        preferences.SetupProperty(service => service.AreNewMailNotificationsEnabled, true);
        preferences.SetupProperty(service => service.AreCalendarRemindersEnabled, true);
        preferences.SetupProperty(service => service.AreTaskRemindersEnabled, true);
        preferences.SetupProperty(service => service.MailNotificationScope, MailNotificationScope.InboxOnly);
        preferences.SetupProperty(service => service.MailNotificationContent, MailNotificationContent.SenderSubjectPreview);
        preferences.SetupProperty(service => service.MailNotificationSoundEvent, NotificationSoundEvent.Mail);
        preferences.SetupProperty(service => service.IsWorkingHoursEnabled, false);

        return preferences;
    }

    private static MailAccountPreferences CreateAccountPreferences() => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        IsNotificationsEnabled = true
    };
}
