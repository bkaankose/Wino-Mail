#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The coverage editor: which folders intelligence reads, and how far back it reads each of them.
/// </summary>
/// <remarks>
/// Performs no I/O at all. Everything it shows arrives in <see cref="IntelligenceCoverageEditorArgs"/>
/// from the management page, and everything it decides leaves through
/// <see cref="IIntelligenceCoverageHandoff"/> — so opening and closing the editor costs nothing and
/// cannot disagree with the page behind it.
/// </remarks>
public partial class IntelligenceCoveragePageViewModel : MailBaseViewModel
{
    /// <summary>
    /// Columns in the histogram. Enough to show the shape of a mailbox without turning each bar
    /// into a hairline on a narrow window.
    /// </summary>
    private const int BucketCount = 32;

    private const double HistogramBarSpacing = 2;

    /// <summary>
    /// Below this many messages the histogram is hidden rather than drawn.
    /// </summary>
    /// <remarks>
    /// Bars are sized to fill the width of the slider they sit above, so a folder holding two
    /// messages renders two slabs half the panel wide. There is no distribution to show at that
    /// size anyway — the three band counts already say everything a chart could.
    /// </remarks>
    private const int MinimumHistogramMessages = 12;

    private readonly IIntelligenceCoverageHandoff _handoff;
    private IntelligenceCoverageEditorArgs? _args;
    private BitArray? _indexedByIndex;

    /// <summary>
    /// The rule a folder ticked from here starts on. Only "apply to all folders" changes it —
    /// deriving it from whichever folder happened to be selected at Apply time would make the
    /// account-wide default depend on where the user last clicked.
    /// </summary>
    private SemanticIndexFolderCoverageRule? _defaultRule;
    private double _histogramWidth = 560;

    /// <summary>
    /// Suppresses the rule rewrites that a preset or slider change would otherwise trigger while
    /// the editor is loading another folder's rule into those same controls.
    /// </summary>
    private bool _isLoadingFolder;

    public IntelligenceCoveragePageViewModel(IIntelligenceCoverageHandoff handoff)
    {
        _handoff = handoff;
    }

    public ObservableCollection<IntelligenceFolderNode> RootFolders { get; } = [];

    public ObservableCollection<SemanticIndexRangeBucketViewModel> Buckets { get; } = [];

    public IReadOnlyList<CoverageCountPresetOption> CountPresets { get; } =
    (CoverageCountPresetOption[])
    [
        new(100, string.Format(Translator.SemanticIndex_CoverageCountNewest, 100)),
        new(500, string.Format(Translator.SemanticIndex_CoverageCountNewest, 500)),
        new(1000, string.Format(Translator.SemanticIndex_CoverageCountNewest, 1000)),
        new(5000, string.Format(Translator.SemanticIndex_CoverageCountNewest, 5000)),
        new(int.MaxValue, Translator.SemanticIndex_RangeEverything),
    ];

