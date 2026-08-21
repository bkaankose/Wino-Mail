#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// The draft rule behind the coverage dialog, and every number that dialog shows.
/// </summary>
/// <remarks>
/// <para>
/// The editor lives in a dialog rather than on the page because a rule is a small form: nested
/// inside a settings expander it never had the width for a period control, a count control and a
/// result at once.
/// </para>
/// <para>
/// It edits a copy. Nothing reaches the account until the dialog is accepted, so cancelling is
/// free. Every count is computed from the already-loaded inventory, so typing in the number box
/// costs no query.
/// </para>
/// </remarks>
public partial class IntelligenceCoverageRuleEditor : ObservableObject
{
    /// <summary>Design width of the dialog histogram, in effective pixels.</summary>
    private const double HistogramWidth = 452;
    private const double MaximumBarHeight = 40;
    private const int HistogramBucketCount = 56;

    private static readonly int[] CountPresetValues = [50, 100, 250, 500, 1000];

    private readonly IntelligenceCoverageInventory _inventory;
    private readonly IReadOnlySet<string> _folderIds;
    private readonly long[] _ticks;
    private bool _isApplying;

    /// <param name="remoteFolderId">The folder being edited, or null when editing the account default.</param>
    /// <param name="folderIds">Folders the counts are measured over: one folder, or every included folder.</param>
    public IntelligenceCoverageRuleEditor(
        IntelligenceCoverageInventory inventory,
        string? remoteFolderId,
        IReadOnlySet<string> folderIds,
        SemanticIndexFolderCoverageRule rule,
        string title,
        bool canApplyToAllFolders)
    {
        _inventory = inventory;
        _folderIds = folderIds;
        _ticks = IntelligenceCoverageCalculator.GetOrderedTicks(inventory, folderIds);

        RemoteFolderId = remoteFolderId;
        Title = title;
        CanApplyToAllFolders = canApplyToAllFolders;
        AvailableMessageCount = _ticks.Length;

        foreach (var preset in CoverageDatePresetOption.All)
        {
            preset.MessageCount = preset.Preset == SemanticIndexRangePreset.Custom
                ? null
                : CountForPreset(preset.Preset);
            DatePresets.Add(preset);
        }
        RebuildCountPresets();

        ApplyRule(rule);
        BuildHistogram();
        Recalculate();
    }

    /// <summary>Null when this edits the account-level default rather than one folder.</summary>
    public string? RemoteFolderId { get; }

    public string Title { get; }

    public bool CanApplyToAllFolders { get; }

    public int AvailableMessageCount { get; }

