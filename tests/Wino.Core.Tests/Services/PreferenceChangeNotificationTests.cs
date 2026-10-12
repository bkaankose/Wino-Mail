using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Client.Shell;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class PreferenceChangeNotificationTests
{
    public static TheoryData<string, Action<IPreferencesService>> FormerlySilentSetters => new()
    {
        { nameof(IPreferencesService.FirstDayOfWeek), p => p.FirstDayOfWeek = DayOfWeek.Sunday },
        { nameof(IPreferencesService.HourHeight), p => p.HourHeight = 80 },
        { nameof(IPreferencesService.IsWorkingHoursEnabled), p => p.IsWorkingHoursEnabled = false },
        { nameof(IPreferencesService.WorkingHourStart), p => p.WorkingHourStart = TimeSpan.FromHours(8) },
        { nameof(IPreferencesService.WorkingHourEnd), p => p.WorkingHourEnd = TimeSpan.FromHours(17) },
        { nameof(IPreferencesService.WorkingDayStart), p => p.WorkingDayStart = DayOfWeek.Sunday },
        { nameof(IPreferencesService.WorkingDayEnd), p => p.WorkingDayEnd = DayOfWeek.Thursday },
        { nameof(IPreferencesService.CalendarTimedDayHeaderDateFormat), p => p.CalendarTimedDayHeaderDateFormat = "dd ddd" },
        { nameof(IPreferencesService.AutoSelectNextItem), p => p.AutoSelectNextItem = false },
        { nameof(IPreferencesService.DefaultSearchMode), p => p.DefaultSearchMode = Wino.Core.Domain.Enums.SearchMode.Online },
        { nameof(IPreferencesService.DefaultReminderDurationInSeconds), p => p.DefaultReminderDurationInSeconds = 600 },
        { nameof(IPreferencesService.DefaultSnoozeDurationInMinutes), p => p.DefaultSnoozeDurationInMinutes = 10 },
        { nameof(IPreferencesService.StartupEntityId), p => p.StartupEntityId = Guid.NewGuid() },
    };

    [Theory]
    [MemberData(nameof(FormerlySilentSetters))]
    public void Setter_SendsPreferenceChangedMessage(string propertyName, Action<IPreferencesService> set)
    {
        var preferences = new PreferencesService(new PreferencesPortabilityTests.MemoryConfiguration());

        var received = Capture(() => set(preferences));

        received.Should().Contain(propertyName);
    }

    [Fact]
    public void StoreUpdateNotificationsSetter_RaisesTheEventOnce()
    {
        var preferences = new PreferencesService(new PreferencesPortabilityTests.MemoryConfiguration());
        var changes = new List<string>();
        preferences.PreferenceChanged += (_, property) => changes.Add(property);

        preferences.IsStoreUpdateNotificationsEnabled = false;

        changes.Should().Equal(nameof(IPreferencesService.IsStoreUpdateNotificationsEnabled));
    }

    [Fact]
    public void Import_SendsMessagesForCalendarRenderingValues()
    {
        var preferences = new PreferencesService(new PreferencesPortabilityTests.MemoryConfiguration());

        var received = Capture(() => preferences.ImportPreferences("""
            {"FirstDayOfWeek": "Sunday", "HourHeight": 72}
            """));

        received.Should().Contain(nameof(IPreferencesService.FirstDayOfWeek))
            .And.Contain(nameof(IPreferencesService.HourHeight));
    }

    private static List<string> Capture(Action action)
    {
        var recipient = new object();
        var received = new List<string>();
        WeakReferenceMessenger.Default.Register<object, PreferenceChangedMessage>(recipient, (_, message) =>
        {
            lock (received) received.Add(message.PropertyName);
        });

        try
        {
            action();
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }

        lock (received) return [.. received];
    }
}
