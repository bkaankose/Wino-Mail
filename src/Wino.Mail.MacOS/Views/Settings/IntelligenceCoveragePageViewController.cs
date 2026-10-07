using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// The coverage editor (Windows IntelligenceCoveragePage): totals with Cancel / Apply, the folder tree
/// on the left, and the selected folder's rule on the right — mode, presets, histogram with a slider
/// or range selector, and the three band counts.
/// </summary>
public sealed class IntelligenceCoveragePageViewController(IntelligenceCoveragePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<IntelligenceCoveragePageViewModel>(viewModel, dispatcher, logger)
{
    private readonly NSOutlineView _outline = new() { HeaderView = null, RowHeight = 32, IndentationPerLevel = 14, Style = NSTableViewStyle.Plain, BackgroundColor = NSColor.Clear, FocusRingType = NSFocusRingType.None };
    private FolderSource? _source;
    private readonly WrapView _presets = new(8);
    private readonly List<(NSButton Button, Func<bool> IsActive)> _presetButtons = [];

    private void Watch<TValue>(Func<IntelligenceCoveragePageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new AnyPropertyBinding<IntelligenceCoveragePageViewModel, TValue>(ViewModel, read, apply, Dispatcher, ReportError));

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Page.Spacing = 12;

        var totals = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        totals.LineBreakMode = NSLineBreakMode.TruncatingTail;
        totals.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        Watch(s => (s.TotalSummary, s.ScopeSummary), state =>
        {
            var text = new NSMutableAttributedString(new NSAttributedString(state.TotalSummary ?? string.Empty, new NSStringAttributes { Font = WinoStyle.BodyStrong, ForegroundColor = WinoStyle.PrimaryText }));
            text.Append(new NSAttributedString(" · " + state.ScopeSummary, new NSStringAttributes { Font = WinoStyle.Body, ForegroundColor = WinoStyle.SecondaryText }));
            totals.AttributedStringValue = text;
        });
        var cancel = Bind.Button(Translator.Buttons_Cancel, vm.CancelCommand);
        var apply = Bind.Button(Translator.Buttons_Apply, vm.ApplyCommand, primary: true);
        apply.KeyEquivalent = "\r";
        var header = WinoLayout.HStack(16, totals, WinoLayout.Spacer(), cancel, apply);
        Add(header);

        var left = FolderPanel();
        var right = EditorPanel();
        var body = WinoLayout.HStack(16, left, right);
        body.Alignment = NSLayoutAttribute.Top;
        left.HeightAnchor.ConstraintEqualTo(right.HeightAnchor).Active = true;
        Add(body);

        Bind.Collection(vm.RootFolders, ReloadTree);
        Watch(s => s.SelectedFolder, _ => SelectCurrentFolder());
        Bind.Collection(vm.Buckets, () => _histogram?.SetBuckets(vm.Buckets.ToList()));
    }

    // NavigationMode.Back with a fresh ViewModel must still load the editor arguments.
    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(NavigationMode.New, parameter!);
        return Task.CompletedTask;
    }

    private NSView FolderPanel()
    {
        var title = WinoStyle.Label(Translator.SemanticIndex_CoverageIncludedFolders, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        var column = new NSTableColumn("folder") { ResizingMask = NSTableColumnResizing.Autoresizing };
        _outline.AddColumn(column);
        _outline.OutlineTableColumn = column;
        _outline.ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly;
        _source = new FolderSource(ViewModel, node => ViewModel.SelectFolderCommand.Execute(node));
        _outline.DataSource = _source;
        _outline.Delegate = _source;
        Bindings.Own(new ActionDisposableLocal(() => { _outline.DataSource = null; _outline.Delegate = null; _source.Dispose(); }));
        var scroll = new NSScrollView { DocumentView = _outline, HasVerticalScroller = true, AutohidesScrollers = true, DrawsBackground = false, BorderType = NSBorderType.NoBorder, TranslatesAutoresizingMaskIntoConstraints = false };
        var stack = WinoLayout.VStack(8, title, scroll);
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.EdgeInsets = new NSEdgeInsets(12, 0, 8, 0);
        title.LeadingAnchor.ConstraintEqualTo(stack.LeadingAnchor, 16).Active = true;
        scroll.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        scroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        var surface = IntelligenceViews.Surface(stack, 0);
        WinoLayout.Size(surface, 300);
        // Totals change whenever a rule does, and Apply to all can change every folder's included dot and
        // accessibility name, so every loaded row is reconfigured in place.
        Watch(s => s.TotalSummary, _ => _source?.RefreshLoadedRows(_outline));
        return surface;
    }

    private CoverageHistogramView? _histogram;

    private NSView EditorPanel()
    {
        var vm = ViewModel;
        var name = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(18, NSFontWeight.Semibold), WinoStyle.PrimaryText);
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        name.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        Watch(s => s.SelectedFolderName, text => name.StringValue = text);
        var applyAll = Bind.Button(Translator.SemanticIndex_CoverageApplyToAll, vm.ApplyToAllFoldersCommand);
        var clear = Bind.Button(Translator.SemanticIndex_CoverageClearFolder, vm.ClearFolderCommand);
        clear.ToolTip = Translator.SemanticIndex_CoverageClearFolderTooltip;
        Watch(s => s.IsEditorVisible, on => { applyAll.Enabled = on; clear.Enabled = on; });
        var top = WinoLayout.HStack(16, name, WinoLayout.Spacer(), applyAll, clear);
        top.EdgeInsets = new NSEdgeInsets(0, 0, 14, 0);
        var divider = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };

        var notice = WinoStyle.Label(Translator.SemanticIndex_CoverageFolderEmptyNotice, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        Watch(s => s.IsEmptyFolderNoticeVisible, on => notice.Hidden = !on);

        // 1. Mode.
        var modeTitle = WinoStyle.Label(Translator.SemanticIndex_CoverageModeTitle, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        var latest = NSButton.CreateRadioButton(Translator.SemanticIndex_CoverageModeLatestCount, () => vm.CoverageModeIndex = 0);
        var range = NSButton.CreateRadioButton(Translator.SemanticIndex_CoverageModeDateRange, () => vm.CoverageModeIndex = 1);
        WinoLayout.Size(latest, 180);
        Watch(s => s.CoverageModeIndex, index => { latest.State = index == 0 ? NSCellStateValue.On : NSCellStateValue.Off; range.State = index == 1 ? NSCellStateValue.On : NSCellStateValue.Off; });
        var mode = Section(modeTitle, WinoLayout.HStack(28, latest, range));

        // 2. Presets for the active mode.
        var presetTitle = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
        Watch(s => s.IsLatestCountMode, count =>
        {
            presetTitle.StringValue = count ? Translator.SemanticIndex_CoverageNewestCount : Translator.SemanticIndex_CoveragePeriod;
            RebuildPresets(count);
        });
        Watch(s => (s.LatestCount, s.RangeMaximum, s.RangeStartOffset, s.RangeEndOffset, s.SelectedFolder?.Rule), _ => UpdatePresetTint());
        var presets = Section(presetTitle, _presets);

        // 3. Histogram, slider or range selector, edge labels.
        _histogram = new CoverageHistogramView();
        Watch(s => s.IsHistogramVisible, on => _histogram.Hidden = !on);
        var slider = new NSSlider { MinValue = 0, TranslatesAutoresizingMaskIntoConstraints = false, Continuous = true };
        WinoAccessibility.Label(slider, Translator.SemanticIndex_CoverageNewestCount);
        var rangeView = new CoverageRangeView();
        Watch(s => (s.RangeMaximum, s.LatestCount, s.RangeStartOffset, s.RangeEndOffset, s.IsLatestCountMode), state =>
        {
            slider.MaxValue = Math.Max(1, state.RangeMaximum);
            slider.DoubleValue = state.LatestCount;
            rangeView.Maximum = state.RangeMaximum;
            rangeView.Start = state.RangeStartOffset;
            rangeView.End = state.RangeEndOffset;
            slider.Hidden = !state.IsLatestCountMode;
            rangeView.Hidden = state.IsLatestCountMode;
        });
        Bind.OnActivated(slider, () => vm.LatestCount = Math.Round(slider.DoubleValue));
        rangeView.Changed += (_, _) => { vm.RangeStartOffset = rangeView.Start; vm.RangeEndOffset = rangeView.End; };
        var newest = WinoStyle.Label(Translator.SemanticIndex_CoverageNewestEdge, WinoStyle.Caption, WinoStyle.SecondaryText);
        newest.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        var reach = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        reach.Alignment = NSTextAlignment.Right;
        reach.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        reach.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        Watch(s => s.ReachSummary, text => { reach.StringValue = text; WinoAccessibility.Label(rangeView, text); });
        var edges = WinoLayout.HStack(12, newest, reach);
        var chart = WinoLayout.VStack(6, _histogram, slider, rangeView, edges);
        chart.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in new NSView[] { _histogram, slider, rangeView, edges }) view.WidthAnchor.ConstraintEqualTo(chart.WidthAnchor).Active = true;

        // 4. Bands.
        var bands = WinoLayout.HStack(8,
            Band(Translator.SemanticIndex_CoverageBandIndexed, CoverageColors.Indexed, s => s.FolderIndexedCount),
            Band(Translator.SemanticIndex_CoverageBandToIndex, CoverageColors.ToIndex, s => s.FolderSelectedNotIndexedCount),
            Band(Translator.SemanticIndex_CoverageBandOutside, CoverageColors.Outside, s => s.FolderOutsideCount));
        bands.Distribution = NSStackViewDistribution.FillEqually;

        var editor = WinoLayout.VStack(20, mode, presets, chart, bands);
        editor.Alignment = NSLayoutAttribute.Leading;
        editor.EdgeInsets = new NSEdgeInsets(18, 0, 20, 0);
        foreach (var view in new NSView[] { mode, presets, chart, bands }) view.WidthAnchor.ConstraintEqualTo(editor.WidthAnchor).Active = true;
        Watch(s => s.IsEditorVisible, on => editor.Hidden = !on);

        var stack = WinoLayout.VStack(0, top, divider, notice, editor);
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.SetCustomSpacing(18, divider);
        foreach (var view in new NSView[] { top, divider, notice, editor }) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        var wrapper = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(stack, wrapper, 16, 24, 0, 24);
        var surface = new WinoSurfaceView { Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke, CornerRadius = 6 };
        WinoLayout.Fill(wrapper, surface);
        surface.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        surface.HeightAnchor.ConstraintGreaterThanOrEqualTo(420).Active = true;
        return surface;
    }

    private static NSStackView Section(NSView title, NSView content)
    {
        var stack = WinoLayout.VStack(10, title, content);
        stack.Alignment = NSLayoutAttribute.Leading;
        return stack;
    }

    private NSView Band(string name, NSColor color, Func<IntelligenceCoveragePageViewModel, int> count)
    {
        var dot = new WinoSurfaceView { Fill = color, CornerRadius = 4 };
        WinoLayout.Size(dot, 8, 8);
        var label = WinoStyle.Label(name, WinoStyle.Caption, WinoStyle.SecondaryText);
        var value = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(16, NSFontWeight.Semibold), WinoStyle.PrimaryText);
        Watch(count, n => value.StringValue = n.ToString("N0"));
        var stack = WinoLayout.VStack(4, WinoLayout.HStack(6, dot, label), value);
        stack.Alignment = NSLayoutAttribute.Leading;
        var surface = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, CornerRadius = 4 };
        WinoLayout.Fill(stack, surface, 12);
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
        button.Activated += (_, _) => action();
        _presets.AddItem(button);
        _presetButtons.Add((button, isActive));
    }

    /// <summary>The preset matching the current rule is tinted with the accent.</summary>
    private void UpdatePresetTint()
    {
        foreach (var (button, isActive) in _presetButtons)
        {
            var active = isActive();
            button.BezelColor = active ? WinoStyle.Accent.ColorWithAlphaComponent(0.18f) : null;
            button.ContentTintColor = active ? WinoStyle.Accent : null;
            button.Font = active ? NSFont.SystemFontOfSize(13, NSFontWeight.Semibold) : NSFont.SystemFontOfSize(13);
        }
    }

    private void ReloadTree()
    {
        if (_source is null) return;
        _source.Reset();
        _outline.ReloadData();
        _outline.ExpandItem(null, true);
        SelectCurrentFolder();
    }

    private void SelectCurrentFolder()
    {
        if (_source is null || ViewModel.SelectedFolder is not { } selected) return;
        var row = _outline.RowForItem(_source.BoxFor(selected));
        if (row >= 0 && row != _outline.SelectedRow) _outline.SelectRow(row, false);
    }

    private sealed class ActionDisposableLocal(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    /// <summary>Outline data over <see cref="IntelligenceFolderNode"/>: glyph, name, included dot, message count.</summary>
    private sealed class FolderSource(IntelligenceCoveragePageViewModel viewModel, Action<IntelligenceFolderNode> selected) : NSOutlineViewDataSource, INSOutlineViewDelegate
    {
        private readonly Dictionary<IntelligenceFolderNode, Box> _boxes = new(ReferenceEqualityComparer.Instance);
        private bool _selecting;

        public sealed class Box(IntelligenceFolderNode node) : NSObject { public IntelligenceFolderNode Node { get; } = node; }

        public Box BoxFor(IntelligenceFolderNode node)
        {
            if (!_boxes.TryGetValue(node, out var box)) _boxes[node] = box = new Box(node);
            return box;
        }

        public void Reset() { foreach (var box in _boxes.Values) box.Dispose(); _boxes.Clear(); }

        private IList<IntelligenceFolderNode> Children(NSObject? item)
            => item is Box box ? box.Node.ChildNodes : viewModel.RootFolders;

        public override nint GetChildrenCount(NSOutlineView outlineView, NSObject? item) => Children(item).Count;
        public override NSObject GetChild(NSOutlineView outlineView, nint childIndex, NSObject? item) => BoxFor(Children(item)[(int)childIndex]);
        public override bool ItemExpandable(NSOutlineView outlineView, NSObject item) => item is Box box && box.Node.HasChildren;

        [Export("outlineView:rowViewForItem:")]
        public NSTableRowView RowViewForItem(NSOutlineView outlineView, NSObject item)
            => outlineView.MakeView(CoverageFolderRowView.ReuseIdentifier, this) as CoverageFolderRowView ?? new CoverageFolderRowView();

        [Export("outlineView:viewForTableColumn:item:")]
        public NSView GetView(NSOutlineView outlineView, NSTableColumn? tableColumn, NSObject item)
        {
            var cell = outlineView.MakeView(CoverageFolderCell.ReuseIdentifier, this) as CoverageFolderCell ?? new CoverageFolderCell();
            var node = ((Box)item).Node;
            cell.Configure(node, ReferenceEquals(node, viewModel.SelectedFolder));
            return cell;
        }

        /// <summary>Reconfigures the loaded cells in place; rows not yet loaded configure when they appear.</summary>
        public void RefreshLoadedRows(NSOutlineView outline)
        {
            for (nint row = 0; row < outline.RowCount; row++) Refresh(outline, row);
        }

        private void Refresh(NSOutlineView outline, nint row)
        {
            if (row >= 0 && outline.GetView(0, row, false) is CoverageFolderCell cell && outline.ItemAtRow(row) is Box box)
                cell.Configure(box.Node, ReferenceEquals(box.Node, viewModel.SelectedFolder));
        }

        [Export("outlineViewSelectionDidChange:")]
        public void SelectionDidChange(NSNotification notification)
        {
            if (_selecting || notification.Object is not NSOutlineView outline) return;
            if (outline.ItemAtRow(outline.SelectedRow) is not Box box) return;
            _selecting = true;
            try
            {
                // Only the previous and new selection change weight.
                var previous = viewModel.SelectedFolder;
                selected(box.Node);
                if (previous is not null) Refresh(outline, outline.RowForItem(BoxFor(previous)));
                Refresh(outline, outline.SelectedRow);
            }
            finally { _selecting = false; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Reset();
            base.Dispose(disposing);
        }
    }
}

/// <summary>Wrapping row of fixed-size items (Windows WrapPanel): lays items out left to right and wraps to the width.</summary>
internal sealed class WrapView : NSView
{
    private readonly double _spacing;
    private double _height;

    public WrapView(double spacing)
    {
        _spacing = spacing;
        TranslatesAutoresizingMaskIntoConstraints = false;
    }

    public override bool IsFlipped => true;

    public void AddItem(NSView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = true;
        AddSubview(view);
        NeedsLayout = true;
        InvalidateIntrinsicContentSize();
    }

    public void Clear()
    {
        foreach (var view in Subviews) view.RemoveFromSuperview();
        NeedsLayout = true;
    }

    public override CoreGraphics.CGSize IntrinsicContentSize => new(NSView.NoIntrinsicMetric, Math.Max(_height, 28));

    public override void Layout()
    {
        base.Layout();
        double x = 0, y = 0, line = 0;
        foreach (var view in Subviews)
        {
            var size = view.FittingSize;
            var width = Math.Max(size.Width, 104);
            if (x > 0 && x + width > Bounds.Width) { x = 0; y += line + _spacing; line = 0; }
            view.Frame = new CoreGraphics.CGRect(x, y, width, size.Height);
            x += width + _spacing;
            line = Math.Max(line, size.Height);
        }
        var height = y + line;
        if (Math.Abs(height - _height) > 0.5) { _height = height; InvalidateIntrinsicContentSize(); }
    }
}

