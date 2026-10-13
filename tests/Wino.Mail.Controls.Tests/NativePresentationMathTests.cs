using FluentAssertions;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Xunit;

namespace Wino.Mail.Controls.Tests;

public sealed class WinoMailRowMetricsTests
{
    // A fixed line height keeps the expectations independent of any platform font.
    private static double Line(double pointSize) => pointSize + 3;

    [Theory]
    [InlineData(WinoMailRowDensity.Compact, 56)]
    [InlineData(WinoMailRowDensity.Medium, 76)]
    [InlineData(WinoMailRowDensity.Spacious, 88)]
    public void RowsWithoutTilesUseTheTemplateMinimumHeight(WinoMailRowDensity density, double expected)
        => WinoMailRowMetrics.HeightFor(density, 0, Line).Should().Be(expected);

    [Fact]
    public void WrappedTileLinesGrowTheRowPastTheMinimum()
    {
        // Content: 16 + 2 + 16 + 2 + 15 + (2 + 2 + 2 * 18 + 4) = 95; chrome: 2 * (7 + 2) = 18.
        WinoMailRowMetrics.HeightFor(WinoMailRowDensity.Medium, 2, Line).Should().Be(113);

        // Compact rows never show the tile line.
        WinoMailRowMetrics.HeightFor(WinoMailRowDensity.Compact, 3, Line).Should().Be(56);
    }

    [Fact]
    public void TileLinesWrapLikeTheFlowViewAndCapAtSixTiles()
    {
        WinoMailRowMetrics.TileLines(Array.Empty<double>(), 200).Should().Be(0);
        WinoMailRowMetrics.TileLines([50, 50], 0).Should().Be(1);
        WinoMailRowMetrics.TileLines([60, 60, 60], 130).Should().Be(2);
        WinoMailRowMetrics.TileLines([300], 100).Should().Be(1);
        WinoMailRowMetrics.TileLines([100, 100, 100, 100, 100, 100, 100, 100], 100).Should().Be(6);
    }

    [Fact]
    public void TextWidthSubtractsIndentAvatarAndCheckbox()
    {
        double plain = WinoMailRowMetrics.TextWidth(400, WinoMailRowDensity.Medium, WinoMailRowKind.Single, false, false);
        plain.Should().Be(400 - 4 - 4 - 12 - 10 - 1);

        WinoMailRowMetrics.TextWidth(400, WinoMailRowDensity.Medium, WinoMailRowKind.ThreadChild, true, true)
            .Should().Be(plain - 20 - (30 + 8) - (16 + 6));
        WinoMailRowMetrics.GroupRowHeight(13.2).Should().Be(34);
    }
}

public sealed class WinoMiniCalendarGridTests
{
    private static readonly DateOnly October = new(2026, 10, 1);

    [Fact]
    public void GridStartsOnTheConfiguredFirstDayOfWeek()
    {
        WinoMiniCalendarGrid.GridStart(new DateOnly(2026, 10, 14), DayOfWeek.Monday).Should().Be(new DateOnly(2026, 9, 28));
        WinoMiniCalendarGrid.GridStart(October, DayOfWeek.Thursday).Should().Be(October);
        WinoMiniCalendarGrid.GridDates(October, DayOfWeek.Sunday).Should().HaveCount(42).And.StartWith(new DateOnly(2026, 9, 27));
        WinoMiniCalendarGrid.DayNameIndex(0, DayOfWeek.Monday).Should().Be(1);
        WinoMiniCalendarGrid.DayNameIndex(6, DayOfWeek.Monday).Should().Be(0);
    }

    [Fact]
    public void HeightAndHitTestingMatchTheDrawnGrid()
    {
        WinoMiniCalendarGrid.HeightFor(collapsed: true).Should().Be(42);
        WinoMiniCalendarGrid.HeightFor(collapsed: false).Should().Be(10 + 22 + 6 + 16 + 6 * 24 + 10);

        // 160 wide: 20pt columns starting at x = 10, rows starting at y = 54.
        WinoMiniCalendarGrid.HitTest(15, 60, 160).Should().Be(0);
        WinoMiniCalendarGrid.HitTest(145, 54 + 24 * 5 + 1, 160).Should().Be(41);
        WinoMiniCalendarGrid.HitTest(15, 40, 160).Should().Be(-1);
        WinoMiniCalendarGrid.CellBounds(8, 160).Should().Be((30d, 78d, 20d, 24d));
    }

    [Fact]
    public void FocusAndSelectionRunsFollowTheMonth()
    {
        WinoMiniCalendarGrid.KeepFocusInMonth(new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 1)).Should().Be(new DateOnly(2026, 2, 28));
        WinoMiniCalendarGrid.KeepFocusInMonth(new DateOnly(2026, 2, 10), new DateOnly(2026, 2, 1)).Should().Be(new DateOnly(2026, 2, 10));
        WinoMiniCalendarGrid.CellIndex(new DateOnly(2026, 10, 14), October, DayOfWeek.Monday).Should().Be(16);
        WinoMiniCalendarGrid.CellIndex(new DateOnly(2026, 12, 1), October, DayOfWeek.Monday).Should().Be(-1);

        var selected = new HashSet<DateOnly> { new(2026, 10, 13), new(2026, 10, 14), new(2026, 10, 15) };
        WinoMiniCalendarGrid.SelectionEnds(selected, new DateOnly(2026, 10, 13), 1).Should().Be((true, false));
        WinoMiniCalendarGrid.SelectionEnds(selected, new DateOnly(2026, 10, 14), 2).Should().Be((false, false));
        WinoMiniCalendarGrid.SelectionEnds(selected, new DateOnly(2026, 10, 15), 6).Should().Be((false, true));
    }
}

public sealed class ContactPictureAppearanceTests
{
    [Fact]
    public void PaletteIndexIsStableCaseInsensitiveAndFallsBackToGrey()
    {
        ContactPictureAppearance.Palette.Should().HaveCount(16);
        ContactPictureAppearance.PaletteIndex(null).Should().Be(14);
        ContactPictureAppearance.PaletteIndex("").Should().Be(14);
        ContactPictureAppearance.PaletteIndex("Ayse@Example.com").Should().Be(ContactPictureAppearance.PaletteIndex("ayse@example.com"));

        // 17 * 31 + 'a' = 624; 624 % 16 = 0.
        ContactPictureAppearance.PaletteIndex("a").Should().Be(0);
        ContactPictureAppearance.PaletteColorHex("a").Should().Be("#E74C3C");
    }

    [Theory]
    [InlineData("Burak Kaan Köse", "BK")]
    [InlineData("ayse.yilmaz@example.com", "AY")]
    [InlineData("x", "X")]
    [InlineData("support", "SU")]
    [InlineData("  ", "?")]
    [InlineData("@example.com", "@C")]
    public void InitialsUseFirstAndLastParts(string name, string expected)
        => ContactPictureAppearance.Initials(name).Should().Be(expected);
}

public sealed class WinoIntelligenceHeaderStateTests
{
    [Fact]
    public void EmptyStateCannotExpandAndHasNoInsights()
    {
        var state = WinoIntelligenceHeaderState.Empty;

        state.CanExpand.Should().BeFalse();
        state.HasInsights.Should().BeFalse();
        state.Tiles.Should().BeEmpty();
        (state with { IsProcessingAvailable = true }).CanRequestProcessing.Should().BeTrue();
    }
}
