#nullable enable
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One folder's row in the coverage list: what its rule is, and what that rule costs.
/// </summary>
/// <remarks>
/// The row is display-only. Editing happens in the coverage dialog, because a rule editor needs
/// more width than a settings row can offer. Every count here is computed from the loaded
/// inventory, never queried.
/// </remarks>
public partial class IntelligenceFolderCoverageItem : ObservableObject
{
    public IntelligenceFolderCoverageItem(string remoteFolderId, string displayName, SemanticIndexFolderCoverageRule rule)
    {
        RemoteFolderId = remoteFolderId;
        DisplayName = displayName;
        Rule = rule;
    }

    public string RemoteFolderId { get; }
    public string DisplayName { get; }

    /// <summary>The stored rule. Replaced wholesale when the dialog is accepted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mode))]
    [NotifyPropertyChangedFor(nameof(DatePreset))]
    [NotifyPropertyChangedFor(nameof(LatestMessageCount))]
    public partial SemanticIndexFolderCoverageRule Rule { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableSummary))]
    public partial int AvailableMessageCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoveragePercentage))]
    [NotifyPropertyChangedFor(nameof(CoveragePercentageText))]
    [NotifyPropertyChangedFor(nameof(RowAutomationName))]
    public partial int SelectedMessageCount { get; set; }

    [ObservableProperty] public partial int MissingMessageCount { get; set; }

    /// <summary>The rule as a short phrase, shown on the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowAutomationName))]
    public partial string SelectedSummary { get; set; } = string.Empty;

    /// <summary>
    /// True when this folder deviates from the account default. Only exceptions are worth marking,
    /// so most rows carry no badge at all.
    /// </summary>
    [ObservableProperty] public partial bool IsOverride { get; set; }

    public SemanticIndexCoverageMode Mode => Rule.Mode;
    public SemanticIndexRangePreset DatePreset => Rule.DatePreset;
    public int LatestMessageCount => Rule.LatestMessageCount;

    public string AvailableSummary => string.Format(Translator.SemanticIndex_CoverageAvailableMessages, AvailableMessageCount);

    public double CoveragePercentage => AvailableMessageCount == 0
        ? 0
        : Math.Round(SelectedMessageCount * 100d / AvailableMessageCount);

    public string CoveragePercentageText => $"{CoveragePercentage:0}%";

    /// <summary>
    /// The row conveys its state through a chip, a number and a bar. Screen readers get the same
    /// facts as one sentence, so none of it depends on seeing the bar.
    /// </summary>
    public string RowAutomationName => string.Format(
        Translator.SemanticIndex_CoverageFolderRowName,
        DisplayName, SelectedSummary, SelectedMessageCount, AvailableMessageCount);
}

/// <summary>
/// One column of a folder's message-volume histogram. Bars are measured in pixels because they
/// live in an ItemsRepeater, which has no shared measuring pass to scale them.
/// </summary>
public partial class IntelligenceCoverageBucketItem : ObservableObject
{
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public required int MessageCount { get; init; }

    /// <summary>
    /// The span of positions this column covers in the newest-first message list, so marking the
    /// covered part of the chart is a comparison rather than a second pass over the messages.
    /// </summary>
    public required int StartPosition { get; init; }
    public required int EndPosition { get; init; }

    public required double BarHeight { get; init; }
    public required double BarWidth { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionOpacity))]
    public partial bool IsCovered { get; set; }

    public double SelectionOpacity => IsCovered ? 1d : 0.32d;

    public string Tooltip => string.Format(
        Translator.SemanticIndex_CoverageBucketVolumeTooltip,
        StartDate.ToString("d MMM yyyy"),
        EndDate.ToString("d MMM yyyy"),
        MessageCount);
}
