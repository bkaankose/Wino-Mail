using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.Core.IntelligenceTileBar;
using Wino.Mail.Controls.Core.Layout;
using Wino.Mail.Controls.Core.Settings;
using Wino.Presentation.Controls;
using Xunit;

namespace Wino.Mail.Controls.Tests;

/// <summary>Rules moved out of the AppKit controls so the UIKit twins share them.</summary>
public sealed class SharedLayoutRulesTests
{
    [Fact]
    public void FlowLayoutWrapsItemsThatDoNotFitAndClipsWideOnes()
    {
        var result = WinoFlowLayout.Arrange([(60, 18), (60, 18), (60, 22), (300, 18)], 130, 4, 6);

        result.Frames[0].Should().Be(new WinoLayoutFrame(0, 0, 60, 18));
        result.Frames[1].Should().Be(new WinoLayoutFrame(64, 0, 60, 18));
        result.Frames[2].Should().Be(new WinoLayoutFrame(0, 24, 60, 22));
        result.Frames[3].Should().Be(new WinoLayoutFrame(0, 52, 130, 18));
        result.Height.Should().Be(70);
        WinoFlowLayout.Arrange(Array.Empty<(double, double)>(), 100, 4, 4).Height.Should().Be(0);
    }

    [Fact]
    public void SwatchGridFitsColumnsAndInsetsTheFrames()
    {
        WinoSwatchGridLayout.Columns(0).Should().Be(1);
        WinoSwatchGridLayout.Columns(90).Should().Be(3);
        WinoSwatchGridLayout.Rows(7, 3).Should().Be(3);
        WinoSwatchGridLayout.Rows(0, 3).Should().Be(1);
        WinoSwatchGridLayout.Frame(4, 3).Should().Be(new WinoLayoutFrame(32, 32, 22, 22));
        WinoSwatchGridLayout.Height(7, 90).Should().Be(3 * 22 + 2 * 8 + 4);
    }

    [Fact]
    public void AspectFillCoversTheBoundsCentred()
    {
        WinoAspectFill.Frame(200, 100, 100, 100).Should().Be(new WinoLayoutFrame(-50, 0, 200, 100));
        WinoAspectFill.Frame(0, 100, 100, 100).Should().BeNull();
    }

    [Fact]
    public void ControlTextFormatsCountsLikeWindows()
    {
        WinoControlText.UnreadPill(5).Should().Be("5");
        WinoControlText.UnreadPill(1000).Should().Be("999+");
        WinoControlText.WithCount("Focused", 2).Should().Be("Focused (2)");
        WinoControlText.WithCount("Other", 0).Should().Be("Other");
        WinoControlText.WithCount(null, 0).Should().BeEmpty();
    }

    [Fact]
    public void ReusablePrefixStopsAtTheFirstMismatch()
    {
        WinoListDiff.ReusablePrefix(new[] { 1, 2, 3 }, new[] { 1, 2, 4, 5 }, (a, b) => a == b).Should().Be(2);
        WinoListDiff.ReusablePrefix(new[] { 1 }, Array.Empty<int>(), (a, b) => a == b).Should().Be(0);
    }

    [Fact]
    public void PagerClampsAndEnablesArrows()
    {
        WinoPagerState.Clamp(5, 3).Should().Be(2);
        WinoPagerState.Clamp(-2, 3).Should().Be(0);
        WinoPagerState.Clamp(1, 0).Should().Be(-1);
        WinoPagerState.CanGoNewer(0).Should().BeFalse();
        WinoPagerState.CanGoNewer(1).Should().BeTrue();
        WinoPagerState.CanGoOlder(1, 3).Should().BeTrue();
        WinoPagerState.CanGoOlder(2, 3).Should().BeFalse();
        WinoPagerState.CanGoOlder(-1, 3).Should().BeFalse();
    }

    [Fact]
    public void SettingsValuesClampParseAndDropSeconds()
    {
        SettingsValueRules.SelectableIndex(2, 3).Should().Be(2);
        SettingsValueRules.SelectableIndex(3, 3).Should().Be(-1);
        SettingsValueRules.SelectableIndex(-1, 3).Should().Be(-1);
        SettingsValueRules.ClampStepper(50, 1, 10).Should().Be(10);
        SettingsValueRules.ParseStepper("7", 3, 1, 10).Should().Be(7);
        SettingsValueRules.ParseStepper("x", 3, 1, 10).Should().Be(3);
        SettingsValueRules.ParseStepper("-4", 3, 1, 10).Should().Be(1);
        SettingsValueRules.TimeOfDay(new TimeSpan(0, 9, 30, 45, 120)).Should().Be(new TimeSpan(9, 30, 0));
    }

