using System;

namespace Wino.Core.Domain.Models.SemanticIndexing;

public enum SemanticIndexRangePreset
{
    OnlyNew,
    OneWeek,
    OneMonth,
    ThreeMonths,
    SixMonths,
    OneYear,
    Everything,
    Custom,
}

public static class SemanticIndexRangePresetExtensions
{
    public static string ToStableId(this SemanticIndexRangePreset preset) => preset switch
    {
        SemanticIndexRangePreset.OnlyNew => "only-new",
        SemanticIndexRangePreset.OneWeek => "one-week",
        SemanticIndexRangePreset.OneMonth => "one-month",
        SemanticIndexRangePreset.ThreeMonths => "three-months",
        SemanticIndexRangePreset.SixMonths => "six-months",
        SemanticIndexRangePreset.OneYear => "one-year",
        SemanticIndexRangePreset.Everything => "everything",
        SemanticIndexRangePreset.Custom => "custom",
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    public static SemanticIndexRangePreset FromStableId(string? value) => value switch
    {
        "one-week" => SemanticIndexRangePreset.OneWeek,
        "one-month" => SemanticIndexRangePreset.OneMonth,
        "three-months" => SemanticIndexRangePreset.ThreeMonths,
        "six-months" => SemanticIndexRangePreset.SixMonths,
        "one-year" => SemanticIndexRangePreset.OneYear,
        "everything" => SemanticIndexRangePreset.Everything,
        "custom" => SemanticIndexRangePreset.Custom,
        _ => SemanticIndexRangePreset.OnlyNew,
    };

    public static DateTimeOffset? CreateCutoff(this SemanticIndexRangePreset preset, DateTimeOffset now) => preset switch
    {
        SemanticIndexRangePreset.OnlyNew => now,
        SemanticIndexRangePreset.OneWeek => now.AddDays(-7),
        SemanticIndexRangePreset.OneMonth => now.AddMonths(-1),
        SemanticIndexRangePreset.ThreeMonths => now.AddMonths(-3),
        SemanticIndexRangePreset.SixMonths => now.AddMonths(-6),
        SemanticIndexRangePreset.OneYear => now.AddYears(-1),
        SemanticIndexRangePreset.Everything => null,
        SemanticIndexRangePreset.Custom => throw new InvalidOperationException("A custom range requires an explicit cutoff."),
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };
}
