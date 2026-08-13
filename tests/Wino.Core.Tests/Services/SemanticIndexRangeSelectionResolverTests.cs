using System;
using System.Collections.Generic;
using Wino.Core.Domain.Models.SemanticIndexing;
using Xunit;

namespace Wino.Core.Tests.Services;

public class SemanticIndexRangeSelectionResolverTests
{
    private static readonly DateOnly Oldest = new(2024, 3, 2);
    private static readonly DateOnly Newest = new(2026, 8, 12);

    private static SemanticIndexAvailableRange CreateRange(DateOnly? oldest = null, DateOnly? newest = null)
    {
        var from = oldest ?? Oldest;
        var to = newest ?? Newest;
        var counts = new Dictionary<DateOnly, int> { [from] = 1, [to] = 1 };
        return new SemanticIndexAvailableRange(from, to, counts);
    }

    [Fact]
    public void Resolve_WithoutStoredPreset_DefaultsToOneMonth()
    {
        var range = CreateRange();

        var selection = SemanticIndexRangeSelectionResolver.Resolve(range, null, null, null);

        Assert.Equal(range.DaySpan, selection.EndOffset);
        Assert.Equal(range.DaySpan - 30, selection.StartOffset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithBlankStoredPreset_DefaultsToOneMonth(string storedPresetId)
    {
        var range = CreateRange();

        var selection = SemanticIndexRangeSelectionResolver.Resolve(range, storedPresetId, null, null);

        Assert.Equal(range.DaySpan - 30, selection.StartOffset);
    }

    [Theory]
    [InlineData("one-week", 7)]
    [InlineData("one-month", 30)]
    [InlineData("three-months", 91)]
    [InlineData("six-months", 182)]
    [InlineData("one-year", 365)]
    public void Resolve_WithStoredPreset_CountsBackFromNewestMessage(string presetId, int expectedDays)
    {
        var range = CreateRange();

        var selection = SemanticIndexRangeSelectionResolver.Resolve(range, presetId, null, null);

        Assert.Equal(range.DaySpan, selection.EndOffset);
        Assert.Equal(range.DaySpan - expectedDays, selection.StartOffset);
    }

    [Fact]
    public void Resolve_WithEverything_SpansTheWholeRange()
    {
        var range = CreateRange();

        var selection = SemanticIndexRangeSelectionResolver.Resolve(range, "everything", null, null);

        Assert.Equal(0, selection.StartOffset);
        Assert.Equal(range.DaySpan, selection.EndOffset);
    }

    [Fact]
    public void Resolve_WithOnlyNew_SelectsTheNewestDayAlone()
    {
        var range = CreateRange();

        var selection = SemanticIndexRangeSelectionResolver.Resolve(range, "only-new", null, null);

        Assert.Equal(range.DaySpan, selection.StartOffset);
        Assert.Equal(range.DaySpan, selection.EndOffset);
    }

    [Fact]
    public void Resolve_WhenPresetReachesPastOldestMessage_ClampsToTheStart()
    {
        // Only ten days of mail, but a one year preset was stored.
        var range = CreateRange(newest: Oldest.AddDays(10));

        var selection = SemanticIndexRangeSelectionResolver.Resolve(range, "one-year", null, null);

        Assert.Equal(0, selection.StartOffset);
        Assert.Equal(range.DaySpan, selection.EndOffset);
    }

    [Fact]
    public void Resolve_WithCustomRange_RestoresTheStoredDates()
    {
        var range = CreateRange();
        var cutoff = Oldest.AddDays(100).ToDateTime(TimeOnly.MinValue);
        var through = Oldest.AddDays(400).ToDateTime(TimeOnly.MinValue);

        var selection = SemanticIndexRangeSelectionResolver.Resolve(range, "custom", cutoff, through);

        Assert.Equal(100, selection.StartOffset);
        Assert.Equal(400, selection.EndOffset);
    }

    [Fact]
    public void Resolve_WithCustomRangeOlderThanAvailableMail_ClampsIntoRange()
    {
        var range = CreateRange();
        var cutoff = Oldest.AddDays(-500).ToDateTime(TimeOnly.MinValue);
        var through = Newest.AddDays(500).ToDateTime(TimeOnly.MinValue);

        var selection = SemanticIndexRangeSelectionResolver.Resolve(range, "custom", cutoff, through);

        Assert.Equal(0, selection.StartOffset);
        Assert.Equal(range.DaySpan, selection.EndOffset);
    }

    [Fact]
    public void Resolve_WithCustomRangeButNoStoredDates_FallsBackToTheDefault()
    {
        var range = CreateRange();

        var selection = SemanticIndexRangeSelectionResolver.Resolve(range, "custom", null, null);

        Assert.Equal(range.DaySpan - 30, selection.StartOffset);
        Assert.Equal(range.DaySpan, selection.EndOffset);
    }

    [Fact]
    public void Resolve_WithInvertedCustomDates_KeepsEndAtOrAfterStart()
    {
        var range = CreateRange();
        var cutoff = Oldest.AddDays(400).ToDateTime(TimeOnly.MinValue);
        var through = Oldest.AddDays(100).ToDateTime(TimeOnly.MinValue);

        var selection = SemanticIndexRangeSelectionResolver.Resolve(range, "custom", cutoff, through);

        Assert.Equal(400, selection.StartOffset);
        Assert.Equal(400, selection.EndOffset);
    }

    [Fact]
    public void Resolve_WithUnknownPresetId_DefaultsToOnlyNew()
    {
        // FromStableId maps anything unrecognised to OnlyNew, which must stay a safe
        // choice: it never schedules a large backfill.
        var range = CreateRange();

        var selection = SemanticIndexRangeSelectionResolver.Resolve(range, "not-a-preset", null, null);

        Assert.Equal(range.DaySpan, selection.StartOffset);
        Assert.Equal(range.DaySpan, selection.EndOffset);
    }
}