    [Fact]
    public void MiniCalendarHeaderButtonsSitRightAlignedAndHitTestWithSlop()
    {
        WinoMiniCalendarGrid.HeaderButtonBounds(WinoMiniCalendarHeaderButton.Toggle, 260).Should().Be((234d, 13d, 16d, 16d));
        WinoMiniCalendarGrid.HeaderButtonBounds(WinoMiniCalendarHeaderButton.Next, 260).Should().Be((210d, 13d, 16d, 16d));
        WinoMiniCalendarGrid.HeaderButtonBounds(WinoMiniCalendarHeaderButton.Previous, 260).Should().Be((186d, 13d, 16d, 16d));
        WinoMiniCalendarGrid.HitTestHeader(232, 12, 260).Should().Be(WinoMiniCalendarHeaderButton.Toggle);
        WinoMiniCalendarGrid.HitTestHeader(190, 20, 260).Should().Be(WinoMiniCalendarHeaderButton.Previous);
        WinoMiniCalendarGrid.HitTestHeader(100, 20, 260).Should().BeNull();
        WinoMiniCalendarGrid.TitleWidth(260).Should().Be(160);
    }
}

public sealed class WinoCalendarSurfaceGeometryTests
{
    private static readonly DateOnly[] Week = Enumerable.Range(0, 7).Select(day => new DateOnly(2026, 10, 12).AddDays(day)).ToArray();

    [Fact]
    public void SnapRoundsToTheNearestInterval()
    {
        WinoCalendarSurfaceGeometry.Snap(new DateTime(2026, 10, 13, 9, 7, 0), 15).Should().Be(new DateTime(2026, 10, 13, 9, 0, 0));
        WinoCalendarSurfaceGeometry.Snap(new DateTime(2026, 10, 13, 9, 8, 0), 15).Should().Be(new DateTime(2026, 10, 13, 9, 15, 0));
    }

    [Fact]
    public void SlotsAndCellsAreClampedToTheVisibleDays()
    {
        // 64pt gutter, 100pt days, 52pt hours: (180, 8 + 52 * 9.5) is Tuesday 9:30.
        WinoCalendarSurfaceGeometry.SlotStartAt(180, 8 + 52 * 9.5, Week, 100, 52).Should().Be(new DateTime(2026, 10, 13, 9, 30, 0));
        WinoCalendarSurfaceGeometry.SlotStartAt(30, 100, Week, 100, 52).Should().BeNull();
        WinoCalendarSurfaceGeometry.TimedCellAt(5000, 99999, Week, 100, 52, 15).Should().Be(new DateTime(2026, 10, 18, 23, 45, 0));
        WinoCalendarSurfaceGeometry.DayIndexAt(-50, 7, 100).Should().Be(0);
        WinoCalendarSurfaceGeometry.SlotBounds(1, 19, 100, 52).Should().Be(new WinoLayoutFrame(164, 8 + 19 * 26, 100, 26));
    }

    [Fact]
    public void TimedDragKeepsTheDurationAndResizeKeepsTheStart()
    {
        var start = new DateTime(2026, 10, 13, 9, 0, 0);
        var end = start.AddHours(1);
        var frame = new WinoLayoutFrame(165, 8 + 9 * 52, 98, 52);

        var move = WinoCalendarSurfaceGeometry.ResolveTimedDrag(Week, 100, 52, 15, false, (200, 500), (300, 526), start, end, frame, new DateOnly(2026, 10, 13));
        move.Should().NotBeNull();
        move.Value.Start.Should().Be(new DateTime(2026, 10, 14, 9, 30, 0));
        move.Value.End.Should().Be(new DateTime(2026, 10, 14, 10, 30, 0));
        move.Value.Frame.X.Should().Be(265);

        var resize = WinoCalendarSurfaceGeometry.ResolveTimedDrag(Week, 100, 52, 15, true, (200, 500), (200, 400), start, end, frame, new DateOnly(2026, 10, 13));
        resize.Value.Start.Should().Be(start);
        resize.Value.End.Should().Be(start.AddMinutes(15));
    }

