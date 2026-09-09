using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Calendar;

namespace Wino.Core.Tests;

public class CalendarRenderingSettingsPageViewModelTests
{
    [Theory]
    [InlineData(CalendarEventDisplayMode.Stacked)]
    [InlineData(CalendarEventDisplayMode.Overlapped)]
    public void Initialization_LoadsPreferenceWithoutWriting(CalendarEventDisplayMode mode)
    {
        var preferences = CreatePreferences(mode);

        var viewModel = CreateViewModel(preferences.Object);

        viewModel.SelectedEventDisplayModeOption.Mode.Should().Be(mode);
        preferences.VerifySet(service => service.CalendarEventDisplayMode = It.IsAny<CalendarEventDisplayMode>(), Times.Never);
    }

    [Fact]
    public void ChangingSelection_PersistsAndNotifiesAfterSaving()
    {
        var preferences = CreatePreferences(CalendarEventDisplayMode.Stacked);
        var viewModel = CreateViewModel(preferences.Object);
        var recipient = new object();
        var observedModes = new List<CalendarEventDisplayMode>();
        WeakReferenceMessenger.Default.Register<CalendarSettingsUpdatedMessage>(recipient,
            (_, _) => observedModes.Add(preferences.Object.CalendarEventDisplayMode));

        try
        {
            viewModel.SelectedEventDisplayModeOption = viewModel.EventDisplayModeOptions[1];
            CreateViewModel(preferences.Object).SelectedEventDisplayModeOption.Mode.Should().Be(CalendarEventDisplayMode.Overlapped);
            viewModel.SelectedEventDisplayModeOption = viewModel.EventDisplayModeOptions[0];

            observedModes.Should().Equal(CalendarEventDisplayMode.Overlapped, CalendarEventDisplayMode.Stacked);
            preferences.Object.CalendarEventDisplayMode.Should().Be(CalendarEventDisplayMode.Stacked);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public void UnknownPreference_SelectsStacked()
    {
        var preferences = CreatePreferences((CalendarEventDisplayMode)42);

        CreateViewModel(preferences.Object).SelectedEventDisplayModeOption.Mode.Should().Be(CalendarEventDisplayMode.Stacked);
    }

    [Fact]
    public void CalendarSettings_DefaultIsStackedAndExplicitModeIsPreserved()
    {
        var settings = new CalendarSettings(DayOfWeek.Monday, [], false, DayOfWeek.Monday, DayOfWeek.Friday,
            TimeSpan.FromHours(9), TimeSpan.FromHours(17), 60, DayHeaderDisplayType.TwentyFourHour, CultureInfo.InvariantCulture);

        settings.EventDisplayMode.Should().Be(CalendarEventDisplayMode.Stacked);
        (settings with { EventDisplayMode = CalendarEventDisplayMode.Overlapped }).EventDisplayMode.Should().Be(CalendarEventDisplayMode.Overlapped);
    }

    private static Mock<IPreferencesService> CreatePreferences(CalendarEventDisplayMode mode)
    {
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupProperty(service => service.CalendarEventDisplayMode, mode);
        preferences.SetupGet(service => service.CalendarTimedDayHeaderDateFormat).Returns("ddd dd");
        return preferences;
    }

    private static CalendarRenderingSettingsPageViewModel CreateViewModel(IPreferencesService preferences)
        => new(preferences, Mock.Of<ICalendarService>(), Mock.Of<IAccountService>());
}
