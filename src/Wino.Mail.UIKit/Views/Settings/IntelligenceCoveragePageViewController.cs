using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// The coverage editor (Windows IntelligenceCoveragePage): totals with Cancel / Apply, the folder tree, and the
/// selected folder's rule — mode, presets, histogram with a slider or range selector, and the three band counts.
/// The Mac puts the tree and the editor side by side; on iPhone width they stack, the tree first (every folder
/// expanded, as the Mac opens it).
/// </summary>
public sealed class IntelligenceCoveragePageViewController(IntelligenceCoveragePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<IntelligenceCoveragePageViewModel>(viewModel, dispatcher, logger)
{
    private readonly UIStackView _folderRows = WinoLayout.VStack(0);
    private readonly List<CoverageFolderCell> _cells = [];
    private readonly WrapView _presets = new(8, 88);
    private readonly List<(UIButton Button, string Title, Func<bool> IsActive)> _presetButtons = [];
    private CoverageHistogramView? _histogram;

    private void Watch<TValue>(Func<IntelligenceCoveragePageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new AnyPropertyBinding<IntelligenceCoveragePageViewModel, TValue>(ViewModel, read, apply, Dispatcher, ReportError));

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Page.Spacing = 12;

        var totals = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        Watch(s => (s.TotalSummary, s.ScopeSummary), state =>
        {
            var text = new NSMutableAttributedString(new NSAttributedString(state.TotalSummary ?? string.Empty,
                new UIStringAttributes { Font = WinoStyle.BodyStrong, ForegroundColor = WinoStyle.PrimaryText }));
            text.Append(new NSAttributedString(" · " + state.ScopeSummary, new UIStringAttributes { Font = WinoStyle.Body, ForegroundColor = WinoStyle.SecondaryText }));
            totals.AttributedText = text;
        });
        var cancel = Bind.Button(Translator.Buttons_Cancel, vm.CancelCommand);
        var apply = Bind.Button(Translator.Buttons_Apply, vm.ApplyCommand, primary: true);
        var buttons = WinoLayout.HStack(8, WinoLayout.Spacer(), cancel, apply);
        var header = WinoLayout.VStack(10, totals, buttons);
        header.Alignment = UIStackViewAlignment.Fill;
        Add(header);

        Add(FolderPanel());
        Add(EditorPanel());

        Bind.Collection(vm.RootFolders, ReloadTree);
        Watch(s => s.SelectedFolder, _ => RefreshRows());
        Bind.Collection(vm.Buckets, () => _histogram?.SetBuckets(vm.Buckets.ToList()));
    }

    // NavigationMode.Back with a fresh ViewModel must still load the editor arguments.
    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(NavigationMode.New, parameter);
        return Task.CompletedTask;
    }

    private UIView FolderPanel()
    {
        var title = WinoStyle.Label(Translator.SemanticIndex_CoverageIncludedFolders, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        title.AccessibilityTraits = UIAccessibilityTrait.Header;
        var titleHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(title, titleHost, 0, 16, 0, 16);
        _folderRows.Alignment = UIStackViewAlignment.Fill;
        var stack = WinoLayout.VStack(8, titleHost, _folderRows);
        stack.Alignment = UIStackViewAlignment.Fill;
        var surface = IntelligenceViews.Surface(stack, 0);
        stack.LayoutMarginsRelativeArrangement = true;
        stack.DirectionalLayoutMargins = new NSDirectionalEdgeInsets(12, 0, 8, 0);
        // Totals change whenever a rule does, and Apply to all can change every folder's included dot and
        // accessibility name, so every row is reconfigured in place.
        Watch(s => s.TotalSummary, _ => RefreshRows());
        return surface;
    }

    private UIView EditorPanel()
    {
        var vm = ViewModel;
        var name = WinoStyle.Label(string.Empty, WinoStyle.Scaled(20, UIFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        name.AccessibilityTraits = UIAccessibilityTrait.Header;
        Watch(s => s.SelectedFolderName, text => name.Text = text);
        var applyAll = Bind.Button(Translator.SemanticIndex_CoverageApplyToAll, vm.ApplyToAllFoldersCommand);
        var clear = Bind.Button(Translator.SemanticIndex_CoverageClearFolder, vm.ClearFolderCommand);
        WinoAccessibility.Help(clear, Translator.SemanticIndex_CoverageClearFolderTooltip);
        Watch(s => s.IsEditorVisible, on => { applyAll.Enabled = on; clear.Enabled = on; });
        var actions = WinoLayout.HStack(8, applyAll, clear, WinoLayout.Spacer());
        var top = WinoLayout.VStack(10, name, actions);
        top.Alignment = UIStackViewAlignment.Fill;
        var divider = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };

        var notice = WinoStyle.Label(Translator.SemanticIndex_CoverageFolderEmptyNotice, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        Watch(s => s.IsEmptyFolderNoticeVisible, on => notice.Hidden = !on);

        // 1. Mode (the Mac radio pair as a segmented control).
        var modeTitle = SectionTitle(Translator.SemanticIndex_CoverageModeTitle);
        var modes = Bind.Segmented(vm, [Translator.SemanticIndex_CoverageModeLatestCount, Translator.SemanticIndex_CoverageModeDateRange],
            nameof(vm.CoverageModeIndex), s => s.CoverageModeIndex, (s, v) => s.CoverageModeIndex = v);
        modes.HeightAnchor.ConstraintGreaterThanOrEqualTo(36).Active = true;
        var mode = Section(modeTitle, modes);

        // 2. Presets for the active mode.
        var presetTitle = SectionTitle(string.Empty);
        Watch(s => s.IsLatestCountMode, count =>
        {
            presetTitle.Text = count ? Translator.SemanticIndex_CoverageNewestCount : Translator.SemanticIndex_CoveragePeriod;
            RebuildPresets(count);
        });
        Watch(s => (s.LatestCount, s.RangeMaximum, s.RangeStartOffset, s.RangeEndOffset, s.SelectedFolder?.Rule), _ => UpdatePresetTint());
        var presets = Section(presetTitle, _presets);

        // 3. Histogram, slider or range selector, edge labels.
        var histogram = new CoverageHistogramView();
        _histogram = histogram;
        Watch(s => s.IsHistogramVisible, on => histogram.Hidden = !on);
        var slider = new UISlider { MinValue = 0, Continuous = true, TranslatesAutoresizingMaskIntoConstraints = false };
        slider.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoAccessibility.Label(slider, Translator.SemanticIndex_CoverageNewestCount);
        var rangeView = new CoverageRangeView();
        Watch(s => (s.RangeMaximum, s.LatestCount, s.RangeStartOffset, s.RangeEndOffset, s.IsLatestCountMode), state =>
        {
            slider.MaxValue = (float)Math.Max(1, state.RangeMaximum);
            slider.Value = (float)state.LatestCount;
            rangeView.Maximum = state.RangeMaximum;
            rangeView.Start = state.RangeStartOffset;
            rangeView.End = state.RangeEndOffset;
            slider.Hidden = !state.IsLatestCountMode;
            rangeView.Hidden = state.IsLatestCountMode;
        });
        Bind.OnActivated(slider, () => vm.LatestCount = Math.Round(slider.Value), UIControlEvent.ValueChanged);
        EventHandler rangeChanged = (_, _) => { vm.RangeStartOffset = rangeView.Start; vm.RangeEndOffset = rangeView.End; };
        rangeView.Changed += rangeChanged;
        Bindings.Own(new Infrastructure.ActionDisposable(() => rangeView.Changed -= rangeChanged));
        var newest = WinoStyle.Label(Translator.SemanticIndex_CoverageNewestEdge, WinoStyle.Caption, WinoStyle.SecondaryText);
        WinoLayout.Priorities(newest, UILayoutConstraintAxis.Horizontal, 750, 750);
        var reach = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        reach.TextAlignment = UITextAlignment.Right;
        WinoLayout.Priorities(reach, UILayoutConstraintAxis.Horizontal, 1, 250);
        Watch(s => s.ReachSummary, text => { reach.Text = text; rangeView.AccessibilityLabel = text; });
        var edges = WinoLayout.HStack(12, newest, reach);
        edges.Alignment = UIStackViewAlignment.FirstBaseline;
        var chart = WinoLayout.VStack(6, histogram, slider, rangeView, edges);
        chart.Alignment = UIStackViewAlignment.Fill;

        // 4. Bands.
        var bands = WinoLayout.HStack(8,
            Band(Translator.SemanticIndex_CoverageBandIndexed, CoverageColors.Indexed, s => s.FolderIndexedCount),
            Band(Translator.SemanticIndex_CoverageBandToIndex, CoverageColors.ToIndex, s => s.FolderSelectedNotIndexedCount),
            Band(Translator.SemanticIndex_CoverageBandOutside, CoverageColors.Outside, s => s.FolderOutsideCount));
        bands.Distribution = UIStackViewDistribution.FillEqually;
        bands.Alignment = UIStackViewAlignment.Fill;

        var editor = WinoLayout.VStack(20, mode, presets, chart, bands);
        editor.Alignment = UIStackViewAlignment.Fill;
        Watch(s => s.IsEditorVisible, on => editor.Hidden = !on);

        var stack = WinoLayout.VStack(14, top, divider, notice, editor);
        stack.Alignment = UIStackViewAlignment.Fill;
        stack.SetCustomSpacing(18, divider);
        return IntelligenceViews.Surface(stack, 16);
    }

    private static UILabel SectionTitle(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        label.AccessibilityTraits = UIAccessibilityTrait.Header;
        return label;
    }

    private static UIStackView Section(UIView title, UIView content)
    {
        var stack = WinoLayout.VStack(10, title, content);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    private UIView Band(string name, UIColor color, Func<IntelligenceCoveragePageViewModel, int> count)
    {
        var dot = new WinoSurfaceView { Fill = color, CornerRadius = 4 };
        WinoLayout.Size(dot, 8, 8);
        var label = WinoStyle.Label(name, WinoStyle.Caption, WinoStyle.SecondaryText, 2);
        var value = WinoStyle.Label(string.Empty, WinoStyle.Scaled(17, UIFontWeight.Semibold), WinoStyle.PrimaryText);
        Watch(count, n =>
        {
            value.Text = n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            value.AccessibilityLabel = $"{name}, {value.Text}";
        });
        var caption = WinoLayout.HStack(6, dot, label);
        caption.Alignment = UIStackViewAlignment.Center;
        var stack = WinoLayout.VStack(4, caption, value);
        var surface = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, CornerRadius = 4, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(stack, surface, 10);
        label.IsAccessibilityElement = false;
        return surface;
    }

    private void RebuildPresets(bool count)
    {
        _presets.Clear();
        _presetButtons.Clear();
        var vm = ViewModel;
        if (count)
        {
            foreach (var preset in vm.CountPresets)
                AddPreset(preset.DisplayName, () => vm.ApplyCountPresetCommand.Execute(preset),
                    () => vm.IsLatestCountMode && Math.Abs(vm.LatestCount - Math.Min(preset.Count, vm.RangeMaximum)) < 0.5 && vm.LatestCount > 0);
        }
        else
        {
            foreach (var preset in vm.DatePresets)
                AddPreset(preset.DisplayName, () => vm.ApplyDatePresetCommand.Execute(preset),
                    () => vm.IsDateRangeMode && vm.SelectedFolder?.Rule is { Mode: SemanticIndexCoverageMode.DateRange } rule && rule.DatePreset == preset.Preset);
        }
        UpdatePresetTint();
    }

    private void AddPreset(string title, Action action, Func<bool> isActive)
    {
        var button = SettingsBinder.CreateButton(title);
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        EventHandler handler = (_, _) =>
        {
            try { action(); }
            catch (Exception exception) { ReportError(exception); }
        };
        button.PrimaryActionTriggered += handler;
        Bindings.Own(new Infrastructure.ActionDisposable(() => button.PrimaryActionTriggered -= handler));
        _presets.AddItem(button);
        _presetButtons.Add((button, title, isActive));
    }

    /// <summary>The preset matching the current rule is tinted with the accent.</summary>
    private void UpdatePresetTint()
    {
        foreach (var (button, title, isActive) in _presetButtons)
        {
            var active = isActive();
            var configuration = active ? UIButtonConfiguration.TintedButtonConfiguration : UIButtonConfiguration.GrayButtonConfiguration;
            configuration.Title = title;
            button.Configuration = configuration;
            button.AccessibilityTraits = active ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
        }
    }

    /// <summary>Rebuilds the flattened tree (every folder expanded) from the ViewModel's roots.</summary>
    private void ReloadTree()
    {
        IntelligenceViews.Clear(_folderRows);
        _cells.Clear();
        void AddNodes(IEnumerable<IntelligenceFolderNode> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                var cell = new CoverageFolderCell(node, depth);
                EventHandler picked = (_, _) => Pick(node);
                cell.Picked += picked;
                Bindings.Own(new Infrastructure.ActionDisposable(() => cell.Picked -= picked));
                _cells.Add(cell);
                _folderRows.AddArrangedSubview(cell);
                if (node.HasChildren) AddNodes(node.ChildNodes, depth + 1);
            }
        }
        AddNodes(ViewModel.RootFolders.ToList(), 0);
        RefreshRows();
    }

    private void Pick(IntelligenceFolderNode node)
    {
        try
        {
            if (ViewModel.SelectFolderCommand.CanExecute(node)) ViewModel.SelectFolderCommand.Execute(node);
        }
        catch (Exception exception) { ReportError(exception); }
        RefreshRows();
    }

    private void RefreshRows()
    {
        var selected = ViewModel.SelectedFolder;
        foreach (var cell in _cells) cell.Configure(ReferenceEquals(cell.Node, selected));
    }
}