    [Fact]
    public void AllDayAndMonthDragsMoveByWholeDays()
    {
        var start = new DateTime(2026, 10, 13);
        var allDay = WinoCalendarSurfaceGeometry.ResolveAllDayDrag(7, 100, 170, 380, start, start.AddDays(1), new WinoLayoutFrame(165, 2, 98, 20));
        allDay.Value.Start.Should().Be(start.AddDays(2));
        allDay.Value.Frame.X.Should().Be(365);

        var month = WinoCalendarSurfaceGeometry.ResolveMonthDrag(new DateOnly(2026, 10, 13), new WinoLayoutFrame(100, 0, 100, 80),
            new DateOnly(2026, 10, 21), new WinoLayoutFrame(200, 80, 100, 80), start.AddHours(9), start.AddHours(10), new WinoLayoutFrame(102, 20, 96, 18));
        month.Start.Should().Be(new DateTime(2026, 10, 21, 9, 0, 0));
        month.Frame.Should().Be(new WinoLayoutFrame(202, 100, 96, 18));
        WinoCalendarSurfaceGeometry.MonthPreviewOffset(9, 100, 80).Should().Be((200d, 80d));
    }

    [Fact]
    public void ScrollOffsetAndWeekendShading()
    {
        WinoCalendarSurfaceGeometry.InitialScrollOffset(8, 52, 1264, 600).Should().Be(7.5 * 52);
        WinoCalendarSurfaceGeometry.InitialScrollOffset(23, 52, 1264, 600).Should().Be(664);
        WinoCalendarSurfaceGeometry.IsShadedWeekend(new DateOnly(2026, 10, 17), 7).Should().BeTrue();
        WinoCalendarSurfaceGeometry.IsShadedWeekend(new DateOnly(2026, 10, 17), 1).Should().BeFalse();
        WinoCalendarSurfaceGeometry.HourHeight(0).Should().Be(52);
    }
}

public sealed class WinoIntelligenceHeaderViewStateTests
{
    private static WinoIntelligenceHeaderState State(string key = "a") => WinoIntelligenceHeaderState.Empty with
    {
        ContentKey = key,
        IsSummaryAvailable = true,
        IsTranslateAvailable = true,
        Languages = new WinoIntelligenceLanguageOption[] { new(string.Empty, "Detect"), new("en", "English"), new("tr", "Turkish") },
        SelectedTargetLanguage = "en"
    };

    [Fact]
    public void SummaryClickOpensThePanelAndRequestsOnce()
    {
        var view = new WinoIntelligenceHeaderViewState();
        view.Update(State());

        view.ClickSummary().Should().BeTrue();
        view.IsExpanded.Should().BeTrue();
        view.OpenPanel.Should().Be(WinoIntelligenceHeaderPanel.Summary);

        view.Update(view.State with { SummaryState = WinoIntelligenceFeatureState.Busy });
        view.IsSummaryWaitingShown.Should().BeTrue();
        view.CanRegenerateSummary.Should().BeFalse();
        view.ClickSummary().Should().BeFalse();
        view.CancelSummary().Should().BeTrue();
        view.OpenPanel.Should().Be(WinoIntelligenceHeaderPanel.None);
    }

    [Fact]
    public void NewContentCollapsesAndUnavailablePanelsClose()
    {
        var view = new WinoIntelligenceHeaderViewState();
        view.Update(State());
        view.Open(WinoIntelligenceHeaderPanel.Translate);
        view.IsTranslatePanelOpen.Should().BeTrue();

        view.Update(State() with { IsTranslateAvailable = false });
        view.OpenPanel.Should().Be(WinoIntelligenceHeaderPanel.None);

        view.Open(WinoIntelligenceHeaderPanel.Summary);
        view.Update(State("b"));
        view.IsExpanded.Should().BeFalse();
        view.OpenPanel.Should().Be(WinoIntelligenceHeaderPanel.None);
    }