    public ObservableCollection<CoverageDatePresetOption> DatePresets { get; } = [];
    public ObservableCollection<CoverageCountPresetOption> CountPresets { get; } = [];
    public ObservableCollection<IntelligenceCoverageBucketItem> Buckets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDateCoverageMode))]
    [NotifyPropertyChangedFor(nameof(IsLatestCountCoverageMode))]
    public partial int CoverageModeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomDateRange))]
    public partial CoverageDatePresetOption? SelectedDatePreset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomCount))]
    public partial CoverageCountPresetOption? SelectedCountPreset { get; set; }

    [ObservableProperty] public partial DateTimeOffset? CutoffUtc { get; set; }
    [ObservableProperty] public partial DateTimeOffset? ThroughUtcExclusive { get; set; }
    [ObservableProperty] public partial double CustomCount { get; set; }

    [ObservableProperty] public partial int SelectedMessageCount { get; set; }
    [ObservableProperty] public partial string ResultDetail { get; set; } = string.Empty;
    [ObservableProperty] public partial string OldestDateText { get; set; } = string.Empty;
    [ObservableProperty] public partial string MiddleDateText { get; set; } = string.Empty;

    /// <summary>Set by the dialog's checkbox. Applies the finished rule to every included folder.</summary>
    [ObservableProperty] public partial bool ApplyToAllFolders { get; set; }

    public bool IsDateCoverageMode => CoverageModeIndex == 0;
    public bool IsLatestCountCoverageMode => CoverageModeIndex == 1;
    public bool IsCustomDateRange => SelectedDatePreset?.Preset == SemanticIndexRangePreset.Custom;
    public bool IsCustomCount => SelectedCountPreset?.IsCustom == true;

    public string Subtitle => string.Format(Translator.SemanticIndex_CoverageAvailableMessages, AvailableMessageCount);

    public double CustomCountMaximum => Math.Max(1, AvailableMessageCount);

    /// <summary>The edited rule, ready to store against a folder.</summary>
    public SemanticIndexFolderCoverageRule ToRule(string remoteFolderId)
    {
        var mode = IsLatestCountCoverageMode ? SemanticIndexCoverageMode.LatestCount : SemanticIndexCoverageMode.DateRange;
        var preset = SelectedDatePreset?.Preset ?? SemanticIndexRangePreset.Everything;
        var isCustom = preset == SemanticIndexRangePreset.Custom;
        return new SemanticIndexFolderCoverageRule(
            remoteFolderId,
            mode,
            preset,
            isCustom ? CutoffUtc : null,
            isCustom ? ThroughUtcExclusive : null,
            ResolveCount());
    }

    /// <summary>What a period would select across the folders this editor covers.</summary>
    private int CountForPreset(SemanticIndexRangePreset preset)
    {
        var rules = _folderIds
            .Select(folderId => SemanticIndexFolderCoverageRule.DateRange(folderId, preset, null, null))
            .ToArray();
        return IntelligenceCoverageCalculator.Resolve(_inventory, rules, DateTimeOffset.UtcNow).DistinctSelectedCount;
    }

    private int ResolveCount()
        => SelectedCountPreset is null || SelectedCountPreset.IsCustom
            ? (int)Math.Clamp(Math.Round(CustomCount), 0, AvailableMessageCount)
            : Math.Min(SelectedCountPreset.Count, AvailableMessageCount);

    private void ApplyRule(SemanticIndexFolderCoverageRule rule)
    {
        _isApplying = true;
        try
        {
            CoverageModeIndex = rule.Mode == SemanticIndexCoverageMode.LatestCount ? 1 : 0;
            SelectedDatePreset = DatePresets.FirstOrDefault(preset => preset.Preset == rule.DatePreset)
                ?? DatePresets.FirstOrDefault(preset => preset.Preset == SemanticIndexRangePreset.OneMonth);
            CutoffUtc = rule.CutoffUtc;
            ThroughUtcExclusive = rule.ThroughUtcExclusive;

            var count = Math.Clamp(rule.LatestMessageCount, 0, Math.Max(0, AvailableMessageCount));
            CustomCount = count;
            SelectedCountPreset = CountPresets.FirstOrDefault(preset => !preset.IsCustom && preset.Count == count)
                ?? CountPresets.FirstOrDefault(preset => preset.IsCustom);
        }
        finally
        {
            _isApplying = false;
        }
    }

    /// <summary>
    /// Offers only shortcuts that fit the folder, plus "all" and a custom entry. A "1,000 newest"
    /// option in a folder holding 3 messages is noise.
    /// </summary>
    private void RebuildCountPresets()
    {
        CountPresets.Clear();
        foreach (var count in CountPresetValues.Where(count => count < AvailableMessageCount))
            CountPresets.Add(new CoverageCountPresetOption(count, string.Format(Translator.SemanticIndex_CoverageCountNewest, count)));

        if (AvailableMessageCount > 0)
        {
            CountPresets.Add(new CoverageCountPresetOption(
                AvailableMessageCount, string.Format(Translator.SemanticIndex_CoverageCountAll, AvailableMessageCount)));
        }
        CountPresets.Add(CoverageCountPresetOption.Custom(Translator.SemanticIndex_CoverageCustom));
    }

    partial void OnCoverageModeIndexChanged(int value) => Recalculate();
    partial void OnSelectedDatePresetChanged(CoverageDatePresetOption? value) => Recalculate();
    partial void OnSelectedCountPresetChanged(CoverageCountPresetOption? value)
    {
        if (value is { IsCustom: false })
            CustomCount = value.Count;
        Recalculate();
    }
    partial void OnCustomCountChanged(double value) => Recalculate();
    partial void OnCutoffUtcChanged(DateTimeOffset? value) => Recalculate();
    partial void OnThroughUtcExclusiveChanged(DateTimeOffset? value) => Recalculate();

    /// <summary>
    /// Recomputes what the draft selects. Pure arithmetic over the loaded inventory, so it runs
    /// inline on every keystroke.
    /// </summary>
    private void Recalculate()
    {
        if (_isApplying)
            return;

        var now = DateTimeOffset.UtcNow;
        var rules = _folderIds
            .Select(folderId => ToRule(folderId))
            .ToArray();
        var selection = IntelligenceCoverageCalculator.Resolve(_inventory, rules, now);

        SelectedMessageCount = selection.DistinctSelectedCount;

        // The oldest message any folder reaches is how far back the rule really goes.
        DateOnly? reach = null;
        foreach (var folder in selection.Folders)
        {
            if (folder.ReachDate is { } date && (reach is null || date < reach))
                reach = date;
        }

        ResultDetail = SelectedMessageCount == 0
            ? Translator.SemanticIndex_CoverageNothingSelected
            : reach is { } oldest
                ? string.Format(Translator.SemanticIndex_CoverageResultDetail, AvailableMessageCount, oldest.ToString("d MMMM yyyy"))
                : string.Format(Translator.SemanticIndex_CoverageResultDetailNoReach, AvailableMessageCount);

        UpdateHistogramCoverage(selection);
    }

    private void BuildHistogram()
    {
        Buckets.Clear();
        if (_ticks.Length == 0)
            return;

        var newest = ToDateOnly(_ticks[0]);
        var oldest = ToDateOnly(_ticks[^1]);
        var daySpan = Math.Max(0, newest.DayNumber - oldest.DayNumber);
        var daysPerBucket = Math.Max(1, (int)Math.Ceiling((daySpan + 1) / (double)HistogramBucketCount));
        var bucketCount = (daySpan / daysPerBucket) + 1;

        var counts = new int[bucketCount];
        var firstPosition = new int[bucketCount];
        var lastPosition = new int[bucketCount];
        Array.Fill(firstPosition, -1);

        for (var position = 0; position < _ticks.Length; position++)
        {
            var day = ToDateOnly(_ticks[position]);
            var bucket = Math.Clamp((newest.DayNumber - day.DayNumber) / daysPerBucket, 0, bucketCount - 1);
            counts[bucket]++;
            if (firstPosition[bucket] < 0)
                firstPosition[bucket] = position;
            lastPosition[bucket] = position;
        }

        var busiest = counts.Max();
        var barWidth = Math.Clamp(HistogramWidth / bucketCount, 3, 10);

        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var endDate = newest.AddDays(-bucket * daysPerBucket);
            var startDate = newest.AddDays(-Math.Min(daySpan, ((bucket + 1) * daysPerBucket) - 1));
            Buckets.Add(new IntelligenceCoverageBucketItem
            {
                StartDate = startDate,
                EndDate = endDate,
                MessageCount = counts[bucket],
                StartPosition = firstPosition[bucket] < 0 ? int.MaxValue : firstPosition[bucket],
                EndPosition = firstPosition[bucket] < 0 ? -1 : lastPosition[bucket],
                BarHeight = counts[bucket] == 0
                    ? 2
                    : Math.Max(2, counts[bucket] / (double)busiest * MaximumBarHeight),
                BarWidth = barWidth,
            });
        }

        OldestDateText = oldest.ToString("MMM yyyy");
        MiddleDateText = newest.AddDays(-daySpan / 2).ToString("MMM yyyy");
    }

    /// <summary>
    /// Marks the columns the draft covers. The union is ordered newest first, so a selection of
    /// N messages is the first N positions whatever mode produced it.
    /// </summary>
    private void UpdateHistogramCoverage(IntelligenceCoverageSelection selection)
    {
        var covered = selection.DistinctSelectedCount;
        foreach (var bucket in Buckets)
            bucket.IsCovered = bucket.StartPosition < covered;
    }

    private static DateOnly ToDateOnly(long utcTicks)
        => DateOnly.FromDateTime(new DateTime(utcTicks, DateTimeKind.Utc));
}

/// <summary>A period the dialog offers, with the number of messages it would select.</summary>
public partial class CoverageDatePresetOption(SemanticIndexRangePreset preset, string displayName) : ObservableObject
{
    public SemanticIndexRangePreset Preset { get; } = preset;
    public string DisplayName { get; } = displayName;

    /// <summary>How many messages this period selects, filled in per folder once counts are known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial int? MessageCount { get; set; }

    /// <summary>
    /// What the dropdown shows. The count rides along in the label so choosing a period is still
    /// priced, the way the old chip row priced it, without costing three rows of chips.
    /// </summary>
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

/// <summary>A latest-N shortcut the dialog offers.</summary>
public sealed class CoverageCountPresetOption(int count, string displayName, bool isCustom = false)
{
    public int Count { get; } = count;
    public string DisplayName { get; } = displayName;
    public bool IsCustom { get; } = isCustom;

    public static CoverageCountPresetOption Custom(string displayName) => new(0, displayName, true);
}
