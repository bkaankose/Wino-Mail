#nullable enable
using System;

namespace Wino.Core.Domain.Models.SemanticIndexing;

/// <summary>
/// Day offsets into a <see cref="SemanticIndexAvailableRange"/>, measured from its oldest date.
/// </summary>
public readonly record struct SemanticIndexRangeSelection(int StartOffset, int EndOffset);

/// <summary>
/// Turns a stored range choice back into a selection over the mail that is available
/// right now. Kept free of view concerns so the rules can be tested on their own.
/// </summary>
public static class SemanticIndexRangeSelectionResolver
{
    /// <summary>
    /// Range offered to a mailbox that has never had one chosen. A month of mail is
    /// useful straight away without spending the monthly quota on the whole mailbox.
    /// </summary>
    public const SemanticIndexRangePreset DefaultPreset = SemanticIndexRangePreset.OneMonth;

    /// <summary>
    /// Number of days a preset covers, counting back from the newest available message.
    /// </summary>
    public static int GetPresetDays(SemanticIndexRangePreset preset, SemanticIndexAvailableRange availableRange)
        => preset switch
        {
            SemanticIndexRangePreset.OnlyNew => 0,
            SemanticIndexRangePreset.OneWeek => 7,
            SemanticIndexRangePreset.OneMonth => 30,
            SemanticIndexRangePreset.ThreeMonths => 91,
            SemanticIndexRangePreset.SixMonths => 182,
            SemanticIndexRangePreset.OneYear => 365,
            _ => availableRange.DaySpan,
        };

    /// <summary>
    /// Resolves the stored choice. A preset is re-applied against today's newest message
    /// so it keeps meaning "the last month" rather than a frozen pair of dates. A custom
    /// range keeps its dates and is clamped into whatever mail still exists.
    /// </summary>
    public static SemanticIndexRangeSelection Resolve(
        SemanticIndexAvailableRange availableRange,
        string? storedPresetId,
        DateTime? storedCutoffUtc,
        DateTime? storedThroughUtc)
    {
        ArgumentNullException.ThrowIfNull(availableRange);

        var preset = string.IsNullOrWhiteSpace(storedPresetId)
            ? DefaultPreset
            : SemanticIndexRangePresetExtensions.FromStableId(storedPresetId);

        if (preset == SemanticIndexRangePreset.Custom)
        {
            if (storedCutoffUtc is not { } cutoffUtc || storedThroughUtc is not { } throughUtc)
                return FromPreset(DefaultPreset, availableRange);

            var startOffset = Math.Clamp(
                DateOnly.FromDateTime(cutoffUtc).DayNumber - availableRange.OldestDate.DayNumber,
                0,
                availableRange.DaySpan);
            var endOffset = Math.Clamp(
                DateOnly.FromDateTime(throughUtc).DayNumber - availableRange.OldestDate.DayNumber,
                startOffset,
                availableRange.DaySpan);
            return new SemanticIndexRangeSelection(startOffset, endOffset);
        }

        return FromPreset(preset, availableRange);
    }

    private static SemanticIndexRangeSelection FromPreset(
        SemanticIndexRangePreset preset,
        SemanticIndexAvailableRange availableRange)
        => new(
            Math.Max(0, availableRange.DaySpan - GetPresetDays(preset, availableRange)),
            availableRange.DaySpan);
}