    [Fact]
    public void TranslationRulesFollowTheState()
    {
        var view = new WinoIntelligenceHeaderViewState();
        var (_, languagesChanged) = view.Update(State());
        languagesChanged.Should().BeTrue();
        view.SourceOptions.Should().HaveCount(3);
        view.TargetOptions.Select(option => option.Code).Should().Equal("en", "tr");
        view.TargetLanguageLabel.Should().Be("English");
        WinoIntelligenceHeaderViewState.LanguageIndex(view.TargetOptions, "TR").Should().Be(1);

        view.TranslateRunKind.Should().Be(WinoIntelligenceTranslateRunKind.Translate);
        view.Update(view.State with { SelectedSourceLanguage = "en" });
        view.IsTranslateRunEnabled.Should().BeFalse();
        view.Update(view.State with { SelectedSourceLanguage = "tr", HasTranslationResult = true, IsTranslationApplied = true });
        view.IsTranslateRunEnabled.Should().BeTrue();
        view.TranslateRunKind.Should().Be(WinoIntelligenceTranslateRunKind.ShowOriginal);

        view.Update(view.State with { IsTranslationBusy = true });
        view.CanRequestTranslation.Should().BeFalse();
        view.CancelTranslation().Should().BeTrue();
    }

    [Fact]
    public void NothingToExpandKeepsTheHeaderCollapsed()
    {
        var view = new WinoIntelligenceHeaderViewState();
        view.Update(WinoIntelligenceHeaderState.Empty with { ContentKey = "x" });
        view.ToggleExpanded().Should().BeFalse();
        view.SetExpanded(true);
        view.IsExpanded.Should().BeFalse();
    }
}

public sealed class PresentationControlRulesTests
{
    [Fact]
    public void ModeSegmentsKeepTheDesktopOrderAndTheAppModesDropSettings()
    {
        WinoModeSegments.All.Select(segment => segment.Mode).Should().Equal(
            WinoApplicationMode.Mail, WinoApplicationMode.Calendar, WinoApplicationMode.Contacts, WinoApplicationMode.Tasks, WinoApplicationMode.Settings);
        WinoModeSegments.AppModes.Should().HaveCount(4).And.NotContain(segment => segment.Mode == WinoApplicationMode.Settings);
        WinoModeSegments.All[0].Filled.Should().Be(WinoIconGlyph.MailFilled);
    }

    [Theory]
    [InlineData(AccountIconProvider.Microsoft, false, WinoIconGlyph.Microsoft)]
    [InlineData(AccountIconProvider.Microsoft, true, WinoIconGlyph.MicrosoftMono)]
    [InlineData(AccountIconProvider.Google, true, WinoIconGlyph.GoogleMono)]
    [InlineData(AccountIconProvider.ICloud, true, WinoIconGlyph.Apple)]
    [InlineData(AccountIconProvider.Imap, false, WinoIconGlyph.IMAP)]
    public void ProviderGlyphsMatchWindows(AccountIconProvider provider, bool tinted, WinoIconGlyph expected)
        => WinoAccountIconGlyphs.ProviderGlyph(provider, tinted).Should().Be(expected);

    [Fact]
    public void CalendarTilesUseTheWindowsStripeAndOpacity()
    {
        WinoCalendarTileAppearance.StripeColor(CalendarItemShowAs.Free).Should().Be(0x4CAF50u);
        WinoCalendarTileAppearance.StripeColor(CalendarItemShowAs.Busy).Should().Be(0xF44336u);
        WinoCalendarTileAppearance.FillOpacity(CalendarPlacementKind.MultiDayGhost, false, true, true).Should().Be(0.2);
        WinoCalendarTileAppearance.FillOpacity(CalendarPlacementKind.Timed, true, false, false).Should().Be(0.55);
        WinoCalendarTileAppearance.FillOpacity(CalendarPlacementKind.MonthCell, false, true, true).Should().Be(1);
        WinoCalendarTileAppearance.FillOpacity(CalendarPlacementKind.Timed, false, false, true).Should().BeApproximately(0.98, 1e-9);
        WinoCalendarTileAppearance.GlyphColumnWidth(true, true).Should().Be(34);
        WinoCalendarTileAppearance.TitleFontSize(CalendarPlacementKind.MonthCell, false, 20).Should().Be(11);
        WinoCalendarTileAppearance.TitleFontSize(CalendarPlacementKind.Timed, false, 60).Should().Be(13);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("IsFavorite", true)]
    [InlineData("Categories", true)]
    [InlineData("Phone", false)]
    public void ContactRowsRefreshOnTheShownProperties(string? property, bool expected)
        => WinoContactRowRules.RefreshesRow(property).Should().Be(expected);
}
