using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Mail.ViewModels.Data;

public partial class CoverageDatePresetOption(
    SemanticIndexRangePreset preset,
    string displayName) : ObservableObject
{
    public SemanticIndexRangePreset Preset { get; } = preset;
    public string DisplayName { get; } = displayName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial int? MessageCount { get; set; }

    public string Label => MessageCount is { } count
        ? string.Format(Translator.SemanticIndex_CoveragePresetWithCount, DisplayName, count)
        : DisplayName;

    public static IReadOnlyList<CoverageDatePresetOption> All =>
    (CoverageDatePresetOption[])
    [
        new(SemanticIndexRangePreset.OnlyNew, Translator.SemanticIndex_RangeOnlyNew),
        new(SemanticIndexRangePreset.OneWeek, Translator.SemanticIndex_CoveragePeriodOneWeek),
        new(SemanticIndexRangePreset.OneMonth, Translator.SemanticIndex_CoveragePeriodOneMonth),
        new(SemanticIndexRangePreset.ThreeMonths, Translator.SemanticIndex_CoveragePeriodThreeMonths),
        new(SemanticIndexRangePreset.SixMonths, Translator.SemanticIndex_CoveragePeriodSixMonths),
        new(SemanticIndexRangePreset.OneYear, Translator.SemanticIndex_CoveragePeriodOneYear),
        new(SemanticIndexRangePreset.Everything, Translator.SemanticIndex_RangeEverything),
        new(SemanticIndexRangePreset.Custom, Translator.SemanticIndex_CoverageCustomRange),
    ];
}

public sealed class CoverageCountPresetOption(
    int count,
    string displayName,
    bool isCustom = false)
{
    public int Count { get; } = count;
    public string DisplayName { get; } = displayName;
    public bool IsCustom { get; } = isCustom;

    public static CoverageCountPresetOption Custom(string displayName)
        => new(0, displayName, true);
}