    public IReadOnlyList<CoverageDatePresetOption> DatePresets { get; } =
    (CoverageDatePresetOption[])
    [
        new(SemanticIndexRangePreset.OneWeek, Translator.SemanticIndex_CoveragePeriodOneWeek),
        new(SemanticIndexRangePreset.OneMonth, Translator.SemanticIndex_CoveragePeriodOneMonth),
        new(SemanticIndexRangePreset.ThreeMonths, Translator.SemanticIndex_CoveragePeriodThreeMonths),
        new(SemanticIndexRangePreset.SixMonths, Translator.SemanticIndex_CoveragePeriodSixMonths),
        new(SemanticIndexRangePreset.OneYear, Translator.SemanticIndex_CoveragePeriodOneYear),
        new(SemanticIndexRangePreset.Everything, Translator.SemanticIndex_RangeEverything),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFolderName))]
    [NotifyPropertyChangedFor(nameof(IsEditorVisible))]
    [NotifyPropertyChangedFor(nameof(IsEmptyFolderNoticeVisible))]
    public partial IntelligenceFolderNode? SelectedFolder { get; set; }

    /// <summary>0 selects the newest N messages, 1 selects a date range.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLatestCountMode))]
    [NotifyPropertyChangedFor(nameof(IsDateRangeMode))]
    public partial int CoverageModeIndex { get; set; }

    /// <summary>Offset of the newest message the rule includes, in the folder's newest-first order.</summary>
    [ObservableProperty]
    public partial double RangeStartOffset { get; set; }

    /// <summary>Offset one past the oldest message the rule includes.</summary>
    [ObservableProperty]
    public partial double RangeEndOffset { get; set; }

    /// <summary>How many of the folder's newest messages the latest-count rule takes.</summary>
    [ObservableProperty]
    public partial double LatestCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistogramVisible))]
    [NotifyPropertyChangedFor(nameof(IsEditorVisible))]
    [NotifyPropertyChangedFor(nameof(IsEmptyFolderNoticeVisible))]
    public partial double RangeMaximum { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderSummary))]
    public partial int FolderIndexedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderSummary))]
    public partial int FolderSelectedNotIndexedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderSummary))]
    public partial int FolderOutsideCount { get; set; }

    [ObservableProperty]
    public partial string ReachSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial string TotalSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ScopeSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial int IncludedFolderCount { get; set; }

    public bool IsLatestCountMode => CoverageModeIndex == 0;
    public bool IsDateRangeMode => CoverageModeIndex == 1;

    public bool IsHistogramVisible => RangeMaximum >= MinimumHistogramMessages;

    public string SelectedFolderName => SelectedFolder?.DisplayName ?? string.Empty;

    /// <summary>
    /// The range editor is shown for every folder that has mail, included or not: setting a range
    /// is how a folder gets included, so hiding it behind inclusion would leave no way in.
    /// </summary>
    public bool IsEditorVisible => SelectedFolder is not null && RangeMaximum > 0;

    public bool IsEmptyFolderNoticeVisible => SelectedFolder is not null && RangeMaximum <= 0;

    /// <summary>Applying nothing is allowed: clearing every folder is a legitimate choice.</summary>
    public bool CanApply => _args is not null;

    public string FolderSummary => string.Format(
        Translator.SemanticIndex_CoverageFolderBandSummary,
        FolderIndexedCount,
        FolderSelectedNotIndexedCount,
        FolderOutsideCount);

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is not IntelligenceCoverageEditorArgs args)
            return;

        // Forward navigation only. Nothing sits on top of this page today, but a re-entry must not
        // discard edits the user has already made.
        if (mode == NavigationMode.Back && _args is not null)
            return;

        Load(args);
    }

    private void Load(IntelligenceCoverageEditorArgs args)
    {
        _args = args;
        _defaultRule = args.DefaultRule;
        _indexedByIndex = IntelligenceCoverageCalculator.BuildIndexBitmap(
            args.Inventory, args.IndexedRemoteMessageIds);

        var rulesByFolderId = args.Rules
            .Where(static rule => !string.IsNullOrWhiteSpace(rule.RemoteFolderId))
            .GroupBy(static rule => rule.RemoteFolderId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);

        var roots = IntelligenceFolderNode.Build(
            args.Folders,
            args.Inventory,
            args.IncludedRemoteFolderIds,
            rulesByFolderId,
            args.DefaultRule);

        RootFolders.Clear();
        foreach (var root in roots)
            RootFolders.Add(root);

        // Counts have to land on the nodes before a folder is picked, because inclusion — and so
        // the badge and the default selection — is read off them.
        RefreshTotals();
        SelectFolder(
            AllNodes().FirstOrDefault(static node => node.IsIncluded)
            ?? AllNodes().FirstOrDefault(static node => node.HasMessages)
            ?? AllNodes().FirstOrDefault());
    }

    private IEnumerable<IntelligenceFolderNode> AllNodes()
        => RootFolders.SelectMany(static root => root.SelfAndDescendants());

    [RelayCommand]
    private void SelectFolder(IntelligenceFolderNode? node)
    {
        SelectedFolder = node;
        LoadSelectedFolderRule();
    }

    /// <summary>Pushes the selected folder's stored rule into the editor controls.</summary>
    private void LoadSelectedFolderRule()
    {
        var folder = SelectedFolder;
        var inventory = _args?.Inventory;
        if (folder is null || inventory is null)
            return;

        _isLoadingFolder = true;
        try
        {
            var indices = inventory.GetFolderIndices(folder.RemoteFolderId);
            RangeMaximum = indices.Length;
            CoverageModeIndex = folder.Rule.Mode == SemanticIndexCoverageMode.LatestCount ? 0 : 1;
            LatestCount = Math.Clamp(folder.Rule.LatestMessageCount, 0, indices.Length);

            var (start, end) = ResolveDateOffsets(folder, indices.Length);
            RangeStartOffset = start;
            RangeEndOffset = end;
        }
        finally
        {
            _isLoadingFolder = false;
        }

        RefreshPreview();
    }

    /// <summary>
    /// Where a date rule's bounds land in the folder's newest-first order, so the range slider can
    /// show a date rule without a second representation of it.
    /// </summary>
    private (double Start, double End) ResolveDateOffsets(IntelligenceFolderNode folder, int folderMessageCount)
    {
        var inventory = _args!.Inventory;
        var cutoff = IntelligenceCoverageCalculator.ResolveCutoff(folder.Rule, DateTimeOffset.UtcNow);
        var newerCount = folder.Rule.ThroughUtcExclusive is null
            ? 0
            : folderMessageCount - IntelligenceCoverageCalculator.CountInDateRange(
                inventory, folder.RemoteFolderId, folder.Rule.ThroughUtcExclusive, null);
        var selected = IntelligenceCoverageCalculator.CountInDateRange(
            inventory, folder.RemoteFolderId, cutoff, folder.Rule.ThroughUtcExclusive);
        return (newerCount, Math.Min(folderMessageCount, newerCount + selected));
    }

    /// <summary>
    /// Switching mode carries the current selection across rather than starting over.
    /// </summary>
    /// <remarks>
    /// A latest-count rule still carries a date preset it never uses, so simply re-reading the rule
    /// in the other mode would silently swap "newest 500" for "last month". Translating through the
    /// offsets keeps the same messages selected, which is what changing how you express a selection
    /// should do.
    /// </remarks>
    partial void OnCoverageModeIndexChanged(int value)
    {
        if (_isLoadingFolder)
            return;

        _isLoadingFolder = true;
        try
        {
            if (IsDateRangeMode)
            {
                RangeStartOffset = 0;
                RangeEndOffset = LatestCount;
            }
            else
            {
                LatestCount = Math.Max(0, RangeEndOffset - RangeStartOffset);
            }
        }
        finally
        {
            _isLoadingFolder = false;
        }

        WriteRuleFromEditor();
    }

    partial void OnLatestCountChanged(double value)
    {
        if (_isLoadingFolder || !IsLatestCountMode)
            return;
        WriteRuleFromEditor();
    }

    partial void OnRangeStartOffsetChanged(double value)
    {
        if (_isLoadingFolder || !IsDateRangeMode)
            return;
        WriteRuleFromEditor();
    }

    partial void OnRangeEndOffsetChanged(double value)
    {
        if (_isLoadingFolder || !IsDateRangeMode)
            return;
        WriteRuleFromEditor();
    }

    /// <summary>Turns the editor controls back into the selected folder's rule.</summary>
    private void WriteRuleFromEditor()
    {
        var folder = SelectedFolder;
        var inventory = _args?.Inventory;
        if (folder is null || inventory is null)
            return;

        var indices = inventory.GetFolderIndices(folder.RemoteFolderId);
        if (IsLatestCountMode)
        {
            folder.Rule = SemanticIndexFolderCoverageRule.Latest(
                folder.RemoteFolderId,
                (int)Math.Clamp(Math.Round(LatestCount), 0, indices.Length));
        }
        else
        {
            var start = (int)Math.Clamp(Math.Round(RangeStartOffset), 0, indices.Length);
            var end = (int)Math.Clamp(Math.Round(RangeEndOffset), start, indices.Length);

            // Bounds are stored as dates, not offsets, because offsets shift as mail arrives. Ties
            // on the boundary date pull in the few messages that share it, which is what a date
            // range means anyway.
            folder.Rule = SemanticIndexFolderCoverageRule.DateRange(
                folder.RemoteFolderId,
                SemanticIndexRangePreset.Custom,
                end > start ? TickAt(inventory, indices, end - 1) : null,
                start > 0 ? TickAt(inventory, indices, start - 1) : null);
        }

        RefreshPreview();
        RefreshTotals();
    }

    private static DateTimeOffset TickAt(IntelligenceCoverageInventory inventory, int[] indices, int position)
        => new(inventory.ReceivedAtUtcTicks[indices[position]], TimeSpan.Zero);

    [RelayCommand]
    private void ApplyCountPreset(CoverageCountPresetOption? preset)
    {
        if (preset is null || SelectedFolder is null)
            return;

        CoverageModeIndex = 0;
        LatestCount = Math.Min(preset.Count, RangeMaximum);
    }

    [RelayCommand]
    private void ApplyDatePreset(CoverageDatePresetOption? preset)
    {
        var folder = SelectedFolder;
        var inventory = _args?.Inventory;
        if (preset is null || folder is null || inventory is null)
            return;

        _isLoadingFolder = true;
        try
        {
            CoverageModeIndex = 1;
            folder.Rule = SemanticIndexFolderCoverageRule.DateRange(
                folder.RemoteFolderId, preset.Preset, null, null);

            var indices = inventory.GetFolderIndices(folder.RemoteFolderId);
            var (start, end) = ResolveDateOffsets(folder, indices.Length);
            RangeStartOffset = start;
            RangeEndOffset = end;
        }
        finally
        {
            _isLoadingFolder = false;
        }

        RefreshPreview();
        RefreshTotals();
    }

    /// <summary>
    /// Copies the selected folder's rule onto every other included folder. Most mailboxes want one
    /// rule everywhere, so this is the shortcut that keeps per-folder rules from becoming a chore.
    /// </summary>
    /// <summary>
    /// Drops the selected folder's coverage to nothing, which is also what removes it from the
    /// selection — there is no separate "exclude" control to keep in step.
    /// </summary>
    [RelayCommand]
    private void ClearFolder()
    {
        if (SelectedFolder is null)
            return;

        CoverageModeIndex = 0;
        if (LatestCount == 0)
            WriteRuleFromEditor();
        else
            LatestCount = 0;
    }

    [RelayCommand]
    private void ApplyToAllFolders()
    {
        var folder = SelectedFolder;
        if (folder is null || _args is null)
            return;

        foreach (var node in AllNodes())
        {
            if (!node.IsIncluded || ReferenceEquals(node, folder))
                continue;
            node.Rule = folder.Rule with { RemoteFolderId = node.RemoteFolderId };
        }

        // Applying a rule everywhere is exactly the statement "this is what this mailbox does",
        // so it is also what a folder ticked later should start on.
        _defaultRule = folder.Rule with { RemoteFolderId = string.Empty };
        RefreshTotals();
    }

    [RelayCommand]
    private void Apply()
    {
        if (_args is null)
            return;

        var included = AllNodes().Where(static node => node.IsIncluded).ToArray();
        _handoff.Publish(new IntelligenceCoverageResult(
            _args.AccountId,
            (string[])[.. included.Select(static node => node.RemoteFolderId)],
            (SemanticIndexFolderCoverageRule[])[.. included.Select(static node => node.Rule)],
            _defaultRule ?? _args.DefaultRule));

        Messenger.Send(new BackBreadcrumNavigationRequested());
    }

    [RelayCommand]
    private void Cancel() => Messenger.Send(new BackBreadcrumNavigationRequested());

    /// <summary>
    /// Called by the page whenever the histogram host is resized, so the bars keep spanning exactly
    /// the width of the slider underneath them.
    /// </summary>
    public void SetHistogramWidth(double width)
    {
        if (width <= 0 || Math.Abs(width - _histogramWidth) < 1)
            return;

        _histogramWidth = width;
        ApplyBarWidth();
    }

    private void ApplyBarWidth()
    {
        if (Buckets.Count == 0)
            return;

        var barWidth = Math.Max(1, (_histogramWidth - (Buckets.Count - 1) * HistogramBarSpacing) / Buckets.Count);
        foreach (var bucket in Buckets)
            bucket.BarWidth = barWidth;
    }

    /// <summary>Recomputes the histogram and band counts for the selected folder alone.</summary>
    private void RefreshPreview()
    {
        var folder = SelectedFolder;
        var inventory = _args?.Inventory;
        if (folder is null || inventory is null || _indexedByIndex is null)
        {
            Buckets.Clear();
            return;
        }

        var folderIds = new HashSet<string>(StringComparer.Ordinal) { folder.RemoteFolderId };
        var selection = IntelligenceCoverageCalculator.Resolve(
            inventory,
            (SemanticIndexFolderCoverageRule[])[folder.Rule],
            DateTimeOffset.UtcNow);
        var histogram = IntelligenceCoverageCalculator.BuildBuckets(
            inventory, folderIds, selection.SelectedByIndex, _indexedByIndex, BucketCount);

        // Reuse the existing columns when the count matches: a rule change only reassigns segment
        // heights, and rebuilding the collection would restart the ItemsRepeater's animations.
        if (Buckets.Count == histogram.Buckets.Count)
        {
            for (var index = 0; index < Buckets.Count; index++)
                Buckets[index].Apply(histogram.Buckets[index], histogram.BusiestBucketCount);
        }
        else
        {
            Buckets.Clear();
            foreach (var bucket in histogram.Buckets)
                Buckets.Add(SemanticIndexRangeBucketViewModel.Create(bucket, histogram.BusiestBucketCount));
            ApplyBarWidth();
        }

        FolderIndexedCount = histogram.IndexedCount;
        FolderSelectedNotIndexedCount = histogram.SelectedNotIndexedCount;
        FolderOutsideCount = histogram.OutsideCount;
        ReachSummary = BuildReachSummary(folder, selection);
    }

    private string BuildReachSummary(IntelligenceFolderNode folder, IntelligenceCoverageSelection selection)
    {
        var resolved = selection.Folders.FirstOrDefault(
            result => string.Equals(result.RemoteFolderId, folder.RemoteFolderId, StringComparison.Ordinal));
        return resolved.SelectedMessageCount == 0 || resolved.ReachDate is null
            ? Translator.SemanticIndex_CoverageNothingSelected
            : string.Format(
                Translator.SemanticIndex_CoverageReachesBackTo,
                resolved.ReachDate.Value.ToString("d MMMM yyyy"));
    }

    /// <summary>Recomputes the footer, which spans every included folder rather than one.</summary>
    private void RefreshTotals()
    {
        var inventory = _args?.Inventory;
        if (inventory is null)
            return;

        // Every folder's rule is resolved, not just the included ones: inclusion is the result of
        // this calculation, so filtering by it first would be circular.
        var allNodes = AllNodes().ToArray();
        var selection = IntelligenceCoverageCalculator.Resolve(
            inventory,
            (SemanticIndexFolderCoverageRule[])[.. allNodes.Select(static node => node.Rule)],
            DateTimeOffset.UtcNow);

        var selectedByFolderId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var folder in selection.Folders)
            selectedByFolderId[folder.RemoteFolderId] = folder.SelectedMessageCount;

        foreach (var node in allNodes)
            node.SelectedMessageCount = selectedByFolderId.GetValueOrDefault(node.RemoteFolderId);

        var included = allNodes.Where(static node => node.IsIncluded).ToArray();
        IncludedFolderCount = included.Length;

        var toIndex = 0;
        if (_indexedByIndex is not null)
        {
            for (var index = 0; index < inventory.TotalMessageCount; index++)
            {
                if (selection.SelectedByIndex[index] && !_indexedByIndex[index])
                    toIndex++;
            }
        }

        TotalSummary = included.Length == 0
            ? Translator.SemanticIndex_CoverageNoFoldersSelected
            : string.Format(
                Translator.SemanticIndex_CoverageEditorTotals,
                included.Length,
                selection.DistinctSelectedCount,
                toIndex);

        ScopeSummary = string.Format(
            Translator.SemanticIndex_CoverageEditorScope,
            IntelligenceCoverageCalculator.CountDistinct(
                inventory,
                included.Select(static node => node.RemoteFolderId).ToHashSet(StringComparer.Ordinal)));
    }
}
