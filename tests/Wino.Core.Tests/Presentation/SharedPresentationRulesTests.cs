using System.Globalization;
using FluentAssertions;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.Domain.Presentation;
using Xunit;

namespace Wino.Core.Tests.Presentation;

public sealed class WinoThemeCatalogTests
{
    [Fact]
    public void CatalogHasTheSystemThemeFirstAndTwentyOneUniqueThemes()
    {
        WinoThemeCatalog.PredefinedThemes.Should().HaveCount(21);
        WinoThemeCatalog.PredefinedThemes[0].Id.Should().Be(WinoThemeCatalog.DefaultThemeId);
        WinoThemeCatalog.PredefinedThemes[0].IsSystemTheme.Should().BeTrue();
        WinoThemeCatalog.PredefinedThemes.Select(theme => theme.Id).Should().OnlyHaveUniqueItems();
        WinoThemeCatalog.PredefinedThemes.Skip(1).Should().OnlyContain(theme => theme.AccentColor.StartsWith('#'));
    }

    [Fact]
    public void ForcedThemesMapToCompatibilityAndGradientsResolveByName()
    {
        var nighty = WinoThemeCatalog.Find(Guid.Parse("5b65e04e-fd7e-4c2d-8221-068d3e02d23a"));
        nighty.Should().NotBeNull();
        nighty!.ForcedElementTheme.Should().Be(ApplicationElementTheme.Dark);
        nighty.Compatibility.Should().Be(ThemeCompatibility.Dark);

        WinoThemeCatalog.GradientFor("Mist").Should().Be(new WinoThemeGradient(0xF4F7FA, 0xE1E7EF, 0x191D23, 0x0C0E12));
        WinoThemeCatalog.GradientFor("Nighty").Should().BeNull();
        WinoThemeCatalog.GradientFor(null).Should().BeNull();
        WinoThemeCatalog.IsPredefined(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void ConfigurationKeysKeepTheirStoredNames()
    {
        WinoThemeCatalog.SelectedAppThemeKey.Should().Be("SelectedAppThemeKey");
        WinoThemeCatalog.AccentColorKey.Should().Be("AccentColorKey");
        WinoThemeCatalog.CurrentApplicationThemeKey.Should().Be("CurrentApplicationThemeKey");
    }
}

public sealed class PresentationMapTests
{
    [Fact]
    public void EveryTitledMailOperationHasATitle()
    {
        foreach (var operation in new[] { MailOperation.Archive, MailOperation.SoftDelete, MailOperation.Move, MailOperation.Reply, MailOperation.SaveAsPdf })
            MailOperationDisplay.GetTitle(operation).Should().NotBeNullOrWhiteSpace();

        MailOperationDisplay.GetTitle(MailOperation.None).Should().BeEmpty();
        MailOperationDisplay.GetGlyph(MailOperation.HardDelete).Should().Be(WinoIconGlyph.Delete);
        MailOperationDisplay.GetGlyph(MailOperation.None).Should().Be(WinoIconGlyph.None);
    }

    [Fact]
    public void FolderOperationsFlagDestructiveAndDialogActions()
    {
        FolderOperationDisplay.IsDestructive(FolderOperation.Delete).Should().BeTrue();
        FolderOperationDisplay.IsDestructive(FolderOperation.Empty).Should().BeTrue();
        FolderOperationDisplay.IsDestructive(FolderOperation.Rename).Should().BeFalse();
        FolderOperationDisplay.AsksForInput(FolderOperation.Rename).Should().BeTrue();
        FolderOperationDisplay.GetGlyph(FolderOperation.Move).Should().Be(WinoIconGlyph.Forward);
    }

    [Fact]
    public void SpecialFolderAndProviderGlyphsFallBackAsDocumented()
    {
        SpecialFolderGlyphs.GetGlyph(SpecialFolderType.Inbox).Should().Be(WinoIconGlyph.SpecialFolderInbox);
        SpecialFolderGlyphs.GetGlyph(SpecialFolderType.Other).Should().Be(WinoIconGlyph.SpecialFolderOther);
        SpecialFolderGlyphs.GetGlyphOrFolder((SpecialFolderType)999).Should().Be(WinoIconGlyph.Folder);

        MailProviderGlyphs.GetGlyph(MailProviderType.Outlook, SpecialImapProvider.None).Should().Be(WinoIconGlyph.Microsoft);
        MailProviderGlyphs.GetGlyph(MailProviderType.IMAP4, SpecialImapProvider.iCloud).Should().Be(WinoIconGlyph.Apple);
        MailProviderGlyphs.GetGlyph(MailProviderType.POP3, SpecialImapProvider.None).Should().Be(WinoIconGlyph.None);
    }

    [Fact]
    public void ExclusiveFilterActionsMoveOrRemoveTheMessage()
    {
        MailFilterDisplay.IsExclusiveAction(MailFilterActionType.Move).Should().BeTrue();
        MailFilterDisplay.IsExclusiveAction(MailFilterActionType.HardDelete).Should().BeTrue();
        MailFilterDisplay.IsExclusiveAction(MailFilterActionType.MarkRead).Should().BeFalse();
        MailFilterDisplay.IsExclusiveAction(null).Should().BeFalse();
        MailFilterDisplay.GetActionTone(MailFilterActionType.SoftDelete).Should().Be(MailFilterActionTone.Critical);
        MailFilterDisplay.GetActionTone(MailFilterActionType.Archive).Should().Be(MailFilterActionTone.Attention);
    }

    [Fact]
    public void SettingsPagesFollowPlatformAndEntitlement()
    {
        SettingsPageAvailability.IsAvailable(WinoPage.SignatureAndEncryptionPage, isSmimeSupported: false, canAccessIntelligence: true).Should().BeFalse();
        SettingsPageAvailability.IsAvailable(WinoPage.WinoIntelligencePage, isSmimeSupported: true, canAccessIntelligence: false).Should().BeFalse();
        SettingsPageAvailability.IsAvailable(WinoPage.AppPreferencesPage, isSmimeSupported: false, canAccessIntelligence: false).Should().BeTrue();
    }

    [Fact]
    public void BirthdayFormatsWithOrWithoutTheYear()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            ContactDisplayFormatter.FormatBirthday(1990, 3, 14).Should().Be("14 March 1990");
            ContactDisplayFormatter.FormatBirthday(null, 2, 29).Should().Be("29 February");
            ContactDisplayFormatter.FormatBirthday(2023, 2, 31).Should().Be("28 February 2023");
            ContactDisplayFormatter.FormatBirthday(0, 3, 14).Should().Be("14 March");
            ContactDisplayFormatter.FormatBirthday(1990, 13, 1).Should().BeEmpty();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ReadableTextColorUsesBothDocumentedRules()
    {
        ReadableTextColor.PrefersDarkText(1, 1, 1).Should().BeTrue();
        ReadableTextColor.PrefersDarkText(0, 0, 0.5).Should().BeFalse();
        ReadableTextColor.TryParseRgb("#FFD700", out var r, out var g, out var b).Should().BeTrue();
        ReadableTextColor.PrefersDarkTextByLuminance(r, g, b).Should().BeTrue();
        ReadableTextColor.PrefersDarkTextByLuminance(0x1E, 0x3A, 0x8A).Should().BeFalse();
        ReadableTextColor.TryParseRgb("nope", out _, out _, out _).Should().BeFalse();
    }
}

public sealed class CalendarPresentationTests
{
    private static CalendarItem Item(DateTime start, TimeSpan duration, string title = "Event")
        => new() { Id = Guid.NewGuid(), Title = title, StartDate = start, DurationInSeconds = duration.TotalSeconds };

    private static readonly DateOnly Day = new(2026, 10, 14);

    [Fact]
    public void OverlappingStackedEventsSplitTheDayIntoColumns()
    {
        var items = new ICalendarItem[]
        {
            Item(new DateTime(2026, 10, 14, 9, 0, 0), TimeSpan.FromHours(2)),
            Item(new DateTime(2026, 10, 14, 10, 0, 0), TimeSpan.FromHours(1)),
            Item(new DateTime(2026, 10, 14, 13, 0, 0), TimeSpan.FromHours(1))
        };

        var placements = CalendarSurfaceLayoutCalculator.CalculateTimed([Day], items, 200, 60, CalendarEventDisplayMode.Stacked);

        placements.Should().HaveCount(3);
        placements[0].Bounds.Should().Be(new CalendarLayoutRect(2, 540, 96, 120));
        placements[1].Bounds.Should().Be(new CalendarLayoutRect(102, 600, 96, 60));
        placements[2].Bounds.Should().Be(new CalendarLayoutRect(2, 780, 200 - 4 - CalendarSurfaceLayoutCalculator.ItemRightSpacing, 60));
        placements.Should().OnlyContain(placement => placement.Kind == CalendarPlacementKind.Timed);
    }

    [Fact]
    public void AllDayEventsStackInLanesAndSetTheStripHeight()
    {
        var items = new ICalendarItem[]
        {
            Item(new DateTime(2026, 10, 14), TimeSpan.FromDays(1), "A"),
            Item(new DateTime(2026, 10, 13), TimeSpan.FromDays(3), "B")
        };

        CalendarSurfaceLayoutCalculator.GetAllDayLaneCount([Day], items).Should().Be(2);
        CalendarSurfaceLayoutCalculator.GetAllDayHeight(2).Should().Be(6 * 2 + 32 * 2 + 4);

        var placements = CalendarSurfaceLayoutCalculator.CalculateAllDay([Day], items, 100);
        placements.Select(placement => placement.Item.Title).Should().Equal("B", "A");
        placements[1].Bounds.Y.Should().Be(6 + 32 + 4);
        placements.Should().OnlyContain(placement => placement.Kind == CalendarPlacementKind.AllDay);
    }

    [Fact]
    public void MonthGridIsWeekAlignedAndPacksCellEntries()
    {
        var dates = CalendarSurfaceLayoutCalculator.MonthGridDates(new DateOnly(2026, 10, 1), DayOfWeek.Monday);
        dates.Should().HaveCount(42);
        dates[0].Should().Be(new DateOnly(2026, 9, 28));

        var items = new ICalendarItem[] { Item(new DateTime(2026, 10, 14, 9, 0, 0), TimeSpan.FromHours(1)) };
        var (cells, placements) = CalendarSurfaceLayoutCalculator.CalculateMonth(dates, items, 700, 600);

        cells.Should().HaveCount(42);
        cells[16].Date.Should().Be(Day);
        cells[16].Bounds.Should().Be(new CalendarLayoutRect(200, 200, 100, 100));
        placements.Should().ContainSingle().Which.Bounds.Should().Be(new CalendarLayoutRect(204, 224, 92, 18));
    }

    [Theory]
    [InlineData("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE", CalendarItemRecurrenceFrequency.Weekly, 2, 2)]
    [InlineData("FREQ=MONTHLY;BYDAY=1FR", CalendarItemRecurrenceFrequency.Monthly, 1, 1)]
    [InlineData("EXDATE:20261020T090000Z___RRULE:FREQ=DAILY;UNTIL=20261231", CalendarItemRecurrenceFrequency.Daily, 1, 0)]
    public void RecurrenceRulesParseFrequencyIntervalAndDays(string recurrence, CalendarItemRecurrenceFrequency frequency, int interval, int dayCount)
    {
        CalendarRecurrenceRuleParser.TryParse(recurrence, out var rule).Should().BeTrue();
        rule!.Frequency.Should().Be(frequency);
        rule.Interval.Should().Be(interval);
        rule.Days.Should().HaveCount(dayCount);
    }

    [Fact]
    public void RecurrenceUntilIsReadAsUtcAndUnknownRulesAreRejected()
    {
        CalendarRecurrenceRuleParser.TryParse("RRULE:FREQ=DAILY;UNTIL=20261231T120000Z", out var rule).Should().BeTrue();
        rule!.Until.Should().Be(new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.Zero));

        CalendarRecurrenceRuleParser.TryParse("RRULE:FREQ=SECONDLY", out _).Should().BeFalse();
        CalendarRecurrenceRuleParser.TryParse(null, out _).Should().BeFalse();
    }

    [Fact]
    public void DragRefusalChecksBusyAndMissingItems()
    {
        CalendarEventDisplay.GetDragRefusal(null, isBusy: false).Should().Be(CalendarDragRefusal.Busy);
    }
}
